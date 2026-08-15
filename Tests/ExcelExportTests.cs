using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HipHipParquet.Tests;

/// <summary>
/// Round-trip test for XLSX export via the DuckDB spatial extension's GDAL driver.
/// The spatial extension is downloaded on first use, so the test soft-passes when
/// the extension cannot be installed (e.g., offline CI).
/// </summary>
public class ExcelExportTests
{
    private readonly ITestOutputHelper _output;

    public ExcelExportTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SaveFileAsync_Xlsx_RoundTripsData()
    {
        using var service = new ParquetService(NullLogger<ParquetService>.Instance);

        var table = new DataTable();
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("count", typeof(int));
        table.Rows.Add("alpha", 1);
        table.Rows.Add("beta", 2);
        table.Rows.Add("gamma, with comma", 3);

        var xlsxPath = Path.Combine(Path.GetTempPath(), $"hhp-excel-export-{Guid.NewGuid():N}.xlsx");
        try
        {
            try
            {
                await service.SaveFileAsync(xlsxPath, table);
            }
            catch (Exception ex) when (ex.Message.Contains("spatial", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("extension", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("download", StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"Skipping: spatial extension unavailable ({ex.Message})");
                return;
            }

            Assert.True(File.Exists(xlsxPath), "XLSX file was not created");
            Assert.True(new FileInfo(xlsxPath).Length > 0, "XLSX file is empty");

            var loaded = await service.LoadFileAsync(xlsxPath);
            Assert.Equal(3, loaded.Rows.Count);
            var nameColumn = loaded.Columns.Contains("name") ? "name" : loaded.Columns[loaded.Columns.Contains("__RowNumber") ? 1 : 0].ColumnName;
            Assert.Contains(loaded.Rows.Cast<DataRow>(), r => Equals(r[nameColumn]?.ToString(), "gamma, with comma"));
        }
        finally
        {
            try { File.Delete(xlsxPath); } catch { /* best-effort cleanup */ }
        }
    }
}
