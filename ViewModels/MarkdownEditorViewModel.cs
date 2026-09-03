using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HipHipParquet.Models;

namespace HipHipParquet.ViewModels;

public partial class MarkdownEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _currentFilePath = string.Empty;

    [ObservableProperty]
    private string _documentText = string.Empty;

    [ObservableProperty]
    private MarkdownFlavorProfile _selectedProfile = MarkdownFlavorProfile.ExtendedBestEffort;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _statusMessage = "Open or create a Markdown document.";

    [ObservableProperty]
    private string _previewHtml = string.Empty;

    [ObservableProperty]
    private bool _isPreviewActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusModeButtonLabel))]
    private bool _isFocusModeActive;

    public string FocusModeButtonLabel => IsFocusModeActive ? "Exit focus" : "Focus";

    /// <summary>Backs the toolbar picker; the status label alone gave no way to change this.</summary>
    public IReadOnlyList<MarkdownFlavorProfile> Profiles { get; } = Enum.GetValues<MarkdownFlavorProfile>();

    public string PreviewProfileLabel => $"Preview profile: {SelectedProfile.GetDisplayName()}";

    partial void OnSelectedProfileChanged(MarkdownFlavorProfile value)
        => OnPropertyChanged(nameof(PreviewProfileLabel));

    public string WindowTitle
        => string.IsNullOrWhiteSpace(CurrentFilePath)
            ? $"Markdown Helper{(IsDirty ? " *" : string.Empty)}"
            : $"{Path.GetFileName(CurrentFilePath)}{(IsDirty ? " *" : string.Empty)} - Markdown Helper";

    partial void OnCurrentFilePathChanged(string value)
        => OnPropertyChanged(nameof(WindowTitle));

    partial void OnIsDirtyChanged(bool value)
        => OnPropertyChanged(nameof(WindowTitle));
}
