// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts.Cff
{
    /// <summary>Parsed representation of a CFF (Compact Font Format) font program.</summary>
    internal sealed class CffFont
    {
        /// <summary>Glyph charstring bytes indexed by GID (glyph ID).</summary>
        public IReadOnlyList<byte[]> Charstrings { get; }

        /// <summary>Local subroutines for this font.</summary>
        public IReadOnlyList<byte[]> LocalSubrs { get; }

        /// <summary>Global subroutines shared across all fonts in the CFF.</summary>
        public IReadOnlyList<byte[]> GlobalSubrs { get; }

        /// <summary>
        /// Maps character code → GID for the font's encoding.
        /// Key: character code (0–255 for simple fonts, CID for CIDFonts).
        /// Value: GID (glyph index into Charstrings).
        /// </summary>
        public IReadOnlyDictionary<int, int> CharCodeToGid { get; }

        /// <summary>
        /// Per-glyph advance widths (PDF-space units, 1/1000 em).
        /// Populated from the CFF charstring width argument when present;
        /// falls back to <see cref="DefaultWidthX"/>.
        /// </summary>
        public float[] GlyphWidths { get; }

        /// <summary>Width used when a charstring has no explicit width operand.</summary>
        public float DefaultWidthX { get; }

        /// <summary>Nominal (base) width added to the charstring's width operand.</summary>
        public float NominalWidthX { get; }

        /// <summary>
        /// Uniform glyph-space → text-space scale from the Top DICT /FontMatrix (element
        /// [0]).  Default 0.001 (a 1000-unit em).  When a font declares a non-default
        /// /FontMatrix the charstring outlines and widths are expressed in a different
        /// glyph space, so the renderer scales them by <c>FontMatrixScale / 0.001</c> to
        /// map into its 1000-unit em space.
        /// </summary>
        public float FontMatrixScale { get; }

        /// <summary>
        /// Maps glyph name → GID.  Populated when the CFF string INDEX is parsed.
        /// Null when glyph names are not available (e.g. legacy CFF without string data).
        /// Used to apply a PDF-level /Encoding override (e.g. WinAnsiEncoding) when
        /// the CFF's internal encoding differs from what the PDF font dict specifies.
        /// </summary>
        public IReadOnlyDictionary<string, int> GlyphNameToGid { get; }

        /// <summary>True when this font was parsed from a CFF2 table (variable-font format).</summary>
        public bool IsCff2 { get; }

        /// <summary>
        /// True when the Top DICT carries <c>ROS</c> (12 30) — a CID-keyed font, whose
        /// <see cref="CharCodeToGid"/> maps <b>CID → GID</b> through the charset rather than
        /// character code → GID through an encoding.
        /// </summary>
        /// <remarks>
        /// The distinction matters to anything holding CIDs: under <c>Identity-H</c> a PDF's
        /// two-byte code <i>is</i> the CID, but the CID is only the GID when the charset happens to
        /// be Identity. A subsetter that assumed CID == GID would blank the wrong glyphs on every
        /// font whose producer ordered its charset any other way. For a <b>non</b>-CID-keyed CFF
        /// reached through a <c>CIDFontType0</c> — legal, and what a converted simple font looks
        /// like — there is no charset indirection and the CID is the glyph index.
        /// </remarks>
        public bool IsCidKeyed { get; }

        /// <summary>
        /// Number of variation <i>regions</i> referenced by the default (vsindex 0)
        /// <c>ItemVariationData</c> of the CFF2 VariationStore — i.e. the count of delta
        /// operands per blended value consumed by the <c>blend</c> operator (opcode 16).
        /// Zero for CFF1 fonts or CFF2 fonts whose VariationStore could not be parsed.
        /// (This is the region count, <b>not</b> the axis count: a multi-axis font has
        /// more regions than axes, so using the axis count here mis-sized the blend deltas.)
        /// </summary>
        public int BlendRegionCount { get; }

        /// <summary>
        /// Per-GID local subroutines for CID-keyed CFF fonts with multiple FDs (FDArray/FDSelect).
        /// Null when all glyphs share the same <see cref="LocalSubrs"/>.
        /// Index is GID; value is the local subroutine list for that glyph.
        /// </summary>
        public IReadOnlyList<byte[]>[] LocalSubrsPerGid { get; }

        /// <summary>Per-GID defaultWidthX for multi-FD CID fonts. Null when all glyphs share <see cref="DefaultWidthX"/>.</summary>
        public float[] DefaultWidthsPerGid { get; }

        /// <summary>Per-GID nominalWidthX for multi-FD CID fonts. Null when all glyphs share <see cref="NominalWidthX"/>.</summary>
        public float[] NominalWidthsPerGid { get; }

        /// <summary>
        /// True when <see cref="FontMatrixScale"/> is the scale the font actually declares for every
        /// glyph: no Top DICT <c>FontMatrix</c>, or one inside the accepted range, and — for a
        /// CID-keyed font — no Font DICT in the FDArray whose own matrix, concatenated with the Top
        /// DICT's, comes to a different scale. False means the renderer's scale is a fallback, so an
        /// advance computed from it is not the font's.
        /// </summary>
        public bool WidthScaleIsKnown { get; }

        /// <summary>
        /// True when <see cref="GlyphNameToGid"/> names every glyph, from a charset read exactly: the
        /// ISOAdobe predefined charset or a charset in the font, not the Expert charsets the parser
        /// approximates, and none truncated, duplicated or naming a string that does not exist. Only
        /// then is a name's absence evidence that the font does not define it.
        /// </summary>
        public bool HasExactGlyphNames { get; }

        public CffFont(
            IReadOnlyList<byte[]> charstrings,
            IReadOnlyList<byte[]> localSubrs,
            IReadOnlyList<byte[]> globalSubrs,
            IReadOnlyDictionary<int, int> charCodeToGid,
            float defaultWidthX,
            float nominalWidthX,
            IReadOnlyDictionary<string, int> glyphNameToGid = null,
            bool isCff2 = false,
            int blendRegionCount = 0,
            bool isCidKeyed = false,
            IReadOnlyList<byte[]>[] localSubrsPerGid = null,
            float[] defaultWidthsPerGid = null,
            float[] nominalWidthsPerGid = null,
            float fontMatrixScale = 0.001f,
            bool widthScaleIsKnown = true,
            bool hasExactGlyphNames = false)
        {
            Charstrings        = charstrings   ?? Array.Empty<byte[]>();
            LocalSubrs         = localSubrs    ?? Array.Empty<byte[]>();
            GlobalSubrs        = globalSubrs   ?? Array.Empty<byte[]>();
            CharCodeToGid      = charCodeToGid ?? new Dictionary<int, int>();
            DefaultWidthX      = defaultWidthX;
            NominalWidthX      = nominalWidthX;
            GlyphNameToGid     = glyphNameToGid;
            IsCff2             = isCff2;
            IsCidKeyed         = isCidKeyed;
            BlendRegionCount   = blendRegionCount;
            LocalSubrsPerGid   = localSubrsPerGid;
            DefaultWidthsPerGid = defaultWidthsPerGid;
            NominalWidthsPerGid = nominalWidthsPerGid;
            // A non-positive / non-finite FontMatrix scale is meaningless — fall back to
            // the 1000-unit em default so downstream ratio math stays finite.
            FontMatrixScale    = fontMatrixScale > 0f && !float.IsNaN(fontMatrixScale) && !float.IsInfinity(fontMatrixScale)
                                 ? fontMatrixScale : 0.001f;
            WidthScaleIsKnown  = widthScaleIsKnown;
            HasExactGlyphNames = hasExactGlyphNames && glyphNameToGid != null;
            GlyphWidths        = new float[Charstrings.Count];
            for (int i = 0; i < GlyphWidths.Length; i++)
                GlyphWidths[i] = float.NaN; // not yet extracted
        }

        /// <summary>
        /// Returns a new <see cref="CffFont"/> identical to this one but with a
        /// different char-code → GID map. Used to apply PDF-level encoding overrides.
        /// </summary>
        public CffFont WithCharCodeToGid(IReadOnlyDictionary<int, int> newMap)
            => new CffFont(Charstrings, LocalSubrs, GlobalSubrs,
                           newMap, DefaultWidthX, NominalWidthX, GlyphNameToGid,
                           IsCff2, BlendRegionCount, IsCidKeyed,
                           LocalSubrsPerGid, DefaultWidthsPerGid, NominalWidthsPerGid,
                           FontMatrixScale, WidthScaleIsKnown, HasExactGlyphNames);

        /// <summary>
        /// The steps every scan of this program's charstrings spends together — the widths
        /// <see cref="TryGetAdvance"/> reads, the subsetter's seac walk (<see cref="CharstringWork"/>).
        /// </summary>
        internal readonly CharstringWork.FontAccount ScanSteps = new CharstringWork.FontAccount();

        /// <summary>
        /// The advance width of glyph <paramref name="gid"/> in PDF glyph space (1000 units per text
        /// space unit) — the charstring's width operand added to <c>nominalWidthX</c>, or
        /// <c>defaultWidthX</c> when it has none (per the glyph's own Font DICT in a multi-FD font),
        /// scaled by the font matrix.
        /// </summary>
        /// <returns>
        /// <c>false</c> when the advance cannot be known: a CFF2 font (its widths live in <c>hmtx</c>,
        /// not the charstrings), <see cref="WidthScaleIsKnown"/> false, a glyph id outside the
        /// CharStrings INDEX, or a charstring <see cref="CffCharstringScanner.TryReadWidth"/> refuses.
        /// </returns>
        public bool TryGetAdvance(int gid, out double advance)
        {
            advance = 0;
            if (IsCff2 || !WidthScaleIsKnown || gid < 0 || gid >= Charstrings.Count) return false;

            IReadOnlyList<byte[]> localSubrs = LocalSubrsPerGid != null && gid < LocalSubrsPerGid.Length
                ? LocalSubrsPerGid[gid] : LocalSubrs;
            if (!CffCharstringScanner.TryReadWidth(Charstrings[gid], localSubrs, GlobalSubrs, out double? width, ScanSteps))
                return false;

            double glyphUnits = width.HasValue
                ? width.Value + (NominalWidthsPerGid != null && gid < NominalWidthsPerGid.Length ? NominalWidthsPerGid[gid] : NominalWidthX)
                : (DefaultWidthsPerGid != null && gid < DefaultWidthsPerGid.Length ? DefaultWidthsPerGid[gid] : DefaultWidthX);
            advance = glyphUnits * FontMatrixScale * 1000.0;
            return true;
        }
    }
}
