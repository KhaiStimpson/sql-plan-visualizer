using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length != 3 || args[0] is not ("save" or "check"))
{
    Console.Error.WriteLine("Usage: SqlPlanViz.Baseline <save|check> <plan.sqlplan> <baseline.json>");
    return 64;
}

try
{
    var statement = ReadPlan(args[1]);
    if (args[0] == "save")
    {
        var baseline = new Baseline(statement.Fingerprint, statement.DurationMs, 0.20, DateTime.UtcNow);
        File.WriteAllText(args[2], JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved {statement.Fingerprint} to {args[2]}");
        return 0;
    }

    var result = Check(statement, args[2]);
    (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
    return result.Success ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static PlanData ReadPlan(string path)
{
    var document = XDocument.Parse(File.ReadAllText(path));
    var statement = document.Descendants()
        .Where(element => element.Name.LocalName == "StmtSimple" && element.Descendants().Any(child => child.Name.LocalName == "QueryPlan"))
        .OrderByDescending(element => ParseDouble(Attr(element, "StatementSubTreeCost")))
        .First();
    var queryPlan = statement.Descendants().First(element => element.Name.LocalName == "QueryPlan");
    var root = queryPlan.Descendants().First(element => element.Name.LocalName == "RelOp");
    var shape = new StringBuilder();
    AppendNode(root, shape);
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString()))).ToLowerInvariant();
    var timeStats = queryPlan.Elements().FirstOrDefault(element => element.Name.LocalName == "QueryTimeStats");
    return new PlanData(fingerprint, ParseNullableDouble(Attr(timeStats, "ElapsedTime")));
}

static void AppendNode(XElement relOp, StringBuilder shape)
{
    shape.Append('(')
        .Append((Attr(relOp, "PhysicalOp") ?? "Unknown").Trim().ToUpperInvariant())
        .Append('|')
        .Append(ObjectName(relOp).Replace("[", string.Empty).Replace("]", string.Empty).ToUpperInvariant());
    foreach (var child in ChildRelOps(relOp)) AppendNode(child, shape);
    shape.Append(')');
}

static IEnumerable<XElement> ChildRelOps(XElement relOp)
{
    var result = new List<XElement>();
    foreach (var child in relOp.Elements()) Collect(child, result);
    return result;

    static void Collect(XElement element, List<XElement> result)
    {
        if (element.Name.LocalName == "RelOp") { result.Add(element); return; }
        foreach (var child in element.Elements()) Collect(child, result);
    }
}

static string ObjectName(XElement relOp)
{
    XElement? found = null;
    foreach (var child in relOp.Elements())
    {
        found = FindObject(child);
        if (found is not null) break;
    }
    if (found is null) return string.Empty;

    var schema = Unbracket(Attr(found, "Schema"));
    var table = Unbracket(Attr(found, "Table"));
    var index = Unbracket(Attr(found, "Index"));
    var alias = Unbracket(Attr(found, "Alias"));
    if (string.IsNullOrEmpty(table)) return index;
    var name = string.IsNullOrEmpty(schema) ? table : schema + "." + table;
    if (!string.IsNullOrEmpty(alias) && alias != table) name += " AS " + alias;
    if (!string.IsNullOrEmpty(index)) name += "." + index;
    return name;

    static XElement? FindObject(XElement element)
    {
        if (element.Name.LocalName == "RelOp") return null;
        if (element.Name.LocalName == "Object") return element;
        foreach (var child in element.Elements())
        {
            var found = FindObject(child);
            if (found is not null) return found;
        }
        return null;
    }
}

static CheckResult Check(PlanData current, string path)
{
    if (!File.Exists(path)) return new(false, $"Baseline not found: {path}");
    var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path));
    if (baseline is null || string.IsNullOrWhiteSpace(baseline.Fingerprint)) return new(false, "Baseline does not contain a fingerprint.");
    if (!string.Equals(baseline.Fingerprint, current.Fingerprint, StringComparison.OrdinalIgnoreCase))
        return new(false, $"Plan shape changed: expected {baseline.Fingerprint[..8]}, got {current.Fingerprint[..8]}.");
    if (baseline.DurationMs is double expected && current.DurationMs is double actual && actual > expected * (1 + baseline.DurationToleranceFraction))
        return new(false, $"Duration regressed: baseline {expected:N1} ms, current {actual:N1} ms.");
    return new(true, $"Baseline passed: fingerprint {current.Fingerprint[..8]} and duration are within bounds.");
}

static string? Attr(XElement? element, string name) => element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;
static string Unbracket(string? value) => (value ?? string.Empty).Trim('[', ']');
static double ParseDouble(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
static double? ParseNullableDouble(string? value) => value is null ? null : ParseDouble(value);

sealed record Baseline(string Fingerprint, double? DurationMs, double DurationToleranceFraction, DateTime CreatedUtc);
sealed record PlanData(string Fingerprint, double? DurationMs);
sealed record CheckResult(bool Success, string Message);
