// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The §8.6.8 / §8.7.3 question "does this paint side paint a pattern, and which one?",
    /// answered once for both renderers.
    /// </summary>
    /// <remarks>
    /// <para>A <c>/Pattern</c> paint is described by <b>two</b> pieces of graphics state that are
    /// set and cleared by different operators — a flag saying the colour space is <c>/Pattern</c>
    /// (<c>cs</c>/<c>CS</c> set it, and every device colour operator <c>g</c>/<c>rg</c>/<c>k</c>
    /// plus a component-only <c>sc</c>/<c>scn</c> clears it, §8.6.8) and a name selected by
    /// <c>scn</c>/<c>SCN</c>. Neither operator clears the <i>name</i>, so the name alone outlives
    /// the space that gave it meaning.</para>
    ///
    /// <para><b>Consulting them separately makes the renderers disagree.</b> Asking
    /// <c>FillIsPattern &amp;&amp; FillPatternName != null</c> in one and <c>PatternNameFill != null</c>
    /// in the other, <c>/Pattern cs /P1 scn  1 0 0 rg  0 0 100 100 re f</c> would render RED and
    /// export the stale <c>/P1</c> — the shape <c>PatternLeakAndRotatedImageTests</c> holds (a QR
    /// code, drawn as black/white CMYK rectangles, coming out solid magenta from a banner's leftover
    /// pattern), which a raster-only test cannot see in the export.</para>
    ///
    /// <para>Both renderers now route every pattern gate through here, so the flag and the name
    /// cannot be read apart again. It is a <b>leaf by construction</b> — no static fields, hence no
    /// type initializer and no initialization edge between the two renderers, which is the hazard
    /// CLAUDE.md records as having crashed the Base test host and silently dropped ~190 tests.</para>
    /// </remarks>
    internal static class PatternPaint
    {
        /// <summary>
        /// The pattern that must paint this side, or <c>null</c> when the side paints a solid
        /// colour. <c>null</c> for a name left over from a <c>/Pattern</c> space that a later
        /// colour operator replaced.
        /// </summary>
        /// <param name="inPatternSpace">
        /// <c>GraphicsState.FillIsPattern</c> / <c>StrokeIsPattern</c> (raster) or
        /// <c>SvgGs.FillIsPattern</c> / <c>StrokeIsPattern</c> (SVG).
        /// </param>
        /// <param name="patternName">The name last selected by <c>scn</c>/<c>SCN</c> for this side.</param>
        internal static string SelectedName(bool inPatternSpace, string patternName)
            => inPatternSpace ? patternName : null;

        /// <summary>
        /// True for §8.6.8 Table 74's <b>initial</b> <c>/Pattern</c> colour — "a pattern object
        /// that causes nothing to be painted": the space is <c>/Pattern</c> and no
        /// <c>scn</c>/<c>SCN</c> has named one yet. Such a paint must produce no ink at all, in
        /// either renderer.
        /// </summary>
        internal static bool SelectedNothing(bool inPatternSpace, string patternName)
            => inPatternSpace && patternName == null;
    }
}
