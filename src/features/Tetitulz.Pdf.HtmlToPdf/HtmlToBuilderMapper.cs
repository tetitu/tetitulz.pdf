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
    /// <summary>
    /// Pure-managed HTML → PDF path (no browser): parses HTML with AngleSharp and maps it to
    /// the centralized <c>Tetitulz.Pdf.Builder</c> document model, then renders via the shared
    /// PDF exporter. Handles static HTML + CSS to roughly html2openxml's level —
    /// headings, paragraphs, <c>div</c>/<c>section</c>, lists (<c>ol type/start</c>,
    /// <c>ul</c> bullet styles, nesting), tables (<c>colspan</c>, <c>rowspan</c>, borders,
    /// <c>colgroup</c>/<c>col</c> + cell widths, <c>cellpadding</c>, cell vertical-align /
    /// shading / width, <c>caption</c>, header-row repeat), <c>blockquote</c>,
    /// <c>pre</c>/<c>code</c> (monospace + whitespace), <c>hr</c>, <c>dl</c>/<c>dt</c>/<c>dd</c>,
    /// <c>b/strong</c>, <c>i/em</c>, <c>u/ins</c>, <c>s/strike/del</c>, <c>sup/sub</c>,
    /// <c>mark</c>, <c>small/big</c>, the legacy <c>font</c> tag, links, images (data-URI plus,
    /// opt-in, local files) and RTL (<c>dir="rtl"</c>).
    ///
    /// CSS comes from inline <c>style=</c> attributes <b>and</b> <c>&lt;style&gt;</c> blocks
    /// (tag / class / id / descendant selectors, with approximate specificity + source-order
    /// cascade; <c>@media</c>/<c>@supports</c> blocks are flattened, other at-rules skipped).
    /// Inheritable properties (color, font-family/size/weight/style, text-align, white-space)
    /// flow from ancestor to descendant. Supported declarations: color, background-color,
    /// background-image (repeat / cover / contain; local and data: urls only),
    /// font size/weight/style/family (sans / serif → Times / monospace → Courier),
    /// text-decoration, text-transform, font-variant (small-caps), letter-spacing,
    /// vertical-align, line-height, text-indent, text-align, direction, white-space, and block
    /// margin/padding/border (shorthand + per-side). Colors: #rgb/#rgba/#rrggbb/#rrggbbaa,
    /// rgb()/rgba(), hsl()/hsla() (alpha composited over white) and the CSS named colours.
    ///
    /// <c>opacity</c> is accumulated down the tree and applied as constant alpha.
    ///
    /// <b>CSS layout</b> — <c>display:flex</c>, <c>display:grid</c>, <c>float</c>/<c>clear</c> and
    /// <c>columns</c> each map onto a borderless table, the only construct in the Builder model that
    /// places blocks side by side (see <c>HtmlToBuilderMapper.Bands.cs</c>); <c>position</c>,
    /// <c>z-index</c>, <c>::before</c>/<c>::after</c>, <c>border-radius</c>, <c>box-shadow</c> and
    /// <c>transform</c> are mapped too. What each of those approximates rather than reproduces is
    /// reported through <c>BuilderDiagnosticKind.ContentApproximated</c> — never silently dropped —
    /// and GAPS.md §4b lists the declines with their reasons.
    ///
    /// Inline <c>&lt;svg&gt;</c> renders as a raster image when an opt-in rasterizer is supplied via
    /// <c>HtmlToPdfOptions.SvgRasterizer</c> (otherwise it is skipped). Not supported (use the
    /// browser engine): JavaScript, WOFF 2.0 web fonts, and remote <c>http(s)</c> resources. (A WOFF
    /// <b>1.0</b> <c>@font-face</c> IS honoured — Core's <c>WoffDecoder</c> unwraps it — as are
    /// <c>.ttf</c>/<c>.otf</c>.)
    ///
    /// <para><b>This summary lives here and nowhere else.</b> It was copied onto <b>thirteen</b> of
    /// the partials, and every copy still said flex, grid and float were unsupported — nine days
    /// and three shipped layout features after they stopped being. One copy had additionally
    /// drifted, gaining an <c>opacity</c> line the other twelve lacked. The seven partials that
    /// actually implement those features are precisely the ones that never carried the header, so
    /// nothing pointed at the contradiction. A partial class needs the doc once.</para>
    /// </summary>
    internal sealed partial class HtmlToBuilderMapper
    {
        // Guards against adversarial input: deeply-nested markup would StackOverflow the
        // recursive walk, and a huge node count would pin CPU/memory. Both are bounded.
        // These bound the HTML DOM, not a PDF object graph, so they stay local rather than
        // moving into Tetitulz.Pdf.Core.Constants.PdfLimits.
        private const int MaxDepth = 200;        // DOM nesting cap (stack-overflow guard)
        private const int MaxNodes = 500_000;    // total mapped-node budget (work guard)
        private const int MaxCssRules = 5_000;   // parsed stylesheet rule cap

        /// <summary>
        /// The characters of one stylesheet this engine reads, after which it is skipped and reported.
        /// A <c>&lt;link&gt;</c> or <c>@import</c> that names a many-megabyte comment-only sheet made the
        /// cascade read, rebase and scan it, finding no rules, so <see cref="MaxCssRules"/> never stopped
        /// the loop; a hundred thousand links to the same file read it a hundred thousand times. The
        /// sheets are also deduplicated by location, so a real sheet is read once.
        /// </summary>
        internal static int MaxStylesheetChars { get; set; } = DefaultMaxStylesheetChars;

        /// <summary>Built-in value of <see cref="MaxStylesheetChars"/> (a test lowers the property and restores it).</summary>
        internal const int DefaultMaxStylesheetChars = 8 * 1024 * 1024;

        /// <summary>
        /// The characters of an <c>@import</c> prelude this engine reads. The prelude (the target, an
        /// optional <c>layer</c>/<c>supports</c>, a media list) is a few dozen characters in any real
        /// sheet; it was parsed by copying the remaining text once a keyword, so half a million
        /// <c>layer</c> tokens were quadratic. A longer prelude names nothing.
        /// </summary>
        private const int MaxImportPreludeChars = 4096;

        /// <summary>
        /// The characters of declaration text one conversion's cascade may fold into element styles,
        /// over every rule and every element it matches.
        /// </summary>
        /// <remarks>
        /// <para>The fold is a product of two counts the markup chooses — a rule's declaration length
        /// times the elements it matches — and <see cref="MaxCssRules"/> caps only the first. A rule
        /// whose value is a megabyte long, matched by ten thousand elements, wrote twenty gigabytes of
        /// style attributes; five thousand rules each matching two hundred thousand elements collected a
        /// billion match tuples. Charging the fold by the declaration text it collects bounds both: the
        /// elements past the budget keep their own inline style and the drop is reported once.</para>
        /// <para>64&#160;MiB is far above any real document: a stylesheet of a few hundred kilobytes
        /// matched across a few thousand elements is a few megabytes of folded text. A test lowers the
        /// property and restores it, rather than building a 64&#160;MiB fixture.</para>
        /// </remarks>
        internal static long MaxCssCascadeChars { get; set; } = DefaultMaxCssCascadeChars;

        /// <summary>Built-in value of <see cref="MaxCssCascadeChars"/>.</summary>
        internal const long DefaultMaxCssCascadeChars = 64L * 1024 * 1024;

        /// <summary>
        /// The characters one conversion's custom-property substitution may write, over every
        /// <c>var()</c> expansion.
        /// </summary>
        /// <remarks>
        /// Each resolved value is pasted in whole, so a chain where each property names the previous one
        /// twice doubles at every step — the "billion laughs" that CSS Variables 1 §3 requires a UA to
        /// bound: <c>--a26: var(--a25) var(--a25)</c> off a sixteen-byte seed is a gigabyte in under a
        /// kilobyte of input. Past the budget a <c>var()</c> is left unexpanded, which the rest of the
        /// mapper reads as an unset value, and the drop is reported once. A test lowers the property and
        /// restores it, rather than building a 32&#160;MiB fixture.
        /// </remarks>
        internal static long MaxCssVarExpansionChars { get; set; } = DefaultMaxCssVarExpansionChars;

        /// <summary>Built-in value of <see cref="MaxCssVarExpansionChars"/>.</summary>
        internal const long DefaultMaxCssVarExpansionChars = 32L * 1024 * 1024;

        /// <summary>
        /// Cap on nested <c>@media</c>/<c>@supports</c> flattening in <c>ExtractRules</c>.
        ///
        /// <para><b>Separate from <see cref="MaxCssRules"/> on purpose.</b> That one counts rules,
        /// and a nesting-only payload — <c>@media all{@media all{…}}</c> repeated — adds zero
        /// rules, so it cannot bound this recursion at all. Depth would otherwise be bounded only
        /// by the length of the attacker-supplied <c>&lt;style&gt;</c> text, and a
        /// <c>StackOverflowException</c> is uncatchable: it kills the host process rather than
        /// failing the conversion.</para>
        ///
        /// <para>16 is far above anything real: an author nests <c>@supports</c> inside
        /// <c>@media</c>, occasionally two deep, never sixteen.</para>
        /// </summary>
        private const int MaxCssNestingDepth = 16;

        /// <summary>
        /// The CSS initial font size (<c>medium</c> = 16&#160;CSS&#160;px = 12&#160;pt). Seeds the root
        /// style, backs the <c>rem</c> unit and is the default <c>em</c> base for length parsing.
        /// </summary>
        private const double DefaultFontSizePt = 12;

        /// <summary>
        /// Font size of the <b>root</b> element, which is what a <c>rem</c> length multiplies.
        /// </summary>
        /// <remarks>
        /// Set once from the computed <c>&lt;html&gt;</c> style, before <c>&lt;body&gt;</c> is
        /// folded in — <c>rem</c> is "root em", so a <c>font-size</c> on <c>body</c> must NOT move
        /// it, which is exactly what makes it different from <c>em</c>. Stays at the 12pt default
        /// for a document that sets no root size, so existing output is unchanged.
        /// </remarks>
        private double _remBasePt = DefaultFontSizePt;

        // Browser-default presentational values the UA stylesheet would otherwise supply.
        private const string HrRuleColor = "#888888";
        private const string MarkHighlightColor = "#FFFF00";
        private const string LinkColor = "#0000EE";
        /// <summary>The UA stylesheet's colour for a <c>&lt;table border&gt;</c>'s rules (HTML §15.3.8, <c>grey</c>).</summary>
        private const string UaTableBorderColor = "#808080";
        private const string BlackColor = "#000000";

        private const double HrRuleWidthPt = 1;

        /// <summary>Display size used for an image with neither an explicit nor an intrinsic size.</summary>
        private const double FallbackImageSizePt = 96;

        /// <summary>
        /// Page margin used on every side when <see cref="HtmlToPdfOptions"/> leaves it unset:
        /// 0.4&#160;inch, the browser print default.
        /// </summary>
        private const double DefaultPageMarginPt = 0.4 * PdfUnits.PointsPerInch;

        /// <summary>
        /// The UA stylesheet's heading sizes, in em of the parent's font size (HTML §15.3.6), keyed
        /// by the (lower-case) tag name — not fixed points, so an h2 is 18pt where a browser's is, and
        /// a heading in small print shrinks with it.
        /// </summary>
        private static readonly Dictionary<string, double> HeadingSizesEm = new(StringComparer.Ordinal)
        {
            [HtmlTags.H1] = 2,
            [HtmlTags.H2] = 1.5,
            [HtmlTags.H3] = 1.17,
            [HtmlTags.H4] = 1,
            [HtmlTags.H5] = 0.83,
            [HtmlTags.H6] = 0.67,
        };

        private readonly HtmlToPdfOptions _options;

        /// <summary>
        /// Directory of the input file when converting one from disk, used as the base for
        /// relative <c>src</c>/<c>href</c> only when the caller set no explicit
        /// <see cref="HtmlToPdfOptions.BasePath"/>. Passed in rather than written onto
        /// <c>_options</c>: an options instance is commonly reused, and mutating it would
        /// silently grant file-read permission to a later <c>ConvertHtml</c> call on untrusted
        /// markup. This is engine parity — the browser path already confines <c>file://</c>
        /// subresources to the input file's own directory.
        /// </summary>
        private readonly string? _implicitBasePath;

        private int _nodeBudget = MaxNodes;

        /// <summary>
        /// Set once <see cref="MaxCssNestingDepth"/> has refused a block, so a payload nesting
        /// thousands of at-rules reports once per conversion rather than flooding the sink.
        /// </summary>
        private bool _cssNestingCapReported;
        private bool _cssLayerReported;
        private bool _cssSkippedAtRuleReported;

        /// <summary>Set once <see cref="MaxNodes"/> has refused a node.</summary>
        private bool _nodeBudgetReported;

        /// <summary>Set once <see cref="MaxDepth"/> has refused a subtree.</summary>
        private bool _depthCapReported;

        /// <summary>Set once <see cref="MaxSelectorCompounds"/> has skipped a rule.</summary>
        private bool _selectorCapReported;

        /// <summary>Set once <see cref="MaxCssCascadeChars"/> has stopped the cascade fold.</summary>
        private bool _cssFoldCapReported;

        /// <summary>Set once <see cref="MaxSelectorMatchWork"/> has skipped a rule whose match would be too costly.</summary>
        private bool _selectorCostReported;

        /// <summary>Set once <see cref="MaxCssVarExpansionChars"/> has stopped a <c>var()</c> expansion.</summary>
        private bool _cssVarCapReported;

        /// <summary>Set once <see cref="MaxStylesheetChars"/> has skipped an oversized stylesheet.</summary>
        private bool _stylesheetSizeReported;

        /// <summary>
        /// The most compound selectors (and selector functions — <c>:not()</c>, <c>:is()</c>,
        /// <c>:where()</c>, <c>:has()</c>) one complex selector may hold before its rule is skipped.
        /// </summary>
        internal const int MaxSelectorCompounds = 32;

        /// <summary>
        /// The most match work one stylesheet rule's selector may be estimated to cost against this
        /// document before the rule is skipped. The compound cap alone does not bound the cost:
        /// AngleSharp walks a descendant combinator up a matched element's ancestors, so a selector
        /// of a few combinators over a document that nests near <see cref="MaxMarkupNesting"/> deep
        /// runs in roughly (elements × combinators × depth) — tens of billions of steps for a
        /// 32-compound selector over a 25 000-deep chain, which hung the whole conversion. The
        /// estimate is that product; a rule past it keeps the matched elements' inline style and is
        /// reported once. No stylesheet over a real (shallow) document approaches it.
        /// </summary>
        internal static long MaxSelectorMatchWork { get; set; } = DefaultMaxSelectorMatchWork;

        /// <summary>Built-in value of <see cref="MaxSelectorMatchWork"/>.</summary>
        internal const long DefaultMaxSelectorMatchWork = 50_000_000;

        /// <summary>
        /// The deepest the markup may nest before the document is refused unparsed. AngleSharp's tree
        /// builder walks its stack of open elements for a list item's scope, so a hundred thousand open
        /// list items are five billion steps and the structure fuzzer's whole window; twenty-five
        /// thousand are a few seconds at worst, and above the twenty thousand the stack-overflow guard
        /// tests nest. The mapper itself stops at <see cref="MaxDepth"/> and says so.
        /// </summary>
        internal const int MaxMarkupNesting = 25_000;

        /// <summary>
        /// The compound count of the longest complex selector in <paramref name="selector"/>'s list,
        /// plus its selector functions: one for each simple-selector group, one more for each
        /// combinator (<c>&gt;</c>, <c>+</c>, <c>~</c>, or whitespace between two compounds) outside
        /// parentheses and brackets, one for each <c>:not(</c>, <c>:is(</c>, <c>:where(</c>, <c>:has(</c>.
        /// </summary>
        internal static int SelectorComplexity(string selector)
        {
            int worst = 0, compounds = 0, functions = 0, parens = 0, brackets = 0;
            bool inCompound = false, pendingSpace = false;
            for (int i = 0; i < selector.Length; i++)
            {
                char c = selector[i];
                if (brackets > 0) { if (c == ']') brackets--; continue; }
                if (c == '[')
                {
                    // An attribute selector opens or continues a compound like any simple selector.
                    if (!inCompound || pendingSpace) compounds++;
                    brackets++; inCompound = true; pendingSpace = false;
                    continue;
                }
                if (c == '(') { parens++; continue; }
                if (c == ')') { if (parens > 0) parens--; continue; }
                if (parens > 0)
                {
                    continue;
                }
                if (c == ',')
                {
                    worst = Math.Max(worst, compounds + functions);
                    compounds = 0; functions = 0; inCompound = false; pendingSpace = false;
                    continue;
                }
                if (char.IsWhiteSpace(c)) { pendingSpace = inCompound; continue; }
                if (c == '>' || c == '+' || c == '~')
                {
                    // An explicit combinator: the next compound starts after it; a space beside it is not one.
                    inCompound = false; pendingSpace = false;
                    continue;
                }
                if (c == ':' && (Starts(selector, i, ":not(") || Starts(selector, i, ":is(")
                              || Starts(selector, i, ":where(") || Starts(selector, i, ":has(")))
                    functions++;
                if (!inCompound || pendingSpace)
                {
                    compounds++;
                    inCompound = true;
                    pendingSpace = false;
                }
            }
            return Math.Max(worst, compounds + functions);
        }

        private static bool Starts(string s, int at, string what)
            => string.Compare(s, at, what, 0, what.Length, StringComparison.OrdinalIgnoreCase) == 0;

        /// <summary>
        /// Refuses markup longer than <paramref name="maxBytes"/> of UTF-8
        /// (<see cref="HtmlToPdfOptions.MaxHtmlBytes"/>) before the parser sees it: the engine's peak
        /// memory is proportional to the markup, and nothing else bounds the markup. Counted, not
        /// re-encoded — one pass over the string.
        /// </summary>
        internal static void RefuseOversizedMarkup(string html, long maxBytes)
        {
            if (maxBytes <= 0) return;
            long bytes = Encoding.UTF8.GetByteCount(html);
            if (bytes > maxBytes)
                throw new HtmlToPdfException(
                    "The markup is " + bytes + " bytes of UTF-8, more than HtmlToPdfOptions.MaxHtmlBytes (" + maxBytes +
                    "); the document was refused before it was parsed.");
        }

        /// <summary>
        /// Refuses markup whose open elements nest deeper than <see cref="MaxMarkupNesting"/>, by a scan
        /// that counts the way the HTML tree builder does: a stack of open elements, pushed by a start
        /// tag and popped by an end tag that matches one, with <c>script</c>, <c>style</c> and comment
        /// contents skipped as the tokenizer skips them. An overcount is possible only inside text that
        /// looks like tags, which cannot reach the cap on a page a person wrote.
        /// </summary>
        /// <remarks>
        /// <para>Counting start tags against end tags would undercount the depth the tree builder
        /// builds in three ways a crafted document could use to get past the cap. A trailing
        /// <c>/&gt;</c> self-closes nothing on an HTML element (<c>&lt;ol/&gt;&lt;li/&gt;</c> opens them),
        /// so the solidus is honoured only for a void element and inside foreign (<c>svg</c>/<c>math</c>)
        /// content. An end tag that matches no open element is ignored rather than decrementing
        /// (<c>&lt;li&gt;&lt;/x&gt;&lt;/x&gt;</c> stays open). And a comment ends at <c>--&gt;</c>,
        /// <c>--!&gt;</c> or the short <c>&lt;!--&gt;</c> / <c>&lt;!---&gt;</c>, not only the first:
        /// a leading <c>&lt;!--&gt;</c> with no later <c>--&gt;</c> stopped the scan where the tree
        /// builder reads an empty comment and goes on.</para>
        /// </remarks>
        /// <exception cref="HtmlToPdfException">The markup nests past the cap.</exception>
        internal static void RefuseDeepMarkup(string html)
        {
            var open = new List<string>();
            int i = 0, n = html.Length;
            bool InForeignContent() { foreach (string e in open) if (e == "svg" || e == "math") return true; return false; }
            while (i < n)
            {
                int lt = html.IndexOf('<', i);
                if (lt < 0 || lt + 1 >= n) break;
                char c = html[lt + 1];
                if (c == '!')
                {
                    // A comment to its end, else a doctype or CDATA to the next `>`.
                    i = Starts(html, lt, "<!--") ? CommentEnd(html, lt) : NextGtOrEnd(html, lt);
                    continue;
                }
                if (c == '/')
                {
                    int nameEnd = lt + 2;
                    while (nameEnd < n && (char.IsLetterOrDigit(html[nameEnd]) || html[nameEnd] == '-')) nameEnd++;
                    string closeName = html.Substring(lt + 2, nameEnd - lt - 2).ToLowerInvariant();
                    // An end tag closes the nearest matching open element and the ones still open
                    // inside it; one that matches nothing open is ignored, as the tree builder ignores it.
                    int at = open.LastIndexOf(closeName);
                    if (at >= 0) open.RemoveRange(at, open.Count - at);
                    int end = html.IndexOf('>', lt);
                    i = end < 0 ? n : end + 1;
                    continue;
                }
                if (!char.IsLetter(c)) { i = lt + 1; continue; }

                int tagNameEnd = lt + 1;
                while (tagNameEnd < n && (char.IsLetterOrDigit(html[tagNameEnd]) || html[tagNameEnd] == '-')) tagNameEnd++;
                string name = html.Substring(lt + 1, tagNameEnd - lt - 1).ToLowerInvariant();
                int close = html.IndexOf('>', tagNameEnd);
                if (close < 0) break;
                bool slash = close > 0 && html[close - 1] == '/';

                if (name == "script" || name == "style")
                {
                    // Raw text to its own end tag, the end tag included: an end tag that reached the
                    // `</` branch would close an element this branch never opened.
                    int end = html.IndexOf("</" + name, close, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) break;
                    int endClose = html.IndexOf('>', end);
                    i = endClose < 0 ? n : endClose + 1;
                    continue;
                }

                // A void element opens nothing. A `/>` self-closes only inside foreign content; on an
                // HTML element the solidus is ignored and the element is opened.
                if (!IsVoidElement(name) && !(slash && InForeignContent()))
                {
                    open.Add(name);
                    if (open.Count > MaxMarkupNesting)
                        throw new HtmlToPdfException(
                            "The markup nests more than " + MaxMarkupNesting + " elements deep; the document was " +
                            "not converted. A page this deep is not a page, and parsing it is quadratic in its depth.");
                }
                i = close + 1;
            }
        }

        /// <summary>The index just past an HTML comment opened at <paramref name="lt"/> (<c>&lt;!--</c>).</summary>
        /// <remarks>
        /// A comment ends at <c>--&gt;</c> or <c>--!&gt;</c>, and an empty one at the <c>&gt;</c> right
        /// after <c>&lt;!--</c> (<c>&lt;!--&gt;</c>) or after one more dash (<c>&lt;!---&gt;</c>) — the
        /// abrupt closings HTML §13.2.5 reads. Past the text's end when none is found.
        /// </remarks>
        private static int CommentEnd(string html, int lt)
        {
            int n = html.Length, start = lt + 4;   // past "<!--"
            if (start < n && html[start] == '>') return start + 1;                          // <!-->
            if (start + 1 < n && html[start] == '-' && html[start + 1] == '>') return start + 2;   // <!--->
            int a = html.IndexOf("-->", start, StringComparison.Ordinal);
            int b = html.IndexOf("--!>", start, StringComparison.Ordinal);
            int endLen = 3;
            int end = a;
            if (b >= 0 && (a < 0 || b < a)) { end = b; endLen = 4; }
            return end < 0 ? n : end + endLen;
        }

        /// <summary>The index just past the next <c>&gt;</c> at or after <paramref name="lt"/>, or the text's end.</summary>
        private static int NextGtOrEnd(string html, int lt)
        {
            int end = html.IndexOf('>', lt);
            return end < 0 ? html.Length : end + 1;
        }

        private static bool IsVoidElement(string name) => name switch
        {
            "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input" or "link" or "meta"
                or "param" or "source" or "track" or "wbr" => true,
            _ => false,
        };

        /// <summary>
        /// Reports that something was mapped approximately rather than reproduced.
        /// </summary>
        /// <remarks>
        /// <b>One method, not one per partial.</b> Five partials each declared a private wrapper
        /// with this exact body under a different name — <c>ReportBox</c>, <c>ReportColumns</c>,
        /// <c>ReportGrid</c>, <c>ReportPositioned</c>, <c>ReportPseudo</c> — which is five names for
        /// one method on one class. Nothing is lost by merging them: every <paramref name="detail"/>
        /// in the tree already opens by naming the CSS property it is about, because the message is
        /// what a caller reads, not the method name.
        /// </remarks>
        private void ReportApproximation(string detail)
            => ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated, detail);

        /// <summary>
        /// Consumes one node from <see cref="MaxNodes"/>; false when the budget is spent.
        /// </summary>
        /// <remarks>
        /// Enforced by a bare <c>return</c>/<c>break</c>, a document that exceeded either cap would
        /// produce a partial PDF indistinguishable from a complete one: a 600 000-node report
        /// converted to roughly its first five-sixths, <c>OnDiagnostic</c> silent, and an automated
        /// pipeline archiving a truncated document as successful. The
        /// latch keeps it to one report per conversion rather than one per node.
        /// </remarks>
        private bool TakeNode()
        {
            if (_nodeBudget-- > 0) return true;

            if (!_nodeBudgetReported)
            {
                _nodeBudgetReported = true;
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "The document exceeded the " + MaxNodes + "-node mapping budget; the content " +
                    "after that point was not mapped and is absent from the output.");
            }
            return false;
        }

        /// <summary>True when <paramref name="depth"/> is past <see cref="MaxDepth"/>, reporting once.</summary>
        private bool TooDeep(int depth)
        {
            if (depth <= MaxDepth) return false;

            if (!_depthCapReported)
            {
                _depthCapReported = true;
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "Markup nested deeper than " + MaxDepth + " elements was not mapped; that " +
                    "subtree is absent from the output.");
            }
            return true;
        }

        private HtmlToBuilderMapper(HtmlToPdfOptions options, string? implicitBasePath,
                                    bool interactiveFormFields = false)
        {
            _options = options;
            _implicitBasePath = implicitBasePath;
            _interactiveFormFields = interactiveFormFields;
        }

        /// <summary>
        /// Whether form controls map to <see cref="M.FormFieldSpec"/> runs rather than static
        /// graphics. Set only by <see cref="ConvertHtmlWithFormFields"/>: the ordinary entry points
        /// cannot turn it on, because a placeholder run that nothing makes into a field is an empty
        /// box — worse than the static drawing, which at least shows the value.
        /// </summary>
        private readonly bool _interactiveFormFields;

        /// <summary>The base directory for managed file I/O: the caller's explicit setting, else the input file's own directory.</summary>
        private string? EffectiveBasePath =>
            string.IsNullOrEmpty(_options.BasePath) ? _implicitBasePath : _options.BasePath;

        /// <summary>
        /// <see cref="HtmlToPdfOptions.Scale"/> put through the exporter's own clamp, so the
        /// divisor applied to the Section and the <c>cm</c> the exporter writes are the same
        /// number by construction rather than by two literals that agree today.
        /// </summary>
        private double EffectiveScale => PdfExportOptions.ClampScale(_options.Scale);

        public static byte[] ConvertHtml(string html, HtmlToPdfOptions options)
            => ConvertHtml(html, options, null);

        public static byte[] ConvertHtml(string html, HtmlToPdfOptions options, string? implicitBasePath)
            => new HtmlToBuilderMapper(options ?? HtmlToPdfOptions.Default, implicitBasePath).Convert(html);

        /// <summary>
        /// Parses, maps and exports one document.
        /// </summary>
        /// <remarks>
        /// The <c>finally</c> bounds the parsed-style cache to this conversion. It holds one small
        /// dictionary per styled element, so leaving it set would keep the whole DOM alive on this
        /// thread until the next conversion happened to replace it — and on a thread that converts
        /// once, for as long as the thread lives.
        /// </remarks>
        private byte[] Convert(string html)
        {
            ResetDeclarationCounters();
            try { return ConvertCore(html); }
            finally { ClearDeclarationCache(); }
        }

        /// <summary>
        /// The document model <paramref name="html"/> maps to, before any exporter sees it.
        /// </summary>
        /// <remarks>
        /// <para><b>A liveness seam, not an abstraction.</b> Some properties of the model are
        /// invisible in the exported PDF, because two different models produce the same page: an
        /// ordered list numbered by its container and one numbered by a literal marker string on
        /// every item draw identical labels. The difference is real — only the first survives
        /// an edit, an HTML export, or a caller reading the model — and only a test that reads
        /// the model can see it. The precedent is <c>CMapCodec.UsesCidRangeIndex</c>: where two
        /// paths agree on the answer, assert the liveness separately.</para>
        /// <para>Internal, and reached only by this assembly's tests.</para>
        /// </remarks>
        internal static M.Document MapToModel(string html, HtmlToPdfOptions options)
        {
            var mapper = new HtmlToBuilderMapper(options ?? HtmlToPdfOptions.Default, null);
            ResetDeclarationCounters();
            try { return mapper.BuildModel(html); }
            finally { ClearDeclarationCache(); }
        }

        private byte[] ConvertCore(string html) => BuildModel(html).ToPdf(ExportOptions);

        /// <summary>
        /// Converts with form controls mapped to form-field runs, and returns where the exporter
        /// placed each one alongside the bytes. The fields themselves are not in the bytes — see
        /// <see cref="HtmlToPdfConverter.ConvertHtmlWithFormFields"/>.
        /// </summary>
        internal static (byte[] Pdf, IReadOnlyList<PlacedFormField> Fields) ConvertHtmlWithFormFields(
            string html, HtmlToPdfOptions options, string? implicitBasePath)
        {
            var mapper = new HtmlToBuilderMapper(options ?? HtmlToPdfOptions.Default, implicitBasePath,
                                                 interactiveFormFields: true);
            ResetDeclarationCounters();
            try
            {
                M.Document doc = mapper.BuildModel(html);
                var exporter = new PdfExporter(mapper.ExportOptions);
                using var ms = new MemoryStream();
                exporter.Export(doc, ms);
                return (ms.ToArray(), exporter.PlacedFormFields);
            }
            finally { ClearDeclarationCache(); }
        }

        /// <summary>
        /// The export options this conversion runs under, built once. The float mapper needs them
        /// before the export does — to measure a shrink-to-fit float with the fonts the export
        /// will draw it with — and two builds could disagree.
        /// </summary>
        /// <remarks>
        /// Safe to build during the walk: the <c>@font-face</c> rules the font provider reads are
        /// collected by <c>ApplyStylesheets</c>, which runs before the first block is mapped.
        /// </remarks>
        private PdfExportOptions ExportOptions => _exportOptions ??= BuildExportOptions();
        private PdfExportOptions? _exportOptions;

        /// <summary>
        /// The exporter that measures a floated paragraph's shrink-to-fit width — the same layout,
        /// with the same fonts, that will draw it.
        /// </summary>
        private PdfExporter Measurer => _measurer ??= new PdfExporter(ExportOptions);
        private PdfExporter? _measurer;

        /// <summary>
        /// The viewport <c>@media</c> is evaluated against: the section's content area in CSS
        /// pixels, or <see cref="HtmlToPdfOptions.ViewportWidth"/> when the caller set one, and
        /// <see cref="HtmlToPdfOptions.MediaType"/>.
        /// </summary>
        private MediaViewport MediaViewportFor(M.Section section)
        {
            const double CssPxPerPt = 96.0 / 72.0;
            double width = (section.PageWidthPt - section.MarginLeft - section.MarginRight) * CssPxPerPt;
            double height = (section.PageHeightPt - section.MarginTop - section.MarginBottom) * CssPxPerPt;
            if (_options.ViewportWidth is double w && w > 0 && !double.IsNaN(w) && !double.IsInfinity(w))
                width = w;
            return new MediaViewport(Math.Max(0, width), Math.Max(0, height), _options.MediaType);
        }

        /// <summary>Everything up to the export: the mapped, scrubbed document model.</summary>
        private M.Document BuildModel(string html)
        {
            // Before the parser sees it: a document past the size cap or nested past the depth cap
            // is refused, not parsed.
            RefuseOversizedMarkup(html ?? "", _options.MaxHtmlBytes);
            RefuseDeepMarkup(html ?? "");
            var parser = new HtmlParser();
            var dom = parser.ParseDocument(html ?? "");

            // The page after its own scripts, when the caller allows them and a script plugin is
            // registered; otherwise the page as written, and reported when it had scripts.
            string? scripted = RunPageScripts(html ?? "", dom);
            if (scripted != null)
            {
                // A script can grow the page past what was given; the same two caps apply to its result.
                RefuseOversizedMarkup(scripted, _options.MaxHtmlBytes);
                RefuseDeepMarkup(scripted);
                dom = parser.ParseDocument(scripted);
            }
            ReportCanvas(dom);

            var doc = new M.Document
            {
                Title = dom.Title ?? "",
                Tagged = _options.Tagged,
                // From the document, never a default: writing a wrong /Lang makes a screen reader
                // pronounce Hebrew with English phonemes while the conformance check reports
                // clean, which is worse than writing none at all.
                Language = dom.DocumentElement?.GetAttribute("lang")
                           ?? dom.DocumentElement?.GetAttribute("xml:lang")
                           ?? _options.DefaultLanguage ?? "",
            };
            // Chromium's print `scale` leaves the paper alone and shrinks the content, so more of
            // it fits per page. PdfExportOptions.Scale does the opposite by itself — it scales the
            // paper — so dividing every Section length by the same factor composes the two into
            // the browser's semantics: /MediaBox comes back out at the caller's nominal size, the
            // physical margins come back out nominal too (margin_logical·s = margin_nominal), and
            // the content area measured in logical points grows by 1/s.
            double scale = EffectiveScale;
            var section = new M.Section
            {
                PageWidthPt = ParseLength(_options.PageWidth, PdfPageSizes.LetterWidth) / scale,
                PageHeightPt = ParseLength(_options.PageHeight, PdfPageSizes.LetterHeight) / scale,
                MarginTop = ParseLength(_options.MarginTop, DefaultPageMarginPt) / scale,
                MarginBottom = ParseLength(_options.MarginBottom, DefaultPageMarginPt) / scale,
                MarginLeft = ParseLength(_options.MarginLeft, DefaultPageMarginPt) / scale,
                MarginRight = ParseLength(_options.MarginRight, DefaultPageMarginPt) / scale,
            };
            if (_options.Landscape)
                (section.PageWidthPt, section.PageHeightPt) = (section.PageHeightPt, section.PageWidthPt);

            // The page is settled BEFORE the stylesheets are read, because `@media` width features
            // are evaluated against it: the content area in CSS pixels, which is what a browser's
            // print evaluates them against. Section lengths are already divided by the scale.
            _mediaViewport = MediaViewportFor(section);
            ApplyStylesheets(dom);     // fold <style>/class/id cascade into each element's style attr
            ResolveCssVariables(dom);  // substitute var(--x[, fallback]) from custom properties

            var body = dom.Body;
            if (body != null)
            {
                var htmlEl = dom.DocumentElement;
                // Inherit <html> → <body> → content (e.g. `html { font-family: serif }`).
                // DeriveStyle picks up `dir` on the way, so the root direction and every
                // descendant's come from ONE traversal rather than two rules that can disagree.
                var rootStyle = new Style { Size = DefaultFontSizePt, Direction = _options.DefaultTextDirection, Language = doc.Language };
                if (htmlEl != null && htmlEl != body) rootStyle = DeriveStyle(rootStyle, htmlEl, null, false);

                // Captured HERE, between <html> and <body>: `rem` is root-em, so `body { font-size }`
                // must not move it. Capturing after the next line would make rem and em identical
                // for any document that sizes its body, which is most of them.
                _remBasePt = rootStyle.Size > 0 ? rootStyle.Size : DefaultFontSizePt;

                rootStyle = DeriveStyle(rootStyle, body, null, false);
                if (rootStyle.Direction is { } rootDir) section.Direction = rootDir;
                var rootAlign = ResolveAlign(body) ?? (htmlEl != null ? ResolveAlign(htmlEl) : null);
                WalkBlocks(body, section, section.Children, 0, 0, rootStyle, rootAlign);
                // Margins first: a float lifted out of its paragraph takes the paragraph's
                // spacing with it, so that spacing has to be the collapsed one already.
                CollapseAdjacentMargins(section.Children);
                LiftInlineFloats(section);

                // HeaderTemplate / FooterTemplate were Playwright-only and silently no-ops here,
                // even though Section.Header/Footer exist and PdfExporter renders them.
                if (_options.DisplayHeaderFooter)
                {
                    var head = BuildRunningBlock(_options.HeaderTemplate, dom, rootStyle);
                    if (head.Count > 0)
                    {
                        section.Header = new M.Header();
                        foreach (var para in head) section.Header.Paragraphs.Add(para);
                        // Reserve room for it. The exporter draws a running block at a fixed
                        // offset from the PAGE edge, and the default HTML margin (0.4in) is
                        // smaller than that offset — so without this the header lands inside the
                        // body and overlaps the first line. A browser does the same thing:
                        // enabling headers grows its default margins.
                        section.MarginTop = Math.Max(section.MarginTop, RunningBlockReservePt);
                    }

                    var foot = BuildRunningBlock(_options.FooterTemplate, dom, rootStyle);
                    if (foot.Count > 0)
                    {
                        section.Footer = new M.Footer();
                        foreach (var para in foot) section.Footer.Paragraphs.Add(para);
                        section.MarginBottom = Math.Max(section.MarginBottom, RunningBlockReservePt);
                    }
                }
            }

            doc.Sections.Add(section);

            // printBackground is an HTML-print concept, so it is resolved here rather than pushed
            // onto PdfExportOptions: that type is shared with DocxToPdf and every hand-built
            // Document, none of which has a notion of "the UA stylesheet's backgrounds".
            if (!_options.PrintBackground) StripBackgrounds(doc);

            return doc;
        }

        /// <summary>
        /// The Builder export options this conversion runs under: the composed font source
        /// (<c>@font-face</c> → <see cref="HtmlToPdfOptions.FontProvider"/> → host fonts), the
        /// unrenderable-text policy, the diagnostic callback, and CSS's placement of text in its
        /// line box (<see cref="PdfExportOptions.HalfLeading"/>).
        /// </summary>
        private PdfExportOptions BuildExportOptions() => new PdfExportOptions
        {
            FontProvider = BuildFontProvider(),
            AllowSystemFontSubstitution = _options.AllowSystemFontSubstitution,
            UnrenderableText = _options.UnrenderableText,
            OnDiagnostic = _options.OnDiagnostic,
            Scale = EffectiveScale,
            HalfLeading = true,
            MaxPages = _options.MaxPages,
        };

    }
}

