// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlBrowser
{
    /// <summary>
    /// Renders HTML to PDF through a headless Chromium driven by
    /// Microsoft Playwright — the implementation behind <see cref="HtmlRenderEngine.Browser"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Register it before converting with the browser engine:</b></para>
    /// <code>
    /// PdfPluginHost.Instance.Register(new PlaywrightHtmlBrowserPlugin());
    /// PlaywrightHtmlBrowserPlugin.InstallBrowsers();          // once, if not already installed
    /// </code>
    /// <para>Or let <c>PdfPluginHost.RegisterFromLoadedAssemblies()</c> find it — this assembly
    /// carries <c>PdfPluginAssemblyAttribute</c>, so referencing the package is enough to make it
    /// discoverable.</para>
    /// <para><b>Why a separate package.</b> Playwright brings a ~100 MB browser download installed
    /// out-of-band. Keeping it here means a consumer of <c>Tetitulz.Pdf.HtmlToPdf</c> — whose default
    /// engine is the pure-managed one and needs nothing installed — does not inherit either.</para>
    /// <para><b>It renders untrusted input, and the guard is not optional.</b>
    /// <c>PlaywrightRenderer</c> installs a request interceptor that re-resolves every host on every
    /// request and redirect (so a redirect to a private address is caught). It is a pre-check:
    /// Chromium resolves the name again to connect, so a name rebound to a private address between
    /// the two is not caught — Playwright exposes no connect hook (GAPS.md §17a). It confines
    /// <c>file://</c> to the input file's own directory, blocks every other scheme, and aborts on any
    /// guard error — failing closed. Honours
    /// <see cref="HtmlToPdfOptions.BlockPrivateNetworkAccess"/> and
    /// <see cref="HtmlToPdfOptions.JavaScriptEnabled"/>.</para>
    /// </remarks>
    public sealed class PlaywrightHtmlBrowserPlugin : IHtmlBrowserPlugin
    {
        /// <inheritdoc/>
        public string Name => "Playwright HTML browser renderer";

        /// <inheritdoc/>
        public string Description =>
            "Renders HTML to PDF with a headless Chromium via Microsoft Playwright.";

        /// <inheritdoc/>
        public Version Version => new Version(1, 0, 0);

        /// <inheritdoc/>
        public void Initialize() { }

        /// <inheritdoc/>
        public void Shutdown() { }

        /// <inheritdoc/>
        public Task<byte[]> RenderAsync(HtmlBrowserRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            HtmlToPdfOptions options = request.Options ?? HtmlToPdfOptions.Default;

            if (request.Url != null)
                return PlaywrightRenderer.RenderUrlAsync(
                    request.Url, options, request.AllowFileScheme, request.FileBaseDirectory);

            return PlaywrightRenderer.RenderHtmlAsync(request.Html ?? string.Empty, options);
        }

        /// <summary>
        /// Installs the Playwright browser binaries. Call once before the first conversion.
        /// </summary>
        /// <remarks>
        /// <c>HtmlToPdfConverter.InstallBrowsers</c> throws, with a message naming this method.
        /// </remarks>
        public static void InstallBrowsers(BrowserEngine? engine = null)
            => PlaywrightRenderer.InstallBrowsers(engine);
    }
}
