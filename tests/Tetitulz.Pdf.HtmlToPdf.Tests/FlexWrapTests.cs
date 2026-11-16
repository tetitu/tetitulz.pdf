// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>flex-wrap</c> in the managed engine (CSS Flexbox §9.3, batch 92): the items are collected
/// into lines on their hypothetical main sizes — a stated width or basis, else a measured
/// paragraph's width — one band table per line, <c>wrap-reverse</c> stacking the lines from the
/// bottom, <c>row-gap</c> between them, and <c>justify-content</c> and the growers resolved per
/// line. Before batch 92 every wrapping container was laid out on one line and reported.
/// </summary>
/// <remarks>
/// <para><b>Measured glyph positions, as every flex test here</b>: a wrapped line is a second row of
/// text under the first, and nothing structural tells a wrapped row from a stretched one. The one
/// case still laid out on one line — an item whose size cannot be known — keeps its report, and the
/// report names the item.</para>
/// </remarks>
public class FlexWrapTests
{
    private const string Half = "<div style='width:50%'>";

    /// <summary>
    /// Three half-width items under <c>flex-wrap: wrap</c>: the third sits under the first, at its
    /// x, and nothing is reported; under <c>nowrap</c> (and with nothing said) the three share the
    /// line, as before.
    /// </summary>
    [Theory]
    [InlineData("flex-wrap:wrap")]
    [InlineData("flex-flow:row wrap")]
    public void ThreeHalfWidthItems_WrapTheThirdUnderTheFirst(string declaration)
    {
        var wrapped = Lay($"<div style='display:flex;{declaration}'>{Half}AAA</div>{Half}BBB</div>{Half}CCC</div></div>");
        var nowrap = Lay($"<div style='display:flex;flex-wrap:nowrap'>{Half}AAA</div>{Half}BBB</div>{Half}CCC</div></div>");

        Assert.True(Math.Abs(wrapped["AAA"].Y - wrapped["BBB"].Y) < 2.0, "the first two items did not share the first line");
        Assert.True(wrapped["CCC"].Y < wrapped["AAA"].Y - 2.0, $"CCC at y={wrapped["CCC"].Y:F1} is not below AAA at y={wrapped["AAA"].Y:F1}");
        Assert.Equal(wrapped["AAA"].X, wrapped["CCC"].X, 1);
        Assert.Empty(wrapped.Reports);

        Assert.True(Math.Abs(nowrap["AAA"].Y - nowrap["CCC"].Y) < 2.0, "nowrap broke the line");
        Assert.Empty(nowrap.Reports);
    }

    /// <summary><c>wrap-reverse</c> (§5.2) stacks the lines from the bottom: the third item's line is above the first's.</summary>
    [Fact]
    public void WrapReverse_StacksTheLinesFromTheBottom()
    {
        var laid = Lay($"<div style='display:flex;flex-wrap:wrap-reverse'>{Half}AAA</div>{Half}BBB</div>{Half}CCC</div></div>");

        Assert.True(laid["CCC"].Y > laid["AAA"].Y + 2.0, $"CCC at y={laid["CCC"].Y:F1} is not above AAA at y={laid["AAA"].Y:F1}");
        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "the first two items did not share a line");
        Assert.Equal(laid["AAA"].X, laid["CCC"].X, 1);
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// The <c>flex-flow</c> shorthand sets both: <c>row-reverse wrap</c> puts the first item at the
    /// right and the third, alone on the second line, at the row's main-start — the right too.
    /// </summary>
    [Fact]
    public void FlexFlow_SetsDirectionAndWrap_AReversedLineStartsAtTheRight()
    {
        var laid = Lay($"<div style='display:flex;flex-flow:row-reverse wrap'>{Half}AAA</div>{Half}BBB</div>{Half}CCC</div></div>");

        Assert.True(laid["AAA"].X > laid["BBB"].X, "row-reverse did not put the first item on the right");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, "the third item did not wrap");
        Assert.Equal(laid["AAA"].X, laid["CCC"].X, 1);
    }

    /// <summary>
    /// The one reading of the direction serves the item's width too: under <c>flex-flow: column</c>,
    /// as under <c>flex-direction: column</c>, a 50% item's box takes its width (its rule is half the
    /// full-width reference rule's), where a row item's width is its track and not taken again.
    /// </summary>
    [Theory]
    [InlineData("flex-flow:column")]
    [InlineData("flex-direction:column")]
    public void FlexFlow_Column_ItemsTakeTheirOwnWidth(string declaration)
    {
        var laid = Lay($"<div style='border-top:1px solid #000'>REF</div><div style='display:flex;{declaration}'><div style='width:50%;border-top:1px solid #000'>AAA</div></div>");

        Assert.Equal(2, laid.Rules.Count);
        Assert.Equal(0.5 * laid.Rules.Max(), laid.Rules.Min(), 0);
    }

    /// <summary><c>row-gap</c> — or the first value of <c>gap</c> — opens 20px (15pt) between the lines.</summary>
    [Theory]
    [InlineData("row-gap:20px")]
    [InlineData("gap:20px 0")]
    public void ARowGap_SeparatesTheLines(string declaration)
    {
        var plain = Lay($"<div style='display:flex;flex-wrap:wrap'>{Half}AAA</div>{Half}BBB</div>{Half}CCC</div></div>");
        var gapped = Lay($"<div style='display:flex;flex-wrap:wrap;{declaration}'>{Half}AAA</div>{Half}BBB</div>{Half}CCC</div></div>");

        double pitch = plain["AAA"].Y - plain["CCC"].Y;
        Assert.True(pitch > 5, "the third item did not wrap");
        Assert.Equal(pitch + 15, gapped["AAA"].Y - gapped["CCC"].Y, 1);
        Assert.Equal(plain["AAA"].Y, gapped["AAA"].Y, 1);
    }

    /// <summary>
    /// An item wider than the line takes a line of its own (§9.3: a line holds at least one item),
    /// and the items after it start the next: 60%, 60%, 30% is [A] [B C].
    /// </summary>
    [Fact]
    public void AnItemWiderThanTheLine_TakesALineOfItsOwn()
    {
        var laid = Lay("<div style='display:flex;flex-wrap:wrap'><div style='width:60%'>AAA</div><div style='width:60%'>BBB</div><div style='width:30%'>CCC</div></div>");

        Assert.True(laid["BBB"].Y < laid["AAA"].Y - 2.0, "BBB did not start the second line");
        Assert.True(Math.Abs(laid["BBB"].Y - laid["CCC"].Y) < 2.0, "CCC did not follow BBB on the second line");
        Assert.True(laid["CCC"].X > laid["BBB"].X, "CCC is not to the right of BBB");
        Assert.Equal(laid["AAA"].X, laid["BBB"].X, 1);
    }

    /// <summary>
    /// Auto items are measured: in a 200px (150pt) container two 12-letter words of about 96pt
    /// cannot share a line, so the second starts the next, and the short third word follows it at
    /// the second's measured width plus the 10px gap — about 104pt on, not the 79pt an equal share
    /// would give. The container's stated width is the line (it was the page's width); without it
    /// the three fit one. (The gap keeps the extractor from reading the two adjacent words as one.)
    /// </summary>
    [Fact]
    public void MeasuredAutoItems_WrapAtTheirContentWidths_AndKeepThem()
    {
        const string items = "<div>AAAAAAAAAAAA</div><div>BBBBBBBBBBBB</div><div>CCC</div>";
        var narrow = Lay($"<div style='display:flex;flex-wrap:wrap;column-gap:10px;width:200px'>{items}</div>");
        var wide = Lay($"<div style='display:flex;flex-wrap:wrap;column-gap:10px'>{items}</div>");

        Assert.True(narrow["BBBBBBBBBBBB"].Y < narrow["AAAAAAAAAAAA"].Y - 2.0, "the second word did not wrap in a 200px container");
        Assert.True(Math.Abs(narrow["BBBBBBBBBBBB"].Y - narrow["CCC"].Y) < 2.0, "CCC did not follow the second word on its line");
        Assert.InRange(narrow["CCC"].X - narrow["BBBBBBBBBBBB"].X, 97, 110);
        Assert.Empty(narrow.Reports);

        Assert.True(Math.Abs(wide["AAAAAAAAAAAA"].Y - wide["CCC"].Y) < 2.0, "at the page's width the three did not share a line");
    }

    /// <summary>
    /// A measured item on a wrapped line is a fixed track of its content width, not a share: in a
    /// 300px (225pt) container a 200pt first item takes the first line, and on the second the
    /// two-word item (about 72pt) keeps that width, so the short item after it starts about 80pt on
    /// — the gap included — not at the 116pt an equal share would put it. (A single long word would
    /// not tell the two apart: the table's min-content floor widens its column to the word anyway.)
    /// </summary>
    [Fact]
    public void AMeasuredItemOnAWrappedLine_IsAFixedTrack_NotAShare()
    {
        var laid = Lay("<div style='display:flex;flex-wrap:wrap;column-gap:10px;width:300px'><div>AAAAAAAAAAAA AAAAAAAAAAAA</div><div>BBBB BBBB</div><div>CCC</div></div>");

        Assert.True(laid["BBBB"].Y < laid["AAAAAAAAAAAA"].Y - 2.0, "the two-word item did not start the second line");
        Assert.True(Math.Abs(laid["BBBB"].Y - laid["CCC"].Y) < 2.0, "CCC did not follow the two-word item on its line");
        Assert.InRange(laid["CCC"].X - laid["BBBB"].X, 74, 88);
    }

    /// <summary>
    /// An item holding a table with no stated width is measured at the table's columns since batch
    /// 110: the short table stays beside the first half-width item, the third item wraps, and nothing
    /// is reported. Before batch 110 the item had no measurable width — one line, reported; the case
    /// still reported is a column whose width nothing says (<c>FlexListTableTests</c>).
    /// </summary>
    [Fact]
    public void ATableItem_IsMeasured_AndTheLineBreaksAfterIt()
    {
        var laid = Lay($"<div style='display:flex;flex-wrap:wrap'>{Half}AAA</div><section><table><tr><td>BBB</td></tr></table></section>{Half}CCC</div></div>");

        // The cell's padding lowers the table's text by about 2pt on the shared line.
        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 5.0, $"the table did not stay on the first line: AAA at ({laid["AAA"].X:F1}, {laid["AAA"].Y:F1}), BBB at ({laid["BBB"].X:F1}, {laid["BBB"].Y:F1}), CCC at ({laid["CCC"].X:F1}, {laid["CCC"].Y:F1})");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, "the third item did not wrap after the measured table");
        Assert.DoesNotContain(laid.Reports, r => r.StartsWith("flex-wrap", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>justify-content</c> places each line's own free space: three 40% items centred put the
    /// first line's pair 10% in and the lone third item 30% in. The width is read off the
    /// <c>flex-start</c> layout, where the second item starts at 40%.
    /// </summary>
    [Fact]
    public void JustifyContent_PlacesEachLinesOwnFreeSpace()
    {
        const string items = "<div style='width:40%'>AAA</div><div style='width:40%'>BBB</div><div style='width:40%'>CCC</div>";
        var start = Lay($"<div style='display:flex;flex-wrap:wrap;justify-content:flex-start'>{items}</div>");
        var centred = Lay($"<div style='display:flex;flex-wrap:wrap;justify-content:center'>{items}</div>");

        double width = (start["BBB"].X - start["AAA"].X) / 0.4;
        Assert.True(width > 200, $"the content width came out {width:F1}pt");
        Assert.Equal(start["AAA"].X, start["CCC"].X, 1);
        Assert.Equal(start["AAA"].X + 0.1 * width, centred["AAA"].X, 1);
        Assert.Equal(start["AAA"].X + 0.3 * width, centred["CCC"].X, 1);
        Assert.Empty(centred.Reports);
    }

    /// <summary>
    /// A boxed paragraph is measured with its box: two 8-letter words of about 64pt fit a 200px
    /// (150pt) line bare, 10px apart, and with 15px of padding a side (86.5pt each) they do not —
    /// the second wraps, and nothing is reported (an unmeasurable item would be).
    /// </summary>
    [Fact]
    public void ABoxedParagraphItem_IsMeasuredWithItsPadding()
    {
        var bare = Lay("<div style='display:flex;flex-wrap:wrap;column-gap:10px;width:200px'><div>AAAAAAAA</div><div>BBBBBBBB</div></div>");
        var padded = Lay("<div style='display:flex;flex-wrap:wrap;column-gap:10px;width:200px'><div style='padding:0 15px'>AAAAAAAA</div><div style='padding:0 15px'>BBBBBBBB</div></div>");

        Assert.True(Math.Abs(bare["AAAAAAAA"].Y - bare["BBBBBBBB"].Y) < 2.0, "two bare 64pt words did not share a 150pt line");
        Assert.True(padded["BBBBBBBB"].Y < padded["AAAAAAAA"].Y - 2.0, "the padded second word did not wrap");
        Assert.Empty(padded.Reports);
    }

    /// <summary>
    /// A growing auto item on a wrapping container grows from its content (§9.2): <c>flex: auto</c>
    /// on a long word and a short one gives the long one its 96pt plus half the rest, so the short
    /// one starts more than 30pt past the midpoint. A nowrap row keeps its equal shares, as before.
    /// </summary>
    [Fact]
    public void GrowingAutoItems_GrowFromTheirContent_WhenWrapping()
    {
        const string items = "<div style='flex:auto'>AAAAAAAAAAAA</div><div style='flex:auto'>CCC</div>";
        var reference = Lay("<div style='display:flex'><div style='width:50%'>AAA</div><div style='width:50%'>BBB</div></div>");
        var wrapping = Lay($"<div style='display:flex;flex-wrap:wrap'>{items}</div>");
        var nowrap = Lay($"<div style='display:flex'>{items}</div>");

        double half = reference["BBB"].X - reference["AAA"].X;
        Assert.True(wrapping["CCC"].X - wrapping["AAAAAAAAAAAA"].X > half + 30,
            $"the long word's track is {wrapping["CCC"].X - wrapping["AAAAAAAAAAAA"].X:F1}pt against a half of {half:F1}pt; it did not grow from its content");
        Assert.Equal(half, nowrap["CCC"].X - nowrap["AAAAAAAAAAAA"].X, 1);
    }

    /// <summary>A value this engine does not place is reported once for the container, not once per line.</summary>
    [Fact]
    public void JustifyContent_AnUnplacedValue_IsReportedOncePerContainer()
    {
        var laid = Lay($"<div style='display:flex;flex-wrap:wrap;justify-content:left'>{Half}AAA</div>{Half}BBB</div>{Half}CCC</div></div>");

        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, "the third item did not wrap");
        Assert.Single(laid.Reports, r => r.StartsWith("justify-content", StringComparison.Ordinal));
    }

    /// <summary>
    /// Growers share each line's own free space (§9.7): three <c>flex: 1 1 40%</c> items give a
    /// first line of two at 50% each and a second line of one at the full width.
    /// </summary>
    [Fact]
    public void Growers_ShareEachLinesOwnFreeSpace()
    {
        const string fixedItems = "<div style='width:40%'>AAA</div><div style='width:40%'>BBB</div><div style='width:40%'>CCC</div>";
        var reference = Lay($"<div style='display:flex;flex-wrap:wrap'>{fixedItems}</div>");
        var grown = Lay("<div style='display:flex;flex-wrap:wrap'><div style='flex:1 1 40%;border-top:1px solid #000'>AAA</div>" +
                        "<div style='flex:1 1 40%'>BBB</div><div style='flex:1 1 40%;border-top:1px solid #000'>CCC</div></div>");

        double width = (reference["BBB"].X - reference["AAA"].X) / 0.4;
        Assert.Equal(0.5 * width, grown["BBB"].X - grown["AAA"].X, 1);
        Assert.True(grown["CCC"].Y < grown["AAA"].Y - 2.0, "the third item did not wrap");
        // The lone grower's rule spans the line: twice the 50% rule of the first.
        var rules = grown.Rules;
        Assert.Equal(2, rules.Count);
        Assert.Equal(2 * rules.Min(), rules.Max(), 0);
    }

    /// <summary>
    /// Three thirds fit one line to the fit tolerance (33.3333% × 3), and a fourth wraps under the
    /// first; a 10px column gap between two halves is counted toward the line, so the second half
    /// wraps where without the gap it fits; and every gap on the line counts — in a 300px (225pt)
    /// container three 30% items (67.5pt) with 20px (15pt) gaps come to 232.5pt, so the third
    /// wraps, where counting one gap (217.5pt) would fit it.
    /// </summary>
    [Fact]
    public void TheGapCountsTowardTheLine_AndThirdsFitToTheTolerance()
    {
        const string third = "<div style='width:33.3333%'>";
        var thirds = Lay($"<div style='display:flex;flex-wrap:wrap'>{third}AAA</div>{third}BBB</div>{third}CCC</div>{third}DDD</div></div>");
        var halves = Lay($"<div style='display:flex;flex-wrap:wrap'>{Half}AAA</div>{Half}BBB</div></div>");
        var gapped = Lay($"<div style='display:flex;flex-wrap:wrap;column-gap:10px'>{Half}AAA</div>{Half}BBB</div></div>");
        const string tenth = "<div style='width:30%'>";
        var everyGap = Lay($"<div style='display:flex;flex-wrap:wrap;column-gap:20px;width:300px'>{tenth}AAA</div>{tenth}BBB</div>{tenth}CCC</div></div>");

        Assert.True(Math.Abs(thirds["AAA"].Y - thirds["CCC"].Y) < 2.0, "three thirds did not fit one line");
        Assert.True(thirds["DDD"].Y < thirds["AAA"].Y - 2.0, "the fourth third did not wrap");
        Assert.Equal(thirds["AAA"].X, thirds["DDD"].X, 1);
        Assert.True(Math.Abs(halves["AAA"].Y - halves["BBB"].Y) < 2.0, "two halves did not share a line");
        Assert.True(gapped["BBB"].Y < gapped["AAA"].Y - 2.0, "two halves with a gap between them still shared the line");
        Assert.True(Math.Abs(everyGap["AAA"].Y - everyGap["BBB"].Y) < 2.0, "the first two 30% items did not share the line");
        Assert.True(everyGap["CCC"].Y < everyGap["AAA"].Y - 2.0, "the third 30% item fitted a line its two gaps overfill");
    }

    /// <summary>
    /// A stated zero basis is the item's longest word (its min-content width, CSS Flexbox §4.5, since
    /// batch 107), not its content: three <c>flex: 1</c> items whose content (about 200pt of words)
    /// would overfill a 200px line stay on it, since their longest words fit it, the table sharing it
    /// out and each cell wrapping its words. <c>FlexMinContentTests</c> has the words that do not fit.
    /// </summary>
    [Fact]
    public void ZeroBasisItems_WhoseLongestWordsFit_ShareTheLine()
    {
        var laid = Lay("<div style='display:flex;flex-wrap:wrap;column-gap:10px;width:200px'><div style='flex:1'>AAAA AAAA AAAA</div><div style='flex:1'>BBBB BBBB</div><div style='flex:1'>CCC</div></div>");

        Assert.True(Math.Abs(laid["AAAA"].Y - laid["CCC"].Y) < 2.0 && Math.Abs(laid["BBBB"].Y - laid["CCC"].Y) < 2.0,
            "flex: 1 items were broken into lines on their content");
        Assert.True(laid["CCC"].X > laid["BBBB"].X && laid["BBBB"].X > laid["AAAA"].X, "the items were not laid out in order");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A first item wider than the line takes the first line, with no empty line before it: its
    /// text sits where the nowrap layout puts it, and the next item starts the second line.
    /// </summary>
    [Fact]
    public void AFirstItemWiderThanTheLine_StartsNoEmptyLineBeforeIt()
    {
        var wrapped = Lay("<div style='display:flex;flex-wrap:wrap'><div style='width:120%'>AAA</div><div style='width:50%'>BBB</div></div>");
        var nowrap = Lay("<div style='display:flex'><div style='width:120%'>AAA</div><div style='width:50%'>BBB</div></div>");

        Assert.Equal(nowrap["AAA"].Y, wrapped["AAA"].Y, 1);
        Assert.Equal(nowrap["AAA"].X, wrapped["AAA"].X, 1);
        Assert.True(wrapped["BBB"].Y < wrapped["AAA"].Y - 2.0, "the half after the over-wide item did not start the second line");
    }

    /// <summary>
    /// An empty item measures 0, a size rather than an unknown: two halves share its line with it
    /// and nothing is reported (an unknown size would have been), and the third half wraps.
    /// </summary>
    [Fact]
    public void AnEmptyItem_MeasuresZero_NotUnknown()
    {
        var laid = Lay($"<div style='display:flex;flex-wrap:wrap'>{Half}AAA</div><div></div>{Half}BBB</div>{Half}CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "the empty item broke the first line");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, "the third half did not wrap");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A wrapping flex column is block flow, as a column is: it wraps only at a definite height
    /// (§9.3), which the flow has not got, so nothing is laid out differently and nothing reported.
    /// </summary>
    [Fact]
    public void AWrappingColumn_IsBlockFlow_AndReportsNothing()
    {
        var laid = Lay("<div style='display:flex;flex-direction:column;flex-wrap:wrap'><div>AAA</div><div>BBB</div></div>");

        Assert.True(laid["BBB"].Y < laid["AAA"].Y - 2.0, "the column's items were not stacked");
        Assert.Empty(laid.Reports);
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
        public readonly List<string> Reports = new();
        public readonly List<double> Rules = new();
        public (double X, double Y) this[string word]
        {
            get
            {
                Assert.True(Words.ContainsKey(word), $"'{word}' was not laid out at all");
                return Words[word];
            }
        }
    }

    private static Laid Lay(string html)
    {
        var laid = new Laid();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        byte[] pdf = output.ToArray();

        using var input = new MemoryStream(pdf, writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);

        foreach (var rule in ManagedTestSupport.StrokedSegments(ManagedTestSupport.DecodeStreams(pdf)).Where(s => s.IsHorizontal))
            laid.Rules.Add(Math.Abs(rule.X1 - rule.X0));
        return laid;
    }
}
