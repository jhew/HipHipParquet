namespace HipHipParquet.Models;

/// <summary>
/// A single step in a cleaning recipe.
/// </summary>
public class RecipeStep
{
    public string StepType { get; set; } = string.Empty; // "trim", "cast", "normalize_nulls", etc.
    public string TargetColumn { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = []; // e.g., { "targetType": "int" }
}
