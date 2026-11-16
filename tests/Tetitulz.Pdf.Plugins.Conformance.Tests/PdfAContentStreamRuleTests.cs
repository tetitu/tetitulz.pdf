// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The content stream rules: A50 (an operator ISO 32000 does not define), A51 (a rendering intent other
/// than the four), A52 (a stream naming a resource without a resource dictionary of its own, PDF/A-2
/// onwards), A53 (a name its resources do not define, PDF/A-4) — ISO 19005-1 6.2.9/6.2.10, ISO
/// 19005-2/3/4 6.2.2 and the rendering intent clauses, the published profile's rules of those numbers. Only what the
/// page draws is judged.
/// </summary>
public class PdfAContentStreamRuleTests
{
    private static string Stream(string content, string keys = "")
        => "<< " + keys + " /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream";

    /// <summary>Objects: 1 catalog, 2 pages (carrying <paramref name="pagesExtras"/>), 3 page, 4 its content, 5 on <paramref name="extras"/>.</summary>
    private static byte[] Document(string content, string pageExtras = "/Resources << >>", string pagesExtras = "", params string[] extras)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 " + pagesExtras + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " + pageExtras + " >>",
            Stream(content),
        };
        objects.AddRange(extras);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static List<PdfAViolation> Coded(byte[] pdf, int part, string code)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    private const string Form5 = "/Resources << /XObject << /X0 5 0 R >> >>";

    // -------------------------------------------------------------- A50

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A50_AnUndefinedOperator_IsEveryPartsFinding(int part)
    {
        Assert.Contains("page 1 content: Undefinedop", Assert.Single(Coded(Document("q Undefinedop Q"), part, "A50")).Message, StringComparison.Ordinal);
        Assert.Single(Coded(Document("BX q Undefinedop Q EX"), part, "A50"));          // BX/EX excuse nothing
        Assert.Empty(Coded(Document("q 0 0 9 9 re n Q BT ET"), part, "A50"));
    }

    [Fact]
    public void A50_OnlyADrawnFormIsJudged()
    {
        string form = Stream("Undefinedop", "/Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << >>");
        Assert.Contains("object 5: Undefinedop", Assert.Single(Coded(Document("/X0 Do", Form5, "", form), 2, "A50")).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(Document("0 0 9 9 re n", Form5, "", form), 2, "A50"));
    }

    [Fact]
    public void A50_AFormTwoPagesDraw_IsReportedOnce()
    {
        // Page 3 draws the form at 5; page 6, with its own content 7, draws it too and adds a finding of its own.
        string text = Encoding.ASCII.GetString(Document("/X0 Do", Form5, "",
            Stream("Undefinedop", "/Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << >>"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 7 0 R " + Form5 + " >>",
            Stream("/X0 Do Undefinedtwo")));
        const string one = "/Kids [3 0 R] /Count 1", two = "/Kids [3 0 R 6 0 R] /Count 2";
        text = text.Replace(one, two, StringComparison.Ordinal);
        int shift = two.Length - one.Length;
        var lines = text.Split('\n');
        int entry = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 19 && lines[i].EndsWith(" 00000 n ", StringComparison.Ordinal))
            {
                long offset = long.Parse(lines[i].Substring(0, 10), CultureInfo.InvariantCulture);
                if (++entry > 2) offset += shift;                     // objects 3 on follow the edit
                lines[i] = offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n ";
            }
            if (i > 0 && lines[i - 1] == "startxref")
                lines[i] = (long.Parse(lines[i], CultureInfo.InvariantCulture) + shift).ToString(CultureInfo.InvariantCulture);
        }
        var a50 = Assert.Single(Coded(Encoding.ASCII.GetBytes(string.Join("\n", lines)), 2, "A50"));
        Assert.Contains("page 1, object 5: Undefinedop", a50.Message, StringComparison.Ordinal);
        Assert.Contains("page 2 content: Undefinedtwo", a50.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("page 2, object 5", a50.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A51

    [Theory]
    [InlineData("/Custom ri", "", true)]
    [InlineData("/Perceptual ri", "", false)]
    [InlineData("/G0 gs", "/Resources << /ExtGState << /G0 << /RI /Custom >> >> >>", true)]
    [InlineData("/G0 gs", "/Resources << /ExtGState << /G0 << /RI /Saturation >> >> >>", false)]
    [InlineData("BI /W 1 /H 1 /CS /G /BPC 8 /Intent /Custom ID \u0080 EI", "", true)]
    public void A51_ARenderingIntent_IsOneOfTheFour(string content, string pageExtras, bool reported)
    {
        foreach (int part in new[] { 1, 2, 4 })
            Assert.Equal(reported ? 1 : 0, Coded(Document(content, pageExtras.Length == 0 ? "/Resources << >>" : pageExtras), part, "A51").Count);
    }

    [Fact]
    public void A51_AnImagesIntent_IsReadToo()
    {
        string image = Stream("\u0080", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Intent /Custom");
        Assert.Contains("/XObject /X0 /Intent /Custom", Assert.Single(Coded(Document("/X0 Do", Form5, "", image), 2, "A51")).Message,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A52

    [Fact]
    public void A52_APageInheritingItsResources_IsPdfA2sFinding()
    {
        byte[] inherited = Document("/G0 gs", "", "/Resources << /ExtGState << /G0 << /LW 1 >> >> >>");
        Assert.Contains("page 1 content: /G0 (ExtGState)", Assert.Single(Coded(inherited, 2, "A52")).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(inherited, 1, "A52"));
        Assert.Empty(Coded(Document("/G0 gs", "/Resources << /ExtGState << /G0 << /LW 1 >> >> >>"), 2, "A52"));
        // A device space is no resource.
        Assert.Empty(Coded(Document("/DeviceRGB cs 1 0 0 sc 0 0 9 9 re f", ""), 2, "A52"));
    }

    [Fact]
    public void A52_AFormWithoutResources_NamingOne_IsReported()
    {
        string bare = Stream("/F0 gs", "/Type /XObject /Subtype /Form /BBox [0 0 9 9]");
        string own = Stream("/F0 gs", "/Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << /ExtGState << /F0 << /LW 1 >> >> >>");
        Assert.Contains("object 5: /F0 (ExtGState)", Assert.Single(Coded(Document("/X0 Do", Form5, "", bare), 2, "A52")).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(Document("/X0 Do", Form5, "", own), 2, "A52"));
    }

    [Fact]
    public void A52_AType3FontWithoutResources_WhoseGlyphNamesOne_IsReported()
    {
        static string Font(string resources) =>
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << /a 6 0 R >> " +
            "/Encoding << /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1] " + resources + " >>";
        string glyph = Stream("1 0 d0 /G0 gs");
        string page = "/Resources << /Font << /F1 5 0 R >> /ExtGState << /G0 << /LW 1 >> >> >>";
        Assert.Single(Coded(Document("BT /F1 1 Tf (a) Tj ET", page, "", Font(""), glyph), 2, "A52"));
        Assert.Empty(Coded(Document("BT /F1 1 Tf (a) Tj ET", page, "", Font("/Resources << /ExtGState << /G0 << /LW 1 >> >> >>"), glyph), 2, "A52"));
    }

    // -------------------------------------------------------------- A53

    [Theory]
    [InlineData("/G9 gs", true)]
    [InlineData("/G0 gs", false)]
    [InlineData("/P0 /Q0 BDC EMC", true)]
    [InlineData("/F9 1 Tf", true)]
    public void A53_PdfA4_EveryNameIsInTheResources(string content, bool reported)
    {
        byte[] pdf = Document("BT " + content + " ET", "/Resources << /ExtGState << /G0 << /LW 1 >> >> >>");
        Assert.Equal(reported ? 1 : 0, Coded(pdf, 4, "A53").Count);
        Assert.Empty(Coded(pdf, 2, "A53"));
    }
}
