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
/// A77 — level A of PDF/A-1, levels A and U of PDF/A-2 and -3: every font used for rendering maps its
/// codes to Unicode (ISO 19005-1 6.3.8, 19005-2/3 6.2.11.7.2), through <c>FontUnicodeMapping</c>, the
/// answer PDF/UA's UA26 reads too — a Type 3 font's <c>/Differences</c> names known to the Adobe Glyph
/// List.
/// </summary>
public class PdfAFontUnicodeRuleTests
{
    private const string Type3Base = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
                                     "/CharProcs << >> /FirstChar 65 /LastChar 65 /Widths [500] ";

    private static byte[] Pdf(string font, int part, string level, string content = "BT /F1 1 Tf (A) Tj ET")
    {
        string identification = part == 4 ? "pdfaid:part='4' pdfaid:rev='2020'" : $"pdfaid:part='{part}' pdfaid:conformance='{level}'";
        string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
                     "<rdf:Description rdf:about='' xmlns:pdfaid='http://www.aiim.org/pdfa/ns/id/' " + identification + "/></rdf:RDF></x:xmpmeta>";
        return PdfAXObjectRuleTests.Document(" /Metadata 6 0 R", " /Resources << /Font << /F1 5 0 R >> >>", content,
            font, "<< /Type /Metadata /Subtype /XML /Length " + xmp.Length + " >>\nstream\n" + xmp + "\nendstream");
    }

    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null)]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)]                       // nonsymbolic, built-in
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Odd /Encoding /Custom >>", "Odd")]
    [InlineData("<< /Type /Font /Subtype /Type0 /BaseFont /Composite /Encoding /Identity-H /DescendantFonts [<< /Subtype /CIDFontType2 " +
                "/BaseFont /Composite /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> >>] >>", "Composite")]
    [InlineData(Type3Base + "/Name /T3g /Encoding << /Type /Encoding /Differences [65 /g1] >> >>", "T3g")]
    [InlineData(Type3Base + "/Name /T3none >>", "T3none")]                                              // no /Encoding: nothing maps
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /OwnNames /Encoding << /Differences [65 /g1] >> >>", null)]   // a Type 1 program's names
    [InlineData(Type3Base + "/Name /T3A /Encoding << /Type /Encoding /Differences [65 /A] >> >>", null)]
    [InlineData(Type3Base + "/Name /T3uni /Encoding << /Type /Encoding /Differences [65 /uni05D0] >> >>", null)]
    [InlineData(Type3Base + "/Name /T3tu /Encoding << /Type /Encoding /Differences [65 /g1] >> /ToUnicode 7 0 R >>", null)]
    public void A77_AFontUsedForRendering_MapsItsCodesToUnicode(string font, string? expected)
        => AssertFinding(PdfAXObjectRuleTests.Coded(Pdf(font, 2, "U"), 2, "A77"), expected);

    [Theory]
    [InlineData(1, "A", true)]
    [InlineData(1, "B", false)]
    [InlineData(1, "U", false)]                // PDF/A-1 has no level U
    [InlineData(2, "A", true)]
    [InlineData(3, "U", true)]
    [InlineData(2, "B", false)]
    [InlineData(4, "", false)]
    public void A77_IsLevelAAndUs(int part, string level, bool reported)
    {
        var pdf = Pdf(Type3Base + "/Name /T3g /Encoding << /Type /Encoding /Differences [65 /g1] >> >>", part, level);
        Assert.Equal(reported ? 1 : 0, PdfAXObjectRuleTests.Coded(pdf, part, "A77").Count);
    }

    [Fact]
    public void A77_AFontNotUsedForRendering_IsNotJudged()
    {
        const string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Odd /Encoding /Custom >>";
        Assert.Empty(PdfAXObjectRuleTests.Coded(Pdf(font, 2, "U", "q Q"), 2, "A77"));
        Assert.Empty(PdfAXObjectRuleTests.Coded(Pdf(font, 2, "U", "BT /F1 1 Tf 3 Tr (A) Tj ET"), 2, "A77"));
    }

    [Theory]
    [InlineData("/Differences [65 /g1]", true)]
    [InlineData("/Differences [65 /A]", false)]
    public void UA26AndA77_AnswerOneQuestion(string differences, bool unmapped)
    {
        var pdf = Pdf(Type3Base + "/Name /T3 /Encoding << /Type /Encoding " + differences + " >> >>", 2, "U");
        bool a77 = PdfAXObjectRuleTests.Coded(pdf, 2, "A77").Count == 1;
        bool ua26 = PdfUaChecker.Validate(new MemoryStream(pdf)).Violations.Any(v => v.Code == "UA26");
        Assert.Equal(new[] { unmapped, unmapped }, new[] { a77, ua26 });
    }
}
