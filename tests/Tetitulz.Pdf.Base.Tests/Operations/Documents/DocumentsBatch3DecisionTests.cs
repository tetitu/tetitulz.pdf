// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The decisions in <see cref="PdfRepair"/> that the folder's third sweep found nothing asserting.
/// </summary>
public class DocumentsBatch3DecisionTests
{
    private const string Body =
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
        "trailer\n<< /Size 4 /Root 1 0 R >>\n%%EOF\n";

    private static (PdfRepairResult Result, byte[] Output) Repair(string pdf)
    {
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(new MemoryStream(Encoding.Latin1.GetBytes(pdf), writable: false), output);
        return (result, output.ToArray());
    }

    private static string FirstLine(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        return text.Substring(0, text.IndexOf('\n'));
    }

    /// <summary>
    /// The repaired file keeps the version its header declares, found where the parser finds it — a
    /// header after a preamble included, which the old 20-byte search missed and called 1.4.
    /// </summary>
    [Theory]
    [InlineData("", "%PDF-1.6")]
    [InlineData("<html><body>junk from a web server</body></html>\n", "%PDF-1.6")]
    public void TheHeadersVersionIsKept(string preamble, string expected)
    {
        var (_, output) = Repair(preamble + "%PDF-1.6\n" + Body);
        Assert.Equal(expected, FirstLine(output));
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(1, doc.Pages.Count);
    }

    /// <summary>
    /// A marker with no version after it is no version: the repaired file is 1.4 and opens, where it
    /// began with a bare <c>%PDF-</c>.
    /// </summary>
    [Fact]
    public void AMarkerWithNoVersion_IsWrittenAs14()
    {
        var (_, output) = Repair("%PDF-\n" + Body);
        Assert.Equal("%PDF-1.4", FirstLine(output));
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(1, doc.Pages.Count);
    }

    /// <summary>
    /// An object that ends the input on its <c>endobj</c>, with no line break, is copied with one:
    /// the cross-reference table written after it starts a line of its own. Read as bytes, since a
    /// tolerant parser accepts the two glued together.
    /// </summary>
    [Fact]
    public void AnObjectEndingTheInput_IsFollowedByALineBreak()
    {
        string pdf = "%PDF-1.4\n" +
                     "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                     "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj";
        string text = Encoding.Latin1.GetString(Repair(pdf).Output);
        Assert.Contains("/Count 0 >>\nendobj\nxref\n", text);
    }

    /// <summary>
    /// A newer trailer's <c>/Info null</c> is no entry (§7.3.9), so an older trailer's
    /// <c>/Info</c> still supplies it.
    /// </summary>
    [Fact]
    public void ANullTrailerEntry_DoesNotHideAnOlderOne()
    {
        string pdf = "%PDF-1.4\n" +
                     "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                     "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
                     "6 0 obj\n<< /Producer (Probe) >>\nendobj\n" +
                     "trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R >>\n" +
                     "trailer\n<< /Size 7 /Info null >>\n%%EOF\n";
        using var doc = PdfDocument.Open(new MemoryStream(Repair(pdf).Output));
        Assert.Equal("Probe", doc.Metadata.Producer);
    }

    /// <summary>
    /// A file the parser reads through a sound table whose trailer names only <c>/Size</c>, after an
    /// orphaned trailer — one no <c>/Prev</c> reaches — that names <c>/Info</c>: the parser's trailer
    /// cannot supply it, and the repair's own reading of the older keywords does.
    /// </summary>
    [Fact]
    public void AnOrphanedOlderTrailer_SuppliesWhatTheParsersTrailerLacks()
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var at = new int[7];
        void Obj(int n, string body) { at[n] = sb.Length; sb.Append($"{n} 0 obj\n{body}\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>");
        Obj(6, "<< /Producer (Probe) >>");
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R >>\n");
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        for (int n = 1; n <= 6; n++)
            sb.Append(at[n] > 0 ? $"{at[n]:D10} 00000 n \n" : "0000000000 65535 f \n");
        sb.Append($"trailer\n<< /Size 7 >>\nstartxref\n{xref}\n%%EOF\n");

        using var doc = PdfDocument.Open(new MemoryStream(Repair(sb.ToString()).Output));
        Assert.Equal("Probe", doc.Metadata.Producer);
    }

    /// <summary>
    /// When the trailer's root names no object the file holds, the catalog is the latest object
    /// whose <c>/Type</c> is <c>/Catalog</c> — not a later one that merely holds the name elsewhere.
    /// The trailer is what makes the search the repair's: with none, the parser's recovery names the
    /// catalog itself.
    /// </summary>
    [Fact]
    public void ALaterObjectHoldingTheNameCatalog_IsNotTheRoot()
    {
        string pdf = "%PDF-1.4\n" +
                     "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                     "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                     "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
                     "4 0 obj\n<< /Type /Annot /Subtype /Catalog >>\nendobj\n" +
                     "trailer\n<< /Size 10 /Root 9 0 R >>\n%%EOF\n";
        var (_, output) = Repair(pdf);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(1, doc.Pages.Count);
    }

    /// <summary>
    /// A trailer's <c>/Info</c> naming an object the file does not hold is not carried: the rebuilt
    /// trailer refers only to what it wrote. Read as bytes, since a reader resolves the dangling
    /// reference to null either way; the written <c>/Info</c> is the control.
    /// </summary>
    [Theory]
    [InlineData(6, true)]
    [InlineData(9, false)]
    public void ATrailerInfoIsCarriedOnlyWhenWritten(int infoObject, bool carried)
    {
        string pdf = "%PDF-1.4\n" +
                     "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                     "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
                     "6 0 obj\n<< /Producer (Probe) >>\nendobj\n" +
                     $"trailer\n<< /Size 10 /Root 1 0 R /Info {infoObject} 0 R >>\n%%EOF\n";
        string text = Encoding.Latin1.GetString(Repair(pdf).Output);
        string trailer = text.Substring(text.LastIndexOf("trailer", System.StringComparison.Ordinal));
        Assert.Equal(carried, trailer.Contains("/Info"));
    }

    /// <summary>
    /// The rebuilt trailer refers to each object by the generation its header declares — a catalog
    /// written <c>1 3 obj</c> is <c>/Root 1 3 R</c>. Read as bytes, since a lenient reader resolves
    /// <c>1 0 R</c> to it anyway.
    /// </summary>
    [Fact]
    public void TheTrailersReferencesCarryTheObjectsGeneration()
    {
        string pdf = "%PDF-1.4\n" +
                     "1 3 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                     "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
                     "6 2 obj\n<< /Producer (Probe) >>\nendobj\n" +
                     "trailer\n<< /Size 7 /Root 1 3 R /Info 6 2 R >>\n%%EOF\n";
        string text = Encoding.Latin1.GetString(Repair(pdf).Output);
        string trailer = text.Substring(text.LastIndexOf("trailer", System.StringComparison.Ordinal));
        Assert.Contains("/Root 1 3 R", trailer);
        Assert.Contains("/Info 6 2 R", trailer);
    }

    /// <summary>
    /// Recovering nothing says which nothing: no header in the file at all, or headers found and
    /// every one skipped — here a cross-reference stream, which describes offsets the repaired file
    /// no longer has.
    /// </summary>
    [Theory]
    [InlineData("%PDF-1.4\nnot a single object here\n", "no 'N G obj' header was found")]
    [InlineData("%PDF-1.5\n1 0 obj\n<< /Type /XRef /Size 2 /W [1 1 1] /Length 0 >>\nstream\n\nendstream\nendobj\n", "all 1 object header(s) found were skipped")]
    public void NothingRecovered_SaysWhy(string pdf, string reason)
    {
        var (result, _) = Repair(pdf);
        Assert.Equal(0, result.RecoveredObjectCount);
        Assert.Contains(result.Errors, e => e.Contains(reason));
    }

    /// <summary>
    /// A seekable input left at its end — as a caller that has just written it leaves it — is read
    /// from the start.
    /// </summary>
    [Fact]
    public void ASeekableInputLeftAtItsEnd_IsReadWhole()
    {
        var input = new BufferedStream(new MemoryStream(Encoding.Latin1.GetBytes("%PDF-1.6\n" + Body)));
        input.Seek(0, SeekOrigin.End);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        Assert.Equal(3, result.RecoveredObjectCount);
    }

    /// <summary>
    /// A cross-reference stream whose entry 0 — the free-list head (§7.5.4) — claims an object in
    /// an object stream: object 0 is no object, and is skipped and reported as numbered outside the
    /// range, as one numbered past the ceiling is; never written into the body.
    /// </summary>
    [Fact]
    public void AnObjectStreamMemberNumberedZero_IsSkippedAndReported()
    {
        var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        var offsets = new long[4];

        Write("%PDF-1.5\n");
        offsets[1] = file.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        string data = "0 0 << /Stray true >>\n";
        offsets[3] = file.Position;
        Write($"3 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Length {data.Length} >>\nstream\n{data}\nendstream\nendobj\n");

        long xrefAt = file.Position;
        var rows = new MemoryStream();
        void Row(byte type, long f2, int f3)
        {
            rows.WriteByte(type);
            rows.WriteByte((byte)(f2 >> 24)); rows.WriteByte((byte)(f2 >> 16)); rows.WriteByte((byte)(f2 >> 8)); rows.WriteByte((byte)f2);
            rows.WriteByte((byte)(f3 >> 8)); rows.WriteByte((byte)f3);
        }
        Row(2, 3, 0);
        Row(1, offsets[1], 0);
        Row(1, offsets[2], 0);
        Row(1, offsets[3], 0);
        Row(1, xrefAt, 0);
        Write($"4 0 obj\n<< /Type /XRef /Size 5 /W [1 4 2] /Root 1 0 R /Length {rows.Length} >>\nstream\n");
        file.Write(rows.ToArray());
        Write($"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n");

        using var output = new MemoryStream();
        var result = PdfRepair.Repair(new MemoryStream(file.ToArray(), writable: false), output);

        Assert.Contains(result.Errors, e => e.Contains("numbered outside 1.."));
        string text = Encoding.Latin1.GetString(output.ToArray());
        Assert.DoesNotContain("/Stray", text);
        Assert.Contains("/Type /Pages", text);
    }
}
