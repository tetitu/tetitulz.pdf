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
/// The two resolvers that turn what a <i>document</i> knows — Unicode code points, or CIDs — into
/// the glyph ids <see cref="CffSubsetter.BlankUnusedGlyphs"/> takes.
///
/// <para><b>Both exist because the obvious shortcut is wrong in a way nothing observes.</b>
/// A bare CFF has no cmap, so <c>TrueTypeFontProgram.TryGetGlyph</c> answers nothing for it and the
/// remediator declined every such font. And a CID is not a glyph index: a CID-keyed CFF's charset
/// maps GID → CID, so the inverse is what a subsetter holding CIDs needs — the two coincide often
/// enough that assuming identity passes on most fonts and blanks the wrong glyphs on the rest.</para>
/// </summary>
public class CffGlyphMappingTests
{
    // ================================================================
    //  Unicode → GID
    // ================================================================

    /// <summary>An sfnt has a cmap and it is the authority, because it is what a reader consults.</summary>
    [Fact]
    public void AnSfntResolvesCodePointsThroughItsCmap()
    {
        byte[] otf = MinimalOpenTypeCff.Build("ABCDE");

        Assert.True(CffSubsetter.TryMapCodePointsToGids(otf, new[] { (int)'C' }, out List<int> gids, out _));

        // The fixture assigns GIDs in sorted character order, so 'C' is the third glyph after .notdef.
        Assert.Contains(3, gids);
        Assert.Contains(0, gids);   // .notdef always
    }

    /// <summary>
    /// The case the remediator declined for a whole round: a bare CFF carries no cmap, but its
    /// glyphs are <b>named</b>, and Adobe glyph names have defined Unicode values.
    /// </summary>
    [Fact]
    public void ABareCffResolvesCodePointsThroughItsOwnGlyphNames()
    {
        var glyphs = new List<MinimalOpenTypeCff.Glyph>
        {
            new() { Name = ".notdef", Charstring = new byte[] { 14 } },
            new() { Name = "A",       Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 60, 1) },
            new() { Name = "B",       Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 60, 2) },
            new() { Name = "eacute",  Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 60, 3) },
        };
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(glyphs);

        // Deliberately NOT an sfnt: this must resolve with no cmap in existence anywhere.
        Assert.Equal(1, cff[0]);

        Assert.True(CffSubsetter.TryMapCodePointsToGids(
            cff, new[] { (int)'B', 0x00E9 /* é */ }, out List<int> gids, out string reason));
        Assert.Null(reason);

        Assert.Contains(2, gids);   // "B"
        Assert.Contains(3, gids);   // "eacute" → U+00E9
        Assert.DoesNotContain(1, gids);
    }

    /// <summary>
    /// Several glyph names can denote one code point, and which one a reader lands on depends on
    /// the font dictionary's encoding — invisible from a font program. Keeping both costs a
    /// charstring; keeping one risks blanking the one that gets drawn.
    /// </summary>
    [Fact]
    public void WhenTwoGlyphNamesShareACodePointBothAreKept()
    {
        var glyphs = new List<MinimalOpenTypeCff.Glyph>
        {
            new() { Name = ".notdef", Charstring = new byte[] { 14 } },
            new() { Name = "A",       Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 60, 1) },
            new() { Name = "uni0041", Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 60, 2) },
            new() { Name = "B",       Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 60, 3) },
        };
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(glyphs);

        Assert.True(CffSubsetter.TryMapCodePointsToGids(cff, new[] { (int)'A' }, out List<int> gids, out _));

        Assert.Contains(1, gids);
        Assert.Contains(2, gids);
        Assert.DoesNotContain(3, gids);
    }

    /// <summary>
    /// One unresolvable code point fails the whole call. A partial glyph set is exactly what must
    /// not drive blanking — the caller embeds whole instead.
    /// </summary>
    [Fact]
    public void AnUnresolvableCodePointFailsTheWholeMappingRatherThanReturningAPartialSet()
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(new List<MinimalOpenTypeCff.Glyph>
        {
            new() { Name = ".notdef", Charstring = new byte[] { 14 } },
            new() { Name = "A",       Charstring = MinimalOpenTypeCff.BoxGlyph(500, 400, 60, 1) },
        });

        Assert.False(CffSubsetter.TryMapCodePointsToGids(
            cff, new[] { (int)'A', (int)'Z' }, out List<int> gids, out string reason));

        Assert.Null(gids);
        Assert.Contains("005A", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CID-keyed CFF numbers its glyphs rather than naming them, so Unicode cannot reach them from
    /// here — and the reason says which fact about the font made it impossible, not just "no".
    /// </summary>
    [Fact]
    public void ACidKeyedCffRefusesUnicodeMappingAndSaysWhy()
    {
        byte[] cid = CidKeyedCff(new[] { 10, 20, 30 });

        Assert.False(CffSubsetter.TryMapCodePointsToGids(cid, new[] { (int)'A' }, out _, out string reason));
        Assert.Contains("CID-keyed", reason, StringComparison.Ordinal);
    }

    // ================================================================
    //  CID → GID
    // ================================================================

    /// <summary>
    /// The load-bearing one. A CID-keyed font's charset decides which glyph a CID reaches, and this
    /// fixture's charset is deliberately <b>not</b> the identity — under an identity assumption every
    /// assertion below would land on the wrong glyph.
    /// </summary>
    [Fact]
    public void CidsResolveThroughTheCharsetRatherThanBeingTreatedAsGlyphIndices()
    {
        // GID 1 ← CID 10, GID 2 ← CID 20, GID 3 ← CID 30.
        byte[] cid = CidKeyedCff(new[] { 10, 20, 30 });

        Assert.True(CffSubsetter.TryMapCidsToGids(cid, new[] { 20 }, out List<int> gids, out string reason));
        Assert.Null(reason);

        Assert.Contains(2, gids);           // the charset says CID 20 is GID 2
        Assert.DoesNotContain(20, gids);    // ...and NOT GID 20, which is the identity answer
        Assert.Contains(0, gids);
    }

    /// <summary>
    /// A non-CID-keyed CFF under a <c>CIDFontType0</c> is legal (§9.7.4.2) and has no charset
    /// indirection — there the CID <i>is</i> the glyph index, and refusing it would decline a whole
    /// legitimate font shape.
    /// </summary>
    [Fact]
    public void ANonCidKeyedCffTreatsTheCidAsTheGlyphIndex()
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(5));

        Assert.True(CffSubsetter.TryMapCidsToGids(cff, new[] { 2, 4 }, out List<int> gids, out _));

        Assert.Equal(new[] { 0, 2, 4 }, gids.OrderBy(g => g).ToArray());
    }

    /// <summary>
    /// The counterpart for the <b>non</b>-CID-keyed shape: there the CID is the glyph index, so a
    /// CID outside the font's glyph range fails the call rather than being passed through as one.
    /// </summary>
    /// <remarks>
    /// <para><b>The bounds check had no coverage.</b> A 2026-08-29 liveness sweep neutralised
    /// <c>cid &lt; 0 || cid &gt;= glyphCount</c> and nothing reddened in Core, Base or the
    /// Maintenance plugin — the three assemblies that reach it. The sibling test above exercises the
    /// same branch with CIDs 2 and 4 against a five-glyph font, both comfortably in range, so it
    /// could not tell the guard from its absence.</para>
    ///
    /// <para>Refusing is the right answer rather than clamping or skipping: the caller is the
    /// optimizer deciding which charstrings to blank, and an out-of-range index means its CID set
    /// and this font do not belong to each other. Blanking on that evidence is how a subset loses
    /// glyphs the document draws.</para>
    /// </remarks>
    [Fact]
    public void ACidBeyondANonCidKeyedFontsGlyphRangeFailsTheMapping()
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(5));

        Assert.False(CffSubsetter.TryMapCidsToGids(cff, new[] { 2, 9999 }, out List<int> gids, out string reason));
        Assert.Null(gids);
        Assert.Contains("9999", reason, StringComparison.Ordinal);
    }

    /// <summary>A CID the charset maps to nothing fails the call rather than resolving to .notdef.</summary>
    [Fact]
    public void ACidTheCharsetDoesNotMapFailsTheMapping()
    {
        byte[] cid = CidKeyedCff(new[] { 10, 20, 30 });

        Assert.False(CffSubsetter.TryMapCidsToGids(cid, new[] { 20, 99 }, out List<int> gids, out string reason));
        Assert.Null(gids);
        Assert.Contains("99", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end: resolving CIDs and blanking must keep the glyph the CID reaches and empty the
    /// others. This is the pairing the optimizer performs, and the one an identity assumption breaks.
    /// </summary>
    [Fact]
    public void ResolvingThenBlankingKeepsTheGlyphTheCidActuallyReaches()
    {
        byte[] cid = CidKeyedCff(new[] { 10, 20, 30 });
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(cid);

        Assert.True(CffSubsetter.TryMapCidsToGids(cid, new[] { 20 }, out List<int> gids, out _));
        var result = CffSubsetter.BlankUnusedGlyphs(cid, gids);

        Assert.Null(result.Reason);
        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(result.Font);

        Assert.True(before[2].SequenceEqual(after[2]), "GID 2, which CID 20 reaches, was blanked");
        Assert.True(MinimalOpenTypeCff.IsBlanked(after[1]), "GID 1 is unused and was not blanked");
        Assert.True(MinimalOpenTypeCff.IsBlanked(after[3]), "GID 3 is unused and was not blanked");
    }

    /// <summary>A CID-keyed CFF whose charset assigns <paramref name="cidsByGid"/>[i] to GID i+1 — the shared
    /// <see cref="MinimalOpenTypeCff.BuildCidKeyed"/>, whose required CIDs keep the charset non-identity.</summary>
    private static byte[] CidKeyedCff(int[] cidsByGid) => MinimalOpenTypeCff.BuildCidKeyed(cidsByGid);
}
