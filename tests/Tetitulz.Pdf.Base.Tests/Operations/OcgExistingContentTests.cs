// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfOcgManager.CreateLayer"/> into a document that <b>already has optional content</b> —
/// the fixture shape the suite had never built. Every authoring test started from a document with no
/// <c>/OCProperties</c>, so nothing could see the new layer's revision drop the layers, lists and
/// keys already there.
/// </summary>
public class OcgExistingContentTests
{
    /// <summary>
    /// The existing layers keep their visibility, under either <c>/BaseState</c>. Both states are
    /// needed: under <c>/ON</c> only the <c>/OFF</c> list hides anything, and under <c>/OFF</c> only
    /// the <c>/ON</c> list shows anything — and a layer in neither list follows the base state, which
    /// a rebuilt configuration would forget.
    /// </summary>
    [Theory]
    [InlineData("ON")]
    [InlineData("OFF")]
    public void CreatingALayerKeepsEveryExistingLayersVisibility(string baseState)
    {
        byte[] pdf = CreateLayer(ExistingContent(baseState), "New");

        var layers = PdfOcgManager.ListLayers(new MemoryStream(pdf)).ToDictionary(l => l.Name, l => l.DefaultVisible);
        Assert.Equal(new[] { "Hidden", "Neutral", "New", "Shown" }, layers.Keys.OrderBy(k => k));
        Assert.True(layers["Shown"]);
        Assert.False(layers["Hidden"]);
        Assert.Equal(baseState == "ON", layers["Neutral"]);
        Assert.True(layers["New"]);
    }

    /// <summary>
    /// The default configuration is superseded <b>in place</b> and keeps the entries this method
    /// does not write — <c>/Order</c>, <c>/Name</c> — and <c>/OCProperties</c> keeps its alternate
    /// <c>/Configs</c>. Read raw, because <c>ListLayers</c> reads neither.
    /// </summary>
    [Fact]
    public void CreatingALayerKeepsTheConfigurationsOtherEntries_InPlace()
    {
        byte[] pdf = CreateLayer(ExistingContent("ON"), "New");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var ocProps = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OCProperties])!;

        Assert.Equal(DefaultConfig, Assert.IsType<PdfReference>(ocProps[PdfName.D]).ReferenceObjectNumber);
        var config = (PdfDictionary)doc.ResolveObject(ocProps[PdfName.D])!;
        Assert.Equal("Default", ((PdfString)doc.ResolveObject(config[PdfName.Name])!).GetText());
        Assert.Equal(3, ((PdfArray)doc.ResolveObject(config[PdfName.Order])!).Count);

        var configs = Assert.IsType<PdfArray>(doc.ResolveObject(ocProps[PdfName.Configs]));
        Assert.Equal(AlternateConfig, ((PdfReference)Assert.Single(configs)).ReferenceObjectNumber);
    }

    /// <summary>
    /// Changing a layer's visibility <b>moves</b> it between <c>/ON</c> and <c>/OFF</c>. Hiding a
    /// layer listed in <c>/ON</c> must take it out of <c>/ON</c>: this library's reader lets
    /// <c>/ON</c> win, so a layer left in both stayed visible and the call's count was a lie. The
    /// reverse case changes nothing this reader can see — <c>/ON</c> wins either way — but a group
    /// in both lists contradicts itself and another reader may apply them in the other order, so the
    /// lists are read raw: the layer is in exactly one of them.
    /// </summary>
    [Theory]
    [InlineData("Shown", 4, false)]
    [InlineData("Hidden", 5, true)]
    public void ChangingALayersVisibilityMovesItBetweenTheLists(string layer, int objectNumber, bool visible)
    {
        using var output = new MemoryStream();
        Assert.Equal(1, PdfOcgManager.SetLayerVisibility(new MemoryStream(ExistingContent("ON")), output, layer, visible));
        byte[] pdf = output.ToArray();

        Assert.Equal(visible, PdfOcgManager.ListLayers(new MemoryStream(pdf)).Single(l => l.Name == layer).DefaultVisible);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var ocProps = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OCProperties])!;
        var config = (PdfDictionary)doc.ResolveObject(ocProps[PdfName.D])!;
        bool InList(PdfName list) => ((PdfArray)doc.ResolveObject(config[list])!)
            .Any(entry => ((PdfReference)entry).ReferenceObjectNumber == objectNumber);
        Assert.Equal(visible, InList(PdfName.ON));
        Assert.Equal(!visible, InList(PdfName.OFF));
    }

    /// <summary>
    /// Layers sharing a name are changed together, and the count says how many — the documented
    /// contract, which no fixture exercised because every one gave its layers distinct names. The
    /// layer with another name is the control.
    /// </summary>
    [Fact]
    public void LayersSharingANameAreHiddenTogether_AndCounted()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R 5 0 R 6 0 R] /D << /ON [4 0 R 5 0 R 6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /OCG /Name (Notes) >>",
            "<< /Type /OCG /Name (Notes) >>",
            "<< /Type /OCG /Name (Other) >>");

        using var output = new MemoryStream();
        Assert.Equal(2, PdfOcgManager.SetLayerVisibility(new MemoryStream(pdf), output, "Notes", visible: false));

        var layers = PdfOcgManager.ListLayers(new MemoryStream(output.ToArray()));
        Assert.Equal(new[] { false, false }, layers.Where(l => l.Name == "Notes").Select(l => l.DefaultVisible));
        Assert.True(layers.Single(l => l.Name == "Other").DefaultVisible);
    }

    /// <summary>
    /// <c>/OCGs</c> written as its own object is read by listing, hiding and renaming alike. Each
    /// of the three carried a comment saying an indirect array had once made it return nothing —
    /// and no fixture wrote one, so the comments were the whole guarantee.
    /// </summary>
    [Fact]
    public void AnIndirectInventoryIsListedHiddenAndRenamed()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs 5 0 R /D << /ON [4 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /OCG /Name (Notes) >>",
            "[4 0 R]");

        Assert.Equal(new[] { "Notes" }, PdfOcgManager.ListLayers(new MemoryStream(pdf)).Select(l => l.Name));

        using var hidden = new MemoryStream();
        Assert.Equal(1, PdfOcgManager.SetLayerVisibility(new MemoryStream(pdf), hidden, "Notes", visible: false));
        Assert.False(Assert.Single(PdfOcgManager.ListLayers(new MemoryStream(hidden.ToArray()))).DefaultVisible);

        using var renamed = new MemoryStream();
        Assert.Equal(1, PdfOcgManager.RenameLayer(new MemoryStream(pdf), renamed, "Notes", "Comments"));
        Assert.Equal("Comments", Assert.Single(PdfOcgManager.ListLayers(new MemoryStream(renamed.ToArray()))).Name);
    }

    /// <summary>
    /// A layer whose <c>/Name</c> is written as a name rather than a text string (§8.11.2.1 says
    /// text; producers write both) is listed, hidden and renamed by that name. The layer named with
    /// a string is the control.
    /// </summary>
    [Fact]
    public void ALayerNamedWithAPdfNameIsFoundByIt()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R 5 0 R] /D << /ON [4 0 R 5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /OCG /Name /Notes >>",
            "<< /Type /OCG /Name (Text) >>");

        Assert.Equal(new[] { "Notes", "Text" }, PdfOcgManager.ListLayers(new MemoryStream(pdf)).Select(l => l.Name));

        using var hidden = new MemoryStream();
        Assert.Equal(1, PdfOcgManager.SetLayerVisibility(new MemoryStream(pdf), hidden, "Notes", visible: false));
        var afterHide = PdfOcgManager.ListLayers(new MemoryStream(hidden.ToArray()));
        Assert.False(afterHide.Single(l => l.Name == "Notes").DefaultVisible);
        Assert.True(afterHide.Single(l => l.Name == "Text").DefaultVisible);

        using var renamed = new MemoryStream();
        Assert.Equal(1, PdfOcgManager.RenameLayer(new MemoryStream(pdf), renamed, "Notes", "Comments"));
        Assert.Equal(new[] { "Comments", "Text" },
            PdfOcgManager.ListLayers(new MemoryStream(renamed.ToArray())).Select(l => l.Name));
    }

    /// <summary>
    /// A layer with no <c>/Name</c> is still listed — under a placeholder a layer panel can show —
    /// rather than dropped or listed with a null name.
    /// </summary>
    [Fact]
    public void ALayerWithNoNameIsListedAsUnnamed()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R 5 0 R] /D << /ON [4 0 R 5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /OCG >>",
            "<< /Type /OCG /Name (Named) >>");

        Assert.Equal(new[] { "(unnamed)", "Named" }, PdfOcgManager.ListLayers(new MemoryStream(pdf)).Select(l => l.Name));
    }

    /// <summary>
    /// A catalog written directly in the trailer — §7.5.5 wants a reference, and a reader may still
    /// accept the dictionary — is no object an appended revision can supersede, so the layer is not
    /// created and the document is copied through unchanged, with 0 for "nothing written". (A
    /// trailer with no catalog at all never reaches this: the document does not open.)
    /// </summary>
    [Fact]
    public void CreatingALayerWhenTheCatalogIsNoObjectWritesNothing()
    {
        string body = "%PDF-1.7\n1 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n";
        int xref = body.Length;
        string pdfText = body + "xref\n0 2\n0000000000 65535 f \n0000000009 00000 n \n" +
                         "trailer\n<< /Size 2 /Root << /Type /Catalog /Pages 1 0 R >> >>\nstartxref\n" + xref + "\n%%EOF\n";
        byte[] pdf = System.Text.Encoding.ASCII.GetBytes(pdfText);

        using var output = new MemoryStream();
        Assert.Equal(0, PdfOcgManager.CreateLayer(new MemoryStream(pdf), output, "Orphan"));
        Assert.Equal(pdf, output.ToArray());
    }

    /// <summary>
    /// <b>Wrapping a page never reuses a <c>/Properties</c> name, however many are taken.</b> The
    /// search for a free name gave up after 999 suffixes and returned the preferred name — which is
    /// taken, so the content already named by it silently moved onto the new group: the one outcome
    /// the search exists to prevent. The cap guarded a loop that cannot run forever, since a
    /// dictionary holds finitely many keys.
    /// </summary>
    [Fact]
    public void WrappingAPageFindsAFreePropertiesName_PastAThousandTakenOnes()
    {
        const int Taken = 1000;   // oc5, then oc5_2 … oc5_1000
        var properties = new System.Text.StringBuilder("/oc5 6 0 R");
        for (int suffix = 2; suffix <= Taken; suffix++) properties.Append(" /oc5_").Append(suffix).Append(" 6 0 R");

        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Properties << " + properties + " >> >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /OCG /Name (Target) >>",
            "<< /Type /OCG /Name (Other) >>");

        using var output = new MemoryStream();
        Assert.True(PdfOcgManager.AssignPageContentToLayer(new MemoryStream(pdf), output, 0, 5));

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var props = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.Properties])!;
        var byTarget = props.Where(kv => ((PdfReference)kv.Value).ReferenceObjectNumber == 5).Select(kv => kv.Key.Value).ToList();
        Assert.Equal(new[] { "oc5_1001" }, byTarget);
        Assert.Equal(Taken, props.Count(kv => ((PdfReference)kv.Value).ReferenceObjectNumber == 6));
    }

    /// <summary>
    /// Wrapping a page adds <c>/Properties</c> to its resources and keeps everything else in them:
    /// the content being wrapped still draws with the fonts it named.
    /// </summary>
    [Fact]
    public void WrappingAPageKeepsItsOtherResources()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /OCG /Name (Target) >>");

        using var output = new MemoryStream();
        Assert.True(PdfOcgManager.AssignPageContentToLayer(new MemoryStream(pdf), output, 0, 6));

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var resources = doc.Pages[0].Resources;
        var fonts = (PdfDictionary)doc.ResolveObject(resources[PdfName.Font])!;
        Assert.Equal(5, ((PdfReference)fonts[new PdfName("F1")]!).ReferenceObjectNumber);
        var props = (PdfDictionary)doc.ResolveObject(resources[PdfName.Properties])!;
        Assert.Equal(6, ((PdfReference)Assert.Single(props).Value).ReferenceObjectNumber);
    }

    /// <summary>
    /// A page index outside the document — at either edge, one below the first page and exactly the
    /// page count — wraps nothing and copies the input through unchanged, rather than failing on
    /// the page lookup.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void WrappingAPageTheDocumentDoesNotHaveChangesNothing(int pageIndex)
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /OCG /Name (Target) >>");

        using var output = new MemoryStream();
        Assert.False(PdfOcgManager.AssignPageContentToLayer(new MemoryStream(pdf), output, pageIndex, 4));
        Assert.Equal(pdf, output.ToArray());
    }

    // ------------------------------------------------------------------ helpers

    private const int DefaultConfig = 7;
    private const int AlternateConfig = 8;

    private static byte[] CreateLayer(byte[] pdf, string name)
    {
        using var output = new MemoryStream();
        PdfOcgManager.CreateLayer(new MemoryStream(pdf), output, name, visible: true);
        return output.ToArray();
    }

    /// <summary>
    /// Three layers — <c>Shown</c> in <c>/ON</c>, <c>Hidden</c> in <c>/OFF</c>, <c>Neutral</c> in
    /// neither — under an indirect default configuration with an <c>/Order</c> and a name, and an
    /// alternate configuration in <c>/Configs</c>.
    /// </summary>
    private static byte[] ExistingContent(string baseState) => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R 5 0 R 6 0 R] /D 7 0 R /Configs [8 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /OCG /Name (Shown) >>",
        "<< /Type /OCG /Name (Hidden) >>",
        "<< /Type /OCG /Name (Neutral) >>",
        "<< /Name (Default) /BaseState /" + baseState + " /Order [4 0 R 5 0 R 6 0 R] /ON [4 0 R] /OFF [5 0 R] >>",
        "<< /Name (Print) /OFF [] >>");
}
