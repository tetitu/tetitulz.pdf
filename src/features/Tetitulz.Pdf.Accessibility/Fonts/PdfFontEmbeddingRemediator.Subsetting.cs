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

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Subsetting
        // ================================================================

        /// <summary>
        /// Reduces <paramref name="program"/> to the glyphs the document actually draws, or returns
        /// null (with the reason in <paramref name="note"/>, or null when there is nothing worth
        /// saying) so the caller embeds the full program instead.
        ///
        /// <para>
        /// A subset is only ever emitted when the character set is <b>provably complete</b>: every
        /// content stream that can reach the font was decoded and tokenised without incident. A
        /// subset built from a partial scan is missing glyphs the document really shows, which is a
        /// worse defect than the file size it saves.
        /// </para>
        ///
        /// <para>
        /// Complete input is not the same as correct output. The subsetter resolves characters
        /// through one cmap subtable and gives up — returning a valid, empty-looking font rather
        /// than an error — when that subtable is one it cannot read, so the result is <b>verified
        /// against the requested code points</b> before it is accepted. See
        /// <see cref="CountCodePointsMissingFrom"/>.
        /// </para>
        /// </summary>
        private static byte[] TrySubset(
            byte[] program,
            ProgramKind kind,
            FontSite site,
            FontEmbeddingRequest request,
            SimpleFontPlan plan,
            out string note)
        {
            note = null;

            if (site.IsType0)
            {
                note = "Font " + request + " is a Type0 composite: its character codes are CIDs, not " +
                       "Unicode, so the glyph set cannot be derived from the content stream here and " +
                       "the whole program was embedded.";
                return null;
            }

            if (kind != ProgramKind.SfntTrueType &&
                kind != ProgramKind.SfntOpenTypeCff &&
                kind != ProgramKind.BareCff)
            {
                note = "Font " + request + " was embedded whole: its program is not one this step " +
                       "can subset.";
                return null;
            }

            if (!request.UsedTextIsComplete)
            {
                note = "Font " + request + " was embedded whole rather than subsetted: at least one " +
                       "content stream that can reach it could not be scanned, so the set of glyphs it " +
                       "draws is not provably complete and a subset could drop one.";
                return null;
            }

            // ISO 32000-1 §9.6.4: a subset's /BaseFont and /FontName *shall* carry an "ABCDEF+" tag.
            // A font dictionary that cannot be revised cannot carry one, and an untagged subset is a
            // document that claims a full font while shipping part of one.
            if (site.FontObjectNumber <= 0)
            {
                note = "Font " + request + " was embedded whole rather than subsetted: its font " +
                       "dictionary is not an indirect object, so the ABCDEF+ subset tag ISO 32000-1 " +
                       "§9.6.4 requires could not be written onto it.";
                return null;
            }

            if (request.UsedCharacterCodes.Count == 0) return null;  // unused font: embed whole, silently

            // The glyph set has to be read through the encoding the output document will carry, not
            // through the one the input happened to imply. When a Standard-14 font's implicit
            // encoding is made explicit as /WinAnsiEncoding, those two differ, and subsetting
            // against the wrong one drops exactly the glyphs that then get shown.
            Dictionary<int, char> encoding = plan?.Encoding;
            if (encoding == null)
            {
                note = "Font " + request + " was embedded whole rather than subsetted: its character " +
                       "codes could not be resolved to an encoding, so the glyph set it needs is unknown.";
                return null;
            }

            var codePoints = new HashSet<int>();
            for (int i = 0; i < request.UsedCharacterCodes.Count; i++)
            {
                int code = request.UsedCharacterCodes[i];
                if (!encoding.TryGetValue(code, out char c))
                {
                    note = "Font " + request + " was embedded whole rather than subsetted: character " +
                           "code " + code.ToString(CultureInfo.InvariantCulture) + " is drawn on the " +
                           "page but its encoding names no glyph, so a subset could not be shown to " +
                           "keep it.";
                    return null;
                }
                codePoints.Add(c);
            }

            // CFF outlines take the other subsetter: PdfFontSubsetter.Subset rebuilds glyf tables
            // and refuses these outright. See TrySubsetCffOutlines for why its result needs no
            // coverage re-check while the glyf path's does. A BARE CFF goes there too — it has no
            // cmap, but PdfFontSubsetter.TryMapCodePointsToCffGlyphs resolves through the font's own
            // glyph names, which is a fact the font states rather than a guess about it.
            if (kind == ProgramKind.SfntOpenTypeCff || kind == ProgramKind.BareCff)
                return TrySubsetCffOutlines(program, request, codePoints, out note);

            // .notdef is glyph 0 and the subsetter always keeps it; nothing else is assumed.
            try
            {
                byte[] subset = PdfFontSubsetter.Subset(program, codePoints);
                if (subset == null || subset.Length == 0)
                {
                    note = "Font " + request + " was embedded whole: subsetting produced no output.";
                    return null;
                }

                // "Non-null and non-empty" is not proof that anything survived. The subsetter maps
                // characters to glyphs through a single cmap subtable and gives up — silently, and
                // with a perfectly well-formed sfnt to show for it — when that subtable is one it
                // cannot read: a Windows-Symbol (3,0) table, or any format other than 4. What comes
                // back then is a font containing .notdef and nothing else, which this method would
                // stamp an "ABCDEF+" subset tag onto and hand to the writer, blanking every glyph
                // the document draws. So the result is verified, not assumed.
                int missing = CountCodePointsMissingFrom(subset, codePoints, out bool readable);
                if (!readable || missing > 0)
                {
                    string cause = readable
                        ? "the subset it produced has no glyph for " +
                          missing.ToString(CultureInfo.InvariantCulture) + " of the " +
                          codePoints.Count.ToString(CultureInfo.InvariantCulture) +
                          " character(s) this font draws (the subsetter reads a single format-4 " +
                          "Unicode cmap, so a symbolic (3,0) table or a format 0/6/12 one yields " +
                          "no glyphs at all)"
                        : "the subset it produced could not be read back as an sfnt, so there is no " +
                          "evidence it kept the glyphs this font draws";

                    AccessibilityDiagnostics.Report(Op,
                        "the subset built for " + request + " was rejected because " + cause +
                        "; the whole program was embedded instead");

                    note = "Font " + request + " was embedded whole rather than subsetted: " + cause +
                           ", and a subset that drops a glyph the document shows is never emitted.";
                    return null;
                }

                return subset;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "subsetting " + request + " failed; the whole program was embedded instead", ex);
                note = "Font " + request + " was embedded whole because subsetting failed (" +
                       ex.Message + ").";
                return null;
            }
        }

        /// <summary>
        /// Subsets a CFF program — sfnt-wrapped (<c>'OTTO'</c>) or bare — by blanking the
        /// charstrings of glyphs this document never draws
        /// (<see cref="PdfFontSubsetter.TrySubsetCff"/>).
        /// </summary>
        /// <remarks>
        /// <para><b>Three things differ from the glyf path above and all three are easy to get wrong.</b></para>
        /// <para>Zeroth, <b>the glyph set is resolved by
        /// <see cref="PdfFontSubsetter.TryMapCodePointsToCffGlyphs"/>, not by this method.</b> A bare
        /// CFF has no cmap, so the obvious <c>TrueTypeFontProgram.TryGetGlyph</c> answers nothing for
        /// it; that resolver falls back to the font's own glyph names, which is a fact the font
        /// states rather than a guess about it. Writing the cmap lookup inline here is what kept
        /// bare CFF declining for the first half of this round.</para>
        /// <para>First, <b>the output is the same length as the input</b> — blanking overwrites
        /// charstrings where they sit so that no offset inside the CFF moves. The saving is entirely
        /// in the Flate-encoded <c>/FontFile3</c>, which is what actually goes into the file. Do not
        /// add a "the subset must be smaller" check here; it would reject every correct result.</para>
        /// <para>Second, <b><see cref="CountCodePointsMissingFrom"/> cannot be used to verify this
        /// one.</b> It re-reads the program's cmap, and blanking never touches the cmap — so it
        /// would report full coverage even if every charstring the document needs had been emptied.
        /// It would be a check that cannot fail, which is banned pattern 3 with a font in it. The
        /// evidence is produced instead by the subsetter, which reads its own output back and
        /// refuses unless every kept glyph's charstring survived byte for byte.</para>
        /// </remarks>
        private static byte[] TrySubsetCffOutlines(
            byte[] program, FontEmbeddingRequest request, HashSet<int> codePoints, out string note)
        {
            note = null;

            try
            {
                // One resolver for both shapes: the cmap when the program is an sfnt, the font's own
                // glyph names when it is a bare CFF. It fails rather than returning a partial set,
                // because a glyph set missing something the page draws is what must not drive
                // blanking — the font having no glyph for a drawn character is a pre-existing defect
                // in the document, and this is not the step that fixes it.
                if (!PdfFontSubsetter.TryMapCodePointsToCffGlyphs(
                        program, codePoints, out int[] keepGids, out string mapReason))
                {
                    note = "Font " + request + " was embedded whole rather than subsetted: " +
                           mapReason + ", so the glyph set to keep is not provably complete.";
                    return null;
                }

                if (PdfFontSubsetter.TrySubsetCff(program, keepGids, out byte[] subset, out string why))
                    return subset;

                note = "Font " + request + " was embedded whole rather than subsetted: " + why + ".";
                return null;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "CFF subsetting " + request + " failed; the whole program was embedded instead", ex);
                note = "Font " + request + " was embedded whole because CFF subsetting failed (" +
                       ex.Message + ").";
                return null;
            }
        }

        /// <summary>
        /// How many of <paramref name="codePoints"/> the subsetted program has no glyph for.
        ///
        /// <para>
        /// The subsetter writes its own cmap: one <c>(3,1)</c> format-4 subtable holding exactly the
        /// code points it managed to resolve. Reading that back with
        /// <see cref="TrueTypeFontProgram"/> — the same parser the writing side of this library uses
        /// — therefore answers the only question that matters here: does the program about to be
        /// embedded still draw what the page draws?
        /// </para>
        ///
        /// <para>
        /// A shortfall is treated as a failure even when the <i>source</i> program had no glyph for
        /// the character either. The cost of being wrong in that direction is a larger file; the
        /// cost of being wrong in the other is a page of blank boxes.
        /// </para>
        /// </summary>
        /// <param name="readable">False when the subset is not parseable as an sfnt at all, which is
        /// itself a refusal — an unreadable program is not evidence of anything.</param>
        /// <param name="subset">The subsetted font program to check.</param>
        /// <param name="codePoints">The code points the document actually draws with this font.</param>
        private static int CountCodePointsMissingFrom(
            byte[] subset, HashSet<int> codePoints, out bool readable)
        {
            TrueTypeFontProgram parsed = TrueTypeFontProgram.Parse(subset);
            readable = parsed != null;
            if (!readable) return codePoints.Count;

            int missing = 0;
            foreach (int codePoint in codePoints)
                if (!parsed.Covers(codePoint)) missing++;
            return missing;
        }

        // The six-letter tag (ISO 32000-1 §9.6.4) and the strip that undoes it live in Core as
        // Tetitulz.Pdf.Core.Fonts.SubsetTag, because this is not the only subsetting path: Core's
        // own Type0FontBuilder is the other one, and both label the same situation the same way
        // whichever door a font comes through.
    }
}
