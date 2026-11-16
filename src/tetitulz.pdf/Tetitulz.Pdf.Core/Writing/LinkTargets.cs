// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Whether a link target taken from a converted file may be written as a link: the Builder's HTML
    /// exporter's <c>href</c>, its PDF exporter's <c>/URI</c> action, the XFA importer's link and the DOCX
    /// converter's hyperlink alike — one answer.
    /// </summary>
    /// <remarks>
    /// <para>Escaping stops a value breaking out of an attribute or a string; it does nothing about a
    /// <c>javascript:</c> target, which needs no quotes to run. Relative and fragment targets carry no scheme and
    /// are kept; an absolute one is kept only for the schemes a document link legitimately uses.</para>
    /// <para><b>The scheme is read as a browser reads it</b> (the WHATWG URL Standard's basic parser): leading
    /// and trailing C0 controls and spaces stripped, every ASCII tab and newline removed. Read raw, a target of
    /// <c>" javascript:alert(1)"</c> or <c>"java\tscript:alert(1)"</c> had no valid scheme, was taken for a
    /// relative target and kept — and a browser runs either.</para>
    /// <para>A target a caller passes to a public link API is the caller's own and is not judged here; the
    /// sanitizer's web-URI test answers a different question (is this a non-web leak) and keeps its own
    /// narrower list.</para>
    /// </remarks>
    internal static class LinkTargets
    {
        internal static bool IsSafe(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            string url = Normalised(uri);
            if (url.Length == 0) return false;
            int colon = url.IndexOf(':');
            if (colon < 0) return true;                       // relative, or a "#fragment"
            int slash = url.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0 && slash < colon) return true;     // the colon is inside a path or query
            string scheme = url.Substring(0, colon);
            for (int i = 0; i < scheme.Length; i++)
            {
                char c = scheme[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (i > 0 && ((c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.'));
                if (!ok) return true;                         // not a scheme at all: a relative target
            }
            return IsSafeScheme(scheme);
        }

        /// <summary>Whether <paramref name="scheme"/> is one a document link uses: http, https, mailto, ftp, ftps, tel.</summary>
        internal static bool IsSafeScheme(string scheme)
        {
            switch (scheme.ToLowerInvariant())
            {
                case "http": case "https": case "mailto": case "ftp": case "ftps": case "tel":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary><paramref name="uri"/> as the URL parser sees it: C0 controls and spaces trimmed from both ends, tabs and newlines removed.</summary>
        private static string Normalised(string uri)
        {
            int start = 0, end = uri.Length;
            while (start < end && uri[start] <= ' ') start++;
            while (end > start && uri[end - 1] <= ' ') end--;
            var sb = new System.Text.StringBuilder(end - start);
            for (int i = start; i < end; i++)
                if (uri[i] != '\t' && uri[i] != '\n' && uri[i] != '\r') sb.Append(uri[i]);
            return sb.ToString();
        }
    }
}
