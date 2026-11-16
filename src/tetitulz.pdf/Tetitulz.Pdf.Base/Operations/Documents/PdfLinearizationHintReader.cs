// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Decodes ISO 32000-1 Annex F's primary hint stream — the tables that say which bytes a given
    /// page needs, without a cross-reference table.
    /// </summary>
    /// <remarks>
    /// <para><b>Written from Tables F.3–F.6 and F.9, not from this library's encoder.</b> A decoder
    /// derived from the encoder agrees with it about a shared mistake, which is the blind spot every
    /// round trip has (CLAUDE.md, "a relative assertion cannot see a change applied to both of the
    /// things it relates"). The tests go further and relate what this decodes to facts measured
    /// independently in the file — where each object physically starts, how long each page's run of
    /// objects actually is — so a table that is internally consistent and factually wrong still
    /// fails.</para>
    ///
    /// <para><b>Item groups are byte-aligned, and the specification does not say so.</b> Annex F
    /// gives bit widths and an interleaving order and is silent about padding; existing implementations both
    /// writes and requires a pad after each group of like items, and its tables only add up to the
    /// lengths it declares with the pads in place. That is the reading implemented here, and it is
    /// the reason this is checked against files other tools produce rather than only our own.</para>
    ///
    /// <para><b>Every width and count in these tables is chosen by the file.</b> Table F.3 and
    /// Table F.5 read their field widths out of the stream, and the per-page shared-object count is
    /// read there too — so the nested loop is <c>pages × count</c>, two attacker-chosen numbers
    /// multiplied, out of a stream that deflates from very little. The ceilings are
    /// <see cref="PdfLimits.MaxLinearizationHintFieldBits"/> and
    /// <see cref="PdfLimits.MaxLinearizationSharedReferences"/>, and a table that exceeds either is
    /// <b>refused rather than clamped</b>: hints that are silently wrong are worse than no hints.
    /// A width may be 0, so no count of items is bounded by the stream's length: each is bounded by
    /// <see cref="PdfLimits.MaxXrefEntries"/>, every item being an object of the document.</para>
    ///
    /// <para><b>A failure here must never break reading the document.</b> The hint stream is an
    /// optimisation; the cross-reference table is the truth. Every refusal answers <c>false</c> and
    /// reports through <see cref="LastError"/>.</para>
    /// </remarks>
    public static class PdfLinearizationHintReader
    {
        /// <summary>
        /// Why the last read on this thread answered <c>false</c>. <c>null</c> after a clean read.
        /// </summary>
        /// <remarks>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent read on another thread cannot
        /// overwrite it — read it on the thread that ran the call.
        /// </remarks>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Reads the hint tables of a linearized document held in full.
        /// </summary>
        /// <param name="input">The document. Read from its current position; not disposed.</param>
        /// <param name="hints">The decoded tables, or <c>null</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static bool TryRead(Stream input, out PdfLinearizationHints hints)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            LastError = null;
            hints = null;

            long start = input.CanSeek ? input.Position : 0;
            if (!PdfRevisions.TryReadLinearization(input, out PdfLinearizationInfo info))
            {
                LastError = "the document does not begin with a linearization parameter dictionary";
                return false;
            }

            byte[] segment = Segment(input, start, info);
            if (segment == null) return false;

            return TryDecode(segment, info, out hints);
        }

        /// <summary>
        /// Reads the hint tables from a source that may hold only part of the file — fetching
        /// exactly the range <c>/H</c> names and nothing else.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the pair that makes Annex F worth having on the read side.</b> The
        /// parameter dictionary is in the first 1024 bytes and names the hint stream's range; the
        /// hint stream names every page's range. Two fetches, and a client can ask for page 12 of a
        /// document it has never opened.</para>
        ///
        /// <para><see cref="PdfLinearizationState.NotYetAvailable"/> here means the head or the
        /// hint stream's own range has not arrived — not that the tables are bad, which is
        /// <see cref="PdfLinearizationState.Linearized"/> with a null <paramref name="hints"/> and a
        /// reported <see cref="LastError"/>. A client that cannot tell those apart either waits for
        /// bytes it already has or gives up on a document it could still read.</para>
        /// </remarks>
        /// <param name="source">The file, or as much of it as has arrived.</param>
        /// <param name="hints">The decoded tables, or <c>null</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public static PdfLinearizationState TryRead(
            IByteRangeSource source, out PdfLinearizationHints hints)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            LastError = null;
            hints = null;

            PdfLinearizationState state = PdfRevisions.ReadLinearization(source, out PdfLinearizationInfo info);
            if (state != PdfLinearizationState.Linearized) return state;

            if (info == null)
            {
                LastError = "the linearization parameter dictionary could not be read";
                return PdfLinearizationState.Linearized;
            }

            if (!IsUsableRange(info, source.Length)) return PdfLinearizationState.Linearized;

            var segment = new byte[info.HintStreamLength];
            if (!source.TryRead(info.HintStreamOffset, segment, 0, segment.Length, out int read))
                return PdfLinearizationState.NotYetAvailable;

            if (read < segment.Length)
            {
                // The bytes are not coming: the source says the file ends inside the range /H
                // claims, so the claim is wrong rather than early.
                LastError = "the hint stream range /H names runs past the end of the file";
                return PdfLinearizationState.Linearized;
            }

            TryDecode(segment, info, out hints);
            return PdfLinearizationState.Linearized;
        }

        // ----------------------------------------------------------------

        /// <summary>The <c>/H</c> range's bytes, read from a stream holding the whole file.</summary>
        private static byte[] Segment(Stream input, long start, PdfLinearizationInfo info)
        {
            if (!input.CanSeek)
            {
                LastError = "a seekable stream is needed to read the hint stream range";
                return null;
            }

            if (!IsUsableRange(info, input.Length - start)) return null;

            input.Position = start + info.HintStreamOffset;
            var segment = new byte[info.HintStreamLength];
            if (StreamBytes.Fill(input, segment, 0, segment.Length) < segment.Length)
            {
                LastError = "the hint stream range /H names runs past the end of the file";
                return null;
            }

            return segment;
        }

        /// <summary>
        /// Whether <c>/H</c> names a range worth allocating for at all.
        /// </summary>
        /// <remarks>
        /// <para><b>Checked before a byte is allocated, because the number is the producer's and
        /// nothing has validated it.</b> <c>/H [0 2147483647]</c> otherwise costs two gigabytes to
        /// discover it was nonsense — and the head that carries it is 1 KB, which is the whole
        /// asymmetry.</para>
        ///
        /// <para><b>The bound is the file's own length where it is known</b>, not an invented
        /// constant: a hint stream cannot be longer than the file that contains it, and a caller
        /// reading progressively knows the length or knows it does not. The absolute ceiling behind
        /// it is the one every other decoded stream in this library reads under.</para>
        /// </remarks>
        private static bool IsUsableRange(PdfLinearizationInfo info, long? fileLength)
        {
            if (info.HintStreamOffset <= 0 || info.HintStreamLength <= 0)
            {
                LastError = "the linearization dictionary names no hint stream (/H)";
                return false;
            }

            if (info.HintStreamLength > PdfLimits.MaxDecompressedBytes)
            {
                LastError = "the hint stream length /H declares ("
                            + info.HintStreamLength.ToString(CultureInfo.InvariantCulture)
                            + " bytes) is past the decoded-stream ceiling";
                return false;
            }

            // Worded differently from the short-read refusal below on purpose: both mean "/H is
            // wrong", but only this one happens BEFORE the buffer is allocated, and a test that
            // cannot tell them apart cannot tell whether 400 MB was allocated to find out. A
            // mutation removing this check survived against a message the other path also produces.
            if (fileLength.HasValue && info.HintStreamOffset + info.HintStreamLength > fileLength.Value)
            {
                LastError = "the hint stream range /H declares ("
                            + info.HintStreamOffset.ToString(CultureInfo.InvariantCulture) + "+"
                            + info.HintStreamLength.ToString(CultureInfo.InvariantCulture)
                            + ") does not fit in the file's "
                            + fileLength.Value.ToString(CultureInfo.InvariantCulture) + " bytes";
                return false;
            }

            return true;
        }

        /// <summary>Parses the stream object in the <c>/H</c> range and decodes its tables.</summary>
        private static bool TryDecode(byte[] segment, PdfLinearizationInfo info, out PdfLinearizationHints hints)
        {
            hints = null;

            PdfStream stream;
            try
            {
                using var slice = new MemoryStream(segment, writable: false);
                stream = new Core.Parsing.PdfParser(slice).ParseIndirectObject() as PdfStream;
            }
            catch (Core.PdfException ex)
            {
                LastError = "the hint stream range /H names does not hold a stream object: " + ex.Message;
                return false;
            }

            if (stream == null)
            {
                LastError = "the hint stream range /H names does not hold a stream object";
                return false;
            }

            // The bounded decode, with its own reason: a hint stream is a compressed stream like any
            // other and a filter bomb hidden in one would otherwise be decoded in full before
            // anything here looked at it.
            if (!stream.TryGetDecodedData(PdfLimits.MaxDecompressedBytes, out byte[] data, out string reason))
            {
                LastError = "the hint stream could not be decoded: " + reason;
                return false;
            }

            if (data == null || data.Length == 0)
            {
                LastError = "the hint stream is empty";
                return false;
            }

            // Table F.2: /S is where the shared object table begins, and it is required. The page
            // offset table occupies everything before it. PdfStream IS a PdfDictionary, so this
            // reads the stream's own dictionary.
            int shared = stream.GetInteger(PdfName.S) ?? -1;
            if (shared <= 0 || shared >= data.Length)
            {
                LastError = "the hint stream's /S does not point inside its own data";
                return false;
            }

            // Table F.2's optional tables, by where each begins: after the shared one, in any order.
            // Every table Table F.2 defines is read: the generic ones, the extended ones (V, C, R), the
            // embedded files' and the thumbnails'.
            var later = new SortedDictionary<int, string>();
            foreach (var (name, key) in LaterTables)
            {
                int at = stream.GetInteger(name) ?? -1;
                if (at < 0 || at >= data.Length) continue;
                if (at <= shared)
                {
                    LastError = "the hint stream's /" + key + " points at or before its shared object table";
                    return false;
                }
                if (later.ContainsKey(at))
                {
                    LastError = "the hint stream's /" + key + " and /" + later[at] + " point at the same table";
                    return false;
                }
                later[at] = key;
            }

            try
            {
                hints = Decode(data, shared, later, info);
                return true;
            }
            catch (HintFormatException ex)
            {
                LastError = ex.Message;
                hints = null;
                return false;
            }
        }

        /// <summary>Table F.2's tables after the shared object table, by key.</summary>
        private static readonly (PdfName Name, string Key)[] LaterTables =
        {
            (PdfName.T, "T"), (PdfName.O, "O"), (PdfName.A, "A"), (PdfName.E, "E"), (PdfName.V, "V"),
            (PdfName.I, "I"), (PdfName.C, "C"), (PdfName.L, "L"), (PdfName.R, "R"), (PdfName.B, "B"),
        };

        /// <summary>The keys whose tables are Table F.9's generic form, read here.</summary>
        private static readonly HashSet<string> GenericKeys = new HashSet<string> { "O", "A", "E", "I", "L" };

        /// <summary>The keys whose tables are Table F.10's extended generic form, read here.</summary>
        private static readonly HashSet<string> ExtendedKeys = new HashSet<string> { "V", "C", "R" };

        private static PdfLinearizationHints Decode(
            byte[] data, int sharedTableOffset, SortedDictionary<int, string> later, PdfLinearizationInfo info)
        {
            var bits = new HintBits(data, 0);

            // ---- page offset hint table, Table F.3 ----------------------------------
            int minObjects = (int)bits.Read(32);
            long firstPageOffset = bits.Read(32);
            int bitsObjects = Width(bits, "objects per page");
            long minPageLength = bits.Read(32);
            int bitsLength = Width(bits, "page length");
            long minContentOffset = bits.Read(32);
            int bitsContentOffset = Width(bits, "content offset");
            long minContentLength = bits.Read(32);
            int bitsContentLength = Width(bits, "content length");
            int bitsShared = Width(bits, "shared object count");
            int bitsIdentifier = Width(bits, "shared object identifier");
            int bitsNumerator = Width(bits, "shared object numerator");
            bits.Read(16);                               // denominator; not used for fetching

            // (long) because data.Length * 8 overflows int for a stream past 256 MB, and an
            // overflowed bound is a negative number that lets everything through.
            int pageCount = info.PageCount;
            if (pageCount <= 0 || pageCount > (long)data.Length * 8)
                throw new HintFormatException(
                    "the page count /N declares (" + pageCount.ToString(CultureInfo.InvariantCulture)
                    + ") cannot be described by a hint stream of "
                    + data.Length.ToString(CultureInfo.InvariantCulture) + " bytes");
            RequireObjectCount(pageCount, "pages (/N)");

            var objectCounts = new int[pageCount];
            var lengths = new long[pageCount];
            var contentOffsets = new long[pageCount];
            var contentLengths = new long[pageCount];
            var identifiers = new List<int>[pageCount];

            for (int i = 0; i < pageCount; i++) objectCounts[i] = minObjects + (int)bits.Read(bitsObjects);
            bits.Align();

            for (int i = 0; i < pageCount; i++) lengths[i] = minPageLength + bits.Read(bitsLength);
            bits.Align();

            var sharedCounts = new int[pageCount];
            long totalShared = 0;
            for (int i = 0; i < pageCount; i++)
            {
                // READ AS long AND BOUND BEFORE NARROWING. The width is the file's, and at 32 bits
                // a count of 0xFFFFFFFF narrows to -1: the running total then goes DOWN and the
                // ceiling can never fire, which is the opposite of a bound. The cast below is safe
                // only because the throw above it has already rejected anything large.
                long count = bits.Read(bitsShared);
                totalShared += count;

                // Checked inside the loop, not after it: the product is what has to be bounded, and
                // discovering that afterwards means having already allocated for it.
                if (totalShared > PdfLimits.MaxLinearizationSharedReferences)
                    throw new HintFormatException(
                        "the page offset table declares more than "
                        + PdfLimits.MaxLinearizationSharedReferences.ToString(CultureInfo.InvariantCulture)
                        + " shared-object references");

                sharedCounts[i] = (int)count;
            }
            bits.Align();

            for (int i = 0; i < pageCount; i++) identifiers[i] = new List<int>();
            for (int i = 1; i < pageCount; i++)
                for (int j = 0; j < sharedCounts[i]; j++)
                    identifiers[i].Add((int)bits.Read(bitsIdentifier));
            bits.Align();

            for (int i = 1; i < pageCount; i++)
                for (int j = 0; j < sharedCounts[i]; j++)
                    bits.Read(bitsNumerator);            // fractional position; not used for fetching
            bits.Align();

            for (int i = 0; i < pageCount; i++) contentOffsets[i] = minContentOffset + bits.Read(bitsContentOffset);
            bits.Align();

            for (int i = 0; i < pageCount; i++) contentLengths[i] = minContentLength + bits.Read(bitsContentLength);
            bits.Align();

            // The page offset table runs up to /S, so a correct decode lands exactly there.
            //
            // THIS IS THE ONLY STRUCTURAL CHECK THE FORMAT ALLOWS. The tables carry no checksum, no
            // length per field and nothing self-describing, so a decoder that reads the item groups
            // in the wrong order, or gets the per-group byte padding wrong, still produces numbers.
            // Landing somewhere other than /S is the one observable consequence, and refusing on it
            // turns a silent misread into a loud one.
            if (bits.BytePosition != sharedTableOffset)
                throw new HintFormatException(
                    "the page offset table ended at byte "
                    + bits.BytePosition.ToString(CultureInfo.InvariantCulture)
                    + " but /S puts the shared object table at "
                    + sharedTableOffset.ToString(CultureInfo.InvariantCulture));

            // ---- shared object hint table, Table F.5 --------------------------------
            var sharedBits = new HintBits(data, sharedTableOffset);

            int firstSharedObjectNumber = (int)sharedBits.Read(32);
            long firstSharedOffset = sharedBits.Read(32);
            int sharedInFirstPage = (int)sharedBits.Read(32);
            int sharedTotal = (int)sharedBits.Read(32);
            int sharedBitsObjects = Width(sharedBits, "objects per shared group");
            long sharedMinLength = sharedBits.Read(32);
            int sharedBitsLength = Width(sharedBits, "shared group length");

            if (sharedTotal < 0 || sharedTotal > (long)data.Length * 8)
                throw new HintFormatException(
                    "the shared object table declares " + sharedTotal.ToString(CultureInfo.InvariantCulture)
                    + " groups, which a hint stream of " + data.Length.ToString(CultureInfo.InvariantCulture)
                    + " bytes cannot describe");
            RequireObjectCount(sharedTotal, "shared object groups");

            var groupLengths = new long[sharedTotal];
            var groupSignatures = new bool[sharedTotal];
            var groupCounts = new int[sharedTotal];

            for (int i = 0; i < sharedTotal; i++) groupLengths[i] = sharedMinLength + sharedBits.Read(sharedBitsLength);
            sharedBits.Align();

            for (int i = 0; i < sharedTotal; i++) groupSignatures[i] = sharedBits.Read(1) != 0;
            sharedBits.Align();

            for (int i = 0; i < sharedTotal; i++) groupCounts[i] = 1 + (int)sharedBits.Read(sharedBitsObjects);
            sharedBits.Align();

            // The shared table is the last one unless a later table follows it, so the same
            // end-position reasoning applies: it must land on whichever comes next.
            int sharedMustEndAt = later.Count > 0 ? later.Keys.First() : data.Length;
            if (sharedBits.BytePosition != sharedMustEndAt)
                throw new HintFormatException(
                    "the shared object table ended at byte "
                    + sharedBits.BytePosition.ToString(CultureInfo.InvariantCulture)
                    + " rather than " + sharedMustEndAt.ToString(CultureInfo.InvariantCulture));

            // ---- generic hint tables, Table F.9 -------------------------------------
            var generic = new Dictionary<string, PdfGenericHint>(StringComparer.Ordinal);
            var starts = later.Keys.ToList();
            for (int t = 0; t < starts.Count; t++)
            {
                string key = later[starts[t]];
                bool extended = ExtendedKeys.Contains(key);
                if (!GenericKeys.Contains(key) && !extended) continue;
                var genericBits = new HintBits(data, starts[t]);
                int first = (int)genericBits.Read(32);
                long location = ToFileOffset(genericBits.Read(32), info);
                int count = (int)genericBits.Read(32);
                long groupLength = genericBits.Read(32);
                int[] sharedIds = null;
                if (extended)
                {
                    // Table F.10: a count, the bits each identifier takes, the identifiers.
                    long references = genericBits.Read(32);
                    int width = (int)genericBits.Read(16);
                    if (width > 32 || references < 0 || references * Math.Max(1, width) > (long)data.Length * 8)
                        throw new HintFormatException(
                            "the /" + key + " extended hint table declares " + references.ToString(CultureInfo.InvariantCulture)
                            + " shared references of " + width.ToString(CultureInfo.InvariantCulture) + " bits, more than the stream holds");
                    RequireObjectCount(references, "shared references in the /" + key + " table");
                    sharedIds = new int[references];
                    for (long r = 0; r < references; r++) sharedIds[r] = (int)genericBits.Read(width);
                    genericBits.Align();
                }
                generic[key] = new PdfGenericHint(first, location, count, groupLength, sharedIds);

                // Table F.9 is four 32-bit fields, so the table ends where the next one begins, or
                // where the data does — the only way to notice a table read as something other than
                // what was written.
                int mustEndAt = t + 1 < starts.Count ? starts[t + 1] : data.Length;
                if (genericBits.BytePosition != mustEndAt)
                    throw new HintFormatException(
                        "the /" + key + " generic hint table ended at byte "
                        + genericBits.BytePosition.ToString(CultureInfo.InvariantCulture)
                        + " rather than at " + mustEndAt.ToString(CultureInfo.InvariantCulture));
            }
            PdfGenericHint Generic(string key) => generic.TryGetValue(key, out PdfGenericHint g) ? g : null;

            // ---- thumbnail hint table, Tables F.7 and F.8 -----------------------------
            List<PdfThumbnailHint> thumbnails = null;
            PdfGenericHint thumbnailShared = null;
            for (int t = 0; t < starts.Count; t++)
            {
                if (later[starts[t]] != "T") continue;
                var th = new HintBits(data, starts[t]);
                int firstThumb = (int)th.Read(32);
                long thumbLocation = th.Read(32);
                long thumbPages = th.Read(32);
                int thBitsGap = Width(th, "pages without a thumbnail");
                long thLeastLength = th.Read(32);
                int thBitsLength = Width(th, "thumbnail length");
                long thLeastObjects = th.Read(32);
                int thBitsObjects = Width(th, "thumbnail object count");
                int sharedFirst = (int)th.Read(32);
                long sharedLocation = th.Read(32);
                long sharedCount = th.Read(32);
                long sharedLength = th.Read(32);
                if (thumbPages < 0 || thumbPages * Math.Max(1, thBitsGap + thBitsLength + thBitsObjects) > (long)data.Length * 8)
                    throw new HintFormatException("the /T hint table declares " + thumbPages.ToString(CultureInfo.InvariantCulture)
                        + " thumbnails, more than the stream holds");
                // A page has one thumbnail at most (Table F.7 item 3 counts the pages that have one), so
                // the table cannot declare more than /N: with every width 0, the stream's bits alone let a
                // megabyte of hint stream ask for eight million.
                if (thumbPages > pageCount)
                    throw new HintFormatException("the /T hint table declares " + thumbPages.ToString(CultureInfo.InvariantCulture)
                        + " thumbnails for " + pageCount.ToString(CultureInfo.InvariantCulture) + " pages");
                var gaps = new long[thumbPages];
                var thCounts = new long[thumbPages];
                var thLengths = new long[thumbPages];
                for (long g = 0; g < thumbPages; g++) gaps[g] = th.Read(thBitsGap);
                for (long g = 0; g < thumbPages; g++) thCounts[g] = thLeastObjects + th.Read(thBitsObjects);
                for (long g = 0; g < thumbPages; g++) thLengths[g] = thLeastLength + th.Read(thBitsLength);
                thumbnails = new List<PdfThumbnailHint>((int)thumbPages);
                long thPosition = thumbLocation;
                int thObject = firstThumb;
                int thPage = -1;
                for (long g = 0; g < thumbPages; g++)
                {
                    thPage = (int)(thPage + 1 + gaps[g]);
                    thumbnails.Add(new PdfThumbnailHint(thPage, thObject, ToFileOffset(thPosition, info), (int)thCounts[g], thLengths[g]));
                    thPosition += thLengths[g];
                    thObject += (int)thCounts[g];
                }
                if (sharedCount > 0)
                    thumbnailShared = new PdfGenericHint(sharedFirst, ToFileOffset(sharedLocation, info), (int)sharedCount, sharedLength);
                th.Align();
                int thMustEndAt = t + 1 < starts.Count ? starts[t + 1] : data.Length;
                if (th.BytePosition != thMustEndAt)
                    throw new HintFormatException(
                        "the /T thumbnail hint table ended at byte "
                        + th.BytePosition.ToString(CultureInfo.InvariantCulture)
                        + " rather than at " + thMustEndAt.ToString(CultureInfo.InvariantCulture));
            }

            // ---- embedded file stream hint table, Tables F.11 and F.12 ---------------
            List<PdfGenericHint> embeddedFiles = null;
            for (int t = 0; t < starts.Count; t++)
            {
                if (later[starts[t]] != "B") continue;
                var b = new HintBits(data, starts[t]);
                b.Read(32);                                   // the first group's first object: its stream, item 1 below
                long location = b.Read(32);
                long groupCount = b.Read(32);
                int efBitsStream = Width(b, "embedded file stream object number");
                int efBitsObjects = Width(b, "embedded file group object count");
                int efBitsLength = Width(b, "embedded file group length");
                int efBitsShared = Width(b, "embedded file group shared reference count");
                if (groupCount < 0 || groupCount * Math.Max(1, efBitsStream + efBitsObjects + efBitsLength + efBitsShared) > (long)data.Length * 8)
                    throw new HintFormatException("the /B hint table declares " + groupCount.ToString(CultureInfo.InvariantCulture)
                        + " embedded file groups, more than the stream holds");
                RequireObjectCount(groupCount, "embedded file groups (/B)");
                var efStreams = new int[groupCount];
                var efCounts = new int[groupCount];
                var efLengths = new long[groupCount];
                var efShareCounts = new long[groupCount];
                for (long g = 0; g < groupCount; g++) efStreams[g] = (int)b.Read(efBitsStream);
                for (long g = 0; g < groupCount; g++) efCounts[g] = (int)b.Read(efBitsObjects);
                for (long g = 0; g < groupCount; g++) efLengths[g] = b.Read(efBitsLength);
                long allShared = 0;
                for (long g = 0; g < groupCount; g++) { efShareCounts[g] = b.Read(efBitsShared); allShared += efShareCounts[g]; }
                if (allShared * Math.Max(1, bitsIdentifier) > (long)data.Length * 8)
                    throw new HintFormatException("the /B hint table declares " + allShared.ToString(CultureInfo.InvariantCulture)
                        + " shared references, more than the stream holds");
                embeddedFiles = new List<PdfGenericHint>((int)groupCount);
                long position = location;
                for (long g = 0; g < groupCount; g++)
                {
                    var ids = new int[efShareCounts[g]];
                    for (long r = 0; r < efShareCounts[g]; r++) ids[r] = (int)b.Read(bitsIdentifier);
                    embeddedFiles.Add(new PdfGenericHint(efStreams[g], ToFileOffset(position, info), efCounts[g], efLengths[g], ids));
                    position += efLengths[g];
                }
                b.Align();
                int mustEndAt = t + 1 < starts.Count ? starts[t + 1] : data.Length;
                if (b.BytePosition != mustEndAt)
                    throw new HintFormatException(
                        "the /B embedded file stream hint table ended at byte "
                        + b.BytePosition.ToString(CultureInfo.InvariantCulture)
                        + " rather than at " + mustEndAt.ToString(CultureInfo.InvariantCulture));
            }

            // ---- assemble, with F.4 NOTE 3 applied once ----------------------------
            var pages = new PdfPageHint[pageCount];
            long at = ToFileOffset(firstPageOffset, info);
            for (int i = 0; i < pageCount; i++)
            {
                // THE CONTENT OFFSET IS RELATIVE TO THE PAGE, NOT TO THE FILE, so it gets the page's
                // own offset added and NOT Annex F.4 NOTE 3's hint-stream correction — that rule is
                // about positions measured from the start of the file, which this is not. Applying
                // it here produced an "offset" of 0 for every page of this library's own output,
                // and a reader that trusted it returned the first bytes of the FILE as the page's
                // content: plausible-looking bytes, wrong page, no error.
                pages[i] = new PdfPageHint(
                    objectCounts[i], at, lengths[i],
                    at + contentOffsets[i], contentLengths[i],
                    identifiers[i]);
                at += lengths[i];
            }

            var groups = new PdfSharedObjectHint[sharedTotal];
            for (int i = 0; i < sharedTotal; i++)
                groups[i] = new PdfSharedObjectHint(groupLengths[i], groupCounts[i], groupSignatures[i]);

            return new PdfLinearizationHints(
                pages, groups, firstSharedObjectNumber, ToFileOffset(firstSharedOffset, info),
                sharedInFirstPage, objectCounts.Length > 0 ? objectCounts[0] : 0, Generic("O"),
                Generic("A"), Generic("E"), Generic("I"), Generic("L"), Generic("C"), Generic("R"), embeddedFiles,
                thumbnails, thumbnailShared, Generic("V"));
        }

        /// <summary>
        /// Annex F.4 NOTE 3: hint positions are written "as if the primary hint stream itself were
        /// not present", so a position at or past the stream is short by its length.
        /// </summary>
        /// <remarks>
        /// <b>At, not merely past.</b> The object following the hint stream has exactly the stream's
        /// offset as its hint position — reading that literally puts it inside the stream — and it
        /// is what existing implementations write and read.
        /// </remarks>
        private static long ToFileOffset(long hintPosition, PdfLinearizationInfo info)
            => hintPosition >= info.HintStreamOffset ? hintPosition + info.HintStreamLength : hintPosition;

        /// <summary>A file-declared field width, refused rather than clamped when it is impossible.</summary>
        private static int Width(HintBits bits, string what)
        {
            int width = (int)bits.Read(16);
            if (width < 0 || width > PdfLimits.MaxLinearizationHintFieldBits)
                throw new HintFormatException(
                    "the hint table declares a " + width.ToString(CultureInfo.InvariantCulture)
                    + "-bit field for " + what + ", past the "
                    + PdfLimits.MaxLinearizationHintFieldBits.ToString(CultureInfo.InvariantCulture)
                    + "-bit ceiling");

            return width;
        }

        /// <summary>
        /// A file-declared count of items that are each at least one indirect object, refused past
        /// the most a document may hold.
        /// </summary>
        /// <remarks>
        /// <b>The stream's bits do not bound such a count.</b> Every width may be 0 — Annex F allows
        /// it when the items agree — so the one-bit floor of each bits check is a floor the file need
        /// not meet: a hint stream inflated to the decoded ceiling from a few hundred kilobytes
        /// declared two thousand million pages, each held here at about 130 bytes, before the end
        /// position could refuse the table. A page, a shared group, a thumbnail, an embedded file
        /// stream and a shared object an extended table names are each an indirect object, so none
        /// outnumbers <see cref="PdfLimits.MaxXrefEntries"/>, the entries the parser itself reads.
        /// </remarks>
        private static void RequireObjectCount(long count, string what)
        {
            if (count > PdfLimits.MaxXrefEntries)
                throw new HintFormatException(
                    "the hint stream declares " + count.ToString(CultureInfo.InvariantCulture) + " " + what
                    + ", more objects than the " + PdfLimits.MaxXrefEntries.ToString(CultureInfo.InvariantCulture)
                    + " a document may hold");
        }

        /// <summary>A most-significant-bit-first reader that refuses to read past its data.</summary>
        private sealed class HintBits
        {
            private readonly byte[] _data;
            private int _bit;

            internal HintBits(byte[] data, int byteOffset)
            {
                _data = data;
                _bit = byteOffset * 8;
            }

            internal long Read(int count)
            {
                if (_bit + count > _data.Length * 8L)
                    throw new HintFormatException("the hint tables run past the end of the hint stream");

                long value = 0;
                for (int i = 0; i < count; i++)
                {
                    int bit = (_data[_bit >> 3] >> (7 - (_bit & 7))) & 1;
                    value = (value << 1) | (uint)bit;
                    _bit++;
                }
                return value;
            }

            internal void Align() => _bit = (_bit + 7) & ~7;

            /// <summary>Where the cursor stands, for the end-position checks.</summary>
            internal int BytePosition => _bit / 8;
        }

        /// <summary>
        /// A malformed hint table. Private and always caught: a bad hint stream is a reported
        /// refusal, never an exception a caller has to know about.
        /// </summary>
        private sealed class HintFormatException : Exception
        {
            internal HintFormatException(string message) : base(message) { }
        }
    }
}
