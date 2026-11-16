// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Encodes ISO 32000-1 Annex F's two required hint tables — the page offset table (F.4.1) and
    /// the shared object table (F.4.2) — as the byte stream that becomes the primary hint stream.
    /// </summary>
    /// <remarks>
    /// <para><b>Without these, first-page-first ordering buys a reader nothing.</b> The hint tables
    /// are what let a conforming reader work out which byte range holds page <i>n</i> before it has
    /// the cross-reference table, which is the entire promise of linearization. Table F.2 makes the
    /// page offset table required at offset 0 and the shared object table required at <c>/S</c>.</para>
    ///
    /// <para><b>Every offset here is written as if the primary hint stream were not in the file</b>
    /// (F.4, NOTE 3): "a position greater than the hint stream offset shall have the hint stream
    /// length added to it to determine the actual offset". That rule is what breaks the circularity —
    /// the tables record the offsets a page would have if the stream they live in did not exist, so
    /// nothing in them depends on their own encoded length. The caller does the subtraction, not this
    /// class.</para>
    ///
    /// <para><b>Each group of like items ends on a byte boundary, and the specification does not say
    /// so.</b> Table F.3 and Table F.4 give bit widths and an interleaving order and stop there. Existing
    /// writers pad after every group and their readers skip to the next byte after every group;
    /// decoding their output field by field only reproduces the values they report with the
    /// pads in place, and the table lengths only add up with them (13 bytes of per-page items for a
    /// five-page file, not the 11 the bit widths alone give). Written without the pads, the stream
    /// decodes to plausible garbage rather than failing loudly — which is why this is asserted in the
    /// tests against a decoder rather than left to the reader's charity.</para>
    ///
    /// <para><b>The content-stream items describe the whole page, deliberately.</b> Table F.4 items 6
    /// and 7 are meant to isolate the content stream inside the page so a reader can start drawing
    /// before the resources arrive; existing writers write offset 0 and length = the page length, and this does
    /// the same. It is an over-approximation, never a wrong answer: a reader that fetches
    /// [page start, page start + content length) always gets the content stream. Narrowing it needs
    /// the position of a <c>/Contents</c> that may be an array of streams interleaved with resources,
    /// and a wrong narrowing is a page that renders incomplete.</para>
    /// </remarks>
    internal sealed class LinearizationHintTables
    {
        /// <summary>What the page offset table records about one page.</summary>
        internal sealed class PageInfo
        {
            internal int ObjectCount;
            internal long Length;
            internal List<int> SharedIdentifiers = new List<int>();
        }

        /// <summary>One entry of the shared object table: a run of adjacent objects.</summary>
        internal sealed class SharedGroup
        {
            internal long Length;
            internal int ObjectCount;
        }

        /// <summary>
        /// A category of document-level objects that sit together in the file — Table F.9's
        /// generic hint table, which describes exactly one such group.
        /// </summary>
        internal sealed class GenericGroup
        {
            internal int FirstObjectNumber;
            internal long Location;
            internal int ObjectCount;
            internal long Length;

            /// <summary>
            /// For an extended generic table (Table F.10), the shared object identifiers the group's
            /// objects reference, ascending; <c>null</c> for a plain generic table (Table F.9).
            /// </summary>
            internal List<int> SharedIdentifiers;
        }

        /// <summary>One embedded file stream's group (Table F.12): the stream, then the objects it references.</summary>
        internal sealed class EmbeddedFileGroup
        {
            internal int StreamObjectNumber;
            internal long Location;
            internal int ObjectCount;
            internal long Length;
            internal List<int> SharedIdentifiers = new List<int>();
        }

        /// <summary>One page's thumbnail group (Table F.8): its image first, then what only it references.</summary>
        internal sealed class ThumbnailGroup
        {
            internal int PageIndex;
            internal int FirstObjectNumber;
            internal long Location;
            internal int ObjectCount;
            internal long Length;
        }

        /// <summary>The encoded stream data.</summary>
        internal byte[] Data { get; private set; }

        /// <summary>The offset of the thumbnail hint table within <see cref="Data"/> — <c>/T</c> — or -1 when there is none.</summary>
        internal int ThumbnailTableOffset { get; private set; } = -1;

        /// <summary>The offset of the embedded file stream hint table within <see cref="Data"/> — <c>/B</c> — or -1 when there is none.</summary>
        internal int EmbeddedFileTableOffset { get; private set; } = -1;

        /// <summary>
        /// The offset of the shared object table within <see cref="Data"/> — the <c>/S</c> entry of
        /// the primary hint stream's dictionary.
        /// </summary>
        internal int SharedTableOffset { get; private set; }

        /// <summary>
        /// The offset of the outline hint table within <see cref="Data"/> — the <c>/O</c> entry —
        /// or -1 when the document has no outline.
        /// </summary>
        /// <remarks>
        /// Table F.2 lists <c>/O</c> as "Present only if a document outline exists", and structure checkers
        /// enforce the other half of that: given a document with an outline and a hint stream
        /// without the table, they report an incorrect object count in the outline hint table. So the
        /// conditional key is required when the condition holds, not merely permitted.
        /// </remarks>
        internal int OutlineTableOffset { get; private set; } = -1;

        /// <summary>
        /// The denominator of the fractional position of a shared object reference (Table F.3,
        /// item 13). Quarters, as is usual; the numerators are all zero because this writer does
        /// not inspect the unencoded content stream to find where a shared object is first used, and
        /// zero means "the first quarter", which is where a resource named in the resource
        /// dictionary is in fact needed.
        /// </summary>
        private const int SharedDenominator = 4;

        internal static LinearizationHintTables Build(
            IReadOnlyList<PageInfo> pages,
            IReadOnlyList<SharedGroup> sharedGroups,
            long firstPageOffset,
            int firstSharedObjectNumber,
            long firstSharedOffset,
            int sharedGroupsInFirstPage,
            IReadOnlyList<(string Key, GenericGroup Group)> generic,
            IReadOnlyList<EmbeddedFileGroup> embeddedFiles = null,
            IReadOnlyList<ThumbnailGroup> thumbnails = null,
            GenericGroup thumbnailShared = null)
        {
            var tables = new LinearizationHintTables();
            // Table F.12 item 5 takes its width from Table F.3's item 11, so that width covers the
            // embedded file groups' identifiers as well as the pages'. The pages' alone do not: a first
            // page's attachment names its stream's first-page identifier, which no later page need
            // reference: two pages sharing one first-page object wrote the sixth one's 5 in one bit, as 1.
            int embeddedIdentifierMax = 0;
            if (embeddedFiles != null)
                foreach (EmbeddedFileGroup g in embeddedFiles)
                    foreach (int id in g.SharedIdentifiers)
                        if (id > embeddedIdentifierMax) embeddedIdentifierMax = id;
            byte[] pageTable = BuildPageOffsetTable(pages, firstPageOffset, embeddedIdentifierMax, out int identifierBits);
            byte[] sharedTable = BuildSharedObjectTable(
                sharedGroups, firstSharedObjectNumber, firstSharedOffset, sharedGroupsInFirstPage);

            var data = new System.IO.MemoryStream();
            data.Write(pageTable, 0, pageTable.Length);
            data.Write(sharedTable, 0, sharedTable.Length);
            if (thumbnails != null && thumbnails.Count > 0)
            {
                tables.ThumbnailTableOffset = (int)data.Length;
                byte[] table = BuildThumbnailTable(thumbnails, thumbnailShared);
                data.Write(table, 0, table.Length);
            }
            // The generic tables after the shared one, in the order given, each at its own key.
            foreach (var (key, group) in generic)
            {
                tables.GenericTableOffsets.Add((key, (int)data.Length));
                byte[] table = BuildGenericTable(group);
                data.Write(table, 0, table.Length);
            }
            if (embeddedFiles != null && embeddedFiles.Count > 0)
            {
                tables.EmbeddedFileTableOffset = (int)data.Length;
                byte[] table = BuildEmbeddedFileTable(embeddedFiles, identifierBits);
                data.Write(table, 0, table.Length);
            }

            tables.Data = data.ToArray();
            tables.SharedTableOffset = pageTable.Length;
            foreach (var (key, offset) in tables.GenericTableOffsets)
                if (key == "O") tables.OutlineTableOffset = offset;
            return tables;
        }

        /// <summary>Each generic hint table's key in the primary hint stream (<c>O</c>, <c>A</c>, <c>E</c>, <c>I</c>, <c>L</c>) and its offset within <see cref="Data"/>.</summary>
        internal List<(string Key, int Offset)> GenericTableOffsets { get; } = new List<(string, int)>();

        /// <summary>Table F.9: four 32-bit fields describing one group of adjacent objects.</summary>
        private static byte[] BuildGenericTable(GenericGroup group)
        {
            var w = new MsbBitWriter(16);
            w.WriteBits(group.FirstObjectNumber, 32);
            w.WriteBits(Fits32(group.Location, "a document-level object group's offset"), 32);
            w.WriteBits(group.ObjectCount, 32);
            w.WriteBits(Fits32(group.Length, "a document-level object group's length"), 32);
            if (group.SharedIdentifiers != null)
            {
                // Table F.10: the count, the bits the greatest identifier needs, then the identifiers.
                int greatest = group.SharedIdentifiers.Count == 0 ? 0 : group.SharedIdentifiers.Max();
                int width = BitsFor(greatest);
                w.WriteBits(group.SharedIdentifiers.Count, 32);
                w.WriteBits(width, 16);
                foreach (int id in group.SharedIdentifiers) w.WriteBits(id, width);
            }
            return w.ToArray();
        }

        /// <summary>
        /// Tables F.11 and F.12: the header, then each of the five items for every group in turn, item
        /// 5's identifiers in Table F.3 item 11's width. Not padded between items: "fields of arbitrary
        /// width without regard to byte boundaries", and unlike the page and shared tables (see the class
        /// remarks) no implementation pads this one to follow instead.
        /// </summary>
        private static byte[] BuildEmbeddedFileTable(IReadOnlyList<EmbeddedFileGroup> groups, int identifierBits)
        {
            int maxStream = 0, maxObjects = 0, maxShared = 0;
            long maxLength = 0;
            foreach (EmbeddedFileGroup g in groups)
            {
                if (g.StreamObjectNumber > maxStream) maxStream = g.StreamObjectNumber;
                if (g.ObjectCount > maxObjects) maxObjects = g.ObjectCount;
                if (g.Length > maxLength) maxLength = g.Length;
                if (g.SharedIdentifiers.Count > maxShared) maxShared = g.SharedIdentifiers.Count;
            }
            int bitsStream = BitsFor(maxStream), bitsObjects = BitsFor(maxObjects);
            int bitsLength = BitsFor(maxLength), bitsShared = BitsFor(maxShared);

            var w = new MsbBitWriter(32);
            w.WriteBits(groups[0].StreamObjectNumber, 32);
            w.WriteBits(Fits32(groups[0].Location, "the first embedded file group's offset"), 32);
            w.WriteBits(groups.Count, 32);
            w.WriteBits(bitsStream, 16);
            w.WriteBits(bitsObjects, 16);
            w.WriteBits(bitsLength, 16);
            w.WriteBits(bitsShared, 16);
            foreach (EmbeddedFileGroup g in groups) w.WriteBits(g.StreamObjectNumber, bitsStream);
            foreach (EmbeddedFileGroup g in groups) w.WriteBits(g.ObjectCount, bitsObjects);
            foreach (EmbeddedFileGroup g in groups) w.WriteBits(Fits32(g.Length, "an embedded file group's length"), bitsLength);
            foreach (EmbeddedFileGroup g in groups) w.WriteBits(g.SharedIdentifiers.Count, bitsShared);
            foreach (EmbeddedFileGroup g in groups)
                foreach (int id in g.SharedIdentifiers) w.WriteBits(id, identifierBits);
            return w.ToArray();
        }

        /// <summary>
        /// Tables F.7 and F.8: the header — the first group's image and offset, how many pages have one, the
        /// widths, the least length and object count, the thumbnail shared objects section — then, for every
        /// page that has a thumbnail, item 1 (the pages without one since the previous entry), then item 2,
        /// then item 3. Unpadded between items, as the embedded file table is (see there).
        /// </summary>
        private static byte[] BuildThumbnailTable(IReadOnlyList<ThumbnailGroup> groups, GenericGroup shared)
        {
            int maxGap = 0, minObjects = int.MaxValue, maxObjects = 0;
            long minLength = long.MaxValue, maxLength = 0;
            int previous = -1;
            foreach (ThumbnailGroup g in groups)
            {
                int gap = g.PageIndex - previous - 1;
                if (gap > maxGap) maxGap = gap;
                previous = g.PageIndex;
                if (g.ObjectCount < minObjects) minObjects = g.ObjectCount;
                if (g.ObjectCount > maxObjects) maxObjects = g.ObjectCount;
                if (g.Length < minLength) minLength = g.Length;
                if (g.Length > maxLength) maxLength = g.Length;
            }
            int bitsGap = BitsFor(maxGap), bitsObjects = BitsFor(maxObjects - minObjects), bitsLength = BitsFor(maxLength - minLength);

            var w = new MsbBitWriter(64);
            w.WriteBits(groups[0].FirstObjectNumber, 32);
            w.WriteBits(Fits32(groups[0].Location, "the first thumbnail's offset"), 32);
            w.WriteBits(groups.Count, 32);
            w.WriteBits(bitsGap, 16);
            w.WriteBits(Fits32(minLength, "the least thumbnail length"), 32);
            w.WriteBits(bitsLength, 16);
            w.WriteBits(minObjects, 32);
            w.WriteBits(bitsObjects, 16);
            w.WriteBits(shared?.FirstObjectNumber ?? 0, 32);
            w.WriteBits(shared == null ? 0 : Fits32(shared.Location, "the thumbnail shared objects' offset"), 32);
            w.WriteBits(shared?.ObjectCount ?? 0, 32);
            w.WriteBits(shared == null ? 0 : Fits32(shared.Length, "the thumbnail shared objects' length"), 32);
            previous = -1;
            foreach (ThumbnailGroup g in groups) { w.WriteBits(g.PageIndex - previous - 1, bitsGap); previous = g.PageIndex; }
            foreach (ThumbnailGroup g in groups) w.WriteBits(g.ObjectCount - minObjects, bitsObjects);
            foreach (ThumbnailGroup g in groups) w.WriteBits(g.Length - minLength, bitsLength);
            return w.ToArray();
        }

        private static byte[] BuildPageOffsetTable(IReadOnlyList<PageInfo> pages, long firstPageOffset,
            int identifierFloor, out int identifierBits)
        {
            int minObjects = int.MaxValue, maxObjects = 0;
            long minLength = long.MaxValue, maxLength = 0;
            int maxShared = 0, maxIdentifier = identifierFloor;

            foreach (PageInfo p in pages)
            {
                if (p.ObjectCount < minObjects) minObjects = p.ObjectCount;
                if (p.ObjectCount > maxObjects) maxObjects = p.ObjectCount;
                if (p.Length < minLength) minLength = p.Length;
                if (p.Length > maxLength) maxLength = p.Length;
                if (p.SharedIdentifiers.Count > maxShared) maxShared = p.SharedIdentifiers.Count;
                foreach (int id in p.SharedIdentifiers)
                    if (id > maxIdentifier) maxIdentifier = id;
            }

            if (pages.Count == 0) { minObjects = 0; minLength = 0; }

            int bitsObjects = BitsFor(maxObjects - minObjects);
            int bitsLength = BitsFor(maxLength - minLength);
            int bitsShared = BitsFor(maxShared);
            int bitsIdentifier = BitsFor(maxIdentifier);
            identifierBits = bitsIdentifier;

            var w = new MsbBitWriter(64);

            // Table F.3, header section, in order. The 16-bit "number of bits needed" items hold a
            // value in 0..32 — "although that range requires only 6 bits, 16-bit numbers shall be
            // used".
            w.WriteBits(minObjects, 32);
            w.WriteBits(Fits32(firstPageOffset, "the first page's offset"), 32);
            w.WriteBits(bitsObjects, 16);
            w.WriteBits(Fits32(minLength, "the least page length"), 32);
            w.WriteBits(bitsLength, 16);
            w.WriteBits(0, 32);                 // least content-stream offset within a page
            w.WriteBits(0, 16);                 // ...and no page deviates from it
            w.WriteBits(Fits32(minLength, "the least content-stream length"), 32);
            w.WriteBits(bitsLength, 16);
            w.WriteBits(bitsShared, 16);
            w.WriteBits(bitsIdentifier, 16);
            w.WriteBits(0, 16);                 // bits per shared-reference numerator: all are zero
            w.WriteBits(SharedDenominator, 16);

            // Table F.4's items are interleaved across pages, one group of like items at a time,
            // each group padded to a byte boundary.
            foreach (PageInfo p in pages) w.WriteBits(p.ObjectCount - minObjects, bitsObjects);
            w.AlignToByte();

            foreach (PageInfo p in pages) w.WriteBits(p.Length - minLength, bitsLength);
            w.AlignToByte();

            // Item 3 is written for every page including the first, whose value is always 0:
            // "For the first page, this number shall be 0; the next two items start with the
            // second page."
            foreach (PageInfo p in pages) w.WriteBits(p.SharedIdentifiers.Count, bitsShared);
            w.AlignToByte();

            for (int i = 1; i < pages.Count; i++)
                foreach (int id in pages[i].SharedIdentifiers) w.WriteBits(id, bitsIdentifier);
            w.AlignToByte();

            // Item 5, the numerators, and item 6, the content-stream offset within the page, are
            // both zero bits wide here, so their groups occupy no bytes. They are still groups:
            // an aligned writer emits nothing for them, and an aligned reader consumes nothing.

            foreach (PageInfo p in pages) w.WriteBits(p.Length - minLength, bitsLength);
            w.AlignToByte();

            return w.ToArray();
        }

        private static byte[] BuildSharedObjectTable(
            IReadOnlyList<SharedGroup> groups, int firstSharedObjectNumber, long firstSharedOffset,
            int groupsInFirstPage)
        {
            long minLength = long.MaxValue, maxLength = 0;
            int maxObjects = 0;
            foreach (SharedGroup g in groups)
            {
                if (g.Length < minLength) minLength = g.Length;
                if (g.Length > maxLength) maxLength = g.Length;
                if (g.ObjectCount > maxObjects) maxObjects = g.ObjectCount;
            }
            if (groups.Count == 0) minLength = 0;

            int bitsLength = BitsFor(maxLength - minLength);
            int bitsObjects = BitsFor(maxObjects - 1);

            var w = new MsbBitWriter(32);

            // Table F.5, header section.
            w.WriteBits(firstSharedObjectNumber, 32);
            w.WriteBits(Fits32(firstSharedOffset, "the shared objects section's offset"), 32);
            w.WriteBits(groupsInFirstPage, 32);
            w.WriteBits(groups.Count, 32);
            w.WriteBits(bitsObjects, 16);
            w.WriteBits(Fits32(minLength, "the least shared object group length"), 32);
            w.WriteBits(bitsLength, 16);

            // Table F.6, one group of like items at a time.
            foreach (SharedGroup g in groups) w.WriteBits(g.Length - minLength, bitsLength);
            w.AlignToByte();

            // Item 2: no group carries an MD5 signature, so item 3 never appears.
            foreach (SharedGroup unused in groups) w.WriteBit(0);
            w.AlignToByte();

            foreach (SharedGroup g in groups) w.WriteBits(g.ObjectCount - 1, bitsObjects);
            w.AlignToByte();

            return w.ToArray();
        }

        /// <summary>
        /// The number of bits needed to represent <paramref name="value"/>, which is 0 for 0.
        /// </summary>
        /// <remarks>
        /// Zero-width fields are normal here and are what a uniform document produces: five pages of
        /// the same length need no bits at all to express the difference from the least length.
        /// </remarks>
        private static int BitsFor(long value)
        {
            int bits = 0;
            while (value > 0) { bits++; value >>= 1; }
            return bits;
        }

        /// <summary>
        /// Refuses a value that does not fit the 32-bit field Annex F gives it, rather than writing
        /// its low 32 bits.
        /// </summary>
        /// <remarks>
        /// A truncated offset is a hint table that decodes cleanly and sends the reader to the wrong
        /// place — the same class of failure as a truncated cross-reference offset, which
        /// <c>PdfWriter.WriteXrefTable</c> also refuses rather than wraps.
        /// </remarks>
        private static long Fits32(long value, string what)
        {
            if (value < 0 || value > uint.MaxValue)
                throw new Tetitulz.Pdf.Core.PdfException(
                    $"Linearization hint tables cannot represent {what} ({value}): Annex F gives it " +
                    "a 32-bit field, so the file is too large to hint.");
            return value;
        }
    }
}
