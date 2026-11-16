// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.LigatureFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A font's own ligatures (<c>GSUB</c> type 4, under <c>liga</c> when the producer asks and
/// <c>rlig</c> always): several glyphs become one, the run's mapping keeps the letters as the
/// ligature's <see cref="GlyphMapping.Text"/>, and <c>/ToUnicode</c> writes them back. See
/// <see cref="F"/>: f, i, l and the fi, fl and ffi ligatures no code point maps to.
/// </summary>
/// <remarks>
/// <b>The wall this takes down.</b> Every producer held a one-glyph-per-code-point run, so a
/// ligature — which has no code point of its own — could not be formed without losing the letters
/// it stands for in <c>/ToUnicode</c>. The glyph now carries its typed text, and the one writer
/// takes it beside the code points.
/// </remarks>
public class LigatureTests
{
    private static readonly TextRunOptions Asked = new TextRunOptions { Ligatures = true };

    /// <summary>
    /// f then i and f then l are drawn as the fi and fl ligatures in every shape that reaches the
    /// substitution — the lookup itself, a chained-context rule applying it, an extension lookup.
    /// The run's width is the ligatures' (520 + 540), each glyph maps to its first letter and
    /// carries both letters as its text.
    /// </summary>
    [Theory]
    [InlineData(F.Form.Direct)]
    [InlineData(F.Form.Chain)]
    [InlineData(F.Form.Extension)]
    public void FiAndFl_AreFormed(F.Form form)
    {
        ShapedGlyphRun run = Compose(F.Build(form), "fifl", Asked);

        Assert.Equal(new[] { F.FiGlyph, F.FlGlyph }, run.GlyphIds);
        Assert.Equal(new[] { 'f', 'f' }, run.Glyphs.Select(g => (char)g.CodePoint));
        Assert.Equal(new[] { "fi", "fl" }, run.Glyphs.Select(g => g.Text));
        Assert.Equal(1060d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>A glyph no ligature touched keeps a null text: it stands for its code point alone.</summary>
    [Fact]
    public void AGlyphOutsideALigature_HasNoText()
    {
        ShapedGlyphRun run = Compose(F.Build(), "afi", Asked);

        Assert.Equal(new[] { F.AGlyph, F.FiGlyph }, run.GlyphIds);
        Assert.Null(run.Glyphs[0].Text);
        Assert.Equal("fi", run.Glyphs[1].Text);
    }

    /// <summary>
    /// A three-component ligature: f, f, i is the ffi ligature (800), not an f and the fi ligature —
    /// the set's first ligature that matches wins, and a font lists the longest first. Where it does
    /// not match (f, f, l) the second f takes the fl ligature and the first stays.
    /// </summary>
    [Fact]
    public void AThreeComponentLigature_Forms_AndTheFirstGlyphStaysWhereItDoesNot()
    {
        ShapedGlyphRun ffi = Compose(F.Build(), "ffi", Asked);
        Assert.Equal(new[] { F.FfiGlyph }, ffi.GlyphIds);
        Assert.Equal("ffi", ffi.Glyphs[0].Text);
        Assert.Equal(800d, ffi.AdvanceGlyphSpaceUnits);

        ShapedGlyphRun ffl = Compose(F.Build(), "ffl", Asked);
        Assert.Equal(new[] { F.FGlyph, F.FlGlyph }, ffl.GlyphIds);
        Assert.Equal(new string[] { null, "fl" }, ffl.Glyphs.Select(g => g.Text));
    }

    /// <summary>
    /// <c>liga</c> is a feature a user can turn off, and Word leaves it off: it applies only when the
    /// producer asks (<see cref="TextRunOptions.Ligatures"/>). The same lookup under <c>rlig</c>
    /// forms either way, and under <c>dlig</c> not with only the standard switch on.
    /// </summary>
    [Fact]
    public void Liga_AppliesOnlyWhenAsked_RligAlways()
    {
        Assert.Equal(new[] { F.FGlyph, F.IGlyph }, Compose(F.Build(feature: "liga"), "fi").GlyphIds);
        Assert.Equal(new[] { F.FiGlyph }, Compose(F.Build(feature: "liga"), "fi", Asked).GlyphIds);
        Assert.Equal(new[] { F.FiGlyph }, Compose(F.Build(feature: "rlig"), "fi").GlyphIds);
        Assert.Equal(new[] { F.FiGlyph }, Compose(F.Build(feature: "rlig"), "fi", Asked).GlyphIds);
        Assert.Equal(new[] { F.FGlyph, F.IGlyph }, Compose(F.Build(feature: "dlig"), "fi", Asked).GlyphIds);
    }

    /// <summary>
    /// The discretionary (<c>dlig</c>) and historical (<c>hlig</c>) ligatures form only when their own
    /// switch asks — neither the standard switch nor the other one reaches them.
    /// </summary>
    [Fact]
    public void DligAndHlig_FormOnlyWhenTheirOwnSwitchAsks()
    {
        var discretionary = new TextRunOptions { DiscretionaryLigatures = true };
        var historical = new TextRunOptions { HistoricalLigatures = true };

        Assert.Equal(new[] { F.FiGlyph }, Compose(F.Build(feature: "dlig"), "fi", discretionary).GlyphIds);
        Assert.Equal(new[] { F.FGlyph, F.IGlyph }, Compose(F.Build(feature: "dlig"), "fi", historical).GlyphIds);
        Assert.Equal(new[] { F.FGlyph, F.IGlyph }, Compose(F.Build(feature: "dlig"), "fi", Asked).GlyphIds);

        Assert.Equal(new[] { F.FiGlyph }, Compose(F.Build(feature: "hlig"), "fi", historical).GlyphIds);
        Assert.Equal(new[] { F.FGlyph, F.IGlyph }, Compose(F.Build(feature: "hlig"), "fi", discretionary).GlyphIds);
        Assert.Equal(new[] { F.FGlyph, F.IGlyph }, Compose(F.Build(feature: "hlig"), "fi").GlyphIds);

        // A discretionary ligature is written back as its letters, as a standard one is.
        Assert.Equal("fi", Compose(F.Build(feature: "dlig"), "fi", discretionary).Glyphs[0].Text);
    }

    /// <summary>
    /// A mark between the components: a lookup flagged IgnoreMarks matches across the acute, forms
    /// the ligature where the f was and leaves the acute after it, still mapped to its own code
    /// point; the same lookup without the flag finds the acute where the i should be and forms
    /// nothing.
    /// </summary>
    [Fact]
    public void AMarkBetweenTheComponents_IsSkippedOnlyWhenTheLookupSaysSo()
    {
        ShapedGlyphRun skipped = Compose(F.Build(F.Form.IgnoreMarks), "f́i", Asked);
        Assert.Equal(new[] { F.FiGlyph, F.AcuteGlyph }, skipped.GlyphIds);
        Assert.Equal(new[] { "fi", null }, skipped.Glyphs.Select(g => g.Text));
        Assert.Equal(0x0301, skipped.Glyphs[1].CodePoint);

        ShapedGlyphRun blocked = Compose(F.Build(F.Form.Direct), "f́i", Asked);
        Assert.Equal(new[] { F.FGlyph, F.AcuteGlyph, F.IGlyph }, blocked.GlyphIds);
    }

    /// <summary>
    /// A removed component is gone for every later lookup: a rule after the ligature lookup that turns
    /// an a into an l when the glyph before it is the fi ligature sees the ligature, not the i it
    /// absorbed — f, i, a is the fi ligature then an l. Without a ligature before it the a stays, so
    /// the rule's look back is what the test reads.
    /// </summary>
    [Fact]
    public void ARuleAfterTheLigature_LooksBackOverTheRemovedComponent()
    {
        ShapedGlyphRun after = Compose(F.Build(F.Form.FollowedByRule), "fia", Asked);
        Assert.Equal(new[] { F.FiGlyph, F.LGlyph }, after.GlyphIds);
        Assert.Equal('a', after.Glyphs[1].CodePoint);

        ShapedGlyphRun bare = Compose(F.Build(F.Form.FollowedByRule), "fa", Asked);
        Assert.Equal(new[] { F.FGlyph, F.AGlyph }, bare.GlyphIds);
    }

    /// <summary>
    /// A ligature table declaring more components than it holds runs past its end: the components
    /// beyond it match nothing, so it never forms, and the fi ligature after it in the set still
    /// does — for f, i and for f, f, i, where the corrupt ffi would have matched.
    /// </summary>
    [Fact]
    public void ALigatureRunningPastItsTable_NeverForms_AndTheNextStillDoes()
    {
        byte[] font = F.Build(corruptFfi: true);
        Assert.Equal(new[] { F.FiGlyph }, Compose(font, "fi", Asked).GlyphIds);
        Assert.Equal(new[] { F.FGlyph, F.FiGlyph }, Compose(font, "ffi", Asked).GlyphIds);
    }

    /// <summary>
    /// The ligature's letters reach <c>/ToUnicode</c>: recorded beside the code points
    /// (<see cref="ShapedGlyphRun.RecordUsedGlyphs"/>) and written as the text, <c>&lt;0004&gt;
    /// &lt;00660069&gt;</c>, while a plain glyph is written as its code point. Without the text the
    /// same code would be written as the f alone — the control that the channel is live.
    /// </summary>
    [Fact]
    public void TheLigaturesLetters_AreWrittenToUnicode()
    {
        ShapedGlyphRun run = Compose(F.Build(), "afi", Asked);
        var gidToUnicode = new Dictionary<ushort, int>();
        var gidToText = new Dictionary<ushort, string>();
        run.RecordUsedGlyphs(gidToUnicode, gidToText);

        Assert.Equal('a', gidToUnicode[F.AGlyph]);
        Assert.Equal('f', gidToUnicode[F.FiGlyph]);
        Assert.Equal(new KeyValuePair<ushort, string>(F.FiGlyph, "fi"), Assert.Single(gidToText));

        string cmap = Encoding.ASCII.GetString(ToUnicodeCMapWriter.Build(gidToUnicode, gidToText));
        Assert.Contains("<0004> <00660069>", cmap);
        Assert.Contains("<0007> <0061>", cmap);

        string without = Encoding.ASCII.GetString(ToUnicodeCMapWriter.Build(gidToUnicode));
        Assert.Contains("<0004> <0066>", without);
        Assert.DoesNotContain("<00660069>", without);
    }

    /// <summary>
    /// The composite font writer takes the text channel: the <c>/ToUnicode</c> stream of a font
    /// built from the registers writes the ligature as its letters.
    /// </summary>
    [Fact]
    public void TheCompositeFontWriter_WritesTheLigatureAsItsLetters()
    {
        var program = TrueTypeFontProgram.Parse(F.Build());
        ShapedGlyphRun run = Compose(F.Build(), "fi", Asked);
        var gidToUnicode = new Dictionary<ushort, int>();
        var gidToText = new Dictionary<ushort, string>();
        run.RecordUsedGlyphs(gidToUnicode, gidToText);

        int next = 1;
        var (_, objects) = Type0FontBuilder.BuildObjects(ref next, program, gidToUnicode, gidToText);
        string streams = string.Concat(objects.Select(o => o.obj).OfType<Core.Objects.PdfStream>()
            .Select(s => Encoding.ASCII.GetString(s.GetDecodedData())));
        Assert.Contains("<0004> <00660069>", streams);
    }

    private static ShapedGlyphRun Compose(byte[] font, string text, TextRunOptions options = null)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(font), options);
}
