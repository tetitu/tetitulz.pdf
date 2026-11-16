// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Repairing a PDF 1.5+ file keeps its pages, and repairing an encrypted file keeps it decryptable.
/// </summary>
/// <remarks>
/// <para><b>Found by running repair over the local corpus: four real files came back with no
/// pages</b> (1, 2, 353 and 527 pages before). The repair copies every <c>N G obj</c> it can find
/// and writes a classic cross-reference table, and three things a PDF 1.5 file does were invisible
/// to that:</para>
/// <list type="bullet">
///   <item>An object inside an object stream has no header of its own, so the catalog, the page
///   tree and the pages were simply not in the output.</item>
///   <item>A cross-reference stream file has no <c>trailer</c> keyword, so <c>/Root</c> came from a
///   search for <c>/Type /Catalog</c> in the first 512 bytes <i>from</i> each object — which ran on
///   past a short object into the next one and named the document's <c>/Info</c> dictionary as its
///   catalog.</item>
///   <item>The rebuilt trailer carried only <c>/Size</c>, <c>/Root</c> and <c>/Info</c>, so an
///   encrypted document lost <c>/Encrypt</c> and <c>/ID</c> and could no longer be decrypted.</item>
/// </list>
/// </remarks>
public sealed class RepairModernFileTests
{
    private const string Content = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET";

    private const string IdHex = "00112233445566778899AABBCCDDEEFF";

    private static byte[] Repair(byte[] pdf, out PdfRepairResult result)
    {
        using var output = new MemoryStream();
        result = PdfRepair.Repair(new MemoryStream(pdf, writable: false), output);
        return output.ToArray();
    }

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return doc.Pages.Count;
    }

    [Fact]
    public void AFileWhoseCatalogAndPagesLiveInAnObjectStream_KeepsItsPage()
    {
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: true);
        Assert.Equal(1, PageCount(pdf));    // the control: the original opens

        byte[] repaired = Repair(pdf, out var result);

        Assert.Equal(1, PageCount(repaired));
        Assert.True(result.IsClean, string.Join("; ", result.Errors));
        Assert.Contains("Hello", PdfOperations.ExtractText(new MemoryStream(repaired)));

        // Neither container survives: the objects were written out one by one, and a classic table
        // cannot point into a stream or use a cross-reference stream's stale offsets.
        string text = Encoding.Latin1.GetString(repaired);
        Assert.DoesNotContain("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /XRef", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootNamingAMissingObject_FindsTheCatalogInsideAnObjectStream()
    {
        // No document can open this file — its /Root names nothing — but the parser still reads its
        // cross-reference stream, and through it the object stream the catalog lives in.
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: true, rootObjectNumber: 99);

        byte[] repaired = Repair(pdf, out _);

        Assert.Equal(1, PageCount(repaired));
    }

    [Fact]
    public void AnObjectStreamMemberNumberedOutsideTheTable_IsSkippedAndReported()
    {
        // One entry numbered two million would make the rebuilt table two million rows long.
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: true, outOfRangeMember: true);

        byte[] repaired = Repair(pdf, out var result);

        Assert.Equal(1, PageCount(repaired));
        Assert.Contains("1 object(s) inside object streams numbered outside 1..1000000 were skipped.", result.Errors);
        Assert.True(repaired.Length < 2 * pdf.Length, $"the repaired file is {repaired.Length} bytes");
    }

    [Fact]
    public void AFileWithACrossReferenceStreamAndAShortInfoObjectFirst_KeepsItsCatalogAsRoot()
    {
        // No object stream: every object has a header. Only the root choice can go wrong.
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: false);
        Assert.Equal(1, PageCount(pdf));

        byte[] repaired = Repair(pdf, out _);

        using var doc = PdfDocument.Open(new MemoryStream(repaired, writable: false));
        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Equal(1, doc.Pages.Count);
    }

    [Fact]
    public void AnObjectStreamThatCannotBeRead_IsReportedRatherThanDroppedSilently()
    {
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: true, unreadableObjectStream: true);

        Repair(pdf, out var result);

        Assert.Contains(result.Errors, e => e.StartsWith("4 object(s) inside object streams could not be read", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.StartsWith("1 object stream(s) were not read", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEncryptedClassicFile_KeepsItsEncryptionDictionaryAndIdentifier()
    {
        byte[] pdf = BuildEncryptedClassicFile(includeEncryptionDictionary: true);

        byte[] repaired = Repair(pdf, out _);

        AssertEncryptionCarried(repaired);
    }

    [Fact]
    public void AnEncryptedFileWithADirectEncryptionDictionary_KeepsIt()
    {
        // Table 15 does not require the trailer's /Encrypt to be an indirect reference.
        byte[] pdf = BuildEncryptedClassicFile(includeEncryptionDictionary: false, directEncryptionDictionary: true);

        byte[] repaired = Repair(pdf, out _);

        AssertEncryptionCarried(repaired);
    }

    [Fact]
    public void AnEncryptedFileWithACrossReferenceStreamAndNoObjectStreams_KeepsItsEncryption()
    {
        // The encryption dictionary and the identifier are named only by the cross-reference stream.
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: false, encrypted: true);

        byte[] repaired = Repair(pdf, out _);

        AssertEncryptionCarried(repaired);
    }

    [Fact]
    public void AnEncryptedFileWithObjectStreams_IsRefusedRatherThanRepairedIntoAnUnreadableOne()
    {
        // The objects inside an encrypted object stream cannot be read without the password, and a
        // classic cross-reference table cannot point into the stream, so a repair would lose them.
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: true, encrypted: true);

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfRepair.Repair(new MemoryStream(pdf, writable: false), output));
        Assert.Contains("object streams", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedFileWhoseObjectStreamHeaderIsDamaged_IsStillRefused()
    {
        // The scan cannot find the container, but the cross-reference stream still says objects live
        // in it — and without the password they are lost either way.
        byte[] pdf = BuildXrefStreamFile(pagesInObjectStream: true, encrypted: true, damagedObjectStreamHeader: true);

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfRepair.Repair(new MemoryStream(pdf, writable: false), output));
        Assert.Contains("object streams", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedFileWithAnObjectStreamItsTableDoesNotList_IsRefused()
    {
        // A classic table names the container as an ordinary object and nothing inside it — as a
        // hybrid file does once its /XRefStm is lost.
        byte[] pdf = BuildEncryptedClassicFile(includeEncryptionDictionary: true, includeObjectStream: true);

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfRepair.Repair(new MemoryStream(pdf, writable: false), output));
        Assert.Contains("object streams", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedFileWhoseEncryptionDictionaryIsMissing_IsRefused()
    {
        // Every object would be copied still encrypted, under a trailer that cannot say how.
        byte[] pdf = BuildEncryptedClassicFile(includeEncryptionDictionary: false);

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfRepair.Repair(new MemoryStream(pdf, writable: false), output));
        Assert.Contains("encryption dictionary is not among the objects found", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertEncryptionCarried(byte[] repaired)
    {
        using var doc = PdfDocument.Open(new MemoryStream(repaired, writable: false));
        Assert.True(doc.IsEncrypted);
        var id = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Trailer[PdfName.ID]));
        Assert.Equal(2, id.Count);
        Assert.Equal(IdHex, Convert.ToHexString(Assert.IsType<PdfString>(id[0]).RawBytes));
        Assert.Equal(PdfName.Standard, doc.EncryptionDictionary.GetName(PdfName.Filter));
    }

    // ---------------------------------------------------------------- fixtures

    private static string Page(int pagesNum, int contentNum, int fontNum) =>
        $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 612 792] /Contents {contentNum} 0 R " +
        $"/Resources << /Font << /F1 {fontNum} 0 R >> >> >>";

    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    private const string EncryptionDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
        "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
        "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    private const int OutOfRangeNumber = 2_000_000;

    /// <summary>
    /// A PDF 1.5 file with a cross-reference stream and no <c>trailer</c> keyword. Object 1 is a
    /// short <c>/Info</c> dictionary, so a catalog search that runs past it finds object 2's text.
    /// </summary>
    /// <param name="pagesInObjectStream">Keep the catalog, the page tree and the page in object 7.</param>
    /// <param name="encrypted">Name an encryption dictionary and an identifier.</param>
    /// <param name="unreadableObjectStream">Declare object 7 Flate-encoded while its data is not.</param>
    /// <param name="rootObjectNumber">What the cross-reference stream's <c>/Root</c> names.</param>
    /// <param name="outOfRangeMember">Also keep an object numbered two million in object 7.</param>
    /// <param name="damagedObjectStreamHeader">Spell object 7's keyword <c>ob</c>.</param>
    private static byte[] BuildXrefStreamFile(
        bool pagesInObjectStream, bool encrypted = false, bool unreadableObjectStream = false,
        int rootObjectNumber = 2, bool outOfRangeMember = false, bool damagedObjectStreamHeader = false)
    {
        //  1 Info (regular)       2 catalog     3 pages       4 page
        //  5 content (regular)    6 font (regular)
        //  7 object stream holding 2..4 and a null object 10 when pagesInObjectStream
        //  8 encryption dictionary when encrypted      9 the cross-reference stream
        var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        var offsets = new Dictionary<int, long>();
        void Obj(int num, string body) { offsets[num] = file.Position; Write($"{num} 0 obj\n{body}\nendobj\n"); }

        Write("%PDF-1.5\n");
        Obj(1, "<< /Producer (Probe) >>");

        string catalog = "<< /Type /Catalog /Pages 3 0 R >>";
        string pages = "<< /Type /Pages /Kids [4 0 R] /Count 1 >>";
        string page = Page(3, 5, 6);
        if (!pagesInObjectStream)
        {
            Obj(2, catalog);
            Obj(3, pages);
            Obj(4, page);
        }

        offsets[5] = file.Position;
        Write($"5 0 obj\n<< /Length {Content.Length} >>\nstream\n{Content}\nendstream\nendobj\n");
        Obj(6, Font);

        // A null object is what its stream holds, not a failure to read one (§7.3.9).
        var members = new List<(int Num, string Body)>();
        if (pagesInObjectStream)
        {
            members.AddRange(new[] { (2, catalog), (3, pages), (4, page), (10, "null") });
            if (outOfRangeMember) members.Add((OutOfRangeNumber, "<< /Stray true >>"));

            var header = new StringBuilder();
            var bodies = new StringBuilder();
            foreach (var (num, body) in members)
            {
                header.Append(num).Append(' ').Append(bodies.Length).Append(' ');
                bodies.Append(body).Append('\n');
            }
            string data = header.ToString() + bodies;
            string filter = unreadableObjectStream ? " /Filter /FlateDecode" : "";
            string keyword = damagedObjectStreamHeader ? "ob" : "obj";
            offsets[7] = file.Position;
            Write($"7 0 {keyword}\n<< /Type /ObjStm /N {members.Count} /First {header.Length}{filter} /Length {data.Length} >>\nstream\n{data}\nendstream\nendobj\n");
        }

        if (encrypted)
            Obj(8, EncryptionDictionary);

        long xrefOffset = file.Position;
        var xref = new MemoryStream();
        void Entry(byte type, uint f2, ushort f3)
        {
            xref.WriteByte(type);
            xref.WriteByte((byte)(f2 >> 24)); xref.WriteByte((byte)(f2 >> 16));
            xref.WriteByte((byte)(f2 >> 8)); xref.WriteByte((byte)f2);
            xref.WriteByte((byte)(f3 >> 8)); xref.WriteByte((byte)f3);
        }

        Entry(0, 0, 0xFFFF);
        for (int num = 1; num <= 10; num++)
        {
            int member = members.FindIndex(m => m.Num == num);
            if (member >= 0) Entry(2, 7, (ushort)member);
            else if (num == 9) Entry(1, (uint)xrefOffset, 0);
            else if (offsets.TryGetValue(num, out long off)) Entry(1, (uint)off, 0);
            else Entry(0, 0, 0xFFFF);
        }

        string sections = "";
        int size = 11;
        if (outOfRangeMember)
        {
            Entry(2, 7, (ushort)members.FindIndex(m => m.Num == OutOfRangeNumber));
            sections = $" /Index [0 11 {OutOfRangeNumber} 1]";
            size = OutOfRangeNumber + 1;
        }

        byte[] xrefBytes = xref.ToArray();
        string encrypt = encrypted ? $" /Encrypt 8 0 R /ID [<{IdHex}> <{IdHex}>]" : "";
        Write($"9 0 obj\n<< /Type /XRef /Size {size}{sections} /W [1 4 2] /Root {rootObjectNumber} 0 R /Info 1 0 R{encrypt} /Length {xrefBytes.Length} >>\nstream\n");
        file.Write(xrefBytes);
        Write("\nendstream\nendobj\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>
    /// A classic one-page file whose trailer names an encryption dictionary (object 7) and an
    /// identifier.
    /// </summary>
    /// <param name="includeEncryptionDictionary">False leaves object 7 out.</param>
    /// <param name="includeObjectStream">Add object 8, an object stream the table lists as an ordinary object.</param>
    /// <param name="directEncryptionDictionary">Write the dictionary itself in the trailer, not a reference to object 7.</param>
    private static byte[] BuildEncryptedClassicFile(
        bool includeEncryptionDictionary, bool includeObjectStream = false, bool directEncryptionDictionary = false)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 3 0 R >>",
            "<< /Producer (Probe) >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            Page(3, 5, 6),
            $"<< /Length {Content.Length} >>\nstream\n{Content}\nendstream",
            Font,
        };
        if (includeEncryptionDictionary) bodies.Add(EncryptionDictionary);
        if (includeObjectStream)
        {
            const string data = "11 0 << >>";
            bodies.Add($"<< /Type /ObjStm /N 1 /First 5 /Length {data.Length} >>\nstream\n{data}\nendstream");
        }

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        string encrypt = directEncryptionDictionary ? EncryptionDictionary : "7 0 R";
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append($" /Root 1 0 R /Info 2 0 R /Encrypt {encrypt} ")
          .Append($"/ID [<{IdHex}> <{IdHex}>] >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
