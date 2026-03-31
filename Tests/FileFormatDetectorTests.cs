using System;
using System.IO;
using System.Text;
using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class FileFormatDetectorTests
{
    // ── DetectFormat ────────────────────────────────────────────────────

    [Theory]
    [InlineData("data.parquet", SupportedFileFormat.Parquet)]
    [InlineData("data.snappy.parquet", SupportedFileFormat.Parquet)]
    [InlineData("data.pqt", SupportedFileFormat.Parquet)]
    [InlineData("data.csv", SupportedFileFormat.Csv)]
    [InlineData("data.tsv", SupportedFileFormat.Tsv)]
    [InlineData("data.tab", SupportedFileFormat.Tsv)]
    [InlineData("data.json", SupportedFileFormat.Json)]
    [InlineData("data.jsonl", SupportedFileFormat.Json)]
    [InlineData("data.ndjson", SupportedFileFormat.Json)]
    [InlineData("data.xlsx", SupportedFileFormat.Excel)]
    [InlineData("data.xls", SupportedFileFormat.Excel)]
    public void DetectFormat_KnownExtensions_ReturnsCorrectFormat(string file, SupportedFileFormat expected)
    {
        Assert.Equal(expected, FileFormatDetector.DetectFormat(file));
    }

    [Theory]
    [InlineData("data.txt")]
    [InlineData("data.log")]
    [InlineData("data.dat")]
    public void DetectFormat_UnknownExtensions_DefaultToCsv(string file)
    {
        Assert.Equal(SupportedFileFormat.Csv, FileFormatDetector.DetectFormat(file));
    }

    [Theory]
    [InlineData("DATA.CSV", SupportedFileFormat.Csv)]
    [InlineData("DATA.PARQUET", SupportedFileFormat.Parquet)]
    [InlineData("DATA.Json", SupportedFileFormat.Json)]
    public void DetectFormat_CaseInsensitive(string file, SupportedFileFormat expected)
    {
        Assert.Equal(expected, FileFormatDetector.DetectFormat(file));
    }

    // ── GetDuckDbReaderExpression ────────────────────────────────────────

    [Fact]
    public void GetDuckDbReaderExpression_Parquet_ReturnsReadParquet()
    {
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.parquet", SupportedFileFormat.Parquet);
        Assert.Equal("read_parquet('data.parquet')", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_ParquetSplitSnappy_ReturnsListReader()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "dataset-2.snappy.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "dataset-1.snappy.parquet"), "");

        var selected = Path.Combine(tempDir.Path, "dataset-2.snappy.parquet");
        var result = FileFormatDetector.GetDuckDbReaderExpression(selected, SupportedFileFormat.Parquet);

        Assert.Contains("read_parquet([", result);
        Assert.Contains("dataset-1.snappy.parquet", result);
        Assert.Contains("dataset-2.snappy.parquet", result);
        Assert.True(result.IndexOf("dataset-1.snappy.parquet", StringComparison.Ordinal)
            < result.IndexOf("dataset-2.snappy.parquet", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveParquetInputs_SnappyShard_OnlyIncludesSamePrefix()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "dataset-1.snappy.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "dataset-2.snappy.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "other-1.snappy.parquet"), "");

        var selected = Path.Combine(tempDir.Path, "dataset-2.snappy.parquet");
        var resolved = FileFormatDetector.ResolveParquetInputs(selected);

        Assert.Equal(2, resolved.Count);
        Assert.Contains(resolved, p => p.EndsWith("dataset-1.snappy.parquet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolved, p => p.EndsWith("dataset-2.snappy.parquet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resolved, p => p.EndsWith("other-1.snappy.parquet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveParquetInputs_UnrelatedSuffixes_DoNotMix()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "data-1.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "data2-1.parquet"), "");

        var selected = Path.Combine(tempDir.Path, "data-1.parquet");
        var resolved = FileFormatDetector.ResolveParquetInputs(selected);

        Assert.Single(resolved);
        Assert.EndsWith("data-1.parquet", resolved[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveParquetInputs_NumberedSiblings_OrdersNumerically()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "events_10.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "events_2.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "events_1.parquet"), "");

        var selected = Path.Combine(tempDir.Path, "events_10.parquet");
        var resolved = FileFormatDetector.ResolveParquetInputs(selected);

        Assert.Equal(3, resolved.Count);
        Assert.EndsWith("events_1.parquet", resolved[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("events_2.parquet", resolved[1], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("events_10.parquet", resolved[2], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveParquetInputs_PartShard_OnlyIncludesSamePrefix()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "part-00000-aaa.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "part-00000-bbb.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "part-00001-aaa.parquet"), "");

        var selected = Path.Combine(tempDir.Path, "part-00000-bbb.parquet");
        var resolved = FileFormatDetector.ResolveParquetInputs(selected);

        Assert.Equal(2, resolved.Count);
        Assert.Contains(resolved, p => p.EndsWith("part-00000-aaa.parquet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolved, p => p.EndsWith("part-00000-bbb.parquet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resolved, p => p.EndsWith("part-00001-aaa.parquet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetDuckDbReaderExpression_ParquetList_EscapesQuotesInPaths()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "pa'rt-1.parquet"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "pa'rt-2.parquet"), "");

        var selected = Path.Combine(tempDir.Path, "pa'rt-1.parquet");
        var result = FileFormatDetector.GetDuckDbReaderExpression(selected, SupportedFileFormat.Parquet);

        Assert.Contains("read_parquet([", result);
        Assert.Contains("pa''rt-1.parquet", result);
        Assert.Contains("pa''rt-2.parquet", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_Csv_ReturnsReadCsvAuto()
    {
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv);
        Assert.Contains("read_csv_auto", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_Tsv_IncludesTabDelimiter()
    {
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.tsv", SupportedFileFormat.Tsv);
        Assert.Contains("read_csv_auto", result);
        Assert.Contains("delim", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_Json_ReturnsReadJsonAuto()
    {
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.json", SupportedFileFormat.Json);
        Assert.Contains("read_json_auto", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_Excel_ReturnsStRead()
    {
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.xlsx", SupportedFileFormat.Excel);
        Assert.Contains("st_read", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_WithCsvOptions_IncludesOptions()
    {
        var options = new CsvImportOptions
        {
            Delimiter = ",",
            HasHeader = true,
            QuoteChar = "\"",
            SkipRows = 2
        };
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv, options);
        Assert.Contains("delim=','", result);
        Assert.Contains("skip=2", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_WithAutoDetectCsvOptions_NoExtraParams()
    {
        var options = CsvImportOptions.AutoDetect;
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv, options);
        // Auto-detect should produce the same as no options
        var basicResult = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv);
        Assert.Equal(basicResult, result);
    }

    // ── GetDuckDbExportFormat ────────────────────────────────────────────

    [Theory]
    [InlineData(SupportedFileFormat.Parquet, "PARQUET")]
    [InlineData(SupportedFileFormat.Csv, "CSV")]
    [InlineData(SupportedFileFormat.Tsv, "CSV")]
    [InlineData(SupportedFileFormat.Json, "JSON")]
    public void GetDuckDbExportFormat_ReturnsCorrectFormat(SupportedFileFormat format, string expected)
    {
        Assert.Equal(expected, FileFormatDetector.GetDuckDbExportFormat(format));
    }

    [Fact]
    public void GetDuckDbExportOptions_Tsv_IncludesTabDelimiter()
    {
        var options = FileFormatDetector.GetDuckDbExportOptions(SupportedFileFormat.Tsv);
        Assert.Contains("\\t", options);
        Assert.Contains("HEADER", options);
    }

    [Fact]
    public void GetDuckDbExportOptions_Csv_IncludesHeader()
    {
        var options = FileFormatDetector.GetDuckDbExportOptions(SupportedFileFormat.Csv);
        Assert.Contains("HEADER", options);
    }

    [Fact]
    public void GetDuckDbExportOptions_Parquet_ReturnsEmpty()
    {
        Assert.Equal("", FileFormatDetector.GetDuckDbExportOptions(SupportedFileFormat.Parquet));
    }

    // ── GetFormatDisplayName ────────────────────────────────────────────

    [Theory]
    [InlineData(SupportedFileFormat.Parquet, "Parquet")]
    [InlineData(SupportedFileFormat.Csv, "CSV")]
    [InlineData(SupportedFileFormat.Tsv, "TSV")]
    [InlineData(SupportedFileFormat.Json, "JSON")]
    [InlineData(SupportedFileFormat.Excel, "Excel")]
    public void GetFormatDisplayName_ReturnsHumanReadableName(SupportedFileFormat format, string expected)
    {
        Assert.Equal(expected, FileFormatDetector.GetFormatDisplayName(format));
    }

    // ── GetFormatBadgeColors ────────────────────────────────────────────

    [Fact]
    public void GetFormatBadgeColors_AllFormats_ReturnValidHexColors()
    {
        foreach (SupportedFileFormat format in Enum.GetValues<SupportedFileFormat>())
        {
            var (bg, fg) = FileFormatDetector.GetFormatBadgeColors(format);
            Assert.StartsWith("#", bg);
            Assert.StartsWith("#", fg);
            Assert.True(bg.Length == 7, $"Background color for {format} should be 7-char hex");
            Assert.True(fg.Length == 7, $"Foreground color for {format} should be 7-char hex");
        }
    }

    // ── IsUnknownExtension ──────────────────────────────────────────────

    [Theory]
    [InlineData("data.csv", false)]
    [InlineData("data.parquet", false)]
    [InlineData("data.snappy.parquet", false)]
    [InlineData("data.json", false)]
    [InlineData("data.xlsx", false)]
    [InlineData("data.tsv", false)]
    [InlineData("data.txt", true)]
    [InlineData("data.log", true)]
    [InlineData("data.dat", true)]
    [InlineData("data.xml", true)]
    public void IsUnknownExtension_ClassifiesCorrectly(string file, bool expected)
    {
        Assert.Equal(expected, FileFormatDetector.IsUnknownExtension(file));
    }

    // ── Dialog Filters ──────────────────────────────────────────────────

    [Fact]
    public void GetOpenFileDialogFilter_ContainsAllFormats()
    {
        var filter = FileFormatDetector.GetOpenFileDialogFilter();
        Assert.Contains("*.parquet", filter);
        Assert.Contains("*.snappy.parquet", filter);
        Assert.Contains("*.csv", filter);
        Assert.Contains("*.tsv", filter);
        Assert.Contains("*.json", filter);
        Assert.Contains("*.xlsx", filter);
        Assert.Contains("All supported files (*.parquet;*.snappy.parquet;", filter);
        Assert.Contains("Parquet files (*.parquet;*.snappy.parquet)", filter);
    }

    [Fact]
    public void GetSaveFileDialogFilter_ContainsExportFormats()
    {
        var filter = FileFormatDetector.GetSaveFileDialogFilter();
        Assert.Contains("*.parquet", filter);
        Assert.Contains("*.csv", filter);
        Assert.Contains("*.tsv", filter);
        Assert.Contains("*.json", filter);
        // Excel is not supported for save (no write support)
        Assert.DoesNotContain("*.xlsx", filter);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hiphipparquet-tests", Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup: ignore transient I/O errors (e.g., file locks).
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup: ignore permission errors.
            }
        }
    }
}

public class SniffEncodingTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string WriteTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void SniffEncoding_PlainAscii_ReturnsAuto()
    {
        var path = WriteTempFile("hello,world\n1,2"u8.ToArray());
        Assert.Equal("auto", FileFormatDetector.SniffEncoding(path));
    }

    [Fact]
    public void SniffEncoding_Utf8Bom_ReturnsUtf8Bom()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var content = "hello"u8.ToArray();
        var path = WriteTempFile(bom.Concat(content).ToArray());
        Assert.Equal("utf-8-bom", FileFormatDetector.SniffEncoding(path));
    }

    [Fact]
    public void SniffEncoding_Utf16LeBom_ReturnsUtf16()
    {
        var bom = new byte[] { 0xFF, 0xFE };
        var content = Encoding.Unicode.GetBytes("hello");
        var path = WriteTempFile(bom.Concat(content).ToArray());
        Assert.Equal("utf-16", FileFormatDetector.SniffEncoding(path));
    }

    [Fact]
    public void SniffEncoding_Utf16BeBom_ReturnsUtf16()
    {
        var bom = new byte[] { 0xFE, 0xFF };
        var content = Encoding.BigEndianUnicode.GetBytes("hello");
        var path = WriteTempFile(bom.Concat(content).ToArray());
        Assert.Equal("utf-16", FileFormatDetector.SniffEncoding(path));
    }

    [Fact]
    public void SniffEncoding_Windows1252EmDash_ReturnsWindows1252()
    {
        // 0x97 = em dash in Windows-1252, invalid in UTF-8
        var content = new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x97, 0x77, 0x6F, 0x72, 0x6C, 0x64 };
        var path = WriteTempFile(content);
        Assert.Equal("windows-1252", FileFormatDetector.SniffEncoding(path));
    }

    [Fact]
    public void SniffEncoding_Latin1HighBytes_ReturnsLatin1()
    {
        // 0xE9 = 'é' in Latin-1, outside 0x80-0x9F range, not valid standalone UTF-8
        var content = new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0xE9 };
        var path = WriteTempFile(content);
        Assert.Equal("latin1", FileFormatDetector.SniffEncoding(path));
    }

    [Fact]
    public void SniffEncoding_ValidUtf8MultiByteChars_ReturnsAuto()
    {
        // UTF-8 encoded 'é' (U+00E9) = 0xC3 0xA9
        var content = new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0xC3, 0xA9 };
        var path = WriteTempFile(content);
        Assert.Equal("auto", FileFormatDetector.SniffEncoding(path));
    }

    [Fact]
    public void SniffEncoding_NonExistentFile_ReturnsAuto()
    {
        Assert.Equal("auto", FileFormatDetector.SniffEncoding(@"C:\nonexistent_file_12345.csv"));
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }
}

public class PrepareFilePathTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string WriteTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void PrepareFilePath_AutoEncoding_ReturnsOriginalPath()
    {
        var path = WriteTempFile("hello"u8.ToArray());
        using var scope = FileFormatDetector.PrepareFilePath(path, new CsvImportOptions { Encoding = "auto" });
        Assert.Equal(path, scope.FilePath);
    }

    [Fact]
    public void PrepareFilePath_NullOptions_ReturnsOriginalPath()
    {
        var path = WriteTempFile("hello"u8.ToArray());
        using var scope = FileFormatDetector.PrepareFilePath(path, null);
        Assert.Equal(path, scope.FilePath);
    }

    [Fact]
    public void PrepareFilePath_Windows1252_TranscodesToValidUtf8()
    {
        // Write Windows-1252 content: "hello" + em dash (0x97) + "world"
        var content = new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x97, 0x77, 0x6F, 0x72, 0x6C, 0x64 };
        var path = WriteTempFile(content);

        using var scope = FileFormatDetector.PrepareFilePath(path, new CsvImportOptions { Encoding = "windows-1252" });

        // Should have created a different (temp) file
        Assert.NotEqual(path, scope.FilePath);
        Assert.True(File.Exists(scope.FilePath));

        // Temp file should be valid UTF-8 containing the em dash (U+2014 = E2 80 94)
        var transcoded = File.ReadAllText(scope.FilePath, Encoding.UTF8);
        Assert.Contains("\u2014", transcoded); // em dash
    }

    [Fact]
    public void PrepareFilePath_Latin1_TranscodesToValidUtf8()
    {
        // 0xE9 = 'é' in Latin-1
        var content = new byte[] { 0x63, 0x61, 0x66, 0xE9 }; // "café"
        var path = WriteTempFile(content);

        using var scope = FileFormatDetector.PrepareFilePath(path, new CsvImportOptions { Encoding = "latin1" });
        Assert.NotEqual(path, scope.FilePath);
        var transcoded = File.ReadAllText(scope.FilePath, Encoding.UTF8);
        Assert.Equal("caf\u00E9", transcoded);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }
}

public class TranscodeScopeTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, "test");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Dispose_DeletesTempFile()
    {
        var tempPath = CreateTempFile();
        Assert.True(File.Exists(tempPath));

        var scope = new TranscodeScope(tempPath, tempPath);
        scope.Dispose();

        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void Dispose_NoTempFile_DoesNothing()
    {
        var originalPath = @"C:\some\original.csv";
        var scope = new TranscodeScope(originalPath);
        scope.Dispose(); // Should not throw
    }

    [Fact]
    public void FilePath_WithTempPath_ReturnsTempPath()
    {
        var originalPath = @"C:\original.csv";
        var tempPath = @"C:\temp.csv";
        var scope = new TranscodeScope(originalPath, tempPath);
        Assert.Equal(tempPath, scope.FilePath);
    }

    [Fact]
    public void FilePath_WithoutTempPath_ReturnsOriginalPath()
    {
        var originalPath = @"C:\original.csv";
        var scope = new TranscodeScope(originalPath);
        Assert.Equal(originalPath, scope.FilePath);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }
}
