using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HipHipParquet.Tests;

/// <summary>
/// Filter keys are DuckDB's CAST(col AS VARCHAR) text, which the SQL paths compare on, but the
/// in-memory filter matches DataView's Convert(col, 'System.String'). The two differ for most
/// non-string types: a DECIMAL(38,5) lists as "10.00000" but loads as 10m and shows "10", so
/// picking it from the header dropdown used to hide every row.
/// </summary>
public class FilterValueTextTests
{
    private static readonly string[] Columns =
        ["dec_wide", "dec_narrow", "dbl", "flt", "flag", "d", "ts", "t", "i", "s"];

    [Fact]
    public async Task EveryDistinctKey_MatchesSameRows_InSql_InMemory_AndViaFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hhp-filter-text-{Guid.NewGuid():N}.parquet");
        try
        {
            await WriteAllTypesParquetAsync(path);

            using var service = new ParquetService(NullLogger<ParquetService>.Instance);
            var table = await service.LoadFileAsync(path);

            foreach (var column in Columns)
            {
                var (keys, gridTexts, _, _) = await service.GetDistinctValuesAsync(path, column);
                Assert.NotEmpty(keys);

                foreach (var key in keys)
                {
                    var sqlCount = (await service.ExecuteGridQueryAsync(new GridQueryState
                    {
                        SourceFilePath = path,
                        ColumnFilters = new Dictionary<string, HashSet<string>> { [column] = [key] },
                        AvailableColumns = [.. Columns],
                        RowLimit = 100
                    })).Rows.Count;

                    var fallbackText = FilterValueText.ToDataViewText(key, table.Columns[column]);
                    Assert.True(sqlCount > 0, $"{column} = {key}: no SQL match");
                    Assert.True(sqlCount == CountInMemory(table, column, gridTexts[key]),
                        $"{column} = {key}: grid text \"{gridTexts[key]}\" matched a different row count");
                    Assert.True(sqlCount == CountInMemory(table, column, fallbackText),
                        $"{column} = {key}: fallback text \"{fallbackText}\" matched a different row count");
                }
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task WideDecimal_ListsDuckDbKey_WithGridTextWithoutTrailingZeros()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hhp-filter-decimal-{Guid.NewGuid():N}.parquet");
        try
        {
            await WriteAllTypesParquetAsync(path);

            using var service = new ParquetService(NullLogger<ParquetService>.Instance);
            var (keys, gridTexts, _, _) = await service.GetDistinctValuesAsync(path, "dec_wide");

            Assert.Contains("10.00000", keys);
            Assert.Equal("10", gridTexts["10.00000"]);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(typeof(double), "10.5", "10,5")]
    [InlineData(typeof(decimal), "10.50", "10,50")]
    public void Fallback_UsesTableLocale(Type type, string duckDbText, string expected)
    {
        var table = new DataTable { Locale = CultureInfo.GetCultureInfo("de-DE") };
        var column = table.Columns.Add("c", type);
        Assert.Equal(expected, FilterValueText.ToDataViewText(duckDbText, column));
    }

    [Fact]
    public void Fallback_LeavesUnparseableAndOffsetTimestampsUnchanged()
    {
        var table = new DataTable();
        Assert.Equal("abc", FilterValueText.ToDataViewText("abc", table.Columns.Add("dbl", typeof(double))));
        Assert.Equal("2024-01-05 10:00:00+00",
            FilterValueText.ToDataViewText("2024-01-05 10:00:00+00", table.Columns.Add("ts", typeof(DateTime))));
    }

    private static int CountInMemory(DataTable table, string column, string gridText)
        => new DataView(table) { RowFilter = $"Convert([{column}], 'System.String') IN ('{gridText.Replace("'", "''")}')" }.Count;

    private static async Task WriteAllTypesParquetAsync(string path)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var escaped = path.Replace("\\", "/").Replace("'", "''");
        using var command = new DuckDBCommand($"""
            COPY (
                SELECT * FROM (VALUES
                    (10::DECIMAL(38,5), 10::DECIMAL(10,5), 10::DOUBLE, 0.1::FLOAT, true,
                     DATE '2024-01-05', TIMESTAMP '2024-01-05 10:00:00', TIME '10:00:00', 10, 'a'),
                    (2.5::DECIMAL(38,5), 2.5::DECIMAL(10,5), 2.5::DOUBLE, 10::FLOAT, false,
                     DATE '2024-12-31', TIMESTAMP '2024-12-31 23:59:59.5', TIME '23:59:59', 2, 'b'),
                    (10::DECIMAL(38,5), 10::DECIMAL(10,5), 1e20::DOUBLE, 0.1::FLOAT, true,
                     DATE '2024-01-05', TIMESTAMP '2024-01-05 10:00:00', TIME '10:00:00', 10, 'a')
                ) AS v(dec_wide, dec_narrow, dbl, flt, flag, d, ts, t, i, s)
            ) TO '{escaped}' (FORMAT PARQUET)
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}
