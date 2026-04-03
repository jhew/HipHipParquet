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
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Shell;

namespace HipHipParquet.Views;

public partial class MainWindow : Window
{
    private const double MaxQualityPaneWidthCap = 1300;
    private const string RowNumberColumnName = "__RowNumber";
    private const string SourceFileColumnName = "__SourceFile";
    private const string DuckDbFilenameColumnName = "filename";
    public static readonly RoutedUICommand GoToRowCommand = new("Go to Row", "GoToRow", typeof(MainWindow));
    public static readonly RoutedUICommand FocusSearchCommand = new("Focus Search", "FocusSearch", typeof(MainWindow));

    private DataTable? _originalData;
    private DataView? _dataView;
    private readonly List<string> _recentFiles = new();
    private const int MaxRecentFiles = 10;
    private const string RecentFilesKey = "RecentFiles";
    private string? _pendingFileToLoad;
    private string? _pendingStartupCommand;
    private string? _currentFilePath;
    private IReadOnlyList<string>? _currentFilePaths;
    private bool _hasUnsavedChanges = false;
    private int _pendingEditCount = 0;
    private QualityReviewViewModel? _qualityViewModel;
    private List<DataGridCellInfo>? _savedSelectedCells;
    private List<object>? _savedSelectedItems;
    private bool _closingConfirmed = false;
    private DataFileInfo? _lastSchemaInfo;
    private string? _lastSchemaFilePath;

    // ── Column filter state (Fabric Lakehouse-style dropdowns) ────────────
    private readonly Dictionary<string, ColumnFilterState> _columnFilters = new();
    private const int MaxDistinctValues = 500;
    private const string BlankDisplayValue = "(Blank)";
    private CancellationTokenSource? _filterCountCts;

    // ── Filter / sort state (preserved across reloads) ────────────────────
    private readonly Dictionary<string, HashSet<string>> _savedColumnFilterSelections = new();
    private string _savedGlobalSearch = string.Empty;
    private string _savedSort = string.Empty;
    // ── Row limiting ─────────────────────────────────────────────────────
    private const int RowLimitBatch = 50_000;
    private const long AutoProfileRowThreshold = 250_000;
    private int _currentRowLimit = RowLimitBatch;
    private long _totalRowCount;
    private CsvImportOptions? _activeCsvOptions;
    private JsonImportOptions? _activeJsonOptions;
    private SupportedFileFormat _currentFormat;

    // ── File watcher ─────────────────────────────────────────────────────
    private FileSystemWatcher? _fileWatcher;

    // ── Stored service reference for reuse ───────────────────────────────
    private ParquetService? _parquetService;
    private readonly WorkspaceService _workspaceService = new();

    // ── Search debounce ───────────────────────────────────────────────────
    private readonly System.Windows.Threading.DispatcherTimer _filterDebounceTimer;
    private static readonly string WorkspaceStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HipHipParquet",
        "workspace-state.json");
    
    public MainWindow()
    {
        InitializeComponent();
        InitializeQualityPanel();
        LoadRecentFiles();
        UpdateRecentFilesMenu();
        RefreshSavedViewsMenu();
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        MainContentGrid.SizeChanged += (_, _) => ClampQualityPaneWidth();

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
            SaveWorkspaceState();
            _parquetService?.Dispose();
            _fileWatcher?.Dispose();
            return;
        }

        if (!_hasUnsavedChanges)
        {
            SaveWorkspaceState();
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
        // Use BeginInvoke to defer Close() until after this Closing event handler returns.
        // Calling Close() directly here while _closing == true (WPF internal state) throws
        // InvalidOperationException: "Cannot call Show, ShowDialog, Close... while a Window is closing."
        _closingConfirmed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ClampQualityPaneWidth();
        UpdateJumpList();
        RefreshSavedViewsMenu();

        // Background startup update check — shows a hint in the status bar if a new version is found.
        _ = CheckForUpdatesOnStartupAsync();

        // If there's a pending file to load from command line, load it now
        if (!string.IsNullOrEmpty(_pendingFileToLoad))
        {
            var fileToLoad = _pendingFileToLoad;
            _pendingFileToLoad = null;
            await OpenWithRecommendedSettingsAsync(fileToLoad);
            await RunPendingStartupCommandAsync();
            return;
        }

        // Skip workspace restore when a startup command will immediately replace it
        // (e.g. --compare-with-last opens a different file). Always restore for --restore-workspace.
        if (string.IsNullOrWhiteSpace(_pendingStartupCommand) ||
            string.Equals(_pendingStartupCommand, "restore-workspace", StringComparison.OrdinalIgnoreCase))
        {
            await RestoreWorkspaceStateAsync();
        }

        await RunPendingStartupCommandAsync();
    }

    private async Task RunPendingStartupCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingStartupCommand))
            return;

        var command = _pendingStartupCommand;
        _pendingStartupCommand = null;

        if (string.Equals(command, "compare-with-last", StringComparison.OrdinalIgnoreCase))
            await CompareWithLastRecentAsync();
    }

    public Task LoadFileFromCommandLineAsync(string filePath)
    {
        // Store the file path to load after the window is loaded
        _pendingFileToLoad = filePath;
        return Task.CompletedTask;
    }

    public Task QueueStartupCommandAsync(string command)
    {
        _pendingStartupCommand = command;
        return Task.CompletedTask;
    }

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = Services.FileFormatDetector.GetOpenFileDialogFilter(),
            Title = "Select Data File",
            Multiselect = true
        };

        if (openFileDialog.ShowDialog() == true)
        {
            if (openFileDialog.FileNames.Length > 1)
            {
                if (openFileDialog.FileNames.Any(path => Services.FileFormatDetector.DetectFormat(path) != SupportedFileFormat.Parquet))
                {
                    MessageBox.Show(
                        "Multi-select loading is currently supported only for parquet files.",
                        "Unsupported Selection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                await LoadParquetFilesAsSingleTableAsync(openFileDialog.FileNames);
                return;
            }

            var filePath = openFileDialog.FileName;
            // Hold Shift while opening to force the advanced import dialog.
            var forceImportDialog = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            await OpenWithRecommendedSettingsAsync(filePath, forceImportDialog);
        }
    }

    private async Task LoadParquetFilesAsSingleTableAsync(IReadOnlyList<string> filePaths)
    {
        try
        {
            var orderedFilePaths = filePaths
                .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
                .ThenBy(path => path, NaturalStringComparer.Instance)
                .ToList();

            ShowLoading("Loading parquet file set...");
            _currentFormat = SupportedFileFormat.Parquet;
            _activeCsvOptions = null;
            _activeJsonOptions = null;

            // When loading a set of files, clear any existing single-file watcher and quality state
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }

            _qualityViewModel?.ClearFile();

            var effectiveLimit = RowLimitBatch;
            _currentRowLimit = effectiveLimit;

            var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
            _parquetService?.Dispose();
            _parquetService = new ParquetService(logger);

            var fileInfo = await _parquetService.GetFileInfoAsync(orderedFilePaths);
            _totalRowCount = fileInfo.RowCount;

            ShowLoading($"Loading rows (limit: {effectiveLimit:N0})...");
            var dataTable = await _parquetService.LoadFilesAsync(
                orderedFilePaths,
                _totalRowCount > effectiveLimit ? effectiveLimit : (int?)null);

            ShowLoading("Indexing rows...");
            await Task.Run(() => EnsureRowMetadataColumns(dataTable, orderedFilePaths));

            UpdateSchemaPanel(orderedFilePaths[0], fileInfo);

            ShowLoading("Building grid...");
            SetupDataGrid(dataTable, fileInfo.Columns);

            EmptyStatePanel.Visibility = Visibility.Collapsed;
            DataGridContainer.Visibility = Visibility.Visible;
            UpdateLoadMoreBanner(dataTable.Rows.Count);

            foreach (var filePath in orderedFilePaths)
                AddToRecentFiles(filePath);

            // Multi-file parquet loads are treated as a logical table, not a single source file.
            // Keep the list of current paths so paging can work, but avoid overwriting source files.
            _currentFilePaths = orderedFilePaths;
            _currentFilePath = orderedFilePaths.Count == 1 ? orderedFilePaths[0] : null;
            _hasUnsavedChanges = false;
            _pendingEditCount = 0;
            UpdateWindowTitle();
            UpdatePendingChangesTray();
            EnableSaveMenuItems();
            UpdateFormatBadge(SupportedFileFormat.Parquet);

            // Enable quality analysis on the first file of the set (best-effort for multi-file loads).
            _qualityViewModel?.SetFilePath(orderedFilePaths[0], sourceFiles: fileInfo.SourceFileSummaries);
            if (_totalRowCount <= AutoProfileRowThreshold)
                _qualityViewModel?.StartAutoAnalyze();

            StatusText.Text =
                $"Loaded {orderedFilePaths.Count} parquet files — {dataTable.Rows.Count:N0}{(_totalRowCount > dataTable.Rows.Count ? $" of {_totalRowCount:N0}" : "")} rows, {fileInfo.Columns.Count} columns";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading parquet file set: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error loading parquet file set";
            EmptyStatePanel.Visibility = Visibility.Visible;
            DataGridContainer.Visibility = Visibility.Collapsed;
        }
        finally
        {
            HideLoading();
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
        {
            dialog.PrePopulateCsvOptions(existingCsvOptions);
        }
        else if (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv)
        {
            // Sniff the file encoding before showing the dialog so that Windows-1252
            // files (e.g. Excel CSVs with em dashes, curly quotes, etc.) are pre-selected
            // correctly rather than failing to load with the default "auto" encoding.
            var sniffed = Services.FileFormatDetector.SniffEncoding(filePath);
            if (sniffed != "auto")
                dialog.PrePopulateCsvOptions(new Models.CsvImportOptions { Encoding = sniffed });
        }

        if (existingJsonOptions != null)
            dialog.PrePopulateJsonOptions(existingJsonOptions);

        if (dialog.ShowDialog() == true)
            return (true, dialog.CsvResult, dialog.JsonResult);

        return (false, null, null);
    }

    private async Task OpenWithRecommendedSettingsAsync(string filePath, bool forceImportDialog = false)
    {
        if (!ConfirmUnknownExtension(filePath))
            return;

        var format = Services.FileFormatDetector.DetectFormat(filePath);
        if ((format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv || format == SupportedFileFormat.Json) && forceImportDialog)
        {
            var dialogResult = ShowFileImportDialog(filePath, format);
            if (!dialogResult.Imported)
                return;

            await LoadFileAsync(filePath, dialogResult.CsvOptions, jsonOptions: dialogResult.JsonOptions);
            return;
        }

        CsvImportOptions? recommendedCsv = null;
        if (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv)
        {
            var sniffedEncoding = Services.FileFormatDetector.SniffEncoding(filePath);
            if (!string.Equals(sniffedEncoding, "auto", StringComparison.OrdinalIgnoreCase))
                recommendedCsv = new CsvImportOptions { Encoding = sniffedEncoding };
        }

        await LoadFileAsync(filePath, recommendedCsv);
    }

    private static bool IsParquetPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".parquet", StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".snappy.parquet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedDropFile(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".parquet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tsv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tab", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jsonl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).EndsWith(".snappy.parquet", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ResolveDroppedFiles(IEnumerable<string> droppedPaths)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string file)
        {
            if (!IsSupportedDropFile(file))
                return;
            if (seen.Add(file))
                files.Add(file);
        }

        foreach (var path in droppedPaths)
        {
            if (File.Exists(path))
            {
                AddFile(path);
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            foreach (var parquetFile in Directory.EnumerateFiles(path, "*.parquet", SearchOption.AllDirectories))
                AddFile(parquetFile);

            foreach (var directFile in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
                AddFile(directFile);
        }

        return files;
    }

    /// <summary>
    /// Shows a confirmation prompt when the file has an unrecognised extension.
    /// Returns true if the file should be opened, false if the user declined.
    /// </summary>
    private static bool ConfirmUnknownExtension(string filePath)
    {
        if (!Services.FileFormatDetector.IsUnknownExtension(filePath))
            return true;

        var ext = System.IO.Path.GetExtension(filePath);
        var extDisplay = string.IsNullOrEmpty(ext) ? "(no extension)" : ext;
        var confirm = MessageBox.Show(
            $"'{System.IO.Path.GetFileName(filePath)}' has an unrecognised file extension ({extDisplay}).\n\n" +
            "Hip Hip Parquet will attempt to read it as a CSV file, which may fail or produce unexpected results.\n\n" +
            "Continue?",
            "Unrecognised File Type",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return confirm == MessageBoxResult.Yes;
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
                ClampQualityPaneWidth();
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

    private void OnQualitySplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        ClampQualityPaneWidth();
    }

    private void ClampQualityPaneWidth()
    {
        if (QualityReviewPanel.Visibility != Visibility.Visible)
            return;

        var available = MainContentGrid.ActualWidth
            - SchemaPaneColumn.ActualWidth
            - SchemaSplitterColumn.ActualWidth
            - QualitySplitterColumn.ActualWidth
            - MainDataColumn.MinWidth;

        var minAllowed = QualityPaneColumn.MinWidth;
        var layoutMax = Math.Max(minAllowed, available);
        var effectiveMax = Math.Min(MaxQualityPaneWidthCap, layoutMax);

        QualityPaneColumn.MaxWidth = effectiveMax;

        var currentWidth = QualityPaneColumn.ActualWidth;
        if (currentWidth > effectiveMax)
            QualityPaneColumn.Width = new GridLength(effectiveMax);
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

            // Wire taskbar progress updates from quality analysis
            _qualityViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(QualityReviewViewModel.TaskbarProgressVisible))
                {
                    MainTaskbarItemInfo.ProgressState = _qualityViewModel.TaskbarProgressVisible
                        ? TaskbarItemProgressState.Normal
                        : TaskbarItemProgressState.None;
                }
                else if (e.PropertyName == nameof(QualityReviewViewModel.TaskbarProgressValue))
                {
                    MainTaskbarItemInfo.ProgressValue = _qualityViewModel.TaskbarProgressValue;
                }
            };

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
                // Fallback for Grid headers without a DockPanel (e.g., row-number header: TextBlock + Button)
                var gridTextBlock = grid.Children.OfType<TextBlock>().FirstOrDefault();
                if (gridTextBlock != null)
                    return gridTextBlock.Text ?? "";
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
        var parquetPartsSuffix = string.Empty;
        if (format == SupportedFileFormat.Parquet)
        {
            parquetPartsSuffix = await Task.Run(() => GetParquetPartsStatusSuffix(filePath, format));
        }
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
        await Task.Run(() => EnsureRowMetadataColumns(dataTable, [filePath]));
        
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
        _currentFilePaths = new[] { filePath };
        _hasUnsavedChanges = false;
        _pendingEditCount = 0;
        UpdateWindowTitle();
        UpdatePendingChangesTray();
        EnableSaveMenuItems();

        UpdateFormatBadge(format);

        // Setup file watcher
        SetupFileWatcher(filePath);

        // Notify Quality panel of new file
        _qualityViewModel?.SetFilePath(filePath, csvOptions, jsonOptions, fileInfo.SourceFileSummaries);
        if (_totalRowCount <= AutoProfileRowThreshold)
            _qualityViewModel?.StartAutoAnalyze();
        
        StatusText.Text = $"Loaded {System.IO.Path.GetFileName(filePath)}{parquetPartsSuffix} — {dataTable.Rows.Count:N0}{(_totalRowCount > dataTable.Rows.Count ? $" of {_totalRowCount:N0}" : "")} rows, {fileInfo.Columns.Count} columns";

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
        bool hasData = _originalData != null;
        bool hasSingleSourcePath = !string.IsNullOrEmpty(_currentFilePath) && _originalData != null;

        SaveMenuItem.IsEnabled = hasSingleSourcePath;
        SaveAsMenuItem.IsEnabled = hasData;
        ExportAsMenuItem.IsEnabled = hasData;
        ImportOptionsMenuItem.IsEnabled = hasSingleSourcePath &&
            (_currentFormat == SupportedFileFormat.Csv || _currentFormat == SupportedFileFormat.Tsv || _currentFormat == SupportedFileFormat.Json);
        FlattenJsonMenuItem.IsEnabled = hasSingleSourcePath && _currentFormat == SupportedFileFormat.Json;
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
            _pendingEditCount = 0;
            UpdateWindowTitle();
            UpdatePendingChangesTray();
            
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
            _pendingEditCount++;
            UpdateWindowTitle();
            UpdatePendingChangesTray();
        }
    }

    private void UpdatePendingChangesTray()
    {
        if (_hasUnsavedChanges && _pendingEditCount > 0)
        {
            PendingChangesBadge.Visibility = Visibility.Visible;
            PendingChangesText.Text = $"{_pendingEditCount} pending change{(_pendingEditCount == 1 ? string.Empty : "s")}";
            return;
        }

        PendingChangesBadge.Visibility = Visibility.Collapsed;
        PendingChangesText.Text = string.Empty;
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
    
    private void UpdateSchemaPanel(string filePath, DataFileInfo fileInfo)
    {
        _lastSchemaInfo = fileInfo;
        _lastSchemaFilePath = filePath;
        CopySchemaButton.IsEnabled = true;

        RenderSchemaPanel();
    }

    private void OnSchemaSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        RenderSchemaPanel();
    }

    private void RenderSchemaPanel()
    {
        SchemaPanel.Children.Clear();

        if (_lastSchemaInfo == null || _lastSchemaFilePath == null)
        {
            SchemaPanel.Children.Add(new TextBlock
            {
                Text = "Schema",
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 8)
            });
            SchemaPanel.Children.Add(new TextBlock
            {
                Text = "No file loaded",
                Foreground = Brushes.Gray
            });
            return;
        }

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
        var formatName = Services.FileFormatDetector.GetFormatDisplayName(_lastSchemaInfo.Format);
        var sourceFileCount = _lastSchemaInfo.SourceFiles.Count;
        var fileLabel = sourceFileCount > 1
            ? $"📁 File Set: {sourceFileCount:N0} parquet files"
            : $"📁 File: {System.IO.Path.GetFileName(_lastSchemaFilePath)}";
        var fileBlock = new TextBlock
        {
            Text = fileLabel,
            Margin = new Thickness(0, 2, 0, 2)
        };
        SchemaPanel.Children.Add(fileBlock);

        if (sourceFileCount > 1)
        {
            foreach (var sourceFile in _lastSchemaInfo.SourceFiles.Take(8))
            {
                SchemaPanel.Children.Add(new TextBlock
                {
                    Text = $"   • {Path.GetFileName(sourceFile)}",
                    Margin = new Thickness(10, 0, 0, 1),
                    Foreground = Brushes.DimGray,
                    FontSize = 11
                });
            }

            if (sourceFileCount > 8)
            {
                SchemaPanel.Children.Add(new TextBlock
                {
                    Text = $"   • ... and {sourceFileCount - 8:N0} more",
                    Margin = new Thickness(10, 0, 0, 1),
                    Foreground = Brushes.DimGray,
                    FontSize = 11
                });
            }
        }

        var formatBlock = new TextBlock
        {
            Text = $"📋 Format: {formatName}",
            Margin = new Thickness(0, 2, 0, 2)
        };
        SchemaPanel.Children.Add(formatBlock);
        
        var rowBlock = new TextBlock
        {
            Text = $"📊 Rows: {_lastSchemaInfo.RowCount:N0}",
            Margin = new Thickness(0, 2, 0, 2)
        };
        SchemaPanel.Children.Add(rowBlock);

        var searchText = SchemaSearchBox?.Text?.Trim() ?? string.Empty;
        var filteredColumns = _lastSchemaInfo.Columns
            .Where(c => string.IsNullOrEmpty(searchText)
                || c.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || c.Type.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        
        var colHeaderBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(searchText)
                ? $"📋 Columns ({_lastSchemaInfo.Columns.Count}):"
                : $"📋 Columns ({filteredColumns.Count} of {_lastSchemaInfo.Columns.Count}):",
            Margin = new Thickness(0, 8, 0, 4)
        };
        SchemaPanel.Children.Add(colHeaderBlock);

        if (filteredColumns.Count == 0)
        {
            SchemaPanel.Children.Add(new TextBlock
            {
                Text = "No columns match this search.",
                Margin = new Thickness(8, 2, 0, 2),
                Foreground = Brushes.Gray
            });
            return;
        }

        foreach (var column in filteredColumns)
        {
            var icon = GetTypeIcon(column.Type);
            var jumpButton = new Button
            {
                Content = new TextBlock
                {
                    Text = $"{icon} {column.Name} ({column.Type})",
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 1, 0, 1),
                Padding = new Thickness(6, 2, 6, 2),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = $"Jump to column '{column.Name}'",
                Tag = column.Name
            };
            jumpButton.Click += OnSchemaColumnJumpClick;
            SchemaPanel.Children.Add(jumpButton);
        }
    }

    private void OnSchemaColumnJumpClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string columnName)
            return;

        var targetColumn = DataGrid.Columns.FirstOrDefault(c => c.SortMemberPath == columnName);
        if (targetColumn == null)
            return;

        if (DataGrid.Items.Count > 0)
            DataGrid.ScrollIntoView(DataGrid.Items[0], targetColumn);

        StatusText.Text = $"Jumped to column {columnName}";
    }

    private void OnCopySchemaClick(object sender, RoutedEventArgs e)
    {
        if (_lastSchemaInfo == null || _lastSchemaFilePath == null) return;

        var sb = new System.Text.StringBuilder();
        if (_lastSchemaInfo.SourceFiles.Count > 1)
        {
            sb.AppendLine($"File Set: {_lastSchemaInfo.SourceFiles.Count:N0} parquet files");
            sb.AppendLine("Source Files:");
            foreach (var sourceFile in _lastSchemaInfo.SourceFiles)
                sb.AppendLine($"  - {Path.GetFileName(sourceFile)}");
        }
        else
        {
            sb.AppendLine($"File: {System.IO.Path.GetFileName(_lastSchemaFilePath)}");
        }
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
        // Note: Distinct values will be loaded on-demand when user opens filter popup (async).
        // For now, just restore the selection state and mark filters as needing loading.
        foreach (var kvp in _savedColumnFilterSelections)
        {
            if (_columnFilters.TryGetValue(kvp.Key, out var state))
            {
                // Restore saved selection (will be applied once values are loaded)
                state.SelectedValues = new HashSet<string>(kvp.Value);
                state.IsActive = true;
                state.IsLoaded = false;  // Mark for async loading on first filter popup open
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

    private async void OnFilterButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string columnName) return;
        if (!_columnFilters.TryGetValue(columnName, out var filterState)) return;

        // Lazy-load distinct values on first open from full dataset (async)
        if (!filterState.IsLoaded)
        {
            button.IsEnabled = false;
            button.Content = "✓";  // Show loading indicator
            try
            {
                await CollectDistinctValuesAsync(columnName, filterState);
            }
            finally
            {
                button.IsEnabled = true;
                button.Content = "\u25BC";  // Restore ▼ filter icon
            }
        }

        ShowFilterPopup(button, columnName, filterState);
    }

    private async Task CollectDistinctValuesAsync(string columnName, ColumnFilterState state)
    {
        if (state.IsLoaded) return;

        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            try
            {
                // Single-file path: query distinct values via DuckDB over the full dataset
                var (values, totalDistinct, truncated) = await _parquetService!.GetDistinctValuesAsync(
                    filePath: _currentFilePath,
                    columnName: columnName,
                    csvOptions: _activeCsvOptions,
                    jsonOptions: _activeJsonOptions,
                    maxValues: MaxDistinctValues,
                    cancellationToken: default);

                state.AllValues = values;
                state.IsTruncated = truncated;
                state.TotalDistinctCount = totalDistinct;

                if (state.SelectedValues.Count == 0)
                    state.SelectedValues = new HashSet<string>(values);

                state.IsLoaded = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load filter values: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (_originalData != null && _originalData.Columns.Contains(columnName))
        {
            // Fallback for multi-file loads: compute distincts from the in-memory batch
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool hasBlank = false;

            foreach (DataRow row in _originalData.Rows)
            {
                var raw = row[columnName];
                if (raw == DBNull.Value || string.IsNullOrWhiteSpace(raw?.ToString()))
                {
                    hasBlank = true;
                }
                else
                {
                    seen.Add(raw.ToString()!);
                }
            }

            var sorted = seen.OrderBy(v => v).Take(MaxDistinctValues - (hasBlank ? 1 : 0)).ToList();
            if (hasBlank) sorted.Insert(0, "(Blank)");

            state.AllValues = sorted;
            state.IsTruncated = seen.Count > sorted.Count - (hasBlank ? 1 : 0);
            state.TotalDistinctCount = seen.Count + (hasBlank ? 1 : 0);

            if (state.SelectedValues.Count == 0)
                state.SelectedValues = new HashSet<string>(sorted);

            state.IsLoaded = true;
        }
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

        // Update the "Select All" checkbox state based on the visible item checkboxes
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

            var nonBlankSelected = selected.Where(v => v != BlankDisplayValue).ToList();
            if (nonBlankSelected.Count > 0)
            {
                // Use IN (...) to produce a single condition instead of one OR per value
                var inList = string.Join(", ", nonBlankSelected.Select(v => $"'{v.Replace("'", "''")}'")); 
                conditions.Add($"Convert([{columnName}], 'System.String') IN ({inList})");
            }

            if (includeBlank)
            {
                conditions.Add($"Convert([{columnName}], 'System.String') = ''");
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

            // Fire-and-forget: Query full dataset for filtered count if filters are active
            if ((columnCount > 0 || hasGlobal) && _totalRowCount > _currentRowLimit)
            {
                _ = UpdateFullDatasetFilterCount();
            }
        }
        catch
        {
            // If filter fails, clear it
            _dataView.RowFilter = string.Empty;
            StatusText.Text = "Filter error - cleared";
        }
    }

    /// <summary>
    /// Asynchronously queries the full dataset to show how many rows match the current filters.
    /// Cancels any previous in-flight count so stale results never overwrite the status bar.
    /// </summary>
    private async Task UpdateFullDatasetFilterCount()
    {
        // Cancel any previous in-flight count query
        _filterCountCts?.Cancel();
        _filterCountCts?.Dispose();
        var cts = _filterCountCts = new CancellationTokenSource();

        try
        {
            var fullDatasetCount = await GetFilteredRowCountAsync(cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            var batchCount = _dataView?.Count ?? 0;

            if (fullDatasetCount > batchCount)
            {
                StatusText.Text = $"{batchCount:N0} shown (filtered to {fullDatasetCount:N0} in full dataset of {_totalRowCount:N0})";
            }
            else if (fullDatasetCount > 0)
            {
                StatusText.Text = $"{fullDatasetCount:N0} matches in full dataset";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request — don't update status
        }
        catch
        {
            // Silent fail - keep existing status
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

    /// <summary>
    /// Builds a GridQueryState from current filter and sort state.
    /// Ready for use with ExecuteGridQueryAsync() for SQL-backed queries.
    /// </summary>
    private GridQueryState BuildCurrentQueryState()
    {
        var columnFilters = new Dictionary<string, HashSet<string>>();
        
        foreach (var kvp in _columnFilters)
        {
            if (kvp.Value.IsActive)
            {
                // Include all active filters, even empty selections (= match-none, signals 1=0 to SQL builder)
                columnFilters[kvp.Key] = new HashSet<string>(kvp.Value.SelectedValues);
            }
        }

        var availableColumns = _originalData?.Columns.Cast<DataColumn>()
            .Where(c => c.ColumnName != "__RowNumber")
            .Select(c => c.ColumnName)
            .ToList() ?? new();

        return new GridQueryState
        {
            SourceFilePath = _currentFilePath ?? string.Empty,
            CsvOptions = _activeCsvOptions,
            JsonOptions = _activeJsonOptions,
            ColumnFilters = columnFilters,
            GlobalSearch = (GlobalSearchBox?.Text ?? string.Empty).Trim(),
            Sort = _dataView?.Sort ?? string.Empty,
            RowLimit = _currentRowLimit,
            RowOffset = 0,
            AvailableColumns = availableColumns,
            TotalRowCount = _totalRowCount
        };
    }

    /// <summary>
    /// Queries the full dataset to get the filtered row count (without paging).
    /// Used for display and to determine if additional data is available beyond the current batch.
    /// </summary>
    private async Task<long> GetFilteredRowCountAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
            return _totalRowCount;

        try
        {
            var queryState = BuildCurrentQueryState();
            return await _parquetService!.GetFilteredRowCountAsync(queryState, cancellationToken);
        }
        catch
        {
            return _dataView?.Count ?? 0;
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

    private void SaveWorkspaceState()
    {
        try
        {
            // Capture active column filter selections
            var columnFilters = new Dictionary<string, List<string>>();
            foreach (var kvp in _columnFilters)
            {
                if (kvp.Value.IsActive)
                    columnFilters[kvp.Key] = [.. kvp.Value.SelectedValues];
            }

            var state = new WorkspaceState
            {
                FilePaths = _currentFilePaths?.ToList() ?? [],
                GlobalSearch = GlobalSearchBox.Text ?? string.Empty,
                Sort = _dataView?.Sort ?? string.Empty,
                SchemaSearch = SchemaSearchBox.Text ?? string.Empty,
                IsSchemaPaneVisible = SchemaPane.Visibility == Visibility.Visible,
                IsQualityPaneVisible = QualityReviewPanel.Visibility == Visibility.Visible,
                SchemaPaneWidth = SchemaPaneColumn.Width.Value,
                QualityPaneWidth = QualityPaneColumn.Width.Value,
                ColumnFilters = columnFilters,
                SavedAtUtc = DateTime.UtcNow
            };

            var dir = Path.GetDirectoryName(WorkspaceStatePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(WorkspaceStatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving workspace state: {ex.Message}");
        }
    }

    private async Task RestoreWorkspaceStateAsync()
    {
        try
        {
            if (!File.Exists(WorkspaceStatePath))
                return;

            var json = await File.ReadAllTextAsync(WorkspaceStatePath);
            var state = JsonSerializer.Deserialize<WorkspaceState>(json);
            if (state == null || state.FilePaths.Count == 0)
                return;

            var existingPaths = state.FilePaths.Where(File.Exists).ToList();
            if (existingPaths.Count == 0)
                return;

            if (existingPaths.Count > 1 && existingPaths.All(IsParquetPath))
                await LoadParquetFilesAsSingleTableAsync(existingPaths);
            else
                await OpenWithRecommendedSettingsAsync(existingPaths[0]);

            if (!string.IsNullOrWhiteSpace(state.SchemaSearch))
                SchemaSearchBox.Text = state.SchemaSearch;

            if (!string.IsNullOrWhiteSpace(state.GlobalSearch))
                GlobalSearchBox.Text = state.GlobalSearch;

            if (!string.IsNullOrWhiteSpace(state.Sort) && _dataView != null)
            {
                try
                {
                    _dataView.Sort = state.Sort;
                }
                catch
                {
                    // Ignore sort restore failures when schemas differ from prior session.
                }
            }

            // Restore column filter selections
            if (state.ColumnFilters.Count > 0)
            {
                foreach (var kvp in state.ColumnFilters)
                {
                    if (_columnFilters.TryGetValue(kvp.Key, out var filterState))
                    {
                        filterState.SelectedValues = new HashSet<string>(kvp.Value);
                        filterState.IsActive = true;
                        UpdateFilterIndicator(kvp.Key);
                    }
                }
                ApplyAllFilters();
            }

            ApplyPaneLayoutState(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error restoring workspace state: {ex.Message}");
        }
    }

    private void ApplyPaneLayoutState(WorkspaceState state)
    {
        ToggleSchemaPaneMenuItem.IsChecked = state.IsSchemaPaneVisible;
        ToggleQualityPanelMenuItem.IsChecked = state.IsQualityPaneVisible;

        if (state.IsSchemaPaneVisible)
        {
            SchemaPane.Visibility = Visibility.Visible;
            SchemaSplitter.Visibility = Visibility.Visible;
            SchemaPaneColumn.MinWidth = 200;
            SchemaPaneColumn.Width = new GridLength(Math.Max(200, state.SchemaPaneWidth));
            SchemaSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            SchemaPane.Visibility = Visibility.Collapsed;
            SchemaSplitter.Visibility = Visibility.Collapsed;
            SchemaPaneColumn.MinWidth = 0;
            SchemaPaneColumn.Width = new GridLength(0);
            SchemaSplitterColumn.Width = new GridLength(0);
        }

        if (state.IsQualityPaneVisible)
        {
            QualityReviewPanel.Visibility = Visibility.Visible;
            QualitySplitter.Visibility = Visibility.Visible;
            QualitySplitterColumn.Width = new GridLength(5);
            QualityPaneColumn.MinWidth = 300;
            QualityPaneColumn.Width = new GridLength(Math.Max(300, state.QualityPaneWidth));
            ClampQualityPaneWidth();
        }
        else
        {
            QualityReviewPanel.Visibility = Visibility.Collapsed;
            QualitySplitter.Visibility = Visibility.Collapsed;
            QualitySplitterColumn.Width = new GridLength(0);
            QualityPaneColumn.MinWidth = 0;
            QualityPaneColumn.Width = new GridLength(0);
        }
    }

    private void OnSaveWorkspaceSnapshotClick(object sender, RoutedEventArgs e)
    {
        SaveWorkspaceState();
        StatusText.Text = "Workspace snapshot saved";
    }

    private async void OnRestoreWorkspaceSnapshotClick(object sender, RoutedEventArgs e)
    {
        await RestoreWorkspaceStateAsync();
        StatusText.Text = "Workspace snapshot restored";
    }

    private async void OnSaveCurrentViewClick(object sender, RoutedEventArgs e)
    {
        if (_dataView == null)
        {
            StatusText.Text = "Open a dataset before saving a view";
            return;
        }

        var view = new SavedView
        {
            Name = $"View {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            Description = string.IsNullOrWhiteSpace(GlobalSearchBox.Text)
                ? "Saved filter/sort state"
                : $"Saved with search '{GlobalSearchBox.Text}'",
            ColumnFilters = _columnFilters
                .Where(kvp => kvp.Value.IsActive)
                .ToDictionary(k => k.Key, v => new HashSet<string>(v.Value.SelectedValues)),
            GlobalSearch = GlobalSearchBox.Text ?? string.Empty,
            Sort = _dataView.Sort ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };

        await _workspaceService.SaveViewAsync(view);
        RefreshSavedViewsMenu();
        StatusText.Text = $"Saved view '{view.Name}'";
    }

    private async void OnClearSavedViewsClick(object sender, RoutedEventArgs e)
    {
        var existing = _workspaceService.GetSavedViews().ToList();
        foreach (var view in existing)
            await _workspaceService.DeleteViewAsync(view.Name);

        RefreshSavedViewsMenu();
        StatusText.Text = "Cleared saved views";
    }

    private void RefreshSavedViewsMenu()
    {
        SavedViewsMenuItem.Items.Clear();

        var views = _workspaceService.GetSavedViews()
            .OrderByDescending(v => v.CreatedAtUtc)
            .ToList();

        if (views.Count == 0)
        {
            SavedViewsMenuItem.Items.Add(new MenuItem
            {
                Header = "(No saved views)",
                IsEnabled = false
            });
            return;
        }

        foreach (var savedView in views)
        {
            var item = new MenuItem
            {
                Header = savedView.Name,
                ToolTip = savedView.Description,
                Tag = savedView
            };
            item.Click += OnApplySavedViewClick;
            SavedViewsMenuItem.Items.Add(item);
        }
    }

    private void OnApplySavedViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not SavedView savedView)
            return;

        foreach (var kvp in _columnFilters)
        {
            kvp.Value.IsActive = false;
            kvp.Value.SelectedValues.Clear();
            UpdateFilterIndicator(kvp.Key);
        }

        foreach (var filter in savedView.ColumnFilters)
        {
            if (!_columnFilters.TryGetValue(filter.Key, out var state))
                continue;

            state.SelectedValues = new HashSet<string>(filter.Value);
            state.IsActive = true;
            UpdateFilterIndicator(filter.Key);
        }

        _savedSort = savedView.Sort;
        if (_dataView != null && !string.IsNullOrWhiteSpace(savedView.Sort))
        {
            try
            {
                _dataView.Sort = savedView.Sort;
            }
            catch
            {
                // Ignore stale sort columns when schema changed.
            }
        }

        GlobalSearchBox.TextChanged -= OnGlobalSearchTextChanged;
        GlobalSearchBox.Text = savedView.GlobalSearch;
        GlobalSearchBox.TextChanged += OnGlobalSearchTextChanged;

        ApplyAllFilters();
        StatusText.Text = $"Applied saved view '{savedView.Name}'";
    }

    private void UpdateJumpList()
    {
        try
        {
            var app = Application.Current;
            if (app == null)
                return;

            var jumpList = JumpList.GetJumpList(app) ?? new JumpList
            {
                ShowRecentCategory = false,
                ShowFrequentCategory = false
            };

            jumpList.JumpItems.Clear();

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = "Restore Last Workspace",
                    Description = "Restore your last workspace snapshot",
                    ApplicationPath = exePath,
                    Arguments = "--restore-workspace",
                    CustomCategory = "Quick Actions"
                });

                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = "Compare With Last File",
                    Description = "Open your most recent file and compare with the previous one",
                    ApplicationPath = exePath,
                    Arguments = "--compare-with-last",
                    CustomCategory = "Quick Actions"
                });

                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = "Open Latest Report",
                    Description = "Open the most recently exported HTML quality report",
                    ApplicationPath = exePath,
                    Arguments = "--open-latest-report",
                    CustomCategory = "Quick Actions"
                });
            }

            foreach (var path in _recentFiles.Where(File.Exists).Take(8))
                jumpList.JumpItems.Add(new JumpPath { Path = path });

            JumpList.SetJumpList(app, jumpList);
            jumpList.Apply();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating jump list: {ex.Message}");
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

        UpdateJumpList();
    }
    
    private async void OnRecentFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string filePath)
        {
            if (System.IO.File.Exists(filePath))
            {
                await OpenWithRecommendedSettingsAsync(filePath);
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

    private async Task CompareWithLastRecentAsync()
    {
        if (_recentFiles.Count < 2)
        {
            StatusText.Text = "Need at least two recent files to run comparison";
            return;
        }

        var primary = _recentFiles.FirstOrDefault(File.Exists);
        var comparison = _recentFiles.Skip(1).FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(comparison))
        {
            StatusText.Text = "No valid recent files found for comparison";
            return;
        }

        await OpenWithRecommendedSettingsAsync(primary);

        if (_qualityViewModel == null)
        {
            StatusText.Text = "Quality panel unavailable for comparison";
            return;
        }

        if (!_qualityViewModel.HasProfile)
            await _qualityViewModel.AnalyzeCommand.ExecuteAsync(null);

        await _qualityViewModel.CompareWithFilePathAsync(comparison);
        StatusText.Text = $"Compared {Path.GetFileName(primary)} with {Path.GetFileName(comparison)}";
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

        var droppedPaths = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (droppedPaths == null || droppedPaths.Length == 0) return;

        var candidateFiles = ResolveDroppedFiles(droppedPaths);
        if (candidateFiles.Count == 0)
        {
            StatusText.Text = "No supported files found in the dropped selection";
            return;
        }

        // Defer past the drag-drop event so the DnD machinery fully releases before
        // opening any modal dialog. Without this, WPF's drag handling can minimize the
        // owner window when the modal steals activation mid-drag.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
        Activate();

        if (candidateFiles.Count > 1 && candidateFiles.All(IsParquetPath))
        {
            await LoadParquetFilesAsSingleTableAsync(candidateFiles);
            return;
        }

        if (candidateFiles.Count > 1)
            StatusText.Text = $"Dropped {candidateFiles.Count} items - opening the first supported file";

        await OpenWithRecommendedSettingsAsync(candidateFiles[0]);
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
        if (_currentFilePaths == null || _currentFilePaths.Count == 0) return;
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
        if (_currentFilePaths == null || _currentFilePaths.Count == 0 || _parquetService == null)
            return;

        try
        {
            ShowLoading($"Loading rows (limit: {_currentRowLimit:N0})...");
            var parquetPartsSuffix = string.Empty;
            if (_currentFormat == SupportedFileFormat.Parquet && _currentFilePath != null)
            {
                parquetPartsSuffix = await Task.Run(() => GetParquetPartsStatusSuffix(_currentFilePath, _currentFormat));
            }

            DataTable dataTable;
            if (_currentFilePaths.Count == 1)
            {
                dataTable = await _parquetService.LoadFileAsync(
                    _currentFilePaths[0],
                    _activeCsvOptions,
                    _totalRowCount > _currentRowLimit ? _currentRowLimit : (int?)null,
                    _activeJsonOptions);
            }
            else
            {
                dataTable = await _parquetService.LoadFilesAsync(
                    _currentFilePaths,
                    _totalRowCount > _currentRowLimit ? _currentRowLimit : (int?)null);
            }

            // Re-index __RowNumber so the # column stays correct after pagination.
            ShowLoading("Indexing rows...");
            await Task.Run(() =>
            {
                EnsureRowMetadataColumns(dataTable, _currentFilePaths);
            });

            SetupDataGrid(dataTable, null);
            UpdateLoadMoreBanner(dataTable.Rows.Count);

            var sourceLabel = GetCurrentSourceStatusLabel();
            StatusText.Text = $"Loaded {sourceLabel}{parquetPartsSuffix} — {dataTable.Rows.Count:N0}{(_totalRowCount > dataTable.Rows.Count ? $" of {_totalRowCount:N0}" : "")} rows";
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

    private static string GetParquetPartsStatusSuffix(string filePath, SupportedFileFormat format)
    {
        if (format != SupportedFileFormat.Parquet)
            return string.Empty;

        var partCount = Services.FileFormatDetector.ResolveParquetInputs(filePath).Count;
        return partCount > 1 ? $" ({partCount:N0} parquet parts)" : string.Empty;
    }

    private string GetCurrentSourceStatusLabel()
    {
        if (_currentFilePaths != null && _currentFilePaths.Count > 1)
            return $"{_currentFilePaths.Count} parquet files";

        if (!string.IsNullOrWhiteSpace(_currentFilePath))
            return System.IO.Path.GetFileName(_currentFilePath);

        if (_currentFilePaths != null && _currentFilePaths.Count > 0)
            return System.IO.Path.GetFileName(_currentFilePaths[0]);

        return "current data";
    }

    private static void EnsureRowMetadataColumns(DataTable dataTable, IReadOnlyList<string>? sourceFiles)
    {
        var hasMultipleSources = sourceFiles != null && sourceFiles.Count > 1;
        var firstSourcePath = sourceFiles != null && sourceFiles.Count > 0 ? sourceFiles[0] : string.Empty;
        var singleSourceName = string.IsNullOrWhiteSpace(firstSourcePath) ? "(Unknown)" : Path.GetFileName(firstSourcePath);

        if (!dataTable.Columns.Contains(RowNumberColumnName))
        {
            var rowNumColumn = dataTable.Columns.Add(RowNumberColumnName, typeof(int));
            rowNumColumn.SetOrdinal(0);
        }

        if (!dataTable.Columns.Contains(SourceFileColumnName))
        {
            var sourceColumn = dataTable.Columns.Add(SourceFileColumnName, typeof(string));
            sourceColumn.SetOrdinal(1);
        }

        var rowNumberColumn = dataTable.Columns[RowNumberColumnName]!;
        if (rowNumberColumn.Ordinal != 0)
            rowNumberColumn.SetOrdinal(0);

        var sourceFileColumn = dataTable.Columns[SourceFileColumnName]!;
        if (sourceFileColumn.Ordinal != 1)
            sourceFileColumn.SetOrdinal(1);

        for (int i = 0; i < dataTable.Rows.Count; i++)
        {
            dataTable.Rows[i][RowNumberColumnName] = i + 1;

            var sourceFileName = singleSourceName;
            if (hasMultipleSources && dataTable.Columns.Contains(DuckDbFilenameColumnName))
            {
                var rawPath = dataTable.Rows[i][DuckDbFilenameColumnName] as string;
                if (!string.IsNullOrWhiteSpace(rawPath))
                    sourceFileName = Path.GetFileName(rawPath);
            }

            dataTable.Rows[i][SourceFileColumnName] = sourceFileName;
        }

        if (dataTable.Columns.Contains(DuckDbFilenameColumnName))
            dataTable.Columns.Remove(DuckDbFilenameColumnName);
    }

    private async void OnLoadAllClick(object sender, RoutedEventArgs e)
    {
        if (_currentFilePaths == null || _currentFilePaths.Count == 0) return;

        if (_totalRowCount > 500_000)
        {
            var result = MessageBox.Show(
                $"This file has {_totalRowCount:N0} rows. Loading all rows may cause the application to become slow or unresponsive.\n\nContinue?",
                "Large File Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _currentRowLimit = _totalRowCount > int.MaxValue ? int.MaxValue : (int)_totalRowCount;
        if (_currentFilePaths.Count == 1)
        {
            await LoadFileAsync(_currentFilePaths[0], _activeCsvOptions, _currentRowLimit, _activeJsonOptions);
        }
        else
        {
            await LoadParquetFilesAsSingleTableAsync(_currentFilePaths);
        }
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
        // Immediately clear the empty state so the overlay renders on a clean background
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        LoadingText.Text = message;
        LoadingOverlay.Visibility = Visibility.Visible;
        MainTaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
    }

    private void HideLoading()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        // Only clear the taskbar progress if quality analysis is not currently showing it
        if (_qualityViewModel?.TaskbarProgressVisible != true)
            MainTaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
    }

    private sealed class WorkspaceState
    {
        public List<string> FilePaths { get; set; } = [];
        public string GlobalSearch { get; set; } = string.Empty;
        public string Sort { get; set; } = string.Empty;
        public string SchemaSearch { get; set; } = string.Empty;
        public bool IsSchemaPaneVisible { get; set; } = true;
        public bool IsQualityPaneVisible { get; set; } = true;
        public double SchemaPaneWidth { get; set; } = 250;
        public double QualityPaneWidth { get; set; } = 420;
        /// <summary>Active column filter selections: column name → selected values.</summary>
        public Dictionary<string, List<string>> ColumnFilters { get; set; } = [];
        public DateTime SavedAtUtc { get; set; }
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

    // ── Update Check ────────────────────────────────────────────────────

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Checking for updates...";

        Services.UpdateInfo? update;
        try
        {
            update = await Services.UpdateService.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not check for updates.";
            MessageBox.Show(
                "HipHipParquet could not check whether a newer version is available.\n\n" +
                "This may be due to being offline, network issues, or a problem contacting the update service.\n\n" +
                $"Details: {ex.Message}",
                "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (update == null)
        {
            var current = Services.UpdateService.GetCurrentVersion();
            StatusText.Text = $"HipHipParquet v{current.ToString(3)} is up to date.";
            MessageBox.Show(
                $"You are running the latest version of HipHipParquet.\n\nCurrent version: {current.ToString(3)}",
                "No Updates Available", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"A new version of HipHipParquet is available!\n\n" +
            $"Current version:  {Services.UpdateService.GetCurrentVersion().ToString(3)}\n" +
            $"Latest version:   {update.LatestVersion.ToString(3)}\n\n" +
            (update.InstallerUrl != null
                ? "Would you like to download and install the update now?"
                : "Would you like to open the releases page to download it?"),
            "Update Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes)
        {
            StatusText.Text = "Update available — see Help > Check for Updates.";
            return;
        }

        if (update.InstallerUrl != null)
            await DownloadAndInstallUpdateAsync(update);
        else
            OpenUrl(update.ReleasePageUrl);
    }

    /// <summary>
    /// Silently checks for updates on startup and shows a hint in the status bar if one is found.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(5000); // Give the app time to finish loading first.
            var update = await Services.UpdateService.CheckForUpdateAsync();
            if (update != null)
            {
                Dispatcher.Invoke(() =>
                {
                    // Only show the update hint if the status bar is still idle
                    if (StatusText.Text == "Ready")
                        StatusText.Text = $"📦 Update available: v{update.LatestVersion.ToString(3)} — Help > Check for Updates";
                });
            }
        }
        catch { /* silently ignore startup check failures */ }
    }

    private async Task DownloadAndInstallUpdateAsync(Services.UpdateInfo update)
    {
        try
        {
            ShowLoading("Downloading update…");

            var progress = new Progress<int>(pct =>
                LoadingText.Text = $"Downloading update ({pct}%)…");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var installerPath = await Services.UpdateService.DownloadInstallerAsync(
                update.InstallerUrl!, progress, cts.Token);

            HideLoading();

            if (installerPath == null || !File.Exists(installerPath))
            {
                MessageBox.Show(
                    "Download failed. Opening the releases page so you can install manually.",
                    "Download Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                OpenUrl(update.ReleasePageUrl);
                return;
            }

            var confirm = MessageBox.Show(
                $"Version {update.LatestVersion.ToString(3)} is ready to install.\n\n" +
                "The application will close and the installer will run. Continue?",
                "Install Update", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                var app = Application.Current;
                if (app != null)
                {
                    // Launch installer only after the app has fully exited, so the
                    // installer never runs against a still-open instance.
                    ExitEventHandler? handler = null;
                    handler = (_, _) =>
                    {
                        app.Exit -= handler!;
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = installerPath,
                                UseShellExecute = true
                            });
                        }
                        catch { /* app is exiting — nothing to report */ }
                    };
                    app.Exit += handler;
                    app.Shutdown();
                }
            }
            else
            {
                StatusText.Text = "Update downloaded — run the installer when ready.";
            }
        }
        catch (Exception ex)
        {
            HideLoading();
            StatusText.Text = $"Download failed: {ex.Message}";
            OpenUrl(update.ReleasePageUrl);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no default browser */ }
    }

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
