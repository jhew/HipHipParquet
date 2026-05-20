namespace HipHipParquet.Models;

public class NotebookQueryDocument
{
    public string Name { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
