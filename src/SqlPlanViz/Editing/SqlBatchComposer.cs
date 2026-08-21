using System.Text;

namespace SqlPlanViz.Editing;

/// <summary>One column of a table-valued parameter's row grid.</summary>
public sealed class TvpColumn
{
    public string Name { get; init; } = string.Empty;

    public string DataType { get; init; } = "nvarchar(100)";
}

/// <summary>A value a parameter is bound to, in the form the composer needs it.</summary>
public sealed class ParameterBinding
{
    public string Name { get; init; } = string.Empty;

    public string DataType { get; init; } = "nvarchar(100)";

    public string Value { get; init; } = string.Empty;

    /// <summary>Explicit and distinct from an empty string — the plan requires the difference.</summary>
    public bool IsNull { get; init; }

    public bool IsTableValued { get; init; }

    /// <summary>Schema-qualified user table type, e.g. <c>dbo.IdList</c>. Table-valued only.</summary>
    public string TableTypeName { get; init; } = string.Empty;

    public IReadOnlyList<TvpColumn> Columns { get; init; } = [];

    /// <summary>Row values, outer list rows and inner list cells aligned to <see cref="Columns"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; } = [];
}

/// <summary>
/// The batch as it will be sent, plus the map back to what the user typed.
///
/// The prelude shifts every offset and every line number in the user's text. Without the map,
/// a SQL error on "line 3" of the composed batch would be reported against line 3 of the
/// editor, which is a different line — hence the plan calling out the offset map as one of
/// the two places correctness bugs hide.
/// </summary>
public sealed class ComposedBatch
{
    public string Text { get; init; } = string.Empty;

    /// <summary>Characters of prelude before the user's first character.</summary>
    public int PreludeLength { get; init; }

    /// <summary>Lines of prelude before the user's first line.</summary>
    public int PreludeLines { get; init; }

    /// <summary>Parameters that could not be turned into a literal, with the reason.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsValid => Errors.Count == 0;

    /// <summary>Maps a character offset in the composed batch to one in the editor's document.</summary>
    public int ToEditorOffset(int composedOffset) => Math.Max(0, composedOffset - PreludeLength);

    /// <summary>
    /// Maps a one-based line number from SQL Server back to a zero-based editor line.
    /// An error inside the prelude itself maps to line 0 — the batch is the app's fault
    /// there, and pointing at a line the user cannot see would be worse than pointing at
    /// the first one.
    /// </summary>
    public int ToEditorLine(int composedLineOneBased) =>
        Math.Max(0, composedLineOneBased - 1 - PreludeLines);

    /// <summary>True when a reported line falls in the generated prelude rather than the user's text.</summary>
    public bool IsPreludeLine(int composedLineOneBased) => composedLineOneBased - 1 < PreludeLines;
}

/// <summary>
/// Builds the batch that actually gets sent: a generated <c>DECLARE</c> prelude for every
/// parameter the user supplied, then their text unchanged (live-plan-editor-plan.md Phase 3).
///
/// The user's text is never rewritten. That is what keeps the offset map a single subtraction
/// and keeps a compile error pointing at the line the user is looking at.
/// </summary>
public static class SqlBatchComposer
{
    public static ComposedBatch Compose(string userText, IReadOnlyList<ParameterBinding> bindings)
    {
        userText ??= string.Empty;
        var errors = new List<string>();

        if (bindings.Count == 0)
        {
            return new ComposedBatch { Text = userText, PreludeLength = 0, PreludeLines = 0 };
        }

        var prelude = new StringBuilder();
        prelude.AppendLine("/* Parameters supplied by SQL Plan Visualizer. */");

        foreach (var binding in bindings)
        {
            if (binding.IsTableValued)
            {
                AppendTableValued(prelude, binding, errors);
            }
            else
            {
                AppendScalar(prelude, binding, errors);
            }
        }

        prelude.AppendLine();

        var preludeText = prelude.ToString();
        return new ComposedBatch
        {
            Text = preludeText + userText,
            PreludeLength = preludeText.Length,
            PreludeLines = CountLines(preludeText),
            Errors = errors,
        };
    }

    private static void AppendScalar(StringBuilder prelude, ParameterBinding binding, List<string> errors)
    {
        var literal = SqlLiteral.Format(binding.Value, binding.DataType, binding.IsNull);
        if (!literal.IsValid)
        {
            errors.Add($"{binding.Name}: {literal.Error}");
            return;
        }

        prelude.Append("DECLARE ")
            .Append(binding.Name)
            .Append(' ')
            .Append(NormalizeType(binding.DataType))
            .Append(" = ")
            .Append(literal.Text)
            .AppendLine(";");
    }

    private static void AppendTableValued(StringBuilder prelude, ParameterBinding binding, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(binding.TableTypeName))
        {
            errors.Add($"{binding.Name}: choose the table type this parameter uses.");
            return;
        }

        prelude.Append("DECLARE ")
            .Append(binding.Name)
            .Append(" AS ")
            .Append(QualifyTypeName(binding.TableTypeName))
            .AppendLine(";");

        if (binding.Rows.Count == 0 || binding.Columns.Count == 0)
        {
            return;
        }

        // Rows are rendered before anything is emitted, so a grid where every row fails
        // produces no INSERT at all rather than one with an empty VALUES list.
        var rendered = new List<string>();
        foreach (var row in binding.Rows)
        {
            var cells = new List<string>(binding.Columns.Count);
            var rowValid = true;

            for (var i = 0; i < binding.Columns.Count; i++)
            {
                var value = i < row.Count ? row[i] : null;

                // A null cell is a NULL; an empty string is an empty string. Same distinction
                // as the scalar IsNull toggle, one level down.
                var literal = SqlLiteral.Format(value, binding.Columns[i].DataType, isNull: value is null);
                if (!literal.IsValid)
                {
                    errors.Add($"{binding.Name}, row {rendered.Count + 1}, {binding.Columns[i].Name}: {literal.Error}");
                    rowValid = false;
                    break;
                }

                cells.Add(literal.Text);
            }

            if (rowValid)
            {
                rendered.Add("(" + string.Join(", ", cells) + ")");
            }
        }

        if (rendered.Count == 0)
        {
            return;
        }

        var columnList = "(" + string.Join(", ", binding.Columns.Select(c => SqlLiteral.QuoteIdentifier(c.Name))) + ")";

        // SQL Server caps a VALUES list at 1000 rows, so longer grids become several INSERTs.
        const int batchSize = 1000;
        for (var i = 0; i < rendered.Count; i += batchSize)
        {
            prelude.Append("INSERT INTO ")
                .Append(binding.Name)
                .Append(' ')
                .AppendLine(columnList);

            prelude.Append("VALUES ")
                .Append(string.Join(", ", rendered.Skip(i).Take(batchSize)))
                .AppendLine(";");
        }
    }

    /// <summary>
    /// Passes a recognised type through as written and brackets anything else. A plan reports
    /// its types in lower case without brackets, and a user-defined type needs quoting.
    /// </summary>
    private static string NormalizeType(string dataType)
    {
        var text = (dataType ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "nvarchar(100)";
        }

        var baseType = SqlLiteral.BaseType(text);
        if (SqlLanguage.IsDataType(baseType))
        {
            return text;
        }

        return QualifyTypeName(text);
    }

    /// <summary>Brackets each part of a possibly schema-qualified type name.</summary>
    private static string QualifyTypeName(string name)
    {
        var parts = (name ?? string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('[', ']'))
            .Where(p => p.Length > 0)
            .ToList();

        return parts.Count == 0
            ? "[dbo].[TableType]"
            : string.Join('.', parts.Select(SqlLiteral.QuoteIdentifier));
    }

    private static int CountLines(string text)
    {
        var lines = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}
