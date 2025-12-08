using DuckDB.NET.Data;
using System.Data;
using System.IO;
using Microsoft.Extensions.Logging;

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