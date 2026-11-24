// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The <c>/ParentTree</c> mapping itself — ISO 32000-1 §14.7.4.4 — rather than the shape of the
/// pointers that implement it.
///
/// <para>
/// <b>The hole these lock shut.</b> If UA14 asked only "does the page's <c>/StructParents</c>
/// key exist in <c>/Nums</c>, and is its value an array", and UA16 and UA30 accepted any integer
/// in <c>/K</c> as "real content", a document could name MCIDs no page contains, point every
/// array slot at the wrong element, and leave the page's real marked content owned by nobody —
/// and the whole audit would report zero tagging findings, because every individual pointer is
/// syntactically valid. That is the hollow-tagging failure the feature exists to prevent,
/// wearing a different disguise.
/// </para>
///
/// <para>
/// Also here: the <c>/OBJR</c> whose optional <c>/Type</c> is absent (ISO 32000-1 Table 325 makes
/// only <c>/Obj</c> Required), which, invisible, would produce a false UA16 <i>and</i> a false
/// UA23 on a perfectly legal file; and an adversarial Form XObject document that would make the
/// audit's form scanning quadratic.
/// </para>
///
/// <para>
/// Every check is exercised in <b>both</b> directions — a fires case and a stays-silent twin —
/// and every fixture is built in memory as ASCII bytes. Findings are read from
/// <see cref="PdfDeepAccessibilityChecker"/> directly rather than through the combined audit, so
/// the assertions describe this checker and cannot be perturbed by the sibling plugin.
/// </para>
/// </summary>
public class ParentTreeMappingTests
{
    // =====================================================================
    //  PDF assembly helpers
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>Catalog entries that keep the unrelated document-level checks quiet.</summary>
    private const string CatalogTail =
        " /MarkInfo << /Marked true >> /Lang (en-US) "
        + "/ViewerPreferences << /DisplayDocTitle true >> >>";

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

    /// <summary>Page content drawing two tagged runs, MCID 0 and MCID 1.</summary>
    private const string TwoMarkedRuns =
        "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n" +
        "/P <</MCID 1>> BDC\nBT /F1 12 Tf 72 660 Td (Beta) Tj ET\nEMC\n";

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    private static IReadOnlyList<AccessibilityIssue> WithCode(
        IEnumerable<AccessibilityIssue> issues, string code)
        => issues.Where(i => string.Equals(i.Code, code, StringComparison.Ordinal)).ToList();

    private static string Describe(IEnumerable<AccessibilityIssue> issues)
        => string.Join(" | ", issues.Select(i => i.ToString()));

    private static bool Mentions(AccessibilityIssue issue, string fragment)
        => issue.Message != null && issue.Message.IndexOf(fragment, StringComparison.Ordinal) >= 0;

    // =====================================================================
    //  Fixture — one tagged page, two marked runs, everything configurable
    // =====================================================================

    /// <summary>
    /// A single tagged page drawing MCID 0 and MCID 1, with two paragraph elements whose
    /// <c>/K</c> values and whose <c>/ParentTree</c> array are supplied by the caller. One fixture
    /// therefore covers the correct document and every way of getting the mapping wrong, with the
    /// object numbers fixed throughout: 3 is the page, 7 the Document, 8 and 9 the paragraphs.
    /// </summary>
    /// <param name="firstKids">The <c>/K</c> value of object 8.</param>
    /// <param name="secondKids">The <c>/K</c> value of object 9, or null to omit object 9.</param>
    /// <param name="nums">The <c>/Nums</c> array of the parent tree.</param>
    /// <param name="extraObjects">Appended from object 11 on, so numbering never shifts.</param>
    private static byte[] MappingPdf(
        string firstKids, string secondKids, string nums, params string[] extraObjects)
    {
        string documentKids = secondKids == null ? "[8 0 R]" : "[8 0 R 9 0 R]";

        var bodies = new List<string>
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,

            // 2 — page tree
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

            // 3 — page
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",

            // 4 — content: MCID 0 and MCID 1, both non-artifact
            StreamObject(TwoMarkedRuns),

            // 5 — font
            HelveticaFont,

            // 6 — structure tree root
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",

            // 7 — Document
            "<< /Type /StructElem /S /Document /P 6 0 R /K " + documentKids + " >>",

            // 8 — first paragraph
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K " + firstKids + " >>",

            // 9 — second paragraph (a harmless placeholder when it is not wanted, so that
            //     object 10 is always the parent tree)
            secondKids == null
                ? "<< /Type /Null >>"
                : "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K " + secondKids + " >>",

            // 10 — parent tree
            "<< /Nums " + nums + " >>",
        };

        bodies.AddRange(extraObjects);
        return BuildPdf(bodies);
    }

    // =====================================================================
    //  The correct document must stay silent
    // =====================================================================

    /// <summary>
    /// The direction that matters most. Both MCIDs exist, each element claims exactly one of
    /// them, and the array's index i names the element that owns MCID i — so not one of the three
    /// new sub-checks may say anything.
    /// </summary>
    [Fact]
    public void Check_CorrectlyMappedDocument_ReportsNoUa14()
    {
        var issues = Check(MappingPdf("0", "1", "[0 [8 0 R 9 0 R]]"));

        Assert.True(WithCode(issues, "UA14").Count == 0,
            "a correctly mapped document must produce no UA14 at all; a checker that fires on good " +
            "files trains people to ignore the report. Issues: " + Describe(issues));
        Assert.Empty(WithCode(issues, "UA16"));
        Assert.Empty(WithCode(issues, "UA30"));
    }

    // =====================================================================
    //  1. An element names an MCID the page does not contain
    // =====================================================================

    /// <summary>
    /// <b>The gate's "BROKEN MCID mapping" probe.</b> Both paragraphs name marked-content
    /// sequences the page never draws, and the array points both slots at the Document, which
    /// claims no MCID at all. Every pointer is individually well-formed — which is exactly why
    /// UA16 (a bare integer is "content"), UA17 (the text <i>is</i> inside a BDC), UA18
    /// (/StructParents is present) and the old UA14 (the key resolves to an array) were all
    /// silent, and the file audited clean while its structure tree was wired to nothing.
    /// </summary>
    [Fact]
    public void Check_ElementsNamingMcidsThePageDoesNotContain_AreReportedAsErrors()
    {
        var issues = Check(MappingPdf("7", "42", "[0 [7 0 R 7 0 R]]"));

        IReadOnlyList<AccessibilityIssue> phantom = WithCode(issues, "UA14")
            .Where(i => Mentions(i, "contains no such marked-content sequence")).ToList();

        Assert.Equal(2, phantom.Count);
        Assert.All(phantom, i => Assert.Equal(IssueSeverity.Error, i.Severity));

        List<int> objects = phantom
            .Where(i => i.ObjectNumber.HasValue).Select(i => i.ObjectNumber.Value)
            .OrderBy(n => n).ToList();
        Assert.Equal(new[] { 8, 9 }, objects);

        Assert.Contains(phantom, i => Mentions(i, "MCID 7 on page 1"));
        Assert.Contains(phantom, i => Mentions(i, "MCID 42 on page 1"));
        Assert.All(phantom, i => Assert.Equal(1, i.PageNumber));

        // The other half of the same defect: the content the page really draws belongs to nothing.
        Assert.Contains(WithCode(issues, "UA14"),
            i => Mentions(i, "no structure element claims"));

        // And the other checks stay silent, which is the point — a bare integer is enough to
        // satisfy every one of them.
        Assert.Empty(WithCode(issues, "UA16"));
        Assert.Empty(WithCode(issues, "UA17"));
        Assert.Empty(WithCode(issues, "UA18"));
    }

    /// <summary>
    /// The audit as a whole must fail this document. Reporting nothing about the tagging would let
    /// a file whose structure tree reaches no content be presented as conforming once its title and
    /// XMP were in place.
    /// </summary>
    [Fact]
    public void Check_BrokenMcidMapping_ProducesBlockingErrors()
    {
        var issues = Check(MappingPdf("7", "42", "[0 [7 0 R 7 0 R]]"));

        Assert.Contains(issues, i => i.Code == "UA14" && i.Severity == IssueSeverity.Error);
    }

    // =====================================================================
    //  2. The array slot does not lead back to the element
    // =====================================================================

    /// <summary>
    /// Both MCIDs exist and both are claimed — but the array's two slots are swapped, so starting
    /// from MCID 0 leads to the element that owns MCID 1. This is the case that needs both sides
    /// of the relation; nothing about either element or either slot is wrong in isolation.
    /// </summary>
    [Fact]
    public void Check_ParentTreeSlotsNamingTheWrongElement_AreReportedAsErrors()
    {
        var issues = Check(MappingPdf("0", "1", "[0 [9 0 R 8 0 R]]"));

        IReadOnlyList<AccessibilityIssue> mismatched = WithCode(issues, "UA14")
            .Where(i => Mentions(i, "names a different element")).ToList();

        Assert.Equal(2, mismatched.Count);
        Assert.All(mismatched, i => Assert.Equal(IssueSeverity.Error, i.Severity));

        List<int> objects = mismatched
            .Where(i => i.ObjectNumber.HasValue).Select(i => i.ObjectNumber.Value)
            .OrderBy(n => n).ToList();
        Assert.Equal(new[] { 8, 9 }, objects);

        // Every MCID is claimed by somebody, so the orphan half must not fire.
        Assert.DoesNotContain(WithCode(issues, "UA14"),
            i => Mentions(i, "no structure element claims"));
    }

    /// <summary>
    /// An array shorter than the MCIDs the tree names: index 1 simply does not exist, so MCID 1's
    /// content can never be mapped back. Reported against the element that named it, with the
    /// array's real length in the message.
    /// </summary>
    [Fact]
    public void Check_ParentTreeArrayShorterThanTheMcidsClaimed_IsReportedAsAnError()
    {
        var issues = Check(MappingPdf("0", "1", "[0 [8 0 R]]"));

        AccessibilityIssue tooShort = Assert.Single(WithCode(issues, "UA14")
            .Where(i => Mentions(i, "the array holds only")));

        Assert.Equal(IssueSeverity.Error, tooShort.Severity);
        Assert.Equal(9, tooShort.ObjectNumber);
        Assert.Equal(1, tooShort.PageNumber);
        Assert.True(Mentions(tooShort, "1 entry"), "got: " + tooShort.Message);
    }

    // =====================================================================
    //  3. Marked content no element claims
    // =====================================================================

    /// <summary>
    /// The page draws two marked-content sequences and the tree claims one. MCID 1 is tagged in
    /// the stream and owned by nothing, so a screen reader never reaches it — invisible to every
    /// other check, because the element that <i>does</i> exist is perfectly correct.
    /// </summary>
    [Fact]
    public void Check_MarkedContentNoElementClaims_IsReportedAsAnError()
    {
        var issues = Check(MappingPdf("0", secondKids: null, nums: "[0 [8 0 R 8 0 R]]"));

        AccessibilityIssue orphan = Assert.Single(WithCode(issues, "UA14")
            .Where(i => Mentions(i, "no structure element claims")));

        Assert.Equal(IssueSeverity.Error, orphan.Severity);
        Assert.Equal(1, orphan.PageNumber);
        Assert.True(Mentions(orphan, "MCID 1"), "got: " + orphan.Message);
    }

    /// <summary>
    /// Artifacts are deliberately unreachable from the structure tree (ISO 32000-1 §14.8.2), so an
    /// artifact sequence is neither claimable nor an orphan. A checker that counted it would fail
    /// every correctly tagged document with a running header.
    /// </summary>
    [Fact]
    public void Check_ArtifactMarkedContent_IsNotReportedAsUnclaimed()
    {
        const string content =
            "/Artifact <</Type /Pagination /Subtype /Header>> BDC\n"
            + "BT /F1 9 Tf 72 760 Td (Running header) Tj ET\nEMC\n"
            + "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n";

        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
            StreamObject(content),
            HelveticaFont,
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [8 0 R]] >>",
        });

        Assert.Empty(WithCode(Check(pdf), "UA14"));
    }

    /// <summary>
    /// An <c>/MCR</c> carrying <c>/Stm</c> names an MCID in a stream this checker never
    /// tokenised, so the page's marked content can no longer be partitioned into "claimed" and
    /// "orphaned" — and the reverse check must therefore say nothing rather than guess. Guessing
    /// here would report a legal file (content split across a form) as broken.
    /// </summary>
    [Fact]
    public void Check_ClaimThroughAnMcrWithAForeignStream_SuppressesTheOrphanFinding()
    {
        var issues = Check(MappingPdf(
            "0",
            "<< /Type /MCR /Pg 3 0 R /Stm 11 0 R /MCID 5 >>",
            "[0 [8 0 R 9 0 R]]",
            StreamObject("0 0 1 1 re\nn\n")));   // 11 — the foreign content stream

        Assert.Empty(WithCode(issues, "UA14"));
    }

    // =====================================================================
    //  3b. A skipped page does not end the check
    // =====================================================================

    /// <summary>
    /// Two tagged pages drawing MCID 0 and 1 each. One paragraph (object 10) claims page 1's two
    /// MCIDs; nothing claims page 2's, so page 2 always has orphaned content to report. Page 1 can
    /// be made a page the checks skip: it invokes a form XObject, it has no
    /// <c>/StructParents</c>, or its element claims through a foreign stream.
    /// </summary>
    private static byte[] TwoPagePdf(bool page1InvokesForm = false, bool page1WithoutKey = false,
        bool page1ForeignStream = false)
    {
        string page1Content = TwoMarkedRuns + (page1InvokesForm ? "/Fm1 Do\n" : "");
        string paragraphKids = page1ForeignStream
            ? "[0 1 << /Type /MCR /Pg 3 0 R /Stm 12 0 R /MCID 5 >>]"
            : "[0 1]";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 8 0 R" + CatalogTail,
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >>"
                + (page1InvokesForm ? " /XObject << /Fm1 12 0 R >>" : "") + " >> /Contents 5 0 R"
                + (page1WithoutKey ? "" : " /StructParents 0") + " /Tabs /S >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >>"
                + " /Contents 6 0 R /StructParents 1 /Tabs /S >>",
            StreamObject(page1Content),
            StreamObject(TwoMarkedRuns),
            HelveticaFont,
            "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 11 0 R /ParentTreeNextKey 2 >>",
            "<< /Type /StructElem /S /Document /P 8 0 R /K [10 0 R] >>",
            "<< /Type /StructElem /S /P /P 9 0 R /Pg 3 0 R /K " + paragraphKids + " >>",
            "<< /Nums [0 [10 0 R 10 0 R] 1 [null null]] >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 0 >>\nstream\nendstream",
        };
        return BuildPdf(bodies);
    }

    private static bool Page2OrphanReported(IReadOnlyList<AccessibilityIssue> issues)
        => WithCode(issues, "UA14").Any(i => Mentions(i, "no structure element claims") && i.PageNumber == 2);

    /// <summary>
    /// Page 2's orphaned content is reported whatever kept page 1 out of a check: the skip is
    /// that page's, not the end of the loop. The plain two-page document is the control.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Check_APageSkippedByTheMappingDoesNotHideTheNextPagesFindings(
        bool invokesForm, bool withoutKey, bool foreignStream)
    {
        var issues = Check(TwoPagePdf(invokesForm, withoutKey, foreignStream));
        Assert.True(Page2OrphanReported(issues), Describe(WithCode(issues, "UA14")));
    }

    /// <summary>
    /// An element whose claim names no page (no <c>/Pg</c> anywhere) is passed over, and the claims
    /// after it are still judged: the one after it names MCID 5, which page 1 does not draw.
    /// </summary>
    [Fact]
    public void Check_AClaimWithNoPageDoesNotStopTheClaimsAfterIt()
    {
        var issues = Check(BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
            StreamObject(TwoMarkedRuns),
            HelveticaFont,
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /K << /Type /MCR /MCID 0 >> >>",   // no /Pg anywhere
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K [1 5] >>",             // MCID 5 is not drawn
            "<< /Nums [0 [8 0 R 9 0 R]] >>",
        }));

        Assert.Contains(WithCode(issues, "UA14"), i => Mentions(i, "MCID 5"));
    }

    /// <summary>
    /// A <c>/ParentTree</c> slot holding <c>null</c>, or something that is not an element at all (a
    /// number), names no element, so the MCID it should lead back from is reported as not mapped
    /// back.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    public void Check_AParentTreeSlotThatIsNoElementIsNotTheClaimingElement(string slot)
    {
        var issues = Check(MappingPdf("0", "1", "[0 [8 0 R " + slot + "]]"));

        Assert.Contains(WithCode(issues, "UA14"), i => Mentions(i, "MCID 1") && Mentions(i, "names a different element"));
    }

    // =====================================================================
    //  4. The conservative gates — a page this checker cannot read is never accused
    // =====================================================================

    /// <summary>
    /// A named property list (<c>/P /Pr1 BDC</c>) whose <c>/Properties</c> entry is missing means
    /// the MCID could not be <i>read</i>, not that it is absent. The page is excluded from the
    /// mapping checks entirely: reporting it would be reporting the checker's own blind spot as a
    /// defect in the document.
    /// </summary>
    [Fact]
    public void Check_UnreadableNamedPropertyList_ExcludesThePageInsteadOfAccusingIt()
    {
        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

            // No /Properties in the resource dictionary, so /Pr1 cannot be resolved.
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",

            StreamObject("/P /Pr1 BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n"),
            HelveticaFont,
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",

            // Claims MCID 0 — which may well be right; the checker simply cannot tell.
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [8 0 R]] >>",
        });

        Assert.Empty(WithCode(Check(pdf), "UA14"));
    }

    // =====================================================================
    //  5. /OBJR with no /Type (ISO 32000-1 Table 325: /Type is Optional)
    // =====================================================================

    /// <summary>
    /// A tagged page with one Link annotation wrapped in a <c>&lt;Link&gt;</c> element whose only
    /// child is an object reference. <paramref name="objrType"/> is spliced in, so the typed and
    /// typeless forms differ by exactly those bytes.
    /// </summary>
    private static byte[] LinkWithObjrPdf(string objrType) => BuildPdf(new List<string>
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R" + CatalogTail,

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [6 0 R] "
        + "/StructParents 0 /Tabs /S >>",

        // 4 — content: one tagged run, MCID 0
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n"),

        // 5 — font
        HelveticaFont,

        // 6 — the link annotation, with its own /StructParent and an alternate description
        "<< /Type /Annot /Subtype /Link /Rect [72 700 260 720] /Border [0 0 0] "
        + "/StructParent 1 /Contents (The Tetitulz home page) >>",

        // 7 — structure tree root
        "<< /Type /StructTreeRoot /K [8 0 R] /ParentTree 11 0 R /ParentTreeNextKey 2 >>",

        // 8 — Document
        "<< /Type /StructElem /S /Document /P 7 0 R /K [9 0 R 10 0 R] >>",

        // 9 — the paragraph that owns MCID 0
        "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K 0 >>",

        // 10 — the Link element: its whole content is the object reference
        "<< /Type /StructElem /S /Link /P 8 0 R /Pg 3 0 R /Alt (Tetitulz home page) "
        + "/K << " + objrType + "/Pg 3 0 R /Obj 6 0 R >> >>",

        // 11 — parent tree: page key 0 -> the MCID array, annotation key 1 -> its element
        "<< /Nums [0 [9 0 R] 1 10 0 R] >>",
    });

    [Fact]
    public void Check_ObjrWithType_IsRecognised()
    {
        var issues = Check(LinkWithObjrPdf("/Type /OBJR "));

        Assert.Empty(WithCode(issues, "UA16"));
        Assert.Empty(WithCode(issues, "UA23"));
        Assert.Empty(WithCode(issues, "UA30"));
    }

    /// <summary>
    /// <b>The false positive.</b> ISO 32000-1 Table 325 marks <c>/Type</c> on an object reference
    /// dictionary Optional — only <c>/Obj</c> is Required — so this document is exactly as legal
    /// as the typed one above. Requiring <c>/Type</c> would accuse it twice: the
    /// <c>&lt;Link&gt;</c> wrapper as a leaf referencing no content (UA16), and the annotation it
    /// wraps as reachable from no <c>/OBJR</c> (UA23), with remediation advice telling the author
    /// to add an <c>/OBJR</c> that is already there.
    /// </summary>
    [Fact]
    public void Check_ObjrWithoutType_IsStillRecognised()
    {
        var issues = Check(LinkWithObjrPdf(string.Empty));

        Assert.Empty(WithCode(issues, "UA16"));
        Assert.Empty(WithCode(issues, "UA23"));
        Assert.Empty(WithCode(issues, "UA30"));
    }

    /// <summary>
    /// The typed and typeless forms describe the same document, so they must produce the same
    /// findings — stated as a set comparison so any future divergence is caught whichever check
    /// introduces it.
    /// </summary>
    [Fact]
    public void Check_TypelessObjr_ProducesTheSameFindingsAsTheTypedForm()
    {
        string typed = string.Join(",",
            Check(LinkWithObjrPdf("/Type /OBJR ")).Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal));
        string typeless = string.Join(",",
            Check(LinkWithObjrPdf(string.Empty)).Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal));

        Assert.Equal(typed, typeless);
    }

    /// <summary>
    /// The opposite direction: a <c>/K</c> dictionary that is neither an <c>/MCR</c> nor an
    /// <c>/OBJR</c> — no <c>/Obj</c>, no <c>/MCID</c>, no <c>/S</c> — must still be reported.
    /// Recognising the typeless <c>/OBJR</c> must not turn "any dictionary counts as content"
    /// into the new rule.
    /// </summary>
    [Fact]
    public void Check_KidDictionaryThatIsNeitherMcrNorObjr_IsStillAnEmptyLeaf()
    {
        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
            StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n"),
            HelveticaFont,
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",

            // 9 — a paragraph whose /K names a dictionary that references nothing at all.
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K << /Foo (bar) >> >>",

            "<< /Nums [0 [8 0 R]] >>",
        });

        AccessibilityIssue empty = Assert.Single(WithCode(Check(pdf), "UA16"));
        Assert.Equal(9, empty.ObjectNumber);
    }

    // =====================================================================
    //  6. Adversarial input — Form XObject scanning must not be quadratic
    // =====================================================================

    /// <summary>
    /// Wall-clock budget for the adversarial audit. Generous on purpose: the assertion that
    /// actually proves the guard exists is the diagnostic below, and the timeout is only there so
    /// an unbounded walk fails the test instead of hanging the run. The audit takes about a second
    /// alone and over a minute beside a whole solution's test hosts, so the bound is ten minutes:
    /// a walk the guard no longer stops still fails the diagnostic assertion, whatever the clock says.
    /// </summary>
    private const int FormScanBudgetMs = 600_000;

    /// <summary>
    /// <b>The quadratic document.</b> <paramref name="count"/> pages that all share <em>one</em>
    /// <c>/Contents</c> object and <em>one</em> <c>/Resources</c> object; that single content
    /// stream invokes <paramref name="count"/> small Form XObjects. The file is O(n) bytes, and
    /// without a cache of the scan result the audit would do O(n²) full re-tokenisations, because
    /// the form-visited set is per page: the same form tokenised once per page, from scratch, for
    /// every page in the document.
    /// </summary>
    private static byte[] SharedFormsPdf(int count)
    {
        int resourcesNumber = 4;
        int firstPageNumber = 5;
        int firstFormNumber = firstPageNumber + count;

        var content = new StringBuilder(count * 10);
        var xobjects = new StringBuilder(count * 20);
        for (int i = 0; i < count; i++)
        {
            content.Append("/F").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" Do\n");
            xobjects.Append("/F").Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append((firstFormNumber + i).ToString(CultureInfo.InvariantCulture))
                    .Append(" 0 R ");
        }

        var kids = new StringBuilder(count * 8);
        for (int i = 0; i < count; i++)
            kids.Append((firstPageNumber + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");

        var bodies = new List<string>(4 + 2 * count)
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R >>",

            // 2 — page tree
            "<< /Type /Pages /Kids [" + kids.ToString().TrimEnd() + "] /Count "
            + count.ToString(CultureInfo.InvariantCulture) + " >>",

            // 3 — the one content stream every page points at
            StreamObject(content.ToString()),

            // 4 — the one resource dictionary every page points at
            "<< /XObject << " + xobjects.ToString().TrimEnd() + " >> >>",
        };

        for (int i = 0; i < count; i++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Resources "
                       + resourcesNumber.ToString(CultureInfo.InvariantCulture) + " 0 R >>");

        // Each form is a small vector drawing: no text, no images, ~120 operators.
        var formContent = new StringBuilder(13 * 60);
        for (int i = 0; i < 60; i++) formContent.Append("0 0 1 1 re\nn\n");
        string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length "
                      + formContent.Length.ToString(CultureInfo.InvariantCulture)
                      + " >>\nstream\n" + formContent + "endstream";

        for (int i = 0; i < count; i++) bodies.Add(form);

        return BuildPdf(bodies);
    }

    /// <summary>
    /// CLAUDE.md requires an explicit adversarial-input test for every DoS guard. This document
    /// asks the audit for 500 x 500 = 250 000 form visits over ~30 million operator tokens from a
    /// file well under a megabyte. Two things must hold: the audit finishes, and it says out loud
    /// that it stopped early — a walk that silently truncated its counts would let UA24 and UA17
    /// rest on numbers the checker knows are incomplete.
    /// </summary>
    [Fact]
    public void Check_ManyPagesSharingManyFormXObjects_StopsAtItsBudgetAndSaysSo()
    {
        byte[] pdf = SharedFormsPdf(500);

        var messages = new List<string>();
        int workerThreadId = 0;

        void Handler(string message)
        {
            if (Volatile.Read(ref workerThreadId) != Environment.CurrentManagedThreadId) return;
            lock (messages) messages.Add(message);
        }

        IReadOnlyList<AccessibilityIssue> issues = null;
        Exception failure = null;

        AccessibilityDiagnostics.OnEvent += Handler;
        try
        {
            var worker = new Thread(() =>
            {
                Volatile.Write(ref workerThreadId, Environment.CurrentManagedThreadId);
                try { issues = Check(pdf); }
                catch (Exception ex) { failure = ex; }
            })
            { IsBackground = true };

            worker.Start();
            Assert.True(worker.Join(FormScanBudgetMs),
                "the audit did not finish within " +
                FormScanBudgetMs.ToString(CultureInfo.InvariantCulture) +
                " ms, which is what re-tokenising every shared Form XObject once per page looks like");
        }
        finally
        {
            AccessibilityDiagnostics.OnEvent -= Handler;
        }

        Assert.Null(failure);
        Assert.NotNull(issues);

        string[] seen;
        lock (messages) seen = messages.ToArray();

        Assert.Contains(seen, m => m != null &&
            m.IndexOf("ceiling on Form XObject content", StringComparison.Ordinal) >= 0);

        // Stopping early must never turn into a finding: the pages whose counts are incomplete
        // are excluded from the checks that rest on them, not accused on partial data.
        Assert.Empty(WithCode(issues, "UA24"));
    }

    /// <summary>
    /// The other direction, and the one that proves the cache is a cache and not a bug. Three
    /// pages share one content stream that draws an image and invokes one shared Form XObject
    /// holding the page's only text. Every page must still be seen to draw text: a cache that
    /// handed the second and third pages an empty or already-consumed token list would lose that
    /// text, and each of those pages — drawing an image and, apparently, nothing else — would be
    /// reported as a scanned page by UA24.
    /// </summary>
    [Fact]
    public void Check_PagesSharingAFormXObject_StillCountItsTextOnEveryPage()
    {
        string formContent = "BT /F1 12 Tf 72 700 Td (Text inside the form) Tj ET\n";
        string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] "
                      + "/Resources << /Font << /F1 4 0 R >> >> /Length "
                      + formContent.Length.ToString(CultureInfo.InvariantCulture)
                      + " >>\nstream\n" + formContent + "endstream";

        const string pageBody =
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 3 0 R "
            + "/Resources << /Font << /F1 4 0 R >> /XObject << /F0 5 0 R /Im0 9 0 R >> >> >>";

        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [6 0 R 7 0 R 8 0 R] /Count 3 >>",

            // 3 — the one content stream: an image, and the text-bearing form.
            StreamObject("q 10 0 0 10 0 0 cm /Im0 Do Q\n/F0 Do\n"),

            HelveticaFont,   // 4
            form,            // 5 — the one shared form

            pageBody,        // 6
            pageBody,        // 7
            pageBody,        // 8

            // 9 — a tiny image, so a page whose form text went missing WOULD be reported by UA24.
            StreamObject2("/Type /XObject /Subtype /Image /Width 2 /Height 2 "
                          + "/ColorSpace /DeviceGray /BitsPerComponent 8", "abcd"),
        });

        var issues = Check(pdf);

        Assert.Empty(WithCode(issues, "UA24"));
        Assert.DoesNotContain(issues, i => i.Code == "UA99");
    }

    /// <summary>A stream object with caller-supplied dictionary entries.</summary>
    private static string StreamObject2(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + data + "\nendstream";
}
