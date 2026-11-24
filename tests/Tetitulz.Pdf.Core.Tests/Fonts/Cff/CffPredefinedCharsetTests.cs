// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// A Top DICT whose <c>charset</c> is 0 names its glyphs by the predefined ISOAdobe charset (CFF
/// §13, Table 22): GID n is SID n for GIDs 1–228, and the charset says nothing past 228.
/// </summary>
/// <remarks>
/// Every other CFF fixture writes a charset of its own, so cutting that arm to GID 1 would fail
/// nothing else. These patch <see cref="MinimalOpenTypeCff.BuildBareCff"/>'s
/// charset offset to 0 in place — the builder writes every Top DICT offset as a five-byte integer,
/// so the font's layout does not move.
/// </remarks>
public sealed class CffPredefinedCharsetTests
{
    [Fact]
    public void TheIsoAdobeCharset_NamesEachGlyphByTheSidOfItsGid()
    {
        CffFont font = CffParser.Parse(WithIsoAdobeCharset(MinimalOpenTypeCff.BoxGlyphs(3)));

        Assert.NotNull(font.GlyphNameToGid);
        Assert.Equal(1, font.GlyphNameToGid["space"]);
        Assert.Equal(2, font.GlyphNameToGid["exclam"]);
        Assert.Equal(3, font.GlyphNameToGid["quotedbl"]);
        // A font of three glyphs is named no further than its glyphs.
        Assert.DoesNotContain("numbersign", font.GlyphNameToGid.Keys);
    }

    [Fact]
    public void TheIsoAdobeCharset_EndsAtGid228()
    {
        CffFont font = CffParser.Parse(WithIsoAdobeCharset(MinimalOpenTypeCff.BoxGlyphs(230, padTo: 0)));

        // SID 228 is "zcaron", the charset's last; GIDs 229 and 230 are glyphs it does not name.
        Assert.Equal(228, font.GlyphNameToGid["zcaron"]);
        Assert.Equal(228, font.GlyphNameToGid.Values.Where(gid => gid > 0).Max());
    }

    /// <summary>
    /// The standard strings as an independent font tool and another reader both give them: SID 227 is
    /// <c>ydieresis</c>, 228 <c>zcaron</c>, 390 <c>Semibold</c>, and there is no <c>Euro</c>. Until
    /// 2026-10-21 the table lacked 227 and read every later SID as its successor.
    /// </summary>
    [Fact]
    public void TheStandardStrings_AreAppendixAsTwoImplementationsGiveThem()
    {
        string[] s = CffParser.StandardStrings;
        Assert.Equal(391, s.Length);
        Assert.Equal("yacute", s[226]);
        Assert.Equal("ydieresis", s[227]);
        Assert.Equal("zcaron", s[228]);
        Assert.Equal("exclamsmall", s[229]);
        Assert.Equal("Hungarumlautsmall", s[230]);
        Assert.Equal("001.000", s[379]);
        Assert.Equal("Semibold", s[390]);
        Assert.DoesNotContain("Euro", s);
    }

    /// <summary>
    /// The writer names a glyph by the same table read the other way: <c>zcaron</c> as SID 228,
    /// <c>Semibold</c> as 390, and a name no standard string holds (<c>Euro</c>) as a custom string
    /// from 391. Read from the charset's bytes, because the parser shares the table and a round trip
    /// through it agrees with any mistake in it.
    /// </summary>
    [Fact]
    public void TheWriter_GivesAStandardNameItsAppendixSid()
    {
        var font = new CffFontWriter.Font();
        foreach (string name in new[] { ".notdef", "zcaron", "Semibold", "Euro" })
        {
            font.GlyphNames.Add(name);
            font.CharStrings.Add(new byte[] { 14 });
        }
        byte[] cff = CffFontWriter.Build(font);

        int charset = CharsetOffset(cff);
        Assert.Equal(0, cff[charset]);   // format 0: one SID per glyph after .notdef
        int Sid(int k) => (cff[charset + 1 + 2 * k] << 8) | cff[charset + 2 + 2 * k];
        Assert.Equal(228, Sid(0));
        Assert.Equal(390, Sid(1));
        Assert.True(Sid(2) >= 391, $"Euro was written as standard SID {Sid(2)}");
    }

    /// <summary>The Top DICT's <c>charset</c> operand: the integer before operator 15.</summary>
    private static int CharsetOffset(byte[] cff)
    {
        for (int i = 4; i + 5 < cff.Length; i++)
            if (cff[i] == 29 && cff[i + 5] == 15)
                return (cff[i + 1] << 24) | (cff[i + 2] << 16) | (cff[i + 3] << 8) | cff[i + 4];
        throw new InvalidOperationException("the writer's Top DICT has no five-byte charset operand");
    }

    /// <summary>The bare CFF with its Top DICT's charset operand (the first entry) set to 0.</summary>
    private static byte[] WithIsoAdobeCharset(IReadOnlyList<MinimalOpenTypeCff.Glyph> glyphs)
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(glyphs);
        // The first five-byte integer (29 and four bytes) followed by operator 15 is the charset.
        for (int i = 4; i + 5 < cff.Length; i++)
        {
            if (cff[i] != 29 || cff[i + 5] != 15) continue;
            cff[i + 1] = cff[i + 2] = cff[i + 3] = cff[i + 4] = 0;
            return cff;
        }
        throw new InvalidOperationException("the builder's Top DICT has no charset operand");
    }
}
