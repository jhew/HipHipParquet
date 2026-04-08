using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
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

        // Find setup .exe asset URL and SHA256SUMS URL from trusted GitHub domains.
        string? downloadUrl = null;
        string? checksumsUrl = null;
        var assets = json["assets"]?.AsArray();
        if (assets != null)
        {
            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var url = asset?["browser_download_url"]?.GetValue<string>();
                if (url == null || !IsTrustedGithubDownloadUrl(url)) continue;

                if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    downloadUrl ??= url;
                else if (name.EndsWith("-SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    checksumsUrl ??= url;
            }
        }

        return new UpdateInfo(latest, downloadUrl, checksumsUrl, ReleasesPageUrl);
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

    /// <summary>
    /// Verifies that the downloaded installer has a valid Authenticode signature.
    /// Returns true if the file is signed with a valid certificate chain; false otherwise.
    /// </summary>
    public static bool VerifyAuthenticodeSignature(string filePath)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            var valid = chain.Build(cert);
            if (!valid)
            {
                // If the only failures are offline/unknown revocation (e.g., CRL endpoint blocked),
                // treat the signature as acceptable — the cert chain is otherwise valid and the
                // file is not actively flagged as revoked.
                var onlyRevocationUnknown = chain.ChainStatus.All(s =>
                    s.Status == X509ChainStatusFlags.RevocationStatusUnknown ||
                    s.Status == X509ChainStatusFlags.OfflineRevocation);
                if (onlyRevocationUnknown)
                    return true;
            }
            return valid;
        }
        catch
        {
            // File is not signed or certificate is invalid
            return false;
        }
    }

    /// <summary>
    /// Downloads the SHA256SUMS file and verifies that the installer's hash matches.
    /// Returns true if the checksum matches; false if it doesn't match or can't be verified.
    /// </summary>
    public static async Task<bool> VerifyChecksumAsync(
        string installerPath,
        string checksumsUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsTrustedGithubDownloadUrl(checksumsUrl))
                return false;

            var checksumsText = await SharedClient.GetStringAsync(checksumsUrl, cancellationToken);
            var installerFileName = Path.GetFileName(installerPath);

            // The temp file name has format "BaseName-<guid>.exe" where <guid> is 32 hex chars (N format).
            // Strip the GUID suffix to recover the original release filename for lookup in the checksums file.
            var originalInstallerName = System.Text.RegularExpressions.Regex.Replace(
                installerFileName,
                @"-[0-9a-fA-F]{32}(?=\.[^.]+$)",
                string.Empty);

            string? expectedHash = null;
            foreach (var line in checksumsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Format: "<hash>  <filename>"
                var parts = line.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].Trim().Equals(originalInstallerName, StringComparison.OrdinalIgnoreCase))
                {
                    expectedHash = parts[0].Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(expectedHash))
                return false;

            var actualHash = await ComputeFileSha256Async(installerPath, cancellationToken);
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}

/// <summary>Describes an available update.</summary>
/// <param name="LatestVersion">The newer version on GitHub.</param>
/// <param name="InstallerUrl">Direct download URL for the setup .exe (may be null).</param>
/// <param name="ChecksumsUrl">Direct download URL for the SHA256SUMS file (may be null).</param>
/// <param name="ReleasePageUrl">Fallback: the GitHub releases page URL.</param>
public record UpdateInfo(Version LatestVersion, string? InstallerUrl, string? ChecksumsUrl, string ReleasePageUrl);
