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
/// Wave 17 R2.2: a format-12 cmap group ending at <c>0xFFFFFFFF</c>. The loop
/// <c>for (cp = start; cp &lt;= end; cp++)</c> wrapped <c>cp</c> to 0 after the last code point and
/// mapped the low code points — U+0000 upward — to the group's glyphs until the entry cap stopped
/// it, so an ASCII letter the font never mapped resolved to a glyph. The group now ends at its end.
/// </summary>
public sealed class CmapFormat12GroupEndTests
{
    private const uint GroupStart = 0xFFFFFFF0;

    [Fact]
    public void AGroupEndingAtTheLastCodePoint_DoesNotWrapOntoTheLowCodePoints()
    {
        var program = TrueTypeFontProgram.Parse(FontWithOneFormat12Group(GroupStart, 0xFFFFFFFF, startGid: 1));
        Assert.NotNull(program);

        // The group's own code points map (the parser keeps a code point as an int, so the top of
        // the range arrives negative), in order.
        Assert.True(program.TryGetGlyph(unchecked((int)GroupStart), out ushort first));
        Assert.Equal(1, first);
        Assert.True(program.TryGetGlyph(unchecked((int)(GroupStart + 5)), out ushort sixth));
        Assert.Equal(6, sixth);

        // Nothing below the group is mapped — U+0041 is not glyph 0x41 + 1 of a wrap.
        Assert.False(program.TryGetGlyph(0x41, out _));
        Assert.False(program.TryGetGlyph(0, out _));
    }

    /// <summary>The control: an ordinary group far below the top maps exactly its own code points.</summary>
    [Fact]
    public void AnOrdinaryGroup_MapsExactlyItsCodePoints()
    {
        var program = TrueTypeFontProgram.Parse(FontWithOneFormat12Group(0x10000, 0x10003, startGid: 1));
        Assert.True(program.TryGetGlyph(0x10000, out ushort a) && a == 1);
        Assert.True(program.TryGetGlyph(0x10003, out ushort d) && d == 4);
        Assert.False(program.TryGetGlyph(0x10004, out _));
        Assert.False(program.TryGetGlyph(0xFFFF, out _));
    }

    /// <summary>A font whose only cmap subtable is a format 12 with one group.</summary>
    private static byte[] FontWithOneFormat12Group(uint startChar, uint endChar, uint startGid)
    {
        var cmap = new MemoryStream();
        void U16(uint v) { cmap.WriteByte((byte)(v >> 8)); cmap.WriteByte((byte)v); }
        void U32(uint v) { U16(v >> 16); U16(v & 0xFFFF); }

        U16(0); U16(1);                 // version, one encoding record
        U16(3); U16(10); U32(12);       // platform 3, encoding 10 (Unicode full), offset of the subtable
        U16(12); U16(0);                // format 12, reserved
        U32(16 + 12);                   // length: header + one group
        U32(0);                         // language
        U32(1);                         // nGroups
        U32(startChar); U32(endChar); U32(startGid);

        // Glyphs enough for the group (the generator makes one glyph per character plus .notdef);
        // the cmap it generates is replaced by the one above.
        return MinimalTrueType.BuildWithTables("ABCDEFGHIJKLMNOP",
            new Dictionary<string, byte[]>(System.StringComparer.Ordinal) { { "cmap", cmap.ToArray() } });
    }
}
