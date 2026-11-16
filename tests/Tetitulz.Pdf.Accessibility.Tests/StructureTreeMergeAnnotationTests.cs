// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Merging into an existing tree leaves the annotations that tree already owns alone: one whose
/// <c>/StructParent</c> is a key the parent tree defines is reached by an existing element's
/// <c>/OBJR</c>, and wrapping it again would give it two parents and a second key.
/// </summary>
public class StructureTreeMergeAnnotationTests
{
    private const int LinkAnnotationNumber = 9;

    [Fact]
    public void MergingLeavesAnAnnotationTheExistingTreeOwns_ToItsOwner()
    {
        byte[] pdf = HalfTaggedPdfWithOwnedLink();

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output, new AutoTagOptions { MergeIntoExistingTree = true });
        byte[] tagged = output.ToArray();

        Assert.True(result.MergedIntoExistingTree);
        Assert.True(result.ContentMarked, "page 2 was not tagged, so nothing was merged");

        // The annotation keeps the key its owner gave it...
        using var doc = PdfDocument.Open(new MemoryStream(tagged, writable: false));
        var link = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(new PdfReference(LinkAnnotationNumber, 0)));
        Assert.Equal(1, Assert.IsType<PdfNumber>(doc.ResolveObject(link[PdfName.StructParent])).IntValue);

        // ...and the revision this pass appended writes no second /OBJR for it.
        string appended = Encoding.Latin1.GetString(tagged, pdf.Length, tagged.Length - pdf.Length);
        Assert.DoesNotContain("/Obj 9 0 R", appended, StringComparison.Ordinal);
        Assert.Contains("/Obj 9 0 R", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);   // the owner's, in the original
    }

    /// <summary>
    /// Page 1: a paragraph (MCID 0) and a Link element wrapping annotation 9 through an /OBJR, the
    /// parent tree defining keys 0 and 1. Page 2: untagged text, which the merge tags.
    /// </summary>
    private static byte[] HalfTaggedPdfWithOwnedLink()
    {
        const string content1 = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (See the report.) Tj ET EMC";
        const string content2 = "BT /F1 12 Tf 72 700 Td (Page two) Tj ET";
        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R 10 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 /Tabs /S /Annots [9 0 R] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(content1),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] /ParentTree 12 0 R /ParentTreeNextKey 2 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /Link /Alt (The annual report) /P 6 0 R /Pg 3 0 R /K << /Type /OBJR /Pg 3 0 R /Obj 9 0 R >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 696 200 712] /Border [0 0 0] /StructParent 1 "
                + "/Contents (The annual report) /A << /S /URI /URI (https://example.com/) >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Tabs /S /Resources << /Font << /F1 5 0 R >> >> /Contents 11 0 R >>",
            StreamObject(content2),
            "<< /Nums [0 [7 0 R] 1 8 0 R] >>",
        });
    }

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
