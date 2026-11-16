// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// The three charset formats of CFF §13 are three spellings of <b>one</b> glyph-to-SID assignment,
/// and this font writes the same assignment in each.
/// </summary>
/// <remarks>
/// <para><b>Why these exist.</b> GAPS.md §7 recorded that <c>CffParser</c> walks the charset three
/// times — SID→GID, GID→SID and CID→GID — and that merging them was blocked on exactly this: the
/// suite had <b>only format-0</b> charsets, so the range arithmetic of formats 1 and 2 was reached
/// by nothing. A merge whose survivor no test reaches is a deletion, so the fixture comes first.</para>
///
/// <para><b>Format 1 and format 2 differ in one thing: the width of <c>nLeft</c>.</b> Format 1
/// spends one byte on it, format 2 spends two, and both mean "this many glyphs <i>after</i> the
/// first" — the off-by-one that <c>i &lt;= nLeft</c> encodes. A range of three glyphs is therefore
/// <c>nLeft = 2</c>, which is the value a copy that read it as a count would get wrong by one.</para>
///
/// <para>The assignment is deliberately <b>contiguous</b> (SIDs 1..3 for GIDs 1..3) because that is
/// the only thing formats 1 and 2 can express in a single range, and format 0 spells the identical
/// mapping out entry by entry. Asserting the three agree is asserting the range arithmetic against
/// an explicit list.</para>
/// </remarks>
public sealed class CffCharsetFormatTests
{
    /// <summary>SIDs 1, 2 and 3 of the Adobe standard strings.</summary>
    private static readonly string[] ExpectedNames = { "space", "exclam", "quotedbl" };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryCharsetFormat_AssignsTheSameSidToTheSameGlyph(int charsetFormat)
    {
        CffFont font = CffParser.Parse(BuildCff(charsetFormat));

        Assert.NotNull(font);
        Assert.NotNull(font.GlyphNameToGid);

        for (int i = 0; i < ExpectedNames.Length; i++)
        {
            Assert.True(font.GlyphNameToGid.TryGetValue(ExpectedNames[i], out int gid),
                $"format {charsetFormat} lost the glyph named {ExpectedNames[i]}");
            Assert.Equal(i + 1, gid);
        }
    }

    /// <summary>
    /// The <b>other</b> walk over the same bytes: the char-code map, which goes through SID→GID
    /// rather than GID→SID. The two are separate loops over one charset and must not disagree.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryCharsetFormat_ReachesTheSameGlyphThroughTheEncoding(int charsetFormat)
    {
        CffFont font = CffParser.Parse(BuildCff(charsetFormat));

        Assert.NotNull(font.CharCodeToGid);
        // The format-0 encoding below maps 'A' → GID 1, 'B' → GID 2, 'C' → GID 3.
        Assert.Equal(1, font.CharCodeToGid['A']);
        Assert.Equal(2, font.CharCodeToGid['B']);
        Assert.Equal(3, font.CharCodeToGid['C']);
    }

    /// <summary>
    /// A range that runs past the last glyph must stop at the glyph count, not walk off the end of
    /// the map — the bound the three walks each apply in their own way.
    /// </summary>
    /// <remarks>
    /// <para>The declared range says nine glyphs follow the first; the font has three. A walk that
    /// honoured <c>nLeft</c> over the glyph count would assign SIDs to glyph ids that do not
    /// exist.</para>
    /// <para><c>.notdef</c> is GID 0 and is named too — the map is seeded with it before the
    /// charset is read at all — so the count here is four, not three. Asserting three was this
    /// test's own mistake and not the parser's.</para>
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ARangeLongerThanTheFont_StopsAtTheLastGlyph(int charsetFormat)
    {
        CffFont font = CffParser.Parse(BuildCff(charsetFormat, nLeftOverride: 9));

        Assert.NotNull(font);
        // .notdef plus the three real glyphs, and nothing named for a fourth.
        Assert.Equal(4, font.GlyphNameToGid.Count);
        foreach (var kv in font.GlyphNameToGid)
            Assert.InRange(kv.Value, 0, 3);
        Assert.Equal(0, font.GlyphNameToGid[".notdef"]);
    }

    /// <summary>
    /// <b>Two</b> ranges, so the walk has to step from one entry to the next — which is the only
    /// way the per-format entry stride can be observed at all.
    /// </summary>
    /// <remarks>
    /// <para>A single-range charset cannot see a wrong stride: the first entry already fills the
    /// font, the loop exits, and the pointer is never advanced again. Fixing the stride at three
    /// bytes — correct for format 1, one short for format 2 — survived the whole suite until this
    /// test existed, which is a finding about the fixture and not about the walk.</para>
    /// <para>The second range starts at SID 10 rather than continuing from the first, so a misread
    /// entry lands on a different standard string instead of coincidentally on the right one.</para>
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TwoRanges_AreBothRead_WhicheverFormatSpellsThem(int charsetFormat)
    {
        CffFont font = CffParser.Parse(BuildCff(charsetFormat, twoRanges: true));

        Assert.NotNull(font);
        // Range A: GID 1 → SID 1. Range B: GIDs 2 and 3 → SIDs 10 and 11.
        Assert.Equal(1, font.GlyphNameToGid["space"]);
        Assert.Equal(2, font.GlyphNameToGid["parenright"]);
        Assert.Equal(3, font.GlyphNameToGid["asterisk"]);
    }

    /// <summary>
    /// A charset whose format byte is none of 0, 1 or 2 cannot be walked, and the two SID builders
    /// leave their maps as they stand rather than inventing an assignment.
    /// </summary>
    /// <remarks>
    /// The CID→GID builder is the one caller that turns this into an identity map, because for a
    /// CID-keyed font "CID == GID" is a better answer than an empty one. That difference is the
    /// caller's and not the walk's, which is why the walk reports <c>false</c> instead of choosing.
    /// </remarks>
    [Fact]
    public void AnUnknownCharsetFormat_AssignsNoGlyphNamesBeyondNotdef()
    {
        CffFont font = CffParser.Parse(BuildCff(charsetFormat: 3));

        Assert.NotNull(font);
        Assert.Equal(".notdef", Assert.Single(font.GlyphNameToGid).Key);
    }

    /// <summary>
    /// The refusal's <b>one reader</b>: a CID-keyed font whose charset format is unknown falls back
    /// to the identity, because "CID == GID" serves such a font better than an empty map.
    /// </summary>
    /// <remarks>
    /// <para><b>Only this caller can see the difference, which is why it needs its own fixture.</b>
    /// The two SID builders ignore whether the walk succeeded — an unwalkable charset and a charset
    /// that assigned nothing leave their maps identical — so making the walk <i>claim</i> success on
    /// an unknown format is invisible to every non-CID test. Here it is the difference between a
    /// three-entry identity and a map holding only <c>.notdef</c>.</para>
    /// <para>The well-formed control sits beside it, where the charset is honoured and the mapping
    /// is deliberately <b>not</b> the identity — otherwise the two answers would coincide.</para>
    /// </remarks>
    [Fact]
    public void ACidKeyedFontWithAnUnknownCharsetFormat_FallsBackToTheIdentity()
    {
        CffFont font = CffParser.Parse(
            MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20 }, charsetFormat: 3));

        Assert.NotNull(font);
        Assert.Equal(3, font.CharCodeToGid.Count);
        Assert.Equal(0, font.CharCodeToGid[0]);
        Assert.Equal(1, font.CharCodeToGid[1]);
        Assert.Equal(2, font.CharCodeToGid[2]);
    }

    /// <summary>The control: a well-formed charset is honoured, and it is not the identity.</summary>
    [Fact]
    public void ACidKeyedFontWithAWellFormedCharset_UsesItRatherThanTheIdentity()
    {
        CffFont font = CffParser.Parse(MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20 }));

        Assert.NotNull(font);
        Assert.Equal(1, font.CharCodeToGid[10]);
        Assert.Equal(2, font.CharCodeToGid[20]);
        Assert.False(font.CharCodeToGid.ContainsKey(1),
            "CID 1 exists only under an identity map, so the charset was not honoured");
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// A four-glyph CFF (<c>.notdef</c> plus three boxes) whose charset assigns SIDs 1, 2 and 3 to
    /// GIDs 1, 2 and 3, written in <paramref name="charsetFormat"/>.
    /// </summary>
    /// <param name="nLeftOverride">
    /// For formats 1 and 2, the <c>nLeft</c> to declare instead of the correct 2 — used to reach the
    /// bound that stops a range at the glyph count.
    /// </param>
    private static byte[] BuildCff(int charsetFormat, int nLeftOverride = -1, bool twoRanges = false)
    {
        byte[] notdef = { 14 };
        byte[] glyph =
        {
            248, 136,           // width 500
            139, 139, 21,       // rmoveto(0, 0)
            139, 248, 36,
            248, 36, 139,
            139, 252, 36,
            5,                  // rlineto
            14                  // endchar
        };

        byte[] charset = BuildCharset(charsetFormat, nLeftOverride, twoRanges);

        // Format 0 encoding: three codes, 'A'→GID 1, 'B'→GID 2, 'C'→GID 3. The code list is in GID
        // order, which is what format 0 means.
        byte[] encoding = { 0, 3, (byte)'A', (byte)'B', (byte)'C' };

        byte[] csIndex = Index(new[] { notdef, glyph, glyph, glyph });
        byte[] nameIndex = Index(new[] { Encoding.ASCII.GetBytes("CharsetTest") });
        byte[] stringIndex = Index(Array.Empty<byte[]>());
        byte[] globalSubrIndex = Index(Array.Empty<byte[]>());

        // The Top DICT stores absolute offsets, so its size must be settled before they can be
        // computed. Every offset here is encoded in the fixed five-byte form, so the placeholder
        // pass and the real pass are the same length by construction rather than by luck.
        byte[] placeholder = Index(new[] { TopDict(0, 0, 0) });
        int encodingOffset = 4 + nameIndex.Length + placeholder.Length
                           + stringIndex.Length + globalSubrIndex.Length;
        int charsetOffset = encodingOffset + encoding.Length;
        int csIndexOffset = charsetOffset + charset.Length;

        byte[] topDict = Index(new[] { TopDict(encodingOffset, charsetOffset, csIndexOffset) });
        Assert.Equal(placeholder.Length, topDict.Length);

        var cff = new List<byte> { 1, 0, 4, 1 };
        cff.AddRange(nameIndex);
        cff.AddRange(topDict);
        cff.AddRange(stringIndex);
        cff.AddRange(globalSubrIndex);
        cff.AddRange(encoding);
        cff.AddRange(charset);
        cff.AddRange(csIndex);
        return cff.ToArray();
    }

    /// <summary>The same GID→SID assignment in each of §13's three formats.</summary>
    private static byte[] BuildCharset(int format, int nLeftOverride, bool twoRanges = false)
    {
        int nLeft = nLeftOverride >= 0 ? nLeftOverride : 2;   // 2 == "two glyphs after the first"

        if (twoRanges)
        {
            // Range A: SID 1 for GID 1 (nLeft 0). Range B: SIDs 10-11 for GIDs 2-3 (nLeft 1).
            // Starting range B at 10 rather than continuing from 2 means a misread entry lands on
            // a different standard string instead of coincidentally on the right one.
            switch (format)
            {
                case 1: return new byte[] { 1, 0, 1, 0, 0, 10, 1 };
                case 2: return new byte[] { 2, 0, 1, 0, 0, 0, 10, 0, 1 };
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
        }

        switch (format)
        {
            case 0:
                // One 16-bit SID per glyph after .notdef, in GID order.
                return new byte[] { 0, 0, 1, 0, 2, 0, 3 };

            case 1:
                // firstSid (16-bit), nLeft (8-bit).
                return new byte[] { 1, 0, 1, (byte)nLeft };

            case 2:
                // firstSid (16-bit), nLeft (16-bit).
                return new byte[] { 2, 0, 1, (byte)(nLeft >> 8), (byte)nLeft };

            default:
                // A format byte §13 does not define — the walk must refuse it rather than guess.
                // The trailing bytes are plausible entry data, so a walk that guessed would find
                // something to assign.
                return new byte[] { (byte)format, 0, 1, 0, 2, 0, 3 };
        }
    }

    private static byte[] TopDict(int encodingOff, int charsetOff, int csOff)
    {
        var d = new List<byte>();
        Int32Operand(d, charsetOff); d.Add(15);    // charset
        Int32Operand(d, encodingOff); d.Add(16);   // Encoding
        Int32Operand(d, csOff); d.Add(17);         // CharStrings
        return d.ToArray();
    }

    /// <summary>The always-five-byte integer form, so the two Top DICT passes cannot differ in size.</summary>
    private static void Int32Operand(List<byte> d, int v)
    {
        d.Add(29);
        d.Add((byte)((uint)v >> 24)); d.Add((byte)((uint)v >> 16));
        d.Add((byte)((uint)v >> 8)); d.Add((byte)v);
    }

    private static byte[] Index(byte[][] items)
    {
        if (items.Length == 0) return new byte[] { 0, 0 };

        int total = 0;
        foreach (byte[] item in items) total += item.Length;

        byte offSize = total + 1 <= 0xFF ? (byte)1 : total + 1 <= 0xFFFF ? (byte)2 : (byte)3;
        var idx = new List<byte> { (byte)(items.Length >> 8), (byte)(items.Length & 0xFF), offSize };

        void WriteOffset(int off)
        {
            for (int i = offSize - 1; i >= 0; i--) idx.Add((byte)((off >> (i * 8)) & 0xFF));
        }

        int running = 1;
        WriteOffset(running);
        foreach (byte[] item in items) { running += item.Length; WriteOffset(running); }
        foreach (byte[] item in items) idx.AddRange(item);
        return idx.ToArray();
    }
}
