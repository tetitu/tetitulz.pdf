// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfOcgManager.CreateLayer"/> refuses optional-content entries it cannot read. Read as
/// absent, an unreadable <c>/OCProperties</c> or <c>/OCGs</c> was replaced by one listing only the
/// new layer, and the document's other layers fell out of its inventory.
/// </summary>
public sealed class OcgCreateLayerUnreadableTests
{
    [Theory]
    [InlineData(Unreadable.Properties, "/OCProperties could not be read")]
    [InlineData(Unreadable.Groups, "/OCGs could not be read")]
    [InlineData(Unreadable.Configuration, "default configuration /D could not be read")]
    [InlineData(Unreadable.OnList, "/ON could not be read")]
    [InlineData(Unreadable.OffList, "/OFF could not be read")]
    public void Adversarial_unreadable_optional_content_refuses_a_new_layer(Unreadable what, string reason)
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfOcgManager.CreateLayer(new MemoryStream(Pdf(what)), output, "Added"));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: a readable inventory keeps its layer beside the new one.</summary>
    [Fact]
    public void A_readable_inventory_keeps_its_layers()
    {
        var output = new MemoryStream();
        PdfOcgManager.CreateLayer(new MemoryStream(Pdf(Unreadable.Nothing)), output, "Added");

        var names = PdfOcgManager.ListLayers(new MemoryStream(output.ToArray())).Select(l => l.Name).ToList();
        Assert.Equal(new[] { "Existing", "Added" }, names);
    }

    /// <summary>
    /// <see cref="PdfOcgManager.SetLayerVisibility"/> refuses the same entries. An unreadable
    /// <c>/ON</c> or <c>/OFF</c> read as empty, and the lists written back held only the target: every
    /// layer the document hid was shown, or every other one hidden. An unreadable <c>/OCProperties</c>,
    /// <c>/D</c> or <c>/OCGs</c> answered "no such layer".
    /// </summary>
    [Theory]
    [InlineData(Unreadable.Properties, "/OCProperties could not be read")]
    [InlineData(Unreadable.Groups, "/OCGs could not be read")]
    [InlineData(Unreadable.Configuration, "default configuration /D could not be read")]
    [InlineData(Unreadable.OnList, "/ON could not be read")]
    [InlineData(Unreadable.OffList, "/OFF could not be read")]
    public void Adversarial_unreadable_optional_content_refuses_a_visibility_change(Unreadable what, string reason)
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfOcgManager.SetLayerVisibility(new MemoryStream(Pdf(what)), output, "Existing", false));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: a readable configuration takes the change, and the layer is hidden.</summary>
    [Fact]
    public void A_readable_configuration_takes_a_visibility_change()
    {
        var output = new MemoryStream();
        Assert.Equal(1, PdfOcgManager.SetLayerVisibility(new MemoryStream(Pdf(Unreadable.Nothing)), output, "Existing", false));

        var layer = Assert.Single(PdfOcgManager.ListLayers(new MemoryStream(output.ToArray())));
        Assert.False(layer.DefaultVisible);
    }

    /// <summary>
    /// <see cref="PdfOcgManager.AssignPageContentToLayer"/> refuses a page whose <c>/Resources</c> or
    /// <c>/Properties</c> it cannot read: rebuilt from nothing, the page kept only the layer's entry,
    /// and its fonts and images went. The readable page is the control.
    /// </summary>
    [Theory]
    [InlineData("/Resources 10 0 R", "]", "/Resources could not be read")]
    [InlineData("/Resources << /Properties 10 0 R >>", "]", "/Properties could not be read")]
    [InlineData("/Resources 10 0 R", "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>", null)]
    public void Adversarial_unreadable_page_resources_refuse_a_layer(string resources, string object10, string reason)
    {
        byte[] pdf = Pdf(Unreadable.Nothing, resources, object10);
        var output = new MemoryStream();
        if (reason == null)
        {
            Assert.True(PdfOcgManager.AssignPageContentToLayer(new MemoryStream(pdf), output, 0, 6));
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(output.ToArray()));
            var res = doc.Pages[0].Resources;
            Assert.NotNull(doc.ResolveObject(res[Tetitulz.Pdf.Core.Objects.PdfName.Font]));
            Assert.NotNull(doc.ResolveObject(res[Tetitulz.Pdf.Core.Objects.PdfName.Properties]));
            return;
        }

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfOcgManager.AssignPageContentToLayer(new MemoryStream(pdf), output, 0, 6));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    public enum Unreadable { Nothing, Properties, Groups, Configuration, OnList, OffList }

    /// <summary>
    /// A one-page document with one layer: the catalog's <c>/OCProperties</c> (object 4) lists its
    /// groups through object 5, which names group 6, and its default configuration is object 7. The
    /// unreadable one is a lone <c>]</c>. The page's <paramref name="pageResources"/> may name object
    /// 10, <paramref name="object10"/>.
    /// </summary>
    private static byte[] Pdf(Unreadable what, string pageResources = "", string object10 = "null")
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 11 0 R " + pageResources + " >>",
            what == Unreadable.Properties ? "]" : "<< /OCGs 5 0 R /D 7 0 R >>",
            what == Unreadable.Groups ? "]" : "[6 0 R]",
            "<< /Type /OCG /Name (Existing) >>",
            what == Unreadable.Configuration ? "]" : "<< /ON 8 0 R /OFF 9 0 R /Order [6 0 R] >>",
            what == Unreadable.OnList ? "]" : "[6 0 R]",
            what == Unreadable.OffList ? "]" : "[]",
            object10,
            "<< /Length 3 >>\nstream\nq Q\nendstream",
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
