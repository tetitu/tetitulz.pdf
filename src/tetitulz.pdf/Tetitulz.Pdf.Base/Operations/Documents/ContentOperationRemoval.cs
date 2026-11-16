// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// How a painting operation is taken out of a content stream while every other operation keeps
    /// its meaning — the one rule <see cref="TransparencyRegions"/> (an operation that paints
    /// transparently) and <see cref="PdfContentFilter"/> (a class of content) both apply.
    /// </summary>
    /// <remarks>
    /// <para>A path's paint becomes <c>n</c>: the path ends unpainted, and a clip set before it
    /// (<c>W n</c> is <c>W</c> then the paint) still takes effect. Text is kept only where its render
    /// mode adds to the clip (§9.3.6, modes 4–7): shown in mode 7 it clips and paints nothing, and
    /// the mode is put back after it; text that only paints is dropped, and with it the text position
    /// it would have advanced, which nothing after it in a run that is all taken out reads. An image,
    /// a form, an inline image and a shading are dropped outright.</para>
    /// <para>What is dropped here is a decision the caller has already made; this type writes it.
    /// Nothing is drawn in place of the operation.</para>
    /// </remarks>
    internal static class ContentOperationRemoval
    {
        /// <summary>True when <paramref name="op"/> paints the current path (§8.5.3) — the operators that become <c>n</c>.</summary>
        internal static bool PaintsPath(string op)
        {
            switch (op)
            {
                case PdfOperators.Stroke:
                case PdfOperators.CloseStroke:
                case PdfOperators.Fill:
                case PdfOperators.FillObsolete:
                case PdfOperators.FillEvenOdd:
                case PdfOperators.FillStroke:
                case PdfOperators.FillStrokeEvenOdd:
                case PdfOperators.CloseFillStroke:
                case PdfOperators.CloseFillStrokeEvenOdd:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Writes a path-painting operation taken out: the path ends unpainted.</summary>
        internal static void WritePathUnpainted(PdfWriter w)
            => w.WriteRaw(PdfOperators.EndPath + "\n");

        /// <summary>
        /// Writes the text-showing operation <paramref name="op"/> taken out: in mode 7 where the
        /// render mode in <paramref name="text"/> adds to the clip, so the clip is kept, the mode
        /// restored after it; nothing otherwise.
        /// </summary>
        internal static void WriteTextUnpainted(PdfWriter w, ContentStreamOperation op, TextPaintTracker text)
        {
            if (!text.RenderMode.AddsToClip()) return;
            int mode = (int)text.RenderMode;
            w.WriteRaw($"{(int)TextRenderMode.Clip} {PdfOperators.SetTextRenderMode}\n");
            ContentOperationWriter.Write(w, op);
            w.WriteRaw($"{mode} {PdfOperators.SetTextRenderMode}\n");
        }

        /// <summary>
        /// Writes the text-showing operation <paramref name="op"/> with nothing painted but the text
        /// kept as text: mode 3, or 7 where the render mode also clips, the mode restored after it.
        /// </summary>
        internal static void WriteTextInvisible(PdfWriter w, ContentStreamOperation op, TextPaintTracker text)
        {
            int mode = (int)text.RenderMode;
            int invisible = text.RenderMode.AddsToClip() ? (int)TextRenderMode.Clip : (int)TextRenderMode.Invisible;
            w.WriteRaw($"{invisible} {PdfOperators.SetTextRenderMode}\n");
            ContentOperationWriter.Write(w, op);
            w.WriteRaw($"{mode} {PdfOperators.SetTextRenderMode}\n");
        }
    }
}
