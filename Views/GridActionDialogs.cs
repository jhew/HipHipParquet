using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HipHipParquet.Views;

internal static class GridActionDialogs
{
    internal sealed record ReplaceSelectionOptions(string FindText, string ReplaceText, bool MatchCase);
    internal sealed record RegexCheckOptions(string ColumnName, string Pattern);

    public static ReplaceSelectionOptions? ShowReplaceSelectionDialog(Window owner)
    {
        var findBox = new TextBox { MinWidth = 260, Margin = new Thickness(0, 4, 0, 10) };
        var replaceBox = new TextBox { MinWidth = 260, Margin = new Thickness(0, 4, 0, 10) };
        var matchCaseCheckBox = new CheckBox { Content = "Match case", Margin = new Thickness(0, 0, 0, 10) };

        var dialog = CreateDialog(owner, "Replace in Selection", content =>
        {
            content.Children.Add(new TextBlock { Text = "Find", FontWeight = FontWeights.SemiBold });
            content.Children.Add(findBox);
            content.Children.Add(new TextBlock { Text = "Replace with", FontWeight = FontWeights.SemiBold });
            content.Children.Add(replaceBox);
            content.Children.Add(matchCaseCheckBox);
        });

        var okButton = new Button
        {
            Content = "Replace",
            MinWidth = 90,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true
        };

        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(findBox.Text))
            {
                MessageBox.Show(dialog, "Enter text to find first.", "Replace in Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                findBox.Focus();
                return;
            }

            dialog.DialogResult = true;
        };

        AppendDialogButtons(dialog, okButton, cancelButton);

        if (dialog.ShowDialog() != true)
            return null;

        return new ReplaceSelectionOptions(findBox.Text, replaceBox.Text, matchCaseCheckBox.IsChecked == true);
    }

    public static IReadOnlyList<string>? ShowColumnPickerDialog(
        Window owner,
        string title,
        string description,
        IReadOnlyList<string> columns,
        IReadOnlyList<string>? preselected = null)
    {
        var selectedColumns = new HashSet<string>(preselected ?? [], StringComparer.Ordinal);
        if (selectedColumns.Count == 0 && columns.Count > 0)
            selectedColumns.Add(columns[0]);

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var selectAll = new CheckBox
        {
            Content = "Select all",
            Margin = new Thickness(0, 0, 0, 8),
            IsChecked = selectedColumns.Count == columns.Count
        };

        var checkBoxes = columns
            .Select(columnName => new CheckBox
            {
                Content = columnName,
                Margin = new Thickness(0, 0, 0, 4),
                IsChecked = selectedColumns.Contains(columnName),
                Tag = columnName
            })
            .ToList();

        selectAll.Checked += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
                checkBox.IsChecked = true;
        };
        selectAll.Unchecked += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
                checkBox.IsChecked = false;
        };

        var listPanel = new StackPanel();
        foreach (var checkBox in checkBoxes)
            listPanel.Children.Add(checkBox);

        content.Children.Add(selectAll);
        content.Children.Add(new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260
        });

        var dialog = CreateDialog(owner, title, stack => stack.Children.Add(content));

        var okButton = new Button
        {
            Content = "Apply",
            MinWidth = 90,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true
        };

        okButton.Click += (_, _) =>
        {
            var checkedColumns = checkBoxes
                .Where(checkBox => checkBox.IsChecked == true)
                .Select(checkBox => (string)checkBox.Tag)
                .ToList();

            if (checkedColumns.Count == 0)
            {
                MessageBox.Show(dialog, "Select at least one column.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            dialog.Tag = checkedColumns;
            dialog.DialogResult = true;
        };

        AppendDialogButtons(dialog, okButton, cancelButton);

        return dialog.ShowDialog() == true
            ? dialog.Tag as IReadOnlyList<string>
            : null;
    }

    public static string? ShowSingleTextInputDialog(
        Window owner,
        string title,
        string label,
        string initialValue = "",
        string confirmText = "Apply")
    {
        var inputBox = new TextBox
        {
            Text = initialValue,
            MinWidth = 280,
            Margin = new Thickness(0, 4, 0, 10)
        };

        var dialog = CreateDialog(owner, title, content =>
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(inputBox);
        });

        var okButton = new Button
        {
            Content = confirmText,
            MinWidth = 90,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true
        };

        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(inputBox.Text))
            {
                MessageBox.Show(dialog, "Enter a value first.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                inputBox.Focus();
                return;
            }

            dialog.Tag = inputBox.Text.Trim();
            dialog.DialogResult = true;
        };

        AppendDialogButtons(dialog, okButton, cancelButton);

        if (dialog.ShowDialog() != true)
            return null;

        return dialog.Tag as string;
    }

    public static RegexCheckOptions? ShowRegexCheckDialog(
        Window owner,
        IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
            return null;

        var columnComboBox = new ComboBox
        {
            ItemsSource = columns,
            SelectedIndex = 0,
            Margin = new Thickness(0, 4, 0, 10),
            MinWidth = 280
        };
        var patternBox = new TextBox
        {
            MinWidth = 280,
            Margin = new Thickness(0, 4, 0, 10)
        };

        var dialog = CreateDialog(owner, "Regex Check", content =>
        {
            content.Children.Add(new TextBlock
            {
                Text = "Column",
                FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(columnComboBox);
            content.Children.Add(new TextBlock
            {
                Text = "Regex pattern",
                FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(patternBox);
            content.Children.Add(new TextBlock
            {
                Text = "Example: ^[A-Z]{2}\\d{4}$",
                Foreground = Brushes.DimGray,
                FontSize = 11
            });
        });

        var okButton = new Button
        {
            Content = "Run Check",
            MinWidth = 90,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true
        };

        okButton.Click += (_, _) =>
        {
            if (columnComboBox.SelectedItem is not string columnName)
            {
                MessageBox.Show(dialog, "Choose a column first.", "Regex Check", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(patternBox.Text))
            {
                MessageBox.Show(dialog, "Enter a regex pattern first.", "Regex Check", MessageBoxButton.OK, MessageBoxImage.Information);
                patternBox.Focus();
                return;
            }

            dialog.Tag = new RegexCheckOptions(columnName, patternBox.Text.Trim());
            dialog.DialogResult = true;
        };

        AppendDialogButtons(dialog, okButton, cancelButton);

        return dialog.ShowDialog() == true
            ? dialog.Tag as RegexCheckOptions
            : null;
    }

    private static Window CreateDialog(Window owner, string title, Action<StackPanel> populateContent)
    {
        var contentStack = new StackPanel { Margin = new Thickness(16) };
        populateContent(contentStack);

        var root = new DockPanel();
        DockPanel.SetDock(contentStack, Dock.Top);
        root.Children.Add(contentStack);

        return new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            Content = root,
            MinWidth = 360,
            ShowInTaskbar = false
        };
    }

    private static void AppendDialogButtons(Window dialog, params Button[] buttons)
    {
        if (dialog.Content is not DockPanel root)
            return;

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };

        foreach (var button in buttons)
            buttonPanel.Children.Add(button);

        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        root.Children.Add(buttonPanel);
    }
}
