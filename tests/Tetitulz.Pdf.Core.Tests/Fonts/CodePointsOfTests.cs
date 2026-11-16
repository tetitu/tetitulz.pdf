// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="FontResolutionRequest.CodePointsOf"/> — the one string → code-point conversion for
/// font resolution.
/// </summary>
/// <remarks>
/// <para>It exists because <c>foreach (char c in text) required.Add(c)</c> <b>looks</b> like it
/// already does this. Over anything outside the BMP it does not: it yields the two halves of the
/// surrogate pair instead of the character, no <c>cmap</c> maps a lone surrogate, and the
/// resolution then fails with "no font on this host covers U+D83D" for text that most of the fonts
/// on the machine can draw. Three call sites built this set; two had their own correct loop and the
/// third was the naive one.</para>
/// </remarks>
public class CodePointsOfTests
{
    /// <summary>The claim. U+1F642 is one code point, not U+D83D and U+DE42.</summary>
    [Fact]
    public void CodePointsOf_CombinesASurrogatePairIntoOneCodePoint()
    {
        var points = FontResolutionRequest.CodePointsOf("a\U0001F642b");

        Assert.Equal(new[] { 'a', 0x1F642, 'b' }, points.OrderBy(cp => cp == 'a' ? 0 : cp == 'b' ? 2 : 1));
        Assert.DoesNotContain(0xD83D, points);
        Assert.DoesNotContain(0xDE42, points);
    }

    /// <summary>
    /// A lone high surrogate — a truncated string, or one cut mid-pair — is passed through as
    /// itself rather than combined with whatever follows it. It cannot be drawn either way; what
    /// matters is that the loop does not run off the end or swallow the next character.
    /// </summary>
    [Fact]
    public void CodePointsOf_MalformedLoneSurrogate_IsPassedThroughWithoutEatingTheNextCharacter()
    {
        var points = FontResolutionRequest.CodePointsOf("\uD83Dz");

        Assert.Contains(0xD83D, points);
        Assert.Contains('z', points);
    }

    [Fact]
    public void CodePointsOf_DeduplicatesAndAcceptsBmpText()
    {
        var points = FontResolutionRequest.CodePointsOf("aab");

        Assert.Equal(2, points.Count);
        Assert.Contains('a', points);
        Assert.Contains('b', points);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CodePointsOf_EmptyInput_IsAnEmptySet(string text)
    {
        Assert.Empty(FontResolutionRequest.CodePointsOf(text));
    }

    /// <summary>
    /// Whitespace is <b>not</b> filtered here, and that is deliberate rather than an omission:
    /// <see cref="SystemFontResolver.Resolve"/> drops space, tab, CR and LF from whatever it is
    /// given, so filtering here too would put one rule in two places and invite them to drift —
    /// which is the defect this helper exists to end.
    /// </summary>
    [Fact]
    public void CodePointsOf_LeavesWhitespaceFilteringToTheResolver()
    {
        var points = FontResolutionRequest.CodePointsOf("a b");

        Assert.Contains(' ', points);
    }
}
