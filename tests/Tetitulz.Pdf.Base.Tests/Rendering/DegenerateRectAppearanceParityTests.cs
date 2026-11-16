// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An annotation with a zero-height <c>/Rect</c> and an appearance stream draws nothing in the
/// raster, so the SVG export draws nothing for it either — the appearance is claimed and not
/// drawn, rather than passed to the synthetic fallback, which drew the <c>/L</c> line the raster
/// left blank. The control, the same annotation with no appearance, is synthesised in both.
/// </summary>
public class DegenerateRectAppearanceParityTests
{
    [Fact]
    public void AZeroHeightRectWithAnAppearance_ExportsNoSyntheticDrawing()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(LinePdf(withAppearance: true)), 0);

        Assert.Empty(Lines(svg));
    }

    [Fact]
    public void AZeroHeightRectWithoutAnAppearance_IsSynthesised()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(LinePdf(withAppearance: false)), 0);

        Assert.Single(Lines(svg));
    }

    private static XElement[] Lines(string svg) =>
        XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "line").ToArray();

    /// <summary>A Line annotation whose /Rect has no height; its appearance strokes the same line.</summary>
    private static byte[] LinePdf(bool withAppearance)
    {
        const string ap = "0 0 1 RG 2 w 0 0 m 100 0 l S";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [5 0 R] /Resources << >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Line /Rect [50 50 150 50] /L [50 50 150 50] /F 4 /C [0 0 1] " +
            (withAppearance ? "/AP << /N 6 0 R >> " : "") + ">>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 0] /Length " + ap.Length + " >>\nstream\n" + ap + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
