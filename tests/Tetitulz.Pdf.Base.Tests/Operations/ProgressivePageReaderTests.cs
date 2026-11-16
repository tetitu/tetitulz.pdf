// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Reading one page out of a file the client holds only part of — GAPS.md §23 slice 14c.
/// </summary>
/// <remarks>
/// <para><b>The claim under test is a protocol, not a value.</b> The reader answers *not yet* and
/// says which ranges it needs; the client fetches those and asks again. So the test that matters is
/// the loop: start with nothing, fetch exactly what you are told, and arrive at the page in a
/// bounded number of rounds. A test that hands over the whole file and checks the page comes back
/// would pass with the entire third answer deleted.</para>
///
/// <para>Everywhere else in this library an object that cannot be produced is <c>PdfNull</c> and
/// every caller reads that as <i>absent</i> — right for a complete file, wrong for an arriving one.
/// This is the one reader where the distinction is the return value.</para>
/// </remarks>
public class ProgressivePageReaderTests
{
    /// <summary>
    /// Enough pages that a late one lies well past the head.
    /// </summary>
    /// <remarks>
    /// <b>A five-page fixture cannot show this protocol at all.</b> Annex F puts the hint stream and
    /// the first page near the front, so the 4 KB head request covers the whole of a small file and
    /// every later stage answers from bytes already delivered — one round, nothing to observe. The
    /// staging is only visible when the page asked for is not in the head.
    /// </remarks>
    private const int PageCount = 60;

    /// <summary>A page far enough in to require its own request.</summary>
    private const int LatePage = 55;

    private static byte[] Linearized()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(PageCount));
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    // ================================================================
    //  The protocol
    // ================================================================

    /// <summary>
    /// <b>Start with nothing, fetch only what you are told, and the page arrives.</b> This is the
    /// whole point of the third answer: a client that is merely refused has to guess which bytes to
    /// get next, and guessing means fetching the file.
    /// </summary>
    [Fact]
    public void FetchingOnlyWhatItAsksForYieldsThePage()
    {
        byte[] pdf = Linearized();
        var source = new DeliveredSource(pdf);

        PdfProgressivePage page = null;
        var asked = new List<PdfByteRange>();

        for (int round = 0; round < 6; round++)
        {
            PdfLinearizationState state = PdfProgressivePageReader.TryReadPage(source, LatePage, out page);
            if (state == PdfLinearizationState.Linearized) break;

            Assert.Equal(PdfLinearizationState.NotYetAvailable, state);
            Assert.NotEmpty(page.Missing);

            asked.AddRange(page.Missing);
            foreach (PdfByteRange r in page.Missing) source.Deliver(r.Offset, r.Length);
        }

        Assert.NotNull(page);
        Assert.NotNull(page.Page);
        Assert.Equal("Page", (page.Page[PdfName.Type] as PdfName)?.Value);

        // Bounded: the head, the hint stream, the page. More than that would mean the reader is
        // discovering its needs one at a time rather than naming them.
        string trace = string.Join(" | ", asked.Select(r => r.ToString()));
        Assert.True(asked.Count <= 3, "asked for: " + trace);

        // The first request is the head, and a later one is this page's own range — so the staging
        // really happened rather than the head having covered everything.
        Assert.Equal(0, asked[0].Offset);

        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(whole, out PdfLinearizationHints hints));
        Assert.Contains(asked, r => r.Offset == hints.Pages[LatePage].Offset);

        // And it never asked for the whole file, which is the capability rather than a nicety.
        Assert.True(source.BytesServed < pdf.Length,
                    $"served {source.BytesServed} bytes of a {pdf.Length}-byte file; asked: {trace}");
    }

    /// <summary>With nothing delivered, the first thing it asks for is the head.</summary>
    [Fact]
    public void AnEmptySourceIsAskedForTheHead()
    {
        var source = new DeliveredSource(Linearized());

        Assert.Equal(PdfLinearizationState.NotYetAvailable,
                     PdfProgressivePageReader.TryReadPage(source, 0, out PdfProgressivePage page));

        PdfByteRange range = Assert.Single(page.Missing);
        Assert.Equal(0, range.Offset);
        Assert.True(range.Length > 0);
        Assert.Null(page.Page);
    }

    /// <summary>
    /// Once the head is here, the request is the page's own range — exactly what the page offset
    /// table names, which is the only range whose size depends on the page asked for.
    /// </summary>
    /// <remarks>
    /// Annex F puts the hint stream near the front, so a 4 KB head usually carries it too; that is
    /// a property of the layout rather than of this reader, and it is why the staging is asserted
    /// against the hint table's own numbers instead of against a fixed number of rounds.
    /// </remarks>
    [Fact]
    public void OnceTheHeadIsHereTheRequestIsThatPagesRange()
    {
        byte[] pdf = Linearized();

        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(whole, out PdfLinearizationHints hints));

        var source = new DeliveredSource(pdf);

        // Round one: the head, whatever it turns out to include.
        Assert.Equal(PdfLinearizationState.NotYetAvailable,
                     PdfProgressivePageReader.TryReadPage(source, LatePage, out PdfProgressivePage page));
        foreach (PdfByteRange r in page.Missing) source.Deliver(r.Offset, r.Length);

        // Round two: this page, named by the table rather than guessed at.
        Assert.Equal(PdfLinearizationState.NotYetAvailable,
                     PdfProgressivePageReader.TryReadPage(source, LatePage, out page));

        PdfByteRange range = Assert.Single(page.Missing);
        Assert.Equal(hints.Pages[LatePage].Offset, range.Offset);
        Assert.Equal(hints.Pages[LatePage].Length, range.Length);
    }

    // ================================================================
    //  What comes back
    // ================================================================

    /// <summary>
    /// Different pages yield different content — so the reader is honouring the page asked for
    /// rather than returning one range for every question.
    /// </summary>
    [Fact]
    public void EachPageYieldsItsOwnContent()
    {
        byte[] pdf = Linearized();

        byte[] first = ReadWhole(pdf, 0).Content;
        byte[] last = ReadWhole(pdf, PageCount - 1).Content;

        Assert.NotNull(first);
        Assert.NotNull(last);
        Assert.NotEmpty(first);
        Assert.False(first.SequenceEqual(last), "every page returned the same content stream");
    }

    /// <summary>
    /// <b>The content is the page's own content stream, decoded</b> — asserted against the text the
    /// fixture put on that page, which is a fact about the document rather than about this reader.
    /// </summary>
    /// <remarks>
    /// The test this replaced compared the result against the bytes at the offset Table F.3 names,
    /// and passed while the reader returned the opening bytes of the <i>file</i> for every page:
    /// both sides read the same wrong range. Correlating against the page's text cannot agree with
    /// a mistake that way.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(LatePage)]
    public void TheContentIsThePagesOwnContentStream(int pageIndex)
    {
        byte[] content = ReadWhole(Linearized(), pageIndex).Content;

        Assert.NotNull(content);
        Assert.Contains($"(Page {pageIndex + 1})", Encoding.ASCII.GetString(content), StringComparison.Ordinal);
    }

    // ================================================================
    //  Refusals
    // ================================================================

    /// <summary>
    /// <b>A file not written for progressive access is refused, not read whole.</b> Falling back
    /// would make the expensive case silent: a caller that asked for one page downloads the file and
    /// never learns why.
    /// </summary>
    [Fact]
    public void AnOrdinaryFileIsRefusedRatherThanReadWhole()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(PageCount);
        var source = new DeliveredSource(pdf);
        source.Deliver(0, pdf.Length);

        Assert.Equal(PdfLinearizationState.NotLinearized,
                     PdfProgressivePageReader.TryReadPage(source, 0, out PdfProgressivePage page));

        Assert.Null(page);
        Assert.Contains("progressive access", PdfProgressivePageReader.LastError ?? "",
                        StringComparison.Ordinal);
    }

    /// <summary>A page the document does not have is named, not silently clamped.</summary>
    [Fact]
    public void APageOutsideTheDocumentIsRefused()
    {
        byte[] pdf = Linearized();
        var source = new DeliveredSource(pdf);
        source.Deliver(0, pdf.Length);

        Assert.Equal(PdfLinearizationState.Linearized,
                     PdfProgressivePageReader.TryReadPage(source, 99, out PdfProgressivePage page));

        Assert.Null(page);
        Assert.Contains("outside the", PdfProgressivePageReader.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A null source is a programming error, not an answer.</summary>
    [Fact]
    public void ANullSourceIsRefused()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfProgressivePageReader.TryReadPage(null, 0, out _));
    }

    // ================================================================
    //  A content stream two pages draw — the shared section
    // ================================================================

    /// <summary>
    /// <b>A page whose content stream another later page also draws is asked for the shared-object
    /// group that holds it, then read.</b> Annex F puts such an object in part 8, outside every
    /// page's own run, so the page's range alone cannot answer; the first cut reported that as an
    /// error. The group is located through Table F.5 — the sum of the group lengths before it — so
    /// the fixture puts a second shared object (a resource dictionary with a lower object number)
    /// ahead of the content stream, and the test checks the range asked for is <i>not</i> the
    /// section's first byte: a reader that ignored the preceding lengths would ask for the wrong
    /// group and the control would show it.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void APageWhoseContentIsSharedWithALaterPageAsksForItsGroupThenReadsIt(int pageIndex)
    {
        byte[] pdf = Linearize(SharedContentPdf(pageCount: 3, sharing: new[] { 1, 2 }));
        using (var whole = new MemoryStream(pdf, writable: false))
            Assert.True(PdfLinearizationHintReader.TryRead(whole, out _));

        var source = new DeliveredSource(pdf);
        PdfProgressivePage page = null;
        var asked = new List<PdfByteRange>();
        PdfLinearizationState state = PdfLinearizationState.NotYetAvailable;
        for (int round = 0; round < 6 && state == PdfLinearizationState.NotYetAvailable; round++)
        {
            state = PdfProgressivePageReader.TryReadPage(source, pageIndex, out page);
            if (state != PdfLinearizationState.NotYetAvailable) break;
            Assert.NotEmpty(page.Missing);
            asked.AddRange(page.Missing);
            foreach (PdfByteRange r in page.Missing) source.Deliver(r.Offset, r.Length);
        }

        Assert.Equal(PdfLinearizationState.Linearized, state);
        Assert.NotNull(page.Content);
        Assert.Contains("SHARED", Encoding.ASCII.GetString(page.Content), StringComparison.Ordinal);
        Assert.True(asked.Count <= 4, "asked for: " + string.Join(" | ", asked));

        using var again = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(again, out PdfLinearizationHints hints));

        // The control: the content object is not inside the page's own run, so the shared group was
        // the only place it could come from …
        PdfPageHint own = hints.Pages[pageIndex];
        string ownRun = Encoding.ASCII.GetString(pdf, (int)own.Offset, (int)own.Length);
        Assert.DoesNotContain("SHARED", ownRun, StringComparison.Ordinal);

        // … and the group asked for lies inside the shared section, after its first group, which is
        // the resource dictionary: a range at the section's first byte would be the wrong object.
        long sectionEnd = hints.FirstSharedObjectOffset;
        for (int i = hints.SharedObjectsInFirstPage; i < hints.SharedObjects.Count; i++)
            sectionEnd += hints.SharedObjects[i].Length;
        PdfByteRange sharedAsk = Assert.Single(asked, r => r.Offset >= hints.FirstSharedObjectOffset && r.Offset < sectionEnd);
        Assert.True(sharedAsk.Offset > hints.FirstSharedObjectOffset,
                    "the shared range asked for is the section's first group, so the preceding length was not added");
        Assert.Contains("SHARED", Encoding.ASCII.GetString(pdf, (int)sharedAsk.Offset, (int)sharedAsk.Length),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A content stream the first page also draws lives in the first page's run</b>, and a later
    /// page is pointed at that range — Table F.5's groups for the first page's objects come first,
    /// at the first page's own offset.
    /// </summary>
    [Fact]
    public void APageSharingTheFirstPagesContentIsPointedAtTheFirstPagesRun()
    {
        byte[] pdf = Linearize(SharedContentPdf(pageCount: 2, sharing: new[] { 0, 1 }));

        var source = new DeliveredSource(pdf);
        PdfProgressivePage page = null;
        var asked = new List<PdfByteRange>();
        PdfLinearizationState state = PdfLinearizationState.NotYetAvailable;
        for (int round = 0; round < 6 && state == PdfLinearizationState.NotYetAvailable; round++)
        {
            state = PdfProgressivePageReader.TryReadPage(source, 1, out page);
            if (state != PdfLinearizationState.NotYetAvailable) break;
            asked.AddRange(page.Missing);
            foreach (PdfByteRange r in page.Missing) source.Deliver(r.Offset, r.Length);
        }

        Assert.Equal(PdfLinearizationState.Linearized, state);
        Assert.NotNull(page.Content);
        Assert.Contains("SHARED", Encoding.ASCII.GetString(page.Content), StringComparison.Ordinal);

        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(whole, out PdfLinearizationHints hints));
        PdfPageHint first = hints.Pages[0];
        Assert.Contains(asked, r => r.Offset >= first.Offset && r.Offset + r.Length <= first.Offset + first.Length
                                    && Encoding.ASCII.GetString(pdf, (int)r.Offset, (int)r.Length)
                                               .Contains("SHARED", StringComparison.Ordinal));
    }

    /// <summary>The awaiting form drives the same protocol through the shared section.</summary>
    [Fact]
    public async System.Threading.Tasks.Task TheAwaitingReaderReachesASharedContentStream()
    {
        byte[] pdf = Linearize(SharedContentPdf(pageCount: 3, sharing: new[] { 1, 2 }));
        var source = new CountingAsyncSource(pdf);

        PdfProgressivePage page = await PdfProgressivePageReader.ReadPageAsync(source, 2);

        Assert.Null(page.Error);
        Assert.NotNull(page.Content);
        Assert.Contains("SHARED", Encoding.ASCII.GetString(page.Content), StringComparison.Ordinal);
        Assert.True(source.Fetches.Count <= 4, "fetches: " + string.Join(" | ", source.Fetches));

        // No fetch is the file, and one of them is the shared group — the awaiting form went through
        // the same fourth stage the synchronous reader names.
        Assert.All(source.Fetches, f => Assert.True(f.Length < pdf.Length, "a fetch took the whole file"));
        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(whole, out PdfLinearizationHints hints));
        Assert.Contains(source.Fetches, f => f.Offset >= hints.FirstSharedObjectOffset);
    }

    // ----------------------------------------------------------------
    //  Stage 5: a page's other shared resources
    // ----------------------------------------------------------------

    /// <summary>
    /// A font two pages use lives in the shared section like a content stream two pages draw, and
    /// the hint table's groups name it the same way: the page costs one more request, and the font,
    /// and its descriptor behind it, resolve through the page.
    /// </summary>
    [Fact]
    public void APageWhoseFontIsSharedAsksForItsGroupThenResolvesIt()
    {
        byte[] pdf = Linearize(SharedFontPdf(4, new[] { 1, 3 }));
        var source = new DeliveredSource(pdf);
        source.Deliver(0, 4096);

        var asked = new List<PdfByteRange>();
        PdfLinearizationState state = PdfLinearizationState.NotYetAvailable;
        PdfProgressivePage page = null;
        for (int round = 0; round < 6 && state == PdfLinearizationState.NotYetAvailable; round++)
        {
            state = PdfProgressivePageReader.TryReadPage(source, 1, out page);
            if (state != PdfLinearizationState.NotYetAvailable) break;
            Assert.NotEmpty(page.Missing);
            foreach (PdfByteRange range in page.Missing)
            {
                asked.Add(range);
                source.Deliver(range.Offset, range.Length);
            }
        }

        Assert.Equal(PdfLinearizationState.Linearized, state);
        Assert.Null(PdfProgressivePageReader.LastError);
        Assert.Contains("(Page 2)", Encoding.ASCII.GetString(page.Content), StringComparison.Ordinal);
        Assert.True(page.UnresolvedResources.Count == 0, "unresolved: " + string.Join(", ", page.UnresolvedResources.Select(r => r.ReferenceObjectNumber)));

        PdfDictionary resources = Assert.IsType<PdfDictionary>(page.Resolve(page.Page[PdfName.Resources]));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(page.Resolve(resources[PdfName.Font]));
        PdfDictionary f1 = Assert.IsType<PdfDictionary>(page.Resolve(fonts[new PdfName("F1")]));
        Assert.Equal("Helvetica", (f1[PdfName.BaseFont] as PdfName)?.Value);
        PdfDictionary descriptor = Assert.IsType<PdfDictionary>(page.Resolve(f1[PdfName.FontDescriptor]));
        Assert.Equal("Helvetica", (descriptor[PdfName.FontName] as PdfName)?.Value);

        // The page's own run holds neither, so both came from the shared section the page asked for.
        Assert.True(PdfLinearizationHintReader.TryRead(new MemoryStream(pdf), out PdfLinearizationHints hints));
        string ownRun = Encoding.ASCII.GetString(pdf, (int)hints.Pages[1].Offset, (int)hints.Pages[1].Length);
        Assert.DoesNotContain("Helvetica", ownRun, StringComparison.Ordinal);
        Assert.True(asked.Count <= 3, "asked for: " + string.Join(" | ", asked));
        Assert.Contains(asked, r => r.Offset >= hints.FirstSharedObjectOffset
            && Encoding.ASCII.GetString(pdf, (int)r.Offset, (int)r.Length).Contains("/FontDescriptor", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control: a page whose resources are its own is read from its run alone, as before — the
    /// resource check costs no request when nothing leaves the run.
    /// </summary>
    [Fact]
    public void APageWithItsOwnResourcesNeedsNoRequestBeyondItsRun()
    {
        byte[] pdf = Linearize(SharedFontPdf(4, new[] { 1, 3 }));
        var source = new DeliveredSource(pdf);
        source.Deliver(0, 4096);

        Assert.Equal(PdfLinearizationState.NotYetAvailable,
            PdfProgressivePageReader.TryReadPage(source, 2, out PdfProgressivePage pending));
        PdfByteRange own = Assert.Single(pending.Missing);
        source.Deliver(own.Offset, own.Length);

        Assert.Equal(PdfLinearizationState.Linearized,
            PdfProgressivePageReader.TryReadPage(source, 2, out PdfProgressivePage page));
        Assert.Null(PdfProgressivePageReader.LastError);
        Assert.True(page.UnresolvedResources.Count == 0, "unresolved: " + string.Join(", ", page.UnresolvedResources.Select(r => r.ReferenceObjectNumber)));

        PdfDictionary resources = Assert.IsType<PdfDictionary>(page.Resolve(page.Page[PdfName.Resources]));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(page.Resolve(resources[PdfName.Font]));
        PdfDictionary f1 = Assert.IsType<PdfDictionary>(page.Resolve(fonts[new PdfName("F1")]));
        Assert.Equal("Courier", (f1[PdfName.BaseFont] as PdfName)?.Value);
    }

    /// <summary>
    /// A reference no range holds — the page's run, then every group the hint table names for the
    /// page — is reported on the page, not answered as absent: Annex F puts a page's objects in its
    /// run or in the shared section, so the file is at fault, and a renderer can say which font the
    /// page could not have. The file is a correct linearization with one reference repointed, in
    /// place, at an object number nothing defines.
    /// </summary>
    [Fact]
    public void AResourceReferenceNoRangeHolds_IsReportedOnThePage_NotAnsweredAsAbsent()
    {
        byte[] pdf = Linearize(SharedFontPdf(12, new[] { 1, 3 }));
        Assert.True(PdfLinearizationHintReader.TryRead(new MemoryStream(pdf), out PdfLinearizationHints hints));

        // Page 1's run: "/F1 N 0 R" → "/F1 M 0 R" where M has N's width and no object.
        int runStart = (int)hints.Pages[1].Offset;
        string run = Encoding.ASCII.GetString(pdf, runStart, (int)hints.Pages[1].Length);
        int at = run.IndexOf("/F1 ", StringComparison.Ordinal);
        Assert.True(at >= 0, "the page's /Font dictionary is not in its run");
        int digitsStart = at + 4;
        int digitsEnd = digitsStart;
        while (char.IsDigit(run[digitsEnd])) digitsEnd++;
        string original = run.Substring(digitsStart, digitsEnd - digitsStart);
        string whole = Encoding.ASCII.GetString(pdf);
        string repointed = null;
        for (int candidate = (int)Math.Pow(10, original.Length) - 1; candidate > 0; candidate--)
        {
            string text = candidate.ToString(CultureInfo.InvariantCulture);
            if (text.Length == original.Length && !whole.Contains(text + " 0 obj", StringComparison.Ordinal))
            {
                repointed = text;
                break;
            }
        }
        Assert.NotNull(repointed);
        Encoding.ASCII.GetBytes(repointed).CopyTo(pdf, runStart + digitsStart);

        var source = new DeliveredSource(pdf);
        source.Deliver(0, pdf.Length);
        Assert.Equal(PdfLinearizationState.Linearized,
            PdfProgressivePageReader.TryReadPage(source, 1, out PdfProgressivePage page));

        PdfReference missing = Assert.Single(page.UnresolvedResources);
        Assert.Equal(int.Parse(repointed, CultureInfo.InvariantCulture), missing.ReferenceObjectNumber);
        Assert.Null(page.Resolve(missing));
        Assert.NotNull(page.Content);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheAwaitingReaderResolvesASharedFont()
    {
        byte[] pdf = Linearize(SharedFontPdf(4, new[] { 1, 3 }));
        var source = new CountingAsyncSource(pdf);

        PdfProgressivePage page = await PdfProgressivePageReader.ReadPageAsync(source, 3);

        Assert.Null(page.Error);
        Assert.True(page.UnresolvedResources.Count == 0, "unresolved: " + string.Join(", ", page.UnresolvedResources.Select(r => r.ReferenceObjectNumber)));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page.Resolve(page.Page[PdfName.Resources]));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(page.Resolve(resources[PdfName.Font]));
        PdfDictionary f1 = Assert.IsType<PdfDictionary>(page.Resolve(fonts[new PdfName("F1")]));
        Assert.Equal("Helvetica", (f1[PdfName.BaseFont] as PdfName)?.Value);
        Assert.True(source.Fetches.Count <= 4, "fetches: " + string.Join(" | ", source.Fetches));
        Assert.All(source.Fetches, f => Assert.True(f.Length < pdf.Length, "a fetch took the whole file"));
    }

    /// <summary>
    /// <paramref name="pageCount"/> pages, each with its own content stream showing its number; those
    /// in <paramref name="sharing"/> use one font (Helvetica, with a descriptor behind it), every other
    /// page a font of its own (Courier). The shared font and its descriptor are the shared section's
    /// groups; an own font sits in its page's run.
    /// </summary>
    /// <remarks>Padded past the 4 KB head, as <see cref="SharedContentPdf"/> is, so the shared section is
    /// a range the reader has to ask for.</remarks>
    internal static byte[] SharedFontPdf(int pageCount, int[] sharing, bool shareDescriptorOnly = false)
    {
        string pad = new string('o', 2000);
        int sharedFont = 3 + pageCount;
        int sharedDescriptor = sharedFont + 1;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + pageCount + " /Kids ["
                + string.Join(" ", Enumerable.Range(3, pageCount).Select(n => n + " 0 R")) + "] >>",
        };
        int next = sharedDescriptor + 1;
        var tail = new List<string>();
        for (int i = 0; i < pageCount; i++)
        {
            int font;
            if (sharing.Contains(i) && !shareDescriptorOnly)
            {
                font = sharedFont;
            }
            else if (sharing.Contains(i))
            {
                // The page's own font, naming the shared descriptor: the shared object is two hops away.
                font = next++;
                tail.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor " + sharedDescriptor + " 0 R >>");
            }
            else
            {
                font = next++;
                tail.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");
            }
            int content = next++;
            tail.Add(Stream("BT /F1 12 Tf 20 100 Td (Page " + (i + 1) + ") Tj ET % " + pad));
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 " + font
                       + " 0 R >> >> /Contents " + content + " 0 R >>");
        }
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor " + sharedDescriptor + " 0 R >>");
        bodies.Add("<< /Type /FontDescriptor /FontName /Helvetica /Flags 32 /FontBBox [-166 -225 1000 931] /ItalicAngle 0 "
                   + "/Ascent 718 /Descent -207 /CapHeight 718 /StemV 88 >>");
        bodies.AddRange(tail);

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());

        static string Stream(string content)
            => "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";
    }

    /// <summary>One page whose <c>/Font</c> dictionary names <paramref name="fontCount"/> fonts of its own.</summary>
    internal static byte[] ManyFontsPdf(int fontCount)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
        };
        var fonts = new StringBuilder();
        for (int i = 0; i < fontCount; i++) fonts.Append("/F").Append(i).Append(' ').Append(5 + i).Append(" 0 R ");
        bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << " + fonts
                   + ">> >> /Contents 4 0 R >>");
        bodies.Add("<< /Length 23 >>\nstream\nBT /F0 12 Tf (x) Tj ET\nendstream");
        // Each font names itself, so a walk that forgets what it has followed never ends on its own.
        for (int i = 0; i < fontCount; i++) bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Self " + (5 + i) + " 0 R >>");

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The check follows a reference it has resolved: a descriptor two pages' own fonts share is two
    /// hops from <c>/Resources</c>, in the shared section, and is still fetched and resolved.
    /// </summary>
    [Fact]
    public void APageWhoseFontDescriptorAloneIsShared_ReachesItThroughTheFont()
    {
        byte[] pdf = Linearize(SharedFontPdf(4, new[] { 1, 3 }, shareDescriptorOnly: true));
        var source = new DeliveredSource(pdf);
        source.Deliver(0, 4096);

        var asked = new List<PdfByteRange>();
        PdfProgressivePage page = ReadDelivering(source, 1, asked);

        Assert.True(page.UnresolvedResources.Count == 0, "unresolved: " + string.Join(", ", page.UnresolvedResources.Select(r => r.ReferenceObjectNumber)));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page.Resolve(page.Page[PdfName.Resources]));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(page.Resolve(resources[PdfName.Font]));
        PdfDictionary f1 = Assert.IsType<PdfDictionary>(page.Resolve(fonts[new PdfName("F1")]));
        Assert.Equal("Helvetica", (f1[PdfName.BaseFont] as PdfName)?.Value);
        PdfDictionary descriptor = Assert.IsType<PdfDictionary>(page.Resolve(f1[PdfName.FontDescriptor]));
        Assert.Equal("Helvetica", (descriptor[PdfName.FontName] as PdfName)?.Value);

        Assert.True(PdfLinearizationHintReader.TryRead(new MemoryStream(pdf), out PdfLinearizationHints hints));
        string ownRun = Encoding.ASCII.GetString(pdf, (int)hints.Pages[1].Offset, (int)hints.Pages[1].Length);
        Assert.Contains("/BaseFont /Helvetica", ownRun, StringComparison.Ordinal);
        Assert.DoesNotContain("/FontName", ownRun, StringComparison.Ordinal);
        Assert.Contains(asked, r => r.Offset >= hints.FirstSharedObjectOffset);
    }

    /// <summary>
    /// The check follows four hops of references and no more — the dictionary, a category's entry,
    /// its descriptor, its program. A reference at the fifth hop is neither fetched nor reported; the
    /// same reference one hop shallower is. Pinned so the depth is a decision and not an accident.
    /// </summary>
    [Theory]
    [InlineData(4, false)]
    [InlineData(3, true)]
    public void AReferenceBeyondTheFourthHop_IsNotFollowed(int chainLength, bool reported)
    {
        byte[] pdf = Linearize(ChainedResourcePdf(chainLength));
        int repointed = Repoint(pdf, pageIndex: 0, key: "/Deep ");

        var source = new DeliveredSource(pdf);
        source.Deliver(0, pdf.Length);
        Assert.Equal(PdfLinearizationState.Linearized,
            PdfProgressivePageReader.TryReadPage(source, 0, out PdfProgressivePage page));

        if (reported)
            Assert.Equal(repointed, Assert.Single(page.UnresolvedResources).ReferenceObjectNumber);
        else
            Assert.Empty(page.UnresolvedResources);
    }

    /// <summary>Reads a page, delivering every range it asks for, and records the asks.</summary>
    private static PdfProgressivePage ReadDelivering(DeliveredSource source, int pageIndex, List<PdfByteRange> asked)
    {
        PdfProgressivePage page = null;
        for (int round = 0; round < 6; round++)
        {
            PdfLinearizationState state = PdfProgressivePageReader.TryReadPage(source, pageIndex, out page);
            if (state == PdfLinearizationState.Linearized) return page;
            Assert.Equal(PdfLinearizationState.NotYetAvailable, state);
            Assert.NotEmpty(page.Missing);
            foreach (PdfByteRange range in page.Missing)
            {
                asked.Add(range);
                source.Deliver(range.Offset, range.Length);
            }
        }
        Assert.Fail("the page was not read in six rounds; asked for: " + string.Join(" | ", asked));
        return page;
    }

    /// <summary>
    /// Repoints the reference after <paramref name="key"/> in page <paramref name="pageIndex"/>'s run
    /// at an object number of the same width that nothing in the file defines, in place, and returns
    /// it — a correct linearization with one dangling reference.
    /// </summary>
    private static int Repoint(byte[] pdf, int pageIndex, string key)
    {
        Assert.True(PdfLinearizationHintReader.TryRead(new MemoryStream(pdf), out PdfLinearizationHints hints));
        int runStart = (int)hints.Pages[pageIndex].Offset;
        string run = Encoding.ASCII.GetString(pdf, runStart, (int)hints.Pages[pageIndex].Length);
        int at = run.IndexOf(key, StringComparison.Ordinal);
        Assert.True(at >= 0, key + " is not in the page's run");
        int digitsStart = at + key.Length;
        int digitsEnd = digitsStart;
        while (char.IsDigit(run[digitsEnd])) digitsEnd++;
        string original = run.Substring(digitsStart, digitsEnd - digitsStart);
        string whole = Encoding.ASCII.GetString(pdf);
        for (int candidate = (int)Math.Pow(10, original.Length) - 1; candidate > 0; candidate--)
        {
            string text = candidate.ToString(CultureInfo.InvariantCulture);
            if (text.Length != original.Length || whole.Contains(text + " 0 obj", StringComparison.Ordinal)) continue;
            Encoding.ASCII.GetBytes(text).CopyTo(pdf, runStart + digitsStart);
            return candidate;
        }
        Assert.Fail("no free object number of width " + original.Length);
        return -1;
    }

    /// <summary>
    /// One page whose <c>/F1</c> font heads a chain of <paramref name="chainLength"/> dictionaries, each
    /// referencing the next through <c>/Next</c>, the last naming the content stream through
    /// <c>/Deep</c> — the reference a test repoints. The font is hop 1, so <c>/Deep</c> sits at hop
    /// <paramref name="chainLength"/> + 1.
    /// </summary>
    internal static byte[] ChainedResourcePdf(int chainLength)
    {
        // Four more pages with content of their own: the linearizer numbers the first page's objects
        // after the other pages', the parameter dictionary and the hint stream, so the first page's
        // content stream — the chain's end — has a two-digit number and a free one exists to repoint at.
        const int ExtraPages = 4;
        int firstExtra = 5 + chainLength;
        var kids = new List<string> { "3 0 R" };
        for (int p = 0; p < ExtraPages; p++) kids.Add((firstExtra + 2 * p) + " 0 R");
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + (ExtraPages + 1) + " /Kids [" + string.Join(" ", kids) + "] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 23 >>\nstream\nBT /F1 12 Tf (x) Tj ET\nendstream",
        };
        for (int i = 0; i < chainLength; i++)
        {
            string link = i + 1 < chainLength ? "/Next " + (6 + i) + " 0 R" : "/Deep 4 0 R";
            bodies.Add(i == 0
                ? "<< /Type /Font /Subtype /Type1 /BaseFont /Courier " + link + " >>"
                : "<< /Link " + i + " " + link + " >>");
        }
        for (int p = 0; p < ExtraPages; p++)
        {
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents " + (firstExtra + 2 * p + 1) + " 0 R >>");
            bodies.Add("<< /Length 23 >>\nstream\nBT /F1 12 Tf (y) Tj ET\nendstream");
        }
        return Assemble(bodies);
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
    internal static byte[] Linearize(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// <paramref name="pageCount"/> pages; those in <paramref name="sharing"/> draw one content
    /// stream, every other its own. The sharing pages also name one shared resource dictionary
    /// whose object number is below the shared stream's, so when the first page is not among them
    /// the stream is the shared section's second group.
    /// </summary>
    /// <remarks>
    /// <b>Padded past the head.</b> The reader's first request is a 4 KB head, which covers the whole
    /// of a small file and every later stage then answers from bytes already delivered — nothing to
    /// observe. Each own stream carries 2 KB of comment and the shared one 6 KB, so the shared
    /// section lies past the head and a first-page run holding the shared stream runs past it too.
    /// </remarks>
    private static byte[] SharedContentPdf(int pageCount, int[] sharing)
    {
        string ownPad = new string('o', 2000);
        string sharedPad = new string('s', 6000);
        int resourcesObj = 3 + pageCount;
        int sharedObj = resourcesObj + 1;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + pageCount + " /Kids ["
                + string.Join(" ", Enumerable.Range(3, pageCount).Select(n => n + " 0 R")) + "] >>",
        };
        int nextOwn = sharedObj + 1;
        var ownContents = new List<string>();
        for (int i = 0; i < pageCount; i++)
        {
            int content = sharing.Contains(i) ? sharedObj : nextOwn++;
            if (!sharing.Contains(i)) ownContents.Add(Stream("0 g 10 10 50 50 re f % OWN" + i + " " + ownPad));
            // Only the sharing pages name the shared resource dictionary: an object the first page
            // also used would be a first-page object (part 6), not a shared-section group.
            string resources = sharing.Contains(i) ? resourcesObj + " 0 R" : "<< >>";
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources " + resources
                       + " /Contents " + content + " 0 R >>");
        }
        bodies.Add("<< /ExtGState << /GS0 << /CA 1 >> >> >>");
        bodies.Add(Stream("0 0 1 rg 10 10 100 100 re f % SHARED " + sharedPad));
        bodies.AddRange(ownContents);

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());

        static string Stream(string content)
            => "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";
    }

    private sealed class CountingAsyncSource : IAsyncByteRangeSource
    {
        private readonly byte[] _all;
        public CountingAsyncSource(byte[] all) => _all = all;
        public long? Length => _all.Length;
        public List<(long Offset, long Length)> Fetches { get; } = new();

        public System.Threading.Tasks.Task<byte[]> FetchAsync(long offset, long length,
                                                               System.Threading.CancellationToken cancellationToken)
        {
            long available = Math.Max(0, Math.Min(length, _all.Length - offset));
            Fetches.Add((offset, available));
            var bytes = new byte[available];
            Array.Copy(_all, offset, bytes, 0, available);
            return System.Threading.Tasks.Task.FromResult(bytes);
        }
    }

    // ================================================================

    private static PdfProgressivePage ReadWhole(byte[] pdf, int pageIndex)
    {
        var source = new DeliveredSource(pdf);
        source.Deliver(0, pdf.Length);

        Assert.Equal(PdfLinearizationState.Linearized,
                     PdfProgressivePageReader.TryReadPage(source, pageIndex, out PdfProgressivePage page));
        return page;
    }

    /// <summary>
    /// A file that serves only the ranges it has been told to deliver — a download whose progress
    /// the test controls exactly.
    /// </summary>
    /// <remarks>
    /// Ranges are tracked rather than a single "arrived" prefix, because a progressive client fetches
    /// the head, then a range in the middle, then another: modelling it as a prefix would quietly
    /// give the reader bytes it never asked for and make the round count meaningless.
    /// </remarks>
    internal sealed class DeliveredSource : IByteRangeSource
    {
        private readonly byte[] _all;
        private readonly List<(long From, long To)> _delivered = new();

        public DeliveredSource(byte[] all) => _all = all;

        public long BytesServed { get; private set; }

        public long? Length => _all.Length;

        public void Deliver(long offset, long length) =>
            _delivered.Add((offset, Math.Min(offset + length, _all.Length)));

        public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
        {
            read = 0;
            if (offset >= _all.Length) return true;

            long wanted = Math.Min(count, _all.Length - offset);
            if (!Has(offset, offset + wanted)) return false;

            Array.Copy(_all, offset, buffer, bufferOffset, wanted);
            read = (int)wanted;
            BytesServed += wanted;
            return true;
        }

        private bool Has(long from, long to)
        {
            foreach ((long f, long t) in _delivered)
                if (f <= from && t >= to) return true;

            return false;
        }
    }
}

/// <summary>
/// The ceiling on the objects the resource check examines — in the limits collection, because it is
/// set through <see cref="PdfLimits"/>.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class ProgressiveResourceCeilingTests
{
    [Fact]
    public void TheResourceCheck_StopsAtTheCeiling_AndSaysSo_WhileThePageIsStillAnswered()
    {
        byte[] pdf = ProgressivePageReaderTests.Linearize(ProgressivePageReaderTests.ManyFontsPdf(20));
        var source = new ProgressivePageReaderTests.DeliveredSource(pdf);
        source.Deliver(0, pdf.Length);
        try
        {
            PdfLimits.MaxProgressiveResourceObjects = 8;
            Assert.Equal(PdfLinearizationState.Linearized,
                PdfProgressivePageReader.TryReadPage(source, 0, out PdfProgressivePage capped));
            Assert.Contains("more than 8 objects", PdfProgressivePageReader.LastError ?? "", StringComparison.Ordinal);
            Assert.NotNull(capped.Content);

            // With each reference followed once the check dequeues 102 objects (the dictionary, the
            // category, twenty fonts, their four entries each); following every /Self again to the hop
            // cap would dequeue 282. The control cap sits between.
            PdfLimits.MaxProgressiveResourceObjects = 150;
            Assert.Equal(PdfLinearizationState.Linearized,
                PdfProgressivePageReader.TryReadPage(source, 0, out PdfProgressivePage whole));
            Assert.Null(PdfProgressivePageReader.LastError);
            Assert.Empty(whole.UnresolvedResources);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
