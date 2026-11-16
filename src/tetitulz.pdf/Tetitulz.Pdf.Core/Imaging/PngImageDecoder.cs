// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>
    /// A decoded PNG: 8-bit RGB samples, plus the alpha plane when the image has one.
    /// </summary>
    public sealed class DecodedPng
    {
        internal DecodedPng(int width, int height, byte[] rgb, byte[] alpha)
        {
            Width = width;
            Height = height;
            Rgb = rgb;
            Alpha = alpha;
        }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>Packed 8-bit RGB, <c>Width * Height * 3</c> bytes.</summary>
        public byte[] Rgb { get; }

        /// <summary>
        /// 8-bit alpha, one byte per pixel - the samples a PDF <c>/SMask</c> wants - or
        /// <see langword="null"/> when the image cannot be transparent at all.
        /// </summary>
        /// <remarks>
        /// Null rather than an all-255 plane on purpose: only colour types that <i>can</i> be
        /// transparent allocate one, so an opaque photograph pays nothing, and a caller can tell
        /// "no transparency in this format" from "transparent nowhere in this image".
        /// </remarks>
        public byte[] Alpha { get; }
    }

    /// <summary>
    /// Pure-managed PNG decoder - chunks, zlib, unfiltering, Adam7 and every colour type, to 8-bit
    /// RGB plus an alpha plane.
    /// </summary>
    /// <remarks>
    /// <para><b>Two copies of this existed and each had what the other lacked</b>, which is how the
    /// tree ended up with a decoder that honoured a colour-key <c>tRNS</c> and one that did not, and
    /// a decoder that supported Adam7 and one that did not. Both said so in their own comments -
    /// <c>Builder.Export.BuilderImageDecoder</c> named "PngImage, the other decoder in this tree"
    /// twice - and by CLAUDE.md's rule a comment asserting two implementations agree is a
    /// duplication report. This is the union of the two, hardening included.</para>
    ///
    /// <para><b>Why <c>Core.Imaging</c>.</b> The same reason <see cref="RasterSample"/> gives:
    /// Builder and DocxToPdf are features that may not reach Base, and Builder may not reference it
    /// at all - while <c>Tetitulz.Pdf.Signing</c> may reach neither and needs a PNG logo for a
    /// visible signature. Core is below all three, so this is the sanctioned share-downward rather
    /// than a sideways edge.</para>
    ///
    /// <para><b>Alpha is preserved, never composited.</b> The DocxToPdf copy flattened it over
    /// white - correct for its own pages and wrong as a decoder's contract, because it throws away
    /// information the caller cannot recover. Compositing is now the caller's step, and only
    /// DocxToPdf takes it.</para>
    /// </remarks>
    public static class PngImageDecoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the most recent <see cref="TryDecode"/> <b>on this thread</b> refused, or null when
        /// it succeeded. Null on a thread that has not decoded anything.
        /// </summary>
        /// <remarks>
        /// <para><b>Every refusal here is "returns false", so without this the guards are
        /// indistinguishable from one another — and a test cannot tell which one fired.</b> That is
        /// not hypothetical: three of this decoder's guards survived mutation because the fixture
        /// was refused by a *different* guard than the one under test, which is the failure CLAUDE.md
        /// records for the CRL scheme whitelist. A refusal test must refuse for the reason in its
        /// name, and only a reason string makes that checkable.</para>
        ///
        /// <para><c>[ThreadStatic]</c> and cleared at the start of every call on every path, so
        /// reading it straight after one describes that call.</para>
        /// </remarks>
        public static string LastError => _lastError;

        /// <summary>
        /// Decodes <paramref name="png"/>. Returns <see langword="false"/> - never throws - when the
        /// bytes are not a PNG this decoder can read.
        /// </summary>
        /// <param name="png">The PNG file bytes.</param>
        /// <param name="result">The decoded image, or null on failure.</param>
        public static bool TryDecode(byte[] png, out DecodedPng result)
        {
            result = null;
            _lastError = null;

            if (png == null || png.Length < 8) return Fail("the data is too short to be a PNG");
            for (int i = 0; i < Signature.Length; i++)
                if (png[i] != Signature[i]) return Fail("the data does not begin with the PNG signature");

            try { result = DecodePng(png); }
            catch (Exception ex) { return Fail("the PNG could not be decoded: " + ex.Message); }

            if (result == null && _lastError == null) _lastError = "the PNG could not be decoded";
            return result != null;
        }

        /// <summary>Records why a decode refused and returns false, so a guard is one statement.</summary>
        private static bool Fail(string reason)
        {
            _lastError = reason;
            return false;
        }

        private static DecodedPng DecodePng(byte[] b)
        {
            int pos = 8;
            int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
            byte[] palette = null, transAlpha = null;

            // Why the chunk walk stopped early, if it did. It has to outlive the loop: aborting
            // leaves width/height at 0, so the *next* guard fires and — reporting only the
            // proximate cause — would tell the caller its IHDR declares a zero width, when what
            // actually happened is that a hostile chunk length ended the walk before the IHDR was
            // ever read. The root cause wins where there is one.
            //
            // `sawIhdr` is what makes that conditional rather than unconditional, and it is needed
            // in BOTH directions. Consulted whenever the dimensions are non-positive, the abort
            // message also displaced the true reason for a file whose IHDR was read and genuinely
            // declares a zero width — the caller was then told the problem was some later chunk.
            // A header that was read owns its own dimensions; only an unread one defers.
            string walkAbort = null;
            bool sawIhdr = false;
            using var idat = new MemoryStream();

            while (pos + 8 <= b.Length)
            {
                int len = (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(b, pos + 4, 4);
                int dataStart = pos + 8;
                // `dataStart + len` in int can overflow for a hostile chunk length (len near
                // int.MaxValue) and wrap negative, passing this guard and driving an out-of-range
                // read/allocation below. Compute in long.
                if (len < 0 || (long)dataStart + len > b.Length)
                {
                    walkAbort = "a chunk declares a length that runs past the end of the data";
                    break;
                }

                switch (type)
                {
                    case "IHDR":
                        // IHDR is exactly 13 bytes (PNG spec §11.2.2). The loop above validates
                        // only that the DECLARED length fits the buffer, so a truncated or
                        // zero-length IHDR passed it and the reads below indexed past the end of
                        // the caller's array — an IndexOutOfRangeException escaping a method whose
                        // contract is to return null. A 16-byte "PNG" (signature + an empty IHDR
                        // header) is enough.
                        if (len < 13)
                        {
                            _lastError = "the IHDR chunk is shorter than the 13 bytes §11.2.2 requires";
                            return null;
                        }
                        width = ReadInt(b, dataStart);
                        height = ReadInt(b, dataStart + 4);
                        bitDepth = b[dataStart + 8];
                        colorType = b[dataStart + 9];
                        interlace = b[dataStart + 12];
                        sawIhdr = true;
                        break;
                    case "PLTE":
                        palette = new byte[len];
                        Array.Copy(b, dataStart, palette, 0, len);
                        break;
                    case "tRNS":
                        transAlpha = new byte[len];
                        Array.Copy(b, dataStart, transAlpha, 0, len);
                        break;
                    case "IDAT":
                        idat.Write(b, dataStart, len);
                        break;
                }
                if (type == "IEND") break;
                pos = dataStart + len + 4; // skip CRC
            }

            if (!sawIhdr)
            {
                // The header was never read, so nothing here knows the image's dimensions. Blame
                // whatever ended the walk, and say plainly that there was no header when the walk
                // ran to completion without finding one.
                _lastError = walkAbort ?? "the file contains no IHDR chunk";
                return null;
            }
            if (width <= 0 || height <= 0)
            {
                // The header WAS read and it is the thing declaring these dimensions, so a later
                // chunk's abort is not the reason and must not be reported as one.
                _lastError = "the IHDR declares a non-positive width or height";
                return null;
            }
            if (interlace != 0 && interlace != 1)
            {
                _lastError = "the IHDR declares an interlace method other than none or Adam7";
                return null;
            }
            if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8 && bitDepth != 16)
            {
                _lastError = "the IHDR declares a bit depth PNG does not define: " + bitDepth;
                return null;
            }
            // Dimension cap (mirror the legacy PngImage 40 MP guard): an attacker IHDR declaring
            // e.g. 30000×30000 would otherwise drive `new byte[width*height*3]` to overflow int /
            // allocate gigabytes. With the cap, width*height ≤ 40M so width*height*3 ≤ 120M fits int.
            if ((long)width * height > 40_000_000L)
            {
                _lastError = "the IHDR declares " + width + "x" + height +
                             " pixels, past the 40 megapixel ceiling";
                return null;
            }

            int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0)
            {
                _lastError = "the IHDR declares a colour type PNG does not define: " + colorType;
                return null;
            }

            // §11.2.2 Table 11.1 — the bit depth and the colour type are NOT independent, and
            // validating each alone (as the two guards above do) accepts pairs the format does not
            // define. That is not a conformance nicety: the row walk for colour types 2, 4 and 6
            // steps `bitDepth / 8` bytes per sample, which is ZERO below 8 bpc, so every pixel in
            // a row is decoded from byte 0 and the image comes out a confident flat colour with
            // TryDecode true and LastError null. Colour type 3 at 16 bpc reads indices up to 65535
            // into a palette that can hold 256. Refuse the pair, by name.
            bool depthAllowed = colorType switch
            {
                0 => true,                                // greyscale: 1, 2, 4, 8, 16
                3 => bitDepth <= 8,                       // palette:   1, 2, 4, 8
                _ => bitDepth == 8 || bitDepth == 16,     // 2, 4, 6:   8, 16
            };
            if (!depthAllowed)
            {
                _lastError = "the IHDR pairs colour type " + colorType + " with bit depth " +
                             bitDepth + ", a combination PNG 11.2.2 does not allow";
                return null;
            }

            // Colour type 3 stores an INDEX, so the palette it indexes has to exist. The expansion
            // below tolerated a missing one by substituting 0 for every channel, which decodes such
            // a file "successfully" to a solid black image — the confidently-wrong answer this
            // codebase refuses everywhere else. The legacy DocxToPdf decoder refused it and the
            // refusal was lost when the two were merged.
            if (colorType == 3 && (palette == null || palette.Length < 3))
            {
                _lastError = "the image declares colour type 3 but carries no usable PLTE chunk";
                return null;
            }

            // tRNS on a NON-palette image is a colour key (PNG spec 11.3.2.1): one sample value
            // per channel, each stored in two bytes whatever the bit depth, and every pixel that
            // matches it exactly is fully transparent. Only the palette form was read, so a keyed
            // grey or RGB pixel came out OPAQUE -- a logo saved with a transparent background by
            // any tool that writes colour type 0 or 2 drew its background as a solid rectangle.
            // PngImage, the other decoder in this tree, has honoured both all along.
            int[] colorKey = null;
            if (colorType == 0 && transAlpha != null && transAlpha.Length >= 2)
                colorKey = new[] { (transAlpha[0] << 8) | transAlpha[1] };
            else if (colorType == 2 && transAlpha != null && transAlpha.Length >= 6)
                colorKey = new[]
                {
                    (transAlpha[0] << 8) | transAlpha[1],
                    (transAlpha[2] << 8) | transAlpha[3],
                    (transAlpha[4] << 8) | transAlpha[5],
                };
            // Bound the inflate to the decoded-PNG size (a tiny zlib stream can otherwise inflate
            // to gigabytes — a decompression bomb). All size math in long.
            int bitsPerPixel = channels * bitDepth;
            long strideLong = ((long)width * bitsPerPixel + 7) / 8;
            // The cap has to be computed for the interlacing the file actually declares. An Adam7
            // stream is SEVEN sub-images, each with its own stride and its own filter byte per row,
            // so it carries ~1.875 filter bytes per image row plus per-pass byte rounding — always
            // MORE than the sequential (stride+1)*height figure. Capping every PNG at the
            // sequential size made ReadCapped throw for any interlaced image taller than ~18 rows,
            // the `catch { return null; }` below swallowed it, and DecodeAdam7 was unreachable code
            // behind an 8x8 fixture small enough to squeak under.
            long expectedRaw = interlace == 1
                ? Adam7RawSize(width, height, bitsPerPixel)
                : (strideLong + 1) * height;
            if (expectedRaw <= 0 || expectedRaw > 256L * 1024 * 1024)
            {
                _lastError = "the image would inflate to " + expectedRaw +
                             " bytes, past the 256 MiB ceiling";
                return null;
            }

            byte[] raw;
            try
            {
                // Core's Flate decoder rather than ZLibStream, for two reasons. ZLibStream is .NET 6+
                // and this assembly ships a netstandard2.0 leg; and CLAUDE.md puts stream filter
                // decoding exclusively in Core.IO.Filters, which this was quietly a second copy of —
                // Core's Decompress does the same zlib-header detection ((b[0] & 0x0F) == 0x08).
                //
                // The ceiling is carried over exactly. It is now ENFORCED rather than truncating:
                // Core throws past it where ReadCapped returned a prefix, and a prefix accepted as
                // the whole stream is the failure this codebase refuses everywhere else. A valid PNG
                // inflates to exactly expectedRaw, so only a malformed one can tell the difference.
                raw = new FlateDecodeFilter { MaxOutputBytes = expectedRaw + 16 }
                    .Decode(idat.ToArray(), null);
            }
            catch (Exception ex)
            {
                _lastError = "the IDAT stream would not inflate: " + ex.Message;
                return null;
            }

            int stride = (int)strideLong;
            int bpp = Math.Max(1, (bitsPerPixel + 7) / 8);
            if (raw.Length < expectedRaw)
            {
                _lastError = "the IDAT stream inflated to " + raw.Length + " bytes, short of the " +
                             expectedRaw + " the header describes";
                return null;
            }

            // Unfilter scanlines in place into a contiguous sample buffer.
            // Only formats that can BE transparent get a mask buffer; the rest stay opaque and
            // pay nothing.
            bool mayHaveAlpha = colorType == 4 || colorType == 6
                || (colorType == 3 && transAlpha != null)
                || colorKey != null;
            byte[] alpha = mayHaveAlpha ? new byte[width * height] : null;

            byte[] cur = new byte[stride];
            byte[] prev = new byte[stride];
            byte[] rgb = new byte[width * height * 3];

            if (interlace == 0)
            {
                int outPos = 0;
                int rp = 0;
                for (int y = 0; y < height; y++)
                {
                    int filter = raw[rp++];
                    Buffer.BlockCopy(raw, rp, cur, 0, stride);
                    rp += stride;
                    if (!Unfilter(filter, cur, prev, bpp, stride)) return null;
                    ExpandRowToRgb(cur, width, bitDepth, colorType, channels, palette, transAlpha,
                        rgb, ref outPos, colorKey, alpha, y * width);
                    var tmp = prev; prev = cur; cur = tmp;
                }
            }
            else if (!DecodeAdam7(raw, width, height, bitDepth, colorType, channels, bpp,
                                  palette, transAlpha, colorKey, rgb, alpha))
            {
                return null;
            }

            return new DecodedPng(width, height, rgb, alpha);
        }


        private static int ReadInt(byte[] b, int o)
            => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        // Reads a stream into a byte[] but throws once more than `cap` bytes are produced, so a
        // decompression bomb cannot exhaust memory. Caller treats the throw as "skip this image".

        /// <summary>
        /// Undoes one scanline's filter in place (PNG §9) through the one row unfilter
        /// (<see cref="IO.Filters.PredictorHelper.UndoPngRow"/>, which reads each byte before it
        /// writes it, so input and output may be one buffer) — the five filters are the same five
        /// a PDF <c>/Predictor</c> 10–15 stream carries. Only the verdict on a type above 4 is PNG's
        /// own: the format makes it an error, where a PDF predictor row passes through.
        /// </summary>
        private static bool Unfilter(int filter, byte[] cur, byte[] prev, int bpp, int stride)
        {
            if (filter < 0 || filter > 4) return false;
            if (filter != 0)
                IO.Filters.PredictorHelper.UndoPngRow((byte)filter, cur, 0, cur, 0, stride, bpp, prev, 0);
            return true;
        }

        private static void ExpandRowToRgb(byte[] row, int width, int bitDepth, int colorType,
            int channels, byte[] palette, byte[] transAlpha, byte[] rgb, ref int outPos,
            int[] colorKey = null, byte[] alpha = null, int alphaPos = 0)
        {
            int max = (1 << bitDepth) - 1;
            for (int x = 0; x < width; x++)
            {
                int r, g, b, a = 255;
                if (colorType == 3) // palette (index)
                {
                    int idx = Sample(row, x, bitDepth);
                    int pi = idx * 3;
                    r = palette != null && pi + 2 < palette.Length ? palette[pi] : 0;
                    g = palette != null && pi + 2 < palette.Length ? palette[pi + 1] : 0;
                    b = palette != null && pi + 2 < palette.Length ? palette[pi + 2] : 0;
                    if (transAlpha != null && idx < transAlpha.Length) a = transAlpha[idx];
                }
                else if (colorType == 0) // grayscale
                {
                    int gray = Sample(row, x, bitDepth);
                    r = g = b = RasterSample.ScaleToByteFromMax(gray, max);
                    // The key is compared against the RAW sample, never the scaled byte: at
                    // 16 bpc many samples share a scaled value, so keying on the byte would
                    // punch a hole in every neighbouring shade as well.
                    if (colorKey != null && gray == colorKey[0]) a = 0;
                }
                // Colour types 4, 6 and 2 through SampleScaled, as greyscale is: 8 bits come back
                // unchanged, and 16 bits round to the nearest byte, never the HIGH byte alone, which
                // truncates — a half-level bias (see SampleScaled).
                else if (colorType == 4) // gray + alpha (8/16-bit)
                {
                    int gray = SampleScaled(row, x * 2, bitDepth, max);
                    a = SampleScaled(row, x * 2 + 1, bitDepth, max);
                    r = g = b = gray;
                }
                else if (colorType == 6) // RGBA (8/16-bit)
                {
                    r = SampleScaled(row, x * 4, bitDepth, max);
                    g = SampleScaled(row, x * 4 + 1, bitDepth, max);
                    b = SampleScaled(row, x * 4 + 2, bitDepth, max);
                    a = SampleScaled(row, x * 4 + 3, bitDepth, max);
                }
                else // colorType 2: RGB (8/16-bit)
                {
                    r = SampleScaled(row, x * 3, bitDepth, max);
                    g = SampleScaled(row, x * 3 + 1, bitDepth, max);
                    b = SampleScaled(row, x * 3 + 2, bitDepth, max);
                    if (colorKey != null
                        && Sample(row, x * 3, bitDepth) == colorKey[0]
                        && Sample(row, x * 3 + 1, bitDepth) == colorKey[1]
                        && Sample(row, x * 3 + 2, bitDepth) == colorKey[2]) a = 0;
                }

                // The alpha channel is CARRIED OUT, not composited away. Compositing over white
                // is only correct when the background happens to be white; over a shaded
                // paragraph or a coloured table cell it leaves a white halo around every
                // transparent edge. The colour samples stay untouched so the /SMask can do the
                // blending the viewer is meant to do.
                if (alpha != null) alpha[alphaPos + x] = (byte)a;
                else if (a != 255)
                {
                    r = (r * a + 255 * (255 - a)) / 255;
                    g = (g * a + 255 * (255 - a)) / 255;
                    b = (b * a + 255 * (255 - a)) / 255;
                }
                rgb[outPos++] = (byte)r; rgb[outPos++] = (byte)g; rgb[outPos++] = (byte)b;
            }
        }


        /// <summary>
        /// Packs decoded RGB (and any alpha) into an embeddable image. The alpha becomes a real
        /// <c>/SMask</c>; a fully opaque one is dropped by the writer.
        /// </summary>

        private static readonly int[] Adam7XOrigin = { 0, 4, 0, 2, 0, 1, 0 };
        private static readonly int[] Adam7YOrigin = { 0, 0, 4, 0, 2, 0, 1 };
        private static readonly int[] Adam7XStep = { 8, 8, 4, 4, 2, 2, 1 };
        private static readonly int[] Adam7YStep = { 8, 8, 8, 4, 4, 2, 2 };

        /// <summary>
        /// Exact size of an Adam7 raw (filtered) stream: for each of the seven passes, one filter
        /// byte plus that pass's stride, per pass row.
        /// </summary>
        /// <remarks>
        /// Deliberately beside the pass tables and using the same four arrays as
        /// <see cref="DecodeAdam7"/>, because a cap derived from different pass geometry than the
        /// decoder walks is a cap that rejects exactly the images the decoder could have read.
        /// </remarks>
        private static long Adam7RawSize(int width, int height, int bitsPerPixel)
        {
            long total = 0;
            for (int pass = 0; pass < 7; pass++)
            {
                int passWidth = (width - Adam7XOrigin[pass] + Adam7XStep[pass] - 1) / Adam7XStep[pass];
                int passHeight = (height - Adam7YOrigin[pass] + Adam7YStep[pass] - 1) / Adam7YStep[pass];
                if (passWidth <= 0 || passHeight <= 0) continue;
                long passStride = ((long)passWidth * bitsPerPixel + 7) / 8;
                total += passHeight * (passStride + 1);
            }
            return total;
        }

        private static bool DecodeAdam7(byte[] raw, int width, int height, int bitDepth,
            int colorType, int channels, int bpp, byte[] palette, byte[] transAlpha,
            int[] colorKey, byte[] rgb, byte[] alpha)
        {
            int bitsPerPixel = channels * bitDepth;
            int rp = 0;

            for (int pass = 0; pass < 7; pass++)
            {
                int passWidth = (width - Adam7XOrigin[pass] + Adam7XStep[pass] - 1) / Adam7XStep[pass];
                int passHeight = (height - Adam7YOrigin[pass] + Adam7YStep[pass] - 1) / Adam7YStep[pass];
                if (passWidth <= 0 || passHeight <= 0) continue;

                int passStride = (passWidth * bitsPerPixel + 7) / 8;
                var cur = new byte[passStride];
                var prev = new byte[passStride];
                // One pass's pixels, expanded to RGB, before they are scattered into the image.
                var passRgb = new byte[passWidth * 3];
                var passAlpha = alpha != null ? new byte[passWidth] : null;

                for (int py = 0; py < passHeight; py++)
                {
                    if (rp >= raw.Length) return false;
                    int filter = raw[rp++];
                    if (rp + passStride > raw.Length) return false;
                    Buffer.BlockCopy(raw, rp, cur, 0, passStride);
                    rp += passStride;
                    if (!Unfilter(filter, cur, prev, bpp, passStride)) return false;

                    int outPos = 0;
                    ExpandRowToRgb(cur, passWidth, bitDepth, colorType, channels, palette,
                        transAlpha, passRgb, ref outPos, colorKey, passAlpha, 0);

                    int y = Adam7YOrigin[pass] + py * Adam7YStep[pass];
                    for (int px = 0; px < passWidth; px++)
                    {
                        int x = Adam7XOrigin[pass] + px * Adam7XStep[pass];
                        if (x >= width || y >= height) continue;
                        int dst = (y * width + x) * 3;
                        rgb[dst] = passRgb[px * 3];
                        rgb[dst + 1] = passRgb[px * 3 + 1];
                        rgb[dst + 2] = passRgb[px * 3 + 2];
                        // passAlpha is allocated exactly when alpha is, so the guard covers both -
                        // a correlation the compiler cannot see through two separate locals.
                        if (alpha != null && passAlpha != null) alpha[y * width + x] = passAlpha[px];
                    }

                    var tmp = prev; prev = cur; cur = tmp;
                }
            }
            return true;
        }

        // Reads the raw sample for pixel x of a single-channel row, MSB-first.
        //
        // Through Core.Imaging.RasterSample, and the 16-bit arm is why. This returned row[x * 2] —
        // the HIGH BYTE, 0..255 — while SampleScaled below divided by max = 65535, so
        // `s * 255 / 65535` was 0 for every s <= 257 and EVERY 16-bit greyscale PNG exported pure
        // black. The read and the scale disagreed about what "sample" meant; DocxToPdf's copy of
        // the same pair agreed, and Base's PdfColor.Scale16To8 had the correct expression all along.
        private static int Sample(byte[] row, int x, int bitDepth)
            => RasterSample.ReadChannelMsbFirst(row, x, bitDepth);

        // Rounds to nearest, as PdfColor and DocxToPdf both do; this truncated, biasing every
        // channel down by half a level even where it was not collapsing to black.
        private static int SampleScaled(byte[] row, int x, int bitDepth, int max)
            => RasterSample.ScaleToByteFromMax(Sample(row, x, bitDepth), max);
    }
}
