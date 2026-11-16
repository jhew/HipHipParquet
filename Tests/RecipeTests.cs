using System.Data;
using System.IO;
using DuckDB.NET.Data;
using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

/// <summary>
/// A recipe rule has to do in DuckDB exactly what the grid action did in the app, or an
/// exported recipe would clean the next file differently from this one.
/// </summary>
public sealed class RecipeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hhp-recipe-{Guid.NewGuid():N}");

    public RecipeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly string?[] TextValues =
    [
        "  padded  ",
        "\ttabbed\t",
        "\u00A0no-break\u00A0",
        "\u2003em space\u2003",
        "\u000Bvertical tab\u0085",
        "inner  spaces kept",
        "",
        "   ",
        null,
        "N/A",
        "n/a",
        "a.b*c (n/a) [x]",
        "back\\slash n/A"
    ];

    private static DataTable GridTable(IEnumerable<string?> values)
    {
        var table = new DataTable();
        table.Columns.Add("__RowNumber", typeof(int));
        table.Columns.Add("text", typeof(string));
        var i = 1;
        foreach (var value in values)
            table.Rows.Add(i++, (object?)value ?? DBNull.Value);
        return table;
    }

    private static IEnumerable<DataTableCellTarget> WholeColumn(DataTable table, string column)
        => table.Rows.Cast<DataRow>().Select(row => new DataTableCellTarget(row, column));

    private static async Task<DuckDBConnection> OpenWithTextAsync(IEnumerable<string?> values)
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await ExecAsync(connection, "CREATE TABLE t (text VARCHAR)");
        foreach (var value in values)
        {
            using var insert = new DuckDBCommand("INSERT INTO t VALUES ($1)", connection);
            insert.Parameters.Add(new DuckDBParameter((object?)value ?? DBNull.Value));
            await insert.ExecuteNonQueryAsync();
        }
        return connection;
    }

    private static async Task ExecAsync(DuckDBConnection connection, string sql)
    {
        using var command = new DuckDBCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string?>> ReadColumnAsync(DuckDBConnection connection, string sql)
    {
        using var command = new DuckDBCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync();
        var values = new List<string?>();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString());
        return values;
    }

    private static List<string?> GridColumn(DataTable table, string column)
        => table.Rows.Cast<DataRow>().Select(row => row[column] is DBNull ? null : row[column].ToString()).ToList();

    [Fact]
    public async Task TrimRule_MatchesGridTrim()
    {
        var grid = GridTable(TextValues);
        DataTableEditHelper.TrimWhitespace(WholeColumn(grid, "text"));

        using var connection = await OpenWithTextAsync(TextValues);
        await ExecAsync(connection, RecipeExporter.BuildStepSql(RecipeExporter.CreateTrimRule("t", "text"))!);

        Assert.Equal(GridColumn(grid, "text"), await ReadColumnAsync(connection, "SELECT text FROM t ORDER BY rowid"));
    }

    [Theory]
    [InlineData("n/a", "-", false)]
    [InlineData("n/a", "-", true)]
    [InlineData("N/A", "", false)]
    [InlineData("(n/a) [x]", "$1 \\0", false)]
    [InlineData("a.b*c", "it's", true)]
    [InlineData("d  ", "_", true)]
    [InlineData("\\", "/", false)]
    public async Task ReplaceRule_MatchesGridReplace(string find, string replace, bool matchCase)
    {
        var grid = GridTable(TextValues);
        DataTableEditHelper.ReplaceInCells(WholeColumn(grid, "text"), find, replace, matchCase);

        using var connection = await OpenWithTextAsync(TextValues);
        await ExecAsync(connection, RecipeExporter.BuildStepSql(RecipeExporter.CreateReplaceRule("t", "text", find, replace, matchCase))!);

        Assert.Equal(GridColumn(grid, "text"), await ReadColumnAsync(connection, "SELECT text FROM t ORDER BY rowid"));
    }

    [Fact]
    public async Task SetNullRule_ClearsTheColumn()
    {
        using var connection = await OpenWithTextAsync(TextValues);
        await ExecAsync(connection, RecipeExporter.BuildStepSql(RecipeExporter.CreateSetNullRule("t", "text"))!);

        Assert.All(await ReadColumnAsync(connection, "SELECT text FROM t"), Assert.Null);
    }

    [Fact]
    public async Task DedupeRule_KeepsTheSameRowsInTheSameOrderAsTheGrid()
    {
        var rows = new (int Id, string? Country, int Amount)[]
        {
            (1, "DE", 10), (2, "FR", 20), (3, "DE", 30), (4, null, 40),
            (5, "FR", 20), (6, null, 60), (7, "IT", 70), (8, "DE", 10)
        };

        var grid = new DataTable();
        grid.Columns.Add("__RowNumber", typeof(int));
        grid.Columns.Add("id", typeof(int));
        grid.Columns.Add("country", typeof(string));
        grid.Columns.Add("amount", typeof(int));
        foreach (var (id, country, amount) in rows)
            grid.Rows.Add(id, id, (object?)country ?? DBNull.Value, amount);
        DataTableEditHelper.DeleteDuplicateRows(grid, ["country"], "__RowNumber");
        var gridIds = grid.Rows.Cast<DataRow>().Select(row => row["id"].ToString()).ToList();

        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await ExecAsync(connection, "CREATE TABLE t (id INTEGER, country VARCHAR, amount INTEGER)");
        await ExecAsync(connection, "INSERT INTO t VALUES " + string.Join(", ",
            rows.Select(r => $"({r.Id}, {(r.Country == null ? "NULL" : $"'{r.Country}'")}, {r.Amount})")));
        await ExecAsync(connection, RecipeExporter.BuildStepSql(RecipeExporter.CreateDedupeRule("t", ["country"]))!);

        Assert.Equal(gridIds, await ReadColumnAsync(connection, "SELECT id FROM t"));
        Assert.Equal(["1", "2", "4", "7"], gridIds);
    }

    [Fact]
    public async Task ExportedSqlScript_RunsInDuckDb_AndWritesTheCleanedFile()
    {
        var input = Path.Combine(_dir, "sales.csv");
        await File.WriteAllTextAsync(input, "id,customer,country\n1,  Ada  ,DE\n2,Bob,n/a\n3,  Ada  ,DE\n4,Cy,FR\n");

        var source = new RecipeSource("sales", FileFormatDetector.GetDuckDbReaderExpression(input.Replace("\\", "/"), SupportedFileFormat.Csv), input, SupportedFileFormat.Csv);
        var steps = new List<RecipeStep>
        {
            RecipeExporter.CreateTrimRule("sales", "customer"),
            RecipeExporter.CreateManualStep("sales", "Edited a cell"),
            RecipeExporter.CreateReplaceRule("sales", "country", "n/a", "", matchCase: false),
            RecipeExporter.CreateDedupeRule("sales", ["customer", "country"]),
            RecipeExporter.CreateSqlStep("sales", "DELETE FROM sales WHERE country = 'FR';", "DELETE sales")
        };

        var script = RecipeExporter.ToDuckDbSql(steps, [source], new DateTime(2026, 11, 14, 9, 30, 0));
        Assert.Contains("-- 2. Edited a cell on sales: a hand edit", script);

        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await ExecAsync(connection, script);

        var output = RecipeExporter.GetCleanedPath(input);
        Assert.True(File.Exists(output));
        var rows = await ReadColumnAsync(connection,
            $"SELECT id || ':' || customer || ':' || coalesce(country, '<null>') FROM read_csv_auto('{output.Replace("\\", "/")}') ORDER BY id");
        Assert.Equal(["1:Ada:DE", "2:Bob:<null>"], rows);
    }

    [Fact]
    public void PythonExport_RunsTheSameStatements()
    {
        var source = new RecipeSource("sales", "read_parquet('C:/data/sales.parquet')", @"C:\data\sales.parquet", SupportedFileFormat.Parquet);
        var steps = new List<RecipeStep>
        {
            RecipeExporter.CreateTrimRule("sales", "customer"),
            RecipeExporter.CreateSqlStep("sales", "UPDATE sales SET country = 'EU' WHERE country = 'DE'", "UPDATE sales")
        };

        var python = RecipeExporter.ToPython(steps, [source], new DateTime(2026, 11, 14));

        Assert.Contains("con.execute(r\"\"\"CREATE OR REPLACE TABLE \"sales\" AS SELECT * FROM read_parquet('C:/data/sales.parquet')\"\"\")", python);
        Assert.Contains($"con.execute(r\"\"\"{RecipeExporter.BuildStepSql(steps[0])}\"\"\")", python);
        Assert.Contains("con.execute(r\"\"\"UPDATE sales SET country = 'EU' WHERE country = 'DE'\"\"\")", python);
        Assert.Contains("\"sales\": con.sql(r'''SELECT * FROM \"sales\"''').pl(),", python);
        Assert.DoesNotContain("COPY", python);
    }

    [Fact]
    public void GridUndo_RemovesTheStepAndLaterGridStepsForThatTable()
    {
        var log = new RecipeLog();
        var trim = log.Add(RecipeExporter.CreateTrimRule("sales", "customer"));
        log.Add(RecipeExporter.CreateSqlStep("other", "DELETE FROM other", "DELETE other"));
        log.Add(RecipeExporter.CreateManualStep("sales", "Edited a cell"));
        log.Add(RecipeExporter.CreateManualStep("returns", "Edited a cell"));

        log.RemoveGridStepAndLater(trim.Id);

        Assert.Equal(["DELETE other", "Edited a cell"], log.Steps.Select(step => step.Description));
        Assert.Equal("returns", log.Steps[1].Target);
    }

    [Fact]
    public void HubUndoDiscardAndSave_KeepTheLogInStep()
    {
        var log = new RecipeLog();
        log.Add(RecipeExporter.CreateSqlStep("sales", "DELETE FROM sales WHERE id = 1", "first"));
        log.MarkSaved(RecipeStepOrigin.QueryHub, "sales");
        log.Add(RecipeExporter.CreateSqlStep("sales", "DELETE FROM sales WHERE id = 2", "second"));
        log.Add(RecipeExporter.CreateSqlStep("sales", "DELETE FROM sales WHERE id = 3", "third"));
        log.Add(RecipeExporter.CreateTrimRule("sales", "customer"));

        log.RemoveLastHubStep("SALES");
        Assert.Equal(["first", "second", "Trim whitespace in customer"], log.Steps.Select(step => step.Description));

        log.RemoveUnsaved(RecipeStepOrigin.QueryHub, "sales");
        Assert.Equal(["first", "Trim whitespace in customer"], log.Steps.Select(step => step.Description));
    }
}
