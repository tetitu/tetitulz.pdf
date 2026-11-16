// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// One horizontal cut through a rendered page, reduced to the run lengths a linear symbology
    /// is actually made of.
    /// </summary>
    /// <remarks>
    /// <para><b>Runs, not pixels, are the unit every 1-D symbology is defined in.</b> Code 128 and
    /// EAN-13 both describe characters as element <i>widths</i> — so converting a row to alternating
    /// dark/light lengths once, here, means neither reader has to know anything about pixels,
    /// thresholds or resolution. That also makes both readers testable from a hand-built run list
    /// with no rendering at all.</para>
    ///
    /// <para><b>The pixels are premultiplied, and this composites over white rather than ignoring
    /// alpha.</b> A rendered page is normally opaque, so the two agree on almost every fixture —
    /// which is exactly why a version that ignored alpha would look correct. A caller rendering
    /// with a transparent background gets the answer paper would give: unpainted is light.</para>
    /// </remarks>
    internal static class BarcodeScanline
    {
        /// <summary>Luminance levels in the histogram Otsu's method searches.</summary>
        private const int Levels = 256;

        /// <summary>
        /// A run of one tone: <see cref="IsDark"/> and its length in pixels.
        /// </summary>
        internal readonly struct Run
        {
            internal Run(bool isDark, int length, int start)
            {
                IsDark = isDark; Length = length; Start = start;
            }

            internal bool IsDark { get; }
            internal int Length { get; }

            /// <summary>x of the run's first pixel, so a decoder can report where it read.</summary>
            internal int Start { get; }
        }

        /// <summary>
        /// The luminance threshold separating ink from paper, by Otsu's method.
        /// </summary>
        /// <remarks>
        /// <b>Computed once for the page, not per row.</b> A row crossing only white paper has no
        /// bimodal histogram at all, so a per-row threshold would invent a split in the noise and
        /// hand the readers a page full of phantom bars. A page-wide threshold has the barcode's own
        /// ink in it wherever the barcode is.
        /// </remarks>
        internal static int Threshold(byte[] pixels, int width, int height, int stride)
        {
            var histogram = new long[Levels];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                    histogram[Luminance(pixels, row + (x * 4))]++;
            }

            long total = (long)width * height;
            if (total == 0) return Levels / 2;

            double sum = 0;
            for (int i = 0; i < Levels; i++) sum += (double)i * histogram[i];

            double sumBackground = 0, best = -1;
            long weightBackground = 0;
            int threshold = Levels / 2;

            for (int i = 0; i < Levels; i++)
            {
                weightBackground += histogram[i];
                if (weightBackground == 0) continue;

                long weightForeground = total - weightBackground;
                if (weightForeground == 0) break;

                sumBackground += (double)i * histogram[i];

                double meanBackground = sumBackground / weightBackground;
                double meanForeground = (sum - sumBackground) / weightForeground;
                double delta = meanBackground - meanForeground;
                double variance = (double)weightBackground * weightForeground * delta * delta;

                if (variance > best) { best = variance; threshold = i; }
            }

            return threshold;
        }

        /// <summary>
        /// One row reduced to runs, or <see langword="null"/> when the row is a single tone.
        /// </summary>
        /// <remarks>
        /// A uniform row is returned as <see langword="null"/> rather than as one long run because
        /// that is the overwhelmingly common case on a real page, and every caller would otherwise
        /// have to special-case it before doing anything useful.
        /// </remarks>
        internal static List<Run> Runs(byte[] pixels, int width, int stride, int y, int threshold)
        {
            int row = y * stride;
            var runs = new List<Run>();

            bool current = Luminance(pixels, row) <= threshold;
            int start = 0;

            for (int x = 1; x < width; x++)
            {
                bool dark = Luminance(pixels, row + (x * 4)) <= threshold;
                if (dark == current) continue;

                runs.Add(new Run(current, x - start, start));
                current = dark;
                start = x;
            }
            runs.Add(new Run(current, width - start, start));

            return runs.Count > 1 ? runs : null;
        }

        /// <summary>
        /// The page as ink or paper, a byte a pixel, against <paramref name="threshold"/> — read once, so
        /// that lines crossing a pixel in many directions do not each convert it to luminance again.
        /// </summary>
        internal static byte[] DarkMap(byte[] pixels, int width, int height, int stride, int threshold)
        {
            var map = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride, at = y * width;
                for (int x = 0; x < width; x++)
                    if (Luminance(pixels, row + (x * 4)) <= threshold) map[at + x] = 1;
            }
            return map;
        }

        /// <summary>
        /// The runs along a line, read from <paramref name="darkMap"/>: <paramref name="count"/> samples a pixel apart from
        /// (<paramref name="x0"/>, <paramref name="y0"/>) in the unit direction (<paramref name="dx"/>,
        /// <paramref name="dy"/>), each the pixel it falls in; a run's <see cref="Run.Start"/> is its
        /// first sample's index. <see langword="null"/> when the line is a single tone, as for a row.
        /// </summary>
        /// <remarks>
        /// A line crossing the bars at an angle sees every width scaled by the same factor, which the
        /// readers' quantisers absorb as they absorb an arbitrary DPI — so a symbol turned on the page is
        /// read along a line turned with it.
        /// </remarks>
        internal static List<Run> RunsAlong(byte[] darkMap, int width, int height,
            double x0, double y0, double dx, double dy, int count)
        {
            if (count < 2) return null;
            List<Run> runs = null;   // a line that never changes tone, most of a page's, allocates nothing
            bool current = false;
            int start = 0;
            double fx = x0, fy = y0;
            for (int i = 0; i < count; i++, fx += dx, fy += dy)
            {
                // The samples lie in the page, so truncation is the floor; the clamp is for the last one.
                int x = SaturatingCast.ToInt(fx), y = SaturatingCast.ToInt(fy);
                if (x < 0) x = 0; else if (x >= width) x = width - 1;
                if (y < 0) y = 0; else if (y >= height) y = height - 1;
                bool dark = darkMap[(y * width) + x] != 0;
                if (i == 0) { current = dark; continue; }
                if (dark == current) continue;
                (runs ??= new List<Run>()).Add(new Run(current, i - start, start));
                current = dark;
                start = i;
            }
            if (runs == null) return null;
            runs.Add(new Run(current, count - start, start));
            return runs;
        }

        /// <summary>
        /// Quantises <paramref name="count"/> runs into module counts summing to
        /// <paramref name="modules"/>, or <see langword="false"/> when they cannot.
        /// </summary>
        /// <remarks>
        /// <para><b>Rounding each element independently is what makes this tolerant of a symbol
        /// whose module width is not a whole number of pixels</b> — which it never is, once a page
        /// is rendered at an arbitrary DPI. The total is then checked against the count the
        /// symbology requires, and a mismatch is a refusal rather than a repair: a group that does
        /// not sum correctly is not a character that needs nudging, it is a misread.</para>
        ///
        /// <para>A zero-length element cannot occur — runs alternate by construction — but a run
        /// narrower than half a module rounds to zero, and that <i>is</i> a misread, so it is
        /// refused for the same reason.</para>
        /// </remarks>
        internal static bool Quantise(
            List<Run> runs, int first, int count, int modules, int[] into)
        {
            if (first < 0 || first + count > runs.Count) return false;

            double total = 0;
            for (int i = 0; i < count; i++) total += runs[first + i].Length;
            if (total <= 0) return false;

            int sum = 0;
            for (int i = 0; i < count; i++)
            {
                int width = SaturatingCast.ToInt(Math.Round(runs[first + i].Length * modules / total,
                                            MidpointRounding.AwayFromZero));
                if (width <= 0) return false;
                into[i] = width;
                sum += width;
            }

            return sum == modules;
        }

        /// <summary>Element widths, most significant module first, packed back into a bit pattern.</summary>
        /// <param name="widths">Element widths in modules, starting with the element in
        /// <paramref name="firstIsDark"/>.</param>
        /// <param name="count">How many entries of <paramref name="widths"/> to read.</param>
        /// <param name="firstIsDark">Whether the first element is a bar.</param>
        internal static int Pattern(int[] widths, int count, bool firstIsDark)
        {
            int pattern = 0;
            bool dark = firstIsDark;

            for (int i = 0; i < count; i++)
            {
                for (int m = 0; m < widths[i]; m++)
                    pattern = (pattern << 1) | (dark ? 1 : 0);
                dark = !dark;
            }

            return pattern;
        }

        /// <summary>Whether the pixel at (x, y) is ink rather than paper.</summary>
        /// <remarks>
        /// The one place a pixel is judged. <see cref="QrLocator"/> samples individual modules
        /// rather than whole rows and would otherwise need its own copy of the premultiplied
        /// composite above — two implementations of "is this dark", which is exactly the shape
        /// CLAUDE.md says to extract rather than to comment.
        /// </remarks>
        internal static bool IsDark(byte[] pixels, int stride, int x, int y, int threshold)
            => Luminance(pixels, (y * stride) + (x * 4)) <= threshold;

        /// <summary>
        /// Perceived luminance of one premultiplied RGBA pixel, composited over white.
        /// </summary>
        /// <remarks>
        /// The weights are <see cref="PdfColorSpaceNames"/>' BT.601 set — the tree's one answer to
        /// "how bright is this colour", so a barcode judges ink the same way everything else does.
        /// </remarks>
        private static byte Luminance(byte[] pixels, int offset)
        {
            byte a = pixels[offset + 3];

            // Premultiplied: the stored byte is already colour x alpha, so compositing over white
            // is an addition rather than an un-premultiply followed by a blend.
            int paper = 255 - a;
            int r = pixels[offset] + paper;
            int g = pixels[offset + 1] + paper;
            int b = pixels[offset + 2] + paper;

            double y = (PdfColorSpaceNames.LumaRed * r)
                     + (PdfColorSpaceNames.LumaGreen * g)
                     + (PdfColorSpaceNames.LumaBlue * b);

            return y <= 0 ? (byte)0 : y >= 255 ? (byte)255 : SaturatingCast.ToByte(y);
        }
    }
}
