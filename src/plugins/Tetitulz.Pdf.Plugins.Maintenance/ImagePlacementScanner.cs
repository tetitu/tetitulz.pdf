// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Works out the resolution at which each image XObject is actually drawn, in dots per inch.
    ///
    /// <para><b>Why this exists.</b> An image XObject has no resolution of its own: §8.9.5.2 says its
    /// samples are mapped onto the unit square of user space, so how many dots per inch it resolves
    /// to is entirely a property of the CTM in force at the <c>Do</c> that paints it
    /// (<c>/XResolution</c> is not a key of ISO 32000-1). <see cref="ImageOptimizationOptions.TargetDpi"/>
    /// is measured against what this finds.</para>
    ///
    /// <para>An image painted more than once keeps its <b>largest placement</b>, which is its
    /// <b>LOWEST</b> effective dpi — the same samples spread over more points. Downsampling to fit
    /// a smaller placement would visibly degrade the biggest one. An image that is never painted is
    /// absent from the result and is left alone — there is no evidence about how it is used.</para>
    ///
    /// <para><b>The largest placement is the minimum dpi.</b> Keeping the maximum would keep the
    /// <i>smallest</i> placement and do the harm the paragraph above exists to prevent: an image
    /// drawn once at 600 dpi (small) and once at 72 dpi (large), with <c>TargetDpi</c> 150, would be
    /// scaled by <c>150/600 = 0.25</c> and the large placement would drop to 18 dpi; by the minimum,
    /// <c>150/72 &gt; 1</c> and nothing is resampled, which is correct. This is the ACROSS-placement
    /// rule only — <c>Math.Max(dpiX, dpiY)</c> WITHIN one placement is right, because the
    /// higher-resolution axis is what the samples must still serve.</para>
    ///
    /// <para><b>Where a paint site can be, and which of them are walked.</b> An image is not only
    /// drawn from page content: it can be drawn from a form XObject, an annotation appearance
    /// stream, a tiling pattern, a soft-mask group, or a Type 3 glyph procedure. The first four are
    /// walked. Type 3 glyph procedures are <b>not</b> — the CTM inside one is the font matrix
    /// composed with the text matrix, and this scanner tracks no text state, so a placement computed
    /// there would be a fabricated resolution rather than a measured one. Selecting a Type 3 font
    /// that has glyph procedures therefore sets <c>reason</c>, as does every other refusal to walk.
    /// <b>A caller that ignores <c>reason</c> is resampling on evidence that was never complete</b>:
    /// a paint site the scan did not reach can have been at any size, so which way it would have
    /// moved the decision is unknowable — which is the whole reason the map is advisory once
    /// <c>reason</c> is set.</para>
    /// </summary>
    internal static class ImagePlacementScanner
    {
        // A page can legitimately nest form XObjects; a malformed or hostile one can nest them
        // forever. Both the depth and the total operator count are bounded.
        private const int MaxFormDepth = 16;
        private const int MaxOperations = 2_000_000;

        /// <summary>
        /// The fewest bytes a content stream can spend on one operation: a one-character operator
        /// and the separator that ends it. Used to refuse a stream the operator budget could not
        /// walk to its end anyway — see <see cref="Walk"/>.
        /// </summary>
        private const int MinBytesPerOperation = 2;

        /// <summary>
        /// Bytes of content this scan may tokenize in all, across every walk — so a stream re-walked many
        /// times (a form invoked once per <c>Do</c>) cannot be re-parsed for free. Parsing a sparse stream
        /// costs zero operations, which left the sum of all walks unbounded; a byte budget of its own
        /// bounds the tokenizing without touching the per-operation accounting.
        /// </summary>
        private const long MaxTokenizedBytes = 512L * 1024 * 1024;

        /// <summary>
        /// Maps image XObject object number → the lowest effective DPI at which it is drawn (its largest
        /// placement; the class remarks say why it is the lowest).
        /// </summary>
        /// <param name="reason">
        /// Non-null when the scan could not be completed. The caller must then treat the map as
        /// advisory: an image missing from it may simply be one the scan never reached, and an image
        /// present in it may be drawn larger somewhere the scan did not go.
        /// </param>
        /// <param name="objects">Every object in the document, by object number.</param>
        /// <param name="trailer">The trailer, for reaching the catalog and hence the page tree.</param>
        /// <param name="options">
        /// Optimization options, read for the walk's budget and recursion ceilings.
        /// </param>
        /// <param name="use">
        /// When non-null, also records which <c>/XObject</c> names every resource dictionary had
        /// invoked and which holders' <c>/Resources</c> the walk used — the evidence
        /// <see cref="UnusedXObjectResources"/> removes entries on. The walk itself is unchanged.
        /// </param>
        internal static Dictionary<int, double> Scan(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer,
            OptimizationOptions options,
            out string reason,
            XObjectUse use = null)
        {
            var state = new ScanState { Use = use };

            // `trailer?[…]`, not `trailer[…]`: "no trailer" is answered with a reason, as
            // FontGlyphUsageScanner answers it.
            if (!(Deref(trailer?[PdfName.Root], objects) is PdfDictionary catalog))
            {
                reason = "the document has no resolvable catalog";
                return state.Placements;
            }

            foreach (var entry in PageTreeWalk.Enumerate(objects, catalog))
            {
                PdfDictionary page = entry.Page;
                // §7.7.3.4: /Resources is inheritable. Reading only the page's own entry would leave
                // this null on every page of a file that hoists them onto the /Pages node, so no
                // placement would be recorded and TargetDpi would do nothing while `reason` stayed null.
                PdfDictionary resources =
                    Deref(page[PdfName.Resources], objects) as PdfDictionary ?? entry.InheritedResources;

                bool partUnread = false;
                byte[] content = ConcatenatePageContent(page, objects, options, state, ref partUnread,
                    out bool overTotal);
                if (overTotal)
                    state.Reason = "a page's content parts together are larger than the remaining operator " +
                        "budget could walk";
                state.Use?.NotePage(page, resources, entry.InheritedResources, objects, partUnread);
                Matrix pageBase = PageBaseMatrix(page, objects);
                if (content != null && content.Length > 0)
                {
                    try
                    {
                        Walk(content, resources, pageBase, pageBase, objects, options,
                            state, new HashSet<int>(), 0);
                    }
                    catch (Exception ex)
                    {
                        state.Reason = "a page's content stream could not be walked: " + ex.GetType().Name;
                    }
                }

                // Inside a guard, like the page walk above it: ContentStreamParser can throw (an
                // inline image over 256 MiB with no EI), an appearance stream is a content stream
                // like any other, and unguarded the exception would propagate out of Scan and out of
                // PdfOptimizer.Optimize. FontGlyphUsageScanner routes every walk through WalkGuarded
                // for the same reason.
                try
                {
                    ScanAnnotations(page, resources, pageBase, objects, options, state);
                }
                catch (Exception ex)
                {
                    state.Reason = "an annotation's appearance stream could not be walked: "
                                   + ex.GetType().Name;
                }

                if (state.Budget <= 0)
                {
                    state.Reason = "the operator budget was exhausted";
                    break;
                }
            }

            reason = state.Reason;
            return state.Placements;
        }

        /// <summary>Everything one scan accumulates, so the walk does not thread five ref parameters.</summary>
        private sealed class ScanState
        {
            internal readonly Dictionary<int, double> Placements = new Dictionary<int, double>();
            internal int Budget = MaxOperations;
            internal string Reason;

            /// <summary>Content bytes this scan may still tokenize, across every walk (<see cref="MaxTokenizedBytes"/>).</summary>
            internal long TokenizeBytesLeft = MaxTokenizedBytes;

            /// <summary>
            /// Annotation appearances this scan may still visit, across every page
            /// (<see cref="PdfLimits.MaxAnnotationsPerDocument"/>). Pages, a page's <c>/Annots</c> and an
            /// annotation's <c>/AP</c> states can every one be a shared object, so their product bounds
            /// the walk. (A shared stream is not de-duplicated here as it is in the font scanner, because
            /// an appearance's placement — its annotation's <c>/Rect</c> — differs per annotation.)
            /// </summary>
            internal int AnnotationAppearancesLeft = PdfLimits.MaxAnnotationsPerDocument;
            internal XObjectUse Use;
        }

        /// <summary>
        /// <paramref name="ctm"/> is the transform in force; <paramref name="baseCtm"/> is the one
        /// this content stream started with. They differ once a <c>cm</c> has run, and both are
        /// needed: an image is placed by the CTM, but §8.7.3.1 maps a pattern's <c>/Matrix</c> onto
        /// the <i>default</i> space of the content stream that uses the pattern, not onto whatever
        /// the CTM happens to be at the <c>scn</c>.
        /// </summary>
        private static void Walk(
            byte[] content,
            PdfDictionary resources,
            Matrix ctm,
            Matrix baseCtm,
            Dictionary<int, PdfObject> objects,
            OptimizationOptions options,
            ScanState state,
            HashSet<int> inProgress,
            int depth)
        {
            if (state.Budget <= 0) return;

            // ContentStreamParser.Parse materialises one operation per operator *before* the caller
            // sees any of them, and its own doc comment puts the bound on the caller: "check the
            // byte count before calling". Decrementing the budget per operation therefore bounded
            // nothing — the whole list already existed. Two bytes is the least one operation can
            // occupy, so a stream longer than twice the remaining budget cannot be walked to its
            // end whatever it holds.
            if (content != null && content.LongLength > (long)state.Budget * MinBytesPerOperation)
            {
                state.Reason = "a content stream of " + content.LongLength +
                    " bytes is larger than the remaining operator budget could walk";
                return;
            }

            // Charge the tokenizing to a budget of its own, so the same stream re-walked many times
            // cannot be re-parsed for free (a sparse stream parses to zero operations): the sum of all
            // walks' bytes is bounded, and the per-operation accounting is untouched.
            if (content != null)
            {
                if (content.LongLength > state.TokenizeBytesLeft)
                {
                    state.Reason = "the content streams reached together are larger than the scan can tokenize";
                    return;
                }
                state.TokenizeBytesLeft -= content.LongLength;
            }

            var operations = ContentStreamParser.Parse(content);
            var stack = new Stack<Matrix>();
            state.Use?.NoteWalked(resources);

            foreach (var op in operations)
            {
                if (--state.Budget <= 0) return;

                switch (op.Operator)
                {
                    case "q":
                        stack.Push(ctm);
                        break;

                    case "Q":
                        if (stack.Count > 0) ctm = stack.Pop();
                        break;

                    case "cm":
                        if (op.Operands.Length >= 6)
                        {
                            // Core.Text.ContentOperand is the repository's operand-coercion helper
                            // but it is internal to Core, so a plugin cannot reach it. PdfNumber is
                            // public and this needs only the numeric case.
                            var m = new Matrix(
                                OperandAt(op.Operands, 0),
                                OperandAt(op.Operands, 1),
                                OperandAt(op.Operands, 2),
                                OperandAt(op.Operands, 3),
                                OperandAt(op.Operands, 4),
                                OperandAt(op.Operands, 5));
                            ctm = Matrix.Concat(m, ctm);
                        }
                        break;

                    case "Do":
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName xobjectName)
                        {
                            HandleDo(xobjectName, resources, ctm, objects, options, state,
                                inProgress, depth);
                        }
                        break;

                    case "gs":
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName gsName)
                        {
                            WalkSoftMask(gsName, resources, ctm, objects, options, state,
                                inProgress, depth);
                            NoteGraphicsStateFont(gsName, resources, objects, state);
                        }
                        break;

                    case "scn":
                    case "SCN":
                        if (op.Operands.Length >= 1 &&
                            op.Operands[op.Operands.Length - 1] is PdfName patternName)
                        {
                            WalkPattern(patternName, resources, baseCtm, objects, options, state,
                                inProgress, depth);
                        }
                        break;

                    case "Tf":
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName fontName)
                            NoteType3Font(fontName, resources, objects, state);
                        break;
                }
            }
        }

        private static void HandleDo(
            PdfName xobjectName,
            PdfDictionary resources,
            Matrix ctm,
            Dictionary<int, PdfObject> objects,
            OptimizationOptions options,
            ScanState state,
            HashSet<int> inProgress,
            int depth)
        {
            state.Use?.NoteInvoked(resources, xobjectName);
            var xobjects = Deref(resources?[PdfName.XObject], objects) as PdfDictionary;
            var entry = xobjects?[xobjectName];

            // A `Do` naming something that will not resolve could have painted any image in the
            // file at any size, so the evidence this scan gathered is not complete.
            if (!(entry is PdfReference reference) || !(Deref(entry, objects) is PdfStream xobject))
            {
                state.Reason = "XObject /" + xobjectName.Value + " could not be resolved at a Do";
                return;
            }

            var subtype = Deref(xobject[PdfName.Subtype], objects) as PdfName;
            if (subtype == null)
            {
                state.Reason = "XObject /" + xobjectName.Value + " declares no /Subtype";
                return;
            }

            if (subtype.Value == "Image")
            {
                RecordPlacement(reference.ReferenceObjectNumber, xobject, ctm, objects, state);
                return;
            }

            if (subtype.Value != "Form") return;

            var formMatrix = Deref(xobject[PdfName.Matrix], objects) as PdfArray;
            var inner = formMatrix != null && formMatrix.Count >= 6
                ? Matrix.Concat(MatrixFrom(formMatrix), ctm)
                : ctm;

            WalkNestedStream(xobject, reference.ReferenceObjectNumber, resources, inner, objects,
                options, state, inProgress, depth);
        }

        /// <summary>
        /// The CTM a page's content starts from, in 1/72-inch units: the identity scaled by the page's
        /// own <c>/UserUnit</c> (Table 30, not inheritable; the default when absent, unreadable or not
        /// a finite positive number). A placement's dpi is pixels over inches, and on a
        /// <c>/UserUnit 4</c> page each unit is four points, so a scan in user units overstates the
        /// dpi four times and <c>TargetDpi</c> resamples far below what the page prints at.
        /// </summary>
        private static Matrix PageBaseMatrix(PdfDictionary page, Dictionary<int, PdfObject> objects)
        {
            double userUnit = (Deref(page[PdfName.UserUnit], objects) as PdfNumber)?.DoubleValue ?? 1.0;
            if (double.IsNaN(userUnit) || double.IsInfinity(userUnit) || userUnit <= 0 || userUnit == 1.0)
                return Matrix.Identity;
            return new Matrix(userUnit, 0, 0, userUnit, 0, 0);
        }

        private static void RecordPlacement(int objectNumber, PdfStream image, Matrix ctm,
            Dictionary<int, PdfObject> objects, ScanState state)
        {
            int pixelWidth = (Deref(image[PdfName.Width], objects) as PdfNumber)?.IntValue ?? 0;
            int pixelHeight = (Deref(image[PdfName.Height], objects) as PdfNumber)?.IntValue ?? 0;
            if (pixelWidth <= 0 || pixelHeight <= 0) return;

            // §8.9.5.2: the image occupies the unit square of the space the CTM defines, so its
            // painted extent is the length of the transformed unit vectors.
            double extentX = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B);
            double extentY = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
            if (extentX <= 0.0001 || extentY <= 0.0001) return;

            double dpiX = pixelWidth * PdfUnits.PointsPerInch / extentX;
            double dpiY = pixelHeight * PdfUnits.PointsPerInch / extentY;
            double dpi = Math.Max(dpiX, dpiY);

            // MIN, not max: the largest placement is the lowest dpi. See the type's summary for the
            // worked example — under max, an image drawn once small and once large is resampled to
            // fit the SMALL one and the large placement is destroyed.
            if (!state.Placements.TryGetValue(objectNumber, out double existing) || dpi < existing)
                state.Placements[objectNumber] = dpi;
        }

        /// <summary>
        /// Walks a content stream reached from another one — a form XObject, an appearance stream, a
        /// soft-mask group or a tiling pattern. <paramref name="ctm"/> is the transform already in
        /// force <i>inside</i> it, so any <c>/Matrix</c> the caller had to honour is composed in
        /// before the call. It becomes the base CTM for anything that stream reaches.
        /// </summary>
        private static void WalkNestedStream(
            PdfStream stream,
            int objectNumber,
            PdfDictionary parentResources,
            Matrix ctm,
            Dictionary<int, PdfObject> objects,
            OptimizationOptions options,
            ScanState state,
            HashSet<int> inProgress,
            int depth)
        {
            if (stream == null) return;
            state.Use?.NoteHolder(stream);

            if (depth >= MaxFormDepth)
            {
                state.Reason = "a content stream nested more than " + MaxFormDepth +
                    " levels deep was not walked";
                return;
            }

            // A stream drawing itself, directly or through a chain, is impossible to follow.
            if (objectNumber >= 0 && !inProgress.Add(objectNumber))
            {
                state.Reason = "object " + objectNumber + " draws itself, directly or through a " +
                    "chain, so its content could not be walked in full";
                return;
            }

            try
            {
                if (!stream.TryGetDecodedData(options.MaxStreamDecodeBytes, out byte[] inner))
                {
                    state.Reason = "a nested content stream could not be decoded";
                    return;
                }
                if (inner == null || inner.Length == 0) return;

                var innerResources = Deref(stream[PdfName.Resources], objects) as PdfDictionary
                                     ?? parentResources;

                Walk(inner, innerResources, ctm, ctm, objects, options, state, inProgress, depth + 1);
            }
            finally
            {
                if (objectNumber >= 0) inProgress.Remove(objectNumber);
            }
        }

        /// <summary>
        /// A soft-mask group (§11.6.5) is a form XObject, and its content is painted — an image
        /// inside it is an image the document draws.
        /// </summary>
        private static void WalkSoftMask(PdfName gsName, PdfDictionary resources, Matrix ctm,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, ScanState state,
            HashSet<int> inProgress, int depth)
        {
            // An ExtGState that will not resolve is left silent, unlike an unresolvable `Do`: `gs`
            // is overwhelmingly a line-width or alpha change, so it is no evidence that an image was
            // painted, whereas a `Do` this scan could not follow is a paint operation outright.
            var gs = Lookup(resources, PdfName.ExtGState, gsName, objects) as PdfDictionary;
            if (gs == null) return;
            if (!(Deref(gs[PdfName.SMask], objects) is PdfDictionary smask)) return;

            var group = smask[PdfName.G];
            if (!(Deref(group, objects) is PdfStream form)) return;

            // The same rule the font scanner applies, via the shared helper, so the two walk the same
            // graph: a group declaring a subtype other than /Form has no content stream to walk, and
            // is skipped and reported rather than parsed as operators.
            if (!ContentScanGraph.ShouldWalkSoftMaskGroup(form, objects, out string refusal))
            {
                if (refusal != null) state.Reason = refusal;
                return;
            }

            int number = group is PdfReference reference ? reference.ReferenceObjectNumber : -1;
            var matrix = Deref(form[PdfName.Matrix], objects) as PdfArray;
            var inner = matrix != null && matrix.Count >= 6
                ? Matrix.Concat(MatrixFrom(matrix), ctm)
                : ctm;

            WalkNestedStream(form, number, resources, inner, objects, options, state, inProgress, depth);
        }

        /// <summary>
        /// A tiling pattern (§8.7.3) is a content stream that may paint images. Its <c>/Matrix</c>
        /// maps pattern space onto the default space of the stream that <i>uses</i> the pattern,
        /// which is why <paramref name="baseCtm"/> and not the current CTM is what it composes with.
        /// </summary>
        private static void WalkPattern(PdfName patternName, PdfDictionary resources, Matrix baseCtm,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, ScanState state,
            HashSet<int> inProgress, int depth)
        {
            var pattern = Lookup(resources, PdfName.Pattern, patternName, objects) as PdfStream;
            if (pattern == null) return;                 // a shading pattern has no content stream

            var patternType = Deref(pattern[PdfName.PatternType], objects) as PdfNumber;
            if (patternType != null && patternType.IntValue != 1) return;

            int number = LookupReference(resources, PdfName.Pattern, patternName, objects);
            var matrix = Deref(pattern[PdfName.Matrix], objects) as PdfArray;
            var inner = matrix != null && matrix.Count >= 6
                ? Matrix.Concat(MatrixFrom(matrix), baseCtm)
                : baseCtm;

            WalkNestedStream(pattern, number, resources, inner, objects, options, state, inProgress, depth);
        }

        /// <summary>
        /// A Type 3 glyph procedure is a content stream and may paint an image, but its CTM is the
        /// font matrix composed with the text matrix and this scanner tracks neither. Guessing would
        /// under-state the placement, which is the direction that destroys samples, so the font is
        /// recorded as a hole in the scan instead.
        /// </summary>
        private static void NoteType3Font(PdfName fontName, PdfDictionary resources,
            Dictionary<int, PdfObject> objects, ScanState state)
        {
            if (Lookup(resources, PdfName.Font, fontName, objects) is PdfDictionary font)
                NoteType3Font(font, "font /" + fontName.Value, objects, state);
        }

        /// <summary>
        /// The same hole for the font an ExtGState's <c>/Font</c> entry selects (§8.4.5 Table 58): both
        /// renderers honour it, so a Type 3 font chosen that way runs its procedures too, and without the
        /// hole the unused-XObject pass would remove an image only those procedures paint.
        /// </summary>
        private static void NoteGraphicsStateFont(PdfName gsName, PdfDictionary resources,
            Dictionary<int, PdfObject> objects, ScanState state)
        {
            if (!(Lookup(resources, PdfName.ExtGState, gsName, objects) is PdfDictionary gs)) return;
            if (!(Deref(gs[PdfName.Font], objects) is PdfArray entry) || entry.Count == 0) return;
            if (Deref(entry[0], objects) is PdfDictionary font)
                NoteType3Font(font, "the font graphics state /" + gsName.Value + " selects", objects, state);
        }

        private static void NoteType3Font(PdfDictionary font, string described,
            Dictionary<int, PdfObject> objects, ScanState state)
        {
            if (!(Deref(font[PdfName.Subtype], objects) is PdfName subtype)) return;
            if (subtype.Value != "Type3") return;
            if (!(Deref(font[PdfName.CharProcs], objects) is PdfDictionary procs) || procs.Count == 0)
                return;

            state.Reason = described + " is a Type 3 font, whose glyph procedures " +
                "are content streams this scanner does not walk";
        }

        private static void ScanAnnotations(PdfDictionary page, PdfDictionary pageResources, Matrix pageBase,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, ScanState state)
        {
            foreach (var (stream, number, annot) in
                     ContentScanGraph.AnnotationAppearanceStreams(page, objects))
            {
                // Pages × /Annots entries × /AP states, all able to share one object, multiply without
                // bound; an empty appearance spends no operation budget. Charge every appearance to the
                // document-wide cap so the walk cannot run for minutes on a small hostile file.
                if (state.AnnotationAppearancesLeft <= 0)
                {
                    state.Reason ??= "more than " + PdfLimits.MaxAnnotationsPerDocument +
                        " annotation appearances were referenced; the rest were not scanned";
                    return;
                }
                state.AnnotationAppearancesLeft--;

                WalkAppearance(stream, number, annot, pageResources, pageBase, objects, options, state);

                if (state.Budget <= 0) return;
            }
        }

        /// <summary>
        /// §12.5.5's appearance algorithm: the form's <c>/BBox</c> is transformed by its
        /// <c>/Matrix</c>, and the bounding box of the result is mapped onto the annotation's
        /// <c>/Rect</c>. Without that mapping a stamp annotation's image would be measured against
        /// the appearance's own coordinates rather than the size it is drawn at on the page.
        /// </summary>
        private static void WalkAppearance(PdfStream appearance, int objectNumber,
            PdfDictionary annot, PdfDictionary pageResources, Matrix pageBase, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, ScanState state)
        {
            if (!TryAppearanceMatrix(appearance, annot, objects, out Matrix ctm))
            {
                state.Reason = "an annotation appearance stream has no /Rect and /BBox pair, so the " +
                    "size it is drawn at is unknown";
                return;
            }

            // The /Rect is in the page's user space, which the page's /UserUnit scales.
            WalkNestedStream(appearance, objectNumber, pageResources, Matrix.Concat(ctm, pageBase), objects,
                options, state, new HashSet<int>(), 0);
        }

        /// <summary>
        /// §12.5.5 Algorithm 8.1: map the four <c>/BBox</c> corners through <c>/Matrix</c>, take the
        /// bounding box of the result, and build the transform that fits that box onto the
        /// annotation's <c>/Rect</c>. <c>/Matrix</c> stays composed in front of the fit, so the
        /// returned CTM is <c>/Matrix</c> then A.
        ///
        /// <para><b>This is the fourth body of that algorithm in the tree</b> —
        /// <c>PdfPageRenderer</c>, <c>PdfSvgRenderer</c> and <c>PdfFormFlattener</c> carry the other
        /// three, all in <c>Tetitulz.Pdf.Base</c>. It cannot be merged from here: the renderers'
        /// shared half (<c>PdfPageRenderer.TransformedBBox</c>) is <c>internal</c> to Base and a
        /// plugin may not reach Base internals, so the shared body has to go <i>down</i> into Core
        /// before any of the four can call it.</para>
        ///
        /// <para><b>Two differences from the Base three are deliberate, not drift.</b> A degenerate
        /// transformed box is refused here (below) where a renderer falls back to scale 1 and paints
        /// — a renderer must draw, an optimiser must not fabricate a resolution. And the
        /// <c>/Rotate</c> | <c>/MK /R</c> turn about the centre is absent here because it cannot
        /// change this scanner's answer: <c>RecordPlacement</c> measures the <i>lengths</i> of the
        /// transformed unit vectors, and a rotation is an isometry.</para>
        ///
        /// <para><b>The composition order is pinned, not asserted in prose.</b> The only appearance
        /// fixture that existed left <c>/Matrix</c> at identity, under which dropping the
        /// composition, dropping the corner mapping and swapping the two operands are all invisible;
        /// <c>ScannerCompletenessTests.PlacementScan_ComposesTheAppearanceMatrixAheadOfTheBBoxToRectFit</c>
        /// uses a rotation with a non-uniform fit, which separates them.</para>
        /// </summary>
        private static bool TryAppearanceMatrix(PdfStream appearance, PdfDictionary annot,
            Dictionary<int, PdfObject> objects, out Matrix ctm)
        {
            ctm = Matrix.Identity;

            var rect = Deref(annot[PdfName.Rect], objects) as PdfArray;
            var bbox = Deref(appearance[PdfName.BBox], objects) as PdfArray;
            if (rect == null || rect.Count < 4 || bbox == null || bbox.Count < 4) return false;

            var matrixArray = Deref(appearance[PdfName.Matrix], objects) as PdfArray;
            Matrix matrix = matrixArray != null && matrixArray.Count >= 6
                ? MatrixFrom(matrixArray)
                : Matrix.Identity;

            double bx0 = Math.Min(NumberAt(bbox, 0), NumberAt(bbox, 2));
            double bx1 = Math.Max(NumberAt(bbox, 0), NumberAt(bbox, 2));
            double by0 = Math.Min(NumberAt(bbox, 1), NumberAt(bbox, 3));
            double by1 = Math.Max(NumberAt(bbox, 1), NumberAt(bbox, 3));

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                double x = (corner & 1) == 0 ? bx0 : bx1;
                double y = (corner & 2) == 0 ? by0 : by1;
                double tx = matrix.A * x + matrix.C * y + matrix.E;
                double ty = matrix.B * x + matrix.D * y + matrix.F;
                if (tx < minX) minX = tx;
                if (tx > maxX) maxX = tx;
                if (ty < minY) minY = ty;
                if (ty > maxY) maxY = ty;
            }

            double rx0 = Math.Min(NumberAt(rect, 0), NumberAt(rect, 2));
            double rx1 = Math.Max(NumberAt(rect, 0), NumberAt(rect, 2));
            double ry0 = Math.Min(NumberAt(rect, 1), NumberAt(rect, 3));
            double ry1 = Math.Max(NumberAt(rect, 1), NumberAt(rect, 3));

            // A degenerate box cannot be scaled onto the rectangle; §12.5.5 leaves that undefined,
            // and a fabricated scale here would be a fabricated resolution.
            if (maxX - minX <= 0.0001 || maxY - minY <= 0.0001) return false;

            double sx = (rx1 - rx0) / (maxX - minX);
            double sy = (ry1 - ry0) / (maxY - minY);
            var a = new Matrix(sx, 0, 0, sy, rx0 - minX * sx, ry0 - minY * sy);

            ctm = Matrix.Concat(matrix, a);
            return true;
        }

        /// <param name="page">The page whose <c>/Contents</c> is read.</param>
        /// <param name="objects">Every object in the document, by object number.</param>
        /// <param name="options">Read for the per-part decode ceiling.</param>
        /// <param name="state">Read for what is left of the walk's operator and tokenizing budgets.</param>
        /// <param name="partUnread">
        /// Set when a part would not decode, or the page is too large to walk. <c>PdfPageContent.Concatenate</c>
        /// skips such a part, which is right for a placement map and wrong for a decision that a name is unused.
        /// </param>
        /// <param name="overTotal">
        /// Set when the parts together pass what the walk would accept (<see cref="ContentScanGraph.PageContent"/>);
        /// nothing of the page is returned.
        /// </param>
        private static byte[] ConcatenatePageContent(PdfDictionary page, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, ScanState state, ref bool partUnread, out bool overTotal)
        {
            long walkable = Math.Min((long)state.Budget * MinBytesPerOperation, state.TokenizeBytesLeft);
            byte[] content = ContentScanGraph.PageContent(page, objects, options, walkable,
                out bool unread, out overTotal);
            if (unread || overTotal) partUnread = true;
            return content;
        }

        // One implementation, in ContentScanGraph. The sibling scanner carried these three
        // token-identical, and both walk the same graph for different reasons.
        private static PdfObject Lookup(PdfDictionary resources, PdfName category, PdfName name,
            Dictionary<int, PdfObject> objects)
            => ContentScanGraph.Lookup(resources, category, name, objects);

        private static int LookupReference(PdfDictionary resources, PdfName category, PdfName name,
            Dictionary<int, PdfObject> objects)
            => ContentScanGraph.LookupReference(resources, category, name, objects);

        private static int ReferenceNumber(PdfObject obj) => ContentScanGraph.ReferenceNumber(obj);

        private static double OperandAt(PdfObject[] operands, int index) =>
            index < operands.Length && operands[index] is PdfNumber number ? number.DoubleValue : 0.0;

        private static double NumberAt(PdfArray array, int index) =>
            array[index] is PdfNumber number ? number.DoubleValue : 0.0;

        private static Matrix MatrixFrom(PdfArray array) => new Matrix(
            NumberAt(array, 0), NumberAt(array, 1), NumberAt(array, 2),
            NumberAt(array, 3), NumberAt(array, 4), NumberAt(array, 5));

        private static PdfObject Deref(PdfObject obj, Dictionary<int, PdfObject> objects)
            => ContentScanGraph.Deref(obj, objects);

        /// <summary>A 2-D affine transform, in the order §8.3.3 writes it: [a b c d e f].</summary>
        private readonly struct Matrix
        {
            internal readonly double A, B, C, D, E, F;

            internal Matrix(double a, double b, double c, double d, double e, double f)
            {
                A = a; B = b; C = c; D = d; E = e; F = f;
            }

            internal static Matrix Identity => new Matrix(1, 0, 0, 1, 0, 0);

            /// <summary>Returns <paramref name="m"/> × <paramref name="n"/>.</summary>
            internal static Matrix Concat(Matrix m, Matrix n) => new Matrix(
                m.A * n.A + m.B * n.C,
                m.A * n.B + m.B * n.D,
                m.C * n.A + m.D * n.C,
                m.C * n.B + m.D * n.D,
                m.E * n.A + m.F * n.C + n.E,
                m.E * n.B + m.F * n.D + n.F);
        }
    }
}
