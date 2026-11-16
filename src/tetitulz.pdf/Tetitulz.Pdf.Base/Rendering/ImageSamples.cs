// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Two answers about an image's raw samples that every reader of one must give alike: how many
    /// components a sample carries, and which pixels a colour-key <c>/Mask</c> removes (§8.9.6.4).
    /// </summary>
    /// <remarks>
    /// <para><b>A leaf, because its callers sit on both sides of the renderer.</b> Both answers
    /// were static methods on <c>ContentStreamInterpreter</c>, and the SVG writer,
    /// <see cref="Images.ImageSampleLayout"/> and — once it needed the colour-key rule to keep an
    /// image's transparency through a conversion — <c>Operations.Metadata.PdfColorSpaceConverter</c>
    /// reached sideways into it for them. CLAUDE.md: "Share downward into a leaf, never sideways";
    /// the interpreter has a static initializer. No static fields here, so no type initializer.</para>
    /// </remarks>
    internal static class ImageSamples
    {
        /// <summary>
        /// Samples per pixel for an image — <b>the single arity answer</b>, shared by the raster
        /// path, the SVG writer and <see cref="Images.ImageSampleLayout"/> (which needs it to tell a
        /// four-channel image apart from CMYK).
        ///
        /// <para>This is the arity of the space the SAMPLES are in, so <c>/Indexed</c> is 1 (a
        /// palette index) and <c>/Separation</c> is 1 (a tint); <c>/DeviceN</c> and
        /// <c>/ICCBased</c> carry their count in the array itself. Falls back to RGB when the
        /// entry is absent or its family says nothing.</para>
        /// </summary>
        /// <remarks>
        /// <b>The <c>/ImageMask</c> rule lives here, not in one of two near-identical copies.</b>
        /// §8.9.6.2: a stencil mask is 1-bit and single-component, and §8.9.5 Table 89 forbids it a
        /// <c>/ColorSpace</c> at all — so it is never the RGB fallback's 3 components, which would make
        /// a padded <c>/ImageMask</c> undecodable: at 3 components the 1-bpc branch could not fire,
        /// leaving only a fallback that demanded the buffer length match the packed
        /// stride EXACTLY, which trailing padding (routinely produced by
        /// <c>/EncodedByteAlign</c>) breaks.
        /// </remarks>
        internal static int ComponentCount(PdfDictionary d, PdfDocument doc)
        {
            if (ImageStencil.IsStencilMask(d, doc))
                return PdfColorSpaceNames.GrayComponents;

            return PdfColor.ResolveSpace(d[PdfName.ColorSpace] ?? d[PdfName.CS], doc).Components;
        }

        /// <summary>
        /// The first <paramref name="count"/> <c>[min max]</c> pairs of a colour-key <c>/Mask</c>
        /// array, each ordered low to high; false when the array holds fewer pairs than that.
        /// </summary>
        /// <remarks>
        /// <para><b>Every element is resolved</b> (§7.3.10) — for the raw-sample rule and both
        /// renderers' decoded-pixel fallbacks alike: cast straight to a number,
        /// <c>/Mask [0 9 0 R …]</c> would read the reference as 0 and the key match a different range
        /// from the one the file wrote.</para>
        ///
        /// <para>An element that is not a number reads as 0 and the pair is then ordered: the
        /// answer all three already gave, kept so that a malformed key masks the same pixels it
        /// did.</para>
        /// </remarks>
        internal static bool TryReadColorKeyRanges(
            PdfArray maskArr, int count, PdfDocument doc, out int[] lo, out int[] hi)
        {
            lo = hi = null;
            if (maskArr == null || count <= 0 || maskArr.Count / 2 < count) return false;

            lo = new int[count];
            hi = new int[count];
            for (int i = 0; i < count; i++)
            {
                lo[i] = PdfResolve.Number(doc, maskArr[i * 2]) is PdfNumber n1 ? n1.IntValue : 0;
                hi[i] = PdfResolve.Number(doc, maskArr[i * 2 + 1]) is PdfNumber n2 ? n2.IntValue : 0;
                if (lo[i] > hi[i]) (lo[i], hi[i]) = (hi[i], lo[i]);
            }
            return true;
        }

        /// <summary>
        /// The per-pixel colour-key decision from RAW samples (§8.9.6.4) — <b>the single
        /// implementation</b>, so the raster and SVG paths, and a colour conversion that has to
        /// carry the key across, cannot disagree about which pixels a <c>/Mask</c> array removes.
        /// </summary>
        /// <returns>
        /// One flag per pixel in row-major order, or <c>null</c> when the key cannot be evaluated
        /// from the stream: an unsupported bit depth, a pair count that disagrees with the
        /// component count, or a buffer that is still compressed.
        /// </returns>
        /// <remarks>
        /// The ranges are raw integer sample values in the image's own colour space, one
        /// <c>[min max]</c> pair per component, and a pixel is masked when <b>every</b> component
        /// falls inside its range. Comparing decoded sRGB instead is wrong for every bit depth but 8, for Indexed (where the ranges are palette indices,
        /// not colours), for CMYK and for Separation; a 2-component array matched nothing at all.
        /// </remarks>
        internal static bool[] TryComputeColorKeyMask(byte[] raw, int w, int h,
                                                      PdfArray maskArr, PdfDictionary imageDict,
                                                      PdfDocument doc)
        {
            if (raw == null || w <= 0 || h <= 0 || maskArr == null || maskArr.Count < 2) return null;

            int bpc = PdfResolve.Int(doc, imageDict, PdfName.BitsPerComponent, PdfName.BPC, 8);
            if (bpc != 1 && bpc != 2 && bpc != 4 && bpc != 8 && bpc != 16) return null;

            int comps = ComponentCount(imageDict, doc);
            int pairs = maskArr.Count / 2;
            if (pairs != comps)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    $"/Mask colour-key array has {pairs} range(s) for a {comps}-component image; " +
                    "masking skipped (§8.9.6.4 requires one pair per component).");
                return null;
            }

            TryReadColorKeyRanges(maskArr, pairs, doc, out int[] lo, out int[] hi);

            // Rows are byte-aligned (§8.9.5.1).
            long rowBits  = (long)w * comps * bpc;
            int  rowBytes = (int)((rowBits + 7) / 8);

            // `raw` is the stream AFTER the filter chain — but DCTDecode, JPXDecode and any
            // unrecognised filter pass their bytes through, so for those it is still COMPRESSED,
            // and indexing it as samples reads JPEG entropy data as pixel values. The size check is
            // the discriminator: a raw-sample buffer is at least rowBytes * h (a producer may pad,
            // never truncate), and a compressed one essentially never is.
            if ((long)rowBytes * h > raw.LongLength) return null;

            var masked = new bool[(long)w * h <= int.MaxValue ? w * h : 0];
            if (masked.Length == 0) return null;

            for (int y = 0; y < h; y++)
            {
                int rowOff = y * rowBytes;
                int bitPos = 0;
                for (int x = 0; x < w; x++)
                {
                    bool all = true;
                    for (int c = 0; c < comps; c++)
                    {
                        int v;
                        if (bpc == 16)
                        {
                            int o = rowOff + (bitPos >> 3);
                            v = (raw[o] << 8) | raw[o + 1];
                        }
                        else
                        {
                            int shift = 8 - bpc - (bitPos & 7);
                            v = (raw[rowOff + (bitPos >> 3)] >> shift) & ((1 << bpc) - 1);
                        }
                        bitPos += bpc;
                        if (v < lo[c] || v > hi[c]) all = false;
                    }
                    masked[y * w + x] = all;
                }
            }
            return masked;
        }
    }
}
