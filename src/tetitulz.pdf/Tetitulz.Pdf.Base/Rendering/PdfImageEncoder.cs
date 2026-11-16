// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The single place a rendered page surface becomes encoded bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one type rather than a method per format.</b> The PNG and JPEG encoders used to
    /// live as two private helpers on <see cref="PdfPageRenderer"/>, and every decision they had in
    /// common was made twice or not at all: the whole-page JPEG path skipped
    /// <see cref="PdfRenderOptions.PngCompression"/> (correctly), the tile JPEG path skipped the
    /// provenance stamp (not correctly), and neither had a way to reach a third format. Adding WebP
    /// through that shape would have been a third copy of the same four decisions. The rule this
    /// repository already applies to the two renderers applies here: a decision gets one
    /// implementation, reached from every path.</para>
    /// <para><b>Every format goes through the same four steps</b> — optional grey conversion,
    /// alpha reconciliation, encode, provenance stamp — so a new format cannot quietly skip one.
    /// </para>
    /// </remarks>
    internal static class PdfImageEncoder
    {
        /// <summary>
        /// Encodes <paramref name="bitmap"/> and returns the bytes.
        /// </summary>
        /// <param name="bitmap">The rendered surface. Not modified.</param>
        /// <param name="format">Which encoder to use.</param>
        /// <param name="quality">
        /// 1–100, for <see cref="PdfImageFormat.Jpeg"/> and <see cref="PdfImageFormat.WebpLossy"/>.
        /// Ignored by the lossless formats.
        /// </param>
        /// <param name="options">
        /// Supplies <see cref="PdfRenderOptions.PngCompression"/>,
        /// <see cref="PdfRenderOptions.JpegSubsampling"/>,
        /// <see cref="PdfRenderOptions.PixelFormat"/> and
        /// <see cref="PdfRenderOptions.BackgroundArgb"/>. Null means
        /// <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        internal static byte[] Encode(SKBitmap bitmap, PdfImageFormat format, int quality,
            PdfRenderOptions options)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            options ??= PdfRenderOptions.Default;

            // Grey has no alpha channel and neither does JPEG, so a caller who asked for a
            // translucent page and one of those gets it flattened. Saying so is the whole point:
            // "the background was dropped" and "this format never had alpha" produce identical
            // bytes, and only one of them is the caller's mistake.
            // The destination the render was converted into, when it was (never reported here: the render
            // reported a profile it could not use). A grey device's pixels are written grey where the format
            // has grey; WebP has none.
            RenderDestination destination = RenderDestination.Cached(options);
            bool isWebp           = format == PdfImageFormat.WebpLossless || format == PdfImageFormat.WebpLossy;
            bool wantsGrey        = options.PixelFormat == RenderPixelFormat.Grayscale || (destination?.IsGrey == true && !isWebp);
            bool formatDropsAlpha = format == PdfImageFormat.Jpeg || wantsGrey;
            if (formatDropsAlpha && (options.BackgroundArgb >> 24) != 0xFF)
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    $"BackgroundArgb 0x{options.BackgroundArgb:X8} has alpha " +
                    $"{options.BackgroundArgb >> 24}, but " +
                    (wantsGrey ? "grayscale output" : "JPEG") +
                    " has no alpha channel; the page was flattened onto white.");

            SKBitmap opaque = null;
            SKBitmap grey   = null;
            try
            {
                // Flatten first, convert second: a grey conversion of premultiplied translucent
                // pixels would carry the alpha into the luminance and darken the page.
                SKBitmap source = bitmap;
                if (formatDropsAlpha && !IsFullyOpaque(source))
                    source = opaque = FlattenOntoWhite(source);

                // A declined conversion leaves `source` alone and encodes in colour, which is why
                // this is not `source = grey = …`: the colour surface is still a correct page, and
                // the report above is what tells the caller the size win did not happen.
                if (wantsGrey)
                {
                    grey = ToGrayscale(source);
                    if (grey != null) source = grey;
                }

                byte[] encoded = format switch
                {
                    PdfImageFormat.Png          => ImageProvenance.StampPng(EncodePng(source, options.PngCompression)),
                    PdfImageFormat.Jpeg         => ImageProvenance.StampJpeg(EncodeJpeg(source, quality, options.JpegSubsampling)),
                    PdfImageFormat.WebpLossless => EncodeWebp(source, SKWebpEncoderCompression.Lossless, 100),
                    PdfImageFormat.WebpLossy    => EncodeWebp(source, SKWebpEncoderCompression.Lossy, quality),
                    _ => throw new ArgumentOutOfRangeException(nameof(format), format,
                            "Unknown image format."),
                };
                return destination == null ? encoded : Tag(encoded, format, destination, grey != null, source.Width, source.Height);
            }
            finally
            {
                grey?.Dispose();
                opaque?.Dispose();
            }
        }

        /// <summary>
        /// Embeds the destination's profile in an encoded render, where the file's own colour type can carry
        /// it: an RGB profile in an RGB file, a grey one in a grey file. Otherwise the file is left untagged and
        /// that is reported — its values are the destination's, and nothing in it says so.
        /// </summary>
        private static byte[] Tag(byte[] encoded, PdfImageFormat format, RenderDestination destination, bool greyFile,
            int width, int height)
        {
            if (destination.IsGrey != greyFile)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.DestinationProfileUnsupported,
                    destination.IsGrey
                        ? $"{format} has no grey form here, so the grey destination's profile is not embedded: the file holds its values untagged."
                        : "grey output of an RGB destination carries no profile: the file holds the destination's values reduced to grey, untagged.");
                return encoded;
            }
            return format switch
            {
                PdfImageFormat.Png  => ImageProvenance.EmbedIccPng(encoded, destination.Profile),
                PdfImageFormat.Jpeg => ImageProvenance.EmbedIccJpeg(encoded, destination.Profile),
                _                   => ImageProvenance.EmbedIccWebp(encoded, destination.Profile, width, height),
            };
        }

        // An `EncodeTo(bitmap, output, …)` helper lived here, documented as the seam for "a caller
        // writing the image to a file, a response body or a pipe" — with no call site anywhere in
        // src/ or tests/. The one entry point matching that description,
        // PdfPageRenderer.RenderPageToStream, buffers and writes the byte[] itself, which is the
        // shape the helper was added to replace. It is deleted rather than wired up, because
        // RenderPageToStream reaches PNG through RenderPageToImage — the chokepoint that gives a
        // registered IRendererPlugin first refusal — and rendering to a bitmap here to feed
        // EncodeTo would silently take that back. An unreferenced helper duplicating a live call
        // site is how the two drift; so is a "single seam" that bypasses the plugin dispatch.

        /// <summary>
        /// True when every pixel is already opaque, so an alpha-less format can encode the surface
        /// directly.
        /// </summary>
        /// <remarks>
        /// <para><b>This replaces an unconditional flatten that cost ~25% of a JPEG encode</b> —
        /// measured 8.7–9.7 ms of a 38–41 ms encode on a 1275x1650 page, spent allocating a
        /// second full-page surface and blitting into it. The page is cleared to an opaque
        /// background and drawn over with <c>SrcOver</c> in the overwhelmingly common case, so the
        /// copy almost never changed a byte.</para>
        /// <para>The scan is a fifth of the flatten's cost and exits at the first translucent
        /// pixel, so the case that still needs the copy barely pays for the check. Encoding a
        /// premultiplied surface whose alpha is everywhere 255 is identical to encoding the same
        /// surface marked opaque — premultiplication by 1 is the identity —
        /// which is what makes skipping it safe rather than merely cheaper.</para>
        /// </remarks>
        internal static bool IsFullyOpaque(SKBitmap bitmap)
        {
            if (bitmap.AlphaType == SKAlphaType.Opaque) return true;

            // Alpha is the fourth byte in both of these; any other layout is not worth a special
            // case, and answering "false" only costs the flatten that already happened before.
            if (bitmap.ColorType != SKColorType.Rgba8888 && bitmap.ColorType != SKColorType.Bgra8888)
                return false;

            var pixels = bitmap.GetPixelSpan();
            if (pixels.Length == 0) return false;

            int stride = bitmap.RowBytes;
            int rowEnd = bitmap.Width * 4;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                if (row + rowEnd > pixels.Length) return false;
                for (int x = 3; x < rowEnd; x += 4)
                    if (pixels[row + x] != 0xFF) return false;
            }
            return true;
        }

        /// <summary>
        /// Composites onto opaque white. The premultiplied surface must not be handed to an
        /// alpha-less encoder directly — a half-transparent pixel would arrive already scaled
        /// towards black.
        /// </summary>
        private static SKBitmap FlattenOntoWhite(SKBitmap bitmap)
        {
            var flattened = new SKBitmap(new SKImageInfo(
                bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            try
            {
                using var canvas = new SKCanvas(flattened);
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
                return flattened;
            }
            catch
            {
                flattened.Dispose();
                throw;
            }
        }

        /// <summary>Converts to 8-bit grey, which every encoder here writes as a single channel.</summary>
        private static SKBitmap ToGrayscale(SKBitmap bitmap)
        {
            var grey = new SKBitmap(new SKImageInfo(
                bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque));
            try
            {
                if (bitmap.CopyTo(grey, SKColorType.Gray8)) return grey;

                // A conversion the imaging library declines is reported rather than swallowed:
                // the caller asked for a smaller, faster image and is getting neither.
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    $"Could not convert a {bitmap.ColorType}/{bitmap.AlphaType} surface to Gray8; " +
                    "the page was encoded in colour instead.");
                grey.Dispose();
                return null;
            }
            catch
            {
                grey.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Encodes to PNG at the requested effort.
        /// </summary>
        /// <remarks>
        /// <para><b>The single largest fixed cost in a raster render.</b> Measured on an i5-10500:
        /// a blank 612x792 page at 150 dpi costs ~82 ms end to end, of which ~79 ms is this method.
        /// On a page with real content the share is smaller but still the biggest single item —
        /// a text-heavy page measured 77 ms total against 37 ms to reach pixels.</para>
        /// <para>PNG is lossless at every zlib level and row filter, so
        /// <see cref="PngCompression"/> moves encode time and file size only — never a pixel.</para>
        /// <para>Skia's default row-filter choice is not monotonic in effort: level 6 with
        /// <c>NoFilters</c> measured *slower* than level 3 with <c>Sub</c> and produced larger
        /// output, because unfiltered rows give the deflater less to match on. The two settings
        /// below are the measured points, not interpolations — do not "tune" them without
        /// re-running <c>PngEncodeBenchmarks</c>.</para>
        /// </remarks>
        internal static byte[] EncodePng(SKBitmap bitmap, PngCompression compression)
        {
            if (compression == PngCompression.Smallest) return EncodeViaImage(bitmap, SKEncodedImageFormat.Png, 100);

            using var pixmap = bitmap.PeekPixels();
            if (pixmap == null)
            {
                // PeekPixels returns null if the bitmap has no addressable pixels. Fall back
                // rather than return nothing — a slower encode beats no image.
                return EncodeViaImage(bitmap, SKEncodedImageFormat.Png, 100);
            }

            using var output  = new MemoryStream();
            using var wstream = new SKManagedWStream(output);
            var options = new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, zLibLevel: 3);
            if (!pixmap.Encode(wstream, options))
                return EncodeViaImage(bitmap, SKEncodedImageFormat.Png, 100);
            return output.ToArray();
        }

        private static byte[] EncodeJpeg(SKBitmap bitmap, int quality, JpegSubsampling subsampling)
        {
            // Default means "whatever the encoder picks", and routing it through the options
            // overload would silently pin today's choice as tomorrow's contract. Keep the
            // historical call so the historical bytes stay reachable.
            if (subsampling == JpegSubsampling.Default)
                return EncodeViaImage(bitmap, SKEncodedImageFormat.Jpeg, quality);

            using var pixmap = bitmap.PeekPixels();
            if (pixmap == null) return EncodeViaImage(bitmap, SKEncodedImageFormat.Jpeg, quality);

            var downsample = subsampling switch
            {
                JpegSubsampling.Chroma444 => SKJpegEncoderDownsample.Downsample444,
                JpegSubsampling.Chroma422 => SKJpegEncoderDownsample.Downsample422,
                _                         => SKJpegEncoderDownsample.Downsample420,
            };

            using var output  = new MemoryStream();
            using var wstream = new SKManagedWStream(output);
            // AlphaOption.Ignore is correct here and only here: everything reaching this method has
            // already been flattened to opaque, so there is no alpha left to blend.
            var options = new SKJpegEncoderOptions(quality, downsample, SKJpegEncoderAlphaOption.Ignore);
            if (!pixmap.Encode(wstream, options))
                return EncodeViaImage(bitmap, SKEncodedImageFormat.Jpeg, quality);
            return output.ToArray();
        }

        /// <summary>
        /// Encodes to WebP.
        /// </summary>
        /// <remarks>
        /// <para><b>WebP output carries no provenance stamp, and that is a limit rather than an
        /// oversight.</b> PNG's <c>tEXt</c> and JPEG's <c>COM</c> are header inserts into a finished
        /// file; a bare WebP is a RIFF container holding one <c>VP8</c>/<c>VP8L</c> chunk with
        /// nowhere to put metadata, and giving it one means rewriting the container into extended
        /// (<c>VP8X</c>) form. That is a format change, not a header insert, and it would alter the
        /// bytes every decoder reads. Recorded here so the difference is a decision rather than a
        /// discovery.</para>
        /// <para><see cref="SKWebpEncoderCompression.Lossless"/> ignores the quality argument in
        /// the sense that matters — output is exact either way — but the value still steers the
        /// encoder's effort, so 100 is passed rather than the caller's number.</para>
        /// </remarks>
        private static byte[] EncodeWebp(SKBitmap bitmap, SKWebpEncoderCompression compression, int quality)
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap == null) return EncodeViaImage(bitmap, SKEncodedImageFormat.Webp, quality);

            using var output  = new MemoryStream();
            using var wstream = new SKManagedWStream(output);
            if (!pixmap.Encode(wstream, new SKWebpEncoderOptions(compression, quality)))
                return EncodeViaImage(bitmap, SKEncodedImageFormat.Webp, quality);
            return output.ToArray();
        }

        private static byte[] EncodeViaImage(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(format, quality);
            return data.ToArray();
        }

        /// <summary>
        /// Encodes ink planes as one separated TIFF (TIFF 6.0 §16): PhotometricInterpretation 5, one
        /// 8-bit sample per ink per pixel, chunky, PackBits rows. Exactly Cyan, Magenta, Yellow and
        /// Black in that order is InkSet 1 (CMYK) — what every CMYK reader opens; any other set is
        /// InkSet 2 with its InkNames, the N-channel form separation composites and Photoshop's
        /// multichannel files use.
        /// </summary>
        /// <param name="width">Pixels per row.</param>
        /// <param name="height">Rows.</param>
        /// <param name="inks">Each ink's name and its plane: <paramref name="width"/> × <paramref name="height"/> bytes, 0 none, 255 full.</param>
        /// <param name="dpi">The resolution recorded in the file.</param>
        /// <remarks>
        /// Not through <see cref="Encode"/>: that takes a rendered surface, and Skia has no TIFF
        /// encoder and no surface of more than four channels. It lives here because this type is the
        /// one place encoded bytes are made.
        /// </remarks>
        internal static byte[] EncodeSeparatedTiff(int width, int height,
            System.Collections.Generic.IReadOnlyList<(string Name, byte[] Plane)> inks, int dpi)
        {
            if (inks == null || inks.Count == 0) throw new ArgumentException("at least one ink", nameof(inks));
            if (inks.Count > ushort.MaxValue) throw new ArgumentException("too many inks", nameof(inks));
            int n = inks.Count;
            long plane = (long)width * height;
            foreach (var ink in inks)
                if (ink.Plane == null || ink.Plane.Length != plane) throw new ArgumentException($"ink {ink.Name}: plane is not {width}x{height}", nameof(inks));

            bool cmyk = n == 4 && inks[0].Name == Core.Constants.PdfColorSpaceNames.CyanColorant && inks[1].Name == Core.Constants.PdfColorSpaceNames.MagentaColorant
                        && inks[2].Name == Core.Constants.PdfColorSpaceNames.YellowColorant && inks[3].Name == Core.Constants.PdfColorSpaceNames.BlackColorant;

            using var data = new MemoryStream();
            var w = new BinaryWriter(data);
            w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write(0u);   // IFD offset patched below

            // Strips: one PackBits-compressed row each.
            var row = new byte[checked(width * n)];
            var offsets = new uint[height];
            var counts = new uint[height];
            for (int y = 0; y < height; y++)
            {
                int o = y * width;
                for (int x = 0; x < width; x++)
                    for (int i = 0; i < n; i++)
                        row[x * n + i] = inks[i].Plane[o + x];
                offsets[y] = checked((uint)data.Position);
                long before = data.Position;
                // TIFF's PackBits is PDF's RunLength without the end-of-data byte (§9; 128 is a no-op
                // there), so the one encoder in Core.IO.Filters writes it and the terminator is dropped.
                byte[] packed = Core.IO.Filters.RunLengthEncodeFilter.Encode(row);
                w.Write(packed, 0, packed.Length - 1);
                counts[y] = (uint)(data.Position - before);
            }

            // Out-of-line values, each word-aligned.
            uint bitsAt = Align(w, data); for (int i = 0; i < n; i++) w.Write((ushort)8);
            uint offsetsAt = Align(w, data); foreach (uint v in offsets) w.Write(v);
            uint countsAt = Align(w, data); foreach (uint v in counts) w.Write(v);
            uint resAt = Align(w, data); w.Write((uint)dpi); w.Write(1u);
            byte[] software = System.Text.Encoding.ASCII.GetBytes("Tetitulz.Pdf\0");
            uint softwareAt = Align(w, data); w.Write(software);
            var namesText = new System.Text.StringBuilder();
            foreach (var ink in inks) namesText.Append(ink.Name).Append('\0');
            byte[] names = System.Text.Encoding.ASCII.GetBytes(namesText.ToString());
            uint namesAt = Align(w, data); w.Write(names);

            var tags = new System.Collections.Generic.List<(ushort Tag, ushort Type, uint Count, uint Value)>
            {
                (256, 4, 1, (uint)width),                                  // ImageWidth
                (257, 4, 1, (uint)height),                                 // ImageLength
                (258, 3, (uint)n, n <= 2 ? 8u | (n == 2 ? 8u << 16 : 0u) : bitsAt), // BitsPerSample
                (259, 3, 1, 32773),                                        // Compression: PackBits
                (262, 3, 1, 5),                                            // Photometric: separated
                (273, 4, (uint)height, height == 1 ? offsets[0] : offsetsAt), // StripOffsets
                (277, 3, 1, (uint)n),                                      // SamplesPerPixel
                (278, 4, 1, 1),                                            // RowsPerStrip
                (279, 4, (uint)height, height == 1 ? counts[0] : countsAt), // StripByteCounts
                (282, 5, 1, resAt),                                        // XResolution
                (283, 5, 1, resAt),                                        // YResolution
                (284, 3, 1, 1),                                            // PlanarConfiguration: chunky
                (296, 3, 1, 2),                                            // ResolutionUnit: inch
                (305, 2, (uint)software.Length, softwareAt),               // Software
                (332, 3, 1, cmyk ? 1u : 2u),                               // InkSet
                (333, 2, (uint)names.Length, namesAt),                     // InkNames
                (334, 3, 1, (uint)n),                                      // NumberOfInks
            };

            uint ifdAt = Align(w, data);
            w.Write((ushort)tags.Count);
            foreach (var t in tags) { w.Write(t.Tag); w.Write(t.Type); w.Write(t.Count); w.Write(t.Value); }
            w.Write(0u);   // no next IFD
            w.Flush();
            data.Position = 4;
            w.Write(ifdAt);
            w.Flush();
            return data.ToArray();
        }

        /// <summary>The largest side a PSD (version 1) holds — Adobe's file format specification, the file header.</summary>
        internal const int MaxPsdSide = 30000;

        /// <summary>The most channels a PSD holds, the four process inks among them.</summary>
        internal const int MaxPsdChannels = 56;

        /// <summary>
        /// The highest resolution a PSD records: ResolutionInfo holds it as a signed 16.16 fixed-point number,
        /// so 32 768 dpi does not fit — and a small page at a high resolution stays within the side limit.
        /// </summary>
        internal const int MaxPsdDpi = 32767;

        /// <summary>
        /// Separations as a Photoshop document in CMYK mode: the four
        /// process planes, then each spot ink as a spot channel, every plane stored as <c>255 −</c> its ink
        /// (Photoshop's CMYK data is additive), PackBits a row (compression 1).
        /// </summary>
        /// <param name="width">Columns, 1 … 30 000.</param>
        /// <param name="height">Rows, 1 … 30 000.</param>
        /// <param name="inks">Cyan, Magenta, Yellow and Black, then the spot inks: each its name, its plane
        /// (<paramref name="width"/> × <paramref name="height"/> bytes, 0 none, 255 full) and, for a spot ink, the
        /// colour Photoshop shows it in (null for black).</param>
        /// <param name="dpi">The resolution recorded in the file.</param>
        /// <remarks>
        /// <para>The grammar: Adobe's Photoshop File Formats Specification for the sections, the resource block,
        /// the alpha-channel names (1006) and the image data; the two structures it leaves to an API guide that is
        /// not public — ResolutionInfo (1005) and DisplayInfo — as existing writers write them and
        /// existing readers read them, which agree. DisplayInfo is written in both its forms: 1007, fourteen bytes a
        /// spot channel, and 1077, a version and thirteen bytes a channel, as
        /// readers read it; each colour is RGB (colour space 0, sixteen bits a component),
        /// solidity 0 — an ink that lets what is beneath show, as a spot ink does — and kind 2, a spot channel.</para>
        /// <para>A name is written as a Pascal string of at most 255 bytes, a character outside printable ASCII
        /// as <c>?</c>: the specification gives the strings no encoding beyond Mac Roman, which other writers
        /// write bytes into unconverted.</para>
        /// </remarks>
        internal static byte[] EncodePsd(int width, int height,
            System.Collections.Generic.IReadOnlyList<(string Name, byte[] Plane, SKColor? Display)> inks, int dpi)
        {
            if (inks == null || inks.Count < 4) throw new ArgumentException("the four process inks first", nameof(inks));
            if (width < 1 || height < 1 || width > MaxPsdSide || height > MaxPsdSide)
                throw new Core.PdfException($"A PSD holds an image of at most {MaxPsdSide} pixels a side; this one is {width} x {height}.");
            if (inks.Count > MaxPsdChannels)
                throw new Core.PdfException($"A PSD holds at most {MaxPsdChannels} channels; these separations have {inks.Count}.");
            if (dpi > MaxPsdDpi)
                throw new Core.PdfException($"A PSD records a resolution of at most {MaxPsdDpi} dpi; these separations are at {dpi}.");
            long plane = (long)width * height;
            foreach (var ink in inks)
                if (ink.Plane == null || ink.Plane.Length != plane) throw new ArgumentException($"ink {ink.Name}: plane is not {width}x{height}", nameof(inks));
            int spots = inks.Count - 4;

            using var data = new MemoryStream();
            void U16(int v) { data.WriteByte((byte)(v >> 8)); data.WriteByte((byte)v); }
            void U32(long v) { U16((int)(v >> 16) & 0xFFFF); U16((int)v & 0xFFFF); }
            void Bytes(byte[] b) => data.Write(b, 0, b.Length);

            // File header.
            Bytes(System.Text.Encoding.ASCII.GetBytes("8BPS"));
            U16(1);                     // version
            U32(0); U16(0);             // reserved
            U16(inks.Count);
            U32(height);
            U32(width);
            U16(8);                     // depth
            U16(4);                     // CMYK
            U32(0);                     // colour mode data

            // Image resources.
            using var resources = new MemoryStream();
            void Resource(int id, byte[] body)
            {
                resources.Write(System.Text.Encoding.ASCII.GetBytes("8BIM"), 0, 4);
                resources.WriteByte((byte)(id >> 8)); resources.WriteByte((byte)id);
                resources.WriteByte(0); resources.WriteByte(0);   // a null name, padded to even
                int n = body.Length;
                resources.WriteByte((byte)(n >> 24)); resources.WriteByte((byte)(n >> 16)); resources.WriteByte((byte)(n >> 8)); resources.WriteByte((byte)n);
                resources.Write(body, 0, n);
                if ((n & 1) != 0) resources.WriteByte(0);
            }
            static byte[] Be(params int[] shorts)
            {
                var b = new byte[shorts.Length * 2];
                for (int i = 0; i < shorts.Length; i++) { b[2 * i] = (byte)(shorts[i] >> 8); b[2 * i + 1] = (byte)shorts[i]; }
                return b;
            }

            // ResolutionInfo: horizontal and vertical as 16.16 fixed, each pixels per inch (1) in inches (1).
            int fixedDpi = checked(dpi * 65536);
            Resource(1005, Be(fixedDpi >> 16, fixedDpi & 0xFFFF, 1, 1, fixedDpi >> 16, fixedDpi & 0xFFFF, 1, 1));

            if (spots > 0)
            {
                using var names = new MemoryStream();
                using var display1007 = new MemoryStream();
                using var display1077 = new MemoryStream();
                display1077.Write(Be(0, 1), 0, 4);   // version 1
                for (int i = 4; i < inks.Count; i++)
                {
                    string name = inks[i].Name ?? "";
                    var ascii = new byte[Math.Min(255, name.Length)];
                    for (int c = 0; c < ascii.Length; c++) ascii[c] = name[c] >= 0x20 && name[c] < 0x7F ? (byte)name[c] : (byte)'?';
                    names.WriteByte((byte)ascii.Length);
                    names.Write(ascii, 0, ascii.Length);

                    // RGB, sixteen bits a component; black where nothing says.
                    SKColor shown = inks[i].Display ?? SKColors.Black;
                    byte[] colour = Be(0, shown.Red * 257, shown.Green * 257, shown.Blue * 257, 0, 0);   // space, four components, solidity
                    display1007.Write(colour, 0, colour.Length); display1007.WriteByte(2); display1007.WriteByte(0);
                    display1077.Write(colour, 0, colour.Length); display1077.WriteByte(2);
                }
                Resource(1006, names.ToArray());
                Resource(1007, display1007.ToArray());
                Resource(1077, display1077.ToArray());
            }
            U32(resources.Length);
            resources.Position = 0;
            resources.CopyTo(data);

            U32(0);                     // no layer and mask information

            // Image data: PackBits a row, the byte counts (rows × channels, two bytes each) first.
            U16(1);
            var rows = new byte[inks.Count * height][];
            var line = new byte[width];
            for (int c = 0; c < inks.Count; c++)
            {
                byte[] p = inks[c].Plane;
                for (int y = 0; y < height; y++)
                {
                    int o = y * width;
                    for (int x = 0; x < width; x++) line[x] = (byte)(255 - p[o + x]);
                    // PackBits is PDF's RunLength without its end-of-data byte (TIFF 6.0 §9).
                    byte[] packed = Core.IO.Filters.RunLengthEncodeFilter.Encode(line);
                    rows[c * height + y] = packed;
                    U16(packed.Length - 1);
                }
            }
            foreach (byte[] packed in rows) data.Write(packed, 0, packed.Length - 1);
            return data.ToArray();
        }

        /// <summary>
        /// One-bit images as one TIFF (TIFF 6.0 §3, bilevel), a page each: CCITT Group 4 (§11, Compression 4), ink
        /// black (PhotometricInterpretation 0, WhiteIsZero: a 1 bit is black), each page named for its colorant
        /// (PageName) and numbered (PageNumber). A page past the CCITT encoder's pixel budget is written in
        /// PackBits a row at a time instead.
        /// </summary>
        internal static byte[] EncodeBilevelTiff(System.Collections.Generic.IReadOnlyList<PdfBilevelImage> pages, int dpi)
        {
            if (pages == null || pages.Count == 0) throw new ArgumentException("at least one page", nameof(pages));
            if (pages.Count > ushort.MaxValue) throw new ArgumentException("too many pages", nameof(pages));

            using var data = new MemoryStream();
            var w = new BinaryWriter(data);
            w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42);
            long nextIfdLink = data.Position;
            w.Write(0u);

            for (int p = 0; p < pages.Count; p++)
            {
                var page = pages[p];
                int stride = (page.Width + 7) / 8;
                bool g4 = (long)page.Width * page.Height <= Core.IO.Filters.CcittFaxEncoder.MaxEncodePixels
                          && page.Width <= Core.IO.Filters.CcittFaxEncoder.MaxEncodeColumns
                          && page.Height <= Core.IO.Filters.CcittFaxEncoder.MaxEncodeRows;
                byte[] strip;
                if (g4)
                {
                    strip = Core.IO.Filters.CcittFaxEncoder.Encode(page.Bits, page.Width, page.Height, blackIs1: true);
                }
                else
                {
                    using var packed = new MemoryStream();
                    var row = new byte[stride];
                    for (int y = 0; y < page.Height; y++)
                    {
                        Buffer.BlockCopy(page.Bits, y * stride, row, 0, stride);
                        byte[] run = Core.IO.Filters.RunLengthEncodeFilter.Encode(row);
                        packed.Write(run, 0, run.Length - 1);   // PackBits is RunLength without its end-of-data byte
                    }
                    strip = packed.ToArray();
                }

                uint stripAt = Align(w, data); w.Write(strip);
                uint resAt = Align(w, data); w.Write((uint)dpi); w.Write(1u);
                byte[] software = System.Text.Encoding.ASCII.GetBytes("Tetitulz.Pdf\0");
                uint softwareAt = Align(w, data); w.Write(software);
                byte[] name = System.Text.Encoding.ASCII.GetBytes((page.Name ?? "") + "\0");
                uint nameAt = Align(w, data); w.Write(name);

                var tags = new System.Collections.Generic.List<(ushort Tag, ushort Type, uint Count, uint Value)>
                {
                    (254, 4, 1, pages.Count > 1 ? 2u : 0u),                         // NewSubfileType: a page of several
                    (256, 4, 1, (uint)page.Width),                                  // ImageWidth
                    (257, 4, 1, (uint)page.Height),                                 // ImageLength
                    (258, 3, 1, 1),                                                 // BitsPerSample
                    (259, 3, 1, g4 ? 4u : 32773u),                                  // Compression: Group 4, or PackBits
                    (262, 3, 1, 0),                                                 // Photometric: WhiteIsZero
                    (273, 4, 1, stripAt),                                           // StripOffsets
                    (277, 3, 1, 1),                                                 // SamplesPerPixel
                    (278, 4, 1, (uint)page.Height),                                 // RowsPerStrip
                    (279, 4, 1, (uint)strip.Length),                                // StripByteCounts
                    (282, 5, 1, resAt),                                             // XResolution
                    (283, 5, 1, resAt),                                             // YResolution
                    (285, 2, (uint)name.Length, name.Length <= 4 ? Pack(name) : nameAt), // PageName
                    (296, 3, 1, 2),                                                 // ResolutionUnit: inch
                    (297, 3, 2, (uint)p | ((uint)pages.Count << 16)),               // PageNumber: this, of
                    (305, 2, (uint)software.Length, softwareAt),                    // Software
                };
                if (g4) tags.Add((293, 4, 1, 0));                                   // T6Options: none
                tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));

                uint ifdAt = Align(w, data);
                w.Write((ushort)tags.Count);
                foreach (var t in tags) { w.Write(t.Tag); w.Write(t.Type); w.Write(t.Count); w.Write(t.Value); }
                long link = data.Position;
                w.Write(0u);   // the next page's IFD, patched when it is written
                w.Flush();
                data.Position = nextIfdLink;
                w.Write(ifdAt);
                w.Flush();
                data.Position = data.Length;
                nextIfdLink = link;
            }
            return data.ToArray();
        }

        /// <summary>Up to four bytes as a tag's value field, first byte lowest (little-endian).</summary>
        private static uint Pack(byte[] bytes)
        {
            uint v = 0;
            for (int i = 0; i < bytes.Length; i++) v |= (uint)bytes[i] << (8 * i);
            return v;
        }

        private static uint Align(BinaryWriter w, MemoryStream data)
        {
            w.Flush();
            if ((data.Position & 1) != 0) w.Write((byte)0);
            w.Flush();
            return checked((uint)data.Position);
        }
    }
}
