// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The one sfnt table-directory reader the three re-emitters share.
/// </summary>
/// <remarks>
/// <para>Re-emitters — <c>SfntTablePruner</c>, <c>Cff.CffSubsetter</c> and the Maintenance plugin's
/// <c>TrueTypeGlyphPruner</c> — write back out whatever they read, so what they cannot represent
/// they must refuse. The metric readers are deliberately <b>not</b> callers: a short <c>hhea</c>
/// should give a caller approximate ascent, not a refusal.</para>
///
/// <para>Every refusal here was already in one of the three and missing from at least one other.
/// The pair of tests below assert the consequence rather than the helper — a mangled tag has to
/// come back as a refusal from the operation the caller actually invokes.</para>
/// </remarks>
public class SfntTableDirectoryTests
{
    [Fact]
    public void EveryRecordIsReadInDirectoryOrder()
    {
        byte[] font = MinimalTrueType.Build("Hello");

        Assert.True(SfntTableDirectory.TryRead(font, out var tables, out string reason));
        Assert.Null(reason);

        var tags = new List<string>();
        foreach (SfntTableDirectory.TableRecord t in tables) tags.Add(t.Tag);

        // The tables the builder writes, and nothing invented.
        Assert.Contains("head", tags);
        Assert.Contains("cmap", tags);
        Assert.Contains("glyf", tags);
        Assert.Contains("maxp", tags);

        // Directory order, which §5.2 makes ascending by tag.
        for (int i = 1; i < tags.Count; i++)
            Assert.True(string.CompareOrdinal(tags[i - 1], tags[i]) < 0,
                $"\"{tags[i - 1]}\" precedes \"{tags[i]}\"");
    }

    /// <summary>A record's payload is the table's own bytes, not an offset the caller must trust.</summary>
    [Fact]
    public void PayloadIsTheTableBytes()
    {
        byte[] font = MinimalTrueType.Build("Hello");
        Assert.True(SfntTableDirectory.TryRead(font, out var tables, out _));

        foreach (SfntTableDirectory.TableRecord t in tables)
        {
            if (t.Tag != "head") continue;

            byte[] head = SfntTableDirectory.Payload(font, t);
            Assert.Equal(t.Length, head.Length);
            // head.magicNumber, §5.2.2 — proves the bytes came from the right place.
            Assert.Equal(new byte[] { 0x5F, 0x0F, 0x3C, 0xF5 },
                new[] { head[12], head[13], head[14], head[15] });
            return;
        }

        Assert.Fail("the fixture has no head table");
    }

    /// <summary>
    /// A tag byte outside OpenType's 0x20–0x7E is refused, because the pruner writes the tag back
    /// out from the decoded string.
    /// </summary>
    /// <remarks>
    /// <c>Encoding.ASCII</c> substitutes <c>'?'</c> for such a byte, and <see cref="SfntBuilder"/>
    /// takes each tag from that string — so before this reader existed, a table tagged
    /// <c>0x80 'P' 'O' 'S'</c> came out of the pruner named <c>?POS</c>, sorted ahead of
    /// <c>OS/2</c> rather than after <c>GSUB</c>, and the prune reported success with no reason.
    /// The assertion is on the <b>operation</b>: the helper refusing is not the point, the pruner
    /// no longer producing a renamed table is.
    /// </remarks>
    [Fact]
    public void PrunerRefusesAdversarialTagRatherThanRenamingTheTable()
    {
        byte[] clean = MinimalTrueType.BuildWithDroppableTables("Hello", 512);
        byte[] mangled = MinimalTrueType.WithTableTagBytes(
            clean, "GPOS", new byte[] { 0x80, (byte)'P', (byte)'O', (byte)'S' });

        // Control: the same font, unmangled, prunes — so the refusal below is about the tag.
        byte[] pruned = SfntTablePruner.Prune(clean, out string cleanReason, out int saved);
        Assert.NotNull(pruned);
        Assert.Null(cleanReason);
        Assert.True(saved > 0);
        Assert.DoesNotContain("?", TagsOf(pruned));

        Assert.Null(SfntTablePruner.Prune(mangled, out string reason, out _));
        Assert.Contains("printable ASCII", reason);
    }

    /// <summary>
    /// Records that each lie inside the font but all span it: every record passed its own bounds
    /// check, and a re-emitter then copied the font once per record — 512 records, 512 copies of a
    /// 20 MB font. The directory is refused when the records declare more than twice the font;
    /// disjoint tables total less than it, so the clean control reads and prunes.
    /// </summary>
    [Fact]
    public void OverlappingTablesThatEachSpanTheFont_AreRefusedBeforeAnyCopy()
    {
        byte[] clean = MinimalTrueType.BuildWithDroppableTables("Hello", 512);
        Assert.True(SfntTableDirectory.TryRead(clean, out var tables, out _));
        Assert.True(tables.Count >= 3, $"{tables.Count} tables");

        byte[] overlapping = (byte[])clean.Clone();
        int count = (overlapping[4] << 8) | overlapping[5];
        for (int i = 0; i < count; i++)
        {
            int at = 12 + 16 * i;
            for (int b = 0; b < 4; b++) overlapping[at + 8 + b] = 0;                              // offset 0
            for (int b = 0; b < 4; b++) overlapping[at + 12 + b] = (byte)(clean.Length >> (24 - 8 * b)); // the whole font
        }

        Assert.False(SfntTableDirectory.TryRead(overlapping, out _, out string reason));
        Assert.Contains("overlap", reason);

        Assert.Null(SfntTablePruner.Prune(overlapping, out string pruneReason, out _));
        Assert.Contains("overlap", pruneReason);
    }

    /// <summary>
    /// Two records with one tag are refused rather than resolved. Keeping the first drops a table
    /// the input had; keeping both writes a directory a binary-searching reader may resolve either
    /// way. Both are a different font returned as a success.
    /// </summary>
    [Fact]
    public void DuplicateTagIsRefused()
    {
        byte[] clean = MinimalTrueType.BuildWithDroppableTables("Hello", 512);
        byte[] duplicated = MinimalTrueType.WithTableTagBytes(
            clean, "GPOS", Encoding.ASCII.GetBytes("GSUB"));

        Assert.False(SfntTableDirectory.TryRead(duplicated, out _, out string reason));
        Assert.Contains("twice", reason);

        Assert.Null(SfntTablePruner.Prune(duplicated, out string prunerReason, out _));
        Assert.Contains("twice", prunerReason);
    }

    /// <summary>A collection is refused by signature, before its header is read as a directory.</summary>
    [Fact]
    public void CollectionIsRefusedBySignature()
    {
        byte[] ttc = MinimalTrueType.BuildCollection(MinimalTrueType.Build("Hello"));

        Assert.True(SfntTableDirectory.IsCollection(ttc));
        Assert.False(SfntTableDirectory.TryRead(ttc, out _, out string reason));
        Assert.Contains("ttcf", reason);

        Assert.False(SfntTableDirectory.IsCollection(MinimalTrueType.Build("Hello")));
    }

    /// <summary>A table claiming to start or end past the file is refused, naming the table.</summary>
    [Fact]
    public void TableOutsideTheFontIsRefused()
    {
        byte[] font = MinimalTrueType.Build("Hello");
        var broken = (byte[])font.Clone();

        int numTables = (broken[4] << 8) | broken[5];
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16;
            if (Encoding.ASCII.GetString(broken, at, 4) != "cmap") continue;
            broken[at + 12] = 0x7F;   // length = 0x7Fxxxxxx, far past the end
            break;
        }

        Assert.False(SfntTableDirectory.TryRead(broken, out _, out string reason));
        Assert.Contains("cmap", reason);
    }

    /// <summary>
    /// A directory declaring more records than the file can hold is refused before any of them is
    /// read — the loop is bounded by the file, never by the declared count.
    /// </summary>
    [Theory]
    [InlineData(0xFF, 0xFF)]   // 65535 tables in a ~700-byte file
    [InlineData(0x00, 0x00)]   // no tables at all
    public void AdversarialTableCountIsRefused(byte high, byte low)
    {
        byte[] font = MinimalTrueType.Build("Hello");
        var broken = (byte[])font.Clone();
        broken[4] = high;
        broken[5] = low;

        Assert.False(SfntTableDirectory.TryRead(broken, out _, out string reason));
        Assert.Contains("directory", reason);
    }

    [Fact]
    public void EmptyOrTruncatedInputIsRefused()
    {
        Assert.False(SfntTableDirectory.TryRead(null, out _, out string nullReason));
        Assert.Contains("shorter", nullReason);

        Assert.False(SfntTableDirectory.TryRead(new byte[11], out _, out string shortReason));
        Assert.Contains("shorter", shortReason);
    }

    private static string TagsOf(byte[] font)
    {
        int n = (font[4] << 8) | font[5];
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(Encoding.ASCII.GetString(font, 12 + i * 16, 4)).Append('|');
        return sb.ToString();
    }
}
