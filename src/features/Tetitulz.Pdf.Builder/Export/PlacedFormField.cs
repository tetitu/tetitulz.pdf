// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Where the PDF exporter drew a run that stands for a form field: the page, and the rectangle
    /// in that page's default user space.
    /// </summary>
    /// <remarks>
    /// <para><b>The rectangle is the run's shaded box</b> — the one the highlight pass fills — so a
    /// widget laid over it covers exactly what the page shows as the field. It is in <b>default</b>
    /// user space, the space an annotation's <c>/Rect</c> is in (§12.5.2), with the export's
    /// <see cref="PdfExportOptions.Scale"/> already applied, exactly as the exporter's own
    /// <c>/Link</c> rectangles are.</para>
    ///
    /// <para><b>One placement per field.</b> A run the layout splits across lines would place twice;
    /// the first placement wins. The HTML mapper's placeholders cannot split, since they are one
    /// unbreakable token.</para>
    /// </remarks>
    public sealed class PlacedFormField
    {
        internal PlacedFormField(FormFieldSpec field, int pageIndex,
                                 double x, double y, double width, double height, double fontSize)
        {
            Field = field;
            PageIndex = pageIndex;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            FontSize = fontSize;
        }

        /// <summary>The field the run stands for.</summary>
        public FormFieldSpec Field { get; }

        /// <summary>The zero-based page the run was drawn on.</summary>
        public int PageIndex { get; }

        /// <summary>The rectangle's left edge, in points.</summary>
        public double X { get; }

        /// <summary>The rectangle's bottom edge, in points.</summary>
        public double Y { get; }

        /// <summary>The rectangle's width, in points.</summary>
        public double Width { get; }

        /// <summary>The rectangle's height, in points.</summary>
        public double Height { get; }

        /// <summary>The size the run was drawn at, in points, scale included.</summary>
        public double FontSize { get; }
    }
}
