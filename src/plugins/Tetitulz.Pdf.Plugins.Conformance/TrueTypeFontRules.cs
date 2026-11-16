// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>Which standard's form of the TrueType <c>cmap</c> rule applies.</summary>
    public enum TrueTypeCmapRule
    {
        /// <summary>
        /// ISO 19005-1 6.3.7: a symbolic font's program has exactly one <c>cmap</c> entry; a
        /// non-symbolic font's is not judged.
        /// </summary>
        PdfA1 = 0,

        /// <summary>
        /// ISO 19005-2/3 6.2.11.6 and ISO 14289-1 7.21.6: a non-symbolic font's program has at least
        /// one entry, and more than one when (3,0) is among them; a symbolic font's exactly one, or
        /// a (3,0).
        /// </summary>
        Iso32000Part1 = 1,

        /// <summary>
        /// ISO 19005-4 6.2.10.6 and ISO 14289-2 8.4.5.7: a non-symbolic font's program has (3,1) or
        /// (1,0); a symbolic font's (3,0) or (1,0).
        /// </summary>
        Iso32000Part2 = 2,
    }

    /// <summary>
    /// The TrueType font rules' shared readings — PDF/UA-1's UA51 (ISO 14289-1 7.21.6), PDF/A's A80
    /// (ISO 19005-1 6.3.7, 19005-2/3 6.2.11.6, 19005-4 6.2.10.6) and PDF/UA-2's §8.4.5.7 checks in the
    /// Accessibility feature ask the same questions of a simple TrueType font: which encoding it
    /// declares, whether its <c>/Differences</c> names are glyph names, whether its descriptor states its
    /// flags, its embedded program, and whether that program's <c>cmap</c> has the subtables the
    /// standard asks for. Public because the Accessibility feature reads it, across the sanctioned
    /// edge, as it reads <see cref="PdfUaFontUse"/>.
    /// </summary>
    public static class TrueTypeFontRules
    {
        /// <summary>
        /// The encoding a font declares by name: its <c>/Encoding</c> name, or its encoding
        /// dictionary's <c>/BaseEncoding</c> — <see langword="null"/> for neither.
        /// </summary>
        public static string DeclaredEncodingName(PdfDocument doc, PdfDictionary font)
        {
            var encoding = doc.ResolveObject(font?[PdfName.Encoding]);
            return (encoding as PdfName)?.Value
                ?? (doc.ResolveObject((encoding as PdfDictionary)?[PdfName.BaseEncoding]) as PdfName)?.Value;
        }

        /// <summary>Whether a declared encoding is one the rules admit for a non-symbolic font: MacRoman or WinAnsi.</summary>
        public static bool IsMacRomanOrWinAnsi(string encodingName)
            => encodingName == "MacRomanEncoding" || encodingName == "WinAnsiEncoding";

        /// <summary>Whether the font's encoding dictionary holds a <c>/Differences</c> array naming at least one glyph.</summary>
        public static bool HasDifferences(PdfDocument doc, PdfDictionary font)
        {
            if (!(doc.ResolveObject((doc.ResolveObject(font?[PdfName.Encoding]) as PdfDictionary)?[PdfName.Differences]) is PdfArray differences))
                return false;
            for (int i = 0; i < differences.Count; i++)
                if (doc.ResolveObject(differences[i]) is PdfName) return true;
            return false;
        }

        /// <summary>
        /// The first name the font's <c>/Differences</c> array holds that the Adobe Glyph List does not
        /// know (<see cref="PdfGlyphNames.TryGetUnicode"/>, its <c>uniXXXX</c> forms included), or
        /// <see langword="null"/>.
        /// </summary>
        public static string FirstDifferencesNameOutsideGlyphList(PdfDocument doc, PdfDictionary font)
        {
            if (!(doc.ResolveObject((doc.ResolveObject(font?[PdfName.Encoding]) as PdfDictionary)?[PdfName.Differences]) is PdfArray differences))
                return null;
            for (int i = 0; i < differences.Count; i++)
                if (doc.ResolveObject(differences[i]) is PdfName name && !PdfGlyphNames.TryGetUnicode(name.Value, out _))
                    return name.Value;
            return null;
        }

        /// <summary>
        /// Whether the font's descriptor states its <c>/Flags</c>: with none, "not symbolic" is only a
        /// default, and a rule for non-symbolic fonts is not applied on a default the file never stated.
        /// </summary>
        public static bool DeclaresFlags(PdfDocument doc, PdfDictionary font)
            => doc.ResolveObject((doc.ResolveObject(font?[PdfName.FontDescriptor]) as PdfDictionary)?[PdfName.Flags]) is PdfNumber;

        /// <summary>
        /// The <c>/FontFile2</c> program under the font's (or CIDFont's) descriptor, parsed — or
        /// <see langword="null"/> when there is none or it does not parse, <paramref name="error"/>
        /// then saying why where reading threw.
        /// </summary>
        public static TrueTypeFontProgram EmbeddedProgram(PdfDocument doc, PdfDictionary fontOrCidFont, out Exception error)
        {
            error = null;
            var descriptor = doc.ResolveObject(fontOrCidFont?[PdfName.FontDescriptor]) as PdfDictionary;
            if (!(doc.ResolveObject(descriptor?[PdfName.FontFile2]) is PdfStream fontFile)) return null;
            try
            {
                return TrueTypeFontProgram.Parse(Core.Fonts.FontStreamBytes.Program(fontFile));
            }
            catch (Exception ex)
            {
                error = ex;
                return null;
            }
        }

        /// <summary>
        /// What PDF/UA-1's 7.21.6 or a PDF/A part's TrueType clause finds wrong with a simple TrueType
        /// font that states its flags — the first finding, or <see langword="null"/>: a symbolic font
        /// with an <c>/Encoding</c>; a non-symbolic one whose declared encoding is not MacRoman or
        /// WinAnsi, which has <c>/Differences</c> under PDF/A-1, or whose <c>/Differences</c> name a glyph
        /// the Adobe Glyph List does not know; an embedded program whose <c>cmap</c> is not what
        /// <paramref name="rule"/> asks.
        /// </summary>
        internal static string WhatIsWrong(PdfDocument doc, PdfDictionary font, TrueTypeCmapRule rule)
        {
            if ((doc.ResolveObject(font?[PdfName.Subtype]) as PdfName)?.Value != "TrueType" || !DeclaresFlags(doc, font)) return null;
            bool symbolic = FontFlags.IsSymbolic(font, doc);
            if (symbolic)
            {
                if (font[PdfName.Encoding] != null) return "a symbolic font with an /Encoding";
            }
            else
            {
                string named = DeclaredEncodingName(doc, font);
                if (!IsMacRomanOrWinAnsi(named))
                    return named == null ? "a non-symbolic font declaring neither MacRoman nor WinAnsi" : $"a non-symbolic font declaring /{named}";
                if (rule == TrueTypeCmapRule.PdfA1 && HasDifferences(doc, font)) return "a non-symbolic font with /Differences";
                string outside = FirstDifferencesNameOutsideGlyphList(doc, font);
                if (outside != null) return $"/Differences names /{outside}, which the Adobe Glyph List does not know";
            }
            if (!CmapConforms(EmbeddedProgram(doc, font, out _), symbolic, rule))
                return $"its embedded program's cmap subtables are not those asked of a {(symbolic ? "symbolic" : "non-symbolic")} font";
            return null;
        }

        /// <summary>
        /// Whether a composite font's <c>CIDFontType2</c> descendant with an embedded program names its
        /// <c>/CIDToGIDMap</c> as <c>/Identity</c> or a stream (ISO 32000-1 Table 117; ISO 14289-1 7.21.3.2,
        /// ISO 19005-1 6.3.3.2, 19005-2/3 6.2.11.3.2, 19005-4 6.2.10.3.2). Any other font conforms.
        /// </summary>
        internal static bool CidToGidMapConforms(PdfDocument doc, PdfDictionary font)
        {
            var cidFont = font;
            if ((doc.ResolveObject(font?[PdfName.Subtype]) as PdfName)?.Value == "Type0")
                cidFont = (doc.ResolveObject(font[PdfName.DescendantFonts]) as PdfArray) is PdfArray descendants && descendants.Count > 0
                    ? doc.ResolveObject(descendants[0]) as PdfDictionary : null;
            if ((doc.ResolveObject(cidFont?[PdfName.Subtype]) as PdfName)?.Value != "CIDFontType2") return true;
            var descriptor = doc.ResolveObject(cidFont[PdfName.FontDescriptor]) as PdfDictionary;
            if (!(doc.ResolveObject(descriptor?[PdfName.FontFile2]) is PdfStream)) return true;
            var map = doc.ResolveObject(cidFont[PdfName.CIDToGIDMap]);
            return map is PdfStream || (map as PdfName)?.Value == "Identity";
        }

        /// <summary>Whether <paramref name="program"/>'s <c>cmap</c> has the subtables <paramref name="rule"/> asks of a font of this kind.</summary>
        public static bool CmapConforms(TrueTypeFontProgram program, bool symbolic, TrueTypeCmapRule rule)
        {
            if (program == null) return true;   // no program, nothing to judge: embedding is another rule's
            switch (rule)
            {
                case TrueTypeCmapRule.PdfA1:
                    return !symbolic || program.CmapSubtableCount == 1;
                case TrueTypeCmapRule.Iso32000Part1:
                    bool symbolSubtable = program.HasCmapSubtable(3, 0);
                    return symbolic
                        ? program.CmapSubtableCount == 1 || symbolSubtable
                        : symbolSubtable ? program.CmapSubtableCount > 1 : program.CmapSubtableCount > 0;
                default:
                    return symbolic
                        ? program.HasCmapSubtable(3, 0) || program.HasCmapSubtable(1, 0)
                        : program.HasCmapSubtable(3, 1) || program.HasCmapSubtable(1, 0);
            }
        }
    }
}
