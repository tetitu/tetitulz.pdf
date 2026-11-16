// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// A80 — every TrueType font used for rendering that states its flags, under the part's clause
        /// (ISO 19005-1 6.3.7, 19005-2/3 6.2.11.6, 19005-4 6.2.10.6; the validation rules of those numbers):
        /// a symbolic font has no <c>/Encoding</c>; a non-symbolic one declares MacRoman or WinAnsi, has
        /// no <c>/Differences</c> in PDF/A-1 and only glyph-list names after; the embedded program's
        /// <c>cmap</c> is the part's — <see cref="TrueTypeFontRules.WhatIsWrong"/>, the reading PDF/UA-1's
        /// UA51 and PDF/UA-2's §8.4.5.7 checks share. A81 — every part: an embedded CIDFontType2's
        /// <c>/CIDToGIDMap</c> is <c>/Identity</c> or a stream (19005-1 6.3.3.2, 19005-2/3 6.2.11.3.2,
        /// 19005-4 6.2.10.3.2). A82 — levels A and U of PDF/A-2 and -3 (6.2.11.7.2) and all of PDF/A-4
        /// (6.2.10.7): no <c>/ToUnicode</c> value is U+0000, U+FEFF or U+FFFE. The readings PDF/UA-1's
        /// UA54 and UA55 share. A83 — PDF/A-1 to -3: a subset's <c>/CharSet</c> and <c>/CIDSet</c>
        /// against its program (19005-1 6.3.5, 19005-2/3 6.2.11.4.2) — <see cref="FontSubsetSets"/>,
        /// the reading PDF/UA-1's UA56 shares; ISO 19005-4 has no such clause.
        /// </summary>
        private static void CheckFontDictionaries(PdfDocument doc, PdfAConformancePart part, string level, List<PdfAViolation> v)
        {
            bool valuesJudged = part == PdfAConformancePart.Part4
                || ((part == PdfAConformancePart.Part2 || part == PdfAConformancePart.Part3) && (level == "A" || level == "U"));
            var cidToGid = new Occurrences();
            var values = new Occurrences();
            var subsetSets = new Occurrences();
            SubsetSetRule? subsetRule = part == PdfAConformancePart.Part1 ? SubsetSetRule.PdfA1
                : part == PdfAConformancePart.Part4 ? (SubsetSetRule?)null : SubsetSetRule.Iso32000Part1;
            if (doc?.Pages == null) return;
            var rule = part == PdfAConformancePart.Part1 ? TrueTypeCmapRule.PdfA1
                : part == PdfAConformancePart.Part4 ? TrueTypeCmapRule.Iso32000Part2 : TrueTypeCmapRule.Iso32000Part1;
            HashSet<PdfDictionary> painted = null;
            bool paintedRead = false;
            // The codes each font paints: A84 judges their glyphs, PDF/A-1's A83 its CharSet against them.
            // Read once, when the first font with a glyph program asks; the painted fonts are its keys then.
            Dictionary<PdfDictionary, HashSet<int>> paintedCodes = null;
            bool paintedCodesRead = false;
            var glyphs = new Occurrences();
            var seen = new HashSet<PdfDictionary>();
            var seenResources = new HashSet<PdfDictionary>();
            var problems = new Occurrences();
            long documentBytes = 0;
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A80-A84 font checks did not run in full — {scan.Incomplete}.", "A80", "A81", "A82", "A83", "A84");
                foreach (var resources in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, resources)) continue;
                    if (!(doc.ResolveObject(resources[PdfName.Font]) is PdfDictionary fonts)) continue;
                    foreach (var kvp in fonts)
                    {
                        if (!(doc.ResolveObject(kvp.Value) is PdfDictionary font) || !seen.Add(font)) continue;
                        string problem = TrueTypeFontRules.WhatIsWrong(doc, font, rule);
                        bool cidProblem = !TrueTypeFontRules.CidToGidMapConforms(doc, font);
                        string valueProblem = valuesJudged ? FontUnicodeMapping.ForbiddenToUnicodeValue(doc, font) : null;
                        string subsetUnjudged = null;
                        if (!paintedCodesRead && (HasGlyphProgram(doc, font)
                            || (doc.ResolveObject(font[PdfName.FontDescriptor]) as PdfDictionary)?[PdfName.CharSet] != null))
                        {
                            paintedCodesRead = true;
                            paintedCodes = PdfUaFontUse.PaintedCodes(doc,
                                PdfXValidator.DefaultMaxPageContentBytes, PdfXValidator.DefaultMaxDocumentContentBytes);
                        }
                        string subsetProblem = subsetRule == null ? null
                            : FontSubsetSets.WhatIsWrong(doc, font, subsetRule.Value, out subsetUnjudged, paintedCodes);
                        string glyphUnjudged = null;
                        string glyphProblem = null;
                        if (paintedCodes != null && paintedCodes.TryGetValue(font, out HashSet<int> fontCodes))
                            glyphProblem = FontGlyphPresence.WhatIsMissing(doc, font, fontCodes, out glyphUnjudged);
                        else if (paintedCodesRead && paintedCodes == null && HasGlyphProgram(doc, font))
                            Report("A84", "the glyphs used for rendering could not be established" +
                                (PdfUaFontUse.LastError != null ? " (" + PdfUaFontUse.LastError + ")" : ""));
                        if (problem == null && !cidProblem && valueProblem == null && subsetProblem == null && subsetUnjudged == null
                            && glyphProblem == null && glyphUnjudged == null) continue;
                        if (!paintedRead)
                        {
                            paintedRead = true;
                            painted = paintedCodesRead
                                ? (paintedCodes == null ? null : new HashSet<PdfDictionary>(paintedCodes.Keys))
                                : PdfUaFontUse.PaintedFonts(doc,
                                    PdfXValidator.DefaultMaxPageContentBytes, PdfXValidator.DefaultMaxDocumentContentBytes);
                        }
                        if (painted != null && !painted.Contains(font)) continue;
                        string label = (doc.ResolveObject(font[PdfName.BaseFont]) as PdfName)?.Value ?? "/" + kvp.Key.Value;
                        if (problem != null) problems.Add(label + ": " + problem);
                        if (cidProblem) cidToGid.Add(label);
                        if (valueProblem != null) values.Add(label + ": " + valueProblem);
                        if (subsetProblem != null) subsetSets.Add(label + ": " + subsetProblem);
                        // A font this library cannot read is not judged, as the profiles' null passes, and is
                        // said in LastValidationError — not as an A99, which would refuse PdfAConverter a
                        // file the profiles accept for a limit of this library's font readers.
                        // One slot: A84's goes first, so a font both checks could not read leaves A83's, the
                        // narrower statement, in it.
                        if (glyphUnjudged != null) Report("A84", $"{label}: {glyphUnjudged}");
                        if (subsetUnjudged != null) Report("A83", $"{label}: {subsetUnjudged}");
                        if (glyphProblem != null) glyphs.Add(label + ": " + glyphProblem);
                    }
                }
            }

            if (problems.Count > 0)
                v.Add(new PdfAViolation("A80",
                    $"A TrueType font used for rendering breaks the part's TrueType rules: {problems} (" +
                    (part == PdfAConformancePart.Part1 ? "ISO 19005-1 6.3.7; rules 6.3.7-1 to -3"
                        : part == PdfAConformancePart.Part4 ? "ISO 19005-4 6.2.10.6; rules 6.2.10.6-1 to -4"
                        : $"ISO 19005-{((int)part).ToString(CultureInfo.InvariantCulture)} 6.2.11.6; rules 6.2.11.6-1 to -4") + ")."));
            if (cidToGid.Count > 0)
                v.Add(new PdfAViolation("A81",
                    $"An embedded CIDFontType2's /CIDToGIDMap is neither /Identity nor a stream: {cidToGid} (" +
                    (part == PdfAConformancePart.Part1 ? "ISO 19005-1 6.3.3.2; rule 6.3.3.2-1"
                        : part == PdfAConformancePart.Part4 ? "ISO 19005-4 6.2.10.3.2; rule 6.2.10.3.2-1"
                        : $"ISO 19005-{((int)part).ToString(CultureInfo.InvariantCulture)} 6.2.11.3.2; rule 6.2.11.3.2-1") + ")."));
            if (values.Count > 0)
                v.Add(new PdfAViolation("A82",
                    $"A /ToUnicode CMap maps a code to U+0000, U+FEFF or U+FFFE: {values} (" +
                    (part == PdfAConformancePart.Part4 ? "ISO 19005-4 6.2.10.7; rule 6.2.10.7-1"
                        : $"ISO 19005-{((int)part).ToString(CultureInfo.InvariantCulture)} 6.2.11.7.2; rule 6.2.11.7.2-2") + ")."));
            if (glyphs.Count > 0)
                v.Add(new PdfAViolation("A84",
                    $"A font used for rendering paints a glyph its embedded program does not define: {glyphs} (" +
                    (part == PdfAConformancePart.Part1 ? "ISO 19005-1 6.3.5; rule 6.3.5-1"
                        : part == PdfAConformancePart.Part4 ? "ISO 19005-4 6.2.10.4.1; rule 6.2.10.4.1-2"
                        : $"ISO 19005-{((int)part).ToString(CultureInfo.InvariantCulture)} 6.2.11.4.1; rule 6.2.11.4.1-2") + ")."));
            if (subsetSets.Count > 0)
                v.Add(new PdfAViolation("A83",
                    $"A font subset's /CharSet or /CIDSet does not match its program: {subsetSets} (" +
                    (part == PdfAConformancePart.Part1 ? "ISO 19005-1 6.3.5; rules 6.3.5-2/-3"
                        : $"ISO 19005-{((int)part).ToString(CultureInfo.InvariantCulture)} 6.2.11.4.2; rules 6.2.11.4.2-1/-2") + ")."));
        }

        /// <summary>
        /// Whether <paramref name="font"/> carries glyphs of its own — a Type 3 font's procedures, or an
        /// embedded program under its descriptor or its descendant's: what the painted codes are read for.
        /// </summary>
        private static bool HasGlyphProgram(PdfDocument doc, PdfDictionary font)
        {
            string subtype = (doc.ResolveObject(font[PdfName.Subtype]) as PdfName)?.Value;
            if (subtype == "Type3") return true;
            PdfDictionary owner = subtype == "Type0" && doc.ResolveObject(font[PdfName.DescendantFonts]) is PdfArray descendants && descendants.Count > 0
                ? doc.ResolveObject(descendants[0]) as PdfDictionary : font;
            var descriptor = doc.ResolveObject(owner?[PdfName.FontDescriptor]) as PdfDictionary;
            return descriptor != null
                && (descriptor[PdfName.FontFile] != null || descriptor[PdfName.FontFile2] != null || descriptor[PdfName.FontFile3] != null);
        }
    }
}
