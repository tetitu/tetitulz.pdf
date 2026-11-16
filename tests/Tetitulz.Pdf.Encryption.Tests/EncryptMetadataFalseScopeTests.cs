// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <c>/EncryptMetadata false</c> leaves the document-level metadata stream — the catalog's <c>/Metadata</c> —
/// in clear (ISO 32000-2 Table 20), and that stream alone: a page's, an image's or a font's XMP is encrypted
/// with the rest. The writer exempted every <c>/Type /Metadata</c> stream; and the decryptor, unable to read
/// a catalog packed in an object stream before decrypting it, decrypted the plaintext document XMP, which
/// threw or came back empty.
/// </summary>
public sealed class EncryptMetadataFalseScopeTests
{
    private const string Password = "metadata-scope-4";
    private const string Component = "COMPONENT-XMP-7Q";

    private static string DocumentXmp(int length) => ("DOC-XMP-" + new string('x', length)).Substring(0, length);

    /// <summary>The catalog's metadata (object 4, <paramref name="documentXmpLength"/> bytes) and an image's (object 6).</summary>
    private static byte[] Fixture(int documentXmpLength)
    {
        string documentXmp = DocumentXmp(documentXmpLength);
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 5 0 R >> >> >>",
            "<< /Type /Metadata /Subtype /XML /Length " + documentXmp.Length + " >>\nstream\n" + documentXmp + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Metadata 6 0 R /Length 1 >>\nstream\nA\nendstream",
            "<< /Type /Metadata /Subtype /XML /Length " + Component.Length + " >>\nstream\n" + Component + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Encrypt(byte[] pdf, bool objectStreams)
    {
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdf), output, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = Password,
            Algorithm = EncryptionAlgorithm.AES128,
            EncryptMetadata = false,
            UseObjectStreams = objectStreams,
        });
        return output.ToArray();
    }

    private static (string Document, string Component) MetadataOf(byte[] encrypted)
    {
        using var plain = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted, writable: false), plain, Password);
        using var doc = PdfDocument.Open(new MemoryStream(plain.ToArray()));
        var catalog = doc.Catalog.Dictionary;
        var documentXmp = (PdfStream)doc.ResolveObject(catalog[PdfName.Metadata]);
        var page = doc.Pages[0].Dictionary;
        var image = (PdfStream)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(page[PdfName.Resources]))[PdfName.XObject]))[new PdfName("Im1")]);
        var componentXmp = (PdfStream)doc.ResolveObject(image[PdfName.Metadata]);
        return (Encoding.Latin1.GetString(documentXmp.GetDecodedData()), Encoding.Latin1.GetString(componentXmp.GetDecodedData()));
    }

    [Theory]
    [InlineData(false, 48)]
    [InlineData(true, 48)]
    [InlineData(true, 50)]
    public void AComponentsXmp_IsEncrypted_AndTheDocumentsComesBackWhole(bool objectStreams, int documentXmpLength)
    {
        byte[] encrypted = Encrypt(Fixture(documentXmpLength), objectStreams);
        string text = Encoding.Latin1.GetString(encrypted);

        Assert.Contains(DocumentXmp(documentXmpLength), text, StringComparison.Ordinal);   // left in clear, as declared
        Assert.DoesNotContain(Component, text, StringComparison.Ordinal);

        var (document, component) = MetadataOf(encrypted);
        Assert.Equal(DocumentXmp(documentXmpLength), document);
        Assert.Equal(Component, component);
    }

    /// <summary>A <see cref="ReEncryptionPolicy.KeepOriginal"/> round trip keeps the component's XMP encrypted too.</summary>
    [Fact]
    public void AComponentsXmp_StaysEncryptedThroughAKeepOriginalRoundTrip()
    {
        byte[] encrypted = Encrypt(Fixture(48), objectStreams: false);
        Assert.DoesNotContain(Component, Encoding.Latin1.GetString(encrypted), StringComparison.Ordinal);

        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(new MemoryStream(encrypted, writable: false), output,
            new EncryptedRoundTripOptions { OpenPassword = Password, Policy = ReEncryptionPolicy.KeepOriginal },
            (input, written) => input.CopyTo(written));
        byte[] kept = output.ToArray();

        Assert.True(result.Succeeded, string.Join(" | ", result.Warnings));
        Assert.DoesNotContain(Component, Encoding.Latin1.GetString(kept), StringComparison.Ordinal);
        Assert.Equal(Component, MetadataOf(kept).Component);
    }
}
