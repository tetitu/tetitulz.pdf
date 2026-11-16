// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="CompositeGlyph"/>: the component records of a composite glyph, read the one way the
/// two subsetters and the Maintenance pruner now share, and bounded by the glyph's own end.
/// </summary>
public class CompositeGlyphTests
{
    private const int ArgsAreWords = 0x0001, HaveScale = 0x0008, More = 0x0020, HaveXAndYScale = 0x0040, HaveTwoByTwo = 0x0080;

    [Fact]
    public void ASimpleGlyphHasNoComponents()
    {
        var simple = new List<byte> { 0x00, 0x01 };   // numberOfContours = 1
        simple.AddRange(new byte[8 + 20]);

        Assert.Empty(CompositeGlyph.ComponentIndexOffsets(simple.ToArray(), 0, simple.Count));
    }

    /// <summary>
    /// An empty glyph — <c>loca</c> gives it no bytes — at the very end of the data is no composite,
    /// and reading its first byte would be past the array: the header-length test is what stops it.
    /// A composite ending at the same place is the control.
    /// </summary>
    [Fact]
    public void AnEmptyGlyphAtTheEndOfTheDataHasNoComponents()
    {
        byte[] data = Composite(Record(0, 6, 2));

        Assert.Empty(CompositeGlyph.ComponentIndexOffsets(data, data.Length, data.Length));
        Assert.Equal(new[] { 6 }, Indices(data, 0, data.Length));
    }

    /// <summary>
    /// Each record's size follows its flags: word or byte arguments, then no transform, a scale, an
    /// x-and-y scale or a two-by-two. Four records, one of each, name glyphs 7, 9, 11 and 13; a
    /// wrong skip for any one of them reads the next index from the wrong place.
    /// </summary>
    [Fact]
    public void EveryRecordShapeIsSteppedOver()
    {
        byte[] glyph = Composite(
            Record(ArgsAreWords | HaveScale | More, 7, 4 + 2),
            Record(HaveXAndYScale | More, 9, 2 + 4),
            Record(ArgsAreWords | HaveTwoByTwo | More, 11, 4 + 8),
            Record(0, 13, 2));

        Assert.Equal(new[] { 7, 9, 11, 13 }, Indices(glyph, 0, glyph.Length));
    }

    /// <summary>
    /// A malformed last record keeps <c>MORE_COMPONENTS</c> set. The walk stops at the glyph's end
    /// instead of taking the next glyph's bytes for records; read without the bound, those bytes name
    /// glyph 42, which is what the two subsetters used to pull into a subset.
    /// </summary>
    [Fact]
    public void TheWalkStopsAtTheGlyphsEnd()
    {
        byte[] glyph = Composite(Record(More, 5, 2));
        byte[] next = Record(0, 42, 2);   // the following glyph's bytes, as the old walk read them
        byte[] data = glyph.Concat(next).ToArray();

        Assert.Equal(new[] { 5 }, Indices(data, 0, glyph.Length));
        Assert.Equal(new[] { 5, 42 }, Indices(data, 0, data.Length));   // the control: unbounded
    }

    /// <summary>
    /// A record without <c>MORE_COMPONENTS</c> is the last, though the glyph goes on: a composite
    /// with instructions keeps them after its records, and they are not records.
    /// </summary>
    [Fact]
    public void TheLastRecordEndsTheWalkBeforeTheInstructions()
    {
        byte[] glyph = Composite(Record(More, 3, 2), Record(0x0100, 4, 2),   // WE_HAVE_INSTRUCTIONS
            new byte[] { 0x00, 0x04, 0x00, 0x2A, 0x00, 0x2A });              // numInstr = 4, then bytes

        Assert.Equal(new[] { 3, 4 }, Indices(glyph, 0, glyph.Length));
    }

    /// <summary>A record cut short by the glyph's end ends the walk; the records before it are kept.</summary>
    [Fact]
    public void ATruncatedRecordKeepsTheOnesBeforeIt()
    {
        byte[] glyph = Composite(Record(More, 3, 2), Record(0, 4, 2));

        Assert.Equal(new[] { 3 }, Indices(glyph, 0, glyph.Length - 3));
        Assert.Equal(new[] { 3, 4 }, Indices(glyph, 0, glyph.Length));
    }

    [Fact]
    public void AnIndexIsRenumberedInPlace()
    {
        byte[] glyph = Composite(Record(More, 3, 2), Record(0, 400, 2));

        foreach (int at in CompositeGlyph.ComponentIndexOffsets(glyph, 0, glyph.Length).ToList())
            CompositeGlyph.WriteIndex(glyph, at, CompositeGlyph.ReadIndex(glyph, at) + 1000);

        Assert.Equal(new[] { 1003, 1400 }, Indices(glyph, 0, glyph.Length));
    }

    /// <summary>
    /// Offsets (<c>ARGS_ARE_XY_VALUES</c>) are signed — a byte 0xFF is −1, a word 0xFFFE −2 — and point
    /// numbers unsigned — the same byte is 255, the same word 65 534.
    /// </summary>
    [Fact]
    public void Components_ArgumentsAreSignedOffsetsOrUnsignedPoints()
    {
        const int XyValues = 0x0002;
        byte[] glyph = Composite(
            Bytes(More | XyValues, 1, 0xFF, 0x05),
            Bytes(More | ArgsAreWords | XyValues, 2, 0xFF, 0xFE, 0x00, 0x07),
            Bytes(More, 3, 0xFF, 0xF0),
            Bytes(ArgsAreWords, 4, 0xFF, 0xFE, 0xFF, 0xF0));

        var c = CompositeGlyph.Components(glyph, 0, glyph.Length).ToList();

        Assert.Equal(new[] { 1, 2, 3, 4 }, c.Select(x => x.GlyphIndex));
        Assert.Equal(new[] { (-1, 5), (-2, 7), (255, 240), (65534, 65520) }, c.Select(x => (x.Argument1, x.Argument2)));
        Assert.All(c, x => Assert.True(x.Complete));
    }

    /// <summary>
    /// The transform's F2Dot14 words: one scale for both axes, an x and a y scale, or the 2 × 2 in the
    /// record's order (x scale, then the y-from-x term, the x-from-y term, the y scale); none is identity.
    /// </summary>
    [Fact]
    public void Components_TransformWordsAreReadInTheRecordsOrder()
    {
        byte[] glyph = Composite(
            Bytes(More | HaveScale, 1, 0, 0, 0x20, 0x00),                                     // 0.5
            Bytes(More | HaveXAndYScale, 2, 0, 0, 0x40, 0x00, 0xE0, 0x00),                    // 1, −0.5
            Bytes(More | HaveTwoByTwo, 3, 0, 0, 0x00, 0x00, 0x40, 0x00, 0xC0, 0x00, 0x10, 0x00), // 0, 1, −1, 0.25
            Bytes(0, 4, 0, 0));

        var c = CompositeGlyph.Components(glyph, 0, glyph.Length).ToList();

        Assert.Equal(new[] { 0.5, 0, 0, 0.5 }, new[] { c[0].A, c[0].B, c[0].C, c[0].D });
        Assert.Equal(new[] { 1.0, 0, 0, -0.5 }, new[] { c[1].A, c[1].B, c[1].C, c[1].D });
        Assert.Equal(new[] { 0.0, 1, -1, 0.25 }, new[] { c[2].A, c[2].B, c[2].C, c[2].D });
        Assert.Equal(new[] { 1.0, 0, 0, 1 }, new[] { c[3].A, c[3].B, c[3].C, c[3].D });
    }

    /// <summary>
    /// A record whose glyph index fits but whose arguments do not is still yielded — the subsetters
    /// renumber it — and says it is not complete; the complete record before it says it is.
    /// </summary>
    [Fact]
    public void Components_ARecordCutAfterItsIndex_IsYieldedIncomplete()
    {
        byte[] glyph = Composite(Bytes(More, 3, 1, 2), Bytes(ArgsAreWords, 4, 0, 1, 0, 2));

        var c = CompositeGlyph.Components(glyph, 0, glyph.Length - 2).ToList();

        Assert.Equal(new[] { 3, 4 }, c.Select(x => x.GlyphIndex));
        Assert.Equal(new[] { true, false }, c.Select(x => x.Complete));
    }

    private static byte[] Bytes(int flags, int glyphIndex, params byte[] tail)
    {
        var r = new byte[4 + tail.Length];
        r[0] = (byte)(flags >> 8); r[1] = (byte)flags;
        r[2] = (byte)(glyphIndex >> 8); r[3] = (byte)glyphIndex;
        tail.CopyTo(r, 4);
        return r;
    }

    private static int[] Indices(byte[] data, int start, int end)
        => CompositeGlyph.ComponentIndexOffsets(data, start, end).Select(at => CompositeGlyph.ReadIndex(data, at)).ToArray();

    private static byte[] Composite(params byte[][] records)
    {
        var bytes = new List<byte> { 0xFF, 0xFF };   // numberOfContours = -1
        bytes.AddRange(new byte[8]);                 // bounding box
        foreach (var r in records) bytes.AddRange(r);
        return bytes.ToArray();
    }

    /// <summary>A component record: flags, glyph index, then <paramref name="tail"/> bytes of arguments and transform.</summary>
    private static byte[] Record(int flags, int glyphIndex, int tail)
    {
        var r = new byte[4 + tail];
        r[0] = (byte)(flags >> 8); r[1] = (byte)flags;
        r[2] = (byte)(glyphIndex >> 8); r[3] = (byte)glyphIndex;
        return r;
    }
}
