using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using HipHipParquet.Models;
using HipHipParquet.Services;
using HipHipParquet.ViewModels;

namespace HipHipParquet.Views;

public partial class MarkdownHelperPanel : UserControl
{
    private readonly MarkdownService _markdownService;
    private bool _suppressDocumentEvents;
    private bool _previewDirty = true;

    public MarkdownEditorViewModel ViewModel { get; }
    public event EventHandler? PopOutRequested;

    public MarkdownHelperPanel()
    {
        InitializeComponent();
        _markdownService = App.Current.Services.GetService(typeof(MarkdownService)) as MarkdownService ?? new MarkdownService();
        ViewModel = new MarkdownEditorViewModel();
        DataContext = ViewModel;
        PreviewBrowser.Navigating += OnPreviewBrowserNavigating;
        EditorTextBox.TextChanged += OnEditorTextChanged;
    }

    public async Task OpenFileAsync(string filePath)
    {
        var content = await _markdownService.LoadFromFileAsync(filePath);
        LoadDocument(filePath, content, isDirty: false);
        ViewModel.StatusMessage = $"Opened {Path.GetFileName(filePath)}";
    }

    public MarkdownEditorState CreateEditorStateSnapshot()
    {
        return new MarkdownEditorState
        {
            FilePath = string.IsNullOrWhiteSpace(ViewModel.CurrentFilePath) ? null : ViewModel.CurrentFilePath,
            DraftContent = EditorTextBox.Text,
            SelectedProfile = ViewModel.SelectedProfile,
            IsDirty = ViewModel.IsDirty,
            SavedAtUtc = DateTime.UtcNow
        };
    }

    public Task LoadDraftStateAsync(MarkdownEditorState state)
    {
        ViewModel.SelectedProfile = state.SelectedProfile;
        LoadDocument(state.FilePath, state.DraftContent ?? string.Empty, state.IsDirty);
        ViewModel.StatusMessage = "Loaded markdown draft from window helper.";
        return Task.CompletedTask;
    }

    private void LoadDocument(string? filePath, string content, bool isDirty)
    {
        _suppressDocumentEvents = true;
        ViewModel.CurrentFilePath = filePath ?? string.Empty;
        ViewModel.DocumentText = content;
        EditorTextBox.Text = content;
        ViewModel.IsDirty = isDirty;
        _suppressDocumentEvents = false;
        _previewDirty = true;
        RefreshPreviewIfVisible();
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        LoadDocument(null, string.Empty, isDirty: false);
        ViewModel.StatusMessage = "Started a new markdown document.";
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Markdown files (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Open Markdown File"
        };

        if (openFileDialog.ShowDialog() == true)
            await OpenFileAsync(openFileDialog.FileName);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void OnSaveAsClick(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private async Task SaveAsync(bool saveAs)
    {
        var path = ViewModel.CurrentFilePath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown files (*.md)|*.md|Markdown files (*.markdown)|*.markdown|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Save Markdown File",
                FileName = string.IsNullOrWhiteSpace(path) ? "document.md" : Path.GetFileName(path)
            };

            if (sfd.ShowDialog() != true) return;
            path = sfd.FileName;
        }
        await _markdownService.SaveToFileAsync(path!, EditorTextBox.Text);
        ViewModel.CurrentFilePath = path!;
        ViewModel.IsDirty = false;
        ViewModel.StatusMessage = $"Saved {Path.GetFileName(path)}";
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDocumentEvents)
            return;

        ViewModel.IsDirty = true;
        ViewModel.StatusMessage = "Draft updated.";
        _previewDirty = true;
        RefreshPreviewIfVisible();
    }

    private void OnEditorTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, EditorTabs))
            return;

        ViewModel.IsPreviewActive = EditorTabs.SelectedIndex == 1;
        if (EditorTabs.SelectedIndex == 1)
            RefreshPreviewIfVisible(force: true);
    }

    private void RefreshPreviewIfVisible(bool force = false)
    {
        if (EditorTabs.SelectedIndex != 1)
            return;

        if (!force && !_previewDirty)
            return;

        try
        {
            var html = _markdownService.RenderHtmlDocument(EditorTextBox.Text, ViewModel.SelectedProfile);
            PreviewBrowser.NavigateToString(html);
            _previewDirty = false;
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Preview failed: {ex.Message}";
        }
    }

    private void OnPreviewBrowserNavigating(object? sender, NavigatingCancelEventArgs e)
    {
        if (e.Uri == null || e.Uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return;

        if (e.Uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || e.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || e.Uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                ViewModel.StatusMessage = $"Unable to open link: {e.Uri.AbsoluteUri}";
            }

            return;
        }

        e.Cancel = true;
        ViewModel.StatusMessage = $"Blocked opening {e.Uri.Scheme} links from the preview.";
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);
}
