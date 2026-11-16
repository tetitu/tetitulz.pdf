// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Ink, PolyLine, Polygon and Line annotations imported from FDF/XFDF carry the appearance the
/// renderers draw for them without one: their points, moved into the annotation's box, in their colour
/// at their border width. Asserted on the parsed appearance stream, and against the renderer's own
/// fallback by pixels; refused, by name, where the annotation asks for drawing the fallback does not do.
/// </summary>
public sealed class AnnotationPathAppearanceTests
{
    private static byte[] OnePage()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 3 0 R >>",
        };
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Write(FormDataAnnotation annotation)
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        Assert.Equal(1, PdfAnnotationWriter.WriteAnnotations(input, output, new[] { annotation }));
        return output.ToArray();
    }

    private static string AppearanceContent(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var annot = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].GetAnnotations()[0]));
        var ap = Assert.IsType<PdfDictionary>(doc.ResolveObject(annot[PdfName.AP]));
        var form = Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
        return Shortest(Encoding.ASCII.GetString(form.GetDecodedData()));
    }

    /// <summary>The content with every number in its shortest form (<c>10.0000</c> as <c>10</c>), so an assertion reads the geometry, not the format.</summary>
    private static string Shortest(string content) =>
        System.Text.RegularExpressions.Regex.Replace(content, @"-?\d+\.\d+",
            m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    private static FormDataAnnotation Ink() =>
        new FormDataAnnotation(PdfAnnotationSubtypes.Ink)
        {
            Rect = new double[] { 40, 50, 160, 150 },
            Color = new double[] { 1, 0, 0 },
            BorderWidth = 3,
            InkList = { new double[] { 50, 60, 100, 140, 150, 60 }, new double[] { 60, 100, 140, 100 } },
        };

    [Fact]
    public void AnInkAnnotationDrawsEachStrokeInItsBox()
    {
        string content = AppearanceContent(Write(Ink()));
        Assert.Null(PdfAnnotationWriter.LastAppearanceSkipped);
        Assert.Contains("1 0 0 RG", content);
        Assert.Contains("3 w", content);
        // Page points less the box's corner (40, 50): two strokes, each moved and stroked.
        Assert.Contains("10 10 m\n60 90 l\n110 10 l\nS", content);
        Assert.Contains("20 50 m\n100 50 l\nS", content);
    }

    [Fact]
    public void APolygonIsClosedAndFilled_APolyLineIsNeither()
    {
        var polygon = new FormDataAnnotation(PdfAnnotationSubtypes.Polygon)
        {
            Rect = new double[] { 20, 20, 120, 120 },
            InteriorColor = new double[] { 0, 0, 1 },
            Vertices = new double[] { 30, 30, 110, 30, 70, 110 },
        };
        string closed = AppearanceContent(Write(polygon));
        Assert.Contains("0 0 1 rg", closed);
        Assert.Contains("10 10 m\n90 10 l\n50 90 l\nh\nB", closed);
        Assert.Contains("0 0 0 RG", closed);   // no /C: black, as the renderer's fallback

        var polyline = new FormDataAnnotation(PdfAnnotationSubtypes.PolyLine)
        {
            Rect = new double[] { 20, 20, 120, 120 },
            InteriorColor = new double[] { 0, 0, 1 },
            Vertices = new double[] { 30, 30, 110, 30, 70, 110 },
        };
        string open = AppearanceContent(Write(polyline));
        Assert.DoesNotContain(" rg", open);
        Assert.DoesNotContain("h\n", open);
        Assert.Contains("10 10 m\n90 10 l\n50 90 l\nS", open);
    }

    [Fact]
    public void ALineIsStrokedBetweenItsEnds()
    {
        var line = new FormDataAnnotation(PdfAnnotationSubtypes.Line)
        {
            Rect = new double[] { 10, 10, 190, 60 },
            Color = new double[] { 0, 0.5, 0 },
            Line = new double[] { 20, 20, 180, 50 },
        };
        string content = AppearanceContent(Write(line));
        Assert.Contains("0 0.5 0 RG", content);
        Assert.Contains("10 10 m\n170 40 l\nS", content);
    }

    /// <summary>
    /// The appearance draws what the renderer's fallback draws: the page rendered with it equals the page
    /// with the same annotation and no <c>/AP</c>, pixel for pixel within anti-aliasing, and differs from
    /// an empty page (the potency).
    /// </summary>
    [Fact]
    public void TheAppearanceDrawsWhatTheFallbackDraws()
    {
        byte[] withAp = Write(Ink());
        byte[] withoutAp = StripAppearance(withAp);
        var drawn = Pixels(withAp);
        var fallback = Pixels(withoutAp);
        var blank = Pixels(OnePage());

        int differing = 0, marked = 0;
        for (int i = 0; i < drawn.Length; i += 4)
        {
            if (System.Math.Abs(drawn[i] - fallback[i]) > 40 || System.Math.Abs(drawn[i + 1] - fallback[i + 1]) > 40) differing++;
            if (drawn[i + 1] < 128 && blank[i + 1] > 200) marked++;
        }
        Assert.True(marked > 200, $"{marked} pixels inked");
        Assert.True(differing < marked / 20, $"{differing} of {marked} inked pixels differ from the fallback");
    }

    /// <summary>What the annotation asks for beyond its points is refused by name; the plain annotation is the control.</summary>
    [Theory]
    [InlineData("Line", "LE", "arrow-first", "line endings")]
    [InlineData("PolyLine", "LE", "arrow-last", "line endings")]
    [InlineData("Polygon", "BE", "cloudy", "cloudy border")]
    [InlineData("Polygon", "IT", "dimension", "intent")]
    [InlineData("Line", "LL", "leader", "leader lines")]
    [InlineData("Line", "Cap", "caption", "caption")]
    public void WhatTheFallbackDoesNotDrawIsRefused(string subtype, string key, string value, string why)
    {
        PdfDictionary Plain()
        {
            var d = new PdfDictionary();
            d.Set(PdfName.Subtype, new PdfName(subtype));
            var pts = new PdfArray();
            foreach (double v in new double[] { 10, 10, 90, 10, 50, 90 }.Take(subtype == "Line" ? 4 : 6)) pts.Add(new PdfNumber(v));
            d.Set(subtype == "Line" ? PdfName.L : PdfName.Vertices, pts);
            return d;
        }
        Assert.NotNull(AnnotationAppearanceBuilder.Build(Plain(), 100, 100, 0, 0, out string none));
        Assert.Null(none);

        PdfDictionary asking = Plain();
        asking.Set(new PdfName(key), value switch
        {
            "arrow-first" => Names("OpenArrow", "None"),
            "arrow-last" => Names("None", "ClosedArrow"),
            "cloudy" => Dict(("S", new PdfName("C")), ("I", new PdfNumber(1))),
            "dimension" => new PdfName("PolygonDimension"),
            "leader" => new PdfNumber(12),
            _ => (PdfObject)PdfBoolean.True,
        });
        Assert.Null(AnnotationAppearanceBuilder.Build(asking, 100, 100, 0, 0, out string reason));
        Assert.Contains(why, reason, System.StringComparison.Ordinal);
    }

    /// <summary>Line endings of /None draw nothing extra, so the appearance is written.</summary>
    [Fact]
    public void NoneLineEndingsAreDrawn()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Subtype, new PdfName("Line"));
        d.Set(PdfName.L, Numbers(10, 10, 90, 90));
        d.Set(PdfName.LE, Names("None", "None"));
        var form = AnnotationAppearanceBuilder.Build(d, 100, 100, 0, 0, out string reason);
        Assert.Null(reason);
        Assert.Contains("10 10 m" + (char)10 + "90 90 l" + (char)10 + "S", Shortest(Encoding.ASCII.GetString(form.RawData)));
    }

    /// <summary>A dashed border (/BS /S /D) is dashed in the appearance, its /D array or the default [3].</summary>
    [Fact]
    public void ADashedBorderIsDashed()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Subtype, new PdfName("PolyLine"));
        d.Set(PdfName.Vertices, Numbers(10, 10, 90, 10, 50, 90));
        d.Set(PdfName.BS, Dict(("S", new PdfName("D")), ("D", Numbers(4, 2)), ("W", new PdfNumber(2))));
        string content = Shortest(Encoding.ASCII.GetString(AnnotationAppearanceBuilder.Build(d, 100, 100, 0, 0, out _).RawData));
        Assert.Contains("[4 2] 0 d", content);

        d.Set(PdfName.BS, Dict(("S", new PdfName("D")), ("W", new PdfNumber(2))));
        Assert.Contains("[3] 0 d", Shortest(Encoding.ASCII.GetString(AnnotationAppearanceBuilder.Build(d, 100, 100, 0, 0, out _).RawData)));

        d.Set(PdfName.BS, Dict(("S", new PdfName("S")), ("W", new PdfNumber(2))));
        Assert.DoesNotContain(" d\n", Encoding.ASCII.GetString(AnnotationAppearanceBuilder.Build(d, 100, 100, 0, 0, out _).RawData));
    }

    /// <summary>Points that are not an even run of at least two finite points draw nothing, and say so.</summary>
    [Fact]
    public void NoUsablePointsDrawNothing()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Subtype, new PdfName("PolyLine"));
        d.Set(PdfName.Vertices, Numbers(10, 10, 90));
        Assert.Null(AnnotationAppearanceBuilder.Build(d, 100, 100, 0, 0, out string reason));
        Assert.Contains("no points", reason, System.StringComparison.Ordinal);
    }

    private static PdfArray Numbers(params double[] values)
    {
        var a = new PdfArray();
        foreach (double v in values) a.Add(new PdfNumber(v));
        return a;
    }

    private static PdfArray Names(params string[] names)
    {
        var a = new PdfArray();
        foreach (string n in names) a.Add(new PdfName(n));
        return a;
    }

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (key, value) in entries) d.Set(new PdfName(key), value);
        return d;
    }

    private static byte[] StripAppearance(byte[] pdf)
    {
        string text = Encoding.GetEncoding("iso-8859-1").GetString(pdf);
        int at = text.LastIndexOf("/AP", System.StringComparison.Ordinal);
        Assert.True(at > 0);
        // Rename the key in place (same length), so the offsets stand and the fallback draws.
        return Encoding.GetEncoding("iso-8859-1").GetBytes(text.Substring(0, at) + "/XP" + text.Substring(at + 3));
    }

    private static byte[] Pixels(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfPageRenderer.RenderPageToPixels(ms, 0, 72).Pixels;
    }
}
