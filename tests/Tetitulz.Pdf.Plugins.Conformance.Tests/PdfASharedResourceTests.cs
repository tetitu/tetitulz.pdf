// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The PDF/A checks that read each page's resource dictionaries read a dictionary the pages share once
/// (<see cref="ScanReuse.FirstVisit"/>): pages × entries otherwise, and each such check dedupes its
/// findings, so the skip changes no answer — the count of skips is what shows it happens.
/// </summary>
public class PdfASharedResourceTests
{
    /// <summary>
    /// A PDF/A-2a document of <paramref name="pages"/> pages inheriting one <c>/Resources</c> — a
    /// font, a graphics state, a colour space and an image, so every per-page resource check reads it.
    /// </summary>
    private static byte[] Document(int pages)
    {
        string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                     "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
                     "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"2\" pdfaid:conformance=\"A\"/>" +
                     "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        const string content = "BT /F1 12 Tf 72 700 Td (shared) Tj ET /GS0 gs q 10 0 0 10 0 0 cm /Im0 Do Q";
        int firstPage = 7;
        var kids = new StringBuilder();
        for (int p = 0; p < pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] /Resources 3 0 R >>",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /ExtGState << /GS0 << /Type /ExtGState /CA 1 /TR /Identity >> >>" +
                " /ColorSpace << /CS0 /DeviceRGB >> /XObject << /Im0 5 0 R >> >>",
            "<< /Type /Metadata /Subtype /XML /Length " + Encoding.UTF8.GetByteCount(xmp) + " >>\nstream\n" + xmp + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\na\nendstream",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
        };
        for (int p = 0; p < pages; p++) bodies.Add("<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>");

        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString())); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1)
          .Append(" /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Three pages: each check that runs reads the shared dictionary on page 1 and skips it on pages
    /// 2 and 3 — two skips each; one page, none. The findings are the same either way. Nine checks
    /// run under each part: under PDF/A-2 all but the ICC component count and the transparency
    /// walk, PDF/A-1's alone; under PDF/A-1 those two with the rest that PDF/A-1 runs (A06 under both).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part2, 18)]
    [InlineData(PdfAConformancePart.Part1, 18)]
    public void ASharedResourceDictionary_IsReadOnceByEachCheck(PdfAConformancePart part, long skips)
    {
        var three = PdfAValidator.Validate(new MemoryStream(Document(3)), part);
        long skippedForThree = ScanReuse.ClosedResourceRevisitsSkipped;
        var one = PdfAValidator.Validate(new MemoryStream(Document(1)), part);
        long skippedForOne = ScanReuse.ClosedResourceRevisitsSkipped;

        Assert.Equal(skips, skippedForThree);
        Assert.Equal(0, skippedForOne);
        Assert.Equal(Codes(one), Codes(three));
        Assert.Contains("A56", Codes(three));   // potency: a check found the shared graphics state's /TR
    }

    private static string Codes(PdfAValidationResult result)
    {
        var codes = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (var violation in result.Violations) codes.Add(violation.Code);
        return string.Join(",", codes);
    }
}
