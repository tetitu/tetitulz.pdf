// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <c>PdfDecryptor.ExtractObjectsFromStream</c>, the second object-stream reader, parsed a member at an
/// offset an earlier member already holds as its own copy of those bytes — a few kilobytes of index
/// could hold a stream's largest object hundreds of times (the review of batch 131, 2026-10-15).
/// ISO 32000-2 §7.5.7: "The byte offsets shall be in increasing order." The member is not extracted,
/// and the decrypted output reports it absent, as <c>PdfParser.ParseObjectFromStream</c> refuses it.
/// </summary>
public class ObjectStreamSharedOffsetTests
{
    private static readonly byte[] FileId =
    {
        0x2C, 0x91, 0x5E, 0x07, 0xB3, 0x48, 0xDA, 0x16,
        0x7F, 0xE2, 0x39, 0x84, 0x0B, 0xC5, 0x62, 0xAD
    };

    private const string Marker = "content-of-the-one-page";

    /// <summary>
    /// Object stream 6 holds page 3 at offset 0, the page tree 2 after it, and object 9 named at
    /// offset 0 again. The page and its tree decrypt; object 9 is not in the output, and the drop
    /// accounting names it.
    /// </summary>
    [Fact]
    public void AMemberAtAnOffsetAnEarlierMemberHolds_IsNotExtracted_WhereTheOthersAre()
    {
        (byte[] decrypted, string? dropped) = Decrypt(Build(object9SharesPage3sOffset: true));

        Assert.Contains(Marker, PageContent(decrypted), StringComparison.Ordinal);
        using (var doc = PdfDocument.Open(new MemoryStream(decrypted)))
            Assert.Null(doc.CrossReferenceTable.GetEntry(9));
        Assert.NotNull(dropped);
        Assert.Contains("Object 9", dropped, StringComparison.Ordinal);
    }

    /// <summary>The control: object 9 at its own offset is extracted beside the others.</summary>
    [Fact]
    public void TheSameStreamWithItsOffsetsIncreasing_ExtractsEveryMember()
    {
        (byte[] decrypted, string? dropped) = Decrypt(Build(object9SharesPage3sOffset: false));

        Assert.Contains(Marker, PageContent(decrypted), StringComparison.Ordinal);
        using (var doc = PdfDocument.Open(new MemoryStream(decrypted)))
            Assert.NotNull(doc.CrossReferenceTable.GetEntry(9));
        Assert.Null(dropped);
    }

    private static byte[] Build(bool object9SharesPage3sOffset)
    {
        const string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\n";
        const string tree = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n";
        const string extra = "<< /Marker 9 >>\n";
        int treeOffset = page.Length;
        int extraOffset = object9SharesPage3sOffset ? 0 : page.Length + tree.Length;
        string index = $"3 0 2 {treeOffset} 9 {extraOffset} ";

        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Stream(4, 0, $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET");
        pdf.EncryptDictionary(5);
        pdf.ObjectStreamWithIndex(6, 3, index, page + tree + extra);
        pdf.Compressed(3, 6, 0);
        pdf.Compressed(2, 6, 1);
        pdf.Compressed(9, 6, 2);
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);
        return pdf.ToArray();
    }

    private static (byte[] Pdf, string? Dropped) Decrypt(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "");
        return (output.ToArray(), PdfDecryptor.LastObjectStreamRejection);
    }

    private static string PageContent(byte[] plainPdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(plainPdf));
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
    }
}
