// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// "Embedded fonts shall define all glyphs referenced for rendering" — PDF/A's A84 (ISO 19005-1 6.3.5,
/// 19005-2/3 6.2.11.4.1, 19005-4 6.2.10.4.1) and PDF/UA-1's UA57 (ISO 14289-1 7.21.4.1), one reading
/// (<c>FontGlyphPresence</c> over <c>Core.Fonts.EmbeddedGlyphSets.GlyphPresence</c>), over the codes each
/// font paints. The published profile's isGlyphPresent rules.
/// </summary>
public class FontGlyphPresenceTests
{
    private static List<PdfAViolation> A84(byte[] pdf, PdfAConformancePart part = PdfAConformancePart.Part2)
        => PdfAValidator.Validate(new MemoryStream(pdf), part).Violations.Where(v => v.Code == "A84").ToList();

    private static void AssertA84(byte[] pdf, string? problem)
    {
        var a84 = A84(pdf);
        if (problem == null) Assert.Empty(a84);
        else Assert.Contains(problem, Assert.Single(a84).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AB", null)]
    [InlineData("ABC", "code(s) 67 painted with no glyph in the embedded program")]
    public void AType1CffFontsPaintedCodes_NameGlyphsItsProgramDefines(string shown, string? problem)
        => AssertA84(SimpleFontPdf("<< /Type /Font /Subtype /Type1 /BaseFont /Box /FirstChar 65 /LastChar 67 /Widths [500 500 500] "
                + "/Encoding << /Differences [65 /A /B /C] >> /FontDescriptor 6 0 R >>",
            Descriptor("/FontFile3 7 0 R"), StreamObj(MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("AB")), " /Subtype /Type1C"),
            Shown(shown)), problem);

    [Theory]
    [InlineData("A", null)]
    [InlineData("AB", "code(s) 66 painted with no glyph in the embedded program")]
    public void AType3FontsPaintedCodes_NameItsGlyphProcedures(string shown, string? problem)
        => AssertA84(SimpleFontPdf("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] "
                + "/CharProcs << /A 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /A /B] >> /FirstChar 65 /LastChar 66 /Widths [500 500] >>",
            StreamObj(Encoding.ASCII.GetBytes("500 0 d0 0 0 10 10 re f"), ""), null, Shown(shown)), problem);

    /// <summary>A non-symbolic TrueType font: the code's WinAnsi name to Unicode, through its (3,1) cmap.</summary>
    [Theory]
    [InlineData("AB", null)]
    [InlineData("ABC", "code(s) 67 painted with no glyph in the embedded program")]
    public void ANonSymbolicTrueTypeFontsCodes_MapThroughTheirNamesToUnicode(string shown, string? problem)
        => AssertA84(SimpleFontPdf("<< /Type /Font /Subtype /TrueType /BaseFont /Box /FirstChar 65 /LastChar 67 /Widths [500 500 500] "
                + "/Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>",
            Descriptor("/FontFile2 7 0 R", flags: 32), StreamObj(MinimalTrueType.Build("AB"), ""), Shown(shown)), problem);

    /// <summary>
    /// A subset can keep a glyph's slot and drop its outline (the published profile's 6-2-11-4-1-t02-fail-c, Cambria's
    /// 'a'): that glyph is not defined. A space's glyph is empty by nature and stays defined.
    /// </summary>
    [Theory]
    [InlineData("A ", null)]
    [InlineData("AB", "code(s) 66 painted with no glyph in the embedded program")]
    public void ATrueTypeGlyphKeptButEmptied_IsNotDefinedUnlessItIsWhitespace(string shown, string? problem)
    {
        // Build numbers glyphs in code point order: 1 is ' ', 2 'A', 3 'B'; empty ' ' and 'B'.
        byte[] font = WithEmptyGlyphs(MinimalTrueType.Build("AB "), 1, 3);
        AssertA84(SimpleFontPdf("<< /Type /Font /Subtype /TrueType /BaseFont /Box /FirstChar 32 /LastChar 66 /Widths ["
                + string.Join(" ", Enumerable.Repeat("500", 35)) + "] /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>",
            Descriptor("/FontFile2 7 0 R", flags: 32), StreamObj(font, ""), Shown(shown)), problem);
    }

    /// <summary>Sets each named glyph's <c>loca</c> span to nothing (short format: the next entry equals its own).</summary>
    private static byte[] WithEmptyGlyphs(byte[] font, params int[] glyphs)
    {
        font = (byte[])font.Clone();
        int tables = (font[4] << 8) | font[5];
        int loca = -1;
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + 16 * i;
            if (Encoding.ASCII.GetString(font, record, 4) == "loca")
                loca = (font[record + 8] << 24) | (font[record + 9] << 16) | (font[record + 10] << 8) | font[record + 11];
        }
        Assert.True(loca > 0);
        foreach (int g in glyphs.OrderBy(g => g))
        {
            // g's end moves back to its start; the next glyph then spans more, and loca stays
            // non-decreasing. In ascending order, so an earlier glyph's freed span is not handed back.
            int start = (font[loca + 2 * g] << 8) | font[loca + 2 * g + 1];
            font[loca + 2 * (g + 1)] = (byte)(start >> 8);
            font[loca + 2 * (g + 1) + 1] = (byte)start;
        }
        return font;
    }

    /// <summary>
    /// A symbolic TrueType font: the code in the symbol area (a (3,0) cmap at 0xF000, <c>BuildSymbolic</c>)
    /// or the code itself (a (3,0) cmap at the codes).
    /// </summary>
    [Theory]
    [InlineData(true, "AB", null)]
    [InlineData(true, "AC", "code(s) 67 painted with no glyph in the embedded program")]
    [InlineData(false, "AB", null)]
    [InlineData(false, "AC", "code(s) 67 painted with no glyph in the embedded program")]
    public void ASymbolicTrueTypeFontsCodes_MapDirectlyOrInTheSymbolArea(bool symbolArea, string shown, string? problem)
        => AssertA84(SimpleFontPdf("<< /Type /Font /Subtype /TrueType /BaseFont /Box /FirstChar 65 /LastChar 67 /Widths [500 500 500] "
                + "/FontDescriptor 6 0 R >>",
            Descriptor("/FontFile2 7 0 R", flags: 4),
            StreamObj(symbolArea ? MinimalTrueType.BuildSymbolic("AB") : MinimalTrueType.Build("AB", 3, 0), ""), Shown(shown)), problem);

    [Theory]
    [InlineData("<00030007>", null)]
    [InlineData("<00030005>", "CID(s) 5 painted with no glyph in the embedded program")]
    public void ACidKeyedCffFontsPaintedCids_AreInItsCharset(string shown, string? problem)
        => AssertA84(Type0Pdf("/CIDFontType0", "/FontFile3", MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 }), " /Subtype /CIDFontType0C",
            "/Identity-H", shown), problem);

    /// <summary>A CID above 255: both bytes of the code are the CID (Identity-H, high byte first).</summary>
    [Theory]
    [InlineData("<0003012C>", null)]
    [InlineData("<0003012D>", "CID(s) 301 painted with no glyph in the embedded program")]
    public void ACidAbove255_IsReadFromBothBytes(string shown, string? problem)
        => AssertA84(Type0Pdf("/CIDFontType0", "/FontFile3", MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 300 }), " /Subtype /CIDFontType0C",
            "/Identity-H", shown), problem);

    /// <summary><see cref="MinimalTrueType.Build(string)"/> holds three glyphs: CID 3 is past them under an Identity map.</summary>
    [Theory]
    [InlineData("<00010002>", null)]
    [InlineData("<00010003>", "CID(s) 3 painted with no glyph in the embedded program")]
    public void ATrueTypeCidFontsPaintedCids_HaveAGlyph(string shown, string? problem)
        => AssertA84(Type0Pdf("/CIDFontType2", "/FontFile2", MinimalTrueType.Build("AB"), "", "/Identity-H", shown), problem);

    /// <summary>
    /// A composite font's strings are decoded through its own CMap. A predefined CMap no one registered
    /// (this test project loads no CMap plugin) cannot be honoured, and the codec would split the bytes
    /// as Identity: not judged, as the published profile's null passes, and said in LastValidationError, not as an A99.
    /// </summary>
    [Fact]
    public void AnUnregisteredPredefinedCMap_IsNotJudgedAndSaidSo()
    {
        var result = PdfAValidator.Validate(new MemoryStream(Type0Pdf("/CIDFontType0", "/FontFile3",
            MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 }), " /Subtype /CIDFontType0C", "/UniGB-UCS2-H", "<00030005>")), PdfAConformancePart.Part2);
        Assert.DoesNotContain(result.Violations, v => v.Code == "A84");
        Assert.DoesNotContain(result.Informational, v => v.Message.Contains("A84", StringComparison.Ordinal));
        Assert.Contains("A84: ", PdfAValidator.LastValidationError, StringComparison.Ordinal);
        Assert.Contains("its /Encoding CMap is not registered or does not read", PdfAValidator.LastValidationError, StringComparison.Ordinal);
    }

    /// <summary>
    /// An embedded CMap stream is the font's own encoding: one-byte codes 'A' and 'B' here map to CIDs 3
    /// and 4, and the program holds CID 3 but not 4.
    /// </summary>
    [Theory]
    [InlineData("<00FF>", "(AA)", null)]
    [InlineData("<00FF>", "(AB)", "CID(s) 4 painted with no glyph in the embedded program")]
    [InlineData("<4142>", "(AZ)", null)]   // 'Z' is in no codespace range: not a code, so no CID to judge
    public void AnEmbeddedCMap_DecodesTheStringsToCids(string codespace, string shown, string? problem)
    {
        string text = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap " +
                      "1 begincodespacerange <" + codespace.Substring(1, 2) + "> <" + codespace.Substring(3, 2) + "> endcodespacerange " +
                      "1 begincidrange <41> <42> 3 endcidrange endcmap end end";
        string cmap = "<< /Type /CMap /CMapName /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                      "/Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "\nendstream";
        byte[] cff = MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 });
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Box /Encoding 9 0 R /DescendantFonts [8 0 R] >>",
            StreamObj(Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 10 Td " + shown + " Tj ET"), ""),
            Descriptor("/FontFile3 7 0 R"),
            StreamObj(cff, " /Subtype /CIDFontType0C"),
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Box /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 6 0 R >>",
            cmap);
        AssertA84(pdf, problem);
    }

    /// <summary>Text in render mode 3 is not rendered, so it references no glyph (the rules' own exemption).</summary>
    [Fact]
    public void InvisibleText_ReferencesNoGlyph()
    {
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Box /FirstChar 65 /LastChar 67 /Widths [500 500 500] "
            + "/Encoding << /Differences [65 /A /B /C] >> /FontDescriptor 6 0 R >>";
        byte[] Pdf(string content) => SimpleFontPdf(font, Descriptor("/FontFile3 7 0 R"),
            StreamObj(MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("AB")), " /Subtype /Type1C"), content);
        Assert.Empty(A84(Pdf("BT /F1 12 Tf 3 Tr 10 10 Td (C) Tj ET BT /F1 12 Tf 0 Tr 10 30 Td (AB) Tj ET")));
        Assert.Single(A84(Pdf("BT /F1 12 Tf 0 Tr 10 10 Td (C) Tj ET")));
    }

    /// <summary>
    /// The font walk takes the painted fonts from the painted codes it read: a font in the resources that
    /// nothing paints is not judged by the A80–A84 rules (here A83's PDF/A-1 CharSet requirement), while
    /// the same font painted is.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AFontNothingPaints_IsNotJudged(bool paintSecond, bool reported)
    {
        string content = "BT /F1 12 Tf 10 10 Td (AB) Tj ET" + (paintSecond ? " BT /F2 12 Tf 10 30 Td (A) Tj ET" : "");
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("AB"));
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R /F2 8 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+Box /FirstChar 65 /LastChar 66 /Widths [500 500] "
                + "/Encoding << /Differences [65 /A /B] >> /FontDescriptor 6 0 R >>",
            StreamObj(Encoding.ASCII.GetBytes(content), ""),
            "<< /Type /FontDescriptor /FontName /ABCDEF+Box /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 "
                + "/Descent -200 /CapHeight 700 /StemV 80 /CharSet (/A/B) /FontFile3 7 0 R >>",
            StreamObj(cff, " /Subtype /Type1C"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /GHIJKL+Box /FirstChar 65 /LastChar 66 /Widths [500 500] "
                + "/Encoding << /Differences [65 /A /B] >> /FontDescriptor 9 0 R >>",
            "<< /Type /FontDescriptor /FontName /GHIJKL+Box /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 "
                + "/Descent -200 /CapHeight 700 /StemV 80 /FontFile3 7 0 R >>");
        var a83 = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part1).Violations.Where(v => v.Code == "A83").ToList();
        Assert.Equal(reported, a83.Any(v => v.Message.Contains("GHIJKL+Box: a Type 1 font subset with no /CharSet", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(PdfAConformancePart.Part1, "ISO 19005-1 6.3.5; rule 6.3.5-1")]
    [InlineData(PdfAConformancePart.Part3, "ISO 19005-3 6.2.11.4.1; rule 6.2.11.4.1-2")]
    [InlineData(PdfAConformancePart.Part4, "ISO 19005-4 6.2.10.4.1; rule 6.2.10.4.1-2")]
    public void A84_CitesEachPartsClause(PdfAConformancePart part, string clause)
    {
        byte[] pdf = Type0Pdf("/CIDFontType0", "/FontFile3", MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 }), " /Subtype /CIDFontType0C",
            "/Identity-H", "<0005>");
        Assert.Contains(clause, Assert.Single(A84(pdf, part)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<00030007>", false)]
    [InlineData("<00030005>", true)]
    public void Ua57_AGlyphPaintedButNotDefined(string shown, bool reported)
    {
        byte[] pdf = Type0Pdf("/CIDFontType0", "/FontFile3", MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 }), " /Subtype /CIDFontType0C",
            "/Identity-H", shown);
        var ua57 = PdfUaChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == "UA57").ToList();
        Assert.Equal(reported, ua57.Count == 1);
        if (reported) Assert.Contains("CID(s) 5 painted with no glyph", ua57[0].Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private static string Shown(string text) => "BT /F1 12 Tf 10 10 Td (" + text + ") Tj ET";

    private static string Descriptor(string fontFile, int flags = 4)
        => "<< /Type /FontDescriptor /FontName /Box /Flags " + flags.ToString(CultureInfo.InvariantCulture)
           + " /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 " + fontFile + " >>";

    /// <summary>Objects: 4 the font, 5 the page content, 6 and 7 what the font names.</summary>
    private static byte[] SimpleFontPdf(string font, string object6, string? object7, string content)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            font,
            StreamObj(Encoding.ASCII.GetBytes(content), ""),
            object6,
        };
        if (object7 != null) bodies.Add(object7);
        return Build(bodies.ToArray());
    }

    private static byte[] Type0Pdf(string cidFontType, string fontFileKey, byte[] program, string programEntries, string cmap, string shown)
        => Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Box /Encoding " + cmap + " /DescendantFonts [8 0 R] >>",
            StreamObj(Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 10 Td " + shown + " Tj ET"), ""),
            Descriptor(fontFileKey + " 7 0 R"),
            StreamObj(program, programEntries),
            "<< /Type /Font /Subtype " + cidFontType + " /BaseFont /Box /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 6 0 R >>");

    private static string StreamObj(byte[] data, string entries)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + entries +
           " >>\nstream\n" + Encoding.Latin1.GetString(data) + "\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
