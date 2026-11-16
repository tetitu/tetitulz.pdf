// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="SfntTablePruner"/> removes the sfnt tables a PDF reader never consults, without
/// touching a single outline.
/// </summary>
/// <remarks>
/// <para>Every CFF font this library embeds is embedded whole — <c>PdfFontSubsetter</c> is
/// TrueType-only — so one modern Hebrew, Arabic or CJK OpenType face ships every byte of itself in
/// every document. Layout tables are usually the largest thing in such a font after the outlines,
/// and dropping them cannot produce a wrong glyph because no charstring is rewritten.</para>
/// <para><b>The fixtures are built here rather than loaded</b>, per the project's self-contained
/// rule, and each one <b>carries the table the assertion is about</b> — a font without a
/// <c>GSUB</c> proves nothing about dropping <c>GSUB</c>.</para>
/// </remarks>
public class SfntTablePrunerTests
{
    [Fact]
    public void Prune_DropsLayoutTables_BecauseAPdfReaderMustNotShape()
    {
        // ISO 32000-1 §9.4: character codes -> glyphs, advances from /Widths. The producer already
        // shaped; a reader that applied GSUB again would double-shape.
        byte[] font = Font(("CFF ", 4000), ("GSUB", 9000), ("GPOS", 7000),
                           ("head", 54), ("cmap", 100));

        byte[] pruned = SfntTablePruner.Prune(font, out string reason, out int saved);

        Assert.NotNull(reason == null ? pruned : null);
        Assert.Null(reason);
        Assert.DoesNotContain("GSUB", Tags(pruned!));
        Assert.DoesNotContain("GPOS", Tags(pruned!));
        // ...and the outlines are still there, untouched.
        Assert.Contains("CFF ", Tags(pruned!));
        Assert.True(saved > 15000, $"expected to save the ~16 000 bytes of layout tables, saved {saved}");
    }

    [Fact]
    public void Prune_DropsDsig_WhichIsACorrectnessRuleRatherThanASizeOne()
    {
        // A DSIG is a signature over the font file. Any rewrite invalidates it, so carrying a stale
        // one is worse than carrying none.
        byte[] font = Font(("glyf", 500), ("loca", 40), ("DSIG", 200), ("head", 54));

        byte[] pruned = SfntTablePruner.Prune(font, out string reason, out _);

        Assert.Null(reason);
        Assert.DoesNotContain("DSIG", Tags(pruned!));
    }

    [Fact]
    public void Prune_KeepsEveryTableAnSfntNeedsToBeValid()
    {
        byte[] font = Font(("CFF ", 900), ("GSUB", 500), ("head", 54), ("hhea", 36),
                           ("hmtx", 20), ("maxp", 6), ("OS/2", 96), ("post", 32),
                           ("name", 60), ("cmap", 80), ("vhea", 36), ("vmtx", 20));

        byte[] pruned = SfntTablePruner.Prune(font, out string reason, out _);

        Assert.Null(reason);
        string[] tags = Tags(pruned!);
        foreach (string required in new[]
                 { "CFF ", "head", "hhea", "hmtx", "maxp", "OS/2", "post", "name", "cmap" })
        {
            Assert.Contains(required, tags);
        }
        // Vertical metrics survive: CJK documents use them, and they are not layout tables.
        Assert.Contains("vhea", tags);
        Assert.Contains("vmtx", tags);
    }

    [Fact]
    public void Prune_PreservesTheSfntVersion_SoAnOpenTypeCffFontStaysOtto()
    {
        // Rewriting an OTTO as 0x00010000 would tell every reader to look for a glyf table that is
        // not there — the exact failure the two hardcoding assemblers could produce.
        byte[] otto = Font(SfntBuilder.VersionOpenTypeCff, ("CFF ", 400), ("GSUB", 300), ("head", 54));
        byte[] ttf = Font(SfntBuilder.VersionTrueType, ("glyf", 400), ("loca", 40), ("GSUB", 300), ("head", 54));

        Assert.Equal(SfntBuilder.VersionOpenTypeCff, ReadU32(SfntTablePruner.Prune(otto, out _, out _)!, 0));
        Assert.Equal(SfntBuilder.VersionTrueType, ReadU32(SfntTablePruner.Prune(ttf, out _, out _)!, 0));
    }

    [Fact]
    public void Prune_AVariableFont_IsRefusedWithAReason()
    {
        // fvar's variation data spans an interdependent family of tables. Guessing which are safe
        // to drop would produce a font that renders at the wrong weight.
        byte[] font = Font(("CFF ", 400), ("fvar", 100), ("GSUB", 300), ("head", 54));

        Assert.Null(SfntTablePruner.Prune(font, out string reason, out _));
        Assert.Contains("variable", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prune_AFontWithNothingToDrop_ReportsThatRatherThanReturningTheInput()
    {
        // Null-with-a-reason lets a caller tell "pruned to the same size" from "not pruned" without
        // comparing byte arrays. Handing back the original would read as a successful prune.
        byte[] font = Font(("CFF ", 400), ("head", 54), ("cmap", 80));

        Assert.Null(SfntTablePruner.Prune(font, out string reason, out int saved));
        Assert.Contains("none of the tables", reason, StringComparison.Ordinal);
        Assert.Equal(0, saved);
    }

    [Fact]
    public void Prune_ATrueTypeCollection_IsRefused()
    {
        var ttc = new byte[64];
        ttc[0] = (byte)'t'; ttc[1] = (byte)'t'; ttc[2] = (byte)'c'; ttc[3] = (byte)'f';

        Assert.Null(SfntTablePruner.Prune(ttc, out string reason, out _));
        Assert.Contains("Collection", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prune_ATablePointingOutsideTheFont_IsRefusedRatherThanRead()
    {
        // Hostile or truncated input: the directory claims a table the file does not contain.
        byte[] font = Font(("CFF ", 400), ("GSUB", 300), ("head", 54));
        // Point CFF's length at far beyond the end.
        int cffEntry = FindEntry(font, "CFF ");
        font[cffEntry + 12] = 0x7F;

        Assert.Null(SfntTablePruner.Prune(font, out string reason, out _));
        Assert.Contains("outside the font", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Prune_TheResultIsAWellFormedSfnt()
    {
        // The whole-file checksum is self-checking, so this catches a layout error the tag
        // assertions above cannot see.
        byte[] pruned = SfntTablePruner.Prune(
            Font(("CFF ", 900), ("GSUB", 800), ("head", 54), ("cmap", 30)), out _, out _);

        Assert.Equal(0xB1B0AFBAu, WholeFileSum(pruned!));
    }

    /// <summary>Each droppable tag on its own is dropped, and the outline beside it kept.</summary>
    [Theory]
    [InlineData("GSUB")] [InlineData("GPOS")] [InlineData("GDEF")] [InlineData("BASE")]
    [InlineData("JSTF")] [InlineData("MATH")] [InlineData("DSIG")] [InlineData("kern")]
    [InlineData("hdmx")] [InlineData("VDMX")] [InlineData("LTSH")] [InlineData("gasp")]
    [InlineData("PCLT")] [InlineData("meta")]
    public void Prune_DropsEachDroppableTag(string tag)
    {
        byte[] pruned = SfntTablePruner.Prune(Font(("CFF ", 400), (tag, 300), ("head", 54)), out string reason, out _);
        Assert.Null(reason);
        Assert.DoesNotContain(tag, Tags(pruned!));
        Assert.Contains("CFF ", Tags(pruned!));
    }

    /// <summary>Each variation marker on its own refuses the font, naming the table.</summary>
    [Theory]
    [InlineData("fvar")] [InlineData("gvar")] [InlineData("CFF2")] [InlineData("HVAR")]
    [InlineData("VVAR")] [InlineData("MVAR")] [InlineData("avar")]
    public void Prune_EachVariationMarker_RefusesTheFont(string tag)
    {
        Assert.Null(SfntTablePruner.Prune(Font(("glyf", 400), (tag, 100), ("GSUB", 300), ("head", 54)), out string reason, out _));
        Assert.Contains($"\"{tag}\"", reason, StringComparison.Ordinal);
    }

    /// <summary>An unknown sfnt version is refused; Apple's <c>true</c> is a TrueType font and is pruned.</summary>
    [Fact]
    public void Prune_AnUnknownVersion_IsRefused_ButAppleTrueTypeIsNot()
    {
        Assert.Null(SfntTablePruner.Prune(Font(0x12345678u, ("glyf", 400), ("GSUB", 300), ("head", 54)), out string reason, out _));
        Assert.Equal("unrecognised sfnt version 0x12345678", reason);

        byte[] apple = SfntTablePruner.Prune(Font(0x74727565u, ("glyf", 400), ("GSUB", 300), ("head", 54)), out string none, out _)!;
        Assert.Null(none);
        Assert.Equal(0x74727565u, ReadU32(apple, 0));
    }

    /// <summary>Eleven bytes are shorter than the header; twelve reach the version check.</summary>
    [Fact]
    public void Prune_AFontShorterThanItsHeader_IsRefusedAsSuch()
    {
        Assert.Null(SfntTablePruner.Prune(new byte[11], out string shortReason, out _));
        Assert.Contains("shorter than an sfnt header", shortReason, StringComparison.Ordinal);
        Assert.Null(SfntTablePruner.Prune(new byte[12], out string twelve, out _));
        Assert.Contains("unrecognised sfnt version", twelve, StringComparison.Ordinal);
    }

    [Fact]
    public void Prune_AFontOfOnlyDroppableTables_IsRefused()
    {
        Assert.Null(SfntTablePruner.Prune(Font(("GSUB", 300), ("GPOS", 300)), out string reason, out int saved));
        Assert.Equal("every table was dropped, which cannot be a font", reason);
        Assert.Equal(0, saved);
    }

    [Fact]
    public void Prune_ReportsExactlyTheBytesItSaved()
    {
        byte[] font = Font(("CFF ", 400), ("GSUB", 1000), ("head", 54));
        byte[] pruned = SfntTablePruner.Prune(font, out _, out int saved)!;
        Assert.Equal(font.Length - pruned.Length, saved);
        Assert.True(saved >= 1000 + 16, $"saved {saved}");
    }

    /// <summary>
    /// Two kept tables sharing one block of data are written out separately, so dropping a small
    /// table can leave the font larger. That is refused, not reported as a saving.
    /// </summary>
    /// <remarks>At 20 bytes the duplicate exactly pays for kern's 4 bytes and its 16-byte directory
    /// entry, so the result is the same size: the boundary of "no smaller".</remarks>
    [Theory]
    [InlineData(400)]
    [InlineData(20)]
    public void Prune_AResultNoSmallerThanTheInput_IsRefused(int shared)
    {
        // cmap is written empty, then pointed at CFF's bytes, so the input holds them once and the
        // rebuild twice.
        byte[] font = Font(("CFF ", shared), ("cmap", 0), ("kern", 4), ("head", 54));
        int cff = FindEntry(font, "CFF "), cmap = FindEntry(font, "cmap");
        Array.Copy(font, cff + 8, font, cmap + 8, 8);   // cmap's offset and length := CFF's

        Assert.Null(SfntTablePruner.Prune(font, out string reason, out _));
        Assert.Equal("dropping kern did not reduce the font", reason);
    }

    // ------------------------------------------------------------------

    private static byte[] Font(params (string tag, int size)[] tables)
        => Font(SfntBuilder.VersionOpenTypeCff, tables);

    /// <summary>
    /// A synthetic sfnt whose tables carry recognisable filler. Built through
    /// <see cref="SfntBuilder"/> so the fixture is a real container rather than an approximation of
    /// one — if the builder is wrong these tests fail too, which is the correct coupling.
    /// </summary>
    private static byte[] Font(uint version, params (string tag, int size)[] tables)
    {
        var built = new List<(string, byte[])>(tables.Length);
        foreach ((string tag, int size) in tables)
        {
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)(tag[0] + i);
            built.Add((tag, data));
        }
        return SfntBuilder.Build(version, built);
    }

    private static string[] Tags(byte[] font)
    {
        int n = (font[4] << 8) | font[5];
        var tags = new string[n];
        for (int i = 0; i < n; i++) tags[i] = Encoding.ASCII.GetString(font, 12 + i * 16, 4);
        return tags;
    }

    private static int FindEntry(byte[] font, string tag)
    {
        int n = (font[4] << 8) | font[5];
        for (int i = 0; i < n; i++)
        {
            int at = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, at, 4) == tag) return at;
        }
        throw new InvalidOperationException($"\"{tag}\" is not in the directory");
    }

    private static uint WholeFileSum(byte[] font)
    {
        int total = (font.Length + 3) & ~3;
        var padded = new byte[total];
        Array.Copy(font, padded, font.Length);

        uint sum = 0;
        for (int i = 0; i + 3 < total; i += 4) sum = unchecked(sum + ReadU32(padded, i));
        return sum;
    }

    private static uint ReadU32(byte[] data, int offset) => unchecked((uint)(
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]));
}
