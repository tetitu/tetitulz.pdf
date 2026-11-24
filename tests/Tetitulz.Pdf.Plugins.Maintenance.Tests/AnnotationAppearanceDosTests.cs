// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Both content scanners walk every annotation appearance a page reaches. Pages, a page's
/// <c>/Annots</c> array and an annotation's <c>/AP</c> state dictionary can each be one shared object, so
/// their product — not any one factor — is what bounds the walk. A ~90 KB file (1000 pages × 1000
/// shared <c>/Annots</c> entries × 1000 shared <c>/AP</c> states, all resolving to one empty stream) would
/// make 10^9 walks, each allocating a cycle set and spending no operation budget on an empty stream, so the
/// scan would run for minutes. The walk is charged to <see cref="PdfLimits.MaxAnnotationsPerDocument"/>, and
/// the font scanner walks a shared stream's glyphs once.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class AnnotationAppearanceDosTests
{
    /// <summary>
    /// One page whose <c>/Annots</c> is an array of <paramref name="annots"/> references to a single
    /// annotation, whose <c>/AP /N</c> is a dictionary of <paramref name="states"/> references to a single
    /// empty form stream — <c>annots × states</c> appearance walks from shared objects.
    /// </summary>
    private static PdfArray Nums(params double[] values)
    {
        var a = new PdfArray();
        foreach (double v in values) a.Add(new PdfNumber(v));
        return a;
    }

    private static (Dictionary<int, PdfObject> Objects, PdfDictionary Trailer) SharedAppearances(int annots, int states)
    {
        var objects = new Dictionary<int, PdfObject>();

        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, new PdfName("Catalog"));
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        objects[1] = catalog;

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, new PdfName("Pages"));
        var kids = new PdfArray();
        kids.Add(new PdfReference(3, 0));
        pages.Set(PdfName.Kids, kids);
        pages.Set(PdfName.Count, new PdfNumber(1));
        objects[2] = pages;

        var page = new PdfDictionary();
        page.Set(PdfName.Type, new PdfName("Page"));
        page.Set(PdfName.Parent, new PdfReference(2, 0));
        page.Set(PdfName.MediaBox, Nums(0, 0, 612, 792));
        page.Set(PdfName.Annots, new PdfReference(5, 0));   // one shared /Annots array
        objects[3] = page;

        var annotArray = new PdfArray();
        for (int i = 0; i < annots; i++) annotArray.Add(new PdfReference(6, 0));   // all the same annotation
        objects[5] = annotArray;

        var annot = new PdfDictionary();
        annot.Set(PdfName.Subtype, new PdfName("Square"));
        annot.Set(PdfName.Rect, Nums(0, 0, 1, 1));
        var ap = new PdfDictionary();
        ap.Set(new PdfName("N"), new PdfReference(7, 0));   // /N is a state dictionary
        annot.Set(PdfName.AP, ap);
        objects[6] = annot;

        var stateDict = new PdfDictionary();
        for (int s = 0; s < states; s++) stateDict.Set(new PdfName("s" + s), new PdfReference(8, 0));   // all one stream
        objects[7] = stateDict;

        var form = new PdfStream { RawData = Array.Empty<byte>() };
        form.Set(PdfName.Type, new PdfName("XObject"));
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.BBox, Nums(0, 0, 1, 1));
        objects[8] = form;

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        return (objects, trailer);
    }

    [Fact]
    public void TheFontScan_StopsAtTheAnnotationCap_AndReportsIt()
    {
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 50;
            var (objects, trailer) = SharedAppearances(annots: 12, states: 12);   // 144 appearances > 50

            FontGlyphUsageScanner.Result result = FontGlyphUsageScanner.Scan(objects, trailer, new OptimizationOptions());

            Assert.NotNull(result.Reason);
            Assert.Contains("annotation appearances", result.Reason, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void TheImageScan_StopsAtTheAnnotationCap_AndReportsIt()
    {
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 50;
            var (objects, trailer) = SharedAppearances(annots: 12, states: 12);

            ImagePlacementScanner.Scan(objects, trailer, new OptimizationOptions(), out string reason);

            Assert.NotNull(reason);
            Assert.Contains("annotation appearances", reason, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The control: the same document under the default cap (well above its 144 appearances) is scanned
    /// whole — neither scan reports the annotation cap — so the cap is not a rule that always fires.
    /// </summary>
    [Fact]
    public void WithinTheCap_NeitherScanReportsIt()
    {
        var (objects, trailer) = SharedAppearances(annots: 12, states: 12);

        FontGlyphUsageScanner.Result font = FontGlyphUsageScanner.Scan(objects, trailer, new OptimizationOptions());
        ImagePlacementScanner.Scan(objects, trailer, new OptimizationOptions(), out string imageReason);

        Assert.DoesNotContain("annotation appearances", font.Reason ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("annotation appearances", imageReason ?? "", StringComparison.Ordinal);
    }
}
