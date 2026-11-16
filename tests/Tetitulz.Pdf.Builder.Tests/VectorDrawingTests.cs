// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="ImageRun.Drawing"/>: a picture of paths drawn in the image's box instead of pixels — in
/// the PDF as path operators at the box's place, filled, stroked or both in one paint (an open path's
/// fill closing it for itself), scaled with the picture when the band is narrower; in HTML as an inline
/// <c>&lt;svg&gt;</c>; left out of DOCX with a report. Its numbers are validated with the model's.
/// </summary>
public class VectorDrawingTests
{
    private static VectorDrawing Vee(string? stroke = "#FF0000", string? fill = null)
    {
        var d = new VectorDrawing { StrokeColor = stroke, StrokeWidthPt = 2, FillColor = fill };
        d.Path.Add(PathCommand.MoveTo(0, 5));
        d.Path.Add(PathCommand.LineTo(10, 20));
        d.Path.Add(PathCommand.CurveTo(15, 20, 20, 10, 20, 0));
        return d;
    }

    private static Document Doc(ImageRun image, AbsolutePlacement? place = null, bool tagged = false)
    {
        var p = new Paragraph { SpacingAfter = 0, SpacingBefore = 0, Placement = place };
        p.Runs.Add(image);
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document { Tagged = tagged };
        doc.Sections.Add(section);
        return doc;
    }

    private static double[] Numbers(Match m, int count)
        => Enumerable.Range(1, count).Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();

    private const string Num = @"(-?[\d.]+)";

    [Fact]
    public void APlacedDrawing_IsItsPathAtTheBoxsPlace_YRunningDown()
    {
        var section = new Section();
        string content = PdfContentProbe.DecodeStreams(Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = Vee() },
            new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 20 }).ToPdf());
        double top = section.PageHeightPt - 100;

        Match move = Regex.Match(content, $@"{Num} {Num} m\s+{Num} {Num} l\s+{Num} {Num} {Num} {Num} {Num} {Num} c");
        Assert.True(move.Success, "no m … l … c path in the content");
        double[] n = Numbers(move, 10);
        Assert.Equal(100, n[0], 2); Assert.Equal(top - 5, n[1], 2);       // (0, 5): five points below the box's top
        Assert.Equal(110, n[2], 2); Assert.Equal(top - 20, n[3], 2);      // (10, 20) twenty points down
        Assert.Equal(new[] { 115, top - 20, 120, top - 10, 120, top }, n.Skip(4).ToArray());
        // Stroked alone: its colour, its width, S.
        Assert.Matches(@"1 0 0 RG\s+2 w", content);
        Assert.Matches(@"c\s+S\s+Q", content);
    }

    [Theory]
    [InlineData("#FF0000", null, "S")]
    [InlineData(null, "#00FF00", "f")]
    [InlineData("#FF0000", "#00FF00", "B")]
    public void TheDrawingsPaint_IsOneOperator_ForItsStrokeAndFill(string? stroke, string? fill, string op)
    {
        string content = PdfContentProbe.DecodeStreams(Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = Vee(stroke, fill) }).ToPdf());
        Match paint = Regex.Match(content, $@"{Num} {Num} m\s+{Num} {Num} l\s+{Num} {Num} {Num} {Num} {Num} {Num} c\s+(\S+)\s+Q");
        Assert.True(paint.Success, "the path's paint");
        Assert.Equal(op, paint.Groups[11].Value);
        Assert.Equal(fill != null, content.Contains("0 1 0 rg", StringComparison.Ordinal));
    }

    [Fact]
    public void ADrawingWithNoPaint_OrNoPath_DrawsNothing_ButKeepsItsRoom()
    {
        string none = PdfContentProbe.DecodeStreams(Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = Vee(null, null) }).ToPdf());
        Assert.DoesNotMatch(@" m\s", none);
        string empty = PdfContentProbe.DecodeStreams(Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = new VectorDrawing() }).ToPdf());
        Assert.DoesNotMatch(@" m\s", empty);

        // The room is kept: text after it starts as far down as after a drawing that paints.
        Document With(VectorDrawing d)
        {
            var doc = Doc(new ImageRun { WidthPt = 20, HeightPt = 60, Drawing = d });
            var after = new Paragraph();
            after.Runs.Add(new TextRun { Text = "AFTER" });
            doc.Sections[0].Children.Add(after);
            return doc;
        }
        double painted = PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(With(Vee()).ToPdf()), "AFTER");
        double unpainted = PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(With(new VectorDrawing()).ToPdf()), "AFTER");
        Assert.Equal(painted, unpainted, 2);
        var noDrawing = Doc(new ImageRun { WidthPt = 20, HeightPt = 60 });
        noDrawing.Sections[0].Children.Add(new Paragraph { Runs = { new TextRun { Text = "AFTER" } } });
        Assert.True(PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(noDrawing.ToPdf()), "AFTER") > painted + 50,
                    "an image with neither pixels, box nor drawing takes no room");
    }

    /// <summary>A drawing wider than the band shrinks with the picture: a 1 000 pt wide path's far end lands at the band's right.</summary>
    [Fact]
    public void ADrawingWiderThanTheBand_IsScaledWithIt()
    {
        var section = new Section();
        double band = section.PageWidthPt - section.MarginLeft - section.MarginRight;
        var wide = new VectorDrawing { StrokeWidthPt = 10 };
        wide.Path.Add(PathCommand.MoveTo(0, 0));
        wide.Path.Add(PathCommand.LineTo(1000, 100));
        string content = PdfContentProbe.DecodeStreams(Doc(new ImageRun { WidthPt = 1000, HeightPt = 100, Drawing = wide }).ToPdf());
        Match m = Regex.Match(content, $@"{Num} w\s+{Num} {Num} m\s+{Num} {Num} l");
        Assert.True(m.Success, "the scaled path");
        double[] n = Numbers(m, 5);
        double scale = band / 1000;
        Assert.Equal(section.MarginLeft + band, n[3], 1);
        Assert.Equal(100 * scale, n[2] - n[4], 1);
        Assert.Equal(10 * scale, n[0], 2);                                  // the stroke by the same scale
    }

    [Fact]
    public void ADecorativeDrawing_IsAnArtifact_AndAnotherAFigureWithItsAlt()
    {
        string decorative = PdfContentProbe.DecodeStreams(Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = Vee(), IsDecorative = true }, tagged: true).ToPdf());
        Assert.Matches(@"/Artifact[^\n]*BDC\s+q\s+1 0 0 RG", decorative);
        byte[] figure = Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = Vee(), Alt = "a vee" }, tagged: true).ToPdf();
        Assert.Matches(@"/Figure <</MCID \d+>> BDC\s+q\s+1 0 0 RG", PdfContentProbe.DecodeStreams(figure));
        Assert.Contains("(a vee)", PdfContentProbe.Raw(figure) + PdfContentProbe.DecodeStreams(figure), StringComparison.Ordinal);
    }

    [Fact]
    public void ANonFiniteCoordinate_IsRefused_ByItsPath()
    {
        var bad = Vee();
        bad.Path.Add(PathCommand.LineTo(double.NaN, 0));
        var ex = Assert.ThrowsAny<ArgumentException>(() => Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = bad }).ToPdf());
        Assert.Contains("Drawing.Path[3].X", ex.Message, StringComparison.Ordinal);

        var wide = Vee();
        wide.StrokeWidthPt = -1;
        var ex2 = Assert.ThrowsAny<ArgumentException>(() => Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = wide }).ToPdf());
        Assert.Contains("Drawing.StrokeWidthPt", ex2.Message, StringComparison.Ordinal);

        Assert.Matches(@" m\s", PdfContentProbe.DecodeStreams(Doc(new ImageRun { WidthPt = 20, HeightPt = 20, Drawing = Vee() }).ToPdf()));
    }

    [Fact]
    public void InHtml_ADrawingIsAnInlineSvg()
    {
        string html = Encoding.UTF8.GetString(Doc(new ImageRun { WidthPt = 20, HeightPt = 30, Drawing = Vee(fill: "#00FF00"), IsDecorative = true }).ToHtml());
        Match svg = Regex.Match(html, "<svg [^>]*width=\"20pt\" height=\"30pt\" viewBox=\"0 0 20 30\"[^>]*aria-hidden=\"true\"[^>]*><path d=\"([^\"]*)\" fill=\"([^\"]*)\" stroke=\"([^\"]*)\" stroke-width=\"([^\"]*)\"/></svg>");
        Assert.True(svg.Success, "no inline svg: " + html);
        Assert.Equal("M0 5 L10 20 C15 20 20 10 20 0", svg.Groups[1].Value);
        Assert.Equal(new[] { "#00FF00", "#FF0000", "2" }, new[] { svg.Groups[2].Value, svg.Groups[3].Value, svg.Groups[4].Value });
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);

        string labelled = Encoding.UTF8.GetString(Doc(new ImageRun { WidthPt = 20, HeightPt = 30, Drawing = Vee(), Alt = "a \"vee\"" }).ToHtml());
        Assert.Contains("role=\"img\" aria-label=\"a &quot;vee&quot;\"", labelled, StringComparison.Ordinal);
    }

    [Fact]
    public void InDocxAndOdt_ADrawingIsLeftOut_AndSaysSo()
    {
        foreach (Func<Document, byte[]> export in new Func<Document, byte[]>[] { d => d.ToDocx(), d => d.ToOdt() })
        {
            export(Doc(new ImageRun { WidthPt = 20, HeightPt = 30, Drawing = Vee() }));
            BuilderDiagnostic? last = BuilderDiagnostics.LastError;
            Assert.NotNull(last);
            Assert.Equal(BuilderDiagnosticKind.UnsupportedByExportFormat, last!.Kind);
            Assert.StartsWith("an inline vector drawing was dropped", last.Detail, StringComparison.Ordinal);
        }
    }
}
