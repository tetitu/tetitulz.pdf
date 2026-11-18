// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfFeatureVersions.Scan"/>: each rule raises the floor to the version ISO 32000-2
/// marks beside the entry, a dictionary that carries none of them leaves it at 1.0, and a rule
/// keyed to a dictionary's kind does not fire on another kind carrying the same key.
/// </summary>
public sealed class PdfFeatureVersionsTests
{
    private static PdfDictionary Dict(params (PdfName key, PdfObject value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (k, v) in entries) d.Set(k, v);
        return d;
    }

    private static PdfFeatureVersions.Floor ScanOf(PdfObject obj)
    {
        var floor = new PdfFeatureVersions.Floor();
        PdfFeatureVersions.Scan(obj, floor);
        return floor;
    }

    [Fact]
    public void DictionaryWithNoVersionedEntry_LeavesTheFloorAt10()
    {
        var floor = ScanOf(Dict((PdfName.Type, PdfName.Page), (PdfName.Rotate, new PdfNumber(90))));
        Assert.Equal(PdfVersion.Pdf10, floor.Version);
        Assert.Null(floor.Feature);
    }

    [Fact]
    public void FilterChain_TakesItsHighestFilter()
    {
        var chain = new PdfArray { PdfName.FlateDecode, PdfName.JPXDecode };
        Assert.Equal(PdfVersion.Pdf15, ScanOf(Dict((PdfName.Filter, chain))).Version);
        Assert.Equal(PdfVersion.Pdf12, ScanOf(Dict((PdfName.Filter, PdfName.FlateDecode))).Version);
    }

    [Fact]
    public void NestedDirectDictionary_IsScanned_AndAReferenceIsNotFollowed()
    {
        var catalog = Dict((PdfName.Type, PdfName.Catalog), (PdfName.OCProperties, Dict()));
        var holder = Dict((PdfName.Kids, new PdfArray { catalog }));
        Assert.Equal(PdfVersion.Pdf15, ScanOf(holder).Version);

        var byReference = Dict((PdfName.Kids, new PdfArray { new PdfReference(5, 0) }));
        Assert.Equal(PdfVersion.Pdf10, ScanOf(byReference).Version);
    }

    [Fact]
    public void CatalogEntries_EachRaiseToTheirMarkedVersion()
    {
        Assert.Equal(PdfVersion.Pdf12, ScanOf(Dict((PdfName.Type, PdfName.Catalog), (PdfName.Names, Dict()))).Version);
        Assert.Equal(PdfVersion.Pdf13, ScanOf(Dict((PdfName.Type, PdfName.Catalog), (PdfName.StructTreeRoot, Dict()))).Version);
        Assert.Equal(PdfVersion.Pdf14, ScanOf(Dict((PdfName.Type, PdfName.Catalog), (PdfName.MarkInfo, Dict()))).Version);
        Assert.Equal(PdfVersion.Pdf14, ScanOf(Dict((PdfName.Type, PdfName.Catalog), (PdfName.Lang, new PdfString("he")))).Version);
    }

    [Fact]
    public void StructureElementExpansion_Is15_ButAnEOutsideAStructureElementIsNothing()
    {
        Assert.Equal(PdfVersion.Pdf15,
            ScanOf(Dict((PdfName.Type, PdfName.StructElem), (PdfName.E, new PdfString("e.g.")))).Version);
        Assert.Equal(PdfVersion.Pdf10, ScanOf(Dict((PdfName.E, new PdfString("x")))).Version);
    }

    [Fact]
    public void ConstantAlpha_CountsOnlyInAGraphicsState()
    {
        Assert.Equal(PdfVersion.Pdf14,
            ScanOf(Dict((PdfName.Type, PdfName.ExtGState), (PdfName.ca, new PdfNumber(0.5)))).Version);
        Assert.Equal(PdfVersion.Pdf10, ScanOf(Dict((PdfName.CA, new PdfString("caption")))).Version);
    }

    [Fact]
    public void OpenTypeSubtype_Is16_AndTheHighestRuleIsTheOneReported()
    {
        var fontFile = Dict((PdfName.Subtype, PdfName.OpenType), (PdfName.Filter, PdfName.FlateDecode));
        var floor = ScanOf(fontFile);
        Assert.Equal(PdfVersion.Pdf16, floor.Version);
        Assert.Contains("OpenType", floor.Feature);
    }

    [Fact]
    public void Trailer_WithAnId_Is11()
    {
        var trailer = Dict((PdfName.Size, new PdfNumber(3)), (PdfName.Root, new PdfReference(1, 0)),
            (PdfName.ID, new PdfArray { new PdfString("a"), new PdfString("a") }));
        Assert.Equal(PdfFeatureVersions.FileIdentifier, ScanOf(trailer).Version);
    }

    [Fact]
    public void HeaderFor_RaisesARequestToTheFloor_AndNeverLowersIt()
    {
        var floor = ScanOf(Dict((PdfName.Filter, PdfName.JPXDecode)));

        Assert.Equal(PdfVersion.Pdf15, floor.HeaderFor(PdfVersion.Pdf13));
        Assert.Equal("a JPEG 2000 image (JPXDecode)", floor.RaisedBecause(PdfVersion.Pdf13));

        Assert.Equal(PdfVersion.Pdf17, floor.HeaderFor(PdfVersion.Pdf17));
        Assert.Null(floor.RaisedBecause(PdfVersion.Pdf17));
        Assert.Null(floor.RaisedBecause(PdfVersion.Pdf15));
    }

    [Fact]
    public void RaiseReport_IsMadeOnlyWhenAVersionWasAskedFor_AndRaised()
    {
        string report = PdfFeatureVersions.RaiseReport(PdfVersion.Pdf13, PdfVersion.Pdf15, "JPXDecode");
        Assert.Equal("PDF 1.3 was asked for and the file was written as PDF 1.5: the content uses JPXDecode.", report);

        // Nothing asked: the header is whatever the content needs, raised from the 1.4 default or not.
        Assert.Null(PdfFeatureVersions.RaiseReport(null, PdfVersion.Pdf15, "JPXDecode"));
        // Asked, and the request stood.
        Assert.Null(PdfFeatureVersions.RaiseReport(PdfVersion.Pdf17, PdfVersion.Pdf17, null));
    }

    [Fact]
    public void DeeplyNestedArrays_AreWalkedWithoutRecursion()
    {
        PdfObject inner = Dict((PdfName.Filter, PdfName.JPXDecode));
        for (int i = 0; i < 100_000; i++) inner = new PdfArray { inner };
        Assert.Equal(PdfVersion.Pdf15, ScanOf(inner).Version);
    }
}
