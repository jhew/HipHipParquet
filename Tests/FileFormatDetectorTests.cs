using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class FileFormatDetectorTests
{
    // ── DetectFormat ────────────────────────────────────────────────────

    [Theory]
    [InlineData("data.parquet", SupportedFileFormat.Parquet)]
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
        Assert.Contains("read_parquet", result);
        Assert.Contains("data.parquet", result);
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
        Assert.Contains("*.csv", filter);
        Assert.Contains("*.tsv", filter);
        Assert.Contains("*.json", filter);
        Assert.Contains("*.xlsx", filter);
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
}
