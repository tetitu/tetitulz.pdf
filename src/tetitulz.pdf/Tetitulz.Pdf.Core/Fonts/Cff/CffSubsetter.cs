// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Core.Fonts.Cff
{
    /// <summary>
    /// Shrinks a CFF / OpenType-CFF font program by <b>blanking the charstrings of glyphs the
    /// document never draws, in place</b> — each unused charstring is overwritten with a single
    /// <c>endchar</c> followed by zeros, at its original offset and to its original length.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing moves, and that is the entire design.</b> A CFF is a web of absolute
    /// offsets: the Top DICT points at the CharStrings INDEX, the charset, the Private DICT; the
    /// Private DICT points at its local subroutines; an FDArray points at more of each. Rebuilding
    /// the CharStrings INDEX means every one of those offsets has to be recomputed, and the INDEX's
    /// own <c>offSize</c> may change width and move the data block under them. Overwriting bytes
    /// where they already are keeps every offset — including the INDEX's own — literally unchanged,
    /// so the only thing that can go wrong is a glyph drawing nothing, and only for glyphs that were
    /// asked to draw nothing. The saving is not in the file length, which is identical: it is that
    /// a run of zeros costs Flate almost nothing, and <c>/FontFile3</c> is always Flate-encoded.</para>
    ///
    /// <para><b>Why <c>endchar</c> rather than zeros throughout.</b> Byte <c>0x00</c> is a reserved
    /// operator in Type 2 (TN #5177 §4), so a charstring of pure zeros is malformed; interpreters
    /// differ on whether that is an empty glyph or an error. <c>0x0E</c> is <c>endchar</c> with an
    /// empty operand stack — an explicitly empty glyph — and every interpreter stops there, so the
    /// zeros after it are never read.</para>
    ///
    /// <para><b>Three ways this refuses rather than guesses,</b> because a font that loads and draws
    /// the wrong thing is worse than one that is merely large:</para>
    /// <list type="bullet">
    ///   <item><b>CFF2</b> is a different format with a different header and a variation store; it is
    ///     refused, never rewritten.</item>
    ///   <item><b><c>seac</c> components.</b> An accented glyph draws two <i>other</i> glyphs, named
    ///     by Adobe StandardEncoding code in a 4- or 5-operand <c>endchar</c>. Those components are
    ///     usually not in the requested set — nothing in the document maps to them directly — so
    ///     blanking them would leave "é" as a floating acute. Every kept charstring is scanned
    ///     (through its subroutines) by <see cref="CffCharstringScanner"/> and the components it
    ///     names are kept too, transitively. A charstring the scanner will not reason about, or a
    ///     component it cannot resolve to a glyph, aborts the whole subset.</item>
    ///   <item><b>Any offset that would write below the CharStrings INDEX.</b> A malformed font whose
    ///     charstring offsets point back into the Top DICT would otherwise corrupt itself.</item>
    /// </list>
    ///
    /// <para><b>Subroutines are deliberately left alone.</b> A local or global subroutine reached
    /// only from a blanked charstring is dead weight, but proving it dead needs the reference graph
    /// of every charstring including the blanked ones, and removing one would renumber the rest —
    /// which relocates data and gives up the property this whole approach is built on. They compress
    /// well and are a small fraction of a large font.</para>
    /// </remarks>
    internal static class CffSubsetter
    {
        /// <summary>The Type 2 <c>endchar</c> operator: an explicitly empty glyph.</summary>
        private const byte EndChar = 0x0E;

        private const uint TagTtcf = 0x74746366u;   // 'ttcf'

        /// <summary>What <see cref="BlankUnusedGlyphs"/> did, or why it did nothing.</summary>
        internal sealed class CffSubsetResult
        {
            /// <summary>The smaller program, or <c>null</c> when nothing was produced.</summary>
            internal byte[] Font;

            /// <summary>
            /// Why <see cref="Font"/> is null, or <c>null</c> on success. Always set when the font
            /// is — a caller that embeds whole needs something to say, and "the font uses every
            /// glyph it has" is a different fact from "this is CFF2".
            /// </summary>
            internal string Reason;

            internal int GlyphCount;
            internal int GlyphsKept;
            internal int GlyphsBlanked;

            /// <summary>
            /// Charstring bytes overwritten. The <b>honest</b> measure of the win: the file length
            /// is unchanged, so only this and the post-Flate size say anything.
            /// </summary>
            internal int CharstringBytesBlanked;

            /// <summary>
            /// Glyphs kept because a kept glyph's <c>seac</c> <c>endchar</c> names them, over and
            /// above what the caller asked for. Zero for every font without accented composites.
            /// </summary>
            internal int SeacComponentsKept;

            /// <summary>
            /// True when the font was fully understood and simply had no unused glyph to blank.
            /// </summary>
            /// <remarks>
            /// This is what separates "this font's shape defeats the subsetter" from "the subsetter
            /// worked and there was nothing to do", which are the same <c>Font == null</c> to a
            /// caller that only looks at the return. <c>BuilderFontResolver</c> needs the
            /// distinction: its size ceiling is documented as applying to fonts that
            /// <b>cannot</b> be subsetted, and treating this case as a failure would keep refusing
            /// exactly the fonts subsetting was added to admit.
            /// </remarks>
            internal bool NothingToBlank;
        }

        /// <summary>
        /// Whether this font's <i>shape</i> can be subsetted at all — CFF1, parseable, with every
        /// charstring locatable and every <c>seac</c> component resolvable — regardless of how many
        /// glyphs <paramref name="keepGids"/> happens to leave unused.
        /// </summary>
        internal static bool CanSubset(byte[] fontBytes, IReadOnlyCollection<int> keepGids, out string reason)
        {
            CffSubsetResult probe = BlankUnusedGlyphs(fontBytes, keepGids);
            if (probe.Font != null || probe.NothingToBlank) { reason = null; return true; }
            reason = probe.Reason;
            return false;
        }

        /// <summary>
        /// Resolves Unicode code points to glyph ids, through the sfnt <c>cmap</c> when there is one
        /// and through the CFF's own glyph <b>names</b> when there is not.
        /// </summary>
        /// <remarks>
        /// <para><b>The bare-CFF route exists because a <c>/FontFile3 /Type1C</c> program has no
        /// cmap at all.</b> Its glyphs are named, and Adobe glyph names have defined Unicode values
        /// (<see cref="PdfGlyphNames.TryGetUnicode"/>, plus the AGL <c>uniXXXX</c> conventions), so
        /// inverting the font's own name table answers the question without inventing anything. Only
        /// names the font itself declares are consulted — nothing is guessed from the code point
        /// side, which is the direction that would produce a glyph set the font does not have.</para>
        /// <para><b>Collisions keep both glyphs.</b> Several names can denote one code point
        /// (<c>"A"</c> and <c>"uni0041"</c>; <c>"mu"</c> and <c>"uni00B5"</c>), and which one a
        /// reader lands on depends on the encoding in the font dictionary, which is not visible from
        /// here. Keeping every glyph that claims the code point costs at most a few charstrings and
        /// removes the possibility of blanking the one that gets drawn.</para>
        /// <para>Fails when <b>any</b> code point resolves to nothing: a partial glyph set is exactly
        /// what must not drive blanking.</para>
        /// </remarks>
        internal static bool TryMapCodePointsToGids(
            byte[] fontBytes, IReadOnlyCollection<int> codePoints, out List<int> gids, out string reason)
        {
            gids = null;
            reason = null;

            if (fontBytes == null || fontBytes.Length < 4) { reason = "the font is empty or shorter than a header"; return false; }
            if (codePoints == null) { reason = "no code points were supplied"; return false; }

            var result = new List<int> { 0 };   // .notdef is always kept

            // An sfnt has a cmap and it is the authority: it is what a reader consults.
            var program = TrueTypeFontProgram.Parse(fontBytes);
            if (program != null && program.GlyphCount > 0)
            {
                foreach (int codePoint in codePoints)
                {
                    if (!program.TryGetGlyph(codePoint, out ushort gid))
                    {
                        reason = "the font's cmap has no glyph for U+" +
                                 codePoint.ToString("X4", CultureInfo.InvariantCulture);
                        return false;
                    }
                    result.Add(gid);
                }
                gids = result;
                return true;
            }

            var font = CffParser.Parse(fontBytes);
            if (font == null)
            {
                reason = "the program is neither a readable sfnt nor a readable CFF (" +
                         CffParser.LastFailureReason.ToString() + ")";
                return false;
            }
            if (font.GlyphNameToGid == null || font.GlyphNameToGid.Count == 0)
            {
                reason = font.IsCidKeyed
                    ? "the program is a CID-keyed CFF, whose glyphs are numbered rather than named, " +
                      "so Unicode code points cannot be resolved to glyph ids without the CMap that " +
                      "selected them"
                    : "the CFF program declares no glyph names, so Unicode code points cannot be " +
                      "resolved to glyph ids";
                return false;
            }

            var byCodePoint = new Dictionary<int, List<int>>();
            foreach (var pair in font.GlyphNameToGid)
            {
                if (!PdfGlyphNames.TryGetUnicode(pair.Key, out char unicode)) continue;
                if (!byCodePoint.TryGetValue(unicode, out var forThis))
                    byCodePoint[unicode] = forThis = new List<int>();
                forThis.Add(pair.Value);
            }

            foreach (int codePoint in codePoints)
            {
                if (!byCodePoint.TryGetValue(codePoint, out var matches))
                {
                    reason = "the CFF program names no glyph for U+" +
                             codePoint.ToString("X4", CultureInfo.InvariantCulture);
                    return false;
                }
                result.AddRange(matches);
            }

            gids = result;
            return true;
        }

        /// <summary>
        /// Resolves CIDs to glyph ids — through the charset for a CID-keyed CFF, and as the identity
        /// for one that is not.
        /// </summary>
        /// <remarks>
        /// <para><b>CID is not GID, and assuming it is blanks the wrong glyphs.</b> A CID-keyed CFF's
        /// charset maps GID → CID (TN #5176 §13), so the inverse is what a subsetter holding CIDs
        /// needs. It is the identity often enough — an Identity charset is common — that the wrong
        /// assumption survives casual testing and then destroys every font whose producer ordered its
        /// charset any other way.</para>
        /// <para>A <b>non</b>-CID-keyed CFF reached through a <c>CIDFontType0</c> is legal (§9.7.4.2)
        /// and has no charset indirection: there the CID <i>is</i> the glyph index, and this returns
        /// it unchanged rather than refusing.</para>
        /// </remarks>
        internal static bool TryMapCidsToGids(
            byte[] fontBytes, IReadOnlyCollection<int> cids, out List<int> gids, out string reason)
        {
            gids = null;
            reason = null;

            if (fontBytes == null || fontBytes.Length < 4) { reason = "the font is empty or shorter than a header"; return false; }
            if (cids == null) { reason = "no CIDs were supplied"; return false; }

            var font = CffParser.Parse(fontBytes);
            if (font == null)
            {
                reason = "the CFF program could not be parsed (" + CffParser.LastFailureReason.ToString() + ")";
                return false;
            }

            int glyphCount = font.Charstrings.Count;
            var result = new List<int> { 0 };

            foreach (int cid in cids)
            {
                if (!font.IsCidKeyed)
                {
                    if (cid < 0 || cid >= glyphCount)
                    {
                        reason = "CID " + cid.ToString(CultureInfo.InvariantCulture) +
                                 " is outside a non-CID-keyed CFF's glyph range, where the CID is the glyph index";
                        return false;
                    }
                    result.Add(cid);
                    continue;
                }

                if (font.CharCodeToGid == null || !font.CharCodeToGid.TryGetValue(cid, out int gid))
                {
                    reason = "the CFF charset maps no glyph to CID " + cid.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                result.Add(gid);
            }

            gids = result;
            return true;
        }

        /// <summary>
        /// Blanks every charstring outside <paramref name="keepGids"/>, keeping the container shape.
        /// Accepts a bare CFF1 program or an <c>'OTTO'</c> sfnt wrapping one; never modifies
        /// <paramref name="fontBytes"/>.
        /// </summary>
        /// <param name="keepGids">
        /// Glyph ids the document draws. GID 0 (<c>.notdef</c>) is always added — a reader
        /// substitutes it for anything it cannot map, so blanking it makes the failure invisible.
        /// </param>
        /// <param name="fontBytes">
        /// The font program: a bare CFF1, or an <c>'OTTO'</c> sfnt wrapping one. Never modified —
        /// the result is a copy of the same length, since charstrings are blanked in place rather
        /// than removed.
        /// </param>
        internal static CffSubsetResult BlankUnusedGlyphs(byte[] fontBytes, IReadOnlyCollection<int> keepGids)
        {
            var result = new CffSubsetResult();

            if (fontBytes == null || fontBytes.Length < 4)
            {
                result.Reason = "the font is empty or shorter than a header";
                return result;
            }
            if (keepGids == null)
            {
                result.Reason = "no glyph set was supplied, so nothing could be shown to be unused";
                return result;
            }

            try
            {
                return Run(fontBytes, keepGids, result);
            }
            catch (Exception ex)
            {
                // A hostile or truncated font must cost an embed-whole, never an exception out of a
                // size optimisation the caller did not have to ask for.
                result.Font = null;
                result.Reason = "subsetting threw (" + ex.Message + ")";
                return result;
            }
        }

        private static CffSubsetResult Run(
            byte[] fontBytes, IReadOnlyCollection<int> keepGids, CffSubsetResult result)
        {
            // ---- Unwrap the container ----------------------------------------------------
            uint version = ReadU32(fontBytes, 0);
            List<(string tag, byte[] data)> sfntTables = null;
            int cffTableIndex = -1;
            byte[] cff;

            if (version == TagTtcf)
            {
                result.Reason = "TrueType Collections carry several fonts in one file and are not subsetted";
                return result;
            }

            if (version == SfntBuilder.VersionOpenTypeCff)
            {
                if (!TryReadSfntTables(fontBytes, out sfntTables, out string tableReason))
                {
                    result.Reason = tableReason;
                    return result;
                }

                for (int i = 0; i < sfntTables.Count; i++)
                {
                    if (string.Equals(sfntTables[i].tag, "CFF2", StringComparison.Ordinal))
                    {
                        result.Reason = "the font carries a CFF2 table (a variable-font format), which is " +
                                        "refused rather than rewritten";
                        return result;
                    }

                    // OS/2 fsType bit 8 — "no subsetting". Blanking a charstring destroys a glyph
                    // outline, so it is subsetting whatever the file length says; declining here
                    // makes every caller embed the whole program and write no "ABCDEF+" tag, which
                    // is what honouring the bit means. A BARE CFF (the `else` branch below) carries
                    // no OS/2 at all and so makes no statement — it is correctly not checked.
                    if (string.Equals(sfntTables[i].tag, "OS/2", StringComparison.Ordinal))
                    {
                        var licence = FontEmbeddingLicense.FromOs2Table(sfntTables[i].data, 0);
                        if (licence.SubsettingProhibited)
                        {
                            result.Reason = licence.Describe();
                            return result;
                        }
                    }

                    if (string.Equals(sfntTables[i].tag, "CFF ", StringComparison.Ordinal))
                        cffTableIndex = i;
                }

                if (cffTableIndex < 0)
                {
                    result.Reason = "the sfnt has no \"CFF \" table, so it has no charstrings to blank";
                    return result;
                }
                cff = (byte[])sfntTables[cffTableIndex].data.Clone();
            }
            else if (version == SfntBuilder.VersionTrueType || version == 0x74727565u /* 'true' */)
            {
                result.Reason = "the font has glyf outlines, not CFF ones — subset it with the TrueType subsetter";
                return result;
            }
            else
            {
                cff = (byte[])fontBytes.Clone();
            }

            // ---- Locate every charstring, by range ---------------------------------------
            if (!CffParser.TryLocateCharStrings(cff, out int[] starts, out int[] lengths,
                                                out int indexOffset, out string locateReason))
            {
                result.Reason = locateReason;
                return result;
            }

            result.GlyphCount = starts.Length;

            for (int gid = 0; gid < starts.Length; gid++)
            {
                if (starts[gid] < 0)
                {
                    result.Reason = "charstring " + gid.ToString(CultureInfo.InvariantCulture) +
                                    " lies outside the font data";
                    return result;
                }
                if (starts[gid] < indexOffset)
                {
                    // Only reachable from a font whose INDEX offsets point backwards. Writing there
                    // would overwrite the Top DICT or the string INDEX rather than a glyph.
                    result.Reason = "charstring " + gid.ToString(CultureInfo.InvariantCulture) +
                                    " is placed before the CharStrings INDEX it belongs to";
                    return result;
                }
            }

            // ---- Decide what to keep ------------------------------------------------------
            var font = CffParser.Parse(cff);
            if (font == null)
            {
                result.Reason = "the CFF program could not be parsed (" +
                                CffParser.LastFailureReason.ToString() + ")";
                return result;
            }
            if (font.IsCff2)
            {
                result.Reason = "the program is CFF2 (a variable-font format), which is refused rather than rewritten";
                return result;
            }
            if (font.Charstrings.Count != starts.Length)
            {
                // The parser and the locator walked the same INDEX; disagreeing means one of them
                // was reading something else, and neither answer can be trusted to pick bytes.
                result.Reason = "the parsed glyph count disagrees with the located charstring count";
                return result;
            }

            var keep = new HashSet<int> { 0 };
            foreach (int gid in keepGids)
                if (gid >= 0 && gid < starts.Length) keep.Add(gid);

            int requested = keep.Count;
            if (!TryExpandSeac(font, keep, out string seacReason))
            {
                result.Reason = seacReason;
                return result;
            }
            result.SeacComponentsKept = keep.Count - requested;
            result.GlyphsKept = keep.Count;

            // ---- Blank ---------------------------------------------------------------------
            for (int gid = 0; gid < starts.Length; gid++)
            {
                if (keep.Contains(gid) || lengths[gid] <= 0) continue;

                int start = starts[gid];
                cff[start] = EndChar;
                for (int i = start + 1; i < start + lengths[gid]; i++) cff[i] = 0;

                result.GlyphsBlanked++;
                result.CharstringBytesBlanked += lengths[gid];
            }

            if (result.GlyphsBlanked == 0)
            {
                // Returning the input here would read as a subset that happened to save nothing,
                // and the caller would then stamp an "ABCDEF+" tag on a complete font.
                result.NothingToBlank = true;
                result.Reason = "every glyph in the font is used, so there was nothing to blank";
                return result;
            }

            // ---- Reassemble ------------------------------------------------------------------
            byte[] output;
            if (sfntTables != null)
            {
                sfntTables[cffTableIndex] = ("CFF ", cff);
                output = SfntBuilder.Build(SfntBuilder.VersionOpenTypeCff, sfntTables);
                if (output == null)
                {
                    result.Reason = "the sfnt container could not be reassembled";
                    return result;
                }
            }
            else
            {
                output = cff;
            }

            // ---- Verify ---------------------------------------------------------------------
            // The bytes of every kept glyph were never written, so they are unchanged by
            // construction — but "by construction" is exactly the claim that a locator off by one
            // would also make. Reading the result back through the same parser the renderer uses is
            // what turns it into evidence.
            if (!Verify(output, sfntTables != null, keep, font, out string verifyReason))
            {
                result.Font = null;
                result.Reason = verifyReason;
                return result;
            }

            result.Font = output;
            result.Reason = null;
            return result;
        }

        /// <summary>
        /// Adds to <paramref name="keep"/> every glyph a kept glyph's <c>seac</c>-form
        /// <c>endchar</c> draws, to a fixed point — a component may itself be a composite.
        /// </summary>
        private static bool TryExpandSeac(CffFont font, HashSet<int> keep, out string reason)
        {
            reason = null;

            var pending = new Queue<int>(keep);
            var codes = new List<int>();

            while (pending.Count > 0)
            {
                int gid = pending.Dequeue();
                if (gid < 0 || gid >= font.Charstrings.Count) continue;

                codes.Clear();
                var localSubrs = font.LocalSubrsPerGid != null && gid < font.LocalSubrsPerGid.Length &&
                                 font.LocalSubrsPerGid[gid] != null
                    ? font.LocalSubrsPerGid[gid]
                    : font.LocalSubrs;

                if (!CffCharstringScanner.TryCollectSeacComponents(
                        font.Charstrings[gid], localSubrs, font.GlobalSubrs, codes, font.ScanSteps))
                {
                    reason = "glyph " + gid.ToString(CultureInfo.InvariantCulture) + "'s charstring uses " +
                             "operators this subsetter will not reason about, so the glyphs it depends on " +
                             "could not be established";
                    return false;
                }

                for (int i = 0; i < codes.Count; i++)
                {
                    if (!TryResolveComponent(font, codes[i], keep, pending))
                    {
                        reason = "glyph " + gid.ToString(CultureInfo.InvariantCulture) + " is an accented " +
                                 "composite whose component (Adobe StandardEncoding code " +
                                 codes[i].ToString(CultureInfo.InvariantCulture) + ") could not be resolved " +
                                 "to a glyph, so blanking could not be shown to be safe";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Resolves one <c>seac</c> component code to glyphs, through <b>both</b> routes and keeping
        /// whatever either finds.
        /// </summary>
        /// <remarks>
        /// TN #5177 says the code is an Adobe StandardEncoding one, resolved to a glyph name and then
        /// through the charset. This library's own renderer
        /// (<c>Base.Rendering.Type2CharstringEvaluator</c>) instead resolves it through the font's
        /// <c>/Encoding</c>. Keeping the union means whichever of the two a reader follows, the glyph
        /// it lands on still has its outline — and the cost of the disagreement is at most one extra
        /// glyph per composite.
        /// </remarks>
        private static bool TryResolveComponent(CffFont font, int code, HashSet<int> keep, Queue<int> pending)
        {
            bool resolved = false;

            if (CffParser.TryGetStandardEncodingGlyphName(code, out string name) &&
                font.GlyphNameToGid != null &&
                font.GlyphNameToGid.TryGetValue(name, out int byName))
            {
                resolved |= Add(byName);
            }

            if (font.CharCodeToGid != null && font.CharCodeToGid.TryGetValue(code, out int byCode))
            {
                resolved |= Add(byCode);
            }

            return resolved;

            bool Add(int gid)
            {
                if (gid < 0 || gid >= font.Charstrings.Count) return false;
                if (keep.Add(gid)) pending.Enqueue(gid);
                return true;
            }
        }

        /// <summary>
        /// Reads the output back and checks that every kept glyph's charstring survived byte for
        /// byte, and that nothing else the parser reads changed.
        /// </summary>
        private static bool Verify(
            byte[] output, bool isSfnt, HashSet<int> keep, CffFont original, out string reason)
        {
            reason = null;

            if (isSfnt && TrueTypeFontProgram.Parse(output) == null)
            {
                reason = "the subsetted program could not be read back as an sfnt";
                return false;
            }

            // CffParser.Parse unwraps an 'OTTO' container itself, so one call covers both shapes.
            var reparsed = CffParser.Parse(output);
            if (reparsed == null)
            {
                reason = "the subsetted program could not be read back as CFF (" +
                         CffParser.LastFailureReason.ToString() + ")";
                return false;
            }

            if (reparsed.Charstrings.Count != original.Charstrings.Count)
            {
                reason = "the subsetted program has a different glyph count than the original";
                return false;
            }

            foreach (int gid in keep)
            {
                if (gid < 0 || gid >= original.Charstrings.Count) continue;
                byte[] before = original.Charstrings[gid];
                byte[] after = reparsed.Charstrings[gid];
                if (before.Length != after.Length)
                {
                    reason = "kept glyph " + gid.ToString(CultureInfo.InvariantCulture) +
                             " changed length in the subsetted program";
                    return false;
                }
                if (!SameBytes(before, after))
                {
                    reason = "kept glyph " + gid.ToString(CultureInfo.InvariantCulture) +
                             " changed content in the subsetted program";
                    return false;
                }
            }

            // Everything else the parser reads must be unchanged too. A malformed INDEX can give a
            // charstring a range that runs over the Private DICT, the subroutines, the encoding or the
            // charset; blanking that glyph then rewrote them, and every kept glyph's bytes still
            // matched, so the subset was returned as a success.
            if (!SameItems(original.GlobalSubrs, reparsed.GlobalSubrs) ||
                !SameItems(original.LocalSubrs, reparsed.LocalSubrs) ||
                !SamePerGid(original.LocalSubrsPerGid, reparsed.LocalSubrsPerGid) ||
                original.DefaultWidthX != reparsed.DefaultWidthX ||
                original.NominalWidthX != reparsed.NominalWidthX ||
                !SameFloats(original.DefaultWidthsPerGid, reparsed.DefaultWidthsPerGid) ||
                !SameFloats(original.NominalWidthsPerGid, reparsed.NominalWidthsPerGid) ||
                original.FontMatrixScale != reparsed.FontMatrixScale ||
                !SameMap(original.CharCodeToGid, reparsed.CharCodeToGid) ||
                !SameMap(original.GlyphNameToGid, reparsed.GlyphNameToGid))
            {
                reason = "blanking reached data outside the charstrings (the Private DICT, a " +
                         "subroutine, the encoding or the charset), so a charstring's range overlaps it";
                return false;
            }

            return true;
        }

        private static bool SameItems(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!SameBytes(a[i], b[i])) return false;
            return true;
        }

        private static bool SamePerGid(IReadOnlyList<byte[]>[] a, IReadOnlyList<byte[]>[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!SameItems(a[i], b[i])) return false;
            return true;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SameFloats(float[] a, float[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SameMap<TKey>(IReadOnlyDictionary<TKey, int> a, IReadOnlyDictionary<TKey, int> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            foreach (var pair in a)
                if (!b.TryGetValue(pair.Key, out int value) || value != pair.Value) return false;
            return true;
        }

        /// <summary>
        /// Reads the sfnt table directory into tag/payload pairs, in directory order, through
        /// <see cref="SfntTableDirectory"/> — the one reader the three re-emitters share.
        /// </summary>
        /// <remarks>
        /// This used <see cref="Encoding.ASCII"/> for the tag, which substitutes <c>'?'</c> for any
        /// byte above 0x7E. Every table here is written straight back out by
        /// <see cref="SfntBuilder"/>, which takes the tag from that string — so such a table was
        /// silently renamed in the subsetted font, and the subset was returned as a success.
        /// </remarks>
        private static bool TryReadSfntTables(
            byte[] font, out List<(string tag, byte[] data)> tables, out string reason)
        {
            tables = null;

            if (!SfntTableDirectory.TryRead(font, out var records, out reason)) return false;

            var result = new List<(string tag, byte[] data)>(records.Count);
            foreach (SfntTableDirectory.TableRecord record in records)
                result.Add((record.Tag, SfntTableDirectory.Payload(font, record)));

            tables = result;
            return true;
        }

        private static uint ReadU32(byte[] data, int offset) => IO.BigEndian.U32(data, offset);
    }
}
