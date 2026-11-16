// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A TrueType program's glyphs (OpenType <c>glyf</c>): each glyph's contours — quadratic
    /// segments raised to cubics, an implied on-curve point between two off-curve ones, a composite's
    /// components placed by their offsets or matched points and transformed — and its <c>hmtx</c>
    /// advance, both in the 1000-units-per-em glyph space a PDF font dictionary uses. What a PostScript
    /// interpreter needs to append a standard face's glyph path (<c>charpath</c>) from the face a host
    /// draws it with.
    /// </summary>
    /// <remarks>
    /// <para><b>A program's counts are hostile input.</b> Every read is bounded by the glyph's own
    /// bytes, as <see cref="CompositeGlyph"/>'s walk is; a contour's end points ascend or the glyph is
    /// refused; a composite nests at most <see cref="PdfLimits.MaxCompositeGlyphDepth"/> deep, and the
    /// points a glyph expands to — a composite may name the same component many times, at every
    /// level, so the count multiplies — stop at <see cref="PdfLimits.MaxGlyphOutlinePoints"/>, as do
    /// the glyphs read to place them (a composite of empty glyphs places none).</para>
    /// </remarks>
    public sealed class TrueTypeOutlines
    {
        private const int ArgsAreXyValues = 0x0002;
        private const int ScaledComponentOffset = 0x0800;

        private readonly TrueTypeFontProgram _program;
        private readonly byte[] _font;
        private readonly int _glyf, _glyfLength, _loca, _locaLength;
        private readonly bool _longLoca;
        private readonly double _scale;
        private readonly Dictionary<int, GlyphOutline> _glyphs = new Dictionary<int, GlyphOutline>();

        private TrueTypeOutlines(TrueTypeFontProgram program, int glyf, int glyfLength, int loca, int locaLength, bool longLoca)
        {
            _program = program;
            _font = program.FontData;
            _glyf = glyf; _glyfLength = glyfLength;
            _loca = loca; _locaLength = locaLength;
            _longLoca = longLoca;
            _scale = PdfUnits.GlyphSpaceUnitsPerEm / (double)program.UnitsPerEm;
        }

        /// <summary>
        /// The glyphs of <paramref name="program"/>, or null, with <paramref name="reason"/>, when it
        /// has no <c>glyf</c> outlines this library can read (a CFF-flavoured OpenType program, or one
        /// missing its <c>head</c>, <c>loca</c> or <c>glyf</c>).
        /// </summary>
        /// <param name="program">The parsed program.</param>
        /// <param name="reason">Why there are no outlines, or null.</param>
        public static TrueTypeOutlines TryRead(TrueTypeFontProgram program, out string reason)
        {
            reason = null;
            if (program?.FontData == null)
            {
                reason = "no font program";
                return null;
            }
            if (program.HasCff)
            {
                reason = "the program's outlines are CFF, not glyf";
                return null;
            }
            if (!SfntTableDirectory.TryRead(program.FontData, out IReadOnlyList<SfntTableDirectory.TableRecord> tables, out reason))
                return null;

            SfntTableDirectory.TableRecord? head = null, loca = null, glyf = null;
            foreach (SfntTableDirectory.TableRecord t in tables)
            {
                if (t.Tag == "head") head = t;
                else if (t.Tag == "loca") loca = t;
                else if (t.Tag == "glyf") glyf = t;
            }
            if (head == null || loca == null || glyf == null || head.Value.Length < 54)
            {
                reason = "the program has no head, loca or glyf table";
                return null;
            }

            short indexToLocFormat = (short)ReadU16(program.FontData, head.Value.Offset + 50);
            return new TrueTypeOutlines(program, glyf.Value.Offset, glyf.Value.Length,
                                        loca.Value.Offset, loca.Value.Length, indexToLocFormat == 1);
        }

        /// <summary>
        /// Glyph <paramref name="glyphId"/>, read once and kept; false when the program has no such
        /// glyph or its data cannot be read within the bounds above. An empty glyph (a space) is an
        /// outline with no segments.
        /// </summary>
        /// <param name="glyphId">The glyph's index, as the program's <c>cmap</c> gives it.</param>
        /// <param name="glyph">The glyph, or null.</param>
        public bool TryGetGlyph(int glyphId, out GlyphOutline glyph)
        {
            glyph = null;
            if (glyphId < 0 || glyphId >= _program.GlyphCount) return false;
            if (_glyphs.TryGetValue(glyphId, out glyph)) return glyph != null;

            var contours = new List<List<Point>>();
            int budget = PdfLimits.MaxGlyphOutlinePoints;
            int visits = PdfLimits.MaxGlyphOutlinePoints;
            glyph = AppendGlyph(glyphId, 0, contours, ref budget, ref visits)
                ? new GlyphOutline(ToSegments(contours), _program.GetAdvance((ushort)glyphId))
                : null;
            _glyphs[glyphId] = glyph;
            return glyph != null;
        }

        private struct Point
        {
            public double X, Y;
            public bool OnCurve;
        }

        /// <summary>
        /// Appends glyph <paramref name="glyphId"/>'s contours, in font units; false when it cannot be
        /// read. <paramref name="visits"/> counts the glyphs read, as <paramref name="budget"/> counts
        /// the points: a composite of empty glyphs places no point, and its fan-out multiplies too.
        /// </summary>
        private bool AppendGlyph(int glyphId, int depth, List<List<Point>> contours, ref int budget, ref int visits)
        {
            if (depth > PdfLimits.MaxCompositeGlyphDepth) return false;
            if (--visits < 0) return false;
            if (!TryLocate(glyphId, out int start, out int end)) return false;
            if (start == end) return true;              // an empty glyph
            if (end - start < 10) return false;

            short numberOfContours = (short)ReadU16(_font, start);
            return numberOfContours >= 0
                ? AppendSimple(start, end, numberOfContours, contours, ref budget)
                : AppendComposite(start, end, depth, contours, ref budget, ref visits);
        }

        private bool TryLocate(int glyphId, out int start, out int end)
        {
            start = end = 0;
            int entry = _longLoca ? 4 : 2;
            if ((long)(glyphId + 2) * entry > _locaLength) return false;
            long from = ReadLoca(glyphId), to = ReadLoca(glyphId + 1);
            if (from > to || to > _glyfLength) return false;
            start = _glyf + (int)from;
            end = _glyf + (int)to;
            return end <= _font.Length;
        }

        private long ReadLoca(int index) => _longLoca
            ? ReadU32(_font, _loca + index * 4)
            : ReadU16(_font, _loca + index * 2) * 2L;

        private bool AppendSimple(int start, int end, int numberOfContours, List<List<Point>> contours, ref int budget)
        {
            int p = start + 10;
            if (p + numberOfContours * 2 + 2 > end) return false;

            var endPoints = new int[numberOfContours];
            int previous = -1;
            for (int k = 0; k < numberOfContours; k++, p += 2)
            {
                endPoints[k] = ReadU16(_font, p);
                if (endPoints[k] <= previous) return false;        // end points ascend
                previous = endPoints[k];
            }
            int count = previous + 1;
            if (count > budget) return false;
            budget -= count;

            int instructionLength = ReadU16(_font, p);
            p += 2 + instructionLength;
            if (p > end) return false;

            var flags = new byte[count];
            for (int k = 0; k < count;)
            {
                if (p >= end) return false;
                byte flag = _font[p++];
                flags[k++] = flag;
                if ((flag & 0x08) != 0)                            // repeat
                {
                    if (p >= end) return false;
                    int repeat = _font[p++];
                    if (k + repeat > count) return false;
                    for (int r = 0; r < repeat; r++) flags[k++] = flag;
                }
            }

            var points = new Point[count];
            if (!ReadCoordinates(flags, points, ref p, end, xAxis: true)) return false;
            if (!ReadCoordinates(flags, points, ref p, end, xAxis: false)) return false;

            int first = 0;
            foreach (int last in endPoints)
            {
                var contour = new List<Point>(last - first + 1);
                for (int k = first; k <= last; k++) contour.Add(points[k]);
                contours.Add(contour);
                first = last + 1;
            }
            return true;
        }

        /// <summary>One axis of a simple glyph's points: a byte with its sign in the flag, or a delta word, or the same.</summary>
        private bool ReadCoordinates(byte[] flags, Point[] points, ref int p, int end, bool xAxis)
        {
            int shortFlag = xAxis ? 0x02 : 0x04, sameFlag = xAxis ? 0x10 : 0x20;
            int value = 0;
            for (int k = 0; k < flags.Length; k++)
            {
                int flag = flags[k];
                if ((flag & shortFlag) != 0)
                {
                    if (p >= end) return false;
                    int delta = _font[p++];
                    value += (flag & sameFlag) != 0 ? delta : -delta;
                }
                else if ((flag & sameFlag) == 0)
                {
                    if (p + 2 > end) return false;
                    value += (short)ReadU16(_font, p);
                    p += 2;
                }
                if (xAxis) { points[k].X = value; points[k].OnCurve = (flag & 0x01) != 0; }
                else points[k].Y = value;
            }
            return true;
        }

        private bool AppendComposite(int start, int end, int depth, List<List<Point>> contours, ref int budget, ref int visits)
        {
            // The points placed so far at the end of each contour, so a point-matched component
            // finds the composite's point by a binary search. The search ran from the first contour
            // each time, and a composite of a hundred thousand one-point components matching the
            // last point placed cost the square of that.
            var ends = new List<int>(contours.Count);
            int placed = 0;
            foreach (List<Point> contour in contours) { placed += contour.Count; ends.Add(placed); }

            foreach (CompositeGlyph.Component c in CompositeGlyph.Components(_font, start, end))
            {
                if (!c.Complete) return false;

                var own = new List<List<Point>>();
                if (!AppendGlyph(c.GlyphIndex, depth + 1, own, ref budget, ref visits)) return false;

                // The component's points through its 2 × 2 (x' = a·x + c·y, y' = b·x + d·y).
                foreach (List<Point> contour in own)
                    for (int k = 0; k < contour.Count; k++)
                    {
                        Point q = contour[k];
                        contour[k] = new Point { X = c.A * q.X + c.C * q.Y, Y = c.B * q.X + c.D * q.Y, OnCurve = q.OnCurve };
                    }

                double dx, dy;
                if ((c.Flags & ArgsAreXyValues) != 0)
                {
                    dx = c.Argument1;
                    dy = c.Argument2;
                    if ((c.Flags & ScaledComponentOffset) != 0)
                    {
                        double tx = c.A * dx + c.C * dy;
                        dy = c.B * dx + c.D * dy;
                        dx = tx;
                    }
                }
                else
                {
                    // Point matching: the component's point Argument2 is put on the composite's point
                    // Argument1, counted over the points placed so far.
                    if (!TryPlacedPoint(contours, ends, c.Argument1, out Point parent) || !TryPoint(own, c.Argument2, out Point child))
                        return false;
                    dx = parent.X - child.X;
                    dy = parent.Y - child.Y;
                }

                foreach (List<Point> contour in own)
                {
                    for (int k = 0; k < contour.Count; k++)
                    {
                        Point q = contour[k];
                        contour[k] = new Point { X = q.X + dx, Y = q.Y + dy, OnCurve = q.OnCurve };
                    }
                    contours.Add(contour);
                    placed += contour.Count;
                    ends.Add(placed);
                }
            }
            return true;
        }

        /// <summary>
        /// The composite's point <paramref name="index"/>, counted over the points placed so far:
        /// the first contour whose running total <paramref name="ends"/> passes it.
        /// </summary>
        private static bool TryPlacedPoint(List<List<Point>> contours, List<int> ends, int index, out Point point)
        {
            point = default;
            if (index < 0 || ends.Count == 0 || index >= ends[ends.Count - 1]) return false;
            int lo = 0, hi = ends.Count - 1;
            while (lo < hi)
            {
                MatchedPointSearchSteps++;
                int mid = (lo + hi) >> 1;
                if (ends[mid] > index) hi = mid; else lo = mid + 1;
            }
            point = contours[lo][index - (lo == 0 ? 0 : ends[lo - 1])];
            return true;
        }

        /// <summary>Steps <see cref="TryPlacedPoint"/> has taken on the calling thread: the evidence that a match is a search, not a scan.</summary>
        [System.ThreadStatic] internal static long MatchedPointSearchSteps;

        private static bool TryPoint(List<List<Point>> contours, int index, out Point point)
        {
            point = default;
            foreach (List<Point> contour in contours)
            {
                if (index < contour.Count)
                {
                    point = contour[index];
                    return true;
                }
                index -= contour.Count;
            }
            return false;
        }

        /// <summary>
        /// The contours as segments in 1000-unit glyph space: each from an on-curve point (or the
        /// point implied between two off-curve ones when it has none), every quadratic raised to the
        /// cubic with the same curve, every contour closed.
        /// </summary>
        private List<GlyphOutlineSegment> ToSegments(List<List<Point>> contours)
        {
            var segments = new List<GlyphOutlineSegment>();
            foreach (List<Point> contour in contours)
            {
                int n = contour.Count;
                if (n == 0) continue;

                int s = contour.FindIndex(q => q.OnCurve);
                var sequence = new List<Point>(n + 2);
                if (s >= 0)
                {
                    for (int k = 0; k <= n; k++) sequence.Add(contour[(s + k) % n]);
                }
                else
                {
                    Point mid = Mid(contour[n - 1], contour[0]);
                    sequence.Add(mid);
                    sequence.AddRange(contour);
                    sequence.Add(mid);
                }

                Point current = sequence[0];
                segments.Add(Segment(GlyphOutlineSegmentKind.MoveTo, current, current, current));
                Point? control = null;
                for (int k = 1; k < sequence.Count; k++)
                {
                    Point q = sequence[k];
                    bool closing = k == sequence.Count - 1;
                    if (q.OnCurve)
                    {
                        if (control is Point c) segments.Add(Quadratic(current, c, q));
                        else if (!closing) segments.Add(Segment(GlyphOutlineSegmentKind.LineTo, q, q, q));
                        control = null;
                        current = q;
                    }
                    else if (control is Point c)
                    {
                        Point mid = Mid(c, q);
                        segments.Add(Quadratic(current, c, mid));
                        current = mid;
                        control = q;
                    }
                    else control = q;
                }
                segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.ClosePath, 0, 0, 0, 0, 0, 0));
            }
            return segments;
        }

        private static Point Mid(Point a, Point b) => new Point { X = (a.X + b.X) / 2, Y = (a.Y + b.Y) / 2, OnCurve = true };

        private GlyphOutlineSegment Quadratic(Point from, Point control, Point to)
            => Segment(GlyphOutlineSegmentKind.CurveTo,
                       new Point { X = from.X + 2.0 / 3 * (control.X - from.X), Y = from.Y + 2.0 / 3 * (control.Y - from.Y) },
                       new Point { X = to.X + 2.0 / 3 * (control.X - to.X), Y = to.Y + 2.0 / 3 * (control.Y - to.Y) },
                       to);

        private GlyphOutlineSegment Segment(GlyphOutlineSegmentKind kind, Point c1, Point c2, Point end)
            => kind == GlyphOutlineSegmentKind.CurveTo
                ? new GlyphOutlineSegment(kind, c1.X * _scale, c1.Y * _scale, c2.X * _scale, c2.Y * _scale, end.X * _scale, end.Y * _scale)
                : new GlyphOutlineSegment(kind, 0, 0, 0, 0, end.X * _scale, end.Y * _scale);

        private static int ReadU16(byte[] data, int at) => IO.BigEndian.U16(data, at);

        private static long ReadU32(byte[] data, int at) => IO.BigEndian.U32(data, at);
    }
}
