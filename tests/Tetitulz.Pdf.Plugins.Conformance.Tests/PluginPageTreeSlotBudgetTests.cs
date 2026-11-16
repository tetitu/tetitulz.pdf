// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfUaChecker"/>'s hand-written page-tree walk spends a 200 000-<c>/Kids</c>-slot
/// budget, and these two flat-tree cases bracket where it runs out.
/// </summary>
/// <remarks>
/// <para><b>This class was called <c>PageTreeBudgetParityTests</c> and its summary claimed to hold
/// this walk and <c>PdfDeepAccessibilityChecker</c>'s in step — while invoking only this one.</b>
/// It could not have done otherwise: the deep checker lives in <c>Tetitulz.Pdf.Accessibility</c>,
/// which references this plugin, so the reverse edge is not available here. The real two-sided test
/// is <c>Tetitulz.Pdf.Accessibility.Tests.PageTreeBudgetParityTests</c>, where both are visible.
/// What is left here is a one-sided claim, correctly named.</para>
///
/// <para><b>The walk runs for exactly one kind of input: an encrypted document.</b>
/// <c>PdfDocument</c> skips catalog and page loading there, so the checker walks the tree itself —
/// and PDF/UA-1 expressly permits encryption, so this is a supported audit target chosen by whoever
/// supplies the file.</para>
///
/// <para>The deep checker charges once per <c>/Kids</c> <b>slot</b>. This one charged per slot
/// <i>and</i> per node, i.e. <b>twice per page</b> on a flat tree, so it exhausted at ~100 000
/// pages where the other reached 200 000. On a large encrypted document the same bytes were audited
/// to different depths by the two halves of one audit.</para>
///
/// <para>The slot is the right unit and the node charge was redundant: the visited set already
/// stops a shared <c>/Kids</c> array being expanded twice, and what it cannot stop is the
/// <i>n</i> resolves each expansion costs.</para>
/// </remarks>
public class PluginPageTreeSlotBudgetTests
{
    /// <summary>
    /// <b>The defect.</b> A flat tree of more than half the budget must be walked completely — it
    /// used to stop at the halfway mark and report UA99.
    /// </summary>
    [Fact]
    public void AFlatTreeLargerThanHalfTheBudgetIsWalkedCompletely()
    {
        const int Pages = 120_000;   // over 200 000 / 2, under 200 000

        var result = Validate(EncryptedFlatTree(Pages));

        Assert.DoesNotContain(result, v => v.Code == "UA99" && v.Message.Contains("/Kids slots"));
    }

    /// <summary>
    /// <b>The floor.</b> The ceiling still exists — a tree past it is refused, and says so — so the
    /// fix cannot be "remove the budget", which is what a DoS guard must never become.
    /// </summary>
    [Fact]
    public void ATreePastTheBudgetIsStillRefusedAndSaysSo()
    {
        const int Pages = 250_000;   // over 200 000

        var result = Validate(EncryptedFlatTree(Pages));

        Assert.Contains(result, v => v.Code == "UA99" && v.Message.Contains("/Kids slots"));
    }

    // ------------------------------------------------------------------

    private static IReadOnlyList<PdfUaViolation> Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms).Violations;
    }

    /// <summary>
    /// A document the checker cannot load a catalog for — it carries an <c>/Encrypt</c> entry — so
    /// the hand-written walk under test is the one that runs. The page tree is one <c>/Pages</c>
    /// root with <paramref name="pageCount"/> leaf kids.
    /// </summary>
    private static byte[] EncryptedFlatTree(int pageCount)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");

        // 1 catalog, 2 pages root, 3 encrypt, 4.. leaves.
        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++) kids.Append(i + 4).Append(" 0 R ");

        offsets.Add(acc.Count);
        Emit("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 3 0 R "
             + "/MarkInfo << /Marked true >> >>\nendobj\n");

        offsets.Add(acc.Count);
        Emit($"2 0 obj\n<< /Type /Pages /Count {pageCount} /Kids [{kids}] >>\nendobj\n");

        offsets.Add(acc.Count);
        Emit("3 0 obj\n<< /Type /StructTreeRoot >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            offsets.Add(acc.Count);
            Emit($"{i + 4} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");

        // The /Encrypt entry is what sends the checker down the hand-written walk. Its contents do
        // not need to be usable — the point is that PdfDocument declines to load the catalog.
        Emit($"trailer\n<< /Size {size} /Root 1 0 R /Encrypt << /Filter /Standard /V 1 /R 2 "
             + "/O <0000000000000000000000000000000000000000000000000000000000000000> "
             + "/U <0000000000000000000000000000000000000000000000000000000000000000> /P -1 >> "
             + $">>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
