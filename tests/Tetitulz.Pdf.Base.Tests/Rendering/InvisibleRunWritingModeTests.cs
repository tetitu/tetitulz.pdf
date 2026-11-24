// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An invisible run (render mode 3 — the OCR-overlay case) must advance the text matrix in the
/// font's <b>writing mode</b>, exactly as the visible path does.
/// </summary>
/// <remarks>
/// <para><c>AdvanceTm</c> composes <c>[1 0 0 1 tx 0]</c> — a purely horizontal step — so an
/// invisible run advancing through it without consulting <c>GetWMode</c> would lay a vertical CJK
/// page's OCR overlay (an Identity-V font under <c>3 Tr</c>) out <b>left to right</b>, while the SVG
/// exporter and this same renderer's <i>visible</i> path lay it top to bottom: the renderer
/// disagreeing with itself between render mode 3 and render mode 0 for one font and one
/// string.</para>
///
/// <para><b>How an invisible run is observed at all.</b> It draws nothing, so its advance is only
/// visible in where the <i>next</i> glyph lands. Each fixture shows one glyph under <c>3 Tr</c> and
/// then one under <c>0 Tr</c>; the ink that appears belongs entirely to the second glyph, so its
/// position <b>is</b> the first glyph's advance. That is CLAUDE.md's rule for advance defects made
/// concrete: the glyph that fails stays correct and only the ones after it move.</para>
///
/// <para><b>Two other instruments cannot see this.</b> A pixel-comparing raster/SVG parity test
/// cannot, because each renderer stays internally self-consistent. And
/// <c>PdfStructuredTextExtractor</c> has its own text-position state machine and never reaches
/// <c>DrawInvisibleRun</c>.</para>
/// </remarks>
public class InvisibleRunWritingModeTests
{
    private const float StartX = 100f, StartY = 700f;

    /// <summary>
    /// <b>The defect.</b> With a writing-mode-1 font, the glyph following an invisible one must sit
    /// below the start point, not to its right.
    /// </summary>
    [Fact]
    public void AVerticalInvisibleRunAdvancesDownward()
    {
        SKRectI ink = InkBounds(Pdf(vertical: true));

        // Device space: x grows right, y grows DOWN from the page top.
        Assert.True(ink.Left < StartX + 12,
            $"the following glyph moved right, so the invisible run advanced horizontally; ink at x={ink.Left}");
        Assert.True(ink.Top > (800 - StartY) + 6,
            $"the following glyph did not move down; ink at y={ink.Top}");
    }

    /// <summary>
    /// <b>The control.</b> A horizontal font must still advance across — otherwise the rule could be
    /// "always advance downward", which is the same defect mirrored.
    /// </summary>
    [Fact]
    public void AHorizontalInvisibleRunStillAdvancesAcross()
    {
        SKRectI ink = InkBounds(Pdf(vertical: false));

        Assert.True(ink.Left > StartX + 12,
            $"the following glyph should have moved right; ink at x={ink.Left}");
    }

    /// <summary>
    /// Guards the fixture: the second glyph must actually paint, or both assertions above are about
    /// an empty page.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFixtureReallyPaintsTheFollowingGlyph(bool vertical)
    {
        SKRectI ink = InkBounds(Pdf(vertical));

        Assert.True(ink.Width > 0 && ink.Height > 0, "no ink on the page");
    }

    // ------------------------------------------------------------------

    /// <summary>Bounding box of every non-white pixel, in device space.</summary>
    private static SKRectI InkBounds(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        using SKBitmap bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha == 0 || (c.Red > 200 && c.Green > 200 && c.Blue > 200)) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }

        return maxX < 0
            ? SKRectI.Empty
            : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// One page showing an <b>invisible</b> CID glyph and then a <b>visible</b> one, through a
    /// Type 0 font that is either Identity-H (writing mode 0) or Identity-V (writing mode 1).
    /// </summary>
    private static byte[] Pdf(bool vertical)
    {
        string encoding = vertical ? "/Identity-V" : "/Identity-H";
        string content =
            $"BT /F0 24 Tf {StartX} {StartY} Td 3 Tr <0003> Tj 0 Tr <0004> Tj ET";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 800] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding {encoding} "
                + "/DescendantFonts [6 0 R] >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 7 0 R /DW 1000 /W [3 [1000 1000]] "
                + "/DW2 [880 -1000] >>\nendobj\n",
            "7 0 obj\n<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 "
                + "/Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 "
                + "/FontBBox [0 -120 1000 880] >>\nendobj\n",
        };
        return Build(objs);
    }

    private static byte[] Build(List<string> objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
