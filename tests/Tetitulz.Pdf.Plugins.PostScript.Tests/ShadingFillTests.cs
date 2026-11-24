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
/// <c>shfill</c> (PLRM §4.9.3) as PDF's <c>sh</c>: the shading dictionary carried key for key, its
/// function dictionaries with it, a mesh given as numbers packed, a separation written as one with its
/// tint transform sampled — not consumed and painted as nothing, which would leave every Illustrator
/// gradient blank.
/// </summary>
public sealed class ShadingFillTests
{
    private const string Axial =
        "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true false]" +
        " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>";

    private static PostScriptConversionResult Convert(string postScript)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript));

    private static string[] Ops(PostScriptConversionResult result)
        => ContentStreamAssert.Parse(result.Content).Select(o => o.ToString()).ToArray();

    private static PdfDictionary Shading(PostScriptConversionResult result, int index = 1)
        => (PdfDictionary)((PdfDictionary)result.Resources[PdfName.Shading])[new PdfName("Sh" + index)];

    private static double[] Numbers(PdfObject array)
        => ((PdfArray)array).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray();

    [Fact]
    public void AnAxialShading_IsPaintedBySh_ItsEntriesCarried()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(Axial + " shfill");
        Assert.Contains("/Sh1 sh", Ops(result));

        PdfDictionary sh = Shading(result);
        Assert.Equal(2, ((PdfNumber)sh[PdfName.ShadingType]).IntValue);
        Assert.Equal("DeviceRGB", ((PdfName)sh[PdfName.ColorSpace]).Value);
        Assert.Equal(new[] { 0.0, 0, 100, 0 }, Numbers(sh[PdfName.Coords]));
        var extend = (PdfArray)sh[PdfName.Extend];
        Assert.True(((PdfBoolean)extend[0]).Value);
        Assert.False(((PdfBoolean)extend[1]).Value);

        var function = (PdfDictionary)sh[PdfName.Function];
        Assert.Equal(2, ((PdfNumber)function[PdfName.FunctionType]).IntValue);
        Assert.Equal(new[] { 1.0, 0, 0 }, Numbers(function[PdfName.C0]));
        Assert.Equal(new[] { 0.0, 0, 1 }, Numbers(function[PdfName.C1]));
        Assert.Equal(1, ((PdfNumber)function[PdfName.N]).DoubleValue);
        Assert.Equal(new[] { 0.0, 1 }, Numbers(function[PdfName.Domain]));
        Assert.DoesNotContain("shfill", PostScriptDiagnostics.UnimplementedOperators);
        Assert.Null(result.LastError);
    }

    /// <summary>A radial shading under a stitching function of two exponential ones, with its bounds and encode.</summary>
    [Fact]
    public void ARadialShading_CarriesAStitchingFunction()
    {
        var result = Convert(
            "<< /ShadingType 3 /ColorSpace /DeviceGray /Coords [50 50 0 50 50 40]" +
            " /Function << /FunctionType 3 /Domain [0 1] /Bounds [0.5] /Encode [0 1 0 1]" +
            "   /Functions [ << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>" +
            "                << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 2 >> ] >> >> shfill");

        PdfDictionary sh = Shading(result);
        Assert.Equal(3, ((PdfNumber)sh[PdfName.ShadingType]).IntValue);
        Assert.Equal(new[] { 50.0, 50, 0, 50, 50, 40 }, Numbers(sh[PdfName.Coords]));
        var stitching = (PdfDictionary)sh[PdfName.Function];
        Assert.Equal(3, ((PdfNumber)stitching[PdfName.FunctionType]).IntValue);
        Assert.Equal(new[] { 0.5 }, Numbers(stitching[PdfName.Bounds]));
        Assert.Equal(new[] { 0.0, 1, 0, 1 }, Numbers(stitching[PdfName.Encode]));
        var parts = (PdfArray)stitching[PdfName.Functions];
        Assert.Equal(2, parts.Count);
        Assert.Equal(2, ((PdfNumber)((PdfDictionary)parts[1])[PdfName.N]).DoubleValue);
    }

    /// <summary>A function-based shading's matrix and domain, its sampled function's string becoming the stream's data.</summary>
    [Fact]
    public void AFunctionBasedShading_CarriesASampledFunctionsData()
    {
        var result = Convert(
            "<< /ShadingType 1 /ColorSpace /DeviceGray /Domain [0 1 0 1] /Matrix [100 0 0 100 0 0]" +
            " /Function << /FunctionType 0 /Domain [0 1 0 1] /Range [0 1] /Size [2 2] /BitsPerSample 8" +
            "   /DataSource <00FF8040> >> >> shfill");

        PdfDictionary sh = Shading(result);
        Assert.Equal(new[] { 100.0, 0, 0, 100, 0, 0 }, Numbers(sh[PdfName.Matrix]));
        Assert.Equal(new[] { 0.0, 1, 0, 1 }, Numbers(sh[PdfName.Domain]));
        var sampled = Assert.IsType<PdfStream>(sh[PdfName.Function]);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x80, 0x40 }, sampled.RawData);
        Assert.Equal(new[] { 2.0, 2 }, Numbers(sampled[PdfName.Size]));
        Assert.Equal(8, ((PdfNumber)sampled[PdfName.BitsPerSample]).IntValue);
        Assert.Equal(new[] { 0.0, 1 }, Numbers(sampled[PdfName.Range]));
    }

    /// <summary>
    /// A separation shading is written as a separation: its tint transform — t to CMYK <c>t/2 0 t 0</c>
    /// — sampled at 256 tints into a FunctionType 0, entry 255 being the full tint's colour.
    /// </summary>
    [Fact]
    public void ASeparationShading_IsWrittenAsASeparation_ItsTransformSampled()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(
            "<< /ShadingType 2 /ColorSpace [/Separation /Orange /DeviceCMYK {dup 0.5 mul exch 0 exch 0}]" +
            " /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >> shfill");

        var space = (PdfArray)Shading(result)[PdfName.ColorSpace];
        Assert.Equal("Separation", ((PdfName)space[0]).Value);
        Assert.Equal("Orange", ((PdfName)space[1]).Value);
        Assert.Equal("DeviceCMYK", ((PdfName)space[2]).Value);
        var transform = Assert.IsType<PdfStream>(space[3]);
        Assert.Equal(0, ((PdfNumber)transform[PdfName.FunctionType]).IntValue);
        Assert.Equal(new[] { 256.0 }, Numbers(transform[PdfName.Size]));
        Assert.Equal(new[] { 0.0, 1, 0, 1, 0, 1, 0, 1 }, Numbers(transform[PdfName.Range]));
        Assert.Equal(256 * 4, transform.RawData.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, transform.RawData.Take(4).ToArray());
        Assert.Equal(new byte[] { 128, 0, 255, 0 }, transform.RawData.Skip(255 * 4).ToArray());
        Assert.Equal(new byte[] { 64, 0, 128, 0 }, transform.RawData.Skip(128 * 4).Take(4).ToArray());   // t = 128/255
        Assert.Null(result.LastError);
    }

    /// <summary>
    /// A DeviceN shading is written as DeviceN: two inks over CMYK, the transform sampled on a 64 × 64
    /// grid, and the sampling said.
    /// </summary>
    [Fact]
    public void ADeviceNShading_IsWrittenAsDeviceN_ItsTransformSampled()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(
            "<< /ShadingType 2 /ColorSpace [/DeviceN [/Orange /Teal] /DeviceCMYK {0 0}] /Coords [0 0 100 0]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [0 1] /C1 [1 0] /N 1 >> >> shfill");
        var space = (PdfArray)Shading(result)[PdfName.ColorSpace];
        Assert.Equal("DeviceN", ((PdfName)space[0]).Value);
        Assert.Equal(new[] { "Orange", "Teal" }, ((PdfArray)space[1]).Cast<PdfName>().Select(n => n.Value).ToArray());
        var transform = (PdfStream)space[3];
        Assert.Equal(new[] { 64.0, 64 }, Numbers(transform[PdfName.Size]));
        Assert.Equal(new[] { 0.0, 1, 0, 1 }, Numbers(transform[PdfName.Domain]));
        Assert.Equal(64 * 64 * 4, transform.RawData.Length);
        Assert.Equal(new byte[] { 255, 255, 0, 0 }, transform.RawData.Skip((64 * 64 - 1) * 4).ToArray());   // both full: C M from the inks
        Assert.Contains("sampled at 64 points", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A triangle mesh given as numbers is packed: 8-bit flags, 32-bit coordinates and 16-bit grey,
    /// each scaled into the <c>/Decode</c> range its values span.
    /// </summary>
    [Fact]
    public void ATriangleMeshGivenAsNumbers_IsPacked()
    {
        var result = Convert(
            "<< /ShadingType 4 /ColorSpace /DeviceGray" +
            " /DataSource [0 0 0 0  0 100 0 1  0 0 50 0.5] >> shfill");

        var mesh = Assert.IsType<PdfStream>(Shading(result));
        Assert.Equal(32, ((PdfNumber)mesh[PdfName.BitsPerCoordinate]).IntValue);
        Assert.Equal(16, ((PdfNumber)mesh[PdfName.BitsPerComponent]).IntValue);
        Assert.Equal(8, ((PdfNumber)mesh[PdfName.BitsPerFlag]).IntValue);
        Assert.Equal(new[] { 0.0, 100, 0, 50, 0, 1 }, Numbers(mesh[PdfName.Decode]));
        Assert.Equal(new byte[]
        {
            0,  0, 0, 0, 0,  0, 0, 0, 0,  0x00, 0x00,
            0,  0xFF, 0xFF, 0xFF, 0xFF,  0, 0, 0, 0,  0xFF, 0xFF,
            0,  0, 0, 0, 0,  0xFF, 0xFF, 0xFF, 0xFF,  0x80, 0x00,
        }, mesh.RawData);
    }

    /// <summary>
    /// A Coons patch's edge flag decides its length: a first patch (flag 0) has twelve points and four
    /// colours, a following one (flag 1) eight and two — so the second patch's flag is read where the
    /// first one's values end.
    /// </summary>
    [Fact]
    public void ACoonsPatchMesh_ReadsEachPatchsLengthFromItsFlag()
    {
        string first = "0 " + string.Join(" ", Enumerable.Range(0, 24).Select(k => (k * 2).ToString())) + " 0 0.25 0.5 1";
        string second = " 1 " + string.Join(" ", Enumerable.Range(0, 16).Select(k => (k * 3).ToString())) + " 0 1";
        var result = Convert(
            "<< /ShadingType 6 /ColorSpace /DeviceGray /DataSource [" + first + second + "] >> shfill");

        var mesh = Assert.IsType<PdfStream>(Shading(result));
        Assert.Equal(1 + 24 * 4 + 4 * 2 + 1 + 16 * 4 + 2 * 2, mesh.RawData.Length);
        Assert.Equal(1, mesh.RawData[1 + 24 * 4 + 4 * 2]);   // the second patch's flag
        Assert.Equal(new[] { 0.0, 44, 2, 46, 0, 1 }, Numbers(mesh[PdfName.Decode]));   // x even values, y odd
    }

    /// <summary>A mesh whose data is a string travels as it is, with the widths and decode the program gave.</summary>
    [Fact]
    public void ALatticeMeshGivenAsAString_IsCarriedAsItIs()
    {
        var result = Convert(
            "<< /ShadingType 5 /ColorSpace /DeviceGray /VerticesPerRow 2 /BitsPerCoordinate 8" +
            " /BitsPerComponent 8 /Decode [0 255 0 255 0 1] /DataSource <0000000A00FF0A0A80FF0A40> >> shfill");

        var mesh = Assert.IsType<PdfStream>(Shading(result));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x0A, 0x00, 0xFF, 0x0A, 0x0A, 0x80, 0xFF, 0x0A, 0x40 }, mesh.RawData);
        Assert.Equal(2, ((PdfNumber)mesh[PdfName.VerticesPerRow]).IntValue);
        Assert.Equal(8, ((PdfNumber)mesh[PdfName.BitsPerCoordinate]).IntValue);
        Assert.Null(mesh[PdfName.BitsPerFlag]);
        Assert.Equal(new[] { 0.0, 255, 0, 255, 0, 1 }, Numbers(mesh[PdfName.Decode]));
    }

    /// <summary>
    /// A lattice mesh given as numbers has no flags — each vertex is a coordinate pair and its colour —
    /// and a value the same everywhere still gets a range, so its samples are 0 at its value.
    /// </summary>
    [Fact]
    public void ALatticeMeshGivenAsNumbers_HasNoFlags_AndAConstantValueARange()
    {
        var result = Convert(
            "<< /ShadingType 5 /ColorSpace /DeviceGray /VerticesPerRow 2" +
            " /DataSource [0 0 0.5  10 0 0.5  0 10 0.5  10 10 0.5] >> shfill");

        var mesh = Assert.IsType<PdfStream>(Shading(result));
        Assert.Null(mesh[PdfName.BitsPerFlag]);
        Assert.Equal(4 * (4 + 4 + 2), mesh.RawData.Length);
        Assert.Equal(new[] { 0.0, 10, 0, 10, 0.5, 1.5 }, Numbers(mesh[PdfName.Decode]));
        Assert.Equal(new byte[] { 0, 0 }, mesh.RawData.Skip(8).Take(2).ToArray());     // the first vertex's grey
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, mesh.RawData.Skip(10).Take(4).ToArray());   // the second's x
    }

    /// <summary>
    /// A mesh's data from a file <c>currentfile</c> made: the block after <c>shfill</c>, read to its
    /// marker, the program resuming after it.
    /// </summary>
    [Fact]
    public void AMeshFromAFile_ReadsTheBlockAfterShfill()
    {
        var result = Convert(
            "<< /ShadingType 4 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8" +
            " /Decode [0 255 0 255 0 1] /DataSource currentfile /ASCIIHexDecode filter >> shfill\n" +
            "00000000 00FF0080 0000FFFF>\n0.25 setgray newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill");
        var mesh = Assert.IsType<PdfStream>(Shading(result));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0xFF, 0, 0x80, 0, 0, 0xFF, 0xFF }, mesh.RawData);
        Assert.Contains("0.25 g", Ops(result));
    }

    /// <summary>A sampled function's data from a file, as a mesh's is.</summary>
    [Fact]
    public void ASampledFunctionFromAFile_ReadsItsBlock()
    {
        var result = Convert(
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] /Function << /FunctionType 0 /Domain [0 1]" +
            " /Range [0 1] /Size [2] /BitsPerSample 8 /DataSource currentfile /ASCIIHexDecode filter >> >> shfill\n00FF>\n");
        var function = Assert.IsType<PdfStream>(Shading(result)[PdfName.Function]);
        Assert.Equal(new byte[] { 0x00, 0xFF }, function.RawData);
    }

    /// <summary>
    /// A shading refused before it reads its file still consumes the block, so the data is not read as
    /// program: the fill after it is drawn.
    /// </summary>
    [Fact]
    public void ARefusedShadingWithAFileSource_ConsumesItsBlock()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(
            "<< /ShadingType 4 /ColorSpace [/DeviceN [/A /B] /DeviceGray {pop pop}] /BitsPerCoordinate 8 /BitsPerComponent 8" +
            " /BitsPerFlag 8 /Decode [0 255 0 255 0 1 0 1] /DataSource currentfile /ASCIIHexDecode filter >> shfill\n" +
            "0000000000 00FF000000 0000FF0000>\n0.25 setgray newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill");
        Assert.Null(result.Resources?[PdfName.Shading]);
        Assert.Contains("0.25 g", Ops(result));
        Assert.Contains("gave no answer", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    private const string RefusedSpace = "/ColorSpace [/DeviceN [/A /B] /DeviceGray {pop pop}]";
    private const string FileFunction =
        "<< /FunctionType 0 /Domain [0 1] /Range [0 1 0 1] /Size [2] /BitsPerSample 8 /DataSource currentfile /ASCIIHexDecode filter >>";

    /// <summary>
    /// A refused shading consumes its function's file block too, alone or in an array of functions —
    /// the refusal stays the last thing said, which data read as program would not leave it.
    /// </summary>
    [Theory]
    [InlineData(FileFunction)]
    [InlineData("[" + FileFunction + "]")]
    public void ARefusedShading_ConsumesItsFunctionsBlock(string function)
    {
        PostScriptDiagnostics.Clear();
        var result = Convert("<< /ShadingType 2 " + RefusedSpace + " /Coords [0 0 100 0] /Function " + function + " >> shfill\n" +
                             "00FF00FF>\n0.25 setgray newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill");
        Assert.Null(result.Resources?[PdfName.Shading]);
        Assert.Contains("0.25 g", Ops(result));
        Assert.Contains("gave no answer", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A shading pattern <c>makepattern</c> refuses consumes its shading's file block, as <c>shfill</c> does.</summary>
    [Fact]
    public void ARefusedShadingPattern_ConsumesItsBlock()
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(
            "<< /PatternType 2 /Shading << /ShadingType 4 " + RefusedSpace + " /BitsPerCoordinate 8 /BitsPerComponent 8" +
            " /BitsPerFlag 8 /Decode [0 255 0 255 0 1 0 1] /DataSource currentfile /ASCIIHexDecode filter >> >> matrix makepattern\n" +
            "0000000000 00FF000000 0000FF0000>\npop 0.25 setgray newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill");
        Assert.Contains("0.25 g", Ops(result));
        Assert.Contains("gave no answer", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A mesh whose file is a JPEG is refused by name — a JPEG is no shading data — the codestream consumed.</summary>
    [Fact]
    public void AMeshFromAJpegFile_IsRefused()
    {
        PostScriptDiagnostics.Clear();
        byte[] head = Encoding.ASCII.GetBytes(
            "<< /ShadingType 4 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8" +
            " /Decode [0 255 0 255 0 1] /DataSource currentfile /DCTDecode filter >> shfill\n");
        byte[] jpeg =
        {
            0xFF, 0xD8,
            0xFF, 0xE1, 0x00, 0x0C, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0xFF, 0xD9, 0x00, 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
            0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78,
            0xFF, 0xFF, 0xD9,
        };
        var result = PostScriptConversion.ConvertFragment(head.Concat(jpeg)
            .Concat(Encoding.ASCII.GetBytes("\n0.25 setgray newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill")).ToArray());
        Assert.Null(result.Resources?[PdfName.Shading]);
        Assert.Contains("0.25 g", Ops(result));
        Assert.Contains("is a JPEG", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>The page's extent is the clip a fill paints, or the shading's own box where nothing clips.</summary>
    [Fact]
    public void AShfillsExtent_IsTheClip_OrItsBBox()
    {
        var clipped = Convert("newpath 10 20 moveto 60 20 lineto 60 70 lineto 10 70 lineto closepath clip " + Axial + " shfill");
        Assert.Equal(new[] { 10.0, 20, 60, 70 }, clipped.BoundingBox);

        var boxed = Convert(Axial.Replace(">> >>", ">> /BBox [5 6 7 8] /AntiAlias true >>") + " shfill");
        Assert.Equal(new[] { 5.0, 6, 7, 8 }, boxed.BoundingBox);
        Assert.Equal(new[] { 5.0, 6, 7, 8 }, Numbers(Shading(boxed)[PdfName.BBox]));
        Assert.True(((PdfBoolean)Shading(boxed)[PdfName.AntiAlias]).Value);
    }

    /// <summary>
    /// A CalRGB shading with its dictionary is carried in CalRGB, nothing said; a bare <c>/CalRGB</c>,
    /// which names no white point, travels as DeviceRGB, said once however many are painted.
    /// </summary>
    [Fact]
    public void ACalRgbShading_IsCarriedInCalRgb_ABareOneAsDeviceRgbSaidOnce()
    {
        PostScriptDiagnostics.Clear();
        string calRgb = Axial.Replace("/ColorSpace /DeviceRGB", "/ColorSpace [/CalRGB << /WhitePoint [0.95 1 1.09] >>]");
        var exact = Convert(calRgb + " shfill");
        Assert.Equal("CalRGB", ((PdfName)((PdfArray)Shading(exact)[PdfName.ColorSpace])[0]).Value);
        Assert.Equal(0, PostScriptDiagnostics.ReportCount);

        string bare = Axial.Replace("/ColorSpace /DeviceRGB", "/ColorSpace /CalRGB");
        var result = Convert(bare + " shfill " + bare + " shfill");
        Assert.Equal("DeviceRGB", ((PdfName)Shading(result, 2)[PdfName.ColorSpace]).Value);
        Assert.Equal(1, PostScriptDiagnostics.ReportCount);
        Assert.Contains("device analogue", PostScriptDiagnostics.LastError, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<< /ShadingType 2 /ColorSpace [/DeviceN [/A /B] /DeviceGray {pop pop}] /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >>", "gave no answer")]
    [InlineData("<< /ShadingType 4 /ColorSpace /DeviceGray /DataSource [3 0 0 0  0 1 0 1  0 0 1 0] >>", "not 0 to 2")]
    [InlineData("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0] >>", "needs a /Function")]
    [InlineData("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0] /Function << /FunctionType 4 /Domain [0 1] >> >>", "FunctionType 4")]
    [InlineData("<< /ShadingType 4 /ColorSpace /DeviceGray /DataSource {} >>", "is a procedure")]
    [InlineData("<< /ShadingType 4 /ColorSpace /DeviceGray /DataSource [0 0 0 0 0 1] >>", "ends inside")]
    [InlineData("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >>", "/Coords is not 4")]
    [InlineData("<< /ShadingType 8 /ColorSpace /DeviceRGB >>", "/ShadingType")]
    [InlineData("<< /ShadingType 2 /ColorSpace /Pattern /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >>", "cannot be in a Pattern space")]
    public void AShadingThisConverterCannotCarry_IsSkippedAndSaid(string shading, string reason)
    {
        PostScriptDiagnostics.Clear();
        var result = Convert(shading + " shfill " + Axial + " shfill");
        string[] ops = Ops(result);
        Assert.Single(ops, "/Sh1 sh");                              // the second, carriable one is painted
        Assert.DoesNotContain("/Sh2 sh", ops);
        string said = PostScriptDiagnostics.LastError ?? "";
        Assert.Contains("a shading was skipped: ", said, System.StringComparison.Ordinal);
        Assert.Contains(reason, said, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>shfill</c>'s operand is a dictionary; anything else is a <c>typecheck</c>, which <c>stopped</c>
    /// catches — and a dictionary is not, the control.
    /// </summary>
    [Theory]
    [InlineData("{ 5 shfill } stopped", true)]
    [InlineData("{ << >> shfill } stopped", false)]
    public void AShfillOfSomethingThatIsNotADictionary_IsATypecheck(string program, bool stopped)
    {
        var interpreter = new Interpreter.PsInterpreter(null);
        interpreter.Run(new Interpreter.PsParser(new Interpreter.PsScanner(Encoding.ASCII.GetBytes(program))).ReadAll());
        Assert.Equal(stopped, interpreter.Operands.Items[interpreter.Operands.Items.Count - 1].AsBool());
    }
}

/// <summary>The shading counts, set through <see cref="PsLimits"/>.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class ShadingLimitTests
{
    private const string Axial =
        "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0]" +
        " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>";

    /// <summary>
    /// Hostile: a loop of <c>shfill</c>s registers no more than <c>MaxShadings</c> shadings — the refused
    /// counted among them — and says so once.
    /// </summary>
    [Fact]
    public void HostileShfillLoop_StopsAtTheCap_AndSaysSoOnce()
    {
        try
        {
            PsLimits.MaxShadings = 3;
            PostScriptDiagnostics.Clear();
            var result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "<< /ShadingType 9 >> shfill 10 { " + Axial + " shfill } repeat"));
            string[] ops = ContentStreamAssert.Parse(result.Content).Select(o => o.ToString()).ToArray();
            Assert.Equal(new[] { "/Sh1 sh", "/Sh2 sh" }, ops.Where(o => o.EndsWith(" sh")).ToArray());
            Assert.Equal(2, PostScriptDiagnostics.ReportCount);     // the refusal, and the cap once
            Assert.Contains("3-shading cap", PostScriptDiagnostics.LastError, System.StringComparison.Ordinal);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// Hostile: function dictionaries nested past <c>MaxShadingFunctions</c> are refused — the count
    /// bounds the nesting as well as the breadth — and a shading at the cap is carried.
    /// </summary>
    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void HostileNestedStitchingFunctions_AreBoundedByTheCount(int dictionaries, bool carried)
    {
        try
        {
            PsLimits.MaxShadingFunctions = 3;
            PostScriptDiagnostics.Clear();
            string function = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>";
            for (int k = 1; k < dictionaries; k++)
                function = "<< /FunctionType 3 /Domain [0 1] /Bounds [] /Encode [0 1] /Functions [" + function + "] >>";
            var result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function " + function + " >> shfill"));
            string[] ops = ContentStreamAssert.Parse(result.Content).Select(o => o.ToString()).ToArray();

            Assert.Equal(carried, ops.Contains("/Sh1 sh"));
            if (!carried)
                Assert.Contains("more than 3 function dictionaries", PostScriptDiagnostics.LastError, System.StringComparison.Ordinal);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
