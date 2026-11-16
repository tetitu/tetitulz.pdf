// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The two whole-file rewrites in this assembly — decryption and encryption — rebuild the trailer from the source's,
/// and must carry nothing that belonged to the source's own cross-reference section: its <c>/AuthCode</c> above all.
/// </summary>
/// <remarks>
/// <para>ISO/TS 32004 §6.5.1 has a standalone token's <c>/ByteRange</c> "cover the entire PDF file" as it stood when
/// the revision was sealed, Annex B.1 takes "only the AuthCode dictionary in the current (most recent) revision"
/// into account, and Table 5 requires <c>/V</c> "at least 5" wherever the entry is present. Carried into a rewrite the
/// token describes bytes that no longer exist: in a decrypted file it sits beside no <c>/Encrypt</c> at all, and in a
/// document re-encrypted without PDF MAC it is what Annex B.2.2.2 reads as possible tampering. Core's
/// <c>PdfTrailerBuilder</c> already drops it for an appended revision, for the same reason.</para>
/// <para><b>The two writers had drifted on the rest as well.</b> The decryptor dropped a cross-reference stream's own
/// keys (<c>/W</c>, <c>/Index</c>, <c>/Filter</c>, <c>/DecodeParms</c>, <c>/Length</c>); the encryptor dropped only
/// <c>/Type</c> and <c>/XRefStm</c>, and so wrote the others into a classic trailer, which §7.5.5 does not allow. One
/// predicate now answers for both.</para>
/// </remarks>
public class TrailerRewriteTests
{
    private const string UserPassword = "u";

    /// <summary>A token that cannot describe any file this suite writes.</summary>
    private const string StaleAuthCode = "/AuthCode << /MACLocation /Standalone /ByteRange [0 1 2 3] /MAC <00> >>";

    private static readonly PdfName[] CrossReferenceStreamKeys =
        { PdfName.Type, PdfName.W, PdfName.Index, PdfName.Filter, PdfName.DecodeParms, PdfName.Length };

    [Fact]
    public void Decrypt_OfAMacSealedDocument_CarriesNoAuthCodeIntoThePlaintext()
    {
        byte[] sealedDoc = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("trailer-rewrite"), EncryptionAlgorithm.AES256, pdfMac: true);
        Assert.Contains("/AuthCode", Encoding.Latin1.GetString(sealedDoc), StringComparison.Ordinal);   // there is one to carry

        byte[] plain = Decrypt(sealedDoc);
        using var doc = PdfDocument.Open(new MemoryStream(plain));
        Assert.Null(doc.Trailer[PdfName.AuthCode]);
        Assert.Null(doc.Trailer[PdfName.Encrypt]);
        Assert.NotNull(doc.Trailer[PdfName.Root]);                 // the rest of the trailer is still carried
        Assert.IsType<PdfArray>(doc.Trailer[PdfName.ID]);
        Assert.Contains("trailer-rewrite", Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    public void Encrypt_WithoutPdfMac_DropsTheSourcesAuthCode(EncryptionAlgorithm algorithm)
    {
        byte[] source = TestPdfs.Plaintext.Build(StaleAuthCode);
        using (var sourceDoc = PdfDocument.Open(new MemoryStream(source)))
            Assert.NotNull(sourceDoc.Trailer[PdfName.AuthCode]);   // there is one to carry

        byte[] encrypted = Encrypt(source, algorithm, pdfMac: false);
        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        Assert.Null(doc.Trailer[PdfName.AuthCode]);
        Assert.NotNull(doc.Trailer[PdfName.Encrypt]);
        Assert.NotNull(doc.Trailer[PdfName.Root]);
    }

    /// <summary>The control: a writer that seals writes its own token, over this file.</summary>
    [Fact]
    public void Encrypt_WithPdfMac_SealsWithItsOwnTokenAndNotTheSources()
    {
        byte[] encrypted = Encrypt(TestPdfs.Plaintext.Build(StaleAuthCode), EncryptionAlgorithm.AES256, pdfMac: true);

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var authCode = Assert.IsType<PdfDictionary>(doc.Trailer[PdfName.AuthCode]);
        var range = Assert.IsType<PdfArray>(authCode[PdfName.ByteRange]);
        Assert.Equal(4, range.Count);
        Assert.Equal(encrypted.Length, ((PdfNumber)range[2]).LongValue + ((PdfNumber)range[3]).LongValue);
    }

    [Fact]
    public void Encrypt_FromACrossReferenceStreamSource_WritesAClassicTrailerWithoutTheStreamsOwnKeys()
    {
        byte[] source = TestPdfs.Plaintext.WithCrossReferenceStream();
        using (var sourceDoc = PdfDocument.Open(new MemoryStream(source)))
        {
            Assert.Equal(PdfName.XRef, sourceDoc.Trailer[PdfName.Type]);   // the fixture's trailer IS the stream's dictionary
            Assert.Contains(TestPdfs.Plaintext.Marker,
                Encoding.ASCII.GetString(sourceDoc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
        }

        byte[] encrypted = Encrypt(source, EncryptionAlgorithm.AES128, pdfMac: false);
        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        foreach (PdfName key in CrossReferenceStreamKeys)
            Assert.True(doc.Trailer[key] == null, $"/{key.Value} was carried from a cross-reference stream into a classic trailer");
        Assert.NotNull(doc.Trailer[PdfName.Root]);
        Assert.NotNull(doc.Trailer[PdfName.Encrypt]);
        Assert.Contains(TestPdfs.Plaintext.Marker, Encoding.ASCII.GetString(Decrypt(encrypted)), StringComparison.Ordinal);
    }

    /// <summary>The decryptor's side of the same predicate, on a document whose trailer is a cross-reference stream's.</summary>
    [Fact]
    public void Decrypt_OfACrossReferenceStreamDocument_WritesAClassicTrailerWithoutTheStreamsOwnKeys()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(new byte[16]);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [] /Count 0 >>");
        pdf.EncryptDictionary(3);
        pdf.EndRevision(xrefStreamObjectNumber: 4, rootObjectNumber: 1, encryptObjectNumber: 3);

        using var doc = PdfDocument.Open(new MemoryStream(Decrypt(pdf.ToArray(), "")));
        foreach (PdfName key in CrossReferenceStreamKeys)
            Assert.True(doc.Trailer[key] == null, $"/{key.Value} was carried from a cross-reference stream into a classic trailer");
        Assert.NotNull(doc.Trailer[PdfName.Root]);
    }

    private static byte[] Encrypt(byte[] plain, EncryptionAlgorithm algorithm, bool pdfMac)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword, OwnerPassword = "o", Algorithm = algorithm, EnablePdfMac = pdfMac,
        });
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password = UserPassword)
    {
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), output, password);
        return output.ToArray();
    }
}
