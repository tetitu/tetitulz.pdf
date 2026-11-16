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
/// <see cref="PdfNumberTree"/> — the number-tree traversal (§7.9.7) behind <c>/ParentTree</c> and
/// <c>/PageLabels</c>.
/// </summary>
/// <remarks>
/// <para>It shares <c>PdfTreeWalk</c> with <see cref="PdfNameTree"/>, because §7.9.7 defines a
/// number tree by reference to §7.9.6 and changes only the leaf array's key and its key type. So
/// the adversarial cases below are asserted <b>here as well as</b> in the name-tree tests, not
/// instead of: the sharing is what has to be verified from both sides, or the next reader who
/// re-inlines one of them takes the guards with it and nothing goes red.</para>
/// </remarks>
public class PdfNumberTreeTests
{
    [Fact]
    public void Flatten_LeafArray_ReturnsPairsInStoredOrder()
    {
        using var doc = OpenDoc(BuildNumberTreePdf(
            "3 0 obj\n<< /Nums [0 10 0 R 5 11 0 R] >>\nendobj\n"));

        var pairs = Flatten(doc, out bool complete, out int budget);

        Assert.True(complete);
        Assert.Equal(2, pairs.Count);
        Assert.True(PdfNumberTree.TryKey(pairs[0].Key, out long first));
        Assert.True(PdfNumberTree.TryKey(pairs[1].Key, out long second));
        Assert.Equal(0, first);
        Assert.Equal(5, second);

        // Values come back UNRESOLVED, exactly as a name tree's do: the merge remaps them through
        // an object map and needs the reference identity to do it.
        Assert.Equal(10, Assert.IsType<PdfReference>(pairs[0].Value).ReferenceObjectNumber);
        Assert.Equal(11, Assert.IsType<PdfReference>(pairs[1].Value).ReferenceObjectNumber);
        Assert.Equal(98, budget);
    }

    [Fact]
    public void Flatten_KidsHierarchy_DescendsIntoEveryLeaf()
    {
        using var doc = OpenDoc(BuildNumberTreePdf(
            "3 0 obj\n<< /Kids [4 0 R 5 0 R] >>\nendobj\n" +
            "4 0 obj\n<< /Nums [1 10 0 R] >>\nendobj\n" +
            "5 0 obj\n<< /Nums [2 11 0 R] >>\nendobj\n"));

        var pairs = Flatten(doc, out bool complete, out _);

        Assert.True(complete);
        Assert.Equal(2, pairs.Count);
        Assert.True(PdfNumberTree.TryKey(pairs[0].Key, out long a));
        Assert.True(PdfNumberTree.TryKey(pairs[1].Key, out long b));
        Assert.Equal(new[] { 1L, 2L }, new[] { a, b });
    }

    /// <summary>
    /// An odd trailing element is a key with no value, and is dropped rather than paired with
    /// whatever the next leaf begins with.
    /// </summary>
    /// <remarks>
    /// Pairing it would give a real key someone else's structure element — a wrong answer that
    /// parses, which is the shape of defect this tree's callers cannot see.
    /// </remarks>
    [Fact]
    public void Flatten_MalformedOddLeaf_DropsTheDanglingKey()
    {
        using var doc = OpenDoc(BuildNumberTreePdf(
            "3 0 obj\n<< /Kids [4 0 R 5 0 R] >>\nendobj\n" +
            "4 0 obj\n<< /Nums [1 10 0 R 7] >>\nendobj\n" +
            "5 0 obj\n<< /Nums [2 11 0 R] >>\nendobj\n"));

        var pairs = Flatten(doc, out bool complete, out _);

        Assert.True(complete);
        Assert.Equal(2, pairs.Count);
        Assert.True(PdfNumberTree.TryKey(pairs[1].Key, out long second));
        Assert.Equal(2, second);
    }

    /// <summary>
    /// A <c>/Kids</c> cycle terminates instead of spinning, and reports what it did read.
    /// </summary>
    /// <remarks>
    /// <b>The depth cap alone does not bound this</b>, which is why the visited set exists: a
    /// two-node cycle is shallow, so a depth-only guard walks it to the cap on every entry. The
    /// assertion is the guard's own evidence — the entries it collected and a return, not a clock.
    /// </remarks>
    [Fact]
    public void Flatten_KidsCycle_TerminatesAndStillReportsTheRealEntries()
    {
        using var doc = OpenDoc(BuildNumberTreePdf(
            "3 0 obj\n<< /Kids [4 0 R] >>\nendobj\n" +
            "4 0 obj\n<< /Nums [1 10 0 R] /Kids [3 0 R] >>\nendobj\n"));

        var pairs = Flatten(doc, out bool complete, out int budget);

        Assert.True(complete);
        Assert.Single(pairs);
        Assert.True(PdfNumberTree.TryKey(pairs[0].Key, out long key));
        Assert.Equal(1, key);
        // Charged once, not once per lap: the cycle was cut on the second visit, not on the budget.
        Assert.Equal(99, budget);
    }

    /// <summary>
    /// The entry budget stops the walk and says so, rather than returning a tree that is quietly
    /// missing entries.
    /// </summary>
    [Fact]
    public void Flatten_BudgetExhausted_ReturnsFalseAndStopsAtTheCeiling()
    {
        using var doc = OpenDoc(BuildNumberTreePdf(
            "3 0 obj\n<< /Nums [1 10 0 R 2 11 0 R 3 12 0 R] >>\nendobj\n"));

        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 2;
        bool complete = PdfNumberTree.TryFlatten(RootRef(), doc, pairs, ref budget);

        Assert.False(complete);
        Assert.Equal(2, pairs.Count);
        Assert.Equal(0, budget);
    }

    /// <summary>
    /// <see cref="PdfNumberTree.TryKey"/> reports failure rather than a sentinel.
    /// </summary>
    /// <remarks>
    /// Every <c>long</c> is a legal key here, so there is no value that could stand for "not a
    /// key" — a <c>-1</c> convention would collide with a real, if unusual, entry. That is the one
    /// place this type's surface deliberately differs from <see cref="PdfNameTree.KeyText"/>, whose
    /// <c>null</c> cannot be a key.
    /// </remarks>
    [Fact]
    public void TryKey_AcceptsNumbersAndRejectsEverythingElse()
    {
        Assert.True(PdfNumberTree.TryKey(new PdfNumber(-1), out long negative));
        Assert.Equal(-1, negative);

        Assert.False(PdfNumberTree.TryKey(new PdfString("0"), out _));
        Assert.False(PdfNumberTree.TryKey(new PdfName("Zero"), out _));
        Assert.False(PdfNumberTree.TryKey(null, out _));
    }

    /// <summary>
    /// A name tree still flattens through <c>/Names</c>, and a number tree does not read it.
    /// </summary>
    /// <remarks>
    /// The two faces share one walk, so the leaf key is the only thing separating them. If it were
    /// ignored — hard-coded to one of the two — every test above would still pass on whichever tree
    /// matched, and the other kind would silently return nothing.
    /// </remarks>
    [Fact]
    public void TheLeafKeyIsWhatSeparatesTheTwoTreeKinds()
    {
        using var doc = OpenDoc(BuildNumberTreePdf(
            "3 0 obj\n<< /Names [(a) 10 0 R] /Nums [1 11 0 R] >>\nendobj\n"));

        var asNumbers = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        PdfNumberTree.TryFlatten(RootRef(), doc, asNumbers, ref budget);

        var asNames = new List<KeyValuePair<PdfObject, PdfObject>>();
        budget = 100;
        PdfNameTree.TryFlatten(RootRef(), doc, asNames, ref budget);

        Assert.Equal(11, Assert.IsType<PdfReference>(Assert.Single(asNumbers).Value)
            .ReferenceObjectNumber);
        Assert.Equal(10, Assert.IsType<PdfReference>(Assert.Single(asNames).Value)
            .ReferenceObjectNumber);
    }

    // ----------------------------------------------------------------

    private static List<KeyValuePair<PdfObject, PdfObject>> Flatten(
        PdfDocument doc, out bool complete, out int budget)
    {
        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        budget = 100;
        complete = PdfNumberTree.TryFlatten(RootRef(), doc, pairs, ref budget);
        return pairs;
    }

    private static PdfObject RootRef() => new PdfReference(3, 0);

    private static PdfDocument OpenDoc(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary>
    /// A minimal PDF whose object 3 is a number-tree root, plus value objects 10-12. The xref
    /// offsets are deliberately zeroed so the parser rebuilds the table by scanning, which keeps
    /// the fixture readable while these tests vary the node objects freely — the same shape
    /// <c>PdfNameTreeTests</c> uses.
    /// </summary>
    private static byte[] BuildNumberTreePdf(string numberTreeObjects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        sb.Append(numberTreeObjects);
        sb.Append("10 0 obj\n<< /Value 10 >>\nendobj\n");
        sb.Append("11 0 obj\n<< /Value 11 >>\nendobj\n");
        sb.Append("12 0 obj\n<< /Value 12 >>\nendobj\n");
        sb.Append("trailer\n<< /Size 13 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
