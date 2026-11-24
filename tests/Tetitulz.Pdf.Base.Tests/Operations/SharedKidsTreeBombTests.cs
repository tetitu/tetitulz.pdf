// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Adversarial input: every node of a name or number tree points <c>/Kids</c> at the <b>same</b>
/// array. Neither the depth cap nor the visited set bounds it.
/// </summary>
/// <remarks>
/// <para><b>Why the two guards already present do not help, and this is the interesting part.</b>
/// The depth cap bounds one branch, and this tree is two levels deep. The visited set bounds how
/// many times a node is <i>expanded</i> — and it works perfectly here: each of the N nodes is
/// expanded exactly once, and every repeat kid is correctly skipped. What neither counts is the
/// <b>iteration</b>. Each of the N nodes walks all N slots of the shared array and resolves each
/// one before the visited set gets a say, so the walk costs N² resolutions while both existing
/// guards report themselves satisfied.</para>
///
/// <para>It is the same distinction <c>NumberTreeCycleTests</c> draws for cycles — a depth cap
/// bounds one branch, never the number of branches — one level further down: <b>a visited set
/// bounds the number of expansions, never the cost of one.</b>
/// <c>PdfLimits.MaxTreeKidSlots</c> is the third guard, charged per slot <i>before</i> the resolve,
/// because the resolve is the cost.</para>
///
/// <para><b>Potency was measured, and the measurement corrected the claim.</b> The first draft of
/// this comment asserted the unguarded fixture "takes minutes at N = 700". It does not — it takes
/// milliseconds, because <b>a quadratic bomb is not an exponential one</b> and 700² is only half a
/// million slots. Mutating the guard out and timing it gives:</para>
///
/// <list type="table">
///   <item><description><b>N = 2 000</b> — guard removed 631 ms, guard present 308 ms</description></item>
///   <item><description><b>N = 6 000</b> — guard removed 4 000 ms, guard present 511 ms</description></item>
/// </list>
///
/// <para>The evidence is not either absolute number, it is the <b>scaling</b>: tripling N costs
/// 6.3× unguarded (quadratic, as N² predicts) and 1.7× guarded, where the residue is fixture
/// construction and parsing, both linear in N. The cap decouples the walk's cost from N². Extended
/// to a file a real attacker would send — N = 100 000, a few hundred KB compressed — the unguarded
/// walk is 10<sup>10</sup> slots and the guarded one still stops at 2 000 000.</para>
///
/// <para>That is why the numbers live here and in the commit message rather than in a wall-clock
/// assertion: the honest claim is a ratio measured on one machine, and a threshold hard-coded from
/// it would be flaky on a loaded CI box. <b>A DoS-guard test that finishes fast has quite possibly
/// not run at all</b>, and this is what rules that out.</para>
///
/// <para><b>Both trees, because the hole was in both.</b> <c>PdfPageLabels</c> owns the number-tree
/// walk (§7.9.7) and <c>Core.Document.PdfNameTree</c> the name-tree one (§7.9.6) — the latter
/// reached by eight call sites across Base and the plugins. Its budget decremented only per stored
/// <c>/Names</c> pair, so a tree with no <c>/Names</c> anywhere charged it nothing at all.</para>
/// </remarks>
public class SharedKidsTreeBombTests
{
    /// <summary>
    /// Nodes in the bomb. Chosen so the slot budget actually FIRES: the walk would cost N² = 4M
    /// slots and <c>PdfLimits.MaxTreeKidSlots</c> is 2M, so below about 1 414 nodes the guard is
    /// never reached and the test would assert nothing about it.
    /// </summary>
    private const int N = 2000;

    /// <summary>
    /// The number tree, reachable from the public <c>PdfOperations.GetPageLabels</c>. The nodes
    /// hold no <c>/Nums</c>, so a bounded walk yields no ranges; an unbounded one does not yield.
    /// </summary>
    [Fact]
    public void SharedKidsNumberTree_TerminatesInsteadOfWalkingItQuadratically()
    {
        using var ms = new MemoryStream(SharedKidsPdf(pageLabels: true));

        var labels = PdfPageLabels.GetLabels(ms);

        Assert.Empty(labels);
    }

    /// <summary>
    /// The same shape in the name tree, through <c>ListDestinations</c>. The nodes hold no
    /// <c>/Names</c> — which is exactly why the existing entry budget never fired.
    /// </summary>
    [Fact]
    public void SharedKidsNameTree_TerminatesInsteadOfWalkingItQuadratically()
    {
        using var ms = new MemoryStream(SharedKidsPdf(pageLabels: false));

        var dests = PdfOperations.ListDestinations(ms);

        Assert.Empty(dests);
    }

    /// <summary>
    /// The floor, and the reason the two above cannot pass by refusing to walk at all: a legitimate
    /// two-level tree whose leaves really do carry entries still yields them. A guard that bounded
    /// the bomb by declining every <c>/Kids</c> would redden this.
    /// </summary>
    [Fact]
    public void LegitimateNestedNumberTree_StillYieldsItsRanges()
    {
        using var ms = new MemoryStream(Build(
            "<< /Type /Catalog /Pages 2 0 R /PageLabels 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            StreamObj("q Q\n"),
            "<< /Kids [6 0 R 7 0 R] >>",
            "<< /Nums [0 << /S /r >>] >>",
            "<< /Nums [4 << /S /D >>] >>"));

        var labels = PdfPageLabels.GetLabels(ms);

        Assert.Equal(2, labels.Count);
        Assert.Equal(0, labels[0].StartPageIndex);
        Assert.Equal(4, labels[1].StartPageIndex);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// One real page (without which <c>GetLabels</c> never reaches the tree at all), a tree root
    /// pointing <c>/Kids</c> at object 6, and N distinct nodes each pointing <c>/Kids</c> at that
    /// <b>same</b> object 6 — an array of N references to those N nodes.
    /// </summary>
    private static byte[] SharedKidsPdf(bool pageLabels)
    {
        var bodies = new List<string>
        {
            pageLabels
                ? "<< /Type /Catalog /Pages 2 0 R /PageLabels 5 0 R >>"
                : "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            StreamObj("q Q\n"),
            "<< /Kids 6 0 R >>",
        };

        var shared = new StringBuilder("[");
        for (int i = 0; i < N; i++)
            shared.Append(' ').Append((7 + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
        shared.Append(" ]");
        bodies.Add(shared.ToString());

        for (int i = 0; i < N; i++) bodies.Add("<< /Kids 6 0 R >>");

        return Build(bodies.ToArray());
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
