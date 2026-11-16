// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// What a font's <b>name</b> says about its weight and slant — <c>Arial,Bold</c>,
    /// <c>TimesNewRomanPS-BoldItalicMT</c>, <c>Helvetica-Oblique</c>, <c>Arial-Black</c> — for a
    /// caller that has nothing better: no descriptor, or a descriptor that does not say.
    /// </summary>
    /// <remarks>
    /// <para><b>One reading, because six sites read it two ways.</b> <see cref="Standard14Names"/>
    /// (which standard-14 face, so which widths) and the SVG writer's face counted <c>Heavy</c> and
    /// <c>Black</c> as bold; the raster renderer's typeface lookup (three times) and the accessibility
    /// font remediator counted <c>Bold</c> alone. So <c>Arial,Black</c> was measured as
    /// Helvetica-Bold and drawn and embedded as regular Arial.</para>
    /// <para>A case-insensitive substring test, as every site had: producers spell the style in any
    /// case and at any position (<c>-BoldMT</c>, <c>,BoldItalic</c>, <c>SemiBold</c>).</para>
    /// </remarks>
    internal static class FontNameStyle
    {
        /// <summary>Whether <paramref name="name"/> says bold: <c>Bold</c>, <c>Heavy</c> or <c>Black</c>.</summary>
        public static bool IsBold(string name)
            => Says(name, "Bold") || Says(name, "Heavy") || Says(name, "Black");

        /// <summary>Whether <paramref name="name"/> says italic: <c>Italic</c> or <c>Oblique</c>.</summary>
        public static bool IsItalic(string name)
            => Says(name, "Italic") || Says(name, "Oblique");

        private static bool Says(string name, string token)
            => name != null && name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
