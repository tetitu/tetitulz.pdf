// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// How an opacity channel embedded in a decoded sample buffer is stored. The values
    /// mirror the image dictionary's <c>/SMaskInData</c> entry (ISO 32000-1 Table 89).
    /// </summary>
    internal enum ImageAlphaMode
    {
        /// <summary><c>/SMaskInData 0</c> (also the default when the key is absent) — any
        /// opacity channel present in the encoded data is ignored.</summary>
        None = 0,
        /// <summary><c>/SMaskInData 1</c> — colour values are NOT premultiplied by alpha.</summary>
        Straight = 1,
        /// <summary><c>/SMaskInData 2</c> — colour values ARE premultiplied by alpha.</summary>
        Premultiplied = 2,
    }

    /// <summary>
    /// Describes how to read the interleaved 8-bit sample buffer produced by an image codec
    /// (an <see cref="Core.Plugins.IImageDecodePlugin"/>): how many of
    /// the channels are colour, what colour space they form, and whether a trailing channel is
    /// alpha.
    ///
    /// <para>This type exists because a bare component count is ambiguous. A four-channel
    /// JPEG 2000 image is CMYK in one file and RGB + opacity in the next, and two decode sites
    /// guessing differently would render the same image differently depending on which one
    /// handled it. Resolution is in one place, driven by evidence in this order:</para>
    /// <list type="number">
    ///   <item><c>/SMaskInData</c> — the PDF's own statement about the opacity channel.</item>
    ///   <item><c>/ColorSpace</c> — optional for <c>JPXDecode</c>, but when present it overrides
    ///         the colour space in the codestream (ISO 32000-1 §7.4.9).</item>
    ///   <item>The JP2 header (<c>colr</c> / <c>cdef</c> boxes), when the bytes are a JP2
    ///         container — see <see cref="JpxHeader.TryReadColourComponents"/>.</item>
    ///   <item>The raw channel count, as a last resort.</item>
    /// </list>
    /// </summary>
    internal readonly struct ImageSampleLayout
    {
        private ImageSampleLayout(int colorComponents, int totalComponents, ImageAlphaMode alpha,
                                  Rendering.IccColorTransform.IccImageConverter cmykIcc = null)
        {
            ColorComponents = colorComponents;
            TotalComponents = totalComponents;
            Alpha           = alpha;
            _cmykIcc        = cmykIcc;
        }

        /// <summary>
        /// The image's own ICC profile for the CMYK case, or null. Carried on the layout because
        /// this is the one place that knows the samples are still four components: after
        /// <see cref="BuildBitmap"/> they are sRGB and the profile's inputs no longer exist.
        /// </summary>
        private readonly Rendering.IccColorTransform.IccImageConverter _cmykIcc;

        /// <summary>Number of leading channels that carry colour: 1 (grey), 3 (RGB) or 4 (CMYK).</summary>
        public int ColorComponents { get; }

        /// <summary>Channels per pixel in the sample buffer, i.e. the stride. Channels beyond
        /// the colour components and the alpha channel (if any) are ignored.</summary>
        public int TotalComponents { get; }

        /// <summary>Whether the channel at index <see cref="ColorComponents"/> is alpha, and how
        /// it relates to the colour values.</summary>
        public ImageAlphaMode Alpha { get; }

        public bool HasAlpha => Alpha != ImageAlphaMode.None;

        /// <summary>True when the colour channels are CMYK, which the <c>/Decode</c> array must
        /// not be applied to after conversion (its eight entries describe the CMYK samples, not
        /// the RGB result).</summary>
        public bool IsCmyk => ColorComponents == PdfColorSpaceNames.CmykComponents;

        /// <summary>False when the buffer cannot be turned into a bitmap — the caller falls
        /// back to its next decoder.</summary>
        public bool IsValid =>
            TotalComponents > 0
            && (ColorComponents == PdfColorSpaceNames.GrayComponents
                || ColorComponents == PdfColorSpaceNames.RgbComponents
                || ColorComponents == PdfColorSpaceNames.CmykComponents)
            && ColorComponents + (HasAlpha ? 1 : 0) <= TotalComponents;

        /// <summary>
        /// A layout already decided: <paramref name="colorComponents"/> colour channels of
        /// <paramref name="totalComponents"/> per pixel, with the channel after the colour ones
        /// read as <paramref name="alpha"/> says. For the in-house JPEG 2000 decoder, whose channel
        /// plan (ISO 32000-2 §7.4.9, <c>Core.Imaging.Jpx.Jp2ColourDescription</c>) has already
        /// answered the questions <see cref="Resolve"/> guesses at from a plugin's bare sample
        /// buffer — so both decoders store straight and premultiplied alpha through the one
        /// <see cref="BuildBitmap"/>.
        /// </summary>
        /// <param name="colorComponents">1 (grey), 3 (RGB) or 4 (CMYK).</param>
        /// <param name="totalComponents">Channels per pixel in the buffer.</param>
        /// <param name="alpha">How the channel after the colour ones relates to them.</param>
        /// <param name="cmykIcc">A per-image converter for four-component samples, or null for <see cref="PdfColor.CmykToRgb"/>.</param>
        internal static ImageSampleLayout ForDecodedChannels(int colorComponents, int totalComponents, ImageAlphaMode alpha,
            Rendering.IccColorTransform.IccImageConverter cmykIcc = null) =>
            new ImageSampleLayout(colorComponents, totalComponents, alpha, cmykIcc);

        /// <summary>
        /// Reads <c>/SMaskInData</c>. Absent, 0, or any value outside 0–2 means
        /// <see cref="ImageAlphaMode.None"/> — an out-of-range value is a malformed file, and
        /// falling back to the spec default is safer than inventing transparency.
        /// </summary>
        /// <remarks>
        /// <c>doc</c> is a parameter because <see cref="ImageAlphaMode.None"/> is <i>also</i> what
        /// this returns for an ABSENT entry, so an unresolved one is indistinguishable from a file
        /// that never asked for alpha — and a JPX codestream's opacity channel is then dropped and
        /// the image renders opaque. <c>Resolve</c>, the only caller, has had a document all along
        /// and its own parameter doc says it is there to resolve an indirect <c>/ColorSpace</c>.
        /// </remarks>
        internal static ImageAlphaMode ReadSMaskInData(PdfDictionary imageDict, PdfDocument doc)
        {
            if (PdfResolve.Value(doc, imageDict?[PdfName.SMaskInData]) is not PdfNumber n)
                return ImageAlphaMode.None;
            return n.LongValue switch
            {
                1 => ImageAlphaMode.Straight,
                2 => ImageAlphaMode.Premultiplied,
                _ => ImageAlphaMode.None,
            };
        }

        /// <summary>
        /// Works out how to read <paramref name="decodedComponents"/> interleaved channels.
        /// </summary>
        /// <param name="imageDict">The image XObject dictionary (may be null).</param>
        /// <param name="doc">Document used to resolve an indirect <c>/ColorSpace</c>.</param>
        /// <param name="decodedComponents">Channels per pixel the codec reported.</param>
        /// <param name="jpxData">
        /// The encoded JPEG 2000 bytes when the image is <c>JPXDecode</c>, so the JP2 header can
        /// be consulted; null for every other filter.
        /// </param>
        /// <param name="graphicsStateIntent">
        /// The rendering intent in force at the <c>Do</c>, which this image's own <c>/Intent</c>
        /// overrides. Threaded from the renderers through the decoders so that a JPX or
        /// plugin-decoded CMYK image picks the same <c>A2B</c> table as a Flate-decoded one beside
        /// it — honouring <c>/RI</c> on one decode path and not the other would put two colours from
        /// one profile into one page.
        /// </param>
        internal static ImageSampleLayout Resolve(
            PdfDictionary imageDict, PdfDocument doc, int decodedComponents, byte[] jpxData,
            Rendering.IccRenderingIntent graphicsStateIntent
                = Rendering.IccRenderingIntents.Default)
        {
            int total = decodedComponents;
            if (total <= 0) return default;

            ImageAlphaMode declaredAlpha = ReadSMaskInData(imageDict, doc);

            // /ColorSpace is optional on a JPXDecode image but authoritative when present.
            int colorComponents = 0;
            if (imageDict != null && (imageDict[PdfName.ColorSpace] ?? imageDict[PdfName.CS]) != null)
                colorComponents = ImageSamples.ComponentCount(imageDict, doc);

            // Nothing declared — ask the codestream's own header before guessing.
            if (colorComponents <= 0 && jpxData != null
                && JpxHeader.TryReadColourComponents(jpxData, out int jp2Colour))
                colorComponents = jp2Colour;

            if (colorComponents <= 0 || colorComponents > total) colorComponents = total;

            // Two channels is grey + one extra; there is no two-component colour space.
            if (colorComponents == 2) colorComponents = PdfColorSpaceNames.GrayComponents;

            ImageAlphaMode alpha = ImageAlphaMode.None;
            if (declaredAlpha != ImageAlphaMode.None)
            {
                // The PDF says an opacity channel is in the data, so split it off the end even
                // when nothing told us the colour arity (4 channels ⇒ RGB + alpha).
                if (colorComponents >= total && total >= 2)
                    colorComponents = total >= PdfColorSpaceNames.CmykComponents
                        ? PdfColorSpaceNames.RgbComponents
                        : total - 1;
                if (colorComponents < total) alpha = declaredAlpha;
            }
            // Otherwise /SMaskInData is absent or 0, which the spec defines as "ignore any
            // opacity channel in the encoded data" — so a trailing channel is dropped, not
            // read as transparency the document never asked for. Knowing the colour arity
            // still matters: it is what keeps an RGB + alpha JP2 from being read as CMYK.

            // Resolved here rather than in BuildBitmap so it costs one profile lookup per image
            // rather than one per pixel, and only for the arity that can use it.
            var cmykIcc = colorComponents == PdfColorSpaceNames.CmykComponents
                ? Rendering.IccColorTransform.IccImageConverter.TryCreate(doc, imageDict, 4,
                                                                          graphicsStateIntent)
                : null;

            return new ImageSampleLayout(colorComponents, total, alpha, cmykIcc);
        }

        /// <summary>
        /// Builds an <see cref="SKBitmap"/> from interleaved 8-bit samples. Returns null when the
        /// layout is unusable or the geometry exceeds the render size limits — codec output is
        /// untrusted as far as allocation is concerned.
        /// </summary>
        /// <param name="pixels">Interleaved samples, <see cref="TotalComponents"/> per pixel.</param>
        /// <param name="width">Raster width the codec reported, in pixels.</param>
        /// <param name="height">Raster height the codec reported, in pixels.</param>
        /// <param name="alphaTypeWithAlpha">
        /// Alpha type for the result when the layout has alpha: <see cref="SKAlphaType.Premul"/>
        /// for the raster renderer (Skia ignores stored alpha on an Opaque bitmap), or
        /// <see cref="SKAlphaType.Unpremul"/> for the SVG renderer, which PNG-encodes the result.
        /// The samples are stored in whichever form the chosen alpha type <i>declares</i> —
        /// premultiplied for Premul, straight for Unpremul — which is what
        /// <see cref="Premultiply"/> exists for; see the remarks on this method.
        /// </param>
        /// <remarks>
        /// <para>Samples are written straight into the bitmap's own buffer. An <see cref="SKColor"/>
        /// array assigned to <c>SKBitmap.Pixels</c> would allocate a second full-size buffer and copy
        /// it — 6.6 ms against 2.7 ms for a 1024x768 RGB image, plus 3 MB of garbage per image.</para>
        /// <para><b>The output is byte-for-byte what that setter produced</b>, which is why the
        /// two conversions it was doing are reproduced here rather than simplified away. The
        /// setter premultiplied when — and only when — the surface was
        /// <see cref="SKAlphaType.Premul"/>, so <see cref="Premultiply"/> matches Skia's
        /// <c>SkMulDiv255Round</c> exactly; and it collapsed a fully transparent pixel to
        /// transparent <i>black</i>, discarding its colour channels. That second one looks like a
        /// quirk worth dropping and is not: an invisible pixel renders the same either way, but
        /// keeping the decoded colour turns a uniform transparent region into noise, and the SVG
        /// renderer PNG-encodes this bitmap. Measured over a 1024x768 image with a random opacity
        /// channel, those were the only 3 061 pixels of 786 432 where the two implementations
        /// could differ at all.</para>
        /// </remarks>
        internal SKBitmap BuildBitmap(byte[] pixels, int width, int height, SKAlphaType alphaTypeWithAlpha)
        {
            if (pixels == null || !IsValid) return null;
            if (width <= 0 || height <= 0) return null;
            if (width > PdfLimits.MaxImageDimension || height > PdfLimits.MaxImageDimension) return null;
            if ((long)width * height > PdfLimits.MaxImagePixelArea) return null;
            if ((long)pixels.Length < (long)width * height * TotalComponents) return null;

            var bmp = new SKBitmap(width, height, SKColorType.Rgba8888,
                HasAlpha ? alphaTypeWithAlpha : SKAlphaType.Opaque);

            var dst = bmp.GetPixelSpan();
            int stride = bmp.RowBytes;
            if (dst.Length < (long)height * stride || stride < width * 4)
            {
                bmp.Dispose();
                return null;
            }

            // Straight or premultiplied is decided once per image, not once per pixel.
            bool premultiplyOnStore = HasAlpha && bmp.AlphaType == SKAlphaType.Premul;

            int o = 0;
            for (int y = 0; y < height; y++)
            {
                int d = y * stride;
                for (int x = 0; x < width; x++, o += TotalComponents, d += 4)
                {
                    byte a = HasAlpha ? pixels[o + ColorComponents] : (byte)255;

                    byte r, g, b;
                    switch (ColorComponents)
                    {
                        case PdfColorSpaceNames.GrayComponents:
                            r = g = b = pixels[o];
                            break;
                        case PdfColorSpaceNames.RgbComponents:
                            r = pixels[o]; g = pixels[o + 1]; b = pixels[o + 2];
                            break;
                        default:
                        {
                            // PdfColor is the single home for colour conversion (see CLAUDE.md).
                            // An evaluable 4-component ICC profile takes precedence — this is the
                            // third of the CMYK decode sites, and all of them consult the same
                            // per-image converter so a JPEG 2000 CMYK image is colour-managed on
                            // exactly the terms an ordinary one is.
                            float cc = pixels[o] / 255f,     mm = pixels[o + 1] / 255f;
                            float yy = pixels[o + 2] / 255f, kk = pixels[o + 3] / 255f;
                            SKColor c = _cmykIcc != null
                                     && _cmykIcc.TryConvert(cc, mm, yy, kk, out SKColor managed)
                                ? managed
                                : PdfColor.CmykToRgb(cc, mm, yy, kk);
                            r = c.Red; g = c.Green; b = c.Blue;
                            break;
                        }
                    }

                    if (Alpha == ImageAlphaMode.Premultiplied && a < 255)
                    {
                        // The samples arrived premultiplied; recover straight colour first, so the
                        // store below is the only place alpha is ever applied.
                        r = Unpremultiply(r, a); g = Unpremultiply(g, a); b = Unpremultiply(b, a);
                    }

                    if (a == 0)
                    {
                        // Transparent black, as the SKBitmap.Pixels setter stored it — see the
                        // remarks: this keeps a transparent region uniform for the PNG encoder.
                        r = g = b = 0;
                    }
                    else if (premultiplyOnStore && a < 255)
                    {
                        r = Premultiply(r, a); g = Premultiply(g, a); b = Premultiply(b, a);
                    }

                    // Rgba8888 is R, G, B, A in memory order.
                    dst[d] = r; dst[d + 1] = g; dst[d + 2] = b; dst[d + 3] = a;
                }
            }

            bmp.NotifyPixelsChanged();
            return bmp;
        }

        private static byte Unpremultiply(byte value, byte alpha)
        {
            if (alpha == 0) return 0;
            int v = (value * 255 + alpha / 2) / alpha;
            return v > 255 ? (byte)255 : (byte)v;
        }

        /// <summary>
        /// Divides a premultiplied sample buffer (<c>/SMaskInData 2</c>) by its opacity, in place:
        /// <paramref name="components"/> interleaved 8-bit samples per pixel, one opacity byte per
        /// pixel — the same rule <see cref="BuildBitmap"/> applies to a pixel, for a consumer that
        /// wants straight samples rather than a bitmap (the raw sample path, the colour converter).
        /// </summary>
        internal static void UnpremultiplySamples(byte[] samples, int components, byte[] opacity)
        {
            if (samples == null || opacity == null || components <= 0) return;
            long area = opacity.Length < samples.Length / components ? opacity.Length : samples.Length / components;
            long s = 0;
            for (long i = 0; i < area; i++)
            {
                byte a = opacity[i];
                if (a == 255) { s += components; continue; }
                for (int c = 0; c < components; c++, s++) samples[s] = Unpremultiply(samples[s], a);
            }
        }

        /// <summary>
        /// Skia's <c>SkMulDiv255Round</c> — round(value × alpha / 255) without a divide. Matching
        /// it exactly is what keeps the Premul surface byte-identical to Skia's own
        /// premultiplication.
        /// </summary>
        private static byte Premultiply(byte value, byte alpha) => StraightPixelLayout.Premultiply(value, alpha);
    }
}
