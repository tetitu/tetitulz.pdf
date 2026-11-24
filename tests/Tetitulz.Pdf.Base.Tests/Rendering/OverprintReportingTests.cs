// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// What the renderers <b>say</b> about an overprint (§11.7.4.2) they did not composite per
/// colorant.
///
/// <para><b>Why the one-shot had to change shape before any of this could be tested.</b> The
/// raster guard was a single <c>[ThreadStatic] bool</c>: the first reason reached on a thread
/// silenced every other for the life of the thread. A file that overprints an image <i>and</i>
/// uses a non-CMYK source reported one of the two, chosen by paint order — so a test asserting
/// the image reason would have passed or failed on what else the page happened to paint, which is
/// a test that measures the fixture rather than the code. The guard is now one bit per reason.</para>
///
/// <para>Assertions are on the reported <i>reasons</i>, not on "something was reported": the whole
/// point of the change is that a caller can tell the reasons apart.</para>
/// </summary>
public class OverprintReportingTests
{
    // ====================================================================
    //  The mechanism: reasons do not silence each other
    // ====================================================================

    /// <summary>
    /// One page, two distinct gaps — an RGB source colour and a shading, both under
    /// <c>/op true</c>. Both must be named; a single-flag guard would let the fill painted first take
    /// the flag and leave the second operation silent.
    /// </summary>
    /// <remarks>
    /// The second half is a <b>shading</b>, a genuinely different reason; an RGB image would decline
    /// for the same reason as the RGB fill, and the guard says that once. Simulation is off: with it
    /// on, the raster renderer simulates a page that overprints through its separation plates
    /// (<c>OverprintPreview</c>) and declines nothing.
    /// </remarks>
    [Fact]
    public void TwoDifferentSkipReasons_OnOnePage_AreBothReported()
    {
        var details = RasterDetails(
            "q /GS0 gs 1 0 0 rg 10 10 20 20 re f 0 0 0 1 k /Sh1 sh Q",
            simulate: false);

        Assert.Contains(details, d => d.Contains("SimulateOverprint is off"));
        Assert.Contains(details, d => d.Contains("shading (sh)"));
    }

    /// <summary>
    /// The same reason twice in one page is still said once — the reason the guard exists at all.
    /// Two RGB fills, one report. (An overprinting image is no reason at all — §8.6.7; see
    /// <c>OverprintOnAnRgbImage_ReportsNothing</c>.)
    /// </summary>
    [Fact]
    public void TheSameSkipReasonTwice_IsReportedOnce()
    {
        var details = RasterDetails(
            "q /GS0 gs 1 0 0 rg 10 10 20 20 re f 40 40 20 20 re f Q",
            simulate: false);

        Assert.Single(details, d => d.Contains("SimulateOverprint is off"));
    }

    // ====================================================================
    //  The four operations the simulator does not cover (plan 08, item 22)
    // ====================================================================

    /// <summary>
    /// An overprinting RGB image reports <b>nothing</b>: §8.6.7 keeps non-zero overprint mode off
    /// images, and an RGB image converts into every process colorant, so its ordinary draw is the
    /// overprinted result. The RGB fill drawn first is the potency control — it is reported — so
    /// the image's silence is not a sink that heard nothing.
    ///
    /// <para><b>Renamed twice, never re-asserted under an old name.</b> It was
    /// <c>OverprintOnAnImage_IsReported</c> (every image reported), then
    /// <c>OverprintOnANonCmykImage_IsReported</c> while DeviceCMYK images were composited per sample
    /// (2026-08-18 to 2026-10-03), which the clause forbids.</para>
    /// </summary>
    [Fact]
    public void OverprintOnAnRgbImage_ReportsNothing()
    {
        var details = RasterDetails("q /GS0 gs 1 0 0 rg 10 10 10 10 re f 100 0 0 100 0 0 cm /Im1 Do Q",
                                    simulate: false);
        Assert.Contains(details, d => d.Contains("SimulateOverprint is off"));
        Assert.DoesNotContain(details, d => d.Contains("image", StringComparison.Ordinal));
    }

    /// <summary>
    /// An overprinting shading or pattern fill is named when simulation is off, and simulated —
    /// nothing reported — when it is on: the raster renderer composites it on the page's separation
    /// plates (<c>OverprintPreview</c>).
    /// </summary>
    [Theory]
    [InlineData("q /GS0 gs 0 0 0 1 k /Sh1 sh Q", "shading (sh)")]
    [InlineData("q /GS0 gs /Pattern cs /P1 scn 10 10 50 50 re f Q", "pattern fill")]
    public void OverprintOnAShadingOrPattern_IsReportedWhenOff_AndSimulatedWhenOn(string content, string reason)
    {
        Assert.Contains(RasterDetails(content, simulate: false), d => d.Contains(reason));
        Assert.Empty(RasterDetails(content, simulate: true));
    }

    /// <summary>
    /// A stroking render mode is <b>simulated</b>, so it reports nothing — the pen is composited
    /// per colorant like a path stroke's.
    /// </summary>
    /// <remarks>
    /// <para>Both halves of every painting mode go through glyph capture, so "a text render mode
    /// other than 0" is no reason to decline, and a report here would be a diagnostic that lies.</para>
    /// <para>The sink is filtered to this thread because the assertion is negative.</para>
    /// </remarks>
    [Theory]
    [InlineData("0 0 0 1 K", "1 Tr")]                // stroke only
    [InlineData("0 0 0 1 k 0 0 0 1 K", "2 Tr")]      // fill then stroke
    [InlineData("0 0 0 1 k 0 0 0 1 K", "6 Tr")]      // fill, stroke and clip
    public void OverprintInAStrokingTextRenderMode_IsSimulatedAndNotReported(string colours, string mode)
    {
        var details = RasterDetails(
            $"q /GS0 gs {colours} BT /F1 24 Tf {mode} 10 10 Td (Hi) Tj ET Q", simulate: true);

        Assert.Empty(details);
    }

    /// <summary>
    /// The acceptance control for the theory above: the stroke half still runs the state ladder,
    /// so a pen set in a non-CMYK space reports the same reason a non-CMYK path stroke does.
    /// </summary>
    /// <remarks>
    /// <para>Without this, a change that simply stopped reporting for text altogether would satisfy
    /// the theory above. A DeviceRGB pen has no colorants for overprint to preserve, and the reason
    /// names that rather than the render mode.</para>
    /// <para><b>The fill is DeviceCMYK on purpose.</b> Mode 1 paints no fill, so the report must
    /// come from the <i>stroke</i> side's ladder; with a CMYK fill in the state, a reporter that
    /// consulted the fill's colorants by mistake would find nothing to say and this test would
    /// fail. With the default black fill both sides declined for the same reason and the mistake
    /// was invisible.</para>
    /// </remarks>
    /// <para><b>Restated 2026-10-04.</b> A non-CMYK pen was the reason this control reported; with
    /// simulation on it is now simulated on the plates like any other, so the control is the
    /// opt-in instead: off, the stroke side reports, and never the render mode; on, nothing.</para>
    [Fact]
    public void OverprintOnANonCmykPen_IsSimulated_AndReportsTheOptInNotTheRenderModeWhenOff()
    {
        const string content = "q /GS0 gs 0 0 0 1 k 1 0 0 RG BT /F1 24 Tf 1 Tr 10 10 Td (Hi) Tj ET Q";
        var off = RasterDetails(content, simulate: false);

        Assert.Contains(off, d => d.Contains("SimulateOverprint is off"));
        Assert.DoesNotContain(off, d => d.Contains("render mode"));
        Assert.Empty(RasterDetails(content, simulate: true));
    }

    /// <summary>
    /// Render mode 0 is simulated too, so claiming it was skipped would be a diagnostic that lies —
    /// the thing <c>RenderDiagnosticKind</c>'s own contract forbids.
    /// </summary>
    /// <remarks>
    /// <b>Both colours are load-bearing, and the second is the one that can fail.</b> With
    /// <c>0 0 0 1 k</c> the run is routed to the glyph-capture compositor; with every CMYK
    /// component non-zero there is nothing for overprint to preserve, so the run is <i>not</i>
    /// routed and reaches the reporting ladder, still under render mode 0 — and the ladder must
    /// find nothing to say, because a normal paint is already exact.
    /// </remarks>
    [Theory]
    [InlineData("0 0 0 1 k")]           // composited per colorant
    [InlineData("0.2 0.2 0.2 0.2 k")]   // no zero component: a normal paint is already exact
    public void OverprintInTextRenderModeFill_IsNotReportedAsASkippedMode(string colour)
    {
        var details = RasterDetails(
            $"q /GS0 gs {colour} BT /F1 24 Tf 10 10 Td (Hi) Tj ET Q", simulate: true);

        Assert.Empty(details);
    }

    // ====================================================================
    //  The opt-in-off policy, now the same on both sides
    // ====================================================================

    /// <summary>
    /// With <c>SimulateOverprint</c> off — the default — both renderers report, so the same prepress
    /// file is not announced by one exporter and not the other.
    /// </summary>
    [Fact]
    public void SimulationOff_IsReportedByBothRenderers()
    {
        const string content = "q /GS0 gs 0 0 0 1 k 10 10 50 50 re f Q";

        Assert.Contains(RasterDetails(content, simulate: false),
                        d => d.Contains("SimulateOverprint is off"));
        Assert.Contains(SvgDetails(content, simulate: false),
                        d => d.Contains("SimulateOverprint is off"));
    }

    // ====================================================================
    //  SVG: the one-shot is per PAGE, not per writer
    // ====================================================================

    /// <summary>
    /// A page runs many SVG writers — one per form XObject, pattern cell, soft mask, Type 3 glyph
    /// and annotation appearance — so a guard held as a field on the writer would let each of them
    /// re-report the same thing. This page overprints on its own content <i>and</i> inside a form
    /// XObject; one report is the whole claim.
    /// </summary>
    [Fact]
    public void SvgOverprintReport_IsOncePerPage_NotOncePerFormXObject()
    {
        var details = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == RenderDiagnosticKind.OverprintNotSimulated)
                lock (details) details.Add(d ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(BuildPdfWithOverprintingForm());
            PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = true });
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Single(details, d => d.Contains("DeviceCMYK"));
    }

    // ====================================================================
    //  The one-shot is re-armed per render, not per process thread
    // ====================================================================

    /// <summary>
    /// The <c>[ThreadStatic]</c> flags were re-armed only by a test helper, so on a server host the
    /// second and every later page rendered on a thread reported nothing at all: a host that had
    /// rendered one overprinting document could never learn that the next one overprints too.
    /// Two renders, and the assertion is about the <b>second</b>.
    /// </summary>
    [Fact]
    public void OverprintReporting_IsReArmedForEachPageRender()
    {
        const string content = "q /GS0 gs 0 0 0 1 k 10 10 50 50 re f Q";

        RasterDetails(content, simulate: false);            // first render takes the flag
        var second = RasterDetails(content, simulate: false);

        Assert.Contains(second, d => d.Contains("SimulateOverprint is off"));
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>
    /// Renders the content stream and returns the details of every
    /// <see cref="RenderDiagnosticKind.OverprintNotSimulated"/> raised on this thread.
    /// </summary>
    /// <remarks>
    /// The sink filters on the managed thread id. <c>RenderDiagnostics.OnEvent</c> is process-wide,
    /// so an unfiltered handler also collects whatever xUnit is rendering in parallel — which turns
    /// a negative assertion into "no other test overprinted while I ran".
    /// </remarks>
    private static List<string> RasterDetails(string content, bool simulate)
    {
        var details = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == RenderDiagnosticKind.OverprintNotSimulated)
                lock (details) details.Add(d ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(BuildPdf(content));
            PdfPageRenderer.RenderPage(ms, 0, 72, new PdfRenderOptions { SimulateOverprint = simulate });
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        return details;
    }

    /// <inheritdoc cref="RasterDetails"/>
    private static List<string> SvgDetails(string content, bool simulate)
    {
        var details = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == RenderDiagnosticKind.OverprintNotSimulated)
                lock (details) details.Add(d ?? string.Empty);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(BuildPdf(content));
            PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = simulate });
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        return details;
    }

    /// <summary>
    /// One page carrying every resource the tests need: an ExtGState that sets overprint on both
    /// paint sides under <c>/OPM 1</c>, an image, a shading, a tiling pattern and a font.
    /// </summary>
    internal static byte[] BuildPdf(string content) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << " +
        "/ExtGState << /GS0 5 0 R >> /XObject << /Im1 6 0 R >> /Shading << /Sh1 7 0 R >> " +
        "/Pattern << /P1 8 0 R >> /Font << /F1 9 0 R >> >> >>",
        Stream("", content),
        "<< /Type /ExtGState /OP true /op true /OPM 1 >>",
        // 2x2 DeviceRGB, hex-encoded.
        Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB" +
               " /BitsPerComponent 8 /Filter /ASCIIHexDecode",
               "FF0000 00FF00 0000FF FFFFFF>"),
        "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Extend [true true]" +
        " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>",
        Stream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10" +
               " /Resources << >>",
               "0 0 1 rg 0 0 5 5 re f"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    /// <summary>
    /// A page that overprints an RGB fill on its own content and again inside a form XObject — so
    /// two distinct <c>SvgContentWriter</c> instances hit the same gap.
    /// </summary>
    private static byte[] BuildPdfWithOverprintingForm() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << " +
        "/ExtGState << /GS0 5 0 R >> /XObject << /Fx1 6 0 R >> >> >>",
        Stream("", "q /GS0 gs 1 0 0 rg 10 10 20 20 re f /Fx1 Do Q"),
        "<< /Type /ExtGState /OP true /op true /OPM 1 >>",
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources " +
               "<< /ExtGState << /GS0 5 0 R >> >>",
               "/GS0 gs 0 1 0 rg 60 60 20 20 re f"));

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
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
