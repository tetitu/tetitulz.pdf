// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Adversarial-input guard for the CIDFont <c>/W</c> width table (§9.7.4.3), the sibling of
/// <see cref="CMapParserWorkBudgetTests"/> and bounded the same way.
///
/// <para>The per-range 0x10000 clamp bounds ONE <c>c1 c2 w</c> triple. The number of triples is
/// bounded only by the array length, and the two multiply: a <c>/W</c> of 200 identical
/// <c>0 65535 1000</c> triples is a few kilobytes of source — a couple of hundred bytes once
/// Flate-compressed — and asks for ~13 M dictionary writes. The dictionary itself stays small
/// because every triple writes the same keys, so no <c>Count</c>-based guard can ever fire, and
/// <c>PdfFont.Create</c> builds the font with no <c>try</c>/<c>catch</c>, so the calling thread
/// was pinned by nothing more than opening the file.</para>
///
/// <para>These assert the guard's own evidence — a bounded dictionary and a stated reason — never
/// a wall clock.</para>
/// </summary>
public class CidWidthWorkBudgetTests
{
    [Fact]
    public void ParseWArray_UnboundedRangeCount_ReportsTheRefusalInsteadOfRunningForever()
    {
        var widths = new Dictionary<int, double>();

        PdfType0Font.ParseWArray(RepeatedFullSpanRanges(200), widths);

        Assert.NotNull(PdfType0Font.LastWidthBudgetError);
        Assert.Contains("budget", PdfType0Font.LastWidthBudgetError!,
            System.StringComparison.OrdinalIgnoreCase);
        // Bounded result: the parse stopped, so it cannot hold more than the CID window one
        // clamped range covers.
        Assert.InRange(widths.Count, 1, 0x10001);
    }

    /// <summary>
    /// The budget is <b>aggregate across both forms</b>. Format 1 (<c>c [w1 w2 …]</c>) cannot
    /// amplify on its own — every width it writes costs an array element — but it shares the
    /// allowance, so its loop has to be charged too or a mixed array escapes the bound through it.
    /// This pins that charge exactly: the ranges consume the budget to the byte, so the single
    /// format-1 width that follows is the tick that overruns it. Delete
    /// <c>budget.Tick()</c> from the format-1 loop and this goes green-with-no-error, which is why
    /// the control below asserts the same array minus that one entry runs clean.
    /// </summary>
    [Fact]
    public void ParseWArray_IndividualWidthsShareTheAggregateBudget()
    {
        const int perRange = 0x10000; // a full-span 0 65535 range writes exactly 65 536 entries
        int exactlyTheBudget = (int)(PdfLimits.MaxCidWidthEntries / perRange);

        var control = RepeatedFullSpanRanges(exactlyTheBudget);
        PdfType0Font.ParseWArray(control, new Dictionary<int, double>());
        Assert.Null(PdfType0Font.LastWidthBudgetError);

        var w = RepeatedFullSpanRanges(exactlyTheBudget);
        w.Add(new PdfNumber(70000));
        var sub = new PdfArray();
        sub.Add(new PdfNumber(1000));
        w.Add(sub);

        var widths = new Dictionary<int, double>();
        PdfType0Font.ParseWArray(w, widths);

        Assert.NotNull(PdfType0Font.LastWidthBudgetError);
        // The refused entry is absent; everything the ranges wrote survives.
        Assert.False(widths.ContainsKey(70000));
        Assert.Equal(perRange, widths.Count);
    }

    [Fact]
    public void LastWidthBudgetError_IsClearedByASubsequentWellFormedParse()
    {
        // The slot must be reset at the START of the operation that writes it: a caller reading it
        // after building a perfectly ordinary font must not be handed the previous font's refusal.
        PdfType0Font.ParseWArray(RepeatedFullSpanRanges(200), new Dictionary<int, double>());
        Assert.NotNull(PdfType0Font.LastWidthBudgetError);

        // 1 [500 600] 10 12 750 — one of each form, both well within the budget.
        var w = new PdfArray();
        w.Add(new PdfNumber(1));
        var sub = new PdfArray();
        sub.Add(new PdfNumber(500));
        sub.Add(new PdfNumber(600));
        w.Add(sub);
        w.Add(new PdfNumber(10));
        w.Add(new PdfNumber(12));
        w.Add(new PdfNumber(750));

        var widths = new Dictionary<int, double>();
        PdfType0Font.ParseWArray(w, widths);

        Assert.Null(PdfType0Font.LastWidthBudgetError);
        Assert.Equal(500, widths[1]);
        Assert.Equal(600, widths[2]);
        Assert.Equal(750, widths[10]);
        Assert.Equal(750, widths[11]);
        Assert.Equal(750, widths[12]);
        Assert.Equal(5, widths.Count);
    }

    /// <summary>
    /// A <c>/W</c> array of <paramref name="rangeCount"/> identical <c>0 65535 1000</c> triples:
    /// 200 × 65 536 ≈ 13.1 M requested writes, comfortably past
    /// <see cref="PdfLimits.MaxCidWidthEntries"/>, from a few kilobytes of source.
    /// </summary>
    private static PdfArray RepeatedFullSpanRanges(int rangeCount)
    {
        var w = new PdfArray();
        for (int i = 0; i < rangeCount; i++)
        {
            w.Add(new PdfNumber(0));
            w.Add(new PdfNumber(0xFFFF));
            w.Add(new PdfNumber(1000));
        }
        return w;
    }
}
