// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <see cref="PdfNumberTree.TryFlatten(PdfObject, PdfDocument, IList{KeyValuePair{PdfObject, PdfObject}}, ref int, int, int, out PdfTreeTruncation)"/>
/// — the walk with the caller's own limits, reporting <b>which</b> one ended it.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Answering only "incomplete" is what kept a second §7.9.7
/// traversal alive in the deep accessibility checker: it writes the reason into a user-facing
/// PDF/UA finding — "The /ParentTree could only be read partially (…)" — and a bare <c>bool</c>
/// cannot fill in the brackets. That copy carried its own cycle guard, depth cap and slot budget,
/// which is the second copy this repository's record says a guard eventually goes missing from.
/// Naming the cause is what let it be retired without losing the diagnostic.</para>
///
/// <para><b>The limits are the caller's on purpose.</b> A conformance checker reading untrusted
/// input holds tighter caps than a reader does — depth 32 against 100, 200 000 entries against
/// 500 000 — and that difference is legitimate, per <c>PdfLimits</c>' rule that centralising a
/// value is not the same as unifying it. The guards are shared; the numbers are not.</para>
///
/// <para>Each limit is asserted <b>alone</b>, on a tree that trips only that one, because the first
/// cause wins and a fixture tripping two would not say which.</para>
/// </remarks>
public sealed class TreeTruncationReasonTests
{
    /// <summary>A leaf node: <c>/Nums [key value …]</c> for <paramref name="count"/> pairs.</summary>
    private static PdfDictionary Leaf(int count, int firstKey = 0)
    {
        var nums = new PdfArray();
        for (int i = 0; i < count; i++)
        {
            nums.Add(new PdfNumber(firstKey + i));
            nums.Add(new PdfDictionary());
        }
        var node = new PdfDictionary();
        node.Set(PdfName.Nums, nums);
        return node;
    }

    /// <summary>A <c>/Kids</c> chain <paramref name="depth"/> levels deep, ending in one leaf pair.</summary>
    private static PdfDictionary Chain(int depth)
    {
        PdfDictionary node = Leaf(1);
        for (int i = 0; i < depth; i++)
        {
            var kids = new PdfArray();
            kids.Add(node);
            var parent = new PdfDictionary();
            parent.Set(PdfName.Kids, kids);
            node = parent;
        }
        return node;
    }

    private static PdfTreeTruncation Walk(
        PdfDictionary root, int maxDepth, int maxKidSlots, int entryBudget, out bool complete, out int read)
    {
        using PdfDocument doc = OpenEmpty();
        var into = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = entryBudget;

        complete = PdfNumberTree.TryFlatten(
            root, doc, into, ref budget, maxDepth, maxKidSlots, out PdfTreeTruncation truncation);

        read = into.Count;
        return truncation;
    }

    // ── each limit, alone ─────────────────────────────────────────────────────────────

    [Fact]
    public void AWholeTreeReportsNoTruncation()
    {
        PdfTreeTruncation why = Walk(Leaf(3), maxDepth: 10, maxKidSlots: 10, entryBudget: 10,
            out bool complete, out int read);

        Assert.Equal(PdfTreeTruncation.None, why);
        Assert.True(complete);
        Assert.Equal(3, read);
    }

    [Fact]
    public void AChainDeeperThanTheCap_ReportsDepth()
    {
        PdfTreeTruncation why = Walk(Chain(6), maxDepth: 2, maxKidSlots: 100, entryBudget: 100,
            out bool complete, out _);

        Assert.Equal(PdfTreeTruncation.Depth, why);
        Assert.False(complete);
    }

    [Fact]
    public void MoreLeafPairsThanTheBudget_ReportsEntries()
    {
        PdfTreeTruncation why = Walk(Leaf(5), maxDepth: 10, maxKidSlots: 100, entryBudget: 2,
            out bool complete, out int read);

        Assert.Equal(PdfTreeTruncation.Entries, why);
        Assert.False(complete);
        Assert.Equal(2, read);   // what it did read is kept, not discarded
    }

    /// <summary>
    /// More <c>/Kids</c> slots than the budget. Distinct from the entry budget because a tree whose
    /// nodes share one <c>/Kids</c> array costs slots without producing entries — the shape neither
    /// a depth cap nor a visited set bounds.
    /// </summary>
    [Fact]
    public void MoreKidSlotsThanTheBudget_ReportsKidSlots()
    {
        var kids = new PdfArray();
        for (int i = 0; i < 5; i++) kids.Add(Leaf(1, firstKey: i));
        var root = new PdfDictionary();
        root.Set(PdfName.Kids, kids);

        PdfTreeTruncation why = Walk(root, maxDepth: 10, maxKidSlots: 2, entryBudget: 100,
            out bool complete, out int read);

        Assert.Equal(PdfTreeTruncation.KidSlots, why);
        Assert.False(complete);
        Assert.Equal(2, read);
    }

    // ── the reason is not a rename of the bool ───────────────────────────────────────

    /// <summary>
    /// The three causes are distinguishable on trees that differ only in which limit they exceed.
    /// A reason that merely echoed <c>complete == false</c> would pass every test above
    /// individually and fail this one.
    /// </summary>
    [Fact]
    public void TheThreeCausesAreDistinguishedFromEachOther()
    {
        PdfTreeTruncation depth = Walk(Chain(6), 2, 100, 100, out _, out _);
        PdfTreeTruncation entries = Walk(Leaf(5), 10, 100, 2, out _, out _);

        var kids = new PdfArray();
        for (int i = 0; i < 5; i++) kids.Add(Leaf(1, firstKey: i));
        var wide = new PdfDictionary();
        wide.Set(PdfName.Kids, kids);
        PdfTreeTruncation slots = Walk(wide, 10, 2, 100, out _, out _);

        Assert.Equal(3, new HashSet<PdfTreeTruncation> { depth, entries, slots }.Count);
    }

    /// <summary>
    /// The default-limit overload still answers, and reports nothing for a tree it can read — the
    /// existing callers are unchanged by the addition.
    /// </summary>
    [Fact]
    public void TheOriginalOverloadStillReadsAWholeTree()
    {
        using PdfDocument doc = OpenEmpty();
        var into = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;

        Assert.True(PdfNumberTree.TryFlatten(Leaf(3), doc, into, ref budget));
        Assert.Equal(3, into.Count);
    }

    /// <summary>A document, for the resolver. The tree itself is built directly above.</summary>
    private static PdfDocument OpenEmpty()
    {
        const string pdf =
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n" +
            "trailer\n<< /Size 4 /Root 1 0 R >>\n%%EOF\n";
        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(pdf)));
    }
}
