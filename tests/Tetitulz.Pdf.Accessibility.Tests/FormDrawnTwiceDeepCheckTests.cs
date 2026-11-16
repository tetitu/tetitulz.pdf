// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// UA17 counts a Form XObject's text where the page draws it: a form drawn once inside an artifact
/// sequence and once outside has its second drawing's text counted as untagged. The visited set that
/// stops a self-drawing form is per path, not per page — kept per page, the second drawing was
/// skipped whole and its text never reached UA17.
/// </summary>
public class FormDrawnTwiceDeepCheckTests
{
    [Theory]
    [InlineData("/Artifact BMC /Fm0 Do EMC /Fm0 Do", true)]
    [InlineData("/Fm0 Do", true)]
    [InlineData("/Artifact BMC /Fm0 Do EMC", false)]
    public void AFormsText_IsUntaggedWhereItIsDrawnOutsideAnArtifact(string pageContent, bool reported)
    {
        IReadOnlyList<AccessibilityIssue> issues = PdfDeepAccessibilityChecker.Check(new MemoryStream(Pdf(pageContent)));

        Assert.Equal(reported, issues.Any(i => string.Equals(i.Code, "UA17", StringComparison.Ordinal)));
    }

    private static byte[] Pdf(string pageContent)
    {
        const string formContent = "BT /F1 12 Tf 72 700 Td (x) Tj ET";
        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 /Tabs /S "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(pageContent),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Length "
                + formContent.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + formContent + "\nendstream",
            "<< /Type /StructTreeRoot /K [] /ParentTree << /Nums [] >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
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
