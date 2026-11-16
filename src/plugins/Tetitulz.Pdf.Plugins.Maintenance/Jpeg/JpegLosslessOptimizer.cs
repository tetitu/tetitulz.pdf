// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Maintenance.Jpeg
{
    /// <summary>
    /// Re-derives a baseline JPEG's Huffman tables from its own symbol statistics and re-codes the
    /// scan under them — what <c>jpegtran -optimize</c> does, and exactly lossless.
    ///
    /// <para><b>Why it is worth having.</b> Across a 244 MB corpus, <c>DCTDecode</c> is 45.9 % of
    /// everything the optimizer emits and nothing lossless touches it. Of 1 360 JPEGs, <b>364</b>
    /// (46.1 MB) carry the ISO/IEC 10918-1 Annex K <i>example</i> tables — the generic ones a
    /// default encoder ships, fitted to no image in particular. Replacing them with tables fitted to
    /// the actual symbol histogram costs nothing in quality because <b>no coefficient is touched</b>:
    /// see <see cref="JpegScanTranscoder"/> for why none is even computed.</para>
    ///
    /// <para><b>The scope is baseline, and everything else is refused.</b> Progressive scans,
    /// arithmetic coding, 12-bit samples, hierarchical frames and multi-scan files are all declined
    /// rather than half-handled — every one of the corpus's 1 360 images is baseline, and a
    /// progressive file mangled by a baseline transcoder is a valid JPEG full of noise.</para>
    ///
    /// <para><b>Every result is verified before it is offered.</b> <see cref="TryOptimize"/> decodes
    /// its own output and compares the symbol stream to the input's, symbol for symbol and bit for
    /// bit. A transcoder bug therefore surfaces as "no saving" rather than as a corrupted image, and
    /// the losslessness claim is checked per image rather than argued from the design.</para>
    /// </summary>
    internal static class JpegLosslessOptimizer
    {
        private const byte MarkerPrefix = 0xFF;
        private const byte Soi = 0xD8;
        private const byte Eoi = 0xD9;
        private const byte Sos = 0xDA;
        private const byte Dht = 0xC4;
        private const byte Dri = 0xDD;
        private const byte Dnl = 0xDC;
        private const byte Sof0 = 0xC0;
        private const byte Sof1 = 0xC1;
        private const byte RstFirst = 0xD0;
        private const byte RstLast = 0xD7;
        private const byte Tem = 0x01;
        private const byte Dhp = 0xDE;   // §B.3.2: define hierarchical progression — the file is hierarchical

        /// <summary>Four DC and four AC tables is the most a baseline file may define (§B.2.4.2).</summary>
        private const int MaxTables = 4;

        /// <summary>§A.1.1: an 8×8 block.</summary>
        private const int BlockSize = 8;

        /// <summary>
        /// A ceiling on the work one image may cost. A hostile file can declare a frame whose MCU
        /// count is astronomical while carrying almost no data; the walk would then spend its life
        /// failing to read bits it does not have.
        /// </summary>
        private const long MaxMcus = 64L * 1024 * 1024;

        /// <summary>
        /// Rewrites <paramref name="jpeg"/> with fitted Huffman tables.
        /// </summary>
        /// <returns>
        /// <c>null</c> when the file is out of scope, malformed, or the result would not be smaller
        /// — and <paramref name="reason"/> then says which. A caller keeps the original bytes.
        /// </returns>
        internal static byte[] TryOptimize(byte[] jpeg, out string reason)
        {
            reason = null;

            if (jpeg == null || jpeg.Length < 4)
            {
                reason = "not a JPEG: fewer than four bytes";
                return null;
            }

            if (jpeg[0] != MarkerPrefix || jpeg[1] != Soi)
            {
                reason = "not a JPEG: no SOI marker";
                return null;
            }

            if (!TryParse(jpeg, out Parsed parsed, out reason)) return null;

            // ---- Pass 1: histogram every symbol under the tables the file already carries.
            var dcFrequencies = NewFrequencyTables();
            var acFrequencies = NewFrequencyTables();

            JpegScanTranscoder.SymbolSink histogram = (tableClass, tableId, symbol, bits, count) =>
            {
                var target = tableClass == 0 ? dcFrequencies : acFrequencies;
                target[tableId][symbol]++;
            };

            if (!JpegScanTranscoder.TryWalk(jpeg, parsed.ScanStart, parsed.Layout, histogram,
                    out int scanEnd, out reason))
                return null;

            // Only the one scan is re-coded and everything after it is copied: a second scan, or a
            // table or frame after it, would be read under the fitted tables.
            if (!EndsAtEoi(jpeg, scanEnd))
            {
                reason = "the scan is not followed by EOI, so another scan or segment follows it";
                return null;
            }

            // ---- Build the fitted tables, keeping the originals for any table the scan never used.
            var newDc = new JpegHuffmanTable[MaxTables];
            var newAc = new JpegHuffmanTable[MaxTables];

            for (int id = 0; id < MaxTables; id++)
            {
                if (!TryFit(dcFrequencies[id], parsed.Layout.DcTables[id], out newDc[id], out reason))
                    return null;
                if (!TryFit(acFrequencies[id], parsed.Layout.AcTables[id], out newAc[id], out reason))
                    return null;
            }

            // ---- Pass 2: re-code the same symbols under the new tables.
            byte[] entropy = JpegScanTranscoder.Encode(jpeg, parsed.ScanStart, parsed.Layout,
                newDc, newAc, out reason);
            if (entropy == null) return null;

            byte[] rebuilt = Rebuild(jpeg, parsed, newDc, newAc, entropy, scanEnd);

            if (rebuilt.Length >= jpeg.Length)
            {
                reason = "the fitted tables did not make the image smaller";
                return null;
            }

            // ---- Pass 3: the output must decode to the input's symbol stream, exactly.
            if (!ProducesTheSameSymbols(jpeg, parsed, rebuilt, out reason)) return null;

            return rebuilt;
        }

        /// <summary>
        /// Fits a table to <paramref name="frequencies"/>, or keeps <paramref name="existing"/> when
        /// the scan never used that table — an unused table still has to be emitted, because the
        /// scan header may name it.
        /// </summary>
        private static bool TryFit(long[] frequencies, JpegHuffmanTable existing,
            out JpegHuffmanTable fitted, out string reason)
        {
            reason = null;
            fitted = null;

            bool used = false;
            foreach (long count in frequencies)
                if (count > 0) { used = true; break; }

            if (!used)
            {
                fitted = existing;                  // may be null; only matters if the scan uses it
                return true;
            }

            fitted = JpegHuffmanTable.FromFrequencies(frequencies);
            if (fitted == null)
            {
                reason = "a fitted Huffman table could not be built from the symbol histogram";
                return false;
            }

            if (!fitted.TryBuildDecodeTables(out reason)) return false;
            return true;
        }

        /// <summary>
        /// Decodes the rebuilt file and checks its symbol stream against the original's.
        ///
        /// <para>This is the guard that turns "lossless by construction" into "lossless, verified".
        /// It compares the symbol <i>and</i> the raw bits that follow it, so a table that mapped two
        /// symbols the same way, or a bit writer that dropped a pad, fails here rather than in a
        /// reader.</para>
        ///
        /// <para><b>The streams are compared by their count and a SHA-256 digest</b> of each packed
        /// symbol, not by a list of them: a list would hold eight bytes a symbol, and a symbol can be
        /// one bit, so a scan would cost 64 bytes of list for each of its bytes. The
        /// rebuilt stream is this class's own output, so a defect that changed it while keeping the
        /// original's digest would be a SHA-256 collision.</para>
        /// </summary>
        private static bool ProducesTheSameSymbols(byte[] original, Parsed parsed, byte[] rebuilt,
            out string reason)
        {
            using var expected = new SymbolDigest();
            JpegScanTranscoder.SymbolSink record = (tableClass, tableId, symbol, bits, count) =>
                expected.Add(Pack(tableClass, tableId, symbol, bits, count));

            if (!JpegScanTranscoder.TryWalk(original, parsed.ScanStart, parsed.Layout, record,
                    out _, out reason))
                return false;

            if (!TryParse(rebuilt, out Parsed check, out reason))
            {
                reason = "the rebuilt image did not parse: " + reason;
                return false;
            }

            using var actual = new SymbolDigest();
            JpegScanTranscoder.SymbolSink compare = (tableClass, tableId, symbol, bits, count) =>
                actual.Add(Pack(tableClass, tableId, symbol, bits, count));

            if (!JpegScanTranscoder.TryWalk(rebuilt, check.ScanStart, check.Layout, compare,
                    out _, out reason))
            {
                reason = "the rebuilt image did not decode: " + reason;
                return false;
            }

            if (!expected.SameAs(actual))
            {
                reason = "the rebuilt image decodes to a different symbol stream";
                return false;
            }

            return true;
        }

        /// <summary>A symbol stream's count and SHA-256 digest, fed a packed symbol at a time.</summary>
        internal sealed class SymbolDigest : IDisposable
        {
            private readonly System.Security.Cryptography.IncrementalHash _hash =
                System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            private readonly byte[] _buffer = new byte[8192];
            private int _filled;
            private byte[] _digest;

            internal long Count { get; private set; }

            internal void Add(long packed)
            {
                if (_filled == _buffer.Length) Flush();
                for (int i = 0; i < 8; i++) _buffer[_filled++] = (byte)(packed >> (8 * i));
                Count++;
            }

            /// <summary>Whether the two streams held the same symbols in the same order.</summary>
            internal bool SameAs(SymbolDigest other)
            {
                if (Count != other.Count) return false;
                byte[] mine = Digest(), theirs = other.Digest();
                for (int i = 0; i < mine.Length; i++)
                    if (mine[i] != theirs[i]) return false;
                return true;
            }

            private byte[] Digest()
            {
                if (_digest != null) return _digest;
                Flush();
                return _digest = _hash.GetHashAndReset();
            }

            private void Flush()
            {
                if (_filled > 0) _hash.AppendData(_buffer, 0, _filled);
                _filled = 0;
            }

            public void Dispose() => _hash.Dispose();
        }

        /// <summary>Whether the marker at <paramref name="at"/> is EOI, after any 0xFF fill (§B.1.1.2).</summary>
        private static bool EndsAtEoi(byte[] data, int at)
        {
            if (at >= data.Length || data[at] != MarkerPrefix) return false;
            while (at < data.Length && data[at] == MarkerPrefix) at++;
            return at < data.Length && data[at] == Eoi;
        }

        private static long Pack(int tableClass, int tableId, int symbol, int bits, int count) =>
            ((long)tableClass << 56) | ((long)tableId << 48) | ((long)symbol << 40)
            | ((long)count << 32) | (uint)bits;

        private static long[][] NewFrequencyTables()
        {
            var tables = new long[MaxTables][];
            for (int i = 0; i < MaxTables; i++) tables[i] = new long[256];
            return tables;
        }

        // -------------------------------------------------------------------
        //  Marker parsing
        // -------------------------------------------------------------------

        private sealed class Parsed
        {
            internal JpegScanTranscoder.ScanLayout Layout;
            internal int ScanStart;                 // first entropy byte
            internal int SosHeaderStart;            // the 0xFF of the SOS marker
            internal List<(int Start, int Length)> DhtSegments = new List<(int, int)>();
        }

        private readonly struct FrameComponent
        {
            internal FrameComponent(int id, int h, int v)
            {
                Id = id;
                H = h;
                V = v;
            }

            internal int Id { get; }
            internal int H { get; }
            internal int V { get; }
        }

        private static bool TryParse(byte[] data, out Parsed parsed, out string reason)
        {
            parsed = new Parsed();
            reason = null;

            var dcTables = new JpegHuffmanTable[MaxTables];
            var acTables = new JpegHuffmanTable[MaxTables];
            var components = new List<FrameComponent>();

            int width = 0, height = 0;
            int restartInterval = 0;
            bool sawFrame = false;

            int position = 2;                        // past SOI

            while (position + 1 < data.Length)
            {
                if (data[position] != MarkerPrefix) { position++; continue; }

                // A run of 0xFF is legal fill before a marker code (§B.1.1.2).
                int codeAt = position + 1;
                while (codeAt < data.Length && data[codeAt] == MarkerPrefix) codeAt++;
                if (codeAt >= data.Length) break;

                byte code = data[codeAt];
                position = codeAt + 1;

                if (code == Eoi) break;
                if (code == Tem || (code >= RstFirst && code <= RstLast)) continue;

                if (position + 1 >= data.Length)
                {
                    reason = "a marker segment is truncated";
                    return false;
                }

                int length = (data[position] << 8) | data[position + 1];
                if (length < 2 || position + length > data.Length)
                {
                    reason = "a marker segment declares a length that runs past the file";
                    return false;
                }

                int body = position + 2;
                int bodyEnd = position + length;

                switch (code)
                {
                    case Sof0:
                    case Sof1:
                        if (sawFrame)
                        {
                            reason = "the file carries more than one frame";
                            return false;
                        }
                        if (!TryReadFrame(data, body, bodyEnd, components, out width, out height,
                                out reason))
                            return false;
                        sawFrame = true;
                        break;

                    case Dht:
                        parsed.DhtSegments.Add((codeAt - 1, length + 2));
                        if (!TryReadHuffmanTables(data, body, bodyEnd, dcTables, acTables, out reason))
                            return false;
                        break;

                    case Dri:
                        if (bodyEnd - body < 2)
                        {
                            reason = "a DRI segment is too short";
                            return false;
                        }
                        restartInterval = (data[body] << 8) | data[body + 1];
                        break;

                    case Dnl:
                        reason = "the frame's height is carried in a DNL marker";
                        return false;

                    case Dhp:
                        // A hierarchical file's frames follow the first scan; only that scan would be
                        // re-coded, and the frames after it decoded under the fitted tables.
                        reason = "the file is hierarchical (DHP)";
                        return false;

                    case Sos:
                        if (!sawFrame)
                        {
                            reason = "a scan appears before any frame header";
                            return false;
                        }
                        parsed.SosHeaderStart = codeAt - 1;
                        if (!TryReadScan(data, body, bodyEnd, components, dcTables, acTables,
                                width, height, restartInterval, out parsed.Layout, out reason))
                            return false;
                        parsed.ScanStart = bodyEnd;
                        return true;

                    default:
                        // SOF2 and friends: anything that is a frame header we do not handle.
                        if (code >= 0xC0 && code <= 0xCF && code != Dht && code != 0xC8 && code != 0xCC)
                        {
                            reason = "the frame is not baseline (SOF" + (code - 0xC0) + ")";
                            return false;
                        }
                        break;                       // APPn, COM, DQT and the rest are copied verbatim
                }

                position = bodyEnd;
            }

            reason = "the file has no scan";
            return false;
        }

        private static bool TryReadFrame(byte[] data, int body, int end, List<FrameComponent> components,
            out int width, out int height, out string reason)
        {
            width = 0;
            height = 0;
            reason = null;

            if (end - body < 6)
            {
                reason = "a frame header is too short";
                return false;
            }

            int precision = data[body];
            if (precision != 8)
            {
                reason = "the frame is " + precision + " bits per sample and only 8 is handled";
                return false;
            }

            height = (data[body + 1] << 8) | data[body + 2];
            width = (data[body + 3] << 8) | data[body + 4];
            int count = data[body + 5];

            if (width <= 0 || height <= 0)
            {
                reason = "the frame declares a zero dimension";
                return false;
            }

            if (count < 1 || count > 4)
            {
                reason = "the frame has " + count + " components";
                return false;
            }

            if (end - body < 6 + count * 3)
            {
                reason = "a frame header ends inside its component list";
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                int at = body + 6 + i * 3;
                int sampling = data[at + 1];
                int h = sampling >> 4;
                int v = sampling & 0x0F;

                if (h < 1 || h > 4 || v < 1 || v > 4)
                {
                    reason = "a component declares a sampling factor of " + h + "x" + v;
                    return false;
                }

                components.Add(new FrameComponent(data[at], h, v));
            }

            return true;
        }

        private static bool TryReadHuffmanTables(byte[] data, int body, int end,
            JpegHuffmanTable[] dcTables, JpegHuffmanTable[] acTables, out string reason)
        {
            reason = null;
            int at = body;

            while (at < end)
            {
                int specification = data[at];
                int tableClass = specification >> 4;
                int tableId = specification & 0x0F;

                if (tableClass > 1 || tableId >= MaxTables)
                {
                    reason = "a DHT segment names table " + tableClass + "/" + tableId;
                    return false;
                }

                var table = JpegHuffmanTable.Parse(data, at + 1, end, out int consumed, out reason);
                if (table == null) return false;
                if (!table.TryBuildDecodeTables(out reason)) return false;

                if (tableClass == 0) dcTables[tableId] = table;
                else acTables[tableId] = table;

                at += 1 + consumed;
            }

            return true;
        }

        private static bool TryReadScan(byte[] data, int body, int end, List<FrameComponent> frame,
            JpegHuffmanTable[] dcTables, JpegHuffmanTable[] acTables, int width, int height,
            int restartInterval, out JpegScanTranscoder.ScanLayout layout, out string reason)
        {
            layout = null;
            reason = null;

            if (end - body < 1)
            {
                reason = "a scan header is too short";
                return false;
            }

            int count = data[body];
            if (count < 1 || count > frame.Count)
            {
                reason = "the scan names " + count + " components of a " + frame.Count +
                         "-component frame";
                return false;
            }

            // A scan of some of the frame's components is one of several (§B.2.3, non-interleaved):
            // the rest follow it, coded under the original tables, and would be decoded under the
            // fitted ones.
            if (count < frame.Count)
            {
                reason = "the scan codes " + count + " of the frame's " + frame.Count +
                         " components, so further scans follow it";
                return false;
            }

            if (end - body < 1 + count * 2 + 3)
            {
                reason = "a scan header ends inside its component list";
                return false;
            }

            int spectralStart = data[body + 1 + count * 2];
            int spectralEnd = data[body + 2 + count * 2];
            int approximation = data[body + 3 + count * 2];

            if (spectralStart != 0 || spectralEnd != 63 || approximation != 0)
            {
                reason = "the scan is progressive (Ss=" + spectralStart + " Se=" + spectralEnd +
                         " Ah/Al=" + approximation + ")";
                return false;
            }

            int hMax = 1, vMax = 1;
            foreach (var component in frame)
            {
                if (component.H > hMax) hMax = component.H;
                if (component.V > vMax) vMax = component.V;
            }

            var scanComponents = new JpegScanTranscoder.ScanComponent[count];
            long mcuCount;

            if (count == 1)
            {
                int id = data[body + 1];
                var match = FindComponent(frame, id);
                if (match == null)
                {
                    reason = "the scan names a component the frame does not define";
                    return false;
                }

                int tables = data[body + 2];
                scanComponents[0] = new JpegScanTranscoder.ScanComponent(1, tables >> 4, tables & 0x0F);

                // §A.2.2: a single-component scan is a plain raster of that component's blocks.
                long blocksWide = CeilDiv(CeilDiv((long)width * match.Value.H, hMax), BlockSize);
                long blocksHigh = CeilDiv(CeilDiv((long)height * match.Value.V, vMax), BlockSize);
                mcuCount = blocksWide * blocksHigh;
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int id = data[body + 1 + i * 2];
                    int tables = data[body + 2 + i * 2];

                    var match = FindComponent(frame, id);
                    if (match == null)
                    {
                        reason = "the scan names a component the frame does not define";
                        return false;
                    }

                    scanComponents[i] = new JpegScanTranscoder.ScanComponent(
                        match.Value.H * match.Value.V, tables >> 4, tables & 0x0F);
                }

                // §A.2.3: an interleaved scan's MCU spans Hi x Vi blocks of every component.
                mcuCount = CeilDiv(width, (long)BlockSize * hMax) * CeilDiv(height, (long)BlockSize * vMax);
            }

            foreach (var component in scanComponents)
            {
                if (component.DcTable >= MaxTables || component.AcTable >= MaxTables)
                {
                    reason = "the scan selects a table index outside 0..3";
                    return false;
                }
            }

            if (mcuCount <= 0 || mcuCount > MaxMcus)
            {
                reason = "the frame describes " + mcuCount + " MCUs";
                return false;
            }

            layout = new JpegScanTranscoder.ScanLayout
            {
                Components = scanComponents,
                McuCount = (int)mcuCount,
                RestartInterval = restartInterval,
                DcTables = dcTables,
                AcTables = acTables,
            };

            return true;
        }

        private static FrameComponent? FindComponent(List<FrameComponent> frame, int id)
        {
            foreach (var component in frame)
                if (component.Id == id) return component;
            return null;
        }

        private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;

        // -------------------------------------------------------------------
        //  Output
        // -------------------------------------------------------------------

        /// <summary>
        /// Emits the original file with every <c>DHT</c> segment replaced by one carrying the fitted
        /// tables, and the entropy data replaced by the re-coded scan. Everything else — <c>DQT</c>,
        /// <c>APPn</c>, <c>COM</c>, the Adobe transform marker, EXIF — is copied byte for byte,
        /// because a PDF's <c>/ColorSpace</c> and <c>/Decode</c> are written against them.
        /// </summary>
        private static byte[] Rebuild(byte[] original, Parsed parsed, JpegHuffmanTable[] newDc,
            JpegHuffmanTable[] newAc, byte[] entropy, int scanEnd)
        {
            var output = new List<byte>(original.Length);

            // Everything before the first DHT (or before SOS if there is none), minus the DHTs.
            int copied = 0;
            foreach (var (start, length) in parsed.DhtSegments)
            {
                for (int i = copied; i < start; i++) output.Add(original[i]);
                copied = start + length;
            }

            for (int i = copied; i < parsed.SosHeaderStart; i++) output.Add(original[i]);

            // One DHT segment carrying every table the scan can reach.
            WriteHuffmanSegment(output, newDc, newAc);

            // The scan header, verbatim.
            for (int i = parsed.SosHeaderStart; i < parsed.ScanStart; i++) output.Add(original[i]);

            output.AddRange(entropy);

            // Everything from the marker that ended the scan onward: EOI, and any trailer.
            for (int i = scanEnd; i < original.Length; i++) output.Add(original[i]);

            return output.ToArray();
        }

        private static void WriteHuffmanSegment(List<byte> output, JpegHuffmanTable[] newDc,
            JpegHuffmanTable[] newAc)
        {
            var body = new List<byte>();

            for (int id = 0; id < MaxTables; id++)
            {
                if (newDc[id] != null) newDc[id].WriteSegmentBody(body, 0, id);
                if (newAc[id] != null) newAc[id].WriteSegmentBody(body, 1, id);
            }

            int length = body.Count + 2;

            output.Add(MarkerPrefix);
            output.Add(Dht);
            output.Add((byte)(length >> 8));
            output.Add((byte)(length & 0xFF));
            output.AddRange(body);
        }
    }
}
