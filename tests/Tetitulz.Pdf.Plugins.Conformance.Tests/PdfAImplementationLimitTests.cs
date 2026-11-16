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
/// The implementation limits: A65 (the objects and the drawn content — ISO 19005-1 6.1.12, 19005-2/3
/// 6.1.13, the published profiles rules -1 to -9) and A66 (page boundaries, 19005-2/3 6.1.13, rule 6.1.13-11).
/// PDF/A-4 has none.
/// </summary>
public class PdfAImplementationLimitTests
{
    private static byte[] Document(string pagesExtra, string pageExtra, string content, params string[] extra)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1" + pagesExtra + " >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R" + pageExtra + " >>",
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

    private const string Box = " /MediaBox [0 0 100 100]";

    private static List<PdfAViolation> Coded(byte[] pdf, int part, string code)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    private static List<PdfAViolation> WithObject(string obj, int part)
        => Coded(Document("", Box, "", obj), part, "A65");

    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A65

    /// <summary>
    /// Rule -10 (ISO 19005-1 6.1.12, 19005-2/3 6.1.13): an embedded CMap's largest CID is at most
    /// 65 535 — a range's last CID counts, not its first.
    /// </summary>
    [Theory]
    [InlineData("1 begincidrange <0000> <00FF> 65400 endcidrange", 2, "a CMap mapping to CID 65655")]
    [InlineData("1 begincidrange <0000> <00FF> 65280 endcidrange", 2, null)]
    [InlineData("1 begincidchar <01> 70000 endcidchar", 1, "a CMap mapping to CID 70000")]
    [InlineData("1 begincidchar <01> 70000 endcidchar", 4, null)]
    public void A65_AnEmbeddedCMapsLargestCid(string sections, int part, string? expected)
    {
        string text = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap " +
                      "1 begincodespacerange <0000> <FFFF> endcodespacerange " + sections + " endcmap end end";
        string cmap = "<< /Type /CMap /CMapName /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                      "/Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "\nendstream";
        AssertFinding(WithObject(cmap, part), expected);
    }

    [Theory]
    [InlineData(130, 1, "object 3: a name of 130 bytes")]
    [InlineData(130, 2, "a name of 130 bytes")]
    [InlineData(130, 4, null)]
    [InlineData(127, 2, null)]
    public void A65_ANameLongerThan127Bytes_UsedOrNot(int length, int part, string? expected)
    {
        // A resource name nothing draws: the published profile's 6-1-13-t04-fail-a.
        string name = "/" + new string('S', length);
        var pdf = Document("", Box + " /Resources << /XObject << " + name + " 5 0 R >> >>", "",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 0 >>\nstream\n\nendstream");
        AssertFinding(Coded(pdf, part, "A65"), expected);
    }

    /// <summary>
    /// A name whose bytes are not UTF-8 is as long as its bytes: 100 bytes of <c>#E9</c> are 100, within
    /// the limit — counting its Latin-1 reading's UTF-8 said 200 — and 128 of them are over it.
    /// </summary>
    [Theory]
    [InlineData(100, null)]
    [InlineData(128, "a name of 128 bytes")]
    public void A65_ANameWhoseBytesAreNotUtf8_IsCountedInItsBytes(int length, string? expected)
    {
        string name = "/" + string.Concat(Enumerable.Repeat("#E9", length));
        var pdf = Document("", Box + " /Resources << /XObject << " + name + " 5 0 R >> >>", "",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 0 >>\nstream\n\nendstream");
        AssertFinding(Coded(pdf, 2, "A65"), expected);
    }

    // -------------------------------------------------------------- A87

    private const string Tint = "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>";

    /// <summary>
    /// PDF/A-2 and -3's 6.1.8 asks for UTF-8 where a name is a font's, a colourant's or a structure
    /// type's (the published profiles rule 6.1.8-1): each, read from bytes that are not UTF-8, is A87 — the second PDF/A suite's
    /// pdfa2-6-1-13-bfo-t11-fail has <c>/BaseFont /SHJHEG+A#c3'</c>. The UTF-8 spelling of each is not.
    /// </summary>
    [Theory]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /SHJHEG+A#c3' >>", "/BaseFont /SHJHEG+A#C3'")]
    [InlineData("<< /Type /FontDescriptor /FontName /Ab#E9 /Flags 32 >>", "/FontName /Ab#E9")]
    [InlineData("[/Separation /Sp#E9 /DeviceGray " + Tint + "]", "a /Separation colourant /Sp#E9")]
    [InlineData("[/DeviceN [/A /B#E9] /DeviceGray " + Tint + "]", "a /DeviceN colourant /B#E9")]
    [InlineData("<< /Type /StructElem /S /P#E9 /P 1 0 R >>", "/S /P#E9")]
    [InlineData("<< /S /P#E9 /P 1 0 R /K 0 >>", "/S /P#E9")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Ab#C3#A9 >>", null)]
    [InlineData("[/Separation /Sp#C3#A9 /DeviceGray " + Tint + "]", null)]
    public void A87_AFontColourantOrStructureTypeNameNotUtf8(string obj, string? expected)
    {
        AssertFinding(Coded(Document("", Box, "", obj), 2, "A87"), expected);
        AssertFinding(Coded(Document("", Box, "", obj), 3, "A87"), expected);
    }

    /// <summary>
    /// Only those names: an action's <c>/S</c> (no <c>/P</c>), an <c>/S</c> in a dictionary another <c>/Type</c> says is no structure
    /// element though it has a <c>/P</c>, a resource name and a dictionary key read
    /// from bytes that are not UTF-8 are not A87 — the control, a font's, in the same file, is.
    /// </summary>
    [Fact]
    public void A87_OtherNamesNotUtf8_AreNotJudged()
    {
        var pdf = Document("", Box + " /Resources << /XObject << /F#E9 5 0 R >> >>", "",
            "<< /S /Go#E9 /D [3 0 R /Fit] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /P 3 0 R /S /An#E9 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Ab#E9 /K#E9 1 >>");

        var found = Assert.Single(Coded(pdf, 2, "A87"));
        Assert.Contains("/BaseFont /Ab#E9", found.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Go#E9", found.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("F#E9", found.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("K#E9", found.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("An#E9", found.Message, StringComparison.Ordinal);
    }

    /// <summary>The rule is PDF/A-2's and -3's: PDF/A-1 does not have it, and PDF/A-4 says it of every name (A4-06).</summary>
    [Fact]
    public void A87_IsNotPdfA1sOrPdfA4s()
    {
        var pdf = Document("", Box, "", "<< /Type /Font /Subtype /Type1 /BaseFont /Ab#E9 >>");

        Assert.Empty(Coded(pdf, 1, "A87"));
        Assert.Empty(Coded(pdf, 4, "A87"));
        Assert.Single(Coded(pdf, 4, "A4-06"));
    }

    [Theory]
    [InlineData("2147483648", 2, "the integer 2147483648")]
    [InlineData("-2147483649", 1, "the integer -2147483649")]
    [InlineData("2147483647", 2, null)]
    [InlineData("99999999999999999999", 2, "the integer 9223372036854775807")]        // past a long: the parser saturates it
    [InlineData("-2147483648", 1, null)]
    [InlineData("32767.5", 1, "the real 32767.5")]
    [InlineData("32767.5", 2, null)]
    [InlineData("-32766.5", 1, null)]
    [InlineData("0.000000000000000000000000000000000000001", 2, "the real 1E-39")]
    [InlineData("0.000000000000000000000000000000000000001", 1, null)]
    [InlineData("0.0", 2, null)]
    [InlineData("100000000000000000000000000000.0", 2, null)]                         // a real, however large its integer part
    [InlineData("100000000000000000000000000000.0", 1, "the real 1E+29")]
    [InlineData("2147483648", 4, null)]
    public void A65_ANumberBeyondThePartsRange(string number, int part, string? expected)
        => AssertFinding(WithObject("<< /Value " + number + " >>", part), expected);

    [Theory]
    [InlineData(32768, 2, "a string of 32768 bytes")]
    [InlineData(32767, 3, null)]
    [InlineData(32768, 1, null)]
    [InlineData(65536, 1, "a string of 65536 bytes")]
    public void A65_AStringLongerThanThePartAdmits(int length, int part, string? expected)
        => AssertFinding(WithObject("<< /Value (" + new string('a', length) + ") >>", part), expected);

    [Theory]
    [InlineData(8192, 1, "an array of 8192 elements")]
    [InlineData(8191, 1, null)]
    [InlineData(8192, 2, null)]
    public void A65_PdfA1_AnArrayOfMoreThan8191Elements(int count, int part, string? expected)
        => AssertFinding(WithObject("[" + string.Join(" ", Enumerable.Repeat("0", count)) + "]", part), expected);

    [Theory]
    [InlineData(4096, 1, "a dictionary of 4096 entries")]
    [InlineData(4095, 1, null)]
    [InlineData(4096, 3, null)]
    public void A65_PdfA1_ADictionaryOfMoreThan4095Entries(int count, int part, string? expected)
        => AssertFinding(WithObject("<< " + string.Join(" ", Enumerable.Range(0, count).Select(i => "/K" + i + " 0")) + " >>", part), expected);

    [Theory]
    [InlineData(9, 1, "a DeviceN space of 9 colorants")]
    [InlineData(8, 1, null)]
    [InlineData(9, 2, null)]
    [InlineData(33, 2, "a DeviceN space of 33 colorants")]
    public void A65_ADeviceNSpaceOfMoreColorantsThanThePartAdmits(int count, int part, string? expected)
    {
        string names = string.Join(" ", Enumerable.Range(0, count).Select(i => "/C" + i));
        AssertFinding(WithObject("[/DeviceN [" + names + "] /DeviceCMYK 5 0 R]", part), expected);
    }

    [Theory]
    [InlineData(29, 2, "page 1 content: q nested deeper than 28")]
    [InlineData(28, 2, null)]
    [InlineData(29, 1, "q nested deeper than 28")]
    [InlineData(29, 4, null)]
    public void A65_QNestedDeeperThan28(int depth, int part, string? expected)
    {
        string content = string.Concat(Enumerable.Repeat("q ", depth)) + string.Concat(Enumerable.Repeat("Q ", depth));
        AssertFinding(Coded(Document("", Box, content), part, "A65"), expected);
    }

    [Fact]
    public void A65_QNesting_CountsWhatIsOpen()
    {
        // Twenty-eight deep twice over is never twenty-nine.
        string twice = string.Concat(Enumerable.Repeat("q ", 28)) + string.Concat(Enumerable.Repeat("Q ", 28));
        Assert.Empty(Coded(Document("", Box, twice + twice), 2, "A65"));
    }

    [Fact]
    public void A65_AContentOperand_IsJudged()
        => AssertFinding(Coded(Document("", Box, "2147483648 w"), 2, "A65"), "page 1 content: w: the integer 2147483648");

    // -------------------------------------------------------------- A66

    [Theory]
    [InlineData(" /MediaBox [0 0 2 2]", 2, "page 1 /MediaBox 2 × 2")]
    [InlineData(" /MediaBox [0 0 100 2]", 3, "page 1 /MediaBox 100 × 2")]
    [InlineData(" /MediaBox [0 0 100 100] /CropBox [0 0 14402 14402]", 2, "page 1 /CropBox 14402 × 14402")]
    [InlineData(" /MediaBox [0 0 100 100] /TrimBox [0 0 2 2]", 2, "page 1 /TrimBox 2 × 2")]
    [InlineData(" /MediaBox [0 0 100 100] /BleedBox [0 0 2 2]", 2, "page 1 /BleedBox 2 × 2")]
    [InlineData(" /MediaBox [0 0 100 100] /ArtBox [0 0 2 14401]", 2, "page 1 /ArtBox 2 × 14401")]
    [InlineData(" /MediaBox [0 0 14401 100]", 2, "page 1 /MediaBox 14401 × 100")]
    [InlineData(" /MediaBox [10 10 7 7]", 2, null)]
    [InlineData(" /MediaBox [0 0 14400 3]", 2, null)]
    [InlineData(" /MediaBox [0 0 2 2]", 1, null)]
    [InlineData(" /MediaBox [0 0 2 2]", 4, null)]
    public void A66_APageBoundaryOutsideTheRange(string boxes, int part, string? expected)
        => AssertFinding(Coded(Document("", boxes, ""), part, "A66"), expected);

    [Theory]
    [InlineData(" /MediaBox [0 0 2 2]", "", "page 1 /MediaBox 2 × 2")]
    [InlineData(" /MediaBox [0 0 2 2]", " /MediaBox [0 0 4 4]", null)]
    [InlineData(" /MediaBox [0 0 4 4] /CropBox [0 0 2 2]", "", "page 1 /CropBox 2 × 2")]
    [InlineData(" /MediaBox [0 0 4 4] /CropBox [0 0 2 2]", " /CropBox [0 0 4 4]", null)]
    [InlineData(" /MediaBox [0 0 4 4] /TrimBox [0 0 2 2]", "", null)]                       // TrimBox is not inherited
    public void A66_TheMediaBoxAndCropBox_AsThePageInheritsThem(string pagesExtra, string pageExtra, string? expected)
        => AssertFinding(Coded(Document(pagesExtra, pageExtra, ""), 2, "A66"), expected);
}
