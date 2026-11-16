// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A region over a paint that goes through a tiling pattern or a soft mask drawing an image: the cover
/// box would hide the image's pixels while the file keeps them, so the redaction is refused. A paint
/// the region does not meet, or one no longer going through the image, is redacted as usual.
/// </summary>
public sealed class RedactionCoveredImageSourceTests
{
    /// <summary>The image's samples, as written; an output holding them still holds the image.</summary>
    private const string Samples = "00FFFF00";

    private static string S(string body, string dict = "") => $"<< {dict} /Length {body.Length} >>\nstream\n{body}\nendstream";

    private const string Cell = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20";

    /// <summary>
    /// Object 6 is a 2 × 2 image; 7 a tiling pattern whose cell draws it (/P1); 8 a graphics state whose
    /// soft mask's group (9) draws it (/GM); 10 a pattern whose cell draws it through a form, 11 (/P2);
    /// 12 a pattern whose cell draws a square alone (/P3); 13 a form filling a square (/Fz); 14 a Type 3
    /// font whose glyph fills its box (/T3, its procedure 15); 16 a stencil (/St); 17 a pattern whose cell
    /// draws an inline image (/P4); 18 one whose cell fills through /P1 (/P5); 19 one whose cell fills
    /// under /GM (/P6); 20 one whose cell draws a form, 21, that draws itself (/P7).
    /// </summary>
    private static byte[] Pdf(string content)
    {
        string resources = "/Font << /F1 5 0 R /T3 14 0 R >> /XObject << /Im 6 0 R /Fz 13 0 R /St 16 0 R >>"
            + " /Pattern << /P1 7 0 R /P2 10 0 R /P3 12 0 R /P4 17 0 R /P5 18 0 R /P6 19 0 R /P7 20 0 R >>"
            + " /ExtGState << /GM 8 0 R /GN << /Type /ExtGState /SMask /None >> >>"
            + " /Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >> >>";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << {resources} >> >>",
            S(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            S(Samples + ">", "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode"),
            S("q 20 0 0 20 0 0 cm /Im Do Q", Cell + " /Resources << /XObject << /Im 6 0 R >> >>"),
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 9 0 R >> >>",
            S("q 200 0 0 200 0 0 cm /Im Do Q", "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /XObject << /Im 6 0 R >> >>"),
            S("/Fx Do", Cell + " /Resources << /XObject << /Fx 11 0 R >> >>"),
            S("q 20 0 0 20 0 0 cm /Im Do Q", "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << /XObject << /Im 6 0 R >> >>"),
            S("0 0 10 10 re f", Cell),
            S("10 10 100 100 re f", "/Type /XObject /Subtype /Form /BBox [0 0 200 200]"),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 15 0 R >>"
                + " /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>",
            S("1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f"),
            S("4080>", "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ImageMask true /BitsPerComponent 1 /Filter /ASCIIHexDecode"),
            S("q 20 0 0 20 0 0 cm BI /W 2 /H 2 /CS /G /BPC 8 /F /AHx ID " + Samples + "> EI Q", Cell),
            S("/Pattern cs /P1 scn 0 0 20 20 re f", Cell + " /Resources << /Pattern << /P1 7 0 R >> >>"),
            S("/GM gs 0 0 20 20 re f", Cell + " /Resources << /ExtGState << /GM 8 0 R >> >>"),
            S("/Fs Do", Cell + " /Resources << /XObject << /Fs 21 0 R >> >>"),
            S("/Fs Do", "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << /XObject << /Fs 21 0 R >> >>"),
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The region, (20, 20) to (60, 60).</summary>
    private static byte[] Redact(byte[] pdf)
    {
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output,
            new[] { new PdfRedactionArea { PageIndex = 0, X = 20, Y = 20, Width = 40, Height = 40 } });
        return output.ToArray();
    }

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "a fill through the image pattern under the region", "/Pattern cs /P1 scn 10 10 100 100 re f", true },
        new object[] { "the same fill away from the region", "/Pattern cs /P1 scn 120 120 50 50 re f", false },
        new object[] { "a stroke through the image pattern under the region", "/Pattern CS /P1 SCN 10 10 100 100 re S", true },
        new object[] { "a fill once the colour is grey again", "/Pattern cs /P1 scn 0 g 10 10 100 100 re f", false },
        new object[] { "a fill after the pattern's q ... Q", "q /Pattern cs /P1 scn Q 10 10 100 100 re f", false },
        new object[] { "a fill while only the stroke goes through the pattern", "/Pattern CS /P1 SCN 10 10 100 100 re f", false },
        new object[] { "text through the image pattern under the region", "/Pattern cs /P1 scn BT /F1 12 Tf 25 30 Td (Hello) Tj ET", true },
        new object[] { "invisible text under the image mask", "/GM gs BT 3 Tr /F1 12 Tf 25 30 Td (Hello) Tj ET", false },
        new object[] { "an image under the image mask", "/GM gs q 40 0 0 40 20 20 cm /Im Do Q", true },
        new object[] { "a fill under the image mask", "/GM gs 0 g 10 10 100 100 re f", true },
        new object[] { "a fill once /None removes the mask", "/GM gs /GN gs 10 10 100 100 re f", false },
        new object[] { "a shading under the image mask", "/GM gs /Sh sh", true },
        new object[] { "a fill through a pattern whose cell's form draws the image", "/Pattern cs /P2 scn 10 10 100 100 re f", true },
        new object[] { "a fill through a pattern whose cell draws a square", "/Pattern cs /P3 scn 10 10 100 100 re f", false },
        new object[] { "a stroke whose width alone reaches the region", "/Pattern CS /P1 SCN 4 w 62 20 30 30 re S", true },
        new object[] { "a fill after a q ... Q the pattern was set before", "/Pattern cs /P1 scn q Q 10 10 100 100 re f", true },
        new object[] { "a form drawn through the pattern filling under the region", "/Pattern cs /P1 scn /Fz Do", true },
        new object[] { "a Type 3 glyph through the image pattern under the region", "/Pattern cs /P1 scn BT /T3 12 Tf 25 30 Td (a) Tj ET", true },
        new object[] { "a stencil through the image pattern under the region", "/Pattern cs /P1 scn q 40 0 0 40 20 20 cm /St Do Q", true },
        new object[] { "an inline image under the image mask", "/GM gs q 40 0 0 40 20 20 cm BI /W 2 /H 2 /CS /G /BPC 8 /F /AHx ID " + Samples + "> EI Q", true },
        new object[] { "an inline stencil through the image pattern", "/Pattern cs /P1 scn q 40 0 0 40 20 20 cm BI /W 2 /H 2 /IM true /F /AHx ID 4080> EI Q", true },
        new object[] { "a fill through a pattern whose cell draws an inline image", "/Pattern cs /P4 scn 10 10 100 100 re f", true },
        new object[] { "a fill through a pattern whose cell fills through the image pattern", "/Pattern cs /P5 scn 10 10 100 100 re f", true },
        new object[] { "a fill through a pattern whose cell fills under the image mask", "/Pattern cs /P6 scn 10 10 100 100 re f", true },
        new object[] { "a fill through a pattern whose cell's form draws itself", "/Pattern cs /P7 scn 10 10 100 100 re f", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_region_over_a_paint_through_an_image_it_could_only_cover_is_refused(string name, string content, bool refused)
    {
        byte[] pdf = Pdf(content);

        if (refused)
        {
            var refusal = Assert.Throws<PdfRedactionException>(() => Redact(pdf));
            Assert.True(refusal.Message.Contains("draws an image", StringComparison.Ordinal), name + ": " + refusal.Message);
        }
        else
        {
            // Accepted, and the image the page still paints elsewhere is still in the file.
            Assert.True(Encoding.Latin1.GetString(Redact(pdf)).Contains(Samples, StringComparison.Ordinal), name);
        }
    }
}
