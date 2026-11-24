// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A rewrite of one catalog name tree (here the attachments) refuses a <c>/Names</c> dictionary the
/// parser cannot read. Read as none, it would make the rewrite write a <c>/Names</c> holding only
/// the edited tree, dropping the rest: the document JavaScript, the named destinations.
/// </summary>
public sealed class CatalogNameTreeUnreadableTests
{
    [Fact]
    public void Adversarial_unreadable_names_dictionary_refuses_an_added_attachment()
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfAttachmentManager.AddAttachment(
            new MemoryStream(Pdf(readableNames: false)), output, "a.txt", new byte[] { 1, 2, 3 }));

        Assert.Contains("/Names dictionary could not be read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Adversarial_unreadable_names_dictionary_refuses_a_removed_attachment()
    {
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfAttachmentManager.RemoveAttachment(
            new MemoryStream(Pdf(readableNames: false)), new MemoryStream(), "a.txt"));

        Assert.Contains("/Names dictionary could not be read", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A /Names that names an object the file does not have is absent, not unreadable, so the
    /// attachment is added under a new /Names.
    /// </summary>
    [Fact]
    public void A_names_reference_to_a_missing_object_is_absent_and_the_attachment_is_added()
    {
        var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(
            new MemoryStream(Pdf(readableNames: false, namesRef: "9 0 R")), output, "a.txt", new byte[] { 1, 2, 3 });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var names = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]));
        Assert.NotNull(names[PdfName.EmbeddedFiles]);
    }

    /// <summary>The control: a readable /Names keeps its JavaScript tree beside the new attachment.</summary>
    [Fact]
    public void A_readable_names_dictionary_keeps_its_other_trees()
    {
        var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(new MemoryStream(Pdf(readableNames: true)), output, "a.txt", new byte[] { 1, 2, 3 });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var names = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]));
        Assert.NotNull(names[new PdfName("JavaScript")]);
        Assert.NotNull(names[PdfName.EmbeddedFiles]);
    }

    /// <summary>
    /// A one-page document whose catalog names object 4 as its <c>/Names</c>, which holds a
    /// <c>/JavaScript</c> tree, or is a lone <c>]</c> the parser cannot read.
    /// </summary>
    private static byte[] Pdf(bool readableNames, string namesRef = "4 0 R")
    {
        string[] bodies =
        {
            $"<< /Type /Catalog /Pages 2 0 R /Names {namesRef} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            readableNames ? "<< /JavaScript << /Names [(s) 5 0 R] >> >>" : "]",
            "<< /S /JavaScript /JS (1) >>",
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
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
