// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Simple-font plan: widths and encoding
        // ================================================================

        /// <summary>
        /// What has to change on a simple font's dictionary so that embedding a program does not
        /// move a single glyph.
        /// </summary>
        private sealed class SimpleFontPlan
        {
            internal PdfArray WidthsToWrite;       // null = the font already has /Widths, leave it
            internal int      FirstChar;
            internal int      LastChar;
            /// <summary>
            /// Make the font's <b>implicit</b> encoding explicit as <c>/StandardEncoding</c>.
            /// </summary>
            /// <remarks>
            /// Not <c>/WinAnsiEncoding</c>, which is a different table across the whole upper half:
            /// 0xFB is germandbls in StandardEncoding and ucircumflex in WinAnsi, 0xE1 is AE against
            /// aacute, 0xD0 is emdash against eth. A German invoice drawing <c>(Stra\373e) Tj</c>
            /// against an unencoded /Helvetica would come out of remediation reading "Straûe", with
            /// the synthesised /Widths carrying ucircumflex's advance to match. Writing the encoding the document already implied leaves both glyph
            /// identity and advance widths untouched, which is the whole contract of this pass.
            /// </remarks>
            internal bool     WriteStandardEncoding;
            /// <summary>
            /// Make the implicit base of an encoding dictionary explicit as <c>/BaseEncoding
            /// /StandardEncoding</c>.
            /// </summary>
            /// <remarks>
            /// ISO 32000-1 §9.6.6.1 (Table 114, <c>BaseEncoding</c>): with the entry absent,
            /// <c>/Differences</c> describe differences from an implicit base — StandardEncoding for a
            /// non-symbolic font that is not embedded, but the program's built-in encoding for one that
            /// is. Embedding therefore changed the glyph every code <c>/Differences</c> does not name
            /// selects, exactly the shift <see cref="WriteStandardEncoding"/> exists to prevent for a
            /// font with no <c>/Encoding</c> at all.
            /// </remarks>
            internal bool     WriteBaseEncoding;
            internal string   SkipReason;
            internal Dictionary<int, char> Encoding;   // effective code → Unicode, for subsetting
        }

        private static SimpleFontPlan PlanSimpleFont(PdfDocument doc, FontSite site, TrueTypeMetrics metrics)
        {
            var plan = new SimpleFontPlan();

            bool hasEncodingKey = site.FontDict[PdfName.Encoding] != null;
            plan.Encoding = ResolveEffectiveEncoding(doc, site, out bool encodingIsKnown);

            // Before the /Widths early return: the implicit base changes on embedding whether or not
            // the font states its widths. A symbolic font's implicit base is its program's already.
            plan.WriteBaseEncoding = !site.IsSymbolic
                && doc.ResolveObject(site.FontDict[PdfName.Encoding]) is PdfDictionary encodingDictionary
                && encodingDictionary[PdfName.BaseEncoding] == null;

            // The document's own advance widths are authoritative and are left exactly as they are:
            // they are what the content stream was laid out against. Nothing else needs changing on
            // a font that already has them, so its /Encoding is left alone too.
            if (ResolveArray(doc, site.FontDict[PdfName.Widths]) != null) return plan;

            // No /Widths. ISO 32000-1 §9.6.2.2 allows that only for the Standard 14, whose built-in
            // AFM metrics the viewer supplies — and only for as long as the font is NOT embedded.
            // Once a program is attached the built-ins no longer apply, so the array has to be
            // synthesised from the ORIGINAL Standard-14 metrics (never the substitute's) or every
            // text position in the document shifts.
            if (site.Standard14Name == null)
                return Fail(plan,
                    "the font dictionary has no /Widths and its /BaseFont (" +
                    (site.BaseFont ?? "none") + ") is not one of the Standard 14, so the advance " +
                    "widths the content stream was laid out against cannot be recovered; embedding " +
                    "a program without them would move every glyph. Add a /Widths array first");

            if (site.FontObjectNumber <= 0)
                return Fail(plan,
                    "the font dictionary has no /Widths and is not an indirect object, so the " +
                    "Standard-14 widths cannot be written into it by an incremental update; " +
                    "embedding without them would move every glyph");

            // Standard14Metrics carries no Unicode-keyed table for the two symbolic faces (their
            // codes address a built-in encoding, not Unicode), so synthesising from it would write
            // an array of zeros and collapse every advance to nothing.
            if (site.Standard14Name == StandardFonts.Symbol || site.Standard14Name == StandardFonts.ZapfDingbats)
                return Fail(plan,
                    "the font dictionary has no /Widths and its base font is " + site.Standard14Name +
                    ", whose built-in metrics are keyed by its own symbolic encoding rather than by " +
                    "Unicode; the advance widths cannot be recovered, and embedding without them " +
                    "would move every glyph. Add a /Widths array first");

            if (!hasEncodingKey)
            {
                if (site.IsSymbolic)
                    return Fail(plan,
                        "the font is symbolic and declares no /Encoding, so the code-to-glyph mapping " +
                        "is the font program's own and cannot be reproduced from a Standard-14 " +
                        "metrics table");

                // Writing the widths means committing to an encoding, so the implicit one is made
                // explicit — and for a non-symbolic Standard-14 font with no /Encoding key the
                // implicit one is StandardEncoding (§9.6.2.2, Annex D.2), which is exactly what
                // ResolveEffectiveEncoding already returned in plan.Encoding above. It is NOT
                // overwritten here: substituting WinAnsi changed the glyph every code in 0xA1–0xFF
                // selects and then computed the synthesised /Widths against the substitution, so a
                // document that rendered "Straße" before remediation rendered "Straûe" after it.
                plan.WriteStandardEncoding = true;
            }

            if (!encodingIsKnown || plan.Encoding == null)
                return Fail(plan,
                    "the font's /Encoding could not be resolved to a code-to-Unicode mapping, so the " +
                    "Standard-14 advance width of each character code cannot be looked up");

            // 32..255 is the range a Standard-14 font's implicit encoding occupies, and it stays
            // the floor. It is not the ceiling, because the branch above also runs for a font that
            // HAS an /Encoding dictionary, and /Differences may map codes below 32 —
            // ResolveEffectiveEncoding builds those entries into plan.Encoding without comment.
            // Any such code fell outside the written array while CompleteDescriptor wrote
            // /MissingWidth 0 onto the new descriptor, so its advance became zero where the
            // un-embedded font had used the built-in AFM metric: `<< … /Encoding << /Differences
            // [1 /bullet 2 /emdash] >> >>` with no /Widths — legal under §9.6.2.2, which exempts
            // the Standard 14 — came out of embedding with codes 1 and 2 advancing by nothing, so
            // the following glyphs overprinted them.
            //
            // The range only ever WIDENS. Narrowing it to what the encoding names would drop codes
            // the array covers today at width 0, which is what /MissingWidth would give them
            // anyway, in exchange for changing the output of every ordinary font — the sort of
            // trade that turns one fix into a regression somewhere unrelated.
            plan.FirstChar = 32;
            plan.LastChar  = 255;

            foreach (int code in plan.Encoding.Keys)
            {
                // A simple font addresses one byte (§9.6.6.1), so a code outside 0..255 cannot be
                // drawn and must not stretch the array towards it.
                if (code < 0 || code > 255) continue;
                if (code < plan.FirstChar) plan.FirstChar = code;
                if (code > plan.LastChar)  plan.LastChar  = code;
            }

            var widths = new PdfArray();
            for (int code = plan.FirstChar; code <= plan.LastChar; code++)
                widths.Add(new PdfNumber(Standard14Width(site.Standard14Name, plan.Encoding, code, metrics)));

            plan.WidthsToWrite = widths;
            return plan;
        }

        /// <summary>
        /// Why the supplied program must not be hung on this composite font's descriptor, or null
        /// when the binding is at least structurally sound.
        ///
        /// <para>
        /// Two different failures are caught here, and neither is visible anywhere else in the
        /// pipeline. The first is a <b>format</b> mismatch: ISO 32000-1 Table 126 pairs
        /// <c>/CIDFontType2</c> with glyf outlines (<c>/FontFile2</c>) and <c>/CIDFontType0</c> with
        /// CFF ones (<c>/FontFile3</c>), and crossing them produces a font dictionary no reader can
        /// use — the text disappears rather than merely changing. The second is a <b>provenance</b>
        /// failure: a CID is an index into one specific program's glyph order, so a font this
        /// library picked off the host machine cannot possibly be the right one, and embedding it
        /// would replace every glyph on every page that uses the font with an unrelated one. That
        /// is strictly worse than the UA21/UA25 violation it would close, so it is refused rather
        /// than done quietly. A program the <i>caller</i> supplied is a different matter: the caller
        /// is the only party that can know which program the document was laid out against, so that
        /// one is embedded — with a warning naming the assumption.
        /// </para>
        /// </summary>
        private static string CheckCompositeBinding(FontSite site, ProgramKind kind, bool substituted)
        {
            // Normally settled earlier, before a system font is even looked up; restated here
            // because this method is where the rule lives, and a future caller must not be able to
            // reach the embedding path with a substitute for a composite font.
            if (substituted) return CompositeSubstitutionRefusal;

            switch (site.DescendantSubtype)
            {
                case "CIDFontType2":
                    // Table 126: glyf outlines via /FontFile2, or an OpenType wrapper via
                    // /FontFile3 /OpenType. A bare CFF would be written as /FontFile3 /Type1C,
                    // which is not a value this descendant may carry.
                    if (kind == ProgramKind.BareCff)
                        return "the descendant font is a /CIDFontType2, whose program ISO 32000-1 " +
                               "Table 126 requires to be TrueType (/FontFile2) or OpenType, but the " +
                               "supplied program is a bare CFF — it would be written as /FontFile3 " +
                               "/Type1C, which no reader accepts there, and the text would vanish. " +
                               "Supply a glyf-outlined program instead";
                    return null;

                case "CIDFontType0":
                    // Table 126: CFF outlines only (/FontFile3 with Type1C, CIDFontType0C or
                    // OpenType). A glyf-outlined sfnt would be written as /FontFile2.
                    if (kind == ProgramKind.SfntTrueType)
                        return "the descendant font is a /CIDFontType0, whose program ISO 32000-1 " +
                               "Table 126 requires to have CFF outlines, but the supplied program " +
                               "has TrueType (glyf) outlines — it would be written as /FontFile2, " +
                               "which no reader accepts there, and the text would vanish. Supply a " +
                               "CFF/OpenType program instead";
                    return null;

                default:
                    return "the descendant font declares /Subtype " +
                           (string.IsNullOrEmpty(site.DescendantSubtype)
                               ? "(none)"
                               : "/" + site.DescendantSubtype) +
                           ", which is neither /CIDFontType0 nor /CIDFontType2, so there is no way " +
                           "to tell which /FontFile key the supplied program may legally be written " +
                           "under; repair the CIDFont dictionary first";
            }
        }

        private static SimpleFontPlan Fail(SimpleFontPlan plan, string reason)
        {
            plan.SkipReason = reason;
            return plan;
        }

        /// <summary>
        /// The advance width, in 1/1000 em, that the document already assumes for
        /// <paramref name="code"/>: the original Standard-14 AFM metric, never the substitute's.
        /// Falls back to the embedded program only for a code the AFM table has no entry for, and
        /// to 0 when even that is unavailable (a code the document cannot be drawing).
        /// </summary>
        private static int Standard14Width(
            string standard14Name, Dictionary<int, char> encoding, int code, TrueTypeMetrics metrics)
        {
            if (encoding == null || !encoding.TryGetValue(code, out char unicode)) return 0;

            double afm = Standard14Metrics.GetWidthByUnicode(standard14Name, unicode);
            if (afm > 0) return SaturatingCast.ToInt(Math.Round(afm));

            if (metrics != null && unicode > 0 && unicode <= 0xFF)
                return metrics.GetWidth(unicode);

            return 0;
        }

        /// <summary>
        /// Resolves the font's effective character-code → Unicode mapping: a named base encoding,
        /// a base encoding plus <c>/Differences</c> glyph names, or (for a font with no
        /// <c>/Encoding</c>) the Standard-14 default. Returns null when the mapping is the font
        /// program's own business (a symbolic font with no encoding).
        /// </summary>
        private static Dictionary<int, char> ResolveEffectiveEncoding(
            PdfDocument doc, FontSite site, out bool known)
        {
            known = false;
            PdfObject encodingObj = doc.ResolveObject(site.FontDict[PdfName.Encoding]);

            if (encodingObj is PdfName encodingName)
            {
                known = true;
                return StandardEncodings.GetEncodingByName(encodingName.Value);
            }

            if (encodingObj is PdfDictionary encodingDict)
            {
                PdfName baseEncoding = ResolveName(doc, encodingDict[PdfName.BaseEncoding]);
                Dictionary<int, char> table = baseEncoding != null
                    ? new Dictionary<int, char>(StandardEncodings.GetEncodingByName(baseEncoding.Value))
                    : new Dictionary<int, char>(DefaultEncodingFor(site));

                PdfArray differences = ResolveArray(doc, encodingDict[PdfName.Differences]);
                if (differences != null)
                {
                    int code = 0;
                    for (int i = 0; i < differences.Count; i++)
                    {
                        PdfObject item = doc.ResolveObject(differences[i]);
                        if (item is PdfNumber number) { code = number.IntValue; continue; }
                        if (item is PdfName glyphName)
                        {
                            if (StandardEncodings.TryGetGlyphUnicode(glyphName.Value, out char unicode))
                                table[code] = unicode;
                            else
                                table.Remove(code);   // an unknown glyph name has no width we can look up
                            code++;
                        }
                    }
                }

                known = true;
                return table;
            }

            // No /Encoding at all. A non-symbolic Standard-14 font uses StandardEncoding; a symbolic
            // one uses the program's built-in table, which is not ours to guess.
            if (site.IsSymbolic) return null;

            known = site.Standard14Name != null;
            return DefaultEncodingFor(site);
        }

        private static Dictionary<int, char> DefaultEncodingFor(FontSite site)
            => site.Standard14Name == StandardFonts.Symbol       ? StandardEncodings.SymbolEncoding
             : site.Standard14Name == StandardFonts.ZapfDingbats ? StandardEncodings.ZapfDingbatsEncoding
             : StandardEncodings.AdobeStandardEncoding;
    }
}
