// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// A procedure run once into a content stream of its own — a tiling pattern's cell, a form's
    /// XObject: a child interpreter over its own engine, emitter and resources, as a Type 3 glyph is
    /// run, the procedure's operand on its stack, the dictionary stack shared and left as it was found.
    ///
    /// <para><b>The state it starts in is the caller's</b> (PLRM §4.9.2, §4.7): line style, colour —
    /// never a pattern colour, whose resource is the caller's — and font. A cell whose PDF stream
    /// inherits the state it is drawn in (a form, ISO 32000-2 §8.10.1) assumes that state in its
    /// emitter, writing only what the procedure changes; one that starts in the page's initial state
    /// (a tiling cell, §8.7.3.1) writes it.</para>
    /// </summary>
    internal static class PsCells
    {
        // Per conversion, reset by both public entry points: cells nest (a form in a pattern in a
        // form), and every kind counts against one depth.
        [ThreadStatic] private static int _depth;

        internal static void ResetPerConversionState() => _depth = 0;

        /// <summary>How deep the cell being built is: 0 outside any.</summary>
        internal static int Depth => _depth;

        /// <summary>The content the procedure drew, its resources through <paramref name="resources"/> (never null).</summary>
        internal static byte[] Run(PsInterpreter i, PsGraphicsEngine from, PsArray procedure, PsObject operand,
            int maxBytes, bool colourSuppressed, bool inheritsState, string op, out PdfDictionary resources)
        {
            var emitter = new ContentStreamEmitter(maxBytes) { ColourSuppressed = colourSuppressed };
            // Identity, as a glyph's: the cell is in its own space, and the caller's /Matrix places it.
            var engine = new PsGraphicsEngine(emitter, PsMatrix.Identity, maxBytes);
            engine.UseOwnResources();

            PsGraphicsState source = from.Gs, to = engine.Gs;
            to.LineWidth = source.LineWidth;
            to.LineCap = source.LineCap;
            to.LineJoin = source.LineJoin;
            to.MiterLimit = source.MiterLimit;
            to.DashArray = (double[])source.DashArray.Clone();
            to.DashPhase = source.DashPhase;
            to.Color = source.Color.WithoutPattern;
            to.Font = source.Font;
            to.FontResourceName = source.FontResourceName;
            if (inheritsState) emitter.AssumeState(to);

            var child = new PsInterpreter(i.Options, i.Allocation, i.SharedBudget, i.Dicts) { Graphics = engine };
            int dictDepth = i.Dicts.Count;
            _depth++;
            try
            {
                child.Operands.Push(operand);
                var program = new List<PsObject>(procedure.Length);
                for (int k = 0; k < procedure.Length; k++) program.Add(procedure[k]);
                child.Run(program);
            }
            finally
            {
                _depth--;
                i.Dicts.TruncateTo(dictDepth);
            }

            byte[] content = emitter.ToBytes();
            i.ChargeAllocation(content.Length, op);
            resources = engine.Resources.Build() ?? new PdfDictionary();
            return content;
        }
    }
}
