// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <see cref="PdfNameTree"/> — the single name-tree traversal (§7.9.6) that replaced six
/// open-coded copies across three assembly rings (GAPS.md §15c).
/// </summary>
/// <remarks>
/// Only one of the six copies guarded against a <c>/Kids</c> cycle or bounded its entry count, so
/// the two adversarial cases below are the ones the consolidation exists to give the other five.
/// </remarks>
public class PdfNameTreeTests
{
    [Fact]
    public void Flatten_LeafArray_ReturnsPairsInStoredOrder()
    {
        using var doc = OpenDoc(BuildNameTreePdf(
            "3 0 obj\n<< /Names [(alpha) 10 0 R (beta) 11 0 R] >>\nendobj\n"));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        bool complete = PdfNameTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        Assert.True(complete);
        Assert.Equal(2, pairs.Count);
        Assert.Equal("alpha", PdfNameTree.KeyText(pairs[0].Key));
        Assert.Equal("beta", PdfNameTree.KeyText(pairs[1].Key));
        // Values come back UNRESOLVED - that is the contract two callers depend on.
        Assert.Equal(10, Assert.IsType<PdfReference>(pairs[0].Value).ReferenceObjectNumber);
        Assert.Equal(11, Assert.IsType<PdfReference>(pairs[1].Value).ReferenceObjectNumber);
        Assert.Equal(98, budget);
    }

    [Fact]
    public void Flatten_KidsHierarchy_DescendsIntoEveryLeaf()
    {
        using var doc = OpenDoc(BuildNameTreePdf(
            "3 0 obj\n<< /Kids [4 0 R 5 0 R] >>\nendobj\n" +
            "4 0 obj\n<< /Names [(a) 10 0 R] >>\nendobj\n" +
            "5 0 obj\n<< /Names [(b) 11 0 R] >>\nendobj\n"));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        bool complete = PdfNameTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        Assert.True(complete);
        Assert.Equal(new[] { "a", "b" },
            pairs.ConvertAll(p => PdfNameTree.KeyText(p.Key)).ToArray());
    }

    /// <summary>
    /// An odd trailing element is a key with no value. It must be dropped, not paired with
    /// whatever the next leaf begins with — which is how a merged tree acquires a destination
    /// nobody wrote.
    /// </summary>
    [Fact]
    public void Flatten_LeafWithOddTrailingKey_DropsItRatherThanMispairing()
    {
        using var doc = OpenDoc(BuildNameTreePdf(
            "3 0 obj\n<< /Names [(a) 10 0 R (orphan)] >>\nendobj\n"));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        PdfNameTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        Assert.Single(pairs);
        Assert.Equal("a", PdfNameTree.KeyText(pairs[0].Key));
    }

    /// <summary>
    /// A two-node <c>/Kids</c> cycle is <i>shallow</i>, so the depth cap alone does not stop it
    /// cheaply — it would spin to <see cref="PdfLimits.MaxTreeDepth"/> once per entry point. The
    /// visited set stops it on the second visit.
    /// </summary>
    [Fact]
    public void Flatten_AdversarialKidsCycle_TerminatesAndVisitsEachNodeOnce()
    {
        using var doc = OpenDoc(BuildNameTreePdf(
            "3 0 obj\n<< /Kids [4 0 R] /Names [(a) 10 0 R] >>\nendobj\n" +
            "4 0 obj\n<< /Kids [3 0 R] /Names [(b) 11 0 R] >>\nendobj\n"));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        bool complete = PdfNameTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        // Terminates, and each node's leaf is read exactly once - not MaxTreeDepth times.
        Assert.True(complete);
        Assert.Equal(2, pairs.Count);
        Assert.Equal(98, budget);
    }

    /// <summary>
    /// Exhausting the shared budget must be <i>reported</i>, so a caller emits "incomplete" rather
    /// than a silently truncated tree. The budget is threaded by <c>ref</c> precisely so it spans
    /// every category and every source document in one operation.
    /// </summary>
    [Fact]
    public void Flatten_BudgetExhausted_ReturnsFalseAndStopsAtTheCeiling()
    {
        using var doc = OpenDoc(BuildNameTreePdf(
            "3 0 obj\n<< /Names [(a) 10 0 R (b) 11 0 R (c) 12 0 R] >>\nendobj\n"));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 2;
        bool complete = PdfNameTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        Assert.False(complete);
        Assert.Equal(2, pairs.Count);
        Assert.Equal(0, budget);
    }

    [Fact]
    public void KeyText_AcceptsStringAndName_AndRejectsOthers()
    {
        Assert.Equal("s", PdfNameTree.KeyText(new PdfString("s")));
        // §7.9.6 says keys are strings, but real files carry names and the walkers this replaced
        // tolerated them.
        Assert.Equal("n", PdfNameTree.KeyText(new PdfName("n")));
        Assert.Null(PdfNameTree.KeyText(new PdfNumber(1)));
        Assert.Null(PdfNameTree.KeyText(null));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A <c>/Kids</c> chain deeper than <see cref="PdfLimits.MaxTreeDepth"/>, with a <b>distinct
    /// object at every level</b>, is cut short and says so.
    /// </summary>
    /// <remarks>
    /// <para><b>The cycle guard beside it cannot catch this, which is the whole point.</b> The
    /// visited set stops an object number being expanded twice; every node here is a different
    /// object, so nothing repeats and the set never fires. Only the depth cap bounds it — and this
    /// is the shape CLAUDE.md describes as the reason the two guards are not one: <i>a depth cap
    /// bounds one branch, never the number of branches</i>, and the converse holds too.</para>
    ///
    /// <para><b>Found by mutation, not by reading.</b> Deleting the depth cap left the Core suite
    /// green <i>and</i> the Base suite green — the cycle guard beside it is killed by two tests, so
    /// the pair looked covered. Nothing in the tree had ever built a deep acyclic tree.</para>
    ///
    /// <para>What is asserted is the guard's own evidence: <c>TryFlatten</c> returns <c>false</c>,
    /// which is the documented signal that a caller must report a truncated result rather than emit
    /// a tree missing entries. No clock is involved — the refusal is deterministic.</para>
    /// </remarks>
    [Fact]
    public void Flatten_ADeepAcyclicKidsChain_IsRefusedByTheDepthCap()
    {
        using var doc = OpenDoc(BuildNameTreePdf(DeepKidsChain(PdfLimits.MaxTreeDepth + 5)));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        bool complete = PdfNameTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        Assert.False(complete);
        Assert.Empty(pairs);
    }

    /// <summary>
    /// The acceptance control: the identical chain, comfortably <b>inside</b> the cap, is walked to
    /// its leaf. Without it the test above would pass on a walker that refused every chain.
    /// </summary>
    /// <remarks>
    /// <b>The exact boundary is deliberately not pinned, because a link is not a level.</b>
    /// Resolving the reference to a node and descending into that node's <c>/Kids</c> each cost a
    /// depth step, so a chain of <i>n</i> links is roughly <i>2n</i> deep — a chain of
    /// <c>MaxTreeDepth - 1</c> links is already refused. Asserting the precise cut-off would pin
    /// that accounting rather than the guard, and would fail the day the walk stops resolving
    /// through a recursive step. A fraction of the cap is inside it under any such accounting.
    /// </remarks>
    [Fact]
    public void Flatten_AChainInsideTheDepthCap_ReachesItsLeaf()
    {
        using var doc = OpenDoc(BuildNameTreePdf(DeepKidsChain(PdfLimits.MaxTreeDepth / 8)));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        bool complete = PdfNameTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        Assert.True(complete);
        Assert.Equal("deep", PdfNameTree.KeyText(Assert.Single(pairs).Key));
    }

    /// <summary>
    /// Object 3 is a <c>/Kids</c> node pointing at object 100, which points at 101, and so on for
    /// <paramref name="levels"/> links; the last node carries the one <c>/Names</c> leaf. Every node
    /// is a distinct object, so the <c>/Kids</c> cycle guard never fires.
    /// </summary>
    private static string DeepKidsChain(int levels)
    {
        var sb = new StringBuilder();
        sb.Append("3 0 obj\n<< /Kids [100 0 R] >>\nendobj\n");

        for (int i = 0; i < levels; i++)
        {
            int self = 100 + i;
            sb.Append(self).Append(" 0 obj\n<< /Kids [").Append(self + 1).Append(" 0 R] >>\nendobj\n");
        }

        sb.Append(100 + levels).Append(" 0 obj\n<< /Names [(deep) 10 0 R] >>\nendobj\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ key order (§7.9.6)

    /// <summary>
    /// Keys order by their bytes: a UTF-16BE key (FE FF …) after every PDFDocEncoded one, whatever
    /// its text. Compared as text, "a" in UTF-16BE sorted before "b" in PDFDocEncoding.
    /// </summary>
    [Fact]
    public void CompareKeys_OrdersByBytes_NotByText()
    {
        byte[] utf16A = { 0xFE, 0xFF, 0x00, 0x61 };
        byte[] docB = { 0x62 };

        Assert.True(PdfNameTree.CompareKeys(docB, utf16A) < 0);
        Assert.True(PdfNameTree.CompareKeys(utf16A, docB) > 0);
    }

    /// <summary>
    /// "Shorter keys shall appear before longer ones beginning with the same byte sequence", and
    /// equal bytes compare equal. Bytes compare unsigned: 0x80 sorts after 0x7F.
    /// </summary>
    [Fact]
    public void CompareKeys_PutsAPrefixFirst_AndComparesUnsigned()
    {
        byte[] ab = { 0x61, 0x62 };
        byte[] abc = { 0x61, 0x62, 0x63 };

        Assert.True(PdfNameTree.CompareKeys(ab, abc) < 0);
        Assert.True(PdfNameTree.CompareKeys(abc, ab) > 0);
        Assert.Equal(0, PdfNameTree.CompareKeys(ab, new byte[] { 0x61, 0x62 }));
        Assert.True(PdfNameTree.CompareKeys(new byte[] { 0x7F }, new byte[] { 0x80 }) < 0);
    }

    /// <summary>A missing key sorts first, and two missing keys are equal.</summary>
    [Fact]
    public void CompareKeys_NullSortsFirst()
    {
        Assert.True(PdfNameTree.CompareKeys(null, new byte[] { 0x00 }) < 0);
        Assert.True(PdfNameTree.CompareKeys(new byte[] { 0x00 }, null) > 0);
        Assert.Equal(0, PdfNameTree.CompareKeys(null, null));
    }

    private static PdfReference RootRef() => new PdfReference(3, 0);

    private static PdfDocument OpenDoc(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary>
    /// A minimal one-page PDF whose object 3 is a name-tree root, plus value objects 10-12.
    /// Written with a rebuild-friendly trailer: the xref offsets are deliberately zeroed so the
    /// parser reconstructs the table by scanning, which keeps the fixture readable while these
    /// tests vary the node objects freely.
    /// </summary>
    private static byte[] BuildNameTreePdf(string nameTreeObjects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names << /Dests 3 0 R >> >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        sb.Append(nameTreeObjects);
        sb.Append("10 0 obj\n<< /Value 10 >>\nendobj\n");
        sb.Append("11 0 obj\n<< /Value 11 >>\nendobj\n");
        sb.Append("12 0 obj\n<< /Value 12 >>\nendobj\n");
        sb.Append("trailer\n<< /Size 13 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
