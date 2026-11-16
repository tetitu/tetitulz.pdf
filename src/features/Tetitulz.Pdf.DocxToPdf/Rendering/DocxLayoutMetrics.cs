// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Layout factors and OOXML element names used by the DOCX renderers.
    ///
    /// <para>Each of these is read by
    /// <see cref="DocxRenderContext"/>, <see cref="ParagraphRenderer"/>,
    /// <see cref="RunRenderer"/>, <see cref="StyleResolver"/> or <see cref="TableRenderer"/>, so one
    /// concept ("one line of leading") is one named value at every site that measures a line, and the
    /// <c>"oMath"</c> / <c>"oMathPara"</c> pair is spelled once.</para>
    ///
    /// <para>Not every entry is read from more than one renderer today; they live here so that a
    /// reader comparing two similar-looking factors (say <see cref="NoteFontScale"/> against
    /// <see cref="NoteMarkFontScale"/>) can see both at once and tell which is which.</para>
    /// </summary>
    internal static class DocxLayoutMetrics
    {
        /// <summary>Single-spaced line height as a multiple of the font size (Word's default).</summary>
        internal const double SingleLineFactor = 1.2;

        /// <summary>Line height used for table-cell content, which is laid out slightly looser.</summary>
        internal const double CellLineFactor = 1.4;

        /// <summary>
        /// Vertical space reserved above the first footnote on a page (the separator gap), as a
        /// multiple of the note font size. Numerically equal to <see cref="CellLineFactor"/> but a
        /// different concept — changing one must not silently change the other.
        /// </summary>
        internal const double FootnoteSeparatorFactor = 1.4;

        /// <summary>Minimum table-row height as a multiple of the default font size.</summary>
        internal const double MinRowHeightFactor = 1.5;

        /// <summary>Footnote / endnote <i>body</i> text size relative to the body default font size.</summary>
        internal const double NoteFontScale = 0.83;

        /// <summary>
        /// Size of the superscript footnote/endnote <i>reference mark</i> drawn in the body, relative
        /// to the surrounding run. Distinct from <see cref="NoteFontScale"/>, which sizes the note
        /// text down in the band.
        /// </summary>
        internal const double NoteMarkFontScale = 0.7;

        /// <summary>Baseline rise of the note reference mark, as a multiple of the run's font size.</summary>
        internal const double NoteMarkRiseFactor = 0.33;

        /// <summary>Size of a <c>w:vertAlign</c> superscript/subscript run relative to its font size.</summary>
        internal const double VertAlignFontScale = 0.65;

        /// <summary>Size of the capital a <c>w:smallCaps</c> run draws for a lowercase letter, relative to its font size.</summary>
        internal const double SmallCapsScale = 0.8;

        /// <summary>Baseline rise of a <c>w:vertAlign="superscript"</c> run, as a multiple of its font size.</summary>
        internal const double SuperscriptRiseFactor = 0.35;

        /// <summary>Baseline drop of a <c>w:vertAlign="subscript"</c> run, as a multiple of its font size.</summary>
        internal const double SubscriptDropFactor = 0.15;

        /// <summary>
        /// Word's default page-border offset, in points, when <c>w:pgBorders@offsetFrom="page"</c>.
        /// </summary>
        internal const double PageBorderPageOffsetPt = 24.0;

        /// <summary>
        /// Hairline border width, in points, used when a border is drawn but no <c>w:sz</c> says how
        /// thick. Shared by the style resolver (a <c>w:*Border</c> with no size) and the table
        /// renderer (a cell with no border information at all), so the two cannot disagree about
        /// what "thin default" means.
        /// </summary>
        internal const double DefaultBorderWidthPt = 0.5;

        /// <summary>Local name of an Office Math (OMML) inline equation element.</summary>
        internal const string OMathElement = "oMath";

        /// <summary>Local name of an Office Math (OMML) display-equation paragraph element.</summary>
        internal const string OMathParaElement = "oMathPara";
    }
}
