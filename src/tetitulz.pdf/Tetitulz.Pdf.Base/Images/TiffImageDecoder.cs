// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Decodes a TIFF (TIFF 6.0) to 8-bit RGB, one <b>page</b> at a time.
    /// </summary>
    /// <remarks>
    /// <para><b>Multi-page is the whole point.</b> A TIFF's images are a linked list of IFDs, and a
    /// scanner writes one per sheet — so "the first frame" is the wrong answer here in a way it is
    /// not for GIF, where the frames are an animation. <see cref="CountPages"/> walks the chain and
    /// <see cref="TryDecodePage(byte[], int, out byte[], out int, out int)"/> reads one.</para>
    ///
    /// <para><b>Every compression this understands is already in the tree.</b> PackBits <i>is</i>
    /// PDF's <c>RunLengthDecode</c>, TIFF's LZW is the MSB-first one <c>LzwDecodeFilter</c> reads
    /// (with the same off-by-one the <c>EarlyChange</c> flag exists for), Deflate is
    /// <c>FlateDecodeFilter</c>, CCITT G3/G4 is <c>CcittFaxDecoder</c>, and predictor 2 is the
    /// horizontal differencing <c>PredictorHelper</c> already applies for PDF. Writing a second
    /// copy of any of them would be centralising two implementations of one question, which is the
    /// thing that goes stale.</para>
    ///
    /// <para><b>What it refuses, by name.</b> JPEG-compressed TIFF (compression 6 and 7), because
    /// the tables live in the IFD rather than in the strip and a JPEG decoder handed a headerless
    /// strip produces garbage rather than an error; <c>PlanarConfiguration 2</c>, where the
    /// channels are in separate strips; a <c>PhotometricInterpretation</c> other than grey, RGB and
    /// palette (CMYK, YCbCr, CIELab); and anything above 8 bits per sample. Each is reported
    /// through <see cref="LastError"/> so a caller can say which, rather than "unreadable".</para>
    ///
    /// <para><b>Bounds.</b> Dimensions are checked against <see cref="MaxPixels"/> before anything
    /// is allocated, and the strip and tile tables are checked against the file's own length — a
    /// TIFF header is a set of offsets, so a hostile one is a set of offsets that do not point
    /// anywhere.</para>
    /// </remarks>
    public static class TiffImageDecoder
    {
        /// <summary>Largest image this decoder will materialise, matching the PNG and GIF paths.</summary>
        private const long MaxPixels = 40_000_000L;

        /// <summary>Largest IFD chain this decoder will walk.</summary>
        /// <remarks>
        /// An IFD names the next one by offset, so a file can point one at another in a cycle. The
        /// visited-offset set below is what actually stops that; this bounds a chain that is merely
        /// enormous rather than circular.
        /// </remarks>
        private const int MaxPages = 10_000;

        [ThreadStatic] private static string _lastError;

        /// <summary>Why the last call failed, or <c>null</c>. Per-thread.</summary>
        public static string LastError => _lastError;

        /// <summary>Whether <paramref name="bytes"/> begins with a TIFF header.</summary>
        public static bool IsTiff(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) return false;
            if (bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 42 && bytes[3] == 0) return true;
            return bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0 && bytes[3] == 42;
        }

        /// <summary>
        /// How many pages the file holds, or 0 when it is not a readable TIFF.
        /// </summary>
        public static int CountPages(byte[] bytes)
        {
            _lastError = null;
            if (!IsTiff(bytes))
            {
                _lastError = "not a TIFF: the header is neither 'II' 42 nor 'MM' 42";
                return 0;
            }

            bool bigEndian = bytes[0] == 0x4D;
            long offset = ReadU32(bytes, 4, bigEndian);

            var visited = new HashSet<long>();
            int count = 0;

            while (offset > 0 && count < MaxPages)
            {
                // An IFD offset pointing at one already walked is a cycle, and a chain length cap
                // alone would still walk it that many times.
                if (!visited.Add(offset)) break;
                if (offset + 2 > bytes.Length) break;

                int entries = ReadU16(bytes, offset, bigEndian);
                long next = offset + 2 + ((long)entries * 12);
                if (next + 4 > bytes.Length) { count++; break; }

                count++;
                offset = ReadU32(bytes, next, bigEndian);
            }

            return count;
        }

        /// <summary>
        /// Decodes one page to 8-bit RGB.
        /// </summary>
        /// <param name="bytes">The whole file.</param>
        /// <param name="pageIndex">Zero-based page.</param>
        /// <param name="rgb">On success, <c>width*height*3</c> bytes.</param>
        /// <param name="width">On success, the page's width in pixels.</param>
        /// <param name="height">On success, the page's height in pixels.</param>
        public static bool TryDecodePage(byte[] bytes, int pageIndex,
            out byte[] rgb, out int width, out int height)
            => TryDecodePage(bytes, pageIndex, null, out rgb, out width, out height);

        /// <summary>
        /// <see cref="TryDecodePage(byte[], int, out byte[], out int, out int)"/>, refusing a page whose
        /// strips read bytes an earlier page's strips claimed in <paramref name="earlierPages"/> — for a
        /// caller decoding every page of one file.
        /// </summary>
        /// <remarks>
        /// A page is the image its own data holds. A file whose pages all read one strip asks for every
        /// page's pixels from it: ten thousand directories of 40 M pixels each over one small strip.
        /// The test is on byte ranges, not offsets, since an uncompressed page can start one byte later
        /// in the same data; strips within one page may share.
        /// </remarks>
        internal static bool TryDecodePage(byte[] bytes, int pageIndex, StripLedger earlierPages,
            out byte[] rgb, out int width, out int height)
        {
            rgb = null; width = 0; height = 0;
            _lastError = null;

            if (!IsTiff(bytes))
            {
                _lastError = "not a TIFF: the header is neither 'II' 42 nor 'MM' 42";
                return false;
            }
            if (pageIndex < 0)
            {
                _lastError = "page index is negative";
                return false;
            }

            bool bigEndian = bytes[0] == 0x4D;
            if (!TryFindIfd(bytes, pageIndex, bigEndian, out long ifd)) return false;

            var tags = new Dictionary<int, TiffEntry>();
            if (!TryReadIfd(bytes, ifd, bigEndian, tags)) return false;

            return TryDecodeIfd(bytes, tags, bigEndian, earlierPages, out rgb, out width, out height);
        }

        /// <summary>
        /// The byte ranges the strips of the pages decoded so far have read, disjoint and sorted; a
        /// page's own ranges are merged before they are checked and added.
        /// </summary>
        internal sealed class StripLedger
        {
            private readonly SortedSet<(long Start, long End)> _claimed = new SortedSet<(long Start, long End)>();

            /// <summary>Claims a page's ranges, or returns the first one an earlier page claimed (nothing added then).</summary>
            internal bool TryClaim(List<(long Start, long End)> ranges, out (long Start, long End) clash)
            {
                clash = default;
                ranges.Sort();
                var merged = new List<(long Start, long End)>(ranges.Count);
                foreach (var r in ranges)
                {
                    if (r.End <= r.Start) continue;
                    if (merged.Count > 0 && r.Start <= merged[merged.Count - 1].End)
                        merged[merged.Count - 1] = (merged[merged.Count - 1].Start, Math.Max(merged[merged.Count - 1].End, r.End));
                    else merged.Add(r);
                }
                foreach (var r in merged)
                {
                    // The claimed range starting last before this one ends: disjoint ranges, so it is the
                    // only one that can reach into it.
                    if (_claimed.Count == 0) break;
                    // Reverse, not Count or Max: a view's Count walks it on .NET Framework.
                    var before = _claimed.GetViewBetween((long.MinValue, long.MinValue), (r.End - 1, long.MaxValue));
                    foreach (var last in before.Reverse())
                    {
                        if (last.End > r.Start) { clash = last; return false; }
                        break;
                    }
                }
                foreach (var r in merged) _claimed.Add(r);
                return true;
            }
        }

        // ==============================================================
        //  IFD
        // ==============================================================

        private struct TiffEntry
        {
            internal int Type;
            internal long Count;
            internal long ValueOffset;      // the field's own offset in the file
            internal bool Inline;           // value fits the 4-byte field
        }

        private static bool TryFindIfd(byte[] bytes, int pageIndex, bool bigEndian, out long ifd)
        {
            ifd = ReadU32(bytes, 4, bigEndian);
            var visited = new HashSet<long>();

            for (int i = 0; i < pageIndex; i++)
            {
                if (ifd <= 0 || ifd + 2 > bytes.Length)
                {
                    _lastError = "the IFD chain ends before page "
                                 + pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return false;
                }
                if (!visited.Add(ifd))
                {
                    _lastError = "the IFD chain is circular";
                    return false;
                }

                int entries = ReadU16(bytes, ifd, bigEndian);
                long next = ifd + 2 + ((long)entries * 12);
                if (next + 4 > bytes.Length)
                {
                    _lastError = "an IFD runs past the end of the file";
                    return false;
                }
                ifd = ReadU32(bytes, next, bigEndian);
            }

            if (ifd <= 0 || ifd + 2 > bytes.Length)
            {
                _lastError = "the IFD chain ends before page "
                             + pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }

        private static bool TryReadIfd(byte[] bytes, long ifd, bool bigEndian,
                                       Dictionary<int, TiffEntry> into)
        {
            int entries = ReadU16(bytes, ifd, bigEndian);
            long p = ifd + 2;

            if (p + ((long)entries * 12) + 4 > bytes.Length)
            {
                _lastError = "an IFD runs past the end of the file";
                return false;
            }

            for (int i = 0; i < entries; i++, p += 12)
            {
                int tag = ReadU16(bytes, p, bigEndian);
                int type = ReadU16(bytes, p + 2, bigEndian);
                long count = ReadU32(bytes, p + 4, bigEndian);

                int size = TypeSize(type);
                // A field of a type TIFF 6.0 does not define has no size, so any count fits in any
                // file — and an array of that count would be allocated whole: 268 million entries of
                // an 86-byte file. Dropped, as a field pointing outside the file is.
                if (size == 0) continue;
                long total = size * count;

                var entry = new TiffEntry { Type = type, Count = count };
                if (total <= 4 && total > 0)
                {
                    entry.Inline = true;
                    entry.ValueOffset = p + 8;
                }
                else
                {
                    entry.ValueOffset = ReadU32(bytes, p + 8, bigEndian);
                    if (entry.ValueOffset < 0 || entry.ValueOffset + total > bytes.Length)
                        continue;      // a tag pointing outside the file is dropped, not fatal
                }

                into[tag] = entry;
            }

            return true;
        }

        private static int TypeSize(int type)
        {
            switch (type)
            {
                case 1: case 2: case 6: case 7: return 1;   // BYTE, ASCII, SBYTE, UNDEFINED
                case 3: case 8: return 2;                   // SHORT, SSHORT
                case 4: case 9: case 11: return 4;          // LONG, SLONG, FLOAT
                case 5: case 10: case 12: return 8;         // RATIONAL, SRATIONAL, DOUBLE
                default: return 0;
            }
        }

        private static long Value(byte[] bytes, TiffEntry entry, int index, bool bigEndian)
        {
            int size = TypeSize(entry.Type);
            long at = entry.ValueOffset + ((long)index * size);
            if (at < 0 || at + size > bytes.Length) return 0;

            switch (size)
            {
                case 1: return bytes[at];
                case 2: return ReadU16(bytes, at, bigEndian);
                case 4: return ReadU32(bytes, at, bigEndian);
                default: return 0;
            }
        }

        private static long Scalar(byte[] bytes, Dictionary<int, TiffEntry> tags, int tag,
                                   bool bigEndian, long fallback)
            => tags.TryGetValue(tag, out TiffEntry e) && e.Count >= 1
                ? Value(bytes, e, 0, bigEndian)
                : fallback;

        private static long[] Array(byte[] bytes, Dictionary<int, TiffEntry> tags, int tag,
                                    bool bigEndian)
        {
            if (!tags.TryGetValue(tag, out TiffEntry e) || e.Count <= 0) return null;
            if (e.Count > int.MaxValue / 8) return null;

            var values = new long[e.Count];
            for (int i = 0; i < values.Length; i++) values[i] = Value(bytes, e, i, bigEndian);
            return values;
        }

        // ==============================================================
        //  The image
        // ==============================================================

        private const int TagImageWidth = 256;
        private const int TagImageLength = 257;
        private const int TagBitsPerSample = 258;
        private const int TagCompression = 259;
        private const int TagPhotometric = 262;
        private const int TagFillOrder = 266;
        private const int TagStripOffsets = 273;
        private const int TagSamplesPerPixel = 277;
        private const int TagRowsPerStrip = 278;
        private const int TagStripByteCounts = 279;
        private const int TagPlanarConfiguration = 284;
        private const int TagT4Options = 292;
        private const int TagT6Options = 293;
        private const int TagPredictor = 317;
        private const int TagColorMap = 320;

        private static bool TryDecodeIfd(byte[] bytes, Dictionary<int, TiffEntry> tags,
            bool bigEndian, StripLedger earlierPages, out byte[] rgb, out int width, out int height)
        {
            rgb = null;

            long w = Scalar(bytes, tags, TagImageWidth, bigEndian, 0);
            long h = Scalar(bytes, tags, TagImageLength, bigEndian, 0);
            width = 0; height = 0;

            if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue)
            {
                _lastError = "the page declares no usable dimensions";
                return false;
            }
            if (w * h > MaxPixels)
            {
                _lastError = "the page is " + w.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + "x" + h.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + ", over the " + MaxPixels.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " pixel cap";
                return false;
            }

            int samples = (int)Scalar(bytes, tags, TagSamplesPerPixel, bigEndian, 1);
            long[] bits = Array(bytes, tags, TagBitsPerSample, bigEndian);
            int bitsPerSample = bits != null && bits.Length > 0 ? (int)bits[0] : 1;

            long compression = Scalar(bytes, tags, TagCompression, bigEndian, 1);
            long photometric = Scalar(bytes, tags, TagPhotometric, bigEndian, 1);
            long planar = Scalar(bytes, tags, TagPlanarConfiguration, bigEndian, 1);

            if (planar != 1)
            {
                _lastError = "PlanarConfiguration 2 (one strip per channel) is not decoded";
                return false;
            }
            if (compression == 6 || compression == 7)
            {
                _lastError = "JPEG-compressed TIFF is not decoded: the tables live in the IFD "
                             + "rather than in the strip";
                return false;
            }
            if (bitsPerSample != 1 && bitsPerSample != 4 && bitsPerSample != 8)
            {
                _lastError = bitsPerSample.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " bits per sample is not decoded; 1, 4 and 8 are";
                return false;
            }
            if (samples < 1 || samples > 4)
            {
                _lastError = samples.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " samples per pixel is not decoded; 1 to 4 are";
                return false;
            }
            if (tags.ContainsKey(322) || tags.ContainsKey(324))
            {
                _lastError = "tiled TIFF is not decoded; only strips are";
                return false;
            }
            // ToRgb reads 0 and 1 as grey, 2 as RGB and 3 as a palette. Anything else was read as
            // one of those: separated CMYK (5) took C, M and Y for R, G and B, an inverted colour
            // image with no error, and YCbCr (6) came out as tinted RGB. Refused by name instead.
            if (photometric < 0 || photometric > 3)
            {
                _lastError = "PhotometricInterpretation "
                             + photometric.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " is not decoded; 0 to 3 (grey, RGB, palette) are";
                return false;
            }

            long[] stripOffsets = Array(bytes, tags, TagStripOffsets, bigEndian);
            long[] stripCounts = Array(bytes, tags, TagStripByteCounts, bigEndian);
            if (stripOffsets == null || stripCounts == null)
            {
                _lastError = "the page names no strips";
                return false;
            }

            long rowsPerStrip = Scalar(bytes, tags, TagRowsPerStrip, bigEndian, h);
            if (rowsPerStrip <= 0) rowsPerStrip = h;

            width = (int)w;
            height = (int)h;

            int stripCount = Math.Min(stripOffsets.Length, stripCounts.Length);
            // The raster is made from the header, so the strips must be able to fill it first: a page of one
            // one-byte strip declaring forty megapixels was a 160 MB raster and a whole conversion, zero-filled.
            if ((long)stripCount * rowsPerStrip < height)
            {
                _lastError = "the page's " + stripCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " strips of " + rowsPerStrip.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " rows cover fewer than its " + height.ToString(System.Globalization.CultureInfo.InvariantCulture) + " rows";
                return false;
            }
            int rowBytes = (int)(((w * samples * bitsPerSample) + 7) / 8);
            if (compression == 1)
            {
                // Uncompressed, the strips' byte counts say what they hold before anything is read.
                long present = 0, needed = (long)rowBytes * height;
                for (int i = 0; i < stripCount && present < needed; i++)
                    if (stripCounts[i] > 0) present += Math.Min(stripCounts[i], (long)rowBytes * rowsPerStrip);
                if (present < needed)
                {
                    _lastError = "the page's strips hold " + present.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 + " of the " + needed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes its rows need";
                    return false;
                }
            }

            var raster = new byte[(long)rowBytes * height];
            int rasterRow = 0;

            long predictor = Scalar(bytes, tags, TagPredictor, bigEndian, 1);
            long fillOrder = Scalar(bytes, tags, TagFillOrder, bigEndian, 1);

            if (earlierPages != null)
            {
                var ranges = new List<(long Start, long End)>(stripCount);
                for (int i = 0; i < stripCount; i++)
                    if (stripOffsets[i] >= 0 && stripCounts[i] > 0) ranges.Add((stripOffsets[i], stripOffsets[i] + stripCounts[i]));
                if (!earlierPages.TryClaim(ranges, out var clash))
                {
                    _lastError = "the page reads bytes " + clash.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 + " to " + clash.End.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 + " an earlier page's strips read; a page is the image its own data holds";
                    return false;
                }
            }
            // Strips within a page may share bytes, and each strip's range is copied and decoded whole:
            // a page of N one-row strips each naming the whole file costs N times the file, quadratic
            // in its length. What a page's strips read is held to twice the file and the page's own
            // raster; an uncompressed strip is read only as far as the rows it supplies.
            long stripBytesLeft = 2L * bytes.Length + raster.LongLength;
            for (int i = 0; i < stripCount && rasterRow < height; i++)
            {
                long at = stripOffsets[i], length = stripCounts[i];
                if (at < 0 || length < 0 || at + length > bytes.Length)
                {
                    _lastError = "a strip runs past the end of the file";
                    return false;
                }

                int rowsThisStrip = (int)Math.Min(rowsPerStrip, height - rasterRow);
                if (compression == 1) length = Math.Min(length, (long)rowBytes * rowsThisStrip);
                if (length > stripBytesLeft)
                {
                    _lastError = "the page's strips read more than twice the file's bytes and its raster's; " +
                                 "they name the same data over and over";
                    return false;
                }
                stripBytesLeft -= length;

                var raw = new byte[length];
                Buffer.BlockCopy(bytes, (int)at, raw, 0, (int)length);
                // FillOrder 2 packs each byte's bits least significant first; unapplied, a fax scanned
                // that way would decode as bit-reversed noise. Reversed here, before any decoder,
                // every one of them reads MSB first as written.
                if (fillOrder == 2) ReverseBitsInPlace(raw);

                // A strip inflates no further than its rows: twice a full strip's bytes and a margin,
                // since a writer may pad the last strip to RowsPerStrip. It went through each filter at
                // the decompression ceiling, and the bytes past its rows were thrown away below — a
                // 20 KB strip inflated to 20 MB for a 100 × 100 page.
                long ceiling = 2L * rowBytes * Math.Min(rowsPerStrip, height) + 65_536;
                byte[] decoded = Decompress(raw, compression, rowsThisStrip, (int)w, samples,
                                            bitsPerSample, predictor, tags, bytes, bigEndian, ceiling);
                if (decoded == null) return false;

                // A strip that supplies fewer bytes than its rows need ends the page short: refused, as PNG refuses
                // a short stream, rather than a page whose cost its header chose, zero-filled past its data.
                if (decoded.LongLength < (long)rowBytes * rowsThisStrip)
                {
                    _lastError = "strip " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " holds "
                                 + decoded.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of the "
                                 + ((long)rowBytes * rowsThisStrip).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 + " bytes its rows need";
                    return false;
                }
                int copy = Math.Min(decoded.Length, rowBytes * rowsThisStrip);
                Buffer.BlockCopy(decoded, 0, raster, rasterRow * rowBytes, copy);
                rasterRow += rowsThisStrip;
            }

            rgb = ToRgb(raster, width, height, rowBytes, samples, bitsPerSample,
                        photometric, bytes, tags, bigEndian);
            if (rgb == null) return false;

            return true;
        }

        /// <summary>
        /// One strip's bytes, through whichever filter its compression names.
        /// </summary>
        /// <remarks>
        /// <para><b>Every branch delegates to the PDF filter, not to a bare decoder.</b> PackBits
        /// <i>is</i> <c>RunLengthDecode</c>, TIFF's LZW is the MSB-first one, Deflate is Flate, and
        /// G3/G4 is the CCITT filter — so the only thing this method contributes is which of them a
        /// compression number means. Going through the filter rather than the decoder underneath it
        /// also inherits each one's decompression-bomb ceiling, which a hand-rolled call would
        /// quietly do without.</para>
        ///
        /// <para><b>Predictor 2 is TIFF horizontal differencing under PDF's name</b>, so it is
        /// passed to the filter as <c>/Predictor</c> and <c>PredictorHelper</c> removes it — the
        /// same code the PDF path uses. Writing a second copy here would be two implementations of
        /// one question, and the copy would be the one nothing exercises.</para>
        /// </remarks>
        private static byte[] Decompress(byte[] raw, long compression, int rows, int columns,
                                         int samples, int bitsPerSample, long predictor,
                                         Dictionary<int, TiffEntry> tags,
                                         byte[] file, bool bigEndian, long ceiling)
        {
            PdfDictionary parms = PredictorParms(predictor, columns, samples, bitsPerSample);

            try
            {
                switch (compression)
                {
                    case 1:
                        return predictor == 2 ? PredictorHelper.Apply(raw, parms) : raw;

                    case 2:
                    case 3:
                    case 4:
                    {
                        // K < 0 is G4; K = 0 is G3 one-dimensional, which is also what compression
                        // 2 (modified Huffman) is once its byte-aligned rows are accounted for.
                        long options = compression == 4
                            ? Scalar(file, tags, TagT6Options, bigEndian, 0)
                            : Scalar(file, tags, TagT4Options, bigEndian, 0);

                        var ccitt = new PdfDictionary(5);
                        ccitt.Set(PdfName.K, new PdfNumber(
                            compression == 4 ? -1 : ((options & 1) != 0 ? 1 : 0)));
                        ccitt.Set(PdfName.Columns, new PdfNumber(columns));
                        ccitt.Set(PdfName.Rows, new PdfNumber(rows));
                        ccitt.Set(PdfName.EncodedByteAlign,
                            compression == 2 || (options & 4) != 0 ? PdfBoolean.True : PdfBoolean.False);

                        // TIFF photometric 0 (WhiteIsZero) is handled when the raster is expanded,
                        // not here: /BlackIs1 describes the CODING, and conflating the two inverts
                        // a fax twice.
                        ccitt.Set(PdfName.BlackIs1, PdfBoolean.False);

                        return new CcittFaxDecodeFilter { MaxOutputBytes = ceiling }.Decode(raw, ccitt);
                    }

                    case 5:
                        // TIFF's LZW is PDF's. A writer may use either early-change convention;
                        // the overwhelming majority use 1, which is also PDF's default.
                        return new LzwDecodeFilter { MaxOutputBytes = ceiling }.Decode(raw, parms);

                    case 8:
                    case 32946:
                        return new FlateDecodeFilter { MaxOutputBytes = ceiling }.Decode(raw, parms);

                    case 32773:
                    {
                        byte[] unpacked = new RunLengthDecodeFilter { MaxOutputBytes = ceiling }.Decode(raw, null);
                        return predictor == 2 ? PredictorHelper.Apply(unpacked, parms) : unpacked;
                    }

                    default:
                        _lastError = "TIFF compression "
                                     + compression.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                     + " is not decoded";
                        return null;
                }
            }
            catch (Exception ex)
            {
                _lastError = "a strip compressed with method "
                             + compression.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " could not be decoded: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// The <c>/DecodeParms</c> a filter needs to remove predictor 2, or <c>null</c> when the
        /// page declares no predictor.
        /// </summary>
        private static PdfDictionary PredictorParms(long predictor, int columns, int samples,
                                                    int bitsPerSample)
        {
            if (predictor != 2) return null;

            var parms = new PdfDictionary(4);
            parms.Set(PdfName.Predictor, new PdfNumber(2));
            parms.Set(PdfName.Colors, new PdfNumber(samples));
            parms.Set(PdfName.BitsPerComponent, new PdfNumber(bitsPerSample));
            parms.Set(PdfName.Columns, new PdfNumber(columns));
            return parms;
        }

        /// <summary>Expands the decoded raster to 8-bit RGB.</summary>
        private static byte[] ToRgb(byte[] raster, int width, int height, int rowBytes,
                                    int samples, int bitsPerSample, long photometric,
                                    byte[] file, Dictionary<int, TiffEntry> tags, bool bigEndian)
        {
            var rgb = new byte[(long)width * height * 3];

            // Photometric 0 is WhiteIsZero, which is the fax convention and the one an inverted
            // page comes from; 3 is a palette.
            bool whiteIsZero = photometric == 0;
            long[] palette = photometric == 3 ? Array(file, tags, TagColorMap, bigEndian) : null;

            if (photometric == 3 && (palette == null || palette.Length < 3))
            {
                _lastError = "the page declares a palette but carries no usable /ColorMap";
                return null;
            }

            int paletteEntries = palette != null ? palette.Length / 3 : 0;
            int max = (1 << bitsPerSample) - 1;

            for (int y = 0; y < height; y++)
            {
                long rowStart = (long)y * rowBytes;

                for (int x = 0; x < width; x++)
                {
                    int d = ((y * width) + x) * 3;
                    byte r, g, b;

                    if (palette != null)
                    {
                        int index = ReadSample(raster, rowStart, x, 0, 1, bitsPerSample);
                        if (index >= paletteEntries) index = paletteEntries > 0 ? paletteEntries - 1 : 0;

                        // TIFF's /ColorMap is three consecutive runs of 16-bit values -- all reds,
                        // then all greens, then all blues -- not interleaved triples. The high byte,
                        // as TIFF readers take it: a colour map written from
                        // 8-bit values stores each as v * 257, whose high byte is v exactly.
                        r = (byte)(palette[index] >> 8);
                        g = (byte)(palette[paletteEntries + index] >> 8);
                        b = (byte)(palette[(2 * paletteEntries) + index] >> 8);
                    }
                    else if (samples >= 3)
                    {
                        r = Scale(ReadSample(raster, rowStart, x, 0, samples, bitsPerSample), max);
                        g = Scale(ReadSample(raster, rowStart, x, 1, samples, bitsPerSample), max);
                        b = Scale(ReadSample(raster, rowStart, x, 2, samples, bitsPerSample), max);
                    }
                    else
                    {
                        byte grey = Scale(ReadSample(raster, rowStart, x, 0, samples, bitsPerSample), max);
                        if (whiteIsZero) grey = (byte)(255 - grey);
                        r = g = b = grey;
                    }

                    rgb[d] = r; rgb[d + 1] = g; rgb[d + 2] = b;
                }
            }

            return rgb;
        }

        /// <summary>
        /// A sample to a byte, through <see cref="RasterSample.ScaleToByteFromMax"/>, the one rule.
        /// This was a truncating copy of its own; at the 1, 4 and 8 bits it is called with the two
        /// agree exactly, so the copy was the only difference.
        /// </summary>
        private static byte Scale(int value, int max)
            => (byte)RasterSample.ScaleToByteFromMax(value, max);

        /// <summary>Reverses the bit order of every byte (TIFF FillOrder 2 to MSB first).</summary>
        private static void ReverseBitsInPlace(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                int v = data[i];
                v = ((v & 0xF0) >> 4) | ((v & 0x0F) << 4);
                v = ((v & 0xCC) >> 2) | ((v & 0x33) << 2);
                v = ((v & 0xAA) >> 1) | ((v & 0x55) << 1);
                data[i] = (byte)v;
            }
        }

        private static int ReadSample(byte[] raster, long rowStart, int x, int channel,
                                      int samples, int bitsPerSample)
        {
            long bit = ((long)x * samples + channel) * bitsPerSample;
            long at = rowStart + (bit >> 3);
            if (at < 0 || at >= raster.Length) return 0;

            if (bitsPerSample == 8) return raster[at];

            int shift = 8 - bitsPerSample - (int)(bit & 7);
            return (raster[at] >> shift) & ((1 << bitsPerSample) - 1);
        }

        private static int ReadU16(byte[] d, long o, bool bigEndian)
        {
            if (o < 0 || o + 2 > d.Length) return 0;
            return bigEndian ? (d[o] << 8) | d[o + 1] : (d[o + 1] << 8) | d[o];
        }

        private static long ReadU32(byte[] d, long o, bool bigEndian)
        {
            if (o < 0 || o + 4 > d.Length) return 0;
            return bigEndian
                ? ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3]
                : ((long)d[o + 3] << 24) | ((long)d[o + 2] << 16) | ((long)d[o + 1] << 8) | d[o];
        }
    }
}
