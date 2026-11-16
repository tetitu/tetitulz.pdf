// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Appends new content to one or more existing pages via an incremental PDF update.
    /// The existing page content is preserved, and the new content is added as an additional
    /// content stream.
    /// </summary>
    /// <remarks>
    /// <para><b>The new content starts in the page's initial graphics state.</b> A page's own
    /// content may leave a transformation, a clip or a colour in force, since plenty of producers
    /// write a top-level <c>cm</c> with no <c>q</c>/<c>Q</c> around it. The page's streams are
    /// therefore bracketed by a <c>q</c> stream before them and a <c>Q</c> stream after, and the
    /// appended stream follows the <c>Q</c> (see <see cref="StateIsolation"/>). This summary used
    /// to claim that isolation while the code did not do it. The callback's own
    /// <c>SaveState</c>/<c>RestoreState</c> protects only what comes after it.</para>
    /// <para><b>Its resource names never collide with the page's.</b> The builder is told every
    /// name the page already uses before the callback runs, so its <c>F1</c> or <c>GS1</c> can no
    /// longer replace the page's own.</para>
    /// </remarks>
    public static class PdfPageContentAppender
    {
        /// <summary>
        /// Appends content to the specified page.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="buildContent">
        ///   Callback that receives a <see cref="PdfContentStreamBuilder"/> and fills it
        ///   with content operators. The content starts in the page's initial graphics state;
        ///   <c>builder.SaveState()</c> / <c>builder.RestoreState()</c> are only needed to keep
        ///   the callback's own changes from anything appended after it.
        /// </param>
        /// <param name="compressStream">Whether to FlateDecode-compress the new stream.</param>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static void AppendContent(
            Stream input,
            Stream output,
            int pageIndex,
            Action<PdfContentStreamBuilder> buildContent,
            bool compressStream = false)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (buildContent == null) throw new ArgumentNullException(nameof(buildContent));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            ValidatePageIndex(doc, pageIndex);
            var page = doc.Pages[pageIndex];

            var objects = new List<IndirectObject>();
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;

            AppendToPage(page, buildContent, compressStream, ref nextObjNum, objects);

            AppendRevision(ms, output, doc, objects, nextObjNum);
        }

        /// <summary>
        /// Appends the same content to every page in the document.
        /// All page updates are bundled into a single incremental update.
        /// </summary>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static void AppendContentToAllPages(
            Stream input,
            Stream output,
            Action<PdfContentStreamBuilder, PdfPageInfo> buildContent,
            bool compressStream = false)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (buildContent == null) throw new ArgumentNullException(nameof(buildContent));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            var objects = new List<IndirectObject>();
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var isolation = new StateIsolation();
            var sharing = new ResourceSharing();

            foreach (var page in doc.Pages)
            {
                var info = GetPageInfo(page);
                AppendToPage(page, b => buildContent(b, info), compressStream, ref nextObjNum, objects,
                             null, isolation, sharing);
            }

            AppendRevision(ms, output, doc, objects, nextObjNum);
        }

        /// <summary>
        /// Appends content to each of several pages — <paramref name="buildContent"/> keyed by
        /// zero-based page index — in a single incremental update.
        /// </summary>
        /// <remarks>
        /// <para>One revision, however many pages. Appending page by page writes one revision per
        /// page, each over the whole file the one before wrote: covering 800 pages took 40 s against
        /// 2.6 s for 200, pages².</para>
        /// <para>The pages share the <c>q</c>/<c>Q</c> pair and the objects their content repeats, as <see cref="AppendContentToAllPages"/>'s do. An index
        /// that is not a page is refused with the document's range, and nothing is written. An empty
        /// map copies the input unchanged.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">A builder is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An index is not a page of the document.</exception>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static void AppendContentToPages(
            Stream input,
            Stream output,
            IReadOnlyDictionary<int, Action<PdfContentStreamBuilder>> buildContent,
            bool compressStream = false)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (buildContent == null) throw new ArgumentNullException(nameof(buildContent));

            var indices = new List<int>(buildContent.Keys);
            foreach (int index in indices)
                if (buildContent[index] == null)
                    throw new ArgumentException($"The builder for page index {index} is null.", nameof(buildContent));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            if (indices.Count == 0)
            {
                ms.CopyTo(output);
                return;
            }

            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);
            foreach (int index in indices) ValidatePageIndex(doc, index);

            var objects = new List<IndirectObject>();
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var isolation = new StateIsolation();
            var sharing = new ResourceSharing();

            foreach (int index in indices)
                AppendToPage(doc.Pages[index], buildContent[index], compressStream, ref nextObjNum, objects,
                             null, isolation, sharing);

            AppendRevision(ms, output, doc, objects, nextObjNum);
        }

        /// <summary>
        /// Refuses an encrypted document. It loads no catalog, so it has no pages: one call said
        /// "page index out of range" about a page the file has, and the other appended an empty
        /// revision and returned.
        /// </summary>
        private static void RefuseEncrypted(PdfDocument doc)
            => EncryptedRevision.Refuse(doc, "append content to", "the new content stream");

        /// <summary>
        /// The two one-operator streams that start appended content in a page's initial graphics
        /// state: <c>q</c> before the page's own streams and <c>Q</c> after them.
        /// </summary>
        /// <remarks>
        /// <para>§7.8.2 concatenates a <c>/Contents</c> array into one content stream, so a
        /// <c>q</c> in one element and its <c>Q</c> in another bracket everything between. This is
        /// how <c>PdfOcgManager</c> already brackets a page in <c>BDC</c>/<c>EMC</c>. The page's
        /// own bytes are not rewritten, so a signature over them still verifies.</para>
        /// <para>The two streams are minted once and shared by every page one revision touches: a
        /// stream is an indirect object, and any number of <c>/Contents</c> arrays may name it.</para>
        /// <para><b>Not a full reset.</b> A page whose content opens more <c>q</c> than it closes
        /// consumes the closing <c>Q</c> with its own. Counting would mean decoding and tokenising
        /// every stream on the page to protect against a malformed one.</para>
        /// </remarks>
        internal sealed class StateIsolation
        {
            private PdfReference _save;
            private PdfReference _restore;

            /// <summary>
            /// Brackets <paramref name="contents"/> in <c>q</c> … <c>Q</c>. A page with no content
            /// leaves no state behind and is left as it is.
            /// </summary>
            internal void Wrap(PdfArray contents, ref int nextObjNum, List<IndirectObject> objects)
            {
                if (contents.Count == 0) return;

                if (_save == null)
                {
                    _save = MintOperatorStream("q\n", ref nextObjNum, objects);
                    _restore = MintOperatorStream("Q\n", ref nextObjNum, objects);
                }

                contents.Insert(0, _save);
                contents.Add(_restore);
            }

            private static PdfReference MintOperatorStream(string op, ref int nextObjNum, List<IndirectObject> objects)
            {
                int number = nextObjNum++;
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes(op);
                var stream = new PdfStream { RawData = bytes };
                stream.Set(PdfName.Length, new PdfNumber(bytes.Length));
                objects.Add(new IndirectObject(number, 0, stream));
                return new PdfReference(number, 0);
            }
        }

        /// <summary>
        /// What the pages one revision stamps can share: the objects each page's content mints that
        /// are the same bytes (a stamp's font, its graphics state, its image, a stream every page
        /// draws), written once; and the merged <c>/Resources</c> of pages that start from one
        /// dictionary and add the same entries to it, written once as an indirect object they all
        /// name.
        /// </summary>
        /// <remarks>
        /// Each page was given a direct copy of its resources with the stamp's merged in, so pages
        /// inheriting one dictionary each carried the whole of it: a watermark over 800 pages
        /// sharing 800 fonts, images and states wrote 40 MB from 455 KB, pages × entries. Only an
        /// object that names no other is compared by its bytes: what an object names was minted for
        /// its own page, so its bytes match no earlier page's, and hashing it would buy nothing.
        /// </remarks>
        internal sealed class ResourceSharing
        {
            private readonly Dictionary<string, int> _objectByBytes = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<object, Dictionary<string, PdfReference>> _merged =
                new Dictionary<object, Dictionary<string, PdfReference>>(PdfReferenceEquality.Instance);
            private static readonly object NoResources = new object();

            /// <summary>Minted objects replaced by one written earlier with the same bytes.</summary>
            internal int ObjectsShared { get; private set; }

            /// <summary>Pages given a merged <c>/Resources</c> written for an earlier page.</summary>
            internal int ResourcesShared { get; private set; }

            /// <summary>
            /// <paramref name="side"/> with each object that names no other and repeats one already
            /// written dropped, and every reference in what is left — and in
            /// <paramref name="resources"/> and <paramref name="content"/> — moved to the one kept.
            /// </summary>
            internal List<(int, PdfObject)> Share(List<(int Number, PdfObject Object)> side,
                ref PdfDictionary resources, ref PdfReference content)
            {
                var moved = new Dictionary<int, int>();
                var kept = new List<(int, PdfObject)>();
                foreach (var (number, obj) in side)
                {
                    if (!NamesNoObject(obj, 0)) { kept.Add((number, obj)); continue; }
                    string key = BytesOf(obj);
                    if (_objectByBytes.TryGetValue(key, out int earlier)) { moved[number] = earlier; ObjectsShared++; continue; }
                    _objectByBytes[key] = number;
                    kept.Add((number, obj));
                }
                if (moved.Count == 0) return kept;

                var result = new List<(int, PdfObject)>(kept.Count);
                foreach (var (number, obj) in kept) result.Add((number, Moved(obj, moved, 0)));
                resources = (PdfDictionary)Moved(resources, moved, 0);
                if (content != null && moved.TryGetValue(content.ReferenceObjectNumber, out int contentTo))
                    content = new PdfReference(contentTo, 0);
                return result;
            }

            /// <summary>
            /// The <c>/Resources</c> a page starting from <paramref name="existing"/> carries with
            /// <paramref name="incoming"/> merged in: a reference to one written for an earlier page
            /// that started from the same dictionary and added the same entries, else a new one.
            /// </summary>
            internal PdfReference Merged(PdfDictionary existing, PdfDictionary incoming, PdfDocument document,
                ref int nextObjNum, List<IndirectObject> objects)
            {
                object from = (object)existing ?? NoResources;
                string adds = incoming == null ? "" : BytesOf(incoming);
                if (!_merged.TryGetValue(from, out var byAdds))
                    _merged[from] = byAdds = new Dictionary<string, PdfReference>(StringComparer.Ordinal);
                if (byAdds.TryGetValue(adds, out PdfReference known)) { ResourcesShared++; return known; }

                int number = nextObjNum++;
                objects.Add(new IndirectObject(number, 0, MergeResources(existing, incoming, document)));
                return byAdds[adds] = new PdfReference(number, 0);
            }

            private static bool NamesNoObject(PdfObject obj, int depth)
            {
                if (depth > PdfLimits.MaxObjectGraphDepth) return false;
                switch (obj)
                {
                    case PdfReference _: return false;
                    case PdfDictionary d:
                        foreach (var kvp in d) if (!NamesNoObject(kvp.Value, depth + 1)) return false;
                        return true;
                    case PdfArray a:
                        for (int i = 0; i < a.Count; i++) if (!NamesNoObject(a[i], depth + 1)) return false;
                        return true;
                    default: return true;
                }
            }

            private static PdfObject Moved(PdfObject obj, Dictionary<int, int> moved, int depth)
            {
                if (depth > PdfLimits.MaxObjectGraphDepth) return obj;
                switch (obj)
                {
                    case PdfReference r when moved.TryGetValue(r.ReferenceObjectNumber, out int to):
                        return new PdfReference(to, 0);
                    case PdfStream s:
                        foreach (var key in new List<PdfName>(s.Keys)) s.Set(key, Moved(s[key], moved, depth + 1));
                        return s;
                    case PdfDictionary d:
                        foreach (var key in new List<PdfName>(d.Keys)) d.Set(key, Moved(d[key], moved, depth + 1));
                        return d;
                    case PdfArray a:
                        for (int i = 0; i < a.Count; i++) a[i] = Moved(a[i], moved, depth + 1);
                        return a;
                    default: return obj;
                }
            }

            /// <summary>An object's bytes as a writer writes them, a stream's data included — their SHA-256, as the key.</summary>
            private static string BytesOf(PdfObject obj)
            {
                using (var ms = new MemoryStream())
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    new PdfWriter(ms).WriteObject(obj);
                    if (obj is PdfStream s && s.RawData != null) { ms.WriteByte(0); ms.Write(s.RawData, 0, s.RawData.Length); }
                    return Convert.ToBase64String(sha.ComputeHash(ms.ToArray()));
                }
            }
        }

        // ----------------------------------------------------------------
        //  Internals shared by the other Phase-4 classes
        // ----------------------------------------------------------------

        internal static void AppendToPage(
            PdfPage page,
            Action<PdfContentStreamBuilder> buildContent,
            bool compressStream,
            ref int nextObjNum,
            List<IndirectObject> objects)
            => AppendToPage(page, buildContent, compressStream, ref nextObjNum, objects, null);

        /// <summary>
        /// As above, resolving the content's references to <b>document-level</b> composite fonts
        /// through <paramref name="embeddedFontObjNums"/> (resource name → object number).
        /// </summary>
        /// <remarks>
        /// The map exists so several pages can share one font: the font's objects are written once
        /// by the caller, before any page is built, and every page's <c>/Resources /Font</c> points
        /// at them. Building the font per page instead would write one copy per page of a scanned
        /// document, which for the OCR text layer this was added for is the whole document again.
        /// </remarks>
        /// <param name="page">The page to append to.</param>
        /// <param name="buildContent">Fills the builder with the content to append.</param>
        /// <param name="compressStream">Whether to Flate-compress the new stream.</param>
        /// <param name="nextObjNum">The next free object number; advanced past any minted.</param>
        /// <param name="objects">Receives the objects the revision must carry.</param>
        /// <param name="embeddedFontObjNums">Resource name → object number of document-level composite fonts.</param>
        /// <param name="isolation">
        /// The <c>q</c>/<c>Q</c> pair to bracket the page's own content with, shared by every page one
        /// revision touches; <c>null</c> mints a pair for this page alone.
        /// </param>
        /// <param name="sharing">
        /// What the pages one revision stamps share (<see cref="ResourceSharing"/>); <c>null</c>
        /// writes this page's objects and a direct merged <c>/Resources</c> of its own.
        /// </param>
        internal static void AppendToPage(
            PdfPage page,
            Action<PdfContentStreamBuilder> buildContent,
            bool compressStream,
            ref int nextObjNum,
            List<IndirectObject> objects,
            IReadOnlyDictionary<string, int> embeddedFontObjNums,
            StateIsolation isolation = null,
            ResourceSharing sharing = null)
        {
            int pageObjNum = page.Dictionary.ObjectNumber;
            if (pageObjNum <= 0)
                throw new Core.PdfException("Page object has no object number; cannot append via incremental write.");
            RefuseUnreadableResources(page);

            var builder = new PdfContentStreamBuilder();
            // Before the callback registers anything: every name the page already uses, so the
            // merge below can never replace one of the page's own resources with ours.
            builder.ReserveResourceNames(ResourceNames(page.Resources, page.Document));
            buildContent(builder);

            var (contentRef, newResources, sideObjects) =
                builder.BuildObjects(ref nextObjNum, compressStream, embeddedFontObjNums);
            if (sharing != null) sideObjects = sharing.Share(sideObjects, ref newResources, ref contentRef);
            foreach (var (n, obj) in sideObjects)
                objects.Add(new IndirectObject(n, 0, obj));

            // Build updated page dictionary.
            var updatedPage = CloneDictionary(page.Dictionary);

            // Extend /Contents to an array that includes the original stream(s) and the new one,
            // with the originals bracketed so the new one starts in the initial graphics state.
            var contentsArray = CopyExistingContents(page.Document, page.Dictionary);
            (isolation ?? new StateIsolation()).Wrap(contentsArray, ref nextObjNum, objects);
            contentsArray.Add(contentRef);
            updatedPage.Set(PdfName.Contents, contentsArray);

            // Merge new resources into the page's own Resources entry — shared with the earlier pages
            // of this revision that started from the same dictionary and added the same entries.
            if (sharing != null)
                updatedPage.Set(PdfName.Resources, sharing.Merged(page.Resources, newResources, page.Document, ref nextObjNum, objects));
            else
                updatedPage.Set(PdfName.Resources, MergeResources(page.Resources, newResources, page.Document));

            updatedPage.ObjectNumber = pageObjNum;
            updatedPage.GenerationNumber = 0;
            objects.Add(new IndirectObject(pageObjNum, 0, updatedPage));
        }

        /// <summary>
        /// Refuses a page whose <c>/Resources</c> names an object the parser cannot read: the page
        /// reads as having none, and the merged dictionary written below would replace the
        /// reference with the stamp's entries alone — a reader that could parse the object losing
        /// the page's fonts and images. The null object answers an absent object and an unreadable
        /// one alike; the parse error, read at once, tells them apart.
        /// </summary>
        private static void RefuseUnreadableResources(PdfPage page)
        {
            PdfObject raw = page.Dictionary[PdfName.Resources];
            if (raw == null || page.Document == null) return;
            if (page.Document.ResolveObject(raw) is PdfDictionary) return;
            if (Core.Parsing.PdfParser.LastObjectParseError is string reason)
                throw new Core.PdfException(
                    "The page's /Resources could not be read (" + reason + "), so content cannot be appended " +
                    "to it without replacing the resources it has. No changes were written.");
        }

        /// <summary>
        /// The page's existing <c>/Contents</c>, spliced into a fresh array ready to receive an
        /// appended stream. Never null; empty when the page has no content.
        /// </summary>
        /// <remarks>
        /// <para><b>Resolve before deciding the shape.</b> <c>/Contents 5 0 R</c> where object 5 is
        /// an <i>array</i> is an ordinary way to write a multi-part page, and testing
        /// <c>raw is PdfReference</c> first puts that reference into the new array unexamined —
        /// producing <c>/Contents [5 0 R newRef]</c>, an array whose first element resolves to
        /// another array. §7.7.3.3 permits a stream or an array of streams, not an array of arrays,
        /// and <c>PdfPage.GetContentStreamData</c> skips any element that is not a stream — so the
        /// page's <b>entire original content vanishes from our own reader</b>, leaving only the
        /// thing that was just appended. A watermark or a form flatten over such a page erased it.
        /// Both writers had this, written out separately and wrong the same way.</para>
        ///
        /// <para>The <b>reference</b> is what goes into the array for the single-stream case, not
        /// the resolved stream: §7.3.8 makes every stream an indirect object, so an array element
        /// must be a reference.</para>
        ///
        /// <para><b>A direct <c>PdfStream</c> is deliberately dropped.</b> It cannot arise from a
        /// parsed document (§7.3.8 again), only from a hand-built dictionary, and writing one inline
        /// into an array would emit a file no reader accepts.</para>
        /// </remarks>
        internal static PdfArray CopyExistingContents(PdfDocument doc, PdfDictionary page)
        {
            var result = new PdfArray();

            var raw = page?[PdfName.Contents];
            if (raw == null) return result;

            var resolved = doc?.ResolveObject(raw) ?? raw;
            if (resolved is PdfArray existing)
            {
                for (int i = 0; i < existing.Count; i++) result.Add(existing[i]);
                return result;
            }

            if (raw is PdfReference singleRef) result.Add(singleRef);
            return result;
        }

        /// <summary>
        /// Merges <paramref name="incoming"/> into a copy of <paramref name="existing"/>, entry by
        /// entry for the sub-dictionaries (<c>/Font</c>, <c>/XObject</c>, <c>/ExtGState</c>).
        /// </summary>
        /// <param name="document">
        /// Resolves an indirect sub-dictionary. <b>Load-bearing:</b> a resource sub-dictionary is
        /// routinely written as a reference — one <c>/Font</c> object shared by every page is the
        /// ordinary way to build a document — and without resolving, the
        /// <c>is PdfDictionary</c> test below fails and the <c>else</c> branch <b>replaces</b> the
        /// page's whole sub-dictionary with the incoming one. Adding a single font to stamp a
        /// watermark therefore removed every font the page already used, leaving its own text
        /// pointing at resource names that no longer exist. May be null, which restores the old
        /// behaviour and is why it is not optional at the call sites.
        /// </param>
        /// <param name="existing">The page's current <c>/Resources</c>, or null.</param>
        /// <param name="incoming">
        /// Resources the appended content needs. Merged per sub-dictionary, never assigned over the
        /// top — see the remarks: replacing wholesale is what removed a page's own fonts.
        /// </param>
        internal static PdfDictionary MergeResources(
            PdfDictionary existing, PdfDictionary incoming, PdfDocument document)
        {
            var merged = existing != null ? CloneDictionary(existing) : new PdfDictionary();
            if (incoming == null) return merged;

            foreach (var kvp in incoming)
            {
                // Sub-dictionaries (/Font, /XObject, /ExtGState) are merged entry-by-entry.
                // Resolved, because the existing one is usually a reference; see the doc comment.
                var existingValue = document?.ResolveObject(merged[kvp.Key]) ?? merged[kvp.Key];
                var incomingValue = document?.ResolveObject(kvp.Value) ?? kvp.Value;
                if (existingValue is PdfDictionary existingSub && incomingValue is PdfDictionary newSub)
                {
                    var mergedSub = CloneDictionary(existingSub);
                    foreach (var sub in newSub)
                        mergedSub.Set(sub.Key, sub.Value);
                    merged.Set(kvp.Key, mergedSub);
                }
                else
                {
                    merged.Set(kvp.Key, kvp.Value);
                }
            }
            return merged;
        }

        /// <summary>
        /// Every name a <c>/Resources</c> dictionary defines, across its sub-dictionaries
        /// (<c>/Font</c>, <c>/XObject</c>, <c>/ExtGState</c>, <c>/Properties</c>, …), each resolved.
        /// </summary>
        /// <param name="resources">The page's effective resources, or null.</param>
        /// <param name="document">Resolves an indirect sub-dictionary; may be null.</param>
        internal static IEnumerable<string> ResourceNames(PdfDictionary resources, PdfDocument document)
        {
            if (resources == null) yield break;
            foreach (var category in resources)
            {
                var sub = document?.ResolveObject(category.Value) ?? category.Value;
                if (!(sub is PdfDictionary names)) continue;     // /ProcSet is an array
                foreach (var entry in names)
                    yield return entry.Key.Value;
            }
        }

        /// <summary>
        /// <paramref name="prefix"/> and the first number from <paramref name="from"/> up that names
        /// nothing in <paramref name="resources"/> (<see cref="ResourceNames"/>, every category) — the
        /// one answer to "a resource name the page does not use" for every writer that adds a font or
        /// an image to an existing page's resources: a name the page uses would replace the page's
        /// own, and one an earlier edit added would replace that edit's.
        /// </summary>
        internal static string FreeResourceName(PdfDictionary resources, PdfDocument document, string prefix, int from)
        {
            var taken = new HashSet<string>(ResourceNames(resources, document), StringComparer.Ordinal);
            for (int n = from; ; n++)
            {
                string name = prefix + n.ToString(CultureInfo.InvariantCulture);
                if (!taken.Contains(name)) return name;
            }
        }

        /// <summary>
        /// The page as a reader shows it — the <c>/CropBox</c>, else the <c>/MediaBox</c>, with its
        /// origin (the one reading of a page box, <c>PageBoxMetrics</c>), and its <c>/Rotate</c> — for a
        /// callback placing content where the reader looks: a box whose corner is not the origin,
        /// or a page shown turned, put a stamp placed by the extent alone off the shown page.
        /// </summary>
        internal static PdfPageInfo GetPageInfo(PdfPage page)
        {
            var box = Rendering.PageBoxMetrics.Of(page.CropBox ?? page.MediaBox);
            int rotation = Core.Document.PdfPageRotation.Normalize(page.Rotate, out _);
            return new PdfPageInfo(page.PageIndex, box.X, box.Y, box.Width, box.Height, rotation);
        }

        // One implementation, in PdfHelpers.ValidatePageIndex — three byte-identical copies
        // existed, including the interpolated message. nameof resolves to the same literal
        // there, so the exception ParamName is unchanged.
        private static void ValidatePageIndex(PdfDocument doc, int pageIndex)
            => PdfHelpers.ValidatePageIndex(doc, pageIndex);

        // One implementation, in Core.Objects.PdfDictionary.CloneShallow.
        public static PdfDictionary CloneDictionary(PdfDictionary source)
            => PdfDictionary.CloneShallow(source);

        /// <summary>
        /// Deep clone: nested dictionaries and arrays are copied, bounded at
        /// <paramref name="maxDepth"/> levels so a cyclic graph terminates instead of
        /// overflowing the stack. Delegates to <see cref="PdfDictionary.DeepClone"/>.
        ///
        /// <para><b>A stream is not cloned as a plain dictionary</b>: a type switch testing
        /// PdfDictionary before PdfStream would lose the stream data, since a stream IS a
        /// dictionary. The shared implementation tests the derived type
        /// first.</para>
        /// </summary>
        public static PdfDictionary DeepCloneDictionary(PdfDictionary source, int maxDepth = 64)
            => PdfDictionary.DeepClone(source, maxDepth);

        /// <summary>
        /// Builds the trailer for a revision appended to <paramref name="original"/>'s document.
        /// </summary>
        /// <remarks>
        /// One implementation, in <see cref="PdfTrailerBuilder.ForUpdate"/>. This method is the
        /// de-facto shared one already — twelve call sites across <c>Base.Operations</c> and the
        /// Conformance plugin — and it is kept as the entry point they name; six further private
        /// copies elsewhere in <c>Base</c> now route here too. <c>/Prev</c> is left to
        /// <see cref="IncrementalUpdateWriter"/>, which every caller feeds and which applies the
        /// guarded rule to whatever trailer it is handed.
        /// </remarks>
        public static PdfDictionary BuildTrailer(PdfDictionary original, int newSize)
            => PdfTrailerBuilder.ForUpdate(original, newSize);

        /// <summary>
        /// Appends <paramref name="objects"/> to <paramref name="output"/> as one incremental
        /// revision, after the document <paramref name="source"/> holds.
        /// </summary>
        /// <param name="source">
        /// The buffer holding the document as it stands. Its position does not matter — see the
        /// remarks.
        /// </param>
        /// <param name="output">The caller's stream, which receives the original bytes plus the
        /// appended revision.</param>
        /// <param name="doc">The document being revised, supplying the trailer to derive from and
        /// the previous xref offset the new one chains to.</param>
        /// <param name="objects">The objects the revision carries, already numbered.</param>
        /// <param name="nextObjectNumber">
        /// One past the highest object number in use, written as the new <c>/Size</c>.
        /// </param>
        /// <param name="trailer">
        /// A trailer to write instead of the one <see cref="BuildTrailer"/> would derive. Only the
        /// caller that mints an <c>/Info</c> the trailer must point at needs this; everyone else
        /// leaves it null.
        /// </param>
        /// <remarks>
        /// <para><b>Why this is a method.</b> The sequence — derive the trailer, rewind the source,
        /// write the update at the previous xref offset with the new <c>/Size</c> — was written out
        /// eleven times across <c>Base.Operations</c> and the Conformance plugin, identically apart
        /// from line wrapping.
        /// </para>
        ///
        /// <para><b>One of those four lines did nothing, and eleven call sites carried it.</b> Every
        /// copy set <c>ms.Position = 0</c> before calling
        /// <see cref="IncrementalUpdateWriter.WriteUpdate(Stream, Stream, List{IndirectObject},
        /// PdfDictionary, long, int, IPdfObjectEncryptor)"/> — which does exactly that itself,
        /// immediately before copying the original through, and whose only earlier read of the
        /// stream saves and restores the position around an absolute seek. So the caller's rewind
        /// was redundant at all eleven sites, and it is not repeated here.</para>
        ///
        /// <para>It reads as the load-bearing line of the four, which is presumably why it was
        /// copied so faithfully — a call site that <i>did</i> have to rewind and forgot would emit a
        /// revision appended to nothing, with no exception and no failed assertion, just a broken
        /// PDF. That is a good reason to check rather than to assume: deleting the rewind from this
        /// helper leaves all 3 022 Base and 495 Conformance tests green, which is what a line that
        /// never did anything looks like, and is <b>not</b> the same as a coverage gap. The evidence
        /// that it is dead is <c>IncrementalUpdateWriter</c>'s own code, not the green run.</para>
        ///
        /// <para><b>Two callers deliberately do not use this.</b>
        /// <c>PdfImageReplacer</c> builds a <i>plan</i> — it derives the trailer and the size and
        /// carries them to a write that happens elsewhere, because the caller may still reject the
        /// replacement. And the encrypted paths go through
        /// <c>Encryption.PdfEncryptedIncrementalUpdate</c> instead, which needs the document's own
        /// key. Neither is a site that forgot to be converted.</para>
        /// </remarks>
        public static WriteResult AppendRevision(
            Stream source,
            Stream output,
            PdfDocument doc,
            List<IndirectObject> objects,
            int nextObjectNumber,
            PdfDictionary trailer = null)
        {
            // The backstop for every caller: a writer that refuses earlier names its own action,
            // and one that did not (the OCR layer, two Conformance repairs) still writes nothing.
            EncryptedRevision.Refuse(doc, "append a revision to", "the revision");
            return IncrementalUpdateWriter.WriteUpdate(
                source, output, objects,
                trailer ?? BuildTrailer(doc.Trailer, nextObjectNumber),
                doc.Parser.StartXrefOffset, nextObjectNumber);
        }
    }

    /// <summary>
    /// Information about a page passed to the content-building callback: the box a reader shows
    /// (the <c>/CropBox</c>, else the <c>/MediaBox</c>), its corner and extent in the page's own
    /// space, and the page's <c>/Rotate</c>.
    /// </summary>
    public sealed class PdfPageInfo
    {
        public int PageIndex { get; }
        /// <summary>The shown box's width in PDF points, in the page's own space (before <see cref="Rotation"/>).</summary>
        public double Width { get; }
        /// <summary>The shown box's height in PDF points, in the page's own space (before <see cref="Rotation"/>).</summary>
        public double Height { get; }
        /// <summary>The shown box's lower-left x in PDF points; 0 for a box at the origin.</summary>
        public double X { get; }
        /// <summary>The shown box's lower-left y in PDF points; 0 for a box at the origin.</summary>
        public double Y { get; }
        /// <summary>The page's <c>/Rotate</c>, normalised to 0, 90, 180 or 270 (§7.7.3.3).</summary>
        public int Rotation { get; }

        /// <summary>The width of the page as shown: <see cref="Height"/> under a quarter turn.</summary>
        public double ShownWidth => Rotation == 90 || Rotation == 270 ? Height : Width;
        /// <summary>The height of the page as shown: <see cref="Width"/> under a quarter turn.</summary>
        public double ShownHeight => Rotation == 90 || Rotation == 270 ? Width : Height;

        public PdfPageInfo(int pageIndex, double width, double height)
            : this(pageIndex, 0, 0, width, height, 0)
        {
        }

        public PdfPageInfo(int pageIndex, double x, double y, double width, double height, int rotation)
        {
            PageIndex = pageIndex;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Rotation = rotation;
        }

        /// <summary>
        /// The text matrix for a line starting at (<paramref name="shownX"/>, <paramref name="shownY"/>)
        /// of the page <b>as shown</b> — measured from the shown page's lower-left corner — and read
        /// left to right as shown: on a turned page the line runs along the page's own axis the turn
        /// maps to the shown baseline, which is how the viewer and the text writer place content.
        /// </summary>
        internal (double A, double B, double C, double D, double E, double F) TextMatrixAsShown(double shownX, double shownY)
        {
            switch (Rotation)
            {
                case 90:  return (0, 1, -1, 0, X + Width - shownY, Y + shownX);
                case 180: return (-1, 0, 0, -1, X + Width - shownX, Y + Height - shownY);
                case 270: return (0, -1, 1, 0, X + shownY, Y + Height - shownX);
                default:  return (1, 0, 0, 1, X + shownX, Y + shownY);
            }
        }
    }
}


