// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Annotation <c>/Subtype</c> values (PDF 32000-1:2008, §12.5.6 "Annotation Types"), as <c>const string</c> so
    /// they can be used as <c>switch</c> case labels.
    ///
    /// <para>The raster renderer and the SVG renderer each carried an independent list of these
    /// names. Keeping two hand-maintained lists in step is exactly the kind of drift this type
    /// removes.</para>
    /// </summary>
    public static class PdfAnnotationSubtypes
    {
        /// <summary><c>/Text</c> — a "sticky note" annotation.</summary>
        public const string Text = "Text";
        /// <summary><c>/Link</c> — a hyperlink or destination jump.</summary>
        public const string Link = "Link";
        /// <summary><c>/FreeText</c> — text displayed directly on the page.</summary>
        public const string FreeText = "FreeText";
        /// <summary><c>/Line</c></summary>
        public const string Line = "Line";
        /// <summary><c>/Square</c></summary>
        public const string Square = "Square";
        /// <summary><c>/Circle</c></summary>
        public const string Circle = "Circle";
        /// <summary><c>/Polygon</c></summary>
        public const string Polygon = "Polygon";
        /// <summary><c>/PolyLine</c></summary>
        public const string PolyLine = "PolyLine";
        /// <summary><c>/Highlight</c></summary>
        public const string Highlight = "Highlight";
        /// <summary><c>/Underline</c></summary>
        public const string Underline = "Underline";
        /// <summary><c>/Squiggly</c></summary>
        public const string Squiggly = "Squiggly";
        /// <summary><c>/StrikeOut</c></summary>
        public const string StrikeOut = "StrikeOut";
        /// <summary><c>/Stamp</c></summary>
        public const string Stamp = "Stamp";
        /// <summary><c>/Caret</c></summary>
        public const string Caret = "Caret";
        /// <summary><c>/Ink</c></summary>
        public const string Ink = "Ink";
        /// <summary><c>/Popup</c> — the pop-up window for a parent markup annotation.</summary>
        public const string Popup = "Popup";
        /// <summary><c>/FileAttachment</c></summary>
        public const string FileAttachment = "FileAttachment";
        /// <summary><c>/Sound</c></summary>
        public const string Sound = "Sound";
        /// <summary><c>/Movie</c></summary>
        public const string Movie = "Movie";
        /// <summary><c>/Widget</c> — a form field's on-page appearance.</summary>
        public const string Widget = "Widget";
        /// <summary><c>/Screen</c></summary>
        public const string Screen = "Screen";
        /// <summary><c>/PrinterMark</c></summary>
        public const string PrinterMark = "PrinterMark";
        /// <summary><c>/TrapNet</c></summary>
        public const string TrapNet = "TrapNet";
        /// <summary><c>/Watermark</c></summary>
        public const string Watermark = "Watermark";
        /// <summary><c>/3D</c></summary>
        public const string ThreeD = "3D";
        /// <summary><c>/Redact</c></summary>
        public const string Redact = "Redact";
        /// <summary><c>/Projection</c></summary>
        public const string Projection = "Projection";
        /// <summary><c>/RichMedia</c></summary>
        public const string RichMedia = "RichMedia";
    }

    /// <summary>
    /// Bit flags for an annotation's <c>/F</c> entry (PDF 32000-1:2008, §12.5.3 "Annotation Flags").
    ///
    /// <para>Named so both renderers test the same bits: bare literals (<c>&amp; 2</c> in one,
    /// <c>&amp; 0x02</c> and no NoView check in the other) would let an annotation marked NoView
    /// render in SVG but not in raster. Naming the bits makes that class of divergence visible.</para>
    /// </summary>
    public static class PdfAnnotationFlags
    {
        /// <summary>Bit 1 — do not render if the annotation type is unknown.</summary>
        public const int Invisible = 1 << 0;
        /// <summary>Bit 2 — do not render or print the annotation at all.</summary>
        public const int Hidden = 1 << 1;
        /// <summary>Bit 3 — render when printing.</summary>
        public const int Print = 1 << 2;
        /// <summary>Bit 4 — do not scale the annotation with the page.</summary>
        public const int NoZoom = 1 << 3;
        /// <summary>Bit 5 — do not rotate the annotation with the page.</summary>
        public const int NoRotate = 1 << 4;
        /// <summary>Bit 6 — render when printing but not on screen.</summary>
        public const int NoView = 1 << 5;
        /// <summary>Bit 7 — the annotation is not interactive.</summary>
        public const int ReadOnly = 1 << 6;
        /// <summary>Bit 8 — the annotation may not be deleted or have its properties changed.</summary>
        public const int Locked = 1 << 7;
        /// <summary>Bit 9 — invert the interpretation of <see cref="NoView"/>.</summary>
        public const int ToggleNoView = 1 << 8;
        /// <summary>Bit 10 — the annotation's contents may not be modified.</summary>
        public const int LockedContents = 1 << 9;

        /// <summary>
        /// True when the flags mean the annotation must not be drawn to a screen-like target —
        /// i.e. either <see cref="Hidden"/> or <see cref="NoView"/> is set.
        /// </summary>
        public static bool IsHiddenOnScreen(int flags) =>
            (flags & Hidden) != 0 || (flags & NoView) != 0;

        /// <summary>
        /// True when the flags mean the annotation must not appear in <b>printed</b> output —
        /// <see cref="Hidden"/> is set, or <see cref="Print"/> is <i>not</i>.
        /// </summary>
        /// <remarks>
        /// <para><b>This is not <see cref="IsHiddenOnScreen"/> with a different name, and using
        /// that one for print output is wrong in both directions.</b> §12.5.3 makes printing
        /// opt-in: an annotation without <see cref="Print"/> must not print. <see cref="NoView"/> is a screen-only rule whose own documentation
        /// says "render when printing but not on screen", so applying it here suppresses exactly
        /// the annotations it exists to keep — again in both renderers.</para>
        /// <para><see cref="ToggleNoView"/> is deliberately not consulted: it inverts
        /// <see cref="NoView"/>, which does not participate in this decision at all.</para>
        /// </remarks>
        public static bool IsHiddenInPrint(int flags) =>
            (flags & Hidden) != 0 || (flags & Print) == 0;

        /// <summary>
        /// An annotation's <c>/F</c>, already resolved, as its flag word — or <c>null</c> when it is
        /// absent or is not an integer, which §12.5.3's default makes 0 for most callers.
        /// </summary>
        /// <remarks>
        /// <para><b>The one reader of <c>/F</c>.</b> Ten sites read it four ways —
        /// <c>IntValue</c>, <c>(int)LongValue</c>, <c>(int)DoubleValue</c>, and private helpers over
        /// <c>IntValue</c> in three assemblies — and they disagreed on the <i>visibility</i> of an
        /// annotation, not only on the number: <c>/F 2147483648</c> saturated to every named flag
        /// through <c>IntValue</c> (hidden) and to bit 32 alone through <c>LongValue</c> (drawn).
        /// See <see cref="Objects.PdfNumber.FlagWord"/> for the rule.</para>
        /// </remarks>
        public static int? FromNumber(Objects.PdfObject value) => (value as Objects.PdfNumber)?.FlagWord;
    }
}
