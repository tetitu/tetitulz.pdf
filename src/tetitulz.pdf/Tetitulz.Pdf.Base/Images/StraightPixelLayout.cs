// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Reads and writes a bitmap's pixels as <b>straight</b> (non-premultiplied)
    /// <see cref="SKColor"/>s, through the bitmap's own buffer — what a <c>SKBitmap.Pixels</c>
    /// get → mutate → set round trip does, without either full-size copy.
    /// </summary>
    /// <remarks>
    /// <para><b>The round trip costs two conversions of every pixel and a full-size array, to
    /// change however few of them the caller cares about.</b> The getter allocates an
    /// <see cref="SKColor"/> array the size of the image and un-premultiplies into it; the setter
    /// walks the whole image again and premultiplies back. One page-sized soft mask at 300 dpi
    /// measured 33 MB and ~264 ms that way — the measurement is quoted at
    /// <c>ContentStreamInterpreter.OpGraphicsState</c>, which was the first site converted.</para>
    ///
    /// <para><b>Straight rather than raw is the whole of why this type exists.</b> On a
    /// <see cref="SKAlphaType.Premul"/> surface the stored bytes are colour·α, so a remap applied
    /// to them is applied to the wrong numbers — and the error is invisible on every fully opaque
    /// pixel, which is nearly every pixel of nearly every fixture. Reproducing the property's two
    /// conversions here is what lets a caller be converted without also re-deciding what its
    /// arithmetic means.</para>
    ///
    /// <para><b>Where a caller does NOT want straight colour, it should not use this.</b> The
    /// luminosity pass reads the premultiplied byte deliberately, because colour·α is exactly the
    /// term it computes by hand — see the comment there. That site is why converting a caller to
    /// this is not mechanical.</para>
    ///
    /// <para><b>Four colour types, and one of them cannot be written.</b> The two 8888 orders carry
    /// real alpha and are read and written. <see cref="SKColorType.Rgb888x"/> is four bytes with a
    /// padding byte where alpha would be, so it reads as opaque and a store leaves the padding
    /// alone. <see cref="SKColorType.Gray8"/> is one byte and cannot hold three channels, so it is
    /// <b>read-only</b>: <see cref="CanStore"/> says which, and a caller that ignored it would be
    /// discarding two channels per pixel.</para>
    ///
    /// <para><b>Those last two are not hypothetical.</b> The SVG image path decodes to
    /// <c>Rgb888x</c> and its soft masks to <c>Gray8</c>. Handling only the 8888 orders would
    /// decline every surface that export produces and send call sites to the allocating property on
    /// every image — no pixel differing, no test failing. <c>BitmapPixels.PropertyFallbacks</c> is
    /// the evidence.</para>
    ///
    /// <para><b>The write is byte-identical to the setter; the read is not quite the getter, and
    /// the difference is measured rather than assumed.</b> Over all 65 536 (colour, alpha) pairs:
    /// storing agrees with <c>SKBitmap.Pixels</c> on every one of the 262 144 bytes, and reading
    /// differs on <b>621 of 65 536 pixels, by at most one, and never where alpha is 0 or 255</b>.
    /// SkiaSharp's getter un-premultiplies in <i>float32</i> — <c>SkColor4f::unpremul</c>
    /// multiplies by a reciprocal — so its rounding follows the representation rather than a
    /// rule: 7/14 rounds up and 11/22 rounds down. Reproducing that bit-for-bit would pin one
    /// machine's floating point and one native build's vectorisation, so this uses Skia's own
    /// integer table (<c>SkUnPreMultiply</c>) instead and states the residue.</para>
    ///
    /// <para><b>A round trip is exactly the identity either way</b>, because the difference is
    /// below the resolution of the stored byte and cannot survive the re-premultiply. It can reach
    /// output only through a caller that <i>transforms</i> colour, on a pixel that is neither
    /// opaque nor invisible, by one part in 255. <c>StraightPixelLayoutTests</c> pins all of those
    /// numbers, so a SkiaSharp upgrade that moves them is loud rather than silent.</para>
    /// </remarks>
    internal readonly struct StraightPixelLayout
    {
        /// <summary>Skia's <c>SkUnPreMultiply::gTable</c> — <c>(255 &lt;&lt; 24) / alpha</c>.</summary>
        /// <remarks>
        /// Built once. 1 KiB, and the alternative is an integer divide per channel per pixel.
        /// Index 0 is 0, which is what collapses a fully transparent pixel to black on read.
        /// </remarks>
        private static readonly uint[] UnpremultiplyScale = BuildUnpremultiplyScale();

        private readonly int _redOffset;
        private readonly bool _premultiplied;
        private readonly bool _grey;
        private readonly bool _opaque;

        private StraightPixelLayout(
            SKBitmap bitmap, int redOffset, int bytesPerPixel,
            bool premultiplied, bool grey, bool opaque, bool canStore)
        {
            _redOffset = redOffset;
            _premultiplied = premultiplied;
            _grey = grey;
            _opaque = opaque;
            BytesPerPixel = bytesPerPixel;
            CanStore = canStore;
            Stride = bitmap.RowBytes;
            Width = bitmap.Width;
            Height = bitmap.Height;
        }

        /// <summary>Bytes per row, which is not always <c>Width * BytesPerPixel</c>.</summary>
        internal int Stride { get; }

        /// <summary>The bitmap's width in pixels.</summary>
        internal int Width { get; }

        /// <summary>The bitmap's height in pixels.</summary>
        internal int Height { get; }

        /// <summary>Bytes each pixel occupies: 4 for the 8888 orders and Rgb888x, 1 for Gray8.</summary>
        internal int BytesPerPixel { get; }

        /// <summary>
        /// Whether <see cref="Set"/> may be called. False for a surface that cannot represent a
        /// colour, which today means <see cref="SKColorType.Gray8"/>.
        /// </summary>
        internal bool CanStore { get; }

        /// <summary>
        /// Reads <paramref name="bitmap"/>'s layout, or answers <c>false</c> when its pixels are
        /// in a format this cannot address.
        /// </summary>
        /// <remarks>
        /// <b>The colour type is read rather than assumed.</b> GAPS §13d records a bitmap arriving
        /// as the platform-preferred <see cref="SKColorType.Bgra8888"/> where
        /// <see cref="SKColorType.Rgba8888"/> was assumed; the two put alpha last and differ only
        /// in whether byte 0 is red or blue. Anything outside the four handled here has no offsets
        /// to trust, and a caller must keep its allocating path rather than reinterpret the buffer.
        /// </remarks>
        internal static bool TryRead(SKBitmap bitmap, out StraightPixelLayout layout)
        {
            layout = default;
            if (bitmap == null) return false;
            if (bitmap.Width <= 0 || bitmap.Height <= 0) return false;

            int redOffset, bytesPerPixel;
            bool grey = false, opaque = false, canStore = true;

            switch (bitmap.ColorType)
            {
                case SKColorType.Rgba8888:
                    redOffset = 0; bytesPerPixel = 4;
                    break;
                case SKColorType.Bgra8888:
                    redOffset = 2; bytesPerPixel = 4;
                    break;
                case SKColorType.Rgb888x:
                    // Four bytes, but the fourth is padding rather than alpha: it reads as opaque
                    // and a store must leave that byte alone.
                    redOffset = 0; bytesPerPixel = 4; opaque = true;
                    break;
                case SKColorType.Gray8:
                    // One byte, and no way to put three channels back into it.
                    redOffset = 0; bytesPerPixel = 1; grey = true; opaque = true; canStore = false;
                    break;
                default:
                    return false;
            }

            if (bitmap.RowBytes < (long)bitmap.Width * bytesPerPixel) return false;

            layout = new StraightPixelLayout(
                bitmap, redOffset, bytesPerPixel,
                bitmap.AlphaType == SKAlphaType.Premul && !opaque, grey, opaque, canStore);
            return true;
        }

        /// <summary>
        /// One row of the buffer, sized to the pixels rather than to the stride.
        /// </summary>
        /// <remarks>
        /// A slice of known length keeps every access inside a span the JIT can reason about, where
        /// indexing <c>buffer[y * stride + x * bpp + k]</c> into the whole surface cannot be proved
        /// in range and pays for a bounds check per access.
        /// </remarks>
        internal Span<byte> Row(Span<byte> buffer, int y)
            => buffer.Slice(y * Stride, Width * BytesPerPixel);

        /// <summary>The straight colour of pixel <paramref name="x"/> of a row.</summary>
        /// <remarks>
        /// <b>A pixel index, not a byte offset.</b> The four handled formats are not all the same
        /// width, so a caller walking two surfaces together would otherwise have to carry a
        /// separate offset per surface — and pairing them by one offset is how it would silently
        /// read the wrong pixel of the narrower one.
        /// </remarks>
        internal SKColor Get(ReadOnlySpan<byte> row, int x)
        {
            int offset = x * BytesPerPixel;

            if (_grey)
            {
                byte level = row[offset];
                return new SKColor(level, level, level, 255);
            }

            byte c0 = row[offset], g = row[offset + 1], c2 = row[offset + 2];
            byte a = _opaque ? (byte)255 : row[offset + 3];
            byte r = _redOffset == 0 ? c0 : c2;
            byte b = _redOffset == 0 ? c2 : c0;

            if (!_premultiplied || a == 255) return new SKColor(r, g, b, a);

            uint scale = UnpremultiplyScale[a];
            return new SKColor(
                ApplyScale(scale, r), ApplyScale(scale, g), ApplyScale(scale, b), a);
        }

        /// <summary>
        /// Stores <paramref name="straight"/> as pixel <paramref name="x"/> of a row. A no-op when
        /// <see cref="CanStore"/> is false.
        /// </summary>
        /// <remarks>
        /// <b>A fully transparent pixel is stored as transparent black</b>, discarding its colour
        /// channels. That is the setter's behaviour and not a simplification of it: an invisible
        /// pixel renders the same either way, but keeping the decoded colour turns a uniform
        /// transparent region into noise for a PNG encoder — see <see cref="ImageSampleLayout"/>,
        /// which had to reproduce the same rule.
        /// <para>On an opaque four-byte surface the alpha is dropped rather than stored, which is
        /// what that surface means, and the padding byte is left as the allocator set it.</para>
        /// </remarks>
        internal void Set(Span<byte> row, int x, SKColor straight)
        {
            if (!CanStore) return;

            int offset = x * BytesPerPixel;
            byte a = straight.Alpha;
            byte r = straight.Red, g = straight.Green, b = straight.Blue;

            if (_premultiplied)
            {
                if (a == 0) r = g = b = 0;
                else if (a != 255)
                {
                    r = Premultiply(r, a); g = Premultiply(g, a); b = Premultiply(b, a);
                }
            }

            row[offset] = _redOffset == 0 ? r : b;
            row[offset + 1] = g;
            row[offset + 2] = _redOffset == 0 ? b : r;
            // An opaque surface stays opaque in its BYTES too, not merely in what Get
            // reports: the fourth byte is padding, and recording a non-opaque alpha there
            // would leave the buffer disagreeing with the alpha type it declares.
            row[offset + 3] = _opaque ? (byte)255 : a;
        }

        /// <summary>Skia's <c>SkUnPreMultiply::ApplyScale</c>.</summary>
        private static byte ApplyScale(uint scale, byte component)
            => (byte)((scale * component + (1u << 23)) >> 24);

        /// <summary>
        /// Skia's <c>SkMulDiv255Round</c> — round(value × alpha / 255) without a divide.
        /// </summary>
        internal static byte Premultiply(byte value, byte alpha)
        {
            int product = (value * alpha) + 128;
            return (byte)((product + (product >> 8)) >> 8);
        }

        private static uint[] BuildUnpremultiplyScale()
        {
            var table = new uint[256];
            for (int a = 1; a < 256; a++) table[a] = (255u << 24) / (uint)a;
            return table;
        }
    }
}
