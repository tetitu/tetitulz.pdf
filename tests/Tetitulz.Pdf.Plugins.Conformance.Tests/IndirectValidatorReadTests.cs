// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A PDF/A violation must still be reported when the key that identifies it is written as an
/// <b>indirect reference</b>.
/// </summary>
/// <remarks>
/// <para><b>In a checker the null direction is the whole question, and both of these fell the wrong
/// way.</b> <c>/S</c> on an <c>/OpenAction</c> was read with <c>GetName</c>, and the condition
/// requires a non-null type — so an unresolved <c>/S</c> meant <b>A05 never fired</b> and a document
/// carrying a JavaScript <c>/OpenAction</c> was reported <i>conforming</i>. (A05 is retired: the
/// action-type rule is A02 wherever the action sits, and it reads <c>/S</c> resolved.) <c>/Subtype</c> on a font
/// was worse: <c>if (subtype == null) return;</c> abandons the <b>whole</b> embedding check, so an
/// unembedded font went unreported and <b>A06 silently passed</b>.</para>
///
/// <para>§7.3.10 permits both spellings, and the party that chooses them is the party being
/// validated. A conformance tool that can be made to pass a non-conforming file by moving a name
/// into its own object is worse than no tool, because its output is relied on.</para>
///
/// <para>Adjacency found these too: <c>/OpenAction</c> and the action dictionary are both resolved
/// on the two lines directly above the raw <c>/S</c>.</para>
///
/// <para>Asserted on the violation list, which is the artefact a caller acts on. "Validate returned
/// a result" is true in both failures.</para>
/// </remarks>
public class IndirectValidatorReadTests
{
    /// <summary>
    /// A JavaScript <c>/OpenAction</c> is reported as A02 whichever way its <c>/S</c> is spelled.
    /// The direct spelling is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /S 9 0 R
    [InlineData(false)]  // /S /JavaScript
    public void JavaScriptOpenAction_IsReportedAsA02(bool indirect)
    {
        using var ms = new MemoryStream(JavaScriptOpenActionPdf(indirect));
        var result = PdfAValidator.Validate(ms);

        Assert.Contains(result.Violations, v => v.Code == "A02" && v.Message.Contains("/JavaScript action"));
    }

    /// <summary>
    /// The floor: a document with no <c>/OpenAction</c> produces no A02, so the assertion above
    /// cannot be met by a validator that reports A02 unconditionally.
    /// </summary>
    [Fact]
    public void NoOpenAction_ProducesNoA02()
    {
        using var ms = new MemoryStream(JavaScriptOpenActionPdf(indirect: false, withAction: false));
        var result = PdfAValidator.Validate(ms);

        Assert.DoesNotContain(result.Violations, v => v.Code == "A02");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A minimal document whose catalog carries a JavaScript <c>/OpenAction</c>. Object 9 holds the
    /// action type name when <paramref name="indirect"/> — the only difference between the two.
    /// </summary>
    private static byte[] JavaScriptOpenActionPdf(bool indirect, bool withAction = true)
    {
        string s = indirect ? "9 0 R" : "/JavaScript";
        string open = withAction ? " /OpenAction 6 0 R" : "";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R" + open + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            StreamObj("q Q\n"),
            "null",
            "<< /Type /Action /S " + s + " /JS (app.alert\\(1\\)) >>",
            "null",
            "null",
            "/JavaScript");
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
