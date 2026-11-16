// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// The current font as <c>setfont</c> left it — part of the graphics state (PLRM §4.2), so
    /// <c>gsave</c> keeps it and <c>grestore</c> puts it back. Immutable: a state's copy is shared by
    /// the states cloned from it, and every change makes a new one.
    /// </summary>
    /// <param name="Matrix">The font matrix <c>scalefont</c> / <c>makefont</c> made.</param>
    /// <param name="Encoding">The font's <c>/Encoding</c> as code to glyph name, or null for WinAnsi.</param>
    /// <param name="CMap">A composite font's CMap — a predefined one, or one the program defined — by name, or null.</param>
    /// <param name="Embedded">The font program the font carries, or null.</param>
    /// <param name="IsType3">Whether the font is a Type 3 (user-defined) one.</param>
    /// <param name="Type3">The recognised Type 3 font, or null.</param>
    /// <param name="Dict">The font dictionary — what <c>currentfont</c> answers — or null until a font is set.</param>
    internal sealed record PsFontState(
        PsMatrix Matrix,
        IReadOnlyDictionary<int, string> Encoding,
        string CMap,
        PsEmbeddedFont Embedded,
        bool IsType3,
        PsType3Font Type3,
        PsDict Dict)
    {
        /// <summary>No font set: the identity matrix and nothing else.</summary>
        public static readonly PsFontState Initial = new(PsMatrix.Identity, null, null, null, false, null, null);
    }
}
