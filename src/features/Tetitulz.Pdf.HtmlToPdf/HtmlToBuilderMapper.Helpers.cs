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
        // ============================================================= helpers

        /// <summary>
        /// A paragraph carrying the resolved alignment and the <b>inherited</b> writing direction.
        ///
        /// <para>The direction comes from <paramref name="st"/> rather than from
        /// <paramref name="el"/>: <c>dir</c> inherits, so probing only the paragraph element
        /// missed every ancestor that set it.</para>
        /// </summary>
        private M.Paragraph NewPara(IElement? el, M.Section sec, M.TextAlignment? align, Style st)
        {
            // No space after by default: the spacing is the element's CSS margins, or the
            // browser's defaults for its tag (ApplyBlockStyle), never the model's flat 6pt.
            var p = new M.Paragraph { SpacingAfter = 0 };
            if (align.HasValue) p.Alignment = align.Value;
            if (st?.Direction is { } dir) p.Direction = dir;
            // The accumulated CSS opacity in force at this block, less what the enclosing opacity
            // box's group paints. Its runs carry only what they added on top
            // (Style.BlockOpacity records the accumulated value for exactly that purpose).
            if (st != null) p.Opacity = RelativeToGroup(st.Opacity);
            if (st != null) { p.Orphans = st.Orphans; p.Widows = st.Widows; }
            if (st != null) p.Hyphenate = Hyphenates(st);
            if (st != null && st.LineHeight > 0) p.LineSpacingPt = st.LineHeight;
            else p.LineHeightFactor = NormalLineHeightFactor;   // `normal`, as a browser sets it
            return p;
        }

        /// <summary>
        /// Whether a paragraph in <paramref name="st"/> hyphenates: <c>hyphens: auto</c> and an English
        /// <c>lang</c> — CSS Text 3 §5.1: "correct automatic hyphenation requires a hyphenation resource
        /// appropriate to the language", and the one the library carries is English.
        /// </summary>
        private static bool Hyphenates(Style st)
            => st.HyphensAuto
            && (st.Language.Equals("en", StringComparison.OrdinalIgnoreCase)
                || st.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Reads <c>dir</c> (or CSS <c>direction</c>) onto the inherited style. An element that
        /// declares nothing leaves the inherited value alone — that is what makes it inherit.
        ///
        /// <para>All three HTML values are handled: a predicate matching the literal string
        /// <c>"rtl"</c> and returning a bool would leave <c>&lt;p dir="ltr"&gt;</c> inside an RTL
        /// block unable to override it, and ignore <c>dir="auto"</c>.</para>
        /// </summary>
        private static void ApplyDirection(IElement el, Style st)
        {
            string? dir = el.GetAttribute("dir") ?? StyleValue(el, "direction");
            if (dir == null) return;
            if (Eq(dir, "rtl")) st.Direction = M.TextDirection.RightToLeft;
            else if (Eq(dir, "ltr")) st.Direction = M.TextDirection.LeftToRight;
            else if (Eq(dir, "auto")) st.Direction = M.TextDirection.Auto;
        }

        private static M.TextAlignment? ResolveAlign(IElement? el)
        {
            if (el == null) return null;
            string? a = StyleValue(el, "text-align") ?? el.GetAttribute("align");
            // <center> is `text-align: -webkit-center` in HTML's rendering rules: its text is
            // centred (and its tables, ResolveTableAlignment).
            if (a == null) return NameIs(el, HtmlTags.Center) ? M.TextAlignment.Center : null;
            if (Eq(a, "-webkit-center")) return M.TextAlignment.Center;
            if (Eq(a, "center")) return M.TextAlignment.Center;
            if (Eq(a, "right")) return M.TextAlignment.Right;
            if (Eq(a, "justify")) return M.TextAlignment.Justify;
            if (Eq(a, "left")) return M.TextAlignment.Left;
            // Logical values: they follow the writing direction rather than naming an edge.
            if (Eq(a, "start")) return M.TextAlignment.Start;
            if (Eq(a, "end")) return M.TextAlignment.End;
            return null;
        }

        // True when an element must not render: the `hidden` attribute, `display:none`, or
        // `visibility:hidden`/`collapse`. (visibility:hidden technically reserves space; the
        // managed engine skips it entirely — a documented approximation.)
        private static bool IsHidden(IElement el)
        {
            if (el.HasAttribute("hidden")) return true;
            string? d = StyleValue(el, "display");
            if (d != null && d.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            string? v = StyleValue(el, "visibility");
            if (v != null)
            {
                string vv = v.Trim();
                if (vv.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                    || vv.Equals("collapse", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// One conversion's parsed <c>style="…"</c> attributes, keyed by element.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a cache at all.</b> <see cref="StyleValue"/> re-lexed the whole attribute on
        /// every lookup, and roughly <b>35</b> lookups fire per block element — <c>IsHidden</c>,
        /// <c>ReportEngineLayoutLimits</c>, the four layout-container predicates, <c>ResolveAlign</c>,
        /// four <c>HasForcedPageBreak</c> probes and about seventeen inside <c>ApplyBlockStyle</c>.
        /// Each pass allocated two or three substrings and ran the <c>!important</c> regex <i>per
        /// declaration</i>, so the cost was (declarations × 35) per element.</para>
        /// <para><b>It is an allocation fix, not a speed fix, and the measurement says so.</b> Over a
        /// 100-section document, allocations scaled linearly with declarations-per-element — 68.7 MiB
        /// at 0 extra declarations per rule, 105.1 at 6, 142.0 at 12, 178.3 at 18, 211.8 at 24, a
        /// clean +6.07 MiB each, with one extra gen-0 collection per four — while wall clock showed
        /// no trend at all (157–218 ms, noise). A 45 KiB document allocated 142 MiB, 3 200× its
        /// input. Claiming this makes conversion faster would be wrong; it removes GC pressure, which
        /// is what a server running conversions concurrently actually pays.</para>
        /// <para><b>[ThreadStatic] rather than an instance field</b> because the 77 call sites reach
        /// <see cref="StyleValue"/> through a chain of <c>static</c> methods — <c>IsHidden</c>,
        /// <c>ResolveAlign</c>, <c>ResolveBackgroundColor</c>, <c>ResolveBidiOverride</c> and the
        /// rest — and making them instance would pull four partials and about a dozen call sites into
        /// instance scope to carry one field, which is the same cascade <c>StripBackgrounds</c>'
        /// doc comment already declines. Cleared by <c>Convert</c> in a <c>finally</c>, so a failed
        /// conversion cannot leave a DOM alive.</para>
        /// </remarks>
        [ThreadStatic] private static Dictionary<IElement, (string? Source, Dictionary<string, string> Map)>?
            _declarationCache;

        [ThreadStatic] private static int _declarationLookups;
        [ThreadStatic] private static int _declarationParses;
        [ThreadStatic] private static int _stylesheetReads;

        /// <summary>
        /// How many stylesheets this thread's last conversion actually read — a <c>&lt;link&gt;</c> or
        /// <c>@import</c> whose location it had not loaded before. The same sheet named a hundred
        /// thousand times reads once; without the location dedup it read every time, and no assertion
        /// about the converted document could tell a live dedup from a dead one.
        /// </summary>
        internal static int StylesheetReadCount => _stylesheetReads;

        /// <summary>Counts one actual stylesheet read (not a deduplicated repeat).</summary>
        internal static void CountStylesheetRead() => _stylesheetReads++;

        /// <summary>
        /// How many times <see cref="StyleValue"/> asked for an element's declarations during the
        /// conversion this thread last ran, and how many of those actually re-lexed a
        /// <c>style</c> attribute.
        /// </summary>
        /// <remarks>
        /// <para><b>This exists because the cache can disable itself silently.</b> Every answer is
        /// identical whether it is served from the map or re-lexed — that is what makes it a cache —
        /// so no assertion about a converted document can tell a live cache from a dead one. The
        /// specific way it can die is the <c>ReferenceEquals</c> validation in
        /// <see cref="DeclarationMap"/>: if AngleSharp ever stopped returning the same
        /// <see cref="string"/> instance for repeated <c>GetAttribute("style")</c> calls, every
        /// lookup would miss, every element would be re-lexed on every one of its ~35 lookups, and
        /// the whole suite would stay green while the optimisation was gone. That is precisely the
        /// shape of the <c>CMapCodec</c> <c>Sort</c> mutation CLAUDE.md records, and the reason
        /// <c>CMapCodec.UsesCidRangeIndex</c> exists.</para>
        /// <para>Reset at the <b>start</b> of a conversion and left standing afterwards, like the
        /// <c>Last*</c> diagnostic slots, so a caller — in practice a test — reads the figures for
        /// the conversion that just finished rather than zeroes.</para>
        /// </remarks>
        internal static int DeclarationLookupCount => _declarationLookups;

        /// <inheritdoc cref="DeclarationLookupCount"/>
        internal static int DeclarationParseCount => _declarationParses;

        /// <summary>Zeroes the liveness counters. Called at the start of every conversion.</summary>
        private static void ResetDeclarationCounters()
        {
            _declarationLookups = 0;
            _declarationParses = 0;
            _tableGridSteps = 0;
            _stylesheetReads = 0;
            _floatsExactlyMapped = 0;
        }

        /// <summary>
        /// Drops the parsed-style cache. Called at the end of every conversion.
        /// </summary>
        /// <remarks>
        /// The counters are deliberately <b>not</b> cleared here — they are the record of the
        /// conversion that just ran, and zeroing them would leave nothing to read.
        /// </remarks>
        private static void ClearDeclarationCache() => _declarationCache = null;

        /// <summary>
        /// <paramref name="el"/>'s declarations, property → value, with <c>!important</c> stripped
        /// and later declarations winning.
        /// </summary>
        /// <remarks>
        /// The cached entry is keyed by element <i>and</i> validated against the style attribute's
        /// string reference, so a <c>SetAttribute("style", …)</c> after an element was cached
        /// invalidates it rather than serving a stale map. Nothing in the mapper does that today —
        /// the cascade fold and the <c>var()</c> substitution both finish before the walk begins —
        /// but a cache that is only correct because of where its callers happen to sit is the kind
        /// of invariant this repo has watched decay.
        /// </remarks>
        private static Dictionary<string, string> DeclarationMap(IElement el)
        {
            _declarationLookups++;
            string? style = el.GetAttribute("style");

            var cache = _declarationCache ??= new Dictionary<IElement, (string?, Dictionary<string, string>)>();
            if (cache.TryGetValue(el, out var cached) && ReferenceEquals(cached.Source, style))
                return cached.Map;

            _declarationParses++;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(style))
            {
                // !important is stripped here as well as in the cascade fold: ApplyStylesheets
                // returns early when the document has no <style>/<link> at all, so an inline
                // `color: red !important` would otherwise reach the value parsers verbatim.
                foreach (var decl in SplitDeclarations(style))
                    if (TrySplitDeclaration(decl, out string prop, out string value, out _)
                        // Custom properties are case-SENSITIVE and this map is not; they are read
                        // by ResolveCssVariables straight off the attribute and never through
                        // StyleValue, so folding them in could only lose one to a case collision.
                        && !prop.StartsWith("--", StringComparison.Ordinal))
                        map[prop] = value;   // last wins, as the linear scan did
            }

            cache[el] = (style, map);
            return map;
        }

        // Returns the LAST matching declaration's value (later declarations win, per CSS).
        private static string? StyleValue(IElement el, string property) =>
            DeclarationMap(el).TryGetValue(property, out string? value) ? value : null;

        private static bool NameIs(IElement el, string tag) =>
            string.Equals(el.LocalName, tag, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The rows belonging to <paramref name="table"/> itself, in <b>rendering</b> order:
        /// <c>thead</c> rows, then <c>tbody</c> and direct <c>tr</c> children in document order,
        /// then <c>tfoot</c> rows. The bool is true for a row inside <c>thead</c>.
        ///
        /// <para>Deliberately <b>not</b> a descendant query.
        /// <c>table.QuerySelectorAll("tr")</c> has two independent defects: it returns rows in
        /// document order, so a <c>tfoot</c> authored before its <c>tbody</c> would render in the
        /// middle of the table; and it reaches <i>through</i> a <c>td</c> into a <b>nested</b>
        /// table, so the inner table's rows would be emitted as extra rows of the outer one — while
        /// <c>BuildCell</c> separately inlines the same text into the cell, duplicating it.</para>
        /// </summary>
        private static IEnumerable<(IElement Row, bool InHead)> TableRows(IElement table)
        {
            foreach (var group in table.Children)
                if (NameIs(group, HtmlTags.THead))
                    foreach (var r in group.Children)
                        if (NameIs(r, HtmlTags.Tr)) yield return (r, true);

            foreach (var child in table.Children)
            {
                if (NameIs(child, HtmlTags.Tr)) yield return (child, false);
                else if (NameIs(child, HtmlTags.TBody))
                    foreach (var r in child.Children)
                        if (NameIs(r, HtmlTags.Tr)) yield return (r, false);
            }

            foreach (var group in table.Children)
                if (NameIs(group, HtmlTags.TFoot))
                    foreach (var r in group.Children)
                        if (NameIs(r, HtmlTags.Tr)) yield return (r, false);
        }

        /// <summary>
        /// The <c>col</c> elements belonging to <paramref name="table"/> itself — its direct
        /// <c>col</c> children and those inside its own <c>colgroup</c>s. Scoped for the same
        /// reason as <see cref="TableRows"/>: a descendant query also collected the columns of a
        /// nested table.
        /// </summary>
        private static IEnumerable<IElement> TableCols(IElement table)
        {
            foreach (var child in table.Children)
            {
                if (NameIs(child, HtmlTags.Col)) yield return child;
                else if (NameIs(child, HtmlTags.ColGroup))
                    foreach (var c in child.Children)
                        if (NameIs(c, HtmlTags.Col)) yield return c;
            }
        }

        // Resolves a width that may be a percentage of a reference width, else a CSS length.
        private double ResolveWidth(string? val, double reference, double fallback)
        {
            if (string.IsNullOrEmpty(val)) return fallback;
            string v = val!.Trim();
            if (v.EndsWith("%", StringComparison.Ordinal))
                // "Infinity%"/"NaN%" parse to a non-finite width, which would reach the model and throw.
                // Decline to the fallback. A negative value is kept: this resolver also serves offset
                // shares (ResolvePlacementOffset), where a negative — top:-50% — is legitimate.
                return double.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)
                    ? FiniteOr(pct / 100 * reference, fallback) : fallback;
            return FiniteOr(ParseLength(v, fallback), fallback);
        }

        /// <summary>A value the model would refuse as non-finite (<c>Infinity%</c>, <c>NaN%</c>) is
        /// declined to <paramref name="fallback"/>; a finite negative is kept (an offset may be negative).</summary>
        private static double FiniteOr(double v, double fallback) =>
            !double.IsNaN(v) && !double.IsInfinity(v) ? v : fallback;

        // Parses a CSS length to PDF points. px→pt at 96dpi (×0.75); in/cm/mm/pt/pc supported;
        // em is relative to <paramref name="emBase"/>, rem to the DOCUMENT's root font size; % is
        // unresolved here.
        // The absolute units go through Core.Text.CssLength.TryToPoints, the one unit table, shared
        // with the media-query parser and the SVG converter. Its points form keeps a
        // multiply-then-divide order for cm and mm, bit-identical to this method's arithmetic.
        //
        // Instance rather than static, because `rem` is the document's root size: a hardcoded 12pt
        // root would make `html { font-size: 62.5% }` — the common "1rem = 10px" idiom — and every
        // other custom root size silently produce wrong lengths everywhere rem appears.
        /// <summary>
        /// The CSS length grammar this engine accepts.
        /// </summary>
        /// <remarks>
        /// A <c>static readonly</c> field rather than <c>Regex.Match(input, pattern)</c>: the static
        /// form goes through the framework's shared regex cache on every call, which is a lock and a
        /// lookup, and the cache holds 15 entries against the eleven patterns this mapper used to
        /// invoke that way. <c>Compiled</c> is worth its one-time JIT cost here specifically —
        /// every margin, padding, border, font-size and track width in the document comes through
        /// this one method.
        /// </remarks>
        /// <remarks>
        /// The number is <c>\d+(?:\.\d*)?|\.\d+</c>, not the earlier <c>\d*\.?\d+</c>: with the dot
        /// optional, the two digit runs split a long digit string every way there is when the unit
        /// then fails to match, which is quadratic in the string — a style attribute is hostile input,
        /// and a megabyte of digits cost a million-squared steps. A value longer than
        /// <see cref="MaxLengthChars"/> is not a length and is refused before the pattern runs; the
        /// match timeout is the belt behind both.
        /// </remarks>
        private static readonly Regex LengthPattern =
            new Regex(@"^(-?(?:\d+(?:\.\d*)?|\.\d+))\s*(px|pt|in|cm|mm|pc|em|rem|vmin|vmax|vw|vh|%)?$",
                      RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        /// <summary>Longer than any CSS length: a sign, digits, a fraction, a unit and spaces.</summary>
        internal const int MaxLengthChars = 64;

        /// <summary>
        /// Whether <paramref name="value"/> is a CSS number (CSS Syntax §4.3.12): an optional minus when
        /// <paramref name="signed"/>, then digits with an optional fraction, or a fraction alone — <c>1</c>,
        /// <c>1.5</c>, <c>.5</c>, never <c>1.</c>. Scanned by hand, so linear in the text: a pattern such as
        /// <c>\d*\.?\d+</c> splits a long digit run every way there is when the match fails (a hundred thousand
        /// digits of <c>line-height</c> cost 22 seconds). Refused past <see cref="MaxLengthChars"/>, as a length is.
        /// </summary>
        internal static bool IsCssNumber(string value, bool signed)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxLengthChars) return false;
            int i = 0;
            if (signed && value[0] == '-') i++;
            int whole = 0, fraction = 0;
            while (i < value.Length && value[i] >= '0' && value[i] <= '9') { i++; whole++; }
            if (i < value.Length && value[i] == '.')
            {
                i++;
                while (i < value.Length && value[i] >= '0' && value[i] <= '9') { i++; fraction++; }
                if (fraction == 0) return false;
            }
            return i == value.Length && whole + fraction > 0;
        }

        /// <summary>
        /// <paramref name="css"/> with its comments removed (CSS Syntax §4.3.2): each from <c>/*</c> to the next
        /// <c>*/</c>, and one left open to the end of the sheet. One pass with <see cref="string.IndexOf(string, int, StringComparison)"/>:
        /// the pattern <c>/\*.*?\*/</c> scans from every unterminated opener to the end, so a sheet of twenty
        /// thousand <c>/*</c> would cost ten seconds.
        /// </summary>
        internal static string StripCssComments(string css)
        {
            int open = css.IndexOf("/*", StringComparison.Ordinal);
            if (open < 0) return css;
            var sb = new System.Text.StringBuilder(css.Length);
            int from = 0;
            while (open >= 0)
            {
                sb.Append(css, from, open - from);
                int close = css.IndexOf("*/", open + 2, StringComparison.Ordinal);
                if (close < 0) return sb.ToString();
                from = close + 2;
                open = css.IndexOf("/*", from, StringComparison.Ordinal);
            }
            sb.Append(css, from, css.Length - from);
            return sb.ToString();
        }

        private double ParseLength(string? value, double fallback, double emBase = DefaultFontSizePt)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string trimmed = value!.Trim();
            if (trimmed.Length > MaxLengthChars) return fallback;
            Match m;
            try { m = LengthPattern.Match(trimmed); }
            catch (RegexMatchTimeoutException) { return fallback; }
            if (!m.Success) return fallback;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return fallback;
            string unit = m.Groups[2].Value.ToLowerInvariant();
            switch (unit)
            {
                case "em": return n * emBase;
                case "rem": return n * _remBasePt;
                case "%": return fallback;
                // Viewport units (CSS Values 4 §6.1.2) of the viewport @media is evaluated against —
                // the page area in print. Before the page is settled there is none, and the
                // fallback stands.
                case "vw": case "vh": case "vmin": case "vmax":
                {
                    if (_mediaViewport is not { } vp || vp.Width <= 0 || vp.Height <= 0) return fallback;
                    double w = vp.Width, h = vp.Height;
                    double side = unit switch { "vw" => w, "vh" => h, "vmin" => Math.Min(w, h), _ => Math.Max(w, h) };
                    return n / 100 * side * PdfUnits.PxToPoint;
                }
                case "": return n * PdfUnits.PxToPoint;   // a unitless length reads as px
            }
            return Tetitulz.Pdf.Core.Text.CssLength.TryToPoints(n, unit, out double points) ? points : fallback;
        }

        /// <summary>
        /// Normalises any CSS colour to <c>"#RRGGBB"</c>, or returns <c>null</c> when the value is
        /// not a colour this engine understands — meaning <b>the declaration is dropped</b> and
        /// the inherited value stands, which is what a browser does with an invalid declaration.
        ///
        /// <para>The reading is <see cref="Tetitulz.Pdf.Core.Text.CssColor.TryParse"/>: hex in its
        /// four lengths, <c>rgb()</c>/<c>rgba()</c>, <c>hsl()</c>/<c>hsla()</c> and the CSS named
        /// colours, shared with the SVG converter; the conversion to hex is
        /// <see cref="Tetitulz.Pdf.Core.Text.CssColor.ToOpaqueHex"/>, shared with XFA's rich text.
        /// A translucent colour is composited over white so it
        /// approximates its on-page appearance. A fully transparent one (<c>transparent</c>,
        /// <c>rgba(…, 0)</c>) is absent instead: composited, it was <b>white</b>, so
        /// <c>background-color: rgba(0, 0, 0, 0)</c> painted a white box over its parent's colour
        /// and a border in it drew a white line.</para>
        ///
        /// <para>Never <b>black</b> on a parse failure. That single decision would cause three
        /// separate visible defects —
        /// <c>color: red !important</c> rendering black, <c>background: url(bg.png)</c> painting a
        /// solid black box, and inline <c>background: transparent</c> painting a black highlight —
        /// and it would silently blacken every colour syntax added to CSS since
        /// (<c>currentColor</c>, <c>color-mix()</c>, <c>lab()</c>, <c>rgb(from …)</c>). Returning
        /// null is what makes an unsupported colour <i>absent</i> rather than <i>wrong</i>.</para>
        /// </summary>
        private static string? TryNormalizeColor(string? value) => Tetitulz.Pdf.Core.Text.CssColor.ToOpaqueHex(value);
    }
}
