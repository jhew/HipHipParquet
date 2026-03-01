namespace HipHipParquet.Models;

/// <summary>
/// A single finding or anomaly detected by the NarrativeService.
/// </summary>
public class NarrativeItem
{
    public NarrativeSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ColumnName { get; set; }
    public string Icon => Severity switch
    {
        NarrativeSeverity.Critical => "🔴",
        NarrativeSeverity.Warning => "🟡",
        NarrativeSeverity.Info => "🟢",
        _ => "⚪"
    };
    public string SeverityColor => Severity switch
    {
        NarrativeSeverity.Critical => "#F44336",
        NarrativeSeverity.Warning => "#FF9800",
        NarrativeSeverity.Info => "#4CAF50",
        _ => "#9E9E9E"
    };
}

public enum NarrativeSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
