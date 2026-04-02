using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace HipHipParquet.Services;

/// <summary>
/// Checks GitHub releases for a newer version and provides a download link.
/// </summary>
public static class UpdateService
{
    private const string GitHubOwner = "jhew";
    private const string GitHubRepo = "HipHipParquet";
    private static readonly string LatestReleaseApiUrl =
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    public static readonly string ReleasesPageUrl =
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

    private static readonly HttpClient SharedClient = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var version = GetCurrentVersion().ToString(3);
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HipHipParquet", version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// Queries the GitHub releases API and returns update info if a newer version is available.
    /// Returns null when the current version is already up-to-date.
    /// Throws on network/parse errors so callers can distinguish failures from "no update".
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var current = GetCurrentVersion();

        var jsonText = await SharedClient.GetStringAsync(LatestReleaseApiUrl, cancellationToken);
        return ParseUpdateInfo(jsonText, current);
    }

    /// <summary>
    /// Parses the JSON payload returned by the GitHub latest-release API.
    /// Returns null only when the current version is up-to-date.
    /// Throws <see cref="InvalidDataException"/> when required fields are missing/invalid.
    /// </summary>
    public static UpdateInfo? ParseUpdateInfo(string jsonText, Version current)
    {
        var json = JsonNode.Parse(jsonText)
            ?? throw new InvalidDataException("GitHub update response was empty.");

        var tagName = json["tag_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tagName))
            throw new InvalidDataException("GitHub update response did not contain tag_name.");

        var versionString = tagName.TrimStart('v', 'V');
        if (!Version.TryParse(versionString, out var latest))
            throw new InvalidDataException($"GitHub release tag '{tagName}' is not a valid version.");

        if (latest <= current)
            return null;

        // Find setup .exe asset URL from trusted GitHub domains.
        string? downloadUrl = null;
        var assets = json["assets"]?.AsArray();
        if (assets != null)
        {
            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var url = asset?["browser_download_url"]?.GetValue<string>();
                if (url != null && name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsTrustedGithubDownloadUrl(url))
                    {
                        downloadUrl = url;
                        break;
                    }
                }
            }
        }

        return new UpdateInfo(latest, downloadUrl, ReleasesPageUrl);
    }

    private static bool IsTrustedGithubDownloadUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               IsTrustedGithubDownloadUri(uri);
    }

    private static bool IsTrustedGithubDownloadUri(Uri uri)
    {
        return uri.IsAbsoluteUri &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateUniqueInstallerTempPath(Uri downloadUri)
    {
        var sourceFileName = Path.GetFileName(downloadUri.LocalPath);
        var extension = Path.GetExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".exe";

        var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "HipHipParquet-Setup";

        var uniqueFileName = $"{baseName}-{Guid.NewGuid():N}{extension}";
        return Path.Combine(Path.GetTempPath(), uniqueFileName);
    }

    /// <summary>
    /// Downloads the installer to a temp file, reporting progress (0-100).
    /// Returns the local path on success, or null on failure.
    /// </summary>
    public static async Task<string?> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Safety: only download from trusted GitHub domains
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                !IsTrustedGithubDownloadUrl(downloadUrl))
                return null;

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HipHipParquet", GetCurrentVersion().ToString(3)));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/octet-stream"));

            using var response = await client.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri == null || !IsTrustedGithubDownloadUri(finalUri))
                return null;

            var tempPath   = CreateUniqueInstallerTempPath(finalUri);
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var downloaded = 0L;

            await using var stream     = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[65536];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloaded += bytesRead;
                if (totalBytes > 0)
                    progress?.Report((int)(downloaded * 100 / totalBytes));
            }

            return tempPath;
        }
        catch { return null; }
    }

    /// <summary>Returns the current assembly version.</summary>
    public static Version GetCurrentVersion()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v ?? new Version(1, 0, 0, 0);
    }
}

/// <summary>Describes an available update.</summary>
/// <param name="LatestVersion">The newer version on GitHub.</param>
/// <param name="InstallerUrl">Direct download URL for the setup .exe (may be null).</param>
/// <param name="ReleasePageUrl">Fallback: the GitHub releases page URL.</param>
public record UpdateInfo(Version LatestVersion, string? InstallerUrl, string ReleasePageUrl);
