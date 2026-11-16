// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Adds annotations (text notes, highlights, underlines, rectangles, links)
    /// to PDF pages via incremental update.
    /// </summary>
    public static class PdfAnnotationWriter
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Adds a sticky-note (text) annotation.</summary>
        public static void AddTextNote(
            Stream input, Stream output,
            int pageIndex, double x, double y,
            string contents,
            string author = null)
        {
            RequireFinite(nameof(x), x, nameof(y), y);
            var rect = new PdfAnnotRect(x, y, x + 20, y + 20);
            var annot = BuildAnnotation(PdfAnnotationSubtypes.Text, rect, contents, author);
            WriteAnnotation(input, output, pageIndex, annot);
        }

        /// <summary>Adds a highlight annotation over the given quad-point rectangle.</summary>
        public static void AddHighlight(
            Stream input, Stream output,
            int pageIndex,
            double x1, double y1, double x2, double y2,
            string contents = null,
            float r = 1f, float g = 1f, float b = 0f)
        {
            RequireFiniteCorners(x1, y1, x2, y2);
            RequireFiniteColour(r, g, b);
            var rect = NormalisedRect(x1, y1, x2, y2);

            var annot = BuildAnnotation(PdfAnnotationSubtypes.Highlight, rect, contents, null);

            annot.Set(PdfName.QuadPoints, QuadPointsFor(x1, y1, x2, y2));

            SetColor(annot, PdfName.C, r, g, b);
            WriteAnnotation(input, output, pageIndex, annot);
        }

        /// <summary>Adds an underline annotation.</summary>
        public static void AddUnderline(
            Stream input, Stream output,
            int pageIndex,
            double x1, double y1, double x2, double y2,
            string contents = null,
            float r = 0f, float g = 0f, float b = 1f)
        {
            RequireFiniteCorners(x1, y1, x2, y2);
            RequireFiniteColour(r, g, b);
            var rect = NormalisedRect(x1, y1, x2, y2);

            var annot = BuildAnnotation(PdfAnnotationSubtypes.Underline, rect, contents, null);
            annot.Set(PdfName.QuadPoints, QuadPointsFor(x1, y1, x2, y2));
            SetColor(annot, PdfName.C, r, g, b);
            WriteAnnotation(input, output, pageIndex, annot);
        }

        /// <summary>Adds a strikethrough annotation (StrikeOut subtype).</summary>
        public static void AddStrikeOut(
            Stream input, Stream output,
            int pageIndex,
            double x1, double y1, double x2, double y2,
            string contents = null,
            float r = 1f, float g = 0f, float b = 0f)
        {
            RequireFiniteCorners(x1, y1, x2, y2);
            RequireFiniteColour(r, g, b);
            var rect = NormalisedRect(x1, y1, x2, y2);

            var annot = BuildAnnotation(PdfAnnotationSubtypes.StrikeOut, rect, contents, null);
            annot.Set(PdfName.QuadPoints, QuadPointsFor(x1, y1, x2, y2));
            SetColor(annot, PdfName.C, r, g, b);
            WriteAnnotation(input, output, pageIndex, annot);
        }

        /// <summary>Adds a free-text annotation (directly rendered text on the page).</summary>
        public static void AddFreeText(
            Stream input, Stream output,
            int pageIndex,
            double x, double y, double width, double height,
            string text,
            string fontName = StandardFonts.Helvetica, float fontSize = 12,
            float r = 0f, float g = 0f, float b = 0f)
        {
            RequireFinite(nameof(x), x, nameof(y), y);
            RequireFinite(nameof(width), width, nameof(height), height);
            NumericArguments.RequireFinite(fontSize, nameof(fontSize));
            RequireFiniteColour(r, g, b);
            var rect = new PdfAnnotRect(x, y, x + width, y + height);
            var annot = BuildAnnotation(PdfAnnotationSubtypes.FreeText, rect, text, null);
            // /DA (default appearance) string for free-text annotations. The numbers go through the
            // invariant PDF formatter and the font through the name escape: this was an interpolated
            // string, so on a host whose culture writes a decimal comma a 10.5 pt size came out as
            // "10,5 Tf", which no reader parses, and a font name with a space ended the name early.
            annot.Set(PdfName.DA, new PdfString(
                "/" + Core.Objects.PdfNameEscape.Escape(fontName) + " " + Fmt(fontSize) + " Tf " +
                Fmt(r) + " " + Fmt(g) + " " + Fmt(b) + " rg"));
            WriteAnnotation(input, output, pageIndex, annot);
        }

        /// <summary>Adds a stamp annotation (e.g., "Approved", "Draft", "Confidential").</summary>
        public static void AddStamp(
            Stream input, Stream output,
            int pageIndex,
            double x, double y, double width, double height,
            string stampName = "Approved")
        {
            RequireFinite(nameof(x), x, nameof(y), y);
            RequireFinite(nameof(width), width, nameof(height), height);
            var rect = new PdfAnnotRect(x, y, x + width, y + height);
            var annot = BuildAnnotation(PdfAnnotationSubtypes.Stamp, rect, stampName, null);
            annot.Set(PdfName.Name, new PdfName(stampName));
            WriteAnnotation(input, output, pageIndex, annot);
        }

        /// <summary>Adds a rectangular border annotation (square/rectangle subtype).</summary>
        public static void AddRectangle(
            Stream input, Stream output,
            int pageIndex,
            double x1, double y1, double x2, double y2,
            string contents = null,
            float r = 1f, float g = 0f, float b = 0f,
            float lineWidth = 1f)
        {
            RequireFiniteCorners(x1, y1, x2, y2);
            RequireFiniteColour(r, g, b);
            NumericArguments.RequireNonNegative(lineWidth, nameof(lineWidth));
            var rect = NormalisedRect(x1, y1, x2, y2);

            var annot = BuildAnnotation(PdfAnnotationSubtypes.Square, rect, contents, null);
            SetColor(annot, PdfName.C, r, g, b);

            // /BS (border style dictionary)
            var bs = new PdfDictionary();
            bs.Set(PdfName.W, new PdfNumber(lineWidth));
            bs.Set(PdfName.S, PdfName.S); // solid
            annot.Set(PdfName.BS, bs);

            WriteAnnotation(input, output, pageIndex, annot);
        }

        /// <summary>Adds a URI link annotation.</summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="x1">One corner's x, in points.</param>
        /// <param name="y1">One corner's y.</param>
        /// <param name="x2">The opposite corner's x.</param>
        /// <param name="y2">The opposite corner's y.</param>
        /// <param name="uri">The target URI.</param>
        /// <remarks>
        /// One element through <see cref="AddUriLinks"/>, so a single link and a batch of them cannot
        /// come out differently.
        /// </remarks>
        public static void AddUriLink(
            Stream input, Stream output,
            int pageIndex,
            double x1, double y1, double x2, double y2,
            string uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            RequireFiniteCorners(x1, y1, x2, y2);
            AddUriLinks(input, output, new[]
            {
                new UriLinkRequest { PageIndex = pageIndex, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Uri = uri },
            });
        }

        /// <summary>
        /// Adds many URI link annotations as <b>one</b> incremental revision.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="links">
        /// The links to add. When none is written, an empty list included, the input is copied to
        /// <paramref name="output"/> unchanged rather than given an empty revision.
        /// </param>
        /// <returns>How many links were written.</returns>
        /// <remarks>
        /// <para><b>Batching is required here, not an optimisation.</b> The single-link path opens
        /// the document, allocates from <c>GetMaxObjectNumber()</c> and appends a complete revision;
        /// a caller looping over N URLs would therefore re-parse a growing file N times and leave N
        /// stacked revisions in the output. Autolinking a page of references is exactly that loop.</para>
        ///
        /// <para>Links are grouped by page so each page's <c>/Annots</c> is rebuilt once, carrying
        /// every annotation it already had.</para>
        /// </remarks>
        public static int AddUriLinks(Stream input, Stream output, IReadOnlyList<UriLinkRequest> links)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (links == null) throw new ArgumentNullException(nameof(links));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var objects = new List<IndirectObject>();
            var perPage = new Dictionary<int, List<int>>();   // page index -> new annotation object numbers
            int written = 0;

            for (int li = 0; li < links.Count; li++)
            {
                var link = links[li];
                if (link == null) continue;
                if (link.Uri == null) throw new ArgumentException("A link's Uri is null.", nameof(links));
                // A NaN corner went through NormalisedRect's Math.Min/Max as NaN and was written as
                // a /Rect of zeros: a link annotation nothing can click, counted as written.
                NumericArguments.RequireFinite(link.X1, nameof(links), $"[{li}].X1");
                NumericArguments.RequireFinite(link.Y1, nameof(links), $"[{li}].Y1");
                NumericArguments.RequireFinite(link.X2, nameof(links), $"[{li}].X2");
                NumericArguments.RequireFinite(link.Y2, nameof(links), $"[{li}].Y2");
                PdfHelpers.ValidatePageIndex(doc, link.PageIndex);

                var page = doc.Pages[link.PageIndex];
                int pageObjNum = page.Dictionary.ObjectNumber;
                if (pageObjNum <= 0)
                    throw new Core.PdfException(
                        "Page " + link.PageIndex + " has no object number; cannot update via " +
                        "incremental write.");

                var rect = NormalisedRect(link.X1, link.Y1, link.X2, link.Y2);

                var annot = BuildAnnotation(PdfAnnotationSubtypes.Link, rect, null, null);

                var action = new PdfDictionary();
                action.Set(PdfName.S, PdfName.URI);
                action.Set(PdfName.URI, new PdfString(link.Uri));
                annot.Set(PdfName.A, action);

                // Invisible border — a link that draws a box round every URL is not what a caller
                // autolinking a page of references wants, and §12.5.6.5 leaves the choice open.
                var bs = new PdfDictionary();
                bs.Set(PdfName.W, new PdfNumber(0));
                annot.Set(PdfName.BS, bs);

                int annotObjNum = nextObjNum++;
                annot.ObjectNumber = annotObjNum;
                annot.GenerationNumber = 0;
                annot.Set(PdfName.P, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, pageObjNum));

                AttachAppearance(annot, ref nextObjNum, objects);
                objects.Add(new IndirectObject(annotObjNum, 0, annot));

                if (!perPage.TryGetValue(link.PageIndex, out var list))
                    perPage[link.PageIndex] = list = new List<int>();
                list.Add(annotObjNum);
                written++;
            }

            // Nothing to write copies the input through, as WriteAnnotations does. This appended an
            // empty revision, which changes nothing yet still moves every offset a signature covers
            // — and PdfAutoLinker.AddLinks reaches here with an empty list for every document whose
            // text holds no URL.
            if (written == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return 0;
            }

            foreach (var entry in perPage)
            {
                var page = doc.Pages[entry.Key];
                StagePageWithAnnots(page, BuildAnnotsArray(page, doc, entry.Value), objects);
            }

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
            return written;
        }

        /// <summary>
        /// Writes many annotations of any subtype in <b>one</b> appended revision, and reports how
        /// many landed.
        /// </summary>
        /// <param name="input">Source PDF stream. Not modified.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="annotations">
        /// What to write. An entry naming a page the document does not have is skipped and counted
        /// in <see cref="LastAnnotationsSkipped"/> rather than throwing — an imported review file
        /// routinely refers to pages a caller has since removed, and losing nineteen good comments
        /// to one stale page number is the wrong trade.
        /// </param>
        /// <returns>How many annotations were written.</returns>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        /// <remarks>
        /// <para><b>One revision, not one per annotation.</b> Every other writer here appends a
        /// revision per call, which for an imported review of two hundred comments means two
        /// hundred revisions, a file several times its original size, and two hundred chances for a
        /// reader to disagree about which is current. The per-page <c>/Annots</c> arrays are
        /// rewritten once each, after every annotation object is staged.</para>
        ///
        /// <para><b>An appearance stream is generated where <c>AnnotationAppearanceBuilder</c> has
        /// one to draw</b> (Square, Circle, Highlight, Underline, StrikeOut, FreeText, Stamp, Ink,
        /// PolyLine, Polygon, Line — the geometry the renderers draw for an annotation without an
        /// <c>/AP</c>), through <see cref="AttachAppearance"/>. §12.5.5 makes <c>/AP</c> optional, so for
        /// a subtype with no builder here — <c>FileAttachment</c> — or a path annotation asking for what
        /// the fallback does not draw (line endings, a cloudy border, an intent, leader lines, a caption),
        /// the annotation is written without one and
        /// <see cref="LastAppearanceSkipped"/> says so: <b>generating a wrong appearance is worse
        /// than generating none</b>, since an <c>/AP</c> is authoritative and a viewer that would
        /// have drawn the ink correctly draws our guess instead. Text and Link have none by design.</para>
        ///
        /// <para><b>Existing annotations are kept.</b> The page's current <c>/Annots</c> is read and
        /// the new entries appended, so importing a review into a document that already has
        /// comments adds to them.</para>
        /// </remarks>
        public static int WriteAnnotations(
            Stream input, Stream output, IReadOnlyList<FormDataAnnotation> annotations)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (annotations == null) throw new ArgumentNullException(nameof(annotations));

            _lastAnnotationsSkipped = null;

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var objects = new List<IndirectObject>();
            var perPage = new Dictionary<int, List<int>>();
            var skipped = new List<string>();
            int written = 0;

            foreach (var annotation in annotations)
            {
                if (annotation == null) continue;

                if (annotation.PageIndex < 0 || annotation.PageIndex >= doc.Pages.Count)
                {
                    skipped.Add($"{annotation.Subtype} names page {annotation.PageIndex}, which " +
                                $"this document does not have ({doc.Pages.Count} page(s))");
                    continue;
                }

                var page = doc.Pages[annotation.PageIndex];
                int pageObjNum = page.Dictionary.ObjectNumber;
                if (pageObjNum <= 0)
                {
                    skipped.Add($"{annotation.Subtype} on page {annotation.PageIndex}: the page has " +
                                "no object number, so it cannot be updated by an incremental write");
                    continue;
                }

                PdfDictionary annot = AnnotationDictionaryMap.ToDictionary(annotation, includePage: false);
                // An imported review's numbers are the file's, so a NaN or infinity there — XFDF reads
                // "NaN" as a number — is skipped and said, as a stale page is, not written as zeros.
                if (HoldsNonFinite(annot))
                {
                    skipped.Add($"{annotation.Subtype} on page {annotation.PageIndex}: a coordinate, " +
                                "colour or width is not a finite number");
                    continue;
                }

                int annotObjNum = nextObjNum++;
                annot.ObjectNumber = annotObjNum;
                annot.GenerationNumber = 0;
                annot.Set(PdfName.P, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, pageObjNum));

                AttachAppearance(annot, ref nextObjNum, objects);
                objects.Add(new IndirectObject(annotObjNum, 0, annot));

                if (!perPage.TryGetValue(annotation.PageIndex, out var list))
                    perPage[annotation.PageIndex] = list = new List<int>();
                list.Add(annotObjNum);
                written++;
            }

            if (skipped.Count > 0)
                _lastAnnotationsSkipped = string.Join("; ", skipped);

            // Nothing to write is not an error, but it must not append an empty revision either:
            // a revision that changes nothing still moves every offset a signature covers.
            if (written == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return 0;
            }

            foreach (var entry in perPage)
            {
                var page = doc.Pages[entry.Key];
                StagePageWithAnnots(page, BuildAnnotsArray(page, doc, entry.Value), objects);
            }

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
            return written;
        }

        /// <summary>
        /// Which annotations the last <see cref="WriteAnnotations"/> on this thread did not write,
        /// and why; <c>null</c> when every one landed.
        /// </summary>
        /// <remarks>
        /// <b>The count returned says how many were written, not which were not.</b> A caller
        /// importing a review file of two hundred comments and receiving 198 has no way to find the
        /// other two from the return value, and the usual cause — a page number the document no
        /// longer has — is one they can act on. Read on the calling thread; cleared at the start of
        /// every call.
        /// </remarks>
        public static string LastAnnotationsSkipped => _lastAnnotationsSkipped;

        [ThreadStatic] private static string _lastAnnotationsSkipped;

        // ----------------------------------------------------------------
        //  Core writer
        // ----------------------------------------------------------------

        /// <summary>One URI link for <see cref="PdfAnnotationWriter.AddUriLinks"/>.</summary>
        public sealed class UriLinkRequest
        {
            /// <summary>Zero-based page index.</summary>
            public int PageIndex { get; set; }
            /// <summary>One corner's x, in points from the page origin.</summary>
            public double X1 { get; set; }
            /// <summary>One corner's y.</summary>
            public double Y1 { get; set; }
            /// <summary>The opposite corner's x.</summary>
            public double X2 { get; set; }
            /// <summary>The opposite corner's y.</summary>
            public double Y2 { get; set; }
            /// <summary>The target URI.</summary>
            public string Uri { get; set; }
        }

        private static void WriteAnnotation(
            Stream input, Stream output,
            int pageIndex, PdfDictionary annot)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            // Was the third copy of this guard, inlined rather than extracted, which is why a
            // grep for ValidatePageIndex found only two.
            PdfHelpers.ValidatePageIndex(doc, pageIndex);

            var page = doc.Pages[pageIndex];
            int pageObjNum = page.Dictionary.ObjectNumber;
            if (pageObjNum <= 0)
                throw new Core.PdfException("Page has no object number; cannot update via incremental write.");

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();
            int nextObjNum = maxObjNum + 1;
            var objects = new List<IndirectObject>();

            // Allocate object number for the new annotation.
            int annotObjNum = nextObjNum++;
            annot.ObjectNumber = annotObjNum;
            annot.GenerationNumber = 0;

            // Set /P reference to the page.
            annot.Set(PdfName.P, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, pageObjNum));

            // §12.5.5 — write the appearance the annotation shows, rather than leaving every
            // viewer to invent one. This library's own renderer synthesises a fallback at draw
            // time, which is exactly why the absence went unnoticed for so long: the annotations
            // looked right here and were at the mercy of whatever else opened the file.
            AttachAppearance(annot, ref nextObjNum, objects);

            objects.Add(new IndirectObject(annotObjNum, 0, annot));

            StagePageWithAnnots(page, BuildAnnotsArray(page, doc, annotObjNum), objects);

            int newSize = nextObjNum;
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, newSize);
        }

        /// <summary>
        /// Builds the annotation's <c>/AP /N</c> form and points the annotation at it.
        /// </summary>
        /// <remarks>
        /// <para><b>A subtype with nothing to draw is left alone, and the reason is on
        /// <see cref="LastAppearanceSkipped"/>.</b> Text (sticky notes) and Link both fall in that
        /// group by design — see <c>AnnotationAppearanceBuilder</c> — so an absent <c>/AP</c> there
        /// is the correct output rather than a failure, and a caller checking the slot can tell
        /// that case from "the text could not be drawn".</para>
        /// <para>The form is written as its own indirect object rather than inline, so the several
        /// annotations a document accumulates do not each embed a copy in the page.</para>
        /// </remarks>
        private static void AttachAppearance(
            PdfDictionary annot, ref int nextObjNum, List<IndirectObject> objects)
        {
            _lastAppearanceSkipped = null;

            var rect = annot[PdfName.Rect] as PdfArray;
            if (rect == null || rect.Count < 4) { _lastAppearanceSkipped = "the annotation has no /Rect"; return; }

            double x1 = (rect[0] as PdfNumber)?.DoubleValue ?? 0;
            double y1 = (rect[1] as PdfNumber)?.DoubleValue ?? 0;
            double x2 = (rect[2] as PdfNumber)?.DoubleValue ?? 0;
            double y2 = (rect[3] as PdfNumber)?.DoubleValue ?? 0;

            double ox = Math.Min(x1, x2), oy = Math.Min(y1, y2);
            double w = Math.Abs(x2 - x1), h = Math.Abs(y2 - y1);

            var form = AnnotationAppearanceBuilder.Build(annot, w, h, ox, oy, out string reason);
            if (form == null) { _lastAppearanceSkipped = reason; return; }

            int formObjNum = nextObjNum++;
            form.ObjectNumber = formObjNum;
            form.GenerationNumber = 0;
            objects.Add(new IndirectObject(formObjNum, 0, form));

            var ap = new PdfDictionary();
            ap.Set(PdfName.N, new PdfReference(formObjNum, 0));
            annot.Set(PdfName.AP, ap);
        }

        [ThreadStatic] private static string _lastAppearanceSkipped;

        /// <summary>
        /// Why the most recent annotation written <b>on this thread</b> carries no <c>/AP</c>, or
        /// <c>null</c> when it carries one.
        /// </summary>
        /// <remarks>
        /// Cleared at the start of every write on every path, so reading it straight after a call
        /// describes that call. A non-null value is not necessarily a problem: Text and Link
        /// annotations have no appearance <i>by design</i> and say so here.
        /// </remarks>
        public static string LastAppearanceSkipped => _lastAppearanceSkipped;

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Stages <paramref name="page"/>, with <paramref name="annots"/> as its <c>/Annots</c>, as
        /// the object that supersedes it in the appended revision.
        /// </summary>
        /// <remarks>
        /// <para>Three writers did this — one annotation, a batch of links, a batch of anything —
        /// and the five lines are the whole of "supersede a page in place": clone it, replace the
        /// one entry, restore the object number and generation, and add it.</para>
        ///
        /// <para><b>The two assignments are inert on this path, and that was measured rather than
        /// assumed.</b> <c>CloneShallow</c> copies entries only, so the clone really does start at
        /// object number 0 — but the <see cref="IndirectObject"/> below is handed
        /// <c>pageObjNum</c> explicitly, and that is what the revision is written from. Deleting
        /// both assignments leaves all 244 annotation and link tests green. They are kept because
        /// a <see cref="PdfDictionary"/> that knows its own number is what every other staging site
        /// in this assembly produces, and a reader of this one should not have to prove the
        /// difference; they are documented here so nobody records their mutation as coverage.</para>
        /// </remarks>
        private static void StagePageWithAnnots(PdfPage page, PdfArray annots,
            List<IndirectObject> objects)
        {
            int pageObjNum = page.Dictionary.ObjectNumber;

            var updatedPage = PdfPageContentAppender.CloneDictionary(page.Dictionary);
            updatedPage.Set(PdfName.Annots, annots);
            updatedPage.ObjectNumber = pageObjNum;
            updatedPage.GenerationNumber = 0;
            objects.Add(new IndirectObject(pageObjNum, 0, updatedPage));
        }

        /// <summary>
        /// A caller's two opposite corners as an ordered rectangle — §12.5.2 requires a
        /// <c>/Rect</c> in lower-left / upper-right order, and a caller may pass either pair.
        /// </summary>
        /// <remarks>
        /// Five entry points took the same two corners and normalised them the same way — the four
        /// single-annotation ones and the batched link writer, whose corners arrive on a request
        /// object rather than as parameters, which is why a search for the parameter names missed
        /// it. The
        /// appearance path's own <c>Math.Min</c> is deliberately <b>not</b> this: it derives a box
        /// origin and size from a <c>/Rect</c> already built, which is a different question that
        /// happens to share an operator.
        /// </remarks>
        private static PdfAnnotRect NormalisedRect(double x1, double y1, double x2, double y2)
            => new PdfAnnotRect(
                Math.Min(x1, x2), Math.Min(y1, y2),
                Math.Max(x1, x2), Math.Max(y1, y2));

        /// <summary>
        /// The <c>/QuadPoints</c> array for a single quad spanning the caller's two corners
        /// (§12.5.6.10): lower-left, lower-right, upper-left, upper-right.
        /// </summary>
        /// <remarks>
        /// <para>One array, three subtypes — highlight, underline and strike-out are the text-markup
        /// annotations that carry it, and the cluster is exactly those three.
        /// <c>AnnotationDictionaryMap</c> also writes the key but from a caller-supplied array, which
        /// is a different question and is left alone.</para>
        /// <para><b>The corners are used as given, not normalised.</b> That is deliberate and is why
        /// this takes the raw operands rather than the rectangle above: the quad order carries the
        /// caller's orientation, and normalising it here would silently re-orient a marked span.</para>
        /// </remarks>
        private static PdfArray QuadPointsFor(double x1, double y1, double x2, double y2)
        {
            var qp = new PdfArray();
            qp.Add(new PdfNumber(x1)); qp.Add(new PdfNumber(y1));
            qp.Add(new PdfNumber(x2)); qp.Add(new PdfNumber(y1));
            qp.Add(new PdfNumber(x1)); qp.Add(new PdfNumber(y2));
            qp.Add(new PdfNumber(x2)); qp.Add(new PdfNumber(y2));
            return qp;
        }

        private static PdfDictionary BuildAnnotation(
            string subtype, PdfAnnotRect rect,
            string contents, string author)
        {
            var annot = new PdfDictionary();
            annot.Set(PdfName.Type, PdfName.Annot);
            annot.Set(PdfName.Subtype, new PdfName(subtype));

            var rectArr = new PdfArray();
            rectArr.Add(new PdfNumber(rect.X1));
            rectArr.Add(new PdfNumber(rect.Y1));
            rectArr.Add(new PdfNumber(rect.X2));
            rectArr.Add(new PdfNumber(rect.Y2));
            annot.Set(PdfName.Rect, rectArr);

            if (contents != null)
                annot.Set(PdfName.Contents, new PdfString(contents));
            if (author != null)
                annot.Set(PdfName.T, new PdfString(author));

            // Default: print flag set (bit 3).
            annot.Set(PdfName.F, new PdfNumber(PdfAnnotationFlags.Print));

            return annot;
        }

        /// <summary>A number as a PDF real, culture-invariant, through the one formatter.</summary>
        private static string Fmt(double v) => PdfRealFormat.Format(v);

        /// <summary>
        /// Refuses an encrypted document. It loads no catalog, so it has no pages: a single write
        /// said "page index out of range" about a page the file has, and a batch skipped every
        /// annotation as naming a page the document does not have and returned 0.
        /// </summary>
        private static void RefuseEncrypted(PdfDocument doc)
            => EncryptedRevision.Refuse(doc, "add annotations to", "they");

        // A caller's NaN or infinity reached the /Rect, /QuadPoints or /C as written zeros — a note
        // nothing can find or a highlight of nothing — and the write reported success (CLAUDE.md: a
        // public float or double argument is refused for NaN and ±∞).
        private static void RequireFiniteCorners(double x1, double y1, double x2, double y2)
        {
            RequireFinite(nameof(x1), x1, nameof(y1), y1);
            RequireFinite(nameof(x2), x2, nameof(y2), y2);
        }

        private static void RequireFinite(string firstName, double first, string secondName, double second)
        {
            NumericArguments.RequireFinite(first, firstName);
            NumericArguments.RequireFinite(second, secondName);
        }

        private static void RequireFiniteColour(float r, float g, float b)
        {
            NumericArguments.RequireFinite(r, nameof(r));
            NumericArguments.RequireFinite(g, nameof(g));
            NumericArguments.RequireFinite(b, nameof(b));
        }

        /// <summary>
        /// Whether a number in <paramref name="obj"/>, as deep as an annotation's arrays go, is NaN or
        /// infinite — which the writer would put in the file as 0.
        /// </summary>
        private static bool HoldsNonFinite(PdfObject obj, int depth = 0)
        {
            if (depth > PdfLimits.MaxObjectGraphDepth) return false;
            switch (obj)
            {
                case PdfNumber number:
                    return double.IsNaN(number.DoubleValue) || double.IsInfinity(number.DoubleValue);
                case PdfArray array:
                    for (int i = 0; i < array.Count; i++)
                        if (HoldsNonFinite(array[i], depth + 1)) return true;
                    return false;
                case PdfDictionary dict:
                    foreach (var kvp in dict)
                        if (HoldsNonFinite(kvp.Value, depth + 1)) return true;
                    return false;
                default:
                    return false;
            }
        }

        private static void SetColor(PdfDictionary annot, PdfName key, float r, float g, float b)
        {
            var c = new PdfArray();
            c.Add(new PdfNumber(r));
            c.Add(new PdfNumber(g));
            c.Add(new PdfNumber(b));
            annot.Set(key, c);
        }

        private static PdfArray BuildAnnotsArray(PdfPage page, PdfDocument doc, int newAnnotObjNum)
            => BuildAnnotsArray(page, doc, new[] { newAnnotObjNum });

        /// <summary>
        /// The page's existing <c>/Annots</c> entries — as the <b>references</b> they are, not the
        /// resolved dictionaries — followed by the new annotations.
        /// </summary>
        private static PdfArray BuildAnnotsArray(PdfPage page, PdfDocument doc, IReadOnlyList<int> newAnnotObjNums)
        {
            var arr = new PdfArray();

            // An unreadable /Annots is refused, as the form builder refuses it: the array written for
            // the page replaces it, holding only the new annotations, and every link, comment and
            // widget the page had would leave the current revision.
            var rawArr = EntryRead.Resolve(doc.ResolveObject, page.Dictionary[PdfName.Annots], out string unreadable) as PdfArray;
            if (unreadable != null)
                throw new Core.PdfException(
                    "Page " + (page.PageIndex + 1) + "'s /Annots could not be read (" + unreadable +
                    "), so adding an annotation there would replace every annotation on the page. No changes were written.");
            if (rawArr != null)
                for (int i = 0; i < rawArr.Count; i++)
                    arr.Add(rawArr[i]);

            for (int i = 0; i < newAnnotObjNums.Count; i++)
                arr.Add(new PdfReference(newAnnotObjNums[i], 0));

            return arr;
        }

        private readonly struct PdfAnnotRect
        {
            public readonly double X1, Y1, X2, Y2;
            public PdfAnnotRect(double x1, double y1, double x2, double y2)
            { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
        }
    }
}
