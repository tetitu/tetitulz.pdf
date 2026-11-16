// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>Tests that write <see cref="PdfLimits"/>, which is process-global.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PdfLimitsCollection
{
    public const string Name = "PdfLimits (process-global settings)";
}

/// <summary>
/// Merge and split behaviour the overnight sweep found no test pinning: the output version, an
/// outline chain longer than the walk's cap, and the outline a split writes.
/// </summary>
public class MergeSplitOvernightTests
{
    /// <summary>
    /// The merged file declares the highest version among its inputs. A later input with the same
    /// major version and a lower minor one does not lower it: 1.7 then 1.5 is 1.7.
    /// </summary>
    [Fact]
    public void MergeKeepsTheHighestVersionWhateverTheOrder()
    {
        using var doc = Merge(OnePage("1.7"), OnePage("1.5"));
        Assert.Equal("1.7", $"{doc.Version.Major}.{doc.Version.Minor}");
    }

    /// <summary>
    /// A split keeping a bookmark only for its kept child drops the bookmark's own <c>/GoTo</c> to
    /// a page the split left out: it would point at nothing.
    /// </summary>
    [Fact]
    public void SplitDropsAKeptBookmarksGoToADroppedPage()
    {
        using var doc = Split(TwoPagesWithBookmarks(), 1, 1);

        var outlines = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]);
        var parent = (PdfDictionary)doc.ResolveObject(outlines[PdfName.First]);
        Assert.NotNull(parent[PdfName.First]);
        Assert.Null(parent[PdfName.A]);
    }

    /// <summary>A split that keeps no bookmark writes no outline at all.</summary>
    [Fact]
    public void SplitKeepingNoBookmarkWritesNoOutline()
    {
        using var doc = Split(TwoPagesWithBookmarkToSecond(), 1, 1);
        Assert.Null(doc.Catalog.Dictionary[PdfName.Outlines]);
    }

    // ------------------------------------------------------------------ fixtures

    internal static PdfDocument Merge(byte[] a, byte[] b)
    {
        using var ma = new MemoryStream(a);
        using var mb = new MemoryStream(b);
        using var output = new MemoryStream();
        PdfMerger.Merge(output, ma, mb);
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static PdfDocument Split(byte[] source, int startPage, int endPage)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, startPage, endPage);
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static byte[] OnePage(string version) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
    }, version);

    /// <summary>
    /// Two pages. A parent bookmark whose own action is a <c>/GoTo</c> to page 2, with one child
    /// bookmark for page 1.
    /// </summary>
    private static byte[] TwoPagesWithBookmarks() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 2 >>",
        "<< /Title (Parent) /Parent 5 0 R /First 7 0 R /Last 7 0 R /Count 1 " +
        "/A << /S /GoTo /D [4 0 R /Fit] >> >>",
        "<< /Title (Child) /Parent 6 0 R /Dest [3 0 R /Fit] >>",
    });

    /// <summary>Two pages and a single bookmark, for page 2.</summary>
    private static byte[] TwoPagesWithBookmarkToSecond() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
        "<< /Title (Second) /Parent 5 0 R /Dest [4 0 R /Fit] >>",
    });

    internal static byte[] Assemble(List<string> bodies, string version = "1.7")
    {
        var sb = new StringBuilder($"%PDF-{version}\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

[Collection(PdfLimitsCollection.Name)]
public class OutlineChainCapTests
{
    /// <summary>
    /// A source outline's top-level chain is walked at most <c>MaxNameTreeEntries</c> items: at 2,
    /// a three-item chain contributes two.
    /// </summary>
    [Fact]
    public void ATopLevelChainLongerThanTheCapIsCut()
    {
        byte[] three = MergeSplitOvernightTests.Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 5 0 R /Last 7 0 R /Count 3 >>",
            "<< /Title (One) /Parent 4 0 R /Next 6 0 R /Dest [3 0 R /Fit] >>",
            "<< /Title (Two) /Parent 4 0 R /Prev 5 0 R /Next 7 0 R /Dest [3 0 R /Fit] >>",
            "<< /Title (Three) /Parent 4 0 R /Prev 6 0 R /Dest [3 0 R /Fit] >>",
        });
        byte[] none = MergeSplitOvernightTests.Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        try
        {
            PdfLimits.MaxNameTreeEntries = 2;
            using var doc = MergeSplitOvernightTests.Merge(three, none);

            var outlines = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]);
            int items = 0;
            for (var item = doc.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
                 item != null && items < 10;
                 item = doc.ResolveObject(item[PdfName.Next]) as PdfDictionary)
                items++;
            Assert.Equal(2, items);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
