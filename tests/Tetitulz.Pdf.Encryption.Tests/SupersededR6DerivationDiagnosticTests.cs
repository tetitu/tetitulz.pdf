// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A file written by this library's <b>superseded</b> R6 key derivation is rejected — and the
/// rejection says so instead of reading as "wrong password".
/// </summary>
/// <remarks>
/// <para><b>Files written by the superseded derivation, made legible.</b> Stopping the hash one round
/// late changes the key for about one input in thirty-eight; such a file carries a <c>/U</c> no
/// conformant reader can authenticate — not a common viewer, not an independent checker, not an
/// independent implementation, and not this library either.</para>
///
/// <para><b>Rejected rather than accepted, deliberately.</b> A fallback would let this library go on
/// opening files that are unopenable everywhere else, which hides the damage. What the caller gets
/// instead is a named reason, because "the correct password is refused with no explanation" is the
/// exact failure mode the encryption notes in CLAUDE.md call out as the worst available.</para>
/// </remarks>
public class SupersededR6DerivationDiagnosticTests
{
    /// <summary>A fixed salt, so the fixture is reproducible.</summary>
    private static readonly byte[] Salt = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };

    /// <summary>
    /// A password whose two readings of the termination predicate give different keys — measured,
    /// not assumed, by <see cref="TheFixturePasswordActuallyDiverges"/>.
    /// </summary>
    private const string DivergingPassword = "r6-reading-99";

    [Fact]
    public void APasswordCorrectOnlyUnderTheSupersededReadingIsRejected()
    {
        Assert.False(PdfDecryptor.TryPassword(SupersededlyEncryptedInfo(), DivergingPassword));
    }

    [Fact]
    public void AndTheRejectionNamesTheSupersededDerivationRatherThanReadingAsAWrongPassword()
    {
        PdfDecryptor.TryPassword(SupersededlyEncryptedInfo(), DivergingPassword);

        Assert.NotNull(PdfDecryptor.LastSupersededR6Derivation);
        Assert.Contains("SUPERSEDED", PdfDecryptor.LastSupersededR6Derivation!, StringComparison.Ordinal);
        Assert.Contains("user password", PdfDecryptor.LastSupersededR6Derivation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary wrong password reports nothing — including right after one that did.
    /// </summary>
    /// <remarks>
    /// Two claims in one. A channel that fired on every failed password would be noise a caller
    /// learns to ignore, and the second half is the reset: a slot read after the operation must be
    /// cleared at the start of it, or the previous document's verdict is returned for this one.
    /// </remarks>
    [Fact]
    public void AnOrdinaryWrongPasswordReportsNothingEvenAfterOneThatDid()
    {
        PdfDecryptor.TryPassword(SupersededlyEncryptedInfo(), DivergingPassword);
        Assert.NotNull(PdfDecryptor.LastSupersededR6Derivation);

        PdfDecryptor.TryPassword(SupersededlyEncryptedInfo(), "not-the-password-at-all");

        Assert.Null(PdfDecryptor.LastSupersededR6Derivation);
    }

    /// <summary>
    /// The acceptance control: the same file written under the conformant reading opens, and reports
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Without this, "rejected with a diagnostic" would be satisfied by a decryptor that had stopped
    /// authenticating R6 at all — and by one that reported the superseded derivation on every file.
    /// </remarks>
    [Fact]
    public void TheSameFileUnderTheConformantReadingOpensAndReportsNothing()
    {
        Assert.True(PdfDecryptor.TryPassword(ConformantInfo(), DivergingPassword));
        Assert.Null(PdfDecryptor.LastSupersededR6Derivation);
    }

    /// <summary>
    /// The fixture's premise: this password really does part the two readings.
    /// </summary>
    /// <remarks>
    /// For thirty-seven passwords in thirty-eight the readings agree, so a fixture built on an
    /// arbitrary one would make <see cref="APasswordCorrectOnlyUnderTheSupersededReadingIsRejected"/>
    /// assert nothing — the password would simply be correct, and the test would be measuring a
    /// typo. Asserted rather than commented, because the difference is invisible in the source.
    /// </remarks>
    [Fact]
    public void TheFixturePasswordActuallyDiverges()
    {
        Assert.NotEqual(Hash(PdfEncryptionPrimitives.HashR6TerminationOffset),
                        Hash(PdfEncryptionPrimitives.HashR6SupersededTerminationOffset));
    }

    // ------------------------------------------------------------------

    private static byte[] Hash(int offset) =>
        PdfEncryptionPrimitives.ComputeHashR6(
            Encoding.UTF8.GetBytes(DivergingPassword), Salt, null, offset);

    /// <summary>An R6 <c>/Encrypt</c> whose <c>/U</c> was computed the old, wrong way.</summary>
    private static EncryptionInfo SupersededlyEncryptedInfo() =>
        InfoWithUserHash(Hash(PdfEncryptionPrimitives.HashR6SupersededTerminationOffset));

    /// <summary>The same, computed the conformant way.</summary>
    private static EncryptionInfo ConformantInfo() =>
        InfoWithUserHash(Hash(PdfEncryptionPrimitives.HashR6TerminationOffset));

    /// <summary>
    /// A minimal AES-256 R6 <c>/Encrypt</c>: <c>/U</c> is the 32-byte validation hash plus the
    /// validation and key salts, and <c>/UE</c> wraps a key of zeros under the matching key hash.
    /// </summary>
    /// <remarks>
    /// <c>/UE</c> has to be real, not filler: validation succeeding is only half the path, and a
    /// caller that got past the hash and then failed to unwrap the key would report a different
    /// outcome from the one under test. It is built by encrypting the zero key with the key-salt
    /// hash, which is precisely what a writer does.
    /// </remarks>
    private static EncryptionInfo InfoWithUserHash(byte[] userValidationHash)
    {
        var u = new byte[48];
        Buffer.BlockCopy(userValidationHash, 0, u, 0, 32);
        Buffer.BlockCopy(Salt, 0, u, 32, 8);          // validation salt
        Buffer.BlockCopy(KeySalt, 0, u, 40, 8);       // key salt

        byte[] intermediate = PdfEncryptionPrimitives.ComputeHashR6(
            Encoding.UTF8.GetBytes(DivergingPassword), KeySalt);
        byte[] ue = PdfEncryptionPrimitives.AesCbcNoPadding(
            true, intermediate, new byte[16], new byte[32]);

        var dict = new PdfDictionary();
        dict.Set(PdfName.UE, new PdfString(ue));

        return new EncryptionInfo
        {
            V = 5,
            R = 6,
            KeyLengthBits = 256,
            UValue = u,
            OValue = null,
            EncryptDict = dict,
            EncryptMetadata = true,
        };
    }

    private static readonly byte[] KeySalt = { 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01 };
}
