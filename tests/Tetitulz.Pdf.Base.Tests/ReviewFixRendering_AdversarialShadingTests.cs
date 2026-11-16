// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Adversarial-input regression tests for code-review fixes in
/// <see cref="ContentStreamInterpreter"/>:
/// <list type="bullet">
///   <item>
///     Mesh-shading (ShadingType 4) bit-decoder must reject a <c>BitsPerCoordinate</c>
///     value &gt; 32. With <c>/BitsPerCoordinate 64</c> the shift <c>1L &lt;&lt; 64</c> is
///     masked to <c>1L &lt;&lt; 0</c>, making <c>maxCoord = 0</c> → divide-by-zero → NaN
///     vertex coordinates feeding <c>SKVertices</c>. The shading must be skipped (reported
///     via <see cref="RenderDiagnostics"/>) rather than crashing or blanking the page.
///   </item>
///   <item>
///     A <c>cm</c> / <c>Tm</c> operator with a non-finite operand must be ignored so it
///     cannot permanently poison the CTM / text matrix for the rest of the <c>q…Q</c>
///     scope and silently blank every later draw.
///   </item>
/// </list>
/// All PDFs are built in memory.
/// </summary>
public sealed class ReviewFixRendering_AdversarialShadingTests
{
    // A decimal real whose value (1e40) is finite as a double but overflows float
    // (float max ≈ 3.4e38) when cast in ContentOperand.Float. The trailing ".0" forces
    // the content-stream parser down its DoubleValue branch. (float)1e40 == +Infinity.
    private const string FloatInfinityReal =
        "10000000000000000000000000000000000000000.0";

    // ------------------------------------------------------------------
    //  1. Mesh shading with an out-of-range BitsPerCoordinate
    // ------------------------------------------------------------------

    [Fact]
    public void MeshShading_BitsPerCoordinate64_RendersWithoutCrash_AndPageNotBlank()
    {
        // The page paints a solid green rectangle, then invokes a Type 4 mesh shading
        // declaring /BitsPerCoordinate 64. Pre-fix the 64-bit field collapsed maxCoord to
        // 0 → NaN vertex coords. The fix rejects the shading; the green rectangle must
        // still be present (page not blank, no crash).
        byte[] pdf = BuildMeshShadingPdf(bitsPerCoordinate: 64);

        byte[] png = null;
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            png = PdfPageRenderer.RenderPage(ms, 0, 72);
        });

        Assert.Null(ex);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
        Assert.True(HasNonWhitePixel(png),
            "The page should still contain the green rectangle drawn before the rejected mesh shading.");
    }

    [Fact]
    public void MeshShading_ValidBitsPerCoordinate_PaintsTheTriangle_Regression()
    {
        // Sanity regression: a well-formed 16-bit mesh shading must continue to be *drawn* after
        // the >32-bit guard was added — a guard that rejected everything would pass a "no
        // exception" test just as happily as the correct one.  The fixture's three flag-0
        // vertices are all pure blue at (85,0), (100,0) and (100,15), a wedge in the bottom-right
        // corner that does not touch the green rectangle painted before it.
        using var bmp = PixelAssert.Render(BuildMeshShadingPdf(bitsPerCoordinate: 16));

        SKColor inWedge = PixelAssert.At(bmp, 96f, 4f);
        Assert.True(inWedge.Blue > 200 && inWedge.Red < 60 && inWedge.Green < 60,
            $"the 16-bit mesh triangle should be painted blue; got {inWedge}.");

        // The rectangle drawn before the shading survives …
        SKColor rect = PixelAssert.At(bmp, 50f, 50f);
        Assert.True(rect.Green > 200 && rect.Red < 60 && rect.Blue < 60,
            $"the green rectangle should be untouched by the shading; got {rect}.");

        // … and the mesh paints its triangle, not the whole clip region.
        SKColor elsewhere = PixelAssert.At(bmp, 5f, 4f);
        Assert.True(elsewhere.Red > 245 && elsewhere.Green > 245 && elsewhere.Blue > 245,
            $"the opposite corner is outside the triangle and must stay unpainted; got {elsewhere}.");
    }

    // ------------------------------------------------------------------
    //  2. Non-finite cm / Tm operands must not blank the rest of the page
    // ------------------------------------------------------------------

    [Fact]
    public void NonFiniteCm_DoesNotBlankRestOfPage()
    {
        // q  <Inf> 0 0 1 0 0 cm  ... Q  then a normal green fill.
        // Pre-fix the Inf operand poisoned GS.CTM for the rest of the q…Q scope, so the
        // green rectangle drawn afterwards vanished. The fix ignores the bad cm.
        string content =
            "q " + FloatInfinityReal + " 0 0 1 0 0 cm Q " +
            "0 1 0 rg 20 20 60 60 re f";
        byte[] pdf = BuildSimplePdf(content);

        byte[] png = null;
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            png = PdfPageRenderer.RenderPage(ms, 0, 72);
        });

        Assert.Null(ex);
        Assert.NotNull(png);
        Assert.True(HasNonWhitePixel(png),
            "Drawing after a non-finite cm should still appear; the CTM must not have been poisoned.");
    }

    [Fact]
    public void NonFiniteTm_DoesNotBlankSubsequentDrawing()
    {
        // A text object sets a non-finite text matrix, then the page fills a green rect.
        // The bad Tm must be ignored and the rectangle must still render.
        string content =
            "BT /F1 12 Tf " + FloatInfinityReal + " 0 0 1 0 0 Tm (X) Tj ET " +
            "0 1 0 rg 20 20 60 60 re f";
        byte[] pdf = BuildSimplePdf(content, withFont: true);

        byte[] png = null;
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            png = PdfPageRenderer.RenderPage(ms, 0, 72);
        });

        Assert.Null(ex);
        Assert.NotNull(png);
        Assert.True(HasNonWhitePixel(png),
            "Drawing after a non-finite Tm should still appear.");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>True if the decoded PNG contains at least one pixel that is not white.</summary>
    private static bool HasNonWhitePixel(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        if (bmp == null) return false;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red != 255 || c.Green != 255 || c.Blue != 255)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a minimal one-page PDF whose content stream is <paramref name="content"/>.
    /// </summary>
    private static byte[] BuildSimplePdf(string content, bool withFont = false)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(sb.Length); sb.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        string res = withFont
            ? "/Resources << /Font << /F1 5 0 R >> >>"
            : "/Resources << >>";
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R {res} >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        if (withFont)
            Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        return Finalize(sb, off);
    }

    /// <summary>
    /// Builds a minimal one-page PDF that paints a solid green rectangle then invokes a
    /// Type 4 (free-form Gouraud) mesh shading via the <c>sh</c> operator. The shading is a
    /// stream carrying one flag-0 triangle worth of vertex bits; <paramref name="bitsPerCoordinate"/>
    /// controls the (possibly adversarial) BitsPerCoordinate field.
    /// </summary>
    private static byte[] BuildMeshShadingPdf(int bitsPerCoordinate)
    {
        // Three flag-0 vertices laid out for the *valid* (16-bit) case: flag(1) + x(2) + y(2) +
        // RGB(3) = 8 bytes each. /Decode [0 100 …] maps 0xFFFF → 100, so the triangle is
        // (85,0) (100,0) (100,15) — a wedge in the bottom-right corner, well clear of the green
        // rectangle at (20,20)-(80,80) — and all three corners are pure blue.
        //
        // Two vertices would parse but draw nothing (a triangle needs three), which is how this
        // fixture previously satisfied a "renders without exception" test while painting nothing.
        //
        // With bpc=64 the same bytes are irrelevant: the shading is rejected on its declared bit
        // widths, before any vertex is read.
        byte[] meshData =
        {
            0x00, 0xD9, 0x99, 0x00, 0x00, 0x00, 0x00, 0xFF,
            0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF,
            0x00, 0xFF, 0xFF, 0x26, 0x66, 0x00, 0x00, 0xFF,
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(sb.Length); sb.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Paint a green rectangle first (proves the page is not blank), then run the shading.
        const string content = "0 1 0 rg 20 20 60 60 re f /Sh1 sh";
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
            "/Contents 4 0 R /Resources << /Shading << /Sh1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        // Type 4 mesh shading stream. Vertex layout: flag + 2 coords + N components.
        var meshSb = new StringBuilder();
        meshSb.Append("5 0 obj\n<< /ShadingType 4 /ColorSpace /DeviceRGB ")
              .Append("/BitsPerCoordinate ").Append(bitsPerCoordinate).Append(' ')
              .Append("/BitsPerComponent 8 /BitsPerFlag 8 ")
              .Append("/Decode [0 100 0 100 0 1 0 1 0 1] ")
              .Append("/Length ").Append(meshData.Length).Append(" >>\nstream\n");
        // The shading stream contains binary mesh data between stream/endstream.
        off.Add(sb.Length);
        sb.Append(meshSb.ToString());
        foreach (byte b in meshData) sb.Append((char)b);
        sb.Append("\nendstream\nendobj\n");

        return Finalize(sb, off);
    }

    private static byte[] Finalize(StringBuilder sb, List<int> off)
    {
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(off.Count + 1).Append('\n')
          .Append("0000000000 65535 f \n");
        foreach (int o in off) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(off.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        // Latin-1 so the raw mesh bytes (0x80, 0xFF, …) round-trip 1:1 to bytes.
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
