// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Reads, adds, and removes embedded file attachments.
    ///
    /// <para>
    /// PDF attachments are stored in the document catalog's /Names /EmbeddedFiles
    /// name tree.  Each entry maps a display name to a /Filespec dictionary that
    /// contains an /EF (Embedded File) stream holding the raw file bytes.
    /// </para>
    ///
    /// <para>
    /// All write operations perform an incremental update so that existing
    /// signatures and structure are preserved. Every other name tree the catalog holds
    /// (<c>/Dests</c>, <c>/JavaScript</c>, …) is carried through untouched.
    /// </para>
    ///
    /// <para>
    /// <b>Encrypted documents are refused, on both sides.</b> A write would put the new file,
    /// its name and the rebuilt tree in clear text where a reader expects per-object
    /// ciphertext. A read without a password sees the name tree's strings and the embedded
    /// streams still encrypted, so it would list names nobody wrote and extract bytes that
    /// are not the file. Read an encrypted document through the <c>PdfOperations</c>
    /// overloads that take a password.
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   // List
    ///   var files = PdfAttachmentManager.ListAttachments(inputStream);
    ///
    ///   // Add
    ///   PdfAttachmentManager.AddAttachment(inputStream, outputStream,
    ///       "report.xlsx", File.ReadAllBytes("report.xlsx"));
    ///
    ///   // Extract
    ///   byte[] data = PdfAttachmentManager.ExtractAttachment(inputStream, "report.xlsx");
    ///
    ///   // Remove
    ///   PdfAttachmentManager.RemoveAttachment(inputStream, outputStream, "report.xlsx");
    /// </code>
    /// </summary>
    public static class PdfAttachmentManager
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Returns display names of all embedded file attachments.</summary>
        public static IReadOnlyList<PdfAttachmentInfo> ListAttachments(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ListAttachments(fs);
        }

        /// <summary>Returns display names of all embedded file attachments.</summary>
        /// <remarks>
        /// A name tree too large or too deep to read in full is reported through
        /// <see cref="LastListError"/>, and the list holds the entries that were reached.
        /// </remarks>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static IReadOnlyList<PdfAttachmentInfo> ListAttachments(Stream input)
        {
            LastListError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            RefuseEncryptedRead(doc);
            return CollectAttachments(doc);
        }

        /// <summary>
        /// Embeds a file as an attachment.  If a file with the same name already
        /// exists it is replaced.
        /// </summary>
        public static void AddAttachment(string inputPath, string outputPath,
            string filename, byte[] data, string mimeType = null, string afRelationship = null)
        {
            if (inputPath  == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            using var fs  = File.OpenRead(inputPath);
            using var fso = File.Create(outputPath);
            AddAttachment(fs, fso, filename, data, mimeType, afRelationship);
        }

        /// <summary>Embeds a file as an attachment with an optional
        /// PDF 2.0 <c>/AFRelationship</c> tag. When the relationship is not null, the
        /// filespec is also added to the catalog <c>/AF</c> array so it appears as an
        /// Associated File (e.g. required for PDF/A-3 XML sidecars).</summary>
        /// <remarks>
        /// Replacing an attachment of the same name also takes the replaced file specification
        /// out of the catalog <c>/AF</c> array, so the old copy is no longer an associated file.
        /// </remarks>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        /// <exception cref="PdfException">
        /// The <c>/EmbeddedFiles</c> name tree is too large or too deep to read in full. Rewriting
        /// it would delete the entries that were not read, so nothing is written.
        /// </exception>
        public static void AddAttachment(Stream input, Stream output,
            string filename, byte[] data, string mimeType = null, string afRelationship = null)
        {
            if (input    == null) throw new ArgumentNullException(nameof(input));
            if (output   == null) throw new ArgumentNullException(nameof(output));
            if (filename == null) throw new ArgumentNullException(nameof(filename));
            if (data     == null) throw new ArgumentNullException(nameof(data));
            // Empty / whitespace-only filenames produce a malformed /Filespec
            // that some viewers display as a blank attachment row, while
            // others reject the whole file.  ExtractAttachment already
            // sanitises empty names on the read path; reject them on the
            // write path too so we never emit one in the first place.
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentException(
                    "Attachment filename must not be empty or whitespace.",
                    nameof(filename));

            // Read twice — parsed, then used as the incremental writer's base — so this one needs
            // a seekable view. `ownsBuffer` distinguishes a buffer to release from the caller's own
            // stream.
            Stream ms = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsBuffer);
            try
            {
                AddAttachmentTo(ms, output, filename, data, mimeType, afRelationship);
            }
            finally
            {
                if (ownsBuffer) ms.Dispose();
            }
        }

        private static void AddAttachmentTo(Stream ms, Stream output, string filename, byte[] data,
            string mimeType, string afRelationship)
        {
            using var doc = PdfDocument.Open(ms);
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            List<IndirectObject> objects = AddAttachmentObjects(doc, ref nextObjNum, filename, data, mimeType, afRelationship);
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
        }

        /// <summary>
        /// The objects <see cref="AddAttachment(Stream, Stream, string, byte[], string, string)"/>
        /// appends, without writing them: for a caller that writes the revision itself —
        /// <c>Tetitulz.Pdf.Encryption.PdfEncryptedAttachments</c>, which plans against a decrypted
        /// copy and appends to the encrypted original.
        /// </summary>
        /// <param name="document">The document to plan against; not an encrypted one.</param>
        /// <param name="firstFreeObjectNumber">The first object number the revision may allocate.</param>
        /// <param name="filename">The attachment's name; an attachment of that name is replaced.</param>
        /// <param name="data">The file's bytes.</param>
        /// <param name="mimeType">Its media type, or null.</param>
        /// <param name="afRelationship">Its PDF 2.0 <c>/AFRelationship</c>, or null for none.</param>
        /// <exception cref="PdfEncryptedDocumentException"><paramref name="document"/> is encrypted.</exception>
        public static IReadOnlyList<IndirectObject> PlanAddAttachment(PdfDocument document, int firstFreeObjectNumber,
            string filename, byte[] data, string mimeType = null, string afRelationship = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (filename == null) throw new ArgumentNullException(nameof(filename));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentException("Attachment filename must not be empty or whitespace.", nameof(filename));
            int next = firstFreeObjectNumber;
            return AddAttachmentObjects(document, ref next, filename, data, mimeType, afRelationship);
        }

        private static List<IndirectObject> AddAttachmentObjects(PdfDocument doc, ref int nextObjNum,
            string filename, byte[] data, string mimeType, string afRelationship)
        {
            RefuseEncryptedWrite(doc, "add an attachment to");

            var catalogRef = CatalogReference(doc);
            var catalog = ResolveCatalog(doc);

            // Read the whole tree before minting anything: a tree too large to read in full
            // refuses here, and nothing has been written.
            var entries = CatalogNameTree.ReadForRewrite(doc, catalog, PdfName.EmbeddedFiles);

            int efObjNum = nextObjNum++;
            int fsObjNum = nextObjNum++;

            // Build /EmbeddedFile stream.
            var efStream = new PdfStream();
            efStream.RawData = data;
            efStream[PdfName.Type]    = PdfName.EmbeddedFile;
            efStream[PdfName.Length]  = new PdfNumber(data.Length);
            if (mimeType != null)
                // §7.3.5 makes '#' an escape introducer that must be followed by two hex digits,
                // so replacing '/' with '#' would produce the name value `application#pdf`, which
                // PdfWriter correctly escapes to `/application#23pdf` — decoding back to the literal
                // text "application#pdf", not "application/pdf": the wrong media type for every
                // reader and a PDF/A-3 validator, invisible in a round trip whose read path mirrors
                // it. Hand the raw MIME type to
                // PdfName and let the writer escape the '/' as #2F, which the tokenizer decodes.
                efStream[PdfName.Subtype] = new PdfName(mimeType);
            var paramsDict = new PdfDictionary();
            paramsDict[PdfName.Size] = new PdfNumber(data.Length);
            // PDF Association AN002 (Associated Files): /Params should carry the modification
            // date so an AF-aware consumer can tell a stale embedded copy from a fresh one
            // without re-hashing the bytes. UTC, through the one PDF-date formatter both this
            // and the metadata/signing paths use — see PdfDate's own remarks on why a second
            // one drifted.
            paramsDict[PdfName.ModDate] = new PdfString(PdfDate.FormatUtc(DateTime.UtcNow));
            efStream[PdfName.Params] = paramsDict;

            // Build /Filespec dictionary.
            var fsDict = new PdfDictionary();
            fsDict[PdfName.Type] = PdfName.Filespec;
            fsDict[PdfName.F]    = new PdfString(filename);
            fsDict[PdfName.UF]   = new PdfString(filename);
            var efDict = new PdfDictionary();
            efDict[PdfName.F]  = new PdfReference(efObjNum, 0);
            efDict[PdfName.UF] = new PdfReference(efObjNum, 0);
            fsDict[PdfName.EF] = efDict;
            fsDict[PdfName.Desc] = new PdfString(filename);
            if (!string.IsNullOrEmpty(afRelationship))
                fsDict[PdfName.AFRelationship] = new PdfName(afRelationship);

            // Every entry of this name is replaced, and the new one joins the tree.
            var fsRef = new PdfReference(fsObjNum, 0);
            var replaced = RemoveNamed(entries, filename);
            entries.Add(new CatalogNameTree.Entry(new PdfString(filename), fsRef));

            var objects = new List<IndirectObject>
            {
                new IndirectObject(efObjNum, 0, efStream),
                new IndirectObject(fsObjNum, 0, fsDict),
            };

            // PDF 2.0 / PDF/A-3 Associated Files: the new filespec joins catalog /AF when it has a
            // relationship, and the ones it replaced leave it.
            var updatedCatalog = UpdateAssociatedFiles(doc, catalog, replaced,
                string.IsNullOrEmpty(afRelationship) ? null : fsRef, objects);
            updatedCatalog = CatalogNameTree.Write(doc, catalog, updatedCatalog,
                PdfName.EmbeddedFiles, entries, objects, ref nextObjNum);
            if (updatedCatalog != null)
                objects.Add(new IndirectObject(catalogRef.ReferenceObjectNumber, 0, updatedCatalog));
            return objects;
        }

        /// <summary>
        /// Extracts the raw bytes of an embedded attachment by display name.
        /// Returns null if the attachment is not found.
        /// </summary>
        public static byte[] ExtractAttachment(string inputPath, string filename)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ExtractAttachment(fs, filename);
        }

        /// <summary>
        /// Extracts the raw bytes of an embedded attachment by display name, whether it is
        /// reachable through the catalog's <c>/EmbeddedFiles</c> name tree or through a page's
        /// <c>/FileAttachment</c> annotation.
        /// </summary>
        /// <remarks>
        /// <para>The annotation half is visible here as it is to <see cref="ListAttachments(Stream)"/>,
        /// or the obvious loop —
        /// <c>foreach (var a in ListAttachments(s)) Save(ExtractAttachment(s, a.Filename))</c> —
        /// would return <c>null</c> for a comment attachment, the same value it returns for "no such
        /// attachment". The two halves of the public API now walk the same set.</para>
        /// <para>A <c>null</c> for a name tree too large or too deep to read in full is reported
        /// through <see cref="LastListError"/>, since the attachment may be in the part that was
        /// not read.</para>
        /// </remarks>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static byte[] ExtractAttachment(Stream input, string filename)
        {
            LastListError = null;
            if (input    == null) throw new ArgumentNullException(nameof(input));
            if (filename == null) throw new ArgumentNullException(nameof(filename));

            using var doc = PdfDocument.Open(input);
            RefuseEncryptedRead(doc);

            foreach (var entry in EnumerateFilespecs(doc))
            {
                if (entry.Name != filename) continue;

                var efStream = ResolveEmbeddedStream(doc, entry.Filespec);
                if (efStream != null) return efStream.GetDecodedData();
            }
            return null;
        }

        /// <summary>
        /// Extracts an embedded attachment and writes it safely under <paramref name="targetDirectory"/>.
        /// The stored filename is sanitised to prevent path-traversal attacks (directory components,
        /// drive letters, and absolute-path prefixes are stripped; colon/NUL/control chars removed).
        /// Returns the absolute path written, or null if the attachment is not found.
        /// </summary>
        public static string ExtractAttachmentToDirectory(Stream input, string filename, string targetDirectory)
        {
            if (input           == null) throw new ArgumentNullException(nameof(input));
            if (filename        == null) throw new ArgumentNullException(nameof(filename));
            if (targetDirectory == null) throw new ArgumentNullException(nameof(targetDirectory));

            var data = ExtractAttachment(input, filename);
            if (data == null) return null;

            Directory.CreateDirectory(targetDirectory);
            string safeName = SanitizeFilename(filename);
            string fullPath = Path.GetFullPath(Path.Combine(targetDirectory, safeName));

            // Defence-in-depth: the resolved path is strictly inside targetDirectory, by the one
            // containment test (a target written with a trailing separator is the same directory).
            if (!Core.IO.DirectoryContainment.IsInside(targetDirectory, fullPath))
                throw new Core.PdfException("Sanitized attachment path escapes target directory.");

            File.WriteAllBytes(fullPath, data);
            return fullPath;
        }

        /// <summary>
        /// Returns a filename safe for use on any filesystem — strips directory components,
        /// drive letters, NUL bytes and control characters, and rejects the reserved
        /// special names "." and "..". Falls back to "attachment.bin" if nothing remains.
        /// </summary>
        internal static string SanitizeFilename(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "attachment.bin";

            // Normalise separators and strip any directory components.
            string name = raw.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);

            // Windows drive prefix like "C:foo" — drop everything up to the colon.
            int colon = name.LastIndexOf(':');
            if (colon >= 0) name = name.Substring(colon + 1);

            // Strip NUL and control characters and any Path.GetInvalidFileNameChars.
            //
            // The `c >= 0x20` clause is REDUNDANT ON WINDOWS and load-bearing off it. Measured:
            // Path.GetInvalidFileNameChars() returns 41 characters there, including all 32 of
            // 0x00-0x1F, so a mutation deleting the clause reddens nothing on a Windows test run.
            // On Unix the set is '\0' and '/' alone (documented .NET behaviour; not measurable
            // from a Windows box), and this library supports Linux - so without the clause a
            // filename carrying 0x01-0x1F would be written to a Linux filesystem, which accepts
            // it. Kept, with the reason here, so the next mutation run does not rediscover the
            // survival and read it as dead code.
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                if (!invalid.Contains(c) && c >= 0x20) sb.Append(c);

            string cleaned = sb.ToString().Trim().TrimEnd('.');
            if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
                return "attachment.bin";

            // Windows device names are kept, on measurement (AttachmentFilenameContainmentTests):
            // an attachment genuinely called CON.pdf keeps its name, and a name that does resolve
            // to a device resolves outside the target directory, where the containment check in
            // ExtractAttachmentToDirectory refuses it.
            return cleaned;
        }

        /// <summary>
        /// Removes an embedded attachment by display name.
        /// Does nothing if the attachment is not found.
        /// </summary>
        public static void RemoveAttachment(string inputPath, string outputPath, string filename)
        {
            if (inputPath  == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            using var fs  = File.OpenRead(inputPath);
            using var fso = File.Create(outputPath);
            RemoveAttachment(fs, fso, filename);
        }

        /// <summary>
        /// Removes an embedded attachment by display name from the catalog's
        /// <c>/EmbeddedFiles</c> name tree, and its file specification from the catalog
        /// <c>/AF</c> array.
        /// </summary>
        /// <remarks>
        /// <para><b>Name-tree entries only.</b> An attachment that exists solely as a page
        /// <c>/FileAttachment</c> annotation is left in place — removing it means deleting an
        /// annotation, which is a different edit from rewriting a name tree. The method returns
        /// <c>void</c> and copies the input through unchanged in that case, so
        /// <see cref="LastRemoveDiagnostic"/> is what tells "there was no such attachment" from
        /// "it is there, as an annotation, and was not touched".</para>
        /// <para>Removing the last attachment removes the <c>/EmbeddedFiles</c> entry itself.
        /// PDF/A-1 forbids the entry whether or not the tree is empty.</para>
        /// <para><b>Not a deletion of the bytes.</b> This appends a revision, so a signature over the
        /// earlier ones survives and the attachment's bytes stay in the file, recoverable from the
        /// revision before. To be rid of them, rewrite the result: <c>Core.Document.PdfSanitizer</c> writes
        /// what is reachable and nothing else.</para>
        /// </remarks>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        /// <exception cref="PdfException">
        /// The <c>/EmbeddedFiles</c> name tree is too large or too deep to read in full. Rewriting
        /// it would delete the entries that were not read, so nothing is written.
        /// </exception>
        public static void RemoveAttachment(Stream input, Stream output, string filename)
        {
            if (input    == null) throw new ArgumentNullException(nameof(input));
            if (output   == null) throw new ArgumentNullException(nameof(output));
            if (filename == null) throw new ArgumentNullException(nameof(filename));

            // Reset at the start, on every path: a caller reads this after the call.
            LastRemoveDiagnostic = null;

            // Read twice — parsed, then copied through or used as the incremental writer's base —
            // so this one needs a seekable view. `ownsBuffer` distinguishes a buffer to release
            // from the caller's own stream, so no call site leaks one.
            Stream ms = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsBuffer);
            try
            {
                RemoveAttachmentFrom(ms, output, filename);
            }
            finally
            {
                if (ownsBuffer) ms.Dispose();
            }
        }

        private static void RemoveAttachmentFrom(Stream ms, Stream output, string filename)
        {
            using var doc = PdfDocument.Open(ms);
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            List<IndirectObject> objects = RemoveAttachmentObjects(doc, ref nextObjNum, filename);
            if (objects == null)
            {
                // Nothing to remove; copy through unchanged.
                ms.Position = 0;
                ms.CopyTo(output);
                return;
            }
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
        }

        /// <summary>
        /// The objects <see cref="RemoveAttachment(Stream, Stream, string)"/> appends, without
        /// writing them, or <c>null</c> when the name tree holds no attachment of that name (and
        /// <see cref="LastRemoveDiagnostic"/> says why, when it is an annotation's): for a caller that
        /// writes the revision itself, as <see cref="PlanAddAttachment"/> is.
        /// </summary>
        /// <exception cref="PdfEncryptedDocumentException"><paramref name="document"/> is encrypted.</exception>
        public static IReadOnlyList<IndirectObject> PlanRemoveAttachment(PdfDocument document, int firstFreeObjectNumber,
            string filename)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (filename == null) throw new ArgumentNullException(nameof(filename));
            LastRemoveDiagnostic = null;
            int next = firstFreeObjectNumber;
            return RemoveAttachmentObjects(document, ref next, filename);
        }

        private static List<IndirectObject> RemoveAttachmentObjects(PdfDocument doc, ref int nextObjNum, string filename)
        {
            RefuseEncryptedWrite(doc, "remove an attachment from");

            var catalog = ResolveCatalog(doc);
            var entries = CatalogNameTree.ReadForRewrite(doc, catalog, PdfName.EmbeddedFiles);
            var removed = RemoveNamed(entries, filename);

            if (removed.Count == 0)
            {
                foreach (var entry in EnumerateFilespecs(doc))
                    if (!entry.InNameTree && entry.Name == filename)
                    {
                        LastRemoveDiagnostic =
                            $"'{filename}' is a /FileAttachment annotation, not an /EmbeddedFiles " +
                            "name-tree entry, so it was left in place. Remove the annotation to " +
                            "delete it.";
                        break;
                    }
                return null;
            }

            var catalogRef = CatalogReference(doc);
            var objects = new List<IndirectObject>();

            var updatedCatalog = UpdateAssociatedFiles(doc, catalog, removed, null, objects);
            updatedCatalog = CatalogNameTree.Write(doc, catalog, updatedCatalog,
                PdfName.EmbeddedFiles, entries, objects, ref nextObjNum);
            if (updatedCatalog != null)
                objects.Add(new IndirectObject(catalogRef.ReferenceObjectNumber, 0, updatedCatalog));
            return objects;
        }

        // ----------------------------------------------------------------
        //  Internal helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Takes every entry named <paramref name="filename"/> out of <paramref name="entries"/>
        /// and returns their values, the file specifications that stop being attachments.
        /// </summary>
        private static List<PdfObject> RemoveNamed(List<CatalogNameTree.Entry> entries, string filename)
        {
            var removed = new List<PdfObject>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Text != filename) continue;
                removed.Add(entries[i].Value);
                entries.RemoveAt(i);
            }
            return removed;
        }

        /// <summary>
        /// Keeps the catalog's <c>/AF</c> array (§14.13) in step with the name tree: the file
        /// specifications in <paramref name="removed"/> leave it, and <paramref name="added"/>
        /// joins it when there is one.
        /// </summary>
        /// <returns>
        /// A catalog clone carrying the new array, or without <c>/AF</c> when none is left; or
        /// <c>null</c> when the catalog need not change — nothing changed, or the array is an
        /// indirect object and was superseded in place.
        /// </returns>
        /// <remarks>
        /// A removed or replaced attachment leaves <c>/AF</c> too, or a reader that presents
        /// associated files, which is the reason a PDF/A-3 document has them, would go on showing a
        /// file the caller had removed, and list a replaced one twice.
        /// </remarks>
        private static PdfDictionary UpdateAssociatedFiles(PdfDocument doc, PdfDictionary catalog,
            List<PdfObject> removed, PdfReference added, List<IndirectObject> objects)
        {
            var afObj = catalog[PdfName.AF];
            var existing = doc.ResolveObject(afObj) as PdfArray;

            var af = new PdfArray();
            bool changed = false;
            if (existing != null)
            {
                foreach (var item in existing)
                {
                    if (IsOneOf(item, removed)) { changed = true; continue; }
                    af.Add(item);
                }
            }
            if (added != null) { af.Add(added); changed = true; }
            if (!changed) return null;

            if (af.Count == 0)
            {
                // No associated files left: no /AF, rather than an empty array claiming some.
                var withoutAf = PdfDictionary.CloneShallow(catalog);
                withoutAf.Remove(PdfName.AF);
                return withoutAf;
            }

            if (afObj is PdfReference afRef && existing != null)
            {
                objects.Add(new IndirectObject(afRef.ReferenceObjectNumber, 0, af));
                return null;
            }

            var updatedCatalog = PdfDictionary.CloneShallow(catalog);
            updatedCatalog[PdfName.AF] = af;
            return updatedCatalog;
        }

        /// <summary>
        /// True when <paramref name="item"/> refers to the same file specification object as one
        /// of <paramref name="values"/>.
        /// </summary>
        /// <remarks>
        /// By object number, the only identity a file specification has across two places in the
        /// file. A direct dictionary in <c>/AF</c> is a separate object from anything in the name
        /// tree, however alike the two look, so it is never matched and is kept.
        /// </remarks>
        private static bool IsOneOf(PdfObject item, List<PdfObject> values)
        {
            if (!(item is PdfReference a)) return false;
            foreach (var value in values)
                if (value is PdfReference b && a.ReferenceObjectNumber == b.ReferenceObjectNumber) return true;
            return false;
        }

        internal static IReadOnlyList<PdfAttachmentInfo> CollectAttachments(PdfDocument doc)
        {
            var result = new List<PdfAttachmentInfo>();

            foreach (var entry in EnumerateFilespecs(doc))
            {
                var fsDict = entry.Filespec;
                var efSt = ResolveEmbeddedStream(doc, fsDict);

                long size = 0;
                PdfDictionary prm = null;
                if (efSt != null)
                {
                    prm = doc.ResolveObject(efSt[PdfName.Params]) as PdfDictionary;
                    var sz  = PdfResolve.Number(doc, prm, PdfName.Size);
                    // Table 45's /Size is an integer. A non-integer or negative one is not a
                    // size, and a `(long)` cast of an out-of-range double is long.MinValue.
                    size = sz != null && sz.IsInteger && sz.LongValue >= 0
                        ? sz.LongValue
                        : (efSt.RawData?.Length ?? 0);
                }

                // The media type lives on the /EmbeddedFile *stream* (Table 44), which is where
                // AddAttachment writes it; the /Filespec has no /Subtype. Reading only the
                // filespec meant this library never reported a MIME type it had written itself.
                // No '#'-to-'/' fix-up either: the tokenizer already decodes #2F.
                string mime = PdfResolve.Name(doc, efSt, PdfName.Subtype)?.Value
                              ?? PdfResolve.Name(doc, fsDict, PdfName.Subtype)?.Value;
                string af = PdfResolve.Name(doc, fsDict, PdfName.AFRelationship)?.Value;
                result.Add(new PdfAttachmentInfo(entry.Name, size, mime, af)
                {
                    Description  = PdfResolve.Str(doc, fsDict, PdfName.Desc)?.GetText(),
                    CreationDate = prm == null ? null : PdfResolve.Str(doc, prm, PdfName.CreationDate)?.GetText(),
                    ModDate      = prm == null ? null : PdfResolve.Str(doc, prm, PdfName.ModDate)?.GetText(),
                });
            }

            return result;
        }

        /// <summary>
        /// Each attachment's embedded file stream by the name the listing gives it — the first entry
        /// of a name, as the listing and the extraction take it; null where that entry has no stream —
        /// in one walk of the document, for a caller that reads several: a walk per attachment cost
        /// the attachments times the document's pages and entries.
        /// </summary>
        internal static Dictionary<string, PdfStream> EmbeddedStreamsByName(PdfDocument doc)
        {
            var map = new Dictionary<string, PdfStream>(StringComparer.Ordinal);
            foreach (var entry in EnumerateFilespecs(doc))
                if (entry.Name != null && !map.ContainsKey(entry.Name))
                    map[entry.Name] = ResolveEmbeddedStream(doc, entry.Filespec);
            return map;
        }

        [ThreadStatic] private static string _lastRemoveDiagnostic;
        [ThreadStatic] private static string _lastListError;

        /// <summary>
        /// Why the most recent <see cref="RemoveAttachment(Stream, Stream, string)"/> on this
        /// thread removed nothing, or <c>null</c> when it removed an entry or the name simply was
        /// not present. Reset at the start of every call, on every path.
        /// </summary>
        /// <remarks>
        /// The method returns <c>void</c>, so without this "the attachment is an annotation and
        /// was deliberately left alone" and "there is no such attachment" are the same observable
        /// outcome — an output byte-identical to the input.
        /// </remarks>
        public static string LastRemoveDiagnostic
        {
            get => _lastRemoveDiagnostic;
            private set => _lastRemoveDiagnostic = value;
        }

        /// <summary>
        /// Why the most recent <see cref="ListAttachments(Stream)"/> or
        /// <see cref="ExtractAttachment(Stream, string)"/> on this thread read fewer
        /// <c>/EmbeddedFiles</c> entries than the document holds, or <c>null</c> when it read
        /// them all. Reset at the start of every call.
        /// </summary>
        /// <remarks>
        /// <para>The name tree's walk stops at <see cref="PdfLimits.MaxNameTreeEntries"/> entries
        /// or <see cref="PdfLimits.MaxTreeDepth"/> levels. The readers returned what they had and
        /// said nothing, so a list cut short looked exactly like a document with fewer attachments,
        /// and an attachment past the limit extracted as <c>null</c>, "not found".</para>
        /// <para><c>[ThreadStatic]</c>: read it on the thread that made the call.</para>
        /// </remarks>
        public static string LastListError
        {
            get => _lastListError;
            private set => _lastListError = value;
        }

        /// <summary>One filespec an attachment is reachable through, and how it was reached.</summary>
        private readonly struct FilespecEntry
        {
            public readonly string Name;
            public readonly PdfDictionary Filespec;
            /// <summary>True when the filespec is in the catalog's /EmbeddedFiles name tree.</summary>
            public readonly bool InNameTree;

            public FilespecEntry(string name, PdfDictionary filespec, bool inNameTree)
            { Name = name; Filespec = filespec; InNameTree = inNameTree; }
        }

        /// <summary>
        /// Every attachment filespec in the document — the catalog's <c>/EmbeddedFiles</c> name
        /// tree (§7.11.4) first, then page <c>/FileAttachment</c> annotations (§12.5.6.15) —
        /// de-duplicated by filespec object number. A name tree cut short by a limit is reported
        /// through <see cref="LastListError"/>.
        /// </summary>
        /// <remarks>
        /// One walk, so <see cref="ListAttachments(Stream)"/> and
        /// <see cref="ExtractAttachment(Stream, string)"/> cannot disagree about what an
        /// attachment is. De-duplication matters because viewers write a comment attachment's
        /// filespec into <i>both</i> places, which listed the same file twice.
        /// </remarks>
        private static List<FilespecEntry> EnumerateFilespecs(PdfDocument doc)
        {
            var result = new List<FilespecEntry>();
            var seenFilespecs = new HashSet<int>();
            var catalog = ResolveCatalog(doc);

            var pairs = LoadEmbeddedFilesPairs(doc, catalog, out bool complete);
            if (!complete)
                LastListError = "the /EmbeddedFiles name tree exceeds the entry budget or the depth " +
                                "limit, so the attachments read from it are incomplete";

            foreach (var p in pairs)
            {
                var fsDict = doc.ResolveObject(p.Ref) as PdfDictionary;
                if (fsDict == null) continue;
                if (p.Ref.ReferenceObjectNumber > 0 && !seenFilespecs.Add(p.Ref.ReferenceObjectNumber))
                    continue;
                result.Add(new FilespecEntry(p.Name, fsDict, inNameTree: true));
            }

            // The one bounded annotation walk: pages may name one /Annots array, so pages × entries
            // is a product the file chooses on both sides, and the cut is said through LastListError.
            var report = new Extraction.AnnotationWalkReport(cut =>
                LastListError = (LastListError == null ? "" : LastListError + "; ") + cut +
                                ", so the attachments carried by annotations are incomplete");
            foreach (var (_, annot, _) in Extraction.AnnotationSubtypeWalk.BySubtype(doc, report, PdfAnnotationSubtypes.FileAttachment))
            {
                var fsRef = annot[PdfName.FS];
                var fsDict = doc.ResolveObject(fsRef) as PdfDictionary;
                if (fsDict == null) continue;
                if (fsRef is PdfReference r && r.ReferenceObjectNumber > 0 &&
                    !seenFilespecs.Add(r.ReferenceObjectNumber))
                    continue;

                string name = FileSpecName(doc, fsDict) ?? "attachment.bin";
                result.Add(new FilespecEntry(name, fsDict, inNameTree: false));
            }
            return result;
        }

        /// <summary>
        /// A file specification's name: <c>/UF</c> when it is present, else <c>/F</c>, each following
        /// an indirect reference; null when neither is a string.
        /// </summary>
        /// <remarks>
        /// <para>ISO 32000-2 Table 43: "A PDF reader shall use the value of the UF key, when present,
        /// instead of the F key." <c>/UF</c> is the Unicode text string; <c>/F</c> is the byte-string
        /// fallback for older readers, so for a name that is not ASCII it is the one that is wrong.
        /// The annotation walk here took <c>/F</c> first and listed a Hebrew attachment under its
        /// fallback, while <c>PdfUnencryptedWrapperReader</c> took <c>/UF</c> first — and both read
        /// raw. One reader now, for both.</para>
        /// </remarks>
        internal static string FileSpecName(PdfDocument doc, PdfDictionary fileSpec)
            => fileSpec == null
                ? null
                : (PdfResolve.Str(doc, fileSpec, PdfName.UF) ?? PdfResolve.Str(doc, fileSpec, PdfName.F))?.GetText();

        /// <summary>The /EmbeddedFile stream a filespec's /EF points at, or null.</summary>
        private static PdfStream ResolveEmbeddedStream(PdfDocument doc, PdfDictionary filespec)
        {
            var efDict = doc.ResolveObject(filespec[PdfName.EF]) as PdfDictionary;
            if (efDict == null) return null;
            var efRef = efDict[PdfName.F] ?? efDict[PdfName.UF];
            return doc.ResolveObject(efRef) as PdfStream;
        }

        /// <summary>
        /// The file specifications the catalog's <c>/EmbeddedFiles</c> name tree holds, resolved,
        /// in tree order — through the same walk the attachment listing uses.
        /// </summary>
        internal static IEnumerable<PdfDictionary> EmbeddedFileSpecs(PdfDocument doc, PdfDictionary catalog)
        {
            foreach (var pair in LoadEmbeddedFilesPairs(doc, catalog, out _))
                if (doc.ResolveObject(pair.Ref) is PdfDictionary fileSpec)
                    yield return fileSpec;
        }

        /// <param name="doc">The document to read.</param>
        /// <param name="catalog">Its catalog.</param>
        /// <param name="complete"><c>false</c> when the name tree's walk stopped at a limit.</param>
        private static List<EfPair> LoadEmbeddedFilesPairs(PdfDocument doc, PdfDictionary catalog, out bool complete)
        {
            complete = true;
            var pairs = new List<EfPair>();

            var namesObj  = doc.ResolveObject(catalog[PdfName.Names]) as PdfDictionary;
            if (namesObj == null) return pairs;

            var efObj = doc.ResolveObject(namesObj[PdfName.EmbeddedFiles]) as PdfDictionary;
            if (efObj == null) return pairs;

            complete = WalkNameTree(doc, efObj, pairs);
            return pairs;
        }

        // Traversal is Core.Document.PdfNameTree (GAPS §15c): one implementation across the three
        // rings that walk name trees, with a /Kids cycle guard and an entry budget — a two-node
        // cycle is shallow, so a depth cap alone would let it spin to MaxTreeDepth once per entry
        // point.
        //
        // The value stays an unresolved PdfReference on purpose: an embedded file is identified by
        // object number here, and resolving it would lose exactly that. Returns false when the walk
        // stopped at a limit, which the entries it did reach cannot show.
        private static bool WalkNameTree(PdfDocument doc, PdfDictionary node, List<EfPair> pairs)
        {
            var flat = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            bool complete = PdfNameTree.TryFlatten(node, doc, flat, ref budget);

            foreach (var pair in flat)
            {
                string n = PdfNameTree.KeyText(pair.Key);
                var r    = pair.Value as PdfReference;
                if (n != null && r != null)
                    pairs.Add(new EfPair(n, r));
            }
            return complete;
        }

        /// <summary>
        /// Every attachment's name and its embedded-file stream (<c>null</c> when its file
        /// specification carries none), in the order <see cref="ListAttachments(Stream)"/> lists
        /// them — one walk of a document the caller has open, for a reader that decodes each
        /// under a ceiling of its own rather than Core's.
        /// </summary>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        internal static List<(string Name, PdfStream Stream)> EmbeddedFileStreams(PdfDocument doc)
        {
            LastListError = null;
            RefuseEncryptedRead(doc);
            var files = new List<(string Name, PdfStream Stream)>();
            foreach (var entry in EnumerateFilespecs(doc))
                files.Add((entry.Name, ResolveEmbeddedStream(doc, entry.Filespec)));
            return files;
        }

        // The writers need the catalog's object number, so a /Root that is no reference is refused.
        private static PdfReference CatalogReference(PdfDocument doc)
        {
            CatalogObject.NumberOrRefuse(doc.Trailer, "the attachments");
            return (PdfReference)doc.Trailer[PdfName.Root];
        }

        // Reading needs only the dictionary: a /Root written directly in the trailer opens, and
        // its attachments are listed, as the document's text is read.
        private static PdfDictionary ResolveCatalog(PdfDocument doc)
            => doc.ResolveObject(doc.Trailer[PdfName.Root]) as PdfDictionary
               ?? throw new Core.PdfException("Cannot resolve catalog.");

        /// <summary>
        /// Refuses to write into an encrypted document: the file, its name and the rebuilt tree
        /// would be clear text where a reader expects per-object ciphertext.
        /// </summary>
        private static void RefuseEncryptedWrite(PdfDocument doc, string what)
            => EncryptedRevision.Refuse(doc, what, "the embedded file, its name and the rebuilt name tree");

        /// <summary>
        /// Refuses to read an encrypted document without its password, rather than answering with
        /// ciphertext.
        /// </summary>
        /// <remarks>
        /// <c>PdfDocument</c> loads no catalog for an encrypted file, but this class reaches the
        /// catalog through the trailer's <c>/Root</c>, so the name tree was readable and its
        /// strings and streams were not decrypted. The listing named attachments by ciphertext,
        /// and an extraction returned encrypted bytes as the file. A plausible-looking wrong
        /// answer is worse than none.
        /// </remarks>
        private static void RefuseEncryptedRead(PdfDocument doc)
        {
            if (doc.IsEncrypted)
                throw new PdfEncryptedDocumentException(
                    "The document is encrypted, so its attachment names and payloads are ciphertext " +
                    "this call cannot read: it would list names nobody wrote and extract bytes that " +
                    "are not the file. Use PdfOperations.ListAttachments(Stream, string) or " +
                    "PdfOperations.ExtractAttachment(Stream, string, string), which decrypt first " +
                    "with a registered decryptor.");
        }

        private struct EfPair
        {
            public string Name;
            public PdfReference Ref;
            public EfPair(string name, PdfReference r) { Name = name; Ref = r; }
        }
    }

    /// <summary>Metadata about an embedded file attachment.</summary>
    public sealed class PdfAttachmentInfo
    {
        /// <summary>Display name of the attachment.</summary>
        public string Filename { get; }
        /// <summary>Stored file size in bytes (0 if unknown).</summary>
        public long SizeBytes { get; }
        /// <summary>MIME type if recorded in /Subtype (may be null).</summary>
        public string MimeType { get; }
        /// <summary>PDF 2.0 Associated Files relationship (/AFRelationship) for this
        /// embedded file — one of: Source, Data, Alternative, Supplement,
        /// EncryptedPayload, FormData, Schema, Unspecified. Null when the file is
        /// not an associated file.</summary>
        public string AFRelationship { get; }
        /// <summary>The file specification's <c>/Desc</c> (Table 43), or <c>null</c>.</summary>
        public string Description { get; internal set; }
        /// <summary>The embedded file's <c>/Params /CreationDate</c> (Table 45), as written (a PDF date), or <c>null</c>.</summary>
        public string CreationDate { get; internal set; }
        /// <summary>The embedded file's <c>/Params /ModDate</c>, as written (a PDF date), or <c>null</c>.</summary>
        public string ModDate { get; internal set; }

        public PdfAttachmentInfo(string filename, long sizeBytes, string mimeType, string afRelationship = null)
        {
            Filename       = filename;
            SizeBytes      = sizeBytes;
            MimeType       = mimeType;
            AFRelationship = afRelationship;
        }

        public override string ToString() =>
            AFRelationship != null
                ? $"{Filename} ({SizeBytes} bytes, /AFRelationship {AFRelationship})"
                : $"{Filename} ({SizeBytes} bytes)";
    }
}
