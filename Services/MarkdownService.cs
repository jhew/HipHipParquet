using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HipHipParquet.Models;
using Markdig;

namespace HipHipParquet.Services;

public sealed partial class MarkdownService
{
    private readonly IReadOnlyDictionary<MarkdownFlavorProfile, MarkdownPipeline> _pipelines;

    public MarkdownService()
    {
        _pipelines = new Dictionary<MarkdownFlavorProfile, MarkdownPipeline>
        {
            [MarkdownFlavorProfile.CommonMark] = BuildCommonMarkPipeline(),
            [MarkdownFlavorProfile.GitHubStyle] = BuildGitHubStylePipeline(),
            [MarkdownFlavorProfile.ExtendedBestEffort] = BuildExtendedPipeline()
        };
    }

    public IReadOnlyList<MarkdownFlavorProfile> GetProfiles()
        => Enum.GetValues<MarkdownFlavorProfile>();

    public async Task<string> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        => await File.ReadAllTextAsync(filePath, cancellationToken);

    public async Task SaveToFileAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(filePath, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    /// <summary>
    /// Renders a standalone preview document. <paramref name="darkTheme"/> selects a palette
    /// matching the app's own theme, so the preview does not sit as a bright slab inside a
    /// dark workspace.
    /// </summary>
    /// <remarks>
    /// Two constraints come from the host being WPF's <c>WebBrowser</c> (Trident), not a
    /// modern engine. The <c>X-UA-Compatible</c> tag must be the first thing in the head or
    /// the control silently falls back to IE7 document mode, where most of this stylesheet is
    /// ignored. And even at the highest available mode, CSS custom properties are unsupported,
    /// so every colour is emitted literally rather than through <c>var()</c>.
    /// </remarks>
    public string RenderHtmlDocument(string markdown, MarkdownFlavorProfile profile, bool darkTheme = false)
    {
        var pipeline = _pipelines.TryGetValue(profile, out var configuredPipeline)
            ? configuredPipeline
            : _pipelines[MarkdownFlavorProfile.GitHubStyle];

        var body = Markdown.ToHtml(markdown ?? string.Empty, pipeline);

        // Extended lets raw HTML through Markdig so <details> works; filter it here.
        if (profile == MarkdownFlavorProfile.ExtendedBestEffort)
            body = HtmlSanitizer.Sanitize(body);

        bool hasDiagrams;
        (body, hasDiagrams) = PromoteMermaidBlocks(body);

        var profileLabel = profile.GetDisplayName();
        var p = darkTheme ? PreviewPalette.Dark : PreviewPalette.Light;
        var diagramBootstrap = hasDiagrams ? BuildMermaidBootstrap(darkTheme) : string.Empty;

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta http-equiv="X-UA-Compatible" content="IE=edge" />
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Markdown Preview</title>
    <style>
        html { color-scheme: {{p.Scheme}}; }

        * { box-sizing: border-box; }

        body {
            margin: 0;
            padding: 18px;
            background: {{p.Bg}};
            color: {{p.Text}};
            font-family: "Segoe UI", Arial, sans-serif;
            font-size: 14px;
            line-height: 1.6;
        }

        .shell {
            max-width: 980px;
            margin: 0 auto;
            background: {{p.Panel}};
            border: 1px solid {{p.Border}};
            border-radius: 14px;
            box-shadow: 0 18px 40px {{p.Shadow}};
            overflow: hidden;
        }

        .meta {
            padding: 10px 16px;
            background: {{p.MetaBg}};
            color: {{p.Muted}};
            border-bottom: 1px solid {{p.Border}};
            font-size: 12px;
            font-weight: 600;
        }

        .content { padding: 20px 22px 28px; }

        h1, h2, h3, h4, h5, h6 {
            color: {{p.Heading}};
            line-height: 1.25;
            margin-top: 1.5em;
        }

        h1, h2 { border-bottom: 1px solid {{p.Border}}; padding-bottom: .3em; }
        h1:first-child, h2:first-child, h3:first-child { margin-top: 0; }

        a { color: {{p.Accent}}; }

        pre, code {
            font-family: Consolas, "Courier New", monospace;
            background: {{p.CodeBg}};
            color: {{p.Text}};
        }

        code {
            padding: 2px 5px;
            border-radius: 4px;
            font-size: 90%;
        }

        pre {
            padding: 14px;
            border-radius: 10px;
            overflow-x: auto;
            border: 1px solid {{p.Border}};
        }

        pre code {
            padding: 0;
            background: transparent;
            border-radius: 0;
            font-size: 100%;
        }

        blockquote {
            margin: 1.2em 0;
            padding: 4px 16px;
            border-left: 4px solid {{p.QuoteBar}};
            color: {{p.Muted}};
            background: {{p.QuoteBg}};
        }

        table {
            width: 100%;
            border-collapse: collapse;
            margin: 1.2em 0;
        }

        th, td {
            border: 1px solid {{p.Border}};
            padding: 8px 10px;
            text-align: left;
            vertical-align: top;
        }

        th { background: {{p.TableHeadBg}}; color: {{p.Text}}; }

        hr {
            border: 0;
            border-top: 1px solid {{p.Border}};
            margin: 2em 0;
        }

        img { max-width: 100%; height: auto; }

        .mermaid {
            margin: 1.4em 0;
            padding: 12px;
            background: {{p.CodeBg}};
            border: 1px solid {{p.Border}};
            border-radius: 10px;
            overflow-x: auto;
            text-align: center;
        }

        .mermaid-error {
            display: block;
            margin-top: 8px;
            color: {{p.Muted}};
            font-size: 12px;
            text-align: left;
        }

        ul, ol { padding-left: 1.6em; }
        li { margin: .25em 0; }
    </style>
</head>
<body>
    <div class="shell">
        <div class="meta">Preview profile: {{System.Net.WebUtility.HtmlEncode(profileLabel)}}</div>
        <div class="content">{{body}}</div>
    </div>
{{diagramBootstrap}}
</body>
</html>
""";
    }

    /// <summary>
    /// Rewrites fenced mermaid blocks into the container mermaid.js looks for. The escaped
    /// source is deliberately left escaped: mermaid reads textContent, which the HTML parser
    /// decodes for us, so nothing has to be turned back into live markup.
    /// </summary>
    private static (string Html, bool HasDiagrams) PromoteMermaidBlocks(string html)
    {
        var found = false;
        var rewritten = MermaidBlockPattern().Replace(html, match =>
        {
            found = true;
            return $"<div class=\"mermaid\">{match.Groups["source"].Value}</div>";
        });

        // The Extended profile runs Markdig diagram support, which emits the container
        // itself, so nothing needed rewriting yet diagrams are still present.
        if (!found && rewritten.Contains("class=\"mermaid\"", StringComparison.OrdinalIgnoreCase))
            found = true;

        return (rewritten, found);
    }

    /// <summary>
    /// Loads the bundled renderer. The script sits beside the generated document in the host's
    /// local virtual folder, so rendering a diagram never makes a network request.
    /// </summary>
    private static string BuildMermaidBootstrap(bool darkTheme)
    {
        var theme = darkTheme ? "dark" : "default";
        return $$"""
    <script src="mermaid.min.js"></script>
    <script>
        (function () {
            if (typeof mermaid === "undefined") {
                var blocks = document.querySelectorAll(".mermaid");
                for (var i = 0; i < blocks.length; i++) {
                    var note = document.createElement("span");
                    note.className = "mermaid-error";
                    note.textContent = "Diagram renderer unavailable - showing source.";
                    blocks[i].appendChild(note);
                }
                return;
            }
            mermaid.initialize({ startOnLoad: true, securityLevel: "strict", theme: "{{theme}}" });
        })();
    </script>
""";
    }

    [GeneratedRegex(
        @"<pre>\s*<code[^>]*class=""[^""]*language-mermaid[^""]*""[^>]*>(?<source>.*?)</code>\s*</pre>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MermaidBlockPattern();

    /// <summary>
    /// Preview colours mirrored from Themes/LightTheme.xaml and Themes/DarkTheme.xaml so the
    /// rendered document reads as part of the same application.
    /// </summary>
    private readonly record struct PreviewPalette(
        string Scheme,
        string Bg,
        string Panel,
        string Text,
        string Muted,
        string Border,
        string Heading,
        string Accent,
        string MetaBg,
        string CodeBg,
        string QuoteBg,
        string QuoteBar,
        string TableHeadBg,
        string Shadow)
    {
        public static readonly PreviewPalette Light = new(
            Scheme: "light",
            Bg: "#EEF3F8",
            Panel: "#FFFFFF",
            Text: "#1F2D3D",
            Muted: "#52606D",
            Border: "#D5DFEA",
            Heading: "#2B4C7E",
            Accent: "#512BD4",
            MetaBg: "#F8FBFF",
            CodeBg: "#F1F4F8",
            QuoteBg: "#F8FBFF",
            QuoteBar: "#99B6D8",
            TableHeadBg: "#EEF4FB",
            Shadow: "rgba(31, 45, 61, 0.08)");

        public static readonly PreviewPalette Dark = new(
            Scheme: "dark",
            Bg: "#1E2227",
            Panel: "#262B31",
            Text: "#E8ECF1",
            Muted: "#A9B4C0",
            Border: "#3A424B",
            Heading: "#9FC1E8",
            Accent: "#B0A0F5",
            MetaBg: "#24292F",
            CodeBg: "#2A3037",
            QuoteBg: "#22262C",
            QuoteBar: "#46617F",
            TableHeadBg: "#2A3037",
            Shadow: "rgba(0, 0, 0, 0.35)");
    }

    private static MarkdownPipeline BuildCommonMarkPipeline()
        => new MarkdownPipelineBuilder()
            .DisableHtml()
            .Build();

    private static MarkdownPipeline BuildGitHubStylePipeline()
        => new MarkdownPipelineBuilder()
            .DisableHtml()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoLinks()
            .UseEmphasisExtras()
            .UseAutoIdentifiers()
            .Build();

    // Raw HTML stays enabled here and is filtered by HtmlSanitizer after rendering, so
    // documents can use <details>/<summary> without opening a scripting hole.
    private static MarkdownPipeline BuildExtendedPipeline()
        => new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseAutoIdentifiers()
            .Build();
}
