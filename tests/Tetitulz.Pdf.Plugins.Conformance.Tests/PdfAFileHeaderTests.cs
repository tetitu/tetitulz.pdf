// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The file header, ISO 19005 6.1.2, as the published profile's profiles state it (rules 6.1.2-1 and -2 of the
/// PDF/A-1b, -2b and -4 profiles): A32 for PDF/A-1 to -3's header line, A4-01 for PDF/A-4's, A33 for
/// the comment after it. Each case below is one of the conformance corpus files' first bytes; every one of
/// those files validated clean before the check, because the parser reads past all of them.
/// </summary>
/// <remarks>In the header strings <c>#</c> stands for the byte 0xE2, above 127.</remarks>
public class PdfAFileHeaderTests
{
    [Theory]
    [InlineData(PdfAConformancePart.Part1, "%PDF-1.4\n%####\n")]
    [InlineData(PdfAConformancePart.Part1, "%PDF-1.70\n%####\n")]        // PDF/A-1's pattern is unanchored
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.7\n%####\n")]
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.0\r\n%####\n")]
    [InlineData(PdfAConformancePart.Part3, "%PDF-1.7\r%####\n")]
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.7\n%####af\n")]      // more than four bytes is fine
    [InlineData(PdfAConformancePart.Part4, "%PDF-2.0\n%####\n")]
    [InlineData(PdfAConformancePart.Part4, "%PDF-2.9\n%####\n")]        // 6-1-2-t01-pass-a
    public void AConformingHeaderIsNotReported(PdfAConformancePart part, string header)
    {
        Assert.Empty(HeaderCodes(header, part));
    }

    [Theory]
    [InlineData(PdfAConformancePart.Part1, " %PDF-1.4\n%####\n", "A32")]       // 6-1-2-t01-fail-a: not at byte 0
    [InlineData(PdfAConformancePart.Part1, "%PDF-a.4\n%####\n", "A32")]        // -fail-b
    [InlineData(PdfAConformancePart.Part1, "xPDF-1.4\n%PDF-1.4\n%####\n", "A32")]  // d.d in place, not %PDF-
    [InlineData(PdfAConformancePart.Part2, "%PDF-1,7\n%####\n", "A32")]
    [InlineData(PdfAConformancePart.Part2, "SomeData\n%PDF-1.7\n%####\n", "A32")]
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.70\n%####\n", "A32")]
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.8\n%####\n", "A32")]
    [InlineData(PdfAConformancePart.Part3, "%PDF-1.7   \n%####\n", "A32")]     // not a single EOL
    [InlineData(PdfAConformancePart.Part2, "%PDF-2.0\n%####\n", "A32")]
    [InlineData(PdfAConformancePart.Part4, "%PDF-2.01\n%####\n", "A4-01")]     // parses as 2.1
    [InlineData(PdfAConformancePart.Part4, "%PDF-1.7\n%####\n", "A4-01")]
    [InlineData(PdfAConformancePart.Part4, "%PDF-2.0   \n%####\n", "A4-01")]
    public void AHeaderLineOutOfShapeIsReported(PdfAConformancePart part, string header, string code)
    {
        string[] codes = HeaderCodes(header, part);

        Assert.Contains(code, codes);
        Assert.DoesNotContain(code == "A32" ? "A4-01" : "A32", codes);
    }

    [Theory]
    [InlineData(PdfAConformancePart.Part1, "%PDF-1.4\n%###\n")]          // three bytes
    [InlineData(PdfAConformancePart.Part1, "%PDF-1.4\n\n1 0 obj\n")]     // no comment
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.7\n%a###\n")]         // an ASCII byte among the four
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.7\n\n%####\n")]       // two EOL markers
    [InlineData(PdfAConformancePart.Part4, "%PDF-2.0\n\r%####\n")]       // LF then CR is two
    [InlineData(PdfAConformancePart.Part4, "%PDF-2.0\ntest1\n")]
    [InlineData(PdfAConformancePart.Part2, "%PDF-1.7\nx####\n")]        // four high bytes, no %
    public void AMissingOrShortBinaryCommentIsAnA33(PdfAConformancePart part, string header)
    {
        string[] codes = HeaderCodes(header, part);

        Assert.Contains("A33", codes);
        Assert.DoesNotContain("A32", codes);
        Assert.DoesNotContain("A4-01", codes);
    }

    [Fact]
    public void TheMessageNamesTheHeaderAndTheShapeItMisses()
    {
        var a32 = Validate(Pdf("%PDF-1.8\n%####\n"), PdfAConformancePart.Part2).Single(v => v.Code == "A32");

        Assert.Contains("\"%PDF-1.8\"", a32.Message, StringComparison.Ordinal);
        Assert.Contains("%PDF-1.n, n from 0 to 7", a32.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ fixtures

    private static string[] HeaderCodes(string header, PdfAConformancePart part)
        => Validate(Pdf(header), part).Select(v => v.Code)
            .Where(c => c == "A32" || c == "A33" || c == "A4-01").ToArray();

    private static IReadOnlyList<PdfAViolation> Validate(byte[] pdf, PdfAConformancePart part)
        => PdfAValidator.Validate(new MemoryStream(pdf), part).Violations;

    /// <summary>A one-page document after <paramref name="header"/>, each <c>#</c> in it the byte 0xE2.</summary>
    private static byte[] Pdf(string header)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        };
        var bytes = new List<byte>();
        foreach (char c in header) bytes.Add(c == '#' ? (byte)0xE2 : (byte)c);
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = bytes.Count;
            bytes.AddRange(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n"));
        }
        int xref = bytes.Count;
        var tail = new StringBuilder();
        tail.Append(CultureInfo.InvariantCulture, $"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) tail.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        tail.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        bytes.AddRange(Encoding.ASCII.GetBytes(tail.ToString()));
        return bytes.ToArray();
    }
}
