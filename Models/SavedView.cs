namespace HipHipParquet.Models;

/// <summary>
/// Represents a saved view or filter configuration that can be restored.
/// </summary>
public class SavedView
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, HashSet<string>> ColumnFilters { get; set; } = [];
    public string GlobalSearch { get; set; } = string.Empty;
    public string Sort { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
