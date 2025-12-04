using System.Windows;
using System.Windows.Controls;
using System.Data;
using Microsoft.Win32;
using System.ComponentModel;
using System.Collections.ObjectModel;
using HipHipParquet.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HipHipParquet.Views;

public partial class MainWindow : Window
{
    private DataTable? _originalData;
    private DataView? _dataView;
    private readonly List<TextBox> _searchBoxes = new();
    
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*",
            Title = "Select Parquet File"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            await LoadFileAsync(openFileDialog.FileName);
        }
    }

    private async Task LoadFileAsync(string filePath)
    {
        try
        {
            StatusText.Text = "Loading file...";
            
            // Get ParquetService from DI
            var logger = App.Current.Services.GetService<ILogger<ParquetService>>();
            var parquetService = new ParquetService(logger!);
            
            // Load file info and data
            var fileInfo = await parquetService.GetFileInfoAsync(filePath);
            var dataTable = await parquetService.LoadParquetFileAsync(filePath);
            
            // Update schema panel
            UpdateSchemaPanel(filePath, fileInfo);
            
            // Setup data grid
            SetupDataGrid(dataTable, fileInfo.Columns);
            
            // Switch UI
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            DataGridContainer.Visibility = Visibility.Visible;
            
            StatusText.Text = $"Loaded {System.IO.Path.GetFileName(filePath)} - {fileInfo.RowCount:N0} rows, {fileInfo.Columns.Count} columns";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error loading file";
            
            // Reset UI
            EmptyStatePanel.Visibility = Visibility.Visible;
            DataGridContainer.Visibility = Visibility.Collapsed;
        }
    }
    
    private void UpdateSchemaPanel(string filePath, ParquetFileInfo fileInfo)
    {
        SchemaPanel.Children.Clear();
        
        // Title
        var titleBlock = new TextBlock
        {
            Text = "Schema",
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8)
        };
        SchemaPanel.Children.Add(titleBlock);
        
        // File info
        var fileBlock = new TextBlock
        {
            Text = $"📁 File: {System.IO.Path.GetFileName(filePath)}",
            Margin = new Thickness(0, 2, 0, 2)
        };
        SchemaPanel.Children.Add(fileBlock);
        
        var rowBlock = new TextBlock
        {
            Text = $"📊 Rows: {fileInfo.RowCount:N0}",
            Margin = new Thickness(0, 2, 0, 2)
        };
        SchemaPanel.Children.Add(rowBlock);
        
        var colHeaderBlock = new TextBlock
        {
            Text = $"📋 Columns ({fileInfo.Columns.Count}):",
            Margin = new Thickness(0, 8, 0, 4)
        };
        SchemaPanel.Children.Add(colHeaderBlock);
        
        // Column list
        foreach (var column in fileInfo.Columns)
        {
            var icon = GetTypeIcon(column.Type);
            var colBlock = new TextBlock
            {
                Text = $"  {icon} {column.Name} ({column.Type})",
                Margin = new Thickness(8, 2, 0, 2)
            };
            SchemaPanel.Children.Add(colBlock);
        }
    }
    
    private void SetupDataGrid(DataTable dataTable, List<ColumnInfo> columns)
    {
        try
        {
            _originalData = dataTable;
            _dataView = dataTable.DefaultView;
            
            // Clear existing
            DataGrid.Columns.Clear();
            SearchPanel.Children.Clear();
            _searchBoxes.Clear();
            
            // Add event handlers for safer sorting
            DataGrid.Sorting += OnDataGridSorting;
            
            // Display all columns
            for (int i = 0; i < dataTable.Columns.Count; i++)
            {
                var column = dataTable.Columns[i];
                var columnInfo = columns.FirstOrDefault(c => c.Name == column.ColumnName);
                
                // Create sortable DataGrid column with proper column name for sorting
                var gridColumn = new DataGridTextColumn
                {
                    Header = CreateColumnHeader(column.ColumnName, columnInfo?.Type ?? "unknown", i),
                    Binding = new System.Windows.Data.Binding($"[{column.ColumnName}]"),
                    Width = 150,
                    CanUserSort = true,
                    SortMemberPath = column.ColumnName
                };
                DataGrid.Columns.Add(gridColumn);
                
                // Create search box
                var searchBox = new TextBox
                {
                    Width = 150,
                    Margin = new Thickness(2),
                    Tag = column.ColumnName,
                    ToolTip = $"Search {column.ColumnName}..."
                };
                searchBox.TextChanged += OnSearchTextChanged;
                _searchBoxes.Add(searchBox);
                SearchPanel.Children.Add(searchBox);
            }
            
            // Set data source
            DataGrid.ItemsSource = _dataView;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error setting up data grid: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error displaying data";
        }
    }
    
    private FrameworkElement CreateColumnHeader(string name, string type, int index)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        var icon = new TextBlock
        {
            Text = GetTypeIcon(type),
            Margin = new Thickness(0, 0, 4, 0)
        };
        
        var text = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold
        };
        
        panel.Children.Add(icon);
        panel.Children.Add(text);
        
        return panel;
    }
    
    private void OnDataGridSorting(object sender, DataGridSortingEventArgs e)
    {
        try
        {
            // Handle sorting manually to prevent crashes
            e.Handled = true;
            
            if (_dataView == null) return;
            
            var column = e.Column;
            var sortMemberPath = column.SortMemberPath;
            
            if (string.IsNullOrEmpty(sortMemberPath)) return;
            
            // Determine sort direction
            ListSortDirection direction = ListSortDirection.Ascending;
            if (column.SortDirection == ListSortDirection.Ascending)
            {
                direction = ListSortDirection.Descending;
            }
            
            // Apply sort
            _dataView.Sort = $"{sortMemberPath} {(direction == ListSortDirection.Ascending ? "ASC" : "DESC")}";
            
            // Update column sort direction
            column.SortDirection = direction;
            
            StatusText.Text = $"Sorted by {sortMemberPath} ({(direction == ListSortDirection.Ascending ? "ascending" : "descending")})";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error sorting data: {ex.Message}", "Sort Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Sort failed";
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox searchBox && _dataView != null)
        {
            ApplyFilters();
        }
    }
    
    private void ApplyFilters()
    {
        if (_dataView == null) return;
        
        var filters = new List<string>();
        
        for (int i = 0; i < _searchBoxes.Count; i++)
        {
            var searchText = _searchBoxes[i].Text?.Trim();
            var columnName = _searchBoxes[i].Tag?.ToString();
            
            if (!string.IsNullOrEmpty(searchText) && !string.IsNullOrEmpty(columnName))
            {
                // Escape single quotes and create LIKE filter using column name
                var escapedText = searchText.Replace("'", "''");
                filters.Add($"Convert([{columnName}], 'System.String') LIKE '*{escapedText}*'");
            }
        }
        
        try
        {
            _dataView.RowFilter = filters.Count > 0 ? string.Join(" AND ", filters) : string.Empty;
            StatusText.Text = filters.Count > 0 ? $"Filtered by {filters.Count} column(s)" : "Ready";
        }
        catch (Exception ex)
        {
            // If filter fails, clear it
            _dataView.RowFilter = string.Empty;
            StatusText.Text = "Filter error - cleared";
        }
    }
    
    private string GetTypeIcon(string type)
    {
        return type.ToLower() switch
        {
            var t when t.Contains("int") || t.Contains("double") || t.Contains("float") => "🔢",
            var t when t.Contains("string") || t.Contains("varchar") => "📝",
            var t when t.Contains("date") || t.Contains("timestamp") => "📅",
            var t when t.Contains("bool") => "✅",
            _ => "🏷️"
        };
    }
}