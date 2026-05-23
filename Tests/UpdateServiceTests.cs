using System.IO;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void ParseUpdateInfo_VPrefixedNewerVersion_ReturnsUpdate()
    {
        var json = """
        {
          "tag_name": "v2.3.4",
          "assets": [
            {
              "name": "HipHipParquet-2.3.4-Setup.exe",
              "browser_download_url": "https://github.com/jhew/HipHipParquet/releases/download/v2.3.4/HipHipParquet-2.3.4-Setup.exe"
            }
          ]
        }
        """;

        var result = UpdateService.ParseUpdateInfo(json, new Version(2, 3, 3));

        Assert.NotNull(result);
        Assert.Equal(new Version(2, 3, 4), result!.LatestVersion);
        Assert.Equal("https://github.com/jhew/HipHipParquet/releases/download/v2.3.4/HipHipParquet-2.3.4-Setup.exe", result.InstallerUrl);
    }

    [Fact]
    public void ParseUpdateInfo_UpToDate_ReturnsNull()
    {
        var json = """
        {
          "tag_name": "v1.0.0",
          "assets": []
        }
        """;

        var result = UpdateService.ParseUpdateInfo(json, new Version(1, 0, 0));

        Assert.Null(result);
    }

    [Fact]
    public void ParseUpdateInfo_MissingTagName_ThrowsInvalidDataException()
    {
        var json = """
        {
          "assets": []
        }
        """;

        Assert.Throws<InvalidDataException>(() =>
            UpdateService.ParseUpdateInfo(json, new Version(1, 0, 0)));
    }

    [Fact]
    public void ParseUpdateInfo_InvalidTagVersion_ThrowsInvalidDataException()
    {
        var json = """
        {
          "tag_name": "not-a-version",
          "assets": []
        }
        """;

        Assert.Throws<InvalidDataException>(() =>
            UpdateService.ParseUpdateInfo(json, new Version(1, 0, 0)));
    }

    [Fact]
    public void ParseUpdateInfo_UntrustedSetupAsset_DoesNotBlockTrustedFallback()
    {
        var json = """
        {
          "tag_name": "v3.0.0",
          "assets": [
            {
              "name": "HipHipParquet-3.0.0-Setup.exe",
              "browser_download_url": "https://example.com/HipHipParquet-3.0.0-Setup.exe"
            },
            {
              "name": "HipHipParquet-3.0.0-Setup.exe",
              "browser_download_url": "https://github.com/jhew/HipHipParquet/releases/download/v3.0.0/HipHipParquet-3.0.0-Setup.exe"
            }
          ]
        }
        """;

        var result = UpdateService.ParseUpdateInfo(json, new Version(2, 9, 9));

        Assert.NotNull(result);
        Assert.Equal("https://github.com/jhew/HipHipParquet/releases/download/v3.0.0/HipHipParquet-3.0.0-Setup.exe", result!.InstallerUrl);
    }

    [Fact]
    public void ParseUpdateInfo_HttpGithubSetupAsset_IsRejected()
    {
        var json = """
        {
          "tag_name": "v3.1.0",
          "assets": [
            {
              "name": "HipHipParquet-3.1.0-Setup.exe",
              "browser_download_url": "http://github.com/jhew/HipHipParquet/releases/download/v3.1.0/HipHipParquet-3.1.0-Setup.exe"
            }
          ]
        }
        """;

        var result = UpdateService.ParseUpdateInfo(json, new Version(3, 0, 0));

        Assert.NotNull(result);
        Assert.Null(result!.InstallerUrl);
    }

    [Fact]
    public void ParseUpdateInfo_HttpsGithubUserContentSetupAsset_IsAccepted()
    {
        var json = """
        {
          "tag_name": "v3.2.0",
          "assets": [
            {
              "name": "HipHipParquet-3.2.0-Setup.exe",
              "browser_download_url": "https://release-assets.githubusercontent.com/repos/123/456/HipHipParquet-3.2.0-Setup.exe"
            }
          ]
        }
        """;

        var result = UpdateService.ParseUpdateInfo(json, new Version(3, 1, 0));

        Assert.NotNull(result);
        Assert.Equal("https://release-assets.githubusercontent.com/repos/123/456/HipHipParquet-3.2.0-Setup.exe", result!.InstallerUrl);
    }

    [Fact]
    public async Task DownloadInstallerWithDiagnosticsAsync_InvalidUrl_ReturnsFailureKind()
    {
        var result = await UpdateService.DownloadInstallerWithDiagnosticsAsync("not-a-url");

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateFailureKind.InvalidUrl, result.FailureKind);
    }

    [Fact]
    public async Task VerifyChecksumWithDiagnosticsAsync_UntrustedUrl_ReturnsFailureKind()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "installer-bytes");

            var result = await UpdateService.VerifyChecksumWithDiagnosticsAsync(tempFile, "https://example.com/SHA256SUMS.txt");

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateFailureKind.UntrustedSource, result.FailureKind);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void VerifyAuthenticodeSignatureWithDiagnostics_MissingFile_ReturnsFileNotFound()
    {
        var result = UpdateService.VerifyAuthenticodeSignatureWithDiagnostics(@"C:\nonexistent-file-12345.exe");

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateFailureKind.FileNotFound, result.FailureKind);
    }
}
