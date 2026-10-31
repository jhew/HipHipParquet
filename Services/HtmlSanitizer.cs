using System.Text;
using System.Text.RegularExpressions;

namespace HipHipParquet.Services;

/// <summary>
/// Allow-list sanitiser for rendered markdown. Raw HTML is permitted in the
/// Extended profile so constructs like <c>&lt;details&gt;</c> work, but nothing
/// executable survives: dangerous elements are dropped with their content, every
/// other unknown element is unwrapped to its text, and attributes are filtered to
/// a per-element allow-list with no event handlers and no scriptable URLs.
/// </summary>
/// <remarks>
/// This runs over Markdig's output, so it must preserve the markup Markdig itself
/// emits — task-list checkboxes, <c>language-*</c> code classes and heading ids.
/// It is a preview-hardening measure for local documents, not a substitute for a
/// full HTML parser on untrusted web input.
/// </remarks>
public static partial class HtmlSanitizer
{
    // Removed together with everything between the tags.
    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "applet",
        "form", "select", "textarea", "button", "noscript", "template"
    };

    // Kept, with attributes filtered below. Covers Markdig's own output plus the
    // handful of raw-HTML elements that are genuinely useful in a document.
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "div", "span",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li", "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "pre", "code", "blockquote", "a", "img", "input",
        "em", "strong", "b", "i", "u", "s", "del", "ins", "mark", "small",
        "sub", "sup", "kbd", "samp", "var", "abbr", "cite", "q",
        "details", "summary", "figure", "figcaption"
    };

    private static readonly Dictionary<string, HashSet<string>> AllowedAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new(StringComparer.OrdinalIgnoreCase) { "href", "title" },
            ["img"] = new(StringComparer.OrdinalIgnoreCase) { "src", "alt", "title", "width", "height" },
            ["input"] = new(StringComparer.OrdinalIgnoreCase) { "type", "checked", "disabled", "class" },
            ["details"] = new(StringComparer.OrdinalIgnoreCase) { "open" },
            ["th"] = new(StringComparer.OrdinalIgnoreCase) { "align", "colspan", "rowspan", "style" },
            ["td"] = new(StringComparer.OrdinalIgnoreCase) { "align", "colspan", "rowspan", "style" },
            ["col"] = new(StringComparer.OrdinalIgnoreCase) { "align", "span", "style" },
            ["pre"] = new(StringComparer.OrdinalIgnoreCase) { "class" },
            ["code"] = new(StringComparer.OrdinalIgnoreCase) { "class" },
            ["div"] = new(StringComparer.OrdinalIgnoreCase) { "class" },
            ["span"] = new(StringComparer.OrdinalIgnoreCase) { "class" },
            ["li"] = new(StringComparer.OrdinalIgnoreCase) { "class" }
        };

    // Headings carry ids when UseAutoIdentifiers is on; allowed everywhere.
    private static readonly HashSet<string> GloballyAllowedAttributes =
        new(StringComparer.OrdinalIgnoreCase) { "id" };

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var working = html;

        foreach (var tag in DropWithContent)
        {
            working = Regex.Replace(
                working,
                $@"<{tag}\b[^>]*>.*?</\s*{tag}\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Unclosed or self-closing forms of the same elements.
            working = Regex.Replace(
                working,
                $@"</?\s*{tag}\b[^>]*/?>",
                string.Empty,
                RegexOptions.IgnoreCase);
        }

        return TagPattern().Replace(working, FilterTag);
    }

    private static string FilterTag(Match match)
    {
        var closing = match.Groups["close"].Success;
        var name = match.Groups["name"].Value;

        // Unknown element: drop the markup, keep whatever text it wrapped.
        if (!AllowedTags.Contains(name))
            return string.Empty;

        if (closing)
            return $"</{name.ToLowerInvariant()}>";

        var builder = new StringBuilder("<").Append(name.ToLowerInvariant());
        var allowed = AllowedAttributes.TryGetValue(name, out var set) ? set : null;

        foreach (Match attr in AttributePattern().Matches(match.Groups["attrs"].Value))
        {
            var attrName = attr.Groups["name"].Value;
            var permitted = GloballyAllowedAttributes.Contains(attrName)
                || (allowed?.Contains(attrName) ?? false);
            if (!permitted)
                continue;

            // Validate the decoded value and emit exactly that, re-encoded. Checking a decoded
            // copy but writing the raw text let entities WebUtility doesn't know (&colon;, &Tab;)
            // pass the check and then decode to "javascript:" in the browser.
            var value = System.Net.WebUtility.HtmlDecode(attr.Groups["quoted"].Success
                ? attr.Groups["quoted"].Value
                : attr.Groups["bare"].Value);

            if (IsUrlAttribute(attrName) && !IsSafeUrl(value))
                continue;

            if (attrName.Equals("style", StringComparison.OrdinalIgnoreCase) && !IsSafeStyle(value))
                continue;

            builder.Append(' ').Append(attrName.ToLowerInvariant())
                   .Append("=\"").Append(System.Net.WebUtility.HtmlEncode(value)).Append('"');
        }

        if (match.Groups["selfClose"].Success)
            builder.Append(" /");

        return builder.Append('>').ToString();
    }

    /// <summary>
    /// Inline styles are rejected except table alignment, which is the only one Markdig
    /// emits and the only one worth carrying: it is how "|---:|" survives into the preview.
    /// </summary>
    private static bool IsSafeStyle(string value)
        => Regex.IsMatch(
            value,
            @"^\s*text-align\s*:\s*(left|right|center)\s*;?\s*$",
            RegexOptions.IgnoreCase);

    private static bool IsUrlAttribute(string name)
        => name.Equals("href", StringComparison.OrdinalIgnoreCase)
        || name.Equals("src", StringComparison.OrdinalIgnoreCase);

    /// <summary>Blocks javascript:, vbscript: and non-image data: URLs.</summary>
    /// <param name="value">The decoded attribute value.</param>
    private static bool IsSafeUrl(string value)
    {
        // Read the scheme the way a browser's URL parser does: tabs and newlines are removed
        // anywhere, so "jav\tascript:" is "javascript:", and leading/trailing controls and
        // spaces are ignored.
        var trimmed = UrlIgnoredCharacters().Replace(value, string.Empty).Trim(UrlTrimmedCharacters);

        if (trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith("/", StringComparison.Ordinal)
            || trimmed.StartsWith("./", StringComparison.Ordinal)
            || trimmed.StartsWith("../", StringComparison.Ordinal))
            return true;

        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var scheme in new[] { "http://", "https://", "mailto:" })
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Anything with a scheme we did not list is rejected; bare relative paths are fine.
        return !Regex.IsMatch(trimmed, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:");
    }

    private static readonly char[] UrlTrimmedCharacters =
        Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray();

    [GeneratedRegex(@"[\t\n\r]")]
    private static partial Regex UrlIgnoredCharacters();

    [GeneratedRegex(@"<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>(?:""[^""]*""|'[^']*'|[^>""'])*?)(?<selfClose>/)?>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<quoted>[^']*)'|(?<bare>[^\s""'>]+))")]
    private static partial Regex AttributePattern();
}
