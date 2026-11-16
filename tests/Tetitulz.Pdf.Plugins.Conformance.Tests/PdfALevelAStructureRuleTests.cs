// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Level A's structure rules: A74 (a <c>/StructTreeRoot</c>) and A75 (every non-standard structure type
/// maps to a standard one, without a cycle; PDF/A-2 and -3 remap no standard type) — ISO 19005-1
/// 6.8.3.3 and 6.8.3.4, 19005-2/3 6.7.3.3 and 6.7.3.4, the published profile's rules of those numbers.
/// </summary>
public class PdfALevelAStructureRuleTests
{
    private const string Marked = " /MarkInfo << /Marked true >>";

    /// <summary>A level A file whose tree holds one element of <paramref name="role"/> under a root carrying <paramref name="rootExtra"/>.</summary>
    private static List<PdfAViolation> Coded(int part, string code, string role, string rootExtra = "", string level = "A")
        => PdfAXObjectRuleTests.Coded(PdfAEmbeddedFileRuleTests.PdfA(part, Marked + " /StructTreeRoot 5 0 R", level,
            "<< /Type /StructTreeRoot /K 6 0 R" + rootExtra + " >>",
            "<< /Type /StructElem /S /" + role + " /P 5 0 R >>"), part, code);

    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A74

    [Theory]
    [InlineData(1, "A", "has no /StructTreeRoot")]
    [InlineData(2, "A", "has no /StructTreeRoot")]
    [InlineData(2, "B", null)]
    [InlineData(4, null, null)]
    [InlineData(4, "A", null)]                 // PDF/A-4 has no level A: a letter it does not admit decides nothing here
    public void A74_ALevelAFile_HasAStructureTree(int part, string? level, string? expected)
        => AssertFinding(PdfAXObjectRuleTests.Coded(PdfAEmbeddedFileRuleTests.PdfA(part, Marked, level ?? "B"), part, "A74"), expected);

    [Fact]
    public void A74_ATreeThatIsPresent_IsAccepted()
        => Assert.Empty(Coded(2, "A74", "P"));

    // -------------------------------------------------------------- A75

    [Theory]
    [InlineData(2, "Custom", "", "/Custom is not in the /RoleMap")]
    [InlineData(2, "Custom", " /RoleMap << /Custom /P >>", null)]
    [InlineData(2, "Custom", " /RoleMap << /Custom /Mine /Mine /P >>", null)]
    [InlineData(2, "Custom", " /RoleMap << /Custom /Mine >>", "/Custom maps to /Mine, which is neither standard nor mapped")]
    [InlineData(2, "Custom", " /RoleMap << /Custom /Mine /Mine /Custom >>", "/Custom maps in a circle through /Custom")]
    [InlineData(1, "Custom", " /RoleMap << /Custom /A /A /B /B /A >>", "/Custom maps in a circle through /A")]
    [InlineData(2, "Custom", " /RoleMap << /Custom (P) >>", "/Custom maps /Custom to a non-name")]
    [InlineData(2, "P", "", null)]
    [InlineData(2, "Custom", "", null, "B")]
    public void A75_ANonStandardType_MapsToAStandardOne(int part, string role, string rootExtra, string? expected, string level = "A")
        => AssertFinding(Coded(part, "A75", role, rootExtra, level), expected);

    [Theory]
    [InlineData("THead", 1, "", "/THead is not in the /RoleMap")]               // PDF 1.5's: not standard in PDF/A-1
    [InlineData("THead", 2, "", null)]
    [InlineData("Custom", 1, " /RoleMap << /Custom /TBody >>", "/Custom maps to /TBody, which is neither standard nor mapped")]
    [InlineData("Custom", 2, " /RoleMap << /Custom /TBody >>", null)]
    public void A75_PdfA1_ReadsThePdf14Vocabulary(string role, int part, string rootExtra, string? expected)
        => AssertFinding(Coded(part, "A75", role, rootExtra), expected);

    [Theory]
    [InlineData(2, " /RoleMap << /P /Custom >>", "/RoleMap remaps the standard /P to /Custom")]
    [InlineData(3, " /RoleMap << /P (x) >>", "/RoleMap remaps the standard /P to a non-name")]
    [InlineData(2, " /RoleMap << /P /Div >>", null)]
    [InlineData(1, " /RoleMap << /P /Custom >>", null)]                        // PDF/A-1 has no such rule
    public void A75_AStandardType_IsNotRemapped(int part, string rootExtra, string? expected)
        => AssertFinding(Coded(part, "A75", "Div", rootExtra), expected);

    [Fact]
    public void A75_ARoleUsedTwice_IsOneOccurrence()
    {
        var pdf = PdfAEmbeddedFileRuleTests.PdfA(2, Marked + " /StructTreeRoot 5 0 R", "A",
            "<< /Type /StructTreeRoot /K [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Custom /P 5 0 R >>",
            "<< /Type /StructElem /S /Custom /P 5 0 R >>");
        string message = Assert.Single(PdfAXObjectRuleTests.Coded(pdf, 2, "A75")).Message;
        int at = message.IndexOf("/Custom", StringComparison.Ordinal);
        Assert.True(at >= 0, message);
        Assert.Equal(-1, message.IndexOf("/Custom is", at + 1, StringComparison.Ordinal));
    }
}
