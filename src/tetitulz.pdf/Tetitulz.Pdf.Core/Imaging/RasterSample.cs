// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>
    /// Turning an <i>n</i>-bit raster sample into an 8-bit channel — the rule every image decoder in
    /// the tree needs and four of them had written for themselves.
    /// </summary>
    /// <remarks>
    /// <para><b>One narrowing for every reader of a 16-bit sample</b> — the Builder's image
    /// decoder, DocxToPdf's PNG reader and both renderers (<c>PdfColor.Scale16To8</c>). A copy that
    /// reads a 16-bit sample as its <i>high byte</i> (0–255) and then divides by 65535 makes
    /// <c>s * 255 / 65535</c> <b>0 for every sample up to 257</b>: every 16-bit greyscale PNG would
    /// come out pure black.</para>
    ///
    /// <para><b>Rounding, not truncation</b>, matching what <c>PdfColor</c> settled on: truncating
    /// biases every channel down by half a level, and CLAUDE.md records the same truncate-vs-round
    /// split being closed between the raster and SVG renderers.</para>
    ///
    /// <para><b>Why <c>Core.Imaging</c>.</b> Builder and DocxToPdf are features and may not reach
    /// <c>PdfColor</c>, which is <c>internal</c> to Base — and Builder may not reference Base at
    /// all. Both already reference Core and already call <see cref="RasterImageInspector"/>, so this
    /// is the sanctioned "share downward into a leaf" rather than a sideways edge. No static fields,
    /// so no type initializer and no initialization edge between peers.</para>
    /// </remarks>
    public static class RasterSample
    {
        private const int MaxByte = 255;

        /// <summary>
        /// Scales a sample of <paramref name="bitDepth"/> bits to 0–255, rounding to nearest.
        /// A depth of 8 is returned unchanged; a non-positive maximum yields 0.
        /// </summary>
        public static int ScaleToByte(int sample, int bitDepth)
        {
            if (bitDepth == 8) return sample;
            int max = (1 << bitDepth) - 1;
            return max <= 0 ? 0 : (sample * MaxByte + max / 2) / max;
        }

        /// <summary>
        /// Scales a sample whose maximum is given directly rather than derived from a bit depth —
        /// the form a PNG decoder already has to hand, since it computes <c>(1 &lt;&lt; bitDepth) - 1</c>
        /// once per row.
        /// </summary>
        public static int ScaleToByteFromMax(int sample, int max)
        {
            if (max == MaxByte) return sample;
            return max <= 0 ? 0 : (sample * MaxByte + max / 2) / max;
        }

        /// <summary>
        /// Reads the <paramref name="index"/>-th single-channel sample of <paramref name="bitDepth"/>
        /// bits from <paramref name="row"/>, MSB-first, as PNG and PDF both pack them.
        /// </summary>
        /// <remarks>
        /// The 16-bit arm returns the <b>whole</b> sample. Returning only the high byte is what made
        /// Builder's greyscale path collapse to black once it was divided by 65535 — the read and
        /// the scale disagreed about what "sample" meant, which is the kind of mismatch that only
        /// shows up when the two halves live in one place.
        /// </remarks>
        public static int ReadChannelMsbFirst(byte[] row, int index, int bitDepth)
        {
            if (row == null) return 0;
            if (bitDepth == 8)
                return index < row.Length ? row[index] : 0;
            if (bitDepth == 16)
            {
                int i = index * 2;
                return i + 1 < row.Length ? (row[i] << 8) | row[i + 1] : 0;
            }

            int perByte = 8 / bitDepth;
            int bytePos = index / perByte;
            if (bytePos >= row.Length) return 0;
            int shift = 8 - bitDepth - (index % perByte) * bitDepth;
            return (row[bytePos] >> shift) & ((1 << bitDepth) - 1);
        }
    }
}
