namespace HipHipParquet.Models;

/// <summary>
/// Aggregated profile for an entire Parquet file containing all column profiles and overall metrics.
/// </summary>
public class FileProfile
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public SupportedFileFormat SourceFormat { get; set; }
    public long RowCount { get; set; }
    public int ColumnCount { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileSizeFormatted => FormatFileSize(FileSizeBytes);
    public DateTime AnalyzedAt { get; set; } = DateTime.Now;

    public List<ColumnProfile> Columns { get; set; } = [];

    // Overall quality score
    public QualityScore OverallScore { get; set; } = new();

    // Summary counters
    public int NumericColumnCount => Columns.Count(c => c.Category == ColumnCategory.Numeric);
    public int StringColumnCount => Columns.Count(c => c.Category == ColumnCategory.String);
    public int BooleanColumnCount => Columns.Count(c => c.Category == ColumnCategory.Boolean);
    public int DateTimeColumnCount => Columns.Count(c => c.Category == ColumnCategory.DateTime);
    public int ColumnsWithNulls => Columns.Count(c => c.NullCount > 0);
    public double OverallCompleteness => Columns.Count == 0 ? 100 : Math.Round(Columns.Average(c => c.Completeness), 2);

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
