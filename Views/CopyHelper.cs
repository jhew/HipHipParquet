using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HipHipParquet.Views
{
    /// <summary>
    /// Helper logic for formatting data grid selections into clipboard-friendly text.
    /// The methods mirror the logic that used to live in <see cref="MainWindow" /> but
    /// are pulled out so that unit tests can exercise the behaviour without needing to
    /// instantiate the entire window.
    /// </summary>
    public static class CopyHelper
    {
        public static string FormatCells(DataGrid grid, IEnumerable<DataGridCellInfo> selectedCells, string delimiter, bool includeHeaders)
        {
            var cells = selectedCells?.ToList() ?? new List<DataGridCellInfo>();
            if (cells.Count == 0)
                return string.Empty;

            // special-case a single cell to avoid trailing newline when not including headers
            if (cells.Count == 1 && !includeHeaders)
            {
                return GetCellValue(cells[0], delimiter);
            }

            // special-case single cell with header so header is on its own line
            if (cells.Count == 1 && includeHeaders)
            {
                var headerText = GetColumnHeaderText(cells[0].Column);
                var cellValue = GetCellValue(cells[0], delimiter);
                var sb = new StringBuilder();
                sb.AppendLine(headerText);
                sb.Append(cellValue);
                return sb.ToString();
            }

            // Build item-to-index lookup once: O(N) instead of O(N*M) from IndexOf per cell
            var indexMap = new Dictionary<object, int>();
            for (int i = 0; i < grid.Items.Count; i++)
            {
                var item = grid.Items[i];
                if (!indexMap.ContainsKey(item))
                    indexMap[item] = i;
            }

            var rowGroups = cells
                .GroupBy(cell => indexMap.TryGetValue(cell.Item, out var idx) ? idx : -1)
                .OrderBy(g => g.Key);

            var output = new StringBuilder();

            if (includeHeaders)
            {
                var headerColumns = cells
                    .Select(cell => cell.Column)
                    .Distinct()
                    .OrderBy(col => col.DisplayIndex)
                    .ToList();
                var headers = headerColumns.Select(GetColumnHeaderText);
                output.AppendLine(string.Join(delimiter, headers));
            }

            foreach (var rowGroup in rowGroups)
            {
                var cellsInRow = rowGroup.OrderBy(c => c.Column.DisplayIndex);
                var values = new List<string>();
                foreach (var cell in cellsInRow)
                {
                    values.Add(GetCellValue(cell, delimiter));
                }
                output.AppendLine(string.Join(delimiter, values));
            }

            return output.ToString();
        }


        private static string GetCellValue(DataGridCellInfo cell, string delimiter)
        {
            var cellValue = string.Empty;
            if (cell.Column is DataGridBoundColumn column)
            {
                var binding = (column as DataGridTextColumn)?.Binding as System.Windows.Data.Binding;
                if (binding != null && cell.Item is DataRowView rowView)
                {
                    var columnName = binding.Path.Path.Trim('[', ']');
                    var value = rowView[columnName];
                    cellValue = value?.ToString() ?? string.Empty;
                }
            }
            else if (cell.Column is DataGridTemplateColumn templateColumn &&
                     !string.IsNullOrEmpty(templateColumn.SortMemberPath) &&
                     cell.Item is DataRowView templateRowView &&
                     templateRowView.Row.Table.Columns.Contains(templateColumn.SortMemberPath))
            {
                // Template columns (used for horizontally scrollable cells) carry the
                // source column name in SortMemberPath rather than a Binding.
                var value = templateRowView[templateColumn.SortMemberPath];
                cellValue = value == DBNull.Value ? string.Empty : value?.ToString() ?? string.Empty;
            }

            // escape if needed for CSV (RFC 4180: quote if contains comma, double-quote, LF or CR)
            if (delimiter == "," && (cellValue.Contains(",") || cellValue.Contains("\"") || cellValue.Contains("\n") || cellValue.Contains("\r")))
            {
                cellValue = "\"" + cellValue.Replace("\"", "\"\"") + "\"";
            }

            return cellValue;
        }

        private static string GetColumnHeaderText(DataGridColumn column)
        {
            if (column == null)
                return string.Empty;
            if (column.Header is System.Windows.FrameworkElement element)
            {
                var text = GetHeaderTextFromElement(element);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            return column.Header?.ToString() ?? string.Empty;
        }

        private static string GetHeaderTextFromElement(System.Windows.FrameworkElement element)
        {
            if (element is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                return tb.Text;

            if (element is System.Windows.Controls.Panel panel)
            {
                // Look for a non-empty TextBlock among direct children (last one wins for icon+name layouts)
                var directBlock = panel.Children
                    .OfType<System.Windows.Controls.TextBlock>()
                    .LastOrDefault(t => !string.IsNullOrEmpty(t.Text));
                if (directBlock != null)
                    return directBlock.Text;

                // Recurse into child FrameworkElements (e.g., Grid > DockPanel > TextBlock)
                foreach (var child in panel.Children.OfType<System.Windows.FrameworkElement>())
                {
                    var text = GetHeaderTextFromElement(child);
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }

            return string.Empty;
        }
    }
}
