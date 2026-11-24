// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="SfntBuilder"/> is the single sfnt container assembler — the prerequisite for
/// CFF/OTF subsetting, because the two assemblers that existed before it can only produce a
/// TrueType container.
/// </summary>
/// <remarks>
/// <para>These assert the parts of §5.2 that a font can get wrong while still rendering in most
/// viewers and failing validation in others: the directory sorted by tag, the checksum taken over
/// the <b>padded</b> bytes while the length field records the <b>actual</b> length, and
/// <c>head.checkSumAdjustment</c> = <c>0xB1B0AFBA − whole-file sum</c>.</para>
/// <para>The whole-file property is the strongest of them, because it is self-checking: summing a
/// correct font — adjustment field included — yields the magic constant. Nothing else here would
/// catch a padding byte in the wrong place.</para>
/// </remarks>
public class SfntBuilderTests
{
    private const uint CheckSumMagic = 0xB1B0AFBAu;

    [Fact]
    public void Build_EmitsTheRequestedSfntVersion()
    {
        // The reason this type exists: the version is a parameter, not a constant. An OpenType font
        // with CFF outlines is 'OTTO' and a TrueType one is 0x00010000; hardcoding it is what
        // turned one assembler into three.
        Assert.Equal(0x4F54544Fu, ReadU32(Build(SfntBuilder.VersionOpenTypeCff), 0));
        Assert.Equal(0x00010000u, ReadU32(Build(SfntBuilder.VersionTrueType), 0));
    }

    [Fact]
    public void Build_SortsTheDirectoryByTag()
    {
        // Handed in deliberately out of order. §5.2 requires ascending tag order, and a reader that
        // binary-searches the directory - which is what searchRange/entrySelector are for - gets
        // wrong answers otherwise.
        byte[] font = SfntBuilder.Build(SfntBuilder.VersionTrueType, new List<(string, byte[])>
        {
            ("post", new byte[] { 1 }),
            ("head", Head()),
            ("cmap", new byte[] { 2 }),
        });

        Assert.Equal(new[] { "cmap", "head", "post" }, DirectoryTags(font));
    }

    [Fact]
    public void Build_ChecksumsPaddedBytesButRecordsTheActualLength()
    {
        // A 5-byte table pads to 8. The length field must say 5 and the checksum must cover 8 -
        // getting either the other way round is the classic sfnt defect.
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        byte[] font = SfntBuilder.Build(SfntBuilder.VersionTrueType, new List<(string, byte[])>
        {
            ("head", Head()),
            ("test", payload),
        });

        (uint checksum, uint offset, uint length) = Entry(font, "test");

        Assert.Equal(5u, length);

        // The padded checksum: 0xAABBCCDD + 0xEE000000, the three pad bytes being zero.
        Assert.Equal(unchecked(0xAABBCCDDu + 0xEE000000u), checksum);

        // ...and the padding really is present and zero, so the next table starts 4-byte aligned.
        Assert.Equal(0, font[offset + 5]);
        Assert.Equal(0, font[offset + 6]);
        Assert.Equal(0, font[offset + 7]);
    }

    [Fact]
    public void Build_SetsHeadCheckSumAdjustmentSoTheWholeFileSumsToTheMagicConstant()
    {
        byte[] font = Build(SfntBuilder.VersionOpenTypeCff);

        // Self-checking: a correct font sums to 0xB1B0AFBA with the adjustment field left in place.
        Assert.Equal(CheckSumMagic, WholeFileSum(font));
    }

    [Fact]
    public void Build_WithNoHeadTable_StillProducesAValidContainer()
    {
        // Legal: not every sfnt carries /head. The builder must not index into a table that is not
        // there, or this shape would raise an IndexOutOfRangeException.
        byte[] font = SfntBuilder.Build(SfntBuilder.VersionTrueType, new List<(string, byte[])>
        {
            ("cmap", new byte[] { 1, 2, 3, 4 }),
        });

        Assert.NotNull(font);
        Assert.Equal(new[] { "cmap" }, DirectoryTags(font));
    }

    [Fact]
    public void Build_WithNoTables_ReturnsNullRatherThanAHeaderWithNothingInIt()
    {
        Assert.Null(SfntBuilder.Build(SfntBuilder.VersionTrueType, Array.Empty<(string, byte[])>()));
    }

    [Theory]
    [InlineData("toolong")]
    [InlineData("abc")]
    public void Build_WithATagThatIsNotFourCharacters_Throws(string tag)
    {
        // A three-character tag would silently read the next byte of the directory as part of it.
        Assert.Throws<ArgumentException>(() => SfntBuilder.Build(
            SfntBuilder.VersionTrueType, new List<(string, byte[])> { (tag, new byte[] { 1 }) }));
    }

    // ------------------------------------------------------------------

    private static byte[] Build(uint version) =>
        SfntBuilder.Build(version, new List<(string, byte[])>
        {
            ("head", Head()),
            ("cmap", new byte[] { 1, 2, 3 }),
            ("name", Encoding.ASCII.GetBytes("nm")),
        });

    /// <summary>A 54-byte /head, which is the real size; only checkSumAdjustment at +8 matters here.</summary>
    private static byte[] Head() => new byte[54];

    private static string[] DirectoryTags(byte[] font)
    {
        int n = (font[4] << 8) | font[5];
        var tags = new string[n];
        for (int i = 0; i < n; i++)
            tags[i] = Encoding.ASCII.GetString(font, 12 + i * 16, 4);
        return tags;
    }

    private static (uint checksum, uint offset, uint length) Entry(byte[] font, string tag)
    {
        int n = (font[4] << 8) | font[5];
        for (int i = 0; i < n; i++)
        {
            int at = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, at, 4) != tag) continue;
            return (ReadU32(font, at + 4), ReadU32(font, at + 8), ReadU32(font, at + 12));
        }
        throw new InvalidOperationException($"table \"{tag}\" is not in the directory");
    }

    private static uint WholeFileSum(byte[] font)
    {
        int total = (font.Length + 3) & ~3;
        var padded = new byte[total];
        Array.Copy(font, padded, font.Length);

        uint sum = 0;
        for (int i = 0; i + 3 < total; i += 4)
            sum = unchecked(sum + ReadU32(padded, i));
        return sum;
    }

    private static uint ReadU32(byte[] data, long offset) => unchecked((uint)(
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]));
}
