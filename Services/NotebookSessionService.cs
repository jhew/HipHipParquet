using System.Data;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using HipHipParquet.Models;
using Microsoft.Extensions.Logging;

namespace HipHipParquet.Services;

public sealed class NotebookSessionService : IDisposable
{
    private readonly ILogger<NotebookSessionService> _logger;
    private readonly Dictionary<string, NotebookSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _ownedScopes = [];
    private DuckDBConnection? _connection;
    private bool _excelExtensionAttempted;
    private bool _disposed;

    public NotebookSessionService(ILogger<NotebookSessionService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<NotebookSource> GetSources()
        => _sources.Values
            .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool TryGetSource(string alias, out NotebookSource? source)
        => _sources.TryGetValue(alias, out source);

    public async Task<NotebookSource> RegisterFileSourceAsync(
        IReadOnlyList<string> filePaths,
        CsvImportOptions? csvOptions = null,
        JsonImportOptions? jsonOptions = null,
        string? preferredAlias = null,
        IReadOnlyList<NotebookColumnSchema>? knownColumns = null,
        long? knownRowCount = null,
        SupportedFileFormat? knownFormat = null)
    {
        if (filePaths == null || filePaths.Count == 0)
            throw new ArgumentException("At least one file path is required.", nameof(filePaths));

        var connection = await EnsureConnectionAsync();

        var alias = CreateUniqueAlias(preferredAlias ?? Path.GetFileNameWithoutExtension(filePaths[0]));
        var displayName = filePaths.Count == 1
            ? Path.GetFileName(filePaths[0])
            : $"{filePaths.Count:N0} parquet files";

        var format = knownFormat ?? FileFormatDetector.DetectFormat(filePaths[0]);
        string readerExpr;

        if (filePaths.Count == 1)
        {
            var transcodeScope = FileFormatDetector.PrepareFilePath(filePaths[0], csvOptions);
            _ownedScopes.Add(transcodeScope);

            if (format == SupportedFileFormat.Excel)
                await EnsureExcelExtensionAsync(connection);

            readerExpr = FileFormatDetector.GetDuckDbReaderExpression(
                transcodeScope.FilePath,
                format,
                csvOptions,
                jsonOptions);
        }
        else
        {
            if (filePaths.Any(path => FileFormatDetector.DetectFormat(path) != SupportedFileFormat.Parquet))
                throw new InvalidOperationException("Notebook source registration currently supports multi-file parquet sources only.");

            readerExpr = BuildParquetReaderExpression(filePaths);
        }

        await ExecuteNonQueryAsync(
            connection,
            $"CREATE OR REPLACE TEMP VIEW {ParquetService.EscapeDuckDbIdentifier(alias)} AS SELECT * FROM {readerExpr}");

        var columns = knownColumns?.ToList() ?? await DescribeSourceAsync(alias);
        var rowCount = knownRowCount ?? await GetSourceRowCountAsync(alias);

        var source = new NotebookSource
        {
            Alias = alias,
            DisplayName = displayName,
            Kind = NotebookSourceKind.File,
            Format = format,
            FilePath = filePaths.Count == 1 ? filePaths[0] : null,
            FilePaths = filePaths.ToList(),
            CsvOptions = csvOptions,
            JsonOptions = jsonOptions,
            RowCount = rowCount,
            Columns = columns
        };

        _sources[alias] = source;
        return source;
    }

    public async Task<NotebookSource> ExecuteReadOnlyQueryAsync(string sql, string? preferredAlias = null)
    {
        var normalizedSql = NormalizeReadOnlyQuery(sql);
        var connection = await EnsureConnectionAsync();
        var alias = CreateUniqueAlias(preferredAlias ?? "query_result");
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(alias);

        await ExecuteNonQueryAsync(
            connection,
            $"CREATE OR REPLACE TEMP TABLE {escapedAlias} AS {normalizedSql}");

        var columns = await DescribeSourceAsync(alias);
        var rowCount = await GetSourceRowCountAsync(alias);
        var source = new NotebookSource
        {
            Alias = alias,
            DisplayName = alias,
            Kind = NotebookSourceKind.QueryResult,
            RowCount = rowCount,
            Columns = columns,
            QuerySql = normalizedSql
        };

        _sources[alias] = source;
        return source;
    }

    public async Task<DataTable> GetPreviewPageAsync(
        string sourceAlias,
        GridQueryState queryState,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var sql = BuildSelectSql(escapedAlias, queryState, includePaging: true);
        var dataTable = await ExecuteDataTableAsync(connection, sql, cancellationToken);

        var countSql = $"SELECT COUNT(*) FROM {escapedAlias}{BuildSqlWhereClause(queryState)}";
        queryState.FilteredRowCount = await ExecuteScalarLongAsync(connection, countSql, cancellationToken);
        return dataTable;
    }

    public async Task<DataTable> MaterializeSourceAsync(
        string sourceAlias,
        GridQueryState? queryState = null,
        int? rowLimit = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var sql = BuildSelectSql(escapedAlias, queryState, includePaging: false, rowLimit: rowLimit);
        return await ExecuteDataTableAsync(connection, sql, cancellationToken);
    }

    public async Task ExportSourceAsync(
        string sourceAlias,
        string filePath,
        GridQueryState? queryState = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var format = FileFormatDetector.DetectFormat(filePath);
        var exportFormat = FileFormatDetector.GetDuckDbExportFormat(format);
        var exportOptions = FileFormatDetector.GetDuckDbExportOptions(format);
        var escapedPath = filePath.Replace("\\", "/").Replace("'", "''");
        var selectSql = BuildSelectSql(escapedAlias, queryState, includePaging: false);
        var copySql = $"COPY ({selectSql}) TO '{escapedPath}' (FORMAT {exportFormat}{exportOptions})";

        using var command = new DuckDBCommand(copySql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(List<string> Values, int TotalDistinct, bool Truncated)> GetDistinctValuesAsync(
        string sourceAlias,
        string columnName,
        int maxValues = 500,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var escapedColumn = ParquetService.EscapeDuckDbIdentifier(columnName);

        var nullSql = $"SELECT COUNT(*) FROM {escapedAlias} WHERE {escapedColumn} IS NULL OR TRIM(CAST({escapedColumn} AS VARCHAR)) = ''";
        var hasNulls = await ExecuteScalarLongAsync(connection, nullSql, cancellationToken) > 0;

        var countSql = $"SELECT COUNT(DISTINCT CAST({escapedColumn} AS VARCHAR)) FROM {escapedAlias} WHERE {escapedColumn} IS NOT NULL AND TRIM(CAST({escapedColumn} AS VARCHAR)) <> ''";
        var nonNullDistinct = await ExecuteScalarLongAsync(connection, countSql, cancellationToken);
        var valueLimit = hasNulls ? Math.Max(1, maxValues - 1) : maxValues;

        var valuesSql = $"SELECT DISTINCT CAST({escapedColumn} AS VARCHAR) AS value_text FROM {escapedAlias} WHERE {escapedColumn} IS NOT NULL AND TRIM(CAST({escapedColumn} AS VARCHAR)) <> '' ORDER BY 1 LIMIT {valueLimit}";
        using var valuesCommand = new DuckDBCommand(valuesSql, connection);
        using var reader = await valuesCommand.ExecuteReaderAsync(cancellationToken);

        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
                values.Add(reader.GetString(0));
        }

        if (hasNulls)
            values.Insert(0, "(Blank)");

        var totalDistinctLong = nonNullDistinct + (hasNulls ? 1L : 0L);
        var totalDistinct = totalDistinctLong > int.MaxValue ? int.MaxValue : (int)totalDistinctLong;
        var truncated = values.Count >= maxValues && totalDistinct > maxValues;
        return (values, totalDistinct, truncated);
    }

    public async Task<long> GetFilteredRowCountAsync(
        string sourceAlias,
        GridQueryState? queryState = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var sql = $"SELECT COUNT(*) FROM {escapedAlias}{BuildSqlWhereClause(queryState)}";
        return await ExecuteScalarLongAsync(connection, sql, cancellationToken);
    }

    public async Task<NotebookValidationResult> RunNullEmptyCheckAsync(
        string sourceAlias,
        IReadOnlyList<string>? columnNames = null,
        CancellationToken cancellationToken = default)
    {
        var source = GetSourceOrThrow(sourceAlias);
        var columns = ResolveTargetColumns(source, columnNames);
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var findings = new List<NotebookValidationFinding>();

        foreach (var column in columns)
        {
            var escapedColumn = ParquetService.EscapeDuckDbIdentifier(column.Name);
            var sql = $"SELECT COUNT(*) FROM {escapedAlias} WHERE {escapedColumn} IS NULL OR TRIM(CAST({escapedColumn} AS VARCHAR)) = ''";
            var count = await ExecuteScalarLongAsync(connection, sql, cancellationToken);
            if (count <= 0)
                continue;

            findings.Add(new NotebookValidationFinding
            {
                Severity = NotebookValidationSeverity.Warning,
                CheckType = "Null/Empty",
                ColumnName = column.Name,
                AffectedRows = count,
                Message = $"{column.Name} has {count:N0} null or empty value(s)."
            });
        }

        var summary = findings.Count == 0
            ? "No null or empty values were found in the checked columns."
            : $"{findings.Count:N0} column(s) contain null or empty values.";

        return new NotebookValidationResult
        {
            Title = "Null / Empty Check",
            Summary = summary,
            Findings = findings
        };
    }

    public async Task<NotebookValidationResult> RunDuplicateCheckAsync(
        string sourceAlias,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken = default)
    {
        if (keyColumns == null || keyColumns.Count == 0)
            throw new ArgumentException("At least one key column is required.", nameof(keyColumns));

        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var groupBy = string.Join(", ", keyColumns.Select(ParquetService.EscapeDuckDbIdentifier));
        var duplicateSql = $"""
            SELECT COALESCE(SUM(group_count) - COUNT(*), 0)
            FROM (
                SELECT COUNT(*) AS group_count
                FROM {escapedAlias}
                GROUP BY {groupBy}
                HAVING COUNT(*) > 1
            ) duplicate_groups
            """;

        var duplicateRows = await ExecuteScalarLongAsync(connection, duplicateSql, cancellationToken);
        var findings = new List<NotebookValidationFinding>();

        if (duplicateRows > 0)
        {
            findings.Add(new NotebookValidationFinding
            {
                Severity = NotebookValidationSeverity.Warning,
                CheckType = "Duplicate Keys",
                AffectedRows = duplicateRows,
                Message = $"{duplicateRows:N0} duplicate row(s) were found for key(s): {string.Join(", ", keyColumns)}."
            });
        }

        return new NotebookValidationResult
        {
            Title = "Duplicate Check",
            Summary = duplicateRows == 0
                ? $"No duplicate rows were found for {string.Join(", ", keyColumns)}."
                : $"{duplicateRows:N0} duplicate row(s) were found.",
            Findings = findings
        };
    }

    public async Task<NotebookValidationResult> RunRegexCheckAsync(
        string sourceAlias,
        string columnName,
        string pattern,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            throw new ArgumentException("A column name is required.", nameof(columnName));

        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("A regex pattern is required.", nameof(pattern));

        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var escapedColumn = ParquetService.EscapeDuckDbIdentifier(columnName);
        var escapedPattern = pattern.Replace("'", "''");
        var sql = $"""
            SELECT COUNT(*)
            FROM {escapedAlias}
            WHERE {escapedColumn} IS NOT NULL
              AND TRIM(CAST({escapedColumn} AS VARCHAR)) <> ''
              AND NOT regexp_matches(CAST({escapedColumn} AS VARCHAR), '{escapedPattern}')
            """;

        var invalidCount = await ExecuteScalarLongAsync(connection, sql, cancellationToken);
        var findings = new List<NotebookValidationFinding>();

        if (invalidCount > 0)
        {
            findings.Add(new NotebookValidationFinding
            {
                Severity = NotebookValidationSeverity.Warning,
                CheckType = "Regex",
                ColumnName = columnName,
                AffectedRows = invalidCount,
                Message = $"{invalidCount:N0} value(s) in {columnName} do not match /{pattern}/."
            });
        }

        return new NotebookValidationResult
        {
            Title = "Regex Check",
            Summary = invalidCount == 0
                ? $"All non-blank values in {columnName} matched /{pattern}/."
                : $"{invalidCount:N0} value(s) failed the regex check.",
            Findings = findings
        };
    }

    public Task<NotebookValidationResult> ValidateSchemaTemplateAsync(
        string sourceAlias,
        SchemaTemplate template,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var source = GetSourceOrThrow(sourceAlias);
        var findings = new List<NotebookValidationFinding>();
        var sourceColumns = source.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var templateColumn in template.Columns)
        {
            if (!sourceColumns.TryGetValue(templateColumn.Name, out var sourceColumn))
            {
                findings.Add(new NotebookValidationFinding
                {
                    Severity = templateColumn.Required ? NotebookValidationSeverity.Error : NotebookValidationSeverity.Warning,
                    CheckType = "Schema",
                    ColumnName = templateColumn.Name,
                    Message = $"{templateColumn.Name} is missing from the active source."
                });
                continue;
            }

            if (!string.Equals(NormalizeDuckDbType(sourceColumn.Type), NormalizeDuckDbType(templateColumn.Type), StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new NotebookValidationFinding
                {
                    Severity = NotebookValidationSeverity.Warning,
                    CheckType = "Schema",
                    ColumnName = templateColumn.Name,
                    Message = $"{templateColumn.Name} is {sourceColumn.Type}, expected {templateColumn.Type}."
                });
            }

            if (!templateColumn.Nullable && sourceColumn.Nullable)
            {
                findings.Add(new NotebookValidationFinding
                {
                    Severity = NotebookValidationSeverity.Warning,
                    CheckType = "Schema",
                    ColumnName = templateColumn.Name,
                    Message = $"{templateColumn.Name} is nullable, but template expects a non-nullable column."
                });
            }
        }

        var unexpectedColumns = source.Columns
            .Where(column => !template.Columns.Any(templateColumn => string.Equals(templateColumn.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(column => column.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var unexpectedColumn in unexpectedColumns)
        {
            findings.Add(new NotebookValidationFinding
            {
                Severity = NotebookValidationSeverity.Info,
                CheckType = "Schema",
                ColumnName = unexpectedColumn,
                Message = $"{unexpectedColumn} exists in the source but is not part of the template."
            });
        }

        var result = new NotebookValidationResult
        {
            Title = $"Schema Check: {template.Name}",
            Summary = findings.Count == 0
                ? $"The active source matches the '{template.Name}' template."
                : $"Schema validation found {findings.Count:N0} issue(s) against '{template.Name}'.",
            Findings = findings
        };

        return Task.FromResult(result);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var scope in _ownedScopes)
            scope.Dispose();

        _ownedScopes.Clear();
        _connection?.Dispose();
        _connection = null;
        _disposed = true;
    }

    private NotebookSource GetSourceOrThrow(string sourceAlias)
    {
        if (!_sources.TryGetValue(sourceAlias, out var source))
            throw new InvalidOperationException($"Notebook source '{sourceAlias}' was not found.");

        return source;
    }

    private IReadOnlyList<NotebookColumnSchema> ResolveTargetColumns(NotebookSource source, IReadOnlyList<string>? columnNames)
    {
        if (columnNames == null || columnNames.Count == 0)
            return source.Columns;

        return source.Columns
            .Where(column => columnNames.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<DuckDBConnection> EnsureConnectionAsync()
    {
        if (_connection == null)
        {
            _connection = new DuckDBConnection("DataSource=:memory:");
            await _connection.OpenAsync();
        }
        else if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync();
        }

        return _connection;
    }

    private async Task EnsureExcelExtensionAsync(DuckDBConnection connection)
    {
        if (_excelExtensionAttempted)
            return;

        _excelExtensionAttempted = true;
        try
        {
            using var configCmd = new DuckDBCommand("SET allow_unsigned_extensions = false;", connection);
            await configCmd.ExecuteNonQueryAsync();

            using var installCmd = new DuckDBCommand("INSTALL spatial; LOAD spatial;", connection);
            await installCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load DuckDB spatial extension for Excel-backed notebook sources.");
        }
    }

    private async Task<List<NotebookColumnSchema>> DescribeSourceAsync(string sourceAlias)
    {
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        var sql = $"DESCRIBE SELECT * FROM {escapedAlias}";
        using var command = new DuckDBCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync();

        var columns = new List<NotebookColumnSchema>();
        while (await reader.ReadAsync())
        {
            columns.Add(new NotebookColumnSchema
            {
                Name = reader.GetString("column_name"),
                Type = reader.GetString("column_type"),
                Nullable = string.Equals(reader.GetString("null"), "YES", StringComparison.OrdinalIgnoreCase)
            });
        }

        return columns;
    }

    private async Task<long> GetSourceRowCountAsync(string sourceAlias)
    {
        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(sourceAlias);
        return await ExecuteScalarLongAsync(connection, $"SELECT COUNT(*) FROM {escapedAlias}");
    }

    private static string BuildParquetReaderExpression(IEnumerable<string> filePaths)
    {
        static string EscapePath(string path) => path.Replace("\\", "/").Replace("'", "''");
        var pathList = string.Join(", ", filePaths.Select(path => $"'{EscapePath(path)}'"));
        return $"read_parquet([{pathList}])";
    }

    private static string NormalizeReadOnlyQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("Enter a SELECT query first.");

        var trimmed = sql.Trim();
        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();

        if (trimmed.Contains(';'))
            throw new InvalidOperationException("Run a single SELECT statement at a time.");

        if (!Regex.IsMatch(trimmed, @"^\s*(select|with)\b", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Notebook queries are limited to SELECT or WITH statements in this phase.");

        return trimmed;
    }

    private static string BuildSelectSql(
        string escapedAlias,
        GridQueryState? queryState,
        bool includePaging,
        int? rowLimit = null)
    {
        var whereClause = BuildSqlWhereClause(queryState);
        var orderClause = BuildOrderClause(queryState);
        var limitClause = string.Empty;

        if (includePaging && queryState != null)
        {
            limitClause = $" LIMIT {queryState.RowLimit} OFFSET {queryState.RowOffset}";
        }
        else if (rowLimit.HasValue)
        {
            limitClause = $" LIMIT {rowLimit.Value}";
        }

        return $"SELECT * FROM {escapedAlias}{whereClause}{orderClause}{limitClause}";
    }

    private static string BuildOrderClause(GridQueryState? queryState)
    {
        if (queryState == null || string.IsNullOrWhiteSpace(queryState.Sort))
            return string.Empty;

        var sortParts = queryState.Sort.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (sortParts.Length == 0)
            return string.Empty;

        var sortColumn = sortParts[0];
        var sortDirection = sortParts.Length > 1 && sortParts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase)
            ? "DESC"
            : "ASC";

        if (queryState.AvailableColumns.Count > 0 &&
            !queryState.AvailableColumns.Contains(sortColumn, StringComparer.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $" ORDER BY {ParquetService.EscapeDuckDbIdentifier(sortColumn)} {sortDirection}";
    }

    private static string BuildSqlWhereClause(GridQueryState? queryState)
    {
        if (queryState == null)
            return string.Empty;

        var conditions = new List<string>();

        foreach (var kvp in queryState.ColumnFilters)
        {
            var escapedColId = ParquetService.EscapeDuckDbIdentifier(kvp.Key);
            var selectedValues = kvp.Value;

            if (selectedValues.Count == 0)
            {
                conditions.Add("1=0");
                continue;
            }

            var nonBlankValues = selectedValues.Where(value => value != "(Blank)").ToList();
            var includeBlank = selectedValues.Contains("(Blank)");
            var columnConditions = new List<string>();

            if (nonBlankValues.Count > 0)
            {
                var escapedValues = string.Join(", ", nonBlankValues.Select(value => $"'{value.Replace("'", "''")}'"));
                columnConditions.Add($"CAST({escapedColId} AS VARCHAR) IN ({escapedValues})");
            }

            if (includeBlank)
                columnConditions.Add($"({escapedColId} IS NULL OR TRIM(CAST({escapedColId} AS VARCHAR)) = '')");

            if (columnConditions.Count > 0)
                conditions.Add($"({string.Join(" OR ", columnConditions)})");
        }

        if (!string.IsNullOrWhiteSpace(queryState.GlobalSearch))
        {
            var escapedSearch = $"%{queryState.GlobalSearch.Replace("'", "''")}%";
            var searchConditions = queryState.AvailableColumns
                .Select(column => $"CAST({ParquetService.EscapeDuckDbIdentifier(column)} AS VARCHAR) LIKE '{escapedSearch}'")
                .ToList();

            if (searchConditions.Count > 0)
                conditions.Add($"({string.Join(" OR ", searchConditions)})");
        }

        return conditions.Count > 0
            ? $" WHERE {string.Join(" AND ", conditions)}"
            : string.Empty;
    }

    private static string NormalizeDuckDbType(string type)
        => type.Trim().ToUpperInvariant();

    private static async Task ExecuteNonQueryAsync(DuckDBConnection connection, string sql)
    {
        using var command = new DuckDBCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarLongAsync(
        DuckDBConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        using var command = new DuckDBCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            null => 0,
            DBNull => 0,
            long value => value,
            int value => value,
            short value => value,
            byte value => value,
            BigInteger value => (long)value,
            _ => Convert.ToInt64(result)
        };
    }

    private static async Task<DataTable> ExecuteDataTableAsync(
        DuckDBConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        using var command = new DuckDBCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var dataTable = new DataTable();
        for (int i = 0; i < reader.FieldCount; i++)
            dataTable.Columns.Add(reader.GetName(i), reader.GetFieldType(i));

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = dataTable.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            dataTable.Rows.Add(row);
        }

        return dataTable;
    }

    private string CreateUniqueAlias(string candidate)
    {
        var sanitized = SanitizeAlias(candidate);
        var alias = sanitized;
        var suffix = 2;

        while (_sources.ContainsKey(alias))
        {
            alias = $"{sanitized}_{suffix}";
            suffix++;
        }

        return alias;
    }

    private static string SanitizeAlias(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return "source";

        var cleaned = new string(candidate
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "source";

        if (!char.IsLetter(cleaned[0]) && cleaned[0] != '_')
            cleaned = $"src_{cleaned}";

        while (cleaned.Contains("__", StringComparison.Ordinal))
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);

        return cleaned;
    }
}
