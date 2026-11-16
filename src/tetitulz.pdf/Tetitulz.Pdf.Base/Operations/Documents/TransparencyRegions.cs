// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// A page's content with its transparency taken out, and where what was taken out painted —
    /// flattening's stage 4: the rest of the page stays vector, and an image of the page covers only
    /// those regions.
    /// </summary>
    /// <remarks>
    /// <para><b>What counts as transparent</b> is read per top-level operation: a paint while the
    /// graphics state has a soft mask, an alpha below 1 or a blend mode other than Normal (any
    /// <c>/BM</c> entry, as <see cref="TransparencyScan"/> reads it); an image with opacity of its
    /// own; a form that reaches any transparency construct (<see cref="TransparencyScan.Reaches"/>).
    /// Each is taken out the way that keeps every other operation's meaning: a path's paint becomes
    /// <c>n</c>, so a clip it set still clips; text is shown invisibly (mode 3, or 7 where it also
    /// clips), so it stays text and the text position still advances; an image, a form, an inline
    /// image or a shading is dropped. The graphics states are made opaque and the transparent
    /// XObjects leave the resources, so nothing transparent is left for a validator to find.</para>
    /// <para><b>Why that is exact.</b> Everything outside the regions is untouched by what was taken
    /// out, so the vector content shows it as before; inside them the image of the page — taken from
    /// the original — shows everything, the opaque content above and below included. The opaque
    /// content stays under the image, so its text is still text.</para>
    /// <para><b>Where it gives up</b> (null, with the reason): a pattern or a Type 3 font in the
    /// page's resources that reaches transparency — those paint transparently from an operation that
    /// looks opaque, and the page is flattened whole.</para>
    /// </remarks>
    internal sealed class TransparencyRegions
    {
        private TransparencyRegions(byte[] content, PdfDictionary resources, List<SKRect> regions)
        {
            Content = content;
            Resources = resources;
            Regions = regions;
        }

        /// <summary>The page's content, every transparent painting operation taken out.</summary>
        internal byte[] Content { get; }

        /// <summary>The page's resources, with opaque graphics states and no transparent XObject.</summary>
        internal PdfDictionary Resources { get; }

        /// <summary>The bounds, in default user space, of what the operations taken out painted.</summary>
        internal IReadOnlyList<SKRect> Regions { get; }

        internal static TransparencyRegions TryRewrite(PdfPage page, PdfDocument doc, PdfRenderOptions renderOptions,
            out string whyNot)
        {
            PdfDictionary resources = page.Resources;
            whyNot = Unsupported(doc, resources);
            if (whyNot != null) return null;

            Dictionary<int, SKRect> bounds = PdfPageRenderer.MeasureOperations(page, doc, renderOptions, out var ops);
            var states = doc.ResolveObject(resources?[PdfName.ExtGState]) as PdfDictionary;
            var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
            var transparentXObjects = new Dictionary<string, bool>();

            var regions = new List<SKRect>();
            var text = new TextPaintTracker();
            var saved = new Stack<PaintState>();
            var state = PaintState.Opaque;
            int textObjects = 0;

            using var ms = new MemoryStream();
            var w = new PdfWriter(ms);
            for (int i = 0; i < ops.Count; i++)
            {
                ContentStreamOperation op = ops[i];
                bool shows = text.Observe(op.Operator, op.Operands);
                bool takenOut = false;

                switch (op.Operator)
                {
                    case PdfOperators.Save:
                        saved.Push(state);
                        break;
                    case PdfOperators.Restore:
                        // A Q with no q restores nothing, and would pop the state isolating this content.
                        if (saved.Count == 0) continue;
                        state = saved.Pop();
                        break;
                    case PdfOperators.SetExtGState:
                        if (FirstName(op) is PdfName gsName && doc.ResolveObject(states?[gsName]) is PdfDictionary gs)
                            state = state.With(doc, gs);
                        break;
                    case PdfOperators.BeginText: textObjects++; break;
                    case PdfOperators.EndText: if (textObjects > 0) textObjects--; break;

                    case PdfOperators.ShadingFill:
                    case PdfOperators.BeginInlineImage:
                        takenOut = state.Transparent;
                        break;

                    case PdfOperators.InvokeXObject:
                        takenOut = state.Transparent
                                   || (FirstName(op) is PdfName xName && XObjectIsTransparent(doc, xobjects, xName, transparentXObjects));
                        break;
                }

                if (ContentOperationRemoval.PaintsPath(op.Operator) && state.Transparent)
                {
                    Take(i);
                    // The path ends unpainted, and a clip set before it still takes effect.
                    ContentOperationRemoval.WritePathUnpainted(w);
                    continue;
                }

                if (shows && state.Transparent && text.Paints)
                {
                    Take(i);
                    ContentOperationRemoval.WriteTextInvisible(w, op, text);
                    continue;
                }

                if (takenOut)
                {
                    Take(i);
                    continue;
                }
                ContentOperationWriter.Write(w, op);
            }

            // Closed as the renderer closes them, so what follows this content starts clean.
            for (; textObjects > 0; textObjects--) w.WriteRaw(PdfOperators.EndText + "\n");
            for (int k = saved.Count; k > 0; k--) w.WriteRaw(PdfOperators.Restore + "\n");

            return new TransparencyRegions(ms.ToArray(), OpaqueResources(doc, resources), regions);

            void Take(int index)
            {
                if (bounds.TryGetValue(index, out SKRect r) && r.Width >= 0 && r.Height >= 0) regions.Add(r);
            }
        }

        /// <summary>Why the page cannot be flattened by regions, or null.</summary>
        private static string Unsupported(PdfDocument doc, PdfDictionary resources)
        {
            if (resources == null) return null;

            if (doc.ResolveObject(resources[PdfName.Pattern]) is PdfDictionary patterns)
                foreach (var kvp in patterns)
                {
                    PdfObject pattern = doc.ResolveObject(kvp.Value);
                    if (pattern is PdfStream tiling && PdfResolve.Int(doc, tiling, PdfName.PatternType, 0) == 1
                        && TransparencyScan.Reaches(doc, tiling))
                        return $"tiling pattern /{kvp.Key.Value} paints transparency";
                    if (pattern is PdfDictionary shading && PdfResolve.Int(doc, shading, PdfName.PatternType, 0) == 2
                        && doc.ResolveObject(shading[PdfName.ExtGState]) is PdfDictionary gs && TransparencyScan.IsTransparent(doc, gs))
                        return $"shading pattern /{kvp.Key.Value} has a transparent graphics state";
                }

            if (doc.ResolveObject(resources[PdfName.Font]) is PdfDictionary fonts)
                foreach (var kvp in fonts)
                    if (doc.ResolveObject(kvp.Value) is PdfDictionary font
                        && PdfResolve.Name(doc, font, PdfName.Subtype) == PdfName.Type3
                        && TransparencyScan.Reaches(doc, font))
                        return $"Type 3 font /{kvp.Key.Value} paints transparency";

            return null;
        }

        private static bool XObjectIsTransparent(PdfDocument doc, PdfDictionary xobjects, PdfName name,
            Dictionary<string, bool> memo)
        {
            if (memo.TryGetValue(name.Value, out bool known)) return known;
            bool transparent = false;
            if (doc.ResolveObject(xobjects?[name]) is PdfStream xobject)
            {
                PdfName subtype = PdfResolve.Name(doc, xobject, PdfName.Subtype);
                transparent = subtype == PdfName.Image ? TransparencyScan.IsTransparentImage(doc, xobject)
                            : subtype == PdfName.Form && TransparencyScan.Reaches(doc, xobject);
            }
            memo[name.Value] = transparent;
            return transparent;
        }

        /// <summary>
        /// The page's resources with each graphics state's soft mask, alphas and blend mode removed
        /// (every operation they made transparent is gone), and without the XObjects that carry
        /// transparency of their own (every use of them is gone).
        /// </summary>
        private static PdfDictionary OpaqueResources(PdfDocument doc, PdfDictionary resources)
        {
            var copy = resources == null ? new PdfDictionary() : PdfDictionary.CloneShallow(resources);

            if (doc.ResolveObject(copy[PdfName.ExtGState]) is PdfDictionary states)
            {
                var opaque = new PdfDictionary();
                foreach (var kvp in states)
                {
                    if (!(doc.ResolveObject(kvp.Value) is PdfDictionary gs)) continue;
                    if (!TransparencyScan.IsTransparent(doc, gs)) { opaque.Set(kvp.Key, kvp.Value); continue; }
                    var made = PdfDictionary.CloneShallow(gs);
                    made.Remove(PdfName.SMask);
                    made.Remove(PdfName.CA);
                    made.Remove(PdfName.ca);
                    made.Remove(PdfName.BM);
                    opaque.Set(kvp.Key, made);
                }
                copy.Set(PdfName.ExtGState, opaque);
            }

            if (doc.ResolveObject(copy[PdfName.XObject]) is PdfDictionary xobjects)
            {
                var kept = new PdfDictionary();
                var memo = new Dictionary<string, bool>();
                foreach (var kvp in xobjects)
                    if (!XObjectIsTransparent(doc, xobjects, kvp.Key, memo)) kept.Set(kvp.Key, kvp.Value);
                copy.Set(PdfName.XObject, kept);
            }
            return copy;
        }

        private static PdfName FirstName(ContentStreamOperation op)
        {
            foreach (var operand in op.Operands)
                if (operand is PdfName name) return name;
            return null;
        }

        /// <summary>The part of the graphics state that makes a paint transparent (§11.6.3–§11.6.5).</summary>
        private readonly struct PaintState
        {
            private PaintState(bool softMask, bool strokeAlpha, bool fillAlpha, bool blend)
            {
                SoftMask = softMask;
                StrokeAlpha = strokeAlpha;
                FillAlpha = fillAlpha;
                Blend = blend;
            }

            internal static PaintState Opaque => default;

            private bool SoftMask { get; }
            private bool StrokeAlpha { get; }
            private bool FillAlpha { get; }
            private bool Blend { get; }

            internal bool Transparent => SoftMask || StrokeAlpha || FillAlpha || Blend;

            /// <summary>This state with the entries <paramref name="gs"/> sets; the rest kept.</summary>
            internal PaintState With(PdfDocument doc, PdfDictionary gs)
            {
                bool softMask = SoftMask, strokeAlpha = StrokeAlpha, fillAlpha = FillAlpha, blend = Blend;
                PdfObject smask = doc.ResolveObject(gs[PdfName.SMask]);
                if (smask != null) softMask = !(smask is PdfName none && none == PdfName.None);
                if (PdfResolve.Number(doc, gs, PdfName.CA) is PdfNumber stroke) strokeAlpha = stroke.DoubleValue < 1;
                if (PdfResolve.Number(doc, gs, PdfName.ca) is PdfNumber fill) fillAlpha = fill.DoubleValue < 1;
                PdfObject bm = doc.ResolveObject(gs[PdfName.BM]);
                if (bm != null) blend = TransparencyScan.NamesASeparableOrOtherBlend(bm);
                return new PaintState(softMask, strokeAlpha, fillAlpha, blend);
            }
        }
    }
}
