// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Why an overprint the file asked for (§11.7.4.2) was not composited per colorant — one bit
    /// per reason, so a page that hits several of them says each of them once.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a single <c>bool</c></b>: the first reason reached on a thread would suppress
    /// every other for the life of the thread, so a file that overprints an image <i>and</i> sets
    /// <c>/OPM 0</c> would report one of the two, chosen by paint order — and a test asserting the image reason would pass or fail on what else the
    /// page happened to paint. Bolting the four operation-level reasons below onto that slot would
    /// have made them mostly invisible.</para>
    /// <para>The storage stays with the caller — <c>[ThreadStatic]</c> on
    /// <see cref="PdfRenderContext"/>, per-page on <see cref="SvgSharedState"/> — because the two
    /// renderers scope it differently and neither scope belongs to this type.</para>
    /// </remarks>
    [Flags]
    internal enum OverprintReportReason
    {
        /// <summary>Nothing reported yet.</summary>
        None = 0,

        /// <summary>The caller did not set <c>PdfRenderOptions.SimulateOverprint</c>.</summary>
        SimulationOff = 1 << 0,

        /// <summary><c>/OPM</c> is not 1, so the nonzero-overprint rule does not apply.</summary>
        OverprintMode = 1 << 1,

        /// <summary>The source colour was not specified in DeviceCMYK.</summary>
        NonCmykSource = 1 << 2,

        /// <summary>The operation carries a soft mask.</summary>
        SoftMask = 1 << 3,

        /// <summary>The page pixels the compositor has to read back are not available.</summary>
        UnreadableBackdrop = 1 << 4,

        /// <summary>The active clip is not rectangular.</summary>
        NonRectangularClip = 1 << 5,

        /// <summary>An image XObject — the simulator composites path geometry only.</summary>
        Image = 1 << 6,

        /// <summary>A shading painted by <c>sh</c>.</summary>
        Shading = 1 << 7,

        /// <summary>A tiling or shading pattern fill.</summary>
        Pattern = 1 << 8,

        // Bits 9 and 10 were TextRenderMode ("a text render mode other than 0") and Text ("a
        // renderer that composites no text per colorant"). Both renderers now cover every painting
        // text mode — the raster path composites each half through glyph capture, the SVG writer
        // applies its multiply approximation to <text> and glyph outlines as it does to paths — so
        // neither reason can be reached, and a reason nothing can raise is a reason a caller will
        // wait for. The bit positions are not reused, so a host filtering on the numeric value of
        // a later member keeps its meaning.

        /// <summary>Overprint <i>was</i> approximated, and the approximation is not the raster result.</summary>
        MultiplyApproximation = 1 << 11,

        /// <summary>
        /// The source is a <c>/Separation</c> or <c>/DeviceN</c> naming a spot ink — a colorant
        /// that is not one of the four process colorants — which a CMYK compositing model cannot
        /// lay down over the backdrop.
        /// </summary>
        SpotColorant = 1 << 12,

        // Bits 13 and 14 are not used: §8.6.7 keeps non-zero overprint mode off images, so a
        // DeviceCMYK image has nothing to composite. The positions are not reused.
    }

    /// <summary>
    /// The one-shot gate and the shared wording behind every overprint diagnostic, reached from
    /// both renderers.
    /// </summary>
    /// <remarks>
    /// A leaf by construction: no static fields, so no type initializer. That is the property
    /// CLAUDE.md's "share downward into a leaf, never sideways between peers" rule is about —
    /// pointing one renderer at the other's static once crashed the Base test host part-way
    /// through a parallel run by creating a type-initialization edge.
    /// </remarks>
    internal static class OverprintReporting
    {
        /// <summary>
        /// Reports <paramref name="detail"/> the first time <paramref name="reason"/> is seen in
        /// the scope <paramref name="reported"/> represents, and never again for that reason.
        /// </summary>
        internal static void ReportOnce(ref OverprintReportReason reported,
                                        OverprintReportReason reason,
                                        RenderDiagnosticKind kind,
                                        string detail)
        {
            if ((reported & reason) != 0) return;
            reported |= reason;
            RenderDiagnostics.Report(kind, detail);
        }

        /// <summary>
        /// The renderer-neutral sentence for a reason, so the raster and SVG paths cannot describe
        /// the same gap two different ways — the divergence this repository keeps finding.
        /// </summary>
        internal static string Describe(OverprintReportReason reason) => reason switch
        {
            OverprintReportReason.SimulationOff =>
                "PdfRenderOptions.SimulateOverprint is off (the default): overprint is a press " +
                "behaviour viewers expose as a preview toggle, so simulating it is opt-in.",
            OverprintReportReason.OverprintMode =>
                "/OPM is not 1; only nonzero overprint mode (1) is simulated, and OPM 0 over " +
                "DeviceCMYK writes every colorant anyway.",
            OverprintReportReason.NonCmykSource =>
                "the source colour was not set in DeviceCMYK or in a Separation/DeviceN of process " +
                "colorants (§11.7.4.2 restricts nonzero overprint mode to DeviceCMYK, and a source " +
                "with no colorants has nothing to preserve).",
            OverprintReportReason.SpotColorant =>
                "the source colour names a spot colorant — a /Separation or /DeviceN ink that is " +
                "not Cyan, Magenta, Yellow or Black. §11.7.4.5 leaves every other colorant " +
                "unchanged and lays the spot ink down over them, which a four-channel CMYK " +
                "compositing model cannot express; that needs a separation device (GAPS §11a).",
            OverprintReportReason.SoftMask =>
                "the operation carries a soft mask.",
            OverprintReportReason.UnreadableBackdrop =>
                "the page pixels are not readable here (no render target was supplied, or the " +
                "operation is inside a transparency group or opacity layer).",
            OverprintReportReason.NonRectangularClip =>
                "the active clip is not rectangular.",
            OverprintReportReason.Image =>
                "the painting operation is an image in a /Separation or /DeviceN space naming some " +
                "colorants and not others; §11.7.4.5 leaves the unnamed ones unchanged, which the " +
                "simulator does not composite for an image. (Any other image writes every process " +
                "colorant — §8.6.7 keeps nonzero overprint mode off images — so its ordinary draw " +
                "is already exact.)",
            OverprintReportReason.Shading =>
                "the painting operation is a shading (sh). The colour varies per pixel and comes " +
                "from the shading's own function, so there is no single source colorant set to " +
                "composite with.",
            OverprintReportReason.Pattern =>
                "the painting operation is a pattern fill. The ink laid down is the pattern cell's " +
                "own colour, not the current colour the simulator would composite.",
            _ => "the operation is not covered by the overprint simulator.",
        };
    }
}
