// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The file structure rules read from the file's own bytes: A42 (the trailer's <c>/ID</c>), A43 (after
/// the last <c>%%EOF</c>), A44 (the <c>xref</c> keyword and subsection headers), A45 (an indirect
/// object's header and <c>endobj</c>), A46 (PDF/A-4's <c>/Info</c>) — ISO 19005-1 6.1.3/6.1.4/6.1.8,
/// ISO 19005-2/3 6.1.3/6.1.4/6.1.9, ISO 19005-4 6.1.3/6.1.4/6.1.8, the published profile's rules of those numbers.
/// </summary>
public class PdfAFileStructureTests
{
    private const string Id = "/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]";

    /// <summary>How the file's pieces are written; each field is the exact text the writer puts there.</summary>
    private sealed class Shape
    {
        public string ObjectHeader = "{0} 0 obj\n";        // {0}: the object number
        public string EndObj = "\nendobj\n";
        public string XrefKeyword = "xref\n";
        public string SubsectionSeparator = " ";
        public string TrailerExtras = " " + Id;
        public string AfterEof = "\n";
        public string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
        public int OffsetShift;                             // added to every table offset
    }

    private static byte[] Build(Shape s)
    {
        var bodies = new List<string>
        {
            s.Catalog,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length + s.OffsetShift);
            sb.Append(string.Format(CultureInfo.InvariantCulture, s.ObjectHeader, i + 1)).Append(bodies[i]).Append(s.EndObj);
        }
        int xref = sb.Length;
        sb.Append(s.XrefKeyword).Append('0').Append(s.SubsectionSeparator).Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R")
          .Append(s.TrailerExtras).Append(" >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF").Append(s.AfterEof);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static List<PdfAViolation> Coded(byte[] pdf, int part, string code)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    private static string[] Structure(byte[] pdf, int part)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Select(v => v.Code)
            .Where(c => c is "A42" or "A43" or "A44" or "A45" or "A46").Distinct().ToArray();
    }

    [Fact]
    public void AWellFormedFile_PassesEveryRule()
    {
        foreach (int part in new[] { 1, 2, 3, 4 })
            Assert.Empty(Structure(Build(new Shape()), part));
    }

    // -------------------------------------------------------------- A42

    [Theory]
    [InlineData("", true)]
    [InlineData(" /ID [<> <>]", true)]
    [InlineData(" /ID [() ()]", true)]
    [InlineData(" /ID [< > < >]", true)]                                   // white space in a hex string is no digit
    [InlineData(" /ID [<0011> <>]", false)]
    [InlineData(" /ID [(a) (b)]", false)]
    [InlineData(" /ID 9 0 R", false)]
    public void A42_TheTrailerNeedsANonEmptyId(string extras, bool reported)
    {
        var a42 = Coded(Build(new Shape { TrailerExtras = extras }), 2, "A42");
        Assert.Equal(reported ? 1 : 0, a42.Count);
    }

    [Fact]
    public void A42_TheLastTrailerIsTheOneRead()
    {
        // An update whose trailer lacks /ID after a revision whose trailer has one.
        byte[] first = Build(new Shape());
        string text = Encoding.Latin1.GetString(first);
        int firstXref = int.Parse(text.Substring(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10).Split('\n')[0], CultureInfo.InvariantCulture);
        var sb = new StringBuilder(text);
        int objectAt = sb.Length;
        sb.Append("4 0 obj\n<< /Producer (x) >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n4 1\n").Append(objectAt.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R /Prev ").Append(firstXref.ToString(CultureInfo.InvariantCulture))
          .Append(" >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Assert.Single(Coded(Encoding.Latin1.GetBytes(sb.ToString()), 1, "A42"));
        Assert.Empty(Coded(first, 1, "A42"));
    }

    /// <summary>A file whose first object is a linearization dictionary, with a first-page trailer before the rest.</summary>
    private static byte[] Linearized(string firstTrailerExtras, string lastTrailerExtras)
    {
        string text = Encoding.Latin1.GetString(Build(new Shape { TrailerExtras = lastTrailerExtras }));
        int afterComment = text.IndexOf('\n', text.IndexOf('\n') + 1) + 1;
        string early = "%first-page section\ntrailer\n<< /Size 4" + firstTrailerExtras + " >>\n";
        string marker = "%/Linearized 1\n";
        // Shift every table offset and startxref by what was inserted before the objects.
        int shift = early.Length + marker.Length;
        string body = text.Substring(afterComment);
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length == 19 && lines[i].EndsWith(" 00000 n ", StringComparison.Ordinal))
                lines[i] = (long.Parse(lines[i].Substring(0, 10), CultureInfo.InvariantCulture) + shift).ToString("D10", CultureInfo.InvariantCulture) + " 00000 n ";
        for (int i = 0; i + 1 < lines.Length; i++)
            if (lines[i] == "startxref")
                lines[i + 1] = (long.Parse(lines[i + 1], CultureInfo.InvariantCulture) + shift).ToString(CultureInfo.InvariantCulture);
        return Encoding.Latin1.GetBytes(text.Substring(0, afterComment) + marker + early + string.Join("\n", lines));
    }

    [Fact]
    public void A42_ALinearizedFile_IsJudgedByItsFirstPageTrailer()
    {
        Assert.Empty(Coded(Linearized(" " + Id, ""), 1, "A42"));
        Assert.Single(Coded(Linearized("", " " + Id), 1, "A42"));
    }

    // -------------------------------------------------------------- A43

    [Theory]
    [InlineData("", false)]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", false)]
    [InlineData("\n\n", true)]
    [InlineData("\nGarbage", true)]
    public void A43_OnlyOneEolMayFollowTheLastEof(string after, bool reported)
        => Assert.Equal(reported ? 1 : 0, Coded(Build(new Shape { AfterEof = after }), 2, "A43").Count);

    // -------------------------------------------------------------- A44

    [Theory]
    [InlineData("xref\n", false)]
    [InlineData("xref\r\n", false)]
    [InlineData("xref \n", true)]
    [InlineData("xref\n\n", true)]
    [InlineData("xref\r\n\r\n", true)]
    public void A44_TheXrefKeywordIsFollowedByOneEol(string keyword, bool reported)
    {
        var a44 = Coded(Build(new Shape { XrefKeyword = keyword }), 2, "A44");
        if (reported)
            Assert.Contains("not separated by one EOL marker", Assert.Single(a44).Message, StringComparison.Ordinal);
        else
            Assert.Empty(a44);
    }

    [Fact]
    public void A44_AnEarlierSectionOfTheChain_IsReadToo()
    {
        byte[] first = Build(new Shape { XrefKeyword = "xref \n" });
        string text = Encoding.Latin1.GetString(first);
        int firstXref = int.Parse(text.Substring(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10).Split('\n')[0], CultureInfo.InvariantCulture);
        var sb = new StringBuilder(text);
        int objectAt = sb.Length;
        sb.Append("4 0 obj\n<< /Producer (x) >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n4 1\n").Append(objectAt.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R ").Append(Id).Append(" /Prev ").Append(firstXref.ToString(CultureInfo.InvariantCulture))
          .Append(" >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        var a44 = Assert.Single(Coded(Encoding.Latin1.GetBytes(sb.ToString()), 2, "A44"));
        Assert.Contains("xref at byte " + firstXref.ToString(CultureInfo.InvariantCulture), a44.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A44_ASubsectionHeadersTwoSpaces_ArePdfA1sFinding()
    {
        byte[] pdf = Build(new Shape { SubsectionSeparator = "  " });
        Assert.Contains("one space", Assert.Single(Coded(pdf, 1, "A44")).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(pdf, 2, "A44"));
    }

    // -------------------------------------------------------------- A45

    [Theory]
    [InlineData("{0}  0 obj\n", "\nendobj\n", "after its number")]
    [InlineData("{0} 0  obj\n", "\nendobj\n", "before obj")]
    [InlineData("{0} 0 obj ", "\nendobj\n", "after obj")]
    [InlineData("{0} 0 obj\n", " endobj\n", "before endobj")]
    [InlineData("{0} 0 obj\n", "\nendobj ", "after endobj")]
    public void A45_AnObjectsHeaderAndEndobj_AreSpacedByTheRule(string header, string endobj, string expected)
        => Assert.Contains(expected, Assert.Single(Coded(Build(new Shape { ObjectHeader = header, EndObj = endobj }), 2, "A45")).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A45_TheLastObjectBeforeTheTable_IsJudgedToo()
    {
        // Every endobj but the last is well-formed; the last is followed by spaces, then the table.
        string text = Encoding.Latin1.GetString(Build(new Shape()));
        int last = text.LastIndexOf("endobj\n", StringComparison.Ordinal);
        string broken = text.Substring(0, last) + "endobj  " + text.Substring(last + 7);
        // The table's offset to "xref" moved by one byte; restate startxref.
        int xref = broken.IndexOf("\nxref", StringComparison.Ordinal) + 1;
        int sx = broken.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10;
        broken = broken.Substring(0, sx) + xref.ToString(CultureInfo.InvariantCulture) + broken.Substring(broken.IndexOf('\n', sx));
        Assert.Contains("object 3: no EOL marker after endobj", Assert.Single(Coded(Encoding.Latin1.GetBytes(broken), 2, "A45")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A45_ATableOffsetAtTheEolBeforeTheHeader_IsReadPast()
    {
        Assert.Empty(Coded(Build(new Shape { OffsetShift = -1 }), 2, "A45"));
        // Control: a header with no EOL before it at all is still found.
        Assert.Single(Coded(Build(new Shape { EndObj = "\nendobj " }), 2, "A45"));
    }

    // -------------------------------------------------------------- A46

    [Fact]
    public void A46_PdfA4sInformationDictionary()
    {
        static byte[] WithInfo(string info, string catalog)
        {
            string text = Encoding.Latin1.GetString(Build(new Shape { Catalog = catalog, TrailerExtras = " " + Id + " /Info " + info }));
            return Encoding.Latin1.GetBytes(text);
        }

        Assert.Equal(2, Coded(WithInfo("<< /Title (t) >>", "<< /Type /Catalog /Pages 2 0 R >>"), 4, "A46").Count);
        Assert.Single(Coded(WithInfo("<< /ModDate (D:2020) >>", "<< /Type /Catalog /Pages 2 0 R >>"), 4, "A46"));
        Assert.Single(Coded(WithInfo("<< /ModDate (D:2020) /Title (t) >>", "<< /Type /Catalog /Pages 2 0 R /PieceInfo << >> >>"), 4, "A46"));
        Assert.Empty(Coded(WithInfo("<< /ModDate (D:2020) >>", "<< /Type /Catalog /Pages 2 0 R /PieceInfo << >> >>"), 4, "A46"));
        // Not a rule before PDF/A-4.
        Assert.Empty(Coded(WithInfo("<< /Title (t) >>", "<< /Type /Catalog /Pages 2 0 R >>"), 2, "A46"));
    }

    // -------------------------------------------------------------- the converter's /ID

    [Fact]
    public void TheConverter_WritesAnIdIntoAFileThatHasNone()
    {
        byte[] input = Build(new Shape { TrailerExtras = "" });
        Assert.Single(Coded(input, 2, "A42"));

        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA2b(new MemoryStream(input), output,
            new PdfAConversionOptions { IccProfile = IccProfileFixtures.Rgb() });

        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A42", StringComparison.Ordinal));
        Assert.Empty(Coded(output.ToArray(), 2, "A42"));
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var id = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Trailer[PdfName.ID]));
        Assert.Equal(2, id.Count);
    }
}
