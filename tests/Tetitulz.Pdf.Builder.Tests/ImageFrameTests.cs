// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="ImageRun.Box"/> — the frame round a picture, and a blank box of the picture's size
/// when there are no pixels (a canvas nothing drew) — and where a block image sits in its band:
/// by the paragraph's alignment, as its text lines do.
/// </summary>
/// <remarks>
/// Read back from the decoded content: the image's <c>cm</c>, the frame's <c>re</c>, and the
/// baseline of the paragraph after it.
/// </remarks>
public class ImageFrameTests
{
    private const double W = 100, H = 50;

    private static string Content(TextDirection? direction, params Paragraph[] paragraphs)
    {
        var section = new Section();
        if (direction is { } d) section.Direction = d;
        foreach (var p in paragraphs) section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static Paragraph ImageParagraph(ImageRun run, TextAlignment align = TextAlignment.Start)
    {
        var p = new Paragraph { Alignment = align };
        p.Runs.Add(run);
        return p;
    }

    private static Paragraph Text(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static ImageRun Picture(InlineBox? box = null) =>
        new ImageRun { Data = ExifJpeg.Build(8, 8, 1), WidthPt = W, HeightPt = H, Box = box };

    private static ImageRun Blank(InlineBox? box) =>
        new ImageRun { WidthPt = W, HeightPt = H, Box = box };

    private static InlineBox Frame(double border, double padding) => new InlineBox
    {
        Border = new Border(border, "#FF0000"),
        PaddingLeft = padding, PaddingRight = padding, PaddingTop = padding, PaddingBottom = padding,
    };

    /// <summary>The first image placement, as (x, y, width, height) from its <c>cm</c>.</summary>
    private static (double X, double Y, double W, double H) ImagePlacement(string content)
    {
        var m = Regex.Match(content,
            @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+cm\s*/\w+\s+Do\b");
        Assert.True(m.Success, "no image was drawn");
        double G(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return (G(5), G(6), G(1), G(4));
    }

    /// <summary>Every rectangle (<c>x y w h re</c>) in the content.</summary>
    private static (double X, double Y, double W, double H)[] Rectangles(string content) =>
        Regex.Matches(content, @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\b")
             .Cast<Match>()
             .Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value)))
             .ToArray();

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the blank box

    /// <summary>
    /// A blank box takes its size on the page: the paragraph after it sits lower by the box's
    /// height (and frame) than after an image run that occupies nothing — the canvas that used to
    /// let everything below a signature pad move up into its place.
    /// </summary>
    [Fact]
    public void ABlankBoxReservesItsHeight()
    {
        double yAfterBlank = PdfContentProbe.YOf(Content(null, ImageParagraph(Blank(Frame(1, 0))), Text("After")), "After");
        double yAfterNothing = PdfContentProbe.YOf(Content(null, ImageParagraph(Blank(null)), Text("After")), "After");

        Assert.False(double.IsNaN(yAfterBlank));
        Assert.False(double.IsNaN(yAfterNothing));
        // The height, a 1pt border above and below, and the 2pt gap every block image leaves under it.
        Assert.Equal(H + 2 + 2, yAfterNothing - yAfterBlank, 1);
    }

    /// <summary>
    /// The blank box draws its frame — a rectangle of the picture's size plus the border — and no
    /// image; an image run with neither pixels nor a box draws nothing (the control).
    /// </summary>
    [Fact]
    public void ABlankBoxDrawsItsFrameAndNoImage()
    {
        string framed = Content(null, ImageParagraph(Blank(Frame(2, 0))));
        string bare = Content(null, ImageParagraph(Blank(null)));

        Assert.DoesNotContain(" Do", framed, System.StringComparison.Ordinal);
        // Stroked along the middle of a 2pt border: the rectangle is the outer box less one border.
        Assert.Contains(Rectangles(framed), r => System.Math.Abs(r.W - W - 2) < 0.01 && System.Math.Abs(r.H - H - 2) < 0.01);
        Assert.Empty(Rectangles(bare));
    }

    /// <summary>
    /// A blank box is not an image that failed: nothing is decoded and nothing is reported. An
    /// image run whose bytes do not decode reports it (the control), so the slot can speak.
    /// </summary>
    [Fact]
    public void ABlankBoxIsNotReportedAsAnUndecodableImage()
    {
        Content(null, ImageParagraph(new ImageRun { Data = new byte[] { 1, 2, 3 }, WidthPt = W, HeightPt = H }));
        Assert.Equal(Tetitulz.Pdf.Builder.Export.BuilderDiagnosticKind.ImageDecodeFailed,
                     Tetitulz.Pdf.Builder.Export.BuilderDiagnostics.LastError?.Kind);

        Content(null, ImageParagraph(Blank(Frame(1, 0))));
        Assert.Null(Tetitulz.Pdf.Builder.Export.BuilderDiagnostics.LastError);
    }

    /// <summary>
    /// The HTML exporter writes a blank box as the room it takes — an inline-block of its size —
    /// never as an <c>&lt;img&gt;</c> with an empty <c>data:</c> URI (a broken-image icon); a
    /// picture is still an <c>&lt;img&gt;</c> (the control).
    /// </summary>
    [Fact]
    public void TheHtmlExporterWritesABlankBoxAsItsRoom()
    {
        static string Html(ImageRun run)
        {
            var section = new Section();
            section.Children.Add(ImageParagraph(run));
            var doc = new Document();
            doc.Sections.Add(section);
            return System.Text.Encoding.UTF8.GetString(doc.ToHtml());
        }

        string blank = Html(Blank(Frame(1, 0)));
        string picture = Html(Picture());

        Assert.DoesNotContain("<img", blank, System.StringComparison.Ordinal);
        Assert.Contains("display:inline-block;width:100pt;height:50pt", blank, System.StringComparison.Ordinal);
        Assert.Contains("<img src=\"data:", picture, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Shrink-to-fit measuring counts a framed picture at its outer width — picture, padding and
    /// border — as the block renderer draws it; a float round a thumbnail was a frame too narrow.
    /// </summary>
    [Fact]
    public void AFramedPictureMeasuresAtItsOuterWidth()
    {
        var plain = new Paragraph();
        plain.Runs.Add(Picture());
        var framed = new Paragraph();
        framed.Runs.Add(Picture(Frame(1, 4)));

        var exporter = new Tetitulz.Pdf.Builder.Export.PdfExporter();
        Assert.Equal(exporter.MeasureShrinkToFitWidth(plain, 400) + 2 * 5,
                     exporter.MeasureShrinkToFitWidth(framed, 400), 3);
    }

    // ------------------------------------------------------------------ the frame round a picture

    /// <summary>
    /// A picture with a frame is drawn inside it: moved in by the padding and border on the left,
    /// down by them on top, at its own size — and the frame's rectangle encloses it.
    /// </summary>
    [Fact]
    public void AFramedPictureSitsInsideItsFrame()
    {
        var plain = ImagePlacement(Content(null, ImageParagraph(Picture())));
        string content = Content(null, ImageParagraph(Picture(Frame(1, 4))));
        var framed = ImagePlacement(content);

        Assert.Equal(W, framed.W, 2);
        Assert.Equal(H, framed.H, 2);
        Assert.Equal(plain.X + 5, framed.X, 2);
        Assert.Equal(plain.Y - 5, framed.Y, 2);
        Assert.Contains(Rectangles(content), r =>
            r.X < framed.X && r.Y < framed.Y && r.X + r.W > framed.X + W && r.Y + r.H > framed.Y + H);
    }

    // ------------------------------------------------------------------ where the block sits

    /// <summary>
    /// A block image follows its paragraph's alignment: centred halfway between the left and right
    /// placements, and right-aligned well to the right of left. It was always drawn at the left.
    /// </summary>
    [Fact]
    public void ABlockImageFollowsItsParagraphsAlignment()
    {
        double left = ImagePlacement(Content(null, ImageParagraph(Picture(), TextAlignment.Left))).X;
        double center = ImagePlacement(Content(null, ImageParagraph(Picture(), TextAlignment.Center))).X;
        double right = ImagePlacement(Content(null, ImageParagraph(Picture(), TextAlignment.Right))).X;

        Assert.True(right > left + W, $"right-aligned at {right}, left at {left}");
        Assert.Equal((left + right) / 2, center, 2);
    }

    /// <summary>
    /// In a right-to-left section a start-aligned image sits at the right — where a browser puts a
    /// photo or a signature box in a Hebrew form — and a left-to-right one at the left (the control).
    /// </summary>
    [Fact]
    public void AStartAlignedImageInARightToLeftSectionSitsAtTheRight()
    {
        double ltr = ImagePlacement(Content(TextDirection.LeftToRight, ImageParagraph(Picture()))).X;
        double rtl = ImagePlacement(Content(TextDirection.RightToLeft, ImageParagraph(Picture()))).X;
        double right = ImagePlacement(Content(TextDirection.LeftToRight, ImageParagraph(Picture(), TextAlignment.Right))).X;

        Assert.Equal(right, rtl, 2);
        Assert.True(rtl > ltr + W, $"rtl at {rtl}, ltr at {ltr}");
    }
}
