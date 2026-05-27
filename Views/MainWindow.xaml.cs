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
using System.Runtime.CompilerServices;

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
    private readonly List<EditUndoSnapshot> _undoHistory = [];
    private const int MaxUndoHistory = 20;

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
    private NotebookSessionService? _notebookSession;
    private readonly ObservableCollection<NotebookSource> _notebookSources = [];
    private readonly ObservableCollection<NotebookBlock> _notebookBlocks = [];
    private string? _activeNotebookSourceAlias;
    private CancellationTokenSource? _previewLoadCts;
    private long _previewRowOffset;
    private long _previewFilteredRowCount;
    private bool _isPreviewMode;
    private bool _suppressNotebookSourceSelectionChanged;
    private bool _suppressNotebookBlockSelectionChanged;
    private bool _suppressFilterApply;
    private string? _lastSuggestedNotebookQuery;
    private bool _queryHubEnabled = true;
    private bool _queryHubExpanded = true;
    private MarkdownEditorWindow? _markdownEditorWindow;
    private bool _markdownHelperEmbedded;
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md",
        ".markdown",
        ".mdown",
        ".mkd"
    };
    private static readonly IEqualityComparer<(DataRow Row, string ColumnName)> DataRowColumnNameComparer = new DataRowColumnNameReferenceComparer();

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
        InitializeNotebookHub();
        RefreshSavedNotebookQueries();
        RefreshSchemaTemplates();
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

        UpdateNotebookUiState();
    }

    private void InitializeNotebookHub()
    {
        NotebookSourcesList.ItemsSource = _notebookSources;
        NotebookBlocksList.ItemsSource = _notebookBlocks;
        NotebookSourceHintText.Text = "Open a source to start building local notebook steps.";
        NotebookLastCheckText.Text = "Trust checks run locally against the active source or query result.";

        NotebookQueryTextBox.TextChanged += (_, _) => UpdateNotebookUiState();
        SavedNotebookQueriesComboBox.SelectionChanged += (_, _) => UpdateNotebookUiState();
        SchemaTemplatesComboBox.SelectionChanged += (_, _) => UpdateNotebookUiState();
    }

    private NotebookSessionService GetNotebookSession()
    {
        if (_notebookSession != null)
            return _notebookSession;

        var logger = App.Current.Services.GetService<ILogger<NotebookSessionService>>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NotebookSessionService>.Instance;

        _notebookSession = new NotebookSessionService(logger);
        return _notebookSession;
    }

    private void RefreshSavedNotebookQueries()
    {
        SavedNotebookQueriesComboBox.ItemsSource = _workspaceService.GetNotebookQueries()
            .OrderByDescending(query => query.UpdatedAtUtc)
            .ToList();
        UpdateNotebookUiState();
    }

    private void RefreshSchemaTemplates()
    {
        SchemaTemplatesComboBox.ItemsSource = _workspaceService.GetSchemaTemplates()
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UpdateNotebookUiState();
    }

    private void AddNotebookBlock(NotebookBlockKind kind, string title, string summary, string? sourceAlias = null, string? sql = null)
    {
        _notebookBlocks.Insert(0, new NotebookBlock
        {
            Kind = kind,
            Title = title,
            Summary = summary,
            SourceAlias = sourceAlias,
            Sql = sql
        });

        while (_notebookBlocks.Count > 60)
            _notebookBlocks.RemoveAt(_notebookBlocks.Count - 1);
    }

    private void UpdateNotebookUiState()
    {
        var hasSources = _notebookSources.Count > 0;
        var hasActiveSource = hasSources && !string.IsNullOrWhiteSpace(_activeNotebookSourceAlias);

        NotebookHub.Visibility = hasSources && _queryHubEnabled && _queryHubExpanded ? Visibility.Visible : Visibility.Collapsed;
        NotebookHubCollapsedBar.Visibility = hasSources && _queryHubEnabled && !_queryHubExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleQueryHubMenuItem.IsChecked = _queryHubEnabled;
        RunNotebookQueryButton.IsEnabled = hasSources && !string.IsNullOrWhiteSpace(NotebookQueryTextBox.Text);
        SaveNotebookQueryButton.IsEnabled = !string.IsNullOrWhiteSpace(NotebookQueryTextBox.Text);
        LoadSavedNotebookQueryButton.IsEnabled = SavedNotebookQueriesComboBox.SelectedItem is NotebookQueryDocument;
        DeleteSavedNotebookQueryButton.IsEnabled = SavedNotebookQueriesComboBox.SelectedItem is NotebookQueryDocument;
        MaterializeWorkingSetButton.IsEnabled = hasActiveSource;
        NotebookExportButton.IsEnabled = hasActiveSource;
        NullEmptyCheckButton.IsEnabled = hasActiveSource;
        DuplicateCheckButton.IsEnabled = hasActiveSource;
        RegexCheckButton.IsEnabled = hasActiveSource;
        SaveSchemaTemplateButton.IsEnabled = hasActiveSource;
        ValidateSchemaTemplateButton.IsEnabled = hasActiveSource && SchemaTemplatesComboBox.SelectedItem is SchemaTemplate;
        ClearNotebookBlocksButton.IsEnabled = _notebookBlocks.Count > 0;

        PreviewPreviousPageButton.IsEnabled = hasActiveSource && _isPreviewMode && _previewRowOffset > 0;
        PreviewNextPageButton.IsEnabled = hasActiveSource && _isPreviewMode && (_previewRowOffset + _currentRowLimit) < _previewFilteredRowCount;
        MaterializeWorkingSetButton.Content = _isPreviewMode ? "Load Current Scope as Working Set" : "Working Set Loaded";
        MaterializeWorkingSetButton.IsEnabled = hasActiveSource && _isPreviewMode;
        NotebookQueryLabelText.Text = hasActiveSource
            ? $"Run local DuckDB SQL over opened notebook aliases. Active source: {_activeNotebookSourceAlias}."
            : "Run local DuckDB SQL over any opened notebook source alias.";

        if (!hasSources)
        {
            NotebookActiveSourceText.Text = "No active source";
            PreviewPageStatusText.Text = "Preview controls appear for large sources.";
            SetNotebookModeBadge("Ready", "#E3F2FD", "#1565C0");
            return;
        }

        NotebookSourceHintText.Text = BuildNotebookAliasHint();

        if (!hasActiveSource)
        {
            NotebookActiveSourceText.Text = "Choose a source to preview or query.";
            PreviewPageStatusText.Text = "Preview controls appear for large sources.";
            SetNotebookModeBadge("Notebook", "#E8F5E9", "#2E7D32");
            return;
        }

        if (_isPreviewMode)
            SetNotebookModeBadge("Read-Only Preview", "#FFF3E0", "#E65100");
        else
            SetNotebookModeBadge("Editable Working Set", "#E8F5E9", "#2E7D32");
    }

    private void SetNotebookModeBadge(string text, string backgroundHex, string foregroundHex)
    {
        NotebookModeBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundHex));
        NotebookModeBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foregroundHex));
        NotebookModeBadgeText.Text = text;
    }

    private string BuildNotebookAliasHint()
    {
        if (_notebookSources.Count == 0)
            return "Open a source to start building local notebook steps.";

        var aliases = string.Join(", ", _notebookSources.Select(source => source.Alias));
        return $"DuckDB aliases: {aliases}. Select a notebook block to reopen its source or SQL.";
    }

    private void SuggestNotebookQuery(NotebookSource source)
    {
        var suggestedQuery = BuildSuggestedNotebookQuery(source);
        var currentQuery = NotebookQueryTextBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(currentQuery) ||
            string.Equals(currentQuery, _lastSuggestedNotebookQuery, StringComparison.Ordinal))
        {
            NotebookQueryTextBox.Text = suggestedQuery;
            NotebookQueryTextBox.SelectAll();
        }

        _lastSuggestedNotebookQuery = suggestedQuery;
    }

    private static string BuildSuggestedNotebookQuery(NotebookSource source)
        => $"SELECT *{Environment.NewLine}FROM {source.Alias}{Environment.NewLine}LIMIT 100;";

    private static List<ColumnInfo> BuildColumnInfos(NotebookSource source)
        => source.Columns
            .Select(column => new ColumnInfo
            {
                Name = column.Name,
                Type = column.Type,
                Nullable = column.Nullable
            })
            .ToList();

    private static IReadOnlyList<string> ResolveNotebookSourceFiles(NotebookSource source)
    {
        if (source.FilePaths.Count > 0)
            return source.FilePaths;

        if (!string.IsNullOrWhiteSpace(source.FilePath))
            return [source.FilePath];

        return [source.DisplayName];
    }

    private async Task<NotebookSource> RegisterNotebookFileSourceAsync(IReadOnlyList<string> filePaths, DataFileInfo fileInfo)
    {
        var session = GetNotebookSession();
        var notebookColumns = fileInfo.Columns
            .Select(column => new NotebookColumnSchema
            {
                Name = column.Name,
                Type = column.Type,
                Nullable = column.Nullable
            })
            .ToList();

        var source = await session.RegisterFileSourceAsync(
            filePaths,
            _activeCsvOptions,
            _activeJsonOptions,
            knownColumns: notebookColumns,
            knownRowCount: fileInfo.RowCount,
            knownFormat: fileInfo.Format);

        _notebookSources.Add(source);
        AddNotebookBlock(
            NotebookBlockKind.Source,
            source.DisplayName,
            $"{source.RowCount:N0} rows available as `{source.Alias}`.",
            source.Alias);

        UpdateNotebookUiState();
        return source;
    }

    private void SelectActiveNotebookSource(string alias)
    {
        _activeNotebookSourceAlias = alias;
        _suppressNotebookSourceSelectionChanged = true;
        NotebookSourcesList.SelectedItem = _notebookSources.FirstOrDefault(source => string.Equals(source.Alias, alias, StringComparison.OrdinalIgnoreCase));
        _suppressNotebookSourceSelectionChanged = false;
        UpdateNotebookUiState();
    }

    private bool TryGetActiveNotebookSource(out NotebookSource source)
    {
        source = _notebookSources.FirstOrDefault(item => string.Equals(item.Alias, _activeNotebookSourceAlias, StringComparison.OrdinalIgnoreCase))
            ?? new NotebookSource();
        return !string.IsNullOrWhiteSpace(source.Alias);
    }

    private async Task ActivateNotebookSourceAsync(
        NotebookSource source,
        IReadOnlyList<SourceFileSummary>? sourceFiles = null,
        string parquetPartsSuffix = "",
        string? unknownExtension = null,
        bool autoAnalyze = true)
    {
        var previousAlias = _activeNotebookSourceAlias;
        var sourceChanged = !string.Equals(previousAlias, source.Alias, StringComparison.OrdinalIgnoreCase);
        if (sourceChanged)
            ResetGridQueryUi();

        SelectActiveNotebookSource(source.Alias);
        SuggestNotebookQuery(source);
        NotebookActiveSourceText.Text = $"{source.DisplayName} ({source.Alias})";
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        DataGridContainer.Visibility = Visibility.Visible;
        _currentFilePath = source.FilePath;
        _currentFilePaths = source.FilePaths.Count > 0 ? source.FilePaths : null;
        _activeCsvOptions = source.CsvOptions;
        _activeJsonOptions = source.JsonOptions;
        if (source.Format.HasValue)
            _currentFormat = source.Format.Value;
        UpdateSchemaPanelForNotebookSource(source);
        UpdateFormatBadgeForNotebookSource(source);

        ConfigureQualityContextForSource(source, sourceFiles, autoAnalyze);

        if (source.RowCount > RowLimitBatch)
        {
            await LoadNotebookPreviewAsync(source, parquetPartsSuffix, unknownExtension, resetPaging: true);
            return;
        }

        await LoadNotebookWorkingSetAsync(
            source,
            queryState: null,
            preserveOriginalSaveTarget: source.Kind == NotebookSourceKind.File,
            parquetPartsSuffix: parquetPartsSuffix,
            unknownExtension: unknownExtension,
            clearFiltersAfterLoad: false);
    }

    private void ConfigureQualityContextForSource(
        NotebookSource source,
        IReadOnlyList<SourceFileSummary>? sourceFiles,
        bool autoAnalyze)
    {
        if (source.Kind == NotebookSourceKind.File && !string.IsNullOrWhiteSpace(source.FilePath))
        {
            SetupFileWatcher(source.FilePath);
        }
        else
        {
            DisposeFileWatcher();
        }

        var qualitySourcePath = !string.IsNullOrWhiteSpace(source.FilePath)
            ? source.FilePath
            : source.FilePaths.FirstOrDefault() ?? source.DisplayName;

        _qualityViewModel?.SetAnalysisContext(
            qualitySourcePath,
            (cancellationToken, progress) => BuildQualityProfileForSourceAsync(source, cancellationToken, progress),
            (selectedDimensions, cancellationToken, progress) => BuildQualityGroupedStatisticsForSourceAsync(source, selectedDimensions, cancellationToken, progress),
            source.CsvOptions,
            source.JsonOptions,
            sourceFiles,
            GetQualityReadyMessage(source, sourceFiles));

        if (autoAnalyze && source.RowCount <= AutoProfileRowThreshold)
            _qualityViewModel?.StartAutoAnalyze();
    }

    private string GetQualityReadyMessage(NotebookSource source, IReadOnlyList<SourceFileSummary>? sourceFiles)
    {
        if (source.Kind != NotebookSourceKind.File)
            return $"{source.DisplayName} is active in Query Hub. Profiling will analyze the current set.";

        if (sourceFiles != null && sourceFiles.Count > 1)
            return $"{sourceFiles.Count} parquet files loaded as one logical table. Profiling will analyze the current set.";

        return "File loaded. Profiling will analyze the current set.";
    }

    private async Task<FileProfile> BuildQualityProfileForSourceAsync(
        NotebookSource source,
        CancellationToken cancellationToken,
        IProgress<(int Current, int Total)>? progress)
    {
        var tempPath = CreateQualityTempFilePath();

        try
        {
            var queryState = CreateQueryStateForSource(source, _currentRowLimit, 0);
            await GetNotebookSession().ExportSourceAsync(source.Alias, tempPath, queryState, cancellationToken);

            var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
            using var parquetService = new ParquetService(logger);
            var profile = await parquetService.GetFileProfileAsync(tempPath, cancellationToken: cancellationToken, progress: progress);

            profile.FilePath = source.FilePath ?? source.DisplayName;
            profile.FileName = queryState.HasActiveFilters
                ? $"{source.DisplayName} (current set)"
                : source.DisplayName;

            if (source.Format.HasValue)
                profile.SourceFormat = source.Format.Value;

            return profile;
        }
        finally
        {
            TryDeleteTempQualityFile(tempPath);
        }
    }

    private async Task<Dictionary<string, FileProfile>> BuildQualityGroupedStatisticsForSourceAsync(
        NotebookSource source,
        IReadOnlyList<string> selectedDimensions,
        CancellationToken cancellationToken,
        IProgress<(int Current, int Total)>? progress)
    {
        var tempPath = CreateQualityTempFilePath();

        try
        {
            var queryState = CreateQueryStateForSource(source, _currentRowLimit, 0);
            await GetNotebookSession().ExportSourceAsync(source.Alias, tempPath, queryState, cancellationToken);

            var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
            using var parquetService = new ParquetService(logger);
            return await parquetService.GetGroupedStatisticsAsync(
                tempPath,
                selectedDimensions.ToList(),
                cancellationToken: cancellationToken,
                progress: progress);
        }
        finally
        {
            TryDeleteTempQualityFile(tempPath);
        }
    }

    private static string CreateQualityTempFilePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HipHipParquet", "quality-cache");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"quality-{Guid.NewGuid():N}.parquet");
    }

    private static void TryDeleteTempQualityFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup for transient quality review exports.
        }
    }

    private async Task LoadNotebookPreviewAsync(
        NotebookSource source,
        string parquetPartsSuffix = "",
        string? unknownExtension = null,
        bool resetPaging = false)
    {
        if (resetPaging)
            _previewRowOffset = 0;

        _previewLoadCts?.Cancel();
        _previewLoadCts?.Dispose();
        _previewLoadCts = new CancellationTokenSource();

        try
        {
            ShowLoading($"Previewing {source.DisplayName}...");

            var queryState = CreateQueryStateForSource(source, RowLimitBatch, _previewRowOffset);
            var dataTable = await GetNotebookSession().GetPreviewPageAsync(source.Alias, queryState, _previewLoadCts.Token);

            ShowLoading("Indexing preview rows...");
            await Task.Run(() => EnsureRowMetadataColumns(dataTable, ResolveNotebookSourceFiles(source)));

            _suppressFilterApply = true;
            try
            {
                SetupDataGrid(dataTable, BuildColumnInfos(source));
            }
            finally
            {
                _suppressFilterApply = false;
            }

            _isPreviewMode = true;
            _previewFilteredRowCount = queryState.FilteredRowCount;
            _totalRowCount = source.RowCount;
            _currentRowLimit = RowLimitBatch;
            _hasUnsavedChanges = false;
            _pendingEditCount = 0;
            ResetUndoHistory();
            UpdateWindowTitle();
            UpdatePendingChangesTray();
            EnableSaveMenuItems();
            LoadMoreBanner.Visibility = Visibility.Collapsed;
            DataGrid.IsReadOnly = true;
            UpdateEditMenuState();
            UpdateContextMenuState();
            UpdateRowCount();

            var firstRow = dataTable.Rows.Count > 0 ? _previewRowOffset + 1 : 0;
            var lastRow = _previewRowOffset + dataTable.Rows.Count;
            PreviewPageStatusText.Text = dataTable.Rows.Count == 0
                ? "No rows match the current scope."
                : $"Rows {firstRow:N0}-{lastRow:N0} of {_previewFilteredRowCount:N0} in the current scope";

            StatusText.Text = dataTable.Rows.Count == 0
                ? $"Previewing {source.DisplayName} - no rows matched the current scope"
                : $"Previewing {source.DisplayName}{parquetPartsSuffix} - rows {firstRow:N0}-{lastRow:N0} of {_previewFilteredRowCount:N0}";

            if (!string.IsNullOrWhiteSpace(unknownExtension))
                StatusText.Text += $" - unknown extension '{unknownExtension}' treated as CSV";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            PreviewPageStatusText.Text = "Preview failed.";
            StatusText.Text = $"Failed to preview {source.DisplayName}: {ex.Message}";
            return;
        }
        finally
        {
            UpdateNotebookUiState();
            HideLoading();
        }
    }

    private async Task LoadNotebookWorkingSetAsync(
        NotebookSource source,
        GridQueryState? queryState,
        bool preserveOriginalSaveTarget,
        string parquetPartsSuffix = "",
        string? unknownExtension = null,
        bool clearFiltersAfterLoad = false)
    {
        try
        {
            ShowLoading($"Loading working set from {source.DisplayName}...");

            var dataTable = await GetNotebookSession().MaterializeSourceAsync(source.Alias, queryState);

            ShowLoading("Indexing working set...");
            await Task.Run(() => EnsureRowMetadataColumns(dataTable, ResolveNotebookSourceFiles(source)));

            if (clearFiltersAfterLoad)
                ResetGridQueryUi();

            SetupDataGrid(dataTable, BuildColumnInfos(source));

            _isPreviewMode = false;
            _previewRowOffset = 0;
            _previewFilteredRowCount = dataTable.Rows.Count;
            _totalRowCount = dataTable.Rows.Count;
            _currentRowLimit = dataTable.Rows.Count;
            _hasUnsavedChanges = false;
            _pendingEditCount = 0;
            ResetUndoHistory();
            UpdateWindowTitle();
            UpdatePendingChangesTray();
            LoadMoreBanner.Visibility = Visibility.Collapsed;
            DataGrid.IsReadOnly = false;
            UpdateEditMenuState();
            UpdateContextMenuState();

            if (preserveOriginalSaveTarget)
            {
                _currentFilePath = source.FilePath;
                _currentFilePaths = ResolveNotebookSourceFiles(source);
            }
            else
            {
                _currentFilePath = null;
                _currentFilePaths = null;
                DisposeFileWatcher();
                if (source.Kind == NotebookSourceKind.File)
                    _qualityViewModel?.ClearFile();
            }

            EnableSaveMenuItems();
            UpdateRowCount();

            StatusText.Text = $"Loaded {dataTable.Rows.Count:N0} rows from {source.DisplayName}{parquetPartsSuffix} as an editable working set";
            if (!string.IsNullOrWhiteSpace(unknownExtension))
                StatusText.Text += $" - unknown extension '{unknownExtension}' treated as CSV";
        }
        finally
        {
            UpdateNotebookUiState();
            HideLoading();
        }
    }

    private void ResetGridQueryUi()
    {
        foreach (var kvp in _columnFilters)
        {
            kvp.Value.IsActive = false;
            kvp.Value.IsLoaded = false;
            kvp.Value.SelectedValues.Clear();
        }

        _savedColumnFilterSelections.Clear();
        _savedGlobalSearch = string.Empty;
        _savedSort = string.Empty;

        GlobalSearchBox.TextChanged -= OnGlobalSearchTextChanged;
        GlobalSearchBox.Text = string.Empty;
        GlobalSearchBox.TextChanged += OnGlobalSearchTextChanged;

        foreach (var column in DataGrid.Columns)
            column.SortDirection = null;

        UpdateFilterBadge();
    }

    private GridQueryState CreateQueryStateForSource(NotebookSource source, int rowLimit, long rowOffset)
    {
        var queryState = BuildCurrentQueryState();
        queryState.AvailableColumns = source.Columns.Select(column => column.Name).ToList();
        queryState.RowLimit = rowLimit;
        queryState.RowOffset = (int)Math.Min(int.MaxValue, rowOffset);
        queryState.TotalRowCount = source.RowCount;
        queryState.CsvOptions = source.CsvOptions;
        queryState.JsonOptions = source.JsonOptions;
        queryState.SourceFilePath = source.FilePath ?? string.Empty;
        return queryState;
    }

    private void UpdateSchemaPanelForNotebookSource(NotebookSource source)
    {
        var info = new DataFileInfo
        {
            FilePath = source.FilePath ?? source.DisplayName,
            Format = source.Format ?? SupportedFileFormat.Parquet,
            RowCount = source.RowCount,
            SourceFiles = source.FilePaths.ToList(),
            Columns = BuildColumnInfos(source)
        };

        UpdateSchemaPanel(source.FilePath ?? source.DisplayName, info);
    }

    private void UpdateFormatBadgeForNotebookSource(NotebookSource source)
    {
        if (source.Format.HasValue)
        {
            UpdateFormatBadge(source.Format.Value);
            return;
        }

        FormatBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ECEFF1"));
        FormatBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#37474F"));
        FormatBadgeText.Text = "DuckDB";
        FormatBadge.Visibility = Visibility.Visible;
    }

    private void DisposeFileWatcher()
    {
        if (_fileWatcher == null)
            return;

        _fileWatcher.EnableRaisingEvents = false;
        _fileWatcher.Dispose();
        _fileWatcher = null;
    }

    private async void OnNotebookSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNotebookSourceSelectionChanged || NotebookSourcesList.SelectedItem is not NotebookSource source)
            return;

        await ActivateNotebookSourceAsync(source, autoAnalyze: false);
    }

    private async void OnNotebookBlockSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNotebookBlockSelectionChanged || NotebookBlocksList.SelectedItem is not NotebookBlock block)
            return;

        _suppressNotebookBlockSelectionChanged = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(block.Sql))
            {
                _lastSuggestedNotebookQuery = null;
                NotebookQueryTextBox.Text = block.Sql;
            }

            if (!string.IsNullOrWhiteSpace(block.SourceAlias) &&
                _notebookSources.FirstOrDefault(item => string.Equals(item.Alias, block.SourceAlias, StringComparison.OrdinalIgnoreCase)) is { } source)
            {
                await ActivateNotebookSourceAsync(source, autoAnalyze: false);
            }
        }
        finally
        {
            NotebookBlocksList.SelectedItem = null;
            _suppressNotebookBlockSelectionChanged = false;
        }
    }

    private void OnRemoveNotebookBlockClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NotebookBlock block)
            return;

        _notebookBlocks.Remove(block);
        UpdateNotebookUiState();
        StatusText.Text = "Removed notebook block";
        e.Handled = true;
    }

    private void OnClearNotebookBlocksClick(object sender, RoutedEventArgs e)
    {
        if (_notebookBlocks.Count == 0)
            return;

        _notebookBlocks.Clear();
        UpdateNotebookUiState();
        StatusText.Text = "Cleared notebook blocks";
    }

    private async void OnRunNotebookQueryClick(object sender, RoutedEventArgs e)
    {
        if (_notebookSources.Count == 0)
        {
            StatusText.Text = "Open a source before running notebook queries";
            return;
        }

        try
        {
            ShowLoading("Running DuckDB query...");
            var sql = NotebookQueryTextBox.Text;
            var resultSource = await GetNotebookSession().ExecuteReadOnlyQueryAsync(sql, preferredAlias: "query_result");
            _notebookSources.Add(resultSource);
            AddNotebookBlock(NotebookBlockKind.Query, "DuckDB Query", $"Created `{resultSource.Alias}` from the current SQL.", resultSource.Alias, sql);
            AddNotebookBlock(NotebookBlockKind.Result, resultSource.DisplayName, $"{resultSource.RowCount:N0} rows available in `{resultSource.Alias}`.", resultSource.Alias);
            await ActivateNotebookSourceAsync(resultSource, autoAnalyze: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Query failed: {ex.Message}", "Query Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Notebook query failed";
        }
        finally
        {
            UpdateNotebookUiState();
            HideLoading();
        }
    }

    private async void OnSaveNotebookQueryClick(object sender, RoutedEventArgs e)
    {
        var sql = NotebookQueryTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusText.Text = "Enter a query before saving it";
            return;
        }

        var name = GridActionDialogs.ShowSingleTextInputDialog(
            this,
            "Save Query",
            "Query name",
            $"Query {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            confirmText: "Save");

        if (string.IsNullOrWhiteSpace(name))
            return;

        await _workspaceService.SaveNotebookQueryAsync(new NotebookQueryDocument
        {
            Name = name.Trim(),
            Sql = sql,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        RefreshSavedNotebookQueries();
        StatusText.Text = $"Saved notebook query '{name.Trim()}'";
    }

    private void OnLoadSavedNotebookQueryClick(object sender, RoutedEventArgs e)
    {
        if (SavedNotebookQueriesComboBox.SelectedItem is not NotebookQueryDocument query)
        {
            StatusText.Text = "Choose a saved query first";
            return;
        }

        _lastSuggestedNotebookQuery = null;
        NotebookQueryTextBox.Text = query.Sql;
        StatusText.Text = $"Loaded query '{query.Name}'";
    }

    private async void OnDeleteSavedNotebookQueryClick(object sender, RoutedEventArgs e)
    {
        if (SavedNotebookQueriesComboBox.SelectedItem is not NotebookQueryDocument query)
        {
            StatusText.Text = "Choose a saved query first";
            return;
        }

        var result = MessageBox.Show(
            $"Delete saved query '{query.Name}'?",
            "Delete Saved Query",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        await _workspaceService.DeleteNotebookQueryAsync(query.Name);
        RefreshSavedNotebookQueries();
        StatusText.Text = $"Deleted saved query '{query.Name}'";
    }

    private async void OnMaterializeWorkingSetClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveNotebookSource(out var source))
        {
            StatusText.Text = "Choose a notebook source first";
            return;
        }

        if (!_isPreviewMode)
        {
            StatusText.Text = "The active source is already loaded as an editable working set";
            return;
        }

        var scopeState = CreateQueryStateForSource(source, _currentRowLimit, 0);
        var scopeCount = _previewFilteredRowCount > 0 ? _previewFilteredRowCount : source.RowCount;
        if (scopeCount > 250_000)
        {
            var confirm = MessageBox.Show(
                $"This working set will materialize {scopeCount:N0} rows into memory.\n\nContinue?",
                "Large Working Set",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var preserveOriginalSaveTarget =
            source.Kind == NotebookSourceKind.File &&
            source.FilePaths.Count == 1 &&
            !scopeState.HasActiveFilters &&
            scopeCount == source.RowCount;

        await LoadNotebookWorkingSetAsync(
            source,
            scopeState,
            preserveOriginalSaveTarget,
            clearFiltersAfterLoad: true);

        AddNotebookBlock(
            NotebookBlockKind.Result,
            $"{source.DisplayName} working set",
            $"{_totalRowCount:N0} rows loaded into an editable working set.",
            source.Alias);
    }

    private async void OnNotebookExportClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveNotebookSource(out var source))
        {
            StatusText.Text = "Choose a notebook source first";
            return;
        }

        if (!_isPreviewMode)
        {
            OnExportAsClick(sender, e);
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Filter = Services.FileFormatDetector.GetSaveFileDialogFilter(),
            Title = "Export Current Notebook Scope",
            FileName = Path.GetFileNameWithoutExtension(source.DisplayName)
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        try
        {
            ShowLoading("Exporting notebook scope...");
            var scopeState = CreateQueryStateForSource(source, _currentRowLimit, 0);
            await GetNotebookSession().ExportSourceAsync(source.Alias, saveFileDialog.FileName, scopeState);
            AddNotebookBlock(
                NotebookBlockKind.Export,
                Path.GetFileName(saveFileDialog.FileName),
                $"Exported the current notebook scope to {Path.GetFileName(saveFileDialog.FileName)}.",
                source.Alias);
            StatusText.Text = $"Exported {Path.GetFileName(saveFileDialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Notebook Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Notebook export failed";
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnNotebookNullEmptyCheckClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveNotebookSource(out var source))
        {
            StatusText.Text = "Choose a notebook source first";
            return;
        }

        try
        {
            ShowLoading("Running null / empty check...");
            var result = await GetNotebookSession().RunNullEmptyCheckAsync(source.Alias);
            RenderNotebookValidationResult(source.Alias, result);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Null / empty check failed: {ex.Message}", "Notebook Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Null / empty check failed";
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnNotebookDuplicateCheckClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveNotebookSource(out var source))
        {
            StatusText.Text = "Choose a notebook source first";
            return;
        }

        var preselectedColumns = GetSelectedColumnNamesFromCells()
            .Where(name => source.Columns.Any(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var chosenColumns = GridActionDialogs.ShowColumnPickerDialog(
            this,
            "Duplicate Check",
            "Choose the column(s) that define a duplicate row key.",
            source.Columns.Select(column => column.Name).ToList(),
            preselectedColumns);

        if (chosenColumns == null || chosenColumns.Count == 0)
            return;

        try
        {
            ShowLoading("Running duplicate check...");
            var result = await GetNotebookSession().RunDuplicateCheckAsync(source.Alias, chosenColumns);
            RenderNotebookValidationResult(source.Alias, result);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Duplicate check failed: {ex.Message}", "Notebook Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Duplicate check failed";
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnNotebookRegexCheckClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveNotebookSource(out var source))
        {
            StatusText.Text = "Choose a notebook source first";
            return;
        }

        var regexOptions = GridActionDialogs.ShowRegexCheckDialog(
            this,
            source.Columns.Select(column => column.Name).ToList());

        if (regexOptions == null)
            return;

        try
        {
            ShowLoading("Running regex check...");
            var result = await GetNotebookSession().RunRegexCheckAsync(source.Alias, regexOptions.ColumnName, regexOptions.Pattern);
            RenderNotebookValidationResult(source.Alias, result);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Regex check failed: {ex.Message}", "Notebook Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Regex check failed";
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnSaveSchemaTemplateClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveNotebookSource(out var source))
        {
            StatusText.Text = "Choose a notebook source first";
            return;
        }

        var templateName = GridActionDialogs.ShowSingleTextInputDialog(
            this,
            "Save Schema Template",
            "Template name",
            $"{source.DisplayName} template",
            confirmText: "Save");

        if (string.IsNullOrWhiteSpace(templateName))
            return;

        var template = new SchemaTemplate
        {
            Name = templateName.Trim(),
            Description = $"Saved from {source.DisplayName}",
            CreatedAtUtc = DateTime.UtcNow,
            Columns = source.Columns
                .Select(column => new SchemaTemplateColumn
                {
                    Name = column.Name,
                    Type = column.Type,
                    Nullable = column.Nullable,
                    Required = true
                })
                .ToList()
        };

        await _workspaceService.SaveSchemaTemplateAsync(template);
        RefreshSchemaTemplates();
        SchemaTemplatesComboBox.SelectedItem = ((IEnumerable<SchemaTemplate>)SchemaTemplatesComboBox.ItemsSource!).FirstOrDefault(item => item.Name == template.Name);
        AddNotebookBlock(NotebookBlockKind.Validation, template.Name, $"Saved schema template with {template.Columns.Count:N0} columns.", source.Alias);
        StatusText.Text = $"Saved schema template '{template.Name}'";
    }

    private async void OnValidateSchemaTemplateClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveNotebookSource(out var source))
        {
            StatusText.Text = "Choose a notebook source first";
            return;
        }

        if (SchemaTemplatesComboBox.SelectedItem is not SchemaTemplate template)
        {
            StatusText.Text = "Choose a schema template first";
            return;
        }

        try
        {
            ShowLoading("Validating schema template...");
            var result = await GetNotebookSession().ValidateSchemaTemplateAsync(source.Alias, template);
            RenderNotebookValidationResult(source.Alias, result);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Schema validation failed: {ex.Message}", "Notebook Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Schema validation failed";
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnPreviewPreviousPageClick(object sender, RoutedEventArgs e)
    {
        if (!_isPreviewMode || !TryGetActiveNotebookSource(out var source))
            return;

        _previewRowOffset = Math.Max(0, _previewRowOffset - RowLimitBatch);
        await LoadNotebookPreviewAsync(source);
    }

    private async void OnPreviewNextPageClick(object sender, RoutedEventArgs e)
    {
        if (!_isPreviewMode || !TryGetActiveNotebookSource(out var source))
            return;

        _previewRowOffset += RowLimitBatch;
        await LoadNotebookPreviewAsync(source);
    }

    private void RenderNotebookValidationResult(string sourceAlias, NotebookValidationResult result)
    {
        var summaryBuilder = new System.Text.StringBuilder();
        summaryBuilder.Append(result.Summary);

        foreach (var finding in result.Findings.Take(3))
            summaryBuilder.AppendLine().Append("- ").Append(finding.Message);

        NotebookLastCheckText.Text = summaryBuilder.ToString();
        AddNotebookBlock(NotebookBlockKind.Validation, result.Title, result.Summary, sourceAlias);
        StatusText.Text = result.Summary;
    }
    
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            // Second pass — let the close proceed and clean up.
            SaveWorkspaceState();
            _parquetService?.Dispose();
            _notebookSession?.Dispose();
            _fileWatcher?.Dispose();
            _previewLoadCts?.Cancel();
            _previewLoadCts?.Dispose();
            return;
        }

        if (!_hasUnsavedChanges)
        {
            SaveWorkspaceState();
            _parquetService?.Dispose();
            _notebookSession?.Dispose();
            _fileWatcher?.Dispose();
            _previewLoadCts?.Cancel();
            _previewLoadCts?.Dispose();
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
            bool saved;
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = Services.FileFormatDetector.GetSaveFileDialogFilter(),
                    Title = "Save Data File",
                    FileName = "untitled.parquet"
                };

                if (saveFileDialog.ShowDialog() == true)
                    saved = await SaveFileAsync(saveFileDialog.FileName);
                else
                    return; // user cancelled save dialog → keep window open
            }
            else
            {
                saved = await SaveFileAsync(_currentFilePath);
            }

            if (!saved)
                return; // save failed — keep window open so the user doesn't lose data
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
            Title = "Select File",
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

    private async Task LoadParquetFilesAsSingleTableAsync(IReadOnlyList<string> filePaths, int? rowLimit = null)
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

            var effectiveLimit = rowLimit ?? RowLimitBatch;
            _currentRowLimit = effectiveLimit;

            var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
            _parquetService?.Dispose();
            _parquetService = new ParquetService(logger);

            var fileInfo = await _parquetService.GetFileInfoAsync(orderedFilePaths);
            _totalRowCount = fileInfo.RowCount;

            UpdateSchemaPanel(orderedFilePaths[0], fileInfo);

            foreach (var filePath in orderedFilePaths)
                AddToRecentFiles(filePath);

            // Multi-file parquet loads are treated as a logical table, not a single source file.
            // Keep the list of current paths so paging can work, but avoid overwriting source files.
            _currentFilePaths = orderedFilePaths;
            _currentFilePath = orderedFilePaths.Count == 1 ? orderedFilePaths[0] : null;
            _hasUnsavedChanges = false;
            _pendingEditCount = 0;
            ResetUndoHistory();
            UpdateWindowTitle();
            UpdatePendingChangesTray();
            EnableSaveMenuItems();
            UpdateFormatBadge(SupportedFileFormat.Parquet);

            var notebookSource = await RegisterNotebookFileSourceAsync(orderedFilePaths, fileInfo);
            await ActivateNotebookSourceAsync(notebookSource, fileInfo.SourceFileSummaries);

            StatusText.Text = $"Loaded {orderedFilePaths.Count:N0} parquet files into the notebook workspace";
            // Legacy in-place status text removed in favor of notebook activation status.
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
        if (IsMarkdownPath(filePath))
        {
            await OpenMarkdownHelperForFileAsync(filePath);
            return;
        }

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
            || MarkdownExtensions.Contains(ext)
            || Path.GetFileName(path).EndsWith(".snappy.parquet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMarkdownPath(string path)
    {
        var ext = Path.GetExtension(path);
        return MarkdownExtensions.Contains(ext);
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
                QualityPanelShell.Visibility = Visibility.Visible;
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
                QualityPanelShell.Visibility = Visibility.Collapsed;
                QualityReviewPanel.Visibility = Visibility.Collapsed;
                QualitySplitter.Visibility = Visibility.Collapsed;
                QualitySplitterColumn.Width = new GridLength(0);
                QualityPaneColumn.MinWidth = 0;
                QualityPaneColumn.Width = new GridLength(0);
            }
        }
    }

    private void OnToggleQueryHubClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
            _queryHubEnabled = menuItem.IsChecked;

        UpdateNotebookUiState();
    }

    private void OnCollapseQueryHubClick(object sender, RoutedEventArgs e)
    {
        _queryHubExpanded = false;

        UpdateNotebookUiState();
    }

    private void OnExpandQueryHubClick(object sender, RoutedEventArgs e)
    {
        _queryHubEnabled = true;
        _queryHubExpanded = true;

        UpdateNotebookUiState();
    }

    private void OnQualitySplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        ClampQualityPaneWidth();
    }

    private void ClampQualityPaneWidth()
    {
        if (QualityPanelShell.Visibility != Visibility.Visible)
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

                if (e.PropertyName is nameof(QualityReviewViewModel.HasProfile)
                    or nameof(QualityReviewViewModel.IsProfileStale)
                    or nameof(QualityReviewViewModel.IsAnalyzing))
                {
                    UpdateQualityStaleBadge();
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
        CaptureCurrentSelection();
        UpdateEditMenuState();
    }

    private void OnEditMenuClosed(object sender, RoutedEventArgs e)
    {
        ClearCapturedSelection();
    }

    private void OnDataGridContextMenuOpened(object sender, RoutedEventArgs e)
    {
        CaptureCurrentSelection();
        UpdateContextMenuState();
    }

    private void OnDataGridContextMenuClosed(object sender, RoutedEventArgs e)
    {
        ClearCapturedSelection();
    }

    private void CaptureCurrentSelection()
    {
        _savedSelectedCells = DataGrid.SelectedCells.ToList();
        _savedSelectedItems = DataGrid.SelectedItems.Cast<object>().ToList();
    }

    private void ClearCapturedSelection()
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

    private void OnContextCopyRowsAsCsvClick(object sender, RoutedEventArgs e)
    {
        CopyRowsToClipboard(",", false);
    }

    private void OnContextCopyRowsAsJsonClick(object sender, RoutedEventArgs e)
    {
        CopyRowsAsJsonToClipboard();
    }

    private void OnContextDeleteRowsClick(object sender, RoutedEventArgs e)
    {
        DeleteSelectedRows();
    }

    private void OnContextKeepSelectedRowsClick(object sender, RoutedEventArgs e)
    {
        KeepOnlySelectedRows();
    }

    private void OnContextDeleteUnselectedRowsClick(object sender, RoutedEventArgs e)
    {
        DeleteUnselectedRows();
    }

    private void OnContextDuplicateRowsClick(object sender, RoutedEventArgs e)
    {
        DuplicateSelectedRows();
    }

    private void OnContextInsertBlankRowAboveClick(object sender, RoutedEventArgs e)
    {
        InsertBlankRow(insertBelow: false);
    }

    private void OnContextInsertBlankRowBelowClick(object sender, RoutedEventArgs e)
    {
        InsertBlankRow(insertBelow: true);
    }

    private async void OnContextExportSelectedRowsClick(object sender, RoutedEventArgs e)
    {
        await ExportSelectedRowsAsync();
    }

    private void OnContextDeleteDuplicateRowsClick(object sender, RoutedEventArgs e)
    {
        DeleteDuplicateRows();
    }

    private void OnContextFilterToValueClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentCellFilter(CurrentCellFilterAction.IncludeValue);
    }

    private void OnContextExcludeValueClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentCellFilter(CurrentCellFilterAction.ExcludeValue);
    }

    private void OnContextShowBlankValuesClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentCellFilter(CurrentCellFilterAction.OnlyBlanks);
    }

    private void OnContextSetSelectedCellsToNullClick(object sender, RoutedEventArgs e)
    {
        SetSelectedCellsToNull();
    }

    private void OnContextFillDownClick(object sender, RoutedEventArgs e)
    {
        FillDownSelectedCells();
    }

    private void OnContextTrimWhitespaceClick(object sender, RoutedEventArgs e)
    {
        TrimWhitespaceInSelection();
    }

    private void OnContextReplaceInSelectionClick(object sender, RoutedEventArgs e)
    {
        ReplaceInSelection();
    }

    private async void OnRefreshQualityReviewClick(object sender, RoutedEventArgs e)
    {
        if (_qualityViewModel == null || !_qualityViewModel.HasFile || _qualityViewModel.IsAnalyzing)
            return;

        await _qualityViewModel.AnalyzeCommand.ExecuteAsync(null);
        UpdateQualityStaleBadge();
    }

    private void OnUndoLastActionClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditCurrentWorkingSet())
        {
            StatusText.Text = "Undo is only available for editable working sets";
            return;
        }

        UndoLastEdit();
    }

    private void DeleteSelectedRows()
    {
        if (!CanEditCurrentWorkingSet())
        {
            StatusText.Text = "Row deletion is only available for editable working sets";
            return;
        }

        CommitPendingGridEdits();

        var selectedRows = GetSelectedRowViews();
        if (selectedRows.Count == 0)
        {
            StatusText.Text = "No rows selected to delete";
            return;
        }

        var rowCount = selectedRows.Count;
        var result = MessageBox.Show(
            rowCount == 1
                ? "Delete the selected row from the current dataset? This action cannot be undone."
                : $"Delete {rowCount:N0} selected rows from the current dataset? This action cannot be undone.",
            rowCount == 1 ? "Delete Row" : "Delete Rows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        ExecuteUndoableMutation(
            actionDescription: rowCount == 1 ? "Deleted 1 row" : $"Deleted {rowCount:N0} rows",
            mutation: () => DataTableEditHelper.DeleteRows(_originalData!, selectedRows, RowNumberColumnName),
            successMessageFactory: deletedCount => deletedCount == 1 ? "Deleted 1 row" : $"Deleted {deletedCount:N0} rows",
            structuralChange: true,
            clearSelection: true);
    }

    private void KeepOnlySelectedRows()
    {
        if (_originalData == null)
        {
            StatusText.Text = "No data loaded";
            return;
        }

        CommitPendingGridEdits();

        var selectedRows = GetSelectedRowViews();
        if (selectedRows.Count == 0)
        {
            StatusText.Text = "No rows selected";
            return;
        }

        var rowCount = selectedRows.Count;
        var removedCount = _originalData.Rows.Count - selectedRows.Select(r => r.Row).Distinct().Count();
        if (removedCount <= 0)
        {
            StatusText.Text = "All loaded rows are already selected";
            return;
        }

        var result = MessageBox.Show(
            $"Keep the {rowCount:N0} selected row{(rowCount == 1 ? string.Empty : "s")} and delete the remaining {removedCount:N0} row{(removedCount == 1 ? string.Empty : "s")} from the current dataset?",
            "Keep Only Selected Rows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        ExecuteUndoableMutation(
            actionDescription: rowCount == 1 ? "Kept 1 selected row" : $"Kept {rowCount:N0} selected rows",
            mutation: () => DataTableEditHelper.KeepOnlyRows(_originalData, selectedRows, RowNumberColumnName),
            successMessageFactory: _ => rowCount == 1 ? "Kept 1 selected row" : $"Kept {rowCount:N0} selected rows",
            structuralChange: true,
            clearSelection: true);
    }

    private void DeleteUnselectedRows()
    {
        if (_originalData == null)
        {
            StatusText.Text = "No data loaded";
            return;
        }

        CommitPendingGridEdits();

        var selectedRows = GetSelectedRowViews();
        if (selectedRows.Count == 0)
        {
            StatusText.Text = "No rows selected";
            return;
        }

        var removedCount = _originalData.Rows.Count - selectedRows.Select(r => r.Row).Distinct().Count();
        if (removedCount <= 0)
        {
            StatusText.Text = "No unselected rows to delete";
            return;
        }

        var result = MessageBox.Show(
            $"Delete the {removedCount:N0} unselected row{(removedCount == 1 ? string.Empty : "s")} and keep the current selection?",
            "Delete Unselected Rows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        ExecuteUndoableMutation(
            actionDescription: removedCount == 1 ? "Deleted 1 unselected row" : $"Deleted {removedCount:N0} unselected rows",
            mutation: () => DataTableEditHelper.DeleteUnselectedRows(_originalData, selectedRows, RowNumberColumnName),
            successMessageFactory: deletedCount => deletedCount == 1 ? "Deleted 1 unselected row" : $"Deleted {deletedCount:N0} unselected rows",
            structuralChange: true,
            clearSelection: true);
    }

    private void DuplicateSelectedRows()
    {
        if (_originalData == null)
        {
            StatusText.Text = "No data loaded";
            return;
        }

        CommitPendingGridEdits();

        var selectedRows = GetSelectedRowViews();
        if (selectedRows.Count == 0)
        {
            StatusText.Text = "No rows selected to duplicate";
            return;
        }

        ExecuteUndoableMutation(
            actionDescription: selectedRows.Count == 1 ? "Duplicated 1 row" : $"Duplicated {selectedRows.Count:N0} rows",
            mutation: () => DataTableEditHelper.DuplicateRows(_originalData, selectedRows, RowNumberColumnName),
            successMessageFactory: duplicatedCount => duplicatedCount == 1 ? "Duplicated 1 row" : $"Duplicated {duplicatedCount:N0} rows",
            structuralChange: true,
            clearSelection: true);
    }

    private void InsertBlankRow(bool insertBelow)
    {
        if (_originalData == null)
        {
            StatusText.Text = "No data loaded";
            return;
        }

        CommitPendingGridEdits();

        var anchorRow = GetPrimarySelectedRow();
        if (anchorRow == null)
        {
            StatusText.Text = "Select a row first";
            return;
        }

        ExecuteUndoableMutation(
            actionDescription: insertBelow ? "Inserted a blank row below" : "Inserted a blank row above",
            mutation: () => DataTableEditHelper.InsertBlankRow(_originalData, anchorRow.Row, insertBelow, RowNumberColumnName, SourceFileColumnName),
            successMessageFactory: _ => insertBelow ? "Inserted a blank row below" : "Inserted a blank row above",
            structuralChange: true,
            clearSelection: true);
    }

    private async Task ExportSelectedRowsAsync()
    {
        if (_originalData == null)
        {
            StatusText.Text = "No data loaded";
            return;
        }

        CommitPendingGridEdits();

        var selectedRows = GetSelectedRowViews();
        if (selectedRows.Count == 0)
        {
            StatusText.Text = "No rows selected to export";
            return;
        }

        var baseFileName = string.IsNullOrWhiteSpace(_currentFilePath)
            ? "selected_rows"
            : $"{Path.GetFileNameWithoutExtension(_currentFilePath)}_selected_rows";

        var saveFileDialog = new SaveFileDialog
        {
            Filter = Services.FileFormatDetector.GetSaveFileDialogFilter(),
            Title = "Export Selected Rows...",
            FileName = baseFileName
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        try
        {
            ShowLoading("Exporting selected rows...");

            if (_parquetService == null)
            {
                var logger = App.Current.Services.GetService<ILogger<ParquetService>>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;
                _parquetService = new ParquetService(logger);
            }

            var exportTable = DataTableEditHelper.CreateTableFromRows(_originalData, selectedRows, RowNumberColumnName);
            await _parquetService.SaveFileAsync(saveFileDialog.FileName, exportTable);

            StatusText.Text = $"Exported {selectedRows.Count:N0} selected row{(selectedRows.Count == 1 ? string.Empty : "s")} to {Path.GetFileName(saveFileDialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error exporting selected rows: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Selected-row export failed";
        }
        finally
        {
            HideLoading();
        }
    }

    private void DeleteDuplicateRows()
    {
        if (_originalData == null)
        {
            StatusText.Text = "No data loaded";
            return;
        }

        CommitPendingGridEdits();

        var availableColumns = GetVisibleDataColumnNames();
        if (availableColumns.Count == 0)
        {
            StatusText.Text = "No data columns available";
            return;
        }

        var preselectedColumns = GetSelectedColumnNamesFromCells();
        if (preselectedColumns.Count == 0 && TryGetCurrentCellContext(out var cellContext))
            preselectedColumns = [cellContext.ColumnName];

        var chosenColumns = GridActionDialogs.ShowColumnPickerDialog(
            this,
            "Delete Duplicate Rows",
            "Choose the column(s) used to identify duplicates. The first matching row in each group will be kept.",
            availableColumns,
            preselectedColumns);

        if (chosenColumns == null || chosenColumns.Count == 0)
            return;

        var duplicateCount = DataTableEditHelper.CountDuplicateRows(_originalData, chosenColumns);
        if (duplicateCount == 0)
        {
            StatusText.Text = "No duplicate rows found for the selected columns";
            return;
        }

        var result = MessageBox.Show(
            $"Delete {duplicateCount:N0} duplicate row{(duplicateCount == 1 ? string.Empty : "s")} using {chosenColumns.Count:N0} chosen column{(chosenColumns.Count == 1 ? string.Empty : "s")}? The first row in each duplicate group will be kept.",
            "Delete Duplicate Rows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        ExecuteUndoableMutation(
            actionDescription: duplicateCount == 1 ? "Deleted 1 duplicate row" : $"Deleted {duplicateCount:N0} duplicate rows",
            mutation: () => DataTableEditHelper.DeleteDuplicateRows(_originalData, chosenColumns, RowNumberColumnName),
            successMessageFactory: deletedCount => deletedCount == 1 ? "Deleted 1 duplicate row" : $"Deleted {deletedCount:N0} duplicate rows",
            structuralChange: true,
            clearSelection: true);
    }

    private void CommitPendingGridEdits()
    {
        DataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DataGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private bool CanEditCurrentWorkingSet()
        => _originalData != null && !_isPreviewMode;

    private List<DataRowView> GetSelectedRowViews()
    {
        var selectedRows = (_savedSelectedItems ?? DataGrid.SelectedItems.Cast<object>().ToList())
            .OfType<DataRowView>()
            .Distinct()
            .ToList();

        if (selectedRows.Count > 0)
            return selectedRows;

        return (_savedSelectedCells ?? DataGrid.SelectedCells.ToList())
            .Select(cell => cell.Item)
            .OfType<DataRowView>()
            .Distinct()
            .ToList();
    }

    private DataRowView? GetPrimarySelectedRow()
    {
        var selectedRows = GetSelectedRowViews();
        if (selectedRows.Count > 0)
            return selectedRows[0];

        if (DataGrid.CurrentCell.Item is DataRowView rowView)
            return rowView;

        return null;
    }

    private void ExecuteUndoableMutation(string actionDescription, Func<int> mutation, Func<int, string> successMessageFactory, bool structuralChange, bool clearSelection)
    {
        if (_originalData == null)
        {
            StatusText.Text = "No data loaded";
            return;
        }

        PushUndoSnapshot(actionDescription);

        try
        {
            var affectedCount = mutation();
            if (affectedCount <= 0)
            {
                DiscardLatestUndoSnapshot();
                StatusText.Text = "No changes were made";
                return;
            }

            FinalizeDataMutation(actionDescription, affectedCount, successMessageFactory(affectedCount), structuralChange, clearSelection);
        }
        catch (Exception ex)
        {
            DiscardLatestUndoSnapshot();
            MessageBox.Show($"Error applying edit: {ex.Message}", "Edit Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Edit failed";
        }
    }

    private void FinalizeDataMutation(string actionDescription, int affectedCount, string successMessage, bool structuralChange, bool clearSelection)
    {
        if (_originalData == null)
            return;

        if (clearSelection)
        {
            DataGrid.UnselectAllCells();
            DataGrid.UnselectAll();
        }

        DataGrid.Items.Refresh();

        if (structuralChange)
        {
            _totalRowCount = _originalData.Rows.Count;
            UpdateLoadMoreBanner(_originalData.Rows.Count);
        }

        _hasUnsavedChanges = true;
        _pendingEditCount += Math.Max(1, affectedCount);
        UpdateWindowTitle();
        UpdatePendingChangesTray();
        UpdateRowCount();
        EnableSaveMenuItems();
        UpdateUndoUi();
        MarkQualityReviewStale();

        StatusText.Text = successMessage;
        UndoActionText.Text = actionDescription;
    }

    private void PushUndoSnapshot(string actionDescription)
    {
        if (_originalData == null)
            return;

        _undoHistory.Add(new EditUndoSnapshot(
            actionDescription,
            _originalData.Copy(),
            _hasUnsavedChanges,
            _pendingEditCount,
            _totalRowCount,
            _qualityViewModel?.IsProfileStale == true));

        if (_undoHistory.Count > MaxUndoHistory)
            _undoHistory.RemoveAt(0);

        UpdateUndoUi();
    }

    private void DiscardLatestUndoSnapshot()
    {
        if (_undoHistory.Count == 0)
            return;

        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        UpdateUndoUi();
    }

    private void UndoLastEdit()
    {
        if (_undoHistory.Count == 0)
        {
            StatusText.Text = "Nothing to undo";
            return;
        }

        var snapshot = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);

        RestoreDataTableSnapshot(snapshot);
        StatusText.Text = $"Undid: {snapshot.ActionDescription}";
        UpdateUndoUi();
    }

    private void RestoreDataTableSnapshot(EditUndoSnapshot snapshot)
    {
        _originalData = snapshot.Snapshot.Copy();
        _dataView = _originalData.DefaultView;
        SetupDataGrid(_originalData, BuildColumnInfos(_originalData));
        _isPreviewMode = false;
        DataGrid.IsReadOnly = false;
        _hasUnsavedChanges = snapshot.WasUnsaved;
        _pendingEditCount = snapshot.PendingEditCount;
        _totalRowCount = snapshot.TotalRowCount;
        UpdateWindowTitle();
        UpdatePendingChangesTray();
        UpdateLoadMoreBanner(_originalData.Rows.Count);
        EnableSaveMenuItems();
        UpdateNotebookUiState();

        if (_qualityViewModel != null)
        {
            _qualityViewModel.RestoreProfileStaleState(snapshot.WasQualityReviewStale);
            UpdateQualityStaleBadge();
        }
    }

    private void UpdateUndoUi()
    {
        var hasUndo = _undoHistory.Count > 0 && CanEditCurrentWorkingSet();
        UndoMenuItem.IsEnabled = hasUndo;
        UndoContextMenuItem.IsEnabled = hasUndo;
        UndoActionBadge.Visibility = hasUndo ? Visibility.Visible : Visibility.Collapsed;
        UndoActionText.Text = hasUndo ? _undoHistory[^1].ActionDescription : string.Empty;
    }

    private void ResetUndoHistory()
    {
        _undoHistory.Clear();
        UpdateUndoUi();
    }

    private void UpdateEditMenuState()
    {
        var canEdit = CanEditCurrentWorkingSet();
        var selectedRowCount = GetSelectedRowViews().Count;
        var visibleRowCount = _dataView?.Count ?? 0;
        var currentCellAvailable = TryGetCurrentCellContext(out _);
        var editKeepSelectedRowsMenuItem = FindName("EditKeepSelectedRowsMenuItem") as MenuItem;
        var editDeleteUnselectedRowsMenuItem = FindName("EditDeleteUnselectedRowsMenuItem") as MenuItem;
        var editDuplicateRowsMenuItem = FindName("EditDuplicateRowsMenuItem") as MenuItem;
        var editInsertBlankAboveMenuItem = FindName("EditInsertBlankAboveMenuItem") as MenuItem;
        var editInsertBlankBelowMenuItem = FindName("EditInsertBlankBelowMenuItem") as MenuItem;
        var editDeleteDuplicateRowsMenuItem = FindName("EditDeleteDuplicateRowsMenuItem") as MenuItem;
        EditDeleteRowsMenuItem.Header = selectedRowCount == 1 ? "_Delete Row" : "_Delete Row(s)";
        EditDeleteRowsMenuItem.IsEnabled = canEdit && selectedRowCount > 0;
        if (editKeepSelectedRowsMenuItem != null)
            editKeepSelectedRowsMenuItem.IsEnabled = canEdit && selectedRowCount > 0 && visibleRowCount > selectedRowCount;
        if (editDeleteUnselectedRowsMenuItem != null)
            editDeleteUnselectedRowsMenuItem.IsEnabled = canEdit && selectedRowCount > 0 && visibleRowCount > selectedRowCount;
        if (editDuplicateRowsMenuItem != null)
        {
            editDuplicateRowsMenuItem.Header = selectedRowCount == 1 ? "Duplicate Row" : selectedRowCount > 1 ? $"Duplicate {selectedRowCount:N0} Rows" : "Duplicate Row(s)";
            editDuplicateRowsMenuItem.IsEnabled = canEdit && selectedRowCount > 0;
        }
        if (editInsertBlankAboveMenuItem != null)
            editInsertBlankAboveMenuItem.IsEnabled = canEdit && (selectedRowCount > 0 || currentCellAvailable);
        if (editInsertBlankBelowMenuItem != null)
            editInsertBlankBelowMenuItem.IsEnabled = canEdit && (selectedRowCount > 0 || currentCellAvailable);
        if (editDeleteDuplicateRowsMenuItem != null)
            editDeleteDuplicateRowsMenuItem.IsEnabled = canEdit && GetVisibleDataColumnNames().Count > 0;
        UpdateUndoUi();
    }

    private void UpdateContextMenuState()
    {
        var canEdit = CanEditCurrentWorkingSet();
        var selectedRows = GetSelectedRowViews();
        var selectedRowCount = selectedRows.Count;
        var selectedCells = GetSelectedEditableCellTargets();
        var currentCellAvailable = TryGetCurrentCellContext(out var currentCellContext);
        var visibleRowCount = _dataView?.Count ?? 0;

        DeleteRowsContextMenuItem.Header = selectedRowCount == 1 ? "Delete Row" : selectedRowCount > 1 ? $"Delete {selectedRowCount:N0} Rows" : "Delete Row(s)";
        DeleteRowsContextMenuItem.IsEnabled = canEdit && selectedRowCount > 0;
        KeepSelectedRowsContextMenuItem.IsEnabled = canEdit && selectedRowCount > 0 && visibleRowCount > selectedRowCount;
        DeleteUnselectedRowsContextMenuItem.IsEnabled = canEdit && selectedRowCount > 0 && visibleRowCount > selectedRowCount;
        DuplicateRowsContextMenuItem.Header = selectedRowCount == 1 ? "Duplicate Row" : selectedRowCount > 1 ? $"Duplicate {selectedRowCount:N0} Rows" : "Duplicate Row(s)";
        DuplicateRowsContextMenuItem.IsEnabled = canEdit && selectedRowCount > 0;
        InsertBlankAboveContextMenuItem.IsEnabled = canEdit && (selectedRowCount > 0 || currentCellAvailable);
        InsertBlankBelowContextMenuItem.IsEnabled = canEdit && (selectedRowCount > 0 || currentCellAvailable);
        ExportSelectedRowsContextMenuItem.Header = selectedRowCount == 1 ? "Export Selected Row..." : selectedRowCount > 1 ? $"Export {selectedRowCount:N0} Selected Rows..." : "Export Selected Rows...";
        ExportSelectedRowsContextMenuItem.IsEnabled = canEdit && selectedRowCount > 0;
        DeleteDuplicateRowsContextMenuItem.IsEnabled = canEdit && GetVisibleDataColumnNames().Count > 0;

        CopyRowsAsCsvContextMenuItem.Header = selectedRowCount == 1 ? "Copy Row as CSV" : selectedRowCount > 1 ? "Copy Rows as CSV" : "Copy Row(s) as CSV";
        CopyRowsAsCsvContextMenuItem.IsEnabled = selectedRowCount > 0;
        CopyRowsAsJsonContextMenuItem.Header = selectedRowCount == 1 ? "Copy Row as JSON" : selectedRowCount > 1 ? "Copy Rows as JSON" : "Copy Row(s) as JSON";
        CopyRowsAsJsonContextMenuItem.IsEnabled = selectedRowCount > 0;

        FilterToValueContextMenuItem.IsEnabled = currentCellAvailable;
        ExcludeValueContextMenuItem.IsEnabled = currentCellAvailable;
        ShowBlankValuesContextMenuItem.IsEnabled = currentCellAvailable;

        if (currentCellAvailable)
        {
            var valueLabel = currentCellContext.IsBlank ? "blank" : $"\"{currentCellContext.DisplayValue}\"";
            FilterToValueContextMenuItem.Header = $"Filter to {valueLabel}";
            ExcludeValueContextMenuItem.Header = $"Exclude {valueLabel}";
            ShowBlankValuesContextMenuItem.Header = $"Show Blank Values in {currentCellContext.ColumnName}";
        }
        else
        {
            FilterToValueContextMenuItem.Header = "Filter to This Value";
            ExcludeValueContextMenuItem.Header = "Exclude This Value";
            ShowBlankValuesContextMenuItem.Header = "Show Blank Values";
        }

        SetSelectedCellsToNullContextMenuItem.IsEnabled = canEdit && selectedCells.Count > 0;
        FillDownContextMenuItem.IsEnabled = canEdit && selectedCells.Count > 1;
        TrimWhitespaceContextMenuItem.IsEnabled = canEdit && selectedCells.Count > 0;
        ReplaceSelectionContextMenuItem.IsEnabled = canEdit && selectedCells.Count > 0;

        UpdateUndoUi();
    }

    private void UpdateQualityStaleBadge()
    {
        QualityStaleBadge.Visibility = _qualityViewModel?.IsProfileStale == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkQualityReviewStale()
    {
        _qualityViewModel?.MarkProfileStale();
        UpdateQualityStaleBadge();
    }

    private static List<ColumnInfo> BuildColumnInfos(DataTable dataTable)
        => dataTable.Columns.Cast<DataColumn>()
            .Where(c => c.ColumnName is not (RowNumberColumnName or SourceFileColumnName))
            .Select(c => new ColumnInfo
            {
                Name = c.ColumnName,
                Type = c.DataType.Name,
                Nullable = c.AllowDBNull
            })
            .ToList();

    private List<string> GetVisibleDataColumnNames()
        => DataGrid.Columns
            .Where(col => col.Visibility == Visibility.Visible)
            .OrderBy(col => col.DisplayIndex)
            .Select(GetBoundColumnName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name is not (RowNumberColumnName or SourceFileColumnName))
            .Cast<string>()
            .ToList();

    private List<string> GetSelectedColumnNamesFromCells()
        => GetSelectedEditableCellTargets()
            .Select(target => target.ColumnName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private List<DataTableCellTarget> GetSelectedEditableCellTargets()
    {
        var selectedCells = _savedSelectedCells ?? DataGrid.SelectedCells.ToList();
        var seen = new HashSet<(DataRow Row, string ColumnName)>(DataRowColumnNameComparer);
        var targets = new List<DataTableCellTarget>();

        foreach (var cell in selectedCells)
        {
            if (cell.Item is not DataRowView rowView)
                continue;

            var columnName = GetBoundColumnName(cell.Column);
            if (string.IsNullOrWhiteSpace(columnName) || columnName is RowNumberColumnName or SourceFileColumnName)
                continue;

            if (!seen.Add((rowView.Row, columnName)))
                continue;

            targets.Add(new DataTableCellTarget(rowView.Row, columnName));
        }

        return targets;
    }

    private sealed class DataRowColumnNameReferenceComparer : IEqualityComparer<(DataRow Row, string ColumnName)>
    {
        public bool Equals((DataRow Row, string ColumnName) x, (DataRow Row, string ColumnName) y)
            => ReferenceEquals(x.Row, y.Row)
               && string.Equals(x.ColumnName, y.ColumnName, StringComparison.Ordinal);

        public int GetHashCode((DataRow Row, string ColumnName) obj)
            => HashCode.Combine(
                RuntimeHelpers.GetHashCode(obj.Row),
                StringComparer.Ordinal.GetHashCode(obj.ColumnName));
    }

    private List<DataTableCellTarget> GetSelectedEditableCellTargetsSorted()
    {
        var rowOrder = BuildViewRowOrderMap();
        return GetSelectedEditableCellTargets()
            .OrderBy(target => target.ColumnName, StringComparer.Ordinal)
            .ThenBy(target => rowOrder.TryGetValue(target.Row, out var index) ? index : int.MaxValue)
            .ToList();
    }

    private Dictionary<DataRow, int> BuildViewRowOrderMap()
    {
        var result = new Dictionary<DataRow, int>();
        if (_dataView == null)
            return result;

        for (int i = 0; i < _dataView.Count; i++)
            result[_dataView[i].Row] = i;

        return result;
    }

    private bool TryGetCurrentCellContext(out CurrentCellContext context)
    {
        context = default;

        var currentCell = DataGrid.CurrentCell;
        if (_savedSelectedCells != null && _savedSelectedCells.Count > 0)
            currentCell = _savedSelectedCells[0];

        if (currentCell.Item is not DataRowView rowView)
            return false;

        var columnName = GetBoundColumnName(currentCell.Column);
        if (string.IsNullOrWhiteSpace(columnName) || columnName is RowNumberColumnName or SourceFileColumnName)
            return false;

        var rawValue = rowView[columnName];
        var isBlank = rawValue == DBNull.Value || string.IsNullOrWhiteSpace(rawValue?.ToString());
        var displayValue = isBlank ? BlankDisplayValue : rawValue?.ToString() ?? string.Empty;
        context = new CurrentCellContext(rowView.Row, columnName, rawValue, displayValue, isBlank);
        return true;
    }

    private static string? GetBoundColumnName(DataGridColumn? column)
    {
        if (column is not DataGridBoundColumn boundColumn)
            return column?.SortMemberPath;

        var binding = (boundColumn as DataGridTextColumn)?.Binding as System.Windows.Data.Binding;
        return binding?.Path?.Path.Trim('[', ']') ?? column.SortMemberPath;
    }

    private HashSet<string> GetLoadedDistinctFilterValues(string columnName)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (_originalData == null || !_originalData.Columns.Contains(columnName))
            return values;

        foreach (DataRow row in _originalData.Rows)
        {
            var rawValue = row[columnName];
            var displayValue = rawValue == DBNull.Value || string.IsNullOrWhiteSpace(rawValue?.ToString())
                ? BlankDisplayValue
                : rawValue.ToString() ?? string.Empty;
            values.Add(displayValue);
        }

        return values;
    }

    private void ApplyCurrentCellFilter(CurrentCellFilterAction action)
    {
        if (!TryGetCurrentCellContext(out var context))
        {
            StatusText.Text = "Select a data cell first";
            return;
        }

        if (!_columnFilters.TryGetValue(context.ColumnName, out var filterState))
        {
            StatusText.Text = $"Filtering is unavailable for {context.ColumnName}";
            return;
        }

        var allValues = GetLoadedDistinctFilterValues(context.ColumnName);
        if (allValues.Count == 0)
        {
            StatusText.Text = $"No values available in {context.ColumnName}";
            return;
        }

        HashSet<string> selectedValues;
        string statusMessage;
        switch (action)
        {
            case CurrentCellFilterAction.IncludeValue:
                selectedValues = [context.IsBlank ? BlankDisplayValue : context.DisplayValue];
                statusMessage = context.IsBlank
                    ? $"Filtered {context.ColumnName} to blank values"
                    : $"Filtered {context.ColumnName} to \"{context.DisplayValue}\"";
                break;
            case CurrentCellFilterAction.ExcludeValue:
                selectedValues = allValues
                    .Where(value => value != (context.IsBlank ? BlankDisplayValue : context.DisplayValue))
                    .ToHashSet(StringComparer.Ordinal);
                statusMessage = context.IsBlank
                    ? $"Excluded blank values from {context.ColumnName}"
                    : $"Excluded \"{context.DisplayValue}\" from {context.ColumnName}";
                break;
            case CurrentCellFilterAction.OnlyBlanks:
                if (!allValues.Contains(BlankDisplayValue))
                {
                    StatusText.Text = $"No blank values found in {context.ColumnName}";
                    return;
                }

                selectedValues = [BlankDisplayValue];
                statusMessage = $"Showing blank values in {context.ColumnName}";
                break;
            default:
                return;
        }

        filterState.AllValues = [.. allValues.OrderBy(value => value == BlankDisplayValue ? string.Empty : value)];
        filterState.SelectedValues = selectedValues;
        filterState.IsActive = selectedValues.Count != filterState.AllValues.Count;
        UpdateFilterIndicator(context.ColumnName);
        ApplyAllFilters();
        StatusText.Text = statusMessage;
    }

    private void SetSelectedCellsToNull()
    {
        var targets = GetSelectedEditableCellTargets();
        if (targets.Count == 0)
        {
            StatusText.Text = "No editable cells selected";
            return;
        }

        ExecuteUndoableMutation(
            actionDescription: targets.Count == 1 ? "Set 1 cell to null" : $"Set {targets.Count:N0} cells to null",
            mutation: () => DataTableEditHelper.SetCellsToNull(targets),
            successMessageFactory: count => count == 1 ? "Set 1 cell to null" : $"Set {count:N0} cells to null",
            structuralChange: false,
            clearSelection: false);
    }

    private void FillDownSelectedCells()
    {
        var targets = GetSelectedEditableCellTargetsSorted();
        if (targets.Count < 2)
        {
            StatusText.Text = "Select at least two cells in the same column to fill down";
            return;
        }

        ExecuteUndoableMutation(
            actionDescription: "Filled down selected cells",
            mutation: () => DataTableEditHelper.FillDown(targets, BuildViewRowOrderMap()),
            successMessageFactory: count => count == 1 ? "Filled down 1 cell" : $"Filled down {count:N0} cells",
            structuralChange: false,
            clearSelection: false);
    }

    private void TrimWhitespaceInSelection()
    {
        var targets = GetSelectedEditableCellTargets();
        if (targets.Count == 0)
        {
            StatusText.Text = "No editable cells selected";
            return;
        }

        ExecuteUndoableMutation(
            actionDescription: "Trimmed whitespace in selection",
            mutation: () => DataTableEditHelper.TrimWhitespace(targets),
            successMessageFactory: count => count == 1 ? "Trimmed whitespace in 1 cell" : $"Trimmed whitespace in {count:N0} cells",
            structuralChange: false,
            clearSelection: false);
    }

    private void ReplaceInSelection()
    {
        var targets = GetSelectedEditableCellTargets();
        if (targets.Count == 0)
        {
            StatusText.Text = "No editable cells selected";
            return;
        }

        var options = GridActionDialogs.ShowReplaceSelectionDialog(this);
        if (options == null)
            return;

        ExecuteUndoableMutation(
            actionDescription: "Replaced text in selection",
            mutation: () => DataTableEditHelper.ReplaceInCells(targets, options.FindText, options.ReplaceText, options.MatchCase),
            successMessageFactory: count => count == 1 ? "Replaced text in 1 cell" : $"Replaced text in {count:N0} cells",
            structuralChange: false,
            clearSelection: false);
    }

    private void CopyRowsToClipboard(string delimiter, bool includeHeaders)
    {
        try
        {
            var selectedRows = GetSelectedRowViews();
            if (selectedRows.Count == 0)
            {
                StatusText.Text = "No rows selected to copy";
                return;
            }

            var output = new System.Text.StringBuilder();
            var columnNames = GetVisibleDataColumnNames();

            if (includeHeaders)
                output.AppendLine(string.Join(delimiter, columnNames));

            foreach (var rowView in selectedRows)
            {
                var values = columnNames
                    .Select(columnName => FormatDelimitedValue(rowView[columnName], delimiter))
                    .ToList();
                output.AppendLine(string.Join(delimiter, values));
            }

            Clipboard.SetText(output.ToString());
            StatusText.Text = $"Copied {selectedRows.Count:N0} row{(selectedRows.Count == 1 ? string.Empty : "s")} to clipboard";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error copying rows: {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Copy failed";
        }
    }

    private void CopyRowsAsJsonToClipboard()
    {
        try
        {
            var selectedRows = GetSelectedRowViews();
            if (selectedRows.Count == 0)
            {
                StatusText.Text = "No rows selected to copy";
                return;
            }

            var columnNames = GetVisibleDataColumnNames();
            var payload = selectedRows
                .Select(rowView => columnNames.ToDictionary(
                    columnName => columnName,
                    columnName =>
                    {
                        var value = rowView[columnName];
                        return value == DBNull.Value ? null : value;
                    }))
                .ToList();

            object payloadObject = selectedRows.Count == 1 ? payload[0] : payload;
            var json = JsonSerializer.Serialize(payloadObject, payloadObject.GetType(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            Clipboard.SetText(json);
            StatusText.Text = selectedRows.Count == 1 ? "Copied 1 row as JSON" : $"Copied {selectedRows.Count:N0} rows as JSON";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error copying rows as JSON: {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Copy failed";
        }
    }

    private static string FormatDelimitedValue(object? value, string delimiter)
    {
        if (value == null || value == DBNull.Value)
            return string.Empty;

        var text = value.ToString() ?? string.Empty;
        var shouldQuote = delimiter == "," && (text.Contains('"') || text.Contains(',') || text.Contains('\r') || text.Contains('\n'));
        return shouldQuote ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
    
    private void CopyRows(bool includeHeaders)
    {
        CopyRowsToClipboard("\t", includeHeaders);
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

    /// <summary>
    /// Escapes a column name for use in a DataView.RowFilter expression.
    /// DataView uses brackets to delimit column names; embedded ] must be escaped as \].
    /// </summary>
    private static string EscapeDataViewColumnName(string name) =>
        $"[{name.Replace("]", @"\]")}]";

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
        else if (mods == System.Windows.Input.ModifierKeys.Control && e.Key == System.Windows.Input.Key.Z)
        {
            if (CanEditCurrentWorkingSet())
                UndoLastEdit();
            else
                StatusText.Text = "Undo is only available for editable working sets";
            e.Handled = true;
        }
        else if (mods == System.Windows.Input.ModifierKeys.None && e.Key == System.Windows.Input.Key.Delete && DataGrid.SelectedItems.Count > 0)
        {
            if (CanEditCurrentWorkingSet())
            {
                CaptureCurrentSelection();
                DeleteSelectedRows();
                ClearCapturedSelection();
            }
            else
            {
                StatusText.Text = "Row deletion is only available for editable working sets";
            }
            e.Handled = true;
        }
    }

    private void OnDataGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
            return;

        var row = FindVisualParent<DataGridRow>(source);
        if (row == null)
            return;

        var rowHeader = FindVisualParent<DataGridRowHeader>(source);
        if (rowHeader != null)
        {
            if (!row.IsSelected)
            {
                grid.UnselectAllCells();
                grid.UnselectAll();
                row.IsSelected = true;
                row.Focus();
            }

            return;
        }

        var cell = FindVisualParent<DataGridCell>(source);
        if (cell == null || cell.IsSelected)
            return;

        var cellInfo = new DataGridCellInfo(row.Item, cell.Column);
        if (grid.SelectedCells.Contains(cellInfo) || row.IsSelected)
            return;

        grid.UnselectAllCells();
        grid.UnselectAll();
        grid.CurrentCell = cellInfo;
        cell.IsSelected = true;
        cell.Focus();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
                return parent;

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
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
        
        // Load file info and register the source with the Query Hub.
        var fileInfo = await _parquetService.GetFileInfoAsync(filePath, csvOptions, jsonOptions);
        _totalRowCount = fileInfo.RowCount;
        
        // Update schema panel
        UpdateSchemaPanel(filePath, fileInfo);
        
        // Add to recent files
        AddToRecentFiles(filePath);
        
        // Track current file and reset unsaved changes
        _currentFilePath = filePath;
        _currentFilePaths = new[] { filePath };
        _hasUnsavedChanges = false;
        _pendingEditCount = 0;
        ResetUndoHistory();
        UpdateWindowTitle();
        UpdatePendingChangesTray();
        EnableSaveMenuItems();

        UpdateFormatBadge(format);

        // Setup file watcher
        SetupFileWatcher(filePath);

        // Notify Quality panel of new file
        var notebookSource = await RegisterNotebookFileSourceAsync([filePath], fileInfo);
        await ActivateNotebookSourceAsync(notebookSource, fileInfo.SourceFileSummaries, parquetPartsSuffix, unknownExt);
        StatusText.Text = $"Loaded {Path.GetFileName(filePath)} into the notebook workspace";
        

    }
    private void UpdateWindowTitle()
    {
        const string appTitle = "HipHipParquet \u2014 Data Quality Viewer";
        var fileName = string.IsNullOrEmpty(_currentFilePath) ? appTitle : $"{System.IO.Path.GetFileName(_currentFilePath)} - {appTitle}";
        Title = _hasUnsavedChanges ? $"*{fileName}" : fileName;
    }
    
    private void EnableSaveMenuItems()
    {
        bool hasData = _originalData != null && !_isPreviewMode;
        bool hasSingleSourcePath = !string.IsNullOrEmpty(_currentFilePath) && hasData;

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
            var success = await SaveFileAsync(saveFileDialog.FileName, showConfirmation: true);
            if (success)
            {
                _currentFilePath = saveFileDialog.FileName;
                UpdateWindowTitle();
            }
        }
    }
    
    private async Task<bool> SaveFileAsync(string filePath, bool showConfirmation = false)
    {
        try
        {
            if (_originalData == null)
            {
                StatusText.Text = "Nothing to save";
                return false;
            }

            ShowLoading("Saving file...");

            // Suppress the file watcher during save to avoid spurious "modified externally" prompts
            if (_fileWatcher != null)
                _fileWatcher.EnableRaisingEvents = false;
            
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

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error saving file: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error saving file";
            return false;
        }
        finally
        {
            // Re-enable the file watcher after save completes (or fails)
            if (_fileWatcher != null)
                _fileWatcher.EnableRaisingEvents = true;

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
            MarkQualityReviewStale();
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
                    Text = $"   \u2022 {Path.GetFileName(sourceFile)}",
                    Margin = new Thickness(10, 0, 0, 1),
                    Foreground = Brushes.DimGray,
                    FontSize = 11
                });
            }

            if (sourceFileCount > 8)
            {
                SchemaPanel.Children.Add(new TextBlock
                {
                    Text = $"   \u2022 ... and {sourceFileCount - 8:N0} more",
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
                if (column.ColumnName is "__RowNumber" or "__SourceFile") continue;

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

        if (!string.IsNullOrWhiteSpace(_activeNotebookSourceAlias))
        {
            try
            {
                var (values, totalDistinct, truncated) = await GetNotebookSession().GetDistinctValuesAsync(
                    _activeNotebookSourceAlias,
                    columnName,
                    MaxDistinctValues);

                state.AllValues = values;
                state.IsTruncated = truncated;
                state.TotalDistinctCount = totalDistinct;

                if (state.SelectedValues.Count == 0)
                    state.SelectedValues = new HashSet<string>(values);

                state.IsLoaded = true;
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load filter values: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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
        var sortValue = $"{sortMemberPath} {(direction == ListSortDirection.Ascending ? "ASC" : "DESC")}";
        _savedSort = sortValue;

        if (_isPreviewMode)
        {
            foreach (var col in DataGrid.Columns)
            {
                if (col.SortMemberPath == sortMemberPath)
                    col.SortDirection = direction;
                else
                    col.SortDirection = null;
            }

            var previewDisplayName = sortMemberPath == "__RowNumber" ? "#" : sortMemberPath;
            StatusText.Text = $"Sorted preview by {previewDisplayName} ({(direction == ListSortDirection.Ascending ? "ascending" : "descending")})";
            _ = TryGetActiveNotebookSource(out var source)
                ? LoadNotebookPreviewAsync(source, resetPaging: true)
                : Task.CompletedTask;
            return;
        }

        if (_dataView == null) return;

        _dataView.Sort = sortValue;

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
        if (_suppressFilterApply)
        {
            UpdateFilterBadge();
            UpdateRowCount();
            return;
        }

        if (_isPreviewMode)
        {
            UpdateFilterBadge();
            _previewRowOffset = 0;

            if (TryGetActiveNotebookSource(out var activeSource))
                _ = LoadNotebookPreviewAsync(activeSource, resetPaging: true);

            return;
        }

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
                var escapedCol = EscapeDataViewColumnName(columnName);
                conditions.Add($"Convert({escapedCol}, 'System.String') IN ({inList})");
            }

            if (includeBlank)
            {
                var escapedCol = EscapeDataViewColumnName(columnName);
                conditions.Add($"Convert({escapedCol}, 'System.String') = ''");
                conditions.Add($"{escapedCol} IS NULL");
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
                if (col.ColumnName is not ("__RowNumber" or "__SourceFile"))
                    globalConditions.Add($"Convert({EscapeDataViewColumnName(col.ColumnName)}, 'System.String') LIKE '*{escapedGlobalText}*'");
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

        if (_isPreviewMode)
        {
            var pageCount = _originalData.Rows.Count;
            var firstRow = pageCount > 0 ? _previewRowOffset + 1 : 0;
            var lastRow = _previewRowOffset + pageCount;
            RowCountText.Text = pageCount == 0
                ? $"0 of {_previewFilteredRowCount:N0} rows"
                : $"{firstRow:N0}-{lastRow:N0} of {_previewFilteredRowCount:N0} rows";
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
            .Where(c => c.ColumnName is not ("__RowNumber" or "__SourceFile"))
            .Select(c => c.ColumnName)
            .ToList() ?? new();

        return new GridQueryState
        {
            SourceFilePath = _currentFilePath ?? string.Empty,
            CsvOptions = _activeCsvOptions,
            JsonOptions = _activeJsonOptions,
            ColumnFilters = columnFilters,
            GlobalSearch = (GlobalSearchBox?.Text ?? string.Empty).Trim(),
            Sort = _isPreviewMode ? _savedSort : (_dataView?.Sort ?? _savedSort ?? string.Empty),
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
        if (!string.IsNullOrWhiteSpace(_activeNotebookSourceAlias))
        {
            try
            {
                if (!TryGetActiveNotebookSource(out var source))
                    return _totalRowCount;

                var queryState = CreateQueryStateForSource(source, _currentRowLimit, 0);
                return await GetNotebookSession().GetFilteredRowCountAsync(source.Alias, queryState, cancellationToken);
            }
            catch
            {
                return _previewFilteredRowCount > 0 ? _previewFilteredRowCount : (_dataView?.Count ?? 0);
            }
        }

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
            var t when t.Contains("bool") => "\u2705",
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
                IsQualityPaneVisible = QualityPanelShell.Visibility == Visibility.Visible,
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
            QualityPanelShell.Visibility = Visibility.Visible;
            QualityReviewPanel.Visibility = Visibility.Visible;
            QualitySplitter.Visibility = Visibility.Visible;
            QualitySplitterColumn.Width = new GridLength(5);
            QualityPaneColumn.MinWidth = 300;
            QualityPaneColumn.Width = new GridLength(Math.Max(300, state.QualityPaneWidth));
            ClampQualityPaneWidth();
        }
        else
        {
            QualityPanelShell.Visibility = Visibility.Collapsed;
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
            _isPreviewMode = false;
            DataGrid.IsReadOnly = false;
            ResetUndoHistory();
            UpdateLoadMoreBanner(dataTable.Rows.Count);
            EnableSaveMenuItems();
            UpdateNotebookUiState();

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
        var hasDuplicateFileNames = hasMultipleSources && sourceFiles!
            .Select(Path.GetFileName)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Count() > 1);

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
                    sourceFileName = hasDuplicateFileNames ? rawPath : Path.GetFileName(rawPath);
            }

            dataTable.Rows[i][SourceFileColumnName] = sourceFileName;
        }

        if (hasMultipleSources && dataTable.Columns.Contains(DuckDbFilenameColumnName))
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
            await LoadParquetFilesAsSingleTableAsync(_currentFilePaths, _currentRowLimit);
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
            _isPreviewMode = false;
            DataGrid.IsReadOnly = false;
            ResetUndoHistory();
            UpdateLoadMoreBanner(dataTable.Rows.Count);
            EnableSaveMenuItems();
            UpdateNotebookUiState();

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

    private void OnOpenMarkdownHelperClick(object sender, RoutedEventArgs e)
    {
        ToggleMarkdownHelperEmbedded(true);
    }

    private async Task OpenMarkdownHelperForFileAsync(string? filePath)
    {
        if (_markdownEditorWindow is { IsLoaded: true })
        {
            if (!string.IsNullOrWhiteSpace(filePath))
                await _markdownEditorWindow.OpenFileAsync(filePath);

            _markdownEditorWindow.Activate();
            _markdownEditorWindow.Focus();
            return;
        }

        if (_markdownHelperEmbedded)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
                await EmbeddedMarkdownHelper.OpenFileAsync(filePath);
            ToggleMarkdownHelperEmbedded(true);
            StatusText.Text = "Opened Markdown Helper in workspace.";
            return;
        }

        var markdownService = App.Current.Services.GetService<MarkdownService>() ?? new MarkdownService();
        _markdownEditorWindow = new MarkdownEditorWindow(markdownService, _workspaceService)
        {
            Owner = this
        };
        _markdownEditorWindow.Closed += (_, _) => _markdownEditorWindow = null;
        _markdownEditorWindow.DockBackRequested += OnMarkdownHelperDockBackRequested;
        _markdownEditorWindow.Show();

        if (!string.IsNullOrWhiteSpace(filePath))
            await _markdownEditorWindow.OpenFileAsync(filePath);

        StatusText.Text = "Opened Markdown Helper window.";
    }

    private void OnToggleMarkdownHelperClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
            ToggleMarkdownHelperEmbedded(mi.IsChecked);
    }

    private void ToggleMarkdownHelperEmbedded(bool visible)
    {
        _markdownHelperEmbedded = visible;
        MarkdownHelperHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MarkdownHelperSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (ToggleMarkdownHelperMenuItem != null) ToggleMarkdownHelperMenuItem.IsChecked = visible;
    }

    private async void OnMarkdownHelperPopOutRequested(object sender, EventArgs e)
    {
        var state = EmbeddedMarkdownHelper.CreateEditorStateSnapshot();
        ToggleMarkdownHelperEmbedded(false);

        if (_markdownEditorWindow is { IsLoaded: true })
        {
            await _markdownEditorWindow.LoadDraftStateAsync(state);
            _markdownEditorWindow.Activate();
            _markdownEditorWindow.Focus();
            return;
        }

        var markdownService = App.Current.Services.GetService<MarkdownService>() ?? new MarkdownService();
        _markdownEditorWindow = new MarkdownEditorWindow(markdownService, _workspaceService)
        {
            Owner = this
        };
        _markdownEditorWindow.Closed += (_, _) => _markdownEditorWindow = null;
        _markdownEditorWindow.DockBackRequested += OnMarkdownHelperDockBackRequested;
        _markdownEditorWindow.Show();
        await _markdownEditorWindow.LoadDraftStateAsync(state);
        StatusText.Text = "Opened Markdown Helper window.";
    }

    private async void OnMarkdownHelperDockBackRequested(object? sender, EventArgs e)
    {
        if (sender is not MarkdownEditorWindow markdownWindow)
            return;

        var state = markdownWindow.CreateEditorStateSnapshot();
        ToggleMarkdownHelperEmbedded(true);
        await EmbeddedMarkdownHelper.LoadDraftStateAsync(state);
        markdownWindow.CloseWithoutPrompt();
        StatusText.Text = "Returned Markdown Helper to workspace.";
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
            var downloadResult = await Services.UpdateService.DownloadInstallerWithDiagnosticsAsync(
                update.InstallerUrl!, progress, cts.Token);
            var installerPath = downloadResult.InstallerPath;

            HideLoading();

            if (!downloadResult.Succeeded || installerPath == null || !File.Exists(installerPath))
            {
                MessageBox.Show(
                    "Download failed. Opening the releases page so you can install manually.\n\n" +
                    (string.IsNullOrWhiteSpace(downloadResult.Message) ? "No additional diagnostics were available." : downloadResult.Message),
                    "Download Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                OpenUrl(update.ReleasePageUrl);
                return;
            }

            // Verify installer integrity: checksum first, then Authenticode signature
            var verified = false;
            string? verificationMessage = null;
            if (!string.IsNullOrEmpty(update.ChecksumsUrl))
            {
                ShowLoading("Verifying checksum…");
                var checksumResult = await Services.UpdateService.VerifyChecksumWithDiagnosticsAsync(installerPath, update.ChecksumsUrl, cts.Token);
                verified = checksumResult.Succeeded;
                if (!verified)
                    verificationMessage = checksumResult.Message;
                HideLoading();
            }

            if (!verified)
            {
                ShowLoading("Verifying signature\u2026");
                try
                {
                    var signatureResult = await Task.Run(() =>
                        Services.UpdateService.VerifyAuthenticodeSignatureWithDiagnostics(installerPath));
                    verified = signatureResult.Succeeded;
                    if (!verified)
                    {
                        verificationMessage = string.IsNullOrWhiteSpace(verificationMessage)
                            ? signatureResult.Message
                            : $"{verificationMessage}\n{signatureResult.Message}";
                    }
                }
                finally
                {
                    HideLoading();
                }
            }

            if (!verified)
            {
                try { File.Delete(installerPath); } catch { /* best-effort cleanup */ }
                MessageBox.Show(
                    "The downloaded installer could not be verified (no valid checksum or Authenticode signature).\n\n" +
                    (string.IsNullOrWhiteSpace(verificationMessage) ? string.Empty : $"Details: {verificationMessage}\n\n") +
                    "Opening the releases page so you can download and verify manually.",
                    "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private sealed record EditUndoSnapshot(
        string ActionDescription,
        DataTable Snapshot,
        bool WasUnsaved,
        int PendingEditCount,
        long TotalRowCount,
        bool WasQualityReviewStale);

    private readonly record struct CurrentCellContext(
        DataRow Row,
        string ColumnName,
        object? RawValue,
        string DisplayValue,
        bool IsBlank);

    private enum CurrentCellFilterAction
    {
        IncludeValue,
        ExcludeValue,
        OnlyBlanks
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
