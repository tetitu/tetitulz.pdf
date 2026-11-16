// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Thrown when a region cannot be redacted in a way that is provably leak-free,
    /// so the operation fails closed instead of producing output that still contains
    /// the supposedly-removed data.
    /// </summary>
    public sealed class PdfRedactionException : PdfException
    {
        public PdfRedactionException(string message) : base(message) { }
    }

    /// <summary>A rectangular region (PDF user space, origin bottom-left) to redact on a page.</summary>
    public sealed class PdfRedactionArea
    {
        public int PageIndex { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        /// <summary>Cover-box fill colour (RGB 0..1). Default black.</summary>
        public float FillR { get; set; }
        public float FillG { get; set; }
        public float FillB { get; set; }
    }

    /// <summary>
    /// TRUE redaction via glyph-level content-stream surgery.
    ///
    /// <para>Unlike a visual cover box, this physically removes the text glyphs and
    /// images that fall inside each region from the page content stream (rewriting
    /// <c>TJ</c> runs with spacing adjustments so the surviving text keeps its
    /// position), drops intersecting annotations, stamps an opaque cover box, and
    /// writes a FULL single-revision PDF so no superseded revision retains the data.</para>
    ///
    /// <para><b>Every</b> intersecting annotation — Stamp, FreeText and Ink exactly as much as
    /// Widget — has its value and appearance keys cleared as well as being dropped from the page's
    /// <c>/Annots</c>. An <c>/AP</c> is <i>drawn content</i> standing inside the region the caller
    /// named, so there is nothing widget-specific about the leak: dropping the annotation from
    /// <c>/Annots</c> alone leaves the dictionary reachable from anywhere else that points at it
    /// (a tagged document's <c>/StructTreeRoot … /OBJR /Obj</c> does), and the rewrite then emits
    /// the appearance stream intact. See <see cref="ScrubDroppedAnnotations"/>.</para>
    ///
    /// <para>A <b>widget annotation</b> needs one thing more, and it is the only genuinely
    /// widget-specific part: detaching from the catalog's <c>/AcroForm /Fields</c>, the second
    /// route to it. Dropping it from <c>/Annots</c> alone left the field dictionary, its <c>/V</c>
    /// and its <c>/AP /N</c> stream reachable from the catalog, so the rewrite emitted them all.
    /// See <see cref="DetachRedactedFormFields"/>.</para>
    ///
    /// <para>Content inside a <b>Form XObject</b> is redacted too. A redacted <b>clone</b> of the
    /// form is substituted at the invocation on the page being redacted; the form is never
    /// rewritten in place, so no other page that draws it is disturbed.</para>
    ///
    /// <para><b>What that means for a form drawn from more than one place.</b> When the form is
    /// drawn only once in the whole document, the original becomes unreferenced and the
    /// single-revision rewrite — which emits reachable objects only — drops it, so the removed
    /// bytes survive nowhere. When the form is <i>shared</i>, the original must stay for the pages
    /// still drawing it, and those pages were never redacted: <b>the content therefore remains in
    /// the output, reachable from them.</b> Redaction removes the data from the pages you name, not
    /// from the file, whenever a named page shares a form with an unnamed one. To clear it
    /// everywhere, redact every page that draws the form.</para>
    ///
    /// <para><b>What a page says about its text goes with it.</b> A text extractor prefers
    /// <c>/ActualText</c> to the glyphs, so a redaction that left one would hand the text straight back:
    /// on a redacted page every marked-content property that can carry text goes
    /// (<see cref="ScrubbedMarkedContent"/>), as do the <c>/ActualText</c>, <c>/Alt</c>, <c>/E</c> and
    /// <c>/T</c> of every structure element on it and every element above one
    /// (<see cref="ScrubStructureText"/>) — all of them, since which described the removed text cannot
    /// be read from the file. So do a page's thumbnail, which is a picture of it, and its private data
    /// (<see cref="DerivedPageEntries"/>), an operand no operator reads, and an operator nothing defines.</para>
    ///
    /// <para><b>Still fail-closed</b> (<see cref="PdfRedactionException"/>) for an encrypted
    /// document, for form nesting deeper than the internal depth cap, for a page whose form walks run
    /// past <see cref="PdfLimits.MaxFormXObjectInvocationsPerPage"/> or, each charged its decoded length,
    /// <see cref="PdfLimits.MaxFormContentBytesPerPage"/>, for a form meeting a region whose content will
    /// not decode (read as empty, its bytes would stay), for a page painting through a
    /// tiling pattern or soft mask that draws text (<see cref="PaintSourceDrawsImage"/>): which
    /// tile a region meets cannot be told, and a pattern's cell is one stream for all of them — and for a
    /// region meeting a paint that goes through a pattern or mask drawing an image
    /// (<see cref="RefuseCoveredImageSource"/>), whose pixels a cover box would hide and the file keep.
    /// A shared or nested form is cloned at its invocation.</para>
    ///
    /// <para><b>Documented limits (v1):</b> vector graphics and shadings under a box are
    /// visually covered but their geometry is not removed (they carry no extractable
    /// text/image data); a Type 3
    /// font's glyph procedures stay, as any embedded font's outlines do, the codes that showed them gone;
    /// an embedded glyph is placed by its advance and an em box, so one drawn far outside that box, which
    /// the file's own font program can do, is covered and not removed (a Type 3 glyph is read for where it
    /// paints: <see cref="RedactionType3Font"/>);
    /// a form the page's resources name and its content never draws stands in no region and stays — and
    /// the document's own metadata, outline and attachments are outside any region
    /// (<see cref="PdfTextRedactor"/> cleans the first two of its matches; <c>Core.Document.PdfSanitizer</c>
    /// takes the attachments); a form's XFA packets go whole, since nothing in
    /// them says where a page draws it; encrypted PDFs throw. An image a region covers in part keeps its
    /// uncovered pixels (<see cref="TryRedactImagePixels"/>) — a stencil, a masked
    /// image or one this cannot decode is removed whole, everywhere it is referenced, as every
    /// overlapping image was before. Rotated/skewed text is handled via full corner transforms
    /// but with a generous glyph box that biases toward removal.</para>
    /// </summary>
    public static class PdfRedactionEngine
    {
        private const int MaxFormDepth = 16;

        [ThreadStatic]
        private static string _lastSharedFormWarning;

        /// <summary>
        /// Names the Form XObjects whose original object survived the last <see cref="Redact(Stream, Stream, IReadOnlyList{PdfRedactionArea})"/> on
        /// this thread because a page the caller did not redact still draws them - so their
        /// content, including what was removed elsewhere, <b>remains in the output</b>. Null when
        /// the last redaction removed everything it was asked to.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the one outcome where redaction does not remove the data, and until
        /// it would be the one outcome it could not report.</b> A caller would have no way to
        /// distinguish a redaction that
        /// removed the secret from one that left it reachable from another page.</para>
        ///
        /// <para><b>Not an error, and not a refusal.</b> The original must survive for the pages
        /// still drawing it - dropping it would corrupt a page the caller never named. The answer
        /// is to redact every page that draws the form, which is a decision only the caller can
        /// make. Hence a diagnostic rather than a <see cref="PdfRedactionException"/>.</para>
        ///
        /// <para>Read it on the thread that called <see cref="Redact(Stream, Stream, IReadOnlyList{PdfRedactionArea})"/>, and read it <i>after</i>
        /// the call - it is cleared at the start of every redaction, on every path.</para>
        /// </remarks>
        public static string LastSharedFormWarning => _lastSharedFormWarning;
        // Inflate every glyph/image box by this many points before the intersection
        // test, so minor advance-width drift never causes a missed (leaking) glyph.
        private const float IntersectMargin = 1.0f;

        /// <summary>
        /// How close to a pixel edge, in pixels, a redaction region's boundary on an image is read as
        /// on the edge (<see cref="TryRedactImagePixels"/>): a half-image region ends at 2.0000001
        /// columns in float arithmetic, and rounding that outward would black a third column.
        /// </summary>
        private const double FootprintEpsilon = 1e-3;

        /// <summary>Redacts the given regions and writes a full, single-revision PDF to <paramref name="output"/>.</summary>
        public static void Redact(Stream input, Stream output, IReadOnlyList<PdfRedactionArea> areas)
            => Redact(input, output, areas, null, null);

        /// <summary>
        /// The same, and <paramref name="documentText"/> applied to the text the document holds outside
        /// its pages (<see cref="ScrubDocumentText"/>), each place it changed named in
        /// <paramref name="removedFrom"/>. A document whose pages lose nothing and whose text it
        /// changes is rewritten all the same; one it changes nothing in is copied.
        /// </summary>
        internal static void Redact(Stream input, Stream output, IReadOnlyList<PdfRedactionArea> areas,
            Func<string, string> documentText, DistinctStrings removedFrom)
        {
            // Cleared FIRST, so it cannot outlive its own call: every later path - the argument
            // throws, the encrypted refusal, the no-regions copy - leaves this method without
            // reaching the report below, and a stale warning from a previous redaction on this
            // thread would be read as belonging to this one.
            _lastSharedFormWarning = null;

            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (areas == null) throw new ArgumentNullException(nameof(areas));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            if (doc.IsEncrypted)
                throw new PdfRedactionException(
                    "Encrypted PDFs are not supported by true redaction; decrypt the document first.");

            // Group regions by page.
            var byPage = new Dictionary<int, List<float[]>>();   // pageIndex -> list of [x,y,w,h]
            var covers = new Dictionary<int, List<PdfRedactionArea>>();
            for (int ai = 0; ai < areas.Count; ai++)
            {
                var a = areas[ai];
                if (a == null) throw new ArgumentException("areas contains a null entry.", nameof(areas));
                if (a.PageIndex < 0 || a.PageIndex >= doc.Pages.Count)
                    throw new ArgumentOutOfRangeException(nameof(areas),
                        $"PageIndex {a.PageIndex} is out of range (0..{doc.Pages.Count - 1}).");
                // A NaN side makes a region that intersects nothing: the text, the paths and the
                // images under it all stayed, the cover was drawn at [0 0 0 0], and the call
                // returned as a success — the one outcome a redaction must never have.
                NumericArguments.RequireFinite(a.X, nameof(areas), $"[{ai}].X");
                NumericArguments.RequireFinite(a.Y, nameof(areas), $"[{ai}].Y");
                NumericArguments.RequireFinite(a.Width, nameof(areas), $"[{ai}].Width");
                NumericArguments.RequireFinite(a.Height, nameof(areas), $"[{ai}].Height");
                float x = Math.Min(a.X, a.X + a.Width);
                float y = Math.Min(a.Y, a.Y + a.Height);
                float w = Math.Abs(a.Width);
                float h = Math.Abs(a.Height);
                if (!byPage.TryGetValue(a.PageIndex, out var list)) { list = new List<float[]>(); byPage[a.PageIndex] = list; covers[a.PageIndex] = new List<PdfRedactionArea>(); }
                list.Add(new[] { x, y, x + w, y + h });   // store as [x1,y1,x2,y2]
                covers[a.PageIndex].Add(a);
            }

            if (byPage.Count == 0)
            {
                if (documentText != null && ScrubDocumentText(doc, documentText, removedFrom))
                {
                    if (!PdfFullRewriter.TryFullRewrite(doc, output))
                        throw new PdfRedactionException("Document has no resolvable catalog; cannot rewrite.");
                    return;
                }
                ms.Position = 0;
                ms.CopyTo(output);
                return;
            }

            using var fontCache = new FontCache();
            // Transitive "drawn exactly once in the whole document" test. No longer decides
            // whether redaction is POSSIBLE - every form is now cloned at its invocation - but it
            // still decides what the caller is TOLD: a form that is drawn elsewhere keeps its
            // original object, so its content survives in the output and that must be reported.
            var formIsUniquelyDrawn = FormInvocationCounter.BuildUniquelyDrawnTest(doc);
            var forms = new FormCloneContext(doc, formIsUniquelyDrawn);

            // Every annotation dropped from a page, by reference identity: the parser caches one
            // instance per object number, so the dictionary reached through /Annots and the one
            // reached through /AcroForm /Fields are the same object.
            var droppedAnnots = new HashSet<PdfDictionary>();
            // Which pages use which content streams, read once for every page this redacts.
            var contentUsage = new PdfFullRewriter.ContentUsage(doc);
            var redactedPages = new HashSet<PdfObject>(PdfReferenceEquality.Instance);

            // Mutate each target page in place: redact its content stream + drop intersecting annotations.
            foreach (var kvp in byPage)
            {
                var page = doc.Pages[kvp.Key];
                var rects = kvp.Value;

                byte[] content = page.GetContentStreamData() ?? Array.Empty<byte>();
                forms.BeginPage();
                byte[] redacted = RedactPageContent(content, page.Resources, doc, rects, covers[kvp.Key], fontCache, forms);

                SubstitutePageContent(page, doc, redacted, forms, contentUsage);
                FilterAnnotations(page, doc, rects, droppedAnnots);
                foreach (PdfName derived in DerivedPageEntries) page.Dictionary.Remove(derived);
                redactedPages.Add(page.Dictionary);
            }

            ScrubStructureText(doc, redactedPages);

            // Dropping an annotation from /Annots does not remove it from the document - anything
            // else pointing at the dictionary keeps it alive - so the value and appearance keys go
            // on every one of them, whatever its subtype.
            ScrubDroppedAnnotations(droppedAnnots);

            // A WIDGET is reached a second way as well, through /AcroForm /Fields. Must run after
            // every page, so one pass sees all the widgets a multi-widget field lost.
            DetachRedactedFormFields(doc, droppedAnnots);
            RemoveXfa(doc);
            if (documentText != null) ScrubDocumentText(doc, documentText, removedFrom);

            ReportSharedFormsLeftIntact(forms);

            FullRewrite(doc, output, forms);
        }

        /// <summary>
        /// Removes a form's XFA packets. They hold every field's value and the form's own text, in XML
        /// that says nothing of where a page draws it, so no region can be matched to what they hold;
        /// the page's fields stay as an AcroForm. A form with no pages of its own but XFA's, which asks
        /// to be rendered from them, is left its placeholder page.
        /// </summary>
        private static void RemoveXfa(PdfDocument doc)
        {
            doc.Catalog?.Dictionary?.Remove(PdfName.NeedsRendering);
            if (doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.AcroForm]) is PdfDictionary acro)
                acro.Remove(PdfName.XFA);
        }

        /// <summary>
        /// <paramref name="clean"/> applied to the text a document holds outside its pages: each string
        /// of the information dictionary, each outline item's <c>/Title</c>, and every text node and
        /// attribute value of the catalog's XMP packet. A string it changes is replaced by a new direct
        /// one, so the full rewrite carries no copy of the old. A packet that cannot be read, parsed or
        /// bounded is removed whole, since nothing can say what it holds. True when anything changed.
        /// </summary>
        /// <remarks>
        /// Page-level and image metadata are not read here: a redacted page's packet is removed
        /// (<see cref="DerivedPageEntries"/>), and an image's or a form's is outside what the caller
        /// searched for.
        /// </remarks>
        internal static bool ScrubDocumentText(PdfDocument doc, Func<string, string> clean, DistinctStrings removedFrom)
        {
            bool changed = false;
            void Removed(string where) { changed = true; removedFrom?.Add(where); }

            if (doc.ResolveObject(doc.Trailer[PdfName.Info]) is PdfDictionary info)
                foreach (var entry in new List<KeyValuePair<PdfName, PdfObject>>(info))
                {
                    if (!(PdfResolve.Str(doc, entry.Value) is PdfString value)) continue;
                    string text = value.GetText(), cleaned = clean(text);
                    if (cleaned == text) continue;
                    info.Set(entry.Key, new PdfString(cleaned));
                    Removed("/Info /" + entry.Key.Value);
                }

            PdfDictionary catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return changed;

            OutlineItems.Walk(doc, catalog, item =>
            {
                if (PdfResolve.Str(doc, item[PdfName.Title]) is PdfString title)
                {
                    string text = title.GetText(), cleaned = clean(text);
                    if (cleaned != text)
                    {
                        item.Set(PdfName.Title, new PdfString(cleaned));
                        Removed("an outline item's /Title");
                    }
                }
                return true;
            }, cut => throw new PdfRedactionException(
                "The outline's titles could not all be searched, so nothing was written: " + cut + "."));

            if (catalog[PdfName.Metadata] != null)
            {
                string why = null;
                string packet = Core.Metadata.XmpPacketDom.ReadCatalogPacket(doc, catalog, (message, _) => why = message);
                // The packet is read no further than XmpPacketDom.MaxExistingPacketBytes, and its depth
                // and re-serialised size are held to the merge limits before it is written again.
                System.Xml.XmlDocument xmp = packet == null ? null : Core.Metadata.XmpPacketDom.TryLoad(packet, (m, _) => why = m);
                if (xmp != null && !Core.Metadata.XmpPacketDom.IsMergeable(xmp, (m, _) => why = m)) xmp = null;

                if (xmp == null)
                {
                    if (packet != null || why != null)
                    {
                        catalog.Remove(PdfName.Metadata);
                        Removed("the XMP packet, whole: " + (why ?? "it could not be read"));
                    }
                }
                else if (CleanXmp(xmp, clean) && doc.ResolveObject(catalog[PdfName.Metadata]) is PdfStream stream)
                {
                    PdfFullRewriter.StripStream(stream, Encoding.UTF8.GetBytes(Core.Metadata.XmpPacketDom.Serialize(xmp)));
                    Removed("the XMP packet");
                }
            }
            return changed;
        }

        /// <summary>
        /// Every text node and attribute value of <paramref name="xmp"/> through <paramref name="clean"/>,
        /// and a comment it would change removed whole (what is left of one may not be a comment); true
        /// when anything changed. Iterative, as the packet chooses its depth.
        /// </summary>
        private static bool CleanXmp(System.Xml.XmlDocument xmp, Func<string, string> clean)
        {
            bool changed = false;
            var comments = new List<System.Xml.XmlComment>();
            var pending = new Stack<System.Xml.XmlNode>();
            pending.Push(xmp.DocumentElement);
            while (pending.Count > 0)
            {
                System.Xml.XmlNode node = pending.Pop();
                if (node is System.Xml.XmlComment comment)
                {
                    if (clean(comment.Value ?? "") != comment.Value) comments.Add(comment);
                }
                else if (node is System.Xml.XmlCharacterData data)
                {
                    string cleaned = clean(data.Value ?? "");
                    if (cleaned != data.Value) { data.Value = cleaned; changed = true; }
                }
                if (node.Attributes != null)
                    foreach (System.Xml.XmlAttribute attribute in node.Attributes)
                    {
                        string cleaned = clean(attribute.Value);
                        if (cleaned != attribute.Value) { attribute.Value = cleaned; changed = true; }
                    }
                foreach (System.Xml.XmlNode child in node.ChildNodes) pending.Push(child);
            }
            foreach (System.Xml.XmlComment comment in comments) { comment.ParentNode.RemoveChild(comment); changed = true; }
            return changed;
        }

        /// <summary>
        /// What a redacted page or a redacted clone of a form must not carry on: a thumbnail is a picture
        /// of the page; private application data and metadata may hold the original; an associated file
        /// is the source itself, as an alternate image or prepress reference is another copy of a picture.
        /// </summary>
        private static readonly PdfName[] DerivedPageEntries =
        {
            PdfName.Thumb, PdfName.PieceInfo, PdfName.Metadata, PdfName.AF, PdfName.OPI,
        };

        /// <summary>
        /// Removes what describes a redacted page's text from the structure tree: an element on the page,
        /// by its <c>/Pg</c> or a marked-content or object reference's, and each element above it, whose
        /// alternative stands for all it holds. A tree the walk cannot finish cannot be shown clean.
        /// </summary>
        private static void ScrubStructureText(PdfDocument doc, HashSet<PdfObject> redactedPages)
        {
            if (!(doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.StructTreeRoot]) is PdfDictionary root)) return;

            var pageIndexes = new HashSet<int>();
            for (int i = 0; i < doc.Pages.Count; i++)
                if (redactedPages.Contains(doc.Pages[i].Dictionary)) pageIndexes.Add(i);

            void ScrubUp(StructureTreeNode node)
            {
                for (StructureTreeNode n = node; n != null && n.Tag == null; n = n.Parent)
                {
                    n.Tag = n;
                    n.Dictionary.Remove(PdfName.ActualText);
                    n.Dictionary.Remove(PdfName.Alt);
                    n.Dictionary.Remove(PdfName.E);
                    n.Dictionary.Remove(PdfName.T);
                    n.Dictionary.Remove(PdfName.AF);   // an associated file is the source, as the page's own is
                }
            }

            var walk = new StructureTreeWalk(doc)
            {
                Enter = node => { if (pageIndexes.Contains(node.PageIndex)) ScrubUp(node); },
                Kid = kid =>
                {
                    if (kid.Resolved is PdfDictionary reference && kid.Kind != StructKidKind.Element
                        && doc.ResolveObject(reference[PdfName.Pg]) is PdfDictionary page && redactedPages.Contains(page))
                        ScrubUp(kid.Parent);
                },
            };
            walk.Run(root);

            if (walk.FirstCeiling != null)
                throw new PdfRedactionException(
                    "The structure tree is past what can be walked, so what it says of the redacted text cannot be removed.");
        }

        /// <summary>
        /// Publishes <see cref="FormCloneContext.SharedFormLeftIntact"/> on
        /// <see cref="LastSharedFormWarning"/>.
        /// </summary>
        /// <remarks>
        /// Distinct names, because a form drawn on several of the redacted pages is recorded once
        /// per invocation and a caller reading the message wants the forms, not the draws.
        /// </remarks>
        private static void ReportSharedFormsLeftIntact(FormCloneContext forms)
        {
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);   // a search of the list was invocations × forms
            foreach (string name in forms.SharedFormLeftIntact)
                if (seen.Add(name)) distinct.Add(name);

            if (distinct.Count == 0) return;

            _lastSharedFormWarning =
                distinct.Count + " Form XObject(s) - or partly redacted image XObjects - were left intact because a page that was not " +
                "redacted still draws them, so their original content - including what was " +
                "removed elsewhere - remains in the output: " + string.Join(", ", distinct) +
                ". Redact every page that draws them to clear it from the file.";
        }

        // ---------------------------------------------------------------
        //  Page content redaction (top level: text + images + inline images)
        // ---------------------------------------------------------------

        private static byte[] RedactPageContent(
            byte[] content, PdfDictionary resources, PdfDocument doc,
            List<float[]> rects, List<PdfRedactionArea> coverBoxes, FontCache fontCache,
            FormCloneContext forms)
        {
            using var outMs = new MemoryStream();
            var w = new PdfWriter(outMs);

            float[] identity = { 1, 0, 0, 1, 0, 0 };

            // The page's content is bracketed, and the walk closes what it leaves open, so the boxes
            // are drawn in the page's initial state: a matrix, clip, opacity or layer the page never
            // ended would otherwise move them, clip them, make them clear or hide them, off what they
            // stand over.
            w.WriteRaw(PdfOperators.Save + "\n");
            WalkContent(content, resources, doc, rects, fontCache, w, identity, depth: 0, forms);
            w.WriteRaw(PdfOperators.Restore + "\n");

            foreach (var c in coverBoxes)
            {
                w.WriteRaw($"{PdfOperators.Save} {Fmt(c.FillR)} {Fmt(c.FillG)} {Fmt(c.FillB)} {PdfOperators.SetRgbFill} ");
                w.WriteRaw($"{Fmt(c.X)} {Fmt(c.Y)} {Fmt(c.Width)} {Fmt(c.Height)} " +
                           $"{PdfOperators.Rectangle} {PdfOperators.Fill} {PdfOperators.Restore}\n");
            }

            return outMs.ToArray();
        }

        /// <summary>
        /// Owns Form XObject cloning for one redaction: object-number allocation, the extra
        /// objects the rewriter must emit, the resource entries that point at them, and the record
        /// of which shared forms were left intact.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One instance per <c>Redact</c> call, threaded through the whole walk, because
        /// <see cref="PdfObjectIndex.AllocateObjectNumber"/> is only collision-free when every
        /// allocation site shares one index.
        /// </para>
        /// <para>
        /// <b>The clone is substituted at the invocation, never in the form.</b> Rewriting a shared
        /// form in place would silently change every other page that draws it. Cloning confines the edit to the page the caller named.
        /// </para>
        /// </remarks>
        private sealed class FormCloneContext
        {
            private readonly PdfObjectIndex _index;
            private readonly Dictionary<int, PdfObject> _extras = new Dictionary<int, PdfObject>();
            private readonly List<string> _sharedLeftIntact = new List<string>();
            private int _seq;

            internal FormCloneContext(PdfDocument doc, Func<int, bool> formIsUniquelyDrawn)
            {
                _index = PdfObjectIndex.Build(doc);
                IsUniquelyDrawn = formIsUniquelyDrawn;
            }

            /// <summary>Transitive "drawn exactly once in the whole document" test.</summary>
            internal Func<int, bool> IsUniquelyDrawn { get; }

            private WalkBudget _walks = new WalkBudget(PdfLimits.MaxFormXObjectInvocationsPerPage);
            private long _formBytesLeft = PdfLimits.MaxFormContentBytesPerPage;

            /// <summary>Starts a page's walk budget and its form-content budget.</summary>
            internal void BeginPage()
            {
                _walks = new WalkBudget(PdfLimits.MaxFormXObjectInvocationsPerPage);
                _formBytesLeft = PdfLimits.MaxFormContentBytesPerPage;
            }

            /// <summary>
            /// A form's decoded content for one walk, charged to the page's
            /// <see cref="PdfLimits.MaxFormContentBytesPerPage"/>; refuses a form it cannot read, and
            /// one the budget has no room left for.
            /// </summary>
            /// <remarks>
            /// Each invocation walks the form again and may write a clone of it, so the walk count
            /// alone bounds nothing: a 16 KB form drawn the 100&#160;000 times the walk budget admits
            /// is 1.6 GB walked and written. The decode is sized by what is left; a refused one
            /// spends half of it, since only the decoder knows how far it got. A form that will not
            /// decode is refused rather than read as empty: an empty reading removes nothing, and
            /// the form's bytes, which another reader may decode, would stay in the output.
            /// </remarks>
            internal byte[] FormContent(PdfStream form)
            {
                if (_formBytesLeft < 1)
                    throw new PdfRedactionException(
                        $"Form XObjects on this page hold more than {PdfLimits.MaxFormContentBytesPerPage} bytes of " +
                        "content in total, counted at every invocation; too much to redact safely.");
                if (!form.TryGetDecodedData(Math.Min(PdfLimits.MaxDecompressedBytes, _formBytesLeft), out byte[] content, out string refused))
                {
                    _formBytesLeft -= Math.Max(1, _formBytesLeft / 2);
                    throw new PdfRedactionException("A Form XObject on this page could not be read, so it cannot be redacted: " + refused);
                }
                content ??= Array.Empty<byte>();
                _formBytesLeft -= content.Length;
                return content;
            }

            /// <summary>
            /// Charges one content walk against the page's budget; false once it is spent.
            /// </summary>
            /// <remarks>
            /// <see cref="MaxFormDepth"/> bounds one branch and never the number of branches: a chain
            /// of sixteen forms each drawing the next ten times never exceeds the depth and is 10^16
            /// walks. The depth check fails closed only because a cycle reaches it on its first
            /// branch; an acyclic fan-out does not, so it needs this.
            /// </remarks>
            internal bool TakeWalk() => _walks.Take();

            private readonly Dictionary<PdfStream, bool> _paintSourceDrawsImage = new Dictionary<PdfStream, bool>(PdfReferenceEquality.Instance);

            /// <summary>
            /// What reading a pattern's cell or a soft mask's group found — whether it draws an image —
            /// once it has been read in this redaction; each is read once however often it is set.
            /// </summary>
            internal bool TryPaintSource(PdfStream source, out bool drawsImage) => _paintSourceDrawsImage.TryGetValue(source, out drawsImage);

            /// <summary>Records what <see cref="TryPaintSource"/> answers for <paramref name="source"/>.</summary>
            internal void RecordPaintSource(PdfStream source, bool drawsImage) => _paintSourceDrawsImage[source] = drawsImage;

            private readonly Dictionary<PdfDictionary, RedactionType3Font> _type3Fonts =
                new Dictionary<PdfDictionary, RedactionType3Font>(PdfReferenceEquality.Instance);
            private readonly WalkBudget _type3Bytes = new WalkBudget(RedactionType3Font.MaxProcedureBytesPerRedaction);

            /// <summary>A Type 3 font as this redaction reads it: its glyphs once, however many streams show them.</summary>
            internal RedactionType3Font Type3FontOf(PdfDictionary font, PdfDocument doc)
            {
                if (!_type3Fonts.TryGetValue(font, out var read))
                    _type3Fonts[font] = read = new RedactionType3Font(font, doc, _type3Bytes);
                return read;
            }

            /// <summary>Objects the rewriter must emit alongside the document's own.</summary>
            internal IReadOnlyDictionary<int, PdfObject> Extras => _extras;

            /// <summary>
            /// The same allocator and sink the form clones use, for a page <c>/Contents</c> stream
            /// shared with a page the caller did not name — see
            /// <see cref="PdfFullRewriter.TrySubstitutePageContent"/>. One allocator, so a content
            /// clone and a form clone can never be handed the same object number.
            /// </summary>
            internal Func<int> AllocateObjectNumber => _index.AllocateObjectNumber;

            /// <summary>Mutable view of <see cref="Extras"/> for the rewriter's own clones.</summary>
            internal IDictionary<int, PdfObject> ExtrasSink => _extras;

            /// <summary>
            /// Names of forms whose original object survives because some page the caller did not
            /// redact still draws it. Their content — including whatever was removed elsewhere —
            /// remains in the output.
            /// </summary>
            internal IReadOnlyList<string> SharedFormLeftIntact => _sharedLeftIntact;

            /// <summary>
            /// Registers a redacted clone of <paramref name="original"/> and adds it to
            /// <paramref name="resources"/> under a fresh name, which is returned for the caller to
            /// emit a <c>Do</c> against.
            /// </summary>
            internal string SubstituteClone(PdfStream original, string originalName,
                                            byte[] redactedContent,
                                            PdfDictionary resources, PdfDocument doc)
            {
                var clone = CloneFormStream(original);
                PdfFullRewriter.StripStream(clone, redactedContent);
                return SubstituteClone(original, originalName, clone, resources, doc, "RdxForm");
            }

            /// <summary>
            /// Registers <paramref name="clone"/> — a prepared stream standing in for
            /// <paramref name="original"/> at this invocation (a form's redacted content, a partly
            /// redacted image's pixels) — under the original's name when the original is drawn once
            /// in the document, else under a fresh one built on <paramref name="namePrefix"/>, the
            /// original recorded as left intact.
            /// </summary>
            internal string SubstituteClone(PdfStream original, string originalName, PdfStream clone,
                                            PdfDictionary resources, PdfDocument doc, string namePrefix)
            {
                int objNum = _index.AllocateObjectNumber();
                _extras[objNum] = clone;

                var xobjects = XObjectCategory(resources, doc);
                bool unique = original.ObjectNumber > 0 && IsUniquelyDrawn(original.ObjectNumber);

                if (unique)
                {
                    // REPOINT the existing name, so the original form is referenced by nothing, so the
                    // rewriter - which emits reachable objects only - drops it and its content
                    // never reaches the output.
                    //
                    // Adding a second name instead would leave the original reachable through the
                    // resource entry even though no content stream names it any more, and the
                    // secret would survive in the file. That is exactly what the first draft of
                    // this change did, and RedactRegions_RedactsContentInsideSingleUseForm caught
                    // it by checking the raw output bytes rather than the extracted text.
                    xobjects.Set(new PdfName(originalName), new PdfReference(objNum, 0));
                    return originalName;
                }

                // Shared or nested: the original must survive for the invocations on pages the
                // caller did not name, so the clone gets a fresh name and only THIS invocation
                // moves to it. The original's content - secret included - stays in the output,
                // which is the documented reduction in what redaction promises here.
                string name = namePrefix + _seq++;
                xobjects.Set(new PdfName(name), new PdfReference(objNum, 0));
                _sharedLeftIntact.Add(originalName);
                return name;
            }

            /// <summary>
            /// A shallow copy of a form's stream dictionary as a <see cref="PdfStream"/>, ready for
            /// <see cref="PdfFullRewriter.StripStream"/> to fill with redacted content.
            /// </summary>
            /// <remarks>
            /// Not <see cref="PdfDictionary.DeepClone(PdfDictionary,int)"/>: that returns a
            /// <c>PdfDictionary</c>, and while it preserves stream-ness for nested <i>values</i> the
            /// top-level result is a plain dictionary — a form cloned that way loses its data and
            /// is no longer a stream at all. The entries are copied by reference on purpose; the
            /// clone differs from the original only in its content bytes, and sharing
            /// <c>/Resources</c> with the original is correct because both draw the same resources.
            /// </remarks>
            private static PdfStream CloneFormStream(PdfStream original)
            {
                var clone = new PdfStream();
                foreach (var kvp in original) clone.Set(kvp.Key, kvp.Value);
                foreach (PdfName derived in DerivedPageEntries) clone.Remove(derived);
                return clone;
            }

            /// <summary>
            /// The <c>/XObject</c> sub-dictionary of <paramref name="resources"/>, created and
            /// attached when absent so a clone always has somewhere to live.
            /// </summary>
            private static PdfDictionary XObjectCategory(PdfDictionary resources, PdfDocument doc)
            {
                if (doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary existing)
                    return existing;
                var created = new PdfDictionary();
                resources.Set(PdfName.XObject, created);
                return created;
            }
        }

        /// <summary>Emits <c>/Name Do</c> for a substituted clone.</summary>
        private static void WriteDo(PdfWriter w, string name)
            => w.WriteRaw("/" + PdfNameEscape.Escape(name) + " " + PdfOperators.InvokeXObject + "\n");

        /// <summary>
        /// Buffers a path-construction sequence so the keep-or-drop decision can be made at the
        /// <b>paint</b> operator, where both the geometry's extent and whether it is painted at all
        /// are finally known.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Containment, not intersection.</b> A shape is removed only when it lies wholly inside
        /// a redaction rectangle. Intersection would delete a table rule crossing the region, or a
        /// page border passing through it — geometry that mostly lives outside the area the caller
        /// named. Under-removal here is safe because the opaque cover box is still painted on top;
        /// over-removal silently damages the rest of the page.
        /// </para>
        /// <para>
        /// <b>A clip is never dropped.</b> <c>W</c>/<c>W*</c> narrow the clipping path for every
        /// operator that follows, so removing one changes content the caller never asked about —
        /// and it would <i>widen</i> what later operators paint, which is the opposite of redaction.
        /// A buffered sequence carrying a clip is emitted verbatim regardless of its extent.
        /// </para>
        /// <para>
        /// <b>Vector geometry carries no extractable text or image data</b>, so this is about the
        /// visual result rather than about a leak: the cover box already hides it. What it buys is
        /// a redacted file that does not still contain the shape of what was removed — a signature
        /// box, a chart bar, a highlight rule.
        /// </para>
        /// </remarks>
        private sealed class PathBuffer
        {
            private readonly List<ContentStreamOperation> _ops = new List<ContentStreamOperation>();
            private bool _hasClip;
            private bool _any;
            private float _minX, _minY, _maxX, _maxY;

            internal void MarkClip() => _hasClip = true;

            /// <summary>Buffers one path operator and grows the device-space bounding box.</summary>
            internal void Append(ContentStreamOperation op, float[] ctm)
            {
                _ops.Add(op);
                switch (op.Operator)
                {
                    case PdfOperators.Rectangle when op.Operands.Length >= 4:
                    {
                        float x = F(op, 0), y = F(op, 1), rw = F(op, 2), rh = F(op, 3);
                        Grow(x, y, ctm); Grow(x + rw, y, ctm);
                        Grow(x + rw, y + rh, ctm); Grow(x, y + rh, ctm);
                        break;
                    }
                    default:
                        // m/l take one point; c takes three; v/y take two. Every operand pair is a
                        // point in user space, so consuming them pairwise covers all of them —
                        // including the Bezier CONTROL points, which is deliberate: the control
                        // hull contains the curve, so the box can only be too large, never too
                        // small. Too large means "not contained", which means "kept".
                        for (int i = 0; i + 1 < op.Operands.Length; i += 2)
                            Grow(F(op, i), F(op, i + 1), ctm);
                        break;
                }
            }

            private void Grow(float x, float y, float[] ctm)
            {
                float dx = ctm[0] * x + ctm[2] * y + ctm[4];
                float dy = ctm[1] * x + ctm[3] * y + ctm[5];
                if (!_any) { _minX = _maxX = dx; _minY = _maxY = dy; _any = true; return; }
                if (dx < _minX) _minX = dx; else if (dx > _maxX) _maxX = dx;
                if (dy < _minY) _minY = dy; else if (dy > _maxY) _maxY = dy;
            }

            /// <summary>Whether the buffered path, grown by <paramref name="reach"/>, meets a region.</summary>
            internal bool Meets(List<float[]> rects, float reach)
                => _any && AabbHitsAny(_minX - reach, _minY - reach, _maxX + reach, _maxY + reach, rects);

            /// <summary>
            /// Emits the buffered path plus its paint operator, or drops the whole sequence when the
            /// geometry is wholly inside a redaction rectangle. Returns true when something was
            /// removed.
            /// </summary>
            internal bool FlushPaint(ContentStreamOperation paintOp, PdfWriter w, List<float[]> rects)
            {
                bool drop = !_hasClip
                            && _any
                            // `n` paints nothing, so dropping it removes no ink and could only
                            // discard a pending clip. Never treated as removable.
                            && paintOp.Operator != PdfOperators.EndPath
                            && IsWhollyInsideAny(_minX, _minY, _maxX, _maxY, rects);

                if (!drop)
                {
                    foreach (var o in _ops) Verbatim(w, o);
                    Verbatim(w, paintOp);
                }
                Reset();
                return drop;
            }

            /// <summary>
            /// Emits a buffered sequence that never reached a paint operator, unchanged. Called
            /// before any unrelated operator and at end of stream, so buffering can never reorder
            /// or swallow content.
            /// </summary>
            internal void FlushUnpainted(PdfWriter w)
            {
                if (_ops.Count == 0) return;
                foreach (var o in _ops) Verbatim(w, o);
                Reset();
            }

            private void Reset()
            {
                _ops.Clear();
                _hasClip = false;
                _any = false;
            }
        }

        /// <summary>
        /// True when the whole box lies inside one redaction rectangle. Deliberately NOT the
        /// inflated test <see cref="AabbHitsAny"/> uses: that margin biases toward removal, which is
        /// right for a glyph that might leak and wrong for geometry that might belong to the rest of
        /// the page.
        /// </summary>
        private static bool IsWhollyInsideAny(
            float minX, float minY, float maxX, float maxY, List<float[]> rects)
        {
            foreach (var r in AreaIndex.Candidates(rects, minX, minY, maxX, maxY))
            {
                AreaTests++;
                if (minX >= r[0] && maxX <= r[2] && minY >= r[1] && maxY <= r[3]) return true;
            }
            return false;
        }
        // Runs the walker into a throwaway buffer; used to produce redacted Form XObject content.
        private static (byte[] bytes, bool removed) RedactToBytes(
            byte[] content, PdfDictionary resources, PdfDocument doc, List<float[]> rects,
            FontCache fontCache, float[] ctm, int depth, FormCloneContext forms, RState drawnFrom)
        {
            using var ms = new MemoryStream();
            var w = new PdfWriter(ms);
            bool removed = WalkContent(content, resources, doc, rects, fontCache, w, ctm, depth, forms, drawnFrom);
            return (ms.ToArray(), removed);
        }

        // ---------------------------------------------------------------
        //  Core content-stream walker. Always emits the (redacted) stream to <paramref name="w"/>
        //  and returns whether any redactable content was removed.
        // ---------------------------------------------------------------

        private static bool WalkContent(
            byte[] content, PdfDictionary resources, PdfDocument doc,
            List<float[]> rects, FontCache fontCache, PdfWriter w,
            float[] initialCtm, int depth, FormCloneContext forms, RState drawnFrom = null)
        {
            if (depth > MaxFormDepth)
                throw new PdfRedactionException("Form XObject nesting too deep to redact safely.");
            if (!forms.TakeWalk())
                throw new PdfRedactionException(
                    $"Form XObjects on this page are drawn more than {PdfLimits.MaxFormXObjectInvocationsPerPage} " +
                    "times in total; too many to redact safely.");

            var ops = ContentStreamParser.Parse(content);
            // A form starts in the state of the stream that draws it: its text needs no Tf of its own.
            var gs = drawnFrom != null ? drawnFrom.Clone() : new RState(initialCtm);
            gs.CTM = (float[])initialCtm.Clone();
            var stack = new Stack<RState>();
            var fontInfo = new Dictionary<string, FontInfo>(StringComparer.Ordinal);
            var path = new PathBuffer();
            bool removedAny = false;
            bool inText = false;
            int markedContent = 0;

            foreach (var op in ops)
            {
                switch (op.Operator)
                {
                    case PdfOperators.Save: stack.Push(gs.Clone()); Verbatim(w, op); break;
                    case PdfOperators.Restore:
                        if (stack.Count > 0)
                        {
                            // §9.3.1: the text matrices are not graphics state, so Q leaves them where
                            // the text has reached.
                            float[] tm = gs.Tm, tlm = gs.Tlm;
                            gs = stack.Pop();
                            gs.Tm = tm;
                            gs.Tlm = tlm;
                            Verbatim(w, op);
                        }
                        // On a page a Q with nothing to restore does nothing, and left in it would
                        // restore the state the cover boxes are drawn in.
                        else if (depth > 0) Verbatim(w, op);
                        break;
                    case PdfOperators.ConcatMatrix:
                        if (op.Operands.Length >= 6)
                            gs.CTM = Compose(new[] { F(op, 0), F(op, 1), F(op, 2), F(op, 3), F(op, 4), F(op, 5) }, gs.CTM);
                        Verbatim(w, op);
                        break;

                    case PdfOperators.BeginText: inText = true; gs.Tm = Identity(); gs.Tlm = Identity(); Verbatim(w, op); break;
                    case PdfOperators.EndText: inText = false; Verbatim(w, op); break;
                    case PdfOperators.SetFont:
                        if (op.Operands.Length >= 2) { gs.FontName = (op.Operands[0] as PdfName)?.Value; gs.FontSize = F(op, 1); }
                        Verbatim(w, op);
                        break;
                    case PdfOperators.SetCharSpacing: gs.Tc = F(op, 0); Verbatim(w, op); break;
                    case PdfOperators.SetWordSpacing: gs.Tw = F(op, 0); Verbatim(w, op); break;
                    case PdfOperators.SetHorizontalScaling: gs.Th = F(op, 0) / 100f; Verbatim(w, op); break;
                    case PdfOperators.SetLeading: gs.TL = F(op, 0); Verbatim(w, op); break;
                    case PdfOperators.SetTextRise: gs.Ts = F(op, 0); Verbatim(w, op); break;
                    case PdfOperators.SetTextRenderMode: gs.RenderMode = SaturatingCast.ToInt(F(op, 0)); Verbatim(w, op); break;

                    case PdfOperators.TextMove:
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, F(op, 0), F(op, 1) }, gs.Tlm); gs.Tm = (float[])gs.Tlm.Clone();
                        Verbatim(w, op); break;
                    case PdfOperators.TextMoveSetLeading:
                        gs.TL = -F(op, 1);
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, F(op, 0), F(op, 1) }, gs.Tlm); gs.Tm = (float[])gs.Tlm.Clone();
                        Verbatim(w, op); break;
                    case PdfOperators.SetTextMatrix:
                        if (op.Operands.Length >= 6)
                        { gs.Tm = new[] { F(op, 0), F(op, 1), F(op, 2), F(op, 3), F(op, 4), F(op, 5) }; gs.Tlm = (float[])gs.Tm.Clone(); }
                        Verbatim(w, op); break;
                    case PdfOperators.NextLine:
                        gs.Tlm = Compose(new[] { 1f, 0, 0, 1, 0, -gs.TL }, gs.Tlm); gs.Tm = (float[])gs.Tlm.Clone();
                        Verbatim(w, op); break;

                    case PdfOperators.ShowText:
                        removedAny |= EmitShow(w, op, gs, resources, doc, fontCache, fontInfo, forms, rects,
                                 GetStringSegs(op, 0), leadMove: false, setSpacing: false, 0, 0);
                        break;
                    case PdfOperators.ShowTextArray:
                        removedAny |= EmitShow(w, op, gs, resources, doc, fontCache, fontInfo, forms, rects,
                                 GetArraySegs(op), leadMove: false, setSpacing: false, 0, 0);
                        break;
                    case PdfOperators.NextLineShowText:
                        removedAny |= EmitShow(w, op, gs, resources, doc, fontCache, fontInfo, forms, rects,
                                 GetStringSegs(op, 0), leadMove: true, setSpacing: false, 0, 0);
                        break;
                    case PdfOperators.NextLineSetSpacingShowText:
                        // §9.4.3 Table 109: `aw ac string "`. Below three operands every reader in
                        // this library leaves Tm, Tlm, Tw and Tc untouched, and this engine did
                        // not — `F` returns 0 for a missing operand, so a one-operand `"` zeroed
                        // the spacing and moved the line origin down one leading.
                        //
                        // WORSE HERE THAN IN THE TEMPLATE ENGINE, because this one WRITES. On the
                        // rebuild path EmitShow emits `Tw`, `Tc` and a literal `T*` derived from
                        // that phantom state, so the shift is baked into the output: a reader that
                        // would have ignored the malformed operator now sees a real `T*`, and every
                        // later line of the redacted document is one leading lower than in the
                        // original.
                        // Dropped, since no reader does anything with it and its string stays otherwise.
                        if (op.Operands.Length < 3) break;

                        removedAny |= EmitShow(w, op, gs, resources, doc, fontCache, fontInfo, forms, rects,
                                 GetStringSegs(op, 2), leadMove: true, setSpacing: true, F(op, 0), F(op, 1));
                        break;

                    case PdfOperators.InvokeXObject:
                        removedAny |= HandleDo(op, resources, doc, rects, fontCache, w, gs, depth, forms);
                        break;

                    case PdfOperators.BeginInlineImage:
                        removedAny |= HandleInlineImage(op, rects, w, gs);
                        break;

                    // ---- Path construction: buffered, not emitted yet ----
                    // The decision belongs at the PAINT operator, because that is the first moment
                    // the geometry's extent and whether it is painted at all are both known.
                    case PdfOperators.MoveTo:
                    case PdfOperators.LineTo:
                    case PdfOperators.CurveTo:
                    case PdfOperators.CurveToV:
                    case PdfOperators.CurveToY:
                    case PdfOperators.ClosePath:
                    case PdfOperators.Rectangle:
                        path.Append(op, gs.CTM);
                        break;

                    // ---- Clip: NEVER dropped ----
                    // W/W* narrow the clip for everything that follows, so removing one changes
                    // content the caller never asked about. A path carrying a clip is flushed
                    // verbatim and excluded from removal, whatever its extent.
                    case PdfOperators.Clip:
                    case PdfOperators.ClipEvenOdd:
                        path.MarkClip();
                        path.Append(op, gs.CTM);
                        break;

                    // ---- Paint: decide here ----
                    case PdfOperators.Fill:
                    case PdfOperators.FillObsolete:
                    case PdfOperators.FillEvenOdd:
                    case PdfOperators.Stroke:
                    case PdfOperators.CloseStroke:
                    case PdfOperators.FillStroke:
                    case PdfOperators.FillStrokeEvenOdd:
                    case PdfOperators.CloseFillStroke:
                    case PdfOperators.CloseFillStrokeEvenOdd:
                    case PdfOperators.EndPath:
                    {
                        bool fills = PaintsFill(op.Operator), strokes = PaintsStroke(op.Operator);
                        if ((fills || strokes) && path.Meets(rects, strokes ? StrokeReach(gs) : 0f))
                            RefuseCoveredImageSource(gs, fills, strokes);
                        removedAny |= path.FlushPaint(op, w, rects);
                        break;
                    }

                    default:
                        // Any other operator ends the buffering window: a path that never reached
                        // a paint operator is malformed, and holding it across unrelated operators
                        // would reorder the stream. Flush it unchanged first.
                        path.FlushUnpainted(w);
                        if (op.Operator == PdfOperators.SetLineWidth) gs.LineWidth = F(op, 0);
                        else if (op.Operator == PdfOperators.SetMiterLimit) gs.MiterLimit = F(op, 0);
                        else if (op.Operator == PdfOperators.SetExtGState) ReadExtGState(op, gs, resources, doc);
                        else if (op.Operator == PdfOperators.BeginMarkedContent || op.Operator == PdfOperators.BeginMarkedContentProps) markedContent++;
                        else if (op.Operator == PdfOperators.EndMarkedContent && markedContent > 0) markedContent--;
                        // A shading paints the whole clip, which is not followed here.
                        else if (op.Operator == PdfOperators.ShadingFill) RefuseCoveredImageSource(gs, false, false);
                        WriteOtherOperator(w, op, resources, doc, forms, gs);
                        break;
                }
            }

            // A content stream that ends mid-path: emit what was buffered rather than losing it.
            path.FlushUnpainted(w);
            if (depth == 0) CloseWhatThePageLeftOpen(w, inText, markedContent, stack.Count);

            // The text matrices belong to no saved state, so what a form shows moves the text of the stream drawing it.
            if (drawnFrom != null) { drawnFrom.Tm = gs.Tm; drawnFrom.Tlm = gs.Tlm; }
            return removedAny;
        }

        /// <summary>
        /// Ends a page's text object, path, marked content and saved states, so what follows starts
        /// where the page started.
        /// </summary>
        private static void CloseWhatThePageLeftOpen(PdfWriter w, bool inText, int markedContent, int saved)
        {
            if (inText) w.WriteRaw(PdfOperators.EndText + "\n");
            w.WriteRaw(PdfOperators.EndPath + "\n");
            for (int i = 0; i < markedContent; i++) w.WriteRaw(PdfOperators.EndMarkedContent + "\n");
            for (int i = 0; i < saved; i++) w.WriteRaw(PdfOperators.Restore + "\n");
        }

        /// <summary>What a graphics state dictionary sets that moves or widens text (§8.4.5): the font, its size, the line width and miter limit.</summary>
        private static void ReadExtGState(ContentStreamOperation op, RState gs, PdfDictionary resources, PdfDocument doc)
        {
            if (op.Operands.Length < 1 || !(op.Operands[0] is PdfName name)
                || !(doc.ResolveObject(resources?[PdfName.ExtGState]) is PdfDictionary states)
                || !(doc.ResolveObject(states[name]) is PdfDictionary state)) return;

            if (ExtGStateFont.TryRead(doc, state, resources, "", out string fontName, out float? fontSize))
            {
                if (fontName != null) gs.FontName = fontName;
                if (fontSize != null) gs.FontSize = fontSize.Value;
            }

            if (PdfResolve.Number(doc, state, PdfName.LW) is PdfNumber width) gs.LineWidth = (float)width.DoubleValue;
            if (PdfResolve.Number(doc, state, PdfName.ML) is PdfNumber miter) gs.MiterLimit = (float)miter.DoubleValue;
        }

        /// <summary>The operators ISO 32000 defines. Anything else in a content stream is dropped from a redacted one.</summary>
        private static readonly HashSet<string> DefinedOperators = DefinedOperatorNames();

        private static HashSet<string> DefinedOperatorNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in typeof(PdfOperators).GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (field.IsLiteral && field.GetRawConstantValue() is string name) names.Add(name);
            return names;
        }

        /// <summary>
        /// An operator with no region to test, written as it stands — its text carried no further. A
        /// marked-content operator's properties may say what the removed text was, and an operator no
        /// reader defines, under <c>BX</c>, can carry anything at all.
        /// </summary>
        private static void WriteOtherOperator(PdfWriter w, ContentStreamOperation op, PdfDictionary resources,
                                               PdfDocument doc, FormCloneContext forms, RState gs)
        {
            switch (op.Operator)
            {
                case PdfOperators.BeginMarkedContentProps:
                case PdfOperators.MarkedPointProps:
                    if (w != null) ContentOperationWriter.Write(w, ScrubbedMarkedContent(op, resources, doc));
                    return;
                case PdfOperators.SetColorFillN:
                case PdfOperators.SetColorStrokeN:
                {
                    string throughImage = PatternThroughImage(op, resources, doc, forms, 0);
                    if (op.Operator == PdfOperators.SetColorFillN) gs.FillImageSource = throughImage;
                    else gs.StrokeImageSource = throughImage;
                    break;
                }
                case PdfOperators.SetColorSpaceFill:
                case PdfOperators.SetGrayFill:
                case PdfOperators.SetRgbFill:
                case PdfOperators.SetCmykFill:
                case PdfOperators.SetColorFill:
                    gs.FillImageSource = null;
                    break;
                case PdfOperators.SetColorSpaceStroke:
                case PdfOperators.SetGrayStroke:
                case PdfOperators.SetRgbStroke:
                case PdfOperators.SetCmykStroke:
                case PdfOperators.SetColorStroke:
                    gs.StrokeImageSource = null;
                    break;
                case PdfOperators.SetExtGState:
                    if (op.Operands.Length > 0 && op.Operands[0] is PdfName state
                        && doc.ResolveObject(resources?[PdfName.ExtGState]) is PdfDictionary states
                        && doc.ResolveObject(states[state]) is PdfDictionary gsDict
                        && gsDict[PdfName.SMask] != null)
                    {
                        // §11.6.5.1: a new /SMask replaces the one in force, /None removing it.
                        gs.MaskImageSource = doc.ResolveObject(gsDict[PdfName.SMask]) is PdfDictionary softMask
                            && doc.ResolveObject(softMask[PdfName.G]) is PdfStream group
                            && PaintSourceDrawsImage(group, resources, doc, forms, "soft mask in /" + state.Value, 0)
                            ? "soft mask in /" + state.Value : null;
                    }
                    break;
            }

            if (DefinedOperators.Contains(op.Operator)) Verbatim(w, op);
        }

        /// <summary>
        /// The tiling pattern an <c>scn</c> or <c>SCN</c> selects when its cell draws an image, as a
        /// name for a refusal, or <see langword="null"/>; a cell that draws text is refused.
        /// </summary>
        private static string PatternThroughImage(ContentStreamOperation op, PdfDictionary resources, PdfDocument doc,
                                                  FormCloneContext forms, int depth)
        {
            if (op.Operands.Length == 0 || !(op.Operands[op.Operands.Length - 1] is PdfName pattern)) return null;
            if (!(doc.ResolveObject(doc.ResolveObject(resources?[PdfName.Pattern]) is PdfDictionary patterns ? patterns[pattern] : null) is PdfStream cell))
                return null;
            string what = "tiling pattern /" + pattern.Value;
            return PaintSourceDrawsImage(cell, resources, doc, forms, what, depth) ? what : null;
        }

        /// <summary>
        /// Refuses a pattern's cell or a soft mask's group that draws text, itself or through a form, a
        /// pattern or a mask it draws: one stream painted wherever the page says, so no region can be
        /// placed on it, and the glyph test has nothing to test. Answers whether it draws an image the
        /// same way, which a paint meeting a region then refuses (<see cref="RefuseCoveredImageSource"/>):
        /// the region would cover the image's pixels, and the file keep them.
        /// </summary>
        private static bool PaintSourceDrawsImage(PdfStream source, PdfDictionary inherited, PdfDocument doc,
                                                  FormCloneContext forms, string what, int depth)
        {
            if (forms.TryPaintSource(source, out bool known)) return known;
            // While it is read: a source that draws itself adds nothing to what it draws.
            forms.RecordPaintSource(source, false);
            if (depth > MaxFormDepth)
                throw new PdfRedactionException("A " + what + " nests forms too deep to redact safely.");
            if (!source.TryGetDecodedData(PdfLimits.MaxDecompressedBytes, out byte[] content, out string refused))
                throw new PdfRedactionException("A " + what + " could not be read, so it cannot be shown free of text: " + refused);

            var resources = doc.ResolveObject(source[PdfName.Resources]) as PdfDictionary ?? inherited;
            bool image = false;
            foreach (var op in ContentStreamParser.Parse(content ?? Array.Empty<byte>()))
            {
                switch (op.Operator)
                {
                    case PdfOperators.ShowText:
                    case PdfOperators.ShowTextArray:
                    case PdfOperators.NextLineShowText:
                    case PdfOperators.NextLineSetSpacingShowText:
                        throw new PdfRedactionException(
                            "This page paints through a " + what + " that draws text, which a region cannot be placed on. " +
                            "Flatten the page, or remove the pattern or mask, before redacting.");
                    case PdfOperators.BeginInlineImage:
                        image = true;
                        break;
                    case PdfOperators.InvokeXObject:
                        if (op.Operands.Length > 0 && op.Operands[0] is PdfName name
                            && ResolveXObject(name.Value, resources, doc) is PdfStream xobject)
                        {
                            string subtype = (doc.ResolveObject(xobject[PdfName.Subtype]) as PdfName)?.Value;
                            if (subtype == "Image") image = true;
                            else if (subtype == "Form") image |= PaintSourceDrawsImage(xobject, resources, doc, forms, what, depth + 1);
                        }
                        break;
                    case PdfOperators.SetColorFillN:
                    case PdfOperators.SetColorStrokeN:
                        image |= PatternThroughImage(op, resources, doc, forms, depth + 1) != null;
                        break;
                    case PdfOperators.SetExtGState:
                        if (op.Operands.Length > 0 && op.Operands[0] is PdfName state
                            && doc.ResolveObject(resources?[PdfName.ExtGState]) is PdfDictionary states
                            && doc.ResolveObject(states[state]) is PdfDictionary gsDict
                            && doc.ResolveObject(gsDict[PdfName.SMask]) is PdfDictionary softMask
                            && doc.ResolveObject(softMask[PdfName.G]) is PdfStream group)
                            image |= PaintSourceDrawsImage(group, resources, doc, forms, what, depth + 1);
                        break;
                }
            }
            forms.RecordPaintSource(source, image);
            return image;
        }

        /// <summary>
        /// Refuses a paint that meets a region while it paints through an image the region could only
        /// cover — a tiling pattern's cell or a soft mask's group that draws one: the cover box would
        /// hide the pixels and the file keep them. <paramref name="fills"/> and <paramref name="strokes"/>
        /// say which colour the paint uses; the soft mask is in force for every paint.
        /// </summary>
        private static void RefuseCoveredImageSource(RState gs, bool fills, bool strokes)
        {
            string what = (fills ? gs.FillImageSource : null) ?? (strokes ? gs.StrokeImageSource : null) ?? gs.MaskImageSource;
            if (what == null) return;
            throw new PdfRedactionException(
                "A region meets what this page paints through a " + what + " that draws an image: the region would cover the " +
                "image's pixels and the file would keep them. Flatten the page, or remove the pattern or mask, before redacting.");
        }

        /// <summary>Whether a path-painting operator fills (§8.5.3.1, Table 59).</summary>
        private static bool PaintsFill(string op)
            => op == PdfOperators.Fill || op == PdfOperators.FillObsolete || op == PdfOperators.FillEvenOdd
            || op == PdfOperators.FillStroke || op == PdfOperators.FillStrokeEvenOdd
            || op == PdfOperators.CloseFillStroke || op == PdfOperators.CloseFillStrokeEvenOdd;

        /// <summary>Whether a path-painting operator strokes (§8.5.3.1, Table 59).</summary>
        private static bool PaintsStroke(string op)
            => op == PdfOperators.Stroke || op == PdfOperators.CloseStroke
            || op == PdfOperators.FillStroke || op == PdfOperators.FillStrokeEvenOdd
            || op == PdfOperators.CloseFillStroke || op == PdfOperators.CloseFillStrokeEvenOdd;

        /// <summary>Text render modes 0, 2, 4 and 6 fill the glyph (§9.3.6).</summary>
        private static bool TextFills(int mode) => mode == 0 || mode == 2 || mode == 4 || mode == 6;

        /// <summary>
        /// A <c>BDC</c> or <c>DP</c> whose properties keep only what says nothing: direct numbers, names,
        /// booleans and arrays of numbers (<c>/MCID</c>, an artifact's <c>/Type</c> and <c>/BBox</c>).
        /// Optional content is a reference to its group and is left as it is. Properties named from the
        /// resources are cleaned where they stand, since the name stays in the stream and the dictionary in
        /// the file — on every page that names them, which is the side to err on.
        /// </summary>
        private static ContentStreamOperation ScrubbedMarkedContent(ContentStreamOperation op, PdfDictionary resources, PdfDocument doc)
        {
            if (op.Operands.Length < 2 || (op.Operands[0] as PdfName)?.Value == "OC") return op;

            if (op.Operands[1] is PdfName named)
            {
                if (doc.ResolveObject(resources?[PdfName.Properties]) is PdfDictionary properties
                    && doc.ResolveObject(properties[named]) is PdfDictionary shared)
                    foreach (string key in KeysThatCanSpeak(shared)) shared.Remove(new PdfName(key));
                return op;
            }

            if (!(op.Operands[1] is PdfDictionary inline)) return new ContentStreamOperation(op.Operator, new[] { op.Operands[0], new PdfDictionary() });

            var kept = new PdfDictionary();
            var speaking = new HashSet<string>(KeysThatCanSpeak(inline), StringComparer.Ordinal);
            foreach (var entry in inline)
                if (!speaking.Contains(entry.Key.Value)) kept.Set(entry.Key, entry.Value);
            return new ContentStreamOperation(op.Operator, new PdfObject[] { op.Operands[0], kept });
        }

        private static List<string> KeysThatCanSpeak(PdfDictionary properties)
        {
            var keys = new List<string>();
            foreach (var entry in properties)
                if (!SaysNothing(entry.Value)) keys.Add(entry.Key.Value);
            return keys;
        }

        private static bool SaysNothing(PdfObject value)
        {
            if (value is PdfNumber || value is PdfName || value is PdfBoolean) return true;
            if (!(value is PdfArray array)) return false;
            foreach (PdfObject item in array) if (!(item is PdfNumber)) return false;
            return true;
        }

        // ---------------------------------------------------------------
        //  Show operators (Tj / TJ / ' / ")
        // ---------------------------------------------------------------

        private static bool EmitShow(
            PdfWriter w, ContentStreamOperation op, RState gs,
            PdfDictionary resources, PdfDocument doc, FontCache fontCache,
            Dictionary<string, FontInfo> fontInfo, FormCloneContext forms, List<float[]> rects,
            List<object> segs, bool leadMove, bool setSpacing, float aw, float ac)
        {
            if (setSpacing) { gs.Tw = aw; gs.Tc = ac; }
            if (leadMove)
            {
                gs.Tlm = Compose(new[] { 1f, 0, 0, 1, 0, -gs.TL }, gs.Tlm);
                gs.Tm = (float[])gs.Tlm.Clone();
            }

            var fi = GetFont(gs.FontName, resources, doc, fontCache, fontInfo, forms);
            var (removed, elems) = BuildRedactedTj(segs, gs, fi, rects);

            if (!removed)
            {
                // Nothing redacted: replay the original operator verbatim (state already advanced).
                Verbatim(w, op);
                return false;
            }

            // Rebuild as positioning + TJ.
            if (setSpacing)
                w.WriteRaw($"{Fmt(aw)} {PdfOperators.SetWordSpacing} {Fmt(ac)} {PdfOperators.SetCharSpacing} ");
            if (leadMove) w.WriteRaw(PdfOperators.NextLine + " ");
            w.WriteRaw("[");
            for (int i = 0; i < elems.Count; i++) { if (i > 0) w.WriteRaw(" "); w.WriteRaw(elems[i]); }
            w.WriteRaw("] " + PdfOperators.ShowTextArray + "\n");
            return true;
        }

        private static (bool removed, List<string> elems) BuildRedactedTj(
            List<object> segs, RState gs, FontInfo fi, List<float[]> rects)
        {
            bool any = false;
            float Tfs = gs.FontSize, Th = gs.Th;
            bool vertical = fi.Vertical != null;

            // The accumulator is ShowTextArrayBuilder, shared with PdfTemplateEngine — the two
            // carried identical flush pairs. This engine keeps its own, higher number precision:
            // a mis-placed kern here could shift surviving text over content the caller asked to
            // destroy, so it is the one that should round least. Down a vertical run a positive
            // TJ number moves the pen on and Th does not apply, hence the factor of -1 there.
            var builder = new ShowTextArrayBuilder(Tfs, vertical ? -1f : Th, RedactionNumberFormat);

            foreach (var seg in segs)
            {
                if (seg is float kern)
                {
                    builder.Kern(kern);
                    gs.Tm = Compose(TextAdjustment.Of(kern, Tfs, Th, vertical), gs.Tm);
                    continue;
                }

                var bytes = (byte[])seg;

                if (fi.Type3 != null)
                {
                    // One byte a glyph, as both renderers read a Type 3 string.
                    for (int b = 0; b < bytes.Length; b++)
                    {
                        RedactionType3Font.Glyph glyph = fi.Type3.GlyphOf(bytes[b]);
                        float type3Advance = Type3Encoding.AdvanceTx(glyph.Width, bytes[b], Tfs, gs.Tc, gs.Tw, Th);
                        if (Type3GlyphIntersects(gs, fi.Type3, glyph, rects))
                        {
                            RefuseCoveredText(gs);
                            builder.Drop(type3Advance);
                            any = true;
                        }
                        else builder.Keep(bytes, b, 1);
                        gs.Tm = Compose(new[] { 1f, 0, 0, 1, type3Advance, 0 }, gs.Tm);
                    }
                    continue;
                }

                var codes = fi.SplitCodes != null
                    ? fi.SplitCodes(bytes)
                    : (IReadOnlyList<CMapCodec.Code>)Array.Empty<CMapCodec.Code>();

                int i = 0;
                foreach (var c in codes)
                {
                    int len = Math.Max(1, Math.Min(c.ByteLength, bytes.Length - i));

                    // Keyed on the CID, not the raw code: a composite font's /W array is indexed by
                    // CID (§9.7.4.3), and CID == code only under Identity-H. The width is the one the
                    // renderers advance by, so the glyphs after this one are tested where they land.
                    float wem = (float)GlyphWidth.TextSpace(fi.Font, c.Cid);

                    float[] glyphTm = gs.Tm;
                    float advance;
                    if (vertical)
                    {
                        // §9.7.4.3: drawn at the pen less the position vector, the pen then moving down
                        // by the one step rule (the spacings shorten it).
                        VerticalGlyphMetrics metrics = fi.Vertical(c.Cid);
                        glyphTm = Compose(new[] { 1f, 0, 0, 1, -metrics.PositionXEm * Tfs, -metrics.PositionYEm * Tfs }, gs.Tm);
                        advance = metrics.Step(Tfs, gs.Tc, gs.Tw, c.IsWordSpaceCode);
                    }
                    else
                    {
                        // §9.3.3: word spacing applies to the SINGLE-BYTE code 32 only — never to a
                        // 2-byte code whose low byte happens to be 32. The rule lives on CMapCodec.Code.
                        advance = wem * Tfs * Th + (gs.Tc + (c.IsWordSpaceCode ? gs.Tw : 0f)) * Th;
                    }

                    if (GlyphIntersects(gs, glyphTm, wem, rects))
                    {
                        RefuseCoveredText(gs);
                        builder.Drop(advance);
                        any = true;
                    }
                    else
                    {
                        // The code's bytes travel together: keeping half of a 2-byte code would
                        // leave the survivor decoding to a character the author never wrote.
                        builder.Keep(bytes, i, len);
                    }

                    gs.Tm = Compose(vertical ? new[] { 1f, 0, 0, 1, 0, -advance } : new[] { 1f, 0, 0, 1, advance, 0 }, gs.Tm);
                    i += len;
                }
            }

            return (any, new List<string>(builder.Build()));
        }

        // Glyph box (em space [0,wem]x[-0.25,0.95]) through Trm, AABB vs rects (inflated). A glyph with
        // no advance, or a backward one, still draws forward from its origin: the box keeps half an em
        // of room for it. Text that strokes reaches as far as a miter join does.
        private static bool GlyphIntersects(RState gs, float[] tm, float wem, List<float[]> rects)
        {
            float[] trm = Compose(Compose(new[] { gs.FontSize * gs.Th, 0, 0, gs.FontSize, 0, gs.Ts }, tm), gs.CTM);
            float right = wem > 0 ? wem : 0.5f;
            float[] gx = { 0f, right, right, 0f };
            float[] gy = { -0.25f, -0.25f, 0.95f, 0.95f };
            float[] box = CornersAabb(trm, gx, gy);
            float reach = TextStrokes(gs.RenderMode) ? StrokeReach(gs) : 0f;
            return AabbHitsAny(box[0] - reach, box[1] - reach, box[2] + reach, box[3] + reach, rects);
        }

        /// <summary>Text render modes 1, 2, 5 and 6 stroke the glyph (§9.3.6).</summary>
        private static bool TextStrokes(int mode) => mode == 1 || mode == 2 || mode == 5 || mode == 6;

        /// <summary><see cref="RefuseCoveredImageSource"/> for a glyph a region meets, as its render mode paints it: invisible text paints nothing.</summary>
        private static void RefuseCoveredText(RState gs)
        {
            bool fills = TextFills(gs.RenderMode), strokes = TextStrokes(gs.RenderMode);
            if (fills || strokes) RefuseCoveredImageSource(gs, fills, strokes);
        }

        private static float StrokeReach(RState gs)
            => Math.Abs(gs.LineWidth) * Math.Max(1f, Math.Abs(gs.MiterLimit)) / 2f
               * Math.Max(Math.Abs(gs.CTM[0]) + Math.Abs(gs.CTM[2]), Math.Abs(gs.CTM[1]) + Math.Abs(gs.CTM[3]));

        /// <summary>
        /// A Type 3 glyph against the regions: where its procedure paints, through the font matrix as
        /// the renderers run it, and the box its advance names, which is where the text is taken to
        /// be even when the procedure paints elsewhere or nothing.
        /// </summary>
        private static bool Type3GlyphIntersects(RState gs, RedactionType3Font font, RedactionType3Font.Glyph glyph, List<float[]> rects)
        {
            if (glyph.PaintsAnywhere || GlyphIntersects(gs, gs.Tm, glyph.Width, rects)) return true;
            if (!glyph.Paints) return false;

            float[] toPage = Compose(font.FontMatrix, Compose(Compose(new[] { gs.FontSize * gs.Th, 0, 0, gs.FontSize, 0, gs.Ts }, gs.Tm), gs.CTM));
            float[] box = CornersAabb(toPage,
                new[] { glyph.MinX, glyph.MaxX, glyph.MaxX, glyph.MinX },
                new[] { glyph.MinY, glyph.MinY, glyph.MaxY, glyph.MaxY });
            return AabbHitsAny(box[0], box[1], box[2], box[3], rects);
        }

        // ---------------------------------------------------------------
        //  XObjects (images and forms) + inline images
        // ---------------------------------------------------------------

        // Returns true if the Do referenced content that was redacted (image stripped / form rewritten).
        private static bool HandleDo(
            ContentStreamOperation op, PdfDictionary resources, PdfDocument doc,
            List<float[]> rects, FontCache fontCache, PdfWriter w, RState gs, int depth,
            FormCloneContext forms)
        {
            string name = op.Operands.Length >= 1 ? (op.Operands[0] as PdfName)?.Value : null;
            var xobj = ResolveXObject(name, resources, doc);

            if (xobj == null)
            {
                // Unknown XObject: it still paints in the unit square under the CTM.
                if (UnitSquareHitsAny(gs.CTM, rects))
                    throw new PdfRedactionException($"Cannot resolve XObject '/{name}' overlapping a redaction region.");
                Verbatim(w, op);
                return false;
            }

            // Resolved. Neither branch below matches a null subtype and the fall-through is
            // Verbatim(w, op) — so an image XObject overlapping a redaction region was written out
            // UNREDACTED. This method throws rather than guess when it cannot resolve an XObject at
            // all (a few lines above); reading its /Subtype raw defeated that same fail-closed
            // intent through a legal §7.3.10 spelling that the document's author chooses.
            string subtype = (doc.ResolveObject(xobj[PdfName.Subtype]) as PdfName)?.Value;

            if (subtype == "Image")
            {
                if (UnitSquareHitsAny(gs.CTM, rects))
                {
                    // A stencil paints in the fill colour (§8.9.6.2); every image under the soft mask.
                    RefuseCoveredImageSource(gs, doc.ResolveObject(xobj[PdfName.ImageMask]) is PdfBoolean { Value: true }, false);
                    // An image the regions cover only in part keeps its uncovered pixels (batch
                    // 113): the covered ones are painted over in a clone drawn at this invocation.
                    // Where that cannot be done exactly — the image wholly covered, a stencil, a
                    // masked image, one this cannot decode, a matrix it cannot invert — the whole
                    // image goes, as every overlapping image did before.
                    string cloneName = TryRedactImagePixels(xobj, name, gs.CTM, rects, resources, doc, forms);
                    if (cloneName != null)
                    {
                        WriteDo(w, cloneName);
                        return true;
                    }

                    // Drop the Do AND empty the image stream so its pixel data is not written
                    // to the output at all (removing only the Do would leave the bytes reachable
                    // via the /XObject resources), and what it keeps beside them: its masks, which
                    // are its shape, and the entries that are another copy of it. Note: this blanks
                    // the image everywhere it is referenced.
                    PdfFullRewriter.StripStream(xobj, Array.Empty<byte>());
                    xobj.Remove(PdfName.SMask);
                    xobj.Remove(PdfName.Mask);
                    xobj.Remove(PdfName.Alternates);
                    foreach (PdfName derived in DerivedPageEntries) xobj.Remove(derived);
                    return true; // drop the Do
                }
                Verbatim(w, op);
                return false;
            }

            if (subtype == "Form")
            {
                float[] formMatrix = ReadMatrix(doc, doc.ResolveObject(xobj[PdfName.Matrix]));
                float[] inner = Compose(formMatrix, gs.CTM);
                bool bboxIntersects = doc.ResolveObject(xobj[PdfName.BBox]) is PdfArray bbox && bbox.Count >= 4
                    ? RectHitsAny(TransformBBox(doc, bbox, inner), rects)
                    : true; // no BBox: assume it could intersect → inspect

                if (bboxIntersects)
                {
                    byte[] formContent = forms.FormContent(xobj);
                    var formRes = doc.ResolveObject(xobj[PdfName.Resources]) as PdfDictionary ?? resources;

                    // Redact a copy of the form's content; if anything was actually removed we must
                    // substitute it — but only when the form is drawn exactly once in the whole
                    // document (a single, unambiguous draw). A shared or nested form stays
                    // fail-closed.
                    //
                    // The uniqueness test is TRANSITIVE: counting Do operators in page content only,
                    // a form invoked once from a page and once from inside another form would score 1
                    // and be rewritten — quietly changing the second place it appears, on a page the
                    // caller never named. See FormInvocationCounter.
                    var (redacted, removed) = RedactToBytes(formContent, formRes, doc, rects, fontCache, inner, depth + 1, forms, gs);
                    if (removed)
                    {
                        // ONE UNIFORM RULE: never rewrite a form in place — always substitute a
                        // clone at THIS invocation. Replaces the previous split (rewrite in place
                        // when uniquely drawn, throw otherwise), which failed closed on any shared
                        // or nested form.
                        //
                        // Why cloning is not weaker for a uniquely-drawn form: the rewriter
                        // emits reachable objects only, so once this Do names the clone the
                        // original form is unreferenced and is dropped from the output entirely.
                        // A uniquely-drawn form therefore leaves no residual bytes, exactly as the
                        // in-place strip did.
                        //
                        // What genuinely changes, and it is a REDUCTION in what redaction promises
                        // for shared forms: a form still drawn from a page the caller did NOT name
                        // keeps the original object, secret included, because that page was never
                        // redacted. The engine can no longer claim the bytes are gone from the
                        // file — only that they are gone from the pages it was asked to redact.
                        // FormCloneContext.SharedFormLeftIntact records every such form, and
                        // ReportSharedFormsLeftIntact carries them out on
                        // PdfRedactionEngine.LastSharedFormWarning, so the caller is told rather
                        // than left to assume.
                        string cloneName = forms.SubstituteClone(xobj, name, redacted, resources, doc);
                        WriteDo(w, cloneName);
                        return true;
                    }
                }
                Verbatim(w, op);
                return false;
            }

            // Unknown subtype: be conservative.
            if (UnitSquareHitsAny(gs.CTM, rects))
                throw new PdfRedactionException($"XObject '/{name}' of unknown subtype overlaps a redaction region.");
            Verbatim(w, op);
            return false;
        }

        /// <summary>
        /// Partial image redaction: the regions' footprint on <paramref name="image"/>,
        /// painted black in a clone substituted at this invocation — the name to draw — or
        /// <c>null</c> when the whole image has to go instead.
        /// </summary>
        /// <remarks>
        /// <para><b>Exact where it answers, and fail-closed everywhere else.</b> It declines — so the
        /// caller strips the whole image, as before — when the image's unit square lies wholly inside
        /// a region (nothing to keep), when the image is a stencil mask (its colour is the paint's where
        /// it is drawn, so no black box can be promised), when the raster decode
        /// fails or disagrees with the dictionary's size, and when the CTM cannot be inverted (the
        /// footprint cannot be placed).</para>
        /// <para><b>The footprint is the regions' bounding boxes in the image's own space</b>, each
        /// taken through the inverse CTM: exact for an axis-aligned or quarter-turned image, and for
        /// a skewed or rotated one the box of the region's corners — more pixels than the region,
        /// never fewer. Pixels are rounded outward (to <see cref="FootprintEpsilon"/> of a pixel), and
        /// the glyph tests' <see cref="IntersectMargin"/> is not applied: it would black a pixel
        /// column past a region that ends on a pixel edge.</para>
        /// <para><b>An image with an <c>/SMask</c> or <c>/Mask</c> keeps its transparency</b> as a new soft
        /// mask from the decoded alpha — the soft mask, the colour key and the stencil mask all reach
        /// it — opaque over the redacted pixels, so the black box shows and the mask's own shape
        /// there is gone too. The original's mask keys go with its pixels, so a mask nothing else draws
        /// drops out of the rewrite; the image is not removed whole.</para>
        /// <para><b>Written back as Flate samples</b> — grey when every kept pixel is grey, RGB
        /// otherwise — whatever the original's filter or colour space. <b>A bilevel result</b> (every
        /// pixel black or white; a soft mask, if any, beside it) is written at one bit, CCITT Group 4 where the
        /// encoder takes its size and Flate otherwise: a redacted scan stays a scan's size. The
        /// original goes the way a form's does
        /// (<see cref="FormCloneContext.SubstituteClone(PdfStream, string, PdfStream, PdfDictionary, PdfDocument, string)"/>):
        /// drawn once in the document, its name is repointed and the original stripped as well, so a
        /// reference this walk does not see (an appearance stream) finds no pixels; drawn elsewhere
        /// too, the clone takes a fresh name and the original stays for the pages not named, reported
        /// on <see cref="LastSharedFormWarning"/>.</para>
        /// </remarks>
        private static string TryRedactImagePixels(PdfStream image, string name, float[] ctm, List<float[]> rects,
                                                   PdfDictionary resources, PdfDocument doc, FormCloneContext forms)
        {
            float[] footprint = TransformUnitSquare(ctm);
            if (IsWhollyInsideAny(footprint[0], footprint[1], footprint[2], footprint[3], rects)) return null;
            if (doc.ResolveObject(image[PdfName.ImageMask]) is PdfBoolean mask && mask.Value) return null;
            bool masked = image[PdfName.SMask] != null || image[PdfName.Mask] != null;
            if (!TryInvert(ctm, out float[] inverse)) return null;

            int width = PdfResolve.Int(doc, image, PdfName.Width, PdfName.W, 0);
            int height = PdfResolve.Int(doc, image, PdfName.Height, PdfName.H, 0);
            if (width <= 0 || height <= 0 || (long)width * height > PdfLimits.MaxImagePixelArea) return null;

            using SKBitmap decoded = ContentStreamInterpreter.DecodeImageXObject(doc, image);
            if (decoded == null || decoded.Width != width || decoded.Height != height) return null;

            // The pixels as straight RGB, row-major from the top-left — and, for a masked image, the
            // alpha every mask kind left in the decoded bitmap.
            var rgb = new byte[(long)width * height * 3];
            byte[] alpha = masked ? new byte[(long)width * height] : null;
            BitmapPixels.Read(decoded, (row, y) =>
            {
                int o = y * width * 3;
                for (int x = 0; x < row.Length && x < width; x++, o += 3)
                {
                    rgb[o] = row[x].Red; rgb[o + 1] = row[x].Green; rgb[o + 2] = row[x].Blue;
                    if (alpha != null) alpha[y * width + x] = row[x].Alpha;
                }
            });

            bool painted = false;
            foreach (float[] r in rects)
            {
                // The region through the inverse CTM into unit space: u along the image's columns,
                // v up its rows. Not inflated by IntersectMargin — that margin absorbs a glyph's
                // advance drift, and here it would black a whole pixel column past a region ending
                // on a pixel edge. The pixel grid rounds outward below, which is the same bias.
                float[] xs = { r[0], r[2], r[2], r[0] };
                float[] ys = { r[1], r[1], r[3], r[3] };
                float[] unit = CornersAabb(inverse, xs, ys);
                if (!MatrixGuard.IsFinite(unit[0]) || !MatrixGuard.IsFinite(unit[1]) || !MatrixGuard.IsFinite(unit[2]) || !MatrixGuard.IsFinite(unit[3])) return null;
                if (unit[2] <= 0 || unit[0] >= 1 || unit[3] <= 0 || unit[1] >= 1) continue;

                // Outward: the first column the box reaches to the last, rows counted from the top;
                // a boundary within a thousandth of a pixel edge is read as on it.
                int x0 = Math.Max(0, SaturatingCast.ToInt(Math.Floor(unit[0] * width + FootprintEpsilon)));
                int x1 = Math.Min(width, SaturatingCast.ToInt(Math.Ceiling(unit[2] * width - FootprintEpsilon)));
                int y0 = Math.Max(0, SaturatingCast.ToInt(Math.Floor((1 - unit[3]) * height + FootprintEpsilon)));
                int y1 = Math.Min(height, SaturatingCast.ToInt(Math.Ceiling((1 - unit[1]) * height - FootprintEpsilon)));
                for (int y = y0; y < y1; y++)
                {
                    int o = (y * width + x0) * 3;
                    for (int x = x0; x < x1; x++, o += 3) { rgb[o] = 0; rgb[o + 1] = 0; rgb[o + 2] = 0; }
                    // Opaque over the region: the box shows, and the mask's own shape there is gone.
                    if (alpha != null) for (int x = x0; x < x1; x++) alpha[y * width + x] = 255;
                }
                painted |= x1 > x0 && y1 > y0;
            }
            if (!painted) return null;

            // Grey when every pixel is: a scan stays a one-channel image.
            bool grey = true;
            for (int i = 0; i < rgb.Length && grey; i += 3) grey = rgb[i] == rgb[i + 1] && rgb[i] == rgb[i + 2];
            byte[] samples = rgb;
            if (grey)
            {
                samples = new byte[(long)width * height];
                for (int i = 0, j = 0; i < samples.Length; i++, j += 3) samples[i] = rgb[j];
            }

            var clone = new PdfStream();
            clone.Set(PdfName.Type, PdfName.XObject);
            clone.Set(PdfName.Subtype, PdfName.Image);
            clone.Set(PdfName.Width, new PdfNumber(width));
            clone.Set(PdfName.Height, new PdfNumber(height));
            clone.Set(PdfName.ColorSpace, grey ? PdfName.DeviceGray : PdfName.DeviceRGB);
            if (image[PdfName.Interpolate] != null) clone.Set(PdfName.Interpolate, image[PdfName.Interpolate]);

            bool transparent = false;
            if (alpha != null) foreach (byte a in alpha) if (a != 255) { transparent = true; break; }

            // One bit whether or not a soft mask comes with it: a mask works over any depth.
            if (grey && IsBilevel(samples))
                WriteBilevel(clone, samples, width, height);
            else
            {
                clone.Set(PdfName.BitsPerComponent, new PdfNumber(8));
                SetFlateData(clone, samples);
            }

            if (transparent)
            {
                var softMask = new PdfStream();
                softMask.Set(PdfName.Type, PdfName.XObject);
                softMask.Set(PdfName.Subtype, PdfName.Image);
                softMask.Set(PdfName.Width, new PdfNumber(width));
                softMask.Set(PdfName.Height, new PdfNumber(height));
                softMask.Set(PdfName.ColorSpace, PdfName.DeviceGray);
                softMask.Set(PdfName.BitsPerComponent, new PdfNumber(8));
                SetFlateData(softMask, alpha);
                // A stream is an indirect object: numbered from the clones' own allocator and emitted
                // with them.
                int maskNumber = forms.AllocateObjectNumber();
                forms.ExtrasSink[maskNumber] = softMask;
                clone.Set(PdfName.SMask, new PdfReference(maskNumber, 0));
            }

            string cloneName = forms.SubstituteClone(image, name, clone, resources, doc, "RdxImg");
            // Drawn once, the name now points at the clone; the original's pixels still go, so a
            // reference this walk does not see finds nothing (the whole-image strip did the same) —
            // and its mask keys with them, so a mask nothing else draws drops out of the rewrite.
            if (cloneName == name)
            {
                PdfFullRewriter.StripStream(image, Array.Empty<byte>());
                image.Remove(PdfName.SMask);
                image.Remove(PdfName.Mask);
                image.Remove(PdfName.Alternates);
                foreach (PdfName derived in DerivedPageEntries) image.Remove(derived);
            }
            return cloneName;
        }

        /// <summary>Every sample black or white.</summary>
        private static bool IsBilevel(byte[] samples)
        {
            foreach (byte s in samples) if (s != 0 && s != 255) return false;
            return true;
        }

        /// <summary>
        /// One-bit grey (1 white, as <c>DeviceGray</c> reads it): CCITT Group 4 within the encoder's
        /// limits, Flate otherwise.
        /// </summary>
        private static void WriteBilevel(PdfStream clone, byte[] samples, int width, int height)
        {
            int stride = (width + 7) / 8;
            var packed = new byte[(long)stride * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (samples[(long)y * width + x] != 0) packed[(long)y * stride + x / 8] |= (byte)(0x80 >> (x % 8));

            clone.Set(PdfName.BitsPerComponent, new PdfNumber(1));
            if (width <= CcittFaxEncoder.MaxEncodeColumns && height <= CcittFaxEncoder.MaxEncodeRows
                && (long)width * height <= CcittFaxEncoder.MaxEncodePixels)
            {
                byte[] g4 = CcittFaxEncoder.Encode(packed, width, height, blackIs1: false);
                var parms = new PdfDictionary();
                parms.Set(PdfName.K, new PdfNumber(-1));
                parms.Set(PdfName.Columns, new PdfNumber(width));
                parms.Set(PdfName.Rows, new PdfNumber(height));
                clone.Set(PdfName.Filter, PdfName.CCITTFaxDecode);
                clone.Set(PdfName.DecodeParms, parms);
                clone.RawData = g4;
                clone.Set(PdfName.Length, new PdfNumber(g4.Length));
                return;
            }
            SetFlateData(clone, packed);
        }

        private static void SetFlateData(PdfStream stream, byte[] samples)
        {
            byte[] encoded = FlateEncodeFilter.Encode(samples);
            stream.Set(PdfName.Filter, PdfName.FlateDecode);
            stream.RawData = encoded;
            stream.Set(PdfName.Length, new PdfNumber(encoded.Length));
        }

        /// <summary>Inverts an affine matrix <c>[a b c d e f]</c>; false when it is singular or not finite.</summary>
        private static bool TryInvert(float[] m, out float[] inverse)
        {
            inverse = null;
            double det = (double)m[0] * m[3] - (double)m[1] * m[2];
            if (!MatrixGuard.IsFinite((float)det) || Math.Abs(det) < 1e-9) return false;
            double ia = m[3] / det, ib = -m[1] / det, ic = -m[2] / det, id = m[0] / det;
            double ie = -(m[4] * ia + m[5] * ic), @if = -(m[4] * ib + m[5] * id);
            inverse = new[] { (float)ia, (float)ib, (float)ic, (float)id, (float)ie, (float)@if };
            foreach (float v in inverse) if (!MatrixGuard.IsFinite(v)) return false;
            return true;
        }

        private static bool HandleInlineImage(ContentStreamOperation op, List<float[]> rects, PdfWriter w, RState gs)
        {
            if (UnitSquareHitsAny(gs.CTM, rects))
            {
                bool stencil = op.Operands.Length > 0 && op.Operands[0] is PdfStream inline
                    && (inline[PdfName.ImageMask] ?? inline[PdfName.IM]) is PdfBoolean { Value: true };
                RefuseCoveredImageSource(gs, stencil, false);
                return true; // drop the inline image (its bytes are inline → dropping the op removes them)
            }

            ContentOperationWriter.Write(w, op);
            return false;
        }

        // ---------------------------------------------------------------
        //  Page mutation helpers
        // ---------------------------------------------------------------

        // The substitution and the full rewrite live in PdfFullRewriter, which template token
        // replacement also uses — both destroy content that must not survive in the output, and a
        // second copy of that walk is exactly the divergence this repo keeps finding.
        private static void SubstitutePageContent(PdfPage page, PdfDocument doc, byte[] redacted,
                                                  FormCloneContext forms, PdfFullRewriter.ContentUsage usage)
        {
            // The allocator and extras sink let the rewriter substitute a private clone when this
            // page's /Contents object is shared with a page the caller did not name. Without them
            // it would strip the shared object in place and silently redact those pages too.
            if (!PdfFullRewriter.TrySubstitutePageContent(page, doc, redacted,
                    forms.AllocateObjectNumber, forms.ExtrasSink, usage))
                throw new PdfRedactionException("Target page has no content stream to redact.");
        }

        /// <summary>
        /// Drops every annotation whose <c>/Rect</c> meets a redaction region from the page's
        /// <c>/Annots</c>, recording each dropped dictionary in <paramref name="dropped"/> for
        /// <see cref="ScrubDroppedAnnotations"/> and <see cref="DetachRedactedFormFields"/>.
        /// </summary>
        /// <remarks>
        /// <b>Subtype plays no part here</b>, deliberately: a Stamp, FreeText or Ink annotation
        /// standing in the region draws content the caller asked to have removed just as a Widget
        /// does. Everything downstream of this set therefore applies to all of them — see
        /// <see cref="ScrubDroppedAnnotations"/>.
        /// </remarks>
        private static void FilterAnnotations(PdfPage page, PdfDocument doc, List<float[]> rects,
                                              HashSet<PdfDictionary> dropped)
        {
            if (!(page.Dictionary[PdfName.Annots] is { } annotsObj)) return;
            if (!(doc.ResolveObject(annotsObj) is PdfArray annots)) return;

            var kept = new PdfArray();
            for (int i = 0; i < annots.Count; i++)
            {
                var aDict = doc.ResolveObject(annots[i]) as PdfDictionary;
                // Resolved: a false here KEEPS the annotation, so an unresolved /Rect preserved
                // an annotation lying inside a redaction region.
                bool intersects = doc.ResolveObject(aDict?[PdfName.Rect]) is PdfArray rect && rect.Count >= 4
                                  && RectHitsAny(NormalizeRect(doc, rect), rects);
                if (!intersects) { kept.Add(annots[i]); continue; }
                if (aDict != null) dropped.Add(aDict);
            }

            // A popup stands elsewhere and shows the same text, and a reply is a comment on it: each
            // goes with the annotation it answers to.
            var stillKept = new PdfArray();
            for (int i = 0; i < kept.Count; i++)
            {
                var aDict = doc.ResolveObject(kept[i]) as PdfDictionary;
                if (aDict != null && AnswersToDropped(aDict, doc, dropped)) dropped.Add(aDict);
                else stillKept.Add(kept[i]);
            }
            page.Dictionary.Set(PdfName.Annots, stillKept);
        }

        /// <summary>The chain of <c>/Parent</c> and <c>/IRT</c> above an annotation, which a file may make any length or a loop, read a bounded way up.</summary>
        private static bool AnswersToDropped(PdfDictionary annotation, PdfDocument doc, HashSet<PdfDictionary> dropped)
        {
            PdfDictionary at = annotation;
            for (int step = 0; step < MaxFormDepth && at != null; step++)
            {
                var up = doc.ResolveObject(at[PdfName.Parent]) as PdfDictionary ?? doc.ResolveObject(at[PdfName.IRT]) as PdfDictionary;
                if (up != null && dropped.Contains(up)) return true;
                at = up;
            }
            return false;
        }

        /// <summary>
        /// Clears the value and appearance keys on every annotation <see cref="FilterAnnotations"/>
        /// dropped, <b>whatever its subtype</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>Not a widget-only rule.</b> The dropped set
        /// is filled by <c>/Rect</c> intersection alone, so a Stamp, FreeText or Ink annotation is in
        /// it on exactly the same terms as a Widget — and its <c>/AP</c> is <i>drawn content</i>
        /// standing inside the region the caller asked to have removed, which is the thing this
        /// engine exists to destroy. Narrowing the loop to Widget would leave a Stamp's appearance
        /// stream in the output whenever anything still pointed at the annotation.
        /// <c>RedactedNonWidgetAnnotationTests</c> pins the Stamp case; the whole pre-existing suite
        /// passed under the narrow reading.</para>
        ///
        /// <para><b>Why clearing is needed at all</b>, given that the rewrite emits reachable objects
        /// only: "unreachable" is often simply false. A tagged document reaches the same annotation
        /// through <c>/StructTreeRoot … /OBJR /Obj</c>, and a widget is reached a second way through
        /// <c>/AcroForm /Fields</c>. Where the annotation really has no other referent this loop is a
        /// no-op and the whole object is dropped instead — so it costs nothing and is the only thing
        /// that holds when it is not.</para>
        ///
        /// <para><b>Why clearing rather than refusing.</b> The engine fails closed where it
        /// <i>cannot</i> remove — an encrypted document, an XObject of unknown subtype, nesting past
        /// the depth cap. Here removal is straightforward and provable, and refusing would reject the
        /// ordinary case of a redaction box touching a filled field or a stamp, which is also the case
        /// <c>PdfOperations</c>' documented ordering ("redact, then flatten") tells callers to rely
        /// on. Same call as plan 08 item 46, which replaced the blanket refusal on shared Form
        /// XObjects with a correct removal once one existed.</para>
        ///
        /// <para><b>Scope.</b> The value (<c>/V</c>, <c>/DV</c>, <c>/RV</c>), the appearance
        /// (<c>/AP</c>, <c>/AS</c>, <c>/MK</c>'s captions), and what any annotation says in words: <c>/Contents</c>,
        /// <c>/RC</c>, <c>/Subj</c>, a field's <c>/TU</c> and <c>/Opt</c> — a free-text annotation's <c>/Contents</c> is the very text drawn —
        /// and a markup annotation's <c>/T</c>, its author. A widget's <c>/T</c> is its field's name, which
        /// <see cref="DetachRedactedFormFields"/> reads.</para>
        /// </remarks>
        private static void ScrubDroppedAnnotations(HashSet<PdfDictionary> dropped)
        {
            foreach (var annot in dropped) Scrub(annot);
        }

        /// <summary>
        /// The value, the appearance and the words of one dictionary: a dropped annotation's, or a field
        /// node's whose every widget was dropped, its name with them, since the form no longer holds it.
        /// </summary>
        private static void Scrub(PdfDictionary annot)
        {
            annot.Remove(PdfName.V);
            annot.Remove(PdfName.DV);
            annot.Remove(PdfName.RV);          // §12.7.4.3 rich-text value: the same text again
            annot.Remove(PdfName.AP);
            annot.Remove(PdfName.AS);
            annot.Remove(PdfName.Contents);
            annot.Remove(PdfName.RC);
            annot.Remove(PdfName.Subj);
            annot.Remove(PdfName.TU);
            annot.Remove(PdfName.MK);
            annot.Remove(PdfName.Opt);
            annot.Remove(PdfName.FS);          // a file attachment's file
            annot.Remove(PdfName.AF);          // its associated files
            if ((annot[PdfName.Subtype] as PdfName)?.Value != PdfName.Widget.Value) annot.Remove(PdfName.T);
        }

        /// <summary>
        /// Removes the <b>widget</b> annotations <see cref="FilterAnnotations"/> dropped from the
        /// AcroForm field tree as well, so the rewrite has no second route to them.
        /// </summary>
        /// <remarks>
        /// <para><b>Why /Annots alone is not enough.</b> A widget annotation is reachable from two
        /// places: the page's <c>/Annots</c> and the catalog's <c>/AcroForm /Fields</c> — directly
        /// for a merged widget/field, or through a parent field's <c>/Kids</c>. The rewrite emits
        /// every object reachable from the catalog, so a widget removed from the page only still
        /// reached the output carrying its <c>/V</c> and its <c>/AP /N</c> appearance stream. The
        /// value never was extractable as page text, which is why a text-extraction assertion
        /// showed nothing wrong; <c>PdfFormReader.GetFields</c> over the output handed it straight
        /// back.</para>
        ///
        /// <para><b>Both halves are needed.</b> Pruning the field tree is what makes the dictionary
        /// unreachable so the rewrite drops it outright.
        /// <see cref="ScrubDroppedAnnotations"/> is what holds when something <i>outside</i> the page
        /// and the form still points at it. Scrubbing is also what bounds the damage when the walk
        /// below stops at its depth cap: the dropped widget's own value and appearance are already
        /// gone from its dictionary, so only a <i>parent</i> node deeper than the cap can keep a
        /// <c>/V</c> — which needs a field tree 100 levels deep.</para>
        ///
        /// <para>A parent field is removed only when <i>every</i> widget under it was redacted —
        /// otherwise it still shows on a page the caller did not name, and its value belongs to that
        /// page.</para>
        /// </remarks>
        private static void DetachRedactedFormFields(PdfDocument doc, HashSet<PdfDictionary> dropped)
        {
            if (dropped.Count == 0) return;

            var acro = doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.AcroForm]) as PdfDictionary;
            if (acro == null) return;
            if (!(doc.ResolveObject(acro[PdfName.Fields]) is PdfArray fields)) return;

            var kept = new PdfArray();
            var visiting = new HashSet<PdfDictionary>();
            var decided = new Dictionary<PdfDictionary, bool>();
            for (int i = 0; i < fields.Count; i++)
                if (KeepFieldAfterRedaction(fields[i], doc, dropped, visiting, decided, 0))
                    kept.Add(fields[i]);
            acro.Set(PdfName.Fields, kept);

            // The calculation order names fields too (§12.7.3 Table 224, /CO): a removed field left there
            // stays in the file by that route. Its value is scrubbed either way; it leaves the order.
            if (doc.ResolveObject(acro[PdfName.CO]) is PdfArray order)
            {
                var keptOrder = new PdfArray();
                for (int i = 0; i < order.Count; i++)
                {
                    var node = doc.ResolveObject(order[i]) as PdfDictionary;
                    if (node == null || (!dropped.Contains(node) && (!decided.TryGetValue(node, out bool keep) || keep)))
                        keptOrder.Add(order[i]);
                }
                acro.Set(PdfName.CO, keptOrder);
            }
        }

        /// <summary>
        /// True when this field node survives redaction. A node whose every kid was dropped is
        /// dropped too, so an inheritable <c>/V</c> held by a parent cannot outlive the widgets
        /// that displayed it.
        /// </summary>
        /// <remarks>
        /// <paramref name="visiting"/> is the current PATH, so it catches a back edge;
        /// <paramref name="decided"/> is every node already answered, and it is what bounds the work.
        /// The two are not interchangeable and the path set alone is not a guard: a field tree whose
        /// every <c>/Kids</c> is <c>[N 0 R, N 0 R]</c> has no cycle at all, so the path set never
        /// fires, and the shared subtree is re-walked twice per level — 2^depth from ~100 tiny
        /// objects, bounded only by <c>PdfLimits.MaxTreeDepth</c>. <c>PdfUsageRightsReader</c>,
        /// <c>PdfDPartReader</c> and <c>PdfFormReader.Walk</c> — the other walker of this very tree —
        /// bound their <c>/Kids</c> walks with a global set the same way.
        ///
        /// <para>Memoising rather than merely skipping is what keeps the answer right: a node
        /// reached twice must give the same verdict both times, and the <c>/Kids</c> rewrite below
        /// is idempotent for one node, so replaying the cached result is equivalent to re-deriving
        /// it.</para>
        /// </remarks>
        private static bool KeepFieldAfterRedaction(PdfObject entry, PdfDocument doc,
                                                    HashSet<PdfDictionary> dropped,
                                                    HashSet<PdfDictionary> visiting,
                                                    Dictionary<PdfDictionary, bool> decided, int depth)
        {
            if (!(doc.ResolveObject(entry) is PdfDictionary node)) return true;
            if (dropped.Contains(node)) return false;
            if (decided.TryGetValue(node, out bool already)) return already;

            // Past the depth cap, stop descending and keep the node. The widget's own value and
            // appearance are gone either way — DetachRedactedFormFields cleared them on the
            // dictionary itself — but a parent node deeper than the cap keeps its own /V, which
            // takes a field tree 100 levels deep to reach.
            if (depth >= PdfLimits.MaxTreeDepth) return true;

            // A /Kids entry pointing back at a node already on this path is a cycle, so it is
            // malformed by definition: a field tree has one parent per node. DROP the back edge
            // rather than keep it — the target is an ancestor here, so it survives through its real
            // parent, and keeping the edge would leave the ancestor looking as though it still had
            // a child after every real one was redacted, which is exactly how its /V would outlive
            // the widget that displayed it.
            if (!visiting.Add(node)) return false;
            try
            {
                bool result;
                if (!(doc.ResolveObject(node[PdfName.Kids]) is PdfArray kids))
                {
                    result = true;
                }
                else
                {
                    var keptKids = new PdfArray();
                    bool anyDropped = false;
                    for (int i = 0; i < kids.Count; i++)
                    {
                        if (KeepFieldAfterRedaction(kids[i], doc, dropped, visiting, decided, depth + 1))
                            keptKids.Add(kids[i]);
                        else anyDropped = true;
                    }

                    if (!anyDropped) result = true;
                    else
                    {
                        node.Set(PdfName.Kids, keptKids);
                        result = keptKids.Count > 0;
                        // Removed with its widgets, the node keeps no value of theirs: a route the field
                        // tree is not (the calculation order, an action's /Fields) still reaches it.
                        if (!result) Scrub(node);
                    }
                }

                decided[node] = result;
                return result;
            }
            finally { visiting.Remove(node); }
        }

        // ---------------------------------------------------------------
        //  Full single-revision rewrite (reachable objects only → no residual data)
        // ---------------------------------------------------------------

        private static void FullRewrite(PdfDocument doc, Stream output, FormCloneContext forms)
        {
            // The cloned forms are objects the document has never heard of, so they travel as
            // extras. Everything still reachable is emitted and everything else - including any
            // original form whose only invocation now names a clone - is dropped.
            if (!PdfFullRewriter.TryFullRewrite(doc, output, forms.Extras))
                throw new PdfRedactionException("Document has no resolvable catalog; cannot rewrite.");
        }

        // ---------------------------------------------------------------
        //  Geometry + parsing helpers
        // ---------------------------------------------------------------

        /// <summary>
        /// True when the box intersects any redaction rectangle, inflated by
        /// <see cref="IntersectMargin"/> — and true, deliberately, when the box is not finite,
        /// i.e. <see cref="CornersAabb"/> could not place the content at all.
        /// </summary>
        /// <remarks>
        /// <para>Answering "hit" is the direction this whole engine already leans: the margin
        /// applied below exists to bias toward removal for exactly this reason, and
        /// <see cref="IsWhollyInsideAny"/> documents the opposite bias for geometry that might
        /// belong to the rest of the page. <b>Content nobody can place is content nobody can prove
        /// lies outside the caller's rectangle.</b></para>
        /// </remarks>
        private static bool AabbHitsAny(float minX, float minY, float maxX, float maxY, List<float[]> rects)
        {
            if (!MatrixGuard.IsFinite(minX) || !MatrixGuard.IsFinite(minY) ||
                !MatrixGuard.IsFinite(maxX) || !MatrixGuard.IsFinite(maxY))
            {
                DocumentEditDiagnostics.Report(
                    "Redact",
                    "content whose transformed bounding box is not finite was treated as " +
                    "intersecting every redaction region on the page; a non-finite matrix operand " +
                    "makes its position unknowable, and unknowable content is removed rather than kept.");
                return rects.Count > 0;
            }

            minX -= IntersectMargin; minY -= IntersectMargin; maxX += IntersectMargin; maxY += IntersectMargin;
            foreach (var r in AreaIndex.Candidates(rects, minX, minY, maxX, maxY))
            {
                AreaTests++;
                if (minX <= r[2] && maxX >= r[0] && minY <= r[3] && maxY >= r[1]) return true;
            }
            return false;
        }

        /// <summary>The area tests the hit questions have made on this thread — the evidence that a glyph is tested against the areas near it, not every area.</summary>
        [ThreadStatic] internal static long AreaTests;

        /// <summary>
        /// A page's redaction areas bucketed on a grid, for the questions every glyph, path, image and
        /// annotation asks of them. Text redaction makes an area of every match, and each glyph was
        /// tested against the whole list: a page of fifty thousand matches was ~10¹⁰ box tests.
        /// </summary>
        /// <remarks>
        /// Built once a list, on the first question, and kept beside it: a page's list is filled
        /// before its content is walked and only read after (the <c>byPage</c> loop in
        /// <see cref="Redact(Stream, Stream, IReadOnlyList{PdfRedactionArea})"/>). A short list is
        /// scanned as it was. An area over more than <see cref="MaxCellsPerArea"/> cells is kept in a
        /// list every question also reads, so one huge area does not fill the grid; a question that
        /// covers many cells reads each candidate at most once a cell it is filed in.
        /// </remarks>
        private sealed class AreaIndex
        {
            private const int LinearBelow = 32, MaxCells = 256, MaxCellsPerArea = 64;
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<float[]>, AreaIndex> Built = new();

            private readonly float _x0, _y0, _cellW, _cellH;
            private readonly int _cols, _rows;
            private readonly List<float[]>[] _cells;
            private readonly List<float[]> _large = new List<float[]>();
            private readonly bool _linear;

            private AreaIndex(List<float[]> rects)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (var r in rects)
                {
                    x0 = Math.Min(x0, r[0]); y0 = Math.Min(y0, r[1]);
                    x1 = Math.Max(x1, r[2]); y1 = Math.Max(y1, r[3]);
                }
                // An area whose corner overflowed float has no cell: the list is scanned as it was.
                if (!Finite(x0) || !Finite(y0) || !Finite(x1) || !Finite(y1)) { _linear = true; _cells = Array.Empty<List<float[]>>(); return; }
                int side = SaturatingCast.ToInt(Math.Min(MaxCells, Math.Max(1, Math.Ceiling(Math.Sqrt(rects.Count)))));
                _cols = _rows = side;
                _x0 = x0; _y0 = y0;
                _cellW = Math.Max((x1 - x0) / side, 1e-3f);
                _cellH = Math.Max((y1 - y0) / side, 1e-3f);
                _cells = new List<float[]>[side * side];
                foreach (var r in rects)
                {
                    Span(r[0], r[2], _x0, _cellW, _cols, out int c0, out int c1);
                    Span(r[1], r[3], _y0, _cellH, _rows, out int r0, out int r1);
                    if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > MaxCellsPerArea) { _large.Add(r); continue; }
                    for (int row = r0; row <= r1; row++)
                        for (int col = c0; col <= c1; col++)
                            (_cells[row * _cols + col] ??= new List<float[]>()).Add(r);
                }
            }

            private static void Span(float lo, float hi, float origin, float cell, int count, out int first, out int last)
            {
                first = SaturatingCast.ToInt(Math.Max(0, Math.Min(count - 1, Math.Floor((lo - origin) / cell))));
                last = SaturatingCast.ToInt(Math.Max(0, Math.Min(count - 1, Math.Floor((hi - origin) / cell))));
                if (last < first) last = first;
            }

            /// <summary>The areas that may meet the box: the whole list when it is short, else those filed in the cells it covers and the large ones.</summary>
            internal static IEnumerable<float[]> Candidates(List<float[]> rects, float minX, float minY, float maxX, float maxY)
            {
                if (rects.Count <= LinearBelow || !Finite(minX) || !Finite(minY) || !Finite(maxX) || !Finite(maxY)) return rects;
                AreaIndex index = Built.GetValue(rects, r => new AreaIndex(r));
                return index._linear ? rects : index.Near(minX, minY, maxX, maxY);
            }

            private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

            private IEnumerable<float[]> Near(float minX, float minY, float maxX, float maxY)
            {
                foreach (var r in _large) yield return r;
                Span(minX, maxX, _x0, _cellW, _cols, out int c0, out int c1);
                Span(minY, maxY, _y0, _cellH, _rows, out int r0, out int r1);
                for (int row = r0; row <= r1; row++)
                    for (int col = c0; col <= c1; col++)
                        if (_cells[row * _cols + col] is List<float[]> cell)
                            foreach (var r in cell) yield return r;
            }
        }

        private static bool RectHitsAny(float[] box, List<float[]> rects)
            => AabbHitsAny(box[0], box[1], box[2], box[3], rects);

        private static bool UnitSquareHitsAny(float[] ctm, List<float[]> rects)
            => RectHitsAny(TransformUnitSquare(ctm), rects);

        private static float[] TransformUnitSquare(float[] m)
        {
            float[] xs = { 0, 1, 1, 0 };
            float[] ys = { 0, 0, 1, 1 };
            return CornersAabb(m, xs, ys);
        }

        private static float[] TransformBBox(PdfDocument doc, PdfArray bbox, float[] m)
        {
            float x1 = NumAt(doc, bbox, 0), y1 = NumAt(doc, bbox, 1), x2 = NumAt(doc, bbox, 2), y2 = NumAt(doc, bbox, 3);
            float[] xs = { x1, x2, x2, x1 };
            float[] ys = { y1, y1, y2, y2 };
            return CornersAabb(m, xs, ys);
        }

        /// <summary>
        /// The axis-aligned bounding box of four corners mapped through <paramref name="m"/>, or a
        /// box of NaN when any mapped corner is not finite.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this closes let text survive redaction while the operation reported
        /// success.</b> The extents are seeded from <c>float.MaxValue</c>/<c>float.MinValue</c>, and
        /// every comparison against NaN is false — so a corner that mapped to NaN left the sentinels
        /// untouched and this returned <c>{MaxValue, MaxValue, MinValue, MinValue}</c>: an inverted
        /// box, indistinguishable from a real one by type, that <see cref="AabbHitsAny"/> then
        /// evaluated as <c>float.MaxValue &lt;= rect.right</c> — <b>no hit</b>. The glyph was
        /// written to the output verbatim and the caller was told the redaction succeeded.
        /// <b>Swallowing the NaN here is what made the failure invisible</b>, so the check belongs
        /// here rather than only at the test: by the time the box reaches
        /// <see cref="AabbHitsAny"/> its four values are finite again.</para>
        /// <para>A <c>cm</c> operand that overflows a <c>float</c> is enough to reach it — the
        /// composed text render matrix then contains <c>0 * Infinity</c> — and nothing on this path
        /// gates on finiteness: <c>MatrixGuard</c> is called by the two renderers only, and this
        /// engine's single <see cref="float.IsNaN(float)"/> test guards number <i>formatting</i> for
        /// a rebuilt <c>TJ</c>, not geometry.</para>
        /// <para>Seeding from the first mapped corner — the shape
        /// <c>PdfPageRenderer.TransformedBBox</c> uses — is <b>not</b> sufficient on its own: a NaN
        /// in the second corner still compares false against a finite seed and is silently dropped.
        /// Every corner is therefore tested.</para>
        /// </remarks>
        private static float[] CornersAabb(float[] m, float[] xs, float[] ys)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                float px = m[0] * xs[i] + m[2] * ys[i] + m[4];
                float py = m[1] * xs[i] + m[3] * ys[i] + m[5];
                if (!MatrixGuard.IsFinite(px) || !MatrixGuard.IsFinite(py))
                    return new[] { float.NaN, float.NaN, float.NaN, float.NaN };
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
            }
            return new[] { minX, minY, maxX, maxY };
        }

        private static float[] NormalizeRect(PdfDocument doc, PdfArray r)
        {
            float x1 = NumAt(doc, r, 0), y1 = NumAt(doc, r, 1), x2 = NumAt(doc, r, 2), y2 = NumAt(doc, r, 3);
            return new[] { Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2) };
        }

        private static float[] ReadMatrix(PdfDocument doc, PdfObject obj)
            => obj is PdfArray a && a.Count >= 6
                ? new[] { NumAt(doc, a, 0), NumAt(doc, a, 1), NumAt(doc, a, 2), NumAt(doc, a, 3), NumAt(doc, a, 4), NumAt(doc, a, 5) }
                : new[] { 1f, 0, 0, 1, 0, 0 };

        // Resolved: read as stored, a box or matrix written with referenced numbers is all zeros, and
        // what it places is taken to stand nowhere near any region.
        private static float NumAt(PdfDocument doc, PdfArray a, int i) => (float)PdfResolve.ArrayDouble(doc, a, i);

        private static PdfStream ResolveXObject(string name, PdfDictionary resources, PdfDocument doc)
        {
            if (name == null || resources == null) return null;
            var xdict = doc.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;
            return xdict == null ? null : doc.ResolveObject(xdict[new PdfName(name)]) as PdfStream;
        }

        // The form-uniqueness test lives in FormInvocationCounter, which walks INTO forms as well as
        // pages: a page-only count would answer 1 for a form invoked once from a page and once from
        // inside another form, and the caller would rewrite it in place. It reads
        // original.ObjectNumber; "which object is this form" answered from a single
        // page's resource dictionary is precisely the mistake this comment records.

        private static FontInfo GetFont(string fontName, PdfDictionary resources, PdfDocument doc,
            FontCache fontCache, Dictionary<string, FontInfo> cache, FormCloneContext forms)
        {
            string key = fontName ?? "";
            if (cache.TryGetValue(key, out var fi)) return fi;

            PdfFont font = ResourceFontLookup.Resolve(fontName, resources, doc);
            // Codes come from the font's own encoding CMap, not from a fixed stride.
            // A fixed stride (FontCache.GetCharCodeSize, since removed) took the NARROWEST codespace,
            // so a mixed-width CMap — 1-byte ASCII plus 2-byte CJK, which is what the Shift-JIS-based
            // Adobe CMaps are — gave 1 and made the walk step through every 2-byte code one byte at a time. Each
            // half is then treated as its own character: the width lookup is wrong, and worse, a
            // redaction hit can drop one half and keep the other, leaving bytes that decode to
            // something the author never wrote. The codec is the splitter, and nothing here needs a
            // step size any more.
            IReadOnlyList<CMapCodec.Code> Split(byte[] bytes) =>
                GlyphIdResolver.DecodeCodes(fontCache, fontName, resources, doc, bytes);

            RedactionType3Font type3 = null;
            if (fontCache.IsType3Font(fontName, resources, doc)
                && doc.ResolveObject(resources[PdfName.Font]) is PdfDictionary fonts
                && doc.ResolveObject(fonts[new PdfName(fontName)]) is PdfDictionary type3Font)
                type3 = forms.Type3FontOf(type3Font, doc);

            Func<int, VerticalGlyphMetrics> vertical = null;
            if (type3 == null && fontCache.GetWMode(fontName, resources, doc) == 1)
                vertical = cid => fontCache.GetVerticalMetrics(fontName, cid, resources, doc, 1f);

            fi = new FontInfo(font, Split, type3, vertical);
            cache[key] = fi;
            return fi;
        }

        // One implementation, in Core.Text.ContentOperand — this pair existed under two
        // names in two rewriters, differing only by a capacity hint.
        private static List<object> GetStringSegs(ContentStreamOperation op, int index)
            => ContentOperand.ShowTextSegments(op, index);

        private static List<object> GetArraySegs(ContentStreamOperation op)
            => ContentOperand.ShowTextArraySegments(op);

        /// <summary>
        /// An operation written back. Only a show operator's string is drawn, and those come through
        /// <see cref="EmitShow"/>: on any other operator a string, a dictionary or an array of either is
        /// an operand no reader reads (the parser hands a stray one to the next operator, <c>BT</c> included)
        /// and what it holds would stay in the file.
        /// </summary>
        private static void Verbatim(PdfWriter w, ContentStreamOperation op)
        {
            if (w == null) return;
            ContentOperationWriter.Write(w, IsShowOrInlineImage(op.Operator) ? op : WithoutWords(op));
        }

        private static bool IsShowOrInlineImage(string op)
            => op == PdfOperators.ShowText || op == PdfOperators.ShowTextArray || op == PdfOperators.NextLineShowText
               || op == PdfOperators.NextLineSetSpacingShowText || op == PdfOperators.BeginInlineImage;

        private static ContentStreamOperation WithoutWords(ContentStreamOperation op)
        {
            bool clean = true;
            foreach (PdfObject operand in op.Operands) clean &= !CarriesWords(operand);
            if (clean) return op;

            var kept = new List<PdfObject>(op.Operands.Length);
            foreach (PdfObject operand in op.Operands) if (!CarriesWords(operand)) kept.Add(operand);
            return new ContentStreamOperation(op.Operator, kept.ToArray());
        }

        private static bool CarriesWords(PdfObject operand)
        {
            if (operand is PdfString || operand is PdfDictionary) return true;
            if (!(operand is PdfArray array)) return false;
            foreach (PdfObject item in array) if (!(item is PdfNumber)) return true;
            return false;
        }

        private static float F(ContentStreamOperation op, int i)
            => op.Operands.Length > i && op.Operands[i] is PdfNumber n ? (float)n.DoubleValue : 0f;

        private static float[] Identity() => Rendering.PdfMatrix.Identity();

        // Compose(A, B): A applied first, then B. Through Rendering.PdfMatrix — this was one of
        // four copies, and the comment that stood here ("matches
        // PdfStructuredTextExtractor.Compose") was the tell.
        private static float[] Compose(float[] A, float[] B) => Rendering.PdfMatrix.Compose(A, B);

        /// <summary>
        /// This engine's <c>TJ</c> number precision — deliberately finer than
        /// <see cref="PdfTemplateEngine.TemplateNumberFormat"/>, and named because
        /// <see cref="ShowTextArrayBuilder"/> takes it as a parameter.
        /// </summary>
        internal const string RedactionNumberFormat = "0.######";

        private static string Fmt(float v) => PdfRealFormat.Format(v, RedactionNumberFormat);

        // ---------------------------------------------------------------
        //  State
        // ---------------------------------------------------------------

        private sealed class RState
        {
            public float[] CTM;
            public float[] Tm = { 1, 0, 0, 1, 0, 0 };
            public float[] Tlm = { 1, 0, 0, 1, 0, 0 };
            public string FontName;
            // The size the renderers show text at before any Tf.
            public float FontSize = 12f;
            public float Tc, Tw, Ts;
            public float Th = 1f;
            public float TL;
            public float LineWidth = 1f;
            public float MiterLimit = 10f;
            public int RenderMode;
            // What a paint goes through that draws an image a region could only cover — the fill's and
            // the stroke's tiling pattern, the soft mask — named for a refusal, or null.
            public string FillImageSource, StrokeImageSource, MaskImageSource;

            public RState(float[] ctm) { CTM = (float[])ctm.Clone(); }

            public RState Clone() => new RState(CTM)
            {
                Tm = (float[])Tm.Clone(),
                Tlm = (float[])Tlm.Clone(),
                FontName = FontName,
                FontSize = FontSize,
                Tc = Tc, Tw = Tw, Ts = Ts, Th = Th, TL = TL,
                LineWidth = LineWidth, MiterLimit = MiterLimit, RenderMode = RenderMode,
                FillImageSource = FillImageSource, StrokeImageSource = StrokeImageSource, MaskImageSource = MaskImageSource,
            };
        }

        private readonly struct FontInfo
        {
            public readonly PdfFont Font;

            /// <summary>
            /// Splits a show string into character codes through the font's own encoding CMap, so a
            /// mixed-width CMap yields 1-byte and 2-byte codes in the right places.
            ///
            /// <para>There is deliberately no <c>Stride</c> alongside this. One was kept briefly
            /// "for callers that need a step size", had none, and its only effect was to call
            /// <c>FontCache.GetCharCodeSize</c> (since removed) on every font resolution and discard the answer — while
            /// leaving the wrong primitive visible for the next person to reach for.</para>
            /// </summary>
            public readonly Func<byte[], IReadOnlyList<CMapCodec.Code>> SplitCodes;

            /// <summary>The font's glyph programs, when it is a Type 3 font.</summary>
            public readonly RedactionType3Font Type3;

            /// <summary>A CID's vertical metrics, when the font writes vertically (§9.7.4.3).</summary>
            public readonly Func<int, VerticalGlyphMetrics> Vertical;

            public FontInfo(PdfFont font, Func<byte[], IReadOnlyList<CMapCodec.Code>> splitCodes,
                            RedactionType3Font type3, Func<int, VerticalGlyphMetrics> vertical)
            {
                Font = font;
                SplitCodes = splitCodes;
                Type3 = type3;
                Vertical = vertical;
            }
        }
    }
}
