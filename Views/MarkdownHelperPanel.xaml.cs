using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using HipHipParquet.Models;
using HipHipParquet.Services;
using HipHipParquet.ViewModels;

namespace HipHipParquet.Views;

public partial class MarkdownHelperPanel : UserControl
{
    private readonly MarkdownService _markdownService;
    private readonly DispatcherTimer _persistDebounceTimer;
    private WorkspaceService? _workspaceService;
    private bool _suppressDocumentEvents;
    private bool _previewDirty = true;

    public MarkdownEditorViewModel ViewModel { get; }
    public event EventHandler? PopOutRequested;
    public event EventHandler? FocusModeToggleRequested;

    public MarkdownHelperPanel()
    {
        InitializeComponent();
        _markdownService = App.Current.Services.GetService(typeof(MarkdownService)) as MarkdownService ?? new MarkdownService();
        ViewModel = new MarkdownEditorViewModel();
        DataContext = ViewModel;
        PreviewBrowser.Navigating += OnPreviewBrowserNavigating;
        EditorTextBox.TextChanged += OnEditorTextChanged;
        _persistDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _persistDebounceTimer.Tick += PersistDebounceTimerOnTick;
    }

    public void InitializeWorkspaceService(WorkspaceService workspaceService)
        => _workspaceService = workspaceService;

    public async Task RestoreDraftAsync()
    {
        if (_workspaceService == null) return;
        var state = await _workspaceService.GetMarkdownEditorStateAsync();
        if (state == null) return;

        var content = state.DraftContent ?? string.Empty;
        if (string.IsNullOrEmpty(content) &&
            !string.IsNullOrWhiteSpace(state.FilePath) &&
            File.Exists(state.FilePath))
        {
            try { content = await _markdownService.LoadFromFileAsync(state.FilePath); }
            catch { content = string.Empty; }
        }

        _suppressDocumentEvents = true;
        ViewModel.CurrentFilePath = state.FilePath ?? string.Empty;
        ViewModel.SelectedProfile = state.SelectedProfile;
        ViewModel.DocumentText = content;
        EditorTextBox.Text = content;
        ViewModel.IsDirty = state.IsDirty;
        _suppressDocumentEvents = false;
        _previewDirty = true;
        ViewModel.StatusMessage = string.IsNullOrWhiteSpace(state.FilePath)
            ? "Restored markdown draft."
            : $"Restored {Path.GetFileName(state.FilePath)} draft.";
    }

    public async Task PersistDraftAsync()
    {
        if (_workspaceService == null) return;
        await _workspaceService.SaveMarkdownEditorStateAsync(new MarkdownEditorState
        {
            FilePath = string.IsNullOrWhiteSpace(ViewModel.CurrentFilePath) ? null : ViewModel.CurrentFilePath,
            DraftContent = EditorTextBox.Text,
            SelectedProfile = ViewModel.SelectedProfile,
            IsDirty = ViewModel.IsDirty,
            SavedAtUtc = DateTime.UtcNow
        });
    }

    public async Task OpenFileAsync(string filePath)
    {
        if (!await EnsureDraftCanChangeAsync("opening a different file"))
            return;

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

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureDraftCanChangeAsync("creating a new document"))
            return;

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

    private async Task<bool> EnsureDraftCanChangeAsync(string actionLabel)
    {
        if (!ViewModel.IsDirty)
            return true;

        var result = MessageBox.Show(
            $"You have unsaved markdown changes. Save before {actionLabel}?",
            "Unsaved Markdown Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
            return false;

        if (result == MessageBoxResult.Yes)
            return await SaveAsync(saveAs: false);

        return true;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void OnSaveAsClick(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private async Task<bool> SaveAsync(bool saveAs)
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

            if (sfd.ShowDialog() != true) return false;
            path = sfd.FileName;
        }
        await _markdownService.SaveToFileAsync(path!, EditorTextBox.Text);
        ViewModel.CurrentFilePath = path!;
        ViewModel.IsDirty = false;
        ViewModel.StatusMessage = $"Saved {Path.GetFileName(path)}";
        return true;
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDocumentEvents)
            return;

        ViewModel.IsDirty = true;
        ViewModel.StatusMessage = "Draft updated.";
        _previewDirty = true;
        SchedulePersist();
        RefreshPreviewIfVisible();
    }

    private void SchedulePersist()
    {
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Start();
    }

    private async void PersistDebounceTimerOnTick(object? sender, EventArgs e)
    {
        _persistDebounceTimer.Stop();
        await PersistDraftAsync();
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

    private void OnFocusModeClick(object sender, RoutedEventArgs e) => FocusModeToggleRequested?.Invoke(this, EventArgs.Empty);
    private void OnPopOutClick(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);
}
