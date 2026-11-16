// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Unwraps a <b>WOFF 2.0</b> container back into the plain sfnt every other font consumer in
    /// this tree already understands.
    /// </summary>
    /// <remarks>
    /// <para><b>Two halves, and only one of them is decompression.</b> WOFF 2.0 Brotli-compresses a
    /// <i>transformed</i> table set, so unwrapping is: parse the directory, inflate the table block,
    /// then reverse the <c>glyf</c>/<c>loca</c> and <c>hmtx</c> transforms and reassemble an sfnt.
    /// The transform reversal (<see cref="Woff2GlyfTransform"/>) is the larger half and has to be
    /// written whatever the decompressor is — which is why "WOFF 2.0 needs Brotli" was only ever
    /// half the cost.</para>
    ///
    /// <para><b>Every length is a claim.</b> <c>totalSfntSize</c>, each <c>origLength</c> and each
    /// <c>transformLength</c> come from the file, and a decoder that sized a buffer from one of them
    /// lets a 200-byte container ask for four gigabytes. Nothing here allocates from a declared
    /// size: the table block is bounded by <see cref="MaxSfntBytes"/>, the reconstruction is bounded
    /// again, and the declared sizes are checked against what actually arrived.</para>
    ///
    /// <para><b>Never throws.</b> <see cref="Decode(byte[])"/> answers <see langword="null"/> for a
    /// malformed, hostile or oversized container, matching <see cref="WoffDecoder"/> and the
    /// contract <see cref="TrueTypeFontProgram.Parse"/> presents to every caller: a font that will
    /// not unwrap is indistinguishable from one that will not parse.</para>
    ///
    /// <para><b>What is deliberately not supported.</b> A font <i>collection</i> (<c>ttcf</c>
    /// flavour) carries a collection directory this does not read, and is refused by name rather
    /// than decoded into whichever font happened to be first. Extended metadata and private data
    /// are ignored: neither affects glyphs, and this tree has no consumer for either.</para>
    /// </remarks>
    internal static class Woff2Decoder
    {
        /// <summary>'wOF2' — the WOFF 2.0 signature.</summary>
        private const uint Woff2Signature = 0x774F4632;

        /// <summary>'ttcf' — a font collection, which this refuses.</summary>
        private const uint TtcFlavor = 0x74746366;

        /// <summary>The fixed WOFF2 header, before the table directory.</summary>
        private const int HeaderSize = 48;

        /// <summary>The known-tag escape: an arbitrary four-byte tag follows the flags.</summary>
        private const int ArbitraryTagFlag = 63;

        /// <summary>The <c>glyf</c>/<c>loca</c> null transform, which is version 3 for those two.</summary>
        private const int GlyfNullTransform = 3;

        /// <summary>
        /// Ceiling on the reassembled sfnt, matching <see cref="WoffDecoder"/>'s. 64 MiB is above
        /// the largest fonts that exist (the pan-CJK Source Han collections are ~40 MB) and absurdly
        /// above anything served as a <i>web</i> font, which is what a WOFF is. It is a cap on the
        /// possible, not on the polite: what it stops is a small container whose Brotli stream is a
        /// zero bomb.
        /// </summary>
        private const long MaxSfntBytes = 64L * 1024 * 1024;

        /// <summary>An upper bound on the table count, so the directory loop is bounded too.</summary>
        /// <remarks>
        /// The field is a <c>UInt16</c>, so 65 535 is already the ceiling; what this adds is that a
        /// count far beyond any real font stops the directory parse before it has read 65 000
        /// entries out of a 100-byte file. sfnt fonts in the wild are well under a hundred tables.
        /// </remarks>
        private const int MaxTables = 4096;

        /// <summary>True when these bytes open with the WOFF 2.0 signature.</summary>
        internal static bool IsWoff2(byte[] data)
            => data != null && data.Length >= 4
               && ((uint)data[0] << 24 | (uint)data[1] << 16 | (uint)data[2] << 8 | data[3])
                  == Woff2Signature;

        /// <summary>
        /// The sfnt inside a WOFF 2.0 container, or <see langword="null"/> when it is malformed,
        /// hostile, a collection, or larger than <see cref="MaxSfntBytes"/> once expanded.
        /// </summary>
        internal static byte[] Decode(byte[] woff2) => Decode(woff2, MaxSfntBytes);

        /// <summary>
        /// As <see cref="Decode(byte[])"/>, under <paramref name="maxSfntBytes"/> in place of
        /// <see cref="MaxSfntBytes"/>.
        /// </summary>
        /// <remarks>
        /// <b>For tests, and only because of scale.</b> Every length ceiling in this decoder is the
        /// one constant, and the check on the <i>reassembled</i> font can only be reached by a font
        /// that is larger than it once rebuilt — 64 MiB of output, which no unit test should
        /// allocate. Taking the ceiling as a parameter lets a fixture of a few hundred bytes stand
        /// exactly where a 64 MiB one would, against the same code.
        /// </remarks>
        internal static byte[] Decode(byte[] woff2, long maxSfntBytes)
        {
            try
            {
                return DecodeCore(woff2, maxSfntBytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] DecodeCore(byte[] woff2, long maxSfntBytes)
        {
            if (!IsWoff2(woff2) || woff2.Length < HeaderSize) return null;

            var reader = new Woff2Reader(woff2, 4);      // past the signature
            if (!reader.TryReadU32(out uint flavor)) return null;
            if (flavor == TtcFlavor) return null;        // a collection; refused by name

            if (!reader.TryReadU32(out uint declaredLength) ||
                !reader.TryReadU16(out int numTables) ||
                !reader.TryReadU16(out int reserved) ||
                !reader.TryReadU32(out uint totalSfntSize) ||
                !reader.TryReadU32(out uint totalCompressedSize)) return null;

            if (reserved != 0) return null;
            if (numTables == 0 || numTables > MaxTables) return null;
            if (declaredLength != (uint)woff2.Length) return null;
            if (totalSfntSize > maxSfntBytes) return null;

            // majorVersion, minorVersion, metaOffset, metaLength, metaOrigLength, privOffset,
            // privLength: read past. None of them affects the glyphs.
            if (!reader.TrySkip(4 + 20)) return null;

            List<Entry> directory = ReadDirectory(reader, numTables, maxSfntBytes);
            if (directory == null) return null;

            // Summed, and deliberately NOT tested against the ceiling here: BrotliInflate tests it
            // at the allocation this total sizes. A second copy here would leave each to refuse when
            // the other was deleted, so neither could be seen to be there; the one kept is the one
            // beside the allocation. A long cannot wrap: at most
            // MaxTables entries of a UInt32 each.
            long expected = 0;
            foreach (Entry e in directory) expected += e.CompressedLength;

            if (reader.Remaining < totalCompressedSize || totalCompressedSize > int.MaxValue) return null;
            if (!reader.TryReadBytes((int)totalCompressedSize, out byte[] compressed)) return null;

            // §4.1: the sum of the directory's lengths MUST equal the decompressed block size.
            // Everything below indexes into the block by that arithmetic, and a mismatch would read
            // one table's bytes as another's - so BrotliInflate is handed the total as the size to
            // produce rather than as a ceiling to test afterwards, and answers null for either
            // direction of disagreement.
            byte[] block = BrotliInflate(compressed, expected, maxSfntBytes);
            if (block == null) return null;

            return Reassemble(flavor, directory, block, maxSfntBytes);
        }

        /// <summary>The table directory: one entry per table, in file order.</summary>
        private static List<Entry> ReadDirectory(Woff2Reader reader, int numTables, long maxSfntBytes)
        {
            var directory = new List<Entry>(numTables);

            for (int i = 0; i < numTables; i++)
            {
                if (!reader.TryReadU8(out int flags)) return null;

                int knownIndex = flags & 0x3F;
                int transformVersion = (flags >> 6) & 0x03;

                string tag;
                if (knownIndex == ArbitraryTagFlag)
                {
                    if (!reader.TryReadBytes(4, out byte[] raw)) return null;
                    tag = TagOf(raw);
                }
                else
                {
                    tag = Woff2Tables.TagFor(knownIndex);
                }

                if (tag == null) return null;

                if (!reader.TryReadBase128(out uint origLength)) return null;

                // For an untransformed table this repeats what BrotliInflate checks on the sum. For a
                // TRANSFORMED one it is the only check the claim gets: origLength is then the size of
                // the table before the transform, which nothing below reads, so without this a
                // directory could declare a 4 GB glyf and decode as though it had not.
                if (origLength > maxSfntBytes) return null;

                // §4.1: transformLength is present if and ONLY if a non-null transform applies.
                // For glyf/loca the null transform is version 3; for everything else it is 0.
                bool transformed = tag == "glyf" || tag == "loca"
                    ? transformVersion != GlyfNullTransform
                    : transformVersion != 0;

                // No ceiling on transformLength of its own: it is what the block is summed from, and
                // BrotliInflate bounds the sum — so a per-entry copy could never be what refused.
                uint transformLength = origLength;
                if (transformed && !reader.TryReadBase128(out transformLength)) return null;

                directory.Add(new Entry(tag, transformVersion, transformed, origLength, transformLength));
            }

            return directory;
        }

        /// <summary>Rebuilds the sfnt from the decompressed table block.</summary>
        private static byte[] Reassemble(uint flavor, List<Entry> directory, byte[] block, long maxSfntBytes)
        {
            var tables = new List<(string tag, byte[] data)>(directory.Count);
            byte[] rebuiltLoca = null;
            byte[] rebuiltGlyf = null;
            int locaIndex = -1;
            int hmtxIndex = -1;
            byte[] transformedHmtx = null;
            long offset = 0;

            foreach (Entry e in directory)
            {
                var slice = new byte[e.CompressedLength];
                Buffer.BlockCopy(block, (int)offset, slice, 0, slice.Length);
                offset += e.CompressedLength;

                if (e.Tag == "loca" && e.Transformed)
                {
                    // §5.3: the transformed loca is EMPTY by definition — its offsets are whatever
                    // the glyf reconstruction produces. Its place in the output is held here and
                    // filled from the glyf pass; a non-zero transformLength is invalid.
                    if (e.TransformLength != 0) return null;
                    locaIndex = tables.Count;
                    tables.Add(("loca", Array.Empty<byte>()));
                    continue;
                }

                if (e.Tag == "glyf" && e.Transformed)
                {
                    if (e.TransformVersion != 0) return null;   // the only defined glyf transform
                    var (glyf, loca) = Woff2GlyfTransform.Rebuild(slice, maxSfntBytes);
                    if (glyf == null) return null;

                    rebuiltLoca = loca;
                    rebuiltGlyf = glyf;
                    tables.Add(("glyf", glyf));
                    continue;
                }

                if (e.Tag == "hmtx" && e.Transformed)
                {
                    // Version 1 rebuilds the left side bearings from the glyph bounding boxes, so
                    // it cannot run until glyf has been reconstructed. §5.5 puts loca after glyf but
                    // says nothing about hmtx, so the slice is held and resolved after the loop
                    // rather than assumed to arrive late.
                    if (e.TransformVersion != 1) return null;
                    hmtxIndex = tables.Count;
                    transformedHmtx = slice;
                    tables.Add(("hmtx", Array.Empty<byte>()));
                    continue;
                }

                if (e.Transformed)
                {
                    // §4.1: an unknown transformation version means the WHOLE font is rejected. A
                    // table passed through in its transformed form would parse as nonsense rather
                    // than fail, which is the outcome that rule exists to prevent.
                    return null;
                }

                if (slice.LongLength != e.OrigLength) return null;
                tables.Add((e.Tag, slice));
            }

            if (locaIndex >= 0)
            {
                if (rebuiltLoca == null) return null;    // a transformed loca with no transformed glyf
                tables[locaIndex] = ("loca", rebuiltLoca);
            }
            else if (rebuiltLoca != null)
            {
                // §5.3: glyf and loca are transformed together or not at all.
                return null;
            }

            if (hmtxIndex >= 0)
            {
                byte[] hmtx = RebuildHmtx(transformedHmtx, rebuiltGlyf, rebuiltLoca, tables);
                if (hmtx == null) return null;
                tables[hmtxIndex] = ("hmtx", hmtx);
            }

            // The answer's own ceiling. Every part is bounded already — the block by BrotliInflate,
            // glyf by the rebuild above — but the transform rebuilds MORE than it reads (a computed
            // bounding box, a loca, padding), so parts that each pass can still sum past it.
            long total = 0;
            foreach (var (_, data) in tables) total += data.Length;
            if (total > maxSfntBytes) return null;

            return SfntBuilder.Build(flavor, tables);
        }

        /// <summary>
        /// The §5.4 <c>hmtx</c> reversal, with the counts read back out of the reconstructed
        /// <c>maxp</c>, <c>hhea</c> and <c>head</c>.
        /// </summary>
        /// <remarks>
        /// <b>The counts come from the font's own tables, not from anything this decoder tracked.</b>
        /// <c>numGlyphs</c>, <c>numberOfHMetrics</c> and <c>indexToLocFormat</c> each live in a
        /// different table, and a decoder that carried its own copies would have two sources for one
        /// number — which is how a font whose <c>maxp</c> and transformed <c>glyf</c> disagree gets
        /// decoded as though they agreed.
        /// </remarks>
        private static byte[] RebuildHmtx(byte[] transformed, byte[] glyf, byte[] loca,
                                          List<(string tag, byte[] data)> tables)
        {
            if (glyf == null || loca == null) return null;   // the transform needs a TrueType glyf

            byte[] maxp = Find(tables, "maxp");
            byte[] hhea = Find(tables, "hhea");
            byte[] head = Find(tables, "head");
            if (maxp == null || maxp.Length < 6) return null;
            if (hhea == null || hhea.Length < 36) return null;
            if (head == null || head.Length < 52) return null;

            int numGlyphs = (maxp[4] << 8) | maxp[5];
            int numberOfHMetrics = (hhea[34] << 8) | hhea[35];
            int indexFormat = (head[50] << 8) | head[51];
            if (indexFormat != 0 && indexFormat != 1) return null;

            int[] xMins = Woff2HmtxTransform.ReadXMins(glyf, loca, numGlyphs, indexFormat);
            if (xMins == null) return null;

            return Woff2HmtxTransform.Rebuild(transformed, numGlyphs, numberOfHMetrics, xMins);
        }

        private static byte[] Find(List<(string tag, byte[] data)> tables, string tag)
        {
            foreach (var (t, data) in tables)
                if (string.Equals(t, tag, StringComparison.Ordinal)) return data;
            return null;
        }

        /// <summary>
        /// Brotli-decompresses the table block into a buffer of exactly
        /// <paramref name="exactBytes"/>, refusing a stream that produces any other number.
        /// </summary>
        /// <remarks>
        /// <para><b>The ceiling is the caller's declared total, not a constant</b>, because §4.1
        /// makes the directory's summed lengths equal the decompressed size exactly. A stream that
        /// expands past it is either malformed or a bomb, and either way there is no reason to
        /// finish inflating it.</para>
        /// <para><b>So the ceiling has to bound the allocation rather than describe it.</b>
        /// Inflating the whole stream with <c>Brotli.DecompressBuffer</c> and then testing the length
        /// of what came back would refuse the <i>result</i> and not the <i>work</i>: a 200-byte
        /// container whose stream expands to four gigabytes would be fully materialised first.
        /// Reading through <c>BrotliStream</c> into a buffer of the declared size is what turns
        /// <see cref="MaxSfntBytes"/> from a statement about the answer into a bound on memory.</para>
        /// <para><b>The size is known exactly, so there is nothing to grow</b> — and both
        /// directions are errors, so both are checked here. A stream that ends early contradicts the
        /// directory it came with; one that still has bytes when the buffer is full is the bomb
        /// case, which is why the extra byte is <i>read</i> rather than assumed absent. That
        /// equality is the read itself, not a caller's test of the returned array.</para>
        /// <para><b>What this does not bound is the decoder's own window.</b> Brotli sizes its ring
        /// buffer from the stream's <c>WBITS</c> — up to 16 MiB — and allocates it inside
        /// the package whatever this method does. What is bounded here is the output, which is the
        /// half a small input can make arbitrarily large.</para>
        /// <para><b>This is the one check on the declared total.</b> The caller's summing loop and
        /// the directory's per-entry <c>transformLength</c> read do not test it too; with three,
        /// deleting any would leave another to refuse, so none of them could be pinned, and the one kept
        /// is the one beside the allocation it bounds.</para>
        /// <para><b>One decompressor on both target frameworks.</b> <c>BrotliStream</c> exists from
        /// .NET Core 2.1 and would serve the <c>net10.0</c> leg, but using it there and a package on
        /// <c>netstandard2.0</c> would mean a hostile font is decoded by two different
        /// implementations depending on the host — the divergence this repository's rendering rules
        /// exist to prevent, applied to a security boundary.</para>
        /// </remarks>
        private static byte[] BrotliInflate(byte[] compressed, long exactBytes, long maxSfntBytes)
        {
            if (exactBytes <= 0 || exactBytes > maxSfntBytes) return null;

            // The declared total is the ceiling: a stream going past it is the bomb, refused when it
            // gets there; one ending short of it disagrees with the directory the other way.
            byte[] block;
            try { block = Tetitulz.Pdf.Core.IO.Brotli.BrotliDecoder.Decode(compressed, exactBytes); }
            catch (Tetitulz.Pdf.Core.IO.Filters.PdfFilterException) { return null; }
            return block.Length == exactBytes ? block : null;
        }

        /// <summary>A four-byte tag as its Latin-1 characters — sfnt tags are ASCII by definition.</summary>
        private static string TagOf(byte[] raw)
        {
            var chars = new char[4];
            for (int i = 0; i < 4; i++)
            {
                if (raw[i] < 0x20 || raw[i] > 0x7E) return null;
                chars[i] = (char)raw[i];
            }
            return new string(chars);
        }

        /// <summary>One table directory entry.</summary>
        private readonly struct Entry
        {
            internal Entry(string tag, int transformVersion, bool transformed,
                           uint origLength, uint transformLength)
            {
                Tag = tag;
                TransformVersion = transformVersion;
                Transformed = transformed;
                OrigLength = origLength;
                TransformLength = transformLength;
            }

            internal string Tag { get; }
            internal int TransformVersion { get; }
            internal bool Transformed { get; }
            internal uint OrigLength { get; }
            internal uint TransformLength { get; }

            /// <summary>How many bytes this table occupies in the decompressed block.</summary>
            internal uint CompressedLength => Transformed ? TransformLength : OrigLength;
        }
    }
}
