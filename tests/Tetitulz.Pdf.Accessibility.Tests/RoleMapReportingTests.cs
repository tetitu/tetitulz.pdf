// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// UA13's three <c>/RoleMap</c> findings, and the deep checker's first-standard resolution rule.
/// </summary>
/// <remarks>
/// <para>These pin what the shared <c>Core.Document.PdfRoleMap</c> reader had to preserve when it
/// replaced the checker's two hand-written map builds. <b>None of the three was covered before</b>:
/// the "→ (not a name)" branch depends on a malformed entry being <i>kept with a null value</i>
/// rather than dropped, which is the one thing the shared reader does differently from the other
/// callers' needs, and nothing in the tree exercised it.</para>
///
/// <para><b>The alt-text assertion is what makes the resolution rule falsifiable.</b> A checker
/// that resolves to the chain's terminus instead of to the first standard type reports the element
/// as a <c>&lt;Div&gt;</c> and never asks it for <c>/Alt</c>; one that resolves correctly reports
/// the missing alt text. Asserting merely that UA13 fired would pass either way.</para>
/// </remarks>
public class RoleMapReportingTests
{
    /// <summary>
    /// A custom role that maps to a standard type is checked as that type: the element is asked
    /// for the <c>/Alt</c> a <c>&lt;Figure&gt;</c> owes, exactly as an untranslated one is.
    /// </summary>
    [Fact]
    public void ARoleMappedFigureIsAskedForTheAltTextAFigureOwes()
    {
        // Control: the same document with the role written literally.
        Assert.Contains("UA07", IssueCodes(TaggedPdf("/Figure", "")));

        string[] codes = IssueCodes(TaggedPdf("/Illustration", "/Illustration /Figure"));

        Assert.Contains("UA07", codes);          // judged a figure, and it has no /Alt
        Assert.DoesNotContain("UA13", codes);    // the role itself resolves fine
    }

    /// <summary>
    /// <b>A known gap, pinned so it cannot change unnoticed.</b> With
    /// <c>/RoleMap &lt;&lt; /Illustration /Figure /Figure /Div &gt;&gt;</c> the chain passes
    /// <i>through</i> a standard type, and the figure's missing alt text is reported by
    /// <b>nothing</b>.
    /// </summary>
    /// <remarks>
    /// <para>The two checkers apply different rules and only one of them owns this finding.
    /// <c>PdfDeepAccessibilityChecker.ResolveRole</c> stops at the first <i>standard</i> type, so it
    /// says <c>Figure</c>; <c>Plugins.Conformance.PdfUaChecker.CanonicalRole</c> — which owns UA07,
    /// "a figure with no /Alt" — stops at the terminus, so it says <c>Div</c> and asks nothing of
    /// the element.</para>
    ///
    /// <para>Making the plugin's rule the strong one needs the standard-type vocabulary that lives
    /// on the deep checker's side, which is a separate decision and a separate row; the shared
    /// <c>Core.Document.PdfRoleMap.Resolve</c> is deliberately vocabulary-free. The remapping of
    /// <c>/Figure</c> <i>is</i> reported (UA13), so the file is not silently accepted — but the
    /// specific finding it suppresses does not survive.</para>
    /// </remarks>
    [Fact]
    public void AChainThroughAStandardTypeSuppressesTheFigureAltTextFinding()
    {
        string[] codes = IssueCodes(TaggedPdf("/Illustration", "/Illustration /Figure /Figure /Div"));

        Assert.Contains("UA13", codes);          // remapping a standard type is itself reported
        Assert.DoesNotContain("UA07", codes);    // …but the missing alt text is not — the gap
    }

    /// <summary>
    /// ISO 14289-1 7.1: a remapped ISO 32000-1 standard type is an error (the published profile's 7.1-7); a type only
    /// ISO 32000-2 made standard (<c>/Title</c>) is a custom type in a PDF/UA-1 file, so remapping it is
    /// a warning. A standard type mapped to itself is remapped too, and circularly (the published profile's 7.1-6).
    /// </summary>
    [Theory]
    [InlineData("/Figure /Div", "Standard structure type(s) are remapped", "Error")]
    [InlineData("/Figure /Figure", "remapped in /RoleMap: Figure → Figure", "Error")]
    [InlineData("/Title /P", "ISO 32000-2 made standard are remapped", "Warning")]
    public void ARemappedStandardType_IsAnErrorUnlessOnlyPdf20MadeItStandard(string roleMapBody, string message, string severity)
    {
        var ua13 = Audit(TaggedPdf("/Figure", roleMapBody)).Issues
            .Where(i => i.Code == "UA13" && i.Message.Contains(message, StringComparison.Ordinal)).ToList();
        Assert.Equal(severity, Assert.Single(ua13).Severity.ToString());
    }

    /// <summary>
    /// A <c>/RoleMap</c> entry whose value is not a name is reported, and named. This is the branch
    /// that needs the malformed entry preserved rather than dropped — dropped, the role reads as
    /// unmapped, which is a different finding with different advice.
    /// </summary>
    [Fact]
    public void ARoleMapEntryWhoseTargetIsNotANameIsReportedAsSuch()
    {
        var report = Audit(TaggedPdf("/Illustration", "/Illustration 42"));

        string message = string.Join(" | ",
            report.Issues.Where(i => i.Code == "UA13").Select(i => i.Message));

        Assert.Contains("not a name", message, StringComparison.Ordinal);
        Assert.Contains("Illustration", message, StringComparison.Ordinal);
    }

    /// <summary>A chain that never reaches a standard type is reported as a cycle, not as unresolved.</summary>
    [Fact]
    public void AMappingCycleIsReportedAsACycle()
    {
        var report = Audit(TaggedPdf("/Alpha", "/Alpha /Beta /Beta /Gamma /Gamma /Alpha"));

        string message = string.Join(" | ",
            report.Issues.Where(i => i.Code == "UA13").Select(i => i.Message));

        Assert.Contains("cycle", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Alpha", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The deep checker resolves to the first standard type, not to the terminus</b>, and this
    /// is what says so. A list item written <c>/Bullet</c> under
    /// <c>/RoleMap &lt;&lt; /Bullet /LI /LI /Div &gt;&gt;</c> is still an <c>LI</c>, so
    /// <c>&lt;L&gt;</c> has a legal child and UA11 stays quiet.
    /// </summary>
    /// <remarks>
    /// <para>Under the terminus rule the chain ends at <c>Div</c>, and UA11 reports the item as an
    /// illegal child of <c>&lt;L&gt;</c> — <b>manufacturing</b> a defect in a document that has
    /// none, which is the failure <c>RoleOf</c>'s own doc comment describes. That is why the deep
    /// checker keeps its own resolver rather than calling the shared
    /// <c>Core.Document.PdfRoleMap.Resolve</c>.</para>
    ///
    /// <para>The single-hop control is not optional: without it a checker that ignored
    /// <c>/RoleMap</c> altogether would also produce no UA11 here, for the wrong reason.</para>
    /// </remarks>
    [Fact]
    public void AListItemWhoseChainPassesThroughLIIsStillAListItem()
    {
        // Control: a plain /Bullet with no mapping at all IS an illegal child of <L>.
        Assert.Contains("UA11", IssueCodes(ListPdf("")));

        // Single hop — the ordinary case.
        Assert.DoesNotContain("UA11", IssueCodes(ListPdf("/Bullet /LI")));

        // Through a standard type: first-standard says LI, terminus says Div.
        Assert.DoesNotContain("UA11", IssueCodes(ListPdf("/Bullet /LI /LI /Div")));
    }

    // ------------------------------------------------------------------

    private static AccessibilityAuditReport Audit(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var report = PdfAccessibility.Audit(ms);
        Assert.NotNull(report);
        return report;
    }

    private static string[] IssueCodes(byte[] pdf)
        => Audit(pdf).Issues.Select(i => i.Code).Distinct()
            .OrderBy(c => c, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A one-page tagged document with a single element of type <paramref name="role"/> and no
    /// <c>/Alt</c>, plus the given <c>/RoleMap</c> body.
    /// </summary>
    private static byte[] TaggedPdf(string role, string roleMapBody) => Build(
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> " +
            "/StructParents 0 >>",
        StreamObj("/Figure <</MCID 0>> BDC\n10 10 50 50 re\nf\nEMC\n"),
        "<< /Type /StructTreeRoot /K [6 0 R] /ParentTree 7 0 R /RoleMap << " + roleMapBody + " >> >>",
        "<< /Type /StructElem /S " + role + " /P 5 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [0 [6 0 R]] >>");

    /// <summary>
    /// A one-page tagged document holding <c>&lt;L&gt; → &lt;Bullet&gt; → (&lt;Lbl&gt;,
    /// &lt;LBody&gt;)</c>, so the only thing UA11 can object to is what <c>/Bullet</c> resolves to.
    /// </summary>
    private static byte[] ListPdf(string roleMapBody) => Build(
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> " +
            "/StructParents 0 >>",
        StreamObj("/P <</MCID 0>> BDC\n10 10 50 50 re\nf\nEMC\n/P <</MCID 1>> BDC\n10 70 50 50 re\nf\nEMC\n"),
        "<< /Type /StructTreeRoot /K [6 0 R] /ParentTree 10 0 R" +
            (roleMapBody.Length == 0 ? "" : " /RoleMap << " + roleMapBody + " >>") + " >>",
        "<< /Type /StructElem /S /L /P 5 0 R /K [7 0 R] >>",
        "<< /Type /StructElem /S /Bullet /P 6 0 R /K [8 0 R 9 0 R] >>",
        "<< /Type /StructElem /S /Lbl /P 7 0 R /Pg 3 0 R /K [0] >>",
        "<< /Type /StructElem /S /LBody /P 7 0 R /Pg 3 0 R /K [1] >>",
        "<< /Nums [0 [8 0 R 9 0 R]] >>");

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
