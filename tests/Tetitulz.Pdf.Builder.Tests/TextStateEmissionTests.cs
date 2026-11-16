// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The text-state operators the PDF exporter emits around each piece of a line — character
/// spacing (<c>Tc</c>), horizontal scale (<c>Tz</c>) and rise (<c>Ts</c>).
///
/// <para><b>These are graphics-state parameters, so they persist</b> across <c>Tm</c>, across
/// pieces and across <c>BT</c>/<c>ET</c> (§9.3). The interpreter below therefore carries them
/// forward exactly as a reader does and never resets at <c>BT</c> — resetting there would model
/// the exporter's habit rather than the format's rule, and would hide the leak these cover.</para>
///
/// <para><b>Asserted on the operands, not on the ink.</b> A run drawn 21 pt wider than it was
/// measured at still rasterises to a perfectly good page, so pixels cannot see this; the numbers
/// in the content stream can.</para>
/// </summary>
public class TextStateEmissionTests
{
    /// <summary>One text-showing operation with the text state in effect when it ran.</summary>
    private readonly struct Drawn
    {
        public Drawn(double tc, int tz, double ts, double x, double y, double size, string text)
        { Tc = tc; Tz = tz; Ts = ts; X = x; Y = y; Size = size; Text = text; }

        public double Tc { get; }
        public int Tz { get; }
        public double Ts { get; }
        public double X { get; }
        public double Y { get; }
        public double Size { get; }
        public string Text { get; }

        /// <summary>Where the glyphs actually sit: §9.4.4 applies the rise ON TOP of the matrix.</summary>
        public double EffectiveBaseline => Y + Ts;

        public override string ToString() =>
            $"'{Text}' Tm=({X.ToString("0.###", CultureInfo.InvariantCulture)}," +
            $"{Y.ToString("0.###", CultureInfo.InvariantCulture)}) " +
            $"Tc={Tc.ToString(CultureInfo.InvariantCulture)} Tz={Tz} " +
            $"Ts={Ts.ToString(CultureInfo.InvariantCulture)} size={Size.ToString(CultureInfo.InvariantCulture)}";
    }

    private static readonly Regex TcOp = new Regex(@"^(-?[\d.]+)\s+Tc$");
    private static readonly Regex TzOp = new Regex(@"^(-?[\d.]+)\s+Tz$");
    private static readonly Regex TsOp = new Regex(@"^(-?[\d.]+)\s+Ts$");
    private static readonly Regex TfOp = new Regex(@"^/\S+\s+(-?[\d.]+)\s+Tf$");
    private static readonly Regex TmOp = new Regex(@"^1\s+0\s+0\s+1\s+(-?[\d.]+)\s+(-?[\d.]+)\s+Tm$");
    private static readonly Regex TjOp = new Regex(@"^\((.*)\)\s*Tj$");

    private static List<Drawn> Draws(string content)
    {
        var list = new List<Drawn>();
        double tc = 0, ts = 0, x = 0, y = 0, size = 0;
        int tz = 100;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            Match m;
            if ((m = TcOp.Match(line)).Success) { tc = D(m.Groups[1].Value); continue; }
            if ((m = TzOp.Match(line)).Success) { tz = (int)D(m.Groups[1].Value); continue; }
            if ((m = TsOp.Match(line)).Success) { ts = D(m.Groups[1].Value); continue; }
            if ((m = TfOp.Match(line)).Success) { size = D(m.Groups[1].Value); continue; }
            if ((m = TmOp.Match(line)).Success) { x = D(m.Groups[1].Value); y = D(m.Groups[2].Value); continue; }
            if ((m = TjOp.Match(line)).Success)
                list.Add(new Drawn(tc, tz, ts, x, y, size, m.Groups[1].Value));
        }
        return list;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static List<Drawn> Draws(params TextRun[] runs)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        foreach (var r in runs) p.Runs.Add(r);
        section.Children.Add(p);
        doc.Sections.Add(section);
        return Draws(PdfContentProbe.DecodeStreams(doc.ToPdf()));
    }

    private static Drawn Find(List<Drawn> draws, string text)
    {
        foreach (var d in draws) if (d.Text == text) return d;
        Assert.Fail($"'{text}' was never drawn. Draws: {string.Join(" | ", draws)}");
        return default;
    }

    // ================================================================ Tc / Tz / Ts leakage

    /// <summary>
    /// A run that sets character spacing must not leave it set for the next run.
    /// </summary>
    /// <remarks>
    /// The emitter wrote an operator only when the value was non-default and never wrote the
    /// default, so <c>3 Tc</c> was emitted for the first piece and nothing for the second: the
    /// second was drawn with 3 pt of extra spacing per character — 21 pt wider than the width it
    /// was measured at — and overprinted whatever followed it.
    /// </remarks>
    [Fact]
    public void CharSpacingFromOneRun_DoesNotLeakIntoTheNext()
    {
        var draws = Draws(
            new TextRun { Text = "Total", CharSpacing = 3 },
            new TextRun { Text = "1234.00" });

        Assert.Equal(3, Find(draws, "Total").Tc);
        Assert.Equal(0, Find(draws, "1234.00").Tc);
    }

    [Fact]
    public void HorizontalScaleFromOneRun_DoesNotLeakIntoTheNext()
    {
        var draws = Draws(
            new TextRun { Text = "wide", HorizontalScale = 150 },
            new TextRun { Text = "normal" });

        Assert.Equal(150, Find(draws, "wide").Tz);
        Assert.Equal(100, Find(draws, "normal").Tz);
    }

    [Fact]
    public void RiseFromASuperscript_DoesNotLeakIntoTheRestOfTheLine()
    {
        var draws = Draws(
            new TextRun { Text = "base" },
            new TextRun { Text = "sup", VerticalAlign = VerticalAlign.Superscript },
            new TextRun { Text = "after" });

        Assert.NotEqual(0, Find(draws, "sup").Ts);       // the superscript IS raised …
        Assert.Equal(0, Find(draws, "after").Ts);        // … and the run after it is not
    }

    /// <summary>
    /// The state a piece leaves behind is the state it drew with, so a piece that draws nothing
    /// must not publish its own <c>Tc</c>. A zero-width run has no glyphs to show.
    /// </summary>
    [Fact]
    public void ARunThatDrawsNothing_DoesNotPublishItsOwnCharSpacing()
    {
        var draws = Draws(
            new TextRun { Text = "", CharSpacing = 9 },
            new TextRun { Text = "plain" });

        Assert.Equal(0, Find(draws, "plain").Tc);
    }

    // ================================================================ text rise, applied once

    /// <summary>
    /// A superscript's glyphs sit exactly <c>0.35 * FontSize</c> above the surrounding baseline.
    /// </summary>
    /// <remarks>
    /// The rise used to be applied twice — once as the piece's <c>Tm</c> translation and again as
    /// <c>Ts</c>, which §9.4.4 adds ON TOP of the matrix — so a 12 pt superscript was drawn 8.4 pt
    /// up instead of 4.2, above the cap height of its own line, while the underline / strike /
    /// highlight passes used the single offset and detached from the glyphs.
    /// </remarks>
    [Fact]
    public void Superscript_IsRaisedByExactlyOneRise()
    {
        var draws = Draws(
            new TextRun { Text = "base", FontSize = 12 },
            new TextRun { Text = "sup", FontSize = 12, VerticalAlign = VerticalAlign.Superscript });

        double baseline = Find(draws, "base").EffectiveBaseline;
        var sup = Find(draws, "sup");

        Assert.Equal(baseline + 12 * 0.35, sup.EffectiveBaseline, 3);
    }

    [Fact]
    public void Subscript_IsDroppedByExactlyOneDrop()
    {
        var draws = Draws(
            new TextRun { Text = "base", FontSize = 12 },
            new TextRun { Text = "sub", FontSize = 12, VerticalAlign = VerticalAlign.Subscript });

        double baseline = Find(draws, "base").EffectiveBaseline;

        Assert.Equal(baseline - 12 * 0.15, Find(draws, "sub").EffectiveBaseline, 3);
    }

    /// <summary>An explicit <c>BaselineShift</c> takes the same single-application route.</summary>
    [Fact]
    public void BaselineShift_IsAppliedExactlyOnce()
    {
        var draws = Draws(
            new TextRun { Text = "flat", FontSize = 12 },
            new TextRun { Text = "lifted", FontSize = 12, BaselineShift = 5 });

        double baseline = Find(draws, "flat").EffectiveBaseline;

        Assert.Equal(baseline + 5, Find(draws, "lifted").EffectiveBaseline, 3);
    }

    /// <summary>
    /// The decoration passes place the rule at <c>baseline + rise</c>, so once the glyphs are
    /// raised once they and their underline share a baseline. Asserted as the pair, because the
    /// two halves were consistent with each other before the fix only by both being wrong.
    /// </summary>
    [Fact]
    public void SuperscriptUnderline_SitsUnderTheSuperscriptItBelongsTo()
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "base", FontSize = 12 });
        p.Runs.Add(new TextRun
        {
            Text = "sup", FontSize = 12,
            VerticalAlign = VerticalAlign.Superscript, IsUnderline = true,
        });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        var sup = Find(Draws(content), "sup");

        // The one filled rule on the page is the underline; it is drawn a little below the
        // baseline it belongs to (0.12 of the drawn glyph size).
        var rules = FilledRects(content);
        var rule = Assert.Single(rules);
        Assert.InRange(rule.Y, sup.EffectiveBaseline - 0.14 * sup.Size - 0.05,
                               sup.EffectiveBaseline - 0.10 * sup.Size + 0.05);
    }

    private static readonly Regex ReF = new Regex(
        @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\s*\n?f",
        RegexOptions.Singleline);

    private static List<(double X, double Y, double W, double H)> FilledRects(string content)
        => ReF.Matches(content).Cast<Match>()
              .Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value),
                            D(m.Groups[3].Value), D(m.Groups[4].Value)))
              .ToList();
}
