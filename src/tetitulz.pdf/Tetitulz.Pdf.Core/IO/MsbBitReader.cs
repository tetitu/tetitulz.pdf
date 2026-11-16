// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// The single implementation of "read <c>count</c> bits, most-significant first, from an
    /// absolute bit offset into a byte array".
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Two copies had accumulated in <c>Core</c> —
    /// <c>LzwDecodeFilter.ReadBits</c> for LZW codes and <c>PdfFunctionEvaluator.ReadBits</c> for
    /// Type 0 sampled-function samples — carrying the same <c>7 - (bit % 8)</c> rule and the same
    /// <c>// MSB first</c> comment, and agreeing on every input either caller could produce. They
    /// were free to stop agreeing, and on two inputs they already had.</para>
    ///
    /// <para><b>The accumulator is <c>long</c>, and that is the whole point of the merge
    /// direction.</b> LZW's copy accumulated into an <c>int</c> with <c>result |= 1 &lt;&lt;
    /// (count - 1 - i)</c>, so a 32-bit read set the sign bit and returned a negative number, and a
    /// read wider than 32 bits was worse still — C# masks a shift count to five bits, so
    /// <c>1 &lt;&lt; 32</c> is <c>1</c>. ISO 32000-1 Table 38 lets <c>/BitsPerSample</c> reach
    /// <b>32</b>, which is exactly why the function evaluator's copy was already <c>long</c>.
    /// Consolidating the other way would have broken 32-bit sampled functions silently.</para>
    ///
    /// <para><b>Bits outside the array read as zero, and the read continues.</b> The two copies
    /// differed here in a way that is easy to miss: LZW <i>abandoned</i> the read
    /// (<c>return result</c>) while this one shifts in a zero and carries on. Those coincide
    /// whenever the out-of-range part is a suffix — which is every case a caller can reach, since
    /// both callers' offsets only ever run off the end. They come apart only for an offset that
    /// starts before the array and runs back into it, because C# truncates division toward zero:
    /// at <c>bitOffset = -8</c> over <c>{0xFF,0xFF,0xFF,0xFF}</c>, a 9-bit read gave <b>0</b> from
    /// the abandoning form and <b>1</b> from this one. Zero-fill is the defensible contract of the
    /// two — "there is no data there" is a statement about those bits, not about the rest of the
    /// read.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer. That matters here —
    /// this repo once crashed the Base test host mid-run, silently dropping ~190 tests, by giving
    /// one type an initialization edge on another.</para>
    /// </remarks>
    internal static class MsbBitReader
    {
        /// <summary>
        /// Reads <paramref name="count"/> bits MSB-first starting at absolute bit
        /// <paramref name="bitOffset"/>. Bits outside <paramref name="data"/> — before it or past
        /// its end — read as zero.
        /// </summary>
        internal static long ReadBits(byte[] data, long bitOffset, int count)
        {
            long value = 0;
            int length = data?.Length ?? 0;

            for (int i = 0; i < count; i++)
            {
                long bit = bitOffset + i;
                long byteIndex = bit / 8;
                int bitIndex = 7 - (int)(bit % 8);   // MSB first

                if (byteIndex >= 0 && byteIndex < length)
                    value = (value << 1) | (uint)((data[(int)byteIndex] >> bitIndex) & 1);
                else
                    value <<= 1;
            }

            return value;
        }
    }
}
