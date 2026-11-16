// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfContentStreamBuilder.AddTransparencyGroup"/> / <see cref="PdfContentStreamBuilder.DrawForm"/>
/// (batch 90): another builder's content written as a form XObject with an isolated transparency
/// group, its own resources built by the same method a page's are, its links hoisted onto the page.
/// </summary>
/// <remarks>
/// Asserted on the objects read back from the file: the form's dictionary, its resources and
/// content, the page's <c>Do</c> and annotations — what a viewer composites from.
/// </remarks>
public class TransparencyGroupTests
{
    private static (PdfDocument Doc, PdfDictionary Page, string Content) Build(Action<PdfContentStreamBuilder> draw)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 200);
        draw(page);
        using var ms = new MemoryStream();
        builder.Build(ms);
        var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));
        return (doc, doc.Pages[0].Dictionary, Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()));
    }

    private static PdfStream Form(PdfDocument doc, PdfDictionary owner, string name)
    {
        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(owner[PdfName.Resources]));
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.XObject]));
        return Assert.IsType<PdfStream>(doc.ResolveObject(xobjects[new PdfName(name)]));
    }

    /// <summary>
    /// The group is a form XObject with an isolated transparency group over the stated box, whose
    /// resources are its content's — the standard font it set — and whose content is the text drawn
    /// into it; the page paints it with one <c>Do</c> and holds none of the text itself.
    /// </summary>
    [Fact]
    public void AGroupIsAnIsolatedFormWithItsOwnResourcesAndContent()
    {
        var (doc, page, content) = Build(p =>
        {
            var group = new PdfContentStreamBuilder();
            group.BeginText().SetFont(group.AddStandardFont(StandardFonts.TimesRoman), 12).SetTextPosition(10, 100).ShowText("Inside").EndText();
            string name = p.AddTransparencyGroup(group, 0, 0, 200, 200);
            p.SaveState().SetGraphicsState(p.AddExtGState(0.5f, 0.5f)).DrawForm(name).RestoreState();
        });
        var form = Form(doc, page, "Fm1");

        Assert.Equal("Form", ((PdfName)form[PdfName.Subtype]).Value);
        var group = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Group]));
        Assert.Equal("Transparency", ((PdfName)group[PdfName.S]).Value);
        Assert.True(Assert.IsType<PdfBoolean>(group[PdfName.I]).Value);
        var bbox = Assert.IsType<PdfArray>(form[PdfName.BBox]);
        Assert.Equal(new[] { 0.0, 0.0, 200.0, 200.0 }, bbox.Cast<PdfNumber>().Select(n => n.DoubleValue));
        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Resources]));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.Font]));
        var f1 = Assert.IsType<PdfDictionary>(doc.ResolveObject(fonts[new PdfName("F1")]));
        Assert.Equal("Times-Roman", ((PdfName)f1[PdfName.BaseFont]).Value);
        string inner = Encoding.Latin1.GetString(form.GetDecodedData());
        Assert.Contains("(Inside) Tj", inner, StringComparison.Ordinal);
        Assert.Contains("/Fm1 Do", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Tj", content, StringComparison.Ordinal);
        Assert.True(content.IndexOf(" gs", StringComparison.Ordinal) < content.IndexOf("/Fm1 Do", StringComparison.Ordinal), "the alpha is not set before the group");
    }

    /// <summary>
    /// A link and an anchor made inside the group belong to the page: the page's <c>/Annots</c> has
    /// the link, at the rectangle the group stated, and a page without a group has no XObjects at all.
    /// </summary>
    [Fact]
    public void TheGroupsLinksAreHoistedOntoThePage()
    {
        var (doc, page, _) = Build(p =>
        {
            var group = new PdfContentStreamBuilder();
            group.AddLink(10, 20, 110, 40, "https://example.test/");
            group.SetFillColorRgb(1, 0, 0).Rectangle(10, 20, 100, 20).Fill();
            p.DrawForm(p.AddTransparencyGroup(group, 0, 0, 200, 200));
        });
        var (plainDoc, plainPage, _) = Build(p => p.SetFillColorRgb(1, 0, 0).Rectangle(10, 20, 100, 20).Fill());

        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(page[PdfName.Annots]));
        var link = Assert.IsType<PdfDictionary>(doc.ResolveObject(Assert.Single(annots)));
        Assert.Equal("Link", ((PdfName)link[PdfName.Subtype]).Value);
        var rect = Assert.IsType<PdfArray>(link[PdfName.Rect]).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray();
        Assert.Equal(new[] { 10.0, 20.0, 110.0, 40.0 }, rect);
        var plainResources = Assert.IsType<PdfDictionary>(plainDoc.ResolveObject(plainPage[PdfName.Resources]));
        Assert.Null(plainResources[PdfName.XObject]);
        Assert.Null(plainPage[PdfName.Annots]);
    }

    /// <summary>A group inside a group is a form inside the outer form's resources, built by the same method.</summary>
    [Fact]
    public void AGroupInsideAGroupIsAFormInsideTheOuterFormsResources()
    {
        var (doc, page, content) = Build(p =>
        {
            var inner = new PdfContentStreamBuilder();
            inner.SetFillColorRgb(0, 0, 1).Rectangle(0, 0, 50, 50).Fill();
            var outer = new PdfContentStreamBuilder();
            outer.DrawForm(outer.AddTransparencyGroup(inner, 0, 0, 200, 200));
            p.DrawForm(p.AddTransparencyGroup(outer, 0, 0, 200, 200));
        });
        var outerForm = Form(doc, page, "Fm1");
        var innerForm = Form(doc, outerForm, "Fm1");

        Assert.Contains("/Fm1 Do", Encoding.Latin1.GetString(outerForm.GetDecodedData()), StringComparison.Ordinal);
        Assert.Contains(" re", Encoding.Latin1.GetString(innerForm.GetDecodedData()), StringComparison.Ordinal);
        Assert.Equal("Transparency", ((PdfName)Assert.IsType<PdfDictionary>(doc.ResolveObject(innerForm[PdfName.Group]))[PdfName.S]).Value);
        Assert.Equal(1, content.Split(new[] { " Do" }, StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// A group cannot be its own content, and marked content inside a group is refused: an MCID in a
    /// form belongs to the form's own parent-tree entry, which this writer does not make — the
    /// caller marks the <c>Do</c> instead.
    /// </summary>
    [Fact]
    public void ASelfGroupAndMarkedContentInsideAGroupAreRefused()
    {
        var page = new PdfContentStreamBuilder();
        Assert.Throws<ArgumentException>(() => page.AddTransparencyGroup(page, 0, 0, 10, 10));

        var marked = new PdfContentStreamBuilder();
        var tree = new PdfStructureTree();
        marked.BeginMarkedContent(tree.Root.AddChild("P"));
        marked.EndMarkedContent();
        Assert.Throws<InvalidOperationException>(() => page.AddTransparencyGroup(marked, 0, 0, 10, 10));

        var plain = new PdfContentStreamBuilder();
        Assert.Equal("Fm1", page.AddTransparencyGroup(plain, 0, 0, 10, 10));   // the control: an unmarked group is taken
    }
}
