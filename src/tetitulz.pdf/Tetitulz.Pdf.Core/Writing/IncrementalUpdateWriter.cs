// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The form an appended cross-reference section takes.
    /// </summary>
    public enum XrefSectionFormat
    {
        /// <summary>
        /// Match the section being superseded: a classic <c>xref</c> table when the previous
        /// section is one, a cross-reference stream when it is one. This is the only setting
        /// that cannot produce a hybrid file, so it is the default.
        /// </summary>
        Auto = 0,

        /// <summary>Always append a classic <c>xref</c> table + <c>trailer</c> dictionary.</summary>
        ClassicTable = 1,

        /// <summary>Always append a <c>/Type /XRef</c> cross-reference stream (PDF 1.5+).</summary>
        CrossReferenceStream = 2,
    }

    /// <summary>
    /// Everything the format-aware
    /// <see cref="IncrementalUpdateWriter.WriteUpdate(byte[], Stream, IReadOnlyList{IndirectObject}, IncrementalUpdateOptions)"/>
    /// overload needs beyond the objects themselves. Every property has a usable default, so a
    /// caller that only wants "append these objects correctly" can pass <c>new IncrementalUpdateOptions()</c>.
    /// </summary>
    public sealed class IncrementalUpdateOptions
    {
        /// <summary>
        /// The parsed original. Used — when supplied — for the trailer, the previous
        /// cross-reference offset, the highest existing object number, and (only when no usable
        /// <c>/Prev</c> exists) the offsets of the objects the appended section has to re-declare.
        /// May be <see langword="null"/>.
        /// </summary>
        public PdfDocument Document { get; set; }

        /// <summary>
        /// Trailer to take <c>/Root</c>, <c>/Info</c>, <c>/ID</c> and <c>/Encrypt</c> from.
        /// Defaults to <see cref="Document"/>'s trailer. Only those four keys are carried
        /// forward: the parser merges an xref <i>stream</i>'s dictionary into the trailer, so
        /// copying it wholesale would drag <c>/Type</c>, <c>/W</c>, <c>/Index</c>,
        /// <c>/Filter</c> and a stale <c>/Length</c> into the appended section.
        /// </summary>
        public PdfDictionary OriginalTrailer { get; set; }

        /// <summary>
        /// Offset of the cross-reference section this revision supersedes. Negative (the
        /// default) means "discover it": the parser's <c>startxref</c> value is used when it
        /// really points at a cross-reference section, otherwise the file tail is scanned for
        /// the literal <c>startxref</c> token. The value is verified either way — a
        /// <c>/Prev</c> that points at nothing breaks the chain and makes every object of the
        /// original revision unreachable.
        /// </summary>
        public long PreviousXrefOffset { get; set; } = -1;

        /// <summary>Form of the appended section. <see cref="XrefSectionFormat.Auto"/> by default.</summary>
        public XrefSectionFormat Format { get; set; } = XrefSectionFormat.Auto;

        /// <summary>
        /// Floor for the trailer <c>/Size</c>. The effective value is always at least one more
        /// than the highest object number written, and at least one more than the highest object
        /// number <see cref="Document"/> declares, so a caller that does not know either can
        /// leave this at 0.
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// Object number of a freshly written <c>/Info</c> dictionary, used only when
        /// <see cref="OriginalTrailer"/> has no <c>/Info</c>. Not applicable when &lt;= 0.
        /// </summary>
        public int NewInfoObjectNumber { get; set; } = -1;

        /// <summary>
        /// When no usable <c>/Prev</c> exists, re-declare every object the original document
        /// defines at the offset the parser recovered, so the appended section is a complete
        /// table. Without it a reader following <c>startxref</c> would find only the handful of
        /// objects this revision rewrites — no catalog, no page tree.
        /// </summary>
        public bool ReDeclareOriginalObjectsWhenUnchained { get; set; } = true;

        /// <summary>
        /// Emit a newline before the revision when the original does not end with one, so the
        /// appended object header cannot run onto the same line as the original's <c>%%EOF</c>.
        /// </summary>
        public bool EnsureSeparatorBeforeRevision { get; set; } = true;

        /// <summary>
        /// Picks the object number for the cross-reference stream, given the highest object
        /// number this revision writes. Only consulted when a cross-reference stream is
        /// actually emitted. Defaults to one more than the highest number known to either the
        /// revision or <see cref="Document"/>.
        /// </summary>
        public Func<int, int> XrefStreamObjectNumberAllocator { get; set; }

        /// <summary>
        /// Resolves an already-parsed object to its object number (0 when unknown). Used only
        /// to recover a trailer whose <c>/Root</c> or <c>/Info</c> is a direct dictionary where
        /// an indirect reference belongs. Defaults to <see cref="PdfObject.ObjectNumber"/>.
        /// </summary>
        public Func<PdfObject, int> ObjectNumberResolver { get; set; }

        /// <summary>
        /// Resolves the generation of an existing object number. Used with
        /// <see cref="ObjectNumberResolver"/>; defaults to the document's cross-reference table.
        /// </summary>
        public Func<int, int> GenerationResolver { get; set; }

        /// <summary>
        /// Receives <c>(operation, detail, exception)</c> for every recoverable anomaly. The
        /// writer never swallows a problem silently; with no sink attached the detail is simply
        /// dropped. The exception argument is <see langword="null"/> for anomalies that are not
        /// an exception.
        /// </summary>
        public Action<string, string, Exception> DiagnosticSink { get; set; }

        /// <summary>
        /// Encrypts the appended objects with the original document's file encryption key, so a
        /// revision can be appended to an <b>encrypted</b> document. Without it the appended
        /// objects are written in clear text, which an encrypted document's reader decrypts into
        /// garbage — the reason every writer here refused an encrypted input.
        ///
        /// <para>Build one with
        /// <c>Tetitulz.Pdf.Encryption.PdfIncrementalEncryption.TryCreate</c>; leave it null for an
        /// unencrypted document.</para>
        /// </summary>
        public IPdfObjectEncryptor ObjectEncryptor { get; set; }

        /// <summary>
        /// Seals the appended revision with a standalone PDF MAC token (ISO/TS 32004 §6.5.1): its
        /// trailer, or cross-reference stream dictionary, carries an <c>/AuthCode</c> whose
        /// <c>/ByteRange</c> covers the whole file. A document protected by PDF MAC needs this on every
        /// revision — the previous token no longer covers the file once anything is appended.
        ///
        /// <para>The output stream must be seekable, readable and writable, with the PDF starting at
        /// its position when the options overload of <c>WriteUpdate</c> is called, which must be
        /// 0. Leave it null for a document without PDF MAC.</para>
        /// </summary>
        public StandalonePdfMacSeal PdfMac { get; set; }

        /// <summary>
        /// A <b>finished</b> <c>AuthCode</c> dictionary to write into the appended revision's
        /// trailer (or cross-reference stream dictionary) verbatim — ISO/TS 32004 §6.5.2's
        /// <c>AttachedToSig</c> placement, where the PDF MAC token lives inside a signature's CMS
        /// and the dictionary only says so.
        ///
        /// <para><b>Why this is not <see cref="PdfMac"/> with a different payload.</b> That property
        /// writes a <i>placeholder</i> and seals the finished file, because a standalone token
        /// digests bytes that do not exist until the write is over. Table 6's other placement has
        /// neither problem: <c>AttachedToSig</c> forbids both <c>/ByteRange</c> and <c>/MAC</c>, so
        /// the dictionary is nothing but <c>/MACLocation</c> and <c>/SigObjRef</c> — and both are
        /// known before a byte is written, because the caller allocated the signature's object
        /// number itself. Nothing is reserved, nothing is patched, and no second pass over the
        /// output is needed.</para>
        ///
        /// <para><b>Set at most one of the two.</b> A revision has one <c>AuthCode</c> dictionary,
        /// and the two placements are alternatives by Table 6's own conditional-presence rules;
        /// setting both is a caller error and is refused rather than resolved by precedence, which
        /// would silently drop one document's protection.</para>
        /// </summary>
        public PdfDictionary AuthCode { get; set; }
    }

    /// <summary>
    /// Appends a single revision (an incremental update) to an existing PDF.
    ///
    /// <para>
    /// Two overloads, deliberately different:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="WriteUpdate(Stream, Stream, List{IndirectObject}, PdfDictionary, long, int, IPdfObjectEncryptor)"/>
    ///     is the original, shipped signature. It copies the caller's trailer verbatim and
    ///     always writes a classic <c>xref</c> table.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="WriteUpdate(byte[], Stream, IReadOnlyList{IndirectObject}, IncrementalUpdateOptions)"/>
    ///     matches the form of the section it supersedes. Appending a classic table to a
    ///     document whose cross-reference section is a <i>stream</i> produces a hybrid file:
    ///     Lenient viewers tolerate it, but strict validators reject it and a reader
    ///     that only understands xref streams cannot follow the chain at all. It also carries
    ///     <c>/ID</c> forward (synthesising a deterministic one when the original has none),
    ///     whitelists the trailer keys, and refuses to write a <c>/Prev</c> it cannot verify.
    ///   </description></item>
    /// </list>
    /// </summary>
    public sealed class IncrementalUpdateWriter
    {
        /// <summary>
        /// Largest value a cross-reference generation field can hold (PDF 32000-1:2008, 7.5.4).
        /// </summary>
        private const int MaxGenerationNumber = Constants.PdfXrefFormat.MaxGenerationNumber;

        /// <summary>
        /// Length, in bytes, of each half of the trailer's <c>/ID</c> array — PDF 32000-1:2008,
        /// 14.4 specifies a 16-byte (128-bit) file identifier.
        /// </summary>
        private const int IdHalfLengthBytes = 16;

        /// <summary>
        /// How far past an <c>N G obj</c> header the probe looks for <c>/Type /XRef</c>. Bounded
        /// so a hostile <c>startxref</c> offset cannot make the classifier walk the whole file.
        /// </summary>
        private const int XrefStreamProbeWindowBytes = 4096;

        /// <summary>
        /// How much the streaming classifier reads at a candidate offset — the probe window plus
        /// room for the <c>N G obj</c> header that precedes it, which is what makes a windowed
        /// classification identical to a whole-file one rather than merely similar.
        /// </summary>
        private const int XrefSectionProbeBytes = XrefStreamProbeWindowBytes + 64;

        /// <summary>
        /// How much of the file tail is searched for the literal <c>startxref</c> token. The
        /// keyword and its offset always sit in the last few dozen bytes of a well-formed file.
        /// </summary>
        private const int StartXrefScanWindowBytes = 4096;

        /// <summary>
        /// Appends an incremental update to an existing PDF.
        /// </summary>
        /// <param name="originalPdf">Stream containing the original PDF (will be read and copied).</param>
        /// <param name="outputPdf">Stream to write the complete output (original + update).</param>
        /// <param name="modifiedObjects">List of new/modified objects to append.</param>
        /// <param name="originalTrailer">Existing trailer; every entry except /Prev is copied.</param>
        /// <param name="previousXrefOffset">Offset of the previous xref table (for /Prev in new trailer).</param>
        /// <param name="size">Total number of objects (/Size in trailer).</param>
        /// <param name="objectEncryptor">
        /// Encrypts the appended objects with the original document's file encryption key, for a
        /// revision appended to an encrypted document. Null — the default, and what every existing
        /// caller passes — writes clear text and leaves the output byte-for-byte as it was.
        /// </param>
        /// <returns>Offsets of written objects.</returns>
        public static WriteResult WriteUpdate(
            Stream originalPdf,
            Stream outputPdf,
            List<IndirectObject> modifiedObjects,
            PdfDictionary originalTrailer,
            long previousXrefOffset,
            int size,
            IPdfObjectEncryptor objectEncryptor = null)
        {
            var result = new WriteResult();

            // Validate /Size BEFORE a byte reaches the caller's stream. If size doesn't cover the
            // objects, a strict reader will reject the document with an xref-bounds error — and the
            // highest object number is knowable from `modifiedObjects` alone, so nothing has to be
            // written to find it out.
            //
            // Before the write loop: after it would be the one outcome worse than either it chooses
            // between — the caller's output holding the original file plus a run of orphan
            // `N G obj … endobj` bodies with no xref section and no %%EOF after them, which a caller
            // that catches the exception, reports failure and moves on would leave on disk —
            // PdfRevisions' physical boundary scan cannot see a revision there, and a reader that
            // scans forward can pick the orphans up.
            int maxObjNum = 0;
            foreach (var obj in modifiedObjects)
                if (obj.ObjectNumber > maxObjNum) maxObjNum = obj.ObjectNumber;

            if (size <= maxObjNum)
            {
                // A document that already uses object number int.MaxValue — two public-corpus
                // files do (GAPS §7c) — leaves "highest plus one" nowhere to go: the caller's next
                // number overflows and arrives here as a negative /Size, or as int.MaxValue itself.
                // The refusal names the cause, not only the overflowed value.
                bool ceiling = size < 0 || maxObjNum == int.MaxValue;
                throw new InvalidOperationException(
                    $"Trailer /Size ({size}) must be strictly greater than the highest " +
                    $"object number written ({maxObjNum}); strict readers reject xref " +
                    "entries beyond /Size." +
                    (ceiling
                        ? $" The document already uses object number {int.MaxValue}, the largest a PDF object may " +
                          "have, so no number is free for a new object; rewrite the document (a full save renumbers " +
                          "its objects) before appending a revision."
                        : ""));
            }

            LastUnchainedRecovery = null;

            // Can the previous section be NAMED? Verified by reading the bytes, not range-checked:
            // PdfParser.ParseCrossReferences assigns StartXrefOffset BEFORE trying to parse the
            // section there, so a file whose section is garbage keeps its bogus non-zero offset
            // after scan recovery — and every one of this overload's callers passes exactly that
            // value. IsUsablePreviousXref would wave it through and the appended trailer would name
            // a /Prev pointing at nothing.
            //
            // Read from the STREAM, not from a copy of the whole file: the classification needs one
            // bounded window and the recovery needs a parser, so neither wants the document in
            // memory — and this overload is the one 25 of the 28 Base.Operations writers use.
            //
            // …at the position the offset names under §7.5.2 — counted from the original's header,
            // which is what the parser's StartXrefOffset is counted from — with the byte-0 reading
            // the parser also keeps as the fallback. Probing at the bare offset meant a prefixed
            // original was never chained: its section was there, one prefix-length away.
            long headerOffset = PdfHeaderLocator.OriginOf(originalPdf);
            bool chained = LocateXrefSection(originalPdf, previousXrefOffset, headerOffset) >= 0;

            // The original's table, read once: it supplies the generation a superseded object keeps
            // (KeptGeneration) and, when there is no /Prev to chain to, the entries this section
            // must re-declare.
            CrossReferenceTable originalTable = ReadCrossReferenceTable(originalPdf, out Exception tableError);

            // Copy original PDF to output
            originalPdf.Position = 0;
            originalPdf.CopyTo(outputPdf);
            long appendStart = outputPdf.Position;
            long originalLength = originalPdf.Position;
            long fileStart = appendStart - originalLength;

            // What every offset this revision writes is measured from; see RevisionOrigin.
            long origin = RevisionOrigin(fileStart, headerOffset);

            var writer = new PdfWriter(outputPdf) { ObjectEncryptor = objectEncryptor };

            // The object header and the xref entry carry one generation, KeptGeneration's — a
            // hardcoded 0 for a gen>0 object makes strict readers treat the entry as stale and drop
            // the update.
            var xrefEntries = new List<(int objNum, int gen, long offset)>();
            foreach (var obj in modifiedObjects)
            {
                // WriteIndirectObject reports an output position. The entry carries the offset
                // (§7.5.2); the result keeps the position, which is what a caller that seals or
                // patches the output needs.
                int generation = KeptGeneration(originalTable, obj);
                long offset = writer.WriteIndirectObject(obj.ObjectNumber, generation, obj.Object);
                xrefEntries.Add((obj.ObjectNumber, generation, offset - origin));
                result.ObjectOffsets[obj.ObjectNumber] = offset;
            }

            // With no /Prev there is no chain to inherit the original objects from, so this section
            // has to name them itself or reopening the file finds only what was just written.
            // Appending moved nothing, so the offsets the parser recovered are still correct.
            //
            // WHY THIS MATTERS MORE THAN IT LOOKS: which of two broken outcomes a caller got was
            // decided by whether it happened to rewrite object 1. An edit that does not touch the
            // catalog (PdfPageContentAppender, PdfFormFiller, PdfFormFlattener, PdfImageReplacer)
            // produced a file that throws "Cannot find document catalog."; one that does
            // (PdfBookmarksManager, PdfOcgManager, PdfNamedDestinations, PdfMetadataWriter,
            // PdfAttachmentManager) produced one that OPENS, with a catalog and zero pages. The
            // silent case is the dangerous one.
            //
            // The recovered offsets are stream positions in the original, so the base to add is
            // where the ORIGINAL's byte 0 landed in the output — not `appendStart` itself, and not a
            // hardcoded 0: the output stream is the caller's and may already hold a prefix — less
            // the origin this revision counts from.
            if (!chained)
                AppendInFileRecoveryEntries(originalTable, tableError, xrefEntries, fileStart - origin);

            // Write xref table. An incremental update does not re-declare object 0 — but a
            // STANDALONE section must carry the free-list head, exactly as WriteClassicSection
            // does on the options overload.
            long xrefOffset = writer.WriteXrefTable(xrefEntries, includeObject0: !chained);

            // Build new trailer.
            //
            // ONE implementation, in PdfTrailerBuilder. The rule this carried — only chain to a
            // previous section that can exist, because "/Prev 0" points at the %PDF header and an
            // out-of-range /Prev points past end of file, either of which makes the whole chain
            // unfollowable and the original revision unreachable — was correct here and absent from
            // the two writers in Tetitulz.Pdf.Signing, which build their own trailers and so never
            // reached it. Callers arrive with 0 whenever the parser had to recover a damaged file
            // by scanning, which is exactly the input a repair tool is pointed at.
            //
            // /Prev is the caller's value as it stands: an offset the original wrote, counted from
            // its header, which is the basis this revision counts from too. It is deliberately not
            // recomputed from where the section was found — a section found at the byte-0 reading
            // has byte-0 entries, and only the value the file wrote makes a reader take that reading
            // again. The bound it is checked against is on the same basis.
            // And only when the probe above found a section there: an unchained revision wrote a
            // complete standalone table, which a /Prev to an offset holding no section contradicts.
            var newTrailer = PdfTrailerBuilder.ForAppendedRevision(
                originalTrailer, size, chained ? previousXrefOffset : 0, appendStart - origin);

            writer.WriteTrailer(newTrailer, xrefOffset - origin);

            result.XrefOffset = xrefOffset;
            result.TotalLength = outputPdf.Position;
            return result;
        }

        // ==================================================================
        //  Format-aware overload
        // ==================================================================

        /// <summary>
        /// Writes <paramref name="originalPdf"/> verbatim followed by exactly one appended
        /// revision containing <paramref name="objects"/>, in the same cross-reference form as
        /// the section it supersedes.
        ///
        /// <para>
        /// When <paramref name="objects"/> is empty or null only the original bytes are written,
        /// so a caller can hand over unconditionally and get a byte-identical passthrough for a
        /// no-op edit.
        /// </para>
        ///
        /// <para>
        /// The revision is assembled in memory and copied to <paramref name="outputPdf"/> only
        /// once it is complete, so a failure part-way leaves the output holding exactly the
        /// original document rather than a truncated revision.
        /// </para>
        /// </summary>
        /// <param name="originalPdf">The complete original PDF.</param>
        /// <param name="outputPdf">
        /// Destination for the whole document. The original bytes have <b>not</b> been written
        /// yet — this method writes them.
        /// </param>
        /// <param name="objects">Objects to append, each carrying its own generation number.</param>
        /// <param name="options">Trailer / format / diagnostics settings; may be null.</param>
        public static WriteResult WriteUpdate(
            byte[] originalPdf,
            Stream outputPdf,
            IReadOnlyList<IndirectObject> objects,
            IncrementalUpdateOptions options)
        {
            if (originalPdf == null) throw new ArgumentNullException(nameof(originalPdf));
            if (outputPdf == null) throw new ArgumentNullException(nameof(outputPdf));

            options = options ?? new IncrementalUpdateOptions();

            // A revision has one AuthCode dictionary and ISO/TS 32004 Table 6 makes the two
            // placements alternatives — /ByteRange and /MAC are required by one and forbidden by
            // the other. Refused rather than resolved by precedence: whichever one a precedence
            // rule dropped, the document would come out claiming a protection it does not have.
            if (options.PdfMac != null && options.AuthCode != null)
                throw new ArgumentException(
                    "Set either PdfMac (ISO/TS 32004 §6.5.1's standalone token, sealed after the write) or " +
                    "AuthCode (§6.5.2's AttachedToSig dictionary, written as given), not both: a revision " +
                    "carries one AuthCode dictionary and the two placements exclude each other.",
                    nameof(options));

            if (options.PdfMac != null
                && (!outputPdf.CanSeek || !outputPdf.CanRead || !outputPdf.CanWrite || outputPdf.Position != 0))
                throw new ArgumentException(
                    "Sealing the revision with a PDF MAC token needs an output stream that is seekable, readable " +
                    "and writable and positioned at 0, because the token covers the whole file.", nameof(outputPdf));

            // Byte offsets in a PDF are measured from the start of the file. Capture where the
            // file starts in `outputPdf` before anything is written so the appended revision's
            // offsets stay correct even if the caller pre-positioned the stream.
            long fileStart = 0;
            try
            {
                if (outputPdf.CanSeek) fileStart = outputPdf.Position;
            }
            catch (Exception ex)
            {
                Report(options, "IncrementalUpdateWriter.WriteUpdate",
                    "the output stream's position could not be read; offsets are computed as if the PDF starts at byte 0", ex);
                fileStart = 0;
            }

            outputPdf.Write(originalPdf, 0, originalPdf.Length);

            var result = new WriteResult();

            SortedList<int, IndirectObject> ordered = Order(objects, options);
            if (ordered.Count == 0)
            {
                result.TotalLength = fileStart + originalPdf.Length;
                return result;
            }

            AppendRevision(originalPdf, outputPdf, ordered, options, fileStart, result);
            return result;
        }

        private static void AppendRevision(
            byte[] originalPdf,
            Stream outputPdf,
            SortedList<int, IndirectObject> objects,
            IncrementalUpdateOptions options,
            long fileStart,
            WriteResult result)
        {
            long appendStart = fileStart + originalPdf.Length;

            // What every offset this revision writes is measured from; see RevisionOrigin.
            long headerOffset = PdfHeaderLocator.OriginOf(originalPdf, originalPdf.Length);
            long origin = RevisionOrigin(fileStart, headerOffset);

            XrefSectionKind previousKind;
            long previousInFile = ResolvePreviousXrefOffset(
                originalPdf, headerOffset, options, out previousKind, out long previousPosition);

            // Where the previous section sits in the output, for the guard. What is WRITTEN as /Prev
            // is `previousInFile` — the offset as the original wrote it, counted from its header,
            // which is the basis this revision counts from too. Deliberately the value and not the
            // position: a section found at the byte-0 reading has byte-0 entries, and only the
            // value the file wrote makes a reader take that reading again.
            long previousXref = previousInFile > 0 ? fileStart + previousPosition : 0;
            if (previousXref <= 0 || previousXref >= appendStart)
            {
                if (previousXref >= appendStart)
                    Report(options, "IncrementalUpdateWriter.AppendRevision",
                        "the previous cross-reference offset (" + previousXref.ToString(CultureInfo.InvariantCulture) +
                        ") is not before the appended revision; /Prev is omitted");
                previousXref = 0;
                previousInFile = 0;
            }

            bool classic = UseClassicXrefSection(previousKind, options);
            CrossReferenceTable originalTable = OriginalTable(originalPdf, options);

            using (var revision = new MemoryStream())
            {
                var writer = new PdfWriter(revision) { ObjectEncryptor = options.ObjectEncryptor };

                // The original may end at "%%EOF" with no trailing EOL; the appended revision
                // must not run onto the same line as it.
                if (options.EnsureSeparatorBeforeRevision && NeedsLeadingEol(originalPdf))
                    writer.WriteRaw("\n");

                var entries = new List<XrefEntry>(objects.Count + 1);
                int highestObjectNumber = 0;

                foreach (KeyValuePair<int, IndirectObject> kvp in objects)
                {
                    IndirectObject obj = kvp.Value;
                    int generation = KeptGeneration(originalTable, obj);
                    long relative = writer.WriteIndirectObject(obj.ObjectNumber, generation, obj.Object);
                    long absolute = appendStart + relative;

                    // The entry carries the offset (§7.5.2); the result keeps the output position,
                    // which is what a caller that seals or patches the output needs.
                    entries.Add(new XrefEntry(obj.ObjectNumber, generation, absolute - origin));
                    result.ObjectOffsets[obj.ObjectNumber] = absolute;

                    if (obj.ObjectNumber > highestObjectNumber) highestObjectNumber = obj.ObjectNumber;
                }

                // With no /Prev there is no chain to inherit the original objects from, so this
                // section has to name them itself or reopening the file finds only what we just
                // wrote — the catalog included. Appending nothing moved them, so the offsets the
                // parser recovered are still correct.
                if (previousXref <= 0 && options.ReDeclareOriginalObjectsWhenUnchained)
                    AddRecoveryEntries(entries, options, fileStart - origin, ref highestObjectNumber, classic);

                long xrefOffset = classic
                    ? WriteClassicSection(writer, originalPdf, options, entries,
                        highestObjectNumber, previousInFile, appendStart, origin)
                    : WriteXrefStreamSection(writer, originalPdf, options, entries,
                        highestObjectNumber, previousInFile, appendStart, origin);

                result.XrefOffset = xrefOffset;

                revision.Position = 0;
                revision.CopyTo(outputPdf);
                result.TotalLength = appendStart + revision.Length;
            }

            // The section starts after every appended object's data, so the placeholder it carries is
            // the first /AuthCode from there.
            options.PdfMac?.Seal(outputPdf, result.XrefOffset);
        }

        /// <summary>
        /// Classic revision: <c>xref</c> subsections + a <c>trailer</c> dictionary. Valid when
        /// the section being superseded is itself a classic table.
        /// </summary>
        /// <remarks>
        /// <c>previousWritten</c> is the <c>/Prev</c> to write, or 0 for a standalone section;
        /// <c>origin</c> is what the written offsets count from (see <see cref="RevisionOrigin"/>).
        /// Returns the section's output position.
        /// </remarks>
        private static long WriteClassicSection(
            PdfWriter writer,
            byte[] originalPdf,
            IncrementalUpdateOptions options,
            List<XrefEntry> entries,
            int highestObjectNumber,
            long previousWritten,
            long appendStart,
            long origin)
        {
            var xrefEntries = new List<(int objNum, int gen, long offset)>(entries.Count);
            foreach (XrefEntry entry in entries)
                xrefEntries.Add((entry.ObjectNumber, entry.Generation, entry.Offset));

            // Object 0 heads the free list. An incremental update must NOT re-declare it, because
            // the section it chains to already did — but a section with no /Prev is standalone and
            // therefore must.
            bool standalone = previousWritten <= 0;
            long xrefOffset = appendStart + writer.WriteXrefTable(xrefEntries, includeObject0: standalone);

            var trailer = new PdfDictionary();
            CopyTrailerKeys(trailer, originalPdf, options);
            trailer.Set(PdfName.Size, new PdfNumber(ComputeSize(options, highestObjectNumber)));

            // Omit /Prev entirely rather than emitting "/Prev 0", which points at the %PDF
            // header and makes the document unreadable.
            if (previousWritten > 0)
                trailer.Set(PdfName.Prev, new PdfNumber(previousWritten));

            if (options.PdfMac != null)
                trailer.Set(PdfName.AuthCode, options.PdfMac.CreatePlaceholder());
            else if (options.AuthCode != null)
                trailer.Set(PdfName.AuthCode, options.AuthCode);

            writer.WriteTrailer(trailer, xrefOffset - origin);
            return xrefOffset;
        }

        /// <summary>
        /// Cross-reference-stream revision (PDF 1.5+, ISO 32000-1 §7.5.8): one
        /// <c>/Type /XRef</c> stream object carrying <c>/W [1 n 2]</c> binary entries,
        /// Flate-compressed, with <c>/Index</c> describing the (sparse) object-number
        /// subsections and <c>startxref</c> pointing at the stream object itself.
        ///
        /// <para>
        /// The xref stream is an indirect object, so it needs an entry for itself in its own
        /// table — that entry is added before the stream is written, from the offset the writer
        /// is currently at.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <c>previousWritten</c> is the <c>/Prev</c> to write, or 0 for a standalone section;
        /// <c>origin</c> is what the written offsets count from (see <see cref="RevisionOrigin"/>).
        /// Returns the section's output position.
        /// </remarks>
        private static long WriteXrefStreamSection(
            PdfWriter writer,
            byte[] originalPdf,
            IncrementalUpdateOptions options,
            List<XrefEntry> entries,
            int highestObjectNumber,
            long previousWritten,
            long appendStart,
            long origin)
        {
            int xrefObjectNumber = AllocateXrefStreamObjectNumber(options, highestObjectNumber);
            if (xrefObjectNumber > highestObjectNumber) highestObjectNumber = xrefObjectNumber;

            long xrefStreamOffset = appendStart + writer.Position;
            entries.Add(new XrefEntry(xrefObjectNumber, 0, xrefStreamOffset - origin));
            entries.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));

            int offsetWidth = XrefStreamFields.Field2Width(entries, e => e.Offset);

            var stream = new PdfStream();
            stream.Set(PdfName.Type, PdfName.XRef);
            stream.Set(PdfName.Size, new PdfNumber(ComputeSize(options, highestObjectNumber)));
            stream.Set(PdfName.Index, XrefStreamFields.IndexArray(entries, e => e.ObjectNumber));
            stream.Set(PdfName.W, XrefStreamFields.WidthArray(offsetWidth));

            CopyTrailerKeys(stream, originalPdf, options);

            if (previousWritten > 0)
                stream.Set(PdfName.Prev, new PdfNumber(previousWritten));

            if (options.PdfMac != null)
                stream.Set(PdfName.AuthCode, options.PdfMac.CreatePlaceholder());
            else if (options.AuthCode != null)
                stream.Set(PdfName.AuthCode, options.AuthCode);

            byte[] raw = BuildXrefStreamData(entries, offsetWidth);
            byte[] encoded = FlateEncodeFilter.Encode(raw);
            if (encoded != null && encoded.Length > 0 && encoded.Length < raw.Length)
            {
                stream.Set(PdfName.Filter, PdfName.FlateDecode);
                stream.RawData = encoded;
            }
            else
            {
                // Compression did not help (a handful of entries can inflate); an uncompressed
                // xref stream is equally valid, so skip the filter.
                stream.RawData = raw;
            }

            // §7.5.8.2: a cross-reference stream is never encrypted — a reader has to parse it
            // before it can know the document is encrypted at all. Its dictionary also carries the
            // trailer's /ID, which §7.6.2 likewise exempts.
            long written = appendStart + writer.WriteIndirectObject(xrefObjectNumber, 0, stream, encrypt: false);
            if (written != xrefStreamOffset)
            {
                Report(options, "IncrementalUpdateWriter.WriteXrefStreamSection",
                    "the cross-reference stream was written at offset " +
                    written.ToString(CultureInfo.InvariantCulture) +
                    " but its own entry records " + xrefStreamOffset.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteRaw("startxref\n");
            writer.WriteRaw((xrefStreamOffset - origin).ToString(CultureInfo.InvariantCulture));
            writer.WriteRaw("\n%%EOF\n");

            return xrefStreamOffset;
        }

        // ------------------------------------------------------------------
        //  Trailer
        // ------------------------------------------------------------------

        /// <summary>
        /// Carries the original trailer's entries into the appended revision's, then settles the four
        /// this writer cannot do without: <c>/Root</c>, <c>/Info</c>, <c>/ID</c> and <c>/Encrypt</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>ISO 32000-2 §7.5.6: the added trailer "shall contain all the entries except the Prev
        /// entry (if present) from the previous trailer".</b> This copied those four keys and nothing
        /// else, so every other entry a producer put in the trailer was gone from the newest revision —
        /// invisibly, because the parser merges trailers along the <c>/Prev</c> chain. The entries now
        /// go through <see cref="PdfTrailerBuilder.ForUpdate"/>, the rule the signer already used, which
        /// leaves out only what describes the section it came from: xref-stream plumbing, <c>/Prev</c>,
        /// <c>/XRefStm</c> and a stale <c>/AuthCode</c>. <c>/Size</c> is restated by the caller.</para>
        /// </remarks>
        private static void CopyTrailerKeys(
            PdfDictionary target, byte[] originalPdf, IncrementalUpdateOptions options)
        {
            PdfDictionary source = options.OriginalTrailer;
            if (source == null)
            {
                try
                {
                    source = options.Document?.Trailer;
                }
                catch (Exception ex)
                {
                    Report(options, "IncrementalUpdateWriter.CopyTrailerKeys",
                        "the original trailer could not be read; /Root, /Info and /ID may be missing from the update", ex);
                }
            }

            foreach (KeyValuePair<PdfName, PdfObject> entry in PdfTrailerBuilder.ForUpdate(source, newSize: 0))
                if (entry.Key != PdfName.Size)
                    target.Set(entry.Key, entry.Value);

            // /Root — required. PDF 32000-1 §7.5.5 requires it to be an indirect reference.
            PdfReference rootRef = AsReference(source?[PdfName.Root], options);
            if (rootRef != null)
                target.Set(PdfName.Root, rootRef);
            else
                Report(options, "IncrementalUpdateWriter.CopyTrailerKeys",
                    "the original trailer has no usable /Root; the appended revision's trailer will not name a catalog");

            // /Info — optional. Carry the original forward; only fall back to a freshly written
            // /Info object when the original had none.
            PdfReference infoRef = AsReference(source?[PdfName.Info], options);
            if (infoRef != null)
                target.Set(PdfName.Info, infoRef);
            else if (options.NewInfoObjectNumber > 0)
                target.Set(PdfName.Info, new PdfReference(options.NewInfoObjectNumber, 0));

            // /ID — required by PDF/UA-1 and PDF/A. An encrypted document that has none keeps
            // having none: a revision-4-or-lower file key is derived from the first /ID (§7.6.3.3
            // Algorithm 2 step e), and the decryptor hashes zero bytes for an absent one, so a
            // synthesised identifier would be a key the password no longer derives.
            PdfObject encrypt = source?[PdfName.Encrypt];
            PdfArray existingId = ResolveExistingId(source, options);
            if (existingId != null)
                target.Set(PdfName.ID, existingId);
            else if (encrypt == null && options.ObjectEncryptor == null)
                target.Set(PdfName.ID, SynthesiseId(originalPdf, options));
            else
                Report(options, "IncrementalUpdateWriter.CopyTrailerKeys",
                    "the encrypted document has no /ID, so none is written: its file key is derived from the first /ID, and a synthesised one would lock the file");

            // /Encrypt — must be carried forward or every earlier revision becomes undecryptable.
            if (encrypt != null)
            {
                target.Set(PdfName.Encrypt, encrypt);

                // Without an encryptor the appended objects are clear text inside a file whose
                // reader will decrypt them, so they decode to garbage. That is only safe if the
                // caller refused to edit encrypted documents in the first place — say so loudly
                // rather than silently emitting a half-encrypted file.
                if (options.ObjectEncryptor == null)
                    Report(options, "IncrementalUpdateWriter.CopyTrailerKeys",
                        "the document is encrypted; /Encrypt was carried forward but the appended objects are written in the clear");
            }
            else if (options.ObjectEncryptor != null)
            {
                // Encrypting objects into a document that declares no /Encrypt would make them
                // unreadable in the opposite direction.
                Report(options, "IncrementalUpdateWriter.CopyTrailerKeys",
                    "an object encryptor was supplied but the original trailer declares no /Encrypt; " +
                    "the appended objects are encrypted and no reader will decrypt them");
            }
        }

        /// <summary>
        /// Returns the original <c>/ID</c> array when the document has a well-formed one,
        /// otherwise synthesises a two-part 16-byte-per-half identifier.
        ///
        /// <para>
        /// The synthesised value is a pure function of the original bytes — a SHA-256 digest
        /// split in half. No <see cref="Random"/>, no timestamp: byte-for-byte reproducible
        /// output matters for deterministic builds and for tests that compare two runs.
        /// </para>
        /// </summary>
        internal static PdfArray ResolveOrSynthesiseId(
            PdfDictionary trailer, byte[] originalPdf, IncrementalUpdateOptions options)
            => ResolveExistingId(trailer, options) ?? SynthesiseId(originalPdf, options);

        /// <summary>The document's own well-formed <c>/ID</c>, resolved when indirect; null when it has none.</summary>
        private static PdfArray ResolveExistingId(PdfDictionary trailer, IncrementalUpdateOptions options)
        {
            PdfObject idObj = trailer?[PdfName.ID];

            if (idObj is PdfReference && options.Document != null)
            {
                try
                {
                    idObj = options.Document.ResolveObject(idObj);
                }
                catch (Exception ex)
                {
                    Report(options, "IncrementalUpdateWriter.ResolveOrSynthesiseId",
                        "the original /ID could not be resolved; the document is treated as having none", ex);
                    idObj = null;
                }
            }

            // The shape test itself is PdfFileId's — the resolve above stays here because only this
            // method has the Document to resolve against and a reason to report the failure.
            return PdfFileId.Existing(idObj, resolve: null);
        }

        /// <summary>The deterministic two-part identifier for a document that has none (see <see cref="ResolveOrSynthesiseId"/>).</summary>
        private static PdfArray SynthesiseId(byte[] originalPdf, IncrementalUpdateOptions options)
        {
            byte[] digest;
            try
            {
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(originalPdf);
            }
            catch (Exception ex)
            {
                Report(options, "IncrementalUpdateWriter.ResolveOrSynthesiseId",
                    "SHA-256 was unavailable; /ID was derived from a non-cryptographic content digest instead", ex);
                digest = FallbackDigest(originalPdf);
            }

            return PdfFileId.FromDigest(digest);
        }

        /// <summary>
        /// Deterministic 32-byte digest for the (practically unreachable) case where the platform
        /// refuses SHA-256, e.g. a FIPS policy that disables the managed provider. FNV-1a over
        /// the content, expanded to 32 bytes — not cryptographic, but /ID is an identifier, not a
        /// security control, and determinism is the property that matters.
        /// </summary>
        private static byte[] FallbackDigest(byte[] data)
        {
            var result = new byte[32];
            for (int lane = 0; lane < 4; lane++)
            {
                ulong hash = 14695981039346656037UL + (ulong)lane;
                for (int i = lane; i < data.Length; i += 4)
                {
                    hash ^= data[i];
                    hash *= 1099511628211UL;
                }
                for (int b = 0; b < 8; b++)
                    result[lane * 8 + b] = (byte)(hash >> (8 * (7 - b)));
            }
            return result;
        }

        private static PdfReference AsReference(PdfObject obj, IncrementalUpdateOptions options)
        {
            if (obj is PdfReference reference) return reference;

            // A direct dictionary where a reference belongs is malformed but recoverable when
            // the object is one we can name.
            if (obj is PdfDictionary dict)
            {
                int number = options.ObjectNumberResolver != null
                    ? options.ObjectNumberResolver(dict)
                    : dict.ObjectNumber;

                if (number > 0)
                    return new PdfReference(number, GetGeneration(options, number, dict));
            }

            return null;
        }

        /// <summary>
        /// The generation of existing object <paramref name="objectNumber"/>, for a trailer entry
        /// recovered from a direct dictionary: the caller's resolver, else
        /// <see cref="ExistingObjectGeneration.Of"/> over the document's table, with the
        /// dictionary's own stamp as the fallback a reference would otherwise supply.
        /// </summary>
        /// <remarks>
        /// It read the table itself, inside a <c>try</c> around a dictionary lookup — which cannot
        /// throw, so the report its <c>catch</c> made could not fire — and took any entry that was
        /// not free, where the one answer takes an in-use one. An object-stream entry's generation is
        /// always 0, so the two agreed; now they are one.
        /// </remarks>
        private static int GetGeneration(IncrementalUpdateOptions options, int objectNumber, PdfObject fallback)
        {
            if (options.GenerationResolver != null) return options.GenerationResolver(objectNumber);

            return ExistingObjectGeneration.Of(options.Document?.CrossReferenceTable, objectNumber,
                fallback == null ? null : new PdfReference(objectNumber, fallback.GenerationNumber));
        }

        // ------------------------------------------------------------------
        //  Previous cross-reference section
        // ------------------------------------------------------------------

        /// <summary>
        /// Adds an entry for every object the original document defines that this revision does
        /// not already rewrite, using the offsets the parser recovered.
        ///
        /// <para>
        /// An object stored inside an <c>/ObjStm</c> has no byte offset of its own, so it can only
        /// be re-declared as a <b>type-2</b> row — which a cross-reference <i>stream</i> can carry
        /// and a classic table cannot. Skipped silently, and a modern file keeps its catalog and page
        /// tree there, the appended section would name neither and the document would not reopen
        /// while the diagnostic still reported success.
        /// </para>
        ///
        /// <para>
        /// <c>classic</c> says which section is being written: a classic <c>xref</c> table has no
        /// type field, so compressed objects are unnameable there and are <i>reported</i> rather
        /// than dropped in silence.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <c>offsetBase</c> is what to add to a recovered offset — a stream position in the
        /// original — to put it on the revision's basis: where the original's byte 0 landed in the
        /// output, less the revision's origin (see <see cref="RevisionOrigin"/>).
        /// </remarks>
        private static void AddRecoveryEntries(
            List<XrefEntry> entries, IncrementalUpdateOptions options, long offsetBase,
            ref int highestObjectNumber, bool classic)
        {
            CrossReferenceTable table;
            try
            {
                table = options.Document?.Parser?.CrossReferenceTable;
            }
            catch (Exception ex)
            {
                Report(options, "IncrementalUpdateWriter.AddRecoveryEntries",
                    "the original cross-reference table could not be read; the appended section names " +
                    "only the edited objects and the result may not reopen", ex);
                return;
            }

            if (table == null)
            {
                Report(options, "IncrementalUpdateWriter.AddRecoveryEntries",
                    "the original document exposes no cross-reference table; the appended section names " +
                    "only the edited objects and the result may not reopen");
                return;
            }

            var already = new HashSet<int>();
            for (int i = 0; i < entries.Count; i++) already.Add(entries[i].ObjectNumber);

            // Two passes: the in-file objects first, because a type-2 row is only usable when the
            // /ObjStm holding it is itself named — and the container is one of those in-file
            // objects. A row pointing at a container this section cannot reach is worse than no
            // row, since a reader would follow it into nothing.
            var inFile = new HashSet<int>();
            int added = 0;
            int compressedSkipped = 0;
            foreach (CrossReferenceEntry entry in table.AllEntries)
            {
                if (entry == null) continue;
                if (entry.Type != CrossReferenceEntry.EntryType.Normal)
                {
                    // A type-2 entry names an object living INSIDE an /ObjStm. This section can
                    // only re-declare an object it can point a byte offset at, so those cannot be
                    // named here — and in a modern file that is exactly where the catalog and page
                    // tree live. Reporting only `added` claimed the table was complete when the
                    // objects a reader needs most were the ones missing from it.
                    if (entry.Type == CrossReferenceEntry.EntryType.Compressed &&
                        entry.ObjectNumber > 0 && !already.Contains(entry.ObjectNumber))
                        compressedSkipped++;
                    continue;
                }
                if (entry.ObjectNumber <= 0) continue;
                if (entry.Offset <= 0) continue;
                inFile.Add(entry.ObjectNumber);
                if (!already.Add(entry.ObjectNumber)) continue;

                entries.Add(new XrefEntry(entry.ObjectNumber, entry.GenerationNumber, offsetBase + entry.Offset));
                if (entry.ObjectNumber > highestObjectNumber) highestObjectNumber = entry.ObjectNumber;
                added++;
            }

            int packed = 0;
            int unnameable = 0;
            foreach (CrossReferenceEntry entry in table.AllEntries)
            {
                if (entry == null) continue;
                if (entry.Type != CrossReferenceEntry.EntryType.Compressed) continue;
                if (entry.ObjectNumber <= 0) continue;
                if (already.Contains(entry.ObjectNumber)) continue;

                // A classic table has no type field, the container was not re-declared, or the
                // index does not fit the 2-byte field a type-2 row gives it.
                if (classic ||
                    !inFile.Contains(entry.ObjectStreamNumber) ||
                    entry.IndexInObjectStream < 0 ||
                    entry.IndexInObjectStream > MaxGenerationNumber)
                {
                    unnameable++;
                    continue;
                }

                already.Add(entry.ObjectNumber);
                entries.Add(XrefEntry.InObjectStream(
                    entry.ObjectNumber, entry.ObjectStreamNumber, entry.IndexInObjectStream));
                if (entry.ObjectNumber > highestObjectNumber) highestObjectNumber = entry.ObjectNumber;
                packed++;
            }

            entries.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));

            string detail =
                "no usable previous cross-reference offset, so the appended section was written as a " +
                "complete table; " + added.ToString(CultureInfo.InvariantCulture) +
                " original object(s) were re-declared at their recovered offsets";
            if (packed > 0)
                detail += " and " + packed.ToString(CultureInfo.InvariantCulture) +
                    " as object-stream (type 2) entries";
            if (unnameable > 0)
                detail += "; " + unnameable.ToString(CultureInfo.InvariantCulture) +
                    " original object(s) live in object streams and could not be re-declared" +
                    (classic ? " in a classic xref table" : "") +
                    ", so the appended section is incomplete and the document may not reopen";

            Report(options, "IncrementalUpdateWriter.AddRecoveryEntries", detail);
        }

        [ThreadStatic] private static string _lastUnchainedRecovery;

        /// <summary>
        /// What the six-argument <see cref="WriteUpdate(Stream, Stream, List{IndirectObject}, PdfDictionary, long, int, IPdfObjectEncryptor)"/>
        /// had to do because the previous cross-reference section could not be named — <c>null</c>
        /// when it chained normally, which is every ordinary document.
        /// </summary>
        /// <remarks>
        /// That overload has no options object and no diagnostic sink — it is the one 25 of the 28
        /// <c>Base.Operations</c> writers use — so this is the only channel it has. Reset at the
        /// start of every call, on every path, because a caller reads it <i>after</i> the call and a
        /// previous document's message must not answer this document's question.
        /// </remarks>
        public static string LastUnchainedRecovery
        {
            get => _lastUnchainedRecovery;
            private set => _lastUnchainedRecovery = value;
        }

        /// <summary>
        /// The original's cross-reference table from a bare <see cref="Parsing.PdfParser"/> — null,
        /// with the reason, when it cannot be read.
        /// </summary>
        /// <remarks>
        /// <para><b>A bare parser, not <c>PdfDocument.Open</c></b>, which throws on exactly the
        /// damaged files the unchained recovery exists for — the parser scan-recovers instead and
        /// hands back what it found.</para>
        ///
        /// <para><see cref="Parsing.PdfParser"/> does not own the stream it is handed, so this does
        /// not dispose it. It leaves the position wherever the parse stopped. When this read ran
        /// after the copy it restored the position, because the caller's stream was left there; it
        /// now runs before the copy, which rewinds to 0 itself, so a restore would do nothing.</para>
        /// </remarks>
        private static CrossReferenceTable ReadCrossReferenceTable(Stream originalPdf, out Exception error)
        {
            error = null;
            try
            {
                originalPdf.Position = 0;
                var parser = new Parsing.PdfParser(originalPdf);
                parser.ParseHeader();
                parser.ParseCrossReferences();
                return parser.CrossReferenceTable;
            }
            catch (Exception ex)
            {
                error = ex;
                return null;
            }
        }

        /// <summary>
        /// The table <see cref="KeptGeneration"/> reads on the options overload: the document's
        /// when <see cref="IncrementalUpdateOptions.Document"/> is set, else one parse of the bytes.
        /// A caller with a parser and no document (the blockchain signer) gets the rule too.
        /// </summary>
        private static CrossReferenceTable OriginalTable(byte[] originalPdf, IncrementalUpdateOptions options)
        {
            CrossReferenceTable table = options.Document?.CrossReferenceTable;
            if (table != null) return table;

            using var stream = new MemoryStream(originalPdf, writable: false);
            table = ReadCrossReferenceTable(stream, out Exception error);
            if (error != null)
                Report(options, "IncrementalUpdateWriter.WriteUpdate",
                    "the original cross-reference table could not be read; each object is written at " +
                    "the generation it was handed", error);
            return table;
        }

        /// <summary>
        /// The generation <paramref name="obj"/> is written at: its own, unless that is 0 and
        /// <paramref name="original"/> holds the number in use at another.
        /// </summary>
        /// <remarks>
        /// <para><b>The one rule, applied here because every appended revision passes through
        /// here.</b> Base's writers build their revisions at some eighty
        /// <c>new IndirectObject(n, 0, …)</c> sites, and many of those supersede an existing object —
        /// a catalog, a page, an <c>/Info</c> dictionary, an image — at 0 whatever its own. §7.5.6
        /// keeps the generation of an updated object. A strict reader matches the section's entry
        /// against the reference, so it treats the revision as naming a different object and drops
        /// it.</para>
        ///
        /// <para><b>Only a 0 is corrected.</b> A caller that computed a generation is taken at its
        /// word. A 0 for a number the original holds in use at <i>g</i> &gt; 0 is never
        /// deliberate: no writer here reuses an in-use number, and reusing a freed one (a
        /// <c>Free</c> entry, which <see cref="ExistingObjectGeneration.Of"/> does not answer for)
        /// is the caller's to number.</para>
        ///
        /// <para>A reference to such an object is the caller's to write correctly
        /// (<see cref="ExistingObjectGeneration.ReferenceTo"/>). This writer cannot rewrite
        /// references: it cannot tell one the caller built from one copied out of the original,
        /// and a stale reference copied through must stay stale (§7.5.4), not be resurrected.</para>
        /// </remarks>
        private static int KeptGeneration(CrossReferenceTable original, IndirectObject obj)
            => obj.GenerationNumber != 0 ? obj.GenerationNumber : ExistingObjectGeneration.Of(original, obj.ObjectNumber);

        /// <summary>
        /// Re-declares the original file's in-file objects in <paramref name="entries"/>, at the
        /// offsets they already occupy, and records what it could not name.
        /// </summary>
        /// <remarks>
        /// <para>The table is <see cref="ReadCrossReferenceTable"/>'s, read once for the whole write;
        /// <paramref name="tableError"/> is why it could not be.</para>
        ///
        /// <para><b>Type-2 (object-stream) entries are skipped and counted, never guessed.</b> This
        /// overload writes a classic table, which has no type field, so an object living inside an
        /// <c>/ObjStm</c> cannot be named here at all — and in a modern file that is exactly where
        /// the catalog and page tree live. Reporting only what was added would claim the table is
        /// complete when the objects a reader needs most are the ones missing from it.</para>
        ///
        /// <para><c>offsetBase</c> is what to add to a recovered offset — a stream position in the
        /// original — to put it on the revision's basis: where the original's byte 0 landed in the
        /// output, less the revision's origin (see <see cref="RevisionOrigin"/>).</para>
        /// </remarks>
        private static void AppendInFileRecoveryEntries(
            CrossReferenceTable table, Exception tableError,
            List<(int objNum, int gen, long offset)> entries, long offsetBase)
        {
            // One give-up, not two: ReadCrossReferenceTable answers null only with its reason, because
            // ParseCrossReferences builds a table before it reads a byte. The second branch this had,
            // "the original document exposes no cross-reference table", could not be reached.
            if (table == null)
            {
                LastUnchainedRecovery =
                    "the previous cross-reference section could not be named and the original table " +
                    "could not be re-read (" + tableError?.GetType().Name + ": " + tableError?.Message + "), so the " +
                    "appended section names only the edited objects and the result may not reopen";
                return;
            }

            var already = new HashSet<int>();
            for (int i = 0; i < entries.Count; i++) already.Add(entries[i].objNum);

            int added = 0;
            int unnameable = 0;
            foreach (CrossReferenceEntry entry in table.AllEntries)
            {
                if (entry == null || entry.ObjectNumber <= 0) continue;

                if (entry.Type != CrossReferenceEntry.EntryType.Normal)
                {
                    if (entry.Type == CrossReferenceEntry.EntryType.Compressed &&
                        !already.Contains(entry.ObjectNumber)) unnameable++;
                    continue;
                }

                if (entry.Offset <= 0) continue;
                if (!already.Add(entry.ObjectNumber)) continue;

                entries.Add((entry.ObjectNumber, entry.GenerationNumber, offsetBase + entry.Offset));
                added++;
            }

            entries.Sort((a, b) => a.objNum.CompareTo(b.objNum));

            string detail =
                "no usable previous cross-reference offset, so the appended section was written as a " +
                "complete table; " + added.ToString(CultureInfo.InvariantCulture) +
                " original object(s) were re-declared at their recovered offsets";
            if (unnameable > 0)
                detail += "; " + unnameable.ToString(CultureInfo.InvariantCulture) +
                    " original object(s) live in object streams and cannot be re-declared in a " +
                    "classic xref table, so the appended section is incomplete and the document " +
                    "may not reopen";

            LastUnchainedRecovery = detail;
        }

        /// <summary>
        /// Whether <paramref name="offset"/> really holds a cross-reference section —
        /// <b>verified by reading the bytes</b>, not merely range-checked.
        /// </summary>
        /// <remarks>
        /// <para><c>PdfTrailerBuilder.IsUsablePreviousXref</c> answers a weaker question, and the
        /// gap between them is a live hazard: <c>PdfParser.ParseCrossReferences</c> assigns
        /// <c>StartXrefOffset</c> <i>before</i> trying to parse the section there, so a file whose
        /// section is garbage keeps its bogus non-zero offset after scan recovery — and that value
        /// passes a range test. This is the predicate a caller should use when the consequence of
        /// being wrong is worse than a refusal.</para>
        ///
        /// <para><b>Internal, and reachable from <c>Tetitulz.Pdf.Signing</c></b>, which is named in
        /// this assembly's <c>InternalsVisibleTo</c>. <c>PdfSigner</c> needs exactly this test
        /// before it appends: signing a document whose chain it cannot name produces a
        /// cryptographically valid attestation over content the write itself made unreachable.</para>
        /// </remarks>
        internal static bool PointsAtCrossReferenceSection(byte[] bytes, long offset)
            => ClassifyXrefSectionAt(bytes, offset) != XrefSectionKind.None;

        /// <summary>
        /// The same test against a seekable stream, reading <b>one bounded window</b> rather than
        /// the whole document.
        /// </summary>
        /// <remarks>
        /// The classification never looks further than <see cref="XrefStreamProbeWindowBytes"/> past
        /// the object header, so a window is all it can use — and the six-argument
        /// <c>WriteUpdate</c> is on the hot path of 25 writers, none of which should pay a full copy
        /// of the document to answer it. The caller's position is restored.
        /// </remarks>
        internal static bool PointsAtCrossReferenceSection(Stream stream, long offset)
        {
            if (stream == null || offset <= 0) return false;
            return LocateXrefSection(stream, offset, PdfHeaderLocator.OriginOf(stream)) >= 0;
        }

        /// <summary>
        /// The stream position an appended revision's byte offsets count from: the original's
        /// header, where it sits in the output.
        /// </summary>
        /// <remarks>
        /// <para>ISO 32000-2 §7.5.2: "byte offsets shall be calculated from the PERCENT SIGN (25h)"
        /// of the header, and its NOTE 1 allows "arbitrary bytes preceding the %PDF-". The
        /// original's own sections count from its header, so the revision appended to it must too,
        /// or a reader following the chain finds the two halves on different bases. Every number
        /// this writer emits into the file — entries, <c>startxref</c>, re-declared originals — is
        /// an output position minus this; <c>/Prev</c> is the previous section's offset as the
        /// original wrote it, which is on this basis already.</para>
        /// <para>Not output positions. For an original with its header at byte 0, written at output
        /// position 0, that is the same number — which is every ordinary call. For a prefixed
        /// original it would be wrong by the prefix; for an output already holding bytes (a
        /// <c>FileMode.Append</c> destination,
        /// one part of a multipart body) wrong by that lead, and readable neither as a whole
        /// nor with the lead stripped: the original's sections count from its header and the
        /// revision's did not. A stream with no header at all keeps origin 0 for an output that
        /// starts at 0, exactly as the parser folds a missing header to 0.</para>
        /// </remarks>
        private static long RevisionOrigin(long fileStart, long headerOffset) => fileStart + headerOffset;

        /// <summary>
        /// The stream position of the cross-reference section a written offset names, or -1 when
        /// neither reading finds one; <paramref name="kind"/> says which kind it is.
        /// </summary>
        /// <remarks>
        /// The same two readings, in the same order, as <c>PdfParser.ParseXrefSection</c>: the
        /// §7.5.2 position first — the offset counted from the header — and, only when that holds
        /// no section and the header is not at byte 0, the offset as a position from byte 0, which
        /// is how this writer itself counted before it honoured §7.5.2. Probing at the bare offset,
        /// as this did, meant every prefixed original was "unchained": its section was there, one
        /// prefix-length away from where the probe looked, and the whole document was re-declared.
        /// </remarks>
        private static long LocateXrefSection(byte[] bytes, long offset, long headerOffset, out XrefSectionKind kind)
        {
            kind = XrefSectionKind.None;
            if (bytes == null || offset <= 0) return -1;

            long position = headerOffset + offset;
            if (position < bytes.Length)
            {
                kind = ClassifyFrom(bytes, (int)position, bytes.Length);
                if (kind != XrefSectionKind.None) return position;
            }

            if (headerOffset > 0 && offset < bytes.Length)
            {
                kind = ClassifyFrom(bytes, (int)offset, bytes.Length);
                if (kind != XrefSectionKind.None) return offset;
            }

            kind = XrefSectionKind.None;
            return -1;
        }

        /// <summary>
        /// The same two readings against a seekable stream, reading <b>one bounded window</b> per
        /// reading rather than the whole document. The caller's position is restored.
        /// </summary>
        /// <remarks>
        /// The classification never looks further than <see cref="XrefStreamProbeWindowBytes"/> past
        /// the object header, so a window is all it can use — and the six-argument
        /// <c>WriteUpdate</c> is on the hot path of 25 writers, none of which should pay a full copy
        /// of the document to answer it.
        /// </remarks>
        private static long LocateXrefSection(Stream stream, long offset, long headerOffset)
        {
            if (stream == null || offset <= 0) return -1;

            long saved = stream.Position;
            try
            {
                long position = headerOffset + offset;
                if (ClassifyWindowAt(stream, position) != XrefSectionKind.None) return position;
                if (headerOffset > 0 && ClassifyWindowAt(stream, offset) != XrefSectionKind.None) return offset;
                return -1;
            }
            finally
            {
                stream.Position = saved;
            }
        }

        private static XrefSectionKind ClassifyWindowAt(Stream stream, long position)
        {
            if (position <= 0 || position >= stream.Length) return XrefSectionKind.None;
            stream.Position = position;

            var window = new byte[XrefSectionProbeBytes];
            int read = StreamBytes.Fill(stream, window, 0, window.Length);
            return read > 0 ? ClassifyFrom(window, 0, read) : XrefSectionKind.None;
        }

        /// <summary>What kind of cross-reference section (if any) sits at a given byte offset.</summary>
        internal enum XrefSectionKind
        {
            /// <summary>Not a cross-reference section at all — the offset is unusable.</summary>
            None,

            /// <summary>A classic <c>xref</c> keyword + subsections + <c>trailer</c>.</summary>
            Classic,

            /// <summary>An <c>N G obj</c> header introducing a <c>/Type /XRef</c> stream.</summary>
            Stream,
        }

        /// <summary>
        /// Offset (relative to the start of the original file) of the cross-reference section
        /// this revision supersedes, or 0 when it cannot be determined — in which case
        /// <c>/Prev</c> is omitted entirely.
        ///
        /// <para>
        /// <b>The offset is verified, not merely range-checked.</b> A <c>startxref</c> value is
        /// whatever the file said, even when the parser then had to recover the document by
        /// scanning because that value was wrong. An in-range but bogus offset is the dangerous
        /// case: it makes the format probe read arbitrary bytes, and it puts a <c>/Prev</c> into
        /// the appended trailer that points at nothing, breaking the chain and leaving every
        /// original object unreachable — turning a file the parser had recovered into one that
        /// cannot be opened at all.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <c>headerOffset</c> is where the original's header is — the offsets are counted from it —
        /// and <c>position</c> receives the verified stream position of the section in the original,
        /// or -1.
        /// </remarks>
        private static long ResolvePreviousXrefOffset(
            byte[] originalPdf, long headerOffset, IncrementalUpdateOptions options,
            out XrefSectionKind kind, out long position)
        {
            kind = XrefSectionKind.None;
            position = -1;

            long offset = options.PreviousXrefOffset;
            if (offset < 0)
            {
                offset = 0;
                try
                {
                    PdfParser parser = options.Document?.Parser;
                    if (parser != null) offset = parser.StartXrefOffset;
                }
                catch (Exception ex)
                {
                    Report(options, "IncrementalUpdateWriter.ResolvePreviousXrefOffset",
                        "the parser's start-xref offset was unavailable; falling back to a tail scan", ex);
                    offset = 0;
                }
            }

            position = LocateXrefSection(originalPdf, offset, headerOffset, out kind);
            if (position >= 0) return offset;

            // The supplied offset is unusable; the literal `startxref` token may still be right
            // (a parser can hold a stale value after recovering a damaged file).
            long scanned = ScanStartXref(originalPdf);
            if (scanned != offset)
            {
                position = LocateXrefSection(originalPdf, scanned, headerOffset, out kind);
                if (position >= 0) return scanned;
            }

            Report(options, "IncrementalUpdateWriter.ResolvePreviousXrefOffset",
                offset == 0
                    ? "the previous cross-reference offset could not be determined; /Prev is omitted from the appended revision"
                    : "the previous start-xref offset (" + offset.ToString(CultureInfo.InvariantCulture) +
                      ") does not point at a cross-reference section; /Prev is omitted from the appended revision");
            kind = XrefSectionKind.None;
            position = -1;
            return 0;
        }

        /// <summary>
        /// Identifies the cross-reference section at <paramref name="offset"/>, or
        /// <see cref="XrefSectionKind.None"/> when the bytes there are not one.
        ///
        /// <para>
        /// An <c>N G obj</c> header is <b>not</b> accepted on its own: a bogus offset lands
        /// inside an ordinary object just as easily as on a cross-reference stream, and mistaking
        /// one for the other is what makes a writer emit an xref stream onto a classic-xref file.
        /// The object's dictionary must actually declare <c>/Type /XRef</c>.
        /// </para>
        /// </summary>
        internal static XrefSectionKind ClassifyXrefSectionAt(byte[] bytes, long offset)
        {
            if (bytes == null) return XrefSectionKind.None;
            LocateXrefSection(bytes, offset, PdfHeaderLocator.OriginOf(bytes, bytes.Length), out XrefSectionKind kind);
            return kind;
        }

        /// <summary>
        /// The classification itself, over <c>bytes[start..end)</c>. Shared by the whole-file and
        /// windowed entry points so the two can never disagree about what a section looks like —
        /// the windowed one passes <c>start = 0</c>, which the whole-file guard rejects as "no
        /// offset at all", so the range check has to live in the callers rather than here.
        /// </summary>
        private static XrefSectionKind ClassifyFrom(byte[] bytes, int start, int end)
        {
            int p = start;
            if (StartsWithXrefToken(bytes, p)) return XrefSectionKind.Classic;

            // "N G obj" ?
            while (p < end && IsPdfWhitespace(bytes[p])) p++;
            if (!TrySkipUnsignedInteger(bytes, ref p)) return XrefSectionKind.None;
            if (!SkipAtLeastOneWhitespace(bytes, ref p)) return XrefSectionKind.None;
            if (!TrySkipUnsignedInteger(bytes, ref p)) return XrefSectionKind.None;
            if (!SkipAtLeastOneWhitespace(bytes, ref p)) return XrefSectionKind.None;
            if (!MatchesAscii(bytes, p, "obj")) return XrefSectionKind.None;
            p += 3;

            // …declaring /Type /XRef, somewhere in the object's dictionary. Bounded so a hostile
            // offset cannot make this walk the whole file.
            int limit = (int)Math.Min(end, (long)p + XrefStreamProbeWindowBytes);
            for (int i = p; i + 5 <= limit; i++)
            {
                if (bytes[i] != (byte)'/') continue;
                if (MatchesAscii(bytes, i + 1, "XRef")) return XrefSectionKind.Stream;
            }

            return XrefSectionKind.None;
        }

        private static bool TrySkipUnsignedInteger(byte[] bytes, ref int p)
        {
            int start = p;
            while (p < bytes.Length && bytes[p] >= (byte)'0' && bytes[p] <= (byte)'9') p++;
            return p > start;
        }

        private static bool SkipAtLeastOneWhitespace(byte[] bytes, ref int p)
        {
            int start = p;
            while (p < bytes.Length && IsPdfWhitespace(bytes[p])) p++;
            return p > start;
        }

        /// <summary>
        /// Scans the tail of the file for the <c>startxref</c> keyword and parses the offset that
        /// follows it. Used only when the supplied offset was unusable — it locates the literal
        /// token rather than counting lines backwards from the end, so a missing <c>%%EOF</c> or
        /// extra trailing whitespace cannot shift the result.
        /// </summary>
        private static long ScanStartXref(byte[] bytes)
        {
            const string Token = "startxref";
            if (bytes == null || bytes.Length < Token.Length) return 0;

            int windowStart = Math.Max(0, bytes.Length - StartXrefScanWindowBytes);
            int found = -1;
            for (int i = bytes.Length - Token.Length; i >= windowStart; i--)
            {
                if (bytes[i] == (byte)'s' && MatchesAscii(bytes, i, Token)) { found = i; break; }
            }
            if (found < 0) return 0;

            int p = found + Token.Length;
            while (p < bytes.Length && IsPdfWhitespace(bytes[p])) p++;

            long value = 0;
            bool any = false;
            while (p < bytes.Length && bytes[p] >= (byte)'0' && bytes[p] <= (byte)'9')
            {
                if (value > (long.MaxValue - 9) / 10) return 0; // absurd offset — treat as unknown
                value = value * 10 + (bytes[p] - (byte)'0');
                any = true;
                p++;
            }

            return any ? value : 0;
        }

        /// <summary>
        /// Whether the appended revision should use a classic <c>xref</c>/<c>trailer</c> section,
        /// based on the <em>verified</em> kind of the section it supersedes.
        ///
        /// <para>
        /// The section kind must come from <see cref="ClassifyXrefSectionAt"/>, never from a raw
        /// "are these bytes the token <c>xref</c>?" test at an unverified offset: answering "not
        /// <c>xref</c>, therefore a stream" for bytes that are neither is how a classic-xref file
        /// ends up with an xref-stream revision appended to it, which no reader can follow.
        /// </para>
        /// </summary>
        private static bool UseClassicXrefSection(XrefSectionKind previousKind, IncrementalUpdateOptions options)
        {
            if (options.Format == XrefSectionFormat.ClassicTable) return true;
            if (options.Format == XrefSectionFormat.CrossReferenceStream) return false;

            if (previousKind == XrefSectionKind.Classic) return true;
            if (previousKind == XrefSectionKind.Stream) return false;

            // No usable offset: fall back to the parser's merged trailer. It only carries
            // /Type /XRef if some section in the /Prev chain was a stream. Both trailers are
            // consulted — a caller that pre-whitelisted its own trailer stripped /Type out of
            // it, and that must not read as "the original was a classic table".
            try
            {
                if (DeclaresXrefStream(options.OriginalTrailer) ||
                    DeclaresXrefStream(options.Document?.Trailer))
                {
                    Report(options, "IncrementalUpdateWriter.UseClassicXrefSection",
                        "the previous cross-reference offset is unknown; the merged trailer indicates an xref stream, so one is written");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Report(options, "IncrementalUpdateWriter.UseClassicXrefSection",
                    "the trailer could not be inspected for /Type /XRef; a classic xref section is written", ex);
            }

            return true;
        }

        /// <summary>
        /// Whether <paramref name="trailer"/> came from (or was merged with) a cross-reference
        /// stream. <c>/W</c> is checked as well as <c>/Type</c>: it is the mandatory field-width
        /// array, so a dictionary carrying one is an xref stream's whatever its <c>/Type</c> says.
        /// </summary>
        private static bool DeclaresXrefStream(PdfDictionary trailer)
        {
            if (trailer == null) return false;
            if (trailer[PdfName.Type] is PdfName type && type == PdfName.XRef) return true;
            return trailer[PdfName.W] != null;
        }

        private static bool StartsWithXrefToken(byte[] bytes, int offset)
        {
            int p = offset;
            while (p < bytes.Length && IsPdfWhitespace(bytes[p])) p++;

            if (!MatchesAscii(bytes, p, "xref")) return false;

            // "xref" must be a standalone token — guard against an object whose header or content
            // merely starts with those four letters.
            int after = p + 4;
            return after >= bytes.Length || IsPdfWhitespace(bytes[after]);
        }

        private static bool MatchesAscii(byte[] bytes, int offset, string token)
        {
            if (offset < 0 || offset + token.Length > bytes.Length) return false;
            for (int i = 0; i < token.Length; i++)
                if (bytes[offset + i] != (byte)token[i]) return false;
            return true;
        }

        // §7.2.3 Table 1. One implementation, in PdfConstants — this was a fourth private copy of
        // the same six bytes, and the copy that mattered was PdfParser.FindStartXref's INLINE set
        // of three, which is why a survey for "IsPdfWhitespace" would never have found the defect.
        private static bool IsPdfWhitespace(byte b) => PdfConstants.IsWhitespace(b);

        private static bool NeedsLeadingEol(byte[] originalBytes)
        {
            if (originalBytes.Length == 0) return false;
            byte last = originalBytes[originalBytes.Length - 1];
            return last != (byte)'\n' && last != (byte)'\r';
        }

        // ------------------------------------------------------------------
        //  Cross-reference stream encoding
        // ------------------------------------------------------------------

        // The field widths, /W, /Index and the record layout are XrefStreamFields' — §7.5.8's
        // answers, shared with PdfCompressedFileWriter, which writes the same stream for a whole
        // file; neither carries a copy.
        private static byte[] BuildXrefStreamData(List<XrefEntry> sortedEntries, int offsetWidth)
        {
            var data = new byte[sortedEntries.Count * XrefStreamFields.RecordSize(offsetWidth)];

            int p = 0;
            foreach (XrefEntry entry in sortedEntries)
            {
                // Field 1 — entry type. Objects this writer appends are uncompressed in-file
                // objects (type 1); a re-declared original that lives inside an /ObjStm keeps its
                // type-2 shape, which is the only way to name it at all. Field 2 is its byte offset
                // or its container's object number; field 3 its generation or its index within that
                // container.
                XrefStreamFields.WriteRecord(
                    data, ref p,
                    entry.Compressed ? (byte)2 : (byte)1,
                    entry.Offset,
                    entry.Generation,
                    offsetWidth);
            }

            return data;
        }

        // ------------------------------------------------------------------
        //  Small helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Delivers one anomaly to <see cref="IncrementalUpdateOptions.DiagnosticSink"/>.
        /// </summary>
        /// <remarks>
        /// Goes through <see cref="DiagnosticFanOut"/> rather than invoking the delegate directly,
        /// and both halves of that matter here. A host that aggregates telemetry can attach more
        /// than one handler, and a single multicast invoke means the first to throw skips the rest.
        /// More seriously, there was no <c>catch</c>: this writer's contract — stated on
        /// <see cref="IncrementalUpdateOptions.DiagnosticSink"/> and relied on by every caller that
        /// falls back to the original bytes — is that it <b>never throws</b>, and a host's logging
        /// callback could break that from outside.
        /// </remarks>
        private static void Report(
            IncrementalUpdateOptions options, string operation, string detail, Exception ex = null)
        {
            DiagnosticFanOut.Invoke(options?.DiagnosticSink, operation, detail, ex);
        }

        /// <summary>
        /// Object-number order, with unusable entries dropped and duplicates collapsed — two
        /// entries for one object number would produce a cross-reference section that names the
        /// same object twice, and only one of the two offsets can be right.
        /// </summary>
        private static SortedList<int, IndirectObject> Order(
            IReadOnlyList<IndirectObject> objects, IncrementalUpdateOptions options)
        {
            var ordered = new SortedList<int, IndirectObject>();
            if (objects == null) return ordered;

            for (int i = 0; i < objects.Count; i++)
            {
                IndirectObject obj = objects[i];

                if (obj == null || obj.ObjectNumber <= 0)
                {
                    Report(options, "IncrementalUpdateWriter.WriteUpdate",
                        "an entry with object number " +
                        (obj == null ? "(null entry)" : obj.ObjectNumber.ToString(CultureInfo.InvariantCulture)) +
                        " is not a valid indirect object; it was skipped");
                    continue;
                }

                if (ordered.ContainsKey(obj.ObjectNumber))
                {
                    Report(options, "IncrementalUpdateWriter.WriteUpdate",
                        "object " + obj.ObjectNumber.ToString(CultureInfo.InvariantCulture) +
                        " appears more than once; the last occurrence was used");
                }

                ordered[obj.ObjectNumber] = obj;
            }

            return ordered;
        }

        /// <summary>
        /// <c>/Size</c> must be strictly greater than every object number in the file, or strict
        /// readers reject the entries beyond it.
        /// </summary>
        private static int ComputeSize(IncrementalUpdateOptions options, int highestObjectNumber)
        {
            int known = DocumentMaxObjectNumber(options);
            int size = Math.Max(known, highestObjectNumber) + 1;
            return Math.Max(size, options.Size);
        }

        private static int DocumentMaxObjectNumber(IncrementalUpdateOptions options)
        {
            try
            {
                CrossReferenceTable xref = options.Document?.CrossReferenceTable;
                if (xref != null) return xref.GetMaxObjectNumber();
            }
            catch (Exception ex)
            {
                Report(options, "IncrementalUpdateWriter.ComputeSize",
                    "the highest existing object number could not be read; /Size covers only the appended objects", ex);
            }

            return 0;
        }

        private static int AllocateXrefStreamObjectNumber(
            IncrementalUpdateOptions options, int highestObjectNumber)
        {
            if (options.XrefStreamObjectNumberAllocator != null)
            {
                int supplied = options.XrefStreamObjectNumberAllocator(highestObjectNumber);
                if (supplied > highestObjectNumber) return supplied;

                Report(options, "IncrementalUpdateWriter.AllocateXrefStreamObjectNumber",
                    "the supplied cross-reference stream object number (" +
                    supplied.ToString(CultureInfo.InvariantCulture) +
                    ") collides with an object this revision writes; the next free number was used instead");
            }

            return Math.Max(DocumentMaxObjectNumber(options), highestObjectNumber) + 1;
        }

        /// <summary>One row of the appended revision's cross-reference section.</summary>
        private readonly struct XrefEntry
        {
            internal XrefEntry(int objectNumber, int generation, long offset)
            {
                ObjectNumber = objectNumber;
                Generation = generation;
                Offset = offset;
                Compressed = false;
            }

            private XrefEntry(int objectNumber, int containerObjectNumber, int indexInContainer, bool compressed)
            {
                ObjectNumber = objectNumber;
                Generation = indexInContainer;
                Offset = containerObjectNumber;
                Compressed = compressed;
            }

            /// <summary>
            /// A type-2 row: the object lives at <paramref name="indexInContainer"/> inside object
            /// stream <paramref name="containerObjectNumber"/> (§7.5.8.3). Only a cross-reference
            /// <i>stream</i> can carry one — a classic table has no type field.
            /// </summary>
            internal static XrefEntry InObjectStream(int objectNumber, int containerObjectNumber, int indexInContainer)
                => new XrefEntry(objectNumber, containerObjectNumber, indexInContainer, compressed: true);

            internal int ObjectNumber { get; }

            /// <summary>Generation for a type-1 row; index within the container for a type-2 row.</summary>
            internal int Generation { get; }

            /// <summary>Byte offset for a type-1 row; the container's object number for a type-2 row.</summary>
            internal long Offset { get; }

            internal bool Compressed { get; }
        }
    }

    public sealed class IndirectObject
    {
        public int ObjectNumber { get; set; }
        public int GenerationNumber { get; set; }
        public PdfObject Object { get; set; }

        public IndirectObject(int objectNumber, int generation, PdfObject obj)
        {
            ObjectNumber = objectNumber;
            GenerationNumber = generation;
            Object = obj;
        }
    }

    public sealed class WriteResult
    {
        public Dictionary<int, long> ObjectOffsets { get; } = new Dictionary<int, long>();
        public long XrefOffset { get; set; }
        public long TotalLength { get; set; }
    }
}
