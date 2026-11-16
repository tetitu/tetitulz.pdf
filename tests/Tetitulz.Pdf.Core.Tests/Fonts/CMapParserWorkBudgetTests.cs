// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Adversarial-input guard for <see cref="CMapParser"/>'s aggregate work budget.
///
/// <para>The 0x10000 clamp bounds ONE <c>bfrange</c> line. The number of lines is bounded only
/// by the decoded stream length, and the two multiply: a <c>/ToUnicode</c> holding a single
/// <c>beginbfrange</c> section of 50 000 identical <c>&lt;0000&gt; &lt;FFFF&gt; &lt;0041&gt;</c>
/// lines is ~1 MB of extremely repetitive text — a couple of kilobytes once Flate-compressed —
/// and expands to ~3.3 billion iterations with a string allocation each. The dictionary stays
/// small because the keys collide, so no <c>Count</c>-based guard can ever fire.
/// <c>PdfFont.Create</c> feeds raw <c>/ToUnicode</c> bytes straight in with no
/// <c>try</c>/<c>catch</c>, so the calling thread was pinned indefinitely by nothing more than
/// opening the file.</para>
///
/// <para>These assert the guard's own evidence — a reported refusal and a bounded result — never
/// a wall clock.</para>
/// </summary>
public class CMapParserWorkBudgetTests
{
    [Fact]
    public void Parse_UnboundedBfRangeLineCount_ReportsTheRefusalInsteadOfRunningForever()
    {
        byte[] cmap = RepeatedBfRangeLines(200);

        var mapping = CMapParser.Parse(cmap, out _);

        Assert.NotNull(CMapParser.LastBudgetError);
        Assert.Contains("budget", CMapParser.LastBudgetError!, System.StringComparison.OrdinalIgnoreCase);
        // Bounded result: the parse stopped, so it cannot have written more entries than the
        // 16-bit code space a single line covers.
        Assert.InRange(mapping.Count, 1, 0x10001);
    }

    [Fact]
    public void Parse_UnboundedCidRangeLineCount_ReportsTheRefusal()
    {
        // The reverse-CID path has the same shape and its own copy of the per-line clamp.
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\nbegincidrange\n");
        for (int i = 0; i < 200; i++) sb.Append("<0000> <FFFF> 1\n");
        sb.Append("endcidrange\nend\n");

        CMapParser.Parse(Encoding.ASCII.GetBytes(sb.ToString()), out _);

        Assert.NotNull(CMapParser.LastBudgetError);
    }

    [Fact]
    public void LastBudgetError_IsClearedByASubsequentWellFormedParse()
    {
        // The slot must be reset at the START of the operation that writes it: a caller reading
        // it after parsing a perfectly ordinary font must not be handed the previous font's
        // refusal. Run the adversarial parse first so there is something to fail to clear.
        CMapParser.Parse(RepeatedBfRangeLines(200), out _);
        Assert.NotNull(CMapParser.LastBudgetError);

        const string wellFormed =
            "/CIDInit /ProcSet findresource begin\n" +
            "beginbfchar\n<0041> <0061>\nendbfchar\n" +
            "beginbfrange\n<0030> <0039> <0030>\nendbfrange\nend\n";
        var mapping = CMapParser.Parse(Encoding.ASCII.GetBytes(wellFormed), out _);

        Assert.Null(CMapParser.LastBudgetError);
        Assert.Equal("a", mapping[0x41]);
        Assert.Equal("5", mapping[0x35]);
    }

    /// <summary>
    /// One <c>beginbfrange</c> section whose lines each declare the whole 16-bit code space.
    /// 200 × 65 537 ≈ 13.1 M mapping writes, comfortably past
    /// <c>PdfLimits.MaxCMapMappingWrites</c>, from ~5 KB of text.
    /// </summary>
    private static byte[] RepeatedBfRangeLines(int lineCount)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\nbeginbfrange\n");
        for (int i = 0; i < lineCount; i++) sb.Append("<0000> <FFFF> <0041>\n");
        sb.Append("endbfrange\nend\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
