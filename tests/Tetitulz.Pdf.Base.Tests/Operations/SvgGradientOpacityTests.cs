// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> applies a gradient's <c>stop-opacity</c> (SVG 1.1 §13.2.4): the
/// stops' opacities are a soft mask — the same shading in grey, their opacities its levels — that the
/// fill, or the stroke, is painted through, and nothing else is.
/// </summary>
/// <remarks>
/// The stops were drawn opaque and the opacity reported, so a gradient fading to transparent — the
/// usual way artwork fades anything out — faded to its last colour instead.
/// </remarks>
public class SvgGradientOpacityTests
{
    private const string Fade =
        @"<linearGradient id=""g""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff"" stop-opacity=""0""/></linearGradient>";

    [Fact]
    public void TheStopsOpacitiesAreASoftMask()
    {
        using var doc = Open(Fade + @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int gs = Array.FindIndex(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.True(gs > 0 && page[gs - 1] == "q" && gs < Array.IndexOf(page, "f"), "the fill was not painted through a mask");
        var states = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]));
        var state = Assert.IsType<PdfDictionary>(doc.ResolveObject(states[new PdfName(page[gs].Split(' ')[0].TrimStart('/'))]));
        var smask = Assert.IsType<PdfDictionary>(doc.ResolveObject(state[new PdfName("SMask")]));
        Assert.Equal("Luminosity", ((PdfName)smask[PdfName.S]).Value);
        var form = Assert.IsType<PdfStream>(doc.ResolveObject(smask[new PdfName("G")]));
        string[] mask = Lines(form.GetDecodedData());
        string grey = Assert.Single(mask, l => l.EndsWith(" scn", StringComparison.Ordinal)).Split(' ')[0].TrimStart('/');
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, grey)[new PdfName("Shading")]));
        var function = Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]));
        Assert.Equal("DeviceGray", ((PdfName)shading[PdfName.ColorSpace]).Value);
        Assert.Equal(new[] { 1.0 }, Numbers(doc, function[new PdfName("C0")]));
        Assert.Equal(new[] { 0.0 }, Numbers(doc, function[new PdfName("C1")]));
        var group = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Group]));
        Assert.Equal("DeviceGray", ((PdfName)group[new PdfName("CS")]).Value);   // luminosity is read in grey
        Assert.Single(page, l => l.EndsWith(" re", StringComparison.Ordinal));   // the path, once
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>One stop that is not opaque is its colour at its opacity: red at a half over white.</summary>
    [Fact]
    public void OneFadedStopIsItsColourAtItsOpacity()
    {
        var at = Render(@"<linearGradient id=""g""><stop offset=""0"" stop-color=""#ff0000"" stop-opacity=""0.5""/></linearGradient>" +
                        @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((255, 128, 128), at(100, 50));
    }

    /// <summary>A line's stroke fades too: the line is stroked once, through its mask.</summary>
    [Fact]
    public void ALinesStrokeFades()
    {
        using var doc = Open(Fade + @"<line x1=""10"" y1=""50"" x2=""190"" y2=""60"" stroke=""url(#g)"" stroke-width=""10""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int gs = Array.FindIndex(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.Equal("q", page[gs - 1]);
        Assert.Single(page, l => l.EndsWith(" m", StringComparison.Ordinal));
        Assert.True(Array.IndexOf(page, "S") > gs, "the line was not stroked through the mask");
        // Only the path is taken back into the mask: the stroke's colour was set before it.
        Assert.True(Array.IndexOf(page, "/Pattern CS") < gs - 1, "more than the line's path was taken into the mask");
    }

    /// <summary>The mask is the run's own: a run after it with a flat fill is not wrapped in it.</summary>
    [Fact]
    public void ARunsMaskEndsWithIt()
    {
        using var doc = Open(Fade + @"<text x=""10"" y=""100"" font-size=""50""><tspan fill=""url(#g)"">H</tspan><tspan fill=""#000000"">H</tspan></text>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int[] bts = Enumerable.Range(0, page.Length).Where(i => page[i] == "BT").ToArray();
        Assert.Equal(2, bts.Length);
        Assert.EndsWith(" gs", page[bts[0] - 1], StringComparison.Ordinal);
        Assert.DoesNotContain(" gs", page[bts[1] - 1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Rendered over white: opaque red at the left, white at the right where the blue has faded out,
    /// and half way the purple at half strength.
    /// </summary>
    [Fact]
    public void TheFillFadesOut()
    {
        var at = Render(Fade + @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((255, 0, 0), at(1, 50));
        AssertNear((255, 255, 255), at(198, 50));
        AssertNear((191, 128, 191), at(100, 50));
    }

    /// <summary>
    /// The mask is the fill's alone: the path is filled through it, then stroked again without it, so a
    /// black stroke stays black where the fill has faded out.
    /// </summary>
    [Fact]
    public void TheStrokeIsNotMaskedByTheFill()
    {
        var at = Render(Fade + @"<rect x=""10"" y=""10"" width=""180"" height=""80"" fill=""url(#g)"" stroke=""#000000"" stroke-width=""10""/>");

        AssertNear((0, 0, 0), at(189, 50));
        AssertNear((255, 255, 255), at(180, 50), tolerance: 30);
    }

    /// <summary>
    /// A stroke's gradient masks the stroke alone: a green fill is opaque everywhere, and the stroke
    /// fades out from left to right.
    /// </summary>
    [Fact]
    public void AStrokesMaskIsItsOwn()
    {
        var at = Render(Fade + @"<rect x=""10"" y=""10"" width=""180"" height=""80"" fill=""#00ff00"" stroke=""url(#g)"" stroke-width=""10""/>");

        AssertNear((0, 255, 0), at(180, 50));
        AssertNear((255, 0, 0), at(10, 50), tolerance: 30);
        AssertNear((255, 255, 255), at(193, 50), tolerance: 30);
    }

    /// <summary>
    /// A marker is not the fill: it is drawn after the mask has ended, whole. The path's end marker is
    /// a black square at (190, 50), where the fill has faded to nothing.
    /// </summary>
    [Fact]
    public void AMarkerIsNotMasked()
    {
        var at = Render(Fade +
            @"<marker id=""m"" markerWidth=""10"" markerHeight=""10"" refX=""5"" refY=""5"" markerUnits=""userSpaceOnUse""><rect width=""10"" height=""10"" fill=""#000000""/></marker>" +
            @"<path d=""M 10 50 H 190 V 90 H 10 Z"" fill=""url(#g)"" marker-mid=""url(#m)""/>");

        AssertNear((0, 0, 0), at(190, 50));
    }

    /// <summary>Text through a faded gradient is painted through its mask too, run by run.</summary>
    [Fact]
    public void TextIsMasked()
    {
        using var doc = Open(Fade + @"<text x=""10"" y=""100"" font-size=""50"" fill=""url(#g)"">HH</text>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int bt = Array.IndexOf(page, "BT"), et = Array.IndexOf(page, "ET");
        Assert.EndsWith(" gs", page[bt - 1], StringComparison.Ordinal);
        Assert.Equal("Q", page[et + 1]);
    }

    /// <summary>A colour's own alpha in a stop is its opacity too: <c>rgba(…, 0.5)</c> masks at a half.</summary>
    [Fact]
    public void AStopColoursAlphaIsItsOpacity()
    {
        var at = Render(@"<linearGradient id=""g""><stop offset=""0"" stop-color=""rgba(255,0,0,0.5)""/><stop offset=""1"" stop-color=""rgba(255,0,0,0.5)""/></linearGradient>" +
                        @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((255, 128, 128), at(100, 50));
    }

    /// <summary>Opaque stops need no mask: the fill is painted as before, with no state of its own.</summary>
    [Fact]
    public void OpaqueStopsNeedNoMask()
    {
        using var doc = Open(@"<linearGradient id=""g""><stop offset=""0"" stop-color=""red"" stop-opacity=""1""/><stop offset=""1"" stop-color=""blue""/></linearGradient>" +
                             @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.DoesNotContain(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.Contains("f", page);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertNear((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance = 14)
    {
        Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
    }

    private static PdfDictionary Pattern(PdfDocument doc, string name)
    {
        var patterns = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[new PdfName("Pattern")]));
        return Assert.IsType<PdfDictionary>(doc.ResolveObject(patterns[new PdfName(name)]));
    }

    private static double[] Numbers(PdfDocument doc, PdfObject array)
        => Assert.IsType<PdfArray>(doc.ResolveObject(array)).Select(o => Math.Round(((PdfNumber)doc.ResolveObject(o)).DoubleValue, 4)).ToArray();

    private static PdfDocument Open(string body) => PdfDocument.Open(new MemoryStream(Convert(body)));

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int, int, int)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    private static string[] Lines(byte[] content) => Encoding.Latin1.GetString(content).Split('\n').Select(l => l.Trim()).ToArray();
}
