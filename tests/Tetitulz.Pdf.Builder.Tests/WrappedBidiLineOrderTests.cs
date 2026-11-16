// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Bidi ordering on <b>wrapped</b> lines, which <see cref="BidiLineOrderTests"/> could not reach:
/// its only wrapping case is pure Hebrew, and appending the first logical word rightmost happens
/// to be correct there, so the defect this file pins was invisible.
///
/// <para>Like its sibling these assert on the <c>Tm</c> coordinates the renderer emitted, never on
/// extracted text — the extractor re-applies bidi per line, so wrongly ordered output extracts to
/// the right logical string.</para>
/// </summary>
public class WrappedBidiLineOrderTests
{
    private const string Shalom = "שלום";
    private const string Olam = "עולם";

    private static PdfExportOptions WithFont() => new PdfExportOptions
    {
        AllowSystemFontSubstitution = false,
        FontProvider = _ => MinimalTrueType.Build(Shalom + Olam + "abcdefghijklmnopqrstuvwxyz "),
    };

    /// <summary>Distinct two-letter Latin words, so each draw identifies its own source position.</summary>
    private static string Word(int i) => new string(new[] { (char)('a' + i / 26), (char)('a' + i % 26) });

    /// <summary>
    /// A left-to-right paragraph carrying Hebrew, long enough to wrap. Every Latin word on a line
    /// is at bidi level 0, so on the page they must read left to right in source order — an
    /// invariant that holds whatever the wrap points turn out to be.
    /// </summary>
    [Fact]
    public void WrappedLine_OpeningWord_IsDrawnAtTheStartOfItsOwnLine()
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < 90; i++)
        {
            string w = Word(i);
            words.Add(w);
            sb.Append(w).Append(' ');
            if (i % 3 == 2) sb.Append(i % 6 == 2 ? Shalom : Olam).Append(' ');
        }

        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Direction = TextDirection.LeftToRight };
        p.Runs.Add(new TextRun { Text = sb.ToString().TrimEnd() });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(WithFont()));

        var index = new Dictionary<string, int>();
        for (int i = 0; i < words.Count; i++) index[words[i]] = i;

        var latin = PdfContentProbe.DrawnTexts(content)
            .Where(d => index.ContainsKey(d.Text))
            .ToList();
        var hebrewYs = PdfContentProbe.DrawnGlyphRuns(content)
            .Select(r => r.Y).ToList();

        // The fixture must actually reach the feature: several wrapped lines, each carrying both
        // scripts. Without this the assertions below could pass on a document that never wrapped.
        var lines = latin.GroupBy(d => Math.Round(d.Y, 2))
            .Where(g => g.Count() >= 2 && hebrewYs.Any(y => Math.Abs(y - g.Key) < 0.01))
            .OrderByDescending(g => g.Key)
            .ToList();
        Assert.True(lines.Count >= 3,
            $"fixture must produce at least 3 wrapped mixed-script lines, got {lines.Count}");

        foreach (var line in lines)
        {
            var byX = line.OrderBy(d => d.X).Select(d => index[d.Text]).ToArray();
            var sorted = byX.OrderBy(i => i).ToArray();
            Assert.True(byX.SequenceEqual(sorted),
                "Latin words on one left-to-right line must be drawn in source order; got " +
                string.Join(", ", line.OrderBy(d => d.X).Select(d => d.Text)) +
                $" on the line at y={line.Key}");
        }
    }

    /// <summary>
    /// The control: the same shape with no Hebrew never reorders, so a failure above is about the
    /// bidi range and not about wrapping in general.
    /// </summary>
    [Fact]
    public void WrappedLatinOnlyParagraph_KeepsSourceOrder()
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < 90; i++) { words.Add(Word(i)); sb.Append(Word(i)).Append(' '); }

        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = sb.ToString().TrimEnd() });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(WithFont()));
        var index = new Dictionary<string, int>();
        for (int i = 0; i < words.Count; i++) index[words[i]] = i;

        var lines = PdfContentProbe.DrawnTexts(content)
            .Where(d => index.ContainsKey(d.Text))
            .GroupBy(d => Math.Round(d.Y, 2))
            .Where(g => g.Count() >= 2)
            .ToList();
        Assert.True(lines.Count >= 3, $"fixture must wrap, got {lines.Count} lines");

        foreach (var line in lines)
        {
            var byX = line.OrderBy(d => d.X).Select(d => index[d.Text]).ToArray();
            Assert.True(byX.SequenceEqual(byX.OrderBy(i => i).ToArray()),
                "a Latin-only line must be drawn in source order");
        }
    }
}
