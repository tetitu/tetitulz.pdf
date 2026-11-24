// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Adversarial-input guards for <see cref="CMapParser"/> range parsing.
///
/// <para>
/// <b>The hazard.</b> A range-length clamp of <c>if (endCode - startCode &gt; MaxRangeLength)</c>
/// in signed <c>int</c> arithmetic, in <c>ParseBfRange</c> or <c>ParseCidRangeReverse</c>, lets a
/// crafted range such as <c>&lt;FFFFFFFF&gt; &lt;7FFFFFFF&gt;</c> wrap that difference negative, so
/// the clamp never fires and the following loop runs for roughly four billion iterations.
/// <c>PdfFont</c> feeds raw <c>/ToUnicode</c> stream bytes straight into this parser, so a single
/// crafted font would hang the calling thread — unauthenticated, from nothing but opening a PDF.
/// </para>
/// <para>
/// Every test here is time-bounded rather than merely asserting a result: an unguarded parser
/// never returns, so an unbounded assertion would hang the whole test run instead of failing.
/// </para>
/// </summary>
public class CMapParserAdversarialTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="parse"/> under a wall-clock budget; fails if it overruns.
    ///
    /// <para>
    /// <b>Why a dedicated thread and not <c>Task.Run</c>.</b> A pooled work item is not guaranteed
    /// to <i>start</i> promptly: the budget then covers queue latency as well as the parse, and the
    /// thread pool grows by roughly one thread per 500 ms once its threads are all busy. Running the
    /// whole solution starts fourteen test hosts at once, each saturating its own pool, and this
    /// test — which needs about 12 ms of CPU — overran the 5 s budget purely by waiting to be
    /// scheduled, reporting a hang in a parser that is fine. A dedicated thread is scheduled by the
    /// OS and never queues behind another work item, so what is measured is the parse.
    /// </para>
    ///
    /// <para>
    /// The guard itself is unchanged and just as strict: a parse that really does run to
    /// <c>int.MaxValue</c> still blows the same 5 s budget and still fails with the same message.
    /// The thread is left running rather than aborted (.NET has no <c>Thread.Abort</c>) and is a
    /// background thread so it cannot keep the test host alive.
    /// </para>
    /// </summary>
    private static T WithinBudget<T>(Func<T> parse, string what)
    {
        T result = default;
        Exception failure = null;

        var worker = new Thread(() =>
        {
            try { result = parse(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "CMapParser budget probe: " + what,
        };
        worker.Start();

        Assert.True(worker.Join(Budget),
            $"{what} did not complete within {Budget.TotalSeconds}s — the range clamp was defeated " +
            "by integer overflow and the loop is running to int.MaxValue.");

        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // ---------------------------------------------------------------- bfrange

    [Fact]
    public void ParseToUnicode_BfRangeWithOverflowingEndpoints_TerminatesAndIsBounded()
    {
        byte[] cmap = Ascii(
            "/CIDInit /ProcSet findresource begin\n" +
            "1 begincmap\n" +
            "beginbfrange\n" +
            "<FFFFFFFF> <7FFFFFFF> <0041>\n" +
            "endbfrange\n" +
            "endcmap\n");

        var map = WithinBudget(() => CMapParser.Parse(cmap), "bfrange (single-unit destination)");

        Assert.NotNull(map);
        Assert.True(map.Count <= 0x10001,
            $"Range produced {map.Count} entries; the 0x10000 cap must hold even on overflowing input.");
    }

    /// <summary>
    /// The multi-char-destination branch is a second, identically-shaped loop that the original
    /// finding did not cite. A 6-hex-digit destination drives it.
    /// </summary>
    [Fact]
    public void ParseToUnicode_BfRangeMultiCharDestination_TerminatesAndIsBounded()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "beginbfrange\n" +
            "<FFFFFFFF> <7FFFFFFF> <004100420043>\n" +
            "endbfrange\n" +
            "endcmap\n");

        var map = WithinBudget(() => CMapParser.Parse(cmap), "bfrange (multi-char destination)");

        Assert.NotNull(map);
        Assert.True(map.Count <= 0x10001, $"Range produced {map.Count} entries.");
    }

    /// <summary>A well-formed range must still map exactly what it says.</summary>
    [Fact]
    public void ParseToUnicode_WellFormedBfRange_StillMapsCorrectly()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "beginbfrange\n" +
            "<0003> <0005> <0041>\n" +
            "endbfrange\n" +
            "endcmap\n");

        var map = CMapParser.Parse(cmap);

        Assert.Equal("A", map[0x03]);
        Assert.Equal("B", map[0x04]);
        Assert.Equal("C", map[0x05]);
        Assert.False(map.ContainsKey(0x06));
    }

    // ---------------------------------------------------------------- bfrange array form
    //
    // <lo> <hi> [<d1> <d2> ...] — one destination per code in the range, used instead of the
    // arithmetic <lo> <hi> <dst> form whenever the destinations are not a contiguous Unicode
    // run (exactly the case for a subsetted font: glyph order reflects first-use order in the
    // document, not Unicode order).

    /// <summary>
    /// The array form on a single physical line — covered here so the multi-line case below cannot
    /// hide behind it.
    /// </summary>
    [Fact]
    public void ParseToUnicode_BfRangeArrayForm_SingleLine_MapsCorrectly()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "beginbfrange\n" +
            "<0000> <0002> [<0041> <0042> <0043>]\n" +
            "endbfrange\n" +
            "endcmap\n");

        var map = CMapParser.Parse(cmap);

        Assert.Equal("A", map[0]);
        Assert.Equal("B", map[1]);
        Assert.Equal("C", map[2]);
    }

    /// <summary>
    /// <b>The bug.</b> A real-world CMap generator (confirmed byte-for-byte against a Cal
    /// credit-card statement PDF's embedded, well-formed <c>/ToUnicode</c> stream) writes one
    /// array element per line:
    /// <code>
    /// beginbfrange
    /// &lt;0000&gt; &lt;00ff&gt; [
    /// &lt;fffd&gt;
    /// &lt;05dc&gt;
    /// &lt;05d0&gt;
    /// ...
    /// ]
    /// endbfrange
    /// </code>
    /// The previous implementation split the bfrange section into lines and inspected each line
    /// independently for a <c>[</c>/<c>]</c> pair. The line holding the opening bracket
    /// (<c>"&lt;0000&gt; &lt;00ff&gt; ["</c>) has no closing bracket on it, so
    /// <c>line.IndexOf(']')</c> returned -1 and the array handler returned immediately —
    /// mapping <b>nothing</b> for the whole 256-entry range. Every character drawn with those
    /// codes then had no ToUnicode entry, and downstream text/table extraction fell back to raw
    /// character codes: a well-formed Hebrew CMap produced garbled non-Hebrew output. This is
    /// the actual real-world symptom that led to this fix.
    /// </summary>
    [Fact]
    public void ParseToUnicode_BfRangeArrayForm_MultiLine_MapsCorrectly()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "beginbfrange\n" +
            "<0004> <0006> [\n" +
            "<05dc>\n" +
            "<05d0>\n" +
            "<05db>\n" +
            "]\n" +
            "endbfrange\n" +
            "endcmap\n");

        var map = CMapParser.Parse(cmap);

        Assert.Equal("ל", map[0x04]); // ל
        Assert.Equal("א", map[0x05]); // א
        Assert.Equal("כ", map[0x06]); // כ
    }

    /// <summary>A multi-line array immediately followed by another bfrange entry must not swallow it.</summary>
    [Fact]
    public void ParseToUnicode_BfRangeArrayForm_MultiLine_DoesNotConsumeFollowingEntry()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "beginbfrange\n" +
            "<0000> <0001> [\n" +
            "<0041>\n" +
            "<0042>\n" +
            "]\n" +
            "<0010> <0010> <0043>\n" +
            "endbfrange\n" +
            "endcmap\n");

        var map = CMapParser.Parse(cmap);

        Assert.Equal("A", map[0x00]);
        Assert.Equal("B", map[0x01]);
        Assert.Equal("C", map[0x10]);
    }

    /// <summary>A missing closing bracket is malformed input, not a reason to scan past this
    /// bfrange section — it must terminate quickly rather than absorb the rest of the CMap.</summary>
    [Fact]
    public void ParseToUnicode_BfRangeArrayForm_MissingClosingBracket_TerminatesAndIsBounded()
    {
        var sb = new StringBuilder();
        sb.Append("1 begincmap\nbeginbfrange\n<0000> <FFFF> [\n");
        for (int i = 0; i < 1000; i++) sb.Append("<0041>\n");
        sb.Append("endbfrange\nendcmap\n"); // no ']' anywhere — array content is clamped at 'endbfrange'

        var map = WithinBudget(() => CMapParser.Parse(Ascii(sb.ToString())),
            "bfrange array (missing closing bracket)");

        Assert.NotNull(map);
        Assert.True(map.Count <= 1000, $"Expected at most the 1000 supplied elements, got {map.Count}.");
    }

    /// <summary>A start code outside the Unicode range is rejected, not clamped into the map.</summary>
    [Fact]
    public void ParseToUnicode_BfRangeWithOutOfRangeStart_ProducesNoEntries()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "beginbfrange\n" +
            "<7FFFFFFF> <7FFFFFFF> <0041>\n" +
            "endbfrange\n" +
            "endcmap\n");

        var map = WithinBudget(() => CMapParser.Parse(cmap), "bfrange (out-of-range start)");

        Assert.NotNull(map);
        Assert.Empty(map);
    }

    // ---------------------------------------------------------------- cidrange

    [Fact]
    public void ParseCidRangeReverse_WithOverflowingEndpoints_TerminatesAndIsBounded()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "begincidrange\n" +
            "<FFFFFFFF> <7FFFFFFF> 1\n" +
            "endcidrange\n" +
            "endcmap\n");

        var map = WithinBudget(() => CMapParser.Parse(cmap), "cidrange");

        Assert.NotNull(map);
        Assert.True(map.Count <= 0x10001, $"Range produced {map.Count} entries.");
    }

    [Fact]
    public void ParseCidRangeReverse_WellFormedRange_StillMapsCorrectly()
    {
        byte[] cmap = Ascii(
            "1 begincmap\n" +
            "begincidrange\n" +
            "<0041> <0043> 10\n" +
            "endcidrange\n" +
            "endcmap\n");

        var map = CMapParser.Parse(cmap);

        Assert.Equal("A", map[10]);
        Assert.Equal("B", map[11]);
        Assert.Equal("C", map[12]);
    }
}
