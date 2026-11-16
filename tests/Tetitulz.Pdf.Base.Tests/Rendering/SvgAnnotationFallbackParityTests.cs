// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A synthesised annotation appearance exports the border and the opacity the raster path draws.
/// </summary>
/// <remarks>
/// <para><b>Two divergences in one method, both in the direction the exporter loses.</b> A
/// <c>Square</c> and a <c>Circle</c> emitted <c>stroke</c> and nothing else, so SVG's own default
/// width of 1 applied however the file had set §12.5.4's <c>/BS /W</c> — a 6-point dashed border
/// exported as a 1-point solid one. Three other subtypes in the same <c>switch</c> already emitted
/// the width, which makes it a divergence inside one method rather than an absent feature. And the
/// dash was never missing: <c>AnnotationFallbackModel.BorderWidth</c> hands it back through an out
/// parameter, and this file discarded it with <c>out _</c>.</para>
///
/// <para><b>The opacity was the other half.</b> Both <c>/CA</c> reads used the plain indexer, which
/// returns the <i>stored</i> value — so an indirect <c>/CA</c> was indistinguishable from an absent
/// one and the annotation exported fully opaque, while the raster path resolved it at both of its
/// own reads. The same fix landing on one renderer only.</para>
/// </remarks>
public class SvgAnnotationFallbackParityTests
{
    /// <summary>A `/BS /W 6` Square border exports at 6, not at SVG's default of 1.</summary>
    [Fact]
    public void ASquareBorderExportsItsDeclaredWidth()
    {
        XElement rect = FirstStroked(Svg(SquarePdf(width: "6", dash: null)), "rect");

        string raw = rect.Attribute("stroke-width")?.Value;
        Assert.True(raw != null,
            "the Square carries no stroke-width, so a viewer strokes it at SVG's default of 1 "
          + "however the file set /BS /W");
        Assert.Equal(6f, float.Parse(raw, CultureInfo.InvariantCulture), 3);
    }

    /// <summary>And a `/BS /D [3 2]` border exports dashed.</summary>
    [Fact]
    public void ASquareBorderExportsItsDash()
    {
        XElement rect = FirstStroked(Svg(SquarePdf(width: "6", dash: "[3 2]")), "rect");

        string dash = rect.Attribute("stroke-dasharray")?.Value;
        Assert.True(dash != null,
            "the Square carries no stroke-dasharray, so a dashed border exports solid");
        Assert.Equal("3,2", dash);
    }

    /// <summary>A Circle carries it too — the other subtype that emitted neither.</summary>
    [Fact]
    public void ACircleBorderExportsItsDeclaredWidth()
    {
        XElement ellipse = FirstStroked(Svg(CirclePdf(width: "6")), "ellipse");

        string raw = ellipse.Attribute("stroke-width")?.Value;
        Assert.True(raw != null, "the Circle carries no stroke-width");
        Assert.Equal(6f, float.Parse(raw, CultureInfo.InvariantCulture), 3);
    }

    /// <summary>
    /// The acceptance control: <c>/W 0</c> means <b>no border</b>, so no width is written.
    /// </summary>
    /// <remarks>
    /// §12.5.4 makes a zero border width mean the border is absent, which is the opposite of
    /// §8.4.3.2's zero <i>line</i> width meaning "thinnest renderable". Without this, writing the
    /// width unconditionally would satisfy the tests above and give every borderless annotation a
    /// hairline it never asked for.
    /// </remarks>
    [Fact]
    public void AZeroBorderWidthWritesNoWidthAtAll()
    {
        XElement rect = FirstStroked(Svg(SquarePdf(width: "0", dash: null)), "rect");

        Assert.Null(rect.Attribute("stroke-width"));
    }

    /// <summary>An indirect <c>/CA</c> reaches the exported group's opacity.</summary>
    [Fact]
    public void AnIndirectOpacityIsExported()
    {
        string svg = Svg(SquarePdf(width: "6", dash: null, indirectCa: true));

        XElement group = XDocument.Parse(svg).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "g"
                              && e.Attribute("data-annot-subtype") != null);

        Assert.True(group != null, "the export produced no annotation group:" + Environment.NewLine + svg);

        string raw = group!.Attribute("opacity")?.Value;
        Assert.True(raw != null, "the annotation group carries no opacity attribute");
        Assert.Equal(0.5f, float.Parse(raw, CultureInfo.InvariantCulture), 3);
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private static XElement FirstStroked(string svg, string localName)
    {
        XElement[] found = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == localName)
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .ToArray();
        Assert.True(found.Length > 0,
            $"the export drew no stroked <{localName}>:" + Environment.NewLine + svg);
        return found[0];
    }

    private static string Svg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static byte[] SquarePdf(string width, string dash, bool indirectCa = false)
    {
        string bs = dash == null
            ? $"/BS << /W {width} /S /S >>"
            : $"/BS << /W {width} /S /D /D {dash} >>";
        string ca = indirectCa ? "/CA 6 0 R " : "";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
            "/Annots [5 0 R] /Resources << >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Square /Rect [20 20 80 80] /F 4 /C [1 0 0] " +
            ca + bs + " >>",
        };
        if (indirectCa) objects.Add("0.5");
        return Assemble(objects);
    }

    private static byte[] CirclePdf(string width) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
        "/Annots [5 0 R] /Resources << >> >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        $"<< /Type /Annot /Subtype /Circle /Rect [20 20 80 80] /F 4 /C [1 0 0] /BS << /W {width} >> >>",
    });

    private static byte[] Assemble(List<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n')
           .Append("0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
