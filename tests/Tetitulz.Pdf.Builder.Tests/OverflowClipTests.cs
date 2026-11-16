// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.ClipsContent"/> — CSS <c>overflow</c> on a box of blocks: what the box holds
/// is clipped to its padding box, rounded by its corners' inner curve, so a <c>.progress</c> track
/// clips the square bar inside it. The clip moves with the box's paint to where the fragment began
/// and is restored after its content, one fragment per page.
/// </summary>
public class OverflowClipTests
{
    private static string Render(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static Paragraph Text(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static (double X, double Y, double W, double H)[] Rects(string c) =>
        Regex.Matches(c, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\n").Cast<Match>()
             .Select(m => (D(m, 1), D(m, 2), D(m, 3), D(m, 4))).ToArray();

    private static double D(Match m, int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// The clip is the padding box — the box less its 2pt border — set before the box's content
    /// and restored after it; a box that does not clip sets none (the control).
    /// </summary>
    [Fact]
    public void TheContentIsClippedToThePaddingBox()
    {
        var border = new Border(2, "#000000");
        var box = new BoxStart { Shading = "#0000FF", ClipsContent = true, BorderTop = border, BorderRight = border, BorderBottom = border, BorderLeft = border };
        string c = Render(box, Text("Inside"), new BoxEnd());

        int clip = Regex.Match(c, @"re\s+W\s+n").Index;
        int text = c.IndexOf("BT", System.StringComparison.Ordinal);
        Assert.True(clip > 0 && clip < text, "the clip is not set before the box's content");
        Assert.Matches(@"ET[\s\S]*Q\s*$", c.TrimEnd());

        var r = Rects(c);
        var fill = r[0];
        var pad = r[1];
        Assert.Equal(fill.X + 2, pad.X, 2);
        Assert.Equal(fill.Y + 2, pad.Y, 2);
        Assert.Equal(fill.W - 4, pad.W, 2);
        Assert.Equal(fill.H - 4, pad.H, 2);

        box.ClipsContent = false;
        Assert.DoesNotMatch(@"W\s+n", Render(box, Text("Inside"), new BoxEnd()));
    }

    /// <summary>A rounded box clips to its corners' inner curve: four curves of its 6pt radius, with no border.</summary>
    [Fact]
    public void ARoundedBoxClipsToItsCurve()
    {
        string c = Render(new BoxStart { Shading = "#0000FF", ClipsContent = true, CornerRadiusPt = 6 }, Text("Inside"), new BoxEnd());
        string clipPath = c.Substring(0, Regex.Match(c, @"W\s+n").Index);
        clipPath = clipPath.Substring(clipPath.LastIndexOf(" m\n", System.StringComparison.Ordinal));
        Assert.Equal(4, Regex.Matches(clipPath, @"\bc\n").Count);
    }

    /// <summary>
    /// Under a border the curve is the inner one — the radius less the border (CSS Backgrounds 3
    /// §5.2): 6pt round a 2pt border clips at 4pt.
    /// </summary>
    [Fact]
    public void ABorderedBoxClipsToItsInnerCurve()
    {
        var border = new Border(2, "#000000");
        string c = Render(new BoxStart { ClipsContent = true, CornerRadiusPt = 6, BorderTop = border, BorderRight = border, BorderBottom = border, BorderLeft = border },
                          Text("Inside"), new BoxEnd());
        string head = c.Substring(0, Regex.Match(c, @"W\s+n").Index);
        int move = head.LastIndexOf(" m\n", System.StringComparison.Ordinal);
        head = head.Substring(head.LastIndexOf('\n', move) + 1);   // from the clip path's move-to line
        var extents = new List<double>();
        double px = 0, py = 0;
        foreach (Match m in Regex.Matches(head, @"((?:[-\d.]+ )+)(m|l|c)\n"))
        {
            double[] v = m.Groups[1].Value.Trim().Split(' ').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
            if (m.Groups[2].Value == "c") extents.Add(System.Math.Abs(v[v.Length - 1] - py));
            px = v[v.Length - 2]; py = v[v.Length - 1];
        }
        Assert.Equal(4, extents.Count);
        Assert.All(extents, e => Assert.Equal(4, e, 2));
    }

    /// <summary>A box with nothing to paint still clips: the clip is the box's whole effect.</summary>
    [Fact]
    public void ABoxWithNothingToPaintStillClips()
        => Assert.Matches(@"re\s+W\s+n", Render(new BoxStart { ClipsContent = true }, Text("Inside"), new BoxEnd()));

    /// <summary>
    /// A box broken across pages clips each fragment on its own page — one clip a page — and every
    /// graphics state it saves is restored.
    /// </summary>
    [Fact]
    public void EachFragmentIsClipped()
    {
        var blocks = new List<IDocElement> { new BoxStart { ClipsContent = true } };
        for (int i = 0; i < 60; i++) blocks.Add(Text("Line " + i));
        blocks.Add(new BoxEnd());
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 2, "the box did not break, so this proves nothing");

        string c = PdfContentProbe.DecodeStreams(pdf);
        Assert.Equal(pages, Regex.Matches(c, @"re\s+W\s+n").Count);
        Assert.Equal(Regex.Matches(c, @"(?m)^q$").Count, Regex.Matches(c, @"(?m)^Q$").Count);
    }
}
