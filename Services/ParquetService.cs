using DuckDB.NET.Data;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using HipHipParquet.Models;

namespace HipHipParquet.Services;

public class ParquetService : IDisposable
{
    private readonly ILogger<ParquetService> _logger;
    private DuckDBConnection? _connection;
    private bool _disposed = false;
    
    public ParquetService(ILogger<ParquetService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Installs and loads the DuckDB spatial extension (required for Excel/XLSX reading via st_read).
    /// </summary>
    public async Task EnsureExcelExtensionAsync(DuckDBConnection connection)
    {
        try
        {
            using var installCmd = new DuckDBCommand("INSTALL spatial; LOAD spatial;", connection);
            await installCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load spatial extension for Excel support. Attempting to continue.");
        }
    }

    /// <summary>
    /// Detects STRUCT/nested columns in a JSON file and returns a flattening SELECT expression.
    /// Returns null if no STRUCT columns are found.
    /// </summary>
    public async Task<string?> GetFlattenedQueryAsync(string filePath, CsvImportOptions? csvOptions = null, JsonImportOptions? jsonOptions = null)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            using var transcodeScope = FileFormatDetector.PrepareFilePath(filePath, csvOptions);
            var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(filePath);
            var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, csvOptions, jsonOptions);

            // Describe columns
            var describeSql = $"DESCRIBE SELECT * FROM {readerExpr}";
            using var cmd = new DuckDBCommand(describeSql, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            var flatColumns = new List<string>();
            var structColumns = new List<(string Name, string Type)>();
            bool hasStruct = false;

            while (await reader.ReadAsync())
            {
                var name = reader.GetString("column_name");
                var type = reader.GetString("column_type");

                if (type.StartsWith("STRUCT", StringComparison.OrdinalIgnoreCase))
                {
                    hasStruct = true;
                    structColumns.Add((name, type));
                    // Parse struct fields: STRUCT(field1 TYPE1, field2 TYPE2, ...)
                    var fields = ParseStructFields(type);
                    foreach (var field in fields)
                    {
                        flatColumns.Add($"\"{name}\".\"{field}\" AS \"{name}_{field}\"");
                    }
                }
                else
                {
                    flatColumns.Add($"\"{name}\"");
                }
            }

            if (!hasStruct) return null;

            return $"SELECT {string.Join(", ", flatColumns)} FROM {readerExpr}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate flattened query for {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Parses STRUCT field names from a DuckDB STRUCT type string.
    /// E.g., "STRUCT(name VARCHAR, age INTEGER)" → ["name", "age"]
    /// </summary>
    private static List<string> ParseStructFields(string structType)
    {
        var fields = new List<string>();
        // Extract content between outermost parentheses
        var start = structType.IndexOf('(');
        var end = structType.LastIndexOf(')');
        if (start < 0 || end <= start) return fields;

        var content = structType.Substring(start + 1, end - start - 1);
        
        // Split by commas at depth 0 (to handle nested structs)
        int depth = 0;
        int fieldStart = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '(') depth++;
            else if (content[i] == ')') depth--;
            else if (content[i] == ',' && depth == 0)
            {
                var field = content.Substring(fieldStart, i - fieldStart).Trim();
                var fieldName = field.Split(' ', 2)[0].Trim('"');
                if (!string.IsNullOrEmpty(fieldName))
                    fields.Add(fieldName);
                fieldStart = i + 1;
            }
        }
        // Last field
        var lastField = content.Substring(fieldStart).Trim();
        var lastFieldName = lastField.Split(' ', 2)[0].Trim('"');
        if (!string.IsNullOrEmpty(lastFieldName))
            fields.Add(lastFieldName);

        return fields;
    }

    /// <summary>
    /// Loads a file using a custom SQL query (for flattened JSON, etc.).
    /// </summary>
    public async Task<DataTable> LoadWithQueryAsync(string sql, int? rowLimit = null)
    {
        try
        {
            _connection = new DuckDBConnection("DataSource=:memory:");
            await _connection.OpenAsync();

            var limitClause = rowLimit.HasValue ? $" LIMIT {rowLimit.Value}" : "";
            var fullSql = $"SELECT * FROM ({sql}) AS flattened{limitClause}";
            _logger.LogDebug("Executing flattened SQL: {SQL}", fullSql);

            using var command = new DuckDBCommand(fullSql, _connection);
            using var reader = await command.ExecuteReaderAsync();

            var dataTable = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++)
                dataTable.Columns.Add(reader.GetName(i), reader.GetFieldType(i));

            while (await reader.ReadAsync())
            {
                var row = dataTable.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                dataTable.Rows.Add(row);
            }

            return dataTable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute flattened query");
            if (_connection != null) { _connection.Dispose(); _connection = null; }
            throw new InvalidOperationException($"Failed to load flattened data: {ex.Message}", ex);
        }
    }
    
    public async Task<DataTable> LoadFileAsync(string filePath, CsvImportOptions? csvOptions = null, int? rowLimit = null, JsonImportOptions? jsonOptions = null)
    {
        try
        {
            _connection = new DuckDBConnection("DataSource=:memory:");
            await _connection.OpenAsync();
            
            // Use DuckDB to read file (format auto-detected from extension)
            using var transcodeScope = FileFormatDetector.PrepareFilePath(filePath, csvOptions);
            var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(filePath);
            var formatName = FileFormatDetector.GetFormatDisplayName(format);
            _logger.LogInformation("Reading {Format} file: {FilePath}", formatName, filePath);

            // Load spatial extension for Excel files
            if (format == SupportedFileFormat.Excel)
                await EnsureExcelExtensionAsync(_connection);
            
            var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, csvOptions, jsonOptions);
            var limitClause = rowLimit.HasValue ? $" LIMIT {rowLimit.Value}" : "";
            var sql = $"SELECT * FROM {readerExpr}{limitClause}";
            _logger.LogDebug("Executing SQL: {SQL}", sql);
            
            using var command = new DuckDBCommand(sql, _connection);
            using var reader = await command.ExecuteReaderAsync();
            
            var dataTable = new DataTable();
            
            // Create columns from reader schema
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dataTable.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
            }
            
            // Fill data
            while (await reader.ReadAsync())
            {
                var row = dataTable.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                }
                dataTable.Rows.Add(row);
            }
            
            _logger.LogInformation("Loaded {RowCount} rows from {FilePath}", dataTable.Rows.Count, filePath);
            return dataTable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load file: {FilePath}. Error: {Error}", filePath, ex.Message);
            
            // Cleanup connection on error
            if (_connection != null)
            {
                _connection.Dispose();
                _connection = null;
            }
            
            // Extract line number from DuckDB error and build a clearer message
            var errorMessage = FormatImportError(Path.GetFileName(filePath), ex);
            throw new InvalidOperationException(errorMessage, ex);
        }
    }

    /// <summary>
    /// Loads multiple parquet files as one logical table.
    /// </summary>
    public async Task<DataTable> LoadFilesAsync(IReadOnlyList<string> filePaths, int? rowLimit = null)
    {
        if (filePaths == null || filePaths.Count == 0)
            throw new ArgumentException("At least one file path is required.", nameof(filePaths));

        if (filePaths.Any(path => FileFormatDetector.DetectFormat(path) != SupportedFileFormat.Parquet))
            throw new InvalidOperationException("Multi-file loading is only supported for parquet files.");

        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var normalizedPaths = filePaths.Select(path => path.Replace("\\", "/")).ToArray();
            var readerExpr = BuildParquetReaderExpression(normalizedPaths);
            var limitClause = rowLimit.HasValue ? $" LIMIT {rowLimit.Value}" : "";
            var sql = $"SELECT * FROM {readerExpr}{limitClause}";
            _logger.LogDebug("Executing multi-file SQL: {SQL}", sql);

            using var command = new DuckDBCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            var dataTable = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++)
                dataTable.Columns.Add(reader.GetName(i), reader.GetFieldType(i));

            while (await reader.ReadAsync())
            {
                var row = dataTable.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);

                dataTable.Rows.Add(row);
            }

            _logger.LogInformation("Loaded {RowCount} rows from {FileCount} parquet files", dataTable.Rows.Count, filePaths.Count);
            return dataTable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load parquet file set. Error: {Error}", ex.Message);
            throw new InvalidOperationException($"Failed to load parquet file set: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts line/row number references from DuckDB exceptions and formats
    /// a clear error message highlighting the problematic location in the file.
    /// </summary>
    private static string FormatImportError(string fileName, Exception ex)
    {
        var msg = ex.Message;

        // DuckDB CSV errors typically: "CSV Error on Line 42: expected 5 values ..."
        // DuckDB JSON errors: "JSON ... error at line 10 ..."
        var lineMatch = System.Text.RegularExpressions.Regex.Match(
            msg,
            @"(?:line|row)\s+(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (lineMatch.Success)
        {
            var lineNum = lineMatch.Groups[1].Value;
            return $"Error at line {lineNum} in '{fileName}': {msg}";
        }

        return $"Failed to load file '{fileName}': {msg}";
    }
    
    public async Task<DataFileInfo> GetFileInfoAsync(string filePath, CsvImportOptions? csvOptions = null, JsonImportOptions? jsonOptions = null)
    {
        try
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
                
            using var transcodeScope = FileFormatDetector.PrepareFilePath(filePath, csvOptions);
            var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(filePath);

            // Load spatial extension for Excel files
            if (format == SupportedFileFormat.Excel)
                await EnsureExcelExtensionAsync(_connection);

            var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, csvOptions, jsonOptions);
            
            // Get schema information using DuckDB reader
            _logger.LogInformation("Getting schema for file: {FilePath}", filePath);
            
            var sql = $"DESCRIBE SELECT * FROM {readerExpr}";
            _logger.LogDebug("Executing schema SQL: {SQL}", sql);
            
            using var command = new DuckDBCommand(sql, _connection);
            using var reader = await command.ExecuteReaderAsync();
            
            var columns = new List<ColumnInfo>();
            while (await reader.ReadAsync())
            {
                columns.Add(new ColumnInfo
                {
                    Name = reader.GetString("column_name"),
                    Type = reader.GetString("column_type"),
                    Nullable = reader.GetString("null") == "YES"
                });
            }
            
            // Get row count (pass csvOptions/jsonOptions so custom delimiters/skip-rows are applied)
            var rowCount = await GetRowCountAsync(normalizedPath, format, csvOptions, jsonOptions);
            
            return new DataFileInfo
            {
                FilePath = filePath,
                Format = format,
                Columns = columns,
                RowCount = rowCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file info: {FilePath}", filePath);
            throw;
        }
    }

    /// <summary>
    /// Gets schema and row count for multiple parquet files treated as one logical table.
    /// </summary>
    public async Task<DataFileInfo> GetFileInfoAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths == null || filePaths.Count == 0)
            throw new ArgumentException("At least one file path is required.", nameof(filePaths));

        if (filePaths.Any(path => FileFormatDetector.DetectFormat(path) != SupportedFileFormat.Parquet))
            throw new InvalidOperationException("Multi-file metadata is only supported for parquet files.");

        try
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

            var normalizedPaths = filePaths.Select(path => path.Replace("\\", "/")).ToArray();
            var readerExpr = BuildParquetReaderExpression(normalizedPaths);

            var describeSql = $"DESCRIBE SELECT * FROM {readerExpr}";
            using var describeCmd = new DuckDBCommand(describeSql, _connection);
            using var schemaReader = await describeCmd.ExecuteReaderAsync();

            var columns = new List<ColumnInfo>();
            while (await schemaReader.ReadAsync())
            {
                columns.Add(new ColumnInfo
                {
                    Name = schemaReader.GetString("column_name"),
                    Type = schemaReader.GetString("column_type"),
                    Nullable = schemaReader.GetString("null") == "YES"
                });
            }

            var countSql = $"SELECT COUNT(*) FROM {readerExpr}";
            using var countCmd = new DuckDBCommand(countSql, _connection);
            var countResult = await countCmd.ExecuteScalarAsync();

            return new DataFileInfo
            {
                // Use a single representative path for consumers that expect a real filesystem path.
                FilePath = filePaths[0],
                Format = SupportedFileFormat.Parquet,
                Columns = columns,
                RowCount = Convert.ToInt64(countResult),
                SourceFiles = filePaths.ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get multi-file info for parquet file set");
            throw;
        }
    }

    private static string BuildParquetReaderExpression(IEnumerable<string> normalizedPaths)
    {
        static string EscapePath(string path) => path.Replace("'", "''");

        var pathList = string.Join(", ", normalizedPaths.Select(path => $"'{EscapePath(path)}'"));
        return $"read_parquet([{pathList}])";
    }
    
    private async Task<long> GetRowCountAsync(string filePath, SupportedFileFormat format, CsvImportOptions? csvOptions = null, JsonImportOptions? jsonOptions = null)
    {
        var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(filePath, format, csvOptions, jsonOptions);
        var sql = $"SELECT COUNT(*) FROM {readerExpr}";
        using var command = new DuckDBCommand(sql, _connection!);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Gets the total row count for a file without loading any data. Uses a fresh connection.
    /// </summary>
    public async Task<long> GetTotalRowCountAsync(string filePath, CsvImportOptions? csvOptions = null, JsonImportOptions? jsonOptions = null)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();

        using var transcodeScope = FileFormatDetector.PrepareFilePath(filePath, csvOptions);
        var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
        var format = FileFormatDetector.DetectFormat(filePath);

        if (format == SupportedFileFormat.Excel)
            await EnsureExcelExtensionAsync(connection);

        var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, csvOptions, jsonOptions);
        var sql = $"SELECT COUNT(*) FROM {readerExpr}";
        using var cmd = new DuckDBCommand(sql, connection);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Gets distinct values for a column from the full dataset (not just loaded rows).
    /// Returns up to maxValues results, with total distinct count and a truncation flag.
    /// </summary>
    public async Task<(List<string> Values, int TotalDistinct, bool Truncated)> GetDistinctValuesAsync(
        string filePath,
        string columnName,
        CsvImportOptions? csvOptions = null,
        JsonImportOptions? jsonOptions = null,
        int maxValues = 500,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            using var transcodeScope = FileFormatDetector.PrepareFilePath(filePath, csvOptions);
            var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(filePath);

            if (format == SupportedFileFormat.Excel)
                await EnsureExcelExtensionAsync(connection);

            var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, csvOptions, jsonOptions);

            // Get total distinct count
            var countSql = $"SELECT COUNT(DISTINCT CAST(\"{columnName}\" AS VARCHAR)) FROM {readerExpr}";
            using var countCmd = new DuckDBCommand(countSql, connection);
            var countResult = await countCmd.ExecuteScalarAsync(cancellationToken);
            var totalDistinct = Convert.ToInt32(countResult ?? 0);

            // Get distinct values (including NULLs)
            var valuesSql = $"SELECT DISTINCT CAST(\"{columnName}\" AS VARCHAR) FROM {readerExpr} WHERE \"{columnName}\" IS NOT NULL ORDER BY 1 LIMIT {maxValues}";
            var values = new List<string>();
            using var valuesCmd = new DuckDBCommand(valuesSql, connection);
            using var reader = await valuesCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                    values.Add(reader.GetString(0));
            }

            // Check for NULL values
            var nullSql = $"SELECT COUNT(*) FROM {readerExpr} WHERE \"{columnName}\" IS NULL";
            using var nullCmd = new DuckDBCommand(nullSql, connection);
            var nullCount = Convert.ToInt64(await nullCmd.ExecuteScalarAsync(cancellationToken));
            if (nullCount > 0)
                values.Insert(0, "(Blank)");

            var truncated = values.Count >= maxValues && totalDistinct > maxValues;

            _logger.LogDebug("Distinct values for {Column}: {Count}/{Total} (truncated: {Truncated})", columnName, values.Count, totalDistinct, truncated);
            return (values, totalDistinct, truncated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get distinct values for column {Column} in {FilePath}", columnName, filePath);
            throw;
        }
    }

    /// <summary>
    /// Executes a query based on GridQueryState and returns the result as a DataTable.
    /// Supports filtering, global search, sorting, and paging.
    /// </summary>
    public async Task<DataTable> ExecuteGridQueryAsync(
        GridQueryState queryState,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            using var transcodeScope = FileFormatDetector.PrepareFilePath(queryState.SourceFilePath, queryState.CsvOptions);
            var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(queryState.SourceFilePath);

            if (format == SupportedFileFormat.Excel)
                await EnsureExcelExtensionAsync(connection);

            var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(
                normalizedPath, format, queryState.CsvOptions, queryState.JsonOptions);

            // Build WHERE clause from filters
            var conditions = new List<string>();

            // Add column-level value filters
            foreach (var kvp in queryState.ColumnFilters.Where(kv => kv.Value.Count > 0))
            {
                var columnName = kvp.Key;
                var selectedValues = kvp.Value;
                var nonBlankValues = selectedValues.Where(v => v != "(Blank)").ToList();
                var includeBlank = selectedValues.Contains("(Blank)");

                var colConditions = new List<string>();
                if (nonBlankValues.Count > 0)
                {
                    var escapedValues = string.Join(",", nonBlankValues.Select(v => $"'{v.Replace("'", "''")}'"));
                    colConditions.Add($"CAST(\"{columnName}\" AS VARCHAR) IN ({escapedValues})");
                }
                if (includeBlank)
                {
                    colConditions.Add($"\"{columnName}\" IS NULL");
                }

                if (colConditions.Count > 0)
                    conditions.Add($"({string.Join(" OR ", colConditions)})");
            }

            // Add global search filter (LIKE across all columns)
            if (!string.IsNullOrWhiteSpace(queryState.GlobalSearch))
            {
                var searchTerm = $"%{queryState.GlobalSearch.Replace("'", "''")}%";
                var searchConditions = new List<string>();
                foreach (var col in queryState.AvailableColumns)
                {
                    searchConditions.Add($"CAST(\"{col}\" AS VARCHAR) LIKE '{searchTerm}'");
                }
                if (searchConditions.Count > 0)
                    conditions.Add($"({string.Join(" OR ", searchConditions)})");
            }

            var whereClause = conditions.Count > 0 ? $" WHERE {string.Join(" AND ", conditions)}" : "";

            // Build ORDER BY clause
            var orderClause = "";
            if (!string.IsNullOrWhiteSpace(queryState.Sort))
            {
                orderClause = $" ORDER BY {queryState.Sort}";
            }

            // Build the full query with LIMIT/OFFSET for paging
            var sql = $"SELECT * FROM {readerExpr}{whereClause}{orderClause} LIMIT {queryState.RowLimit} OFFSET {queryState.RowOffset}";

            _logger.LogDebug("Executing grid query: {SQL}", sql);

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

            // Also get the total filtered count (for display purposes)
            var countSql = $"SELECT COUNT(*) FROM {readerExpr}{whereClause}";
            using var countCmd = new DuckDBCommand(countSql, connection);
            var countResult = await countCmd.ExecuteScalarAsync(cancellationToken);
            queryState.FilteredRowCount = Convert.ToInt64(countResult ?? 0);

            _logger.LogInformation("Grid query returned {RowCount} rows (filtered: {FilteredCount})", dataTable.Rows.Count, queryState.FilteredRowCount);
            return dataTable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute grid query for {FilePath}", queryState.SourceFilePath);
            throw;
        }
    }
    
    public async Task SaveFileAsync(string filePath, DataTable dataTable)
    {
        try
        {
            if (_connection == null || _connection.State != ConnectionState.Open)
            {
                _connection = new DuckDBConnection("DataSource=:memory:");
                await _connection.OpenAsync();
            }
            
            var normalizedPath = filePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(filePath);
            var formatName = FileFormatDetector.GetFormatDisplayName(format);
            _logger.LogInformation("Saving {Format} file: {FilePath}", formatName, normalizedPath);
            
            // Create a temporary table from the DataTable
            var tempTableName = "temp_" + Guid.NewGuid().ToString("N");
            
            // Build CREATE TABLE statement
            var columnDefs = new List<string>();
            foreach (DataColumn col in dataTable.Columns)
            {
                if (col.ColumnName == "__RowNumber") continue; // Skip internal row number column
                
                var duckDbType = GetDuckDbType(col.DataType);
                columnDefs.Add($"\"{col.ColumnName}\" {duckDbType}");
            }
            
            var createTableSql = $"CREATE TABLE {tempTableName} ({string.Join(", ", columnDefs)})";
            _logger.LogDebug("Creating temp table: {SQL}", createTableSql);
            
            using (var createCommand = new DuckDBCommand(createTableSql, _connection))
            {
                await createCommand.ExecuteNonQueryAsync();
            }
            
            // Insert data using parameterized queries (SQL injection-safe, batched in a transaction)
            var dataCols = dataTable.Columns.Cast<DataColumn>()
                .Where(c => c.ColumnName != "__RowNumber").ToList();
            var placeholders = string.Join(", ", Enumerable.Range(1, dataCols.Count).Select(i => $"${i}"));
            var insertSql = $"INSERT INTO {tempTableName} VALUES ({placeholders})";

            using (var beginTx = new DuckDBCommand("BEGIN TRANSACTION", _connection))
                await beginTx.ExecuteNonQueryAsync();

            try
            {
                using (var insertCommand = new DuckDBCommand(insertSql, _connection))
                {
                    for (int i = 0; i < dataCols.Count; i++)
                        insertCommand.Parameters.Add(new DuckDBParameter());

                    foreach (DataRow row in dataTable.Rows)
                    {
                        for (int i = 0; i < dataCols.Count; i++)
                        {
                            var value = row[dataCols[i]];
                            insertCommand.Parameters[i].Value = (value == DBNull.Value) ? (object)DBNull.Value : value;
                        }
                        await insertCommand.ExecuteNonQueryAsync();
                    }
                }

                using (var commitTx = new DuckDBCommand("COMMIT", _connection))
                    await commitTx.ExecuteNonQueryAsync();
            }
            catch
            {
                using (var rollbackTx = new DuckDBCommand("ROLLBACK", _connection))
                    try { await rollbackTx.ExecuteNonQueryAsync(); } catch { /* best-effort rollback */ }
                throw;
            }
            
            // Export to target format
            var exportFormat = FileFormatDetector.GetDuckDbExportFormat(format);
            var exportOptions = FileFormatDetector.GetDuckDbExportOptions(format);
            var escapedExportPath = normalizedPath.Replace("'", "''");
            var exportSql = $"COPY {tempTableName} TO '{escapedExportPath}' (FORMAT {exportFormat}{exportOptions})";
            _logger.LogDebug("Exporting to {Format}: {SQL}", formatName, exportSql);
            
            using (var exportCommand = new DuckDBCommand(exportSql, _connection))
            {
                await exportCommand.ExecuteNonQueryAsync();
            }
            
            // Clean up temporary table
            var dropSql = $"DROP TABLE {tempTableName}";
            using (var dropCommand = new DuckDBCommand(dropSql, _connection))
            {
                await dropCommand.ExecuteNonQueryAsync();
            }
            
            _logger.LogInformation("Successfully saved {RowCount} rows to {FilePath}", dataTable.Rows.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file: {FilePath}", filePath);
            throw new InvalidOperationException($"Failed to save file '{Path.GetFileName(filePath)}': {ex.Message}", ex);
        }
    }
    
    private string GetDuckDbType(Type dotNetType)
    {
        if (dotNetType == typeof(string)) return "VARCHAR";
        if (dotNetType == typeof(int)) return "INTEGER";
        if (dotNetType == typeof(long)) return "BIGINT";
        if (dotNetType == typeof(double)) return "DOUBLE";
        if (dotNetType == typeof(float)) return "FLOAT";
        if (dotNetType == typeof(decimal)) return "DECIMAL";
        if (dotNetType == typeof(bool)) return "BOOLEAN";
        if (dotNetType == typeof(DateTime)) return "TIMESTAMP";
        if (dotNetType == typeof(byte[])) return "BLOB";
        return "VARCHAR"; // Default to VARCHAR for unknown types
    }

    // ── Analytics Methods ───────────────────────────────────────────────

    /// <summary>
    /// Generates a comprehensive FileProfile with per-column statistics using DuckDB aggregate SQL.
    /// </summary>
    public async Task<FileProfile> GetFileProfileAsync(
        string filePath,
        CsvImportOptions? csvOptions = null,
        JsonImportOptions? jsonOptions = null,
        CancellationToken cancellationToken = default,
        IProgress<(int Current, int Total)>? progress = null)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            using var transcodeScope = FileFormatDetector.PrepareFilePath(filePath, csvOptions);
            var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(filePath);
            var formatName = FileFormatDetector.GetFormatDisplayName(format);
            _logger.LogInformation("Profiling {Format} file: {FilePath}", formatName, filePath);

            // Load spatial extension for Excel files
            if (format == SupportedFileFormat.Excel)
                await EnsureExcelExtensionAsync(connection);

            var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, csvOptions, jsonOptions);

            // Get schema
            var columns = new List<(string Name, string Type, bool IsNullable)>();
            var describeSql = $"DESCRIBE SELECT * FROM {readerExpr}";
            using (var cmd = new DuckDBCommand(describeSql, connection))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var nullable = reader["null"]?.ToString()?.Equals("YES", StringComparison.OrdinalIgnoreCase) ?? true;
                    columns.Add((reader.GetString("column_name"), reader.GetString("column_type"), nullable));
                }
            }

            // Get row count
            long rowCount;
            using (var cmd = new DuckDBCommand($"SELECT COUNT(*) FROM {readerExpr}", connection))
            {
                rowCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            var columnProfiles = new List<ColumnProfile>();
            var fileSrc = readerExpr;

            for (int ci = 0; ci < columns.Count; ci++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (colName, colType, isNullable) = columns[ci];
                var profile = await ProfileColumnAsync(connection, fileSrc, colName, colType, rowCount, isNullable);
                columnProfiles.Add(profile);
                progress?.Report((ci + 1, columns.Count));
            }

            var fileProfile = new FileProfile
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                SourceFormat = format,
                RowCount = rowCount,
                ColumnCount = columns.Count,
                FileSizeBytes = new System.IO.FileInfo(filePath).Length,
                AnalyzedAt = DateTime.Now,
                Columns = columnProfiles
            };

            return fileProfile;
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation without hiding it as an InvalidOperationException
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to profile file: {FilePath}", filePath);
            throw new InvalidOperationException($"Failed to profile file '{Path.GetFileName(filePath)}': {ex.Message}", ex);
        }
    }

    private async Task<ColumnProfile> ProfileColumnAsync(DuckDBConnection connection, string src, string colName, string colType, long totalRows, bool isNullable = true)
    {
        var category = CategorizeColumn(colType);
        var escapedCol = $"\"{colName}\"";

        var profile = new ColumnProfile
        {
            Name = colName,
            DuckDbType = colType,
            Category = category,
            TotalRows = totalRows,
            IsNullable = isNullable
        };

        try
        {
            // Universal stats: null count, distinct count
            var universalSql = $"SELECT COUNT(*) - COUNT({escapedCol}) AS null_count, COUNT(DISTINCT {escapedCol}) AS distinct_count FROM {src}";
            using (var cmd = new DuckDBCommand(universalSql, connection))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    profile.NullCount = Convert.ToInt64(reader["null_count"]);
                    profile.DistinctCount = Convert.ToInt64(reader["distinct_count"]);
                }
            }

            switch (category)
            {
                case ColumnCategory.Numeric:
                    await ProfileNumericColumnAsync(connection, src, escapedCol, profile);
                    break;
                case ColumnCategory.String:
                    await ProfileStringColumnAsync(connection, src, escapedCol, profile);
                    break;
                case ColumnCategory.Boolean:
                    await ProfileBooleanColumnAsync(connection, src, escapedCol, profile);
                    break;
                case ColumnCategory.DateTime:
                    await ProfileDateTimeColumnAsync(connection, src, escapedCol, profile);
                    break;
            }

            // Top values (for all column types)
            await GetTopValuesAsync(connection, src, escapedCol, profile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error profiling column {Column}, some stats may be missing", colName);
        }

        return profile;
    }

    private async Task ProfileNumericColumnAsync(DuckDBConnection connection, string src, string col, ColumnProfile profile)
    {
        var sql = $@"SELECT 
            MIN({col})::DOUBLE AS min_val,
            MAX({col})::DOUBLE AS max_val,
            AVG({col})::DOUBLE AS mean_val,
            MEDIAN({col})::DOUBLE AS median_val,
            STDDEV_SAMP({col})::DOUBLE AS stddev_val,
            SUM({col})::DOUBLE AS sum_val,
            QUANTILE_CONT({col}, 0.25)::DOUBLE AS q1_val,
            QUANTILE_CONT({col}, 0.75)::DOUBLE AS q3_val
            FROM {src}
            WHERE {col} IS NOT NULL";

        using var cmd = new DuckDBCommand(sql, connection);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            profile.Min = reader.IsDBNull(reader.GetOrdinal("min_val")) ? null : Convert.ToDouble(reader["min_val"]);
            profile.Max = reader.IsDBNull(reader.GetOrdinal("max_val")) ? null : Convert.ToDouble(reader["max_val"]);
            profile.Mean = reader.IsDBNull(reader.GetOrdinal("mean_val")) ? null : Convert.ToDouble(reader["mean_val"]);
            profile.Median = reader.IsDBNull(reader.GetOrdinal("median_val")) ? null : Convert.ToDouble(reader["median_val"]);
            profile.StdDev = reader.IsDBNull(reader.GetOrdinal("stddev_val")) ? null : Convert.ToDouble(reader["stddev_val"]);
            profile.Sum = reader.IsDBNull(reader.GetOrdinal("sum_val")) ? null : Convert.ToDouble(reader["sum_val"]);
            profile.Q1 = reader.IsDBNull(reader.GetOrdinal("q1_val")) ? null : Convert.ToDouble(reader["q1_val"]);
            profile.Q3 = reader.IsDBNull(reader.GetOrdinal("q3_val")) ? null : Convert.ToDouble(reader["q3_val"]);
        }

        // Outlier count (beyond 1.5 × IQR)
        if (profile.Q1.HasValue && profile.Q3.HasValue && profile.IQR > 0)
        {
            var lowerBound = profile.Q1.Value - 1.5 * profile.IQR.Value;
            var upperBound = profile.Q3.Value + 1.5 * profile.IQR.Value;
            var outlierSql = $"SELECT COUNT(*) FROM {src} WHERE {col} IS NOT NULL AND ({col}::DOUBLE < {lowerBound} OR {col}::DOUBLE > {upperBound})";
            using var outlierCmd = new DuckDBCommand(outlierSql, connection);
            profile.OutlierCount = Convert.ToInt64(await outlierCmd.ExecuteScalarAsync());
        }

        // Histogram (10 buckets)
        await GetHistogramAsync(connection, src, col, profile);
    }

    private async Task ProfileStringColumnAsync(DuckDBConnection connection, string src, string col, ColumnProfile profile)
    {
        var sql = $@"SELECT 
            MIN(LENGTH({col})) AS min_len,
            MAX(LENGTH({col})) AS max_len,
            AVG(LENGTH({col}))::DOUBLE AS avg_len,
            SUM(CASE WHEN {col} = '' THEN 1 ELSE 0 END) AS empty_count
            FROM {src}
            WHERE {col} IS NOT NULL";

        using var cmd = new DuckDBCommand(sql, connection);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            profile.MinLength = reader.IsDBNull(reader.GetOrdinal("min_len")) ? null : Convert.ToInt32(reader["min_len"]);
            profile.MaxLength = reader.IsDBNull(reader.GetOrdinal("max_len")) ? null : Convert.ToInt32(reader["max_len"]);
            profile.AvgLength = reader.IsDBNull(reader.GetOrdinal("avg_len")) ? null : Convert.ToDouble(reader["avg_len"]);
            profile.EmptyStringCount = reader.IsDBNull(reader.GetOrdinal("empty_count")) ? null : Convert.ToInt32(reader["empty_count"]);
        }
    }

    private async Task ProfileBooleanColumnAsync(DuckDBConnection connection, string src, string col, ColumnProfile profile)
    {
        var sql = $@"SELECT 
            SUM(CASE WHEN {col} = TRUE THEN 1 ELSE 0 END) AS true_count,
            SUM(CASE WHEN {col} = FALSE THEN 1 ELSE 0 END) AS false_count
            FROM {src}
            WHERE {col} IS NOT NULL";

        using var cmd = new DuckDBCommand(sql, connection);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            profile.TrueCount = reader.IsDBNull(reader.GetOrdinal("true_count")) ? null : Convert.ToInt64(reader["true_count"]);
            profile.FalseCount = reader.IsDBNull(reader.GetOrdinal("false_count")) ? null : Convert.ToInt64(reader["false_count"]);
        }
    }

    private async Task ProfileDateTimeColumnAsync(DuckDBConnection connection, string src, string col, ColumnProfile profile)
    {
        var sql = $@"SELECT 
            MIN({col})::VARCHAR AS min_date,
            MAX({col})::VARCHAR AS max_date
            FROM {src}
            WHERE {col} IS NOT NULL";

        using var cmd = new DuckDBCommand(sql, connection);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            profile.MinDate = reader.IsDBNull(reader.GetOrdinal("min_date")) ? null : reader["min_date"]?.ToString();
            profile.MaxDate = reader.IsDBNull(reader.GetOrdinal("max_date")) ? null : reader["max_date"]?.ToString();
        }
    }

    private async Task GetTopValuesAsync(DuckDBConnection connection, string src, string col, ColumnProfile profile, int topN = 5)
    {
        try
        {
            // Use CAST instead of :: for better compatibility, and group by CAST value to match SELECT
            var sql = $@"SELECT CAST({col} AS VARCHAR) AS val, COUNT(*) AS cnt 
                FROM {src}
                WHERE {col} IS NOT NULL 
                GROUP BY CAST({col} AS VARCHAR)
                ORDER BY cnt DESC 
                LIMIT {topN}";

            using var cmd = new DuckDBCommand(sql, connection);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var count = Convert.ToInt64(reader["cnt"]);
                profile.TopValues.Add(new ValueFrequency
                {
                    Value = reader["val"]?.ToString() ?? "(null)",
                    Count = count,
                    Percentage = profile.NonNullCount == 0 ? 0 : Math.Round((double)count / profile.NonNullCount * 100, 2)
                });
            }

            // Normalize bar widths so the top entry = 100
            if (profile.TopValues.Count > 0)
            {
                var maxCount = profile.TopValues[0].Count;
                foreach (var tv in profile.TopValues)
                    tv.RelativeWidth = maxCount == 0 ? 0 : Math.Round((double)tv.Count / maxCount * 100, 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve top values for column {Column} in {Source}", col, src);
        }
    }

    private async Task GetHistogramAsync(DuckDBConnection connection, string src, string col, ColumnProfile profile, int buckets = 10)
    {
        if (!profile.Min.HasValue || !profile.Max.HasValue || profile.Min.Value == profile.Max.Value)
            return;

        var range = profile.Max.Value - profile.Min.Value;
        var bucketSize = range / buckets;

        var sql = $@"SELECT 
            WIDTH_BUCKET({col}::DOUBLE, {profile.Min.Value}, {profile.Max.Value + 0.001}, {buckets}) AS bucket,
            COUNT(*) AS cnt
            FROM {src}
            WHERE {col} IS NOT NULL
            GROUP BY bucket
            ORDER BY bucket";

        try
        {
            using var cmd = new DuckDBCommand(sql, connection);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var bucketIndex = Convert.ToInt32(reader["bucket"]);
                if (bucketIndex < 1 || bucketIndex > buckets) continue;

                var lowerBound = profile.Min.Value + (bucketIndex - 1) * bucketSize;
                var upperBound = profile.Min.Value + bucketIndex * bucketSize;

                profile.Histogram.Add(new HistogramBucket
                {
                    LowerBound = lowerBound,
                    UpperBound = upperBound,
                    Count = Convert.ToInt64(reader["cnt"])
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve histogram for column {Column} in {Source}", col, src);
        }
    }

    /// <summary>
    /// Generates grouped statistics for a file, grouped by the specified dimension columns.
    /// Returns a dictionary mapping group key strings to their FileProfile.
    /// </summary>
    public async Task<Dictionary<string, FileProfile>> GetGroupedStatisticsAsync(
        string filePath,
        List<string> groupByColumns,
        CsvImportOptions? csvOptions = null,
        JsonImportOptions? jsonOptions = null,
        CancellationToken cancellationToken = default,
        IProgress<(int Current, int Total)>? progress = null)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var normalizedPath = filePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(filePath);

            // Load spatial extension for Excel files
            if (format == SupportedFileFormat.Excel)
                await EnsureExcelExtensionAsync(connection);

            var src = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, csvOptions, jsonOptions);
            var groupByCols = string.Join(", ", groupByColumns.Select(c => $"\"{c}\""));

            // Get distinct group values
            var groupSql = $"SELECT {groupByCols}, COUNT(*) AS group_count FROM {src} GROUP BY {groupByCols} ORDER BY group_count DESC LIMIT 100";
            var groups = new List<(string Key, long Count)>();

            using (var cmd = new DuckDBCommand(groupSql, connection))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var keyParts = new List<string>();
                    for (int i = 0; i < groupByColumns.Count; i++)
                    {
                        keyParts.Add(reader.IsDBNull(i) ? "(null)" : reader.GetValue(i)?.ToString() ?? "(null)");
                    }
                    var key = string.Join(" | ", keyParts);
                    var count = Convert.ToInt64(reader["group_count"]);
                    groups.Add((key, count));
                }
            }

            // Get schema
            var columns = new List<(string Name, string Type, bool IsNullable)>();
            var describeSql = $"DESCRIBE SELECT * FROM {src}";
            using (var cmd = new DuckDBCommand(describeSql, connection))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString("column_name");
                    if (!groupByColumns.Contains(name))
                    {
                        var nullable = reader["null"]?.ToString()?.Equals("YES", StringComparison.OrdinalIgnoreCase) ?? true;
                        columns.Add((name, reader.GetString("column_type"), nullable));
                    }
                }
            }

            var result = new Dictionary<string, FileProfile>();

            int gi = 0;
            foreach (var (groupKey, groupCount) in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupProfile = new FileProfile
                {
                    FilePath = filePath,
                    FileName = groupKey,
                    RowCount = groupCount,
                    ColumnCount = columns.Count
                };

                // Build WHERE clause for this group
                var keyParts = groupKey.Split(" | ");
                var whereClauses = new List<string>();
                for (int i = 0; i < groupByColumns.Count; i++)
                {
                    var val = keyParts[i];
                    if (val == "(null)")
                        whereClauses.Add($"\"{groupByColumns[i]}\" IS NULL");
                    else
                        whereClauses.Add($"\"{groupByColumns[i]}\"::VARCHAR = '{val.Replace("'", "''")}'");
                }
                var whereClause = string.Join(" AND ", whereClauses);
                var filteredSrc = $"(SELECT * FROM {src} WHERE {whereClause})";

                foreach (var (colName, colType, isNullable) in columns)
                {
                    var profile = await ProfileColumnAsync(connection, filteredSrc, colName, colType, groupCount, isNullable);
                    profile.Name = colName;
                    groupProfile.Columns.Add(profile);
                }

                result[groupKey] = groupProfile;
                progress?.Report((++gi, groups.Count));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation without logging it as an error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get grouped statistics for: {FilePath}", filePath);
            throw;
        }
    }

    private static ColumnCategory CategorizeColumn(string duckDbType)
    {
        var upper = duckDbType.ToUpperInvariant();
        if (upper.Contains("INT") || upper.Contains("FLOAT") || upper.Contains("DOUBLE") ||
            upper.Contains("DECIMAL") || upper.Contains("NUMERIC") || upper == "REAL" ||
            upper == "SMALLINT" || upper == "TINYINT" || upper == "BIGINT" ||
            upper == "HUGEINT" || upper == "UINTEGER" || upper == "UBIGINT" ||
            upper == "USMALLINT" || upper == "UTINYINT" || upper == "UHUGEINT")
            return ColumnCategory.Numeric;

        if (upper.Contains("BOOL"))
            return ColumnCategory.Boolean;

        if (upper.Contains("DATE") || upper.Contains("TIME") || upper.Contains("TIMESTAMP") || upper.Contains("INTERVAL"))
            return ColumnCategory.DateTime;

        if (upper.Contains("VARCHAR") || upper.Contains("TEXT") || upper.Contains("CHAR") ||
            upper.Contains("STRING") || upper == "BLOB" || upper == "UUID")
            return ColumnCategory.String;

        return ColumnCategory.Other;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _connection?.Dispose();
            }
            _disposed = true;
        }
    }
}

public class DataFileInfo
{
    public string FilePath { get; set; } = string.Empty;
    public SupportedFileFormat Format { get; set; }
    public List<ColumnInfo> Columns { get; set; } = [];
    public long RowCount { get; set; }

    /// <summary>
    /// Optional list of source file paths when the data comes from multiple files.
    /// For single-file scenarios this may be empty or contain a single entry equal to <see cref="FilePath" />.
    /// </summary>
    public List<string> SourceFiles { get; set; } = [];
}

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Nullable { get; set; }
}
