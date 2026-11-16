// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Where a Type 3 font's glyphs paint and how far each moves the pen, for
    /// <see cref="PdfRedactionEngine"/>: a glyph here is a program, so what it paints can lie anywhere
    /// around its origin, and its advance goes through the font's own matrix.
    /// </summary>
    /// <remarks>
    /// A glyph's extent is the union of what its procedure paints, in glyph space, read the way the
    /// renderer runs it: <c>q</c>, <c>Q</c> and <c>cm</c> followed, a path taken by its control points,
    /// a stroke widened by its line width and miter, an inline or named image its unit square. What
    /// the walk cannot place — a shading, text, a form, a stroke after an <c>ExtGState</c>, a procedure
    /// that will not decode or is past its ceiling — makes the glyph
    /// <see cref="Glyph.PaintsAnywhere"/>, which every region hits: removal is the side to err on.
    /// </remarks>
    internal sealed class RedactionType3Font
    {
        /// <summary>The largest glyph procedure read. A larger one is a glyph that paints anywhere.</summary>
        internal const int MaxProcedureBytes = 1 << 20;

        /// <summary>The decoded bytes one redaction reads of glyph procedures, over every font it meets.</summary>
        internal const int MaxProcedureBytesPerRedaction = 1 << 26;

        private readonly PdfDocument _doc;
        private readonly PdfDictionary _charProcs;
        private readonly PdfDictionary _encoding;
        private readonly PdfDictionary _ownResources;
        private readonly PdfArray _widths;
        private readonly int _firstChar;
        private readonly WalkBudget _budget;
        private readonly Glyph[] _glyphs = new Glyph[256];

        /// <summary>Glyph space to text space, as the renderers read it.</summary>
        internal float[] FontMatrix { get; }

        internal RedactionType3Font(PdfDictionary font, PdfDocument doc, WalkBudget budget)
        {
            _doc = doc;
            _budget = budget;
            FontMatrix = Type3Encoding.FontMatrixOf(doc, font, out _);
            _charProcs = doc.ResolveObject(font[PdfName.CharProcs]) as PdfDictionary;
            _encoding = doc.ResolveObject(font[PdfName.Encoding]) as PdfDictionary;
            _ownResources = doc.ResolveObject(font[PdfName.Resources]) as PdfDictionary;
            _widths = doc.ResolveObject(font[PdfName.Widths]) as PdfArray;
            _firstChar = PdfResolve.Number(doc, font, PdfName.FirstChar)?.IntValue ?? 0;
        }

        /// <summary>One code's advance and extent, read once.</summary>
        internal Glyph GlyphOf(int code) => _glyphs[code] ?? (_glyphs[code] = Read(code));

        private Glyph Read(int code)
        {
            var glyph = new Glyph { Width = Type3Encoding.GlyphWidth(code, _widths, _firstChar, FontMatrix, _doc) };

            string name = Type3Encoding.GlyphName(_doc, _encoding, code);
            if (name == null || !(_doc.ResolveObject(_charProcs?[name]) is PdfStream procedure)) return glyph;

            int ceiling = Math.Min(MaxProcedureBytes, _budget.Remaining);
            if (ceiling < 1 || !procedure.TryGetDecodedData(ceiling, out byte[] data, out _))
            {
                _budget.Take(_budget.Remaining / 2);
                glyph.PaintsAnywhere = true;
                return glyph;
            }
            _budget.Take(data.Length);

            Scan(data, glyph);
            return glyph;
        }

        private void Scan(byte[] data, Glyph glyph)
        {
            float[] ctm = PdfMatrix.Identity();
            var saved = new Stack<State>();
            float lineWidth = 1f, miter = 10f;
            bool strokeUnknown = false;
            var path = new Extent();

            foreach (ContentStreamOperation op in ContentStreamParser.Parse(data))
            {
                switch (op.Operator)
                {
                    case PdfOperators.SetCharWidth:
                    case PdfOperators.SetCacheDevice:
                        if (op.Operands.Length >= 1) glyph.Width = Number(op, 0) * FontMatrix[0];
                        break;

                    case PdfOperators.Save:
                        saved.Push(new State(ctm, lineWidth, miter, strokeUnknown));
                        break;
                    case PdfOperators.Restore:
                        if (saved.Count > 0)
                        {
                            State state = saved.Pop();
                            ctm = state.Ctm; lineWidth = state.LineWidth; miter = state.Miter; strokeUnknown = state.StrokeUnknown;
                        }
                        break;
                    case PdfOperators.ConcatMatrix:
                        if (op.Operands.Length >= 6)
                            ctm = PdfMatrix.Compose(new[] { Number(op, 0), Number(op, 1), Number(op, 2), Number(op, 3), Number(op, 4), Number(op, 5) }, ctm);
                        break;
                    case PdfOperators.SetLineWidth: lineWidth = Number(op, 0); break;
                    case PdfOperators.SetMiterLimit: miter = Number(op, 0); break;
                    case PdfOperators.SetExtGState: strokeUnknown = true; break;

                    case PdfOperators.Rectangle:
                    {
                        float x = Number(op, 0), y = Number(op, 1), w = Number(op, 2), h = Number(op, 3);
                        path.Add(ctm, x, y); path.Add(ctm, x + w, y); path.Add(ctm, x, y + h); path.Add(ctm, x + w, y + h);
                        break;
                    }
                    case PdfOperators.MoveTo:
                    case PdfOperators.LineTo:
                    case PdfOperators.CurveTo:
                    case PdfOperators.CurveToV:
                    case PdfOperators.CurveToY:
                        for (int i = 0; i + 1 < op.Operands.Length; i += 2) path.Add(ctm, Number(op, i), Number(op, i + 1));
                        break;

                    case PdfOperators.EndPath:
                        path = new Extent();
                        break;
                    case PdfOperators.Fill:
                    case PdfOperators.FillObsolete:
                    case PdfOperators.FillEvenOdd:
                        glyph.Paint(path, 0f);
                        path = new Extent();
                        break;
                    case PdfOperators.Stroke:
                    case PdfOperators.CloseStroke:
                    case PdfOperators.FillStroke:
                    case PdfOperators.FillStrokeEvenOdd:
                    case PdfOperators.CloseFillStroke:
                    case PdfOperators.CloseFillStrokeEvenOdd:
                        if (strokeUnknown) glyph.PaintsAnywhere = true;
                        glyph.Paint(path, StrokeReach(ctm, lineWidth, miter));
                        path = new Extent();
                        break;

                    case PdfOperators.BeginInlineImage:
                        PaintUnitSquare(glyph, ctm);
                        break;
                    case PdfOperators.InvokeXObject:
                        // A form may hold anything. The font's own /Resources alone are read: its
                        // fallback, the resources of whichever stream shows the glyph, changes
                        // between the uses of a glyph read once.
                        if (IsImage(op)) PaintUnitSquare(glyph, ctm);
                        else glyph.PaintsAnywhere = true;
                        break;

                    case PdfOperators.ShadingFill:
                    case PdfOperators.ShowText:
                    case PdfOperators.ShowTextArray:
                    case PdfOperators.NextLineShowText:
                    case PdfOperators.NextLineSetSpacingShowText:
                        glyph.PaintsAnywhere = true;
                        break;
                }
            }
        }

        private bool IsImage(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1 || !(op.Operands[0] is PdfName name)) return false;
            return _doc.ResolveObject(_ownResources?[PdfName.XObject]) is PdfDictionary xobjects
                && _doc.ResolveObject(xobjects[name]) is PdfStream xobject
                && (_doc.ResolveObject(xobject[PdfName.Subtype]) as PdfName)?.Value == "Image";
        }

        private static void PaintUnitSquare(Glyph glyph, float[] ctm)
        {
            var square = new Extent();
            square.Add(ctm, 0, 0); square.Add(ctm, 1, 0); square.Add(ctm, 0, 1); square.Add(ctm, 1, 1);
            glyph.Paint(square, 0f);
        }

        /// <summary>How far a stroke reaches past its path, under the matrix in force.</summary>
        private static float StrokeReach(float[] ctm, float lineWidth, float miter)
        {
            float scale = Math.Max(Math.Abs(ctm[0]) + Math.Abs(ctm[2]), Math.Abs(ctm[1]) + Math.Abs(ctm[3]));
            return Math.Abs(lineWidth) * Math.Max(1f, Math.Abs(miter)) / 2f * scale;
        }

        private static float Number(ContentStreamOperation op, int i)
            => op.Operands.Length > i && op.Operands[i] is PdfNumber n ? (float)n.DoubleValue : 0f;

        private readonly struct State
        {
            internal readonly float[] Ctm;
            internal readonly float LineWidth, Miter;
            internal readonly bool StrokeUnknown;

            internal State(float[] ctm, float lineWidth, float miter, bool strokeUnknown)
            {
                Ctm = ctm; LineWidth = lineWidth; Miter = miter; StrokeUnknown = strokeUnknown;
            }
        }

        internal sealed class Extent
        {
            internal bool Any;
            internal bool NotFinite;
            internal float MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue;

            internal void Add(float[] m, float x, float y)
            {
                float px = m[0] * x + m[2] * y + m[4], py = m[1] * x + m[3] * y + m[5];
                if (!MatrixGuard.IsFinite(px) || !MatrixGuard.IsFinite(py)) { NotFinite = true; return; }
                Any = true;
                MinX = Math.Min(MinX, px); MaxX = Math.Max(MaxX, px);
                MinY = Math.Min(MinY, py); MaxY = Math.Max(MaxY, py);
            }
        }

        /// <summary>One glyph as a redaction sees it.</summary>
        internal sealed class Glyph
        {
            /// <summary>The advance in text space: the procedure's <c>d0</c> or <c>d1</c>, else <c>/Widths</c>, through the font matrix's <c>a</c>.</summary>
            internal float Width;

            /// <summary>What it paints cannot be placed, so every region hits it.</summary>
            internal bool PaintsAnywhere;

            /// <summary>Whether <see cref="MinX"/> and the rest say anything: a glyph may paint nothing.</summary>
            internal bool Paints;

            internal float MinX, MinY, MaxX, MaxY;

            internal void Paint(Extent extent, float reach)
            {
                if (extent.NotFinite || !MatrixGuard.IsFinite(reach)) { PaintsAnywhere = true; return; }
                if (!extent.Any) return;
                float minX = extent.MinX - reach, minY = extent.MinY - reach, maxX = extent.MaxX + reach, maxY = extent.MaxY + reach;
                if (!Paints) { Paints = true; MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; return; }
                MinX = Math.Min(MinX, minX); MinY = Math.Min(MinY, minY);
                MaxX = Math.Max(MaxX, maxX); MaxY = Math.Max(MaxY, maxY);
            }
        }
    }
}
