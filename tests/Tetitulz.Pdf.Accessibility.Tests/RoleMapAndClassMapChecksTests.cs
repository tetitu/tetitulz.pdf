// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The two normative indirections a structure element may use, which the deep checker follows.
///
/// <para>
/// <b><c>/RoleMap</c>.</b> ISO 32000-1 §14.7.3 / §14.8.4 make it the sanctioned way for a producer
/// to use its own structure types, and a conforming consumer must treat a <c>/Bullet</c> mapped to
/// <c>/LI</c> exactly as an <c>/LI</c>. Compared on the raw <c>/S</c>, UA10/UA11/UA12/UA15 would
/// give a fully conformant role-mapped document manufactured Errors — "List (L) element contains a
/// child of type Bullet", "Table row (TR) contains a child of type HeaderCell", "Table has no TH
/// (header) cells" — and never validate the real substructure of those elements, while the
/// sibling <c>PdfUaChecker.CanonicalRole</c> in the Conformance plugin resolves the role: the two
/// checkers in this repository would disagree about the same file.
/// </para>
///
/// <para>
/// <b><c>/ClassMap</c>.</b> §14.7.5.2 defines a second, equally normative way to attach attributes:
/// the element carries <c>/C &lt;class name&gt;</c> and <c>/StructTreeRoot /ClassMap</c> maps each
/// name to an attribute object with the same <c>/O</c> owner semantics as <c>/A</c>. Reading only
/// <c>/A</c>, UA12 would read every span of a table that declares its merges and header scopes
/// through a class as 1, and every <c>/Scope</c> as absent.
/// </para>
///
/// <para>Each rule gets both directions, and each "is silent" case is paired with a control that
/// removes only the map — proving the fixture really does exercise the rule rather than being
/// silent for some unrelated reason. Every fixture is built in memory from ASCII bytes.</para>
/// </summary>
public class RoleMapAndClassMapChecksTests
{
    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(objectBodies.Count);
        for (int i = 0; i < objectBodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(objectBodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objectBodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A one-page document whose <c>/StructTreeRoot</c> body is supplied verbatim (so a test can
    /// add or withhold a <c>/RoleMap</c> or a <c>/ClassMap</c>) and whose structure elements start
    /// at <b>object 7</b>. The page's content is a single <c>/Artifact BMC … EMC</c> sequence and
    /// it carries no <c>/StructParents</c>, which is the correct combination for a page holding no
    /// structural content — so the content-level checks have nothing to say.
    /// </summary>
    private static byte[] StructurePdf(string structTreeRootExtras, params string[] elements)
    {
        var bodies = new List<string>
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> "
            + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

            // 2 — page tree
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

            // 3 — page
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Tabs /S >>",

            // 4 — content: one artifact sequence, so nothing on the page is structural content
            StreamObject("/Artifact BMC\nBT /F1 12 Tf 72 700 Td (Decorative) Tj ET\nEMC\n"),

            // 5 — font
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",

            // 6 — structure tree root
            "<< /Type /StructTreeRoot /K [7 0 R] " + structTreeRootExtras + ">>",
        };

        bodies.AddRange(elements);
        return BuildPdf(bodies);
    }

    private static string Element(string role, params int[] kids)
    {
        var sb = new StringBuilder("<< /Type /StructElem /S /").Append(role).Append(" /K [");
        for (int i = 0; i < kids.Length; i++)
            sb.Append(kids[i].ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
        return sb.Append("] >>").ToString();
    }

    private static string Leaf(string role, string extras = "")
        => "<< /Type /StructElem /S /" + role + " " + extras + ">>";

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    private static IReadOnlyList<AccessibilityIssue> WithCode(
        IEnumerable<AccessibilityIssue> issues, string code)
        => issues.Where(i => string.Equals(i.Code, code, StringComparison.Ordinal)).ToList();

    private static string Describe(IEnumerable<AccessibilityIssue> issues)
        => string.Join(" | ", issues.Select(i => i.ToString()));

    private static void AssertSilent(byte[] pdf, string code)
    {
        var issues = Check(pdf);
        Assert.True(WithCode(issues, code).Count == 0,
            code + " must not fire on this document. Issues: " + Describe(issues));
    }

    // =====================================================================
    //  /RoleMap — the elements under test
    // =====================================================================

    private const string ListAndTableRoleMap =
        "/RoleMap << /Bullet /LI /HeaderCell /TH /DataCell /TD >> ";

    /// <summary>
    /// A list of two role-mapped items, each correctly built as Lbl + LBody, and a one-row table
    /// of one role-mapped header cell and one role-mapped data cell. Every custom type resolves to
    /// a standard one through the caller-supplied root, so this document is conformant.
    /// </summary>
    private static string[] RoleMappedListAndTable() => new[]
    {
        /*  7 */ Element("Document", 8, 15),
        /*  8 */ Element("L", 9, 12),
        /*  9 */ Element("Bullet", 10, 11),
        /* 10 */ Leaf("Lbl"),
        /* 11 */ Leaf("LBody"),
        /* 12 */ Element("Bullet", 13, 14),
        /* 13 */ Leaf("Lbl"),
        /* 14 */ Leaf("LBody"),
        /* 15 */ Element("Table", 16),
        /* 16 */ Element("TR", 17, 18),
        /* 17 */ Leaf("HeaderCell", "/A << /O /Table /Scope /Column >> "),
        /* 18 */ Leaf("DataCell"),
    };

    [Fact]
    public void Ua11_RoleMappedListItems_AreSilent()
        => AssertSilent(StructurePdf(ListAndTableRoleMap, RoleMappedListAndTable()), "UA11");

    [Fact]
    public void Ua12_RoleMappedTableCells_AreSilent()
        => AssertSilent(StructurePdf(ListAndTableRoleMap, RoleMappedListAndTable()), "UA12");

    /// <summary>
    /// The control. The same elements with the <c>/RoleMap</c> removed really are undefined types,
    /// so both rules must fire — which is what proves the two tests above are silent because the
    /// roles resolve, not because the fixture never reaches the rules.
    /// </summary>
    [Fact]
    public void WithoutTheRoleMap_TheSameElementsAreReported()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(StructurePdf("", RoleMappedListAndTable()));

        Assert.Contains(WithCode(issues, "UA11"),
            i => i.Message.IndexOf("child of type Bullet", StringComparison.Ordinal) >= 0);
        Assert.Contains(WithCode(issues, "UA12"),
            i => i.Message.IndexOf("child of type HeaderCell", StringComparison.Ordinal) >= 0);
        Assert.Contains(WithCode(issues, "UA12"),
            i => i.Message.IndexOf("no TH (header) cells", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// Resolution must not be a way of switching the rules off. A role-mapped list item whose
    /// LBody precedes its Lbl is exactly as wrong as a literal <c>&lt;LI&gt;</c> that does it — and
    /// while the roles went unresolved this defect was unreachable, because the item was rejected
    /// as an illegal child of <c>&lt;L&gt;</c> before its own children were ever examined.
    /// </summary>
    [Fact]
    public void Ua11_RoleMappedListItemWithReversedLabelAndBody_IsStillReported()
    {
        byte[] pdf = StructurePdf(ListAndTableRoleMap,
            /*  7 */ Element("Document", 8),
            /*  8 */ Element("L", 9),
            /*  9 */ Element("Bullet", 10, 11),
            /* 10 */ Leaf("LBody"),                 // reversed: the bullet would be read last
            /* 11 */ Leaf("Lbl"));

        AccessibilityIssue bad = Assert.Single(WithCode(Check(pdf), "UA11"));

        Assert.Equal(IssueSeverity.Warning, bad.Severity);   // reading advice (Table 336 states no order)
        Assert.Contains("places its LBody before its Lbl", bad.Message, StringComparison.Ordinal);
        Assert.Equal(9, bad.ObjectNumber);
    }

    /// <summary>
    /// The heading hierarchy is judged on resolved roles too: <c>/Chapter</c> → <c>/H1</c> and
    /// <c>/SubSub</c> → <c>/H3</c> skip a level exactly as H1 → H3 does. The message leads with the
    /// type written in the file, because that is what the reader will search for.
    /// </summary>
    [Fact]
    public void Ua10_RoleMappedHeadingsSkippingALevel_AreReportedWithBothNames()
    {
        byte[] pdf = StructurePdf("/RoleMap << /Chapter /H1 /SubSub /H3 >> ",
            /* 7 */ Element("Document", 8, 9),
            /* 8 */ Leaf("Chapter"),
            /* 9 */ Leaf("SubSub"));

        AccessibilityIssue skip = Assert.Single(WithCode(Check(pdf), "UA10"));

        Assert.Contains("skips a level", skip.Message, StringComparison.Ordinal);
        Assert.Contains("SubSub (role-mapped to H3)", skip.Message, StringComparison.Ordinal);
        Assert.Equal(9, skip.ObjectNumber);
    }

    /// <summary>
    /// A role-mapped illustration type has its alt text judged like any other Figure — UA15 sees
    /// more than a literal <c>/S /Figure</c>.
    /// </summary>
    [Fact]
    public void Ua15_RoleMappedFigureWithGenericAltText_IsReported()
    {
        byte[] pdf = StructurePdf("/RoleMap << /Illustration /Figure >> ",
            /* 7 */ Element("Document", 8),
            /* 8 */ Leaf("Illustration", "/Pg 3 0 R /Alt (image) "));

        AccessibilityIssue generic = Assert.Single(WithCode(Check(pdf), "UA15"));

        Assert.Contains("generic placeholder alt text", generic.Message, StringComparison.Ordinal);
        Assert.Equal(8, generic.ObjectNumber);
    }

    // =====================================================================
    //  /ClassMap — table attributes supplied by class
    // =====================================================================

    private const string TableClassMap =
        "/ClassMap << /Merged2 << /O /Table /ColSpan 2 >> /ColHdr << /O /Table /Scope /Column >> >> ";

    /// <summary>
    /// Row 1 is a single header cell spanning both columns; row 2 is two data cells. The merge and
    /// the header scope are declared through classes, which is exactly as valid as declaring them
    /// inline in <c>/A</c>.
    /// </summary>
    private static string[] ClassAttributedTable(string headerClassEntry) => new[]
    {
        /*  7 */ Element("Document", 8),
        /*  8 */ Element("Table", 9, 10),
        /*  9 */ Element("TR", 11),
        /* 10 */ Element("TR", 12, 13),
        /* 11 */ Leaf("TH", headerClassEntry),
        /* 12 */ Leaf("TD"),
        /* 13 */ Leaf("TD"),
    };

    [Fact]
    public void Ua12_ClassSuppliedColSpanAndScope_AreSilent()
        => AssertSilent(
            StructurePdf(TableClassMap, ClassAttributedTable("/C [/ColHdr /Merged2] ")), "UA12");

    /// <summary>
    /// <c>/C</c> may also be a bare name rather than an array — the single-class shape most
    /// producers emit.
    /// </summary>
    [Fact]
    public void Ua12_ClassSuppliedScopeOnASingleClassName_IsSilent()
        => AssertSilent(StructurePdf(
            "/ClassMap << /ColHdr << /O /Table /Scope /Column >> >> ",
            /*  7 */ Element("Document", 8),
            /*  8 */ Element("Table", 9, 10),
            /*  9 */ Element("TR", 11),
            /* 10 */ Element("TR", 12),
            /* 11 */ Leaf("TH", "/C /ColHdr "),
            /* 12 */ Leaf("TD")), "UA12");

    /// <summary>
    /// A <c>/C</c> array may interleave revision numbers exactly as an <c>/A</c> array does. They
    /// are not class names and must be stepped over rather than ending the search.
    /// </summary>
    [Fact]
    public void Ua12_ClassArrayInterleavedWithRevisionNumbers_IsSilent()
        => AssertSilent(
            StructurePdf(TableClassMap, ClassAttributedTable("/C [/ColHdr 0 /Merged2 0] ")), "UA12");

    /// <summary>
    /// The control: the identical table with the <c>/ClassMap</c> removed. Nothing then declares
    /// the merge, so the table really is ragged and the header really is unaddressable — which is
    /// what the checker reported on the conformant file above until <c>/C</c> was read.
    /// </summary>
    [Fact]
    public void Ua12_WithoutTheClassMap_TheSameTableIsReportedAsRagged()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            Check(StructurePdf("", ClassAttributedTable("/C [/ColHdr /Merged2] ")));

        Assert.Contains(WithCode(issues, "UA12"),
            i => i.Message.IndexOf("row 1 spans 1 column(s) but row 2 spans 2", StringComparison.Ordinal) >= 0);
        Assert.Contains(WithCode(issues, "UA12"),
            i => i.Message.IndexOf("neither a /Scope attribute nor an /ID", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// A class whose attribute object declares a different owner is not a table attribute, so it
    /// must not be honoured — the <c>/O</c> rule applies to the class route exactly as it does to
    /// <c>/A</c>. Without this the class map would become a way to smuggle any key past the owner
    /// check.
    /// </summary>
    [Fact]
    public void Ua12_ClassOwnedByADifferentAttributeOwner_IsNotHonoured()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(StructurePdf(
            "/ClassMap << /Merged2 << /O /Layout /ColSpan 2 >> /ColHdr << /O /Table /Scope /Column >> >> ",
            ClassAttributedTable("/C [/ColHdr /Merged2] ")));

        Assert.Contains(WithCode(issues, "UA12"),
            i => i.Message.IndexOf("row 1 spans 1 column(s) but row 2 spans 2", StringComparison.Ordinal) >= 0);
    }
}
