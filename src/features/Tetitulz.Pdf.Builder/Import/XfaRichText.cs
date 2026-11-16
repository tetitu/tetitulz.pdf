// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Builder.Import
{
    /// <summary>
    /// One paragraph of XFA rich text: the formatting its <c>p</c> states — <c>null</c> where the
    /// container's <c>para</c> decides — and its runs.
    /// </summary>
    internal sealed class XfaRichParagraph
    {
        /// <summary><c>left</c>, <c>center</c>, <c>right</c>, <c>justify</c> or <c>justify-all</c>; <c>null</c> for the container's.</summary>
        public string? HAlign;
        public double? MarginLeft, MarginRight, SpaceAbove, SpaceBelow, TextIndent, LineHeight;
        /// <summary>The paragraph's own tab stops (<c>tab-stops</c>, <c>xfa-tab-stops</c>) and default interval (<c>tab-interval</c>); <c>null</c> for the container's.</summary>
        public List<Model.TabStop>? TabStops;
        public double? TabInterval;
        /// <summary>Chapter 27's page-break and orphan controls: <c>page-break-before/after: always</c>, <c>page-break-after: avoid</c>, <c>page-break-inside: avoid</c>, <c>orphans</c>, <c>widows</c>.</summary>
        public bool PageBreakBefore, PageBreakAfter, KeepWithNext, KeepTogether;
        /// <summary>A first paragraph's <c>page-break-before: avoid</c>: the block before the text, which lies outside it, kept with it.</summary>
        public bool KeepWithPrevious;
        public int? Orphans, Widows;
        public readonly List<XfaRichRun> Runs = new List<XfaRichRun>();
    }

    /// <summary>A run of rich text with the styles in force on it; <c>null</c> is the container's font.</summary>
    internal sealed class XfaRichRun
    {
        public string Text = "";
        /// <summary>A <c>br</c>: a line break, no text.</summary>
        public bool LineBreak;
        /// <summary>One advance to the next tab stop (a span's <c>xfa-tab-count</c>), no text.</summary>
        public bool Tab;
        public bool? Bold, Italic, Underline, Strike;
        public double? Size, LetterSpacing;
        public string? Family, Color, Link;
        /// <summary>1 for superscript, -1 for subscript, 0 on the baseline.</summary>
        public int Shift;
        /// <summary><c>kerning-mode</c>: pair kerning on or off; <c>null</c> for the container's.</summary>
        public bool? Kerning;
        /// <summary><c>xfa-font-horizontal-scale</c> and <c>xfa-font-vertical-scale</c>, percentages; <c>null</c> for the container's.</summary>
        public int? HorizontalScale, VerticalScale;
    }

    /// <summary>
    /// XFA rich text (XFA 3.3 chapter 27, "Rich Text Reference") read into paragraphs of styled runs: the
    /// XHTML elements the chapter lists — <c>html</c> and <c>body</c> as containers, <c>p</c>, <c>span</c>,
    /// <c>b</c>, <c>i</c>, <c>sub</c>, <c>sup</c>, <c>br</c>, <c>a</c> — with <c>div</c> and <c>li</c>
    /// besides (chapter 27 lets a processor read more; its "deprecated <c>u</c> and <c>s</c> … must not be
    /// supported", so their text is kept undecorated), and the <c>style</c> attribute's CSS properties it
    /// lists: the paragraph's <c>text-align</c>, margins, <c>text-indent</c>, <c>line-height</c> and tab
    /// stops (<c>tab-stops</c>, <c>xfa-tab-stops</c>, <c>tab-interval</c>, through
    /// <see cref="XfaTabStops"/>); the run's <c>font-family</c>, <c>font-size</c>, <c>font-weight</c>,
    /// <c>font-style</c>, <c>color</c>, <c>text-decoration</c>, <c>letter-spacing</c> and
    /// <c>vertical-align</c>; a span's <c>xfa-tab-count</c>, its advances to the next stop; and
    /// <c>xfa-spacerun</c>, which keeps a run's spaces. Whitespace is otherwise collapsed as HTML collapses
    /// it, and a tab character is whitespace, as chapter 27 has it.
    /// </summary>
    /// <remarks>
    /// <para><b>What is not read is said</b>: every other property — <c>tab-stop</c>, <c>kerning-mode</c>,
    /// the page-break and widow controls, the font scales — and a length or colour it cannot read lands in
    /// the caller's <c>unapplied</c> set by name, for the layout's report.</para>
    /// <para><b>The file chooses every number here</b>: a <c>style</c> attribute longer than
    /// <see cref="MaxStyleLength"/> is not read (and said), a length goes through <see cref="XfaMeasure"/>
    /// and a colour through <see cref="CssColor"/>, each with its own cap; nesting past
    /// <see cref="MaxDepth"/> and runs past <see cref="MaxRuns"/> are not read.</para>
    /// <para>A link is kept for <c>http</c>, <c>https</c> and <c>mailto</c> alone; any other target is
    /// its text (chapter 27, "Hyperlink Support", and the risks of chapter 15's external references).</para>
    /// </remarks>
    internal static class XfaRichText
    {
        public const int MaxStyleLength = 4096;
        public const int MaxDepth = 64;
        public const int MaxRuns = 10_000;

        private struct Style
        {
            public bool? Bold, Italic, Underline, Strike;
            public double? Size, LetterSpacing;
            public string? Family, Color, Link;
            public int Shift;
            public bool SpaceRun;
            public bool? Kerning;
            public int? HorizontalScale, VerticalScale;
        }

        /// <summary>The paragraphs of <paramref name="html"/> (an <c>exData</c> of <c>text/html</c>), with what was not applied named in <paramref name="unapplied"/>.</summary>
        public static List<XfaRichParagraph> Read(XElement html, ISet<string> unapplied)
        {
            var reader = new Reader(unapplied);
            reader.Walk(html, default, 0);
            reader.Close();
            return reader.Paragraphs;
        }

        private sealed class Reader
        {
            private readonly ISet<string> _unapplied;
            private XfaRichParagraph? _current;
            private bool _lastWasSpace = true;
            private int _runs;

            public Reader(ISet<string> unapplied) { _unapplied = unapplied; }

            public readonly List<XfaRichParagraph> Paragraphs = new List<XfaRichParagraph>();

            private XfaRichParagraph Current
            {
                get
                {
                    if (_current == null) { _current = new XfaRichParagraph(); Paragraphs.Add(_current); _lastWasSpace = true; }
                    return _current;
                }
            }

            /// <summary>Ends the paragraph open; the next text starts another.</summary>
            public void Close()
            {
                if (_current != null)
                {
                    // A trailing space is no part of the paragraph.
                    var runs = _current.Runs;
                    if (runs.Count > 0 && !runs[runs.Count - 1].LineBreak && runs[runs.Count - 1].Text.EndsWith(" ", StringComparison.Ordinal)
                        && !_trailingSpaceKept)
                        runs[runs.Count - 1].Text = runs[runs.Count - 1].Text.TrimEnd(' ');
                }
                _current = null;
            }

            private bool _trailingSpaceKept;

            public void Walk(XElement e, Style inherited, int depth)
            {
                if (depth > MaxDepth) { _unapplied.Add("rich text nested past " + MaxDepth + " levels"); return; }
                foreach (XNode node in e.Nodes())
                {
                    if (_runs > MaxRuns) { _unapplied.Add("rich text past " + MaxRuns + " runs"); return; }
                    if (node is XText text) { Append(text.Value, inherited); continue; }
                    if (node is not XElement x) continue;
                    string name = x.Name.LocalName.ToLowerInvariant();
                    if (name is "head" or "style" or "script" or "title") continue;   // no displayable content
                    Style style = inherited;
                    switch (name)
                    {
                        case "b": style.Bold = true; break;
                        case "i": style.Italic = true; break;
                        case "sup": style.Shift = 1; break;
                        case "sub": style.Shift = -1; break;
                        case "a": style.Link = SafeLink(Attr(x, "href")); break;
                        case "br":
                            Current.Runs.Add(new XfaRichRun { LineBreak = true });
                            _runs++;
                            _lastWasSpace = true;
                            continue;
                    }
                    bool block = name is "p" or "div" or "li";
                    var declarations = Declarations(Attr(x, "style"));
                    if (block)
                    {
                        Close();
                        ParagraphStyle(Current, declarations);
                    }
                    RunStyle(ref style, declarations);
                    // xfa-tab-count: that many advances to the next stop, where the element opens.
                    foreach (var (dName, dValue) in declarations)
                    {
                        if (dName != "xfa-tab-count") continue;
                        if (int.TryParse(dValue, NumberStyles.None, CultureInfo.InvariantCulture, out int tabs) && tabs <= XfaTabStops.MaxStops)
                            for (int t = 0; t < tabs; t++) { Current.Runs.Add(new XfaRichRun { Tab = true }); _runs++; }
                        else _unapplied.Add("xfa-tab-count: " + Short(dValue));
                        _lastWasSpace = true;
                    }
                    Walk(x, style, depth + 1);
                    if (block) Close();
                }
            }

            private void Append(string text, Style style)
            {
                if (text.Length == 0) return;
                string kept;
                if (style.SpaceRun) { kept = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '); }
                else
                {
                    var sb = new StringBuilder(text.Length);
                    foreach (char c in text)
                    {
                        bool space = c is ' ' or '\t' or '\n' or '\r';
                        if (space) { if (!_lastWasSpace) sb.Append(' '); _lastWasSpace = true; }
                        else { sb.Append(c); _lastWasSpace = false; }
                    }
                    kept = sb.ToString();
                }
                if (kept.Length == 0) return;
                if (style.SpaceRun) _lastWasSpace = kept.EndsWith(" ", StringComparison.Ordinal);
                _trailingSpaceKept = style.SpaceRun;
                Current.Runs.Add(new XfaRichRun
                {
                    Text = kept, Bold = style.Bold, Italic = style.Italic, Underline = style.Underline, Strike = style.Strike,
                    Size = style.Size, LetterSpacing = style.LetterSpacing, Family = style.Family, Color = style.Color,
                    Link = style.Link, Shift = style.Shift, Kerning = style.Kerning, HorizontalScale = style.HorizontalScale,
                    VerticalScale = style.VerticalScale,
                });
                _runs++;
            }

            private void ParagraphStyle(XfaRichParagraph p, List<(string Name, string Value)> declarations)
            {
                foreach (var (name, value) in declarations)
                {
                    switch (name)
                    {
                        case "text-align":
                            string align = value.ToLowerInvariant();
                            if (align is "left" or "center" or "right" or "justify" or "justify-all") p.HAlign = align;
                            else _unapplied.Add("text-align: " + Short(value));
                            break;
                        case "margin-left": p.MarginLeft = Length(name, value); break;
                        case "margin-right": p.MarginRight = Length(name, value); break;
                        case "margin-top": p.SpaceAbove = Length(name, value); break;
                        case "margin-bottom": p.SpaceBelow = Length(name, value); break;
                        case "margin":
                        {
                            // One to four lengths, as CSS's shorthand: top, right, bottom, left.
                            string[] parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            double?[] v = new double?[parts.Length];
                            for (int i = 0; i < parts.Length && i < 4; i++) v[i] = Length(name, parts[i]);
                            if (parts.Length is >= 1 and <= 4)
                            {
                                p.SpaceAbove = v[0];
                                p.MarginRight = parts.Length > 1 ? v[1] : v[0];
                                p.SpaceBelow = parts.Length > 2 ? v[2] : v[0];
                                p.MarginLeft = parts.Length > 3 ? v[3] : parts.Length > 1 ? v[1] : v[0];
                            }
                            else _unapplied.Add("margin: " + Short(value));
                            break;
                        }
                        case "text-indent": p.TextIndent = Length(name, value); break;
                        case "line-height": p.LineHeight = Length(name, value); break;
                        case "tab-stops": case "xfa-tab-stops": p.TabStops = XfaTabStops.Parse(value, _unapplied); break;
                        case "tab-interval": p.TabInterval = Length(name, value); break;
                        // Chapter 27, "Page Break Control": auto | always | avoid (inside: auto | avoid).
                        case "page-break-before":
                            // avoid: no break between this paragraph and the one before — that one kept
                            // with its next; a first paragraph's previous lies outside the text, which the
                            // layout keeps with it (KeepWithPrevious).
                            if (Is(value, "always")) p.PageBreakBefore = true;
                            else if (Is(value, "avoid") && Paragraphs.Count > 1 && ReferenceEquals(Paragraphs[Paragraphs.Count - 1], p))
                                Paragraphs[Paragraphs.Count - 2].KeepWithNext = true;
                            else if (Is(value, "avoid")) p.KeepWithPrevious = true;
                            else if (!Is(value, "auto")) _unapplied.Add(name + ": " + Short(value));
                            break;
                        case "page-break-after":
                            if (Is(value, "always")) p.PageBreakAfter = true;
                            else if (Is(value, "avoid")) p.KeepWithNext = true;
                            else if (!Is(value, "auto")) _unapplied.Add(name + ": " + Short(value));
                            break;
                        case "page-break-inside":
                            if (Is(value, "avoid")) p.KeepTogether = true;
                            else if (!Is(value, "auto")) _unapplied.Add(name + ": " + Short(value));
                            break;
                        case "orphans": p.Orphans = Count(name, value) ?? p.Orphans; break;
                        case "widows": p.Widows = Count(name, value) ?? p.Widows; break;
                    }
                }
            }

            private void RunStyle(ref Style style, List<(string Name, string Value)> declarations)
            {
                foreach (var (name, value) in declarations)
                {
                    string v = value.ToLowerInvariant();
                    switch (name)
                    {
                        case "font-family":
                        {
                            string first = value.Split(',')[0].Trim().Trim('\'', '"').Trim();
                            if (first.Length is > 0 and <= 128) style.Family = first;
                            else _unapplied.Add("font-family");
                            break;
                        }
                        case "font-size": style.Size = Length(name, value) is double size && size > 0 ? size : style.Size; break;
                        case "font-weight":
                            if (v is "bold" or "bolder") style.Bold = true;
                            else if (v is "normal" or "lighter") style.Bold = false;
                            else if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int weight) && weight is >= 1 and <= 1000) style.Bold = weight >= 600;
                            else _unapplied.Add("font-weight: " + Short(value));
                            break;
                        case "font-style":
                            if (v is "italic" or "oblique") style.Italic = true;
                            else if (v == "normal") style.Italic = false;
                            else _unapplied.Add("font-style: " + Short(value));
                            break;
                        case "color":
                            if (CssColor.ToOpaqueHex(value) is string hex) style.Color = hex;
                            else _unapplied.Add("color: " + Short(value));
                            break;
                        case "text-decoration":
                            if (v == "none") { style.Underline = false; style.Strike = false; break; }
                            bool any = false;
                            foreach (string word in v.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                // Chapter 27's word and double variants are underlines all.
                                if (word.Contains("underline")) { style.Underline = true; any = true; }
                                else if (word == "line-through") { style.Strike = true; any = true; }
                            }
                            if (!any) _unapplied.Add("text-decoration: " + Short(value));
                            break;
                        case "letter-spacing":
                            style.LetterSpacing = v == "normal" ? 0 : Length(name, value) ?? style.LetterSpacing;
                            break;
                        case "vertical-align":
                            if (v == "super") style.Shift = 1;
                            else if (v == "sub") style.Shift = -1;
                            else if (v == "baseline") style.Shift = 0;
                            else _unapplied.Add("vertical-align: " + Short(value));
                            break;
                        case "xfa-spacerun":
                            style.SpaceRun = v == "yes";
                            break;
                        case "text-align": case "margin": case "margin-left": case "margin-right": case "margin-top":
                        case "margin-bottom": case "text-indent": case "line-height":
                        case "tab-stops": case "xfa-tab-stops": case "tab-interval":
                        case "page-break-before": case "page-break-after": case "page-break-inside": case "orphans": case "widows":
                            break;   // a paragraph's, read by ParagraphStyle
                        case "kerning-mode":
                            if (v == "pair") style.Kerning = true;
                            else if (v == "none") style.Kerning = false;
                            else _unapplied.Add("kerning-mode: " + Short(value));
                            break;
                        case "xfa-font-horizontal-scale":
                            if (Percent(value) is int scale) style.HorizontalScale = scale;
                            else _unapplied.Add("xfa-font-horizontal-scale: " + Short(value));
                            break;
                        case "xfa-font-vertical-scale":
                            if (Percent(value) is int vertical) style.VerticalScale = vertical;
                            else _unapplied.Add("xfa-font-vertical-scale: " + Short(value));
                            break;
                        case "xfa-tab-count":
                            break;   // an advance where the element opens, read by Walk
                        default:
                            _unapplied.Add(name);
                            break;
                    }
                }
            }

            private static bool Is(string value, string keyword) => string.Equals(value.Trim(), keyword, StringComparison.OrdinalIgnoreCase);

            /// <summary>A line count of 1 to 1 000, or null (and said).</summary>
            private int? Count(string name, string value)
            {
                if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 1000) return n;
                _unapplied.Add(name + ": " + Short(value));
                return null;
            }

            private double? Length(string name, string value)
            {
                if (XfaMeasure.TryParse(value.Trim(), out double points)) return points;
                _unapplied.Add(name + ": " + Short(value));
                return null;
            }

            /// <summary>A <c>style</c> attribute's declarations, names lower-cased, in order; one past <see cref="MaxStyleLength"/> is not read, and said.</summary>
            private List<(string Name, string Value)> Declarations(string? style)
            {
                var list = new List<(string, string)>();
                if (string.IsNullOrEmpty(style)) return list;
                if (style!.Length > MaxStyleLength) { _unapplied.Add("a style attribute longer than " + MaxStyleLength + " characters"); return list; }
                foreach (string declaration in style.Split(';'))
                {
                    int colon = declaration.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = declaration.Substring(0, colon).Trim().ToLowerInvariant();
                    string value = declaration.Substring(colon + 1).Trim();
                    if (name.Length > 0 && value.Length > 0) list.Add((name, value));
                }
                return list;
            }

            private static string Short(string value) => value.Length <= 32 ? value : value.Substring(0, 32) + "…";
        }

        /// <summary>
        /// A percentage of 1 to 1 000 (<c>90%</c>, <c>90</c>), rounded, or null — rich text's
        /// <c>xfa-font-horizontal-scale</c> and <c>xfa-font-vertical-scale</c>, and a <c>font</c>'s
        /// <c>fontHorizontalScale</c> and <c>fontVerticalScale</c>, alike.
        /// </summary>
        internal static int? Percent(string? value)
        {
            string v = (value ?? "").Trim();
            if (v.Length > 32) return null;
            if (v.EndsWith("%", StringComparison.Ordinal)) v = v.Substring(0, v.Length - 1).Trim();
            return double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double pct) && pct >= 1 && pct <= 1000
                ? SaturatingCast.ToInt(Math.Round(pct)) : (int?)null;
        }

        /// <summary>An absolute link of a scheme a document link uses (<see cref="Core.Writing.LinkTargets"/>), or <c>null</c>.</summary>
        private static string? SafeLink(string? href)
        {
            if (string.IsNullOrWhiteSpace(href) || href!.Length > 2048) return null;
            if (!Uri.TryCreate(href.Trim(), UriKind.Absolute, out Uri? uri)) return null;
            return Core.Writing.LinkTargets.IsSafeScheme(uri.Scheme) ? uri.OriginalString : null;
        }

        private static string? Attr(XElement e, string name) => XfaXml.Attr(e, name);
    }
}
