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
/// A string outside an indirect object is never encrypted (ISO 32000-2 §7.6.2), so a trailer whose
/// <c>/Info</c> is a direct dictionary kept the title, author and subject readable in a document the
/// caller asked to encrypt — every encryptor copied the trailer as it was. Table 15 requires <c>/Info</c>
/// to be indirect; such a value is written as an object of its own, which the encryptor encrypts.
/// </summary>
public sealed class DirectTrailerValueEncryptionTests
{
    private const string Secret = "TITLE-SECRET-9";
    private const string Password = "trailer-info-7";

    private static byte[] Fixture()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
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
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1)
          .Append(" /Root 1 0 R /Info << /Title (").Append(Secret).Append(") >> >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string TitleOf(byte[] decrypted)
    {
        using var doc = PdfDocument.Open(new MemoryStream(decrypted));
        var info = (PdfDictionary)doc.ResolveObject(doc.Trailer[PdfName.Info]);
        return ((PdfString)doc.ResolveObject(info[new PdfName("Title")])).GetText();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdversarialDirectInfo_IsEncrypted_AndComesBackWhole(bool objectStreams)
    {
        byte[] input = Fixture();
        Assert.Contains(Secret, Encoding.Latin1.GetString(input), StringComparison.Ordinal);

        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(input), output, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = Password,
            Algorithm = EncryptionAlgorithm.AES256,
            UseObjectStreams = objectStreams,
        });
        byte[] encrypted = output.ToArray();

        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(encrypted), StringComparison.Ordinal);
        using var plain = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), plain, Password);
        Assert.Equal(Secret, TitleOf(plain.ToArray()));
    }

    [Fact]
    public void AdversarialDirectInfo_IsEncrypted_ByThePublicKeyEncryptor()
    {
        (byte[] certDer, byte[] pfx) = PublicKeyRoundTripTests.MakeRecipient();
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(new MemoryStream(Fixture()), output, certDer, PdfPermissions.All, EncryptionAlgorithm.AES256);
        byte[] encrypted = output.ToArray();

        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(encrypted), StringComparison.Ordinal);
        Assert.Equal(Secret, TitleOf(PublicKeyRoundTripTests.Decrypt(encrypted, pfx)));
    }
}
