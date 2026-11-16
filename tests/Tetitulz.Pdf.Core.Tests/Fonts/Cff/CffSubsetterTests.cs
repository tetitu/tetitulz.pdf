// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// <see cref="CffSubsetter"/> blanks unused charstrings <b>in place</b>: the file length never
/// changes, so every assertion here is per-glyph or post-Flate. "The subset is smaller" is not
/// available and would be false.
///
/// <para><b>What each test would have to look like to be worthless.</b> <c>Assert.NotNull(result.Font)</c>
/// passes with the blanking loop deleted, because the input is already a valid font. So the
/// assertions below read the charstrings back out and say which GIDs are a bare <c>endchar</c> and
/// which are byte-identical to what went in — the only difference blanking makes.</para>
/// </summary>
public class CffSubsetterTests
{
    // ================================================================
    //  It actually blanks, and only what it was told to
    // ================================================================

    /// <summary>
    /// The central claim: unused glyphs become <c>endchar</c> + zeros, used ones are untouched, and
    /// GID 0 survives even when nobody asked for it.
    /// </summary>
    [Fact]
    public void BlanksExactlyTheUnusedGlyphsAndLeavesTheUsedOnesByteIdentical()
    {
        var glyphs = MinimalOpenTypeCff.BoxGlyphs(10);
        byte[] font = MinimalOpenTypeCff.BuildBareCff(glyphs);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(font);

        var keep = new[] { 3, 7 };
        var result = CffSubsetter.BlankUnusedGlyphs(font, keep);

        Assert.Null(result.Reason);
        Assert.NotNull(result.Font);
        Assert.Equal(11, result.GlyphCount);          // .notdef + 10
        Assert.Equal(3, result.GlyphsKept);           // .notdef + the two asked for
        Assert.Equal(8, result.GlyphsBlanked);

        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.Equal(before.Count, after.Count);

        var kept = new HashSet<int> { 0, 3, 7 };
        for (int gid = 0; gid < after.Count; gid++)
        {
            if (kept.Contains(gid))
                Assert.True(before[gid].SequenceEqual(after[gid]),
                    $"kept glyph {gid} was modified — a subsetter must not touch a glyph the document draws");
            else
                Assert.True(MinimalOpenTypeCff.IsBlanked(after[gid]),
                    $"unused glyph {gid} was not blanked (first byte 0x{after[gid][0]:X2})");
        }
    }

    /// <summary>
    /// The blanked bytes must be a measurable quantity, not a rounding error — and the file length
    /// must NOT change, which is the property the whole in-place design buys.
    /// </summary>
    [Fact]
    public void TheFileLengthIsUnchangedAndTheBlankedByteCountIsWhatTheGlyphsWeighed()
    {
        var glyphs = MinimalOpenTypeCff.BoxGlyphs(20, padTo: 120);
        byte[] font = MinimalOpenTypeCff.BuildBareCff(glyphs);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(font);

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 1 });

        Assert.Null(result.Reason);
        Assert.Equal(font.Length, result.Font.Length);

        int expected = 0;
        for (int gid = 2; gid < before.Count; gid++) expected += before[gid].Length;
        Assert.Equal(expected, result.CharstringBytesBlanked);
        Assert.True(expected > 1000,
            $"the fixture only had {expected} blankable bytes, which is too few to tell a real " +
            "saving from noise — the padded box glyphs must not have shrunk");
    }

    /// <summary>
    /// The point of the exercise. <c>/FontFile3</c> is always Flate-encoded, so the size a document
    /// pays is the compressed one, and a run of zeros costs almost nothing.
    /// </summary>
    [Fact]
    public void TheCompressedProgramIsSubstantiallySmallerAfterBlanking()
    {
        byte[] font = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(200, padTo: 120));
        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 1, 2, 3 });
        Assert.Null(result.Reason);

        int wholeZ = FlateEncodeFilter.Encode(font).Length;
        int subsetZ = FlateEncodeFilter.Encode(result.Font).Length;

        Assert.True(subsetZ * 2 < wholeZ,
            $"blanking 197 of 201 glyphs only took the Flate-encoded program from {wholeZ} to " +
            $"{subsetZ} bytes; the zero run is supposed to compress away");
    }

    /// <summary>An OpenType/CFF container survives the round trip and still reads back as an sfnt.</summary>
    [Fact]
    public void AnOttoContainerIsRebuiltAndStillParsesWithItsCmapIntact()
    {
        byte[] otf = MinimalOpenTypeCff.Build("ABCDEFGH", padTo: 120);
        var program = TrueTypeFontProgram.Parse(otf);
        Assert.True(program!.TryGetGlyph('C', out ushort cGid));

        var result = CffSubsetter.BlankUnusedGlyphs(otf, new[] { (int)cGid });
        Assert.Null(result.Reason);

        var reparsed = TrueTypeFontProgram.Parse(result.Font);
        Assert.NotNull(reparsed);
        Assert.True(reparsed!.HasCff);
        Assert.True(reparsed.TryGetGlyph('C', out ushort afterGid));
        Assert.Equal(cGid, afterGid);

        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(otf);
        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.True(before[cGid].SequenceEqual(after[cGid]), "the kept glyph's charstring changed");
        Assert.True(MinimalOpenTypeCff.IsBlanked(after[cGid == 1 ? 2 : 1]), "no other glyph was blanked");
    }

    // ================================================================
    //  seac — the landmine
    // ================================================================

    /// <summary>
    /// An accented composite draws two <i>other</i> glyphs by Adobe StandardEncoding code. Nothing
    /// in a document's cmap lookup names those components, so a subsetter that keeps only what it
    /// was asked for blanks them — and "Aacute" then renders as a floating accent in a font that
    /// still loads perfectly. Both components must survive.
    /// </summary>
    [Fact]
    public void KeepingASeacCompositeKeepsTheBaseAndAccentGlyphsItDraws()
    {
        // GID 1 "A" (StandardEncoding code 65), GID 2 "acute" (code 194), GID 3 the composite.
        var glyphs = new List<MinimalOpenTypeCff.Glyph>
        {
            new() { Name = ".notdef", Charstring = new byte[] { 14 } },
            new() { Name = "A",       Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 80), Code = 65 },
            new() { Name = "acute",   Charstring = MinimalOpenTypeCff.BoxGlyph(300, 100, 80), Code = 194 },
            new() { Name = "Aacute",  Charstring = MinimalOpenTypeCff.SeacGlyph(65, 194),     Code = 193 },
            new() { Name = "unused",  Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 80), Code = 66 },
        };
        byte[] font = MinimalOpenTypeCff.BuildBareCff(glyphs);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(font);

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 3 });

        Assert.Null(result.Reason);
        Assert.Equal(2, result.SeacComponentsKept);
        Assert.Equal(4, result.GlyphsKept);            // .notdef, A, acute, Aacute
        Assert.Equal(1, result.GlyphsBlanked);         // only "unused"

        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.True(before[1].SequenceEqual(after[1]), "the seac BASE glyph 'A' was blanked");
        Assert.True(before[2].SequenceEqual(after[2]), "the seac ACCENT glyph 'acute' was blanked");
        Assert.True(MinimalOpenTypeCff.IsBlanked(after[4]), "the genuinely unused glyph survived");
    }

    /// <summary>
    /// A real font stores its accented letters' <c>endchar</c> inside a shared subroutine, so a
    /// scanner that reads only the top-level charstring sees a <c>callgsubr</c> and nothing else.
    /// Following the call is what makes the seac guard work on fonts that actually ship.
    /// </summary>
    [Fact]
    public void ASeacReachedThroughAGlobalSubroutineIsStillFollowed()
    {
        var subrs = new List<byte[]> { MinimalOpenTypeCff.SeacGlyph(65, 194) };
        var glyphs = new List<MinimalOpenTypeCff.Glyph>
        {
            new() { Name = ".notdef", Charstring = new byte[] { 14 } },
            new() { Name = "A",       Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 80), Code = 65 },
            new() { Name = "acute",   Charstring = MinimalOpenTypeCff.BoxGlyph(300, 100, 80), Code = 194 },
            new() { Name = "Aacute",  Charstring = MinimalOpenTypeCff.CallGlobalSubrGlyph(0, subrs.Count), Code = 193 },
            // A genuinely unused glyph, so "nothing was blanked" cannot be mistaken for success:
            // keeping GID 3 pulls in .notdef, A and acute, which is the whole font without it.
            new() { Name = "spare",   Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 80, 9), Code = 66 },
        };
        byte[] font = MinimalOpenTypeCff.BuildBareCff(glyphs, subrs);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(font);

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 3 });

        Assert.Null(result.Reason);
        Assert.Equal(2, result.SeacComponentsKept);
        Assert.Equal(1, result.GlyphsBlanked);
        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.True(before[1].SequenceEqual(after[1]), "the base glyph behind a subroutine was blanked");
        Assert.True(before[2].SequenceEqual(after[2]), "the accent glyph behind a subroutine was blanked");
        Assert.True(MinimalOpenTypeCff.IsBlanked(after[4]), "the genuinely unused glyph survived");
    }

    /// <summary>
    /// A composite whose component code resolves to no glyph cannot be shown safe, so the whole
    /// subset is abandoned and the caller embeds whole. Fail-closed: a large font beats a broken one.
    /// </summary>
    [Fact]
    public void ASeacComponentThatResolvesToNoGlyph_AbandonsTheSubset()
    {
        var glyphs = new List<MinimalOpenTypeCff.Glyph>
        {
            new() { Name = ".notdef", Charstring = new byte[] { 14 } },
            // Code 253 is not in Adobe StandardEncoding and this font encodes nothing there.
            new() { Name = "orphan",  Charstring = MinimalOpenTypeCff.SeacGlyph(253, 254), Code = 65 },
            new() { Name = "spare",   Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 80), Code = 66 },
        };
        byte[] font = MinimalOpenTypeCff.BuildBareCff(glyphs);

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 1 });

        Assert.Null(result.Font);
        Assert.Contains("component", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    //  Refusals
    // ================================================================

    /// <summary>CFF2 is a different format with a variation store; it is refused, never rewritten.</summary>
    [Fact]
    public void ACff2ProgramIsRefused()
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(3));
        cff[0] = 2;   // claim CFF2

        var result = CffSubsetter.BlankUnusedGlyphs(cff, new[] { 1 });

        Assert.Null(result.Font);
        Assert.Contains("CFF2", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>An sfnt carrying a <c>CFF2</c> table is refused for the same reason.</summary>
    [Fact]
    public void AnSfntWithACff2TableIsRefused()
    {
        byte[] otf = MinimalOpenTypeCff.Build("ABC");
        // Rename the "CFF " directory tag to "CFF2" in place: same table, different claim.
        int at = FindTagOffset(otf, "CFF ");
        otf[at + 3] = (byte)'2';

        var result = CffSubsetter.BlankUnusedGlyphs(otf, new[] { 1 });

        Assert.Null(result.Font);
        Assert.Contains("CFF2", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>A glyf-outlined font belongs to the TrueType subsetter, and is told so by name.</summary>
    [Fact]
    public void ATrueTypeFontIsRefusedWithAPointerToTheOtherSubsetter()
    {
        var result = CffSubsetter.BlankUnusedGlyphs(MinimalTrueType.Build("ABC"), new[] { 1 });

        Assert.Null(result.Font);
        Assert.Contains("glyf", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adversarial input: truncation, garbage and a collection header must cost a refusal with a
    /// reason, never an exception out of an optimisation the caller did not have to ask for.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(40)]
    [InlineData(90)]
    public void MalformedTruncatedInput_IsRefusedWithoutThrowing(int keepBytes)
    {
        byte[] truncated = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(6))
                                             .Take(keepBytes).ToArray();

        var result = CffSubsetter.BlankUnusedGlyphs(truncated, new[] { 1, 2 });

        // Truncation may leave the header and the CharStrings INDEX intact, in which case blanking
        // is legitimate; what is banned is a null font with no reason, or a throw.
        if (result.Font == null) Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        else Assert.Null(result.Reason);
    }

    /// <summary>A TrueType Collection carries several fonts; it is refused by name.</summary>
    [Fact]
    public void ATrueTypeCollectionIsRefused()
    {
        byte[] ttc = MinimalTrueType.Build("ABC");
        ttc[0] = (byte)'t'; ttc[1] = (byte)'t'; ttc[2] = (byte)'c'; ttc[3] = (byte)'f';

        var result = CffSubsetter.BlankUnusedGlyphs(ttc, new[] { 1 });

        Assert.Null(result.Font);
        Assert.Contains("Collection", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A font whose every glyph is used must be refused with a reason rather than handed back —
    /// otherwise the caller stamps an "ABCDEF+" subset tag onto a complete font, which claims a
    /// restricted character set ISO 32000-1 §9.6.4 says the file does not have.
    /// </summary>
    [Fact]
    public void AFontWhoseGlyphsAreAllUsed_IsRefusedRatherThanReturnedUnchanged()
    {
        byte[] font = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(4));

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 1, 2, 3, 4 });

        Assert.Null(result.Font);
        Assert.Contains("nothing to blank", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, result.GlyphsBlanked);
    }

    /// <summary>
    /// GID 0 is <c>.notdef</c>: a reader substitutes it for anything it cannot map, so blanking it
    /// turns a missing glyph into an invisible one. It survives even when the caller lists it nowhere.
    /// </summary>
    [Fact]
    public void NotdefIsKeptEvenWhenTheCallerNeverAsksForIt()
    {
        var glyphs = MinimalOpenTypeCff.BoxGlyphs(4);
        glyphs[0].Charstring = MinimalOpenTypeCff.BoxGlyph(500, 200, 40);   // give .notdef real bytes
        byte[] font = MinimalOpenTypeCff.BuildBareCff(glyphs);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(font);

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 2 });

        Assert.Null(result.Reason);
        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.True(before[0].SequenceEqual(after[0]), ".notdef was blanked");
    }

    /// <summary>
    /// A glyph id outside the font is ignored rather than throwing or silently widening the keep
    /// set — the callers derive them from a cmap or a document scan, either of which can be wrong.
    /// </summary>
    [Fact]
    public void OutOfRangeGlyphIdsAreIgnored()
    {
        byte[] font = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(4));

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { -5, 2, 9999 });

        Assert.Null(result.Reason);
        Assert.Equal(2, result.GlyphsKept);       // .notdef and GID 2
        Assert.Equal(3, result.GlyphsBlanked);
    }

    /// <summary>
    /// A blanked charstring must be <c>endchar</c> first, not a run of zeros: byte <c>0x00</c> is a
    /// reserved Type 2 operator, so all-zero is malformed and interpreters differ on what it means.
    /// </summary>
    [Fact]
    public void ABlankedCharstringStartsWithEndcharRatherThanAZeroByte()
    {
        byte[] font = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(4));
        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 1 });

        byte[] blanked = MinimalOpenTypeCff.ReadCharstrings(result.Font)[2];
        Assert.Equal(0x0E, blanked[0]);
        Assert.All(blanked.Skip(1), b => Assert.Equal(0, b));
    }

    /// <summary>A null glyph set is a refusal, not a subset of everything.</summary>
    [Fact]
    public void ANullGlyphSetIsRefused()
    {
        byte[] font = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(3));
        var result = CffSubsetter.BlankUnusedGlyphs(font, null);
        Assert.Null(result.Font);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    private static int FindTagOffset(byte[] sfnt, string tag)
    {
        int numTables = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16;
            if (System.Text.Encoding.ASCII.GetString(sfnt, at, 4) == tag) return at;
        }
        throw new InvalidOperationException($"no \"{tag}\" table");
    }

    /// <summary>
    /// Malformed INDEX: the last charstring's range runs two bytes on, over the Private DICT that
    /// follows it. Blanking that glyph zeroed the Private DICT (its default width 100 became 0),
    /// every kept glyph still matched byte for byte, and the subset was returned as a success. The
    /// read-back now compares everything else the parser reads; the control, with the INDEX
    /// honest, subsets.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACharstringRangeOverThePrivateDict_IsRefused(bool overlapping)
    {
        byte[] font = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(3), privateDict: new byte[] { 239, 20 });
        if (overlapping) ExtendLastCharstring(font, 2);
        Assert.Equal(100f, CffParser.Parse(font).DefaultWidthX);

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 1 });

        if (overlapping)
        {
            Assert.Null(result.Font);
            Assert.Contains("outside the charstrings", result.Reason);
        }
        else
        {
            Assert.Null(result.Reason);
            Assert.Equal(100f, CffParser.Parse(result.Font).DefaultWidthX);
        }
    }

    /// <summary>
    /// A seac component is resolved two ways, and the union is kept: by its StandardEncoding name
    /// through the charset, and by its code through the font's own encoding. Each row makes only one
    /// route resolve. With both routes in the fixture, dropping either one still passed.
    /// </summary>
    [Theory]
    [InlineData("A", "acute", -1, -1)]          // named, not encoded: the name route alone
    [InlineData("base", "mark", 65, 194)]       // encoded, misnamed: the code route alone
    public void ASeacComponentReachedByOneRouteOnly_IsKept(string baseName, string accentName, int baseCode, int accentCode)
    {
        var glyphs = new List<MinimalOpenTypeCff.Glyph>
        {
            new() { Name = ".notdef",  Charstring = new byte[] { 14 } },
            new() { Name = baseName,   Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 80), Code = baseCode },
            new() { Name = accentName, Charstring = MinimalOpenTypeCff.BoxGlyph(300, 100, 80), Code = accentCode },
            new() { Name = "Aacute",   Charstring = MinimalOpenTypeCff.SeacGlyph(65, 194),     Code = 193 },
            new() { Name = "unused",   Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 80, 9), Code = 66 },
        };
        byte[] font = MinimalOpenTypeCff.BuildBareCff(glyphs);

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 3 });

        Assert.Null(result.Reason);
        Assert.Equal(2, result.SeacComponentsKept);
        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.False(MinimalOpenTypeCff.IsBlanked(after[1]), "the seac base was blanked");
        Assert.False(MinimalOpenTypeCff.IsBlanked(after[2]), "the seac accent was blanked");
        Assert.True(MinimalOpenTypeCff.IsBlanked(after[4]), "the genuinely unused glyph survived");
    }

    /// <summary>
    /// Malformed INDEX: the last charstring runs past the end of the data. It is refused for that
    /// reason. Without the check the write at offset -1 threw, and the catch gave a reason that
    /// named no cause.
    /// </summary>
    [Fact]
    public void ACharstringPastTheData_IsRefusedByName()
    {
        byte[] font = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(3));
        Assert.True(CffParser.TryLocateCharStrings(font, out int[] starts, out _, out int at, out _));
        int offSize = font[at + 2];
        for (int b = 0; b < offSize; b++) font[at + 3 + starts.Length * offSize + b] = 0xFF;   // the largest offset

        var result = CffSubsetter.BlankUnusedGlyphs(font, new[] { 1 });

        Assert.Null(result.Font);
        Assert.Equal("charstring 3 lies outside the font data", result.Reason);
    }

    /// <summary>
    /// Malformed INDEX: four-byte offsets that are negative as integers place both charstrings in
    /// the Top DICT, before the INDEX. Blanking there would overwrite the Top DICT; it is refused for
    /// that reason. The control, with honest offsets, subsets.
    /// </summary>
    [Theory]
    [InlineData(-29, -28, null)]
    [InlineData(1, 2, "")]
    public void ACharstringBeforeItsIndex_IsRefusedByName(int first, int second, string expected)
    {
        var cff = new List<byte> { 1, 0, 4, 1, 0, 0 };                   // header, empty Name INDEX
        cff.AddRange(new byte[] { 0, 1, 1, 1, 7, 29, 0, 0, 0, 21, 17 });   // Top DICT: CharStrings at 21
        cff.AddRange(new byte[] { 0, 0, 0, 0 });                           // String and Global Subr INDEXes
        cff.AddRange(new byte[] { 0, 2, 4 });                              // CharStrings: 2 items, offSize 4
        foreach (int off in new[] { first, second, 3 })
            cff.AddRange(new[] { (byte)(off >> 24), (byte)(off >> 16), (byte)(off >> 8), (byte)off });
        cff.AddRange(new byte[] { 14, 14 });

        var result = CffSubsetter.BlankUnusedGlyphs(cff.ToArray(), new[] { 0 });

        if (expected == null)
        {
            Assert.Null(result.Font);
            Assert.Equal("charstring 0 is placed before the CharStrings INDEX it belongs to", result.Reason);
        }
        else
        {
            Assert.Null(result.Reason);
            Assert.Equal(1, result.GlyphsBlanked);
        }
    }

    /// <summary>Adds <paramref name="by"/> to the CharStrings INDEX's last offset.</summary>
    private static void ExtendLastCharstring(byte[] cff, int by)
    {
        Assert.True(CffParser.TryLocateCharStrings(cff, out int[] starts, out _, out int at, out _));
        int offSize = cff[at + 2];
        int last = at + 3 + starts.Length * offSize;
        long value = 0;
        for (int b = 0; b < offSize; b++) value = (value << 8) | cff[last + b];
        value += by;
        for (int b = offSize - 1; b >= 0; b--, value >>= 8) cff[last + b] = (byte)value;
    }
}
