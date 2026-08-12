using System.Text;
using Microsoft.Data.SqlClient;
using SqlPlanViz.Model;
using SqlPlanViz.Parsing;

namespace SqlPlanViz.Capture;

public sealed class PlanCaptureException : Exception
{
    public PlanCaptureException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Live capture (TDD §6B): run the user's query with Showplan turned on and read the plan
/// back off the connection, so there's no round trip through SSMS.
/// </summary>
public sealed class PlanCaptureService
{
    /// <summary>The column name SQL Server gives the Showplan result set, unchanged since 2005.</summary>
    private const string ShowplanColumn = "Microsoft SQL Server 2005 XML Showplan";

    public async Task<ExecutionPlan> CaptureAsync(
        ConnectionSettings settings,
        string query,
        CaptureMode mode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new PlanCaptureException("Enter a query to capture a plan for.");
        }

        var documents = new List<string>();

        try
        {
            await using var connection = new SqlConnection(BuildConnectionString(settings));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // SET SHOWPLAN_XML must be alone in its batch, and issuing the SET separately
            // also keeps a query that legitimately starts with ";WITH" from being mangled.
            var setOn = mode == CaptureMode.Actual
                ? "SET STATISTICS XML ON;"
                : "SET SHOWPLAN_XML ON;";
            var setOff = mode == CaptureMode.Actual
                ? "SET STATISTICS XML OFF;"
                : "SET SHOWPLAN_XML OFF;";

            await ExecuteNonQueryAsync(connection, setOn, settings, cancellationToken).ConfigureAwait(false);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = query;
                command.CommandTimeout = settings.CommandTimeoutSeconds;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                do
                {
                    if (!IsShowplanResultSet(reader))
                    {
                        // The query's own rows. Drain them so the next result set (the plan)
                        // becomes reachable.
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                        }

                        continue;
                    }

                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!reader.IsDBNull(0))
                        {
                            documents.Add(reader.GetString(0));
                        }
                    }
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                // Best effort: if the query failed the connection may be unusable, and the
                // original exception is the one worth surfacing.
                try
                {
                    await ExecuteNonQueryAsync(connection, setOff, settings, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (SqlException)
                {
                }
            }
        }
        catch (SqlException ex)
        {
            throw new PlanCaptureException(DescribeSqlError(ex), ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new PlanCaptureException($"Could not connect: {ex.Message}", ex);
        }

        if (documents.Count == 0)
        {
            throw new PlanCaptureException(
                mode == CaptureMode.Actual
                    ? "The query ran but returned no execution plan. Statements like GO batches, "
                      + "or a query that SQL Server did not compile a plan for, produce no Showplan output."
                    : "SQL Server returned no estimated plan for that query.");
        }

        var statements = new List<PlanStatement>();
        foreach (var doc in documents)
        {
            statements.AddRange(ShowplanParser.Parse(doc).Statements);
        }

        var label = mode == CaptureMode.Actual ? "Actual plan" : "Estimated plan";
        return new ExecutionPlan
        {
            Statements = statements,
            SourceName = $"{label} · {settings.Describe()}",
        };
    }

    /// <summary>Opens and closes a connection to verify the settings work.</summary>
    public async Task<string> TestConnectionAsync(
        ConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(BuildConnectionString(settings));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT @@VERSION;";
            command.CommandTimeout = 15;
            var version = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var firstLine = version?.Split('\n').FirstOrDefault()?.Trim() ?? "Connected";
            return firstLine;
        }
        catch (SqlException ex)
        {
            throw new PlanCaptureException(DescribeSqlError(ex), ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new PlanCaptureException($"Could not connect: {ex.Message}", ex);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqlConnection connection,
        string sql,
        ConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsShowplanResultSet(SqlDataReader reader) =>
        reader.FieldCount == 1
        && string.Equals(reader.GetName(0), ShowplanColumn, StringComparison.OrdinalIgnoreCase);

    internal static string BuildConnectionString(ConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Server))
        {
            throw new PlanCaptureException("Enter a server name.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server.Trim(),
            ApplicationName = "SQL Plan Visualizer",
            ConnectTimeout = 15,
            Encrypt = settings.Encrypt,
            TrustServerCertificate = settings.TrustServerCertificate,
            MultipleActiveResultSets = false,
        };

        if (!string.IsNullOrWhiteSpace(settings.Database))
        {
            builder.InitialCatalog = settings.Database.Trim();
        }

        if (settings.Auth == AuthMode.Windows)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = settings.UserId;
            builder.Password = settings.Password;
        }

        return builder.ConnectionString;
    }

    private static string DescribeSqlError(SqlException ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex.Message.Trim());

        // The first error is usually the useful one; the rest are transport noise.
        if (ex.Number is 18456)
        {
            sb.Append(" — check the login and password.");
        }
        else if (ex.Number is 4060)
        {
            sb.Append(" — check the database name.");
        }
        else if (ex.Number is -2)
        {
            sb.Append(" — the query exceeded the command timeout.");
        }
        else if (ex.Number is 53 or -1)
        {
            sb.Append(" — check the server name and that the instance is reachable.");
        }

        return sb.ToString();
    }
}
