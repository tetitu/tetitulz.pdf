// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Read-back tests for <c>PdfAccessibility.FixHeadingHierarchy</c>.
///
/// <para>
/// The step had no test that re-opened its output: every assertion was on the returned count or on
/// the pipeline's skip reason, so "renumbered 2 headings" was believed on the strength of the
/// number the method itself returned. A renumbering function whose <i>result</i> is never parsed is
/// untested in the only way that matters — a fixer that returns 2 and writes <c>H4</c>, or writes a
/// revision the parser cannot read back, passes every count-based test. Each test here therefore
/// re-opens the produced bytes and reads the <c>/S</c> values out of the written structure tree.
/// </para>
///
/// <para>
/// The last test is the deduplication invariant: the traversal that decides whether the pipeline
/// runs this step and the traversal that rewrites the tags are one function
/// (<c>Internal.HeadingHierarchyPlanner</c>), and this asserts the property that made sharing them
/// worth doing — the gate's verdict and the fixer's output agree on the same document, both in
/// whether anything changes and in what the document ends up containing.
/// </para>
///
/// <para>Every fixture is assembled in memory as a <c>byte[]</c>; nothing is read from disk.</para>
/// </summary>
public class HeadingHierarchyTests
{
    // =====================================================================
    //  Structure-tree fixture builder
    // =====================================================================

    /// <summary>One structure element in a fixture tree. Leaves own one marked-content sequence.</summary>
    private sealed class El
    {
        internal readonly string Role;
        internal readonly List<El> Kids;

        internal int ObjectNumber;
        internal int ParentObjectNumber;
        internal int Mcid = -1;

        internal El(string role, params El[] kids)
        {
            Role = role;
            Kids = new List<El>(kids ?? Array.Empty<El>());
        }
    }

    private static El E(string role, params El[] kids) => new El(role, kids);

    /// <summary>A <c>&lt;Document&gt;</c> holding one element per role, in order.</summary>
    private static El Doc(params string[] roles)
        => new El("Document", roles.Select(r => new El(r)).ToArray());

    private const string Helvetica =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>
    /// Builds a single-page tagged PDF whose structure tree is <paramref name="roots"/>.
    ///
    /// <para>
    /// Objects 1–7 are fixed (catalog, page tree, page, contents, font, <c>/StructTreeRoot</c>,
    /// <c>/ParentTree</c>); structure elements are numbered from 8 upward in document order, so
    /// every element is a real indirect object the incremental writer can revise. Each leaf owns
    /// one marked-content sequence on the page and one <c>/ParentTree</c> entry, so the tree really
    /// does reach content.
    /// </para>
    /// </summary>
    private static byte[] StructurePdf(params El[] roots)
    {
        var flat = new List<El>();
        int nextObject = 8;
        int nextMcid = 0;

        void Assign(El el, int parentObjectNumber)
        {
            el.ObjectNumber = nextObject++;
            el.ParentObjectNumber = parentObjectNumber;
            flat.Add(el);
            foreach (El kid in el.Kids) Assign(kid, el.ObjectNumber);
            if (el.Kids.Count == 0) el.Mcid = nextMcid++;
        }

        foreach (El root in roots) Assign(root, 6);

        // Leaves in MCID order — Assign hands them out in document order, so this is that order.
        List<El> leaves = flat.Where(e => e.Mcid >= 0).OrderBy(e => e.Mcid).ToList();

        var content = new StringBuilder();
        for (int i = 0; i < leaves.Count; i++)
        {
            content.Append("/P <</MCID ").Append(leaves[i].Mcid.ToString(CultureInfo.InvariantCulture))
                   .Append(">> BDC\nBT /F1 12 Tf 72 ")
                   .Append((720 - 18 * i).ToString(CultureInfo.InvariantCulture))
                   .Append(" Td (Block ").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                   .Append(") Tj ET\nEMC\n");
        }

        string rootKids = string.Join(" ",
            roots.Select(r => r.ObjectNumber.ToString(CultureInfo.InvariantCulture) + " 0 R"));

        string nums = string.Join(" ",
            leaves.Select(l => l.ObjectNumber.ToString(CultureInfo.InvariantCulture) + " 0 R"));

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R "
                + "/MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(content.ToString()),
            Helvetica,
            "<< /Type /StructTreeRoot /K [" + rootKids + "] /ParentTree 7 0 R /ParentTreeNextKey 1 >>",
            "<< /Nums [0 [" + nums + "]] >>",
        };

        foreach (El el in flat)
        {
            string kids = el.Kids.Count == 0
                ? el.Mcid.ToString(CultureInfo.InvariantCulture)
                : "[" + string.Join(" ", el.Kids.Select(
                      k => k.ObjectNumber.ToString(CultureInfo.InvariantCulture) + " 0 R")) + "]";

            bodies.Add("<< /Type /StructElem /S /" + el.Role + " /P "
                     + el.ParentObjectNumber.ToString(CultureInfo.InvariantCulture)
                     + " 0 R /Pg 3 0 R /K " + kids + " >>");
        }

        return BuildPdf(bodies);
    }

    private static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(128 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // =====================================================================
    //  Read-back
    // =====================================================================

    /// <summary>
    /// Re-opens <paramref name="pdf"/> and returns the <c>/S</c> of every structure element below
    /// <c>/StructTreeRoot</c>, in document order. This is the whole point of these tests: the
    /// values come out of the produced bytes, not out of the fixer's return value.
    /// </summary>
    private static IReadOnlyList<string> RolesOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;
        Assert.NotNull(root);

        var roles = new List<string>();
        Walk(root, 0);
        return roles;

        void Walk(PdfObject obj, int depth)
        {
            if (depth > 32) return;   // fixtures are shallow; a cap keeps a bad parse from hanging

            if (!(doc.ResolveObject(obj) is PdfDictionary dict)) return;

            string role = (doc.ResolveObject(dict[new PdfName("S")]) as PdfName)?.Value;
            if (role != null) roles.Add(role);

            PdfObject kids = doc.ResolveObject(dict[new PdfName("K")]);
            if (kids is PdfArray array)
                for (int i = 0; i < array.Count; i++) Walk(array[i], depth + 1);
            else if (kids is PdfDictionary)
                Walk(kids, depth + 1);
        }
    }

    private static int FixHeadings(byte[] input, out byte[] output)
    {
        using var inMs = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        int changed = PdfAccessibility.FixHeadingHierarchy(inMs, outMs);
        output = outMs.ToArray();
        return changed;
    }

    /// <summary>Runs the remediation pipeline with every pass but the heading pass disabled.</summary>
    private static RemediationReport RemediateHeadingsOnly(byte[] input, out byte[] output, int part = 1)
    {
        using var inMs = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inMs, outMs,
            new RemediationOptions
            {
                PdfUaPart = part,
                Language = null,
                Title = null,
                AutoTagIfUntagged = false,
                MarkArtifacts = false,
                FixReadingOrder = false,
                SetFigureAltText = false,
                FixTabOrder = false,
                SetPdfUaIdentifier = false,
            });
        output = outMs.ToArray();
        return report;
    }

    private static RemediationStepResult HeadingStep(RemediationReport report)
        => Assert.Single(report.Steps.Where(s => s.Name == "FixHeadingHierarchy"));

    // =====================================================================
    //  1 — a skipped level is closed up
    // =====================================================================

    [Fact]
    public void FixHeadingHierarchy_H1ThenH3_WritesH1ThenH2()
    {
        int changed = FixHeadings(StructurePdf(Doc("H1", "H3")), out byte[] output);

        Assert.Equal(1, changed);
        Assert.Equal(new[] { "Document", "H1", "H2" }, RolesOf(output));
    }

    /// <summary>
    /// The no-skip rule is ISO 14289-1 7.4.2's; ISO 14289-2 §8.2.5.12 says it does not include one.
    /// So a part-2 run keeps the author's H1, H3 and says why, while a part-1 run (the control)
    /// closes the gap.
    /// </summary>
    [Fact]
    public void Remediate_Part2_KeepsTheAuthorsHeadingLevels_Part1ClosesTheGap()
    {
        byte[] input = StructurePdf(Doc("H1", "H3"));

        RemediationReport part2 = RemediateHeadingsOnly(input, out byte[] part2Output, part: 2);
        RemediationReport part1 = RemediateHeadingsOnly(input, out byte[] part1Output, part: 1);

        RemediationStepResult kept = HeadingStep(part2);
        Assert.True(kept.Skipped);
        Assert.Contains("ISO 14289-2 §8.2.5.12", kept.SkipReason, StringComparison.Ordinal);
        Assert.Equal(new[] { "Document", "H1", "H3" }, RolesOf(part2Output));

        Assert.True(HeadingStep(part1).Applied);
        Assert.Equal(new[] { "Document", "H1", "H2" }, RolesOf(part1Output));
    }

    [Fact]
    public void FixHeadingHierarchy_LongSkippingSequence_WritesAnUnbrokenSequence()
    {
        // H1, H4, H6, H2, H5 → H1, H2, H3, H2, H3: each heading may descend at most one level
        // below the level the OUTPUT last carried, and may rise to any level freely.
        int changed = FixHeadings(StructurePdf(Doc("H1", "H4", "H6", "H2", "H5")), out byte[] output);

        Assert.Equal(new[] { "Document", "H1", "H2", "H3", "H2", "H3" }, RolesOf(output));
        Assert.Equal(3, changed);   // H4→H2, H6→H3, H5→H3; the H1 and the H2 were already right
    }

    // =====================================================================
    //  2 — a document that starts below H1 is promoted
    // =====================================================================

    [Fact]
    public void FixHeadingHierarchy_DocumentStartingAtH3_PromotesTheFirstHeadingToH1()
    {
        int changed = FixHeadings(StructurePdf(Doc("H3", "H4", "H5")), out byte[] output);

        // The first numbered heading is always H1 — a document whose outline starts at H3 has
        // skipped two levels before it has said anything.
        Assert.Equal(new[] { "Document", "H1", "H2", "H3" }, RolesOf(output));
        Assert.Equal(3, changed);
    }

    // =====================================================================
    //  3 — PDF/UA "strong" structure is not touched
    // =====================================================================

    [Fact]
    public void FixHeadingHierarchy_StrongHeadingsInsideSect_AreLeftExactlyAsTheyAre()
    {
        // <H> takes its level from /Sect nesting, so renumbering it to H1/H2/… would destroy the
        // very structure that makes the document conforming. Nothing here is numbered, so nothing
        // is renumbered — and no revision is appended.
        byte[] input = StructurePdf(
            E("Document",
                E("Sect", E("H"), E("P")),
                E("Sect",
                    E("H"),
                    E("Sect", E("H"), E("P")))));

        int changed = FixHeadings(input, out byte[] output);

        Assert.Equal(0, changed);
        Assert.Equal(
            new[] { "Document", "Sect", "H", "P", "Sect", "H", "Sect", "H", "P" },
            RolesOf(output));

        // A no-op edit must not cost the caller an appended revision.
        Assert.Equal(input, output);
    }

    // =====================================================================
    //  4 — a mixed weak/strong document renumbers the numbered elements only
    // =====================================================================

    [Fact]
    public void FixHeadingHierarchy_MixedWeakAndStrong_RenumbersOnlyTheNumberedElements()
    {
        // Documented behaviour: <H> is not renumbered AND does not advance the running level, so
        // the numbered headings are renumbered among themselves as if the <H> were not there.
        // H1 stays H1; the <H> is skipped; H3 follows a level-1 heading and becomes H2.
        byte[] input = StructurePdf(
            E("Document",
                E("H1"),
                E("Sect", E("H"), E("P")),
                E("H3")));

        int changed = FixHeadings(input, out byte[] output);

        Assert.Equal(1, changed);
        Assert.Equal(
            new[] { "Document", "H1", "Sect", "H", "P", "H2" },
            RolesOf(output));
    }

    [Fact]
    public void FixHeadingHierarchy_StrongHeadingBeforeANumberedOne_DoesNotCountAsAnH1()
    {
        // The mirror case, and the one easy to get wrong: if <H> were folded in as level 1,
        // the following H2 would look legal and be left alone. It is not — H2 is the FIRST
        // numbered heading, so it must become H1.
        int changed = FixHeadings(StructurePdf(Doc("H", "H2")), out byte[] output);

        Assert.Equal(1, changed);
        Assert.Equal(new[] { "Document", "H", "H1" }, RolesOf(output));
    }

    // =====================================================================
    //  5 — the analysis and the fix agree
    // =====================================================================

    [Theory]
    // Already-unbroken sequences: the gate must skip and the fixer must change nothing.
    [InlineData("H1")]
    [InlineData("H1", "H2", "H3")]
    [InlineData("H1", "H1", "H2")]
    [InlineData("H1", "H2", "H1")]
    [InlineData("H")]
    [InlineData("H", "H")]
    [InlineData("H1", "H2", "H", "H3")]
    [InlineData("P", "P")]
    // Broken sequences: the gate must run the step and the fixer must rewrite.
    [InlineData("H3")]
    [InlineData("H1", "H3")]
    [InlineData("H2", "H4")]
    [InlineData("H", "H2")]
    [InlineData("H1", "H2", "H", "H4")]
    [InlineData("H1", "H4", "H6", "H2", "H5")]
    public void HeadingAnalysisAndHeadingFix_AgreeOnTheSameDocument(params string[] roles)
    {
        // The property the shared traversal exists to guarantee. Two answers are derived from one
        // structure tree — the pipeline's pre-flight "would this step change anything?" and the
        // fixer's actual rewrite — and a disagreement is a false report in one direction and a
        // lost repair in the other. Asserted on the REWRITTEN document, not just on the counts:
        // agreeing that something must change while producing different documents is the same bug
        // one step later.
        byte[] input = StructurePdf(Doc(roles));
        string sequence = string.Join(",", roles);

        int changed = FixHeadings(input, out byte[] directly);
        RemediationReport report = RemediateHeadingsOnly(input, out byte[] viaPipeline);
        RemediationStepResult step = HeadingStep(report);

        if (changed == 0)
        {
            Assert.True(step.Skipped,
                sequence + ": a direct fix changes nothing, so the pipeline appended a revision "
                + "for nothing. Step: " + step);
            Assert.Equal(RolesOf(input), RolesOf(viaPipeline));
        }
        else
        {
            Assert.False(step.Skipped,
                sequence + ": a direct fix renumbers " + changed
                + " heading(s), but the pipeline skipped the step. Step: " + step);
            Assert.Equal(changed, step.ItemsChanged);
        }

        // Whatever they decided, they must have produced the same tags.
        Assert.Equal(RolesOf(directly), RolesOf(viaPipeline));
    }

    // =====================================================================
    //  6 — nothing to do costs nothing
    // =====================================================================

    [Fact]
    public void FixHeadingHierarchy_DocumentWithNoHeadings_IsBytewiseUntouched()
    {
        byte[] input = StructurePdf(E("Document", E("P"), E("Figure"), E("P")));

        int changed = FixHeadings(input, out byte[] output);

        Assert.Equal(0, changed);
        Assert.Equal(new[] { "Document", "P", "Figure", "P" }, RolesOf(output));

        // No headings means no edits, and no edits must mean no appended revision: a fixer that
        // grows the file on every run turns an idempotent pipeline into an unbounded one.
        Assert.Equal(input, output);
    }

    [Fact]
    public void FixHeadingHierarchy_AlreadyUnbrokenSequence_IsBytewiseUntouched()
    {
        byte[] input = StructurePdf(Doc("H1", "H2", "H3", "H2", "H1"));

        int changed = FixHeadings(input, out byte[] output);

        Assert.Equal(0, changed);
        Assert.Equal(new[] { "Document", "H1", "H2", "H3", "H2", "H1" }, RolesOf(output));
        Assert.Equal(input, output);
    }

    [Fact]
    public void FixHeadingHierarchy_IsIdempotent()
    {
        // Running the fixer over its own output must be a no-op: the first pass produced an
        // unbroken sequence, so the second has nothing to do and appends no revision.
        byte[] input = StructurePdf(Doc("H2", "H5", "H3", "H6"));

        Assert.True(FixHeadings(input, out byte[] once) > 0);
        IReadOnlyList<string> after = RolesOf(once);

        Assert.Equal(0, FixHeadings(once, out byte[] twice));
        Assert.Equal(after, RolesOf(twice));
        Assert.Equal(once, twice);
    }
}
