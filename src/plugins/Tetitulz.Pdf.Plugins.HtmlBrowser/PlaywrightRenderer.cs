// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Net;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlBrowser
{
    /// <summary>
    /// Everything in this assembly that touches <b>Playwright</b>, in one place.
    /// </summary>
    /// <remarks>
    /// <para>Living in its own plugin package, it keeps <c>Microsoft.Playwright</c> — and with it a
    /// ~100 MB browser download — out of <c>Tetitulz.Pdf.HtmlToPdf</c>, whose default engine is the
    /// managed one.</para>
    /// <para><b>Change the guard on its own, never together with the dispatch around it.</b> The SSRF
    /// guard below (<see cref="ApplySsrfGuardAsync"/>) is the only thing stopping a crafted local
    /// HTML file from pulling <c>file:///etc/passwd</c> into the output, or a redirect from reaching
    /// a cloud metadata endpoint; a change that moves it and rewires its callers at once is how a
    /// hole gets in unnoticed.</para>
    /// <para>The three URL helpers travel with it because the browser path is their only caller —
    /// the managed engine performs no network I/O at all, which is why
    /// <c>RefuseManagedUrlConversion</c> exists rather than a managed URL path.</para>
    /// </remarks>
    internal static class PlaywrightRenderer
    {
        // SSRF / local-file-disclosure guard (scheme check on the top-level URL).
        // ConvertUrl only permits http/https; ConvertFile passes allowFileScheme=true for
        // the file:// URI it builds from a local path. http(s) URLs (top-level AND every
        // subresource/redirect) that resolve to private/loopback/metadata addresses are
        // blocked at request time by ApplySsrfGuardAsync (unless options.BlockPrivateNetworkAccess
        // is cleared).
        internal static void ValidateNavigationUrl(string url, bool allowFileScheme)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new HtmlToPdfException($"Invalid URL: '{url}'.");
            bool ok = uri.Scheme == Uri.UriSchemeHttp
                   || uri.Scheme == Uri.UriSchemeHttps
                   || (allowFileScheme && uri.Scheme == Uri.UriSchemeFile);
            if (!ok)
                throw new HtmlToPdfException(
                    $"URL scheme '{uri.Scheme}' is not allowed (SSRF/local-file protection). " +
                    "Only http/https are permitted for ConvertUrl; use ConvertFile for local files.");
        }

        /// <summary>
        /// Installs a request interceptor on the browser context that aborts any request —
        /// top-level navigation, subresource, or redirect target — whose host resolves to a
        /// loopback/private/link-local/metadata address, and blocks non-http(s) schemes
        /// (file:// allowed only on the ConvertFile path). A scheme check on the navigation URL
        /// alone would not cover subresources or redirects. <b>It is the request-level half of
        /// the guard</b>: it decides schemes and local files, and refuses a private host early with
        /// a named error. It does not see every connection a page causes — a beacon, a worker's
        /// WebSocket — and it answers before the browser connects; the connection-level half is
        /// <see cref="GuardedSocksProxy"/>, which the browser is launched behind. No-op when
        /// disabled via options.
        ///
        /// <para>When <paramref name="allowFileScheme"/> is set and
        /// <paramref name="fileBaseDirectory"/> is non-null, file:// requests are additionally
        /// confined to that directory (and its subtree): a crafted local HTML file cannot pull
        /// arbitrary local files (e.g. <c>file:///etc/passwd</c>) into the output. The top-level
        /// input file itself lives in that directory, so its own navigation is permitted.</para>
        /// </summary>
        internal static async Task ApplySsrfGuardAsync(
            IBrowserContext context, HtmlToPdfOptions options, bool allowFileScheme,
            string fileBaseDirectory)
        {
            if (!options.BlockPrivateNetworkAccess) return;

            string normalizedBaseDir = NormalizeBaseDirectory(fileBaseDirectory);

            // A WebSocket is not a request the route below sees: it has its own interception. A
            // socket the guard admits is connected to its server; any other is closed unopened, so a
            // page's script cannot reach a loopback or private host through ws:// where it cannot
            // through http://. A worker's socket is not routed at all; the proxy refuses that one.
            await context.RouteWebSocketAsync(_ => true, socket =>
            {
                if (IsWebSocketAllowed(socket.Url)) socket.ConnectToServer();
                else _ = socket.CloseAsync(new WebSocketRouteCloseOptions { Code = 1008, Reason = "blocked" });
            }).ConfigureAwait(false);

            await context.RouteAsync("**/*", async route =>
            {
                try
                {
                    if (IsRequestAllowed(route.Request.Url, allowFileScheme, normalizedBaseDir))
                        await route.ContinueAsync().ConfigureAwait(false);
                    else
                        await route.AbortAsync("addressunreachable").ConfigureAwait(false);
                }
                catch
                {
                    // On any guard error, fail closed (abort) rather than allow the request.
                    try { await route.AbortAsync().ConfigureAwait(false); } catch { /* route already handled */ }
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether the route lets a request through: http(s) to a host the pre-check passes, a
        /// file: URL inside the base directory when files are allowed, data:, about: and blob:.
        /// Anything else is aborted — another scheme, and a URL <see cref="Uri"/> cannot parse (.NET
        /// Framework refuses one past about 65,519 characters, which Chromium accepts), which no
        /// check could read; of those only a data: URL, which does no I/O and is often that long
        /// for an image, goes on.
        /// </summary>
        /// <param name="url">The request's URL.</param>
        /// <param name="allowFileScheme">Whether file: URLs may load at all.</param>
        /// <param name="normalizedBaseDir">The directory they are confined to (<see cref="NormalizeBaseDirectory"/>).</param>
        internal static bool IsRequestAllowed(string url, bool allowFileScheme, string normalizedBaseDir)
        {
            string raw = url ?? "";
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u))
                return raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

            if (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            {
                // Checked on every request and redirect, so a redirect to a private address is
                // refused here, by name. It is a PRE-CHECK: a bool about a hostname does not protect
                // the connection (CLAUDE.md). The connection is made by GuardedSocksProxy, to an
                // address it vetted itself.
                return PrivateAddressGuard.IsSafeHost(u.DnsSafeHost);
            }
            if (u.Scheme == Uri.UriSchemeFile)
                return allowFileScheme && IsFileUriWithinBase(u, normalizedBaseDir);
            // ftp:, ws:, chrome: and the rest are blocked; data/about/blob carry no network I/O.
            return u.Scheme == "data" || u.Scheme == "about" || u.Scheme == "blob";
        }
        // Canonicalises the file base directory to a full path with a trailing separator so
        // prefix comparison cannot be tricked by "/base/../etc" or a "/base-evil" sibling.
        // Returns null when no directory constraint applies.
        internal static string NormalizeBaseDirectory(string fileBaseDirectory)
        {
            if (string.IsNullOrEmpty(fileBaseDirectory)) return null;
            try
            {
                string full = Path.GetFullPath(fileBaseDirectory);
                if (full.Length == 0) return null;
                if (full[full.Length - 1] != Path.DirectorySeparatorChar
                    && full[full.Length - 1] != Path.AltDirectorySeparatorChar)
                    full += Path.DirectorySeparatorChar;
                return full;
            }
            catch
            {
                // Unparseable base directory → treat as "no file access permitted".
                return null;
            }
        }

        // Returns true when the file:// URI points at a local path inside the base directory (or
        // its subtree). A null base directory means no file access at all. The rule is Core's, the
        // one the managed engine asks: case counts where the file system's does, so on Linux a
        // sibling directory whose name differs only in case is outside.
        internal static bool IsFileUriWithinBase(Uri fileUri, string normalizedBaseDir)
        {
            if (normalizedBaseDir == null || !fileUri.IsFile) return false;
            try
            {
                return Core.IO.DirectoryContainment.IsInside(normalizedBaseDir, fileUri.LocalPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a page may open a WebSocket to <paramref name="url"/>: <c>ws</c> or <c>wss</c>, to
        /// a host that resolves only to public addresses — the request guard's own rule for
        /// <c>http</c> and <c>https</c>, and like it a pre-check ahead of the proxy's connection.
        /// </summary>
        internal static bool IsWebSocketAllowed(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri u)
               && (u.Scheme == "ws" || u.Scheme == "wss")
               && PrivateAddressGuard.IsSafeHost(u.DnsSafeHost);

        /// <summary>Chromium's switch that keeps WebRTC from sending UDP the proxy does not carry.</summary>
        internal const string WebRtcThroughTheProxyOnly = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";

        /// <summary>
        /// How <paramref name="engine"/> is launched: headless, Chromium in its sandbox unless
        /// <paramref name="chromiumSandbox"/> is off (the driver passes <c>--no-sandbox</c> unless asked
        /// for it), and behind <paramref name="proxyServer"/> when one is given, with what keeps the
        /// browser from reaching the network around it.
        /// </summary>
        internal static BrowserTypeLaunchOptions LaunchOptions(BrowserEngine engine, string proxyServer, bool chromiumSandbox = true)
        {
            var launch = new BrowserTypeLaunchOptions { Headless = true, ChromiumSandbox = chromiumSandbox };
            if (proxyServer == null) return launch;

            // "<-loopback>" takes loopback addresses out of the browser's implicit bypass list, so
            // a request to 127.0.0.1 reaches the proxy and is refused there.
            launch.Proxy = new Proxy { Server = proxyServer, Bypass = "<-loopback>" };

            // WebRTC sends UDP of its own (a STUN request to whatever address a page names as an
            // ICE server), which no proxy carries. Chromium is told to send none outside the proxy.
            launch.Args = new[] { WebRtcThroughTheProxyOnly };
            return launch;
        }

        /// <summary>
        /// Why <paramref name="engine"/> cannot render, or <c>null</c> when it can: Playwright prints
        /// a PDF from headless Chromium alone, so any other value — one cast from an integer outside
        /// the enum — is refused before a browser starts rather than inside <c>page.pdf()</c>.
        /// </summary>
        internal static string WhyTheEngineCannotRender(BrowserEngine engine)
            => engine == BrowserEngine.Chromium
                ? null
                : $"Browser engine {(int)engine} is not supported: Playwright prints a PDF from " +
                  "headless Chromium only. Use BrowserEngine.Chromium.";

        /// <summary>
        /// Launches the browser the options name, behind <paramref name="proxyServer"/> when one is
        /// given: every connection the browser makes, loopback included, then goes to that proxy
        /// (<see cref="GuardedSocksProxy"/>), which is where a page's network access is decided.
        /// </summary>
        internal static async Task<IBrowser> LaunchBrowserAsync(
            IPlaywright playwright, HtmlToPdfOptions options, string proxyServer = null)
        {
            string refusal = WhyTheEngineCannotRender(options.Engine);
            if (refusal != null)
                throw new HtmlToPdfException(refusal);
            IBrowserType browserType = playwright.Chromium;

            try
            {
                return await browserType.LaunchAsync(LaunchOptions(options.Engine, proxyServer, options.ChromiumSandbox)).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is HtmlToPdfException))
            {
                throw new HtmlToPdfException(
                    $"Failed to launch {options.Engine} browser. " +
                    "Ensure Playwright browsers are installed by running " +
                    "'pwsh bin/Debug/net8.0/playwright.ps1 install' or calling " +
                    "HtmlToPdfConverter.InstallBrowsers().",
                    ex);
            }
        }

        internal static async Task<byte[]> GeneratePdfAsync(IPage page, HtmlToPdfOptions options)
        {
            try
            {
                // page.pdf() applies print media unless the page emulates another; screen renders
                // the page as its window shows it.
                if (options.MediaType == HtmlMediaType.Screen)
                    await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Screen }).ConfigureAwait(false);

                var pdfTask = page.PdfAsync(new PagePdfOptions
                {
                    Width = options.PageWidth,
                    Height = options.PageHeight,
                    Margin = new Margin
                    {
                        Top = options.MarginTop,
                        Bottom = options.MarginBottom,
                        Left = options.MarginLeft,
                        Right = options.MarginRight
                    },
                    Landscape = options.Landscape,
                    PrintBackground = options.PrintBackground,
                    // Clamp to Chromium's supported range [0.1, 2.0]; an out-of-range
                    // value otherwise throws deep inside Playwright with an opaque error.
                    // Through the Builder's clamp rather than a local pair of literals, so the
                    // two engines cannot come to disagree about what a given Scale means —
                    // and so NaN lands on 1.0 here too, which Math.Max/Math.Min alone would not do.
                    Scale = (float)PdfExportOptions.ClampScale(options.Scale),
                    DisplayHeaderFooter = options.DisplayHeaderFooter,
                    HeaderTemplate = options.HeaderTemplate,
                    FooterTemplate = options.FooterTemplate
                });

                // PagePdfOptions exposes no timeout, so bound PDF generation here:
                // navigation is timeout-bounded but a page that hangs during print
                // layout would otherwise pin a browser process (and the sync caller
                // thread) indefinitely. The browser is closed by the caller's finally,
                // which faults the abandoned task; observe it to avoid an unobserved
                // exception.
                int timeoutMs = options.NavigationTimeoutMs > 0
                    ? options.NavigationTimeoutMs
                    : HtmlToPdfOptions.DefaultNavigationTimeoutMs;
                var completed = await Task.WhenAny(pdfTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != pdfTask)
                {
                    _ = pdfTask.ContinueWith(t => { _ = t.Exception; },
                        TaskScheduler.Default);
                    throw new HtmlToPdfException(
                        $"PDF generation timed out after {timeoutMs} ms.");
                }
                return await pdfTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is HtmlToPdfException))
            {
                throw new HtmlToPdfException("Failed to generate PDF from HTML.", ex);
            }
        }

        /// <summary>Renders an HTML string through a headless browser.</summary>
        /// <remarks>
        /// Untrusted HTML: <c>file://</c> subresources would read local files, so the guard is
        /// applied with <c>allowFileScheme: false</c> — a string input has no base directory that
        /// could legitimately be read from.
        /// </remarks>
        internal static Task<byte[]> RenderHtmlAsync(string htmlContent, HtmlToPdfOptions options)
            => RunInPageAsync(options, allowFileScheme: false, fileBaseDirectory: null,
                navigate: page => page.SetContentAsync(htmlContent, new PageSetContentOptions
                {
                    WaitUntil = WaitUntil(options),
                    Timeout = options.NavigationTimeoutMs
                }));

        /// <summary>Navigates to a URL and renders it.</summary>
        /// <remarks>
        /// <c>file://</c> is permitted only on the ConvertFile path (<paramref name="allowFileScheme"/>),
        /// and even then subresources are confined to <paramref name="fileBaseDirectory"/>.
        /// </remarks>
        internal static Task<byte[]> RenderUrlAsync(
            string url, HtmlToPdfOptions options, bool allowFileScheme, string fileBaseDirectory)
            => RunInPageAsync(options, allowFileScheme, fileBaseDirectory,
                navigate: page => page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntil(options),
                    Timeout = options.NavigationTimeoutMs
                }));

        /// <summary>
        /// The browser session both entry points run in: create, launch, new context with
        /// <c>JavaScriptEnabled</c>, SSRF guard, new page, navigate, generate, and close everything
        /// in reverse.
        /// </summary>
        /// <remarks>
        /// <b>This is a security scaffold, which is why it is one copy rather than two.</b> Every
        /// step here is a control: <c>JavaScriptEnabled = false</c> is what stops arbitrary script
        /// in untrusted HTML from running inside the browser process, and
        /// <see cref="ApplySsrfGuardAsync"/> is what stops it reaching the network or the disk. The
        /// two callers differ only in HOW they navigate and in what the guard is told to permit;
        /// written out twice, a control added to one copy and not the other would leave the second
        /// path silently weaker, with nothing failing.
        ///
        /// <para>The three nested <c>finally</c> blocks are the teardown order, and it matters:
        /// the context closes before the browser and the browser before the driver, so a failure
        /// part-way through does not leak a browser process.</para>
        ///
        /// <para>The two entry points keep their own remarks because they document OPPOSITE trust
        /// assumptions about <c>file://</c>, which is exactly what their guard arguments express.</para>
        /// </remarks>
        private static async Task<byte[]> RunInPageAsync(
            HtmlToPdfOptions options,
            bool allowFileScheme,
            string fileBaseDirectory,
            Func<IPage, Task> navigate)
        {
            // The connection-level guard: started before the browser and disposed after it, so no
            // connection of the browser's outlives the thing that vets it.
            using GuardedSocksProxy proxy = options.BlockPrivateNetworkAccess ? new GuardedSocksProxy() : null;
            var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            try
            {
                var browser = await LaunchBrowserAsync(playwright, options, proxy?.Server).ConfigureAwait(false);
                try
                {
                    var context = await browser.NewContextAsync(new BrowserNewContextOptions
                    {
                        JavaScriptEnabled = options.JavaScriptEnabled,
                        // A service worker's own fetches do not pass through the context's routes,
                        // so a page that registered one would fetch around the guard; a rendering
                        // needs none. Nor does it need a file the page asks the browser to download.
                        ServiceWorkers = ServiceWorkerPolicy.Block,
                        AcceptDownloads = false,
                    }).ConfigureAwait(false);
                    await ApplySsrfGuardAsync(context, options, allowFileScheme, fileBaseDirectory)
                        .ConfigureAwait(false);
                    try
                    {
                        var page = await context.NewPageAsync().ConfigureAwait(false);
                        await navigate(page).ConfigureAwait(false);

                        return await GeneratePdfAsync(page, options).ConfigureAwait(false);
                    }
                    finally { await context.CloseAsync().ConfigureAwait(false); }
                }
                finally { await browser.CloseAsync().ConfigureAwait(false); }
            }
            finally { playwright.Dispose(); }
        }

        /// <summary>The one mapping from <c>WaitForNetworkIdle</c> to Playwright's enum.</summary>
        private static WaitUntilState WaitUntil(HtmlToPdfOptions options)
            => options.WaitForNetworkIdle ? WaitUntilState.NetworkIdle : WaitUntilState.Load;

        // Serialize installs: Program.Main mutates process-wide / on-disk browser state, so
        // concurrent installs (or an install racing a conversion) can corrupt the shared install
        // directory. Call once at startup ideally.
        private static readonly object _installLock = new object();

        /// <summary>Installs Playwright browser binaries for the given engine.</summary>
        internal static void InstallBrowsers(BrowserEngine? engine)
        {
            string browserArg = engine.HasValue
                ? engine.Value.ToString().ToLowerInvariant()
                : null;

            var args = browserArg != null
                ? new[] { "install", browserArg }
                : new[] { "install" };

            int exitCode;
            lock (_installLock)
                exitCode = Microsoft.Playwright.Program.Main(args);
            if (exitCode != 0)
                throw new HtmlToPdfException(
                    $"Playwright browser installation failed with exit code {exitCode}.");
        }
    }
}
