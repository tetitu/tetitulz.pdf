// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// What <see cref="PdfLinearizer"/>'s output satisfies of ISO 32000-1 Annex F, asserted against the
/// raw bytes.
/// </summary>
/// <remarks>
/// <para><b>Every assertion reads the file rather than a parse of it.</b> A PDF parser normalises
/// exactly the things Annex F is about — byte offsets, object order, where a table physically sits,
/// which trailer a value came from — so parsing the output would hide the whole subject. The two
/// structures that make a linearized file useful, the first-page cross-reference table and the
/// primary hint stream, are invisible to every parser in this repository by design: a reader that
/// ignores them still reads the document correctly.</para>
///
/// <para><b>That is why these tests once pinned two gaps instead of testing two features.</b> The
/// output carried a <c>/Linearized</c> dictionary and put the first page first, so it looked
/// linearized to anything that only read the dictionary — with <c>/H [0 0]</c> naming a hint stream
/// at offset 0 (which is <c>%PDF-1.4</c>) and one cross-reference table at the end of the file. Both
/// are now written, the two <c>Gap…</c> tests are gone, and what replaced them asserts the
/// structures rather than their absence.</para>
///
/// <para><b>Confirmed against an independent checker outside this suite.</b> an independent structure check re-derives a
/// file's linearization data and compares it with what is written; it reports nothing for this
/// output across sixty combinations of page count, inherited attributes, outlines, an information
/// dictionary, shared and private image XObjects and sparse input numbering. An independent implementation extracts
/// all pages in order from the same files. Neither oracle shares any code with this library, which
/// is the only kind of evidence that reaches a bit-packed table nothing else reads.</para>
/// </remarks>
public class LinearizationConformanceTests
{
    // ================================================================== the parameter dictionary

    /// <summary>
    /// <c>/L</c> is exactly the file length, which Annex F makes the test of whether the
    /// linearization data may be trusted at all.
    /// </summary>
    /// <remarks>
    /// Table F.1: "It shall be exactly equal to the actual length of the PDF file. A mismatch
    /// indicates that the file is not linearized and shall be treated as ordinary PDF, ignoring
    /// linearization information." So a wrong <c>/L</c> does not corrupt the file — it silently
    /// discards every other thing the dictionary says, which is the failure that looks like nothing.
    /// </remarks>
    [Fact]
    public void TheDeclaredFileLengthIsExactlyTheFileLength()
    {
        byte[] pdf = Linearize(FivePagePdf());

        Assert.Equal(pdf.Length, Parameter(pdf, "L"));
    }

    /// <summary><c>/N</c> is the page count.</summary>
    [Fact]
    public void TheDeclaredPageCountMatchesTheDocument()
        => Assert.Equal(5, Parameter(Linearize(FivePagePdf()), "N"));

    /// <summary>
    /// <c>/T</c> is the offset of the white-space preceding the main table's entry for object 0.
    /// </summary>
    /// <remarks>
    /// <para><b>Not the <c>startxref</c> value, and this harness found it wrong.</b> Table F.1 spells
    /// out the trap in the entry's own definition — "Note that this differs from the <c>Prev</c>
    /// entry in the first-page trailer, which gives the location of the xref line that precedes the
    /// table" — and <c>/T</c> was the <c>xref</c> keyword's offset, nine bytes early here and further
    /// on a file with more objects, because the subsection header carries the object count.</para>
    /// <para>The consequence is quiet: a reader seeking to <c>/T</c> to read the main table lands on
    /// <c>xref\n0 15\n</c> instead of on an entry, so it either re-scans or gives up on the
    /// linearization data. Nothing reports it.</para>
    /// </remarks>
    [Fact]
    public void TheMainCrossReferenceOffsetPointsAtObjectZerosEntryNotAtTheXrefKeyword()
    {
        byte[] pdf = Linearize(FivePagePdf());

        int entry = IndexOf(pdf, "0000000000 65535 f");
        Assert.True(entry > 0, "the main cross-reference table has no entry for object 0");

        Assert.Equal(entry - 1, Parameter(pdf, "T"));
    }

    /// <summary>
    /// <c>/O</c> names the first page's page object, and that object really is a <c>/Type /Page</c>.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A number that indexes nothing is a dead pointer; a number that indexes
    /// the <i>page tree node</i> rather than the page is the plausible wrong answer, because both
    /// are near the front of a linearized file.
    /// </remarks>
    [Fact]
    public void TheFirstPageObjectNumberNamesAPageObject()
    {
        byte[] pdf = Linearize(FivePagePdf());
        int first = Parameter(pdf, "O");

        string body = ObjectBody(pdf, first);
        Assert.Contains("/Type /Page", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /Pages", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/E</c> bounds the first page: every object it needs ends before that offset.
    /// </summary>
    /// <remarks>
    /// Table F.1 defines <c>/E</c> as the end of the first page. This asserts the property a reader
    /// depends on — that fetching the first <c>/E</c> bytes is enough to draw page 1 — rather than
    /// the number, which any value would satisfy.
    /// </remarks>
    [Fact]
    public void TheFirstPageEndOffsetCoversTheFirstPagesObjects()
    {
        byte[] pdf = Linearize(FivePagePdf());
        int end = Parameter(pdf, "E");
        int firstPage = Parameter(pdf, "O");

        Assert.True(EndOfObject(pdf, firstPage) <= end,
            "the first page's own object ends after /E");

        // Its content stream too — the page is not drawable without it.
        string body = ObjectBody(pdf, firstPage);
        Match contents = Regex.Match(body, @"/Contents (\d+) 0 R");
        Assert.True(contents.Success, "the first page has no /Contents");

        int contentObj = int.Parse(contents.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(EndOfObject(pdf, contentObj) <= end,
            "the first page's content stream ends after /E");
    }

    /// <summary>
    /// The first page's objects physically precede every later page's — the ordering that is the
    /// point of the exercise.
    /// </summary>
    [Fact]
    public void TheFirstPagesObjectsComeBeforeEveryLaterPagesObjects()
    {
        byte[] pdf = Linearize(FivePagePdf());

        int page1 = IndexOf(pdf, "(Page 1)");
        int page2 = IndexOf(pdf, "(Page 2)");
        int page5 = IndexOf(pdf, "(Page 5)");

        Assert.True(page1 > 0 && page2 > page1 && page5 > page2,
            $"pages are not in file order: 1 at {page1}, 2 at {page2}, 5 at {page5}");
    }

    // ================================================================== part 3

    /// <summary>
    /// Part 3 is a cross-reference table near the front of the file, in one subsection, with no free
    /// entries.
    /// </summary>
    /// <remarks>
    /// <para>F.3.4 requires all three properties together: "a valid cross-reference table as defined
    /// in 7.5.4 … although its position in the file shall not be at the end of the file. It shall
    /// consist of a single cross-reference subsection that has no free entries."</para>
    /// <para><b>"No free entries" is what forces the subsection to start above object 0.</b> A
    /// classic table conventionally opens with object 0's free entry; part 3 does not, so it opens at
    /// the linearization dictionary's own object number — which is only possible because the front
    /// matter was given a contiguous run of the <i>high</i> numbers.</para>
    /// </remarks>
    [Fact]
    public void TheFirstPageCrossReferenceTableIsOneSubsectionWithNoFreeEntriesNearTheFront()
    {
        byte[] pdf = Linearize(FivePagePdf());
        string text = Ascii(pdf);

        List<int> tables = XrefOffsets(text);
        Assert.Equal(2, tables.Count);
        Assert.True(tables[0] < pdf.Length / 2,
            $"part 3 is at {tables[0]} of {pdf.Length}, which is not near the front");

        Match header = Regex.Match(text.Substring(tables[0]), @"^xref\n(\d+) (\d+)\n");
        Assert.True(header.Success, "part 3 has no subsection header");

        int first = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        int count = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.True(first > 0, "part 3's subsection starts at object 0, so it has a free entry");

        string entries = text.Substring(tables[0] + header.Length, count * 20);
        Assert.DoesNotContain(" f ", entries, StringComparison.Ordinal);

        // One subsection: nothing but entries between the header and the trailer.
        Assert.StartsWith("trailer", text.Substring(tables[0] + header.Length + count * 20),
            StringComparison.Ordinal);
    }

    /// <summary>Part 3's entries give the real offset of every object they name.</summary>
    /// <remarks>
    /// The entries are written before the hint stream's length is known and patched afterwards, so
    /// an off-by-one in the patching would move every one of them. Nothing else in the file would
    /// notice: our own parser reads the document through this table and would simply fail, and a
    /// reader that fell back to the main table would succeed — which is the shape of a defect that
    /// only shows up in a browser.
    /// </remarks>
    [Fact]
    public void TheFirstPageCrossReferenceEntriesPointAtTheObjectsTheyName()
    {
        byte[] pdf = Linearize(FivePagePdf());
        string text = Ascii(pdf);
        var starts = ObjectStarts(pdf);

        int table = XrefOffsets(text)[0];
        Match header = Regex.Match(text.Substring(table), @"^xref\n(\d+) (\d+)\n");
        int first = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        int count = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);

        for (int i = 0; i < count; i++)
        {
            int at = table + header.Length + i * 20;
            long offset = long.Parse(text.Substring(at, 10), CultureInfo.InvariantCulture);

            Assert.True(starts.ContainsKey(first + i),
                $"part 3 names object {first + i}, which is not in the file");
            Assert.Equal(starts[first + i], offset);
        }
    }

    /// <summary>
    /// The file's own <c>startxref</c> names part 3, and part 3's trailer names the main table.
    /// </summary>
    /// <remarks>
    /// F.3.11: "The startxref line shall give the offset of the first-page cross-reference table. The
    /// Prev entry of the first-page trailer shall give the offset of the main cross-reference table."
    /// Reversing the two produces a file that still opens — every reader follows <c>/Prev</c> — while
    /// costing exactly the fetch linearization exists to save.
    /// </remarks>
    [Fact]
    public void TheCrossReferenceChainStartsAtPartThreeAndLeadsToTheMainTable()
    {
        byte[] pdf = Linearize(FivePagePdf());
        string text = Ascii(pdf);
        List<int> tables = XrefOffsets(text);

        Match last = Regex.Matches(text, @"startxref\n(\d+)\n%%EOF").Cast<Match>().Last();
        Assert.Equal(tables[0], int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture));

        Match prev = Regex.Match(text, @"/Prev (\d+)");
        Assert.True(prev.Success, "the first-page trailer has no /Prev");
        Assert.Equal(tables[1], int.Parse(prev.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The whole file is one revision, and the <c>startxref 0</c> that ends the first-page trailer is
    /// why that has to be asserted.
    /// </summary>
    /// <remarks>
    /// <c>PdfRevisions</c> finds revision boundaries physically, and a linearized file carries a
    /// literal <c>startxref 0 %%EOF</c> a few hundred bytes in. Reading it as a boundary would make
    /// <c>Extract(0)</c> hand back a truncated file. The rule that saves it — an offset of 0 names
    /// the header, which is never a cross-reference section — was until now only tested against a
    /// hand-built prefix; this is the real output.
    /// </remarks>
    [Fact]
    public void TheLinearizedOutputIsStillOneRevision()
    {
        byte[] pdf = Linearize(FivePagePdf());
        Assert.Contains("startxref\n0\n%%EOF", Ascii(pdf), StringComparison.Ordinal);

        using var input = new MemoryStream(pdf);
        var revision = Assert.Single(PdfRevisions.List(input));
        Assert.Equal(pdf.Length, revision.Length);
    }

    // ================================================================== part 5, the hint stream

    /// <summary><c>/H</c> bounds a real stream object rather than naming offset 0.</summary>
    /// <remarks>
    /// Table F.1 makes <c>/H</c> required: "offset1 shall be the offset of the primary hint stream
    /// from the beginning of the file". The previous output wrote <c>/H [0 0]</c> — a stream of no
    /// bytes at the position of <c>%PDF-1.4</c> — and an independent checker's diagnosis of it was blunt: "object has
    /// offset 0", "hint table is not a stream".
    /// </remarks>
    [Fact]
    public void TheHintStreamArrayBoundsARealStreamObject()
    {
        byte[] pdf = Linearize(FivePagePdf());
        PdfLinearizationInfo info = Info(pdf);

        Assert.True(info.HintStreamOffset > 0 && info.HintStreamLength > 0);

        string text = Ascii(pdf);
        Assert.Matches(@"^\d+ 0 obj", text.Substring((int)info.HintStreamOffset));

        int end = (int)(info.HintStreamOffset + info.HintStreamLength);
        Assert.EndsWith("endobj\n",
            text.Substring((int)info.HintStreamOffset, (int)info.HintStreamLength),
            StringComparison.Ordinal);
        Assert.True(end <= pdf.Length);
    }

    /// <summary>
    /// The page offset hint table's positions are written as if the hint stream were not in the
    /// file, and are wrong read literally.
    /// </summary>
    /// <remarks>
    /// <para>F.4 NOTE 3: "a position greater than the hint stream offset shall have the hint stream
    /// length added to it to determine the actual offset relative to the beginning of the file",
    /// because "the length of the primary hint stream depends on the information contained within
    /// the hint tables, which is not known until after they have been generated".</para>
    /// <para><b>A writer that ignored the rule and stored real offsets would satisfy every other
    /// test here</b> — the numbers would be self-consistent, and a reader applying the rule would
    /// land <c>/H</c>'s length past the end of every page. That is precisely the change a round trip
    /// cannot see, and the equality below is what sees it: the reader applies the correction, so an
    /// uncorrected file lands one hint-stream length too far.</para>
    /// </remarks>
    [Fact]
    public void HintTablePositionsAreWrittenAsIfTheHintStreamWereAbsent()
    {
        byte[] pdf = Linearize(FivePagePdf());
        PdfLinearizationHints hints = Hints(pdf);
        PdfLinearizationInfo info = Info(pdf);
        var starts = ObjectStarts(pdf);

        int firstPageObject = Parameter(pdf, "O");

        Assert.Equal(starts[firstPageObject], hints.Pages[0].Offset);

        // And the correction was not a no-op on this fixture: the first page sits past the hint
        // stream, so the stored position and the real one differ by the stream's length.
        Assert.True(hints.Pages[0].Offset > info.HintStreamOffset,
            "the first page precedes the hint stream here, so this fixture cannot show the rule");
        Assert.True(info.HintStreamLength > 0);
    }

    /// <summary>
    /// The page offset hint table locates every page: accumulating its lengths from the first page's
    /// offset lands on each page object in turn.
    /// </summary>
    /// <remarks>
    /// Table F.4 item 2 defines the mechanism — "the locations of subsequent pages shall be
    /// determined by accumulating the lengths of all previous pages" — and this performs it, which
    /// is the only way to find out whether the lengths are right. Asserting each length against the
    /// bytes one page occupies would pass on a table whose <i>order</i> was wrong.
    /// </remarks>
    [Fact]
    public void AccumulatingThePageLengthsLandsOnEveryPageObject()
    {
        byte[] pdf = Linearize(RichPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var starts = ObjectStarts(pdf);
        List<int> pageObjects = PageObjectsInPageOrder(pdf);

        Assert.Equal(RichPageCount, hints.Pages.Count);

        long at = hints.Pages[0].Offset;
        for (int i = 0; i < RichPageCount; i++)
        {
            Assert.Equal(starts[pageObjects[i]], at);
            at += hints.Pages[i].Length;
        }
    }

    /// <summary>Each page's declared object count is the number of objects it actually occupies.</summary>
    /// <remarks>
    /// Table F.4 item 1 makes the counts the way a reader derives object <i>numbers</i>: page 2's
    /// first object is object 1 and every later page accumulates. A count that is right about the
    /// bytes and wrong about the objects sends a reader to the wrong object number for every page
    /// after the mistake.
    /// </remarks>
    [Fact]
    public void EachPagesObjectCountMatchesTheObjectsItOccupies()
    {
        byte[] pdf = Linearize(RichPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var starts = ObjectStarts(pdf);
        List<int> pageObjects = PageObjectsInPageOrder(pdf);

        for (int i = 0; i < RichPageCount; i++)
        {
            long from = starts[pageObjects[i]];
            long to = from + hints.Pages[i].Length;
            int actual = starts.Values.Count(o => o >= from && o < to);

            Assert.Equal(actual, hints.Pages[i].ObjectCount);
        }

        // Table F.4 item 1's numbering rule, performed: page 2's first object is object 1.
        int number = 1;
        for (int i = 1; i < RichPageCount; i++)
        {
            Assert.Equal(number, pageObjects[i]);
            number += hints.Pages[i].ObjectCount;
        }
    }

    /// <summary>
    /// The shared object hint table has one entry per first-page object, then one per shared object,
    /// and every length is the object's real length.
    /// </summary>
    /// <remarks>
    /// F.4.2: "All objects associated with the first page (part 6) shall have entries in the shared
    /// object hint table, regardless of whether they are actually shared", and the two sequences run
    /// first-page entries then shared-section entries. The counts and the boundary between them are
    /// what make a later page's shared-object identifier resolvable at all.
    /// </remarks>
    [Fact]
    public void TheSharedObjectHintTableCoversTheFirstPageThenTheSharedSection()
    {
        byte[] pdf = Linearize(RichPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        Assert.Equal(hints.Pages[0].ObjectCount, hints.SharedObjectsInFirstPage);
        Assert.True(hints.SharedObjects.Count > hints.SharedObjectsInFirstPage,
            "this fixture shares an image between two later pages, so part 8 is not empty");

        // The first-page entries, in file order from /O.
        int firstPageObject = Parameter(pdf, "O");
        for (int i = 0; i < hints.SharedObjectsInFirstPage; i++)
        {
            int objNum = firstPageObject + i;
            Assert.Equal(runs[objNum].End - runs[objNum].Start, hints.SharedObjects[i].Length);
            Assert.Equal(1, hints.SharedObjects[i].ObjectCount);
            Assert.False(hints.SharedObjects[i].HasSignature);
        }

        // The shared section's own entries start where the header says they do.
        Assert.True(hints.FirstSharedObjectNumber > 0);
        Assert.Equal(runs[hints.FirstSharedObjectNumber].Start, hints.FirstSharedObjectOffset);
    }

    /// <summary>
    /// A later page's shared-object identifiers index the shared object hint table, and the first
    /// page declares none.
    /// </summary>
    /// <remarks>
    /// Table F.4 items 3 and 4: "For the first page, this number shall be 0", and an identifier is
    /// "an index into the shared object hint table … shared object identifiers shall not be directly
    /// related to object numbers". An identifier read as an object number is the plausible mistake,
    /// and on a small file it usually still indexes <i>something</i>.
    /// </remarks>
    [Fact]
    public void LaterPagesIdentifyTheirSharedObjectsByTableIndex()
    {
        byte[] pdf = Linearize(RichPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        Assert.Empty(hints.Pages[0].SharedObjectIdentifiers);

        // The font is in part 6 and used by every page, so every later page must name its index.
        int firstPageObject = Parameter(pdf, "O");
        int fontIndex = -1;
        for (int i = 0; i < hints.SharedObjectsInFirstPage; i++)
            if (ObjectBody(pdf, firstPageObject + i).Contains("/Type /Font", StringComparison.Ordinal))
                fontIndex = i;

        Assert.True(fontIndex >= 0, "the fixture's first page reaches a font object");

        for (int i = 1; i < RichPageCount; i++)
        {
            Assert.All(hints.Pages[i].SharedObjectIdentifiers,
                id => Assert.InRange(id, 0, hints.SharedObjects.Count - 1));
            Assert.Contains(fontIndex, hints.Pages[i].SharedObjectIdentifiers);
        }

        // And the shared image, which lives in part 8, is named by the two pages that draw it.
        int imageIndex = hints.SharedObjects.Count - 1;
        int imageObject = hints.FirstSharedObjectNumber;
        Assert.Contains("/Subtype /Image", ObjectBody(pdf, imageObject), StringComparison.Ordinal);
        Assert.Equal(runs[imageObject].End - runs[imageObject].Start,
                     hints.SharedObjects[imageIndex].Length);
    }

    /// <summary>
    /// Every item group is padded to a byte boundary, on a fixture whose numbers make the pad
    /// observable.
    /// </summary>
    /// <remarks>
    /// <para><b>Annex F never mentions the padding</b> — Tables F.3 and F.4 give bit widths and an
    /// interleaving order and stop there — but an independent checker writes a pad after every group of like items and
    /// its reader skips to the next byte after every group. Its own tables only decode field for
    /// field, and only add up to the lengths it declares, with the pads in place.</para>
    ///
    /// <para><b>The fixture is chosen, not incidental.</b> These five pages need seven bits each for
    /// the page-length delta: thirty-five bits, which is not a whole number of bytes, so dropping the
    /// pad moves every group after it. The richer fixture happens to need eight bits, where the pad
    /// is a no-op — a mutation that deleted it survived against that fixture and is killed only by
    /// this one. Two tests reading the same field can differ entirely in what they can detect.</para>
    ///
    /// <para><b>The reader enforces the end positions, so a successful read is the assertion.</b>
    /// <c>PdfLinearizationHintReader</c> refuses a stream whose page table does not finish exactly
    /// at <c>/S</c> and whose shared table does not finish at the next table or the end — the only
    /// structural check a format with no checksum allows. A stream written without the pads is
    /// shorter, decodes to plausible values, and fails that check.</para>
    /// </remarks>
    [Fact]
    public void EveryItemGroupIsPaddedToAByteBoundary()
    {
        byte[] pdf = Linearize(FivePagePdf());
        PdfLinearizationHints hints = Hints(pdf);

        // And the values on the far side of the pad still mean what they say: this fixture's font is
        // the first page's last object, and every later page names its index.
        int fontIndex = hints.SharedObjectsInFirstPage - 1;
        Assert.Contains("/Type /Font",
            ObjectBody(pdf, Parameter(pdf, "O") + fontIndex), StringComparison.Ordinal);

        for (int i = 1; i < 5; i++)
            Assert.Equal(new[] { fontIndex }, hints.Pages[i].SharedObjectIdentifiers);
    }

    /// <summary>The tables end exactly on the byte boundaries their own offsets declare.</summary>
    /// <remarks>
    /// <para><b>This is the property that catches a missing byte pad.</b> Annex F gives bit widths
    /// and an interleaving order and never mentions padding, but an independent checker writes and requires a pad
    /// after every group of like items. A stream written without them is shorter, decodes to
    /// plausible values and disagrees with its own <c>/S</c>.</para>
    ///
    /// <para><b>It is checked inside the reader rather than here</b>, because it is the only
    /// structural check the format allows and every consumer needs it — a decoder that misreads the
    /// layout otherwise returns numbers with nothing to contradict them. So this test asserts the
    /// reader <i>accepts</i> a file with all three tables, which it cannot do unless the page table
    /// lands on <c>/S</c>, the shared table lands on <c>/O</c>, and the generic table lands on the
    /// end of the stream.</para>
    /// </remarks>
    [Fact]
    public void TheTablesEndOnTheByteBoundariesTheirOffsetsDeclare()
    {
        byte[] pdf = Linearize(RichPdf());
        PdfLinearizationHints hints = Hints(pdf);

        // All three tables were present and consistent, which is what the read having succeeded
        // means — the third one is only reached when the second lands exactly on it.
        Assert.NotNull(hints.Outline);
        Assert.NotEmpty(hints.SharedObjects);
        Assert.Equal(RichPageCount, hints.Pages.Count);
    }

    /// <summary>
    /// The outline hint table names the outline hierarchy's own contiguous run of objects.
    /// </summary>
    /// <remarks>
    /// Table F.2 lists <c>/O</c> as "Present only if a document outline exists", and an independent checker enforces
    /// the converse: given an outline and no table it reports "incorrect object count in outline
    /// hint table". Table F.9's four fields describe "a single group of objects that are located
    /// together in the PDF file", which is only true because part 9 places the hierarchy as one run.
    /// </remarks>
    [Fact]
    public void TheOutlineHintTableNamesTheOutlineHierarchysRun()
    {
        byte[] pdf = Linearize(RichPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        Assert.NotNull(hints.Outline);
        PdfGenericHint outline = hints.Outline!;

        Assert.Contains("/Type /Outlines", ObjectBody(pdf, outline.FirstObjectNumber),
            StringComparison.Ordinal);
        Assert.Equal(runs[outline.FirstObjectNumber].Start, outline.Offset);

        long length = 0;
        for (int i = 0; i < outline.ObjectCount; i++)
        {
            int objNum = outline.FirstObjectNumber + i;
            Assert.True(runs.ContainsKey(objNum), $"the outline group names object {objNum}");
            length += runs[objNum].End - runs[objNum].Start;
        }

        Assert.Equal(length, outline.Length);
        Assert.Equal(runs[outline.FirstObjectNumber].Start + length,
                     runs[outline.FirstObjectNumber + outline.ObjectCount - 1].End);
    }

    /// <summary>
    /// F.3.7: under <c>/PageMode /UseOutlines</c> the outline hierarchy opens with the first page — the
    /// bookmarks the document asks to show are inside <c>/E</c>, after the page's own objects — and the
    /// outline hint table names that run. Under any other page mode the hierarchy is in part 9, past
    /// <c>/E</c>, as the control shows.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOutlineOpensWithTheFirstPageWhenThePageModeIsUseOutlines(bool useOutlines)
    {
        byte[] pdf = Linearize(RichPdf(useOutlines));
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);
        int end = Parameter(pdf, "E");
        int firstPage = Parameter(pdf, "O");

        Assert.NotNull(hints.Outline);
        PdfGenericHint outline = hints.Outline!;
        Assert.Contains("/Type /Outlines", ObjectBody(pdf, outline.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Equal(runs[outline.FirstObjectNumber].Start, outline.Offset);
        long length = 0;
        for (int i = 0; i < outline.ObjectCount; i++)
            length += runs[outline.FirstObjectNumber + i].End - runs[outline.FirstObjectNumber + i].Start;
        Assert.Equal(length, outline.Length);

        if (useOutlines)
        {
            Assert.True(runs[outline.FirstObjectNumber].Start > runs[firstPage].Start, "the outline follows the first page's object");
            Assert.True(EndOfObject(pdf, outline.FirstObjectNumber + outline.ObjectCount - 1) <= end,
                "the outline hierarchy ends after /E");
        }
        else
        {
            Assert.True(runs[outline.FirstObjectNumber].Start >= end, "control: the outline is in part 9, past /E");
        }
    }

    /// <summary>
    /// The first-page trailer (part 3) is a classic trailer whatever the input's newest section was:
    /// the parser hands a cross-reference stream's own dictionary over as the trailer, and a copy of it
    /// put <c>/Type /XRef</c>, <c>/W</c>, <c>/Filter</c> and <c>/Length</c> in front of a classic
    /// <c>xref</c> table — a combination §7.5.8 does not permit. Only §7.5.5's keys are copied.
    /// </summary>
    [Fact]
    public void TheFirstPageTrailerCarriesNoCrossReferenceStreamKeys()
    {
        byte[] pdf = Linearize(PdfWithACrossReferenceStream());
        string text = Ascii(pdf);

        int trailer = text.IndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailer > 0, "the output has no classic trailer");
        int open = text.IndexOf("<<", trailer, StringComparison.Ordinal);
        int close = text.IndexOf(">>", open, StringComparison.Ordinal);
        string dictionary = text.Substring(open, close + 2 - open);

        Assert.DoesNotContain("/XRef", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("/W ", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("/Filter", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("/Length", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("/Index", dictionary, StringComparison.Ordinal);
        // Potency: the trailer is read from an input whose only section is a stream, and it keeps what §7.5.5 lists.
        Assert.Contains("/Type /XRef", Ascii(PdfWithACrossReferenceStream()), StringComparison.Ordinal);
        Assert.Contains("/Size", dictionary, StringComparison.Ordinal);
        Assert.Contains("/Root", dictionary, StringComparison.Ordinal);
        Assert.Contains("/Info", dictionary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each document-level category Annex F requires a generic hint table for — the article threads'
    /// information, the named destinations (the catalog's <c>/Dests</c> and the <c>/Names</c> tree), the
    /// information dictionary, the page labels — is one run of part 9, and its table names that run:
    /// the first object, where it starts, how many, how long.
    /// </summary>
    [Fact]
    public void EachGenericHintTableNamesItsCategorysRun()
    {
        byte[] pdf = Linearize(DocumentLevelPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        AssertTheRun(pdf, runs, hints.Outline, "/Type /Outlines", 2);
        AssertTheRun(pdf, runs, hints.Threads, "(A thread)", 1);
        AssertTheRun(pdf, runs, hints.NamedDestinations, "/Chap1", 3);   // /Dests, then the tree's leaf and its destination
        AssertTheRun(pdf, runs, hints.Information, "/Producer", 2);
        AssertTheRun(pdf, runs, hints.PageLabels, "/Nums", 2);
    }

    /// <summary>
    /// The logical structure hint table is Table F.10's extended form: it names the structure tree's run and
    /// lists the shared object groups its objects reference — here an image two pages share, which a
    /// structure element's marked-content reference names as its stream.
    /// </summary>
    [Fact]
    public void TheLogicalStructureTableListsTheSharedObjectsItReferences()
    {
        byte[] pdf = Linearize(TaggedPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        AssertTheRun(pdf, runs, hints.LogicalStructure, "/StructTreeRoot", 2);
        int id = Assert.Single(hints.LogicalStructure!.SharedObjectIdentifiers);
        Assert.True(id >= hints.SharedObjectsInFirstPage, "the image lives in the shared section, after the first page's entries");
        int objectNumber = hints.FirstSharedObjectNumber + (id - hints.SharedObjectsInFirstPage);
        Assert.Contains("/Subtype /Image", ObjectBody(pdf, objectNumber), StringComparison.Ordinal);

        Assert.Null(Hints(Linearize(RichPdf())).LogicalStructure);   // untagged: no table
    }

    /// <summary>
    /// The renditions name tree hint table (Table F.2's <c>/R</c>, F.4.6's extended form) names the tree's run
    /// — the tree and the rendition it holds — with no shared references here; a document without the tree
    /// has none.
    /// </summary>
    [Fact]
    public void TheRenditionsTableNamesTheNameTreesRun()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names 300 0 R >>",
            PageTreePlaceholder,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var kids = new List<string>();
        int next = 4;
        for (int page = 1; page <= 2; page++)
        {
            kids.Add($"{next} 0 R");
            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {next + 1} 0 R >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count 2 " +
                    "/MediaBox [0 0 300 400] /Resources << /Font << /F1 3 0 R >> >> >>";
        var extra = new SortedDictionary<int, string>
        {
            [300] = "<< /Renditions 301 0 R >>",
            [301] = "<< /Names [(clip) 302 0 R] >>",
            [302] = "<< /Type /Rendition /S /MR /N (clip) >>",
        };
        byte[] pdf = Linearize(Assemble(bodies, "/Size {0} /Root 1 0 R", extra));
        PdfLinearizationHints hints = Hints(pdf);

        AssertTheRun(pdf, ObjectRuns(pdf), hints.Renditions, "/Names [(clip)", 2);
        Assert.Empty(hints.Renditions!.SharedObjectIdentifiers);
        Assert.Null(Hints(Linearize(RichPdf())).Renditions);
    }

    /// <summary>
    /// The interactive form hint table (Table F.2 <c>/V</c>, an extended generic table, F.4.6): the objects
    /// <c>/AcroForm</c> reaches — the form dictionary, its field's widget and the <c>/DR</c> font — are one run
    /// of part 4, written once each; the page's own font, which the form does not reach, is not, nor the
    /// viewer preferences, which open with the document too. The form dictionary refers back to the
    /// catalogue (a producer's private key): the catalogue is not in the run, and neither is the outline it
    /// reaches, which stays in part 9 with its own table.
    /// </summary>
    [Fact]
    public void TheInteractiveFormTableNamesTheObjectsTheAcroFormReaches()
    {
        byte[] pdf = Linearize(Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 10 0 R /ViewerPreferences 11 0 R /Outlines 12 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 4 0 R /Annots [5 0 R] "
                + "/Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Length 32 >>\nstream\nBT /F1 24 Tf 40 300 Td (A) Tj ET\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [40 40 200 60] /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 9 0 R "
                + "/Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Length 32 >>\nstream\nBT /F1 24 Tf 40 300 Td (B) Tj ET\nendstream",
            "<< /Fields [5 0 R] /DR << /Font << /Helv 6 0 R >> >> /DA (/Helv 0 Tf 0 g) /Origin 1 0 R >>",
            "<< /DisplayDocTitle true >>",
            "<< /Type /Outlines /Count 0 >>",
        }, "/Size {0} /Root 1 0 R"));
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        AssertTheRun(pdf, runs, hints.InteractiveForm, "/Fields [", 3);
        var numbers = Enumerable.Range(hints.InteractiveForm!.FirstObjectNumber, 3).ToList();
        var bodies = numbers.Select(n => ObjectBody(pdf, n)).ToList();
        Assert.Contains(bodies, b => b.Contains("/Widget", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.Contains("/Helvetica", StringComparison.Ordinal));
        Assert.DoesNotContain(bodies, b => b.Contains("/Courier", StringComparison.Ordinal));
        Assert.DoesNotContain(bodies, b => b.Contains("/Catalog", StringComparison.Ordinal));
        Assert.DoesNotContain(bodies, b => b.Contains("/DisplayDocTitle", StringComparison.Ordinal));
        string text = Ascii(pdf);
        foreach (int n in numbers)
            Assert.Single(Regex.Matches(text, $"(?<![0-9]){n} 0 obj"));
        Assert.NotNull(hints.Outline);
        Assert.Contains("/Outlines", ObjectBody(pdf, hints.Outline!.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Null(Hints(Linearize(RichPdf())).InteractiveForm);
    }

    /// <summary>
    /// The embedded file stream hint table (Tables F.11 and F.12, <c>/B</c>): one group per embedded file
    /// stream, each starting at its stream, located by the first group's offset and the lengths before it,
    /// with the shared groups its objects reference — here an image pages 2 and 3 share, which the first
    /// file's stream dictionary names.
    /// </summary>
    [Fact]
    public void TheEmbeddedFileTableNamesEachStreamsGroup()
    {
        byte[] pdf = Linearize(EmbeddedFilesPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        Assert.NotNull(hints.EmbeddedFiles);
        Assert.Equal(2, hints.EmbeddedFiles!.Count);
        PdfGenericHint first = hints.EmbeddedFiles[0], second = hints.EmbeddedFiles[1];
        Assert.Contains("/Type /EmbeddedFile", ObjectBody(pdf, first.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Contains("hello", ObjectBody(pdf, first.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Contains("world!", ObjectBody(pdf, second.FirstObjectNumber), StringComparison.Ordinal);
        foreach (PdfGenericHint group in hints.EmbeddedFiles)
        {
            Assert.Equal(1, group.ObjectCount);
            Assert.Equal(runs[group.FirstObjectNumber].Start, group.Offset);
            Assert.Equal(runs[group.FirstObjectNumber].End - runs[group.FirstObjectNumber].Start, group.Length);
        }
        Assert.Equal(first.Offset + first.Length, second.Offset);   // adjacent: the second is located from the first

        int id = Assert.Single(first.SharedObjectIdentifiers);
        int objectNumber = hints.FirstSharedObjectNumber + (id - hints.SharedObjectsInFirstPage);
        Assert.Contains("/Subtype /Image", ObjectBody(pdf, objectNumber), StringComparison.Ordinal);
        Assert.Empty(second.SharedObjectIdentifiers);

        Assert.Null(Hints(Linearize(RichPdf())).EmbeddedFiles);
    }

    /// <summary>
    /// The first page's attachment is in the embedded file table (F.4.7: it locates "all byte ranges ... needed
    /// to access its embedded file streams"): the first page's section holds the stream (F.3.7), and the table
    /// names it by an entry of no objects whose one shared reference is the stream's own first-page identifier.
    /// It used to be left out. A later page's attachment is not named, and stays in that page's section: the
    /// table can locate a group only in part 9 or through the shared object table, which lists no later page's
    /// objects, and an independent checker counts the stream in its page's section.
    /// </summary>
    [Fact]
    public void TheFirstPagesAttachment_IsInTheEmbeddedFileTable()
    {
        byte[] pdf = Linearize(AttachmentsPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        // The document's own file, a group of its own in part 9, first — the group the header locates —
        // and the first page's after it.
        Assert.NotNull(hints.EmbeddedFiles);
        Assert.Equal(2, hints.EmbeddedFiles!.Count);
        PdfGenericHint document = hints.EmbeddedFiles[0], first = hints.EmbeddedFiles[1];
        Assert.Contains("DOCUMENTFILE", ObjectBody(pdf, document.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Equal(1, document.ObjectCount);
        Assert.Equal(runs[document.FirstObjectNumber].Start, document.Offset);
        Assert.Contains("FIRSTFILE", ObjectBody(pdf, first.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Equal(0, first.ObjectCount);
        Assert.Equal(0, first.Length);
        int id = Assert.Single(first.SharedObjectIdentifiers);
        Assert.InRange(id, 0, hints.SharedObjectsInFirstPage - 1);
        PdfPageHint page1 = hints.Pages[0];
        Assert.InRange(runs[first.FirstObjectNumber].Start, page1.Offset, page1.Offset + page1.Length - 1);
        // The identifier names the stream itself: the first page's groups lie one after another from the
        // page's offset, so the group it names starts where the stream does. A range alone could not see
        // the identifier written in too few bits — the sixth object's 5 read back as 1.
        long named = page1.Offset;
        for (int k = 0; k < id; k++) named += hints.SharedObjects[k].Length;
        Assert.Equal(runs[first.FirstObjectNumber].Start, named);

        // The second page's stream: in that page's section, which counts it.
        int secondFile = runs.Keys.Single(n => ObjectBody(pdf, n).Contains("SECONDFILE", StringComparison.Ordinal));
        PdfPageHint page2 = hints.Pages[1];
        Assert.InRange(runs[secondFile].Start, page2.Offset, page2.Offset + page2.Length - 1);
    }

    /// <summary>Two pages, each with a file attachment annotation whose file specification embeds a file; and a document-level embedded file.</summary>
    private static byte[] AttachmentsPdf()
    {
        var extra = new SortedDictionary<int, string>
        {
            [300] = "<< /EmbeddedFiles 301 0 R >>",
            [301] = "<< /Names [(doc.txt) 302 0 R] >>",
            [302] = "<< /Type /Filespec /F (doc.txt) /EF << /F 303 0 R >> >>",
            [303] = "<< /Type /EmbeddedFile /Length 12 >>\nstream\nDOCUMENTFILE\nendstream",
        };
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names 300 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R /Annots [8 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 7 0 R /Annots [11 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length 33 >>\nstream\nBT /F1 24 Tf 40 300 Td (One) Tj ET\nendstream",
            "<< /Length 33 >>\nstream\nBT /F1 24 Tf 40 300 Td (Two) Tj ET\nendstream",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /FS 9 0 R >>",
            "<< /Type /Filespec /F (first.txt) /EF << /F 10 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 9 >>\nstream\nFIRSTFILE\nendstream",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /FS 12 0 R >>",
            "<< /Type /Filespec /F (second.txt) /EF << /F 13 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 10 >>\nstream\nSECONDFILE\nendstream",
        };
        return Assemble(bodies, "/Size {0} /Root 1 0 R", extra);
    }

    /// <summary>
    /// The thumbnail hint table (Tables F.7 and F.8, <c>/T</c>): an entry for each page with a thumbnail, in page
    /// order, the pages without one between entries counted; each group the image and what only it references,
    /// adjacent, its first object the image; what the thumbnails share and nothing else does as the thumbnail
    /// shared objects section.
    /// </summary>
    [Fact]
    public void TheThumbnailTableNamesEachPagesGroupAndTheirSharedObjects()
    {
        byte[] pdf = Linearize(ThumbnailPdf());
        PdfLinearizationHints hints = Hints(pdf);
        var runs = ObjectRuns(pdf);

        Assert.NotNull(hints.Thumbnails);
        Assert.Equal(new[] { 0, 2 }, hints.Thumbnails!.Select(t => t.PageIndex));
        Assert.Equal(new[] { 1, 2 }, hints.Thumbnails.Select(t => t.ObjectCount));
        Assert.Contains("(thumb one)", ObjectBody(pdf, hints.Thumbnails[0].FirstObjectNumber), StringComparison.Ordinal);
        Assert.Contains("(thumb three)", ObjectBody(pdf, hints.Thumbnails[1].FirstObjectNumber), StringComparison.Ordinal);
        foreach (PdfThumbnailHint thumb in hints.Thumbnails)
        {
            Assert.Equal(runs[thumb.FirstObjectNumber].Start, thumb.Offset);
            long length = 0;
            for (int i = 0; i < thumb.ObjectCount; i++)
                length += runs[thumb.FirstObjectNumber + i].End - runs[thumb.FirstObjectNumber + i].Start;
            Assert.Equal(length, thumb.Length);
        }
        Assert.Equal(hints.Thumbnails[0].Offset + hints.Thumbnails[0].Length, hints.Thumbnails[1].Offset);

        PdfGenericHint shared = hints.ThumbnailSharedObjects!;
        Assert.NotNull(shared);
        Assert.Equal(1, shared.ObjectCount);
        Assert.Contains("/Indexed", ObjectBody(pdf, shared.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Equal(runs[shared.FirstObjectNumber].Start, shared.Offset);
        Assert.Equal(runs[shared.FirstObjectNumber].End - runs[shared.FirstObjectNumber].Start, shared.Length);

        Assert.Null(Hints(Linearize(RichPdf())).Thumbnails);
    }

    /// <summary>
    /// Three pages: the first and third have thumbnails sharing a colour space nothing else uses, the third's
    /// with a soft mask only it references and a name tree the catalog also reaches, which is therefore not
    /// the thumbnail's; the second has none.
    /// </summary>
    private static byte[] ThumbnailPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /JavaScript 404 0 R >> >>",
            PageTreePlaceholder,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var kids = new List<string>();
        int next = 4;
        for (int page = 1; page <= 3; page++)
        {
            kids.Add($"{next} 0 R");
            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            string thumb = page == 1 ? " /Thumb 400 0 R" : page == 3 ? " /Thumb 401 0 R" : "";
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {next + 1} 0 R{thumb} >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count 3 " +
                    "/MediaBox [0 0 300 400] /Resources << /Font << /F1 3 0 R >> >> >>";
        var extra = new SortedDictionary<int, string>
        {
            [400] = "<< /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace 402 0 R /Name (thumb one) /Length 1 >>\nstream\nA\nendstream",
            [401] = "<< /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace 402 0 R /SMask 403 0 R /Y 404 0 R /Name (thumb three) /Length 1 >>\nstream\nB\nendstream",
            [402] = "[/Indexed /DeviceRGB 0 <FF0000>]",
            [403] = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>\nstream\nC\nendstream",
            [404] = "<< /Names [] >>",
        };
        return Assemble(bodies, "/Size {0} /Root 1 0 R", extra);
    }

    /// <summary>Three pages, the second and third sharing an image; two embedded files, the first naming the image.</summary>
    private static byte[] EmbeddedFilesPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names 300 0 R >>",
            PageTreePlaceholder,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Image(0),
        };
        var kids = new List<string>();
        int next = 5;
        for (int page = 1; page <= 3; page++)
        {
            kids.Add($"{next} 0 R");
            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            string resources = "";
            if (page > 1)
            {
                content += " q 10 0 0 10 5 5 cm /Im0 Do Q";
                resources = " /Resources << /Font << /F1 3 0 R >> /XObject << /Im0 4 0 R >> >>";
            }
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {next + 1} 0 R{resources} >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count 3 " +
                    "/MediaBox [0 0 300 400] /Resources << /Font << /F1 3 0 R >> >> >>";
        var extra = new SortedDictionary<int, string>
        {
            [300] = "<< /EmbeddedFiles 301 0 R >>",
            [301] = "<< /Names [(a.txt) 302 0 R (b.txt) 304 0 R] >>",
            [302] = "<< /Type /Filespec /F (a.txt) /EF << /F 303 0 R >> >>",
            [303] = "<< /Type /EmbeddedFile /Thumbnail 4 0 R /Length 5 >>\nstream\nhello\nendstream",
            [304] = "<< /Type /Filespec /F (b.txt) /EF << /F 305 0 R >> >>",
            [305] = "<< /Length 6 >>\nstream\nworld!\nendstream",
        };
        return Assemble(bodies, "/Size {0} /Root 1 0 R", extra);
    }

    /// <summary>Three pages, the second and third sharing an image a structure element refers to by <c>/Stm</c>.</summary>
    private static byte[] TaggedPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 300 0 R /MarkInfo << /Marked true >> >>",
            PageTreePlaceholder,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Image(0),
        };
        var kids = new List<string>();
        int next = 5;
        for (int page = 1; page <= 3; page++)
        {
            kids.Add($"{next} 0 R");
            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            string resources = "";
            if (page > 1)
            {
                content += " q 10 0 0 10 5 5 cm /Im0 Do Q";
                resources = " /Resources << /Font << /F1 3 0 R >> /XObject << /Im0 4 0 R >> >>";
            }
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {next + 1} 0 R{resources} >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count 3 " +
                    "/MediaBox [0 0 300 400] /Resources << /Font << /F1 3 0 R >> >> >>";
        var extra = new SortedDictionary<int, string>
        {
            [300] = "<< /Type /StructTreeRoot /K 301 0 R >>",
            [301] = "<< /Type /StructElem /S /Figure /P 300 0 R /K << /Type /MCR /Pg 7 0 R /Stm 4 0 R /MCID 0 >> >>",
        };
        return Assemble(bodies, "/Size {0} /Root 1 0 R", extra);
    }

    /// <summary>A category the document lacks gets no table: the rich fixture has an outline and an information dictionary and nothing else.</summary>
    [Fact]
    public void ACategoryTheDocumentLacksGetsNoTable()
    {
        PdfLinearizationHints hints = Hints(Linearize(RichPdf()));
        Assert.NotNull(hints.Outline);
        Assert.NotNull(hints.Information);
        Assert.Null(hints.Threads);
        Assert.Null(hints.NamedDestinations);
        Assert.Null(hints.PageLabels);
    }

    private static void AssertTheRun(byte[] pdf, IDictionary<int, (long Start, long End)> runs, PdfGenericHint? hint, string marker, int objects)
    {
        Assert.NotNull(hint);
        Assert.Contains(marker, ObjectBody(pdf, hint!.FirstObjectNumber), StringComparison.Ordinal);
        Assert.Equal(objects, hint.ObjectCount);
        Assert.Equal(runs[hint.FirstObjectNumber].Start, hint.Offset);
        long length = 0;
        for (int i = 0; i < hint.ObjectCount; i++)
        {
            int objNum = hint.FirstObjectNumber + i;
            Assert.True(runs.ContainsKey(objNum), $"the {marker} group names object {objNum}");
            length += runs[objNum].End - runs[objNum].Start;
        }
        Assert.Equal(length, hint.Length);
        Assert.Equal(runs[hint.FirstObjectNumber].Start + length, runs[hint.FirstObjectNumber + hint.ObjectCount - 1].End);
    }

    /// <summary>
    /// Three pages and every generic category: an outline, catalog <c>/Dests</c>, a <c>/Names</c> destinations
    /// tree, page labels, an article thread with an information dictionary, and an information dictionary
    /// with an indirect title.
    /// </summary>
    private static byte[] DocumentLevelPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R /Dests 200 0 R /Names 201 0 R /PageLabels 204 0 R /Threads 206 0 R >>",
            PageTreePlaceholder,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
            "<< /Title (Start) /Parent 5 0 R /Dest [8 0 R /Fit] >>",
            "<< /Producer (fixture) /Title 210 0 R >>",
        };
        var kids = new List<string>();
        int next = 8;
        for (int page = 1; page <= 3; page++)
        {
            kids.Add($"{next} 0 R");
            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {next + 1} 0 R >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count 3 " +
                    "/MediaBox [0 0 300 400] /Resources << /Font << /F1 3 0 R >> >> >>";
        var extra = new SortedDictionary<int, string>
        {
            [200] = "<< /Chap1 [8 0 R /Fit] >>",
            [201] = "<< /Dests 202 0 R >>",
            [202] = "<< /Names [(intro) 203 0 R] >>",
            [203] = "[10 0 R /XYZ 0 0 0]",
            [204] = "<< /Nums [0 << /S /r >> 2 205 0 R] >>",
            [205] = "<< /S /D /P (A-) >>",
            [206] = "[207 0 R]",
            [207] = "<< /Type /Thread /I 208 0 R /F 209 0 R >>",
            [208] = "<< /Title (A thread) >>",
            [209] = "<< /Type /Bead /T 207 0 R /N 209 0 R /V 209 0 R /P 8 0 R /R [0 0 100 100] >>",
            [210] = "(An indirect title)",
        };
        return Assemble(bodies, "/Size {0} /Root 1 0 R /Info 7 0 R", extra);
    }

    /// <summary>A document with no outline gets no outline hint table.</summary>
    /// <remarks>
    /// The other half of "present only if". A table describing an empty group is not the same as an
    /// absent key: a reader that finds <c>/O</c> will follow it. And with no <c>/O</c> the shared
    /// table must finish at the end of the stream — which the reader enforces, so the successful
    /// read below is that assertion.
    /// </remarks>
    [Fact]
    public void NoOutlineMeansNoOutlineHintTable()
    {
        byte[] pdf = Linearize(FivePagePdf());

        Assert.Null(Hints(pdf).Outline);
    }

    // ================================================================== the whole-file rewrite

    /// <summary>
    /// The trailer's <c>/Info</c> is renumbered with everything else, and it used to point at a page.
    /// </summary>
    /// <remarks>
    /// <para><b>Found by an independent checker, not by this suite, and worth the retelling.</b> The trailer was copied
    /// entry by entry and only <c>/Root</c> was replaced, so <c>/Info</c> kept the source document's
    /// object number. In a linearized file the low numbers belong to the <i>later pages</i>, so the
    /// stale reference did not dangle — it resolved, to a page object, and every reader would have
    /// shown that page's dictionary as the document's metadata.</para>
    /// <para>Nothing in the file is malformed as a result, which is why it survived: a parser reads
    /// it, a renderer draws it, and the only symptom is a wrong answer to a question no test was
    /// asking. An independent checker found it by re-deriving the object graph and noticing a page object that was not
    /// private to its page.</para>
    /// </remarks>
    [Fact]
    public void TheTrailerInfoReferenceIsRenumberedWithEverythingElse()
    {
        byte[] pdf = Linearize(RichPdf());

        Match info = Regex.Match(Ascii(pdf), @"/Info (\d+) 0 R");
        Assert.True(info.Success, "the linearized trailer dropped /Info");

        string body = ObjectBody(pdf, int.Parse(info.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Contains("/Producer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /Page", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reference to an object the file does not contain becomes <c>null</c> rather than keeping a
    /// number that now belongs to something else.
    /// </summary>
    /// <remarks>
    /// §7.3.9 already says "an indirect reference to a non-existent object shall be treated as a null
    /// object", so the substitution changes nothing observable — but carrying the number through
    /// would. Whole-file renumbering is what creates the hazard: a number that pointed at nothing in
    /// the input almost always points at something in the output.
    /// </remarks>
    [Fact]
    public void AReferenceToAMissingObjectBecomesNullRatherThanNamingAnotherObject()
    {
        byte[] pdf = Linearize(PdfWithADanglingDestination());
        string text = Ascii(pdf);

        Match dest = Regex.Match(text, @"/Dest \[(\S+) /Fit\]");
        Assert.True(dest.Success, "the outline destination did not survive");
        Assert.Equal("null", dest.Groups[1].Value);
    }

    /// <summary>
    /// Inheritable page attributes are copied onto every page and removed from the page tree node.
    /// </summary>
    /// <remarks>
    /// <para>F.3.7 requires the first page object to "explicitly specify all required attributes,
    /// such as Resources and MediaBox; the attributes may not be inherited from ancestor page tree
    /// nodes", and F.3.10 says the same of every page. Both exist because the page tree is
    /// deliberately in part 9: "the conforming reader never needs to consult it". A page that
    /// inherits its <c>/MediaBox</c> cannot be drawn from the first fetch, which is the one thing
    /// linearization promises.</para>
    /// <para>Removing the ancestor's copy afterwards is an independent checker's requirement rather than the
    /// specification's — it refuses a file that keeps one — and it cannot change what any page
    /// renders once every leaf carries its own.</para>
    /// </remarks>
    [Fact]
    public void InheritedPageAttributesArePushedDownAndTakenOffTheTree()
    {
        byte[] pdf = Linearize(RichPdf());

        foreach (int page in PageObjectsInPageOrder(pdf))
        {
            string body = ObjectBody(pdf, page);
            Assert.Contains("/MediaBox", body, StringComparison.Ordinal);
            Assert.Contains("/Resources", body, StringComparison.Ordinal);
        }

        Match tree = Regex.Match(Ascii(pdf), @"(\d+) 0 obj\n<< /Type /Pages ");
        Assert.True(tree.Success, "no page tree node in the output");

        string treeBody = ObjectBody(pdf, int.Parse(tree.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.DoesNotContain("/MediaBox", treeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("/Resources", treeBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page tree node that does not declare <c>/Type /Pages</c> still does not join the first
    /// page's section.
    /// </summary>
    /// <remarks>
    /// <para><b>Two independent guards stop the first page's closure from swallowing the
    /// document</b>, and only this fixture separates them. A page dictionary carries
    /// <c>/Parent</c>, that node's <c>/Kids</c> names every other page, and every page names its
    /// own content — so an unguarded walk from page 1 reaches everything, part 6 becomes the whole
    /// file, and the ordering that linearization is for buys nothing. F.3.7 says which edges to
    /// refuse: "except page tree nodes or other page objects".</para>
    ///
    /// <para>Normally the node is excluded because it is typed, so refusing the <c>/Parent</c> key
    /// as well changes nothing — deleting that refusal survived every other test here. It is not
    /// redundant, it is a second guard against the same hazard reached a different way: <c>/Type</c>
    /// is not required on a page tree node, real producers omit it, and this document is what the
    /// key-level guard is actually for.</para>
    /// </remarks>
    [Fact]
    public void APageTreeNodeWithoutATypeStillDoesNotJoinTheFirstPageSection()
    {
        byte[] pdf = Linearize(PdfWithAnUntypedPageTree());
        int end = Parameter(pdf, "E");
        var starts = ObjectStarts(pdf);

        Match kids = Regex.Match(Ascii(pdf), @"/Kids \[([^\]]*)\]");
        Assert.True(kids.Success, "no page tree node survived");

        List<int> pages = Regex.Matches(kids.Groups[1].Value, @"(\d+) 0 R")
                               .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                               .ToList();
        Assert.Equal(3, pages.Count);

        Assert.True(starts[pages[0]] < end, "the first page is not in the first-page section");
        for (int i = 1; i < pages.Count; i++)
            Assert.True(starts[pages[i]] >= end,
                $"page {i + 1} is inside the first-page section, so part 6 is the whole document");

        // And the node itself stays in part 9. This is the half the key-level guard actually
        // decides: the other pages are excluded anyway because a page object is never followed, so
        // an unguarded /Parent pulls in the tree node alone — one object, in the section a reader
        // fetches first, that F.3.10 says it "never needs to consult".
        Match node = Regex.Match(Ascii(pdf), @"(?m)^(\d+) 0 obj
<< /Kids \[");
        Assert.True(node.Success, "the page tree node is not where its own body says it is");

        int nodeNum = int.Parse(node.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(starts[nodeNum] >= end,
            "the untyped page tree node was placed in the first-page section");
    }

    /// <summary>
    /// Everything the first page references is in part 3 — the property that makes the first fetch
    /// sufficient.
    /// </summary>
    /// <remarks>
    /// This is what the whole exercise is for, stated directly: a reader that has fetched the front
    /// of the file can resolve every object the first page needs without going back for the main
    /// table. <c>/Parent</c> is the deliberate exception — F.3.7 excludes page tree nodes from the
    /// first-page section, and the pushed-down attributes are why nothing needs to follow it.
    /// </remarks>
    [Fact]
    public void EveryObjectTheFirstPageReferencesIsInTheFirstPageCrossReferenceTable()
    {
        byte[] pdf = Linearize(RichPdf());
        string text = Ascii(pdf);

        int table = XrefOffsets(text)[0];
        Match header = Regex.Match(text.Substring(table), @"^xref\n(\d+) (\d+)\n");
        int first = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        int count = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);

        var reachable = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(Parameter(pdf, "O"));

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (!reachable.Add(current)) continue;

            string body = Regex.Replace(ObjectBody(pdf, current), @"/Parent \d+ 0 R", string.Empty);
            foreach (Match m in Regex.Matches(body, @"(\d+) 0 R"))
                queue.Enqueue(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        foreach (int objNum in reachable)
            Assert.InRange(objNum, first, first + count - 1);
    }

    // ================================================================== part 4's reach

    /// <summary>
    /// Everything a document-level entry reaches opens with the document (part 4), not only its
    /// top-level value: with <c>/AcroForm</c> written directly in the catalogue, its field widget
    /// and its <c>/DR</c> font are placed before the first page — even though the widget is also an
    /// annotation of that page — and the page's own font is not.
    /// </summary>
    /// <remarks>
    /// Taking only an indirect top-level value claimed nothing here, the widget and the font were
    /// counted as first-page and shared objects, and an independent structure check reported both the page-0 object
    /// count and a shared-object entry no page section held. Asserted by file position, because part
    /// 4 precedes part 6 in the file (Annex F, Figure F.1), so this holds without an independent checker installed;
    /// <c>WriterOutputQpdfTests</c> in Base.Tests asks an independent checker itself.
    /// </remarks>
    [Fact]
    public void ObjectsTheDirectAcroFormReaches_OpenWithTheDocument()
    {
        byte[] pdf = Linearize(Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DR << /Font << /Helv 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 4 0 R /Annots [5 0 R] "
                + "/Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Length 32 >>\nstream\nBT /F1 24 Tf 40 300 Td (A) Tj ET\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [40 40 200 60] /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 9 0 R "
                + "/Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Length 32 >>\nstream\nBT /F1 24 Tf 40 300 Td (B) Tj ET\nendstream",
        }, "/Size {0} /Root 1 0 R"));

        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        PdfDictionary page = doc.Pages[0].Dictionary;
        var widget = (PdfReference)((PdfArray)doc.ResolveObject(page[PdfName.Annots]))[0];
        var acroForm = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);
        var dr = (PdfDictionary)doc.ResolveObject(acroForm[PdfName.DR]);
        var drFont = (PdfReference)((PdfDictionary)doc.ResolveObject(dr[PdfName.Font]))[new PdfName("Helv")];
        var pageFont = (PdfReference)((PdfDictionary)doc.ResolveObject(
            ((PdfDictionary)doc.ResolveObject(page[PdfName.Resources]))[PdfName.Font]))[new PdfName("F1")];

        long Offset(int number) => doc.CrossReferenceTable.GetEntry(number).Offset;
        long firstPage = Offset(page.ObjectNumber);

        Assert.True(Offset(widget.ReferenceObjectNumber) < firstPage, "the widget opens with the document");
        Assert.True(Offset(drFont.ReferenceObjectNumber) < firstPage, "the /DR font opens with the document");
        Assert.True(Offset(pageFont.ReferenceObjectNumber) > firstPage, "control: the page's own font is a page object");
    }

    // ================================================================== helpers

    private static byte[] Linearize(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// The hint tables, through the library's own decoder.
    /// </summary>
    /// <remarks>
    /// <para><b>This suite used to carry a second decoder of its own</b>, written from Tables F.3–F.6
    /// and F.9 so that it could not share a mistake with the encoder. That reasoning is intact —
    /// <c>PdfLinearizationHintReader</c> is spec-derived too, and shares no code with
    /// <c>LinearizationHintTables</c> — and the independence that mattered was from the <i>encoder</i>,
    /// never from the library. Keeping two decoders meant a second place for the same bit-level rules
    /// to drift, so the test copy is gone.</para>
    ///
    /// <para><b>The assertions did not weaken.</b> Everything here still relates the tables to facts
    /// measured in the bytes — where each object starts, how long each run is, which object body
    /// carries <c>/Type /Font</c>. What moved into the reader is the layout self-check (a table must
    /// end exactly where the next one's offset says), which every consumer needs and which now makes
    /// a misread loud rather than plausible.</para>
    /// </remarks>
    private static PdfLinearizationHints Hints(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(input, out PdfLinearizationHints hints),
            PdfLinearizationHintReader.LastError);
        return hints;
    }

    /// <summary>Table F.1's parameters, read from the head the way a progressive client would.</summary>
    private static PdfLinearizationInfo Info(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        Assert.True(PdfRevisions.TryReadLinearization(input, out PdfLinearizationInfo info));
        return info;
    }

    private static string Ascii(byte[] pdf) => Encoding.GetEncoding("iso-8859-1").GetString(pdf);

    /// <summary>A <c>/Linearized</c> dictionary entry, read out of the raw bytes.</summary>
    /// <remarks>
    /// Scoped to the dictionary itself: <c>/O</c> and <c>/S</c> are also keys of the hint stream's
    /// dictionary, and <c>/T</c> and <c>/L</c> are short enough to appear anywhere.
    /// </remarks>
    private static int Parameter(byte[] pdf, string name)
    {
        string text = Ascii(pdf);
        int start = text.IndexOf("/Linearized", StringComparison.Ordinal);
        Assert.True(start > 0, "the output has no linearization dictionary");

        int end = text.IndexOf("endobj", start, StringComparison.Ordinal);
        Match m = Regex.Match(text.Substring(start, end - start), $@"/{name} (\d+)");
        Assert.True(m.Success, $"the linearization dictionary has no /{name}");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static int IndexOf(byte[] pdf, string needle)
        => Ascii(pdf).IndexOf(needle, StringComparison.Ordinal);

    private static List<int> XrefOffsets(string text)
    {
        var offsets = new List<int>();
        foreach (Match m in Regex.Matches(text, @"(?m)^xref\n"))
            offsets.Add(m.Index);
        return offsets;
    }

    /// <summary>The text of one indirect object, header excluded.</summary>
    private static string ObjectBody(byte[] pdf, int objNum)
    {
        string text = Ascii(pdf);
        Match m = Regex.Match(text, $@"(?m)^{objNum} 0 obj\b");
        Assert.True(m.Success, $"object {objNum} is not in the file");

        int end = text.IndexOf("endobj", m.Index, StringComparison.Ordinal);
        Assert.True(end > 0, $"object {objNum} has no endobj");
        return text.Substring(m.Index + m.Length, end - m.Index - m.Length);
    }

    /// <summary>The offset just past an object's <c>endobj</c>.</summary>
    private static int EndOfObject(byte[] pdf, int objNum)
    {
        string text = Ascii(pdf);
        Match m = Regex.Match(text, $@"(?m)^{objNum} 0 obj\b");
        Assert.True(m.Success, $"object {objNum} is not in the file");
        return text.IndexOf("endobj", m.Index, StringComparison.Ordinal) + "endobj".Length;
    }

    /// <summary>Object number to the byte offset the object header starts at.</summary>
    /// <remarks>
    /// Found by scanning for a line that begins an indirect object, deliberately not by reading a
    /// cross-reference table — the tables are what is under test, so trusting one to measure the
    /// other would make every offset assertion circular.
    /// </remarks>
    private static SortedDictionary<int, long> ObjectStarts(byte[] pdf)
    {
        var starts = new SortedDictionary<int, long>();
        foreach (Match m in Regex.Matches(Ascii(pdf), @"(?m)^(\d+) 0 obj\n"))
            starts[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Index;
        return starts;
    }

    /// <summary>
    /// Object number to the half-open byte range it occupies, each object ending where the next
    /// thing in the file begins.
    /// </summary>
    /// <remarks>
    /// Annex F's lengths are "from the beginning of a page object to the last byte of the last object
    /// used by that page" — file positions, not object contents — so an object's length has to be
    /// measured the same way, boundary to boundary, or the two never agree by a few bytes of
    /// separator.
    /// </remarks>
    private static SortedDictionary<int, (long Start, long End)> ObjectRuns(byte[] pdf)
    {
        string text = Ascii(pdf);
        var boundaries = new List<long>();
        SortedDictionary<int, long> starts = ObjectStarts(pdf);

        boundaries.AddRange(starts.Values);
        boundaries.AddRange(XrefOffsets(text).Select(o => (long)o));
        boundaries.Sort();

        var runs = new SortedDictionary<int, (long Start, long End)>();
        foreach (var kvp in starts)
        {
            int next = boundaries.BinarySearch(kvp.Value) + 1;
            long end = next < boundaries.Count ? boundaries[next] : pdf.Length;
            runs[kvp.Key] = (kvp.Value, end);
        }

        return runs;
    }

    /// <summary>The page objects, in page order, read out of the page tree's <c>/Kids</c>.</summary>
    private static List<int> PageObjectsInPageOrder(byte[] pdf)
    {
        Match kids = Regex.Match(Ascii(pdf), @"/Type /Pages /Kids \[([^\]]*)\]");
        Assert.True(kids.Success, "no page tree node with /Kids in the output");

        return Regex.Matches(kids.Groups[1].Value, @"(\d+) 0 R")
                    .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                    .ToList();
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// Stands in for the page tree node until the kid numbers are known. Object 2 in every fixture,
    /// so the placeholder is always overwritten; a fixture that forgot would fail loudly at parse
    /// rather than silently linearizing a document with no pages.
    /// </summary>
    private const string PageTreePlaceholder = "<< /Type /Pages /Kids [] /Count 0 >>";

    /// <summary>
    /// Five pages, each drawing its own number, so file order is observable in the bytes.
    /// </summary>
    private static byte[] FivePagePdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            PageTreePlaceholder,   // filled in below, once the kid numbers are known
        };

        var kids = new List<string>();
        int next = 3;
        for (int page = 1; page <= 5; page++)
        {
            kids.Add($"{next} 0 R");
            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] " +
                       $"/Contents {next + 1} 0 R /Resources << /Font << /F1 13 0 R >> >> >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }

        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count 5 >>";
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        return Assemble(bodies, "/Size {0} /Root 1 0 R");
    }

    private const int RichPageCount = 5;

    /// <summary>
    /// Every part Annex F distinguishes, in one document: inherited attributes, an outline, an
    /// information dictionary, an XObject shared by two later pages and one private to the last.
    /// </summary>
    /// <remarks>
    /// <b>The five-page fixture exercises three parts and this one exercises seven.</b> With no
    /// shared object that the first page cannot reach, part 8 is empty and its hint table entries
    /// never appear; with nothing inherited, the push-down never fires; with no outline, the generic
    /// hint table is never written. Each of those was a real defect at some point in this change, and
    /// none of them is visible on the simpler fixture.
    /// </remarks>
    private static byte[] RichPdf(bool useOutlines = false)
    {
        // 1 catalog, 2 page tree, 3 font, 4 shared image, 5 outlines, 6 outline item, 7 info.
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R" + (useOutlines ? " /PageMode /UseOutlines" : "") + " >>",
            PageTreePlaceholder,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Image(0),
            "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
            "<< /Title (Start) /Parent 5 0 R /Dest [8 0 R /Fit] >>",
            "<< /Producer (fixture) >>",
        };

        var kids = new List<string>();
        int next = 8;
        for (int page = 1; page <= RichPageCount; page++)
        {
            kids.Add($"{next} 0 R");

            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            string resources = string.Empty;
            if (page is 2 or 3)
            {
                content += " q 10 0 0 10 5 5 cm /Im0 Do Q";
                resources = " /Resources << /Font << /F1 3 0 R >> /XObject << /Im0 4 0 R >> >>";
            }
            else if (page == RichPageCount)
            {
                content += " q 10 0 0 10 5 5 cm /ImP Do Q";
                resources = $" /Resources << /Font << /F1 3 0 R >> /XObject << /ImP 100 0 R >> >>";
            }

            // Pages 1 and 4 declare nothing, so they inherit /MediaBox and /Resources from the tree.
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {next + 1} 0 R{resources} >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }

        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {RichPageCount} " +
                    "/MediaBox [0 0 300 400] /Resources << /Font << /F1 3 0 R >> >> >>";

        var extra = new SortedDictionary<int, string> { [100] = Image(48) };
        return Assemble(bodies, "/Size {0} /Root 1 0 R /Info 7 0 R", extra);
    }

    /// <summary>
    /// Three pages under a tree node that declares no <c>/Type</c>, which is legal and which real
    /// producers emit.
    /// </summary>
    private static byte[] PdfWithAnUntypedPageTree()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            PageTreePlaceholder,
        };

        var kids = new List<string>();
        int next = 3;
        for (int page = 1; page <= 3; page++)
        {
            kids.Add($"{next} 0 R");
            string content = $"BT /F1 24 Tf 40 300 Td (Page {page}) Tj ET";
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] " +
                       $"/Contents {next + 1} 0 R /Resources << /Font << /F1 9 0 R >> >> >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            next += 2;
        }

        // No /Type. Table 29 requires it of a page tree node, but nothing enforces it and a reader
        // that walks /Kids never asks.
        bodies[1] = $"<< /Kids [{string.Join(" ", kids)}] /Count 3 >>";
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        return Assemble(bodies, "/Size {0} /Root 1 0 R");
    }

    /// <summary>
    /// A one-page document with an information dictionary whose only cross-reference section is a
    /// cross-reference stream (§7.5.8), ASCII-hex filtered so the fixture stays text.
    /// </summary>
    private static byte[] PdfWithACrossReferenceStream()
    {
        string content = "BT /F1 24 Tf 40 300 Td (Page 1) Tj ET";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Producer (fixture) >>",
        };

        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        // /W [1 4 2]: type, offset, generation — entry 0 free, the rest in use, then the stream itself.
        int xrefNum = bodies.Count + 1;
        int xrefOffset = sb.Length;
        var rows = new StringBuilder();
        rows.Append("00").Append("00000000").Append("FFFF");
        foreach (int o in offsets) rows.Append("01").Append(o.ToString("X8", CultureInfo.InvariantCulture)).Append("0000");
        rows.Append("01").Append(xrefOffset.ToString("X8", CultureInfo.InvariantCulture)).Append("0000");
        string hex = rows + ">";
        sb.Append(xrefNum).Append(" 0 obj\n<< /Type /XRef /Size ").Append(xrefNum + 1)
          .Append(" /W [1 4 2] /Root 1 0 R /Info 6 0 R /Filter /ASCIIHexDecode /Length ").Append(hex.Length)
          .Append(" >>\nstream\n").Append(hex).Append("\nendstream\nendobj\n");
        sb.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A one-page document whose outline destination names an object that is not there.</summary>
    private static byte[] PdfWithADanglingDestination()
    {
        string content = "BT /F1 24 Tf 40 300 Td (Page 1) Tj ET";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 4 0 R " +
            "/Resources << /Font << /F1 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Title (Nowhere) /Parent 5 0 R /Dest [99 0 R /Fit] >>",
        };

        return Assemble(bodies, "/Size {0} /Root 1 0 R");
    }

    private static string Image(int firstByte)
    {
        var data = new StringBuilder();
        for (int i = 0; i < 48; i++) data.Append((char)('0' + (firstByte + i) % 10));

        return $"<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB " +
               $"/BitsPerComponent 8 /Length {data.Length} >>\nstream\n{data}\nendstream";
    }

    /// <summary>
    /// Writes numbered bodies out as a classic PDF, one cross-reference subsection per contiguous
    /// run of object numbers.
    /// </summary>
    /// <param name="bodies">Bodies for objects 1..n, in order.</param>
    /// <param name="trailerFormat">The trailer dictionary's contents, with <c>{0}</c> for /Size.</param>
    /// <param name="extra">
    /// Further objects at chosen numbers, so a fixture can leave a gap in the numbering — which is
    /// what makes a stale reference resolve to the wrong object rather than to nothing.
    /// </param>
    private static byte[] Assemble(List<string> bodies, string trailerFormat,
        SortedDictionary<int, string>? extra = null)
    {
        var objects = new SortedDictionary<int, string>();
        for (int i = 0; i < bodies.Count; i++) objects[i + 1] = bodies[i];
        if (extra != null)
            foreach (var kvp in extra) objects[kvp.Key] = kvp.Value;

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new SortedDictionary<int, int>();
        foreach (var kvp in objects)
        {
            offsets[kvp.Key] = sb.Length;
            sb.Append(kvp.Key).Append(" 0 obj\n").Append(kvp.Value).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objects.Keys.Last() + 1;
        sb.Append("xref\n0 1\n0000000000 65535 f \n");

        var runs = new List<List<int>>();
        foreach (int num in objects.Keys)
        {
            if (runs.Count > 0 && runs[^1][^1] == num - 1) runs[^1].Add(num);
            else runs.Add(new List<int> { num });
        }

        foreach (List<int> run in runs)
        {
            sb.Append(run[0]).Append(' ').Append(run.Count).Append('\n');
            foreach (int num in run)
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[num]);
        }

        sb.Append("trailer\n<< ")
          .AppendFormat(CultureInfo.InvariantCulture, trailerFormat, size)
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
