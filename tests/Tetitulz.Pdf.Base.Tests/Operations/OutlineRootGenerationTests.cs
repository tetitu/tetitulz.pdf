// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfBookmarksManager.WriteBookmarks"/> over an existing outline root names it in each
/// item's <c>/Parent</c> at the root's own generation: the incremental writer keeps a superseded
/// object's generation, so a <c>/Parent</c> at generation 0 named no object. The control, a root at
/// generation 0, is named at 0.
/// </summary>
public class OutlineRootGenerationTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void AnItemsParent_NamesTheExistingRootAtItsGeneration(int generation)
    {
        using var output = new MemoryStream();
        PdfBookmarksManager.WriteBookmarks(new MemoryStream(Pdf(generation)), output,
            new List<PdfBookmark> { new PdfBookmark("One", 0, 0, new List<PdfBookmark>()) });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        Assert.NotNull(root);
        var first = doc.ResolveObject(root![PdfName.First]) as PdfDictionary;
        Assert.NotNull(first);

        var parentRef = Assert.IsType<PdfReference>(first![PdfName.Parent]);
        Assert.Equal(10, parentRef.ReferenceObjectNumber);
        Assert.Equal(generation, parentRef.ReferenceGenerationNumber);
        var parent = doc.ResolveObject(parentRef) as PdfDictionary;
        Assert.NotNull(parent);
        Assert.Equal("Outlines", (parent![PdfName.Type] as PdfName)?.Value);
        Assert.Same(root, parent);
    }

    /// <summary>One page; the outline root is object 10 at <paramref name="generation"/>, empty.</summary>
    private static byte[] Pdf(int generation)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var entries = new List<(int num, int gen, int offset)>();
        void Obj(int num, int gen, string body)
        {
            entries.Add((num, gen, sb.Length));
            sb.Append(num).Append(' ').Append(gen).Append(" obj\n").Append(body).Append("\nendobj\n");
        }
        Obj(1, 0, $"<< /Type /Catalog /Pages 2 0 R /Outlines 10 {generation} R >>");
        Obj(2, 0, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        Obj(10, generation, "<< /Type /Outlines /Count 0 >>");

        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        for (int i = 0; i < 3; i++) sb.Append(entries[i].offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("10 1\n").Append(entries[3].offset.ToString("D10")).Append(' ').Append(generation.ToString("D5")).Append(" n \n");
        sb.Append("trailer\n<< /Size 11 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
