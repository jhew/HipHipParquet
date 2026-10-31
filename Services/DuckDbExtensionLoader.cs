using DuckDB.NET.Data;

namespace HipHipParquet.Services;

/// <summary>
/// Loads DuckDB extensions shared by every connection in the process.
/// </summary>
public static class DuckDbExtensionLoader
{
    /// <summary>
    /// INSTALL downloads into the per-user extension folder shared by all connections. When two
    /// connections install the same extension at once on first use they race on that file and one
    /// fails, leaving its connection without the extension ("Copy Function with name gdal does not
    /// exist"). Installs are serialized per process to avoid that.
    /// </summary>
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>
    /// Installs and loads the spatial extension (Excel/XLSX via GDAL) on <paramref name="connection"/>.
    /// Only signed extensions from the official DuckDB repository are allowed.
    /// </summary>
    public static async Task LoadSpatialAsync(DuckDBConnection connection)
    {
        using (var configCmd = new DuckDBCommand("SET allow_unsigned_extensions = false;", connection))
            await configCmd.ExecuteNonQueryAsync();

        await InstallLock.WaitAsync();
        try
        {
            using var installCmd = new DuckDBCommand("INSTALL spatial; LOAD spatial;", connection);
            await installCmd.ExecuteNonQueryAsync();
        }
        finally
        {
            InstallLock.Release();
        }
    }
}
