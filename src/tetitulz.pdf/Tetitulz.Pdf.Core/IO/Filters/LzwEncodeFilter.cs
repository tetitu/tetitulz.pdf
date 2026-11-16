// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Encodes a PDF <c>LZWDecode</c> stream — ISO 32000-1 §7.4.4, the inverse of
    /// <see cref="LzwDecodeFilter"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>It does not reuse the decoder's table, and could not.</b> The decoder is
    /// chain-coded — entry <i>i</i> is "entry <c>Prefix[i]</c> then <c>Suffix[i]</c>" — which
    /// answers "what string is this code?" in one walk and "what code is this string?" not at all.
    /// An encoder needs the inverse map, <c>(prefix, suffix) → code</c>, so the two share the
    /// constants, the MSB-first bit order and the code-width rule, and nothing else.</para>
    ///
    /// <para><b>The width rule is the part that has to match exactly, and the two sides count
    /// differently.</b> Both widen at <c>count >= (1 &lt;&lt; codeSize) - earlyChange</c>, but the
    /// decoder is <b>one entry behind</b>: it adds nothing for the first code after a clear, so
    /// when it reads code <i>k</i> it holds <i>k</i>-2 new entries where this holds <i>k</i>-1. The
    /// check here is therefore on the count as it stood <i>before</i> the current add.</para>
    ///
    /// <para><b>Getting that wrong does not corrupt the next code.</b> It produces a stream that
    /// decodes perfectly up to the first width change and is garbage afterwards — so a fixture too
    /// short to reach 512 table entries passes, and the file looks damaged rather than mis-encoded.
    /// A code widened early leaves every payload under about 250 codes fine and everything above
    /// it broken.</para>
    ///
    /// <para><b>LZW is not competitive with Flate, and the measurement says so rather than the
    /// reputation.</b> Flate is LZ77 plus Huffman; LZW is a dictionary alone, and PDF 1.2 added
    /// Flate precisely because of the gap. Across eight real content and appearance streams LZW was
    /// smallest <b>once</b>, on a 33-byte border, by two bytes — and on four of the eight the
    /// smallest thing was the uncompressed original, which <c>PdfOptimizer</c> already keeps. So it
    /// is not wired into the optimizer: that would be an encode per stream for a two-byte win on
    /// one stream in eight.</para>
    ///
    /// <para><b>What it is for</b> is a producer that must emit a <i>named</i> filter, and
    /// completing the encode side of a filter this assembly already decodes. Public-API reasons,
    /// not internal ones.</para>
    ///
    /// <para><b>Do not borrow from <c>Core/Imaging/GifImageDecoder</c>.</b> GIF's LZW is
    /// LSB-first and its clear code is variable; PDF's is MSB-first with a fixed 256/257 pair.</para>
    /// </remarks>
    public static class LzwEncodeFilter
    {
        /// <summary>§7.4.4's clear-table code.</summary>
        private const int ClearTable = 256;

        /// <summary>§7.4.4's end-of-data code.</summary>
        private const int EndOfData = 257;

        /// <summary>The first code a table entry may take: 0-255 are bytes, 256/257 are control.</summary>
        private const int FirstFreeCode = 258;

        /// <summary>The 12-bit code space. A full table has to be cleared before codes repeat.</summary>
        private const int MaxCodes = 1 << 12;

        /// <summary>Narrowest and widest code, in bits.</summary>
        private const int MinCodeSize = 9;
        private const int MaxCodeSize = 12;

        /// <summary>
        /// The LZW-encoded form of <paramref name="data"/>, opening with a clear code and closing
        /// with end-of-data.
        /// </summary>
        /// <param name="data">The bytes to encode; <c>null</c> and empty are treated alike.</param>
        /// <param name="earlyChange">
        /// §7.4.4's <c>/EarlyChange</c>: 1 (the default) widens the code one entry sooner than the
        /// table strictly requires. <b>A stream encoded with one value and decoded with the other
        /// is silently wrong</b>, so a caller writing anything but 1 must also write
        /// <c>/DecodeParms &lt;&lt; /EarlyChange 0 &gt;&gt;</c>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="earlyChange"/> is not 0 or 1.</exception>
        public static byte[] Encode(byte[] data, int earlyChange = 1)
        {
            if (earlyChange != 0 && earlyChange != 1)
                throw new ArgumentOutOfRangeException(
                    nameof(earlyChange), earlyChange, "/EarlyChange is 0 or 1 (ISO 32000-1 §7.4.4).");

            int n = data?.Length ?? 0;

            // A rough guess: LZW rarely beats 1:1 on the kind of input that reaches it, and the
            // writer grows by doubling anyway.
            var bits = new MsbBitWriter(n + 16);

            // (prefix << 8) | suffix -> code. The prefix is under 4096 and the suffix a byte, so
            // the key fits in twenty bits and needs no tuple allocation.
            var table = new Dictionary<int, int>(1024);

            int codeSize = MinCodeSize;
            int tableCount = FirstFreeCode;
            int current = -1;                       // the code for the string built so far

            bits.WriteBits(ClearTable, codeSize);

            for (int i = 0; i < n; i++)
            {
                byte b = data[i];

                if (current < 0) { current = b; continue; }

                int key = (current << 8) | b;
                if (table.TryGetValue(key, out int extended)) { current = extended; continue; }

                bits.WriteBits(current, codeSize);

                if (tableCount < MaxCodes)
                {
                    // THE DECODER IS ONE ENTRY BEHIND, AND THE WIDTH HAS TO BE DECIDED ON ITS
                    // COUNT, NOT OURS. It skips the add for the first code after a clear, so by
                    // the time it reads code k it has added k-2 entries where this has added k-1.
                    // Checking the count after the add therefore widens one code early — and the
                    // symptom is not a corrupt byte but a stream that decodes perfectly until the
                    // first width change and is garbage after it, which is what the independent
                    // cross-check found.
                    int countBeforeAdd = tableCount;
                    table[key] = tableCount++;

                    if (countBeforeAdd >= (1 << codeSize) - earlyChange && codeSize < MaxCodeSize)
                        codeSize++;
                }

                if (tableCount >= MaxCodes)
                {
                    // The table is full. §7.4.4 requires a clear before codes are reused, and the
                    // decoder's own comment says a stream that never clears is malformed rather
                    // than merely wasteful.
                    //
                    // The clear goes out at the CURRENT width and the next code at the narrowest,
                    // which is what the decoder does on the other side. And the next code is
                    // always a single byte, because `current` is reset to one below and the table
                    // it would have been extended through is now empty — the decoder's clear
                    // branch outputs nothing for a code >= 256, so a multi-byte code there would
                    // lose data with no error.
                    bits.WriteBits(ClearTable, codeSize);
                    table.Clear();
                    tableCount = FirstFreeCode;
                    codeSize = MinCodeSize;
                }

                current = b;
            }

            if (current >= 0)
            {
                bits.WriteBits(current, codeSize);

                // The decoder adds an entry on reading this last code, as on every code but the
                // first after a clear, and widens on that add's count before it reads end-of-data.
                // There is no add here — nothing follows to extend — so the width decision it would
                // have made is made alone. Without it, end-of-data went out one bit narrow whenever
                // the last code fell on a width change: an independent decoder and LzwDecodeFilter both read it as a
                // further code (two extra bytes at 768 bytes of random data) or a bad one.
                if (tableCount < MaxCodes && tableCount >= (1 << codeSize) - earlyChange && codeSize < MaxCodeSize)
                    codeSize++;
            }
            bits.WriteBits(EndOfData, codeSize);

            return bits.ToArray();
        }
    }
}
