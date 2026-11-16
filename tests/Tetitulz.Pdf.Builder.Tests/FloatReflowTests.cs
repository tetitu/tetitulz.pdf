// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.Float"/> — text flows down a float's side and returns to the full measure
/// once it clears the bottom.
/// </summary>
/// <remarks>
/// <para><b>Re-flow is the whole feature, and it is what a two-column band cannot do.</b> Modelling
/// a float and its neighbour as a two-cell table keeps the text in its column forever, so a short
/// float leaves a channel of whitespace running to the bottom of the block. CSS returns the text to
/// the full measure the moment it passes the float's bottom edge.</para>
///
/// <para><b>Measured from the emitted <c>Tm</c> positions, not from the model.</b> The layout is the
/// thing under test, so asking the model what it intended would be circular; these read the x of
/// each drawn run back out of the produced content stream.</para>
///
/// <para><b>The assertions are about which lines start where, never about a single line.</b> A
/// layout that never re-flows has every line pushed right; one where the float never applied has
/// every line at the margin. Only the presence of <em>both</em> distinguishes the feature from
/// either failure.</para>
/// </remarks>
public sealed class FloatReflowTests
{
    private const double PageWidth = 612;
    private const double Margin = 72;
    private const double BandWidth = PageWidth - (2 * Margin);   // 468 pt
    private const double FloatWidth = 200;

    /// <summary>A left float pushes the text's edge right, and then releases it.</summary>
    /// <remarks>
    /// <b>The assertion that separates re-flow from a two-column band.</b> Some line must start
    /// clear of the float and some later line must start at the band's own left edge; a band
    /// produces only the first.
    /// </remarks>
    [Fact]
    public void ALeftFloatPushesTheEdgeRightAndThenReleasesIt()
    {
        double[] lefts = BodyLineLefts(WithFloat(FloatSide.Left));

        Assert.True(lefts.Length >= 4, $"expected several body lines, got {lefts.Length}");
        Assert.Contains(lefts, x => x > Margin + FloatWidth - 5);
        Assert.Contains(lefts, x => x < Margin + 5);
    }

    /// <summary>Once the float is cleared, no later line is pushed back out.</summary>
    /// <remarks>
    /// A float's exclusion covers one contiguous vertical band, so the release is monotone. A
    /// layout that re-narrowed further down would mean the exclusion was being applied by line
    /// <em>index</em> rather than by position — which is exactly the mistake a per-line-index
    /// width function invites, and the reason the widths are re-derived from y each pass.
    /// </remarks>
    [Fact]
    public void TheReleaseIsMonotone()
    {
        double[] lefts = BodyLineLefts(WithFloat(FloatSide.Left));

        int firstReleased = Array.FindIndex(lefts, x => x < Margin + 5);
        Assert.True(firstReleased >= 0, "no line ever returned to the margin");

        for (int i = firstReleased; i < lefts.Length; i++)
            Assert.True(lefts[i] < Margin + 5,
                $"line {i} was pushed back to x={lefts[i]:F1} after the float had been cleared");
    }

    /// <summary>A right float shortens the lines without moving their start.</summary>
    /// <remarks>
    /// The mirror case, and it fails differently: a right float treated as a left one would move
    /// the text's start rather than its end, which the left-edge assertions above would not catch.
    /// </remarks>
    [Fact]
    public void ARightFloatDoesNotMoveTheTextsStart()
    {
        double[] lefts = BodyLineLefts(WithFloat(FloatSide.Right));

        Assert.True(lefts.Length >= 4);
        Assert.All(lefts, x => Assert.True(x < Margin + 5,
            $"a right float moved a line's left edge to {x:F1}"));
    }

    /// <summary>A right float really does shorten some line.</summary>
    /// <remarks>
    /// Paired with the test above, because "did not move the start" is also true of a float that
    /// did nothing at all. The right edge is what a right float changes.
    /// </remarks>
    [Fact]
    public void ARightFloatShortensTheLinesBesideIt()
    {
        double[] rights = BodyLineRights(WithFloat(FloatSide.Right));
        double[] control = BodyLineRights(WithFloat(FloatSide.None));

        Assert.True(rights.Min() < control.Max() - FloatWidth + 20,
            $"no line was shortened: shortest right edge {rights.Min():F1} against an "
            + $"unfloated maximum of {control.Max():F1}");
    }

    /// <summary>Without a float every line starts at the margin.</summary>
    /// <remarks>
    /// <b>The control, and the regression guard for every document written before floats
    /// existed.</b> The same text with <see cref="FloatSide.None"/> must lay out as it always did.
    /// </remarks>
    [Fact]
    public void WithoutAFloatEveryLineStartsAtTheMargin()
    {
        double[] lefts = BodyLineLefts(WithFloat(FloatSide.None));

        Assert.True(lefts.Length >= 4);
        Assert.All(lefts, x => Assert.True(x < Margin + 5,
            $"an unfloated paragraph started a line at x={x:F1}"));
    }

    /// <summary>Lines beside a float are BROKEN shorter, not merely drawn further right.</summary>
    /// <remarks>
    /// <b>The assertion every other test in this file was missing.</b> Placement and breaking are
    /// separate steps: a layout that breaks every line at the full measure and then draws the
    /// pushed ones further right puts each line's start in exactly the right place and lets its
    /// text run off the end of the band. Two mutations survived on that gap — one that never
    /// consulted the per-line width when breaking, and one that skipped the fixed-point resolution
    /// entirely — because both leave the <em>starts</em> correct.
    /// <para>
    /// Character count rather than a measured right edge, because the probe reports where a run
    /// begins and this needs how much text the line was given.
    /// </para>
    /// </remarks>
    [Fact]
    public void LinesBesideAFloatAreBrokenShorterNotJustMovedRight()
    {
        List<(double Left, int Chars)> lines = FirstPageLines(WithFloat(FloatSide.Left));

        var pushed = lines.Where(l => l.Left > Margin + FloatWidth - 5).Select(l => l.Chars).ToList();
        var full = lines.Where(l => l.Left < Margin + 5).Select(l => l.Chars).ToList();

        Assert.True(pushed.Count > 0, "no line was pushed clear of the float");
        Assert.True(full.Count > 0, "no line returned to the full measure");

        // The longest line beside the float must be shorter than the longest full-width one. Maxima
        // rather than averages, because a paragraph's last line is short whatever the measure is.
        Assert.True(pushed.Max() < full.Max(),
            $"lines beside the float were not broken shorter: longest beside = {pushed.Max()} "
            + $"characters, longest at full measure = {full.Max()}");
    }

    /// <summary>A float on one page does not narrow the next.</summary>
    /// <remarks>
    /// <b>A float's exclusion belongs to the page it was drawn on.</b> Text on the following page
    /// flows at the full measure whatever sat beside it on the previous one; carrying the registry
    /// across would narrow every page of a document after its first floated block. The float is at
    /// the TOP of page 1 deliberately — a leaked exclusion only shows where the next page's lines
    /// share its y band, so a float near the bottom would let the bug through.
    /// </remarks>
    [Fact]
    public void AFloatDoesNotNarrowTheFollowingPage()
    {
        Document doc = NewDoc();
        doc.Sections[0].Children.Add(new Paragraph
        {
            Float = FloatSide.Left,
            FloatWidth = FloatWidth,
            Runs = { new TextRun { Text = FloatText } },
        });
        AddBody(doc, repeats: 220);   // comfortably more than one page

        List<PdfContentProbe.DrawnText> runs = BodyRuns(doc);
        int boundary = PageBoundary(runs);
        Assert.True(boundary > 0, "the fixture did not span two pages");

        // LINE starts, not run starts: a line is drawn as several runs and only the first of them
        // sits at the margin. Asserting every run's x caught a mid-line continuation at 95.4 and
        // read it as a leaked float, which would have been at 272.
        double[] lefts = LineLefts(runs.Skip(boundary));

        Assert.True(lefts.Length > 0, "no text was drawn on the second page");
        Assert.All(lefts, x => Assert.True(x < Margin + 5,
            $"a line on the second page started at x={x:F1}; "
            + "the first page's float was still excluding it"));
    }

    /// <summary>A float with no width is laid out as an ordinary block.</summary>
    /// <remarks>
    /// CSS shrink-to-fit needs a measurement pass this model does not run, so a float stating no
    /// width has nothing to reserve. Laying it out normally keeps the content; inventing a width
    /// would move text the caller never asked to move.
    /// </remarks>
    [Fact]
    public void AFloatWithNoWidthIsLaidOutAsAnOrdinaryBlock()
    {
        Document doc = NewDoc();
        doc.Sections[0].Children.Add(new Paragraph
        {
            Float = FloatSide.Left,
            FloatWidth = 0,
            Runs = { new TextRun { Text = FloatText } },
        });
        AddBody(doc);

        Assert.All(BodyLineLefts(doc), x => Assert.True(x < Margin + 5,
            $"a width-less float still displaced text to x={x:F1}"));
    }

    /// <summary>A float wider than its band is clamped rather than refused.</summary>
    /// <remarks>
    /// <b>Clamped, which is what a browser does with the same input.</b> Refusing would drop the
    /// float's content; letting it exceed the band would push every following line off the page.
    /// With the whole band taken, the text has nowhere to sit beside it and flows below.
    /// </remarks>
    [Fact]
    public void AFloatWiderThanTheBandIsClamped()
    {
        Document doc = NewDoc();
        doc.Sections[0].Children.Add(new Paragraph
        {
            Float = FloatSide.Left,
            FloatWidth = BandWidth * 3,
            Runs = { new TextRun { Text = FloatText } },
        });
        AddBody(doc);

        Assert.All(BodyLineLefts(doc), x => Assert.True(x >= Margin - 1 && x < Margin + BandWidth,
            $"a line started at x={x:F1}, outside the band"));
    }

    // ================================================================
    //  The other three exporters
    // ================================================================

    /// <summary>HTML writes the float natively, because the format has the concept.</summary>
    /// <remarks>
    /// <b>The claim in <c>Paragraph.Float</c>'s own documentation, which was false when written.</b>
    /// The doc said the property is "honoured exactly by the PDF exporter and by HTML"; the HTML
    /// exporter did not mention floats at all, and a grep for <c>Float</c> across the three
    /// non-PDF exporters returned nothing. A doc comment asserting behaviour the code does not have
    /// is the defect class this repository keeps finding in its own register, so it is fixed here
    /// rather than weakened.
    /// </remarks>
    [Fact]
    public void TheHtmlExportWritesTheFloatAsCss()
    {
        string html = ToHtml(WithFloat(FloatSide.Right));

        Assert.Contains("float:right", html, StringComparison.Ordinal);
        Assert.Contains("width:200pt", html, StringComparison.Ordinal);
    }

    /// <summary>A document with no float produces no float CSS.</summary>
    /// <remarks>
    /// <b>The acceptance control for every HTML document this library has ever written.</b> A
    /// property emitted unconditionally would change the markup of every existing export.
    /// </remarks>
    [Fact]
    public void TheHtmlExportOfAnUnfloatedDocumentIsUnchanged()
    {
        Assert.DoesNotContain("float:", ToHtml(WithFloat(FloatSide.None)), StringComparison.Ordinal);
    }

    /// <summary>HTML writes <c>clear</c> too, and only when asked.</summary>
    [Fact]
    public void TheHtmlExportWritesClearOnlyWhenSet()
    {
        Document doc = NewDoc();
        doc.Sections[0].Children.Add(new Paragraph
        {
            ClearFloats = true,
            Runs = { new TextRun { Text = "BELOW" } },
        });

        Assert.Contains("clear:both", ToHtml(doc), StringComparison.Ordinal);
        Assert.DoesNotContain("clear:both", ToHtml(WithFloat(FloatSide.None)), StringComparison.Ordinal);
    }

    /// <summary>DOCX and ODT say that they dropped the float, rather than dropping it quietly.</summary>
    /// <remarks>
    /// <b>Also a claim the documentation made before the code did.</b> Neither format gets a float
    /// from this exporter — a real one means a framed or anchored object in each format's own
    /// vocabulary — so a floated paragraph becomes a block and the text that would have run
    /// alongside runs underneath. The two outputs are indistinguishable from a document that never
    /// floated, which is exactly when a caller needs telling.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDocxAndOdtExportsReportThatTheyDroppedTheFloat(bool docx)
    {
        List<string> seen = Approximations(() =>
        {
            Document doc = WithFloat(FloatSide.Left);
            if (docx) doc.ToDocx(); else doc.ToOdt();
        });

        Assert.Contains(seen, d => d.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>...and stay quiet for a document that never floated.</summary>
    /// <remarks>
    /// A channel that fires on ordinary input is one callers learn to ignore, which is the whole
    /// reason this pair is asserted together.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDocxAndOdtExportsAreQuietWithoutAFloat(bool docx)
    {
        List<string> seen = Approximations(() =>
        {
            Document doc = WithFloat(FloatSide.None);
            if (docx) doc.ToDocx(); else doc.ToOdt();
        });

        Assert.DoesNotContain(seen, d => d.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private const string FloatText = "Sidebar";

    private static Document NewDoc()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        section.PageWidthPt = PageWidth;
        section.PageHeightPt = 792;
        section.MarginLeft = Margin;
        section.MarginRight = Margin;
        section.MarginTop = Margin;
        section.MarginBottom = Margin;
        return doc;
    }

    /// <summary>A short float followed by body text long enough to pass its bottom.</summary>
    private static Document WithFloat(FloatSide side)
    {
        Document doc = NewDoc();

        if (side != FloatSide.None)
        {
            doc.Sections[0].Children.Add(new Paragraph
            {
                Float = side,
                FloatWidth = FloatWidth,
                Runs = { new TextRun { Text = FloatText } },
            });
        }

        AddBody(doc);
        return doc;
    }

    private static void AddBody(Document doc, int repeats = 24)
    {
        string text = string.Join(" ",
            Enumerable.Repeat("The quick brown fox jumps over the lazy dog and keeps on running", repeats));

        doc.Sections[0].Children.Add(new Paragraph { Runs = { new TextRun { Text = text } } });
    }

    /// <summary>Every drawn run except the float's own, in stream order.</summary>
    private static List<PdfContentProbe.DrawnText> BodyRuns(Document doc)
        => PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()))
            .Where(t => !string.IsNullOrWhiteSpace(t.Text)
                     && !t.Text.Contains(FloatText, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Index of the first run on the second page, or -1 when the document has one page.
    /// </summary>
    /// <remarks>
    /// <c>DecodeStreams</c> concatenates every content stream in file order, so pages arrive one
    /// after another and a page break shows as the baseline jumping back UP. Grouping by y alone
    /// would merge the two pages' identically-placed lines, which is exactly what the leaked-float
    /// case needs to tell apart.
    /// </remarks>
    private static int PageBoundary(List<PdfContentProbe.DrawnText> runs)
    {
        for (int i = 1; i < runs.Count; i++)
            if (runs[i].Y > runs[i - 1].Y + 100) return i;
        return -1;
    }

    /// <summary>The left edge of each line among the given runs.</summary>
    private static double[] LineLefts(IEnumerable<PdfContentProbe.DrawnText> runs)
    {
        var byBaseline = new Dictionary<int, double>();
        foreach (PdfContentProbe.DrawnText t in runs)
        {
            int key = (int)Math.Round(t.Y);
            byBaseline[key] = byBaseline.TryGetValue(key, out double left)
                ? Math.Min(left, t.X)
                : t.X;
        }
        return byBaseline.OrderByDescending(kv => kv.Key).Select(kv => kv.Value).ToArray();
    }

    /// <summary>First-page lines only, as (left edge, characters drawn).</summary>
    private static List<(double Left, int Chars)> FirstPageLines(Document doc)
    {
        List<PdfContentProbe.DrawnText> runs = BodyRuns(doc);
        int boundary = PageBoundary(runs);
        if (boundary > 0) runs = runs.Take(boundary).ToList();

        var byBaseline = new Dictionary<int, (double Left, int Chars)>();
        foreach (PdfContentProbe.DrawnText t in runs)
        {
            int key = (int)Math.Round(t.Y);
            if (byBaseline.TryGetValue(key, out var e))
                byBaseline[key] = (Math.Min(e.Left, t.X), e.Chars + t.Text.Length);
            else
                byBaseline[key] = (t.X, t.Text.Length);
        }

        return byBaseline.OrderByDescending(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    // ================================================================
    //  Reading the geometry back out
    // ================================================================

    /// <summary>
    /// Every drawn run except the float's own, grouped into lines by baseline.
    /// </summary>
    /// <remarks>
    /// The float's text is excluded <b>by content</b> rather than by position, so the exclusion
    /// keeps working if the float moves — and so that "some line starts past the float" cannot be
    /// satisfied by the float itself, which would make the acceptance test circular.
    /// </remarks>
    private static List<(double Left, double Right)> BodyLines(Document doc)
    {
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        List<PdfContentProbe.DrawnText> drawn = PdfContentProbe.DrawnTexts(content);

        var byBaseline = new Dictionary<int, (double Left, double Right)>();
        foreach (PdfContentProbe.DrawnText t in drawn)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            if (t.Text.Contains(FloatText, StringComparison.Ordinal)) continue;

            // The right edge is approximated by the run's start plus a conservative advance: the
            // probe reports where a run BEGINS, and these tests only ever compare right edges
            // against each other, never against an absolute.
            double right = t.X + (t.Text.Length * 5.0);

            int key = (int)Math.Round(t.Y);
            if (byBaseline.TryGetValue(key, out var e))
                byBaseline[key] = (Math.Min(e.Left, t.X), Math.Max(e.Right, right));
            else
                byBaseline[key] = (t.X, right);
        }

        return byBaseline.OrderByDescending(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    private static double[] BodyLineLefts(Document doc)
        => BodyLines(doc).Select(l => l.Left).ToArray();

    private static double[] BodyLineRights(Document doc)
        => BodyLines(doc).Select(l => l.Right).ToArray();

    private static string ToHtml(Document doc)
        => System.Text.Encoding.UTF8.GetString(doc.ToHtml());

    /// <summary>
    /// The <c>ContentApproximated</c> details reported while <paramref name="export"/> runs, on
    /// this thread only.
    /// </summary>
    /// <remarks>
    /// <b><c>BuilderDiagnostics.OnEvent</c> is a process-wide static event</b>, so an unfiltered
    /// handler turns this assertion into a claim about everything running in the test host — the
    /// coupling that produced real intermittents elsewhere in this repository. Filtering to the
    /// calling thread is the fix the register prescribes; serialising the collection would hide
    /// the coupling rather than remove it.
    /// </remarks>
    private static List<string> Approximations(Action export)
    {
        var seen = new List<string>();
        int thread = Environment.CurrentManagedThreadId;

        void Collect(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != thread) return;
            if (d.Kind == BuilderDiagnosticKind.ContentApproximated) seen.Add(d.Detail);
        }

        BuilderDiagnostics.OnEvent += Collect;
        try { export(); }
        finally { BuilderDiagnostics.OnEvent -= Collect; }

        return seen;
    }
}
