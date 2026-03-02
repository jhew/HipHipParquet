using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;

namespace HipHipParquet.ViewModels;

/// <summary>
/// ViewModel for the Quality Review Panel. Uses CommunityToolkit.Mvvm for MVVM pattern.
/// </summary>
public partial class QualityReviewViewModel : ObservableObject
{
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

    // Convenience accessor: typed logger for transient ParquetService instances created inside this ViewModel.
    // No real logger factory is registered in this app, so we use the null logger for the service tier.
    private static Microsoft.Extensions.Logging.ILogger<ParquetService> ParquetServiceLogger =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetService>.Instance;

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

    // ── Commands ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrEmpty(CurrentFilePath))
            return;

        IsAnalyzing = true;
        AnalysisProgress = 0;
        StatusMessage = "Analyzing file...";

        try
        {
            using var parquetService = new ParquetService(ParquetServiceLogger);
            AnalysisProgress = 10;

            var profile = await parquetService.GetFileProfileAsync(CurrentFilePath, ActiveCsvOptions);
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
            CompletenessScore = profile.OverallScore.Completeness;
            UniquenessScore = profile.OverallScore.Uniqueness;
            ValidityScore = profile.OverallScore.Validity;
            DistributionScore = profile.OverallScore.Distribution;

            Findings = new ObservableCollection<NarrativeItem>(findings);

            // Populate available dimensions (string/categorical columns)
            AvailableDimensions.Clear();
            foreach (var col in profile.Columns.Where(c => c.Category == ColumnCategory.String && c.DistinctCount <= 100 && c.DistinctCount > 1))
            {
                AvailableDimensions.Add(new DimensionItem { Name = col.Name, IsSelected = false });
            }

            HasProfile = true;
            AnalysisProgress = 100;
            StatusMessage = $"Analysis complete — {profile.ColumnCount} columns, {profile.RowCount:N0} rows, score: {profile.OverallScore.Total:F1}/100";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
            _logger.LogError(ex, "Quality analysis failed for {FilePath}", CurrentFilePath);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private bool CanAnalyze() => !string.IsNullOrEmpty(CurrentFilePath) && !IsAnalyzing;

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

        IsAnalyzing = true;
        StatusMessage = "Comparing files...";

        try
        {
            var compPath = openFileDialog.FileName;
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
            }

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

        IsAnalyzing = true;
        GroupByStatusMessage = "";
        StatusMessage = "Computing grouped statistics...";

        try
        {
            using var parquetService = new ParquetService(ParquetServiceLogger);
            var grouped = await parquetService.GetGroupedStatisticsAsync(CurrentFilePath, selectedDimensions);

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
        }
        catch (Exception ex)
        {
            GroupByStatusMessage = $"Error: {ex.Message}";
            StatusMessage = $"Group-by failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
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

    [RelayCommand]
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

            StatusMessage = $"Report exported to {System.IO.Path.GetFileName(saveDialog.FileName)}";

            // Open in default browser
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = saveDialog.FileName,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
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
    public void SetFilePath(string filePath, CsvImportOptions? csvOptions = null)
    {
        CurrentFilePath = filePath;
        ActiveCsvOptions = csvOptions;
        HasFile = true;
        HasProfile = false;
        HasComparison = false;
        Comparison = null;
        Findings.Clear();
        DisplayedColumns.Clear();
        GroupedResults.Clear();
        HasGroupedResults = false;
        IsGroupByExpanded = false;
        IsColumnProfilesExpanded = true;
        GroupByStatusMessage = "";
        StatusMessage = "File loaded. Click Analyze to profile.";
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

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
