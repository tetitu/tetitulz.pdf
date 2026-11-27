// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// ISO 14289-2 §8.4.5.4's first and last sentences, beside its <c>/WMode</c> one
/// (<see cref="PdfUa2StructureCheckerTests"/>): a CMap ISO 32000-2 Table 116 does not list is embedded,
/// and a CMap references none but a listed one — read through the rule PDF/UA-1's UA62 shares.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class Ua2CMapTests
{
    /// <summary>A Type 0 font in a page's resources naming a CMap: listed passes, unlisted is reported.</summary>
    [Theory]
    [InlineData("/UniJIS-UTF16-H", false)]
    [InlineData("/UniKS-UTF16-V", false)]
    [InlineData("/UniJIS2004-UTF16-H", true)]
    public void ANamedCMap_IsListedOrReported(string encoding, bool reported)
    {
        var found = Coded(FontPdf(encoding), "UA2-8.4.5.4");
        if (!reported) { Assert.Empty(found); return; }
        Assert.Contains($"names the CMap {encoding}, which is not a predefined CMap", Assert.Single(found).Message);
    }

    /// <summary>An embedded CMap's /UseCMap and its program's usecmap name only listed CMaps.</summary>
    [Theory]
    [InlineData("/UseCMap /UniJIS-UCS2-H", "/UniJIS-UCS2-H usecmap", null)]
    [InlineData("/UseCMap /Custom", "", "/UseCMap names /Custom")]
    [InlineData("", "/Custom usecmap", "uses the CMap /Custom")]
    public void AnEmbeddedCMap_ReferencesOnlyListedCMaps(string dictionaryEntries, string program, string? expected)
    {
        var found = Coded(FontPdf("6 0 R", CMapStream(dictionaryEntries, program)), "UA2-8.4.5.4");
        if (expected == null) { Assert.Empty(found); return; }
        Assert.Contains(expected, Assert.Single(found).Message);
    }

    /// <summary>A program past the CMap ceiling is not read, and a UA2-99 says so; under the default it is read.</summary>
    [Fact]
    public void AProgramPastTheCeiling_IsSaidNotJudged()
    {
        byte[] pdf = FontPdf("6 0 R", CMapStream("", "/Custom usecmap"));
        Assert.Single(Coded(pdf, "UA2-8.4.5.4"));
        Assert.DoesNotContain(Coded(pdf, "UA2-99"), v => v.Message.Contains("§8.4.5.4 was not judged"));

        try
        {
            PdfLimits.MaxCMapStreamBytes = 4;
            Assert.Empty(Coded(pdf, "UA2-8.4.5.4"));
            Assert.Contains(Coded(pdf, "UA2-99"), v => v.Message.Contains("§8.4.5.4 was not judged in full for the font Foo"));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// §8.4.5.3.1 for a predefined CMap: its character collection, Adobe-Japan1-1 as the Adobe program declares, is
    /// read from the program registered under its name — a name no other test of this assembly decodes with, since
    /// the registry is process-wide — and a CIDFont below it is reported; at it or above, not.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public void APredefinedCMapsCollection_IsReadFromItsRegisteredProgram(int supplement, bool reported)
    {
        Core.Fonts.PredefinedCMapRegistry.Register("83pv-RKSJ-H", System.Text.Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin\nbegincmap\n/CIDSystemInfo 3 dict dup begin\n  /Registry (Adobe) def\n" +
            "  /Ordering (Japan1) def\n  /Supplement 1 def\nend def\n/CMapName /83pv-RKSJ-H def\nendcmap\n"));
        byte[] pdf = FontPdfWith("/83pv-RKSJ-H",
            $"<< /Registry (Adobe) /Ordering (Japan1) /Supplement {supplement.ToString(CultureInfo.InvariantCulture)} >>");
        var found = Coded(pdf, "UA2-8.4.5.3.1");
        Assert.Equal(reported, found.Any());
        if (reported) Assert.Contains("is Adobe-Japan1-0 and its CMap's Adobe-Japan1-1", found[0].Message);
    }

    /// <summary>A predefined CMap no program is registered for is not judged, and a UA2-99 says so.</summary>
    [Fact]
    public void APredefinedCMapWithNoRegisteredProgram_IsSaidNotJudged()
    {
        Assert.False(Core.Fonts.PredefinedCMapRegistry.IsRegistered("UniCNS-UTF16-V"));
        byte[] pdf = FontPdfWith("/UniCNS-UTF16-V", "<< /Registry (Adobe) /Ordering (Japan1) /Supplement 0 >>");
        Assert.Empty(Coded(pdf, "UA2-8.4.5.3.1"));
        Assert.Contains(Coded(pdf, "UA2-99"), v => v.Message.StartsWith("§8.4.5.3.1 was not judged for a font: Foo", System.StringComparison.Ordinal));
    }

    /// <summary>1 catalog, 2 pages, 3 page with /F1, 4 the Type 0 font, 5 its CIDFont, then <paramref name="extra"/> from 6.</summary>
    private static byte[] FontPdf(string encoding, params string[] extra)
        => FontPdfWith(encoding, "<< /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >>", extra);

    /// <summary>As <see cref="FontPdf"/>, the CIDFont's /CIDSystemInfo <paramref name="cidSystemInfo"/>.</summary>
    private static byte[] FontPdfWith(string encoding, string cidSystemInfo, params string[] extra)
    {
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            $"<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding {encoding} /DescendantFonts [5 0 R] >>",
            $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Foo /CIDSystemInfo {cidSystemInfo} >>",
        };
        bodies.AddRange(extra);
        return PdfUa2StructureCheckerTests.BuildPdf(bodies);
    }

    private static string CMapStream(string dictionaryEntries, string program)
        => $"<< /Type /CMap {dictionaryEntries} /Length {program.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n{program}\nendstream";

    private static System.Collections.Generic.List<PdfUa2StructureViolation> Coded(byte[] pdf, string code)
        => PdfUa2StructureChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == code).ToList();
}
