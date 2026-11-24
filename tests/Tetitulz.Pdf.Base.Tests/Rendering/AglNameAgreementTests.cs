// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The two sides of the AGL glyph-name question must agree on <b>which names are AGL numeric
/// names</b>: the reverse index built from a font's glyph names, and the one-at-a-time lookup used
/// for a <c>/Differences</c> entry.
/// </summary>
/// <remarks>
/// <para><b>They are not independent.</b> <c>FontCache.ApplyPdfEncodingOverride</c> builds the
/// reverse index with <c>BuildUnicodeToGidFromNames</c> and resolves a <c>/Differences</c> name with
/// <c>PdfGlyphNames.TryGetUnicode</c>, then joins both answers on a single <c>int</c> key. A name
/// that one calls AGL and the other does not is a lookup that silently misses — the glyph is simply
/// never found, and the page draws <c>.notdef</c>.</para>
///
/// <para>They disagreed on two shapes, in opposite directions: <c>u1F600</c> (astral — the index
/// accepted it, the lookup's <c>char</c> return type could not carry it) and <c>uni00410042</c>
/// (a code-point sequence — the index truncated it to its first four digits, the lookup refused
/// it). The second is the one that produced wrong <i>output</i> rather than a miss.</para>
///
/// <para><b>These assert agreement, not a particular answer</b>, except where the answer itself was
/// wrong. That is deliberate: the defect was the disagreement, so a test naming only one side's
/// expected value would not have caught it.</para>
///
/// <para><b>The agreement theory is a re-divergence guard, not a correctness proof — read it that
/// way.</b> Now that both sides call one rule, mutating that rule moves them together and the
/// comparison still holds; measured, restoring the take-the-first truncation kills exactly one test
/// here, <see cref="AMultiCodePointUniNameIsRefusedRatherThanTruncated"/>. That is the shape
/// CLAUDE.md warns about — a post-merge <c>A(x) == B(x)</c> test is close to a tautology — so the
/// correctness weight sits on the named-value cases, and the theory earns its place only by failing
/// if someone re-introduces a private copy on either side.</para>
/// </remarks>
public class AglNameAgreementTests
{
    /// <summary>
    /// For every name either side might call AGL, the index and the lookup must classify it the
    /// same way — and where both accept, they must produce the same code point.
    /// </summary>
    /// <remarks>
    /// The BMP ceiling is exempted explicitly rather than silently: <c>TryGetUnicode</c> returns a
    /// <c>char</c>, so it cannot carry an astral code point even though the name is a valid one.
    /// That is a return-type constraint, not a disagreement about the name, and the astral theory
    /// below pins the index side of it.
    /// </remarks>
    [Theory]
    [InlineData("uni0041")]        // canonical four-digit form
    [InlineData("uni05D0")]        // the Hebrew case a real document exercised
    [InlineData("uni00e9")]        // lowercase hex
    [InlineData("uni00410042")]    // SEQUENCE - must be refused by both, not truncated
    [InlineData("uni004100420043")]
    [InlineData("uni041")]         // too few digits
    [InlineData("unicode")]        // not hex at all
    [InlineData("u0041")]          // the four-digit 'u' form
    [InlineData("uF0B7")]
    public void TheReverseIndexAndTheSingleLookupAgreeOnWhatIsAnAglName(string glyphName)
    {
        bool indexAccepts = BuildIndex(glyphName).Count == 1;
        bool lookupAccepts = PdfGlyphNames.TryGetUnicode(glyphName, out _);

        Assert.Equal(lookupAccepts, indexAccepts);
    }

    /// <summary>
    /// The one place the two sides legitimately differ, stated explicitly so it cannot be mistaken
    /// for the drift above: a name that is in the <b>standard</b> AGL table and merely happens to
    /// begin with <c>'u'</c>.
    /// </summary>
    /// <remarks>
    /// <para><c>union</c> (U+222A), <c>uacute</c> (U+00FA) and <c>uhorn</c> (U+01B0) all resolve
    /// through the table, and none of them is a numeric name — <c>union</c> would parse as
    /// <c>u</c> + <c>"nion"</c> if the <c>'n'</c> guard were dropped. The reverse index must ignore
    /// all three, because a font naming a glyph <c>uacute</c> is describing its inventory, not
    /// declaring a Unicode mapping.</para>
    ///
    /// <para><b>The first version of this file put these in the theory above and it failed.</b>
    /// That was the test being wrong, not the code: agreement is about which names are
    /// <i>numeric</i> AGL names, and comparing against a lookup that also consults the table asks a
    /// different question. Keeping them here records the distinction instead of deleting the
    /// cases.</para>
    /// </remarks>
    [Theory]
    [InlineData("union", 0x222A)]
    [InlineData("uacute", 0x00FA)]
    [InlineData("uhorn", 0x01B0)]
    public void AStandardNameBeginningWithUResolvesByTableAndIsNotInTheIndex(
        string glyphName, int expected)
    {
        Assert.True(PdfGlyphNames.TryGetUnicode(glyphName, out char viaLookup));
        Assert.Equal(expected, viaLookup);
        Assert.Empty(BuildIndex(glyphName));
    }

    /// <summary>
    /// Where both accept, they must land on the <b>same</b> code point — agreement on the name is
    /// worth nothing if they then disagree on its value.
    /// </summary>
    [Theory]
    [InlineData("uni0041", 0x0041)]
    [InlineData("uni05D0", 0x05D0)]
    [InlineData("uni00e9", 0x00E9)]
    [InlineData("u0041", 0x0041)]
    [InlineData("uF0B7", 0xF0B7)]
    public void WhereBothAcceptTheyProduceTheSameCodePoint(string glyphName, int expected)
    {
        var index = BuildIndex(glyphName);

        Assert.True(PdfGlyphNames.TryGetUnicode(glyphName, out char viaLookup));
        Assert.Equal(expected, viaLookup);
        Assert.True(index.ContainsKey(expected));
    }

    /// <summary>
    /// <b>The defect that produced wrong output.</b> AGL's <c>uniXXXXYYYY</c> names a code-point
    /// <i>sequence</i>. Truncating it to its first four digits mapped U+0041 to a glyph that draws
    /// "AB", so a page asking for "A" rendered "AB".
    /// </summary>
    [Fact]
    public void AMultiCodePointUniNameIsRefusedRatherThanTruncated()
    {
        var index = BuildIndex("uni00410042");

        Assert.Empty(index);
        Assert.False(index.ContainsKey(0x0041),
            "an AB-ligature glyph must never become the answer for a page asking for 'A'");
    }

    /// <summary>
    /// The astral names stay in the index. This is what a naive consolidation would have broken:
    /// folding the reverse index into the <c>out char</c> lookup narrows it to the BMP.
    /// </summary>
    [Theory]
    [InlineData("u1F600", 0x1F600)]
    [InlineData("u01F601", 0x1F601)]
    public void AnAstralAglNameStillReachesTheReverseIndex(string glyphName, int expected)
    {
        var index = BuildIndex(glyphName);

        Assert.True(index.ContainsKey(expected));
        Assert.False(PdfGlyphNames.TryGetUnicode(glyphName, out _),
            "the char-returning lookup cannot carry an astral code point, and says so by refusing");
    }

    /// <summary>
    /// A code point above U+10FFFF is not a code point: the index does not accept
    /// <c>uFFFFFF</c> and key the map on 16 777 215.
    /// </summary>
    [Theory]
    [InlineData("uFFFFFF")]
    [InlineData("u110000")]
    public void ANameAboveTheUnicodeRangeIsRefused(string glyphName)
        => Assert.Empty(BuildIndex(glyphName));

    /// <summary>
    /// The floor: the reverse index must keep ignoring ordinary glyph names, or "agreement" could
    /// be reached by making it accept everything the standard table knows.
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("eacute")]
    [InlineData("space")]
    public void TheReverseIndexStillIgnoresStandardGlyphNames(string glyphName)
        => Assert.Empty(BuildIndex(glyphName));

    private static Dictionary<int, int> BuildIndex(string glyphName)
        => FontCache.BuildUnicodeToGidFromNames(new Dictionary<string, int> { [glyphName] = 7 });
}
