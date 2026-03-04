namespace HipHipParquet.Models;

/// <summary>
/// Represents user-selected JSON import options passed to DuckDB's read_json_auto().
/// </summary>
public class JsonImportOptions
{
    /// <summary>JSON format: "auto", "array", "unstructured", "newline_delimited".</summary>
    public string Format { get; set; } = "auto";

    /// <summary>Maximum depth for nested object flattening. -1 means unlimited.</summary>
    public int MaxDepth { get; set; } = -1;

    /// <summary>Whether to auto-detect the JSON schema. When false, all columns become VARCHAR.</summary>
    public bool AutoDetect { get; set; } = true;

    /// <summary>Whether to read JSON records as a single object ({...}) vs array of objects ([{...}]).</summary>
    public string Records { get; set; } = "auto";

    /// <summary>Timestamp format string, or "auto" for auto-detect.</summary>
    public string TimestampFormat { get; set; } = "auto";

    /// <summary>Date format string, or "auto" for auto-detect.</summary>
    public string DateFormat { get; set; } = "auto";

    /// <summary>Sample size for schema detection. -1 means all rows.</summary>
    public int SampleSize { get; set; } = -1;

    /// <summary>Whether to ignore parse errors and skip malformed records.</summary>
    public bool IgnoreErrors { get; set; } = false;

    /// <summary>True if all settings are auto-detect (default).</summary>
    public bool IsAutoDetect => Format == "auto" && MaxDepth == -1 && AutoDetect &&
                                 Records == "auto" && TimestampFormat == "auto" &&
                                 DateFormat == "auto" && SampleSize == -1 && !IgnoreErrors;

    /// <summary>Returns the default auto-detect options.</summary>
    public static JsonImportOptions Default => new();

    /// <summary>
    /// Builds DuckDB read_json() option parameters from these settings.
    /// Returns something like: ", format='array', maximum_depth=2"
    /// Returns empty string for full auto-detect.
    /// </summary>
    public string ToDuckDbOptions()
    {
        if (IsAutoDetect)
            return string.Empty;

        var parts = new List<string>();

        if (Format != "auto")
            parts.Add($"format='{EscapeSql(Format)}'");

        if (MaxDepth >= 0)
            parts.Add($"maximum_depth={MaxDepth}");

        if (!AutoDetect)
            parts.Add("auto_detect=false");

        if (Records != "auto")
            parts.Add($"records='{EscapeSql(Records)}'");

        if (TimestampFormat != "auto")
            parts.Add($"timestampformat='{EscapeSql(TimestampFormat)}'");

        if (DateFormat != "auto")
            parts.Add($"dateformat='{EscapeSql(DateFormat)}'");

        if (SampleSize >= 0)
            parts.Add($"sample_size={SampleSize}");

        if (IgnoreErrors)
            parts.Add("ignore_errors=true");

        return parts.Count > 0 ? ", " + string.Join(", ", parts) : string.Empty;
    }

    /// <summary>Escapes a string value for safe embedding inside a DuckDB single-quoted option argument.</summary>
    private static string EscapeSql(string value) => value.Replace("'", "''");}