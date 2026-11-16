// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Plugin that renders HTML to PDF through a <b>headless browser</b>, backing
    /// <see cref="HtmlRenderEngine.Browser"/>. <b>With no plugin registered the browser engine is
    /// unavailable and says so</b> — it never silently falls back to the managed engine.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a plugin.</b> The browser engine needs <c>Microsoft.Playwright</c>
    /// <i>and</i> a separately-downloaded browser (~100 MB, installed by <c>playwright install</c>).
    /// Carrying that as a package reference on <c>Tetitulz.Pdf.HtmlToPdf</c> would make every
    /// consumer inherit both — for an engine that is not the default. The criterion in
    /// CLAUDE.md ("What makes something a plugin rather than part of Base") is that a plugin earns
    /// its place by keeping a dependency out of the core package graph, and this is a larger case
    /// than the <c>Jint</c> one that produced <c>Plugins.JavaScript</c>.
    /// <c>Tetitulz.Pdf.Plugins.HtmlBrowser</c> ships the implementation.</para>
    /// <para><b>Why the managed engine did NOT also become a plugin.</b> Its only dependency is
    /// <c>AngleSharp</c> — a pure-managed HTML parser with no native assets and no runtime install —
    /// so isolating it would buy nothing, and CLAUDE.md already names that shape: "a plugin that
    /// isolates no dependency is an adapter, not an extension point". It is also the default engine,
    /// and a default that needs a second package installed before it works is not a default.</para>
    /// <para><b>Why the contract lives here rather than in <c>Core/Plugins/</c>,</b> where most
    /// plugin contracts live: it names <see cref="HtmlToPdfOptions"/>, a type in this assembly, and
    /// Core cannot reference it. Same reason <c>IJavaScriptPlugin</c> lives in
    /// <c>Tetitulz.Pdf.Base.Scripting</c> and <c>ISignPlugin</c> in
    /// <c>Tetitulz.Pdf.Signing.Plugins</c>.</para>
    /// <para><b>Untrusted input is the implementation's problem, and it is a real one.</b> An
    /// implementation renders attacker-controlled HTML and MUST NOT let it reach the local network,
    /// cloud metadata endpoints, or arbitrary local files. The shipped plugin does this with a
    /// request interceptor that re-resolves every host on every request and redirect — a pre-check,
    /// since Chromium resolves the name again to connect and a name rebound between the two is not
    /// caught (GAPS.md §17a) — confines <c>file://</c> to the input's own directory, blocks every
    /// other scheme, and fails <b>closed</b> on any guard error. Honour
    /// <see cref="HtmlToPdfOptions.BlockPrivateNetworkAccess"/> and
    /// <see cref="HtmlToPdfOptions.JavaScriptEnabled"/>.</para>
    /// </remarks>
    public interface IHtmlBrowserPlugin : IPdfPlugin
    {
        /// <summary>
        /// Renders <paramref name="request"/> to PDF bytes.
        /// </summary>
        /// <returns>The PDF. Never null on success.</returns>
        /// <exception cref="HtmlToPdfException">
        /// The browser could not be launched or the page could not be rendered. Implementations
        /// should surface a launch failure as this type with a message naming what to install.
        /// </exception>
        Task<byte[]> RenderAsync(HtmlBrowserRequest request);
    }

    /// <summary>
    /// What an <see cref="IHtmlBrowserPlugin"/> is asked to render: either inline HTML or a URL,
    /// plus the options and the file-access boundary.
    /// </summary>
    /// <remarks>
    /// Exactly one of <see cref="Html"/> and <see cref="Url"/> is non-null. The pair is a single
    /// request object rather than two interface methods so that adding a third source later does
    /// not break every implementation.
    /// </remarks>
    public sealed class HtmlBrowserRequest
    {
        private HtmlBrowserRequest(
            string? html, string? url, HtmlToPdfOptions options,
            bool allowFileScheme, string? fileBaseDirectory)
        {
            Html = html;
            Url = url;
            Options = options;
            AllowFileScheme = allowFileScheme;
            FileBaseDirectory = fileBaseDirectory;
        }

        /// <summary>A request to render an HTML string. No <c>file://</c> access is permitted.</summary>
        /// <remarks>
        /// A string input has no directory that could legitimately be read from, so
        /// <see cref="AllowFileScheme"/> is false — the distinction that stops untrusted HTML from
        /// pulling local files into the output.
        /// </remarks>
        public static HtmlBrowserRequest ForHtml(string html, HtmlToPdfOptions options)
            => new HtmlBrowserRequest(html, null, options, false, null);

        /// <summary>A request to navigate to a URL.</summary>
        /// <param name="allowFileScheme">
        /// True only on the local-file conversion path. When set,
        /// <paramref name="fileBaseDirectory"/> bounds which local files may be read.
        /// </param>
        /// <param name="url">The address to navigate to.</param>
        /// <param name="options">Conversion options carried through to the browser.</param>
        /// <param name="fileBaseDirectory">
        /// Root that local file reads are confined to. Meaningful only when
        /// <paramref name="allowFileScheme"/> is set.
        /// </param>
        public static HtmlBrowserRequest ForUrl(
            string url, HtmlToPdfOptions options, bool allowFileScheme, string? fileBaseDirectory)
            => new HtmlBrowserRequest(null, url, options, allowFileScheme, fileBaseDirectory);

        /// <summary>Inline HTML to render, or null when <see cref="Url"/> is set.</summary>
        public string? Html { get; }

        /// <summary>URL to navigate to, or null when <see cref="Html"/> is set.</summary>
        public string? Url { get; }

        /// <summary>Conversion options. Never null.</summary>
        public HtmlToPdfOptions Options { get; }

        /// <summary>Whether <c>file://</c> requests may be served at all.</summary>
        public bool AllowFileScheme { get; }

        /// <summary>
        /// Directory that bounds <c>file://</c> access when <see cref="AllowFileScheme"/> is set.
        /// Null means no local file access.
        /// </summary>
        public string? FileBaseDirectory { get; }
    }
}
