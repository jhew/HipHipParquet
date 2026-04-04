using System.IO;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class IdentifierEscapingTests
{
    // ── DuckDB identifier escaping ──────────────────────────────────────

    [Theory]
    [InlineData("simple", "\"simple\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("multi\"\"quotes\"", "\"multi\"\"\"\"quotes\"\"\"")]
    [InlineData("", "\"\"")]
    [InlineData("col-with-dashes", "\"col-with-dashes\"")]
    [InlineData("col.with.dots", "\"col.with.dots\"")]
    [InlineData("123numeric", "\"123numeric\"")]
    [InlineData("SELECT", "\"SELECT\"")]
    [InlineData("col\"name\"here", "\"col\"\"name\"\"here\"")]
    public void EscapeDuckDbIdentifier_HandlesSpecialCharacters(string input, string expected)
    {
        Assert.Equal(expected, ParquetService.EscapeDuckDbIdentifier(input));
    }

    [Fact]
    public void EscapeDuckDbIdentifier_DoubleQuoteInMiddle_IsDoubled()
    {
        var result = ParquetService.EscapeDuckDbIdentifier("a\"b");
        Assert.Equal("\"a\"\"b\"", result);
        // Verify it starts and ends with a single quote
        Assert.StartsWith("\"", result);
        Assert.EndsWith("\"", result);
    }

    [Fact]
    public void EscapeDuckDbIdentifier_OnlyDoubleQuotes_AllDoubled()
    {
        var result = ParquetService.EscapeDuckDbIdentifier("\"\"\"");
        Assert.Equal("\"\"\"\"\"\"\"\"", result);
    }

    // ── UpdateService parsing ───────────────────────────────────────────

    [Fact]
    public void ParseUpdateInfo_IncludesChecksumsUrl_WhenPresent()
    {
        var json = """
        {
          "tag_name": "v2.0.0",
          "assets": [
            {
              "name": "HipHipParquet-2.0.0-Setup.exe",
              "browser_download_url": "https://github.com/jhew/HipHipParquet/releases/download/v2.0.0/HipHipParquet-2.0.0-Setup.exe"
            },
            {
              "name": "HipHipParquet-2.0.0-SHA256SUMS.txt",
              "browser_download_url": "https://github.com/jhew/HipHipParquet/releases/download/v2.0.0/HipHipParquet-2.0.0-SHA256SUMS.txt"
            }
          ]
        }
        """;

        var result = UpdateService.ParseUpdateInfo(json, new Version(1, 0, 0));

        Assert.NotNull(result);
        Assert.NotNull(result!.InstallerUrl);
        Assert.NotNull(result.ChecksumsUrl);
        Assert.Contains("SHA256SUMS", result.ChecksumsUrl);
    }

    [Fact]
    public void ParseUpdateInfo_NoChecksumsAsset_ChecksumsUrlIsNull()
    {
        var json = """
        {
          "tag_name": "v2.0.0",
          "assets": [
            {
              "name": "HipHipParquet-2.0.0-Setup.exe",
              "browser_download_url": "https://github.com/jhew/HipHipParquet/releases/download/v2.0.0/HipHipParquet-2.0.0-Setup.exe"
            }
          ]
        }
        """;

        var result = UpdateService.ParseUpdateInfo(json, new Version(1, 0, 0));

        Assert.NotNull(result);
        Assert.Null(result!.ChecksumsUrl);
    }

    [Fact]
    public void VerifyAuthenticodeSignature_NonExistentFile_ReturnsFalse()
    {
        var result = UpdateService.VerifyAuthenticodeSignature(@"C:\nonexistent-file-12345.exe");
        Assert.False(result);
    }

    [Fact]
    public void VerifyAuthenticodeSignature_UnsignedFile_ReturnsFalse()
    {
        // Create a temp file that isn't signed
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not a signed exe");
            var result = UpdateService.VerifyAuthenticodeSignature(tempFile);
            Assert.False(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
