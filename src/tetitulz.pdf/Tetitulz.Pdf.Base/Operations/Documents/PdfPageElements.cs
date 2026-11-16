// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>What a page element is.</summary>
    public enum PdfPageElementKind
    {
        /// <summary>The text one text-showing operation draws: a run of glyphs on one line.</summary>
        Text = 0,

        /// <summary>A picture: an image XObject drawn, or an inline image.</summary>
        Image = 1,

        /// <summary>A path filled or stroked: a line, a rule of a table, a box, a cell's shading.</summary>
        Path = 2,

        /// <summary>A shading painted over the clip (<c>sh</c>).</summary>
        Shading = 3,

        /// <summary>A form XObject drawn: a group of content the page places as one.</summary>
        Form = 4,
    }

    /// <summary>
    /// One thing a page draws, as <see cref="PdfPageElements.List(Stream, int)"/> reports it: what it
    /// is and the box it paints in.
    /// </summary>
    public sealed class PdfPageElement
    {
        internal PdfPageElement(int pageIndex, int id, PdfPageElementKind kind, double x, double y, double width, double height, int textObject, string text)
        {
            PageIndex = pageIndex;
            Id = id;
            Kind = kind;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            TextObject = textObject;
            Text = text;
        }

        /// <summary>The zero-based page.</summary>
        public int PageIndex { get; }

        /// <summary>
        /// The element's name on its page, which <see cref="PdfPageElements.Remove"/> takes: the place
        /// of its painting operation in the page's content. It names the same element only in the
        /// same bytes — a document that has been rewritten is listed again.
        /// </summary>
        public int Id { get; }

        /// <summary>What the element is.</summary>
        public PdfPageElementKind Kind { get; }

        /// <summary>The left edge of the box the element paints in, in default user space (points, the origin bottom-left).</summary>
        public double X { get; }

        /// <summary>The bottom edge of that box.</summary>
        public double Y { get; }

        /// <summary>The box's width; a hairline's may be 0.</summary>
        public double Width { get; }

        /// <summary>The box's height; a hairline's may be 0.</summary>
        public double Height { get; }

        /// <summary>
        /// For text, which of the page's text objects (<c>BT</c> … <c>ET</c>, counted from 0) shows
        /// it, so the runs of one text object can be taken together; −1 for anything else.
        /// </summary>
        public int TextObject { get; }

        /// <summary>
        /// For text, what the run shows, as its font maps the codes to text, in reading order — the
        /// glyphs are drawn left to right, so a right-to-left run is read back from its right end —
        /// empty when the font maps none of them; null for anything else. What <see cref="PdfTemplateEngine.ReplaceRun"/>
        /// replaces when given the element's id.
        /// </summary>
        public string Text { get; }
    }

    /// <summary>
    /// The things a page draws — its runs of text, pictures, painted paths, shadings and forms —
    /// each with the box it paints in, and the page written again without some of them.
    /// </summary>
    /// <remarks>
    /// <para><b>What an element is.</b> One painting operation of the page's own content: a
    /// text-showing operation (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c>), a path's paint, <c>sh</c>,
    /// an inline image, or a <c>Do</c> — a picture, or a form, which is one element however much it
    /// draws. An operation that paints nothing — invisible text, a path used only to clip, content
    /// on a layer the document's default configuration hides — is not an element: nobody sees it to
    /// choose it. The box is where the raster renderer painted it
    /// (<see cref="PdfPageRenderer.MeasureOperations(PdfPage, PdfDocument, PdfRenderOptions, out IReadOnlyList{ContentStreamOperation})"/>),
    /// its geometry's and never narrowed by a clip; a stroked path's is its own points and half
    /// the line width in force round them.</para>
    /// <para><b>How an element is taken out</b> is <see cref="ContentOperationRemoval"/>'s rule, with
    /// one addition for text. A path's paint becomes <c>n</c>, so a clip set with it still clips; a
    /// picture, a form, a shading is dropped; text whose render mode adds to the clip stays, in mode
    /// 7. Other text is dropped, and <b>the pen is moved as the text moved it</b>
    /// (<see cref="ShownTextAdvance"/>) — by the font's own widths (§9.4.4), whatever face this
    /// host drew a font that is not embedded in: text that follows on the same line without a
    /// position of its own is drawn where it was, in any reader. Everything else in the content — state, colour, transforms, marked
    /// content — is written as it was.</para>
    /// <para><b>Written as one new revision of the whole file</b>, by reachability: the page gets a
    /// content stream of its own, and the streams it had are written only if another page still
    /// draws them, so the removed text is not in the output. What the page's resources name stays —
    /// a removed picture's data is still in the file while the resource dictionary lists it — and so
    /// does anything else that says what the page said: the document's metadata, its bookmarks, its
    /// structure tree. This is an edit, not a redaction; <c>PdfOperations.Redact</c> destroys.</para>
    /// <para><b>A signature does not survive it</b>: the file is written anew. An encrypted
    /// document is refused, as every whole-file writer here refuses it.</para>
    /// </remarks>
    public static class PdfPageElements
    {
        /// <summary>The elements of page <paramref name="pageIndex"/>, in the order the page draws them.</summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <exception cref="ArgumentOutOfRangeException">The document has no such page.</exception>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static IReadOnlyList<PdfPageElement> List(Stream input, int pageIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the elements of a page", "PdfOperations.ListPageElements(Stream, int, string)");
            return Measure(doc, PageOf(doc, pageIndex), pageIndex, null, null, out _, withText: true);
        }

        /// <summary>
        /// Writes <paramref name="input"/> to <paramref name="output"/> with the elements
        /// <paramref name="elementIds"/> name taken out of page <paramref name="pageIndex"/>; how many
        /// were taken out. No ids copies the input through.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Receives the document, written anew. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="elementIds">The <see cref="PdfPageElement.Id"/> of each element to take out, from a listing of these same bytes.</param>
        /// <exception cref="ArgumentOutOfRangeException">The document has no such page.</exception>
        /// <exception cref="ArgumentException">An id names no element of the page.</exception>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        /// <exception cref="PdfException">The document has no catalog to write again from.</exception>
        public static int Remove(Stream input, Stream output, int pageIndex, IReadOnlyCollection<int> elementIds)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (elementIds == null) throw new ArgumentNullException(nameof(elementIds));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "remove elements from a page of", "the page's content");
            PdfPage page = PageOf(doc, pageIndex);
            if (elementIds.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return 0;
            }

            var advances = new Dictionary<int, ShownTextAdvance>();
            List<PdfPageElement> elements = Measure(doc, page, pageIndex, advances, null, out IReadOnlyList<ContentStreamOperation> ops);
            Dictionary<int, PdfPageElementKind> kinds = KindsOf(elements);
            HashSet<int> remove = Named(elementIds, kinds, pageIndex);

            byte[] content;
            using (var written = new MemoryStream())
            {
                var w = new PdfWriter(written);
                var text = new TextPaintTracker();
                for (int i = 0; i < ops.Count; i++)
                {
                    ContentStreamOperation op = ops[i];
                    text.Observe(op.Operator, op.Operands);
                    if (!remove.Contains(i))
                    {
                        ContentOperationWriter.Write(w, op);
                        continue;
                    }
                    switch (kinds[i])
                    {
                        case PdfPageElementKind.Text:
                            WriteTextRemoved(w, op, text, advances.TryGetValue(i, out ShownTextAdvance advance) ? advance : default);
                            break;
                        case PdfPageElementKind.Path:
                            ContentOperationRemoval.WritePathUnpainted(w);
                            break;
                        // A picture, a form, a shading: dropped.
                    }
                }
                content = written.ToArray();
            }
            WriteWithContent(doc, page, content, output);
            return remove.Count;
        }

        /// <summary>
        /// Writes <paramref name="input"/> to <paramref name="output"/> with the elements
        /// <paramref name="elementIds"/> name moved or scaled on page <paramref name="pageIndex"/> by
        /// <paramref name="matrix"/>; how many were. No ids copies the input through.
        /// </summary>
        /// <remarks>
        /// <para>The matrix is in the page's default user space, as a <c>cm</c> takes it
        /// (<c>a b c d e f</c>): each element is drawn where the matrix takes it from where it was,
        /// whatever transform the page drew it under. A picture, a form or an inline image is drawn
        /// inside a <c>q … cm … Q</c> of its own; a path is, whole, from its first point to its paint;
        /// a run of text is shown under a text matrix of its own, and the line matrix and the pen it
        /// left are put back after it, so the text that follows is drawn where it was.</para>
        /// <para>Refused, with the element named: a shading painted over the clip, which has no
        /// place of its own; a path that also sets the clip, and text that adds to it, whose clip
        /// what follows is drawn through; a picture drawn inside a text object.</para>
        /// <para>Written as <see cref="Remove"/> writes: one new revision of the whole file.</para>
        /// </remarks>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Receives the document, written anew. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="elementIds">The <see cref="PdfPageElement.Id"/> of each element to move, from a listing of these same bytes.</param>
        /// <param name="matrix">Six finite numbers, <c>a b c d e f</c>, of a matrix that has an inverse.</param>
        /// <exception cref="ArgumentOutOfRangeException">The document has no such page, or a number of the matrix is not finite.</exception>
        /// <exception cref="ArgumentException">An id names no element of the page; the matrix is not six numbers or has no inverse.</exception>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        /// <exception cref="PdfException">An element cannot be moved by itself, or the document has no catalog to write again from.</exception>
        public static int Transform(Stream input, Stream output, int pageIndex, IReadOnlyCollection<int> elementIds, double[] matrix)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (elementIds == null) throw new ArgumentNullException(nameof(elementIds));
            if (matrix == null) throw new ArgumentNullException(nameof(matrix));
            if (matrix.Length != 6) throw new ArgumentException("A matrix is six numbers: a b c d e f.", nameof(matrix));
            foreach (double value in matrix) Core.Util.NumericArguments.RequireFinite(value, nameof(matrix));
            var m = new float[6];
            for (int k = 0; k < 6; k++)
            {
                m[k] = (float)matrix[k];
                // A finite number past a float's range becomes an infinity here, and an infinity is
                // written into the file as 0: the elements would vanish and the call report success.
                if (float.IsInfinity(m[k]))
                    throw new ArgumentOutOfRangeException(nameof(matrix), "A number of the matrix is past the range a page's coordinates have.");
            }
            float[] inverse = PdfMatrix.Invert(m);
            if (inverse == null || !AllFinite(inverse))
                throw new ArgumentException("The matrix has no inverse: it would flatten what it moves.", nameof(matrix));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "move elements on a page of", "the page's content");
            PdfPage page = PageOf(doc, pageIndex);
            if (elementIds.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return 0;
            }

            var placements = new Dictionary<int, OperationPlacement>();
            List<PdfPageElement> elements = Measure(doc, page, pageIndex, null, placements, out IReadOnlyList<ContentStreamOperation> ops);
            Dictionary<int, PdfPageElementKind> kinds = KindsOf(elements);
            HashSet<int> move = Named(elementIds, kinds, pageIndex);

            // What is written before and after each operation, and in place of a text run.
            var before = new Dictionary<int, string>();
            var after = new Dictionary<int, string>();
            var inPlace = new Dictionary<int, Action<PdfWriter>>();
            var textState = new TextPaintTracker();
            Dictionary<int, PathSpan> paths = PathSpans(ops);
            int textDepth = 0;
            for (int i = 0; i < ops.Count; i++)
            {
                ContentStreamOperation op = ops[i];
                textState.Observe(op.Operator, op.Operands);
                if (op.Operator == PdfOperators.BeginText) textDepth++;
                else if (op.Operator == PdfOperators.EndText && textDepth > 0) textDepth--;
                if (!move.Contains(i)) continue;

                string what = "Element " + i.ToString(CultureInfo.InvariantCulture) + " of page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture);
                if (!placements.TryGetValue(i, out OperationPlacement placement) || PdfMatrix.Invert(placement.Ctm) is not float[] back)
                    throw new PdfException(what + " is drawn under a matrix that cannot be undone, so it cannot be moved.");
                // The caller's matrix, as it acts in the operation's own space.
                float[] own = PdfMatrix.Compose(PdfMatrix.Compose(placement.Ctm, m), back);
                if (!AllFinite(own))
                    throw new PdfException(what + " is drawn under a matrix so large or so small that the move cannot be written, so it cannot be moved.");

                switch (kinds[i])
                {
                    case PdfPageElementKind.Shading:
                        throw new PdfException(what + " is a shading painted over the clip: it has no place of its own to move.");

                    case PdfPageElementKind.Image:
                    case PdfPageElementKind.Form:
                        if (textDepth > 0)
                            throw new PdfException(what + " is drawn inside a text object, where it cannot be given a matrix of its own.");
                        before[i] = PdfOperators.Save + " " + Numbers(own) + " " + PdfOperators.ConcatMatrix + "\n";
                        after[i] = PdfOperators.Restore + "\n";
                        break;

                    case PdfPageElementKind.Path:
                        PathSpan path = paths[i];
                        if (path.SetsClip)
                            throw new PdfException(what + " is a path that also sets the clip, through which what follows it is drawn: it cannot be moved by itself.");
                        // A matrix of its own goes before the path's first point; with another
                        // operator among the points there is no place before it that is not inside the path.
                        if (path.Interrupted)
                            throw new PdfException(what + " is a path with other operators between its points and its paint: it cannot be given a matrix of its own.");
                        before[path.Start] = PdfOperators.Save + " " + Numbers(own) + " " + PdfOperators.ConcatMatrix + "\n";
                        after[i] = PdfOperators.Restore + "\n";
                        break;

                    case PdfPageElementKind.Text:
                        if (textState.RenderMode.AddsToClip())
                            throw new PdfException(what + " is text that adds to the clip, through which what follows it is drawn: it cannot be moved by itself.");
                        OperationPlacement text = placement;
                        ContentStreamOperation shown = op;
                        inPlace[i] = w => WriteTextMoved(w, shown, text, own);
                        break;
                }
            }

            byte[] content;
            using (var written = new MemoryStream())
            {
                var w = new PdfWriter(written);
                for (int i = 0; i < ops.Count; i++)
                {
                    if (before.TryGetValue(i, out string opening)) w.WriteRaw(opening);
                    if (inPlace.TryGetValue(i, out Action<PdfWriter> replacement)) replacement(w);
                    else ContentOperationWriter.Write(w, ops[i]);
                    if (after.TryGetValue(i, out string closing)) w.WriteRaw(closing);
                }
                content = written.ToArray();
            }
            WriteWithContent(doc, page, content, output);
            return move.Count;
        }

        /// <summary>A painted path's operations: where its first point is, whether it also sets the clip, and whether anything else stands among them.</summary>
        private readonly struct PathSpan
        {
            internal PathSpan(int start, bool setsClip, bool interrupted)
            {
                Start = start;
                SetsClip = setsClip;
                Interrupted = interrupted;
            }

            /// <summary>The index of the path's first construction operator; the paint's own when it has none.</summary>
            internal int Start { get; }

            /// <summary>Whether a <c>W</c> or <c>W*</c> stands in the path.</summary>
            internal bool SetsClip { get; }

            /// <summary>
            /// Whether an operator that neither builds the path nor clips stands between its first
            /// point and its paint — a line width, a graphics state, marked content: content that
            /// readers draw, though a path object holds no such operator (§8.2).
            /// </summary>
            internal bool Interrupted { get; }
        }

        private static bool BuildsPath(string op)
        {
            switch (op)
            {
                case PdfOperators.MoveTo:
                case PdfOperators.LineTo:
                case PdfOperators.CurveTo:
                case PdfOperators.CurveToV:
                case PdfOperators.CurveToY:
                case PdfOperators.ClosePath:
                case PdfOperators.Rectangle:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Each path-painting operation's <see cref="PathSpan"/>, by the paint's index, from one pass
        /// over the content: a path is open from its first construction operator to its paint or its
        /// <c>n</c>, whatever stands between.
        /// </summary>
        private static Dictionary<int, PathSpan> PathSpans(IReadOnlyList<ContentStreamOperation> ops)
        {
            var spans = new Dictionary<int, PathSpan>();
            int open = -1;
            bool clip = false, interrupted = false;
            for (int i = 0; i < ops.Count; i++)
            {
                string op = ops[i].Operator;
                if (BuildsPath(op))
                {
                    if (open < 0) { open = i; clip = false; interrupted = false; }
                }
                else if (op == PdfOperators.Clip || op == PdfOperators.ClipEvenOdd)
                {
                    if (open >= 0) clip = true;
                }
                else if (ContentOperationRemoval.PaintsPath(op) || op == PdfOperators.EndPath)
                {
                    if (op != PdfOperators.EndPath) spans[i] = new PathSpan(open < 0 ? i : open, clip, interrupted);
                    open = -1;
                }
                else if (open >= 0)
                {
                    interrupted = true;
                }
            }
            return spans;
        }

        private static bool AllFinite(float[] matrix)
        {
            foreach (float value in matrix)
                if (!MatrixGuard.IsFinite(value)) return false;
            return true;
        }

        /// <summary>
        /// Writes the text-showing operation <paramref name="op"/> moved by <paramref name="own"/>,
        /// the caller's matrix in the operation's own space. The line move and the spacing a
        /// <c>'</c> or a <c>"</c> makes are written first, as they are; then the run under the text
        /// matrix it started from, moved; then the line matrix and the pen the original left
        /// (<see cref="ShownTextAdvance"/>), so what follows is drawn where it was.
        /// </summary>
        private static void WriteTextMoved(PdfWriter w, ContentStreamOperation op, OperationPlacement placement, float[] own)
        {
            PdfObject shown = op.Operands.Length > 0 ? op.Operands[op.Operands.Length - 1] : null;
            if (op.Operator == PdfOperators.NextLineSetSpacingShowText && op.Operands.Length >= 2)
            {
                ContentOperationWriter.Write(w, new ContentStreamOperation(PdfOperators.SetWordSpacing, new[] { op.Operands[0] }));
                ContentOperationWriter.Write(w, new ContentStreamOperation(PdfOperators.SetCharSpacing, new[] { op.Operands[1] }));
            }
            if (op.Operator == PdfOperators.NextLineShowText || op.Operator == PdfOperators.NextLineSetSpacingShowText)
                w.WriteRaw(PdfOperators.NextLine + "\n");

            // The run starts where the file's own widths put the pen (§9.4.4), which is short of the
            // renderer's pen by what the substituted faces of the runs before it measured beyond them.
            float[] start = placement.BeyondWidthsAtStart == 0f
                ? placement.PenStart
                : PdfMatrix.Compose(new[] { 1f, 0f, 0f, 1f, -placement.BeyondWidthsAtStart, 0f }, placement.PenStart);
            w.WriteRaw(Numbers(PdfMatrix.Compose(start, own)) + " " + PdfOperators.SetTextMatrix + "\n");
            if (shown != null)
                ContentOperationWriter.Write(w, new ContentStreamOperation(
                    op.Operator == PdfOperators.ShowTextArray ? PdfOperators.ShowTextArray : PdfOperators.ShowText, new[] { shown }));

            w.WriteRaw(Numbers(placement.LineAfter) + " " + PdfOperators.SetTextMatrix + "\n");
            ShownTextAdvance toPen = ShownTextAdvance.Between(placement.LineAfter, placement.PenAfter, placement.FontSize, placement.HorizontalScaling)
                .Less(placement.BeyondWidthsAfter);
            if (toPen.AsShowArrayNumber() is double number)
                w.WriteRaw("[" + PdfRealFormat.Format(number, "0.#####") + "] " + PdfOperators.ShowTextArray + "\n");
        }

        /// <summary>A matrix's six numbers as content writes them.</summary>
        private static string Numbers(float[] matrix)
        {
            var parts = new string[6];
            for (int k = 0; k < 6; k++) parts[k] = PdfRealFormat.Format(matrix[k], "0.######");
            return string.Join(" ", parts);
        }

        private static Dictionary<int, PdfPageElementKind> KindsOf(List<PdfPageElement> elements)
        {
            var kinds = new Dictionary<int, PdfPageElementKind>();
            foreach (PdfPageElement element in elements) kinds[element.Id] = element.Kind;
            return kinds;
        }

        /// <summary>The ids as a set, each checked to name an element of the page.</summary>
        private static HashSet<int> Named(IReadOnlyCollection<int> elementIds, Dictionary<int, PdfPageElementKind> kinds, int pageIndex)
        {
            var named = new HashSet<int>();
            foreach (int id in elementIds)
            {
                if (!kinds.ContainsKey(id))
                    throw new ArgumentException(
                        "Page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture) + " has no element " +
                        id.ToString(CultureInfo.InvariantCulture) + ". Ids come from a listing of the same bytes.", nameof(elementIds));
                named.Add(id);
            }
            return named;
        }

        /// <summary>
        /// Gives the page <paramref name="content"/> as a stream of its own — under a number the
        /// document does not use: the streams the page had may be other pages' too, and stay theirs —
        /// and writes the document anew by reachability.
        /// </summary>
        private static void WriteWithContent(PdfDocument doc, PdfPage page, byte[] content, Stream output)
        {
            byte[] encoded = FlateEncodeFilter.Encode(content);
            var stream = new PdfStream();
            stream.Set(PdfName.Filter, PdfName.FlateDecode);
            stream.Set(PdfName.Length, new PdfNumber(encoded.Length));
            stream.RawData = encoded;
            int number = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            page.Dictionary.Set(PdfName.Contents, new PdfReference(number, 0));

            if (!PdfFullRewriter.TryFullRewrite(doc, output, new Dictionary<int, PdfObject> { [number] = stream }))
                throw new PdfException("The document has no resolvable catalog, so it cannot be written again with the page's new content.");
        }

        private static PdfPage PageOf(PdfDocument doc, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    "The document has " + doc.Pages.Count.ToString(CultureInfo.InvariantCulture) + " page(s); the index is zero-based.");
            return doc.Pages[pageIndex];
        }

        /// <summary>
        /// The page's elements and the operations their ids index, from one measuring render; into
        /// <paramref name="advances"/>, when given, how far each text-showing operation moved the pen,
        /// and into <paramref name="placements"/> where each painting operation stood. With
        /// <paramref name="withText"/>, each run of text carries what it shows — a second walk of
        /// the content, through the fonts, which a listing makes and a rewrite has no use for.
        /// </summary>
        private static List<PdfPageElement> Measure(PdfDocument doc, PdfPage page, int pageIndex,
            Dictionary<int, ShownTextAdvance> advances, Dictionary<int, OperationPlacement> placements,
            out IReadOnlyList<ContentStreamOperation> ops, bool withText = false)
        {
            placements ??= new Dictionary<int, OperationPlacement>();
            // What the document's default configuration hides is not listed: nobody sees it to select it.
            Dictionary<int, SKRect> bounds = PdfPageRenderer.MeasureOperations(page, doc, new PdfRenderOptions(), out ops, advances, placements,
                hideOptionalContent: true);
            Dictionary<int, string> texts = withText ? PdfTemplateEngine.RunTexts(doc, page) : null;
            Dictionary<int, PathSpan> paths = PathSpans(ops);
            var xobjects = doc.ResolveObject(page.Resources?[PdfName.XObject]) as PdfDictionary;
            var elements = new List<PdfPageElement>();
            var text = new TextPaintTracker();
            int textObject = -1;

            for (int i = 0; i < ops.Count; i++)
            {
                ContentStreamOperation op = ops[i];
                bool shows = text.Observe(op.Operator, op.Operands);
                if (op.Operator == PdfOperators.BeginText) textObject++;

                PdfPageElementKind kind;
                if (shows) kind = PdfPageElementKind.Text;
                else if (ContentOperationRemoval.PaintsPath(op.Operator)) kind = PdfPageElementKind.Path;
                else if (op.Operator == PdfOperators.ShadingFill) kind = PdfPageElementKind.Shading;
                else if (op.Operator == PdfOperators.BeginInlineImage) kind = PdfPageElementKind.Image;
                else if (op.Operator == PdfOperators.InvokeXObject) kind = IsImage(doc, xobjects, op) ? PdfPageElementKind.Image : PdfPageElementKind.Form;
                else continue;

                // No bound: the operation painted nothing, and nothing is there to select.
                if (!bounds.TryGetValue(i, out SKRect r)) continue;
                double left = Math.Min(r.Left, r.Right), bottom = Math.Min(r.Top, r.Bottom);
                double right = Math.Max(r.Left, r.Right), top = Math.Max(r.Top, r.Bottom);
                if (kind == PdfPageElementKind.Path && Strokes(op.Operator) && placements.TryGetValue(i, out OperationPlacement placement))
                    TightenStroke(ops, paths[i], i, placement.Ctm, placement.LineWidth, ref left, ref bottom, ref right, ref top);
                string shownText = null;
                if (kind == PdfPageElementKind.Text && texts != null)
                    shownText = texts.TryGetValue(i, out string shown) ? shown : "";
                elements.Add(new PdfPageElement(pageIndex, i, kind, left, bottom, right - left, top - bottom,
                    kind == PdfPageElementKind.Text ? Math.Max(textObject, 0) : -1, shownText));
            }
            return elements;
        }

        /// <summary>Whether a path-painting operator strokes (§8.5.3.1, Table 59).</summary>
        private static bool Strokes(string op)
            => op == PdfOperators.Stroke || op == PdfOperators.CloseStroke
               || op == PdfOperators.FillStroke || op == PdfOperators.FillStrokeEvenOdd
               || op == PdfOperators.CloseFillStroke || op == PdfOperators.CloseFillStrokeEvenOdd;

        /// <summary>
        /// Brings a stroked path's box in from the renderer's bound, which leaves a miter join's
        /// room round every path, to the path's own points and half its line width round them —
        /// where a line, a rule of a table, is seen to be. The points are the construction
        /// operators' own, control points included, through <paramref name="ctm"/>; the half width
        /// is the line width's in force at the paint, as far as <paramref name="ctm"/> stretches
        /// it, and never more than the bound's own room. A path whose points the bound does not
        /// hold, or that has none, keeps the bound.
        /// </summary>
        private static void TightenStroke(IReadOnlyList<ContentStreamOperation> ops, PathSpan path, int paint, float[] ctm, float lineWidth,
            ref double left, ref double bottom, ref double right, ref double top)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Point(double x, double y)
            {
                (float px, float py) = PdfMatrix.Transform(ctm, (float)x, (float)y);
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
            }

            for (int j = path.Start; j < paint; j++)
            {
                // What stands among a path's points without building it has no points to add.
                if (!BuildsPath(ops[j].Operator)) continue;
                PdfObject[] o = ops[j].Operands;
                var n = new double[o.Length];
                for (int k = 0; k < o.Length; k++)
                {
                    if (!(o[k] is PdfNumber number)) return;
                    n[k] = number.DoubleValue;
                }
                if (ops[j].Operator == PdfOperators.Rectangle && n.Length >= 4)
                {
                    Point(n[0], n[1]);
                    Point(n[0] + n[2], n[1] + n[3]);
                    Point(n[0], n[1] + n[3]);
                    Point(n[0] + n[2], n[1]);
                }
                else
                {
                    for (int k = 0; k + 1 < n.Length; k += 2) Point(n[k], n[k + 1]);
                }
            }
            if (minX > maxX) return;

            double room = Math.Min(Math.Min(minX - left, minY - bottom), Math.Min(right - maxX, top - maxY));
            // NaN fails the comparison too: a point past a float's range keeps the bound.
            if (!(room >= 0)) return;
            double stretch = Math.Max(Math.Sqrt((double)ctm[0] * ctm[0] + (double)ctm[1] * ctm[1]),
                                      Math.Sqrt((double)ctm[2] * ctm[2] + (double)ctm[3] * ctm[3]));
            double halfWidth = Math.Abs(lineWidth) / 2.0 * stretch;
            if (!(halfWidth <= room)) halfWidth = room;
            left = minX - halfWidth;
            bottom = minY - halfWidth;
            right = maxX + halfWidth;
            top = maxY + halfWidth;
        }

        /// <summary>Whether the XObject <paramref name="op"/> draws is an image, by its <c>/Subtype</c>.</summary>
        private static bool IsImage(PdfDocument doc, PdfDictionary xobjects, ContentStreamOperation op)
            => op.Operands.Length > 0 && op.Operands[0] is PdfName name
               && doc.ResolveObject(xobjects?[name]) is PdfStream xobject
               && PdfResolve.Name(doc, xobject, PdfName.Subtype) == PdfName.Image;

        /// <summary>
        /// Writes the text-showing operation <paramref name="op"/> taken out. Text that adds to the
        /// clip stays, in mode 7 (<see cref="ContentOperationRemoval.WriteTextUnpainted"/>). Other
        /// text goes, and what it did besides painting is written in its place: the move to the next
        /// line a <c>'</c> or a <c>"</c> makes, the spacing a <c>"</c> sets, and the pen's move.
        /// </summary>
        private static void WriteTextRemoved(PdfWriter w, ContentStreamOperation op, TextPaintTracker text, ShownTextAdvance advance)
        {
            if (text.RenderMode.AddsToClip())
            {
                ContentOperationRemoval.WriteTextUnpainted(w, op, text);
                return;
            }

            if (op.Operator == PdfOperators.NextLineSetSpacingShowText && op.Operands.Length >= 2)
            {
                ContentOperationWriter.Write(w, new ContentStreamOperation(PdfOperators.SetWordSpacing, new[] { op.Operands[0] }));
                ContentOperationWriter.Write(w, new ContentStreamOperation(PdfOperators.SetCharSpacing, new[] { op.Operands[1] }));
            }
            if (op.Operator == PdfOperators.NextLineShowText || op.Operator == PdfOperators.NextLineSetSpacingShowText)
                w.WriteRaw(PdfOperators.NextLine + "\n");

            if (advance.AsShowArrayNumber() is double number)
                w.WriteRaw("[" + PdfRealFormat.Format(number, "0.#####") + "] " + PdfOperators.ShowTextArray + "\n");
        }
    }
}
