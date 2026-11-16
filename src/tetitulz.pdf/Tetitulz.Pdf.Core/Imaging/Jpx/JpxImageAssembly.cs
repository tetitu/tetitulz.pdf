// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The decoded image: one <c>int</c> plane per component over the image area — Xsiz − XOsiz by
    /// Ysiz − YOsiz (T.800 §B.2), what a PDF's <c>/Width</c> and <c>/Height</c> are — at the
    /// reference grid's resolution, or over the area at a reduced resolution (<see cref="Reduction"/>
    /// decomposition levels short of it: each side ⌈· / 2^r⌉ of the grid value, B-14's ceilings) —
    /// with each component's precision and sign, the placement of decoded tile-components into the
    /// planes, and the narrowing to the 8-bit interleaved samples the image pipeline consumes.
    /// </summary>
    /// <remarks>
    /// <para><b>A reduced image is placed by the same rules on the reduced area</b>: a tile-component
    /// reconstructed <c>r</c> levels short has the bounds B-14 gives it at that resolution, which
    /// are the full bounds' ceilings by 2^r, and the image area's are the same ceilings; so the
    /// placement arithmetic is the full one over the reduced area, and a sub-sampled component's
    /// replication maps the reduced grid to the reduced component the way it maps the full ones.</para>
    /// <para><b>Sub-sampled components are upsampled by replication</b> as they are placed: the
    /// sample at (cx, cy) of a component with separation (XRsiz, YRsiz) fills the grid positions
    /// [cx · XRsiz, (cx + 1) · XRsiz) × [cy · YRsiz, (cy + 1) · YRsiz) — the sample sits at the first
    /// of them (§B.2) — clipped to the image area; the component's first column and row also fill
    /// the grid positions before them when the image offset is not a multiple of the separation
    /// (B-1's ceiling starts the component past the offset). A grid position thus takes the sample
    /// at (⌊x / XRsiz⌋, ⌊y / YRsiz⌋), clamped into the component's extent.</para>
    /// <para><b>Narrowing to 8 bits</b> is the one rule every JPX image in a PDF renders by: a
    /// deeper sample is shifted right by precision − 8, a shallower one scaled by
    /// 255 / (2^precision − 1), and an 8-bit one taken as is, each clipped to 0–255. A signed
    /// component is first offset by 2^(precision − 1), the unconditional level shift, so
    /// −2^(precision − 1) is black and 2^(precision − 1) − 1 white.</para>
    /// </remarks>
    internal sealed class JpxDecodedImage
    {
        /// <summary>
        /// What one sample costs at the decode's peak, in bytes — the price
        /// <see cref="PdfLimits.MaxJpxWorkingSetBytes"/> is charged at. Measured on the shipped build
        /// (a 9 000 × 9 000 × 3 codestream: 4 086 MB private for 243 million samples): a
        /// tile-component's tier 1 values (an <c>int</c>, 4), the wavelet's <c>double</c> plane (8),
        /// the image's <c>int</c> plane (4) and the 8-bit output (1), all live together at the peak.
        /// A wide (<c>long</c>) plane costs 4 more; the constant is the common case's.
        /// </summary>
        internal const int WorkingBytesPerSample = 17;

        // The image area at the image's resolution: (XOsiz, YOsiz)-(Xsiz, Ysiz) at reduction 0,
        // each value ⌈· / 2^r⌉ at reduction r.
        private readonly JpxRect _area;

        private JpxDecodedImage(JpxRect area, int reduction, JpxComponentSize[] components)
        {
            _area = area;
            Reduction = reduction;
            Width = (int)area.Width;
            Height = (int)area.Height;
            Components = components;
            Samples = new int[components.Length][];
            WideSamples = new long[components.Length][];
        }

        /// <summary>Xsiz − XOsiz at reduction 0; ⌈Xsiz / 2^r⌉ − ⌈XOsiz / 2^r⌉ at reduction r.</summary>
        internal int Width { get; }

        /// <summary>Ysiz − YOsiz, reduced likewise.</summary>
        internal int Height { get; }

        /// <summary>How many decomposition levels short of the reference grid's resolution the planes are; 0 for the grid's own.</summary>
        internal int Reduction { get; }

        /// <summary>Each component's precision, sign and separation; a Part 2 transformation's outputs carry the CBD's depths.</summary>
        internal JpxComponentSize[] Components { get; }

        /// <summary>
        /// One plane per component, raster order over <see cref="Width"/> × <see cref="Height"/>. A
        /// plane exists once something was placed in it, or <see cref="Plane"/> was asked for it —
        /// after a decode every plane exists — and it may be the very array a tile-component's
        /// samples are in (<see cref="Adopt(int, JpxRect, int[])"/>).
        /// </summary>
        internal int[][] Samples { get; }

        /// <summary>The planes of components deeper than <see cref="JpxComponentTransforms.MaxNarrowPrecision"/> bits, which <see cref="Samples"/> cannot hold; null for the others.</summary>
        internal long[][] WideSamples { get; }

        /// <summary>Whether component <paramref name="component"/>'s plane is a <see cref="WideSamples"/> one.</summary>
        internal bool IsWide(int component) => JpxComponentTransforms.IsWide(Components[component].Precision);

        /// <summary>A component's plane, made (all zero) if nothing has been placed in it yet.</summary>
        internal int[] Plane(int component) => Samples[component] ?? (Samples[component] = new int[(long)Width * Height]);

        /// <summary>A wide component's plane, likewise.</summary>
        internal long[] WidePlane(int component) => WideSamples[component] ?? (WideSamples[component] = new long[(long)Width * Height]);

        /// <summary>
        /// An image over <paramref name="grid"/>'s image area, <paramref name="reduction"/> levels
        /// short of the grid's resolution (0 for the grid's own), with one plane per entry of
        /// <paramref name="components"/> — the SIZ's components, or the CBD's after a Part 2
        /// transformation. Refuses an empty area, one over <see cref="PdfLimits.MaxImageDimension"/>
        /// or <see cref="PdfLimits.MaxImagePixelArea"/>, more samples than
        /// <see cref="PdfLimits.MaxDecompressedBytes"/> counted one byte each (the filter's 8-bit
        /// output; the planes cost four times that) — the ceilings are on the <b>full</b> area, what
        /// the codestream claims to be, whatever the reduction — and a component deeper than
        /// <see cref="JpxComponentTransforms.MaxSamplePrecision"/> bits.
        /// </summary>
        internal static bool TryCreate(JpxImageSize grid, JpxComponentSize[] components, int reduction, out JpxDecodedImage image, out string reason)
        {
            image = null;
            if (!TryValidateArea(grid, components?.Length ?? 0, out reason)) return false;
            if (!TryValidateWorkingSet(grid, components.Length, reduction, out reason)) return false;
            if (reduction < 0 || reduction > 32)
            {
                reason = $"a reduction of {reduction} levels; T.800 allows 0 to 32 decomposition levels";
                return false;
            }
            for (int c = 0; c < components.Length; c++)
            {
                if (components[c].Precision > JpxComponentTransforms.MaxSamplePrecision)
                {
                    reason = $"component {c} has {components[c].Precision}-bit samples; the sample planes hold at most {JpxComponentTransforms.MaxSamplePrecision} bits";
                    return false;
                }
                if (components[c].XRsiz < 1 || components[c].YRsiz < 1)
                {
                    reason = $"component {c} declares a sample separation of 0; T.800 allows 1 to 255";
                    return false;
                }
            }
            JpxRect area = JpxGeometry.Resolution(JpxGeometry.ImageArea(grid), reduction, 0);
            if (area.Width <= 0 || area.Height <= 0)
            {
                // ⌈x1 / 2^r⌉ − ⌈x0 / 2^r⌉ is 0 when both corners fall in one 2^r cell: an image
                // with an offset, reduced past its own size. (From the origin one sample always
                // remains.) A tile-component's bounds collapse the same way (B-14), so nothing
                // could be placed; refused with the reason rather than an empty plane.
                reason = $"the image area is empty at reduction {reduction}: ({area.X0}, {area.Y0})-({area.X1}, {area.Y1})";
                return false;
            }
            image = new JpxDecodedImage(area, reduction, (JpxComponentSize[])components.Clone());
            return true;
        }

        /// <summary>
        /// The image-wide ceilings, each read at run time — a non-empty area, at most
        /// <see cref="PdfLimits.MaxImageDimension"/> a side, <see cref="PdfLimits.MaxImagePixelArea"/>
        /// pixels, and <see cref="PdfLimits.MaxDecompressedBytes"/> samples over
        /// <paramref name="componentCount"/> planes — checked <b>before</b> a decode allocates a tile's
        /// planes, whatever the reduction: a tile's planes are at most the image's, so a codestream
        /// whose component count times its area is past the ceiling is refused with nothing allocated.
        /// </summary>
        internal static bool TryValidateArea(JpxImageSize grid, int componentCount, out string reason)
        {
            reason = null;
            long width = grid.Xsiz - grid.XOsiz, height = grid.Ysiz - grid.YOsiz;
            if (width <= 0 || height <= 0)
            {
                reason = $"the image area is empty: the grid is {grid.Xsiz}x{grid.Ysiz} and the image starts at ({grid.XOsiz}, {grid.YOsiz})";
                return false;
            }
            long maxDimension = PdfLimits.MaxImageDimension;
            if (width > maxDimension || height > maxDimension)
            {
                reason = $"the image is {width}x{height}; PdfLimits.MaxImageDimension is {maxDimension}";
                return false;
            }
            long area = width * height;
            if (area > PdfLimits.MaxImagePixelArea)
            {
                reason = $"the image is {width}x{height}, {area} pixels; PdfLimits.MaxImagePixelArea is {PdfLimits.MaxImagePixelArea}";
                return false;
            }
            if (componentCount < 1)
            {
                reason = "the image has no components";
                return false;
            }
            long samples = area * componentCount;
            if (samples > PdfLimits.MaxDecompressedBytes)
            {
                reason = $"the image is {width}x{height} with {componentCount} components, {samples} samples; PdfLimits.MaxDecompressedBytes is {PdfLimits.MaxDecompressedBytes}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The decode's peak memory against <see cref="PdfLimits.MaxJpxWorkingSetBytes"/>: the image
        /// area at <paramref name="reduction"/> levels short of the grid — what the planes will hold —
        /// times <paramref name="componentCount"/> planes, at <see cref="WorkingBytesPerSample"/> each.
        /// Asked once the reduction is final (the decoder clamps it to the levels the tiles have) and
        /// before any tile's samples are allocated: a single-tile image's tier 1 values alone are a
        /// quarter of the peak, so a check after the first tile has already paid it.
        /// </summary>
        internal static bool TryValidateWorkingSet(JpxImageSize grid, int componentCount, int reduction, out string reason)
        {
            reason = null;
            JpxRect area = JpxGeometry.Resolution(JpxGeometry.ImageArea(grid), Math.Max(0, Math.Min(reduction, 32)), 0);
            long samples = Math.Max(0, area.Width) * Math.Max(0, area.Height) * componentCount;
            long bytes = samples * WorkingBytesPerSample;
            if (bytes > PdfLimits.MaxJpxWorkingSetBytes)
            {
                reason = $"the decode holds {samples} samples at reduction {reduction} ({area.Width}x{area.Height}, {componentCount} planes), " +
                         $"{bytes} bytes at {WorkingBytesPerSample} a sample; PdfLimits.MaxJpxWorkingSetBytes is {PdfLimits.MaxJpxWorkingSetBytes}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Places a decoded tile-component's samples — raster order over <paramref name="tileComponent"/>,
        /// in the component's coordinates (T.800 B-12; B-14's at the image's reduction) — into the
        /// component's plane, upsampling a sub-sampled component by replication (see the class
        /// remarks). Samples outside the component's extent (B-1) are ignored.
        /// </summary>
        internal void Place(int component, JpxRect tileComponent, int[] samples) => Place(component, tileComponent, samples, Plane(component));

        /// <summary>As <see cref="Place(int, JpxRect, int[])"/>, for a wide component's <c>long</c> samples.</summary>
        internal void Place(int component, JpxRect tileComponent, long[] samples) => Place(component, tileComponent, samples, WidePlane(component));

        /// <summary>The one placement, over either sample type.</summary>
        private void Place<T>(int component, JpxRect tileComponent, T[] samples, T[] plane)
        {
            JpxComponentSize c = Components[component];
            long w = tileComponent.Width, h = tileComponent.Height;
            if (w <= 0 || h <= 0) return;
            if (samples.Length < w * h)
                throw new ArgumentException($"tile-component {tileComponent} needs {w * h} samples; {samples.Length} given", nameof(samples));

            int xr = c.XRsiz, yr = c.YRsiz;
            // The component's extent on its own grid, equation B-2 over the image area at this
            // image's resolution, from the one place that computes it; the image's components carry
            // the SIZ's separations (or a Part 2 output's inputs').
            JpxRect extent = JpxGeometry.Component(_area, new JpxComponentSize(c.Precision, c.IsSigned, xr, yr));
            long cx0 = extent.X0, cy0 = extent.Y0;
            long x0 = Math.Max(tileComponent.X0, cx0), x1 = Math.Min(tileComponent.X1, extent.X1);
            long y0 = Math.Max(tileComponent.Y0, cy0), y1 = Math.Min(tileComponent.Y1, extent.Y1);
            if (x1 <= x0 || y1 <= y0) return;

            if (xr == 1 && yr == 1)
            {
                // The component's grid is the reference grid: each row is a copy.
                for (long cy = y0; cy < y1; cy++)
                    Array.Copy(samples, (cy - tileComponent.Y0) * w + (x0 - tileComponent.X0),
                        plane, (cy - _area.Y0) * Width + (x0 - _area.X0), x1 - x0);
                return;
            }

            // The grid columns a component row covers: from its first sample's to its last's, contiguous. A
            // component sample inside the extent (B-2's ceilings over the area) starts inside the area, so
            // every span and every row range below is non-empty.
            long spanStart = x0 == cx0 ? _area.X0 : x0 * xr, spanEnd = Math.Min(x1 * xr, _area.X1);
            for (long cy = y0; cy < y1; cy++)
            {
                long gy0 = cy == cy0 ? _area.Y0 : cy * yr;
                long gy1 = Math.Min((cy + 1) * yr, _area.Y1);
                // The first grid row the component row covers, each sample replicated across its columns;
                // the rows below it are the same span, copied.
                long source = (cy - tileComponent.Y0) * w - tileComponent.X0;
                long first = (gy0 - _area.Y0) * Width - _area.X0;
                for (long cx = x0; cx < x1; cx++)
                {
                    T v = samples[source + cx];
                    long gx0 = cx == cx0 ? _area.X0 : cx * xr;
                    long gx1 = Math.Min((cx + 1) * xr, _area.X1);
                    for (long gx = gx0; gx < gx1; gx++) plane[first + gx] = v;
                }
                for (long gy = gy0 + 1; gy < gy1; gy++)
                    Array.Copy(plane, first + spanStart, plane, (gy - _area.Y0) * Width - _area.X0 + spanStart, spanEnd - spanStart);
            }
        }

        /// <summary>
        /// Places a tile-component's samples as <see cref="Place(int, JpxRect, int[])"/> does, but when the samples are
        /// the whole plane already — the one tile of a single-tile image, at the image's own
        /// resolution, with exactly the plane's sample count — the array itself becomes the plane
        /// and nothing is copied: true then, false when it was placed by copy. A plane adopted is
        /// shared with the caller's array; a placed one is the image's own.
        /// </summary>
        internal bool Adopt(int component, JpxRect tileComponent, int[] samples)
        {
            if (Samples[component] == null && CanAdopt(component, tileComponent, samples.Length))
            {
                Samples[component] = samples;
                return true;
            }
            Place(component, tileComponent, samples);
            return false;
        }

        /// <summary>As <see cref="Adopt(int, JpxRect, int[])"/>, for a wide component's <c>long</c> samples.</summary>
        internal bool Adopt(int component, JpxRect tileComponent, long[] samples)
        {
            if (WideSamples[component] == null && CanAdopt(component, tileComponent, samples.Length))
            {
                WideSamples[component] = samples;
                return true;
            }
            Place(component, tileComponent, samples);
            return false;
        }

        private bool CanAdopt(int component, JpxRect tileComponent, long sampleCount)
        {
            JpxComponentSize c = Components[component];
            return c.XRsiz == 1 && c.YRsiz == 1
                && tileComponent.X0 == _area.X0 && tileComponent.Y0 == _area.Y0
                && tileComponent.X1 == _area.X1 && tileComponent.Y1 == _area.Y1
                && sampleCount == (long)Width * Height;
        }

        /// <summary>Every component's samples narrowed to 8 bits (see the class remarks), interleaved pixel by pixel in component order.</summary>
        internal byte[] ToInterleaved8Bit()
        {
            int n = Components.Length;
            long area = (long)Width * Height;
            var dst = new byte[area * n];
            for (int c = 0; c < n; c++)
            {
                int precision = Components[c].Precision;
                bool signed = Components[c].IsSigned;
                long at = c;
                if (IsWide(c))
                {
                    long[] wide = WidePlane(c);
                    for (long i = 0; i < area; i++, at += n) dst[at] = NarrowTo8Bit(wide[i], precision, signed);
                    continue;
                }
                int[] plane = Plane(c);
                for (long i = 0; i < area; i++, at += n) dst[at] = NarrowTo8Bit(plane[i], precision, signed);
            }
            return dst;
        }

        /// <summary>The one narrowing rule (see the class remarks): a signed value is offset by 2^(precision − 1) first.</summary>
        internal static byte NarrowTo8Bit(int value, int precision, bool isSigned) => NarrowTo8Bit((long)value, precision, isSigned);

        /// <summary>The same rule for a sample of up to 38 bits.</summary>
        internal static byte NarrowTo8Bit(long value, int precision, bool isSigned)
        {
            long v = isSigned ? value + (1L << (precision - 1)) : value;
            if (precision > 8)
            {
                v >>= precision - 8;
                return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
            if (precision == 8) return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            long max = (1L << precision) - 1;
            if (v < 0) v = 0;
            else if (v > max) v = max;
            return (byte)(v * 255 / max);
        }
    }
}
