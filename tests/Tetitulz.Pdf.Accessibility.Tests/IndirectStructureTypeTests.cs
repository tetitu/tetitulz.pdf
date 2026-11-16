// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A structure element whose <c>/S</c> is written as an <b>indirect reference</b> must still have a
/// role.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>StructTreeWalker.GetRole</c> read
/// <c>(node?[PdfName.S] as PdfName)?.Value</c>. <c>PdfDictionary</c>'s indexer does not follow a
/// reference (§7.3.10 lets any value be indirect), so <c>/S 9 0 R</c> came back <c>null</c>. Its own
/// summary said "no <b>direct</b> <c>/S</c> name" — an accurate description of a defect, phrased as
/// a specification.</para>
///
/// <para><b>Why null is expensive here.</b> A null role is a <i>sentinel</i>: three checks read it
/// as "this node is the <c>/StructTreeRoot</c>" and skip the node —
/// <c>RoleMapping.cs:230</c> (which says so in a comment), <c>ParentTree.cs:564</c> and
/// <c>HollowElements.cs:176/424</c>. So a legal element was mistaken for the root and dropped from
/// UA14's MCID analysis, UA30's hollow-subtree walk, and the role-mapping check — on valid
/// input.</para>
///
/// <para><b>The assertion is a report that differs between the two spellings.</b> A
/// <c>Figure</c> with no <c>/Alt</c> is a UA-reportable fact; with an unresolved <c>/S</c> the
/// element has no role at all, so nothing recognises it as a figure. Asserting merely that the
/// audit returned something would pass with the whole feature deleted.</para>
/// </remarks>
public class IndirectStructureTypeTests
{
    /// <summary>
    /// The same document written both ways must produce the same set of issue codes. The direct
    /// spelling is the control: it always worked, so a fixture built only on it could never go red.
    /// </summary>
    [Fact]
    public void IndirectS_ProducesTheSameIssuesAsADirectS()
    {
        string[] direct = IssueCodes(FigurePdf(indirectS: false));
        string[] indirect = IssueCodes(FigurePdf(indirectS: true));

        // The control really does see a figure without alt text.
        Assert.Contains(direct, c => c == "UA07" || c == "UA13");

        Assert.Equal(direct, indirect);
    }

    // ------------------------------------------------------------------

    private static string[] IssueCodes(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var report = PdfAccessibility.Audit(ms);
        Assert.NotNull(report);
        return report.Issues.Select(i => i.Code).Distinct().OrderBy(c => c, System.StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// A one-page tagged document with a single <c>Figure</c> element carrying no <c>/Alt</c>.
    /// When <paramref name="indirectS"/> its <c>/S</c> is object 7 rather than a direct name.
    /// </summary>
    private static byte[] FigurePdf(bool indirectS) => Build(
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> " +
            "/StructParents 0 >>",
        StreamObj("/Figure <</MCID 0>> BDC\n10 10 50 50 re\nf\nEMC\n"),
        "<< /Type /StructTreeRoot /K [6 0 R] /ParentTree 8 0 R >>",
        "<< /Type /StructElem /S " + (indirectS ? "7 0 R" : "/Figure") +
            " /P 5 0 R /Pg 3 0 R /K [0] >>",
        "/Figure",
        "<< /Nums [0 [6 0 R]] >>");

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
