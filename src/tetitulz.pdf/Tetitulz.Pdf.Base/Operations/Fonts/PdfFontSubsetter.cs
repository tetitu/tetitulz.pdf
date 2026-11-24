// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Fonts
{
    /// <summary>
    /// Subsets a TrueType font to include only the glyphs needed for a specified
    /// character set, reducing embedded font size by 70-90% for large fonts.
    ///
    /// <para>
    /// Subsetting:
    /// <list type="bullet">
    ///   <item>Reads the source TTF/TTC (TrueType) font tables.</item>
    ///   <item>Maps the requested characters to glyph IDs via the cmap (formats 4 and 12).</item>
    ///   <item>Recursively collects composite glyph component references.</item>
    ///   <item>Rebuilds <c>glyf</c>, <c>loca</c>, <c>cmap</c>, <c>hmtx</c>, <c>hhea</c>,
    ///     <c>maxp</c>, and <c>head</c> tables containing only the subset glyphs.</item>
    ///   <item>Copies <c>OS/2</c>, <c>name</c>, <c>post</c>, <c>cvt</c>, <c>fpgm</c>,
    ///     <c>prep</c> tables verbatim.</item>
    ///   <item>Glyph 0 (.notdef) is always included.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Outline format decides the method, and there are two.</b> <see cref="Subset(byte[], IEnumerable{int})"/>
    /// handles TrueType (<c>glyf</c>) only: it rebuilds the tables, so the output really is smaller.
    /// <see cref="TrySubsetCff"/> handles CFF/OpenType-CFF by blanking unused charstrings in place,
    /// so the output is the <b>same length</b> and the saving appears only after Flate. A third
    /// method, <see cref="TryCompact"/>, removes tables rather than glyphs and works on both —
    /// and, unlike the other two, produces something that must not carry a subset tag.
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   byte[] fullFont = File.ReadAllBytes("Arial.ttf");
    ///   byte[] subset   = PdfFontSubsetter.Subset(fullFont, "Hello, World!");
    ///   PdfFontEmbedder.EmbedFont(input, output, subset, "F1");
    /// </code>
    /// </summary>
    public static class PdfFontSubsetter
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Subsets a TrueType font to include only the glyphs required for the given text.
        /// </summary>
        /// <param name="fontBytes">Raw TTF font bytes.</param>
        /// <param name="text">The text whose characters should be included in the subset.</param>
        /// <returns>A new, smaller TTF font byte array containing only the needed glyphs.</returns>
        /// <exception cref="NotSupportedException">Thrown for CFF (OpenType) fonts — subset only TrueType.</exception>
        public static byte[] Subset(byte[] fontBytes, string text)
        {
            if (fontBytes == null) throw new ArgumentNullException(nameof(fontBytes));
            if (text == null)      throw new ArgumentNullException(nameof(text));
            return Subset(fontBytes, CodePointsOf(text));
        }

        /// <summary>
        /// Subsets a TrueType font to include only the glyphs required for the given characters.
        /// A surrogate pair among them is the one character it encodes.
        /// </summary>
        public static byte[] Subset(byte[] fontBytes, IEnumerable<char> characters)
        {
            if (fontBytes == null)  throw new ArgumentNullException(nameof(fontBytes));
            if (characters == null) throw new ArgumentNullException(nameof(characters));
            return Subset(fontBytes, CodePointsOf(characters));
        }

        /// <summary>
        /// The code points UTF-16 text encodes: a high surrogate followed by a low one is one
        /// astral character, and a surrogate standing alone is kept as it is.
        /// </summary>
        /// <remarks>
        /// Each <c>char</c> was taken as a code point, so an emoji or a CJK Extension B ideograph
        /// arrived as its two halves: neither is in any cmap, the character was reported unmapped,
        /// and the format-12 support this class documents could never be reached from text.
        /// </remarks>
        private static HashSet<int> CodePointsOf(IEnumerable<char> characters)
        {
            var codePoints = new HashSet<int>();
            char? pending = null;
            foreach (char c in characters)
            {
                if (pending.HasValue)
                {
                    if (char.IsLowSurrogate(c)) { codePoints.Add(char.ConvertToUtf32(pending.Value, c)); pending = null; continue; }
                    codePoints.Add(pending.Value);
                    pending = null;
                }
                if (char.IsHighSurrogate(c)) pending = c;
                else codePoints.Add(c);
            }
            if (pending.HasValue) codePoints.Add(pending.Value);
            return codePoints;
        }

        /// <summary>
        /// Subsets a TrueType font to include only the glyphs for the given Unicode code points.
        /// </summary>
        public static byte[] Subset(byte[] fontBytes, IEnumerable<int> codePoints)
        {
            if (fontBytes == null)  throw new ArgumentNullException(nameof(fontBytes));
            if (codePoints == null) throw new ArgumentNullException(nameof(codePoints));

            // Reset at the start, on every path: a caller reads this AFTER the call, so a previous
            // font's reason must not answer this font's question.
            LastSubsetError = null;

            // Refused by signature, before the directory reader turns a 'ttcf' header's
            // majorVersion into a table count. Without this the message was "Font does not contain
            // a 'glyf' table" — which a collection plainly does — sending the caller to the CFF
            // path for a TrueType font. See PdfFontEmbedder.RefuseCollection for the full shape.
            if (Core.Fonts.SfntTableDirectory.IsCollection(fontBytes))
                throw new NotSupportedException(Core.Fonts.SfntTableDirectory.CollectionRefusal + ".");

            return new TtfSubsetter(fontBytes).Subset(new HashSet<int>(codePoints));
        }

        /// <summary>
        /// Converts a Type 1 font program to a bare CFF, so it can be subset and embedded through
        /// the same path as an OpenType-CFF one.
        /// </summary>
        /// <remarks>
        /// <para><b>A separate entry rather than a branch inside <c>Subset</c>.</b> That method's
        /// contract is TrueType in, TrueType out; returning a CFF from it would be a type change
        /// wearing the same signature. The composition a caller wants is this, then
        /// <see cref="TrySubsetCff"/>, then embedding the result as
        /// <c>/FontFile3</c> with <c>/Subtype /Type1C</c> — and the <c>/FontFile</c> entry's
        /// <c>/Length1</c>, <c>/Length2</c> and <c>/Length3</c> go with the old program, because
        /// they describe segments a CFF does not have.</para>
        ///
        /// <para><b>Hints do not survive, and the caller should know before choosing this.</b>
        /// Type 1 declares stems inline and replaces them mid-glyph through <c>othersubr 3</c>;
        /// Type 2 needs the complete stem list before any outline is emitted. The outlines are
        /// exact and the rendering on a hinting rasteriser at small sizes is not as good as the
        /// original's. Against a font that could not be subset at all, that is the better
        /// trade — but it is a trade.</para>
        /// </remarks>
        /// <param name="fontFile">The <c>/FontFile</c> stream's decoded bytes.</param>
        /// <param name="length1">The <c>/Length1</c> entry: the cleartext header's length.</param>
        /// <param name="length2">The <c>/Length2</c> entry: the encrypted portion's length.</param>
        /// <param name="cff">The converted bare CFF, or <c>null</c>.</param>
        /// <param name="reason">Why nothing was produced, or <c>null</c> on success.</param>
        public static bool TryConvertType1ToCff(
            byte[] fontFile, int length1, int length2, out byte[] cff, out string reason)
        {
            cff = null;
            reason = null;

            if (fontFile == null || fontFile.Length == 0)
            {
                reason = "the /FontFile stream is empty";
                return false;
            }

            Core.Fonts.Type1.Type1Font parsed;
            try
            {
                parsed = Core.Fonts.Type1.Type1Parser.Parse(fontFile, length1, length2);
            }
            catch (Exception ex)
            {
                reason = "the Type 1 program could not be parsed: " + ex.Message;
                return false;
            }

            if (parsed == null)
            {
                reason = "the Type 1 program could not be parsed";
                return false;
            }

            cff = Core.Fonts.Type1.Type1ToCffConverter.Convert(parsed, null);
            if (cff != null) return true;

            reason = Core.Fonts.Type1.Type1ToCffConverter.LastError
                     ?? "the Type 1 program could not be converted";
            return false;
        }

        /// <summary>
        /// Removes the sfnt tables a PDF reader never consults, leaving every glyph in place.
        /// Works for <b>CFF/OpenType as well as TrueType</b> — unlike
        /// <see cref="Subset(byte[], IEnumerable{int})"/>, which is TrueType-only.
        /// </summary>
        /// <remarks>
        /// <para><b>This is not subsetting, and the name says so on purpose.</b> Subsetting removes
        /// glyphs; this removes tables. A caller expecting the 90% reduction a real subset gives a
        /// CJK font will be disappointed — but for a font that currently ships <i>whole</i>, which
        /// is every CFF font this library embeds, dropping the OpenType Layout tables is usually
        /// the single largest saving available and it cannot change a single rendered glyph.</para>
        /// <para><b>Why it is safe:</b> ISO 32000-1 §9.4 defines showing text as character codes →
        /// glyphs with advances from <c>/Widths</c>, so a reader neither shapes nor kerns — the
        /// producer already did both. <c>GSUB</c>/<c>GPOS</c> and friends are therefore dead weight
        /// in an embedded font. <c>DSIG</c> goes for a different reason: it signs the font file, so
        /// any rewrite invalidates it and a stale signature is worse than none. Full list and
        /// rationale on <c>Core.Fonts.SfntTablePruner</c>.</para>
        /// <para><b>Opt-in, and deliberately not applied by <c>PdfFontEmbedder</c> automatically.</b>
        /// Turning it on by default would move the output bytes of every existing caller, which is
        /// the same rule that keeps <c>PdfRenderOptions.JpegSubsampling</c> at its historical
        /// default. Call it when you want smaller files.</para>
        /// <para>Variable fonts and TrueType Collections are refused with a reason rather than
        /// guessed at.</para>
        /// </remarks>
        /// <param name="fontBytes">The font to compact. Never modified.</param>
        /// <param name="compacted">The smaller font, or <c>null</c> when it could not be compacted.</param>
        /// <param name="reason">
        /// Why nothing was produced, or <c>null</c> on success. Non-null with a <c>false</c> return
        /// is the honest answer for "this font carries nothing droppable" — distinguishable from a
        /// compaction that happened to save nothing, which is also reported rather than returned as
        /// a success.
        /// </param>
        /// <returns><c>true</c> when <paramref name="compacted"/> is a smaller, valid font.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fontBytes"/> is null.</exception>
        public static bool TryCompact(byte[] fontBytes, out byte[] compacted, out string reason)
        {
            if (fontBytes == null) throw new ArgumentNullException(nameof(fontBytes));

            compacted = Tetitulz.Pdf.Core.Fonts.SfntTablePruner.Prune(fontBytes, out reason, out _);
            return compacted != null;
        }

        /// <summary>
        /// Subsets a <b>CFF / OpenType-CFF</b> program to <paramref name="keepGlyphIds"/> by
        /// overwriting every other charstring, in place, with a single <c>endchar</c> and zeros.
        /// The complement of <see cref="Subset(byte[], IEnumerable{int})"/>, which handles
        /// <c>glyf</c> outlines and refuses these.
        /// </summary>
        /// <remarks>
        /// <para><b>The output is the same length as the input, always.</b> Nothing is removed and
        /// nothing relocates — that is what makes the technique safe, since every absolute offset in
        /// a CFF (Top DICT → CharStrings, charset, Private DICT → local subrs, FDArray → both) stays
        /// valid. The saving is in the <i>compressed</i> program: a run of zeros costs Flate almost
        /// nothing, and <c>/FontFile3</c> is always Flate-encoded. A caller measuring
        /// <c>subset.Length &lt; font.Length</c> will conclude, wrongly, that nothing happened.</para>
        ///
        /// <para><b>It fails closed.</b> CFF2, a TrueType Collection, a glyf-outlined font, a
        /// malformed CharStrings INDEX, and an accented composite whose <c>seac</c> components
        /// cannot be resolved all produce <c>false</c> with a reason, so the caller embeds the whole
        /// program. A larger font is a cost; a font missing a glyph the page draws is a defect.</para>
        ///
        /// <para><b>GID 0 is always kept</b> — a reader substitutes <c>.notdef</c> for anything it
        /// cannot map, so blanking it turns a visible failure into an invisible one.</para>
        ///
        /// <para><b>The result is a genuine subset for ISO 32000-1 §9.6.4 purposes</b>, so a caller
        /// writing it into a font dictionary must apply the <c>ABCDEF+</c> tag
        /// (<c>Core.Fonts.SubsetTag</c>). That is the difference from <see cref="TryCompact"/>,
        /// which restricts no glyph and must NOT be tagged.</para>
        /// </remarks>
        /// <param name="fontBytes">The program to subset. Never modified.</param>
        /// <param name="keepGlyphIds">Glyph ids the document draws.</param>
        /// <param name="subset">The subsetted program, or <c>null</c> when nothing was produced.</param>
        /// <param name="reason">
        /// Why nothing was produced, or <c>null</c> on success. A <c>false</c> return always carries
        /// one — including for the honest "every glyph in this font is used" case, which is not a
        /// failure but is also not a subset.
        /// </param>
        /// <returns><c>true</c> when <paramref name="subset"/> is a valid program drawing fewer glyphs.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fontBytes"/> is null.</exception>
        public static bool TrySubsetCff(
            byte[] fontBytes, IEnumerable<int> keepGlyphIds, out byte[] subset, out string reason)
        {
            if (fontBytes == null) throw new ArgumentNullException(nameof(fontBytes));

            var keep = keepGlyphIds == null ? new List<int>() : new List<int>(keepGlyphIds);
            var result = Core.Fonts.Cff.CffSubsetter.BlankUnusedGlyphs(fontBytes, keep);
            subset = result.Font;
            reason = result.Reason;
            return subset != null;
        }

        /// <summary>
        /// Resolves Unicode code points to the glyph ids <see cref="TrySubsetCff"/> takes, through
        /// the sfnt <c>cmap</c> when the program has one and through the CFF's own glyph
        /// <b>names</b> when it does not.
        /// </summary>
        /// <remarks>
        /// <para>The name route is what makes a bare <c>/FontFile3 /Type1C</c> program subsettable:
        /// it carries no cmap, but its glyphs are named and Adobe glyph names have defined Unicode
        /// values. Only names the font itself declares are consulted, and when several denote one
        /// code point <b>every</b> matching glyph is kept — which of them a reader lands on depends
        /// on the font dictionary's encoding, which is not visible from a font program.</para>
        /// <para><b>Fails when any one code point resolves to nothing</b>, rather than returning a
        /// partial set. A glyph set that is missing something the page draws is precisely what must
        /// not reach a subsetter; <c>.notdef</c> is always included.</para>
        /// </remarks>
        /// <returns><c>true</c> when every code point resolved.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fontBytes"/> is null.</exception>
        public static bool TryMapCodePointsToCffGlyphs(
            byte[] fontBytes, IEnumerable<int> codePoints, out int[] glyphIds, out string reason)
        {
            if (fontBytes == null) throw new ArgumentNullException(nameof(fontBytes));

            var wanted = codePoints == null ? new List<int>() : new List<int>(codePoints);
            bool ok = Core.Fonts.Cff.CffSubsetter.TryMapCodePointsToGids(
                fontBytes, wanted, out List<int> gids, out reason);
            glyphIds = ok ? gids.ToArray() : null;
            return ok;
        }

        /// <summary>
        /// Resolves CIDs to the glyph ids <see cref="TrySubsetCff"/> takes — through the CFF charset
        /// for a CID-keyed program, and as the identity for one that is not.
        /// </summary>
        /// <remarks>
        /// <b>A CID is not a glyph index.</b> A CID-keyed CFF's charset maps GID → CID
        /// (TN #5176 §13); the inverse is what a caller holding the CIDs from an <c>Identity-H</c>
        /// content stream needs. The two coincide often enough that assuming it survives casual
        /// testing and then blanks the wrong glyphs on every font ordered any other way. A
        /// non-CID-keyed CFF under a <c>CIDFontType0</c> is legal and has no charset indirection;
        /// there the CID is the glyph index and is returned unchanged.
        /// </remarks>
        /// <returns><c>true</c> when every CID resolved.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fontBytes"/> is null.</exception>
        public static bool TryMapCidsToCffGlyphs(
            byte[] fontBytes, IEnumerable<int> cids, out int[] glyphIds, out string reason)
        {
            if (fontBytes == null) throw new ArgumentNullException(nameof(fontBytes));

            var wanted = cids == null ? new List<int>() : new List<int>(cids);
            bool ok = Core.Fonts.Cff.CffSubsetter.TryMapCidsToGids(
                fontBytes, wanted, out List<int> gids, out reason);
            glyphIds = ok ? gids.ToArray() : null;
            return ok;
        }

        [ThreadStatic] private static string _lastSubsetError;

        /// <summary>
        /// Why the most recent <c>Subset</c> on <b>this thread</b> mapped fewer characters than it
        /// was asked for, or <c>null</c> when every requested character was mapped.
        /// </summary>
        /// <remarks>
        /// <para>The subsetter reads one Unicode cmap subtable, format 4 or 12: with format 4 alone, a
        /// font whose only Unicode cmap is format 12 — normal for anything with astral coverage: Noto
        /// CJK, most emoji and symbol fonts — would leave the char-to-glyph map <b>empty</b> and return
        /// a structurally valid font containing nothing but <c>.notdef</c>, with no exception and no
        /// diagnostic. <see cref="PdfFontEmbedder.EmbedFont"/> then embedded it
        /// happily and every glyph in the document rendered as a blank box; the caller's only
        /// signal was that the output was small, which is also what a legitimately tiny subset
        /// looks like.</para>
        /// <para>Also set when a hostile or truncated font makes a table unreadable, and when the
        /// cmap expansion hits <see cref="MaxCmapExpansion"/>. The
        /// operation still returns a font; the slot is how a caller learns it is incomplete.</para>
        /// <para><c>[ThreadStatic]</c> per the Diagnostics Pattern in CLAUDE.md.</para>
        /// </remarks>
        public static string LastSubsetError
        {
            get => _lastSubsetError;
            private set => _lastSubsetError = value;
        }

        internal static void ReportSubsetIssue(string message)
        {
            // First reason wins: it is the one that explains the rest.
            if (_lastSubsetError == null) _lastSubsetError = message;
        }

        // ----------------------------------------------------------------
        //  Internal subsetter engine
        // ----------------------------------------------------------------

        /// <summary>
        /// Upper bound on the code points a format-4 cmap walk may expand to. The BMP has 65 536
        /// of them, so a legal subtable can never exceed this; only overlapping or lying segments
        /// can, and those would spin ~2 billion iterations.
        /// </summary>
        private const int MaxCmapExpansion = 0x10000;

        /// <summary>Bytes per sfnt table-directory record: tag(4) + checksum(4) + offset(4) + length(4).</summary>
        private const int TableDirectoryEntryBytes = 16;

        private sealed class TtfSubsetter
        {
            private readonly byte[] _src;

            // Table offsets/lengths in the source font.
            private readonly Dictionary<string, (uint offset, uint length)> _tables
                = new Dictionary<string, (uint, uint)>();

            // glyf / loca data parsed from source.
            private byte[] _glyfData;
            private long[]  _locaOffsets;   // absolute offsets into _glyfData for each glyph + 1 sentinel
            private int     _numGlyphs;
            private int     _indexToLocFormat;  // 0=short, 1=long
            private int     _unitsPerEm;
            private int     _numberOfHMetrics;
            private int     _ascent, _descent;
            private ushort[] _advanceWidths;
            private short[]  _leftBearings;

            // char→glyph map from cmap
            private Dictionary<int, int> _charToGlyph = new Dictionary<int, int>();

            /// <summary>
            /// The chosen cmap subtable is Windows SYMBOL <c>(3,0)</c>, whose codes conventionally
            /// sit in the PUA at <c>0xF000 + code</c>.
            /// </summary>
            /// <remarks>
            /// Measured on the shipping fonts: symbol.ttf, wingding.ttf and marlett.ttf all carry a
            /// <c>(3,0)</c> FORMAT 4 table starting at 0xF020, 0xF020 and 0xF030 respectively,
            /// alongside a <c>(1,0)</c> format-0 table. So no format-0 reader is needed to subset
            /// them -- only a score that lets the (3,0) table win, and a lookup that folds.
            /// </remarks>
            private bool _symbolicCmap;

            /// <summary>
            /// The glyph for <paramref name="codePoint"/>, and the cmap key that found it.
            /// </summary>
            /// <remarks>
            /// <b>The key is returned, not assumed.</b> A symbolic font may map a byte directly or
            /// at <c>0xF000 + byte</c>, and real fonts do one or the other per character. The
            /// emitted subset cmap re-uses whichever key matched, so the subset is the same KIND of
            /// font as the original and any consumer that read one reads the other.
            /// <para><b>A glyph id the font does not have is no glyph.</b> Formats 0, 4 and 6 map to
            /// whatever id the table says, and a hostile font names one past <c>numGlyphs</c>; it
            /// reached the subset's glyph list and indexed <c>loca</c> out of range. Format 12
            /// already checked.</para>
            /// </remarks>
            private bool TryGlyph(int codePoint, out int gid, out int cmapKey)
            {
                cmapKey = codePoint;
                if (_charToGlyph.TryGetValue(codePoint, out gid) && gid > 0 && gid < _numGlyphs) return true;

                if (_symbolicCmap && codePoint >= 0 && codePoint <= 0xFF)
                {
                    cmapKey = 0xF000 | codePoint;
                    if (_charToGlyph.TryGetValue(cmapKey, out gid) && gid > 0 && gid < _numGlyphs) return true;
                }

                cmapKey = codePoint;
                gid = 0;
                return false;
            }

            // Selected glyph IDs (old IDs), sorted
            private List<int> _selectedGlyphs;
            // old ID → new ID
            private Dictionary<int, int> _oldToNew = new Dictionary<int, int>();

            internal TtfSubsetter(byte[] src)
            {
                _src = src;
                ParseOffsetTable();
            }

            internal byte[] Subset(HashSet<int> requestedCodePoints)
            {
                if (!_tables.ContainsKey("glyf"))
                    throw new NotSupportedException(
                        "Font does not contain a 'glyf' table. This subsetter rebuilds glyf outlines. " +
                        "For CFF/OpenType outlines use PdfFontSubsetter.TrySubsetCff, which blanks " +
                        "unused charstrings in place, or PdfFontSubsetter.TryCompact to strip the " +
                        "tables a PDF reader never consults, or embed the full font.");

                // The tables the subset is rebuilt from must hold the fields it rewrites: head's
                // indexToLocFormat at 50, hhea's numberOfHMetrics at 34, maxp's numGlyphs at 4. A
                // shorter one, from a hostile or truncated font, was read as zeros and then written
                // into past its end.
                RequireLength("head", 54);
                RequireLength("hhea", 36);
                RequireLength("maxp", 6);

                ParseHead();
                ParseMaxp();
                ParseHhea();
                ParseLoca();
                if (_numGlyphs == 0)
                    throw new Core.PdfException(
                        "Font has no glyph its 'loca' table can locate, so there is nothing to subset.");
                ParseHmtx();
                ParseCmap();

                // Build the set of needed glyph IDs.
                var neededGlyphs = new HashSet<int> { 0 }; // always include .notdef
                int unmapped = 0;
                foreach (var cp in requestedCodePoints)
                    if (TryGlyph(cp, out int gid, out _))
                        neededGlyphs.Add(gid);
                    else
                        unmapped++;

                // A subset that maps none of the requested characters is a valid font full of
                // .notdef, which looks exactly like a legitimately tiny subset from the outside.
                if (unmapped > 0)
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"{unmapped} of {requestedCodePoints.Count} requested characters are not " +
                        "in the font's cmap, or map to a glyph it does not have, and will render as .notdef.");

                // Collect composite components recursively. A component past numGlyphs is dropped
                // (FixCompositeGlyph then points it at .notdef): it indexed loca out of range.
                var toProcess = new Queue<int>(neededGlyphs);
                int missingComponents = 0;
                while (toProcess.Count > 0)
                {
                    int gid = toProcess.Dequeue();
                    foreach (int comp in GetCompositeComponents(gid))
                    {
                        if (comp >= _numGlyphs) { missingComponents++; continue; }
                        if (neededGlyphs.Add(comp))
                            toProcess.Enqueue(comp);
                    }
                }
                if (missingComponents > 0)
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"{missingComponents} composite glyph component(s) name a glyph the font does " +
                        "not have; they draw as .notdef.");

                _selectedGlyphs = neededGlyphs.OrderBy(g => g).ToList();
                for (int i = 0; i < _selectedGlyphs.Count; i++)
                    _oldToNew[_selectedGlyphs[i]] = i;

                return BuildSubsetFont(requestedCodePoints);
            }

            // ---- Parse source font ----

            private void ParseOffsetTable()
            {
                var r = new BigEndianReader(_src, 0);
                uint sfVersion = r.ReadUInt32();
                // Accept 0x00010000 (TrueType), 0x74727565 ('true'), 0x74797031 ('typ1')
                // CFF fonts have 'OTTO' (0x4F54544F) — we'll detect that later.
                int numTables = r.ReadUInt16();
                r.Skip(6); // searchRange, entrySelector, rangeShift

                // numTables, and every offset and length below, come straight from the file. A
                // 40-byte font declaring numTables = 1000 would die with
                // IndexOutOfRangeException here, and an entry claiming length 0xFFFFFFFF size a
                // 4 GB allocation in ParseLoca. Stop at the buffer's end and drop any entry that
                // does not lie inside the file, so no later read can be steered outside it.
                for (int i = 0; i < numTables; i++)
                {
                    if (!r.CanRead(TableDirectoryEntryBytes)) break;
                    string tag = r.ReadTag();
                    uint checksum = r.ReadUInt32();
                    uint offset   = r.ReadUInt32();
                    uint length   = r.ReadUInt32();
                    if (string.IsNullOrEmpty(tag)) continue;
                    if (offset > _src.Length)
                    {
                        PdfFontSubsetter.ReportSubsetIssue(
                            $"the '{tag}' table starts at {offset}, outside the {_src.Length}-byte " +
                            "file; it was ignored.");
                        continue;
                    }
                    if ((long)offset + length > _src.Length)
                    {
                        // Clamped rather than dropped: a merely sloppy length must not cost the
                        // caller the whole table, and clamping is what bounds the allocation in
                        // ParseLoca (`new byte[0xFFFFFFFF]` was an OutOfMemoryException, or a
                        // negative-count Array.Copy after the (int) cast).
                        uint clamped = (uint)(_src.Length - offset);
                        PdfFontSubsetter.ReportSubsetIssue(
                            $"the '{tag}' table claims length {length} but only {clamped} bytes " +
                            "remain in the file; it was clamped.");
                        length = clamped;
                    }
                    _tables[tag] = (offset, length);
                }
            }

            private void ParseHead()
            {
                var (off, _) = GetTable("head");
                var r = new BigEndianReader(_src, (int)off);
                r.Skip(4); // version
                r.Skip(4); // fontRevision
                r.Skip(4); // checkSumAdjustment
                r.Skip(4); // magicNumber
                r.Skip(2); // flags
                _unitsPerEm = r.ReadUInt16();
                r.Skip(16); // created + modified
                r.Skip(8);  // xMin yMin xMax yMax
                r.Skip(2);  // macStyle
                r.Skip(2);  // lowestRecPPEM
                r.Skip(2);  // fontDirectionHint
                _indexToLocFormat = r.ReadInt16(); // 0=short, 1=long
            }

            private void ParseMaxp()
            {
                var (off, _) = GetTable("maxp");
                var r = new BigEndianReader(_src, (int)off);
                r.Skip(4); // version
                _numGlyphs = r.ReadUInt16();
            }

            private void ParseHhea()
            {
                var (off, _) = GetTable("hhea");
                var r = new BigEndianReader(_src, (int)off);
                r.Skip(4);  // version
                _ascent  = r.ReadInt16();
                _descent = r.ReadInt16();
                r.Skip(2);  // lineGap
                r.Skip(2);  // advanceWidthMax
                r.Skip(2);  // minLeftSideBearing
                r.Skip(2);  // minRightSideBearing
                r.Skip(2);  // xMaxExtent
                r.Skip(2);  // caretSlopeRise
                r.Skip(2);  // caretSlopeRun
                r.Skip(2);  // caretOffset
                r.Skip(8);  // reserved (4×int16)
                r.Skip(2);  // metricDataFormat
                _numberOfHMetrics = r.ReadUInt16();
            }

            private void ParseLoca()
            {
                var (off, len) = GetTable("loca");

                // maxp's numGlyphs is file-supplied; loca must hold numGlyphs+1 entries, so a
                // font declaring 65535 glyphs with a 4-byte loca would allocate and read far past
                // the table.
                int entryBytes = _indexToLocFormat == 0 ? 2 : 4;
                int locaCapacity = Math.Max(0, (int)(Math.Min(len, (uint)Math.Max(0, _src.Length - (long)off)) / entryBytes) - 1);
                if (_numGlyphs > locaCapacity)
                {
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"maxp declares {_numGlyphs} glyphs but loca holds only {locaCapacity}; " +
                        "the glyph count was clamped.");
                    _numGlyphs = locaCapacity;
                }

                _locaOffsets = new long[_numGlyphs + 1];
                var r = new BigEndianReader(_src, (int)off);
                if (_indexToLocFormat == 0) // short: offsets are in units of 2 bytes
                    for (int i = 0; i <= _numGlyphs; i++)
                        _locaOffsets[i] = r.ReadUInt16() * 2L;
                else
                    for (int i = 0; i <= _numGlyphs; i++)
                        _locaOffsets[i] = r.ReadUInt32();

                // ParseOffsetTable has already dropped any table whose offset+length falls outside
                // the file, so this copy is in range; the clamp is belt-and-braces for a font
                // whose glyf entry was dropped entirely.
                var (glyfOff, glyfLen) = GetTable("glyf");
                int copyable = (int)Math.Min(glyfLen, (uint)Math.Max(0, _src.Length - (long)glyfOff));
                _glyfData = new byte[copyable];
                if (copyable > 0) Array.Copy(_src, (int)glyfOff, _glyfData, 0, copyable);

                // Every loca offset inside glyf. An offset past its end — a hostile loca, or a glyf
                // the directory clamped — made the subset's glyph copy read past the array and throw.
                // Clamped, such a glyph is empty, and an offset before its predecessor already is.
                int clamped = 0;
                for (int i = 0; i < _locaOffsets.Length; i++)
                    if (_locaOffsets[i] > _glyfData.Length) { _locaOffsets[i] = _glyfData.Length; clamped++; }
                if (clamped > 0)
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"{clamped} 'loca' offset(s) point past the {_glyfData.Length}-byte 'glyf' table; " +
                        "those glyphs were emptied.");
            }

            /// <summary>Refuses a required table shorter than the fields the subset rewrites in it.</summary>
            private void RequireLength(string tag, uint minimum)
            {
                var (_, length) = GetTable(tag);
                if (length < minimum)
                    throw new Core.PdfException(
                        $"Font's '{tag}' table is {length} bytes; the subset rewrites a field within its first {minimum}.");
            }

            private void ParseHmtx()
            {
                var (off, _) = GetTable("hmtx");
                _advanceWidths = new ushort[_numGlyphs];
                _leftBearings  = new short[_numGlyphs];
                var r = new BigEndianReader(_src, (int)off);
                ushort lastWidth = 0;
                for (int i = 0; i < _numGlyphs; i++)
                {
                    if (i < _numberOfHMetrics)
                    {
                        lastWidth = (ushort)r.ReadUInt16();
                        _advanceWidths[i] = lastWidth;
                        _leftBearings[i]  = (short)r.ReadInt16();
                    }
                    else
                    {
                        _advanceWidths[i] = lastWidth;
                        _leftBearings[i]  = (short)r.ReadInt16();
                    }
                }
            }

            private void ParseCmap()
            {
                var (off, _) = GetTable("cmap");
                var r = new BigEndianReader(_src, (int)off);
                r.Skip(2); // version
                int numSubtables = r.ReadUInt16();

                // Prefer platform 3 (Windows) encoding 1 (Unicode BMP), then platform 0.
                uint bestOffset = 0;
                int  bestPriority = -1;
                for (int i = 0; i < numSubtables; i++)
                {
                    int platformId = r.ReadUInt16();
                    int encodingId = r.ReadUInt16();
                    uint subtableOff = r.ReadUInt32();
                    // (3,10) is Windows UCS-4, which is where a format-12 subtable lives, and it
                    // is preferred over (3,1): it covers everything (3,1) does plus the astral
                    // planes. It scored -1 here, so a font whose ONLY Unicode cmap was (3,10) —
                    // normal for anything with astral coverage — matched nothing, reported "no
                    // Windows or Unicode subtable" and subsetted to .notdef alone.
                    int priority = -1;
                    if (platformId == 3 && encodingId == 10)     priority = 12;
                    else if (platformId == 3 && encodingId == 1) priority = 10;
                    else if (platformId == 0)                    priority = 5;
                    // (3,0) is Windows SYMBOL, which is what marlett, symbol, wingdings and
                    // webdings actually carry -- alongside a (1,0) format-0 table that this
                    // subsetter cannot read. Scoring it -1 meant the format-0 table won at
                    // priority 1 and the font was refused outright: 192 glyphs in, 1 out,
                    // measured on symbol.ttf and wingding.ttf. It scores above platform 1 and
                    // below the Unicode tiers, which is exactly what it is: a real table, but not
                    // a Unicode one.
                    else if (platformId == 3 && encodingId == 0) priority = 3;
                    else if (platformId == 1)                    priority = 1;
                    if (priority > bestPriority)
                    {
                        bestPriority = priority;
                        bestOffset = subtableOff;
                        _symbolicCmap = platformId == 3 && encodingId == 0;
                    }
                }

                if (bestOffset == 0)
                {
                    PdfFontSubsetter.ReportSubsetIssue(
                        "the font's cmap has no Windows or Unicode subtable, so no character " +
                        "could be mapped; the subset contains only .notdef.");
                    return;
                }

                int subtableAbsOff = (int)off + (int)bestOffset;
                var sr = new BigEndianReader(_src, subtableAbsOff);
                int format = sr.ReadUInt16();
                if (format == 12)
                {
                    ParseCmapFormat12(sr, subtableAbsOff);
                    return;
                }

                if (format == 0)
                {
                    ParseCmapFormat0(sr, subtableAbsOff);
                    return;
                }

                if (format == 6)
                {
                    ParseCmapFormat6(sr, subtableAbsOff);
                    return;
                }

                if (format != 4)
                {
                    // Silence here produced a valid-looking font with an empty cmap; the caller saw
                    // only a suspiciously small byte array. Formats 2 and 13 remain unread, so they
                    // are still named rather than passed over.
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"the font's only Unicode cmap is format {format}, and this subsetter " +
                        "reads formats 0, 4, 6 and 12, so no character was mapped; the subset " +
                        "contains only .notdef. Embed the full font instead.");
                    return;
                }

                sr.Skip(2); // length
                sr.Skip(2); // language
                int segCount = sr.ReadUInt16() / 2;
                sr.Skip(6); // searchRange, entrySelector, rangeShift

                // segCount is file-supplied: 32767 segments each spanning 0..0xFFFF is ~2 billion
                // iterations in the expansion loop below. Bound the arrays by what the buffer can
                // actually hold — four uint16 arrays plus the pad follow the header.
                int maxSegments = Math.Max(0, (_src.Length - (subtableAbsOff + 16)) / 8);
                if (segCount > maxSegments)
                {
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"the cmap declares {segCount} segments but only {maxSegments} fit in the " +
                        "file; the tail was ignored.");
                    segCount = maxSegments;
                }
                if (segCount <= 0) return;

                int[] endCodes    = new int[segCount];
                int[] startCodes  = new int[segCount];
                int[] idDeltas    = new int[segCount];
                int[] idRangeOff  = new int[segCount];
                for (int i = 0; i < segCount; i++) endCodes[i]   = sr.ReadUInt16();
                sr.Skip(2); // reservedPad
                for (int i = 0; i < segCount; i++) startCodes[i] = sr.ReadUInt16();
                for (int i = 0; i < segCount; i++) idDeltas[i]   = sr.ReadInt16();
                int idRangeOffBase = subtableAbsOff + 14 + 2 + segCount * 3 * 2;
                for (int i = 0; i < segCount; i++) idRangeOff[i] = sr.ReadUInt16();

                // Read glyphIdArray (follows immediately after idRangeOffset).
                int glyphIdArrayBase = subtableAbsOff + 14 + 2 + segCount * 4 * 2;

                // Total code points expanded across all segments. A legal BMP cmap cannot exceed
                // 65536; a hostile one declares overlapping segments that sum to billions.
                int expansionBudget = MaxCmapExpansion;

                for (int seg = 0; seg < segCount - 1; seg++) // last segment is 0xFFFF terminator
                {
                    for (int c = startCodes[seg]; c <= endCodes[seg]; c++)
                    {
                        if (--expansionBudget < 0)
                        {
                            PdfFontSubsetter.ReportSubsetIssue(
                                $"the cmap expands to more than {MaxCmapExpansion} code points, " +
                                "which no legal BMP cmap does; the walk stopped there and some " +
                                "characters may be unmapped.");
                            return;
                        }
                        int gid;
                        if (idRangeOff[seg] == 0)
                        {
                            gid = (c + idDeltas[seg]) & 0xFFFF;
                        }
                        else
                        {
                            // glyphIdArray index
                            int rangeOffPos = idRangeOffBase + seg * 2;
                            int glyphIdOffset = rangeOffPos + idRangeOff[seg] + (c - startCodes[seg]) * 2;
                            var gr = new BigEndianReader(_src, glyphIdOffset);
                            int rawGid = gr.ReadUInt16();
                            gid = rawGid == 0 ? 0 : (rawGid + idDeltas[seg]) & 0xFFFF;
                        }
                        if (gid != 0)
                            _charToGlyph[c] = gid;
                    }
                }
            }

            /// <summary>
            /// Reads a format-0 (byte encoding) cmap subtable: a 256-entry glyph index array.
            /// </summary>
            /// <remarks>
            /// <para><b>Not reading this refused whole fonts.</b> An older symbol or icon face whose
            /// only <c>cmap</c> is <c>(1,0)</c> Macintosh Roman, format 0 was selected by the
            /// priority scan and then rejected by the format test — reported as "no character was
            /// mapped" and subsetted to <c>.notdef</c> alone. A one-glyph font is not a subset; it
            /// is a blank page where the text was.</para>
            ///
            /// <para>A <b>truncated</b> array yields the prefix that is present, matching
            /// <c>TrueTypeFontProgram.ParseCmap0</c> and <c>PdfTrueTypeFont</c> — the two readers on
            /// the other side of this same table. The entries are independent, so the codes that
            /// are there are still correct.</para>
            /// </remarks>
            private void ParseCmapFormat0(BigEndianReader sr, int subtableAbsOff)
            {
                sr.Skip(2); // length
                sr.Skip(2); // language

                int start = subtableAbsOff + 6;
                int available = Math.Min(256, _src.Length - start);
                if (available <= 0)
                {
                    PdfFontSubsetter.ReportSubsetIssue(
                        "the font's format-0 cmap has no glyph index array; no character was mapped.");
                    return;
                }

                for (int c = 0; c < available; c++)
                {
                    int gid = _src[start + c];
                    if (gid != 0) _charToGlyph[c] = gid;
                }

                if (available < 256)
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"the font's format-0 cmap declares 256 entries but only {available} fit in " +
                        "the file; the codes that are present were mapped.");
            }

            /// <summary>
            /// Reads a format-6 (trimmed table mapping) cmap subtable: a contiguous run of
            /// <c>entryCount</c> codes starting at <c>firstCode</c>.
            /// </summary>
            /// <remarks>
            /// Both header fields are file-supplied, so the run is clamped to what the buffer can
            /// actually hold before anything is read — the same treatment format 4's
            /// <c>segCount</c> and format 12's <c>numGroups</c> already get.
            /// </remarks>
            private void ParseCmapFormat6(BigEndianReader sr, int subtableAbsOff)
            {
                sr.Skip(2); // length
                sr.Skip(2); // language

                int firstCode  = sr.ReadUInt16();
                int entryCount = sr.ReadUInt16();

                int arrayBase = subtableAbsOff + 10;
                int available = Math.Max(0, (_src.Length - arrayBase) / 2);
                if (entryCount > available)
                {
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"the font's format-6 cmap declares {entryCount} entries but only " +
                        $"{available} fit in the file; the tail was ignored.");
                    entryCount = available;
                }

                var gr = new BigEndianReader(_src, arrayBase);
                for (int i = 0; i < entryCount; i++)
                {
                    int gid = gr.ReadUInt16();
                    if (gid != 0) _charToGlyph[firstCode + i] = gid;
                }
            }

            /// <summary>
            /// Reads a format-12 (segmented coverage) cmap subtable — the one any font with astral
            /// coverage uses, and the one whose absence made such fonts unsubsettable.
            /// </summary>
            /// <remarks>
            /// <para>Layout after the format field: <c>reserved</c> (uint16), <c>length</c>,
            /// <c>language</c>, <c>numGroups</c> (all uint32), then <c>numGroups</c> groups of
            /// <c>startCharCode</c>, <c>endCharCode</c>, <c>startGlyphID</c>.</para>
            /// <para><b>Both bounds are load-bearing and both are file-controlled.</b>
            /// <c>numGroups</c> is declared, so it is checked against what the file can actually
            /// hold before any array is sized; and a single group may legally span
            /// <c>0..0x10FFFF</c>, so the expansion is charged against the same budget the format-4
            /// walk uses. A hostile font declaring a few overlapping full-plane groups would
            /// otherwise expand to billions of dictionary writes from a few dozen bytes.</para>
            /// </remarks>
            private void ParseCmapFormat12(BigEndianReader sr, int subtableAbsOff)
            {
                sr.Skip(2);              // reserved
                sr.Skip(4);              // length
                sr.Skip(4);              // language
                long numGroups = sr.ReadUInt32();

                // 12 bytes per group, following the 16-byte header.
                long maxGroups = Math.Max(0, (_src.Length - (subtableAbsOff + 16)) / 12);
                if (numGroups > maxGroups)
                {
                    PdfFontSubsetter.ReportSubsetIssue(
                        $"the format-12 cmap declares {numGroups} groups but only {maxGroups} fit " +
                        "in the file; the tail was ignored.");
                    numGroups = maxGroups;
                }
                if (numGroups <= 0) return;

                int expansionBudget = MaxCmapExpansion;

                for (long g = 0; g < numGroups; g++)
                {
                    long startChar = sr.ReadUInt32();
                    long endChar   = sr.ReadUInt32();
                    long startGid  = sr.ReadUInt32();

                    // A group whose end precedes its start, or which runs past the Unicode range,
                    // is malformed. Clamping rather than throwing keeps a partly-usable font
                    // usable, and the budget report below is what tells the caller it is partial.
                    if (endChar < startChar) continue;
                    if (endChar > 0x10FFFF) endChar = 0x10FFFF;

                    for (long c = startChar; c <= endChar; c++)
                    {
                        if (--expansionBudget < 0)
                        {
                            PdfFontSubsetter.ReportSubsetIssue(
                                $"the format-12 cmap expands to more than {MaxCmapExpansion} code " +
                                "points; the walk stopped there and some characters may be unmapped.");
                            return;
                        }

                        long gid = startGid + (c - startChar);
                        if (gid > 0 && gid < _numGlyphs && c <= int.MaxValue)
                            _charToGlyph[(int)c] = (int)gid;
                    }
                }
            }

            // ---- Composite glyph handling ----

            private IEnumerable<int> GetCompositeComponents(int glyph)
            {
                if (glyph >= _numGlyphs) yield break;
                long start = _locaOffsets[glyph];
                long end   = _locaOffsets[glyph + 1];
                if (end <= start) yield break; // empty glyph

                // Bounded by the glyph's own end (Core.Fonts.CompositeGlyph's remarks): this read on
                // into the next glyph while a malformed record kept MORE_COMPONENTS set, and the
                // subset carried whatever glyphs those bytes named.
                foreach (int at in Core.Fonts.CompositeGlyph.ComponentIndexOffsets(_glyfData, (int)start, (int)end))
                    yield return Core.Fonts.CompositeGlyph.ReadIndex(_glyfData, at);
            }

            // ---- Build subset font ----

            private byte[] BuildSubsetFont(HashSet<int> requestedCodePoints)
            {
                int newNumGlyphs = _selectedGlyphs.Count;

                // Build new glyf + loca.
                var newGlyfStream = new MemoryStream();
                var newLocaOffsets = new long[newNumGlyphs + 1];
                for (int ni = 0; ni < newNumGlyphs; ni++)
                {
                    newLocaOffsets[ni] = newGlyfStream.Length;
                    int oldGid = _selectedGlyphs[ni];
                    long oldStart = _locaOffsets[oldGid];
                    long oldEnd   = _locaOffsets[oldGid + 1];
                    if (oldEnd > oldStart)
                    {
                        byte[] glyphBytes = new byte[oldEnd - oldStart];
                        Array.Copy(_glyfData, (int)oldStart, glyphBytes, 0, glyphBytes.Length);
                        FixCompositeGlyph(glyphBytes);
                        newGlyfStream.Write(glyphBytes, 0, glyphBytes.Length);
                        // Align to 4 bytes.
                        while (newGlyfStream.Length % 4 != 0) newGlyfStream.WriteByte(0);
                    }
                }
                newLocaOffsets[newNumGlyphs] = newGlyfStream.Length;
                byte[] newGlyfBytes = newGlyfStream.ToArray();

                // Choose loca format: use long if any offset > 0x1FFFE.
                int newLocaFormat = newLocaOffsets[newNumGlyphs] > 0x1FFFE ? 1 : 0;

                // Build new hmtx.
                int newNumHMetrics = Math.Min(newNumGlyphs, _numberOfHMetrics > 0 ? newNumGlyphs : 1);
                var newHmtxStream = new MemoryStream();
                var hmtxWriter = new BigEndianWriter(newHmtxStream);
                for (int ni = 0; ni < newNumGlyphs; ni++)
                {
                    int oldGid = _selectedGlyphs[ni];
                    hmtxWriter.WriteUInt16(_advanceWidths[Math.Min(oldGid, _advanceWidths.Length - 1)]);
                    hmtxWriter.WriteInt16(_leftBearings[Math.Min(oldGid, _leftBearings.Length - 1)]);
                }
                byte[] newHmtxBytes = newHmtxStream.ToArray();

                // Build new cmap.
                byte[] newCmapBytes = BuildCmap(requestedCodePoints);

                // Build new head.
                byte[] newHeadBytes = BuildHead(newLocaFormat);

                // Build new hhea.
                byte[] newHheaBytes = BuildHhea(newNumGlyphs);

                // Build new maxp.
                byte[] newMaxpBytes = BuildMaxp(newNumGlyphs);

                // Build new loca.
                byte[] newLocaBytes = BuildLoca(newLocaOffsets, newLocaFormat);

                // Collect tables to include.
                var newTables = new Dictionary<string, byte[]>();
                newTables["cmap"] = newCmapBytes;
                newTables["glyf"] = newGlyfBytes;
                newTables["head"] = newHeadBytes;
                newTables["hhea"] = newHheaBytes;
                newTables["hmtx"] = newHmtxBytes;
                newTables["loca"] = newLocaBytes;
                newTables["maxp"] = newMaxpBytes;

                // Copy pass-through tables.
                string[] passthroughTags = { "OS/2", "name", "post", "cvt ", "fpgm", "prep", "gasp" };
                foreach (var tag in passthroughTags)
                    if (_tables.ContainsKey(tag))
                    {
                        var (off, len) = _tables[tag];
                        var tb = new byte[len];
                        Array.Copy(_src, (int)off, tb, 0, (int)len);
                        newTables[tag] = tb;
                    }

                return AssembleFont(newTables);
            }

            // The `: 0` fallback below is for the one component the closure in `Subset` leaves out:
            // a glyph id past numGlyphs, which it drops rather than index loca with. Every other
            // component id this loop can read was added to `_oldToNew` by that closure, which reads
            // the same ids from the same bytes through the same `CompositeGlyph` helpers.
            private void FixCompositeGlyph(byte[] glyphBytes)
            {
                foreach (int at in Core.Fonts.CompositeGlyph.ComponentIndexOffsets(glyphBytes, 0, glyphBytes.Length))
                {
                    int oldCompId = Core.Fonts.CompositeGlyph.ReadIndex(glyphBytes, at);
                    Core.Fonts.CompositeGlyph.WriteIndex(glyphBytes, at,
                        _oldToNew.TryGetValue(oldCompId, out int nid) ? nid : 0);
                }
            }

            /// <summary>
            /// A format-4 subtable's length: seven 16-bit header fields, four arrays of
            /// <paramref name="segCount"/> 16-bit entries, and the 16-bit <c>reservedPad</c> between
            /// the first two arrays — 16 + 8 × segCount.
            /// </summary>
            /// <remarks>
            /// It was 14 + 8 × segCount: the pad was written and not counted, so every subset
            /// declared its cmap two bytes short, and a reader that trusts the field loses the last
            /// <c>idRangeOffset</c>. A second subtable placed after it lands two bytes early.
            /// </remarks>
            private static int Format4Length(int segCount) => 16 + 8 * segCount;

            private byte[] BuildCmap(HashSet<int> requestedCodePoints)
            {
                // Build char→newGlyph mapping for requested code points only.
                var charGlyphPairs = new SortedDictionary<int, int>();
                foreach (var cp in requestedCodePoints)
                    if (TryGlyph(cp, out int oldGid, out int cmapKey)
                        && _oldToNew.TryGetValue(oldGid, out int newGid))
                        charGlyphPairs[cmapKey] = newGid;

                // Build format 4 segments, coalescing runs that share one idDelta.
                //
                // One segment per character was the simple shape, and it overflowed: the subtable
                // writes its own length into a uint16, and `16 + 8 * segCount` (Format4Length)
                // passes 65535 at 8190 segments. Subsetting a CJK font for 9 000 distinct characters produced a
                // 72 KB subtable declaring length 6478, so a conformant rasteriser that trusts the
                // field read a truncated segment array and most characters mapped to the wrong
                // glyph — a document rendered with the wrong ideographs rather than a failure.
                // Coalescing also shrinks the table by an order of magnitude for real text, since
                // subset glyph ids are assigned in code-point order and therefore run contiguously.
                var segs = new List<(int start, int end, int delta)>();
                foreach (var kvp in charGlyphPairs)
                {
                    int cp = kvp.Key, gid = kvp.Value;
                    if (cp > 0xFFFF) continue;   // astral: the format-12 subtable below carries it
                    int delta = (gid - cp) & 0xFFFF;
                    if (segs.Count > 0)
                    {
                        var last = segs[segs.Count - 1];
                        if (last.delta == delta && last.end == cp - 1)
                        {
                            segs[segs.Count - 1] = (last.start, cp, delta);
                            continue;
                        }
                    }
                    segs.Add((cp, cp, delta));
                }

                // 0xFFFF must be the last segment's endCode; a coalesced run that already reaches
                // it must not be followed by a second terminator.
                if (segs.Count == 0 || segs[segs.Count - 1].end != 0xFFFF)
                    segs.Add((0xFFFF, 0xFFFF, 1)); // terminator

                // Still too many segments to describe in a uint16 length field. Refusing is the
                // only honest option: a truncated declared length is silent corruption.
                if (Format4Length(segs.Count) > ushort.MaxValue)
                    throw new NotSupportedException(
                        $"The subset needs {segs.Count} cmap segments, whose format-4 subtable " +
                        $"would be {Format4Length(segs.Count)} bytes — more than the uint16 length field " +
                        "can express. Embed the full font instead.");

                int segCount = segs.Count;
                int subtableLength = Format4Length(segCount);

                // An astral character needs a format-12 subtable under (3,10): format 4 holds 16-bit
                // codes only, and its (ushort) casts wrote U+1F600 as U+F600 — the emoji lost and a
                // private-use code mapped to its glyph. Format 12 carries every pair, BMP included,
                // so a reader preferring (3,10) needs nothing else; (3,1) stays for one that does not.
                var groups = new List<(int start, int end, int startGid)>();
                if (charGlyphPairs.Keys.Any(k => k > 0xFFFF))
                {
                    foreach (var kvp in charGlyphPairs)
                    {
                        if (groups.Count > 0)
                        {
                            var last = groups[groups.Count - 1];
                            if (last.end == kvp.Key - 1 && last.startGid + (kvp.Key - last.start) == kvp.Value)
                            {
                                groups[groups.Count - 1] = (last.start, kvp.Key, last.startGid);
                                continue;
                            }
                        }
                        groups.Add((kvp.Key, kvp.Key, kvp.Value));
                    }
                }
                int subtables = groups.Count > 0 ? 2 : 1;

                var ms = new MemoryStream();
                var w = new BigEndianWriter(ms);

                // cmap header
                w.WriteUInt16(0); // version
                w.WriteUInt16((ushort)subtables);
                // subtable record: platform 3, encoding 1 (Windows Unicode BMP)
                w.WriteUInt16(3); // platformID
                // encodingID follows the SOURCE: a subset of a symbolic font must stay
                // symbolic, or a reader applying §9.6.6.4 looks for a (3,0) table, finds a
                // (3,1) one claiming Unicode BMP for PUA codes, and takes the non-symbolic
                // path instead.
                w.WriteUInt16(_symbolicCmap ? (ushort)0 : (ushort)1); // encodingID
                int format4At = 4 + 8 * subtables;
                w.WriteUInt32((uint)format4At); // offset to subtable (past the header and the records)
                if (subtables == 2)
                {
                    // Records sorted by platform then encoding: (3,10) after (3,1).
                    w.WriteUInt16(3);
                    w.WriteUInt16(10);
                    w.WriteUInt32((uint)(format4At + subtableLength));
                }

                // Format 4 subtable
                int segCountX2 = segCount * 2;
                int sr2 = SaturatingCast.ToInt(Math.Pow(2, Math.Floor(Math.Log(segCount) / Math.Log(2)))) * 2;
                int entrySelector = SaturatingCast.ToInt(Math.Log(segCount) / Math.Log(2));
                // Clamp entrySelector if segCount == 1
                if (segCount == 1) { sr2 = 2; entrySelector = 0; }
                int rangeShift = segCountX2 - sr2;

                w.WriteUInt16(4);              // format
                w.WriteUInt16((ushort)subtableLength);
                w.WriteUInt16(0);              // language
                w.WriteUInt16((ushort)segCountX2);
                w.WriteUInt16((ushort)sr2);
                w.WriteUInt16((ushort)entrySelector);
                w.WriteUInt16((ushort)rangeShift);

                foreach (var seg in segs) w.WriteUInt16((ushort)seg.end);
                w.WriteUInt16(0); // reservedPad
                foreach (var seg in segs) w.WriteUInt16((ushort)seg.start);
                foreach (var seg in segs) w.WriteInt16((short)(seg.delta));
                foreach (var seg in segs) w.WriteUInt16(0); // idRangeOffset = 0 (use delta)
                // no glyphIdArray needed

                if (groups.Count > 0)
                {
                    // Format 12: format, reserved, length, language, numGroups, then the groups.
                    w.WriteUInt16(12);
                    w.WriteUInt16(0);
                    w.WriteUInt32((uint)(16 + 12 * groups.Count));
                    w.WriteUInt32(0);
                    w.WriteUInt32((uint)groups.Count);
                    foreach (var g in groups)
                    {
                        w.WriteUInt32((uint)g.start);
                        w.WriteUInt32((uint)g.end);
                        w.WriteUInt32((uint)g.startGid);
                    }
                }

                return ms.ToArray();
            }

            private byte[] BuildHead(int newLocaFormat)
            {
                if (!_tables.TryGetValue("head", out var t)) return Array.Empty<byte>();
                byte[] head = new byte[t.length];
                Array.Copy(_src, (int)t.offset, head, 0, (int)t.length);
                // Update indexToLocFormat (offset 50) and checkSumAdjustment (offset 8).
                WriteInt16BE(head, 50, (short)newLocaFormat);
                // Set checkSumAdjustment to 0 — will be fixed in AssembleFont.
                WriteUInt32BE(head, 8, 0);
                return head;
            }

            private byte[] BuildHhea(int newNumGlyphs)
            {
                if (!_tables.TryGetValue("hhea", out var t)) return Array.Empty<byte>();
                byte[] hhea = new byte[t.length];
                Array.Copy(_src, (int)t.offset, hhea, 0, (int)t.length);
                // Update numberOfHMetrics (offset 34) to newNumGlyphs.
                WriteUInt16BE(hhea, 34, (ushort)newNumGlyphs);
                return hhea;
            }

            private byte[] BuildMaxp(int newNumGlyphs)
            {
                if (!_tables.TryGetValue("maxp", out var t)) return Array.Empty<byte>();
                byte[] maxp = new byte[t.length];
                Array.Copy(_src, (int)t.offset, maxp, 0, (int)t.length);
                // Update numGlyphs (offset 4).
                WriteUInt16BE(maxp, 4, (ushort)newNumGlyphs);
                return maxp;
            }

            private static byte[] BuildLoca(long[] offsets, int locaFormat)
            {
                int n = offsets.Length;
                var ms = new MemoryStream(n * (locaFormat == 0 ? 2 : 4));
                var w = new BigEndianWriter(ms);
                if (locaFormat == 0)
                    foreach (var o in offsets) w.WriteUInt16((ushort)(o / 2));
                else
                    foreach (var o in offsets) w.WriteUInt32((uint)o);
                return ms.ToArray();
            }

            /// <summary>
            /// Lays the subsetted tables out as a TrueType container.
            /// </summary>
            /// <remarks>
            /// <para>One implementation, in <see cref="Core.Fonts.SfntBuilder"/>, which sorts the table
            /// directory with <c>StringComparer.Ordinal</c>, never the culture-sensitive default
            /// comparer. §5.2 requires ascending <i>byte</i> order, which is what
            /// <c>searchRange</c>/<c>entrySelector</c>/<c>rangeShift</c> exist to let a reader
            /// binary-search; the default comparer would sort <c>OS/2</c> between <c>name</c> and
            /// <c>post</c> instead of first, so essentially every subset would carry a mis-ordered
            /// directory. A linear-scanning reader finds the tables anyway and a binary-searching one
            /// may not, which is the "renders in most viewers and fails validation in others" failure
            /// <see cref="Core.Fonts.SfntBuilder"/>'s own remarks name.</para>
            ///
            /// <para>The shared builder derives the directory hints by integer arithmetic, which cannot
            /// land a power of two on the wrong side of a <c>Math.Floor</c> as <c>Math.Log</c> can.</para>
            /// </remarks>
            private static byte[] AssembleFont(Dictionary<string, byte[]> tables)
            {
                var list = new List<(string tag, byte[] data)>(tables.Count);
                foreach (var kvp in tables) list.Add((kvp.Key, kvp.Value));

            // head.checkSumAdjustment is already zeroed upstream (see BuildHead). The order
            // matters, for a different sum than the whole-file one: SfntBuilder writes
            // head's DIRECTORY entry from the bytes as they arrive, so every caller that hands
            // it a verbatim head -- SfntTablePruner, CffSubsetter, TrueTypeGlyphPruner -- must zero
            // the field first, or the checksum is wrong by exactly the source font's adjustment.
                return Core.Fonts.SfntBuilder.Build(Core.Fonts.SfntBuilder.VersionTrueType, list);
            }

            // ---- Low-level helpers ----

            private (uint offset, uint length) GetTable(string tag)
            {
                if (!_tables.TryGetValue(tag, out var t))
                    throw new Core.PdfException($"Font is missing required table '{tag}'.");
                return t;
            }

            private static void WriteUInt16BE(byte[] data, int pos, ushort value)
            { data[pos] = (byte)(value >> 8); data[pos + 1] = (byte)(value & 0xFF); }

            private static void WriteInt16BE(byte[] data, int pos, short value)
                => WriteUInt16BE(data, pos, (ushort)value);

            private static void WriteUInt32BE(byte[] data, int pos, uint value)
            {
                data[pos]     = (byte)(value >> 24);
                data[pos + 1] = (byte)(value >> 16);
                data[pos + 2] = (byte)(value >>  8);
                data[pos + 3] = (byte)(value & 0xFF);
            }
        }

        // ----------------------------------------------------------------
        //  Big-endian reader / writer helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Big-endian reader over a font file, <b>bounds-checked on every read</b>: a read past the
        /// end yields 0 (or an empty tag) and advances the cursor, rather than throwing
        /// <see cref="IndexOutOfRangeException"/> out of the public <c>Subset</c> API. Every offset
        /// and count that steers it comes from the file, so a truncated or hostile font must
        /// produce a degraded subset and a reported reason, not a fault.
        /// </summary>
        private sealed class BigEndianReader
        {
            private readonly byte[] _data;
            private int _pos;

            internal BigEndianReader(byte[] data, int startPos)
            { _data = data ?? Array.Empty<byte>(); _pos = startPos; }

            internal bool CanRead(int count) =>
                count >= 0 && _pos >= 0 && (long)_pos + count <= _data.Length;

            internal int ReadUInt16()
            {
                int v = CanRead(2) ? (_data[_pos] << 8) | _data[_pos + 1] : 0;
                _pos += 2;
                return v;
            }
            internal int  ReadInt16()  { int v = ReadUInt16(); return v >= 0x8000 ? v - 0x10000 : v; }
            internal uint ReadUInt32()
            {
                if (!CanRead(4)) { _pos += 4; return 0u; }
                uint v = 0;
                for (int i = 0; i < 4; i++) v = (v << 8) | _data[_pos + i];
                _pos += 4;
                return v;
            }
            internal int  ReadInt32()  { return (int)ReadUInt32(); }
            internal void Skip(int n)  { _pos += n; }
            internal string ReadTag()
            {
                if (!CanRead(4)) { _pos += 4; return string.Empty; }
                string t = Encoding.ASCII.GetString(_data, _pos, 4).TrimEnd('\0');
                _pos += 4;
                return t;
            }
        }

        private sealed class BigEndianWriter
        {
            private readonly Stream _s;
            internal BigEndianWriter(Stream s) { _s = s; }

            internal void WriteUInt16(ushort v)
            { _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)(v & 0xFF)); }
            internal void WriteInt16(short v) => WriteUInt16((ushort)v);
            internal void WriteUInt32(uint v)
            { _s.WriteByte((byte)(v >> 24)); _s.WriteByte((byte)(v >> 16));
              _s.WriteByte((byte)(v >> 8));  _s.WriteByte((byte)(v & 0xFF)); }
        }
    }
}
