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
/// A cross-reference stream is never encrypted — ISO 32000-2 §7.5.8.2: "The cross-reference stream shall not be
/// encrypted and strings appearing in the cross-reference stream dictionary shall not be encrypted" — and
/// <c>PdfDecryptor</c> must not try.
/// </summary>
/// <remarks>
/// Step 1 of <c>WriteDecrypted</c> walked every type-1 entry, the stream's own included, and handed its data to the
/// cipher; the stream was only discarded afterwards, by <c>/Type /XRef</c>. Under RC4 that made harmless noise. Under
/// AES the data is read as IV + ciphertext, so whenever its length is 16 + 16·k the PKCS#7 unpad runs and fails, and
/// the whole decrypt throws. 16 rows of 7 bytes is that length; 15 rows is the control, so a fix that merely tolerated
/// the throw would be seen for what it is.
/// </remarks>
public class CrossReferenceStreamNotDecryptedTests
{
    private const string Marker = "content-behind-a-cross-reference-stream";

    private static readonly byte[] FileId =
    {
        0x3A, 0x7C, 0x91, 0x08, 0x5E, 0xD4, 0x2B, 0xF6,
        0x60, 0x19, 0xA7, 0xC3, 0x8D, 0x4F, 0xE2, 0x75
    };

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    public void AesDocument_WhoseCrossReferenceStreamHasThisManyRows_Decrypts(int rows)
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId, LegacyCipher.Aes128);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        pdf.Stream(4, 0, $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET");
        pdf.EncryptDictionary(5);
        for (int n = 6; n <= rows - 2; n++) pdf.Object(n, 0, $"<< /Filler {n} >>");
        pdf.EndRevision(xrefStreamObjectNumber: rows - 1, rootObjectNumber: 1, encryptObjectNumber: 5);
        byte[] encrypted = pdf.ToArray();

        // The fixture's shape: object 0, objects 1..rows-2 and the stream itself, seven bytes a row.
        using (var doc = PdfDocument.Open(new MemoryStream(encrypted)))
            Assert.Equal(rows * 7, Assert.IsType<PdfStream>(doc.Parser.GetObject(rows - 1)).RawData.Length);

        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), output, "");
        byte[] plain = output.ToArray();

        using var decrypted = PdfDocument.Open(new MemoryStream(plain));
        Assert.Contains(Marker, Encoding.ASCII.GetString(decrypted.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
        Assert.DoesNotContain("/XRef", Encoding.ASCII.GetString(plain), StringComparison.Ordinal);   // and it is not carried
    }
}
