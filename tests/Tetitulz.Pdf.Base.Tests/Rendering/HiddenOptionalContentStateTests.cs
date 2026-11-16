// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.11.3.2: content in a hidden optional-content section is not drawn, but its graphics state
/// operators still apply and the text position still moves. Both renderers and the measuring render
/// behind the page elements keep that: the text after a hidden run starts where the hidden run
/// ended, a colour set inside a hidden section paints what follows it, and the hidden run itself
/// is neither painted nor an element. The control, the same section shown, paints it.
/// </summary>
public sealed class HiddenOptionalContentStateTests
{
    /// <summary>"Draft " at 12 pt Helvetica is 28.7 pt wide, so "Approved" starts at 100.7.</summary>
    private const string Text = "BT /F1 12 Tf 72 700 Td /OC /L1 BDC (Draft ) Tj EMC (Approved) Tj ( by QA) Tj ET";

    /// <summary>
    /// "Approved" stands where the shown page puts it — after "Draft " — with the hidden run
    /// neither an element nor painted; a measuring render that skipped the hidden run's advance
    /// put it at 72, over the hidden text's place.
    /// </summary>
    [Fact]
    public void TheTextAfterAHiddenRun_StartsWhereTheRunEnded()
    {
        byte[] hidden = Pdf(Text, hidden: true), shown = Pdf(Text, hidden: false);

        IReadOnlyList<PdfPageElement> hiddenElements = PdfPageElements.List(new MemoryStream(hidden), 0);
        IReadOnlyList<PdfPageElement> shownElements = PdfPageElements.List(new MemoryStream(shown), 0);
        Assert.Equal(new[] { "Approved", " by QA" }, hiddenElements.Select(e => e.Text));
        Assert.Equal(new[] { "Draft ", "Approved", " by QA" }, shownElements.Select(e => e.Text));
        Assert.Equal(shownElements[1].X, hiddenElements[0].X, 2);
        Assert.True(hiddenElements[0].X > shownElements[0].X + 15, $"Approved at {hiddenElements[0].X} stands over Draft at {shownElements[0].X}");

        using SKBitmap hiddenBmp = PixelAssert.Render(hidden);
        using SKBitmap shownBmp = PixelAssert.Render(shown);
        int hiddenLeft = PixelAssert.InkBounds(hiddenBmp)!.Value.left, shownLeft = PixelAssert.InkBounds(shownBmp)!.Value.left;
        Assert.True(hiddenLeft > shownLeft + 15, $"the hidden page's ink starts at {hiddenLeft}, the shown page's at {shownLeft}");
        Assert.Equal(PixelAssert.InkBounds(hiddenBmp)!.Value.right, PixelAssert.InkBounds(shownBmp)!.Value.right);

        XDocument hiddenSvg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(hidden), 0));
        XDocument shownSvg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(shown), 0));
        XElement hiddenGroup = Assert.Single(hiddenSvg.Descendants().Where(e => e.Name.LocalName == "g" && (string)e.Attribute("display") == "none"));
        Assert.Contains(hiddenGroup.Descendants(), e => e.Name.LocalName == "text");
        Assert.Equal(TranslationX(ApprovedText(shownSvg).Attribute("transform")!.Value),
                     TranslationX(ApprovedText(hiddenSvg).Attribute("transform")!.Value), 2);
    }

    private static XElement ApprovedText(XDocument svg) =>
        svg.Descendants().First(e => e.Name.LocalName == "text" && e.Value.StartsWith("Approved", System.StringComparison.Ordinal)
                                     && !e.Ancestors().Any(a => (string)a.Attribute("display") == "none"));

    [Fact]
    public void AColourSetInsideAHiddenSection_PaintsWhatFollows()
    {
        byte[] pdf = Pdf("/OC /L1 BDC 1 0 0 rg EMC 0 0 100 100 re f", hidden: true);

        using SKBitmap bmp = PixelAssert.Render(pdf);
        SKColor c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Red > 200 && c.Green < 60, $"the colour set in the hidden section was dropped: {c}");

        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
        XElement square = Assert.Single(svg.Descendants().Where(e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none"));
        Assert.Equal("rgb(255,0,0)", square.Attribute("fill")!.Value);
    }

    private static float TranslationX(string transform)
    {
        Match m = Regex.Match(transform, @"matrix\(([^)]*)\)");
        return float.Parse(m.Groups[1].Value.Split(',')[4], CultureInfo.InvariantCulture);
    }

    private static byte[] Pdf(string content, bool hidden) =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] /D << " + (hidden ? "/OFF [5 0 R]" : "/ON [5 0 R]") + " >> >> >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> /Properties << /L1 5 0 R >> >> >>")
            .StreamObj(4, "<<", content)
            .Obj(5, "<< /Type /OCG /Name (Draft) >>")
            .Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
            .Finish();
}
