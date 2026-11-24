// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A <c>/Lab</c> image's <c>/Range</c> (§8.6.5.4, Table 65) written as an <b>indirect
/// reference</b> — the axis scaling every a* and b* sample of the image passes through.
/// </summary>
/// <remarks>
/// <para><b>Such a defect is visible only as a colour.</b> Re-reading <c>/Range</c> with
/// <c>PdfDictionary.GetArray</c>, which does not follow a reference, beside a
/// <c>CieParams.ForLab</c> call that does would scale an indirect <c>/Range</c>'s samples by
/// Table 65's ±100 default and then clamp them to the file's real range — two different answers to
/// one question, inside one function.</para>
///
/// <para><b>One read.</b> <c>CieParams</c> carries <c>AMin/AMax/BMin/BMax</c>, resolved and parsed
/// once, and both renderers take the range from it, which makes the scale and the clamp
/// structurally incapable of disagreeing.</para>
///
/// <para><b>Why the assertion is a comparison.</b> Asserting a specific RGB would pin the CIE
/// conversion rather than the range read, and would move whenever the white point did. Asserting
/// that the indirect spelling matches the <i>direct</i> one — with a floor proving the two ranges
/// really do produce different colours — fails for exactly the defect and for nothing else.</para>
/// </remarks>
public class IndirectLabRangeTests
{
    /// <summary>A narrow range, so the a* axis spans half what the default does.</summary>
    private const string NarrowRange = "[-50 50 -50 50]";

    /// <summary>
    /// The same <c>/Range</c> written directly and as <c>7 0 R</c> must produce the same pixel.
    /// Unresolved, the indirect form silently fell back to ±100.
    /// </summary>
    [Fact]
    public void AnIndirectRangeScalesTheSameAsADirectOne()
    {
        SKColor direct   = Render(LabImagePdf(NarrowRange));
        SKColor indirect = Render(LabImagePdf("7 0 R"));

        Assert.Equal(direct, indirect);
    }

    /// <summary>
    /// <b>The floor.</b> The narrow range and the ±100 default really do give different colours for
    /// this sample — without which the assertion above would pass for a renderer that ignored
    /// <c>/Range</c> altogether, which is precisely the bug.
    /// </summary>
    [Fact]
    public void TheTwoRangesProduceDifferentColours()
    {
        SKColor narrow  = Render(LabImagePdf(NarrowRange));
        SKColor wide    = Render(LabImagePdf("[-100 100 -100 100]"));

        Assert.NotEqual(narrow, wide);
    }

    /// <summary>And an absent <c>/Range</c> is Table 65's ±100 default, not something else.</summary>
    [Fact]
    public void AnAbsentRangeIsTheTableDefault()
    {
        SKColor absent = Render(LabImagePdf(null));
        SKColor wide   = Render(LabImagePdf("[-100 100 -100 100]"));

        Assert.Equal(wide, absent);
    }

    // ------------------------------------------------------------------

    /// <summary>The image covers (50,50)-(150,150), so its single pixel is sampled at the centre.</summary>
    private static SKColor Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!.GetPixel(100, bmp.Height - 1 - 100);
    }

    /// <summary>
    /// One page drawing a 1×1 <c>/Lab</c> image. Object 7 holds the narrow range so it can be
    /// referenced.
    /// </summary>
    /// <remarks>
    /// <b>The sample value is the whole difficulty, and the first choice made the test vacuous.</b>
    /// <c>LabToSRgb</c> clamps a* and b* to the range carried by its <c>CieParams</c>, so a sample
    /// at the <i>top</i> of the axis lands on that clamp whichever scaling produced it: against a
    /// narrow ±50 range, <c>a=255</c> scales correctly to 50 and incorrectly (via the ±100 default)
    /// to 100, and the clamp turns both into 50. The defect is invisible at the extremes and shows
    /// only in the interior — <c>a=b=160</c> scales to 12.7 correctly and 25.5 incorrectly, both
    /// well inside ±50, so nothing is clamped and the two colours separate. The first version of
    /// this fixture used 255 and its motivating mutation survived.
    /// </remarks>
    private static byte[] LabImagePdf(string range)
    {
        const string content = "q 100 0 0 100 50 50 cm /Im0 Do Q\n";
        string rangeEntry = range == null ? "" : " /Range " + range;
        // L*=100, a*=160, b*=160 as raw bytes. Written from char codes so the source stays ASCII.
        string samples = new string(new[] { (char)100, (char)160, (char)160 });

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources << /XObject << /Im0 5 0 R >> >> >>",
            StreamObj(content),
            StreamObj(samples,
                "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8"
                + " /ColorSpace [/Lab << /WhitePoint [0.9505 1.0 1.089]" + rangeEntry + " >>]"),
            "null",
            "[-50 50 -50 50]");
    }

    private static string StreamObj(string data, string extraKeys = null) =>
        "<< " + (extraKeys != null ? extraKeys + " " : "") +
        "/Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
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
