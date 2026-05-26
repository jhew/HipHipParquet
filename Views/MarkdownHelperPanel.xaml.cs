using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using HipHipParquet.Services;
using HipHipParquet.ViewModels;

namespace HipHipParquet.Views;

public partial class MarkdownHelperPanel : UserControl
{
    private readonly MarkdownService _markdownService;
    public MarkdownEditorViewModel ViewModel { get; }
    public event EventHandler? PopOutRequested;

    public MarkdownHelperPanel()
    {
        InitializeComponent();
        _markdownService = new MarkdownService();
        ViewModel = new MarkdownEditorViewModel();
        DataContext = ViewModel;
        PreviewBrowser.Navigating += OnPreviewBrowserNavigating;
    }

    public async Task OpenFileAsync(string filePath)
    {
        var content = await _markdownService.LoadFromFileAsync(filePath);
        ViewModel.CurrentFilePath = filePath;
        ViewModel.DocumentText = content;
        ViewModel.IsDirty = false;
        ViewModel.StatusMessage = $"Opened {Path.GetFileName(filePath)}";
        RefreshPreview();
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CurrentFilePath = string.Empty;
        ViewModel.DocumentText = string.Empty;
        ViewModel.IsDirty = false;
        ViewModel.StatusMessage = "Started a new markdown document.";
        RefreshPreview();
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*" };
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
            var sfd = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*" };
            if (sfd.ShowDialog() != true) return;
            path = sfd.FileName;
        }
        await _markdownService.SaveToFileAsync(path!, ViewModel.DocumentText ?? string.Empty);
        ViewModel.CurrentFilePath = path!;
        ViewModel.IsDirty = false;
        ViewModel.StatusMessage = $"Saved {Path.GetFileName(path)}";
    }

    private void OnEditorTabSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void RefreshPreview()
    {
        if (EditorTabs.SelectedIndex != 1) return;
        PreviewBrowser.NavigateToString(_markdownService.ToHtmlDocument(ViewModel.DocumentText));
    }

    private void OnPreviewBrowserNavigating(object? sender, NavigatingCancelEventArgs e)
    {
        if (e.Uri == null || e.Uri.Scheme == "about") return;
        e.Cancel = true;
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);
}
