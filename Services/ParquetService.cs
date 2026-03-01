using DuckDB.NET.Data;
using System.Data;
using System.IO;
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
    
    public async Task<DataTable> LoadParquetFileAsync(string filePath)
    {
        try
        {
            _connection = new DuckDBConnection("DataSource=:memory:");
            await _connection.OpenAsync();
            
            // Use DuckDB to read Parquet file
            var normalizedPath = filePath.Replace("\\", "/");
            _logger.LogInformation("Reading Parquet file: {FilePath}", normalizedPath);
            
            var sql = $"SELECT * FROM read_parquet('{normalizedPath}')";
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
            _logger.LogError(ex, "Failed to load parquet file: {FilePath}. Error: {Error}", filePath, ex.Message);
            
            // Cleanup connection on error
            if (_connection != null)
            {
                _connection.Dispose();
                _connection = null;
            }
            
            throw new InvalidOperationException($"Failed to load Parquet file '{Path.GetFileName(filePath)}': {ex.Message}", ex);
        }
    }
    
    public async Task<ParquetFileInfo> GetFileInfoAsync(string filePath)
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
                
            var normalizedPath = filePath.Replace("\\", "/");
            
            // Get schema information using DuckDB's read_parquet function
            _logger.LogInformation("Getting schema for file: {FilePath}", normalizedPath);
            
            var sql = $"DESCRIBE SELECT * FROM read_parquet('{normalizedPath}')";
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
            
            // Get row count
            var rowCount = await GetRowCountAsync(normalizedPath);
            
            return new ParquetFileInfo
            {
                FilePath = filePath,
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
    
    private async Task<long> GetRowCountAsync(string filePath)
    {
        var sql = $"SELECT COUNT(*) FROM read_parquet('{filePath}')";
        using var command = new DuckDBCommand(sql, _connection!);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
    
    public async Task SaveParquetFileAsync(string filePath, DataTable dataTable)
    {
        try
        {
            if (_connection == null || _connection.State != ConnectionState.Open)
            {
                _connection = new DuckDBConnection("DataSource=:memory:");
                await _connection.OpenAsync();
            }
            
            var normalizedPath = filePath.Replace("\\", "/");
            _logger.LogInformation("Saving Parquet file: {FilePath}", normalizedPath);
            
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
            
            // Insert data into temporary table
            foreach (DataRow row in dataTable.Rows)
            {
                var values = new List<string>();
                foreach (DataColumn col in dataTable.Columns)
                {
                    if (col.ColumnName == "__RowNumber") continue;
                    
                    var value = row[col];
                    if (value == DBNull.Value || value == null)
                    {
                        values.Add("NULL");
                    }
                    else if (col.DataType == typeof(string))
                    {
                        values.Add($"'{value.ToString()?.Replace("'", "''")}'");
                    }
                    else if (col.DataType == typeof(DateTime))
                    {
                        values.Add($"'{((DateTime)value):yyyy-MM-dd HH:mm:ss}'");
                    }
                    else if (col.DataType == typeof(bool))
                    {
                        values.Add(((bool)value) ? "TRUE" : "FALSE");
                    }
                    else
                    {
                        values.Add(value.ToString() ?? "NULL");
                    }
                }
                
                var insertSql = $"INSERT INTO {tempTableName} VALUES ({string.Join(", ", values)})";
                using var insertCommand = new DuckDBCommand(insertSql, _connection);
                await insertCommand.ExecuteNonQueryAsync();
            }
            
            // Export to Parquet
            var exportSql = $"COPY {tempTableName} TO '{normalizedPath}' (FORMAT PARQUET)";
            _logger.LogDebug("Exporting to Parquet: {SQL}", exportSql);
            
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
            _logger.LogError(ex, "Failed to save parquet file: {FilePath}", filePath);
            throw new InvalidOperationException($"Failed to save Parquet file '{Path.GetFileName(filePath)}': {ex.Message}", ex);
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
    public async Task<FileProfile> GetFileProfileAsync(string filePath)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var normalizedPath = filePath.Replace("\\", "/");
            var escapedPath = normalizedPath.Replace("'", "''");
            _logger.LogInformation("Profiling Parquet file: {FilePath}", normalizedPath);

            // Get schema
            var columns = new List<(string Name, string Type, bool IsNullable)>();
            var describeSql = $"DESCRIBE SELECT * FROM read_parquet('{escapedPath}')";
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
            using (var cmd = new DuckDBCommand($"SELECT COUNT(*) FROM read_parquet('{escapedPath}')", connection))
            {
                rowCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            var columnProfiles = new List<ColumnProfile>();
            var fileSrc = $"read_parquet('{escapedPath}')";

            foreach (var (colName, colType, isNullable) in columns)
            {
                var profile = await ProfileColumnAsync(connection, fileSrc, colName, colType, rowCount, isNullable);
                columnProfiles.Add(profile);
            }

            var fileProfile = new FileProfile
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                RowCount = rowCount,
                ColumnCount = columns.Count,
                FileSizeBytes = new System.IO.FileInfo(filePath).Length,
                AnalyzedAt = DateTime.Now,
                Columns = columnProfiles
            };

            return fileProfile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to profile parquet file: {FilePath}", filePath);
            throw new InvalidOperationException($"Failed to profile Parquet file '{Path.GetFileName(filePath)}': {ex.Message}", ex);
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
        var sql = $@"SELECT {col}::VARCHAR AS val, COUNT(*) AS cnt 
            FROM {src} 
            WHERE {col} IS NOT NULL 
            GROUP BY {col} 
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

    /// <summary>
    /// Generates grouped statistics for a file, grouped by the specified dimension columns.
    /// Returns a dictionary mapping group key strings to their FileProfile.
    /// </summary>
    public async Task<Dictionary<string, FileProfile>> GetGroupedStatisticsAsync(string filePath, List<string> groupByColumns)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var normalizedPath = filePath.Replace("\\", "/");
            var escapedPath = normalizedPath.Replace("'", "''");
            var src = $"read_parquet('{escapedPath}')";
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

            foreach (var (groupKey, groupCount) in groups)
            {
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
            }

            return result;
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

public class ParquetFileInfo
{
    public string FilePath { get; set; } = string.Empty;
    public List<ColumnInfo> Columns { get; set; } = [];
    public long RowCount { get; set; }
}

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Nullable { get; set; }
}