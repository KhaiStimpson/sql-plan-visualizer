using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlPlanViz.Capture;

/// <summary>
/// One user-named connection profile — the full form config (or a raw connection string),
/// minus the secret. <see cref="PasswordIsVaulted"/> records that a SQL-auth password for
/// <see cref="Server"/> + <see cref="UserId"/> lives in Windows Credential Manager
/// (<see cref="PasswordVaultStore"/>); <see cref="IsRawConnectionString"/> records that
/// <see cref="RawConnectionString"/> is authoritative and the other fields are ignored.
/// </summary>
public sealed class ConnectionProfile
{
    public ConnectionProfile()
    {
    }

    public ConnectionProfile(
        string name,
        string server,
        string database,
        AuthMode auth,
        string userId,
        bool encrypt,
        bool trustServerCertificate,
        bool passwordIsVaulted,
        bool isRawConnectionString,
        string rawConnectionString)
    {
        Name = name;
        Server = server;
        Database = database;
        Auth = auth;
        UserId = userId;
        Encrypt = encrypt;
        TrustServerCertificate = trustServerCertificate;
        PasswordIsVaulted = passwordIsVaulted;
        IsRawConnectionString = isRawConnectionString;
        RawConnectionString = rawConnectionString;
    }

    public string Name { get; set; } = string.Empty;

    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public AuthMode Auth { get; set; } = AuthMode.Windows;

    public string UserId { get; set; } = string.Empty;

    public bool Encrypt { get; set; } = true;

    public bool TrustServerCertificate { get; set; } = true;

    public bool PasswordIsVaulted { get; set; }

    public bool IsRawConnectionString { get; set; }

    public string RawConnectionString { get; set; } = string.Empty;

    public ConnectionProfile WithName(string name) => new(
        name, Server, Database, Auth, UserId, Encrypt, TrustServerCertificate,
        PasswordIsVaulted, IsRawConnectionString, RawConnectionString);
}

/// <summary>
/// Named connection profiles — persistence separate from <see cref="RecentConnectionsStore"/>
/// (recent targets are an automatic MRU list; profiles are explicit, user-named, and hold the
/// full config). Plain JSON under the per-user local app-data folder, same rationale as the
/// recent-connections store: the app is <b>unpackaged</b> so <c>ApplicationData.Current</c>
/// throws — <see cref="Environment.SpecialFolder.LocalApplicationData"/> + an app subfolder is
/// used instead. Never stores a password (the vault does).
/// </summary>
public sealed class ConnectionProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public ConnectionProfileStore()
        : this(DefaultFilePath())
    {
    }

    /// <summary>Test/override seam — point the store at an explicit file.</summary>
    public ConnectionProfileStore(string filePath) => _filePath = filePath;

    public string FilePath => _filePath;

    public static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlPlanViz",
        "connection-profiles.json");

    /// <summary>
    /// All saved profiles, ordered by name (case-insensitive). Never throws — a missing or
    /// corrupt file yields an empty list.
    /// </summary>
    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<ConnectionProfile>();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(_filePath), Options);
            return loaded is null
                ? Array.Empty<ConnectionProfile>()
                : loaded.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ConnectionProfile>();
        }
    }

    /// <summary>The profile with this name (case-insensitive), or null.</summary>
    public ConnectionProfile? Get(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Load().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds <paramref name="profile"/>, replacing any existing profile of the same name
    /// (case-insensitive). No-ops on a blank name.
    /// </summary>
    public void Save(ConnectionProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
        {
            return;
        }

        var updated = Load().Where(p => !SameName(p.Name, profile.Name)).ToList();
        updated.Add(profile);
        Write(updated);
    }

    /// <summary>
    /// Renames the profile <paramref name="oldName"/> to <paramref name="newName"/>. No-ops when
    /// the source is missing, the new name is blank, or the new name collides with a different
    /// existing profile.
    /// </summary>
    public void Rename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var all = Load().ToList();
        var source = all.FirstOrDefault(p => SameName(p.Name, oldName));
        if (source is null)
        {
            return;
        }

        if (all.Any(p => SameName(p.Name, newName) && !SameName(p.Name, oldName)))
        {
            return;
        }

        var updated = all.Where(p => !SameName(p.Name, oldName))
            .Append(source.WithName(newName))
            .ToList();
        Write(updated);
    }

    /// <summary>Deletes the named profile. Safe when it does not exist.</summary>
    public void Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var updated = Load().Where(p => !SameName(p.Name, name)).ToList();
        Write(updated);
    }

    private void Write(IEnumerable<ConnectionProfile> profiles)
    {
        var list = profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(list, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saving profiles is a convenience — never let a disk failure break connecting.
        }
    }

    private static bool SameName(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
