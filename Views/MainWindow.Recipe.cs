using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Win32;

namespace HipHipParquet.Views;

/// <summary>
/// File › Recipe: the session's changes as steps, exported as a DuckDB SQL script or a Python
/// script so the same cleanup can be run on the next file without the app.
/// </summary>
public partial class MainWindow
{
    private void OnShowRecipeClick(object sender, RoutedEventArgs e) => ShowRecipeDialog();

    private void ShowRecipeDialog()
    {
        var steps = _recipe.Steps;
        var manualCount = steps.Count(step => step.Kind == RecipeStepKind.Manual);

        var dialog = GridActionDialogs.CreateDialog(this, "Recipe", stack =>
        {
            stack.Children.Add(new TextBlock
            {
                Text = steps.Count == 0
                    ? "No changes yet. Grid actions and Query Hub statements are listed here as you make them."
                    : "Every change made this session, in order. Export it to run the same cleanup on another file, without Hip Hip Parquet.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                Margin = new Thickness(0, 0, 0, 12)
            });

            if (steps.Count == 0)
                return;

            var list = new StackPanel();
            var number = 0;
            foreach (var step in steps)
                list.Children.Add(CreateRecipeRow(++number, step));

            stack.Children.Add(new Border
            {
                BorderBrush = ThemeBrushes.Border,
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    Content = list,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 360,
                    Width = 560
                }
            });

            if (manualCount > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = manualCount == 1
                        ? "1 step is a hand edit to picked cells or rows. It's listed in the export but not repeated, since those rows won't be the same in another file."
                        : $"{manualCount} steps are hand edits to picked cells or rows. They're listed in the export but not repeated, since those rows won't be the same in another file.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 560,
                    Foreground = ThemeBrushes.TextSecondary,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }
        });

        var exportSql = new Button { Content = "Export SQL...", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsEnabled = steps.Count > 0 };
        var exportPython = new Button { Content = "Export Python...", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsEnabled = steps.Count > 0 };
        var clear = new Button { Content = "Clear", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = steps.Count > 0 };
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, IsDefault = true };

        exportSql.Click += async (_, _) => await ExportRecipeAsync(dialog, python: false);
        exportPython.Click += async (_, _) => await ExportRecipeAsync(dialog, python: true);
        clear.Click += (_, _) =>
        {
            var answer = MessageBox.Show(dialog,
                "Clear the recipe? Your data isn't changed; only the list of steps is emptied.",
                "Clear Recipe", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            _recipe.Clear();
            dialog.Close();
            StatusText.Text = "Recipe cleared";
        };

        GridActionDialogs.AppendDialogButtons(dialog, exportSql, exportPython, clear, close);
        dialog.ShowDialog();
    }

    private static FrameworkElement CreateRecipeRow(int number, RecipeStep step)
    {
        var (label, brush) = step.Kind switch
        {
            RecipeStepKind.Rule => ("Rule", ThemeBrushes.SuccessText),
            RecipeStepKind.Sql => ("SQL", ThemeBrushes.InfoText),
            _ => ("Hand edit, not exported", ThemeBrushes.TextMuted)
        };

        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run($"{number}. ") { Foreground = ThemeBrushes.TextMuted });
        text.Inlines.Add(new Run(step.Description) { Foreground = ThemeBrushes.TextPrimary });
        text.Inlines.Add(new Run($"   {step.Target} · {label}{(step.IsSaved ? " · saved" : string.Empty)}") { Foreground = brush });

        var row = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        row.Children.Add(text);
        if (step.Kind == RecipeStepKind.Sql && !string.IsNullOrWhiteSpace(step.Sql))
        {
            row.Children.Add(new TextBlock
            {
                Text = step.Sql,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                Foreground = ThemeBrushes.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 2, 0, 0)
            });
        }

        return row;
    }

    private async Task ExportRecipeAsync(Window owner, bool python)
    {
        var sources = GetRecipeSources();
        var baseName = sources.FirstOrDefault(source => source.FilePath != null) is { FilePath: { } path }
            ? Path.GetFileNameWithoutExtension(path) + ".recipe"
            : "recipe";

        var dialog = new SaveFileDialog
        {
            Title = python ? "Export Recipe as Python" : "Export Recipe as SQL",
            Filter = python ? "Python script (*.py)|*.py" : "DuckDB SQL script (*.sql)|*.sql",
            FileName = baseName + (python ? ".py" : ".sql")
        };
        if (dialog.ShowDialog(owner) != true)
            return;

        try
        {
            var script = python
                ? RecipeExporter.ToPython(_recipe.Steps, sources, DateTime.Now)
                : RecipeExporter.ToDuckDbSql(_recipe.Steps, sources, DateTime.Now);
            await AtomicFile.WriteAllTextAsync(dialog.FileName, script);
            StatusText.Text = $"Exported the recipe to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't export the recipe: {UserFacingError.Describe(ex)}", "Export Recipe",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>The opened files the export reads: every file in the Query Hub, so joins still resolve.</summary>
    private List<RecipeSource> GetRecipeSources()
    {
        if (_notebookSession == null)
            return [];

        return _notebookSession.GetSources()
            .Where(source => source.Kind == NotebookSourceKind.File && !string.IsNullOrEmpty(source.ReaderExpression))
            .Select(source => new RecipeSource(
                source.Alias,
                source.ReaderExpression!,
                source.CanSaveInPlace ? source.FilePath : null,
                source.Format))
            .ToList();
    }
}
