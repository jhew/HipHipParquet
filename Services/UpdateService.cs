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

    /// <summary>
    /// Queries the GitHub releases API and returns update info if a newer version is available.
    /// Returns null if up-to-date, offline, or on any error.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var current = GetCurrentVersion();
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HipHipParquet", current.ToString(3)));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            var json = JsonNode.Parse(
                await client.GetStringAsync(LatestReleaseApiUrl, cancellationToken));

            if (json == null) return null;

            var tagName = json["tag_name"]?.GetValue<string>();
            if (tagName == null) return null;

            var versionString = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionString, out var latest))
                return null;

            if (latest <= current)
                return null; // already up-to-date

            // Find setup .exe asset URL
            string? downloadUrl = null;
            var assets = json["assets"]?.AsArray();
            if (assets != null)
            {
                foreach (var asset in assets)
                {
                    var name = asset?["name"]?.GetValue<string>() ?? "";
                    var url  = asset?["browser_download_url"]?.GetValue<string>();
                    if (url != null && name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // Validate domain to prevent untrusted downloads
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                            (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                             uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                        {
                            downloadUrl = url;
                        }
                        break;
                    }
                }
            }

            return new UpdateInfo(latest, downloadUrl, ReleasesPageUrl);
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; } // silently absorb network/parse errors
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
                (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                 !uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                return null;

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HipHipParquet", GetCurrentVersion().ToString(3)));

            using var response = await client.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var fileName   = Path.GetFileName(uri.LocalPath);
            var tempPath   = Path.Combine(Path.GetTempPath(), fileName);
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
