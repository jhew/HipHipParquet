using System.Data;

namespace HipHipParquet.Services;

public static class DataTableEditHelper
{
    public static int DeleteRows(DataTable dataTable, IEnumerable<DataRowView> selectedRows, string rowNumberColumnName)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(selectedRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        var rowsToDelete = selectedRows
            .Select(rowView => rowView.Row)
            .Where(row => row.Table == dataTable)
            .Distinct()
            .ToList();

        foreach (var row in rowsToDelete)
            dataTable.Rows.Remove(row);

        ResequenceRowNumbers(dataTable, rowNumberColumnName);
        return rowsToDelete.Count;
    }

    public static void ResequenceRowNumbers(DataTable dataTable, string rowNumberColumnName)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        if (!dataTable.Columns.Contains(rowNumberColumnName))
            return;

        for (int i = 0; i < dataTable.Rows.Count; i++)
            dataTable.Rows[i][rowNumberColumnName] = i + 1;
    }
}
