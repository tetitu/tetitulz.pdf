// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfInitialView"/> reads its <c>/OpenAction</c> page through the one §12.3.2.2 lookup,
/// and its page-box names through <c>Core.Constants.PdfPageBoxes</c>, instead of a copy of each.
/// </summary>
public class InitialViewSharedReadsTests
{
    /// <summary>
    /// <b>Object 0 designates no object</b> (§7.3.10), and the shared lookup refuses it — the clause
    /// it was merged for. The private copy compared object numbers and so matched a page written
    /// inline, whose object number is the default 0, and reported a confident page 0.
    /// </summary>
    [Fact]
    public void AReferenceToObjectZeroOpensAtNoPage()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /OpenAction [0 0 R /Fit] >>",
            "<< /Type /Pages /Kids [<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>] /Count 1 >>");

        Assert.Equal(-1, PdfInitialView.GetInitialView(new MemoryStream(pdf)).OpenAtPageIndex);
    }

    /// <summary>
    /// A page written as its index is read as every other destination reader reads it. The private
    /// copy looked only for a reference and reported nothing.
    /// </summary>
    [Fact]
    public void APageWrittenAsItsIndexIsRead_AsEveryOtherDestinationReaderReadsIt()
    {
        var view = PdfInitialView.GetInitialView(new MemoryStream(ThreePages("/OpenAction [2 /XYZ 10 20 1.5]")));

        Assert.Equal(2, view.OpenAtPageIndex);
        Assert.Equal(10, view.OpenAtLeft);
        Assert.Equal(20, view.OpenAtTop);
        Assert.Equal(1.5, view.OpenAtZoom);
    }

    /// <summary>The control: a reference to a page object resolves to that page.</summary>
    [Fact]
    public void APageReferenceOpensAtThatPage()
    {
        var view = PdfInitialView.GetInitialView(new MemoryStream(ThreePages("/OpenAction [4 0 R /Fit]")));

        Assert.Equal(1, view.OpenAtPageIndex);
    }

    /// <summary>
    /// Only <c>/XYZ</c> carries a left, a top and a zoom (§12.3.2.2 Table 151). A <c>/FitR</c> names a
    /// rectangle — its top is read as the top, as the one destination reader reads it, and its right
    /// and bottom are not a zoom and a top; a <c>/FitH</c> names a top alone; a <c>/Fit</c> names
    /// nothing. Read as <c>/XYZ</c>, a <c>/FitR</c> came back as top 100 and zoom 540, which a round
    /// trip through SetInitialView wrote as an extreme <c>/XYZ</c>.
    /// </summary>
    [Theory]
    [InlineData("/OpenAction [4 0 R /FitR 72 100 540 700]", null, 700.0, null)]
    [InlineData("/OpenAction [4 0 R /FitH 500]", null, 500.0, null)]
    [InlineData("/OpenAction [4 0 R /Fit]", null, null, null)]
    [InlineData("/OpenAction [4 0 R /XYZ 10 20 1.5]", 10.0, 20.0, 1.5)]
    public void OnlyAnXyzDestinationCarriesLeftAndZoom_AndEveryFormWithATopGivesIt(
        string openAction, double? left, double? top, double? zoom)
    {
        var view = PdfInitialView.GetInitialView(new MemoryStream(ThreePages(openAction)));

        Assert.Equal(1, view.OpenAtPageIndex);
        Assert.Equal(left, view.OpenAtLeft);
        Assert.Equal(top, view.OpenAtTop);
        Assert.Equal(zoom, view.OpenAtZoom);
    }

    /// <summary>
    /// A box name is written as the page-box table spells it, so a name given with its solidus is
    /// not written as <c>//TrimBox</c>; the name is read back raw, where a doubled solidus would show.
    /// </summary>
    [Theory]
    [InlineData("TrimBox")]
    [InlineData("/TrimBox")]
    public void APageBoxNameIsWrittenAsThePageBoxTableSpellsIt(string requested)
    {
        byte[] pdf = SetView(new ViewerPreferenceSettings { ViewArea = requested, PrintClip = "BleedBox" });

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var prefs = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.ViewerPreferences])!;
        Assert.Equal("TrimBox", ((PdfName)prefs[PdfName.ViewArea]!).Value);
        Assert.Equal("BleedBox", ((PdfName)prefs[PdfName.PrintClip]!).Value);
    }

    /// <summary>Names are case-sensitive (§7.3.5): a wrongly cased box is refused, naming the choices.</summary>
    [Fact]
    public void AWronglyCasedPageBoxIsRefused()
    {
        var ex = Assert.Throws<Core.PdfException>(
            () => SetView(new ViewerPreferenceSettings { ViewClip = "trimbox" }));

        Assert.Contains("/ViewClip must be one of MediaBox, CropBox, BleedBox, TrimBox, ArtBox",
                        ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] SetView(ViewerPreferenceSettings prefs)
    {
        using var output = new MemoryStream();
        PdfInitialView.SetInitialView(new MemoryStream(ThreePages("")), output,
            new InitialViewSettings { ViewerPreferences = prefs });
        return output.ToArray();
    }

    /// <summary>Objects: 1 catalog, 2 pages, 3–5 the pages.</summary>
    private static byte[] ThreePages(string catalogExtra) => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
}
