using System.Windows;
using System.Windows.Controls;
using System.Data;
using Microsoft.Win32;
using System.ComponentModel;
using System.Collections.ObjectModel;
using HipHipParquet.Models;
using HipHipParquet.Services;
using HipHipParquet.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.IO;

namespace HipHipParquet.Views;

public partial class MainWindow : Window
{
    private DataTable? _originalData;
    private DataView? _dataView;
    private readonly List<TextBox> _searchBoxes = new();
    private ScrollViewer? _searchScrollViewer;
    private readonly List<string> _recentFiles = new();
    private const int MaxRecentFiles = 10;
    private const string RecentFilesKey = "RecentFiles";
    private string? _pendingFileToLoad;
    private string? _currentFilePath;
    private bool _hasUnsavedChanges = false;
    private QualityReviewViewModel? _qualityViewModel;
    private List<DataGridCellInfo>? _savedSelectedCells;
    private List<object>? _savedSelectedItems;

    // ── Row limiting ─────────────────────────────────────────────────────
    private const int RowLimitBatch = 50_000;
    private int _currentRowLimit = RowLimitBatch;
    private long _totalRowCount;
    private CsvImportOptions? _activeCsvOptions;
    private SupportedFileFormat _currentFormat;

    // ── File watcher ─────────────────────────────────────────────────────
    private FileSystemWatcher? _fileWatcher;

    // ── Stored service reference for reuse ───────────────────────────────
    private ParquetService? _parquetService;
    
    public MainWindow()
    {
        InitializeComponent();
        InitializeQualityPanel();
        LoadRecentFiles();
        UpdateRecentFilesMenu();
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }
    
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_hasUnsavedChanges)
        {
            var result = MessageBox.Show(
                "You have unsaved changes. Do you want to save before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                // Save the file
                if (string.IsNullOrEmpty(_currentFilePath))
                {
                    var saveFileDialog = new SaveFileDialog
                    {
                        Filter = Services.FileFormatDetector.GetSaveFileDialogFilter(),
                        Title = "Save Data File",
                        FileName = "untitled.parquet"
                    };
                    
                    if (saveFileDialog.ShowDialog() == true)
                    {
                        Task.Run(async () => await SaveFileAsync(saveFileDialog.FileName)).Wait();
                    }
                    else
                    {
                        e.Cancel = true; // Cancel closing if user cancels save dialog
                    }
                }
                else
                {
                    Task.Run(async () => await SaveFileAsync(_currentFilePath)).Wait();
                }
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true; // Cancel closing
            }
            // If No, just close without saving
        }

        // Dispose stored services
        _parquetService?.Dispose();
        _fileWatcher?.Dispose();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // If there's a pending file to load from command line, load it now
        if (!string.IsNullOrEmpty(_pendingFileToLoad))
        {
            var fileToLoad = _pendingFileToLoad;
            _pendingFileToLoad = null;
            await LoadFileAsync(fileToLoad);
        }
    }

    public Task LoadFileFromCommandLineAsync(string filePath)
    {
        // Store the file path to load after the window is loaded
        _pendingFileToLoad = filePath;
        return Task.CompletedTask;
    }

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = Services.FileFormatDetector.GetOpenFileDialogFilter(),
            Title = "Select Data File"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            await LoadFileAsync(openFileDialog.FileName);
        }
    }
    
    private void OnToggleSchemaPaneClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            if (menuItem.IsChecked)
            {
                // Show schema pane
                SchemaPane.Visibility = Visibility.Visible;
                SchemaSplitter.Visibility = Visibility.Visible;
                MainContentGrid.ColumnDefinitions[0].Width = new GridLength(250);
                MainContentGrid.ColumnDefinitions[0].MinWidth = 200;
                MainContentGrid.ColumnDefinitions[1].Width = new GridLength(5);
            }
            else
            {
                // Hide schema pane completely
                SchemaPane.Visibility = Visibility.Collapsed;
                SchemaSplitter.Visibility = Visibility.Collapsed;
                MainContentGrid.ColumnDefinitions[0].MinWidth = 0;
                MainContentGrid.ColumnDefinitions[0].Width = new GridLength(0);
                MainContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
            }
        }
    }
    
    private void OnToggleFilterRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            if (menuItem.IsChecked)
            {
                // Show filter row
                SearchPanelContainer.Visibility = Visibility.Visible;
            }
            else
            {
                // Hide filter row
                SearchPanelContainer.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnToggleQualityPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            if (menuItem.IsChecked)
            {
                // Show Quality panel
                QualityReviewPanel.Visibility = Visibility.Visible;
                QualitySplitter.Visibility = Visibility.Visible;
                QualitySplitterColumn.Width = new GridLength(5);
                QualityPaneColumn.MinWidth = 300;
                QualityPaneColumn.Width = new GridLength(420);
            }
            else
            {
                // Hide Quality panel
                QualityReviewPanel.Visibility = Visibility.Collapsed;
                QualitySplitter.Visibility = Visibility.Collapsed;
                QualitySplitterColumn.Width = new GridLength(0);
                QualityPaneColumn.MinWidth = 0;
                QualityPaneColumn.Width = new GridLength(0);
            }
        }
    }

    private void InitializeQualityPanel()
    {
        try
        {
            var logger = App.Current.Services.GetService<ILogger<QualityReviewViewModel>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityReviewViewModel>.Instance;
            var qualityScoreService = App.Current.Services.GetService<QualityScoreService>() ?? new QualityScoreService();
            var narrativeService = App.Current.Services.GetService<NarrativeService>() ?? new NarrativeService();
            var reportService = App.Current.Services.GetService<ReportService>() ?? new ReportService();

            _qualityViewModel = new QualityReviewViewModel(logger, qualityScoreService, narrativeService, reportService);
            QualityReviewPanel.SetViewModel(_qualityViewModel);
        }
        catch (Exception ex)
        {
            // Surface the failure in the status bar rather than silently swallowing it
            StatusText.Text = $"Quality panel unavailable: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Failed to initialize Quality panel: {ex.Message}");
        }
    }
    
    private void OnEditMenuOpened(object sender, RoutedEventArgs e)
    {
        _savedSelectedCells = DataGrid.SelectedCells.ToList();
        _savedSelectedItems = DataGrid.SelectedItems.Cast<object>().ToList();
    }

    private void OnEditMenuClosed(object sender, RoutedEventArgs e)
    {
        _savedSelectedCells = null;
        _savedSelectedItems = null;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard("\t"); // TSV format by default
    }
    
    private void OnCopyAsCsvClick(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard(",");
    }
    
    private void OnCopyAsTsvClick(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard("\t");
    }
    
    private void CopySelectionToClipboard(string delimiter)
    {
        CopySelectionToClipboard(delimiter, false);
    }
    
    private void CopySelectionToClipboard(string delimiter, bool includeHeaders)
    {
        try
        {
            var selectedCells = _savedSelectedCells ?? DataGrid.SelectedCells.ToList();
            if (selectedCells.Count == 0)
            {
                StatusText.Text = "No cells selected to copy";
                return;
            }
            
            // Special handling for single cell selection
            if (selectedCells.Count == 1 && !includeHeaders)
            {
                var cell = selectedCells[0];
                var cellValue = "";
                
                if (cell.Column is DataGridBoundColumn column)
                {
                    var binding = (column as DataGridTextColumn)?.Binding as System.Windows.Data.Binding;
                    if (binding != null && cell.Item is DataRowView rowView)
                    {
                        var columnName = binding.Path.Path.Trim('[', ']');
                        var value = rowView[columnName];
                        cellValue = value?.ToString() ?? "";
                    }
                }
                
                Clipboard.SetText(cellValue);
                StatusText.Text = "Copied cell value to clipboard";
                return;
            }
            
            // Special handling for single cell with header
            if (selectedCells.Count == 1 && includeHeaders)
            {
                var cell = selectedCells[0];
                var cellValue = "";
                var headerText = GetColumnHeaderText(cell.Column);
                
                if (cell.Column is DataGridBoundColumn column)
                {
                    var binding = (column as DataGridTextColumn)?.Binding as System.Windows.Data.Binding;
                    if (binding != null && cell.Item is DataRowView rowView)
                    {
                        var columnName = binding.Path.Path.Trim('[', ']');
                        var value = rowView[columnName];
                        cellValue = value?.ToString() ?? "";
                    }
                }
                
                var output = new System.Text.StringBuilder();
                output.AppendLine(headerText);
                output.Append(cellValue);
                
                Clipboard.SetText(output.ToString());
                StatusText.Text = "Copied cell value with header to clipboard";
                return;
            }
            
            // Group cells by row
            var rowGroups = selectedCells
                .GroupBy(cell => DataGrid.Items.IndexOf(cell.Item))
                .OrderBy(g => g.Key);
            
            var multiOutput = new System.Text.StringBuilder();
            
            // Add headers if requested
            if (includeHeaders)
            {
                var headerColumns = selectedCells
                    .Select(cell => cell.Column)
                    .Distinct()
                    .OrderBy(col => col.DisplayIndex)
                    .ToList();
                
                var headers = headerColumns.Select(col => GetColumnHeaderText(col)).ToList();
                multiOutput.AppendLine(string.Join(delimiter, headers));
            }
            
            foreach (var rowGroup in rowGroups)
            {
                var cellsInRow = rowGroup.OrderBy(cell => cell.Column.DisplayIndex).ToList();
                var values = new List<string>();
                
                foreach (var cell in cellsInRow)
                {
                    var cellValue = "";
                    if (cell.Column is DataGridBoundColumn column)
                    {
                        var binding = (column as DataGridTextColumn)?.Binding as System.Windows.Data.Binding;
                        if (binding != null && cell.Item is DataRowView rowView)
                        {
                            var columnName = binding.Path.Path.Trim('[', ']');
                            var value = rowView[columnName];
                            cellValue = value?.ToString() ?? "";
                        }
                    }
                    
                    // Escape value if it contains delimiter or quotes
                    if (delimiter == "," && (cellValue.Contains(",") || cellValue.Contains("\"") || cellValue.Contains("\n")))
                    {
                        cellValue = "\"" + cellValue.Replace("\"", "\"\"") + "\"";
                    }
                    
                    values.Add(cellValue);
                }
                
                multiOutput.AppendLine(string.Join(delimiter, values));
            }
            
            Clipboard.SetText(multiOutput.ToString());
            StatusText.Text = $"Copied {selectedCells.Count} cell(s) to clipboard" + (includeHeaders ? " with headers" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error copying to clipboard: {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Copy failed";
        }
    }
    
    private void OnContextCopyClick(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard("\t", false);
    }
    
    private void OnContextCopyWithHeadersClick(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard("\t", true);
    }
    
    private void OnContextCopyRowsClick(object sender, RoutedEventArgs e)
    {
        CopyRows(false);
    }
    
    private void OnContextCopyRowsWithHeadersClick(object sender, RoutedEventArgs e)
    {
        CopyRows(true);
    }
    
    private void OnContextCopyColumnsClick(object sender, RoutedEventArgs e)
    {
        CopyColumns(false);
    }
    
    private void OnContextCopyColumnsWithHeadersClick(object sender, RoutedEventArgs e)
    {
        CopyColumns(true);
    }
    
    private void CopyRows(bool includeHeaders)
    {
        try
        {
            var selectedItems = _savedSelectedItems ?? DataGrid.SelectedItems.Cast<object>().ToList();
            if (selectedItems.Count == 0)
            {
                StatusText.Text = "No rows selected to copy";
                return;
            }
            
            var output = new System.Text.StringBuilder();
            
            // Add headers if requested
            if (includeHeaders && DataGrid.Columns.Count > 0)
            {
                var headers = DataGrid.Columns
                    .Where(col => col.Visibility == Visibility.Visible)
                    .OrderBy(col => col.DisplayIndex)
                    .Select(col => GetColumnHeaderText(col))
                    .ToList();
                output.AppendLine(string.Join("\t", headers));
            }
            
            // Copy all columns for selected rows
            foreach (var item in selectedItems)
            {
                if (item is DataRowView rowView)
                {
                    var values = new List<string>();
                    foreach (var column in DataGrid.Columns.Where(col => col.Visibility == Visibility.Visible).OrderBy(col => col.DisplayIndex))
                    {
                        if (column is DataGridBoundColumn boundColumn)
                        {
                            var binding = (boundColumn as DataGridTextColumn)?.Binding as System.Windows.Data.Binding;
                            if (binding != null)
                            {
                                var columnName = binding.Path.Path.Trim('[', ']');
                                var value = rowView[columnName];
                                values.Add(value?.ToString() ?? "");
                            }
                        }
                    }
                    output.AppendLine(string.Join("\t", values));
                }
            }
            
            Clipboard.SetText(output.ToString());
            StatusText.Text = $"Copied {selectedItems.Count} row(s) to clipboard" + (includeHeaders ? " with headers" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error copying rows: {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Copy failed";
        }
    }
    
    private void CopyColumns(bool includeHeaders)
    {
        try
        {
            var selectedCells = _savedSelectedCells ?? DataGrid.SelectedCells.ToList();
            if (selectedCells.Count == 0)
            {
                StatusText.Text = "No cells selected to copy";
                return;
            }
            
            // Get unique columns from selected cells
            var selectedColumns = selectedCells
                .Select(cell => cell.Column)
                .Distinct()
                .OrderBy(col => col.DisplayIndex)
                .ToList();
            
            // Get all rows in the current view
            var allRows = new List<DataRowView>();
            foreach (var item in DataGrid.Items)
            {
                if (item is DataRowView rowView)
                {
                    allRows.Add(rowView);
                }
            }
            
            var output = new System.Text.StringBuilder();
            
            // Add headers if requested
            if (includeHeaders)
            {
                var headers = selectedColumns.Select(col => GetColumnHeaderText(col)).ToList();
                output.AppendLine(string.Join("\t", headers));
            }
            
            // Copy all rows for selected columns
            foreach (var rowView in allRows)
            {
                var values = new List<string>();
                foreach (var column in selectedColumns)
                {
                    if (column is DataGridBoundColumn boundColumn)
                    {
                        var binding = (boundColumn as DataGridTextColumn)?.Binding as System.Windows.Data.Binding;
                        if (binding != null)
                        {
                            var columnName = binding.Path.Path.Trim('[', ']');
                            var value = rowView[columnName];
                            values.Add(value?.ToString() ?? "");
                        }
                    }
                }
                output.AppendLine(string.Join("\t", values));
            }
            
            Clipboard.SetText(output.ToString());
            StatusText.Text = $"Copied {selectedColumns.Count} column(s) ({allRows.Count} rows) to clipboard" + (includeHeaders ? " with headers" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error copying columns: {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Copy failed";
        }
    }
    
    private string GetColumnHeaderText(DataGridColumn column)
    {
        if (column.Header is FrameworkElement element)
        {
            // Handle custom header with StackPanel (icon + text)
            if (element is StackPanel panel)
            {
                var textBlock = panel.Children.OfType<TextBlock>().LastOrDefault();
                return textBlock?.Text ?? column.Header.ToString() ?? "";
            }
        }
        return column.Header?.ToString() ?? "";
    }
    
    private void OnGlobalSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Just reapply all filters (column + global)
        ApplyFilters();
    }

    private async Task LoadFileAsync(string filePath, CsvImportOptions? csvOptions = null, int? rowLimit = null)
    {
        try
        {
            // Show loading overlay
            ShowLoading("Loading file...");

            var format = Services.FileFormatDetector.DetectFormat(filePath);
            _currentFormat = format;
            _activeCsvOptions = csvOptions;

            // Show info bar for unknown extensions defaulting to CSV (saved for after the main status line)
            bool isUnknownExtension = Services.FileFormatDetector.IsUnknownExtension(filePath);
            var unknownExt = isUnknownExtension ? System.IO.Path.GetExtension(filePath) : null;

            // Determine row limit
            var effectiveLimit = rowLimit ?? RowLimitBatch;
            _currentRowLimit = effectiveLimit;

            // Get ParquetService
            var logger = App.Current.Services.GetService<ILogger<ParquetService>>();
            _parquetService?.Dispose();
            _parquetService = new ParquetService(logger!);
            
            // Load file info and data (with row limit)
            var fileInfo = await _parquetService.GetFileInfoAsync(filePath, csvOptions);
            _totalRowCount = fileInfo.RowCount;

            ShowLoading($"Loading rows (limit: {effectiveLimit:N0})...");
            var dataTable = await _parquetService.LoadFileAsync(filePath, csvOptions, 
                _totalRowCount > effectiveLimit ? effectiveLimit : (int?)null);
            
            // Update schema panel
            UpdateSchemaPanel(filePath, fileInfo);
            
            // Setup data grid
            SetupDataGrid(dataTable, fileInfo.Columns);
            
            // Switch UI
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            DataGridContainer.Visibility = Visibility.Visible;

            // Update load-more banner
            UpdateLoadMoreBanner(dataTable.Rows.Count);
            
            // Add to recent files
            AddToRecentFiles(filePath);
            
            // Track current file and reset unsaved changes
            _currentFilePath = filePath;
            _hasUnsavedChanges = false;
            UpdateWindowTitle();
            EnableSaveMenuItems();

            // Update format badge
            UpdateFormatBadge(format);

            // Setup file watcher
            SetupFileWatcher(filePath);

            // Notify Quality panel of new file
            _qualityViewModel?.SetFilePath(filePath, csvOptions);
            
            StatusText.Text = $"Loaded {System.IO.Path.GetFileName(filePath)} — {dataTable.Rows.Count:N0}{(_totalRowCount > dataTable.Rows.Count ? $" of {_totalRowCount:N0}" : "")} rows, {fileInfo.Columns.Count} columns";

            // Append unknown-extension notice after the main status message
            if (unknownExt != null)
                StatusText.Text += $" — unknown extension '{unknownExt}' treated as CSV";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error loading file";
            
            // Reset UI
            EmptyStatePanel.Visibility = Visibility.Visible;
            DataGridContainer.Visibility = Visibility.Collapsed;
        }
        finally
        {
            HideLoading();
        }
    }
    
    private void UpdateWindowTitle()
    {
        const string appTitle = "HipHipParquet \u2014 Data Quality Viewer";
        var fileName = string.IsNullOrEmpty(_currentFilePath) ? appTitle : $"{System.IO.Path.GetFileName(_currentFilePath)} - {appTitle}";
        Title = _hasUnsavedChanges ? $"*{fileName}" : fileName;
    }
    
    private void EnableSaveMenuItems()
    {
        bool hasFile = !string.IsNullOrEmpty(_currentFilePath) && _originalData != null;
        SaveMenuItem.IsEnabled = hasFile;
        SaveAsMenuItem.IsEnabled = hasFile;
        ExportAsMenuItem.IsEnabled = hasFile;
        ImportOptionsMenuItem.IsEnabled = hasFile && 
            (_currentFormat == SupportedFileFormat.Csv || _currentFormat == SupportedFileFormat.Tsv);
        FlattenJsonMenuItem.IsEnabled = hasFile && _currentFormat == SupportedFileFormat.Json;
    }
    
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath) || _originalData == null)
        {
            OnSaveAsClick(sender, e);
            return;
        }
        
        await SaveFileAsync(_currentFilePath);
    }
    
    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (_originalData == null)
        {
            MessageBox.Show("No file is currently loaded.", "Save As", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        
        var saveFileDialog = new SaveFileDialog
        {
            Filter = Services.FileFormatDetector.GetSaveFileDialogFilter(),
            Title = "Save Data File",
            FileName = string.IsNullOrEmpty(_currentFilePath) ? "untitled.parquet" : System.IO.Path.GetFileName(_currentFilePath)
        };
        
        if (saveFileDialog.ShowDialog() == true)
        {
            await SaveFileAsync(saveFileDialog.FileName);
            _currentFilePath = saveFileDialog.FileName;
            UpdateWindowTitle();
        }
    }
    
    private async Task SaveFileAsync(string filePath)
    {
        try
        {
            ShowLoading("Saving file...");
            
            // Reuse stored service, or create one
            if (_parquetService == null)
            {
                var logger = App.Current.Services.GetService<ILogger<ParquetService>>();
                _parquetService = new ParquetService(logger!);
            }
            
            // Save the file
            await _parquetService.SaveFileAsync(filePath, _originalData!);
            
            _hasUnsavedChanges = false;
            UpdateWindowTitle();
            
            StatusText.Text = $"Saved {System.IO.Path.GetFileName(filePath)} — {_originalData!.Rows.Count:N0} rows";
            
            MessageBox.Show($"File saved successfully to:\n{filePath}", "Save Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error saving file: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error saving file";
        }
        finally
        {
            HideLoading();
        }
    }
    
    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            _hasUnsavedChanges = true;
            UpdateWindowTitle();
        }
    }

    private async void OnExportAsClick(object sender, RoutedEventArgs e)
    {
        if (_originalData == null)
        {
            MessageBox.Show("No file is currently loaded.", "Export As", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Filter = Services.FileFormatDetector.GetSaveFileDialogFilter(),
            Title = "Export As...",
            FileName = string.IsNullOrEmpty(_currentFilePath)
                ? "export"
                : System.IO.Path.GetFileNameWithoutExtension(_currentFilePath)
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                ShowLoading("Exporting file...");

                // Reuse the stored service, or create one if needed
                if (_parquetService == null)
                {
                    var logger = App.Current.Services.GetService<ILogger<ParquetService>>();
                    _parquetService = new ParquetService(logger!);
                }

                await _parquetService.SaveFileAsync(saveFileDialog.FileName, _originalData);

                var targetFormat = Services.FileFormatDetector.DetectFormat(saveFileDialog.FileName);
                var formatName = Services.FileFormatDetector.GetFormatDisplayName(targetFormat);
                StatusText.Text = $"Exported as {formatName} to {System.IO.Path.GetFileName(saveFileDialog.FileName)}";

                MessageBox.Show(
                    $"File exported successfully as {formatName} to:\n{saveFileDialog.FileName}",
                    "Export Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting file: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error exporting file";
            }
            finally
            {
                HideLoading();
            }
        }
    }
    
    private void UpdateSchemaPanel(string filePath, DataFileInfo fileInfo)
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
        var formatName = Services.FileFormatDetector.GetFormatDisplayName(fileInfo.Format);
        var fileBlock = new TextBlock
        {
            Text = $"📁 File: {System.IO.Path.GetFileName(filePath)}",
            Margin = new Thickness(0, 2, 0, 2)
        };
        SchemaPanel.Children.Add(fileBlock);

        var formatBlock = new TextBlock
        {
            Text = $"📋 Format: {formatName}",
            Margin = new Thickness(0, 2, 0, 2)
        };
        SchemaPanel.Children.Add(formatBlock);
        
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
    
    private void SetupDataGrid(DataTable dataTable, List<ColumnInfo>? columns)
    {
        try
        {
            _originalData = dataTable;
            _dataView = dataTable.DefaultView;
            
            // Add a row number column to the DataTable
            if (!dataTable.Columns.Contains("__RowNumber"))
            {
                var rowNumColumn = dataTable.Columns.Add("__RowNumber", typeof(int));
                rowNumColumn.SetOrdinal(0); // Move to first position
                
                for (int i = 0; i < dataTable.Rows.Count; i++)
                {
                    dataTable.Rows[i]["__RowNumber"] = i + 1;
                }
            }
            
            // Clear existing
            DataGrid.Columns.Clear();
            SearchPanel.Children.Clear();
            _searchBoxes.Clear();
            
            // Add event handlers for safer sorting
            DataGrid.Sorting += OnDataGridSorting;
            
            // Find the search ScrollViewer
            _searchScrollViewer = FindVisualChild<ScrollViewer>(DataGridContainer);
            
            // Add row number column
            var rowNumberColumn = new DataGridTextColumn
            {
                Header = "#",
                Width = 80,
                MinWidth = 40,
                IsReadOnly = true,
                CanUserSort = false,
                CanUserResize = true,
                Binding = new System.Windows.Data.Binding("[__RowNumber]")
            };
            
            // Style the row number column
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(240, 240, 240))));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            rowNumberColumn.HeaderStyle = headerStyle;
            
            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(250, 250, 250))));
            cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 100, 100))));
            cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            rowNumberColumn.CellStyle = cellStyle;
            
            DataGrid.Columns.Add(rowNumberColumn);
            
            // Add empty space in search panel for row number column
            var rowNumberSpacer = new Border
            {
                MinWidth = 40,
                Background = new SolidColorBrush(Color.FromRgb(248, 248, 248))
            };
            
            // Bind the spacer width to the row number column width
            var spacerBinding = new System.Windows.Data.Binding("ActualWidth")
            {
                Source = rowNumberColumn,
                Mode = System.Windows.Data.BindingMode.OneWay
            };
            rowNumberSpacer.SetBinding(FrameworkElement.WidthProperty, spacerBinding);
            
            SearchPanel.Children.Add(rowNumberSpacer);
            
            // Display all columns (except the internal __RowNumber column)
            for (int i = 0; i < dataTable.Columns.Count; i++)
            {
                var column = dataTable.Columns[i];
                
                // Skip the internal row number column
                if (column.ColumnName == "__RowNumber")
                    continue;
                
                var columnInfo = columns?.FirstOrDefault(c => c.Name == column.ColumnName);
                
                // Create sortable DataGrid column with proper column name for sorting
                var gridColumn = new DataGridTextColumn
                {
                    Header = CreateColumnHeader(column.ColumnName, columnInfo?.Type ?? "unknown", i),
                    Binding = new System.Windows.Data.Binding($"[{column.ColumnName}]"),
                    Width = DataGridLength.Auto,
                    MinWidth = 100,
                    CanUserSort = true,
                    CanUserResize = true,
                    SortMemberPath = column.ColumnName
                };
                DataGrid.Columns.Add(gridColumn);
                
                // Create search box that matches column width
                var searchBox = new TextBox
                {
                    Margin = new Thickness(0, 2, 0, 2),
                    Tag = column.ColumnName,
                    ToolTip = $"Search {column.ColumnName}...",
                    MinWidth = 100
                };
                
                // Bind the search box width to the column width
                var binding = new System.Windows.Data.Binding("ActualWidth")
                {
                    Source = gridColumn,
                    Mode = System.Windows.Data.BindingMode.OneWay
                };
                searchBox.SetBinding(FrameworkElement.WidthProperty, binding);
                
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
        
        // Add column-specific filters from search boxes
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
        
        // Add global search filter (OR condition across all data columns)
        var globalSearchText = GlobalSearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(globalSearchText) && _originalData != null)
        {
            var globalConditions = new List<string>();
            var escapedGlobalText = globalSearchText.Replace("'", "''");
            
            foreach (DataColumn col in _originalData.Columns)
            {
                if (col.ColumnName != "__RowNumber")
                {
                    globalConditions.Add($"Convert([{col.ColumnName}], 'System.String') LIKE '*{escapedGlobalText}*'");
                }
            }
            
            if (globalConditions.Count > 0)
            {
                filters.Add($"({string.Join(" OR ", globalConditions)})");
            }
        }
        
        try
        {
            _dataView.RowFilter = filters.Count > 0 ? string.Join(" AND ", filters) : string.Empty;
            
            var columnCount = _searchBoxes.Count(sb => !string.IsNullOrWhiteSpace(sb.Text));
            var hasGlobal = !string.IsNullOrWhiteSpace(GlobalSearchBox.Text);
            
            if (columnCount > 0 && hasGlobal)
                StatusText.Text = $"Filtered by {columnCount} column(s) + global search";
            else if (columnCount > 0)
                StatusText.Text = $"Filtered by {columnCount} column(s)";
            else if (hasGlobal)
                StatusText.Text = "Filtered by global search";
            else
                StatusText.Text = "Ready";
        }
        catch
        {
            // If filter fails, clear it
            _dataView.RowFilter = string.Empty;
            StatusText.Text = "Filter error - cleared";
        }
    }
    
    private void OnDataGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Sync horizontal scroll between search panel and data grid
        if (_searchScrollViewer != null && e.HorizontalChange != 0)
        {
            _searchScrollViewer.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }
    
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;
            
            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }
        return null;
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
    
    private void LoadRecentFiles()
    {
        try
        {
            var recentFilesJson = Properties.Settings.Default.RecentFiles;
            if (!string.IsNullOrEmpty(recentFilesJson))
            {
                var files = System.Text.Json.JsonSerializer.Deserialize<List<string>>(recentFilesJson);
                if (files != null)
                {
                    _recentFiles.Clear();
                    _recentFiles.AddRange(files.Where(f => System.IO.File.Exists(f)).Take(MaxRecentFiles));
                }
            }
        }
        catch
        {
            // Ignore errors loading recent files
        }
    }
    
    private void SaveRecentFiles()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_recentFiles);
            Properties.Settings.Default.RecentFiles = json;
            Properties.Settings.Default.Save();
        }
        catch
        {
            // Ignore errors saving recent files
        }
    }
    
    private void AddToRecentFiles(string filePath)
    {
        // Remove if already exists
        _recentFiles.Remove(filePath);
        
        // Add to top
        _recentFiles.Insert(0, filePath);
        
        // Limit to max
        if (_recentFiles.Count > MaxRecentFiles)
        {
            _recentFiles.RemoveAt(_recentFiles.Count - 1);
        }
        
        SaveRecentFiles();
        UpdateRecentFilesMenu();
    }
    
    private void UpdateRecentFilesMenu()
    {
        RecentFilesMenuItem.Items.Clear();
        
        if (_recentFiles.Count == 0)
        {
            var emptyItem = new MenuItem { Header = "(No recent files)", IsEnabled = false };
            RecentFilesMenuItem.Items.Add(emptyItem);
        }
        else
        {
            for (int i = 0; i < _recentFiles.Count; i++)
            {
                var filePath = _recentFiles[i];
                var fileName = System.IO.Path.GetFileName(filePath);
                var menuItem = new MenuItem
                {
                    Header = $"_{i + 1}. {fileName}",
                    ToolTip = filePath,
                    Tag = filePath
                };
                menuItem.Click += OnRecentFileClick;
                RecentFilesMenuItem.Items.Add(menuItem);
            }
            
            RecentFilesMenuItem.Items.Add(new Separator());
            
            var clearItem = new MenuItem { Header = "Clear Recent Files" };
            clearItem.Click += (s, e) =>
            {
                _recentFiles.Clear();
                SaveRecentFiles();
                UpdateRecentFilesMenu();
            };
            RecentFilesMenuItem.Items.Add(clearItem);
        }
    }
    
    private async void OnRecentFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string filePath)
        {
            if (System.IO.File.Exists(filePath))
            {
                await LoadFileAsync(filePath);
            }
            else
            {
                MessageBox.Show($"File not found: {filePath}", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                _recentFiles.Remove(filePath);
                SaveRecentFiles();
                UpdateRecentFilesMenu();
            }
        }
    }

    // ── Drag-and-Drop ───────────────────────────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length == 1)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length == 1)
            {
                await LoadFileAsync(files[0]);
            }
        }
    }

    // ── Import Options (re-import CSV/TSV with custom settings) ─────────

    private async void OnImportOptionsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;

        var format = Services.FileFormatDetector.DetectFormat(_currentFilePath);
        if (format != SupportedFileFormat.Csv && format != SupportedFileFormat.Tsv)
        {
            MessageBox.Show("Import options are only available for CSV and TSV files.",
                "Import Options", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new CsvOptionsDialog { Owner = this };
        if (format == SupportedFileFormat.Tsv)
            dialog.PreSelectTsv();

        // Pass the file path for preview
        dialog.SetPreviewFile(_currentFilePath);

        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            // Re-import with the new options
            await LoadFileAsync(_currentFilePath, dialog.Result);
        }
    }

    // ── Row Limiting / Load More ────────────────────────────────────────

    private void UpdateLoadMoreBanner(int loadedRows)
    {
        if (_totalRowCount > loadedRows)
        {
            LoadMoreBanner.Visibility = Visibility.Visible;
            LoadMoreText.Text = $"Showing {loadedRows:N0} of {_totalRowCount:N0} total rows";
        }
        else
        {
            LoadMoreBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        _currentRowLimit += RowLimitBatch;
        await LoadMoreRowsAsync();
    }

    /// <summary>
    /// Lean pagination helper: reloads only the data rows without re-fetching file metadata,
    /// re-building the schema panel, or re-running file-format detection. Significantly faster
    /// than a full <see cref="LoadFileAsync"/> for large files with many columns.
    /// </summary>
    private async Task LoadMoreRowsAsync()
    {
        if (string.IsNullOrEmpty(_currentFilePath) || _parquetService == null) return;

        try
        {
            ShowLoading($"Loading rows (limit: {_currentRowLimit:N0})...");

            var dataTable = await _parquetService.LoadFileAsync(
                _currentFilePath,
                _activeCsvOptions,
                _totalRowCount > _currentRowLimit ? _currentRowLimit : (int?)null);

            SetupDataGrid(dataTable, null);
            UpdateLoadMoreBanner(dataTable.Rows.Count);

            StatusText.Text = $"Loaded {System.IO.Path.GetFileName(_currentFilePath)} — {dataTable.Rows.Count:N0}{(_totalRowCount > dataTable.Rows.Count ? $" of {_totalRowCount:N0}" : "")} rows";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading additional rows: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnLoadAllClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;

        if (_totalRowCount > 500_000)
        {
            var result = MessageBox.Show(
                $"This file has {_totalRowCount:N0} rows. Loading all rows may cause the application to become slow or unresponsive.\n\nContinue?",
                "Large File Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _currentRowLimit = _totalRowCount > int.MaxValue ? int.MaxValue : (int)_totalRowCount;
        await LoadFileAsync(_currentFilePath, _activeCsvOptions, (int?)null);
    }

    // ── JSON Flattening ───────────────────────────────────────────────

    private async void OnFlattenJsonClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath) || _parquetService == null) return;

        try
        {
            ShowLoading("Detecting nested structures...");

            var flatQuery = await _parquetService.GetFlattenedQueryAsync(_currentFilePath, _activeCsvOptions);

            if (flatQuery == null)
            {
                MessageBox.Show("No nested STRUCT columns were detected in this JSON file.",
                    "Flatten JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                "Nested STRUCT columns were detected. Flatten them into separate columns?\n\n" +
                "This will reload the data with nested fields expanded (e.g., address.city becomes address_city).",
                "Flatten Nested JSON", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            ShowLoading("Flattening nested JSON...");

            var logger = App.Current.Services.GetService<ILogger<ParquetService>>();
            _parquetService?.Dispose();
            _parquetService = new ParquetService(logger!);

            var dataTable = await _parquetService.LoadWithQueryAsync(flatQuery, 
                _totalRowCount > _currentRowLimit ? _currentRowLimit : (int?)null);

            var columns = dataTable.Columns.Cast<DataColumn>()
                .Select(c => new ColumnInfo { Name = c.ColumnName, Type = c.DataType.Name, Nullable = true })
                .ToList();

            SetupDataGrid(dataTable, columns);
            UpdateLoadMoreBanner(dataTable.Rows.Count);

            StatusText.Text = $"Flattened JSON — {dataTable.Rows.Count:N0} rows, {dataTable.Columns.Count} columns";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error flattening JSON: {ex.Message}", "Flatten Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideLoading();
        }
    }

    // ── Loading Overlay ─────────────────────────────────────────────────

    private void ShowLoading(string message)
    {
        LoadingText.Text = message;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoading()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    // ── Format Badge ────────────────────────────────────────────────────

    private void UpdateFormatBadge(SupportedFileFormat format)
    {
        var (bg, fg) = Services.FileFormatDetector.GetFormatBadgeColors(format);
        FormatBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        FormatBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        FormatBadgeText.Text = Services.FileFormatDetector.GetFormatDisplayName(format);
        FormatBadge.Visibility = Visibility.Visible;
    }

    // ── File Watcher ────────────────────────────────────────────────────

    private void SetupFileWatcher(string filePath)
    {
        // Dispose any previous watcher
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(filePath);
            var name = System.IO.Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

            _fileWatcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _fileWatcher.Changed += OnFileChanged;
        }
        catch
        {
            // Silently ignore if watcher can't be set up (e.g. network paths)
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: FileSystemWatcher can fire multiple times for one save
        _fileWatcher!.EnableRaisingEvents = false;

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var result = MessageBox.Show(
                    $"The file '{System.IO.Path.GetFileName(e.FullPath)}' has been modified externally.\n\nReload the file?",
                    "File Changed", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await LoadFileAsync(e.FullPath, _activeCsvOptions, _currentRowLimit);
                }
            }
            finally
            {
                if (_fileWatcher != null)
                    _fileWatcher.EnableRaisingEvents = true;
            }
        });
    }
}