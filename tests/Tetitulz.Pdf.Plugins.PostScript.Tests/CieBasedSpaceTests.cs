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
/// <c>CIEBasedA</c>, <c>CIEBasedABC</c>, <c>CIEBasedDEF</c> and <c>CIEBasedDEFG</c> (PLRM §4.8.3)
/// evaluated to XYZ and carried as CIE L*a*b* against the space's white point: a colour painted in
/// <c>[/Lab …]</c>, an image's samples converted to Lab bytes. Each was refused — "needs its
/// whitepoint, range and decode procedures emitted", which nothing did — and a colour in one left the
/// colour as it was.
///
/// <para>The fixtures make the white point come out of the chain, since its Lab is known exactly
/// (L 100, a 0, b 0) whatever the white point is; each stage is then shown to act by a colour that
/// reaches the white only through it.</para>
/// </summary>
public sealed class CieBasedSpaceTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";
    private const string D50 = "/WhitePoint [0.9642 1 0.8249]";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private static string[] Ops(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()).ToArray();

    private static PdfStream OnlyImage(PostScriptConversionResult r)
        => r.Resources?[PdfName.XObject] is PdfDictionary x && x.Count == 1 ? (PdfStream)x.Single().Value : null;

    /// <summary>The Lab components the stream sets with <c>scn</c>, rounded to a hundredth.</summary>
    private static double[] LabFill(PostScriptConversionResult r)
    {
        var scn = ContentStreamAssert.Parse(r.Content).Single(o => o.Operator == "scn");
        return scn.Operands.Select(t => System.Math.Round(double.Parse(t, System.Globalization.CultureInfo.InvariantCulture), 2)).ToArray();
    }

    private static readonly double[] White = { 100, 0, 0 };

    /// <summary>ABC with no procedures and identity matrices is XYZ itself: the white point is Lab white, in a Lab space with that white point.</summary>
    [Fact]
    public void AnAbcColour_IsCarriedAsLab()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " /RangeABC [0 1 0 1 0 1] >>] setcolorspace 0.9642 1 0.8249 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
        var space = (PdfArray)((PdfDictionary)r.Resources[PdfName.ColorSpace]).Single().Value;
        Assert.Equal("Lab", ((PdfName)space[0]).Value);
        Assert.Equal(new[] { 0.9642, 1, 0.8249 },
            ((PdfArray)((PdfDictionary)space[1])[PdfName.WhitePoint]).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray());
    }

    /// <summary>The decode procedures act: half the white point doubled by <c>/DecodeABC</c> is the white.</summary>
    [Fact]
    public void DecodeAbc_IsApplied()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " /RangeABC [0 0.5 0 0.5 0 0.5] /DecodeABC [{2 mul} {2 mul} {2 mul}] >>] setcolorspace" +
                        " 0.4821 0.5 0.41245 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
    }

    /// <summary><c>/MatrixABC</c>, <c>/DecodeLMN</c> and <c>/MatrixLMN</c> each act, in that order.</summary>
    [Fact]
    public void TheLmnStage_IsApplied()
    {
        // ABC (1, 1, 1) → MatrixABC halves → LMN 0.5 → DecodeLMN doubles → 1 → MatrixLMN scales to the white.
        var r = Convert("[/CIEBasedABC << " + D50 + " /MatrixABC [0.5 0 0 0 0.5 0 0 0 0.5] /DecodeLMN [{2 mul} {2 mul} {2 mul}]" +
                        " /MatrixLMN [0.9642 0 0 0 1 0 0 0 0.8249] >>] setcolorspace 1 1 1 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
    }

    /// <summary>
    /// A sampled procedure read at NaN answers its first sample, not NaN: decoded to 1e308 and taken
    /// through +10 and −10, L is infinity minus infinity with no setcolor operand non-finite. It came
    /// out NaN, written as 0 with a report, and on .NET Framework <c>(int)NaN</c> indexed the table at
    /// <c>int.MinValue</c>.
    /// </summary>
    [Fact]
    public void AdversarialNaNIntoADecodeTable_ReadsTheFirstSample()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " /DecodeABC [{1e308 mul} {1e308 mul} {}] /MatrixABC [10 0 0 -10 1 0 0 0 1]" +
                        " /DecodeLMN [{} {} {}] >>] setcolorspace 1 1 1 setcolor" + Marker);

        double[] lab = LabFill(r);
        Assert.Equal(3, lab.Length);
        Assert.DoesNotContain("non-finite", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A component's range holds it: 2 in a range ending at 1 is 1, so 2 1 1 is the white and not a red.</summary>
    [Fact]
    public void AComponent_IsHeldToItsRange()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " /RangeLMN [0 2 0 2 0 2] /MatrixLMN [0.9642 0 0 0 1 0 0 0 0.8249] >>] setcolorspace 2 1 1 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
    }

    /// <summary>
    /// <c>/MatrixABC</c> is read as PLRM writes it, each component's row: B alone through
    /// <c>[0 0 0 Xw Yw Zw 0 0 0]</c> is the white; read by columns its L would be nothing.
    /// </summary>
    [Fact]
    public void MatrixAbc_IsReadByRows()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " /MatrixABC [0 0 0 0.9642 1 0.8249 0 0 0] >>] setcolorspace 0 1 0 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
    }

    /// <summary><c>/RangeLMN</c> holds LMN before its decoding: LMN 1 held to half the white, doubled by MatrixLMN, is the white.</summary>
    [Fact]
    public void RangeLmn_HoldsLmn()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " /RangeLMN [0 0.4821 0 0.5 0 0.41245] /MatrixLMN [2 0 0 0 2 0 0 0 2] >>] setcolorspace" +
                        " 1 1 1 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
    }

    /// <summary>The Lab is against the space's own white point: D65's white is Lab white in a Lab space with D65's white point.</summary>
    [Fact]
    public void TheLab_IsAgainstTheSpacesWhitePoint()
    {
        var r = Convert("[/CIEBasedABC << /WhitePoint [0.9505 1 1.089] /RangeABC [0 1 0 1 0 1.1] /RangeLMN [0 1 0 1 0 1.1] >>] setcolorspace 0.9505 1 1.089 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
    }

    /// <summary>A white point whose Y is not 1 is no white point: the space is refused.</summary>
    [Fact]
    public void AWhitePointOfAnotherY_IsRefused()
    {
        var r = Convert("[/CIEBasedABC << /WhitePoint [0.95 0.5 1.09] >>] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FFFFFF> >> image");
        Assert.Null(OnlyImage(r));
        Assert.Contains("WhitePoint", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A shading in a CIE space is refused even where the same space, set before, is known: its
    /// function answers the space's own components, which the Lab it is carried in is not.
    /// </summary>
    [Fact]
    public void ACieShading_IsRefused_EvenOnceTheSpaceIsKnown()
    {
        var r = Convert("/cs [/CIEBasedABC << " + D50 + " >>] def cs setcolorspace" +
                        " << /ShadingType 2 /ColorSpace cs /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >> >> shfill");
        Assert.Null(r.Resources?[PdfName.Shading]);
        Assert.Contains("carried as Lab", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>CIEBasedA: one component through <c>/MatrixA</c> to the three of LMN.</summary>
    [Fact]
    public void AnAColour_GoesThroughMatrixA()
    {
        var r = Convert("[/CIEBasedA << " + D50 + " /MatrixA [0.9642 1 0.8249] >>] setcolorspace 1 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
        var black = Convert("[/CIEBasedA << " + D50 + " /MatrixA [0.9642 1 0.8249] >>] setcolorspace 0 setcolor" + Marker);
        Assert.Equal(new double[] { 0, 0, 0 }, LabFill(black));
    }

    /// <summary>
    /// CIEBasedDEF: the colour looked up in <c>/Table</c>, whose bytes span <c>/RangeABC</c>. A 2×2×2
    /// table white at its far corner and black elsewhere gives the white only at D = E = F = 1.
    /// </summary>
    [Theory]
    [InlineData("1 1 1", 100)]
    [InlineData("1 1 0", 0)]
    public void ADefColour_IsLookedUpInItsTable(string colour, double lightness)
    {
        string table = "/Table [2 2 2 [<000000 000000 000000 000000> <000000 000000 000000 FFFFFF>]]";
        var r = Convert("[/CIEBasedDEF << " + D50 + " /RangeABC [0 0.9642 0 1 0 0.8249] " + table + " >>] setcolorspace " +
                        colour + " setcolor" + Marker);
        Assert.Equal(lightness, LabFill(r)[0]);
    }

    /// <summary>CIEBasedDEFG: four components, the table an array of arrays of strings.</summary>
    [Fact]
    public void ADefgColour_IsLookedUpInItsTable()
    {
        string s = "<000000 000000 000000 000000>";
        string last = "<000000 000000 000000 FFFFFF>";
        string table = $"/Table [2 2 2 2 [[{s} {s}] [{s} {last}]]]";
        var r = Convert("[/CIEBasedDEFG << " + D50 + " /RangeABC [0 0.9642 0 1 0 0.8249] " + table + " >>] setcolorspace 1 1 1 1 setcolor" + Marker);
        Assert.Equal(White, LabFill(r));
    }

    /// <summary>An image's samples are converted to Lab bytes — L × 2.55, a and b + 128 — in a Lab image of 8 bits.</summary>
    [Fact]
    public void AnAbcImage_IsConvertedToLab()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " /RangeABC [0 0.9642 0 1 0 0.8249] >>] setcolorspace" +
                        " << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] /DataSource <FFFFFF000000> >> image");
        var image = OnlyImage(r);
        Assert.Equal("Lab", ((PdfName)((PdfArray)image[PdfName.ColorSpace])[0]).Value);
        Assert.Equal(8, ((PdfNumber)image[PdfName.BitsPerComponent]).IntValue);
        Assert.Equal(new byte[] { 255, 128, 128, 0, 128, 128 }, image.RawData);
    }

    /// <summary>A sample below a byte is read at its depth and through the image's <c>/Decode</c>.</summary>
    [Fact]
    public void AOneBitAImage_IsReadAtItsDepth()
    {
        var r = Convert("[/CIEBasedA << " + D50 + " /MatrixA [0.9642 1 0.8249] >>] setcolorspace << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 1 /ImageMatrix [2 0 0 -1 0 1] /DataSource <80> >> image");
        Assert.Equal(new byte[] { 255, 128, 128, 0, 128, 128 }, OnlyImage(r).RawData);
        var inverted = Convert("[/CIEBasedA << " + D50 + " /MatrixA [0.9642 1 0.8249] >>] setcolorspace" +
                               " << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 1 /ImageMatrix [2 0 0 -1 0 1] /Decode [1 0] /DataSource <80> >> image");
        Assert.Equal(new byte[] { 0, 128, 128, 255, 128, 128 }, OnlyImage(inverted).RawData);
    }

    /// <summary>An indexed palette over a CIE space is converted entry by entry to the Lab it is carried in.</summary>
    [Fact]
    public void AnIndexedPaletteOverCie_IsConvertedToLab()
    {
        var r = Convert("[/Indexed [/CIEBasedABC << " + D50 + " /RangeABC [0 0.9642 0 1 0 0.8249] >>] 1 <000000 FFFFFF>] setcolorspace" +
                        " << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] /DataSource <0001> >> image");
        var space = (PdfArray)OnlyImage(r)[PdfName.ColorSpace];
        Assert.Equal("Lab", ((PdfName)((PdfArray)space[1])[0]).Value);
        Assert.Equal(new byte[] { 0, 128, 128, 255, 128, 128 }, ((PdfString)space[3]).RawBytes);
    }

    /// <summary>A CIE colour reads back as the sRGB of its XYZ: the white point is white.</summary>
    [Fact]
    public void ACieColour_ReadsBackAsSrgb()
        => Assert.Contains("f", Ops(Convert("[/CIEBasedABC << " + D50 + " >>] setcolorspace 0.9642 1 0.8249 setcolor currentrgbcolor" +
                                            " 0.99 gt exch 0.99 gt and exch 0.99 gt and {" + Marker + "} if")));

    /// <summary>A space with no white point, or a decode procedure that gives no number, is refused; an image in it is skipped.</summary>
    [Theory]
    [InlineData("/RangeABC [0 1 0 1 0 1]")]
    [InlineData("/WhitePoint [0.9642 1 0.8249] /DecodeABC [{pop (x)} {} {}]")]
    public void AnUnusableCieSpace_IsRefused(string entries)
    {
        var r = Convert("[/CIEBasedABC << " + entries + " >>] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FFFFFF> >> image");
        Assert.Null(OnlyImage(r));
        Assert.Contains("CIEBasedABC", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A CIE image with a colour key is skipped: the key names samples the converted image does not have.</summary>
    [Fact]
    public void ACieImageWithAColourKey_IsSkipped()
    {
        var r = Convert("[/CIEBasedABC << " + D50 + " >>] setcolorspace << /ImageType 4 /Width 1 /Height 1 /BitsPerComponent 8" +
                        " /ImageMatrix [1 0 0 -1 0 1] /DataSource <FFFFFF> /MaskColor [255 255 255] >> image");
        Assert.Null(OnlyImage(r));
        Assert.Contains("MaskColor", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A decode procedure is run once per table entry when the space is set, charged to the
    /// program's budget: one that loops runs the conversion out of budget rather than hanging it.
    /// </summary>
    [Fact]
    public void AHostileDecodeProcedure_SpendsTheBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };
        var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "[/CIEBasedA << " + D50 + " /DecodeA { 1000 { 1 pop } repeat } >>] setcolorspace 1 setcolor" + Marker), options);
        Assert.True(r.BudgetExhausted);
    }

    /// <summary>
    /// <b>Adversarial: a <c>CIEBasedDEF</c> space set in a loop, its 64³ table's strings the wrong
    /// length.</b> The lattice — 786 KB — was made before the strings were checked, and dropped, every
    /// time, uncharged; the loop ran on to the operation budget. It is charged to the allocation budget
    /// before it is made, so ten lattices' allowance stops the loop with a limitcheck. The space is made
    /// once, before the loop, so the lattice is all a pass allocates.
    /// </summary>
    [Fact]
    public void AdversarialTableOfTheWrongStrings_IsChargedBeforeItIsMade()
    {
        const long Lattice = 64L * 64 * 64 * 3;
        string strings = string.Concat(Enumerable.Repeat("(x) ", 64));
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 10 * Lattice, MaxTotalOps = 200_000 };
        var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "/S [/CIEBasedDEF << " + D50 + " /Table [64 64 64 [" + strings + "]] >>] def { S setcolorspace } loop"), options);

        Assert.Contains("limitcheck", r.LastError ?? "", System.StringComparison.Ordinal);
    }
}
