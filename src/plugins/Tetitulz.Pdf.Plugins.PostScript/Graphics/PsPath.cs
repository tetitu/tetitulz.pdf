// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    internal enum PsSegmentKind : byte { Move, Line, Curve, Close }

    /// <summary>One path segment. Coordinates are in <b>user space</b> — see <see cref="PsPath"/>.</summary>
    internal readonly struct PsSegment
    {
        public readonly PsSegmentKind Kind;
        public readonly double X1, Y1, X2, Y2, X3, Y3;

        private PsSegment(PsSegmentKind kind, double x1, double y1, double x2, double y2,
                          double x3, double y3)
        {
            Kind = kind; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; X3 = x3; Y3 = y3;
        }

        public static PsSegment Move(double x, double y)  => new(PsSegmentKind.Move, 0, 0, 0, 0, x, y);
        public static PsSegment Line(double x, double y)  => new(PsSegmentKind.Line, 0, 0, 0, 0, x, y);
        public static PsSegment Close()                   => new(PsSegmentKind.Close, 0, 0, 0, 0, 0, 0);

        public static PsSegment Curve(double x1, double y1, double x2, double y2, double x3, double y3)
            => new(PsSegmentKind.Curve, x1, y1, x2, y2, x3, y3);

        /// <summary>This segment carried through <paramref name="m"/>, its kind kept.</summary>
        public PsSegment TransformedBy(in PsMatrix m)
        {
            switch (Kind)
            {
                case PsSegmentKind.Move:
                {
                    var (x, y) = m.Apply(X3, Y3);
                    return Move(x, y);
                }
                case PsSegmentKind.Line:
                {
                    var (x, y) = m.Apply(X3, Y3);
                    return Line(x, y);
                }
                case PsSegmentKind.Curve:
                {
                    var (x1, y1) = m.Apply(X1, Y1);
                    var (x2, y2) = m.Apply(X2, Y2);
                    var (x3, y3) = m.Apply(X3, Y3);
                    return Curve(x1, y1, x2, y2, x3, y3);
                }
                default:
                    return this;
            }
        }
    }

    /// <summary>
    /// The current path.
    ///
    /// <para><b>Segments are kept in user space and the path is kept whole until something paints
    /// it.</b> Both halves of that matter:</para>
    /// <list type="bullet">
    ///   <item><b>User space, not device space.</b> The emitter writes coordinates exactly as the
    ///     program wrote them and emits every CTM change as a <c>cm</c>. Baking the CTM into the
    ///     coordinates instead would destroy line widths, dash phases and non-uniform scaling, all
    ///     of which PDF derives from the CTM the same way PostScript does.</item>
    ///   <item><b>Whole, not streamed.</b> PostScript's <c>clip</c> intersects the clip with the
    ///     current path and <i>leaves the path intact</i>, while PDF's <c>W</c> marks a pending clip
    ///     that the next painting operator applies. Owning the segments means <c>clip</c> can emit
    ///     the path followed by <c>W n</c> and a subsequent <c>fill</c> can emit the identical path
    ///     again. Streaming operators as they were constructed would make that impossible.</item>
    /// </list>
    /// </summary>
    internal sealed class PsPath
    {
        private readonly List<PsSegment> _segments = new();

        /// <summary>The current point, or null when the path has none.</summary>
        public (double X, double Y)? CurrentPoint { get; private set; }

        /// <summary>Start of the current subpath, which <c>closepath</c> returns to.</summary>
        private (double X, double Y)? _subpathStart;

        public IReadOnlyList<PsSegment> Segments => _segments;

        /// <summary>What a segment held elsewhere — a saved path, a clip, a graphics state object — is charged: its kind and six coordinates.</summary>
        public const int BytesPerSegment = 56;
        public bool IsEmpty => _segments.Count == 0;
        public int Count => _segments.Count;

        /// <summary>
        /// Set by <c>strokepath</c>: the path is to be treated as <b>the outline of its own
        /// stroke</b> rather than as itself.
        ///
        /// <para><b>The outline is never constructed, and that is the design rather than a
        /// shortcut.</b> Building it means offsetting every segment by half the line width, mitring
        /// or rounding each join against the miter limit, capping each end, dashing first, and
        /// flattening every curve to do it — hundreds of lines of geometry whose output would then
        /// be <i>filled</i>. But PDF already has an operator that paints exactly that region from
        /// exactly this path: <c>S</c>. So the flag rides along until something consumes the path,
        /// and <see cref="PsGraphicsEngine.Fill"/> emits a stroke. The result is not an
        /// approximation of <c>strokepath fill</c> — it is byte-for-byte what <c>stroke</c> would
        /// have emitted, which is what <c>strokepath fill</c> means, with the dash pattern and line
        /// style honoured because they are read from the same graphics state.</para>
        ///
        /// <para>What that buys nothing for is named at each site rather than hidden:
        /// <c>strokepath clip</c> has no PDF expression at all (reported, and the clip is skipped —
        /// clipping to the un-outlined path would throw away everything after it), and
        /// <c>pathbbox</c> pads by half the line width instead of measuring a real outline.</para>
        /// </summary>
        public bool StrokeOutlinePending { get; set; }

        public void Clear()
        {
            _segments.Clear();
            CurrentPoint = null;
            _subpathStart = null;
            StrokeOutlinePending = false;
        }

        /// <summary>
        /// Drops a pending stroke outline because the path is being <i>extended</i>, which makes it
        /// no longer the outline of anything this can express. Reported: silently keeping the flag
        /// would stroke the extended path, and silently dropping it would fill it.
        /// </summary>
        private void InvalidateOutline()
        {
            if (!StrokeOutlinePending) return;
            StrokeOutlinePending = false;

            PostScriptDiagnostics.Report("PsPath",
                "the current path was extended after 'strokepath', so it is no longer the stroke " +
                "outline; the segments added since are treated as an ordinary path and the outline " +
                "was dropped");
        }

        /// <summary>
        /// Re-expresses every point of the path through <paramref name="m"/> — the correction
        /// <see cref="PsGraphicsEngine"/> applies when the CTM changes under a path, so the path
        /// stays where it was drawn (PLRM §4.4: the current path is in device space). The segment
        /// kinds, the subpath structure and the outline flag are untouched; only coordinates move.
        /// O(segments); the caller charges it.
        /// </summary>
        public void Transform(in PsMatrix m)
        {
            for (int k = 0; k < _segments.Count; k++) _segments[k] = _segments[k].TransformedBy(m);

            if (CurrentPoint.HasValue)
                CurrentPoint = m.Apply(CurrentPoint.Value.X, CurrentPoint.Value.Y);
            if (_subpathStart.HasValue)
                _subpathStart = m.Apply(_subpathStart.Value.X, _subpathStart.Value.Y);
            (_lastSubpathStartX, _lastSubpathStartY) = m.Apply(_lastSubpathStartX, _lastSubpathStartY);
        }

        /// <summary>
        /// Appends one segment, refusing past <see cref="PsLimits.MaxPathSegments"/>.
        ///
        /// <para>This was the one composite a program could grow with nothing watching — the
        /// mutators charge no allocation and tick no budget, so <c>{ 1 1 rlineto } loop</c> grew the
        /// list without limit. <c>limitcheck</c> rather than a silent drop, because a partially
        /// built path would paint a shape the program did not ask for; an error is catchable by
        /// <c>stopped</c> and recoverable at top level.</para>
        /// </summary>
        private void Append(in PsSegment segment)
        {
            if (_segments.Count >= PsLimits.MaxPathSegments)
                throw new Interpreter.PsErrorException(
                    Interpreter.PsErrorKind.LimitCheck, "path construction",
                    $"the path exceeds {PsLimits.MaxPathSegments} segments");

            _segments.Add(segment);
        }

        /// <summary>
        /// <c>moveto</c>. A moveto directly after a moveto replaces it (PLRM <c>moveto</c>), so a
        /// show's pen advances leave one segment, not one each — every CTM change re-expresses the
        /// whole path, and a page of shows and <c>concat</c>s was quadratic in them.
        /// </summary>
        public void MoveTo(double x, double y)
        {
            InvalidateOutline();
            int last = _segments.Count - 1;
            if (last >= 0 && _segments[last].Kind == PsSegmentKind.Move)
                _segments[last] = PsSegment.Move(x, y);
            else
                Append(PsSegment.Move(x, y));
            CurrentPoint = (x, y);
            _subpathStart = (x, y);
        }

        public void LineTo(double x, double y)
        {
            // PLRM: lineto with no current point is a nocurrentpoint error. The caller raises it;
            // reaching here without one would silently start a subpath at the wrong place.
            InvalidateOutline();
            Append(PsSegment.Line(x, y));
            CurrentPoint = (x, y);
        }

        public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            InvalidateOutline();
            Append(PsSegment.Curve(x1, y1, x2, y2, x3, y3));
            CurrentPoint = (x3, y3);
        }

        /// <summary>
        /// <c>closepath</c> — closes the subpath and returns the current point to its start, which
        /// is what makes a following <c>rlineto</c> measure from the right place.
        /// </summary>
        public void ClosePath()
        {
            if (_segments.Count == 0) return;
            Append(PsSegment.Close());
            if (_subpathStart.HasValue) CurrentPoint = _subpathStart;
        }

        /// <summary>
        /// Everything <see cref="Snapshot"/> has to put back.
        ///
        /// <para>Needed because the <c>rect*</c> operators are defined as
        /// <c>gsave newpath … grestore</c>, and <b>this engine's <c>gsave</c> does not save the
        /// path</b>: PostScript makes the current path part of the graphics state (PLRM §4.2) while
        /// PDF does not, so <see cref="PsGraphicsEngine"/> keeps one path outside the state stack.
        /// A <c>rectfill</c> that cleared the path would therefore destroy a path the program is
        /// still building, which is precisely what the operator promises not to do.</para>
        /// </summary>
        internal readonly struct Snapshotted
        {
            public readonly PsSegment[] Segments;
            public readonly (double X, double Y)? CurrentPoint;
            public readonly (double X, double Y)? SubpathStart;
            public readonly bool OutlinePending;

            public Snapshotted(PsSegment[] segments, (double X, double Y)? currentPoint,
                               (double X, double Y)? subpathStart, bool outlinePending)
            {
                Segments = segments; CurrentPoint = currentPoint;
                SubpathStart = subpathStart; OutlinePending = outlinePending;
            }
        }

        internal Snapshotted Snapshot() =>
            new(_segments.ToArray(), CurrentPoint, _subpathStart, StrokeOutlinePending);

        internal void RestoreFrom(in Snapshotted saved)
        {
            _segments.Clear();
            _segments.AddRange(saved.Segments);
            CurrentPoint         = saved.CurrentPoint;
            _subpathStart        = saved.SubpathStart;
            StrokeOutlinePending = saved.OutlinePending;
        }

        /// <summary>
        /// Appends one rectangle as its own closed subpath, the way PLRM defines the <c>rect*</c>
        /// operators: <c>x y moveto w 0 rlineto 0 h rlineto w neg 0 rlineto closepath</c>.
        ///
        /// <para>A negative width or height is legal and needs no special case — the corners simply
        /// run the other way, which is what the rlineto form does too.</para>
        /// </summary>
        public void AppendRectangle(double x, double y, double w, double h)
        {
            MoveTo(x, y);
            LineTo(x + w, y);
            LineTo(x + w, y + h);
            LineTo(x, y + h);
            ClosePath();
        }

        /// <summary>
        /// <c>reversepath</c> — reverses the direction of every subpath, in place.
        ///
        /// <para><b>This changes what the path <i>paints</i>, which is why it is not cosmetic.</b>
        /// Under the non-zero winding rule (<c>fill</c>, PDF's <c>f</c>) a subpath drawn in the
        /// opposite direction to the one enclosing it makes a hole, and drawn in the same direction
        /// fills solid. A program that builds an outer box and an inner box the same way round and
        /// then reverses one is asking for a hole; ignoring the operator fills the hole in, and no
        /// structural check on the output can see that — the emitted stream is valid and the shape
        /// is simply wrong.</para>
        ///
        /// <para>A closed subpath stays closed. The resulting current point is the start of the
        /// original path's last subpath, which is what reversing it makes its end.</para>
        /// </summary>
        public void Reverse()
        {
            if (_segments.Count == 0) return;

            var source = new List<PsSegment>(_segments);

            // Rebuilt through the ordinary mutators so the segment cap and the current-point
            // bookkeeping stay in one place. The rebuild is the same length as the original, and
            // Clear() runs first, so the cap cannot be reached by reversing a legal path.
            Clear();

            int at = 0;
            while (at < source.Count)
            {
                // One subpath: its start point, its segments, and whether it closed.
                double startX, startY;
                if (source[at].Kind == PsSegmentKind.Move)
                {
                    startX = source[at].X3; startY = source[at].Y3;
                    at++;
                }
                else
                {
                    // Segments after a `closepath` with no intervening `moveto`. PostScript begins a
                    // new subpath at the closed one's start point; the path model records no Move
                    // for it, so the run is reversed on its own with that point as its start.
                    startX = _lastSubpathStartX; startY = _lastSubpathStartY;
                }

                var run = new List<PsSegment>();
                bool closed = false;
                while (at < source.Count && source[at].Kind != PsSegmentKind.Move)
                {
                    if (source[at].Kind == PsSegmentKind.Close) { closed = true; at++; break; }
                    run.Add(source[at]);
                    at++;
                }

                EmitReversedSubpath(startX, startY, run, closed);
            }
        }

        private double _lastSubpathStartX, _lastSubpathStartY;

        /// <summary>
        /// Writes one subpath backwards. Each segment is re-aimed at its own <i>start</i> point, and
        /// a curve additionally swaps its two control points — a Bézier reversed by reversing its
        /// control polygon is the identical curve, so this is exact rather than a re-fit.
        /// </summary>
        private void EmitReversedSubpath(double startX, double startY,
                                         List<PsSegment> run, bool closed)
        {
            _lastSubpathStartX = startX;
            _lastSubpathStartY = startY;

            if (run.Count == 0)
            {
                // A degenerate subpath — a bare moveto, or `moveto closepath`. Reversing it is
                // itself, and it still has to survive: a lone moveto is what a round-capped dot is
                // made of, and dropping it would erase the dot.
                MoveTo(startX, startY);
                if (closed) ClosePath();
                return;
            }

            var last = run[run.Count - 1];
            MoveTo(last.X3, last.Y3);

            for (int k = run.Count - 1; k >= 0; k--)
            {
                // Where the segment k started: the previous segment's endpoint, or the subpath start.
                double px = k == 0 ? startX : run[k - 1].X3;
                double py = k == 0 ? startY : run[k - 1].Y3;

                if (run[k].Kind == PsSegmentKind.Curve)
                    CurveTo(run[k].X2, run[k].Y2, run[k].X1, run[k].Y1, px, py);
                else
                    LineTo(px, py);
            }

            if (closed) ClosePath();
        }

        /// <summary>
        /// The path's bounding box in user space (<c>pathbbox</c>), or null when it is empty.
        ///
        /// <para>Curve control points are included rather than the curve being flattened. That
        /// over-estimates a curve's true extent, which is what PostScript itself does — the PLRM
        /// says <c>pathbbox</c> may return a larger box than the tightest one.</para>
        /// </summary>
        public (double X0, double Y0, double X1, double Y1)? BoundingBox()
        {
            if (_segments.Count == 0) return null;

            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            bool any = false;

            void Include(double x, double y)
            {
                any = true;
                if (x < x0) x0 = x;
                if (y < y0) y0 = y;
                if (x > x1) x1 = x;
                if (y > y1) y1 = y;
            }

            foreach (var s in _segments)
            {
                switch (s.Kind)
                {
                    case PsSegmentKind.Move:
                    case PsSegmentKind.Line:
                        Include(s.X3, s.Y3);
                        break;
                    case PsSegmentKind.Curve:
                        Include(s.X1, s.Y1);
                        Include(s.X2, s.Y2);
                        Include(s.X3, s.Y3);
                        break;
                }
            }

            return any ? (x0, y0, x1, y1) : null;
        }
    }
}
