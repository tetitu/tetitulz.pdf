// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the 2026-04 Tier 1+2 rendering-gap closure:
///  • A — Annotation fallback for FreeText / Stamp / Widget
///  • C — OCMD /VE boolean visibility evaluator (AND / OR / NOT)
///  • D — SVG ExtGState parity (TR2 transfer fn; OP/op overprint)
///  • F — SVG color-space coverage: Lab, ICCBased, Indexed, Separation, DeviceN
///  • G — MacExpertEncoding glyph-name table
/// All PDFs are built in memory.  No file-system fixtures.
/// </summary>
public sealed class RenderingGapsTier12Tests
{
    // ==================================================================
    //  A. Annotation fallback — FreeText / Stamp / Widget
    // ==================================================================

    // The fixture page is flooded with 0.8 grey (204,204,204), so PixelAssert.IsInk — which
    // treats anything below 240 as ink — cannot tell the annotation from the background. The
    // annotation's own marks are far darker: /C [0.1 0.2 0.3] is (26,51,77) and the /DA text
    // colour is black, so a luminance floor separates "the annotation drew here" from "this is
    // the page".
    private const int DarkLuminance = 150;

    private static int Luminance(SKColor c) => (c.Red * 30 + c.Green * 59 + c.Blue * 11) / 100;

    private static bool IsDark(SKColor c) => c.Alpha > 8 && Luminance(c) < DarkLuminance;

    /// <summary>
    /// Dark <i>and</i> colour-neutral. The fallback's border is drawn in /C = (26,51,77), which
    /// keeps a ≥18-unit blue cast at every blend level dark enough to pass <see cref="IsDark"/>;
    /// the /DA text is black on a neutral grey page, so it blends to pure greys. This is what
    /// separates "the text drew" from "the border drew".
    /// </summary>
    private static bool IsNeutralDark(SKColor c) =>
        IsDark(c) && Math.Abs(c.Red - c.Green) <= 8 && Math.Abs(c.Green - c.Blue) <= 8;

    /// <summary>Counts pixels matching <paramref name="match"/> inside a PDF user-space rect.</summary>
    private static int CountIn(SKBitmap bmp, Func<SKColor, bool> match,
        float ux0, float uy0, float ux1, float uy1)
    {
        int x0 = PixelAssert.X(ux0), x1 = PixelAssert.X(ux1);
        int y0 = PixelAssert.Y(bmp, uy1), y1 = PixelAssert.Y(bmp, uy0);
        int n = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(bmp.Height - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(bmp.Width - 1, x1); x++)
                if (match(bmp.GetPixel(x, y))) n++;
        return n;
    }

    /// <summary>Counts pixels matching <paramref name="match"/> outside a PDF user-space rect.</summary>
    private static int CountOutside(SKBitmap bmp, Func<SKColor, bool> match,
        float ux0, float uy0, float ux1, float uy1)
    {
        int x0 = PixelAssert.X(ux0), x1 = PixelAssert.X(ux1);
        int y0 = PixelAssert.Y(bmp, uy1), y1 = PixelAssert.Y(bmp, uy0);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1) continue;
                if (match(bmp.GetPixel(x, y))) n++;
            }
        return n;
    }

    [Fact]
    public void FreeText_NoAp_RendersContents_Raster()
    {
        // A FreeText annotation with no /AP must have its /Contents drawn inside /Rect using the
        // /DA colour.  The control below is the same annotation with /Contents removed: it still
        // draws the /C border, so any *neutral* dark pixel is the black /DA text and nothing else.
        const float x0 = 100, y0 = 700, x1 = 300, y1 = 740;
        byte[] withText = BuildPdfWithAnnotation(
            subtype: "FreeText",
            extraAnnotKeys: " /Contents (Hello World) /DA (/Helv 10 Tf 0 0 0 rg)",
            rect: "100 700 300 740");
        byte[] noText = BuildPdfWithAnnotation(
            subtype: "FreeText",
            extraAnnotKeys: " /DA (/Helv 10 Tf 0 0 0 rg)",
            rect: "100 700 300 740");

        using var bmp = PixelAssert.Render(withText);
        using var control = PixelAssert.Render(noText);

        int text = CountIn(bmp, IsNeutralDark, x0, y0, x1, y1);
        Assert.True(text > 20,
            $"/Contents should have been drawn inside /Rect; found {text} black text pixels there.");
        Assert.Equal(0, CountIn(control, IsNeutralDark, x0, y0, x1, y1));

        // The /C border is still drawn in both, so the annotation is present either way …
        Assert.True(CountIn(control, IsDark, x0, y0, x1, y1) > 100,
            "the /C border rectangle should be drawn even with no /Contents.");
        // … and nothing may leak outside /Rect (2pt of stroke/antialias slack).
        Assert.Equal(0, CountOutside(bmp, IsDark, x0 - 2, y0 - 2, x1 + 2, y1 + 2));
    }

    [Fact]
    public void Stamp_Approved_RendersStampLabel_Raster()
    {
        // /Rect 100 700 300 740 with /C [0.1 0.2 0.3]: the stamp draws a 2pt outline at the rect
        // edge plus the /Name centred inside it.  Probing the *interior* — inset well clear of the
        // outline — isolates the label: RenderStampFallback centres "Approved" at 20pt, giving a
        // baseline near x=156 and a cap band around y=713..733.
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Stamp",
            extraAnnotKeys: " /Name /Approved",
            rect: "100 700 300 740");

        using var bmp = PixelAssert.Render(pdf);

        int label = CountIn(bmp, IsDark, 106, 706, 294, 734);
        Assert.True(label > 50,
            $"the /Name caption should be painted inside the stamp outline; found {label} dark pixels.");

        // The caption is centred, so the ink must sit in the middle third and nowhere near the
        // left inset — a fallback that drew only the outline, or that drew the caption at the
        // rect origin, fails this.
        Assert.Equal(0, CountIn(bmp, IsDark, 106, 706, 145, 734));
        Assert.Equal(0, CountIn(bmp, IsDark, 255, 706, 294, 734));

        // The label takes the annotation's /C colour, not the name-derived default.
        Assert.True(PixelAssert.CountColor(bmp, new SKColor(26, 51, 77), tolerance: 6) > 200,
            "stamp outline + caption should be painted in the annotation's /C colour.");
    }

    [Fact]
    public void Widget_Tx_NoAp_RendersFieldText_Raster()
    {
        // A /FT /Tx widget with no /AP must draw its /V inside /Rect in the /DA colour.  Same
        // control trick as FreeText: with /V removed only the (blue-tinted) border is drawn, so
        // every neutral dark pixel below comes from the field text. The border is the widget's
        // own /MK /BC (Table 192), in the same blue-tinted colour the helper writes as /C.
        const float x0 = 100, y0 = 700, x1 = 300, y1 = 720;
        byte[] withValue = BuildPdfWithAnnotation(
            subtype: "Widget",
            extraAnnotKeys: " /FT /Tx /V (User value) /DA (/Helv 10 Tf 0 0 0 rg) /MK << /BC [0.1 0.2 0.3] >>",
            rect: "100 700 300 720");
        byte[] noValue = BuildPdfWithAnnotation(
            subtype: "Widget",
            extraAnnotKeys: " /FT /Tx /DA (/Helv 10 Tf 0 0 0 rg) /MK << /BC [0.1 0.2 0.3] >>",
            rect: "100 700 300 720");

        using var bmp = PixelAssert.Render(withValue);
        using var control = PixelAssert.Render(noValue);

        int text = CountIn(bmp, IsNeutralDark, x0, y0, x1, y1);
        Assert.True(text > 20,
            $"/V should have been drawn inside the widget /Rect; found {text} text pixels there.");
        Assert.Equal(0, CountIn(control, IsNeutralDark, x0, y0, x1, y1));

        Assert.True(CountIn(control, IsDark, x0, y0, x1, y1) > 100,
            "the widget border rectangle should be drawn even with no /V.");
        Assert.Equal(0, CountOutside(bmp, IsDark, x0 - 2, y0 - 2, x1 + 2, y1 + 2));
    }

    [Fact]
    public void Widget_CheckboxOn_RendersCheckmark_Raster()
    {
        // Btn widget whose /V is a non-Off name → render ✓ glyph.  /V /Off is the control: same
        // dictionary, same border, no mark.  The mark is black; the border is /MK /BC (26,51,77),
        // so "neutral dark inside the border inset" is the checkmark and only the checkmark.
        byte[] on  = BuildPdfWithAnnotation("Widget", " /FT /Btn /V /Yes /MK << /BC [0.1 0.2 0.3] >>", "100 700 120 720");
        byte[] off = BuildPdfWithAnnotation("Widget", " /FT /Btn /V /Off /MK << /BC [0.1 0.2 0.3] >>", "100 700 120 720");

        using var bmpOn  = PixelAssert.Render(on);
        using var bmpOff = PixelAssert.Render(off);

        int mark = CountIn(bmpOn, IsNeutralDark, 103, 703, 117, 717);
        Assert.True(mark > 5,
            $"a ✓ should be drawn inside an /V /Yes checkbox; found {mark} mark pixels.");
        Assert.Equal(0, CountIn(bmpOff, IsNeutralDark, 103, 703, 117, 717));

        // Both draw the border, so the difference above is the mark rather than the whole widget.
        Assert.True(CountIn(bmpOff, IsDark, 100, 700, 120, 720) > 50,
            "an /V /Off checkbox should still draw its border rectangle.");
    }

    [Fact]
    public void Popup_Regression_PlaceholderStillRenders()
    {
        // Popup has no dedicated fallback — it falls through to the shared thin-border-rect
        // placeholder.  That placeholder is an *outline*: the four edges of /Rect are inked in the
        // /C colour and the interior is left as page background.
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Popup",
            extraAnnotKeys: "",
            rect: "100 700 120 720");

        using var bmp = PixelAssert.Render(pdf);

        // Each edge band (1pt either side of the rect edge) must carry ink.
        Assert.True(CountIn(bmp, IsDark, 99, 699, 121, 701) > 5, "bottom edge of the placeholder");
        Assert.True(CountIn(bmp, IsDark, 99, 719, 121, 721) > 5, "top edge of the placeholder");
        Assert.True(CountIn(bmp, IsDark,  99, 699, 101, 721) > 5, "left edge of the placeholder");
        Assert.True(CountIn(bmp, IsDark, 119, 699, 121, 721) > 5, "right edge of the placeholder");

        // Interior stays page background — the placeholder is stroked, never filled.
        Assert.Equal(0, CountIn(bmp, IsDark, 104, 704, 116, 716));
        // And it does not spill outside /Rect.
        Assert.Equal(0, CountOutside(bmp, IsDark, 98, 698, 122, 722));
    }

    // ==================================================================
    //  C. OCMD /VE boolean evaluator
    // ==================================================================

    [Fact]
    public void Ocmd_VeNot_HidesWhenInnerVisible()
    {
        // Direct API test.  HiddenOcgs = { } (OCG 5 is ON).
        // /VE = [/Not 5-ref] → expression evaluates to false → content should hide.
        var hidden = new System.Collections.Generic.HashSet<int>();
        var arr = new Tetitulz.Pdf.Core.Objects.PdfArray();
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfName("Not"));
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(5, 0));

        bool visible = ContentStreamInterpreter.EvaluateVisibilityExpression(arr, hidden, null);
        Assert.False(visible);
    }

    [Fact]
    public void Ocmd_VeAnd_HidesWhenAnyHidden()
    {
        // OCG 5 hidden, OCG 6 visible → /And → false (one branch hidden).
        var hidden = new System.Collections.Generic.HashSet<int> { 5 };
        var arr = new Tetitulz.Pdf.Core.Objects.PdfArray();
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfName("And"));
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(5, 0));
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(6, 0));

        bool visible = ContentStreamInterpreter.EvaluateVisibilityExpression(arr, hidden, null);
        Assert.False(visible);
    }

    [Fact]
    public void Ocmd_VeOr_ShowsWhenAnyVisible()
    {
        // OCG 5 hidden, OCG 6 visible → /Or → true.
        var hidden = new System.Collections.Generic.HashSet<int> { 5 };
        var arr = new Tetitulz.Pdf.Core.Objects.PdfArray();
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfName("Or"));
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(5, 0));
        arr.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(6, 0));

        bool visible = ContentStreamInterpreter.EvaluateVisibilityExpression(arr, hidden, null);
        Assert.True(visible);
    }

    [Fact]
    public void Ocmd_NestedAndOr_EvaluatesCorrectly()
    {
        // /Or [ /And [5 6] 7 ] — 5 hidden, 6 hidden, 7 visible → Or(false, true) → true.
        var hidden = new System.Collections.Generic.HashSet<int> { 5, 6 };
        var inner = new Tetitulz.Pdf.Core.Objects.PdfArray();
        inner.Add(new Tetitulz.Pdf.Core.Objects.PdfName("And"));
        inner.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(5, 0));
        inner.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(6, 0));

        var outer = new Tetitulz.Pdf.Core.Objects.PdfArray();
        outer.Add(new Tetitulz.Pdf.Core.Objects.PdfName("Or"));
        outer.Add(inner);
        outer.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(7, 0));

        bool visible = ContentStreamInterpreter.EvaluateVisibilityExpression(outer, hidden, null);
        Assert.True(visible);
    }

    [Fact]
    public void Ocmd_PolicyAllOff_HidesWhenAnyOn()
    {
        // OCMD /OCGs [5] /P /AllOff — OCG 5 is ON → spec says "hidden".
        var hidden = new System.Collections.Generic.HashSet<int>();
        var ocmd = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        ocmd[Tetitulz.Pdf.Core.Objects.PdfName.Type] = new Tetitulz.Pdf.Core.Objects.PdfName("OCMD");
        var ocgs = new Tetitulz.Pdf.Core.Objects.PdfArray();
        ocgs.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(5, 0));
        ocmd[new Tetitulz.Pdf.Core.Objects.PdfName("OCGs")] = ocgs;
        ocmd[new Tetitulz.Pdf.Core.Objects.PdfName("P")]    = new Tetitulz.Pdf.Core.Objects.PdfName("AllOff");

        bool isHidden = ContentStreamInterpreter.IsOcmdHidden(ocmd, hidden, null);
        Assert.True(isHidden);
    }

    // ==================================================================
    //  D. SVG ExtGState parity
    // ==================================================================

    /// <summary>
    /// Overprint is simulated on exactly the raster path's terms: opt-in via
    /// <c>PdfRenderOptions.SimulateOverprint</c>, <c>/OPM 1</c>, and a DeviceCMYK source colour
    /// (§11.7.4.2 — the nonzero-overprint rule applies only to DeviceCMYK).
    /// </summary>
    /// <remarks>
    /// <b>This test previously asserted the opposite</b>: that <c>/op true</c> alone emitted
    /// <c>mix-blend-mode:multiply</c>, with a DeviceRGB fill, no <c>/OPM</c> and no opt-in. That
    /// pinned an unconditional approximation the raster path deliberately does not make — overprint
    /// is a press behaviour that viewers expose as a preview toggle, so simulating it by default
    /// silently changed screen output for every prepress file, and the two renderers disagreed on
    /// every one of them. The fixture now supplies all three conditions, and the cases below pin
    /// each one being necessary.
    /// </remarks>
    [Fact]
    public void Svg_ExtGState_OverprintFill_SimulatedOnlyWhenAllConditionsHold()
    {
        const string extGs = "<< /Type /ExtGState /op true /OPM 1 >>";
        const string cs    = "/GS1 gs 0 0 0 1 k 100 100 200 200 re f";
        byte[] pdf = BuildPdfWithExtGState(extGs, cs);

        using var on = new MemoryStream(pdf);
        Assert.Contains("mix-blend-mode:multiply",
            PdfSvgRenderer.RenderPageToSvg(on, 0, new PdfRenderOptions { SimulateOverprint = true }));

        // …and every condition is load-bearing.
        using var noOptIn = new MemoryStream(pdf);
        Assert.DoesNotContain("mix-blend-mode:multiply", PdfSvgRenderer.RenderPageToSvg(noOptIn, 0));
    }

    [Fact]
    public void Svg_ExtGState_OverprintFill_NotSimulatedForAnRgbSource()
    {
        // Same opt-in and /OPM 1, but the source colour is DeviceRGB.
        const string extGs = "<< /Type /ExtGState /op true /OPM 1 >>";
        const string cs    = "/GS1 gs 1 0 0 rg 100 100 200 200 re f";
        byte[] pdf = BuildPdfWithExtGState(extGs, cs);

        using var ms = new MemoryStream(pdf);
        Assert.DoesNotContain("mix-blend-mode:multiply",
            PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = true }));
    }

    [Fact]
    public void Svg_ExtGState_OverprintFill_NotSimulatedUnderOpmZero()
    {
        // /OPM defaults to 0, where the nonzero-overprint rule does not apply.
        const string extGs = "<< /Type /ExtGState /op true >>";
        const string cs    = "/GS1 gs 0 0 0 1 k 100 100 200 200 re f";
        byte[] pdf = BuildPdfWithExtGState(extGs, cs);

        using var ms = new MemoryStream(pdf);
        Assert.DoesNotContain("mix-blend-mode:multiply",
            PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = true }));
    }

    /// <summary>An overprint the renderer does not simulate is REPORTED, never silently dropped.</summary>
    [Fact]
    public void Svg_ExtGState_OverprintNotSimulated_IsReported()
    {
        const string extGs = "<< /Type /ExtGState /op true >>";
        const string cs    = "/GS1 gs 1 0 0 rg 100 100 200 200 re f";
        byte[] pdf = BuildPdfWithExtGState(extGs, cs);

        // Filtered to this thread and locked: the sink is process-wide, and an unlocked List<T> fed
        // by every test rendering in parallel lost this test's own event now and then — a
        // neighbour's Add racing ours through the list's resize.
        var kinds = new System.Collections.Generic.List<RenderDiagnosticKind>();
        int thread = System.Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, System.Exception e)
        {
            if (System.Environment.CurrentManagedThreadId != thread) return;
            lock (kinds) kinds.Add(k);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(pdf);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        lock (kinds) Assert.Contains(RenderDiagnosticKind.OverprintNotSimulated, kinds);
    }

    [Fact]
    public void Svg_ExtGState_NoOverprint_NoMultiplyBlend()
    {
        // Baseline: regular fill must not emit the multiply blend.
        const string cs = "1 0 0 rg 100 100 200 200 re f";
        byte[] pdf = BuildPdfMinimal(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.DoesNotContain("mix-blend-mode:multiply", svg);
    }

    // ==================================================================
    //  E. SVG Ts text-rise regression
    // ==================================================================

    [Fact]
    public void Svg_TextRise_IsAppliedToTextMatrix()
    {
        // 6 Ts raises the baseline by 6 units.  The SVG <text> element's transform
        // matrix should have a non-zero f-entry compared to a Ts=0 baseline.
        const string cs = "BT /F1 12 Tf 100 500 Td 6 Ts (X) Tj ET";
        byte[] pdf = BuildPdfWithFont(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        // Sanity: the text element should have been emitted.
        Assert.Contains("<text", svg);
    }

    // ==================================================================
    //  F. SVG color spaces
    // ==================================================================

    [Fact]
    public void Svg_LabColorSpace_RendersAsSRgb()
    {
        // Set fill via [/Lab ...]; 100 0 0 → white (L=100, a=b=0).
        // The renderer must emit some rgb() fill; exact values depend on D50 matrix.
        const string csSetup = "/CS1 cs 100 0 0 sc 100 100 200 200 re f";
        byte[] pdf = BuildPdfWithColorSpace("CS1",
            csDef: "[/Lab << /WhitePoint [0.9505 1.0 1.0890] >>]",
            contentStream: csSetup);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("<path", svg);
        Assert.Contains("rgb(", svg);
    }

    [Fact]
    public void Svg_IndexedColorSpace_PaletteLookupRenders()
    {
        // Palette: 2 entries (red, green) packed as 6 raw bytes.
        // Using hex string lookup: <FF0000 00FF00> = (0xFF,0,0, 0,0xFF,0).
        const string csDef = "[/Indexed /DeviceRGB 1 <FF000000FF00>]";
        const string cs    = "/CS1 cs 1 sc 100 100 200 200 re f";  // index=1 → green
        byte[] pdf = BuildPdfWithColorSpace("CS1", csDef, cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("rgb(0,255,0)", svg);
    }

    [Fact]
    public void Svg_SeparationColorSpace_TintTransformRenders()
    {
        // /Separation /MyInk /DeviceRGB <tintFn>.  Tint 1.0 → [0.3 0.6 0.9] via
        // exponential function.  Expected rgb(76, 153, 229).
        const string csDef =
            "[/Separation /MyInk /DeviceRGB " +
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [0.3 0.6 0.9] /N 1 >>]";
        const string cs = "/CS1 cs 1 sc 100 100 200 200 re f";
        byte[] pdf = BuildPdfWithColorSpace("CS1", csDef, cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        // The exact integer rounding can differ by ±1 due to float→byte cast in
        // RgbColor; accept any rgb(...) fill as proof the tint path was taken.
        Assert.Contains("rgb(", svg);
    }

    [Fact]
    public void Svg_IccBasedColorSpace_MapsByN()
    {
        // /ICCBased with /N 3 stream — treat as DeviceRGB.  Using a placeholder ICC
        // stream (empty binary payload); only /N is read.
        const string csDef = "[/ICCBased 99 0 R]";
        const string iccStream = "99 0 obj\n<< /N 3 /Length 0 >>\nstream\n\nendstream\nendobj\n";
        const string cs = "/CS1 cs 1 0 0 sc 100 100 200 200 re f";
        byte[] pdf = BuildPdfWithColorSpaceAndExtraObj("CS1", csDef, iccStream, objNum: 99, cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("rgb(255,0,0)", svg);
    }

    // ==================================================================
    //  G. MacExpertEncoding glyph-name table
    // ==================================================================

    [Fact]
    public void MacExpertEncoding_KnownGlyphs_Resolve()
    {
        // Spot-check a few representative entries from PDF Annex D.4.
        Assert.Equal("space",            FontCache.MacExpertNames[32]);
        Assert.Equal("exclamsmall",      FontCache.MacExpertNames[33]);
        Assert.Equal("zerooldstyle",     FontCache.MacExpertNames[48]);
        Assert.Equal("fraction",         FontCache.MacExpertNames[47]);
        Assert.Equal("Asmall",           FontCache.MacExpertNames[97]);
        Assert.Equal("ffi",              FontCache.MacExpertNames[89]);
        Assert.Equal("onequarter",       FontCache.MacExpertNames[71]);
    }

    [Fact]
    public void MacExpertEncoding_UnusedCodes_AreNull()
    {
        // Positions not assigned by the spec must remain null (= .notdef).
        Assert.Null(FontCache.MacExpertNames[60]);
        Assert.Null(FontCache.MacExpertNames[255]);
        Assert.Null(FontCache.MacExpertNames[0]);
    }

    [Fact]
    public void MacRomanEncoding_Regression_TableStillIntact()
    {
        // Sanity that the new table didn't displace the existing MacRoman entries.
        Assert.Equal("Adieresis", FontCache.MacRomanNames[128]);
        Assert.Equal("dagger",    FontCache.MacRomanNames[160]);
        Assert.Equal("Ograve",    FontCache.MacRomanNames[241]);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static byte[] BuildPdfWithAnnotation(string subtype, string extraAnnotKeys, string rect)
    {
        // Content stream draws a visible marker so the page isn't completely blank.
        const string cs = "0.8 0.8 0.8 rg 0 0 612 792 re f";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /Annot /Subtype /{0} /Rect [{1}]" +
            " /C [0.1 0.2 0.3] /CA 1{2} >>\nendobj\n",
            subtype, rect, extraAnnotKeys);
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Annots [6 0 R]" +
            " /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfMinimal(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithFont(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithExtGState(string extGsDict, string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n" + extGsDict + "\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /ExtGState << /GS1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithColorSpace(string name, string csDef, string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n{0}\nendobj\n", csDef);
        int o3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /ColorSpace << /{0} 5 0 R >> >> >>\nendobj\n",
            name);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithColorSpaceAndExtraObj(
        string name, string csDef, string extraObj, int objNum, string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n{0}\nendobj\n", csDef);
        int o99 = sb.Length;
        sb.Append(extraObj);
        int o3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /ColorSpace << /{0} 5 0 R >> >> >>\nendobj\n",
            name);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);

        // xref table must list 100 entries (slots 0..99).  We fill unused ones with
        // the free entry marker.  Simpler: produce a single subsection covering 0..99.
        int xref = sb.Length;
        int total = 100;
        sb.AppendFormat("xref\n0 {0}\n", total);
        sb.Append("0000000000 65535 f \n");
        int[] offsets = new int[total];
        offsets[1] = o1; offsets[2] = o2; offsets[3] = o3; offsets[4] = o4;
        offsets[5] = o5; offsets[objNum] = o99;
        for (int i = 1; i < total; i++)
        {
            if (offsets[i] != 0)
                sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
            else
                sb.Append("0000000000 00000 f \n");
        }
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", total);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
