// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND MIT) also carries the source of AngleSharp.Js under
// Vendor/, which keeps its own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

#nullable enable
using System;
using System.Collections.Generic;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Tetitulz.Pdf.Plugins.HtmlScript
{
    /// <summary>
    /// Turns every <c>&lt;script src&gt;</c> into an inline script before the page is opened, so the
    /// script engine never needs — and is never given — a way to fetch anything.
    /// </summary>
    /// <remarks>
    /// <para><b>Why inline, rather than hand AngleSharp a loader.</b> A loader answers every request
    /// the page makes — scripts, but also stylesheets, images, frames and <c>XMLHttpRequest</c> —
    /// and the engine would then be the thing deciding what a page may reach. Inlining keeps the one
    /// decision where the managed engine already makes it: each source goes through
    /// <c>HtmlScriptRequest.LoadScript</c> (the SSRF-guarded remote loader and its budget, the base
    /// directory for local files), and the engine runs with resource loading off. With nothing to
    /// load through, a script's <c>XMLHttpRequest</c> reaches nothing.</para>
    ///
    /// <para><b>Order is kept.</b> A classic script runs where it stands; a <c>defer</c> script after
    /// the document is parsed, in document order — so those move to the end of the body. An
    /// <c>async</c> script runs whenever it arrives in a browser, which no page can rely on; it is
    /// run with the deferred ones. A <c>type="module"</c> script is not run, and says so.</para>
    /// </remarks>
    internal static class ScriptInliner
    {
        internal static string Inline(
            string html, Func<string, string?> loadScript, Action<string> report, int maxDepth)
        {
            IHtmlDocument document = new HtmlParser().ParseDocument(html);

            // Before anything recursive touches the tree — AngleSharp's serialiser and selector
            // engine both recurse, and a stack overflow cannot be caught (HtmlScriptLimits.MaxDocumentDepth).
            int depth = Depth(document);
            if (depth > maxDepth)
                throw new Tetitulz.Pdf.HtmlToPdf.HtmlToPdfException(
                    "The page nests elements " + depth + " deep, past HtmlScriptLimits.MaxDocumentDepth (" +
                    maxDepth + "), so its scripts were not run: walking a tree that deep could overflow " +
                    "the stack, which would end the process rather than the conversion.");
            var deferred = new List<IElement>();
            bool changed = false;

            foreach (IElement script in new List<IElement>(document.Scripts))
            {
                string? type = script.GetAttribute("type");
                if (string.Equals(type?.Trim(), "module", StringComparison.OrdinalIgnoreCase))
                {
                    report("A module script (type=\"module\") was not run: modules are not supported " +
                           "by the managed engine's script plugin.");
                    script.Remove();
                    changed = true;
                    continue;
                }

                string? src = script.GetAttribute("src");
                if (src == null) continue;

                // LoadScript reports its own refusals, so a null here has already been explained.
                string? code = loadScript(src);
                script.RemoveAttribute("src");
                script.TextContent = code == null ? "" : Escape(code);
                changed = true;

                if (script.HasAttribute("defer") || script.HasAttribute("async"))
                {
                    script.RemoveAttribute("defer");
                    script.RemoveAttribute("async");
                    deferred.Add(script);
                }
            }

            if (document.Body != null)
                foreach (IElement script in deferred)
                    document.Body.AppendChild(script);

            return changed ? document.ToHtml() : html;
        }

        /// <summary>The deepest element nesting in <paramref name="document"/>, measured with an explicit stack.</summary>
        internal static int Depth(IDocument document)
        {
            if (document.DocumentElement == null) return 0;
            int deepest = 0;
            var pending = new Stack<(IElement Element, int Depth)>();
            pending.Push((document.DocumentElement, 1));
            while (pending.Count > 0)
            {
                (IElement element, int depth) = pending.Pop();
                if (depth > deepest) deepest = depth;
                foreach (IElement child in element.Children) pending.Push((child, depth + 1));
            }
            return deepest;
        }

        /// <summary>
        /// Keeps inlined code from closing its own element: in an inline script the first
        /// <c>&lt;/script</c> ends it, wherever it stands. <c>&lt;\/script</c> means the same thing to
        /// JavaScript, since <c>\/</c> is <c>/</c> in a string, a regular expression or a comment.
        /// </summary>
        /// <remarks>
        /// A scan, where a case-insensitive pattern ran with no timeout over the page's code. The tag
        /// name is matched as HTML matches it, ASCII letters in either case, which is all the
        /// pattern's invariant-culture folding matched too.
        /// </remarks>
        internal static string Escape(string code)
        {
            int at = ClosingScriptTagAt(code, 0);
            if (at < 0) return code;

            var escaped = new System.Text.StringBuilder(code.Length + 8);
            int copied = 0;
            while (at >= 0)
            {
                escaped.Append(code, copied, at - copied).Append("<\\/script");
                copied = at + ClosingScriptTag.Length;
                at = ClosingScriptTagAt(code, copied);
            }
            return escaped.Append(code, copied, code.Length - copied).ToString();
        }

        private const string ClosingScriptTag = "</script";

        /// <summary>Where the next <c>&lt;/script</c>, its letters in either case, starts at or after <paramref name="from"/>, or -1.</summary>
        private static int ClosingScriptTagAt(string code, int from)
        {
            for (int i = code.IndexOf("</", from, StringComparison.Ordinal); i >= 0;
                 i = code.IndexOf("</", i + 2, StringComparison.Ordinal))
            {
                if (i + ClosingScriptTag.Length > code.Length) return -1;
                int k = 2;
                while (k < ClosingScriptTag.Length)
                {
                    char c = code[i + k];
                    if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                    if (c != ClosingScriptTag[k]) break;
                    k++;
                }
                if (k == ClosingScriptTag.Length) return i;
            }
            return -1;
        }
    }
}
