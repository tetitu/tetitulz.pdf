// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A CIE colour space's parameter dictionary must be read whichever way its entries are spelled.
/// </summary>
/// <remarks>
/// <para><b>These are the least visible unresolved reads in the tree, because every one of them has
/// a spec default that is also a perfectly ordinary value.</b> <c>/Gamma</c> 1, <c>/Matrix</c>
/// identity, a D65 <c>/WhitePoint</c>, <c>/Range</c> ±100 — read as absent, nothing fails and
/// nothing is reported; a different colour is simply drawn. A <c>/CalGray</c> space with an
/// indirect <c>/Gamma</c> becomes literally <c>DeviceGray</c>.</para>
///
/// <para><b>Both renderers were wrong together</b>, since <c>PdfColor</c> is the shared colour leaf
/// they both call — so the parity suite is structurally blind to this, exactly as it was for the
/// stencil pair. Absolute pixel evidence is the only kind that can see it.</para>
///
/// <para>The fixture uses <c>/Gamma 2.2</c>, which darkens mid-grey substantially: the luminance
/// of 0.5 becomes 0.5^2.2 ≈ 0.22 rather than 0.5. The gap between sRGB ~129 and ~188 is far outside
/// any rounding question.</para>
/// </remarks>
public class IndirectCieParameterTests
{
    /// <summary>
    /// A <c>/CalGray</c> fill honours its <c>/Gamma</c> whichever way the entry is spelled.
    /// <c>false</c> is the control: the inline spelling always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Gamma 2.2
    [InlineData(true)]   // /Gamma 9 0 R
    public void CalGrayHonoursItsGamma_WhicheverWayItIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(CalGrayPdf(indirect));
        using var bmp = SkiaSharp.SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);

        var centre = bmp.GetPixel(50, 50);

        // 0.5 under gamma 2.2 is luminance ~0.218 -> sRGB ~129. With the gamma dropped it would be ~188.
        Assert.InRange(centre.Red, 118, 140);
    }

    /// <summary>
    /// The floor: without any <c>/Gamma</c> the same fill is luminance 0.5 — sRGB ~188 — so the
    /// assertion above is a claim about the entry being read rather than about the renderer always
    /// painting one grey. (It asserted ~128 under the name <c>CalGrayWithoutGammaIsMidGrey</c> while
    /// CalGray was read as an encoded grey level, against §8.6.5.2.)
    /// </summary>
    [Fact]
    public void CalGrayWithoutGammaIsItsLinearLuminance()
    {
        using var ms = new MemoryStream(CalGrayPdf(indirect: false, withGamma: false));
        using var bmp = SkiaSharp.SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));

        var centre = bmp.GetPixel(50, 50);

        Assert.InRange(centre.Red, 178, 198);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A page filled with <c>/CalGray</c> 0.5. Object 9 holds the gamma when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] CalGrayPdf(bool indirect, bool withGamma = true)
    {
        string gamma = !withGamma ? "" : indirect ? " /Gamma 9 0 R" : " /Gamma 2.2";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 0.5 sc 0 0 100 100 re f\n"),
            "[/CalGray << /WhitePoint [0.9505 1.0 1.089]" + gamma + " >>]",
            "null", "null", "null",
            "2.2");
    }

    private static string Stream(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
        + " >>\nstream\n" + data + "endstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
