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
/// Overprinting <b>text</b> (§11.7.4.2) — the case prepress files actually rely on: type set to
/// overprint so it does not knock a hole in the artwork beneath it.
///
/// <para>Text does not go through <c>FillPath</c>, so the path-geometry overprint compositor never
/// saw it. The run's glyph outlines are now captured — the same mechanism pattern-filled text
/// already used, so advance and positioning are unchanged — and composited per colorant.</para>
///
/// <para><b>Assertions are colour counts, not glyph shapes.</b> Which face the platform
/// substitutes for a non-embedded Helvetica differs between Windows and Linux, so pinning pixel
/// positions would be over-specifying the substitute (CLAUDE.md). What is asserted is the thing
/// under test: whatever glyphs get drawn, their ink is blue when overprinted onto cyan and magenta
/// when not.</para>
/// </summary>
public class OverprintTextTests
{
    private static readonly SKColor Magenta = new SKColor(255, 0, 255);
    private static readonly SKColor Blue    = new SKColor(0, 0, 255);

    [Fact]
    public void OverprintingText_Simulated_CompositesPerColorant()
    {
        using var bmp = Render(simulate: true);

        int blue    = PixelAssert.CountColor(bmp, Blue);
        int magenta = PixelAssert.CountColor(bmp, Magenta);

        Assert.True(blue > 40,
            $"overprinted magenta text on cyan should composite to blue ink; only {blue} blue pixels");
        Assert.True(magenta == 0,
            $"no ink should have stayed plain magenta, found {magenta} pixels");
    }

    [Fact]
    public void OverprintingText_NotSimulated_PaintsPlainMagenta()
    {
        using var bmp = Render(simulate: false);

        int blue    = PixelAssert.CountColor(bmp, Blue);
        int magenta = PixelAssert.CountColor(bmp, Magenta);

        Assert.True(magenta > 40,
            $"with simulation off the text should paint plain magenta; only {magenta} pixels");
        Assert.True(blue == 0,
            $"nothing should have been composited per colorant, found {blue} blue pixels");
    }

    /// <summary>
    /// The glyph-capture route must not lose or move the run. Ink coverage is compared between the
    /// two renders: capture-then-composite has to cover the same pixels the normal path painted.
    /// </summary>
    [Fact]
    public void OverprintingText_CoversTheSamePixelsAsANormalFill()
    {
        using var simulated = Render(simulate: true);
        using var plain     = Render(simulate: false);

        int simulatedInk = PixelAssert.CountColor(simulated, Blue);
        int plainInk     = PixelAssert.CountColor(plain, Magenta);

        Assert.True(plainInk > 40, $"the control render drew almost no text ({plainInk} px)");
        // Same geometry, same rasteriser — allow a small margin for anti-aliased edge pixels
        // landing just inside or outside the colour tolerance after compositing.
        int delta = System.Math.Abs(simulatedInk - plainInk);
        Assert.True(delta <= plainInk / 10,
            $"overprinted text covered {simulatedInk} px vs {plainInk} px normally — the capture " +
            "path moved or dropped glyphs");
    }

    // ====================================================================
    //  The stroke half — render modes that put ink under the pen
    // ====================================================================

    private static readonly SKColor Yellow = new SKColor(255, 255, 0);
    private static readonly SKColor Green  = new SKColor(0, 255, 0);

    /// <summary>
    /// Mode 1 strokes the glyphs, and the pen overprints like a path stroke does.
    /// </summary>
    /// <remarks>
    /// This mode used to be declined wholesale as "a text render mode other than 0" — the mode was
    /// reported and the pen was painted opaque. §11.7.4.2 does not distinguish a pen from a fill,
    /// and neither does the compositor now: the captured outline is stroked through the same
    /// coverage rasteriser a path stroke uses, so magenta pen over cyan page is blue.
    /// </remarks>
    [Fact]
    public void OverprintingStrokedText_Simulated_CompositesThePenPerColorant()
    {
        using var bmp = Render(StrokedText, simulate: true);

        Assert.True(PixelAssert.CountColor(bmp, Blue) > 40,
            "an overprinted magenta pen over cyan should composite to blue");
        Assert.Equal(0, PixelAssert.CountColor(bmp, Magenta));
    }

    [Fact]
    public void OverprintingStrokedText_NotSimulated_PaintsThePenPlainMagenta()
    {
        using var bmp = Render(StrokedText, simulate: false);

        Assert.True(PixelAssert.CountColor(bmp, Magenta) > 40,
            "with simulation off the pen should paint plain magenta");
        Assert.Equal(0, PixelAssert.CountColor(bmp, Blue));
    }

    /// <summary>
    /// Whether the pen overprints is decided by the <b>pen's</b> colorants, not the fill's.
    /// </summary>
    /// <remarks>
    /// <para><b>A mutation found the gap this closes.</b> Making the stroke decision read the fill
    /// colour's colorants survived every stroked fixture, because each of them paints the cyan
    /// page with a DeviceCMYK fill first — so the wrong colour still had a zero component, the run
    /// was routed anyway, and the compositor (which does read the pen) composited it correctly.
    /// The fixture never reached the decision.</para>
    /// <para>Here the fill is DeviceRGB and the pen DeviceCMYK. A decision reading the fill finds
    /// no colorants, declines the run, and paints the pen plain magenta; the correct one composites
    /// it to blue.</para>
    /// </remarks>
    [Fact]
    public void OverprintingStrokedText_IsDecidedByThePensColorants_NotTheFills()
    {
        const string rgbFillCmykPen =
            "1 0 0 0 k 0 0 200 100 re f " +
            "q /GS0 gs 1 0 0 rg 0 1 0 0 K 4 w BT /F1 60 Tf 1 Tr 10 25 Td (III) Tj ET Q";

        using var bmp = Render(rgbFillCmykPen, simulate: true);

        Assert.True(PixelAssert.CountColor(bmp, Blue) > 40,
            "the pen's own zero cyan should be what preserves the page's cyan");
        Assert.Equal(0, PixelAssert.CountColor(bmp, Magenta));
    }

    /// <summary>
    /// Mode 2 fills then strokes, and each half overprints with its <b>own</b> colour: the fill's
    /// magenta composites to blue, the pen's yellow composites to green.
    /// </summary>
    /// <remarks>
    /// Two colours on purpose. With one colour for both halves, a compositor that painted the fill
    /// twice — or stroked with the fill colour — would produce the same blue everywhere and pass.
    /// Green can only come from the stroke colorants meeting the page's cyan.
    /// </remarks>
    [Fact]
    public void OverprintingFilledAndStrokedText_CompositesEachHalfWithItsOwnColour()
    {
        using var bmp = Render(FilledAndStrokedText, simulate: true);

        Assert.True(PixelAssert.CountColor(bmp, Blue) > 40, "the fill half should composite to blue");
        Assert.True(PixelAssert.CountColor(bmp, Green) > 40, "the stroke half should composite to green");
        Assert.Equal(0, PixelAssert.CountColor(bmp, Magenta));
        Assert.Equal(0, PixelAssert.CountColor(bmp, Yellow));
    }

    /// <summary>
    /// Mode 6 fills, strokes <i>and</i> adds the glyphs to the clip. The overprint route captures
    /// the outlines, and a captured run must still clip what follows the text object.
    /// </summary>
    /// <remarks>
    /// The clip is the assertion that separates "routed through capture" from "painted normally":
    /// the glyph-run path used to return from capture before accumulating the clip, so an
    /// overprinting mode-6 run would have composited correctly and then let the rectangle after
    /// <c>ET</c> cover the whole page. Red is chosen for that rectangle because nothing else on the
    /// page can produce it, and the page is far larger than the glyphs, so the assertion is on how
    /// <b>little</b> red survives.
    /// </remarks>
    [Fact]
    public void OverprintingClippedText_StillClipsWhatFollows()
    {
        using var bmp = Render(FilledStrokedAndClippedText, simulate: true);

        int red = PixelAssert.CountColor(bmp, new SKColor(255, 0, 0));
        Assert.True(red > 0, "the rectangle after ET should show through the glyph clip");
        Assert.True(red < bmp.Width * bmp.Height / 4,
            $"the rectangle after ET was not clipped to the glyphs: {red} red pixels of {bmp.Width * bmp.Height}");
        Assert.True(PixelAssert.CountColor(bmp, Blue) + PixelAssert.CountColor(bmp, Green) > 0,
            "the glyphs themselves should still have been composited before the clip applied");
    }

    // ----------------------------------------------------------------

    /// <summary>Magenta pen, no fill, over the cyan page.</summary>
    private const string StrokedText =
        "1 0 0 0 k 0 0 200 100 re f " +
        "q /GS0 gs 0 1 0 0 K 4 w BT /F1 60 Tf 1 Tr 10 25 Td (III) Tj ET Q";

    /// <summary>Magenta fill and a yellow pen, over the cyan page.</summary>
    private const string FilledAndStrokedText =
        "1 0 0 0 k 0 0 200 100 re f " +
        "q /GS0 gs 0 1 0 0 k 0 0 1 0 K 4 w BT /F1 60 Tf 2 Tr 10 25 Td (III) Tj ET Q";

    /// <summary>As above in mode 6, then a red rectangle that the glyph clip must confine.</summary>
    private const string FilledStrokedAndClippedText =
        "1 0 0 0 k 0 0 200 100 re f " +
        "q /GS0 gs 0 1 0 0 k 0 0 1 0 K 4 w BT /F1 60 Tf 6 Tr 10 25 Td (III) Tj ET " +
        "1 0 0 rg 0 0 200 100 re f Q";

    private static SKBitmap Render(bool simulate) => Render(FilledText, simulate);

    private static SKBitmap Render(string content, bool simulate)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(BuildPdf(content)), 0, 72,
            new PdfRenderOptions { SimulateOverprint = simulate });
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    // Cyan page, then magenta text set to overprint. Simulated, the source's zero cyan
    // preserves the page's cyan → CMYK (1,1,0,0) = blue.
    private const string FilledText =
        "1 0 0 0 k 0 0 200 100 re f " +
        "q /GS0 gs 0 1 0 0 k BT /F1 60 Tf 10 25 Td (III) Tj ET Q";

    private static byte[] BuildPdf(string content)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(pdf.Length); pdf.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources " +
            "<< /ExtGState << /GS0 5 0 R >> /Font << /F1 6 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /ExtGState /OP true /op true /OPM 1 >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
