namespace HipHipParquet.Models;

/// <summary>
/// Represents a repeatable cleaning recipe (e.g., trim, cast, normalize nulls).
/// </summary>
public class CleaningRecipe
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<RecipeStep> Steps { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
