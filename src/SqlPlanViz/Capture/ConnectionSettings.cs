using Microsoft.Data.SqlClient;

namespace SqlPlanViz.Capture;

public enum AuthMode
{
    Windows,
    SqlLogin,

    /// <summary>
    /// Microsoft Entra MFA — Active Directory Interactive (browser/popup) auth via
    /// <c>Microsoft.Data.SqlClient</c>'s built-in <c>SqlAuthenticationMethod.ActiveDirectoryInteractive</c>.
    /// Other Entra modes (Password, Integrated, device code, managed identity, service principal)
    /// are deliberately deferred to the connection-string path rather than modelled in the form.
    /// </summary>
    EntraMfa,
}

/// <summary>
/// TDD §10: Windows Auth is the default, SQL auth the fallback, and nothing is persisted —
/// these live for the lifetime of the dialog and the connection it opens.
/// </summary>
public sealed class ConnectionSettings
{
    public string Server { get; set; } = string.Empty;

    public AuthMode Auth { get; set; } = AuthMode.Windows;

    public string UserId { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// Microsoft.Data.SqlClient defaults Encrypt=true, which fails against a dev instance
    /// using a self-signed certificate — hence the explicit escape hatch.
    /// </summary>
    public bool Encrypt { get; set; } = true;

    public bool TrustServerCertificate { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// A full ADO.NET connection string pasted verbatim. When <see cref="UseConnectionString"/>
    /// is set this is authoritative and the individual form fields are ignored.
    /// </summary>
    public string RawConnectionString { get; set; } = string.Empty;

    /// <summary>True when the pasted <see cref="RawConnectionString"/> is the active input.</summary>
    public bool UseConnectionString { get; set; }

    /// <summary>Tears the connection back down to its defaults (the Disconnect action).</summary>
    public void Reset()
    {
        Server = string.Empty;
        Database = string.Empty;
        UserId = string.Empty;
        Password = string.Empty;
        Auth = AuthMode.Windows;
        RawConnectionString = string.Empty;
        UseConnectionString = false;
    }

    /// <summary>
    /// Loads a saved <see cref="ConnectionProfile"/> into this instance (the one-click reconnect
    /// path). <paramref name="vaultedPassword"/> is the value pulled from
    /// <see cref="PasswordVaultStore"/> when the profile's <c>PasswordIsVaulted</c> flag is set.
    /// </summary>
    public void ApplyProfile(ConnectionProfile profile, string? vaultedPassword = null)
    {
        Reset();

        if (profile.IsRawConnectionString)
        {
            UseConnectionString = true;
            RawConnectionString = profile.RawConnectionString;
            return;
        }

        Server = profile.Server;
        Database = profile.Database;
        Auth = profile.Auth;
        UserId = profile.UserId;
        Encrypt = profile.Encrypt;
        TrustServerCertificate = profile.TrustServerCertificate;
        Password = vaultedPassword ?? string.Empty;
    }

    /// <summary>
    /// The server this settings object actually points at, whichever entry mode is active.
    /// In connection-string mode <see cref="Server"/> is deliberately left blank — the pasted
    /// string is authoritative — so every "are we connected / which server is it" test has to
    /// go through here rather than reading <see cref="Server"/> directly, or connection-string
    /// mode reads as disconnected everywhere.
    /// </summary>
    public string EffectiveServer =>
        UseConnectionString ? RawConnectionStringPart(b => b.DataSource) : Server;

    /// <summary>The database this settings object points at, in either entry mode.</summary>
    public string EffectiveDatabase =>
        UseConnectionString ? RawConnectionStringPart(b => b.InitialCatalog) : Database;

    /// <summary>
    /// True when there is a connection target at all. In connection-string mode a pasted
    /// string counts even when it names no server (a "Data Source=" alias, say) — the string
    /// is the target, and only an attempt can prove it wrong.
    /// </summary>
    public bool HasTarget => UseConnectionString
        ? !string.IsNullOrWhiteSpace(RawConnectionString)
        : !string.IsNullOrWhiteSpace(Server);

    private string RawConnectionStringPart(Func<SqlConnectionStringBuilder, string> part)
    {
        if (string.IsNullOrWhiteSpace(RawConnectionString))
        {
            return string.Empty;
        }

        try
        {
            return part(new SqlConnectionStringBuilder(RawConnectionString)) ?? string.Empty;
        }
        catch
        {
            // A malformed string is reported where it is used to connect; here it just means
            // we cannot name the server yet.
            return string.Empty;
        }
    }

    public string Describe()
    {
        if (UseConnectionString && !string.IsNullOrWhiteSpace(RawConnectionString))
        {
            return DescribeRawConnectionString();
        }

        if (string.IsNullOrWhiteSpace(Server))
        {
            return "Not connected";
        }

        var database = string.IsNullOrWhiteSpace(Database) ? string.Empty : " · " + Database;
        return $"{Server}{database} · {AuthLabel(Auth)}";
    }

    private string DescribeRawConnectionString()
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(RawConnectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource))
            {
                return "Connection string";
            }

            var database = string.IsNullOrWhiteSpace(builder.InitialCatalog)
                ? string.Empty
                : " · " + builder.InitialCatalog;
            return $"{builder.DataSource}{database} · connection string";
        }
        catch
        {
            return "Connection string";
        }
    }

    private static string AuthLabel(AuthMode auth) => auth switch
    {
        AuthMode.SqlLogin => "SQL login",
        AuthMode.EntraMfa => "Microsoft Entra MFA",
        _ => "Windows",
    };
}

public enum CaptureMode
{
    /// <summary>SET STATISTICS XML ON — runs the query and returns runtime row counts.</summary>
    Actual,

    /// <summary>SET SHOWPLAN_XML ON — compiles but does not run the query.</summary>
    EstimatedOnly,
}
