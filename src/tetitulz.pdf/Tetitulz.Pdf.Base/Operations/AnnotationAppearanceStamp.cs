// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// §12.5.5's "algorithm: appearance streams" — how an annotation's <c>/AP /N</c> form is placed
    /// onto the page when its appearance is baked into the content, and which stream <c>/N</c>
    /// actually resolves to.
    /// </summary>
    /// <remarks>
    /// <para><b>Shared because there are two flatteners.</b> <c>PdfFormFlattener</c> burns in
    /// widgets and <c>PdfAnnotationEditor.Flatten</c> burns in everything else, and they must place
    /// a form the same way — the arithmetic below took two rounds of fixes to get right and is
    /// exactly the kind that drifts when it is typed twice. A comment asserting that two copies
    /// agree is a duplication report, so there is one copy.</para>
    /// <para><b>A leaf: no static fields, so no type initializer.</b> Same construction rule as
    /// <see cref="AnnotationQuadPoints"/> and <see cref="AnnotationFallbackModel"/>.</para>
    /// </remarks>
    internal static class AnnotationAppearanceStamp
    {
        /// <summary>
        /// The <c>/AP /N</c> stream for an annotation, following the appearance-state
        /// sub-dictionary when there is one, and promoting a direct stream to an indirect object so
        /// it can be named in <c>/Resources /XObject</c>.
        /// </summary>
        /// <param name="annot">The annotation or widget.</param>
        /// <param name="apDict">Its resolved <c>/AP</c> dictionary.</param>
        /// <param name="doc">The document, for resolving references.</param>
        /// <param name="nextObjNum">Next free object number; advanced if a copy has to be written.</param>
        /// <param name="objects">Receives the promoted copy, when one is made.</param>
        /// <param name="normalRef">A reference naming the stream, or null when none could be found.</param>
        /// <returns>The appearance stream, or <c>null</c>.</returns>
        /// <remarks>
        /// <b>A check box's <c>/AP /N</c> is not a stream.</b> §12.5.5 makes it a sub-dictionary of
        /// appearance <i>states</i> (<c>&lt;&lt; /Yes 12 0 R /Off 13 0 R &gt;&gt;</c>) selected by
        /// the widget's <c>/AS</c>, and radio buttons are the same shape. Reading it as
        /// <c>apDict[/N] as PdfReference</c> yields null for every one of them, so a checked box was
        /// skipped by the stamping loop and then erased with the rest of <c>/Annots</c> — the tick
        /// mark vanished from the flattened output entirely.
        /// </remarks>
        internal static PdfStream ResolveNormalAppearance(
            PdfDictionary annot, PdfDictionary apDict, PdfDocument doc,
            ref int nextObjNum, List<IndirectObject> objects, out PdfReference normalRef)
        {
            normalRef = null;

            var normalObj = apDict[PdfName.N];
            // PdfStream derives from PdfDictionary, so the stream case must be tested first.
            var apStream = doc.ResolveObject(normalObj) as PdfStream ?? normalObj as PdfStream;

            if (apStream == null)
            {
                var states = doc.ResolveObject(normalObj) as PdfDictionary ?? normalObj as PdfDictionary;
                if (states == null) return null;

                // Resolved: GetName does not follow a reference, and a null asName falls through
                // to SingleOnState below, which returns the single non-/Off entry — the CHECKED
                // appearance. So an UNCHECKED box whose /AS is an indirect /Off was flattened as
                // checked, and the form is then removed, leaving the true value unrecoverable. The
                // one read here that can invert a document's meaning.
                // A named state the dictionary does not carry is drawn as nothing
                // (AnnotationAppearance.TryNamedState); only a widget with no /AS falls back.
                PdfObject chosen = Rendering.AnnotationAppearance.TryNamedState(doc, annot, states, out var named)
                    ? named
                    : SingleOnState(states);
                if (chosen == null) return null;

                normalRef = chosen as PdfReference;
                apStream = doc.ResolveObject(chosen) as PdfStream ?? chosen as PdfStream;
                if (apStream == null) return null;
            }
            else
            {
                normalRef = normalObj as PdfReference;
            }

            if (normalRef == null)
            {
                // A direct appearance stream has no object number to name in /Resources /XObject,
                // so it is written out as one. Copying rather than renumbering the parsed instance
                // keeps the original document object untouched.
                int apObjNum = nextObjNum++;
                var copy = new PdfStream();
                foreach (var kvp in apStream) copy.Set(kvp.Key, kvp.Value);
                copy.RawData = apStream.RawData;
                copy.ObjectNumber = apObjNum;
                copy.GenerationNumber = 0;
                objects.Add(new IndirectObject(apObjNum, 0, copy));
                normalRef = new PdfReference(apObjNum, 0);
                apStream = copy;
            }

            return apStream;
        }

        /// <summary>
        /// The single non-<c>/Off</c> entry of an appearance-state dictionary, or <c>null</c> when
        /// there is not exactly one. Used when a widget carries no <c>/AS</c>, which real files do.
        /// </summary>
        internal static PdfObject SingleOnState(PdfDictionary states)
        {
            PdfObject only = null;
            foreach (var kvp in states)
            {
                if (kvp.Key == PdfName.Off) continue;
                if (only != null) return null;      // ambiguous — do not guess
                only = kvp.Value;
            }
            return only;
        }

        /// <summary>
        /// The content-stream fragment that draws <paramref name="apStream"/> inside the
        /// annotation's rectangle: clip, one mapping <c>cm</c>, and the <c>Do</c>.
        /// </summary>
        /// <param name="doc">The document, for resolving the stream's <c>/BBox</c> and <c>/Matrix</c>.</param>
        /// <param name="apStream">The appearance form.</param>
        /// <param name="xobjName">The resource name the form is registered under.</param>
        /// <param name="rx">Rectangle origin x, in page space.</param>
        /// <param name="ry">Rectangle origin y, in page space.</param>
        /// <param name="rw">Rectangle width.</param>
        /// <param name="rh">Rectangle height.</param>
        /// <remarks>
        /// <para><b>Two mistakes are avoided here, and they would compound.</b></para>
        /// <list type="number">
        ///   <item>The <c>/Matrix</c> is not emitted as an explicit <c>cm</c>: §8.10.1 makes
        ///   <c>Do</c> apply the form's <c>/Matrix</c> itself, so it would be composed twice, and a
        ///   90-degree-rotated widget (<c>/Matrix [0 1 -1 0 0 0]</c>) would come out rotated 180 from
        ///   where the un-flattened one renders.</item>
        ///   <item>The scale is not taken from the raw <c>/BBox</c> extents: with no translation by
        ///   (−bx0,−by0), and without transforming the box by <c>/Matrix</c> first, a <c>/BBox</c> of
        ///   <c>[10 10 110 30]</c> would draw 10 points right and up of the rectangle and be clipped
        ///   by the <c>re W n</c> box.</item>
        /// </list>
        /// <para>The algorithm: transform the four <c>/BBox</c> corners by <c>/Matrix</c>, take that
        /// axis-aligned bounding box, and emit the single <c>cm</c> mapping it onto the rectangle —
        /// scale plus the translation that cancels the transformed origin.</para>
        /// </remarks>
        internal static string BuildPlacement(
            PdfDocument doc, PdfStream apStream, string xobjName,
            double rx, double ry, double rw, double rh)
        {
            double bx0 = 0, by0 = 0, bx1 = rw, by1 = rh;
            var apBBox = PdfResolve.Array(doc, apStream, PdfName.BBox);
            // Each element resolved (§7.3.10 lets any be indirect), as every other reader of these
            // arrays resolves them: an indirect one read as 0 here, and the stamp was placed and
            // clipped by a box nothing else in the file had.
            if (apBBox != null && apBBox.Count >= 4)
            {
                bx0 = PdfResolve.ArrayDouble(doc, apBBox, 0);
                by0 = PdfResolve.ArrayDouble(doc, apBBox, 1);
                bx1 = PdfResolve.ArrayDouble(doc, apBBox, 2);
                by1 = PdfResolve.ArrayDouble(doc, apBBox, 3);
            }

            // Read /Matrix from the appearance stream (identity if absent). It is used only to map
            // the BBox here; `Do` applies it to the content itself.
            double ma = 1, mb = 0, mc = 0, md = 1, me = 0, mf = 0;
            var apMatrix = PdfResolve.Array(doc, apStream, PdfName.Matrix);
            if (apMatrix != null && apMatrix.Count >= 6)
            {
                ma = PdfResolve.ArrayDouble(doc, apMatrix, 0, 1);
                mb = PdfResolve.ArrayDouble(doc, apMatrix, 1);
                mc = PdfResolve.ArrayDouble(doc, apMatrix, 2);
                md = PdfResolve.ArrayDouble(doc, apMatrix, 3, 1);
                me = PdfResolve.ArrayDouble(doc, apMatrix, 4);
                mf = PdfResolve.ArrayDouble(doc, apMatrix, 5);
            }

            double tx0 = double.MaxValue, ty0 = double.MaxValue;
            double tx1 = double.MinValue, ty1 = double.MinValue;
            foreach (var corner in new[] { (x: bx0, y: by0), (x: bx1, y: by0),
                                           (x: bx1, y: by1), (x: bx0, y: by1) })
            {
                double px = ma * corner.x + mc * corner.y + me;
                double py = mb * corner.x + md * corner.y + mf;
                if (px < tx0) tx0 = px; if (px > tx1) tx1 = px;
                if (py < ty0) ty0 = py; if (py > ty1) ty1 = py;
            }

            double tw = tx1 - tx0, tht = ty1 - ty0;
            // A degenerate transformed box carries no scale information; map 1:1 rather than
            // dividing by zero and writing NaN into the content stream.
            double sx = tw > 1e-9 ? rw / tw : 1;
            double sy = tht > 1e-9 ? rh / tht : 1;

            var sb = new StringBuilder();
            sb.Append("q\n");
            sb.AppendFormat("{0} {1} {2} {3} re W n\n", Fmt(rx), Fmt(ry), Fmt(rw), Fmt(rh));
            sb.AppendFormat("{0} 0 0 {1} {2} {3} cm\n",
                Fmt(sx), Fmt(sy), Fmt(rx - sx * tx0), Fmt(ry - sy * ty0));
            sb.AppendFormat("/{0} Do\n", xobjName);
            sb.Append("Q\n");
            return sb.ToString();
        }

        /// <summary>
        /// The annotation's <c>/Rect</c> as origin plus extent, normalised so width and height are
        /// non-negative whichever way round the corners were written (§7.9.5 allows either).
        /// </summary>
        /// <param name="doc">The document, for resolving an indirect <c>/Rect</c>.</param>
        /// <param name="annot">The annotation.</param>
        /// <param name="rect">The normalised rectangle.</param>
        /// <returns>False when the annotation has no usable rectangle.</returns>
        internal static bool TryGetRect(
            PdfDocument doc, PdfDictionary annot, out (double X, double Y, double W, double H) rect)
        {
            rect = default;
            var arr = PdfResolve.Array(doc, annot, PdfName.Rect);
            if (arr == null || arr.Count < 4) return false;

            double x1 = PdfResolve.ArrayDouble(doc, arr, 0);
            double y1 = PdfResolve.ArrayDouble(doc, arr, 1);
            double x2 = PdfResolve.ArrayDouble(doc, arr, 2);
            double y2 = PdfResolve.ArrayDouble(doc, arr, 3);

            rect = (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
            return true;
        }

        private static string Fmt(double v) => PdfRealFormat.Format(v, "F4");
    }
}
