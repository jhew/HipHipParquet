using HipHipParquet.Models;

namespace HipHipParquet.Services;

/// <summary>
/// Generates rule-based, plain-English narrative findings from file and column profiles.
/// Each finding has a severity level (Info, Warning, Critical) and a descriptive message.
/// </summary>
public class NarrativeService
{
    // ── Thresholds ──────────────────────────────────────────────────────
    private const double CriticalNullPct = 50.0;
    private const double WarningNullPct = 10.0;
    private const double HighOutlierPct = 10.0;
    private const double LowOutlierPct = 5.0;
    private const double DominantValuePct = 95.0;
    private const double WarningDominantValuePct = 80.0;
    private const int LowDistinctThreshold = 2;
    private const double CriticalQualityScore = 40.0;
    private const double WarningQualityScore = 60.0;
    private const double CriticalUniquenessDimension = 7.0;  // out of 25 — near-constant across many columns
    private const double LowColumnUniqueness = 10.0;          // out of 25 — very few distinct values

    /// <summary>
    /// Generates narrative findings for a single file profile.
    /// </summary>
    public List<NarrativeItem> GenerateFindings(FileProfile profile)
    {
        var findings = new List<NarrativeItem>();

        // File-level findings
        GenerateFileLevelFindings(profile, findings);

        // Column-level findings
        foreach (var col in profile.Columns)
        {
            GenerateColumnFindings(col, profile.RowCount, findings);
        }

        // Sort by severity (Critical first), then by column name
        findings.Sort((a, b) =>
        {
            var severityCompare = b.Severity.CompareTo(a.Severity);
            return severityCompare != 0 ? severityCompare : string.Compare(a.ColumnName, b.ColumnName, StringComparison.Ordinal);
        });

        return findings;
    }

    /// <summary>
    /// Generates comparison findings between two file profiles.
    /// </summary>
    public List<NarrativeItem> GenerateComparisonFindings(FileComparison comparison)
    {
        var findings = new List<NarrativeItem>();

        // Row count change
        if (comparison.RowCountDelta != 0)
        {
            var direction = comparison.RowCountDelta > 0 ? "increased" : "decreased";
            var severity = Math.Abs(comparison.RowCountDeltaPercent) > 50
                ? NarrativeSeverity.Critical
                : Math.Abs(comparison.RowCountDeltaPercent) > 10 ? NarrativeSeverity.Warning : NarrativeSeverity.Info;

            findings.Add(new NarrativeItem
            {
                Severity = severity,
                Title = $"Row count {direction}",
                Description = $"Row count {direction} by {Math.Abs(comparison.RowCountDelta):N0} rows ({Math.Abs(comparison.RowCountDeltaPercent):F1}%): {comparison.BaselineProfile.RowCount:N0} → {comparison.ComparisonProfile.RowCount:N0}."
            });
        }

        // Schema changes
        foreach (var change in comparison.SchemaChanges)
        {
            var item = change.ChangeType switch
            {
                SchemaChangeType.Added => new NarrativeItem
                {
                    Severity = NarrativeSeverity.Warning,
                    Title = $"Column added: {change.ColumnName}",
                    Description = $"New column \"{change.ColumnName}\" ({change.NewType}) was added.",
                    ColumnName = change.ColumnName
                },
                SchemaChangeType.Removed => new NarrativeItem
                {
                    Severity = NarrativeSeverity.Critical,
                    Title = $"Column removed: {change.ColumnName}",
                    Description = $"Column \"{change.ColumnName}\" ({change.OldType}) was removed from the file.",
                    ColumnName = change.ColumnName
                },
                SchemaChangeType.TypeChanged => new NarrativeItem
                {
                    Severity = NarrativeSeverity.Critical,
                    Title = $"Type changed: {change.ColumnName}",
                    Description = $"Column \"{change.ColumnName}\" type changed from {change.OldType} to {change.NewType}.",
                    ColumnName = change.ColumnName
                },
                _ => null
            };

            if (item != null)
                findings.Add(item);
        }

        // Column drift
        foreach (var drift in comparison.ColumnDrifts.Where(d => d.IsSignificant))
        {
            var details = new List<string>();
            if (drift.NullPercentageDelta.HasValue && Math.Abs(drift.NullPercentageDelta.Value) > 5)
                details.Add($"null% changed by {drift.NullPercentageDelta.Value:+0.0;-0.0}pp");
            if (drift.MeanDelta.HasValue && Math.Abs(drift.MeanDelta.Value) > 0)
                details.Add($"mean changed by {drift.MeanDelta.Value:+0.00;-0.00}");
            if (drift.QualityScoreDelta.HasValue && Math.Abs(drift.QualityScoreDelta.Value) > 5)
                details.Add($"quality score changed by {drift.QualityScoreDelta.Value:+0.0;-0.0}");

            findings.Add(new NarrativeItem
            {
                Severity = drift.DriftMagnitude > 50 ? NarrativeSeverity.Critical : NarrativeSeverity.Warning,
                Title = $"Significant drift: {drift.ColumnName}",
                Description = $"Column \"{drift.ColumnName}\" shows significant data drift (magnitude: {drift.DriftMagnitude:F1}). {string.Join("; ", details)}.",
                ColumnName = drift.ColumnName
            });
        }

        // Overall quality score change
        var scoreDelta = comparison.ComparisonProfile.OverallScore.Total - comparison.BaselineProfile.OverallScore.Total;
        if (Math.Abs(scoreDelta) > 5)
        {
            var direction = scoreDelta > 0 ? "improved" : "degraded";
            findings.Add(new NarrativeItem
            {
                Severity = scoreDelta < -10 ? NarrativeSeverity.Critical : scoreDelta < 0 ? NarrativeSeverity.Warning : NarrativeSeverity.Info,
                Title = $"Quality score {direction}",
                Description = $"Overall quality score {direction} from {comparison.BaselineProfile.OverallScore.Total:F1} to {comparison.ComparisonProfile.OverallScore.Total:F1} ({scoreDelta:+0.0;-0.0} points)."
            });
        }

        findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return findings;
    }

    private void GenerateFileLevelFindings(FileProfile profile, List<NarrativeItem> findings)
    {
        // ── Always-on summary findings ──────────────────────────────────

        // File overview
        var typeGroups = profile.Columns.GroupBy(c => c.Category)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();
        findings.Add(new NarrativeItem
        {
            Severity = NarrativeSeverity.Info,
            Title = "File overview",
            Description = $"{profile.RowCount:N0} rows × {profile.ColumnCount} columns ({string.Join(", ", typeGroups)}). Overall quality: {profile.OverallScore.Total:F1}/100 ({profile.OverallScore.Grade})."
        });

        // Best and worst columns
        if (profile.Columns.Count > 1)
        {
            var best = profile.Columns.OrderByDescending(c => c.Score.Total).First();
            var worst = profile.Columns.OrderBy(c => c.Score.Total).First();
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Info,
                Title = $"Best column: {best.Name}",
                Description = $"Highest quality score {best.Score.Total:F1}/100 — C:{best.Score.Completeness:F0} U:{best.Score.Uniqueness:F0} V:{best.Score.Validity:F0} D:{best.Score.Distribution:F0}.",
                ColumnName = best.Name
            });
            if (worst.Name != best.Name)
            {
                findings.Add(new NarrativeItem
                {
                    Severity = worst.Score.Total < WarningQualityScore ? NarrativeSeverity.Warning : NarrativeSeverity.Info,
                    Title = $"Weakest column: {worst.Name}",
                    Description = $"Lowest quality score {worst.Score.Total:F1}/100 — C:{worst.Score.Completeness:F0} U:{worst.Score.Uniqueness:F0} V:{worst.Score.Validity:F0} D:{worst.Score.Distribution:F0}.",
                    ColumnName = worst.Name
                });
            }
        }

        // Null summary across all columns
        var totalNulls = profile.Columns.Sum(c => c.NullCount);
        var totalCells = profile.Columns.Sum(c => c.TotalRows);
        var overallNullPct = totalCells == 0 ? 0 : (double)totalNulls / totalCells * 100;
        var colsWithNulls = profile.Columns.Count(c => c.NullCount > 0);
        if (colsWithNulls > 0)
        {
            findings.Add(new NarrativeItem
            {
                Severity = overallNullPct > 20 ? NarrativeSeverity.Warning : NarrativeSeverity.Info,
                Title = $"{colsWithNulls} column(s) contain nulls",
                Description = $"{totalNulls:N0} total nulls across {colsWithNulls} columns ({overallNullPct:F1}% of all cells)."
            });
        }
        else
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Info,
                Title = "No nulls detected",
                Description = "All columns are fully populated — 0 null values across the entire file."
            });
        }

        // Classify columns in a single pass instead of multiple Where/ToList iterations
        var lowUniqueCols = new List<ColumnProfile>();
        var lowDistCols = new List<ColumnProfile>();
        foreach (var c in profile.Columns)
        {
            if (c.Score.Uniqueness < LowColumnUniqueness) lowUniqueCols.Add(c);
            if (c.Score.Distribution < 20) lowDistCols.Add(c);
        }

        if (lowUniqueCols.Count > 0)
        {
            var overallUniqueness = profile.OverallScore.Uniqueness;
            const int maxListed = 5;
            var listedCols = string.Join(", ", lowUniqueCols.Take(maxListed).Select(c => $"\"{c.Name}\" ({c.Score.Uniqueness:F1}/25)"));
            var moreSuffix = lowUniqueCols.Count > maxListed ? $" and {lowUniqueCols.Count - maxListed} more" : "";
            findings.Add(new NarrativeItem
            {
                Severity = overallUniqueness < CriticalUniquenessDimension ? NarrativeSeverity.Critical : NarrativeSeverity.Warning,
                Title = $"{lowUniqueCols.Count} column(s) with low uniqueness score",
                Description = $"Overall uniqueness score is {overallUniqueness:F1}/25. Columns with uniqueness <{LowColumnUniqueness:F0}/25: {listedCols}{moreSuffix}. Very few distinct values indicate near-constant or highly repetitive data."
            });
        }

        // Distribution dimension summary (lowDistCols already populated above)
        if (lowDistCols.Count > 0)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Info,
                Title = $"{lowDistCols.Count} column(s) with low distribution score",
                Description = $"Columns with distribution <20/25: {string.Join(", ", lowDistCols.Select(c => $"\"{c.Name}\" ({c.Score.Distribution:F1}/25)"))}. High outlier rates or skewed values may be present."
            });
        }

        // ── Threshold-based findings ────────────────────────────────────

        // Overall quality score
        if (profile.OverallScore.Total < CriticalQualityScore)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Critical,
                Title = "Critical data quality",
                Description = $"Overall data quality score is {profile.OverallScore.Total:F1}/100 ({profile.OverallScore.Grade}). Immediate review recommended."
            });
        }
        else if (profile.OverallScore.Total < WarningQualityScore)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Warning,
                Title = "Below-average data quality",
                Description = $"Overall data quality score is {profile.OverallScore.Total:F1}/100 ({profile.OverallScore.Grade}). Some columns may need attention."
            });
        }

        // Zero rows
        if (profile.RowCount == 0)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Critical,
                Title = "Empty file",
                Description = $"The {FileFormatDetector.GetFormatDisplayName(profile.SourceFormat)} file contains 0 rows."
            });
        }

        // Columns with high null rates
        var highNullCols = profile.Columns.Where(c => c.NullPercentage > CriticalNullPct).ToList();
        if (highNullCols.Count > 0)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Warning,
                Title = $"{highNullCols.Count} columns >50% null",
                Description = $"{highNullCols.Count} column(s) have more than 50% null values: {string.Join(", ", highNullCols.Select(c => $"\"{c.Name}\" ({c.NullPercentage:F1}%)"))}"
            });
        }
    }

    private void GenerateColumnFindings(ColumnProfile col, long totalRows, List<NarrativeItem> findings)
    {
        // Null rate findings
        if (col.NullPercentage >= CriticalNullPct)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Critical,
                Title = $"High null rate: {col.Name}",
                Description = $"Column \"{col.Name}\" has {col.NullPercentage:F1}% missing values ({col.NullCount:N0} of {col.TotalRows:N0} rows).",
                ColumnName = col.Name
            });
        }
        else if (col.NullPercentage >= WarningNullPct)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Warning,
                Title = $"Elevated null rate: {col.Name}",
                Description = $"Column \"{col.Name}\" has {col.NullPercentage:F1}% missing values ({col.NullCount:N0} of {col.TotalRows:N0} rows).",
                ColumnName = col.Name
            });
        }

        // Constant column (only 1 distinct value)
        bool isConstantCol = col.DistinctCount == 1 && col.NonNullCount > 10;
        // Low cardinality for non-boolean (≤2 distinct values across a large row count)
        bool isLowCardinalityCol = col.Category != ColumnCategory.Boolean
            && col.DistinctCount <= LowDistinctThreshold
            && col.NonNullCount > 100;

        if (isConstantCol)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Warning,
                Title = $"Constant column: {col.Name}",
                Description = $"Column \"{col.Name}\" contains only a single unique value across {col.NonNullCount:N0} non-null rows. Consider if this column is necessary.",
                ColumnName = col.Name
            });
        }

        // Low cardinality for non-boolean
        if (isLowCardinalityCol)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Info,
                Title = $"Low cardinality: {col.Name}",
                Description = $"Column \"{col.Name}\" has only {col.DistinctCount} unique values across {col.NonNullCount:N0} rows.",
                ColumnName = col.Name
            });
        }

        // Low uniqueness score not already covered by constant-column or low-cardinality checks
        // Note: severity intentionally capped at Warning per-column; Critical is surfaced in the file-level summary.
        if (!isConstantCol && !isLowCardinalityCol && col.Score.Uniqueness < LowColumnUniqueness && col.NonNullCount > 0)
        {
            findings.Add(new NarrativeItem
            {
                Severity = col.Score.Uniqueness < CriticalUniquenessDimension ? NarrativeSeverity.Warning : NarrativeSeverity.Info,
                Title = $"Low uniqueness: {col.Name}",
                Description = $"Column \"{col.Name}\" has a uniqueness score of {col.Score.Uniqueness:F1}/25 — only {col.DistinctCount:N0} distinct value(s) among {col.NonNullCount:N0} non-null rows ({col.DistinctPercentage:F1}% distinct).",
                ColumnName = col.Name
            });
        }

        // Dominant value
        if (col.TopValues.Count > 0 && col.TopValues[0].Percentage >= DominantValuePct)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Warning,
                Title = $"Dominant value: {col.Name}",
                Description = $"Column \"{col.Name}\": top value \"{Truncate(col.TopValues[0].Value, 50)}\" accounts for {col.TopValues[0].Percentage:F1}% of rows.",
                ColumnName = col.Name
            });
        }
        else if (col.TopValues.Count > 0 && col.TopValues[0].Percentage >= WarningDominantValuePct)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Info,
                Title = $"Frequent value: {col.Name}",
                Description = $"Column \"{col.Name}\": top value \"{Truncate(col.TopValues[0].Value, 50)}\" accounts for {col.TopValues[0].Percentage:F1}% of rows.",
                ColumnName = col.Name
            });
        }

        // Outliers for numeric columns
        if (col.Category == ColumnCategory.Numeric && col.OutlierCount > 0)
        {
            if (col.OutlierPercentage >= HighOutlierPct)
            {
                findings.Add(new NarrativeItem
                {
                    Severity = NarrativeSeverity.Warning,
                    Title = $"High outlier rate: {col.Name}",
                    Description = $"Column \"{col.Name}\" has {col.OutlierCount:N0} outliers ({col.OutlierPercentage:F1}%) beyond 1.5×IQR.",
                    ColumnName = col.Name
                });
            }
            else if (col.OutlierPercentage >= LowOutlierPct)
            {
                findings.Add(new NarrativeItem
                {
                    Severity = NarrativeSeverity.Info,
                    Title = $"Outliers detected: {col.Name}",
                    Description = $"Column \"{col.Name}\" has {col.OutlierCount:N0} outliers ({col.OutlierPercentage:F1}%) beyond 1.5×IQR.",
                    ColumnName = col.Name
                });
            }
        }

        // Empty strings for string columns
        if (col.Category == ColumnCategory.String && col.EmptyStringCount.HasValue && col.EmptyStringCount.Value > 0)
        {
            var emptyPct = col.NonNullCount == 0 ? 0 : (double)col.EmptyStringCount.Value / col.NonNullCount * 100;
            if (emptyPct > 10)
            {
                findings.Add(new NarrativeItem
                {
                    Severity = NarrativeSeverity.Warning,
                    Title = $"Empty strings: {col.Name}",
                    Description = $"Column \"{col.Name}\" has {col.EmptyStringCount.Value:N0} empty strings ({emptyPct:F1}% of non-null values). These may represent missing data not captured as NULL.",
                    ColumnName = col.Name
                });
            }
        }

        // Quality score per column
        if (col.Score.Total < CriticalQualityScore)
        {
            findings.Add(new NarrativeItem
            {
                Severity = NarrativeSeverity.Critical,
                Title = $"Low quality score: {col.Name}",
                Description = $"Column \"{col.Name}\" has a quality score of {col.Score.Total:F1}/100 — review completeness, distribution, and validity.",
                ColumnName = col.Name
            });
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
