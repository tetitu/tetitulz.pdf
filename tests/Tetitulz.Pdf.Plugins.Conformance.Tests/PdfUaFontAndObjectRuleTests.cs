// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// PDF/UA-1's UA52 (TrapNet annotations, ISO 14289-1 7.18.2), UA53 (reference XObjects, 7.20), UA54 (an
/// embedded CIDFontType2's <c>/CIDToGIDMap</c>, 7.21.3.2) and UA55 (<c>/ToUnicode</c> values, 7.21.7), and
/// PDF/A's A81 and A82, which read the same functions.
/// </summary>
public class PdfUaFontAndObjectRuleTests
{
    private static List<PdfUaViolation> Ua(byte[] pdf, string code)
        => PdfUaChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == code).ToList();

    private static void AssertFinding<T>(List<T> found, string? expected, Func<T, string> message)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, message(Assert.Single(found)), StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- UA52

    [Theory]
    [InlineData("/F 4", "1 TrapNet annotation(s)")]
    [InlineData("/F 2", null)]                                                  // hidden
    [InlineData("/F 4 /Rect [500 500 510 510] /X 1", null)]                     // outside the CropBox (the /Rect below is replaced)
    public void UA52_ATrapNetAReaderIsPresentedWith(string entries, string? expected)
    {
        string rect = entries.Contains("/Rect") ? "" : "/Rect [10 10 20 20] ";
        byte[] pdf = PdfAXObjectRuleTests.Document("", " /Annots [5 0 R]", "",
            "<< /Type /Annot /Subtype /TrapNet " + rect + entries + " /Contents (t) >>");
        AssertFinding(Ua(pdf, "UA52"), expected, v => v.Message);
    }

    // -------------------------------------------------------------- UA53

    [Theory]
    [InlineData("/Ref << /F (a.pdf) /Page 0 >>", "/X0")]
    [InlineData("", null)]
    public void UA53_AReferenceXObject(string keys, string? expected)
    {
        byte[] pdf = PdfAXObjectRuleTests.Document("", " /Resources << /XObject << /X0 5 0 R >> >>", "/X0 Do",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " + keys + " /Length 1 >>\nstream\nx\nendstream");
        AssertFinding(Ua(pdf, "UA53"), expected, v => v.Message);
    }

    // -------------------------------------------------------------- UA54 / A81

    private static byte[] Composite(string cidToGidMap, bool embedded = true)
    {
        string descriptor = "<< /Type /FontDescriptor /FontName /C /Flags 4" + (embedded ? " /FontFile2 7 0 R" : "") + " >>";
        return PdfAXObjectRuleTests.Document("", " /Resources << /Font << /F1 5 0 R >> >>", "BT /F1 1 Tf <0001> Tj ET",
            "<< /Type /Font /Subtype /Type0 /BaseFont /C /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /C /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            "/FontDescriptor " + descriptor + " " + cidToGidMap + " >>",
            "<< /Length 1 >>\nstream\nx\nendstream",
            ToUnicode("1 beginbfchar\n<0001><0041>\nendbfchar"));
    }

    [Theory]
    [InlineData("/CIDToGIDMap /NoIdentity", true, true)]
    [InlineData("", true, true)]
    [InlineData("/CIDToGIDMap /Identity", true, false)]
    [InlineData("/CIDToGIDMap 7 0 R", true, false)]                             // a stream
    [InlineData("/CIDToGIDMap /NoIdentity", false, false)]                     // no embedded program
    public void UA54AndA81_TheCidToGidMap(string cidToGidMap, bool embedded, bool reported)
    {
        byte[] pdf = Composite(cidToGidMap, embedded);
        Assert.Equal(reported ? 1 : 0, Ua(pdf, "UA54").Count);
        foreach (int part in new[] { 1, 2, 4 })
            Assert.Equal(reported ? 1 : 0, PdfAXObjectRuleTests.Coded(pdf, part, "A81").Count);
    }

    // -------------------------------------------------------------- UA55 / A82

    private static string ToUnicode(string body)
    {
        string cmap = "begincmap\n1 begincodespacerange\n<0000><FFFF>\nendcodespacerange\n" + body + "\nendcmap\n";
        return "<< /Length " + cmap.Length + " >>\nstream\n" + cmap + "\nendstream";
    }

    private static byte[] WithToUnicode(string body, string level = "U", int part = 2, string content = "BT /F1 1 Tf (AB) Tj ET")
    {
        string identification = part == 4 ? "pdfaid:part='4' pdfaid:rev='2020'" : $"pdfaid:part='{part}' pdfaid:conformance='{level}'";
        string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
                     "<rdf:Description rdf:about='' xmlns:pdfaid='http://www.aiim.org/pdfa/ns/id/' " + identification + "/></rdf:RDF></x:xmpmeta>";
        return PdfAXObjectRuleTests.Document(" /Metadata 7 0 R", " /Resources << /Font << /F1 5 0 R >> >>", content,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>",
            ToUnicode(body),
            "<< /Type /Metadata /Subtype /XML /Length " + xmp.Length + " >>\nstream\n" + xmp + "\nendstream");
    }

    [Theory]
    [InlineData("1 beginbfchar\n<0041><0000>\nendbfchar", "code 41 maps to U+0000")]
    [InlineData("1 beginbfchar\n<0041><FEFF>\nendbfchar", "code 41 maps to U+FEFF")]
    [InlineData("1 beginbfchar\n<0042><FFFE>\nendbfchar", "code 42 maps to U+FFFE")]
    [InlineData("1 beginbfchar\n<0041><>\nendbfchar", null)]
    [InlineData("1 beginbfchar\n<0041><0041>\nendbfchar", null)]
    public void UA55AndA82_TheToUnicodeValues(string body, string? expected)
    {
        byte[] pdf = WithToUnicode(body);
        AssertFinding(Ua(pdf, "UA55"), expected, v => v.Message);
        AssertFinding(PdfAXObjectRuleTests.Coded(pdf, 2, "A82"), expected, v => v.Message);
    }

    [Theory]
    [InlineData(2, "U", true)]
    [InlineData(3, "A", true)]
    [InlineData(2, "B", false)]
    [InlineData(4, "", true)]                       // PDF/A-4: every level
    [InlineData(1, "A", false)]                     // PDF/A-1 has no such rule
    public void A82_IsLevelsAAndUAndPdfA4s(int part, string level, bool reported)
    {
        byte[] pdf = WithToUnicode("1 beginbfchar\n<0041><FEFF>\nendbfchar", level, part);
        Assert.Equal(reported ? 1 : 0, PdfAXObjectRuleTests.Coded(pdf, part, "A82").Count);
    }

    [Fact]
    public void UA55_AFontNotUsedForRendering_IsNotJudged()
    {
        Assert.Single(Ua(WithToUnicode("1 beginbfchar\n<0041><FEFF>\nendbfchar"), "UA55"));
        Assert.Empty(Ua(WithToUnicode("1 beginbfchar\n<0041><FEFF>\nendbfchar", content: "q Q"), "UA55"));
    }
}
