// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// A §12.4.4 Table 161 page transition, as
    /// <see cref="PdfPageManipulator.SetPageTransition(System.IO.Stream, System.IO.Stream, int, PageTransitionSettings, double?)"/>
    /// writes it.
    /// </summary>
    /// <remarks>
    /// <para>A separate type from the read-side <c>Core.Document.PdfTransition</c>, for the reason
    /// <c>ViewerPreferenceSettings</c> gives: that one is get-only with an <c>internal</c>
    /// constructor, so a caller cannot mutate what it read, and widening it would make "the value in
    /// the file" and "the value I want" one object.</para>
    ///
    /// <para><b>The read side supplies defaults; this one does not.</b> <c>PdfTransition.Style</c>
    /// answers <c>"R"</c> for a page that names none, because §12.4.4 says a reader replaces
    /// instantly. Here <see cref="Style"/> is what will be <i>written</i>, so it defaults to
    /// <c>"R"</c> only in the sense that a caller who sets nothing else gets the spec's own default
    /// written explicitly rather than left implicit.</para>
    /// </remarks>
    public sealed class PageTransitionSettings
    {
        /// <summary>
        /// <c>/S</c> — the transition style: <c>Split</c>, <c>Blinds</c>, <c>Box</c>, <c>Wipe</c>,
        /// <c>Dissolve</c>, <c>Glitter</c>, <c>R</c> (replace), <c>Fly</c>, <c>Push</c>,
        /// <c>Cover</c>, <c>Uncover</c> or <c>Fade</c>.
        /// </summary>
        public string Style { get; set; } = "R";

        /// <summary><c>/D</c> — how long the transition takes, in seconds. Must not be negative.</summary>
        public double? Duration { get; set; }

        /// <summary>
        /// <c>/Dm</c> — the dimension the transition runs in: <c>H</c> or <c>V</c>. Meaningful for
        /// <c>Split</c> and <c>Blinds</c> only.
        /// </summary>
        public string Dimension { get; set; }

        /// <summary>
        /// <c>/M</c> — whether the transition moves <c>I</c>nward from the edges or <c>O</c>utward
        /// from the centre. Table 161 applies it to <c>Split</c>, <c>Box</c> <b>and <c>Fly</c></b>.
        /// </summary>
        /// <remarks>
        /// <b>The <c>Fly</c> case is not incidental</b>: <see cref="Scale"/> is defined in terms of
        /// this entry, so leaving it unset takes the <c>/I</c> default and fixes which way a
        /// <c>Fly</c> animation runs — not "Split and Box only".
        /// </remarks>
        public string Motion { get; set; }

        /// <summary>
        /// <c>/Di</c> — the direction, in degrees: 0, 90, 180, 270 or 315.
        /// </summary>
        /// <remarks>
        /// <para>A closed set of <b>angles</b>, not a free number — §12.4.4 Table 161 enumerates
        /// them, and 315 is there only because <c>Glitter</c> uses it.</para>
        ///
        /// <para>Table 161 applies <c>/Di</c> to <c>Wipe</c>, <c>Glitter</c>, <c>Fly</c>,
        /// <c>Cover</c>, <c>Uncover</c> and <c>Push</c>. Setting it on any other style is not
        /// refused here — the value is legal and a reader simply ignores it — but it will have no
        /// effect.</para>
        /// </remarks>
        public int? Direction { get; set; }

        /// <summary>
        /// Write <c>/Di /None</c> instead of an angle — what <c>Fly</c> uses to mean "no direction".
        /// Ignored when <see cref="Direction"/> is set.
        /// </summary>
        /// <remarks>
        /// A separate flag rather than a magic angle, because <c>/Di</c> is genuinely a union of a
        /// number and a name and spelling <c>None</c> as, say, -1 would be this API inventing a
        /// value the spec does not have.
        /// </remarks>
        public bool DirectionIsNone { get; set; }

        /// <summary>
        /// <c>/SS</c> — the scale a <c>Fly</c> transition starts or ends at (1.0 = full size).
        /// </summary>
        /// <remarks>
        /// <b>Which of the two it is depends on <see cref="Motion"/>.</b> Table 161: with an inward
        /// <c>/M</c> the drawing progresses from this scale to 1.0; with an outward one, from 1.0 to
        /// this scale. Calling it "the starting scale" — as this did — is true only for the default
        /// inward case.
        /// </remarks>
        public double? Scale { get; set; }

        /// <summary><c>/B</c> — for <c>Fly</c>, whether the area being flown in is opaque.</summary>
        public bool? IsOpaque { get; set; }
    }
}
