// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A shading's <c>/Domain</c> written as an <b>indirect reference</b> must be honoured, in both
/// renderers.
/// </summary>
/// <remarks>
/// <para><b>The convention had a hole inside a single file.</b> The SVG shading writer reads
/// <c>/Domain</c> through <c>PdfResolve.Array</c> at one site and read it raw at another; the raster
/// side read it raw at both of its. §8.7.4.5.2 makes <c>/Domain</c> default to <c>[0 1]</c>, so an
/// unresolved read does not fail — it silently substitutes the full parametric range and the
/// gradient runs to the wrong colour.</para>
///
/// <para>The fixture's shading declares <c>/Domain [0 0.5]</c> over a black→white exponential, so
/// the far end of the ramp should be the <b>midpoint</b> colour. Under the defect it reaches white
/// instead — a plausible gradient of the wrong extent, which is why the assertion is on the colour
/// at the far end rather than on "a gradient was emitted".</para>
///
/// <para>Both renderers are asserted because both read it, and the mesh types are deliberately not:
/// for shading types 1 and 4–7 the SVG writer rasterises through the raster interpreter and embeds
/// a PNG, so those have no second reader to diverge from. Axial and radial are the ones SVG emits
/// natively, and therefore the ones with a real twin.</para>
/// </remarks>
public class IndirectShadingDomainParityTests
{
    /// <summary>
    /// The raster render reaches the midpoint colour at the far end of the ramp, not white.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Domain 7 0 R
    [InlineData(false)]  // /Domain [0 0.5]
    public void RasterHonoursTheDomain(bool indirect)
    {
        using var ms = new MemoryStream(AxialShadingPdf(indirect));
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        // The shading spans x = 10..90 at y = 50. Sample near the far end.
        int far = bmp.GetPixel(88, 50).Red;

        Assert.InRange(far, 100, 190);   // midpoint grey, not 255
    }

    /// <summary>
    /// The SVG export's last gradient stop is the midpoint colour, not white.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SvgHonoursTheDomain(bool indirect)
    {
        using var ms = new MemoryStream(AxialShadingPdf(indirect));
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        var doc = XDocument.Parse(svg);
        var gradient = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "linearGradient");
        Assert.NotNull(gradient);

        var stops = gradient!.Elements().Where(e => e.Name.LocalName == "stop").ToList();
        Assert.NotEmpty(stops);

        int red = FirstChannel(stops[stops.Count - 1].Attribute("stop-color")?.Value);
        Assert.InRange(red, 100, 190);
    }

    // ------------------------------------------------------------------

    /// <summary>The red channel of an <c>rgb(r,g,b)</c> or <c>#rrggbb</c> stop colour.</summary>
    private static int FirstChannel(string color)
    {
        Assert.NotNull(color);
        if (color!.StartsWith("#", StringComparison.Ordinal))
            return Convert.ToInt32(color.Substring(1, 2), 16);

        int open = color.IndexOf('(');
        string first = color.Substring(open + 1).Split(',')[0].Trim();
        return (int)Math.Round(double.Parse(first, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A black→white axial shading whose <c>/Domain</c> stops at 0.5, so the far end of the
    /// geometry carries the colour at t = 0.5. Object 7 is that array when
    /// <paramref name="indirect"/>.
    /// </summary>
    private static byte[] AxialShadingPdf(bool indirect)
    {
        string domain = indirect ? "7 0 R" : "[0 0.5]";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh0 5 0 R >> >> >>",
            StreamObj("q 10 40 80 20 re W n /Sh0 sh Q\n"),
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [10 0 90 0]" +
            " /Domain " + domain + " /Extend [true true] /Function 6 0 R >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>",
            "[0 0.5]");
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
