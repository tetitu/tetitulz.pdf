// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// <c>::before</c> and <c>::after</c> — generated inline content.
    ///
    /// <para><b>The decline this replaces said "no pseudo-element box".</b> True of CSS's model,
    /// and not what the common case needs: <c>content: "→ "</c> or <c>content: "»"</c> generates an
    /// <i>inline</i> box, which in this document model is a text run at the front or back of the
    /// paragraph's runs. No box is required. The declines table's own note — "today's
    /// skip-on-selector-throw is correct" — described the cascade dropping these selectors into the
    /// same catch as genuinely unsupported ones, which is what made the two indistinguishable.</para>
    ///
    /// <para><b>What is not generated,</b> each reported rather than silently skipped:
    /// <c>content: url(…)</c> (an image, not a run), counters (<c>counter()</c> needs a document-wide
    /// counter model), and a pseudo-element the author made <c>display: block</c> — that one really
    /// does need a box, and generating an inline run for it would put the text on the wrong line.
    /// <c>attr()</c> <i>is</i> supported, because the value is on the element and needs no machinery
    /// at all.</para>
    /// </summary>
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>
        /// Longest generated string taken from one <c>content</c> declaration. It is author text
        /// and lands in a paragraph run; the cap is generous next to any real marker or quote.
        /// </summary>
        private const int MaxGeneratedContentLength = 4096;

        /// <summary>
        /// Adds the element's <c>::before</c> (or <c>::after</c>) generated run to
        /// <paramref name="para"/>, if it declares one.
        /// </summary>
        private void AddGeneratedContent(IElement el, M.Paragraph para, Style st, bool after)
        {
            string? declarations = el.GetAttribute(after ? AfterStyleAttribute : BeforeStyleAttribute);
            if (string.IsNullOrWhiteSpace(declarations)) return;

            string? content = DeclarationValue(declarations!, "content");
            if (content == null) return;

            // `content: none` and `content: normal` generate nothing — the CSS way of switching a
            // pseudo-element off, and common in a reset stylesheet. Reporting it would fire on
            // documents that are doing the right thing.
            string trimmed = content.Trim();
            if (Eq(trimmed, "none") || Eq(trimmed, "normal")) return;

            string display = DeclarationValue(declarations!, "display")?.Trim() ?? "";
            if (display.Length > 0 && !Eq(display, "inline"))
            {
                // A box that paints nothing — blank content, no background, border or size — is
                // nothing to skip: Bootstrap's clearfix (`display: table; content: " "`) on every
                // row and panel reported itself on every page, burying the reports that mattered.
                if (PaintsNothing(declarations!, trimmed)) return;
                ReportApproximation($"a ::{(after ? "after" : "before")} pseudo-element with " +
                             $"display:{display} was skipped: it needs a box of its own, and " +
                             "generating an inline run for it would put the text on the wrong line.");
                return;
            }

            if (!TryResolveContent(trimmed, el, after, out string text)) return;
            if (text.Length == 0) return;

            // Styled through the ordinary cascade machinery, so colour, weight and size on the
            // pseudo-element work exactly as they do on any run — rather than a second, narrower
            // styling path that would drift from it.
            Style pseudoStyle = st.Clone();
            ApplyInlineStyle(declarations, pseudoStyle);
            var run = MakeRun(text, pseudoStyle);

            // Always appended; CollectInline generates the before-run before it walks the
            // children, so document order falls out of the call order.
            para.Runs.Add(run);
        }

        /// <summary>
        /// Turns a <c>content</c> value into the string it generates. Returns <c>false</c> when the
        /// value names something this cannot generate, having reported it.
        /// </summary>
        private bool TryResolveContent(string value, IElement el, bool after, out string text)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < value.Length)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '"' || c == '\'')
                {
                    int end = FindStringEnd(value, i + 1, c);
                    if (end < 0)
                    {
                        // An unterminated string generates nothing — said, never an empty success.
                        ReportApproximation("a pseudo-element's content holds an unterminated string; nothing was generated for it.");
                        text = "";
                        return false;
                    }
                    sb.Append(UnescapeCssString(value.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                    continue;
                }

                if (string.Compare(value, i, "attr(", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int close = value.IndexOf(')', i);
                    if (close < 0) break;
                    string name = value.Substring(i + 5, close - i - 5).Trim().Trim('"', '\'');
                    sb.Append(el.GetAttribute(name) ?? "");
                    i = close + 1;
                    continue;
                }

                // counter(), counters(), url(), open-quote, image-set(), … — each needs machinery
                // this engine does not have, and half-generating the value would be worse than
                // generating none: a marker that silently loses its number reads as a layout bug.
                string token = value.Substring(i).Split(' ', '\t')[0];
                ReportApproximation($"a ::{(after ? "after" : "before")} content value used '{token}', " +
                             "which the managed engine cannot generate; the pseudo-element was " +
                             "skipped. Strings and attr() are supported.");
                text = "";
                return false;
            }

            text = sb.ToString();
            if (text.Length > MaxGeneratedContentLength)
                text = text.Substring(0, MaxGeneratedContentLength);
            return true;
        }

        /// <summary>Index of the closing quote, honouring backslash escapes.</summary>
        private static int FindStringEnd(string value, int from, char quote)
        {
            for (int i = from; i < value.Length; i++)
            {
                if (value[i] == '\\') { i++; continue; }
                if (value[i] == quote) return i;
            }
            return -1;
        }

        /// <summary>
        /// CSS string escapes: <c>\A</c> is a newline, <c>\\</c> a backslash, and a hex escape is a
        /// code point — U+FFFD for zero, a surrogate or one past U+10FFFF (CSS Syntax Level 3 §4.3.7).
        /// </summary>
        /// <remarks>
        /// A surrogate reached <c>char.ConvertFromUtf32</c>, whose <see cref="ArgumentOutOfRangeException"/>
        /// left the conversion: <c>content: "\D800"</c> in a page's own style sheet ended it with an
        /// exception that is not the library's. Zero and a code point past the range were dropped.
        /// </remarks>
        private static string UnescapeCssString(string raw)
        {
            if (raw.IndexOf('\\') < 0) return raw;

            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '\\') { sb.Append(raw[i]); continue; }
                if (++i >= raw.Length) break;

                int start = i;
                while (i < raw.Length && Uri.IsHexDigit(raw[i]) && i - start < 6) i++;
                if (i > start)
                {
                    // Six hex digits at most, so the parse cannot overflow.
                    int cp = int.Parse(raw.Substring(start, i - start), NumberStyles.HexNumber,
                                       CultureInfo.InvariantCulture);
                    if (cp == 0 || (cp >= 0xD800 && cp <= 0xDFFF) || cp > 0x10FFFF) sb.Append('\uFFFD');
                    else sb.Append(char.ConvertFromUtf32(cp));
                    // One optional whitespace terminates a hex escape and is consumed with it.
                    if (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
                    i--;
                    continue;
                }
                sb.Append(raw[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// A list item's <c>::marker</c>: its <c>content</c> — a string for the marker, or
        /// <c>none</c> for no marker — and its style over the item's, the marker's own
        /// (<c>ListItem.MarkerFormat</c>): a coloured or smaller bullet, a bold number. It failed
        /// the selector match and was dropped. A string is the marker even under
        /// <c>list-style: none</c>, which only sets what <c>normal</c> content is (CSS Lists 3
        /// §3.2); an item that is not a list item has no marker box to fill.
        /// </summary>
        private void ApplyMarkerStyle(IElement item, M.ListItem li, Style itemStyle)
        {
            string? declarations = item.GetAttribute(MarkerStyleAttribute);
            if (string.IsNullOrWhiteSpace(declarations)) return;

            string? content = DeclarationValue(declarations!, "content")?.Trim();
            if (content != null && HasMarkerBox(item))
            {
                if (Eq(content, "none")) li.Marker = "";
                else if (!Eq(content, "normal") && TryResolveContent(content, item, after: false, out string text))
                    li.Marker = text;
            }

            var markerStyle = itemStyle.Clone();
            ApplyInlineStyle(declarations, markerStyle);
            li.MarkerFormat = MakeRun("", markerStyle);
        }

        /// <summary>
        /// Whether <paramref name="content"/> is one or more quoted strings holding only white space,
        /// with white space between and around them. Scanned by hand.
        /// </summary>
        internal static bool IsOnlyBlankStrings(string content)
        {
            int i = 0, strings = 0;
            while (true)
            {
                while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
                if (i == content.Length) return strings > 0;
                char quote = content[i];
                if (quote != '"' && quote != '\'') return false;
                i++;
                while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
                if (i == content.Length || content[i] != quote) return false;
                i++;
                strings++;
            }
        }

        /// <summary>
        /// Whether a pseudo-element box would paint nothing: its <c>content</c> only blank strings,
        /// and nothing declared that draws — no background, border, outline, shadow, padding or size.
        /// </summary>
        private static bool PaintsNothing(string declarations, string content)
        {
            if (!IsOnlyBlankStrings(content)) return false;
            foreach (string part in SplitTopLevel(declarations, ch => ch == ';', keepEmpty: false))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                string name = part.Substring(0, colon).Trim().ToLowerInvariant();
                if (name.StartsWith("background", StringComparison.Ordinal) || name.StartsWith("border", StringComparison.Ordinal)
                    || name.StartsWith("outline", StringComparison.Ordinal) || name.StartsWith("padding", StringComparison.Ordinal)
                    || name == "box-shadow" || name.EndsWith("width", StringComparison.Ordinal) || name.EndsWith("height", StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// One property's value out of a declaration block, or null — the block cut at its top-level
        /// semicolons (<see cref="SplitTopLevel"/>), so <c>content: "a;b"</c> is one declaration.
        /// </summary>
        private static string? DeclarationValue(string declarations, string property)
        {
            foreach (string part in SplitTopLevel(declarations, ch => ch == ';', keepEmpty: false))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                if (!string.Equals(part.Substring(0, colon).Trim(), property,
                                   StringComparison.OrdinalIgnoreCase))
                    continue;
                return part.Substring(colon + 1).Trim();
            }
            return null;
        }

    }
}
