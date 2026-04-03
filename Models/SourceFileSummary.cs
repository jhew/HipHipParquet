using System.IO;

namespace HipHipParquet.Models;

/// <summary>
/// File-level contribution metadata for multi-file loads.
/// </summary>
public class SourceFileSummary
{
    public string FilePath { get; set; } = string.Empty;
    public long RowCount { get; set; }
    public double ContributionPercent { get; set; }

    public string FileName => Path.GetFileName(FilePath);
}
