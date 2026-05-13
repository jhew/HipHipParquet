using System.Data;

namespace HipHipParquet.Services;

public readonly record struct DataTableCellTarget(DataRow Row, string ColumnName);

public static class DataTableEditHelper
{
    public static int DeleteRows(DataTable dataTable, IEnumerable<DataRowView> selectedRows, string rowNumberColumnName)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(selectedRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        var rowsToDelete = GetDistinctRows(dataTable, selectedRows);
        RemoveRows(dataTable, rowsToDelete);
        ResequenceRowNumbers(dataTable, rowNumberColumnName);
        return rowsToDelete.Count;
    }

    public static int KeepOnlyRows(DataTable dataTable, IEnumerable<DataRowView> selectedRows, string rowNumberColumnName)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(selectedRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        var rowsToKeep = GetDistinctRows(dataTable, selectedRows).ToHashSet();
        var rowsToDelete = dataTable.Rows.Cast<DataRow>().Where(row => !rowsToKeep.Contains(row)).ToList();
        RemoveRows(dataTable, rowsToDelete);
        ResequenceRowNumbers(dataTable, rowNumberColumnName);
        return rowsToDelete.Count;
    }

    public static int DeleteUnselectedRows(DataTable dataTable, IEnumerable<DataRowView> selectedRows, string rowNumberColumnName)
        => KeepOnlyRows(dataTable, selectedRows, rowNumberColumnName);

    public static int DuplicateRows(DataTable dataTable, IEnumerable<DataRowView> selectedRows, string rowNumberColumnName)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(selectedRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        var rowsToDuplicate = GetDistinctRows(dataTable, selectedRows)
            .Select(row => new
            {
                Row = row,
                Index = dataTable.Rows.IndexOf(row),
                Values = row.ItemArray.ToArray()
            })
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToList();

        var insertedCount = 0;
        foreach (var item in rowsToDuplicate)
        {
            var newRow = dataTable.NewRow();
            newRow.ItemArray = item.Values.ToArray();
            dataTable.Rows.InsertAt(newRow, item.Index + 1 + insertedCount);
            insertedCount++;
        }

        ResequenceRowNumbers(dataTable, rowNumberColumnName);
        return insertedCount;
    }

    public static int InsertBlankRow(DataTable dataTable, DataRow anchorRow, bool insertBelow, string rowNumberColumnName, string? sourceFileColumnName = null)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(anchorRow);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        var index = dataTable.Rows.IndexOf(anchorRow);
        if (index < 0)
            return 0;

        var newRow = dataTable.NewRow();
        if (!string.IsNullOrWhiteSpace(sourceFileColumnName)
            && dataTable.Columns.Contains(sourceFileColumnName)
            && anchorRow.Table == dataTable)
        {
            newRow[sourceFileColumnName] = anchorRow[sourceFileColumnName];
        }

        dataTable.Rows.InsertAt(newRow, insertBelow ? index + 1 : index);
        ResequenceRowNumbers(dataTable, rowNumberColumnName);
        return 1;
    }

    public static DataTable CreateTableFromRows(DataTable dataTable, IEnumerable<DataRowView> selectedRows, string rowNumberColumnName)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(selectedRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        var clone = dataTable.Clone();
        foreach (var row in GetDistinctRows(dataTable, selectedRows))
            clone.ImportRow(row);

        ResequenceRowNumbers(clone, rowNumberColumnName);
        return clone;
    }

    public static int CountDuplicateRows(DataTable dataTable, IReadOnlyList<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(columnNames);

        if (columnNames.Count == 0)
            return 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        foreach (DataRow row in dataTable.Rows)
        {
            var key = BuildCompositeKey(row, columnNames);
            if (!seen.Add(key))
                duplicates++;
        }

        return duplicates;
    }

    public static int DeleteDuplicateRows(DataTable dataTable, IReadOnlyList<string> columnNames, string rowNumberColumnName)
    {
        ArgumentNullException.ThrowIfNull(dataTable);
        ArgumentNullException.ThrowIfNull(columnNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowNumberColumnName);

        if (columnNames.Count == 0)
            return 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<DataRow>();
        foreach (DataRow row in dataTable.Rows)
        {
            var key = BuildCompositeKey(row, columnNames);
            if (!seen.Add(key))
                duplicates.Add(row);
        }

        RemoveRows(dataTable, duplicates);
        ResequenceRowNumbers(dataTable, rowNumberColumnName);
        return duplicates.Count;
    }

    public static int SetCellsToNull(IEnumerable<DataTableCellTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var updated = 0;
        foreach (var target in DeduplicateTargets(targets))
        {
            var column = target.Row.Table.Columns[target.ColumnName];
            if (column == null || !column.AllowDBNull)
                continue;

            if (target.Row[target.ColumnName] == DBNull.Value)
                continue;

            target.Row[target.ColumnName] = DBNull.Value;
            updated++;
        }

        return updated;
    }

    public static int FillDown(IReadOnlyList<DataTableCellTarget> orderedTargets, IReadOnlyDictionary<DataRow, int> rowOrder)
    {
        ArgumentNullException.ThrowIfNull(orderedTargets);
        ArgumentNullException.ThrowIfNull(rowOrder);

        if (orderedTargets.Count < 2)
            return 0;

        var updated = 0;
        foreach (var group in orderedTargets
                     .Where(target => rowOrder.ContainsKey(target.Row))
                     .GroupBy(target => target.ColumnName, StringComparer.Ordinal))
        {
            var sortedTargets = group.OrderBy(target => rowOrder[target.Row]).ToList();
            if (sortedTargets.Count < 2)
                continue;

            var sourceValue = sortedTargets[0].Row[sortedTargets[0].ColumnName];
            for (int i = 1; i < sortedTargets.Count; i++)
            {
                var target = sortedTargets[i];
                if (Equals(target.Row[target.ColumnName], sourceValue))
                    continue;

                target.Row[target.ColumnName] = sourceValue;
                updated++;
            }
        }

        return updated;
    }

    public static int TrimWhitespace(IEnumerable<DataTableCellTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var updated = 0;
        foreach (var target in DeduplicateTargets(targets))
        {
            if (target.Row[target.ColumnName] == DBNull.Value)
                continue;

            var original = target.Row[target.ColumnName]?.ToString();
            if (original == null)
                continue;

            var trimmed = original.Trim();
            if (string.Equals(original, trimmed, StringComparison.Ordinal))
                continue;

            target.Row[target.ColumnName] = trimmed;
            updated++;
        }

        return updated;
    }

    public static int ReplaceInCells(IEnumerable<DataTableCellTarget> targets, string findText, string replaceText, bool matchCase)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(findText);

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var updated = 0;

        foreach (var target in DeduplicateTargets(targets))
        {
            if (target.Row[target.ColumnName] == DBNull.Value)
                continue;

            var original = target.Row[target.ColumnName]?.ToString();
            if (string.IsNullOrEmpty(original) || original.IndexOf(findText, comparison) < 0)
                continue;

            var replaced = ReplaceWithComparison(original, findText, replaceText, comparison);
            if (string.Equals(original, replaced, StringComparison.Ordinal))
                continue;

            target.Row[target.ColumnName] = replaced;
            updated++;
        }

        return updated;
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

    private static List<DataRow> GetDistinctRows(DataTable dataTable, IEnumerable<DataRowView> selectedRows)
        => selectedRows
            .Select(rowView => rowView.Row)
            .Where(row => row.Table == dataTable)
            .Distinct()
            .ToList();

    private static IEnumerable<DataTableCellTarget> DeduplicateTargets(IEnumerable<DataTableCellTarget> targets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var key = $"{target.Row.GetHashCode()}::{target.ColumnName}";
            if (seen.Add(key))
                yield return target;
        }
    }

    private static void RemoveRows(DataTable dataTable, IEnumerable<DataRow> rows)
    {
        foreach (var row in rows.ToList())
            dataTable.Rows.Remove(row);
    }

    private static string BuildCompositeKey(DataRow row, IReadOnlyList<string> columnNames)
        => string.Join("\u001F", columnNames.Select(columnName =>
        {
            var value = row[columnName];
            return value == DBNull.Value ? "<NULL>" : value?.ToString() ?? string.Empty;
        }));

    private static string ReplaceWithComparison(string source, string findText, string replaceText, StringComparison comparison)
    {
        var start = 0;
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var index = source.IndexOf(findText, start, comparison);
            if (index < 0)
            {
                builder.Append(source, start, source.Length - start);
                break;
            }

            builder.Append(source, start, index - start);
            builder.Append(replaceText);
            start = index + findText.Length;
        }

        return builder.ToString();
    }
}
