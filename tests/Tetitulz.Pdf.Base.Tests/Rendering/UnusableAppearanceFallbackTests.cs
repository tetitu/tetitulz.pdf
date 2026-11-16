// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An annotation whose <c>/AP</c> cannot supply a normal appearance is still drawn, from its
/// styling keys — in the raster renderer as well as the SVG exporter.
/// </summary>
/// <remarks>
/// <para><b>One renderer painted a square and the other painted nothing.</b> A
/// <c>/AP &lt;&lt; /D 8 0 R &gt;&gt;</c> — a down-only appearance, which real producers write —
/// has no <c>/N</c>. The raster path took <c>continue</c> and the annotation vanished; the SVG
/// exporter left its "appearance claimed" flag false and fell through to the synthesised
/// <c>/C</c> + <c>/IC</c> drawing.</para>
///
/// <para><b>The tell was inside the raster method itself.</b> Its third refusal — <c>/N</c> present
/// but not resolving to a usable stream — <i>already</i> called the fallback, under a comment
/// saying so. The two refusals above it did not. The same fix had landed one level short without
/// leaving the function it was made in.</para>
///
/// <para><b>Drawing nothing is the worse answer.</b> §12.5.5 makes <c>/N</c> required in an
/// appearance dictionary, so its absence is malformed input — not an instruction to omit the
/// annotation the file says is there and <c>/F</c> says to print.</para>
/// </remarks>
public sealed class UnusableAppearanceFallbackTests
{
    /// <summary>
    /// <c>/AP</c> with a <c>/D</c> but no <c>/N</c>: the square must still be painted.
    /// </summary>
    [Fact]
    public void ADownOnlyAppearanceStillPaintsTheAnnotation()
    {
        AssertSquareIsPainted(AnnotationPdf("/AP << /D 8 0 R >>"));
    }

    /// <summary>
    /// A dangling <c>/AP</c> that resolves to nothing takes the same route.
    /// </summary>
    [Fact]
    public void AnUnresolvableAppearanceDictionaryStillPaintsTheAnnotation()
    {
        AssertSquareIsPainted(AnnotationPdf("/AP 99 0 R"));
    }

    /// <summary>
    /// <b>The parity assertion.</b> Both renderers must agree that something is there — the
    /// divergence was invisible from inside either, since each was self-consistent.
    /// </summary>
    [Theory]
    [InlineData("/AP << /D 8 0 R >>")]
    [InlineData("/AP 99 0 R")]
    public void TheSvgExportAndTheRasterRendererAgreeThatTheAnnotationIsDrawn(string ap)
    {
        byte[] pdf = AnnotationPdf(ap);

        AssertSquareIsPainted(pdf);

        string svg;
        using (var input = new MemoryStream(pdf))
            svg = PdfSvgRenderer.RenderPageToSvg(input, 0);

        // The synthesised drawing uses /IC for the fill and /C for the stroke.
        Assert.Contains("rgb(0,0,255)", svg, StringComparison.Ordinal);
        Assert.Contains("rgb(255,0,0)", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and the half that stops the cases above from passing on a renderer that paints
    /// the square regardless: a <b>usable</b> <c>/N</c> is honoured, and it paints green — neither
    /// the <c>/C</c> red nor the <c>/IC</c> blue.
    /// </summary>
    [Fact]
    public void AUsableNormalAppearanceIsStillPreferredOverTheSynthesisedOne()
    {
        using SKBitmap bitmap = Render(AnnotationPdf("/AP << /N 8 0 R >>"));
        SKColor mid = bitmap.GetPixel(150, 70);

        Assert.True(mid.Green > 150 && mid.Red < 100 && mid.Blue < 100,
            $"the /N appearance stream paints green; got {mid}");
    }

    // ------------------------------------------------------------------

    private static void AssertSquareIsPainted(byte[] pdf)
    {
        using SKBitmap bitmap = Render(pdf);

        // /Rect [100 100 200 160] with /IC [0 0 1] — blue inside. PDF y is bottom-up and the
        // bitmap is top-down, so the rect spans image rows 40..100 and its centre is y = 70.
        SKColor inside = bitmap.GetPixel(150, 70);
        Assert.True(inside.Blue > 150 && inside.Red < 100,
            $"the annotation's interior colour must be painted; got {inside} " +
            "(white means the annotation was skipped entirely)");

        // …and the page is otherwise untouched, so this is not "the whole page went blue".
        SKColor outside = bitmap.GetPixel(20, 20);
        Assert.True(outside.Red > 200 && outside.Green > 200 && outside.Blue > 200,
            $"outside the /Rect must stay blank; got {outside}");
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToImage(input, 0, PdfImageFormat.Png, 72);
        Assert.NotNull(png);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>
    /// A page carrying one <c>/Square</c> annotation with a red border and a blue interior, whose
    /// appearance entry is supplied by the caller. Object 8 is a green-filled form XObject, so a
    /// renderer that honours a usable <c>/N</c> paints green and one falling back paints blue.
    /// </summary>
    private static byte[] AnnotationPdf(string apEntry)
    {
        const string content = "q Q\n";
        const string form = "0 1 0 rg 0 0 100 60 re f\n";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(int n, string body)
        {
            while (offsets.Count < n - 1) offsets.Add(0);
            offsets.Add(sb.Length);
            sb.Append(body);
        }

        sb.Append("%PDF-1.7\n");
        Obj(1, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(3, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R "
             + "/Annots [5 0 R] /Resources << >> >>\nendobj\n");
        Obj(4, $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj(5, "5 0 obj\n<< /Type /Annot /Subtype /Square /Rect [100 100 200 160] "
             + $"/C [1 0 0] /IC [0 0 1] /F 4 /BS << /W 2 >> {apEntry} >>\nendobj\n");
        Obj(6, "6 0 obj\n<< /Unused true >>\nendobj\n");
        Obj(7, "7 0 obj\n<< /Unused true >>\nendobj\n");
        Obj(8, $"8 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 60] /Length {form.Length} >>\n"
             + $"stream\n{form}endstream\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
