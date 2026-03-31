namespace HipHipParquet.Models;

/// <summary>
/// Represents user-selected CSV/TSV import options passed to DuckDB's read_csv_auto().
/// </summary>
public class CsvImportOptions
{
    /// <summary>Delimiter string: "auto", ",", "tab", ";", "|", " ".</summary>
    public string Delimiter { get; set; } = "auto";

    /// <summary>Whether the first row contains column names.</summary>
    public bool HasHeader { get; set; } = true;

    /// <summary>Quote character: "\"", "'", or "" for none.</summary>
    public string QuoteChar { get; set; } = "\"";

    /// <summary>Encoding: "auto", "utf-8", "utf-8-bom", "latin1", "windows-1252", "utf-16".</summary>
    public string Encoding { get; set; } = "auto";

    /// <summary>Number of rows to skip from the top before reading.</summary>
    public int SkipRows { get; set; } = 0;

    /// <summary>When true, malformed rows are silently skipped instead of aborting the read.</summary>
    public bool IgnoreErrors { get; set; } = false;

    /// <summary>When true, rows with fewer columns than the header are padded with NULLs.</summary>
    public bool NullPadding { get; set; } = false;

    /// <summary>True if all settings are auto-detect (default).</summary>
    public bool IsAutoDetect => Delimiter == "auto" && HasHeader && QuoteChar == "\"" &&
                                 Encoding == "auto" && SkipRows == 0 &&
                                 !IgnoreErrors && !NullPadding;

    /// <summary>Returns the default auto-detect options.</summary>
    public static CsvImportOptions AutoDetect => new();

    /// <summary>
    /// Builds DuckDB read_csv() option parameters from these settings.
    /// Returns something like: ", delim=',', header=true, quote='\"'"
    /// Returns empty string for full auto-detect.
    /// </summary>
    public string ToDuckDbOptions()
    {
        if (IsAutoDetect)
            return string.Empty;

        var parts = new List<string>();

        if (Delimiter != "auto")
        {
            var delim = Delimiter == "tab" ? "\\t" : Delimiter;
            parts.Add($"delim='{delim}'");
        }

        parts.Add($"header={HasHeader.ToString().ToLower()}");

        if (!string.IsNullOrEmpty(QuoteChar))
        {
            var escaped = QuoteChar == "'" ? "''" : QuoteChar;
            parts.Add($"quote='{escaped}'");
        }

        if (SkipRows > 0)
            parts.Add($"skip={SkipRows}");

        // NOTE: DuckDB's read_csv_auto does not accept an 'encoding' parameter.
        // Non-UTF-8 files are handled by FileFormatDetector.PrepareFilePath, which
        // transcodes the file to a UTF-8 temp copy before passing it to DuckDB.

        if (IgnoreErrors)
            parts.Add("ignore_errors=true");

        if (NullPadding)
            parts.Add("null_padding=true");

        return parts.Count > 0 ? ", " + string.Join(", ", parts) : string.Empty;
    }
}
