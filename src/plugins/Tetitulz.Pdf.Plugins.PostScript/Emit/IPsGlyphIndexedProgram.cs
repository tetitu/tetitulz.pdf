// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// A font program shown by glyph index — two-byte codes under <c>/Identity-H</c> in one Type0
    /// font, whatever encoding or CMap the PostScript showed it through — whose glyphs are taken as
    /// they are used and whose program is assembled when the resources are built: a Type 42 font or a
    /// Type 2 CIDFont (<see cref="PsType42Font"/>, a <c>CIDFontType2</c>), or a Type 0 CIDFont
    /// (<see cref="PsCidType0Font"/>, a <c>CIDFontType0</c> over a CFF). The collector writes both
    /// through one resource (<c>PdfResourceCollector.GetOrAddGlyphIndexedFont</c>).
    /// </summary>
    internal interface IPsGlyphIndexedProgram
    {
        /// <summary>Whether the program is CFF (a <c>CIDFontType0</c> with <c>/FontFile3</c>) rather than TrueType.</summary>
        bool IsCff { get; }

        /// <summary>Each glyph used, its advance in the 1000-unit glyph space a PDF font dictionary uses.</summary>
        IReadOnlyDictionary<int, double> Advances { get; }

        /// <summary>Each glyph used whose text is known, the text it stands for.</summary>
        IReadOnlyDictionary<int, string> Texts { get; }

        /// <summary>The font file stream of the glyphs taken so far, one instance until another is taken.</summary>
        PdfStream ProgramStream();

        /// <summary>The program's box, ascent and descent in 1000-unit glyph space, for the font descriptor.</summary>
        (double XMin, double YMin, double XMax, double YMax, double Ascent, double Descent) DescriptorMetrics();

        /// <summary>A glyph's outline in 1000-unit glyph space, advancing by <paramref name="advance"/>, or null.</summary>
        GlyphOutline Outline(int glyph, double advance, Action<long> charge);

        /// <summary>
        /// The glyph a CID-keyed program draws for <paramref name="cid"/> and its advance in 1000-unit
        /// glyph space, taken the first time it is used; <paramref name="font"/> is the CIDFont
        /// dictionary, whose <c>/GlyphDirectory</c> and <c>/Metrics</c> a program may write after it is
        /// defined, and <paramref name="text"/> the CID's text, or null.
        /// </summary>
        int GlyphOfCid(PsDict font, int cid, string text, Action<long> charge, out double advance);
    }
}
