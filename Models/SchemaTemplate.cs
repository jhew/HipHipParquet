namespace HipHipParquet.Models;

public class SchemaTemplateColumn
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public bool Nullable { get; set; }
}

public class SchemaTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<SchemaTemplateColumn> Columns { get; set; } = [];
}
