using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HipHipParquet.Tests;

/// <summary>
/// BLOB columns (e.g. SQL Server CDC __$start_lsn / __$seqval / __$update_mask / rowversion)
/// used to load as UnmanagedMemoryStream, render as "System.IO.UnmanagedMemoryStream",
/// collapse to one key in Delete Duplicate Rows, and fail on save with
/// "Cannot convert value of type System.IO.UnmanagedMemoryStream to DuckDBValue".
/// </summary>
public class BlobRoundTripTests
{
    [Fact]
    public async Task Parquet_WithBlobColumns_LoadsAsBytes_AndSavesRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hhp-blob-{Guid.NewGuid():N}.parquet");
        try
        {
            await WriteBlobParquetAsync(path);

            using var service = new ParquetService(NullLogger<ParquetService>.Instance);
            var table = await service.LoadFileAsync(path);

            Assert.Equal(typeof(byte[]), table.Columns["lsn"]!.DataType);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x2A, 0x01 }, (byte[])table.Rows[0]["lsn"]);
            Assert.Equal(DBNull.Value, table.Rows[2]["lsn"]);

            await service.SaveFileAsync(path, table);

            using var reloadService = new ParquetService(NullLogger<ParquetService>.Instance);
            var reloaded = await reloadService.LoadFileAsync(path);

            Assert.Equal(3, reloaded.Rows.Count);
            Assert.Equal(typeof(byte[]), reloaded.Columns["lsn"]!.DataType);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x2A, 0x01 }, (byte[])reloaded.Rows[0]["lsn"]);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x2A, 0x02 }, (byte[])reloaded.Rows[1]["lsn"]);
            Assert.Equal(DBNull.Value, reloaded.Rows[2]["lsn"]);
            Assert.Equal("b", reloaded.Rows[1]["id"]);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DeleteDuplicateRows_OnBlobColumn_ComparesContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hhp-blob-dupes-{Guid.NewGuid():N}.parquet");
        try
        {
            await WriteBlobParquetAsync(path);

            using var service = new ParquetService(NullLogger<ParquetService>.Instance);
            var table = await service.LoadFileAsync(path);
            table.Columns.Add("__RowNumber", typeof(long));

            // Two distinct LSNs plus a null: nothing is a duplicate.
            Assert.Equal(0, DataTableEditHelper.CountDuplicateRows(table, new List<string> { "lsn" }));

            var copy = table.NewRow();
            copy.ItemArray = table.Rows[0].ItemArray;
            copy["lsn"] = new byte[] { 0x00, 0x00, 0x00, 0x2A, 0x01 }; // equal content, different array instance
            table.Rows.Add(copy);

            Assert.Equal(1, DataTableEditHelper.CountDuplicateRows(table, new List<string> { "lsn" }));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Helper_ConvertsStreamsAndFormatsHex()
    {
        Assert.Equal(typeof(byte[]), DuckDbValueHelper.ResolveColumnType(typeof(Stream)));
        Assert.Equal(typeof(byte[]), DuckDbValueHelper.ResolveColumnType(typeof(UnmanagedMemoryStream)));
        Assert.Equal(typeof(string), DuckDbValueHelper.ResolveColumnType(typeof(string)));

        var materialized = DuckDbValueHelper.MaterializeValue(new MemoryStream(new byte[] { 1, 2, 255 }));
        Assert.Equal(new byte[] { 1, 2, 255 }, materialized);

        Assert.Equal(new byte[] { 9 }, DuckDbValueHelper.ToParameterValue(new MemoryStream(new byte[] { 9 })));
        Assert.Equal(DBNull.Value, DuckDbValueHelper.ToParameterValue(null));

        Assert.Equal("0x0102FF", DuckDbValueHelper.ToDisplayString(new byte[] { 1, 2, 255 }));
        Assert.Equal("0x0102… (3 bytes)", DuckDbValueHelper.ToDisplayString(new byte[] { 1, 2, 255 }, maxBytes: 2));
        Assert.Equal("0x0102FF", DuckDbValueHelper.ToComparisonString(new byte[] { 1, 2, 255 }));
        Assert.Equal(string.Empty, DuckDbValueHelper.ToDisplayString(DBNull.Value));
    }

    private static async Task WriteBlobParquetAsync(string path)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var escaped = path.Replace("\\", "/").Replace("'", "''");
        var sql = $"""
            COPY (
                SELECT * FROM (VALUES
                    ('a', '\x00\x00\x00\x2A\x01'::BLOB),
                    ('b', '\x00\x00\x00\x2A\x02'::BLOB),
                    ('c', NULL::BLOB)
                ) AS t(id, lsn)
            ) TO '{escaped}' (FORMAT PARQUET)
            """;

        using var command = new DuckDBCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
