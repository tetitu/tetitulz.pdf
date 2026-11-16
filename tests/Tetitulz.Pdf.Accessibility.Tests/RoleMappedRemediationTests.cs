// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A check and the fixer for that check must agree on what an element <b>is</b>.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> §14.7.3 makes <c>/RoleMap</c> the sanctioned way for a producer to use
/// its own structure types, and a conforming consumer must treat an <c>&lt;Illustration&gt;</c>
/// mapped to <c>/Figure</c> exactly as a <c>&lt;Figure&gt;</c>. The deep checker has resolved roles
/// since it was written — <c>RoleOf</c>'s own doc comment says "every structural rule must test
/// this, never <c>StructNode.Role</c>". But that resolution lived inside the checker's context, and
/// <b>the fixers had none</b>: <c>SetFigureAltText</c>, its encrypted <c>Plan</c> twin, and the
/// remediation pipeline's own gate all compared <c>node.Role</c> raw.</para>
///
/// <para>So the audit reported the missing alt text and the step that fixes it walked straight past
/// the element — <b><c>IsConforming</c> was unreachable for a role-mapped document however many
/// times <c>Remediate</c> was re-run</b>. Nothing failed; nothing changed.</para>
/// </remarks>
public class RoleMappedRemediationTests
{
    /// <summary>
    /// <c>/RoleMap &lt;&lt; /Illustration /Figure &gt;&gt;</c>: the element means Figure, so the
    /// alt-text fixer must reach it.
    /// </summary>
    /// <remarks>
    /// Asserts the WRITTEN document, not the return value: the defect's signature is a step that
    /// reports zero and returns cleanly, so a count alone could be satisfied by a fixer that had
    /// stopped working entirely. The control below is the same document without the map.
    /// </remarks>
    [Fact]
    public void RoleMappedFigure_ReceivesAltText()
    {
        using var input = new MemoryStream(RoleMappedFigurePdf(mapped: true));
        using var output = new MemoryStream();

        PdfAccessibility.SetFigureAltText(input, output, _ => "a mapped illustration");

        Assert.Contains("a mapped illustration", Latin1(output.ToArray()));
    }

    /// <summary>
    /// The control: an ordinary <c>&lt;Figure&gt;</c> with no <c>/RoleMap</c> is still reached, so
    /// the test above cannot pass by the fixer having become indiscriminate.
    /// </summary>
    [Fact]
    public void PlainFigure_StillReceivesAltText()
    {
        using var input = new MemoryStream(RoleMappedFigurePdf(mapped: false));
        using var output = new MemoryStream();

        PdfAccessibility.SetFigureAltText(input, output, _ => "a plain figure");

        Assert.Contains("a plain figure", Latin1(output.ToArray()));
    }

    /// <summary>
    /// An element that means nothing of the sort is left alone — otherwise "the fixer reached it"
    /// would be true of every element in every document.
    /// </summary>
    [Fact]
    public void UnrelatedRole_IsNotTreatedAsAFigure()
    {
        using var input = new MemoryStream(RoleMappedFigurePdf(mapped: true, role: "Sidebar"));
        using var output = new MemoryStream();

        PdfAccessibility.SetFigureAltText(input, output, _ => "should not appear");

        Assert.DoesNotContain("should not appear", Latin1(output.ToArray()));
    }

    /// <summary>
    /// A document cannot hide its own failures by remapping a <b>standard</b> type:
    /// <c>/RoleMap &lt;&lt; /Figure /Div &gt;&gt;</c> must not make an un-alt-texted figure
    /// invisible to the audit.
    /// </summary>
    /// <remarks>
    /// §14.7.4 forbids remapping a standard type to another standard type, but nothing stops a
    /// file from containing the entry — and a checker that canonicalises before comparing then
    /// reports every <c>&lt;Figure&gt;</c> in the document as a <c>&lt;Div&gt;</c>. UA07 is owned
    /// exclusively by the Conformance plugin (the deep checker defers to it), so there was no
    /// second opinion to catch it: the report simply came back clean.
    /// </remarks>
    [Fact]
    public void RemappingFigureToDiv_DoesNotHideTheMissingAltText()
    {
        using var input = new MemoryStream(FigureRemappedToDivPdf());

        AccessibilityAuditReport report = PdfAccessibility.Audit(input);

        Assert.Contains(report.Issues, i =>
            i.Code == "UA07" ||
            (i.Message ?? string.Empty).IndexOf("alt", System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// The control: the same document with alt text present reports no alt-text issue, so the test
    /// above cannot pass by the audit reporting alt-text problems unconditionally.
    /// </summary>
    [Fact]
    public void RemappedFigureWithAltText_ReportsNoAltTextIssue()
    {
        using var input = new MemoryStream(FigureRemappedToDivPdf(alt: "described"));

        AccessibilityAuditReport report = PdfAccessibility.Audit(input);

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA07");
    }

    // ------------------------------------------------------------------ fixture

    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    /// <summary>
    /// One page, one structure element whose <c>/S</c> is <paramref name="role"/>, and a
    /// <c>/RoleMap</c> mapping <c>/Illustration</c> to <c>/Figure</c> when
    /// <paramref name="mapped"/>. With <c>mapped: false</c> the element is a plain
    /// <c>&lt;Figure&gt;</c> and no map is written at all.
    /// </summary>
    private static byte[] RoleMappedFigurePdf(bool mapped, string role = "Illustration")
    {
        const string content = "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n";
        string elementRole = mapped ? role : "Figure";
        string roleMap = mapped ? " /RoleMap << /Illustration /Figure >>" : "";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> /StructParents 0 >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) +
            " >>\nstream\n" + content + "endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [7 0 R]" + roleMap + " >>",
            "<< /Type /StructElem /S /" + elementRole + " /P 6 0 R /Pg 3 0 R /K [0] >>");
    }

    /// <summary>
    /// A genuine <c>&lt;Figure&gt;</c>, plus a <c>/RoleMap</c> entry remapping the standard type
    /// <c>/Figure</c> to <c>/Div</c> — the shape that made the figure invisible to UA07.
    /// </summary>
    private static byte[] FigureRemappedToDivPdf(string alt = null)
    {
        const string content = "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n";
        string altEntry = alt == null ? "" : " /Alt (" + alt + ")";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> /StructParents 0 >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) +
            " >>\nstream\n" + content + "endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /RoleMap << /Figure /Div >> >>",
            "<< /Type /StructElem /S /Figure /P 6 0 R /Pg 3 0 R /K [0]" + altEntry + " >>");
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
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
