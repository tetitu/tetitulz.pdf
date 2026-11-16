// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// Writes or updates document metadata via an incremental PDF update,
    /// preserving any existing content and digital signatures.
    /// </summary>
    public static class PdfMetadataWriter
    {
        // ----------------------------------------------------------------
        //  /Info dictionary
        // ----------------------------------------------------------------

        /// <summary>
        /// Writes or replaces the /Info dictionary of a PDF.
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream for the updated PDF.</param>
        /// <param name="update">Metadata values to set. Null fields are left unchanged.</param>
        public static void WriteInfo(Stream input, Stream output, PdfMetadataUpdate update)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (update == null) throw new ArgumentNullException(nameof(update));

            var ms = EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc, "the /Info strings");

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();

            // Locate existing /Info or allocate a new object number.
            var infoRef = doc.Trailer[PdfName.Info] as PdfReference;
            int infoObjNum = infoRef?.ReferenceObjectNumber ?? ++maxObjNum;

            // Clone the existing /Info and overlay, rather than rebuilding it from a fixed field
            // list.
            //
            // A REBUILD WOULD BE SILENT DATA LOSS. Starting from `new PdfDictionary()` with exactly
            // the eight keys PdfMetadata exposes, every OTHER /Info entry the document carried would
            // be dropped — and the replacement written at the SAME object number, superseding the
            // original. §14.3.3 Table 317 makes /Trapped a standard entry
            // and permits any custom key besides; this library's own PdfXConverter writes /Trapped,
            // so `WriteMetadata` after `PdfXConverter` silently broke PDF/X conformance. No test
            // caught it because a dropped key is an ABSENCE, which nothing asserted.
            //
            // Resolved, not indexed: §7.3.10 lets the trailer's /Info be an indirect reference, and
            // PdfDictionary's indexer does not follow one — reading it raw yields null, which is
            // indistinguishable from "the document has no /Info" and would have re-created the
            // very data loss this fixes. Same idiom as OutputIntentWriter.
            var existingInfo = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
            var infoDict = existingInfo == null
                ? new PdfDictionary()
                : PdfDictionary.CloneShallow(existingInfo);

            // Custom entries FIRST, so a typed property always beats the same key spelled into the
            // map. Two ways to say one thing is this repository's recurring defect; making the
            // typed one win is the rule a caller can predict, and it is the one documented on
            // PdfMetadataUpdate.Custom.
            if (update.Custom != null)
            {
                foreach (var entry in update.Custom)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    var key = new PdfName(entry.Key);
                    if (entry.Value == null) infoDict.Remove(key);
                    else infoDict.Set(key, new PdfString(entry.Value));
                }
            }

            // The named fields. A null leaves whatever the clone already carried — which is what
            // "not being overwritten" now means, rather than a hand-copied round trip through
            // doc.Metadata's eight properties.
            SetIfNotNull(infoDict, PdfName.Title,    update.Title);
            SetIfNotNull(infoDict, PdfName.Author,   update.Author);
            SetIfNotNull(infoDict, PdfName.Subject,  update.Subject);
            SetIfNotNull(infoDict, PdfName.Keywords, update.Keywords);
            SetIfNotNull(infoDict, PdfName.Creator,  update.Creator);
            SetIfNotNull(infoDict, PdfName.Producer, update.Producer);
            SetIfNotNull(infoDict, PdfName.ModDate,  update.ModDate);

            // Trailer patch: point /Info at the written object, at its own generation — an existing
            // /Info keeps its generation when superseded (§7.5.6), and `N 0 R` to an object at 1
            // names nothing. /Size is not patched here — MergeTrailer restates it last from
            // maxObjNum, which has already counted a new /Info.
            var trailerPatch = new PdfDictionary();
            trailerPatch.Set(PdfName.Info, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, infoObjNum));

            var objects = new List<IndirectObject>
            {
                new IndirectObject(infoObjNum, 0, infoDict)
            };

            AppendWithMergedTrailer(ms, output, objects, doc, trailerPatch, maxObjNum);
        }

        /// <summary>
        /// Merges <paramref name="trailerPatch"/> into the document's trailer and appends
        /// <paramref name="objects"/> as one incremental revision.
        /// </summary>
        /// <remarks>
        /// <para>The XMP writer goes through <see cref="XmpMetadataRevision"/>; <see cref="WriteInfo"/>
        /// is this helper's one caller.</para>
        ///
        /// <para><b>Two of those steps were doing nothing, and both looked like they were.</b></para>
        /// <list type="number">
        ///   <item><c>ms.Position = 0</c> — <see cref="IncrementalUpdateWriter"/>'s six-argument
        ///     <c>WriteUpdate</c> rewinds the source itself before copying it. The same dead line as
        ///     the eleven found elsewhere this round.</item>
        ///   <item><c>mergedTrailer.GetInteger(PdfName.Size) ?? (maxObjNum + 1)</c> — which reads as
        ///     "use the merged trailer's <c>/Size</c>, since the merge may have raised it". It never
        ///     has: <see cref="MergeTrailer"/>'s <i>last</i> statement sets <c>/Size</c> to exactly
        ///     the <c>newSize</c> it was handed, overriding both the original trailer and the patch.
        ///     So the expression was always <c>maxObjNum + 1</c>, and it is now written that way.</item>
        /// </list>
        ///
        /// <para>The second is why replacing it with the bare fallback changed no test — an
        /// <i>equivalent</i> mutation, not a coverage gap, and worth distinguishing: a test written
        /// to "cover" it could not have failed either.</para>
        /// </remarks>
        private static void AppendWithMergedTrailer(
            Stream source, Stream output, List<IndirectObject> objects,
            PdfDocument doc, PdfDictionary trailerPatch, int maxObjNum)
        {
            int newSize = maxObjNum + 1;

            IncrementalUpdateWriter.WriteUpdate(
                source,
                output,
                objects,
                MergeTrailer(doc.Trailer, trailerPatch, newSize),
                doc.Parser.StartXrefOffset,
                newSize);
        }

        // ----------------------------------------------------------------
        //  XMP metadata stream
        // ----------------------------------------------------------------

        /// <summary>
        /// Writes or replaces the /Metadata XMP stream in the document catalog.
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream for the updated PDF.</param>
        /// <param name="xmpXml">
        /// Well-formed XMP XML string (UTF-8).  Validated against an XML
        /// reader before writing so authors cannot inject unbalanced tags
        /// or DOCTYPE-driven XXE payloads through this entry point.
        /// </param>
        public static void WriteXmpMetadata(Stream input, Stream output, string xmpXml)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (xmpXml == null) throw new ArgumentNullException(nameof(xmpXml));

            // Validate well-formed XML up front: malformed XMP is a footgun
            // (downstream tools misparse) and lets attackers smuggle structure
            // through "</Description>"-style premature closures.  A non-
            // resolving reader with DTDs disabled blocks XXE.
            try
            {
                var settings = new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    XmlResolver   = null,
                    ConformanceLevel = System.Xml.ConformanceLevel.Document,
                };
                using var sr = new System.IO.StringReader(xmpXml);
                using var xr = System.Xml.XmlReader.Create(sr, settings);
                while (xr.Read()) { /* drain — XmlReader throws on malformed input */ }
            }
            catch (System.Xml.XmlException ex)
            {
                throw new ArgumentException(
                    "xmpXml is not well-formed XML: " + ex.Message, nameof(xmpXml), ex);
            }

            var ms = EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc, "the /Metadata stream");

            var catalogDict = doc.Catalog?.Dictionary;
            if (catalogDict == null)
                throw new Core.PdfException("PDF has no catalog dictionary.");

            // The revision is XmpMetadataRevision's, shared with PdfDeclarationsWriter and the
            // Accessibility feature's PDF/UA writer. This wrote its own: the catalog and the stream
            // at generation 0 whatever their own (under a trailer still naming the real one), and
            // a classic cross-reference table even after a section that was a stream.
            byte[] updated = XmpMetadataRevision.Append(ms.ToArray(), doc, catalogDict, xmpXml);
            output.Write(updated, 0, updated.Length);
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Fails closed on an encrypted document, before anything reads its catalog.
        /// </summary>
        /// <remarks>
        /// <para>Both writers here append objects in clear text, and an encrypted document's reader
        /// decrypts every string and stream it finds with that object's key: a title written as
        /// <c>(Report)</c> reads back as ciphertext-shaped noise, and so does a clear-text XMP
        /// packet when <c>/EncryptMetadata</c> is true. <c>WriteInfo</c> did exactly that and
        /// returned normally; <c>WriteXmpMetadata</c> threw "PDF has no catalog dictionary", which
        /// is true only because <see cref="PdfDocument"/> loads no catalog for an encrypted file,
        /// and says nothing a caller can act on. The rest of this folder already refused
        /// (<see cref="PdfIccEmbedder"/>, <see cref="PdfDeclarationsWriter"/>).</para>
        /// </remarks>
        private static void RefuseEncrypted(PdfDocument doc, string what)
            => EncryptedRevision.Refuse(doc, "write metadata into", what);

        // CopyStringField lived here: `dest.Set(key, newValue ?? existingValue)`, the "carry the old
        // value forward" half of the rebuild. Cloning the source dictionary carries EVERY key
        // forward, including the ones it had no parameter for, so the helper became the thing that
        // limited what survived rather than the thing that preserved it. Deleted rather than left
        // unused — a dead helper is how the next reader concludes the rebuild was deliberate.

        private static void SetIfNotNull(PdfDictionary dict, PdfName key, string value)
        {
            if (value != null)
                dict.Set(key, new PdfString(value));
        }

        // The carry-forward is Core.Writing.PdfTrailerBuilder's; only the patch overlay is local.
        // /Size is restated after the patch so a patch key cannot displace it.
        private static PdfDictionary MergeTrailer(
            PdfDictionary original,
            PdfDictionary patch,
            int newSize)
        {
            var merged = PdfTrailerBuilder.ForUpdate(original, newSize);

            foreach (var kvp in patch)
                merged.Set(kvp.Key, kvp.Value);

            merged.Set(PdfName.Size, new PdfNumber(newSize));
            return merged;
        }

        /// <summary>
        /// A rewound <see cref="MemoryStream"/> holding <paramref name="input"/>'s bytes, reusing
        /// the stream itself when it already is a seekable <see cref="MemoryStream"/>.
        /// <b>One implementation, in <see cref="Core.IO.SeekableStream.Buffered(Stream)"/>.</b>
        /// </summary>
        /// <remarks>
        /// <para><c>SeekableStream</c>'s own remarks name this method as one of the four it
        /// replaced — but the body was still here, hand-written and byte-for-byte the same rule,
        /// with 38 call sites across Base and three plugins reaching it. The consolidation was
        /// recorded as done and had not landed; this is the half that was missing.</para>
        ///
        /// <para><b>One contract change, in the safe direction.</b> A null argument now throws
        /// <see cref="ArgumentNullException"/> from the shared helper instead of
        /// <see cref="NullReferenceException"/> from <c>input.CanSeek</c> — the same correction
        /// <c>PdfDictionary.CloneShallow</c> made when it absorbed its copies, and an NRE was never
        /// a contract. Every other behaviour is identical: the <c>MemoryStream</c> fast path, the
        /// rewind before <c>CopyTo</c> that stops a mid-way <c>FileStream</c> producing a truncated
        /// document, and the rewound result.</para>
        /// </remarks>
        public static MemoryStream EnsureSeekable(Stream input)
            => Core.IO.SeekableStream.Buffered(input);
    }

    /// <summary>
    /// Metadata fields to write. Null means "leave existing value unchanged".
    /// </summary>
    public sealed class PdfMetadataUpdate
    {
        public string Title    { get; set; }
        public string Author   { get; set; }
        public string Subject  { get; set; }
        public string Keywords { get; set; }
        public string Creator  { get; set; }
        public string Producer { get; set; }
        /// <summary>
        /// Modification date in PDF date string format, e.g. "D:20260318120000Z".
        /// If null, not written.
        /// </summary>
        public string ModDate  { get; set; }

        /// <summary>
        /// Any other <c>/Info</c> entry, keyed <b>without</b> the leading solidus — <c>"Trapped"</c>,
        /// not <c>"/Trapped"</c>. A <see langword="null"/> value <b>removes</b> the key; an entry
        /// that is not present leaves whatever the document had.
        /// </summary>
        /// <remarks>
        /// <para>§14.3.3 Table 317 names <c>/Trapped</c> and permits any additional key, and this
        /// library's own <c>PdfXConverter</c> writes one — so the seven typed properties were never
        /// the whole vocabulary.</para>
        ///
        /// <para><b>A typed property always wins over the same key spelled here.</b> The map is
        /// applied first and the named fields overlay it, so <c>Custom["Title"]</c> cannot displace
        /// <see cref="Title"/>. Two ways to say one thing is this repository's recurring defect;
        /// this is the rule a caller can predict without reading the writer.</para>
        ///
        /// <para>Values are written as PDF strings, so <c>PdfString</c> chooses UTF-16BE for
        /// anything outside PDFDocEncoding — a Hebrew custom property survives the round trip.
        /// A <c>/Trapped</c> value is a <i>name</i> in the spec rather than a string; writing it
        /// through here produces <c>(True)</c>, which conforming readers accept as the entry being
        /// present but which a strict PDF/X validator will reject. Set it through the PDF/X
        /// converter, which writes the name form.</para>
        /// </remarks>
        public System.Collections.Generic.IDictionary<string, string> Custom { get; set; }
    }
}

