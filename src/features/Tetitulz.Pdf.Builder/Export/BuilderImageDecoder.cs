// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Imaging;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>An image ready to embed as a PDF XObject.</summary>
    internal sealed class DecodedImage
    {
        /// <summary>The two values without which the image means nothing.</summary>
        /// <remarks>
        /// A constructor rather than <c>required</c>. netstandard2.0 has no
        /// <c>RequiredMemberAttribute</c> and this assembly ships that leg — but it is the better
        /// form regardless, and for the reason CLAUDE.md gives for <c>CertificateData</c> in
        /// Signing: <c>required</c> states the invariant to the compiler, a constructor <b>checks</b>
        /// it.
        /// </remarks>
        public DecodedImage(byte[] data, string filter)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrEmpty(filter)) throw new ArgumentException("A filter name is required.", nameof(filter));

            Data = data;
            Filter = filter;
        }

        public byte[] Data;            // already in the named filter's encoding
        public int Width;
        public int Height;
        public string Filter;          // "DCTDecode" (JPEG), "JPXDecode" (JPEG 2000) or "FlateDecode" (decoded RGB)
        /// <summary>The colour space name, or null for a JPEG 2000 file whose own colour space applies (no /ColorSpace is written).</summary>
        public string? ColorSpace = PdfColorSpaceNames.DeviceRGB;
        /// <summary>Bits per component; 0 for a JPEG 2000 file whose components' depths the codestream states.</summary>
        public int BitsPerComponent = 8;
        /// <summary>A JPEG 2000 file declares an opacity channel: written with /SMaskInData 1.</summary>
        public int SMaskInData;

        /// <summary>Raw 8-bit alpha, one byte per pixel — becomes a real <c>/SMask</c>. Null = opaque.</summary>
        public byte[]? SoftMask;

        /// <summary>Explicit <c>/Decode</c> array (a CMYK JPEG needs one). Null = the space's default.</summary>
        public double[]? Decode;
    }

    /// <summary>
    /// Decodes JPEG and PNG image bytes into something the Core image-XObject writer can
    /// embed. JPEG is embedded verbatim via <c>DCTDecode</c> (dimensions read from the SOF
    /// marker). PNG is fully decoded to 8-bit RGB -- palette expanded, Adam7 de-interlaced,
    /// and any alpha carried out as a real <c>/SMask</c> rather than composited over white --
    /// then re-deflated. CMYK JPEG returns null (skipped). GIF and BMP are decoded to RGB by
    /// <see cref="GifImageDecoder"/> / <see cref="BmpImageDecoder"/> in Core and deflated here.
    ///
    /// <para>The type is public so a converter can ask <see cref="CanDecode"/> the same question
    /// this class will answer later, at the point where it still knows which <c>src</c> the bytes
    /// came from. <see cref="Decode"/> and <see cref="DecodedImage"/> stay internal — the decoded
    /// form is an implementation detail of writing the XObject.</para>
    /// </summary>
    public static class BuilderImageDecoder
    {
        internal static DecodedImage? Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) return null;
            if (RasterImageInspector.IsJpeg(bytes)) return DecodeJpeg(bytes);
            if (RasterImageInspector.IsJpeg2000(bytes)) return DecodeJpx(bytes);
            if (RasterImageInspector.IsPng(bytes)) return DecodePng(bytes);
            // Through the inspector, like the two above it. These were open-coded, which left the
            // cluster HALF consolidated — the state CLAUDE.md calls more dangerous than not
            // consolidating at all, because `CanDecode` asks the same question through
            // `RasterImageInspector.TryInspect` and the two copies could then drift on what counts
            // as a GIF. They agree today; nothing structural was holding them together.
            if (RasterImageInspector.IsGif(bytes)) return DecodeGif(bytes);
            if (RasterImageInspector.IsBmp(bytes)) return DecodeBmp(bytes);
            return null;
        }

        // ------------------------------------------------------------------ GIF / BMP

        // Both decode to plain RGB in Core (no zlib needed there — that is why PNG stays here)
        // and are deflated on the way into the XObject, exactly like the PNG path.
        private static DecodedImage? DecodeGif(byte[] b)
        {
            if (!GifImageDecoder.TryDecode(b, out byte[] rgb, out byte[] alpha,
                    out int w, out int h, out _))
                return null;

            // A transparent index becomes a real /SMask, exactly as PNG alpha does. The two moved
            // together on purpose: doing it for one format only would leave two transparency
            // models in one renderer.
            return BuildRgbImage(rgb, alpha, w, h);
        }

        private static DecodedImage? DecodeBmp(byte[] b)
        {
            if (!BmpImageDecoder.TryDecode(b, out byte[] rgb, out int w, out int h, out _)) return null;
            return BuildRgbImage(rgb, null, w, h);
        }

        /// <summary>
        /// Whether <see cref="Decode"/> can turn these bytes into an embeddable image, and if not,
        /// why — phrased for a caller to put in front of a user.
        ///
        /// <para>Exists so the decision can be made <b>where the source URL is known</b>. The
        /// exporter sees only bytes, so a failure there can say "an image could not be decoded"
        /// and nothing more; the mapper knows it was <c>logo.gif</c>. Sharing the format opinion
        /// through <see cref="RasterImageInspector"/> is what keeps this answer identical to the
        /// one <see cref="Decode"/> will give.</para>
        /// </summary>
        public static bool CanDecode(byte[] bytes, out string? reason)
        {
            reason = null;
            if (bytes == null || bytes.Length == 0) { reason = "the image is empty"; return false; }

            if (!RasterImageInspector.TryInspect(bytes, out var info))
            {
                reason = "the bytes are not a recognised raster image (expected JPEG or PNG)";
                return false;
            }

            switch (info.Format)
            {
                case RasterImageFormat.Jpeg:
                    // One rule, Core's JpegColorSpace, shared with DecodeJpeg and Base's image writers.
                    return JpegColorSpace.Of(info, out reason) != null;

                case RasterImageFormat.Png:
                    // Progressive JPEG is reported through the same flag but is decoded by the
                    // DCT decoder in the viewer, so only PNG's interlacing was ever a problem —
                    // and Adam7 is handled now.
                    return true;

                case RasterImageFormat.Gif:
                    return true;

                case RasterImageFormat.Jpeg2000:
                    // The same rule DecodeJpx embeds by: a file whose colour space a PDF cannot
                    // name, or whose SIZ is unreadable, is refused here with that reason.
                    return JpxImageDictionary.TryDescribe(bytes, out _, out _, out _, out _, out _, out reason);

                case RasterImageFormat.Bmp:
                    // Ask the decoder itself rather than guessing: RLE compression and exotic
                    // bit depths are only visible once the DIB header is read, and a second
                    // opinion here is how CanDecode and Decode come to disagree. Its header read
                    // makes every refusal its decode makes, before a pixel, so the answer is the
                    // decode's without the decode — which Decode then runs once.
                    if (BmpImageDecoder.TryReadLayout(bytes, out _, out string bmpReason)) return true;
                    reason = bmpReason ?? "the BMP could not be decoded";
                    return false;

                default:
                    reason = $"{info.Format} images are not supported by the managed engine " +
                             "(JPEG, JPEG 2000, PNG, GIF and BMP are)";
                    return false;
            }
        }

        // ------------------------------------------------------------------ JPEG 2000

        /// <summary>
        /// A JP2 file or raw codestream, wrapped as a <c>/JPXDecode</c> image with the dictionary
        /// <see cref="JpxImageDictionary"/> prescribes: the SIZ's size, the depth when the components
        /// share one, the colour space when the file names a device space and none otherwise, and
        /// the opacity channel through <c>/SMaskInData</c>.
        /// </summary>
        private static DecodedImage? DecodeJpx(byte[] b)
        {
            if (!JpxImageDictionary.TryDescribe(b, out int width, out int height, out int bitsPerComponent,
                    out string? colorSpace, out int smaskInData, out _))
                return null;
            return new DecodedImage(b, PdfFilterNames.JpxDecode)
            {
                Width = width, Height = height,
                ColorSpace = colorSpace,
                BitsPerComponent = bitsPerComponent,
                SMaskInData = smaskInData,
            };
        }

        // ------------------------------------------------------------------ JPEG

        /// <summary>
        /// Reads a JPEG's frame header through <see cref="RasterImageInspector"/> — the one JPEG
        /// marker walk — and wraps the bytes as a <c>/DCTDecode</c> image.
        /// </summary>
        /// <remarks>
        /// <para><b>The one marker walk, not a private copy.</b> A walk without fill-byte handling (a
        /// run of <c>0xFF</c> before a marker code is legal padding, and some encoders emit it), a
        /// <c>segLen &lt; 2</c> guard or a stop at SOS would read a padded JPEG's segment length out
        /// of the wrong two bytes and jump to a wrong offset, from which it either resynchronises past
        /// the frame header or falls off the end — the image dropped with a <c>null</c> return and
        /// nothing saying why.</para>
        /// </remarks>
        private static DecodedImage? DecodeJpeg(byte[] b)
        {
            if (!RasterImageInspector.TryInspect(b, out var info)) return null;
            if (info.Width <= 0 || info.Height <= 0) return null;

            string? colorSpace = JpegColorSpace.Of(info, out _);
            if (colorSpace == null) return null;

            return new DecodedImage(b, "DCTDecode")
            {
                Width = info.Width, Height = info.Height,
                ColorSpace = colorSpace,
                BitsPerComponent = 8,
                // Adobe writes CMYK JPEG components inverted (JpegColorSpace.DecodeFor).
                Decode = JpegColorSpace.DecodeFor(info),
            };
        }


        // ------------------------------------------------------------------ PNG

        /// <summary>
        /// Decodes a PNG through <see cref="PngImageDecoder"/> and wraps it for embedding.
        /// </summary>
        /// <remarks>
        /// <para>This file carried its own decoder — chunk walk, zlib, unfiltering, Adam7, every
        /// colour type — and so did <c>DocxToPdf.Rendering.PngImage</c>. Its own comments named that
        /// one twice while explaining what each had that the other lacked, which by CLAUDE.md's rule
        /// is a duplication report rather than documentation. The union moved down into
        /// <c>Core.Imaging</c>, where <c>Tetitulz.Pdf.Signing</c> can reach it too — it may
        /// reference neither Base nor Builder and needs a PNG logo for a visible signature.</para>
        ///
        /// <para><b>Nothing about the output changed.</b> The alpha plane still becomes a real
        /// <c>/SMask</c> here; what moved is only where the bytes are turned into samples.</para>
        /// </remarks>
        private static DecodedImage? DecodePng(byte[] b)
            => PngImageDecoder.TryDecode(b, out var png)
                ? BuildRgbImage(png.Rgb, png.Alpha, png.Width, png.Height)
                : null;

        /// <summary>
        /// Packs decoded RGB (and any alpha) into an embeddable image. The alpha becomes a real
        /// <c>/SMask</c>; a fully opaque one is dropped by the writer.
        /// </summary>
        private static DecodedImage BuildRgbImage(byte[] rgb, byte[]? alpha, int width, int height)
            => new DecodedImage(FlateEncodeFilter.Encode(rgb), "FlateDecode")
            {
                Width = width,
                Height = height,
                ColorSpace = PdfColorSpaceNames.DeviceRGB,
                BitsPerComponent = 8,
                SoftMask = alpha,   // raw; the writer compresses and drops an all-opaque mask
            };

    }
}
