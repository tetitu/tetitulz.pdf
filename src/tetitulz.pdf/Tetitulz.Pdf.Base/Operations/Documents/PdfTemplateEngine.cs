// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Thrown when a template token cannot be replaced in a way that provably removes the
    /// placeholder, so the operation fails closed rather than producing a document that still
    /// contains it.
    /// </summary>
    public sealed class PdfTemplateException : PdfException
    {
        public PdfTemplateException(string message) : base(message) { }
    }

    /// <summary>Per-call policy for <see cref="PdfTemplateEngine.Replace"/>.</summary>
    public sealed class TemplateReplaceOptions
    {
        public string TokenOpen { get; set; } = "{{";
        public string TokenClose { get; set; } = "}}";

        /// <summary>Paragraph direction for replacement values.</summary>
        public BaseDirection BaseDirection { get; set; } = BaseDirection.Auto;

        /// <summary>Supplies font program bytes for text the template's own fonts cannot encode.</summary>
        public Func<string, byte[]> FontProvider { get; set; }

        /// <summary>Permit an installed font when the provider declines. Defaults to false.</summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>
        /// Fit the replacement to the placeholder's own advance, shrinking the font size if needed
        /// so that nothing else on the line moves. Defaults to true.
        /// </summary>
        public bool KeepSlot { get; set; } = true;

        /// <summary>
        /// After writing, re-read the produced document and check that no replaced token is still
        /// extractable from it. Defaults to <c>true</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This can only falsify, never confirm</b>, and the asymmetry is the whole design.
        /// Text extraction is lossy — a font with no <c>/ToUnicode</c> extracts as something other
        /// than what it draws — so <i>not</i> finding the token proves nothing. Finding it is proof
        /// the erasure missed. So a failure is reported on
        /// <see cref="TemplateReplaceResult.UnverifiedKeys"/> and a pass is silent: nothing here
        /// ever adds a key to a "verified" list, because there is no such thing to say.</para>
        ///
        /// <para><b>Why erasure and not position.</b> Position is already self-checked, before
        /// anything is written, and structurally rather than by measurement — see the remarks on
        /// <see cref="PdfTemplateEngine"/>. Erasure is the claim that can plausibly miss, because it
        /// depends on having found <i>every</i> occurrence of a token, including ones split across
        /// show operators and ones inside Form XObjects; and a miss ships a document with the
        /// placeholder still visible on the page.</para>
        ///
        /// <para><b>Cost.</b> Turning this on buffers the produced document in memory so it can be
        /// re-read, and extracts text from the edited pages only. Set it to <c>false</c> to write
        /// straight to the caller's stream.</para>
        /// </remarks>
        public bool VerifyErasure { get; set; } = true;

        /// <summary>
        /// Reflow only: accept a paragraph whose replacement needs more lines than the original had.
        /// </summary>
        /// <remarks>
        /// <para><b>Off, because the overflow is drawn over something.</b> Reflow re-breaks one
        /// paragraph and moves nothing else on the page — it cannot, since a PDF records where text
        /// was put and not what it belongs to. So a paragraph that grows past its last baseline runs
        /// into whatever sits below it, and the honest default is to refuse and say so rather than
        /// produce a page with two things written in the same place.</para>
        ///
        /// <para>Turn it on when the space below is known to be empty — a final paragraph, a box
        /// sized with room to spare — which is a fact about the document that only the caller
        /// has.</para>
        /// </remarks>
        public bool AllowReflowPastTheParagraph { get; set; }

        /// <summary>
        /// Reflow only: hyphenate an English word that does not fit at a line's end — broken at the
        /// last of its hyphenation points (Liang's algorithm over hyph-utf8's American English
        /// patterns) whose head, with a hyphen, still fits the line.
        /// </summary>
        /// <remarks>
        /// <para><b>Off, because the hyphen is drawn into the document's text.</b> The file records what
        /// is drawn, so a hyphenated line extracts as <c>hy-</c> and the next as <c>phenation</c>, as
        /// any typeset hyphenation does. Turn it on for English text whose replacement would otherwise
        /// need another line — a paragraph refused for growing past its last line may then fit.</para>
        /// </remarks>
        public bool Hyphenate { get; set; }
    }

    /// <summary>What <see cref="PdfTemplateEngine.Replace"/> did.</summary>
    public sealed class TemplateReplaceResult
    {
        internal readonly List<string> ReplacedList = new List<string>();
        internal readonly List<string> NotFoundList = new List<string>();
        internal readonly List<string> NotDrawnList = new List<string>();
        internal readonly List<string> NotEditableList = new List<string>();
        internal readonly List<string> NotReflowedList = new List<string>();
        internal readonly List<string> UnverifiedList = new List<string>();
        internal readonly List<string> WarningList = new List<string>();

        /// <summary>Keys located on a page and replaced.</summary>
        public IReadOnlyList<string> ReplacedKeys => ReplacedList;

        /// <summary>Keys whose token appeared nowhere in any page's content.</summary>
        public IReadOnlyList<string> NotFoundKeys => NotFoundList;

        /// <summary>
        /// Keys whose placeholder was found and <b>erased</b> but whose replacement could not be
        /// drawn — typically no font covering the value. Each has an entry in
        /// <see cref="Warnings"/>. Distinguished from <see cref="NotFoundKeys"/> because the
        /// document did change: the placeholder is gone.
        /// </summary>
        public IReadOnlyList<string> NotDrawnKeys => NotDrawnList;

        /// <summary>
        /// Keys whose placeholder <b>was located</b> but sits in a content stream that cannot be
        /// rewritten safely — a Form XObject drawn more than once, where editing it would change
        /// every other place it appears.
        ///
        /// <para>Kept separate from <see cref="NotFoundKeys"/> deliberately. "Not found" tells a
        /// caller to check their key for a typo; this tells them the key is right and the
        /// <i>template</i> needs changing. Collapsing the two sends them to the wrong place.</para>
        /// </summary>
        public IReadOnlyList<string> NotEditableKeys => NotEditableList;

        /// <summary>
        /// Reflow only: keys found in a paragraph that could not be re-broken, and were therefore
        /// left exactly as they were.
        /// </summary>
        /// <remarks>
        /// <b>A fourth outcome, not a variation on the other three.</b> The key is not missing
        /// (<see cref="NotFoundKeys"/>), the stream is not shared (<see cref="NotEditableKeys"/>),
        /// and the font is not the problem (<see cref="NotDrawnKeys"/>) — the paragraph is simply
        /// not the shape this operation re-lays, or the replacement would not fit the space it had.
        /// The warning beside each one says which. Collapsing it into any of the other three sends
        /// a caller to fix something that is not wrong.
        /// </remarks>
        public IReadOnlyList<string> NotReflowedKeys => NotReflowedList;

        /// <summary>
        /// Keys reported as replaced whose token was nonetheless still extractable from the
        /// produced document (<see cref="TemplateReplaceOptions.VerifyErasure"/>). Each has an
        /// entry in <see cref="Warnings"/>.
        /// </summary>
        /// <remarks>
        /// <b>Empty is not a guarantee.</b> The check can only falsify: extraction is lossy, so a
        /// token that survives in a font the extractor cannot read back stays invisible to it. A
        /// non-empty list is proof the erasure missed; an empty one is the absence of that proof,
        /// not its converse. These keys remain in <see cref="ReplacedKeys"/> — the document really
        /// was rewritten — so a caller checking only the happy path is not silently misled about
        /// whether anything changed.
        /// </remarks>
        public IReadOnlyList<string> UnverifiedKeys => UnverifiedList;

        /// <summary>Non-fatal issues, one per tolerated condition.</summary>
        public IReadOnlyList<string> Warnings => WarningList;

        /// <summary>True when at least one token was replaced, i.e. the output differs.</summary>
        public bool ChangedAnything => ReplacedList.Count > 0;
    }

    /// <summary>
    /// Replaces literal <c>{{token}}</c> placeholders drawn on a page with new text, <b>erasing</b>
    /// the placeholder from the content stream rather than covering it.
    ///
    /// <para><b>This invalidates every signature, necessarily.</b> Erasure means the placeholder's
    /// glyphs must exist nowhere in the output, and an incremental update cannot achieve that — the
    /// superseded bytes stay in the revision the first signature's digest covers, which is precisely
    /// what lets <c>PdfRevisions</c> recover them. So the output is a full single-revision rewrite.
    /// Preserve the signature <i>or</i> remove the data; never both. Use <see cref="PdfFormFiller"/>
    /// when the signature matters.</para>
    ///
    /// <para><b>How position is preserved.</b> The placeholder's character codes are dropped from
    /// their show operator and their total advance is emitted as one <c>TJ</c> adjustment, so every
    /// surviving glyph keeps its exact position. That guarantee is total rather than heuristic:
    /// within the operator the adjustment compensates exactly; later operators in the same text
    /// object are unaffected because a show operator advances <c>Tm</c> by exactly what was
    /// compensated; and <c>Td</c>/<c>TD</c>/<c>Tm</c>/<c>T*</c> set position absolutely or relative
    /// to <c>Tlm</c>, which show operators never touch. There is no fourth case.</para>
    ///
    /// <para><b>Form XObjects are scanned.</b> A token inside one is found and erased from the
    /// form's own stream, while the replacement is drawn from <b>page</b> content with an absolute
    /// text matrix — the walk composes the form's <c>/Matrix</c> and CTM, so a position recorded
    /// inside a form is already in page space. Splicing the replacement into the form instead would
    /// draw it once per invocation.</para>
    ///
    /// <para>A form drawn more than once anywhere in the document is <b>not</b> edited: erasing the
    /// placeholder would change every other place that form appears. Such a key is reported on
    /// <see cref="TemplateReplaceResult.NotEditableKeys"/> rather than as not-found, because "the
    /// key is wrong" and "the template is shared" send a caller to opposite investigations.
    /// Uniqueness is transitive — see <see cref="FormInvocationCounter"/>.</para>
    ///
    /// <para><b>Documented limits (v1).</b> A token must lie within one text object, on one
    /// baseline, and in a single content stream; anything else is refused <i>by name</i>.</para>
    ///
    /// <para>Show strings are split into character codes through the font's own encoding CMap
    /// (<c>GlyphIdResolver.DecodeCodes</c>), <b>not</b> by a fixed stride, so a mixed-width CMap —
    /// 1-byte ASCII plus 2-byte CJK, which is what the Shift-JIS-based Adobe CMaps are — is handled
    /// correctly. <see cref="PdfRedactionEngine"/> was converted in the same change; the two walks
    /// must not diverge on how a code is delimited.</para>
    /// </summary>
    public static partial class PdfTemplateEngine
    {
        /// <summary>Baseline tolerance, in text space, for the glyphs of one token.</summary>
        private const float SameBaselineTolerance = 0.5f;

        /// <summary>Smallest size a shrunk replacement may use before the fit is given up on.</summary>
        private const float MinFitFontSize = 4f;

        /// <summary>
        /// The embedded replacement font's resource name: this and a number, the first the page does
        /// not use (<see cref="ReplacementFontResourceFor"/>) — a page edited before holds the
        /// replacement font that edit added, and naming this one the same repainted that edit's text
        /// in this one's glyphs.
        /// </summary>
        private const string ReplacementFontPrefix = "TplF";

        /// <summary>The first <see cref="ReplacementFontPrefix"/> name <paramref name="page"/>'s resources do not use.</summary>
        private static string ReplacementFontResourceFor(PdfPage page)
            => PdfPageContentAppender.FreeResourceName(page.Resources, page.Document, ReplacementFontPrefix, 0);

        /// <summary>
        /// What the locator is looking for, and what the emitter draws back.
        /// </summary>
        /// <remarks>
        /// Three modes rather than two booleans: the combinations a pair of flags would admit —
        /// "token and reflow", "neither" — do not exist, and a reader of the call site should not
        /// have to work out which pairs are real.
        /// </remarks>
        private enum MatchMode
        {
            /// <summary><c>{{token}}</c> placeholders; the replacement keeps the slot.</summary>
            Token,

            /// <summary>Literal text; the replacement keeps the slot.</summary>
            Literal,

            /// <summary>Literal text; the containing paragraph is re-broken.</summary>
            Reflow,

            /// <summary>The runs named by their operations on one page, whatever they say; the replacement keeps the slot.</summary>
            Element,
        }

        /// <summary>
        /// What <see cref="MatchMode.Element"/> replaces: the text-showing operations named on one
        /// page, and the text that takes their place. <see cref="Values"/> receives the one pair the
        /// locator makes of them — the runs' own text as the key — so the rest of the engine sees
        /// what a literal replacement would have given it.
        /// </summary>
        private sealed class ElementScope
        {
            internal int PageIndex;
            internal HashSet<int> OpIndexes;
            internal string Text;
            internal Dictionary<string, string> Values;

            internal string Describe()
            {
                var ids = new List<int>(OpIndexes);
                ids.Sort();
                return "element" + (ids.Count == 1 ? " " : "s ") + string.Join(", ", ids) + " of page " + (PageIndex + 1);
            }
        }

        /// <summary>
        /// Replaces every located token and writes a full single-revision PDF to
        /// <paramref name="output"/>. When no token is found the input is copied through unchanged.
        /// </summary>
        /// <exception cref="PdfTemplateException">
        /// The document cannot be templated safely — encrypted input, or no resolvable catalog.
        /// </exception>
        public static TemplateReplaceResult Replace(
            Stream input, Stream output,
            IReadOnlyDictionary<string, string> values,
            TemplateReplaceOptions options)
            => Run(input, output, values, options, MatchMode.Token);

        /// <summary>
        /// Replaces literal text rather than delimited tokens: every occurrence of each key becomes
        /// its value, in a document that was never authored as a template.
        /// </summary>
        /// <remarks>
        /// <para><b>This is token substitution with a different locator, and it keeps the same
        /// guarantee: everything not replaced stays exactly where it was.</b> A replacement is
        /// drawn into the slot the original occupied — shrunk to fit if it is wider, down to
        /// <c>MinFitFontSize</c> — and nothing around it moves. That is the whole difference from
        /// find-and-replace with <b>reflow</b>, which re-lays the containing paragraph and therefore
        /// moves text it was not asked to touch; that is a separate problem with a separate
        /// contract (GAPS.md §19d) and is deliberately not attempted here.</para>
        ///
        /// <para><b>The consequence to design around:</b> a much longer replacement will shrink
        /// rather than wrap, and one that cannot fit even at the minimum size is refused and
        /// reported rather than drawn over its neighbours. Replacing a word with a sentence wants
        /// reflow, not this.</para>
        ///
        /// <para><b>How overlapping keys are arbitrated, because a literal is not
        /// self-delimiting.</b> The longest key is tried first, so replacing both <c>foo</c> and
        /// <c>foobar</c> does not depend on the order they were supplied in; the earliest hit on the
        /// page wins; and matches never overlap, so a key occurring inside its own replacement is
        /// not re-entered. An empty key is skipped and reported not found — it would otherwise match
        /// before every glyph.</para>
        ///
        /// <para>Every refusal the token path makes applies unchanged — a span crossing two content
        /// streams, two text objects or two baselines is left alone and named, because a
        /// wrongly-placed replacement is worse than an untouched original. A key found only in a
        /// Form XObject that the document draws more than once is reported through
        /// <see cref="TemplateReplaceResult.NotEditableKeys"/>: editing it would change every other
        /// place it appears.</para>
        /// </remarks>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Receives a full single-revision PDF.</param>
        /// <param name="replacements">Literal text to find, mapped to what replaces it.</param>
        /// <param name="options">
        /// The same options the token path takes; <c>TokenOpen</c> and <c>TokenClose</c> are unused
        /// here and are not validated.
        /// </param>
        /// <exception cref="PdfTemplateException">
        /// The document cannot be edited safely — encrypted input, or no resolvable catalog.
        /// </exception>
        public static TemplateReplaceResult ReplaceText(
            Stream input, Stream output,
            IReadOnlyDictionary<string, string> replacements,
            TemplateReplaceOptions options)
            => Run(input, output, replacements, options, MatchMode.Literal);

        /// <summary>
        /// Replaces the text of the runs <paramref name="elementIds"/> name on page
        /// <paramref name="pageIndex"/> — <see cref="PdfPageElement.Id"/>s of text elements, from a
        /// listing of these same bytes — with <paramref name="text"/>, in the slot the runs occupied:
        /// <see cref="ReplaceText"/> for one line a reader chose, rather than for every place some
        /// words occur.
        /// </summary>
        /// <remarks>
        /// <para>The runs are taken as one span, in the order the page draws them; the key the result
        /// reports is their own text. Every refusal <see cref="ReplaceText"/> makes applies: the span
        /// must lie in one text object on one baseline, and nothing else may be drawn between the
        /// runs named. A span that cannot be replaced is left untouched and the reason is in
        /// <see cref="TemplateReplaceResult.Warnings"/>, with <see cref="TemplateReplaceResult.NotFoundKeys"/>
        /// naming the elements; an id among several that names no text is passed over and named in
        /// the warnings; <see cref="TemplateReplaceResult.ChangedAnything"/> says whether the
        /// output differs.</para>
        /// <para>The erasure is not verified by re-reading the text
        /// (<see cref="TemplateReplaceOptions.VerifyErasure"/> is not consulted): the runs were
        /// located by their operations, and the same words elsewhere on the page would be read as
        /// a miss.</para>
        /// </remarks>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Receives a full single-revision PDF.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="elementIds">The ids of the text elements whose text is replaced, from <see cref="PdfPageElements.List"/> over the same bytes.</param>
        /// <param name="text">What the runs show instead.</param>
        /// <param name="options">The same options the literal path takes; the token delimiters are unused.</param>
        /// <exception cref="PdfTemplateException">The document cannot be edited safely — encrypted input, or no resolvable catalog.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative or names a page the document does not have.</exception>
        public static TemplateReplaceResult ReplaceRun(
            Stream input, Stream output, int pageIndex, IReadOnlyCollection<int> elementIds, string text,
            TemplateReplaceOptions options)
        {
            if (elementIds == null) throw new ArgumentNullException(nameof(elementIds));
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), "The page index is zero-based.");
            if (elementIds.Count == 0) throw new ArgumentException("No element named.", nameof(elementIds));
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var scope = new ElementScope { PageIndex = pageIndex, OpIndexes = new HashSet<int>(elementIds), Text = text, Values = values };
            return Run(input, output, values, options, MatchMode.Element, scope);
        }

        /// <summary>
        /// The text each text-showing operation of <paramref name="page"/>'s own content shows, by
        /// the operation's index — as the fonts map the codes to text, in reading order
        /// (<see cref="ReadingOrderOf"/>): a Hebrew run a driver drew letter by letter reads as it is
        /// written, its points after their letters, whichever way along the line it drew them. An
        /// operation that shows nothing readable has an empty string; one that is no text has no entry.
        /// </summary>
        internal static Dictionary<int, string> RunTexts(PdfDocument doc, PdfPage page)
        {
            var texts = new Dictionary<int, StringBuilder>();
            byte[] content = page.GetContentStreamData();
            if (content == null || content.Length == 0) return new Dictionary<int, string>();
            IReadOnlyList<ContentStreamOperation> ops = ContentStreamParser.Parse(content);

            using var fontCache = new FontCache();
            var streams = new List<StreamWork>
            {
                new StreamWork { Id = 0, Form = null, Resources = page.Resources, Ops = ops, Editable = true, Ctm = Identity() },
            };
            var glyphs = new List<Glyph>();
            // Whether a form is drawn once decides only whether it may be edited, which nothing here asks.
            Locate(ops, page.Resources, doc, fontCache, Identity(), 0, 0, glyphs, streams, _ => false);

            var byOperation = new Dictionary<int, List<Glyph>>();
            foreach (Glyph g in glyphs)
            {
                if (g.StreamId != 0) continue;
                if (!byOperation.TryGetValue(g.OpIndex, out List<Glyph> run)) byOperation[g.OpIndex] = run = new List<Glyph>();
                run.Add(g);
            }
            var result = new Dictionary<int, string>();
            foreach (var pair in byOperation) result[pair.Key] = ReadingOrderOf(pair.Value);
            return result;
        }

        /// <summary>
        /// The text of a run's glyphs as a reader reads it: the glyphs put in their order on the page
        /// (<see cref="InPageOrder"/>) — a <c>TJ</c> may draw them right to left, and a span through
        /// several text objects in any order — each appended in visual order
        /// (<see cref="UnicodeBidi.InVisualOrder"/>), and the line reordered under its own direction.
        /// The one rule for <see cref="RunTexts"/> and for the key a located span is reported under,
        /// so a result names a run as <c>PdfPageElement.Text</c> does.
        /// </summary>
        private static string ReadingOrderOf(IReadOnlyList<Glyph> glyphs)
        {
            var sb = new StringBuilder();
            foreach (Glyph g in InPageOrder(glyphs)) sb.Append(UnicodeBidi.InVisualOrder(g.Text));
            string visual = sb.ToString();
            return UnicodeBidi.Reorder(visual, UnicodeBidi.VisualLineDirection(visual));
        }

        /// <summary>
        /// <paramref name="glyphs"/> in the order they stand along the first one's baseline — by where
        /// each begins, read back through the first glyph's text space — those beginning at one point in
        /// the order drawn; in the order drawn when the first's text space cannot be inverted.
        /// </summary>
        private static List<Glyph> InPageOrder(IReadOnlyList<Glyph> glyphs)
        {
            var drawn = new List<Glyph>(glyphs);
            if (drawn.Count < 2 || drawn[0].TextToPage == null || Rendering.PdfMatrix.Invert(drawn[0].TextToPage) is not float[] back)
                return drawn;
            float Along(Glyph g)
            {
                var (x, y) = Rendering.PdfMatrix.Transform(g.TextToPage ?? drawn[0].TextToPage, 0, 0);
                return Rendering.PdfMatrix.Transform(back, x, y).x;
            }
            return drawn.Select((g, i) => (g, i, at: Along(g))).OrderBy(e => e.at).ThenBy(e => e.i).Select(e => e.g).ToList();
        }

        private static TemplateReplaceResult Run(
            Stream input, Stream output,
            IReadOnlyDictionary<string, string> values,
            TemplateReplaceOptions options,
            MatchMode mode,
            ElementScope scope = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (values == null) throw new ArgumentNullException(nameof(values));
            options = options ?? new TemplateReplaceOptions();

            // Delimiters matter only to the token locator; the literal one never reads them, so
            // requiring them there would reject a perfectly well-formed call.
            //
            // THIS SKIP AND THE LOCATOR CHOICE BELOW ARE ONE DECISION. Routing a literal call to
            // MatchTokens leaves it scanning for an empty TokenOpen, which never terminates — a
            // mutation doing exactly that ran for fifteen minutes and was killed by timeout rather
            // than by an assertion. Whatever else changes here, a call that skips this validation
            // must not reach the token scan.
            if (mode == MatchMode.Token &&
                (string.IsNullOrEmpty(options.TokenOpen) || string.IsNullOrEmpty(options.TokenClose)))
                throw new ArgumentException("Token delimiters must be non-empty.", nameof(options));

            var result = new TemplateReplaceResult();

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Checked before anything else, for the reason PdfFormFiller checks it first too: an
            // encrypted document exposes no catalog, so "no tokens were found" would be a true
            // statement about a document we cannot read, and the wrong diagnosis to hand back.
            if (doc.IsEncrypted)
                throw new PdfTemplateException(
                    "Encrypted PDFs are not supported by template token replacement; decrypt the " +
                    "document first (see Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip).");

            // A page the document does not have is the caller's mistake, refused rather than
            // answered with a copy of the input and an empty result.
            if (scope != null && scope.PageIndex >= doc.Pages.Count)
                throw new ArgumentOutOfRangeException("pageIndex",
                    $"The document has {doc.Pages.Count} page(s); page index {scope.PageIndex} names none.");

            using var fontCache = new FontCache();

            // ---- pass 1: locate every token on every page ----
            var pages = new List<PageWork>();
            var found = new HashSet<string>(StringComparer.Ordinal);

            var uniquelyDrawn = FormInvocationCounter.BuildUniquelyDrawnTest(doc);

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                // A scoped replacement names one page: the others are not read.
                if (scope != null && p != scope.PageIndex) continue;
                var page = doc.Pages[p];
                byte[] content = page.GetContentStreamData();
                if (content == null || content.Length == 0)
                {
                    // The scoped page draws nothing, so the operations named show no text: said,
                    // as the locator would say it, rather than a copy of the input with nothing reported.
                    if (scope != null)
                    {
                        string what = scope.Describe();
                        result.NotFoundList.Add(what);
                        result.WarningList.Add(what + ": the page has no content, so those operations show no text; left untouched");
                    }
                    continue;
                }

                IReadOnlyList<ContentStreamOperation> ops;
                try { ops = ContentStreamParser.Parse(content); }
                catch (Exception ex)
                {
                    result.WarningList.Add($"page {p + 1}: content stream could not be parsed ({ex.Message}); skipped");
                    continue;
                }

                // Stream 0 is always the page's own content; forms are appended as they are met.
                var streams = new List<StreamWork>
                {
                    new StreamWork
                    {
                        Id = 0, Form = null, Resources = page.Resources, Ops = ops, Editable = true, Ctm = Identity(),
                    },
                };
                var glyphs = new List<Glyph>();
                streams[0].EndCtm = Locate(ops, page.Resources, doc, fontCache, Identity(), 0, 0,
                                           glyphs, streams, uniquelyDrawn);

                var matches = mode switch
                {
                    MatchMode.Reflow  => MatchReflow(glyphs, streams, values, options, p, result),
                    MatchMode.Literal => MatchLiterals(glyphs, streams, values, p, result),
                    MatchMode.Element => MatchElement(glyphs, streams, scope, p, result),
                    _                 => MatchTokens(glyphs, streams, values, options, p, result),
                };

                // A key located but not acted on is accounted for by its own list, so it must not
                // ALSO be reported as not-found: "not found" tells a caller to check for a typo,
                // and sending them there when the key was found and refused is the one wrong
                // answer. Reflow refuses far more often than the editability check does — a
                // paragraph has a shape to satisfy — so this mattered immediately.
                foreach (string k in result.NotEditableList) found.Add(k);
                foreach (string k in result.NotReflowedList) found.Add(k);
                if (matches.Count == 0) continue;

                foreach (var m in matches) found.Add(m.Key);
                pages.Add(new PageWork
                {
                    Index = p, Page = page, Streams = streams, Glyphs = glyphs, Matches = matches,
                });
            }

            foreach (string key in values.Keys)
                if (!found.Contains(key)) result.NotFoundList.Add(key);

            if (pages.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return result;
            }

            // ---- resolve the font program covering every replacement ----
            int nextObjNum = MaxObjectNumber(doc) + 1;
            var extras = new Dictionary<int, PdfObject>();
            var embedded = ResolveReplacementFont(pages, options, result);

            // Reflow breaks its paragraphs HERE and not at the locator, because breaking needs a
            // width and a width needs the font — which is resolved from the replacements, so it
            // does not exist until every match is known. A paragraph that will not fit is dropped
            // from the page's matches, so nothing downstream sees a match it must not emit.
            if (mode == MatchMode.Reflow) BreakReflowedParagraphs(pages, embedded, options, result);

            // ---- pass 2: rewrite each page's content ----
            //
            // Emit runs BEFORE the font is built, because building it needs the set of glyphs that
            // are actually drawn: Type0FontBuilder subsets to exactly those, and emits /W and
            // /ToUnicode for them alone. Building first would either embed the whole face or
            // describe glyphs the page never shows.
            var rewritten = new List<(PageWork work, Dictionary<int, byte[]> byStream)>();
            foreach (var work in pages)
            {
                var byStream = Emit(work, embedded, options, result);
                if (RewrittenStreamTamper != null)
                    foreach (int id in new List<int>(byStream.Keys))
                        byStream[id] = RewrittenStreamTamper(work.Index, id, byStream[id]);

                // Prove the result before committing to it, rather than trusting the code that
                // produced it — the discipline ContentStreamTagger applies to its own splice. The
                // property being checked is the one this operation promises and the one a bug here
                // would silently break: every glyph that was NOT part of a token is still drawn, at
                // the same device position. A dropped code with no compensating adjustment, a
                // mis-signed one, or a wrong horizontal-scale divisor all leave the page looking
                // plausible and every later glyph moved.
                string drift = VerifySurvivingGlyphs(work, byStream, doc, fontCache, uniquelyDrawn);
                if (drift != null)
                    throw new PdfTemplateException(
                        $"page {work.Index + 1}: the rewritten content does not preserve the " +
                        $"surrounding text ({drift}); nothing was written.");

                rewritten.Add((work, byStream));
            }

            FinishFont(embedded, ref nextObjNum, extras);

            var sharing = new PdfPageContentAppender.ResourceSharing();
            // Which pages use which content streams, read once for every page rewritten.
            var contentUsage = new PdfFullRewriter.ContentUsage(doc);
            foreach (var (work, byStream) in rewritten)
            {
                // Forms first: substituting the page content is the step that can fail, and a form
                // whose stream was already replaced would otherwise be left edited under an
                // unedited page.
                foreach (var s in work.Streams)
                {
                    if (s.Form == null || !byStream.TryGetValue(s.Id, out byte[] formBytes)) continue;
                    PdfFullRewriter.StripStream(s.Form, formBytes);
                }

                if (!byStream.TryGetValue(0, out byte[] content))
                    content = null;
                // The allocator and extras sink let the rewriter substitute a private clone when
                // this page's /Contents object is shared with another page: splicing the
                // replacement into a shared stream would draw this page's filled-in text on every
                // page that uses it.
                int allocated = nextObjNum;
                if (content != null && !PdfFullRewriter.TrySubstitutePageContent(
                        work.Page, doc, content, () => allocated++, extras, contentUsage))
                    throw new PdfTemplateException($"page {work.Index + 1} has no content stream to rewrite.");
                nextObjNum = allocated;
                AttachFontResource(work.Page, doc, embedded, work.FontResource, sharing, ref nextObjNum, extras);
            }

            // A span located by its operations is not re-read by its text: the same words elsewhere
            // on the page would be taken for a miss.
            if (!options.VerifyErasure || mode == MatchMode.Element)
            {
                if (!PdfFullRewriter.TryFullRewrite(doc, output, extras))
                    throw new PdfTemplateException("Document has no resolvable catalog; cannot rewrite.");
                return result;
            }

            // Buffered so the result can be re-read before the caller's stream is touched. The
            // caller's stream is only written once, and only after the check, so a verification
            // failure never reaches it half-written.
            using var produced = new MemoryStream();
            if (!PdfFullRewriter.TryFullRewrite(doc, produced, extras))
                throw new PdfTemplateException("Document has no resolvable catalog; cannot rewrite.");

            byte[] producedBytes = produced.ToArray();
            VerifyErasure(ProducedDocumentTamper?.Invoke(producedBytes) ?? producedBytes, pages, values, options, result, mode);

            produced.Position = 0;
            produced.CopyTo(output);
            return result;
        }

        /// <summary>
        /// Re-reads the produced document and reports any replaced token still extractable from a
        /// page that was edited.
        /// </summary>
        /// <remarks>
        /// <para><b>One-directional by construction.</b> It appends to
        /// <see cref="TemplateReplaceResult.UnverifiedKeys"/> and never records a success, because
        /// extraction cannot establish one: a font with no <c>/ToUnicode</c> reads back as
        /// something other than what it draws, so a surviving token can be invisible here. Finding
        /// one is proof of a miss; not finding one is not proof of anything.</para>
        ///
        /// <para><b>A key whose replacement contains the searched-for text is skipped entirely,
        /// and silently.</b> The check would find the run it had just drawn; it cannot distinguish
        /// that from a missed original, so for those keys it does not answer at all. Silence is the
        /// honest outcome here rather than a third list, because this method already never records
        /// a success — "not reported" means "nothing was proved".</para>
        ///
        /// <para><b>Only the edited pages are scanned.</b> A token on a page this call never
        /// touched is a token it never claimed to have replaced — reporting it would turn a
        /// verification of <i>this operation</i> into a document-wide token search, which is
        /// <c>Locate</c>'s job.</para>
        ///
        /// <para>Its own failure is a warning, not an exception. The document has already been
        /// rewritten successfully at this point, and refusing to hand it back because the
        /// <i>check</i> could not run would discard good work over a missing guarantee.</para>
        /// </remarks>
        private static void VerifyErasure(byte[] producedBytes, List<PageWork> pages,
            IReadOnlyDictionary<string, string> values,
            TemplateReplaceOptions options, TemplateReplaceResult result, MatchMode mode)
        {
            if (result.ReplacedList.Count == 0) return;

            try
            {
                using var check = new MemoryStream(producedBytes, writable: false);
                using var verifyDoc = PdfDocument.Open(check);

                foreach (var work in pages)
                {
                    if (work.Index < 0 || work.Index >= verifyDoc.Pages.Count) continue;

                    string text = PdfTextExtractor.ExtractText(verifyDoc.Pages[work.Index]);
                    if (string.IsNullOrEmpty(text)) continue;

                    foreach (string key in result.ReplacedList)
                    {
                        if (result.UnverifiedList.Contains(key)) continue;

                        // What the erasure was supposed to remove: the delimited token on the
                        // template path, the search text itself on the literal one. Checking for a
                        // token on the literal path would look for something that was never in the
                        // document — a verification that cannot fail, which is worse than none
                        // because it reads as evidence.
                        string token = mode == MatchMode.Token ? options.TokenOpen + key + options.TokenClose : key;

                        // A replacement containing what it replaced puts the searched-for text
                        // back on the page deliberately — "Acme" -> "Acme Corp" on the literal
                        // path, a value carrying a literal token on the other. Extraction cannot
                        // tell that copy from a surviving original, so the check can only find
                        // something, never decide what it found. Reporting anyway means a warning
                        // that fires on correct output, which teaches a caller to ignore it.
                        if (values.TryGetValue(key, out string replacement) && replacement != null &&
                            replacement.IndexOf(token, StringComparison.Ordinal) >= 0)
                            continue;

                        if (text.IndexOf(token, StringComparison.Ordinal) < 0) continue;

                        result.UnverifiedList.Add(key);
                        result.WarningList.Add(
                            $"key '{key}' was reported replaced, but the token '{token}' is still " +
                            $"extractable from page {work.Index + 1} of the produced document. The " +
                            "erasure missed an occurrence; treat the output as not having had this " +
                            "key removed.");
                    }
                }
            }
            catch (Exception ex)
            {
                result.WarningList.Add(
                    "the produced document could not be re-read to verify that the replaced tokens " +
                    $"are gone ({ex.Message}); the output is unaffected, but the erasure is " +
                    "unverified.");
            }
        }

        private sealed class PageWork
        {
            internal int Index;
            internal PdfPage Page;
            internal List<StreamWork> Streams;
            internal List<Glyph> Glyphs;
            internal List<TokenMatch> Matches;

            /// <summary>The replacement font's name on this page (<see cref="ReplacementFontResourceFor"/>), chosen when the page is emitted.</summary>
            internal string FontResource;
        }

        /// <summary>
        /// One content stream reachable from a page — the page's own, or a Form XObject's.
        /// </summary>
        private sealed class StreamWork
        {
            /// <summary>0 is the page's own content; forms are numbered from 1 as they are met.</summary>
            internal int Id;

            /// <summary>The form's stream, or <c>null</c> when this is the page content.</summary>
            internal PdfStream Form;

            internal PdfDictionary Resources;
            internal IReadOnlyList<ContentStreamOperation> Ops;

            /// <summary>
            /// The CTM this stream was first walked with — identity for page content, and the form's
            /// <c>/Matrix</c> composed with the CTM at its <c>Do</c> for a form. Stored so the
            /// verification pass can re-walk the stream into the same coordinate space; re-deriving
            /// it would let a check pass while comparing two different spaces.
            /// </summary>
            internal float[] Ctm;

            /// <summary>
            /// The CTM in force at the end of the stream, for page content: a run for a token found
            /// inside a form is appended there, not after the form's own <c>ET</c>.
            /// </summary>
            internal float[] EndCtm;

            /// <summary>
            /// Whether this stream may be rewritten. False for a Form XObject that is drawn more
            /// than once in the document: editing it would change every other place it appears.
            /// </summary>
            internal bool Editable;

            /// <summary>Why not, when <see cref="Editable"/> is false — reported against the key.</summary>
            internal string NotEditableReason;
        }

        // ================================================================
        //  Pass 1 — every drawn character code, with its position
        // ================================================================

        private sealed class Glyph
        {
            internal int OpIndex;
            internal int SegIndex;
            internal int ByteStart;
            internal int ByteLength;
            internal string Text;
            internal float AdvanceX;
            internal float BaselineY;
            internal float[] Trm;
            internal float FontSize;

            /// <summary>The CTM in force when this glyph was drawn; already folded into <see cref="Trm"/>.</summary>
            internal float[] Ctm;

            /// <summary>
            /// Text space to page space when this glyph was drawn — <c>Tm</c> then the CTM, without the
            /// font size or rise — so its origin and <see cref="AdvanceX"/> can be placed on the page.
            /// </summary>
            internal float[] TextToPage;

            /// <summary>
            /// The horizontal scale (<c>Tz</c>/100) in force when this glyph was drawn.
            /// </summary>
            /// <remarks>
            /// <see cref="AdvanceX"/> is computed with this factor applied (see WalkSegs), so
            /// <c>EmitShowWithDrops</c> must divide by it again to turn a removed advance back into
            /// a TJ adjustment. It hardcoded 1 — the field did not exist — so any template using
            /// <c>Tz</c> other than 100 under-compensated by exactly this factor, moved the text
            /// after the token, and either tripped <c>VerifySurvivingGlyphs</c> (making the
            /// document impossible to template) or shipped silently when the token was last.
            /// </remarks>
            internal float Th;

            internal int TextObjectId;
            internal string FillColorOps;

            /// <summary>Which content stream this glyph was drawn from — see <see cref="StreamWork"/>.</summary>
            internal int StreamId;

            /// <summary>The <c>/BaseFont</c> of the font that drew it, as the file names it; null when the font could not be read.</summary>
            internal string FontBaseName;

            /// <summary>
            /// Drawn by a font in vertical writing mode (<c>/WMode 1</c>, §9.7.4.3): its <see cref="AdvanceX"/>
            /// is not where the next glyph is, and a match over it is refused.
            /// </summary>
            internal bool Vertical;
        }

        /// <summary>
        /// Walks a content stream, recording every drawn character code with its position, and
        /// descends into Form XObjects so a token inside one is found rather than silently missed.
        /// </summary>
        /// <remarks>
        /// The glyph's <c>Trm</c> composes the form's CTM, so a position recorded inside a form is
        /// already in <b>page</b> space. That is what lets the replacement run be inserted into the
        /// page's own content stream with an absolute text matrix, instead of having to be spliced
        /// into the form — which would then draw once per invocation of that form.
        /// </remarks>
        /// <returns>The CTM in force at the end of the stream — where a form token's run is drawn.</returns>
        private static float[] Locate(
            IReadOnlyList<ContentStreamOperation> ops, PdfDictionary resources,
            PdfDocument doc, FontCache fontCache, float[] initialCtm, int streamId, int depth,
            List<Glyph> glyphs, List<StreamWork> streams, Func<int, bool> uniquelyDrawn)
        {
            var gs = new TState { CTM = (float[])initialCtm.Clone() };
            var stack = new Stack<TState>();
            var fontInfo = new Dictionary<string, FontInfo>(StringComparer.Ordinal);
            // Numbered per stream, from zero. Ids do NOT need to be unique across streams: the
            // matcher compares text-object ids only after establishing that both glyphs came from
            // the same stream, so the pair (StreamId, TextObjectId) is what identifies a text
            // object. Offsetting them by `streamId * 100000` to make them globally unique would buy
            // nothing and overflow `int` past ~21,500 forms.
            int textObjectId = 0;

            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                switch (op.Operator)
                {
                    case PdfOperators.Save: stack.Push(gs.Clone()); break;
                    case PdfOperators.Restore:
                        if (stack.Count > 0)
                        {
                            // §9.3.1: the text matrices are not graphics state; Q leaves them where they are.
                            float[] tm = gs.Tm, tlm = gs.Tlm;
                            gs = stack.Pop();
                            gs.Tm = tm;
                            gs.Tlm = tlm;
                        }
                        break;
                    case PdfOperators.ConcatMatrix:
                        if (op.Operands.Length >= 6) gs.CTM = Compose(Six(op), gs.CTM);
                        break;

                    case PdfOperators.BeginText:
                        gs.Tm = Identity(); gs.Tlm = Identity(); textObjectId++;
                        break;

                    case PdfOperators.SetFont:
                        if (op.Operands.Length >= 2)
                        {
                            gs.FontName = (op.Operands[0] as PdfName)?.Value;
                            gs.FontSize = F(op, 1);
                        }
                        break;
                    // §8.4.5: an ExtGState's /Font sets the font and size as Tf does — read through the
                    // renderers' one reader, so a font set only this way is measured, not left at size 1.
                    case PdfOperators.SetExtGState:
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName stateName
                            && doc.ResolveObject(resources?[PdfName.ExtGState]) is PdfDictionary states
                            && doc.ResolveObject(states[stateName]) is PdfDictionary state
                            && ExtGStateFont.TryRead(doc, state, resources, "", out string gsFontName, out float? gsFontSize))
                        {
                            if (gsFontName != null) gs.FontName = gsFontName;
                            if (gsFontSize != null) gs.FontSize = gsFontSize.Value;
                        }
                        break;
                    case PdfOperators.SetCharSpacing: gs.Tc = F(op, 0); break;
                    case PdfOperators.SetWordSpacing: gs.Tw = F(op, 0); break;
                    case PdfOperators.SetHorizontalScaling: gs.Th = F(op, 0) / 100f; break;
                    case PdfOperators.SetLeading: gs.TL = F(op, 0); break;
                    case PdfOperators.SetTextRise: gs.Ts = F(op, 0); break;

                    case PdfOperators.SetGrayFill:
                        gs.FillColorOps = $"{Fmt(F(op, 0))} {PdfOperators.SetGrayFill}"; break;
                    case PdfOperators.SetRgbFill:
                        gs.FillColorOps = $"{Fmt(F(op, 0))} {Fmt(F(op, 1))} {Fmt(F(op, 2))} {PdfOperators.SetRgbFill}"; break;
                    case PdfOperators.SetCmykFill:
                        gs.FillColorOps = $"{Fmt(F(op, 0))} {Fmt(F(op, 1))} {Fmt(F(op, 2))} {Fmt(F(op, 3))} {PdfOperators.SetCmykFill}"; break;

                    // A fill set in a named space is replayed as the space and the colour: the run is
                    // drawn in the same resource context, so a /ColorSpace or /Pattern name it names is
                    // in force there. `cs` alone is the space's initial colour (§8.6.8).
                    case PdfOperators.SetColorSpaceFill:
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName space)
                            gs.FillColorOps = $"/{PdfNameEscape.Escape(space)} {PdfOperators.SetColorSpaceFill}";
                        break;
                    case PdfOperators.SetColorFill:
                    case PdfOperators.SetColorFillN:
                    {
                        var operands = new StringBuilder();
                        foreach (PdfObject operand in op.Operands)
                        {
                            if (operand is PdfNumber n) operands.Append(Fmt((float)n.DoubleValue)).Append(' ');
                            else if (operand is PdfName pn) operands.Append('/').Append(PdfNameEscape.Escape(pn)).Append(' ');
                        }
                        // The space set by the last cs, kept ahead of the colour; with no cs in force the
                        // fill is in the initial DeviceGray, which the operand count names as g, rg or k.
                        string csToken = " " + PdfOperators.SetColorSpaceFill;
                        int csAt = gs.FillColorOps?.IndexOf(csToken, StringComparison.Ordinal) ?? -1;
                        if (csAt >= 0)
                            gs.FillColorOps = gs.FillColorOps.Substring(0, csAt + csToken.Length) + " " + operands + op.Operator;
                        else if (op.Operands.Length == 1 || op.Operands.Length == 3 || op.Operands.Length == 4)
                            gs.FillColorOps = operands + (op.Operands.Length == 1 ? PdfOperators.SetGrayFill
                                                        : op.Operands.Length == 3 ? PdfOperators.SetRgbFill : PdfOperators.SetCmykFill);
                        break;
                    }

                    case PdfOperators.TextMove:
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, F(op, 0), F(op, 1) }, gs.Tlm);
                        gs.Tm = (float[])gs.Tlm.Clone(); break;
                    case PdfOperators.TextMoveSetLeading:
                        gs.TL = -F(op, 1);
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, F(op, 0), F(op, 1) }, gs.Tlm);
                        gs.Tm = (float[])gs.Tlm.Clone(); break;
                    case PdfOperators.SetTextMatrix:
                        if (op.Operands.Length >= 6) { gs.Tm = Six(op); gs.Tlm = (float[])gs.Tm.Clone(); }
                        break;
                    case PdfOperators.NextLine:
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, 0, -gs.TL }, gs.Tlm);
                        gs.Tm = (float[])gs.Tlm.Clone(); break;

                    case PdfOperators.ShowText:
                        WalkSegs(glyphs, i, Segs(op, 0), gs, resources, doc, fontCache, fontInfo, textObjectId, streamId);
                        break;
                    case PdfOperators.ShowTextArray:
                        WalkSegs(glyphs, i, ArraySegs(op), gs, resources, doc, fontCache, fontInfo, textObjectId, streamId);
                        break;
                    case PdfOperators.NextLineShowText:
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, 0, -gs.TL }, gs.Tlm);
                        gs.Tm = (float[])gs.Tlm.Clone();
                        WalkSegs(glyphs, i, Segs(op, 0), gs, resources, doc, fontCache, fontInfo, textObjectId, streamId);
                        break;
                    case PdfOperators.NextLineSetSpacingShowText:
                        // §9.4.3 Table 109: `aw ac string "`. Below three operands EVERY reader in
                        // this library — the raster and SVG renderers, PdfStructuredTextExtractor
                        // and Core's PdfTextExtractor — leaves Tm, Tlm, Tw and Tc untouched. This
                        // walk did not, and `F` returns 0 for a missing operand, so a one-operand
                        // `"` zeroed the spacing and moved the line origin down one leading. Every
                        // later line in that text object then sat one leading lower HERE than where
                        // it is actually painted, which puts a located token in the wrong place.
                        //
                        // Matching the readers is the requirement regardless of what a malformed
                        // operator "should" do: this engine's model of the text position has to be
                        // the renderer's model, or it edits the wrong coordinates.
                        if (op.Operands.Length < 3) break;

                        gs.Tw = F(op, 0); gs.Tc = F(op, 1);
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, 0, -gs.TL }, gs.Tlm);
                        gs.Tm = (float[])gs.Tlm.Clone();
                        WalkSegs(glyphs, i, Segs(op, 2), gs, resources, doc, fontCache, fontInfo, textObjectId, streamId);
                        break;

                    case PdfOperators.InvokeXObject:
                        DescendIntoForm(op, resources, doc, fontCache, gs, depth,
                                        glyphs, streams, uniquelyDrawn);
                        break;
                }
            }

            return gs.CTM;
        }

        /// <summary>
        /// Walks a Form XObject's own content so a token inside it is <b>found</b>, recording whether
        /// that form may be rewritten.
        /// </summary>
        /// <remarks>
        /// <para>A shared form is descended into anyway and its stream marked not-editable. Skipping
        /// it would leave the caller's key in <c>NotFoundKeys</c>, which reads as "your key is
        /// wrong" — a different problem from "the token is there and cannot be edited safely", and
        /// it sends them looking in the wrong place.</para>
        ///
        /// <para>The CTM passed down composes the form's <c>/Matrix</c> (§8.10.1) with the CTM in
        /// force at the <c>Do</c>, so every glyph position recorded inside the form is already in
        /// page space.</para>
        /// </remarks>
        private static void DescendIntoForm(
            ContentStreamOperation op, PdfDictionary resources, PdfDocument doc, FontCache fontCache,
            TState gs, int depth, List<Glyph> glyphs, List<StreamWork> streams,
            Func<int, bool> uniquelyDrawn)
        {
            if (depth >= PdfLimits.MaxFormXObjectDepth) return;
            if (op.Operands.Length < 1) return;

            string name = (op.Operands[0] as PdfName)?.Value;
            if (name == null) return;

            var xobjs = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
            if (xobjs == null) return;

            var entry = xobjs[new PdfName(name)];
            int objNum = entry is PdfReference reference ? reference.ReferenceObjectNumber : 0;
            if (!(doc.ResolveObject(entry) is PdfStream form)) return;
            if (PdfResolve.Name(doc, form, PdfName.Subtype)?.Value != "Form") return;

            // One StreamWork per form object however many times it is invoked: rewriting the same
            // stream twice would apply every edit twice.
            foreach (var existing in streams)
                if (ReferenceEquals(existing.Form, form)) return;

            byte[] inner;
            try { inner = form.GetDecodedData(); }
            catch (Exception) { return; }
            if (inner == null || inner.Length == 0) return;

            IReadOnlyList<ContentStreamOperation> innerOps;
            try { innerOps = ContentStreamParser.Parse(inner); }
            catch (Exception) { return; }

            float[] ctm = gs.CTM;
            if (doc.ResolveObject(form[PdfName.Matrix]) is PdfArray matrix && matrix.Count >= 6)
            {
                var fm = new float[6];
                for (int k = 0; k < 6; k++)
                    fm[k] = (float)PdfResolve.ArrayDouble(doc, matrix, k, k == 0 || k == 3 ? 1.0 : 0.0);
                ctm = Compose(fm, ctm);
            }

            bool editable = objNum > 0 && uniquelyDrawn(objNum);
            var work = new StreamWork
            {
                Id = streams.Count,
                Form = form,
                Resources = doc.ResolveObject(form[PdfName.Resources]) as PdfDictionary ?? resources,
                Ctm = ctm,
                Ops = innerOps,
                Editable = editable,
                NotEditableReason = editable
                    ? null
                    : $"the placeholder is inside Form XObject '/{name}', which is drawn more than once " +
                      "in this document; editing it would change every other place it appears",
            };
            streams.Add(work);

            Locate(innerOps, work.Resources, doc, fontCache, ctm, work.Id, depth + 1,
                   glyphs, streams, uniquelyDrawn);
        }

        private static void WalkSegs(
            List<Glyph> glyphs, int opIndex, List<object> segs, TState gs,
            PdfDictionary resources, PdfDocument doc, FontCache fontCache,
            Dictionary<string, FontInfo> fontInfo, int textObjectId, int streamId)
        {
            var fi = GetFont(gs.FontName, resources, doc, fontCache, fontInfo);
            float tfs = gs.FontSize, th = gs.Th;
            // §9.7.4.3: a vertical font's pen moves down the column. This walk lays a run out along
            // x, so a glyph of such a font is marked and a match over one is refused (TryBuildMatch).
            bool vertical = fi.Font is PdfType0Font composite && composite.IsVerticalWritingMode;

            for (int segIndex = 0; segIndex < segs.Count; segIndex++)
            {
                if (segs[segIndex] is float kern)
                {
                    float kdisp = -(kern / (float)PdfUnits.GlyphSpaceUnitsPerEm) * tfs * th;
                    gs.Tm = Compose(new[] { 1f, 0, 0, 1, kdisp, 0 }, gs.Tm);
                    continue;
                }

                var bytes = (byte[])segs[segIndex];
                int b = 0;
                foreach (var c in fi.SplitCodes(bytes))
                {
                    int len = Math.Max(1, Math.Min(c.ByteLength, bytes.Length - b));

                    // Keyed on the CID, not the raw code. A composite font's /W array is indexed
                    // by CID (§9.7.4.3), and CID == code only under Identity-H. Under a predefined
                    // CMap (UniJIS-UCS2-H and friends) the two differ, so keyed on the code every
                    // advance here would be the width of an unrelated glyph and every token
                    // position computed from it wrong.
                    // PdfFont.SplitCodes sets Cid == Value for a simple font, so this is right for
                    // both kinds; its doc comment says so.
                    // The one width rule (GlyphWidth.TextSpace): an explicit 0 in /Widths or /W is a real
                    // width, the default only for a font that declares nothing, and a Type 3 width
                    // goes through its /FontMatrix — as the renderers, the extractors and the
                    // redaction engine read it, so the erasure's TJ number, the slot and the
                    // replacement's start agree with where a reader draws the glyphs.
                    float wem = (float)GlyphWidth.TextSpace(fi.Font, c.Cid);

                    // §9.3.3: word spacing applies to the single-byte code 32 only. The rule lives
                    // on CMapCodec.Code; the inline copy here tested the CLAMPED length, so a
                    // 2-byte code truncated by the end of the string was treated as a space.
                    bool isSpace = c.IsWordSpaceCode;
                    float advX = (wem * tfs + gs.Tc + (isSpace ? gs.Tw : 0f)) * th;

                    var slice = new byte[len];
                    Array.Copy(bytes, b, slice, 0, len);
                    string text;
                    try { text = fi.Font?.Decode(slice) ?? string.Empty; }
                    catch (Exception) { text = string.Empty; }

                    glyphs.Add(new Glyph
                    {
                        OpIndex = opIndex,
                        SegIndex = segIndex,
                        ByteStart = b,
                        ByteLength = len,
                        Text = text,
                        AdvanceX = advX,
                        BaselineY = gs.Tm[5],
                        Trm = Compose(Compose(new[] { tfs * th, 0, 0, tfs, 0, gs.Ts }, gs.Tm), gs.CTM),
                        Ctm = gs.CTM,
                        TextToPage = Compose(gs.Tm, gs.CTM),
                        FontSize = tfs,
                        Th = th,
                        TextObjectId = textObjectId,
                        FillColorOps = gs.FillColorOps,
                        StreamId = streamId,
                        FontBaseName = fi.Font?.BaseFont,
                        Vertical = vertical,
                    });

                    gs.Tm = Compose(new[] { 1f, 0, 0, 1, advX, 0 }, gs.Tm);
                    b += len;
                }
            }
        }

        // ================================================================
        //  Pass 1b — match tokens across operator boundaries
        // ================================================================

        private sealed class TokenMatch
        {
            internal string Key;
            internal string Value;
            internal int First;
            internal int Last;
            internal float[] Trm;
            internal float[] Ctm;
            internal float SlotWidth;
            internal float FontSize;

            /// <summary>
            /// The placeholder's horizontal scaling (<c>Tz</c> / 100), which <see cref="SlotWidth"/> and
            /// the replacement's text matrix both carry: a run's natural width is compared against the
            /// slot scaled by it, or under <c>200 Tz</c> a value twice the slot is drawn unshrunk.
            /// </summary>
            internal float Th = 1f;
            internal int TextObjectId;
            internal string FillColorOps;
            internal int StreamId;

            /// <summary>The <c>/BaseFont</c> the placeholder was drawn in, whose family the replacement is set in where a face covers it.</summary>
            internal string FontBaseName;

            /// <summary>
            /// Reflow only: the lines the replacement is laid out over, in order. Null for every
            /// slot-preserving replacement, which draws one run on the placeholder's own baseline.
            /// </summary>
            /// <remarks>
            /// Carried on the match rather than recomputed at the emitter because the line breaking
            /// needs the paragraph's measure and the font, both of which the locator has and the
            /// emitter would have to re-derive — and a second derivation of "how many lines is
            /// this" is a different answer from the one the overflow check was made against.
            /// </remarks>
            internal IReadOnlyList<string> ReflowLines;

            /// <summary>Reflow only: the baseline step between those lines, in page space.</summary>
            internal float ReflowLeading;

            /// <summary>
            /// Reflow only: the paragraph was justified — every line but its last ended at one right
            /// edge — so every re-broken line but the last is spread to the measure.
            /// </summary>
            internal bool ReflowJustified;

            /// <summary>
            /// Reflow only: how many lines the paragraph originally had, and therefore how many the
            /// replacement may use before it would be drawn over what follows. Zero on every
            /// non-reflow match, which is what distinguishes the two.
            /// </summary>
            internal int ReflowLineCount;
        }

        private static List<TokenMatch> MatchTokens(
            List<Glyph> glyphs, List<StreamWork> streams, IReadOnlyDictionary<string, string> values,
            TemplateReplaceOptions options, int pageIndex, TemplateReplaceResult result)
        {
            var matches = new List<TokenMatch>();
            if (glyphs.Count == 0 || values.Count == 0) return matches;

            // The buffer spans show operators on purpose. A producer routinely splits "{{name}}"
            // across several Tj runs for kerning — and `{`/`}` is a kerning pair in most Latin
            // fonts, so the default delimiters are the ones MOST likely to be split. Matching
            // per-operator would therefore miss the common case rather than an exotic one.
            var sb = new StringBuilder();
            var owner = new List<int>();            // buffer offset -> glyph index
            for (int g = 0; g < glyphs.Count; g++)
            {
                string t = glyphs[g].Text;
                for (int c = 0; c < t.Length; c++) owner.Add(g);
                sb.Append(t);
            }
            string buffer = sb.ToString();

            string open = options.TokenOpen, close = options.TokenClose;
            int search = 0;
            while (search < buffer.Length)
            {
                int start = buffer.IndexOf(open, search, StringComparison.Ordinal);
                if (start < 0) break;
                int keyStart = start + open.Length;
                int end = buffer.IndexOf(close, keyStart, StringComparison.Ordinal);
                if (end < 0) break;

                string key = buffer.Substring(keyStart, end - keyStart);
                int lastOffset = end + close.Length - 1;
                search = end + close.Length;

                if (!values.TryGetValue(key, out string value)) continue;

                if (TryBuildMatch(glyphs, streams, owner[start], owner[lastOffset], key, value,
                                  pageIndex, result, out TokenMatch match))
                    matches.Add(match);
            }

            return matches;
        }

        /// <summary>The searches <see cref="MatchLiterals"/> has made on this thread — the evidence that a key is searched again only when the cursor passes its last hit.</summary>
        [ThreadStatic] internal static long LiteralSearches;

        /// <summary>
        /// Locates literal search strings rather than delimited tokens — the same scan with a
        /// different question.
        /// </summary>
        /// <remarks>
        /// <para><b>Longest key first, so a short key cannot shadow a longer one.</b> Replacing both
        /// <c>foo</c> and <c>foobar</c> would otherwise depend on dictionary order: <c>foo</c> would
        /// match the first three characters of <c>foobar</c> and leave <c>bar</c> stranded. Sorting
        /// by descending length makes the outcome the same whatever order the caller supplied.</para>
        ///
        /// <para><b>Matches never overlap.</b> The scan resumes after the end of each hit, so a key
        /// that occurs inside its own replacement is not re-entered, and two keys that overlap in
        /// the text are resolved left to right.</para>
        ///
        /// <para>The buffer spans show operators for the reason the token scan does: a producer
        /// splits a word across several <c>Tj</c> runs for kerning, so matching per-operator would
        /// miss the ordinary case rather than an exotic one.</para>
        /// </remarks>
        private static List<TokenMatch> MatchLiterals(
            List<Glyph> glyphs, List<StreamWork> streams, IReadOnlyDictionary<string, string> values,
            int pageIndex, TemplateReplaceResult result)
        {
            var matches = new List<TokenMatch>();
            if (glyphs.Count == 0 || values.Count == 0) return matches;

            var sb = new StringBuilder();
            var owner = new List<int>();            // buffer offset -> glyph index
            for (int g = 0; g < glyphs.Count; g++)
            {
                string t = glyphs[g].Text;
                for (int c = 0; c < t.Length; c++) owner.Add(g);
                sb.Append(t);
            }
            string buffer = sb.ToString();

            var keys = new List<string>(values.Keys);
            keys.Sort((a, b) => b.Length.CompareTo(a.Length));

            // Each key's next hit, kept: the buffer does not change, so a hit at or after the cursor
            // is still the key's earliest there, and a key found nowhere (-1) is never searched again.
            // Every key was searched to the end of the page after every match — matches × keys × text.
            var next = new int[keys.Count];
            for (int k = 0; k < next.Length; k++) next[k] = -2;

            int search = 0;
            while (search < buffer.Length)
            {
                int bestStart = -1;
                string bestKey = null;

                for (int k = 0; k < keys.Count; k++)
                {
                    string key = keys[k];
                    if (string.IsNullOrEmpty(key) || next[k] == -1) continue;

                    if (next[k] < search)
                    {
                        LiteralSearches++;
                        next[k] = buffer.IndexOf(key, search, StringComparison.Ordinal);
                    }
                    int at = next[k];
                    if (at < 0) continue;

                    // Earliest hit wins; ties go to the longer key, which `keys` order gives for
                    // free because the first key tested at a position is the longest.
                    if (bestStart < 0 || at < bestStart) { bestStart = at; bestKey = key; }
                }

                if (bestStart < 0) break;

                int lastOffset = bestStart + bestKey.Length - 1;
                search = bestStart + bestKey.Length;

                if (TryBuildMatch(glyphs, streams, owner[bestStart], owner[lastOffset], bestKey,
                                  values[bestKey], pageIndex, result, out TokenMatch match))
                    matches.Add(match);
            }

            return matches;
        }

        /// <summary>
        /// Locates the one span <paramref name="scope"/> names: the glyphs of its operations in the
        /// page's own content, which must stand together in the order drawn — a glyph of another
        /// operation among them is text drawn between the runs, which no slot holds. The span's own
        /// text becomes the key, and the pair is written into the scope's values.
        /// </summary>
        private static List<TokenMatch> MatchElement(
            List<Glyph> glyphs, List<StreamWork> streams, ElementScope scope, int pageIndex, TemplateReplaceResult result)
        {
            var matches = new List<TokenMatch>();
            int first = -1, last = -1;
            var shown = new HashSet<int>();
            for (int g = 0; g < glyphs.Count; g++)
            {
                if (glyphs[g].StreamId != 0 || !scope.OpIndexes.Contains(glyphs[g].OpIndex)) continue;
                shown.Add(glyphs[g].OpIndex);
                if (first < 0) first = g;
                last = g;
            }
            string what = scope.Describe();
            if (first < 0)
            {
                result.NotFoundList.Add(what);
                result.WarningList.Add(what + ": no text is shown by those operations of the page's own content; left untouched");
                return matches;
            }
            if (shown.Count < scope.OpIndexes.Count)
            {
                // An id that names no text of the page's own content is passed over, and said: the
                // caller's listing and these bytes may differ, and a silent pass-over hides it.
                var silent = new List<int>();
                foreach (int id in scope.OpIndexes) if (!shown.Contains(id)) silent.Add(id);
                silent.Sort();
                result.WarningList.Add(what + ": element" + (silent.Count == 1 ? " " : "s ") + string.Join(", ", silent) +
                                       " show no text of the page's own content and were passed over");
            }
            for (int g = first; g <= last; g++)
            {
                if (glyphs[g].StreamId == 0 && scope.OpIndexes.Contains(glyphs[g].OpIndex)) continue;
                result.NotFoundList.Add(what);
                result.WarningList.Add(what + ": other text is drawn between the runs named, so they are not one slot; left untouched");
                return matches;
            }

            string key = ReadingOrderOf(glyphs.GetRange(first, last - first + 1));
            scope.Values[key] = scope.Text;
            if (TryBuildMatch(glyphs, streams, first, last, key, scope.Text, pageIndex, result, out TokenMatch match, acrossTextObjects: true))
                matches.Add(match);
            else
                result.NotFoundList.Add(what);
            return matches;
        }

        /// <summary>
        /// Turns a located span of glyphs into a replaceable match, or refuses it with a reason.
        /// </summary>
        /// <remarks>
        /// <para><b>Everything here is independent of how the span was found</b>, which is what lets
        /// a second locator exist: the token scan and the literal scan differ only in which glyphs
        /// they nominate, and every judgement about whether that run can actually be replaced —
        /// one stream, one text object, one baseline — is the same question either way. Two copies
        /// could only agree or be a defect.</para>
        ///
        /// <para><b>"Contiguous in the buffer" is not "contiguous on the page".</b> Each check is a
        /// refusal naming the key, never a guess: a wrongly-placed replacement is worse than an
        /// untouched original, because the original is visible as a mistake and the misplacement
        /// looks deliberate.</para>
        ///
        /// <para><b>Runs a reader named</b> (<c>acrossTextObjects</c>) may stand in several text objects —
        /// a print driver often writes a word or a letter to an object, each under its own matrices — and
        /// are always measured on the page (<see cref="TrySpanOnThePage"/>), in one object or several: a
        /// <c>TJ</c>'s numbers are part of the slot, and a run drawn right to left starts at its left
        /// end. The replacement is drawn after the first object. A token found by its text keeps to one
        /// object and its glyphs' advances.</para>
        /// </remarks>
        private static bool TryBuildMatch(
            List<Glyph> glyphs, List<StreamWork> streams, int firstGlyph, int lastGlyph,
            string key, string value, int pageIndex, TemplateReplaceResult result, out TokenMatch match,
            bool acrossTextObjects = false)
        {
            match = null;

            // Same content stream. The buffer spans every stream reachable from the page, so a
            // token could otherwise "match" across the boundary between page content and a form
            // — two streams that are edited independently and drawn at unrelated times.
            if (glyphs[firstGlyph].StreamId != glyphs[lastGlyph].StreamId)
            {
                result.WarningList.Add(
                    $"{key} (page {pageIndex + 1}): the placeholder spans two content streams " +
                    "(page content and a Form XObject, or two forms); left untouched");
                return false;
            }

            for (int g = firstGlyph; g <= lastGlyph; g++)
            {
                if (!glyphs[g].Vertical) continue;
                // The slot, the erasure's TJ number and the replacement's layout are all along x; a
                // vertical column's are along y, which this engine does not lay out.
                result.WarningList.Add(
                    $"{key} (page {pageIndex + 1}): the placeholder is drawn in vertical writing mode (/WMode 1), " +
                    "which this engine does not lay out; left untouched");
                return false;
            }

            var stream = streams[glyphs[firstGlyph].StreamId];
            if (!stream.Editable)
            {
                // Reported against the key, not swallowed: "found but not editable" is a
                // different problem from "not found", and only one of them means the caller
                // mistyped something.
                result.NotEditableList.Add(key);
                result.WarningList.Add($"{key} (page {pageIndex + 1}): {stream.NotEditableReason}");
                return false;
            }

            float[] trm = glyphs[firstGlyph].Trm;
            float slot = 0;
            if (glyphs[firstGlyph].TextObjectId != glyphs[lastGlyph].TextObjectId && !acrossTextObjects)
            {
                result.WarningList.Add(
                    $"{key} (page {pageIndex + 1}): the placeholder spans two text objects, which have " +
                    "independent text matrices, so there is no single slot to erase; left untouched");
                return false;
            }
            if (acrossTextObjects)
            {
                // Runs a reader named are measured on the page, in one text object or several: a
                // TJ's numbers are part of the slot, and a run drawn right to left starts at its left end.
                if (!TrySpanOnThePage(glyphs, firstGlyph, lastGlyph, out slot, out float start))
                {
                    result.WarningList.Add(
                        $"{key} (page {pageIndex + 1}): the runs are not on one line of the page; left untouched");
                    return false;
                }
                // The replacement starts where the span does: its left end along the first glyph's baseline.
                var (x0, y0) = Rendering.PdfMatrix.Transform(glyphs[firstGlyph].TextToPage, 0, 0);
                var (x1, y1) = Rendering.PdfMatrix.Transform(glyphs[firstGlyph].TextToPage, start, 0);
                trm = (float[])trm.Clone();
                trm[4] += x1 - x0;
                trm[5] += y1 - y0;
            }
            else
            {
                float baseline = glyphs[firstGlyph].BaselineY;
                for (int g = firstGlyph; g <= lastGlyph; g++)
                {
                    if (Math.Abs(glyphs[g].BaselineY - baseline) <= SameBaselineTolerance) continue;

                    result.WarningList.Add(
                        $"{key} (page {pageIndex + 1}): the placeholder's glyphs are not on one baseline; left untouched");
                    return false;
                }
                for (int g = firstGlyph; g <= lastGlyph; g++) slot += glyphs[g].AdvanceX;
            }

            match = new TokenMatch
            {
                Key = key,
                Value = value,
                First = firstGlyph,
                Last = lastGlyph,
                Trm = trm,
                Ctm = glyphs[firstGlyph].Ctm,
                SlotWidth = slot,
                FontSize = glyphs[firstGlyph].FontSize,
                Th = glyphs[firstGlyph].Th > 1e-6f ? glyphs[firstGlyph].Th : 1f,
                TextObjectId = glyphs[firstGlyph].TextObjectId,
                FillColorOps = glyphs[firstGlyph].FillColorOps,
                StreamId = glyphs[firstGlyph].StreamId,
                FontBaseName = glyphs[firstGlyph].FontBaseName,
            };
            return true;
        }

        /// <summary>
        /// Where glyphs drawn under several text objects stand on the page, in the first glyph's text
        /// space: each glyph's origin and end (its <see cref="Glyph.AdvanceX"/>) are put on the page by
        /// its own matrices and read back through the first glyph's. False unless every glyph runs the
        /// first one's way and stands on its baseline — within a sixth of its height on the page, the
        /// same allowance whatever matrices the producer chose; otherwise <paramref name="slot"/> is the
        /// span from the leftmost start to the rightmost end, and <paramref name="start"/> its left end.
        /// </summary>
        private static bool TrySpanOnThePage(List<Glyph> glyphs, int firstGlyph, int lastGlyph, out float slot, out float start)
        {
            slot = 0;
            start = 0;
            float[] first = glyphs[firstGlyph].TextToPage;
            if (first == null || Rendering.PdfMatrix.Invert(first) is not float[] back) return false;

            // A text-space unit up the first glyph's baseline, in page units, and the allowance off it.
            double unitUp = Math.Sqrt(first[2] * first[2] + first[3] * first[3]);
            double height = Math.Sqrt(glyphs[firstGlyph].Trm[2] * glyphs[firstGlyph].Trm[2] + glyphs[firstGlyph].Trm[3] * glyphs[firstGlyph].Trm[3]);
            if (!(unitUp > 0) || !(height > 0)) return false;
            double allowance = height / 6 / unitUp;

            float left = float.MaxValue, right = float.MinValue;
            for (int g = firstGlyph; g <= lastGlyph; g++)
            {
                float[] own = glyphs[g].TextToPage;
                if (own == null) return false;
                var (ox, oy) = Rendering.PdfMatrix.Transform(own, 0, 0);
                var (ex, ey) = Rendering.PdfMatrix.Transform(own, glyphs[g].AdvanceX, 0);
                var (u0, v0) = Rendering.PdfMatrix.Transform(back, ox, oy);
                var (u1, v1) = Rendering.PdfMatrix.Transform(back, ex, ey);
                if (Math.Abs(v0) > allowance || Math.Abs(v1) > allowance) return false;
                if (u1 < u0 - 1e-3f) return false;   // drawn against the first glyph's direction
                left = Math.Min(left, u0);
                right = Math.Max(right, u1);
            }
            slot = right - left;
            start = left;
            return slot > 0 && !float.IsInfinity(slot) && !float.IsNaN(slot);
        }

        // ================================================================
        //  Font for the replacement text
        // ================================================================

        private sealed class EmbeddedFont
        {
            internal TrueTypeFontProgram Program;
            internal int ObjectNumber;
            internal Dictionary<ushort, int> GidToUnicode = new Dictionary<ushort, int>();
            /// <summary>A ligature glyph's typed letters, for <c>/ToUnicode</c> (<see cref="GlyphMapping.Text"/>).</summary>
            internal Dictionary<ushort, string> GidToText = new Dictionary<ushort, string>();
        }

        /// <summary>
        /// Resolves one font covering the union of every replacement's code points, or null when
        /// none is available. Null is not a failure of the operation: the placeholders are still
        /// erased, and each undrawn key is named in the result. The family asked for first is the
        /// one the first placeholder was drawn in — its <c>/BaseFont</c> without the subset tag and
        /// the style, the style as the bold and italic flags — so a replacement reads like the text
        /// round it where a face of that family covers it; the script fallbacks follow as before.
        /// </summary>
        private static EmbeddedFont ResolveReplacementFont(
            List<PageWork> pages, TemplateReplaceOptions options, TemplateReplaceResult result)
        {
            // `CodePointsOf` rather than a `foreach (char …)` — it combines surrogate pairs, which
            // this loop split into two lone halves that no cmap covers, so a replacement value
            // containing an emoji or a rarer CJK glyph could never resolve a font.
            var required = new HashSet<int>();
            string family = null;
            bool bold = false, italic = false;
            foreach (var work in pages)
                foreach (var m in work.Matches)
                {
                    foreach (int cp in Core.Text.TextRunComposer.CodePointsToCover(m.Value))
                        required.Add(cp);
                    if (family == null && !string.IsNullOrEmpty(m.FontBaseName))
                    {
                        string name = SubsetTag.Strip(m.FontBaseName);
                        bold = FontNameStyle.IsBold(name);
                        italic = FontNameStyle.IsItalic(name);
                        family = FamilyOf(name);
                    }
                }

            if (required.Count == 0) return null;

            var resolution = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    FamilyHint = family,
                    Bold = bold,
                    Italic = italic,
                    RequiredCodePoints = required,
                    AllowSystemFontSubstitution = options.AllowSystemFontSubstitution,
                },
                options.FontProvider);

            if (!resolution.Success)
            {
                result.WarningList.Add(
                    "no font is available for the replacement text (" + resolution.FailureReason +
                    "); the placeholders were erased but nothing was drawn in their place");
                return null;
            }

            return new EmbeddedFont { Program = resolution.Program };
        }

        /// <summary>
        /// The family part of a <c>/BaseFont</c> name with its subset tag already stripped: what
        /// stands before a comma (<c>David,Bold</c>) and before a hyphenated suffix that names a
        /// style (<c>Helvetica-BoldOblique</c>, <c>Arial-ItalicMT</c>); the whole name when it
        /// names no style that way.
        /// </summary>
        internal static string FamilyOf(string name)
        {
            int comma = name.IndexOf(',');
            if (comma > 0) name = name.Substring(0, comma);
            int hyphen = name.LastIndexOf('-');
            if (hyphen > 0)
            {
                string suffix = name.Substring(hyphen + 1);
                if (FontNameStyle.IsBold(suffix) || FontNameStyle.IsItalic(suffix)
                    || string.Equals(suffix, "Regular", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(suffix, "Roman", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, hyphen);
            }
            return name;
        }

        /// <summary>
        /// Builds the Type0 font once every glyph that will be drawn is known, and registers it and
        /// its descendants as extra objects for the rewrite.
        /// </summary>
        private static void FinishFont(EmbeddedFont font, ref int nextObjNum,
                                       Dictionary<int, PdfObject> extras)
        {
            if (font == null || font.GidToUnicode.Count == 0) return;

            var (type0ObjNum, objects) = Type0FontBuilder.BuildObjects(
                ref nextObjNum, font.Program, font.GidToUnicode, font.GidToText);

            foreach (var (objNum, obj) in objects) extras[objNum] = obj;
            font.ObjectNumber = type0ObjNum;
        }

        /// <summary>
        /// Adds the replacement font to the page's own <c>/Resources /Font</c>.
        ///
        /// <para>The dictionaries are <b>cloned onto the page</b> rather than mutated in place:
        /// <c>PdfPage.Resources</c> falls back to an inherited dictionary shared with every sibling
        /// page, so setting a key on it would add the font to pages that never use it — and, worse,
        /// a name collision would repaint another page's text in this font.</para>
        ///
        /// <para><b>The copy seeds from the EFFECTIVE resources, not the page's own key, and that
        /// distinction is the whole correctness of this method.</b> A page inheriting from a
        /// <c>/Pages</c> node has no own <c>/Resources</c>; seeding from
        /// <c>page.Dictionary[/Resources]</c> therefore started from <b>nothing</b>, and attaching
        /// the result gave the page an own dictionary holding the replacement font alone. §7.7.3.4
        /// makes an own <c>/Resources</c> <i>replace</i> the inherited one rather than merge with
        /// it, so the page lost every font and XObject it had been drawing with. The doc comment
        /// above had the hazard right — the inherited dictionary is shared — and the code then read
        /// the wrong key, which is why copy-on-write was correct and its seed was not.</para>
        ///
        /// <para><b>One copy for every page that starts from the same dictionary</b>
        /// (<see cref="PdfPageContentAppender.ResourceSharing"/>): each page adds the same font under
        /// the same name, so the pages inheriting one dictionary share one indirect copy of it. A
        /// direct copy on each page wrote the whole dictionary once a page — 9.9 MB from 224 KB at
        /// 800 pages sharing 800 XObjects.</para>
        /// </summary>
        private static void AttachFontResource(PdfPage page, PdfDocument doc, EmbeddedFont font, string resourceName,
            PdfPageContentAppender.ResourceSharing sharing, ref int nextObjNum, Dictionary<int, PdfObject> extras)
        {
            if (font == null || font.ObjectNumber <= 0 || resourceName == null) return;

            var effective = doc.ResolveObject(page.Dictionary[PdfName.Resources]) as PdfDictionary
                            ?? page.Resources;
            var fonts = new PdfDictionary();
            fonts.Set(new PdfName(resourceName), new PdfReference(font.ObjectNumber, 0));
            var incoming = new PdfDictionary();
            incoming.Set(PdfName.Font, fonts);

            var written = new List<IndirectObject>();
            PdfReference shared = sharing.Merged(effective, incoming, doc, ref nextObjNum, written);
            foreach (var o in written) extras[o.ObjectNumber] = o.Object;
            page.Dictionary.Set(PdfName.Resources, shared);
        }

        // ================================================================
        //  Pass 2 — erase and insert
        // ================================================================

        /// <summary>
        /// Rewrites every content stream this page's matches touch.
        /// </summary>
        /// <returns>Stream id → new bytes, for the streams that changed.</returns>
        /// <remarks>
        /// <para><b>Erasure happens in whichever stream the placeholder lives in; the replacement is
        /// always drawn into the page's own content.</b> That asymmetry is deliberate. A glyph
        /// position recorded inside a form is already in page space (the walk composed the form's
        /// CTM and <c>/Matrix</c>), so the replacement can carry an absolute text matrix and be
        /// drawn from the page — whereas splicing it into the form would draw it once per
        /// invocation of that form, and a form invoked twice is refused for editing anyway.</para>
        /// </remarks>
        private static Dictionary<int, byte[]> Emit(
            PageWork work, EmbeddedFont font, TemplateReplaceOptions options,
            TemplateReplaceResult result)
        {
            // streamId -> opIndex -> segIndex -> byte ranges to drop
            var drops = new Dictionary<int, Dictionary<int, Dictionary<int, List<Drop>>>>();
            foreach (var m in work.Matches)
            {
                for (int g = m.First; g <= m.Last; g++)
                {
                    var gl = work.Glyphs[g];
                    if (!drops.TryGetValue(gl.StreamId, out var byOp))
                        drops[gl.StreamId] = byOp = new Dictionary<int, Dictionary<int, List<Drop>>>();
                    if (!byOp.TryGetValue(gl.OpIndex, out var bySeg))
                        byOp[gl.OpIndex] = bySeg = new Dictionary<int, List<Drop>>();
                    if (!bySeg.TryGetValue(gl.SegIndex, out var list))
                        bySeg[gl.SegIndex] = list = new List<Drop>();
                    list.Add(new Drop { Start = gl.ByteStart, Length = gl.ByteLength, Advance = gl.AdvanceX });
                }
            }

            // Every match counts as replaced regardless of whether a run could be built, because the
            // erasure is unconditional: the placeholder is gone from the output either way. A token
            // erased but not redrawn is a different outcome from one never found, and conflating
            // them would tell a caller their template still says {{name}} when it now says nothing.
            var pageInserts = new Dictionary<int, List<string>>();   // text-object id -> runs
            var trailingInserts = new List<string>();                // runs whose token was in a form
            work.FontResource = ReplacementFontResourceFor(work.Page);
            foreach (var m in work.Matches)
            {
                result.ReplacedList.Add(m.Key);

                string run = BuildReplacementRun(m, font, work.FontResource, options, result, work.Streams[0].EndCtm);
                if (run == null) continue;

                if (m.StreamId == 0)
                {
                    if (!pageInserts.TryGetValue(m.TextObjectId, out var list))
                        pageInserts[m.TextObjectId] = list = new List<string>();
                    list.Add(run);
                }
                else
                {
                    // The enclosing ET is in the form's stream, not the page's, so there is no
                    // matching point to insert after — the run goes at the end of page content.
                    trailingInserts.Add(run);
                }
            }

            var output = new Dictionary<int, byte[]>();

            foreach (var stream in work.Streams)
            {
                bool isPage = stream.Id == 0;
                drops.TryGetValue(stream.Id, out var streamDrops);

                bool hasInserts = isPage && (pageInserts.Count > 0 || trailingInserts.Count > 0);
                if (streamDrops == null && !hasInserts) continue;   // untouched — leave it alone

                using var ms = new MemoryStream();
                var w = new PdfWriter(ms);
                int textObjectId = 0;   // per stream, matching Locate's numbering

                for (int i = 0; i < stream.Ops.Count; i++)
                {
                    var op = stream.Ops[i];
                    if (op.Operator == PdfOperators.BeginText) textObjectId++;

                    if (streamDrops != null && streamDrops.TryGetValue(i, out var bySeg) && IsShow(op.Operator))
                        EmitShowWithDrops(w, op, bySeg, work, stream.Id, i);
                    else
                        Verbatim(w, op);

                    // Inserted AFTER the ET, never inside the text object: a nested BT is malformed,
                    // and outside it the run carries its own absolute Tm and is immune to whatever
                    // state the original text object left behind.
                    if (isPage && op.Operator == PdfOperators.EndText &&
                        pageInserts.TryGetValue(textObjectId, out var runs))
                        foreach (string run in runs) w.WriteRaw(run);
                }

                if (isPage)
                    foreach (string run in trailingInserts) w.WriteRaw(run);

                output[stream.Id] = ms.ToArray();
            }

            return output;
        }

        /// <summary>
        /// A test seam: given a page index, a stream id (0 is the page's own content) and the
        /// rewritten bytes, returns the bytes the verifier and the document receive instead.
        /// </summary>
        /// <remarks>
        /// <see cref="VerifySurvivingGlyphs"/> changes an outcome only when the engine has
        /// <i>already</i> drifted, and a correct engine never does — so no document fixture can show
        /// it firing, and a test driven through <see cref="Replace"/> alone would pass whether or
        /// not the check works. This hands it a drift on purpose. Thread-static, so a test setting
        /// it affects only its own thread, and <c>null</c> — no effect — everywhere else.
        /// </remarks>
        [ThreadStatic] internal static Func<int, int, byte[], byte[]> RewrittenStreamTamper;

        /// <summary>
        /// A test seam: the bytes <see cref="VerifyErasure"/> re-reads in place of the produced
        /// document — never what is written to the caller's stream. Thread-static, <c>null</c> in
        /// production; see <see cref="RewrittenStreamTamper"/>.
        /// </summary>
        [ThreadStatic] internal static Func<byte[], byte[]> ProducedDocumentTamper;

        private struct Drop
        {
            internal int Start;
            internal int Length;
            internal float Advance;
        }

        /// <summary>
        /// Re-walks the rewritten stream and checks that every glyph which was <b>not</b> part of a
        /// token is still drawn at the same device position.
        /// </summary>
        /// <returns>A description of the first discrepancy, or <c>null</c> when none was found.</returns>
        /// <remarks>
        /// <para>Stated as "every surviving original glyph is present in the output" rather than as
        /// a sequence equality, because the output legitimately gains glyphs — the replacement runs
        /// — and a sequence check would have to know which those are. Presence-at-position tolerates
        /// insertion by construction while still catching removal and movement, which are the two
        /// ways this operation can go wrong.</para>
        ///
        /// <para>The replacement runs reference a font resource that is attached to the page only
        /// after this check, so their glyphs decode to empty text here. That is harmless: the check
        /// only ever asks whether the <i>original</i> glyphs are still where they were.</para>
        ///
        /// <para>What this cannot prove is that the page <b>renders</b> identically outside the
        /// slot. Identical glyph positions are not identical pixels — a text render mode, a clip
        /// path or a pattern fill can interact with the inserted <c>q…Q</c> in ways a geometric
        /// check does not see.</para>
        /// </remarks>
        private static string VerifySurvivingGlyphs(
            PageWork work, Dictionary<int, byte[]> byStream, PdfDocument doc, FontCache fontCache,
            Func<int, bool> uniquelyDrawn)
        {
            var after = new List<Glyph>();

            // Re-walk every stream from its post-edit bytes, in the same shape as the original walk,
            // so an edit inside a form is checked as strictly as one in page content.
            foreach (var stream in work.Streams)
            {
                byte[] bytes;
                if (!byStream.TryGetValue(stream.Id, out bytes))
                {
                    // Unchanged stream: its glyphs are trivially where they were, and re-walking the
                    // originals would only re-derive that. Skip, and let the presence check below
                    // account for them from the original list.
                    foreach (var g in work.Glyphs)
                        if (g.StreamId == stream.Id) after.Add(g);
                    continue;
                }

                IReadOnlyList<ContentStreamOperation> ops;
                try { ops = ContentStreamParser.Parse(bytes); }
                catch (Exception ex)
                {
                    return $"the rewritten stream {stream.Id} could not be re-parsed: {ex.Message}";
                }

                // Streams are walked in isolation here, without descending: each one's own glyphs
                // are what this check is about, and the nested streams are walked on their own turn.
                var scratch = new List<StreamWork>();
                Locate(ops, stream.Resources, doc, fontCache, stream.Ctm ?? Identity(), stream.Id, 0,
                       after, scratch, _ => false);
            }

            // Positions are compared within 0.01 pt — far below anything visible, and far above
            // float noise. A missing or wrongly-scaled adjustment moves text by whole points. The
            // keys are a 0.01 pt grid, and a glyph is looked for in its own cell and the eight
            // round it: a position a hair's breadth from a cell's edge, as the rounding of one
            // re-emitted adjustment leaves it, is in the next cell, not elsewhere.
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in after)
                if (g.Text.Length > 0) present.Add(PositionKey(g, 0, 0));

            var removed = new HashSet<int>();
            foreach (var m in work.Matches)
                for (int i = m.First; i <= m.Last; i++) removed.Add(i);

            for (int i = 0; i < work.Glyphs.Count; i++)
            {
                if (removed.Contains(i)) continue;
                var g = work.Glyphs[i];
                if (g.Text.Length == 0) continue;   // an undecodable code carries no claim

                bool found = false;
                for (int dx = -1; dx <= 1 && !found; dx++)
                    for (int dy = -1; dy <= 1 && !found; dy++)
                        found = present.Contains(PositionKey(g, dx, dy));
                if (!found)
                    return $"'{g.Text}' was at ({Fmt(g.Trm[4])}, {Fmt(g.Trm[5])}) and is no longer drawn there";
            }

            return null;
        }

        /// <summary>A glyph's text and the 0.01 pt cell it is drawn in, <paramref name="dx"/> and <paramref name="dy"/> cells over.</summary>
        private static string PositionKey(Glyph g, int dx, int dy) =>
            g.Text + "\0" +
            (SaturatingCast.ToLong(Math.Round(g.Trm[4] * 100.0)) + dx).ToString(CultureInfo.InvariantCulture) + "\0" +
            (SaturatingCast.ToLong(Math.Round(g.Trm[5] * 100.0)) + dy).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Re-emits a show operator as a <c>TJ</c> with the dropped codes removed and their total
        /// advance replaced by one numeric adjustment, so surviving text keeps its position.
        /// </summary>
        private static void EmitShowWithDrops(
            PdfWriter w, ContentStreamOperation op,
            Dictionary<int, List<Drop>> bySeg, PageWork work, int streamId, int opIndex)
        {
            var segs = op.Operator == PdfOperators.ShowTextArray
                ? ArraySegs(op)
                : Segs(op, op.Operator == PdfOperators.NextLineSetSpacingShowText ? 2 : 0);

            // The font size and horizontal scale in force at this operator, taken from a glyph the
            // operator produced — the walk already resolved them, so they are not re-derived here.
            // `th` really is read from the glyph now: it was assigned the literal 1 while the doc
            // comment claimed otherwise, which is the shape CLAUDE.md names as a defect marker.
            float tfs = 1f, th = 1f;
            foreach (var g in work.Glyphs)
                if (g.StreamId == streamId && g.OpIndex == opIndex) { tfs = g.FontSize; th = g.Th; break; }

            // The accumulator is ShowTextArrayBuilder, shared with PdfRedactionEngine — the two
            // carried identical flush pairs. The number format stays this engine's own; see the
            // remarks there for why the two deliberately differ.
            var builder = new ShowTextArrayBuilder(tfs, th, TemplateNumberFormat);

            for (int segIndex = 0; segIndex < segs.Count; segIndex++)
            {
                if (segs[segIndex] is float kern) { builder.Kern(kern); continue; }

                var bytes = (byte[])segs[segIndex];
                bySeg.TryGetValue(segIndex, out var segDrops);

                int b = 0;
                while (b < bytes.Length)
                {
                    Drop? hit = null;
                    if (segDrops != null)
                        foreach (var d in segDrops)
                            if (d.Start == b) { hit = d; break; }

                    if (hit.HasValue)
                    {
                        builder.Drop(hit.Value.Advance);
                        b += Math.Max(1, hit.Value.Length);
                    }
                    else
                    {
                        builder.Keep(bytes[b]);
                        b++;
                    }
                }
            }

            var elems = builder.Build();

            // A ' or " carries its own line movement, which must survive the rewrite.
            if (op.Operator == PdfOperators.NextLineSetSpacingShowText)
                w.WriteRaw($"{Fmt(F(op, 0))} {PdfOperators.SetWordSpacing} {Fmt(F(op, 1))} {PdfOperators.SetCharSpacing} ");
            if (op.Operator == PdfOperators.NextLineShowText ||
                op.Operator == PdfOperators.NextLineSetSpacingShowText)
                w.WriteRaw(PdfOperators.NextLine + " ");

            var sb = new StringBuilder("[");
            for (int i = 0; i < elems.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(elems[i]);
            }
            sb.Append("] ").Append(PdfOperators.ShowTextArray).Append('\n');
            w.WriteRaw(sb.ToString());
        }

        /// <summary>
        /// Builds the self-contained <c>q BT … ET Q</c> block that draws one replacement, or null
        /// when it cannot be drawn (the placeholder is still erased, and the caller is told).
        /// </summary>
        private static string BuildReplacementRun(
            TokenMatch m, EmbeddedFont font, string fontResource, TemplateReplaceOptions options,
            TemplateReplaceResult result, float[] pageEndCtm)
        {
            if (string.IsNullOrEmpty(m.Value)) return null;   // an empty value means "just remove it"

            if (font == null)
            {
                result.NotDrawnList.Add(m.Key);
                return null;
            }

            // One line for a slot-preserving replacement; the re-broken paragraph's own for a
            // reflowed one. Everything below is the same either way — the difference is how many
            // baselines the run is drawn on, which is the whole of what reflow changes.
            IReadOnlyList<string> lines = m.ReflowLines ?? (IReadOnlyList<string>)new[] { m.Value };

            var run = TextRunComposer.Compose(lines[0], font.Program, new TextRunOptions
            {
                BaseDirection = options.BaseDirection,
                MissingGlyphPolicy = MissingGlyphPolicy.Refuse,
            });

            if (!run.IsComplete)
            {
                // Partial coverage must refuse: silently dropping one unmapped character is how a
                // Hebrew name loses a letter and nobody notices.
                result.NotDrawnList.Add(m.Key);
                result.WarningList.Add(
                    $"{m.Key}: the replacement font has no glyph for U+{run.MissingCodePoint:X4}; " +
                    "the placeholder was erased but nothing was drawn in its place");
                return null;
            }

            run.RecordUsedGlyphs(font.GidToUnicode, font.GidToText);

            float size = m.FontSize;

            // Shrink-to-fit is the slot-preserving answer to "too wide" and must not run here:
            // reflow's answer is another line, and doing both would silently shrink a paragraph
            // that the caller asked to have re-broken at its own size.
            if (options.KeepSlot && m.ReflowLines == null && m.SlotWidth > 0)
            {
                // The slot is a sum of advances already scaled by the placeholder's Tz, and the run
                // is drawn under a text matrix carrying the same scale, so its natural width is
                // compared scaled too.
                double natural = run.WidthAt(size) * m.Th;
                if (natural > m.SlotWidth && natural > 0)
                {
                    // The floor is points on the page. The size here is Tf's, in text space, and the
                    // text and transformation matrices scale it: a driver writes `1 Tf` and carries the
                    // real size in Tm and cm, where a floor of 4 in Tf's units is a hundred points.
                    double pointsPerUnit = m.FontSize > 1e-6f
                        ? Math.Sqrt(m.Trm[2] * m.Trm[2] + m.Trm[3] * m.Trm[3]) / m.FontSize : 1;
                    double floor = pointsPerUnit > 1e-6 ? MinFitFontSize / pointsPerUnit : MinFitFontSize;
                    double shrunk = size * (m.SlotWidth / natural);
                    if (shrunk < floor)
                    {
                        result.WarningList.Add(
                            $"{m.Key}: the replacement does not fit the placeholder's width even at " +
                            $"{Fmt(MinFitFontSize)}pt; it was drawn at that size and overflows");
                        size = (float)floor;
                    }
                    else size = (float)shrunk;
                }
            }

            // The Trm captured at the placeholder's first glyph already includes the font size, the
            // horizontal scale, the rise and the CTM in force there, so the size is divided back out
            // to recover a text matrix in device-independent page space.
            //
            // A page-content run is inserted right after the placeholder's ET, inside whatever q
            // nesting the text object sat in — and q keeps the CTM, it does not reset it. `cm` cannot
            // appear inside BT, so the CTM there is the glyph's own, and it is divided back out too;
            // without that a text object under `1 0 0 1 100 50 cm` drew its replacement 100 points to
            // the right and 50 up. A run for a token inside a form goes at the end of page content,
            // so the CTM divided out there is the one the page's walk ended with — a page that ends
            // under an unmatched `cm` leaves it in force.
            float[] trm = m.Trm;
            float scale = m.FontSize > 1e-6f ? 1f / m.FontSize : 1f;
            float[] tm = { trm[0] * scale, trm[1] * scale, trm[2] * scale, trm[3] * scale, trm[4], trm[5] };
            float[] liveCtm = m.StreamId == 0 ? m.Ctm : pageEndCtm;
            if (liveCtm != null && Rendering.PdfMatrix.Invert(liveCtm) is float[] inverse)
                tm = Compose(tm, inverse);

            var sb = new StringBuilder();
            sb.Append(PdfOperators.Save).Append('\n');
            sb.Append(PdfOperators.BeginText).Append('\n');
            sb.Append(m.FillColorOps ?? ("0 " + PdfOperators.SetGrayFill)).Append('\n');
            sb.Append('/').Append(fontResource).Append(' ').Append(Fmt(size))
              .Append(' ').Append(PdfOperators.SetFont).Append('\n');
            // Text state survives ET (§9.3.1), so the placeholder's would carry into the run: Tz and
            // Ts are already in the matrix above and would apply twice, and Tc would spread glyphs
            // whose width was measured without it. Tw needs no reset — the run's codes are two bytes,
            // and word spacing applies only to the single-byte code 32 (§9.3.3). The render mode is
            // left as the placeholder's.
            sb.Append("0 ").Append(PdfOperators.SetCharSpacing)
              .Append(" 100 ").Append(PdfOperators.SetHorizontalScaling).Append(" 0 ").Append(PdfOperators.SetTextRise)
              .Append('\n');
            sb.Append(Fmt(tm[0])).Append(' ').Append(Fmt(tm[1])).Append(' ')
              .Append(Fmt(tm[2])).Append(' ').Append(Fmt(tm[3])).Append(' ')
              .Append(Fmt(tm[4])).Append(' ').Append(Fmt(tm[5])).Append(' ')
              .Append(PdfOperators.SetTextMatrix).Append('\n');
            // The rise was set to 0 above, which is what a placed mark's own rise is relative to. A
            // justified paragraph's lines but the last are spread to the measure.
            bool spreadFirst = m.ReflowJustified && lines.Count > 1;
            sb.Append(spreadFirst ? JustifiedShowText(run, size, m.SlotWidth / m.Th) : GlyphRunShowText.Operators(run, size)).Append('\n');

            // A reflowed paragraph continues on its own baselines. Each line gets a full Tm rather
            // than a Td or TL/T*: Td is relative to the LINE matrix, so a rounding error in one step
            // accumulates down the paragraph, and stating the matrix each time makes every baseline
            // exact and independent of the one above it.
            for (int i = 1; i < lines.Count; i++)
            {
                string lineShow = RunShowText(lines[i], font, options, m, result, size,
                    justifyTo: m.ReflowJustified && i < lines.Count - 1 ? m.SlotWidth / m.Th : 0);
                if (lineShow == null) return null;

                sb.Append(Fmt(tm[0])).Append(' ').Append(Fmt(tm[1])).Append(' ')
                  .Append(Fmt(tm[2])).Append(' ').Append(Fmt(tm[3])).Append(' ')
                  .Append(Fmt(tm[4])).Append(' ').Append(Fmt(tm[5] - m.ReflowLeading * i)).Append(' ')
                  .Append(PdfOperators.SetTextMatrix).Append('\n');
                sb.Append(lineShow).Append('\n');
            }

            sb.Append(PdfOperators.EndText).Append('\n');
            sb.Append(PdfOperators.Restore).Append('\n');
            return sb.ToString();
        }

        // ================================================================
        //  State + helpers
        // ================================================================

        private sealed class TState
        {
            internal float[] CTM = Identity(), Tm = Identity(), Tlm = Identity();
            internal string FontName;
            internal float FontSize = 1, Tc, Tw, TL, Ts;
            internal float Th = 1;
            internal string FillColorOps = "0 " + PdfOperators.SetGrayFill;

            internal TState Clone() => new TState
            {
                CTM = (float[])CTM.Clone(),
                Tm = (float[])Tm.Clone(),
                Tlm = (float[])Tlm.Clone(),
                FontName = FontName,
                FontSize = FontSize,
                Tc = Tc, Tw = Tw, TL = TL, Ts = Ts, Th = Th,
                FillColorOps = FillColorOps,
            };
        }

        private sealed class FontInfo
        {
            internal readonly PdfFont Font;

            /// <summary>
            /// Splits a show string into character codes through the font's own encoding CMap.
            /// <b>Not</b> a fixed stride: one taken from the narrowest codespace (the removed
            /// <c>FontCache.GetCharCodeSize</c>) means a mixed-width CMap (1-byte ASCII + 2-byte CJK) would split every
            /// 2-byte code in half and match tokens against the wrong characters.
            /// </summary>
            internal readonly Func<byte[], IReadOnlyList<CMapCodec.Code>> SplitCodes;

            internal FontInfo(PdfFont font, Func<byte[], IReadOnlyList<CMapCodec.Code>> splitCodes)
            {
                Font = font;
                SplitCodes = splitCodes;
            }
        }

        private static FontInfo GetFont(
            string fontName, PdfDictionary resources, PdfDocument doc,
            FontCache fontCache, Dictionary<string, FontInfo> cache)
        {
            string key = fontName ?? "";
            if (cache.TryGetValue(key, out var fi)) return fi;

            PdfFont font = ResourceFontLookup.Resolve(fontName, resources, doc);
            IReadOnlyList<CMapCodec.Code> Split(byte[] bytes)
            {
                try { return GlyphIdResolver.DecodeCodes(fontCache, fontName, resources, doc, bytes); }
                catch (Exception)
                {
                    // A CMap this codec cannot read is not a reason to abandon the page: fall back
                    // to one code per byte, which is exactly right for a simple font and is what the
                    // walk did unconditionally before.
                    var simple = new CMapCodec.Code[bytes.Length];
                    for (int i = 0; i < bytes.Length; i++) simple[i] = new CMapCodec.Code(bytes[i], bytes[i], 1);
                    return simple;
                }
            }

            fi = new FontInfo(font, Split);
            cache[key] = fi;
            return fi;
        }

        /// <summary>
        /// The highest object number the document actually uses, so newly minted objects cannot
        /// collide with an existing one.
        /// </summary>
        /// <remarks>
        /// <b>The trailer's <c>/Size</c> is not sufficient on its own.</b> It is a declaration, not
        /// a measurement, and a file with a broken or stale incremental-update trailer can declare
        /// fewer objects than it contains. A new object allocated inside that gap collides with a
        /// real one — and because the rewriter resolves caller-supplied objects <i>ahead</i> of the
        /// parser, the collision does not fail: the real object is silently replaced by the font,
        /// and whatever referenced it is now pointing at a font. The cross-reference table is the
        /// measurement; both are consulted and the larger wins.
        /// </remarks>
        private static int MaxObjectNumber(PdfDocument doc)
        {
            int max = 1;
            // A /Size past the int range numbers nothing: `(int)LongValue` kept its low 32 bits, so
            // 4 294 967 302 read as 6 and the figure meant to keep new objects clear of old ones
            // shrank; and at int.MaxValue the next number, max + 1, overflows. The cross-reference
            // table below decides instead.
            if (doc.Trailer[PdfName.Size] is PdfNumber size && size.IsInteger
                && size.LongValue > max && size.LongValue < int.MaxValue)
                max = (int)size.LongValue;

            try
            {
                int fromXref = doc.CrossReferenceTable?.GetMaxObjectNumber() ?? 0;
                if (fromXref > max) max = fromXref;
            }
            catch (Exception)
            {
                // An unreadable xref leaves the /Size figure standing; it is the weaker source but
                // the only one left, and returning something too small here is what must not happen.
            }

            return Math.Max(max, 1);
        }

        private static bool IsShow(string op) =>
            op == PdfOperators.ShowText || op == PdfOperators.ShowTextArray ||
            op == PdfOperators.NextLineShowText || op == PdfOperators.NextLineSetSpacingShowText;

        private static float[] Identity() => new float[] { 1, 0, 0, 1, 0, 0 };

        private static float[] Six(ContentStreamOperation op) =>
            new[] { F(op, 0), F(op, 1), F(op, 2), F(op, 3), F(op, 4), F(op, 5) };

        // Through Rendering.PdfMatrix — one of four byte-identical copies of §8.3.3.
        private static float[] Compose(float[] m, float[] n) => Rendering.PdfMatrix.Compose(m, n);

        private static float F(ContentStreamOperation op, int i) => ContentOperand.Float(op, i);

        /// <summary>
        /// This engine's <c>TJ</c> number precision. Named because <see cref="ShowTextArrayBuilder"/>
        /// takes it as a parameter — <see cref="PdfRedactionEngine"/> deliberately uses more.
        /// </summary>
        internal const string TemplateNumberFormat = "0.####";

        private static string Fmt(float v) => PdfRealFormat.Format(v, TemplateNumberFormat);

        // One implementation, in Core.Text.ContentOperand — this pair existed under two
        // names in two rewriters, differing only by a capacity hint.
        private static List<object> Segs(ContentStreamOperation op, int index)
            => ContentOperand.ShowTextSegments(op, index);

        private static List<object> ArraySegs(ContentStreamOperation op)
            => ContentOperand.ShowTextArraySegments(op);

        /// <summary>
        /// Writes an operation the edit leaves alone, through the one writer every rewriter keeps a
        /// parsed operation with (<see cref="ContentOperationWriter"/>): its numbers as the parser
        /// read them, an inline image as <c>BI</c>, its entries, <c>ID</c>, its bytes, <c>EI</c>. A
        /// number written through a <c>float</c> at four decimals is not the number read — a
        /// PostScript driver's page draws under a <c>cm</c> of <c>8.33333</c> and text matrices of
        /// <c>0.999436</c> — and the glyphs drawn under it then stand a hair from where they were,
        /// which <see cref="VerifySurvivingGlyphs"/> is there to refuse.
        /// </summary>
        private static void Verbatim(PdfWriter w, ContentStreamOperation op) => ContentOperationWriter.Write(w, op);
    }
}
