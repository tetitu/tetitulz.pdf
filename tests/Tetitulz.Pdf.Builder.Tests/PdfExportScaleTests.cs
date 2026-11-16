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
/// <see cref="PdfExportOptions.Scale"/> — the exporter scales the <b>paper</b> and prepends one
/// <c>cm</c> to each page's content, leaving every <see cref="Section"/> measurement in logical
/// points.
///
/// <para>The assertions here are coordinates rather than "bytes came back", because a wrong
/// translation in that matrix is invisible in a smoke test: the page still renders, the text is
/// still there, it is simply in the wrong place. The <c>/Rect</c> test is the one that pins the
/// half of the feature the content matrix cannot reach — an annotation rectangle is in default
/// user space (§12.5.2), so it has to be scaled by hand.</para>
/// </summary>
public sealed class PdfExportScaleTests
{
    private const double LetterlikeWidth = 595;   // DocDefaults.PageWidthPt (A4, whole points)
    private const double LetterlikeHeight = 842;  // DocDefaults.PageHeightPt

    private static readonly Regex MediaBoxArray =
        new(@"/MediaBox\s*\[([^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex RectArray =
        new(@"/Rect\s*\[([^\]]*)\]", RegexOptions.Compiled);

    private static double[] Numbers(Match m) =>
        m.Groups[1].Value
            .Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

    private static double[] FirstMediaBox(byte[] pdf)
    {
        var m = MediaBoxArray.Match(Encoding.Latin1.GetString(pdf));
        Assert.True(m.Success, "no /MediaBox in the output");
        return Numbers(m);
    }

    private static double[] FirstLinkRect(byte[] pdf)
    {
        var m = RectArray.Match(Encoding.Latin1.GetString(pdf));
        Assert.True(m.Success, "no /Rect in the output — the link annotation was not emitted");
        return Numbers(m);
    }

    private static Document OneParagraph(string text = "Hi")
    {
        var doc = new Document();
        var sec = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        sec.Children.Add(p);
        doc.Sections.Add(sec);
        return doc;
    }

    [Fact]
    public void Scale_Half_ScalesThePaperAndLeavesTheLayoutInLogicalPoints()
    {
        byte[] plain = OneParagraph().ToPdf(new PdfExportOptions());
        byte[] half = OneParagraph().ToPdf(new PdfExportOptions { Scale = 0.5 });

        double[] plainBox = FirstMediaBox(plain), halfBox = FirstMediaBox(half);
        Assert.Equal(LetterlikeWidth, plainBox[2], 3);
        Assert.Equal(LetterlikeHeight, plainBox[3], 3);
        Assert.Equal(LetterlikeWidth / 2, halfBox[2], 3);
        Assert.Equal(LetterlikeHeight / 2, halfBox[3], 3);

        string plainContent = PdfContentProbe.DecodeStreams(plain);
        string halfContent = PdfContentProbe.DecodeStreams(half);

        // A pure scale: no translation terms, or the origin and the top edge would both move.
        Assert.DoesNotContain(" cm", plainContent);
        Assert.Contains("0.5 0 0 0.5 0 0 cm", halfContent);

        // "Scale the paper, never the Section": the text is drawn at exactly the same logical
        // coordinates in both. If Scale had been folded into the Section instead, every Tm here
        // would have moved and pagination with it.
        Assert.Equal(PdfContentProbe.XOf(plainContent, "Hi"), PdfContentProbe.XOf(halfContent, "Hi"), 3);
        Assert.Equal(PdfContentProbe.YOf(plainContent, "Hi"), PdfContentProbe.YOf(halfContent, "Hi"), 3);
    }

    [Fact]
    public void Scale_Half_WritesTheMatrixAheadOfTheHeaderDraw()
    {
        var doc = OneParagraph();
        var header = new Header();
        var hp = new Paragraph();
        hp.Runs.Add(new TextRun { Text = "RunningHead" });
        header.Paragraphs.Add(hp);
        doc.Sections[0].Header = header;

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions { Scale = 0.5 }));

        int matrixAt = content.IndexOf("0.5 0 0 0.5 0 0 cm", StringComparison.Ordinal);
        int firstTextAt = content.IndexOf("BT", StringComparison.Ordinal);
        Assert.True(matrixAt >= 0, "no scaling matrix was written");
        Assert.True(firstTextAt >= 0, "nothing was drawn");
        // StartPage draws the header, so a matrix written anywhere after the page's first text
        // object would leave the running head at full size on a half-size sheet.
        Assert.True(matrixAt < firstTextAt,
            $"the scaling matrix is at {matrixAt}, after the first text object at {firstTextAt}");
    }

    [Fact]
    public void Scale_Half_HalvesTheLinkAnnotationRect()
    {
        var plainDoc = new Document();
        var plainSec = new Section();
        var pp = new Paragraph();
        pp.Runs.Add(new TextRun { Text = "Visit", LinkUri = "https://example.com/" });
        plainSec.Children.Add(pp);
        plainDoc.Sections.Add(plainSec);

        var halfDoc = new Document();
        var halfSec = new Section();
        var hp = new Paragraph();
        hp.Runs.Add(new TextRun { Text = "Visit", LinkUri = "https://example.com/" });
        halfSec.Children.Add(hp);
        halfDoc.Sections.Add(halfSec);

        double[] plainRect = FirstLinkRect(plainDoc.ToPdf(new PdfExportOptions()));
        double[] halfRect = FirstLinkRect(halfDoc.ToPdf(new PdfExportOptions { Scale = 0.5 }));

        // Every corner, not just the width: the /Rect origin is what a link that "still works but
        // points at the wrong words" gets wrong, and a width-only assertion cannot see it.
        for (int i = 0; i < 4; i++)
            Assert.Equal(plainRect[i] / 2, halfRect[i], 3);

        // The control for the loop above: an all-zero rect would satisfy it trivially.
        Assert.True(plainRect[2] - plainRect[0] > 1, "the unscaled link rect has no width");
    }

    [Theory]
    [InlineData(10.0, 2.0)]      // above the band
    [InlineData(0.0, 0.1)]       // AddPage would throw on a zero-width page
    [InlineData(-3.0, 0.1)]      // negative: a mirrored page, not a scaled one
    [InlineData(double.NaN, 1.0)] // Math.Max/Min propagate NaN; "NaN" in a /MediaBox is unopenable
    public void Scale_OutsideTheSupportedBand_IsClamped(double requested, double effective)
    {
        double[] box = FirstMediaBox(OneParagraph().ToPdf(new PdfExportOptions { Scale = requested }));

        Assert.Equal(LetterlikeWidth * effective, box[2], 3);
        Assert.Equal(LetterlikeHeight * effective, box[3], 3);
    }
}
