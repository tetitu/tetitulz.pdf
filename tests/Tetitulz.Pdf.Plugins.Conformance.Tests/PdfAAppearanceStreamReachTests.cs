// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The PDF/A walks read every resource dictionary a page's content reaches, through
/// <c>PdfXValidator.ScanPage</c>: an annotation's appearance stream and a shading pattern's
/// <c>/ExtGState</c> are drawn as the page's own content is, so A06 and A09-A14 judge them as X04
/// and X09 do. Each reported case has a conformant control of the same shape.
/// </summary>
public class PdfAAppearanceStreamReachTests
{
    /// <summary>A06 judges a font painted only by an annotation's appearance stream.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A06_AFontPaintedOnlyByAnAnnotationAppearance_IsJudged(bool embedded, bool reported)
    {
        string font = embedded
            ? "<< /Type /Font /Subtype /TrueType /BaseFont /AppearanceFace /FontDescriptor 8 0 R >>"
            : "<< /Type /Font /Subtype /TrueType /BaseFont /AppearanceFace >>";
        byte[] pdf = Build(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> /Annots [5 0 R] >>",
            Stream("", "q Q"),
            "<< /Type /Annot /Subtype /FreeText /Rect [0 0 100 20] /F 4 /DA (/Helv 12 Tf 0 g) /AP << /N 6 0 R >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /Helv 7 0 R >> >>",
                "BT /Helv 12 Tf 2 5 Td (x) Tj ET"),
            font,
            "<< /Type /FontDescriptor /FontName /AppearanceFace /Flags 32 /FontFile2 9 0 R >>",
            Stream("", " "),
        });

        var result = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2);

        Assert.Equal(reported, result.Violations.Any(v => v.Code == "A06" && v.Message.Contains("AppearanceFace")));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A99");
    }

    /// <summary>A09-A14 read an /ExtGState inside an annotation's appearance stream.</summary>
    [Theory]
    [InlineData("/BM /Multiply /ca 0.5", true)]
    [InlineData("/BM /Normal /ca 1", false)]
    public void TransparencyInsideAnAnnotationAppearance_IsJudgedUnderPart1(string graphicsState, bool reported)
    {
        byte[] pdf = Build(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> /Annots [5 0 R] >>",
            Stream("", "q Q"),
            "<< /Type /Annot /Subtype /Highlight /Rect [0 0 10 10] /QuadPoints [0 10 10 10 0 0 10 0] /F 4 /AP << /N 6 0 R >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /ExtGState << /GS0 << " + graphicsState + " >> >> >>",
                "/GS0 gs 0 0 10 10 re f"),
        });

        var result = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part1);

        Assert.Equal(reported, result.Violations.Any(v => v.Code == "A12"));
        Assert.Equal(reported, result.Violations.Any(v => v.Code == "A14"));
    }

    /// <summary>
    /// A shading pattern's <c>/ExtGState</c> (ISO 32000-1 Table 76) is a graphics state the paint
    /// applies: the /ExtGState rules read it as they read the page's — A12/A14 under PDF/A-1, A40
    /// (a non-standard blend mode) from PDF/A-2.
    /// </summary>
    [Theory]
    [InlineData(1, "/BM /Multiply /ca 0.5", "A12", true)]
    [InlineData(1, "/BM /Multiply /ca 0.5", "A14", true)]
    [InlineData(1, "/BM /Normal /ca 1", "A12", false)]
    [InlineData(1, "/BM /Normal /ca 1", "A14", false)]
    [InlineData(2, "/BM /ReviewProbe", "A40", true)]
    [InlineData(2, "/BM /Multiply", "A40", false)]
    public void AShadingPatternsGraphicsState_IsReadByTheExtGStateRules(int part, string graphicsState, string code, bool reported)
    {
        byte[] pdf = Build(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Pattern << /P0 5 0 R >> >> >>",
            Stream("", "/Pattern cs /P0 scn 0 0 10 10 re f"),
            "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 10 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >> /ExtGState << " + graphicsState + " >> >>",
        });

        var result = PdfAValidator.Validate(new MemoryStream(pdf), (PdfAConformancePart)part);

        Assert.Equal(reported, result.Violations.Any(v => v.Code == code));
    }

    private static string Stream(string entries, string content)
        => "<< " + entries + (entries.Length > 0 ? " " : "") + "/Length " + Encoding.ASCII.GetByteCount(content).ToString(CultureInfo.InvariantCulture) +
           " >>\nstream\n" + content + "\nendstream";

    private static byte[] Build(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
