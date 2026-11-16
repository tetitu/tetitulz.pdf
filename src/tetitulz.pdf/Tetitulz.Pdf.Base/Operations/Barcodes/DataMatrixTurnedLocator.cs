// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Finds Data Matrix symbols turned on the page — any angle, a skewed scan, a label set sideways — and
    /// samples each into a module grid. <see cref="DataMatrixLocator"/> reads the square ones.
    /// </summary>
    /// <remarks>
    /// <para><b>The L is still the handle, found by its outline.</b> The solid finder column and row are one
    /// connected piece of ink whose two outer sides are the symbol's own edges, so in the convex hull of
    /// that piece they are two long edges meeting at a right angle at the symbol's corner. Data modules
    /// joined to the L lie inside the symbol and leave those two edges alone. A rasterised edge at an
    /// angle is a staircase, which the hull turns into a few nearly collinear edges; vertices turning less
    /// than <see cref="StraightTurn"/> are dropped, so a straight edge is one edge again.</para>
    ///
    /// <para><b>Then the usual Data Matrix detector's steps, re-derived:</b> the module size from
    /// the arms' thickness, the solid arms confirmed along their middles, every ECC-200 size whose
    /// proportions the arms fit tried, the fourth corner completing the parallelogram, each module
    /// sampled at its centre through the transform the four corners define. Whether a candidate is a
    /// symbol is the decoder's to say, as for every other candidate.</para>
    /// </remarks>
    internal static class DataMatrixTurnedLocator
    {
        /// <summary>Smallest module size, in pixels, that can be sampled reliably.</summary>
        private const double MinModulePixels = 1.5;

        /// <summary>The fewest modules an arm spans: the smallest symbol, 8×18 or 10×10, has eight.</summary>
        private const int MinArmModules = 8;

        /// <summary>How far, in radians, a hull vertex may turn and still lie on a straight edge.</summary>
        private const double StraightTurn = 12 * Math.PI / 180;

        /// <summary>How far the left arm may differ from the length a size's proportions predict, as a fraction — the square locator's tenth.</summary>
        private const double ArmLengthTolerance = 0.1;

        /// <summary>The share of an arm's middle line that must be ink.</summary>
        private const double SolidFraction = 0.9;

        /// <summary>
        /// Most pieces of ink examined. A page of text is thousands of pieces, each cheap; this bounds a page
        /// of scattered dots. Each pixel is visited once whatever the bound, and a piece is kept as its rows'
        /// ends alone, so no piece's size needs a bound of its own.
        /// </summary>
        internal const int MaxComponents = 8192;

        /// <summary>The candidates offered on a page where none is read, in the order offered.</summary>
        internal static List<(bool[,] Grid, int Left, int Top, int Right, int Bottom)> Locate(byte[] darkMap, int width, int height)
            => Locate(darkMap, width, height, out _, out _);

        /// <summary>
        /// The same, with how many pieces of ink were examined — the component bound's evidence — and how
        /// many Ls passed every check and were sampled, the checks' own.
        /// </summary>
        internal static List<(bool[,] Grid, int Left, int Top, int Right, int Bottom)> Locate(byte[] darkMap, int width, int height,
            out int componentsExamined, out int lsSampled)
        {
            var found = new List<(bool[,], int, int, int, int)>();
            Locate(darkMap, width, height, candidate => { found.Add(candidate); return false; }, new CandidateBudget(),
                   out componentsExamined, out lsSampled);
            return found;
        }

        /// <summary>
        /// Every grid this can sample out of the page, offered to <paramref name="accept"/>, which answers whether
        /// the grid was read; an L's other sizes are not offered once one is, and the search stops when the grids not
        /// read have spent the <see cref="CandidateBudget"/>.
        /// </summary>
        internal static void Locate(byte[] darkMap, int width, int height,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept)
            => Locate(darkMap, width, height, accept, new CandidateBudget(), out _, out _);

        /// <summary>The same, spending <paramref name="budget"/> — the bound's evidence.</summary>
        internal static void Locate(byte[] darkMap, int width, int height,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept, CandidateBudget budget)
            => Locate(darkMap, width, height, accept, budget, out _, out _);

        private static void Locate(byte[] darkMap, int width, int height,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept, CandidateBudget budget,
            out int componentsExamined, out int lsSampled)
        {
            var grids = new List<(bool[,], int, int, int, int)>();
            var seen = new byte[darkMap.Length];
            var ends = new Dictionary<int, (int Min, int Max)>();
            var queue = new Queue<int>();
            int components = 0, candidates = 0;
            int minSide = (int)(MinArmModules * MinModulePixels);

            for (int start = 0; start < darkMap.Length && components < MaxComponents
                 && !budget.Spent; start++)
            {
                if (darkMap[start] == 0 || seen[start] != 0) continue;
                components++;
                Component(darkMap, width, height, seen, start, ends, queue, out int left, out int top, out int right, out int bottom);

                List<(double X, double Y)> hull = Straightened(Hull(ends));
                if (hull.Count < 3) continue;
                foreach (var corner in Corners(hull, minSide))
                {
                    if (!Arms(darkMap, width, height, corner, out var frame)) continue;
                    candidates++;
                    grids.Clear();
                    Sample(darkMap, width, height, frame, grids);
                    foreach (var grid in grids)
                    {
                        if (accept(grid)) break;
                        budget.Fail(grid.Item1.GetLength(0), grid.Item1.GetLength(1));
                    }
                    // The loop's own test stops the search at the next piece of ink: a convex hull has at most four
                    // corners near a right angle, so a piece's remaining Ls are all the overshoot can be.
                }
            }
            componentsExamined = components;
            lsSampled = candidates;
        }

        /// <summary>
        /// The 8-connected piece of ink holding <paramref name="start"/>, as each row's leftmost and rightmost
        /// pixel in <paramref name="ends"/> — all a convex hull needs — with its box.
        /// </summary>
        private static void Component(byte[] dark, int width, int height, byte[] seen, int start,
            Dictionary<int, (int Min, int Max)> ends, Queue<int> queue, out int left, out int top, out int right, out int bottom)
        {
            ends.Clear();
            queue.Clear();
            left = top = int.MaxValue; right = bottom = int.MinValue;
            seen[start] = 1;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                int x = at % width, y = at / width;
                ends[y] = ends.TryGetValue(y, out var e) ? (Math.Min(e.Min, x), Math.Max(e.Max, x)) : (x, x);
                if (x < left) left = x; if (x > right) right = x;
                if (y < top) top = y; if (y > bottom) bottom = y;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= width) continue;
                        int n = (ny * width) + nx;
                        if (dark[n] == 0 || seen[n] != 0) continue;
                        seen[n] = 1;
                        queue.Enqueue(n);
                    }
                }
            }
        }

        /// <summary>
        /// The convex hull of a piece's rows' end pixels' centres — only a row's leftmost and rightmost pixel
        /// can be on it — by Andrew's monotone chain.
        /// </summary>
        private static List<(double X, double Y)> Hull(Dictionary<int, (int Min, int Max)> ends)
        {
            var points = new List<(double X, double Y)>(ends.Count * 2);
            foreach (var kvp in ends)
            {
                points.Add((kvp.Value.Min + 0.5, kvp.Key + 0.5));
                if (kvp.Value.Max != kvp.Value.Min) points.Add((kvp.Value.Max + 0.5, kvp.Key + 0.5));
            }
            points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            if (points.Count < 3) return points;

            var hull = new List<(double X, double Y)>(points.Count + 1);
            for (int pass = 0; pass < 2; pass++)
            {
                int floor = hull.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[pass == 0 ? i : points.Count - 1 - i];
                    while (hull.Count >= floor + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
            => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

        /// <summary>The hull with every vertex that turns less than <see cref="StraightTurn"/> dropped, repeatedly.</summary>
        private static List<(double X, double Y)> Straightened(List<(double X, double Y)> hull)
        {
            bool changed = true;
            while (changed && hull.Count > 3)
            {
                changed = false;
                for (int i = 0; i < hull.Count && hull.Count > 3; i++)
                {
                    var p = hull[(i + hull.Count - 1) % hull.Count];
                    var v = hull[i];
                    var n = hull[(i + 1) % hull.Count];
                    if (Turn(p, v, n) < StraightTurn) { hull.RemoveAt(i); changed = true; i--; }
                }
            }
            return hull;
        }

        /// <summary>How far, in radians, the path p → v → n turns at v.</summary>
        private static double Turn((double X, double Y) p, (double X, double Y) v, (double X, double Y) n)
        {
            double ax = v.X - p.X, ay = v.Y - p.Y, bx = n.X - v.X, by = n.Y - v.Y;
            double la = Math.Sqrt((ax * ax) + (ay * ay)), lb = Math.Sqrt((bx * bx) + (by * by));
            if (la == 0 || lb == 0) return 0;
            double cos = ((ax * bx) + (ay * by)) / (la * lb);
            return Math.Acos(Math.Max(-1, Math.Min(1, cos)));
        }

        /// <summary>
        /// Where two hull edges at least <paramref name="minArm"/> long meet: the
        /// corner the two edges' lines cross at, and each edge's far end. A rasterised corner is chamfered —
        /// its quarter turn is spread over a few edges a pixel or two long — so the long edges are paired
        /// across whatever short ones lie between them, up to <see cref="MaxChamfer"/> of them in all.
        /// </summary>
        private static IEnumerable<((double X, double Y) Corner, (double X, double Y) A, (double X, double Y) B)> Corners(
            List<(double X, double Y)> hull, int minArm)
        {
            int count = hull.Count;
            var longEdges = new List<int>();   // edge i runs from hull[i] to hull[i + 1]
            for (int i = 0; i < count; i++)
                if (Length(hull[i], hull[(i + 1) % count]) >= minArm) longEdges.Add(i);
            for (int k = 0; k < longEdges.Count && longEdges.Count >= 2; k++)
            {
                int first = longEdges[k], second = longEdges[(k + 1) % longEdges.Count];
                double between = 0;
                for (int e = (first + 1) % count; e != second; e = (e + 1) % count)
                    between += Length(hull[e], hull[(e + 1) % count]);
                if (between > MaxChamfer) continue;

                var a0 = hull[first]; var a1 = hull[(first + 1) % count];
                var b0 = hull[second]; var b1 = hull[(second + 1) % count];
                if (!Intersection(a0, a1, b0, b1, out var corner)) continue;
                yield return (corner, a0, b1);
            }
        }

        /// <summary>The longest run of short hull edges between two arms that is still a chamfered corner, in pixels.</summary>
        private const double MaxChamfer = 6;

        /// <summary>Where the lines through (a0, a1) and (b0, b1) cross; <see langword="false"/> when they are parallel.</summary>
        private static bool Intersection((double X, double Y) a0, (double X, double Y) a1, (double X, double Y) b0, (double X, double Y) b1,
            out (double X, double Y) point)
        {
            point = default;
            double rx = a1.X - a0.X, ry = a1.Y - a0.Y, sx = b1.X - b0.X, sy = b1.Y - b0.Y;
            double denominator = (rx * sy) - (ry * sx);
            if (Math.Abs(denominator) < 1e-9) return false;
            double t = (((b0.X - a0.X) * sy) - ((b0.Y - a0.Y) * sx)) / denominator;
            point = (a0.X + (t * rx), a0.Y + (t * ry));
            return true;
        }

        private static double Length((double X, double Y) a, (double X, double Y) b)
            => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

        /// <summary>A candidate L, in the symbol's own frame.</summary>
        private struct Frame
        {
            internal double CornerX, CornerY;      // the outer bottom-left corner
            internal double BottomX, BottomY;      // unit vector along the bottom arm (the symbol's +x)
            internal double LeftX, LeftY;          // unit vector up the left arm (the symbol's -y)
            internal double BottomLength, LeftLength, Module;
        }

        /// <summary>
        /// Whether the corner's two edges are the L — the bottom arm and the left, each as thick as a module
        /// and solid along its middle.
        /// </summary>
        private static bool Arms(byte[] dark, int width, int height,
            ((double X, double Y) Corner, (double X, double Y) A, (double X, double Y) B) corner, out Frame frame)
        {
            frame = default;
            var (v, a, b) = corner;
            double ax = a.X - v.X, ay = a.Y - v.Y, bx = b.X - v.X, by = b.Y - v.Y;
            double la = Math.Sqrt((ax * ax) + (ay * ay)), lb = Math.Sqrt((bx * bx) + (by * by));
            ax /= la; ay /= la; bx /= lb; by /= lb;
            // The hull runs one way round, so at the L's corner the edge before it is the bottom arm and the
            // edge after it the left one, at every angle; a mirrored symbol, whose L winds the other way, is
            // read transposed and refused by the decoder.
            double bottomX = ax, bottomY = ay, bottomLength = la, leftX = bx, leftY = by, leftLength = lb;

            // Inward from the bottom arm is up the left arm's direction, and from the left arm along the bottom's.
            double moduleB = Thickness(dark, width, height, v.X, v.Y, bottomX, bottomY, bottomLength, leftX, leftY);
            double moduleL = Thickness(dark, width, height, v.X, v.Y, leftX, leftY, leftLength, bottomX, bottomY);
            double module = Math.Min(moduleB, moduleL);
            if (!(module >= MinModulePixels)) return false;
            if (bottomLength < MinArmModules * module || leftLength < MinArmModules * module) return false;
            if (!Solid(dark, width, height, v.X, v.Y, bottomX, bottomY, bottomLength, leftX, leftY, module)
                || !Solid(dark, width, height, v.X, v.Y, leftX, leftY, leftLength, bottomX, bottomY, module))
                return false;

            // The left arm ends where its middle line meets paper. Its top is where the top clock track's first,
            // dark, module joins it, and the hull bends short there by a pixel or two, which a small module
            // cannot spare. The bottom arm's end meets the right clock track's light module, so the hull runs
            // true to it: its length is the hull's, out to the edge of the tip pixel.
            frame = new Frame
            {
                CornerX = v.X, CornerY = v.Y,
                BottomX = bottomX, BottomY = bottomY, LeftX = leftX, LeftY = leftY,
                BottomLength = bottomLength + 1,
                LeftLength = ArmEnd(dark, width, height, v, leftX, leftY, bottomX, bottomY, leftLength, module),
                Module = module,
            };
            return true;
        }

        /// <summary>
        /// Where an arm ends: along its middle — half a module in from the corner, the other arm's way — from
        /// four fifths of the hull's length on, the first point on paper.
        /// </summary>
        private static double ArmEnd(byte[] dark, int width, int height, (double X, double Y) corner,
            double alongX, double alongY, double inX, double inY, double hullLength, double module)
        {
            double ox = corner.X + (inX * module / 2), oy = corner.Y + (inY * module / 2);
            double t = 0.8 * hullLength;
            while (t <= hullLength + (3 * module) && Dark(dark, width, height, ox + (alongX * t), oy + (alongY * t))) t += 0.25;
            return t;
        }

        /// <summary>
        /// An arm's thickness, the least of seven measurements across it, an eighth of its length apart,
        /// walking inward (<paramref name="inX"/>, <paramref name="inY"/>) from its
        /// outer edge until the ink ends. A data module joined to the arm thickens one; the least is the arm's own.
        /// </summary>
        private static double Thickness(byte[] dark, int width, int height, double x, double y,
            double alongX, double alongY, double length, double inX, double inY)
        {
            double least = double.MaxValue;
            for (int q = 1; q <= 7; q++)
            {
                double sx = x + (alongX * length * q / 8), sy = y + (alongY * length * q / 8);
                // The hull's edge is a chord over a turned edge's staircase, so it can lie a pixel or two
                // outside the ink: the measurement starts where the ink does.
                double from = 0;
                while (from <= 2 && !Dark(dark, width, height, sx + (inX * from), sy + (inY * from))) from += 0.5;
                if (from > 2) continue;
                double t = from;
                while (t < length && Dark(dark, width, height, sx + (inX * t), sy + (inY * t))) t += 0.5;
                least = Math.Min(least, t - from);
            }
            return least;
        }

        /// <summary>Whether the arm's middle line, half a module in from its edge, is ink for <see cref="SolidFraction"/> of its length.</summary>
        private static bool Solid(byte[] dark, int width, int height, double x, double y,
            double alongX, double alongY, double length, double inX, double inY, double module)
        {
            double ox = x + (inX * module / 2), oy = y + (inY * module / 2);
            int samples = 0, ink = 0;
            for (double t = 0; t < length; t += 1)
            {
                samples++;
                if (Dark(dark, width, height, ox + (alongX * t), oy + (alongY * t))) ink++;
            }
            return samples > 0 && ink >= SolidFraction * samples;
        }

        private static bool Dark(byte[] dark, int width, int height, double x, double y)
        {
            int ix = SaturatingCast.ToInt(Math.Floor(x)), iy = SaturatingCast.ToInt(Math.Floor(y));
            return ix >= 0 && iy >= 0 && ix < width && iy < height && dark[(iy * width) + ix] != 0;
        }

        /// <summary>
        /// Samples the L: at the size its clock tracks count, through the fourth corner they meet at, when both
        /// tracks are traced (<see cref="Traced"/>) — a symbol under perspective, whose fourth corner is not the
        /// parallelogram's and whose arms do not stand in its size's proportions — else at every ECC-200 size its
        /// arms fit, the fourth corner completing the parallelogram.
        /// </summary>
        private static void Sample(byte[] dark, int width, int height, Frame f, List<(bool[,], int, int, int, int)> found)
        {
            double tlX = f.CornerX + (f.LeftX * f.LeftLength), tlY = f.CornerY + (f.LeftY * f.LeftLength);
            double brX = f.CornerX + (f.BottomX * f.BottomLength), brY = f.CornerY + (f.BottomY * f.BottomLength);
            if (Traced(dark, width, height, f, (tlX, tlY), (brX, brY), out int tracedRows, out int tracedColumns, out var topRight))
                for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
                    if (DataMatrixLayout.Rows(symbol) == tracedRows && DataMatrixLayout.Columns(symbol) == tracedColumns
                        && SampleAt(dark, width, height, f, tracedRows, tracedColumns, (tlX, tlY), topRight, (brX, brY), found))
                        return;
            SampleByProportion(dark, width, height, f, found);
        }

        /// <summary>
        /// The symbol's size and its fourth corner from its two clock tracks, as detectors correct its
        /// fourth corner by the tracks' transitions, or follow the tracks' gaps and fit a line to
        /// each: the top track traced from the left arm's end along the bottom arm's way, the right one from the
        /// bottom arm's end up the left arm's, each the outer edge of its dark modules (<see cref="TraceTrack"/>);
        /// the corner where the two edges cross; the size twice each track's dark modules.
        /// </summary>
        private static bool Traced(byte[] dark, int width, int height, Frame f, (double X, double Y) topLeft, (double X, double Y) bottomRight,
            out int rows, out int columns, out (double X, double Y) topRight)
        {
            rows = columns = 0;
            topRight = default;
            // Up the left arm is the top track's outward side; along the bottom arm the right track's.
            if (!TraceTrack(dark, width, height, topLeft, (f.BottomX, f.BottomY), (f.LeftX, f.LeftY), f.Module, 2 * f.BottomLength,
                    out var top, out int topDark)
                || !TraceTrack(dark, width, height, bottomRight, (f.LeftX, f.LeftY), (f.BottomX, f.BottomY), f.Module, 2 * f.LeftLength,
                    out var right, out int rightDark))
                return false;
            if (!Intersection(top.Point, (top.Point.X + top.Direction.X, top.Point.Y + top.Direction.Y),
                    right.Point, (right.Point.X + right.Direction.X, right.Point.Y + right.Direction.Y), out topRight))
                return false;
            columns = 2 * topDark;
            rows = 2 * rightDark;
            return true;
        }

        /// <summary>A fitted edge: a point on it and its unit direction.</summary>
        private struct EdgeLine
        {
            internal (double X, double Y) Point, Direction;
        }

        /// <summary>
        /// Follows a clock track from <paramref name="start"/>, the outer corner of its first, dark, module: along
        /// a line half a module inside the edge, its runs of ink and paper are the track's modules; each dark
        /// module's outer edge, found from its middle outward, is a point of the edge, and the edge is refitted
        /// through them as they come (a least-squares line), so the walk bends with a perspective. It ends at a
        /// run of paper longer than two modules — the light corner module and the quiet zone past it — or at
        /// <paramref name="maxLength"/>. <see langword="false"/> for fewer than <see cref="MinTrackDarkModules"/>
        /// dark modules, too few for the smallest symbol's track.
        /// </summary>
        private static bool TraceTrack(byte[] dark, int width, int height, (double X, double Y) start, (double X, double Y) along,
            (double X, double Y) outward, double module, double maxLength, out EdgeLine edge, out int darkModules)
        {
            const double Step = 0.25;
            darkModules = 0;
            var line = FindEdge(dark, width, height, start, along, outward, 0.6 * maxLength / 2);
            var points = new List<(double X, double Y)> { start };
            // Two sizes: the walk runs half the arms' thickness inside the edge, a module across it; a module ALONG
            // the track, which perspective stretches or shrinks apart from that, is the runs' own length, and says
            // when paper has gone on too long.
            double depth = module / 2, moduleAlong = module, runStart = 0;
            bool inInk = true;
            for (double t = Step; t <= maxLength; t += Step)
            {
                // The outward normal of the current edge, on the side the arm points.
                var (nx, ny) = Normal(line.Direction, outward);
                var origin = Project(start, line);
                bool InkAt(double at) => Dark(dark, width, height,
                    origin.X + (line.Direction.X * at) - (nx * depth), origin.Y + (line.Direction.Y * at) - (ny * depth));
                bool ink = InkAt(t);
                if (ink == inInk)
                {
                    if (!ink && t - runStart > 2.2 * moduleAlong) break;
                    continue;
                }
                // A change lasting less than a third of a module (a pixel at least) is a turned edge's staircase
                // grazed, not a module: the run goes on.
                double settle = Math.Max(1, module / 3);
                bool lasts = true;
                for (double k = Step; k < settle && lasts; k += Step) lasts = InkAt(t + k) == ink;
                if (!lasts) continue;
                double run = t - runStart;
                if (inInk)
                {
                    // A dark module ends: its outer edge, from its middle outward, is a point of the track's edge.
                    darkModules++;
                    double mid = runStart + (run / 2);
                    double mx = origin.X + (line.Direction.X * mid) - (nx * depth);
                    double my = origin.Y + (line.Direction.Y * mid) - (ny * depth);
                    double s = 0;
                    while (s <= 2 * module && Dark(dark, width, height, mx + (nx * s), my + (ny * s))) s += Step;
                    if (s <= 2 * module)
                    {
                        points.Add((mx + (nx * s), my + (ny * s)));
                        // Refitted once the points span three modules: over a shorter base a pixel of noise turns the line.
                        if (Length(points[0], points[points.Count - 1]) >= 3 * moduleAlong) line = Fit(points, along);
                    }
                }
                // The module along the track: the runs averaged as they come.
                moduleAlong = (moduleAlong + run) / 2;
                runStart = t;
                inInk = ink;
            }
            if (inInk) darkModules++;   // the walk ran out inside a module
            edge = line;
            return darkModules >= MinTrackDarkModules;
        }

        /// <summary>
        /// The outer edge of a clock track at <paramref name="start"/>. Under perspective its direction is not
        /// the opposite arm's — a keystone tilts the two apart, a short rectangle's by fifty degrees and more — but
        /// an edge stays straight, so of the lines through the start within <see cref="EdgeSearchDegrees"/> of
        /// <paramref name="near"/>, the edge is the one with paper just outside it and the track's dark modules
        /// just inside: the most ink a pixel in, less twice the ink a pixel and a half out, over
        /// <paramref name="length"/>.
        /// </summary>
        private static EdgeLine FindEdge(byte[] dark, int width, int height, (double X, double Y) start,
            (double X, double Y) near, (double X, double Y) outward, double length)
        {
            var best = near;
            double bestScore = double.MinValue, baseAngle = Math.Atan2(near.Y, near.X);
            for (double degrees = -EdgeSearchDegrees; degrees <= EdgeSearchDegrees; degrees += 0.5)
            {
                double a = baseAngle + (degrees * Math.PI / 180);
                (double X, double Y) d = (Math.Cos(a), Math.Sin(a));
                var (nx, ny) = Normal(d, outward);
                int inside = 0, outside = 0;
                for (double t = 1; t < length; t += 1)
                {
                    double x = start.X + (d.X * t), y = start.Y + (d.Y * t);
                    if (Dark(dark, width, height, x - nx, y - ny)) inside++;
                    if (Dark(dark, width, height, x + (1.5 * nx), y + (1.5 * ny))) outside++;
                }
                double score = inside - (2.0 * outside);
                if (score > bestScore) { bestScore = score; best = d; }
            }
            return new EdgeLine { Point = start, Direction = best };
        }
        /// <summary>How far, in degrees, a clock track's edge may turn from the opposite arm under perspective.</summary>
        private const double EdgeSearchDegrees = 60;

        /// <summary>The fewest dark modules a clock track has: the 8×18 symbol's right one, four.</summary>
        private const int MinTrackDarkModules = 4;

        private static (double X, double Y) Normal((double X, double Y) direction, (double X, double Y) side)
        {
            double nx = -direction.Y, ny = direction.X;
            return (nx * side.X) + (ny * side.Y) >= 0 ? (nx, ny) : (-nx, -ny);
        }

        private static (double X, double Y) Project((double X, double Y) p, EdgeLine line)
        {
            double d = ((p.X - line.Point.X) * line.Direction.X) + ((p.Y - line.Point.Y) * line.Direction.Y);
            return (line.Point.X + (line.Direction.X * d), line.Point.Y + (line.Direction.Y * d));
        }

        /// <summary>The least-squares line through <paramref name="points"/> (their principal axis), its direction <paramref name="along"/>'s way.</summary>
        private static EdgeLine Fit(List<(double X, double Y)> points, (double X, double Y) along)
        {
            double cx = 0, cy = 0;
            foreach (var p in points) { cx += p.X; cy += p.Y; }
            cx /= points.Count; cy /= points.Count;
            double sxx = 0, sxy = 0, syy = 0;
            foreach (var p in points)
            {
                double dx = p.X - cx, dy = p.Y - cy;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            double ux = Math.Cos(angle), uy = Math.Sin(angle);
            if ((ux * along.X) + (uy * along.Y) < 0) { ux = -ux; uy = -uy; }
            return new EdgeLine { Point = (cx, cy), Direction = (ux, uy) };
        }

        /// <summary>Samples one size through the four corners; <see langword="false"/> when a module falls off the page.</summary>
        private static bool SampleAt(byte[] dark, int width, int height, Frame f, int rows, int columns,
            (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, List<(bool[,], int, int, int, int)> found)
        {
            // Module (0, 0) is the top-left: up the left arm; (columns, rows) the bottom arm's end.
            var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
                0, 0, columns, 0, columns, rows, 0, rows,
                tl.X, tl.Y, tr.X, tr.Y, br.X, br.Y, f.CornerX, f.CornerY);
            var grid = new bool[rows, columns];
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    transform.Apply(column + 0.5, row + 0.5, out double x, out double y);
                    int ix = SaturatingCast.ToInt(Math.Floor(x)), iy = SaturatingCast.ToInt(Math.Floor(y));
                    if (ix < 0 || iy < 0 || ix >= width || iy >= height) return false;
                    grid[row, column] = dark[(iy * width) + ix] != 0;
                }
            double left = Math.Min(Math.Min(tl.X, tr.X), Math.Min(br.X, f.CornerX));
            double top = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(br.Y, f.CornerY));
            double right = Math.Max(Math.Max(tl.X, tr.X), Math.Max(br.X, f.CornerX));
            double bottom = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(br.Y, f.CornerY));
            found.Add((grid, SaturatingCast.ToInt(left), SaturatingCast.ToInt(top), SaturatingCast.ToInt(Math.Ceiling(right)), SaturatingCast.ToInt(Math.Ceiling(bottom))));
            return true;
        }

        /// <summary>Samples the L at every ECC-200 size its arms fit, the fourth corner completing the parallelogram.</summary>
        private static void SampleByProportion(byte[] dark, int width, int height, Frame f, List<(bool[,], int, int, int, int)> found)
        {
            // A size is tried when the arms stand in its proportions and the module it implies — an arm's
            // length over its modules, exact where the measured thickness is a pixel or so either way — is
            // near the thickness: as the square locator tries sizes, by proportion first.
            for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
            {
                int rows = DataMatrixLayout.Rows(symbol), columns = DataMatrixLayout.Columns(symbol);
                double expectedLeft = f.BottomLength * rows / columns;
                if (Math.Abs(f.LeftLength - expectedLeft) > ArmLengthTolerance * expectedLeft) continue;
                double implied = f.BottomLength / columns;
                if (implied < 0.6 * f.Module || implied > 1.6 * f.Module || implied < MinModulePixels) continue;

                double tlX = f.CornerX + (f.LeftX * f.LeftLength), tlY = f.CornerY + (f.LeftY * f.LeftLength);
                double brX = f.CornerX + (f.BottomX * f.BottomLength), brY = f.CornerY + (f.BottomY * f.BottomLength);
                SampleAt(dark, width, height, f, rows, columns, (tlX, tlY), (tlX + brX - f.CornerX, tlY + brY - f.CornerY), (brX, brY), found);
            }
        }
    }
}
