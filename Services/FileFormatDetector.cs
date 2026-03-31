using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HipHipParquet.Models;

namespace HipHipParquet.Services;

/// <summary>
/// Detects file formats from extensions and provides DuckDB reader/writer mappings.
/// </summary>
public static class FileFormatDetector
{
    static FileFormatDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
        => $"'{EscapePath(NormalizePath(filePath))}'";

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

        try
        {
            IEnumerable<string> candidates;

            if (fileName.EndsWith(SnappyParquetSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var shardPrefix = GetSnappyShardPrefix(fileName);
                if (string.IsNullOrEmpty(shardPrefix))
                    return [normalizedSelectedPath];

                var shardPattern = "^" + Regex.Escape(shardPrefix) + @"\d+" + Regex.Escape(SnappyParquetSuffix) + "$";
                var shardRegex = new Regex(shardPattern, RegexOptions.IgnoreCase);

                candidates = Directory
                    .GetFiles(directory, $"*{SnappyParquetSuffix}")
                    .Where(path =>
                    {
                        var file = Path.GetFileName(path);
                        return shardRegex.IsMatch(file);
                    });
            }
            else if (IsPartParquetFileName(fileName))
            {
                var shardPrefix = GetPartShardPrefix(fileName);
                if (string.IsNullOrEmpty(shardPrefix) || shardPrefix.Equals("part-", StringComparison.OrdinalIgnoreCase))
                    return [normalizedSelectedPath];

                candidates = Directory
                    .GetFiles(directory, "part-*.parquet")
                    .Where(path =>
                    {
                        var withoutExtension = Path.GetFileNameWithoutExtension(path);
                        return withoutExtension.StartsWith(shardPrefix, StringComparison.OrdinalIgnoreCase);
                    });
            }
            else
            {
                candidates = GetNumberedSiblingFiles(directory, fileName);
            }

            var resolved = candidates
                .Where(path => path.EndsWith(ParquetExtension, StringComparison.OrdinalIgnoreCase))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => TryGetParquetNumericIndex(path) ?? int.MaxValue)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return resolved.Count > 0 ? resolved : [normalizedSelectedPath];
        }
        catch (IOException)
        {
            return [normalizedSelectedPath];
        }
        catch (UnauthorizedAccessException)
        {
            return [normalizedSelectedPath];
        }
    }

    private static bool IsPartParquetFileName(string fileName)
        => fileName.StartsWith("part-", StringComparison.OrdinalIgnoreCase)
           && fileName.EndsWith(ParquetExtension, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetNumberedSiblingFiles(string directory, string fileName)
    {
        var match = Regex.Match(
            fileName,
            @"^(?<prefix>.+?)(?<sep>[-_]?)(?<index>\d+)\.parquet$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return [Path.Combine(directory, fileName)];

        var prefix = Regex.Escape(match.Groups["prefix"].Value);
        var sep = Regex.Escape(match.Groups["sep"].Value);
        var pattern = new Regex(
            $"^{prefix}{sep}\\d+\\.parquet$",
            RegexOptions.IgnoreCase);

        return Directory.EnumerateFiles(directory, "*.parquet")
            .Where(path => pattern.IsMatch(Path.GetFileName(path)));
    }

    /// <summary>
    /// Attempts to parse a numeric shard index from a parquet filename, used to order numbered shards naturally (e.g. 1, 2, 10).
    /// </summary>
    private static int? TryGetParquetNumericIndex(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
            return null;

        // Capture a trailing run of digits before ".parquet" or ".snappy.parquet"
        var match = Regex.Match(
            fileName,
            @"(?<index>\d+)(?=(?:\.snappy)?\.parquet$)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        return int.TryParse(match.Groups["index"].Value, out var index) ? index : null;
    }

    private static string GetSnappyShardPrefix(string fileName)
    {
        // Derive a shard-group prefix from a *.snappy.parquet file name by stripping the suffix
        // and removing any numeric shard suffix (e.g. "dataset-2" -> "dataset-").
        if (!fileName.EndsWith(SnappyParquetSuffix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var baseName = fileName[..^SnappyParquetSuffix.Length];
        if (string.IsNullOrWhiteSpace(baseName))
            return string.Empty;

        var match = Regex.Match(baseName, @"^(?<prefix>.+?)(?<sep>[-_])(?<index>\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return string.Empty;

        return match.Groups["prefix"].Value + match.Groups["sep"].Value;
    }

    private static string GetPartShardPrefix(string fileName)
    {
        // Derive a shard-group prefix from a part-*.parquet file name.
        // Example: "part-00000-abc.parquet" -> "part-00000-"
        if (!IsPartParquetFileName(fileName))
            return string.Empty;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
            return string.Empty;

        var lastDashIndex = nameWithoutExtension.LastIndexOf('-', nameWithoutExtension.Length - 1);
        if (lastDashIndex <= 0)
            return string.Empty;

        var prefix = nameWithoutExtension[..(lastDashIndex + 1)];
        return string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix;
    }

    /// <summary>
    /// Returns the DuckDB SQL expression with user-specified CSV import options.
    /// </summary>
    public static string GetDuckDbReaderExpression(string filePath, SupportedFileFormat format, CsvImportOptions? csvOptions)
    {
        // Only CSV/TSV formats use custom options; all other formats fall through to the base overload
        if (csvOptions != null && (format == SupportedFileFormat.Csv || format == SupportedFileFormat.Tsv))
        {
            var opts = csvOptions.ToDuckDbOptions();
            return $"read_csv_auto({QuotePath(filePath)}{opts})";
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
            var opts = jsonOptions.ToDuckDbOptions();
            return $"read_json_auto({QuotePath(filePath)}{opts})";
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
    /// Sniffs the encoding of a text file by inspecting its leading bytes.
    /// Returns an encoding tag consumed by <see cref="PrepareFilePath"/>:
    /// "utf-8-bom", "utf-16", "windows-1252", "latin1", or "auto".
    /// Windows CSV exports from Excel frequently use Windows-1252, which contains
    /// characters like en dash (0x96) and em dash (0x97) that are invalid in UTF-8.
    /// </summary>
    public static string SniffEncoding(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Check for a byte-order mark first
            var bom = new byte[4];
            int bomRead = stream.Read(bom, 0, 4);
            if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return "utf-8-bom";
            if (bomRead >= 2 && ((bom[0] == 0xFF && bom[1] == 0xFE) || (bom[0] == 0xFE && bom[1] == 0xFF)))
                return "utf-16";

            // Read a sample (up to 8 KB) to detect encoding heuristically
            stream.Seek(0, SeekOrigin.Begin);
            var sample = new byte[8192];
            int sampleLen = stream.Read(sample, 0, sample.Length);

            if (IsValidUtf8(sample, sampleLen))
                return "auto"; // Looks like valid UTF-8; let DuckDB auto-detect

            // Non-UTF-8 content: check for bytes in the Windows-1252–only range (0x80–0x9F).
            // These code points are control characters in ISO-8859-1/Latin-1 but are printable
            // characters in Windows-1252 (e.g. 0x96 = en dash, 0x97 = em dash, 0x93/0x94 = curly quotes).
            for (int i = 0; i < sampleLen; i++)
            {
                if (sample[i] >= 0x80 && sample[i] <= 0x9F)
                    return "windows-1252";
            }

            // Non-UTF-8 but no Windows-1252–specific bytes → plain Latin-1
            return "latin1";
        }
        catch
        {
            return "auto";
        }
    }

    /// <summary>Validates that <paramref name="length"/> bytes of <paramref name="data"/> form valid UTF-8.</summary>
    private static bool IsValidUtf8(byte[] data, int length)
    {
        int i = 0;
        while (i < length)
        {
            byte b = data[i];
            int extra;
            if      (b < 0x80) { i++; continue; }     // ASCII
            else if (b < 0xC2) return false;           // Overlong/invalid lead byte
            else if (b < 0xE0) extra = 1;
            else if (b < 0xF0) extra = 2;
            else if (b < 0xF5) extra = 3;
            else                return false;

            i++;
            for (int j = 0; j < extra; j++, i++)
            {
                if (i >= length || (data[i] & 0xC0) != 0x80)
                    return false;
            }
        }
        return true;
    }

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
        return "All supported files (*.parquet;*.snappy.parquet;*.csv;*.tsv;*.json;*.jsonl;*.xlsx)|*.parquet;*.snappy.parquet;*.csv;*.tsv;*.json;*.jsonl;*.xlsx|" +
               "Parquet files (*.parquet;*.snappy.parquet)|*.parquet;*.snappy.parquet|" +
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

    /// <summary>
    /// If <paramref name="csvOptions"/> specifies a non-UTF-8 encoding, transcodes the source
    /// file to a UTF-8 temp file and returns a <see cref="TranscodeScope"/> whose
    /// <c>FilePath</c> points to that temp file. Otherwise wraps the original path.
    /// Always dispose the returned scope to clean up any temp file.
    /// </summary>
    public static TranscodeScope PrepareFilePath(string filePath, CsvImportOptions? csvOptions)
    {
        var enc = csvOptions?.Encoding;

        // These encodings are either UTF-8 compatible or handled natively by DuckDB.
        if (string.IsNullOrEmpty(enc) || enc == "auto" || enc == "utf-8" || enc == "utf-8-bom")
            return new TranscodeScope(filePath);

        string? tempPath = null;
        try
        {
            var sourceEncoding = enc switch
            {
                "latin1"       => Encoding.GetEncoding("ISO-8859-1"),
                "windows-1252" => Encoding.GetEncoding(1252),
                "utf-16"       => Encoding.Unicode,
                _              => Encoding.GetEncoding(enc)
            };

            tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");

            using var src = new StreamReader(filePath, sourceEncoding);
            // Write UTF-8 without BOM so DuckDB auto-detects it as plain UTF-8.
            using var dst = new StreamWriter(tempPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var buf = new char[65536];
            int charsRead;
            while ((charsRead = src.Read(buf, 0, buf.Length)) > 0)
                dst.Write(buf, 0, charsRead);

            return new TranscodeScope(filePath, tempPath);
        }
        catch
        {
            // Clean up any partially-written temp file before falling back.
            if (tempPath != null)
                try { File.Delete(tempPath); } catch { /* best-effort */ }

            // If transcoding fails for any reason, fall back to the original file.
            return new TranscodeScope(filePath);
        }
    }
}

/// <summary>
/// Manages the lifetime of a UTF-8 temp file produced by <see cref="FileFormatDetector.PrepareFilePath"/>.
/// Use in a <c>using</c> statement; the temp file (if any) is deleted on <see cref="Dispose"/>.
/// </summary>
public sealed class TranscodeScope : IDisposable
{
    /// <summary>Path to pass to DuckDB — the original path or a UTF-8 transcoded copy.</summary>
    public string FilePath { get; }

    private readonly string? _tempPath;

    public TranscodeScope(string originalPath, string? tempPath = null)
    {
        FilePath = tempPath ?? originalPath;
        _tempPath = tempPath;
    }

    public void Dispose()
    {
        if (_tempPath != null)
            try { File.Delete(_tempPath); } catch { /* best-effort */ }
    }
}
