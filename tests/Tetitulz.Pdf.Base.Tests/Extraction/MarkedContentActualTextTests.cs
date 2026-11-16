// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Extraction;

/// <summary>
/// §14.9.4 <c>/ActualText</c> on a <c>BDC</c> — "an exact replacement for the marked-content
/// sequence's content" — applied during extraction.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> A producer writes it when the glyphs do not spell what the text
/// says: an <c>fi</c> ligature drawn from one glyph, a phone number set in a decorative face, a date
/// assembled from separate runs, a word split across two show operators for kerning. Extraction that
/// ignores it returns what was <i>drawn</i> rather than what was <i>written</i> — and the two differ
/// exactly where the producer thought the difference mattered enough to say so.</para>
///
/// <para><b>Every fixture makes the glyphs and the replacement disagree</b>, which is the only way
/// to tell the feature from its absence. A fixture whose <c>/ActualText</c> repeated the glyphs
/// would pass with the whole thing deleted.</para>
///
/// <para><b>The positions are asserted, not only the string.</b> The replacement has to land where
/// the glyphs it replaced were, or word and line grouping downstream runs on invented coordinates
/// and a later change to the layout cannot be seen.</para>
/// </remarks>
public class MarkedContentActualTextTests
{
    /// <summary>The replacement is extracted, and the glyphs it replaces are not.</summary>
    /// <remarks>
    /// Both halves matter. A reader that appended the replacement without removing the glyphs would
    /// satisfy "the text is there" and hand a caller both spellings of the same word.
    /// </remarks>
    [Fact]
    public void TheReplacementIsExtractedAndTheGlyphsAreNot()
    {
        string text = Extract(Page(
            "/Span << /ActualText (fifty) >> BDC\n" +
            "BT /F1 12 Tf 72 700 Td (XY) Tj ET\n" +
            "EMC\n"));

        Assert.Contains("fifty", text, StringComparison.Ordinal);
        Assert.DoesNotContain("XY", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// It replaces the <b>whole sequence</b>, not one show operator.
    /// </summary>
    /// <remarks>
    /// §14.9.4 says the sequence's content, and a word split across several <c>Tj</c>s for kerning
    /// is the case the feature exists for. A per-operator implementation would leave the second half
    /// of the drawn text behind.
    /// </remarks>
    [Fact]
    public void ItReplacesEveryShowOperatorInTheSequence()
    {
        string text = Extract(Page(
            "/Span << /ActualText (Wave) >> BDC\n" +
            "BT /F1 12 Tf 72 700 Td (WWW) Tj 20 0 Td (ZZZ) Tj ET\n" +
            "EMC\n"));

        Assert.Contains("Wave", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WWW", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZZ", text, StringComparison.Ordinal);
    }

    /// <summary>Text outside the sequence is untouched.</summary>
    /// <remarks>
    /// The containment control: an implementation that replaced from the <c>BDC</c> to the end of
    /// the page would pass every test above and destroy the rest of the document.
    /// </remarks>
    [Fact]
    public void TextOutsideTheSequenceIsUntouched()
    {
        string text = Extract(Page(
            "BT /F1 12 Tf 72 720 Td (before) Tj ET\n" +
            "/Span << /ActualText (middle) >> BDC\n" +
            "BT /F1 12 Tf 72 700 Td (QQQ) Tj ET\n" +
            "EMC\n" +
            "BT /F1 12 Tf 72 680 Td (after) Tj ET\n"));

        Assert.Contains("before", text, StringComparison.Ordinal);
        Assert.Contains("middle", text, StringComparison.Ordinal);
        Assert.Contains("after", text, StringComparison.Ordinal);
        Assert.DoesNotContain("QQQ", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/ActualText ()</c> removes the glyphs and contributes nothing.
    /// </summary>
    /// <remarks>
    /// An empty string is not an absent one: it is how a producer marks a decorative run — a
    /// drop-cap repeat, a rule drawn with glyphs — as contributing no text. Reading it as absent
    /// would leave the decoration in the extraction, which is the whole reason the producer wrote
    /// it.
    /// </remarks>
    [Fact]
    public void AnEmptyReplacementRemovesTheGlyphsAndAddsNothing()
    {
        string text = Extract(Page(
            "BT /F1 12 Tf 72 720 Td (kept) Tj ET\n" +
            "/Span << /ActualText () >> BDC\n" +
            "BT /F1 12 Tf 72 700 Td (DROPPED) Tj ET\n" +
            "EMC\n"));

        Assert.Contains("kept", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DROPPED", text, StringComparison.Ordinal);
    }

    /// <summary>Only the outermost replacement applies when they nest.</summary>
    /// <remarks>
    /// A nested <c>/ActualText</c> sits inside content the outer one already replaces, so honouring
    /// it would replace the same glyphs twice — and the inner string would survive into the output
    /// beside the outer one, which is neither reading of §14.9.4.
    /// </remarks>
    [Fact]
    public void OnlyTheOutermostReplacementApplies()
    {
        string text = Extract(Page(
            "/Span << /ActualText (outer) >> BDC\n" +
            "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\n" +
            "/Span << /ActualText (inner) >> BDC\n" +
            "BT /F1 12 Tf 100 700 Td (BBB) Tj ET\n" +
            "EMC\n" +
            "EMC\n"));

        Assert.Contains("outer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("inner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AAA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BBB", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The replacement lands where the glyphs were, not at the origin.
    /// </summary>
    /// <remarks>
    /// Positions have to come from somewhere and the run's own extent is the only honest source. If
    /// they came from nowhere the words would still extract and every assertion above would pass,
    /// while a caller reading coordinates — a redactor, a search highlighter, a reading-order
    /// analyser — would be handed the top-left corner of the page.
    /// </remarks>
    [Fact]
    public void TheReplacementLandsWhereTheGlyphsWere()
    {
        List<PdfTextWord> words = Words(Page(
            "/Span << /ActualText (moved) >> BDC\n" +
            "BT /F1 12 Tf 300 500 Td (XXXX) Tj ET\n" +
            "EMC\n"));

        PdfTextWord word = Assert.Single(words.Where(w => w.Text.Contains("moved", StringComparison.Ordinal)));

        Assert.True(Math.Abs(word.X - 300) < 5, $"the replacement is at x={word.X:F1}, not near 300");
        Assert.True(Math.Abs(word.Y - 500) < 12, $"the replacement is at y={word.Y:F1}, not near 500");
    }

    /// <summary>
    /// <b>A sequence that drew no glyphs reaches the string extractor and not the positioned one,
    /// and that divergence is deliberate.</b>
    /// </summary>
    /// <remarks>
    /// <para><c>/ActualText</c> on an image or an empty span — "Chart of Q3 revenue" — is the case
    /// the feature most earns its keep on, and a caller asking for a page's <i>text</i> should get
    /// it. But a caller asking for positioned characters has to be told <i>where</i>, and there is
    /// no extent: inventing one puts a word somewhere the page does not have it, and every
    /// downstream consumer of coordinates — a redactor, a search highlighter, a reading-order
    /// analyser — would act on it.</para>
    ///
    /// <para><b>So the two extractors differ here on purpose, and both sides are asserted.</b> This
    /// repository's recurring defect is two implementations of one rule drifting apart silently;
    /// the answer is not to force them to agree where their outputs genuinely differ, but to make
    /// the difference explicit and to have a test fail if either side changes its mind. The rule
    /// they <i>do</i> share — what a property list declares — is <c>Core.Text.MarkedContentInfo</c>,
    /// one reading for both.</para>
    /// </remarks>
    [Fact]
    public void ASequenceThatDrewNothingReachesTheStringExtractorButNotThePositionedOne()
    {
        byte[] pdf = Page(
            "BT /F1 12 Tf 72 720 Td (real) Tj ET\n" +
            "/Span << /ActualText (phantom) >> BDC\n" +
            "EMC\n");

        string text = Extract(pdf);
        Assert.Contains("real", text, StringComparison.Ordinal);
        Assert.Contains("phantom", text, StringComparison.Ordinal);

        // Positioned extraction has nowhere to put it, so it is absent rather than at a guess.
        List<PdfTextChar> chars = Chars(pdf);
        string positioned = string.Concat(chars.Select(c => c.Char));
        Assert.DoesNotContain("phantom", positioned, StringComparison.Ordinal);

        // The potency assertion, and it is not decoration: removing the "nothing was drawn" guard
        // makes ApplyActualText index one past the end, ExtractPage catches that and returns an
        // EMPTY layout — on which "does not contain phantom" is trivially true. A mutation survived
        // this test for exactly that reason. An absence is also what total failure looks like.
        Assert.Contains("real", positioned, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The replacement is measured in text elements, not UTF-16 code units</b>, so a character
    /// outside the BMP stays one cell.
    /// </summary>
    /// <remarks>
    /// Per code unit, a surrogate pair becomes two cells holding one lone surrogate each — neither
    /// of which is a character — placed side by side as though the producer had written two. The
    /// glyph run being replaced was one thing, and so is its replacement.
    /// </remarks>
    [Fact]
    public void AReplacementOutsideTheBmpIsOneCell()
    {
        // <FEFF D835 DC00> — U+1D400 MATHEMATICAL BOLD CAPITAL A, as a UTF-16BE text string.
        List<PdfTextChar> chars = Chars(Page(
            "/Span << /ActualText <FEFFD835DC00> >> BDC\n" +
            "BT /F1 12 Tf 100 500 Td (XX) Tj ET\n" +
            "EMC\n"));

        PdfTextChar only = Assert.Single(chars);
        Assert.Equal("\U0001D400", only.Char);
    }

    /// <summary>
    /// <b>The replacement is spread across the extent of the glyphs it replaces</b>, rather than
    /// each cell taking the first glyph's width.
    /// </summary>
    /// <remarks>
    /// The run's own extent is the only honest source of positions, and the cells have to divide it
    /// — otherwise a two-character replacement for a wide run bunches up at the left edge and a
    /// caller reading coordinates highlights a fraction of what the reader sees.
    /// </remarks>
    [Fact]
    public void TheReplacementCellsDivideTheRunsExtent()
    {
        // Eight glyphs at 12 pt is roughly 58 pt of page; two cells must be about 29 pt apart, not
        // one glyph-width apart.
        List<PdfTextChar> chars = Chars(Page(
            "/Span << /ActualText (ab) >> BDC\n" +
            "BT /F1 12 Tf 100 500 Td (XXXXXXXX) Tj ET\n" +
            "EMC\n"));

        Assert.Equal(2, chars.Count);
        Assert.True(chars[1].X - chars[0].X > 20f,
            $"the two cells are {chars[1].X - chars[0].X:F1} pt apart, so they are not dividing the run");
    }

    /// <summary>
    /// <b>Direction is taken from the replacement, not from the glyphs it replaces.</b>
    /// </summary>
    /// <remarks>
    /// The whole point of <c>/ActualText</c> is that the two can disagree — a Hebrew phrase drawn
    /// with a decorative Latin-looking face is exactly the case it exists for — and the replacement
    /// is what a reader is told the text <i>is</i>, so it must group as Hebrew.
    /// </remarks>
    [Fact]
    public void TheDirectionComesFromTheReplacementNotTheGlyphs()
    {
        // <FEFF 05D0 05D1> — alef bet, over Latin glyphs.
        List<PdfTextChar> chars = Chars(Page(
            "/Span << /ActualText <FEFF05D005D1> >> BDC\n" +
            "BT /F1 12 Tf 100 500 Td (AB) Tj ET\n" +
            "EMC\n"));

        Assert.Equal(2, chars.Count);
        Assert.All(chars, c => Assert.True(c.IsRtl, $"'{c.Char}' was not marked right-to-left"));
    }

    /// <summary>
    /// The acceptance control: a <c>BDC</c> with no <c>/ActualText</c> changes nothing.
    /// </summary>
    /// <remarks>
    /// Without it, every assertion above is satisfied by an extractor that drops marked-content
    /// glyphs wholesale — which would be a far larger bug wearing the same green tick, since a
    /// tagged PDF wraps most of its text in <c>BDC</c>.
    /// </remarks>
    [Fact]
    public void AMarkedContentSequenceWithoutActualTextIsUnchanged()
    {
        string text = Extract(Page(
            "/P << /MCID 0 >> BDC\n" +
            "BT /F1 12 Tf 72 700 Td (ordinary) Tj ET\n" +
            "EMC\n"));

        Assert.Contains("ordinary", text, StringComparison.Ordinal);
    }

    /// <summary>The <c>/MCID</c> survives the replacement, so tagged extraction still binds it.</summary>
    /// <remarks>
    /// A replacement that lost the MCID would vanish from <c>PdfTaggedTextExtractor</c>'s output
    /// entirely — the element it belongs to would find no marked content — which is a worse outcome
    /// than not applying <c>/ActualText</c> at all.
    /// </remarks>
    [Fact]
    public void TheReplacementKeepsTheSequencesMcid()
    {
        List<PdfTextChar> chars = Chars(Page(
            "/Span << /MCID 7 /ActualText (bound) >> BDC\n" +
            "BT /F1 12 Tf 72 700 Td (ZZZ) Tj ET\n" +
            "EMC\n"));

        var replacement = chars.Where(c => "bound".Contains(c.Char, StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(replacement);
        Assert.All(replacement, c => Assert.Equal(7, c.Mcid));
    }

    /// <summary>A property list named through <c>/Properties</c> works like an inline one.</summary>
    /// <remarks>
    /// Both spellings are legal and a producer picks one per file, so a reader that handled only the
    /// inline form would apply the feature on some documents and not others with nothing to
    /// distinguish them.
    /// </remarks>
    [Fact]
    public void AnActualTextReachedThroughThePropertiesResourceIsApplied()
    {
        string text = Extract(Page(
            "/Span /MC0 BDC\n" +
            "BT /F1 12 Tf 72 700 Td (RRR) Tj ET\n" +
            "EMC\n",
            properties: "/Properties << /MC0 << /MCID 0 /ActualText (named) >> >>"));

        Assert.Contains("named", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RRR", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// In a named property list, <c>/ActualText</c> and <c>/MCID</c> written as indirect references
    /// are applied — by the string extractor and by the positioned one.
    /// </summary>
    /// <remarks>
    /// An inline property list lives in the content stream, where no reference can be written; one
    /// reached through <c>/Properties</c> is an ordinary dictionary, where any value may be. The
    /// shared reading resolved the dictionary and then its entries raw, so the replacement was not
    /// applied and the glyphs it covers were extracted, unbound to any structure element.
    /// </remarks>
    [Fact]
    public void AnIndirectActualTextAndMcidInANamedPropertyListAreApplied()
    {
        byte[] pdf = Page(
            "/Span /MC0 BDC\n" +
            "BT /F1 12 Tf 72 700 Td (RRR) Tj ET\n" +
            "EMC\n",
            properties: "/Properties << /MC0 << /MCID 7 0 R /ActualText 6 0 R >> >>",
            extraObjects: new[] { "(named)", "9" });

        string text = Extract(pdf);
        Assert.Contains("named", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RRR", text, StringComparison.Ordinal);

        var replacement = Chars(pdf).Where(c => "named".Contains(c.Char, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(replacement);
        Assert.All(replacement, c => Assert.Equal(9, c.Mcid));
    }

    // ==================================================================

    private static string Extract(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractText(input);
    }

    private static List<PdfTextWord> Words(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines).SelectMany(l => l.Words).ToList();
    }

    private static List<PdfTextChar> Chars(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines).SelectMany(l => l.Words).SelectMany(w => w.Chars).ToList();
    }

    /// <summary>A one-page PDF whose content stream is <paramref name="content"/>.</summary>
    private static byte[] Page(string content, string properties = "", string[] extraObjects = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> " + properties + " >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        if (extraObjects != null) objects.AddRange(extraObjects);   // objects 6, 7, …

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objects.Count + 1;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
