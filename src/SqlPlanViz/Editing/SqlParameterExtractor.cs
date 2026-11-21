using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlPlanViz.Model;

namespace SqlPlanViz.Editing;

/// <summary>Where a required parameter's type came from, which decides how much to trust it.</summary>
public enum ParameterTypeSource
{
    /// <summary>The plan's own ParameterList — authoritative, this is the type it compiled for.</summary>
    Plan,

    /// <summary>Inferred from what the batch compares the variable to.</summary>
    Inferred,

    /// <summary>Nothing to go on. The type is a guess and the UI leaves it editable.</summary>
    Default,
}

/// <summary>A variable the batch uses but does not declare — something the user has to supply.</summary>
public sealed class RequiredParameter
{
    /// <summary>Includes the leading '@', as it appears in the batch.</summary>
    public string Name { get; init; } = string.Empty;

    public string DataType { get; init; } = "nvarchar(100)";

    public ParameterTypeSource TypeSource { get; init; } = ParameterTypeSource.Default;

    /// <summary>True when the batch uses the variable as a table, so it needs a table type and rows.</summary>
    public bool IsTableValued { get; init; }

    /// <summary>Value the plan was compiled for, already unwrapped from Showplan's "(1)" form.</summary>
    public string? PlanCompiledValue { get; init; }

    public string? PlanRuntimeValue { get; init; }

    /// <summary>First offset in the batch the variable is mentioned at, for stable ordering.</summary>
    public int FirstOffset { get; init; }
}

/// <summary>
/// Works out which parameters an edited batch needs (live-plan-editor-plan.md Phase 3).
///
/// Every <c>VariableReference</c> in the batch, minus everything the batch declares for
/// itself — <c>DECLARE</c>, table variables, and the parameters of a procedure or function
/// the batch defines. What is left is what the user must supply before the batch will compile.
///
/// Types come from the plan's ParameterList first, because that is the type SQL Server
/// actually compiled for; then from what the AST compares the variable to; and failing both,
/// a default the UI leaves editable rather than pretending to know.
/// </summary>
public static class SqlParameterExtractor
{
    public static IReadOnlyList<RequiredParameter> Extract(
        string sql,
        PlanStatement? statement = null,
        SqlParserVersion? parserVersion = null)
    {
        var planTypes = PlanParameters(statement);
        var fragment = TSqlParserFactory.TryParse(sql, out _, parserVersion);

        if (fragment is null)
        {
            // A batch mid-edit does not parse, and the parameter strip going blank every time
            // you open a bracket would be worse than a slightly rough list.
            return FromTokens(sql, planTypes, parserVersion);
        }

        var visitor = new ParameterVisitor();
        fragment.Accept(visitor);

        var result = new List<RequiredParameter>();
        foreach (var (name, use) in visitor.Uses)
        {
            if (visitor.Declared.Contains(name))
            {
                continue;
            }

            planTypes.TryGetValue(name, out var planParameter);

            var (dataType, source) = planParameter?.DataType is { Length: > 0 } planType
                ? (planType, ParameterTypeSource.Plan)
                : use.InferredType is { Length: > 0 } inferred
                    ? (inferred, ParameterTypeSource.Inferred)
                    : (use.IsTableValued ? "dbo.TableType" : "nvarchar(100)", ParameterTypeSource.Default);

            result.Add(new RequiredParameter
            {
                Name = name,
                DataType = dataType,
                TypeSource = source,
                IsTableValued = use.IsTableValued,
                PlanCompiledValue = Unwrap(planParameter?.CompiledValue),
                PlanRuntimeValue = Unwrap(planParameter?.RuntimeValue),
                FirstOffset = use.FirstOffset,
            });
        }

        return [.. result.OrderBy(p => p.FirstOffset)];
    }

    private static Dictionary<string, ParameterInfo> PlanParameters(PlanStatement? statement)
    {
        var result = new Dictionary<string, ParameterInfo>(StringComparer.OrdinalIgnoreCase);
        if (statement is null)
        {
            return result;
        }

        foreach (var parameter in statement.Summary.Parameters)
        {
            if (!string.IsNullOrEmpty(parameter.Name))
            {
                result[parameter.Name] = parameter;
            }
        }

        return result;
    }

    /// <summary>
    /// Showplan wraps scalar values in parentheses — <c>(1)</c>, <c>('abc')</c>, <c>(NULL)</c>
    /// — and quotes strings. Both come off here so the value can go straight into an editor
    /// field, and the composer can quote it again correctly for its declared type.
    /// </summary>
    public static string? Unwrap(string? showplanValue)
    {
        if (string.IsNullOrWhiteSpace(showplanValue))
        {
            return null;
        }

        var text = showplanValue.Trim();
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')')
        {
            text = text[1..^1].Trim();
        }

        if (string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            text = text[1..^1].Replace("''", "'");
        }

        return text;
    }

    private sealed class VariableUse
    {
        public int FirstOffset { get; set; } = int.MaxValue;

        public bool IsTableValued { get; set; }

        public string? InferredType { get; set; }
    }

    private sealed class ParameterVisitor : TSqlFragmentVisitor
    {
        public Dictionary<string, VariableUse> Uses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Declared { get; } = new(StringComparer.OrdinalIgnoreCase);

        private VariableUse Use(string name, int offset)
        {
            if (!Uses.TryGetValue(name, out var use))
            {
                use = new VariableUse();
                Uses[name] = use;
            }

            use.FirstOffset = Math.Min(use.FirstOffset, offset);
            return use;
        }

        public override void Visit(VariableReference node)
        {
            if (!string.IsNullOrEmpty(node.Name))
            {
                Use(node.Name, node.StartOffset);
            }
        }

        public override void Visit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                if (declaration.VariableName?.Value is { Length: > 0 } name)
                {
                    Declared.Add(name);
                }
            }
        }

        public override void Visit(DeclareTableVariableStatement node)
        {
            if (node.Body?.VariableName?.Value is { Length: > 0 } name)
            {
                Declared.Add(name);
            }
        }

        public override void Visit(ProcedureParameter node)
        {
            if (node.VariableName?.Value is { Length: > 0 } name)
            {
                Declared.Add(name);
            }
        }

        /// <summary>A cursor variable is declared by its own statement shape, not by DECLARE.</summary>
        public override void Visit(DeclareCursorStatement node)
        {
            if (node.Name?.Value is { Length: > 0 } name && name.StartsWith('@'))
            {
                Declared.Add(name);
            }
        }

        /// <summary>Using a variable as a table is the only reliable offline signal that it is a TVP.</summary>
        public override void Visit(VariableTableReference node)
        {
            if (node.Variable?.Name is { Length: > 0 } name)
            {
                Use(name, node.StartOffset).IsTableValued = true;
            }
        }

        public override void Visit(InsertSpecification node)
        {
            if (node.Target is VariableTableReference { Variable.Name: { Length: > 0 } name })
            {
                Use(name, node.StartOffset).IsTableValued = true;
            }
        }

        // ---- Type inference from comparison context ------------------------

        public override void Visit(BooleanComparisonExpression node)
        {
            Infer(node.FirstExpression, node.SecondExpression);
            Infer(node.SecondExpression, node.FirstExpression);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            Infer(node.FirstExpression, node.SecondExpression);
            Infer(node.FirstExpression, node.ThirdExpression);
        }

        public override void Visit(LikePredicate node)
        {
            // The right-hand side of LIKE is a pattern, so a variable there is always textual.
            if (node.SecondExpression is VariableReference { Name: { Length: > 0 } name })
            {
                Use(name, node.StartOffset).InferredType ??= "nvarchar(200)";
            }

            Infer(node.SecondExpression, node.FirstExpression);
        }

        public override void Visit(InPredicate node)
        {
            foreach (var value in node.Values)
            {
                Infer(value, node.Expression);
            }
        }

        public override void Visit(AssignmentSetClause node)
        {
            if (node.Variable is not null)
            {
                Infer(node.Variable, node.NewValue);
            }
        }

        /// <summary>
        /// If <paramref name="target"/> is a bare variable, take a type from whatever it is
        /// being compared with: a literal gives one directly, and a column gives one by name
        /// — the only thing available with no catalog, and the reason the result is offered
        /// as an editable default rather than applied silently.
        /// </summary>
        private void Infer(ScalarExpression? target, ScalarExpression? other)
        {
            if (target is not VariableReference { Name: { Length: > 0 } name } || other is null)
            {
                return;
            }

            var use = Use(name, target.StartOffset);
            use.InferredType ??= TypeOf(other);
        }

        private static string? TypeOf(ScalarExpression expression) => expression switch
        {
            IntegerLiteral => "int",
            NumericLiteral => "decimal(18, 4)",
            RealLiteral => "float",
            MoneyLiteral => "money",
            BinaryLiteral => "varbinary(max)",
            StringLiteral literal => literal.IsNational
                ? $"nvarchar({Math.Max(50, RoundUp(literal.Value?.Length ?? 0))})"
                : $"varchar({Math.Max(50, RoundUp(literal.Value?.Length ?? 0))})",
            ConvertCall convert => Rendered(convert.DataType),
            CastCall cast => Rendered(cast.DataType),
            FunctionCall call => FromFunction(call),
            ColumnReferenceExpression column => FromColumnName(
                column.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value),
            _ => null,
        };

        private static int RoundUp(int length) => ((length / 50) + 1) * 50;

        private static string? Rendered(DataTypeReference? type) => type switch
        {
            SqlDataTypeReference sql => sql.SqlDataTypeOption.ToString().ToLowerInvariant()
                                        + Suffix(sql.Parameters),
            UserDataTypeReference user => string.Join(
                '.',
                user.Name?.Identifiers.Select(i => i.Value) ?? []),
            _ => null,
        };

        private static string Suffix(IList<Literal>? parameters)
        {
            if (parameters is null || parameters.Count == 0)
            {
                return string.Empty;
            }

            return "(" + string.Join(", ", parameters.Select(p =>
                p is MaxLiteral ? "max" : p.Value)) + ")";
        }

        private static string? FromFunction(FunctionCall call) =>
            call.FunctionName?.Value?.ToUpperInvariant() switch
            {
                "GETDATE" or "SYSDATETIME" or "GETUTCDATE" or "SYSUTCDATETIME" or "DATEADD" => "datetime2(7)",
                "NEWID" => "uniqueidentifier",
                "LEN" or "DATEDIFF" or "YEAR" or "MONTH" or "DAY" or "COUNT" => "int",
                _ => null,
            };

        /// <summary>
        /// Name-shape heuristics, used only when there is no plan and no literal to go on.
        /// Deliberately conservative: three suffixes that are near-universal in SQL schemas,
        /// and nothing clever.
        /// </summary>
        private static string? FromColumnName(string? column)
        {
            if (string.IsNullOrEmpty(column))
            {
                return null;
            }

            if (column.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                || column.EndsWith("Count", StringComparison.OrdinalIgnoreCase))
            {
                return "int";
            }

            if (column.EndsWith("Date", StringComparison.OrdinalIgnoreCase)
                || column.EndsWith("At", StringComparison.Ordinal)
                || column.EndsWith("On", StringComparison.Ordinal))
            {
                return "datetime2(7)";
            }

            if (column.StartsWith("Is", StringComparison.Ordinal) || column.StartsWith("Has", StringComparison.Ordinal))
            {
                return "bit";
            }

            return null;
        }
    }

    /// <summary>
    /// Fallback for a batch that does not parse: every variable token, minus every one that a
    /// DECLARE token is immediately followed by. Rougher than the AST — it cannot see
    /// procedure parameters or infer types — but it keeps the strip populated while typing.
    /// </summary>
    private static IReadOnlyList<RequiredParameter> FromTokens(
        string sql,
        Dictionary<string, ParameterInfo> planTypes,
        SqlParserVersion? parserVersion)
    {
        IList<TSqlParserToken> tokens;
        try
        {
            using var reader = new StringReader(sql);
            tokens = TSqlParserFactory.Create(parserVersion).GetTokenStream(reader, out _);
        }
        catch (Exception)
        {
            return [];
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var previousWasDeclare = false;

        foreach (var token in tokens)
        {
            switch (token.TokenType)
            {
                case TSqlTokenType.WhiteSpace:
                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                    continue;

                case TSqlTokenType.Declare:
                    previousWasDeclare = true;
                    continue;

                case TSqlTokenType.Variable when token.Text is { Length: > 1 } name:
                    if (previousWasDeclare)
                    {
                        declared.Add(name);
                    }
                    else if (!used.ContainsKey(name))
                    {
                        used[name] = token.Offset;
                    }

                    break;
            }

            // A comma continues a DECLARE list; anything else ends it.
            previousWasDeclare = previousWasDeclare && token.TokenType is TSqlTokenType.Comma
                or TSqlTokenType.Variable or TSqlTokenType.Identifier or TSqlTokenType.As
                or TSqlTokenType.Integer or TSqlTokenType.LeftParenthesis or TSqlTokenType.RightParenthesis;
        }

        return used
            .Where(pair => !declared.Contains(pair.Key))
            .OrderBy(pair => pair.Value)
            .Select(pair =>
            {
                planTypes.TryGetValue(pair.Key, out var planParameter);
                return new RequiredParameter
                {
                    Name = pair.Key,
                    DataType = planParameter?.DataType is { Length: > 0 } type ? type : "nvarchar(100)",
                    TypeSource = planParameter is null ? ParameterTypeSource.Default : ParameterTypeSource.Plan,
                    PlanCompiledValue = Unwrap(planParameter?.CompiledValue),
                    PlanRuntimeValue = Unwrap(planParameter?.RuntimeValue),
                    FirstOffset = pair.Value,
                };
            })
            .ToList();
    }
}
