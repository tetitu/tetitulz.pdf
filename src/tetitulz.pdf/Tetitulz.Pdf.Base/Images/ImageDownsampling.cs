// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// <b>The one rule for how far below its native size an image may be decoded</b> when
    /// <c>PdfRenderOptions.DownsampleImagesToTarget</c> is on and the image is drawn into a device
    /// extent far smaller than it is stored: the headroom kept above the target, the source size
    /// below which nothing is reduced, and — for a codec that reduces by halving, as JPEG 2000 does
    /// by resolution level — how many halvings the target allows. Skia's codec decode, the raw
    /// sample decimation and the JPEG 2000 decode all ask here, so a change to the policy moves the
    /// three together.
    /// </summary>
    /// <remarks>
    /// <para>The target is multiplied by <see cref="Headroom"/> before a size is chosen, so the
    /// decoded image stays comfortably above the destination and the final resample still has
    /// pixels to work with. Decoding exactly at the destination size and letting a filter run over
    /// it produces visible softness on text-bearing scans.</para>
    /// <para>Nothing is reduced below <see cref="MinDimension"/>: past that the saving is
    /// irrelevant and the risk of a visibly degraded thumbnail is not.</para>
    /// </remarks>
    internal static class ImageDownsampling
    {
        /// <summary>
        /// How much larger than the device target a downsampled decode is kept. 2x leaves the
        /// final resample a full extra octave to work from.
        /// </summary>
        internal const float Headroom = 2f;

        /// <summary>Sources at or below this are never reduced — the saving would not be worth it.</summary>
        internal const int MinDimension = 256;

        /// <summary>The extent a reduced decode must keep for a device target of <paramref name="target"/> pixels: the target with its headroom, never below <see cref="MinDimension"/>.</summary>
        internal static float Wanted(int target) => Math.Max(target * Headroom, MinDimension);

        /// <summary>Whether a source of this size is a candidate for reduction at all: both sides above <see cref="MinDimension"/>.</summary>
        internal static bool IsReducible(long nativeWidth, long nativeHeight) =>
            nativeWidth > MinDimension && nativeHeight > MinDimension;

        /// <summary>
        /// How many times a <paramref name="nativeWidth"/> × <paramref name="nativeHeight"/> source
        /// may be halved — each halving ⌈· / 2⌉ a side, as a JPEG 2000 resolution level is (T.800
        /// B-14) — and still keep <see cref="Wanted"/> of the target on both sides. 0 when there is
        /// no target, the source is not <see cref="IsReducible"/>, or one halving already falls
        /// short; never more than 32.
        /// </summary>
        internal static int HalvingsFor(long nativeWidth, long nativeHeight, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0) return 0;
            if (!IsReducible(nativeWidth, nativeHeight)) return 0;
            float wantedWidth = Wanted(targetWidth), wantedHeight = Wanted(targetHeight);
            int halvings = 0;
            long w = nativeWidth, h = nativeHeight;
            while (halvings < 32)
            {
                long hw = (w + 1) >> 1, hh = (h + 1) >> 1;
                if (hw < wantedWidth || hh < wantedHeight) break;
                w = hw;
                h = hh;
                halvings++;
            }
            return halvings;
        }

        /// <summary>
        /// The size a codec is asked to decode at for a target: the smallest reduction the codec
        /// supports that still keeps <see cref="Wanted"/> of the target's width and covers the target,
        /// or an empty size when no reduction is wanted or available. The renderer's encoded-image
        /// decode and the optimizer's JPEG decode (through <see cref="JpegSamples"/>) both ask here.
        /// </summary>
        /// <remarks>
        /// <para><see cref="SKCodec.GetScaledDimensions"/> rounds to what the codec can deliver: for
        /// JPEG the DCT scales of 1/8 to 7/8; a codec with no reduced modes returns the native size.</para>
        /// <para><b>It rounds to the nearest scale, not up</b>, so asking for the headroom's scale can
        /// return less than the headroom: 256 of a 1600-pixel frame is 0.16, the nearest eighth is
        /// 1/8, and the decode came back at 200. It covered a 100-pixel target and kept none of the
        /// headroom <see cref="Headroom"/> exists for. The
        /// scale asked for is raised in sixteenths until the answer keeps it, or reaches the native size.</para>
        /// </remarks>
        internal static SKSizeI CodecSize(SKCodec codec, int nativeWidth, int nativeHeight,
            int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0) return default;
            if (!IsReducible(nativeWidth, nativeHeight)) return default;

            float wanted = Wanted(targetWidth);
            if (wanted >= nativeWidth) return default;

            float scale = wanted / nativeWidth;
            SKSizeI supported = codec.GetScaledDimensions(scale);
            for (int step = 0; step < 16 && supported.Width > 0 && supported.Width < wanted && scale < 1f; step++)
            {
                scale = Math.Min(1f, scale + 1f / 16);
                supported = codec.GetScaledDimensions(scale);
            }
            if (supported.Width <= 0 || supported.Height <= 0) return default;
            if (supported.Width >= nativeWidth) return default;   // no reduction available
            if (supported.Width < targetWidth || supported.Height < targetHeight) return default; // too coarse

            return supported;
        }

        /// <summary>
        /// The nearest-neighbour decimation of a raw sample buffer for a device target: every
        /// <paramref name="step"/>-th sample of every <paramref name="step"/>-th row, starting at the
        /// first, giving <paramref name="width"/> × <paramref name="height"/>. False — nothing to
        /// decimate — when there is no target, the source is not <see cref="IsReducible"/>, the step
        /// would be below 2, or the result would fall short of the target. The one rule the raster
        /// renderer's decimation of a decoded buffer and the decimating Flate decode both ask, so
        /// the two produce the same samples.
        /// </summary>
        internal static bool TryRawDecimation(int nativeWidth, int nativeHeight, int targetWidth, int targetHeight,
            out int step, out int width, out int height)
        {
            step = 0; width = nativeWidth; height = nativeHeight;
            if (targetWidth <= 0 || targetHeight <= 0) return false;
            if (!IsReducible(nativeWidth, nativeHeight)) return false;

            // Keep headroom above the destination.
            float wanted = Wanted(targetWidth);
            if (wanted >= nativeWidth) return false;
            int s = SaturatingCast.ToInt(nativeWidth / wanted);
            if (s < 2) return false;

            int nw = (nativeWidth + s - 1) / s;
            int nh = (nativeHeight + s - 1) / s;
            if (nw < 1 || nh < 1) return false;
            if (nw < targetWidth || nh < targetHeight) return false;   // too coarse to be worth the quality loss
            step = s; width = nw; height = nh;
            return true;
        }
    }
}
