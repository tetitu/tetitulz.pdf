// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// The sample buffer an OCR plugin is handed for a rendered page: the component count and bit
    /// depth the plugin is selected by, the size the buffer would need, and the packing itself.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation, two entry points.</b> <c>PdfOperations.RecognizeTextWithOcr</c>
    /// and <c>PdfOperations.RecognizeWordsWithOcr</c> each rasterise a page and hand the bytes to a
    /// plugin, and each carried its own copy of all three decisions — the same two constants, the
    /// same overflow test written out again, and the same triple-nested packing loop. They must
    /// agree: the constants are what <c>FindOcrPlugin</c> and <c>FindOcrWordPlugin</c> match a
    /// registered engine against, and the packing is what the engine then reads under that
    /// contract. An engine selected on one side's promise and fed the other side's bytes would
    /// mis-read every pixel, and nothing in either signature says which promise was made.</para>
    ///
    /// <para><b>The refusal is deliberately only half shared.</b> <see cref="TryPack"/> answers
    /// <c>null</c> with the size it could not allocate, and each caller words its own message —
    /// one says "recognise at a lower dpi", the other "render at a lower dpi", and each is accurate
    /// for the operation the caller actually performs. The <i>arithmetic</i> is the thing that had
    /// to stop being written twice; the prose describes two different operations and is not a
    /// duplicate.</para>
    ///
    /// <para><b>It reads the bitmap's buffer, a row at a time</b> (<see cref="BitmapPixels.Read"/>),
    /// where both copies called <c>GetPixel</c> — a native call per pixel. The page is rendered on
    /// an opaque background, so the bytes are the same either way; on a pixel that is neither opaque
    /// nor invisible the buffer read un-premultiplies through Skia's integer table rather than
    /// <c>GetPixel</c>'s float, and may differ by one level (<c>OcrSampleBufferTests</c> pins
    /// both).</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer.</para>
    /// </remarks>
    internal static class OcrSampleBuffer
    {
        /// <summary>
        /// Components per pixel in the buffer handed to a plugin — RGB, in that order.
        /// </summary>
        /// <remarks>
        /// This is the value both entry points select a plugin with, so it is the same constant that
        /// describes the bytes <see cref="TryPack"/> writes. Splitting the two is how an engine comes
        /// to be chosen for a layout it is not given.
        /// </remarks>
        internal const int Components = PdfColorSpaceNames.RgbComponents;

        /// <summary>Bits per component in that buffer.</summary>
        internal const int BitsPerComponent = 8;

        /// <summary>
        /// The buffer for <paramref name="bitmap"/>, or <c>null</c> when it would not fit in one
        /// array — in which case <paramref name="required"/> is the size that was refused, for the
        /// caller's own message.
        /// </summary>
        /// <param name="bitmap">The rendered page. A null bitmap is refused with a size of 0.</param>
        /// <param name="required">
        /// The byte count the buffer needs, computed in <c>long</c> so the overflow it is tested for
        /// cannot happen while computing it.
        /// </param>
        internal static byte[] TryPack(SKBitmap bitmap, out long required)
        {
            required = 0;
            if (bitmap == null) return null;

            required = (long)bitmap.Width * bitmap.Height * Components;
            if (required > int.MaxValue) return null;

            var pixels = new byte[required];
            int width = bitmap.Width;
            BitmapPixels.Read(bitmap, (row, y) =>
            {
                int o = y * width * Components;
                foreach (SKColor c in row)
                {
                    pixels[o++] = c.Red;
                    pixels[o++] = c.Green;
                    pixels[o++] = c.Blue;
                }
            });

            return pixels;
        }
    }
}
