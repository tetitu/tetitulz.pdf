// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A producer writes lam followed by alef as the lam-alef ligature, as Arabic requires: isolated
/// after a lam that begins its word (U+FEFB), final after one joined from the right (U+FEFC), with the
/// alef's madda or hamza kept (U+FEF5–FEFA) — where the font has the glyph, and with <c>/ToUnicode</c>
/// writing lam and alef back. Before, lam and alef were two glyphs, which a reader sees as a misspelling.
/// </summary>
/// <remarks>
/// <c>MinimalTrueType</c> numbers its glyphs from 1 in code-point order, so each test lists the forms
/// the font covers and reads the glyph off that order.
/// </remarks>
public class ArabicLigatureTests
{
    private const string Lam = "ل", Alef = "ا", Seen = "س", Fatha = "َ";

    private static ushort GlyphOf(string covers, int codePoint)
        => (ushort)(covers.Select(c => (int)c).Distinct().OrderBy(c => c).ToList().IndexOf(codePoint) + 1);

    [Theory]
    [InlineData("لا", 0xFEFB)]   // lam, alef: the isolated ligature
    [InlineData("لآ", 0xFEF5)]   // alef with madda
    [InlineData("لأ", 0xFEF7)]   // alef with hamza above
    [InlineData("لإ", 0xFEF9)]   // alef with hamza below
    public void LamThenAlef_IsTheIsolatedLigature(string typed, int ligature)
    {
        string covers = "ﻟﺎﺂﺄﺈ" + (char)ligature;
        var font = TrueTypeFontProgram.Parse(MinimalTrueType.Build(covers));
        ShapedGlyphRun run = TextRunComposer.Compose(typed, font, null);

        Assert.Equal(new[] { GlyphOf(covers, ligature) }, run.GlyphIds);
        Assert.Equal(ligature, run.Glyphs[0].CodePoint);
    }

    /// <summary>After a letter that joins to it, the lam is medial and the ligature final: seen, lam-alef.</summary>
    [Fact]
    public void AJoinedLamThenAlef_IsTheFinalLigature()
    {
        const string covers = "ﺳﻠﺎﻼ";
        var font = TrueTypeFontProgram.Parse(MinimalTrueType.Build(covers));
        ShapedGlyphRun run = TextRunComposer.Compose(Seen + Lam + Alef, font, null);

        Assert.Equal(new[] { GlyphOf(covers, 0xFEFC), GlyphOf(covers, 0xFEB3) }, run.GlyphIds);   // visual order
    }

    /// <summary>
    /// Without the ligature's glyph, or with a mark between lam and alef, the two stay two glyphs —
    /// the lam initial and the alef final, as joining makes them.
    /// </summary>
    [Fact]
    public void WithoutTheGlyphOrAcrossAMark_LamAndAlefStayTwo()
    {
        const string covers = "ﻟﺎَ";
        var font = TrueTypeFontProgram.Parse(MinimalTrueType.Build(covers));
        Assert.Equal(new[] { GlyphOf(covers, 0xFE8E), GlyphOf(covers, 0xFEDF) }, TextRunComposer.Compose(Lam + Alef, font, null).GlyphIds);

        var withLigature = TrueTypeFontProgram.Parse(MinimalTrueType.Build(covers + "ﻻ"));
        Assert.Equal(3, TextRunComposer.Compose(Lam + Fatha + Alef, withLigature, null).GlyphIds.Length);
    }

    [Fact]
    public void TheLigaturesUnicode_IsLamThenAlef()
    {
        Assert.Equal(Lam + Alef, ArabicLigatures.Decomposition(0xFEFB));
        Assert.Equal(Lam + "إ", ArabicLigatures.Decomposition(0xFEFA));
        Assert.Null(ArabicLigatures.Decomposition(0xFEDF));

        string cmap = Encoding.ASCII.GetString(ToUnicodeCMapWriter.Build(new[] { new KeyValuePair<ushort, int>(3, 0xFEFB) }));
        Assert.Contains("<0003> <06440627>", cmap);
    }
}
