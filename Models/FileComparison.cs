namespace HipHipParquet.Models;

/// <summary>
/// Comparison result between two Parquet files, tracking schema changes, row deltas, and KPI drift.
/// </summary>
public class FileComparison
{
    public string BaselineFilePath { get; set; } = string.Empty;
    public string ComparisonFilePath { get; set; } = string.Empty;
    public FileProfile BaselineProfile { get; set; } = new();
    public FileProfile ComparisonProfile { get; set; } = new();

    public long RowCountDelta => ComparisonProfile.RowCount - BaselineProfile.RowCount;
    public double RowCountDeltaPercent => BaselineProfile.RowCount == 0 ? 0 :
        Math.Round((double)RowCountDelta / BaselineProfile.RowCount * 100, 2);

    public List<SchemaChange> SchemaChanges { get; set; } = [];
    public List<ColumnDrift> ColumnDrifts { get; set; } = [];
    public double DriftScore { get; set; }
}

/// <summary>
/// A schema difference between baseline and comparison files.
/// </summary>
public class SchemaChange
{
    public string ColumnName { get; set; } = string.Empty;
    public SchemaChangeType ChangeType { get; set; }
    public string? OldType { get; set; }
    public string? NewType { get; set; }
}

public enum SchemaChangeType
{
    Added,
    Removed,
    TypeChanged
}

/// <summary>
/// Per-column drift metrics comparing baseline vs comparison.
/// </summary>
public class ColumnDrift
{
    public string ColumnName { get; set; } = string.Empty;
    public double? NullPercentageDelta { get; set; }
    public double? MeanDelta { get; set; }
    public double? DistinctCountDelta { get; set; }
    public double? QualityScoreDelta { get; set; }
    public double DriftMagnitude { get; set; }

    /// <summary>True if any individual metric exceeds threshold.</summary>
    public bool IsSignificant => DriftMagnitude > 20;
}
