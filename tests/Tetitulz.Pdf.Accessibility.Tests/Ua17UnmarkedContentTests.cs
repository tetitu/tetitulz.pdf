// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// UA17 — ISO 14289-1 7.1, "content shall be marked as Artifact or tagged as real content" — over every kind of
/// content a page draws: text, a path or shading paint, an image XObject, an inline image, and what a form
/// XObject draws, each inside no marked-content sequence at all.
/// </summary>
public sealed class Ua17UnmarkedContentTests
{
    /// <summary>
    /// A tagged page whose paragraph is marked, with <paramref name="drawing"/> after it: content outside every
    /// sequence is reported by kind; content in an artifact, or in any sequence, is not.
    /// </summary>
    [Theory]
    [InlineData("", null)]
    [InlineData("0 0 10 10 re f", "1 path or shading paint(s) outside every marked-content sequence")]
    [InlineData("0 0 m 10 10 l S 0 0 5 5 re B", "2 path or shading paint(s) outside every marked-content sequence")]
    [InlineData("/Sh0 sh", "1 path or shading paint(s) outside every marked-content sequence")]
    [InlineData("/Im0 Do", "1 image(s) outside every marked-content sequence")]
    [InlineData("BI /W 1 /H 1 /CS /G /BPC 8 ID a EI", "1 image(s) outside every marked-content sequence")]
    [InlineData("BT /F1 12 Tf (x) Tj ET 0 0 1 1 re f /Im0 Do",
        "1 of 2 text-showing operator(s), 1 path or shading paint(s), 1 image(s) outside every marked-content sequence")]
    [InlineData("0 0 10 10 re n", null)]
    [InlineData("/Artifact BMC 0 0 10 10 re f /Im0 Do EMC", null)]
    [InlineData("/Span BMC 0 0 10 10 re f BI /W 1 /H 1 /CS /G /BPC 8 ID a EI EMC", null)]
    [InlineData("/Fm0 Do", "1 path or shading paint(s) outside every marked-content sequence")]
    [InlineData("/Artifact BMC /Fm0 Do EMC", null)]
    public void UA17_ContentOfEveryKind_IsTaggedOrAnArtifact(string drawing, string? expected)
    {
        var found = Audit(Page(drawing)).Issues.Where(i => i.Code == "UA17").ToList();
        if (expected == null)
        {
            Assert.Empty(found);
            return;
        }
        AccessibilityIssue issue = Assert.Single(found);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("Page draws content that belongs to no marked-content sequence (" + expected + ")", issue.Message);
    }

    /// <summary>
    /// 1 catalog, 2 pages, 3 the tagged page, 4 its content — a marked paragraph, then <paramref name="drawing"/> —
    /// 5 Helvetica, 6 the structure tree root, 7 Document, 8 the P claiming MCID 0, 9 the parent tree, 10 an image,
    /// 11 a form filling a path unmarked.
    /// </summary>
    private static byte[] Page(string drawing)
    {
        string content = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (A tagged paragraph.) Tj ET EMC\n" + drawing + "\n";
        const string form = "0 0 10 10 re f";
        return PdfUa2StructureCheckerTests.BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) " +
                "/ViewerPreferences << /DisplayDocTitle true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> " +
                "/XObject << /Im0 10 0 R /Fm0 11 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [8 0 R]] >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\na\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length " + form.Length.ToString(CultureInfo.InvariantCulture) +
                " >>\nstream\n" + form + "\nendstream",
        });
    }

    private static string Stream(string text)
        => "<< /Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "endstream";

    private static AccessibilityAuditReport Audit(byte[] pdf) => PdfAccessibility.Audit(new MemoryStream(pdf));
}
