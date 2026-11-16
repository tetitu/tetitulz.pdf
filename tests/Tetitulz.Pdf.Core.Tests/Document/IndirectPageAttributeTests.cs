// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <c>/Rotate</c> and <c>/UserUnit</c> written as indirect references.
///
/// <para><see cref="PdfDictionary.GetInteger"/> and <see cref="PdfDictionary.GetDouble"/> test
/// <c>v is PdfNumber</c> and never follow a <see cref="PdfReference"/>, so a producer that shares
/// one rotation object across every page of a batch scan — <c>/Rotate 12 0 R</c> — read back as
/// "no rotation" and the page rendered, printed and extracted sideways with nothing reported. The
/// five page boxes in the same file each handled the indirect case explicitly, which is what shows
/// that producers really do write these values that way.</para>
/// </summary>
public class IndirectPageAttributeTests
{
    [Fact]
    public void Rotate_WrittenAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            BuildPage("/Rotate 12 0 R /UserUnit 13 0 R")));

        Assert.Equal(90, doc.Pages[0].Rotate);
    }

    [Fact]
    public void UserUnit_WrittenAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            BuildPage("/Rotate 12 0 R /UserUnit 13 0 R")));

        Assert.Equal(2.5d, doc.Pages[0].UserUnit, 6);
    }

    /// <summary>The direct spelling keeps working — the indirect path is a fallback, not a swap.</summary>
    [Fact]
    public void Rotate_AndUserUnit_WrittenDirectly_AreStillRead()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            BuildPage("/Rotate 270 /UserUnit 4.0")));

        Assert.Equal(270, doc.Pages[0].Rotate);
        Assert.Equal(4.0d, doc.Pages[0].UserUnit, 6);
    }

    /// <summary>
    /// The honest negative: absent means absent. Without it, returning a constant 90 / 2.5 would
    /// satisfy the two tests above.
    /// </summary>
    [Fact]
    public void Rotate_AndUserUnit_Absent_KeepTheirDefaults()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(string.Empty)));

        Assert.Null(doc.Pages[0].Rotate);
        Assert.Equal(1.0d, doc.Pages[0].UserUnit, 6);
    }

    /// <summary>
    /// A one-page file whose page dictionary carries <paramref name="pageExtras"/>. Objects 12
    /// and 13 hold <c>90</c> and <c>2.5</c>, the shape a batch scanner produces when every page
    /// shares one rotation object.
    /// </summary>
    private static byte[] BuildPage(string pageExtras)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.6\n");
        long o1 = file.Position; Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = file.Position; Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        long o3 = file.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " + pageExtras + " >>\nendobj\n");
        long o12 = file.Position; Write("12 0 obj\n90\nendobj\n");
        long o13 = file.Position; Write("13 0 obj\n2.5\nendobj\n");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 4\n0000000000 65535 f \n");
        xs.Append(o1.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o2.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o3.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("12 2\n");
        xs.Append(o12.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o13.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size 14 /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }
}
