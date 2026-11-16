// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An <c>/ICCBased</c> space's arity comes from its profile's <c>/N</c>, whichever way <c>/N</c> is
/// spelled, in every render-path reader of it.
/// </summary>
/// <remarks>
/// <para><b>Three readers, all raw, all defaulting to three.</b> <c>PdfColor.ResolveSpace</c> (the
/// raster colour-space resolver), <c>IndexedPalette</c> (both renderers' palettes) and the SVG
/// writer's own <c>GetIccComponentCount</c> each read <c>icc[PdfName.N] is PdfNumber</c>, so a
/// one-channel profile written <c>/N 7 0 R</c> was taken for RGB. They are one helper now, in the
/// <c>PdfColor</c> leaf both renderers already share.</para>
///
/// <para>The profile streams here are empty, so no transform can be built and each renderer falls
/// back to the device space <c>/N</c> names — which is exactly the decision under test.</para>
/// </remarks>
public class IccProfileComponentCountTests
{
    /// <summary>A grey image in a one-channel profile draws grey.</summary>
    [Theory]
    [InlineData(false)]   // /N 1
    [InlineData(true)]    // /N 7 0 R
    public void AnImageInAOneChannelProfileDrawsGrey(bool indirectN)
    {
        byte[] pdf = ImageFixtures.OneImagePdf(new byte[] { 0x40, 0x40, 0x40, 0x40 }, 2, 2,
            "[/ICCBased 6 0 R]", extraObjects: b => Profile(b, indirectN));

        AssertGrey(RasterPixel(pdf), 0x40, "a one-channel ICC image sample");
    }

    /// <summary>An indexed image whose base is a one-channel profile reads one byte per entry.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIndexedImageOverAOneChannelProfileReadsOneBytePerEntry(bool indirectN)
    {
        byte[] pdf = ImageFixtures.OneImagePdf(new byte[] { 1, 1, 1, 1 }, 2, 2,
            "[/Indexed [/ICCBased 6 0 R] 1 <40C0>]", extraObjects: b => Profile(b, indirectN));

        AssertGrey(RasterPixel(pdf), 0xC0, "palette entry 1 of a one-channel base");
    }

    /// <summary>
    /// An SVG fill in a four-channel profile is the same colour whichever way <c>/N</c> is spelled,
    /// and differs from what three channels make of the same operands.
    /// </summary>
    /// <remarks>
    /// Four operands on purpose: the SVG fallback takes a single operand as grey whatever <c>/N</c>
    /// says, so only a count that separates CMYK from RGB reaches the read. <c>0 1 0 0</c> is magenta
    /// as CMYK and green as RGB.
    /// </remarks>
    [Fact]
    public void AnSvgFillInAFourChannelProfileIsReadAsFourChannels()
    {
        string direct = FillOf(FillPdf("4", "null"));
        string indirect = FillOf(FillPdf("7 0 R", "4"));
        string asRgb = FillOf(FillPdf("3", "null"));

        Assert.NotEqual(asRgb, direct);
        Assert.Equal(direct, indirect);
    }

    // ------------------------------------------------------------------

    private static void Profile(PdfFixtureBuilder b, bool indirectN)
        => b.StreamObj(6, "<< /N " + (indirectN ? "7 0 R" : "1"), string.Empty).Obj(7, "1");

    private static byte[] FillPdf(string n, string object7)
        => new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /ColorSpace << /CS0 [/ICCBased 6 0 R] >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "/CS0 cs 0 1 0 0 scn 0 0 72 72 re f")
            .StreamObj(6, "<< /N " + n, string.Empty)
            .Obj(7, object7)
            .Finish();

    private static SKColor RasterPixel(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);
        return bmp.GetPixel(36, 36);
    }

    private static void AssertGrey(SKColor c, byte expected, string what)
        => Assert.True(
            System.Math.Abs(c.Red - expected) <= 3 && System.Math.Abs(c.Green - expected) <= 3
            && System.Math.Abs(c.Blue - expected) <= 3,
            $"{what}: expected grey {expected} but got ({c.Red},{c.Green},{c.Blue})");

    private static string FillOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        var filled = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path" && e.Attribute("fill") != null
                        && e.Attribute("fill")!.Value != "none")
            .ToList();
        Assert.True(filled.Count > 0, "the export drew no filled path:\n" + svg);
        return filled[0].Attribute("fill")!.Value;
    }
}
