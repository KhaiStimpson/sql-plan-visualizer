using Microsoft.Data.SqlClient;

namespace SqlPlanViz.Capture;

public sealed record CatalogColumn
{
    public string Name { get; init; } = string.Empty;

    public string DataType { get; init; } = string.Empty;

    public bool IsNullable { get; init; }

    public int Ordinal { get; init; }

    /// <summary>Indexes this column is a key of, for the completion item's detail text.</summary>
    public IReadOnlyList<string> KeyOfIndexes { get; init; } = [];

    public IReadOnlyList<string> IncludedInIndexes { get; init; } = [];

    public string Detail
    {
        get
        {
            var parts = new List<string> { DataType, IsNullable ? "null" : "not null" };

            if (KeyOfIndexes.Count > 0)
            {
                parts.Add($"key of {string.Join(", ", KeyOfIndexes)}");
            }
            else if (IncludedInIndexes.Count > 0)
            {
                parts.Add($"included in {string.Join(", ", IncludedInIndexes)}");
            }

            return string.Join("  ·  ", parts);
        }
    }
}

public sealed record CatalogIndexEntry
{
    public string Name { get; init; } = string.Empty;

    public bool IsUnique { get; init; }

    public bool IsClustered { get; init; }

    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    public IReadOnlyList<string> IncludedColumns { get; init; } = [];
}

public sealed record CatalogTable
{
    public string Schema { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool IsView { get; init; }

    public IReadOnlyList<CatalogColumn> Columns { get; init; } = [];

    public IReadOnlyList<CatalogIndexEntry> Indexes { get; init; } = [];

    public string QualifiedName => $"{Schema}.{Name}";
}

/// <summary>A user-defined table type, so a table-valued parameter's grid can be shaped correctly.</summary>
public sealed record CatalogTableType
{
    public string Schema { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<CatalogColumn> Columns { get; init; } = [];

    public string QualifiedName => $"{Schema}.{Name}";
}

/// <summary>Everything the completion engine knows about the connected database.</summary>
public sealed record CatalogSnapshot
{
    public string Server { get; init; } = string.Empty;

    public string Database { get; init; } = string.Empty;

    public DateTime LoadedUtc { get; init; } = DateTime.UtcNow;

    public IReadOnlyList<string> Schemas { get; init; } = [];

    public IReadOnlyList<CatalogTable> Tables { get; init; } = [];

    public IReadOnlyList<CatalogTableType> TableTypes { get; init; } = [];

    public static CatalogSnapshot Empty { get; } = new();

    public bool IsEmpty => Tables.Count == 0 && TableTypes.Count == 0;

    /// <summary>Resolves a one- or two-part name to a table, preferring an exact schema match.</summary>
    public CatalogTable? FindTable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('[', ']'))
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Count >= 2)
        {
            var qualified = Tables.FirstOrDefault(t =>
                string.Equals(t.Schema, parts[^2], StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
            if (qualified is not null)
            {
                return qualified;
            }
        }

        return Tables.FirstOrDefault(t => string.Equals(t.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
    }

    public CatalogTableType? FindTableType(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('[', ']'))
            .ToList();

        return parts.Count == 0
            ? null
            : TableTypes.FirstOrDefault(t => string.Equals(t.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Reads the connected database's schema once and caches it
/// (live-plan-editor-plan.md Phase 6).
///
/// One round trip, deliberately: a completion provider that queried per keystroke would put
/// the user's typing on the network, and a schema is small enough that reading all of it is
/// cheaper than reading part of it repeatedly. The cache is keyed by server and database and
/// is refreshed only on request, since schemas do change under a long-lived session.
/// </summary>
public sealed class CatalogMetadataService
{
    /// <summary>
    /// Four result sets in one batch. Splitting them into four commands would be four round
    /// trips for information that is only ever wanted together.
    /// </summary>
    private const string CatalogSql = """
        SET NOCOUNT ON;

        SELECT s.name AS SchemaName
        FROM   sys.schemas AS s
        WHERE  s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
        ORDER BY s.name;

        SELECT s.name AS SchemaName, o.name AS ObjectName,
               CAST(CASE WHEN o.type = 'V' THEN 1 ELSE 0 END AS bit) AS IsView
        FROM   sys.objects AS o
               INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE  o.type IN ('U', 'V') AND o.is_ms_shipped = 0
        ORDER BY s.name, o.name;

        SELECT s.name AS SchemaName, o.name AS ObjectName, c.name AS ColumnName,
               t.name AS TypeName, c.max_length AS MaxLength, c.precision AS [Precision],
               c.scale AS Scale, c.is_nullable AS IsNullable, c.column_id AS Ordinal
        FROM   sys.columns AS c
               INNER JOIN sys.objects AS o ON o.object_id = c.object_id
               INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
               INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE  o.type IN ('U', 'V') AND o.is_ms_shipped = 0
        ORDER BY s.name, o.name, c.column_id;

        SELECT s.name AS SchemaName, o.name AS ObjectName, i.name AS IndexName,
               i.is_unique AS IsUnique,
               CAST(CASE WHEN i.type = 1 THEN 1 ELSE 0 END AS bit) AS IsClustered,
               c.name AS ColumnName, ic.is_included_column AS IsIncluded,
               ic.key_ordinal AS KeyOrdinal
        FROM   sys.indexes AS i
               INNER JOIN sys.objects AS o ON o.object_id = i.object_id
               INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
               INNER JOIN sys.index_columns AS ic
                   ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               INNER JOIN sys.columns AS c
                   ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE  o.type = 'U' AND o.is_ms_shipped = 0 AND i.name IS NOT NULL
        ORDER BY s.name, o.name, i.name, ic.is_included_column, ic.key_ordinal;

        SELECT s.name AS SchemaName, tt.name AS TypeName, c.name AS ColumnName,
               bt.name AS ColumnTypeName, c.max_length AS MaxLength, c.precision AS [Precision],
               c.scale AS Scale, c.is_nullable AS IsNullable, c.column_id AS Ordinal
        FROM   sys.table_types AS tt
               INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
               INNER JOIN sys.columns AS c ON c.object_id = tt.type_table_object_id
               INNER JOIN sys.types AS bt ON bt.user_type_id = c.user_type_id
        WHERE  tt.is_user_defined = 1
        ORDER BY s.name, tt.name, c.column_id;
        """;

    private readonly Dictionary<string, CatalogSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The snapshot for these settings, reading it if it is not already cached.</summary>
    public async Task<CatalogSnapshot> GetAsync(
        ConnectionSettings settings,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Server))
        {
            return CatalogSnapshot.Empty;
        }

        var key = CacheKey(settings);
        if (!forceRefresh && _cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var snapshot = await LoadAsync(settings, cancellationToken).ConfigureAwait(false);
        _cache[key] = snapshot;
        return snapshot;
    }

    /// <summary>The cached snapshot without touching the network, or empty if there is none.</summary>
    public CatalogSnapshot Peek(ConnectionSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Server)
            ? CatalogSnapshot.Empty
            : _cache.GetValueOrDefault(CacheKey(settings), CatalogSnapshot.Empty);

    public void Invalidate(ConnectionSettings settings) => _cache.Remove(CacheKey(settings));

    private static string CacheKey(ConnectionSettings settings) => $"{settings.Server}|{settings.Database}";

    private static async Task<CatalogSnapshot> LoadAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var schemas = new List<string>();
        var tables = new Dictionary<string, TableBuilder>(StringComparer.OrdinalIgnoreCase);
        var tableTypes = new Dictionary<string, TableTypeBuilder>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var connection = new SqlConnection(PlanCaptureService.BuildConnectionString(settings));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = CatalogSql;
            command.CommandTimeout = settings.CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schemas.Add(reader.GetString(0));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var builder = new TableBuilder
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    IsView = reader.GetBoolean(2),
                };
                tables[$"{builder.Schema}.{builder.Name}"] = builder;
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var builder))
                {
                    builder.Columns.Add(new ColumnBuilder
                    {
                        Name = reader.GetString(2),
                        DataType = FormatType(reader.GetString(3), reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6)),
                        IsNullable = reader.GetBoolean(7),
                        Ordinal = reader.GetInt32(8),
                    });
                }
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var builder))
                {
                    continue;
                }

                var indexName = reader.GetString(2);
                var index = builder.Indexes.FirstOrDefault(i => i.Name == indexName);
                if (index is null)
                {
                    index = new IndexBuilder
                    {
                        Name = indexName,
                        IsUnique = reader.GetBoolean(3),
                        IsClustered = reader.GetBoolean(4),
                    };
                    builder.Indexes.Add(index);
                }

                var column = reader.GetString(5);
                if (reader.GetBoolean(6))
                {
                    index.IncludedColumns.Add(column);
                }
                else
                {
                    index.KeyColumns.Add(column);
                }
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                if (!tableTypes.TryGetValue(key, out var builder))
                {
                    builder = new TableTypeBuilder { Schema = reader.GetString(0), Name = reader.GetString(1) };
                    tableTypes[key] = builder;
                }

                builder.Columns.Add(new ColumnBuilder
                {
                    Name = reader.GetString(2),
                    DataType = FormatType(reader.GetString(3), reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6)),
                    IsNullable = reader.GetBoolean(7),
                    Ordinal = reader.GetInt32(8),
                });
            }
        }
        catch (SqlException ex)
        {
            throw new PlanCaptureException($"Could not read the database schema: {ex.Message.Trim()}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new PlanCaptureException($"Could not connect: {ex.Message}", ex);
        }

        return new CatalogSnapshot
        {
            Server = settings.Server,
            Database = settings.Database,
            Schemas = schemas,
            Tables = [.. tables.Values.Select(t => t.Build())],
            TableTypes = [.. tableTypes.Values.Select(t => t.Build())],
        };
    }

    /// <summary>
    /// Renders a type the way someone would write it. <c>max_length</c> is in bytes, so the
    /// n-types have to be halved, and -1 means max.
    /// </summary>
    private static string FormatType(string typeName, short maxLength, byte precision, byte scale)
    {
        var name = typeName.ToLowerInvariant();

        switch (name)
        {
            case "nvarchar" or "nchar":
                return maxLength == -1 ? $"{name}(max)" : $"{name}({maxLength / 2})";

            case "varchar" or "char" or "varbinary" or "binary":
                return maxLength == -1 ? $"{name}(max)" : $"{name}({maxLength})";

            case "decimal" or "numeric":
                return $"{name}({precision}, {scale})";

            case "datetime2" or "time" or "datetimeoffset":
                return $"{name}({scale})";

            default:
                return name;
        }
    }

    private sealed class ColumnBuilder
    {
        public string Name { get; init; } = string.Empty;

        public string DataType { get; init; } = string.Empty;

        public bool IsNullable { get; init; }

        public int Ordinal { get; init; }
    }

    private sealed class IndexBuilder
    {
        public string Name { get; init; } = string.Empty;

        public bool IsUnique { get; init; }

        public bool IsClustered { get; init; }

        public List<string> KeyColumns { get; } = [];

        public List<string> IncludedColumns { get; } = [];
    }

    private sealed class TableBuilder
    {
        public string Schema { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public bool IsView { get; init; }

        public List<ColumnBuilder> Columns { get; } = [];

        public List<IndexBuilder> Indexes { get; } = [];

        public CatalogTable Build() => new()
        {
            Schema = Schema,
            Name = Name,
            IsView = IsView,
            Columns =
            [
                .. Columns.OrderBy(c => c.Ordinal).Select(c => new CatalogColumn
                {
                    Name = c.Name,
                    DataType = c.DataType,
                    IsNullable = c.IsNullable,
                    Ordinal = c.Ordinal,
                    KeyOfIndexes = [.. Indexes.Where(i => i.KeyColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).Select(i => i.Name)],
                    IncludedInIndexes = [.. Indexes.Where(i => i.IncludedColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).Select(i => i.Name)],
                }),
            ],
            Indexes =
            [
                .. Indexes.Select(i => new CatalogIndexEntry
                {
                    Name = i.Name,
                    IsUnique = i.IsUnique,
                    IsClustered = i.IsClustered,
                    KeyColumns = i.KeyColumns,
                    IncludedColumns = i.IncludedColumns,
                }),
            ],
        };
    }

    private sealed class TableTypeBuilder
    {
        public string Schema { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public List<ColumnBuilder> Columns { get; } = [];

        public CatalogTableType Build() => new()
        {
            Schema = Schema,
            Name = Name,
            Columns =
            [
                .. Columns.OrderBy(c => c.Ordinal).Select(c => new CatalogColumn
                {
                    Name = c.Name,
                    DataType = c.DataType,
                    IsNullable = c.IsNullable,
                    Ordinal = c.Ordinal,
                }),
            ],
        };
    }
}
