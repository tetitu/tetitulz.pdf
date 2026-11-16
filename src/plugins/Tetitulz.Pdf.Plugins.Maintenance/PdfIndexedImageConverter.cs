// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Re-expresses an image's samples as indices into a palette (ISO 32000-1 §8.6.6.3,
    /// <c>[/Indexed base hival lookup]</c>).
    ///
    /// <para><b>Exactly lossless, and that is the whole reason this pass exists.</b> Every distinct
    /// colour in the image becomes one palette entry, so a pixel's colour after conversion is the
    /// same triple or quadruple of bytes it was before — this is a change of <i>storage</i>, not a
    /// quantisation. It is not a colour reduction and must never become one: an image with 257
    /// distinct colours is refused, never dithered down to 256.</para>
    ///
    /// <para><b>Why it pays.</b> A screen shot, a chart, a logo or a flat business graphic uses a few
    /// dozen colours over a million pixels, and stores three or four bytes for each of them. As
    /// indices those pixels cost one byte, or a fraction of one once the index is packed to the
    /// width the palette actually needs — a 4-colour image needs two bits. Measured over a
    /// 14-document corpus: 25 of 117 multi-component Flate images were palettable, and re-expressing
    /// them cut 311 KB to 185 KB, between 22 % and 57 % each.</para>
    ///
    /// <para>The same measurement is what makes this the right pass to have: those small-alphabet
    /// images are precisely the ones a PNG predictor <i>hurts</i>, because differencing an alphabet
    /// of <i>k</i> values yields up to 2<i>k</i>−1. Prediction and palettisation are answers to the
    /// same observation and only one of them is right for this content.</para>
    /// </summary>
    internal static class PdfIndexedImageConverter
    {
        /// <summary>
        /// §8.6.6.3 caps <c>hival</c> at 255, so an image needing more than 256 distinct colours
        /// cannot be indexed at all — and must not be made to fit by discarding any.
        /// </summary>
        internal const int MaxPaletteEntries = 256;

        /// <summary>What a successful conversion produced.</summary>
        internal sealed class PaletteImage
        {
            /// <summary>Index per pixel, packed at <see cref="BitsPerComponent"/> bits, rows padded to a byte.</summary>
            internal byte[] Indices;

            /// <summary>The lookup table: <see cref="PaletteEntries"/> entries of <c>components</c> bytes.</summary>
            internal byte[] Palette;

            /// <summary>1, 2, 4 or 8 — the narrowest width that addresses the palette.</summary>
            internal int BitsPerComponent;

            /// <summary>Palette entry count; <c>hival</c> is one less.</summary>
            internal int PaletteEntries;
        }

        /// <summary>
        /// Builds the palette form of <paramref name="samples"/>, or returns <c>null</c> when the
        /// image cannot be indexed.
        /// </summary>
        /// <returns>
        /// <c>null</c> when the geometry does not describe the buffer exactly, when the arithmetic
        /// would overflow, or when the image needs more than <see cref="MaxPaletteEntries"/> colours.
        /// The last of those is the common case and is not a failure — a photograph is simply not a
        /// palette image.
        /// </returns>
        internal static PaletteImage TryBuild(byte[] samples, int width, int height, int components)
        {
            if (samples == null) return null;
            if (width <= 0 || height <= 0) return null;
            if (components < 1 || components > 4) return null;

            long pixels = (long)width * height;
            long expected = pixels * components;
            if (pixels > int.MaxValue || expected != samples.LongLength) return null;

            var lookup = new Dictionary<int, int>();
            var palette = new List<int>();
            var indices = new byte[(int)pixels];

            for (int pixel = 0, at = 0; pixel < indices.Length; pixel++, at += components)
            {
                // Four 8-bit components fit an int exactly, so the key is the colour itself rather
                // than a hash of it -- no collision to reason about.
                int key = 0;
                for (int component = 0; component < components; component++)
                    key = (key << 8) | samples[at + component];

                if (!lookup.TryGetValue(key, out int index))
                {
                    if (palette.Count >= MaxPaletteEntries) return null;
                    index = palette.Count;
                    lookup[key] = index;
                    palette.Add(key);
                }

                indices[pixel] = (byte)index;
            }

            if (palette.Count == 0) return null;

            int bits = IndexBits(palette.Count);

            return new PaletteImage
            {
                Indices = Pack(indices, width, height, bits),
                Palette = Flatten(palette, components),
                BitsPerComponent = bits,
                PaletteEntries = palette.Count,
            };
        }

        /// <summary>
        /// The narrowest sample width that can address <paramref name="entries"/> palette entries.
        /// §7.4.4 permits 1, 2, 4 and 8; there is no 3-bit image.
        /// </summary>
        internal static int IndexBits(int entries) =>
            entries <= 2 ? 1 : entries <= 4 ? 2 : entries <= 16 ? 4 : 8;

        /// <summary>
        /// Packs one index per pixel at <paramref name="bits"/> bits.
        ///
        /// <para>Each row starts on a byte boundary and the last byte of a row is padded (§8.9.3),
        /// which is also what <c>PredictorHelper</c> and every decoder assume. Packing rows
        /// continuously instead would shift every row after the first by a few bits — an image that
        /// decodes, and shears.</para>
        /// </summary>
        private static byte[] Pack(byte[] indices, int width, int height, int bits)
        {
            if (bits == 8) return indices;

            int rowBytes = (width * bits + 7) / 8;
            var packed = new byte[rowBytes * height];
            int perByte = 8 / bits;

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * rowBytes;
                int sourceStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    int shift = 8 - bits - (x % perByte) * bits;
                    packed[rowStart + x / perByte] |= (byte)(indices[sourceStart + x] << shift);
                }
            }

            return packed;
        }

        /// <summary>Writes the packed colour keys back out as consecutive component bytes.</summary>
        private static byte[] Flatten(List<int> palette, int components)
        {
            var bytes = new byte[palette.Count * components];
            int at = 0;

            foreach (int key in palette)
            {
                // TryBuild packed the first component into the highest byte, so unpack from there.
                for (int component = 0; component < components; component++)
                    bytes[at + component] = (byte)((key >> ((components - 1 - component) * 8)) & 0xFF);
                at += components;
            }

            return bytes;
        }
    }
}
