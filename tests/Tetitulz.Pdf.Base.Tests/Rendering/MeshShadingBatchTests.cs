// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Type 4 / type 5 Gouraud meshes (§8.7.4.5.5–6) are rasterised by accumulating their triangles
/// and issuing <b>one</b> <c>DrawVertices</c> call, as the Coons/tensor path does per patch,
/// rather than allocating a position array, a colour array, an <c>SKVertices</c> and an
/// <c>SKPaint</c> for every triangle.
///
/// <para><b>What this file pins, and why the numbers are literal.</b> Batching changes when
/// triangles are handed to Skia, which is exactly the kind of change that can move pixels without
/// anyone noticing — draw order, and edge antialiasing between neighbours in one call. The exact
/// colours below are those of a triangle-at-a-time render, so they are a two-sided claim: the
/// Gouraud interpolation is right, and the batching does not disturb it. (Measured: Skia's CPU
/// <c>DrawVertices</c> does not antialias triangle edges whether they are batched or not, so
/// batching neither adds nor removes seams along shared edges.)</para>
/// </summary>
public class MeshShadingBatchTests
{
    /// <summary>
    /// A 3x3 lattice is four cells, eight triangles, nine distinct vertex colours — so every probe
    /// below sits inside a different interpolation and no single flat fill can satisfy the set.
    /// </summary>
    [Fact]
    public void LatticeMeshGouraud_InterpolatesEveryCellFromItsOwnFourVertices()
    {
        using var bmp = PixelAssert.Render(GradientLatticePdf());

        Assert.True(PixelAssert.Near(bmp.GetPixel(30, 30), new SKColor(0xFE, 0xDE, 0xFE), 2),
            $"top-left cell, got {bmp.GetPixel(30, 30)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(100, 40), new SKColor(0x9D, 0x63, 0xA0), 2),
            $"top edge, between two cells, got {bmp.GetPixel(100, 40)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(60, 100), new SKColor(0xFE, 0x7E, 0x81), 2),
            $"left edge, got {bmp.GetPixel(60, 100)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(100, 100), new SKColor(0xFC, 0x02, 0xFF), 2),
            $"lattice centre — the shared corner of all four cells, got {bmp.GetPixel(100, 100)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(140, 100), new SKColor(0x7C, 0x82, 0xFF), 2),
            $"right of centre, got {bmp.GetPixel(140, 100)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(100, 160), new SKColor(0x3E, 0xBE, 0x41), 2),
            $"bottom edge, got {bmp.GetPixel(100, 160)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(170, 170), new SKColor(0x1C, 0x02, 0xFF), 2),
            $"bottom-right cell, got {bmp.GetPixel(170, 170)}");
        // Three points that sit on a cell's shared diagonal, where a per-triangle draw would have
        // to blend against nothing if it antialiased its own edges.
        Assert.True(PixelAssert.Near(bmp.GetPixel(41, 40), new SKColor(0xFC, 0xBD, 0xFC), 2),
            $"on the first cell's diagonal, got {bmp.GetPixel(41, 40)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(100, 99), new SKColor(0xFB, 0x04, 0xFE), 2),
            $"on a diagonal at the lattice centre, got {bmp.GetPixel(100, 99)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(150, 149), new SKColor(0x5C, 0x05, 0xFF), 2),
            $"on the last cell's diagonal, got {bmp.GetPixel(150, 149)}");
        Assert.InRange(PixelAssert.CountInk(bmp), 25100, 25400);
    }

    /// <summary>
    /// Type 4's edge flags: 0 0 0 builds an independent triangle, 1 continues it as a <b>strip</b>
    /// (reuse the previous b,c) and 2 as a <b>fan</b> (reuse the previous a,c). The fan triangle
    /// here deliberately overlaps the strip triangle, so the pixel where they cross is a claim
    /// about <b>draw order</b>: Skia rasterises the triangles of one <c>DrawVertices</c> call in
    /// array order, so the later fan must still win. Batching them into one call is where that
    /// could silently have been lost.
    /// </summary>
    [Fact]
    public void FreeFormMesh_StripAndFanTriangles_KeepTheirColoursAndTheirDrawOrder()
    {
        using var bmp = PixelAssert.Render(FreeFormMeshPdf());

        Assert.True(PixelAssert.Near(bmp.GetPixel(30, 30), new SKColor(0x00, 0x11, 0xEE), 2),
            $"first triangle, near its blue vertex, got {bmp.GetPixel(30, 30)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(60, 100), new SKColor(0x40, 0x41, 0x7F), 2),
            $"first triangle interior, got {bmp.GetPixel(60, 100)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(100, 99), new SKColor(0x02, 0x80, 0x7F), 2),
            $"on the edge the strip shares with the first triangle, got {bmp.GetPixel(100, 99)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(100, 160), new SKColor(0x60, 0x80, 0x1F), 2),
            $"strip triangle interior, got {bmp.GetPixel(100, 160)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(170, 170), new SKColor(0x08, 0xFF, 0x1E), 2),
            $"strip triangle, near its green vertex, got {bmp.GetPixel(170, 170)}");

        // The fan's three vertices are green(0,255,0), yellow(255,255,0) and cyan(0,255,255) — all
        // with G = 255, so anywhere inside it interpolates to exactly 255 green. The strip
        // underneath it does not: it carries a blue vertex. G == 255 here is therefore "the fan
        // painted last", and it is the assertion that a reordered batch would break.
        SKColor overlap = bmp.GetPixel(140, 100);
        Assert.Equal(255, overlap.Green);
        Assert.True(PixelAssert.Near(overlap, new SKColor(0x5F, 0xFF, 0x7E), 2),
            $"fan over strip, got {overlap}");
        Assert.InRange(PixelAssert.CountInk(bmp), 25400, 25800);
    }

    /// <summary>
    /// Unbounded batching would be a new DoS surface, which is the whole reason
    /// <see cref="PdfLimits.MaxMeshTrianglesPerBatch"/> exists: the per-triangle code held three
    /// points at a time, so the peak allocation was constant, whereas one batch for the whole mesh
    /// is sized by the <em>file</em>. <c>PdfLimits.MaxMeshVertices</c> permits 4&#160;000&#160;000
    /// vertices, so a legal type-5 lattice reaches ~8&#160;M triangles — ~290&#160;MB of live
    /// arrays.
    ///
    /// <para>The guard's evidence is a <b>flush</b>, not a refusal, and both halves are asserted:
    /// the split is reported, and the region drawn <em>after</em> the first batch is still painted.
    /// A truncating implementation passes the first assertion and fails the second — the top of the
    /// mesh would simply be missing.</para>
    /// </summary>
    [Fact]
    public void OversizedLatticeMesh_IsFlushedInBoundedBatches_WithoutDroppingATriangle()
    {
        var splits = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == RenderDiagnosticKind.MeshBatchSplit) lock (splits) splits.Add(d ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Sink;
        SKBitmap big, small;
        try
        {
            // 260 x 260 vertices = 134 162 triangles: three batches at a 65 536 cap.
            big = PixelAssert.Render(UniformLatticePdf(260));
            int afterBig = splits.Count;
            small = PixelAssert.Render(GradientLatticePdf());
            Assert.Equal(afterBig, splits.Count);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        using (big)
        using (small)
        {
            Assert.NotEmpty(splits);
            Assert.Contains(splits, d => d.Contains(PdfLimits.MaxMeshTrianglesPerBatch.ToString()));

            // Rows are emitted bottom-to-top in PDF space, so one probe lands in each of the three
            // batches: device row 150 in the first, row 50 in the second, row 21 in the short
            // final one that only the closing flush draws.
            Assert.True(PixelAssert.Near(big.GetPixel(100, 150), SKColors.Red, 4),
                $"first batch: got {big.GetPixel(100, 150)}");
            Assert.True(PixelAssert.Near(big.GetPixel(100, 50), SKColors.Red, 4),
                $"drawn after a flush — a truncating batch would leave this unpainted; " +
                $"got {big.GetPixel(100, 50)}");
            Assert.True(PixelAssert.Near(big.GetPixel(100, 21), SKColors.Red, 4),
                $"the last partial batch needs the closing flush; got {big.GetPixel(100, 21)}");
        }
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>3x3 type-5 lattice, nine distinct vertex colours: Gouraud across four cells.</summary>
    private static byte[] GradientLatticePdf()
    {
        var hex = new StringBuilder();
        string[] colours = { "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
                             "FFFFFF", "808080", "000000" };
        int k = 0;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                hex.Append($"{c * 127:X2}{r * 127:X2}{colours[k++]}");
        return MeshPdf("/ShadingType 5 /ColorSpace /DeviceRGB /VerticesPerRow 3" +
                       " /BitsPerCoordinate 8 /BitsPerComponent 8" +
                       " /Decode [20 180 20 180 0 1 0 1 0 1] /Filter /ASCIIHexDecode",
                       hex + ">");
    }

    /// <summary>An <paramref name="n"/> x <paramref name="n"/> all-red type-5 lattice.</summary>
    private static byte[] UniformLatticePdf(int n)
    {
        var hex = new StringBuilder(n * n * 10 + 16);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                hex.Append($"{c * 255 / (n - 1):X2}{r * 255 / (n - 1):X2}FF0000");
        return MeshPdf($"/ShadingType 5 /ColorSpace /DeviceRGB /VerticesPerRow {n}" +
                       " /BitsPerCoordinate 8 /BitsPerComponent 8" +
                       " /Decode [20 180 20 180 0 1 0 1 0 1] /Filter /ASCIIHexDecode",
                       hex + ">");
    }

    /// <summary>Type 4 with flags 0, 0, 0, 1 (strip) then 2 (fan) — the topology cursor.</summary>
    private static byte[] FreeFormMeshPdf()
    {
        const string v =
            "00" + "00" + "00" + "FF0000" +
            "00" + "FF" + "00" + "00FF00" +
            "00" + "00" + "FF" + "0000FF" +
            "01" + "FF" + "FF" + "FFFF00" +
            "02" + "80" + "40" + "00FFFF";
        return MeshPdf("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8" +
                       " /BitsPerComponent 8 /BitsPerFlag 8" +
                       " /Decode [20 180 20 180 0 1 0 1 0 1] /Filter /ASCIIHexDecode",
                       v + ">");
    }

    private static byte[] MeshPdf(string shadingDict, string data) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            Stream(shadingDict, data));

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
