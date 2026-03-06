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

            // group by row index and build lines
            var rowGroups = cells
                .GroupBy(cell => grid.Items.IndexOf(cell.Item))
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

            // escape if needed for CSV
            if (delimiter == "," && (cellValue.Contains(",") || cellValue.Contains("\"") || cellValue.Contains("\n")))
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
                if (element is System.Windows.Controls.StackPanel panel)
                {
                    var textBlock = panel.Children.OfType<System.Windows.Controls.TextBlock>().LastOrDefault();
                    return textBlock?.Text ?? column.Header.ToString() ?? string.Empty;
                }
            }
            return column.Header?.ToString() ?? string.Empty;
        }
    }
}
