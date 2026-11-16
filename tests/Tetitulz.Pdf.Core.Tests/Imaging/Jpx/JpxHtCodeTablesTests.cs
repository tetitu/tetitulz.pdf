// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The CxtVLC tables of ITU-T T.814 Annex C (<see cref="JpxHtCodeTables"/>), checked for what a lost
/// or mistyped entry would break: each context's codewords a complete prefix code, every entry
/// consistent with §7.3.2, and the lookup the decoder reads holding exactly the table's entries.
/// </summary>
public sealed class JpxHtCodeTablesTests
{
    public static IEnumerable<object[]> Tables()
    {
        yield return new object[] { 0, 444 };
        yield return new object[] { 1, 358 };
    }

    private static byte[] Table(int which) => which == 0 ? JpxHtCodeTables.Table0 : JpxHtCodeTables.Table1;

    /// <summary>
    /// The annex's entry count, and per context a complete prefix code: no codeword a prefix of
    /// another (bits are read least significant first) and a Kraft sum of exactly 1 — which is also
    /// why the decoder's codeword loop always ends by the seventh bit.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void EachContextIsACompletePrefixCode(int which, int entries)
    {
        byte[] t = Table(which);
        Assert.Equal(entries * JpxHtCodeTables.FieldsPerEntry, t.Length);
        for (int c = 0; c < 8; c++)
        {
            var codes = new List<(int W, int L)>();
            for (int i = 0; i < t.Length; i += JpxHtCodeTables.FieldsPerEntry)
                if (t[i] == c) codes.Add((t[i + 5], t[i + 6]));
            Assert.NotEmpty(codes);

            long kraft = 0;   // in units of 2^-7
            foreach (var (w, l) in codes)
            {
                Assert.InRange(l, 1, JpxHtCodeTables.MaxCodewordLength);
                kraft += 1L << (JpxHtCodeTables.MaxCodewordLength - l);
                foreach (var (w2, l2) in codes)
                    if ((w2, l2) != (w, l) && l <= l2)
                        Assert.False((w2 & ((1 << l) - 1)) == w, $"table {which}, context {c}: {w}/{l} is a prefix of {w2}/{l2}");
            }
            Assert.Equal(1L << JpxHtCodeTables.MaxCodewordLength, kraft);
        }
    }

    /// <summary>§7.3.2: ϵ<sup>1</sup> ⊆ ϵ<sup>k</sup> ⊆ ρ, both 0 when u<sup>off</sup> is 0, and ρ a 4-bit pattern.</summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void EveryEntryIsConsistentWithItsSignificancePattern(int which, int entries)
    {
        byte[] t = Table(which);
        Assert.Equal(entries * JpxHtCodeTables.FieldsPerEntry, t.Length);
        for (int i = 0; i < t.Length; i += JpxHtCodeTables.FieldsPerEntry)
        {
            int rho = t[i + 1], uoff = t[i + 2], ek = t[i + 3], e1 = t[i + 4];
            Assert.InRange(t[i], 0, 7);
            Assert.InRange(rho, 0, 15);
            Assert.InRange(uoff, 0, 1);
            Assert.Equal(0, e1 & ~ek);
            Assert.Equal(0, ek & ~rho);
            if (uoff == 0) Assert.Equal((0, 0), (ek, e1));
        }
    }

    /// <summary>
    /// The lookup holds each entry at its slot, packed as the decoder unpacks it, and nothing else:
    /// as many filled slots as the table has entries. Two spot values read against the annex: the
    /// first entry of each table.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void TheLookupHoldsExactlyTheTablesEntries(int which, int entries)
    {
        byte[] t = Table(which);
        ushort[] lookup = JpxHtCodeTables.Lookup(initialRow: which == 0);
        int filled = 0;
        foreach (ushort slot in lookup) if (slot != 0) filled++;
        Assert.Equal(entries, filled);

        for (int i = 0; i < t.Length; i += JpxHtCodeTables.FieldsPerEntry)
        {
            int slot = lookup[t[i] * 256 + ((1 << t[i + 6]) | t[i + 5])];
            Assert.Equal(JpxHtCodeTables.Present, slot & JpxHtCodeTables.Present);
            Assert.Equal((t[i + 1], t[i + 2], t[i + 3], t[i + 4]), (slot & 0xF, (slot >> 4) & 1, (slot >> 5) & 0xF, (slot >> 9) & 0xF));
        }

        // Annex C's first entries: {0, 0x1, 0x0, 0x0, 0x0, 0x06, 4} and {0, 0x1, 0x0, 0x0, 0x0, 0x00, 3}.
        Assert.Equal(which == 0 ? new byte[] { 0, 1, 0, 0, 0, 0x06, 4 } : new byte[] { 0, 1, 0, 0, 0, 0x00, 3 }, t[..7]);
    }
}
