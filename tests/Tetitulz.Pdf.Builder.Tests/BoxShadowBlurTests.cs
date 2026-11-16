// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

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
/// <see cref="BoxShadow.BlurPt"/> and <see cref="BoxShadow.SpreadPt"/> (batch 88): a blurred shadow is
/// an RGB image of the shadow's colour under a soft mask — the box's coverage through a Gaussian of
/// half the radius — reaching one radius past the box; a spread grows the box on every side; a hard
/// shadow stays the filled path it was. One drawing serves a paragraph's box and a block box.
/// </summary>
/// <remarks>
/// The mask is read back from the file and sampled: full inside the box, half at its edge, near
/// nothing at the image's edge — the three values a Gaussian of the stated deviation gives and a
/// hard edge, a wider kernel or none would not.
/// </remarks>
public class BoxShadowBlurTests
{
    private static Paragraph Shadowed(BoxShadow shadow, double radius = 0)
    {
        var p = new Paragraph { Shading = "#0000FF", Shadow = shadow, CornerRadiusPt = radius };
        p.Runs.Add(new TextRun { Text = "Inside" });
        return p;
    }

    private static byte[] Pdf(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc.ToPdf();
    }

    /// <summary>The <c>x y w h re</c> filled in red — the hard shadow's path.</summary>
    private static double[] RedRect(string content)
    {
        string[] lines = content.Split('\n').Select(l => l.Trim()).ToArray();
        int red = System.Array.FindIndex(lines, l => Regex.IsMatch(l, @"^1(\.0+)? 0(\.0+)? 0(\.0+)? rg$"));
        Assert.True(red >= 0, "no fill in the shadow's colour");
        string re = lines.Skip(red).First(l => l.EndsWith(" re", System.StringComparison.Ordinal));
        return re.Split(' ').Take(4).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static (PdfStream Image, PdfStream Mask, int Width, int Height) ShadowImage(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]));
        var image = Assert.IsType<PdfStream>(doc.ResolveObject(xobjects[Assert.Single(xobjects.Keys)]));
        var mask = Assert.IsType<PdfStream>(doc.ResolveObject(image[PdfName.SMask]));
        return (image, mask, ((PdfNumber)image[PdfName.Width]).IntValue, ((PdfNumber)image[PdfName.Height]).IntValue);
    }

    /// <summary>
    /// A blurred shadow is drawn as one image, before the fill it sits under, one radius past the
    /// hard shadow's box on every side — and no path is filled in its colour.
    /// </summary>
    [Fact]
    public void ABlurredShadowIsASoftMaskedImageBehindTheFill_OneRadiusPastTheBox()
    {
        string hard = PdfContentProbe.DecodeStreams(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000" })));
        string blurred = PdfContentProbe.DecodeStreams(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 })));

        double[] rect = RedRect(hard);
        var draw = Assert.Single(ParagraphBoxTests.ImageDraws(blurred));
        Assert.Equal(rect[0] - 6, draw.x, 2);
        Assert.Equal(rect[1] - 6, draw.y, 2);
        Assert.Equal(rect[2] + 12, draw.w, 2);
        Assert.Equal(rect[3] + 12, draw.h, 2);
        Assert.True(blurred.IndexOf(" Do", System.StringComparison.Ordinal) < blurred.IndexOf("0 0 1 rg", System.StringComparison.Ordinal), "the image is not behind the fill");
        Assert.DoesNotContain(blurred.Split('\n'), l => Regex.IsMatch(l.Trim(), @"^1(\.0+)? 0(\.0+)? 0(\.0+)? rg$"));
    }

    /// <summary>
    /// The mask is the box through a Gaussian of half the radius, at two pixels a point: inside the
    /// box it is full, at the box's edge half, and at the image's edge — one radius, two deviations,
    /// out — under 4 %. The image is the shadow's colour throughout, and its corner, which the box's
    /// rounding leaves uncovered, is lighter than a square box's.
    /// </summary>
    [Fact]
    public void TheMaskIsTheBoxThroughAGaussianOfHalfTheRadius()
    {
        var (image, mask, width, height) = ShadowImage(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 })));
        byte[] alpha = mask.GetDecodedData();
        byte[] rgb = image.GetDecodedData();
        byte[] rounded = ShadowImage(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 }, radius: 10))).Mask.GetDecodedData();

        int reach = 12;   // one radius at two pixels a point
        int midY = height / 2;
        Assert.Equal(width * height, alpha.Length);
        Assert.InRange(alpha[midY * width + width / 2], 250, 255);          // inside
        Assert.InRange(alpha[midY * width + reach], 115, 140);              // the box's edge: half
        Assert.InRange(alpha[midY * width + 0], 0, 10);                     // the image's edge: the tail
        Assert.Equal(new byte[] { 255, 0, 0 }, rgb.Take(3).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0 }, rgb.Skip(rgb.Length - 3).ToArray());
        int corner = reach * width + reach;
        Assert.True(rounded[corner] < alpha[corner] - 20, $"the rounded box's corner ({rounded[corner]}) is not lighter than the square's ({alpha[corner]})");
    }

    /// <summary>
    /// A blurred shadow's corners are the box's own: rounded only at the top left, its mask is lighter
    /// at that corner than at the other three, which are alike — where one radius, the largest,
    /// rounded all four the same. A uniform radius leaves the four alike (the control).
    /// </summary>
    [Fact]
    public void ABlurredShadowRoundsEachCornerByItsOwnRadius()
    {
        static (int TopLeft, int TopRight, int BottomRight, int BottomLeft, int Middle) Corners(Paragraph p)
        {
            var (_, mask, width, height) = ShadowImage(Pdf(p));
            byte[] a = mask.GetDecodedData();
            int reach = 12;   // one radius at two pixels a point: the box's own corner
            return (a[reach * width + reach], a[reach * width + width - 1 - reach],
                    a[(height - 1 - reach) * width + width - 1 - reach], a[(height - 1 - reach) * width + reach],
                    a[height / 2 * width + width / 2]);
        }
        var shadow = new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 };
        var one = Shadowed(shadow);
        one.CornerRadiiPt = new double[] { 10, 0, 0, 0 };
        var c = Corners(one);
        var u = Corners(Shadowed(shadow, radius: 10));

        Assert.True(c.TopLeft < c.BottomRight - 20, $"the rounded top-left ({c.TopLeft}) is not lighter than the square bottom-right ({c.BottomRight})");
        Assert.InRange(c.TopRight, c.BottomRight - 2, c.BottomRight + 2);
        Assert.InRange(c.BottomLeft, c.BottomRight - 2, c.BottomRight + 2);
        Assert.InRange(u.TopLeft, u.BottomRight - 2, u.BottomRight + 2);
        Assert.InRange(c.TopLeft, u.TopLeft - 2, u.TopLeft + 2);   // the rounded corner is rounded as a uniform 10 rounds it
        Assert.InRange(c.Middle, 250, 255);                          // and the box between its corners is covered
        Assert.InRange(u.Middle, 250, 255);
    }

    /// <summary>
    /// A blurred shadow's elliptical corner is an ellipse in the mask: a corner 10 across and 2 down
    /// covers more of its corner than a circle of 10 and less than a square one, at the box's corner.
    /// </summary>
    [Fact]
    public void ABlurredShadowsEllipticalCornerIsBetweenACircleAndASquare()
    {
        static int Corner(double[]? radii, double[]? radiiY)
        {
            var p = Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 });
            p.CornerRadiiPt = radii;
            p.CornerRadiiYPt = radiiY;
            var (_, mask, width, _) = ShadowImage(Pdf(p));
            return mask.GetDecodedData()[12 * width + 12];
        }
        int circle = Corner(new double[] { 10, 10, 10, 10 }, null);
        int ellipse = Corner(new double[] { 10, 10, 10, 10 }, new double[] { 2, 2, 2, 2 });
        int square = Corner(null, null);

        Assert.True(circle + 10 < ellipse && ellipse + 10 < square, $"circle {circle}, ellipse {ellipse}, square {square}");
    }

    /// <summary>
    /// A spread grows the hard shadow's box on every side — 3 each way is 6 wider and taller, its
    /// corner 3 further out — and a negative one shrinks it; one that eats the whole box casts nothing.
    /// </summary>
    [Fact]
    public void ASpreadGrowsTheHardShadowOnEverySide()
    {
        double[] plain = RedRect(PdfContentProbe.DecodeStreams(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000" }))));
        double[] spread = RedRect(PdfContentProbe.DecodeStreams(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", SpreadPt = 3 }))));
        double[] shrunk = RedRect(PdfContentProbe.DecodeStreams(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", SpreadPt = -2 }))));
        string gone = PdfContentProbe.DecodeStreams(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", SpreadPt = -500 })));

        Assert.Equal(plain[0] - 3, spread[0], 2);
        Assert.Equal(plain[1] - 3, spread[1], 2);
        Assert.Equal(plain[2] + 6, spread[2], 2);
        Assert.Equal(plain[3] + 6, spread[3], 2);
        Assert.Equal(plain[2] - 4, shrunk[2], 2);
        Assert.DoesNotContain(gone.Split('\n'), l => Regex.IsMatch(l.Trim(), @"^1(\.0+)? 0(\.0+)? 0(\.0+)? rg$"));
    }

    /// <summary>
    /// Adversarial, the other way round: a small blur on a huge box — a 5 000-point page, a block box
    /// round three hundred lines — would be a 90-million-pixel mask at two pixels a point (and a
    /// 280 MB RGB image over it). It stays under <see cref="PdfExporter.MaxShadowMaskPixels"/>,
    /// rendered coarser: asserted on the image's own dimensions.
    /// </summary>
    [Fact]
    public void AHugeBoxsBlurredShadowStaysUnderThePixelCap()
    {
        var section = new Section { PageWidthPt = 5000, PageHeightPt = 5000 };
        section.Children.Add(new BoxStart { Shading = "#0000FF", Shadow = new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 4 } });
        for (int i = 0; i < 300; i++)
        {
            var line = new Paragraph();
            line.Runs.Add(new TextRun { Text = "Line" });
            section.Children.Add(line);
        }
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = null!;
        var worker = new System.Threading.Thread(() => pdf = doc.ToPdf(), 16 * 1024 * 1024);
        worker.Start();
        Assert.True(worker.Join(System.TimeSpan.FromSeconds(60)), "the huge box did not finish in a minute");

        var (_, _, width, height) = ShadowImage(pdf);
        Assert.InRange((long)width * height, PdfExporter.MaxShadowMaskPixels / 2, PdfExporter.MaxShadowMaskPixels);
        Assert.True(width < 4864 * 2 && height > width / 2, $"the mask ({width}×{height}) is not a coarser render of the tall box");
    }

    /// <summary>
    /// A paragraph's opacity reaches its shadow — hard or blurred — as it reaches its fill: since
    /// batch 90 the whole paragraph is a transparency group, so the one alpha state is the page's,
    /// set before the group's <c>Do</c>, and the shadow inside the group is drawn opaque; an opaque
    /// paragraph sets no state at all. The shadow's alpha argument is what a caller inside a group
    /// passes (<see cref="PdfExporter"/> passes 1 there), so the hard shadow is a plain red fill.
    /// </summary>
    [Fact]
    public void AShadowTakesTheParagraphsOpacity_ThroughItsGroup()
    {
        var translucent = Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000" });
        translucent.Opacity = 0.5;
        string content = PdfContentProbe.DecodeStreams(Pdf(translucent));
        string opaque = PdfContentProbe.DecodeStreams(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000" })));
        var blurred = Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 });
        blurred.Opacity = 0.5;
        string blurredContent = PdfContentProbe.DecodeStreams(Pdf(blurred));

        string[] lines = content.Split('\n').Select(l => l.Trim()).ToArray();
        int[] gs = lines.Select((l, i) => (l, i)).Where(p => p.l.EndsWith(" gs", System.StringComparison.Ordinal)).Select(p => p.i).ToArray();
        int form = System.Array.FindIndex(lines, l => l.EndsWith(" Do", System.StringComparison.Ordinal));
        int red = System.Array.FindIndex(lines, l => Regex.IsMatch(l, @"^1(\.0+)? 0(\.0+)? 0(\.0+)? rg$"));
        Assert.True(gs.Length == 1 && gs[0] < form, $"one alpha state before the group's Do was expected; gs at [{string.Join(",", gs)}], Do at {form}");
        Assert.True(red >= 0, "the shadow was not drawn");
        Assert.DoesNotContain(opaque.Split('\n'), l => l.Trim().EndsWith(" gs", System.StringComparison.Ordinal));
        string[] blurredLines = blurredContent.Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal(1, blurredLines.Count(l => l.EndsWith(" gs", System.StringComparison.Ordinal)));
        Assert.Equal(2, blurredLines.Count(l => l.EndsWith(" Do", System.StringComparison.Ordinal)));   // the group, and the shadow's image inside it
    }

    /// <summary>A block box's blurred shadow is the same image, before the box's fill.</summary>
    [Fact]
    public void ABlockBoxsBlurredShadowIsTheSameImage()
    {
        var box = new BoxStart { Shading = "#0000FF", Shadow = new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 } };
        var inside = new Paragraph();
        inside.Runs.Add(new TextRun { Text = "Inside" });
        string content = PdfContentProbe.DecodeStreams(Pdf(box, inside, new BoxEnd()));

        Assert.Single(ParagraphBoxTests.ImageDraws(content));
        Assert.True(content.IndexOf(" Do", System.StringComparison.Ordinal) < content.IndexOf("0 0 1 rg", System.StringComparison.Ordinal), "the image is not behind the fill");
    }

    /// <summary>
    /// Adversarial: the blur and the box are author-supplied and size the mask and its kernel. A
    /// 400pt blur at two pixels a point would be a four-million-pixel mask under a 1 600-tap kernel;
    /// it is rendered coarser — under the pixel cap, and with a deviation of at most sixteen pixels,
    /// so at most 16 ÷ 200 pixels a point — not refused: asserted on the image's own dimensions, the
    /// guard's evidence, with the whole conversion held to a second of CPU on a dedicated thread.
    /// </summary>
    [Fact]
    public void AHugeBlurIsRenderedCoarserNotRefused()
    {
        byte[] pdf = null!;
        var worker = new System.Threading.Thread(() => pdf = Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 400 })), 16 * 1024 * 1024);
        var started = System.Diagnostics.Stopwatch.StartNew();
        worker.Start();
        Assert.True(worker.Join(System.TimeSpan.FromSeconds(60)), "the huge blur did not finish in a minute");
        var (_, _, width, height) = ShadowImage(pdf);
        var (_, _, smallW, smallH) = ShadowImage(Pdf(Shadowed(new BoxShadow { OffsetXPt = 4, OffsetYPt = 4, Color = "#FF0000", BlurPt = 6 })));

        Assert.InRange((long)width * height, 1, PdfExporter.MaxShadowMaskPixels);
        // The small mask is at two pixels a point over the box and its 6pt reach; the huge one, over
        // the box and its 400pt reach, at 16 ÷ 200 = 0.08 pixels a point.
        double boxWidth = smallW / 2.0 - 12;
        Assert.InRange(smallW / (boxWidth + 12), 1.9, 2.0);
        Assert.InRange(width / (boxWidth + 800), 0.07, 0.09);
        Assert.True(height < smallH * 2, $"the huge shadow's mask ({width}×{height}) is not coarse next to the small one's ({smallW}×{smallH})");
        Assert.True(started.Elapsed < System.TimeSpan.FromSeconds(20), $"the huge blur took {started.Elapsed.TotalSeconds:F1}s");
    }
}
