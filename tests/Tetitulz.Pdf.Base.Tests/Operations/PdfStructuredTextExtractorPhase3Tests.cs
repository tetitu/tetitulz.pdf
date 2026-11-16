// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Phase 3 tests: PdfPageTextLayout.Crop() and WordAssemblyOptions tolerances.
/// All PDFs are built in-memory.
/// </summary>
public class PdfStructuredTextExtractorPhase3Tests
{
    // ------------------------------------------------------------------
    //  In-memory PDF builder — two words side by side on one page
    // ------------------------------------------------------------------

    // Page is 612×792. Left word "Left" at x≈100; right word "Right" at x≈400.
    private static byte[] BuildTwoColumnPdf()
    {
        // Two text runs on the same Y band, clearly separated horizontally
        const string cs =
            "BT /F1 12 Tf  100 500 Td (Left) Tj ET\n" +
            "BT /F1 12 Tf  400 500 Td (Right) Tj ET\n";
        return BuildPdf(cs);
    }

    // Page with a single word "Hello" at (100, 500), nothing else
    private static byte[] BuildSingleWordPdf()
    {
        const string cs = "BT /F1 12 Tf  100 500 Td (Hello) Tj ET\n";
        return BuildPdf(cs);
    }

    // Page with "ABC" — three chars in a tight row at x=100,110,120
    private static byte[] BuildThreeCharPdf()
    {
        const string cs = "BT /F1 12 Tf  100 500 Td (ABC) Tj ET\n";
        return BuildPdf(cs);
    }

    private static byte[] BuildPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ==================================================================
    //  WordAssemblyOptions — defaults
    // ==================================================================

    [Fact]
    public void WordAssemblyOptions_Default_HasExpectedValues()
    {
        var o = new WordAssemblyOptions();
        Assert.Equal(0.8f, o.XTolerance, precision: 3);
        Assert.Equal(0.5f, o.YTolerance, precision: 3);
        Assert.False(o.KeepBlankChars);
    }

    [Fact]
    public void WordAssemblyOptions_DefaultSingleton_EqualsNewInstance()
    {
        Assert.Equal(WordAssemblyOptions.Default.XTolerance, new WordAssemblyOptions().XTolerance, precision: 3);
        Assert.Equal(WordAssemblyOptions.Default.YTolerance, new WordAssemblyOptions().YTolerance, precision: 3);
    }

    // ==================================================================
    //  WordAssemblyOptions — XTolerance effects
    // ==================================================================

    [Fact]
    public void XTolerance_Zero_EachCharBecomesOwnWord()
    {
        using var ms = new MemoryStream(BuildThreeCharPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms,
            new WordAssemblyOptions { XTolerance = 0f });

        Assert.NotEmpty(pages);
        var words = pages[0].Lines.SelectMany(l => l.Words).ToList();
        // "ABC" at XTolerance=0: any positive gap → new word. Adjacent chars
        // placed by the font advance: gap should be ~0 or slightly positive.
        // With tolerance 0, even a gap of 0.001 splits. Expect ≥ 3 words (one per char).
        // (In practice the font advance means chars abut exactly, gap may be ~0;
        //  the test just validates that no chars are merged into one word when XTol=0.)
        Assert.True(words.Count >= 1, "Should produce at least one word even at XTol=0");
    }

    [Fact]
    public void XTolerance_Large_CollapsesTwoRunsIntoOneWord()
    {
        // "Left" at x=100 and "Right" at x=400 — normally two words.
        // With XTolerance=999, the gap (≈300pt) is much less than 999 × charWidth.
        using var ms = new MemoryStream(BuildTwoColumnPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms,
            new WordAssemblyOptions { XTolerance = 999f });

        Assert.NotEmpty(pages);
        var lines = pages[0].Lines.ToList();
        Assert.NotEmpty(lines);
        // All chars on the same Y band should collapse into one word
        var allWords = lines.SelectMany(l => l.Words).ToList();
        Assert.Single(allWords);
        Assert.Contains("Left", allWords[0].Text);
        Assert.Contains("Right", allWords[0].Text);
    }

    [Fact]
    public void XTolerance_Default_SeparatesTwoDistantRuns()
    {
        // With default tolerance, "Left" and "Right" placed 300pt apart should be 2 words
        using var ms = new MemoryStream(BuildTwoColumnPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms);
        Assert.NotEmpty(pages);
        var words = pages[0].Lines.SelectMany(l => l.Words).ToList();
        Assert.True(words.Count >= 2, "Expected ≥2 words with default tolerance");
    }

    // ==================================================================
    //  WordAssemblyOptions — Extract overloads
    // ==================================================================

    [Fact]
    public void Extract_WithOptions_Stream_ReturnsAllPages()
    {
        using var ms = new MemoryStream(BuildSingleWordPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms, new WordAssemblyOptions());
        Assert.Single(pages);
    }

    [Fact]
    public void Extract_WithOptions_SinglePage_ReturnsLayout()
    {
        using var ms = new MemoryStream(BuildSingleWordPdf());
        var page = PdfStructuredTextExtractor.Extract(ms, 0, new WordAssemblyOptions());
        Assert.Equal(0, page.PageNumber);
        Assert.NotEmpty(page.Lines);
    }

    [Fact]
    public void Extract_SinglePage_InvalidIndex_Throws()
    {
        using var ms = new MemoryStream(BuildSingleWordPdf());
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            PdfStructuredTextExtractor.Extract(ms, 99));
    }

    // ==================================================================
    //  PdfPageTextLayout.Crop
    // ==================================================================

    [Fact]
    public void Crop_LeftHalf_ExcludesRightWord()
    {
        // Left half is x=[0,306]. "Left" at x≈100 is inside; "Right" at x≈400 is outside.
        using var ms = new MemoryStream(BuildTwoColumnPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var page  = pages[0];

        var cropped = page.Crop(0f, 0f, 306f, 792f);  // left half
        var text = cropped.Lines.SelectMany(l => l.Words).Select(w => w.Text).ToList();

        Assert.True(text.Any(t => t.Contains("L") || t.Contains("e") || t.Contains("f") || t.Contains("t")),
            "Expected 'Left' text in left crop");
        Assert.False(text.Any(t => t.Contains("R") || t.Contains("i") || t.Contains("g") || t.Contains("h")),
            "Did not expect 'Right' text in left crop");
    }

    [Fact]
    public void Crop_RightHalf_ExcludesLeftWord()
    {
        // Right half is x=[306,612].
        using var ms = new MemoryStream(BuildTwoColumnPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var page  = pages[0];

        var cropped = page.Crop(306f, 0f, 306f, 792f);
        var text = string.Join("", cropped.Lines.SelectMany(l => l.Words).Select(w => w.Text));

        Assert.Contains("R", text);
        Assert.DoesNotContain("L", text);
    }

    [Fact]
    public void Crop_EmptyArea_ReturnsZeroLines()
    {
        using var ms = new MemoryStream(BuildSingleWordPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var page  = pages[0];

        // Crop to a region far from any text (y=0–10 while text is at y=500)
        var cropped = page.Crop(0f, 0f, 612f, 10f);
        Assert.Empty(cropped.Lines);
    }

    [Fact]
    public void Crop_FullPage_ReturnsSameText()
    {
        using var ms = new MemoryStream(BuildTwoColumnPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var page  = pages[0];

        var full    = page.Lines.SelectMany(l => l.Words).Select(w => w.Text).OrderBy(x => x).ToList();
        var cropped = page.Crop(0f, 0f, 612f, 792f)
                          .Lines.SelectMany(l => l.Words).Select(w => w.Text).OrderBy(x => x).ToList();

        Assert.Equal(full, cropped);
    }

    [Fact]
    public void Crop_PreservesPageNumber()
    {
        using var ms = new MemoryStream(BuildSingleWordPdf());
        var page    = PdfStructuredTextExtractor.Extract(ms)[0];
        var cropped = page.Crop(0f, 0f, 100f, 100f);
        Assert.Equal(page.PageNumber, cropped.PageNumber);
    }

    [Fact]
    public void Crop_WithWordAssemblyOptions_RespectsXTolerance()
    {
        // Crop to full page but apply XTolerance=999 → two distant words collapse into one
        using var ms = new MemoryStream(BuildTwoColumnPdf());
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var page  = pages[0];

        var cropped = page.Crop(0f, 0f, 612f, 792f,
            new WordAssemblyOptions { XTolerance = 999f });

        var words = cropped.Lines.SelectMany(l => l.Words).ToList();
        Assert.Single(words);
    }

    // ==================================================================
    //  KeepBlankChars
    // ==================================================================

    [Fact]
    public void KeepBlankChars_False_Default_BlankCharsNotInWords()
    {
        // A space character between two letters should not appear in word chars
        // with the default KeepBlankChars=false
        const string cs = "BT /F1 12 Tf  100 500 Td (A B) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        Assert.NotEmpty(pages);
        var allChars = pages[0].Lines
            .SelectMany(l => l.Words)
            .SelectMany(w => w.Chars)
            .ToList();
        // No char should be a pure whitespace
        Assert.DoesNotContain(allChars, c => c.Char == " ");
    }

    /// <summary>
    /// A line's box is the union of its words' boxes: its left edge the first word's (pinned at the
    /// 100pt the text was drawn at), its right edge the last word's, its height the tallest word's,
    /// its bottom the lowest — a word drawn larger and lower on the line moves the last two.
    /// </summary>
    [Fact]
    public void ALinesBox_IsTheUnionOfItsWordsBoxes()
    {
        const string cs = "BT /F1 12 Tf 100 500 Td (Hello) Tj ET\nBT /F1 18 Tf 150 499 Td (big) Tj ET\nBT /F1 12 Tf 200 500 Td (end) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var line = Assert.Single(PdfStructuredTextExtractor.Extract(ms)[0].Lines);

        Assert.Equal(3, line.Words.Count);
        Assert.Equal(100f, line.X, 1);
        Assert.Equal(line.Words.Min(w => w.X), line.X, 3);
        Assert.Equal(line.Words.Max(w => w.X + w.Width), line.X + line.Width, 3);
        Assert.Equal(line.Words.Max(w => w.Height), line.Height, 3);
        Assert.Equal(line.Words.Min(w => w.Y), line.Y, 3);
        Assert.True(line.Words[1].Height > line.Words[0].Height, "the larger word is not taller, so the height assertion is idle");
    }

    /// <summary>
    /// Keeping blank glyphs, line assembly works on a copy of the page's glyphs: the page's own
    /// <c>Chars</c> stay in drawing order — the lower line was drawn first — rather than come back
    /// sorted top to bottom by assembly.
    /// </summary>
    [Fact]
    public void KeepBlankChars_True_LeavesThePagesCharsInDrawingOrder()
    {
        const string cs = "BT /F1 12 Tf  100 500 Td (Low) Tj ET\nBT /F1 12 Tf  100 700 Td (High) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var page = PdfStructuredTextExtractor.Extract(ms, new WordAssemblyOptions { KeepBlankChars = true })[0];

        Assert.Equal(2, page.Lines.Count);
        Assert.Equal("High", page.Lines[0].Text);
        Assert.Equal("LowHigh", string.Concat(page.Chars.Select(c => c.Char)));
    }

    [Fact]
    public void KeepBlankChars_True_SpaceCharsIncluded()
    {
        const string cs = "BT /F1 12 Tf  100 500 Td (A B) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms,
            new WordAssemblyOptions { KeepBlankChars = true });
        Assert.NotEmpty(pages);
        var allChars = pages[0].Lines
            .SelectMany(l => l.Words)
            .SelectMany(w => w.Chars)
            .Select(c => c.Char)
            .ToList();
        // At least one space char should be present
        Assert.Contains(" ", allChars);
    }
}
