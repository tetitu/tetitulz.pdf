// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Text;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ====================================================== CSS declaration lexing (shared)

        /// <summary>
        /// Where a trimmed declaration value's <c>!important</c> begins — the bang, the whitespace CSS
        /// permits before the keyword, the keyword in any ASCII case — or −1. Scanned from the end by
        /// hand: the value is the document's, and the keyword's case is compared ordinally, never under
        /// the current culture.
        /// </summary>
        internal static int ImportantSuffixStart(string value)
        {
            const string keyword = "important";
            int k = value.Length - keyword.Length;
            if (k < 1 || string.Compare(value, k, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return -1;
            int j = k - 1;
            while (j >= 0 && char.IsWhiteSpace(value[j])) j--;
            return j >= 0 && value[j] == '!' ? j : -1;
        }

        /// <summary>
        /// Splits a <c>;</c>-separated declaration list, respecting parentheses and quotes.
        ///
        /// <para>A naive <c>Split(';')</c> tears apart the two values that most often contain a
        /// semicolon — <c>url(data:image/png;base64,…)</c> and a quoted <c>font-family</c> — so
        /// the depth/quote tracking is not defensive polish, it is what makes those values
        /// survive at all.</para>
        /// </summary>
        private static IEnumerable<string> SplitDeclarations(string? style)
            => string.IsNullOrEmpty(style)
                ? Array.Empty<string>()
                : SplitTopLevel(style!, ch => ch == ';', keepEmpty: false);

        /// <summary>
        /// <paramref name="text"/> cut at every separator that stands outside parentheses and outside
        /// a quoted string — the one reading behind declarations (<c>;</c>), top-level tokens
        /// (white space) and every comma list (<c>box-shadow</c>, a media query list, <c>src</c> and
        /// <c>srcset</c>). <paramref name="keepEmpty"/> keeps a segment with nothing in it.
        /// </summary>
        /// <remarks>
        /// <para><b>One cutter for every list.</b> A copy that ignores quotes cuts a list at a comma
        /// inside a quoted string, and one whose depth goes below zero at a stray <c>)</c> splits
        /// nothing after it. A <c>)</c> with nothing open is passed over here.</para>
        /// </remarks>
        private static List<string> SplitTopLevel(string text, Func<char, bool> isSeparator, bool keepEmpty)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
                if (ch == '"' || ch == '\'') { quote = ch; continue; }
                if (ch == '(') depth++;
                else if (ch == ')') { if (depth > 0) depth--; }
                else if (depth == 0 && isSeparator(ch))
                {
                    if (keepEmpty || i > start) parts.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (keepEmpty || start < text.Length) parts.Add(text.Substring(start));
            return parts;
        }

        /// <summary>
        /// The index of the <c>)</c> that closes the <c>(</c> at <paramref name="open"/>, or −1 when
        /// none does — a CSS function's argument (<c>url(…)</c>, <c>repeat(…)</c>), a media feature,
        /// a <c>:not(…)</c>. The one matcher.
        /// </summary>
        private static int ClosingParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// Splits one declaration into its property, its value and whether it carried
        /// <c>!important</c> — which is stripped from <paramref name="value"/>.
        ///
        /// <para>This is the ONLY place a declaration is taken apart. A site doing it inline with
        /// <c>IndexOf(':')</c> and not knowing about <c>!important</c> would hand
        /// <c>color: red !important</c> to the colour parser (<c>Core.Text.CssColor.TryParse</c>)
        /// verbatim, to fail every arm and render <b>black</b>. Consolidating just one of those sites would have left the
        /// same bug reachable through the other three.</para>
        /// </summary>
        private static bool TrySplitDeclaration(string decl, out string property, out string value,
            out bool important)
        {
            property = ""; value = ""; important = false;
            if (string.IsNullOrEmpty(decl)) return false;
            int c = decl.IndexOf(':');
            if (c <= 0) return false;
            property = decl.Substring(0, c).Trim();
            if (property.Length == 0) return false;
            value = decl.Substring(c + 1).Trim();
            int bang = ImportantSuffixStart(value);
            if (bang >= 0)
            {
                important = true;
                value = value.Substring(0, bang).TrimEnd();
            }
            return true;
        }

        // Custom properties are case-SENSITIVE; ordinary CSS property names are ASCII
        // case-insensitive and are normalised to lower case so one dictionary can hold both.
        private static string DeclarationKey(string property) =>
            property.StartsWith("--", StringComparison.Ordinal) ? property : property.ToLowerInvariant();

        /// <summary>
        /// Top-level whitespace-separated tokens, keeping <c>url(…)</c> and quoted strings whole.
        /// Used to pick the colour out of a <c>background</c> shorthand.
        /// </summary>
        private static IEnumerable<string> SplitTopLevelTokens(string value)
            => SplitTopLevel(value, char.IsWhiteSpace, keepEmpty: false);





        /// <summary>
        /// The bidi control characters an element's <c>&lt;bdo&gt;</c>/<c>&lt;bdi&gt;</c> nature, its
        /// <c>unicode-bidi</c> declaration or its <c>dir</c> attribute calls for, or
        /// <c>('\0', '\0')</c> for none of them.
        ///
        /// <para>The <b>closer differs by kind</b>, which is the one thing easy to get wrong here:
        /// an embedding or override is ended by U+202C, an isolate by U+2069. Mixing them leaves
        /// the stack unbalanced and the rest of the paragraph in the wrong direction.</para>
        /// </summary>
        /// <param name="inherited">
        /// The style in force at this element, whose <c>Direction</c> carries the <b>computed</b>
        /// CSS <c>direction</c>. It is needed because <c>direction</c> inherits and
        /// <c>unicode-bidi</c>'s embedding direction is the computed one, while the two element-local
        /// sources read below (the <c>dir</c> attribute and an inline <c>direction</c> declaration)
        /// see only what was written on the element itself — and the cascade fold copies stylesheet
        /// declarations onto matching elements, never onto their descendants. Without it,
        /// <c>&lt;div dir="rtl"&gt;&lt;span style="unicode-bidi:isolate"&gt;…</c> emitted U+2066
        /// (LeftToRightIsolate) and resolved the isolate's interior at an even level, which is the
        /// exact reordering the author used the isolate to prevent.
        /// </param>
        /// <param name="el">The element whose own <c>dir</c> and inline <c>direction</c> are read.</param>
        /// <param name="tag">Its lower-cased tag name, for the elements that carry a default.</param>
        private static (char open, char close) ResolveBidiOverride(IElement el, string tag, Style inherited)
        {
            string? dir = el.GetAttribute("dir");
            string? mode = StyleValue(el, "unicode-bidi");

            // <bdo> IS an override — that is its entire meaning — and it requires a dir.
            if (tag == HtmlTags.Bdo)
            {
                if (Eq(dir, "rtl")) return (UnicodeBidi.Controls.RightToLeftOverride, UnicodeBidi.Controls.PopDirectionalFormatting);
                if (Eq(dir, "ltr")) return (UnicodeBidi.Controls.LeftToRightOverride, UnicodeBidi.Controls.PopDirectionalFormatting);
                return ('\0', '\0');
            }

            // <bdi> isolates; its direction is auto unless stated.
            if (tag == HtmlTags.Bdi)
            {
                if (Eq(dir, "rtl")) return (UnicodeBidi.Controls.RightToLeftIsolate, UnicodeBidi.Controls.PopDirectionalIsolate);
                if (Eq(dir, "ltr")) return (UnicodeBidi.Controls.LeftToRightIsolate, UnicodeBidi.Controls.PopDirectionalIsolate);
                return (UnicodeBidi.Controls.FirstStrongIsolate, UnicodeBidi.Controls.PopDirectionalIsolate);
            }

            // HTML's own stylesheet gives an element that carries `dir` `unicode-bidi: isolate`
            // (HTML Standard, Rendering › Bidirectional text), and an author's unicode-bidi
            // overrides it. So `dir` with no declared mode is an isolate, not `normal`: without
            // it, `<span dir="ltr">24/09/2026 07:28</span>` inside right-to-left text came out as
            // "07:28 24/09/2026", because the two numbers were ordered by the paragraph around
            // them — the one thing the author wrote `dir` to prevent.
            if (mode == null)
            {
                if (Eq(dir, "auto"))
                    return (UnicodeBidi.Controls.FirstStrongIsolate, UnicodeBidi.Controls.PopDirectionalIsolate);
                if (!Eq(dir, "ltr") && !Eq(dir, "rtl")) return ('\0', '\0');
                mode = "isolate";
            }

            // Element-local sources first — an explicit dir or direction on this element overrides
            // what it inherits — then the computed direction, which is what CSS actually specifies.
            bool rtl = Eq(dir, "ltr") || Eq(StyleValue(el, "direction"), "ltr")
                ? false
                : Eq(dir, "rtl") || Eq(StyleValue(el, "direction"), "rtl")
                  || inherited.Direction == M.TextDirection.RightToLeft;

            if (Eq(mode, "bidi-override"))
                return (rtl ? UnicodeBidi.Controls.RightToLeftOverride : UnicodeBidi.Controls.LeftToRightOverride,
                        UnicodeBidi.Controls.PopDirectionalFormatting);
            if (Eq(mode, "embed"))
                return (rtl ? UnicodeBidi.Controls.RightToLeftEmbedding : UnicodeBidi.Controls.LeftToRightEmbedding,
                        UnicodeBidi.Controls.PopDirectionalFormatting);
            if (Eq(mode, "isolate"))
                return (rtl ? UnicodeBidi.Controls.RightToLeftIsolate : UnicodeBidi.Controls.LeftToRightIsolate,
                        UnicodeBidi.Controls.PopDirectionalIsolate);
            if (Eq(mode, "plaintext"))
                return (UnicodeBidi.Controls.FirstStrongIsolate, UnicodeBidi.Controls.PopDirectionalIsolate);

            // `isolate-override` needs both an isolate and an override; the isolate is the part a
            // single wrapper can express, and it is the half that affects the surrounding text.
            if (Eq(mode, "isolate-override"))
                return (rtl ? UnicodeBidi.Controls.RightToLeftIsolate : UnicodeBidi.Controls.LeftToRightIsolate,
                        UnicodeBidi.Controls.PopDirectionalIsolate);

            return ('\0', '\0');   // `normal`, or something this engine does not implement
        }
    }
}
