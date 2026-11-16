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
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A Form XObject is tagged where the page draws it as content. Drawn inside an existing
/// <c>/Artifact</c> sequence it is artifact content (§14.8.2.2): neither tagged nor reported as
/// untagged. Drawn inside another existing sequence it belongs to that sequence's owner (§14.7.5.1
/// admits no structure content item inside another): not tagged, and said. Drawn outside every
/// sequence it is tagged — the control.
/// </summary>
public class FormInsideMarkedSequenceTaggingTests
{
    private const int FormObjectNumber = 5;

    [Theory]
    [InlineData("/Artifact <</Type /Pagination /Subtype /Watermark>> BDC /Fm0 Do EMC BT /F1 12 Tf 72 700 Td (Body) Tj ET", false, false)]
    [InlineData("/P <</MCID 0>> BDC /Fm0 Do EMC BT /F1 12 Tf 72 600 Td (Body) Tj ET", false, true)]
    [InlineData("/Fm0 Do BT /F1 12 Tf 72 700 Td (Body) Tj ET", true, false)]
    public void AFormIsTaggedOnlyWhereThePageDrawsItAsContent(string pageContent, bool tagged, bool reportedAsOwned)
    {
        byte[] pdf = Pdf(pageContent);

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output, new AutoTagOptions());

        Assert.Equal(tagged ? 1 : 0, result.FormsTagged);
        Assert.Equal(reportedAsOwned, result.Warnings.Any(w => w.Contains("belongs to that sequence's owner", StringComparison.Ordinal)));
        if (!tagged && !reportedAsOwned)
            Assert.DoesNotContain(result.Warnings, w => w.Contains("partially tagged", StringComparison.Ordinal));

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
        var form = Assert.IsType<PdfStream>(doc.ResolveObject(new PdfReference(FormObjectNumber, 0)));
        bool formMarked = Encoding.ASCII.GetString(form.GetDecodedData()).Contains("/MCID", StringComparison.Ordinal);
        Assert.Equal(tagged, formMarked);
        Assert.Equal(tagged, form.ContainsKey(PdfName.StructParents));
    }

    private static byte[] Pdf(string pageContent)
    {
        const string formContent = "BT /F1 48 Tf 100 400 Td (CONFIDENTIAL) Tj ET";
        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 6 0 R >> /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(pageContent),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 6 0 R >> >> /Length "
                + formContent.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + formContent + "\nendstream",
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
