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
    public void RenderHtmlDocument_StripsScriptEntirely()
    {
        var service = new MarkdownService();

        var html = service.RenderHtmlDocument("<script>alert('x')</script>", MarkdownFlavorProfile.ExtendedBestEffort);

        // The Extended profile now renders raw HTML so <details> works, so a script must be
        // removed with its contents rather than escaped into visible text.
        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert('x')", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_StrictProfiles_StillEscapeRawHtml()
    {
        var service = new MarkdownService();

        foreach (var profile in new[] { MarkdownFlavorProfile.CommonMark, MarkdownFlavorProfile.GitHubStyle })
        {
            var html = service.RenderHtmlDocument("<script>alert('x')</script>", profile);
            Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RenderHtmlDocument_Extended_RendersDetailsDisclosure()
    {
        var service = new MarkdownService();
        var markdown = "<details>\n<summary>More</summary>\n\nHidden body\n\n</details>";

        var html = service.RenderHtmlDocument(markdown, MarkdownFlavorProfile.ExtendedBestEffort);

        Assert.Contains("<details>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<summary>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderHtmlDocument_Extended_DropsEventHandlerAttributes()
    {
        var service = new MarkdownService();

        var html = service.RenderHtmlDocument("<div onclick=\"steal()\">hi</div>", MarkdownFlavorProfile.ExtendedBestEffort);

        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hi", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_Extended_BlocksJavascriptUrls()
    {
        var service = new MarkdownService();

        var html = service.RenderHtmlDocument("<a href=\"javascript:evil()\">click</a>", MarkdownFlavorProfile.ExtendedBestEffort);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderHtmlDocument_Extended_EmitsDiagramContainer()
    {
        var service = new MarkdownService();
        var markdown = "```mermaid\nflowchart LR\n  A --> B\n```";

        var html = service.RenderHtmlDocument(markdown, MarkdownFlavorProfile.ExtendedBestEffort);

        // Markdig's advanced profile turns the fence into the container natively.
        Assert.Contains("<div class=\"mermaid\">", html, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_LeanProfile_PromotesMermaidFenceToContainer()
    {
        var service = new MarkdownService();
        var markdown = "```mermaid\nflowchart LR\n  A --> B\n```";

        // GitHub-style has no diagram extension, so the fence arrives as a code block
        // and has to be promoted before mermaid will pick it up.
        var html = service.RenderHtmlDocument(markdown, MarkdownFlavorProfile.GitHubStyle);

        Assert.Contains("<div class=\"mermaid\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("language-mermaid", html, StringComparison.Ordinal);
        // Source stays escaped here; mermaid reads textContent, which the parser decodes.
        Assert.Contains("--&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_MermaidFence_LoadsTheBundledRendererLocally()
    {
        var service = new MarkdownService();

        var html = service.RenderHtmlDocument("```mermaid\npie\n \"a\" : 1\n```", MarkdownFlavorProfile.ExtendedBestEffort);

        Assert.Contains("<script src=\"mermaid.min.js\">", html, StringComparison.Ordinal);
        // Offline-first: the preview must never reach out to a CDN.
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderHtmlDocument_WithoutDiagrams_DoesNotLoadTheRenderer()
    {
        var service = new MarkdownService();

        var html = service.RenderHtmlDocument("# Just a heading", MarkdownFlavorProfile.ExtendedBestEffort);

        Assert.DoesNotContain("mermaid.min.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_DarkTheme_UsesTheDarkPalette()
    {
        var service = new MarkdownService();

        var dark = service.RenderHtmlDocument("# Title", MarkdownFlavorProfile.GitHubStyle, darkTheme: true);
        var light = service.RenderHtmlDocument("# Title", MarkdownFlavorProfile.GitHubStyle, darkTheme: false);

        Assert.Contains("#1E2227", dark, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color-scheme: dark", dark, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#EEF3F8", light, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color-scheme: light", light, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderHtmlDocument_TargetsTheModernEngine()
    {
        var service = new MarkdownService();

        var html = service.RenderHtmlDocument("# Title", MarkdownFlavorProfile.GitHubStyle);

        // Must precede any other head content or the host falls back to IE7 document mode.
        var compat = html.IndexOf("X-UA-Compatible", StringComparison.OrdinalIgnoreCase);
        var charset = html.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
        Assert.True(compat >= 0 && compat < charset);
        // CSS custom properties are unsupported by the host, so colours must be literal.
        Assert.DoesNotContain("var(--", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtmlDocument_Extended_RendersTaskListsAndAlignedTables()
    {
        var service = new MarkdownService();
        var markdown = "- [x] done\n- [ ] pending\n\n| A | B |\n|---|---:|\n| 1 | 2 |";

        var html = service.RenderHtmlDocument(markdown, MarkdownFlavorProfile.ExtendedBestEffort);

        Assert.Contains("type=\"checkbox\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<table", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text-align: right", html, StringComparison.OrdinalIgnoreCase);
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
