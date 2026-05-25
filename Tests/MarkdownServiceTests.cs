using System.IO;
using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class MarkdownServiceTests
{
    [Fact]
    public void GetProfiles_ReturnsExpectedProfiles()
    {
        var service = new MarkdownService();

        var profiles = service.GetProfiles();

        Assert.Equal(
            [
                MarkdownFlavorProfile.CommonMark,
                MarkdownFlavorProfile.GitHubStyle,
                MarkdownFlavorProfile.ExtendedBestEffort
            ],
            profiles);
    }

    [Fact]
    public void RenderHtmlDocument_CommonMark_DoesNotPromotePipeTable()
    {
        var service = new MarkdownService();
        var markdown = "| Name | Value |\n| --- | --- |\n| Alpha | 1 |";

        var html = service.RenderHtmlDocument(markdown, MarkdownFlavorProfile.CommonMark);

        Assert.DoesNotContain("<table", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Preview profile: CommonMark", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_GitHubStyle_RendersPipeTable()
    {
        var service = new MarkdownService();
        var markdown = "| Name | Value |\n| --- | --- |\n| Alpha | 1 |";

        var html = service.RenderHtmlDocument(markdown, MarkdownFlavorProfile.GitHubStyle);

        Assert.Contains("<table", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Preview profile: GitHub-style", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_DisablesRawHtml()
    {
        var service = new MarkdownService();

        var html = service.RenderHtmlDocument("<script>alert('x')</script>", MarkdownFlavorProfile.ExtendedBestEffort);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert('x')&lt;/script&gt;", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveToFileAsync_ThenLoadFromFileAsync_RoundTripsContent()
    {
        var service = new MarkdownService();
        var tempFile = Path.Combine(Path.GetTempPath(), $"hiphipparquet-md-{Guid.NewGuid():N}.md");
        const string content = "# Sample\n\n- one\n- two\n";

        try
        {
            await service.SaveToFileAsync(tempFile, content);

            var loaded = await service.LoadFromFileAsync(tempFile);

            Assert.Equal(content, loaded);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
