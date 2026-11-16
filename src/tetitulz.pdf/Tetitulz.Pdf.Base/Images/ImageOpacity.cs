// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Composites a decoded opacity plane into a painted bitmap's alpha — for a <c>JPXDecode</c>
    /// image whose <c>/ColorSpace</c> the raw sample path painted (Indexed, Separation, Lab, …)
    /// and whose <c>/SMaskInData</c> channel that path could not carry
    /// (<see cref="JpxDecodeOutcome.RawOpacity"/>). One implementation for both renderers, so the
    /// raster page and the SVG's PNG agree on what the opacity does.
    /// </summary>
    internal static class ImageOpacity
    {
        /// <summary>
        /// The bitmap with <paramref name="opacity"/> (one straight byte per pixel, row-major,
        /// the bitmap's geometry) multiplied into its alpha. An <see cref="SKAlphaType.Opaque"/>
        /// bitmap cannot hold alpha, so it is copied to a new one of
        /// <paramref name="alphaTypeWithAlpha"/> (Premul for the raster renderer, Unpremul for the
        /// SVG writer's PNG) and the copy returned — the caller disposes the original; a bitmap
        /// that already has alpha is written in place and returned itself. A plane of another
        /// size is refused, reported, and the bitmap returned unchanged.
        /// </summary>
        internal static SKBitmap Apply(SKBitmap bitmap, byte[] opacity, SKAlphaType alphaTypeWithAlpha)
        {
            if (bitmap == null || opacity == null) return bitmap;
            int w = bitmap.Width, h = bitmap.Height;
            if (opacity.Length != (long)w * h)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    $"the image's opacity channel has {opacity.Length} sample(s) for a {w}x{h} image; it is drawn opaque.");
                return bitmap;
            }

            SKBitmap target = bitmap;
            if (bitmap.AlphaType == SKAlphaType.Opaque)
            {
                // Copied pixel by pixel through the one pixel walker: a canvas cannot draw onto an
                // Unpremul surface, and every source pixel is opaque, so straight is premultiplied.
                target = new SKBitmap(w, h, SKColorType.Rgba8888, alphaTypeWithAlpha);
                BitmapPixels.Map(target, bitmap, (t, source) => source);
            }

            using var mask = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            BitmapPixels.Fill(mask, (row, y) =>
            {
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte a = opacity[o + x];
                    row[x] = new SKColor(a, a, a, 255);
                }
            });
            // Straight colour in, straight colour out: the walker stores it as the surface declares.
            BitmapPixels.Map(target, mask, (p, m) => new SKColor(p.Red, p.Green, p.Blue, (byte)((p.Alpha * m.Red + 127) / 255)));
            return target;
        }
    }
}
