// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A document part's <c>/Start</c> and <c>/End</c> (ISO 32000-2 §14.12, Table 409) — "an indirect
/// reference to the page object" — read as zero-based page indices.
/// </summary>
/// <remarks>
/// <para><b>The conforming spelling was the one never read.</b> An earlier fix resolved the entries
/// so an indirect value would not read as absent, then asked the resolved object for a number — and
/// a page reference resolves to a page <i>dictionary</i>. Every part of every document written to the
/// standard came back with no page range, while these tests passed on fixtures that wrote page
/// numbers, on a one-page document, with no page for most of the numbers to name. The reference is
/// now matched through <c>PdfDestinationPage.IndexOfPage</c>, the reader a destination's page goes
/// through.</para>
///
/// <para>The number spelling is still read, direct or behind a reference, because the reader
/// required it until then — and range-checked, like a destination's.</para>
/// </remarks>
public class DPartPageRangeTests
{
    /// <summary>§14.12's own spelling: references to the first and last page objects.</summary>
    [Fact]
    public void PageReferencesAreReadAsTheirPageIndices()
    {
        var root = Read("/Start 3 0 R /End 8 0 R");

        var part = Assert.Single(root.Children);
        Assert.Equal(0, part.StartPage);
        Assert.Equal(2, part.EndPage);
    }

    [Fact]
    public void DirectStartAndEndNumbersAreRead()
    {
        var root = Read("/Start 0 /End 2");

        var part = Assert.Single(root.Children);
        Assert.Equal(0, part.StartPage);
        Assert.Equal(2, part.EndPage);
    }

    /// <summary>A page number behind a reference — §7.3.10 lets any value be indirect.</summary>
    [Fact]
    public void IndirectStartAndEndNumbersAreStillRead()
    {
        var root = Read("/Start 20 0 R /End 21 0 R", indirect: true);

        var part = Assert.Single(root.Children);
        Assert.Equal(0, part.StartPage);
        Assert.Equal(2, part.EndPage);
    }

    /// <summary>A number with no page to name is not a page — the destination reader's rule.</summary>
    [Fact]
    public void APageNumberPastTheLastPageNamesNoPage()
    {
        var root = Read("/Start 1 /End 3");

        var part = Assert.Single(root.Children);
        Assert.Equal(1, part.StartPage);
        Assert.Null(part.EndPage);
    }

    /// <summary>A reference to something that is neither a page nor a number names no page.</summary>
    [Fact]
    public void AReferenceToANonPageNamesNoPage()
    {
        var root = Read("/Start 5 0 R /End 8 0 R");

        var part = Assert.Single(root.Children);
        Assert.Null(part.StartPage);
        Assert.Equal(2, part.EndPage);
    }

    /// <summary>
    /// <b>The defect this file found first.</b> §14.12 makes <c>/DParts</c> an array of <i>arrays</i>
    /// of DPart references, and the reader cast each element straight to a dictionary, so on any file
    /// written to the standard the tree came back empty. Two groups, two children each, every part
    /// naming its page by reference.
    /// </summary>
    [Fact]
    public void TheConformingArrayOfArraysShapeYieldsEveryChild()
    {
        using var ms = new MemoryStream(GroupedDocument());
        var root = PdfDPartReader.ReadRoot(ms);

        Assert.Equal(4, root.Children.Count);
        Assert.Equal(new int?[] { 0, 1, 2, 3 },
            new[] { root.Children[0].StartPage, root.Children[1].StartPage,
                    root.Children[2].StartPage, root.Children[3].StartPage });
    }

    /// <summary>
    /// The flat spelling is still accepted — it is what this reader required until then, and a
    /// producer may have written it to match.
    /// </summary>
    [Fact]
    public void TheFlatSpellingIsStillAccepted()
    {
        var root = Read("/Start 3 0 R /End 7 0 R", flat: true);

        var part = Assert.Single(root.Children);
        Assert.Equal(0, part.StartPage);
        Assert.Equal(1, part.EndPage);
    }

    /// <summary>The control: a part declaring neither reports neither, rather than a default.</summary>
    [Fact]
    public void APartDeclaringNoRangeReportsNone()
    {
        var root = Read("");

        var part = Assert.Single(root.Children);
        Assert.Null(part.StartPage);
        Assert.Null(part.EndPage);
    }

    // ------------------------------------------------------------------ helpers

    private static PdfDPartReader.DPartNode Read(string rangeEntries, bool indirect = false, bool flat = false)
    {
        using var ms = new MemoryStream(Document(rangeEntries, indirect, flat));
        var root = PdfDPartReader.ReadRoot(ms);
        Assert.NotNull(root);
        return root;
    }

    /// <summary>
    /// A three-page document — pages 3, 7 and 8, in that order — whose <c>/DPartRoot</c> root node
    /// has one child part carrying <paramref name="rangeEntries"/>. With <paramref name="indirect"/>,
    /// objects 20 and 21 hold the numbers 0 and 2; with <paramref name="flat"/>, <c>/DParts</c> is the
    /// old flat spelling rather than §14.12's array of arrays.
    /// </summary>
    private static byte[] Document(string rangeEntries, bool indirect, bool flat = false)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /DPartRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 7 0 R 8 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /DPartRoot /DPartRootNode 5 0 R >>",
            flat ? "<< /Type /DPart /DParts [6 0 R] >>" : "<< /Type /DPart /DParts [[6 0 R]] >>",
            "<< /Type /DPart " + rangeEntries + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        if (indirect)
        {
            while (bodies.Count < 19) bodies.Add("<< /Type /Filler >>");
            bodies.Add("0");   // object 20
            bodies.Add("2");   // object 21
        }

        return Assemble(bodies);
    }

    /// <summary>
    /// Four pages (3, 10, 11, 12) and a root with <b>two groups of two</b> —
    /// <c>/DParts [[6 0 R 7 0 R] [8 0 R 9 0 R]]</c> — so a reader that took only the first element
    /// of each group, or only the first group, is caught.
    /// </summary>
    private static byte[] GroupedDocument() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /DPartRoot 4 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 10 0 R 11 0 R 12 0 R] /Count 4 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /DPartRoot /DPartRootNode 5 0 R >>",
        "<< /Type /DPart /DParts [[6 0 R 7 0 R] [8 0 R 9 0 R]] >>",
        "<< /Type /DPart /Start 3 0 R >>",
        "<< /Type /DPart /Start 10 0 R >>",
        "<< /Type /DPart /Start 11 0 R >>",
        "<< /Type /DPart /Start 12 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
    });

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
