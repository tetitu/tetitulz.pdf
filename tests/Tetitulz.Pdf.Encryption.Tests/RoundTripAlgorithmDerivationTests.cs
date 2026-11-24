// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// What <see cref="PdfEncryptedRoundTrip.TryDeriveParameters"/> answers for the RC4 handlers: every
/// <c>/V 1</c> and <c>/V 2</c> arm, a nonconforming <c>/V 1</c>
/// with a longer key refused rather than downgraded, and the Recipients policy refusing what the
/// public-key writer cannot write instead of throwing out of <c>Run</c>.
/// </summary>
public class RoundTripAlgorithmDerivationTests
{
    private static EncryptionInfo Info(int v, int r, int lengthBits)
        => new EncryptionInfo { V = v, R = r, KeyLengthBits = lengthBits, Filter = "Standard", PValue = -4 };

    private static (bool Ok, EncryptionAlgorithm? Algorithm, string? Reason) Derive(EncryptionInfo info,
        ReEncryptionPolicy policy = ReEncryptionPolicy.ReuseOpenPassword)
    {
        bool ok = PdfEncryptedRoundTrip.TryDeriveParameters(info, new EncryptedRoundTripOptions { Policy = policy },
            out EncryptionParameters parameters, out string reason);
        return (ok, parameters?.Algorithm, reason);
    }

    [Theory]
    [InlineData(1, 2, 40, EncryptionAlgorithm.RC4_40)]
    [InlineData(1, 3, 40, EncryptionAlgorithm.RC4_40R3)]
    [InlineData(2, 2, 40, EncryptionAlgorithm.RC4_40)]
    [InlineData(2, 3, 40, EncryptionAlgorithm.RC4_40R3)]
    [InlineData(2, 3, 128, EncryptionAlgorithm.RC4_128)]
    public void EachRc4Arm_KeepsTheRevisionAndTheKeyLengthItRead(int v, int r, int bits, EncryptionAlgorithm expected)
    {
        var (ok, algorithm, reason) = Derive(Info(v, r, bits));

        Assert.True(ok, reason);
        Assert.Equal(expected, algorithm);
    }

    /// <summary>A <c>/V 1</c> file declaring a 128-bit key is refused, not re-encrypted at 40 bits; the 40-bit one beside it is accepted.</summary>
    [Fact]
    public void AV1FileDeclaringALongerKey_IsRefusedRatherThanDowngraded()
    {
        var (ok, _, reason) = Derive(Info(1, 3, 128));

        Assert.False(ok);
        Assert.Contains("128-bit key", reason, StringComparison.Ordinal);
        Assert.True(Derive(Info(1, 3, 40)).Ok);
    }

    /// <summary>
    /// Under the Recipients policy a 40-bit RC4 input is refused with the reason, where it threw
    /// <see cref="NotSupportedException"/> out of <c>Run</c> after the caller's operation had run;
    /// a 128-bit RC4 input, which the public-key writer writes, is accepted.
    /// </summary>
    [Theory]
    [InlineData(1, 2, 40)]
    [InlineData(1, 3, 40)]
    public void UnderRecipients_AnAlgorithmThePublicKeyWriterCannotWrite_IsRefusedWithTheReason(int v, int r, int bits)
    {
        var (ok, _, reason) = Derive(Info(v, r, bits), ReEncryptionPolicy.Recipients);

        Assert.False(ok);
        Assert.Contains("cannot be written for recipients", reason, StringComparison.Ordinal);
        Assert.True(Derive(Info(2, 3, 128), ReEncryptionPolicy.Recipients).Ok);
    }
}
