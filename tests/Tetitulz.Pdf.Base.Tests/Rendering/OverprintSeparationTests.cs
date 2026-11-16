// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Overprint with a <c>/Separation</c> or <c>/DeviceN</c> source, §11.7.4.5: the paint marks
/// exactly the colorants the colour space <b>names</b> and leaves every other one unchanged.
/// </summary>
/// <remarks>
/// <para><b>This is a different rule from DeviceCMYK's, and the fixtures are chosen to tell them
/// apart.</b> Under §11.7.4.2 a DeviceCMYK component of 0 preserves its colorant, and only under
/// nonzero overprint mode. Under §11.7.4.5 a named colorant is written <i>even at zero</i> and the
/// mode is irrelevant. A magenta Separation over a cyan page composites to blue under both
/// readings; a <b>cyan Separation at tint 0</b> over a cyan page knocks the cyan out under the
/// colorant-set rule and preserves it under the DeviceCMYK rule — so that is the pixel that says
/// which rule ran.</para>
///
/// <para><b>Spot inks are declined by name.</b> A colorant that is not Cyan, Magenta, Yellow or
/// Black is a fifth plate, and laying it down over four unchanged process plates is something a
/// four-channel compositing model cannot express. Both renderers report that reason, which is the
/// parity claim at the end.</para>
/// </remarks>
public class OverprintSeparationTests
{
    private static readonly SKColor Cyan    = new SKColor(0, 255, 255);
    private static readonly SKColor Magenta = new SKColor(255, 0, 255);
    private static readonly SKColor Blue    = new SKColor(0, 0, 255);
    private static readonly SKColor White   = new SKColor(255, 255, 255);

    // ====================================================================
    //  The colorant-set rule, on the raster side
    // ====================================================================

    /// <summary>A magenta Separation over cyan preserves the cyan: blue.</summary>
    [Fact]
    public void AProcessSeparation_OverprintsLikeTheProcessInkItNames()
    {
        SKColor c = Centre("q /GS0 gs /SepM cs 1 scn 20 20 60 60 re f Q", simulate: true);

        Assert.True(PixelAssert.Near(c, Blue), $"magenta Separation over cyan should be blue, got {c}");
    }

    /// <summary>The control: with simulation off the same fill paints plain magenta.</summary>
    [Fact]
    public void AProcessSeparation_NotSimulated_PaintsNormally()
    {
        SKColor c = Centre("q /GS0 gs /SepM cs 1 scn 20 20 60 60 re f Q", simulate: false);

        Assert.True(PixelAssert.Near(c, Magenta), $"with simulation off the fill should be magenta, got {c}");
    }

    /// <summary>
    /// A named colorant at tint 0 is still marked — with nothing — so a cyan Separation at 0 over a
    /// cyan page knocks the cyan out.
    /// </summary>
    /// <remarks>
    /// The discriminating pixel. Every other fixture here would pass under the DeviceCMYK rule
    /// applied to the Separation's components, because a non-zero named colorant and a zero unnamed
    /// one look the same under both. Only a <i>zero named</i> colorant separates them, and this is
    /// what §11.7.4.5's "shall be marked" means for it.
    /// </remarks>
    [Fact]
    public void ANamedColorantAtTintZero_IsWrittenRatherThanPreserved()
    {
        SKColor c = Centre("q /GS0 gs /SepC cs 0 scn 20 20 60 60 re f Q", simulate: true);

        Assert.True(PixelAssert.Near(c, White), $"cyan Separation at 0 should knock the cyan out, got {c}");
    }

    /// <summary>
    /// A DeviceN of two process inks writes both of the colorants it names — the zero one included
    /// — and preserves the two it does not.
    /// </summary>
    /// <remarks>
    /// Tints (0, 1) for [Cyan Magenta] over the cyan page: cyan is named and written as 0, magenta
    /// is written as 1, so the result is magenta. The DeviceCMYK rule would have preserved the
    /// page's cyan for the zero component and produced blue.
    /// </remarks>
    [Fact]
    public void ADeviceNOfProcessInks_WritesEveryColorantItNames()
    {
        SKColor c = Centre("q /GS0 gs /DnCM cs 0 1 scn 20 20 60 60 re f Q", simulate: true);

        Assert.True(PixelAssert.Near(c, Magenta), $"[Cyan Magenta] at (0,1) over cyan should be magenta, got {c}");
    }

    /// <summary>The colorant-set rule does not depend on <c>/OPM</c>.</summary>
    /// <remarks>
    /// Nonzero overprint mode is a DeviceCMYK concept (§11.7.4.2). The same magenta Separation
    /// under an ExtGState with <c>/OPM 0</c> still composites to blue, where a DeviceCMYK magenta
    /// under the same state paints plain magenta and is reported — the pair below.
    /// </remarks>
    [Fact]
    public void AProcessSeparation_OverprintsUnderOverprintModeZeroToo()
    {
        SKColor c = Centre("q /GS1 gs /SepM cs 1 scn 20 20 60 60 re f Q", simulate: true);

        Assert.True(PixelAssert.Near(c, Blue), $"a Separation ignores /OPM; expected blue, got {c}");
    }

    /// <summary>
    /// A DeviceCMYK source under <c>/OPM 0</c> writes every colorant, so it paints plain magenta —
    /// simulated on the plates, nothing reported. (It was reported as "/OPM is not 1" while the
    /// per-paint compositor declined it; renamed with the behaviour, 2026-10-04.)
    /// </summary>
    [Fact]
    public void ADeviceCmykSource_UnderOverprintModeZero_WritesEveryColorant_AndReportsNothing()
    {
        var reasons = new List<string>();
        SKColor c = Centre("q /GS1 gs 0 1 0 0 k 20 20 60 60 re f Q", simulate: true, reasons);

        Assert.True(PixelAssert.Near(c, Magenta), $"DeviceCMYK under /OPM 0 writes every colorant, got {c}");
        Assert.Empty(reasons);
    }

    /// <summary>
    /// A DeviceCMYK colour set <i>after</i> a Separation falls back to the DeviceCMYK rule: the
    /// channel mask travels with the colorant set and is cleared with it.
    /// </summary>
    /// <remarks>
    /// Under <c>/OPM 0</c> the two rules give opposite answers — a magenta colorant set composites
    /// to blue, a DeviceCMYK magenta paints plain magenta and is reported — so a mask that leaked
    /// from the <c>scn</c> into the following <c>k</c> would show as blue here.
    /// </remarks>
    [Fact]
    public void ADeviceCmykColourAfterASeparation_FallsBackToTheDeviceCmykRule()
    {
        var reasons = new List<string>();
        SKColor c = Centre("q /GS1 gs /SepM cs 1 scn 0 1 0 0 k 20 20 60 60 re f Q", simulate: true, reasons);

        Assert.True(PixelAssert.Near(c, Magenta), $"a `k` after a Separation is a DeviceCMYK source again, got {c}");
        Assert.Empty(reasons);   // simulated on the plates since 2026-10-04, so nothing is declined
    }

    /// <summary>
    /// The channel mask a Separation left behind must not shape the <c>k</c> that follows it.
    /// </summary>
    /// <remarks>
    /// <para><b>A mutation found the gap this closes.</b> Removing the mask reset survived the test
    /// above, because its <c>k</c> was <c>0 1 0 0 k</c> — the stale mask's own channel — so the
    /// stale mask and the DeviceCMYK rule wrote the same colorant. The mask is only consulted once
    /// the state ladder has passed, so the fixture has to reach the compositor with a mask that
    /// disagrees with the components: a <b>cyan</b> <c>k</c> after a <b>magenta</b> Separation,
    /// under OPM 1, over a magenta page.</para>
    /// <para>Correct, the DeviceCMYK rule writes the non-zero cyan and preserves the page's
    /// magenta: blue. With the stale mask, only the magenta channel is written — at the
    /// <c>k</c>'s zero — and the cyan is never written at all: white.</para>
    /// </remarks>
    [Fact]
    public void ADeviceCmykColourAfterASeparation_DoesNotInheritItsChannelMask()
    {
        SKColor c = Centre("q /GS0 gs /SepM cs 1 scn 1 0 0 0 k 20 20 60 60 re f Q", simulate: true,
                           pageFill: "0 1 0 0 k");

        Assert.True(PixelAssert.Near(c, Blue),
            $"a cyan `k` after a magenta Separation should preserve the page's magenta, got {c}");
    }

    /// <summary>
    /// <c>/All</c> names every colorant, so nothing is preserved: the paint is exact as a normal
    /// paint, and neither renderer says anything.
    /// </summary>
    [Fact]
    public void AnAllSeparation_WritesEveryColorant_AndReportsNothing()
    {
        var reasons = new List<string>();
        SKColor c = Centre("q /GS0 gs /SepAll cs 1 scn 20 20 60 60 re f Q", simulate: true, reasons);

        Assert.True(PixelAssert.Near(c, new SKColor(0, 0, 0)), $"/All at tint 1 is every colorant at 1, got {c}");
        Assert.Empty(reasons);
        Assert.Empty(SvgReasons("q /GS0 gs /SepAll cs 1 scn 20 20 60 60 re f Q"));
    }

    // ====================================================================
    //  Spot inks are declined by name, on both sides
    // ====================================================================

    /// <summary>
    /// An overprinting spot Separation is simulated on the plates: the spot ink lands on its own
    /// plate and leaves the cyan page alone, so the two inks mix — its red appearance (M+Y) times
    /// cyan is black — and nothing is reported. (It painted its alternate over the cyan and reported
    /// the spot colorant while the per-paint compositor had no fifth plate; renamed, 2026-10-04.)
    /// The control: without simulation the spot covers the cyan, red.
    /// </summary>
    [Fact]
    public void ASpotSeparation_OverprintsTheBackdrop_AndReportsNothing()
    {
        var reasons = new List<string>();
        SKColor c = Centre("q /GS0 gs /SepSpot cs 1 scn 20 20 60 60 re f Q", simulate: true, reasons);

        Assert.True(PixelAssert.Near(c, new SKColor(0, 0, 0)), $"the spot over cyan should mix to black, got {c}");
        Assert.Empty(reasons);
        SKColor plain = Centre("q /GS0 gs /SepSpot cs 1 scn 20 20 60 60 re f Q", simulate: false);
        Assert.True(PixelAssert.Near(plain, new SKColor(255, 0, 0)), $"without simulation the spot covers the cyan, got {plain}");
    }

    /// <summary>
    /// The raster renderer simulates the spot ink, so it names nothing; the SVG writer has no fifth
    /// plate and still names the spot colorant, not "not CMYK" — the divergence plate-based
    /// simulation accepts (GAPS.md §11k).
    /// </summary>
    [Fact]
    public void ASpotSeparation_IsSimulatedInRaster_AndNamedBySvg()
    {
        const string content = "q /GS0 gs /SepSpot cs 1 scn 20 20 60 60 re f Q";

        Assert.Empty(RasterReasons(content));
        Assert.Contains(SvgReasons(content), d => d.Contains("spot colorant"));
    }

    // ====================================================================
    //  The SVG approximation follows the same decision
    // ====================================================================

    [Fact]
    public void AProcessSeparation_IsMultipliedInSvg_WhenSimulating()
    {
        XElement path = FilledPath(Svg("q /GS0 gs /SepM cs 1 scn 20 20 60 60 re f Q", simulate: true));

        Assert.Contains("mix-blend-mode:multiply", path.Attribute("style")?.Value ?? string.Empty,
                        StringComparison.Ordinal);
    }

    /// <summary>The SVG side applies the colorant-set rule's independence from <c>/OPM</c> too.</summary>
    [Fact]
    public void AProcessSeparation_IsMultipliedInSvg_UnderOverprintModeZero()
    {
        XElement path = FilledPath(Svg("q /GS1 gs /SepM cs 1 scn 20 20 60 60 re f Q", simulate: true));

        Assert.Contains("mix-blend-mode:multiply", path.Attribute("style")?.Value ?? string.Empty,
                        StringComparison.Ordinal);
    }

    /// <summary>The SVG side's fall-back after a Separation, the twin of the raster test above.</summary>
    [Fact]
    public void ADeviceCmykColourAfterASeparation_IsNotMultipliedInSvg_UnderOverprintModeZero()
    {
        const string content = "q /GS1 gs /SepM cs 1 scn 0 1 0 0 k 20 20 60 60 re f Q";

        XElement path = FilledPath(Svg(content, simulate: true));
        Assert.DoesNotContain("multiply", path.Attribute("style")?.Value ?? string.Empty,
                              StringComparison.Ordinal);
        Assert.Contains(SvgReasons(content), d => d.Contains("/OPM is not 1"));
    }

    [Fact]
    public void ASpotSeparation_IsNotMultipliedInSvg()
    {
        XElement path = FilledPath(Svg("q /GS0 gs /SepSpot cs 1 scn 20 20 60 60 re f Q", simulate: true));

        Assert.DoesNotContain("multiply", path.Attribute("style")?.Value ?? string.Empty,
                              StringComparison.Ordinal);
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>The centre pixel of the 100 × 100 page, collecting overprint reports on this thread.</summary>
    private static SKColor Centre(string content, bool simulate, List<string> reasons = null,
                                  string pageFill = "1 0 0 0 k")
    {
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (reasons != null && Environment.CurrentManagedThreadId == ownThread
                && k == RenderDiagnosticKind.OverprintNotSimulated) reasons.Add(d ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(BuildPdf(content, pageFill)), 0, 72,
                new PdfRenderOptions { SimulateOverprint = simulate });
            using var bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);
            return bmp!.GetPixel(50, 50);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }
    }

    private static List<string> RasterReasons(string content)
    {
        var reasons = new List<string>();
        Centre(content, simulate: true, reasons);
        return reasons;
    }

    private static List<string> SvgReasons(string content)
    {
        var reasons = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId == ownThread
                && k == RenderDiagnosticKind.OverprintNotSimulated) reasons.Add(d ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Sink;
        try { Svg(content, simulate: true); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return reasons;
    }

    private static string Svg(string content, bool simulate)
    {
        using var ms = new MemoryStream(BuildPdf(content));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = simulate });
    }

    /// <summary>The overprinting rectangle's <c>&lt;path&gt;</c>: the last filled path on the page.</summary>
    private static XElement FilledPath(string svg)
    {
        XElement[] paths = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Where(e => (e.Attribute("fill")?.Value ?? "none") != "none")
            .ToArray();
        Assert.True(paths.Length >= 2, "the export should hold the page fill and the overprinting rectangle:"
                                       + Environment.NewLine + svg);
        return paths[paths.Length - 1];
    }

    /// <summary>
    /// A page filled with <paramref name="pageFill"/> (cyan by default) and then
    /// <paramref name="content"/>, with two ExtGStates — <c>/GS0</c> under <c>/OPM 1</c>,
    /// <c>/GS1</c> under <c>/OPM 0</c> — and five colorant spaces: <c>/SepM</c> (Magenta),
    /// <c>/SepC</c> (Cyan), <c>/SepSpot</c> (a spot ink whose alternate is M+Y, i.e. red),
    /// <c>/DnCM</c> ([Cyan Magenta]) and <c>/SepAll</c>.
    /// </summary>
    private static byte[] BuildPdf(string content, string pageFill = "1 0 0 0 k")
    {
        string full = pageFill + " 0 0 100 100 re f " + content;

        // The DeviceN tint transform is a Type 4 function: c m -> c m 0 0.
        const string deviceNFn = "{ 0 0 }";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << " +
            "/ExtGState << /GS0 5 0 R /GS1 6 0 R >> " +
            "/ColorSpace << /SepM [/Separation /Magenta /DeviceCMYK 7 0 R] " +
            "/SepC [/Separation /Cyan /DeviceCMYK 8 0 R] " +
            "/SepSpot [/Separation /SPOT_Red /DeviceCMYK 9 0 R] " +
            "/DnCM [/DeviceN [/Cyan /Magenta] /DeviceCMYK 10 0 R] " +
            "/SepAll [/Separation /All /DeviceCMYK 11 0 R] >> >> >>",
            $"<< /Length {full.Length} >>\nstream\n{full}\nendstream",
            "<< /Type /ExtGState /OP true /op true /OPM 1 >>",
            "<< /Type /ExtGState /OP true /op true /OPM 0 >>",
            "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] /C0 [0 0 0 0] /C1 [0 1 0 0] /N 1 >>",
            "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>",
            "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>",
            $"<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1 0 1] /Length {deviceNFn.Length} >>\n" +
            $"stream\n{deviceNFn}\nendstream",
            "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] /C0 [0 0 0 0] /C1 [1 1 1 1] /N 1 >>",
        };

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
