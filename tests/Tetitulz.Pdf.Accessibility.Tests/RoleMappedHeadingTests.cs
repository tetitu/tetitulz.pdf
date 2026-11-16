// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Editing;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>FixHeadingHierarchy</c> must see a heading the document reaches through <c>/RoleMap</c>.
/// </summary>
/// <remarks>
/// <para><b>The last unconverted fixer.</b> <c>RoleMapResolver</c>'s own summary names
/// <c>SetFigureAltText</c>, <c>FixHeadingHierarchy</c> and the remediation gate as the three sites
/// that compared <c>StructNode.Role</c> raw. Two were converted when it was written; this one was
/// not. §14.7.3 makes <c>/RoleMap</c> the sanctioned way for a producer to use its own structure
/// types, so under <c>/RoleMap &lt;&lt; /Chapter /H1 /Section /H3 &gt;&gt;</c> the deep checker —
/// which resolves — reported the skipped level, while the planner saw
/// <c>HeadingLevel("Chapter") == -1</c>, skipped every element, and reported a document with no
/// headings in it. <b>The audit and the fixer contradicted each other on the same bytes.</b></para>
///
/// <para><b>The assertion is the count of headings the planner found</b>, not the audit's issue
/// list. The audit already resolved roles and so was never wrong here; asserting on it would test
/// the half that worked. Counting what the FIXER found is what separates the two.</para>
/// </remarks>
public class RoleMappedHeadingTests
{
    /// <summary>
    /// A document whose headings are reached only through <c>/RoleMap</c> yields the same heading
    /// count as one that spells them <c>/H1</c> and <c>/H3</c> directly.
    /// </summary>
    [Fact]
    public void RoleMappedHeadings_AreCountedLikeDirectOnes()
    {
        int direct = HeadingsFound(HeadingPdf(mapped: false));
        int mapped = HeadingsFound(HeadingPdf(mapped: true));

        // The control really does renumber something — H1 followed by H3 skips a level, which is
        // the UA10 condition, so the H3 becomes an H2. Without this the equality below would hold
        // just as well for two implementations that both found nothing.
        Assert.True(direct > 0, "the control renumbered nothing; the fixture never reached the planner");

        Assert.Equal(direct, mapped);
    }

    /// <summary>
    /// The guard that keeps the rule "raw <b>or</b> mapped" rather than "mapped": a
    /// <c>/RoleMap</c> that remaps <c>/H1</c> to something else must not make a real <c>H1</c>
    /// invisible. Testing only the resolved role would hand a file the power to switch the check
    /// off, which is the attack <c>RoleMapResolver.Means</c> exists to prevent.
    /// </summary>
    [Fact]
    public void RemappingH1ToDiv_DoesNotHideARealH1()
    {
        int plain    = HeadingsFound(HeadingPdf(mapped: false));
        int remapped = HeadingsFound(HeadingPdf(mapped: false, extraRoleMap: "/H1 /Div /H3 /Div"));

        Assert.True(plain > 0);
        Assert.Equal(plain, remapped);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// How many headings the renumbering step rewrote. Zero when the planner recognised none, which
    /// is exactly the state the raw-role comparison produced for a role-mapped document.
    /// </summary>
    private static int HeadingsFound(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        return PdfStructureTreeEditor.FixHeadingHierarchy(input, output);
    }

    /// <summary>
    /// Two headings that skip a level (H1 then H3, which is the UA10 condition). When
    /// <paramref name="mapped"/> they are spelled <c>/Chapter</c> and <c>/Section</c> and reached
    /// through <c>/RoleMap</c>.
    /// </summary>
    private static byte[] HeadingPdf(bool mapped, string extraRoleMap = null)
    {
        string first = mapped ? "/Chapter" : "/H1";
        string second = mapped ? "/Section" : "/H3";
        string roleMap = mapped ? "/Chapter /H1 /Section /H3" : (extraRoleMap ?? "");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> " +
                "/StructParents 0 >>",
            StreamObj("BT ET\n"),
            "<< /Type /StructTreeRoot /K [6 0 R 7 0 R] /RoleMap << " + roleMap + " >> >>",
            "<< /Type /StructElem /S " + first + " /P 5 0 R /Pg 3 0 R >>",
            "<< /Type /StructElem /S " + second + " /P 5 0 R /Pg 3 0 R >>");
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
