using Windows.Security.Credentials;

namespace SqlPlanViz.Capture;

/// <summary>
/// Optional, opt-in storage for a SQL-auth password, backed by Windows Credential Manager
/// via <see cref="PasswordVault"/> (Phase 5). Never plaintext to disk.
///
/// Mechanism decision (Phase 5 task 2 open question): <see cref="PasswordVault"/> is used —
/// verified on this target that a write / read-back / delete round-trip succeeds for this
/// UNPACKAGED app (<c>WindowsPackageType=None</c>), so the DPAPI (<c>ProtectedData</c>)
/// fallback was not needed. If a future host build fails here, fall back to DPAPI
/// CurrentUser-scoped over a JSON file in the same <c>%LOCALAPPDATA%\SqlPlanViz</c> folder
/// the recent-connections store uses.
/// </summary>
public sealed class PasswordVaultStore
{
    // All credentials this app stores share one resource; the account is Server + UserId.
    private const string Resource = "SqlPlanViz";

    private static string AccountFor(string server, string userId) =>
        $"{server?.Trim() ?? string.Empty}|{userId?.Trim() ?? string.Empty}";

    /// <summary>True when a stored password exists for this server + login.</summary>
    public bool Has(string server, string userId) => Retrieve(server, userId) is not null;

    /// <summary>The stored password for this server + login, or null when none is saved.</summary>
    public string? Retrieve(string server, string userId)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        try
        {
            var credential = new PasswordVault().Retrieve(Resource, AccountFor(server, userId));
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            // Not found (COMException 0x80070490) or vault unavailable — treat as "no password".
            return null;
        }
    }

    /// <summary>Stores (or overwrites) the password for this server + login. No-ops on blanks.</summary>
    public void Save(string server, string userId, string password)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            var vault = new PasswordVault();
            RemoveFrom(vault, server, userId);
            vault.Add(new PasswordCredential(Resource, AccountFor(server, userId), password));
        }
        catch (Exception)
        {
            // A vault failure must never break connecting.
        }
    }

    /// <summary>Removes any stored password for this server + login. Safe when none exists.</summary>
    public void Remove(string server, string userId)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        try
        {
            RemoveFrom(new PasswordVault(), server, userId);
        }
        catch (Exception)
        {
        }
    }

    private static void RemoveFrom(PasswordVault vault, string server, string userId)
    {
        try
        {
            var existing = vault.Retrieve(Resource, AccountFor(server, userId));
            vault.Remove(existing);
        }
        catch (Exception)
        {
            // Nothing stored for this key — fine.
        }
    }
}
