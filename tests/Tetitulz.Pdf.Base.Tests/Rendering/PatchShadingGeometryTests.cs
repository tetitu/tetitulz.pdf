// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Coons (type 6) and tensor-product (type 7) patch meshes drawn where ISO 32000-2 §8.7.4.5.7 and
/// §8.7.4.5.8 put them: the four colours at the corners of points 0, 3, 6 and 9, the tensor's
/// sixteen points read in the stream's spiral order, a continuing patch's first edge taken from
/// the edge of the previous one its flag names — and the meshes' work held to the vertex ceiling.
/// </summary>
/// <remarks>
/// <para>Every fixture paints the colour field (x, y, 0) — red is the x coordinate and green the
/// y, both in 0–240 — by giving each corner the field's colour at its own position. A square patch
/// with evenly spaced control points is the identity map, and the field is bilinear, so every pixel
/// has one right answer and a patch drawn turned, mirrored or bent shows as the wrong colour where
/// it lands. The earlier tests painted all four corners red, which no corner order can get wrong.</para>
/// <para>The renderer had the colours at the wrong corners (the second and fourth swapped), read
/// the tensor's points as a row-major matrix, gave every continuing patch the previous patch's last
/// four points — no edge at all — and drew any number of patches.</para>
/// </remarks>
[Collection(PdfLimitsCollection.Name)]
public sealed class PatchShadingGeometryTests
{
    // Probes across the page: the corners, the thirds (where a misplaced interior point of a
    // tensor patch moves the surface furthest) and the centre.
    private static readonly (int X, int Y)[] Probes =
    {
        (8, 8), (8, 232), (232, 232), (232, 8),
        (80, 80), (80, 160), (160, 160), (160, 80), (120, 120), (60, 200), (200, 40),
    };

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void APatchsColoursAreAtTheCornersOfItsPoints0369(int shadingType)
    {
        (int X, int Y)[] square =
        {
            (0, 0), (0, 80), (0, 160), (0, 240), (80, 240), (160, 240),
            (240, 240), (240, 160), (240, 80), (240, 0), (160, 0), (80, 0),
        };

        using var bmp = PixelAssert.Render(PatchPdf(shadingType, FirstPatch(shadingType, square)));

        foreach (var (x, y) in Probes)
            AssertField(bmp, x, y);
    }

    /// <summary>
    /// A second patch continuing from the first by each edge flag: the first is the left half of
    /// the page, labelled so that the edge its flag names is the line x = 120; the second carries
    /// only the right half's points and its two far colours. Both halves paint the field.
    /// </summary>
    [Theory]
    [InlineData(6, 1)]
    [InlineData(6, 2)]
    [InlineData(6, 3)]
    [InlineData(7, 1)]
    [InlineData(7, 2)]
    [InlineData(7, 3)]
    public void AContinuingPatchSharesTheEdgeItsFlagNames(int shadingType, int flag)
    {
        using var bmp = PixelAssert.Render(TwoPatchPdf(shadingType, flag));

        foreach (var (x, y) in Probes)
            AssertField(bmp, x, y);
    }

    /// <summary>
    /// Only a flag's two low bits are read (§8.7.4.5.7): a second patch flagged 4 is a new patch,
    /// with all its points and colours, not one continuing from the first. (5, 6 and 7 name the
    /// same edges as 1, 2 and 3 whether or not the high bit is dropped; 4 is the one that differs.)
    /// </summary>
    [Fact]
    public void AFlagsHighBitsAreNotRead()
    {
        var left = Enumerable.Range(0, 12).Select(k => LeftLoop[(k + 6) % 12]).ToArray();
        string right = FirstPatch(6, RightPatch);
        right = "04" + right.Substring(2);

        using var bmp = PixelAssert.Render(PatchPdf(6, FirstPatch(6, left) + right));

        foreach (var (x, y) in Probes)
            AssertField(bmp, x, y);
    }

    /// <summary>
    /// The patches are charged by the surface points they evaluate, against
    /// <see cref="PdfLimits.MaxMeshVertices"/>: under a ceiling of one patch's grid the second is
    /// not drawn, and that is said; under two, both are drawn and nothing is said.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void PatchesStopAtTheVertexCeiling_AndSaySo(int patchesAllowed, bool secondDrawn)
    {
        int steps = PdfLimits.BezierFlattenSteps;
        var reports = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == RenderDiagnosticKind.PatchShaderFailed) reports.Add(d ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Sink;
        SKBitmap bmp;
        try
        {
            PdfLimits.MaxMeshVertices = patchesAllowed * (steps + 1) * (steps + 1);
            bmp = PixelAssert.Render(TwoPatchPdf(6, 2));
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Sink;
            PdfLimits.ResetToDefaults();
        }

        using (bmp)
        {
            AssertField(bmp, 60, 120);
            if (secondDrawn)
            {
                AssertField(bmp, 180, 120);
                Assert.Empty(reports);
            }
            else
            {
                Assert.True(PixelAssert.Near(PixelAssert.At(bmp, 180, 120), SKColors.White, 2),
                    $"the second patch is past the ceiling; got {PixelAssert.At(bmp, 180, 120)}");
                Assert.Contains(reports, d => d.Contains("PdfLimits.MaxMeshVertices", StringComparison.Ordinal)
                                              && d.Contains("type 6", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// A free-form triangle mesh past the same ceiling draws the triangles it read and says the
    /// rest were not drawn; it stopped without a word.
    /// </summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(6, true)]
    public void ATriangleMeshStopsAtTheVertexCeiling_AndSaysSo(int vertexCeiling, bool secondDrawn)
    {
        var reports = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == RenderDiagnosticKind.MeshShaderFailed) reports.Add(d ?? string.Empty);
        }

        // Two red triangles, each a flag-0 start: the left one first.
        string vertices = Hex(new[]
        {
            0, 0, 0, 255, 0, 0,     0, 100, 0, 255, 0, 0,     0, 0, 240, 255, 0, 0,
            0, 140, 0, 255, 0, 0,   0, 240, 0, 255, 0, 0,     0, 240, 240, 255, 0, 0,
        });
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 240] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            Stream("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8" +
                   " /BitsPerFlag 8 /Decode [0 255 0 255 0 1 0 1 0 1] /Filter /ASCIIHexDecode",
                   vertices + ">"));

        RenderDiagnostics.OnEvent += Sink;
        SKBitmap bmp;
        try
        {
            PdfLimits.MaxMeshVertices = vertexCeiling;
            bmp = PixelAssert.Render(pdf);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Sink;
            PdfLimits.ResetToDefaults();
        }

        using (bmp)
        {
            Assert.True(PixelAssert.Near(PixelAssert.At(bmp, 10, 60), SKColors.Red, 4),
                $"the first triangle; got {PixelAssert.At(bmp, 10, 60)}");
            var second = PixelAssert.At(bmp, 230, 60);
            Assert.True(PixelAssert.Near(second, secondDrawn ? SKColors.Red : SKColors.White, 4),
                $"the second triangle; got {second}");
            if (secondDrawn) Assert.Empty(reports);
            else Assert.Contains(reports, d => d.Contains("PdfLimits.MaxMeshVertices", StringComparison.Ordinal));
        }
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    // The colour field: red is x and green is y, in the 0–240 the coordinates are in.
    private static void AssertField(SKBitmap bmp, int x, int y)
    {
        var c = PixelAssert.At(bmp, x, y);
        Assert.True(Math.Abs(c.Red - x) <= 4 && Math.Abs(c.Green - y) <= 4 && c.Blue <= 4,
            $"at ({x}, {y}) the field is ({x}, {y}, 0); got ({c.Red}, {c.Green}, {c.Blue})");
    }

    // The first patch's twelve boundary points round the left half, from the top of the line
    // x = 120 down, so that whichever flag a continuing patch carries, the edge it names (points
    // 3·flag to 3·flag + 3) is that line.
    private static readonly (int X, int Y)[] LeftLoop =
    {
        (120, 240), (120, 160), (120, 80), (120, 0), (80, 0), (40, 0),
        (0, 0), (0, 80), (0, 160), (0, 240), (40, 240), (80, 240),
    };

    // The continuing patch, all twelve: the shared edge, then on round the right half.
    private static readonly (int X, int Y)[] RightPatch =
    {
        (120, 240), (120, 160), (120, 80), (120, 0), (160, 0), (200, 0),
        (240, 0), (240, 80), (240, 160), (240, 240), (200, 240), (160, 240),
    };

    private static byte[] TwoPatchPdf(int shadingType, int flag)
    {
        var left = Enumerable.Range(0, 12).Select(k => LeftLoop[(k - 3 * flag + 12) % 12]).ToArray();

        // The continuing patch writes neither its first edge nor its first two colours.
        var rightPoints = RightPatch.Skip(4);
        if (shadingType == 7) rightPoints = rightPoints.Concat(Interior(RightPatch));
        string right = Hex(new[] { flag }
            .Concat(rightPoints.SelectMany(p => new[] { p.X, p.Y }))
            .Concat(new[] { RightPatch[6], RightPatch[9] }.SelectMany(Colour)));

        return PatchPdf(shadingType, FirstPatch(shadingType, left) + right);
    }

    // A flag-0 patch: its boundary, a tensor patch's four interior points, and the field's colour
    // at each corner.
    private static string FirstPatch(int shadingType, (int X, int Y)[] boundary)
    {
        IEnumerable<(int X, int Y)> points = boundary;
        if (shadingType == 7) points = points.Concat(Interior(boundary));
        return Hex(new[] { 0 }
            .Concat(points.SelectMany(p => new[] { p.X, p.Y }))
            .Concat(new[] { boundary[0], boundary[3], boundary[6], boundary[9] }.SelectMany(Colour)));
    }

    // A square tensor patch's interior points in the stream's order — p11, p12, p22, p21, where
    // p_ij is i thirds from point 0 towards point 9 and j thirds from point 0 towards point 3.
    private static IEnumerable<(int X, int Y)> Interior((int X, int Y)[] b)
    {
        (int X, int Y) At(int i, int j) =>
            (((3 - i) * (3 - j) * b[0].X + (3 - i) * j * b[3].X + i * j * b[6].X + i * (3 - j) * b[9].X) / 9,
             ((3 - i) * (3 - j) * b[0].Y + (3 - i) * j * b[3].Y + i * j * b[6].Y + i * (3 - j) * b[9].Y) / 9);

        yield return At(1, 1);
        yield return At(1, 2);
        yield return At(2, 2);
        yield return At(2, 1);
    }

    private static int[] Colour((int X, int Y) at) => new[] { at.X, at.Y, 0 };

    private static string Hex(IEnumerable<int> bytes) => string.Concat(bytes.Select(b => b.ToString("X2")));

    private static byte[] PatchPdf(int shadingType, string patches) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 240] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            Stream($"/ShadingType {shadingType} /ColorSpace /DeviceRGB /BitsPerCoordinate 8" +
                   " /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 255 0 255 0 1 0 1 0 1]" +
                   " /Filter /ASCIIHexDecode",
                   patches + ">"));

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
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
