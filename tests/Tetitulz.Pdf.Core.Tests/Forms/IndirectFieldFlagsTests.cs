// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// A field whose <c>/Ff</c> is written as an <b>indirect reference</b> must still report its flags.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>GetInheritedInteger</c> called <c>PdfDictionary.GetInteger</c>, which
/// tests <c>v is PdfNumber</c> against the <i>raw</i> stored value and does not follow a reference
/// — §7.3.10 permits one anywhere. So <c>/Ff 5 0 R</c> read as <b>absent</b>.</para>
///
/// <para><b>Absent is not neutral for a bit field.</b> <c>PdfFormField.Flags</c> coalesces a missing
/// <c>/Ff</c> to <c>0</c>, which is indistinguishable from "the file said no flags are set". Every
/// field-behaviour decision then went the wrong way: a radio group looked like an ordinary check box
/// (<c>Radio</c>, bit 16), a password field like plain text (bit 14), a multiline field like a single
/// line (bit 13).</para>
///
/// <para><b>Why this one outlived its siblings.</b> The same round fixed the unresolved
/// <c>/Kids</c>, <c>/T</c> and <c>/FT</c> reads in this class — the shapes a structural walk
/// notices, because they change which fields exist. <c>/Ff</c> is an integer: the field list is
/// identical either way and only the <i>behaviour</i> is wrong, so nothing structural pointed at
/// it.</para>
/// </remarks>
public class IndirectFieldFlagsTests
{
    /// <summary>
    /// The two spellings of the same radio group must agree. The direct one is the control: it
    /// always worked, so a fixture built on it could never have gone red.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Ff 7 0 R
    [InlineData(false)]  // /Ff 32768
    public void RadioFlag_IsReadWhicheverWayFfIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(RadioGroupPdf(indirect));
        using var doc = PdfDocument.Open(ms);

        var field = Assert.Single(doc.AcroForm.Fields);

        Assert.Equal(PdfFieldFlags.Radio, field.Flags & PdfFieldFlags.Radio);
    }

    /// <summary>
    /// The control's other half: a field that really has no <c>/Ff</c> reports no flags. Without it,
    /// "the flag is set" could be satisfied by an implementation that returned a constant, and the
    /// zero case is the one the defect was impersonating.
    /// </summary>
    [Fact]
    public void NoFf_ReportsNoFlags()
    {
        using var ms = new MemoryStream(RadioGroupPdf(indirect: false, omitFf: true));
        using var doc = PdfDocument.Open(ms);

        var field = Assert.Single(doc.AcroForm.Fields);

        Assert.Equal(0, field.Flags);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// One button field carrying the <c>Radio</c> bit (1 &lt;&lt; 15 = 32768), written directly or
    /// as object 7.
    /// </summary>
    private static byte[] RadioGroupPdf(bool indirect, bool omitFf = false)
    {
        string ff = omitFf ? "" : (indirect ? " /Ff 7 0 R" : " /Ff 32768");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
            StreamObj("q Q\n"),
            "<< /FT /Btn /T (choice)" + ff + " >>",
            "null",
            "32768");
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

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
