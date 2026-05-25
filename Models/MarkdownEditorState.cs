namespace HipHipParquet.Models;

public class MarkdownEditorState
{
    public string? FilePath { get; set; }
    public string DraftContent { get; set; } = string.Empty;
    public MarkdownFlavorProfile SelectedProfile { get; set; } = MarkdownFlavorProfile.ExtendedBestEffort;
    public bool IsDirty { get; set; }
    public DateTime SavedAtUtc { get; set; }
}
