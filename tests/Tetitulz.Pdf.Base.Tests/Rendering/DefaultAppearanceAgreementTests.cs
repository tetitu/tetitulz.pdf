// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The three readers of <c>/DA</c> now share <c>Base.Operations.Forms.DefaultAppearanceString</c>, and the
/// annotation appearance builder takes the renderers' fallback's placement, stamp colour, stamp
/// line width and caption rather than its own numbers.
/// </summary>
public class DefaultAppearanceAgreementTests
{
    /// <summary>A free-text annotation with no appearance, its text in the <c>/DA</c>'s CMYK cyan.</summary>
    private static byte[] CyanFreeText() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>")
        .Obj(4, "<< /Type /Annot /Subtype /FreeText /Rect [10 100 190 160] /Contents (HIHIHI) " +
                "/DA (/Helv 30 Tf 1 0 0 0 k) /Border [0 0 0] >>")
        .Finish();

    /// <summary>
    /// Both renderers draw a CMYK <c>/DA</c>'s text in its colour. They read <c>g</c> and
    /// <c>rg</c> and not <c>k</c>, so the text came out black, while the appearance the library
    /// generated for the same annotation used the colour.
    /// </summary>
    [Fact]
    public void ACmykDefaultAppearance_IsDrawnInItsColour_ByBothRenderers()
    {
        string cyan = PdfColor.ToCssRgb(PdfColor.CmykToRgb(1, 0, 0, 0));
        Assert.NotEqual("rgb(0,0,0)", cyan);

        var svg = XDocument.Parse(PdfOperations.RenderPageToSvg(new MemoryStream(CyanFreeText()), 0));
        var text = svg.Descendants().Single(e => e.Name.LocalName == "text" && e.Value.Contains("HIHIHI"));
        Assert.Equal(cyan, (string)text.Attribute("fill"));

        // RenderedPage is RGBA; the solid middle of a 30 pt stroke is the fill colour exactly.
        var expected = PdfColor.CmykToRgb(1, 0, 0, 0);
        var page = PdfOperations.RenderPageToPixels(new MemoryStream(CyanFreeText()), 0, 72);
        bool sawCyan = false, sawBlack = false;
        for (int y = 40; y < 100; y++)
            for (int x = 10; x < 190; x++)
            {
                int i = y * page.Stride + x * 4;
                int r = page.Pixels[i], g = page.Pixels[i + 1], b = page.Pixels[i + 2];
                if (r + g + b < 150) sawBlack = true;
                if (Math.Abs(r - expected.Red) <= 8 && Math.Abs(g - expected.Green) <= 8
                    && Math.Abs(b - expected.Blue) <= 8)
                    sawCyan = true;
            }
        Assert.True(sawCyan, "no text pixel in the /DA's colour");
        Assert.False(sawBlack, "the text was drawn black");
    }

    /// <summary>
    /// A <c>/DA</c> that sets no size draws at the one default, 10 pt, which the builder shares.
    /// Asserted on the SVG text's transform, whose first coefficient is the size (the element's own
    /// <c>font-size</c> is 1).
    /// </summary>
    [Fact]
    public void ADefaultAppearanceWithNoSize_IsDrawnAtTheSharedDefault()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>")
            .Obj(4, "<< /Type /Annot /Subtype /FreeText /Rect [10 100 190 160] /Contents (NOSIZE) /DA (0 g) >>")
            .Finish();

        var svg = XDocument.Parse(PdfOperations.RenderPageToSvg(new MemoryStream(pdf), 0));
        var text = svg.Descendants().Single(e => e.Name.LocalName == "text" && e.Value.Contains("NOSIZE"));
        var matrix = Regex.Match((string)text.Attribute("transform"), @"matrix\(([^,]+),");
        Assert.True(matrix.Success);
        Assert.Equal(Base.Operations.Forms.DefaultAppearanceString.DefaultFontSize,
            float.Parse(matrix.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(10f, Base.Operations.Forms.DefaultAppearanceString.DefaultFontSize);   // characterisation
    }

    // ------------------------------------------------------------------ the builder and the fallback

    /// <summary>A free-text appearance draws its text in the <c>/DA</c>'s colour.</summary>
    [Fact]
    public void AFreeTextAppearance_DrawsInTheDefaultAppearancesColour()
    {
        string content = Content(Build(
            "<< /Type /Annot /Subtype /FreeText /Rect [0 0 100 40] /Contents (Hi) /DA (/Helv 12 Tf 1 0 0 rg) >>", 100, 40));

        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, Operands(content, "rg"));
    }

    /// <summary>A stamp's caption is sized and placed by the fallback's estimate.</summary>
    [Fact]
    public void AStampCaption_IsPlacedWhereTheFallbackPlacesIt()
    {
        string content = Content(Build(
            "<< /Type /Annot /Subtype /Stamp /Rect [0 0 120 40] /Name /Approved >>", 120, 40));

        Assert.True(AnnotationFallbackModel.StampCaption("Approved", 0, 0, 120, 40,
            out float size, out float x, out float y));
        Assert.Equal(size, Operands(content, "Tf")[0], 1e-3);
        var td = Operands(content, "Td");
        Assert.Equal(x, td[0], 1e-3);
        Assert.Equal(y, td[1], 1e-3);
    }

    private static string Content(PdfStream form) => System.Text.Encoding.ASCII.GetString(form.RawData);

    private static PdfStream Build(string annotBody, double width, double height)
    {
        var parsed = Core.Document.PdfDocument.Open(new MemoryStream(new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Obj(3, annotBody)
            .Finish()));
        var annot = (PdfDictionary)parsed.GetObject(3);
        return AnnotationAppearanceBuilder.Build(annot, width, height, 0, 0, out _);
    }

    /// <summary>
    /// A free-text appearance with no size in its <c>/DA</c> is set at the fallback's default size,
    /// at the fallback's baseline: 10 pt, 2 pt in, 1.1 sizes below the top. It used 12 pt, 4 pt in,
    /// one size below the top, under a comment saying the two agreed.
    /// </summary>
    [Fact]
    public void AFreeTextAppearance_UsesTheFallbacksSizeAndBaseline()
    {
        string content = Content(Build(
            "<< /Type /Annot /Subtype /FreeText /Rect [0 0 100 40] /Contents (Hi) /DA (/Helv 0 g) >>", 100, 40));

        AnnotationFallbackModel.FreeTextBaseline(0f, 40f, 10f, out float x, out float y);
        Assert.Equal(10.0, Operands(content, "Tf")[0]);
        Assert.Equal(new double[] { x, y }, Operands(content, "Td"));
        Assert.Equal(2f, x);    // characterisation: the fallback's inset
        Assert.Equal(29f, y);   // 40 - 1.1 x 10
    }

    /// <summary>
    /// A stamp's appearance is outlined and captioned in the colour the fallback chooses for its
    /// name, at the fallback's minimum width. "Approved" was red here, and green in the fallback.
    /// </summary>
    [Fact]
    public void AnApprovedStampAppearance_IsTheFallbacksGreen_AtItsLineWidth()
    {
        string content = Content(Build(
            "<< /Type /Annot /Subtype /Stamp /Rect [0 0 120 40] /Name /Approved >>", 120, 40));

        var green = AnnotationFallbackModel.StampPalette("Approved");
        var stroke = Operands(content, "RG");
        Assert.Equal(green.Red / 255.0, stroke[0], 1e-3);
        Assert.Equal(green.Green / 255.0, stroke[1], 1e-3);
        Assert.Equal(green.Blue / 255.0, stroke[2], 1e-3);
        Assert.Equal(AnnotationFallbackModel.StampMinLineWidth, Operands(content, "w")[0], 1e-3);
        Assert.Contains("(Approved) Tj", content, StringComparison.Ordinal);
    }

    /// <summary>A declared <c>/C</c> wins over the palette, in the builder as in the fallback.</summary>
    [Fact]
    public void AStampWithItsOwnColour_KeepsIt()
    {
        string content = Content(Build(
            "<< /Type /Annot /Subtype /Stamp /Rect [0 0 120 40] /Name /Approved /C [0 0 1] >>", 120, 40));

        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, Operands(content, "RG"));
    }

    /// <summary>A stamp with no <c>/Name</c> is captioned "Draft", Table 181's default, as the fallback does.</summary>
    [Fact]
    public void AStampWithNoName_IsCaptionedDraft()
    {
        string content = Content(Build(
            "<< /Type /Annot /Subtype /Stamp /Rect [0 0 120 40] /Contents (Some note) >>", 120, 40));

        Assert.Contains("(Draft) Tj", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Some note", content, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the form generator

    /// <summary>
    /// The form generator's colour operator is rebuilt from numbers: a colour operand that is not a
    /// number is ignored rather than pasted into the appearance stream.
    /// </summary>
    [Fact]
    public void AFormColourOperandThatIsNotANumber_IsNotPastedIntoTheAppearance()
    {
        Assert.Equal("0 g", FormAppearance.ParseDefaultAppearance("/Helv 9 Tf (x)Tj g")!.ColorOperators);
        Assert.Equal("0.5 g", FormAppearance.ParseDefaultAppearance("/Helv 9 Tf .5 g")!.ColorOperators);
    }

    /// <summary>
    /// The numeric operands of the first occurrence of <paramref name="op"/> in the content — a
    /// name operand, such as <c>Tf</c>'s font, is skipped.
    /// </summary>
    private static double[] Operands(string content, string op)
    {
        foreach (string line in content.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[^1] == op)
                return parts.Take(parts.Length - 1)
                    .Where(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        }
        throw new Xunit.Sdk.XunitException($"no '{op}' operator in: {content}");
    }
}
