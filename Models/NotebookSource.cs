namespace HipHipParquet.Models;

public enum NotebookSourceKind
{
    File,
    QueryResult,
    WorkingSet,
    /// <summary>A table or view created in the Query Hub with CREATE TABLE / CREATE VIEW.</summary>
    Table
}

public class NotebookColumnSchema
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Nullable { get; set; }
}

public class NotebookSource
{
    public string Alias { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public NotebookSourceKind Kind { get; set; }
    public SupportedFileFormat? Format { get; set; }
    public string? FilePath { get; set; }
    public List<string> FilePaths { get; set; } = [];
    public CsvImportOptions? CsvOptions { get; set; }
    public JsonImportOptions? JsonOptions { get; set; }
    public long RowCount { get; set; }
    public List<NotebookColumnSchema> Columns { get; set; } = [];
    public string? QuerySql { get; set; }

    /// <summary>
    /// For a file source, the DuckDB reader expression over its file(s). The source starts as a
    /// view over it; Discard recreates that view.
    /// </summary>
    public string? ReaderExpression { get; set; }

    /// <summary>
    /// For a file source: its rows have been copied into a session table so statements can
    /// change them. The file is untouched until Save.
    /// </summary>
    public bool IsMaterialized { get; set; }

    /// <summary>For a file source: the session copy differs from the file.</summary>
    public bool HasUnsavedChanges { get; set; }

    /// <summary>Whether Save can write back to the source's own file (one file, not a multi-file set).</summary>
    public bool CanSaveInPlace => Kind == NotebookSourceKind.File && FilePaths.Count <= 1 && !string.IsNullOrEmpty(FilePath);

    public string SourceLabel
    {
        get
        {
            var rowSuffix = RowCount == 1 ? "row" : "rows";
            var unsaved = HasUnsavedChanges ? " ● unsaved" : string.Empty;
            return $"{Alias} | {DisplayName} | {RowCount:N0} {rowSuffix}{unsaved}";
        }
    }

    internal NotebookSource Clone() => (NotebookSource)MemberwiseClone();
}
