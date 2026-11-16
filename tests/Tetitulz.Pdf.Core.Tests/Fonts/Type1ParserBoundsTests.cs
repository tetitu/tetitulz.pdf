// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <c>Type1Parser</c>, batch 1: lengths near <c>int.MaxValue</c> in the section split, a charstring
/// entry and a <c>/Subrs</c> entry, a <c>/Subrs</c> count far past the data, and <c>/lenIV -1</c>.
/// </summary>
public class Type1ParserBoundsTests
{
    private const long AllocationCeiling = 16L * 1024 * 1024;

    private static readonly (string, byte[])[] OneGlyph = { ("A", MinimalType1.AdvanceOnly(500)) };

    /// <summary>
    /// <c>/lenIV -1</c> means the charstrings are not encrypted. The sign was not read, so it was
    /// taken as 0, "encrypted with no seed bytes", and every charstring decrypted into garbage. The
    /// control, the default 4, reads the same glyph.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    public void LenIV_MinusOneMeansUnencrypted(int lenIV)
    {
        var (program, length1, length2) = MinimalType1.Build(OneGlyph, lenIV: lenIV);

        Type1Font font = Type1Parser.Parse(program, length1, length2);

        Assert.NotNull(font);
        Assert.Equal(MinimalType1.AdvanceOnly(500), font.CharStrings["A"]);
    }

    /// <summary>
    /// Hostile charstring entry: <c>/B 2147483647 RD</c>. <c>p + len</c> wrapped negative and passed,
    /// and the decrypt asked for 2 GB; the throw lost the font and the glyph already read. The entry
    /// is skipped now and the scan reports itself incomplete.
    /// </summary>
    [Fact]
    public void ACharstringOfIntMaxValueBytes_KeepsTheFont()
    {
        var (program, length1, length2) = MinimalType1.Build(OneGlyph, extraCharStringsText: "/B 2147483647 RD ");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Type1Font font = Type1Parser.Parse(program, length1, length2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(font);
        Assert.True(font.CharStrings.ContainsKey("A"));
        Assert.False(font.CharStringsComplete);
        Assert.True(allocated < AllocationCeiling, $"allocated {allocated} bytes");
    }

    /// <summary>The same in a <c>/Subrs</c> entry.</summary>
    [Fact]
    public void ASubrOfIntMaxValueBytes_KeepsTheFont()
    {
        var (program, length1, length2) = MinimalType1.Build(OneGlyph,
            subrsText: "/Subrs 1 array\ndup 0 2147483647 RD x NP\nND\n");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Type1Font font = Type1Parser.Parse(program, length1, length2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(font);
        Assert.True(font.CharStrings.ContainsKey("A"));
        Assert.True(allocated < AllocationCeiling, $"allocated {allocated} bytes");
    }

    /// <summary>
    /// Hostile <c>/Subrs 50000000 array</c> in a section of a few hundred bytes. The array was
    /// allocated at the declared count, 400 MB of references; the section's length bounds it now.
    /// </summary>
    [Fact]
    public void ASubrsCountFarPastTheData_AllocatesForTheData()
    {
        var (program, length1, length2) = MinimalType1.Build(OneGlyph, subrsText: "/Subrs 50000000 array\nND\n");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Type1Font font = Type1Parser.Parse(program, length1, length2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(font);
        Assert.True(font.Subrs.Count <= program.Length, $"{font.Subrs.Count} subrs");
        Assert.True(allocated < AllocationCeiling, $"allocated {allocated} bytes");
    }

    /// <summary>
    /// Three thousand one-byte subroutines before the block's <c>def</c>. The walk sought that
    /// <c>def</c> afresh from every <c>dup</c>, reading the section once per subroutine — 180 MB for
    /// a 60 KB program; it is sought once now, and <see cref="Type1Parser.LastScanBytesExamined"/>
    /// is the evidence. The last subroutine is read, so the block was walked to its end.
    /// </summary>
    [Fact]
    public void ASubrsBlockOfThousands_IsScannedOnceNotOncePerEntry()
    {
        const int subrCount = 3000;
        var subrs = new StringBuilder("/Subrs " + subrCount + " array\n");
        for (int i = 0; i < subrCount; i++) subrs.Append("dup ").Append(i).Append(" 1 RD A NP\n");
        subrs.Append("ND\n");
        var (program, length1, length2) = MinimalType1.Build(OneGlyph, lenIV: -1, subrsText: subrs.ToString());

        Type1Font font = Type1Parser.Parse(program, length1, length2);

        Assert.NotNull(font);
        Assert.Equal(subrCount, font.Subrs.Count);
        Assert.Equal(new[] { (byte)'A' }, font.Subrs[subrCount - 1]);
        // At least the Subrs block once (the probe is live), and well under once per subroutine.
        long examined = Type1Parser.LastScanBytesExamined;
        Assert.InRange(examined, program.Length / 4, 20L * program.Length);
    }

    /// <summary>
    /// Hostile <c>/Length2</c>: <c>int.MaxValue - 100</c>. <c>Length1 + Length2</c> wrapped negative
    /// and passed, and the eexec section was copied into a 2 GB buffer. It is refused before any
    /// allocation now; the control, with the real lengths, parses.
    /// </summary>
    [Fact]
    public void ASectionSplitPastTheData_IsRefusedWithoutAllocating()
    {
        var (program, length1, length2) = MinimalType1.Build(OneGlyph);
        Assert.NotNull(Type1Parser.Parse(program, length1, length2));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Type1Font font = Type1Parser.Parse(program, length1, int.MaxValue - 100);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(font);
        Assert.True(allocated < AllocationCeiling, $"allocated {allocated} bytes");
    }
}
