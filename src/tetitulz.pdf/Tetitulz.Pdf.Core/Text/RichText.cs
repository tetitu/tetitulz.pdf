// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The plain text of a rich text string — a markup annotation's <c>/RC</c> (ISO 32000-2 Table 172), a
    /// variable-text field's <c>/RV</c> (Table 228) — which ISO 32000-2 defines by the XFA 3.3 rich text
    /// reference: the text a reader shows, whitespace collapsed to single spaces and trimmed. The FreeText
    /// display's fallback and PDF/UA-2's textual equivalence of <c>/RC</c> with <c>/Contents</c> and of
    /// <c>/RV</c> with <c>/V</c> (ISO 14289-2 §8.9.2.3, §8.10.3.3) read it alike.
    /// </summary>
    /// <remarks>
    /// <para>XFA 3.3 chapter 27 names the XHTML it supports: <c>p</c> and <c>br</c> break the text, <c>a</c>,
    /// <c>b</c>, <c>i</c>, <c>span</c>, <c>sub</c> and <c>sup</c> style it in place, <c>html</c> and <c>body</c>
    /// only contain it, and "unrecognized elements are suppressed" — read here to their text, but for
    /// <c>head</c>, whose content is no displayable content. A break reads as a space.</para>
    /// <para>One pass, linear in the length: a tag runs to the first <c>&gt;</c> outside a quoted attribute
    /// value; a comment, a processing instruction and a declaration are dropped; a CDATA section is its text;
    /// the five XML entities, <c>&amp;nbsp;</c> and a numeric character reference are decoded, and anything
    /// else after an <c>&amp;</c> is the text as written. Tolerant: text that is not well-formed XML still has
    /// its text read out.</para>
    /// </remarks>
    internal static class RichText
    {
        /// <summary>The most bytes a rich text stream is decoded to: a value a reader shows in a field or a popup.</summary>
        internal const int MaxStreamBytes = 4 * 1024 * 1024;

        /// <summary>The plain text of <paramref name="rich"/>, collapsed and trimmed; empty for null or empty.</summary>
        internal static string ToPlain(string rich)
        {
            if (string.IsNullOrEmpty(rich)) return string.Empty;

            var sb = new StringBuilder(rich.Length);
            int head = 0;
            int i = 0, n = rich.Length;
            while (i < n)
            {
                char c = rich[i];
                if (c == '<')
                {
                    if (At(rich, i, "<!--")) { i = After(rich, i + 4, "-->"); continue; }
                    if (At(rich, i, "<![CDATA["))
                    {
                        int end = rich.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        if (end < 0) end = n;
                        if (head == 0) sb.Append(rich, i + 9, end - (i + 9));
                        i = Math.Min(n, end + 3);
                        continue;
                    }
                    if (At(rich, i, "<?")) { i = After(rich, i + 2, "?>"); continue; }
                    if (At(rich, i, "<!")) { i = After(rich, i + 2, ">"); continue; }

                    int close = TagEnd(rich, i + 1);
                    if (close < 0)
                    {
                        if (head == 0) sb.Append(rich, i, n - i);   // no tag closes: the rest is text
                        break;
                    }
                    string name = TagName(rich, i + 1, close, out bool isEnd);
                    bool selfClosing = rich[close - 1] == '/';
                    if (name == "head")
                    {
                        if (isEnd) head = Math.Max(0, head - 1);
                        else if (!selfClosing) head++;
                    }
                    else if (name == "p" || name == "br")
                        sb.Append(' ');
                    i = close + 1;
                    continue;
                }
                if (head > 0) { i++; continue; }
                if (c == '&' && TryEntity(rich, i, out string decoded, out int next))
                {
                    sb.Append(decoded);
                    i = next;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return Collapsed(sb.ToString());
        }

        /// <summary>The plain text of a rich text string or stream, or <see langword="null"/> with <paramref name="unreadable"/> saying why.</summary>
        internal static string ToPlain(PdfObject value, out string unreadable)
        {
            unreadable = null;
            string text = TextOf(value, out unreadable);
            return text == null ? null : ToPlain(text);
        }

        /// <summary>
        /// A text string's text, or a text stream's — decoded under <see cref="MaxStreamBytes"/> and read as a
        /// text string's bytes are (§7.9.2.2) — or <see langword="null"/>, with <paramref name="unreadable"/>
        /// saying why when a stream would not decode.
        /// </summary>
        internal static string TextOf(PdfObject value, out string unreadable)
        {
            unreadable = null;
            if (value is PdfString s) return s.GetText();
            if (!(value is PdfStream stream)) return null;
            if (!stream.TryGetDecodedData(MaxStreamBytes, out byte[] bytes, out string reason))
            {
                unreadable = "a text stream was not decoded (" + reason + ")";
                return null;
            }
            return new PdfString(bytes).GetText();
        }

        /// <summary>Each run of whitespace one space, the ends trimmed: how rich text and plain text are compared.</summary>
        internal static string Collapsed(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = true;
                    continue;
                }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool At(string s, int i, string token)
            => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

        /// <summary>Just past the first <paramref name="terminator"/> from <paramref name="from"/>, or the end.</summary>
        private static int After(string s, int from, string terminator)
        {
            int at = s.IndexOf(terminator, Math.Min(from, s.Length), StringComparison.Ordinal);
            return at < 0 ? s.Length : at + terminator.Length;
        }

        /// <summary>The <c>&gt;</c> closing a tag, a quoted attribute value stepped over; -1 when none does.</summary>
        private static int TagEnd(string s, int from)
        {
            char quote = '\0';
            for (int j = from; j < s.Length; j++)
            {
                char c = s[j];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '"' || c == '\'') quote = c;
                else if (c == '>') return j;
            }
            return -1;
        }

        /// <summary>A tag's local name in lower case — no namespace prefix — and whether it is an end tag.</summary>
        private static string TagName(string s, int from, int close, out bool isEnd)
        {
            int j = from;
            isEnd = j < close && s[j] == '/';
            if (isEnd) j++;
            int start = j;
            while (j < close && !char.IsWhiteSpace(s[j]) && s[j] != '/' && s[j] != '>') j++;
            int colon = s.LastIndexOf(':', j - 1, Math.Max(0, j - start));
            if (colon >= start) start = colon + 1;
            return s.Substring(start, j - start).ToLowerInvariant();
        }

        /// <summary>An entity at <paramref name="at"/>: the five XML names, <c>nbsp</c>, or a numeric reference to a character.</summary>
        private static bool TryEntity(string s, int at, out string decoded, out int next)
        {
            decoded = null;
            next = at + 1;
            int semicolon = s.IndexOf(';', at + 1, Math.Min(12, s.Length - at - 1));
            if (semicolon < 0) return false;
            string name = s.Substring(at + 1, semicolon - at - 1);
            switch (name)
            {
                case "amp": decoded = "&"; break;
                case "lt": decoded = "<"; break;
                case "gt": decoded = ">"; break;
                case "quot": decoded = "\""; break;
                case "apos": decoded = "'"; break;
                case "nbsp": decoded = " "; break;
                default:
                    if (name.Length < 2 || name[0] != '#') return false;
                    bool hex = name[1] == 'x' || name[1] == 'X';
                    string digits = name.Substring(hex ? 2 : 1);
                    if (digits.Length == 0
                        || !int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                            CultureInfo.InvariantCulture, out int code)
                        || code <= 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                        return false;
                    decoded = char.ConvertFromUtf32(code);
                    break;
            }
            next = semicolon + 1;
            return true;
        }
    }
}
