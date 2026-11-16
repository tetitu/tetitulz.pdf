// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// UA32 — ISO 14289-1 7.18.4 and 7.18.5: a widget annotation is nested in a Form element and a link
/// annotation in a Link element, judged by what the element holding the annotation's /OBJR means
/// (its role through /RoleMap). The published profile's 7.18.4-1 and 7.18.5-1.
/// </summary>
public class AnnotationNestingTests
{
    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>(objectBodies.Count);
        for (int i = 0; i < objectBodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(objectBodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objectBodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// One annotation (object 10) whose /OBJR (object 11) is held by element 8 of type
    /// <paramref name="ownerRole"/>, under a Document element.
    /// </summary>
    private static byte[] AnnotationPdf(string ownerRole, string subtype, string annotationExtra = "", string roleMap = "",
        string secondOwnerRole = "")
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) "
            + "/ViewerPreferences << /DisplayDocTitle true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [10 0 R] /Tabs /S >>",
            StreamObject("/Artifact BMC\n0.9 g 72 100 200 200 re f\nEMC\n"),
            "<< >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1"
            + (roleMap.Length > 0 ? " /RoleMap << " + roleMap + " >>" : "") + " >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R" + (secondOwnerRole.Length > 0 ? " 12 0 R" : "") + "] >>",
            "<< /Type /StructElem /S /" + ownerRole + " /P 7 0 R /Pg 3 0 R /K [11 0 R] /Alt (Annotation) >>",
            "<< /Nums [0 8 0 R] >>",
            "<< /Type /Annot /Subtype /" + subtype + " /Rect [72 700 260 720] /StructParent 0 /Border [0 0 0] "
            + "/Contents (Annotation) /TU (Annotation) " + annotationExtra + " >>",
            "<< /Type /OBJR /Pg 3 0 R /Obj 10 0 R >>",
        }.Concat(secondOwnerRole.Length > 0
            ? new[] { "<< /Type /StructElem /S /" + secondOwnerRole + " /P 7 0 R /Pg 3 0 R /K [11 0 R] >>" }
            : Array.Empty<string>()).ToList());

    private static List<AccessibilityIssue> Ua32(byte[] pdf)
        => PdfAccessibility.Audit(new MemoryStream(pdf)).Issues
            .Where(i => string.Equals(i.Code, "UA32", StringComparison.Ordinal)).ToList();

    [Theory]
    [InlineData("P", "Link", "Link annotation is nested in a <P> element, not in a <Link> element")]
    [InlineData("Div", "Widget", "Widget annotation is nested in a <Div> element, not in a <Form> element")]
    [InlineData("Link", "Widget", "Widget annotation is nested in a <Link> element, not in a <Form> element")]
    public void AWidgetOrLinkInTheWrongElement_IsAnError(string ownerRole, string subtype, string message)
    {
        AccessibilityIssue issue = Assert.Single(Ua32(AnnotationPdf(ownerRole, subtype)));
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains(message, issue.Message, StringComparison.Ordinal);
        Assert.Equal(10, issue.ObjectNumber);
    }

    /// <summary>
    /// The silent twins: the right element; the right element reached through /RoleMap; a hidden
    /// annotation (presented to nobody, so out of scope); and a subtype neither clause names.
    /// </summary>
    [Theory]
    [InlineData("Link", "Link", "", "")]
    [InlineData("Form", "Widget", "", "")]
    [InlineData("Hyperlink", "Link", "", "/Hyperlink /Link")]
    [InlineData("P", "Link", "/F 2", "")]
    [InlineData("P", "Square", "", "")]
    public void AWidgetOrLinkInItsOwnElement_OrOutOfScope_IsQuiet(string ownerRole, string subtype, string annotationExtra, string roleMap)
    {
        Assert.Empty(Ua32(AnnotationPdf(ownerRole, subtype, annotationExtra, roleMap)));
        // Potency: the same file with the annotation moved under a <Sect> is reported, unless the
        // annotation is out of scope by its flags or subtype.
        bool inScope = annotationExtra.Length == 0 && subtype != "Square";
        Assert.Equal(inScope ? 1 : 0, Ua32(AnnotationPdf("Sect", subtype, annotationExtra, roleMap)).Count);
    }

    /// <summary>
    /// An /OBJR held by two elements (itself a broken tree) is judged by the first in document order.
    /// </summary>
    [Fact]
    public void AnObjrHeldTwice_IsJudgedByTheFirstHolder()
    {
        Assert.Empty(Ua32(AnnotationPdf("Link", "Link", secondOwnerRole: "P")));
        Assert.Single(Ua32(AnnotationPdf("P", "Link", secondOwnerRole: "Link")));
    }
}
