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
/// A subset's <c>/CharSet</c> and <c>/CIDSet</c> against its embedded program — PDF/UA-1's UA56 (ISO
/// 14289-1 7.21.4.2) and PDF/A's A83 (ISO 19005-1 6.3.5, 19005-2/3 6.2.11.4.2), one reading
/// (<c>FontSubsetSets</c> over <c>Core.Fonts.EmbeddedGlyphSets</c>). The published profile's 7.21.4.2, 6-3-5 and
/// 6-2-11-4-2 files.
/// </summary>
public class FontSubsetSetsTests
{
    private const string Subset = "ABCDEF+Box";

    // ---- UA56: the CharSet lists exactly the program's glyphs ----

    [Theory]
    [InlineData("/A/B", null)]
    [InlineData("/A/B/.notdef", null)]
    [InlineData("/A /B", null)]
    [InlineData("/A", "its /CharSet omits glyphs the program defines (/B)")]
    [InlineData("/A/B/C", "its /CharSet lists glyphs the program does not define (/C)")]
    public void Ua56_ACffSubsetsCharSet_ListsExactlyItsGlyphs(string charSet, string? problem)
    {
        var ua56 = Ua(Type1Pdf(Subset, CffAB(), charSet)).Where(v => v.Code == "UA56").ToList();
        if (problem == null) Assert.Empty(ua56);
        else
        {
            string message = Assert.Single(ua56).Message;
            Assert.Contains(Subset + ": " + problem, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Ua56_AType1ProgramSubsetsCharSet_IsComparedWithItsCharStrings()
    {
        var (program, length1, length2) = MinimalType1.Build(new[]
        {
            (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(500)), ("B", MinimalType1.AdvanceOnly(500)),
        });
        string entries = " /Length1 " + length1.ToString(CultureInfo.InvariantCulture) + " /Length2 " + length2.ToString(CultureInfo.InvariantCulture);
        Assert.Empty(Ua(Type1Pdf(Subset, program, "/A/B", "/FontFile", entries)).Where(v => v.Code == "UA56"));
        Assert.Contains(Ua(Type1Pdf(Subset, program, "/B", "/FontFile", entries)),
            v => v.Code == "UA56" && v.Message.Contains("omits glyphs the program defines (/A)", StringComparison.Ordinal));
    }

    /// <summary>A font with no subset tag, or a tag that is not six capitals and a plus, is not judged.</summary>
    [Theory]
    [InlineData("Box", false)]
    [InlineData("ABCDEf+Box", false)]
    [InlineData("ABCDE+Box", false)]
    [InlineData("ABCDEFG+Box", false)]
    [InlineData("ABCDEF+Box", true)]
    public void Ua56_OnlyATaggedSubsetIsJudged(string baseFont, bool judged)
        => Assert.Equal(judged, Ua(Type1Pdf(baseFont, CffAB(), "/A")).Any(v => v.Code == "UA56"));

    // ---- UA56: the CIDSet identifies every CID the program holds ----

    [Theory]
    [InlineData(new[] { 3, 7 }, null)]
    [InlineData(new[] { 0, 3, 7, 9 }, null)]
    [InlineData(new[] { 3 }, "its /CIDSet does not identify CIDs the program holds (7)")]
    [InlineData(new int[0], "its /CIDSet does not identify CIDs the program holds (3, 7)")]
    public void Ua56_ACidKeyedCffSubsetsCidSet_IdentifiesEveryCid(int[] cidSet, string? problem)
    {
        byte[] cff = MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 });
        AssertUa56(Type0Pdf(Subset, cff, "/FontFile3", "/CIDFontType0", cidSet), problem);
    }

    /// <summary>
    /// A TrueType CIDFont holds the CIDs whose glyph has outline data, through <c>/CIDToGIDMap</c>:
    /// <see cref="MinimalTrueType.Build(string)"/> gives glyphs 1 and 2 outlines and <c>.notdef</c> none.
    /// </summary>
    [Theory]
    [InlineData(new[] { 1, 2 }, null, null)]
    [InlineData(new[] { 1 }, null, "its /CIDSet does not identify CIDs the program holds (2)")]
    [InlineData(new[] { 5, 6 }, new[] { 0, 0, 0, 0, 0, 2, 1 }, null)]
    [InlineData(new[] { 1, 2 }, new[] { 0, 0, 0, 0, 0, 2, 1 }, "its /CIDSet does not identify CIDs the program holds (5, 6)")]
    public void Ua56_ATrueTypeSubsetsCidSet_IdentifiesTheCidsWithOutlines(int[] cidSet, int[]? cidToGid, string? problem)
    {
        byte[] font = MinimalTrueType.Build("AB");
        AssertUa56(Type0Pdf(Subset, font, "/FontFile2", "/CIDFontType2", cidSet, cidToGid), problem);
    }

    /// <summary>A program that cannot be read is said to be unjudged (UA99), never passed or failed.</summary>
    [Fact]
    public void Ua56_AnUnreadableProgram_IsReportedUnjudged()
    {
        var violations = Ua(Type1Pdf(Subset, Encoding.ASCII.GetBytes("not a CFF font"), "/A"));
        Assert.DoesNotContain(violations, v => v.Code == "UA56");
        Assert.Contains(violations, v => v.Code == "UA99"
            && v.Message.Contains("UA56", StringComparison.Ordinal) && v.Message.Contains(Subset, StringComparison.Ordinal));
        Assert.DoesNotContain(Ua(Type1Pdf(Subset, CffAB(), "/A/B")), v => v.Code == "UA99" && v.Message.Contains("UA56", StringComparison.Ordinal));
    }

    // ---- A83: PDF/A-1 requires the sets; PDF/A-2 and -3 judge them where present; PDF/A-4 has no rule ----

    [Theory]
    [InlineData(PdfAConformancePart.Part1, null, "a Type 1 font subset with no /CharSet")]
    [InlineData(PdfAConformancePart.Part2, null, null)]
    [InlineData(PdfAConformancePart.Part1, "/A", "its /CharSet omits glyphs used for rendering (/B)")]
    [InlineData(PdfAConformancePart.Part1, "/A/B/C", null)]
    [InlineData(PdfAConformancePart.Part2, "/A", "its /CharSet omits glyphs the program defines (/B)")]
    [InlineData(PdfAConformancePart.Part3, "/A/B/C", "its /CharSet lists glyphs the program does not define (/C)")]
    [InlineData(PdfAConformancePart.Part4, "/A", null)]
    [InlineData(PdfAConformancePart.Part2, "/A/B", null)]
    public void A83_ACharSetUnderEachPart(PdfAConformancePart part, string? charSet, string? problem)
    {
        var a83 = PdfAValidator.Validate(new MemoryStream(Type1Pdf(Subset, CffAB(), charSet)), part)
            .Violations.Where(v => v.Code == "A83").ToList();
        if (problem == null) Assert.Empty(a83);
        else
        {
            string message = Assert.Single(a83).Message;
            Assert.Contains(Subset + ": " + problem, message, StringComparison.Ordinal);
            Assert.Contains(part == PdfAConformancePart.Part1 ? "ISO 19005-1 6.3.5" : "6.2.11.4.2", message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(PdfAConformancePart.Part1, null, "a CIDFont subset with no /CIDSet")]
    [InlineData(PdfAConformancePart.Part2, null, null)]
    [InlineData(PdfAConformancePart.Part1, new[] { 3 }, "its /CIDSet does not identify CIDs the program holds (7)")]
    [InlineData(PdfAConformancePart.Part2, new[] { 3 }, "its /CIDSet does not identify CIDs the program holds (7)")]
    [InlineData(PdfAConformancePart.Part1, new[] { 3, 7 }, null)]
    [InlineData(PdfAConformancePart.Part4, new[] { 3 }, null)]
    public void A83_ACidSetUnderEachPart(PdfAConformancePart part, int[]? cidSet, string? problem)
    {
        byte[] cff = MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 });
        var a83 = PdfAValidator.Validate(new MemoryStream(Type0Pdf(Subset, cff, "/FontFile3", "/CIDFontType0", cidSet)), part)
            .Violations.Where(v => v.Code == "A83").ToList();
        if (problem == null) Assert.Empty(a83);
        else Assert.Contains(Subset + ": " + problem, Assert.Single(a83).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// PDF/A-1 asks for the CharSet of any Type 1 subset, embedded or not (the published profile's 6.3.5-2); PDF/A-2
    /// compares a CharSet with a program, and an unembedded subset has none to compare.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part1, null, true)]
    [InlineData(PdfAConformancePart.Part2, "/A", false)]
    public void A83_AnUnembeddedSubset(PdfAConformancePart part, string? charSet, bool reported)
    {
        string pdf = Encoding.Latin1.GetString(Type1Pdf(Subset, CffAB(), charSet));
        byte[] unembedded = Rebuild(pdf.Replace(" /FontFile3 7 0 R", ""));
        Assert.Equal(reported, PdfAValidator.Validate(new MemoryStream(unembedded), part).Violations
            .Any(v => v.Code == "A83" && v.Message.Contains(Subset, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A program this library cannot read is not judged — the published profile's null passes — and is said in
    /// <see cref="PdfAValidator.LastValidationError"/>: not as an A99, which would refuse the converter
    /// a file the published profiles accepts.
    /// </summary>
    [Fact]
    public void A83_AnUnreadableProgram_IsNotJudgedAndSaidSo()
    {
        var result = PdfAValidator.Validate(new MemoryStream(Type1Pdf(Subset, Encoding.ASCII.GetBytes("not a CFF font"), "/A")),
            PdfAConformancePart.Part2);
        Assert.DoesNotContain(result.Violations, v => v.Code == "A83");
        Assert.Empty(result.Informational);
        Assert.Contains("A83: " + Subset + ": its /CharSet was not compared", PdfAValidator.LastValidationError, StringComparison.Ordinal);
        PdfAValidator.Validate(new MemoryStream(Type1Pdf(Subset, CffAB(), "/A/B")), PdfAConformancePart.Part2);
        Assert.DoesNotContain("A83", PdfAValidator.LastValidationError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// PDF/A-1 reads a CharSet against the glyphs used for rendering (the published profile's 6-3-5-t02): a defined
    /// glyph nothing paints may go unlisted, and a painted code is named through the dictionary's
    /// encoding or, where it leaves the code to the implicit base, the embedded program's own — a CFF's
    /// encoding and a Type 1 program's <c>/Encoding</c> alike.
    /// </summary>
    [Theory]
    [InlineData("A", "/A", true, null)]
    [InlineData("AB", "/A", false, "its /CharSet omits glyphs used for rendering (/B)")]
    [InlineData("A", "/A", false, null)]
    public void A83_PdfA1_ACharSetListsTheGlyphsUsedForRendering(string shown, string charSet, bool withEncoding, string? problem)
    {
        var a83 = PdfAValidator.Validate(new MemoryStream(Type1Pdf(Subset, CffAB(), charSet, shown: shown, withEncoding: withEncoding)),
            PdfAConformancePart.Part1).Violations.Where(v => v.Code == "A83").ToList();
        if (problem == null) Assert.Empty(a83);
        else Assert.Contains(Subset + ": " + problem, Assert.Single(a83).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A83_PdfA1_AType1ProgramsOwnEncodingNamesAPaintedCode()
    {
        var (program, length1, length2) = MinimalType1.Build(
            new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(500)), ("B", MinimalType1.AdvanceOnly(500)) },
            encoding: new Dictionary<int, string> { [65] = "A", [66] = "B" });
        string entries = " /Length1 " + length1.ToString(CultureInfo.InvariantCulture) + " /Length2 " + length2.ToString(CultureInfo.InvariantCulture);
        var a83 = PdfAValidator.Validate(new MemoryStream(Type1Pdf(Subset, program, "/A", "/FontFile", entries, withEncoding: false)),
            PdfAConformancePart.Part1).Violations.Where(v => v.Code == "A83").ToList();
        Assert.Contains(Subset + ": its /CharSet omits glyphs used for rendering (/B)", Assert.Single(a83).Message, StringComparison.Ordinal);
    }

    /// <summary>A <c>TJ</c> array's strings are painted codes too, the kerning numbers between them not.</summary>
    [Fact]
    public void A83_PdfA1_ATJArraysStringsArePainted()
    {
        string pdf = Encoding.Latin1.GetString(Type1Pdf(Subset, CffAB(), "/A"));
        string original = "BT /F1 12 Tf 10 10 Td (AB) Tj ET";
        string array = "BT /F1 12 Tf 10 10 Td [(A) 120 (B)] TJ ET";
        string stream = " >>\nstream\n";
        byte[] edited = Rebuild(pdf.Replace("/Length " + original.Length.ToString(CultureInfo.InvariantCulture) + stream + original,
            "/Length " + array.Length.ToString(CultureInfo.InvariantCulture) + stream + array));
        var a83 = PdfAValidator.Validate(new MemoryStream(edited), PdfAConformancePart.Part1).Violations.Where(v => v.Code == "A83").ToList();
        Assert.Contains(Subset + ": its /CharSet omits glyphs used for rendering (/B)", Assert.Single(a83).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text shown before a font is selected leaves the glyphs used for rendering unknown: PDF/A-1's
    /// contents are then not judged, and that is said in <see cref="PdfAValidator.LastValidationError"/>.
    /// </summary>
    [Fact]
    public void A83_PdfA1_UnknownGlyphsUsed_IsNotJudgedAndSaidSo()
    {
        string pdf = Encoding.Latin1.GetString(Type1Pdf(Subset, CffAB(), "/A"));
        string unselected = "BT (A) Tj ET BT /F1 12 Tf 10 10 Td (AB) Tj ET";
        string original = "BT /F1 12 Tf 10 10 Td (AB) Tj ET";
        string stream = " >>\nstream\n";
        byte[] edited = Rebuild(pdf.Replace("/Length " + original.Length.ToString(CultureInfo.InvariantCulture) + stream + original,
            "/Length " + unselected.Length.ToString(CultureInfo.InvariantCulture) + stream + unselected));
        var result = PdfAValidator.Validate(new MemoryStream(edited), PdfAConformancePart.Part1);
        Assert.DoesNotContain(result.Violations, v => v.Code == "A83");
        Assert.Empty(result.Informational);
        Assert.Contains("contents were not compared: the glyphs used for rendering could not be established",
            PdfAValidator.LastValidationError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    /// <summary>Re-serialises a fixture whose objects were edited, so its cross-reference offsets stay right.</summary>
    private static byte[] Rebuild(string pdf)
    {
        var bodies = new List<string>();
        int at = 0;
        for (int n = 1; ; n++)
        {
            string open = n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n";
            int start = pdf.IndexOf(open, at, StringComparison.Ordinal);
            if (start < 0) break;
            int end = pdf.IndexOf("\nendobj\n", start, StringComparison.Ordinal);
            bodies.Add(pdf.Substring(start + open.Length, end - start - open.Length));
            at = end;
        }
        return Build(bodies.ToArray());
    }

    private static void AssertUa56(byte[] pdf, string? problem)
    {
        var ua56 = Ua(pdf).Where(v => v.Code == "UA56").ToList();
        if (problem == null) Assert.Empty(ua56);
        else Assert.Contains(Subset + ": " + problem, Assert.Single(ua56).Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<PdfUaViolation> Ua(byte[] pdf) => PdfUaChecker.Validate(new MemoryStream(pdf)).Violations;

    private static byte[] CffAB() => MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("AB"));

    private static byte[] Type1Pdf(string baseFont, byte[] program, string? charSet,
        string fontFileKey = "/FontFile3", string streamEntries = " /Subtype /Type1C", string shown = "AB", bool withEncoding = true)
        => Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /" + baseFont + " /FirstChar 65 /LastChar 66 /Widths [500 500] " +
                (withEncoding ? "/Encoding << /Differences [65 /A /B] >> " : "") + "/FontDescriptor 6 0 R >>",
            StreamObj(Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 10 Td (" + shown + ") Tj ET"), ""),
            "<< /Type /FontDescriptor /FontName /" + baseFont + " /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 " +
                "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80" +
                (charSet == null ? "" : " /CharSet (" + charSet + ")") + " " + fontFileKey + " 7 0 R >>",
            StreamObj(program, streamEntries));

    private static byte[] Type0Pdf(string baseFont, byte[] program, string fontFileKey, string cidFontType, int[]? cidSet, int[]? cidToGid = null)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /" + baseFont + " /Encoding /Identity-H /DescendantFonts [8 0 R] >>",
            StreamObj(Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 10 Td <0001> Tj ET"), ""),
            "<< /Type /FontDescriptor /FontName /" + baseFont + " /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 " +
                "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80" +
                (cidSet == null ? "" : " /CIDSet 9 0 R") + " " + fontFileKey + " 7 0 R >>",
            StreamObj(program, fontFileKey == "/FontFile3" ? " /Subtype /CIDFontType0C" : ""),
            "<< /Type /Font /Subtype " + cidFontType + " /BaseFont /" + baseFont +
                " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R" +
                (cidToGid == null ? "" : " /CIDToGIDMap 10 0 R") + " >>",
            StreamObj(CidSetBytes(cidSet ?? new int[0]), ""),
        };
        if (cidToGid != null)
        {
            var map = new byte[cidToGid.Length * 2];
            for (int i = 0; i < cidToGid.Length; i++) { map[2 * i] = (byte)(cidToGid[i] >> 8); map[2 * i + 1] = (byte)cidToGid[i]; }
            bodies.Add(StreamObj(map, ""));
        }
        return Build(bodies.ToArray());
    }

    private static byte[] CidSetBytes(int[] cids)
    {
        var bytes = new byte[cids.Length == 0 ? 0 : cids.Max() / 8 + 1];
        foreach (int cid in cids) bytes[cid >> 3] |= (byte)(0x80 >> (cid & 7));
        return bytes;
    }

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
