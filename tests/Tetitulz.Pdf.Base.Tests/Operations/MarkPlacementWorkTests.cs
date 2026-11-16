// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A combining mark is placed on its letter through an index of the letters' spans: whether a mark
/// lies over a letter is a binary search, and the nearest in height among the letters it lies over
/// is looked for among at most <see cref="PdfStructuredTextExtractor.MaxLettersPerMark"/>. Each mark
/// scanned every letter on the page, twice — marks × letters, with no step counted.
/// </summary>
public sealed class MarkPlacementWorkTests
{
    private const char Acute = (char)0x0301;

    /// <summary>
    /// Narrow letters in the gaps of one wide letter, and a mark in each gap: only the wide letter
    /// holds a mark, and the search for a nearer one walks the narrow letters back towards it. Each
    /// mark is still placed on the wide letter, within its bound.
    /// </summary>
    [Fact]
    public void AdversarialMarksInTheGapsOfOneWideLetter_AreBoundedAndPlaced()
    {
        const int count = 20_000;
        var page = new List<PdfTextChar> { new PdfTextChar("W", 0, 700, 3f * count + 10, 10, false) };
        for (int k = 1; k <= count; k++) page.Add(new PdfTextChar("a", 3f * k, 700, 0.1f, 10, false));
        for (int k = 1; k <= count; k++) page.Add(new PdfTextChar(Acute.ToString(), 3f * k + 1.5f, 708, 0, 3, false));

        var lines = PdfStructuredTextExtractor.BuildLines(page);

        long bound = (long)count * (PdfStructuredTextExtractor.MaxLettersPerMark + 1) + 20L * page.Count;
        Assert.InRange(PdfStructuredTextExtractor.LastLineGroupingWork, 1, bound);
        string text = string.Concat(lines.Select(l => l.Text));
        Assert.Contains("W" + new string(Acute, count), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: of two letters a mark lies over, it goes to the nearer in height; and of two at
    /// the same height, to the one drawn first — what the scan of every letter chose.
    /// </summary>
    [Fact]
    public void AMarkGoesToTheNearerLetter_TheEarlierDrawnOnATie()
    {
        var page = new List<PdfTextChar>
        {
            new PdfTextChar("b", 100, 700, 6, 10, false),
            new PdfTextChar("c", 100, 600, 6, 10, false),
            new PdfTextChar(Acute.ToString(), 102, 602, 0, 3, false),
        };
        var lines = PdfStructuredTextExtractor.BuildLines(page);
        Assert.Contains(lines, l => l.Text == "c" + Acute);

        var tie = new List<PdfTextChar>
        {
            new PdfTextChar("d", 100, 700, 6, 10, false),
            new PdfTextChar("e", 101, 700, 6, 10, false),
            new PdfTextChar(Acute.ToString(), 103, 705, 0, 3, false),
        };
        string tied = string.Concat(PdfStructuredTextExtractor.BuildLines(tie).Select(l => l.Text));
        Assert.Contains("d" + Acute, tied, StringComparison.Ordinal);
    }
}
