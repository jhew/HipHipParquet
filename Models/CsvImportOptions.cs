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

    /// <summary>Encoding: "auto", "utf-8", "latin1", "windows-1252".</summary>
    public string Encoding { get; set; } = "auto";

    /// <summary>Number of rows to skip from the top before reading.</summary>
    public int SkipRows { get; set; } = 0;

    /// <summary>True if all settings are auto-detect (default).</summary>
    public bool IsAutoDetect => Delimiter == "auto" && HasHeader && QuoteChar == "\"" && Encoding == "auto" && SkipRows == 0;

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
        {
            parts.Add($"skip={SkipRows}");
        }

        return parts.Count > 0 ? ", " + string.Join(", ", parts) : string.Empty;
    }
}
