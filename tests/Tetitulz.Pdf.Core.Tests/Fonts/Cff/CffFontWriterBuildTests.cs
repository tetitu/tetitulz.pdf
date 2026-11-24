// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// <c>CffFontWriter.Build</c> called directly. Its one caller, the Type 1 converter, always writes
/// widths of 0 and matching glyph lists, so the Private DICT's operand order and the refusals were
/// reachable only from here.
/// </summary>
public class CffFontWriterBuildTests
{
    private static CffFontWriter.Font TwoGlyphs()
    {
        var font = new CffFontWriter.Font { DefaultWidthX = 500, NominalWidthX = 250 };
        font.GlyphNames.Add(".notdef"); font.CharStrings.Add(new byte[] { 14 });
        font.GlyphNames.Add("A");       font.CharStrings.Add(new byte[] { 14 });
        return font;
    }

    /// <summary>
    /// The Private DICT's widths survive the round trip, which fixes the operand order: Private is
    /// size then offset, and swapped the parser reads a DICT of header bytes with no widths.
    /// </summary>
    [Fact]
    public void ThePrivateDictsWidthsAreReadBack()
    {
        CffFont parsed = CffParser.Parse(CffFontWriter.Build(TwoGlyphs()));

        Assert.NotNull(parsed);
        Assert.Equal(500f, parsed.DefaultWidthX);
        Assert.Equal(250f, parsed.NominalWidthX);
    }

    /// <summary>
    /// The Private operands as written: size, then offset (TN #5176 Table 10). Read from the bytes,
    /// because the parser hides a swap here: the swapped pair names a span from byte 6 to the end
    /// of the file, which ends with the real Private DICT, so its widths read back the same.
    /// </summary>
    [Fact]
    public void ThePrivateOperandsAreSizeThenOffset()
    {
        byte[] cff = CffFontWriter.Build(TwoGlyphs());

        // The Top DICT ends "29 size(4) 29 offset(4) 18"; the Private DICT is the file's tail.
        int op = Array.LastIndexOf(cff, (byte)18, cff.Length - 7);
        while (op >= 10 && !(cff[op - 10] == 29 && cff[op - 5] == 29)) op = Array.LastIndexOf(cff, (byte)18, op - 1);
        Assert.True(op >= 10, "no fixed-width Private operator was found");

        int size = BigEndian(cff, op - 9), offset = BigEndian(cff, op - 4);
        Assert.Equal(cff.Length - offset, size);
        Assert.True(size < offset, $"size {size}, offset {offset}");
    }

    /// <summary>
    /// An INDEX count is a Card16. Written as two low bytes, 65 536 charstrings would produce a
    /// count of 0 and an INDEX that describes none of its offsets; so it is refused, naming the
    /// cap. The two-glyph font above is the control that a legal count still builds.
    /// </summary>
    [Fact]
    public void MoreThan65535Glyphs_IsRefused()
    {
        var font = new CffFontWriter.Font { DefaultWidthX = 500, NominalWidthX = 250 };
        for (int i = 0; i <= 0xFFFF; i++)
        {
            font.GlyphNames.Add(i == 0 ? ".notdef" : "g" + i);
            font.CharStrings.Add(new byte[] { 14 });
        }
        var ex = Assert.Throws<InvalidOperationException>(() => CffFontWriter.Build(font));
        Assert.Contains("65535", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A custom glyph name two glyphs share is one string: the font repeating a name is smaller by
    /// at least that name than the font giving the second glyph a name of its own, and both name
    /// their glyphs as written.
    /// </summary>
    [Fact]
    public void ACustomNameTwoGlyphsShare_IsWrittenOnce()
    {
        static CffFontWriter.Font Named(params string[] names)
        {
            var font = new CffFontWriter.Font { DefaultWidthX = 500, NominalWidthX = 250 };
            foreach (string n in names) { font.GlyphNames.Add(n); font.CharStrings.Add(new byte[] { 14 }); }
            return font;
        }

        byte[] shared = CffFontWriter.Build(Named(".notdef", "alphaone", "betatwo", "alphaone"));
        byte[] distinct = CffFontWriter.Build(Named(".notdef", "alphaone", "betatwo", "gammathr"));

        Assert.True(distinct.Length - shared.Length >= "gammathr".Length,
            $"shared {shared.Length} bytes, distinct {distinct.Length}");
        var parsed = CffParser.Parse(shared);
        Assert.Equal(2, parsed.GlyphNameToGid["betatwo"]);
        Assert.True(parsed.GlyphNameToGid.ContainsKey("alphaone"));
    }

    private static int BigEndian(byte[] d, int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

    /// <summary>
    /// Characterization: an INDEX whose data fits in 255 bytes uses one-byte offsets. The Name
    /// INDEX follows the four-byte header, so its offSize is byte 6.
    /// </summary>
    [Fact]
    public void ASmallIndexUsesOneByteOffsets()
    {
        byte[] cff = CffFontWriter.Build(TwoGlyphs());

        Assert.Equal(1, cff[6]);
    }

    /// <summary>A glyph without a charstring, or a font without glyphs, is refused; the control builds.</summary>
    [Theory]
    [InlineData("mismatch", true)]
    [InlineData("empty", true)]
    [InlineData("valid", false)]
    public void AMalformedFontIsRefused(string shape, bool refused)
    {
        CffFontWriter.Font font = TwoGlyphs();
        if (shape == "mismatch") font.CharStrings.RemoveAt(1);
        if (shape == "empty") { font.GlyphNames.Clear(); font.CharStrings.Clear(); }

        if (refused) Assert.Throws<ArgumentException>(() => CffFontWriter.Build(font));
        else Assert.Equal(2, CffParser.Parse(CffFontWriter.Build(font)).Charstrings.Count);
    }
}
