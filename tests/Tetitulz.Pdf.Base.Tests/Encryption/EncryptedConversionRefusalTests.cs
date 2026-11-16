// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// <see cref="EncryptedRead.RefuseConversion"/>, the refusal the text, SVG, HTML and Office
/// converters make of an encrypted document instead of writing a valid, empty output: it names the
/// target and the assembly that decrypts, and lets an ordinary document through.
/// </summary>
public sealed class EncryptedConversionRefusalTests
{
    private static byte[] Plain() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
        .Finish();

    private static byte[] Encrypted()
    {
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(Plain()), output, new EncryptionParameters
        {
            UserPassword  = "user",
            OwnerPassword = "owner",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }

    [Fact]
    public void AnEncryptedDocument_IsRefused_NamingTheTargetAndTheDecryptor()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Encrypted()));
        Assert.True(doc.IsEncrypted);

        var ex = Assert.Throws<PdfEncryptedDocumentException>(() => EncryptedRead.RefuseConversion(doc, "DOCX"));

        Assert.StartsWith("Cannot convert an encrypted document to DOCX:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Tetitulz.Pdf.Encryption.PdfDecryptor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryDocument_IsLetThrough()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Plain()));

        EncryptedRead.RefuseConversion(doc, "DOCX");

        Assert.False(doc.IsEncrypted);
        Assert.Equal(1, doc.Pages.Count);
    }
}
