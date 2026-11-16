// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A <see cref="Paragraph.Transform"/> or <see cref="BoxStart.Transform"/> is written about the
/// block's <c>TransformOrigin</c> (batch 104) — the centre of its border box unless the model says
/// where — and the link and form-field rectangles inside the block follow it. Before batch 104 the
/// matrix was written as given, about the page's origin, so a scale pushed the block off the page,
/// and the rectangles stayed where the untransformed text was. Under <c>scale(2)</c> about (ox, oy)
/// the matrix's translation is <c>(−ox, −oy)</c>, which is how the tests read the origin back.
/// </summary>
public class TransformOriginTests
{
    private static readonly double[] Scale2 = { 2, 0, 0, 2, 0, 0 };

    /// <summary>A box with no origin stated scales about the centre of its fill rectangle.</summary>
    [Fact]
    public void ABox_ScalesAboutItsCentreByDefault()
    {
        string content = Content(BlueBox(Scale2), Para("Inside"), new BoxEnd());
        var (x, y, w, h) = FillRect(content);
        double[] m = MatrixOf(content);

        Assert.Equal(2, m[0], 3);
        Assert.Equal(-(x + w / 2), m[4], 1);
        Assert.Equal(-(y + h / 2), m[5], 1);
    }

    /// <summary>The origin is a fraction of the border box plus an offset per axis: the top-left corner, and a point 10pt in from the right edge and 5pt down from the top.</summary>
    [Fact]
    public void ABox_TransformOrigin_IsAFractionAndAnOffsetPerAxis()
    {
        string corner = Content(BlueBox(Scale2, new double[] { 0, 0, 0, 0 }), Para("Inside"), new BoxEnd());
        string offset = Content(BlueBox(Scale2, new double[] { 1, -10, 0, 5 }), Para("Inside"), new BoxEnd());
        var (x, y, w, h) = FillRect(corner);
        double[] mc = MatrixOf(corner), mo = MatrixOf(offset);

        Assert.Equal(-x, mc[4], 1);
        Assert.Equal(-(y + h), mc[5], 1);
        Assert.Equal(-(x + w - 10), mo[4], 1);
        Assert.Equal(-(y + h - 5), mo[5], 1);
    }

    /// <summary>
    /// A paragraph's border box is the band from the top of its text to the bottom, its spacing
    /// before and after outside it as CSS margins are: its transform is written about the same point
    /// a box drawn exactly round it — the spacing as the box's margins — transforms about.
    /// </summary>
    [Fact]
    public void AParagraph_TransformsAboutItsOwnBorderBox_AsABoxRoundItDoes()
    {
        var alone = Para("Inside", spacing: 9);
        alone.Transform = Scale2;
        double[] paragraph = MatrixOf(Content(alone));
        var box = BlueBox(Scale2);
        box.MarginTop = 9; box.MarginBottom = 9;
        double[] boxed = MatrixOf(Content(box, Para("Inside"), new BoxEnd()));

        Assert.Equal(boxed[4], paragraph[4], 1);
        Assert.Equal(boxed[5], paragraph[5], 1);
    }

    /// <summary>
    /// Under an export scale the rectangle is held in default user space: the translation reaches it
    /// scaled (20pt right at half scale is 10pt), as the page's leading matrix scales the paint.
    /// </summary>
    [Fact]
    public void ALink_UnderAnExportScale_IsShiftedByTheScaledTranslation()
    {
        var half = new PdfExportOptions { Scale = 0.5 };
        double[] plain = LinkRect(Pdf(half, Linked()));
        var moved = Linked();
        moved.Transform = new double[] { 1, 0, 0, 1, 20, -30 };
        double[] shifted = LinkRect(Pdf(half, moved));

        Assert.Equal(plain[0] + 10, shifted[0], 2);
        Assert.Equal(plain[1] - 15, shifted[1], 2);
    }

    /// <summary>A link inside a translated paragraph moves with it: 20pt right, 30pt down, every corner.</summary>
    [Fact]
    public void ALink_FollowsAParagraphsTranslation()
    {
        double[] plain = LinkRect(Pdf(Linked()));
        var moved = Linked();
        moved.Transform = new double[] { 1, 0, 0, 1, 20, -30 };
        double[] shifted = LinkRect(Pdf(moved));

        Assert.Equal(plain[0] + 20, shifted[0], 2);
        Assert.Equal(plain[1] - 30, shifted[1], 2);
        Assert.Equal(plain[2] + 20, shifted[2], 2);
        Assert.Equal(plain[3] - 30, shifted[3], 2);
    }

    /// <summary>A link in a paragraph turned a quarter turn is the bounds of its turned rectangle: as wide as it was tall, and as tall as it was wide.</summary>
    [Fact]
    public void ALink_InARotatedParagraph_IsTheBoundsOfItsTurnedRectangle()
    {
        double[] plain = LinkRect(Pdf(Linked()));
        var turned = Linked();
        turned.Transform = new double[] { 0, 1, -1, 0, 0, 0 };
        double[] rotated = LinkRect(Pdf(turned));

        Assert.Equal(plain[3] - plain[1], rotated[2] - rotated[0], 1);
        Assert.Equal(plain[2] - plain[0], rotated[3] - rotated[1], 1);
    }

    /// <summary>A link inside a translated box follows the box's transform, as it follows a paragraph's.</summary>
    [Fact]
    public void ALink_InsideATransformedBox_FollowsIt()
    {
        double[] plain = LinkRect(Pdf(Linked()));
        double[] shifted = LinkRect(Pdf(BlueBox(new double[] { 1, 0, 0, 1, 20, -30 }), Linked(), new BoxEnd()));

        Assert.Equal(plain[0] + 20, shifted[0], 2);
        Assert.Equal(plain[1] - 30, shifted[1], 2);
    }

    /// <summary>A link under <c>scale(2)</c> about the paragraph's top-left corner starts twice as far from the page's left edge as it did, and is twice as wide.</summary>
    [Fact]
    public void ALink_UnderAScale_IsScaledFromTheOrigin()
    {
        double[] plain = LinkRect(Pdf(Linked()));
        var scaled = Linked();
        scaled.Transform = Scale2;
        scaled.TransformOrigin = new double[] { 0, 0, 0, 0 };
        double[] grown = LinkRect(Pdf(scaled));
        double left = PdfContentProbe.XOf(Content(Para("Margin")), "Margin");

        Assert.Equal((plain[2] - plain[0]) * 2, grown[2] - grown[0], 1);
        Assert.Equal(left + (plain[0] - left) * 2, grown[0], 1);
    }

    /// <summary>A form-field placement inside a translated paragraph moves with it, as a link does.</summary>
    [Fact]
    public void AFormFieldPlacement_FollowsAParagraphsTranslation()
    {
        static Paragraph WithField()
        {
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = "Name: " });
            // Ten no-break spaces: an unbreakable placeholder token, as FormFieldPlacementTests has it.
            p.Runs.Add(new TextRun { Text = new string(' ', 10), FontName = "Courier", FontSize = 10, Highlight = "#EFEFEF", FormField = new FormFieldSpec { Name = "name" } });
            return p;
        }
        PlacedFormField plain = Assert.Single(Export(WithField()).PlacedFormFields);
        var moved = WithField();
        moved.Transform = new double[] { 1, 0, 0, 1, 20, -30 };
        PlacedFormField shifted = Assert.Single(Export(moved).PlacedFormFields);

        Assert.Equal(plain.X + 20, shifted.X, 2);
        Assert.Equal(plain.Y - 30, shifted.Y, 2);
        Assert.Equal(plain.Width, shifted.Width, 2);
        Assert.Equal(plain.Height, shifted.Height, 2);
    }

    /// <summary>
    /// A transformed box broken across pages transforms each fragment about the fragment's own origin,
    /// and says so once — one report for a box over three pages — with every page's <c>q</c> closed.
    /// </summary>
    [Fact]
    public void ASlicedTransformedBox_ReportsOnce_AndBalancesEveryPage()
    {
        var section = new Section();
        section.Children.Add(BlueBox(Scale2));
        for (int i = 0; i < 120; i++) section.Children.Add(Para("Line " + i, spacing: 2));
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);

        byte[] pdf = null!;
        var seen = Capture(() => pdf = doc.ToPdf());
        string[] lines = PdfContentProbe.DecodeStreams(pdf).Split('\n').Select(l => l.Trim()).ToArray();

        Assert.True(PdfContentProbe.CountPages(pdf) >= 3, "the box was not sliced over three pages");
        Assert.Single(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated && (d.Detail ?? "").Contains("broke across pages", StringComparison.Ordinal));
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
        Assert.Equal(PdfContentProbe.CountPages(pdf), lines.Count(l => l.EndsWith(" cm", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A link on the second page of a sliced, translated box follows the box there too: the fragment
    /// on the new page takes its own marks (the page's link count, its content position), so the
    /// rectangle registered on that page is remapped — not left where it was because the marks were
    /// the first page's.
    /// </summary>
    [Fact]
    public void ALink_OnALaterPageOfASlicedBox_FollowsTheBoxThere()
    {
        static IDocElement[] Blocks(double[]? transform)
        {
            var blocks = new List<IDocElement> { new BoxStart { Shading = "#0000FF", Transform = transform } };
            for (int i = 0; i < 60; i++) blocks.Add(Para("Line " + i, spacing: 2));
            blocks.Add(Linked());
            blocks.Add(new BoxEnd());
            return blocks.ToArray();
        }
        byte[] plainPdf = Pdf(Blocks(null));
        byte[] movedPdf = Pdf(Blocks(new double[] { 1, 0, 0, 1, 20, -30 }));
        double[] plain = LinkRect(plainPdf), shifted = LinkRect(movedPdf);

        Assert.True(PdfContentProbe.CountPages(movedPdf) >= 2, "the box was not sliced, so the link is on the first page");
        Assert.Equal(plain[0] + 20, shifted[0], 2);
        Assert.Equal(plain[1] - 30, shifted[1], 2);
    }

    /// <summary>A box whole on its page reports nothing: the matrix is written about its origin and that is what CSS draws.</summary>
    [Fact]
    public void AWholeTransformedBox_ReportsNothing()
    {
        var seen = Capture(() => Content(BlueBox(Scale2), Para("Inside"), new BoxEnd()));

        Assert.DoesNotContain(seen, d => (d.Detail ?? "").Contains("transform", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------

    private static BoxStart BlueBox(double[] transform, double[]? origin = null)
        => new() { Shading = "#0000FF", Transform = transform, TransformOrigin = origin };

    private static Paragraph Para(string text, double spacing = 0)
    {
        var p = new Paragraph { SpacingBefore = spacing, SpacingAfter = spacing };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static Paragraph Linked()
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Go to " });
        p.Runs.Add(new TextRun { Text = "the link", LinkUri = "https://example.com/" });
        p.Runs.Add(new TextRun { Text = " and on." });
        return p;
    }

    private static byte[] Pdf(params IDocElement[] blocks) => Pdf(null, blocks);

    private static byte[] Pdf(PdfExportOptions? options, params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return options == null ? doc.ToPdf() : doc.ToPdf(options);
    }

    private static string Content(params IDocElement[] blocks) => PdfContentProbe.DecodeStreams(Pdf(blocks));

    private static PdfExporter Export(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        var exporter = new PdfExporter();
        using var ms = new MemoryStream();
        exporter.Export(doc, ms);
        return exporter;
    }

    /// <summary>The blue box's fill rectangle: <c>x y w h re</c> after its colour.</summary>
    private static (double X, double Y, double W, double H) FillRect(string content)
    {
        var m = Regex.Match(content, @"0 0 1 rg\s+(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");
        Assert.True(m.Success, "the box's fill rectangle was not found");
        return (D(m, 1), D(m, 2), D(m, 3), D(m, 4));
    }

    /// <summary>The six numbers of the first <c>cm</c> in the content.</summary>
    private static double[] MatrixOf(string content)
    {
        var m = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm");
        Assert.True(m.Success, "no cm operator was emitted");
        return Enumerable.Range(1, 6).Select(i => D(m, i)).ToArray();
    }

    /// <summary>The first link annotation's <c>/Rect</c>, as four numbers.</summary>
    private static double[] LinkRect(byte[] pdf)
    {
        var m = Regex.Match(Encoding.Latin1.GetString(pdf), @"/Rect\s*\[([^\]]*)\]");
        Assert.True(m.Success, "no /Rect in the output — the link annotation was not emitted");
        return m.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static double D(Match m, int group) => double.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static List<BuilderDiagnostic> Capture(Action body)
    {
        var seen = new List<BuilderDiagnostic>();
        int mine = Environment.CurrentManagedThreadId;
        void Collect(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != mine) return;
            lock (seen) seen.Add(d);
        }
        BuilderDiagnostics.OnEvent += Collect;
        try { body(); }
        finally { BuilderDiagnostics.OnEvent -= Collect; }
        return seen;
    }
}
