// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests.TestInfrastructure;

/// <summary>
/// The one property the key pool in <see cref="SigningTestCrypto.Keys"/> must keep: every key a test
/// is given differs from every other key that test is given — including one made by a shared
/// fixture. Tests such as a CRL "forged under the issuer's name" are only meaningful because of it.
/// </summary>
public class SigningTestCryptoKeyPoolTests
{
    private static System.Numerics.BigInteger Modulus(Org.BouncyCastle.Crypto.AsymmetricCipherKeyPair kp)
        => new(((RsaKeyParameters)kp.Public).Modulus.ToByteArrayUnsigned(), isUnsigned: true, isBigEndian: true);

    [Fact]
    public void EveryKeyATestDrawsIsDistinct()
    {
        var seen = new System.Collections.Generic.HashSet<System.Numerics.BigInteger>();
        for (int i = 0; i < 6; i++)
            Assert.True(seen.Add(Modulus(SigningTestCrypto.Keys())), $"key {i} repeats an earlier one");
    }

    /// <summary>The cursor follows the test across an <c>await</c>, so a key drawn after one is still new.</summary>
    [Fact]
    public async Task AKeyDrawnAfterAnAwaitIsStillDistinct()
    {
        var first = Modulus(SigningTestCrypto.Keys());
        await Task.Yield();
        var second = Modulus(SigningTestCrypto.Keys());

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// A shared fixture's key is never the one another test starts with — the reason
    /// <see cref="SigningTestCrypto.Shared{T}"/> exists.
    /// </summary>
    /// <remarks>
    /// <para>"Another test" is a fresh execution flow, whose cursor starts at zero — and this test's
    /// own cursor also starts at zero, so a broken <c>Shared</c> would hand the fixture exactly the
    /// key the fresh flow draws. Comparing the shared key with a key drawn in <i>this</i> test could
    /// not fail: a broken <c>Shared</c> would take this test's first key and the comparison would
    /// meet its second.</para>
    ///
    /// <para><b>A new <see cref="System.Threading.Thread"/> started with flow suppressed, not a
    /// <c>Task.Run</c>.</b> A thread-pool thread keeps the context its last work item left, so a
    /// suppressed-flow <c>Task.Run</c> read a cursor some earlier draw had advanced — which is how
    /// the first version of this test survived the very mutation it was written for.</para>
    /// </remarks>
    [Fact]
    public void ASharedFixturesKeyIsNotTheKeyAnotherTestStartsWith()
    {
        var shared = Modulus(SigningTestCrypto.Shared(() => SigningTestCrypto.Keys()));

        System.Numerics.BigInteger other = default;
        var thread = new System.Threading.Thread(() => other = Modulus(SigningTestCrypto.Keys()));
        using (System.Threading.ExecutionContext.SuppressFlow())
            thread.Start();
        thread.Join();

        Assert.NotEqual(shared, other);
    }
}
