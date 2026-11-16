// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="ReEncryptionPolicy.ReuseOpenPassword"/> writes the output under the password that
/// opened the input: the credential's when the caller handed one, and the empty user password when
/// that is what opened an owner-only document, never a string that authenticated nothing.
/// </summary>
public class RoundTripReusedPasswordTests
{
    private static byte[] Encrypt(string user, string owner, EncryptionAlgorithm algorithm)
    {
        using var input = new MemoryStream(TestPdfs.Plaintext.Build());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = user, OwnerPassword = owner, Algorithm = algorithm,
        });
        return output.ToArray();
    }

    private static (RoundTripResult Result, byte[] Output) Run(byte[] encrypted, EncryptedRoundTripOptions options)
    {
        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(input, output, options, (i, o) => i.CopyTo(o));
        return (result, output.ToArray());
    }

    /// <summary>User or owner password, never the empty-user fallback.</summary>
    private static bool Authenticates(byte[] pdf, string password)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        return PdfDecryptor.TryPassword(PdfDecryptor.GetEncryptionInfo(stream), password);
    }

    [Fact]
    public void ACredentialsPassword_IsTheOneTheOutputIsWrittenUnder()
    {
        byte[] encrypted = Encrypt("secret", "owner", EncryptionAlgorithm.AES256);

        var (result, output) = Run(encrypted, new EncryptedRoundTripOptions
        {
            Credential = PdfDecryptionCredential.Password("secret"),
        });

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(Authenticates(output, "secret"));
        Assert.False(Authenticates(output, ""));
    }

    [Fact]
    public void AnOwnerOnlyDocumentTheEmptyPasswordOpened_IsWrittenUnderIt_NotTheCallersString()
    {
        byte[] encrypted = Encrypt("", "owner", EncryptionAlgorithm.AES128);

        var (result, output) = Run(encrypted, new EncryptedRoundTripOptions { OpenPassword = "typo" });

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(Authenticates(output, ""));
        Assert.False(Authenticates(output, "typo"));
        Assert.Contains(result.Warnings, w => w.Contains("empty user password"));
    }

    [Fact]
    public void TheCallersOwnPassword_IsReusedWhenItOpenedTheDocument()
    {
        byte[] encrypted = Encrypt("secret", "owner", EncryptionAlgorithm.AES128);

        var (result, output) = Run(encrypted, new EncryptedRoundTripOptions { OpenPassword = "secret" });

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(Authenticates(output, "secret"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("empty user password"));
    }

    [Fact]
    public void ACredentialAndAnOpenPasswordThatDiffer_AreRefused()
    {
        byte[] encrypted = Encrypt("secret", "owner", EncryptionAlgorithm.AES256);

        var (result, output) = Run(encrypted, new EncryptedRoundTripOptions
        {
            Credential = PdfDecryptionCredential.Password("secret"),
            OpenPassword = "other",
        });

        Assert.False(result.Succeeded);
        Assert.Contains("different passwords", result.FailureReason);
        Assert.Empty(output);
    }
}
