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
/// <c>[/ICCBased dict]</c> (PostScript 3) and <c>[/CalGray dict]</c>, <c>[/CalRGB dict]</c>,
/// <c>[/Lab dict]</c> carried as PDF writes them: an image in the space, a colour painted there with
/// <c>cs</c>/<c>scn</c>. The ICC space was refused — its <c>/N</c> was "in a stream this converter cannot
/// read" — and the Cal spaces travelled as their device analogues, a Lab colour not at all.
/// </summary>
public sealed class IccAndCieSpaceTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private static string[] Ops(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()).ToArray();

    private static PdfStream OnlyImage(PostScriptConversionResult r)
        => r.Resources?[PdfName.XObject] is PdfDictionary x && x.Count == 1 ? (PdfStream)x.Single().Value : null;

    private static PdfDictionary ColorSpaces(PostScriptConversionResult r)
        => r.Resources?[PdfName.ColorSpace] as PdfDictionary;

    /// <summary>
    /// The smallest bytes that are an ICC.1 profile header: 128 bytes, the data colour space at offset
    /// 16, <c>acsp</c> at 36, and a tag count of 0 after them.
    /// </summary>
    private static byte[] Profile(string dataColourSpace)
    {
        var p = new byte[132];
        p[3] = 132;                                          // size, big-endian
        Encoding.ASCII.GetBytes("mntr").CopyTo(p, 12);
        Encoding.ASCII.GetBytes(dataColourSpace).CopyTo(p, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(p, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(p, 36);
        p[8] = 0x02; p[9] = 0x10;                            // version 2.1
        return p;
    }

    private static string Hex(byte[] bytes) => "<" + string.Concat(bytes.Select(b => b.ToString("X2"))) + ">";

    private static string Icc(int n, string signature, string extra = "")
        => $"[/ICCBased << /N {n} /DataSource {Hex(Profile(signature))} {extra}>>]";

    // ------------------------------------------------------------------
    //  ICCBased
    // ------------------------------------------------------------------

    /// <summary>An image in an ICC space is carried in it: <c>[/ICCBased stream]</c>, its <c>/N</c> and profile.</summary>
    [Fact]
    public void AnIccImage_IsCarriedInItsProfile()
    {
        var image = OnlyImage(Convert(Icc(3, "RGB ") + " setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8000> >> image"));
        var space = Assert.IsType<PdfArray>(image[PdfName.ColorSpace]);
        Assert.Equal("ICCBased", ((PdfName)space[0]).Value);
        var profile = Assert.IsType<PdfStream>(space[1]);
        Assert.Equal(3, ((PdfNumber)profile[PdfName.N]).IntValue);
        Assert.Equal(Profile("RGB "), profile.RawData);
        Assert.Equal(new byte[] { 0xFF, 0x80, 0x00 }, image.RawData);
    }

    /// <summary>A colour in an ICC space is painted there: the space named, its components through <c>scn</c>.</summary>
    [Fact]
    public void AnIccColour_IsPaintedInItsSpace()
    {
        var r = Convert(Icc(3, "RGB ") + " setcolorspace 0.2 0.4 0.6 setcolor" + Marker);
        var ops = Ops(r);
        int cs = System.Array.FindIndex(ops, o => o.EndsWith(" cs", System.StringComparison.Ordinal));
        Assert.True(cs >= 0);
        Assert.Equal("0.2 0.4 0.6 scn", ops[cs + 1]);
        string name = ops[cs].Split(' ')[0].TrimStart('/');
        var space = Assert.IsType<PdfArray>(ColorSpaces(r)[new PdfName(name)]);
        Assert.Equal("ICCBased", ((PdfName)space[0]).Value);
        Assert.DoesNotContain(ops, o => o.EndsWith(" rg", System.StringComparison.Ordinal));
    }

    /// <summary>The profile may come from a file <c>currentfile</c> made, the program resuming after its block.</summary>
    [Fact]
    public void AnIccProfileFromAFile_IsReadAtSetcolorspace()
    {
        string hex = Hex(Profile("GRAY")).Trim('<', '>');
        var r = Convert("[/ICCBased << /N 1 /DataSource currentfile /ASCIIHexDecode filter >>] setcolorspace\n" + hex + ">\n" +
                        "<< /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <7F> >> image 0.25 setgray" + Marker);
        var space = Assert.IsType<PdfArray>(OnlyImage(r)[PdfName.ColorSpace]);
        Assert.Equal(Profile("GRAY"), ((PdfStream)space[1]).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>The same dictionary set again is the same space: one resource, however often it is set.</summary>
    [Fact]
    public void TheSameIccDictionary_IsOneResource()
    {
        var r = Convert("/cs " + Icc(1, "GRAY") + " def cs setcolorspace 0.5 setcolor" + Marker +
                        " cs setcolorspace 0.25 setcolor" + Marker);
        Assert.Equal(1, ColorSpaces(r).Count);
        Assert.Contains("0.25 scn", Ops(r));
    }

    /// <summary>
    /// A profile that describes another number of components than <c>/N</c>, or bytes that are no
    /// profile, are refused — a reader would refuse the space — and an image in it is skipped; the
    /// right profile is the control.
    /// </summary>
    [Theory]
    [InlineData("RGB ", true, false)]
    [InlineData("CMYK", false, true)]
    [InlineData("XXXX", false, true)]
    public void AnIccSpace_IsCarriedOnlyWhenItsProfileDescribesN(string signature, bool carried, bool refused)
    {
        var r = Convert(Icc(3, signature) + " setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8000> >> image");
        Assert.Equal(carried, OnlyImage(r) != null);
        Assert.Equal(refused, (r.LastError ?? "").Contains("ICCBased", System.StringComparison.Ordinal));
    }

    /// <summary>A file of no profile header at all is refused by name.</summary>
    [Fact]
    public void ANonProfile_IsRefusedByName()
    {
        var r = Convert("[/ICCBased << /N 3 /DataSource <00010203> >>] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8000> >> image");
        Assert.Null(OnlyImage(r));
        Assert.Contains("is not an ICC profile", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>An ICC colour reads back as the device colour of its count: three components as RGB.</summary>
    [Fact]
    public void AnIccColour_ReadsBackAsItsDeviceAnalogue()
        => Assert.Contains("f", Ops(Convert(Icc(3, "RGB ") + " setcolorspace 0.2 0.4 0.6 setcolor currentrgbcolor" +
                                            " 0.6 eq exch 0.4 eq and exch 0.2 eq and {" + Marker + "} if")));

    /// <summary>An ICC colour is held to the space's <c>/Range</c>, default 0 to 1.</summary>
    [Fact]
    public void AnIccColour_IsHeldToItsRange()
        => Assert.Contains("1 0 0.5 scn", Ops(Convert(Icc(3, "RGB ") + " setcolorspace 2 -1 0.5 setcolor" + Marker)));

    /// <summary>
    /// The bytes are believed only behind ICC.1's <c>acsp</c> signature: a data colour space at offset
    /// 16 with no signature at 36 is no profile.
    /// </summary>
    [Fact]
    public void AProfileWithoutItsSignature_IsRefused()
    {
        byte[] unsigned = Profile("RGB ");
        unsigned[36] = (byte)'x';
        var r = Convert("[/ICCBased << /N 3 /DataSource " + Hex(unsigned) + " >>] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8000> >> image");
        Assert.Null(OnlyImage(r));
        Assert.Contains("is not an ICC profile", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/Range</c> is carried, and a colour is held to it, not to 0..1: two colours past 1 that
    /// read back as the same device white are still two colours.
    /// </summary>
    [Fact]
    public void AnIccRange_IsCarried_AndHoldsTheColour()
    {
        var r = Convert(Icc(3, "RGB ", "/Range [0 4 0 4 0 4] ") + " setcolorspace 2 2 2 setcolor" + Marker + " 3 3 3 setcolor" + Marker +
                        " << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8000> >> image");
        var ops = Ops(r);
        Assert.Contains("2 2 2 scn", ops);
        Assert.Contains("3 3 3 scn", ops);
        var profile = (PdfStream)((PdfArray)OnlyImage(r)[PdfName.ColorSpace])[1];
        Assert.Equal(new double[] { 0, 4, 0, 4, 0, 4 }, ((PdfArray)profile[PdfName.Range]).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray());
    }

    /// <summary>A Cal white point whose Y is not 1 is no white point (§8.6.5.2): the space travels as its device analogue.</summary>
    [Fact]
    public void ACalWhitePointOfAnotherY_IsTheDeviceAnalogue()
        => Assert.Contains("1 0 0 rg", Ops(Convert("[/CalRGB << /WhitePoint [0.95 0.5 1.09] >>] setcolorspace 1 0 0 setcolor" + Marker)));

    /// <summary>A device <c>/Alternate</c> of the profile's count is carried with it.</summary>
    [Fact]
    public void ADeviceAlternate_IsCarried()
    {
        var image = OnlyImage(Convert(Icc(3, "RGB ", "/Alternate /DeviceRGB ") + " setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8000> >> image"));
        var profile = (PdfStream)((PdfArray)image[PdfName.ColorSpace])[1];
        Assert.Equal("DeviceRGB", ((PdfName)profile[new PdfName("Alternate")]).Value);
    }

    // ------------------------------------------------------------------
    //  CalGray, CalRGB, Lab
    // ------------------------------------------------------------------

    private const string CalRgb =
        "[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [1.8 1.8 1.8] /Matrix [0.4497 0.2446 0.0252 0.3163 0.672 0.1412 0.1845 0.0833 0.9227] >>]";

    /// <summary>A CalRGB image is carried in its own space, the dictionary's keys as written.</summary>
    [Fact]
    public void ACalRgbImage_IsCarriedAsWritten()
    {
        var image = OnlyImage(Convert(CalRgb + " setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8000> >> image"));
        var space = Assert.IsType<PdfArray>(image[PdfName.ColorSpace]);
        Assert.Equal("CalRGB", ((PdfName)space[0]).Value);
        var dict = Assert.IsType<PdfDictionary>(space[1]);
        Assert.Equal(new[] { 0.9505, 1, 1.089 }, ((PdfArray)dict[PdfName.WhitePoint]).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray());
        Assert.Equal(9, ((PdfArray)dict[PdfName.Matrix]).Count);
        Assert.Equal(3, ((PdfArray)dict[PdfName.Gamma]).Count);
    }

    /// <summary>A CalRGB colour is painted in its space; a bare <c>/CalRGB</c>, which has no white point, is the device control.</summary>
    [Fact]
    public void ACalRgbColour_IsPaintedInItsSpace()
    {
        Assert.Contains("1 0 0 scn", Ops(Convert(CalRgb + " setcolorspace 1 0 0 setcolor" + Marker)));
        Assert.Contains("1 0 0 rg", Ops(Convert("/CalRGB setcolorspace 1 0 0 setcolor" + Marker)));
    }

    private const string Lab = "[/Lab << /WhitePoint [0.9642 1 0.8249] /Range [-128 127 -128 127] >>]";

    /// <summary>A Lab colour is painted in Lab, its components as they are within the ranges — not clipped to 0..1.</summary>
    [Fact]
    public void ALabColour_IsPaintedInLab_WithinItsRanges()
    {
        var ops = Ops(Convert(Lab + " setcolorspace 50 20 -30 setcolor" + Marker));
        Assert.Contains("50 20 -30 scn", ops);
        Assert.Contains("100 127 -128 scn", Ops(Convert(Lab + " setcolorspace 150 300 -300 setcolor" + Marker)));
    }

    /// <summary>A Lab colour reads back through XYZ as sRGB: L 100 with no chroma is white.</summary>
    [Fact]
    public void ALabColour_ReadsBackAsSrgb()
        => Assert.Contains("f", Ops(Convert(Lab + " setcolorspace 100 0 0 setcolor currentrgbcolor" +
                                            " 0.99 gt exch 0.99 gt and exch 0.99 gt and {" + Marker + "} if")));

    /// <summary>A Lab image is carried in Lab; a Lab space with no white point is refused, as a bare <c>/Lab</c> is.</summary>
    [Fact]
    public void ALabImage_IsCarried_ALabWithoutAWhitePointIsNot()
    {
        var image = OnlyImage(Convert(Lab + " setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8080> >> image"));
        Assert.Equal("Lab", ((PdfName)((PdfArray)image[PdfName.ColorSpace])[0]).Value);
        Assert.Null(OnlyImage(Convert("[/Lab << /Range [-128 127 -128 127] >>] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <FF8080> >> image")));
    }

    /// <summary>An indexed palette over Lab spans Lab's ranges: byte 255 is L 100, byte 0 the bottom of a and b.</summary>
    [Fact]
    public void AnIndexedPaletteOverLab_SpansItsRanges()
        => Assert.Contains("100 -128 -128 scn", Ops(Convert("[/Indexed " + Lab + " 0 <FF0000>] setcolorspace 0 setcolor" + Marker)));

}

/// <summary>The colour space cap, in the collection every test writing a limit joins.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class ColorSpaceCapTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static string Icc() =>
        "[/ICCBased << /N 3 /DataSource <" + string.Concat(Profile().Select(b => b.ToString("X2"))) + "> >>]";

    private static byte[] Profile()
    {
        var p = new byte[132];
        p[3] = 132;
        Encoding.ASCII.GetBytes("RGB ").CopyTo(p, 16);
        Encoding.ASCII.GetBytes("acsp").CopyTo(p, 36);
        return p;
    }

    /// <summary>
    /// Past <see cref="PsLimits.MaxColorSpaceResources"/> a colour in a space not yet named is painted
    /// in its device approximation, said once; the space already named is painted there still.
    /// </summary>
    [Fact]
    public void PastTheColourSpaceCap_AColourIsPaintedInItsDeviceAnalogue()
    {
        try
        {
            PsLimits.MaxColorSpaceResources = 1;
            var ops = ContentStreamAssert.Parse(PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                    Icc() + " setcolorspace 0.2 0.4 0.6 setcolor" + Marker + " " + Icc() + " setcolorspace 0.1 0.2 0.3 setcolor" + Marker)).Content)
                .Select(o => o.ToString()).ToArray();
            Assert.Contains("0.2 0.4 0.6 scn", ops);
            Assert.Contains("0.1 0.2 0.3 rg", ops);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
