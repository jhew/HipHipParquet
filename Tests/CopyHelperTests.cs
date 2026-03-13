using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Xunit;

namespace HipHipParquet.Tests
{
    public class CopyHelperTests
    {
        [Fact]
        public void FormatCells_SingleCell_NoHeader_ReturnsPlainValue()
        {
            string? result = null;
            Exception? threadEx = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var dt = new DataTable();
                    dt.Columns.Add("A");
                    dt.Rows.Add("foo");

                    var grid = new DataGrid();
                    grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
                    grid.SelectionMode = DataGridSelectionMode.Extended;
                    var colA = new DataGridTextColumn { Binding = new Binding("[A]"), Header = "A", DisplayIndex = 0 };
                    grid.Columns.Add(colA);
                    grid.ItemsSource = dt.DefaultView;
                    grid.UpdateLayout();

                    // build our own selection list instead of touching the grid's internal collection
                    var sel = new System.Collections.Generic.List<DataGridCellInfo>();
                    sel.Add(new DataGridCellInfo(dt.DefaultView[0], colA));
                    result = Views.CopyHelper.FormatCells(grid, sel, "\t", includeHeaders: false);
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (threadEx != null)
                throw new System.InvalidOperationException("STA thread threw", threadEx);

            Assert.Equal("foo", result);
        }

        [Fact]
        public void FormatCells_MultipleCells_WithHeadersAndCsvQuotes()
        {
            string? result = null;
            Exception? threadEx = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var dt = new DataTable();
                    dt.Columns.Add("A");
                    dt.Columns.Add("B");
                    dt.Rows.Add("x,1", "value");
                    dt.Rows.Add("second", "y\"z");

                    var grid = new DataGrid();
                    grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
                    grid.SelectionMode = DataGridSelectionMode.Extended;
                    var colA = new DataGridTextColumn { Binding = new Binding("[A]"), Header = "A", DisplayIndex = 0 };
                    var colB = new DataGridTextColumn { Binding = new Binding("[B]"), Header = "B", DisplayIndex = 1 };
                    grid.Columns.Add(colA);
                    grid.Columns.Add(colB);
                    grid.ItemsSource = dt.DefaultView;
                    grid.UpdateLayout();

                    var sel = new System.Collections.Generic.List<DataGridCellInfo>();
                    // first row both columns
                    sel.Add(new DataGridCellInfo(dt.DefaultView[0], colA));
                    sel.Add(new DataGridCellInfo(dt.DefaultView[0], colB));
                    // second row first column only
                    sel.Add(new DataGridCellInfo(dt.DefaultView[1], colA));

                    result = Views.CopyHelper.FormatCells(grid, sel, ",", includeHeaders: true);
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (threadEx != null)
                throw new System.InvalidOperationException("STA thread threw", threadEx);

            // header row
            var expected = "A,B\r\n";
            // first data row: x,1 and value with quotes around "x,1"
            expected += "\"x,1\",value\r\n";
            // second row only A column
            expected += "second\r\n";

            Assert.Equal(expected, result);
        }

        [Fact]
        public void FormatCells_GridBasedHeader_ExtractsColumnName()
        {
            // Regression: headers built as Grid > DockPanel > TextBlock should resolve
            // to the column name string, not "System.Windows.Controls.Grid".
            string? result = null;
            Exception? threadEx = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var dt = new DataTable();
                    dt.Columns.Add("Name");
                    dt.Rows.Add("Alice");

                    // Build a Grid-based header matching the structure created by
                    // MainWindow.CreateColumnHeader (Grid > DockPanel > [icon TextBlock, name TextBlock])
                    var iconBlock = new TextBlock { Text = "Aa" };
                    DockPanel.SetDock(iconBlock, Dock.Left);
                    var nameBlock = new TextBlock { Text = "Name" };
                    var namePanel = new DockPanel { LastChildFill = true };
                    namePanel.Children.Add(iconBlock);
                    namePanel.Children.Add(nameBlock);

                    var headerGrid = new Grid();
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(namePanel, 0);
                    headerGrid.Children.Add(namePanel);
                    // Also add a filter button in column 1 (like the real header does)
                    var btn = new Button { Content = "\u25BC", Tag = "Name" };
                    Grid.SetColumn(btn, 1);
                    headerGrid.Children.Add(btn);

                    var grid = new DataGrid();
                    grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
                    grid.SelectionMode = DataGridSelectionMode.Extended;
                    var col = new DataGridTextColumn
                    {
                        Binding = new Binding("[Name]"),
                        Header = headerGrid,
                        DisplayIndex = 0
                    };
                    grid.Columns.Add(col);
                    grid.ItemsSource = dt.DefaultView;
                    grid.UpdateLayout();

                    var sel = new System.Collections.Generic.List<DataGridCellInfo>();
                    sel.Add(new DataGridCellInfo(dt.DefaultView[0], col));
                    result = Views.CopyHelper.FormatCells(grid, sel, "\t", includeHeaders: true);
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (threadEx != null)
                throw new System.InvalidOperationException("STA thread threw", threadEx);

            Assert.StartsWith("Name\r\n", result);
        }
    }
}
