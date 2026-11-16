// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// RSA key pairs for tests — <b>distinct from every other key the same test has been given</b>,
/// and shared between tests. Every test project's certificate helpers draw from here.
/// </summary>
/// <remarks>
/// <para><b>Why a pool: key generation dominated the signing suites.</b> BouncyCastle's managed
/// generator takes about a quarter of a second per 2048-bit key; measured serially it was 139 s of
/// <c>Tetitulz.Pdf.Signing.Tests</c>' 178 s run, for keys nothing asserts anything about except that
/// they differ. With the pool that suite went from 31 s to 9 s, and Core's serial run from 93 s to
/// 51 s.</para>
///
/// <para><b>Distinctness within a test is the property that must survive</b> — a CRL "forged under
/// the issuer's name", a forged OCSP response, an attacker-issued responder certificate are only
/// forgeries because their key is not the real one. Each test walks the pool with its own cursor, an
/// <see cref="AsyncLocal{T}"/> that follows the test across <c>await</c>s and does not leak into a
/// sibling test, so the n-th key a test draws is the pool's n-th. Two draws in one test never
/// collide; different tests reuse the same keys, which none of them can observe. Were the cursor
/// ever to leak it would only keep counting up: fewer reuses, never a collision.</para>
///
/// <para><b>A key made outside a test's own flow must be made inside <see cref="Shared{T}"/></b> —
/// a <c>static</c> field or <c>static Lazy</c> shared by a class. Drawn from the pool, it would take
/// its keys from whichever test touched it first, and every later test, starting its cursor at zero,
/// could be handed the same key for a certificate meant to be someone else's.</para>
///
/// <para><b>One implementation, here</b>, because every test project references this assembly. It
/// was written first in Signing.Tests and copied into Core.Tests; a comment saying the two agreed
/// was the duplication report.</para>
/// </remarks>
public static class RsaTestKeys
{
    private static readonly SecureRandom Random = new();
    private static readonly AsyncLocal<int> Cursor = new();
    private static readonly AsyncLocal<bool> Fresh = new();
    private static readonly ConcurrentDictionary<int, List<AsymmetricCipherKeyPair>> Pools = new();

    /// <summary>The next key this test has not yet been given (see the remarks).</summary>
    public static AsymmetricCipherKeyPair Next(int bits = 2048)
    {
        if (Fresh.Value) return Generate(bits);

        int index = Cursor.Value;
        Cursor.Value = index + 1;

        List<AsymmetricCipherKeyPair> pool = Pools.GetOrAdd(bits, _ => new List<AsymmetricCipherKeyPair>());
        lock (pool)
        {
            while (pool.Count <= index) pool.Add(Generate(bits));
            return pool[index];
        }
    }

    /// <summary>
    /// Builds <paramref name="make"/> with keys generated fresh and never pooled — for a fixture
    /// shared across tests. See the remarks on <see cref="RsaTestKeys"/>.
    /// </summary>
    public static T Shared<T>(Func<T> make)
    {
        bool saved = Fresh.Value;
        Fresh.Value = true;
        try { return make(); }
        finally { Fresh.Value = saved; }
    }

    private static AsymmetricCipherKeyPair Generate(int bits)
    {
        var gen = new RsaKeyPairGenerator();
        gen.Init(new KeyGenerationParameters(Random, bits));
        return gen.GenerateKeyPair();
    }
}
