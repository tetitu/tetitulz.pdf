// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Regression test for ExtGState luminosity soft-mask placement under a
/// non-identity base CTM (PDF §11.6.5.2: the soft-mask transparency group is
/// painted with the CTM in effect when the mask is established).
///
/// Real-world trigger: <c>raw/Windscribe Receipt.pdf</c>, a browser "print to
/// PDF" receipt whose whole page is drawn under a <c>0.24 0 0 -0.24 0 H cm</c>
/// scale.  Its drop-shadow is a black fill modulated by a luminosity mask; when
/// the mask group was rendered with an identity CTM instead of the page scale,
/// the shadow rendered too dark and offset down-and-right from the card.
/// </summary>
public class SoftMaskCtmTests
{
    [Fact]
    public void LuminosityMask_UnderScaledBaseCtm_LandsInTransformedLocation()
    {
        // Page is 100×100 pt, rendered at 72 dpi → 100×100 px.
        //
        // Base CTM = scale 2, so user space 0..50 maps to device 0..100.
        //  * The black fill `0 0 50 50 re f` covers the WHOLE page.
        //  * The mask group fills white over user x∈[0,25] (full height), which
        //    under the 2× CTM is the LEFT HALF of the page (device x∈[0,50]).
        //
        // Correct (CTM applied): left half black, right half white.
        // Buggy   (identity CTM): the mask white rect lands at device x∈[0,25]
        //    only, so device x=40 would be UNmasked → white. The x=40 black
        //    assertion is what an identity CTM fails.
        byte[] pdf = BuildLuminosityMaskPdf();
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);

        using var data = SKData.CreateCopy(png);
        using var bmp = SKBitmap.Decode(data);
        Assert.NotNull(bmp);

        // Map a logical (0..100) coordinate to the actual bitmap size.
        SKColor At(int x100, int y100) =>
            bmp.GetPixel(x100 * (bmp.Width - 1) / 100, y100 * (bmp.Height - 1) / 100);

        SKColor leftInner  = At(15, 60);  // left half  → masked-in → black
        SKColor leftEdge   = At(40, 60);  // still left half → black (the key check)
        SKColor rightInner = At(60, 60);  // right half → masked-out → page white
        SKColor rightFar   = At(85, 60);  // right half → white

        Assert.True(leftInner.Red < 90,
            $"left half should be painted black, got R={leftInner.Red}");
        Assert.True(leftEdge.Red < 90,
            $"device x=40 is inside the CTM-scaled mask (left half) and must be black; " +
            $"got R={leftEdge.Red} (pre-fix this was page-white because the mask was " +
            $"rendered with an identity CTM)");
        Assert.True(rightInner.Red > 200,
            $"right half is outside the mask and must show white page background, got R={rightInner.Red}");
        Assert.True(rightFar.Red > 200,
            $"right-far should be white page background, got R={rightFar.Red}");
    }

    // ------------------------------------------------------------------
    //  PDF builder — minimal valid PDF with an ExtGState /SMask /Luminosity
    // ------------------------------------------------------------------
    private static byte[] BuildLuminosityMaskPdf()
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }
        void EndObj() => Emit("endobj\n");

        Emit("%PDF-1.7\n");

        // 1: catalog, 2: pages
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\n"); EndObj();
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n"); EndObj();

        // 3: page — references the ExtGState that carries the soft mask
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]\n" +
             "   /Resources << /ExtGState << /GM 6 0 R >> >>\n" +
             "   /Contents 4 0 R >>\n");
        EndObj();

        // 4: content — scale 2, set mask, fill whole page black
        string cs = "2 0 0 2 0 0 cm\n/GM gs\n0 0 0 rg\n0 0 50 50 re f\n";
        BeginObj(4);
        Emit($"<< /Length {Encoding.Latin1.GetByteCount(cs)} >>\nstream\n{cs}\nendstream\n");
        EndObj();

        // 5: form XObject — the luminosity mask group; white over the left part
        string fs = "1 1 1 rg\n0 0 25 50 re f\n";
        BeginObj(5);
        Emit($"<< /Type /XObject /Subtype /Form /BBox [0 0 50 50]\n" +
             $"   /Group << /Type /Group /S /Transparency /CS /DeviceGray >>\n" +
             $"   /Resources << >> /Length {Encoding.Latin1.GetByteCount(fs)} >>\nstream\n{fs}\nendstream\n");
        EndObj();

        // 6: ExtGState with a luminosity soft mask sourcing form 5
        BeginObj(6);
        Emit("<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >>\n");
        EndObj();

        int xrefOffset = acc.Count;
        const int objCount = 7;
        Emit($"xref\n0 {objCount}\n");
        Emit("0000000000 65535 f \n");
        for (int i = 1; i < objCount; i++)
            Emit(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {objCount} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return acc.ToArray();
    }
}
