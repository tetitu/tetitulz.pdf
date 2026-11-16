// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="TrueTypeFontRules"/> — the TrueType readings PDF/UA-1's UA51 (ISO 14289-1 7.21.6), PDF/A's
/// A80 (ISO 19005-1 6.3.7, 19005-2/3 6.2.11.6, 19005-4 6.2.10.6) and PDF/UA-2's §8.4.5.7 checks share —
/// and the two rules that read them, asked about one document each.
/// </summary>
public class TrueTypeFontRulesTests
{
    /// <summary>A <c>cmap</c> table declaring each (platform, encoding) pair, every one a format 0 subtable.</summary>
    private static byte[] Cmap(params (int Platform, int Encoding)[] pairs)
    {
        var table = new List<byte>();
        void U16(int v) { table.Add((byte)(v >> 8)); table.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        U16(0);
        U16(pairs.Length);
        int offset = 4 + 8 * pairs.Length;
        foreach (var (platform, encoding) in pairs)
        {
            U16(platform);
            U16(encoding);
            U32(offset);
            offset += 262;
        }
        foreach (var _ in pairs)
        {
            U16(0);            // format 0
            U16(262);
            U16(0);
            for (int c = 0; c < 256; c++) table.Add(c == 'A' ? (byte)1 : (byte)0);
        }
        return table.ToArray();
    }

    private static TrueTypeFontProgram Program(params (int, int)[] pairs)
        => TrueTypeFontProgram.Parse(MinimalTrueType.BuildWithTables("A",
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["cmap"] = Cmap(pairs) }));

    [Fact]
    public void TheFixture_DeclaresTheSubtablesAsked()
    {
        var program = Program((3, 0), (1, 0));
        Assert.Equal(2, program.CmapSubtableCount);
        Assert.True(program.HasCmapSubtable(3, 0));
        Assert.True(program.HasCmapSubtable(1, 0));
        Assert.False(program.HasCmapSubtable(3, 1));
    }

    public static TheoryData<TrueTypeCmapRule, bool, (int, int)[], bool> CmapCases => new()
    {
        { TrueTypeCmapRule.PdfA1, true, new[] { (3, 0) }, true },
        { TrueTypeCmapRule.PdfA1, true, new[] { (3, 0), (1, 0) }, false },
        { TrueTypeCmapRule.PdfA1, false, new[] { (3, 0), (1, 0) }, true },
        { TrueTypeCmapRule.Iso32000Part1, false, new[] { (3, 1) }, true },
        { TrueTypeCmapRule.Iso32000Part1, false, new[] { (3, 0) }, false },
        { TrueTypeCmapRule.Iso32000Part1, false, new[] { (3, 0), (1, 0) }, true },
        { TrueTypeCmapRule.Iso32000Part1, true, new[] { (1, 0) }, true },
        { TrueTypeCmapRule.Iso32000Part1, true, new[] { (3, 0), (3, 1) }, true },
        { TrueTypeCmapRule.Iso32000Part1, true, new[] { (3, 1), (1, 0) }, false },
        { TrueTypeCmapRule.Iso32000Part2, false, new[] { (3, 1) }, true },
        { TrueTypeCmapRule.Iso32000Part2, false, new[] { (1, 0) }, true },
        { TrueTypeCmapRule.Iso32000Part2, false, new[] { (3, 0) }, false },
        { TrueTypeCmapRule.Iso32000Part2, true, new[] { (3, 0) }, true },
        { TrueTypeCmapRule.Iso32000Part2, true, new[] { (1, 0) }, true },
        { TrueTypeCmapRule.Iso32000Part2, true, new[] { (3, 1) }, false },
    };

    [Theory]
    [MemberData(nameof(CmapCases))]
    public void CmapConforms_IsEachStandardsRule(TrueTypeCmapRule rule, bool symbolic, (int, int)[] pairs, bool conforms)
        => Assert.Equal(conforms, TrueTypeFontRules.CmapConforms(Program(pairs), symbolic, rule));

    [Fact]
    public void CmapConforms_WithNoProgram_JudgesNothing()
        => Assert.True(TrueTypeFontRules.CmapConforms(null!, symbolic: true, TrueTypeCmapRule.PdfA1));

    // -------------------------------------------------------------- the dictionary readings

    private static (PdfDocument Doc, PdfDictionary Font) Font(string fontDictionary, string? program = null)
    {
        var extra = new List<string> { fontDictionary };
        if (program != null) extra.Add(program);
        var bytes = PdfAXObjectRuleTests.Document("", " /Resources << /Font << /F1 5 0 R >> >>", "BT /F1 1 Tf (A) Tj ET", extra.ToArray());
        var doc = PdfDocument.Open(new MemoryStream(bytes));
        return (doc, (PdfDictionary)doc.ResolveObject(new PdfReference(5, 0)));
    }

    private const string Flags32 = "/FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 32 >>";
    private const string Flags4 = "/FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 4 >>";

    [Theory]
    [InlineData("/Encoding /WinAnsiEncoding " + Flags32, TrueTypeCmapRule.Iso32000Part1, null)]
    [InlineData("/Encoding /MacRomanEncoding " + Flags32, TrueTypeCmapRule.PdfA1, null)]
    [InlineData(Flags32, TrueTypeCmapRule.Iso32000Part1, "declaring neither MacRoman nor WinAnsi")]
    [InlineData("/Encoding << /BaseEncoding /Identity >> " + Flags32, TrueTypeCmapRule.Iso32000Part1, "declaring /Identity")]
    [InlineData("/Encoding << /Differences [65 /A] >> " + Flags32, TrueTypeCmapRule.Iso32000Part1, "declaring neither MacRoman nor WinAnsi")]
    [InlineData("/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /gravee] >> " + Flags32, TrueTypeCmapRule.Iso32000Part2, "/Differences names /gravee")]
    [InlineData("/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /Agrave] >> " + Flags32, TrueTypeCmapRule.Iso32000Part1, null)]
    [InlineData("/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /Agrave] >> " + Flags32, TrueTypeCmapRule.PdfA1, "with /Differences")]
    [InlineData("/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65] >> " + Flags32, TrueTypeCmapRule.PdfA1, null)]   // names no glyph
    [InlineData("/Encoding /Custom " + Flags4, TrueTypeCmapRule.Iso32000Part1, "a symbolic font with an /Encoding")]
    [InlineData(Flags4, TrueTypeCmapRule.Iso32000Part1, null)]
    [InlineData("/Encoding /Custom", TrueTypeCmapRule.Iso32000Part1, null)]                  // no flags stated: not judged
    public void WhatIsWrong_ReadsTheFontDictionary(string entries, TrueTypeCmapRule rule, string? expected)
    {
        var (doc, font) = Font("<< /Type /Font /Subtype /TrueType /BaseFont /T " + entries + " >>");
        using (doc)
        {
            string? problem = TrueTypeFontRules.WhatIsWrong(doc, font, rule);
            if (expected == null) Assert.Null(problem);
            else Assert.Contains(expected, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WhatIsWrong_IsAboutTrueTypeFontsAlone()
    {
        var (doc, font) = Font("<< /Type /Font /Subtype /Type1 /BaseFont /T /Encoding /Custom " + Flags32 + " >>");
        using (doc) Assert.Null(TrueTypeFontRules.WhatIsWrong(doc, font, TrueTypeCmapRule.Iso32000Part1));
    }

    private static string ProgramObject((int, int)[] pairs)
    {
        byte[] data = MinimalTrueType.BuildWithTables("A", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["cmap"] = Cmap(pairs) });
        string hex = Convert.ToHexString(data) + ">";
        return "<< /Filter /ASCIIHexDecode /Length " + hex.Length + " >>\nstream\n" + hex + "\nendstream";
    }

    [Fact]
    public void WhatIsWrong_ReadsTheEmbeddedProgramsCmap()
    {
        const string font = "<< /Type /Font /Subtype /TrueType /BaseFont /T /Encoding /WinAnsiEncoding " +
                            "/FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 32 /FontFile2 6 0 R >> >>";
        var (doc, dict) = Font(font, ProgramObject(new[] { (3, 0) }));
        using (doc)
        {
            Assert.Contains("cmap subtables are not those asked of a non-symbolic font",
                TrueTypeFontRules.WhatIsWrong(doc, dict, TrueTypeCmapRule.Iso32000Part1), StringComparison.Ordinal);
            Assert.Null(TrueTypeFontRules.WhatIsWrong(doc, dict, TrueTypeCmapRule.PdfA1));
        }
    }

    // -------------------------------------------------------------- the rules: UA51 and A80

    private static byte[] Document(string entries, string content = "BT /F1 1 Tf (A) Tj ET")
        => PdfAXObjectRuleTests.Document("", " /Resources << /Font << /F1 5 0 R >> >>", content,
            "<< /Type /Font /Subtype /TrueType /BaseFont /Ttf " + entries + " >>");

    [Fact]
    public void UA51AndA80_AnswerOneQuestion()
    {
        byte[] bad = Document("/Encoding /Custom " + Flags4);
        Assert.Contains("Ttf: a symbolic font with an /Encoding",
            Assert.Single(PdfUaChecker.Validate(new MemoryStream(bad)).Violations, v => v.Code == "UA51").Message, StringComparison.Ordinal);
        foreach (int part in new[] { 1, 2, 4 })
            Assert.Contains("Ttf: a symbolic font with an /Encoding",
                Assert.Single(PdfAXObjectRuleTests.Coded(bad, part, "A80")).Message, StringComparison.Ordinal);

        byte[] good = Document(Flags4);
        Assert.DoesNotContain(PdfUaChecker.Validate(new MemoryStream(good)).Violations, v => v.Code == "UA51");
        Assert.Empty(PdfAXObjectRuleTests.Coded(good, 2, "A80"));
    }

    [Theory]
    [InlineData(1, "ISO 19005-1 6.3.7")]
    [InlineData(3, "ISO 19005-3 6.2.11.6")]
    [InlineData(4, "ISO 19005-4 6.2.10.6")]
    public void A80_CitesThePartsClause(int part, string clause)
        => Assert.Contains(clause, Assert.Single(PdfAXObjectRuleTests.Coded(Document(Flags32), part, "A80")).Message, StringComparison.Ordinal);

    /// <summary>Each part's own cmap rule, through A80: one program, three verdicts.</summary>
    [Theory]
    [InlineData("/Encoding /WinAnsiEncoding /FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 32 /FontFile2 6 0 R >>", "30", 1, false)]
    [InlineData("/Encoding /WinAnsiEncoding /FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 32 /FontFile2 6 0 R >>", "30", 2, true)]
    [InlineData("/Encoding /WinAnsiEncoding /FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 32 /FontFile2 6 0 R >>", "30", 4, true)]
    [InlineData("/FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 4 /FontFile2 6 0 R >>", "31+10", 1, true)]
    [InlineData("/FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 4 /FontFile2 6 0 R >>", "31+10", 2, true)]
    [InlineData("/FontDescriptor << /Type /FontDescriptor /FontName /T /Flags 4 /FontFile2 6 0 R >>", "31+10", 4, false)]
    public void A80_TheCmapRule_IsThePartsOwn(string entries, string subtables, int part, bool reported)
    {
        var pairs = subtables == "30" ? new[] { (3, 0) } : new[] { (3, 1), (1, 0) };
        byte[] pdf = PdfAXObjectRuleTests.Document("", " /Resources << /Font << /F1 5 0 R >> >>", "BT /F1 1 Tf (A) Tj ET",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Ttf " + entries + " >>", ProgramObject(pairs));
        Assert.Equal(reported ? 1 : 0, PdfAXObjectRuleTests.Coded(pdf, part, "A80").Count);
    }

    [Fact]
    public void UA51AndA80_JudgeOnlyFontsUsedForRendering()
    {
        byte[] unused = Document("/Encoding /Custom " + Flags4, "q Q");
        Assert.DoesNotContain(PdfUaChecker.Validate(new MemoryStream(unused)).Violations, v => v.Code == "UA51");
        Assert.Empty(PdfAXObjectRuleTests.Coded(unused, 2, "A80"));
    }
}
