// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §11.6.5.2 luminosity and <c>/TR</c> soft masks, asserted on <b>decoded pixels</b> — the
/// behaviour that had to survive `ApplySoftMask` moving off <c>SKBitmap.Pixels</c> and onto the
/// bitmap's own buffer.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists rather than a hash.</b> The change replaced a getter that
/// allocates a managed <c>SKColor[]</c> over the whole bitmap (33 MB for one page-sized mask at
/// 300 dpi, 65 MB when a <c>/TR</c> adds a second pass) with direct span access, and it had to be
/// output-preserving. A hash of the render proves that for one fixture on one Skia build and says
/// nothing about <i>what</i> the mask does; these assert the mask's actual contract, so they still
/// mean something after the next SkiaSharp bump.</para>
///
/// <para><b>The subtlety the change turned on.</b> The buffer is premultiplied
/// (<c>RenderToBitmap</c> makes <c>SKAlphaType.Premul</c>) where <c>SKColor</c> is straight, so
/// the old getter un-premultiplied every pixel and the loop's <c>(c.Red / 255) * a</c> then
/// re-applied the alpha that had just been divided out. Reading the premultiplied byte directly
/// is that term exactly — one lossy round-trip removed, not approximated. Measured over these
/// fixtures the rendered bytes are identical either way.</para>
///
/// <para><b>A ramp, not a flat fill, on purpose.</b> A mask that was dropped entirely leaves the
/// page-covering fill beneath it painting flat — the same ink a correct render produces, so ink
/// alone cannot separate them. A 32-band ramp can: a correct render is monotonic top to bottom
/// and a dropped mask is one colour.</para>
/// </remarks>
public class SoftMaskBufferAccessTests
{
    /// <summary>The mask ramps black (fully masked) to white (fully visible) up the page.</summary>
    [Fact]
    public void LuminosityMaskProducesAMonotonicRampNotAFlatFill()
    {
        using var bmp = Render(RampMaskedPage(transferFunction: false));
        int x = bmp.Width / 2;

        // Sample well inside the top and bottom bands, away from band boundaries.
        var top = bmp.GetPixel(x, bmp.Height / 20);
        var bottom = bmp.GetPixel(x, bmp.Height - bmp.Height / 20);

        // PDF y grows upward, so the LAST band (g = 1, unmasked) is at the TOP of the image.
        // Unmasked shows the 0.2/0.4/0.8 fill; fully masked shows the white page.
        Assert.True(top.Blue > top.Red, $"top should show the blue fill, got {top}");
        Assert.True(bottom.Red > 240 && bottom.Green > 240 && bottom.Blue > 240,
            $"bottom band is fully masked and should be page white, got {bottom}");

        // Monotonic in between: a coarse walk down the page must never get *more* inked.
        int previousInk = -1, increases = 0;
        for (int i = 1; i < 20; i++)
        {
            var c = bmp.GetPixel(x, bmp.Height * i / 20);
            int ink = 255 - c.Red;              // red falls furthest under the 0.2 R fill
            if (previousInk >= 0 && ink > previousInk + 2) increases++;
            previousInk = ink;
        }
        Assert.True(increases == 0, $"the ramp reversed {increases} times — the mask is not monotonic");
    }

    /// <summary>
    /// <c>{ 1 exch sub }</c> inverts the mask, so the ramp must run the other way. A dropped
    /// <c>/TR</c> renders exactly the plain fixture, which is why this compares the two rather
    /// than asserting a value.
    /// </summary>
    [Fact]
    public void TransferFunctionInvertsTheMask()
    {
        using var plain = Render(RampMaskedPage(transferFunction: false));
        using var inverted = Render(RampMaskedPage(transferFunction: true));

        int x = plain.Width / 2, y = plain.Height / 20;
        var plainTop = plain.GetPixel(x, y);
        var invertedTop = inverted.GetPixel(x, y);

        // Top is unmasked without /TR and fully masked with it.
        Assert.True(plainTop.Blue > plainTop.Red, $"expected the fill at the top, got {plainTop}");
        Assert.True(invertedTop.Red > 240 && invertedTop.Blue > 240,
            $"/TR should have masked the top out, got {invertedTop}");
    }

    /// <summary>
    /// <b>The test the premultiplied/straight distinction actually turns on.</b> A mask band
    /// painted at <c>/ca 0.5</c> must composite at ~50% coverage, not ~25%.
    /// </summary>
    /// <remarks>
    /// <para><b>A fully opaque mask cannot detect this and the first version of this test was
    /// exactly that.</b> Where the mask group's alpha is 255, premultiplied and straight are the
    /// same number, so re-applying alpha to an already-premultiplied byte — the one mistake this
    /// whole change could make — is the identity. The mutation survived three passing tests.
    /// A band at alpha 0 is no better: there the colour term is multiplied out either way and only
    /// the backdrop survives. <b>Only a partially transparent mask discriminates</b>, which is why
    /// this fixture puts an explicit <c>/ca 0.5</c> inside the mask group.</para>
    ///
    /// <para>White at half alpha gives a premultiplied byte of 127. Read correctly that is a mask
    /// value of 127/255 ~ 0.5; multiplied by alpha a second time it is ~0.25. Over the
    /// <c>0.2 0.4 0.8</c> fill on a white page the red channel lands at ~153 for the first and
    /// ~204 for the second, so the assertion below separates them by five standard deviations of
    /// anything rounding could contribute.</para>
    /// </remarks>
    [Fact]
    public void AHalfAlphaMaskBandCompositesAtHalfCoverageNotAQuarter()
    {
        using var bmp = Render(HalfAlphaMaskedPage());
        int x = bmp.Width / 2;

        // The masked band is the bottom half in PDF space, which is the bottom half of the image.
        var band = bmp.GetPixel(x, bmp.Height * 3 / 4);

        // red = 1 - 0.8 * coverage. coverage 0.5 -> ~153; coverage 0.25 -> ~204.
        Assert.InRange(band.Red, 135, 175);
    }

    /// <summary>
    /// A mask group painting white at <c>/ca 0.5</c> over the bottom half of the page, and nothing
    /// over the top half.
    /// </summary>
    private static byte[] HalfAlphaMaskedPage()
    {
        const string maskContent = "q /A5 gs 1 1 1 rg 0 0 612 396 re f Q\n";

        var b = new StringBuilder();
        b.Append("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /ExtGState << /GM0 6 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        const string content = "q /GM0 gs 0.2 0.4 0.8 rg 0 0 612 792 re f Q\n";
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj($"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] "
            + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >> "
            + "/Resources << /ExtGState << /A5 << /Type /ExtGState /ca 0.5 >> >> >> "
            + $"/Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream\nendobj\n");
        Obj("6 0 obj\n<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >>\nendobj\n");

        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }

    private static SKBitmap Render(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf, false), 0, 96);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>A page-covering fill under a page-covering 32-band luminosity ramp.</summary>
    private static byte[] RampMaskedPage(bool transferFunction)
    {
        string trRef = transferFunction ? " /TR 7 0 R" : "";
        var mask = new StringBuilder();
        for (int i = 0; i < 32; i++)
            mask.Append((i / 31f).ToString("0.###")).Append(" g 0 ")
                .Append((i * 24.75f).ToString("0.##")).Append(" 612 24.75 re f\n");
        string m = mask.ToString();

        var b = new StringBuilder();
        b.Append("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /ExtGState << /GM0 6 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        const string content = "q /GM0 gs 0.2 0.4 0.8 rg 0 0 612 792 re f Q\n";
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj($"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] "
            + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >> "
            + $"/Resources << >> /Length {m.Length} >>\nstream\n{m}\nendstream\nendobj\n");
        Obj($"6 0 obj\n<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R{trRef} >> >>\nendobj\n");
        const string ps = "{ 1 exch sub }";
        if (transferFunction)
            Obj($"7 0 obj\n<< /FunctionType 4 /Domain [0 1] /Range [0 1] /Length {ps.Length} >>\n"
                + $"stream\n{ps}\nendstream\nendobj\n");

        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }
}
