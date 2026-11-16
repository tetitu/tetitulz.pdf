// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A stream's own crypt filter (ISO 32000-2 §7.4.10): <c>/Crypt</c> first in its <c>/Filter</c>,
/// naming the crypt filter its data is encrypted with in place of the document's — <c>/Identity</c>
/// for a stream left in the clear. The encryptor, the incremental writer and the decryptor honour it;
/// the decryptor used to read every stream under <c>/StmF</c>, turning a clear one into noise. The
/// stream data is uncompressed, so the raw bytes show plainly which side of the line each went.
/// </summary>
public sealed class StreamCryptFilterTests
{
    private const string Password = "crypt-pw-28";
    private const string Clear = "Clear stream data 5e2f";
    private const string Secret = "Encrypted stream data 9a1c";

    [Theory]
    [InlineData("/Filter [/Crypt] /DecodeParms [<< /Name /Identity >>]")]
    [InlineData("/Filter /Crypt /DecodeParms << /Type /CryptFilterDecodeParms /Name /Identity >>")]
    [InlineData("/Filter [/Crypt]")]   // no /Name: /Identity by default
    public void AnIdentityStream_StaysInTheClear_AndDecryptsAsItIs(string filter)
    {
        byte[] encrypted = Encrypt(Document(filter), EncryptionAlgorithm.AES128);

        string raw = Encoding.Latin1.GetString(encrypted);
        Assert.Contains(Clear, raw);
        Assert.DoesNotContain(Secret, raw);

        using PdfDocument plain = Decrypted(encrypted);
        var clear = Assert.IsType<PdfStream>(plain.Parser.GetObject(4));
        Assert.Equal(Clear, Encoding.ASCII.GetString(clear.RawData));
        Assert.Null(clear[PdfName.Filter]);                          // the crypt filter is gone with the encryption
        Assert.Equal(Secret, Encoding.ASCII.GetString(((PdfStream)plain.Parser.GetObject(5)).RawData));
    }

    /// <summary>A stream naming the document's own filter, <c>/StdCF</c>, is encrypted as any other.</summary>
    [Fact]
    public void AStreamNamingStdCF_IsEncrypted_AndDecrypts()
    {
        byte[] encrypted = Encrypt(Document("/Filter [/Crypt] /DecodeParms [<< /Name /StdCF >>]"), EncryptionAlgorithm.AES256);

        Assert.DoesNotContain(Clear, Encoding.Latin1.GetString(encrypted));
        using PdfDocument plain = Decrypted(encrypted);
        Assert.Equal(Clear, Encoding.ASCII.GetString(((PdfStream)plain.Parser.GetObject(4)).RawData));
    }

    /// <summary>
    /// The encryptor writes one crypt filter, <c>/StdCF</c>; a stream naming another could not be
    /// decrypted by any reader of the file, so it is refused rather than guessed.
    /// </summary>
    [Fact]
    public void TheEncryptor_RefusesACryptFilterItWouldNotDefine()
    {
        var ex = Assert.Throws<PdfException>(() =>
            Encrypt(Document("/Filter [/Crypt] /DecodeParms [<< /Name /Other >>]"), EncryptionAlgorithm.AES128));
        Assert.Contains("/Other", ex.Message);
    }

    /// <summary>
    /// A stream naming a crypt filter the document does not define is decrypted under <c>/StmF</c>,
    /// and the decryptor says so — once, however many streams name it.
    /// </summary>
    [Fact]
    public void AnUndefinedFilterName_IsDecryptedUnderStmF_AndReported()
    {
        byte[] encrypted = Encrypt(Document("/Filter [/Crypt] /DecodeParms [<< /Name /StdCF >>]"), EncryptionAlgorithm.AES128);
        byte[] renamed = Replace(encrypted, "/Name /StdCF", "/Name /OthCF");

        using PdfDocument plain = Decrypted(renamed);
        Assert.Equal(Clear, Encoding.ASCII.GetString(((PdfStream)plain.Parser.GetObject(4)).RawData));
        Assert.Contains("/OthCF", PdfDecryptor.LastCryptFilterFallback);
    }

    /// <summary>
    /// A revision appended to an encrypted document leaves an <c>/Identity</c> stream in the clear too:
    /// the incremental writer asks the same question.
    /// </summary>
    [Fact]
    public void AnAppendedIdentityStream_IsWrittenInTheClear()
    {
        byte[] encrypted = Encrypt(Document("/Filter [/Crypt]"), EncryptionAlgorithm.AES128);
        const string Appended = "Appended clear data 31be";

        using var output = new MemoryStream();
        PdfEncryptedIncrementalUpdate.Run(new MemoryStream(encrypted, writable: false), output, Password, (doc, firstFree) =>
        {
            var stream = new PdfStream { RawData = Encoding.ASCII.GetBytes(Appended) };
            stream.Set(PdfName.Filter, new PdfArray { PdfName.Crypt });
            var control = new PdfStream { RawData = Encoding.ASCII.GetBytes(Secret + "-appended") };
            return new List<IndirectObject> { new IndirectObject(firstFree, 0, stream), new IndirectObject(firstFree + 1, 0, control) };
        });

        string raw = Encoding.Latin1.GetString(output.ToArray());
        Assert.Contains(Appended, raw);
        Assert.DoesNotContain(Secret + "-appended", raw);
    }

    /// <summary>
    /// A stream's own filter decides over <c>/EFF</c> too: an embedded file naming <c>/Identity</c> in
    /// a document that encrypts only its attachments is left in the clear, and the decryptor's
    /// <c>/EFF</c> pass leaves it alone rather than decrypting clear bytes into noise.
    /// </summary>
    [Fact]
    public void AnEmbeddedFileNamingIdentity_StaysClear_InAnAttachmentsOnlyDocument()
    {
        using var plain = new MemoryStream(Document("/Type /EmbeddedFile /Filter [/Crypt]"));
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = "owner-" + Password,
            Algorithm = EncryptionAlgorithm.AES128,
            EncryptOnlyAttachments = true,
        });

        Assert.Contains(Clear, Encoding.Latin1.GetString(encrypted.ToArray()));
        using PdfDocument decrypted = Decrypted(encrypted.ToArray());
        Assert.Equal(Clear, Encoding.ASCII.GetString(((PdfStream)decrypted.Parser.GetObject(4)).RawData));
    }

    private static PdfDocument Decrypted(byte[] encrypted)
    {
        using var source = new MemoryStream(encrypted, writable: false);
        var plain = new MemoryStream();
        PdfDecryptor.Decrypt(source, plain, Password);
        plain.Position = 0;
        return PdfDocument.Open(plain);
    }

    private static byte[] Replace(byte[] data, string from, string to)
    {
        string text = Encoding.Latin1.GetString(data);
        Assert.Equal(1, text.Split(from).Length - 1);
        return Encoding.Latin1.GetBytes(text.Replace(from, to));
    }

    private static byte[] Encrypt(byte[] plaintext, EncryptionAlgorithm algorithm)
    {
        using var plain = new MemoryStream(plaintext);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = "owner-" + Password,
            Algorithm = algorithm,
        });
        return encrypted.ToArray();
    }

    /// <summary>Object 4: a stream with <paramref name="filter"/> holding <see cref="Clear"/>; object 5: an ordinary stream holding <see cref="Secret"/>.</summary>
    private static byte[] Document(string filter)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< " + filter + " /Length " + Clear.Length + " >>\nstream\n" + Clear + "\nendstream",
            "<< /Length " + Secret.Length + " >>\nstream\n" + Secret + "\nendstream",
        };
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
