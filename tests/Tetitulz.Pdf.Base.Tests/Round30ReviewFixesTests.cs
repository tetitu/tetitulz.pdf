// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Regression tests for the code-review remediation round 30.
/// Focused on the concrete output-corruption fixes whose pre-fix behaviour was
/// to silently emit a truncated/empty document.
/// </summary>
public class Round30ReviewFixesTests
{
    // §68 H11 — PdfOcgManager pass-through paths did `ms.CopyTo(output)` without
    // resetting ms.Position, so a document with no matching layer (the common
    // no-op case) produced a truncated/empty output. Verify the output is a
    // full, parseable copy.
    [Fact]
    public void SetLayerVisibility_NoMatchingLayer_ReturnsParseableCopy()
    {
        byte[] pdf = BuildPdfMinimal("0.9 0.9 0.9 rg 0 0 612 792 re f");
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        int changed = PdfOcgManager.SetLayerVisibility(input, output, "no-such-layer", true);

        Assert.Equal(0, changed);                  // nothing matched
        byte[] outBytes = output.ToArray();
        Assert.True(outBytes.Length >= pdf.Length); // not truncated
        using var roundTrip = new MemoryStream(outBytes);
        using var doc = PdfDocument.Open(roundTrip);
        Assert.Single(doc.Pages);          // still a valid 1-page PDF
    }

    // §68 H10 — PdfNamedDestinations.RemoveDestination had the same pass-through
    // bug: removing a non-existent destination truncated the output.
    [Fact]
    public void RemoveDestination_NoSuchName_ReturnsParseableCopy()
    {
        byte[] pdf = BuildPdfMinimal("0.9 0.9 0.9 rg 0 0 612 792 re f");
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        bool removed = PdfNamedDestinations.RemoveDestination(input, output, "no-such-dest");

        Assert.False(removed);
        byte[] outBytes = output.ToArray();
        Assert.True(outBytes.Length >= pdf.Length);
        using var roundTrip = new MemoryStream(outBytes);
        using var doc = PdfDocument.Open(roundTrip);
        Assert.Single(doc.Pages);
    }

    private static byte[] BuildPdfMinimal(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
