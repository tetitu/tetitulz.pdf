// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Optional content when every group is ON — the default state, and the one the renderers reach
/// with no hidden set at all (<c>PdfOptionalContentRefs.BuildHiddenSet</c> answers null).
/// </summary>
/// <remarks>
/// <b>"Nothing hidden" is not "everything visible".</b> An OCMD can show content only while groups
/// are OFF — <c>/P /AllOff</c>, <c>/P /AnyOff</c>, a <c>/VE [/Not …]</c> expression — and with every
/// group on that content is hidden. <c>IsOcEntryHidden</c> returned "visible" for any entry whenever
/// the hidden set was empty or null, so such content was painted on every default render, in both
/// renderers (they share the method). The direct tests of <c>IsOcmdHidden</c> passed an empty set
/// straight in and never met the shortcut.
/// </remarks>
public class OptionalContentAllOnTests
{
    [Theory]
    [InlineData("<< /Type /OCMD /OCGs [5 0 R] /P /AllOff >>")]
    [InlineData("<< /Type /OCMD /OCGs [5 0 R] /P /AnyOff >>")]
    [InlineData("<< /Type /OCMD /VE [/Not 5 0 R] >>")]
    public void ContentShownOnlyWhileAGroupIsOffIsHiddenWhenEveryGroupIsOn(string membership)
    {
        byte[] pdf = Pdf(membership);

        Assert.False(RasterPaintsRed(pdf), "the raster renderer painted content its OCMD hides");
        Assert.False(SvgPaintsRed(pdf), "the SVG renderer painted content its OCMD hides");
    }

    /// <summary>
    /// An annotation's <c>/OC</c> (§12.5.2) by the same rule. The raster loop had its own copy of the
    /// empty-set shortcut, so once the shared method was fixed the two renderers would have
    /// disagreed on this page; the SVG loop never had one.
    /// </summary>
    [Theory]
    [InlineData("<< /Type /OCMD /OCGs [5 0 R] /P /AllOff >>", false)]
    [InlineData("<< /Type /OCMD /OCGs [5 0 R] /P /AllOn >>", true)]
    public void AnAnnotationsMembershipIsEvaluatedWhenEveryGroupIsOn(string membership, bool painted)
    {
        byte[] pdf = Pdf(membership, annotation: true);

        Assert.Equal(painted, RasterPaintsRed(pdf));
        Assert.Equal(painted, SvgPaintsRed(pdf));
    }

    /// <summary>The controls: the same page under <c>/AllOn</c>, and under the bare group, paints.</summary>
    [Theory]
    [InlineData("<< /Type /OCMD /OCGs [5 0 R] /P /AllOn >>")]
    [InlineData("5 0 R")]
    public void ContentShownWhileItsGroupIsOnIsPainted(string membership)
    {
        byte[] pdf = Pdf(membership);

        Assert.True(RasterPaintsRed(pdf));
        Assert.True(SvgPaintsRed(pdf));
    }

    private static bool RasterPaintsRed(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 50, 50);
        return c.Red > 200 && c.Green < 60 && c.Blue < 60;
    }

    private static bool SvgPaintsRed(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        // Hidden content is exported in a group a viewer does not display (§8.11.3.2 keeps its
        // state), so what paints is what stands outside one.
        return XDocument.Parse(svg).Descendants()
            .Any(e => e.Name.LocalName == "path"
                      && ((e.Attribute("fill")?.Value ?? "").ToLowerInvariant() is "#ff0000" or "rgb(255,0,0)")
                      && !e.Ancestors().Any(a => (string)a.Attribute("display") == "none"));
    }

    /// <summary>
    /// One 100 × 100 page whose red square sits in <c>/OC /MC0 BDC … EMC</c>, <c>/MC0</c> being
    /// <paramref name="membership"/> (object 6, or a reference to the group itself). The one group,
    /// object 5, is listed in <c>/OCProperties</c> with a default configuration that turns nothing off.
    /// </summary>
    private static byte[] Pdf(string membership, bool annotation = false)
    {
        bool direct = membership.EndsWith(" R");
        string props = direct ? membership : "6 0 R";
        string content = annotation ? "" : "/OC /MC0 BDC 1 0 0 rg 10 10 80 80 re f EMC";
        const string appearance = "1 0 0 rg 0 0 80 80 re f";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] /D << /Order [5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /Properties << /MC0 " + props + " >> >>"
                + (annotation ? " /Annots [7 0 R]" : "") + " >>",
            string.Format(CultureInfo.InvariantCulture, "<< /Length {0} >>\nstream\n{1}\nendstream", content.Length, content),
            "<< /Type /OCG /Name (Layer) >>",
            direct ? "<< >>" : membership,
            "<< /Type /Annot /Subtype /Square /Rect [10 10 90 90] /F 4 /OC " + props + " /AP << /N 8 0 R >> >>",
            string.Format(CultureInfo.InvariantCulture,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 80 80] /Length {0} >>\nstream\n{1}\nendstream",
                appearance.Length, appearance),
        };

        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
