// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Every way an object stream can fail to yield its members during decryption is reported through
/// <see cref="PdfDecryptor.LastObjectStreamRejection"/> — once, for the stream, with its reason.
/// </summary>
/// <remarks>
/// Three paths in <c>WriteDecrypted</c>'s expansion loop were silent about the stream. A stream the cross-reference
/// table names but the document cannot supply, and an object that turns out not to be a stream at all, were skipped
/// without a word; and a stream <c>ExtractObjectsFromStream</c> refused for its declared count set the diagnostic slot
/// only until the next stream's clean expansion cleared it. The per-object accounting still reported the members as
/// absent — but as members that "did not yield", without the reason, and one drop per member instead of one per cause.
/// </remarks>
public class ObjectStreamDropAccountingTests
{
    private static readonly byte[] FileId =
    {
        0x59, 0xC8, 0x13, 0xA0, 0x7F, 0x2E, 0xD6, 0x84,
        0x3B, 0xF9, 0x62, 0x0D, 0xB1, 0x47, 0xEC, 0x95
    };

    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PageTree = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";

    [Fact]
    public void EntryNamingAnObjectStreamTheDocumentDoesNotHave_ReportsTheStreamOnce()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(2, 0, PageTree);
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [11 0 R] >>");
        pdf.EncryptDictionary(5);
        pdf.Compressed(11, objectStreamNumber: 99, index: 0);   // no object 99 exists
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        string? dropped = Decrypt(pdf.ToArray(), out byte[] plain);

        Assert.NotNull(dropped);
        Assert.Contains("Object stream 99", dropped!, StringComparison.Ordinal);
        Assert.Contains("has no readable object of its own", dropped!, StringComparison.Ordinal);
        Assert.DoesNotContain("further object(s)", dropped!, StringComparison.Ordinal);   // one cause, one drop
        Assert.Equal(1, PageCount(plain));
    }

    [Fact]
    public void EntryNamingAnObjectThatIsNotAStream_ReportsTheStreamOnce()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(2, 0, PageTree);
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [11 0 R] >>");
        pdf.EncryptDictionary(5);
        pdf.Object(8, 0, "<< /Type /ObjStm /N 1 /First 4 >>");   // a dictionary, whatever it claims
        pdf.Compressed(11, objectStreamNumber: 8, index: 0);
        pdf.EndRevision(xrefStreamObjectNumber: 9, rootObjectNumber: 1, encryptObjectNumber: 5);

        string? dropped = Decrypt(pdf.ToArray(), out byte[] plain);

        Assert.NotNull(dropped);
        Assert.Contains("Object stream 8", dropped!, StringComparison.Ordinal);
        Assert.Contains("is not a stream", dropped!, StringComparison.Ordinal);
        Assert.DoesNotContain("further object(s)", dropped!, StringComparison.Ordinal);
        Assert.Equal(1, PageCount(plain));
    }

    /// <summary>
    /// Stream 6 is refused for its declared <c>/N</c> and stream 8 then expands cleanly. Their members' object numbers
    /// (4, then 7) put 6 before 8 in the expansion order, so 8's clean expansion is what used to erase 6's refusal.
    /// </summary>
    [Fact]
    public void ObjectStreamRefusedForItsDeclaredCount_IsReportedEvenWhenALaterStreamExpandsCleanly()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(2, 0, PageTree);
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R]" +
                         " /Resources << /Font << /F1 7 0 R >> >> >>");
        pdf.EncryptDictionary(5);
        pdf.ObjectStreamDeclaring(6, 40_000_000, (4, "<< /Type /Annot /Subtype /Text >>"));
        pdf.Compressed(4, objectStreamNumber: 6, index: 0);
        pdf.ObjectStream(8, (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        pdf.EndRevision(xrefStreamObjectNumber: 9, rootObjectNumber: 1, encryptObjectNumber: 5);

        string? dropped = Decrypt(pdf.ToArray(), out byte[] plain);

        Assert.NotNull(dropped);
        Assert.Contains("Object stream 6", dropped!, StringComparison.Ordinal);
        Assert.Contains("40000000", dropped!, StringComparison.Ordinal);                  // the reason survived stream 8
        Assert.DoesNotContain("further object(s)", dropped!, StringComparison.Ordinal);   // member 4 is not counted again

        using var doc = PdfDocument.Open(new MemoryStream(plain));
        Assert.Equal(1, doc.Pages.Count);
        Assert.IsAssignableFrom<PdfDictionary>(doc.Parser.GetObject(7));   // stream 8 did expand
    }

    private static string? Decrypt(byte[] pdf, out byte[] plain)
    {
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(pdf), output, "");
        plain = output.ToArray();
        return PdfDecryptor.LastObjectStreamRejection;
    }

    private static int PageCount(byte[] plain)
    {
        using var doc = PdfDocument.Open(new MemoryStream(plain));
        return doc.Pages.Count;
    }
}
