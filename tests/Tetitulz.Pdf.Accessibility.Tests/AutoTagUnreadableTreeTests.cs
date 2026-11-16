// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The auto-tagger counts a <c>/StructTreeRoot</c> it cannot read as an existing tree. Read as none,
/// a merge silently became a fresh tree, and a fresh tree superseded the old one with no warning.
/// </summary>
public sealed class AutoTagUnreadableTreeTests
{
    [Fact]
    public void Adversarial_unreadable_tree_refuses_a_merge_and_leaves_the_document_untagged()
    {
        var result = Tag(Pdf(tree: Tree.Unreadable), new AutoTagOptions { MergeIntoExistingTree = true });

        Assert.Contains(result.Warnings, w => w.Contains("could not be read", StringComparison.Ordinal));
        Assert.Equal(1, result.PagesSkipped);
        Assert.False(result.MergedIntoExistingTree);
    }

    [Fact]
    public void Adversarial_unreadable_tree_is_reported_as_superseded()
    {
        var result = Tag(Pdf(tree: Tree.Unreadable), new AutoTagOptions());

        Assert.Contains(result.Warnings, w => w.Contains("has been superseded", StringComparison.Ordinal));
        Assert.True(result.ElementsAdded > 0);
    }

    /// <summary>The control: a document with no tree is tagged with neither warning.</summary>
    [Fact]
    public void A_document_with_no_tree_is_tagged_without_either_warning()
    {
        var result = Tag(Pdf(tree: Tree.None), new AutoTagOptions());

        Assert.True(result.ElementsAdded > 0);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("superseded", StringComparison.Ordinal)
                                                     || w.Contains("could not be read", StringComparison.Ordinal));
    }

    /// <summary>
    /// Marked content already on the page is not adopted under an unreadable tree, which may own it.
    /// Under no tree the same page is completed around its marked content.
    /// </summary>
    [Theory]
    [InlineData(Tree.Unreadable, false)]
    [InlineData(Tree.None, true)]
    public void Existing_marked_content_is_adopted_only_when_no_tree_could_own_it(Tree tree, bool adopted)
    {
        var result = Tag(Pdf(tree, "/P << /MCID 0 >> BDC BT /F1 12 Tf 20 50 Td (Hello) Tj ET EMC"), new AutoTagOptions());

        Assert.Equal(adopted ? 0 : 1, result.PagesSkipped);
        Assert.Equal(adopted, result.ElementsAdded > 0);
    }

    /// <summary>
    /// The structure editor copies a document with an unreadable tree through unchanged, and says the
    /// tree could not be read rather than that the document is untagged.
    /// </summary>
    [Theory]
    [InlineData(Tree.Unreadable, "/StructTreeRoot could not be read")]
    [InlineData(Tree.None, "it is untagged")]
    public void The_structure_editor_names_an_unreadable_tree_as_such(Tree tree, string said)
    {
        byte[] input = Pdf(tree);
        var output = new MemoryStream();
        var result = Editing.PdfStructureTreeEditor.SetFigureAltText(new MemoryStream(input), output, _ => "alt");

        Assert.Equal(input, output.ToArray());
        Assert.Equal(0, result.Updated);
        Assert.Contains(said, PdfAccessibility.LastDiagnostic, StringComparison.Ordinal);
    }

    public enum Tree { None, Unreadable }

    private static AutoTagResult Tag(byte[] pdf, AutoTagOptions options)
        => PdfAutoTagger.AutoTag(new MemoryStream(pdf), new MemoryStream(), options);

    /// <summary>
    /// A one-page document showing one line of Helvetica. With <see cref="Tree.Unreadable"/> the
    /// catalog names object 6 as its <c>/StructTreeRoot</c>, a lone <c>]</c> the parser cannot read.
    /// </summary>
    private static byte[] Pdf(Tree tree, string content = "BT /F1 12 Tf 20 50 Td (Hello) Tj ET")
    {
        string[] bodies =
        {
            tree == Tree.Unreadable
                ? "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"
                : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "]",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
