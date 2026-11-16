// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Builder.Model
{
    /// <summary>
    /// Model-level defaults shared by the document model and every exporter, so that a
    /// "did the caller change this?" test in an exporter cannot drift from the property
    /// initializer it is comparing against.
    /// </summary>
    internal static class DocDefaults
    {
        /// <summary>Default run font size in points (the 12&#160;pt body default of both CSS and Word).</summary>
        internal const double FontSizePt = 12;

        /// <summary>Default text / border colour.</summary>
        internal const string TextColorHex = "#000000";

        /// <summary>
        /// Default page width in points — A4 rounded to whole points.
        ///
        /// <para>Deliberately <b>not</b> <see cref="PdfPageSizes.A4Width"/> (595.28&#160;pt): every
        /// document this model has ever produced is 595&#160;&#215;&#160;842, and adopting the
        /// two-decimal spelling would move every page edge. Changing it is a rendering change, not
        /// a refactor.</para>
        /// </summary>
        internal const double PageWidthPt = 595;

        /// <summary>Companion to <see cref="PageWidthPt"/> — A4 height rounded to whole points
        /// (<see cref="PdfPageSizes.A4Height"/> is 841.89).</summary>
        internal const double PageHeightPt = 842;
    }

    /// <summary>Root document — contains metadata and sections.</summary>
    public sealed class Document
    {
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Subject { get; set; } = "";
        public List<Section> Sections { get; } = new();

        /// <summary>
        /// Emit tagged (accessible) PDF: a logical structure tree, marked content, and page
        /// furniture marked as artifacts. Default <c>false</c>.
        ///
        /// <para>Opt-in because a tag tree is only as good as the roles feeding it. A converter
        /// that has not been taught to distinguish a heading from a paragraph would emit a valid
        /// tree of undifferentiated <c>/P</c> elements — which passes structural checks while
        /// telling a screen reader nothing, and is worse than an honestly untagged document
        /// because it silences the finding.</para>
        /// </summary>
        public bool Tagged { get; set; }

        /// <summary>
        /// The document's natural language for the catalog <c>/Lang</c> — e.g. <c>"he-IL"</c>.
        /// Empty writes nothing: a wrong language makes a screen reader pronounce Hebrew with
        /// English phonemes while the conformance check reports clean, which is worse than none.
        /// </summary>
        public string Language { get; set; } = "";

        public void SaveAsPdf(System.IO.Stream output) => new Export.PdfExporter().Export(this, output);

        /// <summary>
        /// Writes PDF using caller-supplied font sources and failure policy — see
        /// <see cref="Export.PdfExportOptions"/>.
        ///
        /// <para>An <b>overload</b> rather than an optional parameter on the method above: this
        /// assembly ships as a NuGet package, and an optional parameter changes the compiled
        /// signature, so a consumer built against the original would take a
        /// <see cref="System.MissingMethodException"/>.</para>
        /// </summary>
        public void SaveAsPdf(System.IO.Stream output, Export.PdfExportOptions options)
            => new Export.PdfExporter(options).Export(this, output);

        public void SaveAsDocx(System.IO.Stream output) => new Export.DocxExporter().Export(this, output);
        public void SaveAsHtml(System.IO.Stream output) => new Export.HtmlExporter().Export(this, output);
        public void SaveAsOdt(System.IO.Stream output) => new Export.OdtExporter().Export(this, output);

        public byte[] ToPdf() { using var ms = new System.IO.MemoryStream(); SaveAsPdf(ms); return ms.ToArray(); }

        /// <summary>PDF bytes, using caller-supplied font sources and failure policy.</summary>
        public byte[] ToPdf(Export.PdfExportOptions options)
        { using var ms = new System.IO.MemoryStream(); SaveAsPdf(ms, options); return ms.ToArray(); }
        public byte[] ToDocx() { using var ms = new System.IO.MemoryStream(); SaveAsDocx(ms); return ms.ToArray(); }
        public byte[] ToHtml() { using var ms = new System.IO.MemoryStream(); SaveAsHtml(ms); return ms.ToArray(); }
        public byte[] ToOdt() { using var ms = new System.IO.MemoryStream(); SaveAsOdt(ms); return ms.ToArray(); }
    }

    /// <summary>
    /// Writing direction for a section or paragraph. <see cref="RightToLeft"/> selects
    /// RTL base direction (Hebrew/Arabic): default right-alignment, RTL run order, and
    /// (PDF) bidi reordering + Arabic shaping; (DOCX) <c>w:bidi</c>/<c>w:rtl</c>;
    /// (HTML) <c>dir="rtl"</c>; (ODT) <c>style:writing-mode="rl-tb"</c>.
    /// </summary>
    public enum TextDirection
    {
        LeftToRight,
        RightToLeft,

        /// <summary>
        /// Detect the base direction from the content, per UAX #9 P2/P3 (the first strong
        /// character decides).
        ///
        /// <para>This is what HTML's <c>dir="auto"</c> means, and it is the honest answer when
        /// nothing declared a direction: a Hebrew paragraph in a document whose section never set
        /// a direction is not rendered left-to-right, which would put its words in the wrong order
        /// and align it to the wrong margin.</para>
        ///
        /// <para>Not the default for <see cref="Section.Direction"/>, which stays
        /// <see cref="LeftToRight"/> so no existing caller's output moves.</para>
        /// </summary>
        Auto,
    }

    /// <summary>A section defines page layout and contains content elements.</summary>
    public sealed class Section
    {
        public double PageWidthPt { get; set; } = DocDefaults.PageWidthPt;
        public double PageHeightPt { get; set; } = DocDefaults.PageHeightPt;
        public double MarginTop { get; set; } = PdfUnits.PointsPerInch;
        public double MarginBottom { get; set; } = PdfUnits.PointsPerInch;
        public double MarginLeft { get; set; } = PdfUnits.PointsPerInch;
        public double MarginRight { get; set; } = PdfUnits.PointsPerInch;
        /// <summary>Base writing direction for the section (default LTR). Paragraphs that
        /// leave <see cref="Paragraph.Direction"/> unset inherit this.</summary>
        public TextDirection Direction { get; set; } = TextDirection.LeftToRight;
        /// <summary>
        /// The section's own layout is right to left — DOCX's section <c>w:bidi</c> (ECMA-376-1
        /// §17.6.1), which "only affects section-level properties" (column order, the gutter's side)
        /// and is not the direction of the section's text: paragraphs and tables do not inherit it.
        /// Carried so a DOCX export writes it back; a right-to-left <see cref="Direction"/> writes it too.
        /// </summary>
        public bool RightToLeftLayout { get; set; }

        /// <summary>Default header (used on every page unless a per-page variant applies).</summary>
        public Header? Header { get; set; }
        /// <summary>Default footer.</summary>
        public Footer? Footer { get; set; }
        /// <summary>First-page header variant (selected when <see cref="TitlePage"/>).</summary>
        public Header? FirstPageHeader { get; set; }
        public Footer? FirstPageFooter { get; set; }
        /// <summary>Even-page header variant (selected when even/odd headers are enabled).</summary>
        public Header? EvenPageHeader { get; set; }
        public Footer? EvenPageFooter { get; set; }
        /// <summary>Use the first-page header/footer variant on the section's first page.</summary>
        public bool TitlePage { get; set; }
        /// <summary>Use distinct even/odd header/footer variants.</summary>
        public bool EvenAndOddHeaders { get; set; }

        /// <summary>Optional page border drawn around each page of the section.</summary>
        public PageBorders? PageBorders { get; set; }
        /// <summary>Page-number numbering format for PAGE fields ("decimal","upperRoman","lowerRoman","upperLetter","lowerLetter","hebrew1","hebrew2").</summary>
        public string PageNumberFormat { get; set; } = "decimal";
        /// <summary>Optional explicit starting page number for this section.</summary>
        public int? PageNumberStart { get; set; }

        /// <summary>
        /// The regions of each page the flow pours into, in order: when one is full the flow goes on
        /// at the top of the next, and after the last onto a new page — XFA's content areas (XFA 3.3
        /// chapter 8, "Flowing Between ContentArea Objects": "it pours content into the current
        /// contentArea, then into its next sibling"), a page of columns. Empty (the default) is the one region the margins leave, as before.
        /// </summary>
        /// <remarks>
        /// <para>The header, footer, page borders and blocks placed on the page keep measuring from
        /// the margins and the page; each region's footnotes are drawn at its own foot.</para>
        /// <para>A block that continues from one region into the next moves with it, its lines and
        /// rows at the new region's left edge. Entering a region of another width, a box of no width of
        /// its own takes the new width (a nested one its parent's new band), a table of no width of its
        /// own resolves its columns at it, and a paragraph of text, in a box or not, is broken again at
        /// it; a box or table of a stated width keeps it, and a paragraph that cannot be split (one with
        /// a box of its own, an image, a footnote) the width it began at. The PDF exporter reads this;
        /// the HTML, DOCX and ODT exporters lay the section out in its margins.</para>
        /// </remarks>
        public List<ContentArea> ContentAreas { get; } = new();

        public List<IDocElement> Children { get; } = new();
    }

    /// <summary>
    /// One region of a page the flow pours into (<see cref="Section.ContentAreas"/>), measured from
    /// the page's top-left in points, as <see cref="AbsolutePlacement"/> is.
    /// </summary>
    public sealed class ContentArea
    {
        /// <summary>The region's left edge, from the page's left edge.</summary>
        public double LeftPt { get; set; }

        /// <summary>The region's top edge, from the page's top edge.</summary>
        public double TopPt { get; set; }

        /// <summary>The region's width; not negative.</summary>
        public double WidthPt { get; set; }

        /// <summary>The region's height; not negative.</summary>
        public double HeightPt { get; set; }
    }

    /// <summary>Per-side page border definition (maps from DOCX <c>w:pgBorders</c>).</summary>
    public sealed class PageBorders
    {
        public Border? Top { get; set; }
        public Border? Bottom { get; set; }
        public Border? Left { get; set; }
        public Border? Right { get; set; }
        /// <summary>True = offset measured from the page edge; false = from the text margin.</summary>
        public bool OffsetFromPage { get; set; } = true;
        /// <summary>Inset (points) from the chosen reference for each side.</summary>
        public double SpaceTop { get; set; } = 24;
        public double SpaceBottom { get; set; } = 24;
        public double SpaceLeft { get; set; } = 24;
        public double SpaceRight { get; set; } = 24;
    }

    /// <summary>A single border edge: width (pt), colour, and a "none" flag.</summary>
    public sealed class Border
    {
        public double Width { get; set; } = 0.5;
        public string Color { get; set; } = DocDefaults.TextColorHex;
        public bool None { get; set; }
        public Border() { }
        public Border(double width, string color) { Width = width; Color = color; }

        /// <summary>
        /// Whether <paramref name="border"/> paints anything: present, not <see cref="None"/>, of a
        /// positive width (a width of 0 is no border). The one reading every exporter and the HTML
        /// converter take.
        /// </summary>
        public static bool Paints([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Border? border)
            => border is { None: false } && border.Width > 0;

        /// <summary>The width <paramref name="border"/> paints: its <see cref="Width"/> when it <see cref="Paints"/>, else 0.</summary>
        public static double PaintedWidth(Border? border) => Paints(border) ? border.Width : 0;
    }

    /// <summary>Which edge a floated paragraph is pinned to, and whether it floats at all.</summary>
    public enum FloatSide
    {
        /// <summary>Not floated: the paragraph takes the whole band, as every paragraph did.</summary>
        None = 0,

        /// <summary>Pinned to the band's left edge; following text flows down its right.</summary>
        Left,

        /// <summary>Pinned to the band's right edge; following text flows down its left.</summary>
        Right,
    }

    /// <summary>Marker interface for all document content elements.</summary>
    public interface IDocElement { }

    /// <summary>A paragraph containing inline runs.</summary>
    public sealed class Paragraph : IDocElement
    {
        /// <summary>
        /// Paragraph alignment. Defaults to <see cref="TextAlignment.Start"/> — the leading edge
        /// for the paragraph's direction — so that an explicitly set <see cref="TextAlignment.Left"/>
        /// stays distinguishable from "unset" inside right-to-left text.
        /// </summary>
        public TextAlignment Alignment { get; set; } = TextAlignment.Start;

        /// <summary>
        /// Pins this paragraph to one edge of the band and lets the paragraphs after it flow
        /// alongside, returning to the full band once they clear its bottom.
        /// <see cref="FloatSide.None"/> (the default) does not float.
        /// </summary>
        /// <remarks>
        /// <para><b>The difference from a two-column band is re-flow, and it is the whole point.</b>
        /// A float and its neighbour modelled as a two-cell table keeps the text in its column
        /// forever, so a short float leaves a channel of whitespace running to the bottom of the
        /// block. CSS returns the text to the full measure the moment it passes the float's
        /// bottom edge, and so does this.</para>
        ///
        /// <para><b>Honoured exactly by the PDF exporter</b>, which flows text down the float's
        /// side and back to the full measure past its bottom, <b>and by HTML</b>, which writes
        /// <c>float</c> and <c>width</c> and lets the reader do the layout.</para>
        ///
        /// <para><b>DOCX and ODT render a floated paragraph as an ordinary block and say so</b>
        /// through <c>BuilderDiagnosticKind.ContentApproximated</c> — a real float there means a
        /// framed or anchored object in each format's own vocabulary, which is a separate piece of
        /// work. The report matters because the two outputs are indistinguishable from a document
        /// that never floated.</para>
        ///
        /// <para><b>Requires <see cref="FloatWidth"/>.</b> CSS shrink-to-fit needs a measurement
        /// pass this model does not run, so the width is stated rather than inferred; a float with
        /// no width is ignored and reported rather than guessed at.</para>
        /// </remarks>
        public FloatSide Float { get; set; } = FloatSide.None;

        /// <summary>
        /// Width in points of a floated paragraph. Ignored unless <see cref="Float"/> is set, and
        /// required when it is.
        /// </summary>
        /// <remarks>
        /// Points rather than a fraction, because the band a float sits in is not always the page:
        /// a float inside a table cell or a multi-column section is measured against that box, and
        /// a fraction would have to be resolved against something the caller cannot see.
        /// </remarks>
        public double FloatWidth { get; set; }

        /// <summary>
        /// Starts this paragraph below every float still in effect, instead of alongside them.
        /// The model's equivalent of CSS <c>clear</c>.
        /// </summary>
        /// <remarks>
        /// <b>Without it a float has no end but its own height.</b> A float taller than the text
        /// beside it would keep narrowing every following paragraph to the bottom of the page,
        /// which is right for flow content and wrong for the heading or rule an author put after
        /// the float to close the section off. <c>clear</c> is the statement that the float's band
        /// is over.
        /// <para>
        /// Clears both sides. CSS distinguishes <c>left</c>, <c>right</c> and <c>both</c>; this
        /// model has one flag because the distinction only matters with floats on both edges at
        /// once, which nothing in this library produces — and a <c>clear</c> that cleared too much
        /// costs vertical space, while one that cleared too little overlaps text.
        /// </para>
        /// </remarks>
        public bool ClearFloats { get; set; }

        /// <summary>
        /// Name this paragraph can be linked to from elsewhere in the document — the target half of
        /// <see cref="TextRun.LinkAnchor"/>, and the equivalent of an HTML <c>id</c> or a DOCX
        /// bookmark. <c>null</c> (the default) declares nothing.
        ///
        /// <para>Exported as a PDF named destination pointing at the top of the paragraph, so a
        /// link scrolls it into view; it is what <see cref="TextRun.LinkAnchor"/> — set by the DOCX
        /// mapper — points at.</para>
        ///
        /// <para>A duplicate name is a caller error with no correct answer; the PDF exporter keeps
        /// the <b>first</b> and ignores the rest, so a table of contents does not end up with every
        /// entry jumping to the last section.</para>
        /// </summary>
        public string? AnchorName { get; set; }

        /// <summary>
        /// Corner radius in points for this paragraph's background and border; <c>0</c> (the
        /// default) is a square box. CSS <c>border-radius</c>.
        /// </summary>
        public double CornerRadiusPt { get; set; }
        /// <summary>
        /// Four corner radii in points — top-left, top-right, bottom-right, bottom-left, CSS's
        /// order — overriding <see cref="CornerRadiusPt"/> when set: a panel heading rounds its top
        /// corners only. <c>null</c> (the default) is the one radius for all four.
        /// </summary>
        public double[]? CornerRadiiPt { get; set; }
        /// <summary>
        /// The four corners' vertical radii in points, in <see cref="CornerRadiiPt"/>'s order —
        /// CSS's <c>border-radius: x / y</c> — making each corner a quarter ellipse, its horizontal
        /// radius from <see cref="CornerRadiiPt"/>. <c>null</c> (the default) is circular corners.
        /// </summary>
        public double[]? CornerRadiiYPt { get; set; }
        /// <summary>
        /// A corner radius as a percentage of the box — CSS's <c>border-radius: 50%</c> — its
        /// horizontal radius that share of the width and its vertical one of the height, so 50% of
        /// a square is a circle and of an oblong an ellipse; overrides the lengths when positive.
        /// </summary>
        public double CornerRadiusPercent { get; set; }
        /// <summary>
        /// Per-corner horizontal radii as percentages of the box's width, in <see cref="CornerRadiiPt"/>'s
        /// order — CSS's <c>border-radius: 50% 10px</c>, a percentage among lengths: a
        /// positive entry replaces that corner's length, 0 leaves the length. Its vertical radius is
        /// the same share of the height unless <see cref="CornerRadiiYPercent"/> or
        /// <see cref="CornerRadiiYPt"/> says otherwise. <c>null</c> (the default) is no percentage corner.
        /// </summary>
        public double[]? CornerRadiiPercent { get; set; }
        /// <summary>
        /// Per-corner vertical radii as percentages of the box's height, in <see cref="CornerRadiiPt"/>'s
        /// order — the second half of <c>border-radius: 10px / 50%</c>: a positive entry replaces that
        /// corner's vertical length. <c>null</c> (the default) is none stated.
        /// </summary>
        public double[]? CornerRadiiYPercent { get; set; }

        /// <summary>
        /// A drop shadow behind this paragraph's box; <c>null</c> (the default) is none.
        /// </summary>
        public BoxShadow? Shadow { get; set; }
        /// <summary>
        /// The shadows after <see cref="Shadow"/>, in CSS's order — each painted beneath the one
        /// before it, as CSS lists them top first. Empty (the default) for one shadow or none.
        /// </summary>
        public List<BoxShadow> MoreShadows { get; private set; } = new List<BoxShadow>();

        /// <summary>
        /// An affine transform applied to everything this paragraph paints, without changing where
        /// the flow put it — CSS <c>transform</c>, which affects painting and not layout.
        /// <c>null</c> (the default) is none. It is taken about the paragraph's
        /// <see cref="TransformOrigin"/>, and the link and form-field rectangles inside the paragraph
        /// follow it (as the bounds of their turned corners).
        /// </summary>
        public double[]? Transform { get; set; }

        /// <summary>
        /// The point <see cref="Transform"/> turns, scales and skews about — CSS
        /// <c>transform-origin</c> — as four numbers: a fraction of the paragraph's border-box width
        /// and an offset in points from its left edge, then a fraction of its height and an offset in
        /// points down from its top. <c>null</c> (the default) is the centre, CSS's <c>50% 50%</c>;
        /// <c>{ 0, 0, 0, 0 }</c> the top-left corner. The border box is the band the paragraph lays
        /// out in, from the top of its text to the bottom, its spacing before and after excluded.
        /// </summary>
        public double[]? TransformOrigin { get; set; }

        /// <summary>
        /// Takes this paragraph out of the flow and places it at a fixed point on the page;
        /// <c>null</c> (the default) leaves it in the flow. The CSS <c>position: absolute</c> case.
        /// </summary>
        public AbsolutePlacement? Placement { get; set; }

        /// <summary>
        /// Structure role for tagged output — <c>null</c> means <c>/P</c>. Use the constants in
        /// <see cref="Tetitulz.Pdf.Core.Constants.PdfStructureTypes"/>: <c>H1</c>…<c>H6</c>,
        /// <c>Caption</c>, <c>BlockQuote</c>, <c>Code</c>.
        ///
        /// <para>A <c>string</c> rather than an enum so a new role does not require a model
        /// change; the writer validates it.</para>
        /// </summary>
        public string? Role { get; set; }

        /// <summary>
        /// This paragraph is decoration — a rule, a spacer — not document content, so tagged
        /// output marks it as an artifact.
        ///
        /// <para>Separate from <see cref="Role"/> on purpose: <c>Artifact</c> is deliberately not
        /// a structure type, and <c>/S /Artifact</c> is precisely what a PDF/UA checker reports.
        /// Folding it into the role would model the thing the format refuses.</para>
        /// </summary>
        public bool IsArtifact { get; set; }

        public double SpacingBefore { get; set; }
        public double SpacingAfter { get; set; } = 6;
        public double IndentLeft { get; set; }
        public double IndentRight { get; set; }
        public double FirstLineIndent { get; set; }
        /// <summary>Exact line height in points; 0 = automatic (font-size derived).</summary>
        public double LineSpacingPt { get; set; }
        /// <summary>
        /// Line height as a multiple of each line's largest font size, with no floor — CSS's
        /// <c>line-height: normal</c> — used when <see cref="LineSpacingPt"/> is 0. 0 (the default)
        /// is the exporter's own: 1.16 of the size, never under 14pt, which a browser does not have.
        /// </summary>
        public double LineHeightFactor { get; set; }
        /// <summary>Force a page break before this paragraph.</summary>
        public bool PageBreakBefore { get; set; }
        /// <summary>
        /// Keep this paragraph on one page when it fits on one (CSS <c>break-inside: avoid</c>): if
        /// it would straddle a page boundary it starts on the next page instead. A paragraph taller
        /// than a page breaks as usual.
        /// </summary>
        public bool KeepTogether { get; set; }
        /// <summary>
        /// Hyphenate English words where a line breaks (CSS <c>hyphens: auto</c>): when a word does
        /// not fit, the PDF export breaks it at the last of its hyphenation points that fits, drawing a
        /// hyphen — Liang's algorithm over hyph-utf8's American English patterns, a word of ASCII
        /// letters only, at least 2 letters before the break and 3 after. The patterns are English's:
        /// set it for English text (the HTML converter sets it for <c>hyphens: auto</c> under an
        /// English <c>lang</c>). The hyphen is drawn; the paragraph's logical text does not hold it.
        /// </summary>
        public bool Hyphenate { get; set; }
        /// <summary>
        /// Keep this paragraph on the same page as the block after it (CSS <c>break-after:
        /// avoid</c>) — with that block's first line, or all of it when it is kept together itself.
        /// </summary>
        public bool KeepWithNext { get; set; }
        /// <summary>
        /// The fewest lines of this paragraph a page may end with where the paragraph breaks across
        /// it (CSS <c>orphans</c>). 1, the default, sets no limit.
        /// </summary>
        /// <remarks>
        /// With <see cref="Widows"/>, decided by the PDF exporter as each page's part of the
        /// paragraph begins: too few lines before the break and the rest of the paragraph moves to
        /// the next page; too few after it and lines move over from this one. Where no break can
        /// satisfy both and the part already starts a page, it breaks where its lines run out. A
        /// forced break inside the paragraph is not constrained. A block kept with
        /// this paragraph needs its orphans on the same page, not only its first line. Other
        /// exporters ignore both.
        /// </remarks>
        public int Orphans { get; set; } = 1;
        /// <summary>
        /// The fewest lines of this paragraph a page may begin with where the paragraph breaks
        /// across the page before it (CSS <c>widows</c>). 1, the default, sets no limit. See
        /// <see cref="Orphans"/>.
        /// </summary>
        public int Widows { get; set; } = 1;
        /// <summary>Paragraph background shading fill (hex); "" = none.</summary>
        public string Shading { get; set; } = "";
        /// <summary>
        /// Paragraph background image (CSS <c>background-image</c>); <c>null</c> = none. Painted
        /// behind the text on top of <see cref="Shading"/>, exactly as CSS layers the two.
        /// </summary>
        public BackgroundImage? BackgroundImage { get; set; }
        /// <summary>Paragraph borders (w:pBdr); null sides = no border.</summary>
        public Border? BorderTop { get; set; }
        public Border? BorderBottom { get; set; }
        public Border? BorderLeft { get; set; }
        public Border? BorderRight { get; set; }

        /// <summary>
        /// The least distance, in points, between a paragraph's text and its box — the borders,
        /// and the shading and background image that fill it — on every side.
        /// </summary>
        public const double DefaultBoxInsetPt = 2;

        /// <summary>
        /// Per-side distance, in points, between the text and the paragraph's box, as Word's
        /// <c>w:pBdr</c> <c>w:space</c> states it. It never falls below
        /// <see cref="DefaultBoxInsetPt"/>, so 0 (the default) is the box every paragraph had
        /// before these existed.
        /// </summary>
        /// <remarks>
        /// <b>The text does not move</b> — the indents and spacing place it; the inset places the
        /// box around it. CSS padding is both: the HTML mapper puts it in the indent and the
        /// spacing, and states it here as well, so the border lands at the margin edge with the
        /// padding inside it.
        /// </remarks>
        public double BoxInsetTop { get; set; }
        public double BoxInsetRight { get; set; }
        public double BoxInsetBottom { get; set; }
        public double BoxInsetLeft { get; set; }

        /// <summary>
        /// The <c>BoxInset*</c> values are the distances exactly, with no
        /// <see cref="DefaultBoxInsetPt"/> floor: CSS's box, where no padding means the text
        /// touches the border. Off by default — Word's <c>w:space</c> semantics — so DOCX and
        /// hand-built paragraphs keep the floor; the HTML converter sets it.
        /// </summary>
        public bool BoxInsetIsExact { get; set; }

        /// <summary>
        /// The width of the paragraph's box — border, padding and text — as a fraction of the width
        /// it is laid out in, plus <see cref="WidthPt"/>. <c>null</c> with <see cref="WidthPt"/> 0
        /// (the default) is the full width, as before.
        /// </summary>
        /// <remarks>
        /// <b>The box sits at the paragraph's start</b> — the left, or the right in a right-to-left
        /// paragraph — and the space it leaves is on its other side, as CSS gives a block narrower
        /// than its container. One wider than the room it has keeps the room. The HTML converter
        /// sets it from CSS <c>width</c>: a <c>60%</c> progress bar is 0.6.
        /// </remarks>
        public double? WidthFraction { get; set; }

        /// <summary>Points added to <see cref="WidthFraction"/>'s share, or the whole box width when that is <c>null</c>.</summary>
        public double WidthPt { get; set; }

        /// <summary>
        /// Put a narrower box at the paragraph's end — the right, or the left in a right-to-left
        /// paragraph — instead of its start: a block floated to that side with nothing beside it
        /// (the HTML converter's lone float, a Bootstrap progress bar floated left in RTL).
        /// </summary>
        public bool WidthAtEnd { get; set; }

        /// <summary>Paragraph writing direction; <c>null</c> inherits the section's
        /// <see cref="Section.Direction"/>. Set via <c>ParagraphBuilder.Rtl()</c>.</summary>
        public TextDirection? Direction { get; set; }

        /// <summary>
        /// Constant alpha for everything this paragraph paints — shading, background image,
        /// borders, and its runs (multiplied by <see cref="TextRun.Opacity"/>). <c>1</c> = opaque,
        /// the default; values outside 0–1 are clamped at the point of use.
        ///
        /// <para><b>This is constant alpha, not CSS group opacity, and the two differ where the
        /// paragraph's own content overlaps.</b> CSS composites the element once as a unit, so
        /// <c>opacity: .5</c> over a shaded paragraph yields one 50&#160;% layer; PDF
        /// <c>/ca</c> is per-object, so here the text is 50&#160;% <i>over an already-50&#160;%
        /// background</i>. Matching CSS exactly needs a transparency-group Form XObject. The
        /// approximation is reported through
        /// <see cref="Export.BuilderDiagnosticKind.ContentApproximated"/> — but only when it is
        /// observable, i.e. when this paragraph paints something behind its own text.</para>
        ///
        /// <para>A <see cref="TableCell"/> has no opacity of its own: a cell's shading and borders
        /// are painted by <c>TableRenderer</c>, which this property does not reach. A paragraph
        /// inside a cell still honours its own value.</para>
        /// </summary>
        public double Opacity { get; set; } = 1.0;
        /// <summary>Tab stops for this paragraph (resolved positions in points).</summary>
        public List<TabStop> Tabs { get; private set; } = new();
        public List<IInlineElement> Runs { get; private set; } = new();

        /// <summary>
        /// A copy of this paragraph's every property with no runs — its tab stops and shadows copied,
        /// its placement and anchor name left (one paragraph carries them) — for a paragraph laid out
        /// in two parts: the head and tail <see cref="Export.PdfExporter.SplitAtHeight"/> makes (batch
        /// 129). The caller sets what differs between the parts (the spacing, the first-line indent).
        /// </summary>
        public Paragraph CloneWithoutRuns()
        {
            var copy = (Paragraph)MemberwiseClone();
            copy.Runs = new List<IInlineElement>();
            copy.Tabs = new List<TabStop>(Tabs);
            copy.MoreShadows = new List<BoxShadow>(MoreShadows);
            copy.Placement = null;
            copy.AnchorName = null;
            return copy;
        }
    }

    /// <summary>
    /// Paragraph alignment. <see cref="Left"/>, <see cref="Center"/> and <see cref="Right"/> are
    /// <b>physical</b> edges, as in CSS; <see cref="Start"/> and <see cref="End"/> are
    /// <b>logical</b> and follow the writing direction.
    ///
    /// <para><see cref="Start"/> is the default, and that is what makes an explicit
    /// <c>text-align: left</c> meaningful inside right-to-left text. With <c>Left</c> as the
    /// default there was no way to tell "the author asked for the left edge" from "nobody said
    /// anything", so right-to-left paragraphs forced <c>Right</c> for both and the explicit
    /// choice was unreachable. Under LTR <c>Start</c> resolves to <c>Left</c> and under RTL to
    /// <c>Right</c> — exactly the previous behaviour — so no existing output moves.</para>
    /// </summary>
    public enum TextAlignment
    {
        Left,
        Center,
        Right,
        Justify,

        /// <summary>The leading edge: left under LTR, right under RTL.</summary>
        Start,

        /// <summary>The trailing edge: right under LTR, left under RTL.</summary>
        End,
    }

    public enum TabAlignment { Left, Center, Right, Decimal }
    public enum TabLeader { None, Dot, Hyphen, Underscore }

    /// <summary>A paragraph tab stop (position in points, alignment, optional leader).</summary>
    public sealed class TabStop
    {
        public double PosPt { get; set; }
        public TabAlignment Align { get; set; } = TabAlignment.Left;
        public TabLeader Leader { get; set; } = TabLeader.None;

        /// <summary>
        /// Text a leader repeats, as many times as it fits, in place of <see cref="Leader"/>'s character — XFA's
        /// <c>use-content</c> leader. <c>null</c> (the default) or empty for <see cref="Leader"/>'s. The
        /// PDF exporter draws it; an exporter that writes a leader as a character draws <see cref="Leader"/>'s.
        /// </summary>
        public string? LeaderText { get; set; }
    }

    /// <summary>
    /// A drop shadow behind a block's box: an offset copy of it in a single colour.
    /// </summary>
    /// <remarks>
    /// <b>No blur radius.</b> A blurred edge needs either a soft mask or a gradient per edge, and
    /// a shadow drawn at its stated offset with a hard edge is recognisably the same decoration —
    /// whereas one silently drawn without its offset would not be. The exporter reports the
    /// approximation rather than implying a blur was applied.
    /// </remarks>
    public sealed class BoxShadow
    {
        /// <summary>Horizontal offset in points; positive is to the right.</summary>
        public double OffsetXPt { get; set; }

        /// <summary>Vertical offset in points; positive is DOWN, as CSS states it.</summary>
        public double OffsetYPt { get; set; }

        /// <summary>Shadow colour as <c>#rrggbb</c>.</summary>
        public string Color { get; set; } = "#808080";

        /// <summary>
        /// CSS's blur radius, in points; 0 (the default) is a hard edge. The shadow is drawn through a
        /// Gaussian of half this deviation (CSS Backgrounds 3 §7.1), reaching this far past the box.
        /// </summary>
        public double BlurPt { get; set; }

        /// <summary>
        /// CSS's spread distance, in points: the shadow's box is this much larger than the box on
        /// every side (smaller, for a negative value); 0 (the default) is the box itself.
        /// </summary>
        public double SpreadPt { get; set; }

        /// <summary>
        /// CSS's <c>inset</c>: the shadow is cast inside the box, over its background and under
        /// its border and content — the edge of the padding box, shrunk by the spread and moved by
        /// the offset, casting inward. <c>false</c> (the default) is a drop shadow behind the box.
        /// </summary>
        public bool Inset { get; set; }
    }

    /// <summary>
    /// Out-of-flow placement for a block: where it sits, and in what order it paints.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured from the page's top-left</b>, in points, the way CSS states
    /// <c>top</c>/<c>left</c> — the exporter converts to PDF's bottom-left origin. Stating it in
    /// CSS's terms is deliberate: the one caller that produces these is the HTML mapper, and a
    /// model that flipped the axis would make every mapping site do the conversion instead of one
    /// exporter site doing it once.</para>
    /// <para><b>The containing block is the page</b> unless <see cref="ContainingBox"/> names a box
    ///: then the offsets are measured from that box's padding box — CSS's containing
    /// block for a positioned descendant of a positioned element — and the block is drawn with the
    /// box's fragment, on the page the anchor falls on.</para>
    /// </remarks>
    public sealed class AbsolutePlacement
    {
        /// <summary>Distance from the page's left edge — or the <see cref="ContainingBox"/>'s left padding edge — in points, when <see cref="AnchorsLeft"/>.</summary>
        public double LeftPt { get; set; }

        /// <summary>Distance from the page's top edge — or the <see cref="ContainingBox"/>'s top padding edge — in points, when <see cref="AnchorsTop"/>.</summary>
        public double TopPt { get; set; }

        /// <summary>
        /// Width of the block, in points. <c>0</c> means "to the right page margin" — or, against a
        /// <see cref="ContainingBox"/>, to its right padding edge less <see cref="RightPt"/>, and
        /// for a block anchored at the right alone its content's width (CSS shrink-to-fit).
        /// </summary>
        public double WidthPt { get; set; }

        /// <summary>
        /// The box of blocks whose padding box the offsets are measured from: the
        /// <see cref="BoxStart"/> open where this paragraph stands, as CSS measures a positioned
        /// descendant from its nearest positioned ancestor. The block is drawn with the fragment of
        /// that box its top falls in — on that fragment's page, over the box's own blocks — and its
        /// <see cref="ZIndex"/> orders it among the box's other positioned blocks. <c>null</c> (the
        /// default) is the page, drawn after the section's flow. A box that is not open where the
        /// paragraph stands is the page too.
        /// </summary>
        public BoxStart? ContainingBox { get; set; }

        /// <summary>
        /// Distance from the <see cref="ContainingBox"/>'s right padding edge — or the page's right
        /// edge — to the block's right edge, in points — CSS <c>right</c>; <c>null</c> (the default)
        /// is unanchored. With <see cref="AnchorsLeft"/> it sizes a block with no <see cref="WidthPt"/>;
        /// without, it places the block, as wide as its content when no width is stated.
        /// </summary>
        public double? RightPt { get; set; }

        /// <summary>
        /// Distance from the <see cref="ContainingBox"/>'s bottom padding edge — or the page's bottom
        /// edge — to the block's bottom edge (its <see cref="Following"/> paragraphs' included), in
        /// points — CSS <c>bottom</c>; <c>null</c> (the default) is unanchored, and it places the
        /// block only without <see cref="AnchorsTop"/>.
        /// </summary>
        public double? BottomPt { get; set; }

        /// <summary>Whether <see cref="LeftPt"/> anchors the block (the default); <c>false</c> leaves the block to <see cref="RightPt"/>, CSS's <c>left: auto</c>.</summary>
        public bool AnchorsLeft { get; set; } = true;

        /// <summary>Whether <see cref="TopPt"/> anchors the block (the default); <c>false</c> leaves the block to <see cref="BottomPt"/>, CSS's <c>top: auto</c>.</summary>
        public bool AnchorsTop { get; set; } = true;

        /// <summary>
        /// A share of the <see cref="ContainingBox"/>'s padding width added to <see cref="LeftPt"/> —
        /// CSS <c>left: 25%</c> inside a positioned ancestor; <c>null</c> (the default) for
        /// none. Read only against a containing box, whose width the exporter knows as it draws and the
        /// producer of the model does not; against the page the producer resolves a percentage itself.
        /// </summary>
        public double? LeftFraction { get; set; }

        /// <summary>A share of the containing box's padding width added to <see cref="RightPt"/> (CSS <c>right: 10%</c>); <c>null</c> for none. It anchors only with a <see cref="RightPt"/>, which may be 0.</summary>
        public double? RightFraction { get; set; }

        /// <summary>
        /// A share of the containing box's padding height added to <see cref="TopPt"/> (CSS
        /// <c>top: 50%</c>); <c>null</c> for none. The box's height is known at its last fragment, so
        /// a block with one is drawn there, as a bottom-anchored block is.
        /// </summary>
        public double? TopFraction { get; set; }

        /// <summary>A share of the containing box's padding height added to <see cref="BottomPt"/> (CSS <c>bottom: 25%</c>); <c>null</c> for none. It anchors only with a <see cref="BottomPt"/>, which may be 0.</summary>
        public double? BottomFraction { get; set; }

        /// <summary>A share of the containing box's padding width added to <see cref="WidthPt"/> (CSS <c>width: 40%</c>); <c>null</c> for none.</summary>
        public double? WidthFraction { get; set; }

        /// <summary>
        /// The height of the band the block is set in, in points, for <see cref="BandAlign"/>: the
        /// block and the blocks after it are moved down by the band's slack (half of it when centred),
        /// and a <see cref="Paragraph.Transform"/> among them is taken about the band rather than about
        /// where the move put the paragraph — so a turn's fixed point stays where the producer put it.
        /// A group taller than its band is not moved. <c>null</c> (the default) is no band. Read by the
        /// PDF exporter; the HTML, DOCX and ODT exporters write placed blocks in the flow.
        /// </summary>
        public double? BandHeightPt { get; set; }

        /// <summary>Where the block sits in its <see cref="BandHeightPt"/>: at the top (the default), centred or at the bottom — XFA's <c>para vAlign</c>.</summary>
        public CellVerticalAlign BandAlign { get; set; } = CellVerticalAlign.Top;

        /// <summary>
        /// Paint order among positioned blocks; higher paints later. In-flow content is always
        /// beneath all of them, which is what <c>position</c> means and is why this does not need
        /// to interleave with the flow.
        /// </summary>
        public int ZIndex { get; set; }

        /// <summary>
        /// Draw the block at its point on every page its section produces — CSS <c>position: fixed</c>
        /// as print media lays it out — rather than on the page the section ends on alone. Wherever the
        /// paragraph stands in the section, it is on the first page too.
        /// </summary>
        public bool EveryPage { get; set; }

        /// <summary>
        /// Paragraphs drawn below the placed one, one after another at its left edge and width — the
        /// rest of a positioned element of several paragraphs (an HTML <c>position: absolute</c>
        /// <c>div</c> holding two <c>p</c>s). Each is drawn where the one above it ended, so their
        /// heights need no measuring by whoever builds the model; none of them is in the flow.
        /// </summary>
        public System.Collections.Generic.List<Paragraph> Following { get; } = new System.Collections.Generic.List<Paragraph>();

        /// <summary>
        /// Blocks drawn after <see cref="Following"/>, one after another at the same left edge and
        /// width, laid out as a table cell's blocks are — paragraphs (floats among them), tables, lists
        /// and list items, boxes (<see cref="BoxStart"/> … <see cref="BoxEnd"/>) — so a positioned
        /// element can hold any of them (an XFA table subform inside a positioned one, an HTML
        /// <c>position: absolute</c> element's list). A paragraph placed itself, or a block a cell does
        /// not hold, is not drawn, and reported. Like the rest of the placement, none of them is in the
        /// flow, and nothing among them breaks across pages.
        /// </summary>
        public System.Collections.Generic.List<IDocElement> FollowingBlocks { get; } = new System.Collections.Generic.List<IDocElement>();

        /// <summary>
        /// Whether <paramref name="block"/> may stand among <paramref name="among"/> in a placement's
        /// <see cref="FollowingBlocks"/>: what a table cell holds — a paragraph in the flow, a table, a list
        /// or a list item, a box's start or end — and a paragraph placed against a box that opens among
        /// them, not on every page: a positioned element inside a positioned one, drawn with that box's
        /// fragment and measured from its padding box (CSS 2.1 §10.1). Any other block is not drawn there.
        /// </summary>
        public static bool CanFollow(IDocElement? block, System.Collections.Generic.IReadOnlyList<IDocElement> among)
        {
            if (block is Paragraph { Placement: null } or Table or ListBlock or ListItem or BoxStart or BoxEnd) return true;
            if (block is not Paragraph { Placement: { EveryPage: false, ContainingBox: BoxStart box } }) return false;
            foreach (IDocElement other in among)
            {
                AmongScanned++;
                if (ReferenceEquals(other, box)) return true;
            }
            return false;
        }

        /// <summary>The list entries the scanning <c>CanFollow</c> has compared on this thread — the evidence that the exporters' walks ask the set instead.</summary>
        [System.ThreadStatic] internal static long AmongScanned;

        /// <summary>
        /// <see cref="CanFollow(IDocElement?, System.Collections.Generic.IReadOnlyList{IDocElement})"/> against
        /// the boxes of the list collected once (<see cref="BoxesAmong"/>): asked of every block of a list,
        /// the scan of the list each time was quadratic in its length.
        /// </summary>
        internal static bool CanFollowBoxes(IDocElement? block, System.Collections.Generic.HashSet<BoxStart> boxesAmong)
        {
            if (block is Paragraph { Placement: null } or Table or ListBlock or ListItem or BoxStart or BoxEnd) return true;
            return block is Paragraph { Placement: { EveryPage: false, ContainingBox: BoxStart box } }
                && boxesAmong.Contains(box);
        }

        /// <summary>The boxes a list opens — <see cref="BoxStart"/> keeps reference equality, so a set of them is by identity.</summary>
        internal static System.Collections.Generic.HashSet<BoxStart> BoxesAmong(System.Collections.Generic.IReadOnlyList<IDocElement> among)
        {
            var boxes = new System.Collections.Generic.HashSet<BoxStart>();
            foreach (IDocElement other in among)
                if (other is BoxStart b) boxes.Add(b);
            return boxes;
        }
    }

    /// <summary>Marker for inline elements (text, images, breaks, tabs).</summary>
    public interface IInlineElement { }

    public enum VerticalAlign { Baseline, Superscript, Subscript }

    /// <summary>A run of text with formatting.</summary>
    public sealed class TextRun : IInlineElement
    {
        public string Text { get; set; } = "";
        public string FontName { get; set; } = StandardFonts.Helvetica;
        public double FontSize { get; set; } = DocDefaults.FontSizePt;
        public string Color { get; set; } = DocDefaults.TextColorHex;
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public bool IsUnderline { get; set; }
        public bool IsStrikethrough { get; set; }
        public bool IsDoubleStrike { get; set; }

        /// <summary>Highlight / run-shading fill behind the text (hex); "" = none.</summary>
        public string Highlight { get; set; } = "";
        /// <summary>Super/subscript (scales size + shifts baseline).</summary>
        public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Baseline;
        /// <summary>Extra baseline shift in points (positive = up); adds to super/sub shift.</summary>
        public double BaselineShift { get; set; }
        /// <summary>Inter-character spacing in points (PDF <c>Tc</c>).</summary>
        public double CharSpacing { get; set; }

        /// <summary>
        /// Kern this run with its font's kerning (<c>GPOS</c> <c>kern</c>, else the legacy <c>kern</c>
        /// table): <c>true</c> or <c>false</c> for this run, <c>null</c> (the default) for
        /// <see cref="Export.PdfExportOptions.Kerning"/>. A run drawn in a standard-14 face is kerned by
        /// that face's Adobe AFM pairs, written as <c>TJ</c> adjustments, since a viewer
        /// positions its glyphs by their widths alone.
        /// </summary>
        public bool? Kerning { get; set; }
        /// <summary>
        /// Draw this run with its font's contextual alternates (<c>GSUB</c> <c>calt</c>): <c>true</c> or
        /// <c>false</c> for this run, <c>null</c> (the default) for
        /// <see cref="Export.PdfExportOptions.ContextualAlternates"/>. Applies where the run is drawn in
        /// an embedded font. The required alternates (<c>rclt</c>) are drawn either way.
        /// </summary>
        public bool? ContextualAlternates { get; set; }
        /// <summary>
        /// Form this run's standard ligatures (<c>GSUB</c> <c>liga</c>): <c>true</c> or <c>false</c> for
        /// this run, <c>null</c> (the default) for <see cref="Export.PdfExportOptions.Ligatures"/>.
        /// Applies where the run is drawn in an embedded font; a ligature is written back in
        /// <c>/ToUnicode</c> as its letters. The required ligatures (<c>rlig</c>) are formed either way.
        /// </summary>
        public bool? Ligatures { get; set; }
        /// <summary>
        /// Form this run's discretionary ligatures (<c>GSUB</c> <c>dlig</c>): <c>true</c> or <c>false</c>
        /// for this run, <c>null</c> (the default) for
        /// <see cref="Export.PdfExportOptions.DiscretionaryLigatures"/>. Applies where the run is drawn in
        /// an embedded font; a ligature is written back in <c>/ToUnicode</c> as its letters.
        /// </summary>
        public bool? DiscretionaryLigatures { get; set; }
        /// <summary>
        /// Form this run's historical ligatures (<c>GSUB</c> <c>hlig</c>): <c>true</c> or <c>false</c>
        /// for this run, <c>null</c> (the default) for <see cref="Export.PdfExportOptions.HistoricalLigatures"/>.
        /// </summary>
        public bool? HistoricalLigatures { get; set; }
        /// <summary>Horizontal glyph scale as a percentage (PDF <c>Tz</c>); 100 = normal.</summary>
        public int HorizontalScale { get; set; } = 100;
        /// <summary>External hyperlink target; null = none. Drawn as a clickable /Link annot.</summary>
        public string? LinkUri { get; set; }
        /// <summary>Internal hyperlink anchor (bookmark name); null = none.</summary>
        public string? LinkAnchor { get; set; }

        /// <summary>
        /// The interactive form field this run stands for; null = none. The PDF exporter reports
        /// where it lands (<see cref="Export.PdfExporter.PlacedFormFields"/>) and every exporter
        /// draws the run's text as usual — see <see cref="FormFieldSpec"/>.
        /// </summary>
        public FormFieldSpec? FormField { get; set; }

        public bool IsSmallCaps { get; set; }
        public bool IsAllCaps { get; set; }

        /// <summary>
        /// Constant alpha for this run, multiplied by its <see cref="Paragraph.Opacity"/>.
        /// <c>1</c> = opaque, the default. See <see cref="Paragraph.Opacity"/> for why this is
        /// constant alpha rather than CSS group opacity.
        /// </summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// The run takes its room and paints no glyph — CSS <c>color: transparent</c>, PDF text
        /// render mode 3. Its underline and strike, which take the text's colour, are not drawn
        /// either; its <see cref="Highlight"/> and <see cref="Box"/> are. <see cref="Color"/> is
        /// kept as the colour the run would have had, for an exporter with no such mode.
        /// </summary>
        public bool IsInvisible { get; set; }

        /// <summary>
        /// The inline box this run is drawn inside — a border, padding and background around an
        /// inline element's content (a label, a badge, a button); <c>null</c> = none. Every run of
        /// one element shares one <see cref="InlineBox"/> instance, which is how the renderer knows
        /// where a box begins and ends.
        /// </summary>
        public InlineBox? Box { get; set; }

        /// <summary>Returns a shallow copy (used when splitting a run across wrapped lines).</summary>
        public TextRun Clone() => (TextRun)MemberwiseClone();
    }

    /// <summary>
    /// A box drawn around a stretch of inline content: CSS <c>border</c>, <c>padding</c>,
    /// <c>background-color</c> and <c>border-radius</c> on an inline element. Shared by reference
    /// among the <see cref="TextRun"/>s it encloses.
    /// </summary>
    /// <remarks>
    /// <para>The horizontal padding and border take room in the line; the vertical ones do not,
    /// and draw over the lines around it, as an inline box does in CSS. A box that wraps is drawn
    /// once per line, each piece with its full padding.</para>
    /// <para><b>A box around no text still takes its room</b>: a run with empty
    /// <see cref="TextRun.Text"/> and a box is drawn as the box alone — an icon drawn as the
    /// background of an empty, padded element.</para>
    /// </remarks>
    public sealed class InlineBox
    {
        /// <summary>The border drawn around the box; <c>null</c> = none.</summary>
        public Border? Border { get; set; }
        /// <summary>Background fill (hex); "" = none.</summary>
        public string Background { get; set; } = "";
        /// <summary>
        /// Background image, painted over <see cref="Background"/> and clipped to the box, as CSS
        /// paints an element's background image; <c>null</c> = none.
        /// </summary>
        public BackgroundImage? BackgroundImage { get; set; }
        /// <summary>Space between the content and the left edge, in points.</summary>
        public double PaddingLeft { get; set; }
        /// <summary>Space between the content and the right edge, in points.</summary>
        public double PaddingRight { get; set; }
        /// <summary>Space above the content, in points.</summary>
        public double PaddingTop { get; set; }
        /// <summary>Space below the content, in points.</summary>
        public double PaddingBottom { get; set; }
        /// <summary>Room outside the box's left edge on each line it starts or ends on, in points.</summary>
        public double MarginLeft { get; set; }
        /// <summary>Room outside the box's right edge on each line it starts or ends on, in points.</summary>
        public double MarginRight { get; set; }
        /// <summary>Room above the box, in points — counted only when <see cref="OccupiesLine"/>.</summary>
        public double MarginTop { get; set; }
        /// <summary>Room below the box, in points — counted only when <see cref="OccupiesLine"/>.</summary>
        public double MarginBottom { get; set; }
        /// <summary>
        /// CSS's <c>inline-block</c>: the box's vertical padding, border and margins make room in the
        /// line it is on — the line is at least as tall as the box's margin box — where an inline
        /// box's (the default, <c>false</c>) overlap the lines round it. A wrapped row of tags is
        /// spaced by their margins, as a browser spaces it.
        /// </summary>
        public bool OccupiesLine { get; set; }
        /// <summary>Corner radius in points; 0 = square corners.</summary>
        public double CornerRadiusPt { get; set; }
        /// <summary>
        /// Four corner radii in points — top-left, top-right, bottom-right, bottom-left, CSS's
        /// order — overriding <see cref="CornerRadiusPt"/> when set: a tab rounds its top corners
        /// only. <c>null</c> (the default) is the one radius for all four. The same four readings as
        /// <see cref="Paragraph.CornerRadiiPt"/> and its companions.
        /// </summary>
        public double[]? CornerRadiiPt { get; set; }
        /// <summary>
        /// The four corners' vertical radii in points, in <see cref="CornerRadiiPt"/>'s order —
        /// CSS's <c>border-radius: x / y</c> — making each corner a quarter ellipse, its horizontal
        /// radius from <see cref="CornerRadiiPt"/>. <c>null</c> (the default) is circular corners.
        /// </summary>
        public double[]? CornerRadiiYPt { get; set; }
        /// <summary>
        /// A corner radius as a percentage of the box — CSS's <c>border-radius: 50%</c> — its
        /// horizontal radius that share of the width and its vertical one of the height, so 50% of
        /// a square is a circle and of an oblong an ellipse; overrides the lengths when positive.
        /// </summary>
        public double CornerRadiusPercent { get; set; }
        /// <summary>
        /// Per-corner horizontal radii as percentages of the box's width, in <see cref="CornerRadiiPt"/>'s
        /// order — CSS's <c>border-radius: 50% 10px</c>, a percentage among lengths: a positive entry
        /// replaces that corner's length, 0 leaves the length. Its vertical radius is the same share
        /// of the height unless <see cref="CornerRadiiYPercent"/> or <see cref="CornerRadiiYPt"/>
        /// states it. <c>null</c> (the default) is no percentage corner.
        /// </summary>
        public double[]? CornerRadiiPercent { get; set; }
        /// <summary>
        /// Per-corner vertical radii as percentages of the box's height, in <see cref="CornerRadiiPt"/>'s
        /// order — the <c>y</c> half of <c>border-radius: x / y</c> when it is a percentage; a positive
        /// entry replaces that corner's vertical length. <c>null</c> (the default) is none.
        /// </summary>
        public double[]? CornerRadiiYPercent { get; set; }
    }

    /// <summary>An inline image.</summary>
    public sealed class ImageRun : IInlineElement
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public double WidthPt { get; set; }
        public double HeightPt { get; set; }

        /// <summary>
        /// Alternative text — HTML <c>alt</c>, DOCX <c>wp:docPr/@descr</c>. Drawn inside the
        /// reserved box when the image cannot be decoded, so a reader sees <i>what</i> is missing
        /// rather than a blank rectangle.
        ///
        /// <para>This is the <b>only</b> alt-text property on an image. A second spelling would
        /// be the divergence shape this codebase keeps finding: two names for one concept, kept
        /// in sync by nothing.</para>
        /// </summary>
        public string? Alt { get; set; }

        /// <summary>
        /// The image carries no information — HTML's <c>alt=""</c>. Distinct from
        /// <see cref="Alt"/> being null, which means the author said nothing at all: one is a
        /// deliberate statement that the image is decoration, the other is missing metadata.
        /// </summary>
        public bool IsDecorative { get; set; }

        /// <summary>
        /// How the stored pixels are turned to fill <see cref="WidthPt"/> × <see cref="HeightPt"/>,
        /// which is the box as shown. <see cref="ImageOrientation.Normal"/> (the default) draws
        /// them as stored. The PDF exporter honours it.
        /// </summary>
        /// <remarks>
        /// Set by the producer, not read from the bytes: the HTML converter sets it from a JPEG's
        /// EXIF orientation, as a browser applies it; a Word document's image is drawn as stored.
        /// </remarks>
        public ImageOrientation Orientation { get; set; } = ImageOrientation.Normal;

        /// <summary>
        /// The frame drawn round the image — CSS padding, border and background on an
        /// <c>&lt;img&gt;</c> or <c>&lt;canvas&gt;</c> — outside <see cref="WidthPt"/> ×
        /// <see cref="HeightPt"/>, which stay the picture's own size. <c>null</c> (the default) draws
        /// the picture alone. The PDF exporter honours it.
        /// </summary>
        /// <remarks>
        /// <b>With no <see cref="Data"/>, a box is a blank area of that size</b> — a canvas whose
        /// picture nothing drew still takes its place on the page and shows its frame, as it does in
        /// a browser. An image run with neither pixels nor a box occupies nothing, as before.
        /// </remarks>
        public InlineBox? Box { get; set; }

        /// <summary>
        /// A picture described by paths rather than pixels, drawn in <see cref="WidthPt"/> ×
        /// <see cref="HeightPt"/> in place of <see cref="Data"/> — XFA's arc of less than a full turn.
        /// <c>null</c> (the default) is the pixels. Laid out, tagged and marked decorative as any image is.
        /// </summary>
        /// <remarks>
        /// The PDF exporter draws it as vector paths, scaled with the picture when the band is narrower;
        /// the HTML exporter writes an inline <c>&lt;svg&gt;</c>. The DOCX and ODT exporters, which write
        /// no image of any kind, leave it out as they leave out pixels, and say so
        /// (<c>BuilderDiagnosticKind.UnsupportedByExportFormat</c>).
        /// </remarks>
        public VectorDrawing? Drawing { get; set; }
    }

    /// <summary>
    /// A vector picture for <see cref="ImageRun.Drawing"/>: one path, in points from the picture's
    /// top-left corner with y running down, stroked and filled. A fill closes an open path for itself
    /// and a stroke does not — PDF's and SVG's rule alike — so an arc left open fills to its chord and
    /// strokes its curve alone.
    /// </summary>
    public sealed class VectorDrawing
    {
        /// <summary>The path's commands, in order; it begins with a <see cref="PathVerb.MoveTo"/>.</summary>
        public List<PathCommand> Path { get; } = new List<PathCommand>();

        /// <summary>The stroke's colour (hex); <c>null</c> is no stroke.</summary>
        public string? StrokeColor { get; set; } = "#000000";

        /// <summary>The stroke's width in points, centred on the path.</summary>
        public double StrokeWidthPt { get; set; } = 1;

        /// <summary>The fill's colour (hex); <c>null</c> (the default) is no fill.</summary>
        public string? FillColor { get; set; }
    }

    /// <summary>What a <see cref="PathCommand"/> does.</summary>
    public enum PathVerb
    {
        /// <summary>Starts a subpath at (<see cref="PathCommand.X"/>, <see cref="PathCommand.Y"/>).</summary>
        MoveTo,
        /// <summary>A straight segment to (<see cref="PathCommand.X"/>, <see cref="PathCommand.Y"/>).</summary>
        LineTo,
        /// <summary>A cubic Bézier segment to (<see cref="PathCommand.X"/>, <see cref="PathCommand.Y"/>) through the two control points.</summary>
        CurveTo,
        /// <summary>Closes the subpath with a straight segment to its start.</summary>
        Close,
    }

    /// <summary>One command of a <see cref="VectorDrawing"/>'s path, in points from the picture's top-left, y down.</summary>
    public readonly struct PathCommand
    {
        private PathCommand(PathVerb verb, double c1x, double c1y, double c2x, double c2y, double x, double y)
        {
            Verb = verb; C1X = c1x; C1Y = c1y; C2X = c2x; C2Y = c2y; X = x; Y = y;
        }

        public PathVerb Verb { get; }
        /// <summary>The first control point of a <see cref="PathVerb.CurveTo"/>.</summary>
        public double C1X { get; }
        public double C1Y { get; }
        /// <summary>The second control point of a <see cref="PathVerb.CurveTo"/>.</summary>
        public double C2X { get; }
        public double C2Y { get; }
        /// <summary>The point the command moves, draws or curves to.</summary>
        public double X { get; }
        public double Y { get; }

        public static PathCommand MoveTo(double x, double y) => new PathCommand(PathVerb.MoveTo, 0, 0, 0, 0, x, y);
        public static PathCommand LineTo(double x, double y) => new PathCommand(PathVerb.LineTo, 0, 0, 0, 0, x, y);
        public static PathCommand CurveTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
            => new PathCommand(PathVerb.CurveTo, c1x, c1y, c2x, c2y, x, y);
        public static PathCommand Close() => new PathCommand(PathVerb.Close, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Opens a box around the blocks that follow it, up to the matching <see cref="BoxEnd"/>: CSS's
    /// margin, border, padding and background on an element whose content is several blocks — a
    /// panel, a card, a well, a padded or negatively-margined container. Boxes nest.
    /// </summary>
    /// <remarks>
    /// <para><b>A marker, not a container.</b> The blocks stay where they were, in the section's
    /// (or a cell's) list, between this and its <see cref="BoxEnd"/>. Every walker over a document's
    /// blocks — fonts to embed, footnote numbers, the table of contents, the DOCX, HTML and ODT
    /// exporters — therefore still sees every block, and one that does not know the box keeps its
    /// content and loses only the frame. A container type would have had to be taught to each of
    /// them, and any one it was not would have dropped the box's contents without a word.</para>
    /// <para><b>The PDF exporter</b> lays the blocks out in the box's content band — narrowed by the
    /// margins, borders and padding, widened by a negative margin — and draws the border and
    /// background around them. A box that breaks across a page is drawn once per page: the first
    /// fragment has no bottom border and each continuation no top border, as CSS's default
    /// <c>box-decoration-break: slice</c> draws it.</para>
    /// <para>An unmatched <see cref="BoxEnd"/> is ignored, and a box still open where its list ends
    /// is closed there.</para>
    /// </remarks>
    public sealed class BoxStart : IDocElement
    {
        /// <summary>
        /// The width of the box — border, padding and blocks — as a fraction of the band it opens
        /// in, plus <see cref="WidthPt"/>; <c>null</c> with <see cref="WidthPt"/> 0 (the default)
        /// is the full band. Narrower, it sits at the band's start (the right in a right-to-left
        /// section) — or its end, with <see cref="WidthAtEnd"/> — and the room it leaves is empty;
        /// wider, it keeps the band. As <see cref="Paragraph.WidthFraction"/> for one paragraph.
        /// </summary>
        public double? WidthFraction { get; set; }

        /// <summary>Points added to <see cref="WidthFraction"/>'s share, or the whole width when that is <c>null</c>.</summary>
        public double WidthPt { get; set; }

        /// <summary>Put a narrower box at the band's end rather than its start: a float to that side.</summary>
        public bool WidthAtEnd { get; set; }

        /// <summary>
        /// Pins the box — its margins, border, padding and every block to its <see cref="BoxEnd"/> —
        /// to one edge of the band and lets the blocks after it flow alongside, returning to the full
        /// band once they clear its bottom: <see cref="Paragraph.Float"/> for a box of blocks, so a floated
        /// sidebar of several paragraphs, a list or a table re-flows as one paragraph does.
        /// <see cref="FloatSide.None"/> (the default) does not float.
        /// Requires <see cref="FloatWidth"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>The PDF exporter</b> lays the box out at the band's edge in its float width, as a
        /// table cell lays its blocks out — the whole box on one page — and the room it takes, its
        /// margins included, is an exclusion the blocks after it wrap round, as a floated paragraph's
        /// is. A box that does not fit the rest of its page is laid out on the next; one taller than
        /// a page, or wider than leaves any measure beside it, is laid out in the flow instead — a
        /// block of its float width on its side (<see cref="WidthAtEnd"/> for a right float) — and a
        /// box taller than a page says so through <c>BuilderDiagnosticKind.ContentApproximated</c>.
        /// Inside a table cell the float sits at the cell's edge and the cell's blocks flow beside it.</para>
        /// <para><b>The float width stands in for the stated width.</b> <see cref="WidthFraction"/>,
        /// <see cref="WidthPt"/> and <see cref="WidthAtEnd"/> are not read on a floated box: the float
        /// is as wide as <see cref="FloatWidth"/>, on the side <see cref="Float"/> names.</para>
        /// <para><b>A marker still.</b> The other exporters see the blocks as before and lose the frame
        /// and the float, as they lose every box's frame.</para>
        /// </remarks>
        public FloatSide Float { get; set; } = FloatSide.None;

        /// <summary>
        /// Width in points of a floated box's border box — its border, padding and blocks, its margins
        /// outside it. Ignored unless <see cref="Float"/> is set, and required when it is; as
        /// <see cref="Paragraph.FloatWidth"/>, in points because the band a float sits in is not
        /// always the page.
        /// </summary>
        public double FloatWidth { get; set; }

        /// <summary>
        /// Starts this box below every float still in effect, instead of alongside them — CSS
        /// <c>clear</c> on an element whose content is several blocks, as <see cref="Paragraph.ClearFloats"/>
        /// is for one paragraph (a box could not carry it before, and the clear was dropped).
        /// A floated box that clears starts below the floats before it.
        /// </summary>
        public bool ClearFloats { get; set; }

        /// <summary>
        /// Keep the box — all of it, to its <see cref="BoxEnd"/> — on the page the block after it starts on, as
        /// <see cref="Paragraph.KeepWithNext"/> keeps a paragraph: CSS's <c>page-break-after: avoid</c>
        /// on a box, or XFA's <c>page-break-before: avoid</c> on the text after it. The PDF exporter starts a new
        /// page before the box when the two do not fit what is left of this one but do fit a fresh one; a keep
        /// chain also passes into a box, by its top edges, to its first block.
        /// </summary>
        public bool KeepWithNext { get; set; }

        /// <summary>Space above the border, in points.</summary>
        public double MarginTop { get; set; }
        /// <summary>Space below the border, in points.</summary>
        public double MarginBottom { get; set; }
        /// <summary>Space left of the border, in points; negative widens the box past its band.</summary>
        public double MarginLeft { get; set; }
        /// <summary>Space right of the border, in points; negative widens the box past its band.</summary>
        public double MarginRight { get; set; }

        /// <summary>The border's four sides; <c>null</c> = none on that side.</summary>
        public Border? BorderTop { get; set; }
        public Border? BorderRight { get; set; }
        public Border? BorderBottom { get; set; }
        public Border? BorderLeft { get; set; }

        /// <summary>Space between the border and the blocks, in points.</summary>
        public double PaddingTop { get; set; }
        public double PaddingRight { get; set; }
        public double PaddingBottom { get; set; }
        public double PaddingLeft { get; set; }

        /// <summary>Background fill (hex); "" = none.</summary>
        public string Shading { get; set; } = "";
        /// <summary>Background image, over <see cref="Shading"/>; <c>null</c> = none.</summary>
        public BackgroundImage? BackgroundImage { get; set; }
        /// <summary>Corner radius in points; 0 = square. A fragment cut by a page break is square.</summary>
        public double CornerRadiusPt { get; set; }
        /// <summary>
        /// Four corner radii in points — top-left, top-right, bottom-right, bottom-left, CSS's
        /// order — overriding <see cref="CornerRadiusPt"/> when set: a panel heading rounds its top
        /// corners only. <c>null</c> (the default) is the one radius for all four.
        /// </summary>
        public double[]? CornerRadiiPt { get; set; }
        /// <summary>
        /// The four corners' vertical radii in points, in <see cref="CornerRadiiPt"/>'s order —
        /// CSS's <c>border-radius: x / y</c> — making each corner a quarter ellipse, its horizontal
        /// radius from <see cref="CornerRadiiPt"/>. <c>null</c> (the default) is circular corners.
        /// </summary>
        public double[]? CornerRadiiYPt { get; set; }
        /// <summary>
        /// A corner radius as a percentage of the box — CSS's <c>border-radius: 50%</c> — its
        /// horizontal radius that share of the width and its vertical one of the height, so 50% of
        /// a square is a circle and of an oblong an ellipse; overrides the lengths when positive.
        /// </summary>
        public double CornerRadiusPercent { get; set; }
        /// <summary>Per-corner horizontal radii as percentages of the width, as <see cref="Paragraph.CornerRadiiPercent"/>; <c>null</c> = none.</summary>
        public double[]? CornerRadiiPercent { get; set; }
        /// <summary>Per-corner vertical radii as percentages of the height, as <see cref="Paragraph.CornerRadiiYPercent"/>; <c>null</c> = none.</summary>
        public double[]? CornerRadiiYPercent { get; set; }
        /// <summary>
        /// A drop shadow behind the box, as <see cref="Paragraph.Shadow"/> is behind a paragraph's;
        /// <c>null</c> = none. Drawn per fragment, so a page break slices it with the box.
        /// </summary>
        public BoxShadow? Shadow { get; set; }
        /// <summary>
        /// The shadows after <see cref="Shadow"/>, in CSS's order — each painted beneath the one
        /// before it, as CSS lists them top first. Empty (the default) for one shadow or none.
        /// </summary>
        public List<BoxShadow> MoreShadows { get; } = new List<BoxShadow>();

        /// <summary>
        /// CSS's <c>overflow</c> other than <c>visible</c>: what the box holds is clipped to its
        /// padding box — its inside edge, rounded by its corners' inner curve — as a
        /// <c>.progress</c> track clips the square bar inside it. <c>false</c> (the default) lets it
        /// paint past the box.
        /// </summary>
        public bool ClipsContent { get; set; }

        /// <summary>
        /// The least height of the box's border box — border, padding and blocks — in points; a
        /// box whose blocks are shorter grows to it, one whose blocks are taller is as tall as they
        /// are. 0 (the default) is no minimum. Applied on the page the box opens on: a box that
        /// breaks across pages, or would, is as tall as its blocks.
        /// </summary>
        public double MinHeightPt { get; set; }

        /// <summary>
        /// Where the box's blocks sit in its <see cref="MinHeightPt"/> when they are shorter than it: at
        /// the top (the default), centred or at the bottom — XFA's <c>para vAlign</c> in a flowed draw or
        /// field, CSS's <c>align-content</c> on a block. Applied where the minimum height is, on the page
        /// the box opens on; a box as tall as its blocks, or one that breaks, has nothing to align.
        /// </summary>
        /// <remarks>
        /// <b>The PDF exporter</b> moves the blocks down by the room left (half of it, centred) when the
        /// box closes, link and form-field rectangles with them; the box's own fill, border and the
        /// paragraphs positioned against it stay where they are. The other exporters lose it, as they
        /// lose the box's frame.
        /// </remarks>
        public CellVerticalAlign ContentAlign { get; set; } = CellVerticalAlign.Top;

        /// <summary>
        /// Open the box at the top of the next page or content area when its <see cref="MinHeightPt"/>
        /// does not fit what is left of this one but fits the next — XFA's unsplittable positioned
        /// subform (XFA 3.3 chapter 8, "Content Splitting": a subform inside a positioned container
        /// is kept intact). The decision reads the minimum height alone, not the box's blocks, so a
        /// box of flowing content without one still breaks between them; one taller than the next
        /// region stays where it is. <c>false</c> (the default) opens the box where it stands.
        /// </summary>
        public bool KeepTogether { get; set; }

        /// <summary>
        /// When the box is the outermost of a table cell's, its bottom edge goes down to the cell's
        /// — CSS's <c>align-items: stretch</c> for a flex item laid out as a cell: a sidebar's
        /// background reaches the bottom of the row rather than of its own text. Ignored in the flow
        /// and for a box inside another. <c>false</c> (the default) keeps the box to its blocks.
        /// </summary>
        public bool StretchesToCell { get; set; }
        /// <summary>
        /// An affine transform applied to everything drawn inside the box — its fill, border and its
        /// blocks — as <see cref="Paragraph.Transform"/> is to a paragraph's painting, without changing
        /// where the flow put them; <c>null</c> = none. Set round each page fragment anew, so a page
        /// break slices a transformed box as it slices any other — each fragment taken about its own
        /// <see cref="TransformOrigin"/>, which is the box's when the box is whole on its page. The
        /// link and form-field rectangles inside the box follow it.
        /// </summary>
        public double[]? Transform { get; set; }

        /// <summary>
        /// The point <see cref="Transform"/> turns, scales and skews about, as
        /// <see cref="Paragraph.TransformOrigin"/> has it — a fraction and an offset per axis, of and
        /// from the border box of the fragment being drawn; <c>null</c> (the default) is the centre.
        /// </summary>
        public double[]? TransformOrigin { get; set; }

        /// <summary>
        /// The opacity of the box and everything inside it, composited as one layer — CSS
        /// <c>opacity</c> on a block of blocks: each page's fragment of the box, its fill, border and
        /// its blocks, is drawn opaque into a transparency group the page paints once at this value,
        /// so a block's text does not show the box's background through it. <c>1</c> (the default)
        /// is opaque. A block inside carries only the opacity it adds on top (its own relative to
        /// this), as a run's <see cref="TextRun.Opacity"/> is relative to its paragraph's.
        /// </summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// The index of the <see cref="BoxEnd"/> closing the box opened at <paramref name="start"/> in
        /// <paramref name="blocks"/> — the boxes nested inside counted — or <paramref name="to"/> when none does before it
        /// (the list's count when <paramref name="to"/> is negative, the default). The one walk every reader of a
        /// list of blocks finds a box's end by.
        /// </summary>
        public static int IndexOfEnd(System.Collections.Generic.IReadOnlyList<IDocElement> blocks, int start, int to = -1)
        {
            if (to < 0) to = blocks.Count;
            int depth = 0;
            for (int i = start + 1; i < to; i++)
            {
                if (blocks[i] is BoxStart) depth++;
                else if (blocks[i] is BoxEnd && depth-- == 0) return i;
            }
            return to;
        }
    }

    /// <summary>Closes the most recently opened <see cref="BoxStart"/>.</summary>
    public sealed class BoxEnd : IDocElement
    {
        /// <summary>
        /// The index of the <see cref="BoxStart"/> the box closed at <paramref name="end"/> in <paramref name="blocks"/>
        /// opened at — the boxes nested inside counted — or -1 when none did: <see cref="BoxStart.IndexOfEnd"/>'s
        /// walk the other way.
        /// </summary>
        public static int IndexOfStart(System.Collections.Generic.IReadOnlyList<IDocElement> blocks, int end)
        {
            int depth = 0;
            for (int i = end - 1; i >= 0; i--)
            {
                if (blocks[i] is BoxEnd) depth++;
                else if (blocks[i] is BoxStart && depth-- == 0) return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// How an image's stored pixels are turned to be shown: the eight EXIF orientations (TIFF tag
    /// 274), with the same numbers, named for what each does.
    /// </summary>
    public enum ImageOrientation
    {
        /// <summary>As stored.</summary>
        Normal = 1,
        /// <summary>Mirrored left to right.</summary>
        FlipHorizontal = 2,
        /// <summary>Turned half round.</summary>
        Rotate180 = 3,
        /// <summary>Mirrored top to bottom.</summary>
        FlipVertical = 4,
        /// <summary>Mirrored across the top-left to bottom-right diagonal: a quarter turn clockwise, then mirrored left to right.</summary>
        Transpose = 5,
        /// <summary>A quarter turn clockwise — how a phone stores a photo taken upright.</summary>
        Rotate90Clockwise = 6,
        /// <summary>Mirrored across the other diagonal: a quarter turn clockwise, then mirrored top to bottom.</summary>
        Transverse = 7,
        /// <summary>A quarter turn anticlockwise.</summary>
        Rotate90Anticlockwise = 8,
    }

    /// <summary>
    /// A paragraph's background image — the bytes, their intrinsic size, and the two CSS
    /// properties that decide how they fill the box.
    ///
    /// <para>One holder rather than four loose fields on <see cref="Paragraph"/>, because the
    /// three non-PDF exporters need <b>one</b> thing to test for. Four nullable fields would give
    /// each of them its own definition of "is there a background image here?", and this repository
    /// has already paid for that shape more than once.</para>
    ///
    /// <para>A background image does <b>not</b> affect layout: it is painted into the box the
    /// paragraph occupies, which is measured from its text. Do not route it through the block
    /// image path, which advances <c>y</c> and emits a <c>/Figure</c>.</para>
    /// </summary>
    public sealed class BackgroundImage
    {
        /// <summary>Encoded image bytes (PNG / JPEG / GIF / BMP), as an <see cref="ImageRun"/> carries them.</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>Intrinsic tile width in points; ≤ 0 means "ask the decoder".</summary>
        public double WidthPt { get; set; }

        /// <summary>Intrinsic tile height in points; ≤ 0 means "ask the decoder".</summary>
        public double HeightPt { get; set; }

        public BackgroundRepeat Repeat { get; set; } = BackgroundRepeat.Repeat;

        public BackgroundSize Size { get; set; } = BackgroundSize.Auto;

        /// <summary>
        /// Horizontal position as a fraction of the space the box has beyond the tile: 0 puts the
        /// tile at the box's left edge (the default), 0.5 centres it, 1 puts it at the right — CSS
        /// <c>background-position</c> percentages. A repeating image tiles outward from there.
        /// </summary>
        public double PositionX { get; set; }

        /// <summary>Vertical position, likewise: 0 the top edge (the default), 0.5 centred, 1 the bottom.</summary>
        public double PositionY { get; set; }
    }

    /// <summary>CSS <c>background-repeat</c>, restricted to the four keyword values.</summary>
    public enum BackgroundRepeat
    {
        Repeat,
        RepeatX,
        RepeatY,
        NoRepeat,
    }

    /// <summary>
    /// CSS <c>background-size</c>, restricted to the keywords. <see cref="Auto"/> is the intrinsic
    /// tile size; <see cref="Cover"/> and <see cref="Contain"/> scale the tile to the box,
    /// preserving aspect ratio, and therefore draw exactly one tile whatever
    /// <see cref="BackgroundRepeat"/> says — which is what CSS does too.
    /// </summary>
    public enum BackgroundSize
    {
        Auto,
        Cover,
        Contain,
    }

    /// <summary>
    /// A footnote: a reference mark where it sits in the text, and the note that mark points at.
    /// </summary>
    /// <remarks>
    /// <para><b>One element carrying two pieces of content in two places.</b> The mark is drawn
    /// inline, superscript, where the footnote appears in the sentence; the note itself is drawn
    /// somewhere else entirely — the bottom of the page in a PDF, a section at the end in
    /// HTML, a separate part in DOCX. Modelling it as one inline element is what keeps those two
    /// halves from drifting apart: a producer states the relationship once and each exporter
    /// decides where the second half goes.</para>
    ///
    /// <para><b>The mark is numbered by the exporter, not by the caller.</b> A footnote's number
    /// depends on how many precede it in the document, which a producer building a section at a
    /// time does not know — the same reason <see cref="ListBlock"/> numbers its own items.
    /// <see cref="Mark"/> exists for the cases numbering cannot express: a dagger, an asterisk, a
    /// symbol that repeats per page.</para>
    /// </remarks>
    public sealed class Footnote : IInlineElement
    {
        /// <summary>The note's own content, drawn wherever the exporter puts notes.</summary>
        public List<IInlineElement> Runs { get; } = new();

        /// <summary>
        /// An explicit reference mark, or null to let the exporter number it.
        /// </summary>
        /// <remarks>
        /// Set it for a symbol sequence (<c>*</c>, <c>†</c>, <c>‡</c>) or for a note that
        /// continues a numbering the document did not start. An explicit mark is used verbatim and
        /// does not advance the automatic sequence, so mixing the two does not renumber the rest.
        /// </remarks>
        public string? Mark { get; set; }

        /// <summary>Formatting for the reference mark; the note's own runs carry their own.</summary>
        /// <remarks>
        /// Null takes the size and colour of the run the footnote sits beside, which is what makes
        /// a mark in a heading come out heading-sized rather than body-sized.
        /// </remarks>
        public TextRun? MarkFormat { get; set; }

        /// <summary>
        /// Where the note's content is drawn: at the foot of the page its mark lands on (the
        /// default), or among the endnotes after the document's last content.
        /// </summary>
        /// <remarks>
        /// Footnotes and endnotes are numbered in two sequences, as Word numbers them. Each exporter
        /// has its own answer for an endnote: the PDF export flows the endnotes after the last
        /// section's content under a rule, paginating as body text does; DOCX writes a
        /// <c>w:endnoteReference</c> and the endnotes part; ODT a <c>text:note</c> of class
        /// <c>endnote</c>; HTML, whose notes are all at the end, a second section after the footnotes.
        /// </remarks>
        public NotePlacement Placement { get; set; } = NotePlacement.PageFoot;
    }

    /// <summary>Where a <see cref="Footnote"/>'s content is drawn.</summary>
    public enum NotePlacement
    {
        /// <summary>At the foot of the page the mark lands on — a footnote.</summary>
        PageFoot,

        /// <summary>After the document's last content — an endnote.</summary>
        DocumentEnd,
    }

    /// <summary>A tab character (advances to the next tab stop / default interval).</summary>
    public sealed class TabRun : IInlineElement { }

    /// <summary>A line break within a paragraph (does not start a new paragraph).</summary>
    public sealed class LineBreak : IInlineElement { }

    /// <summary>A page break.</summary>
    public sealed class PageBreak : IInlineElement { }

    /// <summary>A table with rows and cells.</summary>
    public sealed class Table : IDocElement
    {
        public List<TableRow> Rows { get; } = new();
        public double BorderWidth { get; set; } = 0.5;
        public string BorderColor { get; set; } = DocDefaults.TextColorHex;
        /// <summary>Explicit total table width in points; null = fill content width.</summary>
        public double? WidthPt { get; set; }
        /// <summary>Explicit per-column widths in points (index 0 = first column). An entry
        /// &lt;= 0 (or a missing trailing entry) means auto — that column shares the leftover
        /// width equally with the other auto columns. Empty (the default) = all columns equal
        /// width (the original uniform behaviour). Mapped from HTML <c>&lt;colgroup&gt;/&lt;col&gt;</c>
        /// or explicit cell widths.</summary>
        public List<double> ColumnWidths { get; } = new();
        /// <summary>Mirror column order for RTL tables (DOCX <c>w:bidiVisual</c>).</summary>
        public bool RtlColumns { get; set; }
        /// <summary>
        /// Repeat the table's header rows at the top of each page: the rows marked
        /// <see cref="TableRow.IsHeader"/> at the START of the table, up to the first row that is
        /// not. A header row further down is not repeated.
        /// </summary>
        public bool RepeatHeaderRow { get; set; }
        /// <summary>
        /// Keep the whole table on one page when it fits on one (CSS <c>break-inside: avoid</c>). A
        /// table taller than a page breaks between rows as usual.
        /// </summary>
        public bool KeepTogether { get; set; }
        /// <summary>
        /// Keep the table on the same page as the block after it (CSS <c>break-after: avoid</c>).
        /// </summary>
        public bool KeepWithNext { get; set; }

        /// <summary>
        /// Draw <see cref="BorderWidth"/> around the table's outer edge only, instead of using it
        /// as the default border of every cell.
        ///
        /// <para>The two are genuinely different sources: the legacy HTML <c>&lt;table
        /// border="1"&gt;</c> attribute really does mean "rule every cell", while CSS
        /// <c>table { border: 1px solid }</c> means the table's own box — a frame. Mapping both to
        /// the per-cell default painted a full grid wherever an author wrote the CSS form.</para>
        ///
        /// <para>Defaults to <c>false</c>, the pre-existing behaviour, so DOCX-mapped tables and
        /// every hand-authored <see cref="DocumentBuilder"/> table are byte-identical.</para>
        /// </summary>
        public bool OuterBorderOnly { get; set; }

        /// <summary>
        /// Size the auto columns by their content, as CSS's automatic table layout does: each gets
        /// at least its longest word, and width to spare goes to the columns in proportion to their
        /// unwrapped content — a form's short label column narrow, its value column wide.
        /// </summary>
        /// <remarks>
        /// <para>Defaults to <c>false</c>, the equal split, so DOCX-mapped and hand-authored
        /// <see cref="DocumentBuilder"/> tables are unchanged. The HTML converter sets it unless the
        /// page asks for <c>table-layout: fixed</c>.</para>
        /// <para>A table with an auto column nothing measurable fills — only empty cells, a nested
        /// table, spanning cells — keeps the equal split: content sizing would give that column
        /// nothing.</para>
        /// </remarks>
        public bool AutoLayout { get; set; }

        /// <summary>Space above the table, in points. Default 0.</summary>
        public double SpacingBefore { get; set; }

        /// <summary>
        /// Space below the table, in points. Default <see cref="DefaultSpacingAfterPt"/>, the gap the
        /// renderer always left; the HTML converter sets the table's CSS <c>margin-bottom</c>
        /// (Bootstrap's <c>.table</c> has 20px).
        /// </summary>
        public double SpacingAfter { get; set; } = DefaultSpacingAfterPt;

        /// <summary>The space below a table that states none: 4pt.</summary>
        public const double DefaultSpacingAfterPt = 4;

        /// <summary>
        /// The least height a row takes when its <see cref="TableRow.HeightPt"/> states none, in
        /// points. Default <see cref="DefaultMinRowHeightPt"/>, so an empty row is still a visible
        /// row; the HTML converter sets 0, as a browser's row is only as tall as its content.
        /// </summary>
        public double MinRowHeightPt { get; set; } = DefaultMinRowHeightPt;

        /// <summary>
        /// Where a table narrower than its band (<see cref="WidthPt"/>) sits in it: centred
        /// (<see cref="TextAlignment.Center"/> — HTML's <c>align="center"</c>, CSS's auto margins)
        /// or at the right (<see cref="TextAlignment.Right"/>, or <see cref="TextAlignment.End"/> in
        /// a left-to-right section). <c>null</c> (the default) and the rest keep it where it always
        /// was, at the band's left.
        /// </summary>
        public TextAlignment? Alignment { get; set; }

        /// <summary>
        /// With no <see cref="WidthPt"/>, the table is as wide as its columns' content — CSS's
        /// automatic table width, up to the band — rather than the band: an HTML button table or a
        /// two-cell key/value table stays its own size. A column that cannot be measured keeps the
        /// band. <c>false</c> (the default) fills the band, as a Word table does.
        /// </summary>
        public bool ShrinkToContent { get; set; }

        /// <summary>A row's least height when nothing states one: 16pt.</summary>
        public const double DefaultMinRowHeightPt = 16;

        /// <summary>
        /// Space between two columns' cells, and between the outer cells and the table's edge, in
        /// points — CSS's horizontal <c>border-spacing</c> in the separated border model. 0 (the
        /// default) sets the cells edge to edge, as the renderer always did.
        /// </summary>
        public double CellSpacingPt { get; set; }

        /// <summary>Space between two rows, and above the first and below the last, in points — CSS's vertical <c>border-spacing</c>. 0 by default.</summary>
        public double RowSpacingPt { get; set; }
    }

    public sealed class TableRow
    {
        public List<TableCell> Cells { get; } = new();
        /// <summary>Explicit row height in points; null = content-derived.</summary>
        public double? HeightPt { get; set; }
        /// <summary>True = exact height (DOCX hRule "exact"); false = "atLeast" (minimum).</summary>
        public bool ExactHeight { get; set; }
        /// <summary>Header row, repeated across page breaks when the table sets RepeatHeaderRow.</summary>
        public bool IsHeader { get; set; }
    }

    /// <summary>
    /// Where content sits in the height it is given: a table cell's in its row
    /// (<see cref="TableCell.VAlign"/>), a box's in its least height (<see cref="BoxStart.ContentAlign"/>),
    /// a positioned block's in its band (<see cref="AbsolutePlacement.BandAlign"/>).
    /// </summary>
    public enum CellVerticalAlign
    {
        Top,
        Center,
        Bottom,
        /// <summary>
        /// A table cell's first baseline on its row's baseline, shared with the row's other
        /// <see cref="Baseline"/> cells (CSS 2.1 §17.5.3) — the cell lowered by the difference, the row
        /// grown to hold it; and the row's baseline is then the table's (CSS Tables 3 §3.10.2). Anywhere
        /// else the same as <see cref="Top"/>.
        /// </summary>
        Baseline,
    }

    /// <summary>
    /// What a table header cell labels — the <c>/Scope</c> of its <c>/TH</c> in tagged output.
    /// A header cell with neither a scope nor an explicit id/headers association is reported by
    /// PDF/UA checkers, so <see cref="None"/> is a real answer only for a non-header cell.
    /// </summary>
    public enum TableHeaderScope { None, Row, Column, Both }
    public enum VerticalMerge { None, Restart, Continue }

    public sealed class TableCell
    {
        /// <summary>Simple paragraph content (back-compatible). For richer content
        /// (nested tables interleaved with paragraphs) the renderer prefers
        /// <see cref="Blocks"/> when it is non-empty.</summary>
        public List<Paragraph> Paragraphs { get; } = new();
        /// <summary>Richer block content (paragraphs + nested tables, in order). When
        /// non-empty this supersedes <see cref="Paragraphs"/>.</summary>
        public List<IDocElement> Blocks { get; } = new();

        /// <summary>
        /// A header cell — tagged as <c>/TH</c> rather than <c>/TD</c>.
        ///
        /// <para>Distinct from <see cref="TableRow.IsHeader"/>, which is a different question:
        /// the row flag drives header repetition across page breaks and is set when <i>any</i>
        /// cell is a header, while this is per cell.</para>
        /// </summary>
        public bool IsHeader { get; set; }

        /// <summary>What a header cell labels — written as <c>/A &lt;&lt;/O /Table /Scope …&gt;&gt;</c>.</summary>
        public TableHeaderScope HeaderScope { get; set; }

        public double? WidthPt { get; set; }
        /// <summary>Cell background / shading fill (hex); "" = none.</summary>
        public string BackgroundColor { get; set; } = "";
        /// <summary>Horizontal cells spanned (DOCX <c>w:gridSpan</c>); 1 = none.</summary>
        public int GridSpan { get; set; } = 1;
        /// <summary>Vertical merge state (DOCX <c>w:vMerge</c>).</summary>
        public VerticalMerge VMerge { get; set; } = VerticalMerge.None;
        public CellVerticalAlign VAlign { get; set; } = CellVerticalAlign.Top;

        /// <summary>Per-side cell borders; null = inherit the table border.</summary>
        public Border? BorderTop { get; set; }
        public Border? BorderBottom { get; set; }
        public Border? BorderLeft { get; set; }
        public Border? BorderRight { get; set; }

        /// <summary>
        /// The inner padding the PDF renderer gives every cell on every side, in points. The
        /// per-side margins below are <b>added</b> to it, so two adjacent cells' text is
        /// <c>2 × DefaultPaddingPt</c> apart before either sets a margin.
        /// </summary>
        /// <remarks>
        /// Public because a producer laying columns out as a table has to know it: CSS
        /// <c>column-gap</c> is the distance between two columns' text, and the mapper that turns
        /// it into cell margins would otherwise carry its own copy of this number.
        /// </remarks>
        public const double DefaultPaddingPt = 3;

        /// <summary>
        /// The inner padding the PDF renderer gives this cell on every side, under the margins
        /// below; <see cref="DefaultPaddingPt"/> unless the producer states its padding exactly.
        /// </summary>
        /// <remarks>
        /// The HTML converter sets 0 and puts the CSS <c>padding</c> (or a browser's 1px default)
        /// in the margins: a cell's padding is what its CSS says, and the renderer's 3pt on top of
        /// Bootstrap's 8px made every row 8px taller than a browser's.
        /// </remarks>
        public double InnerPaddingPt { get; set; } = DefaultPaddingPt;

        /// <summary>Per-side inner padding in points, added to <see cref="InnerPaddingPt"/>.</summary>
        public double MarginLeft { get; set; }
        public double MarginRight { get; set; }
        public double MarginTop { get; set; }
        public double MarginBottom { get; set; }

        /// <summary>Returns the authoritative content blocks (Blocks if present, else Paragraphs).</summary>
        public IEnumerable<IDocElement> Content()
        {
            if (Blocks.Count > 0) return Blocks;
            return Paragraphs;
        }
    }

    /// <summary>Page header content.</summary>
    public sealed class Header
    {
        /// <summary>Simple paragraph content. When <see cref="Blocks"/> is non-empty it supersedes this, as a table cell's does.</summary>
        public List<Paragraph> Paragraphs { get; } = new();
        /// <summary>
        /// Richer content — paragraphs, tables and lists, in order: a letterhead's table beside its text. When
        /// non-empty this supersedes <see cref="Paragraphs"/>. Drawn without paginating, untagged (a header is
        /// a pagination artifact).
        /// </summary>
        public List<IDocElement> Blocks { get; } = new();

        /// <summary>Returns the authoritative content blocks (Blocks if present, else Paragraphs).</summary>
        public IEnumerable<IDocElement> Content() => Blocks.Count > 0 ? Blocks : Paragraphs;
    }

    /// <summary>Page footer content.</summary>
    public sealed class Footer
    {
        /// <summary>Simple paragraph content. When <see cref="Blocks"/> is non-empty it supersedes this, as a table cell's does.</summary>
        public List<Paragraph> Paragraphs { get; } = new();
        /// <summary>
        /// Richer content — paragraphs, tables and lists, in order. When non-empty this supersedes
        /// <see cref="Paragraphs"/>. Drawn without paginating, untagged (a footer is a pagination artifact).
        /// </summary>
        public List<IDocElement> Blocks { get; } = new();

        /// <summary>Returns the authoritative content blocks (Blocks if present, else Paragraphs).</summary>
        public IEnumerable<IDocElement> Content() => Blocks.Count > 0 ? Blocks : Paragraphs;
    }

    /// <summary>A bullet or numbered list item.</summary>
    public sealed class ListItem : IDocElement
    {
        public string Text { get; set; } = "";
        public int Level { get; set; }
        public bool IsNumbered { get; set; }
        public TextRun? TextFormat { get; set; }
        /// <summary>
        /// The marker's own style — CSS's <c>::marker</c>: its colour, size, weight and face —
        /// overriding <see cref="TextFormat"/> for the marker alone. <c>null</c> (the default) is
        /// the item's own style, as the marker has always taken.
        /// </summary>
        public TextRun? MarkerFormat { get; set; }
        /// <summary>Explicit, pre-resolved list marker (e.g. "1.2.", "•", "א."); when set
        /// it overrides the default bullet/number derived from <see cref="IsNumbered"/>.</summary>
        public string? Marker { get; set; }
        /// <summary>Optional rich runs (overrides <see cref="Text"/> when non-empty).</summary>
        public List<IInlineElement> Runs { get; } = new();
        public TextDirection? Direction { get; set; }

        /// <summary>Exact line height of the item's text in points; 0 (the default) is automatic, as <see cref="Paragraph.LineSpacingPt"/>.</summary>
        public double LineSpacingPt { get; set; }
        /// <summary>
        /// Line height as a multiple of each line's largest font size, with no floor — CSS's
        /// <c>line-height: normal</c> — used when <see cref="LineSpacingPt"/> is 0. 0 (the default)
        /// is the exporter's own: 1.16 of the size, never under 14pt, which a browser does not have.
        /// </summary>
        public double LineHeightFactor { get; set; }

        /// <summary>
        /// Space after the item's text, in points. Default <see cref="DefaultSpacingAfterPt"/>, the gap
        /// the exporter always left; the HTML converter sets 0, as a browser's list items have none.
        /// </summary>
        public double SpacingAfter { get; set; } = DefaultSpacingAfterPt;

        /// <summary>The space after a list item that states none: 2pt.</summary>
        public const double DefaultSpacingAfterPt = 2;

        /// <summary>A list nested under this item, or <see langword="null"/>.</summary>
        /// <remarks>
        /// <b>Nesting belongs to the item, not to <see cref="Level"/>.</b> An item still carries a
        /// level, because three producers set it and four exporters indent by it, but a level is a
        /// number beside an item rather than a relationship between items: it cannot say where a
        /// sublist starts or stops, so it cannot number one, and it cannot be turned into
        /// <c>&lt;ul&gt;</c>, <c>&lt;text:list&gt;</c> or <c>/L</c> without guessing at the
        /// boundaries. A sublist here is that relationship, stated once.
        /// </remarks>
        public ListBlock? Sublist { get; set; }
    }

    /// <summary>
    /// A table of contents: one entry per heading, with the page each heading starts on.
    /// </summary>
    /// <remarks>
    /// <para><b>The entries are collected, not listed by the caller.</b> A heading is a
    /// <see cref="Paragraph"/> whose <see cref="Paragraph.Role"/> is <c>H1</c>–<c>H6</c>, so
    /// the document already says which of its paragraphs belong here — and a hand-maintained
    /// list is one that goes stale the first time a section is added.</para>
    ///
    /// <para><b>Page numbers are only knowable after layout, which is why the PDF export runs
    /// twice.</b> A heading's page depends on everything before it, including the contents block
    /// itself; the first pass records where each heading landed and the second draws the numbers.
    /// The two passes agree because the block's height does not depend on the numbers — one
    /// line per entry either way, with the number right-aligned at the margin.</para>
    ///
    /// <para><b>The formats that have no page do not get one.</b> HTML and ODT link to the
    /// heading instead, which is the more useful thing there and the only correct one: a page
    /// number in a document that reflows is a number about a layout the reader is not looking
    /// at.</para>
    /// </remarks>
    public sealed class TocBlock : IDocElement
    {
        /// <summary>A heading above the entries, or null for none.</summary>
        public string? Title { get; set; } = "Contents";

        /// <summary>
        /// The deepest heading level to include: 1 lists only <c>H1</c>, 3 lists <c>H1</c> through
        /// <c>H3</c>.
        /// </summary>
        public int MaxLevel { get; set; } = 3;

        /// <summary>Indent per heading level, in points.</summary>
        public double LevelIndentPt { get; set; } = 18;

        /// <summary>
        /// Draw a dotted leader between an entry and its page number.
        /// </summary>
        /// <remarks>
        /// PDF only. HTML and ODT have no page number to lead to, and a leader with nothing at the
        /// end of it is a row of dots.
        /// </remarks>
        public bool DottedLeader { get; set; } = true;
    }

    /// <summary>A list: its items, whether it is ordered, and where its numbering starts.</summary>
    /// <remarks>
    /// <para><b>What a container adds that a flat <see cref="ListItem"/> could not.</b> A loose item
    /// carries <see cref="ListItem.IsNumbered"/> and an <see cref="ListItem.Level"/>, and neither
    /// says which items belong together — so an ordinal has nowhere to come from. The exporters
    /// answered that by numbering from the level (<c>Level + 1</c>), which makes ten sibling items
    /// all render "1."; the alternative was for the caller to pre-compute every marker and pass it
    /// in <see cref="ListItem.Marker"/>, which is what <c>HtmlToBuilderMapper</c> does today.</para>
    ///
    /// <para><b>Both spellings still work and neither is deprecated.</b> A document may hold loose
    /// <see cref="ListItem"/>s in <see cref="Section.Children"/> exactly as before — every exporter
    /// still renders them, and a pre-resolved <see cref="ListItem.Marker"/> still wins over anything
    /// derived. What a container changes is that the marker can be derived <i>correctly</i>, and
    /// that the tagged-PDF exporter has something to emit <c>/L</c> and <c>/LI</c> for; a loose item
    /// remains a <c>/P</c>, because a list of one paragraph is not a list.</para>
    /// </remarks>
    public sealed class ListBlock : IDocElement
    {
        /// <summary>True for a numbered list, false for a bulleted one.</summary>
        public bool IsOrdered { get; set; }

        /// <summary>
        /// The ordinal of the first item of an ordered list. Mirrors HTML's <c>&lt;ol start&gt;</c>,
        /// which is where a list continued across a page or an interruption gets its number.
        /// </summary>
        public int Start { get; set; } = 1;

        /// <summary>The items, in order.</summary>
        public List<ListItem> Items { get; } = new();

        /// <summary>
        /// The distance from the list's edge to its items' text, in points — CSS's
        /// <c>padding-inline-start</c> — with each marker just before the text, and a list nested in
        /// an item starting at that item's text; 0 puts the text at the edge and the marker outside
        /// it. <c>null</c> (the default) is the exporter's own layout: the text a marker's width in,
        /// each level 18pt further.
        /// </summary>
        public double? TextIndentPt { get; set; }

        /// <summary>Space above the list, in points. Default 0.</summary>
        public double SpacingBefore { get; set; }

        /// <summary>Space below the list, in points. Default 0. The HTML converter sets a list's CSS margins, else the UA stylesheet's 1em.</summary>
        public double SpacingAfter { get; set; }

        /// <summary>
        /// Direction for the whole list; each item may still override it. Null inherits from the
        /// section.
        /// </summary>
        public TextDirection? Direction { get; set; }

        /// <summary>
        /// This list goes on from the list drawn before it: one list cut into consecutive parts — the
        /// HTML converter cuts one round an item that is the containing block of positioned content,
        /// which needs a box of its own. Its <see cref="Start"/> carries the numbering on;
        /// the PDF exporter tags its items in the earlier part's <c>/L</c>. <c>false</c> (the default)
        /// is a list of its own.
        /// </summary>
        public bool ContinuesPrevious { get; set; }

        /// <summary>
        /// How deep among the items of the lists drawn before it this list stands, as a block of the
        /// flow: 0 (the default) a list of its own; 1 the sublist of the last item drawn of the list
        /// before it at 0, taken out of its item into the flow — the HTML converter takes one out
        /// that is, or holds an item that is, the containing block of positioned content, which needs
        /// a box of its own; 2 the sublist of an item of that one, and so on. The PDF
        /// exporter tags it in that item's <c>/LI</c>; with <see cref="ContinuesPrevious"/> it goes on
        /// from the list drawn at its own depth. Its <see cref="TextIndentPt"/> is from the flow's
        /// edge, its item's text start included.
        /// </summary>
        public int NestingDepth { get; set; }
    }
}
