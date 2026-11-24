// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Partial image redaction: an image XObject a region covers only in part keeps its
/// uncovered pixels — the covered ones are painted black in a clone drawn at the invocation, the
/// original's pixels gone from the file. A wholly covered image, a stencil mask and one whose matrix
/// cannot be inverted are stripped whole (a masked image keeps its transparency); an image shared
/// with a page not redacted keeps its original there, reported.
/// </summary>
public class PartialImageRedactionTests
{
    /// <summary>A 4×2 RGB image: the left two columns red, the right two green — 24 recognisable bytes, uncompressed.</summary>
    private static readonly byte[] Pixels = Enumerable.Range(0, 8)
        .SelectMany(i => i % 4 < 2 ? new byte[] { 255, 0, 0 } : new byte[] { 0, 255, 0 }).ToArray();

    /// <summary>
    /// The image drawn 200pt square at (100, 100); a region over its left half. The clone keeps the
    /// right half green, the left half is black, the Do stays — and the original's red bytes are
    /// nowhere in the output. Before batch 113 the whole image was blanked and its Do dropped.
    /// </summary>
    [Fact]
    public void ARegionOverHalfAnImage_BlacksThatHalf_AndKeepsTheRest()
    {
        byte[] output = Redact(ImagePdf("200 0 0 200 100 100 cm"), new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfStream image = TheOnlyImage(doc);
        byte[] rgb = image.GetDecodedData();
        Assert.Equal(PdfName.DeviceRGB, image[PdfName.ColorSpace] as PdfName);
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb.Take(3).ToArray());          // top-left: covered
        Assert.Equal(new byte[] { 0, 255, 0 }, rgb.Skip(9).Take(3).ToArray()); // top-right: kept
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb.Skip(15).Take(3).ToArray());  // second row, second column: covered
        Assert.Equal(new byte[] { 0, 255, 0 }, rgb.Skip(18).Take(3).ToArray()); // second row, third column: kept
        Assert.Contains("/Im0 Do", Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.Latin1.GetString(Pixels), Encoding.Latin1.GetString(output), StringComparison.Ordinal);
    }

    /// <summary>
    /// A region ending inside a pixel column takes the whole column: 75pt of a 200pt-wide image is a
    /// column and a half, so the first two columns are black and the third is kept — rounded
    /// outward, never inward.
    /// </summary>
    [Fact]
    public void ARegionEndingInsideAPixel_TakesTheWholePixel()
    {
        byte[] output = Redact(ImagePdf("200 0 0 200 100 100 cm"), new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 75, Height = 200 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        byte[] rgb = TheOnlyImage(doc).GetDecodedData();
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb.Take(3).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb.Skip(3).Take(3).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0 }, rgb.Skip(6).Take(3).ToArray());
    }

    /// <summary>
    /// Rows count from the top: a region over the upper half of the drawn square (y 200–300) blacks
    /// the image's first row and keeps the second — the unit square's v runs upward, the samples
    /// downward.
    /// </summary>
    [Fact]
    public void ARegionOverTheTopHalf_BlacksTheFirstRow()
    {
        byte[] output = Redact(ImagePdf("200 0 0 200 100 100 cm"), new PdfRedactionArea { PageIndex = 0, X = 100, Y = 200, Width = 200, Height = 100 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        byte[] rgb = TheOnlyImage(doc).GetDecodedData();
        Assert.All(Enumerable.Range(0, 12), i => Assert.Equal(0, rgb[i]));           // first row: covered
        Assert.Equal(new byte[] { 255, 0, 0 }, rgb.Skip(12).Take(3).ToArray());        // second row: kept
        Assert.Equal(new byte[] { 0, 255, 0 }, rgb.Skip(18).Take(3).ToArray());
    }

    /// <summary>
    /// An image also drawn by an annotation's appearance stream — a reference the content walk does
    /// not see — loses its pixels there too: drawn once in page content, the original is repointed
    /// and stripped, so only the clone carries pixels and the original's bytes are gone.
    /// </summary>
    [Fact]
    public void AnImageAlsoDrawnByAnAppearanceStream_LosesItsPixelsThereToo()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> /Annots [6 0 R] >>",
            StreamObj("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"),
            StreamObj("/Type /XObject /Subtype /Image /Width 4 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", Encoding.Latin1.GetString(Pixels)),
            "<< /Type /Annot /Subtype /Stamp /Rect [400 400 500 500] /F 4 /AP << /N 7 0 R >> >>",
            StreamObj("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> >>", "q 100 0 0 100 0 0 cm /Im0 Do Q\n"));

        byte[] output = Redact(pdf, new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        Assert.DoesNotContain(Encoding.Latin1.GetString(Pixels), Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Single(ImageStreams(doc).Where(s => (s.GetDecodedData()?.Length ?? 0) > 0));
        Assert.Contains("/Stamp", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
    }

    /// <summary>
    /// The masked variant: the original image stays reachable through the appearance stream, so its
    /// mask keys must go with its pixels — otherwise the original soft mask, whose shape over the
    /// region is part of what was redacted, is still written.
    /// </summary>
    [Fact]
    public void AMaskedImageAlsoDrawnByAnAppearanceStream_LosesItsMaskThereToo()
    {
        byte[] mask = { 10, 20, 30, 40, 50, 60, 70, 80 };
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> /Annots [6 0 R] >>",
            StreamObj("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"),
            StreamObj("/Type /XObject /Subtype /Image /Width 4 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 8 0 R", Encoding.Latin1.GetString(Pixels)),
            "<< /Type /Annot /Subtype /Stamp /Rect [400 400 500 500] /F 4 /AP << /N 7 0 R >> >>",
            StreamObj("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> >>", "q 100 0 0 100 0 0 cm /Im0 Do Q\n"),
            StreamObj("/Type /XObject /Subtype /Image /Width 4 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", Encoding.Latin1.GetString(mask)));

        byte[] output = Redact(pdf, new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        Assert.DoesNotContain(Encoding.Latin1.GetString(mask), Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.Latin1.GetString(Pixels), Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.Contains("/Stamp", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
    }

    /// <summary>
    /// A bilevel image with a soft mask is still written at one bit, the mask beside it: a soft mask
    /// works over any depth.
    /// </summary>
    [Fact]
    public void ABilevelImageWithASoftMask_StaysOneBit_WithItsMask()
    {
        byte[] bits = { 0xAA, 0x0F, 0x55, 0xF0 };
        byte[] mask = Enumerable.Repeat((byte)128, 32).ToArray();
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
            StreamObj("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"),
            StreamObj("/Type /XObject /Subtype /Image /Width 16 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 1 /SMask 6 0 R", Encoding.Latin1.GetString(bits)),
            StreamObj("/Type /XObject /Subtype /Image /Width 16 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", Encoding.Latin1.GetString(mask)));

        byte[] output = Redact(pdf, new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfStream image = DrawnImage(doc, "Im0");
        Assert.Equal(1, (doc.ResolveObject(image[PdfName.BitsPerComponent]) as PdfNumber)?.IntValue);
        var softMask = Assert.IsType<PdfStream>(doc.ResolveObject(image[PdfName.SMask]));
        byte[] alpha = softMask.GetDecodedData();
        Assert.Equal(255, alpha[0]);     // left half: opaque over the region
        Assert.Equal(128, alpha[15]);    // right half: the original alpha
    }

    /// <summary>A region covering the whole image strips it, as before: no image stream with pixels remains, and no Do.</summary>
    [Fact]
    public void ARegionOverTheWholeImage_StripsIt()
    {
        byte[] output = Redact(ImagePdf("200 0 0 200 100 100 cm"), new PdfRedactionArea { PageIndex = 0, X = 50, Y = 50, Width = 300, Height = 300 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.DoesNotContain(" Do", Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.Latin1.GetString(Pixels), Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.All(ImageStreams(doc), s => Assert.True((s.GetDecodedData()?.Length ?? 0) == 0, "an image kept pixels"));
    }

    /// <summary>
    /// A quarter-turned image: <c>0 200 -200 0 300 100 cm</c> maps the image's columns up the page.
    /// A region over the lower half of the drawn square (y 100–200) covers the image's first two
    /// columns — the red ones — on every row; the green columns stay.
    /// </summary>
    [Fact]
    public void ARotatedImage_HasTheRegionMappedThroughItsMatrix()
    {
        byte[] output = Redact(ImagePdf("0 200 -200 0 300 100 cm"), new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 200, Height = 100 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        byte[] rgb = TheOnlyImage(doc).GetDecodedData();
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb.Take(3).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb.Skip(3).Take(3).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0 }, rgb.Skip(6).Take(3).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0 }, rgb.Skip(21).Take(3).ToArray());
    }

    /// <summary>
    /// An image with a soft mask keeps its transparency: the clone carries a new soft mask from the
    /// decoded alpha, opaque over the region (the box shows, the mask's shape there is gone) and the
    /// original alpha elsewhere; the original's pixels and its mask leave the file, rather than the
    /// image being stripped whole.
    /// </summary>
    [Fact]
    public void AMaskedImage_KeepsItsTransparency_OpaqueOverTheRegion()
    {
        byte[] mask = { 10, 20, 30, 40, 50, 60, 70, 80 };
        byte[] output = Redact(ImagePdf("200 0 0 200 100 100 cm", extraImageKeys: " /SMask 6 0 R", extraObjects: new[] {
            StreamObj("/Type /XObject /Subtype /Image /Width 4 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", Encoding.Latin1.GetString(mask)) }),
            new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        Assert.DoesNotContain(Encoding.Latin1.GetString(Pixels), Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.Latin1.GetString(mask), Encoding.Latin1.GetString(output), StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Contains("/Im0 Do", Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
        PdfStream image = DrawnImage(doc, "Im0");
        var softMask = Assert.IsType<PdfStream>(doc.ResolveObject(image[PdfName.SMask]));
        Assert.Equal(new byte[] { 255, 255, 30, 40, 255, 255, 70, 80 }, softMask.GetDecodedData());
        byte[] rgb = image.GetDecodedData();
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb.Take(3).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0 }, rgb.Skip(9).Take(3).ToArray());
    }

    /// <summary>
    /// A colour-key mask (§8.9.6.4) reaches the new soft mask too: the red pixels are keyed out, so
    /// with the right half redacted the left half stays transparent and the right half is opaque black.
    /// </summary>
    [Fact]
    public void AColourKeyedImage_KeepsItsKeyAsASoftMask()
    {
        byte[] output = Redact(ImagePdf("200 0 0 200 100 100 cm", extraImageKeys: " /Mask [255 255 0 0 0 0]"),
            new PdfRedactionArea { PageIndex = 0, X = 200, Y = 100, Width = 100, Height = 200 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfStream image = DrawnImage(doc, "Im0");
        Assert.Null(image[PdfName.Mask]);
        var softMask = Assert.IsType<PdfStream>(doc.ResolveObject(image[PdfName.SMask]));
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, softMask.GetDecodedData());
    }

    /// <summary>
    /// A bilevel image comes back at one bit, CCITT Group 4, its kept pixels unchanged: 16 × 2, the
    /// left half redacted — not 8-bit grey, eight times the samples.
    /// </summary>
    [Fact]
    public void ABilevelImage_ComesBackAtOneBit_AsGroup4()
    {
        byte[] bits = { 0xAA, 0x0F, 0x55, 0xF0 };   // 16 columns, 2 rows, 1 = white
        byte[] output = Redact(OneBitImagePdf(bits), new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfStream image = DrawnImage(doc, "Im0");
        Assert.Equal(1, (doc.ResolveObject(image[PdfName.BitsPerComponent]) as PdfNumber)?.IntValue);
        Assert.Equal(PdfName.CCITTFaxDecode, image[PdfName.Filter] as PdfName);
        Assert.Equal(new byte[] { 0x00, 0x0F, 0x00, 0xF0 }, image.GetDecodedData());
    }
    /// <summary>A grey image comes back grey: the clone is one channel, its kept pixels unchanged.</summary>
    [Fact]
    public void AGreyImage_StaysOneChannel()
    {
        byte[] grey = { 10, 20, 30, 40, 50, 60, 70, 80 };
        byte[] output = Redact(ImagePdf("200 0 0 200 100 100 cm", "/DeviceGray", grey), new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfStream image = TheOnlyImage(doc);
        Assert.Equal(PdfName.DeviceGray, image[PdfName.ColorSpace] as PdfName);
        Assert.Equal(new byte[] { 0, 0, 30, 40, 0, 0, 70, 80 }, image.GetDecodedData());
    }

    /// <summary>
    /// An image drawn on two pages, one redacted: the redacted page draws a clone under a fresh name,
    /// the other keeps the original with its pixels, and the warning names the image.
    /// </summary>
    [Fact]
    public void AnImageSharedWithAnUnredactedPage_KeepsItsOriginalThere_Reported()
    {
        byte[] output = Redact(TwoPagePdf(), new PdfRedactionArea { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 200 });

        Assert.Contains(Encoding.Latin1.GetString(Pixels), Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        string warning = PdfRedactionEngine.LastSharedFormWarning;
        Assert.NotNull(warning);
        Assert.Contains("Im0", warning, StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Contains("/RdxImg0 Do", Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
        Assert.Contains("/Im0 Do", Encoding.Latin1.GetString(doc.Pages[1].GetContentStreamData()), StringComparison.Ordinal);
        Assert.Equal(2, ImageStreams(doc).Count(s => (s.GetDecodedData()?.Length ?? 0) > 0));
    }

    // ------------------------------------------------------------------

    private static byte[] Redact(byte[] pdf, params PdfRedactionArea[] areas)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(input, output, areas);
        return output.ToArray();
    }

    private static PdfStream TheOnlyImage(PdfDocument doc)
    {
        var images = ImageStreams(doc).Where(s => (s.GetDecodedData()?.Length ?? 0) > 0).ToList();
        return Assert.Single(images);
    }

    private static System.Collections.Generic.List<PdfStream> ImageStreams(PdfDocument doc)
    {
        var list = new System.Collections.Generic.List<PdfStream>();
        for (int n = 1; n < 64; n++)
            if (doc.GetObject(n) is PdfStream s && (doc.ResolveObject(s[PdfName.Subtype]) as PdfName)?.Value == "Image" && s[PdfName.SMask] == null && !(s[PdfName.ColorSpace] is PdfName cs && cs.Value == "DeviceGray" && s.GetDecodedData()?.Length == 8 && s.GetDecodedData().All(b => b == 255)))
                list.Add(s);
        return list;
    }

    private static byte[] ImagePdf(string cm, string colorSpace = "/DeviceRGB", byte[] samples = null, string extraImageKeys = "", string[] extraObjects = null)
    {
        samples ??= Pixels;
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
            StreamObj("", "q " + cm + " /Im0 Do Q\n"),
            StreamObj("/Type /XObject /Subtype /Image /Width 4 /Height 2 /ColorSpace " + colorSpace + " /BitsPerComponent 8" + extraImageKeys, Encoding.Latin1.GetString(samples)),
        };
        if (extraObjects != null) bodies.AddRange(extraObjects);
        return Build(bodies.ToArray());
    }

    /// <summary>The image a page draws under <paramref name="name"/>.</summary>
    private static PdfStream DrawnImage(PdfDocument doc, string name)
    {
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]));
        return Assert.IsType<PdfStream>(doc.ResolveObject(xobjects[new PdfName(name)]));
    }

    /// <summary>A 16 × 2 one-bit DeviceGray image drawn 200pt square at (100, 100).</summary>
    private static byte[] OneBitImagePdf(byte[] bits) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
        StreamObj("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"),
        StreamObj("/Type /XObject /Subtype /Image /Width 16 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 1", Encoding.Latin1.GetString(bits)));

    private static byte[] TwoPagePdf() => Build(        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
        StreamObj("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"),
        StreamObj("/Type /XObject /Subtype /Image /Width 4 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", Encoding.Latin1.GetString(Pixels)),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 7 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
        StreamObj("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"));

    private static string StreamObj(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
