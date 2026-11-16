// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Html
{
    /// <summary>
    /// <see cref="IImportPlugin"/> that converts HTML documents to PDF.
    /// Dispatched by <c>PdfOperations.Import(source, pdfOutput, "html")</c>.
    ///
    /// <para><b>The default engine is the pure-managed one, not Playwright.</b>
    /// <c>HtmlToPdfOptions.RenderEngine</c> defaults to <c>Managed</c> and this plugin does not
    /// override it, so an import needs no browser install, opens no network connection and runs
    /// in-process. Pass <c>RenderEngine = "Browser"</c> to select the browser path, which
    /// additionally requires the <c>Tetitulz.Pdf.Plugins.HtmlBrowser</c> plugin to be registered
    /// and throws if it is not — it never silently falls back.</para>
    ///
    /// The <c>source</c> stream must contain UTF-8 encoded HTML markup.
    ///
    /// Drop <c>Tetitulz.Pdf.Plugins.Html.dll</c> into the application plugin
    /// folder and call <c>PdfPluginHost.Instance.RegisterFromDirectory(pluginFolder)</c>
    /// to enable automatic HTML-to-PDF conversion without a compile-time dependency.
    ///
    /// <para><b>Recognised <see cref="ImportOptions.Extra"/> keys</b> (case-insensitive), each
    /// mapping onto the <c>HtmlToPdfOptions</c> property of the same name:
    /// <c>RenderEngine</c> (<c>"Browser"</c> or <c>"Managed"</c> — the managed engine needs no
    /// Playwright install and no network), <c>PageWidth</c>, <c>PageHeight</c>, <c>MarginTop</c>,
    /// <c>MarginBottom</c>, <c>MarginLeft</c>, <c>MarginRight</c> (CSS length strings such as
    /// <c>"8.5in"</c>), <c>Landscape</c> (<c>bool</c>) and <c>Scale</c> (positive number).
    /// </para>
    ///
    /// <para><b>Three settings are deliberately not reachable</b> and naming one is an error rather
    /// than a no-op: <c>JavaScriptEnabled</c>, <c>WaitForNetworkIdle</c> and
    /// <c>BlockPrivateNetworkAccess</c>. Plugin input is untrusted by nature, and these are the
    /// switches that keep imported HTML from scripting the headless browser into reading local
    /// files or internal endpoints. Refusing beats accepting-and-ignoring, which would read to the
    /// caller as though the sandbox had been turned off on request.</para>
    ///
    /// Or register explicitly at startup:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new HtmlImportPlugin());
    /// </code>
    /// </summary>
    public sealed class HtmlImportPlugin : IImportPlugin
    {
        public string Name        => "Tetitulz.Plugins.HtmlImport";
        // Returned to hosts and shown in plugin listings, so a stale value here misinforms a
        // caller directly rather than only a reader of this file.
        public string Description => "Converts HTML documents to PDF (pure-managed by default; " +
                                     "optionally a headless browser).";
        public Version Version    => new Version(1, 0, 0);

        public string FormatKey    => "html";
        public string SourceMimeType => "text/html";

        public void Initialize() { }
        public void Shutdown()   { }

        public void Import(Stream source, Stream pdfOutput, ImportOptions options)
        {
            if (source == null)    throw new ArgumentNullException(nameof(source));
            if (pdfOutput == null) throw new ArgumentNullException(nameof(pdfOutput));

            var convertOptions = ReadOptions(options);

            string html;
            using (var reader = new StreamReader(source, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                html = reader.ReadToEnd();

            Tetitulz.Pdf.HtmlToPdf.HtmlToPdfConverter.ConvertHtml(html, pdfOutput, convertOptions);
        }

        /// <summary>
        /// Maps <see cref="ImportOptions.Extra"/> onto <c>HtmlToPdfOptions</c>, refusing anything it
        /// cannot honour. See the table on the class for the keys and for the three that are
        /// refused on purpose.
        /// </summary>
        private static Tetitulz.Pdf.HtmlToPdf.HtmlToPdfOptions ReadOptions(ImportOptions options)
        {
            const string Plugin = "HtmlImportPlugin";

            // Plugin input is untrusted by nature. Disable JavaScript so imported
            // HTML cannot script the headless browser to fetch local files /
            // internal resources (SSRF / data exfiltration); also skip the
            // network-idle wait so it can't stall on attacker-controlled subresources.
            // These are NOT overridable from the options bag — see the class doc.
            var convert = new Tetitulz.Pdf.HtmlToPdf.HtmlToPdfOptions
            {
                JavaScriptEnabled = false,
                WaitForNetworkIdle = false
            };

            var extra = options?.Extra;
            if (extra == null || extra.Count == 0) return convert;

            PluginOptionReader.RejectUnknownKeys(Plugin, extra,
                nameof(convert.RenderEngine),
                nameof(convert.PageWidth),  nameof(convert.PageHeight),
                nameof(convert.MarginTop),  nameof(convert.MarginBottom),
                nameof(convert.MarginLeft), nameof(convert.MarginRight),
                nameof(convert.Landscape),  nameof(convert.Scale));

            var engine = convert.RenderEngine;
            string? pageWidth = convert.PageWidth, pageHeight = convert.PageHeight;
            string? marginTop = convert.MarginTop, marginBottom = convert.MarginBottom;
            string? marginLeft = convert.MarginLeft, marginRight = convert.MarginRight;
            bool landscape = convert.Landscape;
            double scale = convert.Scale;

            PluginOptionReader.ReadEnum(Plugin, extra, nameof(convert.RenderEngine), ref engine);
            PluginOptionReader.ReadString(Plugin, extra, nameof(convert.PageWidth),    ref pageWidth);
            PluginOptionReader.ReadString(Plugin, extra, nameof(convert.PageHeight),   ref pageHeight);
            PluginOptionReader.ReadString(Plugin, extra, nameof(convert.MarginTop),    ref marginTop);
            PluginOptionReader.ReadString(Plugin, extra, nameof(convert.MarginBottom), ref marginBottom);
            PluginOptionReader.ReadString(Plugin, extra, nameof(convert.MarginLeft),   ref marginLeft);
            PluginOptionReader.ReadString(Plugin, extra, nameof(convert.MarginRight),  ref marginRight);
            PluginOptionReader.ReadBool(Plugin, extra, nameof(convert.Landscape),      ref landscape);
            PluginOptionReader.ReadPositiveDouble(Plugin, extra, nameof(convert.Scale), ref scale);

            // The `!`s are load-bearing rather than lazy: `ReadString` cannot assign null — it
            // returns early when the key is absent and throws when the value is not a non-empty
            // string — and each local was seeded from a property that carries a non-null default
            // ("8.5in", "0.4in", …). So the value here is non-null on every path, which the ref
            // parameter's own type cannot express.
            convert.RenderEngine = engine;
            convert.PageWidth    = pageWidth!;
            convert.PageHeight   = pageHeight!;
            convert.MarginTop    = marginTop!;
            convert.MarginBottom = marginBottom!;
            convert.MarginLeft   = marginLeft!;
            convert.MarginRight  = marginRight!;
            convert.Landscape    = landscape;
            convert.Scale        = scale;
            return convert;
        }
    }
}
