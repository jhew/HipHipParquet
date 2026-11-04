namespace HipHipParquet.Models;

public enum NotebookBlockKind
{
    Source,
    Query,
    Result,
    Validation,
    Export,
    /// <summary>A Query Hub statement that changed, created or dropped a table or view.</summary>
    Change
}

public class NotebookBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NotebookBlockKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? SourceAlias { get; set; }
    public string? Sql { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string TimeLabel => CreatedAtUtc.ToLocalTime().ToString("HH:mm");

    public string TimelineLabel
        => $"{CreatedAtUtc.ToLocalTime():HH:mm} [{Kind}] {Title} - {Summary}";
}
