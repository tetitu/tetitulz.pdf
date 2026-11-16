// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxShadow.Inset"/> and <see cref="Paragraph.MoreShadows"/>: a shadow cast inside the
/// box, over its background and under its border and content (CSS Backgrounds 3 §7.1), and every
/// shadow of a list — where an inset one dropped the whole declaration and only the first of a
/// list was drawn.
/// </summary>
public class InsetShadowTests
{
    private static string Render(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static Paragraph Shaded(params BoxShadow[] shadows)
    {
        var p = new Paragraph { Shading = "#0000FF", Shadow = shadows.FirstOrDefault() };
        p.MoreShadows.AddRange(shadows.Skip(1));
        p.Runs.Add(new TextRun { Text = "Inside" });
        return p;
    }

    private static (double X, double Y, double W, double H)[] Rects(string c) =>
        Regex.Matches(c, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\n").Cast<Match>()
             .Select(m => (D(m, 1), D(m, 2), D(m, 3), D(m, 4))).ToArray();

    private static double D(Match m, int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

    /// <summary>Painted bottom first — the reverse of CSS's list — drop shadows and inset ones apart.</summary>
    [Fact]
    public void ShadowsArePaintedInReverseAndSortedByKind()
    {
        var a = new BoxShadow { Color = "#000001" };
        var b = new BoxShadow { Color = "#000002", Inset = true };
        var c = new BoxShadow { Color = "#000003" };
        var d = new BoxShadow { Color = "#000004" };
        var more = new List<BoxShadow> { b, c, d };
        Assert.Equal(new[] { d, c, a }, PdfExporter.PaintOrder(a, more, inset: false));
        Assert.Equal(new[] { b }, PdfExporter.PaintOrder(a, more, inset: true));
    }

    /// <summary>
    /// A hard inset shadow is drawn over the background: clipped to the padding box, then to all
    /// but its hole — the padding box shrunk by the 4pt spread and moved by the (2, 3) offset, down
    /// for a positive y — and the padding box filled in its colour.
    /// </summary>
    [Fact]
    public void AHardInsetShadowIsThePaddingBoxLessItsHole()
    {
        string c = Render(Shaded(new BoxShadow { Inset = true, Color = "#FF0000", SpreadPt = 4, OffsetXPt = 2, OffsetYPt = 3 }));
        int blue = c.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        int red = c.IndexOf("1 0 0 rg", System.StringComparison.Ordinal);
        Assert.True(blue >= 0 && red > blue, "the inset shadow is not over the background");
        Assert.Matches(@"W\s+n[\s\S]*W\*\s+n", c.Substring(blue, red - blue));

        var r = Rects(c.Substring(blue));
        var fill = r[0];           // the background
        var hole = r[3];           // after the padding clip and the surrounding rectangle
        Assert.Equal(fill.X + 4 + 2, hole.X, 2);
        Assert.Equal(fill.Y + 4 - 3, hole.Y, 2);
        Assert.Equal(fill.W - 8, hole.W, 2);
        Assert.Equal(fill.H - 8, hole.H, 2);
    }

    /// <summary>
    /// A blurred inset shadow is an image over the padding box whose mask is dark at the box's edge
    /// and clear at its middle — the blurred hole's inverse; a drop shadow's mask is the other way
    /// round (the control).
    /// </summary>
    [Fact]
    public void ABlurredInsetShadowsMaskIsDarkAtTheEdge()
    {
        static byte[] Mask(BoxShadow s, out int width, out int height)
        {
            var section = new Section();
            section.Children.Add(Shaded(s));
            var doc = new Document();
            doc.Sections.Add(section);
            using var pdf = PdfDocument.Open(new MemoryStream(doc.ToPdf()));
            var xobjects = Assert.IsType<PdfDictionary>(pdf.ResolveObject(pdf.Pages[0].Resources[PdfName.XObject]));
            var image = Assert.IsType<PdfStream>(pdf.ResolveObject(xobjects[Assert.Single(xobjects.Keys)]));
            width = ((PdfNumber)image[PdfName.Width]).IntValue;
            height = ((PdfNumber)image[PdfName.Height]).IntValue;
            return Assert.IsType<PdfStream>(pdf.ResolveObject(image[PdfName.SMask])).GetDecodedData();
        }
        byte[] inset = Mask(new BoxShadow { Inset = true, Color = "#FF0000", BlurPt = 6 }, out int w, out int h);
        Assert.InRange(inset[(h / 2) * w + w / 2], 0, 5);       // the middle: clear
        Assert.InRange(inset[(h / 2) * w + 0], 100, 255);       // the edge: shadowed

        byte[] drop = Mask(new BoxShadow { Color = "#FF0000", BlurPt = 6 }, out int dw, out int dh);
        Assert.InRange(drop[(dh / 2) * dw + dw / 2], 250, 255);
    }

    /// <summary>Every drop shadow of a list is drawn, under the background, the first on top.</summary>
    [Fact]
    public void EveryDropShadowOfAListIsDrawn()
    {
        string c = Render(Shaded(new BoxShadow { Color = "#FF0000", OffsetXPt = 2 }, new BoxShadow { Color = "#00FF00", OffsetXPt = 6 }));
        int green = c.IndexOf("0 1 0 rg", System.StringComparison.Ordinal);
        int red = c.IndexOf("1 0 0 rg", System.StringComparison.Ordinal);
        int blue = c.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        Assert.True(green >= 0 && green < red && red < blue, $"order green {green}, red {red}, blue {blue}");
    }

    /// <summary>
    /// A box of blocks casts an inset shadow inside its borders: the padding box is the box less its
    /// 2pt border each side — the clip's first rectangle.
    /// </summary>
    [Fact]
    public void ABoxsInsetShadowIsInsideItsBorders()
    {
        var border = new Border(2, "#000000");
        var box = new BoxStart
        {
            Shading = "#0000FF", BorderTop = border, BorderRight = border, BorderBottom = border, BorderLeft = border,
            Shadow = new BoxShadow { Inset = true, Color = "#FF0000", SpreadPt = 3 },
        };
        var a = new Paragraph();
        a.Runs.Add(new TextRun { Text = "A" });
        string c = Render(box, a, new BoxEnd());
        int blue = c.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        var r = Rects(c.Substring(blue));
        var fill = r[0];
        var pad = r[1];
        Assert.Equal(fill.X + 2, pad.X, 2);
        Assert.Equal(fill.W - 4, pad.W, 2);
        Assert.Equal(fill.H - 4, pad.H, 2);
        Assert.True(c.IndexOf("1 0 0 rg", System.StringComparison.Ordinal) > blue, "the inset shadow is not over the box's background");
    }

    /// <summary>
    /// A paragraph's border is stroked along its fill's edge, so its inset shadow's padding box is
    /// the fill less half the 2pt border each side.
    /// </summary>
    [Fact]
    public void AParagraphsInsetShadowIsHalfABorderInside()
    {
        var border = new Border(2, "#000000");
        var p = Shaded(new BoxShadow { Inset = true, Color = "#FF0000", SpreadPt = 3 });
        p.BorderTop = p.BorderRight = p.BorderBottom = p.BorderLeft = border;
        string c = Render(p);
        var r = Rects(c.Substring(c.IndexOf("0 0 1 rg", System.StringComparison.Ordinal)));
        Assert.Equal(r[0].X + 1, r[1].X, 2);
        Assert.Equal(r[0].W - 2, r[1].W, 2);
        Assert.Equal(r[0].H - 2, r[1].H, 2);
    }

    /// <summary>
    /// A box continued past a page break has no top border on its later fragment, so the inset
    /// shadow there reaches the fragment's top — its padding box starts where the fill does —
    /// while the bottom, the box's real edge, is inset by the border.
    /// </summary>
    [Fact]
    public void AContinuedFragmentsInsetShadowReachesTheCut()
    {
        var border = new Border(4, "#000000");
        var blocks = new List<IDocElement>
        {
            new BoxStart
            {
                Shading = "#0000FF", BorderTop = border, BorderRight = border, BorderBottom = border, BorderLeft = border,
                Shadow = new BoxShadow { Inset = true, Color = "#FF0000", SpreadPt = 3 },
            },
        };
        for (int i = 0; i < 60; i++)
        {
            var line = new Paragraph();
            line.Runs.Add(new TextRun { Text = "Line " + i });
            blocks.Add(line);
        }
        blocks.Add(new BoxEnd());
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        Assert.True(PdfContentProbe.CountPages(pdf) >= 2, "the box did not break, so this proves nothing");

        string c = PdfContentProbe.DecodeStreams(pdf);
        var r = Rects(c.Substring(c.LastIndexOf("0 0 1 rg", System.StringComparison.Ordinal)));
        var fill = r[0];
        var pad = r[1];
        Assert.Equal(fill.Y + fill.H, pad.Y + pad.H, 2);   // the cut: no border
        Assert.Equal(fill.Y + 4, pad.Y, 2);                // the real bottom edge: the border
    }
}
