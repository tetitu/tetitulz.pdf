// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Metadata;

/// <summary>
/// The one revision that makes an XMP packet the document's <c>/Metadata</c>, which
/// <c>PdfMetadataWriter.WriteXmpMetadata</c>, <c>PdfDeclarationsWriter</c> and the Accessibility
/// feature's <c>PdfUaXmpWriter</c> now all write through.
/// </summary>
public class XmpMetadataRevisionTests
{
    private const string Packet = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>";

    /// <summary>
    /// <c>WriteXmpMetadata</c> wrote the catalog at generation 0 under a trailer that still named
    /// <c>1 1 R</c>, so a strict reader found no catalog. The revision keeps the catalog's generation.
    /// </summary>
    [Fact]
    public void WriteXmpMetadata_KeepsTheCatalogsGeneration()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .ObjAtGeneration(1, 1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .Finish(root: "1 1 R");

        using var output = new MemoryStream();
        PdfMetadataWriter.WriteXmpMetadata(new MemoryStream(pdf), output, Packet);

        string appended = Encoding.Latin1.GetString(output.ToArray(), pdf.Length, (int)output.Length - pdf.Length);
        Assert.Matches(@"(^|\n)1 1 obj", appended);
        Assert.DoesNotMatch(@"(^|\n)1 0 obj", appended);
    }

    /// <summary>
    /// A document whose cross-reference section is a stream gets a stream: <c>WriteXmpMetadata</c>
    /// appended a classic table, a hybrid strict validators reject.
    /// </summary>
    [Fact]
    public void WriteXmpMetadata_AppendsASectionOfTheFormItSupersedes()
    {
        byte[] pdf = XrefStreamPdf();

        using var output = new MemoryStream();
        PdfMetadataWriter.WriteXmpMetadata(new MemoryStream(pdf), output, Packet);

        string appended = Encoding.Latin1.GetString(output.ToArray(), pdf.Length, (int)output.Length - pdf.Length);
        Assert.Contains("/Type /XRef", appended, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(^|\n)xref\s", appended);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var metadata = Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata]));
        Assert.Equal(Packet, Encoding.UTF8.GetString(metadata.GetDecodedData()));
    }

    /// <summary>A document with a /Metadata stream has it superseded in place, not a second one added.</summary>
    [Fact]
    public void AnExistingMetadataStreamIsSupersededAtItsOwnNumber()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .StreamObj(4, "<< /Type /Metadata /Subtype /XML", "<old/>")
            .Finish();

        using var output = new MemoryStream();
        PdfMetadataWriter.WriteXmpMetadata(new MemoryStream(pdf), output, Packet);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var reference = Assert.IsType<PdfReference>(doc.Catalog.Dictionary[PdfName.Metadata]);
        Assert.Equal(4, reference.ReferenceObjectNumber);
        var metadata = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(4));
        Assert.Equal(Packet, Encoding.UTF8.GetString(metadata.GetDecodedData()));
    }

    /// <summary>
    /// <c>/Metadata 2 0 R</c> naming the page tree: the reference's number was taken on trust, so
    /// the packet superseded object 2 and the document lost its pages. The page tree survives, and
    /// the packet goes into a new object the catalog is repointed at.
    /// </summary>
    [Fact]
    public void AMetadataEntryNamingANonStreamIsNotOverwritten()
    {
        byte[] pdf = OnePage("/Metadata 2 0 R");

        using var output = new MemoryStream();
        PdfMetadataWriter.WriteXmpMetadata(new MemoryStream(pdf), output, Packet);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(1, doc.Pages.Count);
        Assert.IsType<PdfDictionary>(doc.GetObject(2));
        var reference = Assert.IsType<PdfReference>(doc.Catalog.Dictionary[PdfName.Metadata]);
        Assert.Equal(4, reference.ReferenceObjectNumber);
        var metadata = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(4));
        Assert.Equal(Packet, Encoding.UTF8.GetString(metadata.GetDecodedData()));
    }

    /// <summary>
    /// A reference to an undefined object is <c>null</c> (§7.3.10). The packet was written at the
    /// number it named, however large; it now takes the next free number, and the malformed entry
    /// is reported.
    /// </summary>
    [Fact]
    public void AMetadataEntryNamingAnUndefinedObjectGetsANewObject()
    {
        byte[] pdf = OnePage("/Metadata 999999 0 R");
        using var source = PdfDocument.Open(new MemoryStream(pdf));
        var index = PdfObjectIndex.Build(source);
        var reports = new System.Collections.Generic.List<string>();

        XmpMetadataRevision.Build(source, index, source.Catalog.Dictionary, Packet,
            out var modified, out var created, (detail, _) => reports.Add(detail));

        Assert.Equal(new[] { 4 }, created.Keys);
        Assert.Equal(new[] { 1 }, modified.Keys);
        var catalog = Assert.IsType<PdfDictionary>(modified[1]);
        Assert.Equal(4, Assert.IsType<PdfReference>(catalog[PdfName.Metadata]).ReferenceObjectNumber);
        Assert.Contains(reports, r => r.Contains("does not name a stream", StringComparison.Ordinal));
    }

    /// <summary>
    /// A trailer whose <c>/Root</c> is a direct dictionary has no catalog object to revise, and the
    /// appended trailer would name none. Refused — while the same trailer over a document that
    /// already has a <c>/Metadata</c> stream is accepted, since that revision leaves the catalog alone.
    /// </summary>
    [Fact]
    public void ADirectRootIsRefusedOnlyWhenTheCatalogMustBeRevised()
    {
        const string directRoot = "<< /Type /Catalog /Pages 2 0 R >>";
        byte[] withoutMetadata = new PdfFixtureBuilder().Header()
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .Finish(root: directRoot);
        byte[] withMetadata = new PdfFixtureBuilder().Header()
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .StreamObj(4, "<< /Type /Metadata /Subtype /XML", "<old/>")
            .Finish(root: "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>");

        var refusal = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfMetadataWriter.WriteXmpMetadata(new MemoryStream(withoutMetadata), new MemoryStream(), Packet));
        Assert.Contains("/Root is not an indirect reference", refusal.Message, StringComparison.Ordinal);

        using var output = new MemoryStream();
        PdfMetadataWriter.WriteXmpMetadata(new MemoryStream(withMetadata), output, Packet);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var metadata = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(4));
        Assert.Equal(Packet, Encoding.UTF8.GetString(metadata.GetDecodedData()));
    }

    // ------------------------------------------------------------------

    private static byte[] OnePage(string catalogExtras) => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R " + catalogExtras + " >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
        .Finish();

    /// <summary>A one-page document whose only cross-reference section is a stream.</summary>
    internal static byte[] XrefStreamPdf()
    {
        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new int[4];
        offsets[1] = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = sb.Length; sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int xrefAt = sb.Length;

        // /W [1 4 1]: type, offset, generation — five rows (0..4, the fourth being this stream).
        var rows = new System.Collections.Generic.List<byte>();
        void Row(byte type, int field2, byte field3)
        {
            rows.Add(type);
            rows.Add((byte)(field2 >> 24)); rows.Add((byte)(field2 >> 16));
            rows.Add((byte)(field2 >> 8)); rows.Add((byte)field2);
            rows.Add(field3);
        }
        Row(0, 0, 255);
        Row(1, offsets[1], 0);
        Row(1, offsets[2], 0);
        Row(1, offsets[3], 0);
        Row(1, xrefAt, 0);

        byte[] head = Encoding.Latin1.GetBytes(sb.ToString());
        byte[] dict = Encoding.Latin1.GetBytes(
            $"4 0 obj\n<< /Type /XRef /Size 5 /W [1 4 1] /Root 1 0 R /Length {rows.Count} >>\nstream\n");
        byte[] tail = Encoding.Latin1.GetBytes($"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n");

        var all = new byte[head.Length + dict.Length + rows.Count + tail.Length];
        head.CopyTo(all, 0);
        dict.CopyTo(all, head.Length);
        rows.CopyTo(all, head.Length + dict.Length);
        tail.CopyTo(all, head.Length + dict.Length + rows.Count);
        return all;
    }
}
