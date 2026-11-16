// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Merge now carries over the document outline (bookmarks): each source's /Outlines tree is
/// combined under one root, with item destinations remapped to the merged pages.
/// </summary>
public class OutlineMergeTests
{
    [Fact]
    public void Merge_CombinesOutlineTreesAndRemapsDestinations()
    {
        byte[] a = PdfWithTwoBookmarks("A1", "A2");
        byte[] b = PdfWithTwoBookmarks("B1", "B2");

        byte[] merged;
        using (var ma = new MemoryStream(a))
        using (var mb = new MemoryStream(b))
        using (var output = new MemoryStream())
        {
            PdfMerger.Merge(output, ma, mb);
            merged = output.ToArray();
        }

        using var ms = new MemoryStream(merged);
        using var doc = PdfDocument.Open(ms);

        var outlines = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        Assert.NotNull(outlines);
        Assert.Equal(4, (int)((PdfNumber)doc.ResolveObject(outlines[PdfName.Count])).DoubleValue);

        // Walk /First → /Next: the four bookmarks appear in document order across both sources.
        var titles = new List<string>();
        int firstParent = -1, destPageNum = -1;
        var cur = doc.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        int guard = 0;
        while (cur != null && guard++ < 50)
        {
            if (cur[new PdfName("Title")] is PdfString t) titles.Add(Encoding.ASCII.GetString(t.RawBytes));
            if (titles.Count == 1)
            {
                firstParent = (cur[PdfName.Parent] as PdfReference)?.ReferenceObjectNumber ?? -1;
                // /Dest = [pageRef /Fit] — the page reference must survive the merge remap.
                if (doc.ResolveObject(cur[new PdfName("Dest")]) is PdfArray dest && dest.Count > 0
                    && doc.ResolveObject(dest[0]) is PdfDictionary page)
                    destPageNum = page.ObjectNumber;
            }
            cur = doc.ResolveObject(cur[PdfName.Next]) as PdfDictionary;
        }

        Assert.Equal(new[] { "A1", "A2", "B1", "B2" }, titles);
        Assert.Equal(outlines.ObjectNumber, firstParent);          // re-parented to the merged root
        Assert.True(destPageNum > 0, "bookmark destination did not resolve to a merged page");
    }

    [Fact]
    public void Merge_NoOutlines_ProducesNoOutlineEntry()
    {
        byte[] a = PdfWithTwoBookmarks(null, null); // no /Outlines
        byte[] b = PdfWithTwoBookmarks(null, null);

        using var ma = new MemoryStream(a);
        using var mb = new MemoryStream(b);
        using var output = new MemoryStream();
        PdfMerger.Merge(output, ma, mb);

        using var ms = new MemoryStream(output.ToArray());
        using var doc = PdfDocument.Open(ms);
        Assert.Null(doc.Catalog.Dictionary[PdfName.Outlines]);
    }

    // Builds a 1-page PDF; when titles are non-null it also adds an /Outlines tree with two
    // top-level bookmarks pointing at the page.
    private static byte[] PdfWithTwoBookmarks(string t1, string t2)
    {
        bool withOutline = t1 != null;
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }
        const string cs = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

        sb.Append("%PDF-1.4\n");
        Obj(withOutline
            ? "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>\nendobj\n"
            : "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");
        if (withOutline)
        {
            Obj("5 0 obj\n<< /Type /Outlines /First 6 0 R /Last 7 0 R /Count 2 >>\nendobj\n");
            Obj($"6 0 obj\n<< /Title ({t1}) /Parent 5 0 R /Next 7 0 R /Dest [3 0 R /Fit] >>\nendobj\n");
            Obj($"7 0 obj\n<< /Title ({t2}) /Parent 5 0 R /Prev 6 0 R /Dest [3 0 R /Fit] >>\nendobj\n");
        }

        int size = offsets.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
