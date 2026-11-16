// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A writer that appends a revision refuses a trailer whose <c>/Root</c> is written directly
/// (<c>Core.Writing.CatalogObject</c>). The revision can carry no catalog, so the bookmarks manager
/// wrote an outline nothing pointed to, and the form flattener stamped the widgets and left
/// <c>/AcroForm</c> in place, both without a word.
/// </summary>
public sealed class DirectRootRevisionRefusalTests
{
    [Fact]
    public void Adversarial_direct_root_refuses_new_bookmarks()
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() =>
            PdfBookmarksManager.WriteBookmarks(new MemoryStream(Pdf(directRoot: true)), output, Bookmarks()));

        Assert.Contains("/Root is not an indirect reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Adversarial_direct_root_refuses_a_flatten()
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() =>
            PdfFormFlattener.Flatten(new MemoryStream(Pdf(directRoot: true)), output));

        Assert.Contains("/Root is not an indirect reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The controls: an indirect /Root gets the outline linked and the form removed.</summary>
    [Fact]
    public void An_indirect_root_takes_the_bookmarks_and_the_flatten()
    {
        var marked = new MemoryStream();
        PdfBookmarksManager.WriteBookmarks(new MemoryStream(Pdf(directRoot: false)), marked, Bookmarks());
        using (var doc = PdfDocument.Open(new MemoryStream(marked.ToArray())))
            Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]));

        var flat = new MemoryStream();
        PdfFormFlattener.Flatten(new MemoryStream(Pdf(directRoot: false)), flat);
        using (var doc = PdfDocument.Open(new MemoryStream(flat.ToArray())))
            Assert.Null(doc.Catalog.Dictionary[PdfName.AcroForm]);
    }

    /// <summary>
    /// Every other writer that supersedes the catalog refuses the same file, through the same
    /// <c>CatalogObject.NumberOrRefuse</c>. The page labels went out under object 0, which the
    /// revision writer skips — the tree appended with nothing pointing to it, and the call returned —
    /// and removing a destination answered "not found" for one the catalog's <c>/Dests</c> holds.
    /// The rest refused in words of their own, and took <c>/Root 0 0 R</c> for a catalog.
    /// </summary>
    [Theory]
    [MemberData(nameof(CatalogWriters))]
    public void Adversarial_direct_root_refuses_every_catalog_writer(string writer)
    {
        var output = new MemoryStream();
        var ex = Assert.ThrowsAny<PdfException>(() => Write(writer, Pdf(directRoot: true), output));

        Assert.Contains("/Root is not an indirect reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The controls: with an indirect <c>/Root</c> each writer's change is in the catalog it wrote.</summary>
    [Theory]
    [MemberData(nameof(CatalogWriters))]
    public void An_indirect_root_takes_every_catalog_writer(string writer)
    {
        var output = new MemoryStream();
        Write(writer, Pdf(directRoot: false), output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var catalog = doc.Catalog.Dictionary;
        switch (writer)
        {
            case "labels": Assert.NotNull(doc.ResolveObject(catalog[PdfName.PageLabels])); break;
            case "portfolio": Assert.NotNull(doc.ResolveObject(catalog[PdfName.Collection])); break;
            case "field":
                var form = (PdfDictionary)doc.ResolveObject(catalog[PdfName.AcroForm]);
                Assert.Equal(2, ((PdfArray)doc.ResolveObject(form[PdfName.Fields])).Count);
                break;
            case "view": Assert.Equal(new PdfName("UseOutlines"), catalog[PdfName.PageMode]); break;
            case "set-dest":
                Assert.Contains(PdfNamedDestinations.ListDestinations(new MemoryStream(output.ToArray())), d => d.Name == "Next");
                break;
            case "remove-dest":
                Assert.DoesNotContain(PdfNamedDestinations.ListDestinations(new MemoryStream(output.ToArray())), d => d.Name == "Home");
                break;
            case "attachment": Assert.Single(PdfAttachmentManager.ListAttachments(new MemoryStream(output.ToArray()))); break;
        }
    }

    public static TheoryData<string> CatalogWriters() =>
        new() { "labels", "portfolio", "field", "view", "set-dest", "remove-dest", "attachment" };

    private static void Write(string writer, byte[] pdf, Stream output)
    {
        var input = new MemoryStream(pdf);
        switch (writer)
        {
            case "labels":
                PdfPageLabels.SetLabels(input, output, new[] { new PageLabelRange { StartPageIndex = 0, Style = "r" } });
                break;
            case "portfolio": Tetitulz.Pdf.Base.Operations.Documents.PdfPortfolioManager.ConvertToPortfolio(input, output); break;
            case "field":
                PdfFormFieldBuilder.AddTextField(input, output, new FormTextField { FieldName = "added", X = 10, Y = 50 });
                break;
            case "view": PdfInitialView.SetInitialView(input, output, new InitialViewSettings { PageMode = "UseOutlines" }); break;
            case "set-dest": PdfNamedDestinations.SetDestination(input, output, "Next", 0); break;
            case "remove-dest": Assert.True(PdfNamedDestinations.RemoveDestination(input, output, "Home")); break;
            case "attachment":
                Tetitulz.Pdf.Base.Operations.PdfAttachmentManager.AddAttachment(input, output, "a.txt", new byte[] { 65 });
                break;
        }
    }

    private static List<PdfBookmark> Bookmarks() => new() { new PdfBookmark("First", 1, 0) };

    /// <summary>
    /// A one-page document with one text field and one named destination, its catalog in object 1
    /// and named by the trailer either as <c>1 0 R</c> or written out directly.
    /// </summary>
    private static byte[] Pdf(bool directRoot)
    {
        const string catalog = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> /Dests << /Home [3 0 R /Fit] >> >>";
        string[] bodies =
        {
            catalog,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (x) /Rect [10 10 60 30] /P 3 0 R >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root ")
          .Append(directRoot ? catalog : "1 0 R").Append(" >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
