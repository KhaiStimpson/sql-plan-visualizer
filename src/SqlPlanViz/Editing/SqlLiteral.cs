using System.Globalization;

namespace SqlPlanViz.Editing;

/// <summary>Result of turning a typed-in value into T-SQL. Invalid input never becomes a literal.</summary>
public sealed record LiteralResult(string Text, bool IsValid, string? Error = null)
{
    public static LiteralResult Ok(string text) => new(text, true);

    public static LiteralResult Invalid(string error) => new("NULL", false, error);
}

/// <summary>
/// Turns a parameter's text value into a T-SQL literal of its declared type
/// (live-plan-editor-plan.md Phase 3).
///
/// This is one of the two places the plan singles out as where correctness bugs hide, and the
/// rule it enforces is simple: a value never reaches the batch as raw text. Strings are
/// quoted and their quotes doubled, numbers must parse as numbers before they are emitted
/// bare, and anything that does not validate produces an error rather than a literal — so a
/// value containing an apostrophe cannot end the string it is in, and a value containing a
/// semicolon cannot end the statement.
/// </summary>
public static class SqlLiteral
{
    /// <summary>The bare type name, with any length or precision suffix removed.</summary>
    public static string BaseType(string dataType)
    {
        var text = (dataType ?? string.Empty).Trim();
        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = text[..paren];
        }

        // A user-defined or table type arrives schema-qualified; the last part is the name.
        var dot = text.LastIndexOf('.');
        return (dot >= 0 ? text[(dot + 1)..] : text).Trim().ToLowerInvariant();
    }

    public static bool IsText(string dataType) => BaseType(dataType)
        is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" or "xml";

    public static bool IsNumeric(string dataType) => BaseType(dataType)
        is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric"
        or "float" or "real" or "money" or "smallmoney";

    public static bool IsDateTime(string dataType) => BaseType(dataType)
        is "date" or "time" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset";

    public static bool IsBinary(string dataType) => BaseType(dataType)
        is "binary" or "varbinary" or "image" or "rowversion" or "timestamp";

    public static bool IsBit(string dataType) => BaseType(dataType) is "bit";

    public static bool IsGuid(string dataType) => BaseType(dataType) is "uniqueidentifier";

    public static LiteralResult Format(string? value, string dataType, bool isNull)
    {
        if (isNull)
        {
            return LiteralResult.Ok("NULL");
        }

        var text = value ?? string.Empty;
        var baseType = BaseType(dataType);

        if (IsBit(dataType))
        {
            return text.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "on" => LiteralResult.Ok("1"),
                "0" or "false" or "no" or "off" or "" => LiteralResult.Ok("0"),
                _ => LiteralResult.Invalid("A bit takes 0 or 1."),
            };
        }

        if (IsNumeric(dataType))
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return LiteralResult.Invalid($"Enter a {baseType} value, or tick NULL.");
            }

            // Invariant parsing on purpose: a comma decimal separator in a locale that uses
            // one would otherwise be emitted as a comma and split the argument list.
            if (!decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
            {
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
                {
                    return LiteralResult.Invalid($"'{trimmed}' is not a valid {baseType}.");
                }

                return LiteralResult.Ok(dbl.ToString("R", CultureInfo.InvariantCulture));
            }

            return LiteralResult.Ok(dec.ToString(CultureInfo.InvariantCulture));
        }

        if (IsGuid(dataType))
        {
            return Guid.TryParse(text.Trim(), out var guid)
                ? LiteralResult.Ok($"'{guid:D}'")
                : LiteralResult.Invalid("Enter a GUID, e.g. 6F9619FF-8B86-D011-B42D-00C04FC964FF.");
        }

        if (IsBinary(dataType))
        {
            var hex = text.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                hex = hex[2..];
            }

            if (hex.Length == 0)
            {
                return LiteralResult.Ok("0x");
            }

            return hex.All(Uri.IsHexDigit) && hex.Length % 2 == 0
                ? LiteralResult.Ok("0x" + hex.ToUpperInvariant())
                : LiteralResult.Invalid("Enter an even number of hex digits, e.g. 0x00FF.");
        }

        if (IsDateTime(dataType))
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return LiteralResult.Invalid($"Enter a {baseType} value, or tick NULL.");
            }

            if (!DateTimeOffset.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
                && !DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return LiteralResult.Invalid($"'{trimmed}' is not a valid {baseType}.");
            }

            // ISO 8601 is the one format SQL Server reads the same way under every language
            // and DATEFORMAT setting.
            var format = baseType switch
            {
                "date" => "yyyy-MM-dd",
                "time" => "HH:mm:ss.fffffff",
                "datetimeoffset" => "yyyy-MM-ddTHH:mm:ss.fffffffzzz",
                _ => "yyyy-MM-ddTHH:mm:ss.fff",
            };

            return LiteralResult.Ok(Quote(parsed.ToString(format, CultureInfo.InvariantCulture), national: false));
        }

        // Text, and anything unrecognised. Quoting an unknown type is the safe direction:
        // worst case SQL Server converts it, whereas emitting it bare would be an injection.
        return LiteralResult.Ok(Quote(text, national: baseType is "nchar" or "nvarchar" or "ntext" or "sysname" or "xml" || !IsText(dataType)));
    }

    /// <summary>Doubles embedded quotes. The single rule that keeps a value from escaping its literal.</summary>
    public static string Quote(string value, bool national = true) =>
        (national ? "N'" : "'") + (value ?? string.Empty).Replace("'", "''") + "'";

    /// <summary>Wraps an identifier in brackets, doubling any embedded closing bracket.</summary>
    public static string QuoteIdentifier(string name) =>
        "[" + (name ?? string.Empty).Replace("]", "]]") + "]";
}
