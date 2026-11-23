// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Tuning for <see cref="PdfCompressedFileWriter"/>. Every value is clamped into a range the
    /// <b>read</b> side accepts, so no setting here can produce a file this library would refuse
    /// to reopen.
    /// </summary>
    public sealed class PdfCompressedWriteOptions
    {
        /// <summary>
        /// Objects packed into one <c>/ObjStm</c> before a new one is started. Large enough that
        /// the per-stream dictionary and Flate header amortise away, small enough that reading one
        /// object does not force the reader to inflate a megabyte.
        /// </summary>
        public const int DefaultMaxObjectsPerObjectStream = 128;

        /// <summary>
        /// Decoded size at which an object stream is closed and a new one begun, in bytes.
        /// </summary>
        public const int DefaultMaxObjectStreamBytes = 512 * 1024;

        /// <summary>See <see cref="DefaultMaxObjectsPerObjectStream"/>.</summary>
        public int MaxObjectsPerObjectStream { get; set; } = DefaultMaxObjectsPerObjectStream;

        /// <summary>See <see cref="DefaultMaxObjectStreamBytes"/>.</summary>
        public int MaxObjectStreamBytes { get; set; } = DefaultMaxObjectStreamBytes;

        /// <summary>
        /// Encrypts the file being written, for an encrypted document in the compressed layout:
        /// every top-level object's strings and stream data, and each object stream <b>whole</b>,
        /// under its own object number — its members are serialised in the clear inside it, as
        /// ISO 32000-1 §7.5.7 has them, and a reader decrypts the container before it reads them.
        /// The object the trailer's <c>/Encrypt</c> names and the cross-reference stream (§7.6.2,
        /// §7.5.8.2) are written unencrypted. <see langword="null"/> (the default) writes clear
        /// text, which is correct only for an unencrypted file; a trailer naming <c>/Encrypt</c>
        /// without one is reported through <see cref="PdfCompressedFileWriter.LastWriteWarning"/>.
        /// </summary>
        public IPdfObjectEncryptor ObjectEncryptor { get; set; }

        /// <summary>
        /// Deflate level for the object streams and the cross-reference stream.
        /// <see cref="CompressionLevel.Optimal"/> by default — the whole point of this writer is
        /// size, and the streams it compresses are small.
        /// </summary>
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;
    }

    /// <summary>
    /// Writes a complete PDF file using <b>object streams</b> (ISO 32000-1 §7.5.7) and a
    /// <b>cross-reference stream</b> (§7.5.8) — the PDF 1.5 compressed file layout.
    ///
    /// <para><b>Why this exists.</b> <see cref="PdfWriter"/>'s full-file path emits every object as
    /// an uncompressed top-level indirect object under a classic cross-reference table, which costs
    /// <see cref="PdfXrefFormat.EntryLength"/> bytes of xref per object plus an <c>N G obj</c> /
    /// <c>endobj</c> wrapper. <c>PdfParser</c> resolves compressed entries transparently, so a
    /// whole-document rewrite of a modern PDF <i>unpacks</i> its object streams; re-emitting them
    /// that way makes an "optimized" object-heavy file larger than its input. This writer is the
    /// other half of that round trip.</para>
    ///
    /// <para><b>What is never packed</b> — §7.5.7's exclusion list, and getting it wrong produces a
    /// file that parses and then misbehaves rather than one that fails loudly:</para>
    /// <list type="bullet">
    ///   <item>a <see cref="PdfStream"/>, because a stream cannot live inside an object stream;</item>
    ///   <item>an object whose generation number is not 0, because a type-2 cross-reference entry
    ///   has no generation field — §7.5.8.3 defines a compressed object's generation as
    ///   implicitly 0, so packing a gen&gt;0 object silently renumbers it;</item>
    ///   <item>the document's encryption dictionary, which a reader must parse before it can
    ///   decrypt anything (and therefore before it can inflate an object stream);</item>
    ///   <item>any object used as the indirect <c>/Length</c> of a stream — same bootstrap problem,
    ///   and the fourth bullet of §7.5.7's list;</item>
    ///   <item>the cross-reference stream itself, which is a stream and is written last.</item>
    /// </list>
    ///
    /// <para><b>This writer emits clear text.</b> It has no encryptor, so an encrypted document's
    /// <c>/Encrypt</c> dictionary is carried forward but its objects are <i>not</i> re-encrypted;
    /// that is reported through <see cref="LastWriteWarning"/> rather than being left to be
    /// discovered by a reader. Callers that must preserve encryption belong on
    /// <c>Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip</c>.</para>
    /// </summary>
    public static class PdfCompressedFileWriter
    {
        // ------------------------------------------------------------------
        //  Local constants
        //
        //  Write-side counterparts to read-side caps that already live in PdfLimits
        //  (MaxObjStmEntries, MinObjStmIndexBytesPerEntry, MaxObjStmIndexCacheBytes). They are
        //  deliberately local for now; MaxDirectNodesPerObject in particular is a DoS guard and
        //  would be at home in PdfLimits.
        // ------------------------------------------------------------------

        /// <summary>
        /// Object streams do not exist before PDF 1.5 (§7.5.7), so a file written here declares at
        /// least this version regardless of what the caller asked for.
        /// </summary>
        private static readonly PdfVersion MinimumVersion = PdfFeatureVersions.ObjectStreams;

        /// <summary>
        /// Hard ceiling on <see cref="PdfCompressedWriteOptions.MaxObjectsPerObjectStream"/>: the
        /// smaller of two bounds. The read side refuses an <c>/N</c> above
        /// <see cref="PdfLimits.MaxObjStmEntries"/>, and the index-within-stream travels in the
        /// third field of a type-2 cross-reference entry, <see cref="XrefStreamFields.GenerationFieldWidth"/>
        /// bytes wide, so a stream may hold no more members than that field can number.
        /// </summary>
        /// <remarks>
        /// This was the read limit alone, which held while that limit was 65 536. Raised to 131 072
        /// for a real file (GAPS §19h item 6), it let a caller's larger setting pack members past
        /// index 65 535 — and <see cref="XrefStreamFields.WriteRecord"/> clamps field 3 to 65 535, so
        /// every one of them was entered as the member at index 65 535: the file opened and its
        /// objects past that point all read as one other object.
        /// </remarks>
        private static int MaxObjectsPerObjectStreamCeiling =>
            Math.Min(PdfLimits.MaxObjStmEntries, 1 << (8 * XrefStreamFields.GenerationFieldWidth));

        /// <summary>Smallest useful batch — one object per stream still parses, just badly.</summary>
        private const int MinObjectsPerObjectStreamFloor = 1;

        /// <summary>Floor for the decoded-size cap; below this nothing would ever be packed.</summary>
        private const int MinObjectStreamBytesFloor = 1024;

        /// <summary>
        /// Ceiling for the decoded-size cap. Well under <see cref="PdfLimits.MaxDecompressedBytes"/>,
        /// so a stream this writer produces can never trip the read side's inflation guard.
        /// </summary>
        private const int MaxObjectStreamBytesCeiling = 64 * 1024 * 1024;

        /// <summary>
        /// Largest number of <b>direct</b> (non-reference) nodes reachable from one object.
        ///
        /// <para>Both the depth cap and this budget are needed. <see cref="PdfWriter.WriteObject"/>
        /// recurses through arrays and dictionaries and follows a <see cref="PdfReference"/> by
        /// printing <c>N G R</c>, so a reference cycle terminates — but a dictionary holding
        /// <i>itself</i> directly does not, and a graph that shares one sub-object twice per level
        /// is exponential in its depth without ever exceeding it. A parser cannot build either
        /// shape (it allocates a fresh object per token), so this guards a caller-constructed
        /// graph.</para>
        /// </summary>
        private const int MaxDirectNodesPerObject = 1_000_000;

        /// <summary>Bytes per half of the trailer <c>/ID</c> array (§14.4 wants 16).</summary>
        private const int IdHalfLengthBytes = 16;

        // Bytes of the third cross-reference field lived here as a private const, and in
        // IncrementalUpdateWriter as the literal 2 in two places. It is XrefStreamFields'
        // GenerationFieldWidth now, beside the /W array that declares it and the packer that uses
        // it — the three that have to agree.

        // ------------------------------------------------------------------
        //  Diagnostics
        // ------------------------------------------------------------------

        [ThreadStatic] private static string _lastWriteWarning;

        /// <summary>
        /// Everything the last <see cref="Write"/> call on this thread tolerated, newline-separated,
        /// or <see langword="null"/> when it tolerated nothing.
        ///
        /// <para>One call can tolerate several distinct things at once — a raised version, a skipped
        /// entry, an unencrypted rewrite — and a single <c>Last*</c> slot would keep only the last
        /// of them, so the warnings of one call are accumulated rather than overwritten. Reset at
        /// the start of every <see cref="Write"/>, on every path, so a later success can never
        /// report an earlier document's problem.</para>
        /// </summary>
        public static string LastWriteWarning => _lastWriteWarning;

        // ------------------------------------------------------------------
        //  Entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes <paramref name="objects"/> and <paramref name="trailer"/> to
        /// <paramref name="output"/> as a complete PDF file with object streams and a
        /// cross-reference stream.
        /// </summary>
        /// <param name="output">
        /// Receives the whole file, starting at its current position. Every byte offset the file
        /// records — each type-1 cross-reference entry and the <c>startxref</c> value — is measured
        /// from the <c>%PDF-</c> header rather than from the start of this stream, so a
        /// pre-positioned seekable stream yields a PDF that is valid once the prefix is stripped.
        /// A stream whose position cannot be read is treated as starting at 0, and that is reported
        /// through <see cref="LastWriteWarning"/>.
        /// </param>
        /// <param name="version">
        /// Header version. Raised to 1.5 when lower, because object streams do not exist before
        /// then; the raise is reported through <see cref="LastWriteWarning"/>.
        /// </param>
        /// <param name="objects">
        /// The objects to write, by object number. Generation comes from
        /// <see cref="PdfObject.GenerationNumber"/>. Duplicated numbers collapse to the last
        /// occurrence and are reported.
        /// </param>
        /// <param name="trailer">
        /// Source of <c>/Root</c>, <c>/Info</c>, <c>/ID</c> and <c>/Encrypt</c>. Cross-reference
        /// plumbing (<c>/Type</c>, <c>/W</c>, <c>/Index</c>, <c>/Size</c>, <c>/Prev</c>,
        /// <c>/Filter</c>, <c>/Length</c>, <c>/DecodeParms</c>) is <b>not</b> copied — this file's
        /// own values replace it.
        /// </param>
        /// <param name="options">Tuning; defaults are used when <see langword="null"/>.</param>
        /// <exception cref="InvalidOperationException">
        /// An object's direct structure is cyclic or absurdly large, detected <i>before</i> any byte
        /// is written so the caller never receives a half-written file.
        /// </exception>
        public static int Write(
            Stream output,
            PdfVersion version,
            IReadOnlyList<(int objectNumber, PdfObject obj)> objects,
            PdfDictionary trailer,
            PdfCompressedWriteOptions options = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (objects == null) throw new ArgumentNullException(nameof(objects));

            // Reset first, on every path: a caller reads this slot after the call, so leaving the
            // previous document's warnings in it would make a clean write look dirty — and, worse,
            // a dirty one look clean whenever the previous call happened to be clean.
            _lastWriteWarning = null;

            var warnings = new List<string>();
            try
            {
                return WriteCore(output, version, objects, trailer,
                    options ?? new PdfCompressedWriteOptions(), warnings);
            }
            finally
            {
                _lastWriteWarning = warnings.Count == 0 ? null : string.Join("\n", warnings.ToArray());
            }
        }

        private static int WriteCore(
            Stream output,
            PdfVersion version,
            IReadOnlyList<(int objectNumber, PdfObject obj)> objects,
            PdfDictionary trailer,
            PdfCompressedWriteOptions options,
            List<string> warnings)
        {
            int maxPerStream = Clamp(options.MaxObjectsPerObjectStream,
                MinObjectsPerObjectStreamFloor, MaxObjectsPerObjectStreamCeiling);
            int maxStreamBytes = Clamp(options.MaxObjectStreamBytes,
                MinObjectStreamBytesFloor, MaxObjectStreamBytesCeiling);

            PdfVersion effective = version;
            if (effective < MinimumVersion)
            {
                warnings.Add("the requested header version " + version +
                    " predates object streams (ISO 32000-1 §7.5.7); the file declares " +
                    MinimumVersion + " instead");
                effective = MinimumVersion;
            }

            SortedList<int, PdfObject> ordered = Order(objects, warnings);

            // Every structural check runs before a byte is written, so a refusal leaves the caller's
            // stream untouched rather than holding a truncated file.
            foreach (KeyValuePair<int, PdfObject> kvp in ordered)
                EnsureDirectStructureIsFinite(kvp.Value, kvp.Key);

            int encryptObjectNumber = EncryptObjectNumber(trailer);
            if (trailer != null && trailer[PdfName.Encrypt] != null && options.ObjectEncryptor == null)
            {
                warnings.Add("the trailer declares /Encrypt, which was carried forward, but this " +
                    "writer has no encryptor — every object was written in the clear and no reader " +
                    "will decrypt the result");
            }

            HashSet<int> indirectLengthTargets = IndirectLengthTargets(ordered);

            var topLevel = new List<KeyValuePair<int, PdfObject>>();
            var packable = new List<KeyValuePair<int, PdfObject>>();
            foreach (KeyValuePair<int, PdfObject> kvp in ordered)
            {
                if (MustStayTopLevel(kvp.Key, kvp.Value, encryptObjectNumber, indirectLengthTargets))
                    topLevel.Add(kvp);
                else
                    packable.Add(kvp);
            }

            int highestObjectNumber = ordered.Count == 0 ? 0 : ordered.Keys[ordered.Count - 1];

            // The /ID digest covers every byte before the cross-reference stream, which is only
            // computable while writing — so the output is wrapped only when the trailer does not
            // already supply an /ID to carry forward.
            PdfArray existingId = ExistingId(trailer, objects);
            ContentDigest digest = existingId != null ? null : new ContentDigest();

            // Byte offsets in a PDF are measured from the `%PDF-` header (§7.5.4, §7.5.8), not from
            // the start of the stream carrying it — so a caller that pre-positioned `output` (the
            // documented contract: "receives the whole file, starting at its current position")
            // needs every recorded offset and the `startxref` value reduced by where the file
            // began. Recording the raw Stream.Position instead made every type-1 entry and the
            // startxref too large by exactly that prefix, so extracting the PDF portion afterwards
            // yielded an unopenable file with no warning. Same capture as
            // IncrementalUpdateWriter.WriteUpdate.
            long fileStart = 0;
            try
            {
                if (output.CanSeek) fileStart = output.Position;
            }
            catch (Exception ex)
            {
                warnings.Add("the output stream's position could not be read (" + ex.Message +
                    "); offsets are computed as if the PDF starts at byte 0");
                fileStart = 0;
            }

            Stream sink = digest == null ? output : new DigestStream(output, digest);
            var writer = new PdfWriter(sink);
            writer.ObjectEncryptor = options.ObjectEncryptor;
            writer.WriteHeader(effective);

            var rows = new List<XrefRow>(ordered.Count + 8);

            // Object 0 heads the free list (§7.5.4): free, next-free 0, generation 65535. A
            // full-file cross-reference section has to declare it — an incremental one must not,
            // which is why IncrementalUpdateWriter's equivalent is conditional and this is not.
            rows.Add(XrefRow.Free(0, 0, PdfXrefFormat.MaxGenerationNumber));

            foreach (KeyValuePair<int, PdfObject> kvp in topLevel)
            {
                int generation = Clamp(kvp.Value.GenerationNumber, 0, PdfXrefFormat.MaxGenerationNumber);
                // §7.6.2: the /Encrypt dictionary's own strings (/O, /U, /Perms) are never encrypted.
                long offset = writer.WriteIndirectObject(kvp.Key, generation, kvp.Value,
                    encrypt: kvp.Key != encryptObjectNumber);
                rows.Add(XrefRow.InFile(kvp.Key, offset - fileStart, generation));
            }

            int packedCount = WriteObjectStreams(writer, packable, rows, ref highestObjectNumber,
                maxPerStream, maxStreamBytes, options.CompressionLevel, warnings, fileStart,
                out int packedNestedStreamsLost);

            // The packed objects are serialised through a scratch writer, so their losses have to be
            // added here — the outer writer only ever saw the top-level ones.
            int nestedStreamsLost = writer.NestedStreamsWrittenWithoutData + packedNestedStreamsLost;
            if (nestedStreamsLost > 0)
            {
                warnings.Add(nestedStreamsLost.ToString(CultureInfo.InvariantCulture) +
                    " stream(s) were nested inside another object, which ISO 32000-1 §7.3.8 forbids; " +
                    "their data could not be written");
            }

            WriteXrefStream(writer, trailer, rows, ref highestObjectNumber,
                existingId, digest, options.CompressionLevel, warnings, fileStart);

            return packedCount;
        }

        // ------------------------------------------------------------------
        //  Object streams (§7.5.7)
        // ------------------------------------------------------------------

        /// <summary>
        /// Packs <paramref name="packable"/> into <c>/Type /ObjStm</c> streams, writing each one as
        /// an ordinary indirect object (type 1) and recording a type-2 row per member.
        /// </summary>
        private static int WriteObjectStreams(
            PdfWriter writer,
            List<KeyValuePair<int, PdfObject>> packable,
            List<XrefRow> rows,
            ref int highestObjectNumber,
            int maxPerStream,
            int maxStreamBytes,
            CompressionLevel level,
            List<string> warnings,
            long fileStart,
            out int nestedStreamsLost)
        {
            var numbers = new List<int>(maxPerStream);
            var offsets = new List<int>(maxPerStream);
            var bodies = new MemoryStream();
            int packed = 0;
            nestedStreamsLost = 0;

            foreach (KeyValuePair<int, PdfObject> kvp in packable)
            {
                byte[] body = SerialiseBody(kvp.Value, out int lostInThisObject);

                // One object that does not fit a whole stream by itself has to go somewhere, and the
                // only alternative to a top-level object would be emitting a stream past the cap —
                // which is exactly what the cap exists to prevent.
                if (body.Length > maxStreamBytes)
                {
                    warnings.Add("object " + kvp.Key.ToString(CultureInfo.InvariantCulture) +
                        " serialises to " + body.Length.ToString(CultureInfo.InvariantCulture) +
                        " bytes, which exceeds the " + maxStreamBytes.ToString(CultureInfo.InvariantCulture) +
                        "-byte object-stream ceiling; it was written as a top-level object instead");

                    // Written by the real writer instead, which counts its own nested-stream losses —
                    // so the scratch count for this object is deliberately dropped rather than
                    // added twice.
                    long standalone = writer.WriteIndirectObject(kvp.Key, 0, kvp.Value);
                    rows.Add(XrefRow.InFile(kvp.Key, standalone - fileStart, 0));
                    continue;
                }

                nestedStreamsLost += lostInThisObject;

                bool full = numbers.Count >= maxPerStream ||
                            bodies.Length + body.Length > maxStreamBytes;
                if (full)
                {
                    FlushObjectStream(writer, numbers, offsets, bodies, rows, ref highestObjectNumber, level, fileStart);
                    bodies = new MemoryStream();
                }

                numbers.Add(kvp.Key);
                offsets.Add((int)bodies.Length);
                bodies.Write(body, 0, body.Length);
                packed++;

                // The separator is not decoration: two adjacent numeric objects written back to back
                // would tokenize as one number, so the second object would be unreadable and the
                // first would come back wrong.
                bodies.WriteByte((byte)'\n');
            }

            if (numbers.Count > 0)
                FlushObjectStream(writer, numbers, offsets, bodies, rows, ref highestObjectNumber, level, fileStart);

            return packed;
        }

        /// <summary>
        /// Emits one object stream and clears the batch. The body is <c>/First</c> bytes of
        /// <c>"objnum offset "</c> pairs followed by the object bodies at those offsets.
        /// </summary>
        private static void FlushObjectStream(
            PdfWriter writer,
            List<int> numbers,
            List<int> offsets,
            MemoryStream bodies,
            List<XrefRow> rows,
            ref int highestObjectNumber,
            CompressionLevel level,
            long fileStart)
        {
            if (numbers.Count == 0) return;

            var index = new StringBuilder(numbers.Count * 12);
            for (int i = 0; i < numbers.Count; i++)
            {
                index.Append(numbers[i].ToString(CultureInfo.InvariantCulture)).Append(' ')
                     .Append(offsets[i].ToString(CultureInfo.InvariantCulture)).Append(' ');
            }
            index.Append('\n');

            byte[] indexBytes = Encoding.ASCII.GetBytes(index.ToString());
            byte[] bodyBytes = bodies.ToArray();

            var decoded = new byte[indexBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(indexBytes, 0, decoded, 0, indexBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, decoded, indexBytes.Length, bodyBytes.Length);

            var stream = new PdfStream();
            stream.Set(PdfName.Type, PdfName.ObjStm);
            stream.Set(PdfName.N, new PdfNumber(numbers.Count));
            stream.Set(PdfName.First, new PdfNumber(indexBytes.Length));
            ApplyFlate(stream, decoded, level);

            int streamObjectNumber = ++highestObjectNumber;
            long offset = writer.WriteIndirectObject(streamObjectNumber, 0, stream);
            rows.Add(XrefRow.InFile(streamObjectNumber, offset - fileStart, 0));

            for (int i = 0; i < numbers.Count; i++)
                rows.Add(XrefRow.InObjectStream(numbers[i], streamObjectNumber, i));

            numbers.Clear();
            offsets.Clear();
        }

        /// <summary>
        /// Serialises one object exactly as <see cref="PdfWriter"/> would write it at top level,
        /// minus the <c>N G obj</c> / <c>endobj</c> wrapper an object stream does not carry.
        ///
        /// <para>
        /// <c>nestedStreamsLost</c> reports streams nested inside the object whose data could not be
        /// written (§7.3.8).
        /// <b>The count has to travel out of here.</b> The scratch <see cref="PdfWriter"/> is
        /// discarded on return, so its <see cref="PdfWriter.NestedStreamsWrittenWithoutData"/> —
        /// the counter that exists precisely to stop this loss being silent — would be garbage
        /// collected with it, and the outer writer sees only top-level objects, so every packed
        /// object would have no reporting at all: a font dictionary holding its
        /// <c>/FontFile2</c> as a direct stream was written without its bytes and the write
        /// reported success.
        /// </para>
        /// </summary>
        private static byte[] SerialiseBody(PdfObject obj, out int nestedStreamsLost)
        {
            using (var buffer = new MemoryStream())
            {
                var writer = new PdfWriter(buffer);
                writer.WriteObject(obj);
                nestedStreamsLost = writer.NestedStreamsWrittenWithoutData;
                return buffer.ToArray();
            }
        }

        // ------------------------------------------------------------------
        //  Cross-reference stream (§7.5.8)
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes the <c>/Type /XRef</c> stream, then <c>startxref</c> and <c>%%EOF</c>.
        ///
        /// <para>The stream is an indirect object and therefore needs a row for itself in its own
        /// table; that row is added from the writer's current position <i>before</i> the stream is
        /// serialised, which is the only order in which the offset is knowable.</para>
        /// </summary>
        private static void WriteXrefStream(
            PdfWriter writer,
            PdfDictionary trailer,
            List<XrefRow> rows,
            ref int highestObjectNumber,
            PdfArray existingId,
            ContentDigest digest,
            CompressionLevel level,
            List<string> warnings,
            long fileStart)
        {
            int xrefObjectNumber = ++highestObjectNumber;
            long xrefOffset = writer.Position - fileStart;
            rows.Add(XrefRow.InFile(xrefObjectNumber, xrefOffset, 0));
            rows.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));

            int field2Width = XrefStreamFields.Field2Width(rows, r => r.Field2);

            var stream = new PdfStream();
            stream.Set(PdfName.Type, PdfName.XRef);
            stream.Set(PdfName.Size, new PdfNumber(highestObjectNumber + 1));
            stream.Set(PdfName.Index, XrefStreamFields.IndexArray(rows, r => r.ObjectNumber));
            stream.Set(PdfName.W, XrefStreamFields.WidthArray(field2Width));

            CopyTrailerKeys(stream, trailer, warnings);
            stream.Set(PdfName.ID, existingId ?? SynthesiseId(digest));

            ApplyFlate(stream, BuildXrefStreamData(rows, field2Width), level);

            // §7.5.8.2: a cross-reference stream is never encrypted — a reader has to parse it
            // before it can know the document is encrypted at all — and neither are the trailer
            // strings its dictionary carries (/ID, a PDF MAC placeholder).
            long written = writer.WriteIndirectObject(xrefObjectNumber, 0, stream, encrypt: false) - fileStart;
            if (written != xrefOffset)
            {
                warnings.Add("the cross-reference stream was written at offset " +
                    written.ToString(CultureInfo.InvariantCulture) + " but its own entry records " +
                    xrefOffset.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteRaw("startxref\n");
            writer.WriteRaw(xrefOffset.ToString(CultureInfo.InvariantCulture));
            writer.WriteRaw("\n%%EOF\n");
        }

        // The field widths, /W, /Index and the record layout are XrefStreamFields' — §7.5.8's
        // answers, shared with IncrementalUpdateWriter, which writes the same stream for an
        // appended revision; neither carries a copy.
        private static byte[] BuildXrefStreamData(List<XrefRow> sorted, int field2Width)
        {
            var data = new byte[sorted.Count * XrefStreamFields.RecordSize(field2Width)];

            int p = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                XrefRow row = sorted[i];
                XrefStreamFields.WriteRecord(
                    data, ref p, row.Type, row.Field2, row.Field3, field2Width);
            }

            return data;
        }

        // ------------------------------------------------------------------
        //  Trailer
        // ------------------------------------------------------------------

        /// <summary>
        /// Copies the trailer keys a cross-reference stream dictionary carries, and only those.
        /// Copying wholesale would drag the source document's own <c>/Type</c>, <c>/W</c>,
        /// <c>/Index</c>, <c>/Filter</c>, <c>/Length</c> and <c>/Prev</c> into a dictionary that has
        /// just computed its own.
        /// </summary>
        private static void CopyTrailerKeys(PdfDictionary target, PdfDictionary trailer, List<string> warnings)
        {
            PdfObject root = trailer?[PdfName.Root];
            if (root != null)
                target.Set(PdfName.Root, root);
            else
                warnings.Add("the trailer has no /Root; the written file names no catalog and will not open");

            PdfObject info = trailer?[PdfName.Info];
            if (info != null) target.Set(PdfName.Info, info);

            PdfObject encrypt = trailer?[PdfName.Encrypt];
            if (encrypt != null) target.Set(PdfName.Encrypt, encrypt);

            // ISO/TS 32004 §6.5.1: a standalone PDF MAC token's /AuthCode sits in the trailer
            // dictionary, which here is the cross-reference stream's; the caller seals it afterwards.
            PdfObject authCode = trailer?[PdfName.AuthCode];
            if (authCode != null) target.Set(PdfName.AuthCode, authCode);
        }

        /// <summary>The trailer's <c>/ID</c> when it is a well-formed two-string array.</summary>
        /// <remarks>
        /// <para><b>An indirect <c>/ID</c> is resolved against the objects being written</b>, which
        /// this used not to do. <see cref="PdfDictionary"/>'s indexer does not follow a reference,
        /// so <c>/ID 7 0 R</c> read as "not an array" — and this writer answers "no identifier" by
        /// synthesising a fresh one, silently changing the document's identity. §14.4 makes the
        /// first element permanent for the life of the file; readers use the pair to tell one
        /// document's revisions from another's, and an encrypted file's key derivation reads it.
        /// <c>IncrementalUpdateWriter</c> resolved it and this did not, for the same question.</para>
        ///
        /// <para>Resolution goes through <paramref name="objects"/> rather than a
        /// <c>PdfDocument</c>, because this method has no document: it is writing a self-contained
        /// file, so an <c>/ID</c> reference that does not point into that set would dangle in the
        /// output anyway. Resolving against the graph being written is the same rule the merge and
        /// split paths follow for a cloned graph.</para>
        /// </remarks>
        private static PdfArray ExistingId(
            PdfDictionary trailer, IReadOnlyList<(int objectNumber, PdfObject obj)> objects)
        {
            return PdfFileId.Existing(trailer?[PdfName.ID], raw => ResolveWithin(raw, objects));
        }

        /// <summary>The object <paramref name="raw"/> names, when it is among the ones being written.</summary>
        /// <remarks>
        /// From the end: a duplicated object number is written as its <b>last</b> occurrence
        /// (<see cref="Order"/>), and the identifier must be the written object's. Searching from
        /// the start carried an <c>/ID</c> from an occurrence that never reached the file.
        /// </remarks>
        private static PdfObject ResolveWithin(
            PdfObject raw, IReadOnlyList<(int objectNumber, PdfObject obj)> objects)
        {
            if (!(raw is PdfReference reference) || objects == null) return raw;

            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i].objectNumber == reference.ReferenceObjectNumber) return objects[i].obj;

            return null;
        }

        /// <summary>
        /// A deterministic <c>/ID</c> derived from every byte written before the cross-reference
        /// stream. No <see cref="Random"/> and no timestamp: two runs over the same objects must
        /// produce the same file, which is what makes a byte-comparison test possible at all.
        /// </summary>
        private static PdfArray SynthesiseId(ContentDigest digest)
            => PdfFileId.FromDigest(digest?.Finish());

        // ------------------------------------------------------------------
        //  Partitioning
        // ------------------------------------------------------------------

        /// <summary>ISO 32000-1 §7.5.7's exclusion list. See the class remarks for the reasoning.</summary>
        private static bool MustStayTopLevel(
            int objectNumber, PdfObject obj, int encryptObjectNumber, HashSet<int> indirectLengthTargets)
        {
            if (obj is PdfStream) return true;
            if (obj.GenerationNumber != 0) return true;
            if (encryptObjectNumber > 0 && objectNumber == encryptObjectNumber) return true;
            if (indirectLengthTargets != null && indirectLengthTargets.Contains(objectNumber)) return true;
            return false;
        }

        /// <summary>
        /// Object numbers named as the <c>/Length</c> of some stream.
        ///
        /// <para><see cref="PdfWriter.WriteIndirectObject(int, int, PdfObject)"/> overwrites
        /// <c>/Length</c> with a direct
        /// number before serialising, so no indirect length survives into this writer's output and
        /// this exclusion is belt-and-braces. It is kept because §7.5.7 lists it, because the
        /// failure it prevents is a reader that cannot bootstrap an object stream, and because
        /// relying on another class not to change is not a guarantee.</para>
        /// </summary>
        private static HashSet<int> IndirectLengthTargets(SortedList<int, PdfObject> ordered)
        {
            HashSet<int> targets = null;
            foreach (KeyValuePair<int, PdfObject> kvp in ordered)
            {
                if (!(kvp.Value is PdfStream stream)) continue;
                if (!(stream[PdfName.Length] is PdfReference length)) continue;

                targets ??= new HashSet<int>();
                targets.Add(length.ReferenceObjectNumber);
            }
            return targets;
        }

        private static int EncryptObjectNumber(PdfDictionary trailer)
            => trailer?[PdfName.Encrypt] is PdfReference reference ? reference.ReferenceObjectNumber : 0;

        /// <summary>
        /// Object-number order, with unusable entries dropped and duplicates collapsed — two rows
        /// for one object number would name the same object twice, and only one of them can be right.
        /// </summary>
        private static SortedList<int, PdfObject> Order(
            IReadOnlyList<(int objectNumber, PdfObject obj)> objects, List<string> warnings)
        {
            var ordered = new SortedList<int, PdfObject>();

            for (int i = 0; i < objects.Count; i++)
            {
                (int objectNumber, PdfObject obj) = objects[i];

                if (obj == null || objectNumber <= 0)
                {
                    warnings.Add("entry " + i.ToString(CultureInfo.InvariantCulture) + " (object number " +
                        objectNumber.ToString(CultureInfo.InvariantCulture) +
                        ") is not a valid indirect object; it was skipped");
                    continue;
                }

                if (ordered.ContainsKey(objectNumber))
                {
                    warnings.Add("object " + objectNumber.ToString(CultureInfo.InvariantCulture) +
                        " appears more than once; the last occurrence was used");
                }

                ordered[objectNumber] = obj;
            }

            return ordered;
        }

        // ------------------------------------------------------------------
        //  Structural guard
        // ------------------------------------------------------------------

        /// <summary>
        /// Refuses an object whose <b>direct</b> structure is cyclic or absurdly wide, before any
        /// byte reaches the caller's stream.
        ///
        /// <para>A <see cref="PdfReference"/> is a leaf here, exactly as it is to
        /// <see cref="PdfWriter"/>, so the ordinary reference cycles a real document is full of
        /// (a page's <c>/Parent</c> pointing back at the node that lists it) cost nothing and are
        /// never reported.</para>
        /// </summary>
        private static void EnsureDirectStructureIsFinite(PdfObject obj, int objectNumber)
        {
            int budget = MaxDirectNodesPerObject;
            Walk(obj, 0, objectNumber, ref budget);
        }

        private static void Walk(PdfObject obj, int depth, int objectNumber, ref int budget)
        {
            if (obj == null) return;

            if (depth > PdfLimits.MaxObjectGraphDepth)
                throw new InvalidOperationException(
                    "Object " + objectNumber.ToString(CultureInfo.InvariantCulture) +
                    " nests directly to more than " +
                    PdfLimits.MaxObjectGraphDepth.ToString(CultureInfo.InvariantCulture) +
                    " levels, which is what a cyclic object graph does; it cannot be serialised.");

            // A file's own content reaches this (the parser caps no array's length), so it is the file's refusal.
            if (--budget < 0)
                throw new PdfException(
                    "Object " + objectNumber.ToString(CultureInfo.InvariantCulture) +
                    " reaches more than " + MaxDirectNodesPerObject.ToString(CultureInfo.InvariantCulture) +
                    " direct nodes; it cannot be serialised.");

            // A reference is written as "N G R" and never followed, so it terminates the walk for
            // the same reason it terminates serialisation.
            if (obj is PdfReference) return;

            if (obj is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                    Walk(array[i], depth + 1, objectNumber, ref budget);
                return;
            }

            if (obj is PdfDictionary dict)
            {
                foreach (KeyValuePair<PdfName, PdfObject> kvp in dict)
                    Walk(kvp.Value, depth + 1, objectNumber, ref budget);
            }
        }

        // ------------------------------------------------------------------
        //  Small helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Flate-compresses <paramref name="decoded"/> onto <paramref name="stream"/>, or stores it
        /// raw when compression did not help. An uncompressed object or cross-reference stream is
        /// equally valid, and a handful of entries can inflate.
        /// </summary>
        private static void ApplyFlate(PdfStream stream, byte[] decoded, CompressionLevel level)
        {
            byte[] encoded = FlateEncodeFilter.Encode(decoded, level);
            if (encoded != null && encoded.Length > 0 && encoded.Length < decoded.Length)
            {
                stream.Set(PdfName.Filter, PdfName.FlateDecode);
                stream.RawData = encoded;
            }
            else
            {
                stream.Remove(PdfName.Filter);
                stream.RawData = decoded;
            }
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;

        /// <summary>One row of the cross-reference stream (§7.5.8.3, Table 18).</summary>
        private readonly struct XrefRow
        {
            private XrefRow(int objectNumber, byte type, long field2, int field3)
            {
                ObjectNumber = objectNumber;
                Type = type;
                Field2 = field2;
                Field3 = field3;
            }

            /// <summary>Type 0 — a free object; field 2 is the next free number, field 3 its generation.</summary>
            internal static XrefRow Free(int objectNumber, long nextFree, int generation)
                => new XrefRow(objectNumber, 0, nextFree, generation);

            /// <summary>Type 1 — an uncompressed object at a byte offset.</summary>
            internal static XrefRow InFile(int objectNumber, long offset, int generation)
                => new XrefRow(objectNumber, 1, offset, generation);

            /// <summary>Type 2 — an object inside an object stream; its generation is implicitly 0.</summary>
            internal static XrefRow InObjectStream(int objectNumber, int streamObjectNumber, int indexInStream)
                => new XrefRow(objectNumber, 2, streamObjectNumber, indexInStream);

            internal int ObjectNumber { get; }
            internal byte Type { get; }
            internal long Field2 { get; }
            internal int Field3 { get; }
        }

        /// <summary>
        /// Accumulates a digest of everything written, for the synthesised <c>/ID</c>. SHA-256 when
        /// the platform provides it; a deterministic four-lane FNV-1a otherwise, because <c>/ID</c>
        /// is an identifier rather than a security control and determinism is the property that
        /// matters. (Mirrors <see cref="IncrementalUpdateWriter"/>'s choice, for the same reason.)
        /// </summary>
        private sealed class ContentDigest
        {
            private const int DigestLengthBytes = 2 * IdHalfLengthBytes;
            private const ulong FnvOffsetBasis = 14695981039346656037UL;
            private const ulong FnvPrime = 1099511628211UL;

            private readonly SHA256 _sha;
            private readonly ulong[] _lanes;
            private readonly byte[] _oneByte = new byte[1];
            private long _position;

            /// <summary>
            /// The result, computed once. Everything appended after <see cref="Finish"/> is
            /// ignored, which is not laxity but the definition: the digest covers the bytes written
            /// <i>before</i> the cross-reference stream, and that stream travels through the same
            /// wrapper on its way out. Without this the writer threw
            /// <see cref="ObjectDisposedException"/> on its own last object.
            /// </summary>
            private byte[] _result;

            internal ContentDigest()
            {
                try { _sha = SHA256.Create(); }
                catch (Exception) { _sha = null; }

                if (_sha != null) return;

                _lanes = new ulong[4];
                for (int lane = 0; lane < _lanes.Length; lane++)
                    _lanes[lane] = FnvOffsetBasis + (ulong)lane;
            }

            internal void Append(byte[] buffer, int offset, int count)
            {
                if (count <= 0 || _result != null) return;

                if (_sha != null)
                {
                    _sha.TransformBlock(buffer, offset, count, null, 0);
                    return;
                }

                for (int i = 0; i < count; i++)
                    AppendByte(buffer[offset + i]);
            }

            internal void AppendByte(byte value)
            {
                if (_result != null) return;

                if (_sha != null)
                {
                    _oneByte[0] = value;
                    _sha.TransformBlock(_oneByte, 0, 1, null, 0);
                    return;
                }

                int lane = (int)(_position & 3);
                _lanes[lane] ^= value;
                _lanes[lane] *= FnvPrime;
                _position++;
            }

            internal byte[] Finish()
            {
                if (_result != null) return _result;

                if (_sha != null)
                {
                    _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    byte[] hash = _sha.Hash;
                    _sha.Dispose();

                    _result = new byte[DigestLengthBytes];
                    Array.Copy(hash, 0, _result, 0, Math.Min(hash.Length, DigestLengthBytes));
                    return _result;
                }

                var fnv = new byte[DigestLengthBytes];
                for (int lane = 0; lane < _lanes.Length; lane++)
                    for (int b = 0; b < 8; b++)
                        fnv[lane * 8 + b] = (byte)(_lanes[lane] >> (8 * (7 - b)));

                _result = fnv;
                return _result;
            }
        }

        /// <summary>
        /// Write-only pass-through that feeds a <see cref="ContentDigest"/> while forwarding
        /// <see cref="Position"/> to the real output, so the offsets <see cref="PdfWriter"/> records
        /// are the file's own and not this wrapper's idea of them.
        /// </summary>
        private sealed class DigestStream : Stream
        {
            private readonly Stream _inner;
            private readonly ContentDigest _digest;

            internal DigestStream(Stream inner, ContentDigest digest)
            {
                _inner = inner;
                _digest = digest;
            }

            public override bool CanRead => false;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _digest.Append(buffer, offset, count);
            }

            public override void WriteByte(byte value)
            {
                _inner.WriteByte(value);
                _digest.AppendByte(value);
            }
        }
    }
}
