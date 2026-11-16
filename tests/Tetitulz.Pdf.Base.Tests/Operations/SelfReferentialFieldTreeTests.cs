// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A field tree whose <c>/Kids</c> names the same object twice must be walked once, not
/// 2<sup>depth</sup> times.
/// </summary>
/// <remarks>
/// <para>
/// <b>A depth cap is not a cycle guard, and this is the shape that hides the difference.</b>
/// <c>PdfUsageRightsReader.CollectSignatureFields</c> bounded recursion at
/// <c>PdfLimits.MaxTreeDepth</c> (100) and carried a comment saying that stopped a self-referential
/// tree from overflowing the stack. It did — and left an unbounded-*time* walk behind it. With no
/// record of what had been visited, a node reached twice doubles the work at every level, so the cap
/// of 100 permits 2<sup>100</sup> calls. The document below is under 400 bytes and the call never
/// returned, never threw, and pinned a core at 100%.
/// </para>
/// <para>
/// The assertion is on the guard's own evidence — the call returns, and returns the one entry the
/// document actually declares — never on elapsed time. A DoS-guard test that passes by finishing
/// quickly has quite possibly not run at all.
/// </para>
/// </remarks>
public class SelfReferentialFieldTreeTests
{
    [Fact]
    public void Read_FieldWhoseKidsNameItselfTwice_TerminatesInsteadOfDoublingPerLevel()
    {
        using var input = new MemoryStream(SelfReferentialSignatureFieldPdf());

        IReadOnlyList<UsageRightsEntry> entries = PdfUsageRightsReader.Read(input);

        // The document declares no UR3 transform, so the honest answer is "none" — reached by
        // walking the cycle once. Before the visited set this line was never evaluated.
        Assert.Empty(entries);
        Assert.Null(PdfUsageRightsReader.LastError);
    }

    /// <summary>
    /// Two distinct fields that both name a third makes the same node reachable twice without any
    /// self-reference — a diamond, which is legal and must still be walked, just not repeatedly.
    /// </summary>
    [Fact]
    public void Read_DiamondFieldTree_TerminatesAndStillReadsTheDocument()
    {
        using var input = new MemoryStream(DiamondFieldTreePdf());

        IReadOnlyList<UsageRightsEntry> entries = PdfUsageRightsReader.Read(input);

        Assert.Empty(entries);
        Assert.Null(PdfUsageRightsReader.LastError);
    }

    /// <summary><c>6 0 obj</c> is a signature field whose <c>/Kids</c> holds two references to itself.</summary>
    private static byte[] SelfReferentialSignatureFieldPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObject("", "BT ET\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /FT /Sig /T (sig) /Kids [6 0 R 6 0 R] >>",
    });

    /// <summary>Two fields whose <c>/Kids</c> both reach object 8, which in turn cycles back.</summary>
    private static byte[] DiamondFieldTreePdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObject("", "BT ET\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /FT /Sig /T (a) /Kids [8 0 R] >>",
        "<< /FT /Sig /T (b) /Kids [8 0 R] >>",
        "<< /FT /Sig /T (shared) /Kids [6 0 R 7 0 R] >>",
    });

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");

        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";
}
