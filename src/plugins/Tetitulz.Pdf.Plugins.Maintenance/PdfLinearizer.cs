// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Linearizes ("web-optimizes") a PDF document so that a conforming reader can display the
    /// first page before the rest of the file has arrived — ISO 32000-1 Annex F.
    /// </summary>
    /// <remarks>
    /// <para><b>All eleven parts are written.</b> A <c>/Linearized</c> dictionary and first-page-first
    /// ordering alone are what the feature <i>looks</i> like from the outside — but with
    /// <c>/H [0 0]</c> and a single cross-reference table at the end of the file, a reader has to
    /// fetch the tail before it can resolve a single object. The first-page cross-reference table
    /// (part 3) is what makes the first fetch sufficient, and the primary hint stream (part 5) is
    /// what lets a reader find page <i>n</i> without the main table at all.</para>
    ///
    /// <para><b>The layout is verified against an independent structure checker</b>, which reads a linearized file as an
    /// independent oracle: it re-derives the linearization data and reports any
    /// disagreement, and prints what it decoded. That is the standard the
    /// tests hold this to, because a hint stream nothing reads is indistinguishable from a hint
    /// stream that is wrong.</para>
    ///
    /// <para><b>Object numbering is forced by the hint tables, not chosen.</b> Table F.4 item 1 says
    /// "the first object of the second page shall have an object number of 1", and that subsequent
    /// pages accumulate — so parts 7, 8 and 9 take the low numbers and the front matter takes the
    /// high ones. The linearization dictionary, part 4, the first page and the hint stream then form
    /// one contiguous run, which is what lets part 3 be "a single cross-reference subsection that has
    /// no free entries".</para>
    ///
    /// <para><b>Two passes, because a hint table cannot know its own length.</b> F.4 NOTE 3 resolves
    /// the circularity for us: hint-table offsets are written "as if the primary hint stream itself
    /// were not present", so they can be computed before the stream is encoded. Everything the
    /// <i>reader</i> needs in real coordinates — <c>/L</c>, <c>/H</c>, <c>/E</c>, <c>/T</c>, the
    /// first-page cross-reference entries and the trailer's <c>/Prev</c> — is written as a
    /// fixed-width placeholder and patched once the layout is known.</para>
    ///
    /// <para><b>The whole file is buffered.</b> Every object is serialised before any is placed,
    /// because a hint table records page lengths and a cross-reference table records offsets, and
    /// neither is knowable while streaming.</para>
    /// </remarks>
    public static class PdfLinearizer
    {
        /// <summary>Width of a patched-in-place numeric placeholder, in digits.</summary>
        /// <remarks>
        /// Ten, so it can hold any offset the classic cross-reference format admits
        /// (<see cref="PdfXrefFormat.MaxOffset"/>). Zero-padding is what keeps the front matter's
        /// length fixed before the values are known; a PDF integer token may carry leading zeros.
        /// </remarks>
        private const int PlaceholderWidth = 10;

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Linearizes a PDF file and writes the web-optimized result.</summary>
        /// <exception cref="InvalidOperationException">The document is signed; see <see cref="LinearizationOptions.AllowSignatureInvalidation"/>.</exception>
        public static void Linearize(string inputPath, string outputPath)
            => Linearize(inputPath, outputPath, null);

        /// <summary>Linearizes a PDF file and writes the web-optimized result.</summary>
        /// <exception cref="InvalidOperationException">The document is signed and <paramref name="options"/> does not allow invalidating the signature.</exception>
        public static void Linearize(string inputPath, string outputPath, LinearizationOptions options)
        {
            if (inputPath  == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            string outputDir  = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            string tempName   = Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string tempPath   = Path.Combine(outputDir ?? string.Empty, tempName);

            using (var fs  = File.OpenRead(inputPath))
            using (var fso = File.Create(tempPath))
            {
                try
                {
                    Linearize(fs, fso, options);
                    fso.Flush(true);
                }
                catch
                {
                    fso.Dispose();
                    try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
                    throw;
                }
            }

            try
            {
                if (File.Exists(outputPath))
                    File.Replace(tempPath, outputPath, destinationBackupFileName: null);
                else
                    File.Move(tempPath, outputPath);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                throw;
            }
        }

        /// <summary>Linearizes a PDF stream and writes the web-optimized result.</summary>
        /// <exception cref="InvalidOperationException">The document is signed; see <see cref="LinearizationOptions.AllowSignatureInvalidation"/>.</exception>
        public static void Linearize(Stream input, Stream output)
            => Linearize(input, output, null);

        /// <summary>Linearizes a PDF stream and writes the web-optimized result.</summary>
        /// <exception cref="InvalidOperationException">The document is signed and <paramref name="options"/> does not allow invalidating the signature.</exception>
        public static void Linearize(Stream input, Stream output, LinearizationOptions options)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            // Reset at the start, on every path: the slot is per thread, not per document.
            DocumentEditDiagnostics.Clear();

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Fail closed on encrypted input: linearization rewrites the file with new object
            // numbers, and per-object encryption keys are derived from the object number, so
            // the output would not be decryptable.
            if (doc.IsEncrypted)
                throw new PdfEncryptedDocumentException(
                    "Encrypted PDFs are not supported by PdfLinearizer; decrypt the document first " +
                    "(Tetitulz.Pdf.Encryption.PdfDecryptor), linearize, then re-encrypt if required.");

            if (doc.Pages.Count == 0)
                throw new PdfException("Cannot linearize a PDF with no pages.");

            // Every object gets a new number and a new offset, so a signature's /ByteRange would
            // name bytes that no longer exist: the output would look signed and verify as corrupt.
            // PdfSignatureDetection answers whether there is one.
            if (!(options?.AllowSignatureInvalidation ?? false) &&
                PdfSignatureDetection.HasSignature(doc.Trailer, n => doc.GetObject(n)))
            {
                throw new InvalidOperationException(
                    "The document carries a digital signature. Linearizing rewrites every object, so " +
                    "the signature's /ByteRange would name offsets that no longer exist and the output " +
                    "would verify as corrupt rather than merely modified. Set " +
                    "LinearizationOptions.AllowSignatureInvalidation to proceed anyway.");
            }

            LinearizeDocument(doc, output);
        }

        // ----------------------------------------------------------------
        //  Core
        // ----------------------------------------------------------------

        private static void LinearizeDocument(PdfDocument doc, Stream output)
        {
            // The reporting overload: an object that fails to parse is otherwise simply absent, every
            // reference to it renumbered into nothing, and the output a smaller file written in silence.
            // Reported rather than refused, as the optimizer does — a lightly damaged file is still
            // worth linearizing — but the caller is told.
            var allObjects = new Dictionary<int, PdfObject>();
            var unparsable = new List<int>();
            foreach (var kvp in doc.Parser.EnumerateAllObjects((number, ex) => unparsable.Add(number)))
                allObjects[kvp.Key] = kvp.Value;
            if (unparsable.Count > 0)
                DocumentEditDiagnostics.Report("Linearize", unparsable.Count + " object(s) could not be parsed and " +
                    "were left out, so every reference to them now names nothing: " + FirstNumbers(unparsable) + ".");

            RefuseWhatHasNoNumber(doc, allObjects);
            PushDownInheritedPageAttributes(doc, allObjects);

            LinearizationParts parts = LinearizationParts.Build(doc, allObjects);

            // --- object numbering -------------------------------------------------
            // Parts 7, 8 and 9 first, so page 2's first object is object 1 (Table F.4, item 1);
            // then the front matter as one contiguous run, so part 3 can be a single subsection.
            var oldToNew = new Dictionary<int, int>();
            int next = 1;
            foreach (List<int> page in parts.Part7)
                foreach (int old in page) oldToNew[old] = next++;
            foreach (int old in parts.Part8) oldToNew[old] = next++;
            foreach (int old in parts.Part9) oldToNew[old] = next++;

            int lastMainObject = next - 1;
            int linDictNum = next++;
            foreach (int old in parts.Part4) oldToNew[old] = next++;
            foreach (int old in parts.Part6) oldToNew[old] = next++;
            int hintStreamNum = next++;
            int lastObject = hintStreamNum;

            // --- serialise every object once --------------------------------------
            // Object bodies carry no byte offsets, so their lengths are final here and the layout
            // below is arithmetic rather than a third pass.
            var bytes = new Dictionary<int, byte[]>();
            foreach (var kvp in allObjects)
            {
                if (!oldToNew.TryGetValue(kvp.Key, out int newNum)) continue;
                bytes[newNum] = Serialize(newNum, RenumberObject(kvp.Value, oldToNew));
            }

            int firstPageObjNum = oldToNew.TryGetValue(parts.FirstPageObject, out int fp) ? fp : linDictNum + 1;

            var catalogRef = doc.Trailer[PdfName.Root] as PdfReference;
            PdfReference newCatalogRef =
                catalogRef != null && oldToNew.TryGetValue(catalogRef.ReferenceObjectNumber, out int cat)
                    ? new PdfReference(cat, 0)
                    : new PdfReference(linDictNum + 1, 0);

            // --- front matter (parts 1-3), with every offset still a placeholder ----
            int frontEntryCount = 1 + parts.Part4.Count + parts.Part6.Count + 1;
            FrontMatter front = BuildFrontMatter(
                doc.Version, linDictNum, firstPageObjNum, doc.Pages.Count,
                linDictNum, frontEntryCount,
                BuildFirstPageTrailer(doc.Trailer, oldToNew, newCatalogRef, lastObject + 1));

            // --- layout -----------------------------------------------------------
            // Real offsets for parts 1-4, then the hint stream, then everything after it. The hint
            // stream's own length is not known yet, so parts 6-9 are laid out in Annex F's
            // "as if the hint stream were absent" coordinates and shifted afterwards.
            var offsets = new Dictionary<int, long>();
            offsets[linDictNum] = front.LinDictOffset;
            long at = front.Bytes.Length;
            at = Place(parts.Part4, oldToNew, bytes, offsets, at);

            long hintOffset = at;                     // also part 6's hint-absent offset
            long hintAbsent = hintOffset;
            var hintAbsentOffsets = new Dictionary<int, long>();
            hintAbsent = Place(parts.Part6, oldToNew, bytes, hintAbsentOffsets, hintAbsent);
            foreach (List<int> page in parts.Part7)
                hintAbsent = Place(page, oldToNew, bytes, hintAbsentOffsets, hintAbsent);
            long sharedSectionOffset = hintAbsent;
            hintAbsent = Place(parts.Part8, oldToNew, bytes, hintAbsentOffsets, hintAbsent);
            hintAbsent = Place(parts.Part9, oldToNew, bytes, hintAbsentOffsets, hintAbsent);

            // Part 4 precedes the hint stream, so its objects' offsets are the same with the stream absent:
            // the interactive form's table locates its run there.
            var hintLocations = new Dictionary<int, long>(hintAbsentOffsets);
            foreach (int old in parts.Part4)
                if (oldToNew.TryGetValue(old, out int n) && offsets.TryGetValue(n, out long front4)) hintLocations[n] = front4;
            byte[] hintStream = BuildHintStream(
                parts, oldToNew, bytes, hintLocations, hintOffset, sharedSectionOffset,
                hintStreamNum);

            offsets[hintStreamNum] = hintOffset;
            foreach (var kvp in hintAbsentOffsets)
                offsets[kvp.Key] = kvp.Value + hintStream.Length;

            long firstPageEnd = hintOffset + hintStream.Length + Length(parts.Part6, oldToNew, bytes);
            long mainXrefOffset = hintAbsent + hintStream.Length;

            // --- assemble ---------------------------------------------------------
            var buf = new MemoryStream();
            buf.Write(front.Bytes, 0, front.Bytes.Length);
            Emit(buf, parts.Part4, oldToNew, bytes);
            buf.Write(hintStream, 0, hintStream.Length);
            Emit(buf, parts.Part6, oldToNew, bytes);
            foreach (List<int> page in parts.Part7) Emit(buf, page, oldToNew, bytes);
            Emit(buf, parts.Part8, oldToNew, bytes);
            Emit(buf, parts.Part9, oldToNew, bytes);

            if (buf.Position != mainXrefOffset)
                throw new InvalidOperationException(
                    $"Linearization layout disagrees with the bytes written ({buf.Position} vs " +
                    $"{mainXrefOffset}); the file would be unreadable.");

            long mainXrefFirstEntry = WriteMainXref(
                buf, offsets, lastMainObject, doc.Trailer, front.XrefOffset);

            // --- patch ------------------------------------------------------------
            byte[] data = buf.ToArray();
            Patch(data, front.FileLengthPos,   data.Length);
            Patch(data, front.HintOffsetPos,   hintOffset);
            Patch(data, front.HintLengthPos,   hintStream.Length);
            Patch(data, front.FirstPageEndPos, firstPageEnd);
            Patch(data, front.MainXrefPos,     mainXrefFirstEntry);
            Patch(data, front.PrevPos,         mainXrefOffset);

            for (int i = 0; i < frontEntryCount; i++)
                Patch(data, front.FirstEntryPos + i * PdfXrefFormat.EntryLength,
                      offsets.TryGetValue(linDictNum + i, out long o) ? o : 0);

            output.Write(data, 0, data.Length);
        }

        /// <summary>The first twenty object numbers, then how many more (the shape every such message has).</summary>
        private static string FirstNumbers(List<int> numbers)
        {
            const int Named = 20;
            var shown = new List<string>();
            for (int i = 0; i < numbers.Count && i < Named; i++)
                shown.Add(numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            string joined = string.Join(", ", shown);
            return numbers.Count > Named ? joined + " and " + (numbers.Count - Named) + " more" : joined;
        }

        /// <summary>
        /// Refuses a document whose catalog or a page has no object number.
        /// </summary>
        /// <remarks>
        /// Annex F names both by number: the first-page trailer's <c>/Root</c> (§7.5.5 requires the
        /// reference anyway) and the linearization dictionary's <c>/O</c>, with every page's hint-table
        /// entry beside it. A fallback to "object <c>linDictNum + 1</c>" — whatever part 4 placed first —
        /// would make a direct catalog or a page written in its parent's <c>/Kids</c> name some other
        /// object as the catalog or the first page.
        /// </remarks>
        private static void RefuseWhatHasNoNumber(PdfDocument doc, Dictionary<int, PdfObject> allObjects)
        {
            if (!(doc.Trailer[PdfName.Root] is PdfReference root) || !allObjects.ContainsKey(root.ReferenceObjectNumber))
                throw new PdfException(
                    "Cannot linearize: the trailer's /Root is not an indirect reference to an object the file " +
                    "holds, so the linearized file's trailers would have no catalog to name.");

            foreach (PdfPage page in doc.Pages)
            {
                if (page.Dictionary.ObjectNumber > 0) continue;
                throw new PdfException(
                    "Cannot linearize: page " + (page.PageIndex + 1) + " is written directly in its parent's " +
                    "/Kids, so it has no object number for the hint tables and the first-page section to name.");
            }
        }

        // ----------------------------------------------------------------
        //  Inheritable page attributes
        // ----------------------------------------------------------------

        /// <summary>
        /// Copies each inheritable attribute (Table 30) onto every page that relies on an ancestor
        /// for it.
        /// </summary>
        /// <remarks>
        /// <para><b>The page tree is in part 9, so a page that inherits is a page a reader cannot
        /// draw from the first fetch.</b> F.3.7 requires the first page object to "explicitly specify
        /// all required attributes, such as Resources and MediaBox; the attributes may not be
        /// inherited from ancestor page tree nodes", and F.3.10 says the same of every page: "all
        /// Resources attributes and other inheritable attributes of the page objects shall be pushed
        /// down and replicated in each of the leaf page objects". Both rules exist because the tree
        /// is deliberately placed at the end — "the conforming reader never needs to consult
        /// it".</para>
        ///
        /// <para><b>Then the tree node's own copy goes.</b> F.3.10 says "replicated", which reads
        /// as leaving the ancestor's copy in place — but structure checkers refuse a file that does:
        /// <i>"optimize detected an inheritable attribute when called in no-change mode"</i>. Once
        /// every leaf carries its own value the ancestor's is unreachable by inheritance and can only
        /// come to disagree with the leaves, so removing it is the reading that both satisfies the
        /// checker and cannot change what any page renders.</para>
        ///
        /// <para><b>The value is copied by reference.</b> An inherited <c>/Resources</c> that is an
        /// indirect object stays one shared dictionary rather than becoming one per page — which is
        /// also what puts it in part 6 or part 8 rather than duplicating it into every page's own
        /// section.</para>
        /// </remarks>
        private static void PushDownInheritedPageAttributes(
            PdfDocument doc, Dictionary<int, PdfObject> allObjects)
        {
            // Down /Kids, as Core inherits, not up /Parent: where a page's /Parent names another node
            // than the one whose /Kids reached it, the value climbed to would not be the value the page
            // is drawn with. The walk is the scanners' (PageTreeWalk), so the three agree.
            var catalog = ContentScanGraph.Deref(doc.Trailer[PdfName.Root], allObjects) as PdfDictionary;
            foreach (var entry in PageTreeWalk.Enumerate(allObjects, catalog))
            {
                int num = entry.ObjectNumber;
                if (num <= 0 || entry.InheritedAttributes == null) continue;
                if (!allObjects.TryGetValue(num, out PdfObject obj)) continue;
                if (!(obj is PdfDictionary dict) || obj is PdfStream) continue;

                PdfDictionary updated = null;
                foreach (PdfName key in InheritablePageKeys)
                {
                    if (dict[key] != null) continue;

                    if (!entry.InheritedAttributes.TryGetValue(key, out PdfObject inherited)) continue;

                    if (updated == null) updated = PdfDictionary.CloneShallow(dict);
                    updated[key] = inherited;
                }

                if (updated != null) allObjects[num] = updated;
            }

            foreach (int num in new List<int>(allObjects.Keys))
            {
                if (!(allObjects[num] is PdfDictionary node) || allObjects[num] is PdfStream) continue;
                if (!(node[PdfName.Type] is PdfName type) || type.Value != "Pages") continue;

                PdfDictionary stripped = null;
                foreach (PdfName key in InheritablePageKeys)
                {
                    if (node[key] == null) continue;
                    if (stripped == null) stripped = PdfDictionary.CloneShallow(node);
                    stripped.Remove(key);
                }

                if (stripped != null) allObjects[num] = stripped;
            }
        }

        /// <summary>
        /// The page attributes §7.7.3.4 marks inheritable — <c>Core.Document.PdfPageTreeShape</c>,
        /// the one statement of the set.
        /// </summary>
        /// <remarks>
        /// Core's statement records that <c>/UserUnit</c> sits beside them in Table 30 and is
        /// <b>not</b> inheritable.
        /// </remarks>
        private static IReadOnlyList<PdfName> InheritablePageKeys
            => PdfPageTreeShape.InheritableAttributes;

        // ----------------------------------------------------------------
        //  Layout helpers
        // ----------------------------------------------------------------

        private static long Place(List<int> group, Dictionary<int, int> oldToNew,
            Dictionary<int, byte[]> bytes, Dictionary<int, long> into, long at)
        {
            foreach (int old in group)
            {
                int num = oldToNew[old];
                if (!bytes.TryGetValue(num, out byte[] b)) continue;
                into[num] = at;
                at += b.Length;
            }
            return at;
        }

        private static void Emit(Stream s, List<int> group, Dictionary<int, int> oldToNew,
            Dictionary<int, byte[]> bytes)
        {
            foreach (int old in group)
                if (bytes.TryGetValue(oldToNew[old], out byte[] b)) s.Write(b, 0, b.Length);
        }

        private static long Length(List<int> group, Dictionary<int, int> oldToNew,
            Dictionary<int, byte[]> bytes)
        {
            long total = 0;
            foreach (int old in group)
                if (bytes.TryGetValue(oldToNew[old], out byte[] b)) total += b.Length;
            return total;
        }

        private static byte[] Serialize(int objNum, PdfObject obj)
        {
            var ms = new MemoryStream();
            new PdfWriter(ms).WriteIndirectObject(objNum, 0, obj);
            return ms.ToArray();
        }

        // ----------------------------------------------------------------
        //  Part 5 — the primary hint stream
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds the hint tables from the layout and serialises them as the primary hint stream.
        /// </summary>
        /// <remarks>
        /// <b>Every object of the first page is a shared object entry</b>, whether or not any other
        /// page uses it: F.4.2 says "All objects associated with the first page (part 6) shall have
        /// entries in the shared object hint table, regardless of whether they are actually shared."
        /// A later page's reference to a first-page object is then an index into that same table,
        /// which is why the first-page entries come first and the shared section's entries follow.
        /// </remarks>
        private static byte[] BuildHintStream(
            LinearizationParts parts, Dictionary<int, int> oldToNew, Dictionary<int, byte[]> bytes,
            Dictionary<int, long> hintAbsentOffsets, long firstPageOffset, long sharedSectionOffset,
            int hintStreamNum)
        {
            var groups = new List<LinearizationHintTables.SharedGroup>();
            var identifierOf = new Dictionary<int, int>();

            foreach (int old in parts.Part6)
            {
                identifierOf[old] = groups.Count;
                groups.Add(new LinearizationHintTables.SharedGroup
                {
                    Length = ByteLength(old, oldToNew, bytes),
                    ObjectCount = 1,
                });
            }

            int groupsInFirstPage = groups.Count;

            foreach (int old in parts.Part8)
            {
                identifierOf[old] = groups.Count;
                groups.Add(new LinearizationHintTables.SharedGroup
                {
                    Length = ByteLength(old, oldToNew, bytes),
                    ObjectCount = 1,
                });
            }

            var pages = new List<LinearizationHintTables.PageInfo>
            {
                new LinearizationHintTables.PageInfo
                {
                    ObjectCount = parts.Part6.Count,
                    Length = Length(parts.Part6, oldToNew, bytes),
                },
            };

            for (int i = 0; i < parts.Part7.Count; i++)
            {
                var info = new LinearizationHintTables.PageInfo
                {
                    ObjectCount = parts.Part7[i].Count,
                    Length = Length(parts.Part7[i], oldToNew, bytes),
                };
                foreach (int old in parts.SharedReferences[i])
                    if (identifierOf.TryGetValue(old, out int id)) info.SharedIdentifiers.Add(id);
                info.SharedIdentifiers.Sort();
                pages.Add(info);
            }

            int firstSharedObject = 0;
            long firstSharedOffset = 0;
            if (parts.Part8.Count > 0)
            {
                firstSharedObject = oldToNew[parts.Part8[0]];
                firstSharedOffset = sharedSectionOffset;
            }

            // Table F.2's generic hint tables (F.4.5): each category part 9 grouped, by its key.
            var generic = new List<(string, LinearizationHintTables.GenericGroup)>();
            foreach (var (key, objects) in parts.GenericGroups)
            {
                if (!hintAbsentOffsets.TryGetValue(oldToNew[objects[0]], out long at)) continue;
                List<int> identifiers = null;
                if (parts.GroupSharedReferences.TryGetValue(key, out List<int> refs))
                {
                    identifiers = new List<int>();
                    foreach (int old in refs)
                        if (identifierOf.TryGetValue(old, out int id)) identifiers.Add(id);
                    identifiers.Sort();
                }
                generic.Add((key, new LinearizationHintTables.GenericGroup
                {
                    FirstObjectNumber = oldToNew[objects[0]],
                    Location = at,
                    ObjectCount = objects.Count,
                    Length = Length(objects, oldToNew, bytes),
                    SharedIdentifiers = identifiers,
                }));
            }

            // F.4.7's embedded file stream groups, adjacent in part 9 in the order given, then the first
            // page's streams as groups of no objects: no location or length of their own, the stream
            // reached through its shared object identifier.
            var embedded = new List<LinearizationHintTables.EmbeddedFileGroup>();
            long nextGroupAt = 0;
            foreach (var (fileStream, objects, shared) in parts.EmbeddedFileGroups)
            {
                long at = nextGroupAt;
                if (objects.Count > 0 && !hintAbsentOffsets.TryGetValue(oldToNew[objects[0]], out at)) break;
                var identifiers = new List<int>();
                foreach (int old in shared)
                    if (identifierOf.TryGetValue(old, out int id)) identifiers.Add(id);
                identifiers.Sort();
                long length = objects.Count > 0 ? Length(objects, oldToNew, bytes) : 0;
                embedded.Add(new LinearizationHintTables.EmbeddedFileGroup
                {
                    StreamObjectNumber = oldToNew[fileStream],
                    Location = at,
                    ObjectCount = objects.Count,
                    Length = length,
                    SharedIdentifiers = identifiers,
                });
                nextGroupAt = at + length;
            }

            // F.4.4's thumbnail groups and their shared objects section.
            var thumbnails = new List<LinearizationHintTables.ThumbnailGroup>();
            foreach (var (pageIndex, objects) in parts.ThumbnailGroups)
            {
                if (!hintAbsentOffsets.TryGetValue(oldToNew[objects[0]], out long at)) break;
                thumbnails.Add(new LinearizationHintTables.ThumbnailGroup
                {
                    PageIndex = pageIndex,
                    FirstObjectNumber = oldToNew[objects[0]],
                    Location = at,
                    ObjectCount = objects.Count,
                    Length = Length(objects, oldToNew, bytes),
                });
            }
            LinearizationHintTables.GenericGroup thumbnailShared = null;
            if (thumbnails.Count > 0 && parts.ThumbnailShared.Count > 0
                && hintAbsentOffsets.TryGetValue(oldToNew[parts.ThumbnailShared[0]], out long sharedAt))
                thumbnailShared = new LinearizationHintTables.GenericGroup
                {
                    FirstObjectNumber = oldToNew[parts.ThumbnailShared[0]],
                    Location = sharedAt,
                    ObjectCount = parts.ThumbnailShared.Count,
                    Length = Length(parts.ThumbnailShared, oldToNew, bytes),
                };

            LinearizationHintTables tables = LinearizationHintTables.Build(
                pages, groups, firstPageOffset, firstSharedObject, firstSharedOffset,
                groupsInFirstPage, generic, embedded, thumbnails, thumbnailShared);

            var stream = new PdfStream();
            stream[PdfName.Filter] = PdfName.FlateDecode;
            stream[PdfName.S] = new PdfNumber(tables.SharedTableOffset);
            foreach (var (key, offset) in tables.GenericTableOffsets)
                stream[GenericKey(key)] = new PdfNumber(offset);
            if (tables.ThumbnailTableOffset >= 0)
                stream[PdfName.T] = new PdfNumber(tables.ThumbnailTableOffset);
            if (tables.EmbeddedFileTableOffset >= 0)
                stream[PdfName.B] = new PdfNumber(tables.EmbeddedFileTableOffset);
            stream.RawData = FlateEncodeFilter.Encode(tables.Data);

            return Serialize(hintStreamNum, stream);
        }

        /// <summary>The primary hint stream's key for a generic hint table (Table F.2).</summary>
        private static PdfName GenericKey(string key) => key switch
        {
            "O" => PdfName.O,
            "A" => PdfName.A,
            "E" => PdfName.E,
            "I" => PdfName.I,
            "L" => PdfName.L,
            "C" => PdfName.C,
            "R" => PdfName.R,
            "V" => PdfName.V,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not a generic hint table key"),
        };

        private static long ByteLength(int old, Dictionary<int, int> oldToNew,
            Dictionary<int, byte[]> bytes)
            => bytes.TryGetValue(oldToNew[old], out byte[] b) ? b.Length : 0;

        // ----------------------------------------------------------------
        //  Parts 1-3 — header, linearization dictionary, first-page xref
        // ----------------------------------------------------------------

        /// <summary>
        /// The front matter's bytes plus the positions of every value that is not knowable until
        /// the rest of the file has been laid out.
        /// </summary>
        private sealed class FrontMatter
        {
            internal byte[] Bytes;
            internal long FileLengthPos;
            internal long HintOffsetPos;
            internal long HintLengthPos;
            internal long FirstPageEndPos;
            internal long MainXrefPos;
            internal long PrevPos;
            internal long FirstEntryPos;
            internal long XrefOffset;
            internal long LinDictOffset;
        }

        private static FrontMatter BuildFrontMatter(
            PdfVersion version, int linDictNum, int firstPageObjNum, int numPages,
            int firstEntryObject, int entryCount, PdfDictionary firstPageTrailer)
        {
            var s = new MemoryStream();
            var w = new PdfWriter(s);
            var front = new FrontMatter();

            w.WriteHeader(version);

            front.LinDictOffset = s.Position;
            w.WriteRaw($"{linDictNum} 0 obj\n<<\n/Linearized 1\n/L ");
            front.FileLengthPos = s.Position;
            WritePlaceholder(w);

            w.WriteRaw("\n/H [");
            front.HintOffsetPos = s.Position;
            WritePlaceholder(w);
            w.WriteRaw(" ");
            front.HintLengthPos = s.Position;
            WritePlaceholder(w);

            w.WriteRaw($"]\n/O {firstPageObjNum}\n/E ");
            front.FirstPageEndPos = s.Position;
            WritePlaceholder(w);

            w.WriteRaw($"\n/N {numPages}\n/T ");
            front.MainXrefPos = s.Position;
            WritePlaceholder(w);

            w.WriteRaw("\n>>\nendobj\n");

            // Part 3. "A valid cross-reference table … although its position in the file shall not
            // be at the end of the file. It shall consist of a single cross-reference subsection
            // that has no free entries" — so it starts at the linearization dictionary's own object
            // number rather than at 0, and carries no entry for object 0.
            front.XrefOffset = s.Position;
            w.WriteRaw($"xref\n{firstEntryObject} {entryCount}\n");
            front.FirstEntryPos = s.Position;
            for (int i = 0; i < entryCount; i++) w.WriteRaw(PdfXrefFormat.Entry(0));

            w.WriteRaw("trailer\n<<");
            foreach (var kvp in firstPageTrailer)
            {
                w.WriteRaw(" ");
                w.WriteObject(kvp.Key);
                w.WriteRaw(" ");
                w.WriteObject(kvp.Value);
            }

            // /Prev is written here rather than through the dictionary because it must be a fixed
            // width: it names the main cross-reference table, whose offset depends on the length of
            // this very block.
            w.WriteRaw(" /Prev ");
            front.PrevPos = s.Position;
            WritePlaceholder(w);

            // F.3.11: the file's own startxref names part 3. This one is part of the first-page
            // trailer, which "may optionally end with startxref, an integer, and %%EOF … This
            // information shall be ignored." Zero is the conventional filler, and offset 0 is the
            // header, which is never a cross-reference section — so PdfRevisions does not read this
            // as a second revision.
            w.WriteRaw(" >>\nstartxref\n0\n%%EOF\n");

            front.Bytes = s.ToArray();
            return front;
        }

        /// <summary>
        /// The first-page trailer: "valid <c>Size</c> and <c>Root</c> entries, as well as any other
        /// entries needed to display the document" (F.3.4).
        /// </summary>
        /// <param name="orig">The source document's trailer.</param>
        /// <param name="oldToNew">The renumbering map, which the trailer's own references need too.</param>
        /// <param name="newCatalogRef">The catalogue in the new numbering.</param>
        /// <param name="size">
        /// One past the highest object number — "the combined number of entries in both the
        /// first-page cross-reference table and the main cross-reference table".
        /// </param>
        /// <remarks>
        /// <b>The trailer's own references are renumbered.</b> Every object in the file is given a
        /// new number, so a trailer copied entry by entry would carry <c>/Info</c> — and anything else
        /// indirect — in the <i>old</i> numbering. In a linearized file the low numbers belong to the
        /// later pages, so a stale <c>/Info</c> would not dangle where it would be noticed: it would
        /// resolve, to a page object.
        /// </remarks>
        private static PdfDictionary BuildFirstPageTrailer(
            PdfDictionary orig, Dictionary<int, int> oldToNew, PdfReference newCatalogRef, int size)
        {
            // A whitelist of what a classic trailer may carry (§7.5.5), as PdfOptimizer.BuildOutputTrailer
            // writes: the parser hands a cross-reference stream's own dictionary over as the trailer,
            // and a copy of it put /Type /XRef, /W, /Index, /Filter, /DecodeParms and /Length in front
            // of a classic xref table, a combination the standard does not permit. /Root is the new
            // catalog's below; /Size, /Prev and /XRefStm are this file's own.
            var t = new PdfDictionary();
            foreach (var kvp in orig)
            {
                if (kvp.Key != PdfName.Info && kvp.Key != PdfName.ID && kvp.Key != PdfName.Encrypt) continue;

                // A top-level reference the map does not cover named an object that is not in the
                // file. RenumberObject would leave it unchanged, and after renumbering nearly every
                // low number is taken — so the dangling reference would silently start resolving,
                // to something unrelated. Dropping the entry is the honest outcome.
                if (kvp.Value is PdfReference r && !oldToNew.ContainsKey(r.ReferenceObjectNumber))
                    continue;

                t[kvp.Key] = RenumberObject(kvp.Value, oldToNew);
            }

            t[PdfName.Size] = new PdfNumber(size);
            t[PdfName.Root] = newCatalogRef;
            return t;
        }

        // ----------------------------------------------------------------
        //  Part 11 — main cross-reference table and trailer
        // ----------------------------------------------------------------

        /// <summary>
        /// Writes the main cross-reference table and trailer, returning the offset Annex F's
        /// <c>/T</c> entry wants.
        /// </summary>
        /// <param name="s">The output buffer, positioned where the table starts.</param>
        /// <param name="offsets">Object number to byte offset, for every object written.</param>
        /// <param name="lastMainObject">
        /// The highest object number the main table covers. Everything above it is in part 3 —
        /// F.3.11 makes this table "the cross-reference table for all objects in the PDF file except
        /// those listed in the first-page cross-reference table".
        /// </param>
        /// <param name="trailer">The source trailer, for <c>/ID</c>.</param>
        /// <param name="firstPageXrefOffset">
        /// Where part 3 starts: "The startxref line shall give the offset of the first-page
        /// cross-reference table."
        /// </param>
        /// <returns>
        /// The offset of the white-space character preceding the entry for object 0 — <b>not</b> the
        /// offset of the <c>xref</c> keyword.
        /// </returns>
        /// <remarks>
        /// <b>Annex F warns about that confusion in the entry's own definition</b>: "Note that this
        /// differs from the <c>Prev</c> entry in the first-page trailer, which gives the location of
        /// the xref line that precedes the table." The <c>startxref</c> value is that <c>xref</c>
        /// line — nine bytes early on a small file and further on a large one, because the subsection
        /// header carries the object count.
        /// </remarks>
        private static long WriteMainXref(Stream s, Dictionary<int, long> offsets,
            int lastMainObject, PdfDictionary trailer, long firstPageXrefOffset)
        {
            var entries = new List<(int objNum, long offset)>();
            for (int i = 1; i <= lastMainObject; i++)
                if (offsets.TryGetValue(i, out long off)) entries.Add((i, off));

            var w = new PdfWriter(s);
            long xrefOffset = w.WriteXrefTable(entries, 0);

            int size = lastMainObject + 1;

            // The table opens with the five bytes of the xref keyword and its newline, then a single
            // subsection header, "0 <count>" and a newline. /T points at that header's terminating
            // newline: the white space preceding object 0's entry.
            long firstEntryPos = xrefOffset + 5 + $"0 {size}".Length;

            // F.3.11: "The main trailer has no Prev entry and shall not contain any entries other
            // than Size." /ID travels with it because a reader that finds the file's identity only
            // in the front matter cannot check it against a copy of the tail.
            var main = new PdfDictionary();
            main[PdfName.Size] = new PdfNumber(size);
            if (trailer[PdfName.ID] != null) main[PdfName.ID] = trailer[PdfName.ID];

            w.WriteTrailer(main, firstPageXrefOffset);
            return firstEntryPos;
        }

        // ----------------------------------------------------------------
        //  Placeholders
        // ----------------------------------------------------------------

        private static void WritePlaceholder(PdfWriter w)
            => w.WriteRaw(new string('0', PlaceholderWidth));

        private static void Patch(byte[] data, long pos, long value)
        {
            string str = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                              .PadLeft(PlaceholderWidth, '0');
            if (str.Length != PlaceholderWidth)
                throw new InvalidOperationException(
                    $"Linearization placeholder overflow: {value} does not fit in " +
                    $"{PlaceholderWidth} digits.");

            byte[] b = Encoding.ASCII.GetBytes(str);
            Array.Copy(b, 0, data, pos, PlaceholderWidth);
        }

        // ----------------------------------------------------------------
        //  Object renumbering
        // ----------------------------------------------------------------

        private static int MaxNestingDepth => PdfLimits.MaxObjectGraphDepth;

        /// <summary>Rewrites every reference in <paramref name="obj"/> into the new numbering.</summary>
        /// <remarks>
        /// <b>A reference to an object the file does not contain becomes <c>null</c>, not a copy of
        /// itself.</b> §7.3.9 already says "an indirect reference to a non-existent object shall be
        /// treated as a null object", so the substitution changes nothing a reader can observe — but
        /// carrying the number through would: every object is renumbered, so a number that pointed at
        /// nothing in the input very often points at something in the output, and a dangling
        /// reference would quietly start resolving to an unrelated object. Only a whole-file
        /// renumbering creates that hazard, which is why it does not arise in the incremental
        /// writers.
        /// </remarks>
        private static PdfObject RenumberObject(PdfObject obj, Dictionary<int, int> map, int depth = 0)
        {
            if (depth > MaxNestingDepth) return obj;
            if (obj is PdfReference r)
                return map.TryGetValue(r.ReferenceObjectNumber, out int nn)
                    ? (PdfObject)new PdfReference(nn, 0)
                    : PdfNull.Instance;
            if (obj is PdfArray arr)
            {
                var newArr = new PdfArray();
                for (int i = 0; i < arr.Count; i++)
                    newArr.Add(RenumberObject(arr[i], map, depth + 1));
                return newArr;
            }
            if (obj is PdfStream st)
            {
                var newSt = new PdfStream();
                newSt.RawData = st.RawData;
                foreach (var kvp in st)
                    newSt[kvp.Key] = RenumberObject(kvp.Value, map, depth + 1);
                return newSt;
            }
            if (obj is PdfDictionary dict)
            {
                var newDict = new PdfDictionary();
                foreach (var kvp in dict)
                    newDict[kvp.Key] = RenumberObject(kvp.Value, map, depth + 1);
                return newDict;
            }
            return obj;
        }
    }
}
