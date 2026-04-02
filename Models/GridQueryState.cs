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

/// <summary>
/// A single step in a cleaning recipe.
/// </summary>
public class RecipeStep
{
    public string StepType { get; set; } = string.Empty; // "trim", "cast", "normalize_nulls", etc.
    public string TargetColumn { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = []; // e.g., { "targetType": "int" }
}

/// <summary>
/// Represents the current state of grid filtering, searching, sorting, and paging.
/// Can be translated into a SQL query for dataset-wide operations instead of in-memory DataView.RowFilter.
/// </summary>
public class GridQueryState
{
    /// <summary>
    /// Active column-level value filters (column name → set of selected values).
    /// </summary>
    public Dictionary<string, HashSet<string>> ColumnFilters { get; set; } = [];

    /// <summary>
    /// Global search text across all columns.
    /// </summary>
    public string GlobalSearch { get; set; } = string.Empty;

    /// <summary>
    /// Current sort member path and direction (e.g., "ColumnName ASC").
    /// </summary>
    public string Sort { get; set; } = string.Empty;

    /// <summary>
    /// Current row limit for paging (e.g., 50_000).
    /// </summary>
    public int RowLimit { get; set; } = 50_000;

    /// <summary>
    /// Current row offset for paging (e.g., 0 for first batch, 50_000 for second batch).
    /// </summary>
    public int RowOffset { get; set; } = 0;

    /// <summary>
    /// List of column names in the current dataset (used for global search scope).
    /// </summary>
    public List<string> AvailableColumns { get; set; } = [];

    /// <summary>
    /// Total row count of the source dataset (before filtering).
    /// </summary>
    public long TotalRowCount { get; set; }

    /// <summary>
    /// Cached row count after filtering (set after query execution).
    /// </summary>
    public long FilteredRowCount { get; set; }

    /// <summary>
    /// Source file path (for building the DuckDB query expression).
    /// </summary>
    public string SourceFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Associated CSV import options (if applicable).
    /// </summary>
    public CsvImportOptions? CsvOptions { get; set; }

    /// <summary>
    /// Associated JSON import options (if applicable).
    /// </summary>
    public JsonImportOptions? JsonOptions { get; set; }

    /// <summary>
    /// Returns true if any column filter or global search is active.
    /// </summary>
    public bool HasActiveFilters =>
        ColumnFilters.Any(kvp => kvp.Value.Count > 0) ||
        !string.IsNullOrWhiteSpace(GlobalSearch);

    /// <summary>
    /// Clears all filters (resets to unfiltered state).
    /// </summary>
    public void ClearFilters()
    {
        foreach (var kvp in ColumnFilters)
            kvp.Value.Clear();
        GlobalSearch = string.Empty;
        Sort = string.Empty;
        RowOffset = 0;
    }

    /// <summary>
    /// Resets paging to the first batch.
    /// </summary>
    public void ResetPaging()
    {
        RowOffset = 0;
    }
}
