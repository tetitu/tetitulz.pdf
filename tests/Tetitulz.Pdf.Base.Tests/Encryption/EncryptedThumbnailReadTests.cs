// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// Opened without its password, an encrypted document has no pages to read. The thumbnail readers
/// answered for it anyway: <c>ReadThumbnail</c> said the page index was out of range, and
/// <c>ListPagesWithThumbnails</c> that no page has a thumbnail. Both refuse it by name now, and the
/// facade's password overload still reads it.
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedThumbnailReadTests
{
    private const string UserPassword = "s3cret";

    public EncryptedThumbnailReadTests() => PdfEncryptionOperations.Register();

    [Fact]
    public void ReadThumbnail_OnAnEncryptedDocument_RefusesNamingThePasswordRoute()
    {
        var ex = Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfThumbnailManager.ReadThumbnail(new MemoryStream(EncryptedWithThumbnail()), 0));
        Assert.Contains("ReadPageThumbnail(input, pageIndex, password)", ex.Message);
    }

    [Fact]
    public void ListPagesWithThumbnails_OnAnEncryptedDocument_Refuses()
        => Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfThumbnailManager.ListPagesWithThumbnails(new MemoryStream(EncryptedWithThumbnail())));

    /// <summary>
    /// The acceptance control: the password route decrypts first, so the refusal does not reach it,
    /// and it reads the thumbnail the plaintext document carries, pixel for pixel.
    /// </summary>
    [Fact]
    public void ThePasswordRoute_StillReadsTheThumbnail()
    {
        byte[] plain = WithThumbnail();
        var expected = PdfThumbnailManager.ReadThumbnail(new MemoryStream(plain), 0);

        var read = PdfOperations.ReadPageThumbnail(new MemoryStream(Encrypt(plain)), 0, UserPassword);

        Assert.NotNull(read);
        Assert.Equal(expected.Width, read.Width);
        Assert.Equal(expected.PixelData, read.PixelData);
    }

    private static byte[] WithThumbnail()
    {
        var output = new MemoryStream();
        PdfThumbnailManager.GenerateThumbnails(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()), output);
        return output.ToArray();
    }

    private static byte[] EncryptedWithThumbnail() => Encrypt(WithThumbnail());

    private static byte[] Encrypt(byte[] plain)
    {
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(plain), output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }
}
