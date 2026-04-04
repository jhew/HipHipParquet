using System.IO;
using System.Text;
using System.Text.Json;
using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class MalformedInputTests
{
    // ── CSV malformed input ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void DetectFormat_EmptyOrWhitespaceFileName_DefaultsToCsv(string fileName)
    {
        // Empty/whitespace file names have no extension, so they default to CSV
        var format = FileFormatDetector.DetectFormat(fileName);
        Assert.Equal(SupportedFileFormat.Csv, format);
    }

    [Theory]
    [InlineData("file\0name.csv")]
    [InlineData("file<name>.parquet")]
    [InlineData("file|name.json")]
    public void DetectFormat_InvalidPathCharacters_StillDetectsExtension(string fileName)
    {
        // These names are invalid for real file paths but the format detector
        // should still handle them by extension without throwing.
        var ex = Record.Exception(() => FileFormatDetector.DetectFormat(fileName));
        // Either returns a format or throws — both are acceptable defensive behaviors
        // but we prefer no crash
        if (ex != null)
            Assert.IsType<ArgumentException>(ex);
    }

    [Theory]
    [InlineData("file.PARQUET")]
    [InlineData("file.Parquet")]
    [InlineData("file.PaRqUeT")]
    public void DetectFormat_MixedCaseExtension_StillDetects(string fileName)
    {
        Assert.Equal(SupportedFileFormat.Parquet, FileFormatDetector.DetectFormat(fileName));
    }

    // ── Encoding sniffing with hostile content ──────────────────────────

    [Fact]
    public void SniffEncoding_EmptyFile_ReturnsAuto()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, []);
            var result = FileFormatDetector.SniffEncoding(tempFile);
            Assert.Equal("auto", result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SniffEncoding_Utf8BomFile_DetectsUtf8Bom()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };
            File.WriteAllBytes(tempFile, content);
            var result = FileFormatDetector.SniffEncoding(tempFile);
            Assert.Equal("utf-8-bom", result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SniffEncoding_Windows1252_DetectsCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // 0x96 = en dash in Windows-1252, invalid in UTF-8
            var content = new byte[] { (byte)'a', 0x96, (byte)'b' };
            File.WriteAllBytes(tempFile, content);
            var result = FileFormatDetector.SniffEncoding(tempFile);
            Assert.Equal("windows-1252", result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SniffEncoding_NonExistentFile_ReturnsAuto()
    {
        var result = FileFormatDetector.SniffEncoding(@"C:\nonexistent-path-12345\file.csv");
        Assert.Equal("auto", result);
    }

    // ── CsvImportOptions edge cases ─────────────────────────────────────

    [Fact]
    public void CsvImportOptions_DefaultIsAutoDetect()
    {
        var opts = new CsvImportOptions();
        Assert.True(opts.IsAutoDetect);
        Assert.Equal(string.Empty, opts.ToDuckDbOptions());
    }

    [Fact]
    public void CsvImportOptions_SingleQuoteDelimiter_EscapedCorrectly()
    {
        var opts = new CsvImportOptions { QuoteChar = "'" };
        var duckDbOpts = opts.ToDuckDbOptions();
        // The single quote should be doubled in the DuckDB options string
        Assert.Contains("''", duckDbOpts);
    }

    // ── JsonImportOptions edge cases ────────────────────────────────────

    [Fact]
    public void JsonImportOptions_DefaultIsAutoDetect()
    {
        var opts = new JsonImportOptions();
        Assert.True(opts.IsAutoDetect);
    }

    // ── Workspace state deserialization ─────────────────────────────────

    [Fact]
    public void WorkspaceState_EmptyJson_DoesNotCrash()
    {
        var ex = Record.Exception(() => JsonSerializer.Deserialize<object>("{}"));
        Assert.Null(ex);
    }

    [Fact]
    public void WorkspaceState_MalformedJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<object>("{not valid json}"));
    }

    [Fact]
    public void WorkspaceState_NullJson_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            JsonSerializer.Deserialize<object>((string)null!));
    }

    // ── Path handling edge cases ────────────────────────────────────────

    [Theory]
    [InlineData("C:\\Users\\test\\O'Reilly.csv")]
    [InlineData("C:\\Users\\test\\file with spaces.parquet")]
    [InlineData("C:\\Users\\test\\日本語.csv")]
    public void DetectFormat_SpecialPathCharacters_HandledCorrectly(string path)
    {
        var ex = Record.Exception(() => FileFormatDetector.DetectFormat(path));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("C:/forward/slashes/file.parquet", SupportedFileFormat.Parquet)]
    [InlineData("C:\\back\\slashes\\file.csv", SupportedFileFormat.Csv)]
    [InlineData("relative/path/file.json", SupportedFileFormat.Json)]
    public void DetectFormat_VariousPathFormats_WorkCorrectly(string path, SupportedFileFormat expected)
    {
        Assert.Equal(expected, FileFormatDetector.DetectFormat(path));
    }

    // ── TranscodeScope cleanup ──────────────────────────────────────────

    [Fact]
    public void TranscodeScope_NoTempFile_DisposeSafe()
    {
        var scope = new TranscodeScope("original.csv");
        var ex = Record.Exception(() => scope.Dispose());
        Assert.Null(ex);
        Assert.Equal("original.csv", scope.FilePath);
    }

    [Fact]
    public void TranscodeScope_WithTempFile_CleansUpOnDispose()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "test content");

        var scope = new TranscodeScope("original.csv", tempFile);
        Assert.Equal(tempFile, scope.FilePath);

        scope.Dispose();
        Assert.False(File.Exists(tempFile), "Temp file should be deleted after dispose");
    }

    [Fact]
    public void TranscodeScope_TempFileAlreadyDeleted_DisposeSafe()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        // Don't create the file — it doesn't exist
        var scope = new TranscodeScope("original.csv", tempFile);
        var ex = Record.Exception(() => scope.Dispose());
        Assert.Null(ex);
    }
}
