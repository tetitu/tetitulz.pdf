// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Decisions about an annotation's appearance stream that both renderers must answer the same
    /// way: §12.7.3.3's <b>which resource dictionary its content runs against</b>, and §12.5.5's
    /// <b>which blend mode and constant alpha it is composited onto the page with</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>This was written out twice, verbatim</b>, in <c>PdfPageRenderer</c> and
    /// <c>PdfSvgRenderer</c> — the same three-branch ladder down to the <c>?? page.Resources</c>.
    /// It is a <i>value</i> decision, not a paint call, which is the category this project requires
    /// to have one implementation reached from both renderers (the <c>ResolveRenderBox</c> /
    /// <c>TryComputeColorKeyMask</c> pattern). The two agreed; nothing made them.</para>
    ///
    /// <para><b>A leaf: no static fields, hence no type initializer.</b> That is why it is its own
    /// type rather than an <c>internal static</c> on <c>PdfPageRenderer</c> — a call from the SVG
    /// renderer into the raster one is the sideways edge CLAUDE.md records crashing the Base test
    /// host mid-run.</para>
    /// </remarks>
    internal static class AnnotationAppearance
    {
        /// <summary>
        /// The entry of an appearance-state dictionary (§12.5.5) that the annotation's <c>/AS</c>
        /// selects. True when <c>/AS</c> names a state: <paramref name="entry"/> is that state's
        /// entry, or null when the dictionary does not carry it, and then nothing is drawn — a check
        /// box's off appearance is optional (§12.7.5.2.3), and drawing another entry draws the box
        /// checked. False when there is no <c>/AS</c>; the caller's own fallback then applies.
        /// </summary>
        /// <param name="doc">Resolves an indirect <c>/AS</c>.</param>
        /// <param name="annot">The annotation.</param>
        /// <param name="states">The resolved <c>/AP</c> <c>/N</c> (or <c>/D</c>, <c>/R</c>) dictionary.</param>
        /// <param name="entry">The selected entry, unresolved, or null.</param>
        internal static bool TryNamedState(PdfDocument doc, PdfDictionary annot, PdfDictionary states, out PdfObject entry)
        {
            entry = null;
            var asName = PdfResolve.Name(doc, annot, PdfName.AS);
            if (asName == null) return false;
            entry = states[asName];
            return true;
        }

        /// <summary>
        /// The normal appearance stream an annotation presents (§12.5.5): its <c>/AP</c> <c>/N</c>
        /// when that is a stream, else the state the annotation's <c>/AS</c> selects — null when
        /// the dictionary lacks it, since that state draws nothing — else, with no <c>/AS</c>, the
        /// first state that is a stream. Null when the annotation has no normal appearance to draw,
        /// which is the renderers' synthesised-fallback case.
        /// </summary>
        internal static PdfStream NormalAppearance(PdfDocument doc, PdfDictionary annot)
        {
            if (annot == null) return null;
            var apObj = annot[PdfName.AP];
            if (apObj == null) return null;
            var ap = doc.ResolveObject(apObj) as PdfDictionary ?? apObj as PdfDictionary;
            var nObj = ap?[PdfName.N];
            if (nObj == null) return null;
            var n = doc.ResolveObject(nObj);
            if (n is PdfStream stream) return stream;
            if (n is not PdfDictionary states) return null;
            if (TryNamedState(doc, annot, states, out PdfObject named))
                return named == null ? null : doc.ResolveObject(named) as PdfStream;
            foreach (var kv in states)
                if (doc.ResolveObject(kv.Value) is PdfStream first) return first;
            return null;
        }

        /// <summary>
        /// §12.5.5 Algorithm 8.1 for an appearance stream drawn in <paramref name="rect"/>: the
        /// stream's <c>/BBox</c> corners through its <c>/Matrix</c>, their bounding box, and the
        /// matrix <paramref name="fit"/> scaling and moving that box onto the rectangle; the content
        /// then runs under <paramref name="apMatrix"/> (the stream's own <c>/Matrix</c>, or null)
        /// concatenated inside the fit, clipped to <paramref name="bboxArr"/> (null when the stream
        /// declares none, which makes the fit the identity: the appearance is taken to fill the
        /// rectangle). False for a rectangle with no extent, where nothing is drawn — the one
        /// reading for both renderers, the imposer and the text extractor.
        /// </summary>
        internal static bool TryFitToRect(PdfDocument doc, PdfArray rect, PdfDictionary apStream,
                                          out float[] fit, out float[] apMatrix, out PdfArray bboxArr)
        {
            fit = null; apMatrix = null; bboxArr = null;
            if (rect == null || rect.Count < 4 || apStream == null) return false;
            float rx0 = System.Math.Min(PdfResolve.ArrayFloat(doc, rect, 0), PdfResolve.ArrayFloat(doc, rect, 2));
            float ry0 = System.Math.Min(PdfResolve.ArrayFloat(doc, rect, 1), PdfResolve.ArrayFloat(doc, rect, 3));
            float rx1 = System.Math.Max(PdfResolve.ArrayFloat(doc, rect, 0), PdfResolve.ArrayFloat(doc, rect, 2));
            float ry1 = System.Math.Max(PdfResolve.ArrayFloat(doc, rect, 1), PdfResolve.ArrayFloat(doc, rect, 3));
            return TryFitToRect(doc, rx0, ry0, rx1, ry1, apStream, out fit, out apMatrix, out bboxArr);
        }

        /// <inheritdoc cref="TryFitToRect(PdfDocument, PdfArray, PdfDictionary, out float[], out float[], out PdfArray)"/>
        internal static bool TryFitToRect(PdfDocument doc, float rx0, float ry0, float rx1, float ry1, PdfDictionary apStream,
                                          out float[] fit, out float[] apMatrix, out PdfArray bboxArr)
        {
            fit = null; apMatrix = null; bboxArr = null;
            float rw = rx1 - rx0, rh = ry1 - ry0;
            if (!(rw > 0f) || !(rh > 0f) || apStream == null) return false;

            bboxArr = PdfResolve.Array(doc, apStream, PdfName.BBox);
            if (bboxArr != null && bboxArr.Count < 4) bboxArr = null;
            var apMatrixArr = PdfResolve.Array(doc, apStream, PdfName.Matrix);
            float[] m = { 1, 0, 0, 1, 0, 0 };
            if (apMatrixArr != null && apMatrixArr.Count >= 6)
            {
                for (int i = 0; i < 6; i++) m[i] = PdfResolve.ArrayFloat(doc, apMatrixArr, i);
                apMatrix = m;
            }

            // The transformed /BBox's extent; with no /BBox the appearance fills /Rect and the fit
            // is the identity.
            float tbx0 = 0f, tby0 = 0f, tbw = rw, tbh = rh;
            if (bboxArr != null)
            {
                PdfMatrix.TransformedBounds(m,
                    PdfResolve.ArrayFloat(doc, bboxArr, 0), PdfResolve.ArrayFloat(doc, bboxArr, 1),
                    PdfResolve.ArrayFloat(doc, bboxArr, 2), PdfResolve.ArrayFloat(doc, bboxArr, 3),
                    out tbx0, out tby0, out float tbx1, out float tby1);
                tbw = tbx1 - tbx0;
                tbh = tby1 - tby0;
            }
            float sx = tbw > 0f ? rw / tbw : 1f;
            float sy = tbh > 0f ? rh / tbh : 1f;
            fit = new[] { sx, 0f, 0f, sy, rx0 - tbx0 * sx, ry0 - tby0 * sy };
            return true;
        }

        /// <summary>
        /// The resource dictionary an appearance stream's content is interpreted against
        /// (§12.7.3.3): the stream's own <c>/Resources</c>, else the AcroForm's <c>/DR</c>, else
        /// the page's.
        /// </summary>
        /// <remarks>
        /// <para><b>An unusable <c>/Resources</c> yields null rather than falling through to
        /// <c>/DR</c>, and that is preserved deliberately.</b> When the key is present but does not
        /// resolve to a dictionary, the stream has <i>stated</i> its resources and the statement is
        /// broken; treating that as "absent" would silently run the content against a different
        /// dictionary and draw with the wrong fonts rather than with none. Both copies behaved this
        /// way, so it is the established contract, not an accident being locked in.</para>
        /// </remarks>
        /// <param name="apStream">The appearance stream's dictionary.</param>
        /// <param name="doc">Resolves references and supplies the AcroForm.</param>
        /// <param name="page">Supplies the last-resort resources.</param>
        internal static PdfDictionary ResolveResources(
            PdfDictionary apStream, PdfDocument doc, PdfPage page)
        {
            if (apStream?[PdfName.Resources] is PdfObject own)
                return doc?.ResolveObject(own) as PdfDictionary;

            var dr = doc?.AcroForm?.Dictionary?[PdfName.DR];
            return (dr != null ? doc.ResolveObject(dr) as PdfDictionary : null)
                   ?? page?.Resources;
        }

        /// <summary>
        /// The blend mode the annotation is composited onto the page with — Table 166's <c>/BM</c>
        /// (ISO 32000-2, lines 38548–38554 of <c>docs/iso/iso32000-2.txt</c>), "the blend mode that
        /// shall be used when painting the annotation onto the page" — as a
        /// <see cref="Tetitulz.Pdf.Core.Constants.PdfBlendModes"/> name, or <see langword="null"/>
        /// when the annotation names none.
        /// </summary>
        /// <remarks>
        /// Reached from both renderers, for the appearance stream (§12.5.5, 38926–38928, which
        /// composites the appearance "using the values of the BM, ca and CA entries") and for the
        /// synthesised fallback alike. Neither read it before: a <c>/Multiply</c> stamp over text
        /// painted opaque Normal in both, which comparing them could never show. The value goes
        /// through <see cref="PdfBlendModeEntry.FirstRecognised"/>, the same decision the <c>gs</c>
        /// operator's <c>/BM</c> takes, so an array is read the same way here as there.
        /// </remarks>
        internal static string BlendMode(PdfDictionary annot, PdfDocument doc)
            => PdfBlendModeEntry.FirstRecognised(doc, annot?[PdfName.BM]);

        /// <summary>
        /// The constant alpha the annotation's appearance stream is composited at: <c>/ca</c>, else
        /// <c>/CA</c>, else 1, clamped to [0, 1].
        /// </summary>
        /// <remarks>
        /// <para><b>Why <c>/ca</c> first.</b> §12.5.5 (ISO 32000-2, 38926–38928) composites the
        /// appearance's transparency group "using the values of the BM, ca and CA entries in the
        /// annotation dictionary", and a group composite has one alpha. §11.6.4.4 (34121–34122) says
        /// which: "The nonstroking alpha constant shall also be applied when painting a transparency
        /// group's results onto its backdrop." Table 166's <c>/ca</c> (38491–38497) is that
        /// nonstroking value, and its <c>/CA</c> row (38526–38527) supplies the fallback — "If a ca
        /// entry is not present in this dictionary, then the value of this CA entry shall also be
        /// used for nonstroking operations as well." So: <c>/ca</c>, else <c>/CA</c>.</para>
        ///
        /// <para>Only <c>/CA</c> was read, in both renderers. Table 166 also says of both entries
        /// that they "shall not be used if the annotation has an appearance stream" (38500–38502,
        /// 38528–38530), which §12.5.5's own compositing sentence contradicts; the algorithm clause
        /// is the one that says what to do with an appearance stream, the existing <c>/CA</c>
        /// behaviour was built on it, and this extends it to the other two entries it names rather
        /// than reopening that choice.</para>
        ///
        /// <para>The synthesised fallback is a different case and keeps reading <c>/CA</c> for both
        /// stroke and fill: Table 166 calls an annotation painted without an appearance stream
        /// implementation-dependent, and one SVG group carries one opacity, so splitting the two
        /// there would make the two renderers disagree.</para>
        /// </remarks>
        internal static float CompositeAlpha(PdfDictionary annot, PdfDocument doc)
        {
            var alpha = PdfResolve.Number(doc, annot, PdfName.ca)
                        ?? PdfResolve.Number(doc, annot, PdfName.CA);
            if (alpha == null) return 1f;
            return Math.Max(0f, Math.Min(1f, (float)alpha.DoubleValue));
        }
    }
}
