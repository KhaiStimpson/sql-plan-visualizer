using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlPlanViz.Editing;

/// <summary>What running a statement would do. Ordered by how much explaining it needs.</summary>
public enum StatementRisk
{
    /// <summary>Reads only. Safe to execute.</summary>
    ReadOnly,

    /// <summary>Changes data: INSERT, UPDATE, DELETE, MERGE, TRUNCATE, SELECT … INTO.</summary>
    Modifying,

    /// <summary>Changes schema: any CREATE, ALTER or DROP.</summary>
    Ddl,

    /// <summary>Changes the server or a permission: GRANT, BACKUP, DBCC, KILL, RECONFIGURE.</summary>
    Administrative,

    /// <summary>Opaque — an EXEC or dynamic SQL whose body this cannot see.</summary>
    Unknown,
}

/// <summary>One statement the confirmation dialog has to name.</summary>
public sealed record RiskyStatement(StatementRisk Risk, string Kind, string Target, int Line)
{
    /// <summary>One line for the dialog's list: "DELETE from dbo.Orders — line 4".</summary>
    public string Describe() => string.IsNullOrEmpty(Target)
        ? $"{Kind} — line {Line}"
        : $"{Kind} {Target} — line {Line}";
}

/// <summary>
/// Whether a batch is safe to execute (live-plan-editor-plan.md Phase 7).
///
/// <see cref="ParseFailed"/> is not a detail: a batch this could not parse is a batch whose
/// statements were never classified, and the only honest answer for one of those is that it
/// might do anything. The dialog treats it as unsafe.
/// </summary>
public sealed record BatchSafetyReport
{
    public IReadOnlyList<RiskyStatement> Statements { get; init; } = [];

    public bool ParseFailed { get; init; }

    public IReadOnlyList<RiskyStatement> Risky =>
        [.. Statements.Where(s => s.Risk != StatementRisk.ReadOnly)];

    /// <summary>True only when the batch parsed and every statement in it reads.</summary>
    public bool IsReadOnly => !ParseFailed && Risky.Count == 0;

    /// <summary>The worst thing in the batch, for the dialog's headline.</summary>
    public StatementRisk WorstRisk => ParseFailed
        ? StatementRisk.Unknown
        : Risky.Count == 0 ? StatementRisk.ReadOnly : Risky.Max(s => s.Risk);

    public string Headline => ParseFailed
        ? "This batch could not be parsed, so what it would do is unknown."
        : WorstRisk switch
        {
            StatementRisk.ReadOnly => "This batch only reads.",
            StatementRisk.Modifying => $"This batch changes data — {Risky.Count} statement{(Risky.Count == 1 ? string.Empty : "s")} will modify rows.",
            StatementRisk.Ddl => "This batch changes schema.",
            StatementRisk.Administrative => "This batch changes server or security state.",
            _ => "This batch executes something this cannot inspect.",
        };
}

/// <summary>
/// Classifies a batch's statements with ScriptDom, so the confirmation dialog can name
/// exactly what running it would do rather than warning in the abstract.
/// </summary>
public static class BatchSafetyAnalyzer
{
    public static BatchSafetyReport Analyse(string sql, SqlParserVersion? parserVersion = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new BatchSafetyReport();
        }

        var fragment = TSqlParserFactory.TryParse(sql, out _, parserVersion);
        if (fragment is null)
        {
            // Unparseable means unclassified, and unclassified means unsafe. Failing open
            // here would be the one bug in this file that actually costs someone a table.
            return new BatchSafetyReport { ParseFailed = true };
        }

        var visitor = new SafetyVisitor();
        fragment.Accept(visitor);
        return new BatchSafetyReport { Statements = visitor.Statements };
    }

    private sealed class SafetyVisitor : TSqlFragmentVisitor
    {
        public List<RiskyStatement> Statements { get; } = [];

        public override void Visit(TSqlStatement node)
        {
            var type = node.GetType().Name;
            var (risk, kind, target) = Classify(node, type);
            Statements.Add(new RiskyStatement(risk, kind, target, node.StartLine));
        }

        private static (StatementRisk Risk, string Kind, string Target) Classify(TSqlStatement node, string typeName)
        {
            switch (node)
            {
                case InsertStatement insert:
                    return (StatementRisk.Modifying, "INSERT into", NameOf(insert.InsertSpecification?.Target));

                case UpdateStatement update:
                    return (StatementRisk.Modifying, "UPDATE", NameOf(update.UpdateSpecification?.Target));

                case DeleteStatement delete:
                    return (StatementRisk.Modifying, "DELETE from", NameOf(delete.DeleteSpecification?.Target));

                case MergeStatement merge:
                    return (StatementRisk.Modifying, "MERGE into", NameOf(merge.MergeSpecification?.Target));

                case TruncateTableStatement truncate:
                    return (StatementRisk.Modifying, "TRUNCATE", Join(truncate.TableName));

                case BulkInsertStatement bulk:
                    return (StatementRisk.Modifying, "BULK INSERT into", Join(bulk.To));

                // SELECT … INTO reads like a query and creates a table.
                case SelectStatement { Into: not null } into:
                    return (StatementRisk.Modifying, "SELECT … INTO", Join(into.Into));

                case SelectStatement:
                    return (StatementRisk.ReadOnly, "SELECT", string.Empty);

                case ExecuteStatement execute:
                    return (StatementRisk.Unknown, "EXEC", ExecuteTarget(execute));

                case PredicateSetStatement or SetVariableStatement or DeclareVariableStatement
                    or DeclareTableVariableStatement or SetOnOffStatement or UseStatement
                    or PrintStatement or IfStatement or WhileStatement or BeginEndBlockStatement
                    or TryCatchStatement or BeginTransactionStatement or CommitTransactionStatement
                    or RollbackTransactionStatement or ReturnStatement or BreakStatement
                    or ContinueStatement or DeclareCursorStatement:
                    return (StatementRisk.ReadOnly, Humanise(typeName), string.Empty);
            }

            // Everything else is classified by the shape of its type name. Enumerating the
            // hundred-odd DDL node types by hand would be a list nobody keeps current, and a
            // missed one would fail open.
            if (typeName.StartsWith("Create", StringComparison.Ordinal)
                || typeName.StartsWith("Alter", StringComparison.Ordinal)
                || typeName.StartsWith("Drop", StringComparison.Ordinal))
            {
                return (StatementRisk.Ddl, Humanise(typeName), string.Empty);
            }

            if (typeName.StartsWith("Grant", StringComparison.Ordinal)
                || typeName.StartsWith("Deny", StringComparison.Ordinal)
                || typeName.StartsWith("Revoke", StringComparison.Ordinal)
                || typeName.StartsWith("Backup", StringComparison.Ordinal)
                || typeName.StartsWith("Restore", StringComparison.Ordinal)
                || typeName.StartsWith("Dbcc", StringComparison.Ordinal)
                || typeName.StartsWith("Kill", StringComparison.Ordinal)
                || typeName.StartsWith("Shutdown", StringComparison.Ordinal)
                || typeName.StartsWith("Reconfigure", StringComparison.Ordinal)
                || typeName.StartsWith("Checkpoint", StringComparison.Ordinal))
            {
                return (StatementRisk.Administrative, Humanise(typeName), string.Empty);
            }

            // An unrecognised statement is not assumed harmless.
            return (StatementRisk.Unknown, Humanise(typeName), string.Empty);
        }

        private static string ExecuteTarget(ExecuteStatement execute) =>
            execute.ExecuteSpecification?.ExecutableEntity switch
            {
                ExecutableProcedureReference procedure =>
                    Join(procedure.ProcedureReference?.ProcedureReference?.Name),
                ExecutableStringList => "dynamic SQL",
                _ => string.Empty,
            };

        private static string NameOf(TableReference? reference) => reference switch
        {
            NamedTableReference named => Join(named.SchemaObject),
            VariableTableReference variable => variable.Variable?.Name ?? string.Empty,
            _ => string.Empty,
        };

        private static string Join(SchemaObjectName? name) =>
            name is null ? string.Empty : string.Join('.', name.Identifiers.Select(i => i.Value));

        /// <summary>"CreateIndexStatement" → "CREATE INDEX".</summary>
        private static string Humanise(string typeName)
        {
            var text = typeName.EndsWith("Statement", StringComparison.Ordinal)
                ? typeName[..^"Statement".Length]
                : typeName;

            var words = new List<string>();
            var current = new System.Text.StringBuilder();

            foreach (var c in text)
            {
                if (char.IsUpper(c) && current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
            }

            return string.Join(' ', words).ToUpperInvariant();
        }
    }
}
