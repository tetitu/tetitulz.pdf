// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Applies the PNG scanline predictors (ISO 32000-1 §7.4.4.4, PNG specification §6) to raw
    /// image samples, choosing for <b>each row</b> the filter that leaves the least for Flate to
    /// compress.
    ///
    /// <para><b>Why per row rather than one fixed filter.</b> PNG filter type 2 (<i>Up</i>) is the
    /// right choice only when a row resembles the row above it. A vertical gradient, a table of
    /// ruled lines, a photograph with horizontal structure and — most commonly — anything with a
    /// repeating horizontal pattern all predict far better from the pixel to the <i>left</i>
    /// (<i>Sub</i>) or from the Paeth combination, so one fixed filter would give up most of the
    /// option's promised "10–30 % better compression" on everything but flat verticals. The heuristic here is the one
    /// the PNG specification recommends: filter each row five ways, score each by
    /// the sum of the absolute values of the filtered bytes read as signed, and keep the lowest.</para>
    ///
    /// <para><b>The decoder is the contract.</b> <c>Core.IO.Filters.PredictorHelper</c> is what
    /// reads these bytes back, and it derives <c>bytesPerPixel</c> and the row length from
    /// <c>/Colors</c>, <c>/BitsPerComponent</c> and <c>/Columns</c> by the formulas mirrored in
    /// <see cref="BytesPerPixel"/> and <see cref="RowBytes"/>. They must agree exactly: a row stride
    /// that is one byte out does not fail, it decodes to a plausible-looking smear. That helper is
    /// <c>internal</c> to Core and a plugin cannot reach it, which is also why
    /// <see cref="Paeth"/> is restated here rather than shared — the same forced duplication
    /// <c>Plugins.PostScript</c> carries for the subtractive colour model.</para>
    ///
    /// <para>This type only ever produces bytes; deciding whether they are worth committing —
    /// they are not, on data that does not predict — belongs to the caller, which compares the
    /// compressed result against what is already in the file.</para>
    /// </summary>
    internal static class PngPredictorEncoder
    {
        /// <summary>
        /// <c>/Predictor 15</c>, "PNG optimum" (§7.4.4.4, Table 10). Any value of 10 or more tells
        /// the decoder that each row carries its own filter type in a leading byte, so all of them
        /// decode identically; 15 is written because it is what this encoder actually does.
        /// </summary>
        internal const int PngOptimum = 15;

        /// <summary>The five PNG row filters: None, Sub, Up, Average, Paeth.</summary>
        private const int FilterCount = 5;

        /// <summary>
        /// The bit depths §7.4.4.4 permits. Anything else is refused rather than encoded through a
        /// stride the decoder would compute differently.
        /// </summary>
        internal static bool IsSupportedBitDepth(int bitsPerComponent) =>
            bitsPerComponent == 1 || bitsPerComponent == 2 || bitsPerComponent == 4
            || bitsPerComponent == 8 || bitsPerComponent == 16;

        /// <summary>
        /// Bytes in one unfiltered scanline. A row always starts on a byte boundary, so a sub-byte
        /// depth pads at the end and never carries bits into the next row.
        /// </summary>
        internal static long RowBytes(int columns, int colors, int bitsPerComponent)
            => ((long)columns * colors * bitsPerComponent + 7) / 8;

        /// <summary>
        /// The filter's pixel stride. PNG §6.3 rounds it up to one whole byte for depths below 8,
        /// and <c>PredictorHelper.RemovePngPredictor</c> computes exactly this — see the type
        /// summary for why a disagreement here is invisible rather than fatal.
        /// </summary>
        internal static int BytesPerPixel(int colors, int bitsPerComponent)
            => (int)Math.Max(1L, ((long)colors * bitsPerComponent + 7) / 8);

        /// <summary>
        /// Filters <paramref name="samples"/> row by row, returning
        /// <paramref name="rows"/> × (rowBytes + 1) bytes ready for Flate.
        /// </summary>
        /// <returns>
        /// <c>null</c> when the parameters are out of range or when <paramref name="samples"/> is
        /// not <b>exactly</b> the buffer the geometry describes. Exact, not "at least": a longer
        /// buffer means the dictionary does not describe the data, and filtering the first
        /// rowBytes × rows of it would silently discard the rest while looking like a saving.
        /// </returns>
        internal static byte[] Encode(byte[] samples, int columns, int colors, int bitsPerComponent,
            int rows)
        {
            if (samples == null) return null;
            if (columns <= 0 || colors <= 0 || rows <= 0) return null;
            if (!IsSupportedBitDepth(bitsPerComponent)) return null;

            // The decoder refuses more than 32 colorants outright; encoding past its ceiling would
            // produce a stream it will hand back still differenced.
            if (colors > 32) return null;

            long rowBytesLong = RowBytes(columns, colors, bitsPerComponent);
            if (rowBytesLong <= 0 || rowBytesLong > int.MaxValue / 2) return null;

            long expected = rowBytesLong * rows;
            long filteredLength = (rowBytesLong + 1) * rows;
            if (expected != samples.LongLength) return null;
            if (filteredLength > int.MaxValue) return null;

            int rowBytes = (int)rowBytesLong;
            int bytesPerPixel = BytesPerPixel(colors, bitsPerComponent);

            var output = new byte[(int)filteredLength];
            var previous = new byte[rowBytes];
            var candidates = new byte[FilterCount][];
            for (int filter = 0; filter < FilterCount; filter++) candidates[filter] = new byte[rowBytes];
            var scores = new long[FilterCount];

            int destination = 0;

            for (int row = 0; row < rows; row++)
            {
                int source = row * rowBytes;
                Array.Clear(scores, 0, FilterCount);

                for (int i = 0; i < rowBytes; i++)
                {
                    byte x = samples[source + i];
                    byte a = i >= bytesPerPixel ? samples[source + i - bytesPerPixel] : (byte)0;
                    byte b = previous[i];
                    byte c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : (byte)0;

                    byte none = x;
                    byte sub = (byte)(x - a);
                    byte up = (byte)(x - b);
                    byte average = (byte)(x - (byte)((a + b) / 2));
                    byte paeth = (byte)(x - Paeth(a, b, c));

                    candidates[0][i] = none;    scores[0] += Magnitude(none);
                    candidates[1][i] = sub;     scores[1] += Magnitude(sub);
                    candidates[2][i] = up;      scores[2] += Magnitude(up);
                    candidates[3][i] = average; scores[3] += Magnitude(average);
                    candidates[4][i] = paeth;   scores[4] += Magnitude(paeth);
                }

                // Strictly-less keeps the lowest filter number on a tie, which prefers None — the
                // right tie-break, since leaving bytes untouched preserves any run the deflater
                // would otherwise have to rediscover.
                int best = 0;
                for (int filter = 1; filter < FilterCount; filter++)
                    if (scores[filter] < scores[best]) best = filter;

                output[destination++] = (byte)best;
                Buffer.BlockCopy(candidates[best], 0, output, destination, rowBytes);
                destination += rowBytes;

                Buffer.BlockCopy(samples, source, previous, 0, rowBytes);
            }

            return output;
        }

        /// <summary>
        /// The PNG specification's row heuristic: treat each filtered byte as signed and sum the
        /// magnitudes. It approximates entropy well enough that encoders rarely use anything else,
        /// and it costs one pass over data the encoder is walking anyway.
        /// </summary>
        private static int Magnitude(byte value)
        {
            int signed = (sbyte)value;
            return signed < 0 ? -signed : signed;
        }

        /// <summary>
        /// The Paeth predictor. Byte-for-byte the function
        /// <c>Core.IO.Filters.PredictorHelper.PaethPredictor</c> applies on the way back in — it is
        /// restated because that class is <c>internal</c> to Core and this assembly is a plugin.
        /// </summary>
        private static byte Paeth(byte a, byte b, byte c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }
    }
}
