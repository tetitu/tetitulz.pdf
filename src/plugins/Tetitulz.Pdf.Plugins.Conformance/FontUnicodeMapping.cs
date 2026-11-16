// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The one answer to "does this font map its character codes to Unicode?" — ISO 14289-1 7.21.7
    /// (PDF/UA's UA26) and ISO 19005-1 6.3.8, 19005-2/3 6.2.11.7.2 (PDF/A level A and U, A77) ask the
    /// same question in the same words: a <c>/ToUnicode</c> entry, or a mechanism the font itself
    /// carries — a standard encoding whose glyph names are all in the Adobe Glyph List, a
    /// <c>/Differences</c> array, a non-symbolic font's built-in encoding, a Type 0 font over a published
    /// Adobe character collection.
    /// </summary>
    /// <remarks>
    /// <para><b>A Type 3 font's <c>/Differences</c> names must be glyph names.</b> They are the keys of
    /// its own <c>/CharProcs</c>, chosen by the producer (<c>/g1</c>, <c>/a0</c>), and say nothing about
    /// the text unless the Adobe Glyph List (or its <c>uniXXXX</c> conventions) knows them — test file
    /// 6-2-11-7-2-t01-fail-c. A Type 1 or TrueType font's names select glyphs in a program that names
    /// them, and are taken as they were before.</para>
    /// <para>The answer is per font, not per code a page draws: the check is the font's. Public
    /// because the Accessibility feature's deep checker (UA20) reads the published-collection
    /// exemption, across the sanctioned edge, as it reads <see cref="PdfUaFontUse"/>.</para>
    /// </remarks>
    public static class FontUnicodeMapping
    {
        private static readonly HashSet<string> UnicodeCapableEncodings =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // ISO 14289-1 7.21.7 names MacRomanEncoding, MacExpertEncoding and WinAnsiEncoding; the
                // other two are this checker's, StandardEncoding's glyph names all being in the Adobe
                // Glyph List (the clause's second condition).
                "WinAnsiEncoding", "MacRomanEncoding", "MacExpertEncoding", "StandardEncoding", "PDFDocEncoding",
            };

        private static readonly HashSet<string> PublishedCharacterCollections =
            new HashSet<string>(StringComparer.Ordinal) { "GB1", "CNS1", "Japan1", "Korea1" };

        /// <summary>Whether <paramref name="font"/>'s codes map to Unicode.</summary>
        public static bool Maps(PdfDocument doc, PdfDictionary font)
        {
            // An explicit /ToUnicode CMap always settles the question.
            if (font[PdfName.ToUnicode] != null) return true;

            string subtype = (doc.ResolveObject(font[PdfName.Subtype]) as PdfName)?.Value;
            var encodingObj = font[PdfName.Encoding];

            if (subtype == "Type0")
            {
                // ISO 14289-1 7.21.7 exempts "Type 0 fonts whose descendant CIDFont uses the
                // Adobe-GB1, Adobe-CNS1, Adobe-Japan1 or Adobe-Korea1 character collections" — the
                // collection, whatever the CMap: Adobe publishes those collections' Unicode mappings,
                // so a reader recovers the text from the CIDs. Any other collection, Adobe-Identity
                // above all, maps to glyphs and is meaningless without /ToUnicode, the classic "text
                // extracts as gibberish" failure. the conformance corpus passes an Identity-H font over
                // Adobe-Japan1 with no /ToUnicode (7.21.7-t01-pass-a).
                return UsesPublishedCharacterCollection(doc, font);
            }

            if (doc.ResolveObject(encodingObj) is PdfName encodingName)
                return UnicodeCapableEncodings.Contains(encodingName.Value);

            if (doc.ResolveObject(encodingObj) is PdfDictionary encodingDict)
            {
                var baseEnc = doc.ResolveObject(encodingDict[PdfName.BaseEncoding]) as PdfName;
                if (baseEnc != null && UnicodeCapableEncodings.Contains(baseEnc.Value)) return true;
                // /Differences names glyphs (e.g. /aacute), which the Adobe Glyph List maps to Unicode,
                // so an encoding dictionary carrying differences is recoverable — for a Type 3 font
                // only when the glyph list knows its names (the remarks).
                if (!(doc.ResolveObject(encodingDict[PdfName.Differences]) is PdfArray differences)) return false;
                return subtype != "Type3" || DifferencesNamesAreGlyphNames(doc, differences);
            }

            // No /Encoding at all: the font's built-in encoding applies. That is Unicode-recoverable
            // for a non-symbolic font (it defaults to StandardEncoding) but not for a symbolic one,
            // whose built-in encoding is private to the font program — nor for a Type 3 font, whose
            // encoding §9.6.5 requires.
            return subtype != "Type3" && !FontFlags.IsSymbolic(font, doc);
        }

        /// <summary>
        /// What is wrong with the values of <paramref name="font"/>'s <c>/ToUnicode</c> CMap — a code
        /// mapped to U+0000, U+FEFF or U+FFFE (ISO 14289-1 7.21.7, ISO 19005-2/3 6.2.11.7.2, ISO 19005-4
        /// 6.2.10.7; <see cref="ToUnicodeValues"/>) — or <see langword="null"/>, a CMap that will not
        /// decode included.
        /// </summary>
        internal static string ForbiddenToUnicodeValue(PdfDocument doc, PdfDictionary font)
        {
            if (!(doc.ResolveObject(font?[PdfName.ToUnicode]) is PdfStream cmap)) return null;
            byte[] data;
            try
            {
                data = Core.Fonts.FontStreamBytes.CMap(cmap);
            }
            catch (Exception)
            {
                return null;
            }
            return ToUnicodeValues.TryFindForbidden(data, out int code, out char value)
                ? $"code {code.ToString("X", System.Globalization.CultureInfo.InvariantCulture)} maps to U+{((int)value).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)}"
                : null;
        }

        /// <summary>Whether every name a <c>/Differences</c> array holds is one the Adobe Glyph List knows.</summary>
        private static bool DifferencesNamesAreGlyphNames(PdfDocument doc, PdfArray differences)
        {
            for (int i = 0; i < differences.Count; i++)
                if (doc.ResolveObject(differences[i]) is PdfName name && !PdfGlyphNames.TryGetUnicode(name.Value, out _))
                    return false;
            return true;
        }

        /// <summary>
        /// Whether a Type 0 font's descendant names an Adobe collection ISO 14289-1 7.21.7 exempts —
        /// Adobe-GB1, -CNS1, -Japan1 or -Korea1, whatever the CMap: Adobe publishes their Unicode mappings.
        /// </summary>
        public static bool UsesPublishedCharacterCollection(PdfDocument doc, PdfDictionary font)
        {
            var descendants = doc.ResolveObject(font[PdfName.DescendantFonts]) as PdfArray;
            var cidFont = descendants != null && descendants.Count > 0 ? doc.ResolveObject(descendants[0]) as PdfDictionary : null;
            var info = doc.ResolveObject(cidFont?[PdfName.CIDSystemInfo]) as PdfDictionary;
            if (info == null) return false;
            return (doc.ResolveObject(info[PdfName.Registry]) as PdfString)?.GetText() == "Adobe"
                && PublishedCharacterCollections.Contains((doc.ResolveObject(info[PdfName.Ordering]) as PdfString)?.GetText() ?? "");
        }
    }
}
