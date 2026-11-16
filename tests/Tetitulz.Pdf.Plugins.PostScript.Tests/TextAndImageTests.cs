// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Text and image conversion (S5 / S6), asserted on the emitted operators, the registered resources
/// and — for the image placement matrix — on concrete numbers, because the inversion it depends on
/// is the single easiest thing in this converter to get wrong.
/// </summary>
public sealed class TextAndImageTests
{
    private static PostScriptConversionResult Convert(string ps,
                                                      PostScriptConversionOptions? options = null)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(ps), options);

    private static byte[] Content(string ps) => Convert(ps).Content;

    // ==================================================================
    //  Fonts
    // ==================================================================

    [Theory]
    [InlineData("/Helvetica", "Helvetica")]
    [InlineData("/Helvetica-Bold", "Helvetica-Bold")]
    [InlineData("/Times-Roman", "Times-Roman")]
    [InlineData("/Times-BoldItalic", "Times-BoldItalic")]
    [InlineData("/Courier", "Courier")]
    [InlineData("/Symbol", "Symbol")]
    [InlineData("/ZapfDingbats", "ZapfDingbats")]
    // Common non-standard names that map onto a metric-compatible standard face.
    [InlineData("/Arial", "Helvetica")]
    [InlineData("/Arial-Bold", "Helvetica-Bold")]
    [InlineData("/TimesNewRoman", "Times-Roman")]
    [InlineData("/Courier-Oblique", "Courier-Oblique")]
    public void FindFont_MapsOntoTheExpectedStandard14Face(string psName, string expectedBaseFont)
    {
        var result = Convert($"{psName} findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (x) show");

        var font = FirstFont(result.Resources);
        Assert.Equal(expectedBaseFont, ((PdfName)font[PdfName.BaseFont]).Value);
    }

    /// <summary>
    /// An unknown font must still produce text — raising <c>undefined</c> from <c>findfont</c> would
    /// kill the program at its first prologue line — and must say that it substituted.
    /// </summary>
    [Fact]
    public void FindFont_WithAnUnknownName_SubstitutesHelveticaAndReports()
    {
        var result = Convert("/NoSuchFontFamily findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (x) show");

        var font = FirstFont(result.Resources);
        Assert.Equal(StandardFonts.Helvetica, ((PdfName)font[PdfName.BaseFont]).Value);
        Assert.Contains("NoSuchFontFamily", result.LastError);
    }

    /// <summary>The emitted font declares WinAnsi, which is the encoding the strings are measured in.</summary>
    [Fact]
    public void EmittedFont_DeclaresWinAnsiEncoding()
    {
        var result = Convert("/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto (x) show");

        var font = FirstFont(result.Resources);
        Assert.Equal("WinAnsiEncoding", ((PdfName)font[PdfName.Encoding]).Value);
    }

    /// <summary>Setting one font repeatedly registers one resource, not one per call.</summary>
    [Fact]
    public void RepeatedSetFont_RegistersASingleFontResource()
    {
        var result = Convert(
            "/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto (a) show " +
            "/Helvetica findfont 24 scalefont setfont 0 20 moveto (b) show");

        var fonts = (PdfDictionary)result.Resources[PdfName.Font];
        Assert.Equal(1, fonts.Count);
    }

    // ==================================================================
    //  show
    // ==================================================================

    [Fact]
    public void Show_EmitsACompleteTextObject()
    {
        byte[] content = Content("/Helvetica findfont 12 scalefont setfont " +
                                 "newpath 100 200 moveto (Hi) show");

        var ops = ContentStreamAssert.Operators(content);
        Assert.Contains("BT", ops);
        Assert.Contains("Tf", ops);
        Assert.Contains("Tm", ops);
        Assert.Contains("Tj", ops);
        Assert.Contains("ET", ops);
        Assert.True(Array.IndexOf(ops, "BT") < Array.IndexOf(ops, "ET"));
    }

    /// <summary>
    /// The text matrix carries the font's scale <i>and</i> the pen position, and <c>Tf</c> uses
    /// size 1 — the only way PDF can express a <c>makefont</c> skew.
    /// </summary>
    [Fact]
    public void Show_PutsTheFontScaleInTheTextMatrixAndUsesSizeOneForTf()
    {
        byte[] content = Content("/Helvetica findfont 12 scalefont setfont " +
                                 "newpath 100 200 moveto (Hi) show");

        double[] tm = ContentStreamAssert.FirstOperandsOf(content, "Tm");
        Assert.Equal(12, tm[0], 5);
        Assert.Equal(0, tm[1], 5);
        Assert.Equal(0, tm[2], 5);
        Assert.Equal(12, tm[3], 5);
        Assert.Equal(100, tm[4], 5);
        Assert.Equal(200, tm[5], 5);

        ContentStreamAssert.Contains(content, "/F1 1 Tf");
    }

    /// <summary>A <c>makefont</c> skew survives into the emitted text matrix.</summary>
    [Fact]
    public void MakeFont_SkewSurvivesIntoTheTextMatrix()
    {
        byte[] content = Content("/Helvetica findfont [ 12 0 6 12 0 0 ] makefont setfont " +
                                 "newpath 0 0 moveto (Hi) show");

        double[] tm = ContentStreamAssert.FirstOperandsOf(content, "Tm");
        Assert.Equal(12, tm[0], 5);
        Assert.Equal(6, tm[2], 5);    // the skew term
        Assert.Equal(12, tm[3], 5);
    }

    [Fact]
    public void Show_EscapesParenthesesAndBackslashesInTheEmittedString()
    {
        byte[] content = Content(@"/Helvetica findfont 12 scalefont setfont " +
                                 @"newpath 0 0 moveto (a\(b\)c) show");

        string text = Encoding.ASCII.GetString(content);
        Assert.Contains(@"(a\(b\)c)", text);
    }

    [Fact]
    public void Show_AdvancesTheCurrentPointByTheStringWidth()
    {
        // Read the advance back out through the language: `currentpoint` pushes where the pen
        // ended up, and feeding that to `moveto` puts the number in the emitted stream where it
        // can be asserted. Helvetica 'A' and 'V' are 667/1000 each, so at size 10 the pen must
        // have moved 13.34 points — and not moved vertically at all.
        byte[] content = Content("/Helvetica findfont 10 scalefont setfont " +
                                 "newpath 0 0 moveto (AV) show " +
                                 "currentpoint newpath moveto 0 0 lineto stroke");

        double[] start = ContentStreamAssert.FirstOperandsOf(content, "m");
        Assert.Equal(13.34, start[0], 2);
        Assert.Equal(0, start[1], 5);
    }

    // ==================================================================
    //  ashow / widthshow — spacing that must not outlive its own show
    // ==================================================================

    /// <summary>
    /// <c>ashow</c> adds its extra spacing to every character, which PDF spells <c>Tc</c>.
    /// </summary>
    [Fact]
    public void AShow_EmitsTheCharacterSpacingAsTc()
    {
        byte[] content = Content("/Helvetica findfont 12 scalefont setfont " +
                                 "newpath 0 0 moveto 5 0 (AB) ashow");

        // 5 user-space units at size 12 is 5/12 in TEXT space, which is where Tc lives (§9.4.4) —
        // and this emitter puts the size in Tm with `Tf 1`. This asserted a bare "5 Tc", i.e. the
        // bug: a reader applied 5x12 = 60 points per glyph and the run came out 12x too loose.
        ContentStreamAssert.Contains(content, "0.416667 Tc");
    }

    /// <summary>
    /// The emitted <c>Tc</c> must change when only the font size does.
    ///
    /// <para>The pair above passed with the spacing emitted raw, because at one size a raw value
    /// and a correctly converted one are indistinguishable. Two sizes are what make the conversion
    /// observable at all.</para>
    /// </summary>
    [Fact]
    public void AShow_ScalesTheCharacterSpacingWithTheFontSize()
    {
        byte[] small = Content("/Helvetica findfont 10 scalefont setfont " +
                               "newpath 0 0 moveto 5 0 (AB) ashow");
        byte[] large = Content("/Helvetica findfont 100 scalefont setfont " +
                               "newpath 0 0 moveto 5 0 (AB) ashow");

        ContentStreamAssert.Contains(small, "0.5 Tc");     // 5/10
        ContentStreamAssert.Contains(large, "0.05 Tc");    // 5/100
    }

    /// <summary>
    /// <c>widthshow</c> adds its extra width to one nominated character — the space, in every real
    /// use — which PDF spells <c>Tw</c>.
    /// </summary>
    [Fact]
    public void WidthShow_EmitsTheWordSpacingAsTw()
    {
        byte[] content = Content("/Helvetica findfont 12 scalefont setfont " +
                                 "newpath 0 0 moveto 7 0 32 (A B) widthshow");

        ContentStreamAssert.Contains(content, "0.583333 Tw");   // 7 user-space units / size 12
    }

    /// <summary>
    /// <c>awidthshow</c> takes <c>cx cy char ax ay string</c> — the word adjustment FIRST, then the
    /// nominated character, then the per-glyph adjustment. The pops were in the wrong order, so the
    /// word amount was applied to every glyph and the letter spacing was dropped entirely.
    /// </summary>
    [Fact]
    public void AWidthShow_EmitsBothAdjustmentsFromTheRightOperands()
    {
        byte[] content = Content("/Helvetica findfont 12 scalefont setfont " +
                                 "newpath 0 0 moveto 3 0 32 1 0 (A B) awidthshow");

        ContentStreamAssert.Contains(content, "0.083333 Tc");   // ax = 1 / 12
        ContentStreamAssert.Contains(content, "0.25 Tw");       // cx = 3 / 12
    }

    /// <summary>A fractional <c>ay</c> must not land in the integer <c>char</c> slot.</summary>
    [Fact]
    public void AWidthShow_WithAFractionalVerticalAdjustment_StillDrawsTheText()
    {
        byte[] content = Content("/Helvetica findfont 12 scalefont setfont " +
                                 "newpath 0 0 moveto 3 0 32 1 0.5 (A B) awidthshow");

        ContentStreamAssert.Contains(content, "Tj");
    }

    /// <summary>
    /// The spacing must not survive into the next <c>show</c>.
    ///
    /// <para><c>Tc</c> and <c>Tw</c> are <b>graphics-state</b> parameters, not text-object ones:
    /// <c>BT</c> resets the text matrices and nothing else (§9.4.1). So emitting <c>Tc</c> only when
    /// it is non-zero leaves one <c>ashow</c>'s letter spacing applied to every plain <c>show</c>
    /// that follows it — text the program asked to set solid, drawn loose.</para>
    /// </summary>
    [Fact]
    public void AShow_DoesNotLeakItsCharacterSpacingIntoTheNextShow()
    {
        byte[] content = Content("/Helvetica findfont 12 scalefont setfont " +
                                 "newpath 0 100 moveto 5 0 (AB) ashow " +
                                 "newpath 0 50 moveto (CD) show");

        var ops = ContentStreamAssert.Parse(content);
        int secondText = ops.FindIndex(ops.FindIndex(o => o.Operator == "BT") + 1,
                                       o => o.Operator == "BT");
        Assert.True(secondText > 0, "the second show should have emitted its own text object.");

        // Everything from the second BT onward must put the spacing back to zero.
        var second = ops.Skip(secondText).Select(o => o.ToString()).ToList();
        Assert.Contains("0 Tc", second);
        Assert.Contains("0 Tw", second);
    }

    // ==================================================================
    //  stringwidth — real metrics
    // ==================================================================

    /// <summary>
    /// <c>stringwidth</c> must use the real AFM widths. Helvetica's 'A' and 'V' are both 667/1000,
    /// so "AV" at size 1 is exactly 1.334 — a number no stub returns by accident.
    /// </summary>
    [Fact]
    public void StringWidth_Helvetica_ReturnsTheAfmSum()
    {
        byte[] content = Content(
            "/Helvetica findfont 1 scalefont setfont (AV) stringwidth pop " +
            "newpath 0 0 moveto 0 rlineto fill");

        // The width was pushed then used as the x of an rlineto, so it lands in a lineto operand.
        double[] line = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(1.334, line[0], 3);
    }

    [Fact]
    public void StringWidth_ScalesWithTheFontSize()
    {
        byte[] content = Content(
            "/Helvetica findfont 10 scalefont setfont (AV) stringwidth pop " +
            "newpath 0 0 moveto 0 rlineto fill");

        double[] line = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(13.34, line[0], 2);
    }

    /// <summary>Courier is monospaced at 600/1000, so three characters are exactly 1.8 at size 1.</summary>
    [Fact]
    public void StringWidth_Courier_IsMonospaced()
    {
        byte[] content = Content(
            "/Courier findfont 1 scalefont setfont (iWm) stringwidth pop " +
            "newpath 0 0 moveto 0 rlineto fill");

        double[] line = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(1.8, line[0], 3);
    }

    [Fact]
    public void StringWidth_PushesZeroForTheVerticalComponent()
    {
        byte[] content = Content(
            "/Helvetica findfont 10 scalefont setfont (A) stringwidth " +
            "newpath 0 0 moveto lineto fill");

        // wx wy were both pushed; lineto consumed them as x y, so y must be 0.
        double[] line = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(0, line[1], 6);
    }

    // ==================================================================
    //  charpath
    // ==================================================================

    /// <summary>
    /// <c>charpath</c> on a standard face no outline font is had for appends a metric box, not
    /// outlines, and says so. The box has to be the right width or a following clip would be wrong.
    /// </summary>
    [Fact]
    public void CharPath_WithNoFace_AppendsTheMetricBoxAndReportsTheApproximation()
    {
        var result = Convert("/Helvetica findfont 10 scalefont setfont " +
                             "newpath 0 0 moveto (AV) false charpath fill",
                             new PostScriptConversionOptions { OutlineFontProvider = _ => null });

        Assert.Contains("charpath", result.LastError);
        Assert.Contains("metric box", result.LastError);

        // Four corners of a box 13.34 wide.
        var ops = ContentStreamAssert.Parse(result.Content);
        Assert.Contains(ops, o => o.Operator == "l" &&
            Math.Abs(double.Parse(o.Operands[0], System.Globalization.CultureInfo.InvariantCulture) - 13.34) < 0.01);
    }

    // ==================================================================
    //  Text can be disabled
    // ==================================================================

    [Fact]
    public void Show_WithTextDisabled_DrawsNothingAndIsRecorded()
    {
        var result = Convert("/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto (x) show",
                             new PostScriptConversionOptions { AllowText = false });

        ContentStreamAssert.DoesNotContainOperator(result.Content, "Tj");
        Assert.Contains("show", result.UnimplementedOperators);
    }

    // ==================================================================
    //  Non-Latin text — the failure that is otherwise invisible
    // ==================================================================

    /// <summary>
    /// Bytes with no glyph at all — the five slots CP1252 leaves undefined — are reported, and the
    /// conversion is not clean.
    /// </summary>
    [Fact]
    public void Show_WithBytesTheFontCannotRepresent_IsReportedAndNotClean()
    {
        // 0x81, 0x8D, 0x8F, 0x90 and 0x9D are undefined in WinAnsi.
        var result = ConvertBytes(BuildShow(new byte[] { 0x81, 0x8D, 0x8F, 0x90, 0x9D }));

        Assert.False(result.IsClean, "a conversion that draws nothing for its text must not be clean.");
        Assert.Contains("no glyph", result.LastError);
        Assert.Contains("WinAnsi", result.LastError);
    }

    /// <summary>Plain Latin text stays clean — the report must not fire for ordinary documents.</summary>
    [Fact]
    public void Show_WithLatinText_IsClean()
    {
        var result = ConvertBytes(BuildShow(Encoding.ASCII.GetBytes("Hello, world!")));

        Assert.True(result.IsClean,
            $"a plain Latin conversion should be clean; got: {result.LastError}");
    }

    /// <summary>
    /// Accented Latin-1 IS representable in WinAnsi, so it must not be reported — otherwise the
    /// warning fires on every French or German document and stops meaning anything.
    /// </summary>
    [Fact]
    public void Show_WithAccentedLatin1_IsClean()
    {
        // "cafe" with an acute e — 0xE9 in both Latin-1 and WinAnsi.
        var result = ConvertBytes(BuildShow(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 }));

        Assert.True(result.IsClean,
            $"WinAnsi-representable accented text should be clean; got: {result.LastError}");
    }

    /// <summary>The report fires once per conversion, not once per shown string.</summary>
    [Fact]
    public void Show_WithManyUnrepresentableStrings_ReportsOnce()
    {
        var sb = new StringBuilder("/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto ");
        for (int i = 0; i < 20; i++) sb.Append(@"(\201\215) show ");

        var result = ConvertBytes(Encoding.Latin1.GetBytes(sb.ToString()));

        // findfont/setfont report nothing for Helvetica, so the unrepresentable notice is the only
        // diagnostic this program can produce.
        Assert.Equal(1, result.DiagnosticCount);
    }

    /// <summary>
    /// Legacy Hebrew bytes with <b>no</b> re-encoding are indistinguishable from Latin-1, and
    /// convert as Latin-1.
    ///
    /// <para>ISO-8859-8 for "shalom" is the bytes <c>E9 EC E5 ED</c> — byte-identical to Latin-1
    /// accented text. Nothing in the PostScript distinguishes them except the font /Encoding, so a
    /// file that omits the re-encoding genuinely cannot be told apart and is converted as what it
    /// looks like. Asserting a detection here would be asserting something information-theoretically
    /// impossible.</para>
    /// </summary>
    [Fact]
    public void Show_WithLegacyHebrewBytesAndNoReEncoding_ConvertsAsLatin1()
    {
        var result = ConvertBytes(BuildShow(new byte[] { 0xE9, 0xEC, 0xE5, 0xED }));

        Assert.True(result.IsClean);
        Assert.Contains("Tj", Encoding.ASCII.GetString(result.Content));
    }

    /// <summary>
    /// A Hebrew document that <i>does</i> re-encode — the normal case — now carries its glyph names
    /// into a PDF <c>/Differences</c> encoding, and is told that the base face cannot draw them.
    ///
    /// <para>Both halves matter. The <c>/Differences</c> array is what a reader needs to resolve the
    /// intended glyphs at all; the report is what stops the conversion claiming success when the
    /// standard-14 base face has no Hebrew in it. Embedding a font that does is the out-of-scope
    /// part.</para>
    /// </summary>
    [Fact]
    public void Show_WithReEncodedHebrew_EmitsDifferencesAndReportsTheMissingGlyphs()
    {
        var slots = new string[256];
        for (int k = 0; k < 256; k++) slots[k] = "/.notdef";
        slots[0xE9] = "/afii57668";
        slots[0xEC] = "/afii57676";

        var result = ConvertBytes(Encoding.Latin1.GetBytes(
            "/EncodingArray [ " + string.Join(" ", slots) + " ] def " +
            "/Helvetica findfont dup length dict begin " +
            "{1 index /FID ne {def} {pop pop} ifelse} forall " +
            "/Encoding EncodingArray def currentdict end /HebrewFont exch definefont pop " +
            @"/HebrewFont findfont 12 scalefont setfont newpath 0 0 moveto (éì) show"));

        // definefont is implemented, so it is no longer the signal.
        Assert.DoesNotContain("definefont", result.UnimplementedOperators);

        // The glyph names reached the PDF font.
        var fonts = (PdfDictionary)result.Resources[PdfName.Font];
        PdfDictionary font = null;
        foreach (var kv in fonts) { font = (PdfDictionary)kv.Value; break; }
        var encoding = (PdfDictionary)font[PdfName.Encoding];
        Assert.NotNull(encoding[PdfName.Differences]);

        // And the caller is told the base face cannot draw them.
        Assert.False(result.IsClean);
        Assert.Contains("embedded font", result.LastError);
    }

    private static string BuildShow(byte[] textBytes)
    {
        var sb = new StringBuilder("/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto (");
        foreach (byte b in textBytes)
        {
            // Escape so the bytes survive as string content rather than as delimiters.
            if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\') sb.Append('\\');
            sb.Append((char)b);
        }
        sb.Append(") show");
        return sb.ToString();
    }

    private static PostScriptConversionResult ConvertBytes(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    private static PostScriptConversionResult ConvertBytes(byte[] ps)
        => PostScriptConversion.ConvertFragment(ps);

    // ==================================================================
    //  Images
    // ==================================================================

    /// <summary>
    /// The placement matrix is the <b>inverse</b> of PostScript's image matrix, composed with the
    /// unit-square-to-grid scale. A unit-square image matrix <c>[2 0 0 -2 0 2]</c> — the canonical
    /// "map a 2x2 grid onto the unit square, top row first" — must place the image upright.
    ///
    /// <para>Concrete numbers, because the failure mode is a vertically mirrored image that still
    /// looks structurally correct in every other assertion.</para>
    /// </summary>
    [Fact]
    public void Image_UnitSquareMatrix_EmitsACmThatIsNotVerticallyFlipped()
    {
        // 2x2 greyscale, 8bpc. Image matrix maps user space to the 2x2 grid.
        byte[] content = Content("2 2 8 [ 2 0 0 -2 0 2 ] <00FF00FF> image");

        double[] cm = ContentStreamAssert.FirstOperandsOf(content, "cm");

        // Expected: unit square scaled to 1x1 in user space, upright (positive d).
        Assert.Equal(1, cm[0], 5);
        Assert.Equal(0, cm[1], 5);
        Assert.Equal(0, cm[2], 5);
        Assert.Equal(1, cm[3], 5);
        Assert.Equal(0, cm[4], 5);
        Assert.Equal(0, cm[5], 5);
    }

    [Fact]
    public void Image_RegistersAnImageXObjectWithTheRightGeometry()
    {
        var result = Convert("2 2 8 [ 2 0 0 -2 0 2 ] <00FF00FF> image");

        var xobjects = (PdfDictionary)result.Resources[PdfName.XObject];
        var image = (PdfStream)xobjects[new PdfName("Im1")];

        Assert.Equal("Image", ((PdfName)image[PdfName.Subtype]).Value);
        Assert.Equal(2, ((PdfNumber)image[PdfName.Width]).LongValue);
        Assert.Equal(2, ((PdfNumber)image[PdfName.Height]).LongValue);
        Assert.Equal(8, ((PdfNumber)image[PdfName.BitsPerComponent]).LongValue);
        Assert.Equal(PdfColorSpaceNames.DeviceGray, ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00, 0xFF }, image.RawData);
    }

    [Fact]
    public void Image_IsPlacedWithADoOperator()
        => ContentStreamAssert.Contains(Content("2 2 8 [ 2 0 0 -2 0 2 ] <00FF00FF> image"),
                                        "/Im1 Do");

    [Fact]
    public void ColorImage_WithThreeComponents_UsesDeviceRgb()
    {
        var result = Convert("1 1 8 [ 1 0 0 -1 0 1 ] <FF8000> false 3 colorimage");

        var xobjects = (PdfDictionary)result.Resources[PdfName.XObject];
        var image = (PdfStream)xobjects[new PdfName("Im1")];
        Assert.Equal(PdfColorSpaceNames.DeviceRGB, ((PdfName)image[PdfName.ColorSpace]).Value);
    }

    [Fact]
    public void ColorImage_WithFourComponents_UsesDeviceCmyk()
    {
        var result = Convert("1 1 8 [ 1 0 0 -1 0 1 ] <00FF00FF> false 4 colorimage");

        var xobjects = (PdfDictionary)result.Resources[PdfName.XObject];
        var image = (PdfStream)xobjects[new PdfName("Im1")];
        Assert.Equal(PdfColorSpaceNames.DeviceCMYK, ((PdfName)image[PdfName.ColorSpace]).Value);
    }

    [Fact]
    public void ImageMask_EmitsImageMaskTrueAndNoColorSpace()
    {
        var result = Convert("8 1 true [ 8 0 0 -1 0 1 ] <AA> imagemask");

        var xobjects = (PdfDictionary)result.Resources[PdfName.XObject];
        var image = (PdfStream)xobjects[new PdfName("Im1")];

        Assert.Equal(PdfBoolean.True, image[PdfName.ImageMask]);
        Assert.Null(image[PdfName.ColorSpace]);
    }

    /// <summary>
    /// <c>imagemask</c>'s polarity operand decides <b>which sample value paints</b>, and PDF spells
    /// that choice as <c>/Decode</c>. PLRM: polarity <c>true</c> paints where the sample is 0, which
    /// is PDF's default <c>/Decode [0 1]</c>; polarity <c>false</c> paints where it is 1, which needs
    /// the array reversed.
    ///
    /// <para>The two cases are asserted together in one test because the defect was that they were
    /// <b>indistinguishable</b> — the boolean was popped into the bit-depth slot and discarded, so
    /// both polarities produced a byte-identical dictionary and every mask written the second way
    /// round rendered inverted. A test of either polarity alone cannot see that.</para>
    /// </summary>
    [Fact]
    public void ImageMask_PolarityChoosesTheDecodeArray()
    {
        var painted0 = MaskImage(Convert("8 1 true  [ 8 0 0 -1 0 1 ] <AA> imagemask"));
        var painted1 = MaskImage(Convert("8 1 false [ 8 0 0 -1 0 1 ] <AA> imagemask"));

        // true: 0 paints, which is the PDF default — no /Decode needed.
        Assert.Null(painted0[PdfName.Decode]);

        // false: 1 paints, which only /Decode [1 0] can say.
        var decode = (PdfArray)painted1[PdfName.Decode];
        Assert.NotNull(decode);
        Assert.Equal(2, decode.Count);
        Assert.Equal(1, ((PdfNumber)decode[0]).LongValue);
        Assert.Equal(0, ((PdfNumber)decode[1]).LongValue);
    }

    private static PdfStream MaskImage(PostScriptConversionResult result)
    {
        var xobjects = (PdfDictionary)result.Resources[PdfName.XObject];
        return (PdfStream)xobjects[new PdfName("Im1")];
    }

    [Fact]
    public void Image_WithImagesDisabled_DrawsNothingAndIsRecorded()
    {
        var result = Convert("2 2 8 [ 2 0 0 -2 0 2 ] <00FF00FF> image",
                             new PostScriptConversionOptions { AllowImages = false });

        ContentStreamAssert.DoesNotContainOperator(result.Content, "Do");
        Assert.Contains("image", result.UnimplementedOperators);
    }

    /// <summary>A procedure data source is run through the step loop, its string the image's samples.</summary>
    [Fact]
    public void Image_WithAProcedureDataSource_IsDrawn()
    {
        var result = Convert("2 2 8 [ 2 0 0 -2 0 2 ] { <00FF00FF> } image");

        Assert.Equal(1, ContentStreamAssert.Count(result.Content, "Do"));
        Assert.DoesNotContain(result.UnimplementedOperators, n => n.Contains("procedure data source"));
    }

    // ==================================================================
    //  Adversarial
    // ==================================================================

    [Fact]
    public void ImageWithAbsurdDimensions_IsRefusedBeforeAllocating()
    {
        var result = Convert("1000000 1000000 8 [ 1 0 0 -1 0 1 ] <00> image");

        Assert.Contains("LimitCheck", result.LastError);
        ContentStreamAssert.DoesNotContainOperator(result.Content, "Do");
    }

    [Fact]
    public void ImageWithAShortDataSource_IsPaddedAndReported()
    {
        var result = Convert("4 4 8 [ 4 0 0 -4 0 4 ] <00FF> image");

        Assert.Contains("padded", result.LastError);
        ContentStreamAssert.Contains(result.Content, "/Im1 Do");
    }

    [Fact]
    public void ImageBomb_IsBoundedByTheImageCountCap()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < PsLimits.MaxImageXObjects + 20; i++)
            sb.Append("1 1 8 [ 1 0 0 -1 0 1 ] <FF> image ");

        var result = Convert(sb.ToString());

        var xobjects = (PdfDictionary)result.Resources[PdfName.XObject];
        Assert.True(xobjects.Count <= PsLimits.MaxImageXObjects,
            $"{xobjects.Count} images were registered, past the {PsLimits.MaxImageXObjects} cap.");
    }

    [Fact]
    public void ImageWithASingularMatrix_IsRefusedAndReported()
    {
        var result = Convert("2 2 8 [ 0 0 0 0 0 0 ] <00FF00FF> image");

        ContentStreamAssert.DoesNotContainOperator(result.Content, "Do");
        Assert.Contains("singular", result.LastError);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static PdfDictionary FirstFont(PdfDictionary resources)
    {
        Assert.NotNull(resources);
        var fonts = (PdfDictionary)resources[PdfName.Font];
        Assert.NotNull(fonts);

        foreach (var kv in fonts) return (PdfDictionary)kv.Value;
        throw new Xunit.Sdk.XunitException("no font was registered");
    }
}
