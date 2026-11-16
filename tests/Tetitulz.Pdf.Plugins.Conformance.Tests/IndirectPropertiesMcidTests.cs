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
/// An MCID reached through a named <c>/Properties</c> entry must count as <b>defined</b>, whichever
/// way the entry's <c>/MCID</c> is spelled.
/// </summary>
/// <remarks>
/// <para><b>UA50 is invented for a conforming file when this read fails.</b> The MCID is simply not
/// added to the <c>defined</c> set, and <c>CheckNamedMcidsExist</c> then reports a structure
/// element as pointing at marked content the page never defines. The document is correct; the
/// checker is wrong — the worst direction for a conformance tool, because its output is what a
/// caller acts on.</para>
///
/// <para><b>The inline-dictionary exemption does not cover this site.</b> A <c>BDC</c> operand
/// written inline in the content stream cannot hold an indirect reference — a content stream has no
/// syntax for one — which is why the sibling read of the inline operand is legitimately raw. But
/// this <c>entry</c> is a <c>/Properties</c> entry in the <b>page resources</b>: a real document
/// object, free to hold indirect values. The two lines above it already resolve, for exactly that
/// reason.</para>
///
/// <para>Asserted on the absence of the UA50 code with a floor either side: a structure element
/// naming an MCID the page really does not define must still be reported.</para>
/// </remarks>
public class IndirectPropertiesMcidTests
{
    /// <summary>
    /// No UA50 is raised for a document whose MCID is defined through a named property list.
    /// <c>false</c> is the control: the inline spelling always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /P0 << /MCID 0 >>
    [InlineData(true)]   // /P0 << /MCID 9 0 R >>
    public void NoUa50_WhicheverWayThePropertyMcidIsSpelled(bool indirect)
    {
        var result = PdfUaChecker.Validate(new MemoryStream(NamedPropertyPdf(indirect, mcid: 0)));

        Assert.DoesNotContain(result.Violations, i => i.Code == "UA50");
    }

    /// <summary>
    /// The floor: a structure element naming an MCID the page genuinely never defines must still be
    /// reported, so the assertion above is a claim about the read rather than about a check that
    /// stopped running.
    /// </summary>
    [Fact]
    public void AnUndefinedMcidIsStillReported()
    {
        // The element names MCID 0; the page's only property list defines MCID 7.
        var result = PdfUaChecker.Validate(new MemoryStream(NamedPropertyPdf(indirect: false, mcid: 7)));

        Assert.Contains(result.Violations, i => i.Code == "UA50");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A tagged page whose marked content is opened with <c>/P0 BDC</c>, so the MCID comes from the
    /// page's <c>/Properties</c> rather than from an inline dictionary. Object 9 holds the number
    /// when <paramref name="indirect"/>; <paramref name="mcid"/> is what the property list defines,
    /// while the structure element always names 0.
    /// </summary>
    private static byte[] NamedPropertyPdf(bool indirect, int mcid)
    {
        string value = indirect ? "9 0 R" : mcid.ToString(CultureInfo.InvariantCulture);

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >>"
                + " /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /StructParents 0 /Resources << /Properties << /P0 7 0 R >> >> >>",
            StreamObj("/P /P0 BDC 0 0 0 rg 20 20 60 60 re f EMC\n"),
            "<< /Type /StructTreeRoot /K [6 0 R] >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K 0 >>",
            "<< /MCID " + value + " >>",
            "null",
            mcid.ToString(CultureInfo.InvariantCulture));
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
        + " >>\nstream\n" + data + "endstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
