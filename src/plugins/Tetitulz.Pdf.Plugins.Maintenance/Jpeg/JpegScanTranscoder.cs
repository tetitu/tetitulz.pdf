// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Maintenance.Jpeg
{
    /// <summary>
    /// Walks a baseline JPEG's entropy-coded scan, handing every Huffman symbol and its trailing
    /// raw bits to a sink — and, given a set of tables, writes that same sequence back out.
    ///
    /// <para><b>This is where the losslessness comes from, and it is a property of what is NOT
    /// done.</b> Nothing here reconstructs a DC value, undoes a differential prediction, dequantises
    /// or transforms anything. A scan is read as a sequence of
    /// <c>(table, symbol, extra bits)</c> triples and written back as the same sequence under
    /// different codes. The DCT coefficients are therefore not merely preserved — they are never
    /// computed, so there is nothing that could round differently.</para>
    ///
    /// <para><b>One walk, two passes.</b> The histogram pass and the encode pass call the same
    /// <see cref="TryWalk"/>. A second copy of this decode loop that drifted from the first would
    /// produce a stream whose symbols no longer match the tables built for them — an image that
    /// decodes to noise — so the sink indirection is load-bearing rather than tidiness.</para>
    /// </summary>
    internal static class JpegScanTranscoder
    {
        /// <summary>The 64 coefficients of a block (§A.3.3).</summary>
        private const int BlockCoefficients = 64;

        /// <summary>Receives one Huffman symbol and the raw bits that follow it.</summary>
        /// <param name="tableClass">0 for a DC table, 1 for AC (§B.2.4.2).</param>
        /// <param name="tableId">Which of the four tables of that class the symbol was coded with.</param>
        /// <param name="symbol">The decoded Huffman symbol.</param>
        /// <param name="extraBits">
        /// The raw additional bits following the symbol, right-aligned. Passed through verbatim
        /// rather than decoded, so re-encoding reproduces the coefficient exactly.
        /// </param>
        /// <param name="extraBitCount">How many of those bits are significant; 0 when there are none.</param>
        internal delegate void SymbolSink(int tableClass, int tableId, int symbol,
            int extraBits, int extraBitCount);

        /// <summary>One component as the scan header describes it.</summary>
        internal readonly struct ScanComponent
        {
            internal ScanComponent(int blocksPerMcu, int dcTable, int acTable)
            {
                BlocksPerMcu = blocksPerMcu;
                DcTable = dcTable;
                AcTable = acTable;
            }

            internal int BlocksPerMcu { get; }
            internal int DcTable { get; }
            internal int AcTable { get; }
        }

        /// <summary>Everything the walk needs that lives outside the entropy data itself.</summary>
        internal sealed class ScanLayout
        {
            internal ScanComponent[] Components;
            internal int McuCount;
            internal int RestartInterval;
            internal JpegHuffmanTable[] DcTables;      // by table id, 0..3
            internal JpegHuffmanTable[] AcTables;
        }

        /// <summary>
        /// Decodes the scan starting at <paramref name="start"/>, reporting every symbol to
        /// <paramref name="sink"/>.
        /// </summary>
        /// <param name="end">Index one past the last entropy byte consumed, i.e. where the marker
        /// that ended the scan begins.</param>
        /// <param name="data">The whole JPEG file.</param>
        /// <param name="start">Index of the first entropy-coded byte, after the scan header.</param>
        /// <param name="layout">The scan's components and their Huffman tables.</param>
        /// <param name="sink">Receives every symbol decoded, in order.</param>
        /// <param name="reason">Why the walk stopped early, or <c>null</c> on success.</param>
        internal static bool TryWalk(byte[] data, int start, ScanLayout layout, SymbolSink sink,
            out int end, out string reason)
        {
            end = start;
            reason = null;

            var reader = new BitReader(data, start);
            int restartCountdown = layout.RestartInterval;

            for (int mcu = 0; mcu < layout.McuCount; mcu++)
            {
                if (layout.RestartInterval > 0 && restartCountdown == 0)
                {
                    if (!reader.TryConsumeRestart(out reason)) return false;
                    restartCountdown = layout.RestartInterval;
                }

                foreach (var component in layout.Components)
                {
                    for (int block = 0; block < component.BlocksPerMcu; block++)
                    {
                        if (!TryWalkBlock(reader, layout, component, sink, out reason)) return false;
                    }
                }

                if (layout.RestartInterval > 0) restartCountdown--;
            }

            // Whatever is left must be a marker, not more entropy data. A scan that under-runs its
            // own MCU count is a file we have mis-read, and re-encoding it would truncate the image.
            end = reader.MarkerPosition();
            return true;
        }

        private static bool TryWalkBlock(BitReader reader, ScanLayout layout, ScanComponent component,
            SymbolSink sink, out string reason)
        {
            reason = null;

            var dc = layout.DcTables[component.DcTable];
            var ac = layout.AcTables[component.AcTable];

            if (dc == null || ac == null)
            {
                reason = "the scan selects a Huffman table the file never defined";
                return false;
            }

            // ---- DC coefficient (§F.2.2.1): one symbol giving the magnitude category.
            if (!reader.TryDecodeSymbol(dc, out int category, out reason)) return false;
            if (category < 0 || category > 16)
            {
                reason = "a DC magnitude category of " + category + " is out of range";
                return false;
            }

            int bits = 0;
            if (category > 0 && !reader.TryReadBits(category, out bits, out reason)) return false;
            sink(0, component.DcTable, category, bits, category);

            // ---- AC coefficients (§F.2.2.2): run-length/size pairs to the end of the block.
            int k = 1;
            while (k < BlockCoefficients)
            {
                if (!reader.TryDecodeSymbol(ac, out int runSize, out reason)) return false;

                int run = runSize >> 4;
                int size = runSize & 0x0F;

                if (size == 0)
                {
                    sink(1, component.AcTable, runSize, 0, 0);

                    // 0xF0 is ZRL, sixteen zeroes; anything else with size 0 is EOB.
                    if (run != 15) break;
                    k += 16;
                    continue;
                }

                if (!reader.TryReadBits(size, out int acBits, out reason)) return false;
                sink(1, component.AcTable, runSize, acBits, size);

                k += run + 1;
            }

            if (k > BlockCoefficients)
            {
                reason = "a block's run lengths overrun its 64 coefficients";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Re-encodes a walk's symbols under <paramref name="newDc"/> / <paramref name="newAc"/>, producing the entropy bytes
        /// of the new scan. Restart markers are re-emitted at the same MCU boundaries.
        /// </summary>
        internal static byte[] Encode(byte[] data, int start, ScanLayout layout,
            JpegHuffmanTable[] newDc, JpegHuffmanTable[] newAc, out string reason)
        {
            var writer = new BitWriter();

            if (!TryWalkForEncode(data, start, layout, newDc, newAc, writer, out reason))
                return null;

            return writer.ToArray();
        }

        /// <summary>
        /// The encode pass. It repeats <see cref="TryWalk"/>'s structure rather than reusing it,
        /// for one reason: the writer must know where the MCU boundaries are so a restart marker
        /// lands in the right place, and a symbol sink cannot see them. The two loops are kept
        /// adjacent and identical in shape so a change to one is visibly a change to the other.
        /// </summary>
        private static bool TryWalkForEncode(byte[] data, int start, ScanLayout layout,
            JpegHuffmanTable[] newDc, JpegHuffmanTable[] newAc, BitWriter writer, out string reason)
        {
            reason = null;

            var reader = new BitReader(data, start);
            int restartCountdown = layout.RestartInterval;
            int restartMarker = 0;

            for (int mcu = 0; mcu < layout.McuCount; mcu++)
            {
                if (layout.RestartInterval > 0 && restartCountdown == 0)
                {
                    if (!reader.TryConsumeRestart(out reason)) return false;

                    writer.PadToByteBoundary();
                    writer.WriteMarker((byte)(0xD0 + (restartMarker & 7)));
                    restartMarker++;

                    restartCountdown = layout.RestartInterval;
                }

                foreach (var component in layout.Components)
                {
                    for (int block = 0; block < component.BlocksPerMcu; block++)
                    {
                        if (!TryTranscodeBlock(reader, layout, component, newDc, newAc, writer,
                                out reason))
                            return false;
                    }
                }

                if (layout.RestartInterval > 0) restartCountdown--;
            }

            writer.PadToByteBoundary();
            return true;
        }

        private static bool TryTranscodeBlock(BitReader reader, ScanLayout layout,
            ScanComponent component, JpegHuffmanTable[] newDc, JpegHuffmanTable[] newAc,
            BitWriter writer, out string reason)
        {
            reason = null;

            var dc = layout.DcTables[component.DcTable];
            var ac = layout.AcTables[component.AcTable];
            var dcOut = newDc[component.DcTable];
            var acOut = newAc[component.AcTable];

            if (dcOut == null || acOut == null)
            {
                reason = "no replacement table was generated for a table the scan uses";
                return false;
            }

            if (!reader.TryDecodeSymbol(dc, out int category, out reason)) return false;
            if (!writer.TryWriteSymbol(dcOut, category, out reason)) return false;

            if (category > 0)
            {
                if (!reader.TryReadBits(category, out int bits, out reason)) return false;
                writer.WriteBits(bits, category);
            }

            int k = 1;
            while (k < BlockCoefficients)
            {
                if (!reader.TryDecodeSymbol(ac, out int runSize, out reason)) return false;
                if (!writer.TryWriteSymbol(acOut, runSize, out reason)) return false;

                int run = runSize >> 4;
                int size = runSize & 0x0F;

                if (size == 0)
                {
                    if (run != 15) break;
                    k += 16;
                    continue;
                }

                if (!reader.TryReadBits(size, out int acBits, out reason)) return false;
                writer.WriteBits(acBits, size);

                k += run + 1;
            }

            return true;
        }

        // -------------------------------------------------------------------
        //  Bit I/O
        // -------------------------------------------------------------------

        /// <summary>
        /// Reads the entropy-coded segment a bit at a time, undoing the byte stuffing of §B.1.1.5:
        /// a <c>0xFF</c> in the data is written as <c>0xFF 0x00</c>, and any other <c>0xFF xx</c> is
        /// a marker that ends the segment.
        /// </summary>
        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _position;
            private int _bitBuffer;
            private int _bitCount;
            private bool _atMarker;

            internal BitReader(byte[] data, int start)
            {
                _data = data;
                _position = start;
            }

            /// <summary>Where the marker that ends the scan begins, once the walk is complete.</summary>
            internal int MarkerPosition()
            {
                // Any bits still buffered are the pad of the final byte, which is not data.
                return _position;
            }

            private bool TryNextBit(out int bit, out string reason)
            {
                reason = null;
                bit = 0;

                if (_bitCount == 0)
                {
                    if (_atMarker)
                    {
                        reason = "the scan asked for more data than it contains";
                        return false;
                    }

                    if (_position >= _data.Length)
                    {
                        reason = "the scan ran off the end of the file";
                        return false;
                    }

                    byte value = _data[_position++];

                    if (value == 0xFF)
                    {
                        if (_position >= _data.Length)
                        {
                            reason = "the file ends on a 0xFF inside the scan";
                            return false;
                        }

                        byte next = _data[_position];
                        if (next == 0x00)
                        {
                            _position++;              // stuffed byte: the datum really is 0xFF
                        }
                        else
                        {
                            _position--;              // leave the marker where the caller can see it
                            _atMarker = true;
                            reason = "the scan reached a marker before decoding every MCU";
                            return false;
                        }
                    }

                    _bitBuffer = value;
                    _bitCount = 8;
                }

                _bitCount--;
                bit = (_bitBuffer >> _bitCount) & 1;
                return true;
            }

            internal bool TryReadBits(int count, out int value, out string reason)
            {
                value = 0;
                reason = null;

                for (int i = 0; i < count; i++)
                {
                    if (!TryNextBit(out int bit, out reason)) return false;
                    value = (value << 1) | bit;
                }

                return true;
            }

            /// <summary>§F.2.2.3's decode procedure: lengthen the code until the table terminates it.</summary>
            internal bool TryDecodeSymbol(JpegHuffmanTable table, out int symbol, out string reason)
            {
                symbol = -1;

                if (!TryNextBit(out int bit, out reason)) return false;
                int code = bit;

                for (int length = 1; length <= JpegHuffmanTable.MaxCodeLength; length++)
                {
                    if (table.Terminates(length, code))
                    {
                        symbol = table.SymbolFor(length, code);
                        if (symbol < 0)
                        {
                            reason = "a Huffman code resolved outside its table's symbol list";
                            return false;
                        }
                        return true;
                    }

                    if (length == JpegHuffmanTable.MaxCodeLength) break;

                    if (!TryNextBit(out bit, out reason)) return false;
                    code = (code << 1) | bit;
                }

                reason = "the scan contains a Huffman code no table defines";
                return false;
            }

            /// <summary>
            /// Consumes an <c>RSTn</c> marker (§B.2.1). The remaining bits of the current byte are
            /// pad and are discarded, which is why the encoder pads on the same boundary.
            /// </summary>
            internal bool TryConsumeRestart(out string reason)
            {
                reason = null;
                _bitCount = 0;

                // Skip any fill bytes before the marker (§B.1.1.2 permits them).
                while (_position + 1 < _data.Length && _data[_position] == 0xFF
                       && _data[_position + 1] == 0xFF)
                    _position++;

                if (_position + 1 >= _data.Length)
                {
                    reason = "the file ends where a restart marker was expected";
                    return false;
                }

                if (_data[_position] != 0xFF || _data[_position + 1] < 0xD0 || _data[_position + 1] > 0xD7)
                {
                    reason = "a restart marker was expected and something else was found";
                    return false;
                }

                _position += 2;
                _atMarker = false;
                return true;
            }
        }

        /// <summary>
        /// Writes the entropy-coded segment, re-applying the byte stuffing of §B.1.1.5 and padding
        /// an incomplete final byte with one-bits as §F.1.2.1 requires.
        /// </summary>
        private sealed class BitWriter
        {
            private readonly List<byte> _output = new List<byte>();
            private int _buffer;
            private int _count;

            internal void WriteBits(int value, int length)
            {
                for (int i = length - 1; i >= 0; i--)
                {
                    _buffer = (_buffer << 1) | ((value >> i) & 1);
                    _count++;

                    if (_count != 8) continue;

                    byte emitted = (byte)_buffer;
                    _output.Add(emitted);
                    if (emitted == 0xFF) _output.Add(0x00);      // stuffing

                    _buffer = 0;
                    _count = 0;
                }
            }

            internal bool TryWriteSymbol(JpegHuffmanTable table, int symbol, out string reason)
            {
                reason = null;

                int length = table.Length(symbol);
                if (length <= 0)
                {
                    reason = "a symbol present in the scan has no code in the generated table";
                    return false;
                }

                WriteBits(table.Code(symbol), length);
                return true;
            }

            /// <summary>Pads with one-bits, which §F.1.2.1 requires so the pad cannot start a code.</summary>
            internal void PadToByteBoundary()
            {
                while (_count != 0) WriteBits(1, 1);
            }

            internal void WriteMarker(byte code)
            {
                _output.Add(0xFF);
                _output.Add(code);
            }

            internal byte[] ToArray() => _output.ToArray();
        }
    }
}
