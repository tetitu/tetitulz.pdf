// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts.Type1
{
    /// <summary>
    /// Parsed representation of a Type 1 PostScript font program embedded in a PDF /FontFile stream.
    /// Reference: Adobe Type 1 Font Format (Black Book), ATN #5002.
    /// </summary>
    internal sealed class Type1Font
    {
        /// <summary>
        /// Encoding vector: maps char code (0–255) → glyph name.
        /// Parsed from the ASCII section's /Encoding definition.
        /// </summary>
        public string[] Encoding { get; }

        /// <summary>
        /// Charstring bytes indexed by glyph name.
        /// Each value is the raw (encrypted) charstring data after charstring-key decryption.
        /// </summary>
        public IReadOnlyDictionary<string, byte[]> CharStrings { get; }

        /// <summary>Local subroutines (Subrs) for this font.</summary>
        public IReadOnlyList<byte[]> Subrs { get; }

        /// <summary>Font matrix a-element (scales glyph space to text space; typically 0.001).</summary>
        public float FontMatrixScale { get; }

        /// <summary>
        /// False when the program declares a <c>/FontMatrix</c> the parser could not read or rejected,
        /// so <see cref="FontMatrixScale"/> is the 0.001 fallback rather than the font's own scale.
        /// </summary>
        public bool WidthScaleIsKnown { get; }

        /// <summary>
        /// True when the <c>/CharStrings</c> scan reached the dictionary's end without skipping an
        /// entry — so a glyph name missing from <see cref="CharStrings"/> is one the program does not
        /// define, not one the scan lost.
        /// </summary>
        public bool CharStringsComplete { get; }

        public Type1Font(
            string[] encoding,
            IReadOnlyDictionary<string, byte[]> charStrings,
            IReadOnlyList<byte[]> subrs,
            float fontMatrixScale = 0.001f,
            bool widthScaleIsKnown = true,
            bool charStringsComplete = false)
        {
            Encoding       = encoding;
            CharStrings    = charStrings;
            Subrs          = subrs;
            FontMatrixScale = fontMatrixScale;
            WidthScaleIsKnown = widthScaleIsKnown;
            CharStringsComplete = charStringsComplete;
        }

        /// <summary>
        /// The advance width of the glyph named <paramref name="glyphName"/> in PDF glyph space (1000
        /// units per text space unit): its <c>hsbw</c>/<c>sbw</c> width scaled by the font matrix.
        /// </summary>
        /// <returns>
        /// <c>false</c> when the program has no such glyph, <see cref="WidthScaleIsKnown"/> is false,
        /// or the charstring cannot be run or declares no width.
        /// </returns>
        public bool TryGetAdvance(string glyphName, out double advance)
        {
            advance = 0;
            if (!WidthScaleIsKnown || glyphName == null || CharStrings == null
                || !CharStrings.TryGetValue(glyphName, out byte[] charstring))
                return false;

            Type1CharstringInterpreter.Glyph glyph = Type1CharstringInterpreter.Run(charstring, Subrs);
            if (glyph == null || !glyph.HasWidth) return false;

            advance = glyph.Width * FontMatrixScale * 1000.0;
            return true;
        }
    }
}
