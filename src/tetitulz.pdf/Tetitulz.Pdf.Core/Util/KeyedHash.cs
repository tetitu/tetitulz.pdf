// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// The hash a hash table gives a key a file chose: SipHash-2-4 under a 128-bit key drawn once a
    /// process, so a file cannot choose many keys that share one bucket.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a keyed hash.</b> A hash table compares a key with every key in its bucket, so n
    /// keys in one bucket cost n² comparisons to insert. A hash anyone can compute lets a file choose
    /// such keys. <c>double.GetHashCode</c> XORs the value's two 32-bit halves on every runtime, so
    /// any reals whose halves XOR to one value share a bucket: 131 072 of them in a 3.5 MB PostScript
    /// program take 112 s to define in one dictionary under it, against 0.3 s for random reals. On
    /// .NET Framework <c>string.GetHashCode</c> is one fixed function, so names can be chosen to
    /// collide offline in the same way. Under a key the file cannot see, no choice of keys does better
    /// than chance.</para>
    /// <para><b>Strings.</b> On .NET Core and later <c>string.GetHashCode</c> is already keyed per
    /// process, so <see cref="Of(string)"/> answers it there; on .NET Framework it is SipHash over the
    /// string's UTF-16 code units. <c>Dictionary&lt;string, …&gt;</c> and <c>HashSet&lt;string&gt;</c>
    /// protect themselves on .NET Framework by switching to a randomized comparer when a bucket grows
    /// long; a key type of the library's own (<c>PdfName</c>, a PostScript key) does not, so it hashes
    /// through here. A tuple holding a string, and LINQ's <c>Distinct</c>, <c>GroupBy</c> and
    /// <c>ToLookup</c>, hash a string with the runtime's function: on .NET Framework the host makes
    /// that keyed with <c>&lt;UseRandomizedStringHashAlgorithm enabled="1" /&gt;</c> in its
    /// configuration file's <c>&lt;runtime&gt;</c> section.</para>
    /// <para>Public because a plugin needs it: Core's <c>InternalsVisibleTo</c> names no plugin.</para>
    /// </remarks>
    public static class KeyedHash
    {
        private static readonly ulong K0;
        private static readonly ulong K1;

        static KeyedHash()
        {
            var key = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
            K0 = Word(key, 0);
            K1 = Word(key, 8);
        }

        /// <summary>The hash of a number: equal values hash alike, 0 and −0 included.</summary>
        public static int Of(double value)
        {
            if (value == 0) value = 0;   // −0 equals 0, so it is the same key
            return Fold(Hash(K0, K1, unchecked((ulong)BitConverter.DoubleToInt64Bits(value))));
        }

        /// <summary>The hash of a string's characters, compared ordinally; 0 for null.</summary>
        public static int Of(string value)
        {
            if (value == null) return 0;
#if NET
            return value.GetHashCode();
#else
            return Fold(HashChars(K0, K1, value));
#endif
        }

        /// <summary>The hash of <paramref name="count"/> bytes of <paramref name="data"/> from <paramref name="offset"/>.</summary>
        public static int Of(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count), "The range is outside the array.");
            return Fold(Hash(K0, K1, data, offset, count));
        }

        private static int Fold(ulong hash) => unchecked((int)hash ^ (int)(hash >> 32));

        /// <summary>SipHash-2-4 of a run of bytes under the key (<paramref name="k0"/>, <paramref name="k1"/>).</summary>
        internal static ulong Hash(ulong k0, ulong k1, byte[] data, int offset, int count)
        {
            ulong v0 = k0 ^ 0x736f6d6570736575UL, v1 = k1 ^ 0x646f72616e646f6dUL;
            ulong v2 = k0 ^ 0x6c7967656e657261UL, v3 = k1 ^ 0x7465646279746573UL;
            int i = offset, end = offset + count;
            for (; end - i >= 8; i += 8) Compress(ref v0, ref v1, ref v2, ref v3, Word(data, i));
            ulong last = unchecked((ulong)count << 56);
            for (int shift = 0; i < end; i++, shift += 8) last |= (ulong)data[i] << shift;
            return Finish(ref v0, ref v1, ref v2, ref v3, last);
        }

        /// <summary>SipHash-2-4 of one 64-bit word's eight little-endian bytes.</summary>
        internal static ulong Hash(ulong k0, ulong k1, ulong word)
        {
            ulong v0 = k0 ^ 0x736f6d6570736575UL, v1 = k1 ^ 0x646f72616e646f6dUL;
            ulong v2 = k0 ^ 0x6c7967656e657261UL, v3 = k1 ^ 0x7465646279746573UL;
            Compress(ref v0, ref v1, ref v2, ref v3, word);
            return Finish(ref v0, ref v1, ref v2, ref v3, 8UL << 56);
        }

        /// <summary>SipHash-2-4 of a string's UTF-16 code units as little-endian bytes.</summary>
        internal static ulong HashChars(ulong k0, ulong k1, string text)
        {
            ulong v0 = k0 ^ 0x736f6d6570736575UL, v1 = k1 ^ 0x646f72616e646f6dUL;
            ulong v2 = k0 ^ 0x6c7967656e657261UL, v3 = k1 ^ 0x7465646279746573UL;
            int n = text.Length, i = 0;
            for (; n - i >= 4; i += 4)
                Compress(ref v0, ref v1, ref v2, ref v3,
                    text[i] | (ulong)text[i + 1] << 16 | (ulong)text[i + 2] << 32 | (ulong)text[i + 3] << 48);
            ulong last = unchecked((ulong)(2L * n) << 56);
            for (int shift = 0; i < n; i++, shift += 16) last |= (ulong)text[i] << shift;
            return Finish(ref v0, ref v1, ref v2, ref v3, last);
        }

        private static void Compress(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3, ulong m)
        {
            v3 ^= m;
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        private static ulong Finish(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3, ulong last)
        {
            Compress(ref v0, ref v1, ref v2, ref v3, last);
            v2 ^= 0xff;
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            return v0 ^ v1 ^ v2 ^ v3;
        }

        private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
        {
            unchecked
            {
                v0 += v1; v1 = Rotl(v1, 13); v1 ^= v0; v0 = Rotl(v0, 32);
                v2 += v3; v3 = Rotl(v3, 16); v3 ^= v2;
                v0 += v3; v3 = Rotl(v3, 21); v3 ^= v0;
                v2 += v1; v1 = Rotl(v1, 17); v1 ^= v2; v2 = Rotl(v2, 32);
            }
        }

        private static ulong Rotl(ulong x, int bits) => (x << bits) | (x >> (64 - bits));

        private static ulong Word(byte[] data, int i)
            => data[i] | (ulong)data[i + 1] << 8 | (ulong)data[i + 2] << 16 | (ulong)data[i + 3] << 24
             | (ulong)data[i + 4] << 32 | (ulong)data[i + 5] << 40 | (ulong)data[i + 6] << 48 | (ulong)data[i + 7] << 56;
    }
}
