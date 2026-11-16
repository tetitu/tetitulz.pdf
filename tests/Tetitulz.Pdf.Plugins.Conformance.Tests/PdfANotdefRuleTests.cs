// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A86 — no reference to the <c>.notdef</c> glyph from any text-showing operator, in any render mode
/// (ISO 19005-2/3 6.2.11.8, 19005-4 6.2.10.9; the published profile's rules of those numbers); PDF/A-1 has no such
/// clause. Which codes reference it is <c>Core.Fonts.NotdefReferences</c>'s answer, tested in Core;
/// the published profile's nine fail files and 551 pass files are the wider check, run by the corpus probe.
/// </summary>
public class PdfANotdefRuleTests
{
    /// <summary>
    /// WinAnsi's code 0 names no glyph: shown in invisible mode 3 it is still a reference, reported for
    /// PDF/A-2 and -4 under each one's clause and not for PDF/A-1; a named code is not one.
    /// </summary>
    [Fact]
    public void A86_AnUnnamedCodeShownInvisibly_IsAReference_FromPdfA2On()
    {
        byte[] pdf = Document("BT /F1 12 Tf 3 Tr 10 50 Td <0041> Tj ET");
        string part2 = A86(pdf, PdfAConformancePart.Part2);
        Assert.NotNull(part2);
        Assert.Contains("Helvetica: code 0 is named by no entry of the font's encoding", part2, System.StringComparison.Ordinal);
        Assert.DoesNotContain("code 65", part2, System.StringComparison.Ordinal);
        Assert.Contains("ISO 19005-2 6.2.11.8", part2, System.StringComparison.Ordinal);
        Assert.Contains("ISO 19005-4 6.2.10.9", A86(pdf, PdfAConformancePart.Part4), System.StringComparison.Ordinal);
        Assert.Contains("ISO 19005-3 6.2.11.8", A86(pdf, PdfAConformancePart.Part3), System.StringComparison.Ordinal);
        Assert.Null(A86(pdf, PdfAConformancePart.Part1));

        Assert.Null(A86(Document("BT /F1 12 Tf 10 50 Td (AB) Tj ET"), PdfAConformancePart.Part2));
    }

    /// <summary>A <c>TJ</c> array's strings are read too, and one reference is listed once however often it is shown.</summary>
    [Fact]
    public void A86_ATjArraysStrings_AndOneEntryPerReference()
    {
        string message = A86(Document("BT /F1 12 Tf 10 50 Td [(A) -20 <01> 10 <01>] TJ ET"), PdfAConformancePart.Part2)!;
        Assert.Contains("code 1 is named by no entry", message, System.StringComparison.Ordinal);
        Assert.Equal(1, Count(message, "code 1 "));
    }

    /// <summary>
    /// A form XObject drawing in the font its caller selected shows text this walk cannot attribute: it
    /// is passed over, not taken as the whole check cut short — the page's own reference is still found
    /// and no A99 is raised for A86.
    /// </summary>
    [Fact]
    public void A86_AFormThatInheritsItsFont_IsPassedOver()
    {
        var result = PdfAValidator.Validate(new MemoryStream(Document("BT /F1 12 Tf 10 50 Td <00> Tj /Fm1 Do ET",
            "/XObject << /Fm1 6 0 R >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Length 13 >>\nstream\nBT <01> Tj ET\nendstream")),
            PdfAConformancePart.Part2);
        string message = Assert.Single(result.Violations, v => v.Code == "A86").Message;
        Assert.Contains("code 0 ", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("code 1 ", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain(result.Informational, i => i.Message.Contains("A86", System.StringComparison.Ordinal));
    }

    private static int Count(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, System.StringComparison.Ordinal)) n++;
        return n;
    }

    private static string? A86(byte[] pdf, PdfAConformancePart part)
        => PdfAValidator.Validate(new MemoryStream(pdf), part).Violations.FirstOrDefault(v => v.Code == "A86")?.Message;

    private static byte[] Document(string content, string moreResources = "", params string[] extra)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> " + moreResources + " >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        objects.AddRange(extra);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
