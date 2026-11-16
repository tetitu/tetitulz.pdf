// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Transparency flattening: which pages reach transparency (<see cref="PdfTransparencyFlattener.FindTransparency"/>),
/// and those pages replaced by an opaque image of themselves (<see cref="PdfTransparencyFlattener.Flatten"/>).
/// </summary>
/// <remarks>
/// The flattening fixture is a 200×100 page: an opaque blue square on its left half, then — under an
/// <c>/ExtGState</c> with <c>/ca 0.5</c> — a red rectangle across the middle, so the overlap is purple
/// and the rest of the red pink. Asymmetric on purpose: a flattened image placed upside down or mirrored
/// cannot match the original's render.
/// </remarks>
public class TransparencyFlattenerTests
{
    private const string Art = "0 0 1 rg 0 0 100 100 re f /GS0 gs 1 0 0 rg 50 25 100 50 re f\n";
    private const string HalfAlpha = "/ExtGState << /GS0 << /ca 0.5 >> >>";

    // ---------------------------------------------------------------- the scan

    [Theory]
    [InlineData("/ExtGState << /G << /ca 0.5 >> >>", TransparencyKind.ConstantAlpha)]
    [InlineData("/ExtGState << /G << /CA 0.25 >> >>", TransparencyKind.ConstantAlpha)]
    [InlineData("/ExtGState << /G << /SMask << /S /Luminosity /G 9 0 R >> >> >>", TransparencyKind.SoftMask)]
    [InlineData("/ExtGState << /G << /BM /Multiply >> >>", TransparencyKind.BlendMode)]
    [InlineData("/ExtGState << /G << /BM [/Normal /Screen] >> >>", TransparencyKind.BlendMode)]
    [InlineData("/XObject << /Im 7 0 R >>", TransparencyKind.ImageSoftMask)]
    [InlineData("/XObject << /Im 8 0 R >>", TransparencyKind.ImageOpacityInData)]
    [InlineData("/XObject << /Fm 6 0 R >>", TransparencyKind.ContainerGroup)]
    [InlineData("/Pattern << /P << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >> /ExtGState << /ca 0.5 >> >> >>", TransparencyKind.ConstantAlpha)]
    public void EachTransparencyConstruct_IsFound(string resources, TransparencyKind kind)
    {
        var findings = PdfTransparencyFlattener.FindTransparency(new MemoryStream(Pdf(resources, "")));
        Assert.Contains(findings, f => f.PageIndex == 0 && f.Kind == kind);
    }

    /// <summary>
    /// The opaque settings are not transparency: <c>/SMask /None</c>, alphas of 1, a <c>/BM</c> of
    /// only <c>/Normal</c> and <c>/Compatible</c>, and an image with no soft mask.
    /// </summary>
    [Theory]
    [InlineData("/ExtGState << /G << /SMask /None /ca 1 /CA 1.0 /BM /Normal >> >>")]
    [InlineData("/ExtGState << /G << /BM [/Compatible /Normal] >> >>")]
    [InlineData("/XObject << /Im 5 0 R >>")]
    public void OpaqueSettings_AreNotTransparency(string resources)
        => Assert.Empty(PdfTransparencyFlattener.FindTransparency(new MemoryStream(Pdf(resources, ""))));

    [Fact]
    public void APageGroup_AndAnAnnotationsOpacity_AreFound()
    {
        var findings = PdfTransparencyFlattener.FindTransparency(new MemoryStream(
            Pdf("", "", pageExtras: "/Group << /S /Transparency /CS /DeviceRGB >> /Annots [10 0 R]")));

        Assert.Contains(findings, f => f.Kind == TransparencyKind.PageGroup);
        Assert.Contains(findings, f => f.Kind == TransparencyKind.AnnotationOpacity);
    }

    /// <summary>
    /// An annotation's <c>/ca</c> below 1 is opacity the renderers composite by (§12.5.5, read before
    /// <c>/CA</c>), so it is found as <c>/CA</c> is; the control, both entries at 1, is no finding.
    /// </summary>
    [Theory]
    [InlineData("/ca 0.5", true)]
    [InlineData("/ca 1 /CA 1", false)]
    public void AnAnnotationsFillOpacity_IsFound(string opacity, bool found)
    {
        var findings = PdfTransparencyFlattener.FindTransparency(new MemoryStream(
            Pdf("", "", pageExtras: "/Annots [14 0 R]", extraObjects: new[]
            {
                "<< /Type /Annot /Subtype /Square /Rect [150 60 190 90] /F 4 " + opacity + " /C [1 0 0] /AP << /N 13 0 R >> >>",
            })));

        Assert.Equal(found, findings.Any(f => f.Kind == TransparencyKind.AnnotationOpacity));
        if (found) Assert.Contains(findings, f => f.Kind == TransparencyKind.AnnotationOpacity && f.Detail.Contains("/ca", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// An annotation's own blend mode (ISO 32000-2 §12.5.2) is transparency the page reaches — read
    /// by the open-document overload exactly as by the stream one — and, like its opacity, is not a
    /// reason to flatten the page's content, which does not reach it.
    /// </summary>
    [Fact]
    public void AnAnnotationsBlendMode_IsFound_ByBothOverloads_AndDoesNotFlattenThePage()
    {
        const string multiply = "<< /Type /Annot /Subtype /Square /Rect [150 60 190 90] /F 4 /BM /Multiply /AP << /N 13 0 R >> >>";
        const string normal = "<< /Type /Annot /Subtype /Square /Rect [150 60 190 90] /F 4 /BM /Normal /AP << /N 13 0 R >> >>";
        byte[] blended = Pdf("", "0 0 1 rg 0 0 100 100 re f\n", pageExtras: "/Annots [14 0 R]", extraObjects: new[] { multiply });
        byte[] plain = Pdf("", "0 0 1 rg 0 0 100 100 re f\n", pageExtras: "/Annots [14 0 R]", extraObjects: new[] { normal });

        var byStream = PdfTransparencyFlattener.FindTransparency(new MemoryStream(blended));
        using (var doc = PdfDocument.Open(new MemoryStream(blended)))
        {
            var byDocument = PdfTransparencyFlattener.FindTransparency(doc);
            Assert.Equal(byStream.Select(f => f.ToString()), byDocument.Select(f => f.ToString()));
        }
        Assert.Contains(byStream, f => f.Kind == TransparencyKind.AnnotationBlendMode);
        Assert.DoesNotContain(PdfTransparencyFlattener.FindTransparency(new MemoryStream(plain)),
            f => f.Kind == TransparencyKind.AnnotationBlendMode);

        var (flattened, result) = Flatten(blended);
        Assert.Empty(result.FlattenedPages);
        Assert.Equal(blended, flattened);
    }

    // ---------------------------------------------------------------- flattening

    /// <summary>
    /// A flattened page renders as the original did — at every rotation, and with a crop box away
    /// from the origin — and nothing transparent is left on it, its page group included.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void AFlattenedPage_RendersAsTheOriginalDid(int rotate)
    {
        byte[] original = Pdf(HalfAlpha, Art.Replace("0 0 100 100 re", "10 5 100 100 re"),
            pageExtras: $"/Rotate {rotate} /CropBox [10 5 210 105] /Group << /S /Transparency /CS /DeviceRGB >>",
            mediaBox: "[-20 -10 240 120]");
        var (flattened, result) = Flatten(original, new TransparencyFlattenOptions { Dpi = 72, KeepTextSearchable = false });

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        Assert.Empty(result.Remaining);
        Assert.Empty(PdfTransparencyFlattener.FindTransparency(new MemoryStream(flattened)));
        AssertRendersAlike(original, flattened);
    }

    /// <summary>
    /// The control for the render comparison: the comparison tells pages apart. Unflattened, the
    /// same page with its red drawn opaque is a different page.
    /// </summary>
    [Fact]
    public void TheRenderComparison_SeesADifferentPage()
    {
        byte[] translucent = Pdf(HalfAlpha, Art);
        byte[] opaque = Pdf("", Art.Replace("/GS0 gs ", ""));
        Assert.True(MeanDifference(Render(translucent), Render(opaque)) > 10);
    }

    /// <summary>A page with no transparency is left exactly as it was: the document is copied byte for byte.</summary>
    [Fact]
    public void ADocumentWithNoTransparency_IsCopiedUnchanged()
    {
        byte[] original = Pdf("", "0 0 1 rg 0 0 100 100 re f\n");
        var (flattened, result) = Flatten(original);

        Assert.Empty(result.FlattenedPages);
        Assert.Equal(original, flattened);
    }

    /// <summary>
    /// A flattened page's text is an invisible layer over its image: it still extracts. Without the
    /// layer it does not — the image holds the text's pixels and nothing else.
    /// </summary>
    [Fact]
    public void AFlattenedPagesText_StillExtracts_UnlessItIsNotKept()
    {
        string content = Art + "BT /F1 12 Tf 10 80 Td (Hello flattened world) Tj ET\n";
        byte[] original = Pdf(HalfAlpha + " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>", content);

        var (kept, _) = Flatten(original, new TransparencyFlattenOptions { Dpi = 72 });
        Assert.Contains("Hello flattened world", Text(kept));

        var (dropped, _) = Flatten(original, new TransparencyFlattenOptions { Dpi = 72, KeepTextSearchable = false });
        Assert.DoesNotContain("Hello", Text(dropped));
    }

    /// <summary>Only the pages that reach transparency are flattened: the other page keeps its own content object.</summary>
    [Fact]
    public void OnlyTransparentPages_AreFlattened()
    {
        byte[] original = Pdf(HalfAlpha, Art, secondPage: true);
        var (flattened, result) = Flatten(original, new TransparencyFlattenOptions { Dpi = 72 });

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        using var doc = PdfDocument.Open(new MemoryStream(flattened));
        var secondContents = Assert.IsType<PdfReference>(doc.Pages[1].Dictionary[PdfName.Contents]);
        Assert.Equal(11, secondContents.ReferenceObjectNumber);
    }

    /// <summary>The grey option writes a DeviceGray image, one component per pixel.</summary>
    [Fact]
    public void TheGreyOption_WritesADeviceGrayImage()
    {
        var (flattened, _) = Flatten(Pdf(HalfAlpha, Art), new TransparencyFlattenOptions { Dpi = 36, ColorSpace = FlattenedColorSpace.Gray });

        using var doc = PdfDocument.Open(new MemoryStream(flattened));
        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]);
        var xobjects = (PdfDictionary)doc.ResolveObject(resources[PdfName.XObject]);
        var image = (PdfStream)doc.ResolveObject(xobjects.First().Value);
        Assert.Equal(PdfName.DeviceGray, image[PdfName.ColorSpace]);
        Assert.Equal(100, ((PdfNumber)image[PdfName.Width]).IntValue);

        // BT.601 luminance: the opaque blue at the left edge is 255 × 0.114, truncated — 29, where
        // a red channel copied through would be 0.
        byte[] samples = image.GetDecodedData();
        Assert.Equal(29, samples[25 * 100 + 5]);
    }

    /// <summary>
    /// A page whose only transparency is an annotation's opacity is not flattened: flattening its
    /// content would not reach the annotation, so it would cost the page its vectors for nothing.
    /// </summary>
    [Fact]
    public void APageWhoseOnlyTransparencyIsAnAnnotation_IsNotFlattened()
    {
        byte[] original = Pdf("", "0 0 1 rg 0 0 100 100 re f\n", pageExtras: "/Annots [10 0 R]");
        var (flattened, result) = Flatten(original);

        Assert.Empty(result.FlattenedPages);
        Assert.Equal(original, flattened);
        Assert.Contains(result.Remaining, f => f.Kind == TransparencyKind.AnnotationOpacity);
    }

    /// <summary>
    /// A signed document is refused — replacing a page's content is a change no signature permits —
    /// unless the caller says otherwise, when it is flattened.
    /// </summary>
    [Fact]
    public void ASignedDocument_IsRefused_UnlessAllowed()
    {
        byte[] signed = Pdf(HalfAlpha, Art, catalogExtras: "/Perms << /DocMDP 10 0 R >>");

        var ex = Assert.Throws<PdfException>(() => Flatten(signed));
        Assert.Contains("signed", ex.Message);

        var (_, result) = Flatten(signed, new TransparencyFlattenOptions { Dpi = 36, AllowSignedDocument = true });
        Assert.Equal(new[] { 0 }, result.FlattenedPages);
    }

    /// <summary>
    /// Annotations stay annotations: the page keeps its <c>/Annots</c>, the annotation is not drawn
    /// into the image as well, and its own opacity — which flattening the content does not reach — is
    /// reported as remaining.
    /// </summary>
    [Fact]
    public void Annotations_StayAnnotations_AndTheirTransparencyIsReported()
    {
        byte[] original = Pdf(HalfAlpha, Art, pageExtras: "/Annots [10 0 R]");
        var (flattened, result) = Flatten(original, new TransparencyFlattenOptions { Dpi = 72, KeepTextSearchable = false });

        // Drawn once, by the annotation, not also into the image: at half opacity a second drawing
        // would darken its square visibly.
        AssertRendersAlike(original, flattened);
        using var doc = PdfDocument.Open(new MemoryStream(flattened));
        Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));
        Assert.Contains(result.Remaining, f => f.Kind == TransparencyKind.AnnotationOpacity);
        Assert.Equal(new[] { 0 }, result.FlattenedPages);
    }

    [Fact]
    public void AnEncryptedDocument_IsRefused()
    {
        byte[] original = Pdf(HalfAlpha, Art, trailerExtras: "/Encrypt << /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >> /ID [<00><00>]");
        var ex = Assert.Throws<PdfException>(() => Flatten(original));
        Assert.Contains("encrypted", ex.Message);
    }

    // ---------------------------------------------------------------- helpers

    internal static (byte[] Output, TransparencyFlattenResult Result) Flatten(byte[] pdf, TransparencyFlattenOptions options = null)
    {
        using var output = new MemoryStream();
        var result = PdfTransparencyFlattener.Flatten(new MemoryStream(pdf), output, options);
        return (output.ToArray(), result);
    }

    internal static RenderedPage Render(byte[] pdf) => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

    internal static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return PdfTextExtractor.ExtractText(doc.Pages[0]);
    }

    internal static void AssertRendersAlike(byte[] a, byte[] b)
    {
        RenderedPage ra = Render(a), rb = Render(b);
        Assert.Equal((ra.Width, ra.Height), (rb.Width, rb.Height));
        double diff = MeanDifference(ra, rb);
        Assert.True(diff < 2, $"the flattened page renders differently: mean channel difference {diff:F2}");
    }

    internal static double MeanDifference(RenderedPage a, RenderedPage b)
    {
        long total = 0, count = 0;
        for (int y = 0; y < Math.Min(a.Height, b.Height); y++)
            for (int x = 0; x < Math.Min(a.Width, b.Width); x++)
                for (int c = 0; c < 3; c++)
                {
                    total += Math.Abs(a.Pixels[y * a.Stride + x * 4 + c] - b.Pixels[y * b.Stride + x * 4 + c]);
                    count++;
                }
        return (double)total / count;
    }

    /// <summary>
    /// A document whose first page draws <paramref name="content"/> with <paramref name="resources"/>.
    /// Objects 5–10 are fixtures the resources may name: 5 an opaque image, 6 a form with a
    /// transparency group, 7 an image with a soft mask, 8 a JPEG 2000 image asking for its opacity
    /// channel, 9 a soft-mask group form, 10 an annotation at half opacity, printable, drawing a red
    /// square through appearance stream 13; 11 is the second page's content when there is one.
    /// </summary>
    internal static byte[] Pdf(string resources, string content, string pageExtras = "", string mediaBox = "[0 0 200 100]",
        bool secondPage = false, string trailerExtras = "", string catalogExtras = "", IReadOnlyList<string> extraObjects = null)
    {
        string kids = secondPage ? "[3 0 R 12 0 R]" : "[3 0 R]";
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtras} >>",
            $"<< /Type /Pages /Kids {kids} /Count {(secondPage ? 2 : 1)} >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Contents 4 0 R /Resources << {resources} >> {pageExtras} >>",
            Stream(content),
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\u0080\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Group << /S /Transparency >> /Length 0 >>\nstream\n\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /SMask 5 0 R /Length 1 >>\nstream\n\u0080\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /Filter /JPXDecode /SMaskInData 1 /Length 1 >>\nstream\n\u0080\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Group << /S /Transparency >> /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Square /Rect [150 60 190 90] /F 4 /CA 0.5 /C [1 0 0] /AP << /N 13 0 R >> >>",
            Stream("0 1 0 rg 0 0 50 50 re f\n"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 11 0 R /Resources << >> >>",
            Stream("1 0 0 rg 0 0 40 30 re f\n", "/Type /XObject /Subtype /Form /BBox [0 0 40 30] "),
        };
        if (extraObjects != null) bodies.AddRange(extraObjects);

        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(ms.Length);
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Length;
        var sb = new StringBuilder($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R {trailerExtras} >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }

    /// <summary>A stream object's body: <paramref name="keys"/>, then the data.</summary>
    internal static string Stream(string s, string keys = "") => $"<< {keys}/Length {Encoding.Latin1.GetByteCount(s)} >>\nstream\n{s}\nendstream";
}
