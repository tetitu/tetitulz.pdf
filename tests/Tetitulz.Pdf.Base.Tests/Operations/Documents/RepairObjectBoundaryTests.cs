// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Repair finds where each object ends by reading its tokens, not by searching for the next
/// <c>endobj</c>.
/// </summary>
/// <remarks>
/// <para>The byte search cut an object at any <c>endobj</c> inside a string or stream data, took a
/// <c>N G obj</c> inside either as an object that could replace the real one, dropped an object
/// that merely lacked its <c>endobj</c>, and copied everything up to a distant <c>endobj</c> once per
/// header that shared it.</para>
/// <para><b>Every fixture here has no cross-reference data</b> — the case repair exists for. The
/// parser then recovers by scanning too, and its scan sees the same header-shaped text, so its
/// choice of copy cannot hide a wrong boundary.</para>
/// </remarks>
public sealed class RepairObjectBoundaryTests
{
    private const string Content = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET";

    private static string Obj(int num, string body) => $"{num} 0 obj\n{body}\nendobj\n";

    private static readonly string Catalog = Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
    private static readonly string Pages = Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    private static readonly string Page = Obj(3,
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << /Font << /F1 5 0 R >> >> >>");
    private static readonly string Font = Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    private static readonly string Info = Obj(6, "<< /Producer (Probe) >>");

    private static string ContentStream(string data) => Obj(4, $"<< /Length {data.Length} >>\nstream\n{data}\nendstream");

    /// <summary>A file of <paramref name="parts"/> and a trailer, with no xref table and no <c>startxref</c>.</summary>
    private static byte[] Pdf(params string[] parts)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        foreach (string part in parts) sb.Append(part);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R /Info 6 0 R >>\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Repair(byte[] pdf, out PdfRepairResult result)
    {
        using var output = new MemoryStream();
        result = PdfRepair.Repair(new MemoryStream(pdf, writable: false), output);
        return output.ToArray();
    }

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf, writable: false));

    private static string Errors(PdfRepairResult result) => string.Join("; ", result.Errors);

    // ---------------------------------------------------------------- inside stream data

    [Fact]
    public void AnEndobjInsideStreamData_DoesNotCutTheStreamShort()
    {
        // Ahead of the text, so a cut there keeps only the comment and the stream's /Length runs on
        // into the next object.
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream("% endobj inside the data\n" + Content), Font, Info);

        byte[] repaired = Repair(pdf, out var result);

        Assert.True(result.IsClean, Errors(result));
        Assert.Contains("Hello", PdfOperations.ExtractText(new MemoryStream(repaired)));
    }

    [Fact]
    public void AnObjectHeaderInsideStreamData_IsNotAnObjectAndDoesNotReplaceTheRealOne()
    {
        // Object 2 is the page tree, and this text follows it: taken as a header, the later "object 2"
        // is the current copy and the page is gone.
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content + "\n% 2 0 obj << /Type /Fake >>"), Font, Info);

        byte[] repaired = Repair(pdf, out var result);

        using var doc = Open(repaired);
        Assert.Single(doc.Pages);
        Assert.True(result.IsClean, Errors(result));
    }

    /// <summary>
    /// An uncompressed embedded PDF, carrying endstream and endobj keywords of its own. It ends in
    /// no white space, so a data start one byte off cannot be absorbed by the white space skipped
    /// before the real endstream.
    /// </summary>
    private const string EmbeddedPdf = "%PDF-1.4\n9 0 obj\n<< /Length 3 >>\nstream\nabc\nendstream\nendobj\n%%EOF";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AStreamWhoseDataHoldsEndstream_IsSteppedOverByItsLength(string endOfLine)
    {
        const string embedded = EmbeddedPdf;
        string file = Obj(7, $"<< /Type /EmbeddedFile /Length {embedded.Length} >>\nstream{endOfLine}{embedded}\nendstream");
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content), Font, Info, file);

        byte[] repaired = Repair(pdf, out var result);

        using var doc = Open(repaired);
        var stream = Assert.IsType<PdfStream>(doc.GetObject(7));
        Assert.Equal(embedded, Encoding.Latin1.GetString(stream.RawData));
        Assert.True(result.IsClean, Errors(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Length 3")]
    [InlineData("/Length -100000")]
    [InlineData("/Length 99999")]
    [InlineData("/Length 9223372036854774784")]
    public void AStreamWhoseLengthCannotBeTrusted_EndsAtItsEndstream(string lengthEntry)
    {
        // Missing, short, negative, past the end of the file, and so large that adding it to the
        // data's offset wraps around. A number is held as a double, so the largest length below 2^63
        // is 2^63 − 1024: the data has to start more than 1 024 bytes in for the sum to wrap.
        string padding = "% " + new string('x', 1200) + "\n";
        string content = Obj(4, $"<< {lengthEntry} >>\nstream\n{Content}\nendstream");
        byte[] pdf = Pdf(Catalog, Pages, Page, padding, content, Font, Info);

        byte[] repaired = Repair(pdf, out var result);

        Assert.Contains("Hello", PdfOperations.ExtractText(new MemoryStream(repaired)));
        Assert.True(result.IsClean, Errors(result));
    }

    [Fact]
    public void AStreamWhoseLengthIsIndirect_IsSteppedOverByTheLengthItNames()
    {
        const string embedded = EmbeddedPdf;
        string file = Obj(7, $"<< /Type /EmbeddedFile /Length 8 0 R >>\nstream\n{embedded}\nendstream");
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content), Font, Info, file, Obj(8, embedded.Length.ToString()));

        byte[] repaired = Repair(pdf, out _);

        using var doc = Open(repaired);
        var stream = Assert.IsType<PdfStream>(doc.GetObject(7));
        Assert.Equal(embedded, Encoding.Latin1.GetString(stream.RawData));
    }

    // ---------------------------------------------------------------- inside strings

    [Fact]
    public void AnEndobjInsideALiteralString_DoesNotEndTheObject()
    {
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content), Font,
                         Obj(6, "<< /Title (what endobj means) /Producer (Probe) >>"));

        byte[] repaired = Repair(pdf, out var result);

        using var doc = Open(repaired);
        Assert.Equal("what endobj means", doc.Metadata.Title);
        Assert.Equal("Probe", doc.Metadata.Producer);
        Assert.True(result.IsClean, Errors(result));
    }

    [Fact]
    public void AnObjectHeaderInsideALiteralString_IsNotAnObject()
    {
        // The string follows object 2, so as a header it would be the page tree's current copy — and
        // the information dictionary holding it would have no endobj before "the next object".
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content), Font,
                         Obj(6, "<< /Title (see 2 0 obj) /Producer (Probe) >>"));

        byte[] repaired = Repair(pdf, out var result);

        using var doc = Open(repaired);
        Assert.Single(doc.Pages);
        Assert.Equal("Probe", doc.Metadata.Producer);
        Assert.True(result.IsClean, Errors(result));
    }

    // ---------------------------------------------------------------- a missing endobj

    [Fact]
    public void AnObjectMissingItsEndobj_IsClosedAtTheNextObjectAndReported()
    {
        string catalog = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\n";
        byte[] pdf = Pdf(catalog, Pages, Page, ContentStream(Content), Font, Info);

        byte[] repaired = Repair(pdf, out var result);

        using var doc = Open(repaired);
        Assert.Single(doc.Pages);
        Assert.Contains(result.Errors, e => e.Contains("closed", StringComparison.Ordinal) &&
                                            e.Contains("object 1 at byte", StringComparison.Ordinal));
    }

    [Fact]
    public void AStreamMissingItsEndobj_IsClosedAfterItsEndstream()
    {
        string content = $"4 0 obj\n<< /Length {Content.Length} >>\nstream\n{Content}\nendstream\n";
        byte[] pdf = Pdf(Catalog, Pages, Page, content, Font, Info);

        byte[] repaired = Repair(pdf, out var result);

        Assert.Contains("\nendstream\nendobj\n5 0 obj", Encoding.Latin1.GetString(repaired));
        Assert.Contains("Hello", PdfOperations.ExtractText(new MemoryStream(repaired)));
        Assert.Contains(result.Errors, e => e.Contains("closed", StringComparison.Ordinal) &&
                                            e.Contains("object 4 at byte", StringComparison.Ordinal));
    }

    [Fact]
    public void AnObjectMissingItsEndobjBeforeTheTrailer_IsClosed()
    {
        string info = "6 0 obj\n<< /Producer (Probe) >>\n";
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content), Font, info);

        byte[] repaired = Repair(pdf, out var result);

        using var doc = Open(repaired);
        Assert.Equal("Probe", doc.Metadata.Producer);
        Assert.Contains(result.Errors, e => e.Contains("closed", StringComparison.Ordinal) &&
                                            e.Contains("object 6 at byte", StringComparison.Ordinal));
    }

    [Fact]
    public void AnObjectCutOffAtTheEndOfTheFile_IsClosedWhenItsValueIsWhole()
    {
        var sb = new StringBuilder("%PDF-1.4\n").Append(Catalog).Append(Pages).Append(Page)
            .Append(ContentStream(Content)).Append(Font)
            .Append("trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R >>\n")
            .Append("6 0 obj\n<< /Producer (Probe) >>");

        byte[] repaired = Repair(Encoding.Latin1.GetBytes(sb.ToString()), out _);

        using var doc = Open(repaired);
        Assert.Equal("Probe", doc.Metadata.Producer);
    }

    [Theory]
    [InlineData("")]                                   // no value at all
    [InlineData("<< /Producer (Probe)")]               // a dictionary still open
    [InlineData("<< /Producer (Probe) >> ~~ junk ~~")] // bytes that are no PDF object
    [InlineData("<< /Producer (Probe) >> >>")]         // a close with nothing open
    public void AnObjectThatDoesNotEndInAWholeValue_IsNotRecovered(string body)
    {
        // Followed directly by object 5: nothing says where object 6 was meant to end, so it is not
        // guessed.
        string info = $"6 0 obj\n{body}\n";
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content), info, Font);

        byte[] repaired = Repair(pdf, out var result);

        using var doc = Open(repaired);
        Assert.IsType<PdfNull>(doc.GetObject(6));
        Assert.Contains(result.Errors, e => e.Contains("not recovered", StringComparison.Ordinal) &&
                                            e.Contains("object 6 at byte", StringComparison.Ordinal));
    }

    [Fact]
    public void AStringEscapeCutOffByTheEndOfTheFile_IsNotRecoveredAndTheRestIs()
    {
        var sb = new StringBuilder("%PDF-1.4\n").Append(Catalog).Append(Pages).Append(Page)
            .Append(ContentStream(Content)).Append(Font).Append(Info)
            .Append("trailer\n<< /Size 8 /Root 1 0 R /Info 6 0 R >>\n")
            .Append("7 0 obj\n(abc\\");

        byte[] repaired = Repair(Encoding.Latin1.GetBytes(sb.ToString()), out var result);

        using var doc = Open(repaired);
        Assert.Single(doc.Pages);
        Assert.Contains(result.Errors, e => e.Contains("not recovered", StringComparison.Ordinal) &&
                                            e.Contains("object 7 at byte", StringComparison.Ordinal));
    }

    [Fact]
    public void AStreamWithNoEndstream_IsNotRecovered()
    {
        // Cut off inside its data, as a truncated download is.
        string truncated = "7 0 obj\n<< /Length 500 >>\nstream\nabc";
        var sb = new StringBuilder("%PDF-1.4\n").Append(Catalog).Append(Pages).Append(Page)
            .Append(ContentStream(Content)).Append(Font).Append(Info)
            .Append("trailer\n<< /Size 8 /Root 1 0 R /Info 6 0 R >>\n").Append(truncated);

        byte[] repaired = Repair(Encoding.Latin1.GetBytes(sb.ToString()), out var result);

        using var doc = Open(repaired);
        Assert.IsType<PdfNull>(doc.GetObject(7));
        Assert.Single(doc.Pages);
        Assert.Contains(result.Errors, e => e.Contains("not recovered", StringComparison.Ordinal) &&
                                            e.Contains("object 7 at byte", StringComparison.Ordinal));
    }

    [Fact]
    public void AHeaderNumberedOutsideTheTable_IsSkippedAndReported()
    {
        byte[] pdf = Pdf(Catalog, Pages, Page, ContentStream(Content), Font, Info, Obj(2000000, "<< >>"));

        Repair(pdf, out var result);

        Assert.Contains(result.Errors, e => e.Contains("numbered outside", StringComparison.Ordinal) &&
                                            e.Contains("object 2000000 at byte", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- adversarial

    [Fact]
    public void ManyObjectsMissingTheirEndobj_AreClosedWithoutCopyingThePaddingOncePerObject()
    {
        // Two thousand complete objects with no endobj, then 100 KB of padding before a single
        // endobj. Each object used to be copied up to that endobj — the padding two thousand times.
        var sb = new StringBuilder("%PDF-1.4\n").Append(Catalog);
        for (int n = 10; n < 2010; n++) sb.Append(n).Append(" 0 obj\n<< /K 1 >>\n");
        sb.Append(' ', 100_000).Append("endobj\n");
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        byte[] repaired = Repair(pdf, out var result);

        // The last one does own the endobj; the 1 999 before it are closed where their value ends.
        Assert.Equal(2001, result.RecoveredObjectCount);
        Assert.Contains(result.Errors, e => e.StartsWith("1999 object(s) had no endobj", StringComparison.Ordinal));
        Assert.True(repaired.Length < 2 * pdf.Length, $"the repaired file is {repaired.Length} bytes");
    }

    [Fact]
    public void ManyIncompleteObjectsSharingOneDistantEndobj_AreSkippedAndReported()
    {
        // The same, but no object's value is complete, so none can be closed.
        var sb = new StringBuilder("%PDF-1.4\n").Append(Catalog);
        for (int n = 10; n < 2010; n++) sb.Append(n).Append(" 0 obj\n<< /K 1\n");
        sb.Append(' ', 100_000).Append("endobj\n");
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        byte[] repaired = Repair(pdf, out var result);

        Assert.Equal(2, result.RecoveredObjectCount);
        Assert.Contains(result.Errors, e => e.StartsWith("1999 object(s) were not recovered", StringComparison.Ordinal));
        Assert.True(repaired.Length < 2 * pdf.Length, $"the repaired file is {repaired.Length} bytes");
    }

    [Fact]
    public void UnclosedStringsThatEachHoldTheNextHeaders_AreReadNoFurtherThanAFewHeaders()
    {
        // Forty thousand headers, each opening a string that never closes. Read to its end, every
        // string runs to the end of the file: ~11 GB of tokens. Read no further than a few headers,
        // a few megabytes.
        var sb = new StringBuilder("%PDF-1.4\n").Append(Catalog);
        for (int n = 10; n < 40_010; n++) sb.Append(n).Append(" 0 obj (\n");
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        PdfRepairResult result = OnItsOwnThread(() =>
        {
            Repair(pdf, out var r);
            return r;
        });

        Assert.Contains(result.Errors, e => e.Contains("were not recovered", StringComparison.Ordinal));
    }

    private static T OnItsOwnThread<T>(Func<T> work)
    {
        T value = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { value = work(); }
            catch (Exception ex) { failure = ex; }
        }, 16 * 1024 * 1024)
        { IsBackground = true };

        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            Assert.Fail("the repair did not finish: each object's token walk ran on to the end of the file.");
        if (failure != null)
            Assert.Fail("the repair threw: " + failure);
        return value;
    }
}
