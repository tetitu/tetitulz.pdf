// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Standard page dimensions in PDF points.
    ///
    /// <para>Named so every site agrees: the rounded <c>595 &#215; 842</c> beside
    /// <c>595.28 &#215; 841.89</c> would let two pages nominally "A4" differ by nearly a point. The
    /// two-decimal spelling is authoritative here.</para>
    ///
    /// <para>The ISO sizes are metric, so their point values are irrational-ish and these
    /// constants are rounded to 0.01&#160;pt — the same rounding common producers write. Do not
    /// "correct" one of them to more digits: differing precision between call sites is the exact
    /// bug this type exists to stop.</para>
    /// </summary>
    public static class PdfPageSizes
    {
        /// <summary>US Letter width — 8.5&#160;in.</summary>
        public const float LetterWidth = 612f;
        /// <summary>US Letter height — 11&#160;in.</summary>
        public const float LetterHeight = 792f;

        /// <summary>US Legal width — 8.5&#160;in.</summary>
        public const float LegalWidth = 612f;
        /// <summary>US Legal height — 14&#160;in.</summary>
        public const float LegalHeight = 1008f;

        /// <summary>ISO A4 width — 210&#160;mm (595.2756&#160;pt, rounded).</summary>
        public const float A4Width = 595.28f;
        /// <summary>ISO A4 height — 297&#160;mm (841.8898&#160;pt, rounded).</summary>
        public const float A4Height = 841.89f;

        /// <summary>ISO A3 width — 297&#160;mm.</summary>
        public const float A3Width = 841.89f;
        /// <summary>ISO A3 height — 420&#160;mm.</summary>
        public const float A3Height = 1190.55f;

        /// <summary>ISO A5 width — 148&#160;mm.</summary>
        public const float A5Width = 419.53f;
        /// <summary>ISO A5 height — 210&#160;mm.</summary>
        public const float A5Height = 595.28f;

        /// <summary>
        /// The page size assumed when a page dictionary has no usable <c>/MediaBox</c>.
        /// US Letter, matching the historical behaviour of the renderer.
        /// </summary>
        public const float DefaultWidth = LetterWidth;

        /// <summary>Companion to <see cref="DefaultWidth"/>.</summary>
        public const float DefaultHeight = LetterHeight;
    }
}
