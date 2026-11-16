// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Merging carries optional content (layers) across, including which of them are hidden.
/// </summary>
/// <remarks>
/// <para><b>Before this the merge SHOWED content the source hid.</b> The group dictionaries were
/// copied like every other object and the catalog entry that configures them was not — and a layer
/// is hidden only because a configuration says so, so every switched-off layer came out visible in
/// a merge that reported success.</para>
///
/// <para><b>The assertions go through <see cref="PdfOcgManager.ListLayers"/>, not through the
/// catalog.</b> That is the reader a caller would use, it resolves the configuration itself, and it
/// knows nothing about this merge — so it cannot agree with a mistake by sharing one. Each case
/// pairs a layer that must stay hidden with one that must stay visible, because "all layers
/// visible" and "all layers hidden" are each a single wrong answer that half a test would accept.</para>
/// </remarks>
public sealed class OptionalContentMergeTests
{
    /// <summary>
    /// A one-page document with two layers: <c>{prefix} on</c> visible and <c>{prefix} off</c>
    /// hidden.
    /// </summary>
    /// <param name="prefix">Distinguishes the sources, so a merged list can be attributed.</param>
    /// <param name="baseStateOff">
    /// Express the same visibility through <c>/BaseState /OFF</c> plus <c>/ON</c> instead of
    /// through <c>/OFF</c>. The two are equivalent for this document and NOT equivalent once
    /// another document's layers are in the list, which is the case the merge has to rewrite.
    /// </param>
    /// <param name="withConfigs">Add an alternate configuration, which the merge cannot carry.</param>
    private static byte[] LayeredDocument(
        string prefix, bool baseStateOff = false, bool withConfigs = false)
    {
        string config = baseStateOff
            ? "<< /BaseState /OFF /ON [6 0 R] /Order [6 0 R 7 0 R] >>"
            : "<< /OFF [7 0 R] /Order [6 0 R 7 0 R] >>";

        string configs = withConfigs
            ? " /Configs [<< /Name (Alternate) /BaseState /OFF >>]"
            : "";

        const string content = "BT /F1 12 Tf 72 700 Td (layered) Tj ET\n";
        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R 7 0 R] /D " +
            config + configs + " >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /Properties << /L1 6 0 R /L2 7 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /OCG /Name ({prefix} on) >>",
            $"<< /Type /OCG /Name ({prefix} off) >>",
        });
    }

    private static byte[] PlainDocument()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (plain) Tj ET\n";
        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        });
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Merge(params byte[][] inputs)
    {
        var streams = new List<Stream>();
        foreach (byte[] pdf in inputs) streams.Add(new MemoryStream(pdf, writable: false));
        try
        {
            using var output = new MemoryStream();
            PdfMerger.Merge(output, streams.ToArray());
            return output.ToArray();
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    /// <summary>Layer name → whether a reader would show it by default.</summary>
    private static Dictionary<string, bool> Visibility(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfOcgManager.ListLayers(ms).ToDictionary(l => l.Name, l => l.DefaultVisible);
    }

    // ================================================================ the carry

    /// <summary>
    /// Every source's layers survive with the visibility they had — the hidden ones still hidden.
    /// </summary>
    [Fact]
    public void HiddenLayersStayHiddenAndVisibleOnesStayVisible()
    {
        var layers = Visibility(Merge(LayeredDocument("A"), LayeredDocument("B")));

        Assert.Equal(4, layers.Count);
        Assert.True(layers["A on"]);
        Assert.False(layers["A off"]);
        Assert.True(layers["B on"]);
        Assert.False(layers["B off"]);
    }

    /// <summary>
    /// A source expressing the same thing through <c>/BaseState /OFF</c> gets the same result, and
    /// leaves the other source's layers alone.
    /// </summary>
    /// <remarks>
    /// <b>This is the case carrying <c>/BaseState</c> across would break, and it would break it
    /// silently.</b> <c>/BaseState /OFF</c> means "every group in this document is off except those
    /// in <c>/ON</c>" — a statement about one document's groups. Copied into a merged
    /// configuration it switches off the OTHER document's layers too, which is a well-formed
    /// document showing less than either source did. The merge therefore rewrites it as the
    /// explicit list of the groups it actually hid.
    /// </remarks>
    [Fact]
    public void ABaseStateOffSourceDoesNotSwitchOffTheOtherSourcesLayers()
    {
        var layers = Visibility(Merge(
            LayeredDocument("A", baseStateOff: true),
            LayeredDocument("B")));

        Assert.True(layers["A on"]);
        Assert.False(layers["A off"]);
        Assert.True(layers["B on"]);      // the assertion the naive carry fails
        Assert.False(layers["B off"]);

        Assert.NotNull(PdfMerger.LastOptionalContentWarning);
        Assert.Contains("BaseState", PdfMerger.LastOptionalContentWarning);
    }

    /// <summary>
    /// Merging a layered document with a plain one keeps the layers and their visibility.
    /// </summary>
    [Fact]
    public void LayersSurviveAMergeWithAnUnlayeredDocument()
    {
        var layers = Visibility(Merge(LayeredDocument("A"), PlainDocument()));

        Assert.Equal(2, layers.Count);
        Assert.True(layers["A on"]);
        Assert.False(layers["A off"]);
    }

    /// <summary>The panel order is carried, so a viewer still groups the layers as each source did.</summary>
    [Fact]
    public void ThePanelOrderIsCarried()
    {
        byte[] merged = Merge(LayeredDocument("A"), LayeredDocument("B"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var properties = (PdfDictionary)doc.ResolveObject(
            doc.Catalog.Dictionary[PdfName.OCProperties]);
        var config = (PdfDictionary)doc.ResolveObject(properties[PdfName.D]);
        var order = (PdfArray)doc.ResolveObject(config[PdfName.Order]);

        Assert.Equal(4, order.Count);
        var names = new List<string>();
        for (int i = 0; i < order.Count; i++)
        {
            var group = (PdfDictionary)doc.ResolveObject(order[i]);
            names.Add(((PdfString)doc.ResolveObject(group[PdfName.Name])).GetText());
        }
        Assert.Equal(new[] { "A on", "A off", "B on", "B off" }, names);
    }

    // ================================================================ honesty

    /// <summary>Alternate configurations are not carried, and the caller is told.</summary>
    /// <remarks>
    /// A configuration names a complete on/off state for one document; two documents' alternates
    /// are not a set of alternates for the merged one. Concatenating them would offer a viewer
    /// states that switch off half the pages, which looks like a feature and behaves like a defect.
    /// </remarks>
    [Fact]
    public void AlternateConfigurationsAreDroppedAndReported()
    {
        byte[] merged = Merge(LayeredDocument("A", withConfigs: true), LayeredDocument("B"));

        Assert.NotNull(PdfMerger.LastOptionalContentWarning);
        Assert.Contains("Configs", PdfMerger.LastOptionalContentWarning);

        // The default configuration still merged correctly - dropping the alternates costs the
        // caller the alternates, not their layers.
        var layers = Visibility(merged);
        Assert.False(layers["A off"]);
        Assert.True(layers["B on"]);
    }

    /// <summary>
    /// Merging documents with no layers writes no <c>/OCProperties</c>, and reports nothing.
    /// </summary>
    /// <remarks>
    /// An empty <c>/OCProperties</c> is not harmless: §8.11.4.3 makes its presence the signal that
    /// a document uses optional content, so writing one would make every viewer offer an empty
    /// layers panel for a document that has none.
    /// </remarks>
    [Fact]
    public void MergingUnlayeredDocumentsWritesNoOptionalContent()
    {
        byte[] merged = Merge(PlainDocument(), PlainDocument());

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.Catalog.Dictionary[PdfName.OCProperties]);
        Assert.Null(PdfMerger.LastOptionalContentWarning);
    }

    /// <summary>A single layered source comes through unchanged in substance and reports nothing.</summary>
    [Fact]
    public void ASingleLayeredSourceIsUnchangedInSubstance()
    {
        var layers = Visibility(Merge(LayeredDocument("A")));

        Assert.Equal(2, layers.Count);
        Assert.True(layers["A on"]);
        Assert.False(layers["A off"]);
        Assert.Null(PdfMerger.LastOptionalContentWarning);
    }
}
