// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// The password-less extraction doors refuse an encrypted document rather than answer from one
/// they cannot read: a search answered "not found" and the structured, graphics, table, 3D and rich
/// media extractors answered "nothing" for a document whose pages were there, encrypted. Each names
/// the facade overload that takes a password.
/// </summary>
public sealed class EncryptedExtractionDoorsTests
{
    private const string UserPassword = "user-secret";

    [Fact]
    public void EveryPasswordLessDoor_RefusesAnEncryptedDocument_NamingThePasswordDoor()
    {
        byte[] pdf = EncryptedTextPdf();
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;

            Refuses(() => PdfTextSearch.Contains(new MemoryStream(pdf), "Locked"), "ContainsText");
            Refuses(() => PdfTextSearch.Contains(new MemoryStream(pdf), "Locked", new TextSearchOptions()), "ContainsText");
            Refuses(() => PdfTextSearch.Search(new MemoryStream(pdf), "Locked"), "SearchText");
            Refuses(() => PdfTextSearch.Search(new MemoryStream(pdf), "Locked", new TextSearchOptions()), "SearchText");
            Refuses(() => PdfStructuredTextExtractor.Extract(new MemoryStream(pdf)), "ExtractStructuredText");
            Refuses(() => PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0), "ExtractStructuredText");
            Refuses(() => PdfGraphicsExtractor.Extract(new MemoryStream(pdf)), "ExtractGraphics");
            Refuses(() => PdfGraphicsExtractor.ExtractPage(new MemoryStream(pdf), 0), "ExtractGraphics");
            Refuses(() => PdfTableExtractor.Extract(new MemoryStream(pdf)), "ExtractStructuredText");
            Refuses(() => Pdf3DExtractor.List3DAnnotations(new MemoryStream(pdf)), "List3DAnnotations");
            Refuses(() => Pdf3DExtractor.Extract3DStream(new MemoryStream(pdf), 0), "Extract3DStream");
            Refuses(() => PdfRichMediaExtractor.ListRichMedia(new MemoryStream(pdf)), "ListRichMedia");
            Refuses(() => PdfRichMediaExtractor.ExtractMedia(new MemoryStream(pdf), 0), "ExtractMedia");
        }
        finally { PdfDecryptorHolder.Current = saved; }
    }

    /// <summary>The control: the same doors read the text of the document before it was encrypted.</summary>
    [Fact]
    public void ThePlainDocument_IsRead()
    {
        byte[] plain = MinimalPdfBuilder.CreateMinimalPdf(contentStream: "BT /F1 24 Tf 72 700 Td (Locked away) Tj ET");

        Assert.True(PdfTextSearch.Contains(new MemoryStream(plain), "Locked"));
        Assert.Single(PdfTextSearch.Search(new MemoryStream(plain), "Locked"));
        Assert.Single(PdfStructuredTextExtractor.Extract(new MemoryStream(plain)));
        Assert.Single(PdfGraphicsExtractor.Extract(new MemoryStream(plain)));
        Assert.Empty(Pdf3DExtractor.List3DAnnotations(new MemoryStream(plain)));
        Assert.Empty(PdfRichMediaExtractor.ListRichMedia(new MemoryStream(plain)));
    }

    private static void Refuses(Action door, string passwordDoor)
    {
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(door);
        Assert.Contains(passwordDoor, ex.Message, StringComparison.Ordinal);
    }

    private static byte[] EncryptedTextPdf()
    {
        PdfEncryptionOperations.Register();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(
            contentStream: "BT /F1 24 Tf 72 700 Td (Locked away) Tj ET"));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }
}
