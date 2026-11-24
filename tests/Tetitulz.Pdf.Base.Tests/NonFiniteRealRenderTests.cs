// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// A real the file supplies past ±3.4×10³⁸ is finite as a <c>double</c> and infinite as the
/// <c>float</c> both renderers draw with. Each renderer reads it as the rule
/// <see cref="MatrixGuard"/> states: an operator with such an operand is skipped, a graphics-state
/// entry is ignored, and a form, pattern, annotation or page box whose geometry holds one is not
/// drawn (a page box reads as none). An SVG writer writing the infinity —
/// <c>stroke-width="Infinity"</c>, <c>transform="matrix(NaN,…)"</c> — would have a viewer drop it
/// for its default, and the two renderers would disagree.
/// </summary>
/// <remarks>
/// Each case is three pages: the value past float's range must render exactly as the page
/// <b>without</b> that operator or entry does — raster pixels and SVG text alike — and a finite
/// value must render differently from it (the potency: the fixture draws what it varies).
/// </remarks>
public sealed class NonFiniteRealRenderTests
{
    /// <summary>10⁴⁰ written as a real; an integer literal that size saturates at 2⁶³ in the parser.</summary>
    private const string PastFloat = "10000000000000000000000000000000000000000.5";

    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    private const string Axial =
        "/ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>";

    /// <summary>A page: its resources, content, objects from 5, <c>/MediaBox</c> and annotations.</summary>
    private sealed record Page(string Resources, string Content, string[]? Objects = null,
                               string MediaBox = "[0 0 100 100]", string Annots = "");

    /// <summary>
    /// <see cref="With"/> holds <c>{V}</c>; <see cref="Finite"/> is a value that draws differently;
    /// <see cref="Reported"/>: the skip is said through <see cref="RenderDiagnostics"/> — an operator,
    /// a form, a pattern or an annotation dropped whole. An entry read as absent is not.
    /// </summary>
    private sealed record Case(Page With, Page Without, string Finite, bool Reported = true);

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["w"] = new(new("", "0 0 1 RG {V} w 10 10 m 90 90 l S"), new("", "0 0 1 RG 10 10 m 90 90 l S"), "6"),
        ["d array"] = new(new("", "0 0 1 RG 4 w [{V} 3] 0 d 10 10 m 90 90 l S"), new("", "0 0 1 RG 4 w 10 10 m 90 90 l S"), "9"),
        ["d phase"] = new(new("", "0 0 1 RG 4 w [6 6] {V} d 10 10 m 90 90 l S"), new("", "0 0 1 RG 4 w 10 10 m 90 90 l S"), "3"),
        ["re"] = new(new("", "0 0 1 rg 0 0 {V} 50 re f"), new("", "0 0 1 rg f"), "40"),
        ["Tf"] = new(new("/Font << /F 5 0 R >>", "BT /F {V} Tf 10 40 Td (AB) Tj ET", new[] { Helvetica }),
                     new("/Font << /F 5 0 R >>", "BT 10 40 Td (AB) Tj ET", new[] { Helvetica }), "10"),
        ["Tz"] = new(new("/Font << /F 5 0 R >>", "BT /F 10 Tf {V} Tz 10 40 Td (AB) Tj ET", new[] { Helvetica }),
                     new("/Font << /F 5 0 R >>", "BT /F 10 Tf 10 40 Td (AB) Tj ET", new[] { Helvetica }), "50"),
        ["Td"] = new(new("/Font << /F 5 0 R >>", "BT /F 10 Tf {V} 40 Td (AB) Tj ET", new[] { Helvetica }),
                     new("/Font << /F 5 0 R >>", "BT /F 10 Tf (AB) Tj ET", new[] { Helvetica }), "30"),
        ["TJ"] = new(new("/Font << /F 5 0 R >>", "BT /F 10 Tf 10 40 Td [(A) {V} (B)] TJ ET", new[] { Helvetica }),
                     new("/Font << /F 5 0 R >>", "BT /F 10 Tf 10 40 Td ET", new[] { Helvetica }), "-2000"),
        ["ExtGState LW"] = new(new("/ExtGState << /G << /LW {V} >> >>", "/G gs 0 0 1 RG 10 10 m 90 90 l S"),
                               new("/ExtGState << /G << >> >>", "/G gs 0 0 1 RG 10 10 m 90 90 l S"), "6", false),
        ["ExtGState ML"] = new(new("/ExtGState << /G << /ML {V} >> >>", "/G gs 0 0 1 RG 10 w 10 10 m 50 90 l 90 10 l S"),
                               new("/ExtGState << /G << >> >>", "/G gs 0 0 1 RG 10 w 10 10 m 50 90 l 90 10 l S"), "1", false),
        ["ExtGState D"] = new(new("/ExtGState << /G << /D [[{V} 3] 0] >> >>", "/G gs 0 0 1 RG 4 w 10 10 m 90 90 l S"),
                              new("/ExtGState << /G << >> >>", "/G gs 0 0 1 RG 4 w 10 10 m 90 90 l S"), "9", false),
        ["ExtGState Font"] = new(new("/Font << /F 5 0 R >> /ExtGState << /G << /Font [5 0 R {V}] >> >>", "BT /F 10 Tf /G gs 10 40 Td (AB) Tj ET", new[] { Helvetica }),
                                 new("/Font << /F 5 0 R >> /ExtGState << /G << >> >>", "BT /F 10 Tf /G gs 10 40 Td (AB) Tj ET", new[] { Helvetica }), "20", false),
        ["form Matrix"] = new(new("/XObject << /X 5 0 R >>", "/X Do", new[] { Form("/BBox [0 0 100 100] /Matrix [{V} 0 0 1 0 0]") }),
                              new("/XObject << /X 5 0 R >>", "", new[] { Form("/BBox [0 0 100 100]") }), "0.5"),
        ["form BBox"] = new(new("/XObject << /X 5 0 R >>", "/X Do", new[] { Form("/BBox [0 0 {V} 100]") }),
                            new("/XObject << /X 5 0 R >>", "", new[] { Form("/BBox [0 0 100 100]") }), "20"),
        ["shading pattern Matrix"] = new(new("/Pattern << /P 5 0 R >>", "/Pattern cs /P scn 0 0 100 100 re f",
                                             new[] { "<< /PatternType 2 /Matrix [{V} 0 0 1 0 0] /Shading << " + Axial + " >> >>" }),
                                         new("/Pattern << >>", "/Pattern cs /P scn 0 0 100 100 re f"), "1"),
        ["tiling XStep"] = new(new("/Pattern << /P 5 0 R >>", "/Pattern cs /P scn 0 0 100 100 re f", new[] { Tiling(xStep: "{V}") }),
                               new("/Pattern << >>", "/Pattern cs /P scn 0 0 100 100 re f"), "10"),
        ["tiling YStep"] = new(new("/Pattern << /P 5 0 R >>", "/Pattern cs /P scn 0 0 100 100 re f", new[] { Tiling(yStep: "{V}") }),
                               new("/Pattern << >>", "/Pattern cs /P scn 0 0 100 100 re f"), "10"),
        ["tiling BBox"] = new(new("/Pattern << /P 5 0 R >>", "/Pattern cs /P scn 0 0 100 100 re f", new[] { Tiling(bboxRight: "{V}") }),
                              new("/Pattern << >>", "/Pattern cs /P scn 0 0 100 100 re f"), "10"),
        ["annotation Rect"] = new(new("", "", Annots: " /Annots [<< /Type /Annot /Subtype /Square /Rect [10 10 {V} 50] /C [1 0 0] >>]"),
                                  // An annotation that is there and not drawn: a hidden one (/F 2).
                                  new("", "", Annots: " /Annots [<< /Type /Annot /Subtype /Square /Rect [10 10 90 50] /C [1 0 0] /F 2 >>]"), "90"),
        ["annotation border width"] = new(new("", "", Annots: " /Annots [<< /Type /Annot /Subtype /Square /Rect [10 10 90 50] /C [1 0 0] /BS << /W {V} >> >>]"),
                                          new("", "", Annots: " /Annots [<< /Type /Annot /Subtype /Square /Rect [10 10 90 50] /C [1 0 0] /BS << >> >>]"), "8", false),
        ["annotation Border"] = new(new("", "", Annots: " /Annots [<< /Type /Annot /Subtype /Square /Rect [10 10 90 50] /C [1 0 0] /Border [0 0 {V}] >>]"),
                                    new("", "", Annots: " /Annots [<< /Type /Annot /Subtype /Square /Rect [10 10 90 50] /C [1 0 0] >>]"), "8", false),
        ["MediaBox"] = new(new("", "0 0 1 rg 0 0 50 50 re f", MediaBox: "[0 0 {V} 100]"),
                           new("", "0 0 1 rg 0 0 50 50 re f", MediaBox: ""), "100", false),
    };

    public static IEnumerable<object[]> CaseNames => Cases.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Raster_AValuePastFloat_RendersAsTheEntryLeftOut(string name)
    {
        Case c = Cases[name];
        byte[] without = Raster(c.Without, "", out _);
        Assert.Equal(without, Raster(c.With, PastFloat, out List<string> reports));
        Assert.NotEqual(without, Raster(c.With, c.Finite, out _));
        // The skip is said, not only done: a render that drew nothing for another reason looks alike.
        Assert.Equal(c.Reported, reports.Any(r => r.Contains("past the range of a float")));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Svg_AValuePastFloat_RendersAsTheEntryLeftOut_AndWritesNoNonFiniteNumber(string name)
    {
        Case c = Cases[name];
        string svg = Svg(c.With, PastFloat);
        var doc = XDocument.Parse(svg);
        Assert.DoesNotContain(doc.Descendants().SelectMany(e => e.Attributes()),
            a => a.Value.Contains("Infinity") || a.Value.Contains("NaN"));

        string without = Svg(c.Without, "");
        Assert.Equal(without, svg);
        Assert.NotEqual(without, Svg(c.With, c.Finite));
    }

    private static string Form(string geometry)
        => $"<< /Type /XObject /Subtype /Form {geometry} /Length 23 >>\nstream\n0 0 1 rg 0 0 50 50 re f\nendstream";

    private static string Tiling(string xStep = "10", string yStep = "10", string bboxRight = "10")
        => $"<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 {bboxRight} 10] /XStep {xStep} /YStep {yStep} /Resources << >> /Length 21 >>\nstream\n0 0 1 rg 0 0 5 5 re f\nendstream";

    /// <summary>The raster page, and what the renderer reported on this thread while drawing it.</summary>
    private static byte[] Raster(Page page, string value, out List<string> reports)
    {
        var mine = new List<string>();
        int thread = System.Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (System.Environment.CurrentManagedThreadId == thread) lock (mine) mine.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var input = new MemoryStream(Pdf(page, value));
            return PdfPageRenderer.RenderPageToPixels(input, 0, 72).Pixels;
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Sink;
            reports = mine;
        }
    }

    private static string Svg(Page page, string value)
    {
        using var input = new MemoryStream(Pdf(page, value));
        return PdfSvgRenderer.RenderPageToSvg(input, 0);
    }

    private static byte[] Pdf(Page page, string value)
    {
        string V(string s) => s.Replace("{V}", value);
        string content = V(page.Content);
        string mediaBox = page.MediaBox.Length > 0 ? " /MediaBox " + V(page.MediaBox) : "";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R{mediaBox} /Contents 4 0 R /Resources << {V(page.Resources)} >>{V(page.Annots)} >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        objects.AddRange((page.Objects ?? new string[0]).Select(V));

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
           .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
