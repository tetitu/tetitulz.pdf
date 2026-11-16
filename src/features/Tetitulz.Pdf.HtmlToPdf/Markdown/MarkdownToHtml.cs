// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.HtmlToPdf.Markdown
{
    /// <summary>
    /// Converts a documented subset of CommonMark (plus GFM tables) to HTML.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it emits HTML rather than building a <c>Document</c>.</b> The obvious design —
    /// map Markdown straight onto <c>DocumentBuilder</c> — is a second implementation of every
    /// block-styling decision <c>HtmlToBuilderMapper</c> already owns: heading sizes and their
    /// <c>/H1</c>–<c>/H6</c> roles, list nesting and markers, <c>pre</c>/<c>code</c> monospace with
    /// preserved whitespace, blockquote indent, rules, tables. Two mappers would answer those
    /// differently the first time either was touched. Emitting HTML means Markdown inherits the
    /// tagged output, the RTL handling, the font resolution and the pagination that pipeline already
    /// has, for free and permanently.</para>
    ///
    /// <para><b>And why it lives inside <c>Tetitulz.Pdf.HtmlToPdf</c>.</b> A separate
    /// <c>MarkdownToPdf</c> feature would need <c>MarkdownToPdf → HtmlToPdf</c> to reuse that
    /// pipeline — a feature→feature edge CLAUDE.md bans outright and
    /// <c>AssemblyLayeringTests</c> fails the build on.</para>
    ///
    /// <para><b>This is a subset, and it says so rather than pretending.</b> What is <i>not</i>
    /// implemented: reference links and reference images, setext headings, HTML blocks and inline
    /// HTML pass-through, entity references, autolinks in angle brackets, link titles, nested
    /// emphasis beyond one level, loose-vs-tight list semantics, and footnotes. Anything unrecognised
    /// is emitted as literal text, escaped — never silently dropped, which is the failure mode that
    /// loses a caller's content.</para>
    /// </remarks>
    public static class MarkdownToHtml
    {
        /// <summary>Longest run of input lines this will process, as a DoS bound.</summary>
        public const int MaxLines = 200_000;

        /// <summary>Deepest list nesting honoured; deeper items are flattened to this level.</summary>
        public const int MaxListDepth = 16;

        /// <summary>Converts Markdown to an HTML fragment wrapped in a minimal document.</summary>
        /// <param name="markdown">The Markdown source. Null is treated as empty.</param>
        /// <param name="title">Document title for the <c>&lt;title&gt;</c> element, or null.</param>
        public static string Convert(string? markdown, string? title = null)
        {
            var body = new StringBuilder();
            AppendBody(body, markdown);

            var html = new StringBuilder();
            // Void elements are self-closed, so the output is well-formed XML as well as valid
            // HTML5. Both AngleSharp and a browser accept either spelling, and the stricter one
            // means a caller — or a test — can parse the result with an XML reader instead of
            // substring-matching it.
            html.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\" />\n");
            // `title != null &&` rather than `!string.IsNullOrEmpty(title)`: the netstandard2.0
            // reference assemblies carry no [NotNullWhen(false)] on IsNullOrEmpty, so it narrows on
            // the net10.0 leg and warns on the other — the exact split CLAUDE.md records measuring
            // on Signing (9 sites that warned on netstandard2.0 alone).
            if (title != null && title.Length > 0)
                html.Append("<title>").Append(Escape(title)).Append("</title>\n");
            html.Append("</head>\n<body>\n").Append(body).Append("</body>\n</html>\n");
            return html.ToString();
        }

        // ----------------------------------------------------------------
        //  Block structure
        // ----------------------------------------------------------------

        private static void AppendBody(StringBuilder html, string? markdown)
        {
            string[] lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int count = Math.Min(lines.Length, MaxLines);

            var openLists = new List<bool>();   // true = ordered, one entry per open level
            bool inParagraph = false;
            bool inQuote = false;

            for (int i = 0; i < count; i++)
            {
                string line = lines[i];

                // Fenced code block: everything to the closing fence is literal, so it is handled
                // here rather than by any inline rule.
                if (IsFence(line, out string fence, out string language))
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseLists(html, openLists);
                    CloseQuote(html, ref inQuote);

                    html.Append("<pre><code");
                    if (language.Length > 0) html.Append(" class=\"language-").Append(Escape(language)).Append('"');
                    html.Append('>');

                    for (i++; i < count; i++)
                    {
                        if (IsClosingFence(lines[i], fence)) break;
                        html.Append(Escape(lines[i])).Append('\n');
                    }
                    html.Append("</code></pre>\n");
                    continue;
                }

                string trimmed = line.TrimStart();

                if (trimmed.Length == 0)
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseLists(html, openLists);
                    CloseQuote(html, ref inQuote);
                    continue;
                }

                // Blockquote: strip one level of "> " and fall through, so a quoted heading or list
                // is still a heading or list rather than quoted literal text.
                bool quoted = trimmed.StartsWith(">", StringComparison.Ordinal);
                if (quoted)
                {
                    trimmed = trimmed.Substring(1).TrimStart();
                    if (!inQuote)
                    {
                        CloseParagraph(html, ref inParagraph);
                        CloseLists(html, openLists);
                        html.Append("<blockquote>\n");
                        inQuote = true;
                    }
                }
                else if (inQuote)
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseQuote(html, ref inQuote);
                }

                if (IsThematicBreak(trimmed))
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseLists(html, openLists);
                    html.Append("<hr />\n");
                    continue;
                }

                if (TryHeading(trimmed, out int level, out string headingText))
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseLists(html, openLists);
                    html.Append("<h").Append(level).Append('>')
                        .Append(Inline(headingText))
                        .Append("</h").Append(level).Append(">\n");
                    continue;
                }

                if (TryTableRow(lines, i, count, out string tableHtml, out int consumed))
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseLists(html, openLists);
                    html.Append(tableHtml);
                    i += consumed - 1;
                    continue;
                }

                if (TryListItem(line, out bool ordered, out int depth, out string itemText))
                {
                    CloseParagraph(html, ref inParagraph);
                    OpenListTo(html, openLists, ordered, Math.Min(depth, MaxListDepth - 1));
                    html.Append("<li>").Append(Inline(itemText)).Append("</li>\n");
                    continue;
                }

                // Anything else is paragraph text. A continuation line joins the open paragraph
                // rather than starting a new one, which is what makes a wrapped source paragraph
                // one paragraph in the output.
                if (openLists.Count > 0) CloseLists(html, openLists);
                if (!inParagraph) { html.Append("<p>"); inParagraph = true; }
                else html.Append(' ');
                html.Append(Inline(trimmed));
            }

            CloseParagraph(html, ref inParagraph);
            CloseLists(html, openLists);
            CloseQuote(html, ref inQuote);
        }

        private static void CloseParagraph(StringBuilder html, ref bool open)
        {
            if (!open) return;
            html.Append("</p>\n");
            open = false;
        }

        private static void CloseQuote(StringBuilder html, ref bool open)
        {
            if (!open) return;
            html.Append("</blockquote>\n");
            open = false;
        }

        private static void CloseLists(StringBuilder html, List<bool> openLists)
        {
            for (int i = openLists.Count - 1; i >= 0; i--)
                html.Append(openLists[i] ? "</ol>\n" : "</ul>\n");
            openLists.Clear();
        }

        /// <summary>Opens or closes list levels until the stack matches the item's depth and kind.</summary>
        private static void OpenListTo(StringBuilder html, List<bool> openLists, bool ordered, int depth)
        {
            while (openLists.Count > depth + 1)
            {
                html.Append(openLists[openLists.Count - 1] ? "</ol>\n" : "</ul>\n");
                openLists.RemoveAt(openLists.Count - 1);
            }

            // A kind change at the same depth closes and reopens: <ul> and <ol> are different
            // elements, and continuing the wrong one silently renumbers the caller's list.
            if (openLists.Count == depth + 1 && openLists[depth] != ordered)
            {
                html.Append(openLists[depth] ? "</ol>\n" : "</ul>\n");
                openLists.RemoveAt(depth);
            }

            while (openLists.Count < depth + 1)
            {
                html.Append(ordered ? "<ol>\n" : "<ul>\n");
                openLists.Add(ordered);
            }
        }

        // ----------------------------------------------------------------
        //  Block recognisers
        // ----------------------------------------------------------------

        private static bool IsFence(string line, out string fence, out string language)
        {
            fence = string.Empty;
            language = string.Empty;
            string t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal)) fence = "```";
            else if (t.StartsWith("~~~", StringComparison.Ordinal)) fence = "~~~";
            else return false;

            language = t.Substring(3).Trim();
            return true;
        }

        private static bool IsClosingFence(string line, string fence)
            => line.TrimStart().StartsWith(fence, StringComparison.Ordinal);

        /// <summary>Three or more <c>-</c>, <c>*</c> or <c>_</c>, optionally spaced.</summary>
        private static bool IsThematicBreak(string trimmed)
        {
            char c = trimmed[0];
            if (c != '-' && c != '*' && c != '_') return false;

            int count = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == c) count++;
                else if (trimmed[i] != ' ' && trimmed[i] != '\t') return false;
            }
            return count >= 3;
        }

        private static bool TryHeading(string trimmed, out int level, out string text)
        {
            level = 0;
            text = string.Empty;
            while (level < trimmed.Length && trimmed[level] == '#') level++;
            if (level == 0 || level > 6) return false;
            if (level < trimmed.Length && trimmed[level] != ' ') return false;

            text = trimmed.Substring(level).Trim().TrimEnd('#').TrimEnd();
            return true;
        }

        /// <summary>
        /// A list item: an indent of spaces and tabs, a bullet (<c>-</c>, <c>*</c>, <c>+</c>) or one to
        /// nine digits and <c>.</c> or <c>)</c>, whitespace, the text. Scanned by hand — the line is the
        /// document's, and a scan is linear in it with no pattern to time out.
        /// </summary>
        internal static bool TryListItem(string line, out bool ordered, out int depth, out string text)
        {
            ordered = false;
            depth = 0;
            text = string.Empty;

            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            int indentEnd = i;

            bool numbered = false;
            if (i < line.Length && (line[i] == '-' || line[i] == '*' || line[i] == '+'))
            {
                i++;
            }
            else
            {
                int digits = 0;
                while (i < line.Length && char.IsDigit(line[i])) { i++; digits++; }
                if (digits == 0 || digits > 9 || i >= line.Length || (line[i] != '.' && line[i] != ')')) return false;
                i++;
                numbered = true;
            }

            int afterMarker = i;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i == afterMarker) return false;

            string indent = line.Substring(0, indentEnd).Replace("\t", "    ");
            depth = indent.Length / 2;              // two spaces per level, the common convention
            ordered = numbered;
            text = line.Substring(i);
            return true;
        }

        /// <summary>
        /// A GFM table: a header row, a delimiter row of <c>---</c> cells, then body rows.
        /// </summary>
        /// <remarks>
        /// The delimiter row is what distinguishes a table from a paragraph containing pipes, so it
        /// is required rather than inferred — without it, prose like "a | b" becomes a table.
        /// </remarks>
        private static bool TryTableRow(string[] lines, int start, int count,
                                        out string html, out int consumed)
        {
            html = string.Empty;
            consumed = 0;

            if (start + 1 >= count) return false;
            if (lines[start].IndexOf('|') < 0) return false;
            if (!IsDelimiterRow(lines[start + 1])) return false;

            var sb = new StringBuilder("<table>\n<thead>\n<tr>");
            foreach (string cell in SplitRow(lines[start]))
                sb.Append("<th>").Append(Inline(cell)).Append("</th>");
            sb.Append("</tr>\n</thead>\n<tbody>\n");

            int i = start + 2;
            for (; i < count; i++)
            {
                if (lines[i].IndexOf('|') < 0 || lines[i].Trim().Length == 0) break;
                sb.Append("<tr>");
                foreach (string cell in SplitRow(lines[i]))
                    sb.Append("<td>").Append(Inline(cell)).Append("</td>");
                sb.Append("</tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
            html = sb.ToString();
            consumed = i - start;
            return true;
        }

        private static bool IsDelimiterRow(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || t.IndexOf('-') < 0) return false;

            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c != '-' && c != '|' && c != ':' && c != ' ' && c != '\t') return false;
            }
            return true;
        }

        private static IEnumerable<string> SplitRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);

            foreach (string cell in t.Split('|')) yield return cell.Trim();
        }

        // ----------------------------------------------------------------
        //  Inline
        // ----------------------------------------------------------------

        /// <summary>
        /// How many label searches <see cref="ReplaceBracketed"/> has made on this thread — the evidence that a
        /// failed opener takes the openers inside its label with it: one search per <c>]</c>, not per opener. A
        /// search runs at vector speed, so a clock cannot tell the two apart at any size a test can afford.
        /// </summary>
        [ThreadStatic] internal static long LabelSearches;

        /// <summary>
        /// Each <c>[label](target)</c> after <paramref name="opener"/> (<c>![</c> for an image, <c>[</c> for a link)
        /// rendered by <paramref name="render"/>: the label up to the first <c>]</c>, then <c>(</c>, a target of no
        /// <c>)</c> and no white space, then <c>)</c> — what <c>\[([^\]]*)\]\(([^)\s]+)\)</c> matched, leftmost first.
        /// </summary>
        /// <remarks>
        /// Scanned in one pass. The pattern, unanchored, scanned from every opener to the label's end and from every
        /// target to its end, so a paragraph of unclosed <c>[</c> or <c>[](</c> cost the square of its length. Here an
        /// opener that fails takes every opener inside its label with it (they end at the same <c>]</c>), and each
        /// target's end — the next <c>)</c> or white space — is read from one right-to-left pass.
        /// </remarks>
        private static string ReplaceBracketed(string text, string opener, Func<string, string, string> render)
        {
            if (text.IndexOf(opener, StringComparison.Ordinal) < 0) return text;
            var stop = new int[text.Length + 1];
            stop[text.Length] = text.Length;
            for (int k = text.Length - 1; k >= 0; k--)
                stop[k] = text[k] == ')' || char.IsWhiteSpace(text[k]) ? k : stop[k + 1];

            var sb = new StringBuilder(text.Length);
            int copied = 0, search = 0;
            while (true)
            {
                int open = text.IndexOf(opener, search, StringComparison.Ordinal);
                if (open < 0) break;
                int labelStart = open + opener.Length;
                LabelSearches++;
                int close = text.IndexOf(']', labelStart);
                if (close < 0) break;                       // no later opener has a ']' after it either
                int targetStart = close + 2;
                if (close + 1 < text.Length && text[close + 1] == '(' && targetStart < text.Length)
                {
                    int end = stop[targetStart];
                    if (end > targetStart && end < text.Length && text[end] == ')')
                    {
                        sb.Append(text, copied, open - copied)
                          .Append(render(text.Substring(labelStart, close - labelStart), text.Substring(targetStart, end - targetStart)));
                        copied = search = end + 1;
                        continue;
                    }
                }
                search = close + 1;
            }
            return copied == 0 ? text : sb.Append(text, copied, text.Length - copied).ToString();
        }

        /// <summary>
        /// Applies the inline rules to one run of text.
        /// </summary>
        /// <remarks>
        /// <para><b>Escaping happens first and the markup is inserted after</b>, so a
        /// <c>&lt;script&gt;</c> in the source becomes literal text rather than an element — a
        /// Markdown document is content, and treating it as markup is how a converter becomes an
        /// injection vector.</para>
        ///
        /// <para><b>Code spans are extracted before emphasis</b>, because their contents are literal:
        /// <c>`a_b_c`</c> must not italicise. They are replaced by placeholders and restored last.</para>
        /// </remarks>
        private static string Inline(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // The placeholder mark is the converter's own: one in the source is no character a document
            // holds (XML 1.0 forbids it), and a placeholder the source wrote would put markup where it stands.
            var codeSpans = new List<string>();
            string work = ExtractCodeSpans(text.Replace(PlaceholderMark, '\uFFFD'), codeSpans);

            work = Escape(work);

            // Each image and link is rendered into a placeholder, restored after the later passes: a pass that
            // read another's output would find a '[' inside a generated alt and write its own quotes there.
            // A target holding a placeholder, or a scheme no document link uses, renders the label alone.
            var rendered = new List<string>();
            work = ReplaceBracketed(work, "![", (alt, src) => Rendered(rendered, IsSafeTarget(src)
                ? "<img src=\"" + src + "\" alt=\"" + alt + "\" />" : alt));
            work = ReplaceBracketed(work, "[", (label, href) =>
            {
                string content = RestoreRendered(Emphasis(label), rendered);
                return Rendered(rendered, IsSafeTarget(href) ? "<a href=\"" + href + "\">" + content + "</a>" : content);
            });

            work = Emphasis(work);

            return RestoreCodeSpans(RestoreRendered(work, rendered), codeSpans);
        }

        private static string Emphasis(string work)
        {
            work = ReplacePair(work, "**", "strong");
            work = ReplacePair(work, "__", "strong");
            work = ReplacePair(work, "*", "em");
            return ReplacePair(work, "_", "em");
        }

        /// <summary>A link or image target the converter writes: no placeholder in it, and a scheme a document link uses (<see cref="Tetitulz.Pdf.Core.Writing.LinkTargets"/>).</summary>
        private static bool IsSafeTarget(string target)
            => target.IndexOf(PlaceholderMark) < 0 && Tetitulz.Pdf.Core.Writing.LinkTargets.IsSafe(target);

        private const string RenderedWord = "HTML";

        private static string Rendered(List<string> rendered, string markup)
        {
            rendered.Add(markup);
            return PlaceholderMark + RenderedWord + (rendered.Count - 1).ToString(CultureInfo.InvariantCulture) + PlaceholderMark;
        }

        /// <summary>Each rendered placeholder replaced by its markup, in one pass; a code span's placeholder is left for <see cref="RestoreCodeSpans"/>.</summary>
        private static string RestoreRendered(string work, List<string> rendered)
        {
            if (rendered.Count == 0 || work.IndexOf(PlaceholderMark) < 0) return work;
            var sb = new StringBuilder(work.Length);
            int i = 0;
            while (i < work.Length)
            {
                int mark = work.IndexOf(PlaceholderMark, i);
                if (mark < 0) { sb.Append(work, i, work.Length - i); break; }
                sb.Append(work, i, mark - i);
                int digits = mark + 1 + RenderedWord.Length, end = digits;
                while (end < work.Length && work[end] >= '0' && work[end] <= '9') end++;
                if (string.CompareOrdinal(work, mark + 1, RenderedWord, 0, RenderedWord.Length) == 0
                    && end > digits && end < work.Length && work[end] == PlaceholderMark
                    && int.TryParse(work.Substring(digits, end - digits), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && index < rendered.Count)
                {
                    sb.Append(rendered[index]);
                    i = end + 1;
                }
                else
                {
                    sb.Append(PlaceholderMark);
                    i = mark + 1;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Each code span's placeholder replaced by its <c>&lt;code&gt;</c> element, in one pass: a
        /// <c>string.Replace</c> per span read the whole text once for each, the square of a paragraph of spans.
        /// </summary>
        private static string RestoreCodeSpans(string work, List<string> codeSpans)
        {
            if (codeSpans.Count == 0) return work;
            var sb = new StringBuilder(work.Length);
            int i = 0;
            while (i < work.Length)
            {
                int mark = work.IndexOf(PlaceholderMark, i);
                if (mark < 0) { sb.Append(work, i, work.Length - i); break; }
                sb.Append(work, i, mark - i);
                // "\u0001CODE" + index + "\u0001": the index's digits, then the closing mark.
                int digits = mark + 1 + PlaceholderWord.Length, end = digits;
                while (end < work.Length && work[end] >= '0' && work[end] <= '9') end++;
                if (string.CompareOrdinal(work, mark + 1, PlaceholderWord, 0, PlaceholderWord.Length) == 0
                    && end > digits && end < work.Length && work[end] == PlaceholderMark
                    && int.TryParse(work.Substring(digits, end - digits), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && index < codeSpans.Count)
                {
                    sb.Append("<code>").Append(Escape(codeSpans[index])).Append("</code>");
                    i = end + 1;
                }
                else
                {
                    sb.Append(PlaceholderMark);
                    i = mark + 1;
                }
            }
            return sb.ToString();
        }

        private const char PlaceholderMark = '\u0001';
        private const string PlaceholderWord = "CODE";

        private static string CodePlaceholder(int index)
            => PlaceholderMark + PlaceholderWord + index.ToString(CultureInfo.InvariantCulture) + PlaceholderMark;

        private static string ExtractCodeSpans(string text, List<string> spans)
        {
            var sb = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                if (text[i] != '`') { sb.Append(text[i++]); continue; }

                int close = text.IndexOf('`', i + 1);
                if (close < 0) { sb.Append(text[i++]); continue; }

                spans.Add(text.Substring(i + 1, close - i - 1));
                sb.Append(CodePlaceholder(spans.Count - 1));
                i = close + 1;
            }

            return sb.ToString();
        }

        /// <summary>Wraps text between matched <paramref name="delimiter"/> pairs in an element.</summary>
        private static string ReplacePair(string text, string delimiter, string element)
        {
            var sb = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                int open = text.IndexOf(delimiter, i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(text, i, text.Length - i); break; }

                int close = text.IndexOf(delimiter, open + delimiter.Length, StringComparison.Ordinal);
                if (close < 0) { sb.Append(text, i, text.Length - i); break; }

                sb.Append(text, i, open - i);
                sb.Append('<').Append(element).Append('>');
                sb.Append(text, open + delimiter.Length, close - open - delimiter.Length);
                sb.Append("</").Append(element).Append('>');
                i = close + delimiter.Length;
            }

            return sb.ToString();
        }

        private static string Escape(string s)
            => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
