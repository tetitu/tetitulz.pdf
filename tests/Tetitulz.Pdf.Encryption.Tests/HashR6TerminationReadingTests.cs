// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// How often the two readings of the Algorithm 2.B termination predicate part company.
/// </summary>
/// <remarks>
/// <para><b>Which reading is right is settled elsewhere.</b>
/// <see cref="HashR6ExternalVectorTests"/> settles it with six R6 files from two independent
/// producers: the loop stops once the last byte of <c>E</c> is at most <c>i - 31</c> with
/// <c>i</c> zero-based, as an independent checker, an independent implementation, another reader
/// and another library do — the specification's round number minus 32, the round number being
/// <c>i + 1</c>. <c>HashR6TerminationOffset</c> is 31, and
/// <c>HashR6SupersededTerminationOffset</c> the other reading's 32.</para>
///
/// <para><b>What is worth measuring is the rate</b>, because the rate is what makes the wrong
/// reading severe rather than academic. The two readings agree for roughly thirty-seven passwords
/// in thirty-eight, so a round trip through our own pair passes under either constant.
/// <see cref="TheTwoReadingsDisagreeOftenEnoughToMatter"/> says how often; a change that made them
/// agree always, or differ always, would be a second change to Algorithm 2.B and this is what
/// would notice.</para>
///
/// <para><b>The comparison runs through the production function</b>, with only the offset supplied,
/// so the 64-fold <c>K1</c>, the AES-CBC and the mod-3 digest choice are shared by construction. A
/// second copy of Algorithm 2.B in a test would make a measured difference mean nothing.</para>
///
/// <para><b>Relative assertions are still the blind spot here, and the vectors are the answer to
/// it.</b> Everything in this file compares two readings of one implementation, so a change applied
/// to both is invisible — that is exactly how a deleted 64-round minimum once survived. Do not add a
/// correctness claim here; add it to <see cref="HashR6ExternalVectorTests"/>, where the expected
/// value came from somebody else's code.</para>
/// </remarks>
public class HashR6TerminationReadingTests
{
    /// <summary>A fixed salt: this measurement has to be reproducible, so nothing here is random.</summary>
    private static readonly byte[] Salt = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };

    /// <summary>
    /// How many passwords to compare. Each sample runs Algorithm 2.B twice, so this is the whole
    /// cost of the file.
    /// </summary>
    /// <remarks>
    /// <b>Chosen from the measured rate, not guessed.</b> A first version used 300 and happened to
    /// see three divergences, which makes <i>at least one</i> close to a coin flip on a bad draw —
    /// a flaky test asserting a real property. At 2 000 the count measured <b>53</b>, so seeing
    /// none is on the order of one in a billion, and the sweep costs about five seconds.
    /// </remarks>
    private const int Samples = 2000;

    private static byte[] Password(int i) => Encoding.UTF8.GetBytes("r6-reading-" + i);

    private static byte[] ThisLibrary(byte[] password) =>
        PdfEncryptionPrimitives.ComputeHashR6(
            password, Salt, null, PdfEncryptionPrimitives.HashR6TerminationOffset);

    private static byte[] SupersededReading(byte[] password) =>
        PdfEncryptionPrimitives.ComputeHashR6(
            password, Salt, null, PdfEncryptionPrimitives.HashR6SupersededTerminationOffset);

    /// <summary>
    /// Every password whose two readings differ, in order — computed once for the whole class.
    /// </summary>
    /// <remarks>
    /// 2 000 samples is 4 000 runs of Algorithm 2.B, so the two tests that need this share one
    /// sweep rather than paying for it twice.
    /// </remarks>
    private static readonly Lazy<List<int>> _divergences = new(() =>
    {
        var found = new List<int>();
        for (int i = 0; i < Samples; i++)
        {
            byte[] password = Password(i);
            if (!AreEqual(ThisLibrary(password), SupersededReading(password))) found.Add(i);
        }
        return found;
    });

    private static List<int> Divergences() => _divergences.Value;

    private static bool AreEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ================================================================

    /// <summary>
    /// <b>The two readings produce different keys, often enough that the register's HIGH severity
    /// is earned.</b>
    /// </summary>
    /// <remarks>
    /// <para>The band is wide on purpose. The point is not the exact rate — it is that the rate is
    /// neither zero nor one: the readings are <b>not</b> equivalent, so one of them is wrong for
    /// some real files, and they are not <b>always</b> different, which is why every round trip in
    /// this suite passes and no existing test can see the problem.</para>
    ///
    /// <para>A divergence needs the last byte of <c>E</c> to land exactly on the boundary at the
    /// first round that would stop, so a rate near one in thirty-something is what the arithmetic
    /// predicts. <b>Measured: 53 of 2 000, one in 37.7</b> — which is the register's estimate of
    /// "~1 file in 36" confirmed rather than replaced. Being right about the rate is not being
    /// right about which reading is correct, and this file settles only the first.</para>
    /// </remarks>
    [Fact]
    public void TheTwoReadingsDisagreeOftenEnoughToMatter()
    {
        List<int> diverged = Divergences();

        Assert.NotEmpty(diverged);

        Assert.True(diverged.Count < Samples / 2,
            $"the two readings differed for {diverged.Count} of {Samples} passwords. That is far " +
            "above the roughly one-in-thirty-eight measured here, so it is a " +
            "SECOND difference between the two calls, not this one — check what else changed");
    }

    /// <summary>
    /// <b>The exact input that first parts the two readings.</b>
    /// </summary>
    /// <remarks>
    /// This was the shape of the check the external vectors eventually ran: compute both readings
    /// for a password and salt, and see which one matches what a different implementation stored.
    /// It is kept because it holds a case where that question <i>has</i> an answer — for most
    /// passwords it does not, since both readings give the same key, and a test that picked an
    /// arbitrary password would pass under either.
    /// </remarks>
    [Fact]
    public void TheFirstInputThatPartsTheTwoReadingsIsRecorded()
    {
        List<int> diverged = Divergences();
        int first = Assert.IsType<int>(diverged[0]);

        byte[] password = Password(first);
        byte[] mine = ThisLibrary(password);
        byte[] superseded = SupersededReading(password);

        Assert.Equal(32, mine.Length);
        Assert.Equal(32, superseded.Length);
        Assert.False(AreEqual(mine, superseded),
            $"password \"r6-reading-{first}\" with the fixed salt was recorded as diverging");
    }

    /// <summary>
    /// <b>What this implementation actually computes, for one fixed input.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>Every other test in this file compares the two readings against each other, and a
    /// mutation proved that is a blind spot.</b> Deleting the 64-round minimum changes about
    /// six hashes in seven — and changes them on <i>both</i> sides, so every relative assertion here
    /// stayed green. The R6 round-trip tests elsewhere are blind for the same reason: they encrypt
    /// and decrypt with the same code. Nothing in the tree could see it.</para>
    ///
    /// <para><b>This pins the answer itself, and it is a characterisation test, not a correctness
    /// one.</b> The value came from this implementation, so it proves nothing about whether the
    /// implementation is right — <see cref="HashR6ExternalVectorTests"/> is where correctness now
    /// lives, because its expected values came from somebody else's code. What this does is make an
    /// <i>unintended</i> change to Algorithm 2.B loud, whichever constant or step it touches.</para>
    ///
    /// <para><b>The value did not move when the termination offset was corrected</b>, and that is
    /// worth stating rather than leaving a reader to wonder whether the pin went stale: password
    /// <c>"r6-vector-probe"</c> with salt <c>11 22 33 44 55 66 77 88</c> is one of the
    /// thirty-seven-in-thirty-eight inputs where the two readings agree. Which is exactly why a
    /// single pinned input is not enough on its own — <see cref="TheHashOfADivergingInputIsPinnedToo"/>
    /// covers the other case.</para>
    /// </remarks>
    [Fact]
    public void TheHashOfAFixedInputIsPinnedSoAnUnintendedChangeIsLoud()
    {
        byte[] hash = PdfEncryptionPrimitives.ComputeHashR6(
            Encoding.UTF8.GetBytes("r6-vector-probe"), Salt);

        Assert.Equal(
            "b3dc60260f910872e6ba3033b01cd97a61bc74a928571df1b63a663e76418bbc",
            BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant());
    }

    /// <summary>
    /// <b>And the hash of an input where the readings DO differ.</b>
    /// </summary>
    /// <remarks>
    /// <para>The pin above is blind to the one change this area actually had: for its input both
    /// readings give the same answer, so it stayed green while the termination offset was corrected
    /// — a characterisation test that could not characterise the only thing that moved.</para>
    /// <para>This one uses a password from the measured divergence list, so it pins a value that
    /// <i>is</i> a function of the offset. It is still characterisation, not correctness: the number
    /// came from here. The correctness claim for this same class of input is
    /// <see cref="HashR6ExternalVectorTests"/>, whose six discriminating rows came from an independent checker and
    /// an independent implementation.</para>
    /// </remarks>
    [Fact]
    public void TheHashOfADivergingInputIsPinnedToo()
    {
        byte[] hash = PdfEncryptionPrimitives.ComputeHashR6(Password(Divergences()[0]), Salt);

        Assert.Equal(
            "104629bba8da930d1504554b7439dd48885a09c348b7a86069d5045d6ae7cdc2",
            BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant());
    }

    /// <summary>
    /// The offsets differ by exactly one, and production uses the settled one.
    /// </summary>
    /// <remarks>
    /// <para>A guard on the two constants rather than on behaviour: the superseded offset exists
    /// only for this comparison and for the external vectors' discrimination check, and if someone
    /// reconciles them the comparison silently becomes a tautology and
    /// <see cref="TheTwoReadingsDisagreeOftenEnoughToMatter"/> starts failing for a reason that has
    /// nothing to do with the algorithm.</para>
    /// <para><b>The superseded reading is the STRICTER one</b> — <c>E[^1] &lt;= i - 32</c> is
    /// harder to satisfy than <c>&lt;= i - 31</c>, so it can run an extra round. Stating the
    /// direction here is what stops the pair being "corrected" back.</para>
    /// </remarks>
    [Fact]
    public void TheSupersededReadingIsExactlyOneRoundStricter()
    {
        Assert.Equal(PdfEncryptionPrimitives.HashR6TerminationOffset + 1,
                     PdfEncryptionPrimitives.HashR6SupersededTerminationOffset);

        Assert.Equal(31, PdfEncryptionPrimitives.HashR6TerminationOffset);
    }

    /// <summary>
    /// The parameterless entry point is the settled reading — production behaviour is unchanged by
    /// the parameter existing.
    /// </summary>
    /// <remarks>
    /// <b>It has to be asked with a password where the two readings differ.</b> For roughly
    /// thirty-seven passwords in thirty-eight they agree, so a test using an arbitrary one would
    /// pass with the default entry point silently switched to the other reading — the exact
    /// regression this is here to catch, and an assertion that cannot fail is worse than none.
    /// </remarks>
    [Fact]
    public void TheDefaultEntryPointUsesTheSettledReadingAndNotTheSupersededOne()
    {
        byte[] password = Password(Divergences()[0]);

        Assert.Equal(
            PdfEncryptionPrimitives.ComputeHashR6(password, Salt),
            PdfEncryptionPrimitives.ComputeHashR6(
                password, Salt, null, PdfEncryptionPrimitives.HashR6TerminationOffset));

        Assert.NotEqual(
            PdfEncryptionPrimitives.ComputeHashR6(password, Salt),
            PdfEncryptionPrimitives.ComputeHashR6(
                password, Salt, null, PdfEncryptionPrimitives.HashR6SupersededTerminationOffset));
    }
}
