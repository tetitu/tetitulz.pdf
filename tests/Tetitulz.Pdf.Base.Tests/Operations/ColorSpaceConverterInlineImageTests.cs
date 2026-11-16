// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>PdfColorSpaceConverter</c> converts inline images (batch 109): a <c>BI … ID … EI</c> in a
/// page's content, in a form XObject's, with its colour space abbreviated or named from the
/// resources, is converted to the target space and its stream written back; one that is not
/// DeviceRGB, or whose filter chain does not decode to samples, is left as it was — and a stream
/// with nothing converted is not rewritten at all. Before batch 109 a grayscale conversion left an
/// RGB inline image red.
/// </summary>
public class ColorSpaceConverterInlineImageTests
{
    /// <summary>A one-pixel red inline image, hex-encoded so the fixture stays ASCII.</summary>
    private const string RedInline = "BI /W 1 /H 1 /CS /RGB /BPC 8 /F /AHx ID FF0000> EI";

    private static byte[] PageWithContent(string content, string resources = "<< >>", string extraObjects = "")
        => new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources " + resources + " /Contents 4 0 R >>")
            .StreamObj(4, "<<", content)
            .Finish();

    [Fact]
    public void AnInlineRgbImageInThePageContent_IsConvertedToGray()
    {
        byte[] pdf = PageWithContent("q 72 0 0 72 0 0 cm " + RedInline + " Q");

        int converted = ToGray(pdf, out byte[] output);

        Assert.Equal(1, converted);
        PdfStream image = TheInlineImage(output, 4);
        Assert.Equal(PdfName.DeviceGray, image[PdfName.ColorSpace] as PdfName);
        Assert.Equal(PdfName.FlateDecode, image[PdfName.Filter] as PdfName);
        // BT.601, truncating: 255 * 299 / 1000.
        Assert.Equal(new byte[] { 76 }, image.GetDecodedData());
        Assert.Null(PdfColorSpaceConverter.LastConversionWarning);
        Assert.Equal((76, 76, 76), CentrePixel(output));
        Assert.Equal((255, 0, 0), CentrePixel(pdf));
    }

    [Fact]
    public void AnInlineImageInsideAFormXObject_IsConverted_AndThePageStreamLeftAlone()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << /XObject << /Fx 5 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Fx Do Q")
            .StreamObj(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1]", RedInline)
            .Finish();

        int converted = ToGray(pdf, out byte[] output);

        Assert.Equal(1, converted);
        PdfStream image = TheInlineImage(output, 5);
        Assert.Equal(new byte[] { 76 }, image.GetDecodedData());
        string appended = Encoding.Latin1.GetString(output, pdf.Length, output.Length - pdf.Length);
        Assert.Matches(@"(^|\n)5 0 obj", appended);
        Assert.DoesNotMatch(@"(^|\n)4 0 obj", appended);
        Assert.Equal((76, 76, 76), CentrePixel(output));
    }

    /// <summary>A colour space named from the resources (§8.9.7) is resolved: <c>/CS /CS0</c> naming DeviceRGB converts; a DeviceGray inline image is not RGB and leaves the file untouched.</summary>
    [Fact]
    public void ANamedColourSpace_IsResolved_AndAGrayImageLeavesTheFileUntouched()
    {
        byte[] named = PageWithContent("q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /CS0 /BPC 8 /F /AHx ID FF0000> EI Q",
            resources: "<< /ColorSpace << /CS0 /DeviceRGB >> >>");
        byte[] gray = PageWithContent("q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 /F /AHx ID 80> EI Q");

        Assert.Equal(1, ToGray(named, out byte[] namedOut));
        Assert.Equal(new byte[] { 76 }, TheInlineImage(namedOut, 4).GetDecodedData());
        Assert.Equal(0, ToGray(gray, out byte[] grayOut));
        Assert.Equal(gray, grayOut);
    }

    /// <summary>A JPEG inline image does not decode to raw samples: left as it was, the file untouched, and the warning says why.</summary>
    [Fact]
    public void AJpegInlineImage_IsLeft_AndNamed()
    {
        byte[] pdf = PageWithContent("q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /RGB /BPC 8 /F [/AHx /DCT] ID FFD8FFD9> EI Q");

        Assert.Equal(0, ToGray(pdf, out byte[] output));

        Assert.Equal(pdf, output);
        Assert.Contains("inline image", PdfColorSpaceConverter.LastConversionWarning, StringComparison.Ordinal);
        Assert.Contains("filter chain", PdfColorSpaceConverter.LastConversionWarning, StringComparison.Ordinal);
    }

    /// <summary>The inline image's <c>/D</c> (Decode) array is applied before conversion: a stored (64,64,64) under an inverting array shows 191 and converts to grey 191.</summary>
    [Fact]
    public void TheDecodeArray_IsApplied()
    {
        byte[] pdf = PageWithContent("q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /RGB /BPC 8 /D [1 0 1 0 1 0] /F /AHx ID 404040> EI Q");

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        PdfStream image = TheInlineImage(output, 4);
        Assert.Equal(new byte[] { 191 }, image.GetDecodedData());
        Assert.Null(image[PdfName.Decode]);
        Assert.Null(image[PdfName.D]);
    }

    /// <summary>The CMYK direction converts an inline image too: four samples, DeviceCMYK, the page drawn in ink.</summary>
    [Fact]
    public void AnInlineImage_ConvertsToCmykToo()
    {
        byte[] pdf = PageWithContent("q 72 0 0 72 0 0 cm " + RedInline + " Q");

        using var ms = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToCmyk(new MemoryStream(pdf), ms);
        byte[] output = ms.ToArray();

        Assert.Equal(1, converted);
        PdfStream image = TheInlineImage(output, 4);
        Assert.Equal(PdfName.DeviceCMYK, image[PdfName.ColorSpace] as PdfName);
        Assert.Equal(4, image.GetDecodedData().Length);
        var (r, g, b) = CentrePixel(output);
        Assert.True(r > 200 && g < 80 && b < 80, $"the converted page is not red any more: ({r}, {g}, {b})");
    }

    /// <summary>Two inline images in one stream are both converted and the stream written once; an image XObject beside them is counted with them.</summary>
    [Fact]
    public void SeveralInlineImages_AndAnXObject_AreAllCounted()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 36 0 0 72 0 0 cm " + RedInline + " Q q 36 0 0 72 36 0 cm " + RedInline + " Q q 1 0 0 1 0 0 cm /Im0 Do Q")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .Finish();

        Assert.Equal(3, ToGray(pdf, out byte[] output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var ops = ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
        var images = ops.Where(o => o.Operator == PdfOperators.BeginInlineImage).Select(o => (PdfStream)o.Operands[0]).ToList();
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal(PdfName.DeviceGray, i[PdfName.ColorSpace] as PdfName));
        string appended = Encoding.Latin1.GetString(output, pdf.Length, output.Length - pdf.Length);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(appended, @"(^|\n)4 0 obj"));
    }

    /// <summary>One content stream drawn by two pages is converted once: counted once, superseded once, both pages grey.</summary>
    [Fact]
    public void AContentStreamSharedByTwoPages_IsConvertedOnce()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm " + RedInline + " Q")
            .Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R >>")
            .Finish();

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        string appended = Encoding.Latin1.GetString(output, pdf.Length, output.Length - pdf.Length);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(appended, @"(^|\n)4 0 obj"));
        Assert.Equal((76, 76, 76), CentrePixel(output));
        var page2 = PdfPageRenderer.RenderPageToPixels(new MemoryStream(output), 1, 72);
        int i = (page2.Height / 2) * page2.Stride + (page2.Width / 2) * 4;
        Assert.Equal((76, 76, 76), (page2.Pixels[i], page2.Pixels[i + 1], page2.Pixels[i + 2]));
    }

    /// <summary>
    /// Thirty forms each holding an inline image the converter leaves alone (4 bits a sample) are
    /// noted once each, and <see cref="PdfColorSpaceConverter.LastConversionWarning"/> names the first
    /// twenty and counts the rest: joined whole, its length was the file's to choose.
    /// </summary>
    [Fact]
    public void AdversarialManyUnconvertedInlineImages_AreEachNoted_AndTheSlotIsCapped()
    {
        const int forms = 30;
        var draws = new StringBuilder();
        var names = new StringBuilder();
        for (int k = 0; k < forms; k++)
        {
            draws.Append("/Fx").Append(k).Append(" Do ");
            names.Append("/Fx").Append(k).Append(' ').Append(5 + k).Append(" 0 R ");
        }
        var builder = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << /XObject << " + names + ">> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", draws.ToString());
        for (int k = 0; k < forms; k++)
            builder = builder.StreamObj(5 + k, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1]",
                "BI /W 1 /H 1 /CS /RGB /BPC 4 /F /AHx ID F00> EI");

        ToGray(builder.Finish(), out _);

        string warning = PdfColorSpaceConverter.LastConversionWarning;
        Assert.Equal(Core.Util.DistinctStrings.MaxJoined,
            System.Text.RegularExpressions.Regex.Matches(warning, "depth is not 8 bits").Count);
        Assert.EndsWith($"and {forms - Core.Util.DistinctStrings.MaxJoined} more.", warning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private static int ToGray(byte[] pdf, out byte[] output)
    {
        using var ms = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), ms);
        output = ms.ToArray();
        return converted;
    }

    /// <summary>The one inline image in the stream of object <paramref name="objectNumber"/>, as the parser reads it back.</summary>
    private static PdfStream TheInlineImage(byte[] pdf, int objectNumber)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var stream = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(objectNumber));
        var ops = ContentStreamParser.Parse(stream.GetDecodedData());
        var bi = Assert.Single(ops, o => o.Operator == PdfOperators.BeginInlineImage);
        return Assert.IsAssignableFrom<PdfStream>(bi.Operands[0]);
    }

    private static (int R, int G, int B) CentrePixel(byte[] pdf)
    {
        var page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        int i = (page.Height / 2) * page.Stride + (page.Width / 2) * 4;
        return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
    }
}
