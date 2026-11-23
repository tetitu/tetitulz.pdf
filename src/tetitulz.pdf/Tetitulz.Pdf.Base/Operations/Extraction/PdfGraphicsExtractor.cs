// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    // ------------------------------------------------------------------
    //  Data model
    // ------------------------------------------------------------------

    /// <summary>A horizontal ruling line detected in a PDF page's graphics stream.</summary>
    public sealed class HRuling
    {
        /// <summary>Left X of the ruling in PDF points (always &lt;= X2).</summary>
        public float X1        { get; }
        /// <summary>Right X of the ruling in PDF points.</summary>
        public float X2        { get; }
        /// <summary>Y position of the ruling (mid-line, PDF coordinates: origin bottom-left).</summary>
        public float Y         { get; }
        /// <summary>Stroke line-width in PDF points (or fill height for thin filled shapes).</summary>
        public float LineWidth { get; }
        /// <summary>X2 - X1.</summary>
        public float Length    => X2 - X1;

        public HRuling(float x1, float x2, float y, float lineWidth)
        {
            X1        = Math.Min(x1, x2);
            X2        = Math.Max(x1, x2);
            Y         = y;
            LineWidth = lineWidth;
        }

        public override string ToString() => $"H y={Y:F1} [{X1:F1}..{X2:F1}] lw={LineWidth:F2}";
    }

    /// <summary>A vertical ruling line detected in a PDF page's graphics stream.</summary>
    public sealed class VRuling
    {
        /// <summary>X position of the ruling in PDF points.</summary>
        public float X         { get; }
        /// <summary>Bottom Y of the ruling in PDF points (always &lt;= Y2).</summary>
        public float Y1        { get; }
        /// <summary>Top Y of the ruling in PDF points.</summary>
        public float Y2        { get; }
        /// <summary>Stroke line-width in PDF points.</summary>
        public float LineWidth { get; }
        /// <summary>Y2 - Y1.</summary>
        public float Length    => Y2 - Y1;

        public VRuling(float x, float y1, float y2, float lineWidth)
        {
            X         = x;
            Y1        = Math.Min(y1, y2);
            Y2        = Math.Max(y1, y2);
            LineWidth = lineWidth;
        }

        public override string ToString() => $"V x={X:F1} [{Y1:F1}..{Y2:F1}] lw={LineWidth:F2}";
    }

    /// <summary>Bounding box of an embedded image on a PDF page.</summary>
    public sealed class ImageBbox
    {
        public float X      { get; }
        public float Y      { get; }
        public float Width  { get; }
        public float Height { get; }

        public ImageBbox(float x, float y, float width, float height)
        { X = x; Y = y; Width = width; Height = height; }
    }

    /// <summary>All graphical elements extracted from a single PDF page.</summary>
    public sealed class PdfPageGraphics
    {
        public int PageIndex { get; }
        public IReadOnlyList<HRuling>   HorizontalRulings { get; }
        public IReadOnlyList<VRuling>   VerticalRulings   { get; }
        public IReadOnlyList<ImageBbox> Images            { get; }

        public PdfPageGraphics(
            int pageIndex,
            IReadOnlyList<HRuling>   horizontalRulings,
            IReadOnlyList<VRuling>   verticalRulings,
            IReadOnlyList<ImageBbox> images)
        {
            PageIndex         = pageIndex;
            HorizontalRulings = horizontalRulings ?? Array.Empty<HRuling>();
            VerticalRulings   = verticalRulings   ?? Array.Empty<VRuling>();
            Images            = images            ?? Array.Empty<ImageBbox>();
        }
    }

    // ------------------------------------------------------------------
    //  Extractor
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses PDF content streams for path and XObject operators and extracts
    /// horizontal rulings, vertical rulings, and image bounding boxes.
    ///
    /// <para>Only axis-aligned segments (after CTM transform) longer than
    /// <see cref="MinRulingLength"/> points are reported.  Thin filled rectangles
    /// (height or width &lt; 3 pt) are also treated as rulings.  All output
    /// rulings are deduplicated and collinear overlaps merged.</para>
    /// </summary>
    public static class PdfGraphicsExtractor
    {
        /// <summary>Minimum segment length in PDF points to be reported as a ruling.</summary>
        public const float MinRulingLength = 5f;

        // Segment must be within this many pts of axis-aligned to count.
        private const float AxisTol = 0.5f;

        /// <summary>
        /// Maximum XObject (Form) recursion depth.  Cycles or deeply-nested Form XObjects
        /// beyond this depth are skipped and recorded in <see cref="LastError"/>.
        /// </summary>
        private const int MaxXObjectDepth = 8;

        /// <summary>
        /// Last non-fatal error encountered during graphics extraction (failed Form XObject
        /// decode, recursion-depth cap hit, etc.).  <c>null</c> on a clean run.  Per-call:
        /// reset to <c>null</c> at the start of each <see cref="Extract(Stream)"/> /
        /// <see cref="ExtractPage(Stream, int)"/> call.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent extraction on another
        /// thread cannot overwrite it — read it on the thread that ran the call (see the
        /// Diagnostics Pattern section of CLAUDE.md).
        /// </para>
        /// </summary>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        [System.ThreadStatic]
        private static string _lastError;

        // ------------------------------------------------------------------
        //  Public API
        // ------------------------------------------------------------------

        public static IReadOnlyList<PdfPageGraphics> Extract(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Extract(fs);
        }

        public static IReadOnlyList<PdfPageGraphics> Extract(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            LastError = null;
            using var doc   = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the graphics", "PdfOperations.ExtractGraphics(Stream, string password)");
            var result      = new List<PdfPageGraphics>(doc.Pages.Count);
            for (int pi = 0; pi < doc.Pages.Count; pi++)
                result.Add(ExtractPage(doc.Pages[pi], doc, pi));
            return result;
        }

        public static PdfPageGraphics ExtractPage(Stream input, int pageIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            LastError = null;
            using var doc   = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the graphics", "PdfOperations.ExtractGraphics(Stream, int, string password)");
            if ((uint)pageIndex >= (uint)doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            return ExtractPage(doc.Pages[pageIndex], doc, pageIndex);
        }

        // ------------------------------------------------------------------
        //  Per-page
        // ------------------------------------------------------------------

        private static PdfPageGraphics ExtractPage(PdfPage page, PdfDocument doc, int pageIndex)
        {
            byte[] content = page.GetContentStreamData();
            if (content == null || content.Length == 0)
                return new PdfPageGraphics(pageIndex, null, null, null);

            var hRaw   = new List<HRuling>();
            var vRaw   = new List<VRuling>();
            var images = new List<ImageBbox>();

            // One aggregate budget per page, charged per content walk and by each form's decoded content. The depth cap bounds one
            // branch and never the number of branches: a form that draws another ten times, eight
            // levels deep, is 10^8 walks under a cap of 8 — the same product PdfStructuredTextExtractor
            // bounds with this same limit.
            var budget = new FormContentBudget();
            Walk(ContentStreamParser.Parse(content), page.Resources, doc, hRaw, vRaw, images, 0,
                 budget: budget);
            if (budget.Exhausted)
                LastError = budget.Shortfall(pageIndex, "the page's graphics are partial.");

            return new PdfPageGraphics(pageIndex, MergeH(hRaw), MergeV(vRaw), images);
        }

        // ------------------------------------------------------------------
        //  Content-stream walker
        // ------------------------------------------------------------------

        private static void Walk(
            List<ContentStreamOperation> ops,
            PdfDictionary resources,
            PdfDocument   doc,
            List<HRuling>   hOut,
            List<VRuling>   vOut,
            List<ImageBbox> imgOut,
            int depth,
            Gs initialGs = null,
            FormContentBudget budget = null,
            HashSet<int> visited = null)
        {
            if (depth > MaxXObjectDepth)
            {
                LastError = $"XObject recursion depth limit ({MaxXObjectDepth}) exceeded";
                return;
            }

            // Charged before the work, once per walk, so the aggregate is bounded even where every
            // branch is short.
            if (budget != null && !budget.TakeInvocation()) return;

            var gsStack = new Stack<Gs>();
            // A form starts in the state at its Do with its /Matrix composed in (§8.10.1).
            var gs      = initialGs ?? new Gs();
            var path    = new PathAccum();

            foreach (var op in ops)
            {
                switch (op.Operator)
                {
                    // ── Graphics state ──────────────────────────────────
                    case PdfOperators.Save:
                        gsStack.Push(gs.Clone());
                        break;
                    case PdfOperators.Restore:
                        if (gsStack.Count > 0) gs = gsStack.Pop();
                        break;
                    case PdfOperators.ConcatMatrix:
                        if (op.Operands.Length >= 6)
                            gs.Ctm = MatMul(ReadMat(op), gs.Ctm);
                        break;
                    case PdfOperators.SetLineWidth:
                        gs.Lw = ContentOperand.Float(op, 0);
                        break;

                    // ── Path construction ────────────────────────────────
                    case PdfOperators.MoveTo:
                        path.MoveTo(ContentOperand.Float(op, 0), ContentOperand.Float(op, 1));
                        break;
                    case PdfOperators.LineTo:
                        path.LineTo(ContentOperand.Float(op, 0), ContentOperand.Float(op, 1));
                        break;
                    case PdfOperators.CurveTo:   // cubic bezier — advance to endpoint only
                        path.AdvanceTo(ContentOperand.Float(op, 4), ContentOperand.Float(op, 5));
                        break;
                    case PdfOperators.CurveToV:  // cubic bezier (first control = current point)
                        path.AdvanceTo(ContentOperand.Float(op, 2), ContentOperand.Float(op, 3));
                        break;
                    case PdfOperators.CurveToY:  // cubic bezier (last control = endpoint)
                        path.AdvanceTo(ContentOperand.Float(op, 2), ContentOperand.Float(op, 3));
                        break;
                    case PdfOperators.ClosePath:
                        path.Close();
                        break;
                    case PdfOperators.Rectangle:
                        path.Rect(ContentOperand.Float(op, 0), ContentOperand.Float(op, 1), ContentOperand.Float(op, 2), ContentOperand.Float(op, 3));
                        break;

                    // ── Path painting: stroke ────────────────────────────
                    case PdfOperators.Stroke:
                        EmitStroke(path, gs, hOut, vOut);
                        path.Clear();
                        break;
                    case PdfOperators.CloseStroke:
                        path.Close();
                        EmitStroke(path, gs, hOut, vOut);
                        path.Clear();
                        break;

                    // ── Path painting: fill ──────────────────────────────
                    case PdfOperators.Fill:
                    case PdfOperators.FillObsolete:
                    case PdfOperators.FillEvenOdd:
                        EmitFill(path, gs, hOut, vOut);
                        path.Clear();
                        break;

                    // ── Path painting: fill + stroke ─────────────────────
                    case PdfOperators.FillStroke:
                    case PdfOperators.FillStrokeEvenOdd:
                        EmitFill(path, gs, hOut, vOut);
                        EmitStroke(path, gs, hOut, vOut);
                        path.Clear();
                        break;
                    case PdfOperators.CloseFillStroke:
                    case PdfOperators.CloseFillStrokeEvenOdd:
                        path.Close();
                        EmitFill(path, gs, hOut, vOut);
                        EmitStroke(path, gs, hOut, vOut);
                        path.Clear();
                        break;

                    // ── End path without paint ───────────────────────────
                    case PdfOperators.EndPath:
                    case PdfOperators.Clip:
                    case PdfOperators.ClipEvenOdd:
                        path.Clear();
                        break;

                    // ── XObjects ─────────────────────────────────────────
                    case PdfOperators.InvokeXObject:
                        if (op.Operands.Length >= 1)
                        {
                            string xname = (op.Operands[0] as PdfName)?.Value;
                            if (xname != null)
                                HandleDo(xname, resources, doc, gs, hOut, vOut, imgOut, depth, budget, visited);
                        }
                        break;
                }
            }
        }

        // ------------------------------------------------------------------
        //  Emit helpers
        // ------------------------------------------------------------------

        private static void EmitStroke(PathAccum path, Gs gs, List<HRuling> hOut, List<VRuling> vOut)
        {
            foreach (var s in path.Segs)
                EmitSeg(s.x1, s.y1, s.x2, s.y2, gs, hOut, vOut);
        }

        /// <summary>
        /// Runs the thin-band ruling test once <b>per subpath</b>.
        /// </summary>
        /// <remarks>
        /// Not one bounding box across every segment of the accumulated path: a PDF path may hold any number of disjoint subpaths painted by a single
        /// <c>f</c>, which is the normal way a table's rules are emitted, so
        /// <c>0 100 400 0.6 re  0 120 400 0.6 re  0 140 400 0.6 re  f</c> — three 0.6&#160;pt rules
        /// — would make one 400&#160;×&#160;40.6 box, fail <c>thin &lt; 3</c>, and emit nothing;
        /// PdfTableExtractor's Lines strategy would then see fewer than two horizontal rulings, and a
        /// bordered table would be silently detected as unbordered or missed.
        /// </remarks>
        private static void EmitFill(PathAccum path, Gs gs, List<HRuling> hOut, List<VRuling> vOut)
        {
            int n = path.Segs.Count;
            if (n == 0) return;

            var starts = path.SubpathStarts;
            int prev = 0;
            for (int k = 0; k <= starts.Count; k++)
            {
                // Segments before the first recorded start belong to a subpath opened by a
                // stray `l` with no `m`; they are still a band and are tested as one.
                int boundary = k < starts.Count ? Math.Min(starts[k], n) : n;
                if (boundary > prev)
                {
                    EmitFillSubpath(path, prev, boundary, gs, hOut, vOut);
                    prev = boundary;
                }
            }
        }

        /// <summary>The thin-band test over <c>path.Segs[from..to)</c>.</summary>
        private static void EmitFillSubpath(
            PathAccum path, int from, int to, Gs gs, List<HRuling> hOut, List<VRuling> vOut)
        {
            // Compute bounding box of this subpath's endpoints in page space.
            float minPx = float.MaxValue, maxPx = float.MinValue;
            float minPy = float.MaxValue, maxPy = float.MinValue;
            var c = gs.Ctm;

            void Expand(float ux, float uy)
            {
                float px = c[0]*ux + c[2]*uy + c[4];
                float py = c[1]*ux + c[3]*uy + c[5];
                if (px < minPx) minPx = px; if (px > maxPx) maxPx = px;
                if (py < minPy) minPy = py; if (py > maxPy) maxPy = py;
            }

            for (int i = from; i < to; i++)
            {
                var s = path.Segs[i];
                Expand(s.x1, s.y1); Expand(s.x2, s.y2);
            }

            float pw = maxPx - minPx, ph = maxPy - minPy;
            float thin = Math.Min(pw, ph), thick = Math.Max(pw, ph);

            // Only treat as a ruling if one dimension is very thin (< 3 pt) and the
            // other is long enough.  This excludes large background fill rectangles.
            if (thin >= 3f || thick < MinRulingLength) return;

            if (ph < pw) // thin horizontal band
                hOut.Add(new HRuling(minPx, maxPx, (minPy + maxPy) / 2f, ph));
            else          // thin vertical band
                vOut.Add(new VRuling((minPx + maxPx) / 2f, minPy, maxPy, pw));
        }

        private static void EmitSeg(
            float ux1, float uy1, float ux2, float uy2,
            Gs gs, List<HRuling> hOut, List<VRuling> vOut)
        {
            var c = gs.Ctm;
            float px1 = c[0]*ux1 + c[2]*uy1 + c[4];
            float py1 = c[1]*ux1 + c[3]*uy1 + c[5];
            float px2 = c[0]*ux2 + c[2]*uy2 + c[4];
            float py2 = c[1]*ux2 + c[3]*uy2 + c[5];

            float dx = Math.Abs(px2 - px1);
            float dy = Math.Abs(py2 - py1);

            if      (dy <= AxisTol && dx >= MinRulingLength)
                hOut.Add(new HRuling(px1, px2, (py1 + py2) / 2f, gs.Lw));
            else if (dx <= AxisTol && dy >= MinRulingLength)
                vOut.Add(new VRuling((px1 + px2) / 2f, py1, py2, gs.Lw));
        }

        // ------------------------------------------------------------------
        //  XObject handler
        // ------------------------------------------------------------------

        private static void HandleDo(
            string name, PdfDictionary resources, PdfDocument doc, Gs gs,
            List<HRuling> hOut, List<VRuling> vOut, List<ImageBbox> imgOut, int depth,
            FormContentBudget budget, HashSet<int> visited)
        {
            var cat = resources?[PdfName.XObject];
            if (cat == null) return;
            var catDict = doc.ResolveObject(cat) as PdfDictionary;
            if (catDict == null) return;
            var entry = doc.ResolveObject(catDict[new PdfName(name)]);
            if (entry is not PdfStream xs) return;

            // Resolved: GetName reads only a direct name, so `/Subtype 8 0 R` skipped the XObject.
            string sub = Rendering.PdfResolve.Name(doc, xs, PdfName.Subtype)?.Value;

            if (sub == "Form")
            {
                byte[] data;
                try { data = xs.GetDecodedData(); }
                catch (Exception ex)
                {
                    LastError = $"Form XObject '{name}' decode failed: {ex.Message}";
                    return;
                }

                var res = xs[PdfName.Resources] is PdfObject ro
                    ? doc.ResolveObject(ro) as PdfDictionary ?? resources
                    : resources;

                var cg  = gs.Clone();
                // Resolved: GetArray reads only a direct array, so `/Matrix 7 0 R` was ignored.
                var mat = Rendering.PdfResolve.Array(doc, xs, PdfName.Matrix);
                if (mat != null && mat.Count >= 6)
                {
                    float[] m =
                    {
                        Rendering.PdfResolve.ArrayFloat(doc, mat, 0), Rendering.PdfResolve.ArrayFloat(doc, mat, 1), Rendering.PdfResolve.ArrayFloat(doc, mat, 2),
                        Rendering.PdfResolve.ArrayFloat(doc, mat, 3), Rendering.PdfResolve.ArrayFloat(doc, mat, 4), Rendering.PdfResolve.ArrayFloat(doc, mat, 5)
                    };
                    cg.Ctm = MatMul(m, cg.Ctm);
                }

                // The cycle half of the guard, scoped to this branch so a form legitimately drawn
                // twice side by side is still walked twice.
                visited ??= new HashSet<int>();
                int formObj = xs.ObjectNumber;
                if (formObj > 0 && !visited.Add(formObj))
                {
                    LastError = $"Form XObject '{name}' draws itself; the recursion was stopped.";
                    return;
                }

                try
                {
                    // `cg` was built above and then not passed, so every ruling and image inside a
                    // form came out in form space — the same defect PdfStructuredTextExtractor had.
                    // Charged its decoded length and parsed once a page (FormContentBudget).
                    var formOps = budget != null ? budget.Operations(data) : ContentStreamParser.Parse(data);
                    if (formOps == null) return;   // the page's form content is spent; the page's report says so
                    Walk(formOps, res, doc, hOut, vOut, imgOut, depth + 1, cg, budget, visited);
                }
                finally
                {
                    if (formObj > 0) visited.Remove(formObj);
                }
            }
            else if (sub == "Image")
            {
                // Image occupies the unit square [0,1]×[0,1] mapped by the current CTM.
                var c    = gs.Ctm;
                float[] pxs = { c[4], c[0]+c[4], c[0]+c[2]+c[4], c[2]+c[4] };
                float[] pys = { c[5], c[1]+c[5], c[1]+c[3]+c[5], c[3]+c[5] };
                float x = pxs.Min(), y = pys.Min();
                float w = pxs.Max() - x, h = pys.Max() - y;
                if (w > 0 && h > 0) imgOut.Add(new ImageBbox(x, y, w, h));
            }
        }

        // ------------------------------------------------------------------
        //  Merge collinear / overlapping rulings
        // ------------------------------------------------------------------

        private static IReadOnlyList<HRuling> MergeH(List<HRuling> raw)
        {
            if (raw.Count == 0) return Array.Empty<HRuling>();
            raw.Sort((a, b) => a.Y.CompareTo(b.Y));
            var result = new List<HRuling>();
            var grp    = new List<HRuling> { raw[0] };
            for (int i = 1; i < raw.Count; i++)
            {
                if (Math.Abs(raw[i].Y - raw[i-1].Y) <= 1f)
                    grp.Add(raw[i]);
                else
                {
                    FlushHGroup(grp, result);
                    grp = new List<HRuling> { raw[i] };
                }
            }
            FlushHGroup(grp, result);
            return result;
        }

        private static void FlushHGroup(List<HRuling> g, List<HRuling> out_)
        {
            float y = 0f, lw = 0f;
            foreach (var r in g) { y += r.Y; if (r.LineWidth > lw) lw = r.LineWidth; }
            y /= g.Count;
            g.Sort((a, b) => a.X1.CompareTo(b.X1));
            float x1 = g[0].X1, x2 = g[0].X2;
            for (int i = 1; i < g.Count; i++)
            {
                if (g[i].X1 <= x2 + 1f) { if (g[i].X2 > x2) x2 = g[i].X2; }
                else
                {
                    if (x2 - x1 >= MinRulingLength) out_.Add(new HRuling(x1, x2, y, lw));
                    x1 = g[i].X1; x2 = g[i].X2;
                }
            }
            if (x2 - x1 >= MinRulingLength) out_.Add(new HRuling(x1, x2, y, lw));
        }

        private static IReadOnlyList<VRuling> MergeV(List<VRuling> raw)
        {
            if (raw.Count == 0) return Array.Empty<VRuling>();
            raw.Sort((a, b) => a.X.CompareTo(b.X));
            var result = new List<VRuling>();
            var grp    = new List<VRuling> { raw[0] };
            for (int i = 1; i < raw.Count; i++)
            {
                if (Math.Abs(raw[i].X - raw[i-1].X) <= 1f)
                    grp.Add(raw[i]);
                else
                {
                    FlushVGroup(grp, result);
                    grp = new List<VRuling> { raw[i] };
                }
            }
            FlushVGroup(grp, result);
            return result;
        }

        private static void FlushVGroup(List<VRuling> g, List<VRuling> out_)
        {
            float x = 0f, lw = 0f;
            foreach (var r in g) { x += r.X; if (r.LineWidth > lw) lw = r.LineWidth; }
            x /= g.Count;
            g.Sort((a, b) => a.Y1.CompareTo(b.Y1));
            float y1 = g[0].Y1, y2 = g[0].Y2;
            for (int i = 1; i < g.Count; i++)
            {
                if (g[i].Y1 <= y2 + 1f) { if (g[i].Y2 > y2) y2 = g[i].Y2; }
                else
                {
                    if (y2 - y1 >= MinRulingLength) out_.Add(new VRuling(x, y1, y2, lw));
                    y1 = g[i].Y1; y2 = g[i].Y2;
                }
            }
            if (y2 - y1 >= MinRulingLength) out_.Add(new VRuling(x, y1, y2, lw));
        }

        // ------------------------------------------------------------------
        //  Matrix / operand helpers
        // ------------------------------------------------------------------

        // Compose: apply A first, then B. Delegated to the single source of truth
        // in Rendering.GraphicsState.Compose (identical row-vector convention).
        private static float[] MatMul(float[] A, float[] B) => Rendering.GraphicsState.Compose(A, B);

        private static float[] ReadMat(ContentStreamOperation op) =>
            new float[]
            {
                ContentOperand.Float(op,0), ContentOperand.Float(op,1), ContentOperand.Float(op,2),
                ContentOperand.Float(op,3), ContentOperand.Float(op,4), ContentOperand.Float(op,5)
            };

        // ------------------------------------------------------------------
        //  Internal state types
        // ------------------------------------------------------------------

        private sealed class Gs
        {
            public float[] Ctm = { 1, 0, 0, 1, 0, 0 };
            public float   Lw  = 1f;

            public Gs Clone()
            {
                var c = (Gs)MemberwiseClone();
                c.Ctm = (float[])Ctm.Clone();
                return c;
            }
        }

        private sealed class PathAccum
        {
            public readonly List<(float x1, float y1, float x2, float y2)> Segs = new();

            /// <summary>
            /// Index into <see cref="Segs"/> at which each subpath begins. §8.5.2.1 starts a new
            /// subpath at every <c>m</c> (and therefore at every <c>re</c>), and one <c>f</c> may
            /// paint any number of them — which is how a table's rules are normally emitted.
            /// <see cref="EmitFill"/> needs the boundaries to test each band separately.
            /// </summary>
            public readonly List<int> SubpathStarts = new();

            private float _sx, _sy, _cx, _cy;
            private bool  _has;

            public void MoveTo(float x, float y)
            {
                // Record the boundary, de-duplicating consecutive MoveTos that add no segment
                // between them, so an empty range never reaches EmitFill.
                if (SubpathStarts.Count == 0 || SubpathStarts[SubpathStarts.Count - 1] != Segs.Count)
                    SubpathStarts.Add(Segs.Count);
                _sx = _cx = x; _sy = _cy = y; _has = true;
            }

            public void LineTo(float x, float y)
            {
                if (_has) Segs.Add((_cx, _cy, x, y));
                _cx = x; _cy = y; _has = true;
            }

            // Bezier endpoint only — curves are not table borders.
            public void AdvanceTo(float x, float y) { _cx = x; _cy = y; _has = true; }

            public void Close()
            {
                if (_has) Segs.Add((_cx, _cy, _sx, _sy));
                _cx = _sx; _cy = _sy;
            }

            public void Rect(float x, float y, float w, float h)
            {
                MoveTo(x, y);
                LineTo(x + w, y);
                LineTo(x + w, y + h);
                LineTo(x, y + h);
                Close();
            }

            public void Clear()
            {
                Segs.Clear();
                SubpathStarts.Clear();
                _sx = _sy = _cx = _cy = 0;
                _has = false;
            }
        }
    }
}
