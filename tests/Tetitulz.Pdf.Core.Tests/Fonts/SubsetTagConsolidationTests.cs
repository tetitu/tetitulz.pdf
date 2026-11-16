// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The §9.6.4 subset-tag rule, and the three inputs on which the copies that used to exist
/// disagreed with it.
/// </summary>
/// <remarks>
/// <para>A subset tag is <b>six uppercase letters then <c>'+'</c> then the name</b>. Three separate
/// hand-written strips checked weaker conditions, and each weakening had a visible consequence:</para>
/// <list type="bullet">
/// <item><c>FontCache</c> tested <c>Length &gt;= 7 &amp;&amp; [6] == '+'</c>, so a bare
/// <c>"ABCDEF+"</c> became the <b>empty string</b>, reached
/// <c>SKTypeface.FromFamilyName("")</c> and silently resolved to the platform default face with no
/// diagnostic. Its own comment stated the uppercase rule it did not check.</item>
/// <item><c>PsFontMapper</c> tested position and length but not case, so <c>"abcdef+Arial"</c> —
/// which is <b>not</b> a subset tag — was mapped to the Arial family.</item>
/// <item><c>PdfUaChecker</c> tested neither position nor case, so any <c>'+'</c> anywhere split the
/// name. That feeds the Symbol/ZapfDingbats prefix test deciding <c>IsSymbolic</c>, which gates a
/// real <b>UA26</b> verdict.</item>
/// </list>
/// <para><b>These assert the rule, not the call sites.</b> Two of the three sites are in plugins and
/// one needs a live typeface resolver, so a test driving them would be a host-dependent integration
/// test for a decision that is platform-independent. The rule is what the call sites now share, so
/// the rule is what is pinned; <c>SubsetTag</c> being <c>public</c> is what let them share it.</para>
/// </remarks>
public class SubsetTagConsolidationTests
{
    /// <summary>A well-formed tag is stripped — the case every copy already agreed on.</summary>
    [Theory]
    [InlineData("ABCDEF+Arial", "Arial")]
    [InlineData("AAAAAA+Times New Roman", "Times New Roman")]
    [InlineData("ZZZZZZ+Symbol", "Symbol")]
    public void AWellFormedSubsetTagIsStripped(string baseFont, string expected)
        => Assert.Equal(expected, SubsetTag.Strip(baseFont));

    /// <summary>
    /// <b>The FontCache defect.</b> A bare tag names nothing, so stripping it would return the
    /// empty string — worse than a name that merely looks odd.
    /// </summary>
    [Fact]
    public void ABareTagNamingNothingIsLeftAlone()
        => Assert.Equal("ABCDEF+", SubsetTag.Strip("ABCDEF+"));

    /// <summary>
    /// <b>The PsFontMapper defect.</b> §9.6.4 says uppercase, so a lowercase or mixed-case prefix is
    /// part of the name rather than a tag.
    /// </summary>
    [Theory]
    [InlineData("abcdef+Arial")]
    [InlineData("AbCdEf+Arial")]
    [InlineData("ABCDE1+Arial")]
    public void ATagThatIsNotSixUppercaseLettersIsLeftAlone(string baseFont)
        => Assert.Equal(baseFont, SubsetTag.Strip(baseFont));

    /// <summary>
    /// <b>The PdfUaChecker defect.</b> A <c>'+'</c> somewhere other than position 6 is an ordinary
    /// character in the name. <c>"Symbol+Bold"</c> is the case that changed a UA26 verdict: split at
    /// the plus it becomes <c>"Bold"</c>, which is not symbolic, so the checker answered about a
    /// string the file never named.
    /// </summary>
    [Theory]
    [InlineData("Symbol+Bold")]
    [InlineData("Sym+bolMT")]
    [InlineData("AB+Arial")]
    [InlineData("ABCDEFG+Arial")]
    public void APlusThatIsNotAtPositionSixIsLeftAlone(string baseFont)
        => Assert.Equal(baseFont, SubsetTag.Strip(baseFont));

    /// <summary>
    /// The floor: a name that carries no tag at all comes back untouched, including the empty and
    /// null cases the call sites can reach from a missing or malformed <c>/BaseFont</c>.
    /// </summary>
    [Theory]
    [InlineData("Arial", "Arial")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void AnUntaggedNameIsUnchanged(string? baseFont, string? expected)
        => Assert.Equal(expected, SubsetTag.Strip(baseFont));

    /// <summary>
    /// <c>Strip</c> is idempotent, which is what makes it safe at a call site that cannot know
    /// whether an earlier stage already stripped.
    /// </summary>
    [Theory]
    [InlineData("ABCDEF+Arial")]
    [InlineData("abcdef+Arial")]
    [InlineData("ABCDEF+")]
    public void StrippingTwiceIsStrippingOnce(string baseFont)
        => Assert.Equal(SubsetTag.Strip(baseFont), SubsetTag.Strip(SubsetTag.Strip(baseFont)));
}
