// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>Raster container formats this library can recognise from a file header.</summary>
    public enum RasterImageFormat
    {
        Unknown = 0,
        Jpeg,
        Png,
        Gif,
        Bmp,
        Webp,
        Tiff,
        /// <summary>A JP2 file or a raw JPEG 2000 codestream.</summary>
        Jpeg2000,
    }

    /// <summary>
    /// What a raster image's header says about it, without decoding a single pixel.
    /// </summary>
    public readonly struct RasterImageInfo
    {
        internal RasterImageInfo(RasterImageFormat format, int width, int height, int components,
            bool hasAlpha, bool isInterlaced, bool hasAdobeMarker, int adobeTransform,
            int exifOrientation = 1)
        {
            Format = format;
            Width = width;
            Height = height;
            Components = components;
            HasAlpha = hasAlpha;
            IsInterlaced = isInterlaced;
            HasAdobeMarker = hasAdobeMarker;
            AdobeTransform = adobeTransform;
            ExifOrientation = exifOrientation;
        }

        public RasterImageFormat Format { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Colour components per pixel (1 grey, 3 RGB, 4 CMYK), or 0 when unknown.</summary>
        public int Components { get; }

        /// <summary>The format declares an alpha channel (PNG colour type 4/6, GIF transparent index).</summary>
        public bool HasAlpha { get; }

        /// <summary>Interlaced/progressive storage (PNG Adam7, GIF interlace flag).</summary>
        public bool IsInterlaced { get; }

        /// <summary>
        /// A JPEG carries an Adobe APP14 marker. This is the only reliable signal that a
        /// four-component frame is CMYK rather than something else, which is why it is surfaced
        /// rather than guessed at.
        /// </summary>
        public bool HasAdobeMarker { get; }

        /// <summary>The APP14 transform byte (0 = CMYK/RGB, 1 = YCbCr, 2 = YCCK), or -1.</summary>
        public int AdobeTransform { get; }

        /// <summary>
        /// A JPEG's EXIF orientation (TIFF tag 274): how the stored pixels are turned to be shown.
        /// 1 is as stored; 2–4 flip or turn it half round; 5–8 turn it a quarter, so its shown
        /// width is its stored <see cref="Height"/>. 1 when the file states none, or states a value
        /// outside 1–8.
        /// </summary>
        /// <remarks>
        /// A browser applies it by default (CSS <c>image-orientation: from-image</c>). A phone stores a
        /// portrait photo sideways with orientation 6, so ignoring it prints the photo on its side.
        /// </remarks>
        public int ExifOrientation { get; }

        /// <summary>
        /// Whether <see cref="ExifOrientation"/> turns the image a quarter (5–8), exchanging its
        /// shown width and height.
        /// </summary>
        public bool ExifSwapsAxes => ExifOrientation >= 5 && ExifOrientation <= 8;

        public override string ToString() =>
            $"{Format} {Width}×{Height}, {Components} component(s)" +
            (HasAlpha ? ", alpha" : "") + (IsInterlaced ? ", interlaced" : "");
    }

    /// <summary>
    /// Reads a raster image's format and dimensions from its header.
    ///
    /// <para><b>Why this is one class in Core.</b> Dimensions read by one piece of code and pixels
    /// decoded by another, in different assemblies, would each have their own opinion about which
    /// formats exist — a sizing side knowing GIF and an embedding side not would <i>measure</i> a
    /// GIF and then <i>drop</i> it, and
    /// because the layout still advanced by the measured height the result was a correctly-sized
    /// blank hole — the most confusing possible failure, since the page looks deliberate.
    /// Asking one component means the two answers cannot disagree.</para>
    ///
    /// <para>Pure header sniffing: no allocation beyond the returned struct, no decoding, and
    /// nothing that needs APIs missing from the <c>netstandard2.0</c> leg.</para>
    /// </summary>
    public static class RasterImageInspector
    {
        /// <summary>
        /// True when <paramref name="b"/> begins with a JPEG SOI followed by a marker
        /// (<c>FF D8 FF</c>).
        ///
        /// <para><b>Three bytes, not two.</b> <c>FF D8</c> alone accepts a two-byte prefix of anything and hands it to a decoder that
        /// cannot read it. A real JPEG always has a marker after the SOI.</para>
        /// </summary>
        public static bool IsJpeg(byte[] b)
            => b != null && b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

        /// <summary>
        /// True when <paramref name="b"/> begins with the PNG signature's four identifying bytes
        /// (<c>89 'P' 'N' 'G'</c>).
        ///
        /// <para><b>Four bytes, not two.</b> The SVG renderer's copy checked <c>89 'P'</c> only,
        /// while the raster renderer's checked all four — the two paths disagreeing about the same
        /// question, which is this repository's recurring defect shape.</para>
        /// </summary>
        public static bool IsPng(byte[] b)
            => b != null && b.Length >= 8
            && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

        /// <summary>True for a GIF87a/GIF89a header.</summary>
        public static bool IsGif(byte[] b)
            => b != null && b.Length >= 6
            && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F';

        /// <summary>True for a Windows bitmap header.</summary>
        public static bool IsBmp(byte[] b)
            => b != null && b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M';

        /// <summary>True for a JP2 file's signature box or a raw JPEG 2000 codestream's SOC and SIZ markers — the one JPEG 2000 framing check, <see cref="Jpx.JpxHeader.IsJpx"/>.</summary>
        public static bool IsJpeg2000(byte[] b) => Jpx.JpxHeader.IsJpx(b);

        /// <summary>
        /// Identifies <paramref name="bytes"/> and reads its dimensions. Returns <c>false</c>
        /// when the header is not a recognised raster format or is truncated.
        /// </summary>
        public static bool TryInspect(byte[] bytes, out RasterImageInfo info)
        {
            info = default;
            if (bytes == null || bytes.Length < 12) return false;

            // PNG, GIF and BMP go through the published predicates: the conditions written here
            // were byte-for-byte the same tests, and a class that publishes "is this a PNG?" and
            // then answers it a second time inline is one edit away from disagreeing with itself.
            //
            // JPEG deliberately does NOT, and the looser gate here is not an oversight. IsJpeg
            // demands FF D8 FF because it is a SNIFF — its answer is final, and a two-byte prefix
            // of anything would be handed to a decoder that cannot read it. This is a DISPATCH into
            // the JPEG marker walk, which scans for a real SOF and returns false when it finds none, so
            // the validation happens after the gate rather than at it. Tightening this to IsJpeg
            // would reject a JPEG carrying fill bytes between its SOI and the next marker — a file
            // the parser below reads correctly today.
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ReadJpeg(bytes, out info);
            if (IsPng(bytes)) return TryInspectPng(bytes, out info);
            if (IsGif(bytes)) return TryInspectGif(bytes, out info);
            if (IsBmp(bytes)) return TryInspectBmp(bytes, out info);
            if (bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
                return TryInspectWebp(bytes, out info);
            if (IsJpeg2000(bytes)) return TryInspectJpx(bytes, out info);
            if ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00)
                || (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A))
            {
                // TIFF dimensions live in the IFD, which needs a real parse; the format identity
                // is what callers need in order to decline it with a reason.
                info = new RasterImageInfo(RasterImageFormat.Tiff, 0, 0, 0, false, false, false, -1);
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ JPEG 2000

        /// <summary>
        /// The SIZ's image area and, for a JP2 file, the colour channels its header declares
        /// (else every component is one) and whether a channel is opacity — through the one JP2
        /// reader, <see cref="Jpx.JpxHeader"/>, and the one description of its boxes.
        /// </summary>
        private static bool TryInspectJpx(byte[] b, out RasterImageInfo info)
        {
            info = default;
            if (!Jpx.JpxHeader.TryReadImageSize(b, out long width, out long height, out int components)) return false;
            if (width < 1 || height < 1 || width > int.MaxValue || height > int.MaxValue) return false;
            int colour = components;
            bool opacity = false;
            if (Jpx.JpxHeader.IsJp2Container(b) && Jpx.Jp2ColourDescription.TryRead(b, out Jpx.Jp2ColourDescription header, out _))
            {
                int declared = header.ColourChannelCount();
                if (declared > 0) colour = declared;
                opacity = header.DeclaresOpacity();
            }
            info = new RasterImageInfo(RasterImageFormat.Jpeg2000, (int)width, (int)height, colour, opacity, false, false, -1);
            return true;
        }

        // ------------------------------------------------------------------ JPEG

        /// <summary>
        /// Reads a JPEG's frame header — dimensions, component count, the Adobe APP14 marker and the
        /// EXIF orientation — by walking its marker segments (ITU-T T.81, Table B.1), with no entropy
        /// decoding. Returns <c>false</c> when the bytes do not start with an SOI or carry no SOF
        /// with usable dimensions, so a caller can tell "not a JPEG" from a zero-sized one.
        /// </summary>
        /// <remarks>
        /// The one JPEG marker walk: <see cref="TryInspect"/> dispatches here, and so does every
        /// reader that wants a JPEG and nothing else (the raster decode, the image writers, the SVG
        /// converter). An Adobe APP14 marker is read anywhere in the chain up to the scan, before or
        /// after the frame header.
        /// </remarks>
        public static bool TryInspectJpeg(byte[] bytes, out RasterImageInfo info)
        {
            info = default;
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
            return ReadJpeg(bytes, out info);
        }

        private static bool ReadJpeg(byte[] b, out RasterImageInfo info)
        {
            info = default;
            int i = 2, len = b.Length;
            int width = 0, height = 0, comps = 0;
            bool adobe = false;
            int transform = -1;
            bool progressive = false;
            bool haveFrame = false;
            int orientation = 1;

            while (i + 1 < len)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }                          // fill byte
                // Standalone markers, which carry no length field. 0x01 is TEM — rare, but a
                // walker that treats it as length-bearing reads two bytes of the NEXT marker as a
                // segment length and jumps to a wrong offset.
                if (marker == 0x01 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                if (marker == 0xD9 || marker == 0xDA) break;                    // EOI / start of scan
                if (i + 3 >= len) break;
                int segLen = (b[i + 2] << 8) | b[i + 3];
                if (segLen < 2) break;

                // APP14 "Adobe" — the marker that distinguishes CMYK from a four-component frame
                // of something else. Read even after the frame header, since it may follow it.
                if (marker == 0xEE && i + 4 + 5 <= len
                    && b[i + 4] == (byte)'A' && b[i + 5] == (byte)'d' && b[i + 6] == (byte)'o'
                    && b[i + 7] == (byte)'b' && b[i + 8] == (byte)'e')
                {
                    adobe = true;
                    int transformOffset = i + 2 + segLen - 1;
                    if (transformOffset < len) transform = b[transformOffset];
                }

                // APP1 "Exif\0\0" — the orientation a camera recorded. Read only inside the
                // segment: its offsets are the file's, so each is checked against the segment's end.
                if (marker == 0xE1 && orientation == 1)
                    orientation = ReadExifOrientation(b, i + 4, Math.Min(len, i + 2 + segLen));

                bool isSof = marker >= 0xC0 && marker <= 0xCF
                             && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof && !haveFrame)
                {
                    if (i + 9 >= len) return false;
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    comps = b[i + 9];
                    progressive = marker == 0xC2 || marker == 0xC6 || marker == 0xCA || marker == 0xCE;
                    haveFrame = true;
                }
                i += 2 + segLen;
            }

            if (!haveFrame || width <= 0 || height <= 0) return false;
            info = new RasterImageInfo(RasterImageFormat.Jpeg, width, height, comps,
                hasAlpha: false, isInterlaced: progressive, hasAdobeMarker: adobe, adobeTransform: transform,
                exifOrientation: orientation);
            return true;
        }

        /// <summary>
        /// The orientation tag (0x0112) of the first image directory in an APP1 Exif segment whose
        /// data runs from <paramref name="start"/> to <paramref name="end"/>, or 1.
        /// </summary>
        /// <remarks>
        /// The segment is a TIFF structure (Exif 2.3 §4.5.4): <c>Exif\0\0</c>, a byte-order mark
        /// (<c>II</c> or <c>MM</c>), 42, the offset of IFD0, then that directory's 12-byte entries.
        /// Every offset is the file's to choose, so each read is checked against the segment's end
        /// and a directory that runs past it simply ends; the walk is bounded by the segment's
        /// length, at most 65,533 bytes.
        /// </remarks>
        private static int ReadExifOrientation(byte[] b, int start, int end)
        {
            if (end - start < 14) return 1;
            if (b[start] != (byte)'E' || b[start + 1] != (byte)'x' || b[start + 2] != (byte)'i'
                || b[start + 3] != (byte)'f' || b[start + 4] != 0 || b[start + 5] != 0) return 1;

            int tiff = start + 6;
            bool little = b[tiff] == (byte)'I' && b[tiff + 1] == (byte)'I';
            bool big = b[tiff] == (byte)'M' && b[tiff + 1] == (byte)'M';
            if (!little && !big) return 1;

            int U16(int o) => little ? b[o] | (b[o + 1] << 8) : (b[o] << 8) | b[o + 1];
            long U32(int o) => little
                ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24))
                : (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

            if (U16(tiff + 2) != 42) return 1;
            long ifd = tiff + U32(tiff + 4);
            if (ifd + 2 > end) return 1;

            int entries = U16((int)ifd);
            for (int k = 0; k < entries; k++)
            {
                long entry = ifd + 2 + 12L * k;
                if (entry + 12 > end) break;
                int e = (int)entry;
                // SHORT (type 3), count 1: the value sits in the first two bytes of the value field.
                if (U16(e) == 0x0112 && U16(e + 2) == 3)
                {
                    int value = U16(e + 8);
                    return value >= 1 && value <= 8 ? value : 1;
                }
            }
            return 1;
        }

        // ------------------------------------------------------------------ PNG

        private static bool TryInspectPng(byte[] b, out RasterImageInfo info)
        {
            info = default;
            // 8-byte signature, then the IHDR chunk: length(4) type(4) w(4) h(4) depth(1)
            // colourType(1) compression(1) filter(1) interlace(1).
            if (b.Length < 33) return false;
            if (b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R') return false;

            int w = ReadBE32(b, 16), h = ReadBE32(b, 20);
            if (w <= 0 || h <= 0) return false;
            int colourType = b[25];
            bool interlaced = b[28] != 0;

            int comps = colourType switch
            {
                0 => 1,   // greyscale
                2 => 3,   // truecolour
                3 => 3,   // palette — expands to RGB
                4 => 1,   // greyscale + alpha
                6 => 3,   // truecolour + alpha
                _ => 0,
            };
            bool alpha = colourType == 4 || colourType == 6;

            info = new RasterImageInfo(RasterImageFormat.Png, w, h, comps, alpha, interlaced, false, -1);
            return true;
        }

        // ------------------------------------------------------------------ GIF

        private static bool TryInspectGif(byte[] b, out RasterImageInfo info)
        {
            info = default;
            if (b.Length < 13) return false;
            int w = b[6] | (b[7] << 8);
            int h = b[8] | (b[9] << 8);
            if (w <= 0 || h <= 0) return false;
            // A GIF's interlace flag lives on the image descriptor, not the screen descriptor, so
            // it is not knowable from the header alone; report false and let the decoder find it.
            info = new RasterImageInfo(RasterImageFormat.Gif, w, h, 3, hasAlpha: true,
                isInterlaced: false, hasAdobeMarker: false, adobeTransform: -1);
            return true;
        }

        // ------------------------------------------------------------------ BMP

        private static bool TryInspectBmp(byte[] b, out RasterImageInfo info)
        {
            info = default;
            if (b.Length < 26) return false;
            int w = ReadLE32(b, 18);
            int h = ReadLE32(b, 22);
            if (w <= 0) return false;
            // A negative height means a top-down bitmap; the magnitude is still the height. The one
            // negative value with no magnitude, int.MinValue, is refused as the decoder refuses it:
            // Math.Abs of it throws, and this method's contract is false, never an exception.
            if (h == 0 || h == int.MinValue) return false;
            info = new RasterImageInfo(RasterImageFormat.Bmp, w, Math.Abs(h), 3,
                hasAlpha: false, isInterlaced: false, hasAdobeMarker: false, adobeTransform: -1);
            return true;
        }

        // ------------------------------------------------------------------ WebP

        /// <summary>
        /// Canvas dimensions from a WebP's first chunk. The RIFF/WEBP signature is checked by
        /// <see cref="TryInspect"/> before this is reached.
        /// </summary>
        /// <remarks>
        /// <para><b>Each variant's own signature is verified, and that came from the copy this
        /// replaced.</b> A private reader in <c>PdfOperations.PageRendering</c> checked the VP8L
        /// <c>0x2F</c> byte and the VP8 <c>9D 01 2A</c> start code where this did not — so this one
        /// would read four bytes of whatever followed the fourcc and report them as a size. Merging
        /// the two without noticing would have made the <i>shared</i> reader the weaker one, which
        /// is the opposite of the point.</para>
        ///
        /// <para><b>Alpha is derived, not assumed.</b> This reported <c>hasAlpha: true</c> for every
        /// WebP; a plain VP8 lossy bitstream has no alpha channel at all, and a VP8X container
        /// states it in a flag. Nothing in the tree reads <see cref="RasterImageInfo.HasAlpha"/>
        /// yet, which is exactly why a wrong value could sit here — the first consumer would have
        /// inherited it.</para>
        /// </remarks>
        private static bool TryInspectWebp(byte[] b, out RasterImageInfo info)
        {
            info = default;
            if (b.Length < 30) return false;

            int w = 0, h = 0;
            bool alpha = false;

            // VP8X carries canvas size; VP8 (lossy) and VP8L (lossless) encode it differently.
            if (b[12] == (byte)'V' && b[13] == (byte)'P' && b[14] == (byte)'8')
            {
                if (b[15] == (byte)'X')
                {
                    // Extended: flags at 20, then 24-bit little-endian canvas width-1 / height-1.
                    alpha = (b[20] & 0x10) != 0;
                    w = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                    h = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
                }
                else if (b[15] == (byte)' ' && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
                {
                    // Lossy: the 3-byte start code above, then 16-bit LE width/height whose low
                    // 14 bits are the dimension. A simple VP8 bitstream carries no alpha.
                    w = (b[26] | (b[27] << 8)) & 0x3FFF;
                    h = (b[28] | (b[29] << 8)) & 0x3FFF;
                }
                else if (b[15] == (byte)'L' && b.Length >= 25 && b[20] == 0x2F)
                {
                    // Lossless: the 0x2F signature above, then 14 bits width-1 and 14 bits
                    // height-1. VP8L's bitstream is ARGB throughout.
                    int bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                    w = (bits & 0x3FFF) + 1;
                    h = ((bits >> 14) & 0x3FFF) + 1;
                    alpha = true;
                }
            }

            info = new RasterImageInfo(RasterImageFormat.Webp, w, h, 3, alpha,
                isInterlaced: false, hasAdobeMarker: false, adobeTransform: -1);
            return w > 0 && h > 0;
        }

        // ------------------------------------------------------------------ helpers

        private static int ReadBE32(byte[] b, int o) =>
            (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static int ReadLE32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    }
}
