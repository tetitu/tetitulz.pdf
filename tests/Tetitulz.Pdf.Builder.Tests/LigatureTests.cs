// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.LigatureFont;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The Builder's standard ligatures (<c>GSUB</c> <c>liga</c>): off unless
/// <see cref="PdfExportOptions.Ligatures"/> or a run's own <see cref="TextRun.Ligatures"/> asks. With
/// <see cref="F"/>, f then i is drawn as the fi ligature, <c>&lt;0004&gt; Tj</c>, and without it as
/// the two letters, <c>&lt;00010002&gt; Tj</c>. The <c>rlig</c> font forms it either way, and the
/// ligature is written back in <c>/ToUnicode</c> as its letters.
/// </summary>
public class LigatureTests
{
    private const string Ligature = "<0004> Tj";
    private const string Plain = "<00010002> Tj";

    [Fact]
    public void LigaturesAreOffByDefault()
    {
        string content = Content(new TextRun { Text = "fi" }, option: false);
        Assert.Contains(Plain, content);
        Assert.DoesNotContain(Ligature, content);
    }

    [Fact]
    public void TheExportOption_TurnsThemOn()
    {
        string content = Content(new TextRun { Text = "fi" }, option: true);
        Assert.Contains(Ligature, content);
        Assert.DoesNotContain(Plain, content);
    }

    /// <summary>A run's own setting wins over the option, either way.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ARunsOwnSetting_OverridesTheOption(bool run, bool option)
    {
        string content = Content(new TextRun { Text = "fi", Ligatures = run }, option);
        Assert.Contains(run ? Ligature : Plain, content);
        Assert.DoesNotContain(run ? Plain : Ligature, content);
    }

    /// <summary>The required ligatures (<c>rlig</c>) are formed whatever the option says.</summary>
    [Fact]
    public void RequiredLigatures_AreFormedWithTheOptionOff()
    {
        string content = Content(new TextRun { Text = "fi" }, option: false, feature: "rlig");
        Assert.Contains(Ligature, content);
        Assert.DoesNotContain(Plain, content);
    }

    /// <summary>
    /// The ligature glyph's <c>/ToUnicode</c> entry is its letters, <c>&lt;0004&gt; &lt;00660069&gt;</c>,
    /// so a search for "fi" finds it; the control without ligatures maps each letter's glyph to itself.
    /// </summary>
    [Fact]
    public void TheLigature_IsWrittenBackAsItsLetters()
    {
        string on = Content(new TextRun { Text = "fi" }, option: true);
        Assert.Contains("<0004> <00660069>", on);

        string off = Content(new TextRun { Text = "fi" }, option: false);
        Assert.Contains("<0001> <0066>", off);
        Assert.Contains("<0002> <0069>", off);
        Assert.DoesNotContain("<00660069>", off);
    }

    /// <summary>
    /// The discretionary and historical ligatures: a run's own switch, or the export option, forms the
    /// font's <c>dlig</c> or <c>hlig</c>; the standard switch does not.
    /// </summary>
    [Fact]
    public void DiscretionaryAndHistoricalLigatures_FollowTheirOwnSwitches()
    {
        Assert.Contains(Ligature, Content(new TextRun { Text = "fi", DiscretionaryLigatures = true }, option: false, feature: "dlig"));
        Assert.Contains(Plain, Content(new TextRun { Text = "fi" }, option: true, feature: "dlig"));
        Assert.Contains(Ligature, Content(new TextRun { Text = "fi" }, option: false, feature: "dlig", discretionaryOption: true));
        Assert.Contains(Plain, Content(new TextRun { Text = "fi", DiscretionaryLigatures = false }, option: false, feature: "dlig", discretionaryOption: true));

        Assert.Contains(Ligature, Content(new TextRun { Text = "fi", HistoricalLigatures = true }, option: false, feature: "hlig"));
        Assert.Contains(Plain, Content(new TextRun { Text = "fi", DiscretionaryLigatures = true }, option: false, feature: "hlig"));
        Assert.Contains(Ligature, Content(new TextRun { Text = "fi" }, option: false, feature: "hlig", historicalOption: true));
    }

    private static string Content(TextRun run, bool option, string feature = "liga",
                                  bool discretionaryOption = false, bool historicalOption = false)
    {
        run.FontName = "LigatureFixture";
        run.FontSize = 20;
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(run);
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => F.Build(feature: feature),
            Ligatures = option,
            DiscretionaryLigatures = discretionaryOption,
            HistoricalLigatures = historicalOption,
        }));
    }
}
