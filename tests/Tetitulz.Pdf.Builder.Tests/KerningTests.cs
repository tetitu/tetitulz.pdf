// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.KerningFont;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The Builder's kerning: off unless <see cref="PdfExportOptions.Kerning"/> or a run's own
/// <see cref="TextRun.Kerning"/> asks. With <see cref="F"/>, "To" at 20 points kerned is
/// <c>[&lt;0003&gt; 110 &lt;0004&gt;] TJ</c> — T's advance narrowed by 110 thousandths — and unkerned
/// <c>&lt;00030004&gt; Tj</c>.
/// </summary>
public class KerningTests
{
    private const string Kerned = "[<0003> 110 <0004>] TJ";
    private const string Plain = "<00030004> Tj";

    [Fact]
    public void KerningIsOffByDefault()
    {
        string content = Content(new TextRun { Text = "To" }, optionKerning: false);
        Assert.Contains(Plain, content);
        Assert.DoesNotContain(Kerned, content);
    }

    [Fact]
    public void TheExportOption_KernsEveryRun()
    {
        string content = Content(new TextRun { Text = "To" }, optionKerning: true);
        Assert.Contains(Kerned, content);
        Assert.DoesNotContain(Plain, content);
    }

    /// <summary>A run's own setting wins over the option, either way.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ARunsOwnSetting_OverridesTheOption(bool run, bool option)
    {
        string content = Content(new TextRun { Text = "To", Kerning = run }, optionKerning: option);
        Assert.Contains(run ? Kerned : Plain, content);
        Assert.DoesNotContain(run ? Plain : Kerned, content);
    }

    /// <summary>
    /// The kerned run is narrower by what it is kerned by, and the layout knows: right-aligned, it
    /// starts 2.2 points (110 thousandths of 20) further right.
    /// </summary>
    [Fact]
    public void AKernedRun_IsLaidOutAtItsKernedWidth()
    {
        double plain = TmX(Content(new TextRun { Text = "To" }, optionKerning: false, TextAlignment.Right));
        double kerned = TmX(Content(new TextRun { Text = "To" }, optionKerning: true, TextAlignment.Right));
        Assert.Equal(2.2, kerned - plain, 3);
    }

    /// <summary>
    /// A leader of text (<see cref="TabStop.LeaderText"/>, 2026-10-19) kerned: "oT" repeated kerns T into the next o,
    /// 2.2 points a junction, so n repetitions are n widths less n - 1 kerns, and as many fit as that allows — more
    /// than the unit's own width would say. The width of "oT" is measured from where a right-aligned one starts.
    /// </summary>
    [Fact]
    public void AContentLeader_CountsTheKerningBetweenItsRepetitions()
    {
        const double Stop = 300, Kern = 2.2;
        double unit = Width("oT");
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Tabs.Add(new TabStop { PosPt = Stop, LeaderText = "oT" });
        p.Runs.Add(new TabRun());
        p.Runs.Add(new TextRun { Text = "T", FontName = "KerningFixture", FontSize = 20, Kerning = true });
        section.Children.Add(p);
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions { AllowSystemFontSubstitution = false, FontProvider = _ => F.Build() }));

        // The o glyphs (id 4) the text operators draw — a TJ array's strings and a Tj's — not the ToUnicode CMap's entries.
        int os = 0;
        foreach (System.Text.RegularExpressions.Match shown in System.Text.RegularExpressions.Regex.Matches(content, @"\[([^\]]*)\]\s*TJ|(<[0-9A-Fa-f]+>)\s*Tj"))
            foreach (System.Text.RegularExpressions.Match hex in System.Text.RegularExpressions.Regex.Matches(shown.Value, "<([0-9A-Fa-f]+)>"))
                for (int i = 0; i + 4 <= hex.Groups[1].Value.Length; i += 4)
                    if (hex.Groups[1].Value.Substring(i, 4) == "0004") os++;

        int exact = 1 + (int)((Stop - unit) / (unit - Kern));
        Assert.True(exact > (int)(Stop / unit), "the kerning saves too little for the count to show it");
        Assert.Equal(exact, os);
    }

    /// <summary>
    /// The width of <paramref name="text"/> at 20 points unkerned: right-aligned it starts its width before the band's
    /// end, the band being the section's page less its margins from where a left-aligned one starts.
    /// </summary>
    private static double Width(string text)
    {
        var section = new Section();
        double band = section.PageWidthPt - section.MarginLeft - section.MarginRight;
        double left = TmX(Content(new TextRun { Text = text }, optionKerning: false, TextAlignment.Left));
        double right = TmX(Content(new TextRun { Text = text }, optionKerning: false, TextAlignment.Right));
        return left + band - right;
    }

    private static double TmX(string content)
    {
        var m = System.Text.RegularExpressions.Regex.Match(content, @"1 0 0 1 (\S+) \S+ Tm");
        Assert.True(m.Success, "no text matrix was written");
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Content(TextRun run, bool optionKerning, TextAlignment alignment = TextAlignment.Left)
    {
        run.FontName = "KerningFixture";
        run.FontSize = 20;
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Alignment = alignment };
        p.Runs.Add(run);
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => F.Build(),
            Kerning = optionKerning,
        }));
    }
}
