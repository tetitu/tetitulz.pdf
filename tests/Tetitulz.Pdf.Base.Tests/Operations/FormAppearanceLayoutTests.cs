// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="FormAppearance"/>'s own layout decisions — the ones a mutation sweep of
/// <c>Base/Operations/Forms</c> found nothing could see: the clip, the baseline rule, comb cells and
/// their quadding, the auto-size searches, and how a string is measured.
/// </summary>
/// <remarks>
/// <para><b>Called directly rather than through a filled document.</b> Every one of these is a
/// decision about a content stream, and driving them through <c>PdfFormFiller</c> would mean a PDF
/// fixture per case whose real subject is two numbers in a <c>Td</c>. The filler's own suite already
/// proves this generator is what it reaches.</para>
/// <para>No document is needed for the single-byte path: with no <c>/DR</c> to consult, the generator
/// synthesises a Standard-14 font, which is the case these assert against.</para>
/// </remarks>
public class FormAppearanceLayoutTests
{
    // ================================================================
    //  /DA parsing
    // ================================================================

    [Fact]
    public void ADefaultAppearanceNamingNoFontIsNotADefaultAppearance()
    {
        // A /DA is only usable if it names a font: "/Helv 0 Tf" with the slash missing is not a
        // resource name, and a colour alone says nothing about which font to draw with.
        Assert.Null(FormAppearance.ParseDefaultAppearance("Helv 12 Tf"));
        Assert.Null(FormAppearance.ParseDefaultAppearance("0 g"));
        Assert.Null(FormAppearance.ParseDefaultAppearance(""));
    }

    [Fact]
    public void ADefaultAppearanceIsReadForItsFontSizeAndColour()
    {
        var da = FormAppearance.ParseDefaultAppearance("/Helv 9 Tf 0 0 1 rg");

        Assert.NotNull(da);
        Assert.Equal("Helv", da!.FontResourceName);
        Assert.Equal(9f, da.FontSize);
        Assert.Equal("0 0 1 rg", da.ColorOperators);
    }

    // ================================================================
    //  The stream itself
    // ================================================================

    [Fact]
    public void TheAppearanceClipsToTheWidgetBox()
    {
        // Without the clip a value too long for its widget draws past it, over whatever the page
        // has beside the field.
        Assert.Matches(@"0 0 200\.00 20\.00 re W n", Content("Ada"));
    }

    [Fact]
    public void TheDefaultAppearanceColourIsUsedWhenThereIsOne()
    {
        Assert.Contains("0 0 1 rg", Content("Ada", da: Da(size: 10, colour: "0 0 1 rg")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 5.0)]    // single line: centred in the box, (20 - 12) / 2 + 1
    [InlineData(true, 46.0)]    // multiline: from the top, 60 - 2 - 12
    public void TheFirstBaselineIsCentredOnOneLineAndUnderTheTopOnSeveral(bool multiline, double expected)
    {
        string content = Content("Ada", height: multiline ? 60 : 20,
            layout: new FieldLayout { Multiline = multiline }, da: Da(size: 12));

        Assert.Equal(expected, FirstTd(content).Y, precision: 2);
    }

    // ================================================================
    //  Quadding
    // ================================================================

    [Theory]
    [InlineData(PdfQuadding.Left, 2.0)]
    [InlineData(PdfQuadding.Centred, 74.69)]   // 2 + (196 − 50.616) / 2
    [InlineData(PdfQuadding.Right, 147.38)]    // 2 + (196 − 50.616)
    public void AShortValueIsPlacedByItsQuadding(int quadding, double expected)
    {
        // 19 Helvetica i's at 222/1000 em, 12 pt: 50.616 pt inside a 196 pt box — a width this test
        // can state exactly.
        string content = Content(Narrow, layout: new FieldLayout { Quadding = quadding }, da: Da(size: 12));

        Assert.Equal(expected, FirstTd(content).X, precision: 1);
    }

    [Fact]
    public void AValueWiderThanItsBoxStartsAtTheLeftMarginWhateverTheQuadding()
    {
        // Right-quadding a string wider than the box would place it at a negative x — off the left
        // edge of the widget, with its end cut off instead of its beginning.
        string content = Content(new string('W', 60), layout: new FieldLayout { Quadding = PdfQuadding.Right },
            da: Da(size: 12));

        Assert.Equal(2.0, FirstTd(content).X, precision: 2);
    }

    // ================================================================
    //  Comb fields (§12.7.4.3)
    // ================================================================

    [Fact]
    public void ACombDrawsOneCharacterPerCellAndNoMoreThanItHasCells()
    {
        // Seven characters, five cells: the value is drawn up to the cell count. The filler reports
        // the over-long value separately; what must not happen is a sixth cell that does not exist.
        string content = Content("ABCDEFG", layout: Comb(5), da: Da(size: 8));

        Assert.Equal(5, ShowCount(content));
    }

    [Theory]
    [InlineData(PdfQuadding.Left, 16.22)]      // first cell: (0 + 0.5) × 40 − half the glyph
    [InlineData(PdfQuadding.Centred, 56.22)]   // one empty cell either side of three glyphs
    [InlineData(PdfQuadding.Right, 96.22)]     // the last three cells
    public void ACombShorterThanItsCellsIsPlacedByItsQuadding(int quadding, double expected)
    {
        // Five 40 pt cells, three characters. 'W' is 944/1000 em, so at 8 pt each glyph is 7.552 pt
        // and the centring subtracts 3.776.
        string content = Content("WWW", layout: Comb(5, quadding), da: Da(size: 8));

        Assert.Equal(expected, FirstTd(content).X, precision: 1);
    }

    /// <summary>
    /// §12.7.4.3 puts one <i>character</i> in each comb cell. A supplementary-plane character is
    /// one character in two UTF-16 code units, so a cell walk by code unit spends two cells on it —
    /// and spends them on two lone surrogates, which no cmap maps and no font can draw.
    /// </summary>
    /// <remarks>
    /// Takes the composite path, which is the only one that can reach this: a value outside the
    /// single-byte encoding is what an astral character is.
    /// </remarks>
    [Fact]
    public void AnAstralCharacterFillsOneCombCellRatherThanTwo()
    {
        string value = char.ConvertFromUtf32(0x1D400) + "B";   // MATHEMATICAL BOLD CAPITAL A, then a letter
        var options = new FormFillOptions { FontProvider = _ => MinimalTrueType.Build(value) };

        int next = 1;
        var objects = new List<IndirectObject>();
        var ap = FormAppearance.Build(value, 200, 20, 0, Da(size: 8), Comb(4), null, null,
            options, ref next, objects, out string error, out _);

        Assert.True(ap != null, "no appearance was built: " + error);
        // Two cells drawn, each one show operation: the pair is one of them, not two.
        Assert.Equal(2, Regex.Matches(Encoding.Latin1.GetString(ap!.RawData), "> Tj").Count);
    }

    [Fact]
    public void ACombCellSetsTheFontSizeWhenTheSizeIsAuto()
    {
        // A character has to fit its cell, so the cell width is the ceiling however tall the widget
        // is: 20 pt of width over 2 cells is 10, well under the height-derived 0.6 × 40.
        string content = Content("AB", width: 20, height: 40, layout: Comb(2));

        Assert.Equal(10.0, FontSize(content), precision: 2);
    }

    // ================================================================
    //  Auto-sizing
    // ================================================================

    [Fact]
    public void ASingleLineValueIsShrunkUntilItFitsTheWidth()
    {
        // 19 i's are 4.218 em, so they fit a 400 pt box at the height-derived 12 pt and do not fit a
        // 40 pt one, where the size has to come down to 36 / 4.218.
        string wide = Content(Narrow, width: 400);
        string narrow = Content(Narrow, width: 40);

        Assert.True(FontSize(narrow) < FontSize(wide),
            $"a narrower box must shrink the value: {FontSize(narrow)} against {FontSize(wide)}");
        Assert.True(FontSize(narrow) * 19 * 0.222 <= 36.01, "…and it must actually fit");
    }

    [Fact]
    public void AMultilineBlockIsShrunkUntilItsLinesFitTheHeight()
    {
        // Both in the SAME box, so the starting size is the same and only the search can explain the
        // difference. Comparing a tall box with a shallow one does not: their starting sizes already
        // differ by the height rule, and the assertion then passes with the search disabled — which
        // is how this test first failed to kill its own mutation.
        var layout = new FieldLayout { Multiline = true };
        string oneLine = Content("Ada", width: 200, height: 40, layout: layout);
        string many = Content(Paragraph, width: 200, height: 40, layout: layout);

        Assert.True(FontSize(many) < FontSize(oneLine),
            $"a block of several lines must be shrunk to fit: {FontSize(many)} against {FontSize(oneLine)}");
        // …and it fits: every line drawn, at 1.15 line spacing, inside the box less its margins.
        Assert.True(ShowCount(many) * FontSize(many) * 1.15 <= 40 - 4 + 0.01,
            "the shrunk block still does not fit its box");
    }

    [Fact]
    public void AWordTooWideForTheBoxIsBrokenAcrossLinesRatherThanOverflowing()
    {
        // One word, no space to wrap at. Breaking it by character is what keeps the tail of the
        // value visible; the alternative hides everything past the right edge.
        string content = Content(new string('M', 80), width: 60, height: 200,
            layout: new FieldLayout { Multiline = true }, da: Da(size: 10));

        Assert.True(ShowCount(content) > 1, "the long word should have been broken: " + content);
    }

    [Fact]
    public void LinesPastTheBottomOfTheBoxAreReportedAsNotDrawn()
    {
        // At the minimum size the block still does not fit, so the tail is not drawn — and the
        // caller is told rather than shipping a document with a silently truncated value.
        Build(string.Join("\n", Enumerable.Repeat(Paragraph, 40)), 100, 20,
            new FieldLayout { Multiline = true }, Da(size: 6), out _, out string warning);

        Assert.Contains("not drawn", warning ?? "", StringComparison.Ordinal);
    }

    // ================================================================
    //  Measurement and encoding
    // ================================================================

    [Fact]
    public void AValueOutsideTheSingleByteRangeIsRefusedRatherThanDrawnAsAnotherGlyph()
    {
        // U+0085 is a C1 control: WinAnsiEncoding gives byte 0x85 to an ellipsis, so passing the raw
        // byte through would draw one. Nothing covers it, so the appearance is refused with a reason.
        var ap = Build(((char)0x85).ToString(), 200, 20, null, null, out string error, out _);

        Assert.Null(ap);
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ================================================================
    //  The font the /DA names, out of the form's /DR
    // ================================================================

    [Fact]
    public void TheValueIsMeasuredWithTheFontTheFormActuallyDefines()
    {
        // /Helv here means Courier, whose every glyph is 600/1000 em against Helvetica's 222 for an
        // 'i'. Measured as Helvetica, a right-quadded value is placed 18 pt too far right — a field
        // that looks correct until it is compared with the rest of the form.
        string content = WithForm("iiii", "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            new FieldLayout { Quadding = PdfQuadding.Right });

        Assert.Equal(2 + 196 - 4 * 0.6 * 12, FirstTd(content).X, precision: 1);
    }

    [Fact]
    public void ACompositeResourceFontIsRefusedAndAStandardOneIsSynthesised()
    {
        // A Type0 font's codes are not the single bytes this path emits, so reusing it draws the
        // wrong glyphs. The refusal falls back to a synthesised Standard-14, named F1.
        string content = WithForm("Ada",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-H >>", null);

        Assert.Contains("/F1 ", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/Helv ", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFontsOwnWidthsAreReadFromItsFirstCharOnwards()
    {
        // /FirstChar 65 with three widths describes A, B and C. Indexed by the raw character code
        // the lookup falls off the end of the array and silently measures with the Standard-14
        // metrics instead — a different width for every glyph.
        string content = WithForm("AAA",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /Widths [1000 1000 1000] >>",
            new FieldLayout { Quadding = PdfQuadding.Right });

        Assert.Equal(2 + 196 - 3 * 1.0 * 12, FirstTd(content).X, precision: 1);
    }

    [Fact]
    public void TheFontProviderIsAskedForTheFontTheDefaultAppearanceNames()
    {
        // The /DA's resource name is the hint: a form that says /Arial must not have its provider
        // asked for Helvetica, which is the name it would have been given by default.
        string asked = null;
        var options = new FormFillOptions
        {
            FontProvider = name => { asked = name; return null; },
            AllowSystemFontSubstitution = true,
        };

        int next = 1;
        var objects = new List<IndirectObject>();
        FormAppearance.Build(Hebrew, 200, 20, 0,
            new DefaultAppearance { FontResourceName = "Arial", FontSize = 10 }, null, null, null,
            options, ref next, objects, out _, out _);

        Assert.Equal("Arial", asked);
    }

    [Fact]
    public void AValueThatIsBothSubstitutedAndTruncatedReportsBoth()
    {
        // Two things went wrong with one value, and which of them the caller heard about used to
        // depend on the order the code happened to be in.
        var options = new FormFillOptions
        {
            FontProvider = _ => null,               // consulted, supplies nothing
            AllowSystemFontSubstitution = true,     // …so a host font is embedded instead
        };

        int next = 1;
        var objects = new List<IndirectObject>();
        var ap = FormAppearance.Build(string.Join(" ", Enumerable.Repeat(Hebrew, 60)), 80, 16, 0,
            new DefaultAppearance { FontResourceName = "Arial", FontSize = 6 },
            new FieldLayout { Multiline = true }, null, null, options, ref next, objects,
            out string error, out string warning);

        Assert.True(ap != null, "no appearance was built: " + error);
        Assert.Contains("not the one supplied", warning ?? "", StringComparison.Ordinal);
        Assert.Contains("not drawn", warning ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void AFontCoveringTheLettersButNotTheirShapedFormsIsNotTheFontChosen()
    {
        // The provider supplies a font built over the Arabic letters themselves. Shaping substitutes
        // presentation forms, which that font has no glyph for — so it cannot draw a single glyph of
        // the run. Asked about the letters, the resolver used to accept it and the appearance was
        // then refused; asked about the shaped forms, it falls through to a host font that can.
        var options = new FormFillOptions
        {
            FontProvider = _ => MinimalTrueType.Build(Arabic),
            AllowSystemFontSubstitution = true,
        };

        int next = 1;
        var objects = new List<IndirectObject>();
        var ap = FormAppearance.Build(Arabic, 200, 20, 0, Da(size: 12), null, null, null,
            options, ref next, objects, out string error, out string warning);

        Assert.True(ap != null, "no appearance was built: " + error);
        Assert.Contains("not the one supplied", warning ?? "", StringComparison.Ordinal);
    }

    /// <summary>بيت — every letter joins, so every one of them is substituted before a glyph is chosen.</summary>
    private const string Arabic = "بيت";

    /// <summary>שלום — needs an embedded font, so it takes the composite path.</summary>
    private const string Hebrew = "שלום";

    /// <summary>
    /// Builds against a document whose <c>/AcroForm</c> <c>/DR</c> defines <c>/Helv</c> as
    /// <paramref name="fontBody"/>, which is what the <c>/DA</c> below names.
    /// </summary>
    private static string WithForm(string text, string fontBody, FieldLayout layout)
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Fields [] /DR << /Font << /Helv 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            fontBody);

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(pdf));
        int next = 20;
        var objects = new List<IndirectObject>();
        var ap = FormAppearance.Build(text, 200, 20, 0, Da(size: 12), layout, doc,
            doc.AcroForm.Dictionary, new FormFillOptions(), ref next, objects, out string error, out _);
        Assert.True(ap != null, "no appearance was built: " + error);
        return Encoding.Latin1.GetString(ap!.RawData);
    }

    // ================================================================
    //  Helpers
    // ================================================================

    /// <summary>Nineteen Helvetica <c>i</c>'s: 19 × 222/1000 em, so 99 pt at 12 pt.</summary>
    private const string Narrow = "iiiiiiiiiiiiiiiiiii";

    private const string Paragraph = "The quick brown fox jumps over the lazy dog again and again";

    private static DefaultAppearance Da(float size, string colour = null) =>
        new DefaultAppearance
        {
            FontResourceName = "Helv",
            FontSize = size,
            ColorOperators = colour ?? "0 g",
        };

    private static FieldLayout Comb(int cells, int quadding = PdfQuadding.Left) =>
        new FieldLayout { Comb = true, MaxLength = cells, Quadding = quadding };

    private static PdfStream Build(string text, double width, double height, FieldLayout layout,
                                   DefaultAppearance da, out string error, out string warning)
    {
        int next = 1;
        var objects = new List<IndirectObject>();
        return FormAppearance.Build(text, width, height, 0, da, layout, null, null,
            new FormFillOptions(), ref next, objects, out error, out warning);
    }

    private static string Content(string text, double width = 200, double height = 20,
                                  FieldLayout layout = null, DefaultAppearance da = null)
    {
        var ap = Build(text, width, height, layout, da, out string error, out _);
        Assert.True(ap != null, "no appearance was built: " + error);
        return Encoding.Latin1.GetString(ap!.RawData);
    }

    /// <summary>The first <c>Td</c>, which is the first line's or the first cell's origin.</summary>
    private static (double X, double Y) FirstTd(string content)
    {
        var m = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) Td");
        Assert.True(m.Success, "no Td in: " + content);
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static double FontSize(string content)
    {
        var m = Regex.Match(content, @"/\w+ (-?[\d.]+) Tf");
        Assert.True(m.Success, "no Tf in: " + content);
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static int ShowCount(string content) => Regex.Matches(content, @"\) Tj").Count;
}
