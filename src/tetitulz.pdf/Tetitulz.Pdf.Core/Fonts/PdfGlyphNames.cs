// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Resolves an Adobe glyph name to the Unicode code point it denotes — the lookup a
    /// <c>/Differences</c> encoding array needs.
    ///
    /// <para><b>Why this exists as its own public type.</b> The table lives in the internal
    /// <c>StandardEncodings</c>, which cannot be published as-is: its eight
    /// <c>Dictionary&lt;int, char&gt;</c> properties return the backing dictionaries directly, so
    /// making that class public would let any consumer corrupt the encoding tables process-wide.
    /// This exposes the one lookup an external font-building consumer actually needs — currently
    /// <c>Tetitulz.Pdf.Plugins.PostScript</c>, which builds <c>/Differences</c> arrays from a
    /// PostScript <c>/Encoding</c> and has to measure the result.</para>
    /// </summary>
    public static class PdfGlyphNames
    {
        /// <summary>
        /// The Unicode code point <paramref name="glyphName"/> denotes.
        /// </summary>
        /// <param name="glyphName">
        /// An Adobe glyph name — <c>"alpha"</c>, <c>"afii57668"</c>, <c>"eacute"</c> — or one of the
        /// AGL numeric conventions <c>uniXXXX</c> / <c>uXXXX</c>.
        /// </param>
        /// <param name="unicode">The code point, when the name is known.</param>
        /// <returns><c>false</c> for a name outside the table and the numeric conventions.</returns>
        public static bool TryGetUnicode(string glyphName, out char unicode)
            => StandardEncodings.TryGetGlyphUnicode(glyphName, out unicode);
    }
}
