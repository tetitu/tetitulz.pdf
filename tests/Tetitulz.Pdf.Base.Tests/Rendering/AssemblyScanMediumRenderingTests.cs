// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The rendering and image-decode findings of the 2026-08 assembly scan, each pinned by decoded
/// pixels, a parsed object, or the diagnostic the code is supposed to emit.
/// </summary>
public class AssemblyScanMediumRenderingTests
{
    private const int Dpi = 72;

    // ==================================================================
    //  ContentStreamInterpreter — cancellation through a nested stream
    // ==================================================================

    /// <summary>
    /// A form XObject, a Type 3 glyph and a tiling pattern cell each run a <b>nested</b>
    /// <c>Execute</c> from inside <c>Dispatch</c>, so the nested loop's cancellation threw straight
    /// into the parent's fault-isolating <c>catch (Exception)</c>: cancellation was reported as a
    /// <c>ContentStreamError</c> — a fabricated content-stream fault for a file that has none — and
    /// the parent carried on drawing with an unbalanced graphics-state stack.
    /// </summary>
    [Fact]
    public void Cancellation_InsideAFormXObject_PropagatesInsteadOfBecomingAContentStreamError()
    {
        // The page draws ONE operator, so the outer loop never reaches its 256-operator check and
        // the entry-point check has already passed: the only place cancellation can be noticed is
        // inside the form's own Execute. The form's first operator reports a diagnostic, and the
        // handler cancels there - so the cancellation is deterministic rather than timed.
        byte[] pdf = FormXObjectPdf(operatorsInsideForm: 4000, reportingFirstOperator: true);

        using var cts = new CancellationTokenSource();
        var errors = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Listen(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.RenderParameterIgnored) cts.Cancel();
            if (kind == RenderDiagnosticKind.ContentStreamError) errors.Add(detail ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Listen;
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, null, cts.Token));
        }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        Assert.DoesNotContain(errors, e => e.Contains("cancel", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The control: without cancellation the same fixture renders its form, so the test above
    /// cannot be satisfied by a page that never reached the nested stream at all.
    /// </summary>
    [Fact]
    public void TheNestedFormFixtureActuallyDrawsWhenNotCancelled()
    {
        byte[] pdf = FormXObjectPdf(operatorsInsideForm: 4000);

        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));

        SKColor c = bmp.GetPixel(20, 180);
        Assert.True(c.Red > 200 && c.Green < 80, $"the form should paint red at (20,180), was {c}");
    }

    // ==================================================================
    //  Tile + annotation cancellation
    // ==================================================================

    /// <summary>
    /// <c>RenderPageTileToBitmap</c> checked the token once at entry and then built its interpreter
    /// without one, so a tile of a page with a million operators ran to completion however early
    /// the host aborted — the exact case the overload's cancellation contract advertises.
    /// </summary>
    [Fact]
    public void Cancellation_ReachesTheTileInterpreter()
    {
        // Cancelled from inside the render, so the entry-point check has already passed and the
        // only thing that can notice is the tile's own interpreter.
        byte[] pdf = ManyOperatorPdf(4000, reportingFirstOperator: true);

        using var cts = new CancellationTokenSource();
        int ownThread = Environment.CurrentManagedThreadId;
        void Listen(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.RenderParameterIgnored) cts.Cancel();
        }

        RenderDiagnostics.OnEvent += Listen;
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                PdfPageRenderer.RenderPageTileToImage(new MemoryStream(pdf), 0, 0, 0, 100, 100,
                    PdfImageFormat.Png, Dpi, PdfPageRenderer.DefaultJpegQuality, null, cts.Token));
        }
        finally { RenderDiagnostics.OnEvent -= Listen; }
    }

    // ==================================================================
    //  /Rotate + a translucent background
    // ==================================================================

    /// <summary>
    /// The rotated destination surface was cleared to the requested background and then had the
    /// raw bitmap — which already carries that background — drawn over it with <c>SrcOver</c>, so a
    /// translucent background was composited <b>twice</b>. 50% alpha came back as 50% on a
    /// <c>/Rotate 0</c> page and 75% on an otherwise identical <c>/Rotate 90</c> one.
    /// </summary>
    [Fact]
    public void ATranslucentBackground_HasTheSameAlphaWithAndWithoutRotate()
    {
        var options = new PdfRenderOptions { BackgroundArgb = 0x80FF0000 };   // 50% red

        using SKBitmap unrotated = Decode(PdfPageRenderer.RenderPage(
            new MemoryStream(RotatedPdf(rotate: 0)), 0, Dpi, options));
        using SKBitmap rotated = Decode(PdfPageRenderer.RenderPage(
            new MemoryStream(RotatedPdf(rotate: 90)), 0, Dpi, options));

        // A corner the artwork does not cover, in each image's own space.
        byte a0 = unrotated.GetPixel(unrotated.Width - 5, 5).Alpha;
        byte a90 = rotated.GetPixel(rotated.Width - 5, 5).Alpha;

        Assert.InRange(a0, 120, 136);            // the 50% that was asked for
        Assert.Equal(a0, a90);
    }

    /// <summary>
    /// The control: an opaque background is unaffected either way, and the rotated page still
    /// carries its artwork — so the test above cannot pass on a rotated render that draws nothing.
    /// </summary>
    [Fact]
    public void ARotatedPageStillCarriesItsArtworkAndAnOpaqueBackground()
    {
        using SKBitmap rotated = Decode(PdfPageRenderer.RenderPage(
            new MemoryStream(RotatedPdf(rotate: 90)), 0, Dpi));

        Assert.Equal(255, rotated.GetPixel(rotated.Width - 5, 5).Alpha);
        int inked = 0;
        for (int y = 0; y < rotated.Height; y++)
            for (int x = 0; x < rotated.Width; x++)
            {
                SKColor c = rotated.GetPixel(x, y);
                if (c.Red > 200 && c.Green < 80) inked++;
            }
        Assert.True(inked > 100, $"the rotated page should still draw its red square, found {inked} px");
    }

    // ==================================================================
    //  Overprint bounds vs the CTM-scaled pen
    // ==================================================================

    /// <summary>
    /// <c>TryOverprintPath</c> padded its device rectangle with the <b>raw</b> line width and no
    /// miter allowance, while the coverage bitmap is drawn at <c>LineWidth × StrokeCtmScale()</c>.
    /// Because it returns true the normal paint is suppressed, so every stroke pixel outside the
    /// too-small rectangle was not painted at all: under <c>10 0 0 10 0 0 cm</c> a 2 pt pen paints
    /// 20 units wide and the pad was 2, leaving the outer 8 units on each side missing.
    /// </summary>
    [Fact]
    public void OverprintedWideStroke_PaintsToTheOuterEdgeOfThePen()
    {
        byte[] pdf = OverprintWideStrokePdf();
        var options = new PdfRenderOptions { SimulateOverprint = true };

        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));

        // The path is the horizontal line y = 5 under `10 0 0 10 0 0 cm`, i.e. device y = 50 on a
        // 100 pt page (device y counts down from the top: 100 - 50 = 50). A 2 pt pen scaled by 10
        // reaches 10 units either side, so device y = 42 is inside the pen and 8 units out from
        // the path — well beyond the old 2-unit pad.
        SKColor outer = bmp.GetPixel(50, 42);
        SKColor centre = bmp.GetPixel(50, 50);

        Assert.True(centre.Red < 100, $"the stroke centre should be inked, was {centre}");
        Assert.True(outer.Red < 100,
            $"the outer edge of the CTM-scaled pen should be inked too, was {outer}");
    }

    // ==================================================================
    //  IRendererPlugin vs PixelFormat
    // ==================================================================

    /// <summary>
    /// <c>plugin.RenderPage</c> returns finished PNG bytes that bypass <c>PdfImageEncoder</c>
    /// entirely, so <see cref="RenderPixelFormat.Grayscale"/> — a change to the encoded pixels, not
    /// merely their size — was silently discarded, with no diagnostic because the bypass report
    /// only fires when the predicate says a plugin cannot serve.
    /// </summary>
    [Fact]
    public void GrayscalePixelFormat_IsSomethingAPluginCannotHonour()
    {
        var grayscale = new PdfRenderOptions { PixelFormat = RenderPixelFormat.Grayscale };
        var rgba      = new PdfRenderOptions();

        Assert.True(grayscale.RequestsAnythingAPluginCannotHonour());
        Assert.False(rgba.RequestsAnythingAPluginCannotHonour());
    }

    // ==================================================================
    //  MaxRenderThreads
    // ==================================================================

    /// <summary>
    /// A negative value was silently reinterpreted as "choose automatically" — the one reading a
    /// caller who typed it cannot have meant. 0 keeps its documented meaning.
    /// </summary>
    [Fact]
    public void ANegativeMaxRenderThreads_IsRejectedRatherThanReinterpreted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageRenderer.RenderDegree(new PdfRenderOptions { MaxRenderThreads = -1 }, 10));

        Assert.Equal(1, PdfPageRenderer.RenderDegree(new PdfRenderOptions { MaxRenderThreads = 1 }, 10));
        Assert.True(PdfPageRenderer.RenderDegree(new PdfRenderOptions { MaxRenderThreads = 0 }, 10) >= 1);
    }

    // ==================================================================
    //  PluginImageDecoder — an indirect /Filter
    // ==================================================================

    /// <summary>
    /// <c>PdfDictionary</c>'s indexer does not follow an indirect reference, so an image whose
    /// <c>/Filter</c> is written as <c>12 0 R</c> — legal; the spec constrains only
    /// <c>/Length</c> — yielded no filter names at all. No plugin was ever offered the image, and
    /// the outcome was indistinguishable from "no plugin is registered for this filter".
    /// </summary>
    [Fact]
    public void AnIndirectFilter_StillYieldsItsFilterName()
    {
        byte[] pdf = ImagePdfWithIndirectFilter();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        // The dictionary holds the REFERENCE, which is what a producer writes and what
        // PdfDictionary's indexer hands back unresolved. Built here rather than read off the
        // parsed stream because the parser resolves /Filter eagerly to decode it - so a fixture
        // read back through the parser cannot exhibit the shape this method has to cope with.
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Image);
        imageDict.Set(PdfName.Filter, new PdfReference(6, 0));
        Assert.Equal("JBIG2Decode", (doc.ResolveObject(new PdfReference(6, 0)) as PdfName)?.Value);

        Assert.Equal(new[] { "JBIG2Decode" },
            PluginImageDecoder.GetImageFilterNames(imageDict, doc).ToArray());

        // …and an indirect element INSIDE the array form resolves too.
        var chain = new PdfArray();
        chain.Add(new PdfName("FlateDecode"));
        chain.Add(new PdfReference(6, 0));
        var chained = new PdfDictionary();
        chained.Set(PdfName.Filter, chain);
        Assert.Equal(new[] { "FlateDecode", "JBIG2Decode" },
            PluginImageDecoder.GetImageFilterNames(chained, doc).ToArray());
    }

    /// <summary>
    /// The direct spelling of the same dictionary reads the same, so resolving is a superset of
    /// the direct reading rather than a different one.
    /// </summary>
    [Theory]
    [InlineData("/JBIG2Decode", "JBIG2Decode")]
    [InlineData("[/FlateDecode /JBIG2Decode]", "FlateDecode,JBIG2Decode")]
    public void ADirectFilter_ReadsExactlyAsBefore(string filter, string expected)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, filter.StartsWith("[")
            ? BuildArray("FlateDecode", "JBIG2Decode")
            : (PdfObject)new PdfName("JBIG2Decode"));

        Assert.Equal(expected,
            string.Join(",", PluginImageDecoder.GetImageFilterNames(dict, null)));
    }

    // ==================================================================
    //  The JPEG marker walk — an Adobe APP14 after the frame header
    // ==================================================================

    /// <summary>
    /// The walk returned at the first SOF, so an Adobe APP14 written <b>after</b> it — legal, and
    /// the case <c>Core.Imaging.RasterImageInspector</c> deliberately keeps scanning for — was
    /// never seen. A CMYK JPEG in that shape was refused by <c>ReplacementImage.Jpeg</c> while
    /// Builder's decoder, reading the identical bytes, accepted it.
    /// </summary>
    [Theory]
    [InlineData(true)]      // APP14 before SOF — the case that already worked
    [InlineData(false)]     // APP14 after SOF — the case that did not
    public void AnAdobeMarkerIsFound_WhicheverSideOfTheFrameHeaderItSits(bool adobeFirst)
    {
        byte[] jpeg = CmykJpeg(adobeBeforeSof: adobeFirst);

        Assert.True(Core.Imaging.RasterImageInspector.TryInspectJpeg(jpeg, out var frame));
        Assert.Equal(4, frame.Components);
        Assert.Equal(8, frame.Width);
        Assert.Equal(4, frame.Height);
        Assert.True(frame.HasAdobeMarker,
            $"the Adobe APP14 marker should be found with adobeBeforeSof={adobeFirst}");
    }

    // ==================================================================
    //  SVG text with an unresolvable pattern fill
    // ==================================================================

    /// <summary>
    /// The path sites paint <c>none</c> and report when a named pattern cannot be built; the two
    /// text sites fell through to the graphics-state fill colour instead — which the Pattern colour
    /// space never sets, so the state still holds §8.4.1's initial black. The raster renderer paints
    /// nothing and the export showed solid black text, with no diagnostic on either side.
    /// </summary>
    [Fact]
    public void SvgText_WithAnUnresolvablePatternFill_PaintsNoneAndReports()
    {
        byte[] pdf = PatternTextPdf();

        var reported = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Listen(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.PatternFillUnresolved) reported.Add(detail ?? string.Empty);
        }

        string svg;
        RenderDiagnostics.OnEvent += Listen;
        try { svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0); }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        Assert.NotEmpty(reported);

        // Parsed as XML, never substring-matched: no element may claim a solid fill for the text.
        var root = XDocument.Parse(svg).Root;
        var fills = root.Descendants()
            .Select(e => e.Attribute("fill")?.Value)
            .Where(v => v != null)
            .ToList();
        Assert.DoesNotContain("rgb(0,0,0)", fills);
    }

    // ==================================================================
    //  Fixtures
    // ==================================================================

    private static SKBitmap Decode(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static PdfArray BuildArray(params string[] names)
    {
        var a = new PdfArray();
        foreach (string n in names) a.Add(new PdfName(n));
        return a;
    }

    /// <summary>A JPEG carrying only the markers <c>JpegHeader</c> reads, four-component.</summary>
    private static byte[] CmykJpeg(bool adobeBeforeSof)
    {
        byte[] app14 = App14Adobe();
        byte[] sof = Sof(width: 8, height: 4, components: 4);

        var b = new List<byte> { 0xFF, 0xD8 };
        if (adobeBeforeSof) { b.AddRange(app14); b.AddRange(sof); }
        else                { b.AddRange(sof); b.AddRange(app14); }
        b.AddRange(new byte[] { 0xFF, 0xD9 });
        return b.ToArray();
    }

    private static byte[] App14Adobe()
    {
        byte[] tag = Encoding.ASCII.GetBytes("Adobe");
        int len = 2 + tag.Length + 2 + 2 + 2 + 1;
        var b = new List<byte> { 0xFF, 0xEE, (byte)(len >> 8), (byte)len };
        b.AddRange(tag);
        b.AddRange(new byte[] { 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x02 });
        return b.ToArray();
    }

    private static byte[] Sof(int width, int height, int components)
    {
        int len = 8 + components * 3;
        var b = new List<byte> { 0xFF, 0xC0, (byte)(len >> 8), (byte)len, 0x08 };
        b.AddRange(new byte[] { (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width });
        b.Add((byte)components);
        for (int i = 0; i < components; i++) b.AddRange(new byte[] { (byte)(i + 1), 0x11, 0x00 });
        return b.ToArray();
    }

    /// <summary>A page drawing a form XObject that holds <paramref name="operatorsInsideForm"/> fills.</summary>
    private static byte[] FormXObjectPdf(int operatorsInsideForm, bool reportingFirstOperator = false)
    {
        // `/Sa gs` — an ExtGState setting stroke adjustment, which neither renderer honours — is
        // reported once per content stream through RenderParameterIgnored: a deterministic "the
        // interpreter has started this stream" signal.
        //
        // It was `/Perceptual ri`, then `/AbsoluteColorimetric ri`; every intent is honoured now
        // (absolute colorimetric since its media-white scaling), so none is reported.
        var form = new StringBuilder(
            reportingFirstOperator ? "/Sa gs\n1 0 0 rg\n" : "1 0 0 rg\n");
        for (int i = 0; i < operatorsInsideForm; i++) form.Append("10 10 40 40 re f\n");

        return Pdf("/MediaBox [0 0 200 200] /Resources << /XObject << /Fx0 5 0 R >> >>",
                   "/Fx0 Do\n",
                   $"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] " +
                   $"/Resources << /ExtGState << /Sa << /SA true >> >> >> " +
                   $"/Length {form.Length} >>\nstream\n{form}\nendstream\nendobj\n");
    }

    /// <summary>A page whose own content is <paramref name="count"/> fills.</summary>
    private static byte[] ManyOperatorPdf(int count, bool reportingFirstOperator = false)
    {
        // The reporting operator is `/Sa gs` — see FormXObjectPdf for why.
        var sb = new StringBuilder(
            reportingFirstOperator ? "/Sa gs\n1 0 0 rg\n" : "1 0 0 rg\n");
        for (int i = 0; i < count; i++) sb.Append("10 10 40 40 re f\n");
        return Pdf("/MediaBox [0 0 100 100] /Resources << /ExtGState << /Sa << /SA true >> >> >>", sb.ToString());
    }

    private static byte[] RotatedPdf(int rotate) =>
        Pdf($"/MediaBox [0 0 200 100] /Rotate {rotate}", "1 0 0 rg 10 10 40 40 re f");

    /// <summary>
    /// A DeviceCMYK line stroked 2 pt wide under <c>10 0 0 10 0 0 cm</c>, with overprint on — so
    /// the pen paints 20 device units wide while the old pad allowed 2.
    /// </summary>
    private static byte[] OverprintWideStrokePdf() => Pdf(
        "/MediaBox [0 0 100 100] /Resources << /ExtGState << /GS0 5 0 R >> >>",
        "0 0 0 0 k 0 0 100 100 re f\n" +
        "q /GS0 gs 10 0 0 10 0 0 cm 0 0 0 1 K 2 w 1 5 m 9 5 l S Q\n",
        "5 0 obj\n<< /Type /ExtGState /OP true /op true /OPM 1 >>\nendobj\n");

    /// <summary>Text filled with a PatternType 2 pattern whose /Shading cannot be built.</summary>
    private static byte[] PatternTextPdf() => Pdf(
        "/MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> " +
        "/Pattern << /P1 6 0 R >> >>",
        "/Pattern cs /P1 scn BT /F1 24 Tf 20 40 Td (Hello) Tj ET\n",
        "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        // PatternType 2 with no /Shading entry at all: nothing can be built from it.
        "6 0 obj\n<< /PatternType 2 >>\nendobj\n");

    private static byte[] ImagePdfWithIndirectFilter() => Pdf(
        "/MediaBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> >>",
        "q 20 0 0 20 10 10 cm /Im0 Do Q\n",
        "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
        "/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter 6 0 R /Length 4 >>\n" +
        "stream\n\x01\x02\x03\x04\nendstream\nendobj\n",
        "6 0 obj\n/JBIG2Decode\nendobj\n");

    /// <summary>A one-page PDF; every element of <paramref name="extraObjects"/> starts at 5.</summary>
    private static byte[] Pdf(string pageExtras, string content, params string[] extraObjects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R {pageExtras} /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        for (int i = 0; i < (extraObjects?.Length ?? 0); i++) Obj(extraObjects[i]);

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
