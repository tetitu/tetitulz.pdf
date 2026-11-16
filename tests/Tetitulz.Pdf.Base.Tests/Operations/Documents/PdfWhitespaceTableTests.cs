// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Two structural scanners classified §7.2.2 Table 1 white space by hand and each dropped members
/// of it — so both refused input the ordinary parser accepts.
/// </summary>
/// <remarks>
/// <para>Table 1 lists six bytes: NUL, TAB, LF, FF, CR and SPACE. <c>PdfRepair.IsObjWhitespace</c>
/// omitted <b>NUL</b>; <c>PdfRevisions</c>' inline test accepted only CR, LF, SPACE and TAB,
/// omitting both NUL and <b>form feed</b>.</para>
///
/// <para><b>Both failures are silent and subtractive</b>, which is what makes them worth a test
/// apiece: the repaired file simply has one fewer object, and the revision list simply has one fewer
/// revision. Nothing throws, and the output is well formed.</para>
/// </remarks>
public class PdfWhitespaceTableTests
{
    /// <summary>
    /// <b>The repair defect.</b> A NUL pad byte before an object header made repair drop the object
    /// — while <c>PdfTokenizer</c>, which uses the shared classifier, reads the same bytes happily.
    /// The recovery path was stricter than the ordinary parser.
    /// </summary>
    [Theory]
    [InlineData("\0")]     // the missing one
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\f")]
    public void AnObjectHeaderPrecededByAnyTableOneByteIsRecovered(string pad)
    {
        byte[] repaired = Repair(DamagedPdf(padBeforeObject4: pad));

        Assert.Contains("(recovered)", Encoding.Latin1.GetString(repaired), StringComparison.Ordinal);
    }

    /// <summary>
    /// The floor: a byte that is <b>not</b> Table 1 white space must still disqualify the header, or
    /// the fix would be "accept anything" and the scan would start finding object headers inside
    /// strings and streams.
    /// </summary>
    /// <remarks>
    /// <b>A digit is deliberately not among these.</b> Padding with <c>"9"</c> makes the header read
    /// <c>94 0 obj</c>, so the object is still recovered — as object 94. That is the scan working
    /// correctly, not the guard failing, and asserting non-recovery for it was simply wrong: the
    /// first version of this theory included <c>"9"</c> and failed for that reason.
    /// </remarks>
    [Theory]
    [InlineData("x")]
    [InlineData("/")]
    [InlineData(">")]
    [InlineData(")")]
    public void AnObjectHeaderPrecededByANonWhitespaceByteIsNotRecovered(string pad)
    {
        byte[] repaired = Repair(DamagedPdf(padBeforeObject4: pad));

        Assert.DoesNotContain("(recovered)", Encoding.Latin1.GetString(repaired), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The revision defect.</b> A form feed between <c>startxref</c> and its offset made the
    /// boundary go uncounted, so the public revision list under-reported the file.
    /// </summary>
    [Theory]
    [InlineData("\r\n")]   // the ordinary spelling
    [InlineData(" ")]
    [InlineData("\f")]     // the missing one
    [InlineData("\0")]     // the other missing one
    public void ARevisionBoundaryIsCountedWhateverTableOneByteSeparatesTheOffset(string separator)
    {
        byte[] pdf = TwoRevisionPdf(separator);

        using var ms = new MemoryStream(pdf);
        var revisions = PdfRevisions.List(ms);

        Assert.Equal(2, revisions.Count);
    }

    // ------------------------------------------------------------------

    private static byte[] Repair(byte[] damaged)
    {
        using var input = new MemoryStream(damaged);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// A document with no xref, whose object 4 is preceded by the caller's pad byte. Object 4 is the
    /// only carrier of the marker text, so its presence in the output is exactly "was it recovered".
    /// </summary>
    private static byte[] DamagedPdf(string padBeforeObject4)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        sb.Append(padBeforeObject4);
        sb.Append("4 0 obj\n<< /Length 14 >>\nstream\n((recovered))\nendstream\nendobj\n");
        sb.Append("%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A base document plus one appended incremental revision, whose <c>startxref</c> offset is
    /// separated from the keyword by <paramref name="separator"/>.
    /// </summary>
    private static byte[] TwoRevisionPdf(string separator)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length; sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        int xref1 = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3 }) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n").Append(xref1).Append("\n%%EOF\n");

        // The appended revision: one superseded object, its own xref and trailer.
        int o3b = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>\nendobj\n");

        int xref2 = sb.Length;
        sb.Append("xref\n0 1\n0000000000 65535 f \n3 1\n");
        sb.AppendFormat("{0:D10} 00000 n \n", o3b);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R /Prev ").Append(xref1).Append(" >>\n");
        sb.Append("startxref").Append(separator).Append(xref2).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
