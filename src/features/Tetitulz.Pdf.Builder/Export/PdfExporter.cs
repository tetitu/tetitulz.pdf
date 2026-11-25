// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Exports a <see cref="Document"/> to PDF using <see cref="PdfDocumentBuilder"/>.
    ///
    /// This is the centralized PDF renderer shared by the document builder and (optionally)
    /// the DOCX/HTML converters. It performs greedy line-wrapping, multi-page pagination,
    /// paragraph alignment + full justification, per-run character formatting
    /// (underline/strike/highlight/super-subscript/char-spacing/horizontal-scale), tables
    /// (multi-run cells, gridSpan, vMerge, nested tables, per-side borders, cell vAlign /
    /// margins / shading, RTL column mirroring, content-derived row heights, row pagination,
    /// header-row repeat), per-page header/footer variants, and page borders.
    ///
    /// Unicode text (Hebrew/Arabic/CJK) is drawn with an embedded composite Type0/Identity-H
    /// font resolved from the OS (<see cref="BuilderFontResolver"/>); each wrapped line is
    /// Arabic-shaped + bidi-reordered (<see cref="RtlText"/>) and emitted as glyph ids.
    ///
    /// Deferred (the legacy DocxToPdf renderer still covers these): inline images,
    /// clickable /Link annotations, and live PAGE/NUMPAGES fields — they need /Annots,
    /// /Info and image-XObject hooks that <see cref="PdfDocumentBuilder"/> does not yet expose.
    /// </summary>
    public sealed partial class PdfExporter : IDocumentExporter
    {
        private const double DefaultTabInterval = 36;   // 0.5"

        // A non-left tab stop whose following text already overruns it collapses to this, rather
        // than to zero: the text would otherwise butt straight against what precedes the tab, and a
        // tab that visibly does nothing reads as a missing space rather than as an overfull line.
        private const double MinCollapsedTabWidth = 4;

        // The character a decimal tab aligns on. Not culture-derived on purpose: it is a property
        // of the DOCUMENT's content, and a server rendering a document written elsewhere must not
        // align it differently because of its own locale.
        private const char DecimalAlignSeparator = '.';

        // Longest run of leader characters one tab may emit. A leader is decoration filling a gap
        // that is at most a page wide, so this is unreachable for any real stop; it exists because
        // the count is derived from a file-supplied tab position divided by a measured glyph width,
        // and neither of those is this code's to trust.
        private const int MaxLeaderCharacters = 4096;

        /// <summary>Distance from the page edge at which a header/footer band is drawn (0.5&#160;in).</summary>
        private const double HeaderFooterOffsetPt = 36;

        /// <summary>Line leading as a multiple of the line's largest font size, when the paragraph
        /// sets no explicit <c>LineSpacingPt</c>.</summary>
        private const double DefaultLineHeightFactor = 1.16;

        /// <summary>Floor on a derived line height, so a line of very small text still advances a
        /// usable amount.</summary>
        private const double MinLineHeightPt = 14;

        /// <summary>Left indent added per list nesting level.</summary>
        private const double ListLevelIndentPt = 18;

        /// <summary>
        /// Minimum width reserved for a list label, so a one-character bullet still produces a
        /// hanging indent the body aligns to.
        /// </summary>
        private const double MinListLabelPt = 14;

        /// <summary>
        /// Size the label's advance is measured at when the item declares no format of its own.
        /// </summary>
        /// <remarks>
        /// Only the reserved <i>width</i> depends on it; the label is drawn in the item's own format
        /// like any other run, so a mismatch costs a few points of indent and never a wrong glyph.
        /// </remarks>
        private const double DefaultListMarkerSizePt = 10;

        /// <summary>Size of a superscript/subscript run relative to its nominal font size.</summary>
        private const double SuperSubFontScale = 0.65;

        /// <summary>Baseline rise of a superscript run, as a multiple of its font size.</summary>
        private const double SuperscriptRiseFactor = 0.35;

        /// <summary>Baseline drop of a subscript run, as a multiple of its font size.</summary>
        private const double SubscriptDropFactor = 0.15;

        /// <summary>Horizontal text scale (PDF <c>Tz</c>) meaning "no scaling", in percent.</summary>
        private const int NormalHorizontalScale = 100;

        /// <summary>
        /// Advance width, in glyph-space units, assumed for a character the standard-14 metrics
        /// tables have no entry for — half an em, which is close to the average across all four
        /// families and keeps measurement from collapsing to zero.
        /// </summary>
        private const double UnknownGlyphWidthUnits = 500;

        // Field placeholders: PAGE is resolved at draw time; NUMPAGES is back-filled once
        // the final page count is known. Mappers emit these literal tokens for page-number
        // fields (e.g. a PAGE field in a DOCX footer).
        public const string PagePlaceholder = "{{PAGE}}";
        public const string NumPagesPlaceholder = "{{NUMPAGES}}";

        private readonly PdfExportOptions _options;

        /// <summary>Exports with default options: host fonts allowed, unrenderable text reported.</summary>
        public PdfExporter() : this(null) { }

        /// <summary>Exports with caller-supplied font sources and failure policy.</summary>
        public PdfExporter(PdfExportOptions? options) => _options = options ?? PdfExportOptions.Default;

        /// <summary>
        /// Count of floated-box layout passes in the current export, for the regression test that a
        /// nested floated box is laid out a number of times linear in its depth, not 2^depth.
        /// <see cref="System.ThreadStaticAttribute"/> and reset at every export, like
        /// the diagnostic slots; read on the exporting thread.
        /// </summary>
        [ThreadStatic]
        internal static int FloatedBoxLayoutPasses;

        /// <summary>
        /// Count of float exclusions examined while placing floats in the current export, for the
        /// regression test that the work a page spends placing floats is bounded by
        /// <see cref="PdfLimits.MaxBuilderFloatsPerPage"/> rather than cubic in the floats on the page.
        /// <see cref="System.ThreadStaticAttribute"/> and reset at every export, like
        /// the diagnostic slots; read on the exporting thread.
        /// </summary>
        [ThreadStatic]
        internal static long FloatExclusionsExamined;

        /// <summary>
        /// Writes <paramref name="document"/> as a PDF, laying it out twice when it has a table of
        /// contents.
        /// </summary>
        /// <remarks>
        /// <para><b>A contents entry's page number is only knowable after the page it names has
        /// been laid out</b>, and the entry is usually in front of it. The first pass records where
        /// each heading landed and its output is discarded; the second draws the numbers.</para>
        ///
        /// <para><b>The two passes agree because the block's height does not depend on the
        /// numbers.</b> One line per entry either way, with the number right-aligned at the margin
        /// — so nothing after the contents moves between passes, and the numbers recorded in
        /// the first are the ones the second draws. That is what makes two passes enough rather
        /// than a fixed point to iterate towards.</para>
        ///
        /// <para><b>The cost is paid only by a document that asks for it.</b> A document with no
        /// contents block is laid out once, exactly as before.</para>
        /// </remarks>
        public void Export(Document document, Stream output)
        {
            FloatedBoxLayoutPasses = 0;
            FloatExclusionsExamined = 0;

            // Every measurement finite and every size non-negative, before anything is laid out —
            // the one walk the four exporters share.
            ModelMeasurements.Validate(document);

            if (HasToc(document))
            {
                // The scratch pass exists for its side effect on _tocPages; its bytes are thrown
                // away. A MemoryStream rather than Stream.Null because the builder seeks.
                _tocPages = new Dictionary<Paragraph, string>();
                using var scratch = new MemoryStream();
                ExportOnce(document, scratch);
            }

            // The kept pass's reports reach the caller's own handler too (PdfExportOptions.OnDiagnostic).
            var previousHandler = BuilderDiagnostics.ExportHandler;
            BuilderDiagnostics.ExportHandler = _options.OnDiagnostic;
            try { ExportOnce(document, output); }
            finally { _tocPages = null; BuilderDiagnostics.ExportHandler = previousHandler; }
        }

        /// <summary>Whether any section holds a contents block.</summary>
        private static bool HasToc(Document? document)
        {
            if (document == null) return false;

            foreach (Section section in document.Sections)
                foreach (IDocElement child in section.Children)
                    if (child is TocBlock) return true;

            return false;
        }

        /// <summary>
        /// The page each heading starts on — as its section formats it — from the previous pass;
        /// null on the first pass and on a document with no contents block.
        /// </summary>
        /// <remarks>
        /// An instance field, so two threads exporting two documents do not share it. The exporter
        /// is created per export at every call site in this repository.
        /// <para>The formatted text rather than the page index, because the format belongs to the
        /// section the heading is in, and that is known where the heading is drawn — not where the
        /// contents entry reads it back.</para>
        /// </remarks>
        private Dictionary<Paragraph, string>? _tocPages;

        /// <summary>
        /// Where the last <see cref="Export"/> drew each run that stands for a form field
        /// (<see cref="TextRun.FormField"/>), in document order; empty when there were none.
        /// </summary>
        /// <remarks>
        /// <para>Per instance, and replaced by every export pass — a document with a table of
        /// contents is laid out twice, and only the pass whose bytes are kept is reported. Hand the
        /// list to <c>Base.Operations.Forms.PdfFormFieldBuilder.AddFields</c> to make the fields
        /// real; <see cref="FormFieldSpec"/> says why this exporter does not write them itself.</para>
        /// </remarks>
        public IReadOnlyList<PlacedFormField> PlacedFormFields => _placedFormFields;
        private List<PlacedFormField> _placedFormFields = new();

        /// <summary>Every section laid out, its pages begun and finished; stops by <see cref="PageLimitReached"/> at the page limit.</summary>
        private void LayOutSections(Ctx ctx, Document document)
        {
            foreach (var section in document.Sections)
            {
                // The element is created BEFORE BeginSection, which draws the first page's
                // header — that draw must not land under the section element.
                var sectionEl = ctx.NewElement(PdfStructureTypes.Sect);
                // A block placed on every page (position: fixed) is drawn on each page the flow
                // leaves, so it is collected before the flow starts — wherever it stands in the
                // section, the first page has it — in z order; the last page draws it with the rest.
                ctx.FixedBlocks = section.Children
                    .OfType<Paragraph>()
                    .Where(p => p.Placement is { EveryPage: true })
                    .Select(p => (p, p.Placement!))
                    .OrderBy(b => b.Item2.ZIndex)
                    .ToList();
                ctx.BeginSection(section);
                using (ctx.EnterParent(sectionEl))
                {
                    // Positioned blocks are held back and drawn after the flow, sorted by z-index.
                    // Both halves are what `position` means: out of flow, and painted over it.
                    // OrderBy is a STABLE sort in LINQ, so blocks sharing a z-index keep document
                    // order — which is CSS's tie-break, and getting it from the sort rather than
                    // from a secondary key is why there is no secondary key here.
                    // The placement travels WITH the paragraph rather than being re-read from it.
                    // The null test below is the only thing that decides a block is positioned, so
                    // pairing them here is what carries that fact to the two places that use it —
                    // otherwise both have to re-assert an invariant this loop already established.
                    var positioned = new List<(Paragraph Para, AbsolutePlacement Place)>();
                    LooseListTags? loose = null;
                    for (int k = 0; k < section.Children.Count; k++)
                    {
                        var child = section.Children[k];
                        // Two or more consecutive loose list items are one /L, nested by their levels:
                        // each an /LI whose paragraph is its /LBody. Any other block ends the list. A
                        // lone item is no list and stays a /P (RenderBlock).
                        if (child is ListItem looseItem
                            && (loose != null || (k + 1 < section.Children.Count && section.Children[k + 1] is ListItem)))
                        {
                            loose ??= new LooseListTags();
                            using (ctx.EnterParent(loose.ItemFor(ctx, looseItem.Level)))
                            {
                                KeepTogetherIfItFits(ctx, section.Children, k, ctx.FlowWidth);
                                ctx.Y = RenderListItem(ctx, looseItem, ctx.FlowLeft, ctx.Y, ctx.FlowWidth, PdfStructureTypes.LBody);
                            }
                            continue;
                        }
                        loose = null;
                        if (child is Paragraph placed && placed.Placement != null)
                        {
                            // A placement against a box open here is the box's: attached
                            // as the box opened, drawn with the fragment its anchor falls in, from
                            // that fragment's padding box. Against the page, or a box not open here,
                            // held back as before.
                            if (placed.Placement.EveryPage || FindOpenBox(ctx.OpenBoxes, placed.Placement.ContainingBox) == null)
                                positioned.Add((placed, placed.Placement));
                            continue;
                        }
                        if (child is BoxStart box)
                        {
                            // A floated box: laid out whole at the band's edge, the blocks
                            // after it beside it, as a floated paragraph is; one the flow cannot float
                            // opens as a block of its float width on its side.
                            if (IsFloated(box))
                            {
                                int end = BoxStart.IndexOfEnd(section.Children, k);
                                double y = ctx.Y;
                                if (TryPlaceFloatedBox(ctx, section.Children, k, end, ctx.FlowLeft, ref y, ctx.FlowWidth,
                                                       measureOnly: false, paginate: true,
                                                       (b, bx, by, bw) => TableRenderer.DrawCellBlock(ctx, b, bx, by, bw),
                                                       (b, bx, by, bw) => TableRenderer.MeasureCellBlock(ctx, b, bx, by, bw)))
                                {
                                    ctx.Y = y;
                                    k = Math.Min(end, section.Children.Count - 1);
                                    continue;
                                }
                            }
                            // A box kept with the next block, or the next in a chain, starts a new page as a
                            // paragraph would, before it opens.
                            KeepTogetherIfItFits(ctx, section.Children, k, ctx.FlowWidth);   // before the box opens
                            OpenBox(ctx, box);
                            // The positioned paragraphs measured from this box, wherever they stand in
                            // it, so its first fragment knows them.
                            AttachPositioned(ctx.OpenBoxes[ctx.OpenBoxes.Count - 1], section.Children, k, BoxStart.IndexOfEnd(section.Children, k));
                            continue;
                        }
                        if (child is BoxEnd) { CloseBox(ctx); continue; }
                        KeepTogetherIfItFits(ctx, section.Children, k, ctx.FlowWidth);
                        RenderBlock(ctx, child, ctx.FlowLeft, ctx.FlowWidth);
                    }
                    CloseAllBoxes(ctx);

                    // The section's page-placed paragraphs and the ones a table cell deferred to it
                    //, in one z order.
                    foreach (var (para, place) in positioned.Concat(ctx.DeferredPositioned).OrderBy(b => b.Place.ZIndex))
                        RenderPositioned(ctx, para, place);
                    ctx.DeferredPositioned.Clear();
                    ctx.DeferredPositionedSeen.Clear();

                    // The endnotes, after the last section's content and before its last page closes
                    // — so they take that section's margins, header and footer, and flow onto further
                    // pages as body text does.
                    if (ReferenceEquals(section, document.Sections[document.Sections.Count - 1]))
                        DrawEndnotes(ctx);
                }
                // A footnote still continuing gets the pages it needs before the section closes: each
                // takes at least one of its lines, so this ends.
                while (ctx.CarriedNotes.Count > 0) ctx.NewPageContinue();
                ctx.FinishPage();
            }
        }

        /// <summary>
        /// Thrown by <see cref="Ctx.StartPage"/> instead of beginning the page past <see cref="PdfExportOptions.MaxPages"/>,
        /// and caught once, around the layout: every page begun before it was finished, so the document ends there.
        /// </summary>
        private sealed class PageLimitReached : Exception
        {
        }

        private void ExportOnce(Document document, Stream output)
        {
            // Cleared per export, on every path, so a value read afterwards is about THIS export.
            // A slot that is only ever written accumulates a previous document's answer, which is
            // exactly how a "did anything go wrong?" check comes back with a stale yes or no.
            BuilderDiagnostics.Clear();
            LastKeepBlocksMeasured = 0;
            _colorReportsThisExport = 0;
            _placedFormFields = new List<PlacedFormField>();

            var ctx = new Ctx
            {
                PlacedFormFields = _placedFormFields,
                Doc = new PdfDocumentBuilder().WithCompression(true)
                    .WithVersion(_options.Version ?? Tetitulz.Pdf.Core.Document.PdfVersion.Pdf14)
                    .WithMetadata(document.Title, document.Author, document.Subject),
                Fonts = new BuilderFontResolver(_options),
                Options = _options,
                // Only a cheap "is there any non-Latin-1 text at all?" test now. Resolution
                // itself is per RUN (Ctx.GetUniFont), because demanding that one font cover the
                // whole document's code points made a single CJK glyph break the Hebrew too.
                AnyCompositeText = CollectRequiredCodePoints(document).Count > 0,
                Tagged = document.Tagged,
                // Clamped once, here, and carried on the context: the `cm` StartPage writes and
                // the /Rect arithmetic in AddLink below must be the same number, and re-clamping
                // at each site is how two sites come to disagree.
                Scale = PdfExportOptions.ClampScale(_options.Scale),

                // Numbered once, before anything is laid out. Layout runs more than once per
                // paragraph -- MeasureParagraph lays one out to find its height and throws the
                // result away -- so a counter advanced during layout numbers some notes twice.
                Footnotes = FootnoteNumbering.For(document),

                // Null on the first pass: the contents draws a placeholder, and this pass exists
                // to fill the map the second one reads.
                TocPages = _tocPages,
                Document = document,
                MaxPages = _options.MaxPages,
                KeepMeasureBudget = KeepMeasureBudgetFor(document),
            };
            if (document.Tagged)
            {
                ctx.Parent = ctx.Doc.Structure.Root;
                if (!string.IsNullOrEmpty(document.Language)) ctx.Doc.Structure.Language = document.Language;
            }

            // Laid out until the document ends or the page limit is reached (PdfExportOptions.MaxPages): the
            // pages begun by then are whole, and everything after this — the page count, the links — is about them.
            try { LayOutSections(ctx, document); }
            catch (PageLimitReached)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.PageLimitReached,
                    $"the document reached the {_options.MaxPages}-page limit (PdfExportOptions.MaxPages); the rest of it was not laid out.");
            }
            finally { LastKeepBlocksMeasured = ctx.KeepBlocksMeasured; }

            // Never emit a zero-page PDF; a document with no content still gets one blank page
            // at the model's default size.
            if (!ctx.AnyPage) ctx.Doc.AddPage(DocDefaults.PageWidthPt, DocDefaults.PageHeightPt);

            // Back-fill the NUMPAGES placeholder now that the page count is final.
            // (The colour-report counter is reset at the top of Export, with the sink.)
            string total = ctx.AllPages.Count.ToString(CultureInfo.InvariantCulture);
            foreach (var pg in ctx.AllPages) pg.ReplaceInContent(NumPagesPlaceholder, total);

            // Unlike {{PAGE}}, which BuildPiece resolves BEFORE measurement, this substitution
            // happens after layout: every x on the field's line — the field's own advance, the
            // pieces after it, and the line's alignment offset — was computed from the 12-character
            // placeholder rather than from the 1- or 2-character count. Content that IS drawn but
            // not as the model described it is exactly what ContentApproximated is for; a two-pass
            // export is the real fix and a much larger change.
            if (ctx.UsedNumPagesField && total.Length != NumPagesPlaceholder.Length)
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                    $"the {{{{NUMPAGES}}}} field was substituted with \"{total}\" after layout, so " +
                    "positions on its line were measured against the 12-character placeholder: " +
                    "expect a gap after the number and an alignment offset on that line.");

            ctx.Doc.Build(output);

            string? raised = PdfFeatureVersions.RaiseReport(_options.Version, ctx.Doc.WrittenVersion!.Value, ctx.Doc.VersionRaisedBecause);
            if (raised != null) BuilderDiagnostics.Report(BuilderDiagnosticKind.VersionRaised, raised);
        }

        /// <summary>
        /// Treats two font programs as one when their embedded bytes are identical — so one physical
        /// face is embedded once however many families resolve to it. Reference equality is the fast
        /// path (a host face is the same cached instance); the byte comparison is the fallback for
        /// two instances of the same bytes (a provider handing them to two families).
        /// </summary>
        private sealed class FontProgramContentEquality : IEqualityComparer<TrueTypeFontProgram>
        {
            internal static readonly FontProgramContentEquality Instance = new FontProgramContentEquality();

            public bool Equals(TrueTypeFontProgram? x, TrueTypeFontProgram? y)
            {
                if (ReferenceEquals(x, y)) return true;
                byte[]? a = x?.FontData, b = y?.FontData;
                if (a == null || b == null) return false;
                return a.AsSpan().SequenceEqual(b);
            }

            public int GetHashCode(TrueTypeFontProgram program)
            {
                byte[]? d = program?.FontData;
                if (d == null || d.Length == 0) return 0;
                // Cheap and stable: the length and three sampled bytes. A collision only costs a
                // SequenceEqual in Equals, which is where correctness lives.
                int h = d.Length;
                h = h * 31 + d[0];
                h = h * 31 + d[d.Length / 2];
                h = h * 31 + d[d.Length - 1];
                return h;
            }
        }

        // ================================================================== context

        internal sealed class Ctx
        {
            /// <summary>Where this export pass drew each form-field run; the exporter's own list.</summary>
            public List<PlacedFormField> PlacedFormFields = new();

            /// <summary>The fields already placed, so a run split across lines places once.</summary>
            public readonly HashSet<FormFieldSpec> PlacedFieldSpecs = new();

            public PdfDocumentBuilder Doc = null!;
            public BuilderFontResolver Fonts = null!;
            public PdfExportOptions Options = PdfExportOptions.Default;

            /// <summary>
            /// The named families already reported as drawn in a standard-14 face — once each per
            /// export: a page in an unavailable Georgia reported every word, 169 diagnostics for one fact.
            /// </summary>
            public readonly HashSet<string> SubstitutedFamiliesReported = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// A scratch context that measures and never draws (<see cref="MeasureShrinkToFitWidth(Paragraph, double)"/>):
            /// what it would report, the export that draws the text reports.
            /// </summary>
            public bool MeasureOnly;

            /// <summary>The most pages this export begins (<see cref="PdfExportOptions.MaxPages"/>); 0 for no limit, as every measuring context has.</summary>
            public int MaxPages;

            /// <summary>
            /// The blocks keep decisions may still measure in this export (<see cref="KeepTogetherIfItFits"/>,
            /// <see cref="KeepMeasureBudgetFor"/>); unlimited in a measuring context, which makes none.
            /// </summary>
            public long KeepMeasureBudget = long.MaxValue;

            /// <summary>The blocks keep decisions have measured so far — <see cref="LastKeepBlocksMeasured"/>'s source.</summary>
            public long KeepBlocksMeasured;

            /// <summary>
            /// Each image's bytes decoded once this export (<see cref="DecodeOnce"/>), by the identity of the array — a
            /// picture drawn n times was decoded and compressed n times — so every drawing hands the writer the same data.
            /// </summary>
            public readonly Dictionary<object, DecodedImage?> DecodedImages =
                new Dictionary<object, DecodedImage?>(Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance);

            /// <summary>The image pixels this export may still decode (<see cref="PdfLimits.MaxImagePixelsPerExport"/>).</summary>
            public long ImagePixelsLeft = PdfLimits.MaxImagePixelsPerExport;

            /// <summary>
            /// Charges <paramref name="blocks"/> to <see cref="KeepMeasureBudget"/>, or, past it, reports once
            /// and answers false — the decision then keeps nothing.
            /// </summary>
            public bool SpendKeepMeasure(long blocks)
            {
                if (blocks > KeepMeasureBudget)
                {
                    if (KeepMeasureBudget >= 0)
                        BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                            "keep-together and keep-with-next decisions stopped at their budget (" + KeepMeasurePerBlock +
                            " measured blocks per block of the document); the blocks after it were laid out as if not kept.");
                    KeepMeasureBudget = -1;
                    return false;
                }
                KeepMeasureBudget -= blocks;
                KeepBlocksMeasured += blocks;
                return true;
            }

            /// <summary>
            /// Whether the document contains any character a standard-14 WinAnsi font cannot
            /// encode. Kept so a pure-ASCII document never touches the OS font list at all —
            /// that fast path is why the document-wide scan exists and it must survive the move
            /// to per-run resolution.
            /// </summary>
            public bool AnyCompositeText;

            /// <summary>
            /// The effective paper scale (<see cref="PdfExportOptions.Scale"/>), already clamped.
            /// Everything drawn goes through the page's leading <c>cm</c>; the only things that do
            /// not are annotation rectangles, which live in default user space.
            /// </summary>
            public double Scale = 1.0;

            /// <summary>
            /// Set once the constant-alpha approximation of CSS group opacity has been reported,
            /// so a shaded header repeated on 200 pages raises one diagnostic rather than 200.
            /// A channel that fires per page is one a caller learns to ignore.
            /// </summary>
            public bool ReportedOpacityApproximation;

            /// <summary>
            /// Set once a transformed box breaking across pages has been reported (each fragment
            /// about its own origin), so a long box says it once.
            /// </summary>
            public bool ReportedSlicedTransform;

            // ---------------------------------------------------------- tagging

            /// <summary>Emit a structure tree and marked content. Off unless the document asked.</summary>
            public bool Tagged;

            /// <summary>Where a new block element is attached. Null until <see cref="Tagged"/>.</summary>
            public PdfStructureElement? Parent;

            /// <summary>
            /// Non-null while everything drawn is page furniture: a running head, a rule, cell
            /// shading. Set through <see cref="ArtifactScope"/> — never assigned directly, because
            /// an early return would leak it and attribute the next paragraph's text to a header.
            /// </summary>
            public string? ArtifactType;
            public string? ArtifactSubtype;

            /// <summary>
            /// Opens a marked-content sequence for <paramref name="element"/>, or an artifact
            /// sequence when an artifact scope is open. Returns a disposable that closes it.
            ///
            /// <para>Returns a no-op when tagging is off, so every call site reads the same
            /// whether or not the document is tagged.</para>
            /// </summary>
            public MarkedContentScope Mark(PdfStructureElement? element)
            {
                // Inside an opacity group nothing is marked: the group's Do is marked as one
                // sequence on the page, and a form invoked inside a sequence is part of it (§14.7.4.4).
                if (!Tagged || InOpacityGroup) return default;
                if (ArtifactType != null)
                {
                    Page.BeginArtifact(ArtifactType, ArtifactSubtype);
                    return new MarkedContentScope(Page);
                }
                if (element == null) return default;
                Page.BeginMarkedContent(element);
                return new MarkedContentScope(Page);
            }

            /// <summary>Marks the enclosed drawing as an artifact of the given type.</summary>
            public MarkedContentScope MarkArtifact(string type, string? subtype = null)
            {
                if (!Tagged || InOpacityGroup) return default;
                Page.BeginArtifact(type, subtype);
                return new MarkedContentScope(Page);
            }

            /// <summary>
            /// The transparency groups being drawn into, innermost last: the content each stood in
            /// for, and the block whose opacity the group carries (a paragraph, or a
            /// <see cref="BoxStart"/>), which draws itself opaque inside it.
            /// </summary>
            private readonly Stack<(PdfContentStreamBuilder Outer, object? Owner)> _opacityGroups = new();

            /// <summary>
            /// True while <see cref="Page"/> is a transparency group's content rather than the page's
            /// (<see cref="BeginOpacityGroup"/>): nothing inside is marked and no structure element is
            /// made there (the group's <c>Do</c> is one sequence on the page), and the group's owner
            /// draws at opacity 1, the group carrying it.
            /// </summary>
            public bool InOpacityGroup => _opacityGroups.Count > 0;

            /// <summary>The block whose opacity the innermost group carries, or null outside a group.</summary>
            public object? OpacityGroupOwner => _opacityGroups.Count > 0 ? _opacityGroups.Peek().Owner : null;

            /// <summary>
            /// Starts drawing into a transparency group: <see cref="Page"/> becomes a fresh content
            /// builder with the standard fonts bound, and the page it stood for is returned for
            /// <see cref="EndOpacityGroup"/>. A paragraph with a box below full opacity draws itself
            /// into one, so its text and its background composite as one layer (CSS opacity) rather
            /// than each at the alpha (PDF's <c>/ca</c>), which let the background show through the text;
            /// a box of blocks below full opacity draws each page's fragment into one.
            /// Groups nest: a group begun inside another is the outer group's content.
            /// </summary>
            public PdfContentStreamBuilder BeginOpacityGroup(object? owner = null)
            {
                PdfContentStreamBuilder outer = Page;
                Page = new PdfContentStreamBuilder();
                BindStandardFonts();
                _opacityGroups.Push((outer, owner));
                return outer;
            }

            /// <summary>
            /// Closes the group begun by <see cref="BeginOpacityGroup"/>: <paramref name="outer"/> is
            /// the page again, the group is registered on it over the whole page's box, and painted
            /// once at <paramref name="alpha"/> — fill and stroke alike — inside one marked-content
            /// sequence for <paramref name="element"/>, which a form invoked inside a sequence
            /// belongs to (§14.7.4.4). The group's links and anchors move to the page with it.
            /// </summary>
            public void EndOpacityGroup(PdfContentStreamBuilder outer, double alpha, PdfStructureElement? element)
            {
                PdfContentStreamBuilder group = Page;
                Page = outer;
                BindStandardFonts();
                if (_opacityGroups.Count > 0) _opacityGroups.Pop();
                string name = Page.AddTransparencyGroup(group, 0, 0, Section.PageWidthPt, Section.PageHeightPt);
                using (Mark(element))
                {
                    Page.SaveState();
                    Page.SetGraphicsState(Page.AddExtGState((float)alpha, (float)alpha));
                    Page.DrawForm(name);
                    Page.RestoreState();
                }
            }

            /// <summary>
            /// Makes everything drawn inside the scope an artifact, restoring the previous state
            /// on exit — including across the page break a header/footer draw sits inside.
            /// </summary>
            public ArtifactScope EnterArtifact(string type, string? subtype = null)
                => new ArtifactScope(this, type, subtype);

            /// <summary>Attaches new block elements under <paramref name="parent"/> for the scope's lifetime.</summary>
            public ParentScope EnterParent(PdfStructureElement? parent) => new ParentScope(this, parent);

            /// <summary>
            /// Creates a child of the current parent, or null when tagging is off. Inside a
            /// transparency group an element gets no content (<see cref="Mark"/> marks nothing there:
            /// the group's <c>Do</c> is the one sequence), and the document builder prunes a childless
            /// element with none when it writes the tree, so nothing is gated on the group here.
            /// </summary>
            public PdfStructureElement? NewElement(string role)
                => Tagged && Parent != null ? Parent.AddChild(role) : null;


            // Resolved faces, keyed by what was asked for. The value is a LIST because one
            // (family, style) can need several programs: a run of Hebrew and a run of CJK ask
            // the same question and need different answers.
            public readonly Dictionary<(string family, bool bold, bool italic),
                List<(PdfDocumentBuilder.EmbeddedFont handle, TrueTypeFontProgram prog)>> UniFonts =
                new(FaceKeys);

            // A family name is the document's, so it hashes under a key the document cannot see
            // (KeyedHash): the tuple's own hash is the runtime's string hash, which .NET Framework does
            // not key.
            private static readonly IEqualityComparer<(string family, bool bold, bool italic)> FaceKeys =
                KeyedHash.Comparer<(string family, bool bold, bool italic)>(
                    k => unchecked((KeyedHash.Of(k.family) * 31 + (k.bold ? 1 : 0)) * 31 + (k.italic ? 1 : 0)));

            // The embedded-font handle already minted for a resolved program, keyed by the program's
            // content, so two families that fall back to the same face share one handle and
            // AddEmbeddedFont (which never dedupes) embeds the one physical font once. A host face
            // resolves to the same instance (the file cache's entry), so the reference-equal fast
            // path catches it; a provider that hands the same bytes to two families gives distinct
            // instances, so the content comparison catches that too.
            public readonly Dictionary<TrueTypeFontProgram, PdfDocumentBuilder.EmbeddedFont> EmbeddedByProgram =
                new(FontProgramContentEquality.Instance);

            public Section Section = null!;

            // The current section's margins after PageContentBox.Fit has let any that swallow the
            // page give way to zero — computed once per section in BeginSection, so the give-way
            // diagnostic fires once rather than on every geometry read. Equal to the Section's own
            // margins for every page with a usable content box, which is every real one.
            private double _effMarginTop, _effMarginBottom, _effMarginLeft, _effMarginRight;

            /// <summary>The section's top margin, after margins that swallow the page give way (<see cref="Model.PageContentBox"/>).</summary>
            public double EffMarginTop => _effMarginTop;
            /// <summary>The section's bottom margin, after the give-way.</summary>
            public double EffMarginBottom => _effMarginBottom;
            /// <summary>The section's left margin, after the give-way.</summary>
            public double EffMarginLeft => _effMarginLeft;
            /// <summary>The section's right margin, after the give-way.</summary>
            public double EffMarginRight => _effMarginRight;

            /// <summary>The document being exported, so a block can ask about the whole of it.</summary>
            /// <remarks>
            /// A table of contents is the first block whose content depends on paragraphs outside
            /// its own section -- every other one is laid out from what it holds.
            /// </remarks>
            public Document Document = null!;
            public PdfContentStreamBuilder Page = null!;
            public string FRegular = "", FBold = "", FItalic = "", FBoldItalic = "";
            public string FMono = "", FMonoBold = "", FMonoItalic = "", FMonoBoldItalic = "";
            public string FSerif = "", FSerifBold = "", FSerifItalic = "", FSerifBoldItalic = "";
            public double Y;
            public int PageInSection;
            public int GlobalPage;
            public bool AnyPage;

            /// <summary>
            /// Whether any run carried the <c>{{NUMPAGES}}</c> field, so the back-fill can report
            /// that the surrounding line was measured against the placeholder rather than the
            /// number that replaced it.
            /// </summary>
            public bool UsedNumPagesField;

            /// <summary>Whether a link left unwritten for its scheme (<see cref="LinkTargets"/>) was reported this export.</summary>
            public bool UnsafeLinkReported;

            /// <summary>
            /// One-shot guards for the table diagnostics. A pathological model is wide as well as
            /// deep, so an unguarded report would mint one message and one <c>OnEvent</c> call per
            /// dropped subtree — the flood shape <c>PsGraphicsEngine</c>'s depth cap already
            /// demonstrated. One report per export says everything a caller needs.
            /// </summary>
            public bool ReportedTableDepthLimit;
            /// <summary>See <see cref="ReportedTableDepthLimit"/>.</summary>
            public bool ReportedMergedCellClamp;
            /// <summary>See <see cref="ReportedTableDepthLimit"/>.</summary>
            public bool ReportedTableColumnClamp;
            // Every page's content builder, so the NUMPAGES placeholder can be back-filled
            // with the final page count before the document is written.
            public readonly List<PdfContentStreamBuilder> AllPages = new();

            // Float exclusions on the CURRENT page, in logical points, cleared by StartPage.
            //
            // Per page, because that is the scope a float has: text on the next page flows at the
            // full measure whatever sat beside it on this one. Keeping them across a page break
            // would narrow every line of a document after its first floated image.
            private readonly List<FloatExclusion> _pageFloats = new List<FloatExclusion>();

            /// <summary>A floated box that text must flow around, in logical points.</summary>
            /// <remarks>
            /// <c>Top</c> is the larger Y and <c>Bottom</c> the smaller, because PDF user space
            /// grows upward and the exporter's cursor descends. Named rather than implied: a
            /// rectangle whose Top is below its Bottom overlaps nothing and silently disables the
            /// exclusion.
            /// </remarks>
            internal readonly struct FloatExclusion
            {
                public FloatExclusion(double left, double right, double top, double bottom)
                {
                    Left = left; Right = right; Top = top; Bottom = bottom;
                }

                public double Left { get; }
                public double Right { get; }
                public double Top { get; }
                public double Bottom { get; }
            }

            /// <summary>
            /// Registers a float on the current page, up to <see cref="PdfLimits.MaxBuilderFloatsPerPage"/>
            /// — past which the page keeps no more exclusions, so scanning them stays bounded however
            /// many floats the flow places. The float past the cap still draws; it just does not push
            /// the text beside it.
            /// </summary>
            internal void AddFloat(FloatExclusion exclusion)
            {
                if (_pageFloats.Count >= PdfLimits.MaxBuilderFloatsPerPage) return;
                _pageFloats.Add(exclusion);
            }

            /// <summary>
            /// Takes the page's floats away, leaving the registry empty, so a measure can register
            /// floats of its own at its own coordinates (<c>TableRenderer.MeasureCell</c>);
            /// <see cref="RestoreFloats"/> puts them back.
            /// </summary>
            internal List<FloatExclusion> DetachFloats()
            {
                var floats = new List<FloatExclusion>(_pageFloats);
                _pageFloats.Clear();
                return floats;
            }

            /// <summary>Puts back what <see cref="DetachFloats"/> took, dropping whatever was registered since.</summary>
            internal void RestoreFloats(List<FloatExclusion> floats)
            {
                _pageFloats.Clear();
                _pageFloats.AddRange(floats);
            }

            /// <summary>True when any float is registered, so the common path can skip the walk.</summary>
            internal bool HasFloats => _pageFloats.Count > 0;

            /// <summary>How many floats the page holds: the most steps a float can take down past them.</summary>
            internal int FloatCount => _pageFloats.Count;

            /// <summary>
            /// The bottom of the float nearest below <paramref name="top"/> among those a line there
            /// (<paramref name="lineHeight"/> tall, in the band) overlaps — the least distance a box
            /// that does not fit beside them can drop to ask again; <paramref name="top"/> itself when
            /// none overlaps.
            /// </summary>
            internal double NearestFloatBottom(double bandLeft, double bandWidth, double top, double lineHeight)
            {
                double nearest = double.MinValue;
                double lineBottom = top - lineHeight, x1 = bandLeft + bandWidth;
                FloatExclusionsExamined += _pageFloats.Count;
                for (int i = 0; i < _pageFloats.Count; i++)
                {
                    FloatExclusion f = _pageFloats[i];
                    if (f.Bottom >= top || f.Top <= lineBottom) continue;
                    if (f.Right <= bandLeft || f.Left >= x1) continue;
                    if (f.Bottom > nearest) nearest = f.Bottom;
                }
                return nearest == double.MinValue ? top : nearest;
            }

            /// <summary>
            /// The lowest edge of any float on this page, or <see cref="double.MaxValue"/> when
            /// there is none — the y a <c>clear</c> has to drop to.
            /// </summary>
            /// <remarks>
            /// <c>MaxValue</c> rather than 0 for the empty case, so a caller taking
            /// <c>Math.Min(cursor, LowestFloatBottom)</c> is unaffected by a page with no floats.
            /// Returning 0 would drop every cleared paragraph to the foot of the page.
            /// </remarks>
            internal double LowestFloatBottom
            {
                get
                {
                    double lowest = double.MaxValue;
                    for (int i = 0; i < _pageFloats.Count; i++)
                        if (_pageFloats[i].Bottom < lowest) lowest = _pageFloats[i].Bottom;
                    return lowest;
                }
            }

            /// <summary>
            /// The usable horizontal extent for a line whose top is at <paramref name="top"/> and
            /// which is <paramref name="lineHeight"/> tall, within the band
            /// [<paramref name="bandLeft"/>, <paramref name="bandLeft"/> +
            /// <paramref name="bandWidth"/>] and clear of every float it vertically overlaps.
            /// </summary>
            /// <remarks>
            /// <b>The shape `DocxToPdf`'s `GetLineSegment` already proves</b>, in that exporter's
            /// own `DocxRenderContext`. Not shared with it: that one resolves against a column and
            /// a page it owns, this one against a band handed in per call, and the two contexts
            /// have no common base. The rule they do share is that a float left of the band's
            /// midpoint pushes the start right and one right of it shrinks the width.
            /// </remarks>
            internal (double left, double width) GetLineSegment(
                double bandLeft, double bandWidth, double top, double lineHeight)
            {
                double x0 = bandLeft;
                double x1 = bandLeft + bandWidth;
                if (_pageFloats.Count == 0) return (x0, Math.Max(1, x1 - x0));

                double lineBottom = top - lineHeight;
                double mid = (x0 + x1) / 2.0;

                FloatExclusionsExamined += _pageFloats.Count;
                for (int i = 0; i < _pageFloats.Count; i++)
                {
                    FloatExclusion f = _pageFloats[i];
                    if (f.Bottom >= top || f.Top <= lineBottom) continue;   // no vertical overlap
                    if (f.Right <= x0 || f.Left >= x1) continue;            // not in this band

                    double floatMid = (f.Left + f.Right) / 2.0;
                    if (floatMid <= mid) x0 = Math.Max(x0, f.Right);
                    else x1 = Math.Min(x1, f.Left);
                }

                return (x0, Math.Max(1, x1 - x0));
            }

            /// <summary>
            /// The section's content areas (<see cref="Model.Section.ContentAreas"/>, nulls passed
            /// over), empty for the margins' one region; and which of them the flow is in.
            /// </summary>
            private List<ContentArea> _areas = new();
            private int _area;

            /// <summary>The region the flow is in: the current content area, or null for the margins'.</summary>
            private ContentArea? Area => _area < _areas.Count ? _areas[_area] : null;

            /// <summary>
            /// How many regions the flow has entered — each page, and each content area of a page —
            /// so a block can tell that it broke where the page did not change.
            /// </summary>
            public int FlowRegion { get; private set; }

            /// <summary>Whether the flow is in the page's first region — the margins', or its first content area.</summary>
            public bool InFirstContentArea => _area == 0;

            /// <summary>
            /// The height of the region a break would take the flow to — the page's next content area,
            /// else the next page's first, else the margins' text block — before any footnotes take from it.
            /// </summary>
            public double NextRegionHeight =>
                _areas.Count == 0 ? Section.PageHeightPt - EffMarginTop - EffMarginBottom
                : _areas[_area + 1 < _areas.Count ? _area + 1 : 0].HeightPt;

            /// <summary>
            /// How far right of the section's first region the current one is: a block that began in
            /// one region and continues in another adds the difference of this between the two to the
            /// left edge it was laid out at.
            /// </summary>
            public double RegionShiftX => _areas.Count == 0 ? 0 : _areas[_area].LeftPt - _areas[0].LeftPt;

            public double ContentLeft => Area?.LeftPt ?? EffMarginLeft;
            public double ContentRight => Area is ContentArea a ? a.LeftPt + a.WidthPt : PageContentRight;
            public double ContentWidth => Math.Max(1, ContentRight - ContentLeft);

            /// <summary>The right margin's edge — the page's, whatever region the flow is in.</summary>
            public double PageContentRight => Section.PageWidthPt - EffMarginRight;

            /// <summary>The bottom of the region the flow is in, before any footnotes take from it.</summary>
            public double ContentBottom => Area is ContentArea a ? Section.PageHeightPt - a.TopPt - a.HeightPt : EffMarginBottom;
            /// <summary>
            /// The lowest Y body content may reach — the bottom margin, plus whatever this page
            /// has reserved for footnotes.
            /// </summary>
            /// <remarks>
            /// <b>Reserving by raising the limit is what makes the rest of the layout correct for
            /// free.</b> Every page break in this exporter goes through <c>EnsureSpace</c>, which
            /// compares against this — so a page carrying notes breaks earlier than one that
            /// does not, and nothing else has to know footnotes exist. Drawing the notes into the
            /// margin instead would have needed every caller of <c>EnsureSpace</c> to subtract them.
            /// </remarks>
            public double BottomLimit => ContentBottom + NoteAreaHeight;

            /// <summary>Footnotes whose reference mark has been drawn on this page.</summary>
            public readonly List<Model.Footnote> PendingNotes = new();

            /// <summary>Height reserved at the foot of this page for <see cref="PendingNotes"/>.</summary>
            public double NoteAreaHeight;

            /// <summary>
            /// The parts of footnotes that ran past the foot of this page — the rest of a note split here,
            /// and any note after it on the same line, whole — drawn first at the foot of the next page.
            /// </summary>
            public readonly List<Paragraph> CarriedNotes = new();

            /// <summary>This page's continued parts, carried from the page before, drawn first under a full-width rule.</summary>
            private readonly List<Paragraph> _continuedNotes = new();

            /// <summary>The head drawn on this page of a note split here, by note.</summary>
            private readonly Dictionary<Model.Footnote, Paragraph> _noteHeads = new();

            /// <summary>Each later part of a split note, mapped to the note's first part.</summary>
            private readonly Dictionary<Paragraph, Paragraph> _noteFirstPart = new(PdfReferenceEquality.Instance);

            /// <summary>The <c>/Note</c> element each split note's first part was drawn as, by that part.</summary>
            private readonly Dictionary<Paragraph, PdfStructureElement?> _noteElements = new(PdfReferenceEquality.Instance);

            /// <summary>Records that <paramref name="later"/> continues the note <paramref name="part"/> is a part of.</summary>
            private void ContinuesNote(Paragraph later, Paragraph part)
                => _noteFirstPart[later] = _noteFirstPart.TryGetValue(part, out Paragraph? first) ? first : part;

            /// <summary>
            /// The <c>/L</c> a list of the block flow is drawn in: the one the list drawn before it at
            /// its <see cref="ListBlock.NestingDepth"/> opened when it continues that list
            /// (<see cref="ListBlock.ContinuesPrevious"/>), else a new one — at a depth past 0 in the
            /// <c>/LI</c> last drawn one level up, the item it was taken out of — which a continuation
            /// after it then reuses. A nested list always opens its own, in its item.
            /// </summary>
            public PdfStructureElement? ListElement(ListBlock list, int depth)
            {
                if (depth > 0) return NewElement(PdfStructureTypes.L);
                // A depth past every list open is the deepest that can be: the next one down.
                int at = Math.Min(Math.Max(0, list.NestingDepth), _flowLists.Count);
                if (list.ContinuesPrevious && at < _flowLists.Count)
                {
                    _flowLists.RemoveRange(at + 1, _flowLists.Count - at - 1);
                    return _flowLists[at].List;
                }
                _flowLists.RemoveRange(at, _flowLists.Count - at);
                PdfStructureElement? made;
                using (EnterParent(at > 0 ? _flowLists[at - 1].LastItem : Parent)) made = NewElement(PdfStructureTypes.L);
                _flowLists.Add((made, null));
                return made;
            }

            /// <summary>Records <paramref name="item"/> as the last <c>/LI</c> drawn in the block flow's innermost list, the one a sublist taken out of it is tagged in.</summary>
            public void FlowListItemDrawn(PdfStructureElement? item)
            {
                if (_flowLists.Count > 0) _flowLists[_flowLists.Count - 1] = (_flowLists[_flowLists.Count - 1].List, item);
            }

            /// <summary>The block flow's open lists, outermost first: each <c>/L</c> and the last <c>/LI</c> drawn in it.</summary>
            private readonly List<(PdfStructureElement? List, PdfStructureElement? LastItem)> _flowLists = new();

            /// <summary>
            /// The <c>/Note</c> element a note part is drawn in: for the parts of a note split across
            /// pages, one element whatever page each part lands on — the note's first part makes it,
            /// the rest add their content to it; any other part a new one.
            /// </summary>
            public PdfStructureElement? NoteElement(Paragraph part)
            {
                Paragraph first = _noteFirstPart.TryGetValue(part, out Paragraph? f) ? f : part;
                if (_noteElements.TryGetValue(first, out PdfStructureElement? made)) return made;
                PdfStructureElement? element = NewElement(PdfStructureTypes.Note);
                _noteElements[first] = element;
                return element;
            }

            /// <summary>Marks assigned before layout; see <c>FootnoteNumbering</c>.</summary>
            public FootnoteNumbering Footnotes = new FootnoteNumbering();

            /// <summary>
            /// Each table's column content widths, by the table, the measure (min-content or not) and
            /// its column count — measured once per context: a nested table's columns are read by
            /// every table around it, and by each of their draws and row measures, so without this a
            /// chain of nested tables costs the cube of its depth. See <c>TableRenderer.ColumnContentWidths</c>.
            /// </summary>
            public readonly Dictionary<(Table Table, bool MinContent, int Cols), (double[] Widths, bool[] Unmeasured)> ColumnContent = new();

            /// <summary>
            /// Each row's height at the column widths it was measured at — measured once per context: a
            /// baseline cell's nested table is read both for its first baseline and for its height, so
            /// without this a chain of them costs twice as much for every level of baseline cells.
            /// See <c>TableRenderer.RowHeight</c>.
            /// </summary>
            public readonly Dictionary<(TableRow Row, double[] Widths), double> RowHeights = new(TableRenderer.RowAtWidths.Instance);

            /// <summary>
            /// Where each heading landed, filled by the first pass and read by the second.
            /// </summary>
            /// <remarks>
            /// The same dictionary object on both passes: the first writes into it, the second
            /// reads. Null means "no contents block in this document", in which case nothing writes
            /// and nothing reads.
            /// </remarks>
            public Dictionary<Paragraph, string>? TocPages;

            /// <summary>The one-based number of the page being drawn.</summary>
            /// <remarks>
            /// <c>AllPages</c> gains its entry when a page starts, so its count IS the current
            /// page's number. What a reader sees is <see cref="PageNumberText"/>, which applies the
            /// section's format and start to it.
            /// </remarks>
            public int CurrentPageNumber => AllPages.Count;

            /// <summary>
            /// The number the current section's first page displays: its own
            /// <see cref="Model.Section.PageNumberStart"/>, else one past the previous section's last.
            /// </summary>
            private int _sectionPageBase = 1;

            /// <summary>The displayed number of the last page started, so the next section can continue it.</summary>
            private int _lastDisplayedPage;

            /// <summary>
            /// The number the page being drawn displays — Word's <c>w:pgNumType</c> rule: a section
            /// with a start restarts there, one without continues the previous section's count.
            /// </summary>
            /// <remarks>
            /// For a document that sets no start anywhere this is <see cref="GlobalPage"/> exactly,
            /// so every existing page number is unchanged.
            /// </remarks>
            public int DisplayedPageNumber => _sectionPageBase + PageInSection - 1;

            /// <summary>
            /// The page number a reader sees on the page being drawn, in the section's
            /// <see cref="Model.Section.PageNumberFormat"/>.
            /// </summary>
            /// <remarks>
            /// <b>One formatter for the <c>{{PAGE}}</c> field and the contents entry.</b> Both
            /// describe the same page, and a footer saying "3" under a contents saying "iii" is
            /// the disagreement a single implementation exists to prevent.
            /// </remarks>
            public string PageNumberText()
                => FormatPageNumber(DisplayedPageNumber, Section.PageNumberFormat);
            public double TopStart => Section.PageHeightPt - (Area?.TopPt ?? EffMarginTop);

            /// <summary>
            /// A font that can draw <paramref name="codePoints"/> for a run of this family and
            /// style, or <c>(null, null)</c> when none is available.
            ///
            /// <para>Resolution is per RUN, not per document. Demanding one font cover every code
            /// point above U+00FF in the whole document, a single CJK glyph anywhere would make
            /// resolution fail for the Hebrew as well and the whole document fall back to
            /// <c>'?'</c>. Already-resolved faces are reused whenever
            /// they happen to cover the new run, so distinct scripts get distinct fonts without
            /// re-scanning the host for every run.</para>
            /// </summary>
            public (PdfDocumentBuilder.EmbeddedFont? handle, TrueTypeFontProgram? prog) GetUniFont(
                string? family, bool bold, bool italic, IReadOnlyCollection<int> codePoints,
                bool namedFamily = false)
            {
                if (codePoints.Count == 0) return (null, null);
                // AnyCompositeText is a document-level shortcut: with nothing above U+00FF anywhere,
                // no run can need a composite font and the host scan is pure cost. A run that NAMES
                // a family is the exception — it may want its own face for plain Latin text — so it
                // says so rather than being excluded by a flag about other runs.
                if (!AnyCompositeText && !namedFamily) return (null, null);

                var key = (family ?? "", bold, italic);
                if (!UniFonts.TryGetValue(key, out var candidates))
                {
                    candidates = new List<(PdfDocumentBuilder.EmbeddedFont, TrueTypeFontProgram)>();
                    UniFonts[key] = candidates;
                }

                foreach (var (handle, prog) in candidates)
                {
                    bool covers = true;
                    foreach (int cp in codePoints) if (!prog.CoversShaped(cp)) { covers = false; break; }
                    if (covers) return (handle, prog);
                }

                // providerOnly for a named Latin family: see BuilderFontResolver.Resolve. A run
                // that only NAMES a family must not be resolved from the host, or the same document
                // would embed a face on one machine and fall back on another.
                var resolved = Fonts.Resolve(family, bold, italic, codePoints,
                                             providerOnly: namedFamily && !NeedsCompositeFont(codePoints));
                if (resolved == null) return (null, null);

                // One embedded-font handle per physical program: a face several families fall back to
                // is embedded once, not once per family (the same instance resolves for each).
                if (!EmbeddedByProgram.TryGetValue(resolved, out var newHandle))
                {
                    newHandle = Doc.AddEmbeddedFont(resolved);
                    EmbeddedByProgram[resolved] = newHandle;
                }
                candidates.Add((newHandle, resolved));
                return (newHandle, resolved);
            }

            /// <summary>
            /// Whether this set contains anything the standard-14 fonts cannot draw. Text above
            /// U+00FF has no fallback worth having, so it may be resolved from the host; pure Latin
            /// has one, so it may not.
            /// </summary>
            private static bool NeedsCompositeFont(IReadOnlyCollection<int> codePoints)
            {
                foreach (int cp in codePoints) if (cp > 0xFF) return true;
                return false;
            }
            public string StandardFont(bool bold, bool italic, StdFamily fam = StdFamily.Sans)
                => fam switch
                {
                    StdFamily.Mono => bold ? (italic ? FMonoBoldItalic : FMonoBold) : (italic ? FMonoItalic : FMono),
                    StdFamily.Serif => bold ? (italic ? FSerifBoldItalic : FSerifBold) : (italic ? FSerifItalic : FSerif),
                    _ => bold ? (italic ? FBoldItalic : FBold) : (italic ? FItalic : FRegular),
                };

            public void BeginSection(Section section)
            {
                Section = section;

                // Margins that leave no content box make the flow lay out one line a page (and, in a
                // one-point column, one character a line), so a tiny document explodes into hundreds
                // of thousands of pages. They give way: on each axis a page that would keep less than
                // PageContentBox.MinContentPt drops both its margins to zero, and the content box
                // becomes the whole page. One rule, shared with DocxToPdf's SectionPageSize, so the
                // Builder API and the HTML @page path cannot reach the exporter with the explosion
                // still live (F11 sibling).
                (_effMarginTop, _effMarginBottom) = Model.PageContentBox.Fit(
                    section.PageHeightPt, section.MarginTop, section.MarginBottom, out bool gaveV);
                (_effMarginLeft, _effMarginRight) = Model.PageContentBox.Fit(
                    section.PageWidthPt, section.MarginLeft, section.MarginRight, out bool gaveH);
                if (gaveV)
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                        $"the section's top and bottom margins leave under {Model.PageContentBox.MinContentPt} pt of a " +
                        $"{section.PageHeightPt:0.##} pt page; they give way so the content fills the page height.");
                if (gaveH)
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                        $"the section's left and right margins leave under {Model.PageContentBox.MinContentPt} pt of a " +
                        $"{section.PageWidthPt:0.##} pt page; they give way so the content fills the page width.");

                _areas = section.ContentAreas.Where(a => a != null).ToList();
                PageInSection = 0;
                // Before StartPage, which numbers the first page: a start restarts the count, no
                // start continues it from wherever the previous section left off.
                _sectionPageBase = section.PageNumberStart ?? (_lastDisplayedPage + 1);
                StartPage();
            }

            public void StartPage()
            {
                // Scale the PAPER, never the Section: every derived measurement below
                // (ContentRight, TopStart, DrawPageBorders) keeps reading Section.* in logical
                // points, so pagination and wrapping are unchanged and only the sheet the result
                // is printed on differs.
                if (MaxPages > 0 && GlobalPage >= MaxPages) throw new PageLimitReached();
                Page = Doc.AddPage(Section.PageWidthPt * Scale, Section.PageHeightPt * Scale);

                // A pure scale with no translation: a point at logical y maps to Scale·y, so
                // logical 0 → 0 and the logical top edge → the scaled top edge. Written here
                // because StartPage is the first thing to touch the page, so it lands at the very
                // front of the content — ahead of DrawHeader below, which would otherwise draw
                // unscaled. No `q` wraps it: an unbalanced `q` is a conformance defect, and there
                // is nothing after it on the page that must see the unscaled matrix.
                if (Scale != 1.0)
                    Page.AppendRaw(F(Scale) + " 0 0 " + F(Scale) + " 0 0 " + PdfOperators.ConcatMatrix + "\n");

                // A float's exclusion belongs to the page it was drawn on; text on the next page
                // flows at the full measure. Clearing here rather than at FinishPage keeps the
                // invariant on the side that creates the page.
                _pageFloats.Clear();

                AnyPage = true;
                PageInSection++;
                GlobalPage++;
                _lastDisplayedPage = DisplayedPageNumber;
                AllPages.Add(Page);
                BindStandardFonts();
                _area = 0;
                FlowRegion++;
                Y = TopStart;
                DrawHeader();
                TakeCarriedNotes();
            }

            /// <summary>
            /// Reserves the foot of a new page for the note parts the page before carried, in order, up
            /// to half the text block — a continuation longer than that is cut again and continues on the
            /// following page. The first part keeps at least one line here, so a note of any length ends.
            /// </summary>
            private void TakeCarriedNotes()
            {
                if (CarriedNotes.Count == 0) return;
                var carried = new List<Paragraph>(CarriedNotes);
                CarriedNotes.Clear();

                double room = (TopStart - ContentBottom) / 2 - FootnoteRuleGapPt, used = 0;
                bool full = false;
                foreach (Paragraph part in carried)
                {
                    if (full) { CarriedNotes.Add(part); continue; }
                    double height = MeasureParagraph(this, part, ContentWidth);
                    if (used + height <= room + 1e-6) { _continuedNotes.Add(part); used += height; continue; }

                    var (head, tail) = SplitNoteToFit(this, part, room - used, atLeastOneLine: _continuedNotes.Count == 0);
                    if (head != null) { ContinuesNote(head, part); _continuedNotes.Add(head); used += MeasureParagraph(this, head, ContentWidth); }
                    if (tail != null) ContinuesNote(tail, part);
                    if (head == null || tail != null) CarriedNotes.Add(tail ?? part);
                    full = true;
                }
                if (_continuedNotes.Count > 0) NoteAreaHeight += used + FootnoteRuleGapPt;
            }

            /// <summary>
            /// Registers the twelve standard faces on <see cref="Page"/> and binds their resource
            /// names — on every new page, and on a transparency group's content, which has resources
            /// of its own.
            /// </summary>
            private void BindStandardFonts()
            {
                FRegular = Page.AddStandardFont(StandardFonts.Helvetica);
                FBold = Page.AddStandardFont(StandardFonts.HelveticaBold);
                FItalic = Page.AddStandardFont(StandardFonts.HelveticaOblique);
                FBoldItalic = Page.AddStandardFont(StandardFonts.HelveticaBoldOblique);
                FMono = Page.AddStandardFont(StandardFonts.Courier);
                FMonoBold = Page.AddStandardFont(StandardFonts.CourierBold);
                FMonoItalic = Page.AddStandardFont(StandardFonts.CourierOblique);
                FMonoBoldItalic = Page.AddStandardFont(StandardFonts.CourierBoldOblique);
                FSerif = Page.AddStandardFont(StandardFonts.TimesRoman);
                FSerifBold = Page.AddStandardFont(StandardFonts.TimesBold);
                FSerifItalic = Page.AddStandardFont(StandardFonts.TimesItalic);
                FSerifBoldItalic = Page.AddStandardFont(StandardFonts.TimesBoldItalic);
            }

            public void FinishPage()
            {
                DrawFootnotes();
                DrawFooter();
                DrawPageBorders();
            }

            /// <summary>
            /// Draws this page's notes in the strip <see cref="NoteAreaHeight"/> reserved for them,
            /// then clears it so the next page starts with a full text block.
            /// </summary>
            /// <remarks>
            /// <para><b>Drawn at page end, not where the mark was.</b> By the time the page closes,
            /// every note that belongs to it has been registered — and the strip's top is known
            /// only then, because each note that joined the page pushed it up.</para>
            ///
            /// <para><b>The separator is an artifact and the notes are not.</b> The rule is page
            /// furniture with no document meaning; the notes are content a reader must be able to
            /// reach, so they carry their own structure. Marking the whole strip as an artifact
            /// would make the notes unreachable from the tag tree, which is the failure this
            /// exporter's tagging is documented never to cause.</para>
            /// </remarks>
            private void DrawFootnotes()
            {
                if (PendingNotes.Count == 0 && _continuedNotes.Count == 0) return;

                double top = ContentBottom + NoteAreaHeight;

                // A strip that opens with a note continued from the page before has a full-width rule
                // (Word's continuation separator), so the reader can tell it from a strip of new notes.
                double rule = _continuedNotes.Count > 0 ? ContentWidth : ContentWidth * FootnoteRuleWidthFraction;
                using (EnterArtifact(PdfArtifactTypes.Layout, null))
                {
                    Page.SetStrokeColorGray(0.4f);
                    Page.SetLineWidth(FootnoteRuleWidthPt);
                    Page.MoveTo(ContentLeft, top);
                    Page.LineTo(ContentLeft + rule, top);
                    Page.Stroke();
                }

                double y = top - FootnoteRuleGapPt;
                foreach (Paragraph part in _continuedNotes)
                    y = PdfExporter.RenderNotePartAt(this, part, ContentLeft, y, ContentWidth);
                foreach (Model.Footnote note in PendingNotes)
                    y = _noteHeads.TryGetValue(note, out Paragraph? head)
                        ? PdfExporter.RenderNotePartAt(this, head, ContentLeft, y, ContentWidth)
                        : PdfExporter.RenderNoteAt(this, note, ContentLeft, y, ContentWidth);

                PendingNotes.Clear();
                _continuedNotes.Clear();
                _noteHeads.Clear();
                NoteAreaHeight = 0;
            }

            /// <summary>
            /// Reserves room for <paramref name="notes"/> at the foot of the current page and
            /// records that they belong to it.
            /// </summary>
            /// <remarks>
            /// <b>Called after the line's own <c>EnsureSpace</c>, never before.</b> Reserving first
            /// would raise the limit, push the line to the next page, and leave the note stranded on
            /// the page its mark is no longer on. The caller asks for the line's height AND its
            /// notes' in one go, so the break happens once and everything lands together.
            /// </remarks>
            public void ReserveNotes(IEnumerable<Model.Footnote> notes, NoteSplit? split = null)
            {
                foreach (Model.Footnote note in notes)
                {
                    if (note == null || PendingNotes.Contains(note)) continue;

                    // The notes after a split one continue on the next page, whole, after its rest.
                    if (split != null && split.After.Contains(note))
                    {
                        CarriedNotes.Add(PdfExporter.NoteParagraph(this, note));
                        continue;
                    }

                    PendingNotes.Add(note);
                    // The strip's rule gap once, with its first item: a continued part reserved it already.
                    double gap = PendingNotes.Count == 1 && _continuedNotes.Count == 0 ? FootnoteRuleGapPt : 0;
                    if (split != null && ReferenceEquals(note, split.Note))
                    {
                        _noteHeads[note] = split.Head;
                        NoteAreaHeight += MeasureParagraph(this, split.Head, ContentWidth) + gap;
                        ContinuesNote(split.Tail, split.Head);
                        CarriedNotes.Add(split.Tail);
                        continue;
                    }
                    NoteAreaHeight += PdfExporter.NoteHeight(this, note, ContentWidth) + gap;
                }
            }

            /// <summary>
            /// Breaks to a new page in the flow. Boxes open around the flow are sliced: each draws its
            /// fragment on this page, and continues on the next. The blocks placed on every page draw
            /// on the page being left, over its flow, before its footer.
            /// </summary>
            /// <remarks>
            /// In a section of several content areas (<see cref="Model.Section.ContentAreas"/>) the flow
            /// goes on in the page's next area while it has one, unless <paramref name="wholePage"/> —
            /// a page break the model asks for, which ends the page. The open boxes move with the flow
            /// to the area it reaches, back to the left as well as to the right.
            /// </remarks>
            public void NewPageContinue(bool wholePage = false)
            {
                SliceOpenBoxes(this);
                if (!wholePage && _area + 1 < _areas.Count)
                {
                    NextContentArea();
                }
                else
                {
                    foreach (var (para, place) in FixedBlocks) RenderPositioned(this, para, place);
                    FinishPage();
                    StartPage();
                }
                FollowRegion();
                ResumeOpenBoxes(this);
            }

            /// <summary>
            /// The open boxes follow the flow into the region it is in now, outermost first: each was
            /// opened in its parent's content band (the region's, for the outermost; <see cref="OpenBox"/>),
            /// and is laid out again in the parent's band there — in a region of another width, a box of
            /// no width of its own takes the new width, and its blocks with it.
            /// </summary>
            private void FollowRegion()
            {
                double left = ContentLeft, width = ContentWidth;
                foreach (OpenBlockBox open in OpenBoxes)
                {
                    open.Rebase(left, width);
                    (left, width) = (open.ContentLeft, open.ContentWidth);
                }
            }

            /// <summary>
            /// The flow's region is full and the page has another: the region's footnotes are drawn at
            /// its foot, and the flow goes on at the next one's top — on the same page, so nothing placed
            /// on every page is drawn yet, and no header or footer.
            /// </summary>
            private void NextContentArea()
            {
                DrawFootnotes();
                _area++;
                FlowRegion++;
                Y = TopStart;
                TakeCarriedNotes();
            }

            /// <summary>
            /// The section's paragraphs placed on every page (<see cref="AbsolutePlacement.EveryPage"/>),
            /// in z order; the section's last page draws them in the positioned pass with the others.
            /// </summary>
            public List<(Paragraph Para, AbsolutePlacement Place)> FixedBlocks = new();

            /// <summary>The boxes open around the flow, outermost first (<see cref="BoxStart"/>).</summary>
            public readonly List<OpenBlockBox> OpenBoxes = new();

            /// <summary>
            /// Page-placed paragraphs met inside a table cell's blocks, drawn after the
            /// section's flow with the section's own positioned paragraphs, in one z order; a cell has
            /// no held-back pass of its own, and laid them out in its flow.
            /// </summary>
            public readonly List<(Paragraph Para, AbsolutePlacement Place)> DeferredPositioned = new();

            /// <summary>
            /// The paragraphs already in <see cref="DeferredPositioned"/>, by reference identity, so
            /// the one-per-paragraph rule is an O(1) check rather than a linear scan of the list on
            /// every page-placed paragraph a repeating cell meets. Cleared with the
            /// list.
            /// </summary>
            public readonly HashSet<Paragraph> DeferredPositionedSeen =
                new HashSet<Paragraph>(Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance);

            /// <summary>The band the flow lays its blocks out in: the innermost open box's, or the page's.</summary>
            public double FlowLeft => OpenBoxes.Count == 0 ? ContentLeft : OpenBoxes[OpenBoxes.Count - 1].ContentLeft;
            public double FlowWidth => OpenBoxes.Count == 0 ? ContentWidth : OpenBoxes[OpenBoxes.Count - 1].ContentWidth;

            public void EnsureSpace(double height)
            {
                // A hundredth of a point of slack below the limit: a block that ends exactly on it —
                // a `min-height: 100vh` box — left the cursor a rounding error under, and the next
                // EnsureSpace(0) (the box closing, which needs no room) opened a blank page.
                if (Y - height < BottomLimit - 0.01 && Y < TopStart - 0.01)
                    NewPageContinue();
            }

            private void DrawHeader()
            {
                var h = SelectHeader();
                if (h == null) return;
                using var _ = EnterArtifact(PdfArtifactTypes.Pagination, PdfArtifactSubtypes.Header);
                double hy = Section.PageHeightPt - HeaderFooterOffsetPt;
                foreach (var block in h.Content()) hy = DrawBandBlock(block, hy);
            }

            /// <summary>
            /// One block of a header or footer, drawn without paginating: a paragraph as the band always drew it
            /// (a float in a band registers nothing on the page's flow), a table or a list as a cell draws it.
            /// </summary>
            private double DrawBandBlock(IDocElement block, double y)
                => block is Paragraph p
                    ? PdfExporter.RenderParagraphAt(this, p, EffMarginLeft, y, MarginWidth, paginate: false)
                    : TableRenderer.DrawCellBlock(this, block, EffMarginLeft, y, MarginWidth);

            private void DrawFooter()
            {
                var f = SelectFooter();
                if (f == null) return;
                using var _ = EnterArtifact(PdfArtifactTypes.Pagination, PdfArtifactSubtypes.Footer);
                // After the footnotes are drawn, so no strip is reserved: the bottom margin.
                double fy = Math.Max(EffMarginBottom + NoteAreaHeight, HeaderFooterOffsetPt);
                foreach (var block in f.Content()) fy = DrawBandBlock(block, fy);
            }

            /// <summary>The width between the margins, which the header and footer take whatever region the flow is in.</summary>
            private double MarginWidth => Math.Max(1, PageContentRight - EffMarginLeft);

            private Header? SelectHeader()
            {
                if (Section.TitlePage && PageInSection == 1 && Section.FirstPageHeader != null)
                    return Section.FirstPageHeader;
                if (Section.EvenAndOddHeaders && PageInSection % 2 == 0 && Section.EvenPageHeader != null)
                    return Section.EvenPageHeader;
                return Section.Header;
            }

            private Footer? SelectFooter()
            {
                if (Section.TitlePage && PageInSection == 1 && Section.FirstPageFooter != null)
                    return Section.FirstPageFooter;
                if (Section.EvenAndOddHeaders && PageInSection % 2 == 0 && Section.EvenPageFooter != null)
                    return Section.EvenPageFooter;
                return Section.Footer;
            }

            private void DrawPageBorders()
            {
                // A page border is typographic decoration, never document content.
                using var _ = MarkArtifact(PdfArtifactTypes.Layout);
                var pb = Section.PageBorders;
                if (pb == null) return;
                double W = Section.PageWidthPt, H = Section.PageHeightPt;
                double leftX = pb.OffsetFromPage ? pb.SpaceLeft : EffMarginLeft - pb.SpaceLeft;
                double rightX = pb.OffsetFromPage ? W - pb.SpaceRight : W - EffMarginRight + pb.SpaceRight;
                double topY = pb.OffsetFromPage ? H - pb.SpaceTop : H - EffMarginTop + pb.SpaceTop;
                double botY = pb.OffsetFromPage ? pb.SpaceBottom : EffMarginBottom - pb.SpaceBottom;
                StrokeEdge(Page, pb.Top, leftX, topY, rightX, topY);
                StrokeEdge(Page, pb.Bottom, leftX, botY, rightX, botY);
                StrokeEdge(Page, pb.Left, leftX, botY, leftX, topY);
                StrokeEdge(Page, pb.Right, rightX, botY, rightX, topY);
            }
        }

        /// <summary>Closes a marked-content sequence. Default-constructed = nothing was opened.</summary>
        internal readonly struct MarkedContentScope : IDisposable
        {
            private readonly PdfContentStreamBuilder _page;
            public MarkedContentScope(PdfContentStreamBuilder page) => _page = page;
            public void Dispose() => _page?.EndMarkedContent();
        }

        /// <summary>
        /// Sets and restores the artifact scope.
        ///
        /// <para>A struct with <c>using</c> rather than a pair of assignments because
        /// <c>NewPageContinue</c> draws a footer and a header <b>from inside</b> a paragraph's
        /// line loop. A bare flag left set by an early return would silently attribute the rest of
        /// that paragraph to the header.</para>
        /// </summary>
        internal readonly struct ArtifactScope : IDisposable
        {
            private readonly Ctx _ctx;
            private readonly string? _prevType, _prevSubtype;

            public ArtifactScope(Ctx ctx, string type, string? subtype)
            {
                _ctx = ctx;
                _prevType = ctx.ArtifactType;
                _prevSubtype = ctx.ArtifactSubtype;
                ctx.ArtifactType = type;
                ctx.ArtifactSubtype = subtype;
            }

            public void Dispose()
            {
                if (_ctx == null) return;
                _ctx.ArtifactType = _prevType;
                _ctx.ArtifactSubtype = _prevSubtype;
            }
        }

        /// <summary>Sets and restores <see cref="Ctx.Parent"/>.</summary>
        internal readonly struct ParentScope : IDisposable
        {
            private readonly Ctx _ctx;
            private readonly PdfStructureElement? _prev;

            public ParentScope(Ctx ctx, PdfStructureElement? parent)
            {
                _ctx = ctx;
                _prev = ctx.Parent;
                if (parent != null) ctx.Parent = parent;
            }

            public void Dispose() { if (_ctx != null) _ctx.Parent = _prev; }
        }

        // ================================================================== blocks

        /// <summary>
        /// Draws an out-of-flow paragraph at its placement, leaving the flow cursor alone.
        /// </summary>
        /// <remarks>
        /// <para><c>paginate: false</c> is the whole mechanism: it is the exporter's existing way
        /// of saying "draw here and do not move or break", already used by headers, footers and
        /// table cells. A positioned block therefore needs no new drawing path — only a different
        /// origin and a different moment.</para>
        /// <para><b>The flow cursor is saved and restored</b> even though the draw should not move
        /// it. RenderParagraphAt reads and writes ctx.Y for its own box reservation, and a
        /// positioned block drawn between two in-flow ones would otherwise leave the cursor
        /// wherever it finished — which is invisible here, because these are drawn after the flow,
        /// and would become a silent defect the moment that order changed.</para>
        /// </remarks>
        /// <param name="ctx">Render state; its <c>Y</c> cursor is saved and restored around the draw.</param>
        /// <param name="para">The out-of-flow paragraph to draw.</param>
        /// <param name="place">
        /// The paragraph's placement, passed in rather than re-read from <paramref name="para"/>:
        /// the caller's null test is what selected this paragraph, so taking it as a parameter is
        /// what makes "a positioned paragraph has a placement" a fact of the signature.
        /// </param>
        private static void RenderPositioned(Ctx ctx, Paragraph para, AbsolutePlacement place)
        {
            double savedY = ctx.Y;
            double pageWidth = ctx.Section.PageWidthPt;

            // CSS 2.1 §10.3.7 against the page's edges, as DrawPositionedInBox against a box's: a
            // stated width; else `left` and `right` together; else, anchored at the right alone,
            // the content's width (shrink-to-fit); else to the right margin.
            double width;
            if (place.WidthPt > 0) width = place.WidthPt;
            else if (!place.AnchorsLeft && place.RightPt is double rightAlone) width = ShrinkToFitGroupWidth(ctx, para, place, Math.Max(1, pageWidth - rightAlone));
            else if (place.RightPt is double right) width = pageWidth - place.LeftPt - right;
            else width = ctx.PageContentRight - place.LeftPt;
            width = Math.Max(1, width);
            double x = place.AnchorsLeft || place.RightPt == null ? place.LeftPt : pageWidth - place.RightPt.Value - width;

            // CSS states `top` from the page's top edge; PDF user space counts up from the bottom. A
            // block anchored at its bottom alone ends `bottom` above the page's bottom edge (§10.6.4).
            double y = place.AnchorsTop || place.BottomPt == null
                ? ctx.Section.PageHeightPt - place.TopPt
                : place.BottomPt.Value + MeasurePlacedGroup(ctx, para, place, width);

            RenderPlacedGroup(ctx, para, place, x, y, width);
            ctx.Y = savedY;
        }

        /// <summary>
        /// Draws a placed paragraph at (<paramref name="x"/>, <paramref name="y"/>) and then each of
        /// its placement's <see cref="AbsolutePlacement.Following"/> paragraphs and
        /// <see cref="AbsolutePlacement.FollowingBlocks"/> where the one above ended, at the same left
        /// edge and width — the page-relative and the box-relative draw alike. The following blocks
        /// are laid out as a table cell's are (<see cref="LayOutCellBlocks"/>: lists, boxes and
        /// floats among them), the page's floats set aside, since none of them is in the flow.
        /// </summary>
        /// <remarks>
        /// The page's floats are set aside for the whole group: they are the flow's, and a positioned
        /// block is out of it — a float the flow left on the page pushed the placed paragraph beside it.
        /// </remarks>
        internal static void RenderPlacedGroup(Ctx ctx, Paragraph para, AbsolutePlacement place, double x, double y, double width)
        {
            var pageFloats = ctx.DetachFloats();
            try
            {
                // A band (AbsolutePlacement.BandHeightPt): the group moved down by the band's slack, and
                // its paragraphs' transforms taken about the band, where the producer measured them from.
                (double Top, double Height)? band = null;
                if (place.BandHeightPt is double bandHeight)
                {
                    band = (y, bandHeight);
                    if (place.BandAlign is CellVerticalAlign.Center or CellVerticalAlign.Bottom)   // Baseline is Top here
                    {
                        double slack = bandHeight - MeasurePlacedGroup(ctx, para, place, width);
                        if (slack > 0) y -= place.BandAlign == CellVerticalAlign.Center ? slack / 2 : slack;
                    }
                }
                double next = RenderParagraphAt(ctx, para, x, y, width, paginate: false, band);
                foreach (Paragraph following in place.Following)
                    if (following != null) next = RenderParagraphAt(ctx, following, x, next, width, paginate: false, band);
                List<IDocElement> blocks = PlaceableFollowingBlocks(place, report: true);
                if (blocks.Count == 0) return;
                LayOutCellBlocks(ctx, blocks, x, next, width, measureOnly: false,
                    (b, bx, by, bw) => TableRenderer.DrawCellBlock(ctx, b, bx, by, bw),
                    (b, bx, by, bw) => TableRenderer.MeasureCellBlock(ctx, b, bx, by, bw));
            }
            finally { ctx.RestoreFloats(pageFloats); }
        }

        /// <summary>
        /// A placement's <see cref="AbsolutePlacement.FollowingBlocks"/> that are drawn
        /// (<see cref="AbsolutePlacement.CanFollow"/>), the others reported when <paramref name="report"/> —
        /// on the draw alone, so a block measured and drawn is reported once.
        /// </summary>
        private static List<IDocElement> PlaceableFollowingBlocks(AbsolutePlacement place, bool report)
            // ModelRules.PlaceableFollowingBlocks collects the list's boxes once and asks each block
            // against that set, so the three callers here (RenderPlacedGroup, MeasurePlacedGroup,
            // ShrinkToFitGroupWidth) do not each run a per-block scan of the whole list, which would
            // be quadratic.
            => ModelRules.PlaceableFollowingBlocks(place, report);

        /// <summary>
        /// The shrink-to-fit width of a placed paragraph and the blocks after it — the widest of
        /// theirs, within <paramref name="availableWidth"/> (CSS 2.1 §10.3.7); a table its stated
        /// width, else all of it, as a table fills its band; a list or a box all of it.
        /// </summary>
        internal static double ShrinkToFitGroupWidth(Ctx ctx, Paragraph para, AbsolutePlacement place, double availableWidth)
        {
            double width = ShrinkToFitWidth(ctx, para, availableWidth, minContent: false);
            foreach (Paragraph following in place.Following)
                if (following != null) width = Math.Max(width, ShrinkToFitWidth(ctx, following, availableWidth, minContent: false));
            foreach (IDocElement block in PlaceableFollowingBlocks(place, report: false))
            {
                if (block is Paragraph p) width = Math.Max(width, ShrinkToFitWidth(ctx, p, availableWidth, minContent: false));
                else if (block is Table t) width = Math.Max(width, t.WidthPt is double w && w > 0 ? Math.Min(w, availableWidth) : availableWidth);
                else if (block is not BoxEnd) width = availableWidth;
            }
            return width;
        }

        /// <summary>The height a placed paragraph and the blocks after it take at <paramref name="width"/>, the following blocks by the cell's own measure.</summary>
        internal static double MeasurePlacedGroup(Ctx ctx, Paragraph para, AbsolutePlacement place, double width)
        {
            double height = MeasureParagraph(ctx, para, width);
            foreach (Paragraph following in place.Following)
                if (following != null) height += MeasureParagraph(ctx, following, width);
            List<IDocElement> blocks = PlaceableFollowingBlocks(place, report: false);
            if (blocks.Count > 0) height += TableRenderer.MeasureBlocks(ctx, blocks, width);
            return height;
        }

        /// <summary>
        /// Starts a new page before <paramref name="children"/>[<paramref name="k"/>] when it begins
        /// a group that must stay on one page — a block kept together, or a chain linked by
        /// <c>KeepWithNext</c> — and the group does not fit in what is left of this page but would
        /// fit on a fresh one.
        /// </summary>
        /// <remarks>
        /// <para><b>Decided once, at the start of the group.</b> A block inside a chain is not
        /// re-examined: a group taller than a page flows as though it were not kept at all, which
        /// is what CSS's <c>avoid</c> asks for — a preference, not a demand — instead of breaking
        /// again part-way down at whatever point the rest of it happens to fit.</para>
        /// <para><b>The block a chain leads into counts only by its start</b> — its first line, or
        /// a table's header and first row — unless it is kept together itself. A heading kept with
        /// the paragraph after it needs that paragraph to begin on the same page, not to end on
        /// it.</para>
        /// <para>A group containing anything this cannot measure — a list, a table of contents, a
        /// positioned paragraph — is not kept: an unmeasured height would decide the break on a
        /// guess. A float measures nothing, which is exact: the block after it starts beside it.</para>
        /// <para><b>Its measuring is budgeted</b> (<see cref="Ctx.SpendKeepMeasure"/>): a box kept together is measured whole
        /// where it opens, and again inside every kept box around it, so boxes nested d deep measured d²/2 blocks — 0.5 s at
        /// the HTML mapper's 200 levels, and a page repeating that tree repeated the cost. A decision past the budget keeps
        /// nothing and says so.</para>
        /// </remarks>
        private static void KeepTogetherIfItFits(Ctx ctx, IReadOnlyList<IDocElement> children, int k, double width)
        {
            if (k > 0 && KeepsWithNext(children, k - 1)) return;   // inside a chain: decided at its start
            if (!KeepsTogether(children[k]) && !KeepsWithNext(children, k)) return;

            // No "already at the top of a page" test: there the space left IS a page, so the fit
            // test below already declines — a group that fits a page fits, and one that does not
            // is not moved.
            double need = 0;
            for (int m = k; m < children.Count; m++)
            {
                // A chain crosses a box's edges: into a box, its
                // top margin, border and padding before its first block; out of one, its bottom ones — one with a
                // least height leaves room the chain cannot measure, and is not kept. (A box measured whole where
                // it opened is passed to its end there, and never reaches this.)
                if (m > k && children[m] is BoxEnd)
                {
                    int opened = BoxEnd.IndexOfStart(children, m);
                    if (opened < 0 || children[opened] is not BoxStart left || left.MinHeightPt > 0) return;
                    need += left.PaddingBottom + (left.BorderBottom?.Width ?? 0) + left.MarginBottom;
                    continue;
                }
                if (m > k && children[m] is BoxStart entered && !entered.KeepWithNext && !entered.KeepTogether)
                {
                    if (entered.Float != FloatSide.None) return;
                    need += entered.MarginTop + (entered.BorderTop?.Width ?? 0) + entered.PaddingTop;
                    continue;
                }

                bool last = !KeepsWithNext(children, m) || m == children.Count - 1;
                double? h;
                if (children[m] is BoxStart kept)
                {
                    // A box kept together, or with the next block: all of it, to its end — its blocks, edges and
                    // least height (a box kept together was judged by its least height alone, when it opened).
                    int end = BoxStart.IndexOfEnd(children, m);
                    if (kept.Float != FloatSide.None || end >= children.Count) return;
                    if (!ctx.SpendKeepMeasure(end - m + 1)) return;
                    h = TableRenderer.MeasureBlocks(ctx, Slice(children, m, end), width);
                    m = end;
                }
                else
                {
                    if (!ctx.SpendKeepMeasure(1)) return;
                    h = MeasureForKeep(ctx, children[m], width, leadOnly: m > k && last);
                }
                if (h == null) return;
                need += h.Value;
                if (last) break;
            }

            if (need > ctx.Y - ctx.BottomLimit && need <= ctx.TopStart - ctx.BottomLimit)
                ctx.NewPageContinue();
        }

        /// <summary>
        /// The blocks keep decisions may measure per block of the document's flow. The price is measured:
        /// about 12.5 µs a block measured for a keep, against about 38 µs a block for the layout
        /// itself, so the budget holds a hostile document to some three times its ordinary layout, while every
        /// block may sit inside eight boxes kept together before any keep is given up.
        /// </summary>
        internal const int KeepMeasurePerBlock = 8;

        /// <summary>The least budget, so a short document's keeps are never cut.</summary>
        internal const int MinKeepMeasureBlocks = 20_000;

        /// <summary>The blocks keep decisions measured in the last export on this thread — the budget's evidence.</summary>
        [ThreadStatic] internal static long LastKeepBlocksMeasured;

        /// <summary>A document's keep budget: <see cref="KeepMeasurePerBlock"/> for each block of its sections' flow, at least <see cref="MinKeepMeasureBlocks"/>.</summary>
        internal static long KeepMeasureBudgetFor(Document document)
        {
            long blocks = 0;
            foreach (var section in document.Sections) blocks += section.Children.Count;
            return Math.Max(MinKeepMeasureBlocks, KeepMeasurePerBlock * blocks);
        }

        private static bool KeepsTogether(IDocElement el) => el switch
        {
            Paragraph p => p.KeepTogether,
            Table t => t.KeepTogether,
            BoxStart b => b.KeepTogether,
            _ => false,
        };

        /// <summary>
        /// Whether <paramref name="children"/>[<paramref name="i"/>] keeps with the block after it: a paragraph's, a
        /// table's or a box's own setting — a box's on its start and on its end alike, so the block after the box is
        /// in its chain.
        /// </summary>
        private static bool KeepsWithNext(IReadOnlyList<IDocElement> children, int i) => children[i] switch
        {
            Paragraph p => p.KeepWithNext,
            Table t => t.KeepWithNext,
            BoxStart b => b.KeepWithNext,
            BoxEnd => BoxEnd.IndexOfStart(children, i) is int opened && opened >= 0 && ((BoxStart)children[opened]).KeepWithNext,
            _ => false,
        };

        /// <summary><paramref name="children"/> from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
        private static List<IDocElement> Slice(IReadOnlyList<IDocElement> children, int from, int to)
        {
            var list = new List<IDocElement>(to - from + 1);
            for (int i = from; i <= to; i++) list.Add(children[i]);
            return list;
        }

        /// <summary>
        /// A block's height for a keep decision — all of it, or only its start when
        /// <paramref name="leadOnly"/> and it is not kept together itself — or <c>null</c> for a
        /// block this does not measure.
        /// </summary>
        private static double? MeasureForKeep(Ctx ctx, IDocElement el, double width, bool leadOnly)
        {
            switch (el)
            {
                case Paragraph p when p.Float == FloatSide.None && p.Placement == null:
                    return leadOnly && !p.KeepTogether ? MeasureParagraphLead(ctx, p, width) : MeasureParagraph(ctx, p, width);
                // A float does not move the flow — the block after it starts beside it — so it adds
                // nothing to the group; kept with the next block, it stays on that block's page.
                case Paragraph p when p.Float != FloatSide.None && p.FloatWidth > 0 && p.Placement == null:
                    return 0;
                case Table t:
                    return leadOnly && !t.KeepTogether ? TableRenderer.MeasureLead(ctx, t, width) : TableRenderer.Measure(ctx, t, width);
                default:
                    return null;
            }
        }

        private static void RenderBlock(Ctx ctx, IDocElement child, double x, double width)
        {
            switch (child)
            {
                case Paragraph para when para.Float != FloatSide.None && para.FloatWidth > 0:
                    // A float can clear too (CSS 2.1 §9.5.2): it starts below the floats before it
                    // rather than beside them — a second row of floats after a clearfix.
                    if (para.ClearFloats && ctx.HasFloats) ctx.Y = Math.Min(ctx.Y, ctx.LowestFloatBottom);
                    RenderFloatedParagraph(ctx, para, x, width);
                    break;
                case Paragraph para when para.ClearFloats && ctx.HasFloats:
                    // Drop past every float still in effect before laying the paragraph out, so
                    // `clear` means what it says even when the float is taller than the text that
                    // flowed beside it. Never moves the cursor UP: a float that already ended above
                    // the cursor must not pull the next block back over the text between them.
                    ctx.Y = Math.Min(ctx.Y, ctx.LowestFloatBottom);
                    ctx.Y = RenderParagraphAt(ctx, para, x, ctx.Y, width, paginate: true);
                    break;
                case Paragraph para:
                    ctx.Y = RenderParagraphAt(ctx, para, x, ctx.Y, width, paginate: true);
                    break;
                case Table table:
                    ctx.Y = TableRenderer.Render(ctx, table, x, ctx.Y, width);
                    break;
                case ListItem li:
                    ctx.Y = RenderListItem(ctx, li, x, ctx.Y, width);
                    break;
                case ListBlock list:
                    ctx.Y = RenderList(ctx, list, x, ctx.Y, width);
                    break;
                case TocBlock toc:
                    ctx.Y = RenderToc(ctx, toc, x, ctx.Y, width);
                    break;
            }
        }

        /// <summary>
        /// Draws a floated paragraph at one edge of the band, registers the space it occupies, and
        /// leaves the flow cursor where it was so the next block starts <em>beside</em> it.
        /// </summary>
        /// <remarks>
        /// <para><b>Not advancing <c>ctx.Y</c> is the whole difference.</b> Every other block
        /// returns a new cursor and the next one starts below it; a float returns the cursor
        /// unchanged and instead leaves an exclusion behind, so the following paragraphs wrap down
        /// its side and close over its bottom. That is what makes this re-flow rather than a
        /// two-column band, which keeps the text in its column to the end of the block.</para>
        ///
        /// <para><b>A float wider than its band is clamped, not refused.</b> A caller that asks for
        /// 400 pt inside a 200 pt table cell gets 200 and text below it rather than beside it,
        /// which is what a browser does with the same input; refusing would drop the content.</para>
        ///
        /// <para><b>It paginates on its own.</b> The float is rendered with <c>paginate: true</c>,
        /// so a float taller than the remaining space moves to the next page — and because
        /// <c>StartPage</c> clears the registry, its exclusion lands on the page it was actually
        /// drawn on rather than the one it was pushed off.</para>
        /// </remarks>
        private static void RenderFloatedParagraph(Ctx ctx, Paragraph para, double x, double width)
            => ctx.Y = PlaceFloat(ctx, para, x, ctx.Y, width, paginate: true);

        /// <summary>The height of the line a float is placed on, for asking which floats it would sit beside.</summary>
        private const double FloatProbeHeightPt = 1;

        /// <summary>
        /// Where a floated paragraph goes in a band <paramref name="width"/> wide from
        /// <paramref name="x"/>, asked to sit at <paramref name="y"/>: its <paramref name="left"/>,
        /// <paramref name="floatWidth"/> and <paramref name="top"/>, and whether it floats at all —
        /// false where it leaves no usable measure beside it, and it is a block. The one decision
        /// <see cref="PlaceFloat"/> and <see cref="MeasureFloat"/> share.
        /// </summary>
        /// <remarks>
        /// <para><b>Beside the floats already on its line, inward of them</b> (CSS 2.1 §9.5.1): a
        /// second right float sits to the left of the first, a second left float to the right of the
        /// first, each against the segment the earlier ones leave. Where that segment would leave
        /// less than <see cref="MinTextWidthBesideFloatPt"/> beside the new float, it drops to the
        /// nearest bottom among the floats it overlaps and asks again, as a browser shifts a float
        /// down until it fits — so text is never squeezed between two floats, and a second float on a
        /// side never covers the first.</para>
        /// <para>NOT clamped to the band, because the guard makes a clamp unreachable: any width that
        /// would need clamping is by definition wider than <paramref name="width"/>, so
        /// <c>width - floatWidth</c> is negative, so it is already below the minimum and takes the
        /// block path, so a <c>Math.Min(..., width)</c> here would be dead code.</para>
        /// <para>Registering the exclusion of a float as wide as its container would squeeze the
        /// following text into the one point <c>GetLineSegment</c> floors the width at, which is
        /// unreadable and not what any reader does: the content goes BELOW it. The clamping test asks
        /// for three times the band and expects text underneath, not a column one point wide.</para>
        /// </remarks>
        private static bool FloatBox(Ctx ctx, Paragraph para, double x, double y, double width,
            out double left, out double floatWidth, out double top)
            => FloatBox(ctx, para.Float, para.FloatWidth, x, y, width, out left, out floatWidth, out top);

        /// <summary>
        /// <see cref="FloatBox(Ctx, Paragraph, double, double, double, out double, out double, out double)"/>
        /// for any float: a paragraph's, or a floated box's margin box.
        /// </summary>
        private static bool FloatBox(Ctx ctx, FloatSide side, double wantedWidth, double x, double y, double width,
            out double left, out double floatWidth, out double top)
        {
            floatWidth = Math.Max(1, wantedWidth);
            top = y;
            double segmentLeft = x, segmentWidth = width;
            // Stepping down past each float bottom is what makes placement O(floats²) on a page, so a
            // page of nothing but floats was O(floats³); a page already at MaxBuilderFloatsPerPage
            // stops stepping (AddFloat has also stopped registering, so FloatCount is the cap), asks
            // for the segment at `y` once, and places the float there or, if nothing fits, as a block.
            // A page below the cap steps exactly as before.
            int maxSteps = ctx.FloatCount >= PdfLimits.MaxBuilderFloatsPerPage ? 0 : ctx.FloatCount;
            for (int step = 0; ctx.HasFloats && step <= maxSteps; step++)
            {
                (segmentLeft, segmentWidth) = ctx.GetLineSegment(x, width, top, FloatProbeHeightPt);
                if (segmentWidth >= width - 0.01 || segmentWidth - floatWidth >= MinTextWidthBesideFloatPt) break;
                double below = ctx.NearestFloatBottom(x, width, top, FloatProbeHeightPt);
                if (!(below < top)) break;
                top = below;
            }
            left = side == FloatSide.Left ? segmentLeft : segmentLeft + segmentWidth - floatWidth;
            return segmentWidth - floatWidth >= MinTextWidthBesideFloatPt;
        }

        /// <summary>
        /// Draws a floated paragraph at one edge of its band from <paramref name="y"/>, registers
        /// the space it occupies, and returns the cursor for the block after it: <paramref name="y"/>
        /// itself, so that block starts <em>beside</em> the float — or, for a float as wide as its
        /// band, the bottom of the paragraph drawn as a block. The section's flow calls it with
        /// pagination, a table cell (<c>TableRenderer.DrawCellBlock</c>) without.
        /// </summary>
        /// <remarks>
        /// <para><b>Not advancing the cursor is the whole difference.</b> Every other block returns
        /// a new cursor and the next one starts below it; a float returns the cursor unchanged and
        /// instead leaves an exclusion behind, so the following paragraphs wrap down its side and
        /// close over its bottom. That is what makes this re-flow rather than a two-column band,
        /// which keeps the text in its column to the end of the block.</para>
        ///
        /// <para><b>It paginates on its own</b> where asked. The float is rendered with
        /// <c>paginate: true</c>, so a float taller than the remaining space moves to the next page —
        /// and because <c>StartPage</c> clears the registry, its exclusion lands on the page it was
        /// actually drawn on rather than the one it was pushed off.</para>
        /// </remarks>
        internal static double PlaceFloat(Ctx ctx, Paragraph para, double x, double y, double width, bool paginate)
        {
            if (!FloatBox(ctx, para, x, y, width, out double left, out double floatWidth, out double top))
                return RenderParagraphAt(ctx, para, x, y, width, paginate);

            double bottom = RenderParagraphAt(ctx, para, left, top, floatWidth, paginate);

            // RenderParagraphAt may have moved to a new page, in which case `top` names a y on the
            // page before it. Re-read the cursor's page-relative top from the value it returned:
            // the exclusion must describe where the float was DRAWN, and the flow continues there.
            bool newPage = bottom > top;
            if (newPage) top = ctx.TopStart;

            ctx.AddFloat(new Ctx.FloatExclusion(left, left + floatWidth, top, bottom));

            // The cursor does not advance: the next block starts beside the float, not below it —
            // where it was asked to, even when the float itself dropped below earlier floats.
            return newPage ? top : y;
        }

        /// <summary>
        /// The measure of <see cref="PlaceFloat"/>: registers the float's exclusion where it would be
        /// drawn, measured rather than drawn, and returns the cursor as <see cref="PlaceFloat"/>
        /// would. A table cell is measured with it, so its row is as tall as the float and the
        /// blocks after the float are measured beside it, as they will be drawn.
        /// </summary>
        internal static double MeasureFloat(Ctx ctx, Paragraph para, double x, double y, double width)
        {
            if (!FloatBox(ctx, para, x, y, width, out double left, out double floatWidth, out double top))
                return y - MeasureParagraphAt(ctx, para, x, y, width);
            double bottom = top - MeasureParagraphAt(ctx, para, left, top, floatWidth);
            ctx.AddFloat(new Ctx.FloatExclusion(left, left + floatWidth, top, bottom));
            return y;
        }

        // ================================================================ contents

        /// <summary>Gap between an entry's text and its page number, in points.</summary>
        private const double TocNumberGutterPt = 24;

        /// <summary>
        /// Draws a table of contents: one line per heading, indented by level, with the page the
        /// heading starts on at the right margin.
        /// </summary>
        /// <remarks>
        /// <para><b>Each entry is an ordinary paragraph with a right tab stop.</b> That is what
        /// gives it the wrapping, bidi, fonts and links every other paragraph gets, and it is what
        /// keeps the number at the margin whatever its width — which is what makes the two
        /// layout passes agree.</para>
        ///
        /// <para><b>On the first pass the number is blank.</b> The pass exists to learn where the
        /// headings land, and no number is known yet; drawing a placeholder digit instead would
        /// make the line measure differently from the one the second pass draws, which is the
        /// misalignment the <c>{{NUMPAGES}}</c> field documents and this design avoids.</para>
        /// </remarks>
        private static double RenderToc(Ctx ctx, TocBlock toc, double x, double y, double width)
        {
            List<TocEntries.Entry> entries = TocEntries.Collect(ctx.Document, toc.MaxLevel);

            PdfStructureElement? tocEl = ctx.NewElement(PdfStructureTypes.TOC);
            using var _ = ctx.EnterParent(tocEl);

            if (!string.IsNullOrEmpty(toc.Title))
            {
                var title = new Paragraph { Role = PdfStructureTypes.H1, SpacingAfter = 6 };
                title.Runs.Add(new TextRun { Text = toc.Title!, FontSize = 16, IsBold = true });
                ctx.Y = y;
                y = RenderParagraphAt(ctx, title, x, y, width, paginate: true);
            }

            foreach (TocEntries.Entry entry in entries)
            {
                PdfStructureElement? itemEl = ctx.NewElement(PdfStructureTypes.TOCI);
                using var __ = ctx.EnterParent(itemEl);

                ctx.Y = y;
                y = RenderParagraphAt(ctx, TocEntryParagraph(ctx, toc, entry, x, width),
                                      x, y, width, paginate: true);
            }

            return y;
        }

        /// <summary>One contents line: the heading's text, a leader, and its page number.</summary>
        private static Paragraph TocEntryParagraph(
            Ctx ctx, TocBlock toc, TocEntries.Entry entry, double x, double width)
        {
            double indent = System.Math.Max(0, entry.Level - 1) * toc.LevelIndentPt;

            var paragraph = new Paragraph
            {
                IndentLeft = indent,
                SpacingBefore = 0,
                SpacingAfter = 0,
            };

            // The number is right-aligned at the text column's right edge, so its width never
            // moves the entry -- which is what lets the second pass draw a number the first pass
            // did not know without shifting anything after it.
            paragraph.Tabs.Add(new TabStop
            {
                PosPt = width - indent,
                Align = TabAlignment.Right,
                Leader = toc.DottedLeader ? TabLeader.Dot : TabLeader.None,
            });

            paragraph.Runs.Add(new TextRun
            {
                Text = entry.Text,
                LinkAnchor = entry.Anchor,
            });
            paragraph.Runs.Add(new TabRun());
            paragraph.Runs.Add(new TextRun { Text = PageNumberOf(ctx, entry) });

            return paragraph;
        }

        /// <summary>
        /// The recorded page of a heading, in its section's format, or the empty string on the
        /// first pass.
        /// </summary>
        private static string PageNumberOf(Ctx ctx, TocEntries.Entry entry)
        {
            if (ctx.TocPages == null) return "";

            return ctx.TocPages.TryGetValue(entry.Heading, out string? page) ? page : "";
        }

        /// <summary>
        /// <paramref name="pageNumber"/> in one of the five formats
        /// <see cref="Model.Section.PageNumberFormat"/> documents; anything else is decimal.
        /// </summary>
        /// <remarks>
        /// The format vocabulary and both alphabets are <c>Core.Util.OrdinalNumbering</c>'s, which
        /// <c>DocxToPdf</c>'s legacy renderer reaches for the same five strings. This method is the
        /// name the exporter calls it by, nothing more.
        /// </remarks>
        internal static string FormatPageNumber(int pageNumber, string? format)
            => Core.Util.OrdinalNumbering.FormatOoxmlNumber(pageNumber, format);

        // Renders a paragraph starting at (x, y) within the given width. When paginate is
        // true, wrapped lines that overflow the page break to a new page. Returns the new y.
        /// <summary>
        /// The base direction to resolve a paragraph's bidi against: its own
        /// <see cref="Paragraph.Direction"/>, else the section's.
        ///
        /// <para><b>One resolver, called from every site that asks the question.</b> Here,
        /// <see cref="RenderListItem"/> and <c>TableRenderer</c> all ask it; an open-coded
        /// <c>== TextDirection.RightToLeft</c> silently answers "left-to-right" for
        /// <see cref="TextDirection.Auto"/>, and three independent comparisons are three places for
        /// one to be changed while another silently disagrees.</para>
        /// </summary>
        internal static BaseDirection ResolveBaseDirection(Ctx ctx, Paragraph para)
        {
            var dir = para?.Direction ?? ctx.Section.Direction;
            return dir switch
            {
                TextDirection.RightToLeft => BaseDirection.RightToLeft,
                TextDirection.Auto => BaseDirection.Auto,
                _ => BaseDirection.LeftToRight,
            };
        }

        /// <summary>Whether a section's own direction resolves right-to-left (tables, list markers).</summary>
        /// <remarks>
        /// <b>It has to resolve <see cref="TextDirection.Auto"/> too.</b> This was
        /// <c>section.Direction == TextDirection.RightToLeft</c>, so an Auto Hebrew section got
        /// right-to-left paragraphs from <see cref="ResolveBaseDirection"/> and left-to-right table
        /// columns from here — the same document disagreeing with itself inside one exporter, and
        /// the reason <see cref="ModelRules.IsRightToLeft(Section)"/> exists.
        /// </remarks>
        internal static bool IsSectionRtl(Section section) => ModelRules.IsRightToLeft(section);

        // band: the band a placed paragraph is set in (AbsolutePlacement.BandHeightPt) — its top and
        // height, the box a transform is taken about instead of the paragraph's own; null for none.
        internal static double RenderParagraphAt(Ctx ctx, Paragraph para, double x, double y,
            double width, bool paginate, (double Top, double Height)? band = null)
        {
            // CSS transform affects PAINTING, not layout: the box keeps the size and position flow
            // gave it, and only the marks are transformed. So this wraps the whole render and
            // returns the untransformed y — anything else would make a rotated paragraph push the
            // next one sideways.
            //
            // The matrix is taken about the paragraph's transform-origin (the centre of its border
            // box unless the model says where), which needs the paragraph's height before it is
            // drawn — one measurement, by the same layout the render will do.
            //
            // Link annotations follow it by hand. A /Rect is in DEFAULT user space (§12.5.2), which
            // a content-stream `cm` does not reach, so the rectangles registered while the paragraph
            // drew are remapped afterwards through the same matrix — as the bounds of their turned
            // corners, since a rotated link is still one /Rect, and the transform turns about the block's
            // origin, not the page's.
            if (para.Transform is { Length: 6 } m)
            {
                double top, height;
                if (band is (double bandTop, double bandHeight)) { top = bandTop - para.SpacingBefore; height = bandHeight; }
                else
                {
                    double h = MeasureParagraphAt(ctx, para, x, y, width);
                    top = y - para.SpacingBefore;
                    height = Math.Max(0, h - para.SpacingBefore - para.SpacingAfter);
                }
                // A transform is one painting unit: its `q`/`cm` and the matching `Q` must sit on one
                // page. If the render paginated inside it, the first page kept an open `q` — its
                // footer and footnotes then drew under the transform — and the second page ran a `Q`
                // with nothing to pop. CSS does not fragment a transformed box across pages either. So
                // when this paragraph may paginate and does not fit the rest of the page (but fits a
                // page at all, and is not already at the page top), move to the next page first, then
                // draw the whole thing there without paginating within the transform (2:7).
                if (paginate && band is null && top - height < ctx.BottomLimit - 0.01
                    && height <= ctx.TopStart - ctx.BottomLimit && top < ctx.TopStart - 0.01)
                {
                    ctx.NewPageContinue();
                    y = ctx.Y;
                    top = y - para.SpacingBefore;
                }

                double[] about = TransformAbout(m, para.TransformOrigin, x, width, top, height);
                var marks = AnnotationMarks.Of(ctx);
                ctx.Page.SaveState().Transform(about[0], about[1], about[2], about[3], about[4], about[5]);
                try { return RenderParagraphUntransformed(ctx, para, x, y, width, paginate: false); }
                finally
                {
                    ctx.Page.RestoreState();
                    marks.Remap(ctx, about);
                }
            }

            return RenderParagraphUntransformed(ctx, para, x, y, width, paginate);
        }

        /// <summary>
        /// <paramref name="m"/> about the transform origin of a border box <paramref name="width"/>
        /// wide and <paramref name="height"/> tall whose left edge is at <paramref name="left"/> and
        /// top at <paramref name="top"/>: <paramref name="origin"/>'s fraction and offset per axis
        /// (<see cref="Paragraph.TransformOrigin"/>), the centre when it is null or malformed.
        /// </summary>
        internal static double[] TransformAbout(double[] m, double[]? origin, double left, double width, double top, double height)
        {
            double fx = 0.5, dx = 0, fy = 0.5, dy = 0;
            if (origin is { Length: 4 })
            {
                fx = origin[0]; dx = origin[1]; fy = origin[2]; dy = origin[3];
            }
            double ox = left + fx * width + dx;
            double oy = top - (fy * height + dy);
            return TransformMatrix.About(m, ox, oy);
        }

        /// <summary>
        /// How many link and form-field rectangles the page held before a transformed region drew,
        /// so the ones it added can be brought along afterwards (<see cref="Remap"/>).
        /// </summary>
        internal readonly struct AnnotationMarks
        {
            private AnnotationMarks(PdfContentStreamBuilder page, int links, int internalLinks, int fields)
            {
                Page = page; Links = links; InternalLinks = internalLinks; Fields = fields;
            }

            public PdfContentStreamBuilder Page { get; }
            public int Links { get; }
            public int InternalLinks { get; }
            public int Fields { get; }

            public static AnnotationMarks Of(Ctx ctx)
                => new AnnotationMarks(ctx.Page, ctx.Page.Links.Count, ctx.Page.InternalLinks.Count, ctx.PlacedFormFields.Count);

            /// <summary>
            /// Remaps the rectangles registered since these marks through <paramref name="matrix"/>,
            /// a content-space matrix: a rectangle is held in default user space, so it is taken back
            /// through the page's scale, transformed, and scaled again. Only on the page the marks
            /// were taken on — a paragraph that broke to the next page leaves that page's links where
            /// they are, as its transform there is the page's problem.
            /// </summary>
            public void Remap(Ctx ctx, double[] matrix)
            {
                if (!ReferenceEquals(ctx.Page, Page)) return;
                double s = ctx.Scale;
                double[] inDefaultSpace = TransformMatrix.Multiply(TransformMatrix.Multiply(
                    new[] { 1 / s, 0, 0, 1 / s, 0, 0 }, matrix), new[] { s, 0, 0, s, 0, 0 });
                Page.RemapLinkRectangles(Links, InternalLinks, inDefaultSpace);
                for (int i = Fields; i < ctx.PlacedFormFields.Count; i++)
                {
                    PlacedFormField f = ctx.PlacedFormFields[i];
                    var r = TransformMatrix.BoundsOf(inDefaultSpace, f.X, f.Y, f.X + f.Width, f.Y + f.Height);
                    ctx.PlacedFormFields[i] = new PlacedFormField(f.Field, f.PageIndex, r.X0, r.Y0, r.X1 - r.X0, r.Y1 - r.Y0, f.FontSize);
                }
            }
        }

        private static double RenderParagraphUntransformed(Ctx ctx, Paragraph para, double x,
            double y, double width, bool paginate)
        {
            // A paragraph the model marked as decoration draws inside an artifact scope instead
            // of becoming a structure element — a horizontal rule is not a paragraph of nothing.
            using var artifact = para.IsArtifact
                ? ctx.EnterArtifact(PdfArtifactTypes.Layout)
                : default;

            // Created eagerly but attached lazily: EmitLine only marks content when there IS
            // content, and PdfStructureTree.Prune drops an element that never got any. The mapper
            // emits genuinely empty paragraphs (an <hr>, a vMerge placeholder), and an empty /P
            // is exactly the hollow tagging a PDF/UA checker reports.
            PdfStructureElement? paraEl = null;
            if (ctx.Tagged && ctx.ArtifactType == null)
                paraEl = ctx.NewElement(para.Role ?? PdfStructureTypes.P);

            var baseDir = ResolveBaseDirection(ctx, para);
            // Only used for the box/indent geometry decided before any text is laid out. Under
            // Auto the authoritative answer comes from the resolved paragraph inside RenderLines.
            bool rtl = baseDir == BaseDirection.RightToLeft;

            // Not at the top of a page, which a page break would leave blank — the top of a later
            // content area is not one.
            if (paginate && para.PageBreakBefore && (ctx.Y < ctx.TopStart - 0.01 || !ctx.InFirstContentArea))
            {
                // A page break ends the page whatever content areas it has left (Section.ContentAreas);
                // the paragraph moves to the next page's first, from wherever the flow was.
                double shift = ctx.RegionShiftX;
                ctx.NewPageContinue(wholePage: true);
                x += ctx.RegionShiftX - shift;
                y = ctx.Y;
            }

            y -= para.SpacingBefore;
            // Commit the space-before to ctx.Y so the line loop (which re-reads ctx.Y on its
            // first paginated line) doesn't discard it. No-op when SpacingBefore == 0.
            if (paginate) ctx.Y = y;

            var (indentLeft, indentRight) = EffectiveIndents(ctx, para, width);
            double bandLeft = x + indentLeft;
            double bandRight = x + width - indentRight;
            double avail = Math.Max(1, bandRight - bandLeft);

            // Paragraph shading / borders: reserve the block, fill the background behind the
            // text, then stroke the border around the rendered region.
            bool hasBackgroundImage = HasBackgroundImage(para);
            bool hasBox = HasBox(para);
            double insetTop = BoxInset(para, para.BoxInsetTop), insetBottom = BoxInset(para, para.BoxInsetBottom);
            double insetLeft = BoxInset(para, para.BoxInsetLeft), insetRight = BoxInset(para, para.BoxInsetRight);
            double boxTop = y;
            // Everything this paragraph paints — shading, background image, borders, and every
            // piece of its text — is scaled by this. Resolved once so the box passes below and
            // BuildPiece cannot disagree about it.
            double paraAlpha = Alpha01(para.Opacity);
            // A box below full opacity with content over it is a transparency group: the
            // paragraph draws itself, opaque, into a form the page paints once at its opacity — CSS
            // composites the element as one layer, and constant alpha on each object let the
            // background show through the text. Not for a box taller than the page's remaining
            // height, which breaks across pages and cannot be one form; that keeps the constant
            // alpha and the report. Nested: a group inside a box's group is the
            // outer group's content, painted there at the paragraph's own relative opacity.
            bool group = hasBox && paraAlpha < 1 && HasVisibleContent(para);
            PdfContentStreamBuilder? outerPage = null;
            if (hasBox)
            {
                double ch = MeasureParagraph(ctx, para, width) - para.SpacingBefore - para.SpacingAfter;

                // Reserving the block is the only part of this that is a PAGINATION decision —
                // without it a shaded paragraph could split across a break and the fill would land
                // on one page while its text sat on the next. The FILL is not, and is drawn outside
                // this branch, so a shaded paragraph rendered with paginate:false — the table-cell,
                // header and footer paths — gets its background as it gets its border.
                if (paginate)
                {
                    double shift = ctx.RegionShiftX;
                    ctx.EnsureSpace(ch + insetTop + insetBottom);
                    bandLeft += ctx.RegionShiftX - shift;
                    bandRight += ctx.RegionShiftX - shift;
                    y = ctx.Y;
                    boxTop = y;
                    if (ch + insetTop + insetBottom > y - ctx.BottomLimit + 0.01) group = false;
                }
                if (group)
                {
                    outerPage = ctx.BeginOpacityGroup(para);
                    paraAlpha = 1;   // the group carries the opacity; inside it everything is opaque
                }
                else ReportOpacityApproximation(ctx, para, hasBox);

                double boxX = bandLeft - insetLeft;
                double boxY = y - ch - insetBottom;
                double boxW = (bandRight - bandLeft) + insetLeft + insetRight;
                double boxH = ch + insetTop + insetBottom;
                Corners radius = Corners.Of(para.CornerRadiusPt, para.CornerRadiiPt, para.CornerRadiiYPt, boxW, boxH, percent: para.CornerRadiusPercent,
                                            radiiPercent: para.CornerRadiiPercent, radiiYPercent: para.CornerRadiiYPercent);

                // The shadow goes down FIRST — it is behind everything, including the background
                // colour, and drawing it after would hide the fill it is supposed to sit behind.
                using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                    foreach (var shadow in PaintOrder(para.Shadow, para.MoreShadows, inset: false))
                        DrawShadow(ctx, shadow, boxX, boxY, boxW, boxH, radius, paraAlpha);

                if (!string.IsNullOrEmpty(para.Shading))
                {
                    // Background shading is decoration, so on a tagged page it is an artifact —
                    // the rule TableRenderer already applies to its own cell shading. Unmarked ink
                    // on a tagged page is a PDF/UA violation in its own right.
                    var (sr, sg, sb) = ParseColorD(para.Shading);
                    using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                    {
                        ctx.Page.SaveState().SetFillColorRgb((float)sr, (float)sg, (float)sb);
                        ApplyFillAlpha(ctx, paraAlpha);
                        AppendBoxPath(ctx, boxX, boxY, boxW, boxH, radius);
                        ctx.Page.Fill().RestoreState();
                    }
                }

                if (hasBackgroundImage)
                {
                    // AFTER the shading fill, never instead of it: CSS layers the image on top of
                    // the background colour, so a translucent or non-repeating image still shows
                    // the colour behind it. Decoration, so the same artifact scope for the same
                    // PDF/UA reason — unmarked ink on a tagged page is a violation in itself.
                    using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                        DrawBackgroundImage(ctx, para.BackgroundImage, boxX, boxY, boxW, boxH, paraAlpha);
                }

                // Inset shadows over the background, inside the padding box — the fill's edge less
                // half each border, which is stroked along it.
                double bt = Border.PaintedWidth(para.BorderTop) / 2, br = Border.PaintedWidth(para.BorderRight) / 2;
                double bb = Border.PaintedWidth(para.BorderBottom) / 2, bl = Border.PaintedWidth(para.BorderLeft) / 2;
                using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                    foreach (var shadow in PaintOrder(para.Shadow, para.MoreShadows, inset: true))
                        DrawInsetShadow(ctx, shadow, boxX + bl, boxY + bb, boxW - bl - br, boxH - bt - bb,
                                        radius.Inset(bl, bt, br, bb), paraAlpha);
            }

            bool hasImage = false;
            foreach (var r in para.Runs) if (r is ImageRun) { hasImage = true; break; }

            // A transparency group is one form XObject the page paints once; it cannot fragment
            // across a page break (CSS composites the element as one layer). The box was already
            // reserved whole above, and a box taller than a page kept its constant alpha instead of
            // a group. So the group's content is drawn with pagination OFF: if MeasureParagraph fell
            // short of the real drawn height the box overflows its page — as CSS clips a composited
            // layer — rather than starting a new page inside the open group, whose q/Do and close
            // would then straddle the break and corrupt the stack (Builder-2:6, the group twin of 2:7).
            bool contentPaginate = paginate && !group;

            try
            {
                if (!hasImage)
                {
                    y = RenderLines(ctx, para, para.Runs, bandLeft, avail, y, contentPaginate, baseDir, paraEl);
                }
                else
                {
                    // Render text in runs up to each image, then the image as a block, in order —
                    // each piece from the content area the pieces before it reached.
                    double shiftAtStart = ctx.RegionShiftX;
                    double Band() => contentPaginate ? bandLeft + ctx.RegionShiftX - shiftAtStart : bandLeft;
                    var buf = new List<IInlineElement>();
                    foreach (var run in para.Runs)
                    {
                        if (run is ImageRun img)
                        {
                            y = RenderLines(ctx, para, buf, Band(), avail, y, contentPaginate, baseDir, paraEl);
                            buf.Clear();
                            y = DrawImageBlock(ctx, img, Band(), avail, y, contentPaginate, para.Alignment, rtl);
                        }
                        else buf.Add(run);
                    }
                    y = RenderLines(ctx, para, buf, Band(), avail, y, contentPaginate, baseDir, paraEl);
                }

                // Stroke the paragraph border around the rendered region. Decoration, so an artifact
                // on a tagged page — same reason as the shading fill above.
                if (hasBox)
                {
                    double bx0 = bandLeft - insetLeft, bx1 = bandRight + insetRight;
                    double by0 = y - insetBottom, by1 = boxTop + insetTop;
                    using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                    if (!StrokeRoundedBorder(ctx, para.BorderTop, para.BorderRight, para.BorderBottom, para.BorderLeft,
                            bx0, by0, bx1, by1,
                            Corners.Of(para.CornerRadiusPt, para.CornerRadiiPt, para.CornerRadiiYPt, bx1 - bx0, by1 - by0, percent: para.CornerRadiusPercent,
                                       radiiPercent: para.CornerRadiiPercent, radiiYPercent: para.CornerRadiiYPercent),
                            paraAlpha))
                    {
                        StrokeEdge(ctx.Page, para.BorderTop, bx0, by1, bx1, by1, paraAlpha);
                        StrokeEdge(ctx.Page, para.BorderBottom, bx0, by0, bx1, by0, paraAlpha);
                        StrokeEdge(ctx.Page, para.BorderLeft, bx0, by0, bx0, by1, paraAlpha);
                        StrokeEdge(ctx.Page, para.BorderRight, bx1, by0, bx1, by1, paraAlpha);
                    }
                }
            }
            finally
            {
                // The page is the page again whatever happened inside, and the group is painted
                // once at the paragraph's opacity, in one marked sequence for the paragraph.
                if (outerPage != null) ctx.EndOpacityGroup(outerPage, Alpha01(para.Opacity), paraEl);
            }

            y -= para.SpacingAfter;
            if (paginate) ctx.Y = y;
            return y;
        }

        /// <summary>Whether the paragraph paints anything over its box — text or an image — so that group and constant alpha differ.</summary>
        private static bool HasVisibleContent(Paragraph para)
        {
            foreach (var run in para.Runs)
            {
                if (run is TextRun tr && !string.IsNullOrEmpty(tr.Text)) return true;
                if (run is ImageRun) return true;
            }
            return false;
        }

        /// <summary>Whether a paragraph carries background-image bytes worth decoding.</summary>
        internal static bool HasBackgroundImage(Paragraph para) =>
            para?.BackgroundImage?.Data != null && para.BackgroundImage.Data.Length > 0;

        /// <summary>
        /// Ceiling on the tiles one background image may draw.
        ///
        /// <para>The destination box divided by the tile size is a number the <b>content</b>
        /// chooses: a 1×1 pixel GIF behind a full page asks for roughly 600 × 800 tiles, each of
        /// which is a <c>cm</c>/<c>Do</c> pair in the content stream. The cap is checked from the
        /// arithmetic <i>before</i> anything is drawn, so an absurd request costs the division and
        /// nothing else.</para>
        ///
        /// <para>Over the cap the background is drawn <b>truncated</b> rather than stretched to
        /// fit, and reported. That follows this repository's stated preference for an obviously
        /// missing result over a confidently wrong one — stretching a patterned tile across the
        /// box produces something that looks deliberate and is not what the document said.</para>
        /// </summary>
        internal const int MaxBackgroundTiles = 512;

        /// <summary>
        /// Paints a paragraph's background image into the box <paramref name="x"/>,
        /// <paramref name="y"/> (bottom-left), <paramref name="w"/> × <paramref name="h"/>,
        /// honouring <c>background-repeat</c> and <c>background-size</c>.
        ///
        /// <para>The image XObject is registered <b>once</b> and every tile references the same
        /// resource name; registering inside the loop would put N copies of the samples in the
        /// file. The whole thing is clipped to the box so the final row and column can overhang
        /// the way CSS's do, and <c>SaveState</c>/<c>RestoreState</c> keep that clip off the text
        /// that follows.</para>
        /// </summary>
        private static void DrawBackgroundImage(Ctx ctx, BackgroundImage? bg,
            double x, double y, double w, double h, double alpha)
        {
            if (bg == null || w <= 0 || h <= 0) return;

            var dec = DecodeOnce(ctx, bg.Data);
            if (dec == null)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ImageDecodeFailed,
                    $"a background image of {bg.Data.Length:N0} bytes could not be decoded, so its " +
                    "box was drawn without it");
                return;
            }

            // "Intrinsic" is the decoder's pixel size at CSS's 96 dpi, unless the model states a
            // size — the same convention BuildSizedImageRun uses for an <img>.
            double tileW = bg.WidthPt > 0 ? bg.WidthPt : PdfUnits.PixelsToPoints(dec.Width);
            double tileH = bg.HeightPt > 0 ? bg.HeightPt : PdfUnits.PixelsToPoints(dec.Height);
            if (tileW <= 0 || tileH <= 0) return;

            // cover / contain scale the tile to the box preserving aspect ratio, and therefore
            // draw exactly one tile whatever background-repeat says — which is what CSS does.
            var repeat = bg.Repeat;
            if (bg.Size != BackgroundSize.Auto)
            {
                double scale = bg.Size == BackgroundSize.Cover
                    ? Math.Max(w / tileW, h / tileH)
                    : Math.Min(w / tileW, h / tileH);
                tileW *= scale;
                tileH *= scale;
                repeat = BackgroundRepeat.NoRepeat;
            }

            // The anchor tile sits at the position — a fraction of the room the box has beyond one
            // tile, as CSS percentages place it — and a repeat tiles outward from it to the box's
            // edges. At position 0 the first tile is the box's top-left corner, as it always was.
            bool tileX = repeat is BackgroundRepeat.Repeat or BackgroundRepeat.RepeatX;
            bool tileY = repeat is BackgroundRepeat.Repeat or BackgroundRepeat.RepeatY;
            double anchorLeft = x + (w - tileW) * bg.PositionX;
            double anchorTop = y + h - (h - tileH) * bg.PositionY;
            double firstLeft = tileX ? anchorLeft - Math.Ceiling((anchorLeft - x) / tileW) * tileW : anchorLeft;
            double firstTop = tileY ? anchorTop + Math.Ceiling((y + h - anchorTop) / tileH) * tileH : anchorTop;

            int cols = tileX ? SaturatingCast.ToInt(Math.Min(Math.Ceiling((x + w - firstLeft) / tileW), MaxBackgroundTiles + 1L)) : 1;
            int rows = tileY ? SaturatingCast.ToInt(Math.Min(Math.Ceiling((firstTop - y) / tileH), MaxBackgroundTiles + 1L)) : 1;
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);

            long requested = (long)cols * rows;
            if (requested > MaxBackgroundTiles)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                    $"a background image tiled {cols}×{rows} would need {requested:N0} draws; " +
                    $"only the first {MaxBackgroundTiles:N0} were painted (PdfExporter.MaxBackgroundTiles). " +
                    "A tile this small relative to its box is usually a 1×1 spacer that wants " +
                    "background-size:cover.");
            }

            string res = AddDecodedImage(ctx.Page, dec);

            ctx.Page.SaveState();
            ApplyFillAlpha(ctx, alpha);
            ctx.Page.Rectangle(x, y, w, h).ClipNonzero();

            int drawn = 0;
            for (int row = 0; row < rows && drawn < MaxBackgroundTiles; row++)
            {
                // Rows run downward from the first one, which is at or above the box's top edge.
                double tileTop = firstTop - row * tileH;
                for (int col = 0; col < cols && drawn < MaxBackgroundTiles; col++)
                {
                    ctx.Page.DrawImage(res, firstLeft + col * tileW, tileTop - tileH, tileW, tileH);
                    drawn++;
                }
            }

            ctx.Page.RestoreState();
        }

        /// <summary>
        /// Strokes a rounded box's border as one outline when it can be one: the box has a rounded
        /// corner and every edge it draws is the same border. Its centre line is the rectangle
        /// (<paramref name="x0"/>, <paramref name="y0"/>)–(<paramref name="x1"/>, <paramref name="y1"/>)
        /// with corners <paramref name="centreLine"/>; an edge a page break cuts
        /// (<paramref name="cutTop"/>, <paramref name="cutBottom"/>) is clipped off. Returns false,
        /// having drawn nothing, when the box is square or its edges differ — the caller strokes
        /// them one by one.
        /// </summary>
        /// <remarks>
        /// Four straight strokes met at square corners over the rounded fill, so every rounded
        /// Bootstrap box — <c>.panel</c>, <c>.alert</c>, <c>.well</c>, <c>.form-control</c> —
        /// printed with square corners (found against Chrome). Edges that differ keep the straight
        /// strokes: CSS blends two borders round a corner, which one path cannot.
        /// </remarks>
        private static bool StrokeRoundedBorder(Ctx ctx, Border? top, Border? right, Border? bottom, Border? left,
            double x0, double y0, double x1, double y1, Corners centreLine, double alpha,
            bool cutTop = false, bool cutBottom = false)
        {
            if (centreLine.IsSquare) return false;
            Border? shared = null;
            foreach (var (edge, drawn) in new[] { (top, !cutTop), (right, true), (bottom, !cutBottom), (left, true) })
            {
                if (!drawn) continue;
                if (!Border.Paints(edge)) return false;
                if (shared == null) shared = edge;
                else if (edge.Width != shared.Width || !string.Equals(edge.Color, shared.Color, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (shared == null) return false;

            double half = shared.Width / 2;
            var (r, g, b) = ParseColorD(shared.Color);
            ctx.Page.SaveState();
            if (cutTop || cutBottom)
            {
                // The outline closes across the cut; the clip takes that edge's band away.
                double lo = cutBottom ? y0 + half : y0 - half, hi = cutTop ? y1 - half : y1 + half;
                ctx.Page.Rectangle(x0 - half, lo, x1 - x0 + 2 * half, hi - lo).ClipNonzero();
            }
            ctx.Page.SetStrokeColorRgb((float)r, (float)g, (float)b).SetLineWidth((float)Math.Max(0.2, shared.Width));
            ApplyStrokeAlpha(ctx, alpha);
            AppendBoxPath(ctx, x0, y0, x1 - x0, y1 - y0, centreLine);
            ctx.Page.Stroke().RestoreState();
            return true;
        }

        /// <summary>
        /// Strokes one border edge from (<paramref name="x0"/>, <paramref name="y0"/>) to
        /// (<paramref name="x1"/>, <paramref name="y1"/>) in its colour, at least 0.2pt wide, under
        /// <paramref name="alpha"/>; nothing for a border that does not paint
        /// (<see cref="Border.Paints"/>). Every edge the exporter strokes alone — a paragraph's, a
        /// box's, a table's and a cell's, the page border's.
        /// </summary>
        internal static void StrokeEdge(PdfContentStreamBuilder page, Border? b, double x0, double y0, double x1,
            double y1, double alpha = 1)
        {
            if (!Border.Paints(b)) return;
            var (r, g, bl) = ParseColorD(b.Color);
            page.SaveState().SetStrokeColorRgb((float)r, (float)g, (float)bl)
                .SetLineWidth((float)Math.Max(0.2, b.Width));
            ApplyStrokeAlpha(page, alpha);
            page.MoveTo(x0, y0).LineTo(x1, y1).Stroke().RestoreState();
        }

        // ================================================================== constant alpha

        /// <summary>
        /// A model opacity coerced into the 0–1 <c>/ca</c> range. <c>NaN</c> becomes <b>1</b>, not
        /// 0: an unparsed or uninitialised value must leave the content visible, since an
        /// invisible paragraph and a missing one are indistinguishable on the page.
        /// </summary>
        internal static double Alpha01(double v)
            => double.IsNaN(v) ? 1.0 : (v < 0 ? 0.0 : (v > 1 ? 1.0 : v));

        /// <summary>
        /// Sets non-stroking constant alpha on the <b>current</b> graphics state, which the caller
        /// must already have bracketed with <c>SaveState</c>/<c>RestoreState</c> (or, for the text
        /// pass, must reset itself) — <c>/ca</c> is graphics state and survives <c>ET</c>.
        ///
        /// <para>A no-op at full opacity, and that is load-bearing rather than an optimisation: a
        /// document that never sets <c>Opacity</c> must mint no <c>/ExtGState</c> resource and emit
        /// no <c>gs</c>, so its bytes are unchanged.</para>
        /// </summary>
        private static void ApplyFillAlpha(Ctx ctx, double alpha)
        {
            if (alpha >= 1) return;
            ctx.Page.SetGraphicsState(ctx.Page.AddExtGState((float)alpha, null));
        }

        /// <summary>Stroking counterpart of <see cref="ApplyFillAlpha"/> (<c>/CA</c>).</summary>
        private static void ApplyStrokeAlpha(Ctx ctx, double alpha) => ApplyStrokeAlpha(ctx.Page, alpha);

        private static void ApplyStrokeAlpha(PdfContentStreamBuilder page, double alpha)
        {
            if (alpha >= 1) return;
            page.SetGraphicsState(page.AddExtGState(null, (float)alpha));
        }

        /// <summary>
        /// Reports that constant alpha is standing in for CSS group opacity, once per export — where
        /// a paragraph with a box below full opacity could not be a transparency group: its box is
        /// taller than the page's remaining height and breaks across pages, or it is inside another
        /// group's content.
        ///
        /// <para>Presence is not deviation: this fires only where the two models actually give
        /// different pixels — a paragraph below full opacity that paints something (shading,
        /// background image, a border) <i>behind its own content</i>. CSS composites that as one
        /// layer; <c>/ca</c> composites the text over an already-faded background. With nothing
        /// behind the text the two agree exactly, so an ordinary faded paragraph must not warn.</para>
        /// </summary>
        private static void ReportOpacityApproximation(Ctx ctx, Paragraph para, bool hasBox)
        {
            if (ctx.ReportedOpacityApproximation) return;
            if (!hasBox || Alpha01(para.Opacity) >= 1) return;
            if (!HasVisibleContent(para)) return;

            ctx.ReportedOpacityApproximation = true;
            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                "Paragraph.Opacity is emitted as PDF constant alpha (/ca), which composites each " +
                "object separately; CSS opacity composites the element once as a group. This " +
                "paragraph paints a background or border behind its own content, so the two " +
                "differ — the text is drawn over an already-faded backdrop. A paragraph whose box " +
                "fits on one page is drawn as a transparency group instead; this one breaks across " +
                "pages, or sits inside another group.");
        }

        /// <summary>
        /// Lays a paragraph's lines out from <paramref name="y"/> in the band at <paramref name="bandLeft"/>,
        /// <paramref name="avail"/> wide. A paragraph the flow carries into a content area of another
        /// width (<see cref="Section.ContentAreas"/>) is broken again there: its lines from the first in
        /// the new region are a paragraph of their own (<see cref="SplitRunsAtLines"/>), laid out in the
        /// band the region's width gives — the band's insets kept — so it fills the region it lands in
        /// rather than the one it began in — inside the boxes open round it too, which the region's
        /// width lays out again (<see cref="Ctx.FlowWidth"/>). Taken for a paragraph <see cref="CanSplit"/>
        /// admits; any other keeps its width. A loop, not a call, so a paragraph through many regions
        /// cannot deepen the stack.
        /// </summary>
        private static double RenderLines(Ctx ctx, Paragraph para, IList<IInlineElement> runs,
            double bandLeft, double avail, double y, bool paginate, BaseDirection baseDirection,
            PdfStructureElement? element = null)
        {
            double widthAtStart = ctx.FlowWidth;
            while (true)
            {
                double leftAtStart = ctx.FlowLeft;
                y = RenderLinesOnce(ctx, para, runs, bandLeft, avail, y, paginate, baseDirection, element, widthAtStart, out Paragraph? rest);
                if (rest == null) return y;
                bandLeft += ctx.FlowLeft - leftAtStart;
                avail = Math.Max(1, avail + ctx.FlowWidth - widthAtStart);
                widthAtStart = ctx.FlowWidth;
                para = rest;
                runs = rest.Runs;
            }
        }

        /// <summary>
        /// One pass of <see cref="RenderLines"/>: the lines laid out at one width, until they run out or
        /// the flow enters a region of a width other than <paramref name="widthAtStart"/> — then the
        /// lines from there are <paramref name="rest"/>, not drawn, and the answer is where they start.
        /// </summary>
        private static double RenderLinesOnce(Ctx ctx, Paragraph para, IList<IInlineElement> runs,
            double bandLeft, double avail, double y, bool paginate, BaseDirection baseDirection,
            PdfStructureElement? element, double widthAtStart, out Paragraph? rest)
        {
            rest = null;
            if (runs.Count == 0) return y;

            // Floats make a line's width depend on where it lands, and where it lands depends on
            // the heights of the lines above it, which depend on their widths. Resolved by
            // iteration: lay out, walk the heights to find each line's y, ask the context what is
            // free there, and lay out again until nothing moves. Two passes settle the ordinary
            // case; the cap stops a pathological one oscillating.
            //
            // Skipped entirely when the page has no float — `laid` is then a single call.
            var laid = LayoutBesideFloats(ctx, para, runs, bandLeft, avail, y, baseDirection);

            var lines = laid.Lines;
            // Placement must use the SAME answer the reordering will use, which is why it comes
            // from the resolved paragraph rather than being re-derived from the text.
            bool rtl = laid.Bidi.IsRightToLeft;
            double firstIndent = para.FirstLineIndent > 0 ? para.FirstLineIndent : 0;

            // Orphans and widows: where this page's part of the paragraph must break, planned as the
            // part begins (PlanLineBreak) — at the first line, and again on each page it goes on to.
            bool counted = paginate && (para.Orphans > 1 || para.Widows > 1);
            int plannedIn = -1;
            int breakBefore = -1;
            // A line the flow carried into a content area further right is drawn there.
            double shiftAtStart = ctx.RegionShiftX;

            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                double lineHeight = LineHeightOf(para, line);

                // The line and its notes break together. EnsureSpace is asked for BOTH heights, so
                // a line whose note will not fit moves to the next page before the note is
                // registered — reserving first would raise the bottom limit, push the line
                // over, and strand the note on a page its mark is no longer on.
                if (paginate)
                {
                    if (counted && li == breakBefore) ctx.NewPageContinue();

                    // A note continuing from this page keeps the notes in order: a later mark's note
                    // starts on the page that note ends on.
                    if (line.Notes.Count > 0 && ctx.CarriedNotes.Count > 0 && ctx.Y < ctx.TopStart - 0.01)
                        ctx.NewPageContinue();

                    double notesHeight = FullNotesHeight(ctx, line);
                    // The line fits and its notes do not: a note splits (NoteSplit), its head here,
                    // the rest at the foot of the next page — so the mark and the note's start share a page.
                    NoteSplit? SplitHere(ref double height)
                    {
                        if (line.Notes.Count == 0 || ctx.Y - (lineHeight + height) >= ctx.BottomLimit - 0.01
                            || ctx.Y - lineHeight < ctx.BottomLimit) return null;
                        var cut = new NoteSplit();
                        double take = NotesRoomOnPage(ctx, line, ctx.Y - lineHeight - ctx.BottomLimit, cut);
                        if (take < 0 || cut.Note == null) return null;
                        height = take;
                        return cut;
                    }
                    NoteSplit? split = SplitHere(ref notesHeight);
                    int splitIn = ctx.FlowRegion;

                    ctx.EnsureSpace(lineHeight + notesHeight);
                    // The first line of this page's part — the paragraph's first, or the first
                    // after any break, the plan's or EnsureSpace's own — now has its room: plan
                    // where the part ends. Planned after EnsureSpace, so it starts from a line
                    // that fits.
                    if (counted && plannedIn != ctx.FlowRegion)
                    {
                        breakBefore = PlanLineBreak(ctx, para, lines, li, avail);
                        plannedIn = ctx.FlowRegion;
                    }
                    // The plan may have moved the part to a new page (its orphans), where the note may
                    // fit whole or split elsewhere: the cut is the new page's, never the one left.
                    if (split != null && splitIn != ctx.FlowRegion)
                    {
                        double again = FullNotesHeight(ctx, line);
                        split = SplitHere(ref again);
                    }
                    ctx.ReserveNotes(line.Notes, split);
                    y = ctx.Y;

                    // In a region of another width: the rest is broken again there (RenderLines).
                    if (li > 0 && Math.Abs(ctx.FlowWidth - widthAtStart) > 0.01
                        && ReferenceEquals(runs, para.Runs) && CanSplit(para))
                    {
                        rest = SplitRunsAtLines(para, lines, new[] { li })[1];
                        return y;
                    }
                }

                y -= lineHeight;

                // The destination is declared on the FIRST line, and after EnsureSpace rather than
                // before the loop: a paragraph that breaks across a page boundary starts on the
                // page EnsureSpace moved to, and an anchor recorded earlier would name the page the
                // paragraph was pushed off. It is also the reason this sits here rather than at the
                // caller, which does not know where the first line landed.
                //
                // The top of the line, not its baseline: a destination scrolls its target to the
                // top of the window, and landing on the baseline would clip the first line's
                // ascenders off the top of the view.
                if (li == 0 && !string.IsNullOrEmpty(para.AnchorName))
                    ctx.Page.AddAnchor(para.AnchorName, (y + lineHeight) * ctx.Scale);

                // A heading's page, recorded where its destination is -- on the FIRST line and
                // after EnsureSpace, so it names the page the heading actually starts on rather
                // than the one it was pushed off. Written on both passes: the first fills the map
                // the second reads, and the second re-writing the same values costs nothing and
                // keeps the two from disagreeing if a heading ever did move. Recorded as the text
                // the section displays, because the format is the section's and this is where the
                // section is known. LevelOf places a bare H too, so its entry gets a number.
                if (li == 0 && ctx.TocPages != null && TocEntries.LevelOf(para.Role) > 0)
                    ctx.TocPages[para] = ctx.PageNumberText();

                bool lastLine = li == lines.Count - 1;
                // First-line indent: shift the start (LTR) / shrink the band (RTL puts the
                // indent on the right) for line 0 only. Layout already wrapped line 0 narrower.
                double lineBand = paginate ? bandLeft + ctx.RegionShiftX - shiftAtStart : bandLeft;
                double lineLeft = lineBand, lineAvail = avail;

                // Placement must agree with the breaking, which is why this asks the same question
                // at the same y rather than reusing the array: EnsureSpace above may have moved
                // this line to a new page, where the floats are different (usually none). Asking
                // again is what keeps a paginated paragraph from being placed against the previous
                // page's exclusions.
                if (ctx.HasFloats)
                    (lineLeft, lineAvail) = ctx.GetLineSegment(lineBand, avail, y + lineHeight, lineHeight);

                if (li == 0 && firstIndent > 0)
                {
                    lineAvail = Math.Max(1, lineAvail - firstIndent);
                    if (!rtl) lineLeft += firstIndent;
                }
                // y is the line's bottom. The line keeps its height either way; only where the text
                // sits inside it moves, so nothing measured or paginated changes.
                var (lineAscent, lineDescent) = LineGlyphBox(line);
                double baseline = ctx.Options.HalfLeading ? HalfLeadingBaseline(y, lineHeight, lineAscent, lineDescent) : y;
                EmitLine(ctx, laid, line, lineLeft, lineAvail, baseline, para.Alignment, rtl, lastLine, element);

                if (paginate) ctx.Y = y;

                // An inline page break (Model.PageBreak, DocumentBuilder.AddPageBreak) ends its line
                // AND its page, as the DOCX, HTML and ODT exporters of the same model break it. Only where the caller paginates: a table cell or a running header has no page
                // of its own to end.
                if (paginate && line.PageBreakAfter)
                {
                    ctx.NewPageContinue(wholePage: true);
                    y = ctx.Y;
                }
            }
            return y;
        }

        /// <summary>
        /// Plans where the part of a paragraph beginning at line <paramref name="from"/> breaks, so
        /// that <see cref="Paragraph.Orphans"/> and <see cref="Paragraph.Widows"/> hold: answers the
        /// line that must start the next page, or -1 to break where the lines run out (or not at all).
        /// </summary>
        /// <remarks>
        /// <para>A part runs to the paragraph's end or to a forced break, whichever comes first; the
        /// counts constrain only the break the page makes.</para>
        /// <para><b>Too few lines would stay</b> — fewer than the orphans, or fewer once enough are
        /// sent on for the widows — and the whole part moves to the next page, but only from below
        /// the top of one. A part that already starts a page gains nothing from a new one, and
        /// moving it would never end — so it breaks where the lines run out, as an unconstrained
        /// paragraph does. That test is also what stops the move from repeating: a new page starts
        /// at its top (<c>StartPage</c>), so the plan made there cannot move again.</para>
        /// </remarks>
        private static int PlanLineBreak(Ctx ctx, Paragraph para, IReadOnlyList<Line> lines, int from, double avail)
        {
            int end = from;
            while (end < lines.Count - 1 && !lines[end].PageBreakAfter) end++;
            int part = end - from + 1;

            int fit = LinesThatFit(ctx, para, lines, from, end, avail);
            if (fit >= part) return -1;

            // A part after the page's own break begins a page, so its first fragment keeps the widows
            // too; after a forced break neither count applies.
            bool afterBreak = from > 0 && !lines[from - 1].PageBreakAfter;
            int keep = LinesBeforeBreak(para, part, fit, afterBreak);
            if (keep >= 0) return from + keep;

            if (ctx.Y < ctx.TopStart - 0.01)
            {
                ctx.NewPageContinue();
                return PlanLineBreak(ctx, para, lines, from, avail);
            }
            // Where the lines run out is EnsureSpace's to find, by the rule it applies to each.
            return -1;
        }

        /// <summary>
        /// How many of lines <paramref name="from"/>..<paramref name="end"/> fit on this page, by the
        /// rule <c>EnsureSpace</c> applies to each: its height and its notes', the notes' room
        /// reserved as it goes. Line <paramref name="from"/> fits — the plan is made once
        /// <c>EnsureSpace</c> has found it room, and before its notes are reserved.
        /// </summary>
        private static int LinesThatFit(Ctx ctx, Paragraph para, IReadOnlyList<Line> lines, int from, int end, double avail)
        {
            double y = ctx.Y - LineHeightOf(para, lines[from]);
            double first = NotesTakenHere(ctx, lines[from], y - ctx.BottomLimit);
            // Once a note continues onto the next page, no later mark's note starts on this one.
            bool continuing = first >= 0 && first < FullNotesHeight(ctx, lines[from]) - 1e-9;
            double limit = ctx.BottomLimit + Math.Max(0, first);
            int n = 1;
            for (int i = from + 1; i <= end; i++)
            {
                double height = LineHeightOf(para, lines[i]);
                if (y - height < limit) break;
                double notes = 0;
                if (lines[i].Notes.Count > 0)
                {
                    if (continuing) break;
                    notes = NotesTakenHere(ctx, lines[i], y - height - limit);
                    if (notes < 0) break;
                    continuing = notes < FullNotesHeight(ctx, lines[i]) - 1e-9;
                }
                y -= height;
                limit += notes;
                n++;
            }
            return n;
        }

        /// <summary>
        /// A footnote cut at a page's foot (<see cref="NotesRoomOnPage"/>): the note, the head drawn on
        /// the page its mark is on, the rest carried to the next, and the notes after it on the same line,
        /// carried whole.
        /// </summary>
        internal sealed class NoteSplit
        {
            public Model.Footnote? Note;
            public Paragraph Head = null!, Tail = null!;
            public readonly List<Model.Footnote> After = new();
        }

        /// <summary>A line's notes, each at the page's content width — the width they are drawn at — with its rule gap.</summary>
        private static double FullNotesHeight(Ctx ctx, Line line)
        {
            double notes = 0;
            for (int k = 0; k < line.Notes.Count; k++)
                notes += NoteHeight(ctx, line.Notes[k], ctx.ContentWidth) + FootnoteRuleGapPt;
            return notes;
        }

        /// <summary>How much of the page's foot a line's notes take with <paramref name="room"/> under the line: all of them when they fit, else <see cref="NotesRoomOnPage"/>.</summary>
        private static double NotesTakenHere(Ctx ctx, Line line, double room)
        {
            double full = FullNotesHeight(ctx, line);
            return full <= room + 1e-6 ? full : NotesRoomOnPage(ctx, line, room, null);
        }

        /// <summary>
        /// How much of the page's foot <paramref name="line"/>'s notes take when <paramref name="room"/> is
        /// left under the line and they do not all fit: the notes before the first that does not, whole, and
        /// as many of that note's lines as fit, keeping its orphans and widows — its rest and the notes after
        /// it continue at the foot of the next page (<paramref name="split"/> receives the cut). -1 when that
        /// note can keep no part here — too few lines, or a note that cannot be cut (an image in it) — and
        /// the line moves to the next page with its notes.
        /// </summary>
        private static double NotesRoomOnPage(Ctx ctx, Line line, double room, NoteSplit? split)
        {
            double total = 0;
            for (int n = 0; n < line.Notes.Count; n++)
            {
                double height = NoteHeight(ctx, line.Notes[n], ctx.ContentWidth) + FootnoteRuleGapPt;
                if (total + height <= room + 1e-6) { total += height; continue; }

                var (head, tail) = SplitNoteToFit(ctx, NoteParagraph(ctx, line.Notes[n]),
                                                  room - total - FootnoteRuleGapPt, atLeastOneLine: false);
                if (head == null || tail == null) return -1;
                if (split != null)
                {
                    split.Note = line.Notes[n];
                    split.Head = head;
                    split.Tail = tail;
                    for (int k = n + 1; k < line.Notes.Count; k++) split.After.Add(line.Notes[k]);
                }
                return total + FootnoteRuleGapPt + MeasureParagraph(ctx, head, ctx.ContentWidth);
            }
            return total;
        }

        /// <summary>
        /// A note's paragraph cut so its head, at the page's content width, is no taller than
        /// <paramref name="height"/>: whole lines, keeping its orphans and widows — or, with
        /// <paramref name="atLeastOneLine"/>, at least one line whatever the room, so a continuation always
        /// advances. (head, null) when it all fits; (null, null) when no cut keeps both counts or the
        /// paragraph cannot be cut (<see cref="CanSplit"/>), which with <paramref name="atLeastOneLine"/>
        /// draws it whole.
        /// </summary>
        private static (Paragraph? Head, Paragraph? Tail) SplitNoteToFit(Ctx ctx, Paragraph note, double height, bool atLeastOneLine)
        {
            if (!CanSplit(note)) return atLeastOneLine ? (note, null) : (null, null);
            List<Line> lines = LayoutLines(ctx, note, ctx.ContentWidth);
            double used = note.SpacingBefore;
            int fit = 0;
            for (; fit < lines.Count; fit++)
            {
                double lineHeight = LineHeightOf(note, lines[fit]);
                if (used + lineHeight > height + 1e-6) break;
                used += lineHeight;
            }
            if (fit >= lines.Count) return (note, null);
            int keep = atLeastOneLine ? Math.Max(1, fit) : LinesBeforeBreak(note, lines.Count, fit, afterBreak: false);
            if (keep <= 0) return (null, null);
            if (keep >= lines.Count) return (note, null);
            List<Paragraph> parts = SplitRunsAtLines(note, lines, new[] { keep });
            return (parts[0], parts[1]);
        }

        /// <summary>
        /// How many times line widths are re-derived before the layout is accepted as it stands.
        /// </summary>
        /// <remarks>
        /// <b>Two settles the ordinary case</b> — the first pass learns the heights, the second
        /// uses them — and the third exists only to notice that nothing changed. A float whose
        /// bottom lands exactly on a line boundary can oscillate between two layouts forever; the
        /// cap makes that terminate with one of them rather than hang, which is the same trade
        /// every line-breaker with floats makes.
        /// </remarks>
        private const int MaxFloatLayoutPasses = 3;

        /// <summary>
        /// Least measure, in points, that must remain beside a float for text to flow there.
        /// </summary>
        /// <remarks>
        /// An inch. Below it a "column" is a few characters wide and hyphenless breaking produces
        /// one word per line, which is worse than the whitespace the float was meant to avoid — so
        /// a float leaving less than this is laid out as an ordinary block and the text goes under
        /// it, which is what a reader does with a float as wide as its container.
        /// </remarks>
        private const double MinTextWidthBesideFloatPt = 72;

        /// <summary>
        /// The paragraph laid out for the floats about it, from <paramref name="y"/> down: lay out,
        /// walk the heights to find each line's y, ask the context what is free there, and lay out
        /// again until nothing moves. Two passes settle the ordinary case; the cap stops a
        /// pathological one oscillating. With no float on the page it is the one layout it always
        /// was. <see cref="RenderLines"/> draws what this lays out and <see cref="MeasureParagraphAt"/>
        /// measures it, so the two agree.
        /// </summary>
        private static LaidOutParagraph LayoutBesideFloats(Ctx ctx, Paragraph para, IList<IInlineElement> runs,
            double bandLeft, double avail, double y, BaseDirection baseDirection)
        {
            var laid = LayoutParagraph(ctx, para, runs, avail, baseDirection);
            double[]? lineWidths = null;

            if (ctx.HasFloats)
            {
                for (int pass = 0; pass < MaxFloatLayoutPasses; pass++)
                {
                    double[] next = ResolveLineWidths(ctx, para, laid, bandLeft, avail, y);
                    if (SameWidths(lineWidths, next)) break;

                    lineWidths = next;
                    laid = LayoutParagraph(ctx, para, runs, avail, baseDirection,
                                           i => i < next.Length ? next[i] : avail);
                }
            }
            return laid;
        }

        /// <summary>
        /// The width available to each line of <paramref name="laid"/>, given where the lines fall
        /// when placed from <paramref name="startY"/>.
        /// </summary>
        /// <remarks>
        /// Pagination is deliberately not simulated here. A paragraph that breaks across a page
        /// gets widths derived from this page's floats for its later lines, and placement corrects
        /// the geometry by asking the context again after <c>EnsureSpace</c> — so those lines are
        /// placed correctly but may have been BROKEN slightly narrow. Narrow-but-correct is the
        /// recoverable direction, and it is the case a caller is least likely to meet: a float and
        /// the text beside it are normally on one page.
        /// </remarks>
        private static double[] ResolveLineWidths(
            Ctx ctx, Paragraph para, LaidOutParagraph laid, double bandLeft, double avail, double startY)
        {
            var widths = new double[laid.Lines.Count];
            double y = startY;

            for (int i = 0; i < laid.Lines.Count; i++)
            {
                double lineHeight = LineHeightOf(para, laid.Lines[i]);
                widths[i] = ctx.GetLineSegment(bandLeft, avail, y, lineHeight).width;
                y -= lineHeight;
            }

            return widths;
        }

        /// <summary>Whether two width vectors agree, so the iteration can stop.</summary>
        /// <remarks>
        /// A tolerance rather than equality: the widths come from subtracting float edges from a
        /// band, and two passes that produce visually identical layouts can differ in the last
        /// bits. Exact comparison would spend every pass of the cap without changing a line.
        /// </remarks>
        private static bool SameWidths(double[]? a, double[]? b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 0.01) return false;
            return true;
        }

        /// <summary>
        /// Draws an image as a block in the band, scaled to fit its width and placed by the
        /// paragraph's alignment — the rule its text lines follow (<see cref="ResolveBlockLeft"/>).
        /// </summary>
        /// <remarks>
        /// It was always drawn at the band's left edge: in a right-to-left page every photo and
        /// signature box sat on the side opposite the text it belonged to, where a browser puts it
        /// at the start — the right.
        /// </remarks>
        private static double DrawImageBlock(Ctx ctx, ImageRun img, double bandLeft, double avail,
            double y, bool paginate, TextAlignment align, bool rtl)
        {
            if (!OccupiesSpace(img)) return y;
            var (outerW, outerH) = ImageBlockSize(img, avail);
            double x = ResolveBlockLeft(align, rtl, bandLeft, avail, outerW);
            var (fl, fr, ft, fb) = ImageFrame(img);
            double w = outerW - fl - fr, h = outerH - ft - fb;
            if (paginate)
            {
                // Into the content area the break reached, when it reached one further right.
                double shift = ctx.RegionShiftX;
                ctx.EnsureSpace(outerH);
                x += ctx.RegionShiftX - shift;
                y = ctx.Y;
            }

            if (img.Box != null)
                using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                    PaintInlineBox(ctx, img.Box, x, y - outerH, x + outerW, y);

            // A blank box — a canvas nothing drew — is the frame alone: no figure, no decode.
            if (img.Drawing == null && (img.Data == null || img.Data.Length == 0))
            {
                y -= outerH + ImageBlockGapPt;
                if (paginate) ctx.Y = y;
                return y;
            }
            x += fl;
            double top = y - ft;

            // A decorative image (HTML alt="") is page furniture and is marked as such; anything
            // else is a /Figure carrying its /Alt. An image with NEITHER stays a figure with no
            // alternative text, which a checker correctly reports — the honest outcome, rather
            // than inventing a description.
            //
            // The element is created BEFORE the decode attempt, and the fallback alt text is drawn
            // inside the same scope. Drawn outside any marked sequence, the substitute for the image
            // would be unmarked ink on a tagged document — a PDF/UA violation in its own right — and
            // the alt text the model carried would never reach the structure tree.
            PdfStructureElement? figure = null;
            if (ctx.Tagged && ctx.ArtifactType == null && !img.IsDecorative)
            {
                figure = ctx.NewElement(PdfStructureTypes.Figure);
                if (figure != null && !string.IsNullOrEmpty(img.Alt)) figure.Alt = img.Alt;
            }

            // A picture of paths (ImageRun.Drawing) in place of pixels: nothing to decode.
            if (img.Drawing != null)
            {
                using (img.IsDecorative ? ctx.MarkArtifact(PdfArtifactTypes.Layout) : ctx.Mark(figure))
                    DrawVector(ctx, img.Drawing, x, top, w, h, img.WidthPt, img.HeightPt);
                y -= outerH + ImageBlockGapPt;
                if (paginate) ctx.Y = y;
                return y;
            }

            var dec = DecodeOnce(ctx, img.Data);

            // The size rule every producer's image answers to, asked here so an oversized picture
            // leaves its gap and its alt text like an undecodable one, rather than aborting the
            // export at the writer, which refuses the same size.
            string? oversize = dec == null ? null : AuthoredImageSize.Refusal(dec.Width, dec.Height);
            using (var imgScope = img.IsDecorative
                ? ctx.MarkArtifact(PdfArtifactTypes.Layout)
                : ctx.Mark(figure))
            {
                if (dec != null && oversize == null)
                {
                    string res = AddDecodedImage(ctx.Page, dec);
                    // A rounded box clips its picture to the inner curve of its content box, as a
                    // browser clips a replaced element (.img-circle, .img-rounded).
                    Corners clip = img.Box is { } frame
                        ? InlineBoxCorners(frame, outerW, outerH).Inset(fl, ft, fr, fb)
                        : default;
                    if (!clip.IsSquare)
                    {
                        ctx.Page.SaveState();
                        AppendBoxPath(ctx, x, top - h, w, h, clip);
                        ctx.Page.ClipNonzero();
                    }
                    DrawOrientedImage(ctx, res, img.Orientation, x, top - h, w, h);
                    if (!clip.IsSquare) ctx.Page.RestoreState();
                }
                else
                {
                    // The space is reserved either way (the layout above already committed to it),
                    // so an undecodable image leaves a gap. Filling that gap with the alt text is
                    // what a browser does, and it turns "a mysterious blank rectangle" into "this
                    // is what should have been here" — which is the whole difference between a
                    // reader being confused and a reader being informed.
                    // Captured and tested in place, NOT via a `bool hasAlt` flag: a bool standing in
                    // for a null check carries no narrowing, so the call below stays a possibly-null
                    // argument. (string.IsNullOrEmpty is no use either — it has no
                    // [NotNullWhen(false)] on the netstandard2.0 leg this assembly also builds for.)
                    string? alt = img.Alt;

                    BuilderDiagnostics.Report(BuilderDiagnosticKind.ImageDecodeFailed,
                        (oversize ?? $"an image of {img.Data.Length:N0} bytes could not be decoded") +
                        (alt != null && alt.Length > 0 ? $" (alt: '{alt}')" : ""));
                    if (alt != null && alt.Length > 0) DrawAltText(ctx, alt, x, top, w, h);
                }
            }
            y -= outerH + ImageBlockGapPt;
            if (paginate) ctx.Y = y;
            return y;
        }

        /// <summary>
        /// <paramref name="data"/> decoded, once an export (<see cref="Ctx.DecodedImages"/>): a 3 KB DOCX naming one
        /// 1000 × 1000 PNG two hundred times took 20 s decoding and compressing it each time.
        /// </summary>
        private static DecodedImage? DecodeOnce(Ctx ctx, byte[] data)
        {
            if (data == null) return null;
            if (ctx.DecodedImages.TryGetValue(data, out DecodedImage? dec)) return dec;
            // Each picture decodes once, and the export's pictures together are held to one budget of pixels: a
            // picture past it is not drawn (its alt text stands in), and the cut is said once.
            dec = ctx.ImagePixelsLeft > 0 ? BuilderImageDecoder.Decode(data) : null;
            // A picture the authored-image size refuses is never drawn and is reported by that rule; it spends nothing.
            if (dec != null && AuthoredImageSize.Refusal(dec.Width, dec.Height) == null)
            {
                ctx.ImagePixelsLeft -= (long)dec.Width * dec.Height;
                if (ctx.ImagePixelsLeft < 0)
                {
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.ImageDecodeFailed,
                        $"the export's pictures decode to more than {PdfLimits.MaxImagePixelsPerExport:N0} pixels " +
                        "(PdfLimits.MaxImagePixelsPerExport); the pictures past it are not drawn");
                    dec = null;
                }
            }
            ctx.DecodedImages[data] = dec;
            return dec;
        }

        /// <summary>
        /// Registers a decoded image on the page: a JPEG 2000 file through the entry that leaves
        /// <c>/ColorSpace</c> and <c>/BitsPerComponent</c> to the file and writes <c>/SMaskInData</c>,
        /// everything else through the general one. Both call sites — the background and the inline
        /// image — go through here, so the two cannot differ on which entry a filter takes.
        /// </summary>
        private static string AddDecodedImage(PdfContentStreamBuilder page, DecodedImage dec) =>
            dec.Filter == PdfFilterNames.JpxDecode
                ? page.AddJpxImage(dec.Data, dec.Width, dec.Height, dec.ColorSpace, dec.BitsPerComponent, dec.SMaskInData)
                : page.AddImage(dec.Data, dec.Width, dec.Height, dec.Filter, dec.ColorSpace, dec.BitsPerComponent, dec.Decode, dec.SoftMask);

        /// <summary>
        /// Draws an image turned by <paramref name="orientation"/> into the box at
        /// (<paramref name="x"/>, <paramref name="y"/>), <paramref name="w"/> × <paramref name="h"/>
        /// as shown: one <c>cm</c> maps the image's unit square onto the box, turned or mirrored.
        /// </summary>
        /// <remarks>
        /// Each matrix sends the stored image's top-left corner — unit-square (0, 1) — to where the
        /// orientation puts it: 6 to the top right, 8 to the bottom left, 3 to the bottom right,
        /// and so on (Exif 2.3, tag 274). The box is already the shown shape, so a quarter turn's
        /// <paramref name="w"/> spans the stored height. <see cref="ImageOrientation.Normal"/>, and
        /// any value outside the eight, is <c>w 0 0 h x y</c> — the operators
        /// <c>DrawImage</c> writes, byte for byte.
        /// </remarks>
        private static void DrawOrientedImage(Ctx ctx, string res, ImageOrientation orientation,
            double x, double y, double w, double h)
        {
            var (a, b, c, d, e, f) = orientation switch
            {
                ImageOrientation.FlipHorizontal => (-w, 0.0, 0.0, h, x + w, y),
                ImageOrientation.Rotate180 => (-w, 0.0, 0.0, -h, x + w, y + h),
                ImageOrientation.FlipVertical => (w, 0.0, 0.0, -h, x, y + h),
                ImageOrientation.Transpose => (0.0, -h, -w, 0.0, x + w, y + h),
                ImageOrientation.Rotate90Clockwise => (0.0, -h, w, 0.0, x, y + h),
                ImageOrientation.Transverse => (0.0, h, w, 0.0, x, y),
                ImageOrientation.Rotate90Anticlockwise => (0.0, h, -w, 0.0, x + w, y),
                _ => (w, 0.0, 0.0, h, x, y),
            };
            ctx.Page.SaveState().Transform(a, b, c, d, e, f);
            ctx.Page.AppendRaw("/" + res + " " + PdfOperators.InvokeXObject + "\n");
            ctx.Page.RestoreState();
        }

        /// <summary>Vertical gap left below a block image.</summary>
        private const double ImageBlockGapPt = 2;

        /// <summary>
        /// The height a block image occupies in <paramref name="avail"/> — the single rule that
        /// <see cref="DrawImageBlock"/> and <see cref="MeasureParagraph"/> must both use. Two
        /// copies of this arithmetic is how a row gets sized from one number and filled with
        /// another.
        /// </summary>
        private static double ImageBlockHeight(ImageRun img, double avail) => ImageBlockSize(img, avail).Height;

        /// <summary>
        /// Whether an image run takes room: a picture to draw, or a frame to reserve (a canvas
        /// nothing drew) — at a positive size either way.
        /// </summary>
        private static bool OccupiesSpace(ImageRun? img) =>
            img != null && img.WidthPt > 0 && img.HeightPt > 0 &&
            ((img.Data != null && img.Data.Length > 0) || img.Box != null || img.Drawing != null);

        /// <summary>
        /// Draws <paramref name="drawing"/>'s path in the box at <paramref name="x"/>, <paramref name="top"/>,
        /// <paramref name="w"/> × <paramref name="h"/> — its own <paramref name="ownW"/> × <paramref name="ownH"/>
        /// scaled to the box when the band shrank it, the stroke by the smaller scale — filled, stroked or
        /// both in one paint, so an open path fills to its chord and strokes its curve alone. Nothing for
        /// an empty path or one with neither paint.
        /// </summary>
        private static void DrawVector(Ctx ctx, VectorDrawing drawing, double x, double top, double w, double h, double ownW, double ownH)
        {
            bool stroke = drawing.StrokeColor != null && drawing.StrokeWidthPt > 0, fill = drawing.FillColor != null;
            if (drawing.Path.Count == 0 || (!stroke && !fill)) return;
            double sx = ownW > 0 ? w / ownW : 1, sy = ownH > 0 ? h / ownH : 1;
            var page = ctx.Page;
            page.SaveState();
            if (fill)
            {
                var (r, g, b) = ParseColorD(drawing.FillColor!);
                page.SetFillColorRgb((float)r, (float)g, (float)b);
            }
            if (stroke)
            {
                var (r, g, b) = ParseColorD(drawing.StrokeColor!);
                page.SetStrokeColorRgb((float)r, (float)g, (float)b);
                page.SetLineWidth((float)(drawing.StrokeWidthPt * Math.Min(sx, sy)));
            }
            foreach (PathCommand c in drawing.Path)
            {
                switch (c.Verb)
                {
                    case PathVerb.MoveTo: page.MoveTo(x + c.X * sx, top - c.Y * sy); break;
                    case PathVerb.LineTo: page.LineTo(x + c.X * sx, top - c.Y * sy); break;
                    case PathVerb.CurveTo:
                        page.CurveTo(x + c.C1X * sx, top - c.C1Y * sy, x + c.C2X * sx, top - c.C2Y * sy, x + c.X * sx, top - c.Y * sy);
                        break;
                    case PathVerb.Close: page.ClosePath(); break;
                }
            }
            if (fill && stroke) page.FillAndStroke();
            else if (fill) page.Fill();
            else page.Stroke();
            page.RestoreState();
        }

        /// <summary>The room <see cref="ImageRun.Box"/> takes on each side of the picture: padding plus border.</summary>
        private static (double Left, double Right, double Top, double Bottom) ImageFrame(ImageRun img)
        {
            if (img.Box is not { } box) return (0, 0, 0, 0);
            double bw = BoxBorderWidth(box);
            return (box.PaddingLeft + bw, box.PaddingRight + bw, box.PaddingTop + bw, box.PaddingBottom + bw);
        }

        /// <summary>
        /// The outer size of a block image in <paramref name="avail"/>, frame included — the one
        /// rule drawing and measuring share. Too wide, the picture shrinks (aspect kept) and the
        /// frame does not, as a browser's <c>max-width: 100%</c> image does.
        /// </summary>
        private static (double Width, double Height) ImageBlockSize(ImageRun img, double avail)
        {
            if (!OccupiesSpace(img)) return (0, 0);
            var (fl, fr, ft, fb) = ImageFrame(img);
            double w = img.WidthPt, h = img.HeightPt;
            double room = avail - fl - fr;
            if (w > room) { double s = Math.Max(0, room) / w; w *= s; h *= s; }
            return (w + fl + fr, h + ft + fb);
        }

        // Draws an undecodable image's alt text inside the box that was reserved for it, clipped
        // so a long string cannot spill over the surrounding content.
        private static void DrawAltText(Ctx ctx, string alt, double x, double y, double w, double h)
        {
            const double AltFontSize = 9;
            double size = Math.Min(AltFontSize, Math.Max(6, h * 0.6));
            string res = ctx.StandardFont(false, true, StdFamily.Sans);
            if (string.IsNullOrEmpty(res)) return;

            ctx.Page.SaveState();
            ctx.Page.Rectangle(x, y - h, w, h).ClipNonzero();
            ctx.Page.SetFillColorRgb(0.4f, 0.4f, 0.4f);
            ctx.Page.BeginText();
            ctx.Page.SetFont(res, (float)size).SetTextMatrix(1, 0, 0, 1, x + 2, y - h / 2 - size / 3).ShowText(alt);
            ctx.Page.EndText();
            ctx.Page.RestoreState();
        }

        // Measures the vertical space a paragraph would consume in the given width,
        // without drawing (used by the table renderer to size rows before drawing).
        // ================================================================ footnotes

        /// <summary>Thickness of the rule separating the notes from the text above them.</summary>
        private const float FootnoteRuleWidthPt = 0.5f;

        /// <summary>How far across the text column the rule runs — a third, as convention has it.</summary>
        private const double FootnoteRuleWidthFraction = 1.0 / 3.0;

        /// <summary>Gap above and below the rule.</summary>
        private const double FootnoteRuleGapPt = 6;

        /// <summary>Point size a note is set in when its own runs do not say.</summary>
        /// <remarks>
        /// Notes are conventionally smaller than the text they annotate, and a note that is not
        /// visibly smaller reads as body copy that has fallen to the bottom of the page.
        /// </remarks>
        private const double FootnoteSizePt = 8;

        /// <summary>The paragraph a note is drawn as: its mark, then its own content.</summary>
        /// <remarks>
        /// <b>Built rather than stored, so a note is laid out by the same code as everything
        /// else.</b> Wrapping, bidi, tabs, fonts and tagging all come free, and a note that wrapped
        /// differently from a paragraph of the same text would be a second layout engine nobody
        /// asked for.
        /// </remarks>
        private static Paragraph NoteParagraph(Ctx ctx, Model.Footnote note)
        {
            // Two lines each side of a page break when a note splits (NotesRoomOnPage), as Word's
            // widow and orphan control keeps a footnote by default: the model gives a note no counts
            // of its own, and the paragraph default of one sets no limit.
            var paragraph = new Paragraph { SpacingBefore = 0, SpacingAfter = 0, Orphans = 2, Widows = 2 };

            paragraph.Runs.Add(new TextRun
            {
                Text = ctx.Footnotes.MarkOf(note) + " ",
                FontSize = FootnoteSizePt,
                VerticalAlign = VerticalAlign.Superscript,
            });

            foreach (IInlineElement run in note.Runs)
            {
                // A note's own footnote is not drawn: a note that annotated itself would reserve
                // room for a note that reserves room, and there is no page long enough. The mark
                // would also be numbered by a walk that never reached it.
                if (run is Model.Footnote) continue;

                // A run still at the model's default size is set at note size instead — notes
                // are conventionally smaller than the text they annotate, and one that is not
                // visibly smaller reads as body copy that fell to the bottom of the page. A run the
                // caller sized is left alone: they said what they wanted.
                //
                // Cloned through TextRun.Clone rather than copied field by field. A hand-written
                // copy silently drops whatever property is added next — there are eighteen, and
                // the run beside this one carries links, small caps and a baseline shift.
                if (run is TextRun text
                    && System.Math.Abs(text.FontSize - DocDefaults.FontSizePt) < 0.001)
                {
                    TextRun smaller = text.Clone();
                    smaller.FontSize = FootnoteSizePt;
                    paragraph.Runs.Add(smaller);
                    continue;
                }

                paragraph.Runs.Add(run);
            }

            return paragraph;
        }

        /// <summary>How much vertical room one note needs.</summary>
        internal static double NoteHeight(Ctx ctx, Model.Footnote note, double width)
            => MeasureParagraph(ctx, NoteParagraph(ctx, note), width);

        /// <summary>Draws one note and returns the Y beneath it.</summary>
        /// <remarks>
        /// <c>paginate: false</c>: a note is drawn into room already reserved for it, so it must
        /// not itself break the page — a break here would recurse into the very page-close
        /// that is drawing it.
        /// </remarks>
        internal static double RenderNoteAt(Ctx ctx, Model.Footnote note,
                                            double x, double y, double width)
            => RenderNotePartAt(ctx, NoteParagraph(ctx, note), x, y, width);

        /// <summary>
        /// Draws one note, or the part of one a page holds (a split note's head or rest), in its
        /// <c>/Note</c>; returns the Y beneath it. A note split across pages is one <c>/Note</c>, its
        /// content on each page it reaches (<see cref="Ctx.NoteElement"/>).
        /// </summary>
        internal static double RenderNotePartAt(Ctx ctx, Paragraph part, double x, double y, double width)
        {
            PdfStructureElement? element = ctx.NoteElement(part);

            using (ctx.EnterParent(element))
                return RenderParagraphAt(ctx, part, x, y, width, paginate: false);
        }

        /// <summary>
        /// Draws the document's endnotes (<see cref="FootnoteNumbering.Endnotes"/>) where the last
        /// section's content ended: a rule, then each note as a paragraph that paginates like body text.
        /// </summary>
        /// <remarks>
        /// <para><b>Flowed, not reserved.</b> A footnote has a strip reserved for it on the page its
        /// mark lands on, because the mark and the note must share a page; an endnote has no such
        /// tie, so the notes simply continue the flow — <c>paginate: true</c>, where a footnote is
        /// drawn into room already reserved and must not break the page itself. The rule and its gap
        /// are asked for together with the first note, so a last page with no room for any of it
        /// starts the endnotes on a new page rather than drawing a rule above nothing.</para>
        /// <para>The notes come from the numbering's walk, which saw the document once in order;
        /// layout measures blocks ahead of drawing them and would list a note twice.</para>
        /// </remarks>
        private static void DrawEndnotes(Ctx ctx)
        {
            IReadOnlyList<Model.Footnote> notes = ctx.Footnotes.Endnotes;
            if (notes.Count == 0) return;

            ctx.EnsureSpace(FootnoteRuleGapPt * 2 + NoteHeight(ctx, notes[0], ctx.ContentWidth));

            using (ctx.EnterArtifact(PdfArtifactTypes.Layout, null))
            {
                double top = ctx.Y - FootnoteRuleGapPt;
                ctx.Page.SetStrokeColorGray(0.4f);
                ctx.Page.SetLineWidth(FootnoteRuleWidthPt);
                ctx.Page.MoveTo(ctx.ContentLeft, top);
                ctx.Page.LineTo(ctx.ContentLeft + (ctx.ContentWidth * FootnoteRuleWidthFraction), top);
                ctx.Page.Stroke();
            }
            ctx.Y -= FootnoteRuleGapPt * 2;

            foreach (Model.Footnote note in notes)
            {
                PdfStructureElement? element = ctx.NewElement(PdfStructureTypes.Note);
                using (ctx.EnterParent(element))
                    ctx.Y = RenderParagraphAt(ctx, NoteParagraph(ctx, note), ctx.ContentLeft, ctx.Y, ctx.ContentWidth, paginate: true);
            }
        }

        /// <summary>Inset between a boxed paragraph's text and its shading or border, in points.</summary>
        private const double BoxPadPt = Paragraph.DefaultBoxInsetPt;

        /// <summary>
        /// One side's distance from the text to the box: the paragraph's stated inset, never less
        /// than <see cref="BoxPadPt"/> — unless the paragraph states its insets exactly
        /// (<see cref="Paragraph.BoxInsetIsExact"/>).
        /// </summary>
        private static double BoxInset(Paragraph para, double stated) =>
            para.BoxInsetIsExact ? stated : Math.Max(BoxPadPt, stated);

        /// <summary>
        /// Whether a paragraph paints a box — shading, a background image, a shadow or any border —
        /// around its text, and so is drawn at least <see cref="BoxPadPt"/> wider than its band on each side.
        /// </summary>
        /// <remarks>
        /// The shadow was not counted, so a paragraph whose only decoration was a <c>box-shadow</c>
        /// drew nothing at all: the shadow is painted inside this box's branch, and every test of it
        /// had a background too.
        /// </remarks>
        private static bool HasBox(Paragraph para)
            => !string.IsNullOrEmpty(para.Shading) || HasBackgroundImage(para) || para.Shadow != null || para.BorderTop != null
               || para.BorderBottom != null || para.BorderLeft != null || para.BorderRight != null;

        /// <summary>
        /// A band no line can fill, so laying a paragraph out in it yields its unwrapped lines —
        /// CSS's max-content width.
        /// </summary>
        private const double MaxContentMeasureWidthPt = 1e6;

        /// <summary>
        /// Added to a shrink-to-fit width so the float holds the lines that were measured. The
        /// HTML exporter writes <see cref="Paragraph.FloatWidth"/> to two decimals, and a width
        /// rounded down by a hundredth wraps the last word; half a point is invisible.
        /// </summary>
        internal const double ShrinkToFitSlackPt = 0.5;

        /// <summary>
        /// The width in points <paramref name="paragraph"/> takes when it shrinks to its content
        /// inside a band <paramref name="availableWidth"/> wide — CSS shrink-to-fit: its widest
        /// unwrapped line, capped at the band. Zero when there is nothing to measure.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured by the layout that will draw it</b>, with the fonts this exporter's
        /// options resolve, so a float reserved from the answer holds exactly the lines that were
        /// measured. An estimate from character counts wraps differently from the exporter and
        /// leaves a float either too wide or one word short. This is what lets
        /// <c>HtmlToBuilderMapper</c> give a <c>float</c> with no declared width a real
        /// <see cref="Paragraph.FloatWidth"/> instead of a two-cell band.</para>
        /// <para><b>The answer is the box width</b>: the widest line, the paragraph's indents, and
        /// for a bordered or shaded paragraph the inset its box is drawn with — what
        /// <see cref="Paragraph.FloatWidth"/> reserves and the float renderer subtracts the indents
        /// from again. An image run counts at its own width, as <c>DrawImageBlock</c> draws it.</para>
        /// <para><b>A line wider than the band answers the band.</b> CSS gives such a float the
        /// whole available width and puts the following text below it, and so does this exporter.</para>
        /// </remarks>
        public double MeasureShrinkToFitWidth(Paragraph paragraph, double availableWidth)
            => MeasureContentWidth(paragraph, availableWidth, minContent: false);

        /// <summary>
        /// The narrowest width in points <paramref name="paragraph"/> can take inside a band
        /// <paramref name="availableWidth"/> wide with no line running past it — CSS's min-content
        /// width: its widest unbreakable piece (the longest word), its indents and its box's inset,
        /// capped at the band. Zero when there is nothing to measure. Measured as
        /// <see cref="MeasureShrinkToFitWidth(Paragraph, double)"/> measures, by the layout that will draw it, so a flex
        /// item's automatic minimum size (CSS Flexbox §4.5) is the width its longest word really takes.
        /// </summary>
        public double MeasureMinContentWidth(Paragraph paragraph, double availableWidth)
            => MeasureContentWidth(paragraph, availableWidth, minContent: true);

        /// <summary>
        /// The width in points <paramref name="list"/> takes when it shrinks to its content inside a
        /// band <paramref name="availableWidth"/> wide — CSS shrink-to-fit: its widest item, unwrapped,
        /// at the item's text start (the list's text indent, or the exporter's own marker room), a
        /// nested list at its own edge further in; capped at the band. Zero for a list with nothing
        /// in it. Measured by the layout that draws the list, so a flex item sized from the answer
        /// holds the list it then draws.
        /// </summary>
        public double MeasureShrinkToFitWidth(ListBlock list, double availableWidth)
            => MeasureListWidth(list, availableWidth, minContent: false);

        /// <summary>
        /// The narrowest width in points <paramref name="list"/> can take inside a band
        /// <paramref name="availableWidth"/> wide with no line running past it — CSS's min-content
        /// width: its longest word at its item's text start; capped at the band. Zero for a list with
        /// nothing in it. Measured as <see cref="MeasureShrinkToFitWidth(ListBlock, double)"/> measures.
        /// </summary>
        public double MeasureMinContentWidth(ListBlock list, double availableWidth)
            => MeasureListWidth(list, availableWidth, minContent: true);

        /// <summary>
        /// The width in points <paramref name="table"/> takes when it shrinks to its content inside a
        /// band <paramref name="availableWidth"/> wide — CSS shrink-to-fit: its stated width, else its
        /// cell spacing and each column's unwrapped content (or stated column width, where wider),
        /// capped at the band; a column's content includes its cells' lists and nested tables, each at
        /// its own shrink-to-fit width. Zero for a table with no rows. <c>null</c> when a column has no
        /// content this measures and no stated width — a nested table past
        /// <see cref="PdfLimits.MaxTableNestingDepth"/> or itself unknown, a table of contents — so nothing says how
        /// wide the table is. Measured by the layout that draws the table.
        /// </summary>
        public double? MeasureShrinkToFitWidth(Table table, double availableWidth)
            => MeasureTableWidth(table, availableWidth, minContent: false);

        /// <summary>
        /// The narrowest width in points <paramref name="table"/> can take inside a band
        /// <paramref name="availableWidth"/> wide with no cell's word running past its column — CSS's
        /// min-content width: its stated width, else its cell spacing and each column's longest word (or
        /// stated column width, where wider); capped at the band. Zero and <c>null</c> as
        /// <see cref="MeasureShrinkToFitWidth(Table, double)"/> answers them.
        /// </summary>
        public double? MeasureMinContentWidth(Table table, double availableWidth)
            => MeasureTableWidth(table, availableWidth, minContent: true);

        /// <summary>
        /// The height in points <paramref name="blocks"/> take laid out in a band
        /// <paramref name="availableWidth"/> wide, by the layout that draws them in a table cell:
        /// paragraphs with their spacing and line heights at that width, tables, lists, boxes with
        /// their margins, padding and borders, a float beside the blocks after it; no pagination.
        /// Zero for no blocks or no width. What the HTML mapper balances a multi-column block by.
        /// </summary>
        public double MeasureHeight(IReadOnlyList<IDocElement> blocks, double availableWidth)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            if (!(availableWidth > 0) || blocks.Count == 0) return 0;
            return TableRenderer.MeasureBlocks(MeasureContext(blocks), blocks, availableWidth);
        }

        /// <summary>
        /// Splits <paramref name="paragraph"/> at a line boundary so that its head, laid out in a band
        /// <paramref name="availableWidth"/> wide, is no taller than <paramref name="availableHeight"/>
        /// — its spacing before and as many whole lines as fit, never fewer than its
        /// <see cref="Paragraph.Orphans"/> — and its tail holds the rest, never fewer than its
        /// <see cref="Paragraph.Widows"/> lines: the break a multi-column block makes inside a
        /// paragraph, by the layout that will draw both parts. <c>null</c> when there is
        /// nothing to split: the whole paragraph fits, too few lines would fit or be left, or the
        /// paragraph cannot be split in two (<see cref="CanSplit"/>). Neither part is the paragraph
        /// itself: both are copies (<see cref="Paragraph.CloneWithoutRuns"/>), the head without its
        /// spacing after, the tail without its spacing before or first-line indent, and the run the
        /// boundary falls in cut into two with its formatting.
        /// </summary>
        public (Paragraph Head, Paragraph Tail)? SplitAtHeight(Paragraph paragraph, double availableWidth, double availableHeight)
        {
            if (paragraph == null) throw new ArgumentNullException(nameof(paragraph));
            if (!(availableWidth > 0) || !CanSplit(paragraph)) return null;

            List<Line> lines = LayoutLines(MeasureContext(paragraph), paragraph, availableWidth);
            if (lines.Count < 2) return null;

            // The lines that fit under the height, after the spacing before.
            double used = paragraph.SpacingBefore;
            int fit = 0;
            for (; fit < lines.Count; fit++)
            {
                double lineHeight = LineHeightOf(paragraph, lines[fit]);
                if (used + lineHeight > availableHeight + 1e-6) break;
                used += lineHeight;
            }
            if (fit >= lines.Count) return null;   // the whole paragraph fits: nothing to split

            int keep = LinesBeforeBreak(paragraph, lines.Count, fit, afterBreak: false);
            if (keep < 0) return null;   // too few lines would stay for the orphans, or be left for the widows

            List<Paragraph> parts = SplitRunsAtLines(paragraph, lines, new[] { keep });
            return (parts[0], parts[1]);
        }

        /// <summary>
        /// The fewest lines a fragment of <paramref name="paragraph"/> may hold (CSS 2.1 §13.3.3): its
        /// <see cref="Paragraph.Orphans"/> when the fragment ends at a break (<paramref name="beforeBreak"/>),
        /// its <see cref="Paragraph.Widows"/> when it begins after one (<paramref name="afterBreak"/>),
        /// the greater of the two when it does both — a fragment between two breaks is at the top of
        /// its page or column and at the bottom. The page breaker, <see cref="SplitAtHeight"/> and a
        /// multi-column balance take every minimum here.
        /// </summary>
        public static int MinFragmentLines(Paragraph paragraph, bool afterBreak, bool beforeBreak)
        {
            if (paragraph == null) throw new ArgumentNullException(nameof(paragraph));
            return Math.Max(beforeBreak ? Math.Max(1, paragraph.Orphans) : 1, afterBreak ? Math.Max(1, paragraph.Widows) : 1);
        }

        /// <summary>
        /// Where a part of <paramref name="paragraph"/> <paramref name="lines"/> lines long, of which
        /// the first <paramref name="fit"/> have room, breaks: the lines before the break — as many as
        /// fit, leaving the rest at least the paragraph's widows, and themselves at least its orphans,
        /// or its widows too when the part itself begins after a break (<paramref name="afterBreak"/>);
        /// -1 when no break keeps both (<see cref="MinFragmentLines"/>). The page breaker,
        /// <see cref="SplitAtHeight"/> and a multi-column balance decide every break between a
        /// paragraph's lines by this one rule.
        /// </summary>
        public static int LinesBeforeBreak(Paragraph paragraph, int lines, int fit, bool afterBreak)
        {
            int keep = Math.Min(fit, lines - MinFragmentLines(paragraph, afterBreak: true, beforeBreak: false));
            return keep >= MinFragmentLines(paragraph, afterBreak, beforeBreak: true) ? keep : -1;
        }

        /// <summary>
        /// The distance from the top of <paramref name="paragraph"/>, laid out in a band
        /// <paramref name="availableWidth"/> wide, to its first line's baseline: its spacing before and
        /// the first line's height, less how far above the line's bottom the text sits — on the bottom,
        /// or centred under <see cref="PdfExportOptions.HalfLeading"/>, as the paragraph is drawn. What a
        /// flex line's <c>align-items: baseline</c> lines up. 0 for no width or no lines.
        /// </summary>
        public double MeasureFirstBaseline(Paragraph paragraph, double availableWidth)
        {
            if (paragraph == null) throw new ArgumentNullException(nameof(paragraph));
            if (!(availableWidth > 0)) return 0;
            return FirstBaseline(MeasureContext(paragraph), paragraph, availableWidth) ?? 0;
        }

        /// <summary>
        /// The distance from the top of <paramref name="blocks"/>, laid out as a table cell lays them
        /// out in a band <paramref name="availableWidth"/> wide, to their first baseline (CSS 2.1
        /// §17.5.3, CSS Box Alignment §9.1): under the top margin, border and padding of each box they
        /// open, the first paragraph's (<see cref="MeasureFirstBaseline(Paragraph, double)"/>), a list's
        /// first item's, or a table's first row's — the baseline its <see cref="CellVerticalAlign.Baseline"/>
        /// cells share, else the bottom content edge of its lowest cell (CSS Tables 3 §3.10.2). A
        /// positioned or floated paragraph is out of the flow and passed over. <c>null</c> when nothing
        /// gives one — no blocks, a box closed before any line, a floated box, a table of contents, a
        /// table nested past <see cref="PdfLimits.MaxTableNestingDepth"/> — for the caller to synthesize
        /// one from the box (Flexbox §8.5). What a flex line's <c>align-items: baseline</c> lines up.
        /// </summary>
        public double? MeasureFirstBaseline(IReadOnlyList<IDocElement> blocks, double availableWidth)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            if (!(availableWidth > 0) || blocks.Count == 0) return null;
            return FirstBaseline(MeasureContext(blocks), blocks, availableWidth);
        }

        /// <summary>A paragraph's first baseline from its top in <paramref name="ctx"/>; <c>null</c> for no lines.</summary>
        internal static double? FirstBaseline(Ctx ctx, Paragraph paragraph, double width)
        {
            List<Line> lines = LayoutLines(ctx, paragraph, width);
            if (lines.Count == 0) return null;
            double height = LineHeightOf(paragraph, lines[0]);
            double aboveBottom = 0;
            if (ctx.Options.HalfLeading)
            {
                var (ascent, descent) = LineGlyphBox(lines[0]);
                aboveBottom = HalfLeadingBaseline(0, height, ascent, descent);
            }
            return paragraph.SpacingBefore + height - aboveBottom;
        }

        /// <summary>The reading behind <see cref="MeasureFirstBaseline(IReadOnlyList{IDocElement}, double)"/>, in <paramref name="ctx"/>: a table cell's content and the blocks a measure hands over alike.</summary>
        internal static double? FirstBaseline(Ctx ctx, IEnumerable<IDocElement> blocks, double width)
        {
            double above = 0;
            foreach (IDocElement block in blocks)
            {
                switch (block)
                {
                    case Paragraph p when p.Placement != null || (p.Float != FloatSide.None && p.FloatWidth > 0):
                        continue;   // out of the flow: the next block starts where it would have
                    case Paragraph p:
                        if (FirstBaseline(ctx, p, width) is double line) return above + line;
                        above += MeasureParagraph(ctx, p, width);   // no line: its spacing, and on
                        continue;
                    case BoxStart box when !IsFloated(box):
                        above += box.MarginTop + BoxTopInset(box);
                        width = Math.Max(1, width - BoxHorizontalInsets(box));
                        continue;
                    case ListBlock list:
                        foreach (var (_, body) in ListParagraphs(ctx, list))
                            return FirstBaseline(ctx, body, width) is double item ? above + list.SpacingBefore + item : null;
                        above += list.SpacingBefore + list.SpacingAfter;   // no items
                        continue;
                    case ListItem item:
                        return FirstBaseline(ctx, ListItemParagraph(ctx, item), width) is double itemLine ? above + itemLine : null;
                    case Table table:
                        return TableRenderer.FirstBaseline(ctx, table, width) is double row ? above + row : null;
                    default:
                        return null;   // a box closed before any line, a floated box, a table of contents
                }
            }
            return null;
        }

        /// <summary>
        /// The height of each line <paramref name="paragraph"/> lays out to in a band
        /// <paramref name="availableWidth"/> wide, first line first — what <see cref="SplitAtHeight"/>
        /// counts, measured once so a caller deciding many breaks (a multi-column balance)
        /// can count them itself and cut with <see cref="SplitAtLines"/>. A paragraph's height is its
        /// spacing before and after round their sum. Empty for no width.
        /// </summary>
        public IReadOnlyList<double> MeasureLineHeights(Paragraph paragraph, double availableWidth)
        {
            if (paragraph == null) throw new ArgumentNullException(nameof(paragraph));
            if (!(availableWidth > 0)) return Array.Empty<double>();
            List<Line> lines = LayoutLines(MeasureContext(paragraph), paragraph, availableWidth);
            var heights = new List<double>(lines.Count);
            foreach (Line line in lines) heights.Add(LineHeightOf(paragraph, line));
            return heights;
        }

        /// <summary>
        /// Cuts <paramref name="paragraph"/>, laid out in a band <paramref name="availableWidth"/>
        /// wide, before each line in <paramref name="cutLines"/> (ascending, each inside the line
        /// count) into one part more than there are cuts — from one layout, so the parts' lines are
        /// the ones <see cref="MeasureLineHeights"/> counted. The parts are copies as
        /// <see cref="SplitAtHeight"/> makes them: the first keeps the spacing before and first-line
        /// indent, the last the spacing after, each cut run made two with its formatting. <c>null</c>
        /// when <see cref="CanSplit"/> refuses the paragraph; a cut outside the lines is an
        /// <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        public IReadOnlyList<Paragraph>? SplitAtLines(Paragraph paragraph, double availableWidth, IReadOnlyList<int> cutLines)
        {
            if (paragraph == null) throw new ArgumentNullException(nameof(paragraph));
            if (cutLines == null) throw new ArgumentNullException(nameof(cutLines));
            if (!(availableWidth > 0) || !CanSplit(paragraph)) return null;

            List<Line> lines = LayoutLines(MeasureContext(paragraph), paragraph, availableWidth);
            for (int i = 0; i < cutLines.Count; i++)
                if (cutLines[i] < 1 || cutLines[i] >= lines.Count || (i > 0 && cutLines[i] <= cutLines[i - 1]))
                    throw new ArgumentOutOfRangeException(nameof(cutLines), $"a cut at line {cutLines[i]} of {lines.Count} is outside the paragraph, or out of order");
            return SplitRunsAtLines(paragraph, lines, cutLines);
        }

        /// <summary>The lines <paramref name="paragraph"/> lays out to in a band <paramref name="availableWidth"/> wide, its indents taken off, in <paramref name="ctx"/>.</summary>
        private static List<Line> LayoutLines(Ctx ctx, Paragraph paragraph, double availableWidth)
        {
            var (indentLeft, indentRight) = EffectiveIndents(ctx, paragraph, availableWidth);
            double avail = Math.Max(1, availableWidth - indentLeft - indentRight);
            return LayoutParagraph(ctx, paragraph, paragraph.Runs, avail, ResolveBaseDirection(ctx, paragraph)).Lines;
        }

        /// <summary>
        /// Whether <see cref="SplitAtHeight"/> can split <paramref name="paragraph"/> at all: in the
        /// flow (not floated or placed), not kept together, without a box or a transform (its box
        /// would become two), and holding plain text runs and line breaks alone — no image, footnote,
        /// tab, page break, form field or inline box, whose place in the text a cut could not keep. A
        /// line break is one character of the text a line indexes, so a cut keeps it in its part, or
        /// takes its place where the cut falls on it.
        /// </summary>
        public static bool CanSplit(Paragraph paragraph)
        {
            if (paragraph == null) throw new ArgumentNullException(nameof(paragraph));
            if (paragraph.Float != FloatSide.None || paragraph.Placement != null || paragraph.KeepTogether) return false;
            if (HasBox(paragraph) || paragraph.Transform != null) return false;
            bool text = false;
            foreach (IInlineElement run in paragraph.Runs)
            {
                if (run is LineBreak) continue;
                if (run is not TextRun { FormField: null, Box: null }) return false;
                text = true;
            }
            return text;
        }

        /// <summary>
        /// The parts of <paramref name="paragraph"/> cut before each of <paramref name="lines"/> at
        /// <paramref name="cutLines"/>: each part the runs' text between two lines' starts — the
        /// index into the runs' texts laid end to end, which is what a line indexes — the run a cut
        /// falls in cloned into its two sides, a part's trailing spaces (the break's) gone. A line break
        /// is the one character <c>\n</c> of that text, kept in the part it falls in — the one ending a
        /// part too: a paragraph's last break opens no line of its own, and one before it, an empty
        /// line, needs it to close. The first part keeps the spacing before, the first-line indent and the page break before,
        /// the last the spacing after and the keep-with-next; a part between has neither.
        /// </summary>
        private static List<Paragraph> SplitRunsAtLines(Paragraph paragraph, List<Line> lines, IReadOnlyList<int> cutLines)
        {
            var parts = new List<Paragraph>(cutLines.Count + 1);
            int from = 0;
            for (int c = 0; c <= cutLines.Count; c++)
            {
                int to = c < cutLines.Count ? lines[cutLines[c]].CharStart : int.MaxValue;
                Paragraph part = paragraph.CloneWithoutRuns();
                if (c > 0) { part.SpacingBefore = 0; part.FirstLineIndent = 0; part.PageBreakBefore = false; }
                if (c < cutLines.Count) { part.SpacingAfter = 0; part.KeepWithNext = false; }

                int position = 0;
                foreach (IInlineElement run in paragraph.Runs)
                {
                    if (run is LineBreak)
                    {
                        if (position >= from && position < to) part.Runs.Add(run);
                        position++;
                        continue;
                    }
                    var text = (TextRun)run;
                    int start = Math.Max(from, position), end = Math.Min(to, position + text.Text.Length);
                    if (end > start)
                    {
                        TextRun piece = text.Clone();
                        piece.Text = text.Text.Substring(start - position, end - start);
                        part.Runs.Add(piece);
                    }
                    position += text.Text.Length;
                }
                if (c < cutLines.Count) TrimTrailingSpaces(part);
                parts.Add(part);
                from = to;
            }
            return parts;
        }

        /// <summary>The break's spaces end a part's last run; a run of nothing but them goes. A line break stops the trim: the spaces before it are its line's.</summary>
        private static void TrimTrailingSpaces(Paragraph part)
        {
            for (int i = part.Runs.Count - 1; i >= 0; i--)
            {
                if (part.Runs[i] is not TextRun last) break;
                last.Text = last.Text.TrimEnd(' ');
                if (last.Text.Length > 0) break;
                part.Runs.RemoveAt(i);
            }
        }

        private double MeasureContentWidth(Paragraph paragraph, double availableWidth, bool minContent)
        {
            if (paragraph == null) throw new ArgumentNullException(nameof(paragraph));
            if (!(availableWidth > 0)) return 0;
            return ShrinkToFitWidth(MeasureContext(paragraph), paragraph, availableWidth, minContent);
        }

        /// <summary>
        /// <paramref name="paragraph"/>'s shrink-to-fit width — or its min-content width, with
        /// <paramref name="minContent"/> — in <paramref name="ctx"/>, capped at <paramref name="availableWidth"/>:
        /// the one reading behind <see cref="MeasureShrinkToFitWidth(Paragraph, double)"/> and
        /// <see cref="MeasureMinContentWidth(Paragraph, double)"/>, which measure in a scratch context,
        /// and behind a positioned block anchored at its right edge alone, measured where it is drawn.
        /// Zero when there is nothing to measure.
        /// </summary>
        internal static double ShrinkToFitWidth(Ctx ctx, Paragraph paragraph, double availableWidth, bool minContent)
        {
            double content = minContent ? MinContentWidth(ctx, paragraph) : MaxContentWidth(ctx, paragraph);
            if (content <= 0) return 0;

            double box = content + ShrinkToFitSlackPt;
            // The box's floor on each side, where the inset is not stated exactly; an exact one is
            // already in the indents MaxContentWidth counted.
            if (HasBox(paragraph) && !paragraph.BoxInsetIsExact) box += 2 * BoxPadPt;

            return Math.Min(box, availableWidth);
        }

        private double MeasureListWidth(ListBlock list, double availableWidth, bool minContent)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (!(availableWidth > 0)) return 0;

            double content = ListContentWidth(MeasureContext(list), list, minContent);
            if (content <= 0) return 0;
            return Math.Min(content + ShrinkToFitSlackPt, availableWidth);
        }

        private double? MeasureTableWidth(Table table, double availableWidth, bool minContent)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (!(availableWidth > 0)) return 0;
            return TableRenderer.MeasureWidth(MeasureContext(table), table, availableWidth, minContent);
        }

        /// <summary>
        /// A context to measure <paramref name="element"/> in, around a scratch document: the context
        /// resolves direction, footnote marks and composite-text needs from a Section and a Document,
        /// and the caller has neither yet — the mapper measures while it is still building the model.
        /// Its page exists so the standard fonts a piece is measured against do; it is never written out.
        /// </summary>
        private Ctx MeasureContext(IDocElement element) => MeasureContext(new[] { element });

        private Ctx MeasureContext(IEnumerable<IDocElement> elements)
        {
            var scratch = new Document();
            var section = new Section();
            foreach (IDocElement element in elements) section.Children.Add(element);
            scratch.Sections.Add(section);

            var ctx = new Ctx
            {
                Doc = new PdfDocumentBuilder(),
                Fonts = new BuilderFontResolver(_options),
                Options = _options,
                AnyCompositeText = CollectRequiredCodePoints(scratch).Count > 0,
                Scale = PdfExportOptions.ClampScale(_options.Scale),
                Footnotes = FootnoteNumbering.For(scratch),
                Document = scratch,
                MeasureOnly = true,
            };
            ctx.BeginSection(section);
            return ctx;
        }

        /// <summary>A band too narrow for any two pieces: laid out in it, each starts its own line.</summary>
        private const double MinContentMeasureWidthPt = 1;

        /// <summary>
        /// The narrowest band <paramref name="paragraph"/> can be laid out in with no line running
        /// past it — CSS's min-content width: its widest unbreakable piece, plus its indents. Zero
        /// for a paragraph with no text.
        /// </summary>
        /// <remarks>
        /// Measured by the layout that draws it, so a piece breaks from its neighbour exactly where
        /// drawing it would. Image runs are left out: a block image is drawn at most as wide as
        /// its band and never runs past it.
        /// </remarks>
        internal static double MinContentWidth(Ctx ctx, Paragraph paragraph) =>
            ContentWidth(ctx, paragraph, MinContentMeasureWidthPt, countImages: false);

        /// <summary>
        /// The width <paramref name="paragraph"/> takes with nothing wrapped — CSS's max-content
        /// width: its widest unwrapped line, or its widest image with the image's frame, plus its
        /// indents. Zero when there is nothing to measure.
        /// </summary>
        internal static double MaxContentWidth(Ctx ctx, Paragraph paragraph) =>
            ContentWidth(ctx, paragraph, MaxContentMeasureWidthPt, countImages: true);

        /// <summary>
        /// The widest line of <paramref name="paragraph"/> laid out in a band
        /// <paramref name="measureWidth"/> wide — and, when <paramref name="countImages"/>, its
        /// widest image — plus its indents: min-content, max-content and shrink-to-fit alike.
        /// </summary>
        private static double ContentWidth(Ctx ctx, Paragraph paragraph, double measureWidth, bool countImages)
        {
            var laid = LayoutParagraph(ctx, paragraph, paragraph.Runs, measureWidth,
                                       ResolveBaseDirection(ctx, paragraph));

            double widest = 0;
            foreach (var line in laid.Lines) widest = Math.Max(widest, LineWidthForMeasure(line));
            if (countImages)
                foreach (var run in paragraph.Runs)
                    if (run is ImageRun img && OccupiesSpace(img))
                    {
                        var (fl, fr, _, _) = ImageFrame(img);
                        widest = Math.Max(widest, img.WidthPt + fl + fr);
                    }
            return widest <= 0 ? 0 : widest + Math.Max(0, paragraph.IndentLeft) + Math.Max(0, paragraph.IndentRight);
        }

        /// <summary>
        /// A line's width without a trailing space piece — the rule <see cref="EmitLine"/> aligns
        /// by, so a measured float is no wider than the drawn line.
        /// </summary>
        private static double LineWidthForMeasure(Line line)
        {
            double width = line.Width;
            if (line.Pieces.Count > 0 && line.Pieces[line.Pieces.Count - 1].IsSpace)
                width -= line.Pieces[line.Pieces.Count - 1].Width;
            return width;
        }

        internal static double MeasureParagraph(Ctx ctx, Paragraph para, double width)
        {
            var (indentLeft, indentRight) = EffectiveIndents(ctx, para, width);
            double avail = Math.Max(1, width - indentLeft - indentRight);
            // Measurement never reorders: widths are sums of advances and so are order-invariant.
            // LaidOutParagraph.Bidi stays unresolved here, which is why it is lazy.
            var lines = LayoutParagraph(ctx, para, para.Runs, avail, ResolveBaseDirection(ctx, para)).Lines;
            double h = para.SpacingBefore + para.SpacingAfter;
            foreach (var line in lines)
                h += LineHeightOf(para, line);

            // Images are NOT line content — LayoutParagraph skips them — so their height has to be
            // added here, by the same rule DrawImageBlock uses. Without this the two disagree: a
            // table row is sized from a measurement that excludes its images and then filled with
            // them at full height, so the content overflows the row it was measured into.
            foreach (var run in para.Runs)
                if (run is ImageRun img) h += ImageBlockHeight(img, avail) + ImageBlockGapPt;
            return h;
        }

        /// <summary>
        /// <see cref="MeasureParagraph"/> for a paragraph at a known place, beside the floats in
        /// effect: its lines laid out to the widths the floats leave at each line's own y
        /// (<see cref="LayoutBesideFloats"/>), as <see cref="RenderLines"/> will lay them out from
        /// <paramref name="x"/> and <paramref name="y"/>. With no float it is
        /// <see cref="MeasureParagraph"/>.
        /// </summary>
        internal static double MeasureParagraphAt(Ctx ctx, Paragraph para, double x, double y, double width)
        {
            if (!ctx.HasFloats) return MeasureParagraph(ctx, para, width);
            var (indentLeft, indentRight) = EffectiveIndents(ctx, para, width);
            double avail = Math.Max(1, width - indentLeft - indentRight);
            var laid = LayoutBesideFloats(ctx, para, para.Runs, x + indentLeft, avail, y - para.SpacingBefore, ResolveBaseDirection(ctx, para));
            double h = para.SpacingBefore + para.SpacingAfter;
            foreach (var line in laid.Lines)
                h += LineHeightOf(para, line);
            foreach (var run in para.Runs)
                if (run is ImageRun img) h += ImageBlockHeight(img, avail) + ImageBlockGapPt;
            return h;
        }

        /// <summary>
        /// The height of a paragraph's start — the space before it and the fewest lines that may
        /// end a page, its <see cref="Paragraph.Orphans"/> — which is what must share a page with a
        /// block kept with it.
        /// </summary>
        /// <remarks>
        /// All of it when it is too short to break at all: orphans and widows together need more
        /// lines than it has.
        /// </remarks>
        internal static double MeasureParagraphLead(Ctx ctx, Paragraph para, double width)
        {
            var (indentLeft, indentRight) = EffectiveIndents(ctx, para, width);
            double avail = Math.Max(1, width - indentLeft - indentRight);
            var lines = LayoutParagraph(ctx, para, para.Runs, avail, ResolveBaseDirection(ctx, para)).Lines;
            int orphans = MinFragmentLines(para, afterBreak: false, beforeBreak: true);
            int widows = MinFragmentLines(para, afterBreak: true, beforeBreak: false);
            int lead = (long)orphans + widows > lines.Count ? lines.Count : orphans;
            double h = para.SpacingBefore;
            for (int i = 0; i < lead; i++) h += LineHeightOf(para, lines[i]);
            return h;
        }

        /// <summary>
        /// A paragraph's indents in a band <paramref name="width"/> wide, with its box width
        /// (<see cref="Paragraph.WidthFraction"/>, <see cref="Paragraph.WidthPt"/>) applied: the room
        /// the box leaves goes to the indent on its end side. The one place drawing and every
        /// measure take them from.
        /// </summary>
        /// <remarks>
        /// The box's frame on a side is its inset and half its border — the rest of the indent is
        /// margin — so the box's own width is what is compared with the room, as CSS compares a
        /// block's border box with its container.
        /// </remarks>
        private static (double Left, double Right) EffectiveIndents(Ctx ctx, Paragraph para, double width)
        {
            if (para.WidthFraction is null && para.WidthPt <= 0) return (para.IndentLeft, para.IndentRight);

            bool box = HasBox(para);
            double frameLeft = (box ? BoxInset(para, para.BoxInsetLeft) : para.BoxInsetLeft) + Border.PaintedWidth(para.BorderLeft) / 2;
            double frameRight = (box ? BoxInset(para, para.BoxInsetRight) : para.BoxInsetRight) + Border.PaintedWidth(para.BorderRight) / 2;
            double marginLeft = para.IndentLeft - frameLeft, marginRight = para.IndentRight - frameRight;

            double wanted = (para.WidthFraction ?? 0) * width + para.WidthPt;
            double spare = width - marginLeft - marginRight - wanted;
            if (!(spare > 0)) return (para.IndentLeft, para.IndentRight);

            bool spareOnLeft = (ResolveBaseDirection(ctx, para) == BaseDirection.RightToLeft) != para.WidthAtEnd;
            return spareOnLeft ? (para.IndentLeft + spare, para.IndentRight) : (para.IndentLeft, para.IndentRight + spare);
        }

        // Vertical advance for one laid-out line. Drawing and measuring must agree exactly, or a
        // table row is sized from one number and filled using another.
        // An inline-block's margin box (Line.BoxExtent) makes the line at least that tall, whatever
        // the paragraph's line height: CSS's line box holds every inline-level box it contains.
        private static double LineHeightOf(Paragraph para, Line line)
            => Math.Max(line.BoxExtent,
                para.LineSpacingPt > 0 ? para.LineSpacingPt
              : para.LineHeightFactor > 0 ? (line.MaxRunSize > 0 ? line.MaxRunSize : line.MaxSize) * para.LineHeightFactor
              : Math.Max(MinLineHeightPt, line.MaxSize * DefaultLineHeightFactor));


        /// <summary>
        /// Draws a list, tagging it <c>/L</c> → <c>/LI</c> → <c>/Lbl</c> + <c>/LBody</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>A loose <see cref="ListItem"/> is drawn as a paragraph and tagged <c>/P</c>, and
        /// that stays true.</b> A list of one paragraph is not a list: without a container there is
        /// nothing to be an item <i>of</i>, and inventing an <c>/L</c> around each loose item would
        /// produce a document full of one-item lists — which PDF/UA-1 §7.6 permits and no reader
        /// benefits from.</para>
        ///
        /// <para><b>The marker and the body are drawn as two paragraphs, not one string.</b> That is
        /// what makes <c>/Lbl</c> and <c>/LBody</c> separable — a single drawn line can carry only
        /// one marked-content sequence at this granularity — and it is also what produces a hanging
        /// indent, so a wrapped item's second line aligns with its first rather than with the
        /// marker.</para>
        ///
        /// <para><b>Depth is capped.</b> <see cref="ListItem.Sublist"/> is a public mutable
        /// reference, so an item can be made its own descendant; the cap is the same one
        /// <c>ModelRules</c> applies when flattening.</para>
        /// </remarks>
        private static double RenderList(Ctx ctx, ListBlock list, double x, double y, double width,
                                         int depth = 0, double edge = 0)
        {
            if (list == null || depth > ModelRules.MaxListDepth) return y;

            y -= list.SpacingBefore;
            ctx.Y = y;

            PdfStructureElement? listEl = ctx.ListElement(list, depth);
            using (ctx.EnterParent(listEl))
            {
                for (int i = 0; i < list.Items.Count; i++)
                {
                    ListItem li = list.Items[i];
                    if (li == null) continue;

                    y = RenderListItemOf(ctx, list, i, x, y, width, depth, edge);
                }
            }

            y -= list.SpacingAfter;
            ctx.Y = y;
            return y;
        }

        /// <summary>Draws one item of a list: its label, its body, then any sublist beneath it.</summary>
        private static double RenderListItemOf(Ctx ctx, ListBlock list, int index,
                                               double x, double y, double width, int depth, double edge)
        {
            ListItem li = list.Items[index];
            bool rtl = ListItemIsRtl(ctx, list, li);
            var g = ListItemGeometry(list, index, depth, edge);

            PdfStructureElement? itemEl = ctx.NewElement(PdfStructureTypes.LI);
            if (depth == 0) ctx.FlowListItemDrawn(itemEl);
            using (ctx.EnterParent(itemEl))
            {
                // The label is one line by construction, so its own advance is discarded: the
                // item's height is the body's, and the two start on the same baseline.
                Paragraph? label = ListItemLabel(list, li, rtl, g, tagged: true);
                if (label != null) RenderParagraphAt(ctx, label, x, y, width, paginate: false);

                y = RenderParagraphAt(ctx, ListItemBody(list, li, rtl, g, tagged: true), x, y, width, paginate: true);

                if (li.Sublist != null) y = RenderList(ctx, li.Sublist, x, y, width, depth + 1, g.NestedEdge);
            }

            return y;
        }

        /// <summary>
        /// The paragraphs <paramref name="list"/> is drawn as, in reading order — each item's label
        /// (<c>null</c> for no marker) and body, a nested list's after its item's — placed by
        /// <see cref="ListItemGeometry"/>, untagged (no <c>Lbl</c>/<c>LBody</c> role). What a table
        /// cell draws a list as, flat, and what <see cref="ListContentWidth"/> measures: the one
        /// geometry <see cref="RenderListItemOf"/> draws by, so a list in a cell keeps its stated text
        /// indent (one paragraph per item at the exporter's own indent would lose an HTML list's
        /// <c>padding-left</c> inside a cell) and a flex item measured from the
        /// list holds the list it then draws.
        /// </summary>
        internal static IEnumerable<(Paragraph? Label, Paragraph Body)> ListParagraphs(Ctx ctx, ListBlock list, int depth = 0, double edge = 0)
        {
            if (list == null || depth > ModelRules.MaxListDepth) yield break;
            for (int i = 0; i < list.Items.Count; i++)
            {
                ListItem li = list.Items[i];
                if (li == null) continue;
                bool rtl = ListItemIsRtl(ctx, list, li);
                var g = ListItemGeometry(list, i, depth, edge);
                yield return (ListItemLabel(list, li, rtl, g, tagged: false), ListItemBody(list, li, rtl, g, tagged: false));
                if (li.Sublist != null)
                    foreach (var nested in ListParagraphs(ctx, li.Sublist, depth + 1, g.NestedEdge))
                        yield return nested;
            }
        }

        /// <summary>The paragraph a list item's marker is drawn as, before its text start; <c>null</c> for an item with no marker.</summary>
        private static Paragraph? ListItemLabel(ListBlock list, ListItem li, bool rtl, ListItemPlacement g, bool tagged)
        {
            if (g.Marker.Length == 0) return null;
            var label = new Paragraph
            {
                IndentLeft = rtl ? 0 : g.Indent,
                IndentRight = rtl ? g.Indent : 0,
                Direction = li.Direction ?? list.Direction,
                Alignment = rtl ? TextAlignment.Right : TextAlignment.Left,
                Role = tagged ? PdfStructureTypes.Lbl : null,
                // The body's line height, so the two share a baseline (half-leading puts
                // the baseline by the line's height).
                LineSpacingPt = li.LineSpacingPt,   // the label's
                LineHeightFactor = li.LineHeightFactor,
            };
            label.Runs.Add(CloneStyleRun(li.MarkerFormat ?? li.TextFormat, g.Marker));
            return label;
        }

        /// <summary>Whether a list item of <paramref name="list"/> is laid out right to left: its own direction, else the list's, else the section's; <c>Auto</c> read off its text.</summary>
        private static bool ListItemIsRtl(Ctx ctx, ListBlock list, ListItem li)
        {
            var itemDir = li.Direction ?? list.Direction ?? ctx.Section.Direction;
            return itemDir == TextDirection.RightToLeft
                || (itemDir == TextDirection.Auto
                    && UnicodeBidi.IsRightToLeftParagraph(li.Text ?? "", BaseDirection.Auto));
        }

        /// <summary>
        /// Where a list item's parts sit across its band — from the start side: its marker, the
        /// marker's room, the start of its text, and the edge a nested list hangs from.
        /// </summary>
        private readonly struct ListItemPlacement
        {
            public ListItemPlacement(string marker, double labelWidth, double textStart, double nestedEdge)
            {
                Marker = marker; LabelWidth = labelWidth; TextStart = textStart; NestedEdge = nestedEdge;
            }
            public string Marker { get; }
            /// <summary>The marker's room, at least <see cref="MinListLabelPt"/>.</summary>
            public double LabelWidth { get; }
            /// <summary>Where the item's text starts; the body's indent.</summary>
            public double TextStart { get; }
            /// <summary>Where the marker starts; the label's indent.</summary>
            public double Indent => TextStart - LabelWidth;
            public double NestedEdge { get; }
        }

        /// <summary>
        /// The placement of item <paramref name="index"/> of <paramref name="list"/> at nesting
        /// <paramref name="depth"/>, the list's own edge at <paramref name="edge"/>: the text starts
        /// the list's stated text indent from its edge — the marker just before the text, a nested
        /// list at the item's text, as CSS has them — or, unstated, at the exporter's own (a
        /// marker's width past 18pt a level, a nested list 18pt further). One reading, for drawing
        /// an item and for measuring a list's width (<see cref="ListContentWidth"/>): the two must
        /// agree exactly, or a flex item sized by the measure wraps the list it then draws.
        /// </summary>
        private static ListItemPlacement ListItemGeometry(ListBlock list, int index, int depth, double edge)
        {
            string marker = ModelRules.ListMarker(list, index);
            double labelWidth = Math.Max(MinListLabelPt,
                AdvanceWidth(marker, bold: false, italic: false, size: DefaultListMarkerSizePt));
            double textStart = list.TextIndentPt is double stated
                ? edge + stated
                : depth * ListLevelIndentPt + labelWidth;
            double nestedEdge = list.TextIndentPt.HasValue ? textStart : (depth + 1) * ListLevelIndentPt;
            return new ListItemPlacement(marker, labelWidth, textStart, nestedEdge);
        }

        /// <summary>The paragraph a list item's own text is drawn as: its runs, indented to the item's text start, with the item's spacing and line height.</summary>
        private static Paragraph ListItemBody(ListBlock list, ListItem li, bool rtl, ListItemPlacement g, bool tagged)
        {
            var body = new Paragraph
            {
                IndentLeft = rtl ? 0 : g.TextStart,
                IndentRight = rtl ? g.TextStart : 0,
                Direction = li.Direction ?? list.Direction,
                SpacingAfter = li.SpacingAfter,     // the body's
                LineSpacingPt = li.LineSpacingPt,   // the body's
                LineHeightFactor = li.LineHeightFactor,
                Role = tagged ? PdfStructureTypes.LBody : null,
            };
            foreach (IInlineElement run in ModelRules.ListRuns(li)) body.Runs.Add(run);
            return body;
        }

        /// <summary>
        /// The width <paramref name="list"/>'s content takes, nothing wrapped — or, when
        /// <paramref name="minContent"/>, its widest unbreakable piece — in the band it is drawn in:
        /// the widest of its items' bodies (<see cref="ListParagraphs"/>) at their text starts, which
        /// an item reaches with no text after its marker, and of its nested lists at their edges.
        /// 0 for a list with nothing in it.
        /// </summary>
        internal static double ListContentWidth(Ctx ctx, ListBlock list, bool minContent)
        {
            double widest = 0;
            foreach (var (_, body) in ListParagraphs(ctx, list))
            {
                // The text start is reached whether or not text follows it.
                widest = Math.Max(widest, Math.Max(body.IndentLeft, body.IndentRight));
                widest = Math.Max(widest, minContent ? MinContentWidth(ctx, body) : MaxContentWidth(ctx, body));
            }
            return widest;
        }
        private static double RenderListItem(Ctx ctx, ListItem li, double x, double y, double width, string? role = null)
        {
            Paragraph p = ListItemParagraph(ctx, li);
            p.Role = role;
            return RenderParagraphAt(ctx, p, x, y, width, paginate: true);
        }

        /// <summary>
        /// The structure a run of two or more loose list items (<see cref="ListItem"/>s among a section's
        /// blocks, not in a <see cref="ListBlock"/>) is tagged with: one <c>/L</c>, an <c>/LI</c> per item, an item of a
        /// deeper <see cref="ListItem.Level"/> in an <c>/L</c> inside the item before it, a shallower one
        /// back in its own level's list.
        /// </summary>
        private sealed class LooseListTags
        {
            private readonly List<(int Level, PdfStructureElement? List, PdfStructureElement? LastItem)> _open = new();

            /// <summary>The <c>/LI</c> an item of <paramref name="level"/> is drawn in, made in the list its level opens or continues.</summary>
            public PdfStructureElement? ItemFor(Ctx ctx, int level)
            {
                while (_open.Count > 0 && _open[_open.Count - 1].Level > level) _open.RemoveAt(_open.Count - 1);
                if (_open.Count == 0 || _open[_open.Count - 1].Level < level)
                {
                    PdfStructureElement? list;
                    using (ctx.EnterParent(_open.Count == 0 ? null : _open[_open.Count - 1].LastItem))
                        list = ctx.NewElement(PdfStructureTypes.L);
                    _open.Add((level, list, null));
                }
                var top = _open[_open.Count - 1];
                PdfStructureElement? item;
                using (ctx.EnterParent(top.List)) item = ctx.NewElement(PdfStructureTypes.LI);
                _open[_open.Count - 1] = (top.Level, top.List, item);
                return item;
            }
        }

        /// <summary>
        /// The paragraph a list item is drawn as: its marker, then its content, indented by level.
        /// </summary>
        /// <remarks>
        /// <b>One conversion, because measuring and drawing must agree exactly.</b> A table cell
        /// sizes its row by measuring and then fills it by drawing, and the file's own comment on
        /// <c>LineHeightOf</c> says what happens when the two use different numbers. Building the
        /// paragraph twice — once in a measure path and once in a draw path — is the shape that
        /// guarantees they eventually will.
        /// </remarks>
        internal static Paragraph ListItemParagraph(Ctx ctx, ListItem li)
        {
            // A list marker is placed before any text is laid out, so it cannot consult the
            // resolved paragraph; Auto therefore falls back to the section's own direction here.
            var liDir = li.Direction ?? ctx.Section.Direction;
            bool rtl = liDir == TextDirection.RightToLeft
                || (liDir == TextDirection.Auto && UnicodeBidi.IsRightToLeftParagraph(li.Text ?? "", BaseDirection.Auto));
            var fmt = li.TextFormat;
            // Build an equivalent paragraph: marker + text, with a level indent.
            var p = new Paragraph
            {
                IndentLeft = rtl ? 0 : li.Level * ListLevelIndentPt,
                IndentRight = rtl ? li.Level * ListLevelIndentPt : 0,
                Direction = li.Direction,
                SpacingAfter = li.SpacingAfter,     // a loose or in-cell item's
                LineSpacingPt = li.LineSpacingPt,   // a loose or in-cell item's
                LineHeightFactor = li.LineHeightFactor,
            };
            // The marker and the run selection are ModelRules', not this exporter's, as for the
            // other three exporters. Open-coded here, a branch could go untested: every list
            // assertion in the suite goes through a fixture whose content lives in Runs, so a
            // branch reading Text would be exercised by nothing.
            p.Runs.Add(CloneStyleRun(li.MarkerFormat ?? fmt, ModelRules.ListMarker(li)));

            // The item's content, Text or Runs — AddListItem("first"), the primary public overload,
            // puts it in Text.
            foreach (IInlineElement run in ModelRules.ListRuns(li)) p.Runs.Add(run);
            return p;
        }

        private static TextRun CloneStyleRun(TextRun? fmt, string text)
        {
            var r = fmt != null ? fmt.Clone() : new TextRun();
            r.Text = text;
            return r;
        }

        // ================================================================== line layout

        // A measured, drawable piece of a line.
        internal sealed class Piece
        {
            public bool Unicode;
            public ushort[] Gids = Array.Empty<ushort>();
            /// <summary>Where each of <see cref="Gids"/> is drawn — the font's mark attachment — or <c>null</c>.</summary>
            public IReadOnlyList<GlyphPlacement>? Placements;
            public string Text = "";
            /// <summary>
            /// A standard-font piece's characters in the order they are displayed
            /// (<see cref="ArrangeStandardPieces"/>); <c>null</c> for a piece no bidi pass arranged,
            /// which is drawn as <see cref="Text"/>. <see cref="Text"/> itself is never reordered, so
            /// arranging a line twice gives the same answer.
            /// </summary>
            public string? DisplayText;
            /// <summary>
            /// A kerned standard-font piece's adjustment after each character of <see cref="Text"/>, in
            /// thousandths of an em (<c>Standard14Kerning</c>), counted in its <see cref="Width"/>;
            /// <c>null</c> for none.
            /// </summary>
            public double[]? Kerns;
            public string FontRes = "";
            public float DrawSize;
            public double Rise;
            public double CharSpacing;
            public int Scale = NormalHorizontalScale;   // horizontal text scale, in percent (Tz)
            public float R, G, B;

            /// <summary>
            /// The piece's glyph box as fractions of its size above and below the baseline: its
            /// font's line metrics (<see cref="StandardLineMetrics"/>, <see cref="ProgramLineMetrics"/>),
            /// or the fixed box for a piece with no font.
            /// </summary>
            public double Ascent = GlyphBoxAscent, Descent = GlyphBoxDescent;

            /// <summary>
            /// Effective constant alpha: the paragraph's opacity multiplied by the run's, already
            /// clamped. <c>1</c> for everything that never asked, which is what keeps an ordinary
            /// document free of any <c>gs</c> operator.
            /// </summary>
            public double Alpha = 1.0;
            public double Width;            // advance (incl scale + char spacing)
            public bool IsSpace;            // a breakable inter-word space
            public bool IsTab;
            /// <summary>A tab's leader: like the tab it fills, it ends the word before it (<c>TakeGluedTail</c>).</summary>
            public bool IsLeader;
            /// <summary>The size of the run a text piece came from, 0 for any other: what the line's sizes are recomputed from.</summary>
            public double RunSize;
            /// <summary>How many spaces a committed space piece stands for (its line's justification count).</summary>
            public int SpaceRun;
            /// <summary>Render mode 3: the glyphs take their room and paint nothing; no rules either.</summary>
            public bool Invisible;
            public bool Underline, Strike, DoubleStrike;
            public string Highlight = "";
            public string? LinkUri;

            /// <summary>The form field the piece's run stands for; see <see cref="FormFieldSpec"/>.</summary>
            public FormFieldSpec? FormField;

            /// <summary>The inline box the piece's run is drawn inside; see <see cref="InlineBox"/>.</summary>
            public InlineBox? Box;

            // Where this piece's characters sit in the paragraph's logical text. Bidi is resolved
            // over that whole text once, and the line's visual order is then expressed in these
            // indices — which is what lets pieces be PLACED in bidi order instead of being
            // reversed wholesale.
            public int CharStart;
            public int CharLength;
        }

        internal sealed class Line
        {
            /// <summary>
            /// Footnotes whose reference mark falls on this line.
            /// </summary>
            /// <remarks>
            /// <b>Per line, because that is the unit the page break works in.</b> A paragraph may
            /// straddle a boundary, so knowing which notes it holds is not enough — the note
            /// belongs to whichever page its mark ends up on, and only the line knows that.
            /// </remarks>
            public readonly List<Model.Footnote> Notes = new();

            public readonly List<Piece> Pieces = new();

            /// <summary>
            /// The inline boxes of the pieces added to this line, by reference identity — so
            /// <see cref="BoxRoomOnLine"/> answers "is this piece the box's first on the line?" with
            /// an O(1) lookup rather than scanning <see cref="Pieces"/>. Every append
            /// goes through <c>AddTracked</c>, which keeps it in step with <see cref="Pieces"/>.
            /// </summary>
            public readonly HashSet<InlineBox> Boxes =
                new HashSet<InlineBox>(Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance);

            /// <summary>The line was closed by an inline page break, so the page ends with it.</summary>
            public bool PageBreakAfter;

            public double Width;            // sum of non-trailing-space piece widths
            public int SpaceCount;          // breakable spaces (for justify)
            public double MaxSize = DocDefaults.FontSizePt;
            /// <summary>The largest size of a run on the line, 0 for none — unlike <see cref="MaxSize"/>, not floored at the default size, as CSS's line box is not.</summary>
            public double MaxRunSize;
            /// <summary>The tallest margin box of an <see cref="InlineBox.OccupiesLine"/> box on the line, 0 for none: the least the line is tall.</summary>
            public double BoxExtent;

            // The line's span in the paragraph's logical text. L1 and L2 are per-LINE rules, so
            // reordering needs to know where the line begins and ends — a trailing space resets
            // to the paragraph level only because it is at the end of THIS line.
            public int CharStart;
            public int CharEnd;
        }

        /// <summary>
        /// A wrapped paragraph plus the logical text its pieces index into, and the bidi
        /// resolution of that text — computed lazily, because <see cref="MeasureParagraph"/> also
        /// lays paragraphs out and must not pay for a reordering it never uses.
        /// </summary>
        internal sealed class LaidOutParagraph
        {
            public List<Line> Lines = new();
            public string LogicalText = "";
            public BaseDirection BaseDirection = BaseDirection.LeftToRight;

            private BidiParagraph? _bidi;

            /// <summary>
            /// Resolved lazily and memoised. <c>??=</c> replaces a separate <c>_resolved</c> flag:
            /// <c>ResolveParagraph</c> returns non-null, so the field itself already records whether
            /// the work has been done, and the flag was a second source of truth the compiler could
            /// not tie to the first.
            /// </summary>
            public BidiParagraph Bidi
                => _bidi ??= UnicodeBidi.ResolveParagraph(LogicalText, BaseDirection);
        }

        /// <summary>
        /// Greedy word-wrap of the given runs into lines fitting <paramref name="avail"/>, plus
        /// the logical text and base direction the lines will be reordered against.
        ///
        /// <para><b>Measurement stays in logical order and stays untouched.</b> Line breaking has
        /// to measure before it can choose a break, while L1/L2 are defined per line and can only
        /// run after one. Resolving that is the whole reason bidi is split into "levels for the
        /// paragraph" (here, lazily) and "order for a line" (in <see cref="EmitLine"/>): a piece's
        /// width is a sum of advances, which is order-invariant, so wrapping never needs to know
        /// the visual order. Keep it that way — if width were ever computed after reordering,
        /// <see cref="MeasureParagraph"/> and drawing would size table rows differently.</para>
        /// </summary>
        /// <remarks>
        /// <para><c>widthOfLine</c> gives the width available to the line at a given zero-based
        /// index, and is <see langword="null"/> for the single-width behaviour every caller had
        /// before floats existed. Described here rather than in a <c>param</c> tag because
        /// documenting one parameter obliges all six (CS1573 is an error in this tree).</para>
        ///
        /// <para><b>A width per line INDEX, not per y</b>, because this method has a second caller
        /// that measures a paragraph's height with no render context and therefore no y at all.
        /// The caller that does know where its lines land — <see cref="RenderLines"/> — converts y
        /// to an index by laying out, walking the heights, and laying out again until the widths
        /// stop moving.</para>
        /// </remarks>
        private static LaidOutParagraph LayoutParagraph(Ctx ctx, Paragraph para,
            IList<IInlineElement> runs, double avail, BaseDirection baseDirection,
            Func<int, double>? widthOfLine = null)
        {
            var laid = LayoutParagraphPass(ctx, para, runs, avail, baseDirection, widthOfLine, null);
            HashSet<int>? splits = MixedLevelSplits(laid);
            return splits == null ? laid : LayoutParagraphPass(ctx, para, runs, avail, baseDirection, widthOfLine, splits);
        }

        /// <summary>
        /// The logical indices at which a word must be cut so that each piece holds one bidi level,
        /// or <c>null</c> when every piece already does.
        /// </summary>
        /// <remarks>
        /// <para>A piece is a whitespace-delimited token, and <see cref="VisualPieceOrder"/> places
        /// it as one unit — but a token's characters need not share a level. In a right-to-left
        /// paragraph, <c>Ruehl:</c> at the end of <c>Frank Ruehl: …</c> has its letters in the
        /// left-to-right run and its colon, between that run and the Hebrew, at the paragraph's
        /// level (UAX #9 N1, N2): the colon is displayed on the far side of the whole Latin run, and
        /// the piece, placed where its first displayed character falls, dragged <c>Ruehl</c> across
        /// with it — "RuehlFrank". Cut there, each part is placed on its own.</para>
        /// <para>The levels are the paragraph's, which line breaking does not change (L1 resets only
        /// whitespace, which no token contains), so they can be read off a first layout and the
        /// paragraph laid out again with the cuts: the logical text is the same, so the indices
        /// are. The parts have no space between them, so they break as one word.</para>
        /// </remarks>
        private static HashSet<int>? MixedLevelSplits(LaidOutParagraph laid)
        {
            if (laid.BaseDirection != BaseDirection.RightToLeft && !ContainsRightToLeft(laid.LogicalText)) return null;
            HashSet<int>? splits = null;
            BidiParagraph bidi = laid.Bidi;
            foreach (Line line in laid.Lines)
                foreach (Piece pc in line.Pieces)
                {
                    if (pc.IsSpace || pc.IsTab || pc.CharLength < 2) continue;
                    int end = Math.Min(pc.CharStart + pc.CharLength, laid.LogicalText.Length);
                    for (int i = pc.CharStart + 1; i < end; i++)
                        if (bidi.LevelAt(i) != bidi.LevelAt(i - 1)) (splits ??= new HashSet<int>()).Add(i);
                }
            return splits;
        }

        /// <summary>Whether <paramref name="text"/> has any character that can carry an odd level: the same pre-test <see cref="ContainsMultipleLevels"/> applies to a line.</summary>
        private static bool ContainsRightToLeft(string text)
        {
            foreach (char c in text)
                if (CanCarryOddLevel(c)) return true;
            return false;
        }

        /// <summary>
        /// <paramref name="tokens"/>, the tokens of one run whose first character is logical index
        /// <paramref name="start"/>, with each word cut at the indices in <paramref name="splits"/>
        /// that fall inside it (<see cref="MixedLevelSplits"/>). A field placeholder is never cut:
        /// it is replaced whole (<see cref="TokenizeFields"/>).
        /// </summary>
        private static IEnumerable<(string token, bool isSpace)> SplitAtLevels(
            IEnumerable<(string token, bool isSpace)> tokens, int start, HashSet<int>? splits)
        {
            int at = start;
            foreach (var (token, isSpace) in tokens)
            {
                if (splits == null || isSpace
                    || string.Equals(token, PagePlaceholder, StringComparison.Ordinal)
                    || string.Equals(token, NumPagesPlaceholder, StringComparison.Ordinal))
                {
                    yield return (token, isSpace);
                    at += token.Length;
                    continue;
                }
                int from = 0;
                for (int i = 1; i < token.Length; i++)
                    if (splits.Contains(at + i))
                    {
                        yield return (token.Substring(from, i - from), false);
                        from = i;
                    }
                yield return (token.Substring(from), false);
                at += token.Length;
            }
        }

        /// <summary>One layout pass of <see cref="LayoutParagraph"/>, its words cut at <paramref name="splits"/> when given.</summary>
        private static LaidOutParagraph LayoutParagraphPass(Ctx ctx, Paragraph para,
            IList<IInlineElement> runs, double avail, BaseDirection baseDirection,
            Func<int, double>? widthOfLine, HashSet<int>? splits)
        {
            var laid = new LaidOutParagraph { BaseDirection = baseDirection };
            // Read once here as well as in RenderParagraphAt, because measurement runs through
            // this method with no render context: a piece must carry the same alpha whether it was
            // built to be measured or to be drawn. Inside an opacity group the paragraph is opaque —
            // the group carries its opacity — so the pieces are too.
            // The paragraph whose group this is draws opaque inside it; a paragraph inside a box's
            // group keeps its own (relative) opacity.
            double paraAlpha = ReferenceEquals(ctx.OpacityGroupOwner, para) ? 1.0 : Alpha01(para.Opacity);
            var logical = new System.Text.StringBuilder();
            // The embeddings, overrides and isolates the logical text so far leaves open.
            var controls = new BidiControlStack();
            var lines = laid.Lines;
            var cur = new Line();
            double pendingSpace = 0;
            int pendingSpaceCount = 0;
            // The first pending space piece, kept for its font / size / colour, and the space text
            // itself. Collapsed into a bare width, a run of spaces would leave every word positioned
            // with its own Tm and no space CHARACTER at all — legal, but plain extraction would
            // return "HelloWorld" and the document copy-paste and search badly.
            Piece? pendingSpaceModel = null;
            // A StringBuilder, not `+=`: a paragraph of N single-space runs between inline elements
            // (CollapseSpaces keeps each) committed once at the end, so `pendingSpaceText += sp.Text`
            // copied Σk ≈ N²/2 characters — tens of GB for 200 000 runs, per layout pass.
            var pendingSpaceText = new System.Text.StringBuilder();
            // First-line indent narrows the first line's available width; subsequent lines
            // use the full band. Guarded on >0 so unindented paragraphs are unaffected.
            double firstIndent = para.FirstLineIndent > 0 ? para.FirstLineIndent : 0;
            // The band this line may use. `widthOfLine` is null on every path that existed before
            // floats, so the expression collapses to exactly what it was.
            double LineAvail()
            {
                double band = widthOfLine == null ? avail : Math.Max(1, widthOfLine(lines.Count));
                return lines.Count == 0 && firstIndent > 0 ? Math.Max(1, band - firstIndent) : band;
            }

            // `splitAt` is the logical index the break falls at. It defaults to "everything appended
            // so far", which is right for a forced break — but NOT for a wrap, where the token that
            // triggered the break has already been appended and belongs to the NEW line. Closing at
            // logical.Length there put that token inside the closed line's [CharStart, CharEnd) and
            // started the new line after it, so VisualPieceOrder resolved the new line's order over
            // a range that excluded the piece opening it: the piece was not found in `order` and
            // fell through to the "there should be none" tail, which appends at the END of the
            // display order. An LTR line beginning with a Latin word and containing Hebrew drew that
            // word at the far right instead of the left margin.
            void Flush(int splitAt = -1)
            {
                int at = splitAt >= 0 ? splitAt : logical.Length;
                // Trim a trailing pending space; commit the line.
                cur.CharEnd = at;
                lines.Add(cur);
                cur = new Line { CharStart = at };
                pendingSpace = 0; pendingSpaceCount = 0;
                pendingSpaceModel = null; pendingSpaceText.Clear();
            }
            void AddSpace(Piece sp)
            {
                pendingSpace += sp.Width;
                pendingSpaceCount++;
                // BuildPiece already gave this piece the originating run's font, size and colour —
                // keep them so the space can be drawn as a real character rather than discarded.
                pendingSpaceModel ??= sp;
                pendingSpaceText.Append(sp.Text);
            }
            void CommitPendingSpace()
            {
                if (pendingSpaceCount == 0) return;
                // Reuse the first space piece rather than fabricating a fontless one; BuildPiece
                // returns a fresh instance per token, so mutating it here is safe.
                var sp = pendingSpaceModel ?? new Piece { Text = " ", Alpha = paraAlpha };
                sp.IsSpace = true;
                sp.Width = pendingSpace;
                sp.SpaceRun = pendingSpaceCount;
                // A shaped (Unicode) space carries GIDs that match only its own token, so the
                // concatenated text applies to the simple-font path alone.
                if (!sp.Unicode && pendingSpaceText.Length > 0) sp.Text = pendingSpaceText.ToString();
                AddTracked(cur, sp);
                cur.Width += pendingSpace;
                cur.SpaceCount += pendingSpaceCount;
                pendingSpace = 0; pendingSpaceCount = 0;
                pendingSpaceModel = null; pendingSpaceText.Clear();
            }

            // The run a tab leader borrows its font from. A tab carries no formatting of its own,
            // and a leader has to be measured in some font to know how many characters fit.
            TextRun? lastTextRun = null;

            // Set when a right/centre/decimal tab was just emitted: the content that follows is
            // positioned AGAINST the stop (its width is what sized the tab), so its first token must
            // not trigger a line break. Without this, an entry whose text reaches within the number's
            // width of a right tab at the margin — a long table-of-contents heading — pushed its page
            // number onto a line of its own, and because the first (number-less) TOC pass did not, the
            // two passes disagreed on the entry's height and the recorded page numbers drifted (2:8).
            bool afterAlignTab = false;

            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
            {
                var inline = runs[runIndex];
                // A forced break is a paragraph separator (bidi class B) and a tab a segment
                // separator (class S); both are in the logical text so UAX #9 L1 can reset them.
                if (inline is Model.LineBreak) { CommitPendingSpace(); logical.Append('\n'); Flush(); continue; }
                if (inline is Model.PageBreak)
                {
                    CommitPendingSpace();
                    logical.Append('\n');
                    cur.PageBreakAfter = true;          // read by RenderLines, which owns the page
                    Flush();
                    continue;
                }
                if (inline is Model.TabRun)
                {
                    CommitPendingSpace();
                    TabStop? stop = NextTabStop(para, cur.Width, out double tabTo);

                    // Centre / Right / Decimal position the text that FOLLOWS the tab against the
                    // stop, so the tab's width depends on how wide that text is. It is measured by
                    // looking AHEAD rather than by emitting a placeholder and widening it later,
                    // and that choice is what keeps leaders working: a leader has to be emitted in
                    // its place in the logical text, and a retro-fitted one would have to insert
                    // characters into text already appended -- putting a table of contents' dots
                    // after its page number in every extraction.
                    double tabW;
                    if (stop != null && stop.Align != TabAlignment.Left)
                    {
                        // The content after this tab is aligned to the stop; its first token must not
                        // wrap, or an aligned number/value ends up on a line of its own.
                        afterAlignTab = true;
                        double ahead = MeasureSegmentAhead(ctx, runs, runIndex + 1, stop.Align,
                                                           baseDirection, paraAlpha);
                        // How much of the measured segment sits BEFORE the stop. Right puts all of
                        // it there and Centre half; for Decimal the measurement is already the
                        // width up to the separator, so it takes all of what it was given.
                        double before = stop.Align == TabAlignment.Center ? ahead / 2 : ahead;
                        tabW = tabTo - cur.Width - before;
                        // The text already runs past the stop, so there is nothing to align to.
                        // A visible word space keeps it from butting against what precedes it,
                        // which is what Word falls back to as well.
                        if (tabW < MinCollapsedTabWidth) tabW = MinCollapsedTabWidth;
                    }
                    else
                    {
                        tabW = tabTo - cur.Width;
                        if (tabW < 1) tabW = DefaultTabInterval;
                    }

                    // A leader fills the gap the tab opens (Word's dotted table-of-contents rule).
                    // It needs a font, and a tab has no run of its own, so it borrows the run
                    // nearest it - the one before, else the first in the paragraph. With neither
                    // there is no font to measure with and the tab stays plain: a leader drawn in a
                    // guessed font would be the wrong width and would push the very stop it exists
                    // to reach.
                    TextRun? leaderRun = stop != null && LeaderUnit(stop) != null
                        ? (lastTextRun ?? FirstTextRun(runs))
                        : null;

                    // `stop != null` is re-tested rather than inferred from `leaderRun`: the two are
                    // correlated by the assignment above and nothing in the type system says so.
                    if (stop != null && leaderRun != null &&
                        TryBuildLeader(ctx, leaderRun, stop, tabW, baseDirection, paraAlpha,
                                       logical.Length, out Piece? leader, out double leaderWidth))
                    {
                        // The unfilled remainder goes FIRST, so the leader ends flush against the
                        // tab stop rather than starting flush against the text: a leader stopping
                        // short of the stop reads as a mistake, one starting a hair late does not.
                        // The two widths still sum to exactly tabW, so nothing downstream sees a
                        // different line width.
                        double gap = tabW - leaderWidth;
                        if (gap > 0.01)
                        {
                            AddTracked(cur, new Piece
                            {
                                IsTab = true, Width = gap, Text = "\t", Alpha = paraAlpha,
                                CharStart = logical.Length, CharLength = 1,
                            });
                            logical.Append('\t');
                            leader.CharStart = logical.Length;
                        }
                        AddTracked(cur, leader);
                        logical.Append(leader.Text);
                        cur.Width += tabW;
                        continue;
                    }

                    AddTracked(cur, new Piece
                    {
                        IsTab = true, Width = tabW, Text = "\t", Alpha = paraAlpha,
                        CharStart = logical.Length, CharLength = 1,
                    });
                    logical.Append('\t');
                    cur.Width += tabW;
                    continue;
                }
                if (inline is ImageRun) { continue; } // images deferred in the centralized renderer

                // A footnote contributes a superscript MARK to the line and nothing else; its own
                // content is drawn at the foot of whichever page this line lands on. Turning it
                // into a TextRun here is what gives the mark the wrapping, bidi and font handling
                // every other run gets — a mark laid out by its own code would be the second
                // text layout in this file.
                if (inline is Model.Footnote note)
                {
                    // An endnote's content is drawn after the last section's content (DrawEndnotes),
                    // not at this page's foot, so the line reserves nothing for it.
                    if (note.Placement != Model.NotePlacement.DocumentEnd) cur.Notes.Add(note);

                    TextRun mark = note.MarkFormat?.Clone() ?? (lastTextRun?.Clone() ?? new TextRun());
                    mark.Text = ctx.Footnotes.MarkOf(note);
                    mark.VerticalAlign = VerticalAlign.Superscript;
                    mark.LinkUri = null;      // the mark points at the note, not at the run's link
                    mark.LinkAnchor = null;
                    mark.FormField = null;    // nor is it the field the run it borrowed from stands for

                    if (mark.Text.Length > 0)
                    {
                        var markPiece = BuildPiece(ctx, mark, mark.Text, isSpace: false,
                                                   baseDirection, paraAlpha);
                        if (markPiece != null)
                        {
                            markPiece.CharStart = logical.Length;
                            markPiece.CharLength = mark.Text.Length;
                            logical.Append(mark.Text);
                            CommitPendingSpace();
                            AddTracked(cur, markPiece);
                            cur.Width += markPiece.Width;
                        }
                    }
                    continue;
                }

                if (inline is not TextRun tr) continue;
                lastTextRun = tr;

                // A box around no text still takes its room (InlineBox remarks): one piece with
                // nothing to draw carries it into the line, so BoxRoomOnLine counts its padding and
                // EmitLine places and paints it. Its one logical character is U+FFFC, the object
                // replacement character — a bidi neutral, as CSS treats an inline object — so the
                // box keeps its place when bidi reorders the line, where a piece with no character
                // would have nothing to be ordered by.
                bool emptyBox = tr.Text.Length == 0 && tr.Box != null;
                foreach (var (token, isSpace) in emptyBox ? EmptyBoxToken : SplitAtLevels(TokenizeFields(tr.Text), logical.Length, splits))
                {
                    if (string.Equals(token, NumPagesPlaceholder, StringComparison.Ordinal))
                        ctx.UsedNumPagesField = true;
                    // The FULL token — controls included — goes into the logical text, because
                    // that is what the bidi resolution has to see. Only the stripped form is
                    // drawn: a bidi control is zero-width, no font has a glyph for it, and letting
                    // one reach the glyph mapper both draws a spurious substitute and records the
                    // control in /ToUnicode, so every copy-paste carries invisible characters and
                    // a search for the surrounding words fails.
                    string drawn = emptyBox ? "" : UnicodeBidi.StripBidiControls(token);
                    // The override the controls before the token leave open. A control glued to the
                    // front of a word never reaches here glued: an override raises the level, so the
                    // word is cut from it (SplitAtLevels) and the control is a token of its own.
                    BaseDirection? overrideDirection = controls.OverrideDirection;
                    var piece = BuildPiece(ctx, tr, drawn, isSpace, baseDirection, paraAlpha, overrideDirection);
                    piece.RunSize = tr.FontSize;
                    piece.CharStart = logical.Length;
                    piece.CharLength = token.Length;
                    logical.Append(token);
                    controls.Feed(token);
                    if (isSpace) { AddSpace(piece); continue; }

                    // An inline box's padding and border take room once per line it appears on —
                    // EmitLine puts them at the box's two visual edges on that line.
                    double boxRoom = BoxRoomOnLine(cur, piece);
                    double need = (pendingSpaceCount > 0 ? pendingSpace : 0) + piece.Width + boxRoom;
                    // Content aligned to a right/centre/decimal tab sits at the stop; its first token
                    // stays on the line rather than wrapping, even when it reaches past the stop.
                    bool pinnedByTab = afterAlignTab;
                    afterAlignTab = false;
                    if (!pinnedByTab && (cur.Pieces.Count > 0 || pendingSpaceCount > 0) && cur.Width + need > LineAvail())
                    {
                        // No space before this token: it is glued to the word it follows — a
                        // bold number and the full stop after it are one word — so the word moves
                        // to the new line whole, as a browser breaks only at a break opportunity.
                        // A word as wide as the line stays, and breaks where it must.
                        List<Piece>? glued = null;
                        // A hyphenating paragraph breaks a word that follows a space at the last of
                        // its hyphenation points whose head, a hyphen drawn after it, still fits;
                        // the rest opens the new line. A glued word moves whole, as above.
                        if (para.Hyphenate && pendingSpaceCount > 0 && tr.Box == null
                            && TryHyphenate(ctx, tr, token, drawn, piece, LineAvail() - cur.Width - pendingSpace,
                                            baseDirection, paraAlpha, overrideDirection, out Piece head, out Piece rest))
                        {
                            CommitPendingSpace();
                            AppendToLine(cur, head);
                            Flush(rest.CharStart);
                            piece = rest;
                        }
                        else
                        {
                            glued = pendingSpaceCount == 0 ? TakeGluedTail(cur) : null;

                            // Close the previous line where this token STARTS: the dropped leading
                            // space stays with the line it trailed (UAX #9 L1 resets it there), and the
                            // token that opens the new line falls inside the new line's own range.
                            Flush(glued != null ? glued[0].CharStart : piece.CharStart);
                            if (glued != null) foreach (var g in glued) AppendToLine(cur, g);
                        }
                        // word starts the new line (drop the leading space)
                        boxRoom = BoxRoomOnLine(cur, piece);
                        AddTracked(cur, piece);
                        cur.Width += piece.Width + boxRoom;
                        cur.MaxSize = Math.Max(cur.MaxSize, tr.FontSize);
                        cur.MaxRunSize = Math.Max(cur.MaxRunSize, tr.FontSize);
                        cur.BoxExtent = Math.Max(cur.BoxExtent, BoxExtentOf(piece));
                    }
                    else
                    {
                        CommitPendingSpace();
                        AddTracked(cur, piece);
                        cur.Width += piece.Width + boxRoom;
                        cur.MaxSize = Math.Max(cur.MaxSize, tr.FontSize);
                        cur.MaxRunSize = Math.Max(cur.MaxRunSize, tr.FontSize);
                        cur.BoxExtent = Math.Max(cur.BoxExtent, BoxExtentOf(piece));
                    }
                }
            }

            CommitPendingSpace();
            cur.CharEnd = logical.Length;
            if (cur.Pieces.Count > 0 || lines.Count == 0) lines.Add(cur);
            laid.LogicalText = logical.ToString();
            return laid;
        }

        /// <summary>
        /// Splits a word that does not fit at the last of its hyphenation points
        /// (<see cref="Tetitulz.Pdf.Core.Text.Hyphenation.EnglishHyphenation"/>) whose head, drawn with a
        /// hyphen after it, is no wider than <paramref name="room"/>: <paramref name="head"/> covers the
        /// head's characters of the logical text and draws them and the hyphen, <paramref name="rest"/>
        /// the rest. <c>false</c> — the word moves whole — when there is no such point, or when the word
        /// is drawn otherwise than it is written (a bidi control stripped).
        /// </summary>
        private static bool TryHyphenate(Ctx ctx, TextRun tr, string token, string drawn, Piece piece,
            double room, BaseDirection baseDirection, double paraAlpha, BaseDirection? overrideDirection,
            out Piece head, out Piece rest)
        {
            head = rest = null!;
            if (room <= 0 || !string.Equals(token, drawn, StringComparison.Ordinal)) return false;
            var points = Tetitulz.Pdf.Core.Text.Hyphenation.EnglishHyphenation.BreakPointsWithin(token);
            for (int i = points.Count - 1; i >= 0; i--)
            {
                int at = points[i];
                var h = BuildPiece(ctx, tr, token.Substring(0, at) + "-", isSpace: false, baseDirection, paraAlpha, overrideDirection);
                if (h.Width > room) continue;
                var r = BuildPiece(ctx, tr, token.Substring(at), isSpace: false, baseDirection, paraAlpha, overrideDirection);
                h.RunSize = r.RunSize = piece.RunSize;
                h.CharStart = piece.CharStart;
                h.CharLength = at;
                r.CharStart = piece.CharStart + at;
                r.CharLength = token.Length - at;
                head = h;
                rest = r;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes from <paramref name="line"/> the word its last pieces make — everything after its
        /// last space, tab or tab leader — with the space before it, and returns the word's pieces; <c>null</c>
        /// when the word is the whole line (nowhere to break but inside it) or there is none. The
        /// line's width, space count and sizes are recomputed from what stays.
        /// </summary>
        private static List<Piece>? TakeGluedTail(Line line)
        {
            int start = line.Pieces.Count;
            while (start > 0 && !line.Pieces[start - 1].IsSpace && !line.Pieces[start - 1].IsTab
                   && !line.Pieces[start - 1].IsLeader) start--;
            if (start == line.Pieces.Count) return null;

            var word = line.Pieces.GetRange(start, line.Pieces.Count - start);
            int keep = start;
            while (keep > 0 && line.Pieces[keep - 1].IsSpace) keep--;   // the space it followed ends the line
            if (keep == 0) return null;
            line.Pieces.RemoveRange(keep, line.Pieces.Count - keep);

            var pieces = line.Pieces.ToList();
            line.Pieces.Clear();
            line.Width = 0; line.SpaceCount = 0;
            line.MaxSize = DocDefaults.FontSizePt; line.MaxRunSize = 0; line.BoxExtent = 0;
            foreach (var p in pieces) AppendToLine(line, p);
            return word;
        }

        /// <summary>
        /// Adds a piece to a line, recording its inline box so <see cref="BoxRoomOnLine"/> can tell
        /// the box's first piece on the line from the rest in O(1). Every append to
        /// <see cref="Line.Pieces"/> goes through here, so <see cref="Line.Boxes"/> never drifts.
        /// </summary>
        private static void AddTracked(Line line, Piece piece)
        {
            line.Pieces.Add(piece);
            if (piece.Box != null) line.Boxes.Add(piece.Box);
        }

        /// <summary>Adds <paramref name="piece"/> to <paramref name="line"/> with its width, its box's room, its spaces and its size.</summary>
        private static void AppendToLine(Line line, Piece piece)
        {
            line.Width += piece.Width + BoxRoomOnLine(line, piece);
            AddTracked(line, piece);
            if (piece.IsSpace) { line.SpaceCount += piece.SpaceRun; return; }
            if (piece.RunSize > 0)
            {
                line.MaxSize = Math.Max(line.MaxSize, piece.RunSize);
                line.MaxRunSize = Math.Max(line.MaxRunSize, piece.RunSize);
            }
            line.BoxExtent = Math.Max(line.BoxExtent, BoxExtentOf(piece));
        }

        /// <summary>The one token an empty boxed run lays out as: U+FFFC, drawn as nothing.</summary>
        private static readonly (string token, bool isSpace)[] EmptyBoxToken = { ("\uFFFC", false) };

        // Splits text into word / space tokens, preserving runs of spaces as single tokens.
        private static IEnumerable<(string token, bool isSpace)> Tokenize(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            int i = 0;
            while (i < text.Length)
            {
                bool space = text[i] == ' ';
                int j = i;
                while (j < text.Length && (text[j] == ' ') == space) j++;
                yield return (text.Substring(i, j - i), space);
                i = j;
            }
        }

        /// <summary>
        /// <see cref="Tokenize"/>, with the <c>{{NUMPAGES}}</c> and <c>{{PAGE}}</c> fields always split
        /// into tokens of their own, which <see cref="SplitAtLevels"/> never cuts.
        /// </summary>
        /// <remarks>
        /// <para>The field is back-filled after layout by a literal ASCII search over the finished
        /// content stream, so it can only be substituted in a piece that took the <b>simple-font</b>
        /// path. Glued to non-Latin text — <c>"עמוד{{NUMPAGES}}"</c> — the whole token needed a
        /// composite font, was written as glyph ids, and the search never matched: the reader saw
        /// the literal <c>{{NUMPAGES}}</c> on the page. On its own the placeholder is pure ASCII,
        /// and <see cref="BuildPiece"/> keeps it on that path whatever family the run names.</para>
        /// <para><c>{{PAGE}}</c> is replaced in <see cref="BuildPiece"/>, which sees one token at a
        /// time, so it has to arrive whole. In a right-to-left paragraph its braces resolve to a
        /// level other than <c>PAGE</c>'s, and <see cref="SplitAtLevels"/> cut it into <c>{{</c>,
        /// <c>PAGE</c> and <c>}}</c>, none of which matched: a Hebrew footer showed the literal
        /// placeholder.</para>
        /// <para>The logical text is unchanged — the same characters in the same order, just more
        /// pieces — so bidi resolution and every <c>CharStart</c>/<c>CharLength</c> are unaffected.</para>
        /// </remarks>
        private static IEnumerable<(string token, bool isSpace)> TokenizeFields(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            int pos = 0;
            while (true)
            {
                int numPages = text.IndexOf(NumPagesPlaceholder, pos, StringComparison.Ordinal);
                int page = text.IndexOf(PagePlaceholder, pos, StringComparison.Ordinal);
                if (numPages < 0 && page < 0) break;
                bool isPage = numPages < 0 || (page >= 0 && page < numPages);
                int at = isPage ? page : numPages;
                string field = isPage ? PagePlaceholder : NumPagesPlaceholder;
                if (at > pos)
                    foreach (var t in Tokenize(text.Substring(pos, at - pos))) yield return t;
                yield return (field, false);
                pos = at + field.Length;
            }
            if (pos < text.Length)
                foreach (var t in Tokenize(text.Substring(pos))) yield return t;
        }

        /// <summary>
        /// The next tab stop past <paramref name="cur"/>: its position through
        /// <paramref name="position"/>, and the <see cref="TabStop"/> itself when the paragraph
        /// declared one.
        /// </summary>
        /// <returns>
        /// <c>null</c> when the stop is one of the implicit half-inch defaults, which carry no
        /// alignment and no leader. That distinction is why this is not a <c>double</c>-returning
        /// helper: with only a position to hand back, a declared leader would have nowhere to arrive
        /// and be silently dropped.
        /// </returns>
        private static TabStop? NextTabStop(Paragraph para, double cur, out double position)
        {
            double best = double.MaxValue;
            TabStop? bestStop = null;
            foreach (var t in para.Tabs)
                if (t.PosPt > cur + 0.1 && t.PosPt < best) { best = t.PosPt; bestStop = t; }

            if (bestStop != null) { position = best; return bestStop; }

            // No stop lies ahead. A right/centre/decimal stop the text has already reached still aligns
            // the content that follows to it: the stop cannot move backward, so the content sits at it
            // and overflows rather than wrapping to a line of its own. (A left stop carries no such
            // pull, so a passed one falls through to the implicit default below.) The rightmost passed
            // alignment stop wins. Without this a long table-of-contents heading pushed its page number
            // onto its own line, and the number-less first layout pass did not, so the two passes
            // disagreed on the entry's height and the page numbers drifted (2:8).
            double passed = double.MinValue;
            TabStop? passedStop = null;
            foreach (var t in para.Tabs)
                if (t.Align != TabAlignment.Left && t.PosPt <= cur + 0.1 && t.PosPt > passed)
                { passed = t.PosPt; passedStop = t; }
            if (passedStop != null) { position = passedStop.PosPt; return passedStop; }

            position = (Math.Floor(cur / DefaultTabInterval) + 1) * DefaultTabInterval;
            return null;
        }

        /// <summary>
        /// Width of the text between a tab and whatever ends its segment — the next tab, a break,
        /// or the end of the paragraph — as an aligned tab stop needs it.
        /// </summary>
        /// <remarks>
        /// <para><b>It builds the pieces a second time, and that is deliberate.</b> The alternative
        /// is to emit a zero-width tab and widen it once the segment has been laid out, which
        /// works for the width and breaks the leader: a leader emitted after the fact would have to
        /// insert its characters into logical text that has already been appended, so a contents
        /// page would extract as "Chapter One 7 ......". Measuring ahead costs one extra
        /// <see cref="BuildPiece"/> per token, only for a non-left stop, and keeps every piece in
        /// the order it is read in.</para>
        /// <para><b>Decimal alignment measures to the separator, not to the end.</b> For a piece
        /// this renderer draws as a simple font the part before the separator is re-measured
        /// directly; for a shaped (Unicode) run there is no substring to re-measure without
        /// re-shaping, so it falls back to right alignment and says so. That is the honest
        /// direction to fail in: a decimal column that lines up on the right is wrong by a
        /// fraction, while one silently aligned on the wrong character is wrong by a digit.</para>
        /// </remarks>
        private static double MeasureSegmentAhead(Ctx ctx, IList<IInlineElement> runs, int from,
                                                  TabAlignment align, BaseDirection baseDirection,
                                                  double paragraphAlpha)
        {
            double total = 0;
            for (int i = from; i < runs.Count; i++)
            {
                var inline = runs[i];
                if (inline is Model.TabRun || inline is Model.LineBreak || inline is Model.PageBreak)
                    break;
                if (inline is not TextRun tr) continue;

                foreach (var (token, isSpace) in TokenizeFields(tr.Text))
                {
                    Piece piece = BuildPiece(ctx, tr, token, isSpace, baseDirection, paragraphAlpha);
                    if (piece == null) continue;

                    if (align == TabAlignment.Decimal)
                    {
                        int at = token.IndexOf(DecimalAlignSeparator);
                        if (at >= 0)
                        {
                            if (piece.Unicode)
                            {
                                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                                    "a decimal tab stop fell on shaped text, whose separator " +
                                    "position cannot be measured without re-shaping the run; the " +
                                    "segment was right-aligned instead");
                                return total + piece.Width;
                            }
                            Piece head = BuildPiece(ctx, tr, token.Substring(0, at), isSpace: false,
                                                    baseDirection, paragraphAlpha);
                            return total + (head?.Width ?? 0);
                        }
                    }

                    total += piece.Width;
                }
            }
            return total;
        }

        /// <summary><paramref name="unit"/> <paramref name="count"/> times over.</summary>
        private static string Repeat(string unit, int count)
        {
            var sb = new System.Text.StringBuilder(unit.Length * count);
            for (int i = 0; i < count; i++) sb.Append(unit);
            return sb.ToString();
        }

        /// <summary>
        /// What a stop's leader repeats: its <see cref="TabStop.LeaderText"/>, else its <see cref="TabStop.Leader"/>'s
        /// character; <c>null</c> for no leader.
        /// </summary>
        private static string? LeaderUnit(TabStop stop) =>
            !string.IsNullOrEmpty(stop.LeaderText) ? stop.LeaderText
            : stop.Leader switch
            {
                TabLeader.Dot        => ".",
                TabLeader.Hyphen     => "-",
                TabLeader.Underscore => "_",
                _                    => null,
            };

        /// <summary>
        /// Builds the repeated run that fills a tab's gap — the stop's <see cref="LeaderUnit"/> as many times as
        /// it fits — or returns <c>false</c> when not even one fits.
        /// </summary>
        /// <remarks>
        /// It goes through <see cref="BuildPiece"/> rather than measuring and emitting on its own,
        /// so the leader inherits the borrowed run's font, size, colour and character spacing - and,
        /// more to the point, so it takes the same Unicode-versus-WinAnsi decision every other piece
        /// takes. A separate emit path here would be a second place that has to know how a glyph
        /// reaches the page.
        /// </remarks>
        private static bool TryBuildLeader(Ctx ctx, TextRun source, TabStop stop, double gapWidth,
                                           BaseDirection baseDirection, double paragraphAlpha,
                                           int charStart,
                                           [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Piece? piece,
                                           out double width)
        {
            piece = null; width = 0;
            string? unit = LeaderUnit(stop);
            if (unit == null || gapWidth <= 0) return false;

            Piece one = BuildPiece(ctx, source, unit, isSpace: false,
                                   baseDirection, paragraphAlpha);
            if (one == null || one.Width <= 0.01 || one.Width > gapWidth) return false;
            // Each repetition after the first adds the width two make less one — the kerning where one ends
            // and the next begins included — so n of them are one's width and n - 1 steps, exactly.
            double step = one.Width;
            if (unit.Length > 1 && BuildPiece(ctx, source, unit + unit, isSpace: false, baseDirection, paragraphAlpha) is Piece two
                && two.Width - one.Width > 0.01)
                step = two.Width - one.Width;

            long fits = 1 + SaturatingCast.ToLong((gapWidth - one.Width) / step);
            // A leader is decoration; a pathological tab stop must not turn one tab into tens of
            // thousands of glyphs. The cap is generous next to any real page width.
            int count = (int)Math.Min(fits, MaxLeaderCharacters / unit.Length);
            if (count < 1) return false;

            piece = BuildPiece(ctx, source, Repeat(unit, count), isSpace: false,
                               baseDirection, paragraphAlpha);
            if (piece == null || piece.Width <= 0) return false;

            piece.CharStart = charStart;
            piece.CharLength = count * unit.Length;
            // The leader is not a link and is not underlined even when the borrowed run is: it
            // belongs to the tab, not to the text whose formatting it borrowed for its metrics.
            piece.LinkUri = null;
            piece.FormField = null;
            piece.Underline = piece.Strike = piece.DoubleStrike = false;
            piece.Highlight = "";
            piece.IsLeader = true;
            width = piece.Width;
            return true;
        }

        /// <summary>The first <see cref="TextRun"/> in a paragraph's inline list, or null.</summary>
        private static TextRun? FirstTextRun(IEnumerable<IInlineElement> runs)
        {
            foreach (var r in runs) if (r is TextRun tr) return tr;
            return null;
        }

        private static Piece BuildPiece(Ctx ctx, TextRun tr, string token, bool isSpace,
            BaseDirection baseDirection, double paragraphAlpha, BaseDirection? overrideDirection = null)
        {
            bool sup = tr.VerticalAlign == VerticalAlign.Superscript;
            bool sub = tr.VerticalAlign == VerticalAlign.Subscript;
            float drawSize = (float)((sup || sub) ? tr.FontSize * SuperSubFontScale : tr.FontSize);
            double rise = (sup ? tr.FontSize * SuperscriptRiseFactor
                               : (sub ? -tr.FontSize * SubscriptDropFactor : 0)) + tr.BaselineShift;
            var (r, g, b) = ParseColorD(tr.Color);

            string text = ApplyCaps(token, tr);
            // PAGE field: resolved to the current page number at draw time, in the section's
            // format and from its start -- the same text a contents entry records for the page.
            if (text.Length > 0 && text.IndexOf(PagePlaceholder, StringComparison.Ordinal) >= 0)
                text = text.Replace(PagePlaceholder, ctx.PageNumberText());
            var p = new Piece
            {
                DrawSize = drawSize,
                Rise = rise,
                CharSpacing = tr.CharSpacing,
                Scale = tr.HorizontalScale <= 0 ? NormalHorizontalScale : tr.HorizontalScale,
                R = (float)r, G = (float)g, B = (float)b,
                // The paragraph x run multiply happens HERE and nowhere else, so the text pass and
                // the underline / highlight passes cannot end up at different opacities.
                Alpha = paragraphAlpha * Alpha01(tr.Opacity),
                IsSpace = isSpace,
                Invisible = tr.IsInvisible,
                Underline = tr.IsUnderline,
                Strike = tr.IsStrikethrough,
                DoubleStrike = tr.IsDoubleStrike,
                Highlight = tr.Highlight,
                // LinkAnchor folds into LinkUri with the '#' the link pass already recognises,
                // rather than becoming a second field on Piece. An explicit LinkUri wins: a run
                // carrying both is contradictory, and the external target is the more specific
                // statement. This is where TextRun.LinkAnchor, which the DOCX mapper sets,
                // reaches the output.
                LinkUri = !string.IsNullOrEmpty(tr.LinkUri) ? tr.LinkUri
                        : !string.IsNullOrEmpty(tr.LinkAnchor) ? "#" + tr.LinkAnchor
                        : null,
                FormField = tr.FormField,
                Box = tr.Box,
            };

            // Nothing to draw — an empty box's piece, or a token that was only a bidi control —
            // needs no font: resolving one for no code points is a question with no answer.
            if (text.Length == 0) return p;

            // A run reaches the embedding path either because the standard-14 fonts CANNOT draw it
            // (anything above U+00FF) or because the caller NAMED a family they want. The second is
            // new: the exporter mapped every Latin run onto one of three standard-14 faces, so a
            // document asking for Garamond got Helvetica's shapes AND Helvetica's metrics, with
            // nothing anywhere saying so.
            //
            // It costs nothing for a document that does not ask. TextRun.FontName defaults to
            // Helvetica, the generic CSS keywords resolve to the standard-14 buckets, and
            // BuilderFontResolver.Resolve returns null unless the caller supplied a FontProvider or
            // set AllowSystemFontSubstitution — so an untouched document produces byte-identical
            // output and no font is redistributed that the caller did not choose.
            bool namesOwnFamily = !IsStandardFourteenFamily(tr.FontName);
            // The NUMPAGES field is a token of its own, found in the finished content as a literal
            // and replaced after layout (ReplaceInContent). Shaped into a named family's glyph ids
            // there would be no literal to find and the page would show the placeholder, so the
            // field is drawn in the standard face whatever family the run names. PAGE was replaced
            // above, before any shaping.
            bool numPagesField = string.Equals(text, NumPagesPlaceholder, StringComparison.Ordinal);
            if (RtlText.NeedsUnicode(text) || (namesOwnFamily && !numPagesField))
            {
                var cps = new HashSet<int>();
                RtlText.CollectCodePoints(text, cps);
                // CollectCodePoints gathers only what is ABOVE U+00FF, which is exactly right when
                // the question is "can the standard-14 fonts draw this at all". For a NAMED family
                // the question is different — the candidate has to cover the run's Latin too — and
                // for a pure-Latin run the set would otherwise be empty, which the resolver reads
                // as "nothing to cover" and declines.
                //
                // Through RtlText, not `foreach (char c in text)`: that walked code UNITS, so a
                // named-family run containing an astral character asked every candidate to cover two
                // lone surrogates, which no cmap maps — the same defect CollectCodePoints itself had,
                // reintroduced one line below the call that fixes it.
                if (namesOwnFamily)
                    RtlText.CollectAllCodePoints(text, cps);

                var (handle, prog) = ctx.GetUniFont(tr.FontName, tr.IsBold, tr.IsItalic, cps,
                                                    namedFamily: namesOwnFamily);
                if (handle != null && prog != null)
                {
                    // The PARAGRAPH's direction, not the token's own first-strong character:
                    // resolving per token is what made a Latin word inside Hebrew its own
                    // left-to-right paragraph.
                    // Inside an override, the override's direction holds every character of the token
                    // (BidiControlStack): shaped in the paragraph's, Hebrew under an LTR override kept
                    // the right-to-left order the override exists to undo.
                    var shaped = RtlText.Shape(text, prog, drawSize, handle, overrideDirection ?? baseDirection,
                                               tr.Kerning ?? ctx.Options.Kerning,
                                               tr.ContextualAlternates ?? ctx.Options.ContextualAlternates,
                                               tr.Ligatures ?? ctx.Options.Ligatures,
                                               tr.DiscretionaryLigatures ?? ctx.Options.DiscretionaryLigatures,
                                               tr.HistoricalLigatures ?? ctx.Options.HistoricalLigatures,
                                               directionalOverride: overrideDirection != null);
                    p.Unicode = true;
                    p.Gids = shaped.Gids;
                    p.Placements = shaped.Placements;
                    p.FontRes = handle.ResourceName;
                    // Letter spacing follows each letter with its marks, not each glyph (GlyphRunShowText).
                    p.Width = (shaped.WidthPt + tr.CharSpacing * (shaped.Gids.Length - shaped.MarkCount)) * p.Scale / 100.0;
                    (p.Ascent, p.Descent) = ProgramLineMetrics(prog);
                    return p;
                }

                // A run that only NAMED a family loses nothing a reader will notice: the
                // standard-14 face below draws every character it has, at that face's metrics. So
                // it falls through quietly rather than through the policy ladder — throwing, or
                // omitting the text, because Garamond is not installed would turn a cosmetic
                // substitution into a failed conversion.
                if (!RtlText.NeedsUnicode(text))
                {
                    // Once per family per export, from the export that draws it: every piece of
                    // every run in the family comes through here, a word at a time.
                    if (!ctx.MeasureOnly && ctx.SubstitutedFamiliesReported.Add(tr.FontName ?? ""))
                        ctx.Fonts.ReportUnresolved(
                            $"'{Excerpt(text)}' was drawn in a standard-14 face: no font program was " +
                            $"available for the requested family '{tr.FontName}'. Supply one through " +
                            "PdfExportOptions.FontProvider, or set AllowSystemFontSubstitution. " +
                            "Reported once for the family; all its text is drawn the same way.");
                }
                else
                {
                // Falling through here means every character above U+00FF in this run is about to
                // be written as '?' by the WinAnsi path. That was silent: "no Hebrew font on this
                // host" and "no Hebrew in the document" produced indistinguishable output.
                switch (ctx.Options.UnrenderableText)
                {
                    case UnrenderableTextPolicy.Throw:
                        throw new UnrenderableTextException(
                            $"no font available to this export can render '{Excerpt(text)}' " +
                            $"(needs {BuilderFontResolver.DescribeMissing(null, cps)}); " +
                            "supply one through PdfExportOptions.FontProvider.");
                    case UnrenderableTextPolicy.Omit:
                        ctx.Fonts.ReportUnresolved(
                            $"omitted '{Excerpt(text)}': no font covers {BuilderFontResolver.DescribeMissing(null, cps)}");
                        p.Unicode = false;
                        p.Text = "";
                        p.FontRes = ctx.StandardFont(tr.IsBold, tr.IsItalic, FamilyOf(tr.FontName));
                        p.Width = 0;
                        return p;
                    default:
                        ctx.Fonts.ReportUnresolved(
                            $"'{Excerpt(text)}' falls back to a standard-14 WinAnsi font, which writes " +
                            $"'?' for {BuilderFontResolver.DescribeMissing(null, cps)}" +
                            (string.IsNullOrEmpty(tr.FontName) ? "" : $" (requested family '{tr.FontName}')") +
                            "; supply a font through PdfExportOptions.FontProvider to fix this.");
                        break;
                }
                }
            }

            var fam = FamilyOf(tr.FontName);
            p.Unicode = false;
            p.Text = text;
            p.FontRes = ctx.StandardFont(tr.IsBold, tr.IsItalic, fam);
            // Kerned when asked, by the standard face's own AFM pairs: a reader positions
            // the face's glyphs by their widths alone, so the pairs are the TJ the piece is drawn with.
            // Not the NUMPAGES field: it is found in the drawn content and replaced after layout (PAGE was
            // replaced above).
            double kerned = 0;
            if ((tr.Kerning ?? ctx.Options.Kerning) && !string.Equals(text, NumPagesPlaceholder, StringComparison.Ordinal))
            {
                p.Kerns = Standard14Kerning.Adjustments(StandardBaseFont(tr.IsBold, tr.IsItalic, fam), text);
                if (p.Kerns != null) foreach (double k in p.Kerns) kerned += k;
            }
            p.Width = (AdvanceWidth(text, tr.IsBold, tr.IsItalic, drawSize, fam) + kerned / PdfUnits.GlyphSpaceUnitsPerEm * drawSize
                       + tr.CharSpacing * text.Length) * p.Scale / 100.0;
            (p.Ascent, p.Descent) = StandardLineMetrics(fam);
            return p;
        }

        /// <summary>
        /// The line metrics of the fonts a viewer puts under the standard faces, as fractions of the
        /// size: the <c>hhea</c> ascender and descender of Liberation Sans, Serif and Mono — metric
        /// compatible with Arial, Times New Roman and Courier New, and what <c>FontFileLocator</c>
        /// maps the standard 14 to first — 1854/434, 1825/443 and 1705/615 of a 2048 em. A browser
        /// lays its text out by these; the standard faces themselves have no <c>hhea</c>.
        /// </summary>
        /// <remarks>
        /// Per face, not a fixed 0.83 above and 0.22 below for every face, which would put Arial text
        /// 4% of its size higher in a line than a browser does and draw a highlight 6% shorter.
        /// </remarks>
        internal static (double Ascent, double Descent) StandardLineMetrics(StdFamily fam) => fam switch
        {
            StdFamily.Serif => (0.891, 0.216),
            StdFamily.Mono => (0.833, 0.300),
            _ => (0.905, 0.212),
        };

        /// <summary>
        /// An embedded program's line metrics as fractions of the size: its <c>hhea</c> ascent and
        /// descent, which <see cref="TrueTypeFontProgram"/> scales to a 1000-unit em. A program with
        /// no usable pair keeps the fixed box.
        /// </summary>
        internal static (double Ascent, double Descent) ProgramLineMetrics(TrueTypeFontProgram prog)
        {
            double ascent = prog.Ascent / (double)PdfUnits.GlyphSpaceUnitsPerEm;
            double descent = -prog.Descent / (double)PdfUnits.GlyphSpaceUnitsPerEm;   // hhea's descender is negative
            return ascent > 0 && descent >= 0 && ascent + descent < 4 ? (ascent, descent) : (GlyphBoxAscent, GlyphBoxDescent);
        }

        /// <summary>A short, single-line excerpt of a run, for a diagnostic message.</summary>
        private static string Excerpt(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= 24 ? s : s.Substring(0, 24) + "…";
        }

        /// <summary>The three standard-14 families the renderer can draw for WinAnsi text.</summary>
        internal enum StdFamily { Sans, Serif, Mono }

        // Maps a run's FontName to one of the built-in standard-14 families: Courier (mono),
        // Times (serif), else Helvetica (sans). "sans-serif" / "sans serif" is checked before
        // "serif" so it resolves to Helvetica. Used for HTML &lt;pre&gt;/&lt;code&gt; (mono),
        // serif font-family stacks (Times), and any caller that sets a FontName.
        /// <summary>
        /// Whether <paramref name="fontName"/> is one of the standard-14 faces (or a generic CSS
        /// keyword that maps onto one), rather than a family the caller wants in its own right.
        /// </summary>
        /// <remarks>
        /// <para>Deliberately <b>narrow</b>: only the names the exporter can already draw exactly
        /// count as standard-14. Everything else — Garamond, Calibri, Aptos, whatever a Word
        /// document names — is a family the document asked for and did not get.</para>
        /// <para><c>Arial</c>, <c>Times New Roman</c> and <c>Courier New</c> are included, and that
        /// is the one judgement call here. They are not standard-14 names, but the standard-14
        /// faces are <i>metric-compatible</i> with them — which is the same reason
        /// <c>Core.Fonts.FontFileLocator</c> substitutes Liberation for them — so drawing them as
        /// Helvetica/Times/Courier leaves the layout correct. Treating them as "named" would make
        /// every ordinary Word or HTML document start embedding fonts, which is a large behaviour
        /// change to fix a difference nobody asked about.</para>
        /// </remarks>
        internal static bool IsStandardFourteenFamily(string? fontName)
        {
            if (string.IsNullOrWhiteSpace(fontName)) return true;

            string f = fontName!.Trim().ToLowerInvariant();
            // A CSS stack ("Georgia, serif") is named by its FIRST family; the rest are fallbacks.
            int comma = f.IndexOf(',');
            if (comma >= 0) f = f.Substring(0, comma).Trim();
            f = f.Trim('\'', '"');

            switch (f)
            {
                case "helvetica":
                case "arial":
                case "times":
                case "times-roman":
                case "times new roman":
                case "courier":
                case "courier new":
                case "symbol":
                case "zapfdingbats":
                case "sans-serif":
                case "sans serif":
                case "serif":
                case "monospace":
                case "mono":
                    return true;
                default:
                    return false;
            }
        }

        internal static StdFamily FamilyOf(string? fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return StdFamily.Sans;
            string f = fontName!.ToLowerInvariant();
            if (f.Contains("courier") || f.Contains("mono") || f.Contains("consol")) return StdFamily.Mono;
            if (f.Contains("sans")) return StdFamily.Sans;
            if (f.Contains("serif") || f.Contains("times") || f.Contains("georgia")
                || f.Contains("garamond") || f.Contains("cambria") || f.Contains("palatino")
                || f.Contains("minion")) return StdFamily.Serif;
            return StdFamily.Sans;
        }

        private static string ApplyCaps(string token, TextRun tr)
        {
            if (tr.IsAllCaps || tr.IsSmallCaps) return token.ToUpperInvariant();
            return token;
        }

        // ================================================================== line emit

        private static void EmitLine(Ctx ctx, LaidOutParagraph para, Line line, double bandLeft,
            double avail, double baselineY, TextAlignment align, bool rtl, bool lastLine,
            PdfStructureElement? element = null)
        {
            // Drop a trailing space piece from layout width for alignment.
            double lineWidth = line.Width;
            // Not [^1]: System.Index does not exist on the netstandard2.0 leg this assembly builds.
            Piece? trailingSpace = line.Pieces.Count > 0 && line.Pieces[line.Pieces.Count - 1].IsSpace
                ? line.Pieces[line.Pieces.Count - 1]
                : null;
            if (trailingSpace != null) lineWidth -= trailingSpace.Width;

            // Justification works in both directions: the walk below runs left→right regardless of
            // direction, so no RTL special case is needed.
            double extraPerSpace = 0;
            bool justify = align == TextAlignment.Justify && !lastLine && line.SpaceCount > 0
                           && lineWidth < avail;
            if (justify) extraPerSpace = (avail - lineWidth) / line.SpaceCount;

            double startLeft = ResolveBlockLeft(align, rtl, bandLeft, avail,
                justify ? avail : lineWidth);

            // Pieces in DISPLAY order, from the line's own bidi reordering (UAX #9 L1 then L2).
            //
            // Not "left→right in logical order, or right→left in logical order", which is only
            // accidentally correct. Reversing the token sequence performs the level-1 reversal, so
            // pure Hebrew and Hebrew-plus-one-Latin-word come out right — but a multi-word Latin run
            // inside Hebrew has its own level-2 run that must NOT be reversed, and blanket reversal
            // would emit "John Smith" as "Smith John".
            var ordered = VisualPieceOrder(para, line, rtl);

            // `placed` drives the ink passes (highlight, rules, links) and deliberately excludes
            // spaces and tabs, so their geometry is unchanged. `laid` adds them back for the text
            // pass alone: a space glyph draws nothing, but it is what carries the word boundary into
            // the content stream for extraction, copy-paste and search.
            var placed = new List<(Piece pc, double x)>();
            var laid = new List<(Piece pc, double x)>();
            {
                double cx = startLeft;
                var visual = ordered as IList<Piece> ?? ordered.ToList();
                // The trailing space is outside the width the line is aligned by, so it hangs past
                // the line's end — which in a right-to-left line is its LEFT, where bidi puts it.
                // Drawn from startLeft like any first piece, it pushed every word one space to the
                // right: past the right edge of a right-aligned line. Its justification share still
                // counts, as it does at the right end of a left-to-right line, so the line's START
                // edge is the flush one in both directions.
                if (trailingSpace != null && visual.Count > 0 && ReferenceEquals(visual[0], trailingSpace))
                    cx -= trailingSpace.Width;
                for (int k = 0; k < visual.Count; k++)
                {
                    var pc = visual[k];
                    // An inline box's padding and border sit at its visual edges on this line —
                    // the room layout reserved for it (BoxRoomOnLine), placed where it belongs.
                    InlineBox? box = pc.Box;
                    if (box != null && (k == 0 || !ReferenceEquals(visual[k - 1].Box, box)))
                        cx += box.MarginLeft + box.PaddingLeft + BoxBorderWidth(box);

                    double w = pc.Width + (pc.IsSpace && justify ? extraPerSpace : 0);
                    laid.Add((pc, cx));
                    if (!pc.IsSpace && !pc.IsTab) placed.Add((pc, cx));
                    cx += w;

                    if (box != null && (k == visual.Count - 1 || !ReferenceEquals(visual[k + 1].Box, box)))
                        cx += box.PaddingRight + BoxBorderWidth(box) + box.MarginRight;
                }
            }

            // A tab piece is synthesised by layout and never had a font. Borrow a simple font from
            // the line so the gap still reaches the text stream — as a space, not U+0009, which no
            // simple font maps and which extractors drop. The exact gap stays recoverable from the
            // geometry; what this restores is the word boundary.
            foreach (var (pc, _) in laid)
            {
                if (!string.IsNullOrEmpty(pc.FontRes) || (!pc.IsSpace && !pc.IsTab)) continue;
                foreach (var (donor, _) in placed)
                {
                    if (donor.Unicode || string.IsNullOrEmpty(donor.FontRes)) continue;
                    pc.FontRes = donor.FontRes;
                    pc.DrawSize = donor.DrawSize;
                    break;
                }
                if (pc.IsTab) pc.Text = " ";
            }

            // Pass 1 — highlight rectangles (behind text).
            //
            // Each pass is wrapped separately, and the ink order is untouched: only BDC/EMC lines
            // are interleaved, so nothing moves on the page. Decoration is marked as an ARTIFACT
            // rather than left bare, because unmarked ink inside a tagged page is itself a
            // conformance violation — "not tagged" is not a neutral state once the page claims to
            // be tagged.
            using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                DrawInlineBoxes(ctx, laid, baselineY, justify ? extraPerSpace : 0);

            using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
            foreach (var (pc, px) in placed)
            {
                if (string.IsNullOrEmpty(pc.Highlight)) continue;
                var (r, g, b) = ParseColorD(pc.Highlight);
                ctx.Page.SaveState().SetFillColorRgb((float)r, (float)g, (float)b);
                ApplyFillAlpha(ctx, pc.Alpha);
                var (hx, hy, hw, hh) = HighlightBox(pc, px, baselineY);
                ctx.Page.Rectangle(hx, hy, hw, hh).Fill().RestoreState();
            }

            // Pass 2 — text. One marked-content sequence per LINE, which is also one BT…ET.
            //
            // The line is the right unit because RenderLines calls EnsureSpace BEFORE EmitLine, so
            // a page break always falls between lines: a line therefore draws on exactly one page,
            // and one page is exactly one MCID space. A paragraph spanning pages simply marks on
            // two pages, and the element accumulates both — the page identity travels with the
            // MCID, not with the element.
            using (ctx.Mark(element))
            {
                ctx.Page.BeginText();
                // The line opens with the text state at its PDF defaults — the previous line's
                // ResetTextState put it there, and a fresh content stream starts there (§9.3).
                var state = TextState.Default;
                // `baselineY`, NOT `baselineY + pc.Rise`: the rise reaches the glyphs through
                // `Ts`, which §9.4.4 applies IN ADDITION to the text matrix. Adding it to the
                // matrix as well drew every superscript, subscript and BaselineShift at twice its
                // offset, and detached the underline / strike / highlight passes below — which use
                // the single offset — from the glyphs they belong to.
                foreach (var (pc, px) in laid) DrawPiece(ctx, pc, px, baselineY, ref state);
                ResetTextState(ctx, ref state);
                ctx.Page.EndText();
            }

            // Pass 3 — underline / strike rules (over text). Decoration, like pass 1.
            using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
            foreach (var (pc, px) in placed)
            {
                // A rule takes the text's colour (CSS text-decoration-color: currentcolor), so an
                // invisible run's rules are invisible too.
                if (pc.Invisible) continue;
                double yb = baselineY + pc.Rise;
                double thick = Math.Max(0.4, pc.DrawSize * 0.06);
                if (pc.Underline) FillRule(ctx, px, yb - pc.DrawSize * 0.12, pc.Width, thick, pc);
                if (pc.Strike) FillRule(ctx, px, yb + pc.DrawSize * 0.26, pc.Width, thick, pc);
                if (pc.DoubleStrike)
                {
                    FillRule(ctx, px, yb + pc.DrawSize * 0.32, pc.Width, thick, pc);
                    FillRule(ctx, px, yb + pc.DrawSize * 0.20, pc.Width, thick, pc);
                }
            }

            // Pass 4 — clickable /Link annotations. An external URI becomes a /URI action; a
            // leading '#' names a destination declared by some paragraph's AnchorName and becomes
            // a /GoTo, so a converted DOCX's cross-references are links, not plain text.
            foreach (var (pc, px) in placed)
            {
                // Captured into a local so the null check narrows on BOTH legs — netstandard2.0's
                // string.IsNullOrEmpty carries no [NotNullWhen(false)], so the indexer below stayed
                // a possibly-null dereference there.
                string? linkUri = pc.LinkUri;
                if (linkUri == null || linkUri.Length == 0) continue;
                double yb = baselineY + pc.Rise;
                // An annotation /Rect is in DEFAULT user space (§12.5.2), which the page's
                // leading `cm` does not reach — the matrix applies to the content stream only.
                // So this is the one site that has to apply Ctx.Scale by hand; miss it and a
                // scaled page's links stay where the unscaled text would be.
                double s = ctx.Scale;
                double x0 = px * s, y0 = (yb - pc.DrawSize * 0.25) * s;
                double x1 = (px + pc.Width) * s, y1 = (yb + pc.DrawSize * 0.9) * s;

                if (linkUri[0] == '#')
                    ctx.Page.AddInternalLink(x0, y0, x1, y1, linkUri.Substring(1));
                else if (LinkTargets.IsSafe(linkUri))
                    ctx.Page.AddLink(x0, y0, x1, y1, linkUri);
                else if (!ctx.MeasureOnly && !ctx.UnsafeLinkReported)
                {
                    // Its text stays; a viewer must not be handed a javascript: (or any other) action to open.
                    ctx.UnsafeLinkReported = true;
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                        $"a link to '{Excerpt(linkUri)}' was not written: its scheme is not one a document link uses "
                        + "(http, https, mailto, ftp, ftps, tel). Reported once per export.");
                }
            }

            // Pass 5 — where each form-field run landed. Reported, not written: see FormFieldSpec.
            // The rectangle is the highlight pass's own box, so a widget laid over it covers exactly
            // what the page shows as the field; and it is in DEFAULT user space, like a /Link
            // rectangle above, so Ctx.Scale is applied by hand here too.
            foreach (var (pc, px) in placed)
            {
                FormFieldSpec? field = pc.FormField;
                if (field == null || !ctx.PlacedFieldSpecs.Add(field)) continue;   // first placement wins
                var (hx, hy, hw, hh) = HighlightBox(pc, px, baselineY);
                double s = ctx.Scale;
                ctx.PlacedFormFields.Add(new PlacedFormField(
                    field, ctx.CurrentPageNumber - 1, hx * s, hy * s, hw * s, hh * s, pc.DrawSize * s));
            }
        }

        /// <summary>The border width an inline box draws, or 0 for none.</summary>
        private static double BoxBorderWidth(InlineBox box)
            => Border.PaintedWidth(box.Border);

        /// <summary>
        /// The horizontal room <paramref name="piece"/>'s inline box adds to <paramref name="line"/>
        /// — both paddings and both borders — when the piece is the box's first on that line, and 0
        /// otherwise. Counted once per line because <see cref="EmitLine"/> draws a wrapped box as one
        /// fragment per line, each with its full padding.
        /// </summary>
        private static double BoxRoomOnLine(Line line, Piece piece)
        {
            InlineBox? box = piece.Box;
            if (box == null) return 0;
            // The box already has a piece on this line iff it is in the line's box set, which
            // AddTracked keeps in step with Pieces — an O(1) lookup in place of scanning every piece.
            if (line.Boxes.Contains(box)) return 0;
            return box.MarginLeft + box.PaddingLeft + box.PaddingRight + box.MarginRight + 2 * BoxBorderWidth(box);
        }

        /// <summary>
        /// The height <paramref name="piece"/>'s inline box claims in its line when it
        /// <see cref="InlineBox.OccupiesLine"/>: the piece's glyph box, the box's vertical padding
        /// and borders, and its vertical margins. 0 for any other piece.
        /// </summary>
        private static double BoxExtentOf(Piece piece)
            => piece.Box is { OccupiesLine: true } box
                ? piece.DrawSize * (piece.Ascent + piece.Descent) + box.PaddingTop + box.PaddingBottom
                  + 2 * BoxBorderWidth(box) + box.MarginTop + box.MarginBottom
                : 0;

        /// <summary>
        /// Draws each inline box on a line — the fill, then the border — behind the line's text, one
        /// fragment per consecutive stretch of the box's pieces in visual order.
        /// </summary>
        /// <remarks>
        /// The fragment spans its pieces plus the padding and border <see cref="EmitLine"/> left on
        /// either side, and vertically the glyph box of its tallest piece (the same proportions the
        /// highlight uses) plus the vertical padding and border.
        /// </remarks>
        private static void DrawInlineBoxes(Ctx ctx, List<(Piece pc, double x)> laid, double baselineY,
            double extraPerSpace)
        {
            int i = 0;
            while (i < laid.Count)
            {
                InlineBox? box = laid[i].pc.Box;
                if (box == null) { i++; continue; }

                int j = i;
                // The box spans the tallest ascent and the deepest descent of its pieces, each at its
                // own size and font; the rise is the largest piece's.
                double maxSize = 0, rise = 0, ascentPt = 0, descentPt = 0;
                while (j < laid.Count && ReferenceEquals(laid[j].pc.Box, box))
                {
                    var pc = laid[j].pc;
                    if (pc.DrawSize > maxSize) { maxSize = pc.DrawSize; rise = pc.Rise; }
                    ascentPt = Math.Max(ascentPt, pc.DrawSize * pc.Ascent);
                    descentPt = Math.Max(descentPt, pc.DrawSize * pc.Descent);
                    j++;
                }

                var last = laid[j - 1];
                double bw = BoxBorderWidth(box);
                double lastWidth = last.pc.Width + (last.pc.IsSpace ? extraPerSpace : 0);
                double x0 = laid[i].x - box.PaddingLeft - bw;
                double x1 = last.x + lastWidth + box.PaddingRight + bw;
                double y0 = baselineY + rise - descentPt - box.PaddingBottom - bw;
                double y1 = baselineY + rise + ascentPt + box.PaddingTop + bw;
                PaintInlineBox(ctx, box, x0, y0, x1, y1);
                i = j;
            }
        }

        /// <summary>
        /// Paints <paramref name="box"/>'s background colour, background image and border over the
        /// rectangle (<paramref name="x0"/>, <paramref name="y0"/>)–(<paramref name="x1"/>,
        /// <paramref name="y1"/>), its outer edge — for an inline run's box and an image's frame alike.
        /// </summary>
        private static void PaintInlineBox(Ctx ctx, InlineBox box, double x0, double y0, double x1, double y1)
        {
            double bw = BoxBorderWidth(box);
            Corners radius = InlineBoxCorners(box, x1 - x0, y1 - y0);

            if (!string.IsNullOrEmpty(box.Background))
            {
                var (r, g, b) = ParseColorD(box.Background);
                ctx.Page.SaveState().SetFillColorRgb((float)r, (float)g, (float)b);
                AppendBoxPath(ctx, x0, y0, x1 - x0, y1 - y0, radius);
                ctx.Page.Fill().RestoreState();
            }
            // Over the colour and under the border, as CSS layers an element's background.
            DrawBackgroundImage(ctx, box.BackgroundImage, x0, y0, x1 - x0, y1 - y0, 1);
            if (bw > 0)
            {
                var (r, g, b) = ParseColorD(box.Border!.Color);
                ctx.Page.SaveState().SetStrokeColorRgb((float)r, (float)g, (float)b).SetLineWidth((float)bw);
                // Stroked along the middle of the border band, so its outer edge is the box's.
                AppendBoxPath(ctx, x0 + bw / 2, y0 + bw / 2, x1 - x0 - bw, y1 - y0 - bw, radius.Grown(-bw / 2));
                ctx.Page.Stroke().RestoreState();
            }
        }

        /// <summary>
        /// The box a piece's highlight fills, in the page's content space: from a little below the
        /// baseline to a little above the cap height. One definition, because the form-field
        /// placement must cover exactly the shaded box the page shows.
        /// </summary>
        private static (double X, double Y, double Width, double Height) HighlightBox(
            Piece pc, double px, double baselineY)
            => (px, baselineY + pc.Rise - pc.DrawSize * pc.Descent, pc.Width,
                pc.DrawSize * (pc.Ascent + pc.Descent));

        /// <summary>
        /// The glyph box of a piece with no font, as fractions of its size above and below the
        /// baseline. A piece with a font takes the font's line metrics (<see cref="Piece.Ascent"/>):
        /// the box a highlight fills and an inline box surrounds, and what
        /// <see cref="HalfLeadingBaseline"/> centres in the line.
        /// </summary>
        private const double GlyphBoxAscent = 0.83, GlyphBoxDescent = 0.22;

        /// <summary>
        /// Where a line's baseline goes under <see cref="PdfExportOptions.HalfLeading"/>: the
        /// leading — the line height less the glyph box of its text, <paramref name="ascentPt"/>
        /// above the baseline and <paramref name="descentPt"/> below — split half above and half
        /// below (CSS 2.1 §10.8.1). Without the option the baseline is the line's bottom.
        /// </summary>
        internal static double HalfLeadingBaseline(double lineBottom, double lineHeight, double ascentPt, double descentPt)
            => lineBottom + (lineHeight - (ascentPt + descentPt)) / 2 + descentPt;

        /// <summary>
        /// The glyph box of a line's text, in points above and below the baseline: the tallest
        /// ascent and the deepest descent among its pieces, each at its own size and its own font's
        /// metrics — the box <see cref="HalfLeadingBaseline"/> centres. Zero for a line with no
        /// pieces, whose baseline places nothing.
        /// </summary>
        /// <remarks>
        /// Not from <c>MaxSize</c>: that starts at the document's default size, so a line of small
        /// text is never squeezed shorter than a default one, and centring it would put 10pt text as
        /// low as 12pt text sits.
        /// </remarks>
        private static (double AscentPt, double DescentPt) LineGlyphBox(Line line)
        {
            double ascent = 0, descent = 0;
            foreach (var pc in line.Pieces)
            {
                ascent = Math.Max(ascent, pc.DrawSize * pc.Ascent);
                descent = Math.Max(descent, pc.DrawSize * pc.Descent);
            }
            return (ascent, descent);
        }

        /// <summary>
        /// An inline box's corners at its outer edge, <paramref name="w"/> × <paramref name="h"/>: its
        /// one radius or its four, their vertical radii, its one percentage of each side or its
        /// per-corner ones — the same reading as a paragraph's box (<see cref="Corners.Of"/>), not one radius at
        /// every corner.
        /// </summary>
        private static Corners InlineBoxCorners(InlineBox box, double w, double h)
            => Corners.Of(box.CornerRadiusPt, box.CornerRadiiPt, box.CornerRadiiYPt, w, h, percent: box.CornerRadiusPercent,
                          radiiPercent: box.CornerRadiiPercent, radiiYPercent: box.CornerRadiiYPercent);

        /// <summary>
        /// Appends the box outline to the current path: a plain rectangle, or a rounded one when
        /// <paramref name="radius"/> is positive.
        /// </summary>
        /// <remarks>
        /// The corner constant is 0.5523: a circular quadrant drawn as one cubic Bézier needs its
        /// control points at 4/3·(√2−1)·r from the corner. It is the standard value and is written
        /// out rather than approximated, because a visibly non-circular corner is the whole thing a
        /// reader would notice about this feature.
        /// </remarks>
        private static void AppendBoxPath(Ctx ctx, double x, double y, double w, double h, double radius)
            => AppendBoxPath(ctx, x, y, w, h, Corners.Uniform(radius));

        /// <summary>
        /// Appends the box outline with each corner rounded by its own radii (<see cref="Corners"/>,
        /// already clamped to the box) — a quarter ellipse, its horizontal radius along the top or
        /// bottom edge and its vertical one along the side: a plain rectangle when none is rounded.
        /// </summary>
        /// <remarks>The same constant serves an ellipse: a quarter ellipse is a quarter circle scaled on one axis, and so are its control points.</remarks>
        private static void AppendBoxPath(Ctx ctx, double x, double y, double w, double h, Corners r)
        {
            if (r.IsSquare)
            {
                ctx.Page.Rectangle(x, y, w, h);
                return;
            }

            const double K = 0.5522847498307936;
            double x1 = x + w, y1 = y + h;
            // PDF's y runs up: the bottom edge is y, the top y1. From the bottom-left corner's end,
            // anticlockwise: bottom-right, top-right, top-left, bottom-left.
            ctx.Page.MoveTo(x + r.BottomLeft, y)
                    .LineTo(x1 - r.BottomRight, y);
            if (r.BottomRight > 0)
                ctx.Page.CurveTo(x1 - r.BottomRight + r.BottomRight * K, y, x1, y + r.BottomRightY - r.BottomRightY * K, x1, y + r.BottomRightY);
            ctx.Page.LineTo(x1, y1 - r.TopRightY);
            if (r.TopRight > 0)
                ctx.Page.CurveTo(x1, y1 - r.TopRightY + r.TopRightY * K, x1 - r.TopRight + r.TopRight * K, y1, x1 - r.TopRight, y1);
            ctx.Page.LineTo(x + r.TopLeft, y1);
            if (r.TopLeft > 0)
                ctx.Page.CurveTo(x + r.TopLeft - r.TopLeft * K, y1, x, y1 - r.TopLeftY + r.TopLeftY * K, x, y1 - r.TopLeftY);
            ctx.Page.LineTo(x, y + r.BottomLeftY);
            if (r.BottomLeft > 0)
                ctx.Page.CurveTo(x, y + r.BottomLeftY - r.BottomLeftY * K, x + r.BottomLeft - r.BottomLeft * K, y, x + r.BottomLeft, y);
            ctx.Page.ClosePath();
        }

        /// <summary>
        /// A box's four corners, each with a horizontal radius (<c>TopLeft</c> …) and a vertical one
        /// (<c>TopLeftY</c> …), clamped to half the box — the one reading of a paragraph's or a box's
        /// <c>CornerRadiusPt</c> / <c>CornerRadiiPt</c> / <c>CornerRadiiYPt</c> for its fill, its
        /// outline and its shadow. A circular corner has the two equal; a corner with either 0 is
        /// square (CSS Backgrounds 3 §5.4), so both are.
        /// </summary>
        internal readonly struct Corners
        {
            /// <summary>Four circular corners.</summary>
            public Corners(double topLeft, double topRight, double bottomRight, double bottomLeft)
                : this(topLeft, topRight, bottomRight, bottomLeft, topLeft, topRight, bottomRight, bottomLeft) { }

            /// <summary>Four corners, each with its horizontal and its vertical radius.</summary>
            public Corners(double topLeft, double topRight, double bottomRight, double bottomLeft,
                           double topLeftY, double topRightY, double bottomRightY, double bottomLeftY)
            {
                TopLeft = topLeft; TopRight = topRight; BottomRight = bottomRight; BottomLeft = bottomLeft;
                TopLeftY = topLeftY; TopRightY = topRightY; BottomRightY = bottomRightY; BottomLeftY = bottomLeftY;
            }

            public double TopLeft { get; }
            public double TopRight { get; }
            public double BottomRight { get; }
            public double BottomLeft { get; }
            public double TopLeftY { get; }
            public double TopRightY { get; }
            public double BottomRightY { get; }
            public double BottomLeftY { get; }
            public bool IsSquare => TopLeft <= 0 && TopRight <= 0 && BottomRight <= 0 && BottomLeft <= 0;

            public static Corners Uniform(double r) => new Corners(r, r, r, r);

            /// <summary>Every radius times <paramref name="factor"/> — the shadow mask's pixels.</summary>
            public Corners Scaled(double factor) => new Corners(
                TopLeft * factor, TopRight * factor, BottomRight * factor, BottomLeft * factor,
                TopLeftY * factor, TopRightY * factor, BottomRightY * factor, BottomLeftY * factor);

            /// <summary>
            /// The corners a box of <paramref name="w"/> × <paramref name="h"/> draws: the stated four
            /// (<paramref name="radii"/>, CSS's order) or the one <paramref name="uniform"/>, with the
            /// vertical radii of <paramref name="radiiY"/> when stated (else circular), each corner
            /// clamped; a corner on an edge cut by a page break (<paramref name="top"/>,
            /// <paramref name="bottom"/> false) is square, as CSS's <c>slice</c> leaves it.
            /// </summary>
            /// <remarks>
            /// A corner is scaled down whole, keeping its shape, until its horizontal radius is at most
            /// half the width and its vertical one half the height — for a circular corner that is
            /// half the shorter side, exactly — an unclamped one crosses its neighbour and fills as a
            /// bow tie; an ellipse keeps its proportions, as CSS Backgrounds 3 §5.5's scaling keeps
            /// them. A positive <paramref name="percent"/> replaces the lengths: that share of the
            /// width across and of the height down, at every corner. A positive entry of
            /// <paramref name="radiiPercent"/> replaces that corner's horizontal length by
            /// its share of the width, and its vertical one by the same share of the height unless
            /// <paramref name="radiiYPercent"/> or <paramref name="radiiY"/> states it — a percentage
            /// vertical radius is of the height (CSS Backgrounds 3 §5.1).
            /// </remarks>
            public static Corners Of(double uniform, double[]? radii, double[]? radiiY, double w, double h,
                                     bool top = true, bool bottom = true, double percent = 0,
                                     double[]? radiiPercent = null, double[]? radiiYPercent = null)
            {
                double X(int i) => percent > 0 ? percent / 100 * w : radiiPercent is { Length: 4 } && radiiPercent[i] > 0 ? radiiPercent[i] / 100 * w : radii is { Length: 4 } ? radii[i] : uniform;
                double Y(int i) => percent > 0 ? percent / 100 * h : radiiYPercent is { Length: 4 } && radiiYPercent[i] > 0 ? radiiYPercent[i] / 100 * h : radiiY is { Length: 4 } ? radiiY[i] : radiiPercent is { Length: 4 } && radiiPercent[i] > 0 ? radiiPercent[i] / 100 * h : X(i);
                var c = new double[8];
                for (int i = 0; i < 4; i++)
                {
                    bool kept = i < 2 ? top : bottom;
                    double rx = X(i), ry = Y(i);
                    if (!kept || rx <= 0 || ry <= 0) continue;
                    double f = Math.Min(1, Math.Min(w / 2 / rx, h / 2 / ry));
                    c[i] = rx * f; c[i + 4] = ry * f;
                }
                return new Corners(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]);
            }

            /// <summary>
            /// The corners of the box <paramref name="left"/>, <paramref name="top"/>,
            /// <paramref name="right"/> and <paramref name="bottom"/> inside this one: each radius
            /// less the inset on its side (CSS Backgrounds 3 §5.2's inner curve), a corner that
            /// reaches 0 either way square.
            /// </summary>
            public Corners Inset(double left, double top, double right, double bottom)
            {
                static (double, double) In(double rx, double ry, double dx, double dy)
                {
                    double x = rx - dx, y = ry - dy;
                    return rx > 0 && x > 0 && y > 0 ? (x, y) : (0, 0);
                }
                var (tl, tly) = In(TopLeft, TopLeftY, left, top);
                var (tr, tr_y) = In(TopRight, TopRightY, right, top);
                var (br, bry) = In(BottomRight, BottomRightY, right, bottom);
                var (bl, bly) = In(BottomLeft, BottomLeftY, left, bottom);
                return new Corners(tl, tr, br, bl, tly, tr_y, bry, bly);
            }

            /// <summary>Each rounded corner grown by <paramref name="by"/> on both radii (a shadow's spread); a square one stays square (CSS Backgrounds 3 §7.1).</summary>
            public Corners Grown(double by)
            {
                static double Grow(double r, double d) => r > 0 ? Math.Max(0, r + d) : 0;
                return new Corners(Grow(TopLeft, by), Grow(TopRight, by), Grow(BottomRight, by), Grow(BottomLeft, by),
                                   Grow(TopLeftY, by), Grow(TopRightY, by), Grow(BottomRightY, by), Grow(BottomLeftY, by));
            }
        }

        private static void FillRule(Ctx ctx, double x, double y, double w, double thick, Piece pc)
        {
            ctx.Page.SaveState().SetFillColorRgb(pc.R, pc.G, pc.B);
            ApplyFillAlpha(ctx, pc.Alpha);
            ctx.Page.Rectangle(x, y, w, thick).Fill().RestoreState();
        }

        /// <summary>
        /// The three text-state parameters that persist across <c>Tm</c> and across pieces, tracked
        /// so a piece emits an operator only when its value <i>differs</i> from what is in effect.
        /// </summary>
        /// <remarks>
        /// Emitting only non-default values is not the same rule: character spacing, horizontal
        /// scale and rise live in the graphics state (§9.3), so a run that set one would contaminate
        /// every later piece on the line, the reset coming once, at the end. Two runs where only the
        /// first set <c>CharSpacing</c> would draw the second with the first's spacing; a superscript
        /// would raise the whole rest of the line.
        /// </remarks>
        private struct TextState
        {
            public double CharSpacing;
            public int Scale;
            public double Rise;

            /// <summary>
            /// The constant alpha currently in force. Tracked here for the same
            /// emit-only-on-change reason as the three above — but note it is <b>not</b> text
            /// state: <c>/ca</c> lives in the general graphics state and therefore survives
            /// <c>ET</c>, which is why <see cref="ResetTextState"/> has to put it back.
            /// </summary>
            public double Alpha;

            /// <summary>Render mode 3 in force (<see cref="Piece.Invisible"/>); text state, like the first three.</summary>
            public bool Invisible;

            public static TextState Default =>
                new TextState { CharSpacing = 0, Scale = NormalHorizontalScale, Rise = 0, Alpha = 1.0, Invisible = false };
        }

        private static void DrawPiece(Ctx ctx, Piece pc, double x, double y, ref TextState state)
        {
            // Decide whether anything will be drawn BEFORE touching the text state: a piece that
            // draws nothing must not leave its own Tc/Tz/Ts behind for the next one.
            //
            // No font resource means no donor was available on the line (a tab on a line of shaped
            // RTL text). Emitting `/ 0 Tf` would corrupt the stream, so skip.
            bool draws = pc.Unicode
                ? pc.Gids.Length > 0 && !string.IsNullOrEmpty(pc.FontRes)
                : !string.IsNullOrEmpty(pc.Text) && !string.IsNullOrEmpty(pc.FontRes);
            if (!draws) return;

            ctx.Page.SetFillColorRgb(pc.R, pc.G, pc.B);
            if (pc.Alpha != state.Alpha)
            {
                // `gs` IS permitted inside a text object (§8.2, general graphics state), so this
                // needs no BT/ET split. Emitted only on a change, so a line at one opacity costs
                // one operator and a fully opaque document costs none.
                ctx.Page.SetGraphicsState(ctx.Page.AddExtGState((float)pc.Alpha, null));
                state.Alpha = pc.Alpha;
            }
            if (pc.CharSpacing != state.CharSpacing)
            {
                ctx.Page.AppendRaw(F(pc.CharSpacing) + " Tc\n");
                state.CharSpacing = pc.CharSpacing;
            }
            if (pc.Scale != state.Scale)
            {
                ctx.Page.AppendRaw(pc.Scale.ToString(CultureInfo.InvariantCulture) + " Tz\n");
                state.Scale = pc.Scale;
            }
            if (pc.Rise != state.Rise)
            {
                ctx.Page.AppendRaw(F(pc.Rise) + " Ts\n");
                state.Rise = pc.Rise;
            }
            if (pc.Invisible != state.Invisible)
            {
                // Mode 3 paints nothing and still advances (§9.3.6), which is what CSS's transparent
                // text is; the colour was drawn as the inherited one before.
                ctx.Page.SetTextRenderMode(pc.Invisible ? TextRenderMode.Invisible : TextRenderMode.Fill);
                state.Invisible = pc.Invisible;
            }

            if (pc.Unicode)
            {
                ctx.Page.UseEmbeddedFont(pc.FontRes);
                ctx.Page.SetFont(pc.FontRes, pc.DrawSize).SetTextMatrix(1, 0, 0, 1, x, y)
                        .ShowGlyphs(pc.Gids, pc.Placements, pc.DrawSize, pc.Rise, pc.CharSpacing);
            }
            else
            {
                // A piece a bidi pass reordered keeps its width and is drawn unkerned: its pairs were
                // its logical text's, and the characters now beside each other are others.
                string shown = pc.DisplayText ?? pc.Text;
                double[]? kerns = string.Equals(shown, pc.Text, StringComparison.Ordinal) ? pc.Kerns : null;
                ctx.Page.SetFont(pc.FontRes, pc.DrawSize).SetTextMatrix(1, 0, 0, 1, x, y).ShowText(shown, kerns);
            }
        }

        private static void ResetTextState(Ctx ctx, ref TextState state)
        {
            // Return char-spacing / scale / rise to defaults so later content is unaffected.
            ctx.Page.AppendRaw("0 Tc\n100 Tz\n0 Ts\n");
            // The render mode too, when this line set it: text state as well, so a later line's
            // text — and a form field's appearance drawn after — would otherwise be invisible.
            if (state.Invisible)
            {
                ctx.Page.SetTextRenderMode(TextRenderMode.Fill);
                state.Invisible = false;
            }

            // Alpha is restored only if this line actually changed it. Unlike the three above it
            // is NOT text state — nothing about ET puts /ca back — so leaving it set would fade
            // the paragraph border stroked after this line, the next paragraph, and the rest of
            // the page. Conditional so a document that never sets Opacity emits no gs at all.
            if (state.Alpha != 1.0)
            {
                ctx.Page.SetGraphicsState(ctx.Page.AddExtGState(1f, null));
                state.Alpha = 1.0;
            }
        }

        /// <summary>
        /// The line's pieces in left-to-right display order.
        ///
        /// <para>Derived from <see cref="BidiParagraph.VisualOrder"/> over the line's own
        /// character range, so L1 (reset separators and trailing whitespace) and L2 (reverse runs
        /// from the highest level down) both apply to <i>this line</i> rather than to the
        /// paragraph. Each piece is placed where its first displayed character falls — a piece is
        /// a whitespace-delimited token cut wherever its bidi level changes
        /// (<see cref="MixedLevelSplits"/>), so its characters share a level and occupy one
        /// contiguous visual span.</para>
        ///
        /// <para>Falls back to logical order for a purely left-to-right line, which is both the
        /// right answer and the fast path: <see cref="LaidOutParagraph.Bidi"/> is lazy, so a
        /// Latin-only document never resolves bidi at all.</para>
        /// </summary>
        private static List<Piece> VisualPieceOrder(LaidOutParagraph para, Line line, bool rtl)
        {
            if (!rtl && !ContainsMultipleLevels(para, line)) return line.Pieces;

            int start = line.CharStart;
            int length = Math.Max(0, Math.Min(line.CharEnd, para.LogicalText.Length) - start);
            if (length <= 0) return line.Pieces;

            int[] order = para.Bidi.VisualOrder(start, length);

            // Map each logical index to the piece that owns it, then take the pieces in the order
            // their characters first appear on screen.
            var byIndex = new Dictionary<int, Piece>(length);
            foreach (var pc in line.Pieces)
                for (int i = 0; i < pc.CharLength; i++)
                    byIndex[pc.CharStart + i] = pc;

            // Before the one-piece return: a line that is one word — "well." — still has its
            // characters to arrange.
            ArrangeStandardPieces(para, order, byIndex);
            if (line.Pieces.Count <= 1) return line.Pieces;

            var seen = new HashSet<Piece>();
            var result = new List<Piece>(line.Pieces.Count);
            foreach (int logical in order)
                if (byIndex.TryGetValue(logical, out var pc) && seen.Add(pc))
                    result.Add(pc);

            // Any piece whose characters were outside the line's range (there should be none)
            // still has to be drawn — losing text silently is worse than misplacing it.
            //
            // A mutation of this line survives the whole suite, as expected rather than a coverage
            // gap: it is a net under an invariant the loop above is
            // supposed to maintain, so no legitimate input reaches it. Deleting it would be
            // invisible until the day the invariant broke, which is exactly when the text would
            // disappear instead of merely being misplaced. Kept deliberately, unasserted.
            foreach (var pc in line.Pieces) if (seen.Add(pc)) result.Add(pc);
            return result;
        }

        /// <summary>
        /// Arranges each standard-font piece's characters in the line's visual
        /// <paramref name="order"/>, mirroring those at an odd level (UAX #9 L2, L4), into
        /// <see cref="Piece.DisplayText"/>.
        /// </summary>
        /// <remarks>
        /// <para>A piece is a whitespace-delimited token, and its characters need not share a level:
        /// in a right-to-left paragraph the period of <c>well.</c> at the paragraph's end resolves
        /// right-to-left (N2), so it is displayed to the left of the word. A piece drawn through an
        /// embedded font is shaped in the paragraph's direction and was right; one the standard
        /// fonts draw was written in logical order, so a Latin word's closing punctuation sat on the
        /// wrong side — where the same word with one Hebrew letter in it was right.</para>
        /// <para>From the paragraph's levels, not the token's own: <c>Mr.</c> before another Latin
        /// word keeps its period, which lies between two left-to-right letters (N1). A piece whose
        /// drawn text is its logical text less bidi controls is arranged without them — an override
        /// at a word's start resolves to the word's level and stays in its piece, and left out it
        /// shielded the word: under <c>RLO alpha omega PDF</c> one word was reversed and not the other.
        /// A piece whose drawn text differs otherwise — a capital that expanded — is left as it is.</para>
        /// </remarks>
        private static void ArrangeStandardPieces(LaidOutParagraph para, int[] order, Dictionary<int, Piece> byIndex)
        {
            Dictionary<Piece, System.Text.StringBuilder>? arranged = null;
            Dictionary<Piece, bool>? lessControls = null;
            foreach (int logical in order)
            {
                // A shaped piece draws glyphs, not Text, which it leaves empty — so the length test
                // leaves it alone too; a separate test for it would do nothing.
                if (!byIndex.TryGetValue(logical, out var pc)) continue;
                bool stripped = pc.Text.Length != pc.CharLength;
                if (stripped)
                {
                    lessControls ??= new Dictionary<Piece, bool>();
                    if (!lessControls.TryGetValue(pc, out bool same))
                        lessControls[pc] = same = pc.Text.Length > 0 && pc.Text == UnicodeBidi.StripBidiControls(para.LogicalText.Substring(pc.CharStart, pc.CharLength));
                    if (!same || UnicodeBidi.IsBidiControl(para.LogicalText[logical])) continue;
                }
                arranged ??= new Dictionary<Piece, System.Text.StringBuilder>();
                if (!arranged.TryGetValue(pc, out var sb))
                    arranged[pc] = sb = new System.Text.StringBuilder(pc.CharLength);
                // Mirrored at an odd level (MirroredCharAt asks the level); a character the drawn text
                // changed — a capital — is not the one the level was resolved for, and is kept.
                if (stripped) { sb.Append(para.Bidi.MirroredCharAt(logical)); continue; }
                char c = pc.Text[logical - pc.CharStart];
                sb.Append(para.LogicalText[logical] == c ? para.Bidi.MirroredCharAt(logical) : c);
            }
            if (arranged == null) return;
            foreach (var entry in arranged) entry.Key.DisplayText = entry.Value.ToString();
        }

        // Whether an LTR line nonetheless carries an embedded RTL run, which still needs L2.
        private static bool ContainsMultipleLevels(LaidOutParagraph para, Line line)
        {
            if (line.CharEnd <= line.CharStart) return false;
            int end = Math.Min(line.CharEnd, para.LogicalText.Length);
            for (int i = line.CharStart; i < end; i++)
                if (CanCarryOddLevel(para.LogicalText[i])) return true;
            return false;
        }

        /// <summary>
        /// Whether <paramref name="c"/> can give text an odd bidi level: the cheap pre-test that
        /// decides whether a line (<see cref="ContainsMultipleLevels"/>) or a paragraph
        /// (<see cref="ContainsRightToLeft"/>) needs the full resolve.
        /// </summary>
        private static bool CanCarryOddLevel(char c)
        {
            // Cheap pre-test: only text that can carry an odd level needs the full resolve.
            // It must recognise EVERY way an odd level can arise, not just RTL scripts —
            // a wrong "no" here silently skips the reorder, and the fast path then looks
            // like correct left-to-right output rather than like a missing feature.
            if (c >= 0x0590 && c <= 0x08FF) return true;               // Hebrew … Arabic-adjacent
            if (c >= 0xFB1D && c <= 0xFEFF) return true;               // Hebrew/Arabic presentation forms
            // Deliberately over-triggers: ANY astral character forces the full resolve, because
            // this cheap scan cannot tell an astral RTL script from an emoji. A mutation of it
            // survives, and the reason is reach rather than coverage — an astral RTL script
            // needs a font this exporter has no path to (the Standard-14 set is Latin, and the
            // injected-font test fixtures build format-4 cmaps, which cannot carry astral code
            // points). It guards a hazard the public API cannot currently reach; see GAPS.md.
            if (char.IsHighSurrogate(c)) return true;                  // may be an astral RTL script
            if (c == '‏' || c == '‎') return true;           // RLM / LRM — invisible but strong
            // A bidi control (<bdo>, <bdi>, unicode-bidi) forces a level onto text that is
            // otherwise entirely left-to-right, which is the one case no script test can see.
            if (UnicodeBidi.IsBidiControl(c)) return true;
            return false;
        }

        private static double ResolveBlockLeft(TextAlignment align, bool rtl,
            double bandLeft, double bandWidth, double lineWidth)
        {
            // Direction-AWARE, not direction-overriding. Start/End are logical and resolve
            // against the direction; Left/Right/Center are physical, as in CSS, so `text-align: left`
            // inside `dir="rtl"` keeps the author's explicit choice.
            TextAlignment ea = align switch
            {
                // A justified line that is not being stretched (the last one, or one with no
                // spaces) falls back to the leading edge.
                TextAlignment.Justify => rtl ? TextAlignment.Right : TextAlignment.Left,
                TextAlignment.Start => rtl ? TextAlignment.Right : TextAlignment.Left,
                TextAlignment.End => rtl ? TextAlignment.Left : TextAlignment.Right,
                _ => align,   // Left / Right / Center are physical and mean what they say
            };

            double left = ea switch
            {
                TextAlignment.Center => bandLeft + (bandWidth - lineWidth) / 2,
                TextAlignment.Right => bandLeft + (bandWidth - lineWidth),
                _ => bandLeft,
            };
            return left < bandLeft ? bandLeft : left;
        }

        // ================================================================== shared helpers

        private static IReadOnlyCollection<int> CollectRequiredCodePoints(Document document)
        {
            var set = new HashSet<int>();
            void Inline(IEnumerable<IInlineElement> runs)
            {
                foreach (var run in runs) if (run is TextRun tr) RtlText.CollectCodePoints(tr.Text, set);
            }
            void Para(Paragraph p) => Inline(p.Runs);
            void Item(ListItem li)
            {
                RtlText.CollectCodePoints(li.Text, set);
                RtlText.CollectCodePoints(li.Marker, set);
                Inline(li.Runs);
            }

            // A list's DERIVED markers are glyphs too. Collecting only ListItem.Marker misses every
            // ordinal and every bullet the container computes, which is most of them — and a code
            // point the subset does not carry is drawn as a blank.
            void Lst(ListBlock lb)
            {
                foreach (var entry in ModelRules.Flatten(lb))
                {
                    Item(entry.Item);
                    RtlText.CollectCodePoints(entry.Marker, set);
                }
            }
            void Blocks(IEnumerable<IDocElement>? blocks)
            {
                if (blocks == null) return;
                foreach (var b in blocks)
                {
                    if (b is Paragraph cp) Para(cp);
                    else if (b is Table nt) Tbl(nt);
                    else if (b is ListItem cli) Item(cli);
                    else if (b is ListBlock clb) Lst(clb);
                }
            }
            void Tbl(Table t)
            {
                foreach (var row in t.Rows) foreach (var cell in row.Cells) Blocks(cell.Content());
            }
            foreach (var section in document.Sections)
            {
                Blocks(section.Header?.Content());
                Blocks(section.Footer?.Content());
                Blocks(section.FirstPageHeader?.Content());
                Blocks(section.FirstPageFooter?.Content());
                Blocks(section.EvenPageHeader?.Content());
                Blocks(section.EvenPageFooter?.Content());
                Blocks(section.Children);
                // A page number is text made at draw time, in the section's format: Hebrew numbering
                // (hebrew1, hebrew2) is above U+00FF, and with nothing else in the document to say so
                // the {{PAGE}} field was drawn as "??".
                RtlText.CollectCodePoints(FormatPageNumber(1, section.PageNumberFormat), set);
            }
            return set;
        }

        /// <summary>The standard-14 face a family, weight and slant draw in.</summary>
        internal static string StandardBaseFont(bool bold, bool italic, StdFamily fam) => fam switch
        {
            StdFamily.Mono => bold ? (italic ? StandardFonts.CourierBoldOblique : StandardFonts.CourierBold)
                                   : (italic ? StandardFonts.CourierOblique : StandardFonts.Courier),
            StdFamily.Serif => bold ? (italic ? StandardFonts.TimesBoldItalic : StandardFonts.TimesBold)
                                    : (italic ? StandardFonts.TimesItalic : StandardFonts.TimesRoman),
            _ => bold ? (italic ? StandardFonts.HelveticaBoldOblique : StandardFonts.HelveticaBold)
                      : (italic ? StandardFonts.HelveticaOblique : StandardFonts.Helvetica),
        };

        internal static double AdvanceWidth(string text, bool bold, bool italic, double size, StdFamily fam = StdFamily.Sans)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string baseFont = StandardBaseFont(bold, italic, fam);
            // The table is selected ONCE: GetWidthByUnicode resolves the font with an if-chain of up
            // to eleven string comparisons, which per character would reach the same table every
            // time.
            // This branch CANNOT FIRE as the switch above is written: every StdFamily maps to one
            // of the twelve Latin-text Standard-14 names, and TryGetWidthTable returns a table for
            // all twelve (it answers false only for a non-Standard-14 name, or for Symbol and
            // ZapfDingbats, which are reached through their own encodings and which nothing here
            // can select). A mutation of it survives every test, which is the evidence: it is
            // dead, not untested. Kept because it is the correct answer should StdFamily ever gain
            // a member.
            if (!Standard14Metrics.TryGetWidthTable(baseFont, out var widths, out double missing))
                return text.Length * UnknownGlyphWidthUnits / PdfUnits.GlyphSpaceUnitsPerEm * size;

            double units = 0;
            foreach (char c in text)
            {
                double w = widths.TryGetValue(c, out double found) ? found : missing;
                // The per-glyph fallback, unlike the branch above, is live and load-bearing: the
                // proportional families report a missing width of ZERO, so a code point outside
                // their AFM table would otherwise contribute nothing. Width feeds wrapping and
                // every Tm emitted, so a run of such characters would measure as an empty string —
                // the line never wraps, the pen never moves, and every glyph paints at the same x.
                // That is the normal case for non-Latin text before a covering font is resolved.
                // Courier reports a real 600 and never reaches this. MissingGlyphAdvanceTests.
                units += w > 0 ? w : UnknownGlyphWidthUnits;
            }
            return units / PdfUnits.GlyphSpaceUnitsPerEm * size;
        }

        /// <summary>
        /// Parses a <c>#RRGGBB</c> (or <c>#RGB</c>) colour into 0..1 components.
        /// </summary>
        /// <remarks>
        /// <para><b>An unparseable colour is reported, not quietly painted black.</b> In silence,
        /// <c>Shading = "white"</c> would paint a solid black rectangle behind black text. Such
        /// values are plausible for a hand-written <c>DocumentBuilder</c> caller, and the model
        /// documents these properties only as "(hex)".</para>
        /// <para>The <c>#RGB</c> shorthand is accepted (CSS doubles each digit); everything else
        /// falls back to black, because a wrong guess is worse than a documented default —
        /// but it says so through <see cref="BuilderDiagnosticKind.ContentApproximated"/>.</para>
        /// <para>The report is capped per export: this runs once per drawn piece, so an unbounded
        /// report would mint one message per glyph run of a document that sets one bad colour.</para>
        /// </remarks>
        internal static (double r, double g, double b) ParseColorD(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return (0, 0, 0);
            string h = hex!.TrimStart('#');

            if (h.Length == 3 && IsHexRun(h, 3))
                h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });

            if (h.Length < 6 || !IsHexRun(h, 6))
            {
                ReportUnparseableColor(hex!);
                return (0, 0, 0);
            }

            double Ch(int start) =>
                int.TryParse(h.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)
                    ? v / 255.0 : 0.0;
            return (Ch(0), Ch(2), Ch(4));
        }

        private static bool IsHexRun(string s, int count)
        {
            for (int i = 0; i < count; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>Colour-parse reports raised so far on this thread's export.</summary>
        /// <remarks>
        /// <c>[ThreadStatic]</c> like every other diagnostic slot in this repository, and reset at
        /// the start of <see cref="Export"/> so a value never carries between documents.
        /// </remarks>
        [ThreadStatic] private static int _colorReportsThisExport;

        /// <summary>How many distinct colour-parse failures one export reports before going quiet.</summary>
        internal const int MaxColorReportsPerExport = 8;

        private static void ReportUnparseableColor(string value)
        {
            if (_colorReportsThisExport >= MaxColorReportsPerExport) return;
            _colorReportsThisExport++;
            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                $"the colour \"{value}\" is not a #RGB or #RRGGBB value and was painted black; " +
                "the model documents these properties as hex.");
        }

        private static string F(double v) => PdfRealFormat.Format(v, "0.###");
    }
}
