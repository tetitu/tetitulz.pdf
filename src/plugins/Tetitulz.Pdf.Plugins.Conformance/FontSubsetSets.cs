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
    /// <summary>Which reading of a subset's <c>/CharSet</c> and <c>/CIDSet</c> a standard takes.</summary>
    internal enum SubsetSetRule
    {
        /// <summary>
        /// ISO 19005-1 6.3.5: a Type 1 subset has a <c>/CharSet</c> and a CIDFont subset a <c>/CIDSet</c>
        /// identifying the CIDs its program holds. The CharSet lists the glyphs used for rendering —
        /// test 6-3-5-t02-pass-a lists extra names, -pass-b leaves an unused one out — so every glyph
        /// a painted code selects is in it, and nothing else is asked; a code whose glyph the font does not
        /// name (its program's encoding unread) is not judged.
        /// </summary>
        PdfA1,

        /// <summary>
        /// ISO 19005-2/3 6.2.11.4.2 and ISO 14289-1 7.21.4.2, from ISO 32000-1 Tables 122 and 124: a
        /// <c>/CharSet</c>, where present, lists exactly the glyphs the program defines ("regardless of
        /// whether a glyph in the font is referenced or used"; test 7.21.4.2-t01-fail-b fails a name
        /// the program lacks); a <c>/CIDSet</c>, where present, identifies every CID the program holds.
        /// </summary>
        Iso32000Part1,
    }

    /// <summary>
    /// A subset font's <c>/CharSet</c> or <c>/CIDSet</c> against its embedded program — PDF/A's A83 and
    /// PDF/UA-1's UA56 alike, over <see cref="EmbeddedGlyphSets"/>. A subset is a font whose name has
    /// the six-capital tag (ISO 32000-1 9.6.4: <c>EOODIA+Poetica</c>); <c>.notdef</c> and CID 0 need not
    /// be listed, and are not counted when they are. PDF/A-1 asks for the sets of every subset, embedded
    /// or not (rule 6.3.5-2 and -3 do not ask whether a program is there); a comparison needs the
    /// program, so a subset with none is compared with nothing.
    /// </summary>
    internal static class FontSubsetSets
    {
        /// <summary>How many of the missing or extra entries a finding names.</summary>
        private const int NamedEntries = 8;

        /// <summary>Whether a font name carries a subset tag: six capital letters A–Z, then <c>+</c>.</summary>
        internal static bool IsSubsetName(string fontName)
        {
            if (fontName == null || fontName.Length < 7 || fontName[6] != '+') return false;
            for (int i = 0; i < 6; i++)
                if (fontName[i] < 'A' || fontName[i] > 'Z') return false;
            return true;
        }

        /// <summary>
        /// What is wrong with a simple Type 1 or composite font's subset set under <paramref name="rule"/>,
        /// or <see langword="null"/>; <paramref name="unjudged"/> names a set that was there and could not
        /// be compared because the program could not be read. <paramref name="painted"/> is
        /// <c>PdfUaFontUse.PaintedCodes</c>'s answer, which <see cref="SubsetSetRule.PdfA1"/>
        /// reads a CharSet against; <see langword="null"/> there leaves the contents unjudged.
        /// </summary>
        internal static string WhatIsWrong(PdfDocument doc, PdfDictionary font, SubsetSetRule rule, out string unjudged,
            IReadOnlyDictionary<PdfDictionary, HashSet<int>> painted = null)
        {
            unjudged = null;
            if (doc == null || font == null) return null;
            System.Func<PdfObject, PdfObject> resolve = doc.ResolveObject;
            string subtype = (resolve(font[PdfName.Subtype]) as PdfName)?.Value;

            if (subtype == "Type1" || subtype == "MMType1")
            {
                if (!IsSubsetName((resolve(font[PdfName.BaseFont]) as PdfName)?.Value)) return null;
                var descriptor = resolve(font[PdfName.FontDescriptor]) as PdfDictionary;
                if (descriptor == null) return null;
                if (!(resolve(descriptor[PdfName.CharSet]) is PdfString charSet))
                    return rule == SubsetSetRule.PdfA1 ? "a Type 1 font subset with no /CharSet" : null;
                if (rule == SubsetSetRule.PdfA1) return GlyphsUsedButNotListed(doc, font, charSet, painted, out unjudged);

                ISet<string> defined = EmbeddedGlyphSets.GlyphNames(descriptor, resolve, out string unreadable);
                if (defined == null)
                {
                    if (unreadable != null) unjudged = "its /CharSet was not compared: " + unreadable;
                    return null;
                }
                ISet<string> listed = EmbeddedGlyphSets.CharSetNames(charSet);
                var missing = defined.Where(n => n != EmbeddedGlyphSets.NotDefGlyph && !listed.Contains(n)).OrderBy(n => n, System.StringComparer.Ordinal).ToList();
                var extra = listed.Where(n => n != EmbeddedGlyphSets.NotDefGlyph && !defined.Contains(n)).OrderBy(n => n, System.StringComparer.Ordinal).ToList();
                var problems = new List<string>(2);
                if (missing.Count > 0) problems.Add("its /CharSet omits glyphs the program defines (" + Named(missing.Select(n => "/" + n)) + ")");
                if (extra.Count > 0) problems.Add("its /CharSet lists glyphs the program does not define (" + Named(extra.Select(n => "/" + n)) + ")");
                return problems.Count == 0 ? null : string.Join("; ", problems);
            }

            if (subtype == "Type0")
            {
                var cidFont = resolve(font[PdfName.DescendantFonts]) is PdfArray descendants && descendants.Count > 0
                    ? resolve(descendants[0]) as PdfDictionary : null;
                if (cidFont == null || !IsSubsetName((resolve(cidFont[PdfName.BaseFont]) as PdfName)?.Value)) return null;
                var descriptor = resolve(cidFont[PdfName.FontDescriptor]) as PdfDictionary;
                if (descriptor == null) return null;
                if (!(resolve(descriptor[PdfName.CIDSet]) is PdfStream cidSet))
                    return rule == SubsetSetRule.PdfA1 ? "a CIDFont subset with no /CIDSet" : null;

                ISet<int> held = EmbeddedGlyphSets.Cids(cidFont, resolve, out string unreadable);
                if (held == null)
                {
                    if (unreadable != null) unjudged = "its /CIDSet was not compared: " + unreadable;
                    return null;
                }
                ISet<int> identified = EmbeddedGlyphSets.CidSetCids(cidSet);
                var missing = held.Where(c => c != 0 && !identified.Contains(c)).OrderBy(c => c).ToList();
                return missing.Count == 0 ? null
                    : "its /CIDSet does not identify CIDs the program holds (" +
                      Named(missing.Select(c => c.ToString(CultureInfo.InvariantCulture))) + ")";
            }

            return null;
        }

        /// <summary>
        /// PDF/A-1's reading of a CharSet: every glyph a painted code selects is listed.
        /// </summary>
        private static string GlyphsUsedButNotListed(PdfDocument doc, PdfDictionary font, PdfString charSet,
            IReadOnlyDictionary<PdfDictionary, HashSet<int>> painted, out string unjudged)
        {
            unjudged = null;
            if (painted == null)
            {
                unjudged = "its /CharSet's contents were not compared: the glyphs used for rendering could not be established";
                return null;
            }
            if (!painted.TryGetValue(font, out HashSet<int> codes) || codes == null || codes.Count == 0) return null;
            IReadOnlyDictionary<int, string> names = EmbeddedGlyphSets.GlyphNamesByCode(font, doc.ResolveObject, out string unreadable);
            if (names == null)
            {
                unjudged = "its /CharSet's contents were not compared: " + unreadable;
                return null;
            }
            ISet<string> listed = EmbeddedGlyphSets.CharSetNames(charSet);
            var missing = codes.Where(c => names.TryGetValue(c, out string n) && n != EmbeddedGlyphSets.NotDefGlyph && !listed.Contains(n))
                .Select(c => names[c]).Distinct().OrderBy(n => n, System.StringComparer.Ordinal).ToList();
            return missing.Count == 0 ? null
                : "its /CharSet omits glyphs used for rendering (" + Named(missing.Select(n => "/" + n)) + ")";
        }

        private static string Named(IEnumerable<string> entries)
        {
            var list = entries.ToList();
            string shown = string.Join(", ", list.Take(NamedEntries));
            return list.Count > NamedEntries
                ? shown + ", and " + (list.Count - NamedEntries).ToString(CultureInfo.InvariantCulture) + " more"
                : shown;
        }
    }
}
