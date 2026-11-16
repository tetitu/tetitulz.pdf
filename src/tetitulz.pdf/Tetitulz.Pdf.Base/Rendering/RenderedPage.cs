// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A rendered page as raw pixels, for callers that are going to draw or process the image
    /// rather than store or transmit it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Every other raster entry point returns PNG bytes, and PNG
    /// encoding turned out to be about <b>97%</b> of a render: a blank 612x792 page at 150 dpi
    /// costs ~82 ms, of which ~79 ms is the encoder. A caller that immediately decodes those
    /// bytes back into a bitmap — which is exactly what an on-screen viewer does — pays for an
    /// encode <i>and</i> a decode to obtain pixels the renderer already had.</para>
    /// <para><b>No SkiaSharp in the signature, deliberately.</b> Returning an <c>SKBitmap</c>
    /// would be copy-free, but it would put a specific SkiaSharp version in this library's
    /// public API, and consumers would have to move in lockstep with it. This project has
    /// already been bitten by SkiaSharp versioning once (the 4.147 preview line that never
    /// shipped stable, which forced prerelease resolution on every consumer). One
    /// <c>byte[]</c> copy costs ~1-2 ms against the ~80 ms it avoids, so the trade is not close.
    /// </para>
    /// </remarks>
    public sealed class RenderedPage
    {
        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>
        /// For a band or tile from <see cref="PdfRenderSession"/>, how many paint calls the
        /// display-list gate skipped because their bound missed the region (<see cref="PaintGate"/>);
        /// 0 otherwise. Internal: the gate's liveness, for tests.
        /// </summary>
        internal int SuppressedPaints { get; set; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>
        /// Bytes per pixel row in <see cref="Pixels"/>. May exceed <c>Width * 4</c> when the
        /// underlying surface is padded, so always index rows by this rather than by width.
        /// </summary>
        public int Stride { get; }

        /// <summary>
        /// The pixel data, <b>RGBA8888 with premultiplied alpha</b> — the renderer's native
        /// surface format, handed over without conversion.
        /// </summary>
        /// <remarks>
        /// Premultiplied means each colour channel has already been scaled by alpha, so a
        /// consumer that wants straight (unassociated) alpha must divide it back out. Two common
        /// targets need a swizzle as well as that: <c>System.Drawing</c>'s
        /// <c>Format32bppPArgb</c> is premultiplied <b>B,G,R,A</b>, and WPF's <c>Pbgra32</c> is
        /// the same order. Neither is a straight memcpy from this buffer.
        /// </remarks>
        public byte[] Pixels { get; }

        /// <summary>Creates a rendered page. The array is taken by reference, not copied.</summary>
        public RenderedPage(int width, int height, int stride, byte[] pixels)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            // In long: width * 4 overflows int past 536 870 911 pixels, and a negative product
            // would let any stride through.
            if (stride < (long)width * 4) throw new ArgumentOutOfRangeException(nameof(stride),
                "Stride must be at least four bytes per pixel of width.");
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            // Every row the dimensions promise must be in the buffer — the last one need not be
            // padded to the stride. A short array would otherwise fail later, in the consumer.
            if (pixels.LongLength < (long)stride * (height - 1) + (long)width * 4)
                throw new ArgumentException(
                    "The pixel buffer is shorter than the width, height and stride describe.", nameof(pixels));

            Width = width;
            Height = height;
            Stride = stride;
            Pixels = pixels;
        }
    }
}
