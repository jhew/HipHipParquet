using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using HipHipParquet.Models;
using HipHipParquet.Services;
using HipHipParquet.ViewModels;

namespace HipHipParquet.Views;

public partial class MarkdownEditorWindow : Window
{
    private readonly MarkdownService _markdownService;
    private readonly WorkspaceService _workspaceService;
    private readonly System.Windows.Threading.DispatcherTimer _previewDebounceTimer;
    private readonly System.Windows.Threading.DispatcherTimer _persistDebounceTimer;
    private bool _suppressDocumentEvents;
    private bool _closingConfirmed;
    private string? _pendingOpenFilePath;
    private MarkdownEditorState? _pendingDraftState;
    private bool _previewDirty;

    public MarkdownEditorViewModel ViewModel { get; }

    public MarkdownEditorWindow(MarkdownService markdownService, WorkspaceService workspaceService)
    {
        InitializeComponent();

        _markdownService = markdownService;
        _workspaceService = workspaceService;
        ViewModel = new MarkdownEditorViewModel();
        DataContext = ViewModel;

        _previewDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _previewDebounceTimer.Tick += PreviewDebounceTimerOnTick;

        _persistDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _persistDebounceTimer.Tick += PersistDebounceTimerOnTick;

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        PreviewBrowser.Navigating += OnPreviewBrowserNavigating;
        EditorTextBox.TextChanged += OnEditorTextChanged;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_pendingDraftState != null)
        {
            await ApplyDraftStateAsync(_pendingDraftState);
            _pendingDraftState = null;
        }
        else
        {
            await RestoreStateAsync();
        }

        if (!string.IsNullOrWhiteSpace(_pendingOpenFilePath))
        {
            var pendingFile = _pendingOpenFilePath;
            _pendingOpenFilePath = null;
            await OpenFileAsync(pendingFile);
        }

        RefreshPreviewIfVisible();
        EditorTextBox.Focus();
        EditorTextBox.CaretIndex = EditorTextBox.Text.Length;
    }

    public async Task LoadDraftStateAsync(MarkdownEditorState state)
    {
        if (!IsLoaded)
        {
            _pendingDraftState = state;
            return;
        }

        await ApplyDraftStateAsync(state);
    }

    public async Task OpenFileAsync(string filePath)
    {
        if (!IsLoaded)
        {
            _pendingOpenFilePath = filePath;
            return;
        }

        if (!await EnsureDocumentCanChangeAsync("opening a different file"))
            return;

        try
        {
            var content = await _markdownService.LoadFromFileAsync(filePath);
            LoadDocument(filePath, content, isDirty: false);
            ViewModel.StatusMessage = $"Opened {Path.GetFileName(filePath)}";
            await PersistStateAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the markdown file.\n\n{ex.Message}", "Open Markdown File", MessageBoxButton.OK, MessageBoxImage.Warning);
            ViewModel.StatusMessage = "Open failed.";
        }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Markdown files (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Open Markdown File"
        };

        if (openFileDialog.ShowDialog() != true)
            return;

        await OpenFileAsync(openFileDialog.FileName);
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureDocumentCanChangeAsync("creating a new document"))
            return;

        LoadDocument(null, string.Empty, isDirty: false);
        ViewModel.StatusMessage = "Started a new markdown document.";
        await PersistStateAsync();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
        => await SaveDocumentAsync(promptForPath: string.IsNullOrWhiteSpace(ViewModel.CurrentFilePath));

    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
        => await SaveDocumentAsync(promptForPath: true);

    private void OnEditorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressDocumentEvents)
            return;

        ViewModel.IsDirty = true;
        ViewModel.StatusMessage = "Draft updated.";
        _previewDirty = true;
        SchedulePreview();
        SchedulePersist();
    }

    private void OnEditorTabSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, EditorTabs))
            return;

        if (IsPreviewTabActive())
            RefreshPreviewIfVisible(force: true);
    }

    private void OnPreviewBrowserNavigating(object? sender, NavigatingCancelEventArgs e)
    {
        if (e.Uri == null || e.Uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return;

        if (e.Uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            e.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            e.Uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
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

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed)
            return;

        e.Cancel = true;

        if (!await EnsureDocumentCanChangeAsync("closing"))
            return;

        await PersistStateAsync();
        _closingConfirmed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Tick -= PreviewDebounceTimerOnTick;
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Tick -= PersistDebounceTimerOnTick;
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

    private async Task RestoreStateAsync()
    {
        var state = await _workspaceService.GetMarkdownEditorStateAsync();
        if (state == null)
        {
            ViewModel.StatusMessage = "Open or create a Markdown document.";
            return;
        }

        string content = state.DraftContent ?? string.Empty;
        var restoredPath = state.FilePath;
        if (string.IsNullOrEmpty(content) &&
            !string.IsNullOrWhiteSpace(restoredPath) &&
            File.Exists(restoredPath))
        {
            try
            {
                content = await _markdownService.LoadFromFileAsync(restoredPath);
            }
            catch
            {
                content = string.Empty;
            }
        }

        _suppressDocumentEvents = true;
        ViewModel.CurrentFilePath = state.FilePath ?? string.Empty;
        ViewModel.SelectedProfile = state.SelectedProfile;
        ViewModel.DocumentText = content;
        EditorTextBox.Text = content;
        ViewModel.IsDirty = state.IsDirty;
        _suppressDocumentEvents = false;

        ViewModel.StatusMessage = string.IsNullOrWhiteSpace(state.FilePath)
            ? "Restored markdown draft."
            : $"Restored {System.IO.Path.GetFileName(state.FilePath)} draft.";
    }

    private async Task ApplyDraftStateAsync(MarkdownEditorState state)
    {
        _suppressDocumentEvents = true;
        ViewModel.CurrentFilePath = state.FilePath ?? string.Empty;
        ViewModel.SelectedProfile = state.SelectedProfile;
        ViewModel.DocumentText = state.DraftContent ?? string.Empty;
        EditorTextBox.Text = ViewModel.DocumentText;
        ViewModel.IsDirty = state.IsDirty;
        _suppressDocumentEvents = false;
        _previewDirty = true;
        RefreshPreviewIfVisible();
        ViewModel.StatusMessage = "Loaded markdown draft from embedded helper.";
        await PersistStateAsync();
    }

    private async Task<bool> EnsureDocumentCanChangeAsync(string actionLabel)
    {
        if (!ViewModel.IsDirty)
            return true;

        var keepDraftNote = actionLabel.Equals("closing", StringComparison.OrdinalIgnoreCase)
            ? "\n\nSelecting No closes the window and keeps this draft for next time."
            : string.Empty;

        var result = MessageBox.Show(
            $"You have unsaved markdown changes. Save before {actionLabel}?{keepDraftNote}",
            "Unsaved Markdown Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
            return false;

        if (result == MessageBoxResult.Yes)
            return await SaveDocumentAsync(promptForPath: string.IsNullOrWhiteSpace(ViewModel.CurrentFilePath));

        return true;
    }

    private async Task<bool> SaveDocumentAsync(bool promptForPath)
    {
        var targetPath = ViewModel.CurrentFilePath;
        if (promptForPath || string.IsNullOrWhiteSpace(targetPath))
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown files (*.md)|*.md|Markdown files (*.markdown)|*.markdown|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Save Markdown File",
                FileName = string.IsNullOrWhiteSpace(targetPath)
                    ? "document.md"
                    : System.IO.Path.GetFileName(targetPath)
            };

            if (saveFileDialog.ShowDialog() != true)
                return false;

            targetPath = saveFileDialog.FileName;
        }

        try
        {
            await _markdownService.SaveToFileAsync(targetPath!, EditorTextBox.Text);
            ViewModel.CurrentFilePath = targetPath!;
            ViewModel.IsDirty = false;
            ViewModel.StatusMessage = $"Saved {System.IO.Path.GetFileName(targetPath)}";
            await PersistStateAsync();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the markdown file.\n\n{ex.Message}", "Save Markdown File", MessageBoxButton.OK, MessageBoxImage.Warning);
            ViewModel.StatusMessage = "Save failed.";
            return false;
        }
    }

    private bool IsPreviewTabActive()
        => EditorTabs.SelectedIndex == 1;

    private void RefreshPreviewIfVisible(bool force = false)
    {
        if (!force && (!IsPreviewTabActive() || !_previewDirty))
            return;

        try
        {
            ViewModel.PreviewHtml = _markdownService.RenderHtmlDocument(EditorTextBox.Text, ViewModel.SelectedProfile);
            PreviewBrowser.NavigateToString(ViewModel.PreviewHtml);
            _previewDirty = false;
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Preview failed: {ex.Message}";
        }
    }

    private void SchedulePreview()
    {
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    private void SchedulePersist()
    {
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Start();
    }

    private void PreviewDebounceTimerOnTick(object? sender, EventArgs e)
    {
        _previewDebounceTimer.Stop();
        RefreshPreviewIfVisible();
    }

    private async void PersistDebounceTimerOnTick(object? sender, EventArgs e)
    {
        _persistDebounceTimer.Stop();
        await PersistStateAsync();
    }

    private async Task PersistStateAsync()
    {
        await _workspaceService.SaveMarkdownEditorStateAsync(new MarkdownEditorState
        {
            FilePath = string.IsNullOrWhiteSpace(ViewModel.CurrentFilePath) ? null : ViewModel.CurrentFilePath,
            DraftContent = EditorTextBox.Text,
            SelectedProfile = ViewModel.SelectedProfile,
            IsDirty = ViewModel.IsDirty,
            SavedAtUtc = DateTime.UtcNow
        });
    }
}
