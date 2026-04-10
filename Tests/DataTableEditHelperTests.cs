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
}
