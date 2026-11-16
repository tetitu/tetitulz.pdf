// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Overprint and <b>images</b>: ISO 32000-2 §8.6.7 — non-zero overprint mode "shall not, however,
/// apply to the painting of images or shadings".
/// </summary>
/// <remarks>
/// <para><b>This suite used to assert the opposite.</b> From 2026-08-18 a DeviceCMYK image under
/// <c>/OPM 1</c> was composited per sample, a zero component leaving the backdrop's colorant alone —
/// magenta over cyan made blue. The clause above says it shall not, and the reference renderer's
/// separation output agrees: an overprinting DeviceCMYK image knocks black out under a zero K sample
/// (found while building separations). An overprinting image writes every
/// colorant its colour space names, which for DeviceCMYK, grey and RGB is all four process
/// colorants — exactly the ordinary draw on a composite. The composite is gone.</para>
///
/// <para>What overprint still changes for an image is a colorant set naming some colorants and not
/// others (§11.7.4.5): a Separation-Cyan image keeps the magenta beneath it (separation output again).
/// The composite does not simulate that and both renderers report it, through one rule
/// (<c>OverprintCompositor.ImageLeavesColorantsAlone</c>); a separation render
/// (<see cref="PdfSeparationRenderer"/>) gets it right per plate.</para>
/// </remarks>
public class ImageOverprintTests
{
    /// <summary>
    /// A magenta DeviceCMYK image over cyan, overprinting under <c>/OPM 1</c>: it replaces the cyan
    /// (magenta, not blue) whether or not simulation is on — the two renders are the same pixels.
    /// </summary>
    [Fact]
    public void AMagentaCmykImageOverCyan_ReplacesIt_BecauseOverprintModeDoesNotApplyToImages()
    {
        byte[] pdf = OverprintImagePdf("1 0 0 0", new byte[] { 0, 255, 0, 0 });

        using var simulated = Render(pdf, simulate: true);
        using var plain = Render(pdf, simulate: false);

        SKColor over = PixelAssert.At(simulated, 50, 50);
        Assert.True(PixelAssert.Near(over, PdfColor.CmykToRgb(0, 1, 0, 0), 12),
            $"the image's magenta must replace the cyan; got {over}");
        Assert.Equal(PixelAssert.At(plain, 50, 50), over);
    }

    public static IEnumerable<object[]> Spaces()
    {
        yield return new object[] { "/DeviceCMYK", new byte[] { 0, 255, 0, 0 }, false };
        yield return new object[] { "[/Separation /Cyan /DeviceCMYK 6 0 R]", new byte[] { 255 }, true };
        yield return new object[] { "[/Separation /All /DeviceCMYK 6 0 R]", new byte[] { 255 }, false };
        yield return new object[] { "[/DeviceN [/Cyan /Magenta /Yellow /Black] /DeviceCMYK 7 0 R]", new byte[] { 0, 255, 0, 0 }, false };
        yield return new object[] { "[/Indexed [/Separation /Cyan /DeviceCMYK 6 0 R] 0 <FF>]", new byte[] { 0 }, true };
        yield return new object[] { "[/DeviceN [/Cyan /Magenta] /DeviceCMYK 8 0 R]", new byte[] { 255, 0 }, true };
    }

    /// <summary>
    /// What the SVG writer reports: an overprinting image whose colour space names some colorants
    /// and not others (a Separation of one process ink, a DeviceN of two, an Indexed space over one)
    /// leaves the rest alone, which its composite does not show; DeviceCMYK, <c>/All</c> and a
    /// DeviceN of all four process inks write every colorant, so there is nothing to report. The
    /// raster renderer simulates every one of them on the page's separation plates, so it reports
    /// none (since 2026-10-04; the pixels are <c>ASeparationCyanImage_KeepsTheMagentaBeneathIt</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(Spaces))]
    public void AnOverprintingImage_IsSimulatedInRaster_AndReportedInSvgWhenItsSpaceLeavesAColorantAlone(string space, byte[] samples, bool reported)
    {
        byte[] pdf = OverprintImagePdf("0 1 0 0", samples, colorSpace: space);

        var raster = new List<string>();
        using (Render(pdf, simulate: true, raster)) { }
        var svg = new List<string>();
        Listen(svg, () => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, new PdfRenderOptions { SimulateOverprint = true }));

        Assert.DoesNotContain(raster, r => r.Contains(ImageReason, StringComparison.Ordinal));
        Assert.Equal(reported, svg.Exists(r => r.Contains(ImageReason, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A Separation-Cyan image overprinting a magenta page, simulated: §11.7.4.5 marks only the cyan
    /// plate, so the magenta stays and the two make blue. Unsimulated, the image's cyan covers it.
    /// </summary>
    [Fact]
    public void ASeparationCyanImage_KeepsTheMagentaBeneathIt()
    {
        byte[] pdf = OverprintImagePdf("0 1 0 0", new byte[] { 255 }, colorSpace: "[/Separation /Cyan /DeviceCMYK 6 0 R]");
        using var simulated = Render(pdf, simulate: true);
        using var plain = Render(pdf, simulate: false);
        Assert.True(PixelAssert.Near(PixelAssert.At(simulated, 50, 50), PdfColor.CmykToRgb(1, 1, 0, 0)),
            $"cyan over magenta should be blue, got {PixelAssert.At(simulated, 50, 50)}");
        Assert.True(PixelAssert.Near(PixelAssert.At(plain, 50, 50), PdfColor.CmykToRgb(1, 0, 0, 0)),
            $"unsimulated the cyan covers, got {PixelAssert.At(plain, 50, 50)}");
    }

    /// <summary>
    /// An image that does not ask to overprint is unaffected by simulation and reports nothing —
    /// the control for the reported case above, on the same Separation-Cyan image.
    /// </summary>
    [Fact]
    public void AnImageWithoutOp_IsUnchangedBySimulation_AndReportsNothing()
    {
        byte[] pdf = OverprintImagePdf("0 1 0 0", new byte[] { 255 }, op: false, colorSpace: "[/Separation /Cyan /DeviceCMYK 6 0 R]");

        var reasons = new List<string>();
        using var simulated = Render(pdf, simulate: true, reasons);
        using var plain = Render(pdf, simulate: false);

        Assert.Equal(PixelAssert.At(plain, 50, 50), PixelAssert.At(simulated, 50, 50));
        Assert.DoesNotContain(reasons, r => r.Contains(ImageReason, StringComparison.Ordinal));
    }

    // ================================================================

    private const string ImageReason = "an image in a /Separation or /DeviceN space";

    private static SKBitmap Render(byte[] pdf, bool simulate, List<string> reasons = null)
    {
        byte[] png = null;
        void Draw()
        {
            using var ms = new MemoryStream(pdf);
            png = PdfPageRenderer.RenderPage(ms, 0, PixelAssert.Dpi, new PdfRenderOptions { SimulateOverprint = simulate });
        }

        if (reasons != null) Listen(reasons, Draw);
        else Draw();
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>Collects every <c>OverprintNotSimulated</c> detail <paramref name="act"/> reports on this thread.</summary>
    private static void Listen(List<string> reasons, Action act)
    {
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.OverprintNotSimulated && Environment.CurrentManagedThreadId == own)
                lock (reasons) reasons.Add(detail ?? "");
        }

        RenderDiagnostics.OnEvent += Sink;
        try { act(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
    }

    /// <summary>
    /// A page that fills itself with <paramref name="backdrop"/> in DeviceCMYK and then draws a 1×1
    /// image over the whole page under <c>/op true /OPM 1</c> (or not, <paramref name="op"/>).
    /// Object 6 is a Separation tint transform to DeviceCMYK cyan, object 7 an identity DeviceN one,
    /// object 8 a [Cyan Magenta] one.
    /// </summary>
    private static byte[] OverprintImagePdf(string backdrop, byte[] image, bool op = true, string colorSpace = "/DeviceCMYK")
    {
        string content = $"q {backdrop} k 0 0 100 100 re f Q q /GS1 gs 100 0 0 100 0 0 cm /Im0 Do Q";
        string gs = $"<< /Type /ExtGState /op {(op ? "true" : "false")} /OP {(op ? "true" : "false")} /OPM 1 >>";
        const string identity = "{ }";
        const string twoToFour = "{ 0 0 }";

        var objects = new List<(string dict, byte[] raw)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources <<" +
             " /XObject << /Im0 5 0 R >> /ExtGState << /GS1 " + gs + " >> >> >>", null),
            ($"<< /Length {content.Length} >>\nstream\n{content}\nendstream", null),
            ($"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace {colorSpace} /Length {image.Length} >>", image),
            ("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>", null),
            ($"<< /FunctionType 4 /Domain [0 1 0 1 0 1 0 1] /Range [0 1 0 1 0 1 0 1] /Length {identity.Length} >>", Encoding.ASCII.GetBytes(identity)),
            ($"<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1 0 1] /Length {twoToFour.Length} >>", Encoding.ASCII.GetBytes(twoToFour)),
        };

        var bytes = new MemoryStream();
        var offsets = new List<int>();
        void Append(string s) => bytes.Write(Encoding.ASCII.GetBytes(s), 0, Encoding.ASCII.GetByteCount(s));

        Append("%PDF-1.7\n");
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add((int)bytes.Length);
            Append($"{i + 1} 0 obj\n");
            Append(objects[i].dict);
            if (objects[i].raw != null)
            {
                Append("\nstream\n");
                bytes.Write(objects[i].raw, 0, objects[i].raw.Length);
                Append("\nendstream");
            }
            Append("\nendobj\n");
        }

        int xref = (int)bytes.Length;
        Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) Append(off.ToString("D10") + " 00000 n \n");
        Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        Append($"startxref\n{xref}\n%%EOF\n");
        return bytes.ToArray();
    }
}
