// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Raw 8-bit samples — grey or RGB, rows packed with no padding — encoded as a PNG file.
    /// </summary>
    /// <remarks>
    /// <para><b>The public door to <c>PdfImageEncoder</c>, the one encoder.</b> That encoder is
    /// internal and takes a SkiaSharp bitmap, so the OCR plugin, which has samples and hands
    /// Tesseract a file, wrote a PNG of its own: signature, chunks, a CRC-32 and the zlib framing.
    /// This builds the bitmap and encodes it the way a rendered page is encoded, so there is one PNG
    /// writer in the tree.</para>
    /// <para>The PNG is RGB whatever the input, since the surface is 8-bit colour; a grey sample is
    /// written as three equal channels, which every PNG reader shows as the same grey.</para>
    /// </remarks>
    public static class PdfRawImageEncoder
    {
        /// <summary>
        /// Encodes <paramref name="samples"/> as a PNG.
        /// </summary>
        /// <param name="samples">
        /// <paramref name="width"/> × <paramref name="height"/> pixels, top row first, each
        /// <paramref name="components"/> bytes; bytes past the last pixel are ignored.
        /// </param>
        /// <param name="width">Pixels per row, at least 1.</param>
        /// <param name="height">Rows, at least 1.</param>
        /// <param name="components">1 for grey, 3 for RGB.</param>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="components"/> is neither 1 nor 3, or the size is not positive or exceeds
        /// <see cref="PdfLimits.MaxImagePixelArea"/>.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="samples"/> is shorter than the image.</exception>
        public static byte[] EncodePng(byte[] samples, int width, int height, int components)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (components != 1 && components != 3)
                throw new ArgumentOutOfRangeException(nameof(components), components,
                    "Only grey (1) and RGB (3) samples are encoded.");
            if (width <= 0 || height <= 0 || (long)width * height > PdfLimits.MaxImagePixelArea)
                throw new ArgumentOutOfRangeException(nameof(width),
                    $"{width}x{height} is not a size this encoder accepts: each side at least 1, and at " +
                    $"most {PdfLimits.MaxImagePixelArea} pixels.");
            if (samples.Length < (long)width * height * components)
                throw new ArgumentException(
                    $"{samples.Length} bytes cannot hold {width}x{height} pixels of {components} " +
                    "component(s).", nameof(samples));

            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            BitmapPixels.Fill(bitmap, (row, y) =>
            {
                int o = y * width * components;
                for (int x = 0; x < width; x++, o += components)
                    row[x] = components == 1
                        ? new SKColor(samples[o], samples[o], samples[o])
                        : new SKColor(samples[o], samples[o + 1], samples[o + 2]);
            });
            return PdfImageEncoder.Encode(bitmap, PdfImageFormat.Png, 100, null);
        }
    }
}
