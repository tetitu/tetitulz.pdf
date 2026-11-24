// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Repairs malformed or corrupt PDF files by scanning the byte stream for
    /// indirect objects and rebuilding the cross-reference table from scratch.
    ///
    /// <para>
    /// The repair strategy:
    /// <list type="number">
    ///   <item>Scan the raw bytes for the pattern <c>N G obj</c> (N ≥ 1) to locate
    ///     every indirect object regardless of the original xref table. Header recognition is
    ///     <see cref="Tetitulz.Pdf.Core.Parsing.ObjectHeaderScanner"/>'s — the same rule the
    ///     parser's own recovery scan applies, so this tool can never be the stricter of the
    ///     two.</item>
    ///   <item>Walk each object's tokens to find where it ends, so that <c>endobj</c> or a header
    ///     inside a string, a comment or stream data ends nothing.</item>
    ///   <item>Ask the parser for what the scan cannot see: the objects kept inside object
    ///     streams, which have no header of their own.</item>
    ///   <item>Copy the objects under their own numbers and generations and write a fresh
    ///     classic xref table + trailer, carrying <c>/Root</c>, <c>/Info</c>, <c>/Encrypt</c> and
    ///     <c>/ID</c> from the file's trailers — every <c>trailer</c> keyword's, and the parser's
    ///     reading of a cross-reference stream's.</item>
    ///   <item>Objects that cannot be recovered are skipped and reported rather than aborting
    ///     the entire repair.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para><b>Four real PDF 1.5+ files came out of this tool with no pages</b> (1, 2, 353 and
    /// 527 before) while it reported a clean repair. Their catalog and page tree lived in object
    /// streams the header scan never saw, and with no <c>trailer</c> keyword the root came from a
    /// <c>/Type /Catalog</c> search that read 512 bytes <i>from</i> each object — past a short
    /// <c>/Info</c> dictionary and into the catalog after it, naming <c>/Info</c> as the catalog.
    /// </para>
    /// <para><b>An encrypted document is refused when it cannot be carried whole.</b> Its objects
    /// are copied still encrypted, which a rebuilt trailer keeps readable only by carrying
    /// <c>/Encrypt</c> and <c>/ID</c>; objects inside its object streams cannot be read without the
    /// password at all. Either gap would produce a file nothing can open, so the call throws
    /// <see cref="PdfEncryptedDocumentException"/> instead, like every other whole-file rewrite.</para>
    /// </remarks>
    public static class PdfRepair
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Repairs a PDF file and writes the result to a new file.</summary>
        public static PdfRepairResult Repair(string inputPath, string outputPath)
        {
            if (inputPath  == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            using var fs  = File.OpenRead(inputPath);
            using var fso = File.Create(outputPath);
            return Repair(fs, fso);
        }

        /// <summary>Repairs a PDF stream and writes the result to another stream.</summary>
        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted and either keeps objects inside object streams or its
        /// encryption dictionary is not among the objects the scan found, so the repaired file
        /// could not be decrypted.
        /// </exception>
        public static PdfRepairResult Repair(Stream input, Stream output)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            byte[] raw = Core.IO.StreamBytes.ReadAll(input);   // rewinds a seekable input
            return RebuildFromRaw(raw, output);
        }

        // ----------------------------------------------------------------
        //  Core — object discovery and rebuild
        // ----------------------------------------------------------------

        /// <summary>
        /// How many <c>trailer</c> keywords — the newest in the file — supply the rebuilt trailer's
        /// entries. Each is parsed to the end of its dictionary, which for an unclosed one is the end
        /// of the file.
        /// </summary>
        private const int MaxTrailersRead = 16;

        private static PdfRepairResult RebuildFromRaw(byte[] raw, Stream output)
        {
            string version = DetectVersion(raw) ?? "1.4";
            var errors = new List<string>();

            PdfParser parser = TryParse(raw);
            List<RawObject> located = RawObjectScanner.Scan(raw, parser, errors, out int headersFound);
            Dictionary<int, RawObject> chosen = ChooseCurrentCopies(located, parser);

            PdfDictionary trailer = ReadTrailer(raw, located, parser);
            RefuseUnrepairableEncryption(trailer, chosen, parser);

            // An encrypted document has no compressed objects left to read: it was refused if it had.
            var compressed = new SortedDictionary<int, PdfObject>();
            var containersRead = new HashSet<int>();
            ReadCompressedObjects(parser, compressed, containersRead, errors);

            var numbers = new SortedSet<int>(chosen.Keys);
            numbers.UnionWith(compressed.Keys);

            var xrefs       = new Dictionary<int, long>();
            var generations = new Dictionary<int, int>();
            int recovered   = 0;
            var unreadContainers = new List<int>();

            // The HIGHEST object number actually written, not how many were written. A rebuilt
            // cross-reference table is indexed by object number, so those are the same value only
            // when the numbering is dense from 1 — see the note on WriteXref.
            int maxObjNum = 0;

            using var buf = new MemoryStream();
            var writer = new PdfWriter(buf);
            WriteRaw(buf, $"%PDF-{version}\n");
            WriteBinaryMarker(buf);

            foreach (int objNum in numbers)
            {
                if (compressed.TryGetValue(objNum, out PdfObject fromStream))
                {
                    // An object in an object stream always has generation 0 (§7.5.7).
                    xrefs[objNum] = writer.WriteIndirectObject(objNum, 0, fromStream);
                    generations[objNum] = 0;
                }
                else
                {
                    RawObject obj = chosen[objNum];

                    // A cross-reference stream describes offsets this file no longer has. An object
                    // stream's contents are written out one object at a time, and a classic table
                    // cannot point into a stream, so the container itself is dead weight.
                    if (obj.Declares(PdfName.XRef)) continue;
                    if (obj.Declares(PdfName.ObjStm))
                    {
                        if (!containersRead.Contains(objNum)) unreadContainers.Add(objNum);
                        continue;
                    }

                    xrefs[objNum] = buf.Position;
                    buf.Write(raw, (int)obj.Start, (int)(obj.End - obj.Start));
                    if (obj.NeedsEndobj)
                        WriteRaw(buf, "\nendobj\n");
                    else if (raw[obj.End - 1] != (byte)'\n')
                        buf.WriteByte((byte)'\n');

                    // The generation the object's own header declares: generation 0 for every object
                    // would contradict a copied `12 3 obj` header and make a strict reader drop the
                    // object as a stale entry.
                    generations[objNum] = obj.Generation;
                }

                recovered++;
                if (objNum > maxObjNum) maxObjNum = objNum;
            }

            if (unreadContainers.Count > 0)
                errors.Add($"{unreadContainers.Count} object stream(s) were not read, so the objects inside " +
                           $"them were not recovered (the first: object {unreadContainers[0]}).");

            if (recovered == 0)
                errors.Add(NothingRecoveredReason(headersFound, raw.Length));

            // §7.5.5: /Size is one greater than the highest object number IN THE FILE, and the
            // table it describes runs 0 .. /Size − 1 — neither is `recovered + 1`.
            int size = maxObjNum + 1;

            long xrefOff = buf.Position;
            WriteXref(buf, xrefs, generations, size);

            int rootObjNum = FindRootObjectNumber(trailer, chosen, compressed, xrefs, raw);
            WriteTrailer(writer, trailer, rootObjNum, xrefs, generations, size, xrefOff);

            output.Write(buf.ToArray(), 0, (int)buf.Length);

            return new PdfRepairResult(recovered, errors);
        }

        // ----------------------------------------------------------------
        //  Which copy of an object
        // ----------------------------------------------------------------

        /// <summary>
        /// One copy of each object number: the last in the file, unless the parser's
        /// cross-reference data names another copy's offset.
        /// </summary>
        /// <remarks>
        /// A later revision's copy of an object follows the earlier one, which is why the last
        /// copy is the default. Where the parser did read a table, an in-use entry is the file's
        /// own word on which copy is current, and it wins.
        /// </remarks>
        private static Dictionary<int, RawObject> ChooseCurrentCopies(List<RawObject> located, PdfParser parser)
        {
            var chosen = new Dictionary<int, RawObject>();
            var byOffset = new Dictionary<long, RawObject>(located.Count);
            foreach (RawObject obj in located)
            {
                chosen[obj.ObjectNumber] = obj;
                byOffset[obj.Start] = obj;
            }

            if (parser?.CrossReferenceTable != null)
                foreach (CrossReferenceEntry entry in parser.CrossReferenceTable.AllEntries)
                    if (entry.Type == CrossReferenceEntry.EntryType.Normal &&
                        byOffset.TryGetValue(entry.Offset, out RawObject named) &&
                        named.ObjectNumber == entry.ObjectNumber)
                        chosen[entry.ObjectNumber] = named;

            return chosen;
        }

        // ----------------------------------------------------------------
        //  What only the parser can see
        // ----------------------------------------------------------------

        /// <summary>
        /// The parser's reading of the file's cross-reference data — its own recovery scan
        /// included — or <see langword="null"/> when even that gives up.
        /// </summary>
        /// <remarks>
        /// <para>A <see cref="PdfParser"/> rather than a <c>PdfDocument</c>: opening a document also
        /// demands a catalog, and a file whose catalog cannot be found is one this tool is asked to
        /// rescue — its object streams can still be read.</para>
        /// <para>Not reported when it fails: a file the parser cannot read is the case this tool exists
        /// for, and the header scan carries on without it. <see cref="PdfParser.LastXrefSectionError"/>
        /// says why.</para>
        /// </remarks>
        private static PdfParser TryParse(byte[] raw)
        {
            var parser = new PdfParser(new MemoryStream(raw, writable: false));
            try
            {
                parser.ParseCrossReferences();
                return parser;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Every object the parser's cross-reference data places inside an object stream, read
        /// through the parser — which is what opens the stream.
        /// </summary>
        private static void ReadCompressedObjects(
            PdfParser parser, SortedDictionary<int, PdfObject> objects, HashSet<int> containersRead,
            List<string> errors)
        {
            if (parser?.CrossReferenceTable == null) return;

            int unreadable = 0, outOfRange = 0;
            string firstUnreadable = null;
            foreach (CrossReferenceEntry entry in parser.CrossReferenceTable.AllEntries
                         .Where(e => e.Type == CrossReferenceEntry.EntryType.Compressed)
                         .OrderBy(e => e.ObjectNumber))
            {
                // The rebuilt table has a row for every number up to the highest written, so one
                // entry is enough to make it as large as its number says.
                if (entry.ObjectNumber < 1 || entry.ObjectNumber > RawObjectScanner.MaxObjectNumber)
                {
                    outOfRange++;
                    continue;
                }

                PdfObject obj = parser.GetObject(entry.ObjectNumber);
                if (obj == null || obj is PdfNull)
                {
                    // A null object with no parse error is what the stream holds, and absent is the
                    // same object (§7.3.9).
                    string reason = PdfParser.LastObjectParseError;
                    if (reason == null) continue;
                    if (unreadable++ == 0)
                        firstUnreadable = $"object {entry.ObjectNumber} in object stream {entry.ObjectStreamNumber}: {reason}";
                    continue;
                }

                objects[entry.ObjectNumber] = obj;
                containersRead.Add(entry.ObjectStreamNumber);
            }

            if (unreadable > 0)
                errors.Add($"{unreadable} object(s) inside object streams could not be read (the first: {firstUnreadable}).");
            if (outOfRange > 0)
                errors.Add($"{outOfRange} object(s) inside object streams numbered outside 1..{RawObjectScanner.MaxObjectNumber} were skipped.");
        }

        // ----------------------------------------------------------------
        //  Trailer, encryption and version
        // ----------------------------------------------------------------

        private static readonly PdfName[] TrailerKeys = { PdfName.Root, PdfName.Info, PdfName.Encrypt, PdfName.ID };

        /// <summary>
        /// <c>/Root</c>, <c>/Info</c>, <c>/Encrypt</c> and <c>/ID</c>, each from the newest
        /// <c>trailer</c> keyword's dictionary that has it, then from the parser's trailer.
        /// </summary>
        /// <remarks>
        /// <para>Every keyword's dictionary, not only the last one's <c>/Root</c> and <c>/Info</c>,
        /// which would let a file whose last trailer names nothing but <c>/Size</c> lose its document
        /// information and identifier, and an encrypted one no longer decrypt. The parser's recovery
        /// reads every keyword as well, but only when it recovers: a file it reads through
        /// a sound table keeps the trailer of that table's chain, and an orphaned older trailer — one
        /// no <c>/Prev</c> reaches — is seen by this walk alone.</para>
        /// <para>The parser's trailer is what a PDF 1.5 file needs: its trailer is its
        /// cross-reference stream's dictionary (§7.5.8.2), which the parser reads — and, in recovery,
        /// merges in when no keyword supplied a <c>/Root</c>.</para>
        /// <para>A keyword inside an object — an uncompressed embedded PDF's — is not this file's
        /// trailer.</para>
        /// </remarks>
        private static PdfDictionary ReadTrailer(byte[] raw, List<RawObject> located, PdfParser parser)
        {
            var trailer = new PdfDictionary();
            foreach (long at in TrailerKeywords(raw, located))
                AdoptTrailerKeys(trailer, RawObjectScanner.ReadDictionary(raw, at + TrailerKeyword.Length, raw.Length));
            AdoptTrailerKeys(trailer, parser?.Trailer);
            return trailer;
        }

        private static void AdoptTrailerKeys(PdfDictionary trailer, PdfDictionary source)
        {
            if (source == null) return;
            foreach (PdfName key in TrailerKeys)
            {
                PdfObject value = source[key];
                if (value != null && !(value is PdfNull) && !trailer.ContainsKey(key))
                    trailer[key] = value;
            }
        }

        /// <summary>The newest <c>trailer</c> keywords that lie outside every located object, newest first.</summary>
        private static List<long> TrailerKeywords(byte[] raw, List<RawObject> located)
        {
            var found = new List<long>();
            for (long i = raw.Length - TrailerKeyword.Length; i >= 0 && found.Count < MaxTrailersRead; i--)
            {
                if (raw[i] != TrailerKeyword[0] || !RawObjectScanner.MatchesAt(raw, i, TrailerKeyword)) continue;

                // The start of a token, not the tail of a longer word. What follows needs no such
                // test: anything glued on is parsed as the dictionary, and is not one.
                if (i > 0 && !PdfConstants.IsEndOfToken(raw[i - 1])) continue;
                if (InsideAnObject(located, i)) continue;
                found.Add(i);
            }
            return found;
        }

        /// <summary>Whether <paramref name="position"/> lies within a located object's bytes.</summary>
        private static bool InsideAnObject(List<RawObject> located, long position)
        {
            int lo = 0, hi = located.Count - 1, last = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (located[mid].Start <= position) { last = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return last >= 0 && position < located[last].End;
        }

        /// <summary>
        /// Throws when an encrypted document cannot be carried whole: when objects live in object
        /// streams, or when its encryption dictionary is not among the objects found.
        /// </summary>
        private static void RefuseUnrepairableEncryption(
            PdfDictionary trailer, Dictionary<int, RawObject> chosen, PdfParser parser)
        {
            PdfObject encrypt = trailer[PdfName.Encrypt];
            if (encrypt == null) return;

            bool objectStreams =
                chosen.Values.Any(o => o.Declares(PdfName.ObjStm)) ||
                (parser?.CrossReferenceTable?.AllEntries.Any(e => e.Type == CrossReferenceEntry.EntryType.Compressed) ?? false);
            if (objectStreams)
                throw new PdfEncryptedDocumentException(
                    "The document is encrypted and keeps objects inside object streams. They cannot " +
                    "be read without the password, and a rebuilt classic cross-reference table cannot " +
                    "point into a stream, so the repair would lose them. Decrypt the document first, " +
                    "then repair the decrypted copy.");

            bool carried = encrypt is PdfDictionary ||
                           (encrypt is PdfReference reference && chosen.ContainsKey(reference.ReferenceObjectNumber));
            if (!carried)
                throw new PdfEncryptedDocumentException(
                    "The document is encrypted and its encryption dictionary is not among the objects " +
                    "found, so a rebuilt trailer cannot carry it and the repaired file could not be " +
                    "decrypted. Decrypt the document first, then repair the decrypted copy.");
        }

        /// <summary>
        /// The header's version, as the parser reads it, or <see langword="null"/> when there is no
        /// readable one.
        /// </summary>
        /// <remarks>
        /// <b>This had its own search, over the first 20 bytes</b>: a header after a preamble — the
        /// parser accepts one anywhere in its 1 024-byte window (§7.5.2 NOTE 1) — was not found and
        /// the repaired file claimed 1.4, and a marker with no digits after it read as the empty
        /// version, so the file began with a bare <c>%PDF-</c> no reader accepts.
        /// </remarks>
        private static string DetectVersion(byte[] raw)
        {
            try
            {
                return new PdfParser(new MemoryStream(raw, writable: false)).ParseHeader().ToString();
            }
            catch (PdfParseException)
            {
                return null;
            }
        }

        /// <summary>
        /// The catalog's object number: the trailer's <c>/Root</c> when it names a written object,
        /// otherwise the latest object whose own dictionary declares <c>/Type /Catalog</c>.
        /// </summary>
        /// <remarks>
        /// <b>Not a fixed 512 bytes from each object's start</b>, which would run past a short object
        /// into the next one: a document whose <c>/Info</c> dictionary comes first — a common
        /// producer layout — would have <c>/Info</c> named as its catalog, and the repaired file open
        /// with no pages. The latest catalog in the file wins, as in the parser's own
        /// recovery.
        /// </remarks>
        private static int FindRootObjectNumber(
            PdfDictionary trailer,
            Dictionary<int, RawObject> chosen,
            SortedDictionary<int, PdfObject> compressed,
            Dictionary<int, long> written,
            byte[] raw)
        {
            if (trailer[PdfName.Root] is PdfReference r && written.ContainsKey(r.ReferenceObjectNumber))
                return r.ReferenceObjectNumber;

            foreach (RawObject obj in chosen.Values.Where(o => !o.IsStream).OrderByDescending(o => o.Start))
                if (written.ContainsKey(obj.ObjectNumber) && IsCatalog(raw, obj))
                    return obj.ObjectNumber;

            foreach (var kvp in compressed)
                if (kvp.Value is PdfDictionary dict && !(dict is PdfStream) &&
                    dict.GetName(PdfName.Type) == PdfName.Catalog)
                    return kvp.Key;

            return written.Count > 0 ? written.Keys.Min() : 1;
        }

        private static bool IsCatalog(byte[] raw, RawObject obj)
            => RawObjectScanner.IndexOf(raw, CatalogName, obj.BodyStart, obj.DictionaryEnd) >= 0 &&
               RawObjectScanner.ReadDictionary(raw, obj.BodyStart, obj.DictionaryEnd)?.GetName(PdfName.Type) == PdfName.Catalog;

        // ----------------------------------------------------------------
        //  Writing helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Writes one classic cross-reference subsection covering object numbers
        /// <c>0 .. size − 1</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><paramref name="size"/> is one greater than the highest object number written — never
        /// the number of objects recovered.</b> The table is <i>indexed by object number</i>: row
        /// <c>i</c> is object <c>i</c>, at a fixed 20 bytes each. Passing the count made the two
        /// agree only for numbering that is dense from 1, and a recovered file's numbering routinely
        /// is not: a file holding objects 1, 2, 3 and 9 got a table declaring <c>0 5</c>, so
        /// <b>object 9 was copied into the body and then had no cross-reference entry at all</b> —
        /// every reference to it resolved to null in the "repaired" file, and the trailer's
        /// <c>/Size</c> said the document contained no such object.
        /// </para>
        /// <para>
        /// A number the table covers but the file does not contain is written as a <b>free</b> entry
        /// (§7.5.4). The alternative — leaving it in-use at offset 0 — points a reader at the file
        /// header, which is worse than an absent object.
        /// </para>
        /// <para>
        /// The table's size is therefore bounded by <see cref="RawObjectScanner.MaxObjectNumber"/>, i.e. ~20 MB of
        /// rows in the worst case a hostile file can force. That ceiling is the bound; one derived
        /// from the object <i>count</i> would not bound the numbers a file chooses.
        /// </para>
        /// </remarks>
        private static void WriteXref(
            MemoryStream buf, Dictionary<int, long> xrefs, Dictionary<int, int> generations, int size)
        {
            WriteRaw(buf, "xref\n");
            WriteRaw(buf, $"0 {size}\n");
            WriteRaw(buf, PdfXrefFormat.FreeListHead);
            for (int i = 1; i < size; i++)
            {
                if (xrefs.TryGetValue(i, out long off))
                {
                    // The generation the copied object header actually declares — writing 0 here
                    // contradicted that header and made strict readers drop the object.
                    generations.TryGetValue(i, out int gen);
                    WriteRaw(buf, PdfXrefFormat.Entry(off, gen));
                }
                else
                {
                    // A gap in the numbering: free, generation 65535, next-free 0. Byte-identical
                    // to the free-list head, but spelled out because this is not the head — the
                    // head is the row above, and only object 0 is it.
                    WriteRaw(buf, PdfXrefFormat.Entry(
                        0, PdfXrefFormat.MaxGenerationNumber, inUse: false));
                }
            }
        }

        /// <summary>
        /// Why a repair recovered nothing, for <see cref="PdfRepairResult.Errors"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Recovering zero objects is reported: <c>RecoveredObjectCount = 0</c> with an <b>empty</b>
        /// error list is what a repair with nothing to do also returns, so a caller could not tell "this file has no indirect objects" from "every object in it
        /// was refused". The output in both cases is a structurally well-formed but EMPTY document
        /// whose <c>/Root</c> names an object that is not in it, and <c>IsClean</c> said it was
        /// fine.
        /// </para>
        /// <para>
        /// <b>Reported, not thrown.</b> Every other failure in this tool is tolerated and recorded
        /// (CLAUDE.md, Diagnostics Pattern); a repair that produced an empty file has still produced
        /// a file, and the caller decides what that is worth.
        /// </para>
        /// </remarks>
        private static string NothingRecoveredReason(int headersFound, int rawLength)
            => headersFound == 0
                ? $"No indirect objects were recovered: no 'N G obj' header was found in " +
                  $"{rawLength} byte(s), so the repaired file is empty and its /Root names an " +
                  "object that is not in it."
                : $"No indirect objects were recovered: all {headersFound} object header(s) found " +
                  "were skipped (see the other errors), so the repaired file is empty and its " +
                  "/Root names an object that is not in it.";

        /// <summary>
        /// Writes the trailer. <paramref name="size"/> is the same value the table was written with
        /// — the highest object number plus one — because §7.5.5 requires <c>/Size</c> to cover
        /// every object number in the file, and a reader that trusts it will not look at an object
        /// numbered at or above it.
        /// </summary>
        /// <remarks>
        /// <c>/Encrypt</c> and <c>/ID</c> are carried because every copied object is still encrypted
        /// under keys derived from both; a trailer without them made an encrypted document
        /// unreadable. References take the generation the named object's header declares.
        /// </remarks>
        private static void WriteTrailer(
            PdfWriter writer, PdfDictionary source, int rootObjNum, Dictionary<int, long> written,
            Dictionary<int, int> generations, int size, long xrefOff)
        {
            PdfReference Written(int objNum) =>
                new PdfReference(objNum, generations.TryGetValue(objNum, out int gen) ? gen : 0);

            var trailer = new PdfDictionary();
            trailer.Set(PdfName.Size, new PdfNumber(size));
            trailer.Set(PdfName.Root, Written(rootObjNum));

            if (source[PdfName.Info] is PdfReference info && written.ContainsKey(info.ReferenceObjectNumber))
                trailer.Set(PdfName.Info, Written(info.ReferenceObjectNumber));

            switch (source[PdfName.Encrypt])
            {
                case PdfReference encrypt when written.ContainsKey(encrypt.ReferenceObjectNumber):
                    trailer.Set(PdfName.Encrypt, Written(encrypt.ReferenceObjectNumber));
                    break;
                case PdfDictionary encryptDict:
                    trailer.Set(PdfName.Encrypt, encryptDict);
                    break;
            }

            if (source[PdfName.ID] is PdfArray id)
                trailer.Set(PdfName.ID, id);

            writer.WriteTrailer(trailer, xrefOff);
        }

        /// <summary>
        /// The §7.5.2 binary marker: a comment whose four bytes are all <b>&gt; 127</b>, which is
        /// how a transfer tool tells a PDF is binary and must not be newline-translated.
        /// </summary>
        /// <remarks>
        /// <para><b>Written as BYTES because <see cref="WriteRaw"/> cannot carry them.</b> It
        /// encodes through <see cref="Encoding.ASCII"/>, which maps every char above 0x7F to
        /// <c>'?'</c> (0x3F) — so <c>"%\xE2\xE3\xCF\xD3\n"</c> reached the file as
        /// <c>%????</c>, four bytes all UNDER 128. Every repaired document carried a marker that
        /// says the opposite of what §7.5.2 requires, on a code path whose entire purpose is
        /// producing a readable file.</para>
        ///
        /// <para>The chars were right and the encoder threw them away, which is why it survived
        /// review: the literal reads correctly. <see cref="WriteRaw"/> keeps ASCII deliberately —
        /// every other thing it writes is PDF syntax, and an encoder that silently substituted
        /// there would hide a different bug.</para>
        /// </remarks>
        private static void WriteBinaryMarker(Stream s)
        {
            byte[] marker = { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' };
            s.Write(marker, 0, marker.Length);
        }

        private static void WriteRaw(Stream s, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        // ----------------------------------------------------------------
        //  Keywords
        // ----------------------------------------------------------------

        private static readonly byte[] TrailerKeyword = Encoding.ASCII.GetBytes("trailer");
        private static readonly byte[] CatalogName = Encoding.ASCII.GetBytes("/Catalog");
    }

    /// <summary>Result of a PDF repair operation.</summary>
    public sealed class PdfRepairResult
    {
        /// <summary>Number of indirect objects that were successfully recovered.</summary>
        public int RecoveredObjectCount { get; }

        /// <summary>
        /// Non-fatal errors encountered during recovery (skipped objects, parse failures, etc.).
        /// </summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>True when no errors occurred during recovery.</summary>
        public bool IsClean => Errors.Count == 0;

        public PdfRepairResult(int recoveredObjectCount, IReadOnlyList<string> errors)
        {
            RecoveredObjectCount = recoveredObjectCount;
            Errors               = errors ?? Array.Empty<string>();
        }

        public override string ToString()
            => $"Recovered {RecoveredObjectCount} objects, {Errors.Count} error(s)";
    }
}
