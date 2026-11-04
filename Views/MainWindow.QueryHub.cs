using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Win32;

namespace HipHipParquet.Views;

/// <summary>
/// Query Hub scripts that change data. Statements change an in-session copy of an opened file;
/// the file changes only when the user saves. A source with unsaved Hub changes shows read-only
/// in the grid, so the grid's copy and the Hub's copy never both hold unsaved edits.
/// </summary>
public partial class MainWindow
{
    private async void OnRunNotebookQueryClick(object sender, RoutedEventArgs e)
        => await RunNotebookScriptAsync(selectionOnly: false);

    private async void OnRunNotebookSelectionClick(object sender, RoutedEventArgs e)
        => await RunNotebookScriptAsync(selectionOnly: true);

    private async void OnNotebookQueryPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        e.Handled = true;
        await RunNotebookScriptAsync(selectionOnly: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private async Task RunNotebookScriptAsync(bool selectionOnly)
    {
        if (_notebookSources.Count == 0)
        {
            StatusText.Text = "Open a source before running notebook queries";
            return;
        }

        var sql = selectionOnly ? NotebookQueryTextBox.SelectedText : NotebookQueryTextBox.Text;
        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusText.Text = selectionOnly ? "Select part of the script to run it" : "Enter a SQL statement first";
            return;
        }

        IReadOnlyList<SqlStatement> statements;
        try
        {
            statements = SqlScriptParser.Parse(sql);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Query Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (statements.Any(s => s.IsWrite && IsActiveNotebookAlias(s.Target)) && !await ResolveGridEditsBeforeHubChangeAsync())
            return;

        NotebookScriptResult result;
        try
        {
            ShowLoading(statements.Count == 1 ? "Running SQL..." : $"Running {statements.Count} statements...");
            result = await GetNotebookSession().ExecuteScriptAsync(sql);
        }
        catch (Exception ex)
        {
            AddNotebookBlock(NotebookBlockKind.Query, "Failed", UserFacingError.Describe(ex), sql: sql);
            MessageBox.Show(this, $"Query failed: {UserFacingError.Describe(ex)}", "Query Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Query Hub script failed; nothing was changed";
            UpdateNotebookUiState();
            return;
        }
        finally
        {
            HideLoading();
        }

        foreach (var statement in result.Statements)
        {
            var summary = statement.CanUndo ? statement.Summary : $"{statement.Summary} (too large to undo)";
            AddNotebookBlock(
                statement.Statement.IsWrite ? NotebookBlockKind.Change : NotebookBlockKind.Query,
                statement.Statement.KindLabel,
                summary,
                statement.ResultSource?.Alias ?? statement.Statement.Target,
                statement.Statement.Sql);
        }

        // Show what the script produced: the last statement's result or the table it changed.
        var last = result.Statements[^1];
        var showAlias = last.ResultSource?.Alias ?? last.Statement.Target;
        await AfterHubSessionChangeAsync(result.ChangedAliases, showAlias);

        StatusText.Text = result.Statements.Count == 1
            ? last.Summary
            : $"Ran {result.Statements.Count} statements - {last.Summary}";
    }

    /// <summary>
    /// Before a Hub change to the source the grid shows: the grid's own unsaved edits must be
    /// saved or discarded first, or one copy would overwrite the other.
    /// </summary>
    private async Task<bool> ResolveGridEditsBeforeHubChangeAsync()
    {
        if (!_hasUnsavedChanges)
            return true;

        var choice = MessageBox.Show(this,
            "The grid has unsaved edits to this source.\n\n" +
            "Yes: save them to the file, then run.\nNo: discard them, then run.\nCancel: keep them and don't run.",
            "Unsaved Grid Edits",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Cancel)
            return false;

        if (choice == MessageBoxResult.Yes)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                MessageBox.Show(this, "These edits aren't tied to a file yet. Use File > Save As first.", "Unsaved Grid Edits",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return await SaveFileAsync(_currentFilePath);
        }

        _hasUnsavedChanges = false;
        _pendingEditCount = 0;
        UpdateWindowTitle();
        UpdatePendingChangesTray();
        return true;
    }

    private bool IsActiveNotebookAlias(string? alias)
        => alias != null && string.Equals(alias, _activeNotebookSourceAlias, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings the source list in line with the session after a script, save, discard or undo,
    /// then shows <paramref name="showAlias"/>, or reloads the active source if it was affected.
    /// </summary>
    private async Task AfterHubSessionChangeAsync(IReadOnlyCollection<string> affectedAliases, string? showAlias = null)
    {
        SyncNotebookSourcesFromSession();

        var target = FindNotebookSource(showAlias)
            ?? (affectedAliases.Any(IsActiveNotebookAlias) ? FindNotebookSource(_activeNotebookSourceAlias) : null);

        if (target == null && FindNotebookSource(_activeNotebookSourceAlias) == null)
            target = _notebookSources.FirstOrDefault();

        if (target != null)
            await ActivateNotebookSourceAsync(target, autoAnalyze: false);

        UpdateNotebookUiState();
    }

    private NotebookSource? FindNotebookSource(string? alias)
        => alias == null ? null : _notebookSources.FirstOrDefault(s => string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mirrors the session's sources: existing entries keep their place (refreshed), dropped ones
    /// go, new ones are appended.
    /// </summary>
    private void SyncNotebookSourcesFromSession()
    {
        var current = GetNotebookSession().GetSources().ToDictionary(s => s.Alias, StringComparer.OrdinalIgnoreCase);

        _suppressNotebookSourceSelectionChanged = true;
        try
        {
            for (var i = _notebookSources.Count - 1; i >= 0; i--)
            {
                if (current.Remove(_notebookSources[i].Alias, out var fresh))
                    _notebookSources[i] = fresh;
                else
                    _notebookSources.RemoveAt(i);
            }

            foreach (var added in current.Values)
                _notebookSources.Add(added);

            NotebookSourcesList.SelectedItem = FindNotebookSource(_activeNotebookSourceAlias);
        }
        finally
        {
            _suppressNotebookSourceSelectionChanged = false;
        }
    }

    private void UpdateQueryHubPendingBar()
    {
        var session = _notebookSession;
        var unsaved = session?.GetSourcesWithUnsavedChanges() ?? [];
        var undoDescription = session?.UndoDescription;
        var canUndo = session?.CanUndo == true;

        QueryHubPendingBar.Visibility = unsaved.Count > 0 || undoDescription != null ? Visibility.Visible : Visibility.Collapsed;
        QueryHubPendingText.Text = unsaved.Count > 0
            ? $"● Unsaved changes to {string.Join(", ", unsaved.Select(s => s.Alias))}. Files change only when you save."
            : "All changes are saved to their files.";

        SaveHubChangesButton.IsEnabled = unsaved.Count > 0;
        DiscardHubChangesButton.IsEnabled = unsaved.Count > 0;
        UndoHubChangeButton.IsEnabled = canUndo;
        UndoHubChangeButton.Content = canUndo ? $"Undo {undoDescription}" : "Undo";
        UndoHubChangeButton.ToolTip = canUndo || undoDescription == null
            ? "Undo the last statement that changed data"
            : $"{undoDescription} can't be undone: the table was too large to snapshot";
    }

    private async void OnSaveHubChangesClick(object sender, RoutedEventArgs e)
    {
        var unsaved = GetNotebookSession().GetSourcesWithUnsavedChanges();
        if (unsaved.Count == 0)
            return;

        var saved = await SaveHubChangesAsync(unsaved, confirm: true);
        await AfterHubSessionChangeAsync(saved);
    }

    private async void OnDiscardHubChangesClick(object sender, RoutedEventArgs e)
    {
        var unsaved = GetNotebookSession().GetSourcesWithUnsavedChanges();
        if (unsaved.Count == 0)
            return;

        await DiscardHubChangesAsync(unsaved);
    }

    private async void OnUndoHubChangeClick(object sender, RoutedEventArgs e)
    {
        var session = GetNotebookSession();
        var target = session.UndoTarget;
        if (target == null)
            return;

        if (IsActiveNotebookAlias(target) && !await ResolveGridEditsBeforeHubChangeAsync())
            return;

        try
        {
            var undone = await session.UndoLastChangeAsync();
            AddNotebookBlock(NotebookBlockKind.Change, "Undo", undone, target);
            StatusText.Text = $"Undid {undone}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UserFacingError.Describe(ex), "Undo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await AfterHubSessionChangeAsync([target]);
    }

    private async void OnSaveHubSourceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NotebookSource source)
            return;

        var saved = await SaveHubChangesAsync([source], confirm: true);
        await AfterHubSessionChangeAsync(saved);
    }

    private async void OnSaveHubSourceAsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NotebookSource source)
            return;

        var path = PromptHubSaveAsPath(source);
        if (path == null)
            return;

        if (await SaveHubSourceToFileAsync(source, path))
            await AfterHubSessionChangeAsync([source.Alias]);
    }

    private async void OnDiscardHubSourceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NotebookSource source)
            await DiscardHubChangesAsync([source]);
    }

    /// <summary>Saves each source to its own file (asking for a path where it has none). Returns the aliases saved.</summary>
    private async Task<List<string>> SaveHubChangesAsync(IReadOnlyList<NotebookSource> sources, bool confirm)
    {
        var saved = new List<string>();
        var inPlace = sources.Where(s => s.CanSaveInPlace).ToList();
        if (confirm && inPlace.Count > 0)
        {
            var files = string.Join("\n", inPlace.Select(s => $"  {s.Alias} → {s.FilePath}"));
            var answer = MessageBox.Show(this,
                $"Save the Query Hub changes to these files? This overwrites them.\n\n{files}",
                "Save Query Hub Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return saved;
        }

        foreach (var source in sources)
        {
            string? path = null;
            if (!source.CanSaveInPlace)
            {
                path = PromptHubSaveAsPath(source);
                if (path == null)
                    break;
            }

            if (!await SaveHubSourceToFileAsync(source, path))
                break;
            saved.Add(source.Alias);
        }

        return saved;
    }

    private async Task<bool> SaveHubSourceToFileAsync(NotebookSource source, string? path)
    {
        var fileName = Path.GetFileName(path ?? source.FilePath ?? source.Alias);
        try
        {
            ShowLoading($"Saving {source.Alias}...");

            // The file being written may be the one the window watches for outside changes.
            if (_fileWatcher != null)
                _fileWatcher.EnableRaisingEvents = false;

            await GetNotebookSession().SaveSourceAsync(source.Alias, path);
            AddNotebookBlock(NotebookBlockKind.Export, "Saved", $"{source.Alias} → {fileName}", source.Alias);
            StatusText.Text = $"Saved {source.Alias} to {fileName}";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save {source.Alias}: {UserFacingError.Describe(ex)}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            if (_fileWatcher != null)
                _fileWatcher.EnableRaisingEvents = true;
            HideLoading();
        }
    }

    private async Task DiscardHubChangesAsync(IReadOnlyList<NotebookSource> sources)
    {
        var names = string.Join(", ", sources.Select(s => s.Alias));
        var answer = MessageBox.Show(this,
            $"Discard the unsaved Query Hub changes to {names}?\n\nThey return to their saved files, and their changes can't be undone afterwards.",
            "Discard Query Hub Changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var session = GetNotebookSession();
        var discarded = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                await session.DiscardChangesAsync(source.Alias);
                AddNotebookBlock(NotebookBlockKind.Change, "Discarded", $"{source.Alias} returned to its saved file", source.Alias);
                discarded.Add(source.Alias);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't discard {source.Alias}: {UserFacingError.Describe(ex)}", "Discard",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        StatusText.Text = $"Discarded changes to {string.Join(", ", discarded)}";
        await AfterHubSessionChangeAsync(discarded);
    }

    private string? PromptHubSaveAsPath(NotebookSource source)
    {
        var extension = source.Format.HasValue && source.FilePath != null ? Path.GetExtension(source.FilePath) : ".parquet";
        var dialog = new SaveFileDialog
        {
            Filter = FileFormatDetector.GetSaveFileDialogFilter(),
            Title = $"Save {source.Alias}",
            FileName = source.Alias + extension
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// After the grid saves a file, the Hub's copy of that file is reloaded from it. The grid can
    /// only edit a source with no unsaved Hub changes, so nothing of the Hub's is lost.
    /// </summary>
    private async Task ReloadHubCopyAfterGridSaveAsync(string filePath)
    {
        if (_notebookSession == null)
            return;

        var fullPath = Path.GetFullPath(filePath);
        foreach (var source in _notebookSession.GetSources().Where(s =>
                     s is { Kind: NotebookSourceKind.File, IsMaterialized: true, HasUnsavedChanges: false } &&
                     string.Equals(Path.GetFullPath(s.FilePath ?? string.Empty), fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            try { await _notebookSession.DiscardChangesAsync(source.Alias); }
            catch (Exception ex) { StatusText.Text = $"Saved, but the Query Hub copy of {source.Alias} couldn't reload: {ex.Message}"; }
        }

        SyncNotebookSourcesFromSession();
        UpdateNotebookUiState();
    }

    private void OnInsertColumnNameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name)
            InsertIntoNotebookEditor(QuoteSqlIdentifierIfNeeded(name));
        e.Handled = true;
    }

    private void OnInsertSourceNameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NotebookSource source)
            InsertIntoNotebookEditor(QuoteSqlIdentifierIfNeeded(source.Alias));
    }

    private void InsertIntoNotebookEditor(string text)
    {
        NotebookQueryTextBox.SelectedText = text;
        NotebookQueryTextBox.CaretIndex = NotebookQueryTextBox.SelectionStart + NotebookQueryTextBox.SelectionLength;
        NotebookQueryTextBox.Focus();
    }

    private static string QuoteSqlIdentifierIfNeeded(string name)
        => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") ? name : ParquetService.EscapeDuckDbIdentifier(name);
}
