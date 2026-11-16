// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>The one document every writer check runs over, for any test that wants the same input.</summary>
internal static class WriterCheckFixture
{
    /// <summary>
    /// The same document as an independent producer wrote it with object streams and a cross-reference
    /// stream (recorded under <c>Fixtures/WriterCheck</c>), so a writer is also handed the other file layout.
    /// </summary>
    internal static byte[] ObjectStreamsLayout() =>
        System.IO.File.ReadAllBytes(System.IO.Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "WriterCheck", "fixture-object-streams.pdf"));

    /// <summary>
    /// Three pages of text and colour, an AcroForm text field with a widget on page 1, a link, an
    /// outline of two entries, an /Info dictionary and an /ID.
    /// </summary>
    internal static byte[] Fixture()
    {
        static string Page(int n) =>
            "BT /F1 18 Tf 72 700 Td (Page " + n.ToString(CultureInfo.InvariantCulture) + " heading) Tj ET\n" +
            "0 0 1 rg 72 600 200 40 re f\n" +
            "BT /F1 11 Tf 72 560 Td (Body text on page " + n.ToString(CultureInfo.InvariantCulture) + ".) Tj ET\n";

        var bodies = new List<string>
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 3 0 R >> >> >> /Outlines 11 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>",
            /* 3 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            /* 4 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 3 0 R >> >> /Annots [10 0 R 14 0 R] >>",
            /* 5 */ Stream(Page(1)),
            /* 6 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources << /Font << /F1 3 0 R >> >> >>",
            /* 7 */ Stream(Page(2)),
            /* 8 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 9 0 R /Resources << /Font << /F1 3 0 R >> >> >>",
            /* 9 */ Stream(Page(3)),
            /* 10 */ "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Initial) /Rect [72 500 272 520] /P 4 0 R /DA (/Helv 11 Tf 0 g) /F 4 >>",
            /* 11 */ "<< /Type /Outlines /First 12 0 R /Last 13 0 R /Count 2 >>",
            /* 12 */ "<< /Title (One) /Parent 11 0 R /Next 13 0 R /Dest [4 0 R /Fit] >>",
            /* 13 */ "<< /Title (Two) /Parent 11 0 R /Prev 12 0 R /Dest [6 0 R /Fit] >>",
            /* 14 */ "<< /Type /Annot /Subtype /Link /Rect [72 440 200 460] /Border [0 0 0] /A << /S /URI /URI (https://example.org/) >> >>",
            /* 15 */ "<< /Title (Writer check) /Producer (fixture) >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R /Info 15 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\n")
          .Append("startxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + data + "\nendstream";
}
