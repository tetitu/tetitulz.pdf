// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Multiline wrapping and comb layout in a generated field appearance.
///
/// <para>Both were previously listed as limits: a multiline field broke on explicit newlines only,
/// and a comb field was drawn as ordinary text. The assertions here are on the emitted operators —
/// how many positioned runs there are and where they sit — because that is what distinguishes the
/// two layouts. "An appearance was produced" is true of the unlaid-out version too.</para>
/// </summary>
public class FormFieldLayoutTests
{
    /// <summary>Every <c>Tj</c> in the appearance, with the absolute x its <c>Td</c> put it at.</summary>
    /// <remarks><c>Td</c> is relative to the previous text-line origin, so the offsets accumulate.</remarks>
    private static List<(double X, double Y)> ShownRuns(string content)
    {
        var runs = new List<(double, double)>();
        double x = 0, y = 0;

        foreach (Match m in Regex.Matches(content, @"(-?[\d.]+) (-?[\d.]+) Td|Tj"))
        {
            if (m.Groups[1].Success)
            {
                x += double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                y += double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                runs.Add((x, y));
            }
        }
        return runs;
    }

    private static string AppearanceContent(byte[] pdf, (string Name, string Value) value)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFiller.Fill(input, output, new Dictionary<string, string> { [value.Name] = value.Value },
            new FormFillOptions());

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var field = (PdfDictionary)doc.GetObject(6);
        var ap = doc.ResolveObject(field[PdfName.AP]) as PdfDictionary;
        var normal = doc.ResolveObject(ap![PdfName.N]) as PdfStream;
        return Encoding.Latin1.GetString(normal!.GetDecodedData());
    }

    // ================================================================
    //  Multiline wrapping
    // ================================================================

    private const string LongValue =
        "The quick brown fox jumps over the lazy dog and keeps running well past the margin";

    /// <summary>
    /// A multiline field wraps to the box width: the value becomes several positioned runs, each
    /// on its own baseline. Without wrapping it is one run that overflows the clip, and everything
    /// past the right edge is invisible.
    /// </summary>
    [Fact]
    public void MultilineField_WrapsALongValueOntoSeveralLines()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 600 260 720]",
            fieldEntries: $"/DA (/Helv 10 Tf 0 g) /Ff {PdfFieldFlags.Multiline}");

        var runs = ShownRuns(AppearanceContent(pdf, ("name", LongValue)));

        Assert.True(runs.Count >= 3, $"expected the value to wrap onto several lines, got {runs.Count}");

        // Each line sits below the one before it.
        for (int i = 1; i < runs.Count; i++)
            Assert.True(runs[i].Y < runs[i - 1].Y,
                $"line {i} should be below line {i - 1}: {runs[i].Y} vs {runs[i - 1].Y}");
    }

    /// <summary>
    /// The same value in a single-line field must <b>not</b> wrap — it scrolls in a viewer, and the
    /// auto-size shrinks it instead. This is the control that proves the previous test observes
    /// wrapping rather than just "several runs happen".
    /// </summary>
    [Fact]
    public void SingleLineField_DoesNotWrap()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 600 260 720]", fieldEntries: "/DA (/Helv 10 Tf 0 g)");

        var runs = ShownRuns(AppearanceContent(pdf, ("name", LongValue)));

        Assert.Single(runs);
    }

    /// <summary>
    /// Explicit newlines still start a new line, and now coexist with wrapping rather than being
    /// the only thing that breaks a line.
    /// </summary>
    [Fact]
    public void MultilineField_HonoursExplicitNewlines()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 600 400 720]",
            fieldEntries: $"/DA (/Helv 10 Tf 0 g) /Ff {PdfFieldFlags.Multiline}");

        var runs = ShownRuns(AppearanceContent(pdf, ("name", "alpha\nbeta\ngamma")));

        Assert.Equal(3, runs.Count);
    }

    // ================================================================
    //  Comb fields
    // ================================================================

    /// <summary>
    /// A comb field divides its box into <c>/MaxLen</c> equal cells with one character centred in
    /// each. The characters therefore do not advance naturally: each is its own positioned run,
    /// one cell width apart.
    /// </summary>
    /// <remarks>
    /// Digits, because every digit in Helvetica is 556/1000 wide. Each glyph is centred in its own
    /// cell, so it is the cell <i>centres</i> that are evenly spaced; with equal-width glyphs the
    /// left edges are too, which keeps the assertion an exact number rather than a re-derivation of
    /// the production formula. Ordinary (un-combed) text would advance by the glyph width — about
    /// 5.6pt here, not 20 — so this still fails outright if comb layout is not applied.
    /// </remarks>
    [Fact]
    public void CombField_PlacesOneCharacterPerEvenlySpacedCell()
    {
        const int cells = 5;
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 700 200 720]",                 // 100pt wide → 20pt cells
            fieldEntries: $"/DA (/Helv 10 Tf 0 g) /Ff {PdfFieldFlags.Comb} /MaxLen {cells}");

        var runs = ShownRuns(AppearanceContent(pdf, ("name", "12345")));

        Assert.Equal(cells, runs.Count);

        const double cellWidth = 100.0 / cells;
        for (int i = 1; i < runs.Count; i++)
            Assert.Equal(cellWidth, runs[i].X - runs[i - 1].X, precision: 2);
    }

    /// <summary>
    /// All cells share one baseline — a comb is a row, not a column.
    /// </summary>
    [Fact]
    public void CombField_KeepsEveryCellOnOneBaseline()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 700 200 720]",
            fieldEntries: $"/DA (/Helv 10 Tf 0 g) /Ff {PdfFieldFlags.Comb} /MaxLen 5");

        var runs = ShownRuns(AppearanceContent(pdf, ("name", "ABCDE")));

        Assert.All(runs, run => Assert.Equal(runs[0].Y, run.Y, precision: 3));
    }

    /// <summary>
    /// Without a <c>/MaxLen</c> there is no cell count, so there is no comb to lay out. The value
    /// is drawn as ordinary text — one run — and the caller is told why rather than left to wonder.
    /// </summary>
    [Fact]
    public void CombFieldWithoutMaxLen_FallsBackToOrdinaryTextAndWarns()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            fieldEntries: $"/DA (/Helv 10 Tf 0 g) /Ff {PdfFieldFlags.Comb}");

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfFormFiller.Fill(input, output,
            new Dictionary<string, string> { ["name"] = "ABCDE" }, new FormFillOptions());

        Assert.Contains(result.Warnings, w => w.Contains("/MaxLen", StringComparison.Ordinal));

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var field = (PdfDictionary)doc.GetObject(6);
        var ap = doc.ResolveObject(field[PdfName.AP]) as PdfDictionary;
        var normal = doc.ResolveObject(ap![PdfName.N]) as PdfStream;

        Assert.Single(ShownRuns(Encoding.Latin1.GetString(normal!.GetDecodedData())));
    }

    /// <summary>
    /// §12.7.4.3: comb is ignored on a multiline field. Multiline wins, so the value wraps rather
    /// than being combed — the two layouts are mutually exclusive and the flag combination is legal.
    /// </summary>
    [Fact]
    public void CombFlagOnAMultilineField_IsIgnored()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 600 260 720]",
            fieldEntries: $"/DA (/Helv 10 Tf 0 g) " +
                          $"/Ff {PdfFieldFlags.Comb | PdfFieldFlags.Multiline} /MaxLen 5");

        var runs = ShownRuns(AppearanceContent(pdf, ("name", LongValue)));

        // Combed would be exactly 5 runs on one baseline; wrapped is several runs on descending
        // baselines.
        Assert.True(runs.Count >= 3);
        Assert.True(runs[1].Y < runs[0].Y, "a multiline field should stack its lines");
    }
}
