// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The model-validation walk bounds a paragraph's placement chain. A paragraph's
/// <c>Placement.Following</c> recurses through the walk's <c>Paragraph</c> step, not its <c>Block</c>
/// step, so without a cap of its own a paragraph placed with itself in its <c>Following</c>, or a long
/// chain of them, would overflow the stack during validation — uncatchably, before any exporter ran.
/// The exporter draws the following paragraphs flat and would produce a document.
/// </summary>
public sealed class ModelCyclePlacementTests
{
    private static int Pages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return doc.Pages.Count;
    }

    private static byte[] Export(Document doc)
    {
        using var ms = new MemoryStream();
        new PdfExporter().Export(doc, ms);
        return ms.ToArray();
    }

    /// <summary>A paragraph whose placement follows itself validates and exports instead of overflowing.</summary>
    [Fact]
    public void AParagraphPlacedWithItselfInItsFollowing_DoesNotOverflow()
    {
        var p = new Paragraph { Placement = new AbsolutePlacement { LeftPt = 10, TopPt = 10 } };
        p.Runs.Add(new TextRun { Text = "x" });
        p.Placement.Following.Add(p);   // the cycle

        var doc = new Document();
        var section = new Section();
        section.Children.Add(p);
        doc.Sections.Add(section);

        Assert.True(Pages(Export(doc)) >= 1);
    }

    /// <summary>A long non-cyclic chain of placed paragraphs is bounded the same way.</summary>
    [Fact]
    public void ALongChainOfPlacedParagraphs_DoesNotOverflow()
    {
        var head = new Paragraph { Placement = new AbsolutePlacement { LeftPt = 10, TopPt = 10 } };
        head.Runs.Add(new TextRun { Text = "h" });
        var cur = head;
        for (int i = 0; i < 20_000; i++)
        {
            var next = new Paragraph { Placement = new AbsolutePlacement { LeftPt = 10, TopPt = 10 } };
            next.Runs.Add(new TextRun { Text = "n" });
            cur.Placement.Following.Add(next);
            cur = next;
        }

        var doc = new Document();
        var section = new Section();
        section.Children.Add(head);
        doc.Sections.Add(section);

        Assert.True(Pages(Export(doc)) >= 1);
    }
}
