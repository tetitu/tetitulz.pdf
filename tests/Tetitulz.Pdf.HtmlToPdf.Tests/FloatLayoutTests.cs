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
/// <c>float</c> / <c>clear</c> in the managed engine: a floated element and the content beside it
/// share a line, and <c>clear</c> ends the band.
/// </summary>
/// <remarks>
/// <para>Asserted on <b>measured glyph positions from the produced PDF</b>. Before this, a floated
/// element fell through to ordinary block flow and stacked — producing a perfectly valid PDF that
/// every structural assertion accepted, which is why the gap was invisible.</para>
/// <para><b>The band is the fallback, not the rule.</b> A float whose width can be known — declared,
/// or measured from one paragraph or, since batch 125, from a box of blocks — is a real float that
/// re-flows the text past its bottom (<c>FloatReflowTests</c>, <c>FloatedBlockContentTests</c>). A
/// float whose width nothing says keeps the text in a fixed column, reported through
/// <c>BuilderDiagnosticKind.ContentApproximated</c>; the report rather than the behaviour is what
/// is pinned there — a silent approximation is the thing worth guarding against.</para>
/// </remarks>
public class FloatLayoutTests
{
    [Fact]
    public void FloatLeft_PutsTheFloatBeforeTheContentOnOneLine()
    {
        var (logo, addr) = Positions(
            "<div><div style='float:left;width:100pt'>LOGO</div><div>ADDRESS</div></div>",
            "LOGO", "ADDRESS");

        Assert.True(Math.Abs(logo.Y - addr.Y) < 2.0,
            $"LOGO at y={logo.Y:F1} and ADDRESS at y={addr.Y:F1} — the float did not form a band");
        Assert.True(logo.X < addr.X,
            $"LOGO at x={logo.X:F1} is not left of ADDRESS at x={addr.X:F1}");
    }

    [Fact]
    public void FloatRight_PutsTheFloatAfterTheContentOnOneLine()
    {
        var (logo, addr) = Positions(
            "<div><div style='float:right;width:100pt'>LOGO</div><div>ADDRESS</div></div>",
            "LOGO", "ADDRESS");

        Assert.True(Math.Abs(logo.Y - addr.Y) < 2.0, "float:right did not form a band");
        Assert.True(logo.X > addr.X,
            $"float:right put LOGO at x={logo.X:F1}, not right of ADDRESS at x={addr.X:F1}");
    }

    /// <summary>
    /// <c>clear</c> is honoured exactly — it ends the band and the cleared element resumes full
    /// width. This is the one part of float that is not an approximation.
    /// </summary>
    [Fact]
    public void Clear_EndsTheBandAndResumesFullWidth()
    {
        var words = Words(
            "<div><div style='float:left;width:100pt'>LOGO</div><div>ADDRESS</div>" +
            "<div style='clear:both'>BELOW</div></div>");

        var logo = Find(words, "LOGO");
        var below = Find(words, "BELOW");

        Assert.True(below.Y < logo.Y - 2.0,
            $"BELOW at y={below.Y:F1} did not drop under the band at y={logo.Y:F1}");
        // Back at the left margin, not in the second column.
        Assert.True(below.X < logo.X + 5.0,
            $"BELOW at x={below.X:F1} stayed in the band's column instead of clearing to full width");
    }

    /// <summary>Without a float the same markup stacks — the control that gives the tests meaning.</summary>
    [Fact]
    public void WithoutAFloat_TheSameMarkupStacks()
    {
        var (logo, addr) = Positions(
            "<div><div style='width:100pt'>LOGO</div><div>ADDRESS</div></div>",
            "LOGO", "ADDRESS");

        Assert.True(Math.Abs(logo.Y - addr.Y) > 2.0,
            "unfloated blocks shared a line, so the float tests prove nothing");
    }

    /// <summary>A float with nothing beside it stays an ordinary block rather than becoming a band.</summary>
    [Fact]
    public void FloatAlone_IsLeftAsAnOrdinaryBlock()
    {
        var words = Words("<div><div style='float:left;width:100pt'>LOGO</div></div>");
        var logo = Find(words, "LOGO");

        // At the content margin, i.e. not indented into a table cell.
        Assert.True(logo.X < 35.0, $"a lone float was boxed into a band (x={logo.X:F1})");
    }

    /// <summary>
    /// Text beside a short float returns to the full measure once it passes the bottom.
    /// </summary>
    /// <remarks>
    /// <b>The point of the whole exercise, and the thing a two-cell table can never do.</b> A band
    /// keeps the text in its narrower column for its whole length, so a short float leaves a
    /// channel of whitespace running to the bottom of the block. Both halves are asserted: some
    /// line must start clear of the float, and some later line must start back at the margin. Only
    /// one of them is true of either failure.
    /// </remarks>
    [Fact]
    public void TextBesideAShortFloatReturnsToTheFullMeasureBelowIt()
    {
        string body = string.Join(" ",
            Enumerable.Repeat("The quick brown fox jumps over the lazy dog and keeps on running", 20));

        List<PdfTextWord> words = Words(
            $"<div><div style='float:left;width:120pt'>LOGO</div><div>{body}</div></div>");

        // Line starts, keyed on baseline — a line is several words and only its first is at the edge.
        var lineLefts = words
            .Where(w => !w.Text.Contains("LOGO", StringComparison.Ordinal))
            .GroupBy(w => (int)Math.Round(w.Y))
            .Select(g => g.Min(w => w.X))
            .ToList();

        Assert.True(lineLefts.Count >= 4, $"expected several lines, got {lineLefts.Count}");

        double leftmost = lineLefts.Min();
        Assert.Contains(lineLefts, x => x > leftmost + 60);
        Assert.Contains(lineLefts, x => x < leftmost + 5);
    }

    /// <summary>
    /// A float that becomes a real float reports nothing, because nothing was approximated.
    /// </summary>
    /// <remarks>
    /// Content beside a two-cell band keeps its narrower column for its whole length, an approximation
    /// that must say so; a float whose element is one paragraph of known width is emitted as
    /// <c>Paragraph.Float</c> and re-flows exactly, so a report would be false.
    /// <para>
    /// The channel is <c>HtmlToPdfOptions.OnDiagnostic</c> — a <b>per-call</b> callback, not a
    /// process-wide event. A global sink would make this a claim about the whole test process,
    /// which is the coupling that produced real flakes elsewhere in this repository.
    /// </para>
    /// </remarks>
    [Fact]
    public void FloatMappedExactly_ReportsNothing()
    {
        Assert.Empty(ApproximationsFor(
            "<div><div style='float:left;width:100pt'>LOGO</div><div>ADDRESS</div></div>"));
    }

    /// <summary>
    /// A float with no declared width is measured, becomes a real float, and reports nothing.
    /// </summary>
    /// <remarks>
    /// <b>This test asserted the opposite until the mapper ran the shrink-to-fit pass</b> it used
    /// to say it did not run: the reserved width is now the paragraph's widest line as
    /// <c>PdfExporter.MeasureShrinkToFitWidth</c> measures it, so the two-cell band is not taken and
    /// the report would be false. Renamed rather than re-asserted, because the behaviour changed
    /// rather than the claim. <c>FloatShrinkToFitTests</c> pins the measured layout; the one shape
    /// that still reaches the fallback is the multi-block float below.
    /// </remarks>
    [Fact]
    public void FloatWithNoDeclaredWidth_IsMeasuredAndReportsNothing()
    {
        Assert.DoesNotContain(
            ApproximationsFor("<div><div style='float:left'>LOGO</div><div>ADDRESS</div></div>"),
            d => d.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A float of several paragraphs is a floated box since batch 125: both paragraphs are kept, the
    /// content beside it shares the first one's line, clear of the float's width, and nothing is
    /// reported — it fell back to the band, reported, before.
    /// </summary>
    /// <remarks>
    /// <b>The count condition and the type condition are separate, and only this pins the count
    /// one.</b> A float wrapping a <c>&lt;table&gt;</c> produces one block of the wrong type, so a
    /// mutation that floated <c>blocks[0]</c> alone and dropped the rest would pass it; here the
    /// assertion is that both paragraphs survive.
    /// </remarks>
    [Fact]
    public void FloatOfSeveralParagraphs_IsAFloatedBoxAndKeepsAllOfThem()
    {
        const string Html =
            "<div><div style='float:left;width:100pt'><p>ALPHA</p><p>BETA</p></div>"
            + "<div>ADDRESS</div></div>";

        List<PdfTextWord> words = Words(Html);

        PdfTextWord alpha = Find(words, "ALPHA");
        PdfTextWord beta = Find(words, "BETA");
        PdfTextWord address = Find(words, "ADDRESS");
        // Beside the float: between its first paragraph's line (a <p>'s 12pt top margin above the
        // div's line) and its second's.
        Assert.True(address.Y <= alpha.Y + 14 && address.Y >= beta.Y - 2, $"ADDRESS at y={address.Y:F1} is not beside the float, ALPHA at y={alpha.Y:F1}, BETA at y={beta.Y:F1}");
        Assert.True(address.X > alpha.X + 95, $"ADDRESS at x={address.X:F1} is not clear of the 100pt float at x={alpha.X:F1}");
        Assert.DoesNotContain(ApproximationsFor(Html), d => d.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>clear</c> drops past a float that is TALLER than the text beside it.
    /// </summary>
    /// <remarks>
    /// <b>The case a short float cannot test.</b> With a one-line float, whatever follows falls
    /// below it whether <c>clear</c> was honoured or not, so a one-line test passes against a mapper
    /// that drops the statement entirely. Here the
    /// float is several lines tall and the content beside it is one line, so an unhonoured
    /// <c>clear</c> leaves BELOW alongside the float instead of under it.
    /// <para>
    /// A real float does not end until its own bottom, so this had to be carried explicitly as
    /// <c>Paragraph.ClearFloats</c>; a two-cell band gets it structurally, because a table ends the
    /// side-by-side region on its own.
    /// </para>
    /// </remarks>
    [Fact]
    public void Clear_DropsPastAFloatTallerThanTheTextBesideIt()
    {
        string tall = string.Join(" ", Enumerable.Repeat("TALL", 40));

        List<PdfTextWord> words = Words(
            $"<div><div style='float:left;width:120pt'>{tall}</div>"
            + "<div>ADDR</div><div style='clear:both'>BELOW</div></div>");

        PdfTextWord below = Find(words, "BELOW");
        PdfTextWord addr = Find(words, "ADDR");
        double floatBottom = words
            .Where(w => w.Text.Contains("TALL", StringComparison.Ordinal))
            .Min(w => w.Y);

        // Back at the margin: ADDR was pushed clear of the float, BELOW must not be.
        Assert.True(below.X < addr.X - 20,
            $"BELOW at x={below.X:F1} was not released; ADDR beside the float is at x={addr.X:F1}");

        // And genuinely under it.
        Assert.True(below.Y < floatBottom,
            $"BELOW at y={below.Y:F1} is not below the float, whose last line is at y={floatBottom:F1}");
    }

    /// <summary>
    /// A float whose width nothing says falls back to the band: no declared width, and a table nested
    /// past the layout's cap, which the measurer cannot size. The same table with a declared width is
    /// a floated box (batch 125) and reports nothing — the control.
    /// </summary>
    /// <remarks>
    /// <b>The channel has to keep working for the case that is still approximate</b>, or removing
    /// it would look the same as fixing it. Until batch 125 any float that was not one paragraph
    /// reached the band; now only one whose width cannot be known does.
    /// </remarks>
    [Fact]
    public void FloatWhoseWidthCannotBeKnown_StillReportsTheApproximation()
    {
        string Nested = "<p>LOGO</p>" + ManagedTestSupport.TablesPastTheNestingCap("");
        Assert.Contains(
            ApproximationsFor($"<div><div style='float:left'>{Nested}</div><div>ADDRESS</div></div>"),
            d => d.Contains("float", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            ApproximationsFor($"<div><div style='float:left;width:100pt'>{Nested}</div><div>ADDRESS</div></div>"),
            d => d.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every ContentApproximated detail reported while converting some markup.</summary>
    private static List<string> ApproximationsFor(string html)
    {
        var seen = new List<string>();

        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d =>
            {
                if (d.Kind == Tetitulz.Pdf.Builder.Export.BuilderDiagnosticKind.ContentApproximated)
                    seen.Add(d.Detail);
            },
        });

        return seen;
    }

    /// <summary>
    /// ...and does not report when there is no band, so the channel stays worth reading.
    /// </summary>
    [Fact]
    public void WithoutAFloat_NothingIsReportedAsApproximated()
    {
        var seen = new List<string>();

        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(
            "<div><div>LOGO</div><div>ADDRESS</div></div>",
            output,
            new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                OnDiagnostic = d =>
                {
                    if (d.Kind == Tetitulz.Pdf.Builder.Export.BuilderDiagnosticKind.ContentApproximated)
                        seen.Add(d.Detail);
                },
            });

        Assert.DoesNotContain(seen, d => d.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ runs of floats

    /// <summary>
    /// Floats with nothing beside them share a line — the whole of a float-based grid
    /// (Bootstrap 3's <c>.row &gt; .col-*</c>).
    /// </summary>
    /// <remarks>
    /// This fell back to block flow, because a float with nothing beside it "is just a block" —
    /// true of one float, and wrong for a run of them. Every column of such a grid stacked, and
    /// nothing was reported.
    /// </remarks>
    [Fact]
    public void FloatsWithNothingBesideThem_ShareALine()
    {
        var (a, b) = Positions(
            "<div><div style='float:left;width:50%'>AAA</div><div style='float:left;width:50%'>BBB</div></div>",
            "AAA", "BBB");

        Assert.True(Math.Abs(a.Y - b.Y) < 2.0, $"AAA at y={a.Y:F1} and BBB at y={b.Y:F1} did not share a line");
        Assert.True(b.X - a.X > 200, $"BBB at x={b.X:F1} is not half a measure right of AAA at x={a.X:F1}");

        // The control: the same widths without float stack.
        var (c, d) = Positions(
            "<div><div style='width:50%'>AAA</div><div style='width:50%'>BBB</div></div>",
            "AAA", "BBB");
        Assert.True(Math.Abs(c.Y - d.Y) > 2.0, "unfloated blocks shared a line, so this proves nothing");
    }

    /// <summary>
    /// A float that does not fit beside the ones before it starts a new line, at the float edge.
    /// </summary>
    /// <remarks>
    /// Four half-width floats are two lines of two. One line of four would squeeze each to a
    /// quarter: the table normalises its column widths to fit, so nothing overflows to warn.
    /// </remarks>
    [Fact]
    public void AFloatThatDoesNotFit_StartsANewLine()
    {
        const string Half = "<div style='float:left;width:50%'>{0}</div>";
        var words = Words("<div>" + string.Concat(new[] { "CA", "CB", "CC", "CD" }
                                        .Select(t => string.Format(Half, t))) + "</div>");

        var ca = Find(words, "CA"); var cb = Find(words, "CB");
        var cc = Find(words, "CC"); var cd = Find(words, "CD");

        Assert.True(Math.Abs(ca.Y - cb.Y) < 2.0, "the first two did not share a line");
        Assert.True(Math.Abs(cc.Y - cd.Y) < 2.0, "the last two did not share a line");
        Assert.True(cc.Y < ca.Y - 2.0, $"CC at y={cc.Y:F1} did not drop below CA at y={ca.Y:F1}");
        Assert.True(Math.Abs(cc.X - ca.X) < 2.0, $"CC at x={cc.X:F1} did not return to CA's edge at x={ca.X:F1}");
    }

    /// <summary>
    /// A line of floats narrower than the measure keeps the widths it declared, instead of being
    /// stretched to fill the line.
    /// </summary>
    [Fact]
    public void ALineNarrowerThanTheMeasure_KeepsItsDeclaredWidths()
    {
        var (a, b) = Positions(
            "<div><div style='float:left;width:25%'>AAA</div><div style='float:left;width:25%'>BBB</div></div>",
            "AAA", "BBB");

        // The default page: 8.5in less two 0.4in margins is a 554.4pt measure, a quarter of which
        // is 138.6pt. Stretched to fill, the two would be a half-measure (277pt) apart.
        double quarter = (612 - 2 * 28.8) / 4;
        Assert.True(Math.Abs((b.X - a.X) - quarter) < 10,
            $"BBB is {b.X - a.X:F1}pt right of AAA; a quarter of the measure is {quarter:F1}pt");
    }

    /// <summary>
    /// A run of floats with no declared width is measured — each as wide as its content, since batch
    /// 126 — and reports nothing, as a run that declares its widths does not; a run with a float
    /// nothing can measure (a table nested past the layout's cap) shares the line equally and is
    /// REPORTED, the control. (<c>FloatRunMeasureTests</c> has the widths.)
    /// </summary>
    [Fact]
    public void ARunOfFloatsWithNoDeclaredWidth_IsMeasuredAndReportsNothing()
    {
        Assert.DoesNotContain(
            ApproximationsFor("<div><div style='float:left'>AAA</div><div style='float:left'>BBB</div></div>"),
            d => d.Contains("float", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            ApproximationsFor("<div><div style='float:left;width:50%'>AAA</div>" +
                              "<div style='float:left;width:50%'>BBB</div></div>"),
            d => d.Contains("float", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            ApproximationsFor("<div><div style='float:left'>AAA</div>" +
                              "<div style='float:left'><p>BBB</p>" + ManagedTestSupport.TablesPastTheNestingCap("") + "</div></div>"),
            d => d.Contains("float", StringComparison.OrdinalIgnoreCase) && d.Contains("no measurable content", StringComparison.Ordinal));
    }

    /// <summary>
    /// A clearfix element's content still sits beside the floats; only its <c>::after</c> drops
    /// below them. Bootstrap 3's <c>.dl-horizontal</c> is exactly this: a floated <c>dt</c> and a
    /// <c>dd</c> carrying a clearfix.
    /// </summary>
    [Fact]
    public void AClearfixElementWithContent_StaysBesideTheFloats()
    {
        const string Css =
            "<style>dt{float:left;width:100pt;clear:left} dd{margin-left:120pt}" +
            " dd:after{content:' ';display:table;clear:both}</style>";
        var words = Words(Css + "<dl><dt>TERMA</dt><dd>DESCA</dd><dt>TERMB</dt><dd>DESCB</dd></dl>");

        var termA = Find(words, "TERMA"); var descA = Find(words, "DESCA");
        var termB = Find(words, "TERMB"); var descB = Find(words, "DESCB");

        Assert.True(Math.Abs(termA.Y - descA.Y) < 2.0, $"DESCA at y={descA.Y:F1} left TERMA's line at y={termA.Y:F1}");
        Assert.True(Math.Abs(termB.Y - descB.Y) < 2.0, $"DESCB at y={descB.Y:F1} left TERMB's line at y={termB.Y:F1}");
        Assert.True(termB.Y < termA.Y - 2.0, "the second pair did not start below the first");

        // Consumed once: the band takes the dd, and the walk must not map it a second time.
        Assert.Single(words, w => w.Text.Contains("DESCA", StringComparison.Ordinal));
        Assert.Single(words, w => w.Text.Contains("DESCB", StringComparison.Ordinal));
    }

    /// <summary>
    /// An empty clearfix between floats ends their line, and the floats after it start a new one —
    /// the grid idiom of a <c>&lt;div class="clearfix"&gt;</c> between column groups.
    /// </summary>
    [Fact]
    public void AnEmptyClearfixBetweenFloats_EndsTheLine()
    {
        const string Quarter = "<div style='float:left;width:25%'>{0}</div>";
        var words = Words(
            "<style>.cf:after{content:' ';display:table;clear:both}</style><div>" +
            string.Format(Quarter, "QA") + string.Format(Quarter, "QB") +
            "<div class='cf'></div>" +
            string.Format(Quarter, "QC") + "</div>");

        var qa = Find(words, "QA"); var qb = Find(words, "QB"); var qc = Find(words, "QC");

        Assert.True(Math.Abs(qa.Y - qb.Y) < 2.0, "the floats before the clearfix did not share a line");
        Assert.True(qc.Y < qa.Y - 2.0, $"QC at y={qc.Y:F1} did not start a new line below QA at y={qa.Y:F1}");
        // Within a band cell's padding of QA: a lone float is laid out as a block, with no cell
        // around it. Beside QB instead, it would be a quarter-measure (~140pt) along.
        Assert.True(Math.Abs(qc.X - qa.X) < 10, $"QC at x={qc.X:F1} did not return to the float edge at x={qa.X:F1}");
    }

    // ------------------------------------------------------------------

    private static ((double X, double Y) A, (double X, double Y) B) Positions(
        string html, string a, string b)
    {
        var words = Words(html);
        var wa = Find(words, a);
        var wb = Find(words, b);
        return ((wa.X, wa.Y), (wb.X, wb.Y));
    }

    private static PdfTextWord Find(List<PdfTextWord> words, string text)
    {
        var w = words.FirstOrDefault(x => x.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(w != null, $"\"{text}\" was not laid out at all");
        return w!;
    }

    private static List<PdfTextWord> Words(string html)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output,
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
