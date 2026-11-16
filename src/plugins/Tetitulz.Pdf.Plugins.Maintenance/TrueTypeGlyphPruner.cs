// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Drops the outlines of glyphs a document never draws from a TrueType font program,
    /// <b>without renumbering the glyphs that remain</b>.
    ///
    /// <para><b>Why not reuse <c>Base.Operations.Fonts.PdfFontSubsetter</c>.</b> That subsetter
    /// serves the <i>embedding</i> direction: it is handed a system font plus the text to draw, so
    /// it is free to renumber glyphs densely and rebuild <c>cmap</c> to match. An optimizer works
    /// on a font that is <i>already embedded</i>, and there the glyph IDs are load-bearing — a
    /// CIDFontType2 reaches them through <c>/CIDToGIDMap</c>, a simple font through
    /// <c>/Encoding</c> plus the font's own cmap, and the advance widths through <c>/W</c> or
    /// <c>/Widths</c> indexed by CID. Renumbering without rewriting every one of those in step
    /// yields a file that is still structurally valid PDF but draws the <i>wrong glyphs</i> —
    /// silently, and only for the characters that moved.</para>
    ///
    /// <para><b>What this does instead.</b> The glyph <i>count</i> is preserved and every glyph
    /// keeps its ID; only the outline data of unused glyphs is dropped, by giving them a
    /// zero-length <c>loca</c> range. A zero-length entry is the spec's own encoding for "no
    /// outline" (§5.3.4.2 of the OpenType spec — <c>loca[i] == loca[i+1]</c> means glyph <i>i</i>
    /// has no glyf data), which is exactly what a blank glyph such as <c>space</c> already uses in
    /// virtually every shipping font. So <c>/CIDToGIDMap</c>, <c>/Encoding</c>, <c>/W</c>,
    /// <c>/Widths</c>, <c>hmtx</c> and <c>maxp.numGlyphs</c> all stay valid untouched, and the
    /// saving still lands where the bytes are: <c>glyf</c> is the dominant table in a text font.
    /// </para>
    ///
    /// <para><b>Composite glyphs.</b> A retained composite glyph references component glyphs by ID
    /// (§5.3.4.3). Those components are pulled into the retained set transitively, or the composite
    /// would render with pieces missing. The walk is cycle-guarded: a font may legally be malformed
    /// such that glyph A references B which references A.</para>
    ///
    /// <para>Returns <c>null</c> with a <c>reason</c> whenever the font cannot be pruned safely —
    /// CFF outlines, a TrueType Collection, a truncated table, or a pruned result that would not be
    /// smaller. Never throws on malformed input.</para>
    /// </summary>
    internal static class TrueTypeGlyphPruner
    {
        // sfnt version tags, named by Core rather than re-declared: four copies of the same four
        // words is how the tree ended up with three readers that disagreed about 'ttcf'.
        private const uint VersionTrueType1 = SfntBuilder.VersionTrueType;
        private const uint VersionTrue      = SfntTableDirectory.VersionAppleTrueType;
        private const uint VersionCollection= SfntTableDirectory.VersionCollection;
        private const uint VersionCff       = SfntBuilder.VersionOpenTypeCff;

        /// <summary>
        /// A malformed font can describe a composite glyph chain that never terminates. The closure
        /// walk is bounded by total components visited as well as by the cycle guard.
        /// </summary>
        private const int MaxComponentsVisited = 1_000_000;

        /// <summary>The magic constant §5.2.2 subtracts the whole-font checksum from.</summary>
        private const uint CheckSumMagic = 0xB1B0AFBAu;

        /// <summary>
        /// Rewrites <paramref name="font"/> keeping the outlines of <paramref name="usedGlyphs"/>
        /// (plus glyph 0 and any composite components they reference) and blanking the rest.
        /// </summary>
        /// <returns>
        /// The pruned font program, or <c>null</c> when pruning was not possible or not worthwhile;
        /// <paramref name="reason"/> then says which.
        /// </returns>
        internal static byte[] Prune(byte[] font, ICollection<int> usedGlyphs, out string reason)
        {
            reason = null;

            if (font == null || font.Length < 12)
            {
                reason = "the font program is too short to hold an offset table";
                return null;
            }
            if (usedGlyphs == null)
            {
                reason = "no glyph-usage evidence was supplied";
                return null;
            }

            uint version = ReadU32(font, 0);
            if (version == VersionCollection)
            {
                reason = SfntTableDirectory.CollectionRefusal;
                return null;
            }
            if (version == VersionCff)
            {
                reason = "the font has CFF outlines, which this pruner does not rewrite";
                return null;
            }
            if (version != VersionTrueType1 && version != VersionTrue)
            {
                reason = "unrecognised sfnt version 0x" + version.ToString("X8");
                return null;
            }

            // The directory read — the fits check, the per-table range check, the printable-ASCII
            // tag rule and the duplicate-tag refusal — is Core.Fonts.SfntTableDirectory's, shared
            // with the two other places that re-emit a font.
            if (!SfntTableDirectory.TryRead(font, out var records, out reason)) return null;

            var tables = new Dictionary<string, TableRecord>(StringComparer.Ordinal);
            foreach (SfntTableDirectory.TableRecord record in records)
                tables[record.Tag] = new TableRecord(record.Offset, record.Length);

            if (!tables.TryGetValue("glyf", out TableRecord glyf) ||
                !tables.TryGetValue("loca", out TableRecord loca))
            {
                reason = "the font has no glyf/loca pair, so its outlines are not TrueType";
                return null;
            }
            if (!tables.TryGetValue("head", out TableRecord head) || head.Length < 54)
            {
                reason = "the font has no usable head table";
                return null;
            }
            if (!tables.TryGetValue("maxp", out TableRecord maxp) || maxp.Length < 6)
            {
                reason = "the font has no usable maxp table";
                return null;
            }

            int numGlyphs = ReadU16(font, maxp.Offset + 4);
            int indexToLocFormat = (short)ReadU16(font, head.Offset + 50);
            if (numGlyphs <= 0)
            {
                reason = "the font declares no glyphs";
                return null;
            }
            if (indexToLocFormat != 0 && indexToLocFormat != 1)
            {
                reason = "indexToLocFormat is " + indexToLocFormat + ", which is neither short nor long";
                return null;
            }

            long[] locaOffsets = ReadLoca(font, loca, numGlyphs, indexToLocFormat, out reason);
            if (locaOffsets == null) return null;

            // Every loca range must lie inside glyf and run forwards, or the closure walk would
            // read another table's bytes as glyph data.
            for (int i = 0; i < numGlyphs; i++)
            {
                if (locaOffsets[i] > locaOffsets[i + 1] || locaOffsets[i + 1] > glyf.Length)
                {
                    reason = "loca entry " + i + " does not describe a range inside glyf";
                    return null;
                }
            }

            var retained = BuildClosure(font, glyf, locaOffsets, numGlyphs, usedGlyphs, out reason);
            if (retained == null) return null;

            byte[] newGlyf = BuildGlyf(font, glyf, locaOffsets, numGlyphs, retained, out long[] newLoca);

            // Short loca stores offset/2 in a uint16, so it can only address 128 KiB. The pruned
            // table is never larger than the original, so an original that was short-format still
            // fits — but assert it rather than truncating silently.
            if (indexToLocFormat == 0 && newLoca[numGlyphs] > 0x1FFFE)
            {
                reason = "the pruned glyf table is too large for the font's short loca format";
                return null;
            }

            byte[] newLocaBytes = BuildLoca(newLoca, numGlyphs, indexToLocFormat);

            byte[] result = Reassemble(font, tables, newGlyf, newLocaBytes, head);
            if (result == null)
            {
                reason = "the pruned font could not be reassembled";
                return null;
            }

            if (result.Length >= font.Length)
            {
                reason = "pruning would not make the font program smaller";
                return null;
            }

            return result;
        }

        // ------------------------------------------------------------------
        //  loca
        // ------------------------------------------------------------------

        private static long[] ReadLoca(byte[] font, TableRecord loca, int numGlyphs, int format,
            out string reason)
        {
            reason = null;
            int entries = numGlyphs + 1;
            long needed = format == 0 ? (long)entries * 2 : (long)entries * 4;
            if (loca.Length < needed)
            {
                reason = "the loca table is shorter than numGlyphs + 1 entries";
                return null;
            }

            var offsets = new long[entries];
            for (int i = 0; i < entries; i++)
            {
                offsets[i] = format == 0
                    ? (long)ReadU16(font, loca.Offset + i * 2) * 2
                    : ReadU32(font, loca.Offset + i * 4);
            }
            return offsets;
        }

        private static byte[] BuildLoca(long[] offsets, int numGlyphs, int format)
        {
            int entries = numGlyphs + 1;
            var bytes = new byte[format == 0 ? entries * 2 : entries * 4];
            for (int i = 0; i < entries; i++)
            {
                if (format == 0) WriteU16(bytes, i * 2, (int)(offsets[i] / 2));
                else             WriteU32(bytes, i * 4, (uint)offsets[i]);
            }
            return bytes;
        }

        // ------------------------------------------------------------------
        //  Composite-glyph closure
        // ------------------------------------------------------------------

        private static HashSet<int> BuildClosure(byte[] font, TableRecord glyf, long[] loca,
            int numGlyphs, ICollection<int> seed, out string reason)
        {
            reason = null;
            var closure = new HashSet<int>();
            var pending = new Stack<int>();

            // Glyph 0 is .notdef and must always survive: a reader substitutes it for any glyph the
            // font cannot supply, so removing its outline would change how missing glyphs render.
            pending.Push(0);
            foreach (int gid in seed)
            {
                if (gid >= 0 && gid < numGlyphs) pending.Push(gid);
            }

            // Counted per pop, not per unique glyph. numGlyphs is a uint16, so a per-glyph counter
            // could never exceed 65 535 and the budget would be unreachable dead code; what can
            // actually grow without bound is the number of component references pushed, since one
            // glyph may name many and each is pushed before the cycle guard sees it.
            int visited = 0;
            while (pending.Count > 0)
            {
                if (++visited > MaxComponentsVisited)
                {
                    reason = "the composite-glyph graph exceeded the component budget";
                    return null;
                }

                int gid = pending.Pop();
                if (!closure.Add(gid)) continue;      // cycle guard

                long start = loca[gid];
                long end = loca[gid + 1];

                // The component records, bounded by the glyph's own end: a truncated record ends
                // the walk and the ones before it are kept. The walk is CompositeGlyph's.
                foreach (int indexAt in CompositeGlyph.ComponentIndexOffsets(
                             font, glyf.Offset + (int)start, glyf.Offset + (int)end))
                {
                    int component = CompositeGlyph.ReadIndex(font, indexAt);
                    if (component < numGlyphs && !closure.Contains(component))
                        pending.Push(component);
                }
            }

            return closure;
        }

        // ------------------------------------------------------------------
        //  glyf
        // ------------------------------------------------------------------

        private static byte[] BuildGlyf(byte[] font, TableRecord glyf, long[] loca, int numGlyphs,
            HashSet<int> retained, out long[] newLoca)
        {
            newLoca = new long[numGlyphs + 1];
            var output = new MemoryStream();

            for (int gid = 0; gid < numGlyphs; gid++)
            {
                newLoca[gid] = output.Length;

                if (!retained.Contains(gid)) continue;   // zero-length range == blank glyph

                long start = loca[gid];
                long length = loca[gid + 1] - start;
                if (length <= 0) continue;

                output.Write(font, glyf.Offset + (int)start, (int)length);

                // Pad to a 4-byte boundary. Long loca needs no alignment and short loca needs 2,
                // but 4 satisfies both and matches what shipping fonts do.
                int pad = (int)((4 - (output.Length & 3)) & 3);
                for (int i = 0; i < pad; i++) output.WriteByte(0);
            }

            newLoca[numGlyphs] = output.Length;
            return output.ToArray();
        }

        // ------------------------------------------------------------------
        //  Reassembly
        // ------------------------------------------------------------------

        /// <summary>
        /// Lays the pruned tables out as an sfnt container, through
        /// <see cref="Tetitulz.Pdf.Core.Fonts.SfntBuilder"/> — the single assembler.
        /// </summary>
        /// <remarks>
        /// The input's sfnt version is carried through rather than assumed. The overall-size guard
        /// stays here: the
        /// builder lays out whatever it is given, and refusing a font that would not fit in an
        /// <see cref="int"/> is this method's decision, not the layout's.
        /// </remarks>
        private static byte[] Reassemble(byte[] font, Dictionary<string, TableRecord> tables,
            byte[] newGlyf, byte[] newLoca, TableRecord head)
        {
            var tags = new List<string>(tables.Keys);
            tags.Sort(StringComparer.Ordinal);

            var payloads = new List<(string tag, byte[] data)>(tags.Count);
            foreach (string tag in tags)
            {
                if (tag == "glyf") { payloads.Add((tag, newGlyf)); continue; }
                if (tag == "loca") { payloads.Add((tag, newLoca)); continue; }

                TableRecord record = tables[tag];
                var copy = new byte[record.Length];
                Buffer.BlockCopy(font, record.Offset, copy, 0, record.Length);
                payloads.Add((tag, copy));
            }

            long total = 12 + (long)tags.Count * 16;
            foreach (var p in payloads) total += Align4(p.data.Length);
            if (total > int.MaxValue) return null;

            uint sfntVersion = ReadU32(font, 0);
            return Tetitulz.Pdf.Core.Fonts.SfntBuilder.Build(sfntVersion, payloads);
        }

        private static int Align4(int value) => (value + 3) & ~3;

        // ------------------------------------------------------------------
        //  Primitives
        // ------------------------------------------------------------------

        private readonly struct TableRecord
        {
            internal readonly int Offset;
            internal readonly int Length;
            internal TableRecord(int offset, int length) { Offset = offset; Length = length; }
        }

        private static int ReadU16(byte[] data, int offset) =>
            (data[offset] << 8) | data[offset + 1];

        private static uint ReadU32(byte[] data, int offset) =>
            ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
            ((uint)data[offset + 2] << 8) | data[offset + 3];

        private static void WriteU16(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 8) & 0xFF);
            data[offset + 1] = (byte)(value & 0xFF);
        }

        private static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }
}
