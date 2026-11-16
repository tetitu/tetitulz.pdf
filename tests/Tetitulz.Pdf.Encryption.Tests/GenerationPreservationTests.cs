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
/// The three whole-file writers in this assembly keep each object's generation number: in the <c>N G obj</c> header,
/// in the cross-reference entry, and under the per-object key.
/// </summary>
/// <remarks>
/// <para>ISO 32000-2 §7.3.10: "Together, the combination of an object number and a generation number shall uniquely
/// identify an indirect object", and "An indirect reference to an undefined object shall not be considered an error by a
/// PDF processor; it shall be treated as a reference to the null object." §7.5.4 gives a reused object number a new
/// generation so that references to the freed object cannot alias the new one. All three writers used to emit every
/// object as <c>N 0 obj</c> with a generation-0 cross-reference entry while leaving every <c>N G R</c> in the file as
/// it was — so each such reference named an identifier the output no longer defined, and a conforming reader resolved
/// it to null. Rewriting the references instead would resurrect the stale ones §7.5.4 exists to retire.</para>
/// <para>Objects stored in an object stream stay at generation 0: §7.5.7, "The generation number of an object stream
/// and of any compressed object shall be zero."</para>
/// </remarks>
public class GenerationPreservationTests
{
    private const string Note = "annotation-at-generation-one";
    private const string UserPassword = "u";

    private static readonly byte[] FileId =
    {
        0x8E, 0x21, 0xB7, 0x4C, 0x03, 0xD9, 0x66, 0xAF,
        0x15, 0xF2, 0x7B, 0x38, 0xC0, 0x5D, 0x94, 0xEB
    };

    public static TheoryData<EncryptionAlgorithm> PasswordAlgorithms => new()
    {
        EncryptionAlgorithm.RC4_40, EncryptionAlgorithm.RC4_128, EncryptionAlgorithm.AES128,
        EncryptionAlgorithm.AES256, EncryptionAlgorithm.AES256GCM,
    };

    [Fact]
    public void Decrypt_WritesAReusedObjectNumberAtTheGenerationItsReferencesName()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 1 R] >>");
        pdf.EncryptDictionary(5);
        pdf.Object(6, 1, $"<< /Type /Annot /Subtype /Text /Contents {pdf.EncryptedString(6, 1, Note)} >>");
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        byte[] plain = Decrypt(pdf.ToArray(), "");
        string text = Encoding.Latin1.GetString(plain);
        Assert.Contains("6 1 R", text, StringComparison.Ordinal);     // the reference, as the file made it
        Assert.Contains("6 1 obj", text, StringComparison.Ordinal);   // the object it names

        using var doc = PdfDocument.Open(new MemoryStream(plain));
        Assert.Equal(1, doc.CrossReferenceTable.GetEntry(6).GenerationNumber);
        var annots = Assert.IsType<PdfArray>(doc.Pages[0].Dictionary[PdfName.Annots]);
        var annot = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(annots[0]));
        Assert.Equal(Note, Encoding.ASCII.GetString(Assert.IsType<PdfString>(annot[PdfName.Contents]).RawBytes));
    }

    /// <summary>The control for the rule's other half: a compressed object comes out at generation 0.</summary>
    [Fact]
    public void Decrypt_WritesAnObjectStreamMemberAtGenerationZero()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        pdf.EncryptDictionary(5);
        pdf.ObjectStream(6, (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        byte[] plain = Decrypt(pdf.ToArray(), "");
        Assert.Contains("2 0 obj", Encoding.Latin1.GetString(plain), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(plain));
        Assert.Equal(0, doc.CrossReferenceTable.GetEntry(2).GenerationNumber);
        Assert.Equal(1, doc.Pages.Count);
    }

    [Theory]
    [MemberData(nameof(PasswordAlgorithms))]
    public void Encrypt_WritesAReusedObjectNumberAtItsGeneration_AndKeysItThere(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(TestPdfs.Plaintext.Build(contentGeneration: 3), algorithm);
        AssertContentStreamIsAtGenerationThree(encrypted);

        // Keyed under the generation the file now states: the content decrypts.
        Assert.Contains(TestPdfs.Plaintext.Marker, PageContent(Decrypt(encrypted, UserPassword)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void PublicKeyEncrypt_WritesAReusedObjectNumberAtItsGeneration_AndKeysItThere(EncryptionAlgorithm algorithm)
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        using var input = new MemoryStream(TestPdfs.Plaintext.Build(contentGeneration: 3));
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, certDer, PdfPermissions.All, algorithm);
        byte[] encrypted = output.ToArray();

        AssertContentStreamIsAtGenerationThree(encrypted);
        Assert.Contains(TestPdfs.Plaintext.Marker, PageContent(PublicKeyRoundTripTests.Decrypt(encrypted, pfx)), StringComparison.Ordinal);
    }

    private static void AssertContentStreamIsAtGenerationThree(byte[] encrypted)
    {
        string text = Encoding.Latin1.GetString(encrypted);
        Assert.Contains("4 3 obj", text, StringComparison.Ordinal);
        Assert.Contains("/Contents 4 3 R", text, StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        Assert.Equal(3, doc.CrossReferenceTable.GetEntry(4).GenerationNumber);
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

    private static string PageContent(byte[] plain)
    {
        using var doc = PdfDocument.Open(new MemoryStream(plain));
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
    }
}
