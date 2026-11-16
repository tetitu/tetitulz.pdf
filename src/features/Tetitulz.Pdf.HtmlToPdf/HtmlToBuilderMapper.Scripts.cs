// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= page scripts

        /// <summary>
        /// The page after its own scripts have run, as HTML — or <c>null</c> when the page as
        /// written is what gets laid out (scripting off, no scripts, no plugin, or the plugin
        /// failed). Every <c>null</c> that loses something a script would have done is reported.
        /// </summary>
        /// <remarks>
        /// <para><b>Asked only when the page has a script.</b> A page without one never reaches the
        /// plugin host, so a host with a script plugin registered converts static pages exactly as
        /// one without — no second parse, no engine start-up.</para>
        ///
        /// <para><b>No plugin is not an error.</b> The managed engine is the default and lays a page
        /// out without its scripts perfectly well; what it must not do is let the caller believe the
        /// scripts ran. <c>&lt;noscript&gt;</c> is unaffected either way — it still follows
        /// <see cref="HtmlToPdfOptions.JavaScriptEnabled"/>, so a page's "please enable JavaScript"
        /// banner does not start appearing in every PDF made on a host that registered no plugin.</para>
        ///
        /// <para><b>A failing engine degrades to the page as written, reported</b> — the same rule as
        /// a script that throws in a browser: the page is still there, and the caller is told which
        /// page they got.</para>
        /// </remarks>
        private string? RunPageScripts(string html, IDocument dom)
        {
            if (!_options.JavaScriptEnabled) return null;

            int scripts = dom.Scripts.Length;
            if (scripts == 0) return null;

            IHtmlScriptPlugin? plugin = PluginHtmlScriptRunner.Resolve();
            if (plugin == null)
            {
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "The page's " + scripts + (scripts == 1 ? " script" : " scripts") + " did not run: " +
                    "the managed engine runs page JavaScript only through a registered " +
                    "IHtmlScriptPlugin (Tetitulz.Pdf.Plugins.HtmlScript). The output is the page as " +
                    "written, before anything its scripts would have changed.");
                return null;
            }

            string? result;
            try
            {
                result = plugin.RunScripts(new HtmlScriptRequest(
                    html, _options, LoadScript,
                    detail => ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated, detail)));
            }
            catch (Exception ex)
            {
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "The script plugin '" + plugin.Name + "' failed (" + ex.GetType().Name + ": " +
                    ex.Message + "). The output is the page as written, before its scripts.");
                return null;
            }

            if (result == null)
            {
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "The script plugin '" + plugin.Name + "' returned no document. The output is the " +
                    "page as written, before its scripts.");
            }
            return result;
        }

        /// <summary>
        /// Reports, once, that the page has a <c>&lt;canvas&gt;</c> — whose picture only a script
        /// draws, through a 2D or WebGL context neither this engine nor its script plugin has.
        /// </summary>
        /// <remarks>
        /// <b>Checked on the document that is laid out</b> — after the scripts, when they ran — so a
        /// canvas a script creates is reported too. Without this the element vanished in silence: a
        /// signature pad, a chart, the thing the page was printed for, gone from a PDF that reported
        /// nothing.
        /// </remarks>
        private void ReportCanvas(IDocument dom)
        {
            int canvases = CountCanvases(dom);
            if (canvases == 0) return;

            ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                "The page has " + canvases + (canvases == 1 ? " <canvas>" : " <canvas> elements") +
                ", which the managed engine does not draw: a canvas's picture is drawn by script " +
                "through a 2D or WebGL context, and neither this engine nor its script plugin has one. " +
                "Use HtmlRenderEngine.Browser for a page that needs it, or give the page an <img> " +
                "of the drawing instead.");
        }

        /// <summary>
        /// The <c>&lt;canvas&gt;</c> elements in <paramref name="dom"/>, counted with an explicit
        /// stack.
        /// </summary>
        /// <remarks>
        /// <b>Not <c>GetElementsByTagName</c></b>: AngleSharp walks the tree recursively for it, and
        /// this runs on every page before the mapper's own depth cap applies — so the hostile
        /// fixtures that nest elements tens of thousands deep would overflow the stack and take the
        /// test host down with them. An explicit stack has no depth.
        /// </remarks>
        private static int CountCanvases(IDocument dom)
        {
            if (dom.DocumentElement == null) return 0;
            int count = 0;
            var pending = new Stack<IElement>();
            pending.Push(dom.DocumentElement);
            while (pending.Count > 0)
            {
                IElement element = pending.Pop();
                if (string.Equals(element.LocalName, HtmlTags.Canvas, StringComparison.OrdinalIgnoreCase)) count++;
                foreach (IElement child in element.Children) pending.Push(child);
            }
            return count;
        }

        /// <summary>
        /// The text of a script a <c>&lt;script src&gt;</c> names — through the same remote and local
        /// paths as every other subresource — or <c>null</c>, reported, when it is not loaded.
        /// </summary>
        private string? LoadScript(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;

            byte[]? bytes;
            if (IsRemoteUrl(src))
            {
                bytes = LoadRemote(src, "script", BuilderDiagnosticKind.ContentApproximated);
            }
            else
            {
                string? full = ResolveLocalPath(src);
                if (full == null)
                {
                    ReportResourceDeclined("script", src);
                    return null;
                }
                try { bytes = File.ReadAllBytes(full); }
                catch (Exception ex)
                {
                    ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                        "The script '" + Excerpt(src) + "' was not loaded: " + ex.Message + ".");
                    return null;
                }
            }
            if (bytes == null) return null;

            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8,
                                                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
}
