using System.IO;
using System.Linq;
using HipHipParquet.Models;

namespace HipHipParquet.Services;

/// <summary>
/// Detects file formats from extensions and provides DuckDB reader/writer mappings.
/// </summary>
public static class FileFormatDetector
{
    private const string ParquetExtension = ".parquet";
    private const string SnappyParquetSuffix = ".snappy.parquet";

    /// <summary>
    /// Escapes a file path for safe embedding inside DuckDB single-quoted SQL strings.
    /// Single quotes are doubled: O'Reilly.csv → O''Reilly.csv
    /// </summary>
    private static string EscapePath(string filePath)
        => filePath.Replace("'", "''");

    /// <summary>
    /// Quotes a file path for DuckDB SQL, applying single-quote escaping.
    /// </summary>
    private static string QuotePath(string filePath)
        => $"'{EscapePath(filePath)}'";

    /// <summary>
    /// Normalizes file paths for DuckDB reader functions.
    /// </summary>
    private static string NormalizePath(string filePath)
        => filePath.Replace("\\", "/");

    /// <summary>
    /// Detects the file format from the file extension.
    /// </summary>
    public static SupportedFileFormat DetectFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".parquet" or ".pqt" => SupportedFileFormat.Parquet,
            ".csv" => SupportedFileFormat.Csv,
            ".tsv" or ".tab" => SupportedFileFormat.Tsv,
            ".json" or ".jsonl" or ".ndjson" => SupportedFileFormat.Json,
            ".xlsx" or ".xls" => SupportedFileFormat.Excel,
            _ => SupportedFileFormat.Csv // Default to CSV for unknown text files
        };
    }

    /// <summary>
    /// Returns the DuckDB SQL expression to read a file, e.g. "read_csv_auto('/path/file.csv')".
    /// </summary>
    public static string GetDuckDbReaderExpression(string filePath, SupportedFileFormat format)
    {
        return format switch
        {
            SupportedFileFormat.Parquet => GetParquetReaderExpression(filePath),
            SupportedFileFormat.Csv => $"read_csv_auto({QuotePath(filePath)})",
            SupportedFileFormat.Tsv => $"read_csv_auto({QuotePath(filePath)}, delim='\\t')",
            SupportedFileFormat.Json => $"read_json_auto({QuotePath(filePath)})",
            SupportedFileFormat.Excel => $"st_read({QuotePath(filePath)})",
            _ => $"read_csv_auto({QuotePath(filePath)})"
        };
    }

    private static string GetParquetReaderExpression(string selectedPath)
    {
        var inputs = ResolveParquetInputs(selectedPath);
        if (inputs.Count <= 1)
            return $"read_parquet({QuotePath(inputs[0])})";

        var quotedPaths = string.Join(", ", inputs.Select(QuotePath));
        return $"read_parquet([{quotedPaths}])";
    }

    /// <summary>
    /// Resolves parquet inputs, expanding split/snappy parquet parts in the same folder when the selected file looks like a shard.
    /// </summary>
    public static IReadOnlyList<string> ResolveParquetInputs(string selectedPath)
    {
        var normalizedSelectedPath = NormalizePath(selectedPath);
        var directory = Path.GetDirectoryName(selectedPath);
        var fileName = Path.GetFileName(selectedPath);

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
            return [normalizedSelectedPath];

        var candidates = fileName.EndsWith(SnappyParquetSuffix, StringComparison.OrdinalIgnoreCase)
            ? Directory.GetFiles(directory, $"*{SnappyParquetSuffix}")
            : IsPartParquetFileName(fileName)
                ? Directory.GetFiles(directory, "part-*.parquet")
                : GetNumberedSiblingFiles(directory, fileName);

        var resolved = candidates
            .Where(path => path.EndsWith(ParquetExtension, StringComparison.OrdinalIgnoreCase))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();

        return resolved.Count > 0 ? resolved : [normalizedSelectedPath];
    }

    private static bool IsPartParquetFileName(string fileName)
        => fileName.StartsWith("part-", StringComparison.OrdinalIgnoreCase)
           && fileName.EndsWith(ParquetExtension, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetNumberedSiblingFiles(string directory, string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"^(?<prefix>.+?)(?<sep>[-_]?)(?<index>\d+)\.parquet$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
            return [Path.Combine(directory, fileName)];

        var prefix = System.Text.RegularExpressions.Regex.Escape(match.Groups["prefix"].Value);
        var sep = System.Text.RegularExpressions.Regex.Escape(match.Groups["sep"].Value);
        var pattern = new System.Text.RegularExpressions.Regex(
            $"^{prefix}{sep}\\d+\\.parquet$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return Directory.EnumerateFiles(directory, "*.parquet")
            .Where(path => pattern.IsMatch(Path.GetFileName(path)));
    }

    /// <summary>
    /// Returns the DuckDB SQL expression with user-specified CSV import options.
    /// </summary>
    public static string GetDuckDbReaderExpression(string filePath, SupportedFileFormat format, CsvImportOptions? csvOptions)
    {
        // Only CSV/TSV formats use custom options; all other formats fall through to the base overload
        if (csvOptions != null && (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv))
        {
            var p = EscapePath(filePath);
            var opts = csvOptions.ToDuckDbOptions();
            return $"read_csv_auto('{p}'{opts})";
        }

        return GetDuckDbReaderExpression(filePath, format);
    }

    /// <summary>
    /// Returns the DuckDB SQL expression with user-specified JSON import options.
    /// </summary>
    public static string GetDuckDbReaderExpression(string filePath, SupportedFileFormat format, JsonImportOptions? jsonOptions)
    {
        if (jsonOptions != null && format == SupportedFileFormat.Json)
        {
            var p = EscapePath(filePath);
            var opts = jsonOptions.ToDuckDbOptions();
            return $"read_json_auto('{p}'{opts})";
        }

        return GetDuckDbReaderExpression(filePath, format);
    }

    /// <summary>
    /// Returns the DuckDB SQL expression using whichever import options are provided.
    /// Dispatches to CSV or JSON overloads based on which options are non-null.
    /// </summary>
    public static string GetDuckDbReaderExpression(string filePath, SupportedFileFormat format,
        CsvImportOptions? csvOptions, JsonImportOptions? jsonOptions)
    {
        if (jsonOptions != null && format == SupportedFileFormat.Json)
            return GetDuckDbReaderExpression(filePath, format, jsonOptions);

        return GetDuckDbReaderExpression(filePath, format, csvOptions);
    }

    /// <summary>
    /// Returns the DuckDB COPY FORMAT keyword for the given format.
    /// </summary>
    public static string GetDuckDbExportFormat(SupportedFileFormat format) => format switch
    {
        SupportedFileFormat.Parquet => "PARQUET",
        SupportedFileFormat.Csv => "CSV",
        SupportedFileFormat.Tsv => "CSV",
        SupportedFileFormat.Json => "JSON",
        _ => "PARQUET"
    };

    /// <summary>
    /// Returns additional DuckDB COPY options for the format (e.g., TSV delimiter).
    /// </summary>
    public static string GetDuckDbExportOptions(SupportedFileFormat format) => format switch
    {
        SupportedFileFormat.Tsv => ", DELIMITER '\\t', HEADER",
        SupportedFileFormat.Csv => ", HEADER",
        _ => ""
    };

    /// <summary>
    /// Human-readable display name for the format.
    /// </summary>
    public static string GetFormatDisplayName(SupportedFileFormat format) => format switch
    {
        SupportedFileFormat.Parquet => "Parquet",
        SupportedFileFormat.Csv => "CSV",
        SupportedFileFormat.Tsv => "TSV",
        SupportedFileFormat.Json => "JSON",
        SupportedFileFormat.Excel => "Excel",
        _ => "Unknown"
    };

    /// <summary>
    /// Returns a (Background, Foreground) hex color pair for the format badge.
    /// </summary>
    public static (string Background, string Foreground) GetFormatBadgeColors(SupportedFileFormat format) => format switch
    {
        SupportedFileFormat.Parquet => ("#E8F5E9", "#2E7D32"),  // Green
        SupportedFileFormat.Csv     => ("#E3F2FD", "#1565C0"),  // Blue
        SupportedFileFormat.Tsv     => ("#E1F5FE", "#0277BD"),  // Light blue
        SupportedFileFormat.Json    => ("#FFF3E0", "#E65100"),  // Orange
        SupportedFileFormat.Excel   => ("#E8F5E9", "#1B5E20"),  // Dark green
        _                           => ("#F5F5F5", "#616161")   // Grey
    };

    /// <summary>
    /// Returns true when the file extension is not a well-known data format,
    /// meaning it was auto-mapped to CSV by default.
    /// </summary>
    public static bool IsUnknownExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".parquet" or ".pqt" => false,
            ".csv" => false,
            ".tsv" or ".tab" => false,
            ".json" or ".jsonl" or ".ndjson" => false,
            ".xlsx" or ".xls" => false,
            _ => true
        };
    }

    /// <summary>
    /// File dialog filter string for opening files.
    /// </summary>
    public static string GetOpenFileDialogFilter()
    {
        return "All supported files (*.parquet;*.csv;*.tsv;*.json;*.jsonl;*.xlsx)|*.parquet;*.csv;*.tsv;*.json;*.jsonl;*.xlsx|" +
               "Parquet files (*.parquet)|*.parquet|" +
               "CSV files (*.csv)|*.csv|" +
               "TSV files (*.tsv;*.tab)|*.tsv;*.tab|" +
               "JSON files (*.json;*.jsonl)|*.json;*.jsonl|" +
               "Excel files (*.xlsx)|*.xlsx|" +
               "All files (*.*)|*.*";
    }

    /// <summary>
    /// File dialog filter string for saving files.
    /// </summary>
    public static string GetSaveFileDialogFilter()
    {
        return "Parquet files (*.parquet)|*.parquet|" +
               "CSV files (*.csv)|*.csv|" +
               "TSV files (*.tsv)|*.tsv|" +
               "JSON files (*.json)|*.json|" +
               "All files (*.*)|*.*";
    }
}
