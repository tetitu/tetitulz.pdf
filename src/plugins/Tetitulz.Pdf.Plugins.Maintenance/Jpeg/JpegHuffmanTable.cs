// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Maintenance.Jpeg
{
    /// <summary>
    /// One JPEG Huffman table (ISO/IEC 10918-1 §B.2.4.2): the count of codes at each of the 16
    /// permitted lengths, plus the symbols in canonical order.
    ///
    /// <para>It serves both directions. <see cref="TryBuildDecodeTables"/> derives the
    /// <c>MINCODE/MAXCODE/VALPTR</c> triple of §F.2.2.3 so a scan can be read; <see cref="Code"/>
    /// and <see cref="Length"/> give the canonical code per symbol so one can be written.
    /// <see cref="FromFrequencies"/> builds a table fitted to an image's own statistics, which is
    /// the entire point of the exercise — see <see cref="JpegLosslessOptimizer"/>.</para>
    /// </summary>
    internal sealed class JpegHuffmanTable
    {
        /// <summary>§B.2.4.2 caps a Huffman code at 16 bits.</summary>
        internal const int MaxCodeLength = 16;

        /// <summary>
        /// The working ceiling before length-limiting. A Huffman code over 256 symbols cannot
        /// exceed 255 bits, but frequencies this skewed do not occur; 32 is the usual bound and
        /// is the value the limiting loop below is written against.
        /// </summary>
        private const int MaxWorkingLength = 32;

        private const int SymbolCount = 256;

        /// <summary>The pseudo-symbol that reserves one code point so no code is all ones (§K.2).</summary>
        private const int ReservedSymbol = 256;

        private readonly int[] _minCode = new int[MaxCodeLength + 1];
        private readonly int[] _maxCode = new int[MaxCodeLength + 1];
        private readonly int[] _valuePointer = new int[MaxCodeLength + 1];

        private readonly int[] _code = new int[SymbolCount];
        private readonly int[] _length = new int[SymbolCount];

        private JpegHuffmanTable(byte[] bits, byte[] values)
        {
            Bits = bits;
            Values = values;
        }

        /// <summary>Codes of each length: <c>Bits[1]</c> … <c>Bits[16]</c>. Index 0 is unused.</summary>
        internal byte[] Bits { get; }

        /// <summary>Symbols, ordered by code length then by the order they were assigned.</summary>
        internal byte[] Values { get; }

        /// <summary>The canonical code for <paramref name="symbol"/>, valid where <see cref="Length"/> &gt; 0.</summary>
        internal int Code(int symbol) => _code[symbol];

        /// <summary>The code length in bits for <paramref name="symbol"/>, or 0 if it has none.</summary>
        internal int Length(int symbol) => _length[symbol];

        /// <summary>Reads a table out of a <c>DHT</c> segment payload.</summary>
        /// <returns><c>null</c> when the counts and the symbol list contradict each other.</returns>
        internal static JpegHuffmanTable Parse(byte[] data, int offset, int end, out int consumed,
            out string reason)
        {
            consumed = 0;
            reason = null;

            if (offset + 1 + MaxCodeLength > end)
            {
                reason = "a DHT segment ends inside its code-length counts";
                return null;
            }

            var bits = new byte[MaxCodeLength + 1];
            int total = 0;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                bits[length] = data[offset + length - 1];
                total += bits[length];
            }

            if (total == 0 || total > SymbolCount)
            {
                reason = "a DHT segment declares " + total + " symbols";
                return null;
            }

            if (offset + MaxCodeLength + total > end)
            {
                reason = "a DHT segment ends inside its symbol list";
                return null;
            }

            var values = new byte[total];
            Array.Copy(data, offset + MaxCodeLength, values, 0, total);

            consumed = MaxCodeLength + total;
            return new JpegHuffmanTable(bits, values);
        }

        /// <summary>
        /// Builds the decode triple of §F.2.2.3. Also rejects an over-full table — one whose counts
        /// need more codes than the code space has — which the parse above cannot see.
        /// </summary>
        internal bool TryBuildDecodeTables(out string reason)
        {
            reason = null;

            int code = 0;
            int index = 0;

            for (int length = 1; length <= MaxCodeLength; length++)
            {
                _valuePointer[length] = index;
                _minCode[length] = code;

                code += Bits[length];
                index += Bits[length];

                _maxCode[length] = Bits[length] == 0 ? -1 : code - 1;

                if (code > (1 << length))
                {
                    reason = "a Huffman table needs more codes of length " + length +
                             " than that length can express";
                    return false;
                }

                code <<= 1;
            }

            if (index != Values.Length)
            {
                reason = "a Huffman table's counts and symbol list disagree";
                return false;
            }

            BuildEncodeTables();
            return true;
        }

        /// <summary>§C.2's canonical assignment, kept alongside the decode tables so the two cannot
        /// describe different codes.</summary>
        private void BuildEncodeTables()
        {
            Array.Clear(_code, 0, _code.Length);
            Array.Clear(_length, 0, _length.Length);

            int code = 0;
            int index = 0;

            for (int length = 1; length <= MaxCodeLength; length++)
            {
                for (int i = 0; i < Bits[length]; i++, index++)
                {
                    byte symbol = Values[index];
                    _code[symbol] = code;
                    _length[symbol] = length;
                    code++;
                }
                code <<= 1;
            }
        }

        /// <summary>
        /// Looks up the symbol for a code of <paramref name="length"/> bits whose value is
        /// <paramref name="code"/>, or −1 when the code is not in the table.
        /// </summary>
        internal int SymbolFor(int length, int code)
        {
            if (length < 1 || length > MaxCodeLength) return -1;
            if (_maxCode[length] < 0 || code > _maxCode[length]) return -1;

            int index = _valuePointer[length] + code - _minCode[length];
            return index >= 0 && index < Values.Length ? Values[index] : -1;
        }

        /// <summary>True when a code of <paramref name="length"/> bits can end here.</summary>
        internal bool Terminates(int length, int code) =>
            length >= 1 && length <= MaxCodeLength && _maxCode[length] >= 0 && code <= _maxCode[length];

        /// <summary>
        /// Builds the table that codes <paramref name="frequencies"/> in the fewest bits, subject to
        /// the 16-bit ceiling.
        ///
        /// <para>This is the algorithm of §K.2, in its usual published form
        /// has shipped for thirty years, and it is deliberately not a from-scratch Huffman builder.
        /// Two details are easy to miss and both produce a table a decoder will reject:
        /// a pseudo-symbol is given frequency 1 so that the all-ones code is never assigned to a
        /// real symbol (§F.1.2.1.1 reserves it), and codes longer than 16 bits are folded back by
        /// the standard's adjustment loop rather than by truncation.</para>
        /// </summary>
        /// <returns><c>null</c> when the frequencies contain no symbol at all.</returns>
        internal static JpegHuffmanTable FromFrequencies(long[] frequencies)
        {
            if (frequencies == null || frequencies.Length < SymbolCount) return null;

            var freq = new long[SymbolCount + 1];
            bool any = false;
            for (int i = 0; i < SymbolCount; i++)
            {
                freq[i] = frequencies[i];
                if (freq[i] > 0) any = true;
            }

            if (!any) return null;

            freq[ReservedSymbol] = 1;

            var codeSize = new int[SymbolCount + 1];
            var others = new int[SymbolCount + 1];
            for (int i = 0; i <= SymbolCount; i++) others[i] = -1;

            while (true)
            {
                int v1 = LeastFrequent(freq, -1);
                if (v1 < 0) break;

                int v2 = LeastFrequent(freq, v1);
                if (v2 < 0) break;                 // one symbol left: the tree is complete

                freq[v1] += freq[v2];
                freq[v2] = 0;

                codeSize[v1]++;
                while (others[v1] >= 0) { v1 = others[v1]; codeSize[v1]++; }
                others[v1] = v2;

                codeSize[v2]++;
                while (others[v2] >= 0) { v2 = others[v2]; codeSize[v2]++; }
            }

            var counts = new int[MaxWorkingLength + 1];
            for (int i = 0; i <= SymbolCount; i++)
            {
                if (codeSize[i] == 0) continue;
                if (codeSize[i] > MaxWorkingLength) return null;
                counts[codeSize[i]]++;
            }

            // §K.2 figure K.3: fold every code longer than 16 bits back into the shorter lengths,
            // keeping the code space exactly full.
            for (int length = MaxWorkingLength; length > MaxCodeLength; length--)
            {
                while (counts[length] > 0)
                {
                    int shorter = length - 2;
                    while (shorter > 0 && counts[shorter] == 0) shorter--;
                    if (shorter <= 0) return null;

                    counts[length] -= 2;
                    counts[length - 1] += 1;
                    counts[shorter + 1] += 2;
                    counts[shorter] -= 1;
                }
            }

            // Drop the pseudo-symbol's code, which is the longest one still in use.
            int last = MaxCodeLength;
            while (last > 0 && counts[last] == 0) last--;
            if (last == 0) return null;
            counts[last]--;

            var bits = new byte[MaxCodeLength + 1];
            int total = 0;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                bits[length] = (byte)counts[length];
                total += counts[length];
            }

            // Symbols ordered by their ORIGINAL code length, then by symbol value — the ordering
            // §C.2's canonical assignment expects, and the one the counts above were derived from.
            var values = new List<byte>(total);
            for (int length = 1; length <= MaxWorkingLength; length++)
                for (int symbol = 0; symbol < SymbolCount; symbol++)
                    if (codeSize[symbol] == length)
                        values.Add((byte)symbol);

            if (values.Count != total) return null;

            return new JpegHuffmanTable(bits, values.ToArray());
        }

        /// <summary>
        /// The least frequent symbol still in play, excluding <paramref name="exclude"/>. Ties go to
        /// the <b>highest</b> index, which is the usual choice and what keeps the generated table
        /// reproducible.
        /// </summary>
        private static int LeastFrequent(long[] freq, int exclude)
        {
            int found = -1;
            long least = long.MaxValue;

            for (int i = 0; i <= SymbolCount; i++)
            {
                if (i == exclude || freq[i] <= 0) continue;
                if (freq[i] <= least) { least = freq[i]; found = i; }
            }

            return found;
        }

        /// <summary>Serialises the table as the body of a <c>DHT</c> segment (§B.2.4.2).</summary>
        internal void WriteSegmentBody(List<byte> output, int tableClass, int tableId)
        {
            output.Add((byte)((tableClass << 4) | tableId));
            for (int length = 1; length <= MaxCodeLength; length++) output.Add(Bits[length]);
            output.AddRange(Values);
        }

        /// <summary>Bytes this table occupies inside a <c>DHT</c> segment.</summary>
        internal int SegmentBodyLength => 1 + MaxCodeLength + Values.Length;
    }
}
