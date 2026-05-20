namespace HipHipParquet.Models;

public enum NotebookValidationSeverity
{
    Info,
    Warning,
    Error
}

public class NotebookValidationFinding
{
    public NotebookValidationSeverity Severity { get; set; }
    public string CheckType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ColumnName { get; set; }
    public long? AffectedRows { get; set; }
}

public class NotebookValidationResult
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<NotebookValidationFinding> Findings { get; set; } = [];

    public bool HasErrors => Findings.Any(f => f.Severity == NotebookValidationSeverity.Error);
    public bool HasWarnings => Findings.Any(f => f.Severity == NotebookValidationSeverity.Warning);
}
