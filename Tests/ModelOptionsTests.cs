using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

// ── CsvImportOptions.ToDuckDbOptions ────────────────────────────────────────

public class CsvImportOptionsTests
{
    [Fact]
    public void ToDuckDbOptions_AllDefaults_ReturnsEmpty()
    {
        var opts = CsvImportOptions.AutoDetect;
        Assert.Equal(string.Empty, opts.ToDuckDbOptions());
    }

    [Fact]
    public void ToDuckDbOptions_IgnoreErrors_EmitsFlag()
    {
        var opts = new CsvImportOptions { IgnoreErrors = true };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("ignore_errors=true", result);
    }

    [Fact]
    public void ToDuckDbOptions_NullPadding_EmitsFlag()
    {
        var opts = new CsvImportOptions { NullPadding = true };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("null_padding=true", result);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("latin1")]
    [InlineData("windows-1252")]
    public void ToDuckDbOptions_NonAutoEncoding_EmitsEncodingOption(string encoding)
    {
        var opts = new CsvImportOptions { Encoding = encoding };
        var result = opts.ToDuckDbOptions();
        Assert.Contains($"encoding='{encoding}'", result);
    }

    [Fact]
    public void ToDuckDbOptions_AutoEncoding_DoesNotEmitOption()
    {
        var opts = new CsvImportOptions { Encoding = "auto" };
        var result = opts.ToDuckDbOptions();
        Assert.DoesNotContain("encoding=", result);
    }

    [Fact]
    public void ToDuckDbOptions_CustomDelimiter_EmitDelim()
    {
        var opts = new CsvImportOptions { Delimiter = ";" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("delim=';'", result);
    }

    [Fact]
    public void ToDuckDbOptions_TabDelimiter_EmitsEscapedTab()
    {
        var opts = new CsvImportOptions { Delimiter = "tab" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains(@"delim='\t'", result);
    }

    [Fact]
    public void ToDuckDbOptions_SkipRows_EmitsSkip()
    {
        var opts = new CsvImportOptions { SkipRows = 3 };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("skip=3", result);
    }

    [Fact]
    public void IsAutoDetect_IgnoreErrors_IsNotAutoDetect()
    {
        var opts = new CsvImportOptions { IgnoreErrors = true };
        Assert.False(opts.IsAutoDetect);
    }

    [Fact]
    public void IsAutoDetect_NullPadding_IsNotAutoDetect()
    {
        var opts = new CsvImportOptions { NullPadding = true };
        Assert.False(opts.IsAutoDetect);
    }

    [Fact]
    public void IsAutoDetect_NonAutoEncoding_IsNotAutoDetect()
    {
        var opts = new CsvImportOptions { Encoding = "latin1" };
        Assert.False(opts.IsAutoDetect);
    }
}

// ── JsonImportOptions.ToDuckDbOptions ───────────────────────────────────────

public class JsonImportOptionsTests
{
    [Fact]
    public void ToDuckDbOptions_AllDefaults_ReturnsEmpty()
    {
        var opts = JsonImportOptions.Default;
        Assert.Equal(string.Empty, opts.ToDuckDbOptions());
    }

    [Fact]
    public void ToDuckDbOptions_Format_EmitsFormatOption()
    {
        var opts = new JsonImportOptions { Format = "array" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("format='array'", result);
    }

    [Fact]
    public void ToDuckDbOptions_AutoFormat_DoesNotEmitFormatOption()
    {
        var opts = new JsonImportOptions { Format = "auto" };
        var result = opts.ToDuckDbOptions();
        Assert.DoesNotContain("format=", result);
    }

    [Fact]
    public void ToDuckDbOptions_Records_EmitsRecordsOption()
    {
        var opts = new JsonImportOptions { Records = "true" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("records='true'", result);
    }

    [Fact]
    public void ToDuckDbOptions_MaxDepth_EmitsMaximumDepth()
    {
        var opts = new JsonImportOptions { MaxDepth = 3 };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("maximum_depth=3", result);
    }

    [Fact]
    public void ToDuckDbOptions_DefaultMaxDepth_NegativeOne_NotEmitted()
    {
        var opts = new JsonImportOptions { MaxDepth = -1 };
        var result = opts.ToDuckDbOptions();
        Assert.DoesNotContain("maximum_depth=", result);
    }

    [Fact]
    public void ToDuckDbOptions_SampleSize_EmitsSampleSize()
    {
        var opts = new JsonImportOptions { SampleSize = 5000 };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("sample_size=5000", result);
    }

    [Fact]
    public void ToDuckDbOptions_DefaultSampleSize_NegativeOne_NotEmitted()
    {
        var opts = new JsonImportOptions { SampleSize = -1 };
        var result = opts.ToDuckDbOptions();
        Assert.DoesNotContain("sample_size=", result);
    }

    [Fact]
    public void ToDuckDbOptions_IgnoreErrors_EmitsFlag()
    {
        var opts = new JsonImportOptions { IgnoreErrors = true };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("ignore_errors=true", result);
    }

    [Fact]
    public void ToDuckDbOptions_AutoDetectFalse_EmitsOption()
    {
        var opts = new JsonImportOptions { AutoDetect = false };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("auto_detect=false", result);
    }

    [Fact]
    public void ToDuckDbOptions_TimestampFormat_EmitsOption()
    {
        var opts = new JsonImportOptions { TimestampFormat = "%Y-%m-%d %H:%M:%S" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("timestampformat='%Y-%m-%d %H:%M:%S'", result);
    }

    [Fact]
    public void ToDuckDbOptions_DateFormat_EmitsOption()
    {
        var opts = new JsonImportOptions { DateFormat = "%d/%m/%Y" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("dateformat='%d/%m/%Y'", result);
    }

    // SQL injection / single-quote escaping

    [Fact]
    public void ToDuckDbOptions_TimestampFormat_WithSingleQuote_IsEscaped()
    {
        var opts = new JsonImportOptions { TimestampFormat = "%Y'%m" };
        var result = opts.ToDuckDbOptions();
        // The embedded quote must be doubled, not left raw
        Assert.Contains("timestampformat='%Y''%m'", result);
        Assert.DoesNotContain("timestampformat='%Y'%m'", result);
    }

    [Fact]
    public void ToDuckDbOptions_DateFormat_WithSingleQuote_IsEscaped()
    {
        var opts = new JsonImportOptions { DateFormat = "d'M'Y" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("dateformat='d''M''Y'", result);
    }

    [Fact]
    public void ToDuckDbOptions_Format_WithSingleQuote_IsEscaped()
    {
        var opts = new JsonImportOptions { Format = "arr'ay" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("format='arr''ay'", result);
    }

    [Fact]
    public void ToDuckDbOptions_Records_WithSingleQuote_IsEscaped()
    {
        var opts = new JsonImportOptions { Records = "tr'ue" };
        var result = opts.ToDuckDbOptions();
        Assert.Contains("records='tr''ue'", result);
    }

    [Fact]
    public void IsAutoDetect_WithIgnoreErrors_IsNotAutoDetect()
    {
        var opts = new JsonImportOptions { IgnoreErrors = true };
        Assert.False(opts.IsAutoDetect);
    }

    [Fact]
    public void IsAutoDetect_WithMaxDepth_IsNotAutoDetect()
    {
        var opts = new JsonImportOptions { MaxDepth = 2 };
        Assert.False(opts.IsAutoDetect);
    }
}

// ── FileFormatDetector — JSON overloads and path escaping ───────────────────

public class FileFormatDetectorJsonTests
{
    [Fact]
    public void GetDuckDbReaderExpression_JsonWithOptions_IncludesOptions()
    {
        var opts = new JsonImportOptions { Format = "array" };
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.json", SupportedFileFormat.Json, opts);
        Assert.Contains("read_json_auto", result);
        Assert.Contains("format='array'", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_JsonWithNullOptions_FallsBackToDefault()
    {
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.json", SupportedFileFormat.Json, (JsonImportOptions?)null);
        var baseline = FileFormatDetector.GetDuckDbReaderExpression("data.json", SupportedFileFormat.Json);
        Assert.Equal(baseline, result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_CsvWithIgnoreErrors_IncludesFlag()
    {
        var opts = new CsvImportOptions { IgnoreErrors = true };
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv, opts);
        Assert.Contains("ignore_errors=true", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_CsvWithNullPadding_IncludesFlag()
    {
        var opts = new CsvImportOptions { NullPadding = true };
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv, opts);
        Assert.Contains("null_padding=true", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_CsvWithEncoding_IncludesEncoding()
    {
        var opts = new CsvImportOptions { Encoding = "latin1" };
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv, opts);
        Assert.Contains("encoding='latin1'", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_PathWithSingleQuote_IsEscapedOnce()
    {
        // Path contains a single quote — FileFormatDetector must escape it exactly once.
        var result = FileFormatDetector.GetDuckDbReaderExpression("O'Brien/data.csv", SupportedFileFormat.Csv);
        Assert.Contains("O''Brien/data.csv", result);
        // Must NOT be double-escaped (would produce O''''Brien)
        Assert.DoesNotContain("O''''Brien", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_CombinedOverload_RoutesToJsonWhenJsonOptions()
    {
        var jsonOpts = new JsonImportOptions { Format = "newline_delimited" };
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.json", SupportedFileFormat.Json, null, jsonOpts);
        Assert.Contains("read_json_auto", result);
        Assert.Contains("format='newline_delimited'", result);
    }

    [Fact]
    public void GetDuckDbReaderExpression_CombinedOverload_RoutesToCsvWhenCsvOptions()
    {
        var csvOpts = new CsvImportOptions { Delimiter = "|" };
        var result = FileFormatDetector.GetDuckDbReaderExpression("data.csv", SupportedFileFormat.Csv, csvOpts, null);
        Assert.Contains("read_csv_auto", result);
        Assert.Contains("delim='|'", result);
    }
}
