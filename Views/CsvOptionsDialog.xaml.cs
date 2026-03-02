using System.Data;
using System.Windows;
using System.Windows.Controls;
using DuckDB.NET.Data;
using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Views;

/// <summary>
/// Dialog for configuring CSV/TSV import options (delimiter, header, quote char, encoding, skip rows).
/// Includes a live data preview that updates when options change.
/// </summary>
public partial class CsvOptionsDialog : Window
{
    /// <summary>
    /// The resulting options after the user clicks Apply. Null if cancelled.
    /// </summary>
    public CsvImportOptions? Result { get; private set; }

    private string? _previewFilePath;
    private bool _isInitialized;
    private CancellationTokenSource? _previewCts;

    public CsvOptionsDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { _isInitialized = true; RefreshPreview(); };
        Closed += (_, _) => { _previewCts?.Cancel(); _previewCts?.Dispose(); };
    }

    /// <summary>
    /// Sets the file path used to generate the live preview.
    /// </summary>
    public void SetPreviewFile(string filePath)
    {
        _previewFilePath = filePath;
        if (_isInitialized)
            RefreshPreview();
    }

    /// <summary>
    /// Pre-selects the tab delimiter when opening a TSV file.
    /// </summary>
    public void PreSelectTsv()
    {
        // Select "Tab" in the delimiter combo
        DelimiterCombo.SelectedIndex = 2;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        Result = BuildOptions();
        DialogResult = true;
        Close();
    }

    private void OnUseDefaultsClick(object sender, RoutedEventArgs e)
    {
        // Return auto-detect defaults
        Result = CsvImportOptions.AutoDetect;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized)
            RefreshPreview();
    }

    private void OnOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialized)
            RefreshPreview();
    }

    private void OnOptionChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
            RefreshPreview();
    }

    /// <summary>
    /// Schedules a debounced preview refresh (300 ms). Any in-flight query is cancelled before
    /// starting a new one, so rapid option changes don't stack up DuckDB connections.
    /// </summary>
    private async void RefreshPreview()
    {
        // Cancel and dispose any in-flight preview before starting a new one
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        if (string.IsNullOrEmpty(_previewFilePath))
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewGrid.Visibility = Visibility.Collapsed;
            PreviewError.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            // Debounce: wait 300 ms before querying so rapid UI changes coalesce
            await Task.Delay(300, ct);

            var options = BuildOptions();
            var normalizedPath = _previewFilePath.Replace("\\", "/");
            var format = FileFormatDetector.DetectFormat(_previewFilePath);
            var readerExpr = FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, format, options);

            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            ct.ThrowIfCancellationRequested();

            var sql = $"SELECT * FROM {readerExpr} LIMIT 5";
            using var cmd = new DuckDBCommand(sql, connection);
            using var reader = await cmd.ExecuteReaderAsync();
            ct.ThrowIfCancellationRequested();

            var dt = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++)
                dt.Columns.Add(reader.GetName(i), typeof(string));

            while (await reader.ReadAsync())
            {
                var row = dt.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? "(null)" : reader.GetValue(i)?.ToString() ?? "";
                dt.Rows.Add(row);
            }

            ct.ThrowIfCancellationRequested();

            PreviewGrid.ItemsSource = dt.DefaultView;
            PreviewGrid.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewError.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh request — discard silently
        }
        catch (Exception ex)
        {
            PreviewGrid.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewError.Text = $"Preview error: {ex.Message}";
            PreviewError.Visibility = Visibility.Visible;
        }
    }

    private CsvImportOptions BuildOptions()
    {
        var delimTag = (DelimiterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        var quoteTag = (QuoteCharCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "\"";
        var encodingTag = (EncodingCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

        int.TryParse(SkipRowsTextBox.Text, out var skipRows);

        return new CsvImportOptions
        {
            Delimiter = delimTag,
            HasHeader = HasHeaderCheckBox.IsChecked == true,
            QuoteChar = quoteTag,
            Encoding = encodingTag,
            SkipRows = Math.Max(0, skipRows)
        };
    }
}
