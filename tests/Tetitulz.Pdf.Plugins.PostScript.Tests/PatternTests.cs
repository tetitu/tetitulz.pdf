// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>makepattern</c> and <c>setpattern</c> (PLRM §4.9) as PDF patterns: a tiling pattern's cell drawn
/// by its <c>PaintProc</c> into a pattern stream, a shading pattern through the shading converter, the
/// pattern matrix under the CTM at <c>makepattern</c>, and a fill or stroke painted with
/// <c>/Pattern cs /Pn scn</c>. Both operators used to be arity-table entries: the fill painted in
/// whatever colour came before.
/// </summary>
public sealed class PatternTests
{
    private const string Square = " newpath 10 10 moveto 90 10 lineto 90 90 lineto closepath";

    private static string Tiling(int paintType, string paintProc) =>
        $"<< /PatternType 1 /PaintType {paintType} /TilingType 2 /BBox [0 0 10 10] /XStep 10 /YStep 10" +
        $" /PaintProc {{ pop {paintProc} }} >>";

    private const string RedCell = "1 0 0 setrgbcolor 0 0 moveto 5 0 lineto 5 5 lineto closepath fill";

    private static PostScriptConversionResult Convert(string postScript)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript));

    private static string[] Ops(byte[] content) => ContentStreamAssert.Parse(content).Select(o => o.ToString()).ToArray();

    private static bool InOrder(string[] ops, params string[] expected)
    {
        int at = 0;
        foreach (string op in ops)
            if (at < expected.Length && op == expected[at]) at++;
        return at == expected.Length;
    }

    private static PdfObject Resource(PostScriptConversionResult result, PdfName category, string name)
        => ((PdfDictionary)result.Resources[category])[new PdfName(name)];

    private static double[] Numbers(PdfObject array)
        => ((PdfArray)array).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray();

    [Fact]
    public void AColouredTilingPattern_IsAPatternStream_AndTheFillPaintsWithIt()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(Tiling(1, RedCell) + " matrix makepattern setpattern" + Square + " fill");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/Pattern cs", "/P1 scn", "f"), string.Join(" | ", ops));

        var pattern = Assert.IsType<PdfStream>(Resource(result, PdfName.Pattern, "P1"));
        Assert.Equal(1, ((PdfNumber)pattern[PdfName.PatternType]).IntValue);
        Assert.Equal(1, ((PdfNumber)pattern[PdfName.PaintType]).IntValue);
        Assert.Equal(2, ((PdfNumber)pattern[PdfName.TilingType]).IntValue);
        Assert.Equal(new[] { 0.0, 0, 10, 10 }, Numbers(pattern[PdfName.BBox]));
        Assert.Equal(10, ((PdfNumber)pattern[PdfName.XStep]).DoubleValue);
        Assert.Equal(10, ((PdfNumber)pattern[PdfName.YStep]).DoubleValue);
        Assert.Null(pattern[PdfName.Matrix]);                                  // identity CTM, identity matrix
        Assert.IsAssignableFrom<PdfDictionary>(pattern[PdfName.Resources]);
        string[] cell = Ops(pattern.RawData);
        Assert.True(InOrder(cell, "1 0 0 rg", "f"), string.Join(" | ", cell));
        Assert.DoesNotContain("makepattern", PostScriptDiagnostics.UnimplementedOperators);
        Assert.DoesNotContain("setpattern", PostScriptDiagnostics.UnimplementedOperators);
    }

    /// <summary>The pattern matrix is the program's under the CTM at makepattern: <c>2 0 0 2 10 20</c> after <c>5 5 translate</c>.</summary>
    [Fact]
    public void ThePatternMatrix_IsTheProgramsUnderTheCtmAtMakepattern()
    {
        var result = Convert("5 5 translate " + Tiling(1, RedCell) + " [2 0 0 2 10 20] makepattern" +
                             " 100 100 translate setpattern" + Square + " fill");
        var pattern = (PdfStream)Resource(result, PdfName.Pattern, "P1");
        Assert.Equal(new[] { 2.0, 0, 0, 2, 15, 25 }, Numbers(pattern[PdfName.Matrix]));
    }

    /// <summary>
    /// An uncoloured pattern is painted in the colour setpattern names over the current space —
    /// <c>[/Pattern /DeviceRGB]</c> after <c>setrgbcolor</c> — and its cell writes no colour, even the one
    /// its PaintProc sets.
    /// </summary>
    [Fact]
    public void AnUncolouredPattern_IsPaintedInTheColourSetpatternNames_ItsCellWritingNone()
    {
        var result = Convert("0 0 0 setrgbcolor 0 1 0 " + Tiling(2, RedCell + " 1 0 0 setrgbcolor 0 0 moveto 10 10 lineto stroke") +
                             " matrix makepattern setpattern" + Square + " fill");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/CsP1 cs", "0 1 0 /P1 scn", "f"), string.Join(" | ", ops));

        var space = (PdfArray)Resource(result, PdfName.ColorSpace, "CsP1");
        Assert.Equal("Pattern", ((PdfName)space[0]).Value);
        Assert.Equal("DeviceRGB", ((PdfName)space[1]).Value);

        var pattern = (PdfStream)Resource(result, PdfName.Pattern, "P1");
        Assert.Equal(2, ((PdfNumber)pattern[PdfName.PaintType]).IntValue);
        string[] cell = Ops(pattern.RawData);
        Assert.Contains("f", cell);                                             // the cell still paints
        Assert.Contains("S", cell);
        Assert.DoesNotContain(cell, o => o.EndsWith(" rg") || o.EndsWith(" g") || o.EndsWith(" k")
                                         || o.EndsWith(" RG") || o.EndsWith(" G") || o.EndsWith(" K"));
    }

    /// <summary>Two uncoloured patterns over one space share its <c>[/Pattern base]</c> resource; another base gets its own.</summary>
    [Fact]
    public void UncolouredPatternsOverOneSpace_ShareItsPatternSpace()
    {
        var result = Convert("/U " + Tiling(2, RedCell) + " matrix makepattern def" +
                             " 0 0 0 setrgbcolor 0 1 0 U setpattern" + Square + " fill" +
                             " 0 0 1 U setpattern" + Square + " fill" +
                             " 0 setgray 0.5 U setpattern" + Square + " fill");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "0 1 0 /P1 scn", "f", "0 0 1 /P1 scn", "f", "/CsP2 cs", "0.5 /P1 scn", "f"), string.Join(" | ", ops));
        var spaces = (PdfDictionary)result.Resources[PdfName.ColorSpace];
        Assert.Equal(2, spaces.Count);
        Assert.Equal("DeviceGray", ((PdfName)((PdfArray)spaces[new PdfName("CsP2")])[1]).Value);
    }

    /// <summary>
    /// The pattern matrix is expressed against the stream's own space: under a host's base matrix
    /// a pattern made at the initial CTM needs no <c>/Matrix</c>, as one made in a plain conversion does not.
    /// </summary>
    [Fact]
    public void UnderABaseMatrix_APatternMadeAtTheInitialCtm_HasNoMatrix()
    {
        var options = new PostScriptConversionOptions { BaseMatrix = new double[] { 2, 0, 0, 2, 100, 0 } };
        var result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            Tiling(1, RedCell) + " matrix makepattern setpattern" + Square + " fill"), options);
        Assert.Null(((PdfStream)Resource(result, PdfName.Pattern, "P1"))[PdfName.Matrix]);

        var moved = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "3 0 translate " + Tiling(1, RedCell) + " matrix makepattern setpattern" + Square + " fill"), options);
        Assert.Equal(new[] { 1.0, 0, 0, 1, 3, 0 }, Numbers(((PdfStream)Resource(moved, PdfName.Pattern, "P1"))[PdfName.Matrix]));
    }

    /// <summary>A cell made while a pattern is the colour starts in that pattern's base colour, never the pattern, whose resource is not the cell's.</summary>
    [Fact]
    public void ACellMadeUnderAPatternColour_StartsInItsBaseColour()
    {
        var result = Convert(Tiling(1, RedCell) + " matrix makepattern setpattern " +
                             Tiling(1, "0 0 moveto 5 0 lineto 5 5 lineto closepath fill") + " matrix makepattern setpattern" + Square + " fill");
        string[] cell = Ops(((PdfStream)Resource(result, PdfName.Pattern, "P2")).RawData);
        Assert.True(InOrder(cell, "0 g", "f"), string.Join(" | ", cell));
        Assert.DoesNotContain(cell, o => o.EndsWith(" scn"));
    }

    /// <summary>A pattern whose matrix is singular under the CTM is not built, and says why.</summary>
    [Fact]
    public void APatternWithASingularMatrix_IsNotBuilt()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert("0 0 1 setrgbcolor " + Tiling(1, RedCell) + " [0 0 0 0 0 0] makepattern" + Square + " fill");
        Assert.Null(result.Resources?[PdfName.Pattern]);
        Assert.Contains("singular", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
        var control = Convert(Tiling(1, RedCell) + " [1 0 0 1 0 0] makepattern pop");
        Assert.Null(control.LastError);
    }

    /// <summary>A shading pattern in a bare <c>/CalRGB</c> travels as DeviceRGB, said once however many are made.</summary>
    [Fact]
    public void ABareCalRgbShadingPattern_IsSaidOnce()
    {
        PostScriptDiagnostics.Clear();
        const string calRgb =
            "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /CalRGB /Coords [0 0 1 0]" +
            "   /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >> matrix makepattern pop ";
        Convert(calRgb + calRgb);
        Assert.Equal(1, PostScriptDiagnostics.ReportCount);
        Assert.Contains("device analogue", PostScriptDiagnostics.LastError, System.StringComparison.Ordinal);
    }

    /// <summary><c>setcolor</c> in <c>[/Pattern /DeviceGray]</c>: the grey below the pattern.</summary>
    [Fact]
    public void SetcolorInAPatternSpaceOverGrey_TakesTheGreyBelowThePattern()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert("[/Pattern /DeviceGray] setcolorspace 0.5 " + Tiling(2, RedCell) +
                             " matrix makepattern setcolor" + Square + " fill");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/CsP1 cs", "0.5 /P1 scn", "f"), string.Join(" | ", ops));
        Assert.Equal("DeviceGray", ((PdfName)((PdfArray)Resource(result, PdfName.ColorSpace, "CsP1"))[1]).Value);
        Assert.DoesNotContain("setcolorspace", PostScriptDiagnostics.UnimplementedOperators);
        Assert.Null(result.LastError);
    }

    /// <summary><c>/Pattern setcolorspace … setcolor</c>, and a stroke painted with the pattern.</summary>
    [Fact]
    public void APatternSetBySetcolor_PaintsAStroke()
    {
        var result = Convert("/Pattern setcolorspace " + Tiling(1, RedCell) + " matrix makepattern setcolor" + Square + " stroke");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/Pattern CS", "/P1 SCN", "S"), string.Join(" | ", ops));
    }

    /// <summary>
    /// An uncoloured pattern's components are in the current space: after <c>/DeviceGray
    /// setcolorspace</c> and then <c>setrgbcolor</c>, RGB — one grey component would leave two on the stack.
    /// </summary>
    [Fact]
    public void AnUncolouredPatternAfterSetrgbcolor_TakesRgbComponents()
    {
        var result = Convert("/DeviceGray setcolorspace 0 0 0 setrgbcolor 0 1 0 " + Tiling(2, RedCell) +
                             " matrix makepattern setpattern" + Square + " fill count 0 eq { } { 0 0 moveto 1 0 lineto 1 1 lineto closepath stroke } ifelse");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/CsP1 cs", "0 1 0 /P1 scn", "f"), string.Join(" | ", ops));
        Assert.DoesNotContain("S", ops);                                        // nothing was left on the stack
        Assert.Equal("DeviceRGB", ((PdfName)((PdfArray)Resource(result, PdfName.ColorSpace, "CsP1"))[1]).Value);
    }

    /// <summary>
    /// <c>setpattern</c> makes the space a Pattern space, so a <c>setcolor</c> after it takes another
    /// pattern; and an uncoloured one after <c>[/Pattern /DeviceCMYK] setcolorspace</c> takes CMYK.
    /// </summary>
    [Fact]
    public void AfterSetpattern_SetcolorTakesAPattern_AndAPatternSpacesBaseIsTheUnderlyingOne()
    {
        var result = Convert("/A " + Tiling(1, RedCell) + " matrix makepattern def /B " + Tiling(1, RedCell) +
                             " matrix makepattern def A setpattern B setcolor" + Square + " fill" +
                             " [/Pattern /DeviceCMYK] setcolorspace 0 0 0 1 " + Tiling(2, RedCell) + " matrix makepattern setpattern" + Square + " fill");
        string[] ops = Ops(result.Content);
        // A is registered by its setpattern (P1), B by the setcolor after it (P2), which paints.
        Assert.True(InOrder(ops, "/P2 scn", "f", "0 0 0 1 /P3 scn", "f"), string.Join(" | ", ops));
        Assert.DoesNotContain("/P1 scn", ops);
        Assert.Equal("DeviceCMYK", ((PdfName)((PdfArray)Resource(result, PdfName.ColorSpace, "CsP1"))[1]).Value);
    }

    /// <summary>
    /// An uncoloured pattern over an indexed space takes one index, painted as its palette entry:
    /// index 1 of a red–green palette is green, over <c>[/Pattern /DeviceRGB]</c>.
    /// </summary>
    [Fact]
    public void AnUncolouredPatternOverAnIndexedSpace_TakesAnIndex()
    {
        var result = Convert("[/Indexed /DeviceRGB 1 <FF000000FF00>] setcolorspace 1 " + Tiling(2, RedCell) +
                             " matrix makepattern setpattern" + Square + " fill count 0 eq { } { 0 0 moveto 1 0 lineto stroke } ifelse");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/CsP1 cs", "0 1 0 /P1 scn", "f"), string.Join(" | ", ops));
        Assert.DoesNotContain("S", ops);
    }

    /// <summary>
    /// After <c>setpattern</c> and a <c>setgray</c> — which clears the Pattern space as it clears any —
    /// a grey Level 2 image is drawn, not refused as in a Pattern space.
    /// </summary>
    [Fact]
    public void AnImageAfterSetpatternAndSetgray_IsDrawn()
    {
        var result = Convert("/DeviceGray setcolorspace " + Tiling(1, RedCell) + " matrix makepattern setpattern 0 setgray" +
                             " 2 1 8 [2 0 0 1 0 0] <00FF> image");
        Assert.Contains(Ops(result.Content), o => o.EndsWith(" Do"));
        Assert.Equal("DeviceGray", ((PdfName)((PdfStream)Resource(result, PdfName.XObject, "Im1"))[PdfName.ColorSpace]).Value);
    }

    /// <summary>A shading pattern carries its shading through the shading converter.</summary>
    [Fact]
    public void AShadingPattern_CarriesItsShading()
    {
        var result = Convert(
            "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0]" +
            "   /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >>" +
            " [1 0 0 1 3 4] makepattern setpattern" + Square + " fill");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/Pattern cs", "/P1 scn", "f"), string.Join(" | ", ops));

        var pattern = Assert.IsType<PdfDictionary>(Resource(result, PdfName.Pattern, "P1"));
        Assert.Equal(2, ((PdfNumber)pattern[PdfName.PatternType]).IntValue);
        Assert.Equal(new[] { 1.0, 0, 0, 1, 3, 4 }, Numbers(pattern[PdfName.Matrix]));
        var shading = (PdfDictionary)pattern[PdfName.Shading];
        Assert.Equal(2, ((PdfNumber)shading[PdfName.ShadingType]).IntValue);
    }

    /// <summary>The cell starts in the graphics state at makepattern: the line width set before it is the cell's.</summary>
    [Fact]
    public void TheCell_StartsInTheLineStyleAtMakepattern()
    {
        var result = Convert("5 setlinewidth " + Tiling(1, "0 0 moveto 10 10 lineto stroke") + " matrix makepattern" +
                             " 1 setlinewidth setpattern" + Square + " fill");
        string[] cell = Ops(((PdfStream)Resource(result, PdfName.Pattern, "P1")).RawData);
        Assert.True(InOrder(cell, "5 w", "S"), string.Join(" | ", cell));
    }

    /// <summary>The same pattern set twice is one resource; a second pattern is another.</summary>
    [Fact]
    public void APatternSetTwice_IsOneResource()
    {
        var result = Convert("/A " + Tiling(1, RedCell) + " matrix makepattern def /B " + Tiling(1, RedCell) +
                             " matrix makepattern def A setpattern" + Square + " fill B setpattern" + Square +
                             " fill A setpattern" + Square + " fill");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "/P1 scn", "f", "/P2 scn", "f", "/P1 scn", "f"), string.Join(" | ", ops));
        Assert.Equal(2, ((PdfDictionary)result.Resources[PdfName.Pattern]).Count);
    }

    /// <summary>
    /// What makepattern refuses, it refuses with PLRM's error, which <c>stopped</c> catches — and a
    /// well-formed pattern is not refused, the control.
    /// </summary>
    [Theory]
    [InlineData("/PaintType 3", true)]
    [InlineData("/TilingType 4", true)]
    [InlineData("/XStep 0", true)]
    [InlineData("/PaintProc 5", true)]
    [InlineData("/BBox [0 0 10]", true)]
    [InlineData("/PatternType 3 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >>", true)]
    [InlineData("", false)]
    public void AMalformedPattern_IsAnError_AWellFormedOneIsNot(string replacement, bool stopped)
    {
        string dict = Tiling(1, RedCell);
        if (replacement.Length > 0)
            dict = dict.Replace(">>", replacement + " >>");     // a later key replaces the earlier one
        var result = Convert("{ " + dict + " matrix makepattern pop } stopped { newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill } if");
        Assert.Equal(stopped, Ops(result.Content).Contains("f"));
    }

    /// <summary>A shading pattern whose shading cannot be carried is not built; setting it leaves the colour, said once.</summary>
    [Fact]
    public void AnUnbuiltPattern_LeavesTheColour_SaidOnce()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(
            "/U << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace [/DeviceN [/A /B] /DeviceGray {pop pop}] /Coords [0 0 1 0]" +
            " /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >> >> matrix makepattern def" +
            " 0 0 1 setrgbcolor U setpattern" + Square + " fill U setpattern" + Square + " fill");
        string[] ops = Ops(result.Content);
        Assert.True(InOrder(ops, "0 0 1 rg", "f", "f"), string.Join(" | ", ops));
        Assert.DoesNotContain(ops, o => o.EndsWith(" scn"));
        Assert.Equal(2, PostScriptDiagnostics.ReportCount);       // the refused shading, the unbuilt set once
        Assert.Contains("did not build", PostScriptDiagnostics.LastError, System.StringComparison.Ordinal);
    }
}

/// <summary>The pattern counts, set through <see cref="PsLimits"/>.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class PatternLimitTests
{
    private const string Cell =
        "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10" +
        " /PaintProc { pop 0 0 moveto 5 0 lineto 5 5 lineto closepath fill } >>";

    private System.Action<string>? _collector;

    /// <summary>Every report while a test runs: <c>LastError</c> keeps only the last.</summary>
    private System.Collections.Generic.List<string> Collect()
    {
        var seen = new System.Collections.Generic.List<string>();
        _collector = m => { lock (seen) seen.Add(m); };
        PostScriptDiagnostics.OnEvent += _collector;
        return seen;
    }

    /// <summary>Hostile: a loop of makepatterns builds no more than <c>MaxPatterns</c>, and says so once.</summary>
    [Fact]
    public void HostileMakepatternLoop_StopsAtTheCap_AndSaysSoOnce()
    {
        try
        {
            PsLimits.MaxPatterns = 2;
            PostScriptDiagnostics.Clear();
            var seen = Collect();
            var result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "10 { " + Cell + " matrix makepattern setpattern newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill } repeat"));
            Assert.Equal(2, ((PdfDictionary)result.Resources[PdfName.Pattern]).Count);
            Assert.Equal(2, PostScriptDiagnostics.ReportCount);     // the cap once, the unbuilt set once
            Assert.Contains(seen, m => m.Contains("2-pattern cap", System.StringComparison.Ordinal));
        }
        finally
        {
            PostScriptDiagnostics.OnEvent -= _collector;
            PsLimits.ResetToDefaults();
        }
    }


    /// <summary>
    /// Hostile: a PaintProc that makes its own pattern recurses; the cells nest no deeper than
    /// <c>MaxPatternNestingDepth</c>, and the conversion finishes with the outer pattern built.
    /// </summary>
    [Fact]
    public void HostileSelfMakingPaintProc_NestsNoDeeperThanTheCap()
    {
        try
        {
            PsLimits.MaxPatternNestingDepth = 2;
            PostScriptDiagnostics.Clear();
            var seen = Collect();
            var result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "/D << /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10" +
                " /PaintProc { pop D matrix makepattern setpattern 0 0 moveto 5 0 lineto 5 5 lineto closepath fill } >> def" +
                " D matrix makepattern setpattern newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill"));
            var outer = Assert.IsType<PdfStream>(((PdfDictionary)result.Resources[PdfName.Pattern])[new PdfName("P1")]);
            // depth 1 and 2 are built; the third is refused, so the second cell paints in its plain colour
            var middle = Assert.IsType<PdfStream>(((PdfDictionary)((PdfDictionary)outer[PdfName.Resources])[PdfName.Pattern])[new PdfName("P1")]);
            Assert.Null(((PdfDictionary)middle[PdfName.Resources])[PdfName.Pattern]);
            Assert.Contains(seen, m => m.Contains("nested past 2", System.StringComparison.Ordinal));
        }
        finally
        {
            PostScriptDiagnostics.OnEvent -= _collector;
            PsLimits.ResetToDefaults();
        }
    }
}
