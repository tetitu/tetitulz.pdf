// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Decoding ISO 32000-1 Annex F's primary hint stream — GAPS.md §23 slice 14d.
/// </summary>
/// <remarks>
/// <para><b>The assertions relate the tables to facts measured in the file, not to the encoder.</b>
/// A hint table is a bit stream with no checksum, no length per field and nothing self-describing,
/// so an internally consistent table that is factually wrong parses perfectly. Every claim below is
/// therefore checked against where objects physically start in the bytes — which is also what makes
/// these tests able to fail when the decoder and the encoder are changed together.</para>
///
/// <para><b>What the decoding is for</b> is the per-page byte range: with the parameter dictionary
/// from the first 1024 bytes and the hint stream from the one range it names, a client can ask for
/// page <i>k</i> of a document it has never opened. That is why <see cref="PdfPageHint.Offset"/> is
/// asserted against real object positions rather than against itself.</para>
/// </remarks>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class LinearizationHintReaderTests
{
    private const int PageCount = 5;

    private static byte[] Linearized(int pages = PageCount)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(pages));
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    private static PdfLinearizationHints Read(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints),
                    PdfLinearizationHintReader.LastError);
        return hints;
    }

    /// <summary>Object number → the offset at which its <c>N 0 obj</c> header begins.</summary>
    private static Dictionary<int, long> ObjectStarts(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        var starts = new Dictionary<int, long>();

        foreach (Match m in Regex.Matches(text, @"(?<=^|[\r\n])(\d+) 0 obj"))
            starts[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Index;

        return starts;
    }

    // ================================================================
    //  What the tables say, against what the file contains
    // ================================================================

    /// <summary>
    /// Every page's hinted range begins exactly where an object begins — the claim a client acts on
    /// when it fetches that range, and one no internally consistent table can fake.
    /// </summary>
    [Fact]
    public void EveryPagesRangeStartsAtARealObject()
    {
        byte[] pdf = Linearized();
        PdfLinearizationHints hints = Read(pdf);

        Assert.Equal(PageCount, hints.Pages.Count);

        var starts = new HashSet<long>(ObjectStarts(pdf).Values);
        for (int i = 0; i < hints.Pages.Count; i++)
            Assert.True(starts.Contains(hints.Pages[i].Offset),
                        $"page {i}'s hinted offset {hints.Pages[i].Offset} is not an object start");
    }

    /// <summary>
    /// The pages' ranges are contiguous and in page order, which is what lets a client compute the
    /// next one without re-reading the table.
    /// </summary>
    [Fact]
    public void ThePagesRangesTileTheFileInOrder()
    {
        PdfLinearizationHints hints = Read(Linearized());

        for (int i = 1; i < hints.Pages.Count; i++)
            Assert.Equal(hints.Pages[i - 1].Offset + hints.Pages[i - 1].Length, hints.Pages[i].Offset);
    }

    /// <summary>
    /// <b>Annex F.4 NOTE 3 is applied.</b> Hint positions are written as if the hint stream were not
    /// in the file, so the first page's raw position is short by the stream's length. The corrected
    /// value is asserted against where that object really is — the only check that can tell a
    /// corrected offset from an uncorrected one, since both are plausible numbers.
    /// </summary>
    [Fact]
    public void ThePositionsAreCorrectedForTheHintStreamsOwnLength()
    {
        byte[] pdf = Linearized();

        using var input = new MemoryStream(pdf, writable: false);
        Assert.True(PdfRevisions.TryReadLinearization(input, out PdfLinearizationInfo info));

        PdfLinearizationHints hints = Read(pdf);
        Dictionary<int, long> starts = ObjectStarts(pdf);

        Assert.Equal(starts[info.FirstPageObjectNumber], hints.Pages[0].Offset);

        // And the correction was not a no-op: the raw table value is short by the stream's length.
        Assert.True(hints.Pages[0].Offset > info.HintStreamOffset,
                    "the first page precedes the hint stream, so this fixture cannot show the rule");
    }

    /// <summary>
    /// A page's declared object count matches the objects its range actually contains.
    /// </summary>
    [Fact]
    public void EachPagesObjectCountMatchesItsRange()
    {
        byte[] pdf = Linearized();
        PdfLinearizationHints hints = Read(pdf);
        Dictionary<int, long> starts = ObjectStarts(pdf);

        for (int i = 0; i < hints.Pages.Count; i++)
        {
            long from = hints.Pages[i].Offset;
            long to = from + hints.Pages[i].Length;

            int actual = 0;
            foreach (long at in starts.Values)
                if (at >= from && at < to) actual++;

            Assert.Equal(actual, hints.Pages[i].ObjectCount);
        }
    }

    /// <summary>
    /// Table F.5 puts the first page's own objects at the head of the shared table, so a caller
    /// cannot index it without knowing how many — which is what this reports.
    /// </summary>
    [Fact]
    public void TheSharedTableOpensWithTheFirstPagesOwnObjects()
    {
        PdfLinearizationHints hints = Read(Linearized());

        Assert.Equal(hints.Pages[0].ObjectCount, hints.SharedObjectsInFirstPage);
        Assert.Equal(hints.Pages[0].ObjectCount, hints.FirstPageObjectCount);
        Assert.True(hints.SharedObjects.Count >= hints.SharedObjectsInFirstPage);
    }

    /// <summary>
    /// Later pages reference shared objects <b>by index into the same table</b>, and the indices
    /// they use here name the first page's own entries — which is why
    /// <see cref="PdfLinearizationHints.SharedObjectsInFirstPage"/> has to be reported at all.
    /// </summary>
    /// <remarks>
    /// <para>Table F.5 puts the first page's objects at the head of the shared table and the shared
    /// section after them, in one index space. A later page that needs an object the first page
    /// also has therefore carries an index below
    /// <see cref="PdfLinearizationHints.SharedObjectsInFirstPage"/>, and a caller that assumed those
    /// entries were private to page 1 would resolve it to the wrong object.</para>
    ///
    /// <para>This fixture has no shared <i>section</i> — no later page reaches an object the first
    /// page cannot — so the correlation of shared-object offsets against real objects lives in the
    /// linearizer's own conformance suite, over a fixture with a genuinely shared XObject.</para>
    /// </remarks>
    [Fact]
    public void LaterPagesIndexTheSharedTableIncludingTheFirstPagesEntries()
    {
        PdfLinearizationHints hints = Read(Linearized());

        Assert.Equal(hints.SharedObjectsInFirstPage, hints.SharedObjects.Count);
        Assert.Equal(hints.Pages[0].ObjectCount, hints.SharedObjects.Count);
        Assert.All(hints.SharedObjects, g => Assert.True(g.ObjectCount >= 1));

        // The first page's own objects are not "shared with" itself.
        Assert.Empty(hints.Pages[0].SharedObjectIdentifiers);

        // Some later page does reference one, or this asserts over nothing.
        int referencing = 0;
        for (int i = 1; i < hints.Pages.Count; i++)
        {
            if (hints.Pages[i].SharedObjectIdentifiers.Count > 0) referencing++;
            Assert.All(hints.Pages[i].SharedObjectIdentifiers,
                       id => Assert.InRange(id, 0, hints.SharedObjects.Count - 1));
        }

        Assert.True(referencing > 0, "no page referenced a shared object, so the indices are untested");
    }

    // ================================================================
    //  Reading only the two ranges, over a byte-range source
    // ================================================================

    /// <summary>
    /// <b>Two fetches and a client can locate any page.</b> The head gives the parameter dictionary,
    /// which names the hint stream's range; the hint stream names every page's. Nothing else of the
    /// file is read — asserted, because "it worked" is also what reading the whole file looks like.
    /// </summary>
    [Fact]
    public void TheHintsAreReadFromTwoRangesAndNothingElse()
    {
        byte[] pdf = Linearized();
        var source = new CountingSource(pdf);

        Assert.Equal(PdfLinearizationState.Linearized,
                     PdfLinearizationHintReader.TryRead(source, out PdfLinearizationHints hints));
        Assert.NotNull(hints);
        Assert.Equal(PageCount, hints.Pages.Count);

        Assert.True(source.BytesServed < pdf.Length,
                    $"served {source.BytesServed} bytes of a {pdf.Length}-byte file, so it read the lot");
    }

    /// <summary>
    /// A source whose hint-stream range has not arrived says <b>not yet</b> — distinct from a bad
    /// table, which is <c>Linearized</c> with no hints and a reported reason. A client that confuses
    /// them either waits for bytes it holds or abandons a document it could read.
    /// </summary>
    [Fact]
    public void AHintStreamThatHasNotArrivedIsNotYetAvailable()
    {
        byte[] pdf = Linearized();

        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfRevisions.TryReadLinearization(whole, out PdfLinearizationInfo info));

        // Enough for the head, not enough for the hint stream.
        var source = new PrefixSource(pdf, arrived: (int)info.HintStreamOffset);

        Assert.Equal(PdfLinearizationState.NotYetAvailable,
                     PdfLinearizationHintReader.TryRead(source, out PdfLinearizationHints hints));
        Assert.Null(hints);
    }

    /// <summary>An unlinearized file has no hints, and the refusal says which step failed.</summary>
    [Fact]
    public void AnOrdinaryFileHasNoHints()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("linearization parameter dictionary",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A null argument is a programming error on either overload.</summary>
    [Fact]
    public void ANullArgumentIsRefused()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfLinearizationHintReader.TryRead((Stream)null, out _));
        Assert.Throws<ArgumentNullException>(
            () => PdfLinearizationHintReader.TryRead((IByteRangeSource)null, out _));
    }

    // ================================================================
    //  Hostile tables
    // ================================================================

    /// <summary>
    /// <b>Adversarial: a field width the format cannot hold.</b> Table F.3's widths are read out of
    /// the stream in 16 bits, so a file can ask for a 65 535-bit field per item and multiply every
    /// per-item loop by four thousand. Nothing the tables carry is wider than 32 bits, so this is
    /// refused rather than clamped — hints that are silently wrong are worse than none.
    /// </summary>
    [Fact]
    public void AdversarialFieldWidthIsRefused()
    {
        // 32-bit minObjects, 32-bit first-page offset, then a 16-bit width of 0xFFFF.
        var table = new byte[64];
        table[8] = 0xFF;
        table[9] = 0xFF;

        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 32), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("-bit ceiling", PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: tables that claim more than the stream holds.</b> The bit reader refuses to
    /// read past its data rather than throwing an index error at the caller.
    /// </summary>
    [Fact]
    public void AdversarialTruncatedTableIsRefused()
    {
        using var input = new MemoryStream(CraftedHintFile(new byte[20], sharedOffset: 8), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.NotNull(PdfLinearizationHintReader.LastError);
    }

    /// <summary>
    /// <b>Adversarial: <c>/S</c> pointing outside the stream's own data</b>, which would otherwise
    /// start the shared table's bit reader at an arbitrary position.
    /// </summary>
    /// <remarks>
    /// <b>The refusal is asserted by its own words, not by the substring <c>/S</c>.</b> A mutation
    /// deleting this guard survived an assertion for <c>/S</c>, because the page-table end check
    /// downstream also refuses and also says <c>/S</c> — so the test passed while the guard it is
    /// named for did nothing. A refusal test must refuse for the reason in its name.
    /// </remarks>
    [Fact]
    public void AdversarialSharedTableOffsetIsRefused()
    {
        using var input = new MemoryStream(CraftedHintFile(new byte[64], sharedOffset: 100_000),
                                           writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("does not point inside its own data",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a page table that does not end where <c>/S</c> says it does.</b>
    /// </summary>
    /// <remarks>
    /// This is the only structural check the format allows. The tables carry no checksum, no length
    /// per field and nothing self-describing, so a decoder reading the item groups in the wrong
    /// order — or getting the undocumented per-group byte padding wrong — still produces numbers.
    /// Landing somewhere other than <c>/S</c> is the single observable consequence.
    /// </remarks>
    [Fact]
    public void AdversarialTableThatDoesNotEndWhereSSaysIsRefused()
    {
        // All widths zero, so every item group consumes nothing and the cursor stops at the end of
        // the fixed header — which is not byte 32.
        using var input = new MemoryStream(CraftedHintFile(new byte[64], sharedOffset: 32),
                                           writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("/S puts the shared object table at",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A document with no outline has no generic hint table, and the reader reports its absence
    /// rather than inventing an empty one — the distinction a client needs to know whether it can
    /// fetch the outline in one range.
    /// </summary>
    [Fact]
    public void ADocumentWithoutAnOutlineHasNoOutlineHint()
    {
        Assert.Null(Read(Linearized()).Outline);
    }

    /// <summary>
    /// <b>Adversarial: an oversized <c>/H</c> length.</b> The head that carries it is a kilobyte and
    /// the range it claims is gigabytes — so the claim is measured against the file's own length
    /// before a buffer is allocated for it.
    /// </summary>
    [Fact]
    public void AdversarialHintRangePastTheFileIsRefusedBeforeAllocating()
    {
        // Under the decoded-stream ceiling and vastly past the file, so it is the file's own length
        // that refuses it — the check this test is named for.
        byte[] pdf = CraftedHintFile(new byte[64], sharedOffset: 32, hintLengthOverride: 400_000_000);

        using var input = new MemoryStream(pdf, writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);

        // The EARLY refusal's own words. Reading the range and coming up short refuses too, with a
        // different message — and a test that accepts either cannot tell whether 400 MB was
        // allocated before the answer was known, which is the entire claim in this test's name.
        Assert.Contains("does not fit in the file's",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a page count no hint stream that size could describe.</b> <c>/N</c> is the
    /// producer's number and every per-page loop runs that many times, so it is bounded by what the
    /// stream could physically hold before a single array is allocated for it.
    /// </summary>
    [Fact]
    public void AdversarialPageCountLargerThanTheStreamIsRefused()
    {
        byte[] pdf = CraftedHintFile(new byte[64], sharedOffset: 36, pageCount: 1_000_000);

        using var input = new MemoryStream(pdf, writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("cannot be described by a hint stream of",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a page offset table declaring more shared-object references than the
    /// ceiling.</b> The identifier loop is <c>pages × count</c> and both factors come out of the
    /// stream, so the bound is on their product and is checked as it accumulates.
    /// </summary>
    /// <remarks>
    /// The per-page count is read at the width the table declares. At 32 bits a count of
    /// <c>0xFFFFFFFF</c> narrows to <b>-1</b>, which made the running total go down and the ceiling
    /// unreachable — found by building this fixture, not by the sweep, because no fixture had ever
    /// reached the ceiling at all.
    /// </remarks>
    [Fact]
    public void AdversarialSharedReferenceCountIsRefused()
    {
        var table = new byte[96];
        WriteUInt16(table, 28, 32);                       // bits per page's shared-object count
        for (int i = 36; i < 56; i++) table[i] = 0xFF;    // five 32-bit counts, each ~4.29 billion

        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 56), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("shared-object references",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a shared object table that does not end where <c>/O</c> says the next one
    /// starts.</b> The same structural check as the page table's, one table along.
    /// </summary>
    /// <remarks>
    /// A conformant file cannot show this: its tables abut exactly, so removing the check changes
    /// nothing anywhere. Only a stream built to land wrong reaches it — which is why the row
    /// survived against every real fixture, including the one with an outline.
    /// </remarks>
    [Fact]
    public void AdversarialSharedTableThatDoesNotEndWhereOSaysIsRefused()
    {
        // Page-table widths all zero, so the page table is exactly its 36-byte header and ends at
        // /S = 36. The shared table's header is 24 bytes with no groups, ending at 60 — and /O
        // claims the generic table begins at 80.
        var table = new byte[96];
        using var input = new MemoryStream(
            CraftedHintFile(table, sharedOffset: 36, outlineOffset: 80), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("the shared object table ended at byte",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a generic hint table that does not end at the end of the stream.</b>
    /// Table F.9 is four 32-bit fields and the last table there is, so anything after it is unread
    /// bytes the decoder cannot account for.
    /// </summary>
    [Fact]
    public void AdversarialGenericTableThatDoesNotEndTheStreamIsRefused()
    {
        // /S = 36 and /O = 60, so the page and shared tables land exactly; the generic table then
        // reads its 16 bytes and stops at 76, with 20 bytes of stream left over.
        var table = new byte[96];
        using var input = new MemoryStream(
            CraftedHintFile(table, sharedOffset: 36, outlineOffset: 60), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("generic hint table ended at byte",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Two generic tables, each Table F.9's sixteen bytes, each ending where the next begins: both are read,
    /// each with its own first object number.
    /// </summary>
    [Fact]
    public void TwoGenericTablesAreEachRead()
    {
        // /S = 36 and the shared header ends at 60, the outline table there, the information table at 76.
        var table = new byte[92];
        table[63] = 7;
        table[79] = 11;
        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, outlineOffset: 60, extraKeys: " /I 76"), writable: false);

        Assert.True(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints), PdfLinearizationHintReader.LastError);
        Assert.Equal(7, hints.Outline.FirstObjectNumber);
        Assert.Equal(11, hints.Information.FirstObjectNumber);
        Assert.Null(hints.PageLabels);
    }

    /// <summary>
    /// An extended generic table (Table F.10, the logical structure's <c>/C</c>): the four fields, a count, a bit
    /// width, then the identifiers packed at that width — read, and ending exactly at the stream's end.
    /// </summary>
    [Fact]
    public void AnExtendedTableListsItsSharedIdentifiers()
    {
        var table = new byte[84];
        table[63] = 9;                // first object
        table[79] = 2;                // two shared references
        table[81] = 8;                // eight bits each
        table[82] = 3; table[83] = 5; // identifiers 3 and 5
        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, extraKeys: " /C 60"), writable: false);

        Assert.True(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints), PdfLinearizationHintReader.LastError);
        Assert.Equal(9, hints.LogicalStructure.FirstObjectNumber);
        Assert.Equal(new[] { 3, 5 }, hints.LogicalStructure.SharedObjectIdentifiers);
    }

    /// <summary><b>Adversarial: an extended table declaring more references than the stream holds</b> is refused before anything is allocated for them.</summary>
    [Fact]
    public void AdversarialExtendedReferenceCountIsRefused()
    {
        var table = new byte[92];
        table[76] = 0x7F;             // a reference count near 2^31
        table[81] = 8;
        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, extraKeys: " /C 60"), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("extended hint table declares", PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A table that is not generic (a thumbnail table) still marks where the shared table ends, and is read as
    /// what it is: Table F.7's header, 42 bytes, here with no pages — not as a generic table.
    /// </summary>
    [Fact]
    public void AThumbnailTableBoundsTheSharedTable()
    {
        var table = new byte[60 + 42];
        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, extraKeys: " /T 60"), writable: false);

        Assert.True(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints), PdfLinearizationHintReader.LastError);
        Assert.Null(hints.Outline);
        Assert.Empty(hints.Thumbnails!);
        Assert.Null(hints.ThumbnailSharedObjects);
    }

    /// <summary><b>Adversarial: a thumbnail table declaring more pages than its stream could hold is refused, not looped over.</b></summary>
    [Fact]
    public void AdversarialThumbnailCountIsRefused()
    {
        var table = new byte[60 + 42];
        table[68] = 0x7F;             // Table F.7 item 3: a page count near 2^31
        table[73] = 8;                // item 4: eight bits per gap
        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, extraKeys: " /T 60"), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("thumbnails, more than the stream holds", PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a thumbnail table declaring more thumbnails than the document has pages is refused</b>
    /// — a page has one at most. With every width 0 the stream's bits allowed eight million a megabyte; here
    /// one more than /N, and the control /N itself, read.
    /// </summary>
    [Fact]
    public void AdversarialThumbnailCountPastThePageCountIsRefused()
    {
        PdfLinearizationHints Read(int thumbnails, out string error)
        {
            var table = new byte[60 + 42];
            table[71] = (byte)thumbnails;   // Table F.7 item 3, every width 0
            using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, extraKeys: " /T 60"), writable: false);
            PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints);
            error = PdfLinearizationHintReader.LastError;
            return hints;
        }

        Assert.Null(Read(PageCount + 1, out string refused));
        Assert.Contains($"thumbnails for {PageCount} pages", refused ?? "", StringComparison.Ordinal);
        Assert.Equal(PageCount, Read(PageCount, out _)!.Thumbnails!.Count);
    }

    /// <summary><b>Adversarial: an embedded file stream table declaring more groups than its stream could hold is refused.</b></summary>
    [Fact]
    public void AdversarialEmbeddedFileGroupCountIsRefused()
    {
        var table = new byte[60 + 20];
        table[68] = 0x7F;             // Table F.11 item 3: a group count near 2^31
        table[73] = 8;                // item 4: eight bits per stream object number
        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, extraKeys: " /B 60"), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("embedded file groups, more than the stream holds", PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a count of objects past the most a document may hold is refused, whatever the
    /// stream's bits allow.</b> Every width here is 0, so six items cost the stream nothing: a hint stream
    /// inflated to the decoded ceiling asked for two thousand million pages that way. Each table's six is
    /// read under a ceiling of six and refused under five, naming its own table — the pages through /N
    /// with every other table empty, the rest with /N at five.
    /// </summary>
    [Theory]
    [InlineData("pages", "pages (/N)")]
    [InlineData("shared", "shared object groups")]
    [InlineData("embedded", "embedded file groups (/B)")]
    [InlineData("extended", "shared references in the /C table")]
    public void AdversarialObjectCountPastTheCrossReferenceCeilingIsRefused(string table, string what)
    {
        const int Count = 6;
        byte[] data;
        string keys = "";
        int pages = PageCount;
        switch (table)
        {
            case "pages":
                data = new byte[60];
                pages = Count;
                break;
            case "shared":
                data = new byte[61];          // six one-bit signatures, padded to a byte
                WriteUInt32(data, 36 + 12, Count);
                break;
            case "embedded":
                data = new byte[60 + 20];     // Table F.11's header, every width 0
                WriteUInt32(data, 60 + 8, Count);
                keys = " /B 60";
                break;
            default:
                data = new byte[60 + 22];     // Table F.10's header, identifiers of no bits
                WriteUInt32(data, 60 + 16, Count);
                keys = " /C 60";
                break;
        }

        PdfLinearizationHints ReadUnder(int ceiling, out string error)
        {
            PdfLimits.MaxXrefEntries = ceiling;
            try
            {
                using var input = new MemoryStream(CraftedHintFile(data, sharedOffset: 36, pageCount: pages, extraKeys: keys), writable: false);
                PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints);
                error = PdfLinearizationHintReader.LastError;
                return hints;
            }
            finally
            {
                PdfLimits.ResetToDefaults();
            }
        }

        Assert.Null(ReadUnder(Count - 1, out string refused));
        Assert.Contains($"declares {Count} {what}, more objects than the {Count - 1}", refused ?? "", StringComparison.Ordinal);

        PdfLinearizationHints read = ReadUnder(Count, out string accepted);
        Assert.True(read != null, accepted);
        int items = table switch
        {
            "pages" => read!.Pages.Count,
            "shared" => read!.SharedObjects.Count,
            "embedded" => read!.EmbeddedFiles!.Count,
            _ => read!.LogicalStructure.SharedObjectIdentifiers.Count,
        };
        Assert.Equal(Count, items);
    }

    /// <summary><b>Adversarial: a table offset at or before the shared table, and two keys naming one table, are refused.</b></summary>
    [Theory]
    [InlineData(" /I 20", "points at or before")]
    [InlineData(" /I 60 /L 60", "point at the same table")]
    public void AdversarialTableOffsetsAreRefused(string keys, string why)
    {
        var table = new byte[92];
        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36, extraKeys: keys), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains(why, PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a shared object table declaring more groups than the stream could hold.</b>
    /// </summary>
    [Fact]
    public void AdversarialSharedGroupCountIsRefused()
    {
        // All page-table widths are zero, so the page table is exactly its 36-byte header and ends
        // at /S. The shared header's fourth 32-bit field is the group count.
        var table = new byte[96];
        WriteUInt32(table, 36 + 12, 0x7FFFFFFF);

        using var input = new MemoryStream(CraftedHintFile(table, sharedOffset: 36), writable: false);

        Assert.False(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("groups, which a hint stream of",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a source that reads short instead of deferring.</b>
    /// </summary>
    /// <remarks>
    /// <para><see cref="IByteRangeSource"/> says a short read means <i>end of file</i> and a range
    /// that has not arrived means <c>false</c>, so a source which does not know its length must
    /// defer rather than hand back a partial buffer. This one does neither — unknown length, and a
    /// truthful-looking <c>true</c> over half the bytes.</para>
    ///
    /// <para><b>A public seam is implemented by other people, so the reader checks rather than
    /// trusts.</b> Decoding the partial buffer would produce tables built from whatever followed in
    /// memory: not a refusal, not a wait, but confident nonsense.</para>
    /// </remarks>
    [Fact]
    public void AdversarialSourceThatReadsShortInsteadOfDeferring()
    {
        byte[] pdf = Linearized();

        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfRevisions.TryReadLinearization(whole, out PdfLinearizationInfo info));

        var source = new ShortReadingSource(pdf, stopAt: (int)(info.HintStreamOffset + info.HintStreamLength / 2));

        Assert.Equal(PdfLinearizationState.Linearized,
                     PdfLinearizationHintReader.TryRead(source, out PdfLinearizationHints hints));
        Assert.Null(hints);
        Assert.Contains("runs past the end of the file",
                        PdfLinearizationHintReader.LastError ?? "", StringComparison.Ordinal);
    }

    private static void WriteUInt16(byte[] b, int at, int value)
    {
        b[at] = (byte)(value >> 8);
        b[at + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] b, int at, uint value)
    {
        b[at] = (byte)(value >> 24);
        b[at + 1] = (byte)(value >> 16);
        b[at + 2] = (byte)(value >> 8);
        b[at + 3] = (byte)value;
    }

    // ================================================================

    /// <summary>
    /// A file whose first object is a linearization dictionary and whose hint stream carries
    /// <paramref name="table"/> verbatim, uncompressed — so a test can state exactly what the
    /// decoder is handed.
    /// </summary>
    private static byte[] CraftedHintFile(
        byte[] table, int sharedOffset, long hintLengthOverride = 0, int pageCount = PageCount,
        int outlineOffset = -1, string extraKeys = "")
    {
        // Two passes: /H needs the hint object's offset and length, which depend on the text that
        // carries them. The dictionary is padded to a fixed width so the second pass cannot move it.
        string hintObject =
            "2 0 obj\n<< /Length " + table.Length.ToString(CultureInfo.InvariantCulture)
            + " /S " + sharedOffset.ToString(CultureInfo.InvariantCulture)
            + (outlineOffset >= 0 ? " /O " + outlineOffset.ToString(CultureInfo.InvariantCulture) : string.Empty)
            + extraKeys + " >>\nstream\n";
        string tail = "\nendstream\nendobj\n";

        string header = "%PDF-1.7\n";
        string dictionary =
            "1 0 obj\n<< /Linearized 1 /L 99999 /H [{0} {1}] /O 3 /E 900 /N "
            + pageCount.ToString(CultureInfo.InvariantCulture) + " /T 800 >>\nendobj\n";

        // Format once with fixed-width placeholders so the real values cannot change the length.
        string probe = string.Format(CultureInfo.InvariantCulture, dictionary, "0000000000", "0000000000");
        int hintOffset = header.Length + probe.Length;
        int hintLength = hintObject.Length + table.Length + tail.Length;

        string real = string.Format(
            CultureInfo.InvariantCulture, dictionary,
            hintOffset.ToString("D10", CultureInfo.InvariantCulture),
            (hintLengthOverride > 0 ? hintLengthOverride : hintLength).ToString("D10", CultureInfo.InvariantCulture));

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(header));
        bytes.AddRange(Encoding.ASCII.GetBytes(real));
        bytes.AddRange(Encoding.ASCII.GetBytes(hintObject));
        bytes.AddRange(table);
        bytes.AddRange(Encoding.ASCII.GetBytes(tail));
        return bytes.ToArray();
    }

    /// <summary>A complete file that records how many bytes it was actually asked for.</summary>
    private sealed class CountingSource : IByteRangeSource
    {
        private readonly byte[] _all;

        public CountingSource(byte[] all) => _all = all;

        public long BytesServed { get; private set; }

        public long? Length => _all.Length;

        public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
        {
            read = 0;
            if (offset >= _all.Length) return true;

            read = (int)Math.Min(count, _all.Length - offset);
            Array.Copy(_all, offset, buffer, bufferOffset, read);
            BytesServed += read;
            return true;
        }
    }

    /// <summary>
    /// A source that breaks the contract: it does not know its length and still reads short instead
    /// of answering <c>false</c>.
    /// </summary>
    private sealed class ShortReadingSource : IByteRangeSource
    {
        private readonly byte[] _all;
        private readonly int _stopAt;

        public ShortReadingSource(byte[] all, int stopAt)
        {
            _all = all;
            _stopAt = stopAt;
        }

        /// <summary>Unknown — which is what makes a short read a contract violation.</summary>
        public long? Length => null;

        public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
        {
            read = 0;
            if (offset >= _stopAt) return true;

            read = (int)Math.Min(count, _stopAt - offset);
            Array.Copy(_all, offset, buffer, bufferOffset, read);
            return true;
        }
    }

    /// <summary>A file of which only the first <c>arrived</c> bytes are here.</summary>
    private sealed class PrefixSource : IByteRangeSource
    {
        private readonly byte[] _all;
        private readonly int _arrived;

        public PrefixSource(byte[] all, int arrived)
        {
            _all = all;
            _arrived = arrived;
        }

        public long? Length => _all.Length;

        public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
        {
            read = 0;
            if (offset >= _all.Length) return true;

            long wanted = Math.Min(count, _all.Length - offset);
            if (offset + wanted > _arrived) return false;

            Array.Copy(_all, offset, buffer, bufferOffset, wanted);
            read = (int)wanted;
            return true;
        }
    }
}
