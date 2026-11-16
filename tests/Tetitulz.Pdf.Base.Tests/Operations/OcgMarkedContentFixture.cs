// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The one-page document the optional-content marked-content tests wrap, shared by the structural
/// checks and the an independent checker oracle.
/// </summary>
/// <remarks>
/// Shared rather than copied because the oracle's whole value is that it judges <b>the same bytes</b>
/// the structural assertions judge. Two fixtures that drifted apart would let an independent checker bless a document
/// no test inspects, which is the failure an external check is supposed to close rather than open.
/// </remarks>
internal static class OcgMarkedContentFixture
{
    /// <summary>A one-page document with one layer, that page's content wrapped in it.</summary>
    internal static byte[] LayeredPageBytes() => LayeredPage().Pdf;

    /// <inheritdoc cref="LayeredPageBytes"/>
    internal static (byte[] Pdf, int OcgNumber) LayeredPage()
    {
        byte[] pdf = OnePagePdf();

        int ocgNum;
        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            ocgNum = PdfOcgManager.CreateLayer(input, output, "Wrapped");
            pdf = output.ToArray();
        }

        return (Wrap(pdf, ocgNum), ocgNum);
    }

    internal static byte[] Wrap(byte[] pdf, int ocgNumber)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        Assert.True(PdfOcgManager.AssignPageContentToLayer(input, output, 0, ocgNumber),
            "the page was not wrapped, so the fixture proves nothing");

        return output.ToArray();
    }

    /// <summary>One page drawing a filled rectangle, so the content has recognisable ink.</summary>
    internal static byte[] OnePagePdf()
    {
        const string content = "0 0 1 rg 100 100 200 300 re f\n";

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << >> >>");
        Obj("<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + content + "endstream");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
