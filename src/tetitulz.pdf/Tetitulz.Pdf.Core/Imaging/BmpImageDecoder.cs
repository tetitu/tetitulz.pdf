// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>
    /// Decodes an uncompressed Windows BMP to 8-bit RGB: 1/4/8-bit palette, 16-bit
    /// <c>BI_BITFIELDS</c>, 24-bit and 32-bit, bottom-up or top-down.
    ///
    /// <para>RLE4/RLE8 are declined rather than half-implemented — a partially decoded run-length
    /// image is worse than a reported one, because it looks like the file is corrupt.</para>
    /// </summary>
    public static class BmpImageDecoder
    {
        private const long MaxPixels = 40_000_000L;

        private const int BiRgb = 0;
        private const int BiBitfields = 3;

        /// <summary>
        /// Decodes to <c>width*height*3</c> RGB bytes in top-down row order (the direction a PDF
        /// image XObject expects), or returns false with <paramref name="reason"/> set.
        /// </summary>
        public static bool TryDecode(byte[] b, out byte[] rgb, out int width, out int height, out string reason)
        {
            rgb = null; width = 0; height = 0;
            if (!TryReadLayout(b, out Layout layout, out reason)) return false;
            int w = layout.Width, h = layout.Height, bpp = layout.BitsPerPixel, compression = layout.Compression;
            int rowBytes = layout.RowBytes, pixelOffset = layout.PixelOffset;
            bool topDown = layout.TopDown;
            byte[] palette = layout.Palette;
            uint rMask = layout.RedMask, gMask = layout.GreenMask, bMask = layout.BlueMask;

            rgb = new byte[w * h * 3];

            for (int y = 0; y < h; y++)
            {
                int srcRow = topDown ? y : h - 1 - y;
                int rowStart = pixelOffset + srcRow * rowBytes;
                int dst = y * w * 3;

                for (int x = 0; x < w; x++)
                {
                    byte r, g, bl;
                    switch (bpp)
                    {
                        case 1:
                        case 2:
                        case 4:
                        case 8:
                        {
                            int bitIndex = x * bpp;
                            int byteIndex = rowStart + (bitIndex >> 3);
                            int shift = 8 - bpp - (bitIndex & 7);
                            int idx = (b[byteIndex] >> shift) & ((1 << bpp) - 1);
                            int p = idx * 4;
                            if (palette == null || p + 2 >= palette.Length) { r = g = bl = 0; }
                            else { bl = palette[p]; g = palette[p + 1]; r = palette[p + 2]; }  // BGRA order
                            break;
                        }
                        case 16:
                        {
                            int o = rowStart + x * 2;
                            uint v = (uint)(b[o] | (b[o + 1] << 8));
                            r = ScaleMask(v, rMask);
                            g = ScaleMask(v, gMask);
                            bl = ScaleMask(v, bMask);
                            break;
                        }
                        case 24:
                        {
                            int o = rowStart + x * 3;
                            bl = b[o]; g = b[o + 1]; r = b[o + 2];
                            break;
                        }
                        case 32:
                        {
                            int o = rowStart + x * 4;
                            if (compression == BiBitfields)
                            {
                                // Assembled entirely in uint. Only `b[o + 3] << 24` can reach bit 31,
                                // so mixing it with the int sub-expression promoted both to long and
                                // sign-extended the int half (CS0675). That half is three bytes shifted
                                // at most 16, hence provably in 0..0x00FFFFFF and never negative, so the
                                // value was already correct — the casts state that rather than rely on it.
                                uint v = (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
                                r = ScaleMask(v, rMask);
                                g = ScaleMask(v, gMask);
                                bl = ScaleMask(v, bMask);
                            }
                            else { bl = b[o]; g = b[o + 1]; r = b[o + 2]; }   // the 4th byte is unused in BI_RGB
                            break;
                        }
                        default:
                            reason = $"{bpp} bits per pixel is not supported";
                            rgb = null;
                            return false;
                    }

                    rgb[dst++] = r; rgb[dst++] = g; rgb[dst++] = bl;
                }
            }

            width = w;
            height = h;
            return true;
        }

        /// <summary>
        /// Whether <see cref="TryDecode"/> would decode <paramref name="b"/>, answered from the headers alone: every
        /// refusal the decode makes is made here, before a pixel is read, so a caller that only asks (a converter
        /// choosing whether to keep a picture) pays for no decode. The layout is what the decode reads its pixels by.
        /// </summary>
        internal static bool TryReadLayout(byte[] b, out Layout layout, out string reason)
        {
            layout = default;
            reason = null;

            if (b == null || b.Length < 54) { reason = "the file is too short to be a BMP"; return false; }
            if (b[0] != 'B' || b[1] != 'M') { reason = "missing the BM signature"; return false; }

            int pixelOffset = ReadLE32(b, 10);
            int dibSize = ReadLE32(b, 14);
            if (dibSize < 40) { reason = $"unsupported DIB header size {dibSize} (BITMAPCOREHEADER is not handled)"; return false; }

            int w = ReadLE32(b, 18);
            int rawH = ReadLE32(b, 22);
            int planes = ReadLE16(b, 26);
            int bpp = ReadLE16(b, 28);
            int compression = ReadLE32(b, 30);
            int paletteCount = ReadLE32(b, 46);

            if (w <= 0 || rawH == 0) { reason = "zero or negative dimensions"; return false; }
            // int.MinValue has no positive counterpart: Math.Abs threw OverflowException out of a
            // method whose contract is false-with-a-reason. A height that large is no image anyway.
            if (rawH == int.MinValue) { reason = "the height field is int.MinValue, which names no row count"; return false; }
            if (planes != 1) { reason = $"{planes} colour planes (only 1 is defined)"; return false; }
            // The depth is judged before any arithmetic uses it: `w * bpp` in the row stride below is
            // safe for every real depth under the pixel ceiling (40 M × 32 fits an int), and a depth the
            // file invents — the field is sixteen bits — would wrap it, pass the truncation check on a
            // negative stride, and allocate the whole image before the per-pixel switch refused it.
            if (bpp != 1 && bpp != 2 && bpp != 4 && bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32)
            {
                reason = $"{bpp} bits per pixel is not supported";
                return false;
            }
            if (compression != BiRgb && compression != BiBitfields)
            {
                reason = "RLE-compressed BMP is not supported";
                return false;
            }

            // A negative height means the rows are stored top-down rather than bottom-up.
            bool topDown = rawH < 0;
            int h = Math.Abs(rawH);
            if ((long)w * h > MaxPixels) { reason = $"{w}×{h} exceeds the {MaxPixels:N0}-pixel ceiling"; return false; }

            byte[] palette = null;
            if (bpp <= 8)
            {
                // 64-bit arithmetic and a format ceiling.
                //
                // `biClrUsed` is read straight out of the file as a signed int. Taken verbatim,
                // `entries * 4` in 32 bits — in the truncated-table check AND in the allocation —
                // would let a crafted value near 2^29 wrap the product negative, sail past
                // `> b.Length`, and reach `new byte[negative]`, as the widened dimension checks a
                // few lines up (`(long)w * h`, `(long)pixelOffset + (long)rowBytes * h`) prevent.
                // A ceiling of 1<<bpp is the real bound: a 1/4/8-bpp image cannot index more than
                // 2/16/256 entries, so anything larger is malformed rather than merely large.
                int maxEntries = 1 << bpp;
                int entries    = paletteCount > 0 ? paletteCount : maxEntries;
                if (entries > maxEntries)
                {
                    reason = $"the colour table declares {entries} entries, more than the " +
                             $"{maxEntries} a {bpp}-bpp BMP can index";
                    return false;
                }

                long paletteOffset = 14L + dibSize;
                long paletteBytes  = (long)entries * 4L;
                if (paletteOffset + paletteBytes > b.Length)
                {
                    reason = "the colour table is truncated";
                    return false;
                }

                palette = new byte[paletteBytes];
                Array.Copy(b, paletteOffset, palette, 0, palette.Length);
            }

            // 16- and 32-bit BI_BITFIELDS carry channel masks after the header.
            uint rMask = 0, gMask = 0, bMask = 0;
            if (compression == BiBitfields)
            {
                int maskOffset = 14 + 40;
                if (maskOffset + 12 > b.Length) { reason = "the bit-field masks are truncated"; return false; }
                rMask = (uint)ReadLE32(b, maskOffset);
                gMask = (uint)ReadLE32(b, maskOffset + 4);
                bMask = (uint)ReadLE32(b, maskOffset + 8);
            }
            else if (bpp == 16) { rMask = 0x7C00; gMask = 0x03E0; bMask = 0x001F; }   // default 5-5-5

            int rowBytes = ((w * bpp + 31) / 32) * 4;                                 // rows pad to 4 bytes
            if (pixelOffset < 0 || (long)pixelOffset + (long)rowBytes * h > b.Length)
            {
                reason = "the pixel data is truncated";
                return false;
            }

            layout = new Layout(w, h, topDown, bpp, compression, palette, rMask, gMask, bMask, rowBytes, pixelOffset);
            return true;
        }

        /// <summary>What a BMP's headers say its pixels are: size, row order, depth, coding, palette, masks, stride and offset.</summary>
        internal readonly struct Layout
        {
            internal Layout(int width, int height, bool topDown, int bitsPerPixel, int compression, byte[] palette,
                uint redMask, uint greenMask, uint blueMask, int rowBytes, int pixelOffset)
            {
                Width = width; Height = height; TopDown = topDown; BitsPerPixel = bitsPerPixel; Compression = compression;
                Palette = palette; RedMask = redMask; GreenMask = greenMask; BlueMask = blueMask; RowBytes = rowBytes; PixelOffset = pixelOffset;
            }

            internal int Width { get; }
            internal int Height { get; }
            internal bool TopDown { get; }
            internal int BitsPerPixel { get; }
            internal int Compression { get; }
            internal byte[] Palette { get; }
            internal uint RedMask { get; }
            internal uint GreenMask { get; }
            internal uint BlueMask { get; }
            internal int RowBytes { get; }
            internal int PixelOffset { get; }
        }

        /// <summary>
        /// Extracts a channel through its mask and scales it to a full 8-bit range — in 64 bits, since
        /// a mask may be all 32 (in 32 bits C# reads <c>1 &lt;&lt; 32</c> as <c>1 &lt;&lt; 0</c>, so the
        /// maximum would be 0 and the division would throw), and clamped, since the scale is read off
        /// the mask's lowest run of bits and a non-contiguous mask holds more.
        /// </summary>
        private static byte ScaleMask(uint value, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            uint m = mask;
            while ((m & 1) == 0) { m >>= 1; shift++; }
            int bits = 0;
            while ((m & 1) == 1) { m >>= 1; bits++; }
            ulong raw = (value & mask) >> shift;
            ulong max = (1UL << bits) - 1;
            ulong scaled = (raw * 255 + max / 2) / max;
            return scaled > 255 ? (byte)255 : (byte)scaled;
        }

        private static int ReadLE32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        private static int ReadLE16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
    }
}
