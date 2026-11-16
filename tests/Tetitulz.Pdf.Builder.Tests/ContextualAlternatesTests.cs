// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.RcltFont;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The Builder's contextual alternates (<c>GSUB</c> <c>calt</c>): off unless
/// <see cref="PdfExportOptions.ContextualAlternates"/> or a run's own
/// <see cref="TextRun.ContextualAlternates"/> asks. With <see cref="F"/>'s <c>calt</c> font, beh then
/// yeh is drawn yeh then the beh's alternate, <c>&lt;00020003&gt; Tj</c>, and without it yeh then the
/// beh, <c>&lt;00020001&gt; Tj</c>. The <c>rclt</c> font substitutes either way.
/// </summary>
public class ContextualAlternatesTests
{
    private const string Alternate = "<00020003> Tj";
    private const string Plain = "<00020001> Tj";

    [Fact]
    public void ContextualAlternatesAreOffByDefault()
    {
        string content = Content(new TextRun { Text = "بي" }, option: false);
        Assert.Contains(Plain, content);
        Assert.DoesNotContain(Alternate, content);
    }

    [Fact]
    public void TheExportOption_TurnsThemOn()
    {
        string content = Content(new TextRun { Text = "بي" }, option: true);
        Assert.Contains(Alternate, content);
        Assert.DoesNotContain(Plain, content);
    }

    /// <summary>A run's own setting wins over the option, either way.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ARunsOwnSetting_OverridesTheOption(bool run, bool option)
    {
        string content = Content(new TextRun { Text = "بي", ContextualAlternates = run }, option);
        Assert.Contains(run ? Alternate : Plain, content);
        Assert.DoesNotContain(run ? Plain : Alternate, content);
    }

    /// <summary>The required alternates (<c>rclt</c>) are drawn whatever the option says.</summary>
    [Fact]
    public void RequiredAlternates_AreDrawnWithTheOptionOff()
    {
        string content = Content(new TextRun { Text = "بي" }, option: false, feature: "rclt");
        Assert.Contains(Alternate, content);
        Assert.DoesNotContain(Plain, content);
    }

    private static string Content(TextRun run, bool option, string feature = "calt")
    {
        run.FontName = "RcltFixture";
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
            ContextualAlternates = option,
        }));
    }
}
