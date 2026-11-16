// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The deep checker reads each page's annotations once, every page's together under
/// <see cref="PdfLimits.MaxAnnotationsPerDocument"/>: pages may name one <c>/Annots</c> array, so
/// what they name between them is pages × entries — 800 pages over one array of 800 links took the
/// audit 798 ms against 46 ms for 200, and two checks each walked them all.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class SharedAnnotsArrayDeepCheckTests
{
    private const int Pages = 20, Links = 10;

    [Fact]
    public void AdversarialPagesSharingOneAnnotsArray_AreReadUpToTheDocumentCeiling_AndSaid()
    {
        byte[] pdf = Document();
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 95;   // not a multiple of a page, so page 10 is cut part-way

            IReadOnlyList<AccessibilityIssue> issues = PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

            Assert.Equal(95, Ua23WithoutStructParent(issues));
            AccessibilityIssue notice = Assert.Single(issues, i => i.Code == "UA99"
                && i.Message.Contains("annotations", StringComparison.Ordinal));
            Assert.Equal(IssueSeverity.Info, notice.Severity);
            Assert.Contains("from page 10 on", notice.Message, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// At a ceiling of exactly the entries the pages name, everything is read and nothing is said:
    /// the checks that each ask for a page's annotations — UA22/UA23 and the parent tree's — read
    /// them once between them, not once each.
    /// </summary>
    [Fact]
    public void AtACeilingOfExactlyTheEntriesNamed_EachIsReadOnceForEveryCheck_AndNothingIsSaid()
    {
        byte[] pdf = Document();
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = Pages * Links;

            IReadOnlyList<AccessibilityIssue> issues = PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

            Assert.Equal(Pages * Links, Ua23WithoutStructParent(issues));
            Assert.DoesNotContain(issues, i => i.Code == "UA99"
                && i.Message.Contains("annotations", StringComparison.Ordinal));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The links UA23 reports for want of a /StructParent, aggregated or one by one.</summary>
    private static int Ua23WithoutStructParent(IReadOnlyList<AccessibilityIssue> issues) =>
        issues.Where(i => i.Code == "UA23" && i.Message.Contains("/StructParent", StringComparison.Ordinal))
              .Sum(i => i.InstanceCount ?? 1);

    /// <summary>
    /// A tagged document of <see cref="Pages"/> pages all naming one /Annots array of
    /// <see cref="Links"/> URI links, none with a /StructParent.
    /// </summary>
    private static byte[] Document()
    {
        var bodies = new List<string>();
        int firstAnnot = 5, firstPage = firstAnnot + Links, content = firstPage + Pages;
        var kids = new StringBuilder();
        for (int p = 0; p < Pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        var refs = new StringBuilder();
        for (int i = 0; i < Links; i++) refs.Append(firstAnnot + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R /Lang (en) >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {Pages} /MediaBox [0 0 612 792] >>");
        bodies.Add($"[{refs}]");
        bodies.Add("<< /Type /StructTreeRoot /ParentTree << /Nums [] >> >>");
        for (int i = 0; i < Links; i++)
            bodies.Add($"<< /Type /Annot /Subtype /Link /Rect [{i} 0 {i + 1} 1] /Contents (link {i}) /A << /S /URI /URI (http://example.com/{i}) >> >>");
        for (int p = 0; p < Pages; p++) bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R /Annots 3 0 R >>");
        bodies.Add("<< /Length 0 >>\nstream\n\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
