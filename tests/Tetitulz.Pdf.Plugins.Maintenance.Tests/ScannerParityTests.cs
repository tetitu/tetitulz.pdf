// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The optimizer carries two near-copies of one content-stream walk —
/// <see cref="FontGlyphUsageScanner"/> and <see cref="ImagePlacementScanner"/> — because they
/// answer different questions (which glyphs are drawn, at what size each image is painted) from the
/// same traversal. CLAUDE.md records that they have drifted before, and that a drift between two
/// copies is invisible until something calls the one that was not exercised.
///
/// <para>These tests assert the two against <b>each other</b> on malformed input, where a
/// difference is a defect rather than a design choice. The assertion is the <i>agreement</i>, not
/// that either returned something: "a reason came back" would pass with either scanner's refusal
/// logic deleted, whereas "these two disagree about whether this document can be scanned" is the
/// thing that has actually gone wrong here before.</para>
/// </summary>
public class ScannerParityTests
{
    /// <summary>
    /// A malformed document with no trailer at all. <c>ImagePlacementScanner</c>, like its sibling,
    /// reads it as <c>trailer?[…]</c> rather than throwing <see cref="NullReferenceException"/>
    /// straight out of <c>Scan</c>.
    /// </summary>
    [Fact]
    public void BothScanners_GiveTheSameAnswer_OnAMalformedDocumentWithNoTrailer()
    {
        AssertScannersAgree(new Dictionary<int, PdfObject>(), null, "catalog");
    }

    /// <summary>
    /// A malformed trailer whose <c>/Root</c> names an object that is not in the file — what a
    /// truncated or partly-recovered document actually looks like.
    /// </summary>
    [Fact]
    public void BothScanners_GiveTheSameAnswer_OnAMalformedTrailerWhoseCatalogIsMissing()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(99, 0));

        AssertScannersAgree(new Dictionary<int, PdfObject>(), trailer, "catalog");
    }

    /// <summary>
    /// A malformed catalog with no <c>/Pages</c>: no page is enumerated, so neither scanner sees
    /// any content.
    ///
    /// <para><b>Neither reports a reason for it</b>, and that is asserted rather than corrected
    /// here, because it is a property of <c>PageTreeWalk</c> that both share: an empty enumeration
    /// is returned as "this document draws nothing" and is indistinguishable from a document that
    /// genuinely does. The pass that consumes these scans fails closed on a reason, so the two
    /// scanners agreeing is what keeps that decision consistent — if one of them ever starts
    /// reporting this case, this test says so instead of letting the two drift apart again.</para>
    /// </summary>
    [Fact]
    public void BothScanners_GiveTheSameAnswer_OnAMalformedCatalogWithNoPageTree()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);

        var objects = new Dictionary<int, PdfObject> { [1] = catalog };
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));

        var options = OptimizationOptions.Default;
        var placements = ImagePlacementScanner.Scan(objects, trailer, options, out string placementReason);
        var usage = FontGlyphUsageScanner.Scan(objects, trailer, options);

        Assert.Null(placementReason);
        Assert.Null(usage.Reason);
        Assert.Empty(placements);
        Assert.Empty(usage.UsedGlyphs);
    }

    /// <summary>
    /// Runs both scanners over the same malformed document and asserts they reach the same verdict:
    /// both refuse, and both say why in terms naming <paramref name="expectedInReason"/>.
    /// </summary>
    private static void AssertScannersAgree(Dictionary<int, PdfObject> objects, PdfDictionary trailer,
        string expectedInReason)
    {
        var options = OptimizationOptions.Default;

        ImagePlacementScanner.Scan(objects, trailer, options, out string placementReason);
        var usage = FontGlyphUsageScanner.Scan(objects, trailer, options);

        Assert.Contains(expectedInReason, placementReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedInReason, usage.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
