// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Tests for text render mode (ISO 32000-1 Table 106 / "Tr") awareness in structured
/// text extraction.
///
/// <para><b>Invisible text is extracted, except where it copies visible text.</b> Modes 3 and 7
/// are how an OCR tool puts searchable text over a scanned page, so dropping every such glyph
/// would extract a scanned page as nothing, and search-driven redaction would find nothing to
/// remove. A glyph is dropped only when a visible glyph of similar size and the same rotation
/// covers its centre.</para>
///
/// <para><b>What the rule prevents.</b> A page that draws an invisible (mode 3)
/// searchable/accessibility text layer directly alongside styled visible glyphs for the SAME
/// content — a modern, accessibility-conscious "digital statement" template does — would, with
/// every glyph extracted regardless of render mode, emit both copies. Clustered into the same
/// line, the two interleaved character streams come out as character-doubled garbage
/// (<c>"מזון ומשקאות"</c>-shaped input becomes <c>"ממזזווןן..."</c>-shaped output), corrupting a
/// transaction's date and merchant fields past what a downstream line-matching parser can
/// recognise, and silently dropping a real charge. It is distinct from the bidi-reordering
/// duplication tracked in GAPS.md §21, which fires only when a caller opts into
/// <c>WordAssemblyOptions.ApplyBidi</c>/<c>TableExtractionOptions.ApplyBidi</c> (both default
/// <see langword="false"/>), as structured-text extraction's default path never does.</para>
/// </summary>
public class PdfStructuredTextExtractorInvisibleTextTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    private static byte[] BuildPdf(string contentStream) => BuildPdf(contentStream, Helvetica);

    /// <summary>A one-page document whose resources name each font as /F1, /F2, … in order.</summary>
    private static byte[] BuildPdf(string contentStream, params string[] fonts)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << " +
                string.Concat(fonts.Select((_, i) => $"/F{i + 1} {i + 5} 0 R ")) + ">> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contentStream)} >>\nstream\n{contentStream}\nendstream",
        };
        bodies.AddRange(fonts);

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A Helvetica dictionary whose /Widths gives every code from A to Z the same advance.</summary>
    private static string FontWithUniformWidth(int width) =>
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 90 /Widths [" +
        string.Join(" ", Enumerable.Repeat(width, 26)) + "] >>";

    private static PdfPageTextLayout ExtractPage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms);
        return pages[0];
    }

    private static string LineText(byte[] pdf) =>
        string.Concat(ExtractPage(pdf).Lines.Select(l => l.Text));

    [Fact]
    public void InvisibleRenderMode3_WithNothingVisibleOverIt_IsExtracted()
    {
        // A scanned page's OCR layer: the only text there is, drawn invisible.
        const string cs = "BT /F1 12 Tf 3 Tr 100 700 Td (HIDDEN) Tj ET\n";

        Assert.Equal("HIDDEN", LineText(BuildPdf(cs)));
    }

    [Fact]
    public void ClipOnlyRenderMode7_WithNothingVisibleOverIt_IsExtracted()
    {
        const string cs = "BT /F1 12 Tf 7 Tr 100 700 Td (HIDDEN) Tj ET\n";

        Assert.Equal("HIDDEN", LineText(BuildPdf(cs)));
    }

    [Fact]
    public void InvisibleTextBesideVisibleText_IsExtractedAlongsideIt()
    {
        const string cs =
            "BT /F1 12 Tf 0 Tr 100 700 Td (VISIBLE) Tj ET\n" +
            "BT /F1 12 Tf 3 Tr 300 700 Td (HIDDEN) Tj ET\n";

        var words = ExtractPage(BuildPdf(cs)).Lines.SelectMany(l => l.Words).Select(w => w.Text);

        Assert.Equal(new[] { "VISIBLE", "HIDDEN" }, words);
    }

    [Fact]
    public void InvisibleCopyInANarrowerFont_OverVisibleText_IsStillDropped()
    {
        // The invisible glyphs advance 6 pt and the visible ones 8.4 pt, so from the second glyph on
        // no invisible glyph starts where a visible one does. Each one's centre still lies inside
        // some visible glyph's box, which is what marks the run as a copy.
        const string cs =
            "BT /F2 12 Tf 3 Tr 100 700 Td (ABCDEF) Tj ET\n" +
            "BT /F1 12 Tf 0 Tr 100 700 Td (ABCDEF) Tj ET\n";

        string text = LineText(BuildPdf(cs, FontWithUniformWidth(700), FontWithUniformWidth(500)));

        Assert.Equal("ABCDEF", text);
    }

    [Fact]
    public void InvisibleCopyShiftedLeftAndDown_ByLessThanHalfAGlyph_IsStillDropped()
    {
        // Shifted 4 pt left and 3 pt down, the copy's first glyph starts outside every visible box
        // and every glyph's baseline sits below them, while every centre still lands inside one.
        // The first glyph also starts in the grid cell left of the one its centre is in.
        const string cs =
            "BT /F1 12 Tf 0 Tr 97 700 Td (ABCDEF) Tj ET\n" +
            "BT /F1 12 Tf 3 Tr 93 697 Td (ABCDEF) Tj ET\n";

        Assert.Equal("ABCDEF", LineText(BuildPdf(cs, FontWithUniformWidth(700))));
    }

    [Theory]
    [InlineData(72, 12)]
    [InlineData(12, 72)]
    public void GlyphsOfVeryDifferentSizes_AreNotCopies_AndTheInvisibleOneIsKept(int visibleSize, int invisibleSize)
    {
        // The invisible glyph is placed so its centre is the visible glyph's centre, so only the
        // size can tell them apart. The first row is a watermark letter over a scanned page's OCR
        // text; a copy of a word is set at about the word's size.
        float centreX = 100 + 0.25f * visibleSize, centreY = 700 + 0.5f * visibleSize;
        string cs = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "BT /F1 {0} Tf 0 Tr 100 700 Td (M) Tj ET\nBT /F1 {1} Tf 3 Tr {2} {3} Td (W) Tj ET\n",
            visibleSize, invisibleSize, centreX - 0.25f * invisibleSize, centreY - 0.5f * invisibleSize);

        var chars = ExtractPage(BuildPdf(cs, FontWithUniformWidth(500)))
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars);

        Assert.Contains(chars, c => c.Char == "W");
    }

    [Fact]
    public void InvisibleTextAtADifferentRotation_IsNotACopy_AndIsKept()
    {
        // Turned a quarter, the invisible glyph's centre as the box is computed still lands inside
        // the upright visible glyph. A copy of text is drawn in the text's own orientation.
        const string cs =
            "BT /F1 12 Tf 0 Tr 100 700 Td (M) Tj ET\n" +
            "BT /F1 12 Tf 3 Tr 0 1 -1 0 100 700 Tm (W) Tj ET\n";

        var chars = ExtractPage(BuildPdf(cs, FontWithUniformWidth(500)))
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars);

        Assert.Contains(chars, c => c.Char == "W");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACopyReplacedByActualText_IsStillRecognised(bool actualTextOnTheInvisibleCopy)
    {
        // /ActualText replaces a marked-content section's glyphs with new ones. The replacements
        // must stay invisible when the glyphs they replace were, and visible when any was. The
        // invisible copy is 3 pt right of the visible one (glyphs 7.2 pt wide): covered, and beyond
        // the third of a glyph within which the overlapping-duplicate rule would drop it anyway.
        const string plain = "BT /F1 12 Tf {0} Tr {1} 700 Td (ABC) Tj ET\n";
        const string replaced = "/Span <</ActualText (ABC)>> BDC BT /F1 12 Tf {0} Tr {1} 700 Td (ABC) Tj ET EMC\n";
        string cs = actualTextOnTheInvisibleCopy
            ? string.Format(plain, 0, 100) + string.Format(replaced, 3, 103)
            : string.Format(replaced, 0, 100) + string.Format(plain, 3, 103);

        Assert.Equal("ABC", LineText(BuildPdf(cs, FontWithUniformWidth(600))));
    }

    [Fact]
    public void InvisibleCopyUnderAVisibleGlyphTooLargeForTheGrid_IsStillDropped()
    {
        // At 200 pt the visible glyph's box spans more cells of the coverage grid than a cell list
        // takes, so it is checked from the list of large glyphs instead. The copy is 40 pt right of
        // the 100 pt-wide visible "W": centre inside, but beyond the third of a glyph within which
        // the overlapping-duplicate rule would drop it first.
        const string cs =
            "BT /F1 200 Tf 0 Tr 100 300 Td (W) Tj ET\n" +
            "BT /F1 200 Tf 3 Tr 140 300 Td (W) Tj ET\n";

        var chars = ExtractPage(BuildPdf(cs, FontWithUniformWidth(500)))
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars);

        Assert.Single(chars);
    }

    [Fact]
    public void CoverageCheck_OnACellCrowdedPastTheCap_StopsAndKeepsTheInvisibleGlyph()
    {
        // One more visible glyph than the cap, each 0.012 pt wide, crowded into one 16 pt cell ahead
        // of the visible "X" that covers the invisible one. The candidates a lookup compares are
        // capped, so the covering glyph is never reached and the invisible "X" stays. Stopping early
        // can extract a character twice; it never loses one.
        //
        // The copy sits 3 pt right of the visible "X" (7.2 pt wide): its centre is still inside, but
        // it is further than a third of a glyph away, so the overlapping-duplicate rule cannot drop it
        // in the cap's place.
        string crowd = new string('I', PdfLimitsCap + 1);
        string cs =
            "BT /F2 12 Tf 0 Tr 97 700 Td (" + crowd + ") Tj ET\n" +
            "BT /F1 12 Tf 0 Tr 100 700 Td (X) Tj ET\n" +
            "BT /F1 12 Tf 3 Tr 103 700 Td (X) Tj ET\n";

        var chars = ExtractPage(BuildPdf(cs, FontWithUniformWidth(600), FontWithUniformWidth(1)))
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars);

        Assert.Equal(2, chars.Count(c => c.Char == "X"));
    }

    [Fact]
    public void CoverageCheck_PastTheCapOnLargeGlyphs_StopsAndKeepsTheInvisibleGlyph()
    {
        // Two hundred and fifty-six 200 pt glyphs far below the page fill the list of glyphs too
        // large for the grid, so the visible "W" drawn after them is never compared, and the
        // invisible "W" it covers stays. The copy is 40 pt right of the 100 pt-wide visible "W":
        // centre inside, but beyond the third of a glyph the overlapping-duplicate rule allows.
        string decoys = new string('M', PdfLimitsCap);
        string cs =
            "BT /F1 200 Tf 0 Tr 0 -10000 Td (" + decoys + ") Tj ET\n" +
            "BT /F1 200 Tf 0 Tr 100 300 Td (W) Tj ET\n" +
            "BT /F1 200 Tf 3 Tr 140 300 Td (W) Tj ET\n";

        var chars = ExtractPage(BuildPdf(cs, FontWithUniformWidth(500)))
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars);

        Assert.Equal(2, chars.Count(c => c.Char == "W"));
    }

    private static int PdfLimitsCap => Tetitulz.Pdf.Core.Constants.PdfLimits.MaxInvisibleGlyphCoverCandidates;

    [Fact]
    public void DefaultFillRenderMode_StillExtractsNormally()
    {
        const string cs = "BT /F1 12 Tf 100 700 Td (VISIBLE) Tj ET\n";

        Assert.Equal("VISIBLE", LineText(BuildPdf(cs)));
    }

    [Fact]
    public void ExplicitFillRenderMode0_StillExtractsNormally()
    {
        const string cs = "BT /F1 12 Tf 0 Tr 100 700 Td (VISIBLE) Tj ET\n";

        Assert.Equal("VISIBLE", LineText(BuildPdf(cs)));
    }

    [Fact]
    public void InvisibleTextFollowedByVisibleText_VisibleTextStillAdvancesCorrectly()
    {
        // The invisible run must still consume text-space width — a visible run right after it on
        // the same line depends on that advance landing it in the correct position, not on top of
        // (or missing the gap left by) the invisible run.
        const string invisibleThenVisible = "BT /F1 12 Tf 3 Tr 100 700 Td (HIDDEN) Tj 0 Tr (VISIBLE) Tj ET\n";
        const string visibleAlone = "BT /F1 12 Tf 100 700 Td (VISIBLE) Tj ET\n";

        var withInvisiblePrefix = ExtractPage(BuildPdf(invisibleThenVisible));
        var alone = ExtractPage(BuildPdf(visibleAlone));

        // Both runs are extracted now, and nothing separates them, so they form one word.
        var combinedWord = Assert.Single(withInvisiblePrefix.Lines.SelectMany(l => l.Words));
        var aloneWord = Assert.Single(alone.Lines.SelectMany(l => l.Words));
        Assert.Equal("HIDDENVISIBLE", combinedWord.Text);

        // The invisible "HIDDEN" run advanced Tm, so this copy of "VISIBLE" starts further right
        // than the one with nothing before it.
        float visibleStart = combinedWord.Chars.First(c => c.Char == "V").X;
        Assert.True(visibleStart > aloneWord.X,
            $"expected the invisible prefix to have advanced the text position (got X={visibleStart} vs baseline X={aloneWord.X})");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void InvisibleTextAtTheSamePositionAsVisibleText_DoesNotDuplicateInOutput(int renderMode)
    {
        // The exact real-world shape that produced character-doubled garbage: an invisible
        // accessibility/OCR text layer drawn at (or very near) the same position as a visible copy
        // of the same content. Confirmed real on a "digital statement" PDF template.
        string cs =
            "BT /F1 12 Tf " + renderMode + " Tr 100 700 Td (PAYPAL*ALIPAYSINGA) Tj ET\n" +
            "BT /F1 12 Tf 0 Tr 100 700 Td (PAYPAL*ALIPAYSINGA) Tj ET\n";

        var page = ExtractPage(BuildPdf(cs));
        var text = string.Concat(page.Lines.Select(l => l.Text));

        Assert.Equal("PAYPAL*ALIPAYSINGA", text);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void AnInvisibleCopyTooFarAsideToBeADuplicate_IsStillDroppedAsACopy(int renderMode)
    {
        // At exactly the same position either rule removes the copy. Here it is 3 pt right of
        // glyphs 7.2 pt wide: beyond the third of a glyph the overlapping-duplicate rule allows, so
        // only the invisible-copy rule can drop it, and only if the mode counts as invisible.
        string cs =
            "BT /F1 12 Tf " + renderMode + " Tr 103 700 Td (PAYPAL) Tj ET\n" +
            "BT /F1 12 Tf 0 Tr 100 700 Td (PAYPAL) Tj ET\n";

        Assert.Equal("PAYPAL", LineText(BuildPdf(cs, FontWithUniformWidth(600))));
    }
}
