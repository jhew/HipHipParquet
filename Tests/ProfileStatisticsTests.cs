using System.Globalization;
using System.IO;
using DuckDB.NET.Data;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HipHipParquet.Tests;

/// <summary>
/// Column profiling must fill in its statistics, not log and skip them: DuckDB has no
/// WIDTH_BUCKET, and SUM over integers returns HUGEINT, which arrives as a BigInteger.
/// </summary>
public sealed class ProfileStatisticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hhp-profile-{Guid.NewGuid():N}");

    public ProfileStatisticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> WriteSampleAsync()
    {
        var path = Path.Combine(_dir, "sample.parquet");
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var command = new DuckDBCommand($"""
            COPY (
                SELECT
                    i AS id,
                    i % 20 AS int_col,
                    CASE WHEN i = 7 THEN 'NaN'::DOUBLE ELSE (i % 20)::DOUBLE END AS dbl_col,
                    CASE WHEN i % 10 = 0 THEN '' ELSE 'v' || i END AS str_col,
                    i % 3 = 0 AS bool_col
                FROM range(1, 2001) t(i)
            ) TO '{path.Replace("\\", "/")}' (FORMAT PARQUET)
            """, connection);
        await command.ExecuteNonQueryAsync();
        return path;
    }

    [Fact]
    public async Task NumericColumns_GetAHistogram_ThatCoversEveryFiniteValue()
    {
        var service = new ParquetService(NullLogger<ParquetService>.Instance);
        var profile = await service.GetFileProfileAsync(await WriteSampleAsync());

        var id = profile.Columns.Single(c => c.Name == "id");
        Assert.Equal(10, id.Histogram.Count);
        Assert.Equal(2000, id.Histogram.Sum(b => b.Count));
        Assert.Equal(200, id.Histogram[^1].Count);

        var intCol = profile.Columns.Single(c => c.Name == "int_col");
        Assert.Equal(2000, intCol.Histogram.Sum(b => b.Count));
    }

    [Fact]
    public async Task Histogram_IsBuilt_WhenTheDecimalSeparatorIsAComma()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var service = new ParquetService(NullLogger<ParquetService>.Instance);
            var profile = await service.GetFileProfileAsync(await WriteSampleAsync());
            Assert.NotEmpty(profile.Columns.Single(c => c.Name == "int_col").Histogram);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task StringAndBooleanCounts_AreFilledIn()
    {
        var service = new ParquetService(NullLogger<ParquetService>.Instance);
        var profile = await service.GetFileProfileAsync(await WriteSampleAsync());

        var str = profile.Columns.Single(c => c.Name == "str_col");
        Assert.Equal(200, str.EmptyStringCount);
        Assert.Equal(0, str.MinLength);
        Assert.Equal(5, str.MaxLength);

        var flag = profile.Columns.Single(c => c.Name == "bool_col");
        Assert.Equal(666, flag.TrueCount);
        Assert.Equal(1334, flag.FalseCount);
    }
}
