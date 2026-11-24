// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// What the decryptor and the encryptors cannot carry is said, and an object stream is decoded under the
/// parser's own rules: an image codec refused by name.
/// </summary>
public class DecryptionDropAndCodecTests
{
    private static readonly byte[] FileId =
    {
        0x2A, 0x91, 0x5C, 0xE0, 0x13, 0x7B, 0xD4, 0x68,
        0xA5, 0x0F, 0x36, 0xC9, 0x82, 0x4E, 0xF1, 0x1D
    };

    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PageTree = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";

    /// <summary>
    /// An object stream whose <c>/Filter</c> names an image codec is refused by name, before the codec runs — the
    /// parser's own rule for its object streams, now the decryptor's: samples are never object syntax, and a
    /// JBIG2 region is sized by its header, so asking the codec spends its whole budget, once a stream. The
    /// control is the same document with the stream unfiltered, whose member is extracted.
    /// </summary>
    [Fact]
    public void ObjectStreamFilteredThroughAnImageCodec_IsRefusedByNameBeforeAnyDecode()
    {
        string? refused = Decrypt(WithObjectStream("/Filter /JBIG2Decode"), out byte[] plain);

        Assert.NotNull(refused);
        Assert.Contains("JBIG2Decode", refused!, StringComparison.Ordinal);
        Assert.Contains("image codec", refused!, StringComparison.Ordinal);
        using (var doc = PdfDocument.Open(new MemoryStream(plain)))
            Assert.True(doc.Parser.GetObject(7) is Core.Objects.PdfNull);

        string? clean = Decrypt(WithObjectStream(""), out byte[] controlPlain);
        Assert.Null(clean);
        using var control = PdfDocument.Open(new MemoryStream(controlPlain));
        Assert.IsType<Core.Objects.PdfDictionary>(control.Parser.GetObject(7));
    }

    /// <summary>
    /// A top-level object the parser cannot read is reported as a drop, with the parser's reason. The parser answers
    /// such an object with the null object, and the decryptor read every null object as a genuine one, so the
    /// commonest drop of all was silent. The control is a genuine <c>null</c> object, which drops nothing.
    /// </summary>
    [Fact]
    public void UnreadableTopLevelObject_IsReportedAsADrop_AndAGenuineNullIsNot()
    {
        string? dropped = Decrypt(WithObjectFour("]"), out _);
        Assert.NotNull(dropped);
        Assert.Contains("Object 4 could not be read", dropped!, StringComparison.Ordinal);

        Assert.Null(Decrypt(WithObjectFour("null"), out _));
    }

    /// <summary>
    /// The round trip carries the decrypt's drop as a warning: the output lacks the object, and a result reporting
    /// success with nothing said is the answer the accounting exists to prevent.
    /// </summary>
    [Fact]
    public void RoundTrip_CarriesTheDecryptsDropAsAWarning()
    {
        var result = PdfEncryptedRoundTrip.Run(new MemoryStream(WithObjectFour("]")), new MemoryStream(),
            new EncryptedRoundTripOptions { Policy = ReEncryptionPolicy.None }, (i, o) => i.CopyTo(o));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains(result.Warnings, w => w.Contains("Object 4 could not be read", StringComparison.Ordinal));

        var clean = PdfEncryptedRoundTrip.Run(new MemoryStream(WithObjectFour("null")), new MemoryStream(),
            new EncryptedRoundTripOptions { Policy = ReEncryptionPolicy.None }, (i, o) => i.CopyTo(o));
        Assert.True(clean.Succeeded, clean.FailureReason);
        Assert.DoesNotContain(clean.Warnings, w => w.Contains("dropped", StringComparison.Ordinal));
    }

    /// <summary>
    /// The encryptor leaves an object it cannot read out of its output, and says so; a document whose every object
    /// reads leaves the slot null.
    /// </summary>
    [Fact]
    public void Encryptor_ReportsAnObjectItCouldNotRead()
    {
        var parameters = new EncryptionParameters { UserPassword = "u", OwnerPassword = "o", Algorithm = EncryptionAlgorithm.AES256 };

        PdfEncryptor.Encrypt(new MemoryStream(PlainWithObjectFour("]")), new MemoryStream(), parameters);
        Assert.NotNull(PdfEncryptor.LastDroppedObjects);
        Assert.Contains("object 4", PdfEncryptor.LastDroppedObjects!, StringComparison.Ordinal);

        PdfEncryptor.Encrypt(new MemoryStream(PlainWithObjectFour("<< /Type /Annot /Subtype /Text >>")), new MemoryStream(), parameters);
        Assert.Null(PdfEncryptor.LastDroppedObjects);
    }

    /// <summary>
    /// AES-CBC data that is no IV followed by whole blocks — a stream cut short — still decrypts to nothing, and is
    /// now said: an emptied stream is otherwise a page that lost its content with no word. Well-formed data leaves
    /// the slot alone.
    /// </summary>
    [Fact]
    public void MalformedAesCbcLength_IsEmptiedAndSaid()
    {
        byte[] key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        PdfDecryptor.BeginDocument(4);
        Assert.Empty(PdfDecryptor.AesCbcDecrypt(key, new byte[31]));
        Assert.NotNull(PdfDecryptor.LastAesCbcMalformedLength);
        Assert.Contains("31 bytes", PdfDecryptor.LastAesCbcMalformedLength!, StringComparison.Ordinal);

        PdfDecryptor.BeginDocument(4);
        byte[] wellFormed = PdfEncryptor.AesCbcEncrypt(key, Encoding.ASCII.GetBytes("hello"));
        Assert.Equal("hello", Encoding.ASCII.GetString(PdfDecryptor.AesCbcDecrypt(key, wellFormed)));
        Assert.Null(PdfDecryptor.LastAesCbcMalformedLength);
    }

    private static byte[] WithObjectStream(string filterEntries)
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(2, 0, PageTree);
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> >>");
        pdf.EncryptDictionary(5);
        pdf.ObjectStreamWithoutEntries(8, filterEntries, (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        pdf.Compressed(7, objectStreamNumber: 8, index: 0);
        pdf.EndRevision(xrefStreamObjectNumber: 9, rootObjectNumber: 1, encryptObjectNumber: 5);
        return pdf.ToArray();
    }

    private static byte[] WithObjectFour(string body)
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(2, 0, PageTree);
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        pdf.Object(4, 0, body);
        pdf.EncryptDictionary(5);
        pdf.EndRevision(xrefStreamObjectNumber: 9, rootObjectNumber: 1, encryptObjectNumber: 5);
        return pdf.ToArray();
    }

    private static byte[] PlainWithObjectFour(string body)
    {
        string[] bodies =
        {
            Catalog, PageTree, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>", body,
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string? Decrypt(byte[] pdf, out byte[] plain)
    {
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(pdf), output, "");
        plain = output.ToArray();
        return PdfDecryptor.LastObjectStreamRejection;
    }
}
