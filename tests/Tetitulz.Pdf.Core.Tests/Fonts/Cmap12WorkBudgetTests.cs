// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A format-12 <c>cmap</c>'s groups are visited under one cumulative budget, as format 4's segments
/// are: groups that overlap never raise the map's size, so a hundred thousand copies of one
/// 0x1FFFF-wide group were 1.3×10^10 writes with neither of the size breaks firing. The budget's
/// evidence is the visit count, not a clock; an ordinary subtable is read whole (the control).
/// </summary>
public sealed class Cmap12WorkBudgetTests
{
    private static byte[] Cmap12(params (uint Start, uint End, uint Gid)[] groups)
    {
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        U16(0); U16(1);                 // cmap version, one subtable
        U16(3); U16(10); U32(12);       // (3, 10) at offset 12
        U16(12); U16(0);                // format 12, reserved
        U32((uint)(16 + 12 * groups.Length));
        U32(0);                         // language
        U32((uint)groups.Length);
        foreach (var (s, e, g) in groups) { U32(s); U32(e); U32(g); }
        return ms.ToArray();
    }

    private static TrueTypeFontProgram Parse(byte[] cmap)
        => TrueTypeFontProgram.Parse(MinimalTrueType.BuildWithTables("A", new Dictionary<string, byte[]> { ["cmap"] = cmap }));

    [Fact]
    public void AdversarialOverlappingGroups_AreVisitedUnderOneBudget()
    {
        var groups = new (uint, uint, uint)[100_000];
        for (int i = 0; i < groups.Length; i++) groups[i] = (0x10000, 0x2FFFF, 1);

        var font = Parse(Cmap12(groups));

        Assert.NotNull(font);
        Assert.InRange(font.Cmap12CodePointVisits, 1, 4L * 0x20000 + 2);   // the budget, and the visit that found it spent
    }

    [Fact]
    public void AnOrdinaryFormat12Subtable_IsReadWhole()
    {
        var font = Parse(Cmap12((0x41, 0x43, 1), (0x1F600, 0x1F601, 4)));

        Assert.NotNull(font);
        Assert.Equal(5, font.Cmap12CodePointVisits);
    }
}
