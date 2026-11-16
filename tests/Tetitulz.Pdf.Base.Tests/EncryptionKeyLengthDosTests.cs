// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Adversarial regression from remediation round 37: the <c>/Length</c> clamp lived in
/// <c>PdfDecryptor.Decrypt</c> and inside <c>ComputeEncryptionKey</c>, but the public fast
/// brute-force API (<see cref="PdfDecryptor.GetEncryptionInfo"/> + <see cref="PdfDecryptor.TryPassword"/>)
/// never ran that validation, and <c>RecoverUserPasswordFromOwner</c> used an unclamped
/// <c>keyLenBytes = KeyLengthBits / 8</c> as a <c>Buffer.BlockCopy</c> count against the fixed
/// 16-byte MD5 digest. A standard-handler dict with <c>/Length 136</c> (or larger/negative)
/// produced an uncaught <c>ArgumentException</c> (or OOM). The fix clamps keyLenBytes to 5..16.
/// </summary>
public class EncryptionKeyLengthDosTests
{
    private static EncryptionInfo Info(int keyLengthBits) => new EncryptionInfo
    {
        V = 2,
        R = 3,
        KeyLengthBits = keyLengthBits,
        PValue = -1,
        OValue = new byte[32],
        UValue = new byte[32],
        FileId = new byte[16],
        EncryptMetadata = true,
    };

    [Theory]
    [InlineData(136)]          // 17 bytes > 16 → out-of-bounds BlockCopy past the 16-byte digest
    [InlineData(256)]          // 32 bytes
    [InlineData(2_000_000_000)] // ~250 MB allocation per RC4 round pre-fix
    [InlineData(-8)]           // negative → new byte[-1]
    public void OversizedOrNegativeKeyLength_DoesNotThrow(int keyLengthBits)
    {
        var ex = Record.Exception(() => PdfDecryptor.TryPassword(Info(keyLengthBits), "guess"));
        Assert.Null(ex);
    }

    [Fact]
    public void NormalKeyLength_StillReturnsFalseForWrongPassword()
    {
        // A legitimate 128-bit key (16 bytes) is within the clamp range — behaviour unchanged.
        Assert.False(PdfDecryptor.TryPassword(Info(128), "definitely-not-the-password"));
    }
}
