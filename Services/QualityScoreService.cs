using HipHipParquet.Models;

namespace HipHipParquet.Services;

/// <summary>
/// Computes data quality scores (0–100) from column and file profile statistics.
/// Four dimensions each contribute 0–25 points: Completeness, Uniqueness, Validity, Distribution.
/// </summary>
public class QualityScoreService
{
    /// <summary>
    /// Scores all columns in the file profile and computes the overall file quality score.
    /// </summary>
    public void ScoreFileProfile(FileProfile profile)
    {
        foreach (var col in profile.Columns)
        {
            col.Score = ScoreColumn(col);
        }

        // Overall score = weighted average of all column scores (single-pass)
        if (profile.Columns.Count > 0)
        {
            double sumC = 0, sumU = 0, sumV = 0, sumD = 0;
            foreach (var col in profile.Columns)
            {
                sumC += col.Score.Completeness;
                sumU += col.Score.Uniqueness;
                sumV += col.Score.Validity;
                sumD += col.Score.Distribution;
            }
            var count = profile.Columns.Count;
            profile.OverallScore = new QualityScore
            {
                Completeness = Math.Round(sumC / count, 1),
                Uniqueness = Math.Round(sumU / count, 1),
                Validity = Math.Round(sumV / count, 1),
                Distribution = Math.Round(sumD / count, 1)
            };
        }
    }

    /// <summary>
    /// Computes the quality score for a single column.
    /// </summary>
    public QualityScore ScoreColumn(ColumnProfile col)
    {
        return new QualityScore
        {
            Completeness = ScoreCompleteness(col),
            Uniqueness = ScoreUniqueness(col),
            Validity = ScoreValidity(col),
            Distribution = ScoreDistribution(col)
        };
    }

    /// <summary>
    /// Completeness (0–25): Linear scale from null percentage.
    /// 0% nulls → 25, 100% nulls → 0.
    /// </summary>
    private static double ScoreCompleteness(ColumnProfile col)
    {
        var completenessRatio = col.TotalRows == 0 ? 1.0 : (double)col.NonNullCount / col.TotalRows;
        return Math.Round(completenessRatio * 25, 1);
    }

    /// <summary>
    /// Uniqueness (0–25): Based on distinct ratio.
    /// For high-cardinality columns (likely IDs): high distinct ratio is good.
    /// For categorical: moderate distinct ratio is expected.
    /// All-same-value column scores low.
    /// </summary>
    private static double ScoreUniqueness(ColumnProfile col)
    {
        if (col.NonNullCount == 0)
            return 12.5; // Neutral if no data

        var distinctRatio = (double)col.DistinctCount / col.NonNullCount;

        // If there's only 1 distinct value, that's suspicious but not always wrong
        if (col.DistinctCount == 1 && col.NonNullCount > 10)
            return 5.0;

        // Boolean columns: 2 distinct values is perfect
        if (col.Category == ColumnCategory.Boolean)
            return col.DistinctCount == 2 ? 25.0 : col.DistinctCount == 1 ? 15.0 : 25.0;

        // For other columns, moderate uniqueness is ideal
        // Very low (constant) or very high (all unique in non-ID) can both be okay depending on context
        // We score linearly with a slight preference for higher diversity
        var score = distinctRatio switch
        {
            >= 0.95 => 25.0,   // Extremely unique (likely ID)
            >= 0.5 => 20.0 + (distinctRatio - 0.5) * 10, // Good diversity
            >= 0.1 => 15.0 + (distinctRatio - 0.1) * 12.5, // Moderate
            >= 0.01 => 10.0 + (distinctRatio - 0.01) * 55.6, // Low diversity
            _ => 5.0 + distinctRatio * 500 // Very low diversity
        };

        return Math.Round(Math.Min(25.0, Math.Max(0.0, score)), 1);
    }

    /// <summary>
    /// Validity (0–25): Based on type consistency and reasonable value ranges.
    /// Deductions for: empty strings in non-nullable columns, dates far in future/past, etc.
    /// </summary>
    private static double ScoreValidity(ColumnProfile col)
    {
        var score = 25.0;

        switch (col.Category)
        {
            case ColumnCategory.String:
                // Deduct for empty strings
                if (col.EmptyStringCount.HasValue && col.NonNullCount > 0)
                {
                    var emptyRatio = (double)col.EmptyStringCount.Value / col.NonNullCount;
                    score -= emptyRatio * 15; // Up to -15 for all empty strings
                }
                // Deduct if min length is 0 (suggests empty strings not caught by null check)
                if (col.MinLength == 0)
                    score -= 2;
                break;

            case ColumnCategory.Numeric:
                // Deduct if stddev is 0 but multiple values exist (constant column)
                if (col.StdDev == 0 && col.DistinctCount > 1)
                    score -= 5;
                break;

            case ColumnCategory.DateTime:
                // Dates are valid by type; no additional checks beyond nulls
                break;

            case ColumnCategory.Boolean:
                // Booleans are inherently valid
                break;
        }

        // Deduct for very high null percentage when column is marked non-nullable
        if (!col.IsNullable && col.NullPercentage > 0)
            score -= Math.Min(10, col.NullPercentage / 10);

        return Math.Round(Math.Max(0, Math.Min(25, score)), 1);
    }

    /// <summary>
    /// Distribution (0–25): Based on value spread and outlier presence.
    /// Deductions for high outlier %, extreme skew, or single-value dominance.
    /// </summary>
    private static double ScoreDistribution(ColumnProfile col)
    {
        if (col.NonNullCount == 0)
            return 12.5; // Neutral

        var score = 25.0;

        if (col.Category == ColumnCategory.Numeric)
        {
            // Deduct for outliers
            if (col.OutlierPercentage > 0)
            {
                score -= Math.Min(15, col.OutlierPercentage * 0.5);
            }

            // Deduct if a single value dominates
            if (col.TopValues.Count > 0 && col.TopValues[0].Percentage > 80)
            {
                score -= (col.TopValues[0].Percentage - 80) * 0.5;
            }
        }
        else if (col.Category == ColumnCategory.String)
        {
            // Deduct if top value dominates excessively
            if (col.TopValues.Count > 0 && col.TopValues[0].Percentage > 90)
            {
                score -= (col.TopValues[0].Percentage - 90) * 1.0;
            }
        }
        else if (col.Category == ColumnCategory.Boolean)
        {
            // Deduct if extremely skewed (>99% one value)
            if (col.TrueCount.HasValue && col.FalseCount.HasValue)
            {
                var total = col.TrueCount.Value + col.FalseCount.Value;
                if (total > 0)
                {
                    var maxRatio = (double)Math.Max(col.TrueCount.Value, col.FalseCount.Value) / total;
                    if (maxRatio > 0.99)
                        score -= 10;
                    else if (maxRatio > 0.95)
                        score -= 5;
                }
            }
        }

        return Math.Round(Math.Max(0, Math.Min(25, score)), 1);
    }

    /// <summary>
    /// Computes a drift score (0–100) between two file profiles.
    /// 0 = identical, 100 = completely different.
    /// </summary>
    public double ComputeDriftScore(FileProfile baseline, FileProfile comparison)
    {
        var driftFactors = new List<double>();

        // Row count drift
        if (baseline.RowCount > 0)
        {
            var rowDrift = Math.Abs((double)(comparison.RowCount - baseline.RowCount) / baseline.RowCount) * 100;
            driftFactors.Add(Math.Min(100, rowDrift));
        }

        // Quality score drift
        var scoreDrift = Math.Abs(comparison.OverallScore.Total - baseline.OverallScore.Total);
        driftFactors.Add(scoreDrift);

        // Column-level completeness drift
        var baselineCols = baseline.Columns.ToDictionary(c => c.Name);
        foreach (var compCol in comparison.Columns)
        {
            if (baselineCols.TryGetValue(compCol.Name, out var baseCol))
            {
                var completeDrift = Math.Abs(compCol.Completeness - baseCol.Completeness);
                driftFactors.Add(completeDrift);
            }
        }

        return driftFactors.Count > 0 ? Math.Round(driftFactors.Average(), 1) : 0;
    }
}
