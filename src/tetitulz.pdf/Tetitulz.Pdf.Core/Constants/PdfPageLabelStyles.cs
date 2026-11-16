// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// The page-label numbering styles — the values of a label dictionary's <c>/S</c>
    /// (ISO 32000-1 §12.4.2, Table 159).
    /// </summary>
    /// <remarks>
    /// <para>One set for both directions: the facade formats labels with these as its case labels,
    /// and <c>Base.Operations.Structure.PdfPageLabels.SetLabels</c> refuses a style outside
    /// them.</para>
    ///
    /// <para><b>The alphabetic styles are Table 159's repeated letters</b> — a…z, then aa, bb, cc —
    /// not the bijective base-26 of <see cref="Util.OrdinalNumbering.ToLetters"/>; see that type's
    /// remarks.</para>
    /// </remarks>
    public static class PdfPageLabelStyles
    {
        /// <summary>Decimal arabic numerals.</summary>
        public const string Decimal = "D";

        /// <summary>Upper-case roman numerals.</summary>
        public const string UpperRoman = "R";

        /// <summary>Lower-case roman numerals.</summary>
        public const string LowerRoman = "r";

        /// <summary>Upper-case letters: A to Z, then AA to ZZ, and so on.</summary>
        public const string UpperAlpha = "A";

        /// <summary>Lower-case letters: a to z, then aa to zz, and so on.</summary>
        public const string LowerAlpha = "a";

        /// <summary>
        /// Whether <paramref name="style"/> is one of the five, matched case-sensitively — PDF names
        /// are (§7.3.5), and <c>R</c> and <c>r</c> are different styles.
        /// </summary>
        public static bool IsDefined(string style)
            => style == Decimal || style == UpperRoman || style == LowerRoman
               || style == UpperAlpha || style == LowerAlpha;
    }
}
