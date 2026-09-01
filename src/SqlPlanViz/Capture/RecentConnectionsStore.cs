using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlPlanViz.Capture;

/// <summary>
/// One remembered connection target. The password is deliberately absent — recent
/// connections never carry a secret (Phase 5 handles opt-in password storage separately).
/// </summary>
public sealed record RecentConnection(string Server, string Database, string UserId, AuthMode Auth);

/// <summary>
/// Remembers the most recent connection targets (server / database / login / auth — never the
/// password) in a plain JSON file under the per-user local app-data folder.
///
/// The app is <b>unpackaged</b> (<c>WindowsPackageType=None</c>), so <c>ApplicationData.Current</c>
/// throws here. We use <see cref="Environment.SpecialFolder.LocalApplicationData"/> + an app
/// subfolder instead — an ordinary directory this process can always create and write.
/// </summary>
public sealed class RecentConnectionsStore
{
    public const int MaxEntries = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public RecentConnectionsStore()
        : this(DefaultFilePath())
    {
    }

    /// <summary>Test/override seam — point the store at an explicit file.</summary>
    public RecentConnectionsStore(string filePath) => _filePath = filePath;

    public string FilePath => _filePath;

    public static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlPlanViz",
        "recent-connections.json");

    /// <summary>Most-recent-first. Never throws — a missing or corrupt file yields an empty list.</summary>
    public IReadOnlyList<RecentConnection> Load()
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<RecentConnection>();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<RecentConnection>>(File.ReadAllText(_filePath), Options);
            return loaded is null
                ? Array.Empty<RecentConnection>()
                : loaded.Take(MaxEntries).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<RecentConnection>();
        }
    }

    /// <summary>
    /// Moves <paramref name="entry"/> to the front, de-duplicated by server + database
    /// (case-insensitive), capped at <see cref="MaxEntries"/>. Blank-server entries are ignored.
    /// </summary>
    public void Record(RecentConnection entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Server))
        {
            return;
        }

        var updated = new List<RecentConnection> { entry };
        updated.AddRange(Load().Where(existing => !SameTarget(existing, entry)));

        Save(updated.Take(MaxEntries).ToList());
    }

    public void Save(IEnumerable<RecentConnection> entries)
    {
        var list = entries.Take(MaxEntries).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(list, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering connections is a convenience — never let a disk failure break connecting.
        }
    }

    private static bool SameTarget(RecentConnection a, RecentConnection b) =>
        string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase);
}
