// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A non-finite <c>cm</c> / <c>Tm</c> operand is ignored by BOTH renderers.
///
/// <para><b>Why this exists.</b> The raster interpreter has guarded this for a long time — a NaN or
/// Infinity operand poisons the CTM for the rest of the enclosing <c>q</c>…<c>Q</c> scope, blanking
/// every later draw in it. The SVG writer had no such check and composed the value straight in, so
/// the export came out as <c>d="MInfinity,NaN LInfinity,NaN LInfinity,NaN LInfinity,NaN Z"</c> —
/// markup no viewer renders, from a file the raster path draws correctly.</para>
///
/// <para><b>The fixture is the interesting part.</b> An earlier attempt to reproduce this used
/// <c>1e400</c> and found nothing wrong, which was recorded as "did not reproduce". It was the
/// fixture, not the code: <c>PdfTokenizer.ReadNumber</c> accepts digits and one <c>.</c> and has no
/// exponent syntax at all, so <c>1e400</c> tokenizes as the number <c>1</c> followed by a keyword
/// <c>e400</c> — <c>cm</c> never received six operands and never ran. The constant below is the
/// idiom the repository already uses (<c>ReviewFixRendering_AdversarialShadingTests</c>): finite as
/// a <c>double</c>, <c>+Infinity</c> once cast to <c>float</c>, and the trailing <c>.0</c> is
/// load-bearing because it forces the parser's <c>DoubleValue</c> branch — without it
/// <c>long.TryParse</c> fails on a 41-digit literal and the operand silently becomes 0.</para>
/// </summary>
public class NonFiniteMatrixGuardTests
{
    private const string InfOperand = "10000000000000000000000000000000000000000.0";

    /// <summary>
    /// The rect keeps its literal page coordinates, because the poisoning <c>cm</c> is dropped.
    /// </summary>
    [Fact]
    public void SvgExport_IgnoresANonFiniteCm_KeepingFiniteCoordinates()
    {
        byte[] pdf = Pdf($"q {InfOperand} 0 0 1 0 0 cm 0 0 1 rg 10 10 20 20 re f Q");

        var d = PathData(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));

        Assert.Equal("M10,10 L30,10 L30,30 L10,30 Z", d);
    }

    /// <summary>
    /// The same file through the raster renderer — the two must agree, and the raster side already
    /// did. Blue ink lands inside the rect and nowhere near the origin.
    /// </summary>
    [Fact]
    public void RasterRender_IgnoresANonFiniteCm_LikeTheSvgExport()
    {
        byte[] pdf = Pdf($"q {InfOperand} 0 0 1 0 0 cm 0 0 1 rg 10 10 20 20 re f Q");

        using SKBitmap bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72));
        // PDF (20,20) is inside the 10..30 rect; device y is flipped on a 200 pt page.
        SKColor inside = bmp.GetPixel(20, 200 - 20);

        Assert.Equal((byte)0,   inside.Red);
        Assert.Equal((byte)0,   inside.Green);
        Assert.Equal((byte)255, inside.Blue);
    }

    /// <summary>A non-finite <c>Tm</c> never reaches the export either.</summary>
    [Fact]
    public void SvgExport_IgnoresANonFiniteTm()
    {
        byte[] pdf = Pdf($"BT /F1 12 Tf {InfOperand} 0 0 1 0 0 Tm (X) Tj ET");

        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);

        Assert.DoesNotContain("NaN", svg);
        Assert.DoesNotContain("Infinity", svg);
    }

    /// <summary>
    /// An ordinary <c>cm</c> still applies — the control, so the guard cannot pass by having
    /// started ignoring every <c>cm</c>.
    /// </summary>
    [Fact]
    public void SvgExport_StillAppliesAFiniteCm()
    {
        byte[] pdf = Pdf("q 2 0 0 2 0 0 cm 0 0 1 rg 10 10 20 20 re f Q");

        var d = PathData(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));

        Assert.Equal("M20,20 L60,20 L60,60 L20,60 Z", d);
    }

    // ------------------------------------------------------------------

    private static string PathData(string svg)
    {
        var d = XDocument.Parse(svg).Descendants()
                         .Select(e => e.Attribute("d"))
                         .FirstOrDefault(a => a != null);
        Assert.NotNull(d);
        return d.Value.Trim();
    }

    private static byte[] Pdf(string content)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
