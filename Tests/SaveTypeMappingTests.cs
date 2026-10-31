using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HipHipParquet.Tests;

/// <summary>
/// Save used to recreate every column from its .NET type, and mapped anything outside a short
/// list to VARCHAR: HUGEINT, UUID, DATE, TIME, INTERVAL, the small and unsigned integers, lists
/// and structs were all rewritten as text, and DECIMAL(p,s) became DECIMAL(18,3).
/// </summary>
public class SaveTypeMappingTests
{
    private const string AllTypesSelect = """
        SELECT * FROM (VALUES
            (1, 1::TINYINT, 1::SMALLINT, 1::INTEGER, 1::BIGINT, 170141183460469231731687303715884105727::HUGEINT,
             255::UTINYINT, 65535::USMALLINT, 4294967295::UINTEGER, 18446744073709551615::UBIGINT, 1::UHUGEINT,
             1.5::FLOAT, 1.25::DOUBLE, 1234567.891::DECIMAL(10,3), 0.123456789012::DECIMAL(18,12),
             DATE '2024-01-02', TIME '12:34:56.789', TIMESTAMP '2024-01-02 03:04:05.123456',
             TIMESTAMPTZ '2024-01-02 03:04:05.5+00', INTERVAL '3 days 4 hours',
             '0f8fad5b-d9cb-469f-a165-70867728950e'::UUID, '\x00\x2A'::BLOB, true, 'text',
             [1, 2, 3], {'k': 1, 'v': 'a, ''quoted'''}, MAP {1: 'x'}, [{'a': 1}, {'a': 2}]),
            (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
             NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
        ) AS t(id, tiny, small, int32, int64, huge, utiny, usmall, uint32, uint64, uhuge,
               f32, f64, dec_10_3, dec_18_12, d, tm, ts, tstz, iv, uid, bin, flag, txt, lst, st, mp, lst_st)
        """;

    [Fact]
    public async Task Parquet_LoadThenSave_KeepsEveryColumnTypeAndValue()
    {
        var source = TempParquetPath("source");
        var saved = TempParquetPath("saved");
        try
        {
            await ExecuteAsync($"COPY ({AllTypesSelect}) TO '{Sql(source)}' (FORMAT PARQUET)");

            using var service = new ParquetService(NullLogger<ParquetService>.Instance);
            var table = await service.LoadFileAsync(source);
            await service.SaveFileAsync(saved, table);

            Assert.Equal(await DescribeAsync(source), await DescribeAsync(saved));
            Assert.Equal(await ReadAsTextAsync(source), await ReadAsTextAsync(saved));
        }
        finally
        {
            TryDelete(source);
            TryDelete(saved);
        }
    }

    [Theory]
    [InlineData(".csv")]
    [InlineData(".json")]
    [InlineData(".xlsx")]
    public async Task AllTypes_SaveToOtherFormats(string extension)
    {
        var source = TempParquetPath("source");
        var target = Path.ChangeExtension(TempParquetPath("export"), extension);
        try
        {
            await ExecuteAsync($"COPY ({AllTypesSelect}) TO '{Sql(source)}' (FORMAT PARQUET)");

            using var service = new ParquetService(NullLogger<ParquetService>.Instance);
            var table = await service.LoadFileAsync(source);
            await service.SaveFileAsync(target, table);

            var reloaded = await service.LoadFileAsync(target);
            Assert.Equal(2, reloaded.Rows.Count);

            // GDAL leaves columns of some types out of an XLSX without an error.
            foreach (DataColumn column in table.Columns)
                Assert.True(reloaded.Columns.Contains(column.ColumnName), $"Column '{column.ColumnName}' missing from {extension}");
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    [Fact]
    public void GetDuckDbType_WithoutLoadedType_MapsFromDotNetType()
    {
        var table = new DataTable();
        var expected = new Dictionary<string, (Type Type, string DuckDbType)>
        {
            ["sbyte"] = (typeof(sbyte), "TINYINT"),
            ["short"] = (typeof(short), "SMALLINT"),
            ["huge"] = (typeof(BigInteger), "HUGEINT"),
            ["byte"] = (typeof(byte), "UTINYINT"),
            ["ushort"] = (typeof(ushort), "USMALLINT"),
            ["uint"] = (typeof(uint), "UINTEGER"),
            ["ulong"] = (typeof(ulong), "UBIGINT"),
            ["date"] = (typeof(DateOnly), "DATE"),
            ["time"] = (typeof(TimeOnly), "TIME"),
            ["tstz"] = (typeof(DateTimeOffset), "TIMESTAMPTZ"),
            ["interval"] = (typeof(TimeSpan), "INTERVAL"),
            ["uuid"] = (typeof(Guid), "UUID"),
            ["blob"] = (typeof(byte[]), "BLOB"),
            ["struct"] = (typeof(Dictionary<string, object>), "JSON"),
            ["list_of_struct"] = (typeof(List<Dictionary<string, object>>), "JSON"),
            ["list"] = (typeof(List<int>), "INTEGER[]"),
            ["nested_list"] = (typeof(List<List<string>>), "VARCHAR[][]"),
            ["other"] = (typeof(Uri), "VARCHAR"),
        };
        foreach (var (name, (type, _)) in expected)
            table.Columns.Add(name, type);

        foreach (var (name, (_, duckDbType)) in expected)
            Assert.Equal(duckDbType, ParquetService.GetDuckDbType(table.Columns[name]!));
    }

    [Fact]
    public void GetDuckDbType_DecimalWithoutLoadedType_FitsTheValues()
    {
        var table = new DataTable();
        var column = table.Columns.Add("amount", typeof(decimal));
        Assert.Equal("DECIMAL(38,0)", ParquetService.GetDuckDbType(column));

        table.Rows.Add(1.5m);
        table.Rows.Add(12345.123456m);
        table.Rows.Add(DBNull.Value);
        Assert.Equal("DECIMAL(38,6)", ParquetService.GetDuckDbType(column));

        // 29 integer digits leave room for 9 decimal places, not the 28 a tiny value asks for.
        table.Rows.Add(decimal.MaxValue);
        table.Rows.Add(0.0000000000000000000000000001m);
        Assert.Equal("DECIMAL(38,9)", ParquetService.GetDuckDbType(column));
    }

    [Fact]
    public void LoadedType_SurvivesCopyAndClone()
    {
        var table = new DataTable();
        var column = table.Columns.Add("amount", typeof(decimal));
        column.ExtendedProperties[DuckDbValueHelper.DuckDbTypeProperty] = "DECIMAL(10,3)";

        // Undo snapshots use Copy(); Export Selected Rows uses Clone().
        Assert.Equal("DECIMAL(10,3)", ParquetService.GetDuckDbType(table.Copy().Columns["amount"]!));
        Assert.Equal("DECIMAL(10,3)", ParquetService.GetDuckDbType(table.Clone().Columns["amount"]!));
    }

    private static string TempParquetPath(string label)
        => Path.Combine(Path.GetTempPath(), $"hhp-types-{label}-{Guid.NewGuid():N}.parquet");

    private static string Sql(string path) => path.Replace("\\", "/").Replace("'", "''");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static async Task ExecuteAsync(string sql)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var command = new DuckDBCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<List<string>> DescribeAsync(string path)
        => QueryRowsAsync($"SELECT column_name || ' ' || column_type FROM (DESCRIBE SELECT * FROM read_parquet('{Sql(path)}'))");

    private static Task<List<string>> ReadAsTextAsync(string path)
        => QueryRowsAsync($"SELECT concat_ws(' | ', COLUMNS(*)::VARCHAR) FROM read_parquet('{Sql(path)}') ORDER BY id");

    private static async Task<List<string>> QueryRowsAsync(string sql)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var command = new DuckDBCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
