namespace HipHipParquet.Models;

/// <summary>
/// Comprehensive statistical profile for a single column in a Parquet file.
/// </summary>
public class ColumnProfile
{
    public string Name { get; set; } = string.Empty;
    public string DuckDbType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public ColumnCategory Category { get; set; }

    // Counts
    public long TotalRows { get; set; }
    public long NullCount { get; set; }
    public long NonNullCount => TotalRows - NullCount;
    public double NullPercentage => TotalRows == 0 ? 0 : Math.Round((double)NullCount / TotalRows * 100, 2);
    public double Completeness => TotalRows == 0 ? 100 : Math.Round((double)NonNullCount / TotalRows * 100, 2);
    public long DistinctCount { get; set; }
    public double DistinctPercentage => NonNullCount == 0 ? 0 : Math.Round((double)DistinctCount / NonNullCount * 100, 2);
    public long DuplicateCount => NonNullCount - DistinctCount;

    // Numeric statistics (only for numeric columns)
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Mean { get; set; }
    public double? Median { get; set; }
    public double? StdDev { get; set; }
    public double? Sum { get; set; }
    public double? Q1 { get; set; }
    public double? Q3 { get; set; }
    public double? IQR => (Q1.HasValue && Q3.HasValue) ? Q3.Value - Q1.Value : null;
    public long OutlierCount { get; set; }
    public double OutlierPercentage => NonNullCount == 0 ? 0 : Math.Round((double)OutlierCount / NonNullCount * 100, 2);

    // String statistics (only for string columns)
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public double? AvgLength { get; set; }
    public int? EmptyStringCount { get; set; }

    // Date/timestamp statistics
    public string? MinDate { get; set; }
    public string? MaxDate { get; set; }

    // Boolean statistics
    public long? TrueCount { get; set; }
    public long? FalseCount { get; set; }

    // Distribution data
    public List<ValueFrequency> TopValues { get; set; } = [];
    public List<HistogramBucket> Histogram { get; set; } = [];

    // Quality score (computed by QualityScoreService)
    public QualityScore Score { get; set; } = new();

    // ── Display helpers for table view (type-aware formatting) ──────────

    /// <summary>Formatted Min value suitable for display across all column types.</summary>
    public string DisplayMin => Category switch
    {
        ColumnCategory.Numeric => Min.HasValue ? $"{Min.Value:G6}" : "—",
        ColumnCategory.String => MinLength.HasValue ? $"len {MinLength}" : "—",
        ColumnCategory.DateTime => MinDate ?? "—",
        ColumnCategory.Boolean => TrueCount.HasValue ? $"T:{TrueCount}" : "—",
        _ => "—"
    };

    /// <summary>Formatted Max value suitable for display across all column types.</summary>
    public string DisplayMax => Category switch
    {
        ColumnCategory.Numeric => Max.HasValue ? $"{Max.Value:G6}" : "—",
        ColumnCategory.String => MaxLength.HasValue ? $"len {MaxLength}" : "—",
        ColumnCategory.DateTime => MaxDate ?? "—",
        ColumnCategory.Boolean => FalseCount.HasValue ? $"F:{FalseCount}" : "—",
        _ => "—"
    };

    /// <summary>Formatted Mean value suitable for display across all column types.</summary>
    public string DisplayMean => Category switch
    {
        ColumnCategory.Numeric => Mean.HasValue ? $"{Mean.Value:G6}" : "—",
        ColumnCategory.String => AvgLength.HasValue ? $"avg {AvgLength.Value:F1}" : "—",
        _ => "—"
    };
}

/// <summary>
/// Categorizes a column by its data type family.
/// </summary>
public enum ColumnCategory
{
    Numeric,
    String,
    Boolean,
    DateTime,
    Other
}

/// <summary>
/// A value and how frequently it appears.
/// </summary>
public class ValueFrequency
{
    public string Value { get; set; } = string.Empty;
    public long Count { get; set; }
    public double Percentage { get; set; }
}

/// <summary>
/// A single bucket in a histogram distribution.
/// </summary>
public class HistogramBucket
{
    public double LowerBound { get; set; }
    public double UpperBound { get; set; }
    public long Count { get; set; }
    public string Label => $"{LowerBound:G4}–{UpperBound:G4}";
}
