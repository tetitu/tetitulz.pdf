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
/// Fires / does-not-fire coverage for UA10 (heading hierarchy), UA11 (list structure) and UA12
/// (table structure and regularity).
///
/// <para>
/// <b>Why this file exists.</b> These three families were rewritten from four coarse aggregate
/// warnings into eleven distinct sub-checks — several of them new, several of them Error severity
/// and therefore able to flip <see cref="AccessibilityAuditReport.IsConforming"/> on their own —
/// and not one of the three codes appeared in any test. The span arithmetic that decides whether a
/// table is regular, and the model classification that decides whether a document is using
/// <c>&lt;H&gt;</c> or <c>H1…H6</c>, could both have inverted without a single red test. Every
/// comparable check in this repository has a fires / does-not-fire pair; these now do too.
/// </para>
///
/// <para>
/// Each sub-check gets both directions, because a check that fires on a correct document is as
/// useless as one that never fires: the false positive is what teaches people to ignore the
/// report. Findings are read straight from <see cref="PdfDeepAccessibilityChecker"/> and filtered
/// by code, so unrelated findings (this stack's fixtures deliberately carry no /ParentTree, for
/// instance) cannot perturb the assertions.
/// </para>
///
/// <para>Every fixture is assembled in memory from ASCII bytes; nothing is read from disk.</para>
/// </summary>
public class HeadingListTableChecksTests
{
    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

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
    /// A one-page document whose structure tree is supplied element by element, starting at
    /// <b>object 7</b> (the tree root's single kid). The page's content is a single
    /// <c>/Artifact BMC … EMC</c> sequence and it carries no <c>/StructParents</c>, which is the
    /// correct combination for a page holding no structural content — so the content-level checks
    /// have nothing to say and the findings under test stand alone.
    /// </summary>
    private static byte[] StructurePdf(params string[] elements)
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
            HelveticaFont,

            // 6 — structure tree root
            "<< /Type /StructTreeRoot /K [7 0 R] >>",
        };

        bodies.AddRange(elements);
        return BuildPdf(bodies);
    }

    /// <summary>
    /// A structure element whose <c>/K</c> lists the given object numbers as children.
    /// </summary>
    private static string Element(string role, params int[] kids)
    {
        var sb = new StringBuilder("<< /Type /StructElem /S /").Append(role).Append(" /K [");
        for (int i = 0; i < kids.Length; i++)
            sb.Append(kids[i].ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
        return sb.Append("] >>").ToString();
    }

    /// <summary>
    /// A childless structure element, optionally carrying extra dictionary entries verbatim
    /// (an <c>/A</c> attribute dictionary, an <c>/ID</c>, …). Named distinctly from
    /// <see cref="Element(string,int[])"/> so no call site depends on overload resolution
    /// between an optional parameter and a <c>params</c> array.
    /// </summary>
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
    //  UA10 — mixed heading models
    // =====================================================================

    /// <summary>
    /// PDF/UA-1 admits two heading models and forbids mixing them: the "strong" model, where a
    /// generic <c>&lt;H&gt;</c> takes its level from its <c>&lt;Sect&gt;</c> nesting, and the
    /// numbered model, <c>H1…H6</c>. This document uses one of each, which is an Error against the
    /// minority model — here the single <c>&lt;H&gt;</c>.
    /// </summary>
    [Fact]
    public void Ua10_MixedHeadingModels_IsReportedAsAnError()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8, 10),
            /* 8  */ Element("Sect", 9),
            /* 9  */ Leaf("H"),
            /* 10 */ Leaf("H1"));

        AccessibilityIssue mixed = Assert.Single(WithCode(Check(pdf), "UA10"));

        Assert.Equal(IssueSeverity.Error, mixed.Severity);
        Assert.Contains("mixes strong heading structure", mixed.Message, StringComparison.Ordinal);
        Assert.Equal(9, mixed.ObjectNumber);
    }

    /// <summary>A document that uses only the strong model, correctly nested, must be silent.</summary>
    [Fact]
    public void Ua10_PureStrongModel_IsSilent()
    {
        AssertSilent(StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Sect", 9, 10),
            /* 9  */ Leaf("H"),
            /* 10 */ Element("Sect", 11),
            /* 11 */ Leaf("H")), "UA10");
    }

    /// <summary>And so must one that uses only the numbered model, without skipping a level.</summary>
    [Fact]
    public void Ua10_PureNumberedModel_IsSilent()
    {
        AssertSilent(StructurePdf(
            /* 7 */ Element("Document", 8, 9),
            /* 8 */ Leaf("H1"),
            /* 9 */ Leaf("H2")), "UA10");
    }

    // =====================================================================
    //  UA10 — level skips
    // =====================================================================

    [Fact]
    public void Ua10_NumberedHeadingSkippingALevel_IsReported()
    {
        byte[] pdf = StructurePdf(
            /* 7 */ Element("Document", 8, 9),
            /* 8 */ Leaf("H1"),
            /* 9 */ Leaf("H3"));

        AccessibilityIssue skip = Assert.Single(WithCode(Check(pdf), "UA10"));

        Assert.Contains("skips a level", skip.Message, StringComparison.Ordinal);
        Assert.Contains("H1 is followed directly by H3", skip.Message, StringComparison.Ordinal);
        Assert.Equal(9, skip.ObjectNumber);
    }

    /// <summary>
    /// The strong model's own skip rule: a section nested two levels deeper than its parent
    /// without an intervening heading. The level of an <c>&lt;H&gt;</c> is its Sect/Part/Art
    /// depth, so this is the strong-model equivalent of H1 → H3.
    /// </summary>
    [Fact]
    public void Ua10_StrongHeadingSkippingALevel_IsReported()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8, 10),
            /* 8  */ Element("Sect", 9),
            /* 9  */ Leaf("H"),                 // effective level 1
            /* 10 */ Element("Sect", 11),
            /* 11 */ Element("Sect", 12),
            /* 12 */ Element("Sect", 13),
            /* 13 */ Leaf("H"));                // effective level 3 — one level was skipped

        AccessibilityIssue skip = Assert.Single(WithCode(Check(pdf), "UA10"));

        Assert.Contains("Strong heading structure skips a level", skip.Message, StringComparison.Ordinal);
        Assert.Equal(13, skip.ObjectNumber);
    }

    // =====================================================================
    //  UA10 — more than one <H> in one section
    // =====================================================================

    [Fact]
    public void Ua10_TwoStrongHeadingsInOneSection_IsReported()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Sect", 9, 10),
            /* 9  */ Leaf("H"),
            /* 10 */ Leaf("H"));

        AccessibilityIssue multi = Assert.Single(WithCode(Check(pdf), "UA10"));

        Assert.Contains("more than one <H>", multi.Message, StringComparison.Ordinal);
        Assert.Equal(8, multi.ObjectNumber);   // reported against the section, not the heading
    }

    /// <summary>
    /// ISO 14289-1 7.4.4: "each node in the structure tree shall contain at most one child H tag" —
    /// every node, a <c>&lt;Div&gt;</c> included (the published profile's 7.4.4-1), not sectioning
    /// containers only.
    /// </summary>
    [Fact]
    public void Ua10_TwoStrongHeadingsUnderANonSectioningParent_IsAnError()
    {
        AccessibilityIssue multi = Assert.Single(WithCode(Check(StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Sect", 9),
            /* 9  */ Element("Div", 10, 11),
            /* 10 */ Leaf("H"),
            /* 11 */ Leaf("H"))), "UA10"));
        Assert.Equal(IssueSeverity.Error, multi.Severity);
        Assert.Contains("more than one <H> (ISO 14289-1 7.4.4)", multi.Message, StringComparison.Ordinal);
        Assert.Equal(9, multi.ObjectNumber);
    }

    /// <summary>ISO 14289-1 7.4.2: a numbered heading that skips a level, and headings with no H1, are errors.</summary>
    [Fact]
    public void Ua10_ANumberedHeadingSkipAndAMissingH1_AreErrors()
    {
        var skip = WithCode(Check(StructurePdf(
            Element("Document", 8, 9), Leaf("H1"), Leaf("H3"))), "UA10");
        Assert.Contains(skip, i => i.Severity == IssueSeverity.Error && i.Message.Contains("skips a level", StringComparison.Ordinal));
        var noH1 = WithCode(Check(StructurePdf(
            Element("Document", 8, 9), Leaf("H2"), Leaf("H3"))), "UA10");
        Assert.Contains(noH1, i => i.Severity == IssueSeverity.Error && i.Message.Contains("no H1", StringComparison.Ordinal));
        Assert.Empty(WithCode(Check(StructurePdf(Element("Document", 8, 9), Leaf("H1"), Leaf("H2"))), "UA10"));
    }

    /// <summary>
    /// ISO 14289-1 7.5: a TH with neither /Scope nor a referenced /ID is an error when some TD names no
    /// headers (the structure is not determinable through /Headers and /ID), advice when every TD does.
    /// </summary>
    [Theory]
    [InlineData("", IssueSeverity.Error)]
    [InlineData("/A << /O /Table /Headers [(h1)] >>", IssueSeverity.Warning)]
    public void Ua12_AnUnaddressedHeader_IsAnErrorUnlessHeadersDetermineTheTable(string tdAttributes, IssueSeverity severity)
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11, 12),
            /* 10 */ Element("TR", 13, 14),
            /* 11 */ Leaf("TH", "/ID (h1) /A << /O /Table /Scope /Column >>"),
            /* 12 */ Leaf("TH"),
            /* 13 */ Leaf("TD", tdAttributes),
            /* 14 */ Leaf("TD", tdAttributes));
        AccessibilityIssue unaddressed = Assert.Single(WithCode(Check(pdf), "UA12"), i => i.Message.Contains("neither a /Scope", StringComparison.Ordinal));
        Assert.Equal(severity, unaddressed.Severity);
        Assert.Equal(12, unaddressed.ObjectNumber);
    }

    // =====================================================================
    //  UA11 — list structure
    // =====================================================================

    /// <summary>
    /// The shape PDF/UA-1 §7.6 requires: L → LI → (Lbl, LBody), in that order, with a nested list
    /// living inside an LBody rather than directly under the LI. Nothing may be reported.
    /// </summary>
    [Fact]
    public void Ua11_CorrectlyStructuredNestedList_IsSilent()
    {
        AssertSilent(StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("L", 9),
            /* 9  */ Element("LI", 10, 11),
            /* 10 */ Leaf("Lbl"),
            /* 11 */ Element("LBody", 12),
            /* 12 */ Element("L", 13),          // the nested list, correctly inside the LBody
            /* 13 */ Element("LI", 14, 15),
            /* 14 */ Leaf("Lbl"),
            /* 15 */ Leaf("LBody")), "UA11");
    }

    [Fact]
    public void Ua11_ListWithANonListItemChild_IsReportedAsAnError()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("L", 9, 10),
            /* 9  */ Element("LI", 11, 12),
            /* 10 */ Leaf("P"),              // illegal: only LI (and Caption) may sit under L
            /* 11 */ Leaf("Lbl"),
            /* 12 */ Leaf("LBody"));

        AccessibilityIssue bad = Assert.Single(WithCode(Check(pdf), "UA11"));

        Assert.Equal(IssueSeverity.Error, bad.Severity);
        Assert.Contains("List (L) element contains a child of type P", bad.Message, StringComparison.Ordinal);
        Assert.Equal(10, bad.ObjectNumber);
    }

    [Fact]
    public void Ua11_ListItemWithANonLabelNonBodyChild_IsReportedAsAnError()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("L", 9),
            /* 9  */ Element("LI", 10, 11, 12),
            /* 10 */ Leaf("Lbl"),
            /* 11 */ Leaf("LBody"),
            /* 12 */ Leaf("P"));             // illegal: only Lbl and LBody may sit under LI

        AccessibilityIssue bad = Assert.Single(WithCode(Check(pdf), "UA11"));

        Assert.Equal(IssueSeverity.Error, bad.Severity);
        Assert.Contains("List item (LI) contains a child of type P", bad.Message, StringComparison.Ordinal);
        Assert.Equal(12, bad.ObjectNumber);
    }

    /// <summary>
    /// A warning, not an error: ISO 32000-1 Table 336 lets an LI hold a label alone (the published profile's
    /// 7.2-t17-pass-c), so the missing body is reading advice.
    /// </summary>
    // ------------------------------------------------------------------ UA31: nesting (ISO 14289-1 7.2)

    public static TheoryData<string[], string> MisnestedTrees => new()
    {
        // Parents
        { new[] { Element("Document", 8), Element("Table", 9), Leaf("TD") }, "TD is not inside a TR (it is under Table)" },
        { new[] { Element("Document", 8), Element("TR", 9), Leaf("TD") }, "TR is not inside a Table, THead, TBody or TFoot (it is under Document)" },
        { new[] { Element("Document", 8), Element("TBody", 9), Element("TR", 10), Leaf("TD") }, "TBody is not inside a Table (it is under Document)" },
        { new[] { Element("Document", 8), Leaf("LI") }, "LI is not inside an L (it is under Document)" },
        { new[] { Element("Document", 8), Element("L", 9), Element("LI", 10), Element("Lbl", 11), Leaf("LBody") }, "LBody is not inside an LI (it is under Lbl)" },
        { new[] { Element("Document", 8), Leaf("TOCI") }, "TOCI is not inside a TOC (it is under Document)" },
        // Children
        { new[] { Element("Document", 8), Element("Table", 9, 10), Element("TR", 11), Leaf("P"), Leaf("TD") }, "Table holds a P" },
        { new[] { Element("Document", 8), Element("Table", 9, 11, 13), Element("THead", 10), Leaf("TR"), Element("THead", 12), Leaf("TR"), Element("TBody", 14), Leaf("TR") }, "holds 2 THead elements" },
        { new[] { Element("Document", 8), Element("Table", 9), Element("TFoot", 10), Leaf("TR") }, "holds a TFoot but no TBody" },
        { new[] { Element("Document", 8), Element("Table", 9, 11, 13), Leaf("Caption"), Element("TR", 12), Leaf("Caption"), Leaf("TD"), Element("TR", 14), Leaf("TD") }, "holds 2 Captions" },
        { new[] { Element("Document", 8), Element("Table", 9, 11, 12), Element("TR", 10), Leaf("TD"), Leaf("Caption"), Element("TR", 13), Leaf("TD") }, "holds its Caption between other children" },
        { new[] { Element("Document", 8), Element("Table", 9, 11), Element("THead", 10), Leaf("TD"), Element("TBody", 12), Leaf("TR") }, "holds a TD; a THead holds only TR" },
        { new[] { Element("Document", 8), Element("TOC", 9), Leaf("P") }, "TOC holds a P" },
        { new[] { Element("Document", 8), Element("TOC", 9, 10), Leaf("TOCI"), Leaf("Caption") }, "TOC holds a Caption after its first child" },
        { new[] { Element("Document", 8), Element("L", 9, 11), Element("LI", 10), Leaf("LBody"), Leaf("Caption") }, "L holds a Caption after its first child" },
    };

    [Theory]
    [MemberData(nameof(MisnestedTrees))]
    public void Ua31_AnElementNestedWhereIso14289Forbids_IsAnError(string[] elements, string expected)
    {
        // A tree can break a parent rule and a child rule at once (a TD under a Table: the TD's parent,
        // the Table's child); the published profiles reports both, and so does UA31.
        var ua31 = WithCode(Check(StructurePdf(elements)), "UA31");
        Assert.Contains(ua31, i => i.Message.Contains(expected, StringComparison.Ordinal));
        Assert.All(ua31, i => Assert.Equal(IssueSeverity.Error, i.Severity));
    }

    [Fact]
    public void Ua31_AWellNestedTableTocAndList_AreQuiet()
    {
        byte[] table = StructurePdf(
            /* 7  */ Element("Document", 8, 17, 20),
            /* 8  */ Element("Table", 9, 10, 13, 15),
            /* 9  */ Leaf("Caption"),
            /* 10 */ Element("THead", 11),
            /* 11 */ Element("TR", 12),
            /* 12 */ Leaf("TH", "/A << /O /Table /Scope /Column >>"),
            /* 13 */ Element("TBody", 14),
            /* 14 */ Element("TR", 23),
            /* 15 */ Element("TFoot", 16),
            /* 16 */ Element("TR", 24),
            /* 17 */ Element("TOC", 18, 19),
            /* 18 */ Leaf("Caption"),
            /* 19 */ Leaf("TOCI"),
            /* 20 */ Element("L", 21),
            /* 21 */ Element("LI", 22),
            /* 22 */ Leaf("LBody"),
            /* 23 */ Leaf("TD"),
            /* 24 */ Leaf("TD"));
        Assert.Empty(WithCode(Check(table), "UA31"));
        // A child of a non-standard type the role map does not resolve is UA13's, not UA31's: the
        // nesting rules count standard kids (the published profile's kidsStandardTypes).
        Assert.Empty(WithCode(Check(StructurePdf(
            Element("Document", 8), Element("Table", 9, 10), Leaf("Custom"), Element("TR", 11), Leaf("TD"))), "UA31"));
        // Potency: the same tree with its TBody gone breaks the THead and TFoot rule.
        Assert.Single(WithCode(Check(StructurePdf(
            Element("Document", 8), Element("Table", 9, 11), Element("THead", 10), Leaf("TR"), Element("TFoot", 12), Leaf("TR"))), "UA31"));
    }

    [Fact]
    public void Ua11_LabelWithNoListBody_IsReportedAsAWarning()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("L", 9),
            /* 9  */ Element("LI", 10),
            /* 10 */ Leaf("Lbl"));

        AccessibilityIssue bad = Assert.Single(WithCode(Check(pdf), "UA11"));

        Assert.Equal(IssueSeverity.Warning, bad.Severity);
        Assert.Contains("has a Lbl (label) but no LBody", bad.Message, StringComparison.Ordinal);
        Assert.Equal(9, bad.ObjectNumber);      // reported against the LI
    }

    /// <summary>A warning, not an error: Table 336 states no order for an LI's labels and bodies.</summary>
    [Fact]
    public void Ua11_ListBodyBeforeLabel_IsReportedAsAWarning()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("L", 9),
            /* 9  */ Element("LI", 10, 11),
            /* 10 */ Leaf("LBody"),          // reversed: the bullet would be read last
            /* 11 */ Leaf("Lbl"));

        AccessibilityIssue bad = Assert.Single(WithCode(Check(pdf), "UA11"));

        Assert.Equal(IssueSeverity.Warning, bad.Severity);
        Assert.Contains("places its LBody before its Lbl", bad.Message, StringComparison.Ordinal);
        Assert.Equal(9, bad.ObjectNumber);
    }

    // =====================================================================
    //  UA12 — table regularity, after /ColSpan and /RowSpan
    // =====================================================================

    private const string ColumnScope = "/A << /O /Table /Scope /Column >> ";

    /// <summary>
    /// One header cell over two data cells and <b>no</b> <c>/ColSpan</c>: row 1 spans one column,
    /// row 2 spans two. The table cannot be navigated cell by cell.
    /// </summary>
    [Fact]
    public void Ua12_RaggedTable_IsReportedAsAnError()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11),
            /* 10 */ Element("TR", 12, 13),
            /* 11 */ Leaf("TH", ColumnScope),
            /* 12 */ Leaf("TD"),
            /* 13 */ Leaf("TD"));

        AccessibilityIssue ragged = Assert.Single(WithCode(Check(pdf), "UA12"));

        Assert.Equal(IssueSeverity.Error, ragged.Severity);
        Assert.Contains("row 1 spans 1 column(s) but row 2 spans 2", ragged.Message, StringComparison.Ordinal);
        Assert.Equal("15-003", ragged.MatterhornCheckpoint);
        Assert.Equal(8, ragged.ObjectNumber);
    }

    /// <summary>
    /// The same table with the <c>/ColSpan 2</c> the header always meant. Declaring the merge
    /// makes both rows two columns wide, and a checker that ignored /ColSpan would report this
    /// perfectly regular table.
    /// </summary>
    [Fact]
    public void Ua12_ColumnSpanningHeaderOverTwoDataCells_IsSilent()
    {
        AssertSilent(StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11),
            /* 10 */ Element("TR", 12, 13),
            /* 11 */ Leaf("TH", "/A << /O /Table /Scope /Column /ColSpan 2 >> "),
            /* 12 */ Leaf("TD"),
            /* 13 */ Leaf("TD")), "UA12");
    }

    /// <summary>
    /// A <c>/RowSpan 2</c> cell in the first column: row 2 supplies only one cell because the
    /// first column is still occupied from above. Ignoring /RowSpan would make row 2 one column
    /// wide and report the table.
    /// </summary>
    [Fact]
    public void Ua12_RowSpanningCell_IsSilent()
    {
        AssertSilent(StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11, 12),
            /* 10 */ Element("TR", 13),
            /* 11 */ Leaf("TH", "/A << /O /Table /Scope /Row /RowSpan 2 >> "),
            /* 12 */ Leaf("TD"),
            /* 13 */ Leaf("TD")), "UA12");
    }

    // =====================================================================
    //  UA12 — header addressability
    // =====================================================================

    /// <summary>
    /// A TH with no <c>/Scope</c> is still addressable when a data cell names its <c>/ID</c> in
    /// <c>/Headers</c> — the second of the two associations ISO 32000-1 §14.8.4.3.5 defines. The
    /// old check demanded <c>/Scope</c> outright and failed this document.
    /// </summary>
    [Fact]
    public void Ua12_HeaderAddressedByADataCellsHeadersArray_IsSilent()
    {
        AssertSilent(StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11),
            /* 10 */ Element("TR", 12),
            /* 11 */ Leaf("TH", "/ID (hdr1) "),
            /* 12 */ Leaf("TD", "/A << /O /Table /Headers [(hdr1)] >> ")), "UA12");
    }

    /// <summary>
    /// The same table with nothing tying the cell to the header: no <c>/Scope</c>, no
    /// <c>/ID</c>/<c>/Headers</c> pair. Nothing then associates the data cell with its header, so
    /// a screen reader cannot say which column the value belongs to.
    /// </summary>
    [Fact]
    public void Ua12_HeaderWithNeitherScopeNorAReferencingHeaders_IsReported()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11),
            /* 10 */ Element("TR", 12),
            /* 11 */ Leaf("TH"),
            /* 12 */ Leaf("TD"));

        AccessibilityIssue unaddressed = Assert.Single(WithCode(Check(pdf), "UA12"));

        Assert.Contains("neither a /Scope attribute nor an /ID", unaddressed.Message, StringComparison.Ordinal);
        Assert.Equal(11, unaddressed.ObjectNumber);
    }

    /// <summary>
    /// A TH with an /ID that no data cell quotes is not addressable on an unencrypted document: the
    /// /ID alone is not an association. The encrypted-string weakening must not reach a plain file.
    /// </summary>
    [Fact]
    public void Ua12_HeaderWithAnUnreferencedIdOnAnUnencryptedDocument_IsReported()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11),
            /* 10 */ Element("TR", 12),
            /* 11 */ Leaf("TH", "/ID (hdr1) "),
            /* 12 */ Leaf("TD"));

        AccessibilityIssue unaddressed = Assert.Single(WithCode(Check(pdf), "UA12"));

        Assert.Contains("neither a /Scope attribute nor an /ID", unaddressed.Message, StringComparison.Ordinal);
        Assert.Equal(11, unaddressed.ObjectNumber);
    }

    /// <summary>
    /// A ragged table whose data cell names a header in /Headers. Reading the /Headers array must
    /// not disturb the regularity verdict: the table is still ragged and still reported.
    /// </summary>
    [Fact]
    public void Ua12_RaggedTableWhoseDataCellNamesAHeader_IsStillReported()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10),
            /* 9  */ Element("TR", 11, 12),
            /* 10 */ Element("TR", 13),
            /* 11 */ Leaf("TH", "/ID (hdr1) "),
            /* 12 */ Leaf("TD", "/A << /O /Table /Headers [(hdr1)] >> "),
            /* 13 */ Leaf("TD"));

        AccessibilityIssue ragged = Assert.Single(WithCode(Check(pdf), "UA12"));

        Assert.Equal(IssueSeverity.Error, ragged.Severity);
        Assert.Contains("row 1 spans 2 column(s) but row 2 spans 1", ragged.Message, StringComparison.Ordinal);
        Assert.Equal(8, ragged.ObjectNumber);
    }

    /// <summary>
    /// A row-span that expires after the row it covers is measured as expired: the column it held is
    /// free again in the rows that follow. Row 3 is one cell wide only if the span ended after row 2.
    /// </summary>
    [Fact]
    public void Ua12_RowSpanEndsAfterTheRowsItCovers_LaterRowIsMeasuredWithoutIt()
    {
        byte[] pdf = StructurePdf(
            /* 7  */ Element("Document", 8),
            /* 8  */ Element("Table", 9, 10, 11),
            /* 9  */ Element("TR", 12, 13),
            /* 10 */ Element("TR", 14),
            /* 11 */ Element("TR", 15),
            /* 12 */ Leaf("TH", "/A << /O /Table /Scope /Row /RowSpan 2 >> "),
            /* 13 */ Leaf("TD"),
            /* 14 */ Leaf("TD"),
            /* 15 */ Leaf("TD"));

        AccessibilityIssue ragged = Assert.Single(WithCode(Check(pdf), "UA12"));

        Assert.Contains("row 1 spans 2 column(s) but row 3 spans 1", ragged.Message, StringComparison.Ordinal);
        Assert.Equal(8, ragged.ObjectNumber);
    }
}
