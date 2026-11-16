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
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfPageTextWriter"/>: text drawn on an existing page — read back as the text typed,
/// drawn in the order and at the place its direction and the page's turn give it, in a font
/// embedded for it, and refused whole when it cannot be drawn whole.
/// </summary>
/// <remarks>
/// The font is the tests' own, handed over through the provider, so no machine's fonts decide an
/// answer; its glyph ids follow its characters' code points, which is what the drawing-order
/// assertions read.
/// </remarks>
public class PageTextWriterTests
{
    private const string Hebrew = "שלום";
    private const string Letters = "Helo Wrdabcשלום";

    private static byte[] Page(string rotate = "", string resources = "/Font << /F1 4 0 R >>", string content = "BT /F1 12 Tf 10 180 Td (Before) Tj ET")
    {
        using var ms = new MemoryStream();
        void Text(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        Text("%PDF-1.7\n");
        var offsets = new List<long>();
        void Object(string body) { offsets.Add(ms.Position); Text($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] {rotate} /Resources << {resources} >> /Contents 5 0 R >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Object($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        long xref = ms.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Text(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static PageTextOptions Options(string text, double x = 40, double y = 100) => new()
    {
        Text = text, X = x, Y = y, FontSize = 10,
        FontProvider = _ => MinimalTrueType.BuildWithDistinctWidths(Letters),
    };

    private static (byte[] Pdf, PageTextResult Result) Add(byte[] pdf, PageTextOptions options)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PageTextResult result = PdfPageTextWriter.AddText(input, output, 0, options);
        return (output.ToArray(), result);
    }

    /// <summary>The last content stream of the page, decoded: the one the text was appended in.</summary>
    private static string Appended(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var contents = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]));
        var last = Assert.IsType<PdfStream>(doc.ResolveObject(contents[contents.Count - 1]));
        return Encoding.Latin1.GetString(last.GetDecodedData());
    }

    private static string TextOfPage(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var text = new StringBuilder();
        PdfTextExtractor.ExtractText(doc, (_, pageText) => text.Append(pageText));
        return text.ToString();
    }

    /// <summary>The glyph ids of the appended stream's show strings, in the order they are written.</summary>
    private static int[][] ShownGlyphs(string content)
        => Regex.Matches(content, @"<([0-9A-Fa-f]+)>", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => Enumerable.Range(0, m.Groups[1].Value.Length / 4)
                .Select(i => int.Parse(m.Groups[1].Value.Substring(i * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();

    private static double[][] TextMatrices(string content)
        => Regex.Matches(content, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => Enumerable.Range(1, 6).Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();

    // ── what is drawn ───────────────────────────────────────────────────────

    [Fact]
    public void TheTextIsThePagesOwnAfterwards_ReadBackAsTyped_BesideWhatWasThere()
    {
        var (pdf, result) = Add(Page(), Options("Hello World"));

        string text = TextOfPage(pdf);
        Assert.Contains("Hello World", text, StringComparison.Ordinal);
        Assert.Contains("Before", text, StringComparison.Ordinal);
        Assert.Equal(1, result.Lines);
        Assert.False(result.FontFromSystem);

        // It is listed as an element like any other, where it was put.
        using var listed = new MemoryStream(pdf, writable: false);
        PdfPageElement added = PdfOperations.ListPageElements(listed, 0).Last(e => e.Kind == PdfPageElementKind.Text);
        Assert.Equal("Hello World", added.Text);
        Assert.InRange(added.X, 30, 41);
        Assert.InRange(added.Y, 90, 101);
    }

    /// <summary>
    /// A left-to-right line is written in the order typed; a right-to-left one in the order it is
    /// seen, its first letter last. Reading it back cannot tell the two apart, so the glyphs are read.
    /// </summary>
    [Fact]
    public void ARightToLeftLine_IsDrawnInTheOrderItIsSeen_AndReadBackInTheOrderTyped()
    {
        string latin = Appended(Add(Page(), Options("abc")).Pdf);
        var (pdf, _) = Add(Page(), Options(Hebrew));
        string hebrew = Appended(pdf);

        int[] abc = Assert.Single(ShownGlyphs(latin));
        int[] shalom = Assert.Single(ShownGlyphs(hebrew));
        // The test font numbers its glyphs by code point, from 1.
        int Glyph(char c) => Letters.Distinct().OrderBy(l => (int)l).ToList().IndexOf(c) + 1;
        Assert.Equal("abc".Select(Glyph), abc);
        Assert.Equal(Hebrew.Reverse().Select(Glyph), shalom);
        // Potency: the two orders differ, so a line written as typed would be seen.
        Assert.NotEqual(Hebrew.Select(Glyph), shalom);

        Assert.Contains(Hebrew, TextOfPage(pdf), StringComparison.Ordinal);
    }

    /// <summary>The point given is where the pen goes down: a Latin line runs right from it, a Hebrew line left.</summary>
    [Fact]
    public void ALineStartsAtThePointGiven_AsItIsRead()
    {
        PageTextResult latin = Add(Page(), Options("abc", x: 100)).Result;
        PageTextResult hebrew = Add(Page(), Options(Hebrew, x: 100)).Result;

        Assert.Equal(100, latin.X, 6);
        Assert.True(latin.Width > 5, "the Latin line has no width");
        Assert.Equal(100, hebrew.X + hebrew.Width, 6);
        Assert.True(hebrew.Width > 5, "the Hebrew line has no width");
        // The matrix puts the Hebrew line's first glyph at its left end.
        Assert.Equal(hebrew.X, Assert.Single(TextMatrices(Appended(Add(Page(), Options(Hebrew, x: 100)).Pdf)))[4], 3);
    }

    [Fact]
    public void LinesGoDownThePage_ABlankOneLeavingItsRoom_AndAFinalBreakStartingNone()
    {
        PageTextOptions options = Options("abc\n\nabc\r\nabc\n", y: 200);
        options.LineSpacing = 1.5;

        var (pdf, result) = Add(Page(), options);

        Assert.Equal(4, result.Lines);
        double[][] matrices = TextMatrices(Appended(pdf));
        Assert.Equal(new[] { 200.0, 170.0, 155.0 }, matrices.Select(m => m[5]));
        Assert.All(matrices, m => Assert.Equal(40, m[4]));
        // The box runs from the last line's descent to the first line's ascent.
        Assert.InRange(result.Y, 140, 154.5);
        Assert.InRange(result.Y + result.Height, 205, 215);
    }

    /// <summary>
    /// On a page shown turned the lines run across the page the reader sees: a quarter turn
    /// clockwise shows the page's own upward direction running to the right.
    /// </summary>
    [Theory]
    [InlineData("", 1, 0, 0, 1, 40, 88)]
    [InlineData("/Rotate 90", 0, 1, -1, 0, 52, 100)]
    [InlineData("/Rotate 180", -1, 0, 0, -1, 40, 112)]
    [InlineData("/Rotate 270", 0, -1, 1, 0, 28, 100)]
    public void OnAPageShownTurned_TheTextIsUprightAsShown(string rotate, double a, double b, double c, double d, double secondX, double secondY)
    {
        double[][] matrices = TextMatrices(Appended(Add(Page(rotate), Options("abc\nabc")).Pdf));

        Assert.Equal(2, matrices.Length);
        Assert.Equal(new[] { a, b, c, d, 40, 100 }, matrices[0]);
        // The second line is one leading (12) below the first, as shown.
        Assert.Equal(new[] { a, b, c, d, secondX, secondY }, matrices[1]);
    }

    /// <summary>On a page shown turned a quarter clockwise the line runs up the page's own y axis, so a right-to-left line starts that far below the point.</summary>
    [Fact]
    public void ARightToLeftLine_OnAPageShownTurned_RunsBackAlongThePageAsShown()
    {
        var (pdf, result) = Add(Page("/Rotate 90"), Options(Hebrew, x: 40, y: 100));

        double[] matrix = Assert.Single(TextMatrices(Appended(pdf)));
        Assert.Equal(40, matrix[4], 3);
        Assert.True(result.Height > 5, "the line has no length along the page's y axis");
        Assert.Equal(100 - result.Height, matrix[5], 3);
        // The line ends at the point given.
        Assert.Equal(100, result.Y + result.Height, 6);
    }

    /// <summary>Under a point that is the line's top, the text hangs below it as shown; under a baseline, it stands on it.</summary>
    [Theory]
    [InlineData("", 40, 100)]
    [InlineData("/Rotate 90", 40, 100)]
    public void WithThePointAsTheLinesTop_TheTextHangsBelowIt_AsShown(string rotate, double x, double y)
    {
        PageTextOptions top = Options("abc", x, y);
        top.Anchor = PageTextAnchor.Top;

        PageTextResult hung = Add(Page(rotate), top).Result;
        PageTextResult standing = Add(Page(rotate), Options("abc", x, y)).Result;

        if (rotate.Length == 0)
        {
            // Upright: the box's top is the point; a baseline's box reaches above it.
            Assert.Equal(y, hung.Y + hung.Height, 6);
            Assert.True(standing.Y + standing.Height > y + 1, "the baseline's box does not reach above the point");
        }
        else
        {
            // A quarter turn clockwise shows the page's own left as the top: the box's left is the point.
            Assert.Equal(x, hung.X, 6);
            Assert.True(standing.X < x - 1, "the baseline's box does not reach above the point as shown");
        }
        Assert.Equal(standing.Width, hung.Width, 6);
    }

    /// <summary>
    /// The box's height is the font's own ascent and descent at the size asked, whatever units the
    /// font is drawn in: a font of 2048 units to the em — what most real fonts have, and the test
    /// font does not — is the case a division by the wrong scale gets wrong by half.
    /// </summary>
    [Fact]
    public void TheBoxIsTheFontsAscentAndDescentAtTheSizeAsked_WhateverTheFontsOwnUnits()
    {
        byte[] font = MinimalTrueType.BuildWithDistinctWidths(Letters);
        (int headAt, _) = Table(font, "head");
        (int hheaAt, _) = Table(font, "hhea");
        font[headAt + 18] = 0x08;      // unitsPerEm: 2048
        font[headAt + 19] = 0x00;
        int ascender = (short)((font[hheaAt + 4] << 8) | font[hheaAt + 5]), descender = (short)((font[hheaAt + 6] << 8) | font[hheaAt + 7]);
        Assert.True(ascender > 0 && descender < 0, $"the test font's ascender {ascender} and descender {descender} are not ones");
        PageTextOptions options = Options("abc", x: 40, y: 100);
        options.FontSize = 20;
        options.FontProvider = _ => font;

        PageTextResult result = Add(Page(), options).Result;

        Assert.Equal(100 + ascender / 2048.0 * 20, result.Y + result.Height, 1);
        Assert.Equal(100 + descender / 2048.0 * 20, result.Y, 1);
    }

    /// <summary>Where the table <paramref name="tag"/> of an sfnt lies, from its directory.</summary>
    private static (int Offset, int Length) Table(byte[] font, string tag)
    {
        int tables = (font[4] << 8) | font[5];
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, record, 4) != tag) continue;
            int Read(int at) => (font[at] << 24) | (font[at + 1] << 16) | (font[at + 2] << 8) | font[at + 3];
            return (Read(record + 8), Read(record + 12));
        }
        throw new Xunit.Sdk.XunitException($"the test font has no {tag} table");
    }

    [Fact]
    public void WithUprightAsShownOff_TheBaselineIsThePagesOwnXAxis()
    {
        PageTextOptions options = Options("abc");
        options.UprightAsShown = false;

        Assert.Equal(new[] { 1.0, 0, 0, 1, 40, 100 }, Assert.Single(TextMatrices(Appended(Add(Page("/Rotate 90"), options).Pdf))));
    }

    [Fact]
    public void TheColourIsSet_ClampedToWhatAColourCanBe_AndTheSizeIsTheOneAsked()
    {
        PageTextOptions options = Options("abc");
        options.Red = 2;
        options.Green = 0.5;
        options.Blue = -1;
        options.FontSize = 17;

        string content = Appended(Add(Page(), options).Pdf);

        Assert.Matches(@"(?m)^1 0\.5 0 rg$", content);
        Assert.Matches(@"/TF1 17 Tf", content);
    }

    [Fact]
    public void ATab_IsDrawnAsASpace()
    {
        var (pdf, _) = Add(Page(), Options("ab\tc"));

        int Glyph(char c) => Letters.Distinct().OrderBy(l => (int)l).ToList().IndexOf(c) + 1;
        Assert.Equal("ab c".Select(Glyph), Assert.Single(ShownGlyphs(Appended(pdf))));
    }

    /// <summary>A direction said is the line's, whatever its letters: a Latin line laid out right to left ends at the point given, its letters still in their order.</summary>
    [Fact]
    public void ADirectionSaid_IsTheLines()
    {
        PageTextOptions options = Options("abc", x: 100);
        options.Direction = BaseDirection.RightToLeft;

        var (pdf, result) = Add(Page(), options);

        Assert.Equal(100, result.X + result.Width, 6);
        int Glyph(char c) => Letters.Distinct().OrderBy(l => (int)l).ToList().IndexOf(c) + 1;
        Assert.Equal("abc".Select(Glyph), Assert.Single(ShownGlyphs(Appended(pdf))));
        // The control: with no direction said, the same line starts there.
        Assert.Equal(100, Add(Page(), Options("abc", x: 100)).Result.X, 6);
    }

    /// <summary>The text's own colour and font are its own: what it sets is put back before anything appended after it.</summary>
    [Fact]
    public void TheTextIsBracketed_SoWhatItSetsDoesNotOutliveIt()
    {
        string content = Appended(Add(Page(), Options("abc")).Pdf).Trim();

        Assert.StartsWith("q\n", content, StringComparison.Ordinal);
        Assert.EndsWith("ET\nQ", content, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Matches(content, @"(?m)^BT$", RegexOptions.None, TimeSpan.FromSeconds(1)).Count);
    }

    // ── the file ────────────────────────────────────────────────────────────

    [Fact]
    public void TheFontIsEmbedded_UnderANameThePageDoesNotUse()
    {
        byte[] page = Page(resources: "/Font << /F1 4 0 R /TF1 4 0 R >>");

        var (pdf, _) = Add(page, Options("abc"));

        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]))[PdfName.Font]));
        // The page's own TF1 is still the page's Helvetica.
        Assert.Equal("Helvetica", (doc.ResolveObject(Assert.IsType<PdfDictionary>(doc.ResolveObject(fonts[new PdfName("TF1")]))[PdfName.BaseFont]) as PdfName)?.Value);
        var added = Assert.IsType<PdfDictionary>(doc.ResolveObject(fonts[new PdfName("TF2")]));
        Assert.Equal("Type0", (doc.ResolveObject(added[PdfName.Subtype]) as PdfName)?.Value);
        Assert.NotNull(doc.ResolveObject(added[PdfName.ToUnicode]));
        Assert.Contains("/TF2 10 Tf", Appended(pdf), StringComparison.Ordinal);
        string file = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/FontFile2", file, StringComparison.Ordinal);
    }

    /// <summary>A revision is appended: what was in the file is there byte for byte, so a signature over it covers what it covered.</summary>
    [Fact]
    public void TheFileAsItWas_IsThePrefixOfTheFileWritten()
    {
        byte[] page = Page();

        byte[] pdf = Add(page, Options("abc")).Pdf;

        Assert.True(pdf.Length > page.Length);
        Assert.Equal(page, pdf.Take(page.Length).ToArray());
    }

    // ── refusals: nothing is written ────────────────────────────────────────

    [Fact]
    public void WithNoFontToEmbed_NothingIsAdded_AndTheRefusalSaysHowToSupplyOne()
    {
        using var input = new MemoryStream(Page(), writable: false);
        using var output = new MemoryStream();

        PdfException refused = Assert.Throws<PdfException>(() => PdfPageTextWriter.AddText(input, output, 0,
            new PageTextOptions { Text = "abc", X = 40, Y = 100 }));

        Assert.Contains("FontProvider", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ACharacterTheFontCannotDraw_RefusesTheWholeText_NamingIt()
    {
        using var input = new MemoryStream(Page(), writable: false);
        using var output = new MemoryStream();

        PdfException refused = Assert.Throws<PdfException>(() => PdfPageTextWriter.AddText(input, output, 0, Options("abcz")));

        Assert.Contains("U+007A", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
        // The control: without the letter the font lacks, the same call writes.
        Assert.Contains("abc", TextOfPage(Add(Page(), Options("abc")).Pdf), StringComparison.Ordinal);
    }

    /// <summary>
    /// The font search does not ask for a space, so a font without one is found — and the line is
    /// still refused whole when it is shaped, not written with its words run together.
    /// </summary>
    [Fact]
    public void AFontWithNoSpace_RefusesALineWithOne_WhenTheLineIsShaped()
    {
        var options = new PageTextOptions { Text = "a b", X = 40, Y = 100, FontProvider = _ => MinimalTrueType.Build("ab") };
        using var input = new MemoryStream(Page(), writable: false);
        using var output = new MemoryStream();

        PdfException refused = Assert.Throws<PdfException>(() => PdfPageTextWriter.AddText(input, output, 0, options));

        Assert.Contains("U+0020", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
        // The control: the same font draws the letters without the space.
        options.Text = "ab";
        Assert.Contains("ab", TextOfPage(Add(Page(), options).Pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedDocument_IsRefused_BeforeAFontIsSought()
    {
        PdfEncryptionOperations.Register();
        byte[] encrypted;
        using (var plain = new MemoryStream(Page()))
        using (var locked = new MemoryStream())
        {
            PdfEncryptor.Encrypt(plain, locked, new EncryptionParameters
            {
                UserPassword = "s3cret", OwnerPassword = "owner-only", Algorithm = EncryptionAlgorithm.AES128, Permissions = PdfPermissions.All,
            });
            encrypted = locked.ToArray();
        }
        int asked = 0;
        PageTextOptions options = Options("abc");
        options.FontProvider = _ => { asked++; return MinimalTrueType.Build(Letters); };
        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();

        Assert.Throws<PdfEncryptedDocumentException>(() => PdfPageTextWriter.AddText(input, output, 0, options));

        Assert.Equal(0, asked);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n \t ")]
    public void TextWithNothingToDraw_IsRefused(string text)
    {
        using var input = new MemoryStream(Page(), writable: false);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => PdfPageTextWriter.AddText(input, output, 0, Options(text)));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void TextLongerThanAPageHolds_IsRefused_BeforeAnyFontIsSought()
    {
        int asked = 0;
        var options = new PageTextOptions
        {
            Text = new string('a', PdfPageTextWriter.MaxTextLength + 1), X = 40, Y = 100,
            FontProvider = _ => { asked++; return MinimalTrueType.Build(Letters); },
        };
        using var input = new MemoryStream(Page(), writable: false);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => PdfPageTextWriter.AddText(input, output, 0, options));
        Assert.Equal(0, asked);

        // The control: at the limit the text is taken, and the font is asked for.
        options.Text = new string('a', PdfPageTextWriter.MaxTextLength);
        using var again = new MemoryStream(Page(), writable: false);
        PdfPageTextWriter.AddText(again, output, 0, options);
        Assert.True(asked > 0);
        Assert.True(output.Length > 0);
    }

    [Theory]
    [InlineData(double.NaN, 100, 10, 1.2, 0)]
    [InlineData(40, double.PositiveInfinity, 10, 1.2, 0)]
    [InlineData(40, 100, 0, 1.2, 0)]
    [InlineData(40, 100, double.NaN, 1.2, 0)]
    [InlineData(40, 100, 10, 0, 0)]
    [InlineData(40, 100, 10, 1.2, double.NaN)]
    public void ANumberThatIsNotOne_IsRefused_NamingTheOption(double x, double y, double size, double spacing, double red)
    {
        PageTextOptions options = Options("abc", x, y);
        options.FontSize = size;
        options.LineSpacing = spacing;
        options.Red = red;
        using var input = new MemoryStream(Page(), writable: false);
        using var output = new MemoryStream();

        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => PdfPageTextWriter.AddText(input, output, 0, options));

        Assert.Equal("options", refused.ParamName);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void APageTheDocumentDoesNotHave_IsRefused()
    {
        using var input = new MemoryStream(Page(), writable: false);
        using var output = new MemoryStream();

        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => PdfPageTextWriter.AddText(input, output, 1, Options("abc")));

        // The refusal is this method's, naming its own parameter, not an index error from inside it.
        Assert.Equal("pageIndex", refused.ParamName);
        Assert.Equal(0, output.Length);
    }
}
