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

using System.Windows.Input;

namespace HipHipParquet.Views;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand GoToRowCommand = new("Go to Row", "GoToRow", typeof(MainWindow));
    public static readonly RoutedUICommand FocusSearchCommand = new("Focus Search", "FocusSearch", typeof(MainWindow));

    private DataTable? _originalData;
    private DataView? _dataView;
    private readonly List<string> _recentFiles = new();
    private const int MaxRecentFiles = 10;
    private const string RecentFilesKey = "RecentFiles";
    private string? _pendingFileToLoad;
    private string? _currentFilePath;
    private bool _hasUnsavedChanges = false;
    private QualityReviewViewModel? _qualityViewModel;
    private List<DataGridCellInfo>? _savedSelectedCells;
    private List<object>? _savedSelectedItems;
    private bool _closingConfirmed = false;

    // ── Column filter state (Fabric Lakehouse-style dropdowns) ────────────
    private readonly Dictionary<string, ColumnFilterState> _columnFilters = new();
    private const int MaxDistinctValues = 500;
    private const string BlankDisplayValue = "(Blank)";

    // ── Filter / sort state (preserved across reloads) ────────────────────
    private readonly Dictionary<string, HashSet<string>> _savedColumnFilterSelections = new();
    private string _savedGlobalSearch = string.Empty;
    private string _savedSort = string.Empty;
    // ── Row limiting ─────────────────────────────────────────────────────
    private const int RowLimitBatch = 50_000;
    private int _currentRowLimit = RowLimitBatch;
    private long _totalRowCount;
    private CsvImportOptions? _activeCsvOptions;
    private JsonImportOptions? _activeJsonOptions;
    private SupportedFileFormat _currentFormat;

    // ── File watcher ─────────────────────────────────────────────────────
    private FileSystemWatcher? _fileWatcher;

    // ── Stored service reference for reuse ───────────────────────────────
    private ParquetService? _parquetService;

    // ── Search debounce ───────────────────────────────────────────────────
    private readonly System.Windows.Threading.DispatcherTimer _filterDebounceTimer;
    
    public MainWindow()
    {
        InitializeComponent();
        InitializeQualityPanel();
        LoadRecentFiles();
        UpdateRecentFilesMenu();
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;

        // Search debounce: wait 300 ms after last keystroke before filtering.
        _filterDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _filterDebounceTimer.Tick += (_, _) =>
        {
            _filterDebounceTimer.Stop();
            ApplyAllFilters();
        };
    }
    
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            // Second pass — let the close proceed and clean up.
            _parquetService?.Dispose();
            _fileWatcher?.Dispose();
            return;
        }

        if (!_hasUnsavedChanges)
        {
            _parquetService?.Dispose();
            _fileWatcher?.Dispose();
            return;
        }

        // Cancel initially so we can run async save without deadlocking.
        e.Cancel = true;

        var result = MessageBox.Show(
            "You have unsaved changes. Do you want to save before closing?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
            return;

        if (result == MessageBoxResult.Yes)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = Services.FileFormatDetector.GetSaveFileDialogFilter(),
                    Title = "Save Data File",
                    FileName = "untitled.parquet"
                };

                if (saveFileDialog.ShowDialog() == true)
                    await SaveFileAsync(saveFileDialog.FileName);
                else
                    return; // user cancelled save dialog → keep window open
            }
            else
            {
                await SaveFileAsync(_currentFilePath);
            }
        }

        // "No" or save complete — now close for real.
        _closingConfirmed = true;
        Close();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // If there's a pending file to load from command line, load it now
        if (!string.IsNullOrEmpty(_pendingFileToLoad))
        {
            var fileToLoad = _pendingFileToLoad;
            _pendingFileToLoad = null;

            var format = Services.FileFormatDetector.DetectFormat(fileToLoad);
            if (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv || format == SupportedFileFormat.Json)
            {
                var result = ShowFileImportDialog(fileToLoad, format);
                if (result.Imported)
                    await LoadFileAsync(fileToLoad, result.CsvOptions, jsonOptions: result.JsonOptions);
            }
            else
            {
                await LoadFileAsync(fileToLoad);
            }
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
            var filePath = openFileDialog.FileName;
            var format = Services.FileFormatDetector.DetectFormat(filePath);

            // Show the import settings dialog for CSV/TSV/JSON files
            if (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv || format == SupportedFileFormat.Json)
            {
                var result = ShowFileImportDialog(filePath, format);
                if (!result.Imported) return;

                await LoadFileAsync(filePath, result.CsvOptions, jsonOptions: result.JsonOptions);
            }
            else
            {
                await LoadFileAsync(filePath);
            }
        }
    }

    /// <summary>
    /// Shows the unified FileImportDialog for CSV/TSV/JSON files and returns the result.
    /// </summary>
    private (bool Imported, CsvImportOptions? CsvOptions, JsonImportOptions? JsonOptions) ShowFileImportDialog(
        string filePath, SupportedFileFormat format,
        CsvImportOptions? existingCsvOptions = null, JsonImportOptions? existingJsonOptions = null)
    {
        var dialog = new FileImportDialog { Owner = this };
        dialog.SetFile(filePath, format);

        if (existingCsvOptions != null)
            dialog.PrePopulateCsvOptions(existingCsvOptions);
        if (existingJsonOptions != null)
            dialog.PrePopulateJsonOptions(existingJsonOptions);

        if (dialog.ShowDialog() == true)
            return (true, dialog.CsvResult, dialog.JsonResult);

        return (false, null, null);
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

            // attach a few helper handlers once the grid exists
            DataGrid.PreviewKeyDown += OnDataGridPreviewKeyDown;
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

            var text = CopyHelper.FormatCells(DataGrid, selectedCells, delimiter, includeHeaders);
            Clipboard.SetText(text);
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

    private void OnContextCopyAsCsvClick(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard(",", false);
    }

    private void OnContextCopyAsCsvWithHeadersClick(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard(",", true);
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
            // Handle new Grid-based column header (DockPanel with icon + name TextBlocks)
            if (element is Grid grid)
            {
                var namePanel = grid.Children.OfType<DockPanel>().FirstOrDefault();
                if (namePanel != null)
                {
                    var nameBlock = namePanel.Children.OfType<TextBlock>().LastOrDefault();
                    return nameBlock?.Text ?? "";
                }
            }
            // Handle legacy StackPanel header (icon + text)
            if (element is StackPanel panel)
            {
                var textBlock = panel.Children.OfType<TextBlock>().LastOrDefault();
                return textBlock?.Text ?? column.Header.ToString() ?? "";
            }
        }
        return column.Header?.ToString() ?? "";
    }
    
    /// <summary>Escapes characters that have special meaning in DataView LIKE expressions.</summary>
    private static string EscapeDataViewLikeValue(string value) =>
        value.Replace("[", "[[]")
             .Replace("*", "[*]")
             .Replace("%", "[%]");

    private void OnGlobalSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Debounce: restart the timer on every keystroke.
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void OnDataGridPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (mods == System.Windows.Input.ModifierKeys.Control && e.Key == System.Windows.Input.Key.C)
        {
            // plain copy
            CopySelectionToClipboard("\t", false);
            e.Handled = true;
        }
        else if (mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) && e.Key == System.Windows.Input.Key.C)
        {
            // copy with headers
            CopySelectionToClipboard("\t", true);
            e.Handled = true;
        }
        else if (mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt) && e.Key == System.Windows.Input.Key.C)
        {
            // copy as CSV
            CopySelectionToClipboard(",", false);
            e.Handled = true;
        }
    }

    private async Task LoadFileAsync(string filePath, CsvImportOptions? csvOptions = null, int? rowLimit = null, JsonImportOptions? jsonOptions = null)
    {
        try
        {
            ShowLoading("Loading file...");

            // Detect format once at the start
            _currentFormat = Services.FileFormatDetector.DetectFormat(filePath);

            // Retry loop: keeps the overlay showing throughout all attempts
            while (true)
            {
                try
                {
                    await LoadFileInternalAsync(filePath, csvOptions, rowLimit, jsonOptions);
                    return;  // Success
                }
                catch (Exception ex)
                {
                    // For CSV/TSV/JSON, offer to retry with adjusted settings
                    if (_currentFormat == SupportedFileFormat.Csv || _currentFormat == SupportedFileFormat.Tsv || _currentFormat == SupportedFileFormat.Json)
                    {
                        var retry = MessageBox.Show(
                            $"Error loading file: {ex.Message}\n\nWould you like to return to the import settings to adjust options (e.g. enable 'Skip malformed rows')?",
                            "Import Error",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (retry == MessageBoxResult.Yes)
                        {
                            var result = ShowFileImportDialog(filePath, _currentFormat, csvOptions, jsonOptions);
                            if (result.Imported)
                            {
                                // Update options for next retry iteration and loop
                                csvOptions = result.CsvOptions;
                                jsonOptions = result.JsonOptions;
                                continue;
                            }
                        }
                    }
                    else
                    {
                        MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    // If we get here, either non-CSV format or user declined retry — give up
                    StatusText.Text = "Error loading file";
                    EmptyStatePanel.Visibility = Visibility.Visible;
                    DataGridContainer.Visibility = Visibility.Collapsed;
                    return;
                }
            }
        }
        finally
        {
            HideLoading();  // Always hide overlay exactly once after all retries complete
        }
    }

    /// <summary>
    /// Core file loading logic: reads file data, updates UI, and populates grid.
    /// Called by LoadFileAsync in a retry loop so retries don't add stack frames.
    /// </summary>
    private async Task LoadFileInternalAsync(string filePath, CsvImportOptions? csvOptions, int? rowLimit, JsonImportOptions? jsonOptions)
    {
        var format = _currentFormat;
        _activeCsvOptions = csvOptions;
        _activeJsonOptions = jsonOptions;

        // Show info bar for unknown extensions defaulting to CSV (saved for after the main status line)
        bool isUnknownExtension = Services.FileFormatDetector.IsUnknownExtension(filePath);
        var unknownExt = isUnknownExtension ? System.IO.Path.GetExtension(filePath) : null;

        // Determine row limit
        var effectiveLimit = rowLimit ?? RowLimitBatch;
        _currentRowLimit = effectiveLimit;

        // Get ParquetService
        var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
        _parquetService?.Dispose();
        _parquetService = new ParquetService(logger);
        
        // Load file info and data (with row limit)
        var fileInfo = await _parquetService.GetFileInfoAsync(filePath, csvOptions, jsonOptions);
        _totalRowCount = fileInfo.RowCount;

        ShowLoading($"Loading rows (limit: {effectiveLimit:N0})...");
        var dataTable = await _parquetService.LoadFileAsync(filePath, csvOptions, 
            _totalRowCount > effectiveLimit ? effectiveLimit : (int?)null, jsonOptions);

        // Compute row-number column on a background thread (avoids stalling the UI for large files).
        ShowLoading("Indexing rows...");
        await Task.Run(() =>
        {
            if (!dataTable.Columns.Contains("__RowNumber"))
            {
                var rowNumColumn = dataTable.Columns.Add("__RowNumber", typeof(int));
                rowNumColumn.SetOrdinal(0);
                for (int i = 0; i < dataTable.Rows.Count; i++)
                    dataTable.Rows[i]["__RowNumber"] = i + 1;
            }
        });
        
        // Update schema panel
        UpdateSchemaPanel(filePath, fileInfo);
        
        // Setup data grid (row-number column already present)
        ShowLoading("Building grid...");
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
        _qualityViewModel?.SetFilePath(filePath, csvOptions, jsonOptions);
        
        StatusText.Text = $"Loaded {System.IO.Path.GetFileName(filePath)} — {dataTable.Rows.Count:N0}{(_totalRowCount > dataTable.Rows.Count ? $" of {_totalRowCount:N0}" : "")} rows, {fileInfo.Columns.Count} columns";

        // Append unknown-extension notice after the main status message
        if (unknownExt != null)
            StatusText.Text += $" — unknown extension '{unknownExt}' treated as CSV";
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
            (_currentFormat == SupportedFileFormat.Csv || _currentFormat == SupportedFileFormat.Tsv || _currentFormat == SupportedFileFormat.Json);
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
            await SaveFileAsync(saveFileDialog.FileName, showConfirmation: true);
            _currentFilePath = saveFileDialog.FileName;
            UpdateWindowTitle();
        }
    }
    
    private async Task SaveFileAsync(string filePath, bool showConfirmation = false)
    {
        try
        {
            if (_originalData == null)
            {
                StatusText.Text = "Nothing to save";
                return;
            }

            ShowLoading("Saving file...");
            
            // Reuse stored service, or create one
            if (_parquetService == null)
            {
                var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
                _parquetService = new ParquetService(logger);
            }
            
            // Save the file
            await _parquetService.SaveFileAsync(filePath, _originalData);
            
            _hasUnsavedChanges = false;
            UpdateWindowTitle();
            
            StatusText.Text = $"Saved {System.IO.Path.GetFileName(filePath)} — {_originalData.Rows.Count:N0} rows";

            if (showConfirmation)
                MessageBox.Show($"File saved to:\n{filePath}", "Save Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
                    _parquetService = new ParquetService(logger);
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
    
    private DataFileInfo? _lastSchemaInfo;
    private string? _lastSchemaFilePath;

    private void UpdateSchemaPanel(string filePath, DataFileInfo fileInfo)
    {
        _lastSchemaInfo = fileInfo;
        _lastSchemaFilePath = filePath;
        CopySchemaButton.IsEnabled = true;

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

    private void OnCopySchemaClick(object sender, RoutedEventArgs e)
    {
        if (_lastSchemaInfo == null || _lastSchemaFilePath == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"File: {System.IO.Path.GetFileName(_lastSchemaFilePath)}");
        sb.AppendLine($"Format: {Services.FileFormatDetector.GetFormatDisplayName(_lastSchemaInfo.Format)}");
        sb.AppendLine($"Rows: {_lastSchemaInfo.RowCount:N0}");
        sb.AppendLine($"Columns ({_lastSchemaInfo.Columns.Count}):");
        foreach (var col in _lastSchemaInfo.Columns)
            sb.AppendLine($"  {col.Name} ({col.Type})");

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText.Text = "Schema copied to clipboard";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to copy schema to clipboard";
            MessageBox.Show($"Error copying schema to clipboard: {ex.Message}", "Clipboard Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    private void SetupDataGrid(DataTable dataTable, List<ColumnInfo>? columns)
    {
        try
        {
            SnapshotFilterState();

            _originalData = dataTable;
            _dataView = dataTable.DefaultView;
            
            // __RowNumber column is already added by LoadFileAsync (on a background thread).
            
            // Clear existing
            DataGrid.Columns.Clear();
            _columnFilters.Clear();
            
            // Guard against double-subscription on repeated loads
            DataGrid.Sorting -= OnDataGridSorting;
            DataGrid.Sorting += OnDataGridSorting;
            
            // Row number column
            var rowNumberColumn = CreateRowNumberColumn();
            DataGrid.Columns.Add(rowNumberColumn);
            
            // Data columns with filter dropdown headers
            for (int i = 0; i < dataTable.Columns.Count; i++)
            {
                var column = dataTable.Columns[i];
                if (column.ColumnName == "__RowNumber") continue;

                var columnInfo = columns?.FirstOrDefault(c => c.Name == column.ColumnName);
                var gridColumn = CreateDataColumn(column, columnInfo, i);
                DataGrid.Columns.Add(gridColumn);
            }
            
            DataGrid.ItemsSource = _dataView;

            RestoreFilterState();
            UpdateRowCount();
            UpdateFilterBadge();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error setting up data grid: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error displaying data";
        }
    }

    private DataGridTextColumn CreateRowNumberColumn()
    {
        var col = new DataGridTextColumn
        {
            Header = CreateRowNumberHeader(),
            Width = 80,
            MinWidth = 40,
            IsReadOnly = true,
            CanUserSort = true,
            SortMemberPath = "__RowNumber",
            CanUserResize = true,
            Binding = new System.Windows.Data.Binding("[__RowNumber]")
        };

        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(240, 240, 240))));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        col.HeaderStyle = headerStyle;

        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(250, 250, 250))));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 100, 100))));
        cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        col.CellStyle = cellStyle;

        return col;
    }

    private FrameworkElement CreateRowNumberHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "#",
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var goToButton = new Button
        {
            Content = "\u25BC", // ▼
            FontSize = 8,
            Padding = new Thickness(3, 1, 3, 1),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            ToolTip = "Go to row...",
            Tag = "GoToRowButton"
        };
        goToButton.Click += OnGoToRowButtonClick;
        Grid.SetColumn(goToButton, 1);
        grid.Children.Add(goToButton);

        return grid;
    }

    private void OnGoToRowButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (_originalData == null || _dataView == null) return;

        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };

        var border = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Width = 200,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                Opacity = 0.2,
                ShadowDepth = 2,
                Color = Colors.Black
            }
        };

        var mainPanel = new StackPanel();

        // Sort buttons
        mainPanel.Children.Add(CreateSortButtonsPanel("__RowNumber", popup));

        var header = new TextBlock
        {
            Text = "Go to Row",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6)
        };
        mainPanel.Children.Add(header);

        var totalRows = _originalData.Rows.Count;
        var hint = new TextBlock
        {
            Text = $"Enter row number (1–{totalRows:N0})",
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };
        mainPanel.Children.Add(hint);

        var rowInput = new TextBox
        {
            Padding = new Thickness(4, 3, 4, 3),
            FontSize = 12
        };
        mainPanel.Children.Add(rowInput);

        var errorText = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Red,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed
        };
        mainPanel.Children.Add(errorText);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var goButton = new Button
        {
            Content = "Go",
            Padding = new Thickness(16, 4, 16, 4),
            Background = new SolidColorBrush(Color.FromRgb(81, 43, 212)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 12
        };

        void DoGoToRow()
        {
            if (!int.TryParse(rowInput.Text?.Trim(), out int targetRow) || targetRow < 1 || targetRow > totalRows)
            {
                errorText.Text = $"Enter a number between 1 and {totalRows:N0}";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            // Locate the row in the current (filtered/sorted) view by __RowNumber
            int matchedIndex = -1;
            for (int i = 0; i < _dataView.Count; i++)
            {
                if (_dataView[i]["__RowNumber"] is int rowNum && rowNum == targetRow)
                {
                    matchedIndex = i;
                    break;
                }
            }

            if (matchedIndex == -1)
            {
                errorText.Text = $"Row {targetRow:N0} is not visible in the current view.";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            popup.IsOpen = false;

            if (matchedIndex < DataGrid.Items.Count)
            {
                DataGrid.ScrollIntoView(DataGrid.Items[matchedIndex]);
                DataGrid.SelectedIndex = matchedIndex;
                DataGrid.Focus();
                StatusText.Text = $"Jumped to row {targetRow:N0}";
            }
        }

        goButton.Click += (s, _) => DoGoToRow();
        rowInput.KeyDown += (s, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter)
                DoGoToRow();
        };

        buttonPanel.Children.Add(goButton);
        mainPanel.Children.Add(buttonPanel);

        border.Child = mainPanel;
        popup.Child = border;
        popup.IsOpen = true;

        // Focus the input when popup opens
        popup.Opened += (s, _) => rowInput.Focus();
    }

    private void OnGoToRowShortcut(object sender, ExecutedRoutedEventArgs e)
    {
        if (_originalData == null || _dataView == null) return;

        // Find the go-to-row button in the # column header and simulate a click
        foreach (var col in DataGrid.Columns)
        {
            if (col.SortMemberPath == "__RowNumber" && col.Header is Grid headerGrid)
            {
                foreach (var child in headerGrid.Children)
                {
                    if (child is Button btn && btn.Tag?.ToString() == "GoToRowButton")
                    {
                        OnGoToRowButtonClick(btn, new RoutedEventArgs());
                        return;
                    }
                }
            }
        }
    }

    private void OnFocusSearchShortcut(object sender, ExecutedRoutedEventArgs e)
    {
        GlobalSearchBox.Focus();
        GlobalSearchBox.SelectAll();
    }

    private DataGridTextColumn CreateDataColumn(DataColumn column, ColumnInfo? columnInfo, int index)
    {
        var columnName = column.ColumnName;
        // Initialize filter state for this column
        _columnFilters[columnName] = new ColumnFilterState();

        var gridColumn = new DataGridTextColumn
        {
            Header = CreateColumnHeader(columnName, columnInfo?.Type ?? "unknown", index),
            Binding = new System.Windows.Data.Binding($"[{columnName}]"),
            Width = DataGridLength.Auto,
            MinWidth = 100,
            CanUserSort = true,
            CanUserResize = true,
            SortMemberPath = columnName
        };

        // Right-align numeric columns
        if (IsNumericType(column.DataType))
        {
            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
            gridColumn.CellStyle = cellStyle;

            var elementStyle = new Style(typeof(TextBlock));
            elementStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            gridColumn.ElementStyle = elementStyle;
        }

        return gridColumn;
    }

    private static bool IsNumericType(Type type)
    {
        return type == typeof(int) || type == typeof(long) || type == typeof(short) ||
               type == typeof(byte) || type == typeof(double) || type == typeof(float) ||
               type == typeof(decimal) || type == typeof(uint) || type == typeof(ulong) ||
               type == typeof(ushort);
    }

    private void SnapshotFilterState()
    {
        _savedColumnFilterSelections.Clear();
        foreach (var kvp in _columnFilters)
        {
            if (kvp.Value.IsActive)
                _savedColumnFilterSelections[kvp.Key] = new HashSet<string>(kvp.Value.SelectedValues);
        }
        _savedGlobalSearch = GlobalSearchBox?.Text ?? string.Empty;
        _savedSort = _dataView?.Sort ?? string.Empty;
    }

    private void RestoreFilterState()
    {
        // Re-apply saved column filters whose column still exists
        foreach (var kvp in _savedColumnFilterSelections)
        {
            if (_columnFilters.TryGetValue(kvp.Key, out var state))
            {
                // Collect distinct values first so we can apply the saved selection
                CollectDistinctValues(kvp.Key, state);
                state.SelectedValues = new HashSet<string>(kvp.Value);
                state.IsActive = true;
                UpdateFilterIndicator(kvp.Key);
            }
        }

        if (!string.IsNullOrEmpty(_savedGlobalSearch) && GlobalSearchBox != null)
            GlobalSearchBox.Text = _savedGlobalSearch;

        if (!string.IsNullOrEmpty(_savedSort) && _dataView != null)
        {
            try { _dataView.Sort = _savedSort; }
            catch { /* column may no longer exist in the reloaded schema */ }
        }

        if (_savedColumnFilterSelections.Count > 0 || !string.IsNullOrEmpty(_savedGlobalSearch))
            ApplyAllFilters();
    }

    private FrameworkElement CreateColumnHeader(string name, string type, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left side: type icon + column name
        var namePanel = new DockPanel { LastChildFill = true };
        namePanel.Children.Add(new TextBlock
        {
            Text = GetTypeIcon(type),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(namePanel.Children[0], Dock.Left);
        var nameBlock = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = name
        };
        namePanel.Children.Add(nameBlock);
        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        // Right side: filter button with indicator
        var filterPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };

        // Filter active indicator (small colored dot, hidden by default)
        var indicator = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(81, 43, 212)), // #512BD4
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Tag = $"indicator_{name}"
        };
        filterPanel.Children.Add(indicator);

        // Filter dropdown button (funnel icon)
        var filterButton = new Button
        {
            Content = "\u25BC", // ▼ down arrow
            FontSize = 8,
            Padding = new Thickness(3, 1, 3, 1),
            Margin = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            Tag = name,
            ToolTip = $"Filter {name}"
        };
        filterButton.Click += OnFilterButtonClick;
        filterPanel.Children.Add(filterButton);

        Grid.SetColumn(filterPanel, 1);
        grid.Children.Add(filterPanel);

        return grid;
    }

    private void UpdateFilterIndicator(string columnName)
    {
        // Find the indicator in the column header by scanning DataGrid columns
        foreach (var col in DataGrid.Columns)
        {
            if (col.SortMemberPath == columnName && col.Header is Grid headerGrid)
            {
                foreach (var child in headerGrid.Children)
                {
                    if (child is StackPanel sp)
                    {
                        foreach (var spChild in sp.Children)
                        {
                            if (spChild is Border border && border.Tag?.ToString() == $"indicator_{columnName}")
                            {
                                var isActive = _columnFilters.TryGetValue(columnName, out var state) && state.IsActive;
                                border.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
                                return;
                            }
                        }
                    }
                }
            }
        }
    }

    private void OnFilterButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string columnName) return;
        if (!_columnFilters.TryGetValue(columnName, out var filterState)) return;

        // Lazy-load distinct values on first open
        if (!filterState.IsLoaded)
            CollectDistinctValues(columnName, filterState);

        ShowFilterPopup(button, columnName, filterState);
    }

    private void CollectDistinctValues(string columnName, ColumnFilterState state)
    {
        if (_originalData == null || state.IsLoaded) return;

        var distinctValues = new HashSet<string>();
        bool hasBlank = false;
        bool truncated = false;
        int totalDistinct = 0;

        foreach (DataRow row in _originalData.Rows)
        {
            var val = row[columnName];
            var strVal = val?.ToString();
            if (val == null || val == DBNull.Value || string.IsNullOrWhiteSpace(strVal))
            {
                hasBlank = true;
            }
            else
            {
                var nonBlank = strVal!;
                if (!distinctValues.Contains(nonBlank))
                {
                    if (distinctValues.Count < MaxDistinctValues)
                    {
                        distinctValues.Add(nonBlank);
                    }
                    else
                    {
                        truncated = true;
                    }
                    totalDistinct++;
                }
            }
        }

        var limited = distinctValues.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

        if (hasBlank)
            limited.Insert(0, BlankDisplayValue);

        state.AllValues = limited;
        state.IsTruncated = truncated;
        state.TotalDistinctCount = totalDistinct == 0 ? distinctValues.Count : totalDistinct;
        // Default: all values selected (no filter)
        if (state.SelectedValues.Count == 0)
            state.SelectedValues = new HashSet<string>(limited);
        state.IsLoaded = true;
    }

    private void ShowFilterPopup(Button anchor, string columnName, ColumnFilterState filterState)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };

        var border = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Width = 260,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                Opacity = 0.2,
                ShadowDepth = 2,
                Color = Colors.Black
            }
        };

        var mainPanel = new StackPanel();

        // Sort buttons
        mainPanel.Children.Add(CreateSortButtonsPanel(columnName, popup));

        // Header
        var header = new TextBlock
        {
            Text = $"Filter: {columnName}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6)
        };
        mainPanel.Children.Add(header);

        // Search box
        var searchBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(4, 3, 4, 3),
            FontSize = 12
        };
        // Placeholder watermark via adorner-style approach: use GotFocus/LostFocus
        var searchPlaceholder = new TextBlock
        {
            Text = "Search values...",
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            IsHitTestVisible = false,
            Margin = new Thickness(5, 3, 0, 0),
            FontSize = 12
        };
        var searchContainer = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        searchContainer.Children.Add(searchBox);
        searchContainer.Children.Add(searchPlaceholder);
        searchBox.TextChanged += (s, _) =>
        {
            searchPlaceholder.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        };
        mainPanel.Children.Add(searchContainer);

        // Truncation notice when distinct values exceed the limit
        if (filterState.IsTruncated)
        {
            var truncNotice = new TextBlock
            {
                Text = $"Showing up to {MaxDistinctValues:N0} of {filterState.TotalDistinctCount:N0} values",
                Foreground = new SolidColorBrush(Color.FromRgb(180, 130, 0)),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 4)
            };
            mainPanel.Children.Add(truncNotice);
        }

        // Select All checkbox
        var selectAllCheckBox = new CheckBox
        {
            Content = "Select All",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
            IsThreeState = true
        };
        mainPanel.Children.Add(selectAllCheckBox);

        // Separator
        mainPanel.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 4) });

        // Scrollable checkbox list
        var checkboxPanel = new StackPanel();
        var scrollViewer = new ScrollViewer
        {
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = checkboxPanel
        };
        mainPanel.Children.Add(scrollViewer);

        // Build checkbox items
        var checkBoxes = new List<CheckBox>();
        foreach (var value in filterState.AllValues)
        {
            var cb = new CheckBox
            {
                Content = value,
                IsChecked = filterState.SelectedValues.Contains(value),
                Margin = new Thickness(0, 1, 0, 1),
                FontSize = 12,
                Tag = value
            };
            checkBoxes.Add(cb);
            checkboxPanel.Children.Add(cb);
        }

        // Update Select All state based on current selection
        UpdateSelectAllState(selectAllCheckBox, checkBoxes);

        // Select All click handler
        selectAllCheckBox.Checked += (s, _) =>
        {
            foreach (var cb in checkBoxes)
            {
                if (cb.Visibility == Visibility.Visible)
                    cb.IsChecked = true;
            }
        };
        selectAllCheckBox.Unchecked += (s, _) =>
        {
            foreach (var cb in checkBoxes)
            {
                if (cb.Visibility == Visibility.Visible)
                    cb.IsChecked = false;
            }
        };

        // Individual checkbox changes update Select All state
        foreach (var cb in checkBoxes)
        {
            cb.Checked += (s, _) => UpdateSelectAllState(selectAllCheckBox, checkBoxes);
            cb.Unchecked += (s, _) => UpdateSelectAllState(selectAllCheckBox, checkBoxes);
        }

        // Search box filters the checkbox list
        searchBox.TextChanged += (s, _) =>
        {
            var searchText = searchBox.Text?.Trim() ?? string.Empty;
            foreach (var cb in checkBoxes)
            {
                var val = cb.Tag?.ToString() ?? string.Empty;
                cb.Visibility = string.IsNullOrEmpty(searchText) ||
                    val.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateSelectAllState(selectAllCheckBox, checkBoxes);
        };

        // Separator before buttons
        mainPanel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });

        // Buttons panel
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var clearButton = new Button
        {
            Content = "Clear",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12
        };
        clearButton.Click += (s, _) =>
        {
            // Clear filter: select all values
            foreach (var cb in checkBoxes)
                cb.IsChecked = true;
            filterState.SelectedValues = new HashSet<string>(filterState.AllValues);
            filterState.IsActive = false;
            UpdateFilterIndicator(columnName);
            ApplyAllFilters();
            popup.IsOpen = false;
        };
        buttonPanel.Children.Add(clearButton);

        var applyButton = new Button
        {
            Content = "Apply",
            Padding = new Thickness(12, 4, 12, 4),
            Background = new SolidColorBrush(Color.FromRgb(81, 43, 212)), // #512BD4
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 12
        };
        applyButton.Click += (s, _) =>
        {
            // Apply filter: read checked values
            var selected = new HashSet<string>();
            foreach (var cb in checkBoxes)
            {
                if (cb.IsChecked == true && cb.Tag is string val)
                    selected.Add(val);
            }
            filterState.SelectedValues = selected;
            filterState.IsActive = selected.Count < filterState.AllValues.Count;
            UpdateFilterIndicator(columnName);
            ApplyAllFilters();
            popup.IsOpen = false;
        };
        buttonPanel.Children.Add(applyButton);

        mainPanel.Children.Add(buttonPanel);

        border.Child = mainPanel;
        popup.Child = border;
        popup.IsOpen = true;
    }

    private static void UpdateSelectAllState(CheckBox selectAll, List<CheckBox> checkBoxes)
    {
        var visibleBoxes = checkBoxes.Where(cb => cb.Visibility == Visibility.Visible).ToList();
        var checkedCount = visibleBoxes.Count(cb => cb.IsChecked == true);

        // Temporarily remove handler to avoid recursive trigger
        selectAll.IsThreeState = true;
        if (checkedCount == 0)
            selectAll.IsChecked = false;
        else if (checkedCount == visibleBoxes.Count)
            selectAll.IsChecked = true;
        else
            selectAll.IsChecked = null; // indeterminate
    }
    
    private void ApplySort(string sortMemberPath, ListSortDirection direction)
    {
        if (_dataView == null) return;

        _dataView.Sort = $"{sortMemberPath} {(direction == ListSortDirection.Ascending ? "ASC" : "DESC")}";

        // Update the DataGrid column header sort indicator
        foreach (var col in DataGrid.Columns)
        {
            if (col.SortMemberPath == sortMemberPath)
                col.SortDirection = direction;
            else
                col.SortDirection = null;
        }

        var displayName = sortMemberPath == "__RowNumber" ? "#" : sortMemberPath;
        StatusText.Text = $"Sorted by {displayName} ({(direction == ListSortDirection.Ascending ? "ascending" : "descending")})";
    }

    private FrameworkElement CreateSortButtonsPanel(string sortMemberPath, Popup popup)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        var ascButton = new Button
        {
            Content = "\u2B06 Sort Ascending",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4, 6, 4),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12
        };
        ascButton.Click += (s, _) =>
        {
            popup.IsOpen = false;
            ApplySort(sortMemberPath, ListSortDirection.Ascending);
        };
        panel.Children.Add(ascButton);

        var descButton = new Button
        {
            Content = "\u2B07 Sort Descending",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4, 6, 4),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12
        };
        descButton.Click += (s, _) =>
        {
            popup.IsOpen = false;
            ApplySort(sortMemberPath, ListSortDirection.Descending);
        };
        panel.Children.Add(descButton);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });

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
            
            ApplySort(sortMemberPath, direction);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error sorting data: {ex.Message}", "Sort Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Sort failed";
        }
    }

    private void ApplyAllFilters()
    {
        if (_dataView == null) return;
        
        var filters = new List<string>();
        
        // Add column-specific value filters
        foreach (var kvp in _columnFilters)
        {
            if (!kvp.Value.IsActive) continue;
            
            var columnName = kvp.Key;
            var selected = kvp.Value.SelectedValues;
            
            if (selected.Count == 0)
            {
                // Nothing selected means show no rows for this column
                filters.Add("1=0");
                continue;
            }

            var conditions = new List<string>();
            bool includeBlank = selected.Contains(BlankDisplayValue);

            foreach (var val in selected)
            {
                if (val == BlankDisplayValue) continue;
                var escaped = val.Replace("'", "''");
                conditions.Add($"Convert([{columnName}], 'System.String') = '{escaped}'");
            }

            if (includeBlank)
            {
                conditions.Add($"LTRIM(RTRIM(Convert([{columnName}], 'System.String'))) = ''");
                conditions.Add($"[{columnName}] IS NULL");
            }

            if (conditions.Count > 0)
                filters.Add($"({string.Join(" OR ", conditions)})");
        }
        
        // Add global search filter (OR condition across all data columns)
        var globalSearchText = GlobalSearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(globalSearchText) && _originalData != null)
        {
            var globalConditions = new List<string>();
            var escapedGlobalText = EscapeDataViewLikeValue(globalSearchText.Replace("'", "''"));
            
            foreach (DataColumn col in _originalData.Columns)
            {
                if (col.ColumnName != "__RowNumber")
                    globalConditions.Add($"Convert([{col.ColumnName}], 'System.String') LIKE '*{escapedGlobalText}*'");
            }
            
            if (globalConditions.Count > 0)
            {
                filters.Add($"({string.Join(" OR ", globalConditions)})");
            }
        }
        
        try
        {
            _dataView.RowFilter = filters.Count > 0 ? string.Join(" AND ", filters) : string.Empty;
            
            var columnCount = _columnFilters.Count(kvp => kvp.Value.IsActive);
            var hasGlobal = !string.IsNullOrWhiteSpace(GlobalSearchBox.Text);
            
            if (columnCount > 0 && hasGlobal)
                StatusText.Text = $"Filtered by {columnCount} column(s) + global search";
            else if (columnCount > 0)
                StatusText.Text = $"Filtered by {columnCount} column(s)";
            else if (hasGlobal)
                StatusText.Text = "Filtered by global search";
            else
                StatusText.Text = "Ready";

            UpdateFilterBadge();
            UpdateRowCount();
        }
        catch
        {
            // If filter fails, clear it
            _dataView.RowFilter = string.Empty;
            StatusText.Text = "Filter error - cleared";
        }
    }
    
    private void OnClearAllFiltersClick(object sender, RoutedEventArgs e)
    {
        foreach (var kvp in _columnFilters)
        {
            if (kvp.Value.IsActive)
            {
                kvp.Value.SelectedValues = new HashSet<string>(kvp.Value.AllValues);
                kvp.Value.IsActive = false;
                UpdateFilterIndicator(kvp.Key);
            }
        }
        // Temporarily detach the TextChanged handler to prevent triggering the debounce
        // timer (and a redundant ApplyAllFilters call) when clearing the global search box.
        GlobalSearchBox.TextChanged -= OnGlobalSearchTextChanged;
        GlobalSearchBox.Text = string.Empty;
        GlobalSearchBox.TextChanged += OnGlobalSearchTextChanged;

        ApplyAllFilters();
    }

    private void UpdateFilterBadge()
    {
        var activeCount = _columnFilters.Count(kvp => kvp.Value.IsActive);
        var hasGlobal = !string.IsNullOrWhiteSpace(GlobalSearchBox.Text);
        var totalFilters = activeCount + (hasGlobal ? 1 : 0);

        if (totalFilters > 0)
        {
            FilterBadge.Visibility = Visibility.Visible;
            FilterBadgeText.Text = $"🔽 {totalFilters} filter{(totalFilters > 1 ? "s" : "")} active";
        }
        else
        {
            FilterBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateRowCount()
    {
        if (_dataView == null || _originalData == null)
        {
            RowCountText.Text = string.Empty;
            return;
        }

        var filtered = _dataView.Count;
        var total = _originalData.Rows.Count;
        RowCountText.Text = filtered < total
            ? $"{filtered:N0} of {total:N0} rows"
            : $"{total:N0} rows";
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading recent files: {ex.Message}");
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving recent files: {ex.Message}");
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
                var format = Services.FileFormatDetector.DetectFormat(filePath);
                if (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv || format == SupportedFileFormat.Json)
                {
                    var result = ShowFileImportDialog(filePath, format);
                    if (!result.Imported) return;
                    await LoadFileAsync(filePath, result.CsvOptions, jsonOptions: result.JsonOptions);
                }
                else
                {
                    await LoadFileAsync(filePath);
                }
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
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        if (files.Length > 1)
            StatusText.Text = $"{files.Length} files dropped — opening the first one only";

        var filePath = files[0];
        var format = Services.FileFormatDetector.DetectFormat(filePath);

        if (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv || format == SupportedFileFormat.Json)
        {
            // Defer past the drag-drop event so the DnD machinery fully releases before
            // opening a modal dialog. Without this, WPF's drag handling can minimize the
            // owner window when the modal steals activation mid-drag.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
            Activate();

            var result = ShowFileImportDialog(filePath, format);
            if (!result.Imported) return;
            await LoadFileAsync(filePath, result.CsvOptions, jsonOptions: result.JsonOptions);
        }
        else
        {
            await LoadFileAsync(filePath);
        }
    }

    // ── Import Options (re-import CSV/TSV with custom settings) ─────────

    private async void OnImportOptionsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;

        var format = Services.FileFormatDetector.DetectFormat(_currentFilePath);
        if (format != SupportedFileFormat.Csv && format != SupportedFileFormat.Tsv && format != SupportedFileFormat.Json)
        {
            MessageBox.Show("Import options are available for CSV, TSV, and JSON files.",
                "Import Options", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = ShowFileImportDialog(_currentFilePath, format, _activeCsvOptions, _activeJsonOptions);
        if (result.Imported)
        {
            await LoadFileAsync(_currentFilePath, result.CsvOptions, jsonOptions: result.JsonOptions);
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
                _totalRowCount > _currentRowLimit ? _currentRowLimit : (int?)null,
                _activeJsonOptions);

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
        await LoadFileAsync(_currentFilePath, _activeCsvOptions, _currentRowLimit, _activeJsonOptions);
    }

    // ── JSON Flattening ───────────────────────────────────────────────

    private async void OnFlattenJsonClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath) || _parquetService == null) return;

        try
        {
            ShowLoading("Detecting nested structures...");

            var flatQuery = await _parquetService.GetFlattenedQueryAsync(_currentFilePath, _activeCsvOptions, _activeJsonOptions);

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

            var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
            _parquetService?.Dispose();
            _parquetService = new ParquetService(logger);

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

    // ── Help Menu ────────────────────────────────────────────────────

    private void OnHelpClick(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        => new HelpDialog(initialTab: 0) { Owner = this }.ShowDialog();

    private void OnKeyboardShortcutsClick(object sender, RoutedEventArgs e)
        => new HelpDialog(initialTab: 0) { Owner = this }.ShowDialog();

    private void OnTipsAndTricksClick(object sender, RoutedEventArgs e)
        => new HelpDialog(initialTab: 1) { Owner = this }.ShowDialog();

    private void OnAboutClick(object sender, RoutedEventArgs e)
        => new HelpDialog(initialTab: 2) { Owner = this }.ShowDialog();

    // ── File Watcher ────────────────────────────────────────────────────

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
                    // Reset to default batch size on external reload — avoids silently
                    // re-loading millions of rows that were previously loaded via Load All
                    await LoadFileAsync(e.FullPath, _activeCsvOptions, null, _activeJsonOptions);
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

/// <summary>
/// Tracks per-column filter state for Fabric Lakehouse-style dropdown filters.
/// </summary>
internal class ColumnFilterState
{
    public List<string> AllValues { get; set; } = new();
    public HashSet<string> SelectedValues { get; set; } = new();
    public bool IsActive { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsTruncated { get; set; }
    public int TotalDistinctCount { get; set; }
}