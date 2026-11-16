// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The walker's own ceilings and its heading rule, each pinned by a tree small enough to count by hand.
/// The node, depth and edge ceilings each report that they stopped the walk, and each stops it at the
/// number the ceiling names: one more node, level or slot than that is a defect in the walk, not a
/// tolerance. The heading rule maps structure types to levels, and its digit check is the same as
/// the one a reader of a tagged file applies.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class StructTreeWalkerLimitTests
{
    // ------------------------------------------------------------------
    //  Heading level
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("H", 0)]
    [InlineData("H1", 1)]
    [InlineData("H6", 6)]
    [InlineData("H7", 7)]
    [InlineData("H01", 1)]
    [InlineData("H0", -1)]
    [InlineData("H1x", -1)]
    [InlineData("H1:", -1)]
    [InlineData("Hx", -1)]
    [InlineData("P", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    [InlineData("H99999999999", -1)]
    public void HeadingLevel_MapsEachStructureTypeToItsLevel(string role, int expected)
    {
        Assert.Equal(expected, StructTreeWalker.HeadingLevel(role));
    }

    // ------------------------------------------------------------------
    //  The walk's root
    // ------------------------------------------------------------------

    [Fact]
    public void Collect_RootIsNumberedFromItsOwnReference()
    {
        byte[] pdf = Tree("7 0 R", Leaf("P"));

        Assert.Equal(6, Collect(pdf).Nodes[0].ObjectNumber);
    }

    // ------------------------------------------------------------------
    //  Node ceiling
    // ------------------------------------------------------------------

    [Fact]
    public void Collect_NodeCeiling_StopsAtTheCountItNamesAndReportsIt()
    {
        byte[] pdf = Tree("7 0 R 8 0 R 9 0 R 10 0 R", Leaf("P"), Leaf("P"), Leaf("P"), Leaf("P"));

        Walk run = Collect(pdf, maxNodes: 3);

        Assert.Equal(3, run.Nodes.Count);
        Assert.Contains("3-node", run.Reason, StringComparison.Ordinal);
        Assert.True(run.Mentions("node ceiling"),
            "the node ceiling truncated the walk, so the report must say so. Diagnostics: " + string.Join(" | ", run.Diagnostics));
    }

    [Fact]
    public void Collect_NodeCeilingNotReached_VisitsEveryElementAndReportsNothing()
    {
        byte[] pdf = Tree("7 0 R 8 0 R 9 0 R 10 0 R", Leaf("P"), Leaf("P"), Leaf("P"), Leaf("P"));

        Walk run = Collect(pdf, maxNodes: 10);

        Assert.Equal(5, run.Nodes.Count);
        Assert.Null(run.Reason);
        Assert.False(run.Mentions("node ceiling"), "a walk that stopped nowhere must not claim it did");
    }

    // ------------------------------------------------------------------
    //  Depth cap
    // ------------------------------------------------------------------

    [Fact]
    public void Collect_DepthCap_StopsBelowTheLevelItNamesAndSetsTheDepthOfEachNode()
    {
        // root (0) -> 7 (1) -> 8 (2) -> 9 (3) -> 10 (4). A cap of 2 keeps levels 0 to 2.
        byte[] pdf = Tree("7 0 R",
            Element("Sect", "8 0 R"),
            Element("Sect", "9 0 R"),
            Element("Sect", "10 0 R"),
            Leaf("P"));

        Walk run = Collect(pdf, maxDepth: 2);

        Assert.Equal(3, run.Nodes.Count);
        Assert.Equal(new List<int> { 0, 1, 2 }, run.Nodes.ConvertAll(n => n.Depth));
        Assert.Contains("2-level", run.Reason, StringComparison.Ordinal);
        Assert.True(run.Mentions("deeper than"),
            "the depth cap truncated the walk, so the report must say so. Diagnostics: " + string.Join(" | ", run.Diagnostics));
    }

    [Fact]
    public void Collect_DepthCapNotReached_VisitsEveryLevel()
    {
        byte[] pdf = Tree("7 0 R",
            Element("Sect", "8 0 R"),
            Element("Sect", "9 0 R"),
            Element("Sect", "10 0 R"),
            Leaf("P"));

        Walk run = Collect(pdf, maxDepth: 10);

        Assert.Equal(5, run.Nodes.Count);
        Assert.Equal(4, run.Nodes[4].Depth);
        Assert.Null(run.Reason);
    }

    // ------------------------------------------------------------------
    //  Edge budget
    // ------------------------------------------------------------------

    [Fact]
    public void Collect_EdgeBudget_StopsAfterTheSlotsItAllows()
    {
        // The root has five kid slots; a budget of three examines three of them.
        byte[] pdf = Tree("7 0 R 8 0 R 9 0 R 10 0 R 11 0 R",
            Leaf("P"), Leaf("P"), Leaf("P"), Leaf("P"), Leaf("P"));

        try
        {
            PdfLimits.MaxStructTreeEdges = 3;
            Walk run = Collect(pdf);

            Assert.Equal(4, run.Nodes.Count);
            Assert.Contains("edge", run.Reason, StringComparison.Ordinal);
            Assert.True(run.Mentions("edge ceiling"),
                "the edge budget truncated the walk, so the report must say so. Diagnostics: " + string.Join(" | ", run.Diagnostics));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    [Fact]
    public void Collect_EdgeBudgetAtItsDefault_VisitsEveryKid()
    {
        byte[] pdf = Tree("7 0 R 8 0 R 9 0 R 10 0 R 11 0 R",
            Leaf("P"), Leaf("P"), Leaf("P"), Leaf("P"), Leaf("P"));

        Walk run = Collect(pdf);

        Assert.Equal(6, run.Nodes.Count);
        Assert.Null(run.Reason);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>The outcome of one walk: its nodes, the truncation reason, and the diagnostics raised by it.</summary>
    private sealed class Walk
    {
        public List<StructNode> Nodes;
        public string Reason;
        public List<string> Diagnostics;

        public bool Mentions(string fragment)
        {
            foreach (string message in Diagnostics)
                if (message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }

    /// <summary>
    /// Collects the tree of <paramref name="pdf"/> on this thread and records every diagnostic this thread
    /// raises. The walk is synchronous, so the subscription is the one the walk's reports reach.
    /// </summary>
    private static Walk Collect(byte[] pdf, int maxDepth = StructTreeWalker.DefaultMaxDepth, int maxNodes = -1)
    {
        var diagnostics = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Handler(string message)
        {
            if (Environment.CurrentManagedThreadId == thread) diagnostics.Add(message);
        }

        AccessibilityDiagnostics.OnEvent += Handler;
        try
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            PdfObjectIndex index = PdfObjectIndex.Build(doc);
            var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
            Assert.NotNull(root);

            IReadOnlyList<StructNode> nodes = StructTreeWalker.Collect(root, doc, index, out string reason, maxDepth, maxNodes);
            return new Walk { Nodes = new List<StructNode>(nodes), Reason = reason, Diagnostics = diagnostics };
        }
        finally
        {
            AccessibilityDiagnostics.OnEvent -= Handler;
        }
    }

    /// <summary>A structure element with no kids of its own.</summary>
    private static string Leaf(string role) => "<< /Type /StructElem /S /" + role + " /P 6 0 R >>";

    /// <summary>A structure element with one kid list.</summary>
    private static string Element(string role, string kids) =>
        "<< /Type /StructElem /S /" + role + " /P 6 0 R /K [" + kids + "] >>";

    /// <summary>
    /// A classic-xref PDF whose structure tree root is object 6 and whose kids are <paramref name="rootKids"/>.
    /// The elements that follow are objects 7 onward, in order.
    /// </summary>
    private static byte[] Tree(string rootKids, params string[] elements)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [" + rootKids + "] >>",
        };
        bodies.AddRange(elements);

        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n")
              .Append(bodies[i]).Append("\nendobj\n");
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
}
