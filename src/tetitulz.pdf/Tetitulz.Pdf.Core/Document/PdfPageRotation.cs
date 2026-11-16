// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Reading a page's <c>/Rotate</c> (§7.7.3.3, Table 30): "the number of degrees by which the
    /// page shall be rotated clockwise when displayed. <b>The value shall be a multiple of 90.</b>"
    /// </summary>
    /// <remarks>
    /// <para><b>Seven copies normalised it and only some validated it</b>, and that split is the
    /// whole defect. Every copy wrote <c>((r % 360) + 360) % 360</c> — the two Accessibility ones
    /// spell it as a compound assignment, but it is the same arithmetic — and then:</para>
    /// <list type="bullet">
    ///   <item><c>PdfArtifactMarker</c> and <c>PdfAutoTagger</c> reject a non-multiple of 90 and
    ///     refuse the page.</item>
    ///   <item>The WinForms viewer's <c>PageGeometry</c> quantises it to 0.</item>
    ///   <item><b>Neither renderer checked at all.</b></item>
    /// </list>
    ///
    /// <para><b>What that costs on <c>/Rotate 45</c>.</b> The raster path feeds 45 straight to
    /// <c>canvas.RotateDegrees</c> while its <c>swap = rotate == 90 || rotate == 270</c> test is
    /// false, so the page is rendered at an arbitrary angle inside an un-swapped bitmap and its
    /// corners are clipped away; the SVG path emits <c>rotate(45,…)</c> into an un-swapped
    /// <c>viewBox</c>. Meanwhile the viewer reports 0, so every link hotspot, search highlight and
    /// text-selection rectangle is placed as if the page were upright. Three different answers to
    /// one sentence of the spec.</para>
    ///
    /// <para><b>Policy stays with the caller.</b> This returns the quantised angle <i>and</i> says
    /// whether the file's value was legal; it does not decide what to do about it. A renderer
    /// draws upright and may report; a tagger refuses the page. Collapsing those into one answer
    /// would be the mistake <c>PdfLimits</c>' header warns about — centralising a value is not the
    /// same as unifying the decisions built on it.</para>
    /// </remarks>
    public static class PdfPageRotation
    {
        private const int FullTurn = 360;
        private const int QuarterTurn = 90;

        /// <summary>
        /// Normalises <paramref name="rotate"/> to one of 0, 90, 180, 270.
        /// </summary>
        /// <param name="rotate">The raw <c>/Rotate</c> value; <c>null</c> means absent, i.e. 0.</param>
        /// <param name="malformed">
        /// <c>true</c> when the file's value was not a multiple of 90 — in which case the return is
        /// <b>0</b>, the spec's default, rather than a nonsense angle.
        /// </param>
        public static int Normalize(int? rotate, out bool malformed)
        {
            int r = Reduce(rotate);
            malformed = IsMalformed(r);
            return malformed ? 0 : r;
        }

        /// <summary>
        /// Reduces <paramref name="rotate"/> to [0, 360) <b>without</b> judging whether it is the
        /// multiple of 90 §7.7.3.3 requires.
        /// </summary>
        /// <remarks>
        /// Exists for the callers that <i>refuse</i> a malformed page, because such a caller has to
        /// name the angle the file actually asked for. <see cref="Normalize(int?, out bool)"/>
        /// returns 0 for a malformed value — the spec default, which is right for a renderer — so a
        /// warning built from its return reads "/Rotate 0 is not a multiple of 90". That is the one
        /// thing that kept `PdfArtifactMarker` and `PdfAutoTagger` on their own copies of this
        /// arithmetic after the other five sites were converted.
        /// </remarks>
        public static int Reduce(int? rotate) => (((rotate ?? 0) % FullTurn) + FullTurn) % FullTurn;

        /// <summary>
        /// Whether <paramref name="reducedRotation"/> — a value from <see cref="Reduce"/> — is not
        /// the multiple of 90 that §7.7.3.3 requires.
        /// </summary>
        public static bool IsMalformed(int reducedRotation) => reducedRotation % QuarterTurn != 0;

        /// <summary>Normalises without reporting legality, for callers that have nothing to do
        /// with the answer.</summary>
        public static int Normalize(int? rotate) => Normalize(rotate, out _);

        /// <summary>
        /// Whether <paramref name="normalizedRotation"/> exchanges the page's width and height —
        /// true for 90 and 270.
        /// </summary>
        /// <remarks>
        /// Its own function because the test was written out at each site as
        /// <c>r == 90 || r == 270</c>, and at the sites that had not quantised first that test
        /// silently answered "no" for every illegal angle — which is how a 45° page ended up drawn
        /// rotated inside a portrait bitmap.
        /// </remarks>
        public static bool SwapsAxes(int normalizedRotation)
            => normalizedRotation == 90 || normalizedRotation == 270;
    }
}
