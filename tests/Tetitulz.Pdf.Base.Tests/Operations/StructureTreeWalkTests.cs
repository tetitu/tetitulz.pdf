// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="StructureTreeWalk"/> — the one traversal of a logical structure tree, which the
/// tagged-text extractor, <see cref="PdfStructureTreeReader"/>, the accessibility feature and the
/// PDF/UA checker all read through.
/// </summary>
/// <remarks>
/// <para>Each ceiling is asserted on the walk's own evidence (<see cref="StructureTreeWalk.EdgesExamined"/>,
/// <see cref="StructureTreeWalk.NodesVisited"/>, the events), never on a clock. The order of
/// <c>Enter</c>, <c>Kid</c> and <c>Leave</c> is pinned as one trace, because a consumer that
/// interleaves leaves and child elements (the reader) or emits a parent after its children (the
/// extractor) depends on exactly that order.</para>
/// <para>The two tests that set a <see cref="PdfLimits"/> value are why the class joins the limits
/// collection.</para>
/// </remarks>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class StructureTreeWalkTests
{
    // Objects: 1 catalog, 2 pages, 3 page A, 4 page B, 5 contents, 6 struct-tree root, 7.. the test's.
    private const int Root = 6;
    private const int First = 7;

    [Fact]
    public void EntersElementsInDocumentOrder_SeesKidsInArrayOrder_AndLeavesAParentAfterItsChildren()
    {
        // root → [P(7) → [MCID 0, Span(8) → [1], 2], Figure(9)]
        byte[] pdf = Pdf("[7 0 R 9 0 R]",
            "<< /Type /StructElem /S /P /P 6 0 R /K [0 8 0 R 2] >>",
            "<< /Type /StructElem /S /Span /P 7 0 R /K [1] >>",
            "<< /Type /StructElem /S /Figure /P 6 0 R /Alt (a chart) >>");

        var trace = new List<string>();
        var nodes = new List<StructureTreeNode>();
        StructureTreeWalk walk = Run(pdf, w =>
        {
            w.Enter = n => { trace.Add("enter " + (n.Role ?? "root") + "@" + n.Depth); nodes.Add(n); };
            w.Kid = k => trace.Add("kid " + (k.Parent.Role ?? "root") + "#" + k.Slot + " " +
                                   (k.IsMarkedContentIdentifier ? "mcid" : k.Kind.ToString()));
            w.Leave = n => trace.Add("leave " + (n.Role ?? "root"));
        });

        Assert.Equal(new[]
        {
            "enter root@0",
            "kid root#0 Element",
            "enter P@1",
            "kid P#0 mcid",
            "kid P#1 Element",
            "enter Span@2",
            "kid Span#0 mcid",
            "leave Span",
            "kid P#2 mcid",
            "leave P",
            "kid root#1 Element",
            "enter Figure@1",
            "leave Figure",
            "leave root",
        }, trace);

        Assert.Equal(new[] { Root, 7, 8, 9 }, nodes.Select(n => n.ObjectNumber));
        Assert.Same(nodes[1], nodes[2].Parent);
        Assert.Same(nodes[0], nodes[3].Parent);
        Assert.Null(nodes[0].Parent);
        Assert.Null(nodes[0].Role);
        Assert.Equal(4, walk.NodesVisited);
        Assert.Equal(6, walk.EdgesExamined);
        Assert.Null(walk.FirstCeiling);
        Assert.False(walk.Revisited);
    }

    [Fact]
    public void ASharedSubtree_IsEnteredOnce_AndSeenAgainAsAVisitedKid()
    {
        // root → [A(7) → [S(9)], B(8) → [S(9)]]: a DAG, which §14.7.2 permits.
        byte[] pdf = Pdf("[7 0 R 8 0 R]",
            "<< /Type /StructElem /S /Sect /P 6 0 R /K [9 0 R] >>",
            "<< /Type /StructElem /S /Sect /P 6 0 R /K [9 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /K [0] >>");

        var entered = new List<int>();
        var sharedKids = new List<StructureTreeKid>();
        var reports = new List<(StructureTreeWalkEvent Event, int Number)>();
        StructureTreeWalk walk = Run(pdf, w =>
        {
            w.Enter = n => entered.Add(n.ObjectNumber);
            w.Kid = k => { if (k.ObjectNumber == 9) sharedKids.Add(k); };
            w.Report = (e, at, number, ex) => reports.Add((e, number));
        });

        Assert.Equal(new[] { Root, 7, 9, 8 }, entered);
        Assert.Equal(2, sharedKids.Count);
        Assert.False(sharedKids[0].AlreadyVisited);
        Assert.True(sharedKids[0].IsEntered);
        Assert.True(sharedKids[1].AlreadyVisited);
        Assert.False(sharedKids[1].IsEntered);
        Assert.Equal(StructKidKind.Element, sharedKids[1].Kind);
        Assert.True(walk.Revisited);
        Assert.Null(walk.FirstCeiling);
        Assert.Equal(new[] { (StructureTreeWalkEvent.Revisit, 9) }, reports);
    }

    [Fact]
    public void AKidCycle_IsWalkedOnce_AndCostsItsSize()
    {
        // A(7) → B(8) → A(7): the shape that cost 2^depth to the walkers this one replaced.
        byte[] pdf = Pdf("[7 0 R]",
            "<< /Type /StructElem /S /Sect /P 6 0 R /K [8 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /K [7 0 R 7 0 R] >>");

        StructureTreeWalk walk = Run(pdf, w => { });

        Assert.Equal(3, walk.NodesVisited);
        Assert.Equal(5, walk.EdgesExamined);
        Assert.True(walk.Revisited);
        Assert.Null(walk.FirstCeiling);
    }

    [Fact]
    public void ADirectDictionaryKid_IsEnteredWithNoObjectNumber()
    {
        byte[] pdf = Pdf("[<< /S /P /K [0] >>]");

        StructureTreeNode direct = null;
        Run(pdf, w => w.Enter = n => { if (n.Depth == 1) direct = n; });

        Assert.NotNull(direct);
        Assert.Equal("P", direct.Role);
        Assert.Equal(-1, direct.ObjectNumber);
        Assert.Equal(1, direct.RawKids.Count);
    }

    [Fact]
    public void TheDepthCap_RefusesTheNodesBelowIt_AndIsReportedOnce()
    {
        // A chain five deep below the root.
        byte[] pdf = Pdf("[7 0 R]", Chain(First, 5));

        var depths = new List<int>();
        var reports = new List<StructureTreeWalkEvent>();
        StructureTreeWalk walk = Run(pdf, w =>
        {
            w.MaxDepth = 2;
            w.Enter = n => depths.Add(n.Depth);
            w.Report = (e, at, number, ex) => reports.Add(e);
        });

        Assert.Equal(new[] { 0, 1, 2 }, depths);
        Assert.True(walk.DepthCapReached);
        Assert.Equal(StructureTreeWalkEvent.DepthCapReached, walk.FirstCeiling);
        Assert.Equal(new[] { StructureTreeWalkEvent.DepthCapReached }, reports);
        Assert.False(walk.NodeCapReached);
        Assert.False(walk.EdgeCapReached);
    }

    [Fact]
    public void TheDepthCapAtItsValue_EntersTheNodeAtThatDepth()
    {
        byte[] pdf = Pdf("[7 0 R]", Chain(First, 5));

        var depths = new List<int>();
        StructureTreeWalk walk = Run(pdf, w =>
        {
            w.MaxDepth = 5;
            w.Enter = n => depths.Add(n.Depth);
        });

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, depths);
        Assert.Null(walk.FirstCeiling);
    }

    [Fact]
    public void TheNodeCap_StopsEnteringAtTheCount_WhileTheSlotsAreStillExamined()
    {
        byte[] pdf = Pdf("[7 0 R 8 0 R 9 0 R 10 0 R 11 0 R]", Siblings(First, 5));

        int kids = 0;
        StructureTreeWalk walk = Run(pdf, w =>
        {
            w.MaxNodes = 3;
            w.Kid = k => kids++;
        });

        // Five root slots, plus the one /K slot of each of the two paragraphs that were entered.
        Assert.Equal(3, walk.NodesVisited);
        Assert.Equal(7, kids);
        Assert.Equal(7, walk.EdgesExamined);
        Assert.True(walk.NodeCapReached);
        Assert.Equal(StructureTreeWalkEvent.NodeCapReached, walk.FirstCeiling);
    }

    [Fact]
    public void TheEdgeCap_StopsExaminingSlots_AndTheParentIsStillLeft()
    {
        // root → [P(7) → [0 1 2 3 4 5]]: one root slot and six under P.
        byte[] pdf = Pdf("[7 0 R]", "<< /Type /StructElem /S /P /P 6 0 R /K [0 1 2 3 4 5] >>");

        var kids = new List<int>();
        var left = new List<string>();
        StructureTreeWalk walk = Run(pdf, w =>
        {
            w.MaxEdges = 4;
            w.Kid = k => kids.Add(k.Slot);
            w.Leave = n => left.Add(n.Role ?? "root");
        });

        Assert.Equal(new[] { 0, 0, 1, 2 }, kids);
        Assert.Equal(4, walk.EdgesExamined);
        Assert.True(walk.EdgeCapReached);
        Assert.Equal(StructureTreeWalkEvent.EdgeCapReached, walk.FirstCeiling);
        Assert.Equal(new[] { "P", "root" }, left);
    }

    [Fact]
    public void ASharedKidsArray_CostsOneSlotPerElementPerEntry_AndTheEdgeCapBoundsIt()
    {
        // 40 elements all pointing /K at one 40-entry array of references to a single leaf: the
        // O(n²) shape in O(n) bytes, which a visited set is perfectly satisfied by.
        byte[] pdf = SharedKidsTree(40);

        StructureTreeWalk whole = Run(pdf, w => { });
        Assert.Equal(40 + 40 * 40, whole.EdgesExamined);
        Assert.Equal(1 + 40 + 1, whole.NodesVisited);
        Assert.Null(whole.FirstCeiling);

        StructureTreeWalk capped = Run(pdf, w => w.MaxEdges = 100);
        Assert.Equal(100, capped.EdgesExamined);
        Assert.True(capped.EdgeCapReached);
    }

    [Fact]
    public void PageIndex_IsTheElementsOwnPg_ElseTheNearestAncestors()
    {
        // Sect(7) on page B → P(8) with no /Pg → Span(9) on page A.
        byte[] pdf = Pdf("[7 0 R]",
            "<< /Type /StructElem /S /Sect /P 6 0 R /Pg 4 0 R /K [8 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /K [9 0 R] >>",
            "<< /Type /StructElem /S /Span /P 8 0 R /Pg 3 0 R /K [0] >>");

        var nodes = new List<StructureTreeNode>();
        Run(pdf, w => w.Enter = nodes.Add);

        Assert.Equal(new[] { -1, 1, -1, 0 }, nodes.Select(n => n.OwnPageIndex));
        Assert.Equal(new[] { -1, 1, 1, 0 }, nodes.Select(n => n.PageIndex));
    }

    [Fact]
    public void AnIndirectKidsArray_AndAnIndirectMcid_AreBothResolved()
    {
        // P(7) /K 8 0 R where 8 is the array [0 9 0 R] and 9 is the integer 1.
        byte[] pdf = Pdf("[7 0 R]",
            "<< /Type /StructElem /S /P /P 6 0 R /K 8 0 R >>",
            "[0 9 0 R]",
            "1");

        var kids = new List<StructureTreeKid>();
        Run(pdf, w => w.Kid = k => { if (k.Parent.Role == "P") kids.Add(k); });

        Assert.Equal(2, kids.Count);
        Assert.True(kids[0].IsMarkedContentIdentifier);
        Assert.True(kids[1].IsMarkedContentIdentifier);
        Assert.Equal(9, kids[1].ObjectNumber);
        Assert.Equal(1, ((PdfNumber)kids[1].Resolved).IntValue);
        Assert.IsType<PdfReference>(kids[1].Raw);
    }

    [Fact]
    public void AnIndirectRole_IsResolved()
    {
        byte[] pdf = Pdf("[7 0 R]",
            "<< /Type /StructElem /S 8 0 R /P 6 0 R /K [0] >>",
            "/H1");

        string role = null;
        Run(pdf, w => w.Enter = n => { if (n.Depth == 1) role = n.Role; });

        Assert.Equal("H1", role);
    }

    [Fact]
    public void AKidThatIsNoKidAtAll_IsSeenAsUnknown_AndNotEntered()
    {
        byte[] pdf = Pdf("[7 0 R]",
            "<< /Type /StructElem /S /P /P 6 0 R /K [<< /Foo /Bar >> << /Type /MCR /MCID 0 >> << /Type /OBJR /Obj 3 0 R >>] >>");

        var kinds = new List<StructKidKind>();
        StructureTreeWalk walk = Run(pdf, w => w.Kid = k => { if (k.Parent.Role == "P") kinds.Add(k.Kind); });

        Assert.Equal(new[] { StructKidKind.Unknown, StructKidKind.MarkedContentReference, StructKidKind.ObjectReference }, kinds);
        Assert.Equal(2, walk.NodesVisited);
    }

    [Fact]
    public void TheReader_RefusesATreePastTheNodeCeiling_NamingIt()
    {
        byte[] pdf = Pdf("[7 0 R 8 0 R 9 0 R 10 0 R 11 0 R]", Siblings(First, 5));
        try
        {
            PdfLimits.MaxStructTreeNodes = 3;
            var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
                () => PdfStructureTreeReader.ReadStructureTree(new MemoryStream(pdf)));
            Assert.Contains("exceeds 3 elements", ex.Message, StringComparison.Ordinal);

            PdfLimits.MaxStructTreeNodes = 6;
            PdfStructElement root = PdfStructureTreeReader.ReadStructureTree(new MemoryStream(pdf));
            Assert.Equal(5, root.Children.Count);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// The reader refuses the element <i>at</i> <see cref="PdfLimits.MaxStructTreeDepth"/> and reads a
    /// chain one shorter — the reader's threshold, which the shared walk's "deeper than" cap meets by
    /// being set one below the limit.
    /// </summary>
    [Fact]
    public void TheReader_RefusesTheElementAtTheDepthLimit_AndReadsAChainOneShorter()
    {
        try
        {
            PdfLimits.MaxStructTreeDepth = 4;

            var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
                () => PdfStructureTreeReader.ReadStructureTree(new MemoryStream(Pdf("[7 0 R]", Chain(First, 4)))));
            Assert.Contains("depth exceeds 4", ex.Message, StringComparison.Ordinal);

            PdfStructElement root = PdfStructureTreeReader.ReadStructureTree(new MemoryStream(Pdf("[7 0 R]", Chain(First, 3))));
            PdfStructElement deepest = root;
            int depth = 0;
            while (deepest.Children.Count > 0 && deepest.Children[0].ElementType == "Sect")
            {
                deepest = deepest.Children[0];
                depth++;
            }
            Assert.Equal(3, depth);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    [Fact]
    public void TheExtractor_ReportsATreePastTheNodeCeiling_AndKeepsWhatItRead()
    {
        byte[] pdf = Pdf("[7 0 R 8 0 R 9 0 R 10 0 R 11 0 R]", Siblings(First, 5, actualText: true));
        try
        {
            PdfLimits.MaxStructTreeNodes = 3;
            IReadOnlyList<PdfTextBlock> blocks = PdfTaggedTextExtractor.Extract(new MemoryStream(pdf));

            Assert.Equal(new[] { "e7", "e8" }, blocks.Select(b => b.Text));
            Assert.Contains("more than 3 elements", PdfTaggedTextExtractor.LastError ?? "", StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    // ----------------------------------------------------------------

    private static StructureTreeWalk Run(byte[] pdf, Action<StructureTreeWalk> configure)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) as PdfDictionary;
        Assert.NotNull(root);

        var walk = new StructureTreeWalk(doc);
        configure(walk);
        walk.Run(root);
        return walk;
    }

    /// <summary>A chain of <paramref name="count"/> elements, the first at <paramref name="first"/>, each the next one's parent.</summary>
    private static string[] Chain(int first, int count)
    {
        var bodies = new string[count];
        for (int i = 0; i < count; i++)
        {
            int number = first + i;
            string kids = i + 1 < count ? "[" + (number + 1) + " 0 R]" : "[0]";
            bodies[i] = "<< /Type /StructElem /S /Sect /P " + (i == 0 ? Root : number - 1) + " 0 R /K " + kids + " >>";
        }
        return bodies;
    }

    /// <summary><paramref name="count"/> sibling paragraphs under the root, each naming MCID 0 — or, with <paramref name="actualText"/>, carrying its own number as text.</summary>
    private static string[] Siblings(int first, int count, bool actualText = false)
    {
        var bodies = new string[count];
        for (int i = 0; i < count; i++)
        {
            int number = first + i;
            bodies[i] = "<< /Type /StructElem /S /P /P 6 0 R /K [0]" +
                        (actualText ? " /ActualText (e" + number + ")" : "") + " >>";
        }
        return bodies;
    }

    /// <summary>Two pages, an empty content stream, a struct-tree root with <paramref name="rootKids"/> as its <c>/K</c>, then <paramref name="bodies"/> from object 7.</summary>
    private static byte[] Pdf(string rootKids, params string[] bodies)
        => Pdf(rootKids, (IEnumerable<string>)bodies);

    private static byte[] Pdf(string rootKids, IEnumerable<string> bodies)
    {
        var all = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << >> /StructParents 0 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << >> /StructParents 1 >>",
            Stream("BT ET"),
            "<< /Type /StructTreeRoot /K " + rootKids + " >>",
        };
        all.AddRange(bodies);
        return Assemble(all);
    }

    /// <summary><paramref name="n"/> elements whose <c>/K</c> all name one shared <paramref name="n"/>-entry array of references to a single leaf.</summary>
    private static byte[] SharedKidsTree(int n)
    {
        const int SharedArray = 7;
        const int Leaf = 8;
        const int FirstElement = 9;

        var kids = new StringBuilder();
        for (int i = 0; i < n; i++) kids.Append(Leaf).Append(" 0 R ");
        var rootKids = new StringBuilder();
        for (int i = 0; i < n; i++) rootKids.Append(FirstElement + i).Append(" 0 R ");

        var bodies = new List<string>
        {
            "[" + kids + "]",
            "<< /Type /StructElem /S /Span /P 6 0 R >>",
        };
        for (int i = 0; i < n; i++)
            bodies.Add("<< /Type /StructElem /S /P /P 6 0 R /K " + SharedArray + " 0 R >>");

        return Pdf("[" + rootKids + "]", bodies);
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string content) =>
        "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
        + content + "\nendstream";
}
