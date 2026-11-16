// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Attachments in an <b>encrypted</b> document: <see cref="PdfEncryptedAttachments"/> appends them in
/// place, and ISO 32000-2 §7.6.5's <c>/EFF</c> — the embedded files' own crypt filter — is honoured on
/// both sides, including by <see cref="EncryptionParameters.EncryptOnlyAttachments"/>, which writes a
/// document that encrypts nothing else. The payloads are stored uncompressed, so the raw bytes show
/// plainly whether one was encrypted.
/// </summary>
public sealed class EncryptedAttachmentsTests
{
    private const string Password = "attach-pw-63";
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("Secret attachment payload 7d1e40");
    private static readonly byte[] Second = Encoding.ASCII.GetBytes("Second payload, appended 91c3aa");

    public EncryptedAttachmentsTests() => PdfEncryptionOperations.Register();

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void AnAttachmentIsAddedInPlace_Encrypted(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(OnePage(), algorithm);

        byte[] updated = Add(encrypted, "notes.txt", Payload);

        Assert.Equal(encrypted, updated.Take(encrypted.Length).ToArray());
        using (var doc = PdfDocument.Open(new MemoryStream(updated))) Assert.True(doc.IsEncrypted);
        string raw = Encoding.Latin1.GetString(updated);
        Assert.DoesNotContain(Encoding.ASCII.GetString(Payload), raw);
        Assert.DoesNotContain("notes.txt", raw);
        Assert.Equal(Payload, PdfOperations.ExtractAttachment(new MemoryStream(updated), "notes.txt", Password));
    }

    [Fact]
    public void AnAttachmentIsRemovedInPlace()
    {
        byte[] withFile = Add(Encrypt(OnePage(), EncryptionAlgorithm.AES128), "notes.txt", Payload);

        using var output = new MemoryStream();
        Assert.True(PdfEncryptedAttachments.RemoveAttachment(new MemoryStream(withFile, writable: false), output, Password, "notes.txt"));
        byte[] without = output.ToArray();

        Assert.Equal(withFile, without.Take(withFile.Length).ToArray());
        Assert.Empty(PdfOperations.ListAttachments(new MemoryStream(without), Password));
    }

    [Fact]
    public void RemovingAnAbsentAttachment_WritesTheDocumentUnchanged()
    {
        byte[] encrypted = Encrypt(OnePage(), EncryptionAlgorithm.AES128);
        using var output = new MemoryStream();

        Assert.False(PdfEncryptedAttachments.RemoveAttachment(new MemoryStream(encrypted, writable: false), output, Password, "notes.txt"));
        Assert.Equal(encrypted, output.ToArray());
    }

    /// <summary>
    /// "Encrypt only file attachments": <c>/StmF</c> and <c>/StrF</c> are <c>/Identity</c> and
    /// <c>/EFF</c> names the crypt filter. The attachment's name is in the clear and its payload is
    /// not; decrypted with the password, the payload comes back — the decryptor used to read it under
    /// <c>/StmF</c>'s <c>/Identity</c> and hand back ciphertext as the file.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void OnlyTheAttachments_AreEncrypted_AndDecryptBack(EncryptionAlgorithm algorithm)
    {
        byte[] attachmentsOnly = Encrypt(WithAttachment(OnePage(), "notes.txt", Payload), algorithm, attachmentsOnly: true);

        string raw = Encoding.Latin1.GetString(attachmentsOnly);
        Assert.Contains("/EFF /StdCF", raw);
        Assert.Contains("/StmF /Identity", raw);
        // Strings go through /Identity: the name is in the clear (the encryptor writes every string as hex).
        Assert.Contains(Hex("notes.txt"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Encoding.ASCII.GetString(Payload), raw);      // the file: encrypted
        Assert.Equal(Payload, PdfOperations.ExtractAttachment(new MemoryStream(attachmentsOnly), "notes.txt", Password));
    }

    /// <summary>
    /// An attachment appended to such a document is encrypted under <c>/EFF</c> too. Under
    /// <c>/StmF</c> — <c>/Identity</c> there — it was written in the clear, where a reader decrypts it
    /// into noise.
    /// </summary>
    [Fact]
    public void AnAttachmentAppendedToAnAttachmentsOnlyDocument_IsEncryptedUnderEff()
    {
        byte[] attachmentsOnly = Encrypt(WithAttachment(OnePage(), "notes.txt", Payload), EncryptionAlgorithm.AES128, attachmentsOnly: true);

        byte[] updated = Add(attachmentsOnly, "second.txt", Second);

        Assert.DoesNotContain(Encoding.ASCII.GetString(Second), Encoding.Latin1.GetString(updated));
        Assert.Equal(Second, PdfOperations.ExtractAttachment(new MemoryStream(updated), "second.txt", Password));
        Assert.Equal(Payload, PdfOperations.ExtractAttachment(new MemoryStream(updated), "notes.txt", Password));
    }

    /// <summary>
    /// An embedded file stream that does not name itself <c>/EmbeddedFile</c> is still one when a file
    /// specification's <c>/EF</c> names it (§7.11.4): it is encrypted under <c>/EFF</c> and decrypted
    /// back, the decryptor finding it through the file specification once the objects are plaintext.
    /// </summary>
    [Fact]
    public void AnEmbeddedFileWithoutItsType_IsFoundThroughTheFileSpecification()
    {
        byte[] attachmentsOnly = Encrypt(UntypedAttachment(Payload), EncryptionAlgorithm.AES128, attachmentsOnly: true);

        Assert.DoesNotContain(Encoding.ASCII.GetString(Payload), Encoding.Latin1.GetString(attachmentsOnly));
        Assert.Equal(Payload, PdfOperations.ExtractAttachment(new MemoryStream(attachmentsOnly), "untyped.txt", Password));
    }

    /// <summary>
    /// A stream naming itself <c>/EmbeddedFile</c> is one even where no file specification names it:
    /// encrypted under <c>/EFF</c>, and decrypted back under it. Each side's <c>/Type</c> reading is
    /// what decides here; the file specification's <c>/EF</c>, the other sign, is absent.
    /// </summary>
    [Fact]
    public void AStreamNamingItselfAnEmbeddedFile_IsOne_WithNoFileSpecification()
    {
        byte[] attachmentsOnly = Encrypt(OrphanEmbeddedFile(Payload), EncryptionAlgorithm.AES128, attachmentsOnly: true);
        Assert.DoesNotContain(Encoding.ASCII.GetString(Payload), Encoding.Latin1.GetString(attachmentsOnly));

        using var source = new MemoryStream(attachmentsOnly, writable: false);
        using var plain = new MemoryStream();
        PdfDecryptor.Decrypt(source, plain, Password);
        using var doc = PdfDocument.Open(new MemoryStream(plain.ToArray()));
        var stream = Assert.IsType<PdfStream>(doc.Parser.GetObject(4));
        Assert.Equal(Payload, stream.RawData);
    }

    /// <summary>
    /// Embedded file streams named the other ways §7.11 allows, each without its optional <c>/Type</c>: through
    /// an indirect <c>/EF</c> dictionary, and as a related file an <c>/RF</c> array names. Taken for ordinary
    /// content, an attachments-only encryption wrote them under <c>/Identity</c>, in clear.
    /// </summary>
    [Theory]
    [InlineData("<< /Type /Filespec /F (untyped.txt) /UF (untyped.txt) /EF 6 0 R >>", "<< /F 5 0 R >>")]
    [InlineData("<< /Type /Filespec /F (untyped.txt) /UF (untyped.txt) /EF << /F 6 0 R >> /RF << /F [(related.txt) 5 0 R] >> >>",
                "<< /Length 1 >>\nstream\nx\nendstream")]
    public void AdversarialEmbeddedFileNamedIndirectlyOrAsARelatedFile_IsEncrypted(string fileSpecification, string sixth)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(untyped.txt) 4 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            fileSpecification,
            "<< /Length " + Payload.Length + " >>\nstream\n" + Encoding.ASCII.GetString(Payload) + "\nendstream",
            sixth);
        Assert.Contains(Encoding.ASCII.GetString(Payload), Encoding.Latin1.GetString(pdf));

        byte[] attachmentsOnly = Encrypt(pdf, EncryptionAlgorithm.AES128, attachmentsOnly: true);

        Assert.DoesNotContain(Encoding.ASCII.GetString(Payload), Encoding.Latin1.GetString(attachmentsOnly));
        using var plain = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(attachmentsOnly, writable: false), plain, Password);
        using var doc = PdfDocument.Open(new MemoryStream(plain.ToArray()));
        Assert.Equal(Payload, Assert.IsType<PdfStream>(doc.Parser.GetObject(5)).RawData);
    }

    /// <summary>
    /// An untyped attachment the fresh encryptor encrypted under <c>/EFF</c> stays encrypted through a
    /// <see cref="ReEncryptionPolicy.KeepOriginal"/> round trip: the writer tells an embedded file by its
    /// <c>/Type</c>, and an attachments-only original's <c>/StmF</c> is <c>/Identity</c>.
    /// </summary>
    [Fact]
    public void AdversarialUntypedAttachment_StaysEncryptedThroughAKeepOriginalRoundTrip()
    {
        byte[] attachmentsOnly = Encrypt(UntypedAttachment(Payload), EncryptionAlgorithm.AES128, attachmentsOnly: true);
        Assert.DoesNotContain(Encoding.ASCII.GetString(Payload), Encoding.Latin1.GetString(attachmentsOnly));

        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(new MemoryStream(attachmentsOnly, writable: false), output,
            new EncryptedRoundTripOptions { OpenPassword = Password, Policy = ReEncryptionPolicy.KeepOriginal },
            (input, written) => input.CopyTo(written));
        byte[] kept = output.ToArray();

        Assert.True(result.Succeeded, string.Join(" | ", result.Warnings));
        Assert.DoesNotContain(Encoding.ASCII.GetString(Payload), Encoding.Latin1.GetString(kept));
        Assert.Equal(Payload, PdfOperations.ExtractAttachment(new MemoryStream(kept), "untyped.txt", Password));
    }

    /// <summary>RC4 has no crypt filters, so it cannot leave everything but the attachments in the clear.</summary>
    [Fact]
    public void EncryptOnlyAttachments_IsRefusedWithRc4()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Encrypt(WithAttachment(OnePage(), "notes.txt", Payload), EncryptionAlgorithm.RC4_128, attachmentsOnly: true));
        Assert.Contains("crypt-filter", ex.Message);
    }

    private static string Hex(string text) => BitConverter.ToString(Encoding.ASCII.GetBytes(text)).Replace("-", "");

    private static byte[] Add(byte[] encrypted, string name, byte[] data)
    {
        using var output = new MemoryStream();
        PdfEncryptedAttachments.AddAttachment(new MemoryStream(encrypted, writable: false), output, Password, name, data);
        return output.ToArray();
    }

    private static byte[] WithAttachment(byte[] pdf, string name, byte[] data)
    {
        using var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(new MemoryStream(pdf), output, name, data);
        return output.ToArray();
    }

    private static byte[] Encrypt(byte[] plaintext, EncryptionAlgorithm algorithm, bool attachmentsOnly = false)
    {
        using var plain = new MemoryStream(plaintext);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = "owner-" + Password,
            Algorithm = algorithm,
            EncryptOnlyAttachments = attachmentsOnly,
        });
        return encrypted.ToArray();
    }

    private static byte[] OnePage() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

    /// <summary>An attachment whose embedded file stream has no <c>/Type</c>, named only by the file specification's <c>/EF</c>.</summary>
    private static byte[] UntypedAttachment(byte[] data) => Build(
        "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(untyped.txt) 4 0 R] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Filespec /F (untyped.txt) /UF (untyped.txt) /EF << /F 5 0 R >> >>",
        "<< /Length " + data.Length + " >>\nstream\n" + Encoding.ASCII.GetString(data) + "\nendstream");

    /// <summary>An embedded file stream, object 4, that names itself and that nothing references.</summary>
    private static byte[] OrphanEmbeddedFile(byte[] data) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /EmbeddedFile /Length " + data.Length + " >>\nstream\n" + Encoding.ASCII.GetString(data) + "\nendstream");

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
