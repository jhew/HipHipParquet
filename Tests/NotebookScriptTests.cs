using DuckDB.NET.Data;
using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace HipHipParquet.Tests;

/// <summary>
/// Query Hub scripts change an in-session copy of an opened file; the file changes only on Save.
/// </summary>
public sealed class NotebookScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hhp-scripts-{Guid.NewGuid():N}");
    private readonly NotebookSessionService _session = new(NullLogger<NotebookSessionService>.Instance);

    public NotebookScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _session.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(string Path, NotebookSource Source)> OpenSalesAsync()
    {
        var path = Path.Combine(_dir, "sales.parquet");
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            await connection.OpenAsync();
            using var command = new DuckDBCommand($"""
                COPY (SELECT * FROM (VALUES (1, 'DE', 10), (2, 'FR', 20), (3, 'DE', 30)) AS t(id, country, amount))
                TO '{path.Replace("\\", "/")}' (FORMAT PARQUET)
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
        return (path, await _session.RegisterFileSourceAsync([path], preferredAlias: "sales"));
    }

    private async Task<long> ScalarAsync(string sql)
    {
        var result = await _session.ExecuteScriptAsync(sql);
        var table = await _session.MaterializeSourceAsync(result.Statements[^1].ResultSource!.Alias);
        return Convert.ToInt64(table.Rows[0][0]);
    }

    private static async Task<long> FileRowCountAsync(string path, string where = "")
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var command = new DuckDBCommand($"SELECT COUNT(*) FROM read_parquet('{path.Replace("\\", "/")}') {where}", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Update_ChangesSessionCopy_NotTheFile_UntilSave()
    {
        var (path, _) = await OpenSalesAsync();

        var result = await _session.ExecuteScriptAsync("UPDATE sales SET country = 'EU' WHERE country = 'DE'");

        Assert.Equal(2, result.Statements[0].RowsAffected);
        Assert.True(_session.TryGetSource("sales", out var source));
        Assert.True(source!.HasUnsavedChanges);
        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM sales WHERE country = 'EU'"));
        Assert.Equal(0, await FileRowCountAsync(path, "WHERE country = 'EU'"));

        await _session.SaveSourceAsync("sales");

        Assert.False(source.HasUnsavedChanges);
        Assert.Equal(2, await FileRowCountAsync(path, "WHERE country = 'EU'"));
    }

    [Fact]
    public async Task Undo_RevertsOneStatementAtATime()
    {
        await OpenSalesAsync();
        await _session.ExecuteScriptAsync("DELETE FROM sales WHERE id = 1");
        await _session.ExecuteScriptAsync("INSERT INTO sales VALUES (4, 'IT', 40), (5, 'IT', 50)");
        Assert.Equal(4, await ScalarAsync("SELECT COUNT(*) FROM sales"));

        Assert.Equal("INSERT sales", await _session.UndoLastChangeAsync());
        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM sales"));

        Assert.Equal("DELETE sales", await _session.UndoLastChangeAsync());
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM sales"));
        Assert.True(_session.TryGetSource("sales", out var source));
        Assert.False(source!.HasUnsavedChanges);
        Assert.False(source.IsMaterialized);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task Discard_ReturnsToTheSavedFile()
    {
        await OpenSalesAsync();
        await _session.ExecuteScriptAsync("ALTER TABLE sales ADD COLUMN note VARCHAR; UPDATE sales SET note = 'x'");

        await _session.DiscardChangesAsync("sales");

        Assert.True(_session.TryGetSource("sales", out var source));
        Assert.False(source!.HasUnsavedChanges);
        Assert.DoesNotContain(source.Columns, c => c.Name == "note");
        Assert.Empty(_session.GetSourcesWithUnsavedChanges());
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task FailedScript_AppliesNothing()
    {
        await OpenSalesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _session.ExecuteScriptAsync("DELETE FROM sales; CREATE TABLE t AS SELECT 1 AS x; SELECT * FROM missing_table"));

        Assert.StartsWith("Statement 3 of 3 failed", ex.Message);
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM sales"));
        Assert.False(_session.TryGetSource("t", out _));
        Assert.True(_session.TryGetSource("sales", out var source));
        Assert.False(source!.HasUnsavedChanges);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task CreateAndDrop_RegisterAndRemoveSources_AndUndoBringsThemBack()
    {
        var (path, _) = await OpenSalesAsync();

        var created = await _session.ExecuteScriptAsync("CREATE TABLE de_sales AS SELECT * FROM sales WHERE country = 'DE'");
        Assert.Equal(["de_sales"], created.ChangedAliases);
        Assert.True(_session.TryGetSource("de_sales", out var table));
        Assert.Equal(NotebookSourceKind.Table, table!.Kind);
        Assert.Equal(2, table.RowCount);

        await _session.ExecuteScriptAsync("DROP VIEW sales");
        Assert.False(_session.TryGetSource("sales", out _));
        Assert.True(File.Exists(path));

        await _session.UndoLastChangeAsync();
        Assert.True(_session.TryGetSource("sales", out var restored));
        Assert.Equal(3, restored!.RowCount);

        await _session.UndoLastChangeAsync();
        Assert.False(_session.TryGetSource("de_sales", out _));
    }

    [Fact]
    public async Task UndoOfDropTable_RestoresItsRows()
    {
        await OpenSalesAsync();
        await _session.ExecuteScriptAsync("CREATE TABLE t AS SELECT * FROM sales; DROP TABLE t");
        Assert.False(_session.TryGetSource("t", out _));

        await _session.UndoLastChangeAsync();

        Assert.True(_session.TryGetSource("t", out var t));
        Assert.Equal(3, t!.RowCount);
    }

    [Fact]
    public async Task TooLargeToSnapshot_ChangeRunsButCantBeUndone()
    {
        await OpenSalesAsync();
        await _session.ExecuteScriptAsync("CREATE TABLE t AS SELECT * FROM sales");
        _session.UndoSnapshotRowLimit = 1;

        var result = await _session.ExecuteScriptAsync("DELETE FROM t WHERE id = 1");

        Assert.False(result.Statements[0].CanUndo);
        Assert.False(_session.CanUndo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.UndoLastChangeAsync());
    }

    [Fact]
    public async Task QueriesProduceResultSources_IncludingDescribe()
    {
        await OpenSalesAsync();

        var result = await _session.ExecuteScriptAsync("SELECT * FROM sales WHERE amount > 10; DESCRIBE sales");

        Assert.Equal(2, result.Statements[0].ResultSource!.RowCount);
        Assert.Equal(3, result.Statements[1].ResultSource!.RowCount);
        Assert.Empty(result.ChangedAliases);
    }

    [Fact]
    public async Task SaveAs_MovesAFileSourceToTheNewFile()
    {
        var (path, _) = await OpenSalesAsync();
        await _session.ExecuteScriptAsync("DELETE FROM sales WHERE id = 3");
        var copyPath = Path.Combine(_dir, "sales-copy.csv");

        await _session.SaveSourceAsync("sales", copyPath);

        Assert.True(_session.TryGetSource("sales", out var source));
        Assert.Equal(copyPath, source!.FilePath);
        Assert.False(source.HasUnsavedChanges);
        Assert.Equal(3, await FileRowCountAsync(path));
        Assert.Equal(3, File.ReadAllLines(copyPath).Length); // header + 2 rows

        // Discard now returns to the new file.
        await _session.ExecuteScriptAsync("DELETE FROM sales");
        await _session.DiscardChangesAsync("sales");
        Assert.Equal(2, source.RowCount);
    }
}
