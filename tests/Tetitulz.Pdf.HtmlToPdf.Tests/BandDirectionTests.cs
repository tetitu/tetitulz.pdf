// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Column order for the four constructs that map onto a side-by-side band — flex row, float band,
/// grid and multi-column — must follow the writing direction, and must follow it the same way a
/// real <c>&lt;table&gt;</c> does.
/// </summary>
/// <remarks>
/// <b>The assertion is on measured glyph positions</b>, read back from the produced PDF: which of
/// two items ends up on the left. Nothing structural can see this — the elements, the text and the
/// table are all present and correct either way, and only the ORDER of the columns changes.
///
/// <para><b>The defect pinned here was invisible to the existing suite:</b> the float band took its
/// direction from the FIRST FLOATED CHILD rather than from its container, so a declaration on one
/// float decided the order of the whole band.</para>
///
/// <para>A second suspected defect was REFUTED and is recorded rather than acted on. The four band
/// mappers omitted the section-direction fallback that a real <c>&lt;table&gt;</c> carried, which
/// reads like a document that mirrors its tables and not its flex rows — but the root style is
/// seeded from <c>DefaultTextDirection</c> and every derived style copies it, so the direction is
/// never null and the fallback never fires. Removing it from the shared helper reddens nothing.</para>
/// </remarks>
public class BandDirectionTests
{
    /// <summary>
    /// Latin content is used deliberately. CLAUDE.md warns that pure single-script text cannot
    /// detect a base-direction bug in RUN order — one run reverses as a unit — but that warning is
    /// about text; here the observable is which COLUMN a box lands in, which the script does not
    /// affect. Using Latin keeps the two items unambiguously identifiable in the extracted words.
    /// </summary>
    private const string TwoItems = "<div>AAA</div><div>BBB</div>";

    // The flex row is gapped: since batch 121 its auto items keep their content widths, so two
    // gapless items print as one word and extraction cannot tell them apart.
    public static IEnumerable<object[]> BandStyles => new[]
    {
        new object[] { "display:flex;column-gap:10px" },
        new object[] { "display:grid;grid-template-columns:1fr 1fr" },
    };

    [Theory]
    [MemberData(nameof(BandStyles))]
    public void ABandInsideAnRtlContainerPutsTheFirstItemOnTheRight(string style)
    {
        var (a, b) = Positions($"<div dir='rtl'><div style='{style}'>{TwoItems}</div></div>");

        Assert.True(a.X > b.X,
            $"AAA is at x={a.X:F1} and BBB at x={b.X:F1}; the RTL container did not mirror the band");
    }

    /// <summary>
    /// The LTR control for the same fixture. Without it, a band that mirrored unconditionally —
    /// or one whose items simply landed in a fixed order — would satisfy every case above.
    /// </summary>
    [Theory]
    [MemberData(nameof(BandStyles))]
    public void TheSameBandInsideAnLtrContainerPutsTheFirstItemOnTheLeft(string style)
    {
        var (a, b) = Positions($"<div dir='ltr'><div style='{style}'>{TwoItems}</div></div>");

        Assert.True(a.X < b.X,
            $"AAA is at x={a.X:F1} and BBB at x={b.X:F1}; an LTR band should not be mirrored");
    }

    /// <summary>
    /// A float band takes its column order from its CONTAINER, not from whatever the first floated
    /// child happens to declare.
    /// </summary>
    /// <remarks>
    /// It read <c>DeriveStyle(inherited, first, …)</c>, so <c>direction:ltr</c> on the first float
    /// un-mirrored the entire band of an RTL container — and the same wrong base was what every
    /// other float inherited its style from. <c>TryMapFloatBand</c> has no container element in
    /// scope at all, so the container's already-derived <c>inherited</c> style is the correct base
    /// and <c>first</c> was simply the wrong variable within reach.
    ///
    /// <para><b>The assertion is on the BESIDE-content, not on the float.</b> A float that declares
    /// <c>direction:ltr</c> legitimately left-aligns its own text inside its cell, so the float's
    /// own x moves for a correct reason and cannot separate the two cases — the first version of
    /// this test compared exactly that and failed against a correct fix. The beside-content's style
    /// is derived from the container either way, so its position depends only on which column it is
    /// in, which is the thing under test.</para>
    /// </remarks>
    [Fact]
    public void AFloatBandTakesItsColumnOrderFromTheContainerNotTheFloat()
    {
        // float:left puts the float in the FIRST column; an RTL container mirrors the columns, so
        // the beside-content moves right. That must hold whatever the float itself declares.
        double plain    = BesideX("<div dir='rtl'><p style='float:left;width:200px'>AAA</p><p>BBB</p></div>");
        double declared = BesideX("<div dir='rtl'><p style='float:left;width:200px;direction:ltr'>AAA</p><p>BBB</p></div>");
        double ltr      = BesideX("<div dir='ltr'><p style='float:left;width:200px'>AAA</p><p>BBB</p></div>");

        Assert.True(Math.Abs(plain - declared) < 2.0,
            $"the float's own direction re-ordered the band: beside-content at x={plain:F1} without " +
            $"it and x={declared:F1} with it. The container decides the column order.");

        // The control: an LTR container really does put it somewhere else, so the equality above
        // is not simply "the band never moves".
        Assert.True(plain - ltr > 50.0,
            $"an RTL container should move the beside-content right; got x={plain:F1} against x={ltr:F1} for LTR");
    }

    /// <summary>
    /// <c>float:left</c> and <c>float:right</c> are PHYSICAL: a run of floats puts its first float
    /// at the edge it names, and the run hugs that edge, in either writing direction.
    /// </summary>
    /// <remarks>
    /// <para>A band's cells are in reading order and an RTL band is mirrored, so "which edge" is a
    /// function of both the side and the direction. Bootstrap-RTL floats its columns right inside
    /// <c>dir="rtl"</c>; a run that followed the mirror blindly would put the first column on the
    /// left.</para>
    /// <para>Both halves are asserted. The order alone passes with the floats on the wrong edge and
    /// the empty remainder on theirs; the edge alone passes with the floats in the wrong order.</para>
    /// </remarks>
    [Theory]
    [InlineData("ltr", "left")]
    [InlineData("ltr", "right")]
    [InlineData("rtl", "left")]
    [InlineData("rtl", "right")]
    public void ARunOfFloatsStartsAtTheEdgeItNames(string dir, string side)
    {
        var (a, b) = Positions(
            $"<div dir='{dir}'><div style='float:{side};width:30%'>AAA</div>" +
            $"<div style='float:{side};width:30%'>BBB</div></div>");

        const double Middle = 306;   // the middle of the default 612pt page
        if (side == "left")
        {
            Assert.True(a.X < b.X, $"float:left in {dir}: AAA at x={a.X:F1} is not left of BBB at x={b.X:F1}");
            Assert.True(b.X < Middle + 100, $"float:left in {dir}: the run is not at the left edge (BBB at x={b.X:F1})");
        }
        else
        {
            Assert.True(a.X > b.X, $"float:right in {dir}: AAA at x={a.X:F1} is not right of BBB at x={b.X:F1}");
            Assert.True(b.X > Middle - 100, $"float:right in {dir}: the run is not at the right edge (BBB at x={b.X:F1})");
        }
    }

    /// <summary>
    /// The same rule for a float band — a float that cannot be a real float (several paragraphs)
    /// beside content: <c>float:left</c> stays on the left in a right-to-left container.
    /// </summary>
    /// <remarks>
    /// A band laying its float first in reading order whatever the side, and letting the RTL mirror
    /// move it, would land a left float in an RTL container on the right.
    /// </remarks>
    [Theory]
    [InlineData("ltr", "left")]
    [InlineData("ltr", "right")]
    [InlineData("rtl", "left")]
    [InlineData("rtl", "right")]
    public void AFloatBandPutsTheFloatOnTheSideItNames(string dir, string side)
    {
        var (a, b) = Positions(
            $"<div dir='{dir}'><div style='float:{side};width:100pt'><p>AAA</p><p>AA2</p></div>" +
            "<div>BBB</div></div>");

        if (side == "left")
            Assert.True(a.X < b.X, $"float:left in {dir}: the float (x={a.X:F1}) is not left of the content (x={b.X:F1})");
        else
            Assert.True(a.X > b.X, $"float:right in {dir}: the float (x={a.X:F1}) is not right of the content (x={b.X:F1})");
    }

    /// <summary>
    /// Two <c>float:right</c> elements in a band: the FIRST is the rightmost, and the second lines up
    /// to its left — laid in document order after the content, the first one would sit inward.
    /// </summary>
    [Fact]
    public void AFloatBandPutsTheFirstRightFloatAtTheRightEdge()
    {
        var (a, b) = Positions(
            "<div><div style='float:right;width:100pt'><p>AAA</p><p>AA2</p></div>" +
            "<div style='float:right;width:100pt'><p>BBB</p><p>BB2</p></div>" +
            "<div>CCC</div></div>");

        Assert.True(a.X > b.X, $"the first right float (x={a.X:F1}) is not right of the second (x={b.X:F1})");
    }

    /// <summary>The x of the content beside the float — the cell whose style the float cannot touch.</summary>
    private static double BesideX(string html)
    {
        var word = Words(html).FirstOrDefault(w => w.Text.Contains("BBB", StringComparison.Ordinal));
        Assert.True(word != null, "the beside-content was not laid out at all");
        return word!.X;
    }

    // ------------------------------------------------------------------

    private static ((double X, double Y) A, (double X, double Y) B) Positions(string html)
    {
        var words = Words(html);
        var a = words.FirstOrDefault(w => w.Text.Contains("AAA", StringComparison.Ordinal));
        var b = words.FirstOrDefault(w => w.Text.Contains("BBB", StringComparison.Ordinal));

        Assert.True(a != null, "AAA was not laid out at all");
        Assert.True(b != null, "BBB was not laid out at all");

        return ((a!.X, a.Y), (b!.X, b.Y));
    }

    private static List<PdfTextWord> Words(string html)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output,
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        using var input = new MemoryStream(output.ToArray(), writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
