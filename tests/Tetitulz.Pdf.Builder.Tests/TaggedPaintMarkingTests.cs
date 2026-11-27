// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A tagged document leaves nothing it paints outside both the structure tree and the artifacts — ISO 14289-1
/// 7.1 ("content shall be marked as Artifact or tagged as real content") and ISO 14289-2 §8.2.2, §8.3.1: every
/// path, shading, image and text operator on every page is inside an <c>/Artifact</c> sequence or one with an
/// <c>/MCID</c>.
/// </summary>
public sealed class TaggedPaintMarkingTests
{
    /// <summary>
    /// A bordered, shaded paragraph, a bordered table with a shaded cell, a list, a footnote and an endnote with their rules, an
    /// image with a description and a decorative one, and page borders: every paint is marked.
    /// </summary>
    [Fact]
    public void EveryPaintOfATaggedDocument_IsTaggedOrAnArtifact()
    {
        byte[] png = ParagraphBoxTests.MakeRgbPng(2, 2);
        Document model = new DocumentBuilder()
            .AddSection(s => s
                .PageBorders(new PageBorders { Top = new Border(1, "#000000"), Bottom = new Border(1, "#000000") })
                .AddParagraph(p => p.AddText("A boxed paragraph").Borders(new Border(1, "#336699")).Shading("#EEEEEE").AddFootnote("A note").AddEndnote("An endnote"))
                .AddTable(t => t
                    .AddRow(r => r.AddCell("Head one").AddCell(c => c.Background("#DDDDDD").AddParagraph(p => p.AddText("Head two"))))
                    .AddRow(r => r.AddCell("Cell one").AddCell("Cell two")))
                .AddList(l => l.AddItem("First").AddItem("Second"))
                .AddParagraph(p => p.AddImage(png, 40, 40, "A red square").AddDecorativeImage(png, 20, 20)))
            .Build();
        model.Tagged = true;

        using var doc = PdfDocument.Open(new MemoryStream(model.ToPdf()));
        var unmarked = new List<string>();
        for (int i = 0; i < doc.Pages.Count; i++)
            foreach (string op in UnmarkedPaints(doc.Pages[i].GetContentStreamData()))
                unmarked.Add($"page {i + 1}: {op}");

        Assert.True(unmarked.Count == 0, string.Join("\n", unmarked));
    }

    /// <summary>The painting operators of <paramref name="content"/> outside every artifact and every MCID-carrying sequence.</summary>
    private static IEnumerable<string> UnmarkedPaints(byte[] content)
    {
        var frames = new Stack<bool>();     // whether each open sequence marks: an artifact, or one with an MCID
        int marking = 0;
        var recent = new Queue<string>();
        foreach (ContentStreamOperation op in ContentStreamParser.Parse(content))
        {
            recent.Enqueue(op.Operator + "(" + string.Join(" ", System.Linq.Enumerable.Select(op.Operands, o => o.ToString())) + ")");
            if (recent.Count > 12) recent.Dequeue();
            switch (op.Operator)
            {
                case "BMC":
                case "BDC":
                {
                    bool marks = op.Operands.Length > 0 && op.Operands[0] is PdfName tag && tag.Value == "Artifact"
                              || op.Operator == "BDC" && op.Operands.Length > 1 && op.Operands[1] is PdfDictionary props && props[PdfName.MCID] != null;
                    frames.Push(marks);
                    if (marks) marking++;
                    continue;
                }
                case "EMC":
                    if (frames.Count > 0 && frames.Pop()) marking--;
                    continue;
                case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                case "sh": case "Do": case "BI": case "Tj": case "TJ": case "'": case "\"":
                    if (marking == 0) yield return string.Join(" ", recent);
                    continue;
            }
        }
    }
}
