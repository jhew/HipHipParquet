using System.IO;
using System.Text;
using HipHipParquet.Models;
using Markdig;

namespace HipHipParquet.Services;

public sealed class MarkdownService
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

    public string RenderHtmlDocument(string markdown, MarkdownFlavorProfile profile)
    {
        var pipeline = _pipelines.TryGetValue(profile, out var configuredPipeline)
            ? configuredPipeline
            : _pipelines[MarkdownFlavorProfile.GitHubStyle];

        var body = Markdown.ToHtml(markdown ?? string.Empty, pipeline);
        var profileLabel = profile.GetDisplayName();

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Markdown Preview</title>
    <style>
        :root {
            color-scheme: light;
            --bg: #f5f7fa;
            --panel: #ffffff;
            --text: #1f2d3d;
            --muted: #5c6f82;
            --border: #d5dfea;
            --accent: #2b4c7e;
            --code-bg: #f1f4f8;
        }

        * { box-sizing: border-box; }

        body {
            margin: 0;
            padding: 18px;
            background: linear-gradient(180deg, #eef3f8 0%, #f7f9fc 100%);
            color: var(--text);
            font-family: "Segoe UI", Arial, sans-serif;
            line-height: 1.6;
        }

        .shell {
            max-width: 980px;
            margin: 0 auto;
            background: var(--panel);
            border: 1px solid var(--border);
            border-radius: 14px;
            box-shadow: 0 18px 40px rgba(31, 45, 61, 0.08);
            overflow: hidden;
        }

        .meta {
            padding: 10px 16px;
            background: #eef4fb;
            color: var(--muted);
            border-bottom: 1px solid var(--border);
            font-size: 12px;
            font-weight: 600;
        }

        .content {
            padding: 20px 22px 28px;
        }

        h1, h2, h3, h4, h5, h6 {
            color: #17324d;
            line-height: 1.25;
            margin-top: 1.5em;
        }

        h1:first-child, h2:first-child, h3:first-child { margin-top: 0; }
        a { color: var(--accent); }
        pre, code {
            font-family: Consolas, "Courier New", monospace;
            background: var(--code-bg);
        }

        code {
            padding: 2px 5px;
            border-radius: 4px;
        }

        pre {
            padding: 14px;
            border-radius: 10px;
            overflow-x: auto;
            border: 1px solid var(--border);
        }

        pre code {
            padding: 0;
            background: transparent;
            border-radius: 0;
        }

        blockquote {
            margin: 1.2em 0;
            padding: 4px 16px;
            border-left: 4px solid #99b6d8;
            color: #42586f;
            background: #f8fbff;
        }

        table {
            width: 100%;
            border-collapse: collapse;
            margin: 1.2em 0;
            overflow: hidden;
        }

        th, td {
            border: 1px solid var(--border);
            padding: 8px 10px;
            text-align: left;
            vertical-align: top;
        }

        th {
            background: #eef4fb;
        }

        hr {
            border: 0;
            border-top: 1px solid var(--border);
            margin: 2em 0;
        }

        img {
            max-width: 100%;
            height: auto;
        }
    </style>
</head>
<body>
    <div class="shell">
        <div class="meta">Preview profile: {{System.Net.WebUtility.HtmlEncode(profileLabel)}}</div>
        <div class="content">{{body}}</div>
    </div>
</body>
</html>
""";
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

    private static MarkdownPipeline BuildExtendedPipeline()
        => new MarkdownPipelineBuilder()
            .DisableHtml()
            .UseAdvancedExtensions()
            .UseAutoIdentifiers()
            .Build();
}
