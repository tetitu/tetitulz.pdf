// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §10.4's transfer function applied to a decoded <b>image</b>, in both renderers.
///
/// <para>The two had separate appliers and the SVG one was worse in two ways its "Parity with
/// <c>ContentStreamInterpreter.ApplyTransferFunction</c>" comment did not cover. It evaluated the
/// function <b>per channel per pixel</b> instead of building the 256-entry table — 3·w·h
/// evaluations against 768 — and it evaluated with <b>no resolver</b>, so a Type 3 stitching
/// function whose sub-functions are written as indirect references degraded in the export and not
/// in the render.</para>
///
/// <para>Both now go through <c>TransferFunctionLut</c>. The tests read the sample back out of each
/// renderer's output, because a transfer function that silently fails leaves a perfectly valid
/// image of the wrong colour.</para>
/// </summary>
public sealed class ImageTransferFunctionParityTests
{
    /// <summary>An inverting Type 2 exponential: <c>out = 1 − t</c>.</summary>
    private const string Invert = "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>";

    /// <summary>
    /// A mid-grey image under an inverting <c>/TR</c> comes out the complementary grey. 0x40
    /// becomes 0xBF, and neither value is the page background, so this cannot pass with the image
    /// dropped.
    /// </summary>
    [Fact]
    public void InvertingTransferFunction_RemapsAnImage_InBothRenderers()
    {
        byte[] pdf = ImagePdf(Invert);

        Assert.Equal(0xBF, RasterSample(pdf));
        Assert.Equal(0xBF, SvgSample(pdf));
    }

    /// <summary>
    /// The control: with no <c>/TR</c> the sample is untouched. Without it, "the renderers agree"
    /// would also hold for two renderers that both ignored the function.
    /// </summary>
    [Fact]
    public void WithNoTransferFunction_TheSampleIsUnchanged()
    {
        byte[] pdf = ImagePdf(null);

        Assert.Equal(0x40, RasterSample(pdf));
        Assert.Equal(0x40, SvgSample(pdf));
    }

    /// <summary>
    /// <b>The resolver.</b> A Type 3 stitching function whose sub-functions are <i>indirect
    /// references</i> can only be evaluated by a caller that passes a resolver. The raster path
    /// always did; the SVG copy called <c>Evaluate(fn, args)</c> with no resolver, so the
    /// sub-function came back unresolved and the export kept the original sample.
    /// </summary>
    [Fact]
    public void IndirectSubFunctionsInAStitchingFunction_AreResolvedByBothRenderers()
    {
        byte[] pdf = StitchingImagePdf();

        int raster = RasterSample(pdf);
        int svg = SvgSample(pdf);

        // The stitching function inverts across its whole domain, so 0x40 -> 0xBF.
        Assert.Equal(0xBF, raster);
        Assert.Equal(0xBF, svg);
    }

    /// <summary>
    /// <b>The transfer function and a soft mask commute — asserted, not assumed.</b>
    /// </summary>
    /// <remarks>
    /// <para>The two renderers apply <c>/TR</c> at <i>opposite ends</i> of the masking chain. Raster
    /// runs ICC → <c>/SMask</c> → colour-key → stencil → <c>/TR</c>; the SVG exporter runs
    /// <c>/Decode</c> → ICC → <c>/TR</c> → <c>/SMask</c> → hard mask. The backlog row that sent me
    /// here claimed the per-bitmap application was still written twice, which is false — both are
    /// one-line forwarders to <c>TransferFunctionLut</c> — but the <i>sequencing</i> difference it
    /// walked past had no test of any kind.</para>
    ///
    /// <para>It commutes because the mask steps write only alpha, preserving RGB, while
    /// <c>ApplyToBitmap</c> writes only RGB, preserving alpha. That is a property of the code as it
    /// stands today and nothing structural holds it: a mask step that ever zeroes a partially
    /// transparent pixel's colour, or a transfer applier that ever weights by alpha, separates the
    /// two renderers immediately and silently. This test is what would notice.</para>
    ///
    /// <para><b>The two renderers are sampled differently on purpose.</b> Raster composites over the
    /// white page, so its expectation folds the mask in; the SVG exporter bakes the mask into the
    /// embedded PNG's alpha, so its colour channel is read back <i>unmultiplied</i>. Comparing the
    /// two numbers directly would be comparing a composite against a source. Each is checked
    /// against the value the other implies, which is the actual parity claim.</para>
    /// </remarks>
    [Fact]
    public void TransferFunctionAndSoftMask_Commute_InBothRenderers()
    {
        byte[] pdf = MaskedImagePdf(Invert);

        // Colour 0x40 inverted is 0xBF; at alpha 128 over the white page that composites to 0xDF.
        Assert.Equal(0xDF, RasterSample(pdf));

        (int red, int alpha) = SvgSampleWithAlpha(pdf);
        Assert.Equal(0xBF, red);
        Assert.Equal(128, alpha);
    }

    /// <summary>
    /// The control that makes the one above mean something: the same masked image with no
    /// <c>/TR</c>. Without it, "both renderers agree under a mask" would also hold for two that
    /// dropped the transfer function entirely once a mask was present — which is precisely the
    /// failure the sequencing difference could produce.
    /// </summary>
    [Fact]
    public void MaskedImage_WithNoTransferFunction_KeepsTheOriginalSample()
    {
        byte[] pdf = MaskedImagePdf(null);

        // 0x40 at alpha 128 over white composites to 0x9F.
        Assert.Equal(0x9F, RasterSample(pdf));

        (int red, int alpha) = SvgSampleWithAlpha(pdf);
        Assert.Equal(0x40, red);
        Assert.Equal(128, alpha);
    }

    /// <summary>And the two agree — the claim neither single-renderer assertion makes.</summary>
    [Theory]
    [InlineData(Invert)]
    [InlineData(null)]
    public void RasterAndSvg_AgreeOnTheRemappedSample(string? tr)
    {
        byte[] pdf = ImagePdf(tr);
        Assert.Equal(RasterSample(pdf), SvgSample(pdf));
    }

    // ------------------------------------------------------------------

    /// <summary>A 1×1 8-bpc grey image (sample 0x40) drawn large, optionally under a /TR.</summary>
    private static byte[] ImagePdf(string trObject)
    {
        string gs = trObject == null ? "" : "/GT gs ";
        string res = trObject == null
            ? "/XObject << /Im0 5 0 R >>"
            : "/XObject << /Im0 5 0 R >> /ExtGState << /GT << /Type /ExtGState /TR 6 0 R >> >>";

        var objs = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            $" /Resources << {res} >> >>",
            Stream("", $"q {gs}70 0 0 70 10 10 cm /Im0 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace /DeviceGray /BitsPerComponent 8", "@"),
        };
        if (trObject != null) objs.Add(trObject);
        return Build(objs.ToArray());
    }

    /// <summary>
    /// The same image under a Type 3 stitching function whose two sub-functions are indirect —
    /// objects 7 and 8 — which is the shape that needs a resolver.
    /// </summary>
    private static byte[] StitchingImagePdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << /XObject << /Im0 5 0 R >>" +
            " /ExtGState << /GT << /Type /ExtGState /TR 6 0 R >> >> >> >>",
            Stream("", "q /GT gs 70 0 0 70 10 10 cm /Im0 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace /DeviceGray /BitsPerComponent 8", "@"),
            "<< /FunctionType 3 /Domain [0 1] /Functions [7 0 R 8 0 R]" +
            " /Bounds [0.5] /Encode [0 1 0 1] >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0.5] /N 1 >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0.5] /C1 [0] /N 1 >>");

    /// <summary>
    /// The 1×1 grey image again, this time carrying a 1×1 <c>/SMask</c> of 0x80 (half alpha),
    /// optionally under a <c>/TR</c>.
    /// </summary>
    private static byte[] MaskedImagePdf(string trObject)
    {
        string gs = trObject == null ? "" : "/GT gs ";
        string res = trObject == null
            ? "/XObject << /Im0 5 0 R >>"
            : "/XObject << /Im0 5 0 R >> /ExtGState << /GT << /Type /ExtGState /TR 7 0 R >> >>";

        var objs = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            $" /Resources << {res} >> >>",
            Stream("", $"q {gs}70 0 0 70 10 10 cm /Im0 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /SMask 6 0 R" +
                   " /ColorSpace /DeviceGray /BitsPerComponent 8", "@"),
            // The mask sample is 0x80 - half alpha. Written as an escape because Build encodes
            // with Latin1, so this one char is one byte and the /Length 1 it computes is honest.
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace /DeviceGray /BitsPerComponent 8", "\u0080"),
        };
        if (trObject != null) objs.Add(trObject);
        return Build(objs.ToArray());
    }

    /// <summary>
    /// The embedded PNG's own red and alpha at (0,0) — the mask having been baked into the alpha
    /// channel, so the colour channel is still the unmultiplied image sample.
    /// </summary>
    private static (int Red, int Alpha) SvgSampleWithAlpha(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        XElement image = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "image");
        string href = image.Attributes().Single(a => a.Name.LocalName == "href").Value;

        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href, StringComparison.Ordinal);
        using var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        SKColor c = bmp.GetPixel(0, 0);
        return (c.Red, c.Alpha);
    }

    private static int RasterSample(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp.GetPixel(45, 90 - 45).Red;
    }

    private static int SvgSample(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        XElement image = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "image");
        string href = image.Attributes().Single(a => a.Name.LocalName == "href").Value;

        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href, StringComparison.Ordinal);
        using var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        return bmp.GetPixel(0, 0).Red;
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
