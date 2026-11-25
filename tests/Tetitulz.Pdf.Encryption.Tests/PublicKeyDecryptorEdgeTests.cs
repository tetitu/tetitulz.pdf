// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The edges of <see cref="PdfPublicKeyDecryptor"/>: the recipient cap, the defaults of <c>/V</c>,
/// <c>/R</c> and <c>/EncryptMetadata</c>, and a stream that
/// cannot seek.
/// </summary>
public class PublicKeyDecryptorEdgeTests
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

    // ================================================================ the recipient cap

    /// <summary>
    /// A document listing more recipients than the reader examines is refused, with the count.
    /// Truncated, it would decrypt to noise without saying so: the file key hashes every recipient,
    /// so reading the first 1024 of 1025 derives a wrong key. The control is 1024 recipients, none
    /// of them this key's, which reaches the recipient match and is refused for that reason instead.
    /// </summary>
    [Fact]
    public void MoreRecipientsThanTheCap_IsRefusedWithTheCount()
    {
        var (_, pfx) = PublicKeyRoundTripTests.MakeRecipient();

        var over = Assert.Throws<PdfException>(() => PublicKeyRoundTripTests.Decrypt(WithJunkRecipients(1025), pfx));
        Assert.Contains("lists 1025 recipients", over.Message);

        var at = Assert.Throws<PdfException>(() => PublicKeyRoundTripTests.Decrypt(WithJunkRecipients(1024), pfx));
        Assert.Contains("not encrypted for this key", at.Message);
    }

    /// <summary>
    /// A <c>/Recipients</c> entry that is not a string is refused, naming the entry: every entry is hashed into the
    /// file key, so leaving one out derived a wrong key and reported the document as encrypted for somebody else.
    /// The control is the same list with a string there, which reaches the recipient match.
    /// </summary>
    [Fact]
    public void ARecipientsEntryThatIsNotAString_IsRefusedNamingIt()
    {
        var (_, pfx) = PublicKeyRoundTripTests.MakeRecipient();

        var refused = Assert.Throws<PdfException>(() => PublicKeyRoundTripTests.Decrypt(WithRecipients("() 5"), pfx));
        Assert.Contains("/Recipients entry 1 is not a string", refused.Message);

        var control = Assert.Throws<PdfException>(() => PublicKeyRoundTripTests.Decrypt(WithRecipients("() ()"), pfx));
        Assert.Contains("not encrypted for this key", control.Message);
    }

    private static byte[] WithRecipients(string recipients) => Build(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        $"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s4 /V 4 /R 4 /Length 128 /Recipients [{recipients}] >>",
    }, " /Encrypt 4 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]");

    /// <summary>
    /// ...and the encryptor will not write such a document, since nothing could open it. Two
    /// recipients — the control — encrypt as before.
    /// </summary>
    [Fact]
    public void Encrypting_ForMoreRecipientsThanTheCap_IsRefused()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();

        var ex = Assert.Throws<ArgumentException>(() => EncryptFor(Enumerable.Repeat(certDer, 1025)));
        Assert.Contains("1025 recipients", ex.Message);

        byte[] two = EncryptFor(Enumerable.Repeat(certDer, 2));
        Assert.Contains(PublicKeyRoundTripTests.Marker,
            PublicKeyRoundTripTests.PageContent(PublicKeyRoundTripTests.Decrypt(two, pfx)));
    }

    // ================================================================ defaults

    /// <summary>
    /// <c>/V</c> defaults to 0 (§7.6.2, Table 20), which no public-key algorithm uses: a document
    /// without it is refused as unsupported, not opened as if it said AES-128.
    /// </summary>
    [Fact]
    public void MissingV_IsRefusedAsUnsupported()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = EncryptFor(new[] { certDer }, EncryptionAlgorithm.AES128);

        var ex = Assert.Throws<PdfException>(() => PublicKeyRoundTripTests.Decrypt(RenameFirst(encrypted, "/V 4", "/W 4"), pfx));
        Assert.Contains("Unsupported public-key encryption: V=0", ex.Message);
    }

    /// <summary>
    /// <c>/R</c> has no default that selects RC4-128: <c>/V 2</c> without it is refused, not read
    /// as revision 3. The control is the same document with its <c>/R</c>, which opens.
    /// </summary>
    [Fact]
    public void MissingR_IsRefusedAsUnsupported()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = EncryptFor(new[] { certDer }, EncryptionAlgorithm.RC4_128);

        Assert.Contains(PublicKeyRoundTripTests.Marker, PublicKeyRoundTripTests.PageContent(PublicKeyRoundTripTests.Decrypt(encrypted, pfx)));

        var ex = Assert.Throws<PdfException>(() => PublicKeyRoundTripTests.Decrypt(RenameFirst(encrypted, "/R 3", "/X 3"), pfx));
        Assert.Contains("V=2, R=0", ex.Message);
    }

    /// <summary>
    /// <c>/EncryptMetadata</c> defaults to true, and the encryptor does not write it. So the
    /// metadata stream is encrypted, and decrypting must decrypt it too. The control: the marker
    /// in it is not visible in the encrypted file.
    /// </summary>
    [Fact]
    public void MetadataStream_IsDecryptedWhenEncryptMetadataIsAbsent()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        const string metaMarker = "MetadataMarkerXYZ";

        byte[] encrypted;
        using (var input = new MemoryStream(WithMetadata(metaMarker)))
        using (var output = new MemoryStream())
        {
            PdfPublicKeyEncryptor.Encrypt(input, output, new[] { certDer }, PdfPermissions.All,
                EncryptionAlgorithm.AES256);
            encrypted = output.ToArray();
        }

        string cipherText = Latin1.GetString(encrypted);
        Assert.DoesNotContain(metaMarker, cipherText);
        Assert.DoesNotContain("/EncryptMetadata", cipherText);

        using var ms = new MemoryStream(PublicKeyRoundTripTests.Decrypt(encrypted, pfx));
        using var doc = PdfDocument.Open(ms);
        var catalog = (PdfDictionary)doc.ResolveObject(doc.Trailer[PdfName.Root]);
        var metadata = (PdfStream)doc.ResolveObject(catalog[PdfName.Metadata]);
        Assert.Contains(metaMarker, Latin1.GetString(metadata.GetDecodedData()));
    }

    // ================================================================ stream handling

    /// <summary>
    /// A stream that cannot seek is answered, not thrown at: there is no position to restore.
    /// </summary>
    [Fact]
    public void IsPublicKeyEncrypted_OnAStreamThatCannotSeek_Answers()
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = EncryptFor(new[] { certDer });

        Assert.True(PdfPublicKeyDecryptor.IsPublicKeyEncrypted(new ForwardOnly(encrypted)));
        Assert.False(PdfPublicKeyDecryptor.IsPublicKeyEncrypted(new ForwardOnly(PublicKeyRoundTripTests.SourcePdf())));
    }

    // ================================================================ fixtures

    private static byte[] EncryptFor(System.Collections.Generic.IEnumerable<byte[]> certs,
        EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
    {
        using var input = new MemoryStream(PublicKeyRoundTripTests.SourcePdf());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, certs, PdfPermissions.All, algorithm);
        return output.ToArray();
    }

    /// <summary>A file declaring <c>/Adobe.PubSec</c> with <paramref name="count"/> empty recipient strings.</summary>
    private static byte[] WithJunkRecipients(int count)
    {
        string recipients = string.Join(" ", Enumerable.Repeat("()", count));
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            $"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s4 /V 4 /R 4 /Length 128 /Recipients [{recipients}] >>",
        }, " /Encrypt 4 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]");
    }

    /// <summary>The round-trip source document, with an XMP metadata stream carrying <paramref name="marker"/>.</summary>
    private static byte[] WithMetadata(string marker)
    {
        string content = $"BT /F1 12 Tf 72 700 Td ({PublicKeyRoundTripTests.Marker}) Tj ET";
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>{marker}</dc:title></x:xmpmeta>";
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream",
        }, "");
    }

    private static byte[] Build(string[] bodies, string trailerExtra)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R").Append(trailerExtra).Append(" >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Replaces the first occurrence of <paramref name="from"/>, keeping every offset.</summary>
    private static byte[] RenameFirst(byte[] pdf, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);
        int at = Latin1.GetString(pdf).IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{from}' does not appear in the encrypted document");
        var copy = (byte[])pdf.Clone();
        Encoding.ASCII.GetBytes(to).CopyTo(copy, at);
        return copy;
    }

    /// <summary>A read-only stream that cannot seek: CanSeek is false and Position throws.</summary>
    private sealed class ForwardOnly : Stream
    {
        private readonly MemoryStream _inner;
        public ForwardOnly(byte[] data) => _inner = new MemoryStream(data, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
