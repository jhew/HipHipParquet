namespace HipHipParquet.Models;

/// <summary>
/// Data quality score with four component dimensions, each scored 0–25 for a total of 0–100.
/// </summary>
public class QualityScore
{
    /// <summary>Based on null percentage. 0% nulls = 25, 100% nulls = 0.</summary>
    public double Completeness { get; set; }

    /// <summary>Based on distinct value ratio and expected cardinality.</summary>
    public double Uniqueness { get; set; }

    /// <summary>Based on type consistency and value validity.</summary>
    public double Validity { get; set; }

    /// <summary>Based on distribution normality, skewness, and outlier presence.</summary>
    public double Distribution { get; set; }

    /// <summary>Overall score (0–100).</summary>
    public double Total => Math.Round(Completeness + Uniqueness + Validity + Distribution, 1);

    /// <summary>Color-coded grade for UI rendering.</summary>
    public string Grade => Total switch
    {
        >= 90 => "Excellent",
        >= 80 => "Good",
        >= 60 => "Fair",
        >= 40 => "Poor",
        _ => "Critical"
    };

    /// <summary>Hex color for the score gauge.</summary>
    public string Color => Total switch
    {
        >= 80 => "#4CAF50", // Green
        >= 60 => "#FF9800", // Orange
        _ => "#F44336"      // Red
    };
}
