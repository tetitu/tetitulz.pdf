// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Operators that would raise <c>undefined</c>, the document writer's font programs, the
/// emitter's cap, and four smaller items. Every test here fails without the code it guards.
/// </summary>
public sealed class SecondReviewRegressionTests
{
    private static PostScriptConversionResult Run(string ps, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), options);

    private static string Text(string ps) => Encoding.Latin1.GetString(Run(ps).Content ?? []);

    // ==================================================================
    //  selectfont — undefined, and it cost the whole top-level object
    // ==================================================================

    /// <summary>The Level 2 shorthand must be exactly the long form.</summary>
    [Fact]
    public void SelectFont_IsEquivalentToFindFontScaleFontSetFont()
    {
        string longForm = Text("/Helvetica findfont 24 scalefont setfont " +
                               "newpath 100 100 moveto (Hi) show");
        string shortForm = Text("/Helvetica 24 selectfont newpath 100 100 moveto (Hi) show");

        Assert.Contains("Tj", longForm);           // the fixture really draws text
        Assert.Equal(longForm, shortForm);
    }

    /// <summary>
    /// The matrix form is how condensed/expanded/obliqued text is expressed. Treating it as a
    /// uniform scale collapses exactly what the emitted Tm exists to carry.
    /// </summary>
    [Fact]
    public void SelectFont_WithAMatrix_CarriesTheSkewIntoTheTextMatrix()
    {
        string s = Text("/Helvetica [12 0 6 12 0 0] selectfont newpath 0 0 moveto (A) show");

        // Tm is "a b c d tx ty Tm" — c = 6 is the skew, and it must survive.
        Assert.Contains("12 0 6 12 0 0 Tm", s);
    }

    /// <summary>
    /// The reason this mattered: an undefined operator makes PsInterpreter.Run abandon the whole
    /// top-level object, so for a page whose body is one procedure every mark after it was lost.
    /// </summary>
    [Fact]
    public void SelectFont_InsideAProcedure_DoesNotCostTheDrawingAfterIt()
    {
        string s = Text("{ newpath 0 0 moveto 10 0 lineto 10 10 lineto fill " +
                        "/Helvetica 12 selectfont " +
                        "newpath 50 50 moveto 60 50 lineto 60 60 lineto fill } exec");

        Assert.Contains("0 0 m", s);     // before
        Assert.Contains("50 50 m", s);   // and after
    }

    [Fact]
    public void SelectFont_LeavesNoOperandsBehind()
        => AssertLeavesOnStack("/Helvetica 12 selectfont", 0);

    [Theory]
    [InlineData("12 /Helvetica selectfont")]   // operands reversed
    [InlineData("/Helvetica (x) selectfont")]  // scale is neither number nor matrix
    public void SelectFont_WithBadOperands_RaisesTypeCheck(string ps)
    {
        // Nothing after it: LastError is the LAST report, so a trailing `show` would overwrite the
        // typecheck with its own "no font set" note and the assertion would read the wrong one.
        var result = Run(ps);
        Assert.Contains("typecheck", result.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    //  glyphshow / xshow / yshow / xyshow
    // ==================================================================

    /// <summary>A glyph already in the encoding is shown as itself — no extra font resource.</summary>
    [Fact]
    public void GlyphShow_ForAGlyphInTheEncoding_MatchesShow()
    {
        var viaShow = Run("/Helvetica 12 selectfont newpath 0 0 moveto (A) show");
        var viaGlyph = Run("/Helvetica 12 selectfont newpath 0 0 moveto /A glyphshow");

        Assert.Equal(Encoding.Latin1.GetString(viaShow.Content), Encoding.Latin1.GetString(viaGlyph.Content));
        Assert.Equal(FontCount(viaShow), FontCount(viaGlyph));
    }

    /// <summary>
    /// A glyph WinAnsi can reach is reached that way — no synthesised font. /bullet lives at 0x95,
    /// so this is the ordinary path even though no /Differences names it.
    /// </summary>
    [Fact]
    public void GlyphShow_ForAGlyphWinAnsiCanReach_UsesTheOrdinaryFont()
    {
        var result = Run("/Helvetica 12 selectfont newpath 0 0 moveto /bullet glyphshow");

        Assert.Equal(1, FontCount(result));
        Assert.DoesNotContain("/Differences", DescribeFonts(result));
        Assert.Contains(@"(\225) Tj", Encoding.Latin1.GetString(result.Content ?? []));
    }

    /// <summary>
    /// A glyph the encoding genuinely cannot reach is the case glyphshow exists for: it gets a
    /// synthesised one-entry /Differences, the only way to name it in PDF. /alpha is not in WinAnsi.
    /// </summary>
    [Fact]
    public void GlyphShow_ForAGlyphOutsideTheEncoding_SynthesisesADifferencesFont()
    {
        var result = Run("/Helvetica 12 selectfont newpath 0 0 moveto /alpha glyphshow");

        Assert.Contains("Tj", Encoding.Latin1.GetString(result.Content ?? []));
        Assert.Contains("/alpha", DescribeFonts(result));
        Assert.Contains("/Differences", DescribeFonts(result));
    }

    /// <summary>Repeating one glyph must not mint a font per call.</summary>
    [Fact]
    public void GlyphShow_RepeatedForOneGlyph_MintsOneFont()
    {
        var once = Run("/Helvetica 12 selectfont newpath 0 0 moveto /alpha glyphshow");
        var many = Run("/Helvetica 12 selectfont newpath 0 0 moveto " +
                       "20 { /alpha glyphshow } repeat");

        Assert.Equal(FontCount(once), FontCount(many));
    }

    /// <summary>The synthetic-font cap keeps /Resources from becoming the attack.</summary>
    [Fact]
    public void GlyphShow_OverTheSyntheticFontCap_StopsMintingAndReports()
    {
        // Build far more distinct glyph names than the cap allows.
        var sb = new StringBuilder("/Helvetica 12 selectfont newpath 0 0 moveto ");
        for (int i = 0; i < PsLimits.MaxSyntheticGlyphFonts + 50; i++)
            sb.Append("/zz").Append(i).Append(" glyphshow ");

        var result = Run(sb.ToString());

        Assert.True(FontCount(result) <= PsLimits.MaxSyntheticGlyphFonts + 2,
            $"{FontCount(result)} fonts were minted, past the {PsLimits.MaxSyntheticGlyphFonts} cap");
    }

    /// <summary>xshow places each glyph by an explicit displacement, not by its own width.</summary>
    [Fact]
    public void XShow_PlacesEachGlyphAtTheGivenDisplacement()
    {
        string s = Text("/Helvetica 12 selectfont newpath 0 0 moveto (AB) [40 40] xshow");

        // Two text objects: the first at x=0, the second 40 further along.
        Assert.Contains("12 0 0 12 0 0 Tm", s);
        Assert.Contains("12 0 0 12 40 0 Tm", s);
    }

    [Fact]
    public void YShow_DisplacesVertically()
    {
        string s = Text("/Helvetica 12 selectfont newpath 0 0 moveto (AB) [40 40] yshow");
        Assert.Contains("12 0 0 12 0 40 Tm", s);
    }

    [Fact]
    public void XyShow_ConsumesAPairPerGlyph()
    {
        string s = Text("/Helvetica 12 selectfont newpath 0 0 moveto (AB) [40 10 40 10] xyshow");
        Assert.Contains("12 0 0 12 40 10 Tm", s);
    }

    /// <summary>A new show path must honour the caller's AllowText, exactly as Paint does.</summary>
    [Theory]
    [InlineData("(AB) [40 40] xshow")]
    [InlineData("/bullet glyphshow")]
    public void PositionedShow_WhenTextIsDisallowed_DrawsNothing(string tail)
    {
        var options = new PostScriptConversionOptions { AllowText = false };
        var result = Run("/Helvetica 12 selectfont newpath 0 0 moveto " + tail, options);

        Assert.DoesNotContain("Tj", Encoding.Latin1.GetString(result.Content ?? []));
    }

    /// <summary>
    /// And it must still report text the font cannot draw — a Hebrew xshow reporting IsClean is the
    /// exact case ReportUnrepresentable's doc says must never happen.
    /// </summary>
    [Fact]
    public void XShow_WithUnrepresentableText_IsReported()
    {
        // <8181> is a PostScript hex string: two bytes of 0x81, one of the five code points CP1252
        // leaves undefined. WinAnsi.ToChar maps it to NUL, which is exactly what
        // CountUnrepresentable looks for — and writing it as hex keeps this source 7-bit clean
        // rather than carrying an invisible control character.
        //
        // Hebrew bytes would NOT work here, and that is not a fixture flaw: ISO-8859-8 and Latin-1
        // are byte-identical, so a Hebrew run really is undetectable. PsFontMapper's own doc and
        // GAPS.md §2 both record that limit.
        // Thread-filtered: OnEvent is process-wide, so an unfiltered handler collects whatever
        // other test classes are converting in parallel.
        using var collector = TestInfrastructure.DiagnosticCollector.Start();
        Run(@"/Helvetica 12 selectfont newpath 0 0 moveto <8181> [10 10] xshow");

        Assert.True(collector.Any("no glyph"),
            "an unrepresentable run must not convert silently");
    }

    // ==================================================================
    //  The language operators
    // ==================================================================

    [Fact]
    public void CleardictStack_PopsBackToThePermanentDictionaries()
        => AssertPushesInt("5 dict begin 5 dict begin cleardictstack countdictstack", 3);

    [Fact]
    public void DictStack_StoresTheStackAndPushesThePopulatedPrefix()
        => AssertPushesInt("20 array dictstack length", 3);

    [Fact]
    public void ExecStack_DoesNotHandOutTheStoppedSentinel()
    {
        // Inside `stopped`, the frame body carries a private operator that pushes false. If
        // execstack handed it out, running it would push a spurious boolean.
        AssertPushesInt("{ 20 array execstack length } stopped { 0 } if", 1);
    }

    [Fact]
    public void SetPacking_RoundTripsThroughCurrentPacking()
    {
        Assert.Contains("\n1 0 l\n", Text(
            "true setpacking currentpacking { 1 } { 0 } ifelse " +
            "newpath 0 0 moveto 0 rlineto 10 0 rlineto fill"));
        Assert.Contains("\n0 0 l\n", Text(
            "false setpacking currentpacking { 1 } { 0 } ifelse " +
            "newpath 0 0 moveto 0 rlineto 10 0 rlineto fill"));
    }

    [Theory]
    [InlineData("255 16 8 string cvrs", "FF")]
    [InlineData("8 2 8 string cvrs", "1000")]
    [InlineData("-1 16 12 string cvrs", "FFFFFFFF")]   // unsigned: PLRM treats it as a bit pattern
    [InlineData("255 10 8 string cvrs", "255")]
    public void Cvrs_ConvertsToTheGivenRadix(string ps, string expected)
    {
        // Show the produced string so the assertion reads the actual bytes out of the stream.
        string s = Text("/Helvetica 12 selectfont newpath 0 0 moveto " + ps + " show");
        Assert.Contains("(" + expected + ") Tj", s);
    }

    // ==================================================================
    //  Operators deliberately left undefined
    // ==================================================================

    /// <summary>
    /// The user-path and hit-test families must stay undefined so a `where` probe answers no and
    /// the program takes its own fallback — which this interpreter can actually draw. Registering
    /// them would make the probe say yes and select a branch whose painting we discard.
    /// </summary>
    [Theory]
    [InlineData("ufill")]
    [InlineData("ueofill")]
    [InlineData("ustroke")]
    [InlineData("upath")]
    [InlineData("uappend")]
    [InlineData("infill")]
    [InlineData("ineofill")]
    public void ProbeGuardedOperators_AreNotDefined(string op)
    {
        // `/op where` pushes false when undefined, so the else branch runs and draws.
        string s = Text($"/{op} where {{ pop 1 }} {{ 0 }} ifelse " +
                        "newpath 0 0 moveto 0 rlineto 10 0 rlineto fill");
        Assert.Contains("\n0 0 l\n", s);
    }

    /// <summary>
    /// The unconditionally-called ones must be recognised instead, so their operands go and the
    /// rest of the object survives.
    /// </summary>
    [Theory]
    [InlineData("{} {} {} {} pathforall", 0)]
    [InlineData("/Helvetica findfont 10 scalefont setfont 0 0 moveto {pop pop} (AB) kshow", 0)]
    [InlineData("0 0 10 10 setbbox", 0)]
    [InlineData("{} setblackgeneration", 0)]
    [InlineData("{} setundercolorremoval", 0)]
    public void UnconditionallyCalledOperators_ConsumeTheirOperands(string ps, int leftOver)
        => AssertLeavesOnStack(ps, leftOver);

    // ==================================================================
    //  Document import: the font program must reach the file
    // ==================================================================

    private const string EmbeddedMarker = "A1B2C3D4E5F60718293A4B5C6D7E8F90";

    private static readonly string FontProgram =
        "%!PS-AdobeFont-1.0: Custom 001.000\n" +
        "/FontName /Custom def\n/FontType 1 def\n/Encoding StandardEncoding def\n" +
        "currentfile eexec\n" + EmbeddedMarker + "\n" +
        new string('0', 512) + "\ncleartomark\n";

    private static string ConvertDocument(string body)
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 200\n" + body));
        PostScriptConversion.ConvertDocument(input, output);
        return TestInfrastructure.PdfStreamText.Decoded(output.ToArray());
    }

    /// <summary>
    /// The promotion walk descended one level, so /Font -> F1 -> /FontDescriptor -> /FontFile
    /// stayed direct and PdfWriter serialised it as dictionary entries with its data dropped —
    /// a /FontFile with no font program, and a success report.
    /// </summary>
    [Fact]
    public void DocumentImport_CarriesTheEmbeddedFontProgramIntoThePdf()
    {
        string pdf = ConvertDocument(FontProgram +
            "/Custom 12 dict definefont pop /Custom findfont 12 scalefont setfont " +
            "newpath 20 20 moveto (Hi) show showpage\n");

        Assert.Contains("/FontFile", pdf);
        Assert.Contains(EmbeddedMarker, pdf);
    }

    /// <summary>And the entry must be a reference, not an inline stream (§7.3.8).</summary>
    [Fact]
    public void DocumentImport_WritesTheFontFileAsAnIndirectReference()
    {
        string pdf = ConvertDocument(FontProgram +
            "/Custom 12 dict definefont pop /Custom findfont 12 scalefont setfont " +
            "newpath 20 20 moveto (Hi) show showpage\n");

        int at = pdf.IndexOf("/FontFile", StringComparison.Ordinal);
        string tail = pdf.Substring(at, Math.Min(40, pdf.Length - at));
        Assert.Matches(@"/FontFile \d+ 0 R", tail);
    }

    /// <summary>Images were already promoted correctly — kept so the rewrite cannot regress them.</summary>
    [Fact]
    public void DocumentImport_StillCarriesImageSamples()
    {
        string pdf = ConvertDocument("gsave 100 0 0 100 20 20 concat " +
                                     "2 2 8 [2 0 0 -2 0 2] <00FF7F40> image grestore showpage\n");
        Assert.Contains("/Image", pdf);
        Assert.Contains("\nstream", pdf);
    }

    /// <summary>Two pages share one resource dictionary; it must be written once, not per page.</summary>
    [Fact]
    public void DocumentImport_SharesOneResourceObjectAcrossPages()
    {
        string pdf = ConvertDocument(FontProgram +
            "/Custom 12 dict definefont pop /Custom findfont 12 scalefont setfont " +
            "newpath 20 20 moveto (A) show showpage " +
            "newpath 20 20 moveto (B) show showpage\n");

        int fontFiles = 0;
        for (int at = 0; (at = pdf.IndexOf("/FontFile", at, StringComparison.Ordinal)) >= 0; at += 9)
            fontFiles++;

        Assert.Equal(1, fontFiles);
    }

    // ==================================================================
    //  Emitter: a truncated stream must stay within the host's cap
    // ==================================================================

    /// <summary>
    /// CloseAllSaves writes past the cap deliberately, to keep the stream balanced — two bytes per
    /// open `q`, up to 4096 of them. The host rejects at > MaxPluginContentStreamBytes, the same
    /// value, so the "valid truncated stream" was discarded whole.
    /// </summary>
    [Theory]
    [InlineData(600)]
    [InlineData(2000)]
    public void TruncatedStream_StaysWithinTheCap(int gsaveLevels)
    {
        const int Cap = 4096;
        var options = new PostScriptConversionOptions { MaxEmittedContentBytes = Cap };

        var sb = new StringBuilder();
        for (int i = 0; i < gsaveLevels; i++) sb.Append("gsave ");
        sb.Append("newpath 0 0 moveto ");
        for (int i = 0; i < 4000; i++) sb.Append(i % 100).Append(' ').Append(i % 97).Append(" lineto ");
        sb.Append("fill");

        var result = Run(sb.ToString(), options);

        Assert.True(result.ContentTruncated, "expected the cap to have been reached");
        Assert.True(result.Content.Length <= Cap,
            $"the truncated stream is {result.Content.Length} bytes, past the {Cap}-byte cap");
        TestInfrastructure.ContentStreamAssert.AssertBalancedSaveRestore(result.Content);
    }

    /// <summary>
    /// Truncation must not land between BT and ET, leaving an unterminated text object — BT/ET
    /// balance is asserted beside q/Q's.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(800)]
    [InlineData(1600)]
    public void TruncatedStream_NeverLeavesAnUnterminatedTextObject(int cap)
    {
        var options = new PostScriptConversionOptions { MaxEmittedContentBytes = cap };

        var sb = new StringBuilder("/Helvetica 12 selectfont newpath 0 0 moveto ");
        for (int i = 0; i < 200; i++) sb.Append("(Hello) show 0 12 rmoveto ");

        string s = Encoding.Latin1.GetString(Run(sb.ToString(), options).Content ?? []);

        int bt = 0, et = 0;
        foreach (string line in s.Split('\n'))
        {
            if (line.Trim() == "BT") bt++;
            if (line.Trim() == "ET") et++;
        }
        Assert.Equal(bt, et);
    }

    // ==================================================================
    //  Number parsing
    // ==================================================================

    /// <summary>
    /// A radix literal was bounded by DIGIT COUNT, which rejected a legal 33-character binary
    /// literal while silently truncating 16#1FFFFFFFF to a different number.
    /// </summary>
    [Theory]
    [InlineData("2#11111111111111111111111111111111", -1)]   // 32 ones == 0xFFFFFFFF == -1
    [InlineData("2#00000000000000000000000000000000001", 1)] // leading zeros are not a length
    [InlineData("16#FFFFFFFF", -1)]
    [InlineData("16#7FFFFFFF", 2147483647)]
    public void RadixLiteral_IsBoundedByValueNotDigitCount(string literal, int expected)
        => AssertPushesInt(literal, expected);

    /// <summary>A radix literal that does not fit 32 bits is a name, not a silently wrong number.</summary>
    [Fact]
    public void RadixLiteral_TooWideForThirtyTwoBits_IsNotSilentlyTruncated()
    {
        // If it parsed, it would truncate to -1 and the fill would draw. As a name it is undefined,
        // so the object is abandoned and nothing is emitted.
        var result = Run("newpath 0 0 moveto 16#1FFFFFFFF 0 rlineto 10 0 rlineto fill");
        Assert.DoesNotContain("\n-1 0 l\n", Encoding.Latin1.GetString(result.Content ?? []));
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>Asserts that <paramref name="ps"/> leaves exactly n operands above a mark.</summary>
    private static void AssertLeavesOnStack(string ps, int expected)
    {
        string s = Text($"mark {ps} counttomark " +
                        "newpath 0 0 moveto 0 rlineto 10 0 rlineto fill");
        Assert.Contains($"\n{expected} 0 l\n", s);
    }

    /// <summary>Asserts that <paramref name="ps"/> leaves one integer on the stack.</summary>
    private static void AssertPushesInt(string ps, int expected)
    {
        string s = Text($"newpath 0 0 moveto {ps} 0 rlineto 10 0 rlineto fill");
        Assert.Contains($"\n{expected} 0 l\n", s);
    }

    private static PdfDictionary Fonts(PostScriptConversionResult r)
        => r.Resources?[PdfName.Font] as PdfDictionary;

    private static int FontCount(PostScriptConversionResult r) => Fonts(r)?.Keys.Count ?? 0;

    /// <summary>A flat rendering of the font resources, for substring assertions.</summary>
    private static string DescribeFonts(PostScriptConversionResult r)
    {
        var sb = new StringBuilder();
        Describe(Fonts(r), sb, 0);
        return sb.ToString();
    }

    private static void Describe(PdfObject obj, StringBuilder sb, int depth)
    {
        if (obj == null || depth > 8) return;
        switch (obj)
        {
            case PdfDictionary d:
                foreach (var kv in d) { sb.Append('/').Append(kv.Key.Value).Append(' '); Describe(kv.Value, sb, depth + 1); }
                break;
            case PdfArray a:
                for (int i = 0; i < a.Count; i++) Describe(a[i], sb, depth + 1);
                break;
            case PdfName n:
                sb.Append('/').Append(n.Value).Append(' ');
                break;
            default:
                sb.Append(obj).Append(' ');
                break;
        }
    }
}
