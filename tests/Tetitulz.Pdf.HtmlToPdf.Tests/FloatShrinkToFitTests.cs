// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A <c>float</c> with no declared width shrinks to its content — CSS shrink-to-fit — instead of
/// falling back to the two-cell band.
/// </summary>
/// <remarks>
/// <para><b>The fallback was argued from a pass the mapper did not run.</b> "CSS shrink-to-fit
/// needs a measurement pass" was true and was not a reason: the Builder's layout measures every
/// line it draws, and <c>PdfExporter.MeasureShrinkToFitWidth</c> hands the mapper that measure for
/// one paragraph. The width it answers is the paragraph's widest unwrapped line, capped at the
/// band — min(max-content, available), which is what CSS computes.</para>
///
/// <para><b>Measured glyph positions, as every float test in this suite.</b> A band and a real
/// float put the first line of the beside-text at the same place; only where the beside-text
/// STARTS — a few points past a short word, rather than a column's width away — and where it goes
/// once past the float's bottom separates the two.</para>
/// </remarks>
public class FloatShrinkToFitTests
{
    /// <summary>
    /// The beside-text starts just past the float's own text, not a declared column away.
    /// </summary>
    /// <remarks>
    /// <para>Compared against the same markup with <c>width:100pt</c>, so the number it is measured
    /// against comes from the layout engine: "LOGO" at 12 pt is a fraction of 100 pt wide, and a
    /// mapper that reserved the fallback's column or any fixed width would put ADDRESS well past
    /// it.</para>
    /// <para>The float carries a <c>margin-right</c>, as an author's would. Without one the
    /// shrink-to-fit float ends half a point before ADDRESS begins — exactly as CSS would place
    /// it — and the structured-text extractor reads the abutting glyphs as ONE word, so both
    /// lookups return the same word and the test measures nothing.</para>
    /// </remarks>
    [Fact]
    public void AFloatWithNoDeclaredWidth_ShrinksToItsContent()
    {
        var measured = Positions("<div><div style='float:left;margin-right:12pt'>LOGO</div><div>ADDRESS</div></div>");
        var declared = Positions("<div><div style='float:left;width:100pt;margin-right:12pt'>LOGO</div><div>ADDRESS</div></div>");

        Assert.True(Math.Abs(measured.A.Y - measured.B.Y) < 2.0,
            $"LOGO at y={measured.A.Y:F1} and ADDRESS at y={measured.B.Y:F1}: the float did not form");

        double shrunk = measured.B.X - measured.A.X;
        double column = declared.B.X - declared.A.X;

        Assert.True(shrunk > 20, $"ADDRESS at {shrunk:F1} pt past LOGO overlaps the float's text");
        Assert.True(shrunk < column - 30,
            $"ADDRESS starts {shrunk:F1} pt past LOGO with no width declared and {column:F1} pt with " +
            "width:100pt; the float did not shrink to its content");
    }

    /// <summary>
    /// The measured float is a REAL float: text beside it returns to the full measure below it.
    /// </summary>
    /// <remarks>
    /// The property the two-cell band can never have, asserted the way <c>FloatLayoutTests</c>
    /// asserts it for a declared width: some line starts clear of the float, and some later line
    /// starts back at the margin.
    /// </remarks>
    [Fact]
    public void TextBesideAMeasuredFloat_ReturnsToTheFullMeasureBelowIt()
    {
        string body = string.Join(" ",
            Enumerable.Repeat("The quick brown fox jumps over the lazy dog and keeps on running", 20));

        List<PdfTextWord> words = Words(
            $"<div><div style='float:left'>LOGOTYPE</div><div>{body}</div></div>");

        var lineLefts = words
            .Where(w => !w.Text.Contains("LOGOTYPE", StringComparison.Ordinal))
            .GroupBy(w => (int)Math.Round(w.Y))
            .Select(g => g.Min(w => w.X))
            .ToList();

        Assert.True(lineLefts.Count >= 4, $"expected several lines, got {lineLefts.Count}");

        double leftmost = lineLefts.Min();
        Assert.Contains(lineLefts, x => x > leftmost + 30);
        Assert.Contains(lineLefts, x => x < leftmost + 5);
    }

    /// <summary>A float whose content is wider than the band takes the band, and the text goes below.</summary>
    /// <remarks>
    /// CSS's shrink-to-fit is capped at the available width, so a float of running text is as wide
    /// as its container and nothing flows beside it. Asserted on the beside-text's Y: below every
    /// line of the float, not alongside its first.
    /// </remarks>
    [Fact]
    public void AFloatWiderThanTheBand_TakesTheBandAndPutsTheTextBelow()
    {
        string wide = string.Join(" ", Enumerable.Repeat("WIDE", 60));

        List<PdfTextWord> words = Words(
            $"<div><div style='float:left'>{wide}</div><div>ADDRESS</div></div>");

        PdfTextWord address = Find(words, "ADDRESS");
        double floatBottom = words.Where(w => w.Text.Contains("WIDE", StringComparison.Ordinal)).Min(w => w.Y);

        Assert.True(address.Y < floatBottom,
            $"ADDRESS at y={address.Y:F1} sits beside a float whose last line is at y={floatBottom:F1}");
    }

    /// <summary>The model carries the measured width, so an HTML or DOCX export of it sees a float.</summary>
    /// <remarks>
    /// <b>The liveness seam.</b> A band and a measured float can draw the same first line, and a
    /// float measured to the wrong width still draws SOME layout. Only the model says whether
    /// <c>FloatWidth</c> was set, and to what: "LOGO" in 12 pt Helvetica is 34.7 pt of advances,
    /// so the width must land near that and nowhere near the 100 pt a declared column would be.
    /// </remarks>
    [Fact]
    public void TheModelCarriesTheMeasuredWidth()
    {
        Document model = HtmlToBuilderMapper.MapToModel(
            "<div><div style='float:left'>LOGO</div><div>ADDRESS</div></div>",
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        Paragraph floated = model.Sections[0].Children.OfType<Paragraph>()
            .Single(p => p.Float == FloatSide.Left);

        Assert.InRange(floated.FloatWidth, 30, 45);
    }

    /// <summary>A float with nothing to measure still falls back, and still says so.</summary>
    /// <remarks>
    /// An empty floated block has no line and no image, so the measurer answers zero and the band
    /// — with its report — is the honest result. The acceptance control for the measurement: a
    /// mapper that reserved some default width here would report nothing and draw an invisible
    /// column.
    /// </remarks>
    [Fact]
    public void AFloatWithNothingToMeasure_StillFallsBackAndReports()
    {
        var seen = new List<string>();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(
            "<div><div style='float:left'></div><div>ADDRESS</div></div>", output,
            new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                OnDiagnostic = d =>
                {
                    if (d.Kind == Tetitulz.Pdf.Builder.Export.BuilderDiagnosticKind.ContentApproximated)
                        seen.Add(d.Detail);
                },
            });

        Assert.Contains(seen, d => d.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every float that has nothing to measure falls back, whichever guard catches it.
    /// </summary>
    /// <remarks>
    /// <b>All of these leave through the "not a single paragraph" guard, and that is the finding.</b>
    /// An empty element, one holding only whitespace, one holding only a zero-sized image and one
    /// holding a zero-width space all map to <i>no</i> block at all, so none of them reaches the
    /// mapper's second check, the one that refuses a measurement of zero. That check is therefore
    /// unreachable from HTML today — see the comment at it — and this theory is what says so: it
    /// pins the outcome a caller can observe for every shape of "nothing", so a mapper change that
    /// started producing an empty paragraph here would have to answer to it.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("<span></span>")]
    [InlineData("&#8203;")]
    public void AFloatWithNothingOfAnyShape_FallsBackAndCreatesNoFloat(string inner)
    {
        string html = $"<div><div style='float:left'>{inner}</div><div>ADDRESS</div></div>";

        Document model = HtmlToBuilderMapper.MapToModel(html,
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        // A zero-width float would draw an invisible column and report nothing.
        Assert.DoesNotContain(
            model.Sections[0].Children.OfType<Paragraph>(),
            p => p.Float != FloatSide.None);
    }

    // ------------------------------------------------------------------

    private static ((double X, double Y) A, (double X, double Y) B) Positions(string html)
    {
        var words = Words(html);
        var a = Find(words, "LOGO");
        var b = Find(words, "ADDRESS");
        return ((a.X, a.Y), (b.X, b.Y));
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

        using var input = new MemoryStream(output.ToArray(), writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
