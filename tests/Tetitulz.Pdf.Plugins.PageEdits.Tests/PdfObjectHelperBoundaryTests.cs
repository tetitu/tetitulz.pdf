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

/// <summary>
/// Boundaries of the merge/split helper:
/// what a page keeps of its own, the clone and hop ceilings, the destination step budget, and the
/// numbers the assembled page tree and catalog carry.
/// </summary>
public class PdfObjectHelperBoundaryTests
{
    private static readonly PdfName Key = new PdfName("K");

    // ---------- MaterializeInheritedPageAttributes: a page's own attributes win ----------

    /// <summary>A page with its own /MediaBox, /Rotate and /CropBox.</summary>
    private static PdfPage SourcePage()
    {
        string body =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [10 10 600 780] /Rotate 90 >>\nendobj\n";
        var sb = new StringBuilder(body);
        var offsets = new List<int>();
        int at = 0;
        foreach (string marker in new[] { "1 0 obj", "2 0 obj", "3 0 obj" })
        {
            at = body.IndexOf(marker, at, System.StringComparison.Ordinal);
            offsets.Add(at);
        }
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
        return doc.Pages[0];
    }

    private static PdfArray Box(int a, int b, int c, int d)
        => new PdfArray(new PdfObject[] { new PdfNumber(a), new PdfNumber(b), new PdfNumber(c), new PdfNumber(d) });

    private static int[] Numbers(PdfObject o)
    {
        var arr = Assert.IsType<PdfArray>(o);
        var r = new int[arr.Count];
        for (int i = 0; i < r.Length; i++) r[i] = ((PdfNumber)arr[i]).IntValue;
        return r;
    }

    [Fact]
    public void AClonedPageThatDefinesItsOwnMediaBox_KeepsIt()
    {
        var cloned = new PdfDictionary();
        cloned.Set(PdfName.MediaBox, Box(0, 0, 100, 200));

        PdfObjectHelper.MaterializeInheritedPageAttributes(cloned, SourcePage(), new Dictionary<int, int>(), 99);

        Assert.Equal(new[] { 0, 0, 100, 200 }, Numbers(cloned[PdfName.MediaBox]));
    }

    [Fact]
    public void AClonedPageWithoutAMediaBox_TakesTheSourcesAsAControl()
    {
        var cloned = new PdfDictionary();

        PdfObjectHelper.MaterializeInheritedPageAttributes(cloned, SourcePage(), new Dictionary<int, int>(), 99);

        Assert.Equal(new[] { 0, 0, 612, 792 }, Numbers(cloned[PdfName.MediaBox]));
    }

    [Fact]
    public void AClonedPageThatDefinesItsOwnRotate_KeepsIt()
    {
        var cloned = new PdfDictionary();
        cloned.Set(PdfName.Rotate, new PdfNumber(180));

        PdfObjectHelper.MaterializeInheritedPageAttributes(cloned, SourcePage(), new Dictionary<int, int>(), 99);

        Assert.Equal(180, ((PdfNumber)cloned[PdfName.Rotate]).IntValue);
    }

    [Fact]
    public void AClonedPageWithoutARotate_TakesTheSourcesAsAControl()
    {
        var cloned = new PdfDictionary();

        PdfObjectHelper.MaterializeInheritedPageAttributes(cloned, SourcePage(), new Dictionary<int, int>(), 99);

        Assert.Equal(90, ((PdfNumber)cloned[PdfName.Rotate]).IntValue);
    }

    [Fact]
    public void AClonedPageThatDefinesItsOwnCropBox_KeepsIt()
    {
        var cloned = new PdfDictionary();
        cloned.Set(PdfName.CropBox, Box(1, 2, 3, 4));

        PdfObjectHelper.MaterializeInheritedPageAttributes(cloned, SourcePage(), new Dictionary<int, int>(), 99);

        Assert.Equal(new[] { 1, 2, 3, 4 }, Numbers(cloned[PdfName.CropBox]));
    }

    [Fact]
    public void AClonedPageWithoutACropBox_TakesTheSourcesAsAControl()
    {
        var cloned = new PdfDictionary();

        PdfObjectHelper.MaterializeInheritedPageAttributes(cloned, SourcePage(), new Dictionary<int, int>(), 99);

        Assert.Equal(new[] { 10, 10, 600, 780 }, Numbers(cloned[PdfName.CropBox]));
    }

    // ---------- DeepClone ----------

    [Fact]
    public void DeepClone_OfNull_IsPdfNull()
    {
        Assert.Same(PdfNull.Instance, PdfObjectHelper.DeepClone(null, new Dictionary<int, int>(), 1));
    }

    private static PdfObject Nest(int levels, PdfObject leaf)
    {
        PdfObject inner = leaf;
        for (int i = 0; i < levels; i++)
        {
            var a = new PdfArray();
            a.Add(inner);
            inner = a;
        }
        return inner;
    }

    private static PdfObject Innermost(PdfObject o)
    {
        while (o is PdfArray a) o = a[0];
        return o;
    }

    [Collection(PdfLimitsCollection.Name)]
    public class DepthCap
    {
        /// <summary>
        /// The leaf of three nested arrays is cloned at depth 3 — the cap itself — and a fourth
        /// level is cut. A cap of 3 therefore keeps three levels of nesting, not two.
        /// </summary>
        [Fact]
        public void TheLevelAtTheCapIsCloned_TheOneBelowIsCutAndCounted()
        {
            int saved = PdfLimits.MaxObjectGraphDepth;
            try
            {
                PdfLimits.MaxObjectGraphDepth = 3;
                var map = new Dictionary<int, int>();

                PdfObjectHelper.ResetDepthTruncations();
                var kept = PdfObjectHelper.DeepClone(Nest(3, new PdfNumber(7)), map, 1);
                Assert.Equal(7, ((PdfNumber)Innermost(kept)).IntValue);
                Assert.Equal(0, PdfObjectHelper.DepthTruncations);

                PdfObjectHelper.ResetDepthTruncations();
                var cut = PdfObjectHelper.DeepClone(Nest(4, new PdfNumber(7)), map, 1);
                Assert.Same(PdfNull.Instance, Innermost(cut));
                Assert.Equal(1, PdfObjectHelper.DepthTruncations);
            }
            finally
            {
                PdfLimits.MaxObjectGraphDepth = saved;
                PdfObjectHelper.ResetDepthTruncations();
            }
        }
    }

    [Fact]
    public void AStreamKeepsItsBytesAndTheirLength_NotTheLengthItWasHanded()
    {
        var stream = new PdfStream { RawData = new byte[] { 1, 2, 3, 4, 5 } };
        stream.Set(PdfName.Length, new PdfNumber(99));

        var cloned = Assert.IsType<PdfStream>(PdfObjectHelper.DeepClone(stream, new Dictionary<int, int>(), 1));

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, cloned.RawData);
        Assert.Equal(5, ((PdfNumber)cloned[PdfName.Length]).IntValue);
    }

    [Fact]
    public void AStreamWithNoBytes_IsClonedWithoutALengthItCannotMeasure()
    {
        var stream = new PdfStream();
        stream.Set(Key, new PdfNumber(1));

        var cloned = Assert.IsType<PdfStream>(PdfObjectHelper.DeepClone(stream, new Dictionary<int, int>(), 1));

        Assert.Null(cloned.RawData);
        Assert.Null(cloned[PdfName.Length]);
        Assert.Equal(1, ((PdfNumber)cloned[Key]).IntValue);
    }

    [Fact]
    public void OnlyAPageDictionary_HasItsParentRepointedAtTheNewTree()
    {
        var map = new Dictionary<int, int> { [5] = 50 };

        var page = new PdfDictionary();
        page.Set(PdfName.Type, PdfName.Page);
        page.Set(PdfName.Parent, new PdfReference(5, 0));
        var clonedPage = Assert.IsType<PdfDictionary>(PdfObjectHelper.DeepClone(page, map, 99));
        var pageParent = Assert.IsType<PdfReference>(clonedPage[PdfName.Parent]);
        Assert.Equal(99, pageParent.ReferenceObjectNumber);
        Assert.Equal(0, pageParent.ReferenceGenerationNumber);

        // The control: a dictionary that is not a page maps /Parent like any other reference.
        var other = new PdfDictionary();
        other.Set(PdfName.Type, new PdfName("Other"));
        other.Set(PdfName.Parent, new PdfReference(5, 0));
        var clonedOther = Assert.IsType<PdfDictionary>(PdfObjectHelper.DeepClone(other, map, 99));
        Assert.Equal(new PdfReference(50, 0), clonedOther[PdfName.Parent]);
    }

    // ---------- DerefCloned: the hop ceiling ----------

    private static Dictionary<int, PdfObject> Chain(int hops)
    {
        // 1 -> 2 -> ... -> hops, then a number: following the whole chain takes `hops` lookups.
        var map = new Dictionary<int, PdfObject>();
        for (int i = 1; i < hops; i++) map[i] = new PdfReference(i + 1, 0);
        map[hops] = new PdfNumber(7);
        return map;
    }

    [Fact]
    public void ThirtyTwoHops_ReachTheObject()
    {
        var r = PdfObjectHelper.DerefCloned(new PdfReference(1, 0), Chain(32));

        Assert.Equal(7, ((PdfNumber)r).IntValue);
    }

    [Fact]
    public void ThirtyThreeHops_StopShortAndHandBackTheReferenceReached()
    {
        var r = PdfObjectHelper.DerefCloned(new PdfReference(1, 0), Chain(33));

        Assert.Equal(new PdfReference(33, 0), r);
    }

    // ---------- DestinationPageObjectNumber: the step budget and the empty array ----------

    private static PdfObject DictChain(int dictionaries)
    {
        // << /D << /D ... [ page /Fit ] >> >>: each dictionary costs one step, the array a last one.
        PdfObject inner = new PdfArray(new PdfObject[] { new PdfReference(42, 0), new PdfName("Fit") });
        for (int i = 0; i < dictionaries; i++)
        {
            var d = new PdfDictionary();
            d.Set(PdfName.D, inner);
            inner = d;
        }
        return inner;
    }

    [Fact]
    public void ADestinationSixStepsDeep_Resolves()
    {
        Assert.Equal(42, PdfObjectHelper.DestinationPageObjectNumber(DictChain(5), null, null));
    }

    [Fact]
    public void ADestinationSevenStepsDeep_IsRefused()
    {
        Assert.Equal(0, PdfObjectHelper.DestinationPageObjectNumber(DictChain(6), null, null));
    }

    [Fact]
    public void AnEmptyExplicitDestination_ResolvesToNoPage()
    {
        Assert.Equal(0, PdfObjectHelper.DestinationPageObjectNumber(new PdfArray(), null, null));
    }

    [Fact]
    public void AnExplicitDestinationWhoseFirstItemIsNotAPage_ResolvesToNoPage()
    {
        var dest = new PdfArray(new PdfObject[] { new PdfNumber(3), new PdfName("Fit") });

        Assert.Equal(0, PdfObjectHelper.DestinationPageObjectNumber(dest, null, null));
    }

    // ---------- BuildPageTreeAndCatalog: the numbers it stamps ----------

    [Fact]
    public void ThePageTreeAndCatalog_CarryTheirObjectNumbersAndPointAtEachOther()
    {
        var kids = new PdfArray();
        kids.Add(new PdfReference(3, 0));

        var (pages, catalog) = PdfObjectHelper.BuildPageTreeAndCatalog(kids, 7, 8);

        Assert.Equal(7, pages.ObjectNumber);
        Assert.Equal(8, catalog.ObjectNumber);
        Assert.Equal(0, catalog.GenerationNumber);
        Assert.Equal(new PdfReference(7, 0), catalog[PdfName.Pages]);
        Assert.Equal(1, ((PdfNumber)pages[PdfName.Count]).IntValue);
    }
}
