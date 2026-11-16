// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// "Embedded fonts shall define all glyphs referenced for rendering" — ISO 19005-1 6.3.5, 19005-2/3
    /// 6.2.11.4.1, 19005-4 6.2.10.4.1, ISO 14289-1 7.21.4.1; PDF/A's A84 and PDF/UA-1's UA57 alike. The
    /// painted codes are <c>PdfUaFontUse.PaintedCodes</c>'s, each judged by
    /// <see cref="EmbeddedGlyphSets.GlyphPresence"/>; a code whose glyph the font does not say is not
    /// judged, as the profile's <c>isGlyphPresent == null</c> passes.
    /// </summary>
    internal static class FontGlyphPresence
    {
        /// <summary>How many of the codes a finding names.</summary>
        private const int NamedCodes = 8;

        /// <summary>
        /// The painted codes of <paramref name="font"/> whose glyph its embedded program does not define,
        /// as a finding, or <see langword="null"/>; <paramref name="unjudged"/> says why the font's codes
        /// were not judged at all. <paramref name="codes"/> is the font's entry in <c>PaintedCodes</c>'s
        /// answer: <see langword="null"/> for a composite font whose strings were not decoded.
        /// </summary>
        internal static string WhatIsMissing(PdfDocument doc, PdfDictionary font, HashSet<int> codes, out string unjudged)
        {
            unjudged = null;
            if (doc == null || font == null) return null;
            if (codes == null)
            {
                unjudged = "its strings were not decoded: its /Encoding CMap is not registered or does not read";
                return null;
            }
            if (codes.Count == 0) return null;
            var present = EmbeddedGlyphSets.GlyphPresence(font, doc.ResolveObject, out string unreadable);
            if (present == null)
            {
                if (unreadable != null) unjudged = "its program could not be read: " + unreadable;
                return null;
            }
            var missing = codes.Where(c => present(c) == false).OrderBy(c => c).ToList();
            if (missing.Count == 0) return null;
            bool composite = (doc.ResolveObject(font[PdfName.Subtype]) as PdfName)?.Value == "Type0";
            string listed = string.Join(", ", missing.Take(NamedCodes).Select(c => c.ToString(CultureInfo.InvariantCulture)));
            if (missing.Count > NamedCodes)
                listed += ", and " + (missing.Count - NamedCodes).ToString(CultureInfo.InvariantCulture) + " more";
            return (composite ? "CID(s) " : "code(s) ") + listed + " painted with no glyph in the embedded program";
        }
    }
}
