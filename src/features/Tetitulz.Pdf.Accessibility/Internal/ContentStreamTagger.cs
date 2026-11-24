// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// How a run of text on a page should be marked. Produced by
    /// <see cref="PageTagOptions.Classifier"/>, consumed by <see cref="ContentStreamTagger"/>.
    /// </summary>
    internal readonly struct RunClassification
    {
        /// <summary>Structure type for real content — <c>"P"</c>, <c>"H1"</c>…<c>"H6"</c>, <c>"Span"</c>, … Null for artifacts.</summary>
        public string Role { get; }

        /// <summary>True when the run is decoration/pagination and must be excluded from the structure tree.</summary>
        public bool IsArtifact { get; }

        /// <summary>Artifact owner: <c>"Pagination"</c>, <c>"Layout"</c>, <c>"Page"</c> or <c>"Background"</c>.</summary>
        public string ArtifactType { get; }

        /// <summary>Artifact subtype: <c>"Header"</c>, <c>"Footer"</c>, <c>"Watermark"</c>, or null.</summary>
        public string ArtifactSubtype { get; }

        private RunClassification(string role, bool isArtifact, string artifactType, string artifactSubtype)
        {
            Role            = role;
            IsArtifact      = isArtifact;
            ArtifactType    = artifactType;
            ArtifactSubtype = artifactSubtype;
        }

        /// <summary>Classifies the run as tagged content with the given structure type.</summary>
        internal static RunClassification Content(string role) => new RunClassification(role, false, null, null);

        /// <summary>Classifies the run as an artifact (no MCID, never referenced by the structure tree).</summary>
        internal static RunClassification Artifact(string artifactType, string artifactSubtype)
            => new RunClassification(null, true, string.IsNullOrEmpty(artifactType) ? "Layout" : artifactType, artifactSubtype);

        /// <summary>
        /// Leaves the run untouched. Note that untagged, un-artifacted content is a PDF/UA-1
        /// violation (7.1: all content shall be tagged or marked as an artifact) — this exists so
        /// a caller can deliberately opt out of a run it does not understand, not as a default.
        /// </summary>
        internal static RunClassification Unmarked => default;

        /// <summary>True when this classification asks for a marked-content sequence.</summary>
        internal bool IsMarked => IsArtifact || !string.IsNullOrEmpty(Role);
    }

    /// <summary>
    /// One candidate line handed to the classifier. Carries only what a layout heuristic needs;
    /// the tagger owns everything about byte offsets and marked-content mechanics.
    /// </summary>
    /// <remarks>
    /// <b>It carried no horizontal position at all until <see cref="X"/>.</b> That was defensible
    /// while the vocabulary was headings and paragraphs, which a font size decides — and it is
    /// what made every richer inference impossible, because an indented list item, a centred
    /// heading and a table cell are all distinguished by where a line starts and by nothing else a
    /// line-level tagger can see.
    /// </remarks>
    internal sealed class RunCandidate
    {
        /// <summary>Baseline Y of the line in default user space (origin bottom-left, Y grows upward).</summary>
        public float Y { get; set; }

        /// <summary>
        /// X of the line's left edge in default user space, or <see cref="float.NaN"/> when the
        /// content stream's matrices did not yield a finite one.
        /// </summary>
        /// <remarks>
        /// <b>Check it before comparing.</b> A CTM that overflows a <c>float</c> produces a
        /// non-finite X, and every comparison against NaN is false in both directions — so a
        /// heuristic written as <c>if (x &gt; margin)</c> and one written as <c>if (x &lt;= margin)</c>
        /// take opposite branches on the same line. The tagger's own guards use
        /// <c>PageTagOptions.IsFinite</c>.
        /// </remarks>
        public float X { get; set; } = float.NaN;

        /// <summary>Effective font size after Tm × CTM scaling — i.e. the size the reader actually sees.</summary>
        public float FontSize { get; set; }

        /// <summary>Best-effort decoded text. Heuristic only; see <see cref="TaggedRun.Text"/>.</summary>
        public string Text { get; set; }

        /// <summary>Zero-based index of this line among all lines on the page, in content order.</summary>
        public int LineIndex { get; set; }

        /// <summary>Baseline Y of the preceding line, or <see cref="float.NaN"/> for the first line.</summary>
        public float PreviousY { get; set; }

        /// <summary>
        /// Y of the page's <em>upper</em> edge in default user space — see
        /// <see cref="PageTagOptions.PageHeight"/>. 0 when unknown.
        /// </summary>
        public float PageHeight { get; set; }

        /// <summary>
        /// Y of the page's <em>lower</em> edge in default user space — see
        /// <see cref="PageTagOptions.PageBottom"/>. 0 for the ordinary <c>[0 0 w h]</c> page box.
        /// </summary>
        public float PageBottom { get; set; }

        /// <summary>Median body font size used as the denominator for heading ratios.</summary>
        public float MedianFontSize { get; set; }

        /// <summary>
        /// The page-level pass judged this line to be an item of a list. Always false unless
        /// <see cref="PageTagOptions.InferLists"/> is set.
        /// </summary>
        /// <remarks>
        /// <b>A page-level answer delivered through a per-line seam.</b> Whether a line is a list
        /// item depends on its neighbours — on a marker repeating down a shared left edge —
        /// and the classifier is handed one line at a time. Rather than widen that contract, the
        /// tagger runs <see cref="ListLineDetector"/> over every line first and hands each
        /// classifier call the verdict for its own. A custom classifier may ignore it; the default
        /// one turns it into <c>LBody</c>.
        /// </remarks>
        public bool IsListItem { get; set; }

        /// <summary>
        /// Zero-based row of the table cell this run is, or <c>-1</c> when it is not one. Always
        /// <c>-1</c> unless <see cref="PageTagOptions.InferTables"/> is set.
        /// </summary>
        /// <remarks>
        /// <b>A page-level answer delivered through a per-cell seam</b>, the shape
        /// <see cref="IsListItem"/> already established. Whether a line is a table row is a fact
        /// about the column grid its neighbours share, so <see cref="TableGridDetector"/> settles
        /// it for the whole page first; by the time a classifier is called, the row has already
        /// been split into one candidate per cell.
        /// <para>
        /// <b>Row 0 is where the default classifier puts <c>/TH</c>.</b> That is a heuristic, not a
        /// measurement: nothing the tagger records distinguishes a header row from a data row —
        /// weight is not recorded and a rule above the row is not text. A custom classifier that
        /// knows better returns <c>TD</c> for row 0, and one that knows the first <em>column</em>
        /// holds the headers keys off <see cref="TableColumnIndex"/> instead.
        /// </para>
        /// </remarks>
        public int TableRowIndex { get; set; } = -1;

        /// <summary>
        /// Zero-based column of the table cell this run is, or <c>-1</c> when it is not one.
        /// Columns are numbered left to right by the X the cell's ink starts at, <em>not</em> by
        /// the order the content stream draws them in.
        /// </summary>
        public int TableColumnIndex { get; set; } = -1;

        /// <summary>
        /// How many columns this cell's table has, or <c>0</c> when the run is not a cell. The one
        /// value to test: a cell always has a finite row and column, and a non-cell has neither.
        /// </summary>
        public int TableColumnCount { get; set; }

        /// <summary>True when this run is one cell of an inferred table.</summary>
        public bool IsTableCell => TableColumnCount > 0;

        /// <summary>The options the tagger is running with, so a custom classifier can reuse the thresholds.</summary>
        public PageTagOptions Options { get; set; }
    }

    /// <summary>
    /// Inputs for a single page's tagging pass. Classification is injected rather than hardcoded so
    /// that the auto-tagger (headings/paragraphs) and the artifact marker (headers/footers only) can
    /// share one splicing engine without either dictating policy to the other.
    /// </summary>
    internal sealed class PageTagOptions
    {
        /// <summary>
        /// Y coordinate of the page's <em>upper</em> edge in default user space — <c>CropBox[3]</c>
        /// (else <c>MediaBox[3]</c>), <em>not</em> the box's height. For the overwhelmingly common
        /// <c>[0 0 w h]</c> box the two are the same number, which is why the name survives; for any
        /// other box they are not, and the pair that actually locates the header and footer bands is
        /// (<see cref="PageHeight"/>, <see cref="PageBottom"/>). A value that leaves the page zero or
        /// negative height (<c>PageHeight &lt;= PageBottom</c>) means "geometry unknown" and disables
        /// edge-relative decisions altogether.
        /// <para>
        /// <see cref="ContentStreamTagger.TryGetPageVerticalExtent"/> produces both values from a
        /// page; callers should use it rather than reading a box themselves.
        /// </para>
        /// </summary>
        public float PageHeight { get; set; }

        /// <summary>
        /// Y coordinate of the page's <em>lower</em> edge in default user space — <c>CropBox[1]</c>
        /// (else <c>MediaBox[1]</c>). Defaults to 0, which is right for a <c>[0 0 w h]</c> box.
        ///
        /// <para>
        /// <b>Callers whose page box does not start at the origin must set this.</b> ISO 32000-1
        /// §7.7.3.3 lets a page box sit anywhere, including below the origin
        /// (<c>[0 -100 612 692]</c>). The footer band is measured as <c>Y − PageBottom</c>, so
        /// leaving this at 0 for such a page stretches the band from a 50 pt strip to the whole
        /// 150 pt below <c>Y = 50</c> and marks that body text <c>/Artifact /Pagination /Footer</c> —
        /// content that then carries no MCID, gets no structure element, and is unreachable by
        /// assistive technology. Losing content is the one failure this pass must never cause, so
        /// when the extent cannot be read, leave <em>both</em> values at 0: that disables the bands
        /// and merely over-tags a running head as a <c>&lt;P&gt;</c>, which is recoverable.
        /// </para>
        /// </summary>
        public float PageBottom { get; set; }

        /// <summary>Distance from the top of the page within which a line counts as a running header.</summary>
        public float HeaderMargin { get; set; } = 50f;

        /// <summary>Distance from the bottom of the page within which a line counts as a running footer.</summary>
        public float FooterMargin { get; set; } = 50f;

        /// <summary>When false, header/footer lines are tagged as body content instead of artifacts.</summary>
        public bool MarkHeadersFootersAsArtifacts { get; set; } = true;

        /// <summary>
        /// Median body font size assumed when the document supplies none (no measurable text, or a
        /// measuring pass that produced 0). Every heading ratio is relative to this, so it is the
        /// one place the assumption is written down.
        /// </summary>
        internal const float FallbackMedianFontSize = 10f;

        /// <summary>Median body font size on the document. Used as the heading-ratio denominator.</summary>
        public float MedianFontSize { get; set; } = FallbackMedianFontSize;

        /// <summary>Size ratio at or above which a line becomes H1. Mirrors <c>AutoTagOptions.H1Ratio</c>.</summary>
        public float H1Ratio { get; set; } = 1.8f;

        /// <summary>Size ratio at or above which a line becomes H2.</summary>
        public float H2Ratio { get; set; } = 1.4f;

        /// <summary>Size ratio at or above which a line becomes H3.</summary>
        public float H3Ratio { get; set; } = 1.15f;

        /// <summary>
        /// The document's heading sizes, largest first, when headings are levelled by rank
        /// (<c>AutoTagOptions.HeadingLevelsBySize</c>); null for the fixed H1–H3 ratios.
        /// </summary>
        public float[] HeadingSizes { get; set; }

        /// <summary>Vertical gap above which two consecutive paragraph lines start separate paragraphs.</summary>
        public float ParagraphGapThreshold { get; set; } = 6f;

        /// <summary>Baseline-Y tolerance within which two text-showing operators count as the same line.</summary>
        public float SameLineTolerance { get; set; } = 1f;

        /// <summary>First MCID to hand out on this page. MCIDs are per-page and must start at 0 for a fresh page.</summary>
        /// <remarks>
        /// Ignored when <see cref="AdoptExistingMarkedContent"/> completes a page that already
        /// carries marked content: the first free identifier is then one past the greatest the page
        /// already uses, which is a fact about the page rather than a caller's choice.
        /// </remarks>
        public int FirstMcid { get; set; }

        /// <summary>
        /// Complete a page that already carries marked content instead of refusing it.
        /// </summary>
        /// <remarks>
        /// <para><b>Off by default, because it is only safe when the caller knows the existing
        /// MCIDs are unclaimed.</b> Adopting a sequence means writing a structure element that
        /// claims its MCID; if an element in the document's existing tree already claims it, the
        /// page ends up with two owners for one range. The caller holds the tree and this pass does
        /// not, so the decision is the caller's to make and to state.</para>
        ///
        /// <para>Even when on, the page is still refused if its marked content cannot be read
        /// exactly — see <see cref="MarkedContentInventory.Incomplete"/> for the shapes that
        /// count.</para>
        /// </remarks>
        public bool AdoptExistingMarkedContent { get; set; }

        /// <summary>
        /// The stream's <c>/Resources /Properties</c>, so a <c>BDC</c> whose property list is a
        /// name can be resolved. Only read when <see cref="AdoptExistingMarkedContent"/> is set.
        /// </summary>
        public Core.Objects.PdfDictionary MarkedContentProperties { get; set; }

        /// <summary>
        /// The document, for following an indirect property list. Only read when
        /// <see cref="AdoptExistingMarkedContent"/> is set; null simply makes an indirect list
        /// unresolvable, which the inventory reports rather than guesses at.
        /// </summary>
        public Core.Document.PdfDocument Document { get; set; }

        /// <summary>
        /// Ceiling, in bytes, on the <em>decoded</em> content stream this pass will accept. A page
        /// larger than this is refused up front with a
        /// <see cref="PageTagResult.SkipReason"/> instead of being analysed.
        ///
        /// <para>
        /// <b>Why a ceiling is mandatory.</b> Tagging builds three full parallel representations of
        /// the same bytes — the scanner's token list, the production parser's operation list built
        /// during the cross-check, and the re-scan of the spliced copy — so peak memory is a large
        /// multiple of the stream, not a fraction of it. The multiple was measured at roughly 120×
        /// the decoded size in peak working set and ~460× in total allocation, and it is linear.
        /// Core allows a single stream to inflate to <see cref="PdfLimits.MaxDecompressedBytes"/>
        /// (512 MiB) from a few kilobytes of Flate, so without a ceiling a ~2.5 MiB PDF drives one
        /// page to tens of gigabytes and minutes of CPU — per page. The bill is paid even when the
        /// page turns out to have no text at all and is skipped.
        /// </para>
        ///
        /// <para>
        /// A value of <c>0</c> or less disables the ceiling. That is only for a caller that has
        /// already bounded the input itself; the default
        /// (<see cref="ContentStreamTagger.DefaultMaxContentBytes"/>) is what every caller in this
        /// assembly uses.
        /// </para>
        /// </summary>
        public int MaxContentBytes { get; set; } = ContentStreamTagger.DefaultMaxContentBytes;

        /// <summary>
        /// Ceiling, in bytes, on the total <em>decoded</em> content
        /// <see cref="ContentStreamTagger.ConcatenatePageContent(PdfDocument, PdfDictionary, long)"/> will assemble for this page —
        /// the ceiling that applies <em>before</em> <see cref="MaxContentBytes"/>, because it bounds
        /// the decode itself rather than the analysis that follows it.
        ///
        /// <para>
        /// It is carried here, next to the analysis ceiling, so that one options object threaded
        /// from the public <c>AutoTagOptions</c> configures both halves of the page's memory bill.
        /// A value of <c>0</c> or less means "use
        /// <see cref="ContentStreamTagger.MaxConcatenatedContentBytes"/>"; there is deliberately no
        /// way to ask for an unbounded concatenation.
        /// </para>
        /// </summary>
        public long MaxConcatenatedContentBytes { get; set; } = ContentStreamTagger.MaxConcatenatedContentBytes;

        /// <summary>
        /// Classifier. When null, <see cref="ContentStreamTagger.DefaultClassifier"/> is used, which
        /// reproduces the heuristics <c>PdfAutoTagger</c> already applies.
        /// </summary>
        public Func<RunCandidate, RunClassification> Classifier { get; set; }

        /// <summary>
        /// Run <see cref="ListLineDetector"/> over the page and set
        /// <see cref="RunCandidate.IsListItem"/>. Off by default.
        /// </summary>
        /// <remarks>
        /// <b>Off by default because a wrongly-inferred list is worse than none.</b> It puts an
        /// <c>/L</c> into the tree that a screen reader announces as a list of the wrong length,
        /// and this repository's own deep checker then reports the shape — remediation
        /// manufacturing the violation it exists to remove. A caller opts in.
        /// </remarks>
        public bool InferLists { get; set; }

        /// <summary>
        /// Run <see cref="TableGridDetector"/> over the page and fill
        /// <see cref="RunCandidate.TableRowIndex"/>, <see cref="RunCandidate.TableColumnIndex"/>
        /// and <see cref="RunCandidate.TableColumnCount"/>. Off by default.
        /// </summary>
        /// <remarks>
        /// <para><b>Off by default for the reason <see cref="InferLists"/> is, and more so.</b> A
        /// table has a required internal shape — <c>/TR</c> containing only <c>/TD</c> and
        /// <c>/TH</c>, every row the same width — and this repository's own UA12 check reports a
        /// table that fails it as an <b>Error</b> rather than a warning. A caller opts in.</para>
        ///
        /// <para><b>It changes the granularity of marked content, which nothing else here does.</b>
        /// Every other classification decision is taken per line; a table row is split into one
        /// marked-content sequence <em>per cell</em>, so a page whose text objects mix cells with
        /// <c>q</c>/<c>Q</c> can now hit the existing "a text object mixes several structure roles
        /// and also pushes/pops the graphics state" refusal where the same page tagged as
        /// paragraphs would not.</para>
        /// </remarks>
        public bool InferTables { get; set; }

        /// <summary>
        /// Names of the XObjects on this page whose <c>/Subtype</c> is <c>/Image</c>. Null (the
        /// default) means figures are not inferred at all.
        /// </summary>
        /// <remarks>
        /// <b>The tagger is handed raw content bytes and has no resources</b>, so it cannot tell a
        /// <c>/Im0 Do</c> that draws an image from one that draws a Form XObject — and wrapping a
        /// form as a <c>/Figure</c> would declare a page of text to be a picture. Resolving
        /// <c>/Resources /XObject</c> is the caller's job because the caller is the one holding the
        /// document; this is the answer coming back.
        /// </remarks>
        public System.Collections.Generic.ISet<string> ImageXObjectNames { get; set; }

        /// <summary>
        /// Asked for the alternate text of each image, by XObject name. Returning null or empty
        /// means <b>do not tag that image at all</b>.
        /// </summary>
        /// <remarks>
        /// <b>Declining is the whole design, not a fallback.</b> ISO 14289-1 §7.3 requires a
        /// <c>/Figure</c> to carry an <c>/Alt</c>, and this library's own UA07 check reports one
        /// that does not — so a tagger that emitted <c>/Figure</c> and left the text to a later
        /// pass would manufacture the finding it exists to remove, on every image, for however long
        /// it took the caller to run that pass. Nothing here can describe a picture; the caller
        /// can, or can say it cannot, and an untagged image is exactly the state the page was
        /// already in.
        /// </remarks>
        public Func<string, string> FigureAlternateText { get; set; }

        /// <summary>
        /// Alternate text for an inline image (<c>BI…EI</c>), given its zero-based position among the
        /// stream's inline images and its parameter dictionary, or <c>null</c> to leave it untagged —
        /// <see cref="FigureAlternateText"/>'s contract for the images a stream draws in place.
        /// </summary>
        public Func<int, PdfDictionary, string> InlineImageAlternateText { get; set; }
    }

    /// <summary>
    /// One marked-content sequence the tagger emitted (or would emit). Each run corresponds to
    /// exactly one <c>BDC…EMC</c> pair and, for content runs, exactly one MCID.
    /// </summary>
    internal sealed class TaggedRun
    {
        /// <summary>Structure type (<c>"P"</c>, <c>"H1"</c>…) for content runs; null for artifacts.</summary>
        public string Role;

        /// <summary>True when this run was emitted as <c>/Artifact … BDC</c>.</summary>
        public bool IsArtifact;

        /// <summary>Artifact owner (<c>"Pagination"</c>, <c>"Layout"</c>, …); null for content runs.</summary>
        public string ArtifactType;

        /// <summary>Artifact subtype (<c>"Header"</c>, <c>"Footer"</c>, …); may be null.</summary>
        public string ArtifactSubtype;

        /// <summary>
        /// MCID assigned to this run, or <c>-1</c> for artifacts. Artifacts consume no MCID because
        /// they are never referenced from the structure tree or the page's ParentTree entry.
        /// </summary>
        public int Mcid = -1;

        /// <summary>Baseline Y of the run's first line, in default user space.</summary>
        public float Y;

        /// <summary>Largest effective font size in the run — what heading detection keys off.</summary>
        public float FontSize;

        /// <summary>
        /// Best-effort decoded text. String operands hold <em>font codes</em>, not Unicode, so this
        /// is only correct for simple Latin-encoded fonts. It exists for logging, for the caller's
        /// <c>/ActualText</c>, and for heuristics — the correctness of the marked content never
        /// depends on it.
        /// </summary>
        public string Text;

        /// <summary>
        /// Start of the byte range wrapped by this run, as an offset into the <em>original</em>
        /// (pre-splice) content. For a text object with a single run this is the offset of its
        /// <c>BT</c>; otherwise the offset of the run's first text-showing operator.
        /// </summary>
        public int TokenStart;

        /// <summary>Exclusive end of the wrapped byte range in the original content.</summary>
        public int TokenEnd;

        /// <summary>
        /// Identifier grouping runs that belong to the same logical paragraph. A paragraph that
        /// spans several text objects produces several runs (one marked-content sequence each,
        /// one MCID each) sharing a ParagraphId; the caller emits <em>one</em> <c>/P</c> structure
        /// element whose <c>/K</c> is the array of those MCIDs. This is exactly how real tagged
        /// PDFs represent a multi-line paragraph, and it avoids the far riskier alternative of
        /// stretching one marked-content sequence across <c>BT</c>/<c>ET</c> boundaries.
        /// </summary>
        public int ParagraphId = -1;

        /// <summary>
        /// Identifier of the inferred table this run is a cell of, unique within its page, or
        /// <c>-1</c>. Paired with <see cref="TableRowIndex"/> it is everything the caller needs to
        /// rebuild <c>Table</c> → <c>TR</c> → <c>TD</c>: cells of one table are contiguous in page
        /// order, and a change of row index starts a new <c>/TR</c>.
        /// </summary>
        public int TableId = -1;

        /// <summary>Zero-based row of this cell within its table, or <c>-1</c>.</summary>
        public int TableRowIndex = -1;

        /// <summary>
        /// Name of the image XObject this run wraps, or null when the run is not an inferred
        /// figure. Carried so a caller can relate the element back to the resource it describes.
        /// </summary>
        public string FigureXObjectName;

        /// <summary>
        /// The <c>/Alt</c> this run must carry. Non-null exactly when
        /// <see cref="FigureXObjectName"/> is: an inferred figure without alternate text is never
        /// emitted, so there is no state in which one is set and the other is not.
        /// </summary>
        public string AlternateText;

        public override string ToString()
            => (IsArtifact ? "Artifact/" + ArtifactType + "/" + (ArtifactSubtype ?? "-") : Role + " mcid=" + Mcid)
             + " y=" + Y.ToString("F1", CultureInfo.InvariantCulture)
             + " size=" + FontSize.ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>Result of tagging one page's content stream.</summary>
    internal sealed class PageTagResult
    {
        /// <summary>
        /// The spliced, <em>uncompressed</em> content stream. When <see cref="Changed"/> is false
        /// this is the original array, unmodified.
        /// </summary>
        public byte[] NewContent;

        /// <summary>Runs actually marked, in content order. Empty when the page was skipped.</summary>
        public IReadOnlyList<TaggedRun> Runs = ContentStreamTagger.EmptyRuns;

        /// <summary>
        /// Next free MCID for this page — <c>FirstMcid</c> plus the number of content runs marked.
        /// The caller writes this (per page) into the page's structure bookkeeping.
        /// </summary>
        public int NextMcid;

        /// <summary>True only when <see cref="NewContent"/> differs from the input and passed validation.</summary>
        public bool Changed;

        /// <summary>
        /// Why the page was left untouched, or null when it was tagged. Always populated when
        /// <see cref="Changed"/> is false so a caller can surface an honest warning instead of
        /// silently claiming success.
        /// </summary>
        public string SkipReason;

        /// <summary>
        /// True when the page invokes XObjects. Text drawn inside a Form XObject lives in a
        /// different stream and is <em>not</em> tagged by this pass; callers should warn.
        /// </summary>
        /// <remarks>
        /// <b>This is necessary but not sufficient for "partially tagged"</b> — it is true for an
        /// <i>image</i> XObject too, and an image carries no text to tag. Use
        /// <see cref="InvokedXObjectNames"/> to resolve what was actually invoked; the caller has
        /// the document and the page resources, which this method does not.
        /// </remarks>
        public bool ContainsXObjectInvocations;

        /// <summary>
        /// Resource names passed to <c>Do</c>, in first-seen order, so a caller holding the page's
        /// <c>/Resources</c> can tell a Form XObject from an image.
        /// </summary>
        /// <remarks>
        /// Recorded here rather than resolved here because <c>TagPage</c> takes bytes and options —
        /// it has no <c>PdfDocument</c>. Splitting it this way keeps the tagger byte-only and puts
        /// the resolution where the information already is.
        /// </remarks>
        public IReadOnlyList<string> InvokedXObjectNames = System.Array.Empty<string>();

        /// <summary>
        /// Of <see cref="InvokedXObjectNames"/>, the names a <c>Do</c> inside one of the page's existing
        /// <c>/Artifact</c> sequences passes: what such a form draws is artifact content, not content
        /// to tag (§14.8.2.2).
        /// </summary>
        public IReadOnlyCollection<string> NamesInsideArtifacts = System.Array.Empty<string>();

        /// <summary>
        /// Of <see cref="InvokedXObjectNames"/>, the names a <c>Do</c> inside one of the page's existing
        /// non-artifact sequences passes — or every name, when the page's marked content could not be
        /// read: what such a form draws belongs to that sequence's owner (§14.7.5.1 admits no structure
        /// content item inside another), and tagging it would give the glyphs two owners.
        /// </summary>
        public IReadOnlyCollection<string> NamesInsideSequences = System.Array.Empty<string>();

        /// <summary>Of <see cref="InvokedXObjectNames"/>, the names a <c>Do</c> outside every existing sequence passes.</summary>
        public IReadOnlyCollection<string> NamesOutsideSequences = System.Array.Empty<string>();
    }

    /// <summary>
    /// Rewrites a page content stream so its text carries real marked content
    /// (<c>BDC</c>/<c>EMC</c> with MCIDs), which is the missing half of "tagging a PDF".
    ///
    /// <para>
    /// <b>Why this exists.</b> A structure tree whose elements have no <c>/K</c> pointing at
    /// content is theatre: the objects exist, an audit can see <c>/S /P</c> and <c>/ActualText</c>,
    /// and a screen reader still gets nothing, because assistive technology reaches content
    /// through marked-content sequences, not through element attributes. independent checkers fail
    /// such a file outright. Producing MCIDs means touching the content stream — which is the
    /// riskiest edit in a PDF library, hence the deliberately paranoid design below.
    /// </para>
    ///
    /// <para><b>Design rules, in order of importance:</b></para>
    /// <list type="number">
    ///   <item><description><b>Splice, never re-serialise.</b> The output is the input bytes with
    ///     short ASCII fragments inserted at operation boundaries found by
    ///     <see cref="ContentStreamScanner"/>. Every byte outside an insertion point is copied
    ///     verbatim, so rendering is bit-for-bit identical. Re-emitting a parsed operation list
    ///     would silently drop inline-image payloads and any operator the parser does not model.
    ///     </description></item>
    ///   <item><description><b>Refuse rather than risk it.</b> Existing marked content, a
    ///     <c>BX</c>/<c>EX</c> compatibility section, an unbounded construct, a text object that
    ///     nests <c>q</c>/<c>Q</c> in a way our wrap cannot enclose, a disagreement with the
    ///     production parser — every one of these returns the original bytes plus a
    ///     <see cref="PageTagResult.SkipReason"/>.</description></item>
    ///   <item><description><b>Bounded work per page.</b> The pass costs a large multiple of the
    ///     stream it is given (three parallel token/operation representations of the same bytes),
    ///     and Core lets a single stream inflate to 512 MiB from a few kilobytes of Flate, so a
    ///     page above <see cref="PageTagOptions.MaxContentBytes"/> is refused <em>before</em> the
    ///     first scan — the only point at which refusing is free — and
    ///     <see cref="ConcatenatePageContent(PdfDocument, PdfDictionary, long)"/> caps the total it will ever assemble for one page.
    ///     </description></item>
    ///   <item><description><b>Prove the result before returning it.</b> The spliced bytes are
    ///     re-scanned and checked: <c>BT</c>/<c>ET</c> balanced and not nested, <c>BDC</c>/<c>EMC</c>
    ///     balanced and never straddling a text-object boundary, and — the strongest check — the
    ///     operator sequence with our <c>BDC</c>/<c>EMC</c> removed must equal the original operator
    ///     sequence exactly. If anything fails, the original bytes are returned.</description></item>
    ///   <item><description><b>Nesting is constructed, not hoped for.</b> A marked-content sequence
    ///     is placed either strictly around a whole <c>BT…ET</c> or strictly inside one, spanning
    ///     text-showing operators. It never begins inside a text object and ends outside it.
    ///     </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Out of scope by design:</b> glyph widths and word segmentation (position and size are all
    /// the layout heuristics need), and reading-order re-ordering.
    /// </para>
    ///
    /// <para>
    /// <b>Form XObjects are out of scope for this type only, not for the tagger.</b> It takes bytes
    /// rather than a page, so the form path runs it over a form's decoded content unchanged; which
    /// streams get tagged, and whether a shared form is duplicated first, is decided one layer up
    /// in <c>PdfAutoTagger.TagForms</c> and <c>SharedFormDuplicator</c>. The names a stream invokes
    /// are reported here through <see cref="PageTagResult.InvokedXObjectNames"/> so that layer need
    /// not decode anything twice.
    /// </para>
    /// </summary>
    internal static class ContentStreamTagger
    {
        internal static readonly IReadOnlyList<TaggedRun> EmptyRuns = new TaggedRun[0];

        /// <summary>
        /// Ceiling on <c>q</c> nesting while walking a content stream. The value is the
        /// repository-wide <see cref="PdfLimits.MaxGraphicsStateStackDepth"/>, which
        /// <c>PdfReadingOrderAnalyzer.MaxContentStackDepth</c> also uses. No real producer nests
        /// graphics state anywhere near this deep; the cap exists so an unbalanced or adversarial
        /// stream cannot grow the stack without bound.
        /// </summary>
        private static int MaxGraphicsStackDepth => PdfLimits.MaxGraphicsStateStackDepth;

        /// <summary>
        /// Default ceiling on the decoded bytes of one page's content stream — see
        /// <see cref="PageTagOptions.MaxContentBytes"/> for why a ceiling has to exist at all.
        ///
        /// <para>
        /// 4 MiB is chosen from the measured cost of the pass, which is linear at roughly 1.3 s and
        /// ~120× the stream size in peak working set per megabyte: 4 MiB therefore bounds one page
        /// at a few seconds and a few hundred megabytes, which is survivable, while 16 MiB would
        /// already cost ~20 s and ~2 GiB. It is also far above any real text page — a text-heavy A4
        /// page decodes to tens of kilobytes, and even a dense multi-column page stays under a
        /// megabyte — so refusing above it costs no realistic document its tags. Pages that do
        /// exceed it are vector art or scans, whose text (if any) is usually inside Form XObjects
        /// that this pass does not rewrite in the first place.
        /// </para>
        ///
        /// <para>
        /// Refusing is the honest outcome, not a silent one: the page keeps its original bytes and
        /// the caller gets a <see cref="PageTagResult.SkipReason"/> naming both numbers, which
        /// surfaces as a remediation warning rather than as a claim that the page was tagged.
        /// </para>
        /// </summary>
        internal const int DefaultMaxContentBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Ceiling on the total decoded bytes <see cref="ConcatenatePageContent(PdfDocument, PdfDictionary, long)"/> will assemble for
        /// one page. Core caps a <em>single</em> stream at <see cref="PdfLimits.MaxDecompressedBytes"/>,
        /// but <c>/Contents</c> may be an array of arbitrarily many streams, so the concatenation
        /// itself is otherwise unbounded: forty 512 MiB parts ask for a 20 GiB array, and merely
        /// crossing <c>int.MaxValue</c> would overflow <c>int</c> length arithmetic and throw out of
        /// a method whose documented failure mode is a <c>null</c> return. Matching Core's per-stream
        /// ceiling keeps the assembly from ever materialising more for one page than Core would allow
        /// for one stream.
        /// </summary>
        internal static long MaxConcatenatedContentBytes => PdfLimits.MaxDecompressedBytes;

        /// <summary>
        /// Reason the most recent tagging attempt failed unexpectedly (an exception, or a page
        /// content stream that would not decode); null when the last call behaved normally.
        /// A deliberate refusal is <em>not</em> reported here — that is
        /// <see cref="PageTagResult.SkipReason"/>. <c>[ThreadStatic]</c> so concurrent pages do
        /// not overwrite each other's diagnostics.
        /// </summary>
        [ThreadStatic]
        private static string _lastTagError;

        /// <summary>Last unexpected tagging failure on this thread, or null.</summary>
        internal static string LastTagError => _lastTagError;

        /// <summary>Inserted after the last operator of a marked run. Never mutated.</summary>
        private static readonly byte[] CloseSequence = Encoding.ASCII.GetBytes("\nEMC\n");

        // ================================================================
        //  Public entry point
        // ================================================================

        /// <summary>
        /// Tags one page. Returns the spliced content plus the runs that were marked, or the
        /// original content plus a <see cref="PageTagResult.SkipReason"/> when the page could not
        /// be tagged safely. Never throws and never returns a stream it has not validated.
        /// </summary>
        /// <param name="originalContent">
        /// The page's <em>decoded</em> content stream — use <see cref="ConcatenatePageContent(PdfDocument, PdfDictionary, long)"/>
        /// to obtain it. Passing still-Flate-compressed bytes would be scanned as garbage and
        /// (correctly) refused, but there is no way to detect that here, so decode first.
        /// </param>
        /// <param name="options">Page geometry, thresholds and the injected classifier.</param>
        internal static PageTagResult TagPage(byte[] originalContent, PageTagOptions options)
        {
            _lastTagError = null;
            options = options ?? new PageTagOptions();

            var result = new PageTagResult
            {
                NewContent = originalContent ?? new byte[0],
                Runs       = EmptyRuns,
                NextMcid   = options.FirstMcid,
                Changed    = false,
            };

            if (originalContent == null || originalContent.Length == 0)
            {
                result.SkipReason = "Page has no content-stream bytes.";
                return result;
            }

            try
            {
                return TagPageCore(originalContent, options, result);
            }
            catch (Exception ex)
            {
                // Any escape hatch taken here must leave the page exactly as it was found.
                _lastTagError    = ex.ToString();
                AccessibilityDiagnostics.Report("ContentStreamTagger.TagPage",
                    "an unexpected failure aborted tagging; the page was left unmodified", ex);
                result.NewContent = originalContent;
                result.Runs       = EmptyRuns;
                result.NextMcid   = options.FirstMcid;
                result.Changed    = false;
                result.SkipReason = "Tagging aborted after an unexpected failure: " + ex.Message;
                return result;
            }
        }

        /// <summary>
        /// The distinct resource names given to <c>Do</c>, in first-seen order.
        /// </summary>

        private static PageTagResult TagPageCore(byte[] content, PageTagOptions options, PageTagResult result)
        {
            // ---- 0. refuse an oversized page before touching a single byte -------
            // Everything below this line is at least three passes over the stream, each of which
            // materialises its own object-per-token representation, so the work is a large multiple
            // of the input rather than a fraction of it. The check has to come first: it is the only
            // point at which refusing is free. See PageTagOptions.MaxContentBytes.
            int maxContentBytes = options.MaxContentBytes;
            if (maxContentBytes > 0 && content.Length > maxContentBytes)
            {
                result.SkipReason = "Page content stream is " +
                                    content.Length.ToString(CultureInfo.InvariantCulture) +
                                    " bytes, past the " +
                                    maxContentBytes.ToString(CultureInfo.InvariantCulture) +
                                    "-byte tagging limit; analysing it would cost a multiple of that in " +
                                    "memory and time, so the page was left unmodified and untagged.";
                return result;
            }

            // ---- 1. scan, and bail out on anything we cannot reason about --------
            ContentScanResult scan = ContentStreamScanner.ScanDetailed(content);
            result.ContainsXObjectInvocations = scan.XObjectInvocations > 0;
            result.InvokedXObjectNames = scan.InvokedXObjectNames();

            if (!scan.IsSafe)
            {
                result.SkipReason = "Content stream could not be scanned safely: " + scan.Error;
                return result;
            }
            MarkedContentInventory existing = null;
            if (scan.HasMarkedContent)
            {
                // Marked content this pass cannot read is marked content a form drawn on the page may
                // belong to: every form the page invokes is owned until known otherwise.
                result.NamesInsideSequences = result.InvokedXObjectNames;

                if (!options.AdoptExistingMarkedContent)
                {
                    result.SkipReason = "Page already contains marked content (BDC/BMC/EMC); "
                                      + "adding a second, overlapping layer would corrupt the existing structure.";
                    return result;
                }

                existing = MarkedContentInventory.Read(
                    scan.Tokens, options.MarkedContentProperties, options.Document);

                if (existing.Incomplete != null)
                {
                    result.SkipReason = "Page already contains marked content that could not be completed: "
                                      + existing.Incomplete + ".";
                    return result;
                }

                ClassifyInvocationsBySequence(scan.Tokens, existing, result);
            }
            else
            {
                result.NamesOutsideSequences = result.InvokedXObjectNames;
            }
            if (scan.HasCompatibilitySection)
            {
                result.SkipReason = "Page contains a BX/EX compatibility section; the operators inside it are "
                                  + "undefined for this scanner, so their operand boundaries cannot be trusted.";
                return result;
            }
            if (scan.HasUncertainInlineImage)
            {
                // The end of the image's samples was located by heuristic, not computed. If that
                // guess is one byte early, every offset after it lands inside pixel data and a
                // splice writes BDC/EMC bytes into the image — corrupting what the page renders.
                // The cross-check below cannot catch it: Core's parser uses the same heuristic and
                // mis-bounds identically, so the two agree precisely when both are wrong.
                result.SkipReason = "Page contains an inline image whose payload length could not be determined "
                                  + "exactly (no /L, and the image is filtered or its geometry is not readable), "
                                  + "so the end of its binary samples is only a guess; splicing at offsets derived "
                                  + "from a guess risks writing marked-content operators into the image data.";
                return result;
            }

            IReadOnlyList<ContentToken> tokens = scan.Tokens;

            // ---- 2. walk the stream, collecting text objects and their lines -----
            string walkError;
            List<TextObjectInfo> textObjects = BuildTextObjects(tokens, options, out walkError);
            if (walkError != null)
            {
                result.SkipReason = walkError;
                return result;
            }

            var lines = new List<LineGroup>();
            for (int i = 0; i < textObjects.Count; i++)
                lines.AddRange(textObjects[i].Lines);

            // A page being completed may legitimately have no text left to classify: every text
            // object it has is already inside somebody's sequence, or the marked content wraps an
            // image rather than words. Its sequences are still worth adopting, so "nothing to
            // classify" only ends the pass when there is also nothing to adopt.
            bool hasAdoptable = false;
            if (existing != null)
            {
                foreach (MarkedContentInventory.Sequence seq in existing.Sequences)
                    if (!seq.IsArtifact && seq.Mcid >= 0) { hasAdoptable = true; break; }
            }

            if (lines.Count == 0 && !hasAdoptable)
            {
                result.SkipReason = "Page contains no text-showing operators to tag"
                                  + (result.ContainsXObjectInvocations
                                        ? " (its text may live inside a Form XObject, which this pass does not rewrite)."
                                        : ".");
                return result;
            }

            // ---- 2b. split the rows of any inferred table into cells -------------
            // A table is the one structure whose marked content has to be finer than a line: a row
            // of three cells drawn inside one text object must become three sequences, not one.
            // The grid is settled for the whole page first, then every line it named is replaced —
            // inside its OWN text object's list, because that is the list the splice later reads —
            // by one line per show operator. Skipped entirely when the caller has not asked for
            // tables, so the ordinary pass allocates nothing and sees no extra roles.
            if (options.InferTables && lines.Count > 0)
                lines = SplitTableCells(textObjects, lines, options);

            // ---- 3. classify every line, then group P lines into paragraphs ------
            Func<RunCandidate, RunClassification> classifier = options.Classifier;
            if (classifier == null) classifier = DefaultClassifier;
            ClassifyLines(lines, options, classifier);
            AssignParagraphIds(lines, options);

            // ---- 4. build runs per text object ----------------------------------
            var runs = new List<TaggedRun>();
            var insertions = new List<Insertion>();

            // One past whatever the page already uses, which is the whole reason a page carrying
            // marked content need not be refused: the identifiers only collide if you start at
            // zero.
            int mcid = existing != null ? existing.MaxMcid + 1 : options.FirstMcid;
            int sequence = 0;

            for (int t = 0; t < textObjects.Count; t++)
            {
                TextObjectInfo to = textObjects[t];

                // Already inside somebody's marked-content sequence: leave the bytes alone. The
                // sequence itself becomes a run below, so the content is still reachable — it is
                // reached through the identifier its producer chose rather than one of ours.
                if (existing != null && EnclosingSequence(existing, to.BtStart) != null) continue;

                // A sequence the producer opened INSIDE this text object (tagpdf and others write
                // BDC after BT). ISO 32000-2 §14.7.5.1: a structure content item shall not have
                // another nested within it — so the object is never wrapped whole around one, and a
                // line sharing bytes with one is left to its producer (adopted below when it carries
                // an MCID). Judged by its first byte, such an object would look unmarked: wrapped
                // whole, or its already-marked line wrapped a second time inside the producer's
                // sequence. Both balance; both put one run of glyphs under two MCIDs.
                bool holdsSequence = existing != null && LeaveLinesInsideSequencesToTheirProducer(existing, to);

                List<RunSegment> segments = BuildRunSegments(to);

                int markedSegments = 0;
                for (int s = 0; s < segments.Count; s++)
                    if (segments[s].Classification.IsMarked) markedSegments++;
                if (markedSegments == 0) continue;

                // A wrap that encloses the whole text object also encloses whatever q/Q pairs the
                // object contains, so it stays properly nested as long as those pairs balance
                // inside it. Splitting *within* an object that pushes/pops the graphics state
                // could put a lone q or Q inside one of our sequences, which violates the
                // nesting requirement of ISO 32000-1 §8.4.1 — refuse instead.
                bool wholeObjectWrap = segments.Count == 1 && !holdsSequence;
                if (to.HasGraphicsStateOps)
                {
                    if (!wholeObjectWrap)
                    {
                        result.SkipReason = "A text object mixes several structure roles"
                                          + (holdsSequence ? " or already carries a marked-content sequence," : "")
                                          + " and also pushes/pops the graphics state (q/Q); a split "
                                          + "marked-content sequence there could not be guaranteed properly nested.";
                        return result;
                    }
                    if (to.GraphicsStateNet != 0 || to.GraphicsStateMin < 0)
                    {
                        result.SkipReason = "A text object contains unbalanced q/Q operators; wrapping it in "
                                          + "marked content would produce an improperly nested content stream.";
                        return result;
                    }
                }

                for (int s = 0; s < segments.Count; s++)
                {
                    RunSegment seg = segments[s];
                    if (!seg.Classification.IsMarked) continue;

                    var run = new TaggedRun
                    {
                        Role            = seg.Classification.Role,
                        IsArtifact      = seg.Classification.IsArtifact,
                        ArtifactType    = seg.Classification.ArtifactType,
                        ArtifactSubtype = seg.Classification.ArtifactSubtype,
                        Y               = seg.Y,
                        FontSize        = seg.FontSize,
                        Text            = seg.Text,
                        ParagraphId     = seg.ParagraphId,
                        TableId         = seg.TableId,
                        TableRowIndex   = seg.TableRowIndex,
                        TokenStart      = wholeObjectWrap ? to.BtStart : seg.StartOffset,
                        TokenEnd        = wholeObjectWrap ? to.EtEnd   : seg.EndOffset,
                    };

                    if (!run.IsArtifact)
                    {
                        if (!IsWritableName(run.Role))
                        {
                            result.SkipReason = "Classifier returned a structure role that cannot be written as a "
                                              + "PDF name: '" + (run.Role ?? "<null>") + "'.";
                            return result;
                        }
                        run.Mcid = mcid++;
                    }
                    else
                    {
                        if (!IsWritableName(run.ArtifactType)
                            || (run.ArtifactSubtype != null && !IsWritableName(run.ArtifactSubtype)))
                        {
                            result.SkipReason = "Classifier returned an artifact type/subtype that cannot be written "
                                              + "as a PDF name.";
                            return result;
                        }
                    }

                    runs.Add(run);
                    insertions.Add(new Insertion(run.TokenStart, InsertionKind.Open,  BuildOpenSequence(run), sequence++));
                    insertions.Add(new Insertion(run.TokenEnd,   InsertionKind.Close, CloseSequence,          sequence++));
                }
            }

            // ---- 4a. images, for a caller that can describe them -----------------
            if ((options.ImageXObjectNames != null && options.ImageXObjectNames.Count > 0
                 && options.FigureAlternateText != null)
                || options.InlineImageAlternateText != null)
            {
                CollectFigureRuns(tokens, textObjects, existing, options, runs, insertions,
                                  ref mcid, ref sequence);
            }

            if (existing != null)
            {
                string adoptError = AdoptExistingSequences(existing, textObjects, runs);
                if (adoptError != null) { result.SkipReason = adoptError; return result; }
            }

            // Content order, so the caller emits elements in the order a reader meets them.
            //
            // **Unconditional.** Completing a page that already carries marked content is one way
            // two sources of runs interleave; a figure is another, and interleaves with the first:
            // an image drawn above a paragraph is collected after every text object, and without
            // this it would be read out after all of them — a picture announced at the end of the
            // page it sits at the top of. On a text-only page the list is already in this order and
            // the sort does nothing, which is why it can simply always run.
            //
            // The MCIDs themselves are NOT renumbered to match. A run adopted from the page keeps
            // the identifier its producer chose (that is what adopting means), so monotonic MCIDs
            // are not available on every page and a rule that holds only sometimes is worse than
            // none. Nothing requires them: §14.7.4.4 makes the page's /ParentTree entry an array
            // INDEXED by MCID, and reading order is the /K order this sort produces.
            //
            // `List<T>.Sort` is UNSTABLE, so a tie would leave reading order unspecified. No two
            // runs can tie, and it is worth writing down which cases were checked: two segments of
            // one text object start at different shows; a whole-object wrap is the object's only
            // run; two text objects have different BT offsets; a figure's `Do` is refused if it
            // falls inside any BT…ET, so it cannot share a start with a text run; and both a text
            // run and a figure inside an existing marked-content sequence are skipped before they
            // get here, so an adopted run's BDC offset collides with neither. A stabiliser would
            // therefore be a guard against a hazard that cannot occur — inert, and untestable.
            runs.Sort((a, b) => a.TokenStart.CompareTo(b.TokenStart));

            if (runs.Count == 0)
            {
                result.SkipReason = "No line on the page was classified as taggable content or as an artifact.";
                return result;
            }

            // ---- 4b. cross-check the scanner against the production parser --------
            // This re-parses the whole stream a second time, into a heavier representation than the
            // scanner's, so it is deliberately the last thing done before the splice rather than the
            // first thing done after the scan: it only guards *offsets we are about to write at*,
            // and a page we are not going to modify (no text, nothing classifiable, an unbalanced
            // text object) must not pay for it. That ordering is what keeps a page of pure vector
            // operators to one pass instead of two.
            string disagreement = CrossCheckWithCoreParser(content, tokens);
            if (disagreement != null)
            {
                result.SkipReason = "Byte scanner disagrees with the production content-stream parser (" +
                                    disagreement + "); refusing to splice.";
                result.Runs       = EmptyRuns;
                result.NextMcid   = options.FirstMcid;
                return result;
            }

            // ---- 5. splice --------------------------------------------------------
            byte[] spliced = Splice(content, insertions);

            // ---- 6. prove the result before handing it back -----------------------
            string invalid = ValidateSplicedStream(spliced, tokens, insertions.Count);
            if (invalid != null)
            {
                _lastTagError     = "Post-splice validation failed: " + invalid;
                AccessibilityDiagnostics.Report("ContentStreamTagger.TagPage",
                    "the spliced content stream failed its balance check (" + invalid +
                    "); the page was left unmodified");
                result.NewContent = content;
                result.Runs       = EmptyRuns;
                result.NextMcid   = options.FirstMcid;
                result.Changed    = false;
                result.SkipReason = "Spliced content stream failed its balance check (" + invalid +
                                    "); the page was left unmodified.";
                return result;
            }

            result.NewContent = spliced;
            result.Runs       = runs;
            result.NextMcid   = mcid;
            result.Changed    = true;
            result.SkipReason = null;
            return result;
        }

        /// <summary>
        /// Sorts the page's <c>Do</c> operands by the existing sequence each stands in — an artifact,
        /// another sequence, none — into <see cref="PageTagResult.NamesInsideArtifacts"/>,
        /// <see cref="PageTagResult.NamesInsideSequences"/> and <see cref="PageTagResult.NamesOutsideSequences"/>,
        /// so the form tagger can tell a form drawn as a watermark's artifact from one drawn as content.
        /// A name drawn in two places is in two sets.
        /// </summary>
        private static void ClassifyInvocationsBySequence(
            IReadOnlyList<ContentToken> tokens, MarkedContentInventory existing, PageTagResult result)
        {
            var artifacts = new HashSet<string>(StringComparer.Ordinal);
            var sequences = new HashSet<string>(StringComparer.Ordinal);
            var outside = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Count; i++)
            {
                ContentToken token = tokens[i];
                if (!string.Equals(token.Operator, PdfOperators.InvokeXObject, StringComparison.Ordinal)) continue;
                if (token.Operands == null || token.Operands.Count == 0) continue;
                if (!(token.Operands[token.Operands.Count - 1] is PdfName name)) continue;

                MarkedContentInventory.Sequence sequence = EnclosingSequence(existing, token.StartOffset);
                if (sequence == null) outside.Add(name.Value);
                else if (sequence.IsArtifact) artifacts.Add(name.Value);
                else sequences.Add(name.Value);
            }
            result.NamesInsideArtifacts = artifacts;
            result.NamesInsideSequences = sequences;
            result.NamesOutsideSequences = outside;
        }

        /// <summary>The existing sequence whose content contains <paramref name="offset"/>, or null.</summary>
        private static MarkedContentInventory.Sequence EnclosingSequence(
            MarkedContentInventory inventory, int offset)
        {
            IReadOnlyList<MarkedContentInventory.Sequence> sequences = inventory.Sequences;
            for (int i = 0; i < sequences.Count; i++)
                if (sequences[i].Contains(offset)) return sequences[i];
            return null;
        }

        /// <summary>
        /// Whether a sequence of <paramref name="inventory"/> opens inside <paramref name="textObject"/>
        /// — after its <c>BT</c>, before its <c>ET</c> — and, for each that does, unmarks every line
        /// sharing bytes with it, so <see cref="BuildRunSegments"/> leaves that line to its producer.
        /// </summary>
        /// <remarks>
        /// A line half inside (one show the producer marked, one it did not) is unmarked whole: half
        /// a line can be marked only by nesting into the producer's sequence or interleaving with it,
        /// and §14.7.5.1 forbids the first while §14.6 forbids the second. The text left unmarked is
        /// a reported gap, not a corrupt tree.
        /// </remarks>
        private static bool LeaveLinesInsideSequencesToTheirProducer(
            MarkedContentInventory inventory, TextObjectInfo textObject)
        {
            IReadOnlyList<MarkedContentInventory.Sequence> sequences = inventory.Sequences;
            bool holds = false;

            for (int i = 0; i < sequences.Count; i++)
            {
                MarkedContentInventory.Sequence seq = sequences[i];
                if (seq.OpenStart < textObject.BtStart || seq.OpenStart >= textObject.EtEnd) continue;
                holds = true;

                for (int l = 0; l < textObject.Lines.Count; l++)
                {
                    LineGroup line = textObject.Lines[l];
                    if (line.StartOffset < seq.CloseStart && line.EndOffset > seq.OpenEnd)
                        line.Classification = RunClassification.Unmarked;
                }
            }

            return holds;
        }

        /// <summary>
        /// Wraps each image the caller can describe in a <c>/Figure</c> marked-content sequence.
        /// </summary>
        /// <remarks>
        /// <para><b>An image is the one taggable thing that is not text</b>, so it is collected
        /// from the token stream rather than from a line: there is no baseline, no font size and no
        /// text to classify. The byte range is the <c>Do</c> token's own — which spans its operand,
        /// because a token's start is where its first operand begins — so the splice produces
        /// <c>/Figure &lt;&lt;/MCID n&gt;&gt; BDC /Im0 Do EMC</c>.</para>
        ///
        /// <para><b>Three refusals, each for a different reason.</b> An image inside a
        /// <c>BT…ET</c> is skipped because a text object's bytes are already being cut into runs by
        /// offset and a second, overlapping sequence there could not be guaranteed properly nested.
        /// One already inside somebody's marked-content sequence is skipped because it is already
        /// tagged — adopting it is <see cref="AdoptExistingSequences"/>'s job. And one the caller
        /// returns no alternate text for is skipped because a <c>/Figure</c> without an
        /// <c>/Alt</c> is a UA07 finding this pass would have manufactured.</para>
        ///
        /// <para><b>The same image drawn twice is two figures.</b> Each <c>Do</c> is its own
        /// occurrence with its own MCID, and the caller is asked once per occurrence rather than
        /// once per resource — a logo drawn in a header and again in a footer may want different
        /// text, and deduplicating here would take that choice away.</para>
        /// </remarks>
        private static void CollectFigureRuns(
            IReadOnlyList<ContentToken> tokens,
            List<TextObjectInfo> textObjects,
            MarkedContentInventory existing,
            PageTagOptions options,
            List<TaggedRun> runs,
            List<Insertion> insertions,
            ref int mcid,
            ref int sequence)
        {
            int inlineImages = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                ContentToken token = tokens[i];
                if (string.Equals(token.Operator, PdfOperators.BeginInlineImage, StringComparison.Ordinal))
                {
                    // An inline image is drawn where it stands, so it is a figure the same way an
                    // image XObject's Do is, its byte range the whole BI…EI. Counted whether or not
                    // it is tagged, so its index names the same image for every caller.
                    int index = inlineImages++;
                    if (options.InlineImageAlternateText == null || token.InlineImage == null) continue;
                    if (IsInsideTextObject(textObjects, token.StartOffset)) continue;
                    if (existing != null && EnclosingSequence(existing, token.StartOffset) != null) continue;

                    string inlineAlt = options.InlineImageAlternateText(index, token.InlineImage);
                    if (string.IsNullOrEmpty(inlineAlt)) continue;

                    var inlineRun = new TaggedRun
                    {
                        Role          = StructureTypes.Figure,
                        Mcid          = mcid++,
                        TokenStart    = token.StartOffset,
                        TokenEnd      = token.EndOffset,
                        AlternateText = inlineAlt,
                    };
                    runs.Add(inlineRun);
                    insertions.Add(new Insertion(inlineRun.TokenStart, InsertionKind.Open,  BuildOpenSequence(inlineRun), sequence++));
                    insertions.Add(new Insertion(inlineRun.TokenEnd,   InsertionKind.Close, CloseSequence,                sequence++));
                    continue;
                }
                if (!string.Equals(token.Operator, PdfOperators.InvokeXObject, StringComparison.Ordinal))
                    continue;
                if (options.ImageXObjectNames == null || options.FigureAlternateText == null) continue;
                if (token.Operands == null || token.Operands.Count == 0) continue;

                // Read from the end: a content stream is postfix, so `/Name Do` puts the name
                // immediately before the operator, and a malformed stream that left extra operands
                // on the stack would otherwise yield whichever stale value happened to be first.
                if (!(token.Operands[token.Operands.Count - 1] is PdfName name)) continue;
                if (!options.ImageXObjectNames.Contains(name.Value)) continue;

                if (IsInsideTextObject(textObjects, token.StartOffset)) continue;
                if (existing != null && EnclosingSequence(existing, token.StartOffset) != null) continue;

                string alt = options.FigureAlternateText(name.Value);
                if (string.IsNullOrEmpty(alt)) continue;

                var run = new TaggedRun
                {
                    Role              = StructureTypes.Figure,
                    Mcid              = mcid++,
                    TokenStart        = token.StartOffset,
                    TokenEnd          = token.EndOffset,
                    FigureXObjectName = name.Value,
                    AlternateText     = alt,
                };

                runs.Add(run);
                insertions.Add(new Insertion(run.TokenStart, InsertionKind.Open,  BuildOpenSequence(run), sequence++));
                insertions.Add(new Insertion(run.TokenEnd,   InsertionKind.Close, CloseSequence,          sequence++));
            }
        }

        /// <summary>Whether an offset falls inside any <c>BT…ET</c> on the page.</summary>
        private static bool IsInsideTextObject(List<TextObjectInfo> textObjects, int offset)
        {
            for (int t = 0; t < textObjects.Count; t++)
                if (offset >= textObjects[t].BtStart && offset < textObjects[t].EtEnd) return true;
            return false;
        }

        /// <summary>
        /// Turns each existing MCID-bearing sequence into a run, so the element the caller writes
        /// for it claims the identifier its producer already chose.
        /// </summary>
        /// <remarks>
        /// <para><b>The role comes from the sequence's own tag, not from the classifier.</b>
        /// §14.6.1 makes a marked-content tag the structure type the producer intended, so the page
        /// has already answered the question the classifier exists to guess at. Re-classifying by
        /// font size would overrule a producer that said <c>/Figure</c> or <c>/TD</c> — types this
        /// tagger cannot infer at all — with a <c>/P</c>.</para>
        ///
        /// <para><b>A tag that is not a standard structure type is refused rather than copied.</b>
        /// It would need a <c>/RoleMap</c> entry, and the tree being built is a new one, so the old
        /// map is not there to carry it. Writing the element anyway produces exactly the
        /// unresolvable role UA11 reports.</para>
        ///
        /// <para><b>An artifact sequence is left entirely alone</b> — it claims no identifier and
        /// needs no element (§14.8.2.2), which is also why it is safe not to touch.</para>
        /// </remarks>
        private static string AdoptExistingSequences(
            MarkedContentInventory inventory, List<TextObjectInfo> textObjects, List<TaggedRun> runs)
        {
            IReadOnlyList<MarkedContentInventory.Sequence> sequences = inventory.Sequences;

            for (int i = 0; i < sequences.Count; i++)
            {
                MarkedContentInventory.Sequence seq = sequences[i];
                if (seq.IsArtifact || seq.Mcid < 0) continue;

                if (!StructureTypes.IsStandard(seq.Tag))
                {
                    return "Page already contains marked content tagged /" + (seq.Tag ?? "?") +
                           ", which is not a standard structure type; completing the page would "
                           + "need the /RoleMap of the tree being replaced.";
                }

                var run = new TaggedRun
                {
                    Role       = seq.Tag,
                    Mcid       = seq.Mcid,
                    TokenStart = seq.OpenStart,
                    TokenEnd   = seq.CloseEnd,
                };

                // Geometry, when the sequence encloses text. A sequence around an image or a path
                // has none, and none is needed: the run is not being placed, only recorded.
                for (int t = 0; t < textObjects.Count; t++)
                {
                    if (!seq.Contains(textObjects[t].BtStart)) continue;
                    List<LineGroup> lines = textObjects[t].Lines;
                    if (lines.Count == 0) continue;

                    run.Y        = lines[0].Y;
                    run.FontSize = lines[0].FontSize;
                    run.Text     = lines[0].BuildText();
                    break;
                }

                runs.Add(run);
            }

            return null;
        }

        // ================================================================
        //  Default classifier — reproduces the existing AutoTagOptions heuristics
        // ================================================================

        /// <summary>
        /// The heuristics <c>PdfAutoTagger</c> already used, restated against real content-stream
        /// geometry instead of extracted-text geometry:
        /// <list type="bullet">
        ///   <item><description>a line within <see cref="PageTagOptions.HeaderMargin"/> of the page's
        ///     upper edge or <see cref="PageTagOptions.FooterMargin"/> of its lower edge is a
        ///     pagination artifact;</description></item>
        ///   <item><description>otherwise a line whose effective font size is at least
        ///     H1/H2/H3 × the median body size becomes that heading level;</description></item>
        ///   <item><description>otherwise it is a paragraph line.</description></item>
        /// </list>
        /// Paragraph <em>grouping</em> is not the classifier's job — the tagger merges consecutive
        /// P lines using <see cref="PageTagOptions.ParagraphGapThreshold"/>.
        ///
        /// <para>
        /// Both bands are measured against the <em>real</em> page edges
        /// (<see cref="PageTagOptions.PageHeight"/> and <see cref="PageTagOptions.PageBottom"/>),
        /// never against an assumed <c>Y = 0</c> lower edge: a page box such as
        /// <c>[0 -100 612 692]</c> is legal (ISO 32000-1 §7.7.3.3), and measuring its footer from
        /// the origin would turn the bottom 150 pt of body text into an artifact and drop it out of
        /// the structure tree. Whenever the geometry is unusable — unknown, non-finite, or so small
        /// that the two bands meet — <em>no</em> edge classification happens at all, because
        /// over-tagging a running head as a paragraph is recoverable and hiding content from
        /// assistive technology is not.
        /// </para>
        /// </summary>
        internal static RunClassification DefaultClassifier(RunCandidate candidate)
        {
            if (candidate == null) return RunClassification.Content(StructureTypes.Paragraph);
            PageTagOptions options = candidate.Options ?? new PageTagOptions();

            if (options.MarkHeadersFootersAsArtifacts)
            {
                float top    = candidate.PageHeight;
                float bottom = candidate.PageBottom;

                if (IsFinite(top) && IsFinite(bottom) && IsFinite(candidate.Y))
                {
                    float height = top - bottom;
                    float header = options.HeaderMargin > 0f ? options.HeaderMargin : 0f;
                    float footer = options.FooterMargin > 0f ? options.FooterMargin : 0f;

                    // Bands that meet or overlap would classify every line on the page as
                    // pagination and leave the structure tree with nothing to point at, which is
                    // the same content-losing outcome PdfArtifactMarker refuses a page over.
                    if (height > 0f && header + footer < height)
                    {
                        if (header > 0f && top - candidate.Y <= header)
                            return RunClassification.Artifact("Pagination", "Header");
                        if (footer > 0f && candidate.Y - bottom <= footer)
                            return RunClassification.Artifact("Pagination", "Footer");
                    }
                }
            }

            // A table cell before everything below it. A cell drawn in a larger face is still a
            // cell — taking the heading ratio first would put an H1 inside a /TD — and a cell whose
            // text happens to start with "1. " is a cell rather than a list item, because the grid
            // is two-dimensional evidence and the marker is one-dimensional.
            //
            // Row 0 becomes /TH. See RunCandidate.TableRowIndex for why that is a heuristic rather
            // than a measurement, and how a caller overrides it.
            if (candidate.IsTableCell)
                return RunClassification.Content(candidate.TableRowIndex == 0
                    ? StructureTypes.TableHeaderCell
                    : StructureTypes.TableDataCell);

            // A list item before the heading ratios, because an item drawn in a larger face is
            // still an item: taking the ratio first would turn the first line of a big-type list
            // into an H1 sitting inside an /L, which is a worse tree than no list at all.
            if (candidate.IsListItem) return RunClassification.Content(StructureTypes.ListBody);

            float median = candidate.MedianFontSize > 0 ? candidate.MedianFontSize : PageTagOptions.FallbackMedianFontSize;
            float ratio  = candidate.FontSize > 0 ? candidate.FontSize / median : 1f;

            if (options.HeadingSizes != null && options.HeadingSizes.Length > 0 && ratio >= options.H3Ratio)
                return RunClassification.Content(HeadingByRank(candidate.FontSize, options.HeadingSizes));

            if (ratio >= options.H1Ratio) return RunClassification.Content(StructureTypes.Heading1);
            if (ratio >= options.H2Ratio) return RunClassification.Content(StructureTypes.Heading2);
            if (ratio >= options.H3Ratio) return RunClassification.Content(StructureTypes.Heading3);
            return RunClassification.Content(StructureTypes.Paragraph);
        }

        /// <summary>
        /// The heading level a size takes among the document's ranked heading sizes: H1 for the
        /// largest, each smaller size one level down, H6 for the sixth and every size below it. A size
        /// the ranking did not see (a form's text, measured separately) takes the level of the first
        /// ranked size it is not smaller than.
        /// </summary>
        private static string HeadingByRank(float fontSize, float[] ranked)
        {
            float key = Tagging.PdfAutoTagger.HeadingSizeKey(fontSize);
            int level = ranked.Length;
            for (int i = 0; i < ranked.Length; i++)
                if (key >= ranked[i]) { level = i + 1; break; }
            switch (Math.Min(level, 6))
            {
                case 1: return StructureTypes.Heading1;
                case 2: return StructureTypes.Heading2;
                case 3: return StructureTypes.Heading3;
                case 4: return StructureTypes.Heading4;
                case 5: return StructureTypes.Heading5;
                default: return StructureTypes.Heading6;
            }
        }

        /// <summary>
        /// True for a real number. A NaN comparison is false in both directions, so a NaN edge would
        /// slip past a naive range test and leave the band undefined rather than closed.
        /// </summary>
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // ================================================================
        //  Page geometry
        // ================================================================

        /// <summary>
        /// Vertical extent of the visible page in default user space, for
        /// <see cref="PageTagOptions.PageHeight"/> / <see cref="PageTagOptions.PageBottom"/>.
        ///
        /// <para>
        /// <c>/CropBox</c> is preferred over <c>/MediaBox</c> because it is the region a reader
        /// actually sees, so it is the region a running head sits at the top of;
        /// <see cref="PdfPage"/> resolves inheritance from the page tree and already falls back to
        /// <c>/MediaBox</c>. Either corner may be written first (§7.9.5), so the pair is sorted
        /// rather than assumed.
        /// </para>
        ///
        /// <para>
        /// Returns false — with both outputs 0, which disables edge classification — when no box is
        /// readable. That is deliberately the safe direction: a page whose bands cannot be located
        /// gets its running heads tagged as paragraphs instead of having body text marked as an
        /// artifact and lost.
        /// </para>
        /// </summary>
        internal static bool TryGetPageVerticalExtent(PdfDocument doc, PdfPage page, out float top, out float bottom)
        {
            top    = 0f;
            bottom = 0f;
            if (page == null) return false;

            PdfArray crop = ReadPageBox(page, cropBox: true);
            if (TryReadBoxVerticalExtent(doc, crop, "/CropBox", out top, out bottom)) return true;

            PdfArray media = ReadPageBox(page, cropBox: false);
            if (!ReferenceEquals(media, crop) &&
                TryReadBoxVerticalExtent(doc, media, "/MediaBox", out top, out bottom)) return true;

            top    = 0f;
            bottom = 0f;
            return false;
        }

        private static PdfArray ReadPageBox(PdfPage page, bool cropBox)
        {
            try
            {
                return cropBox ? page.CropBox : page.MediaBox;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("ContentStreamTagger.TryGetPageVerticalExtent",
                    (cropBox
                        ? "/CropBox could not be read; /MediaBox is tried instead"
                        : "/MediaBox could not be read, so the page's vertical extent is unknown and "
                          + "header/footer classification is disabled for it"), ex);
                return null;
            }
        }

        private static bool TryReadBoxVerticalExtent(
            PdfDocument doc, PdfArray box, string boxName, out float top, out float bottom)
        {
            top    = 0f;
            bottom = 0f;
            if (box == null) return false;

            if (box.Count < 4)
            {
                AccessibilityDiagnostics.Report("ContentStreamTagger.TryGetPageVerticalExtent",
                    boxName + " has " + box.Count.ToString(CultureInfo.InvariantCulture) +
                    " elements instead of 4, so it cannot describe a rectangle; header/footer bands "
                    + "cannot be located from it");
                return false;
            }

            float y0, y1;
            if (!TryResolveNumber(doc, box[1], boxName, out y0)) return false;
            if (!TryResolveNumber(doc, box[3], boxName, out y1)) return false;

            bottom = Math.Min(y0, y1);
            top    = Math.Max(y0, y1);

            if (top - bottom > 0f) return true;

            AccessibilityDiagnostics.Report("ContentStreamTagger.TryGetPageVerticalExtent",
                boxName + " has zero height, so no header or footer band can be located");
            top    = 0f;
            bottom = 0f;
            return false;
        }

        /// <summary>
        /// One implementation, in <see cref="PageBoxNumber"/> — shared with
        /// <c>PdfArtifactMarker</c>, post-cast finiteness re-test included.
        /// </summary>
        private static bool TryResolveNumber(PdfDocument doc, PdfObject obj, string boxName, out float value)
            => PageBoxNumber.TryResolveFinite(
                doc, obj, "ContentStreamTagger.TryGetPageVerticalExtent", "", boxName, out value);

        // ================================================================
        //  Page-content helper
        // ================================================================

        /// <summary>
        /// Resolves a page's <c>/Contents</c> — a single stream or an array of streams — and returns
        /// the decoded bytes.
        ///
        /// <para>
        /// ISO 32000-1 7.8.2: when <c>/Contents</c> is an array, the parts "shall be concatenated
        /// … with at least one white-space character between the data in each pair", and the
        /// division between streams may occur only at the boundary between lexical tokens.
        /// Exactly one <c>\n</c> is therefore inserted between parts — enough to keep the last
        /// token of one part from fusing with the first token of the next (a real hazard: a part
        /// ending in <c>BT</c> followed by one starting with <c>/F1</c> would otherwise still be
        /// fine, but a part ending in <c>Tf</c> followed by one starting with <c>72</c> would
        /// tokenise as <c>Tf72</c>), and few enough that no offset arithmetic is wasted.
        /// </para>
        ///
        /// <para>
        /// The running total is accumulated in a <c>long</c> and capped at
        /// <see cref="MaxConcatenatedContentBytes"/>. Both halves of that matter: an array of many
        /// large parts is otherwise an unbounded allocation request from a small file, and once the
        /// total crosses <c>int.MaxValue</c>, <c>int</c> arithmetic would wrap negative and
        /// <c>new byte[total - 1]</c> throw <see cref="OverflowException"/> out of a method whose
        /// contract is to report failure by returning <c>null</c>.
        /// </para>
        ///
        /// <para>
        /// <b>The ceiling is consulted before each part is decoded, not after.</b> Measuring a part
        /// only once <see cref="PdfStream.GetDecodedData"/> had already returned it made the ceiling
        /// useless against the attack it exists for: a single Flate bomb inflates to whatever Core
        /// allows a stream to reach (<see cref="PdfLimits.MaxDecompressedBytes"/>, 512 MiB) and the
        /// allocation is already made and already charged to the process by the time the length is
        /// looked at. <see cref="TryGetKnownDecodedLength"/> therefore asks each part what it can be
        /// asked for free — see its remarks for exactly what is knowable and, importantly, what is
        /// not.
        /// </para>
        /// </summary>
        /// <returns>
        /// The decoded concatenation; an empty array when the page has no content; <c>null</c> when
        /// a part could not be decoded or the parts together exceed
        /// <see cref="MaxConcatenatedContentBytes"/> (with the reason in
        /// <see cref="LastTagError"/>) — a partial concatenation is never returned, because splicing
        /// into one would corrupt the page.
        /// </returns>
        /// <param name="doc">Document the page belongs to, used to resolve indirect references.</param>
        /// <param name="pageDict">The page dictionary whose <c>/Contents</c> is to be assembled.</param>
        /// <param name="maxBytes">
        /// Ceiling on the assembled total. Zero or less means "use
        /// <see cref="MaxConcatenatedContentBytes"/>" — there is deliberately no way to ask for an
        /// unbounded concatenation, because bounding it is the method's whole job. Exists so a test
        /// can exercise the guard at a tractable size; production callers pass nothing.
        /// </param>
        internal static byte[] ConcatenatePageContent(
            PdfDocument doc, PdfDictionary pageDict, long maxBytes = 0)
            => ConcatenatePageContent(doc, pageDict, maxBytes, out _);

        /// <summary>
        /// As <see cref="ConcatenatePageContent(PdfDocument, PdfDictionary, long)"/>, but also
        /// reports the decompression this call <b>paid for</b>, which the return value cannot.
        /// </summary>
        /// <param name="doc">Document the page belongs to, used to resolve indirect references.</param>
        /// <param name="pageDict">The page dictionary whose <c>/Contents</c> is to be assembled.</param>
        /// <param name="maxBytes">Ceiling on the assembled total, as on the three-argument form.</param>
        /// <param name="decodedBytes">
        /// Bytes actually inflated, including on the paths that end in <c>null</c>. A caller
        /// enforcing a document-wide budget has to charge a refused page too: the bounded decode
        /// still inflates up to the ceiling before giving up, and a filtered stream that declares no
        /// <c>/DL</c> cannot be measured any other way. Charging only successes let a document of N
        /// identical Flate bombs spend N × ceiling against a budget that never moved — the "same
        /// attack with an extra loop" the document ceiling exists to stop. A refusal made
        /// <i>before</i> decoding (a <c>/DL</c> over the ceiling) reports 0, because it cost nothing.
        /// </param>
        /// <remarks>
        /// <b>A deliberate non-caller of <c>Core.Document.PdfPageContent.Concatenate</c></b>, on two
        /// counts. It <i>refuses</i> rather than skips when a part will not decode — this assembles
        /// the content a page will be rewritten from, so keeping the readable parts would silently
        /// drop the rest — and it names the failing part <b>by index</b>, which a shared resolve
        /// cannot report. And the ceiling is checked <i>between</i> parts, before each is inflated,
        /// so nothing can wrap a method that returns a finished array. The separator rule is the
        /// same and stays in step by inspection.
        /// </remarks>
        internal static byte[] ConcatenatePageContent(
            PdfDocument doc, PdfDictionary pageDict, long maxBytes, out long decodedBytes)
        {
            _lastTagError = null;
            decodedBytes = 0;
            maxBytes = NormaliseConcatenationCeiling(maxBytes);
            if (doc == null || pageDict == null) return new byte[0];

            PdfObject contentsObj = pageDict[PdfName.Contents];
            if (contentsObj == null) return new byte[0];

            PdfObject resolved;
            try { resolved = doc.ResolveObject(contentsObj); }
            catch (Exception ex)
            {
                _lastTagError = "Could not resolve /Contents: " + ex.Message;
                AccessibilityDiagnostics.Report("ContentStreamTagger.ConcatenatePageContent",
                    "the page's /Contents entry could not be resolved; the page cannot be tagged", ex);
                return null;
            }

            var parts = new List<byte[]>();
            long total = 0;                       // decoded bytes plus the separators between parts
            if (resolved is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    PdfObject item;
                    try { item = doc.ResolveObject(array[i]); }
                    catch (Exception ex)
                    {
                        _lastTagError = "Could not resolve /Contents[" + i.ToString(CultureInfo.InvariantCulture) +
                                        "]: " + ex.Message;
                        AccessibilityDiagnostics.Report("ContentStreamTagger.ConcatenatePageContent",
                            "part " + i.ToString(CultureInfo.InvariantCulture) +
                            " of the page's /Contents array could not be resolved; the page cannot be tagged", ex);
                        return null;
                    }

                    if (item is PdfStream partStream)
                    {
                        // Ceiling first, decode second. Checked per part, so an abusive array stops
                        // at the part that crosses the ceiling instead of after all of them — and
                        // stops *before* that part is inflated whenever its decoded size can be
                        // known without inflating it.
                        if (WouldExceedCeiling(partStream, parts.Count > 0, maxBytes, total)) return null;

                        long partBudget = RemainingBudget(maxBytes, total, parts.Count > 0);
                        byte[] decoded = Decode(partStream, i, partBudget);
                        // The inflation was authorised whether or not it produced anything usable,
                        // so it is charged either way.
                        decodedBytes += decoded?.Length ?? partBudget;
                        if (decoded == null) return null;
                        // Second gate: the pre-decode check is only as good as what the stream
                        // declares, so the real length is still measured and still refused.
                        if (!TryAccumulate(decoded.Length, parts.Count > 0, maxBytes, ref total)) return null;
                        parts.Add(decoded);
                    }
                    // A null / non-stream element is legal padding in damaged files; skip it. It
                    // contributes no bytes, so no offset is affected.
                }
            }
            else if (resolved is PdfStream single)
            {
                // The single-stream path is the one a Flate bomb takes: one /Contents stream, a few
                // kilobytes on disk, 512 MiB decoded. It gets the same pre-decode check as an array
                // part — measuring it after GetDecodedData() returned would be measuring an
                // allocation that has already been made.
                if (WouldExceedCeiling(single, false, maxBytes, total)) return null;

                long singleBudget = RemainingBudget(maxBytes, total, false);
                byte[] decoded = Decode(single, 0, singleBudget);
                decodedBytes += decoded?.Length ?? singleBudget;
                if (decoded == null) return null;
                if (!TryAccumulate(decoded.Length, false, maxBytes, ref total)) return null;
                parts.Add(decoded);
            }
            else
            {
                return new byte[0];
            }

            if (parts.Count == 0) return new byte[0];
            if (parts.Count == 1) return parts[0];

            // total is every part plus the one separator between each pair, and
            // NormaliseConcatenationCeiling is what makes it fit an int.
            var buffer = new byte[(int)total];
            int offset = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) buffer[offset++] = (byte)'\n';
                Buffer.BlockCopy(parts[i], 0, buffer, offset, parts[i].Length);
                offset += parts[i].Length;
            }
            return buffer;
        }

        /// <summary>
        /// The ceiling this method will actually enforce: the default when the caller asked for
        /// none, and never more than <see cref="int.MaxValue"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The int clamp is the load-bearing half. The assembled buffer is allocated as
        /// <c>new byte[(int)total]</c>, which is safe only because <c>total</c> can never exceed the
        /// ceiling — and the ceiling comes from the <b>public</b>
        /// <c>AutoTagOptions.MaxConcatenatedContentBytes</c>, a <c>long</c> whose documentation
        /// invites a host doing large-page work to raise it. Above <c>int.MaxValue</c> the cast
        /// truncates: at 4 831 838 216 bytes it yields 536 870 920, and at 4 294 967 303 it yields
        /// 7, so the buffer is allocated far smaller than the parts and <c>Buffer.BlockCopy</c>
        /// throws <c>ArgumentException</c> out of a method whose contract is to report failure by
        /// returning null. Reached from <c>MeasureFontSizesFromContentStreams</c> that exception is
        /// not caught per page, so it escapes to <c>AutoTag</c>'s outermost handler and the whole
        /// document is copied through untagged rather than one page being skipped with a reason.
        /// </para>
        /// <para>
        /// Clamping rather than throwing keeps the promise the option makes: a host that asks for
        /// 6 GiB gets the largest page this method can assemble, and is refused — through the
        /// documented null-plus-reason path — only if the page really is bigger than that.
        /// </para>
        /// </remarks>
        internal static long NormaliseConcatenationCeiling(long maxBytes)
        {
            if (maxBytes <= 0) maxBytes = MaxConcatenatedContentBytes;
            return maxBytes > int.MaxValue ? int.MaxValue : maxBytes;
        }

        /// <summary>
        /// Adds one decoded part (plus the separator that will follow it) to the running total and
        /// reports whether the page is still inside <see cref="MaxConcatenatedContentBytes"/>.
        /// Accumulating in a <c>long</c> is the point: <c>/Contents</c> may hold any number of
        /// streams, each of which Core lets inflate to 512 MiB, so the sum is neither bounded by the
        /// file size nor by <c>int</c>.
        /// </summary>
        private static bool TryAccumulate(int partLength, bool needsSeparator, long maxBytes, ref long total)
        {
            total += (long)partLength + (needsSeparator ? 1 : 0);   // the '\n' joining it to the previous part
            if (total <= maxBytes) return true;

            // A backstop rather than the gate that normally fires: a part heading past the
            // ceiling is stopped mid-decode by the budget Decode passes down. The accumulation
            // itself is load-bearing — the assembled buffer is sized from `total` — and a refusal
            // here costs nothing. Do not read its rarity as dead code.
            ReportOverCeiling(maxBytes, "measured after decoding");
            return false;
        }

        /// <summary>
        /// What is left of the page's ceiling once the parts already assembled — and the separator
        /// this one would need — are accounted for. Floored at 1 so a full page never asks for a
        /// zero-or-negative budget, which <c>TryGetDecodedData</c> would have to reject outright
        /// rather than decode; the post-decode <see cref="TryAccumulate"/> check refuses that case
        /// with the right message.
        /// </summary>
        private static long RemainingBudget(long maxBytes, long total, bool needsSeparator)
        {
            long remaining = maxBytes - total - (needsSeparator ? 1 : 0);
            return remaining > 0 ? remaining : 1;
        }

        /// <summary>
        /// Whether adding <paramref name="stream"/> would take the page past
        /// <paramref name="maxBytes"/>, judged <em>without decoding it</em>. Returns false when the
        /// answer cannot be established in advance — see <see cref="TryGetKnownDecodedLength"/> —
        /// which is a "not proven over", never a "proven under": the post-decode
        /// <see cref="TryAccumulate"/> check still runs.
        /// </summary>
        private static bool WouldExceedCeiling(PdfStream stream, bool needsSeparator, long maxBytes, long total)
        {
            long declared;
            if (!TryGetKnownDecodedLength(stream, out declared)) return false;

            long projected = total + (needsSeparator ? 1 : 0) + declared;
            if (projected <= maxBytes) return false;

            ReportOverCeiling(maxBytes,
                "declared as " + declared.ToString(CultureInfo.InvariantCulture) +
                " decoded bytes by the stream itself, so it was refused before being decoded");
            return true;
        }

        /// <summary>
        /// The decoded length of one <c>/Contents</c> part when it can be established <em>without</em>
        /// calling <see cref="PdfStream.GetDecodedData"/>, which is the only kind of answer a guard
        /// against a decompression bomb may use.
        ///
        /// <para>Two things are knowable for free:</para>
        /// <list type="number">
        ///   <item><description><b><c>/DL</c></b> — "a non-negative integer representing the number
        ///     of bytes in the decoded (defiltered) stream" (ISO 32000-1 Table 5). It is a producer
        ///     hint, so it is trusted in one direction only: a <c>/DL</c> over the ceiling refuses
        ///     the page, while a <c>/DL</c> under it proves nothing and merely falls through to the
        ///     post-decode check. A hostile file that under-declares therefore gains nothing.
        ///     </description></item>
        ///   <item><description><b>An unfiltered stream</b> — with no <c>/Filter</c>,
        ///     <c>GetDecodedData</c> returns the raw bytes themselves, so the raw length <em>is</em>
        ///     the decoded length, exactly. (<c>/Length</c> is the <em>encoded</em> length and so
        ///     bounds nothing on a filtered stream; it is consulted only here, where the two are the
        ///     same number, and only if the raw bytes are somehow absent.)</description></item>
        /// </list>
        ///
        /// <para>
        /// <b>What is not knowable, stated plainly rather than papered over.</b> A filtered stream
        /// with no <c>/DL</c> — the shape a deliberate Flate bomb takes — cannot have its decoded
        /// size predicted from anything in the file: that is what a compression filter is. This
        /// guard does not fire for that input, and it does not need to.
        /// </para>
        ///
        /// <para>
        /// <b>Such a stream is bounded by the page, not Core.</b>
        /// <c>PdfStream.TryGetDecodedData(long, out byte[], out string)</c> stops the filter the moment its output passes the caller's budget, and
        /// <see cref="Decode"/> passes what remains of this page's ceiling. The pre-decode check
        /// here is still worth having: refusing on a declared <c>/DL</c> costs nothing at all,
        /// where a bounded decode still pays for the bytes up to the ceiling.
        /// </para>
        /// </summary>
        private static bool TryGetKnownDecodedLength(PdfStream stream, out long length)
        {
            length = 0;
            if (stream == null) return false;

            long? dl = stream.GetLong(PdfName.DL);
            if (dl.HasValue && dl.Value >= 0)
            {
                length = dl.Value;
                return true;
            }

            if (!IsUnfiltered(stream)) return false;

            byte[] raw = stream.RawData;
            if (raw != null)
            {
                length = raw.Length;
                return true;
            }

            long? declaredRawLength = stream.GetLong(PdfName.Length);
            if (declaredRawLength.HasValue && declaredRawLength.Value >= 0)
            {
                length = declaredRawLength.Value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when the stream carries no filter, in which case its decoded bytes are its raw
        /// bytes. An <em>indirect</em> <c>/Filter</c> is reported as filtered without resolving it:
        /// resolving costs a document lookup and the wrong answer here would be a guard that refuses
        /// a legitimate page, so the conservative direction is "assume it filters".
        /// </summary>
        private static bool IsUnfiltered(PdfStream stream)
        {
            PdfObject filter = stream[PdfName.Filter];
            if (filter == null || filter is PdfNull) return true;
            if (filter is PdfArray array) return array.Count == 0;
            return false;
        }

        /// <summary>
        /// Records the one refusal this method has — the page's content is larger than the caller
        /// allowed — with <paramref name="how"/> saying whether it was caught before or after the
        /// decode, so a report can distinguish "we declined to allocate this" from "we allocated it
        /// and then declined to use it".
        /// </summary>
        private static void ReportOverCeiling(long maxBytes, string how)
        {
            string limit = maxBytes.ToString(CultureInfo.InvariantCulture);
            _lastTagError = "Page /Contents decodes to more than " + limit +
                            " bytes (" + how + "); the concatenation was abandoned.";
            AccessibilityDiagnostics.Report("ContentStreamTagger.ConcatenatePageContent",
                "the page's content streams decode to more than " + limit +
                " bytes in total (" + how + "); assembling them would be an unbounded allocation, so the page's "
                + "content is treated as unreadable and the page is neither summarised nor tagged");
        }

        /// <summary>
        /// Decodes one part, <b>bounded by what is left of the page's budget</b>.
        /// </summary>
        /// <remarks>
        /// <para>A filtered stream declaring no <c>/DL</c> is bounded here:
        /// <c>PdfStream.TryGetDecodedData</c> stops the filter the moment its output passes
        /// <paramref name="budget"/>, so the transient is this page's number rather than Core's
        /// 512 MiB.</para>
        ///
        /// <para><b>The budget is what remains, not the whole ceiling</b>, and that cannot
        /// over-refuse: a part that alone exceeds the remaining budget is one
        /// <see cref="TryAccumulate"/> would reject a moment later anyway. The post-decode check is
        /// still run — this bounds the work, it does not replace the accounting.</para>
        ///
        /// <para>Both failure kinds land here and the reason is passed through verbatim rather than
        /// relabelled, so "the stream is corrupt" and "the stream is too big" stay
        /// distinguishable in the message a caller actually reads.</para>
        /// </remarks>
        private static byte[] Decode(PdfStream stream, int index, long budget)
        {
            try
            {
                if (stream.TryGetDecodedData(budget, out byte[] decoded, out string reason))
                    return decoded ?? new byte[0];

                _lastTagError = "Could not decode content stream part " +
                                index.ToString(CultureInfo.InvariantCulture) + ": " + reason;
                AccessibilityDiagnostics.Report("ContentStreamTagger.ConcatenatePageContent",
                    "content stream part " + index.ToString(CultureInfo.InvariantCulture) +
                    " could not be decoded within the " + budget.ToString(CultureInfo.InvariantCulture) +
                    "-byte budget remaining for this page (" + reason +
                    "); a partial concatenation is never returned, so the page cannot be tagged");
                return null;
            }
            catch (Exception ex)
            {
                _lastTagError = "Could not decode content stream part " +
                                index.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message;
                AccessibilityDiagnostics.Report("ContentStreamTagger.ConcatenatePageContent",
                    "content stream part " + index.ToString(CultureInfo.InvariantCulture) +
                    " could not be decoded; a partial concatenation is never returned, so the page cannot be tagged", ex);
                return null;
            }
        }

        // ================================================================
        //  Stream walk: text objects, lines, and the graphics state we need
        // ================================================================

        /// <summary>
        /// The slice of graphics state that survives <c>q</c>/<c>Q</c> and, importantly, survives
        /// <c>BT</c>/<c>ET</c>. Text state parameters (<c>Tf</c>, <c>TL</c>, <c>Ts</c>) live in the
        /// graphics state, not in the text object — <c>/F1 12 Tf BT … ET</c> is legal and common,
        /// so resetting the font size at every <c>BT</c> would silently mis-size every heading.
        /// The text matrices, by contrast, <em>are</em> reset to identity at each <c>BT</c> (9.4.1).
        /// </summary>
        private struct GraphicsState
        {
            public AffineMatrix Ctm;
            public float  FontSize;   // Tf size operand
            public float  Leading;    // TL
            public float  Rise;       // Ts
        }

        private sealed class ShowOp
        {
            public int   StartOffset;
            public int   EndOffset;
            public float Y;
            public float FontSize;

            /// <summary>Device X of the glyph origin this show starts at.</summary>
            /// <remarks>
            /// <c>RecordShow</c> builds the full text-to-device matrix; this is its <c>E</c>
            /// component, beside the <c>F</c> it reads for the baseline. Horizontal position is what separates an
            /// indented list item from a paragraph, a centred heading from a left-aligned one, and
            /// a table cell from the cell beside it — none of which a line-level tagger could
            /// even ask about.
            /// </remarks>
            public float X;

            public string Text;
        }

        private sealed class LineGroup
        {
            public readonly List<ShowOp> Shows = new List<ShowOp>();
            public float Y;
            public float FontSize;

            /// <summary>Device X of the line's leftmost show — where the line starts.</summary>
            /// <remarks>
            /// The <b>minimum</b> rather than the first show's, because a line is assembled from
            /// shows in content order and a producer may place them right to left, or place a
            /// hanging marker after the text it belongs to. Its left edge is where its ink starts,
            /// whichever show put it there.
            /// </remarks>
            public float X = float.NaN;

            public int   StartOffset;
            public int   EndOffset;
            public RunClassification Classification;
            public int   ParagraphId = -1;

            /// <summary>Inferred table this line is a cell of, or -1. See <see cref="TaggedRun.TableId"/>.</summary>
            public int   TableId = -1;

            /// <summary>Row of this cell within its table, or -1.</summary>
            public int   TableRowIndex = -1;

            /// <summary>Column of this cell within its table, or -1.</summary>
            public int   TableColumnIndex = -1;

            /// <summary>Columns its table has, or 0 when this line is not a cell.</summary>
            public int   TableColumnCount;

            /// <summary>
            /// Memoised result of <see cref="BuildText"/>. A line's shows never change after
            /// <c>BuildTextObjects</c> has returned, and the text is asked for at least twice
            /// (once by the classifier, once by run segmentation), so it is built once.
            /// </summary>
            private string _text;

            public string BuildText()
            {
                if (_text != null) return _text;
                if (Shows.Count == 0) return _text = string.Empty;
                if (Shows.Count == 1) return _text = Shows[0].Text ?? string.Empty;

                var sb = new StringBuilder();
                for (int i = 0; i < Shows.Count; i++) sb.Append(Shows[i].Text);
                return _text = sb.ToString();
            }

            /// <summary>
            /// Appends this line's text to <paramref name="sb"/> without materialising an
            /// intermediate string when the text has not already been built.
            /// </summary>
            public void AppendTextTo(StringBuilder sb)
            {
                if (_text != null) { sb.Append(_text); return; }
                for (int i = 0; i < Shows.Count; i++) sb.Append(Shows[i].Text);
            }
        }

        private sealed class TextObjectInfo
        {
            public int BtStart;
            public int EtEnd;
            public readonly List<LineGroup> Lines = new List<LineGroup>();

            /// <summary>True when the object contains <c>q</c> or <c>Q</c> (illegal per spec, common in practice).</summary>
            public bool HasGraphicsStateOps;

            /// <summary>Net q-depth change across the object; must be 0 for a wrap to nest correctly.</summary>
            public int GraphicsStateNet;

            /// <summary>Lowest relative q-depth reached; must be ≥ 0 for a wrap to nest correctly.</summary>
            public int GraphicsStateMin;
        }

        /// <summary>
        /// Walks the token stream, maintaining exactly the state needed to place a baseline:
        /// the CTM stack (<c>q</c>/<c>Q</c>/<c>cm</c>) and, inside each <c>BT…ET</c>, the text and
        /// text-line matrices plus font size, leading and rise. No font metrics, no glyph widths —
        /// horizontal position is irrelevant to a line-level tagger.
        /// </summary>
        private static List<TextObjectInfo> BuildTextObjects(
            IReadOnlyList<ContentToken> tokens, PageTagOptions options, out string error)
        {
            error = null;
            var result = new List<TextObjectInfo>();

            var stack = new List<GraphicsState>();
            var gs    = new GraphicsState { Ctm = AffineMatrix.Identity, FontSize = 0f, Leading = 0f, Rise = 0f };
            AffineMatrix tm = AffineMatrix.Identity;    // text matrix
            AffineMatrix tlm = AffineMatrix.Identity;   // text line matrix

            TextObjectInfo current = null;
            int qDepth = 0, qMin = 0;
            var numbers = new float[6];

            for (int i = 0; i < tokens.Count; i++)
            {
                ContentToken token = tokens[i];
                switch (token.Operator)
                {
                    case PdfOperators.Save:
                        // A stream of nothing but "q " pushes one state per two bytes and never
                        // pops. Uncapped that is ~256M structs (~9 GB) from a stream inside the
                        // decoder's own size limit, so the depth is bounded here the same way
                        // PdfReadingOrderAnalyzer bounds its identical stack.
                        if (stack.Count >= MaxGraphicsStackDepth)
                        {
                            error = "Content stream nests the graphics state deeper than " +
                                    MaxGraphicsStackDepth.ToString(CultureInfo.InvariantCulture) +
                                    " levels (unbalanced or hostile q operators); refusing to splice.";
                            return result;
                        }
                        stack.Add(gs);
                        if (current != null)
                        {
                            current.HasGraphicsStateOps = true;
                            qDepth++;
                        }
                        break;

                    case PdfOperators.Restore:
                        if (stack.Count > 0)
                        {
                            gs = stack[stack.Count - 1];
                            stack.RemoveAt(stack.Count - 1);
                        }
                        if (current != null)
                        {
                            current.HasGraphicsStateOps = true;
                            qDepth--;
                            if (qDepth < qMin) qMin = qDepth;
                        }
                        break;

                    case PdfOperators.ConcatMatrix:
                        if (TryGetTrailingNumbers(token.Operands, 6, numbers))
                            gs.Ctm = AffineMatrix.Multiply(
                                new AffineMatrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]),
                                gs.Ctm);
                        break;

                    case PdfOperators.BeginText:
                        if (current != null)
                        {
                            error = "Content stream nests BT inside another text object; refusing to tag it.";
                            return result;
                        }
                        current = new TextObjectInfo { BtStart = token.StartOffset };
                        TextPositioning.BeginText(ref tm, ref tlm);   // §9.4.1: Tm and Tlm, nothing else
                        qDepth  = 0;
                        qMin    = 0;
                        break;

                    case PdfOperators.EndText:
                        if (current == null)
                        {
                            error = "Content stream contains ET without a matching BT; refusing to tag it.";
                            return result;
                        }
                        current.EtEnd            = token.EndOffset;
                        current.GraphicsStateNet = qDepth;
                        current.GraphicsStateMin = qMin;
                        result.Add(current);
                        current = null;
                        break;

                    case PdfOperators.SetFont:
                        if (TryGetTrailingNumbers(token.Operands, 1, numbers)) gs.FontSize = numbers[0];
                        break;

                    case PdfOperators.SetTextRise:
                        // Rise stays here: it lands in this walk's own graphics state and takes
                        // part in no algebra, so TextPositioning does not handle it.
                        if (TryGetTrailingNumbers(token.Operands, 1, numbers)) gs.Rise = numbers[0];
                        break;

                    // TL, Tm, Td, TD and T* are §9.4.2 algebra, shared with
                    // PdfReadingOrderAnalyzer through Internal.TextPositioning. The two walks
                    // keep their own policies -- this one REFUSES a malformed page because it is
                    // computing byte offsets to splice; the analyzer tolerates and counts.
                    case PdfOperators.SetLeading:
                    case PdfOperators.SetTextMatrix:
                    case PdfOperators.TextMove:
                    case PdfOperators.TextMoveSetLeading:
                    case PdfOperators.NextLine:
                        TextPositioning.TryApply(token.Operator, token.Operands, numbers,
                                                 ref tm, ref tlm, ref gs.Leading);
                        break;

                    case PdfOperators.ShowText:
                    case PdfOperators.ShowTextArray:
                        if (current != null) RecordShow(current, tm, gs, token, options);
                        break;

                    case PdfOperators.NextLineShowText:
                        // ' is "T* then Tj": advance to the next line first, then show.
                        TextPositioning.NextLine(ref tm, ref tlm, gs.Leading);
                        if (current != null) RecordShow(current, tm, gs, token, options);
                        break;

                    case PdfOperators.NextLineSetSpacingShowText:
                        // aw ac string " — sets word/char spacing, then behaves like '.
                        TextPositioning.NextLine(ref tm, ref tlm, gs.Leading);
                        if (current != null) RecordShow(current, tm, gs, token, options);
                        break;
                }
            }

            if (current != null)
            {
                error = "Content stream ends inside a text object (BT with no ET); refusing to tag it.";
                return result;
            }

            return result;
        }

        private static void RecordShow(
            TextObjectInfo textObject, AffineMatrix tm, GraphicsState gs, ContentToken token, PageTagOptions options)
        {
            // Text-space → device: Trm = [Tfs·Th 0 0 Tfs 0 Trise] × Tm × CTM. The glyph origin sits
            // at text-space (0, Trise), so its device Y is Trise·M.D + M.F where M = Tm × CTM.
            AffineMatrix m = AffineMatrix.Multiply(tm, gs.Ctm);
            float y  = (gs.Rise * m.D) + m.F;

            // The X the same matrix already carried. Rise is a vertical displacement, so it
            // contributes through B rather than D on this axis.
            float x = (gs.Rise * m.B) + m.E;

            // A non-finite CTM makes VerticalScale NaN, and `NaN <= 0f` is false — so the fallback
            // this line exists for was skipped for exactly the input that needs it most, and a NaN
            // size travelled on into RunCandidate.FontSize and TaggedRun.FontSize. IsFinite is what
            // the classifier below already uses for the same reason.
            float size = Math.Abs(gs.FontSize) * m.VerticalScale;
            if (!IsFinite(size) || size <= 0f) size = options.MedianFontSize > 0 ? options.MedianFontSize : PageTagOptions.FallbackMedianFontSize;

            var show = new ShowOp
            {
                StartOffset = token.StartOffset,
                EndOffset   = token.EndOffset,
                Y           = y,
                X           = x,
                FontSize    = size,
                Text        = ExtractShowText(token.Operator, token.Operands),
            };

            LineGroup line = textObject.Lines.Count > 0 ? textObject.Lines[textObject.Lines.Count - 1] : null;
            if (line == null || !SameLine(line.Y, y, options.SameLineTolerance))
            {
                line = new LineGroup { Y = y, FontSize = size, StartOffset = show.StartOffset };
                textObject.Lines.Add(line);
            }

            line.Shows.Add(show);
            line.EndOffset = show.EndOffset;

            // NaN is never less than anything, so a non-finite X neither becomes the line's left
            // edge nor displaces one already found — the same rule SameLine applies to Y, for
            // the same reason: a CTM that overflows a float must not silently move the geometry a
            // classifier reads.
            if (IsFinite(x) && (!IsFinite(line.X) || x < line.X)) line.X = x;
            if (size > line.FontSize) line.FontSize = size;   // a line's size is its largest glyph run
        }

        /// <summary>
        /// Whether two baselines are close enough to be one line. <b>A non-finite baseline is never
        /// on the same line as anything</b> — not as the line before it, and not as the line after.
        /// </summary>
        /// <remarks>
        /// <b>Why the test is written this way round.</b> Written as
        /// <c>Math.Abs(line.Y - y) &gt; tolerance</c> starting a NEW group, a NaN comparison is false
        /// in every direction — so a NaN baseline would answer "same line" and every following show,
        /// finite or not, fold into whichever group was open. A CTM that overflows
        /// <see cref="AffineMatrix"/>'s <c>float</c> produces exactly that: a
        /// <c>1e39 0 0 1e39 0 0 cm</c> makes <c>0 &#215; Infinity</c> appear in the multiply, so
        /// <c>y = (Rise &#215; m.D) + m.F</c> is NaN. The visible damage is not a crash: the shows
        /// join the preceding group and inherit ITS classification, so text that follows a footer
        /// line is emitted inside that line's <c>/Artifact /Pagination /Footer</c> sequence, carries
        /// no MCID, and is unreachable from the structure tree — the one failure this pass is
        /// documented never to cause.
        ///
        /// <para><b>Why it hides.</b> Nothing throws, nothing renders wrong, and the page still
        /// tags: the result is one fewer marked run than the page has lines, which only an assertion
        /// on the run count sees. <c>PdfReadingOrderAnalyzer.Record</c> — the assembly's other
        /// content walk, written from the same algebra — drops a non-finite origin the same
        /// way.</para>
        ///
        /// <para><b>Why a non-finite show is grouped rather than dropped.</b> The analyzer can
        /// discard a measurement it cannot trust; this pass cannot, because dropping the show would
        /// leave real bytes outside every marked-content sequence, which is a §7.1 violation.
        /// Giving it a group of its own keeps the content tagged and merges it with nothing.</para>
        /// </remarks>
        private static bool SameLine(float lineY, float y, float tolerance)
            => IsFinite(lineY) && IsFinite(y) && Math.Abs(lineY - y) <= tolerance;

        /// <summary>
        /// Best-effort text for logging and heuristics. String operands hold font-specific character
        /// codes; without the font's encoding and ToUnicode CMap this is only right for simple
        /// Latin-encoded fonts. Nothing about the emitted marked content depends on it.
        /// </summary>
        private static string ExtractShowText(string op, IReadOnlyList<PdfObject> operands)
        {
            if (operands == null || operands.Count == 0) return string.Empty;

            if (string.Equals(op, PdfOperators.ShowTextArray, StringComparison.Ordinal))
            {
                var array = operands[operands.Count - 1] as PdfArray;
                if (array == null) return string.Empty;
                var sb = new StringBuilder();
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is PdfString s) sb.Append(s.GetText());
                    else if (array[i] is PdfNumber n && n.DoubleValue <= -100d) sb.Append(' ');  // wide kern ≈ a space
                }
                return sb.ToString();
            }

            // Tj, ' and " all take the string as their last operand.
            var last = operands[operands.Count - 1] as PdfString;
            return last != null ? last.GetText() : string.Empty;
        }

        // One implementation, in Core.Text.ContentOperand.TryTrailingNumbers — the one home of the
        // operand coercions, so no copy drifts under a name of its own.
        private static bool TryGetTrailingNumbers(IReadOnlyList<PdfObject> operands, int count, float[] destination)
            => Core.Text.ContentOperand.TryTrailingNumbers(operands, count, destination);

        // ================================================================
        //  Classification and paragraph grouping
        // ================================================================

        private static void ClassifyLines(
            List<LineGroup> lines, PageTagOptions options, Func<RunCandidate, RunClassification> classifier)
        {
            // Whether a line is a list item is a fact about its neighbours, so it is settled for
            // the whole page before any line is classified. Skipped entirely when the caller has
            // not asked for it, so the ordinary pass does no extra work and sees no extra roles.
            bool[] listItems = options.InferLists
                ? ListLineDetector.Detect(
                    Project(lines, l => l.BuildText()),
                    Project(lines, l => l.X),
                    Project(lines, l => l.FontSize))
                : null;

            for (int i = 0; i < lines.Count; i++)
            {
                LineGroup line = lines[i];

                // A fresh candidate per line: the classifier is caller-supplied and may keep the
                // reference (for logging, for a second pass), which a recycled instance would
                // silently corrupt.
                var candidate = new RunCandidate
                {
                    PageHeight     = options.PageHeight,
                    PageBottom     = options.PageBottom,
                    MedianFontSize = options.MedianFontSize,
                    Options        = options,
                    Y              = line.Y,
                    X              = line.X,
                    FontSize       = line.FontSize,
                    Text           = line.BuildText(),
                    LineIndex      = i,
                    PreviousY      = i > 0 ? lines[i - 1].Y : float.NaN,
                    IsListItem     = listItems != null && listItems[i],

                    // Settled before any line was classified — by SplitTableCells, which also
                    // produced these lines. A line the grid did not name carries the -1/0 its
                    // LineGroup was built with, which is what IsTableCell reads as "not a cell".
                    TableRowIndex    = line.TableRowIndex,
                    TableColumnIndex = line.TableColumnIndex,
                    TableColumnCount = line.TableColumnCount,
                };

                line.Classification = classifier(candidate);
            }
        }

        /// <summary>
        /// Replaces each row of an inferred table with one line per cell, and stamps every cell
        /// with its position in the grid.
        /// </summary>
        /// <remarks>
        /// <para><b>The rebuild happens in the text objects, not in the flat list.</b> The two hold
        /// the same <c>LineGroup</c> objects, and it is <c>TextObjectInfo.Lines</c> that
        /// <see cref="BuildRunSegments"/> reads when the byte ranges to wrap are chosen — a flat
        /// list rewritten on its own would classify cells that no splice ever emitted. The flat
        /// list is rebuilt from the text objects afterwards so the two stay the same lines.</para>
        ///
        /// <para><b>A row already drawn as one text object per cell is stamped, not rebuilt.</b>
        /// That shape — the one a word processor emits — arrives here as one line per cell
        /// already, and replacing each with an identical copy would throw away the memoised text
        /// and the offsets for nothing.</para>
        ///
        /// <para><b>Cells are emitted in content order.</b> Their <em>column</em> comes from where
        /// their ink starts, but their order is the order the stream draws them, because the
        /// splice inserts by byte offset and a marked-content sequence cannot enclose bytes that
        /// come before it.</para>
        /// </remarks>
        private static List<LineGroup> SplitTableCells(
            List<TextObjectInfo> textObjects, List<LineGroup> lines, PageTagOptions options)
        {
            TableGridDetector.Grid grid = TableGridDetector.Detect(
                Project(lines, l => l.Y),
                Project(lines, l => l.FontSize),
                Project(lines, ShowXs),
                options.SameLineTolerance);

            if (grid.IsEmpty) return lines;

            // The flat list was built by concatenating each text object's lines in order, so the
            // flat index of a line is its position in that concatenation. Recomputing it rather
            // than looking the line up by reference keeps this free of an identity-keyed map.
            int flat = 0;
            for (int t = 0; t < textObjects.Count; t++)
            {
                List<LineGroup> owned = textObjects[t].Lines;
                var rebuilt = new List<LineGroup>(owned.Count);
                bool changed = false;

                for (int k = 0; k < owned.Count; k++, flat++)
                {
                    LineGroup line = owned[k];
                    int table = grid.TableOfLine[flat];
                    int[] columns = table >= 0 ? grid.ColumnOfShow[flat] : null;

                    // A column per show is what the detector promises; anything else is a line it
                    // did not name, and it travels through untouched.
                    if (columns == null || columns.Length == 0 || columns.Length != line.Shows.Count)
                    {
                        rebuilt.Add(line);
                        continue;
                    }

                    if (columns.Length == 1)
                    {
                        Stamp(line, grid, flat, columns[0]);
                        rebuilt.Add(line);
                        continue;
                    }

                    for (int i = 0; i < line.Shows.Count; i++)
                    {
                        ShowOp show = line.Shows[i];
                        var cell = new LineGroup
                        {
                            Y           = line.Y,
                            FontSize    = show.FontSize > 0f ? show.FontSize : line.FontSize,
                            StartOffset = show.StartOffset,
                            EndOffset   = show.EndOffset,
                        };
                        cell.Shows.Add(show);

                        // NaN is never a left edge, exactly as in RecordShow: a non-finite X must
                        // not silently become the geometry a classifier reads.
                        if (IsFinite(show.X)) cell.X = show.X;

                        Stamp(cell, grid, flat, columns[i]);
                        rebuilt.Add(cell);
                    }

                    changed = true;
                }

                if (!changed) continue;
                owned.Clear();
                owned.AddRange(rebuilt);
            }

            var result = new List<LineGroup>(lines.Count);
            for (int t = 0; t < textObjects.Count; t++) result.AddRange(textObjects[t].Lines);
            return result;
        }

        /// <summary>Records one cell's position in the grid on the line that carries it.</summary>
        private static void Stamp(LineGroup line, TableGridDetector.Grid grid, int flat, int column)
        {
            line.TableId          = grid.TableOfLine[flat];
            line.TableRowIndex    = grid.RowOfLine[flat];
            line.TableColumnIndex = column;
            line.TableColumnCount = grid.ColumnsOfLine[flat];
        }

        /// <summary>Every show's X on one line, in content order.</summary>
        private static float[] ShowXs(LineGroup line)
        {
            var xs = new float[line.Shows.Count];
            for (int i = 0; i < line.Shows.Count; i++) xs[i] = line.Shows[i].X;
            return xs;
        }

        /// <summary>One property of every line, in page order.</summary>
        private static T[] Project<T>(List<LineGroup> lines, Func<LineGroup, T> select)
        {
            var values = new T[lines.Count];
            for (int i = 0; i < lines.Count; i++) values[i] = select(lines[i]);
            return values;
        }

        /// <summary>
        /// Groups consecutive paragraph lines into logical paragraphs, matching the existing
        /// <c>AutoTagOptions.ParagraphGapThreshold</c> semantics: PDF Y grows upward, so the gap
        /// between two stacked lines is <c>previousY − y − lineHeight</c>, and only a gap
        /// <em>larger</em> than the threshold starts a new paragraph. An upward jump (negative gap,
        /// e.g. the top of the next column) deliberately does not split, exactly as before.
        /// Any non-paragraph line — a heading or an artifact — ends the current paragraph.
        /// </summary>
        private static void AssignParagraphIds(List<LineGroup> lines, PageTagOptions options)
        {
            int nextId = 0;
            int currentId = -1;
            bool previousWasParagraph = false;
            float previousY = 0f;
            float previousHeight = 0f;

            for (int i = 0; i < lines.Count; i++)
            {
                LineGroup line = lines[i];
                bool isParagraph = !line.Classification.IsArtifact
                                   && string.Equals(line.Classification.Role, StructureTypes.Paragraph, StringComparison.Ordinal);

                if (!isParagraph)
                {
                    line.ParagraphId     = nextId++;
                    previousWasParagraph = false;
                    continue;
                }

                if (previousWasParagraph)
                {
                    float gap = previousY - line.Y - previousHeight;
                    if (gap > options.ParagraphGapThreshold) currentId = nextId++;
                }
                else
                {
                    currentId = nextId++;
                }

                line.ParagraphId     = currentId;
                previousWasParagraph = true;
                previousY            = line.Y;
                previousHeight       = line.FontSize;
            }
        }

        // ================================================================
        //  Run segmentation inside a text object
        // ================================================================

        private sealed class RunSegment
        {
            public RunClassification Classification;
            public int   ParagraphId;
            public int   StartOffset;
            public int   EndOffset;
            public float Y;
            public float FontSize;
            public int   TableId = -1;
            public int   TableRowIndex = -1;

            /// <summary>
            /// The segment's text. While lines are still being merged this holds the first line's
            /// text and is null once <see cref="_accumulator"/> has taken over; it is final only
            /// after <see cref="MaterialiseText"/>.
            /// </summary>
            public string Text;

            /// <summary>
            /// Accumulator for a multi-line segment, created lazily when a second line joins.
            /// Merging must never round-trip the accumulated text through
            /// <c>ToString()</c> once per line: that copies the whole run twice for
            /// every line appended, which is O(lines² × charsPerLine) and allocates a
            /// progressively larger string each time (straight onto the large object heap for a
            /// long run). A page is free to put an unbounded number of lines in one paragraph —
            /// nothing in the layout heuristics caps it, and a stream that alternates its baseline
            /// by a couple of points produces one LineGroup per show operator while
            /// <c>AssignParagraphIds</c> keeps them all in a single paragraph — so this has to be
            /// linear, not merely "fast enough for real documents".
            /// </summary>
            private StringBuilder _accumulator;

            internal void AppendLine(LineGroup line)
            {
                if (_accumulator == null)
                {
                    _accumulator = new StringBuilder(Text ?? string.Empty, (Text != null ? Text.Length : 0) + 32);
                    Text = null;
                }

                _accumulator.Append(' ');
                line.AppendTextTo(_accumulator);
            }

            /// <summary>Materialises <see cref="Text"/> exactly once, after all merging is done.</summary>
            internal void MaterialiseText()
            {
                if (_accumulator == null) return;
                Text         = _accumulator.ToString();
                _accumulator = null;
            }
        }

        /// <summary>
        /// Merges the lines of one text object into runs: consecutive lines with the same
        /// classification and the same paragraph belong to one marked-content sequence, so a
        /// three-line paragraph drawn inside a single <c>BT…ET</c> yields one MCID rather than three.
        /// </summary>
        private static List<RunSegment> BuildRunSegments(TextObjectInfo textObject)
        {
            var segments = new List<RunSegment>();

            for (int i = 0; i < textObject.Lines.Count; i++)
            {
                LineGroup line = textObject.Lines[i];
                RunSegment last = segments.Count > 0 ? segments[segments.Count - 1] : null;

                bool extend = last != null
                              && last.ParagraphId == line.ParagraphId
                              && SameClassification(last.Classification, line.Classification);

                if (extend)
                {
                    last.EndOffset = line.EndOffset;
                    if (line.FontSize > last.FontSize) last.FontSize = line.FontSize;
                    last.AppendLine(line);
                }
                else
                {
                    segments.Add(new RunSegment
                    {
                        Classification = line.Classification,
                        ParagraphId    = line.ParagraphId,
                        StartOffset    = line.StartOffset,
                        EndOffset      = line.EndOffset,
                        Y              = line.Y,
                        FontSize       = line.FontSize,
                        TableId        = line.TableId,
                        TableRowIndex  = line.TableRowIndex,
                        Text           = line.BuildText(),
                    });
                }
            }

            // One ToString() per segment, not one per merged line.
            for (int i = 0; i < segments.Count; i++) segments[i].MaterialiseText();

            return segments;
        }

        private static bool SameClassification(RunClassification a, RunClassification b)
            => a.IsArtifact == b.IsArtifact
            && string.Equals(a.Role, b.Role, StringComparison.Ordinal)
            && string.Equals(a.ArtifactType, b.ArtifactType, StringComparison.Ordinal)
            && string.Equals(a.ArtifactSubtype, b.ArtifactSubtype, StringComparison.Ordinal);

        // ================================================================
        //  Splicing
        // ================================================================

        private enum InsertionKind
        {
            /// <summary>An <c>EMC</c>. Sorts before an <c>Open</c> at the same offset so sequences never interleave.</summary>
            Close = 0,

            /// <summary>A <c>BDC</c> with its property list.</summary>
            Open = 1,
        }

        private sealed class Insertion
        {
            public readonly int Offset;
            public readonly InsertionKind Kind;
            public readonly byte[] Bytes;
            public readonly int Sequence;

            public Insertion(int offset, InsertionKind kind, byte[] bytes, int sequence)
            {
                Offset   = offset;
                Kind     = kind;
                Bytes    = bytes;
                Sequence = sequence;
            }
        }

        private static byte[] BuildOpenSequence(TaggedRun run)
        {
            var sb = new StringBuilder(64);
            sb.Append('\n');
            if (run.IsArtifact)
            {
                // ISO 32000-1 Table 330 ("Additional entries specific to an artifact"): the
                // property list carries /Type — Pagination, Layout, Page or Background — and, for
                // a pagination artifact, /Subtype (Header, Footer or Watermark). Note /Type, not
                // /O: /O is the owner key of an *attribute* object (Table 326), a different thing.
                // A property list requires BDC, not BMC.
                //
                // /BBox and /Attached are Optional and are not emitted. Accessibility checkers use /BBox
                // to reason about an artifact's extent, so emitting it would be an improvement;
                // it is not a conformance requirement and is left for a later pass.
                sb.Append("/Artifact <</Type /").Append(run.ArtifactType);
                if (!string.IsNullOrEmpty(run.ArtifactSubtype))
                    sb.Append(" /Subtype /").Append(run.ArtifactSubtype);
                sb.Append(">> BDC\n");
            }
            else
            {
                sb.Append('/').Append(run.Role)
                  .Append(" <</MCID ").Append(run.Mcid.ToString(CultureInfo.InvariantCulture)).Append(">> BDC\n");
            }
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Rejects anything that would need <c>#xx</c> escaping (or would produce a malformed name)
        /// when written after a solidus. Structure roles and artifact types are drawn from small
        /// controlled vocabularies, so a value outside this set means the classifier is wrong and
        /// the page must not be modified.
        /// </summary>
        private static bool IsWritableName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 127) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                          || c == '_' || c == '-' || c == '.';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Copies <paramref name="original"/> and inserts each fragment at its offset. Insertions
        /// are sorted by offset, then closes before opens at an identical offset (which happens when
        /// a producer wrote two operations with no separating whitespace), then by creation order.
        /// Every byte of the original ends up in the output exactly once, in the same order.
        /// </summary>
        private static byte[] Splice(byte[] original, List<Insertion> insertions)
        {
            insertions.Sort((x, y) =>
            {
                int compare = x.Offset.CompareTo(y.Offset);
                if (compare != 0) return compare;
                compare = ((int)x.Kind).CompareTo((int)y.Kind);
                if (compare != 0) return compare;
                return x.Sequence.CompareTo(y.Sequence);
            });

            int total = original.Length;
            for (int i = 0; i < insertions.Count; i++) total += insertions[i].Bytes.Length;

            var output = new byte[total];
            int source = 0, destination = 0;

            for (int i = 0; i < insertions.Count; i++)
            {
                Insertion insertion = insertions[i];
                int copy = insertion.Offset - source;
                if (copy > 0)
                {
                    Buffer.BlockCopy(original, source, output, destination, copy);
                    destination += copy;
                    source      += copy;
                }
                Buffer.BlockCopy(insertion.Bytes, 0, output, destination, insertion.Bytes.Length);
                destination += insertion.Bytes.Length;
            }

            int tail = original.Length - source;
            if (tail > 0) Buffer.BlockCopy(original, source, output, destination, tail);
            return output;
        }

        // ================================================================
        //  Verification
        // ================================================================

        /// <summary>
        /// Re-scans the spliced bytes and proves four properties. Returns null when the stream is
        /// good, or a description of the first violation.
        ///
        /// <list type="number">
        ///   <item><description>The result still scans cleanly (no construct became unbounded).</description></item>
        ///   <item><description><c>BT</c>/<c>ET</c> are balanced and never nested.</description></item>
        ///   <item><description><c>BDC</c>/<c>EMC</c> are balanced, never go negative, and the
        ///     marked-content depth at each <c>ET</c> equals the depth at its <c>BT</c> — i.e. no
        ///     sequence straddles a text-object boundary.</description></item>
        ///   <item><description>Removing our <c>BDC</c>/<c>EMC</c> reproduces the original operator
        ///     sequence exactly. This is the decisive check: if an insertion had landed inside a
        ///     string, a comment or an inline image, the surrounding bytes would re-tokenise
        ///     differently and this comparison would fail. It is only sound because the page was
        ///     already refused if it contained marked content of its own.</description></item>
        /// </list>
        /// </summary>
        private static string ValidateSplicedStream(
            byte[] spliced, IReadOnlyList<ContentToken> originalTokens, int expectedInsertions)
        {
            ContentScanResult rescan = ContentStreamScanner.ScanDetailed(spliced);
            if (!rescan.IsSafe) return "spliced stream no longer scans cleanly: " + rescan.Error;

            IReadOnlyList<ContentToken> tokens = rescan.Tokens;

            if (tokens.Count != originalTokens.Count + expectedInsertions)
            {
                return "operator count changed by " +
                       (tokens.Count - originalTokens.Count).ToString(CultureInfo.InvariantCulture) +
                       " instead of the expected " + expectedInsertions.ToString(CultureInfo.InvariantCulture);
            }

            int textDepth = 0, markDepth = 0, markDepthAtBt = 0;
            int originalIndex = 0;

            for (int i = 0; i < tokens.Count; i++)
            {
                string op = tokens[i].Operator;

                switch (op)
                {
                    case PdfOperators.BeginText:
                        if (textDepth != 0) return "BT nested inside another text object";
                        textDepth     = 1;
                        markDepthAtBt = markDepth;
                        break;

                    case PdfOperators.EndText:
                        if (textDepth != 1) return "ET without a matching BT";
                        if (markDepth != markDepthAtBt)
                            return "a marked-content sequence straddles a BT/ET boundary";
                        textDepth = 0;
                        break;

                    case PdfOperators.BeginMarkedContentProps:
                    case PdfOperators.BeginMarkedContent:
                        markDepth++;
                        break;

                    case PdfOperators.EndMarkedContent:
                        markDepth--;
                        if (markDepth < 0) return "EMC without a matching BDC";
                        break;
                }

                // Compare against the original by matching first and only then treating an
                // unmatched BDC/EMC as one of ours.
                //
                // Skipping EVERY BDC and EMC would be right only while the input carries none: a
                // page being *completed* already has some, and they appear in originalTokens too, so
                // skipping them on one side and not the other would make the two sequences diverge
                // at the first one. Matching first is skipping exactly when the input has no marked
                // content, and BMC is deliberately
                // not in the insertion class because this pass never writes one.
                bool matches = originalIndex < originalTokens.Count
                               && string.Equals(op, originalTokens[originalIndex].Operator,
                                                StringComparison.Ordinal);
                if (!matches)
                {
                    if (op == PdfOperators.BeginMarkedContentProps || op == PdfOperators.EndMarkedContent)
                        continue;
                    if (originalIndex >= originalTokens.Count)
                        return "spliced stream has more original-operator tokens than the input";
                    return "operator sequence diverged at index " +
                           originalIndex.ToString(CultureInfo.InvariantCulture) + " (expected '" +
                           originalTokens[originalIndex].Operator + "', found '" + op + "')";
                }
                originalIndex++;
            }

            if (originalIndex != originalTokens.Count) return "spliced stream lost original operators";
            if (textDepth != 0) return "unterminated text object";
            if (markDepth != 0) return "unbalanced marked-content sequences";
            return null;
        }

        /// <summary>
        /// Independently re-parses the stream with the production parser
        /// (<see cref="ContentStreamParser"/>) and compares the counts of the structural operators
        /// that the splice depends on.
        ///
        /// <para>
        /// The two implementations tokenise separately: if they disagree about how many text objects
        /// or marked-content brackets a stream contains, one of them has mis-bounded a string or an
        /// inline image, and no amount of local reasoning can tell which. Refusing on disagreement
        /// turns a would-be corruption into a skipped page.
        /// </para>
        /// </summary>
        /// <returns>A description of the disagreement, or null when the two agree.</returns>
        private static string CrossCheckWithCoreParser(byte[] content, IReadOnlyList<ContentToken> tokens)
        {
            List<ContentStreamOperation> operations;
            try
            {
                operations = ContentStreamParser.Parse(content);
            }
            catch (Exception ex)
            {
                _lastTagError = "Cross-check parse failed: " + ex.Message;
                AccessibilityDiagnostics.Report("ContentStreamTagger.TagPage",
                    "the production content-stream parser could not parse this page, so the byte scanner's "
                    + "offsets cannot be cross-checked; the page was left unmodified", ex);
                return "the production parser could not parse this stream (" + ex.Message + ")";
            }
            if (operations == null) return "the production parser returned no operations";

            int scannerBt = 0, scannerEt = 0, scannerMarks = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                switch (tokens[i].Operator)
                {
                    case PdfOperators.BeginText:               scannerBt++;    break;
                    case PdfOperators.EndText:                 scannerEt++;    break;
                    case PdfOperators.BeginMarkedContentProps:
                    case PdfOperators.BeginMarkedContent:
                    case PdfOperators.EndMarkedContent:        scannerMarks++; break;
                }
            }

            int parserBt = 0, parserEt = 0, parserMarks = 0;
            for (int i = 0; i < operations.Count; i++)
            {
                switch (operations[i].Operator)
                {
                    case PdfOperators.BeginText:               parserBt++;    break;
                    case PdfOperators.EndText:                 parserEt++;    break;
                    case PdfOperators.BeginMarkedContentProps:
                    case PdfOperators.BeginMarkedContent:
                    case PdfOperators.EndMarkedContent:        parserMarks++; break;
                }
            }

            if (scannerBt != parserBt)
                return "BT count " + scannerBt.ToString(CultureInfo.InvariantCulture) + " vs " +
                       parserBt.ToString(CultureInfo.InvariantCulture);
            if (scannerEt != parserEt)
                return "ET count " + scannerEt.ToString(CultureInfo.InvariantCulture) + " vs " +
                       parserEt.ToString(CultureInfo.InvariantCulture);
            if (scannerMarks != parserMarks)
                return "marked-content operator count " + scannerMarks.ToString(CultureInfo.InvariantCulture) +
                       " vs " + parserMarks.ToString(CultureInfo.InvariantCulture);

            return null;
        }
    }
}
