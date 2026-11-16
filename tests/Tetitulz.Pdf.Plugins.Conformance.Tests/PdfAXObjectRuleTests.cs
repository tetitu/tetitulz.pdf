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
/// The XObject rules: A58 (an image dictionary — ISO 19005-1 6.2.4, 19005-2/3 6.2.8, 19005-4 6.2.7.1)
/// and A59 (form, reference and PostScript XObjects — 19005-1 6.2.4 to 6.2.7, 19005-2/3 6.2.9,
/// 19005-4 6.2.8.1 and 6.2.8.2), against the published profile's rules of those numbers.
/// </summary>
public class PdfAXObjectRuleTests
{
    internal static byte[] Document(string catalogExtra, string pageExtra, string content, params string[] extra)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" + pageExtra + " >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
        };
        objects.AddRange(extra);
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

    internal static List<PdfAViolation> Coded(byte[] pdf, int part, string code)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    private const string Uses = " /Resources << /XObject << /X0 5 0 R >> >>";

    private static byte[] WithXObject(string dictionary, params string[] more)
        => Document("", Uses, "/X0 Do", new[] { dictionary + "\nstream\nx\nendstream" }.Concat(more).ToArray());

    private static string Image(string keys, string bpc = "/BitsPerComponent 8")
        => "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " + bpc + " " + keys + " /Length 1 >>";

    private static string Form(string keys)
        => "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " + keys + " /Length 1 >>";

    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A58

    [Theory]
    [InlineData("/Interpolate true", "/X0 /Interpolate true")]
    [InlineData("/Interpolate false", null)]
    [InlineData("/Alternates []", "/X0 /Alternates")]
    [InlineData("/OPI << >>", "/X0 /OPI")]
    [InlineData("", null)]
    public void A58_AnImageKey_IsEveryPartsFinding(string keys, string? expected)
    {
        foreach (int part in new[] { 1, 2, 4 })
            AssertFinding(Coded(WithXObject(Image(keys)), part, "A58"), expected);
    }

    [Theory]
    [InlineData("/BitsPerComponent 16", 1, "/X0 /BitsPerComponent 16")]
    [InlineData("/BitsPerComponent 16", 2, null)]
    [InlineData("/BitsPerComponent 16", 4, null)]
    [InlineData("/BitsPerComponent 3", 2, "/X0 /BitsPerComponent 3")]
    [InlineData("/BitsPerComponent 1", 1, null)]
    [InlineData("/BitsPerComponent 2", 1, null)]
    [InlineData("/BitsPerComponent 4", 1, null)]
    [InlineData("/BitsPerComponent 8", 1, null)]
    public void A58_AnImagesDepth_IsOneThePartAdmits(string bpc, int part, string? expected)
        => AssertFinding(Coded(WithXObject(Image("", bpc)), part, "A58"), expected);

    [Theory]
    [InlineData("/ImageMask true", "/BitsPerComponent 2", "an image mask's /BitsPerComponent 2")]
    [InlineData("/ImageMask true", "/BitsPerComponent 16", "an image mask's /BitsPerComponent 16")]
    [InlineData("/ImageMask true", "/BitsPerComponent 1", null)]
    [InlineData("/Mask 6 0 R", "/BitsPerComponent 8", "/X0 /Mask's /BitsPerComponent 2")]
    public void A58_AMasksDepth_IsOne(string keys, string bpc, string? expected)
    {
        string stencil = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /BitsPerComponent 2 /Length 1 >>\nstream\nx\nendstream";
        AssertFinding(Coded(WithXObject(Image(keys, bpc), stencil), 2, "A58"), expected);
    }

    [Theory]
    [InlineData("/I true", "inline image /I true")]
    [InlineData("/Interpolate true", "inline image /I true")]
    [InlineData("/I false", null)]
    [InlineData("/BPC 3", "inline image /BitsPerComponent 3")]
    [InlineData("/IM true /BPC 2", "inline image an image mask's /BitsPerComponent 2")]
    public void A58_AnInlineImage_IsJudgedUnderItsAbbreviations(string keys, string? expected)
    {
        string bpc = keys.Contains("/BPC") ? "" : " /BPC 8";
        string content = "BI /W 1 /H 1 /CS /G" + bpc + " " + keys + " ID x EI";
        foreach (int part in new[] { 1, 4 })
            AssertFinding(Coded(Document("", "", content), part, "A58"), expected);
    }

    [Fact]
    public void A58_AnImageNamedTwice_IsOneOccurrence()
    {
        var pdf = Document("", " /Resources << /XObject << /X0 5 0 R /X1 5 0 R >> >>", "/X0 Do",
            Image("/Interpolate true") + "\nstream\nx\nendstream");
        string message = Assert.Single(Coded(pdf, 2, "A58")).Message;
        int at = message.IndexOf("/Interpolate true", StringComparison.Ordinal);
        Assert.True(at >= 0, message);
        Assert.Equal(-1, message.IndexOf("/Interpolate true", at + 1, StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- A59

    [Theory]
    [InlineData("/OPI << >>", 1, "/X0 /OPI")]
    [InlineData("/OPI << >>", 4, "/X0 /OPI")]
    [InlineData("/Ref << /F (a.pdf) /Page 0 >>", 2, "/X0 /Ref (a reference XObject)")]
    [InlineData("/Ref << /F (a.pdf) /Page 0 >>", 4, "/X0 /Ref (a reference XObject)")]
    [InlineData("/PS 5 0 R", 1, "/X0 /PS")]
    [InlineData("/PS 5 0 R", 2, "/X0 /PS")]
    [InlineData("/PS 5 0 R", 4, null)]
    [InlineData("/Subtype2 /PS", 3, "/X0 /Subtype2 /PS")]
    [InlineData("/Subtype2 /PS", 4, null)]
    [InlineData("/Subtype2 /Other", 2, null)]
    [InlineData("", 1, null)]
    public void A59_AFormKey_IsOneThePartForbids(string keys, int part, string? expected)
        => AssertFinding(Coded(WithXObject(Form(keys)), part, "A59"), expected);

    [Theory]
    [InlineData(1, "/X0 is a PostScript XObject")]
    [InlineData(2, "/X0 is a PostScript XObject")]
    [InlineData(4, null)]
    public void A59_APostScriptXObject_IsForbiddenBeforePdfA4(int part, string? expected)
        => AssertFinding(Coded(WithXObject("<< /Type /XObject /Subtype /PS /Length 1 >>"), part, "A59"), expected);
}
