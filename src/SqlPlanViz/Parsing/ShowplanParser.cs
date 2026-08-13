using System.Text;
using System.Xml;
using System.Xml.Linq;
using SqlPlanViz.Model;

namespace SqlPlanViz.Parsing;

public sealed class ShowplanParseException : Exception
{
    public ShowplanParseException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Showplan XML → normalized <see cref="ExecutionPlan"/> (TDD §7). One parser serves both
/// capture paths (§6): a .sqlplan file and a live SET STATISTICS XML result are the same
/// document.
/// </summary>
public static class ShowplanParser
{
    /// <summary>
    /// Elements under a RelOp that describe the operator itself rather than its input.
    /// Everything else is the physical-op element whose children are the child RelOps.
    /// </summary>
    private static readonly HashSet<string> RelOpMetadata =
    [
        "OutputList",
        "Warnings",
        "RunTimeInformation",
        "RunTimePartitionSummary",
        "MemoryFractions",
        "InternalInfo",
    ];

    public static ExecutionPlan Parse(string xml, string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new ShowplanParseException("The plan is empty.");
        }

        XDocument doc;
        try
        {
            // Reading through a TextReader makes the reader ignore the declared encoding,
            // which matters because .sqlplan files routinely declare utf-16 while we hold
            // an already-decoded string.
            using var reader = XmlReader.Create(
                new StringReader(xml.Trim()),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ShowplanParseException(
                $"That isn't well-formed XML (line {ex.LineNumber}, position {ex.LinePosition}): {ex.Message}",
                ex);
        }

        if (doc.Root is null)
        {
            throw new ShowplanParseException("The plan is empty.");
        }

        var statements = doc.Descendants()
            .Where(e => e.Name.LocalName == "QueryPlan")
            .Select(ParseStatement)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        if (statements.Count == 0)
        {
            var hint = doc.Root.Name.LocalName is "ShowPlanXML"
                ? "It looks like Showplan XML but contains no QueryPlan element."
                : $"Expected a ShowPlanXML document but the root element is <{doc.Root.Name.LocalName}>.";
            throw new ShowplanParseException($"No execution plan found. {hint}");
        }

        return new ExecutionPlan { Statements = statements, SourceName = sourceName };
    }

    private static PlanStatement? ParseStatement(XElement queryPlan)
    {
        var rootRelOp = queryPlan.Elem("RelOp");
        if (rootRelOp is null)
        {
            // DML statements carry their plan under a nested element; fall back to the
            // first RelOp anywhere beneath.
            rootRelOp = queryPlan.Descendants().FirstOrDefault(e => e.Name.LocalName == "RelOp");
            if (rootRelOp is null)
            {
                return null;
            }
        }

        var stmt = queryPlan.Ancestors().FirstOrDefault(a => a.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal));
        var timeStats = queryPlan.Elem("QueryTimeStats");
        var memoryGrant = ParseMemoryGrant(queryPlan);
        var root = ParseNode(rootRelOp, memoryGrant);

        var summary = new PlanSummary
        {
            StatementText = stmt?.Attr("StatementText")?.Trim() ?? "(statement text not recorded in this plan)",
            TotalSubtreeCost = stmt?.DblOrNull("StatementSubTreeCost") ?? root.EstimatedSubtreeCost,
            DegreeOfParallelism = queryPlan.Int("DegreeOfParallelism", 1),
            QueryElapsedMs = timeStats?.DblOrNull("ElapsedTime"),
            QueryCpuMs = timeStats?.DblOrNull("CpuTime"),
            MemoryGrant = memoryGrant,
            Waits = ParseWaitStats(queryPlan),
            Parameters = ParseParameters(queryPlan),
            StatisticsUsed = ParseStatisticsUsage(queryPlan),
            Compile = ParseCompileInfo(queryPlan, stmt),
        };

        return new PlanStatement
        {
            Summary = summary,
            Root = root,
            MissingIndexes = ParseMissingIndexes(queryPlan),
        };
    }

    private static PlanNode ParseNode(XElement relOp, MemoryGrantInfo? memoryGrant = null)
    {
        var children = ChildRelOps(relOp).Select(c => ParseNode(c)).ToList();

        // The physical-op element (IndexScan, Hash, NestedLoops, …) holds the Object,
        // Predicate and friends; everything else under RelOp is bookkeeping.
        var opElement = relOp.Elements()
            .FirstOrDefault(e => !RelOpMetadata.Contains(e.Name.LocalName));

        var runtime = ParseRuntime(relOp);
        var subtreeCost = relOp.Dbl("EstimatedTotalSubtreeCost");
        var objectParts = opElement is null ? default : ParseObjectParts(opElement);

        return new PlanNode
        {
            NodeId = relOp.Int("NodeId"),
            PhysicalOp = relOp.Attr("PhysicalOp") ?? opElement?.Name.LocalName ?? "Unknown",
            LogicalOp = relOp.Attr("LogicalOp") ?? string.Empty,
            EstimatedRows = relOp.Dbl("EstimateRows"),
            EstimatedExecutions = relOp.Dbl("EstimateRebinds") + relOp.Dbl("EstimateRewinds") + 1,
            EstimatedSubtreeCost = subtreeCost,
            EstimatedOperatorCost = Math.Max(0, subtreeCost - children.Sum(c => c.EstimatedSubtreeCost)),
            EstimatedCpuCost = relOp.Dbl("EstimateCPU"),
            EstimatedIoCost = relOp.Dbl("EstimateIO"),
            Parallel = relOp.Bool("Parallel"),
            ActualRows = runtime.Rows,
            ActualElapsedMs = runtime.ElapsedMs,
            ActualCpuMs = runtime.CpuMs,
            ActualExecutions = runtime.Executions,
            ObjectName = FormatObjectName(objectParts),
            ObjectTable = objectParts.Table,
            ObjectAlias = objectParts.Alias,
            Predicate = opElement is null ? null : ParsePredicate(opElement, "Predicate")
                                                   ?? ParsePredicate(opElement, "Where"),
            SeekPredicate = opElement is null ? null : ParsePredicate(opElement, "SeekPredicates"),
            OutputList = ParseOutputList(relOp),
            Warnings = ParseWarnings(relOp),
            Children = children,
            MemoryGrant = memoryGrant,
            PerThread = ParsePerThread(relOp),
        };
    }

    /// <summary>
    /// One <see cref="ThreadRuntime"/> per RunTimeCountersPerThread element. Purely additive
    /// alongside <see cref="ParseRuntime"/>, which still does the sum/max aggregation that
    /// <see cref="PlanNode.ActualRows"/>/<see cref="PlanNode.ActualElapsedMs"/> rely on.
    /// </summary>
    private static List<ThreadRuntime> ParsePerThread(XElement relOp)
    {
        var rti = relOp.Elem("RunTimeInformation") ?? relOp.FirstWithinOperator("RunTimeInformation");
        if (rti is null)
        {
            return [];
        }

        return rti.Elems("RunTimeCountersPerThread")
            .Select(t => new ThreadRuntime(
                t.Int("Thread"),
                t.Dbl("ActualRows"),
                t.DblOrNull("ActualElapsedms"),
                t.DblOrNull("ActualCPUms")))
            .ToList();
    }

    /// <summary>
    /// MemoryGrantInfo sits directly under QueryPlan, once per statement — it describes the
    /// grant for the statement as a whole, not a single operator, but the model attaches it
    /// to the grant-owning operator (the root) so a rule walking <see cref="PlanNode"/> can
    /// find it without also threading <see cref="PlanSummary"/> through.
    /// </summary>
    private static MemoryGrantInfo? ParseMemoryGrant(XElement queryPlan)
    {
        var mgi = queryPlan.Elem("MemoryGrantInfo");
        if (mgi is null)
        {
            return null;
        }

        return new MemoryGrantInfo
        {
            SerialRequiredMemoryKb = mgi.Dbl("SerialRequiredMemory"),
            SerialDesiredMemoryKb = mgi.Dbl("SerialDesiredMemory"),
            RequestedMemoryKb = mgi.DblOrNull("RequestedMemory"),
            GrantedMemoryKb = mgi.DblOrNull("GrantedMemory"),
            MaxUsedMemoryKb = mgi.DblOrNull("MaxUsedMemory"),
            GrantWaitTimeMs = mgi.DblOrNull("GrantWaitTime") is double seconds ? seconds * 1000 : null,
        };
    }

    /// <summary>
    /// A RelOp's children are the RelOps nested inside its physical-op element — at any
    /// depth (a Compute Scalar's subquery sits several levels down), but never past
    /// another RelOp.
    /// </summary>
    private static List<XElement> ChildRelOps(XElement relOp)
    {
        var acc = new List<XElement>();
        foreach (var child in relOp.Elements())
        {
            Collect(child, acc);
        }

        return acc;

        static void Collect(XElement e, List<XElement> acc)
        {
            if (e.Name.LocalName == "RelOp")
            {
                acc.Add(e);
                return;
            }

            foreach (var child in e.Elements())
            {
                Collect(child, acc);
            }
        }
    }

    private static (double? Rows, double? ElapsedMs, double? CpuMs, int? Executions) ParseRuntime(XElement relOp)
    {
        var rti = relOp.Elem("RunTimeInformation") ?? relOp.FirstWithinOperator("RunTimeInformation");
        if (rti is null)
        {
            return (null, null, null, null);
        }

        var threads = rti.Elems("RunTimeCountersPerThread").ToList();
        if (threads.Count == 0)
        {
            return (null, null, null, null);
        }

        // Rows and CPU are per-thread totals that add up; elapsed is wall-clock, so the
        // slowest thread is the operator's real cost (TDD §7).
        return (
            threads.Sum(t => t.Dbl("ActualRows")),
            threads.Max(t => t.DblOrNull("ActualElapsedms") ?? 0),
            threads.Sum(t => t.DblOrNull("ActualCPUms") ?? 0),
            threads.Sum(t => t.Int("ActualExecutions")));
    }

    /// <summary>
    /// The object's parts, unbracketed. <see cref="FormatObjectName"/> flattens these into one
    /// display string; anything that needs to match the object against SQL text (the operator →
    /// SQL mapping) needs the table and alias on their own, since the alias is what the query
    /// actually says.
    /// </summary>
    private static ObjectParts ParseObjectParts(XElement opElement)
    {
        var obj = opElement.Name.LocalName == "Object"
            ? opElement
            : opElement.FirstWithinOperator("Object");

        return obj is null
            ? default
            : new ObjectParts(
                obj.Attr("Schema").Unbracket(),
                obj.Attr("Table").Unbracket(),
                obj.Attr("Index").Unbracket(),
                obj.Attr("Alias").Unbracket());
    }

    private readonly record struct ObjectParts(string? Schema, string? Table, string? Index, string? Alias);

    private static string? FormatObjectName(ObjectParts parts)
    {
        var (schema, table, index, alias) = parts;

        if (string.IsNullOrEmpty(table))
        {
            return string.IsNullOrEmpty(index) ? null : index;
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(schema))
        {
            sb.Append(schema).Append('.');
        }

        sb.Append(table);
        if (!string.IsNullOrEmpty(alias) && alias != table)
        {
            sb.Append(" AS ").Append(alias);
        }

        if (!string.IsNullOrEmpty(index))
        {
            sb.Append('.').Append(index);
        }

        return sb.ToString();
    }

    private static string? ParsePredicate(XElement opElement, string containerName)
    {
        var container = opElement.Name.LocalName == containerName
            ? opElement
            : opElement.FirstWithinOperator(containerName);
        if (container is null)
        {
            return null;
        }

        var strings = container.DescendantsWithinOperator()
            .Where(e => e.Name.LocalName == "ScalarOperator")
            .Select(e => e.Attr("ScalarString"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct()
            .ToList();

        return strings.Count == 0 ? null : string.Join(Environment.NewLine, strings);
    }

    private static List<string> ParseOutputList(XElement relOp)
    {
        var outputList = relOp.Elem("OutputList");
        return outputList is null
            ? []
            : outputList.Elems("ColumnReference").Select(FormatColumnReference).ToList();
    }

    private static string FormatColumnReference(XElement col)
    {
        var column = col.Attr("Column").Unbracket();
        var alias = col.Attr("Alias").Unbracket();
        var table = col.Attr("Table").Unbracket();
        var qualifier = !string.IsNullOrEmpty(alias) ? alias : table;
        return string.IsNullOrEmpty(qualifier) ? column : $"{qualifier}.{column}";
    }

    /// <summary>
    /// Statement-level wait stats: <c>&lt;QueryTimeStats&gt;&lt;WaitStats&gt;&lt;Wait
    /// WaitType="…" WaitTimeMs="…" WaitCount="…"/&gt;&lt;/WaitStats&gt;&lt;/QueryTimeStats&gt;</c>.
    /// Distinct from the per-node "Wait" element under a RelOp's Warnings, which is a
    /// different (older, per-operator) shape already handled by <see cref="ParseWarnings"/>.
    /// </summary>
    private static List<WaitStat> ParseWaitStats(XElement queryPlan)
    {
        var waitStats = queryPlan.Elem("QueryTimeStats")?.Elem("WaitStats")
                         ?? queryPlan.Elem("WaitStats");
        if (waitStats is null)
        {
            return [];
        }

        return waitStats.Elems("Wait")
            .Select(w => new WaitStat(
                w.Attr("WaitType") ?? "Unknown",
                w.Dbl("WaitTimeMs"),
                (long)w.Dbl("WaitCount")))
            .ToList();
    }

    /// <summary>
    /// <c>&lt;ParameterList&gt;&lt;ColumnReference Column="@p1" ParameterDataType="int"
    /// ParameterCompiledValue="(1)" ParameterRuntimeValue="(5)"/&gt;&lt;/ParameterList&gt;</c>,
    /// under QueryPlan. Absent on estimated-only plans and on plans with no parameters.
    /// </summary>
    private static List<ParameterInfo> ParseParameters(XElement queryPlan)
    {
        var list = queryPlan.Elem("ParameterList");
        if (list is null)
        {
            return [];
        }

        return list.Elems("ColumnReference")
            .Select(p => new ParameterInfo
            {
                Name = p.Attr("Column") ?? string.Empty,
                DataType = p.Attr("ParameterDataType"),
                CompiledValue = p.Attr("ParameterCompiledValue"),
                RuntimeValue = p.Attr("ParameterRuntimeValue"),
            })
            .ToList();
    }

    /// <summary>
    /// <c>&lt;OptimizerStatsUsage&gt;&lt;StatisticsInfo Database="[..]" Schema="[..]"
    /// Table="[..]" Statistics="[..]" ModificationCount="0" SamplingPercent="100"
    /// LastUpdate="…"/&gt;&lt;/OptimizerStatsUsage&gt;</c>, under QueryPlan.
    /// </summary>
    private static List<StatisticsUsage> ParseStatisticsUsage(XElement queryPlan)
    {
        var usage = queryPlan.Elem("OptimizerStatsUsage");
        if (usage is null)
        {
            return [];
        }

        return usage.Elems("StatisticsInfo")
            .Select(s => new StatisticsUsage
            {
                Database = s.Attr("Database").Unbracket(),
                Schema = s.Attr("Schema").Unbracket(),
                Table = s.Attr("Table").Unbracket(),
                StatisticsName = s.Attr("Statistics").Unbracket(),
                SamplingPercent = s.DblOrNull("SamplingPercent"),
                LastUpdate = s.Attr("LastUpdate") is { } d && DateTime.TryParse(
                    d, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null,
                ModificationCount = s.Attr("ModificationCount") is { } m && long.TryParse(
                    m, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var mc)
                    ? mc
                    : null,
            })
            .ToList();
    }

    /// <summary>
    /// Compile-time cost, budget outcome and environment: CompileTime/CompileCPU/
    /// CompileMemory and CardinalityEstimationModelVersion are attributes on QueryPlan;
    /// StatementOptmEarlyAbortReason is on the Stmt element; TraceFlags and SetOptions are
    /// child elements of QueryPlan and Stmt respectively.
    /// </summary>
    private static CompileInfo? ParseCompileInfo(XElement queryPlan, XElement? stmt)
    {
        var compileTime = queryPlan.DblOrNull("CompileTime");
        var compileCpu = queryPlan.DblOrNull("CompileCPU");
        var compileMemory = queryPlan.DblOrNull("CompileMemory");
        var earlyAbort = stmt?.Attr("StatementOptmEarlyAbortReason");
        var ceVersion = queryPlan.Attr("CardinalityEstimationModelVersion");
        var traceFlags = queryPlan.Elem("TraceFlags")?.Elems("TraceFlag")
            .Select(f => f.Attr("Value"))
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList() ?? [];
        var setOptions = stmt?.Elem("SetOptions")?.Attributes()
            .ToDictionary(a => a.Name.LocalName, a => a.Value)
            ?? new Dictionary<string, string>();

        if (compileTime is null && compileCpu is null && compileMemory is null
            && earlyAbort is null && ceVersion is null && traceFlags.Count == 0 && setOptions.Count == 0)
        {
            return null;
        }

        return new CompileInfo
        {
            CompileTimeMs = compileTime,
            CompileCpuMs = compileCpu,
            CompileMemoryKb = compileMemory,
            EarlyAbortReason = earlyAbort,
            CardinalityEstimationModelVersion = ceVersion,
            TraceFlags = traceFlags,
            SetOptions = setOptions,
        };
    }

    private static List<PlanWarning> ParseWarnings(XElement relOp)
    {
        var warnings = relOp.Elem("Warnings");
        if (warnings is null)
        {
            return [];
        }

        var result = new List<PlanWarning>();

        // Some warnings are attributes on <Warnings> itself rather than child elements.
        if (warnings.Bool("NoJoinPredicate"))
        {
            result.Add(new PlanWarning(
                "NoJoinPredicate",
                WarningSeverity.Critical,
                "This join has no predicate — every row on one side is matched against every row on the other."));
        }

        if (warnings.Bool("SpatialGuess"))
        {
            result.Add(new PlanWarning("SpatialGuess", WarningSeverity.Info, "Cardinality for a spatial predicate was guessed."));
        }

        if (warnings.Bool("FullUpdateForOnlineIndexBuild"))
        {
            result.Add(new PlanWarning("FullUpdateForOnlineIndexBuild", WarningSeverity.Info, null));
        }

        foreach (var w in warnings.Elements())
        {
            var name = w.Name.LocalName;
            result.Add(name switch
            {
                "SpillToTempDb" => new PlanWarning(
                    name,
                    WarningSeverity.Critical,
                    $"Spill level {w.Attr("SpillLevel") ?? "?"}"
                    + (w.Attr("SpilledThreadCount") is { } t ? $", {t} thread(s)" : string.Empty)),

                "SortSpillDetails" or "HashSpillDetails" or "ExchangeSpillDetails" => new PlanWarning(
                    name,
                    WarningSeverity.Critical,
                    FormatSpillDetails(w)),

                "ColumnsWithNoStatistics" => new PlanWarning(
                    name,
                    WarningSeverity.Warning,
                    string.Join(", ", w.Elems("ColumnReference").Select(FormatColumnReference))),

                "PlanAffectingConvert" => new PlanWarning(
                    name,
                    WarningSeverity.Warning,
                    $"{w.Attr("ConvertIssue")}: {w.Attr("Expression")}".Trim(':', ' ')),

                "MemoryGrantWarning" => new PlanWarning(
                    name,
                    WarningSeverity.Warning,
                    $"{w.Attr("GrantWarningKind")} — granted {w.Attr("GrantedMemory")}KB, used {w.Attr("MaxUsedMemory")}KB"),

                "Wait" => new PlanWarning(
                    name,
                    WarningSeverity.Info,
                    $"{w.Attr("WaitType")} for {w.Attr("WaitTime")}ms"),

                "UnmatchedIndexes" => new PlanWarning(
                    name,
                    WarningSeverity.Info,
                    string.Join(", ", w.Descendants()
                        .Where(o => o.Name.LocalName == "Object")
                        .Select(o => o.Attr("Index").Unbracket()))),

                _ => new PlanWarning(name, WarningSeverity.Warning, null),
            });
        }

        return result;
    }

    private static string FormatSpillDetails(XElement w)
    {
        var parts = new List<string>();
        if (w.Attr("GrantedMemoryKb") is { } granted)
        {
            parts.Add($"granted {granted}KB");
        }

        if (w.Attr("UsedMemoryKb") is { } used)
        {
            parts.Add($"used {used}KB");
        }

        if (w.Attr("WritesToTempDb") is { } writes)
        {
            parts.Add($"{writes} writes to tempdb");
        }

        if (w.Attr("ReadsFromTempDb") is { } reads)
        {
            parts.Add($"{reads} reads from tempdb");
        }

        return string.Join(", ", parts);
    }

    private static List<MissingIndexSuggestion> ParseMissingIndexes(XElement queryPlan)
    {
        var container = queryPlan.Elem("MissingIndexes");
        if (container is null)
        {
            return [];
        }

        var result = new List<MissingIndexSuggestion>();
        foreach (var group in container.Elems("MissingIndexGroup"))
        {
            var impact = group.Dbl("Impact");
            foreach (var mi in group.Elems("MissingIndex"))
            {
                var equality = ColumnsFor(mi, "EQUALITY");
                var inequality = ColumnsFor(mi, "INEQUALITY");
                var included = ColumnsFor(mi, "INCLUDE");

                result.Add(new MissingIndexSuggestion
                {
                    Database = mi.Attr("Database").Unbracket(),
                    Schema = mi.Attr("Schema").Unbracket(),
                    Table = mi.Attr("Table").Unbracket(),
                    EqualityColumns = equality,
                    InequalityColumns = inequality,
                    IncludedColumns = included,
                    ImpactPercent = impact,
                    SuggestedCreateStatement = BuildCreateIndex(
                        mi.Attr("Schema").Unbracket(),
                        mi.Attr("Table").Unbracket(),
                        equality,
                        inequality,
                        included),
                });
            }
        }

        return result.OrderByDescending(r => r.ImpactPercent).ToList();

        static List<string> ColumnsFor(XElement missingIndex, string usage) =>
            missingIndex.Elems("ColumnGroup")
                .Where(g => string.Equals(g.Attr("Usage"), usage, StringComparison.OrdinalIgnoreCase))
                .SelectMany(g => g.Elems("Column"))
                .Select(c => c.Attr("Name").Unbracket())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
    }

    /// <summary>
    /// Showplan gives the columns but not the DDL, so the CREATE INDEX is generated here
    /// (TDD §7). Key order is equality-then-inequality, which is what the optimizer wants.
    /// </summary>
    private static string BuildCreateIndex(
        string schema,
        string table,
        IReadOnlyList<string> equality,
        IReadOnlyList<string> inequality,
        IReadOnlyList<string> included)
    {
        var keys = equality.Concat(inequality).ToList();
        var nameParts = keys.Concat(included.Take(2)).Take(4);
        var indexName = $"IX_{table}_{string.Join("_", nameParts)}";
        if (indexName.Length > 120)
        {
            indexName = indexName[..120];
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE NONCLUSTERED INDEX [{indexName}]");
        sb.Append($"ON [{schema}].[{table}] (");
        sb.Append(string.Join(", ", keys.Select(k => $"[{k}]")));
        sb.Append(')');

        if (included.Count > 0)
        {
            sb.AppendLine();
            sb.Append("INCLUDE (");
            sb.Append(string.Join(", ", included.Select(c => $"[{c}]")));
            sb.Append(')');
        }

        sb.Append(';');
        return sb.ToString();
    }
}
