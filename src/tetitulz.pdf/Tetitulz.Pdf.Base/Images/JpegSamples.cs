// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// A <c>DCTDecode</c> image as the samples its filter outputs (ISO 32000-2 §7.4.8), so the
    /// image's own <c>/ColorSpace</c> can colour them, and the rule for when the codec's colours
    /// may stand in for that.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists.</b> Both renderers handed every JPEG to Skia's codec and drew what
    /// it gave back: correct exactly when the image's space is the device space the codec assumes.
    /// An <c>/ICCBased</c>, <c>/Separation</c>, <c>/DeviceN</c>, <c>/Lab</c> or <c>/Cal*</c> JPEG
    /// lost its colour space — a spot JPEG at full tint painted white where it should paint the
    /// spot's alternate, and an <c>/ICCBased</c> CMYK JPEG ignored its profile. A JPEG's filter
    /// output is samples like any other filter's; only the entropy decoding belongs to the codec.</para>
    ///
    /// <para><b>One and three components</b>: Skia's codec gives a grey frame's samples as
    /// <c>Gray8</c> and a three-component frame's as RGB (the JFIF/Adobe transform undone, which is
    /// what DCTDecode outputs) with no colour conversion when no destination colour space is asked
    /// for.</para>
    ///
    /// <para><b>Four components, through a profile of our own.</b> Skia turns CMYK into RGB before
    /// anything sees a sample — naively, unless the JPEG embeds an ICC profile, which Skia then
    /// converts through. So the samples are recovered by embedding one: a synthetic CMYK profile
    /// whose table copies three of the four inks into the three PCS channels, decoded into a linear
    /// identity colour space, so each output channel is an ink. Two decodes — C, M and Y, then K.
    /// The profile's own ICC segments are removed first, so ours is the one Skia reads. Checked
    /// bit for bit against an independent JPEG decoder on noisy CMYK JPEGs, chroma-subsampled and not. Skia
    /// undoes the Adobe inversion itself, so the samples are the inks; <see cref="Describing"/>
    /// accordingly drops a four-component image's <c>/Decode</c>, which describes that inversion —
    /// the rule the codec path applies too.</para>
    /// </remarks>
    public static class JpegSamples
    {
        /// <summary>
        /// The number of components the JPEG's frame header declares — 1, 3 or 4 for the frames
        /// <see cref="TryDecode(byte[], int, long, int, int, out int, out int, out int)"/> takes — or 0 when the bytes carry no readable frame. Read before
        /// any decode: a caller that cannot use a four-component frame refuses it for the price of
        /// its header, not the two decodes <see cref="TryDecode(byte[], int, long, int, int, out int, out int, out int)"/> spends recovering the inks.
        /// </summary>
        public static int FrameComponents(byte[] jpeg)
            => RasterImageInspector.TryInspectJpeg(jpeg, out RasterImageInfo frame) ? frame.Components : 0;

        /// <summary>
        /// Whether the codec's colours stand for the image's: unless the image declares a space other
        /// than a device space (ICC, spot, Lab, CIE, an <c>/Indexed</c> palette) <b>with as many
        /// components as the frame has</b>, in which case it must be coloured from its samples.
        /// A device space keeps the codec, as does a frame whose arity contradicts the space — its
        /// samples could not be strided by that space, and the codec's picture is the lenient
        /// reading. Both renderers ask this one question: the raster path before choosing the
        /// codec's bitmap, the SVG writer before passing the JPEG through — and so does the
        /// optimizer before re-encoding a JPEG (GAPS §19h), which is why the class is public: a
        /// plugin is outside Base's <c>InternalsVisibleTo</c>. With no <paramref name="doc"/> the
        /// dictionary is read as it stands, so a caller without a document resolves the colour
        /// space's references itself first.
        /// </summary>
        public static bool CodecColoursAreTheImages(PdfDictionary imageDict, PdfDocument doc, byte[] jpeg)
        {
            if (imageDict == null) return true;
            var space = PdfColor.ResolveSpace(imageDict[PdfName.ColorSpace] ?? imageDict[PdfName.CS], doc);
            if (space.Family == null || space.Family == PdfColorSpaceNames.DeviceGray
                || space.Family == PdfColorSpaceNames.DeviceRGB || space.Family == PdfColorSpaceNames.DeviceCMYK) return true;
            return !RasterImageInspector.TryInspectJpeg(jpeg, out RasterImageInfo frame) || frame.Components != space.Components;
        }

        /// <summary>
        /// The JPEG's 8-bit samples, interleaved, top row first — or null when Skia's codec does not
        /// decode it, its frame is not one, three or four components, its own frame exceeds the
        /// limits, or (four components) Skia would not take the profile that recovers the inks.
        /// </summary>
        /// <param name="jpeg">The <c>DCTDecode</c> stream's bytes.</param>
        /// <param name="maxDimension">The largest side the caller decodes.</param>
        /// <param name="maxArea">The largest pixel count the caller decodes.</param>
        /// <param name="width">The frame's width.</param>
        /// <param name="height">The frame's height.</param>
        /// <param name="components">1, 3 or 4.</param>
        public static byte[] TryDecode(byte[] jpeg, int maxDimension, long maxArea,
            out int width, out int height, out int components)
            => TryDecode(jpeg, maxDimension, maxArea, 0, 0, out width, out height, out components);

        /// <summary>
        /// The JPEG's 8-bit samples as <see cref="TryDecode(byte[], int, long, out int, out int, out int)"/>
        /// gives them, decoded below the frame's size when a target allows it: one or three
        /// components at the smallest DCT scale that keeps <see cref="ImageDownsampling"/>'s headroom
        /// over the target and still covers it. A caller that resamples to the target anyway (the
        /// optimizer) is spared the full decode — a 2900² frame for a 300-pixel target was 8.4 million
        /// pixels decoded and then thrown away. Four components, and a target of 0, decode at full size.
        /// </summary>
        /// <param name="jpeg">The <c>DCTDecode</c> stream's bytes.</param>
        /// <param name="maxDimension">The largest side the caller decodes, judged on the frame.</param>
        /// <param name="maxArea">The largest pixel count the caller decodes, judged on the frame.</param>
        /// <param name="targetWidth">The width the caller will resample to, or 0 for the frame's.</param>
        /// <param name="targetHeight">The height the caller will resample to, or 0 for the frame's.</param>
        /// <param name="width">The decoded width: the frame's, or the reduced one.</param>
        /// <param name="height">The decoded height.</param>
        /// <param name="components">1, 3 or 4.</param>
        public static byte[] TryDecode(byte[] jpeg, int maxDimension, long maxArea,
            int targetWidth, int targetHeight,
            out int width, out int height, out int components)
        {
            width = height = components = 0;
            if (!RasterImageInspector.TryInspectJpeg(jpeg, out RasterImageInfo frame)) return null;
            int n = frame.Components;
            if (n != PdfColorSpaceNames.GrayComponents && n != PdfColorSpaceNames.RgbComponents
                && n != PdfColorSpaceNames.CmykComponents) return null;

            byte[] samples;
            int w, h;
            if (n == PdfColorSpaceNames.CmykComponents)
            {
                using SKBitmap cmy = DecodeThrough(jpeg, CmyProfile.Value, maxDimension, maxArea);
                if (cmy == null) return null;
                using SKBitmap k = DecodeThrough(jpeg, KProfile.Value, maxDimension, maxArea);
                if (k == null || k.Width != cmy.Width || k.Height != cmy.Height) return null;
                w = cmy.Width; h = cmy.Height;
                samples = new byte[checked((long)w * h * 4)];
                ReadOnlySpan<byte> a = cmy.GetPixelSpan(), b = k.GetPixelSpan();
                for (int y = 0; y < h; y++)
                    for (int x = 0, src = y * cmy.RowBytes, ks = y * k.RowBytes, dst = y * w * 4; x < w; x++, src += 4, ks += 4, dst += 4)
                    {
                        samples[dst] = a[src];
                        samples[dst + 1] = a[src + 1];
                        samples[dst + 2] = a[src + 2];
                        samples[dst + 3] = b[ks];
                    }
            }
            else
            {
                using var data = SKData.CreateCopy(jpeg);
                using var codec = SKCodec.Create(data);
                if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg) return null;
                w = codec.Info.Width; h = codec.Info.Height;
                if (!WithinLimits(w, h, maxDimension, maxArea)) return null;

                bool grey = n == PdfColorSpaceNames.GrayComponents;
                SKColorType colourType = grey ? SKColorType.Gray8 : SKColorType.Rgba8888;
                // No colour space on the destination: the codec converts nothing (an embedded ICC
                // profile included) — the samples are what the entropy-coded data decodes to.
                SKBitmap decoded = null;
                SKSizeI reduced = ImageDownsampling.CodecSize(codec, w, h, targetWidth, targetHeight);
                if (reduced.Width > 0 && reduced.Width < w)
                {
                    var small = new SKImageInfo(reduced.Width, reduced.Height, colourType, SKAlphaType.Opaque);
                    decoded = new SKBitmap(small);
                    if (codec.GetPixels(small, decoded.GetPixels()) == SKCodecResult.Success) { w = small.Width; h = small.Height; }
                    else { decoded.Dispose(); decoded = null; }   // the advertised scale not delivered: decode in full
                }
                if (decoded == null)
                {
                    var info = new SKImageInfo(w, h, colourType, SKAlphaType.Opaque);
                    decoded = new SKBitmap(info);
                    if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success) { decoded.Dispose(); return null; }
                }
                using var bitmap = decoded;
                // Opaque, so the raw bytes are the straight colour.
                ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
                int rowBytes = bitmap.RowBytes;
                samples = new byte[checked((long)w * h * n)];
                for (int y = 0; y < h; y++)
                {
                    int src = y * rowBytes, dst = y * w * n;
                    if (grey) { pixels.Slice(src, w).CopyTo(samples.AsSpan(dst, w)); continue; }
                    for (int x = 0; x < w; x++, src += 4, dst += 3)
                    {
                        samples[dst] = pixels[src];
                        samples[dst + 1] = pixels[src + 1];
                        samples[dst + 2] = pixels[src + 2];
                    }
                }
            }

            width = w; height = h; components = n;
            return samples;
        }

        /// <summary>
        /// The image dictionary describing <see cref="TryDecode(byte[], int, long, int, int, out int, out int, out int)"/>'s samples: 8 bits whatever the
        /// file declared, and the frame's size (the abbreviations removed, so no reader finds the
        /// declared size beside it) — the raw path strides by these. <c>/Decode</c> stays for one
        /// and three components: it maps the filter's output, which these are. For four it is
        /// dropped: the inks are recovered with the Adobe inversion already undone, which is what
        /// a four-component JPEG's <c>/Decode</c> describes (the codec path never applied it
        /// either). The filter entries stay, as the JPEG 2000 route's do: the raw path reads them
        /// only to attribute a CCITT or JBIG2 failure.
        /// </summary>
        internal static PdfDictionary Describing(PdfDictionary imageDict, int width, int height, int components)
        {
            PdfDictionary d = PdfDictionary.CloneShallow(imageDict);
            d.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            d.Set(PdfName.Width, new PdfNumber(width));
            d.Set(PdfName.Height, new PdfNumber(height));
            d.Remove(PdfName.BPC);
            d.Remove(PdfName.W);
            d.Remove(PdfName.H);
            if (components == PdfColorSpaceNames.CmykComponents)
            {
                d.Remove(PdfName.Decode);
                d.Remove(PdfName.D);
            }
            return d;
        }

        private static bool WithinLimits(int w, int h, int maxDimension, long maxArea) =>
            w > 0 && h > 0 && w <= maxDimension && h <= maxDimension && (long)w * h <= maxArea;

        // ------------------------------------------------------------------ four components

        /// <summary>Output channels X, Y, Z take C, M, Y.</summary>
        private static readonly Lazy<byte[]> CmyProfile = new Lazy<byte[]>(() => InkCopyProfile(0, 1, 2));

        /// <summary>Every output channel takes K.</summary>
        private static readonly Lazy<byte[]> KProfile = new Lazy<byte[]>(() => InkCopyProfile(3, 3, 3));

        /// <summary>
        /// <paramref name="jpeg"/> with <paramref name="profile"/> embedded in place of its own,
        /// decoded into a linear colour space whose primaries are the PCS axes — so each output
        /// channel is what the profile's table put on that axis. Null when the codec does not decode
        /// it, the frame is over the limits, or Skia did not take the profile (a decode without it
        /// would be Skia's naive RGB, not inks).
        /// </summary>
        private static SKBitmap DecodeThrough(byte[] jpeg, byte[] profile, int maxDimension, long maxArea)
        {
            byte[] spliced = WithProfile(jpeg, profile);
            if (spliced == null) return null;
            using var data = SKData.CreateCopy(spliced);
            using var codec = SKCodec.Create(data);
            if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg || codec.Info.ColorSpace == null) return null;
            int w = codec.Info.Width, h = codec.Info.Height;
            if (!WithinLimits(w, h, maxDimension, maxArea)) return null;

            using var axes = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear, SKColorSpaceXyz.Identity);
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque, axes);
            var bitmap = new SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
            bitmap.Dispose();
            return null;
        }

        private const byte MarkerPrefix = 0xFF;
        private const byte App2 = 0xE2;
        private const byte Sos = 0xDA;
        private const byte Tem = 0x01;
        private const byte RstFirst = 0xD0, RstLast = 0xD7;
        private static readonly byte[] IccTag = Encoding.ASCII.GetBytes("ICC_PROFILE\0");

        /// <summary>The largest ICC payload one APP2 segment carries: 65 535 less the length field, tag and two sequence bytes.</summary>
        private const int IccChunk = 65535 - 2 - 12 - 2;

        /// <summary>
        /// SOI, <paramref name="profile"/> as <c>ICC_PROFILE</c> APP2 segments, then every marker
        /// segment the file has up to its first SOS except its own APP2 ones, then the rest as it is.
        /// Every APP2 goes, not only <c>ICC_PROFILE</c>: a profile split over several segments
        /// numbers them, and its numbering beside ours leaves Skia with none; the other APP2 uses
        /// (FlashPix, multi-picture) say nothing about the samples. Null when the segments before
        /// SOS cannot be walked.
        /// </summary>
        private static byte[] WithProfile(byte[] jpeg, byte[] profile)
        {
            if (jpeg.Length < 4 || jpeg[0] != MarkerPrefix) return null;
            var o = new MemoryStream(jpeg.Length + profile.Length + 64);
            o.Write(jpeg, 0, 2);
            int chunks = (profile.Length + IccChunk - 1) / IccChunk;
            for (int c = 0; c < chunks; c++)
            {
                int len = Math.Min(IccChunk, profile.Length - c * IccChunk);
                int segment = 2 + IccTag.Length + 2 + len;
                o.WriteByte(MarkerPrefix); o.WriteByte(App2);
                o.WriteByte((byte)(segment >> 8)); o.WriteByte((byte)segment);
                o.Write(IccTag, 0, IccTag.Length);
                o.WriteByte((byte)(c + 1)); o.WriteByte((byte)chunks);
                o.Write(profile, c * IccChunk, len);
            }

            int i = 2;
            while (true)
            {
                if (i + 2 > jpeg.Length || jpeg[i] != MarkerPrefix) return null;
                byte marker = jpeg[i + 1];
                if (marker == MarkerPrefix) { i++; continue; }                 // fill byte
                if (marker == Sos) break;
                if (marker == Tem || (marker >= RstFirst && marker <= RstLast)) { o.Write(jpeg, i, 2); i += 2; continue; }
                if (i + 4 > jpeg.Length) return null;
                int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
                if (length < 2 || (long)i + 2 + length > jpeg.Length) return null;
                if (marker != App2) o.Write(jpeg, i, 2 + length);
                i += 2 + length;
            }
            o.Write(jpeg, i, jpeg.Length - i);
            return o.ToArray();
        }

        /// <summary>
        /// A version 2 output (<c>prtr</c>) CMYK profile, PCS XYZ, whose one table (<c>A2B0</c>, a
        /// <c>lut16Type</c> of grid 2 with identity curves) puts ink <paramref name="x"/> on X,
        /// <paramref name="y"/> on Y and <paramref name="z"/> on Z, linearly. Full scale is
        /// <c>0xFFFF</c>, which Skia reads as 1.0 — <c>0x8000</c>, the ICC encoding's 1.0, came
        /// back as half.
        /// </summary>
        private static byte[] InkCopyProfile(int x, int y, int z)
        {
            int[] source = { x, y, z };
            var lut = new MemoryStream();
            Ascii(lut, "mft2"); U32(lut, 0);
            lut.WriteByte(4); lut.WriteByte(3); lut.WriteByte(2); lut.WriteByte(0);   // in, out, grid, pad
            foreach (int v in new[] { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000 }) U32(lut, (uint)v);
            U16(lut, 2); U16(lut, 2);                                                  // curve entries in, out
            for (int i = 0; i < 4; i++) { U16(lut, 0); U16(lut, 0xFFFF); }
            for (int cell = 0; cell < 16; cell++)                                       // the first input varies slowest
                for (int o = 0; o < 3; o++)
                    U16(lut, ((cell >> (3 - source[o])) & 1) == 1 ? 0xFFFF : 0);
            for (int i = 0; i < 3; i++) { U16(lut, 0); U16(lut, 0xFFFF); }
            byte[] table = lut.ToArray();

            var white = new MemoryStream();
            Ascii(white, "XYZ "); U32(white, 0);
            foreach (double d in new[] { PdfColor.D50X, PdfColor.D50Y, PdfColor.D50Z }) U32(white, (uint)SaturatingCast.ToInt(Math.Round(d * 65536)));
            byte[] wtpt = white.ToArray();

            const int headerAndCount = 128 + 4, tagEntry = 12;
            int tableAt = headerAndCount + 2 * tagEntry;
            int whiteAt = tableAt + ((table.Length + 3) & ~3);
            int size = whiteAt + wtpt.Length;

            var s = new MemoryStream(size);
            U32(s, (uint)size);
            Pad(s, 8);
            s.WriteByte(2); s.WriteByte(0x10); s.WriteByte(0); s.WriteByte(0);        // version 2.1
            Ascii(s, "prtr"); Ascii(s, "CMYK"); Ascii(s, "XYZ ");
            Pad(s, 36);
            Ascii(s, "acsp");
            Pad(s, 68);
            foreach (double d in new[] { PdfColor.D50X, PdfColor.D50Y, PdfColor.D50Z }) U32(s, (uint)SaturatingCast.ToInt(Math.Round(d * 65536)));
            Pad(s, 128);
            U32(s, 2);
            Ascii(s, "A2B0"); U32(s, (uint)tableAt); U32(s, (uint)table.Length);
            Ascii(s, "wtpt"); U32(s, (uint)whiteAt); U32(s, (uint)wtpt.Length);
            s.Write(table, 0, table.Length);
            Pad(s, whiteAt);
            s.Write(wtpt, 0, wtpt.Length);
            return s.ToArray();
        }

        private static void Pad(Stream s, long to) { while (s.Length < to) s.WriteByte(0); }
        private static void Ascii(Stream s, string t) { byte[] b = Encoding.ASCII.GetBytes(t); s.Write(b, 0, b.Length); }
        private static void U32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    }
}
