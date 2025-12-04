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
            
            // Use DuckDB to read Parquet file with limit for performance  
            var normalizedPath = filePath.Replace("\\", "/");
            _logger.LogInformation("Reading Parquet file: {FilePath}", normalizedPath);
            
            var sql = $"SELECT * FROM read_parquet('{normalizedPath}') LIMIT 1000";
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