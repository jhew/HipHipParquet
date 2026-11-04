using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HipHipParquet.Tests;

/// <summary>
/// A header dropdown lists the values left by the other columns' selections, so after
/// filtering one column the next dropdown doesn't offer values with no visible rows.
/// </summary>
public class DistinctValuesRowFilterTests
{
    [Fact]
    public async Task DistinctValues_WithRowFilter_ListOnlyValuesInMatchingRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hhp-cascade-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var connection = new DuckDBConnection("DataSource=:memory:"))
            {
                await connection.OpenAsync();
                var escaped = path.Replace("\\", "/").Replace("'", "''");
                using var command = new DuckDBCommand($"""
                    COPY (
                        SELECT * FROM (VALUES
                            ('east', 'apple'), ('east', 'pear'), ('west', 'plum'), ('west', NULL)
                        ) AS t(region, fruit)
                    ) TO '{escaped}' (FORMAT PARQUET)
                    """, connection);
                await command.ExecuteNonQueryAsync();
            }

            using var service = new ParquetService(NullLogger<ParquetService>.Instance);

            var (all, _, allCount, _) = await service.GetDistinctValuesAsync(path, "fruit");
            Assert.Equal(["(Blank)", "apple", "pear", "plum"], all);
            Assert.Equal(4, allCount);

            var (east, _, eastCount, _) = await service.GetDistinctValuesAsync(path, "fruit", rowFilter: new GridQueryState
            {
                ColumnFilters = new Dictionary<string, HashSet<string>> { ["region"] = ["east"] },
                AvailableColumns = ["region", "fruit"]
            });
            Assert.Equal(["apple", "pear"], east);
            Assert.Equal(2, eastCount);

            var (searched, _, _, _) = await service.GetDistinctValuesAsync(path, "fruit", rowFilter: new GridQueryState
            {
                GlobalSearch = "west",
                AvailableColumns = ["region", "fruit"]
            });
            Assert.Equal(["(Blank)", "plum"], searched);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
