using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace HipHipParquet.ViewModels;

/// <summary>
/// ViewModel for the Quality Review Panel. Uses CommunityToolkit.Mvvm for MVVM pattern.
/// </summary>
public partial class QualityReviewViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly ILogger<QualityReviewViewModel> _logger;
    private readonly QualityScoreService _qualityScoreService;
    private readonly NarrativeService _narrativeService;
    private readonly ReportService _reportService;

    // ── Observable Properties ────────────────────────────────────────────

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private bool _hasProfile;

    [ObservableProperty]
    private bool _hasFile;

    [ObservableProperty]
    private bool _hasComparison;

    [ObservableProperty]
    private string _statusMessage = "Open a file and click Analyze to begin.";

    [ObservableProperty]
    private double _analysisProgress;

    [ObservableProperty]
    private FileProfile? _fileProfile;

    [ObservableProperty]
    private FileComparison? _comparison;

    [ObservableProperty]
    private string _currentFilePath = string.Empty;

    // Overall score
    [ObservableProperty]
    private double _overallScore;

    [ObservableProperty]
    private string _overallGrade = "";

    [ObservableProperty]
    private string _overallScoreColor = "#9E9E9E";

    // File summary
    [ObservableProperty]
    private string _formatDisplay = "";

    [ObservableProperty]
    private string _analyzedAtDisplay = "";

    // Score components
    [ObservableProperty]
    private double _completenessScore;

    [ObservableProperty]
    private double _uniquenessScore;

    [ObservableProperty]
    private double _validityScore;

    [ObservableProperty]
    private double _distributionScore;

    // Column profiles displayed in the panel
    [ObservableProperty]
    private ObservableCollection<ColumnProfile> _displayedColumns = [];

    // All column profiles (before filtering)
    private List<ColumnProfile> _allColumns = [];

    // Narrative findings
    [ObservableProperty]
    private ObservableCollection<NarrativeItem> _findings = [];

    // All findings (before severity filtering)
    private List<NarrativeItem> _allFindings = [];

    // Findings severity filtering
    [ObservableProperty]
    private string _selectedFindingFilter = "All";

    [ObservableProperty]
    private ObservableCollection<FindingSeverityGroup> _groupedFindings = [];

    [ObservableProperty]
    private int _findingsCriticalCount;

    [ObservableProperty]
    private int _findingsWarningCount;

    [ObservableProperty]
    private int _findingsInfoCount;

    // Column sort
    [ObservableProperty]
    private string _columnSortBy = "Name ↑";

    // Dimension group-by
    [ObservableProperty]
    private ObservableCollection<DimensionItem> _availableDimensions = [];

    [ObservableProperty]
    private ObservableCollection<GroupedResult> _groupedResults = [];

    [ObservableProperty]
    private bool _hasGroupedResults;

    [ObservableProperty]
    private bool _isGroupByExpanded;

    [ObservableProperty]
    private bool _isColumnProfilesExpanded = true;

    [ObservableProperty]
    private string _groupByStatusMessage = "";

    // Filter/query builder
    [ObservableProperty]
    private string _filterColumn = "All";

    [ObservableProperty]
    private string _filterOperator = ">";

    [ObservableProperty]
    private string _filterValue = "";

    [ObservableProperty]
    private ObservableCollection<string> _filterableMetrics = 
    [
        "All", "Null %", "Quality Score", "Distinct Count", "Outlier %"
    ];

    // Comparison
    [ObservableProperty]
    private string _comparisonFileName = "";

    // Schema diff items (populated on compare)
    [ObservableProperty]
    private ObservableCollection<SchemaDiffItem> _schemaDiffItems = [];

    [ObservableProperty]
    private bool _hasSchemaDiff;

    // Active CSV import options (set when a CSV/TSV file is loaded with custom settings)
    public CsvImportOptions? ActiveCsvOptions { get; private set; }

    // Active JSON import options (set when a JSON file is loaded with custom settings)
    public JsonImportOptions? ActiveJsonOptions { get; private set; }

    // Taskbar progress state for long-running operations
    [ObservableProperty]
    private double taskbarProgressValue;

    [ObservableProperty]
    private bool taskbarProgressVisible;

    // Convenience accessor: typed logger for transient ParquetService instances created inside this ViewModel.
    // No real logger factory is registered in this app, so we use the null logger for the service tier.
    private static Microsoft.Extensions.Logging.ILogger<ParquetService> ParquetServiceLogger =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;

    // ── Cancellation ────────────────────────────────────────────────────
    private CancellationTokenSource? _analysisCts;

    [ObservableProperty]
    private bool _canCancel;

    // ── Constructor ─────────────────────────────────────────────────────

    public QualityReviewViewModel(
        ILogger<QualityReviewViewModel> logger,
        QualityScoreService qualityScoreService,
        NarrativeService narrativeService,
        ReportService reportService)
    {
        _logger = logger;
        _qualityScoreService = qualityScoreService;
        _narrativeService = narrativeService;
        _reportService = reportService;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = null;
        _disposed = true;
    }

    // ── Commands ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrEmpty(CurrentFilePath))
            return;

        // Cancel any running analysis and start fresh.
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();
        var cts = _analysisCts;

        IsAnalyzing = true;
        CanCancel = true;
        AnalysisProgress = 0;
        TaskbarProgressValue = 0;
        TaskbarProgressVisible = true;
        StatusMessage = "Analyzing file...";

        try
        {
            // Report per-column progress: map 0..N columns to the 10-60 % range.
            var columnProgress = new Progress<(int Current, int Total)>(p =>
            {
                var progress = p.Total > 0
                    ? 10.0 + (double)p.Current / p.Total * 50.0
                    : 10.0;
                AnalysisProgress = progress;
                TaskbarProgressValue = progress / 100.0;
                StatusMessage = $"Profiling column {p.Current} of {p.Total}...";
            });

            // Run all DuckDB work on the thread-pool so the WPF dispatcher stays free.
            using var parquetService = new ParquetService(ParquetServiceLogger);
            AnalysisProgress = 10;
            var profile = await Task.Run(
                () => parquetService.GetFileProfileAsync(
                    CurrentFilePath, ActiveCsvOptions, ActiveJsonOptions,
                    cts.Token, columnProgress),
                cts.Token);
            AnalysisProgress = 60;

            // Score the profile
            _qualityScoreService.ScoreFileProfile(profile);
            AnalysisProgress = 80;

            // Generate narrative
            var findings = _narrativeService.GenerateFindings(profile);
            AnalysisProgress = 90;

            // Update UI state
            FileProfile = profile;
            _allColumns = [.. profile.Columns];
            DisplayedColumns = new ObservableCollection<ColumnProfile>(profile.Columns);

            OverallScore = profile.OverallScore.Total;
            OverallGrade = profile.OverallScore.Grade;
            OverallScoreColor = profile.OverallScore.Color;
            FormatDisplay = Services.FileFormatDetector.GetFormatDisplayName(profile.SourceFormat);
            AnalyzedAtDisplay = profile.AnalyzedAt.ToString("MMM d, yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
            CompletenessScore = profile.OverallScore.Completeness;
            UniquenessScore = profile.OverallScore.Uniqueness;
            ValidityScore = profile.OverallScore.Validity;
            DistributionScore = profile.OverallScore.Distribution;

            _allFindings = findings;
            Findings = new ObservableCollection<NarrativeItem>(findings);
            RebuildGroupedFindings();

            // Populate available dimensions (string/categorical columns)
            AvailableDimensions.Clear();
            foreach (var col in profile.Columns.Where(c => c.Category == ColumnCategory.String && c.DistinctCount <= 100 && c.DistinctCount > 1))
            {
                AvailableDimensions.Add(new DimensionItem { Name = col.Name, IsSelected = false });
            }

            HasProfile = true;
            ExportHtmlReportCommand.NotifyCanExecuteChanged();
            AnalysisProgress = 100;
            TaskbarProgressValue = 1.0;
            StatusMessage = $"Analysis complete — {profile.ColumnCount} columns, {profile.RowCount:N0} rows, score: {profile.OverallScore.Total:F0}/100";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
            AnalysisProgress = 0;
            TaskbarProgressValue = 0;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
            _logger.LogError(ex, "Quality analysis failed for {FilePath}", CurrentFilePath);
            TaskbarProgressValue = 0;
        }
        finally
        {
            IsAnalyzing = false;
            CanCancel = false;
            TaskbarProgressVisible = false;
        }
    }

    private bool CanAnalyze() => !string.IsNullOrEmpty(CurrentFilePath) && !IsAnalyzing;

    [RelayCommand]
    private void CancelAnalysis()
    {
        _analysisCts?.Cancel();
    }

    [RelayCommand]
    private async Task CompareWithFileAsync()
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = Services.FileFormatDetector.GetOpenFileDialogFilter(),
            Title = "Select Comparison Data File"
        };

        if (openFileDialog.ShowDialog() != true || FileProfile == null)
            return;

        await CompareWithFilePathAsync(openFileDialog.FileName);
    }

    public async Task CompareWithFilePathAsync(string comparisonFilePath)
    {
        if (FileProfile == null || string.IsNullOrWhiteSpace(comparisonFilePath))
            return;

        IsAnalyzing = true;
        StatusMessage = "Comparing files...";

        try
        {
            var compPath = comparisonFilePath;
            ComparisonFileName = System.IO.Path.GetFileName(compPath);

            using var parquetService = new ParquetService(ParquetServiceLogger);
            var compProfile = await parquetService.GetFileProfileAsync(compPath);
            _qualityScoreService.ScoreFileProfile(compProfile);

            var comparison = BuildComparison(FileProfile, compProfile);
            comparison.DriftScore = _qualityScoreService.ComputeDriftScore(FileProfile, compProfile);

            var compFindings = _narrativeService.GenerateComparisonFindings(comparison);

            Comparison = comparison;
            HasComparison = true;

            // Build schema diff view
            BuildSchemaDiff(FileProfile, compProfile, comparison);

            // Append comparison findings to existing findings
            foreach (var finding in compFindings)
            {
                Findings.Add(finding);
                _allFindings.Add(finding);
            }
            RebuildGroupedFindings();

            StatusMessage = $"Comparison complete with {ComparisonFileName} — drift score: {comparison.DriftScore:F1}/100";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void SetFindingFilter(string filter)
    {
        SelectedFindingFilter = filter;
        RebuildGroupedFindings();
    }

    partial void OnColumnSortByChanged(string value)
    {
        ApplySortToColumns();
    }

    [RelayCommand]
    private void SetSort(string sortBy)
    {
        // Extract column name (remove direction arrow)
        var newColumnName = sortBy.Replace(" ↑", "").Replace(" ↓", "").Trim();
        var currentColumnName = ColumnSortBy.Replace(" ↑", "").Replace(" ↓", "").Trim();

        // If clicking the same column, toggle direction
        if (newColumnName == currentColumnName)
        {
            // Toggle between ↑ and ↓
            var newSort = ColumnSortBy.EndsWith("↑") 
                ? ColumnSortBy.Replace(" ↑", " ↓") 
                : ColumnSortBy.Replace(" ↓", " ↑");
            ColumnSortBy = newSort;
        }
        else
        {
            // New column - default to ascending
            ColumnSortBy = $"{newColumnName} ↑";
        }
    }

    [RelayCommand]
    private void ToggleGroupByExpanded()
    {
        IsGroupByExpanded = !IsGroupByExpanded;
    }

    [RelayCommand]
    private void ToggleColumnProfilesExpanded()
    {
        IsColumnProfilesExpanded = !IsColumnProfilesExpanded;
    }

    [RelayCommand]
    private async Task ApplyGroupByAsync()
    {
        var selectedDimensions = AvailableDimensions.Where(d => d.IsSelected).Select(d => d.Name).ToList();
        if (selectedDimensions.Count == 0)
        {
            GroupByStatusMessage = "⚠ Select at least one dimension above.";
            return;
        }

        if (string.IsNullOrEmpty(CurrentFilePath))
            return;

        // Cancel any running analysis and start fresh.
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();
        var cts = _analysisCts;

        IsAnalyzing = true;
        CanCancel = true;
        GroupByStatusMessage = "";
        StatusMessage = "Computing grouped statistics...";
        AnalysisProgress = 0;

        try
        {
            var groupProgress = new Progress<(int Current, int Total)>(p =>
            {
                AnalysisProgress = p.Total > 0 ? (double)p.Current / p.Total * 100.0 : 0;
                StatusMessage = $"Processing group {p.Current} of {p.Total}...";
            });

            // Run all DuckDB work on the thread-pool so the WPF dispatcher stays free.
            using var parquetService = new ParquetService(ParquetServiceLogger);
            var grouped = await Task.Run(
                () => parquetService.GetGroupedStatisticsAsync(
                    CurrentFilePath, selectedDimensions, ActiveCsvOptions, ActiveJsonOptions,
                    cts.Token, groupProgress),
                cts.Token);

            GroupedResults.Clear();
            foreach (var kvp in grouped.OrderByDescending(g => g.Value.RowCount))
            {
                _qualityScoreService.ScoreFileProfile(kvp.Value);
                GroupedResults.Add(new GroupedResult
                {
                    GroupKey = kvp.Key,
                    RowCount = kvp.Value.RowCount,
                    QualityScore = kvp.Value.OverallScore.Total,
                    ScoreColor = kvp.Value.OverallScore.Color,
                    Profile = kvp.Value
                });
            }

            HasGroupedResults = GroupedResults.Count > 0;
            GroupByStatusMessage = HasGroupedResults
                ? $"{GroupedResults.Count} groups found"
                : "No groups found for selected dimensions.";
            StatusMessage = $"Grouped by {string.Join(", ", selectedDimensions)} — {GroupedResults.Count} groups";
            AnalysisProgress = 100;
        }
        catch (OperationCanceledException)
        {
            GroupByStatusMessage = "Cancelled.";
            StatusMessage = "Group-by cancelled.";
            AnalysisProgress = 0;
        }
        catch (Exception ex)
        {
            GroupByStatusMessage = $"Error: {ex.Message}";
            StatusMessage = $"Group-by failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            CanCancel = false;
        }
    }

    [RelayCommand]
    private void ClearGroupBy()
    {
        foreach (var dim in AvailableDimensions)
            dim.IsSelected = false;
        GroupedResults.Clear();
        HasGroupedResults = false;
        GroupByStatusMessage = "";
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        if (string.IsNullOrEmpty(FilterValue) || FilterColumn == "All")
        {
            DisplayedColumns = new ObservableCollection<ColumnProfile>(_allColumns);
            return;
        }

        if (!double.TryParse(FilterValue, out var threshold))
            return;

        var filtered = _allColumns.Where(col =>
        {
            var metricValue = FilterColumn switch
            {
                "Null %" => col.NullPercentage,
                "Quality Score" => col.Score.Total,
                "Distinct Count" => col.DistinctCount,
                "Outlier %" => col.OutlierPercentage,
                _ => 0.0
            };

            return FilterOperator switch
            {
                ">" => metricValue > threshold,
                "<" => metricValue < threshold,
                "=" => Math.Abs(metricValue - threshold) < 0.01,
                ">=" => metricValue >= threshold,
                "<=" => metricValue <= threshold,
                _ => true
            };
        }).ToList();

        DisplayedColumns = new ObservableCollection<ColumnProfile>(filtered);
        StatusMessage = $"Showing {filtered.Count} of {_allColumns.Count} columns matching filter";
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterColumn = "All";
        FilterValue = "";
        DisplayedColumns = new ObservableCollection<ColumnProfile>(_allColumns);
        if (HasProfile)
            StatusMessage = $"Showing all {_allColumns.Count} columns";
    }

    private bool CanExportHtmlReport() => HasProfile;

    [RelayCommand(CanExecute = nameof(CanExportHtmlReport))]
    private async Task ExportHtmlReportAsync()
    {
        if (FileProfile == null)
            return;

        var saveDialog = new SaveFileDialog
        {
            Filter = "HTML files (*.html)|*.html",
            Title = "Export Quality Report",
            FileName = $"Quality_Report_{FileProfile.FileName}_{DateTime.Now:yyyyMMdd_HHmmss}.html"
        };

        if (saveDialog.ShowDialog() != true)
            return;

        try
        {
            StatusMessage = "Generating HTML report...";
            var html = _reportService.GenerateHtmlReport(FileProfile, Findings.ToList(), Comparison);
            await System.IO.File.WriteAllTextAsync(saveDialog.FileName, html);

            var latestReportPointerPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HipHipParquet",
                "latest-report.txt");
            var pointerDir = System.IO.Path.GetDirectoryName(latestReportPointerPath);
            if (!string.IsNullOrEmpty(pointerDir))
                Directory.CreateDirectory(pointerDir);
            await System.IO.File.WriteAllTextAsync(latestReportPointerPath, saveDialog.FileName);

            StatusMessage = $"Report exported to {System.IO.Path.GetFileName(saveDialog.FileName)}";

            // Open in default browser
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = saveDialog.FileName,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No default browser associated with .html — open containing folder instead
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        $"/select,\"{saveDialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    // Log the failure to launch explorer, but still provide a user-friendly message
                    _logger.LogError(ex, "Failed to open explorer.exe for exported quality report at path {Path}", saveDialog.FileName);
                    StatusMessage = $"Report saved to {saveDialog.FileName} — open it manually to view.";
                }
            }
            catch (System.Exception)
            {
                // Fallback for other exceptions from Process.Start (ArgumentException, PathTooLongException, etc.)
                StatusMessage = $"Report saved to {saveDialog.FileName} — open it manually to view.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    // ── Public Methods (called from MainWindow code-behind) ────────────

    /// <summary>
    /// Sets the current file path when a new file is loaded in MainWindow.
    /// </summary>
    public void SetFilePath(string filePath, CsvImportOptions? csvOptions = null, JsonImportOptions? jsonOptions = null)
    {
        CurrentFilePath = filePath;
        ActiveCsvOptions = csvOptions;
        ActiveJsonOptions = jsonOptions;
        HasFile = true;
        HasProfile = false;
        HasComparison = false;
        Comparison = null;
        FormatDisplay = "";
        AnalyzedAtDisplay = "";
        Findings.Clear();
        _allFindings.Clear();
        GroupedFindings.Clear();
        FindingsCriticalCount = 0;
        FindingsWarningCount = 0;
        FindingsInfoCount = 0;
        SelectedFindingFilter = "All";
        DisplayedColumns.Clear();
        _allColumns.Clear();
        ColumnSortBy = "Name ↑";
        GroupedResults.Clear();
        HasGroupedResults = false;
        IsGroupByExpanded = false;
        IsColumnProfilesExpanded = true;
        GroupByStatusMessage = "";
        StatusMessage = "File loaded. Profiling can start automatically.";
        AnalyzeCommand.NotifyCanExecuteChanged();
        ExportHtmlReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Starts analysis in the background when a file is loaded and no analysis is currently running.
    /// </summary>
    public void StartAutoAnalyze()
    {
        if (!CanAnalyze())
            return;

        _ = AnalyzeAsync();
    }

    /// <summary>
    /// Clears any reference to an opened file, e.g., when loading multiple files as a single logical table.
    /// </summary>
    public void ClearFile()
    {
        CurrentFilePath = string.Empty;
        HasFile = false;
        HasProfile = false;
        HasComparison = false;
        Comparison = null;
        FormatDisplay = "";
        AnalyzedAtDisplay = "";
        Findings.Clear();
        _allFindings.Clear();
        GroupedFindings.Clear();
        FindingsCriticalCount = 0;
        FindingsWarningCount = 0;
        FindingsInfoCount = 0;
        SelectedFindingFilter = "All";
        DisplayedColumns.Clear();
        _allColumns.Clear();
        ColumnSortBy = "Name ↑";
        GroupedResults.Clear();
        HasGroupedResults = false;
        IsGroupByExpanded = false;
        IsColumnProfilesExpanded = true;
        GroupByStatusMessage = "";
        StatusMessage = "Open a file and click Analyze to begin.";
        AnalyzeCommand.NotifyCanExecuteChanged();
        ExportHtmlReportCommand.NotifyCanExecuteChanged();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void RebuildGroupedFindings()
    {
        FindingsCriticalCount = _allFindings.Count(f => f.Severity == NarrativeSeverity.Critical);
        FindingsWarningCount = _allFindings.Count(f => f.Severity == NarrativeSeverity.Warning);
        FindingsInfoCount = _allFindings.Count(f => f.Severity == NarrativeSeverity.Info);

        var filtered = SelectedFindingFilter switch
        {
            "Critical" => _allFindings.Where(f => f.Severity == NarrativeSeverity.Critical).ToList(),
            "Warning" => _allFindings.Where(f => f.Severity == NarrativeSeverity.Warning).ToList(),
            "Info" => _allFindings.Where(f => f.Severity == NarrativeSeverity.Info).ToList(),
            _ => _allFindings.ToList()
        };

        var groups = new ObservableCollection<FindingSeverityGroup>();
        var severities = new[] { NarrativeSeverity.Critical, NarrativeSeverity.Warning, NarrativeSeverity.Info };

        foreach (var severity in severities)
        {
            var items = filtered.Where(f => f.Severity == severity).ToList();
            if (items.Count == 0) continue;

            groups.Add(new FindingSeverityGroup
            {
                Severity = severity,
                Label = severity switch
                {
                    NarrativeSeverity.Critical => "Needs Review",
                    NarrativeSeverity.Warning => "Fair",
                    NarrativeSeverity.Info => "Good",
                    _ => "Other"
                },
                Color = severity switch
                {
                    NarrativeSeverity.Critical => "#F44336",
                    NarrativeSeverity.Warning => "#FF9800",
                    NarrativeSeverity.Info => "#4CAF50",
                    _ => "#9E9E9E"
                },
                Count = items.Count,
                IsExpanded = severity == NarrativeSeverity.Critical || items.Count <= 10,
                Items = new ObservableCollection<NarrativeItem>(items)
            });
        }

        GroupedFindings = groups;
    }

    private void ApplySortToColumns()
    {
        // Sort the currently displayed (potentially filtered) set so active filters are preserved.
        // Fall back to _allColumns only when no profile has been loaded yet (DisplayedColumns
        // not yet initialized). When HasProfile is true but DisplayedColumns is empty it means
        // the user's filter returned zero results — preserve that empty set rather than resetting.
        var source = HasProfile
            ? (IEnumerable<ColumnProfile>)DisplayedColumns
            : _allColumns;

        if (!source.Any()) return;

        var sorted = ColumnSortBy switch
        {
            "Name ↑" => source.OrderBy(c => c.Name).ToList(),
            "Name ↓" => source.OrderByDescending(c => c.Name).ToList(),
            "Type ↑" => source.OrderBy(c => c.DuckDbType).ToList(),
            "Type ↓" => source.OrderByDescending(c => c.DuckDbType).ToList(),
            "Score ↑" => source.OrderBy(c => c.Score.Total).ToList(),
            "Score ↓" => source.OrderByDescending(c => c.Score.Total).ToList(),
            "Completeness ↑" => source.OrderBy(c => c.Score.Completeness).ToList(),
            "Completeness ↓" => source.OrderByDescending(c => c.Score.Completeness).ToList(),
            "Uniqueness ↑" => source.OrderBy(c => c.Score.Uniqueness).ToList(),
            "Uniqueness ↓" => source.OrderByDescending(c => c.Score.Uniqueness).ToList(),
            "Validity ↑" => source.OrderBy(c => c.Score.Validity).ToList(),
            "Validity ↓" => source.OrderByDescending(c => c.Score.Validity).ToList(),
            "Distribution ↑" => source.OrderBy(c => c.Score.Distribution).ToList(),
            "Distribution ↓" => source.OrderByDescending(c => c.Score.Distribution).ToList(),
            "Nulls ↑" => source.OrderBy(c => c.NullPercentage).ToList(),
            "Nulls ↓" => source.OrderByDescending(c => c.NullPercentage).ToList(),
            "Distinct ↑" => source.OrderBy(c => c.DistinctCount).ToList(),
            "Distinct ↓" => source.OrderByDescending(c => c.DistinctCount).ToList(),
            _ => source.OrderBy(c => c.Name).ToList()
        };
        DisplayedColumns = new ObservableCollection<ColumnProfile>(sorted);
    }

    private static FileComparison BuildComparison(FileProfile baseline, FileProfile comparison)
    {
        var result = new FileComparison
        {
            BaselineFilePath = baseline.FilePath,
            ComparisonFilePath = comparison.FilePath,
            BaselineProfile = baseline,
            ComparisonProfile = comparison
        };

        var baselineCols = baseline.Columns.ToDictionary(c => c.Name);
        var compCols = comparison.Columns.ToDictionary(c => c.Name);

        // Schema changes
        foreach (var col in comparison.Columns)
        {
            if (!baselineCols.ContainsKey(col.Name))
            {
                result.SchemaChanges.Add(new SchemaChange
                {
                    ColumnName = col.Name,
                    ChangeType = SchemaChangeType.Added,
                    NewType = col.DuckDbType
                });
            }
            else if (baselineCols[col.Name].DuckDbType != col.DuckDbType)
            {
                result.SchemaChanges.Add(new SchemaChange
                {
                    ColumnName = col.Name,
                    ChangeType = SchemaChangeType.TypeChanged,
                    OldType = baselineCols[col.Name].DuckDbType,
                    NewType = col.DuckDbType
                });
            }
        }

        foreach (var col in baseline.Columns)
        {
            if (!compCols.ContainsKey(col.Name))
            {
                result.SchemaChanges.Add(new SchemaChange
                {
                    ColumnName = col.Name,
                    ChangeType = SchemaChangeType.Removed,
                    OldType = col.DuckDbType
                });
            }
        }

        // Column drift
        foreach (var compCol in comparison.Columns)
        {
            if (!baselineCols.TryGetValue(compCol.Name, out var baseCol))
                continue;

            var drift = new ColumnDrift
            {
                ColumnName = compCol.Name,
                NullPercentageDelta = compCol.NullPercentage - baseCol.NullPercentage,
                MeanDelta = (compCol.Mean.HasValue && baseCol.Mean.HasValue) ? compCol.Mean.Value - baseCol.Mean.Value : null,
                DistinctCountDelta = compCol.DistinctCount - baseCol.DistinctCount,
                QualityScoreDelta = compCol.Score.Total - baseCol.Score.Total
            };

            // Drift magnitude: average of absolute deltas (normalized)
            var magnitudes = new List<double>();
            if (drift.NullPercentageDelta.HasValue)
                magnitudes.Add(Math.Abs(drift.NullPercentageDelta.Value));
            if (drift.QualityScoreDelta.HasValue)
                magnitudes.Add(Math.Abs(drift.QualityScoreDelta.Value));
            drift.DriftMagnitude = magnitudes.Count > 0 ? magnitudes.Average() : 0;

            result.ColumnDrifts.Add(drift);
        }

        return result;
    }

    /// <summary>
    /// Builds a side-by-side schema diff for the comparison view.
    /// </summary>
    private void BuildSchemaDiff(FileProfile baseline, FileProfile compProfile, FileComparison comparison)
    {
        SchemaDiffItems.Clear();

        var baselineCols = baseline.Columns.ToDictionary(c => c.Name);
        var compCols = compProfile.Columns.ToDictionary(c => c.Name);
        var allNames = baseline.Columns.Select(c => c.Name)
            .Union(compProfile.Columns.Select(c => c.Name))
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        foreach (var name in allNames)
        {
            var hasBase = baselineCols.TryGetValue(name, out var baseCol);
            var hasComp = compCols.TryGetValue(name, out var compCol);

            var item = new SchemaDiffItem
            {
                ColumnName = name,
                BaselineType = hasBase ? baseCol!.DuckDbType : "—",
                ComparisonType = hasComp ? compCol!.DuckDbType : "—"
            };

            if (!hasBase)
            {
                item.Status = "Added";
                item.StatusColor = "#2E7D32";
                item.StatusIcon = "+";
            }
            else if (!hasComp)
            {
                item.Status = "Removed";
                item.StatusColor = "#C62828";
                item.StatusIcon = "−";
            }
            else if (baseCol!.DuckDbType != compCol!.DuckDbType)
            {
                item.Status = "Changed";
                item.StatusColor = "#E65100";
                item.StatusIcon = "~";
            }
            else
            {
                item.Status = "Match";
                item.StatusColor = "#9E9E9E";
                item.StatusIcon = "=";
            }

            SchemaDiffItems.Add(item);
        }

        HasSchemaDiff = SchemaDiffItems.Count > 0;
    }
}

/// <summary>
/// Represents a selectable dimension column for group-by analysis.
/// </summary>
public partial class DimensionItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A single group result from group-by dimension analysis.
/// </summary>
public class GroupedResult
{
    public string GroupKey { get; set; } = string.Empty;
    public long RowCount { get; set; }
    public double QualityScore { get; set; }
    public string ScoreColor { get; set; } = "#9E9E9E";
    public FileProfile Profile { get; set; } = new();
}

/// <summary>
/// Represents one row in the side-by-side schema diff view.
/// </summary>
public class SchemaDiffItem
{
    public string ColumnName { get; set; } = string.Empty;
    public string BaselineType { get; set; } = "—";
    public string ComparisonType { get; set; } = "—";
    public string Status { get; set; } = "Match";
    public string StatusColor { get; set; } = "#9E9E9E";
    public string StatusIcon { get; set; } = "=";
}

/// <summary>
/// A group of findings by severity level, with collapse/expand support.
/// </summary>
public partial class FindingSeverityGroup : ObservableObject
{
    public NarrativeSeverity Severity { get; set; }
    public string Label { get; set; } = "";
    public string Color { get; set; } = "";
    public int Count { get; set; }

    [ObservableProperty]
    private bool _isExpanded = true;

    public ObservableCollection<NarrativeItem> Items { get; set; } = [];
}
