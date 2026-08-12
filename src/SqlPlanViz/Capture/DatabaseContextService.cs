using Microsoft.Data.SqlClient;

namespace SqlPlanViz.Capture;

public sealed record DatabaseObjectContext
{
    public string Schema { get; init; } = string.Empty;
    public string Table { get; init; } = string.Empty;
    public long RowCount { get; init; }
    public double ReservedMb { get; init; }
    public double UsedMb { get; init; }
    public IReadOnlyList<DatabaseIndexInfo> Indexes { get; init; } = [];
    public IReadOnlyList<DatabaseStatisticInfo> Statistics { get; init; } = [];

    public string DisplayName => $"{Schema}.{Table}";

    public string RowCountText => RowCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public string SizeText => $"{UsedMb:N1} MB used · {ReservedMb:N1} MB reserved";
}

public sealed record DatabaseIndexInfo
{
    public string Name { get; init; } = string.Empty;
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    public string ColumnSummary => $"({string.Join(", ", KeyColumns)})"
        + (IncludedColumns.Count == 0 ? string.Empty : $" INCLUDE ({string.Join(", ", IncludedColumns)})");

    public string Kind => IsPrimaryKey ? "Primary key" : IsUnique ? "Unique" : "Index";
}

public sealed record DatabaseStatisticInfo
{
    public string Name { get; init; } = string.Empty;
    public DateTime? LastUpdated { get; init; }
    public long? Rows { get; init; }
    public long? ModificationCount { get; init; }

    public string Detail => $"Updated {(LastUpdated is null ? "unknown" : LastUpdated.Value.ToString("g"))}"
        + $" · {(Rows is null ? "?" : Rows.Value.ToString("N0"))} rows"
        + $" · {(ModificationCount is null ? "?" : ModificationCount.Value.ToString("N0"))} modified";
}

public sealed record QueryStorePlanEntry
{
    public long QueryId { get; init; }
    public long PlanId { get; init; }
    public DateTime? LastExecutionTime { get; init; }
    public long ExecutionCount { get; init; }
    public double AverageDurationMs { get; init; }
    public string QueryText { get; init; } = string.Empty;
    public string PlanXml { get; init; } = string.Empty;

    public string Title => QueryText.Replace('\r', ' ').Replace('\n', ' ').Trim() is { Length: > 90 } text ? text[..90] + "…" : QueryText.Replace('\r', ' ').Replace('\n', ' ').Trim();
    public string Detail => $"Plan {PlanId} · {ExecutionCount:N0} executions · {AverageDurationMs:N1} ms avg · {(LastExecutionTime is null ? "never" : LastExecutionTime.Value.ToString("g"))}";
}

/// <summary>Read-only catalog and DMV lookups used to judge plan suggestions against the live database.</summary>
public sealed class DatabaseContextService
{
    public async Task<IReadOnlyList<QueryStorePlanEntry>> GetQueryStoreHistoryAsync(
        ConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(PlanCaptureService.BuildConnectionString(settings));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = settings.CommandTimeoutSeconds;
            command.CommandText = QueryStoreSql;
            var entries = new List<QueryStorePlanEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new QueryStorePlanEntry
                {
                    QueryId = reader.GetInt64(0),
                    PlanId = reader.GetInt64(1),
                    LastExecutionTime = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    ExecutionCount = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3)),
                    AverageDurationMs = reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4)),
                    QueryText = reader.GetString(5),
                    PlanXml = reader.GetString(6),
                });
            }

            return entries;
        }
        catch (SqlException ex)
        {
            throw new PlanCaptureException($"Could not read Query Store history: {ex.Message}", ex);
        }
    }

    public async Task<DatabaseObjectContext?> GetObjectContextAsync(
        ConnectionSettings settings,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(PlanCaptureService.BuildConnectionString(settings));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = settings.CommandTimeoutSeconds;
            command.CommandText = ContextSql;
            command.Parameters.AddWithValue("@objectName", NormalizeObjectName(objectName));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var rowCount = reader.GetInt64(2);
            var reservedMb = reader.GetDouble(3);
            var usedMb = reader.GetDouble(4);

            var indexRows = new Dictionary<int, MutableIndex>();
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var indexId = reader.GetInt32(0);
                if (!indexRows.TryGetValue(indexId, out var index))
                {
                    index = new MutableIndex(reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3));
                    indexRows[indexId] = index;
                }

                if (!reader.IsDBNull(4))
                {
                    var column = reader.GetString(4);
                    if (reader.GetBoolean(5)) index.Included.Add(column); else index.Keys.Add(column);
                }
            }

            var statistics = new List<DatabaseStatisticInfo>();
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                statistics.Add(new DatabaseStatisticInfo
                {
                    Name = reader.GetString(0),
                    LastUpdated = reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    Rows = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    ModificationCount = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                });
            }

            return new DatabaseObjectContext
            {
                Schema = schema,
                Table = table,
                RowCount = rowCount,
                ReservedMb = reservedMb,
                UsedMb = usedMb,
                Indexes = indexRows.Values.Select(index => index.ToInfo()).ToList(),
                Statistics = statistics,
            };
        }
        catch (SqlException ex)
        {
            throw new PlanCaptureException($"Could not read database context: {ex.Message}", ex);
        }
    }

    private static string NormalizeObjectName(string value) => value
        .Split('.')
        .TakeLast(2)
        .Select(part => part.Trim('[', ']'))
        .Aggregate((left, right) => $"[{left}].[{right}]");

    private sealed class MutableIndex(string name, bool unique, bool primaryKey)
    {
        public List<string> Keys { get; } = [];
        public List<string> Included { get; } = [];

        public DatabaseIndexInfo ToInfo() => new()
        {
            Name = name,
            IsUnique = unique,
            IsPrimaryKey = primaryKey,
            KeyColumns = Keys,
            IncludedColumns = Included,
        };
    }

    private const string ContextSql = """
        DECLARE @objectId int = OBJECT_ID(@objectName);

        SELECT s.name, t.name,
               CONVERT(bigint, COALESCE(SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.row_count ELSE 0 END), 0)),
               CONVERT(float, COALESCE(SUM(ps.reserved_page_count), 0)) * 8.0 / 1024.0,
               CONVERT(float, COALESCE(SUM(ps.used_page_count), 0)) * 8.0 / 1024.0
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id
        WHERE t.object_id = @objectId
        GROUP BY s.name, t.name;

        SELECT i.index_id, i.name, i.is_unique, i.is_primary_key, c.name, ic.is_included_column
        FROM sys.indexes i
        LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = @objectId AND i.is_hypothetical = 0
        ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;

        SELECT st.name, sp.last_updated, CONVERT(bigint, sp.rows), CONVERT(bigint, sp.modification_counter)
        FROM sys.stats st
        OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) sp
        WHERE st.object_id = @objectId
        ORDER BY st.name;
        """;

    private const string QueryStoreSql = """
        SELECT TOP (100)
               q.query_id,
               p.plan_id,
               MAX(rs.last_execution_time),
               COALESCE(SUM(CONVERT(bigint, rs.count_executions)), 0),
               COALESCE(SUM(CONVERT(float, rs.avg_duration) * rs.count_executions)
                        / NULLIF(SUM(CONVERT(float, rs.count_executions)), 0) / 1000.0, 0),
               qt.query_sql_text,
               CONVERT(nvarchar(max), p.query_plan)
        FROM sys.query_store_query_text qt
        JOIN sys.query_store_query q ON q.query_text_id = qt.query_text_id
        JOIN sys.query_store_plan p ON p.query_id = q.query_id
        LEFT JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
        WHERE p.query_plan IS NOT NULL
        GROUP BY q.query_id, p.plan_id, qt.query_sql_text, p.query_plan
        ORDER BY MAX(rs.last_execution_time) DESC;
        """;
}
