// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfDecryptor.IdentifyPassword"/> says which of a document's two passwords a string is —
/// what a host honouring <c>/P</c> needs beside <see cref="PdfDecryptor.TryPassword"/>, which answers
/// "does it open" for both alike.
/// </summary>
public class PasswordRoleTests
{
    private static EncryptionInfo Info(string user, string owner, EncryptionAlgorithm algorithm)
    {
        using var input = new MemoryStream(TestPdfs.Plaintext.Build());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = user, OwnerPassword = owner, Algorithm = algorithm,
        });
        output.Position = 0;
        return PdfDecryptor.GetEncryptionInfo(output);
    }

    /// <summary>Each password is named for what it is, under the revision 4 and the AES-256 derivations alike; a string that opens nothing is neither.</summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void TheUserAndOwnerPasswords_AreEachNamed(EncryptionAlgorithm algorithm)
    {
        EncryptionInfo info = Info("reader", "author", algorithm);

        Assert.Equal(PdfPasswordRole.User, PdfDecryptor.IdentifyPassword(info, "reader"));
        Assert.Equal(PdfPasswordRole.Owner, PdfDecryptor.IdentifyPassword(info, "author"));
        Assert.Equal(PdfPasswordRole.None, PdfDecryptor.IdentifyPassword(info, "nobody"));
        Assert.Equal(PdfPasswordRole.None, PdfDecryptor.IdentifyPassword(info, ""));
    }

    /// <summary>A document that opens without a password has the empty string as its user password.</summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void TheEmptyString_IsAnOwnerOnlyDocumentsUserPassword(EncryptionAlgorithm algorithm)
    {
        EncryptionInfo info = Info("", "author", algorithm);

        Assert.Equal(PdfPasswordRole.User, PdfDecryptor.IdentifyPassword(info, ""));
        Assert.Equal(PdfPasswordRole.Owner, PdfDecryptor.IdentifyPassword(info, "author"));
    }
}
