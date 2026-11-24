// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Images;
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
    /// <summary>A class of page content <see cref="PdfContentFilter"/> can take out.</summary>
    [Flags]
    public enum PdfContentClass
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary>
        /// Text: every text-showing operation (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c>), in every
        /// font — a Type 3 font's glyphs included, since they are shown by those operators. Text
        /// whose render mode adds to the clip is kept in mode 7, so what it clipped is still clipped
        /// and nothing of it is painted.
        /// </summary>
        Text = 1,

        /// <summary>Images: every image XObject drawn (<c>Do</c>) and every inline image (<c>BI</c> … <c>EI</c>).</summary>
        Images = 2,

        /// <summary>
        /// Vector graphics: every path painted (<c>f</c>, <c>S</c>, <c>B</c> and the rest, which
        /// become <c>n</c>, so a clip set with the path still clips) and every shading painted with
        /// <c>sh</c>. A pattern a path is painted with goes with the path.
        /// </summary>
        Vector = 4,
    }

    /// <summary>What <see cref="PdfContentFilter.Remove"/> did.</summary>
    public sealed class ContentFilterResult
    {
        internal ContentFilterResult(int streamsRewritten, int operationsRemoved, IReadOnlyList<string> warnings)
        {
            StreamsRewritten = streamsRewritten;
            OperationsRemoved = operationsRemoved;
            Warnings = warnings;
        }

        /// <summary>How many content streams were written anew — each once, however many pages share it.</summary>
        public int StreamsRewritten { get; }

        /// <summary>How many painting operations were taken out across them.</summary>
        public int OperationsRemoved { get; }

        /// <summary>Content the filter could not read and left as it was, each named once; empty when none.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>
    /// Takes a class of content — text, images or vector graphics — out of every page of a document:
    /// A content-class filter (text, image, vector), as a writer.
    /// </summary>
    /// <remarks>
    /// <para><b>Where it looks.</b> Every page's content streams, and every container the pages reach
    /// — form XObjects, tiling patterns and annotation appearance streams, through
    /// <see cref="DocumentImageIndex"/>'s walk, each once however many pages draw it. A Type 3 font's
    /// glyph procedures are not rewritten: they are text, taken out whole by
    /// <see cref="PdfContentClass.Text"/> and left whole by the other classes.
    /// A form XObject stays invoked; its own content loses the class.</para>
    /// <para><b>How an operation is taken out</b> is <see cref="ContentOperationRemoval"/>'s rule, the
    /// one the transparency flattener's region pass applies: a path's paint becomes <c>n</c>, so a clip
    /// set before it still clips; text whose render mode adds to the clip is shown in mode 7, so it
    /// clips and paints nothing; other text is dropped, except a run before such a clipping run in
    /// its text object, which is kept in mode 3 so the pen reaches the clip where the page set it;
    /// an image, an inline image or a shading is dropped. Everything else — graphics state, colour,
    /// transforms, marked content — stays as it was, so the content that remains draws exactly where
    /// it did.</para>
    /// <para><b>Written as an appended revision</b> that supersedes each rewritten content stream under
    /// its own object number (<see cref="IncrementalUpdateWriter"/>), the pages and resources untouched;
    /// an input with nothing to take out is copied through. An encrypted document is refused, as every
    /// whole-file writer here refuses it: the rewritten streams would be written in clear text where
    /// the reader expects ciphertext.</para>
    /// <para><b>Not a deletion of the bytes</b>, for the same reason: what was taken out is still in the
    /// revision before, and recoverable. Use <c>PdfOperations.Redact</c> when it has to be destroyed.</para>
    /// </remarks>
    public static class PdfContentFilter
    {
        [ThreadStatic] private static string _lastFilterWarning;

        /// <summary>
        /// Non-null when the most recent <see cref="Remove"/> on this thread left some content as it
        /// was — a stream it could not decode — naming it; null when it read everything. Reset at the
        /// start of every call.
        /// </summary>
        public static string LastFilterWarning => _lastFilterWarning;

        /// <summary>
        /// Writes <paramref name="input"/> to <paramref name="output"/> with every operation of
        /// <paramref name="classes"/> taken out.
        /// </summary>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static ContentFilterResult Remove(Stream input, Stream output, PdfContentClass classes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            _lastFilterWarning = null;

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "filter the content of", "the rewritten streams");

            var objects = new List<IndirectObject>();
            var warnings = new Core.Util.DistinctStrings();
            int removed = 0;
            var rewritten = new HashSet<int>();

            if (classes != PdfContentClass.None)
            {
                // The containers the pages reach, once each by object number, with the page whose
                // resources stand in for a container that has none.
                var containers = new List<(PdfStream Stream, int PageIndex)>();
                var seen = new HashSet<int>();
                DocumentImageIndex.Build(doc, onContainer: (pageIndex, container) =>
                {
                    if (container is PdfStream s && s.ObjectNumber > 0 && seen.Add(s.ObjectNumber) && IsRewritable(doc, s))
                        containers.Add((s, pageIndex));
                });

                for (int p = 0; p < doc.Pages.Count; p++)
                {
                    PdfDictionary pageResources = doc.Pages[p].Resources;
                    string where = "page " + (p + 1).ToString(CultureInfo.InvariantCulture) + "'s content";
                    foreach (PdfStream content in PdfColorSpaceConverter.PageContentStreams(doc, doc.Pages[p].Dictionary))
                    {
                        if (content.ObjectNumber <= 0 || !rewritten.Add(content.ObjectNumber)) continue;
                        removed += Filter(doc, content, pageResources, classes, where, warnings, objects);
                    }
                }
                foreach (var (container, pageIndex) in containers)
                {
                    if (!rewritten.Add(container.ObjectNumber)) continue;
                    PdfDictionary resources = doc.ResolveObject(container[PdfName.Resources]) as PdfDictionary
                                              ?? (pageIndex >= 0 && pageIndex < doc.Pages.Count ? doc.Pages[pageIndex].Resources : null);
                    string where = "the content of object " + container.ObjectNumber.ToString(CultureInfo.InvariantCulture)
                        + (pageIndex >= 0 ? " on page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture) : "");
                    removed += Filter(doc, container, resources, classes, where, warnings, objects);
                }
            }

            if (warnings.Count > 0) _lastFilterWarning = warnings.Joined("; ") + ".";

            if (objects.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return new ContentFilterResult(0, 0, warnings.Items);
            }

            int size = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            IncrementalUpdateWriter.WriteUpdate(ms, output, objects, PdfTrailerBuilder.ForUpdate(doc.Trailer, size),
                doc.Parser.StartXrefOffset, size);
            return new ContentFilterResult(objects.Count, removed, warnings.Items);
        }

        /// <summary>A form XObject or a tiling pattern; not a Type 3 glyph procedure, which is text.</summary>
        private static bool IsRewritable(PdfDocument doc, PdfStream container)
            => PdfResolve.Name(doc, container, PdfName.Subtype) == PdfName.Form
               || PdfResolve.Int(doc, container, PdfName.PatternType, 0) == 1;

        /// <summary>
        /// Rewrites <paramref name="content"/> without <paramref name="classes"/>, superseding it in
        /// <paramref name="objects"/> when anything was taken out; the operations taken out.
        /// </summary>
        private static int Filter(PdfDocument doc, PdfStream content, PdfDictionary resources, PdfContentClass classes,
            string where, Core.Util.DistinctStrings warnings, List<IndirectObject> objects)
        {
            byte[] data;
            try { data = content.GetDecodedData(); }
            catch (Exception ex) when (ex is PdfException || ex is InvalidDataException || ex is IOException)
            {
                Warn(warnings, $"{where}: left as it is — its stream could not be decoded ({ex.Message})");
                return 0;
            }
            if (data == null || data.Length == 0) return 0;

            bool text = (classes & PdfContentClass.Text) != 0;
            bool images = (classes & PdfContentClass.Images) != 0;
            bool vector = (classes & PdfContentClass.Vector) != 0;
            var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
            var imageNames = new Dictionary<string, bool>(StringComparer.Ordinal);

            List<ContentStreamOperation> ops = ContentStreamParser.Parse(data);
            bool[] clipFollows = text ? ClippingTextFollows(ops) : null;
            var tracker = new TextPaintTracker();
            int removed = 0;
            using var ms = new MemoryStream();
            var w = new PdfWriter(ms);
            for (int i = 0; i < ops.Count; i++)
            {
                ContentStreamOperation op = ops[i];
                bool shows = tracker.Observe(op.Operator, op.Operands);
                if (shows)
                {
                    if (!text) { ContentOperationWriter.Write(w, op); continue; }
                    removed++;
                    // A clipping run later in the same text object is set where the pen is, so a
                    // painting run before it is kept as text in mode 3: it moves the pen as it did
                    // and paints nothing. With nothing after it reading the position, it goes.
                    if (clipFollows[i]) ContentOperationRemoval.WriteTextInvisible(w, op, tracker);
                    else ContentOperationRemoval.WriteTextUnpainted(w, op, tracker);
                    continue;
                }
                if (vector && ContentOperationRemoval.PaintsPath(op.Operator))
                {
                    removed++;
                    ContentOperationRemoval.WritePathUnpainted(w);
                    continue;
                }
                if (vector && op.Operator == PdfOperators.ShadingFill) { removed++; continue; }
                if (images && op.Operator == PdfOperators.BeginInlineImage) { removed++; continue; }
                if (images && op.Operator == PdfOperators.InvokeXObject && IsImage(doc, xobjects, op, imageNames))
                {
                    removed++;
                    continue;
                }
                ContentOperationWriter.Write(w, op);
            }
            if (removed == 0) return 0;

            byte[] rewritten = FlateEncodeFilter.Encode(ms.ToArray());
            var replacement = (PdfStream)PdfDictionary.CloneShallow(content);
            replacement[PdfName.Filter] = PdfName.FlateDecode;
            replacement.Remove(PdfName.DecodeParms);
            replacement[PdfName.Length] = new PdfNumber(rewritten.Length);
            replacement.RawData = rewritten;
            objects.Add(new IndirectObject(content.ObjectNumber, content.GenerationNumber, replacement));
            return removed;
        }

        /// <summary>
        /// For each operation, whether a text-showing operation in a clipping render mode follows it
        /// in the same text object: a painting run before one moves the pen the clip is set at.
        /// </summary>
        private static bool[] ClippingTextFollows(List<ContentStreamOperation> ops)
        {
            var result = new bool[ops.Count];
            // One tracker over the whole stream: the render mode is text state, set before a BT as
            // well as inside one. Within each text object the last clipping show is found, and every
            // operation before it in the object is marked.
            var tracker = new TextPaintTracker();
            int objectStart = 0, lastClip = -1;
            for (int i = 0; i <= ops.Count; i++)
            {
                bool ends = i == ops.Count || ops[i].Operator == PdfOperators.EndText;
                if (!ends)
                {
                    if (ops[i].Operator == PdfOperators.BeginText) { objectStart = i; lastClip = -1; }
                    if (tracker.Observe(ops[i].Operator, ops[i].Operands) && tracker.RenderMode.AddsToClip()) lastClip = i;
                    continue;
                }
                for (int j = objectStart; j < lastClip; j++) result[j] = true;
                lastClip = -1;
                if (i < ops.Count) tracker.Observe(ops[i].Operator, ops[i].Operands);
            }
            return result;
        }

        /// <summary>Whether the XObject <paramref name="op"/> invokes is an image, by its <c>/Subtype</c>; a form, or a name the resources lack, is not.</summary>
        private static bool IsImage(PdfDocument doc, PdfDictionary xobjects, ContentStreamOperation op, Dictionary<string, bool> memo)
        {
            PdfName name = null;
            foreach (PdfObject operand in op.Operands) if (operand is PdfName n) { name = n; break; }
            if (name == null) return false;
            if (memo.TryGetValue(name.Value, out bool known)) return known;
            bool image = doc.ResolveObject(xobjects?[name]) is PdfStream xobject
                         && PdfResolve.Name(doc, xobject, PdfName.Subtype) == PdfName.Image;
            memo[name.Value] = image;
            return image;
        }

        private static void Warn(Core.Util.DistinctStrings warnings, string text) => warnings.Add(text);
    }
}
