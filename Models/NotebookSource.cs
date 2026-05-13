namespace HipHipParquet.Models;

public enum NotebookSourceKind
{
    File,
    QueryResult,
    WorkingSet
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

    public string SourceLabel
    {
        get
        {
            var rowSuffix = RowCount == 1 ? "row" : "rows";
            return $"{Alias} | {DisplayName} | {RowCount:N0} {rowSuffix}";
        }
    }
}
