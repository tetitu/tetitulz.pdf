// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util;

/// <summary>
/// <see cref="KeyedHash"/> is SipHash-2-4, and the keys a file can make collide under an unkeyed hash
/// — reals under <c>double.GetHashCode</c>, names under .NET Framework's string hash, bitmaps under
/// FNV-1 — spread across buckets under it.
/// </summary>
public sealed class KeyedHashTests
{
    // The reference implementation's key, 00 01 … 0f, as SipHash's two words.
    private const ulong K0 = 0x0706050403020100UL;
    private const ulong K1 = 0x0f0e0d0c0b0a0908UL;

    private static byte[] Counting(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)i;
        return data;
    }

    /// <summary>
    /// The reference implementation's test vectors (SipHash-2-4, key 00…0f, message 00 01 02 … of
    /// each length): the empty message, one byte, one whole word, and the paper's fifteen-byte example.
    /// </summary>
    [Theory]
    [InlineData(0, 0x726fdb47dd0e0e31UL)]
    [InlineData(1, 0x74f839c593dc67fdUL)]
    [InlineData(8, 0x93f5f5799a932462UL)]
    [InlineData(15, 0xa129ca6149be45e5UL)]
    public void Hash_MatchesTheReferenceVectors(int length, ulong expected)
    {
        Assert.Equal(expected, KeyedHash.Hash(K0, K1, Counting(length), 0, length));
    }

    /// <summary>An offset into a larger array hashes the bytes it names, not the array's start.</summary>
    [Fact]
    public void Hash_ReadsFromTheOffset()
    {
        byte[] padded = new byte[20];
        Array.Copy(Counting(15), 0, padded, 3, 15);
        Assert.Equal(0xa129ca6149be45e5UL, KeyedHash.Hash(K0, K1, padded, 3, 15));
    }

    /// <summary>The one-word form is the eight-byte form of the word's little-endian bytes.</summary>
    [Fact]
    public void OneWordForm_IsTheEightByteForm()
    {
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var bytes = new byte[8];
            random.NextBytes(bytes);
            ulong word = BitConverter.ToUInt64(bytes, 0);
            Assert.Equal(KeyedHash.Hash(K0, K1, bytes, 0, 8), KeyedHash.Hash(K0, K1, word));
        }
        Assert.Equal(0x93f5f5799a932462UL, KeyedHash.Hash(K0, K1, 0x0706050403020100UL));
    }

    /// <summary>The string form is the byte form of the string's UTF-16 little-endian bytes, at every length's remainder.</summary>
    [Fact]
    public void StringForm_IsTheUtf16LittleEndianBytes()
    {
        foreach (string text in new[] { "", "A", "Ty", "Typ", "Type", "XObject", "Font Descriptor", "אבג", "😀x" })
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            Assert.Equal(KeyedHash.Hash(K0, K1, bytes, 0, bytes.Length), KeyedHash.HashChars(K0, K1, text));
        }
    }

    /// <summary>Equal numbers hash alike, 0 and −0 among them; different numbers do not all hash alike.</summary>
    [Fact]
    public void Of_Double_EqualValuesHashAlike()
    {
        Assert.Equal(KeyedHash.Of(0.0), KeyedHash.Of(-0.0));
        Assert.Equal(KeyedHash.Of(1.5), KeyedHash.Of(3.0 / 2.0));
        int distinct = Enumerable.Range(0, 100).Select(i => KeyedHash.Of((double)i)).Distinct().Count();
        Assert.True(distinct >= 99, $"{distinct} distinct hashes for 100 integers");
    }

    /// <summary>Reals whose two halves XOR to one value.</summary>
    private static List<double> RealsSharingDoubleGetHashCode(int count)
    {
        var values = new List<double>();
        for (uint lo = 1; lo <= count; lo++)
            values.Add(BitConverter.Int64BitsToDouble((long)(((ulong)(0x3FF00000u ^ lo) << 32) | lo)));
        return values;
    }

    /// <summary>
    /// 4 096 reals that share one <c>double.GetHashCode</c> — every one in one bucket of a table keyed
    /// by it — take nearly as many hashes under the keyed hash.
    /// </summary>
    [Fact]
    public void Of_Double_SpreadsRealsThatShareDoubleGetHashCode()
    {
        List<double> reals = RealsSharingDoubleGetHashCode(4096);
        Assert.Equal(4096, reals.Distinct().Count());
        Assert.Single(reals.Select(v => v.GetHashCode()).Distinct());

        int distinct = reals.Select(KeyedHash.Of).Distinct().Count();
        Assert.True(distinct >= 4090, $"{distinct} distinct keyed hashes for 4096 reals");
    }

    /// <summary>
    /// .NET Framework's 64-bit <c>string.GetHashCode</c> when randomized hashing is off: two DJB-style
    /// chains over the even and the odd characters. Its answers for the strings below are what
    /// .NET Framework 4.8 returned for them.
    /// </summary>
    private static int NetFrameworkStringHash(string s)
    {
        int hash1 = 5381, hash2 = 5381;
        for (int i = 0; i < s.Length; i += 2)
        {
            hash1 = unchecked(((hash1 << 5) + hash1) ^ s[i]);
            if (i + 1 >= s.Length) break;
            hash2 = unchecked(((hash2 << 5) + hash2) ^ s[i + 1]);
        }
        return unchecked(hash1 + hash2 * 1566083941);
    }

    /// <summary>The model gives .NET Framework 4.8's own answers, so names that collide under it collide there.</summary>
    [Theory]
    [InlineData("a", 372029373)]
    [InlineData("abc", 1099313834)]
    [InlineData("Type", 1421152990)]
    [InlineData("Font", -864298781)]
    [InlineData("XObject", -668179875)]
    [InlineData("hello world", 1118511802)]
    public void NetFrameworkStringHash_IsTheRuntimesAnswer(string text, int framework)
    {
        Assert.Equal(framework, NetFrameworkStringHash(text));
    }

    /// <summary>
    /// 2^<paramref name="stages"/> names that share one .NET Framework string hash: the odd characters
    /// fixed, so the second chain is the same for all, and each four even characters one of two blocks
    /// that take the first chain to the same state.
    /// </summary>
    private static List<string> NamesSharingNetFrameworkHash(int stages)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random(1);
        uint state = 5381u;
        var pairs = new List<string[]>();
        for (int stage = 0; stage < stages; stage++)
        {
            var seen = new Dictionary<uint, string>();
            while (true)
            {
                var block = new char[4];
                for (int k = 0; k < 4; k++) block[k] = alphabet[random.Next(alphabet.Length)];
                string text = new string(block);
                uint next = state;
                foreach (char c in text) next = unchecked((next * 33u) ^ c);
                if (seen.TryGetValue(next, out string other) && other != text)
                {
                    pairs.Add(new[] { other, text });
                    state = next;
                    break;
                }
                seen[next] = text;
            }
        }
        var names = new List<string>();
        for (int m = 0; m < 1 << stages; m++)
        {
            var sb = new StringBuilder();
            for (int stage = 0; stage < stages; stage++)
                foreach (char c in pairs[stage][(m >> stage) & 1]) sb.Append(c).Append('x');
            names.Add(sb.ToString());
        }
        return names;
    }

    /// <summary>
    /// 1 024 names that share one .NET Framework string hash take nearly as many hashes under the
    /// string form .NET Framework uses, and under a <see cref="PdfName"/>'s hash on this runtime.
    /// </summary>
    [Fact]
    public void StringForm_SpreadsNamesThatShareTheNetFrameworkHash()
    {
        List<string> names = NamesSharingNetFrameworkHash(10);
        Assert.Equal(1024, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(names.Select(NetFrameworkStringHash).Distinct());

        int chars = names.Select(n => KeyedHash.HashChars(K0, K1, n)).Distinct().Count();
        Assert.True(chars >= 1020, $"{chars} distinct SipHash values for 1024 names");
        int pdfNames = names.Select(n => new PdfName(n).GetHashCode()).Distinct().Count();
        Assert.True(pdfNames >= 1020, $"{pdfNames} distinct PdfName hashes for 1024 names");
    }

    /// <summary>A name's hash is its characters', so equal names hash alike, whatever instance.</summary>
    [Fact]
    public void PdfName_EqualNamesHashAlike()
    {
        Assert.Equal(new PdfName("Resources").GetHashCode(), PdfName.Resources.GetHashCode());
        Assert.Equal(KeyedHash.Of("Resources"), PdfName.Resources.GetHashCode());
        Assert.NotEqual(PdfName.Font.GetHashCode(), PdfName.XObject.GetHashCode());
    }

    /// <summary>
    /// <see cref="KeyedHash.Strings"/> compares ordinally and hashes as <see cref="KeyedHash.Of(string)"/>,
    /// so a LINQ grouping under it hashes as the string form does.
    /// </summary>
    [Fact]
    public void Strings_ComparesOrdinallyAndHashesAsOf()
    {
        IEqualityComparer<string> strings = KeyedHash.Strings;
        Assert.True(strings.Equals("Font", new string("Font".ToCharArray())));
        Assert.False(strings.Equals("Font", "font"));
        Assert.False(strings.Equals("Font", null!));
        Assert.Equal(KeyedHash.Of("XObject"), strings.GetHashCode("XObject"));
        Assert.Equal(0, strings.GetHashCode(null!));

        List<string> names = NamesSharingNetFrameworkHash(6);
        Assert.Equal(64, names.GroupBy(n => n, strings).Count());
    }

    /// <summary>
    /// A comparer from <see cref="KeyedHash.Comparer{T}(Func{T, int})"/> hashes through the function it
    /// is given and compares as the type does: a tuple's own equality, its strings ordinally.
    /// </summary>
    [Fact]
    public void Comparer_HashesThroughItsFunctionAndComparesAsTheType()
    {
        IEqualityComparer<(string, int)> keys =
            KeyedHash.Comparer<(string, int)>(k => unchecked(KeyedHash.Of(k.Item1) * 31 + k.Item2));
        Assert.Equal(unchecked(KeyedHash.Of("Font") * 31 + 2), keys.GetHashCode(("Font", 2)));
        Assert.True(keys.Equals(("Font", 2), (new string("Font".ToCharArray()), 2)));
        Assert.False(keys.Equals(("Font", 2), ("font", 2)));
        Assert.False(keys.Equals(("Font", 2), ("Font", 3)));
        Assert.Throws<ArgumentNullException>(() => KeyedHash.Comparer<(string, int)>(null));
    }

    /// <summary>FNV-1 of a symbol key's size and bytes, the unkeyed hash an image can make collide.</summary>
    private static int Fnv1(int width, int height, byte[] bits)
    {
        unchecked
        {
            int h = (int)2166136261 ^ width;
            h = (h * 16777619) ^ height;
            foreach (byte b in bits) h = (h * 16777619) ^ b;
            return h;
        }
    }

    /// <summary>
    /// 1 024 symbol bitmaps of one size that share one FNV-1 hash — chained two-way collisions, each
    /// stage four bytes — take nearly as many hashes as JBIG2 symbol keys.
    /// </summary>
    [Fact]
    public void Jbig2SymbolKey_SpreadsBitmapsThatShareAnFnvHash()
    {
        const int width = 32, height = 10, stages = 10;   // a row of four bytes a stage
        var random = new Random(3);
        int state = unchecked(((int)2166136261 ^ width) * 16777619 ^ height);
        var pairs = new List<byte[][]>();
        for (int stage = 0; stage < stages; stage++)
        {
            var seen = new Dictionary<int, byte[]>();
            while (true)
            {
                var block = new byte[4];
                random.NextBytes(block);
                int next = state;
                foreach (byte b in block) next = unchecked((next * 16777619) ^ b);
                if (seen.TryGetValue(next, out byte[] other) && !other.AsSpan().SequenceEqual(block))
                {
                    pairs.Add(new[] { other, block });
                    state = next;
                    break;
                }
                seen[next] = block;
            }
        }
        var bitmaps = new List<byte[]>();
        for (int m = 0; m < 1 << stages; m++)
        {
            var bits = new byte[4 * stages];
            for (int stage = 0; stage < stages; stage++) pairs[stage][(m >> stage) & 1].CopyTo(bits, 4 * stage);
            bitmaps.Add(bits);
        }
        Assert.Single(bitmaps.Select(b => Fnv1(width, height, b)).Distinct());

        var keys = bitmaps.Select(b => new Jbig2SymbolEncoder.SymbolKey(width, height, b)).ToList();
        Assert.Equal(1024, keys.Distinct().Count());
        int distinct = keys.Select(k => k.GetHashCode()).Distinct().Count();
        Assert.True(distinct >= 1020, $"{distinct} distinct hashes for 1024 symbol keys");
    }
}
