namespace HipHipParquet.Models;

/// <summary>How a recipe step can be replayed on another file.</summary>
public enum RecipeStepKind
{
    /// <summary>A column-wide grid action, exported as generated SQL.</summary>
    Rule,
    /// <summary>A Query Hub statement, exported as written.</summary>
    Sql,
    /// <summary>A hand edit to picked cells or rows. Listed, but not exported.</summary>
    Manual
}

/// <summary>Where a recipe step was made.</summary>
public enum RecipeStepOrigin
{
    Grid,
    QueryHub
}

/// <summary>
/// A single step in a cleaning recipe.
/// </summary>
public class RecipeStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RecipeStepKind Kind { get; set; }
    public RecipeStepOrigin Origin { get; set; }

    /// <summary>The Query Hub alias of the table the step changes.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Plain-English summary, e.g. "Trim whitespace in customer_name".</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>For <see cref="RecipeStepKind.Sql"/>: the statement as it was run.</summary>
    public string? Sql { get; set; }

    /// <summary>For <see cref="RecipeStepKind.Rule"/>: "trim", "replace", "set_null" or "dedupe".</summary>
    public string StepType { get; set; } = string.Empty;

    /// <summary>For <see cref="RecipeStepKind.Rule"/>: the columns the rule applies to.</summary>
    public List<string> Columns { get; set; } = [];

    /// <summary>Rule arguments, e.g. { "find": "N/A", "replace": "", "matchCase": "true" }.</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>True once the change has been written to a file.</summary>
    public bool IsSaved { get; set; }

    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Kept so recipes saved by earlier versions still deserialize.</summary>
    public string TargetColumn { get; set; } = string.Empty;
}
