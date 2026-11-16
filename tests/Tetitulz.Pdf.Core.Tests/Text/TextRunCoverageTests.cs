// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="TextRunComposer.CodePointsToCover"/> — the code points a font has to cover to draw a
/// run, which for Arabic are the shaped forms and not the letters that were typed.
/// </summary>
/// <remarks>
/// <para><b>Why this is a question of its own.</b> Every producer in this tree resolved a font by the
/// code points of the text and then shaped it. Shaping substitutes presentation forms before a glyph
/// is looked up, so a font that covers the letters and not the forms passes the coverage test and
/// then cannot draw a single glyph of the run — and what each caller did next differed: the form and
/// template appearance generators refused the run, the DOCX renderer drew spaces.</para>
/// <para>The last test is the separating input: one font built over the letters, one over the forms,
/// and only the second can compose the run.</para>
/// </remarks>
public class TextRunCoverageTests
{
    /// <summary>بيت — three letters that all join, so every one of them is substituted.</summary>
    private const string Arabic = "بيت";

    [Fact]
    public void ForArabicItAsksForThePresentationFormsRatherThanTheLetters()
    {
        var needed = TextRunComposer.CodePointsToCover(Arabic);

        Assert.All(needed, cp => Assert.InRange(cp, 0xFE70, 0xFEFF));
        Assert.Empty(needed.Intersect(FontResolutionRequest.CodePointsOf(Arabic)));
    }

    [Theory]
    [InlineData("שלום")]          // Hebrew: reordered, never substituted
    [InlineData("Ada Lovelace")]  // Latin
    [InlineData("漢字")]           // CJK
    public void ForEverythingThatIsNotShapedItIsTheTextItself(string text)
    {
        Assert.Equal(FontResolutionRequest.CodePointsOf(text).OrderBy(c => c),
                     TextRunComposer.CodePointsToCover(text).OrderBy(c => c));
    }

    [Fact]
    public void AFontCoveringOnlyTheLettersCannotDrawTheRunAndOneCoveringTheFormsCan()
    {
        var lettersOnly = TrueTypeFontProgram.Parse(MinimalTrueType.Build(Arabic));
        var formsToo = TrueTypeFontProgram.Parse(
            MinimalTrueType.Build(new string(TextRunComposer.CodePointsToCover(Arabic)
                .Select(char.ConvertFromUtf32).SelectMany(s => s).ToArray())));

        Assert.NotNull(TextRunComposer.Compose(Arabic, lettersOnly, new TextRunOptions()).MissingCodePoint);
        Assert.True(TextRunComposer.Compose(Arabic, formsToo, new TextRunOptions()).IsComplete,
            "a font built over exactly the code points this asks for must draw the whole run");
    }
}
