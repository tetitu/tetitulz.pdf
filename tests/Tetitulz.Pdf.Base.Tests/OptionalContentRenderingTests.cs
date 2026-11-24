// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using SkiaSharp;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for Optional Content Group (OCG / layer) visibility enforcement during rendering.
/// Gap 31.1 — hidden layers must not appear in the rendered output.
/// </summary>
/// <remarks>
/// <b>Every visibility case here asserts on decoded pixels, and every one is paired with its
/// opposite.</b> "Rendering did not throw" passes with <c>BDC /OC</c> suppression and
/// indirect-<c>/D</c> resolution both completely broken — a blank page encodes to a perfectly
/// valid non-empty PNG. Pairing matters as much as the pixel
/// assertion: "hidden content is absent" is satisfied by a renderer that draws nothing at all, and
/// "visible content is present" by one that ignores <c>/OC</c> entirely, so each test renders both
/// fixtures and asserts they differ in the way the spec requires.
/// Further visibility coverage lives in <see cref="Rendering.RenderingSpecFixTests"/>.
/// </remarks>
public sealed class OptionalContentRenderingTests
{
    // ------------------------------------------------------------------
    //  Basic OCG visibility: no /OCProperties → all content visible
    // ------------------------------------------------------------------

    /// <summary>
    /// With no <c>/OCProperties</c> there is nothing to hide, so every operator draws. This is the
    /// control the rest of the file is measured against: if suppression were applied unconditionally
    /// — the natural way to get the "hidden" tests passing — this one would come out blank.
    ///
    /// <para>The predecessor's content stream was <c>q Q</c>: save state, restore state, draw
    /// nothing. It then asserted a non-empty PNG, which a blank page satisfies. "All content
    /// visible" was never checked, because there was no content.</para>
    /// </summary>
    [Fact]
    public void NoOCProperties_AllContentIsVisible()
    {
        byte[] pdf = BuildMinimalPdf("0 0 1 rg 100 600 100 100 re f");

        using var bmp = Rendering.PixelAssert.Render(pdf);

        Assert.True(Rendering.PixelAssert.Near(
                Rendering.PixelAssert.At(bmp, 150, 650), new SKColor(0, 0, 255)),
            "content drew nothing on a page with no /OCProperties");
        Assert.True(Rendering.PixelAssert.IsWhite(Rendering.PixelAssert.At(bmp, 400, 300)),
            "ink appeared outside the filled rectangle");
    }

    // ------------------------------------------------------------------
    //  XObject with /OC pointing to a visible OCG — must render
    // ------------------------------------------------------------------

    /// <summary>
    /// The XObject is a 1×1 red image drawn over user-space (100,600)-(200,700) and its <c>/OC</c>
    /// names an OCG that is <b>not</b> in <c>/OFF</c>, so red must reach that point.
    ///
    /// <para>Rendered together with the <c>/OFF</c> fixture, which must leave the same point white.
    /// Without that half a renderer that ignores <c>/OC</c> altogether passes this test.</para>
    /// </summary>
    [Fact]
    public void XObjectWithVisibleOCG_IsRendered()
    {
        using var visible = Rendering.PixelAssert.Render(BuildPdfWithOcImage(ocgVisible: true));
        using var hidden  = Rendering.PixelAssert.Render(BuildPdfWithOcImage(ocgVisible: false));

        var shown = Rendering.PixelAssert.At(visible, 150, 650);
        Assert.True(Rendering.PixelAssert.Near(shown, new SKColor(255, 0, 0)),
            $"the /OC image on a visible layer did not reach the page; got {shown}");
        Assert.True(Rendering.PixelAssert.IsWhite(Rendering.PixelAssert.At(hidden, 150, 650)),
            "the identical image on an /OFF layer drew too, so /OC was never consulted");
    }

    // ------------------------------------------------------------------
    //  XObject with /OC pointing to a hidden OCG — must not be painted
    // ------------------------------------------------------------------

    /// <summary>
    /// The same fixture with the OCG listed in <c>/OFF</c>: the image must be absent from the
    /// pixels, not merely skipped without an exception. The visible control renders alongside it so
    /// "absent" cannot be satisfied by a renderer that draws nothing at all.
    /// </summary>
    [Fact]
    public void XObjectWithHiddenOCG_IsNotRendered()
    {
        using var hidden  = Rendering.PixelAssert.Render(BuildPdfWithOcImage(ocgVisible: false));
        using var visible = Rendering.PixelAssert.Render(BuildPdfWithOcImage(ocgVisible: true));

        var suppressed = Rendering.PixelAssert.At(hidden, 150, 650);
        Assert.True(Rendering.PixelAssert.IsWhite(suppressed),
            $"the /OC image on an /OFF layer was painted anyway; got {suppressed}");
        Assert.Equal(0, Rendering.PixelAssert.CountInk(hidden));

        // The control proves the fixture really draws something when the layer is on.
        Assert.True(Rendering.PixelAssert.Near(
                Rendering.PixelAssert.At(visible, 150, 650), new SKColor(255, 0, 0)),
            "the fixture draws nothing even with the layer visible, so hiding proves nothing");
    }

    // ------------------------------------------------------------------
    //  BDC /OC block with hidden OCG — content inside must be suppressed
    // ------------------------------------------------------------------

    /// <summary>
    /// The fixture draws a grey square at (100,100)-(200,200) <b>outside</b> any marked-content
    /// block and a red square at (200,200)-(300,300) <b>inside</b> <c>/OC /OC1 BDC … EMC</c>. With
    /// the OCG in <c>/OFF</c> the red square must be gone and the grey one must survive — the second
    /// half is what stops a renderer that aborts the whole stream at <c>BDC</c> from passing.
    /// </summary>
    [Fact]
    public void BdcOcBlock_HiddenOCG_ContentIsSuppressed()
    {
        using var hidden  = Rendering.PixelAssert.Render(BuildPdfWithBdcOcBlock(ocgVisible: false));
        using var visible = Rendering.PixelAssert.Render(BuildPdfWithBdcOcBlock(ocgVisible: true));

        var inBlock = Rendering.PixelAssert.At(hidden, 250, 250);
        Assert.True(Rendering.PixelAssert.IsWhite(inBlock),
            $"content inside BDC /OC on an /OFF layer was painted; got {inBlock}");

        // Unmarked content on the same page is untouched by the suppression.
        Assert.True(Rendering.PixelAssert.Near(
                Rendering.PixelAssert.At(hidden, 150, 150), new SKColor(128, 128, 128), 12),
            "suppressing the /OC block also swallowed the unmarked content before it");

        // ...and the control shows the block really does paint when the layer is on.
        Assert.True(Rendering.PixelAssert.Near(
                Rendering.PixelAssert.At(visible, 250, 250), new SKColor(255, 0, 0)),
            "the fixture's BDC block draws nothing even when visible");
    }

    /// <summary>
    /// The other half: with the OCG absent from <c>/OFF</c>, everything between <c>BDC</c> and
    /// <c>EMC</c> executes, so both squares are on the page.
    /// </summary>
    [Fact]
    public void BdcOcBlock_VisibleOCG_ContentIsRendered()
    {
        using var visible = Rendering.PixelAssert.Render(BuildPdfWithBdcOcBlock(ocgVisible: true));
        using var hidden  = Rendering.PixelAssert.Render(BuildPdfWithBdcOcBlock(ocgVisible: false));

        var inBlock = Rendering.PixelAssert.At(visible, 250, 250);
        Assert.True(Rendering.PixelAssert.Near(inBlock, new SKColor(255, 0, 0)),
            $"content inside BDC /OC on a visible layer never reached the page; got {inBlock}");
        Assert.True(Rendering.PixelAssert.Near(
                Rendering.PixelAssert.At(visible, 150, 150), new SKColor(128, 128, 128), 12),
            "the unmarked content before the block is missing");
        Assert.True(Rendering.PixelAssert.IsWhite(Rendering.PixelAssert.At(visible, 400, 500)),
            "ink appeared where neither square was drawn");

        // Paired with /OFF so "rendered" cannot pass by ignoring /OC entirely.
        Assert.True(Rendering.PixelAssert.IsWhite(Rendering.PixelAssert.At(hidden, 250, 250)),
            "the block painted on an /OFF layer too, so visibility was never consulted");
    }

    // ------------------------------------------------------------------
    //  Nested BDC blocks — outer hidden hides inner too
    // ------------------------------------------------------------------

    /// <summary>
    /// A hidden outer <c>BDC /OC</c> suppresses the inner block too, even though the inner block's
    /// own OCG is visible — the skip counts nesting depth rather than re-evaluating visibility.
    /// </summary>
    /// <remarks>
    /// <b>This test was exempt from the pattern-3 guard for the wrong reason.</b> Its body was the
    /// laundered <c>Record.Exception</c> shape, and it escaped only because "Nested" is in
    /// <c>HostileInputNameTokens</c> — a token that describes the content stream's <i>structure</i>,
    /// not a hostile input. Nesting two BDC blocks is exactly what this feature is for. That is the
    /// abuse the guard's own documentation warns about ("a behaviour claim wearing a guard's
    /// clothes"), reached by accident rather than on purpose.
    /// </remarks>
    [Fact]
    public void NestedBdcBlocks_OuterHidden_BothSuppressed()
    {
        using var hidden  = Rendering.PixelAssert.Render(BuildPdfWithNestedBdcBlocks(outerHidden: true));
        using var visible = Rendering.PixelAssert.Render(BuildPdfWithNestedBdcBlocks(outerHidden: false));

        // Outer block paints red at (110,110); inner paints green at (200,200).
        Assert.True(Rendering.PixelAssert.IsWhite(Rendering.PixelAssert.At(hidden, 110, 110)),
            "the outer block painted despite its OCG being /OFF");
        Assert.True(Rendering.PixelAssert.IsWhite(Rendering.PixelAssert.At(hidden, 200, 200)),
            "the INNER block painted while the outer block was hidden — the skip did not nest");

        // Paired with the visible fixture, so "suppressed" cannot pass by drawing nothing at all.
        Assert.True(Rendering.PixelAssert.IsInk(Rendering.PixelAssert.At(visible, 110, 110)),
            "the outer block did not paint even with its OCG visible");
        Assert.True(Rendering.PixelAssert.IsInk(Rendering.PixelAssert.At(visible, 200, 200)),
            "the inner block did not paint even with both OCGs visible");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] BuildMinimalPdf(string cs)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
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

    /// <summary>
    /// Builds a PDF with one image XObject whose /OC entry points to an OCG
    /// that is either in /OFF (hidden) or not (visible).
    /// </summary>
    private static byte[] BuildPdfWithOcImage(bool ocgVisible)
    {
        // 1×1 red pixel as raw RGB
        byte[] imgData = { 255, 0, 0 };
        var hexSb = new StringBuilder();
        foreach (byte b in imgData) hexSb.AppendFormat("{0:X2}", b);
        hexSb.Append(">");
        string hexData = hexSb.ToString();

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OCProperties 8 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // OCG dict (obj 7)
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /OCG /Name (Layer1) >>\nendobj\n");

        // OCProperties (obj 8) — put OCG in /OFF when hidden
        int o8 = sb.Length;
        string offEntry = ocgVisible ? "" : "/OFF [7 0 R]";
        sb.AppendFormat(
            "8 0 obj\n<< /OCGs [7 0 R] /D << /Order [7 0 R] {0} >> >>\nendobj\n",
            offEntry);

        // Image XObject (obj 5) with /OC → OCG obj 7
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1" +
            " /ColorSpace /DeviceRGB /BitsPerComponent 8 /OC 7 0 R" +
            " /Filter /ASCIIHexDecode /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            hexData.Length, hexData);

        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 6 0 R >>" +
            " /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        const string cs = "q 100 0 0 100 100 600 cm /Im1 Do Q";
        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.AppendFormat("{0:D10} 00000 n \n", o8);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a PDF where a content block is surrounded by  /Oc BDC ... EMC,
    /// with the OCG either visible or hidden.
    /// </summary>
    private static byte[] BuildPdfWithBdcOcBlock(bool ocgVisible)
    {
        // Content stream: outside BDC block (always visible) + inside BDC block (conditional).
        // The Properties resource /OC1 points to the OCG.
        const string cs =
            "0.5 g 100 100 100 100 re f\n" +          // always visible: grey square
            "/OC /OC1 BDC\n" +
            "1 0 0 rg 200 200 100 100 re f\n" +       // conditional: red square
            "EMC";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OCProperties 7 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // OCG (obj 6)
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /OCG /Name (Layer1) >>\nendobj\n");

        // OCProperties (obj 7)
        int o7 = sb.Length;
        string offEntry = ocgVisible ? "" : "/OFF [6 0 R]";
        sb.AppendFormat(
            "7 0 obj\n<< /OCGs [6 0 R] /D << /Order [6 0 R] {0} >> >>\nendobj\n",
            offEntry);

        // Page (obj 3): Properties resource /OC1 → OCG obj 6
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>" +
            " /Properties << /OC1 6 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithNestedBdcBlocks(bool outerHidden)
    {
        // Outer BDC /OC with one OCG; inner BDC /OC with another.
        // If the outer OCG is hidden, neither block should render.
        const string cs =
            "/OC /OC1 BDC\n" +
            "  1 0 0 rg 100 100 200 200 re f\n" +
            "  /OC /OC2 BDC\n" +
            "    0 1 0 rg 150 150 100 100 re f\n" +
            "  EMC\n" +
            "EMC";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OCProperties 8 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /OCG /Name (Outer) >>\nendobj\n");

        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /OCG /Name (Inner) >>\nendobj\n");

        int o8 = sb.Length;
        string offEntry = outerHidden ? "/OFF [6 0 R]" : "";
        sb.AppendFormat(
            "8 0 obj\n<< /OCGs [6 0 R 7 0 R] /D << /Order [6 0 R [7 0 R]] {0} >> >>\nendobj\n",
            offEntry);

        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>" +
            " /Properties << /OC1 6 0 R /OC2 7 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.AppendFormat("{0:D10} 00000 n \n", o8);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
