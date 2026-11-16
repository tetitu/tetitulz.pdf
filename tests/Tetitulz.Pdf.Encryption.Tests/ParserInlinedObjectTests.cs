// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <c>PdfParser</c> resolves a stream's indirect <c>/Filter</c>, <c>/DecodeParms</c> and the entries inside the latter
/// into the stream dictionary while reading. Both writers in this assembly must put the <b>reference</b> back where
/// the file had one, rather than writing the resolved object inline.
/// </summary>
/// <remarks>
/// <para>Written inline, a resolved <c>/JBIG2Globals</c> stream keeps only its dictionary — <c>PdfWriter</c> can give a
/// nested stream no data — so the image loses its globals and the reference to the object that still holds them. And
/// an inline copy is keyed wrongly by construction: the encryptor enciphered it under the object it was resolved from,
/// while every reader — this library's decryptor included — deciphers what sits inside object N under N's key
/// (ISO 32000-2 §7.6.3.2, Algorithm 1 step (a)). Under RC4 that is a garbled string; under AES the whole decrypt
/// fails PKCS#7 padding.</para>
/// <para>The rule is restricted to containers and strings, which the parser creates afresh per object. <c>true</c>,
/// <c>false</c> and <c>null</c> are shared instances that the parser stamps with whatever object number last held one,
/// so their object number says nothing about where they came from — and inline they are correct anyway.</para>
/// </remarks>
public class ParserInlinedObjectTests
{
    private const string UserPassword = "u";

    private static readonly byte[] FileId =
    {
        0x47, 0xB2, 0x0E, 0x9D, 0x63, 0xF1, 0x28, 0xC5,
        0x7A, 0x14, 0xE8, 0x3B, 0x96, 0x50, 0xAD, 0x0C
    };

    public static TheoryData<EncryptionAlgorithm> PasswordAlgorithms => new()
    {
        EncryptionAlgorithm.RC4_40, EncryptionAlgorithm.RC4_128, EncryptionAlgorithm.AES128,
        EncryptionAlgorithm.AES256, EncryptionAlgorithm.AES256GCM,
    };

    [Theory]
    [InlineData(LegacyCipher.Rc4With40BitKey)]
    [InlineData(LegacyCipher.Aes128)]
    public void Decrypt_WritesAResolvedDecodeParmsAsTheReferenceItWas_AndItsGlobalsStreamAsItsOwnObject(LegacyCipher cipher)
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId, cipher);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [] /Count 0 >>");
        pdf.EncryptDictionary(5);
        pdf.Stream(6, 0, "image-samples", "/Type /XObject /Subtype /Image /DecodeParms 7 0 R");
        pdf.Object(7, 0, $"<< /JBIG2Globals 8 0 R /T {pdf.EncryptedString(7, 0, TestPdfs.Plaintext.DecodeParmsMarker)} >>");
        pdf.Stream(8, 0, TestPdfs.Plaintext.GlobalsData);
        pdf.EndRevision(xrefStreamObjectNumber: 9, rootObjectNumber: 1, encryptObjectNumber: 5);

        AssertChainIntact(Decrypt(pdf.ToArray(), ""));
    }

    [Theory]
    [MemberData(nameof(PasswordAlgorithms))]
    public void EncryptThenDecrypt_KeepsAResolvedDecodeParmsChainIntact(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(TestPdfs.Plaintext.Build(indirectDecodeParms: true), algorithm);

        string cipherText = Encoding.Latin1.GetString(encrypted);
        Assert.Contains("/DecodeParms 7 0 R", cipherText, StringComparison.Ordinal);      // the encryptor wrote the reference
        Assert.Contains("/JBIG2Globals 8 0 R", cipherText, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPdfs.Plaintext.DecodeParmsMarker, cipherText, StringComparison.Ordinal);   // and enciphered the string

        AssertChainIntact(Decrypt(encrypted, UserPassword));
    }

    /// <summary>
    /// The compressed layout's writer and the one that keeps the original encryption write the graph as the
    /// parser left it; the references go back before either sees it.
    /// </summary>
    [Fact]
    public void EncryptIntoObjectStreams_ThenDecrypt_KeepsAResolvedDecodeParmsChainIntact()
    {
        using var input = new MemoryStream(TestPdfs.Plaintext.Build(indirectDecodeParms: true));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword, OwnerPassword = "o", Algorithm = EncryptionAlgorithm.AES128, UseObjectStreams = true,
        });

        // The decode parameters are packed into an object stream the decryptor's own parser cannot
        // read while it is still encrypted, so the image keeps its reference to them rather than
        // the null the resolution answered.
        AssertChainIntact(Decrypt(output.ToArray(), UserPassword));
    }

    [Fact]
    public void KeepOriginalRoundTrip_KeepsAResolvedDecodeParmsChainIntact()
    {
        byte[] encrypted = Encrypt(TestPdfs.Plaintext.Build(indirectDecodeParms: true), EncryptionAlgorithm.AES128);

        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(input, output,
            new EncryptedRoundTripOptions { OpenPassword = UserPassword, Policy = ReEncryptionPolicy.KeepOriginal },
            (i, o) => i.CopyTo(o));
        Assert.True(result.Succeeded, result.FailureReason);

        AssertChainIntact(Decrypt(output.ToArray(), UserPassword));
    }

    /// <summary>
    /// A string the parser resolved into a decode-parameter dictionary is a top-level object too, and goes back as
    /// its reference — it was enciphered under its own identifier, not the image's.
    /// </summary>
    [Fact]
    public void Decrypt_WritesAStringTheParserResolvedIntoDecodeParms_AsTheReferenceItWas()
    {
        const string Resolved = "string-resolved-into-the-decode-parms";

        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [] /Count 0 >>");
        pdf.EncryptDictionary(5);
        pdf.Stream(6, 0, "image-samples", "/Type /XObject /Subtype /Image /DecodeParms << /K 7 0 R >>");
        pdf.Object(7, 0, pdf.EncryptedString(7, 0, Resolved));
        pdf.EndRevision(xrefStreamObjectNumber: 8, rootObjectNumber: 1, encryptObjectNumber: 5);

        byte[] plain = Decrypt(pdf.ToArray(), "");
        Assert.Contains("/K 7 0 R", Encoding.Latin1.GetString(plain), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(plain));
        Assert.Equal(Resolved, Encoding.ASCII.GetString(Assert.IsType<PdfString>(doc.Parser.GetObject(7)).RawBytes));
    }

    /// <summary>
    /// <c>true</c>, <c>false</c> and <c>null</c> are shared instances, and the parser stamps a top-level one with its
    /// object number — so a nested one may carry a number that says nothing about where it came from. It is written
    /// inline, as it is; a reference minted from that number would point at an unrelated object.
    /// </summary>
    [Fact]
    public void Decrypt_WritesASharedScalarInstanceInline_EvenAfterATopLevelObjectStampedIt()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [] /Count 0 >>");
        pdf.EncryptDictionary(3);
        pdf.Object(4, 0, "true");   // parsed before object 6: stamps the shared PdfBoolean.True with 4
        pdf.Object(5, 0, "null");   // and PdfNull.Instance with 5
        pdf.Object(6, 0, "<< /Type /Annot /Subtype /Text /Flag true /Missing null >>");
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 3);

        string text = Encoding.Latin1.GetString(Decrypt(pdf.ToArray(), ""));
        Assert.Contains("/Flag true", text, StringComparison.Ordinal);
        Assert.Contains("/Missing null", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Object 6's <c>/DecodeParms</c> is the reference <c>7 0 R</c>; object 7 names <c>8 0 R</c> and still holds its
    /// string; object 8 still holds its data. The raw text pins the structure, the parsed reads pin the content.
    /// </summary>
    internal static void AssertChainIntact(byte[] plain)
    {
        string text = Encoding.Latin1.GetString(plain);
        Assert.Contains("/DecodeParms 7 0 R", text, StringComparison.Ordinal);
        Assert.Contains("/JBIG2Globals 8 0 R", text, StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(plain));
        Assert.Equal(TestPdfs.Plaintext.GlobalsData,
            Encoding.ASCII.GetString(Assert.IsType<PdfStream>(doc.Parser.GetObject(8)).RawData));

        var parms = Assert.IsAssignableFrom<PdfDictionary>(doc.Parser.GetObject(7));
        Assert.Equal(TestPdfs.Plaintext.DecodeParmsMarker,
            Encoding.ASCII.GetString(Assert.IsType<PdfString>(parms[PdfName.T]).RawBytes));

        // Reached from the image, as a decoder would reach it — the parser resolves the reference into place again.
        var image = Assert.IsType<PdfStream>(doc.Parser.GetObject(6));
        var viaImage = Assert.IsAssignableFrom<PdfDictionary>(image[PdfName.DecodeParms]);
        Assert.Equal(TestPdfs.Plaintext.DecodeParmsMarker,
            Encoding.ASCII.GetString(Assert.IsType<PdfString>(viaImage[PdfName.T]).RawBytes));
    }

    private static byte[] Encrypt(byte[] plain, EncryptionAlgorithm algorithm)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword, OwnerPassword = "o", Algorithm = algorithm,
        });
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), output, password);
        return output.ToArray();
    }
}
