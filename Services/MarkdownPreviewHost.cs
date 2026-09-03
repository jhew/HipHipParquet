using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HipHipParquet.Services;

/// <summary>
/// Drives the markdown preview inside a <see cref="WebView2"/>. Both the docked panel and
/// the popped-out window use this, so the preview behaves identically in either place.
/// </summary>
/// <remarks>
/// The document is written to a per-instance temp folder that is mapped to a virtual host
/// rather than passed to NavigateToString. That gives the page a real origin, which is what
/// lets it load the bundled mermaid script; a string-navigated page is treated as
/// <c>about:blank</c> and cannot pull in local resources. Nothing leaves the machine —
/// off-host navigation is cancelled and handed to the user's browser instead.
/// </remarks>
public sealed class MarkdownPreviewHost : IDisposable
{
    private const string VirtualHost = "hhp-markdown-preview";
    private const string DocumentName = "preview.html";

    private readonly WebView2 _view;
    private readonly string _folder;
    private bool _initialized;
    private bool _disposed;

    public MarkdownPreviewHost(WebView2 view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _folder = Path.Combine(
            Path.GetTempPath(), "HipHipParquet", "preview", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Set when the WebView2 runtime could not be started; the caller surfaces it.</summary>
    public string? UnavailableReason { get; private set; }

    public async Task<bool> RenderAsync(string html)
    {
        if (_disposed)
            return false;

        if (!await EnsureInitializedAsync())
            return false;

        var target = Path.Combine(_folder, DocumentName);
        await File.WriteAllTextAsync(target, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // A changing query string forces a real reload; navigating to the same URL is a no-op.
        _view.CoreWebView2.Navigate($"https://{VirtualHost}/{DocumentName}?r={DateTime.UtcNow.Ticks}");
        return true;
    }

    private async Task<bool> EnsureInitializedAsync()
    {
        if (_initialized)
            return true;

        if (UnavailableReason != null)
            return false;

        try
        {
            Directory.CreateDirectory(_folder);
            CopyBundledAssets();

            // An explicit user-data folder keeps this working when the app is installed
            // somewhere the process cannot write, such as Program Files.
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(Path.GetTempPath(), "HipHipParquet", "webview2"));

            await _view.EnsureCoreWebView2Async(environment);

            var core = _view.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, _folder, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;

            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason = ex is WebView2RuntimeNotFoundException
                ? "The WebView2 runtime is not installed, so the rich preview is unavailable."
                : UserFacingError.Describe(ex);
            return false;
        }
    }

    /// <summary>Copies the bundled diagram renderer next to the document so it loads same-origin.</summary>
    private void CopyBundledAssets()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "Markdown", "mermaid.min.js");
        if (!File.Exists(source))
            return;

        var destination = Path.Combine(_folder, "mermaid.min.js");
        if (!File.Exists(destination))
            File.Copy(source, destination);
    }

    /// <summary>Only the local preview may render in-frame; real links go to the user's browser.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && uri.Host.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        OpenExternally(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return;

        if (parsed.Scheme != Uri.UriSchemeHttp
            && parsed.Scheme != Uri.UriSchemeHttps
            && parsed.Scheme != Uri.UriSchemeMailto)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Launching the browser is best-effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_initialized && _view.CoreWebView2 != null)
            {
                _view.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                _view.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }
        }
        catch
        {
            // The control may already be torn down.
        }

        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}
