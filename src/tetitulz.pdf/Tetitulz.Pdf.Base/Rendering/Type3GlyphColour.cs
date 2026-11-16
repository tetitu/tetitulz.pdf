// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// §9.6.5's rule for an uncoloured Type 3 glyph — one implementation for both renderers.
    /// </summary>
    /// <remarks>
    /// A glyph procedure that begins with <c>d1</c> (<c>setcachedevice</c>) describes a shape only:
    /// "the glyph description shall not contain any colour operators", and any it carries is to be
    /// ignored, so the glyph paints in the colour of the text that showed it — which is what makes
    /// such a font recolourable. <c>d0</c> glyphs keep their own colours. Neither renderer had the
    /// rule, so a <c>d1</c> glyph that set a colour painted in it.
    /// </remarks>
    internal static class Type3GlyphColour
    {
        /// <summary>Whether <paramref name="op"/> sets a colour or colour space (§8.6.8, Table 73).</summary>
        internal static bool IsColourOperator(string op) => op switch
        {
            PdfOperators.SetGrayFill or PdfOperators.SetGrayStroke
            or PdfOperators.SetRgbFill or PdfOperators.SetRgbStroke
            or PdfOperators.SetCmykFill or PdfOperators.SetCmykStroke
            or PdfOperators.SetColorSpaceFill or PdfOperators.SetColorSpaceStroke
            or PdfOperators.SetColorFill or PdfOperators.SetColorFillN
            or PdfOperators.SetColorStroke or PdfOperators.SetColorStrokeN => true,
            _ => false,
        };
    }
}
