using System.IO;
using HipHipParquet.Models;

namespace HipHipParquet.Services;

/// <summary>
/// Detects file formats from extensions and provides DuckDB reader/writer mappings.
/// </summary>
public static class FileFormatDetector
{
    /// <summary>
    /// Escapes a file path for safe embedding inside DuckDB single-quoted SQL strings.
    /// Single quotes are doubled: O'Reilly.csv → O''Reilly.csv
    /// </summary>
    private static string EscapePath(string filePath)
        => filePath.Replace("'", "''");

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
        var p = EscapePath(filePath);
        return format switch
        {
            SupportedFileFormat.Parquet => $"read_parquet('{p}')",
            SupportedFileFormat.Csv => $"read_csv_auto('{p}')",
            SupportedFileFormat.Tsv => $"read_csv_auto('{p}', delim='\\t')",
            SupportedFileFormat.Json => $"read_json_auto('{p}')",
            SupportedFileFormat.Excel => $"st_read('{p}')",
            _ => $"read_csv_auto('{p}')"
        };
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
