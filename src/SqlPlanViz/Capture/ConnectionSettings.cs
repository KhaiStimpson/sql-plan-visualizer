namespace SqlPlanViz.Capture;

public enum AuthMode
{
    Windows,
    SqlLogin,
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

    /// <summary>Tears the connection back down to its defaults (the Disconnect action).</summary>
    public void Reset()
    {
        Server = string.Empty;
        Database = string.Empty;
        UserId = string.Empty;
        Password = string.Empty;
        Auth = AuthMode.Windows;
    }

    public string Describe()
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            return "Not connected";
        }

        var database = string.IsNullOrWhiteSpace(Database) ? string.Empty : " · " + Database;
        return $"{Server}{database} · {AuthLabel(Auth)}";
    }

    private static string AuthLabel(AuthMode auth) => auth switch
    {
        AuthMode.SqlLogin => "SQL login",
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
