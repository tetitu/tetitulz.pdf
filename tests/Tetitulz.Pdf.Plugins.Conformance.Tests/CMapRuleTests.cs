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
/// ISO 14289-1 7.21.3.3 (UA62), through <see cref="CMapRules"/>, which PDF/UA-2's §8.4.5.4 reads too: a
/// CMap that ISO 32000-1 Table 118 does not list is embedded, an embedded CMap's <c>/WMode</c> is its
/// program's, and a CMap references no CMap but a listed one.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class CMapRuleTests
{
    // -------------------------------------------------------------- the list

    /// <summary>Table 116 and Table 118 list 61 names: the half-width and -H-only entries are in, their neighbours out.</summary>
    [Fact]
    public void ThePredefinedList_IsTheTablesSixtyOneNames()
    {
        Assert.Equal(61, CMapRules.PredefinedCount);
        foreach (string listed in new[] { "GB-EUC-H", "UniCNS-UTF16-V", "UniJIS-UCS2-HW-V", "KSCms-UHC-HW-H",
                                          "KSCpc-EUC-H", "83pv-RKSJ-H", "90pv-RKSJ-H", "H", "V", "Identity-H", "Identity-V" })
            Assert.True(CMapRules.IsPredefined(listed), listed);
        foreach (string unlisted in new[] { "UniJIS2004-UTF16-H", "KSCpc-EUC-V", "83pv-RKSJ-V", "90pv-RKSJ-V",
                                            "Identity", "identity-h", "", null! })
            Assert.False(CMapRules.IsPredefined(unlisted), unlisted ?? "(null)");
    }

    // -------------------------------------------------------------- UA62

    /// <summary>A painted Type 0 font naming a CMap: one Table 118 lists passes, one it does not is reported.</summary>
    [Theory]
    [InlineData("/UniJIS-UTF16-H", false)]
    [InlineData("/Identity-V", false)]
    [InlineData("/UniJIS2004-UTF16-H", true)]
    [InlineData("/MyOwnCMap", true)]
    public void UA62_ANamedCMap_IsPredefinedOrReported(string encoding, bool reported)
    {
        var found = Coded(FontPdf(encoding), "UA62");
        if (!reported) { Assert.Empty(found); return; }
        var finding = Assert.Single(found);
        Assert.Contains($"names the CMap {encoding}, which is not a predefined CMap", finding.Message);
    }

    /// <summary>An embedded CMap's <c>/UseCMap</c>: a listed name passes; an unlisted name or another embedded CMap is reported.</summary>
    [Theory]
    [InlineData("/UseCMap /UniJIS-UCS2-H", null)]
    [InlineData("/UseCMap /UniJIS2004-UTF16-H", "/UseCMap names /UniJIS2004-UTF16-H")]
    [InlineData("/UseCMap 8 0 R", "/UseCMap references another embedded CMap")]
    public void UA62_AnEmbeddedCMapsUseCMap_NamesOnlyAPredefinedCMap(string useCMap, string? expected)
    {
        var found = Coded(FontPdf("7 0 R", CMapStream(useCMap, "/WMode 0 def"), CMapStream("", "/WMode 0 def")), "UA62");
        if (expected == null) { Assert.Empty(found); return; }
        Assert.Contains(expected, Assert.Single(found).Message);
    }

    /// <summary>
    /// An embedded CMap program's <c>usecmap</c>: a listed operand passes, an unlisted one is reported, and the
    /// words inside a comment or a string are not the operator.
    /// </summary>
    [Theory]
    [InlineData("/UniJIS-UCS2-H usecmap", null)]
    [InlineData("/UniJIS2004-UTF16-H usecmap", "uses the CMap /UniJIS2004-UTF16-H")]
    [InlineData("/Custom\nusecmap", "uses the CMap /Custom")]
    [InlineData("[/Custom] usecmap", "calls usecmap on an operand that is not a CMap name")]
    [InlineData("% /Custom usecmap\n/UniJIS-UCS2-H usecmap", null)]
    [InlineData("(/Custom usecmap) pop", null)]
    [InlineData("<2F437573746F6D> pop /Custom usecmapx", null)]
    public void UA62_AnEmbeddedCMapsProgram_UsesOnlyAPredefinedCMap(string program, string? expected)
    {
        var found = Coded(FontPdf("7 0 R", CMapStream("", program)), "UA62");
        if (expected == null) { Assert.Empty(found); return; }
        Assert.Contains(expected, Assert.Single(found).Message);
    }

    /// <summary>An embedded CMap's <c>/WMode</c> entry, 0 when absent, is the program's; a program that declares none is not compared.</summary>
    [Theory]
    [InlineData(" /WMode 1", "/WMode 1 def", false)]
    [InlineData("", "/WMode 0 def", false)]
    [InlineData("", "/WMode 1 def", true)]
    [InlineData(" /WMode 1", "/WMode 0 def", true)]
    [InlineData(" /WMode 1", "", false)]
    [InlineData("", "(/WMode 1) pop % /WMode 1\n/WMode 0 def", false)]
    public void UA62_AnEmbeddedCMapsWMode_IsItsProgramsWMode(string dictionaryEntry, string program, bool reported)
    {
        var found = Coded(FontPdf("7 0 R", CMapStream(dictionaryEntry, program)), "UA62");
        if (!reported) { Assert.Empty(found); return; }
        Assert.Contains("/WMode entry is", Assert.Single(found).Message);
    }

    /// <summary>A font in the resources that nothing paints is not judged; the same font painted is.</summary>
    [Fact]
    public void UA62_JudgesTheFontsUsedForRendering()
    {
        Assert.Empty(Coded(FontPdfDrawing("/MyOwnCMap", "q Q"), "UA62"));
        Assert.Single(Coded(FontPdf("/MyOwnCMap"), "UA62"));
    }

    /// <summary>
    /// A program past the CMap ceiling is not read, and says so; its dictionary's /UseCMap still is judged.
    /// Under the default ceiling the same program is read, and its usecmap reported.
    /// </summary>
    [Fact]
    public void UA62_AProgramPastTheCeiling_IsReportedAsNotJudged()
    {
        byte[] pdf = FontPdf("7 0 R", CMapStream("/UseCMap /Custom", "/Other usecmap"));
        Assert.Contains("uses the CMap /Other", Assert.Single(Coded(pdf, "UA62")).Message);

        try
        {
            Core.Constants.PdfLimits.MaxCMapStreamBytes = 4;
            var result = PdfUaChecker.Validate(new MemoryStream(pdf)).Violations;

            Assert.Contains(result, v => v.Code == "UA99" && v.Message.Contains("UA62 (ISO 14289-1 7.21.3.3) was not judged in full"));
            string reported = Assert.Single(result, v => v.Code == "UA62").Message;
            Assert.Contains("/UseCMap names /Custom", reported);
            Assert.DoesNotContain("/Other", reported);
        }
        finally
        {
            Core.Constants.PdfLimits.ResetToDefaults();
        }
    }

    // -------------------------------------------------------------- UA63, 7.21.3.1

    /// <summary>
    /// A predefined CMap's character collection, Adobe-Japan1-1 as its Adobe program declares, registered under
    /// a name no other test of this assembly decodes with: the registry is process-wide and keeps it.
    /// </summary>
    private const string RegisteredCMap = "83pv-RKSJ-H";

    private const string RegisteredProgram =
        "%!PS-Adobe-3.0 Resource-CMap\n/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "/CIDSystemInfo 3 dict dup begin\n  /Registry (Adobe) def\n  /Ordering (Japan1) def\n  /Supplement 1 def\nend def\n" +
        "/CMapName /83pv-RKSJ-H def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
        "1 begincidrange\n<20> <7E> 231\nendcidrange\nendcmap\n";

    /// <summary>
    /// A CIDFont against a predefined CMap's character collection, read from the program registered for it: the
    /// same registry and ordering at the CMap's supplement or above passes, anything else is reported.
    /// </summary>
    [Theory]
    [InlineData("<< /Registry (Adobe) /Ordering (Japan1) /Supplement 1 >>", null)]
    [InlineData("<< /Registry (Adobe) /Ordering (Japan1) /Supplement 7 >>", null)]
    [InlineData("<< /Registry (Adobe) /Ordering (Japan1) /Supplement 0 >>", "is Adobe-Japan1-0 and its CMap's Adobe-Japan1-1")]
    [InlineData("<< /Registry (Adobe) /Ordering (GB1) /Supplement 5 >>", "is Adobe-GB1-5 and its CMap's Adobe-Japan1-1")]
    [InlineData("<< /Registry (Other) /Ordering (Japan1) /Supplement 1 >>", "is Other-Japan1-1 and its CMap's Adobe-Japan1-1")]
    public void UA63_ACidFontMatchesItsPredefinedCMapsCollection(string cidSystemInfo, string? expected)
    {
        Core.Fonts.PredefinedCMapRegistry.Register(RegisteredCMap, Encoding.ASCII.GetBytes(RegisteredProgram));
        var found = Coded(FontPdfWith("/" + RegisteredCMap, "BT /F1 12 Tf <20> Tj ET", cidSystemInfo), "UA63");
        if (expected == null) { Assert.Empty(found); return; }
        Assert.Contains(expected, Assert.Single(found).Message);
    }

    /// <summary>An embedded CMap's collection is its stream dictionary's /CIDSystemInfo; Identity-H asks nothing.</summary>
    [Theory]
    [InlineData("7 0 R", "<< /Registry (Adobe) /Ordering (Japan1) /Supplement 4 >>", false)]
    [InlineData("7 0 R", "<< /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >>", true)]
    [InlineData("/Identity-H", "<< /Registry (Any) /Ordering (Thing) /Supplement 0 >>", false)]
    public void UA63_AnEmbeddedCMapsCollection_IsItsDictionarys(string encoding, string cidSystemInfo, bool reported)
    {
        string cmap = "<< /Type /CMap /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 3 >> /Length 0 >>\nstream\n\nendstream";
        byte[] pdf = FontPdfWith(encoding, "BT /F1 12 Tf <0001> Tj ET", cidSystemInfo, cmap);
        Assert.Equal(reported, Coded(pdf, "UA63").Any());
        Assert.DoesNotContain(Coded(pdf, "UA99"), v => v.Message.Contains("UA63"));
    }

    /// <summary>A registered program that declares no complete /CIDSystemInfo is not judged, and says so.</summary>
    [Fact]
    public void UA63_ARegisteredProgramWithNoCidSystemInfo_IsSaidNotJudged()
    {
        Core.Fonts.PredefinedCMapRegistry.Register("90pv-RKSJ-H", Encoding.ASCII.GetBytes("/CMapName /90pv-RKSJ-H def\n"));
        byte[] pdf = FontPdfWith("/90pv-RKSJ-H", "BT /F1 12 Tf <20> Tj ET", "<< /Registry (Adobe) /Ordering (Japan1) /Supplement 0 >>");
        Assert.Empty(Coded(pdf, "UA63"));
        Assert.Contains(Coded(pdf, "UA99"), v => v.Message.Contains("declares no complete /CIDSystemInfo"));
    }

    /// <summary>A predefined CMap no program is registered for is not judged, and says so; a registered one is judged.</summary>
    [Fact]
    public void UA63_APredefinedCMapWithNoRegisteredProgram_IsSaidNotJudged()
    {
        Assert.False(Core.Fonts.PredefinedCMapRegistry.IsRegistered("UniCNS-UTF16-V"));
        var result = PdfUaChecker.Validate(new MemoryStream(FontPdfWith("/UniCNS-UTF16-V", "BT /F1 12 Tf <0020> Tj ET",
            "<< /Registry (Adobe) /Ordering (Japan1) /Supplement 0 >>"))).Violations;
        Assert.DoesNotContain(result, v => v.Code == "UA63");
        Assert.Contains(result, v => v.Code == "UA99" && v.Message.Contains("UA63 (ISO 14289-1 7.21.3.1) was not judged")
                                     && v.Message.Contains("no program is registered under that name"));
    }

    /// <summary>The program's /CIDSystemInfo, in either form a CMap writes it; a name outside it, or in a comment, is not it.</summary>
    [Theory]
    [InlineData("/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Korea1) def /Supplement 2 def end def", "Adobe", "Korea1", 2)]
    [InlineData("/CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 5 >> def", "Adobe", "GB1", 5)]
    [InlineData("/Registry (Wrong) def /CIDSystemInfo << /Registry (Adobe) /Ordering (CNS1) /Supplement 0 >> def", "Adobe", "CNS1", 0)]
    [InlineData("% /CIDSystemInfo << /Registry (X) >>\n/Ordering (Y) def", null, null, null)]
    public void ReadProgram_FindsTheCidSystemInfo(string program, string? registry, string? ordering, int? supplement)
    {
        var read = CMapRules.ReadProgram(Encoding.ASCII.GetBytes(program));
        Assert.Equal(registry, read.Registry);
        Assert.Equal(ordering, read.Ordering);
        Assert.Equal(supplement, read.Supplement);
    }

    // -------------------------------------------------------------- the program reader

    /// <summary>The first <c>/WMode</c> name followed by an integer is the declaration; comments, strings and dictionaries are stepped over.</summary>
    [Theory]
    [InlineData("/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) >> def /WMode 1 def", 1)]
    [InlineData("(/WMode 0) pop /WMode 1 def", 1)]
    [InlineData("% /WMode 0\n/WMode 1 def", 1)]
    [InlineData("<2F574D6F6465> pop /WMode 0 def", 0)]
    [InlineData("/WMode /Foo def /WMode 1 def", 1)]
    [InlineData("/WMode 0 def /WMode 1 def", 0)]
    [InlineData("/WMode", null)]
    [InlineData("", null)]
    public void ReadProgram_FindsTheWModeDeclaration(string program, int? expected)
        => Assert.Equal(expected, CMapRules.ReadProgram(Encoding.ASCII.GetBytes(program)).WMode);

    // -------------------------------------------------------------- fixture

    /// <summary>
    /// 1 catalog, 2 pages, 3 page drawing <paramref name="content"/> with /F1, 4 the Type 0 font, 5 the content
    /// stream, 6 the descendant CIDFont, then <paramref name="extra"/> from 7.
    /// </summary>
    private static byte[] FontPdf(string encoding, params string[] extra) => FontPdfDrawing(encoding, "BT /F1 12 Tf <0001> Tj ET", extra);

    private static byte[] FontPdfDrawing(string encoding, string content, params string[] extra)
        => FontPdfWith(encoding, content, "<< /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >>", extra);

    /// <summary>As <see cref="FontPdfDrawing"/>, the CIDFont's /CIDSystemInfo <paramref name="cidSystemInfo"/>.</summary>
    private static byte[] FontPdfWith(string encoding, string content, string cidSystemInfo, params string[] extra)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            $"<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding {encoding} /DescendantFonts [6 0 R] >>",
            Stream("", content),
            $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Foo /CIDSystemInfo {cidSystemInfo} >>",
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

    private static string CMapStream(string dictionaryEntries, string program) => Stream(" /Type /CMap " + dictionaryEntries, program);

    private static string Stream(string dictionaryEntries, string text)
        => "<<" + dictionaryEntries + " /Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "\nendstream";

    private static List<PdfUaViolation> Coded(byte[] pdf, string code)
        => PdfUaChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == code).ToList();
}
