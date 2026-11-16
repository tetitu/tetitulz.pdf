// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Where a painting operation stood when the raster renderer ran it: the matrix from its user
    /// space to the page's default user space, and for a text-showing operation the text matrix it
    /// started from and the text and line matrices it left.
    /// </summary>
    /// <remarks>
    /// What moving one operation on its page needs. A matrix <c>M</c> given in default user space
    /// acts on the operation as <c>Ctm × M × Ctm⁻¹</c> in its own space; and text shown under a
    /// matrix of its own is followed by the line matrix and the pen the original left, so the text
    /// after it is drawn where it was.
    /// </remarks>
    internal sealed class OperationPlacement
    {
        /// <summary>The operation's user space to the page's default user space.</summary>
        internal float[] Ctm;

        /// <summary>For text: the text matrix the show starts from — after the line move a <c>'</c> or a <c>"</c> makes.</summary>
        internal float[] PenStart;

        /// <summary>For text: the text line matrix once the operation has run.</summary>
        internal float[] LineAfter;

        /// <summary>For text: the text matrix once the operation has run.</summary>
        internal float[] PenAfter;

        /// <summary>
        /// For text: how far beyond the font's own widths the renderer's pen stood, along the line,
        /// where the show starts — the substituted faces' measure of the runs shown on the line
        /// before it, less what the file's widths say.
        /// </summary>
        internal float BeyondWidthsAtStart;

        /// <summary>For text: the same, once the operation has run.</summary>
        internal float BeyondWidthsAfter;

        /// <summary>The line width in force, in the operation's user space.</summary>
        internal float LineWidth;

        internal float FontSize;

        /// <summary>The horizontal scaling as a fraction (100% is 1).</summary>
        internal float HorizontalScaling;
    }
}
