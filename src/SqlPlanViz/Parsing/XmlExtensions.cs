using System.Globalization;
using System.Xml.Linq;

namespace SqlPlanViz.Parsing;

/// <summary>
/// Showplan XML arrives either with a bare default namespace or with a prefix (p1:RelOp)
/// depending on the SQL Server version and the client that produced it (TDD §7). Every
/// lookup here goes through <see cref="XName.LocalName"/> so both forms parse identically.
/// </summary>
internal static class XmlExtensions
{
    public static IEnumerable<XElement> Elems(this XElement e, string localName) =>
        e.Elements().Where(x => x.Name.LocalName == localName);

    public static XElement? Elem(this XElement e, string localName) =>
        e.Elems(localName).FirstOrDefault();

    public static string? Attr(this XElement e, string localName) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    public static double Dbl(this XElement e, string localName, double fallback = 0) =>
        e.Attr(localName) is { } v
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : fallback;

    public static double? DblOrNull(this XElement e, string localName) =>
        e.Attr(localName) is { } v
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    public static int Int(this XElement e, string localName, int fallback = 0) =>
        e.Attr(localName) is { } v
        && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : fallback;

    public static bool Bool(this XElement e, string localName, bool fallback = false)
    {
        var v = e.Attr(localName);
        return v switch
        {
            null => fallback,
            "1" or "true" or "True" => true,
            "0" or "false" or "False" => false,
            _ => fallback,
        };
    }

    /// <summary>
    /// Descendants of <paramref name="e"/> that stop at nested RelOps — used so an
    /// operator never picks up the Object/Predicate belonging to one of its children.
    /// </summary>
    public static IEnumerable<XElement> DescendantsWithinOperator(this XElement e)
    {
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "RelOp")
            {
                continue;
            }

            yield return child;

            foreach (var d in child.DescendantsWithinOperator())
            {
                yield return d;
            }
        }
    }

    public static XElement? FirstWithinOperator(this XElement e, string localName) =>
        e.DescendantsWithinOperator().FirstOrDefault(x => x.Name.LocalName == localName);

    /// <summary>Strips the [brackets] Showplan wraps identifiers in.</summary>
    public static string Unbracket(this string? s) => s?.Trim().Trim('[', ']') ?? string.Empty;
}
