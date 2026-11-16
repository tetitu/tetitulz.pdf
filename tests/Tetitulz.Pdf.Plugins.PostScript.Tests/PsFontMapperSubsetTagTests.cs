// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <see cref="PsFontMapper.Resolve"/> strips a §9.6.4 subset tag before matching a family, and it
/// must strip exactly the names the spec calls tags.
/// </summary>
/// <remarks>
/// <para><b>Written because a mutation proved this call site had no test at all.</b> Neutering
/// <c>Core.Fonts.SubsetTag.Strip</c> reddened six tests in Core and two in Base and <b>nothing</b>
/// in this assembly, even though the tag strip decides which family every PostScript font resolves
/// to. A call site a mutation cannot reach is indistinguishable from one that was deleted.</para>
///
/// <para>The copy this replaced tested <c>IndexOf('+') == 6 &amp;&amp; Length &gt; 7</c> — position
/// and length, never case — so a lowercase prefix was treated as a tag and stripped. §9.6.4 spells
/// the tag as six <b>uppercase</b> letters, so <c>"abcdef+Helvetica"</c> is simply a font whose name
/// begins "abcdef+", and resolving it to Helvetica silently draws a different face.</para>
/// </remarks>
public class PsFontMapperSubsetTagTests
{
    /// <summary>A real subset tag is removed, so the family behind it resolves exactly.</summary>
    [Theory]
    [InlineData("ABCDEF+Helvetica", StandardFonts.Helvetica)]
    [InlineData("ZZZZZZ+Times-Roman", StandardFonts.TimesRoman)]
    [InlineData("AAAAAA+Courier", StandardFonts.Courier)]
    public void AWellFormedSubsetTagIsStrippedBeforeMatching(string postScriptName, string expected)
    {
        string resolved = PsFontMapper.Resolve(postScriptName, out bool exact);

        Assert.Equal(expected, resolved);
        Assert.True(exact, "a tagged standard-14 name must still resolve exactly once the tag is gone");
    }

    /// <summary>
    /// <b>The defect.</b> A lowercase prefix is not a subset tag, so the name is not Helvetica and
    /// must not resolve to it exactly.
    /// </summary>
    /// <remarks>
    /// Asserted as "not an exact match" rather than as one particular fallback: what the mapper does
    /// with an unrecognised family is its own business and may reasonably change, but claiming an
    /// <i>exact</i> standard-14 match for a font the file did not name is wrong under any policy.
    /// The paired case above is what stops this passing by the mapper simply never matching.
    /// </remarks>
    [Theory]
    [InlineData("abcdef+Helvetica")]
    [InlineData("AbCdEf+Helvetica")]
    [InlineData("ABCDE1+Helvetica")]
    public void ALowercaseOrNonAlphabeticPrefixIsNotATagAndDoesNotResolveExactly(string postScriptName)
    {
        PsFontMapper.Resolve(postScriptName, out bool exact);

        Assert.False(exact,
            "a prefix that is not six uppercase letters is part of the name, not a §9.6.4 subset tag");
    }

    /// <summary>
    /// <b>Register row 72.</b> The PostScript-suffixed spellings a real program writes must resolve
    /// to their true family, not fall back to Helvetica.
    /// </summary>
    /// <remarks>
    /// <para>This mapper carried its own family table plus a <c>StripStyleWords</c> pass, and the
    /// pass cut <c>"timesnewromanpsmt"</c> at <c>"roman"</c> — leaving <c>"timesnew"</c>, which
    /// matched nothing. A program doing
    /// <c>/TimesNewRomanPSMT findfont 12 scalefont setfont</c> therefore emitted
    /// <c>/BaseFont /Helvetica</c> and measured <c>stringwidth</c> against the <b>Helvetica</b> AFM
    /// table: Times text drawn in Helvetica, at Helvetica advances.</para>
    ///
    /// <para>The reported reason was false as well — the caller said "no metric-compatible
    /// substitute is known" while the other implementation of this same rule, two assemblies away,
    /// knew Times-Roman.</para>
    /// </remarks>
    [Theory]
    [InlineData("TimesNewRomanPSMT", StandardFonts.TimesRoman)]
    [InlineData("TimesNewRomanPS-BoldMT", StandardFonts.TimesBold)]
    [InlineData("CourierNewPSMT", StandardFonts.Courier)]
    [InlineData("ArialMT", StandardFonts.Helvetica)]
    [InlineData("Cour", StandardFonts.Courier)]
    [InlineData("TiRo", StandardFonts.TimesRoman)]
    public void APostScriptSuffixedNameResolvesExactlyRatherThanFallingBack(
        string postScriptName, string expected)
    {
        string resolved = PsFontMapper.Resolve(postScriptName, out bool exact);

        Assert.Equal(expected, resolved);
        Assert.True(exact, "a name whose family is known is not a substitution");
    }

    /// <summary>
    /// The floor: a font that really is unknown still falls back to Helvetica and still says so, so
    /// the theory above cannot pass by <c>exact</c> having been hardcoded true.
    /// </summary>
    [Theory]
    [InlineData("Garamond")]
    [InlineData("MinionPro-Regular")]
    [InlineData("Wingdings")]
    public void AnUnknownFamilyStillFallsBackAndReportsIt(string postScriptName)
    {
        string resolved = PsFontMapper.Resolve(postScriptName, out bool exact);

        Assert.Equal(StandardFonts.Helvetica, resolved);
        Assert.False(exact, "a substitution must be reported as one");
    }

    /// <summary>
    /// A <c>'+'</c> anywhere other than position 6 is an ordinary character, and a bare tag names
    /// nothing — stripping it would leave the empty string.
    /// </summary>
    [Theory]
    [InlineData("AB+Helvetica")]
    [InlineData("ABCDEFG+Helvetica")]
    [InlineData("ABCDEF+")]
    public void APlusThatIsNotASubsetTagIsNotStripped(string postScriptName)
    {
        PsFontMapper.Resolve(postScriptName, out bool exact);

        Assert.False(exact, "only a six-uppercase-letter prefix at position 0 is a subset tag");
    }
}
