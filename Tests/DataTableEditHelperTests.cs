using System.Data;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class DataTableEditHelperTests
{
    [Fact]
    public void DeleteRows_RemovesDistinctRowsAndResequencesRowNumbers()
    {
        var table = new DataTable();
        table.Columns.Add("__RowNumber", typeof(int));
        table.Columns.Add("Name", typeof(string));

        table.Rows.Add(1, "Alpha");
        table.Rows.Add(2, "Beta");
        table.Rows.Add(3, "Gamma");
        table.Rows.Add(4, "Delta");

        var view = table.DefaultView;
        var deletedCount = DataTableEditHelper.DeleteRows(
            table,
            [view[1], view[3], view[1]],
            "__RowNumber");

        Assert.Equal(2, deletedCount);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Alpha", table.Rows[0]["Name"]);
        Assert.Equal("Gamma", table.Rows[1]["Name"]);
        Assert.Equal(1, table.Rows[0]["__RowNumber"]);
        Assert.Equal(2, table.Rows[1]["__RowNumber"]);
    }

    [Fact]
    public void DeleteRows_WithoutRowNumberColumn_StillRemovesRows()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));

        table.Rows.Add("Alpha");
        table.Rows.Add("Beta");

        var deletedCount = DataTableEditHelper.DeleteRows(
            table,
            [table.DefaultView[0]],
            "__RowNumber");

        Assert.Equal(1, deletedCount);
        Assert.Single(table.Rows);
        Assert.Equal("Beta", table.Rows[0]["Name"]);
    }

    [Fact]
    public void DeleteRows_WithNoSelectedRows_DoesNotMutateRowNumbers()
    {
        var table = new DataTable();
        table.Columns.Add("__RowNumber", typeof(int));
        table.Columns.Add("Name", typeof(string));

        table.Rows.Add(10, "Alpha");
        table.Rows.Add(20, "Beta");

        var deletedCount = DataTableEditHelper.DeleteRows(
            table,
            Array.Empty<DataRowView>(),
            "__RowNumber");

        Assert.Equal(0, deletedCount);
        Assert.Equal([10, 20], table.Rows.Cast<DataRow>().Select(r => (int)r["__RowNumber"]).ToArray());
    }

    [Fact]
    public void DuplicateRows_InsertsCopiesAfterEachSelectedRow()
    {
        var table = new DataTable();
        table.Columns.Add("__RowNumber", typeof(int));
        table.Columns.Add("Name", typeof(string));

        table.Rows.Add(1, "Alpha");
        table.Rows.Add(2, "Beta");
        table.Rows.Add(3, "Gamma");

        var duplicated = DataTableEditHelper.DuplicateRows(
            table,
            [table.DefaultView[0], table.DefaultView[2]],
            "__RowNumber");

        Assert.Equal(2, duplicated);
        Assert.Equal(["Alpha", "Alpha", "Beta", "Gamma", "Gamma"], table.Rows.Cast<DataRow>().Select(r => (string)r["Name"]).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], table.Rows.Cast<DataRow>().Select(r => (int)r["__RowNumber"]).ToArray());
    }

    [Fact]
    public void DuplicateRows_WithNoSelectedRows_DoesNotMutateRowNumbers()
    {
        var table = new DataTable();
        table.Columns.Add("__RowNumber", typeof(int));
        table.Columns.Add("Name", typeof(string));

        table.Rows.Add(7, "Alpha");
        table.Rows.Add(8, "Beta");

        var duplicatedCount = DataTableEditHelper.DuplicateRows(
            table,
            Array.Empty<DataRowView>(),
            "__RowNumber");

        Assert.Equal(0, duplicatedCount);
        Assert.Equal([7, 8], table.Rows.Cast<DataRow>().Select(r => (int)r["__RowNumber"]).ToArray());
    }

    [Fact]
    public void DuplicateRows_WithLargeSelection_PreservesOrderAndResequences()
    {
        var table = new DataTable();
        table.Columns.Add("__RowNumber", typeof(int));
        table.Columns.Add("Name", typeof(string));

        for (int i = 1; i <= 500; i++)
            table.Rows.Add(i, $"Row {i}");

        var selected = new List<DataRowView>();
        for (int i = 0; i < 500; i += 25)
            selected.Add(table.DefaultView[i]);

        var duplicated = DataTableEditHelper.DuplicateRows(table, selected, "__RowNumber");

        Assert.Equal(selected.Count, duplicated);
        Assert.Equal(500 + selected.Count, table.Rows.Count);
        Assert.Equal(1, table.Rows[0]["__RowNumber"]);
        Assert.Equal(table.Rows.Count, table.Rows[^1]["__RowNumber"]);
        Assert.Equal("Row 1", table.Rows[0]["Name"]);
        Assert.Equal("Row 1", table.Rows[1]["Name"]);
    }

    [Fact]
    public void DeleteDuplicateRows_RemovesLaterMatches()
    {
        var table = new DataTable();
        table.Columns.Add("__RowNumber", typeof(int));
        table.Columns.Add("First", typeof(string));
        table.Columns.Add("Last", typeof(string));

        table.Rows.Add(1, "Ada", "Lovelace");
        table.Rows.Add(2, "Ada", "Lovelace");
        table.Rows.Add(3, "Grace", "Hopper");
        table.Rows.Add(4, "Ada", "Lovelace");

        var removed = DataTableEditHelper.DeleteDuplicateRows(table, ["First", "Last"], "__RowNumber");

        Assert.Equal(2, removed);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["Ada", "Grace"], table.Rows.Cast<DataRow>().Select(r => (string)r["First"]).ToArray());
    }

    [Fact]
    public void TrimWhitespace_OnlyUpdatesChangedValues()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add("  Alpha  ");
        table.Rows.Add("Beta");

        var updated = DataTableEditHelper.TrimWhitespace(
            [
                new DataTableCellTarget(table.Rows[0], "Name"),
                new DataTableCellTarget(table.Rows[1], "Name")
            ]);

        Assert.Equal(1, updated);
        Assert.Equal("Alpha", table.Rows[0]["Name"]);
        Assert.Equal("Beta", table.Rows[1]["Name"]);
    }

    [Fact]
    public void ReplaceInCells_RespectsCaseSensitivity()
    {
        var table = new DataTable();
        table.Columns.Add("Text", typeof(string));
        table.Rows.Add("foo FOO");

        var insensitive = DataTableEditHelper.ReplaceInCells(
            [new DataTableCellTarget(table.Rows[0], "Text")],
            "foo",
            "bar",
            matchCase: false);

        Assert.Equal(1, insensitive);
        Assert.Equal("bar bar", table.Rows[0]["Text"]);

        table.Rows[0]["Text"] = "foo FOO";
        var sensitive = DataTableEditHelper.ReplaceInCells(
            [new DataTableCellTarget(table.Rows[0], "Text")],
            "foo",
            "bar",
            matchCase: true);

        Assert.Equal(1, sensitive);
        Assert.Equal("bar FOO", table.Rows[0]["Text"]);
    }
}
