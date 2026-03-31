using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DuckDB.NET.Data;
using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Views;

/// <summary>
/// Unified import dialog for CSV/TSV and JSON files.
/// Displays format-specific options, a live data preview, error diagnostics with
/// suggested fixes, and action buttons to retry or import with current settings.
/// </summary>
public partial class FileImportDialog : Window
{
    // ── Public result properties ──────────────────────────────────────────
    /// <summary>CSV/TSV options chosen by the user. Null if format is JSON or user cancelled.</summary>
    public CsvImportOptions? CsvResult { get; private set; }

    /// <summary>JSON options chosen by the user. Null if format is CSV/TSV or user cancelled.</summary>
    public JsonImportOptions? JsonResult { get; private set; }

    /// <summary>True when the user clicked Import (DialogResult == true).</summary>
    public bool Imported => DialogResult == true;

    // ── Private state ────────────────────────────────────────────────────
    private string _filePath = string.Empty;
    private SupportedFileFormat _format;
    private bool _isInitialized;
    private bool _suppressRefresh;         // True while resetting controls en-masse to avoid N preview kicks
    private CancellationTokenSource? _previewCts;

    public FileImportDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { _isInitialized = true; RefreshPreview(); };
        Closed += (_, _) => { _previewCts?.Cancel(); _previewCts?.Dispose(); };
    }

    // ── Public configuration methods ─────────────────────────────────────

    /// <summary>
    /// Initializes the dialog for the given file. Call before ShowDialog().
    /// </summary>
    public void SetFile(string filePath, SupportedFileFormat format)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("File path must not be null or empty.", nameof(filePath));

        _filePath = filePath;
        _format = format;

        // Update header
        FilePathText.Text = filePath;
        var displayName = FileFormatDetector.GetFormatDisplayName(format);
        FormatBadgeText.Text = displayName;
        HeaderTitle.Text = $"Configure {displayName} Import Settings";
        Title = $"Import {displayName} File";

        var (bg, fg) = FileFormatDetector.GetFormatBadgeColors(format);
        // ConvertFromString throws FormatException on invalid hex; guard defensively even though
        // GetFormatBadgeColors returns hardcoded constants.
        try
        {
            FormatBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
            FormatBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        }
        catch (FormatException) { /* keep default colors */ }

        // Show/hide the correct settings panel
        bool isCsv = format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv;
        CsvSettingsPanel.Visibility = isCsv ? Visibility.Visible : Visibility.Collapsed;
        JsonSettingsPanel.Visibility = !isCsv ? Visibility.Visible : Visibility.Collapsed;

        // Pre-select TSV delimiter
        if (format == SupportedFileFormat.Tsv)
            SelectComboByTag(DelimiterCombo, "tab");

        // If the dialog is already showing (e.g. SetFile called again), refresh the preview immediately
        if (_isInitialized)
            RefreshPreview();
    }

    /// <summary>
    /// Pre-populates CSV options (used when re-opening the dialog for an already-loaded file).
    /// Suppresses preview refreshes while bulk-setting controls.
    /// </summary>
    public void PrePopulateCsvOptions(CsvImportOptions options)
    {
        _suppressRefresh = true;
        try
        {
            SelectComboByTag(DelimiterCombo, options.Delimiter);
            HasHeaderCheckBox.IsChecked = options.HasHeader;
            SelectComboByTag(QuoteCharCombo, options.QuoteChar);
            SelectComboByTag(EncodingCombo, options.Encoding);
            SkipRowsTextBox.Text = options.SkipRows.ToString();
            CsvIgnoreErrorsCheckBox.IsChecked = options.IgnoreErrors;
            CsvNullPaddingCheckBox.IsChecked = options.NullPadding;
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    /// <summary>
    /// Pre-populates JSON options (used when re-opening the dialog for an already-loaded file).
    /// Suppresses preview refreshes while bulk-setting controls.
    /// </summary>
    public void PrePopulateJsonOptions(JsonImportOptions options)
    {
        _suppressRefresh = true;
        try
        {
            SelectComboByTag(JsonFormatCombo, options.Format);
            SelectComboByTag(JsonRecordsCombo, options.Records);
            JsonAutoDetectCheckBox.IsChecked = options.AutoDetect;
            JsonMaxDepthTextBox.Text = options.MaxDepth.ToString();
            JsonSampleSizeTextBox.Text = options.SampleSize.ToString();
            JsonIgnoreErrorsCheckBox.IsChecked = options.IgnoreErrors;
            SelectComboByTag(JsonDateFormatCombo, options.DateFormat);
            SelectComboByTag(JsonTimestampFormatCombo, options.TimestampFormat);
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    // ── ComboBox helpers ─────────────────────────────────────────────────

    /// <summary>Selects the ComboBox item whose Tag matches <paramref name="tag"/>. No-op if not found.</summary>
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if ((combo.Items[i] as ComboBoxItem)?.Tag?.ToString() == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Returns the Tag string of the currently selected ComboBox item, or <paramref name="defaultValue"/> if none.</summary>
    private static string GetSelectedTag(ComboBox combo, string defaultValue)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? defaultValue;

    // ── Options builders ─────────────────────────────────────────────────

    private CsvImportOptions BuildCsvOptions()
    {
        int.TryParse(SkipRowsTextBox.Text, out var skipRows);
        return new CsvImportOptions
        {
            Delimiter    = GetSelectedTag(DelimiterCombo, "auto"),
            HasHeader    = HasHeaderCheckBox.IsChecked == true,
            QuoteChar    = GetSelectedTag(QuoteCharCombo, "\""),
            Encoding     = GetSelectedTag(EncodingCombo, "auto"),
            SkipRows     = Math.Max(0, skipRows),
            IgnoreErrors = CsvIgnoreErrorsCheckBox.IsChecked == true,
            NullPadding  = CsvNullPaddingCheckBox.IsChecked == true,
        };
    }

    private JsonImportOptions BuildJsonOptions()
    {
        if (!int.TryParse(JsonMaxDepthTextBox.Text, out var maxDepth)) maxDepth = -1;
        if (!int.TryParse(JsonSampleSizeTextBox.Text, out var sampleSize)) sampleSize = -1;
        return new JsonImportOptions
        {
            Format          = GetSelectedTag(JsonFormatCombo, "auto"),
            Records         = GetSelectedTag(JsonRecordsCombo, "auto"),
            AutoDetect      = JsonAutoDetectCheckBox.IsChecked == true,
            MaxDepth        = maxDepth,
            SampleSize      = sampleSize,
            DateFormat      = GetSelectedTag(JsonDateFormatCombo, "auto"),
            TimestampFormat = GetSelectedTag(JsonTimestampFormatCombo, "auto"),
            IgnoreErrors    = JsonIgnoreErrorsCheckBox.IsChecked == true,
        };
    }

    // ── Preview ──────────────────────────────────────────────────────────

    private async void RefreshPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        if (string.IsNullOrEmpty(_filePath))
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = "No file selected.";
            PreviewGrid.Visibility = Visibility.Collapsed;
            PreviewLoading.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            // Show loading state
            PreviewLoading.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewGrid.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            PreviewStatusText.Text = "Reading file...";

            // Debounce 400ms
            await Task.Delay(400, ct);

            var csvOpts = (_format == SupportedFileFormat.Csv || _format == SupportedFileFormat.Tsv)
                ? BuildCsvOptions() : null;
            using var transcodeScope = await Task.Run(() => FileFormatDetector.PrepareFilePath(_filePath, csvOpts), ct);
            var normalizedPath = transcodeScope.FilePath.Replace("\\", "/");
            var readerExpr = csvOpts != null
                ? FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, _format, csvOpts)
                : FileFormatDetector.GetDuckDbReaderExpression(normalizedPath, _format, BuildJsonOptions());
            var sql = $"SELECT * FROM {readerExpr} LIMIT 100";

            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            ct.ThrowIfCancellationRequested();

            using var cmd = new DuckDBCommand(sql, connection);
            using var reader = await cmd.ExecuteReaderAsync();
            ct.ThrowIfCancellationRequested();

            var dt = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var colName = reader.GetName(i);
                var colType = reader.GetFieldType(i)?.Name ?? "String";
                dt.Columns.Add($"{colName}\n({colType})", typeof(string));
            }

            int rowCount = 0;
            while (await reader.ReadAsync())
            {
                var row = dt.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? "(null)" : reader.GetValue(i)?.ToString() ?? "";
                dt.Rows.Add(row);
                rowCount++;
            }

            ct.ThrowIfCancellationRequested();

            PreviewGrid.ItemsSource = dt.DefaultView;
            PreviewGrid.Visibility = Visibility.Visible;
            PreviewLoading.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;

            PreviewStatusText.Text = $"Showing {rowCount} preview rows, {dt.Columns.Count} columns";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh — discard silently
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;

            PreviewGrid.Visibility = Visibility.Collapsed;
            PreviewLoading.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewStatusText.Text = "Preview failed — see error below";

            ShowError(ex);
        }
    }

    // ── Error display & suggestions ──────────────────────────────────────

    private void ShowError(Exception ex)
    {
        ErrorPanel.Visibility = Visibility.Visible;

        // Extract and highlight line number from the error if present
        var rawMsg = ex.Message;
        var lineMatch = System.Text.RegularExpressions.Regex.Match(
            rawMsg,
            @"(?:line|row)\s+(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (lineMatch.Success)
        {
            var lineNum = lineMatch.Groups[1].Value;
            ErrorText.Text = $"\u26a0\ufe0f Error at line {lineNum}\n{rawMsg}";
        }
        else
        {
            ErrorText.Text = rawMsg;
        }

        var msg = rawMsg.ToLowerInvariant();

        // Analyze the error and show contextual suggestions
        bool showSkipErrors = false;
        bool showEncoding = false;
        var suggestions = new List<string>();

        if (msg.Contains("csv") || msg.Contains("line") || msg.Contains("column count") ||
            msg.Contains("expected") || msg.Contains("delimiter") || msg.Contains("unterminated") ||
            msg.Contains("value error") || msg.Contains("conversion"))
        {
            showSkipErrors = true;
            suggestions.Add("• Try enabling 'Skip malformed rows' to ignore problematic lines");
        }

        // Detect encoding-related errors broadly: DuckDB may report them as invalid UTF-8
        // byte sequences, conversion failures, or general "invalid" byte errors.
        // Windows CSV exports often contain bytes like 0x96 (en dash) or 0x97 (em dash)
        // that are valid Windows-1252 but not valid UTF-8 or Latin-1.
        bool looksLikeEncodingError =
            (msg.Contains("utf") || msg.Contains("encoding") || msg.Contains("encode")) ||
            (msg.Contains("invalid") && (msg.Contains("byte") || msg.Contains("sequence") || msg.Contains("character"))) ||
            msg.Contains("could not convert") ||
            msg.Contains("byte value");

        if (looksLikeEncodingError)
        {
            showEncoding = true;
            suggestions.Add("• The file may use Windows-1252 encoding (common for Excel CSV exports) — try switching encoding to Windows-1252");
        }

        if (msg.Contains("delimiter") || msg.Contains("too many columns") || msg.Contains("column count"))
        {
            suggestions.Add("• Try changing the delimiter (the file may use tabs, pipes, or semicolons)");
        }

        if (msg.Contains("header") || msg.Contains("column0") || msg.Contains("column name"))
        {
            suggestions.Add("• Check whether the file truly has a header row");
        }

        if (msg.Contains("json") || msg.Contains("parse") || msg.Contains("unexpected"))
        {
            if (_format == SupportedFileFormat.Json)
            {
                showSkipErrors = true;
                suggestions.Add("• Try changing the JSON format (Array, NDJSON, or Unstructured)");
                suggestions.Add("• Enable 'Skip malformed records' to ignore bad entries");
            }
        }

        if (msg.Contains("maximum_depth") || msg.Contains("depth") || msg.Contains("nested"))
        {
            suggestions.Add("• Try increasing the max nesting depth or set it to -1 (unlimited)");
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add("• Try different import settings and click Refresh to retry");
        }

        ErrorSuggestions.Text = string.Join("\n", suggestions);
        ErrorSuggestions.Visibility = Visibility.Visible;

        // Show quick-fix buttons
        SuggestSkipErrorsButton.Visibility = showSkipErrors ? Visibility.Visible : Visibility.Collapsed;
        SuggestEncodingButton.Visibility = showEncoding && 
            (_format == SupportedFileFormat.Csv || _format == SupportedFileFormat.Tsv) 
            ? Visibility.Visible : Visibility.Collapsed;

        // For JSON files, update the skip errors button text
        if (_format == SupportedFileFormat.Json)
            SuggestSkipErrorsButton.Content = "Enable 'Skip errors' and retry";
    }

    // ── Event handlers ───────────────────────────────────────────────────

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized && !_suppressRefresh) RefreshPreview();
    }

    private void OnOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialized && !_suppressRefresh) RefreshPreview();
    }

    private void OnOptionChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized && !_suppressRefresh) RefreshPreview();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshPreview();
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_format == SupportedFileFormat.Csv || _format == SupportedFileFormat.Tsv)
            CsvResult = BuildCsvOptions();
        else
            JsonResult = BuildJsonOptions();

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CsvResult = null;
        JsonResult = null;
        DialogResult = false;
        Close();
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        // Suppress per-control refreshes while bulk-resetting; fire one consolidated refresh at the end.
        _suppressRefresh = true;
        try
        {
            if (_format == SupportedFileFormat.Csv || _format == SupportedFileFormat.Tsv)
            {
                DelimiterCombo.SelectedIndex = 0;
                QuoteCharCombo.SelectedIndex = 0;
                HasHeaderCheckBox.IsChecked = true;
                SkipRowsTextBox.Text = "0";
                EncodingCombo.SelectedIndex = 0;
                CsvIgnoreErrorsCheckBox.IsChecked = false;
                CsvNullPaddingCheckBox.IsChecked = false;

                if (_format == SupportedFileFormat.Tsv)
                    SelectComboByTag(DelimiterCombo, "tab");
            }
            else
            {
                JsonFormatCombo.SelectedIndex = 0;
                JsonRecordsCombo.SelectedIndex = 0;
                JsonAutoDetectCheckBox.IsChecked = true;
                JsonMaxDepthTextBox.Text = "-1";
                JsonSampleSizeTextBox.Text = "-1";
                JsonDateFormatCombo.SelectedIndex = 0;
                JsonTimestampFormatCombo.SelectedIndex = 0;
                JsonIgnoreErrorsCheckBox.IsChecked = false;
            }
        }
        finally
        {
            _suppressRefresh = false;
            RefreshPreview();
        }
    }

    private void OnSuggestSkipErrors(object sender, RoutedEventArgs e)
    {
        // Suppress individual checkbox-change events; fire one refresh at the end.
        // CheckBox.Checked only fires when the value actually changes, so an explicit
        // RefreshPreview() call ensures a retry even if the flag was already set.
        _suppressRefresh = true;
        try
        {
            if (_format == SupportedFileFormat.Csv || _format == SupportedFileFormat.Tsv)
            {
                CsvIgnoreErrorsCheckBox.IsChecked = true;
                CsvNullPaddingCheckBox.IsChecked = true;
            }
            else
            {
                JsonIgnoreErrorsCheckBox.IsChecked = true;
            }
        }
        finally
        {
            _suppressRefresh = false;
            RefreshPreview();
        }
    }

    private void OnSuggestEncoding(object sender, RoutedEventArgs e)
    {
        // Windows CSV exports (Excel, Notepad, etc.) typically use Windows-1252, which
        // covers characters like en dash (0x96), em dash (0x97), and curly quotes that
        // are invalid in UTF-8 and unmapped in Latin-1.  Try Windows-1252 first; if the
        // preview still fails the user can manually switch to Latin-1.
        SelectComboByTag(EncodingCombo, "windows-1252");
        // OnOptionChanged fires from SelectionChanged and will trigger a refresh,
        // but call explicitly in case the encoding was already set to windows-1252.
        RefreshPreview();
    }

    private void OnCopyErrorClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ErrorText.Text))
        {
            try
            {
                System.Windows.Clipboard.SetText(ErrorText.Text);
                CopyErrorButton.Content = "✓ Copied";
                
                // Revert button text after 1.5 seconds using DispatcherTimer (UI thread-safe)
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                timer.Tick += (_, _) =>
                {
                    CopyErrorButton.Content = "📋 Copy";
                    timer.Stop();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                // Keep dialog stable and inform user of clipboard failure
                CopyErrorButton.Content = "Copy failed";
                MessageBox.Show(
                    $"Failed to copy error details to clipboard: {ex.Message}\n\nPlease try again or manually select and copy the error text.",
                    "Clipboard Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void OnDismissError(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
    }
}
