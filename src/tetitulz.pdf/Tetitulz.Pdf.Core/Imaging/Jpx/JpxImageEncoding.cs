// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using System;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>What <see cref="JpxImageEncoding.Encode"/> is asked for; every default is a lossless JP2 file.</summary>
    public sealed class JpxEncodingOptions
    {
        /// <summary>
        /// True for the reversible path (the 5-3 wavelet, no quantisation): the samples come back
        /// exactly unless <see cref="TargetBytes"/> cuts the codestream short. False for the
        /// irreversible path (the 9-7 wavelet, quantised by <see cref="Quality"/>).
        /// </summary>
        public bool Lossless { get; set; } = true;

        /// <summary>
        /// The lossy path's quality, 1 to 100, in the spirit of a JPEG quality: 100 is nearly
        /// lossless (and about the lossless size), 75 is a good photograph, 50 is visibly
        /// compressed. It sets the quantisation step (<see cref="JpxImageEncoding.StepForQuality"/>),
        /// scaled to the sample depth. Ignored when <see cref="Lossless"/>.
        /// </summary>
        public int Quality { get; set; } = 90;

        /// <summary>
        /// A size the whole codestream is fitted to, in bytes, or 0 for none: coding passes are
        /// dropped, least distortion removed per byte first, until it fits. Applies to both paths;
        /// a lossless codestream under a target is lossless no longer. The headers are never
        /// dropped, so a target below them is met as nearly as empty packets allow.
        /// </summary>
        public long TargetBytes { get; set; }

        /// <summary>Quality layers, 1 to 65535: each adds the passes that most improve the picture per byte, so a reader may stop after any of them.</summary>
        public int Layers { get; set; } = 1;

        /// <summary>Wavelet decomposition levels, 0 to 32; fewer are used when the image is too small for them.</summary>
        public int DecompositionLevels { get; set; } = 5;

        /// <summary>
        /// True to wrap the codestream in the JP2 file format (ISO/IEC 15444-1 Annex I) with a
        /// colour specification naming greyscale, sRGB or CMYK by the component count, which a PDF
        /// consumer reads when the image dictionary names no <c>/ColorSpace</c>. False for the raw
        /// codestream alone, which needs the dictionary to say.
        /// </summary>
        public bool Jp2Container { get; set; } = true;

        /// <summary>
        /// True when the last component is the image's opacity — two components for grey with
        /// alpha, four for RGB with alpha, five for CMYK with alpha. The JP2 file's Channel
        /// Definition box declares it, so a <c>/JPXDecode</c> image with <c>/SMaskInData 1</c> shows
        /// it; a raw codestream carries no such declaration, so this needs <see cref="Jp2Container"/>.
        /// </summary>
        public bool OpacityChannel { get; set; }

        /// <summary>
        /// True for HTJ2K (ITU-T T.814 | ISO/IEC 15444-15): every code-block coded with the
        /// high-throughput block coder, which encodes and decodes several times faster than Part 1's
        /// arithmetic coder at a slightly larger size, in a JPH file (brand <c>jph </c>) when
        /// <see cref="Jp2Container"/> is set. Default false.
        /// </summary>
        /// <remarks>
        /// <para><b>Not for a PDF a reader must open.</b> ISO 32000-2's <c>/JPXDecode</c> is ISO/IEC 15444-2's
        /// baseline, which HTJ2K is not: this library decodes it, many readers do not, and PDF/A
        /// forbids it (<c>PdfAValidator</c> rule A29). It is for files this library reads back, or for
        /// a JPH file outside a PDF.</para>
        /// <para>Each code-block is one cleanup pass at a plane: <see cref="TargetBytes"/> picks every
        /// block's plane, and <see cref="Layers"/> decide when each block arrives — the blocks removing
        /// the most error per byte first — rather than re-coding a block in each, which other decoders do not
        /// decode. A CMYK image goes as a raw codestream (<see cref="Jp2Container"/> false), since a JPH
        /// file enumerates no CMYK.</para>
        /// </remarks>
        public bool HighThroughput { get; set; }

        /// <summary>
        /// The most threads an encode uses: 0 (the default) chooses — the processor count, at most 16,
        /// and no more than the image's size is worth — 1 keeps it on the calling thread, a larger number
        /// caps it there. The bytes written are the same whatever it is.
        /// </summary>
        public int MaxThreads { get; set; }
    }

    /// <summary>
    /// JPEG 2000 encoding for any consumer — the optimiser's image re-encoding, a raw-samples
    /// caller, the operations facade: interleaved unsigned samples in, a JP2 file or codestream
    /// out, fit for a <c>/JPXDecode</c> image XObject. The mirror of <see cref="JpxImageDecoding"/>.
    /// </summary>
    public static class JpxImageEncoding
    {
        /// <summary>The deepest samples the byte form encodes: two bytes each, big-endian, as a PDF stores 16-bit samples. <see cref="EncodePlanes"/> takes the codec's full depth.</summary>
        public const int MaxBitsPerComponent = 16;

        /// <summary>The deepest samples <see cref="EncodePlanes"/> encodes: T.800's 38 bits (Table A.11).</summary>
        public const int MaxPlaneBitsPerComponent = JpxEncoder.MaxPrecision;

        /// <summary>
        /// Encodes <paramref name="planes"/> — one array per component (1 grey, 3 RGB, 4 CMYK), each
        /// <c>width · height</c> samples in raster order, 0 to 2<sup>bits</sup> − 1 unsigned or
        /// −2<sup>bits − 1</sup> to 2<sup>bits − 1</sup> − 1 signed, at up to
        /// <see cref="MaxPlaneBitsPerComponent"/> bits — under <paramref name="options"/>, as
        /// <see cref="Encode"/> does for bytes. The door for samples deeper than a PDF's 16 bits.
        /// </summary>
        public static byte[] EncodePlanes(long[][] planes, int width, int height, int bitsPerComponent, bool isSigned, JpxEncodingOptions options)
        {
            if (planes == null) throw new ArgumentNullException(nameof(planes));
            options = options ?? new JpxEncodingOptions();
            int components = planes.Length;
            RequireComponentCount(components, options);
            if (bitsPerComponent < 1 || bitsPerComponent > MaxPlaneBitsPerComponent)
                throw new ArgumentOutOfRangeException(nameof(bitsPerComponent), bitsPerComponent, $"1 to {MaxPlaneBitsPerComponent} bits per component are encoded");
            RequireArea(width, height);
            return EncodeCore(planes, width, height, components, bitsPerComponent, isSigned, options);
        }

        /// <summary>
        /// The pixel area every image door shares, <see cref="PdfLimits.MaxImagePixelArea"/>, read
        /// at the call — before a sample is widened to a <c>long</c> and copied per band, which the
        /// facade did unguarded. Core's own check; the
        /// per-side ceiling an authored image gets stays Base's <c>AuthoredImageSize</c>.
        /// </summary>
        private static void RequireArea(int width, int height)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "the image must have at least one pixel");
            long area = (long)width * height;
            if (area > PdfLimits.MaxImagePixelArea)
                throw new ArgumentOutOfRangeException(nameof(width), area, $"the image is {width}x{height}, {area} pixels; PdfLimits.MaxImagePixelArea is {PdfLimits.MaxImagePixelArea}");
        }

        /// <summary>
        /// Encodes <paramref name="samples"/> — row-major, <paramref name="components"/> per pixel
        /// (1 grey, 3 RGB, 4 CMYK), each one byte up to 8 bits per component and two big-endian
        /// bytes above, unsigned, exactly <c>width · height · components</c> samples — under
        /// <paramref name="options"/> (null for the defaults: a lossless JP2 file).
        /// </summary>
        /// <exception cref="ArgumentException">The samples do not fit the size and depth given, or a sample exceeds the depth.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A size, depth, component count or option is outside what is encoded.</exception>
        public static byte[] Encode(byte[] samples, int width, int height, int components, int bitsPerComponent, JpxEncodingOptions options)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            options = options ?? new JpxEncodingOptions();
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "the image must have at least one pixel");
            RequireComponentCount(components, options);
            if (bitsPerComponent < 1 || bitsPerComponent > MaxBitsPerComponent)
                throw new ArgumentOutOfRangeException(nameof(bitsPerComponent), bitsPerComponent, $"1 to {MaxBitsPerComponent} bits per component are encoded in bytes; EncodePlanes takes up to {MaxPlaneBitsPerComponent}");

            RequireArea(width, height);
            int bytesPerSample = bitsPerComponent > 8 ? 2 : 1;
            long count = (long)width * height;
            long expected = count * components * bytesPerSample;
            if (samples.Length != expected)
                throw new ArgumentException($"{samples.Length} bytes were given for {width}x{height} pixels of {components} {bitsPerComponent}-bit components, which take {expected}", nameof(samples));

            int max = (1 << bitsPerComponent) - 1;
            var planes = new long[components][];
            for (int c = 0; c < components; c++) planes[c] = new long[count];
            for (long i = 0, at = 0; i < count; i++)
                for (int c = 0; c < components; c++)
                {
                    int v = bytesPerSample == 1 ? samples[at++] : (samples[at++] << 8) | samples[at++];
                    if (v > max) throw new ArgumentException($"sample {i} of component {c} is {v}, above the {bitsPerComponent}-bit maximum {max}", nameof(samples));
                    planes[c][i] = v;
                }
            return EncodeCore(planes, width, height, components, bitsPerComponent, false, options);
        }

        /// <summary>The component counts a PDF colour space names and a JP2 colour specification describes: 1, 3 or 4 colour channels, plus one for the opacity when declared.</summary>
        private static void RequireComponentCount(int components, JpxEncodingOptions options)
        {
            int colour = options.OpacityChannel ? components - 1 : components;
            if (colour == 1 || colour == 3 || colour == 4) return;
            throw new ArgumentOutOfRangeException(nameof(components), components, options.OpacityChannel
                ? "with an opacity channel, 2 (grey), 4 (RGB) or 5 (CMYK) components are encoded — a colour count a PDF colour space names, plus the opacity"
                : "1 (grey), 3 (RGB) or 4 (CMYK) components are encoded — the counts a PDF colour space names and a JP2 colour specification describes");
        }

        /// <summary>The one encode under the facade's options: the level cap, the target less the container, the depth-scaled step, then <see cref="JpxEncoder"/> and the JP2 wrapper.</summary>
        private static byte[] EncodeCore(long[][] planes, int width, int height, int components, int bitsPerComponent, bool isSigned, JpxEncodingOptions options)
        {
            if (options.OpacityChannel && !options.Jp2Container)
                throw new ArgumentException("an opacity channel is declared by the JP2 file's Channel Definition box; a raw codestream cannot say which channel it is", nameof(options));
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "the image must have at least one pixel");
            // Quality is read by the lossy path alone (its step, its layers' shares); under Lossless it
            // is ignored, and an ignored value is not checked.
            if (!options.Lossless && (options.Quality < 1 || options.Quality > 100)) throw new ArgumentOutOfRangeException(nameof(options), "the quality is 1 to 100");
            if (options.Layers < 1 || options.Layers > JpxEncoder.MaxLayers) throw new ArgumentOutOfRangeException(nameof(options), $"1 to {JpxEncoder.MaxLayers} layers");
            if (options.TargetBytes < 0) throw new ArgumentOutOfRangeException(nameof(options), "a size target is a number of bytes, or 0 for none");
            if (options.DecompositionLevels < 0 || options.DecompositionLevels > JpxWavelet.MaxLevels)
                throw new ArgumentOutOfRangeException(nameof(options), $"0 to {JpxWavelet.MaxLevels} decomposition levels");

            // Levels past the image's size only add empty resolution levels.
            int levels = options.DecompositionLevels;
            int smallest = Math.Min(width, height);
            int fit = 0;
            while ((smallest >> (fit + 1)) >= 1) fit++;
            if (levels > fit) levels = fit;

            // A size target is for the file: the container's boxes come off the codestream's share.
            long target = options.TargetBytes;
            if (target > 0 && options.Jp2Container)
                target = Math.Max(1, target - JpxEncoder.ContainerBytes(options.OpacityChannel ? components - 1 : components)
                                            - (options.OpacityChannel ? JpxEncoder.CdefBoxBytes(components) : 0));
            var encode = new JpxEncodeOptions
            {
                Levels = levels,
                Layers = options.Layers,
                TargetBytes = target,
                MultipleComponentTransform = (options.OpacityChannel ? components - 1 : components) == 3,
                // The step is the lossy path's alone; under Lossless the quality is ignored, unread.
                QuantizationStep = options.Lossless ? 1 : StepForQuality(options.Quality) * (1 << Math.Max(0, bitsPerComponent - 8)),
                HighThroughput = options.HighThroughput,
                MaxThreads = options.MaxThreads,
            };
            byte[] codestream = options.Lossless
                ? JpxEncoder.EncodeLossless(planes, width, height, bitsPerComponent, isSigned, encode)
                : JpxEncoder.EncodeLossy(planes, width, height, bitsPerComponent, isSigned, encode);
            return options.Jp2Container ? JpxEncoder.WrapAsJp2(codestream, options.OpacityChannel) : codestream;
        }

        /// <summary>
        /// The quantisation step (T.800 E-3, in the units of 8-bit samples' wavelet coefficients)
        /// a quality of 1 to 100 denotes: 1 at 100 — the integers' own resolution, so about the
        /// lossless size; a finer step spends more bytes than lossless for nothing a viewer sees —
        /// doubling every 16⅔ points down: about 1.5 at 90, 2.8 at 75, 8 at 50, 22 at 25, 61 at 1.
        /// </summary>
        public static double StepForQuality(int quality)
        {
            if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality), quality, "a quality is 1 to 100");
            return Math.Pow(2, (100 - quality) * 3 / 50.0);
        }
    }
}
