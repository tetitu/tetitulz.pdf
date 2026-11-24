// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Table 192's <c>/MK /R</c> — "the number of degrees by which the widget annotation shall be
/// rotated counterclockwise relative to the page" — honoured by everything that constructs an
/// appearance: the form filler, the flattener's regeneration, and both renderers' no-<c>/AP</c>
/// fallbacks.
/// </summary>
/// <remarks>
/// <para><b>Where the turn goes.</b> §12.5.5's algorithm maps the form's <c>/BBox</c> through its
/// <c>/Matrix</c> and fits the result to <c>/Rect</c>; it has no rotation step, so neither renderer
/// applies a second turn. A generated appearance is laid out upright in a box of the rectangle's
/// swapped dimensions and carries a <c>/Matrix</c> that turns it onto the rectangle — which any
/// conforming reader then draws turned.</para>
///
/// <para><b>The fixture is a tall, narrow field</b> — 30 wide, 200 high — with a value the
/// upright reading cannot fit across it. Turned by 90 the value runs up the field, so its ink is
/// taller than it is wide; upright it is the other way round. That asymmetry is the assertion, on
/// decoded pixels for the raster paths and on the parsed transform for the SVG one; the written
/// appearance is asserted on its <c>/BBox</c> and <c>/Matrix</c>, with the transformed box compared
/// to the rectangle rather than six literals quoted.</para>
/// </remarks>
public class RotatedWidgetAppearanceTests
{
    private const float X0 = 50, Y0 = 50, X1 = 80, Y1 = 250;   // 30 wide, 200 high
    private const string Value = "HELLO WORLD";

    // ------------------------------------------------------------------ the form filler

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Fill_LaysTheValueOutUprightAndTurnsTheBoxOntoTheRectangle(int rotation)
    {
        byte[] filled = Fill(Form(rotation, value: null));

        using var doc = PdfDocument.Open(new MemoryStream(filled));
        PdfStream ap = NormalAppearanceOf(doc);
        double[] bbox = Numbers(doc, ap, PdfName.BBox);

        bool quarter = rotation == 90 || rotation == 270;
        Assert.Equal(new[] { 0d, 0d, quarter ? Y1 - Y0 : X1 - X0, quarter ? X1 - X0 : Y1 - Y0 }, bbox);

        if (rotation == 0)
        {
            Assert.Null(doc.ResolveObject(ap[PdfName.Matrix]));
            return;
        }

        double[] m = Numbers(doc, ap, PdfName.Matrix);
        AssertTurnsOntoTheRectangle(m, bbox[2], bbox[3], X1 - X0, Y1 - Y0);
    }

    /// <summary>End to end: the filled document renders its value along the turn.</summary>
    [Fact]
    public void Fill_AQuarterTurnedFieldRendersItsValueUpTheField()
    {
        AssertInkTallerThanWide(Fill(Form(90, value: null)));
        AssertInkWiderThanTall(Fill(Form(0, value: null)));
    }

    // ------------------------------------------------------------------ the flattener

    /// <summary>
    /// A stale appearance regenerated while flattening is turned the same way. The fixture carries
    /// the blank template appearance §12.7.3.3 describes — a widget with no <c>/AP</c> at all is
    /// left in place by the flattener, and its ink would then be the renderer's own fallback — and
    /// the flattened page is asserted to keep no widget, so the ink can only be the stamped form.
    /// </summary>
    [Fact]
    public void Flatten_AQuarterTurnedFieldIsStampedTurned()
    {
        byte[] turned = Flatten(Form(90, value: Value, needAppearances: true, staleAppearance: true));
        AssertFullyFlattened(turned, expectMatrix: true);
        AssertInkTallerThanWide(turned);

        byte[] upright = Flatten(Form(0, value: Value, needAppearances: true, staleAppearance: true));
        AssertFullyFlattened(upright, expectMatrix: false);
        AssertInkWiderThanTall(upright);
    }

    /// <summary>
    /// No widget survives on the page — so no renderer fallback can flatter the result — and the
    /// one stamped form carries the turn as its <c>/Matrix</c> exactly when the field was turned.
    /// </summary>
    private static void AssertFullyFlattened(byte[] pdf, bool expectMatrix)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfDictionary page = doc.Pages[0].Dictionary;
        Assert.Null(page[PdfName.Annots]);

        var resources = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(page[PdfName.Resources]));
        var xobjects = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(resources[PdfName.XObject]));
        var stamped = new List<PdfStream>();
        foreach (var kvp in xobjects)
            if (doc.ResolveObject(kvp.Value) is PdfStream form) stamped.Add(form);

        PdfStream only = Assert.Single(stamped);
        Assert.Equal(expectMatrix, doc.ResolveObject(only[PdfName.Matrix]) is PdfArray);
    }

    // ------------------------------------------------------------------ the renderers' fallbacks

    [Fact]
    public void RasterFallback_DrawsAQuarterTurnedValueUpTheField()
    {
        AssertInkTallerThanWide(Form(90, value: Value));
        AssertInkWiderThanTall(Form(0, value: Value));
    }

    /// <summary>
    /// A half turn mirrors the placement: the value starts at the field's right edge and runs
    /// leftwards, as the upright one starts at the left edge and runs rightwards. Neither fallback
    /// clips to the field, so a value wider than it overflows — on the far side from where it starts,
    /// which is what tells the two apart.
    /// </summary>
    [Fact]
    public void RasterFallback_AHalfTurnStartsTheValueAtTheOppositeEdge()
    {
        using var turned = PixelAssert.Render(Form(180, value: Value));
        var (tLeft, tTop, tRight, tBottom) = Ink(turned);
        Assert.True(tRight - tLeft > tBottom - tTop, "a half-turned value still runs across the field");
        Assert.InRange(tRight, PixelAssert.X(X1) - 12, PixelAssert.X(X1));
        Assert.True(tLeft < PixelAssert.X(X0), "the overflow of a half-turned value spills past the left edge");

        using var upright = PixelAssert.Render(Form(0, value: Value));
        var (uLeft, _, uRight, _) = Ink(upright);
        Assert.InRange(uLeft, PixelAssert.X(X0), PixelAssert.X(X0) + 12);
        Assert.True(uRight > PixelAssert.X(X1), "the overflow of an upright value spills past the right edge");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void SvgFallback_TurnsTheValueByTheSameCoefficients(int rotation)
    {
        XElement text = FallbackText(Form(rotation, value: Value));
        double[] m = Matrix(text.Attribute("transform")?.Value);
        const double size = 8;   // the /DA size

        // SVG glyphs run down the page, so the upright text matrix is [s 0 0 -s]; a turn [a b c d]
        // applied after that flip reads [a·s b·s -c·s -d·s].
        double[] expected = rotation switch
        {
            90  => new[] { 0, size, size, 0 },
            180 => new[] { -size, 0, 0, size },
            270 => new[] { 0, -size, -size, 0 },
            _   => new[] { size, 0, 0, -size },
        };
        for (int i = 0; i < 4; i++) Assert.Equal(expected[i], m[i], 3);

        // The baseline lands inside the field whichever way it is turned.
        Assert.InRange(m[4], (double)X0, X1);
        Assert.InRange(m[5], (double)Y0, Y1);
    }

    // ------------------------------------------------------------------ helpers

    private static (int left, int top, int right, int bottom) Ink(SKBitmap bmp)
    {
        var bounds = PixelAssert.InkBounds(bmp);
        Assert.True(bounds.HasValue, "the page is blank: the value was not drawn at all");
        return bounds.Value;
    }

    private static void AssertInkTallerThanWide(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        var (left, top, right, bottom) = Ink(bmp);
        Assert.True(bottom - top > 2 * (right - left),
            $"a quarter-turned value should run up the field; ink is {right - left + 1} wide and {bottom - top + 1} high");
    }

    private static void AssertInkWiderThanTall(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        var (left, top, right, bottom) = Ink(bmp);
        Assert.True(right - left > bottom - top,
            $"an upright value should run across the field; ink is {right - left + 1} wide and {bottom - top + 1} high");
    }

    /// <summary>
    /// The corners of <c>[0 0 boxW boxH]</c> through the matrix span exactly <c>[0 0 w h]</c>.
    /// </summary>
    private static void AssertTurnsOntoTheRectangle(double[] m, double boxW, double boxH, double w, double h)
    {
        Assert.Equal(6, m.Length);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (0d, 0d), (boxW, 0d), (0d, boxH), (boxW, boxH) })
        {
            double tx = m[0] * x + m[2] * y + m[4], ty = m[1] * x + m[3] * y + m[5];
            minX = System.Math.Min(minX, tx); maxX = System.Math.Max(maxX, tx);
            minY = System.Math.Min(minY, ty); maxY = System.Math.Max(maxY, ty);
        }
        Assert.Equal(0, minX, 6); Assert.Equal(0, minY, 6);
        Assert.Equal(w, maxX, 6); Assert.Equal(h, maxY, 6);
    }

    private static byte[] Fill(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFiller.Fill(input, output, new Dictionary<string, string> { ["f1"] = Value });
        return output.ToArray();
    }

    private static byte[] Flatten(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFlattener.Flatten(input, output, null);
        return output.ToArray();
    }

    private static PdfStream NormalAppearanceOf(PdfDocument doc)
    {
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        var widget = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(Assert.Single(annots!)));
        var ap = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(widget[PdfName.AP]));
        return Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
    }

    private static double[] Numbers(PdfDocument doc, PdfDictionary dict, PdfName key)
    {
        var array = Assert.IsAssignableFrom<PdfArray>(doc.ResolveObject(dict[key]));
        return Enumerable.Range(0, array.Count)
            .Select(i => ((PdfNumber)doc.ResolveObject(array[i])).DoubleValue).ToArray();
    }

    private static XElement FallbackText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        XElement group = Assert.Single(svg.Descendants().Where(
            e => e.Attribute("data-annot-subtype")?.Value == "Widget"));
        return Assert.Single(group.Descendants().Where(e => e.Name.LocalName == "text"));
    }

    private static double[] Matrix(string transform)
    {
        Assert.NotNull(transform);
        Assert.StartsWith("matrix(", transform);
        return transform.Substring(7).TrimEnd(')').Split(',')
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>
    /// A 200×300 page with one text field over <c>/Rect [50 50 80 250]</c>; <paramref name="rotation"/>
    /// is written as <c>/MK /R</c> when non-zero. With <paramref name="staleAppearance"/> the widget
    /// carries a blank template <c>/AP /N</c> — the shape a producer leaves under
    /// <c>/NeedAppearances</c> — and without it no appearance at all.
    /// </summary>
    private static byte[] Form(int rotation, string value, bool needAppearances = false, bool staleAppearance = false)
    {
        string mk = rotation == 0 ? "" : $" /MK << /R {rotation} >>";
        string v = value == null ? "" : $" /V ({value})";
        string na = needAppearances ? " /NeedAppearances true" : "";
        string ap = staleAppearance ? " /AP << /N 7 0 R >>" : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R]" + na +
            " /DA (/Helv 8 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (f1) /F 4 /DA (/Helv 8 Tf 0 g)" +
            $" /Rect [{F(X0)} {F(Y0)} {F(X1)} {F(Y1)}]{mk}{v}{ap} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 {F(X1 - X0)} {F(Y1 - Y0)}] /Length 0 >>\nstream\n\nendstream");
    }

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
            sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
