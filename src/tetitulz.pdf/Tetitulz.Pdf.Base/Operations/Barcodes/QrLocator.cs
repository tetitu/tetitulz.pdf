// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Finds QR symbols on a rendered page and samples each into a module grid.
    /// </summary>
    /// <remarks>
    /// <para><b>Any rotation, and perspective.</b> A typeset symbol sits square to the page, but a
    /// scanned one is skewed, a page set at an angle turns it, and a photographed label is
    /// foreshortened. The geometry is the usual detector's, re-derived: the three finders ordered by their
    /// distances, the module size measured along the sides, the fourth corner from the alignment
    /// pattern, every module sampled through the perspective transform the four points define. A square
    /// symbol's transform is a scale and an offset, so it samples as the axis-aligned reader did.</para>
    ///
    /// <para><b>Finder patterns are located by ratio, not by size.</b> The three corner squares are
    /// dark:light:dark:light:dark in 1:1:3:1:1 module widths — a proportion that survives any
    /// resolution, so nothing here needs to know the DPI the page was rendered at.</para>
    ///
    /// <para><b>Whether a candidate is really a symbol is settled by decoding it, not here.</b> This
    /// deliberately over-offers: ordinary page furniture produces 1:1:3:1:1 runs often enough that a
    /// locator strict enough to exclude them would also exclude real symbols at low resolution. The
    /// format field admits 32 values out of 32768 and Reed–Solomon has to close on top of that, so
    /// a false candidate costs a few microseconds and is rejected on evidence.</para>
    /// </remarks>
    internal static class QrLocator
    {
        /// <summary>Module widths of the finder's five elements.</summary>
        private static readonly int[] FinderRatio = { 1, 1, 3, 1, 1 };

        /// <summary>Modules across a finder pattern.</summary>
        private const int FinderModules = 7;

        /// <summary>How far an element may deviate from its expected width, as a fraction.</summary>
        /// <remarks>
        /// Half a module. Tighter rejects real symbols whose module width is not a whole number of
        /// pixels, which is every symbol at an arbitrary DPI; looser starts matching text.
        /// </remarks>
        private const double RatioTolerance = 0.5;

        /// <summary>Smallest module size, in pixels, that can be sampled reliably.</summary>
        private const double MinModulePixels = 1.5;

        /// <summary>A located finder pattern's centre and module size.</summary>
        private readonly struct Finder
        {
            internal Finder(double x, double y, double module)
            {
                X = x; Y = y; Module = module;
            }

            internal double X { get; }
            internal double Y { get; }
            internal double Module { get; }
        }

        /// <summary>
        /// Every QR grid this can sample out of the page, with the pixel box each came from, offered to
        /// <paramref name="accept"/>, which answers whether the grid was read; the three finders of a grid
        /// read are not offered again, and the search stops when the grids not read have spent the
        /// <see cref="CandidateBudget"/>.
        /// </summary>
        /// <remarks>
        /// <b>Candidates, plural, and the caller decides.</b> Returning the first triple it can sample
        /// is fine on a page holding nothing but a barcode and wrong on a real one: a false triple formed out of body text would be sampled first and the
        /// genuine symbol never tried. Sampling is cheap and decoding rejects a false candidate on
        /// evidence — the format field admits 32 values in 32768, and Reed–Solomon has to close on
        /// top of that — so offering several and letting them fail is both simpler and more robust
        /// than trying to be sure here.
        /// </remarks>
        internal static void Locate(
            byte[] pixels, int width, int height, int stride, int threshold,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept)
            => Locate(pixels, width, height, stride, threshold, accept, new CandidateBudget());

        /// <summary>The same, spending <paramref name="budget"/> — the bound's evidence.</summary>
        internal static void Locate(
            byte[] pixels, int width, int height, int stride, int threshold,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept, CandidateBudget budget)
        {
            List<Finder> finders = FindFinders(pixels, width, height, stride, threshold);
            if (finders.Count < 3) return;
            var retired = new bool[finders.Count];

            // Three finders make a symbol, in any orientation: the one opposite the longest side is
            // the top-left, and which of the other two is the top-right is the sign of a cross product
            // (the order the usual detector takes). The triples are tried
            // squarest first, so that finder-shaped data — a large symbol's modules make some — does not
            // spend the budget before the symbol's own three are reached. Smallest first, measured
            // against it, fails 449 candidates on a sheet of 130 labels before its last, not none.
            foreach (var (_, a, b, c) in Triples(finders, out _))
            {
                if (retired[a] || retired[b] || retired[c]) continue;
                Finder topLeft = finders[a], topRight = finders[b], bottomLeft = finders[c];

                // Measured at both ends of both sides (the usual module-size estimate): under perspective the
                // modules shrink across the symbol, and the top-left's alone would undercount them.
                double module = (ModuleAlong(pixels, width, height, stride, threshold, topLeft, topRight)
                               + ModuleAlong(pixels, width, height, stride, threshold, topRight, topLeft)
                               + ModuleAlong(pixels, width, height, stride, threshold, topLeft, bottomLeft)
                               + ModuleAlong(pixels, width, height, stride, threshold, bottomLeft, topLeft)) / 4;
                if (!(module >= MinModulePixels)) continue;
                double estimate = ((Distance(topLeft, topRight) + Distance(topLeft, bottomLeft)) / 2 / module) + FinderModules;
                foreach (int size in CandidateSizes(estimate))
                {
                    var candidate = Sample(pixels, width, height, stride, threshold, topLeft, topRight, bottomLeft, module, size);
                    if (candidate == null) continue;
                    if (accept(candidate.Value))
                    {
                        retired[a] = retired[b] = retired[c] = true;
                        break;
                    }
                    budget.Fail(size, size);
                    if (budget.Spent) return;
                }
            }
        }

        /// <summary>The candidates offered on a page where none is read, in the order offered.</summary>
        internal static List<(bool[,] Grid, int Left, int Top, int Right, int Bottom)> Locate(
            byte[] pixels, int width, int height, int stride, int threshold)
        {
            var found = new List<(bool[,], int, int, int, int)>();
            Locate(pixels, width, height, stride, threshold, candidate => { found.Add(candidate); return false; });
            return found;
        }

        /// <summary>
        /// Most finders kept. A page tiled with finder patterns makes as many as its area holds — a
        /// 10-megapixel one forty thousand, and recording them, each against those already kept, took 76 s;
        /// a page of symbols holds three to a symbol.
        /// </summary>
        internal const int MaxFinders = 1024;

        /// <summary>
        /// How many of its nearest finders each finder is paired with. A symbol's corners are each other's
        /// near neighbours — data inside a large symbol makes some finder-shaped ink nearer still, and a
        /// sheet of labels its neighbours' finders — so the triples tested are linear in the finders, where
        /// every combination was tested: a row of a thousand finders, every three collinear and none a
        /// triple, took 41 s.
        /// </summary>
        internal const int NearestFinders = 16;

        /// <summary>
        /// The triples that can be a symbol's corners, as indices into <paramref name="finders"/> ordered
        /// top-left, top-right, bottom-left, squarest first. Each finder is tried with pairs of its
        /// <see cref="NearestFinders"/> nearest whose module it could share, each combination once;
        /// <paramref name="tests"/> is how many were tested.
        /// </summary>
        private static List<(double Score, int TopLeft, int TopRight, int BottomLeft)> Triples(
            List<Finder> finders, out int tests)
        {
            var triples = new List<(double, int, int, int)>();
            var tested = new HashSet<long>();
            var near = new int[NearestFinders];
            var nearDistance = new double[NearestFinders];
            tests = 0;
            for (int a = 0; a < finders.Count; a++)
            {
                int count = Nearest(finders, a, near, nearDistance);
                for (int i = 0; i < count; i++)
                    for (int j = i + 1; j < count; j++)
                    {
                        int b = near[i], c = near[j];
                        if (!tested.Add(Combination(a, b, c))) continue;
                        tests++;
                        if (Order(finders, a, b, c, out int topLeft, out int topRight, out int bottomLeft, out double score))
                            triples.Add((score, topLeft, topRight, bottomLeft));
                    }
            }
            triples.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return triples;
        }

        /// <summary>
        /// The up to <see cref="NearestFinders"/> finders nearest <paramref name="of"/> whose modules are
        /// within a factor of two of its own — the factor <see cref="Order"/> allows a triple — nearest first.
        /// </summary>
        private static int Nearest(List<Finder> finders, int of, int[] near, double[] distance)
        {
            Finder centre = finders[of];
            int count = 0;
            for (int k = 0; k < finders.Count; k++)
            {
                if (k == of) continue;
                Finder other = finders[k];
                if (other.Module > 2 * centre.Module || centre.Module > 2 * other.Module) continue;
                double d = ((other.X - centre.X) * (other.X - centre.X)) + ((other.Y - centre.Y) * (other.Y - centre.Y));
                if (count == near.Length && d >= distance[count - 1]) continue;
                int at = count < near.Length ? count++ : count - 1;
                while (at > 0 && distance[at - 1] > d)
                {
                    near[at] = near[at - 1];
                    distance[at] = distance[at - 1];
                    at--;
                }
                near[at] = k;
                distance[at] = d;
            }
            return count;
        }

        /// <summary>Three finder indices as one key, whatever their order; each is below <see cref="MaxFinders"/>.</summary>
        private static long Combination(int a, int b, int c)
        {
            int low = Math.Min(a, Math.Min(b, c)), high = Math.Max(a, Math.Max(b, c));
            int middle = a + b + c - low - high;
            return ((long)low << 40) | ((long)middle << 20) | (uint)high;
        }

        /// <summary>How many triples a page yields for sampling.</summary>
        internal static int CountTriples(byte[] pixels, int width, int height, int stride, int threshold)
            => Triples(FindFinders(pixels, width, height, stride, threshold), out _).Count;

        /// <summary>How many combinations of a page's finders are tested — the nearest-finder bound's evidence.</summary>
        internal static int CountTripleTests(byte[] pixels, int width, int height, int stride, int threshold)
        {
            Triples(FindFinders(pixels, width, height, stride, threshold), out int tests);
            return tests;
        }

        /// <summary>
        /// The symbol sizes an estimate allows, smallest first: a QR symbol is 4v + 17 modules, and the
        /// module size measured from a finder's edges is a few percent out — over a large symbol, more
        /// than one module. So the valid sizes within four percent (at least two modules) are each
        /// offered, and the decoder's Reed–Solomon decides, as it decides every candidate.
        /// </summary>
        internal static IEnumerable<int> CandidateSizes(double estimate)
        {
            double window = Math.Max(2, 0.04 * estimate);
            var sizes = new List<int>();
            for (int size = 21; size <= 177; size += 4)
                if (Math.Abs(size - estimate) <= window) sizes.Add(size);
            return sizes;
        }

        /// <summary>
        /// Orders three finders as a symbol's corners, or refuses them: the two sides meeting at the
        /// top-left must agree within two fifths (perspective foreshortens one), and the diagonal with
        /// their right-angled sum within three tenths; the module sizes within a factor of two.
        /// <paramref name="score"/> is how far from a square the three are, smallest first.
        /// </summary>
        private static bool Order(List<Finder> finders, int p, int q, int r,
            out int topLeftIndex, out int topRightIndex, out int bottomLeftIndex, out double score)
        {
            score = 0;
            double pq = Distance(finders[p], finders[q]), qr = Distance(finders[q], finders[r]), pr = Distance(finders[p], finders[r]);
            // The corner opposite the longest side is the top-left.
            if (qr >= pq && qr >= pr) { topLeftIndex = p; topRightIndex = q; bottomLeftIndex = r; }
            else if (pr >= pq && pr >= qr) { topLeftIndex = q; topRightIndex = p; bottomLeftIndex = r; }
            else { topLeftIndex = r; topRightIndex = p; bottomLeftIndex = q; }
            Finder topLeft = finders[topLeftIndex], topRight = finders[topRightIndex], bottomLeft = finders[bottomLeftIndex];

            // In image coordinates (y down) a symbol read the right way round has the cross product of
            // (topRight - topLeft) and (bottomLeft - topLeft) positive. Swap when it is not.
            double cross = ((topRight.X - topLeft.X) * (bottomLeft.Y - topLeft.Y))
                         - ((topRight.Y - topLeft.Y) * (bottomLeft.X - topLeft.X));
            if (cross < 0)
            {
                var t = topRight; topRight = bottomLeft; bottomLeft = t;
                int ti = topRightIndex; topRightIndex = bottomLeftIndex; bottomLeftIndex = ti;
            }

            double across = Distance(topLeft, topRight), down = Distance(topLeft, bottomLeft);
            double smallest = Math.Min(topLeft.Module, Math.Min(topRight.Module, bottomLeft.Module));
            double largest = Math.Max(topLeft.Module, Math.Max(topRight.Module, bottomLeft.Module));
            if (largest > 2 * smallest) return false;
            if (across < FinderModules * smallest || down < FinderModules * smallest) return false;
            if (Math.Abs(across - down) > (0.4 * Math.Max(across, down)) + (2 * largest)) return false;
            double diagonal = Distance(topRight, bottomLeft);
            double right = Math.Sqrt((across * across) + (down * down));
            if (Math.Abs(diagonal - right) > 0.3 * right) return false;
            score = (Math.Abs(across - down) / Math.Max(across, down)) + (Math.Abs(diagonal - right) / right)
                  + ((largest / smallest) - 1);
            return true;
        }

        private static double Distance(Finder a, Finder b)
            => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

        /// <summary>
        /// Samples a symbol of <paramref name="size"/> modules from its three finders: the fourth corner
        /// from the alignment pattern where the version has one, and every module's colour at its centre
        /// through the perspective transform the four points define — the usual detector
        /// (Detector.processFinderPatternInfo), re-derived.
        /// </summary>
        private static (bool[,], int, int, int, int)? Sample(
            byte[] pixels, int width, int height, int stride, int threshold,
            Finder topLeft, Finder topRight, Finder bottomLeft, double module, int size)
        {
            int version = QrLayout.VersionOf(size);
            if (version < 0) return null;

            // The fourth corner: the parallelogram's, or the alignment pattern's where there is one.
            double cornerX = topRight.X - topLeft.X + bottomLeft.X;
            double cornerY = topRight.Y - topLeft.Y + bottomLeft.Y;
            double cornerModules = size - 3.5;
            if (version >= 2)
            {
                // The bottom-right alignment pattern's centre is three modules in from the corner the
                // finder centres' parallelogram makes.
                double toward = 1.0 - (3.0 / (size - FinderModules));
                double expectedX = topLeft.X + (toward * (cornerX - topLeft.X));
                double expectedY = topLeft.Y + (toward * (cornerY - topLeft.Y));
                if (FindAlignment(pixels, width, height, stride, threshold, expectedX, expectedY, module,
                                  out double alignX, out double alignY))
                {
                    cornerX = alignX;
                    cornerY = alignY;
                    cornerModules = size - 6.5;
                }
            }

            var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
                3.5, 3.5, size - 3.5, 3.5, cornerModules, cornerModules, 3.5, size - 3.5,
                topLeft.X, topLeft.Y, topRight.X, topRight.Y, cornerX, cornerY, bottomLeft.X, bottomLeft.Y);

            var grid = new bool[size, size];
            for (int row = 0; row < size; row++)
                for (int column = 0; column < size; column++)
                {
                    transform.Apply(column + 0.5, row + 0.5, out double fx, out double fy);
                    if (double.IsNaN(fx) || double.IsNaN(fy)) return null;
                    int x = SaturatingCast.ToInt(Math.Floor(fx)), y = SaturatingCast.ToInt(Math.Floor(fy));
                    if (x < 0 || x >= width || y < 0 || y >= height) return null;
                    grid[row, column] = BarcodeScanline.IsDark(pixels, stride, x, y, threshold);
                }

            double left = double.MaxValue, top = double.MaxValue, rightEdge = double.MinValue, bottom = double.MinValue;
            foreach ((double gx, double gy) in new[] { (0.0, 0.0), (size, 0.0), (0.0, size), ((double)size, (double)size) })
            {
                transform.Apply(gx, gy, out double ix, out double iy);
                left = Math.Min(left, ix); top = Math.Min(top, iy);
                rightEdge = Math.Max(rightEdge, ix); bottom = Math.Max(bottom, iy);
            }
            return (grid, SaturatingCast.ToInt(left), SaturatingCast.ToInt(top), SaturatingCast.ToInt(rightEdge), SaturatingCast.ToInt(bottom));
        }

        /// <summary>
        /// The module size along the line from one finder's centre toward another's: the finder's
        /// extent measured both ways from its centre — dark, light, dark, then light — is seven
        /// modules (a run measured both ways). Falls back to the finder's own estimate.
        /// </summary>
        private static double ModuleAlong(byte[] pixels, int width, int height, int stride, int threshold,
            Finder from, Finder toward)
        {
            double dx = toward.X - from.X, dy = toward.Y - from.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 0) return from.Module;
            dx /= length; dy /= length;
            double forward = FinderExtent(pixels, width, height, stride, threshold, from, dx, dy);
            double backward = FinderExtent(pixels, width, height, stride, threshold, from, -dx, -dy);
            if (forward <= 0 || backward <= 0) return from.Module;
            double measured = (forward + backward) / FinderModules;
            return Math.Abs(measured - from.Module) <= from.Module ? measured : from.Module;
        }

        /// <summary>How far from the centre, in pixels along (dx, dy), the finder's dark-light-dark ends; 0 when it does not.</summary>
        private static double FinderExtent(byte[] pixels, int width, int height, int stride, int threshold,
            Finder from, double dx, double dy)
        {
            int state = 0;   // 0 the centre block, 1 the light ring, 2 the outer border
            double limit = from.Module * FinderModules * 2;
            for (double t = 0; t <= limit; t += 0.5)
            {
                int x = SaturatingCast.ToInt(Math.Floor(from.X + (dx * t))), y = SaturatingCast.ToInt(Math.Floor(from.Y + (dy * t)));
                if (x < 0 || x >= width || y < 0 || y >= height) return 0;
                bool dark = BarcodeScanline.IsDark(pixels, stride, x, y, threshold);
                if (state == 0 && !dark) state = 1;
                else if (state == 1 && dark) state = 2;
                else if (state == 2 && !dark) return t;
            }
            return 0;
        }

        /// <summary>
        /// The alignment pattern nearest where it should be: a dark module ringed light, ringed dark,
        /// each a module — searched for in squares of four, eight and sixteen modules about
        /// (<paramref name="expectedX"/>, <paramref name="expectedY"/>), confirmed down its centre column.
        /// </summary>
        private static bool FindAlignment(byte[] pixels, int width, int height, int stride, int threshold,
            double expectedX, double expectedY, double module, out double alignX, out double alignY)
        {
            alignX = alignY = 0;
            for (int allowance = 4; allowance <= 16; allowance <<= 1)
            {
                double reach = allowance * module;
                int left = Math.Max(0, SaturatingCast.ToInt(expectedX - reach)), right = Math.Min(width - 1, SaturatingCast.ToInt(expectedX + reach));
                int top = Math.Max(0, SaturatingCast.ToInt(expectedY - reach)), bottom = Math.Min(height - 1, SaturatingCast.ToInt(expectedY + reach));
                if (right - left < 3 * module || bottom - top < 3 * module) continue;

                double best = double.MaxValue;
                for (int y = top; y <= bottom; y++)
                {
                    // A light-dark-light run of one module each, the dark one the centre.
                    int runStart = left, i = 0;
                    var runs = new List<(int Start, int Length, bool Dark)>();
                    bool current = BarcodeScanline.IsDark(pixels, stride, left, y, threshold);
                    for (int x = left + 1; x <= right + 1; x++)
                    {
                        bool dark = x <= right && BarcodeScanline.IsDark(pixels, stride, x, y, threshold);
                        if (x <= right && dark == current) continue;
                        runs.Add((runStart, x - runStart, current));
                        runStart = x;
                        current = dark;
                    }
                    for (i = 1; i + 1 < runs.Count; i++)
                    {
                        var (start, length, dark) = runs[i];
                        if (!dark || runs[i - 1].Dark || runs[i + 1].Dark) continue;
                        if (!Near(length, module) || !Near(runs[i - 1].Length, module) || !Near(runs[i + 1].Length, module)) continue;
                        double cx = start + (length / 2.0);
                        if (!AlignmentColumn(pixels, height, stride, threshold, SaturatingCast.ToInt(cx), y, module, out double cy)) continue;
                        double d = ((cx - expectedX) * (cx - expectedX)) + ((cy - expectedY) * (cy - expectedY));
                        if (d < best) { best = d; alignX = cx; alignY = cy; }
                    }
                }
                if (best < double.MaxValue) return true;
            }
            return false;
        }

        private static bool Near(double length, double module) => Math.Abs(length - module) <= RatioTolerance * module;

        /// <summary>The same light-dark-light proportion down the candidate's column, and its centre row.</summary>
        private static bool AlignmentColumn(byte[] pixels, int height, int stride, int threshold,
            int x, int y, double module, out double centreY)
        {
            centreY = 0;
            if (!BarcodeScanline.IsDark(pixels, stride, x, y, threshold)) return false;
            int up = y, down = y;
            while (up > 0 && BarcodeScanline.IsDark(pixels, stride, x, up - 1, threshold)) up--;
            while (down < height - 1 && BarcodeScanline.IsDark(pixels, stride, x, down + 1, threshold)) down++;
            if (!Near(down - up + 1, module)) return false;
            if (!Run(pixels, stride, x, y, 0, 1, 0, height, threshold, up - 1, -1, false, out int above) || !Near(above, module)) return false;
            if (!Run(pixels, stride, x, y, 0, 1, 0, height, threshold, down + 1, 1, false, out int below) || !Near(below, module)) return false;
            centreY = (up + down) / 2.0;   // the finders' convention, so the four corners agree
            return true;
        }

        /// <summary>
        /// How many finder candidates a page yields — the vertical filter's liveness signal.
        /// </summary>
        /// <remarks>
        /// <para><b>The vertical confirmation changes no answer, so nothing that asserts an answer
        /// can pin it.</b> Deleting it left the whole suite green, including a page carrying text, a
        /// QR code and a linear barcode: the extra candidates it admits are rejected later, by the
        /// format field and by Reed–Solomon, and the right symbol is still found. That is
        /// CLAUDE.md's second blind spot exactly — an optimisation whose fast and slow paths agree.
        /// </para>
        ///
        /// <para><b>What it does do is bound the work</b>, and that is worth protecting because the
        /// triple loop over finders is cubic. A page dense with text produces horizontal 1:1:3:1:1
        /// runs freely — the word "ill" makes one — so without the vertical check the candidate list
        /// grows with the prose and the pairing cost grows with its cube. This is the guard's own
        /// evidence, asserted directly rather than through a stopwatch.</para>
        /// </remarks>
        internal static int CountFinderCandidates(
            byte[] pixels, int width, int height, int stride, int threshold)
            => FindFinders(pixels, width, height, stride, threshold).Count;

        /// <summary>
        /// Finder-pattern centres, found by scanning rows for the ratio and confirming each
        /// vertically.
        /// </summary>
        /// <remarks>
        /// <b>The vertical confirmation is what makes this usable.</b> A horizontal 1:1:3:1:1 run
        /// appears all over ordinary text — the word "ill" produces one at the right size. Requiring
        /// the same proportion through the candidate's centre column costs one more scan and removes
        /// almost all of them.
        /// </remarks>
        private static List<Finder> FindFinders(
            byte[] pixels, int width, int height, int stride, int threshold)
        {
            var finders = new List<Finder>();

            for (int y = 0; y < height; y++)
            {
                List<BarcodeScanline.Run> runs =
                    BarcodeScanline.Runs(pixels, width, stride, y, threshold);
                if (runs == null) continue;

                for (int i = 0; i + 5 <= runs.Count; i++)
                {
                    if (!runs[i].IsDark) continue;
                    if (!MatchesRatio(runs, i, out double module)) continue;
                    if (module < MinModulePixels) continue;

                    double centreX = runs[i].Start + (module * FinderModules / 2.0);
                    if (!ConfirmVertically(pixels, width, height, stride, threshold,
                                           SaturatingCast.ToInt(centreX), y, module, out double centreY))
                        continue;

                    // On a rotated symbol this row crossed the central block off its middle, so the
                    // centre is re-found along the row through the vertical estimate, then down the
                    // column through that. On a square one both land where they started.
                    if (ConfirmHorizontally(pixels, width, height, stride, threshold,
                                            SaturatingCast.ToInt(centreX), SaturatingCast.ToInt(centreY), module, out double refinedX)
                        && ConfirmVertically(pixels, width, height, stride, threshold,
                                             SaturatingCast.ToInt(refinedX), SaturatingCast.ToInt(centreY), module, out double refinedY))
                    {
                        centreX = refinedX;
                        centreY = refinedY;
                    }

                    Add(finders, new Finder(centreX, centreY, module));
                    if (finders.Count >= MaxFinders) return finders;
                }
            }

            return finders;
        }

        /// <summary>Whether five runs from <paramref name="first"/> are in 1:1:3:1:1 proportion.</summary>
        private static bool MatchesRatio(List<BarcodeScanline.Run> runs, int first, out double module)
        {
            module = 0;

            double total = 0;
            for (int i = 0; i < 5; i++) total += runs[first + i].Length;

            module = total / FinderModules;
            if (module <= 0) return false;

            for (int i = 0; i < 5; i++)
            {
                double expected = FinderRatio[i] * module;
                if (Math.Abs(runs[first + i].Length - expected) > RatioTolerance * module)
                    return false;
            }

            return true;
        }

        /// <summary>Confirms the same proportion down the candidate's centre column.</summary>
        /// <remarks>
        /// <b>Measured outward from the centre, not downward from a top edge.</b> The scan row that
        /// produced this candidate runs through the finder's central 3×3 block — those are the only
        /// rows whose horizontal profile is 1:1:3:1:1 — so walking up while dark reaches the top of
        /// that <i>block</i>, not of the finder. Walking that way would find three elements where five
        /// are wanted, and reject every real symbol on the page.
        /// </remarks>
        private static bool ConfirmVertically(
            byte[] pixels, int width, int height, int stride, int threshold,
            int x, int y, double module, out double centreY)
        {
            centreY = 0;
            if (!CrossCheck(pixels, width, height, stride, threshold, x, y, vertical: true, module,
                            out int centreTop, out int centreBottom))
                return false;
            centreY = (centreTop + centreBottom) / 2.0;
            return true;
        }

        /// <summary>
        /// The same check along the row through (<paramref name="x"/>, <paramref name="y"/>), and the
        /// central block's middle on it — what a rotated finder needs: the row that found it crossed
        /// the central block off its middle, so its first estimate of the centre is off along the row.
        /// </summary>
        private static bool ConfirmHorizontally(
            byte[] pixels, int width, int height, int stride, int threshold,
            int x, int y, double module, out double centreX)
        {
            centreX = 0;
            if (!CrossCheck(pixels, width, height, stride, threshold, x, y, vertical: false, module,
                            out int centreLeft, out int centreRight))
                return false;
            // Pixel edges, as the row scan's estimate is.
            centreX = (centreLeft + centreRight + 1) / 2.0;
            return true;
        }

        /// <summary>
        /// The finder's 1:1:3:1:1 proportion through (<paramref name="x"/>, <paramref name="y"/>) down its
        /// column or along its row, measured outward from the central block it stands in;
        /// <paramref name="first"/> and <paramref name="last"/> are that block's ends on the axis.
        /// </summary>
        private static bool CrossCheck(
            byte[] pixels, int width, int height, int stride, int threshold,
            int x, int y, bool vertical, double module, out int first, out int last)
        {
            first = last = 0;
            if (x < 0 || x >= width || y < 0 || y >= height) return false;
            if (!BarcodeScanline.IsDark(pixels, stride, x, y, threshold)) return false;
            int dx = vertical ? 0 : 1, dy = vertical ? 1 : 0;
            int at = vertical ? y : x, limit = vertical ? height : width;
            bool DarkAt(int p) => vertical ? BarcodeScanline.IsDark(pixels, stride, x, p, threshold)
                                           : BarcodeScanline.IsDark(pixels, stride, p, y, threshold);

            // The central block, extended both ways from the point we are standing on.
            first = at; last = at;
            while (first > 0 && DarkAt(first - 1)) first--;
            while (last < limit - 1 && DarkAt(last + 1)) last++;

            var lengths = new int[5];
            lengths[2] = last - first + 1;

            // Before it: the light ring, then the outer dark border; after it, the same mirrored.
            if (!Run(pixels, stride, x, y, dx, dy, width, height, threshold, first - 1, -1, false, out lengths[1])) return false;
            if (!Run(pixels, stride, x, y, dx, dy, width, height, threshold, first - 1 - lengths[1], -1, true, out lengths[0])) return false;
            if (!Run(pixels, stride, x, y, dx, dy, width, height, threshold, last + 1, 1, false, out lengths[3])) return false;
            if (!Run(pixels, stride, x, y, dx, dy, width, height, threshold, last + 1 + lengths[3], 1, true, out lengths[4])) return false;

            double total = 0;
            for (int i = 0; i < 5; i++) total += lengths[i];

            double measured = total / FinderModules;
            if (Math.Abs(measured - module) > RatioTolerance * module) return false;

            for (int i = 0; i < 5; i++)
                if (Math.Abs(lengths[i] - (FinderRatio[i] * measured)) > RatioTolerance * measured)
                    return false;

            return true;
        }

        /// <summary>
        /// The length of the run of <paramref name="wantDark"/> on the axis (<paramref name="dx"/>,
        /// <paramref name="dy"/>) through (<paramref name="x"/>, <paramref name="y"/>), starting at
        /// position <paramref name="from"/> on it and going in <paramref name="step"/>, or
        /// <see langword="false"/> if there is none.
        /// </summary>
        private static bool Run(
            byte[] pixels, int stride, int x, int y, int dx, int dy, int width, int height, int threshold,
            int from, int step, bool wantDark, out int length)
        {
            length = 0;
            int at = from, limit = dy != 0 ? height : width;

            while (at >= 0 && at < limit
                   && BarcodeScanline.IsDark(pixels, stride, dy != 0 ? x : at, dy != 0 ? at : y, threshold) == wantDark)
            {
                length++;
                at += step;
            }

            return length > 0;
        }

        /// <summary>Adds a centre unless one is already recorded within a module of it, on both axes.</summary>
        /// <remarks>
        /// A finder is several modules tall, so every row crossing it produces the same candidate.
        /// Merging here keeps the pairing loop over three finders quadratic in symbols rather than
        /// in scan lines.
        /// </remarks>
        private static void Add(List<Finder> finders, Finder candidate)
        {
            foreach (Finder existing in finders)
                if (Math.Abs(existing.X - candidate.X) < candidate.Module
                    && Math.Abs(existing.Y - candidate.Y) < candidate.Module)
                    return;

            finders.Add(candidate);
        }

    }
}
