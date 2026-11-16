// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// What <c>charpath</c> appends for a Type 3 glyph: the paths its procedure paints (PLRM §5.7 —
    /// a Type 3 glyph's outline is what its procedure would fill or stroke). They are read back from
    /// the glyph's built content stream, which is in glyph space and says every painting operator
    /// outright. The stream is this plugin's own <see cref="ContentStreamEmitter"/>'s, so the operators
    /// read are the ones it writes: <c>m l c h</c> build a path, <c>f f* S</c> paint it, <c>n</c>
    /// discards it — after <c>W</c>, a clip, which paints nothing — and <c>q cm Q</c> carry the
    /// transform (a <c>gsave</c> and <c>scale</c> in the glyph, or a widened clip's reconciliation). An
    /// image or text the glyph shows has no path here, so a glyph that paints only those has no
    /// outline, and its caller appends its box.
    /// </summary>
    internal static class PsType3Outlines
    {
        /// <summary>
        /// The paths <paramref name="glyph"/> paints — glyph space through <paramref name="glyphMatrix"/>
        /// into thousandths of text space, the units <c>PsTextOps.AppendOutline</c> takes, its advance
        /// the same way — read once and kept on the glyph; null when it paints no path.
        /// </summary>
        public static GlyphOutline Of(PsBuiltGlyph glyph, in PsMatrix glyphMatrix)
        {
            if (glyph.OutlineRead) return glyph.Outline;
            glyph.OutlineRead = true;
            glyph.Outline = Read(glyph, glyphMatrix);
            return glyph.Outline;
        }

        private const double Thousandths = 1000;

        private static GlyphOutline Read(PsBuiltGlyph glyph, PsMatrix glyphMatrix)
        {
            var painted = new List<GlyphOutlineSegment>();
            var path = new List<GlyphOutlineSegment>();
            var saved = new Stack<PsMatrix>();
            PsMatrix ctm = PsMatrix.Identity;

            foreach (ContentStreamOperation op in ContentStreamParser.Parse(glyph.Content))
            {
                switch (op.Operator)
                {
                    case "q": saved.Push(ctm); break;
                    case "Q": if (saved.Count > 0) ctm = saved.Pop(); break;
                    case "cm":
                        ctm = new PsMatrix(N(op, 0), N(op, 1), N(op, 2), N(op, 3), N(op, 4), N(op, 5)).Concat(ctm);
                        break;
                    case "m": path.Add(Point(GlyphOutlineSegmentKind.MoveTo, N(op, 0), N(op, 1))); break;
                    case "l": path.Add(Point(GlyphOutlineSegmentKind.LineTo, N(op, 0), N(op, 1))); break;
                    case "c":
                        path.Add(Curve(N(op, 0), N(op, 1), N(op, 2), N(op, 3), N(op, 4), N(op, 5)));
                        break;
                    case "h": path.Add(Close()); break;
                    case "f": case "f*": case "S":
                        painted.AddRange(path);
                        path.Clear();
                        break;
                    case "n":
                        path.Clear();
                        break;
                }
            }

            return painted.Count == 0 ? null : new GlyphOutline(painted, glyph.Wx * glyphMatrix.A * Thousandths);

            // A point of the glyph's content: through the content's transform into glyph space, then
            // through the glyph matrix into text space, in thousandths.
            (double X, double Y) ToText(double x, double y)
            {
                var (gx, gy) = ctm.Apply(x, y);
                var (tx, ty) = glyphMatrix.Apply(gx, gy);
                return (tx * Thousandths, ty * Thousandths);
            }

            GlyphOutlineSegment Point(GlyphOutlineSegmentKind kind, double x, double y)
            {
                var (tx, ty) = ToText(x, y);
                return new GlyphOutlineSegment(kind, 0, 0, 0, 0, tx, ty);
            }

            GlyphOutlineSegment Curve(double x1, double y1, double x2, double y2, double x3, double y3)
            {
                var (a, b) = ToText(x1, y1);
                var (c, d) = ToText(x2, y2);
                var (e, f) = ToText(x3, y3);
                return new GlyphOutlineSegment(GlyphOutlineSegmentKind.CurveTo, a, b, c, d, e, f);
            }

            static GlyphOutlineSegment Close() => new GlyphOutlineSegment(GlyphOutlineSegmentKind.ClosePath, 0, 0, 0, 0, 0, 0);
        }

        private static double N(ContentStreamOperation op, int k)
            => k < op.Operands.Length && op.Operands[k] is PdfNumber n ? n.DoubleValue : 0;
    }
}
