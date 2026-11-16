// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Where a soft mask lands on the page.
///
/// <para>
/// <b>Written before the group bitmap was bounded to its <c>/BBox</c>, deliberately.</b> That
/// change moves the mask's origin off device (0,0), and an offset applied in the wrong direction —
/// or in one of the three places the mask is consumed but not the others — misaligns every mask on
/// every page. Nothing throws; the artwork is simply masked in the wrong place, which is the
/// silent-wrong-output shape this codebase keeps finding. These pass against the unbounded
/// implementation, so they are a regression harness rather than a description of the new one.
/// </para>
///
/// <para>
/// <c>SoftMaskCtmTests</c> covers a mask under a scaled CTM; this covers a mask whose group is
/// <i>small relative to the page</i>, which is the case bounding actually changes.
/// </para>
/// </summary>
public class SoftMaskAlignmentTests
{
    private const int Dpi = 72;   // 1 pt == 1 px, so PDF units are pixel indices

    /// <summary>
    /// The mask reveals a 60x60 window at (100,100) on a 300x300 page; a red rectangle covers the
    /// whole page. Ink must appear inside the window and nowhere else — which pins the mask's
    /// position in both axes, so an offset applied with the wrong sign fails rather than merely
    /// shifting the result somewhere else that still has ink.
    /// </summary>
    [Fact]
    public void MaskWindow_RevealsExactlyItsOwnRegion()
    {
        using SKBitmap bmp = Render(WindowMaskPdf());

        // Inside the window.
        AssertRed(bmp, 130, 130, expectRed: true);
        AssertRed(bmp, 105, 105, expectRed: true);
        AssertRed(bmp, 155, 155, expectRed: true);

        // Outside it, on all four sides — each catches a shift in one direction.
        AssertRed(bmp, 130, 60, expectRed: false);    // above
        AssertRed(bmp, 130, 200, expectRed: false);   // below
        AssertRed(bmp, 60, 130, expectRed: false);    // left
        AssertRed(bmp, 200, 130, expectRed: false);   // right
    }

    /// <summary>
    /// The same window under a CTM that translates the drawing. The mask is built with the CTM in
    /// force at the <c>gs</c>, so it must move with it — a bounded bitmap whose offset ignored the
    /// CTM would leave the mask behind at the untranslated position.
    /// </summary>
    [Fact]
    public void MaskWindow_MovesWithTheCtm()
    {
        using SKBitmap bmp = Render(WindowMaskPdf(translateX: 80, translateY: 40));

        // Window now at (180,140)-(240,200) in PDF space.
        AssertRed(bmp, 210, 170, expectRed: true);

        // The original position must now be clear.
        AssertRed(bmp, 130, 130, expectRed: false);
    }

    /// <summary>
    /// A mask window that starts at the page origin — the case where an offset of (0,0) and a
    /// correctly-computed offset are indistinguishable, so it guards against a bounded
    /// implementation that only ever works when the group happens to sit at the corner.
    /// </summary>
    [Fact]
    public void MaskWindowAtTheOrigin_StillRevealsOnlyItsRegion()
    {
        using SKBitmap bmp = Render(WindowMaskPdf(windowX: 0, windowY: 0));

        AssertRed(bmp, 30, 30, expectRed: true);
        AssertRed(bmp, 120, 120, expectRed: false);
    }

    // ------------------------------------------------------------------

    private static void AssertRed(SKBitmap bmp, float userX, float userY, bool expectRed)
    {
        int x = (int)userX;
        int y = bmp.Height - 1 - (int)userY;
        SKColor c = bmp.GetPixel(x, y);

        bool isRed = c.Red > 150 && c.Green < 110 && c.Blue < 110;
        Assert.True(isRed == expectRed,
            $"at PDF ({userX},{userY}) expected {(expectRed ? "red" : "no red")}, got {c}");
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A 300x300 page. The mask group paints white (reveal) in a 60x60 window and nothing
    /// elsewhere; the page then fills red everywhere under that mask.
    /// </summary>
    private static byte[] WindowMaskPdf(float windowX = 100, float windowY = 100,
                                        float translateX = 0, float translateY = 0)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        string cm = (translateX != 0 || translateY != 0)
            ? $"1 0 0 1 {translateX:0.##} {translateY:0.##} cm\n"
            : "";

        // The mask group's own content sits at the window position in its own space; the /BBox is
        // tight around it, which is exactly what a bounded render would use.
        string maskContent = $"1 1 1 rg {windowX:0.##} {windowY:0.##} 60 60 re f\n";
        string content = $"{cm}/GM gs\n1 0 0 rg 0 0 300 300 re f\n";

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] "
             + "/Resources << /ExtGState << /GM 6 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5);
        Emit($"<< /Type /XObject /Subtype /Form /BBox [{windowX:0.##} {windowY:0.##} "
             + $"{windowX + 60:0.##} {windowY + 60:0.##}] "
             + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >> "
             + $"/Resources << >> /Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream\nendobj\n");
        BeginObj(6);
        Emit("<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >>\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
