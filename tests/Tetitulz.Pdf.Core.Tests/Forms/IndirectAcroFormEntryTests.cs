// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <c>PdfAcroForm.SigFlags</c> and <c>PdfAcroForm.NeedAppearances</c> answer the same whether the
/// entry is written directly or as an indirect reference (ISO 32000-2 §7.3.10).
/// </summary>
/// <remarks>
/// <para><b>Both were raw reads in a class whose other read resolves.</b> <c>LoadFields</c>
/// follows an indirect <c>/Fields</c> and says why in a comment; the two public properties above it
/// went through <c>GetInteger</c> and the indexer. An indirect <c>/SigFlags</c> read as 0 — "no
/// signatures, not append-only" — and an indirect <c>/NeedAppearances true</c> as false, the answer
/// that leaves every field without an appearance blank. Base's own readers of the flag
/// (<c>AnnotationFallbackModel.FormNeedsAppearances</c>, <c>PdfFormFiller</c>) already resolved,
/// so the public property disagreed with the library about the same file.</para>
///
/// <para>The direct spelling is the control, as in <c>IndirectFieldFlagsTests</c>.</para>
/// </remarks>
public class IndirectAcroFormEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SigFlagsAndNeedAppearancesAreReadWhicheverWayTheyAreSpelled(bool indirect)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirect), writable: false));

        Assert.NotNull(doc.AcroForm);
        Assert.Equal(3, doc.AcroForm.SigFlags);
        Assert.True(doc.AcroForm.NeedAppearances);
    }

    /// <summary>The other half of the control: a form that says neither reports neither.</summary>
    [Fact]
    public void AFormThatSetsNeitherReportsNeither()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirect: false, omit: true), writable: false));

        Assert.Equal(0, doc.AcroForm.SigFlags);
        Assert.False(doc.AcroForm.NeedAppearances);
    }

    /// <summary>
    /// <c>/SigFlags</c> is a flag word: the low 32 bits of an integer (2³² + 1 is bit 1), nothing for a
    /// real — where the saturating integer read made an out-of-range value every bit set.
    /// </summary>
    [Theory]
    [InlineData("4294967297", 1)]
    [InlineData("3", 3)]
    [InlineData("2.5", 0)]
    public void SigFlagsIsReadAsAFlagWord(string value, int expected)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /SigFlags " + value + " >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 4 >>\nstream\nq Q\nendstream");
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        Assert.Equal(expected, doc.AcroForm.SigFlags);
    }

    // ------------------------------------------------------------------

    private static byte[] Pdf(bool indirect, bool omit = false)
    {
        string entries = omit ? ""
            : indirect ? " /SigFlags 5 0 R /NeedAppearances 6 0 R"
            : " /SigFlags 3 /NeedAppearances true";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields []" + entries + " >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 4 >>\nstream\nq Q\nendstream",
            "3",
            "true");
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
