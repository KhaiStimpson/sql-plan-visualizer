namespace SqlPlanViz.ViewModels;

/// <summary>
/// Common T-SQL parameter types offered as suggestions by the parameter strip's type field
/// (Phase 2 of docs/editor-and-parameters-ux-plan.md). Typing anything else — a length or
/// precision the list doesn't spell out, e.g. <c>nvarchar(50)</c> — still works; this is a
/// pick-list for the common case, not a validator.
/// </summary>
public static class ParameterTypeCatalog
{
    public static IReadOnlyList<string> CommonTypes { get; } =
    [
        "int",
        "bigint",
        "smallint",
        "tinyint",
        "bit",
        "decimal(18,2)",
        "numeric(18,2)",
        "float",
        "money",
        "nvarchar(50)",
        "varchar(50)",
        "nchar(10)",
        "char(10)",
        "datetime2",
        "datetime",
        "date",
        "time",
        "datetimeoffset",
        "uniqueidentifier",
        "varbinary(max)",
        "binary(16)",
    ];
}
