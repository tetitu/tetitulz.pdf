// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Metadata;

/// <summary>
/// The XMP helpers the three metadata writers share, one decision at a time.
/// </summary>
/// <remarks>
/// <para><b>Written because the consolidation's sweep found the bounds unreached.</b> Every guard here
/// survived against the Accessibility suite, and for reasons that repeat through this file's history:
/// the one adversarial packet was both too deep <i>and</i> too costly, so the depth cap and the
/// indentation budget each hid the other's removal; nothing fed a packet over either size ceiling,
/// a DTD, a byte-order mark, or an <c>rdf:RDF</c> with no description. Each test below sits on the
/// side of one bound that only that bound decides, with a control on the other side.</para>
/// </remarks>
public class XmpPacketDomTests
{
    private const string Rdf = XmpPacket.RdfNamespace;

    private static string Packet(string descriptionInner) =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf + "\">" +
        "<rdf:Description rdf:about=\"\" xmlns:t=\"urn:test\">" + descriptionInner +
        "</rdf:Description></rdf:RDF></x:xmpmeta>";

    // ------------------------------------------------------------------ the catalog packet read

    [Fact]
    public void APacketDecodingPastTheByteCeilingIsNotReadAndTheReasonIsReported()
    {
        string big = Packet("<t:marker>kept</t:marker>" + new string(' ', XmpPacketDom.MaxExistingPacketBytes));
        var reports = new List<string>();

        string read = ReadPacketOf(big, (detail, _) => reports.Add(detail));

        Assert.Null(read);
        Assert.Contains(reports, r => r.Contains("-byte limit", StringComparison.Ordinal));
    }

    [Fact]
    public void APacketUnderTheByteCeilingIsRead()
    {
        string small = Packet("<t:marker>kept</t:marker>");

        Assert.Equal(small, ReadPacketOf(small, null));
    }

    /// <summary>
    /// A flated packet is inflated no further than the ceiling. The read compared the length after
    /// a whole decode, bounded only by the per-stream ceiling sized for images, so a few kilobytes
    /// of flate were inflated in full before the answer — the same null. A stream decoded in full
    /// keeps its bytes and answers any later bounded read; one refused at the ceiling does not.
    /// </summary>
    [Fact]
    public void AdversarialFlatedPacketPastTheCeiling_IsNotInflatedInFull()
    {
        string big = Packet("<t:marker>kept</t:marker>" + new string(' ', 3 * XmpPacketDom.MaxExistingPacketBytes / 2));
        var reports = new List<string>();

        string read = ReadFlatedPacketOf(big, (detail, _) => reports.Add(detail), out PdfStream metadata);

        Assert.Null(read);
        Assert.Contains(reports, r => r.Contains("-byte limit", StringComparison.Ordinal));
        Assert.False(metadata.TryGetDecodedData(1, out _));
    }

    /// <summary>The control: a flated packet under the ceiling is read, and its bytes kept.</summary>
    [Fact]
    public void AFlatedPacketUnderTheCeilingIsRead()
    {
        string small = Packet("<t:marker>kept</t:marker>" + new string(' ', XmpPacketDom.MaxExistingPacketBytes / 2));

        Assert.Equal(small, ReadFlatedPacketOf(small, null, out PdfStream metadata));
        Assert.True(metadata.TryGetDecodedData(1, out _));
    }

    /// <summary>A <c>/Metadata</c> that is not a stream is reported, not silently treated as absent.</summary>
    [Fact]
    public void AMetadataEntryThatIsNotAStreamIsReported()
    {
        byte[] pdf = Pdf("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>", ("4", "<< /Type /Metadata >>"));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var reports = new List<string>();

        string read = XmpPacketDom.ReadCatalogPacket(doc, doc.Catalog.Dictionary, (detail, _) => reports.Add(detail));

        Assert.Null(read);
        Assert.Contains(reports, r => r.Contains("does not point to a stream", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the text budget

    [Fact]
    public void TextPastTheMergeBudgetIsRefusedAndReported()
    {
        var reports = new List<string>();

        Assert.False(XmpPacketDom.IsWithinMergeBudget(
            new string(' ', XmpPacketDom.MaxMergedPacketChars + 1), (d, _) => reports.Add(d)));
        Assert.Single(reports);
        Assert.True(XmpPacketDom.IsWithinMergeBudget(new string(' ', XmpPacketDom.MaxMergedPacketChars)));
    }

    // ------------------------------------------------------------------ the load

    /// <summary>
    /// A DTD is refused, internal entities included — the packet is discarded, not expanded. Parsed,
    /// the entity's text would reach the merged output.
    /// </summary>
    [Fact]
    public void APacketWithADtdIsNotLoaded()
    {
        string withDtd = "<!DOCTYPE x [<!ENTITY e \"INJECTED\">]>" + Packet("<t:marker>&e;</t:marker>");
        var reports = new List<string>();

        Assert.Null(XmpPacketDom.TryLoad(withDtd, (d, _) => reports.Add(d)));
        Assert.Single(reports);
    }

    /// <summary>
    /// The begin attribute of an <c>xpacket</c> is a byte-order mark (ISO 16684-1 §7.3.2), and a
    /// packet read as text can start with one.
    /// </summary>
    [Fact]
    public void APacketStartingWithAByteOrderMarkLoads()
    {
        var doc = XmpPacketDom.TryLoad("\uFEFF" + Packet("<t:marker>kept</t:marker>"));

        Assert.Equal("kept", XmpPacketDom.FindElement(doc!.DocumentElement!, "urn:test", "marker")!.InnerText);
    }

    // ------------------------------------------------------------------ the two bounds, separated

    /// <summary>
    /// A hundred levels deep: well inside the indentation budget, so only the depth cap refuses it.
    /// The adversarial packet the suite already had was twenty thousand deep and tripped both.
    /// </summary>
    [Fact]
    public void APacketDeeperThanTheSearchCapIsNotMergeable()
    {
        var doc = Load(Packet(Nest("t:n", 100)));
        var reports = new List<string>();

        Assert.False(XmpPacketDom.IsMergeable(doc, (d, _) => reports.Add(d)));
        Assert.Contains(reports, r => r.Contains("search cap", StringComparison.Ordinal));
    }

    [Fact]
    public void APacketWithinTheSearchCapIsMergeable()
    {
        Assert.True(XmpPacketDom.IsMergeable(Load(Packet(Nest("t:n", 10)))));
    }

    /// <summary>
    /// Under the depth cap and pathologically wide: twenty thousand leaves at depth sixty cost about
    /// 4.9 million characters of indentation, past the budget, so only the indentation bound refuses
    /// it. Ten thousand is the control.
    /// </summary>
    [Fact]
    public void AWidePacketPastTheIndentationBudgetIsNotMergeable()
    {
        var reports = new List<string>();

        Assert.False(XmpPacketDom.IsMergeable(Load(Packet(WideAtDepth(56, 20000))), (d, _) => reports.Add(d)));
        Assert.Contains(reports, r => r.Contains("output limit", StringComparison.Ordinal));
        Assert.True(XmpPacketDom.IsMergeable(Load(Packet(WideAtDepth(56, 10000)))));
    }

    // ------------------------------------------------------------------ search, skeleton, description

    [Fact]
    public void TheSearchStopsAtItsDepthCap()
    {
        var deep = Load(Packet(Nest("t:n", 70, "<t:target/>")));
        var shallow = Load(Packet(Nest("t:n", 10, "<t:target/>")));

        Assert.Null(XmpPacketDom.FindElement(deep.DocumentElement!, "urn:test", "target"));
        Assert.NotNull(XmpPacketDom.FindElement(shallow.DocumentElement!, "urn:test", "target"));
    }

    [Fact]
    public void AFreshSkeletonCarriesTheToolkitOnlyWhenOneIsNamed()
    {
        var named = XmpPacketDom.CreateEmpty("Tetitulz.Pdf Accessibility");
        var unnamed = XmpPacketDom.CreateEmpty();

        Assert.Equal("Tetitulz.Pdf Accessibility",
            named.DocumentElement!.GetAttribute("xmptk", XmpPacket.XmpMetaNamespace));
        Assert.False(unnamed.DocumentElement!.HasAttribute("xmptk", XmpPacket.XmpMetaNamespace));
    }

    /// <summary>
    /// An <c>rdf:RDF</c> with no description gets one, attached — a detached element would take every
    /// property written into it out of the output.
    /// </summary>
    [Fact]
    public void AMissingDescriptionIsCreatedInsideTheRdfElement()
    {
        var doc = Load("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf + "\"/></x:xmpmeta>");

        XmlElement description = XmpPacketDom.EnsureDescription(doc)!;

        Assert.NotNull(description);
        Assert.Equal("RDF", description.ParentNode!.LocalName);
        Assert.Contains("rdf:Description", XmpPacketDom.Serialize(doc), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static XmlDocument Load(string xmp)
    {
        var doc = XmpPacketDom.TryLoad(xmp);
        Assert.NotNull(doc);
        return doc!;
    }

    private static string Nest(string element, int depth, string inner = "")
    {
        var sb = new StringBuilder();
        for (int i = 0; i < depth; i++) sb.Append('<').Append(element).Append('>');
        sb.Append(inner);
        for (int i = 0; i < depth; i++) sb.Append("</").Append(element).Append('>');
        return sb.ToString();
    }

    private static string WideAtDepth(int depth, int leaves)
        => Nest("t:n", depth, string.Concat(Enumerable.Repeat("<t:leaf/>", leaves)));

    private static string? ReadPacketOf(string xmp, Action<string, Exception?>? report)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(xmp);
        byte[] pdf = Pdf("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            ("4", "<< /Type /Metadata /Subtype /XML /Length " + bytes.Length + " >>\nstream\n" +
                  Encoding.Latin1.GetString(bytes) + "\nendstream"));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return XmpPacketDom.ReadCatalogPacket(doc, doc.Catalog.Dictionary, report!);
    }

    private static string? ReadFlatedPacketOf(string xmp, Action<string, Exception?>? report, out PdfStream metadata)
    {
        byte[] bytes;
        using (var output = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(
                       output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                z.Write(Encoding.UTF8.GetBytes(xmp));
            bytes = output.ToArray();
        }
        byte[] pdf = Pdf("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            ("4", "<< /Type /Metadata /Subtype /XML /Filter /FlateDecode /Length " + bytes.Length +
                  " >>\nstream\n" + Encoding.Latin1.GetString(bytes) + "\nendstream"));
        var doc = PdfDocument.Open(new MemoryStream(pdf));
        metadata = (PdfStream)doc.GetObject(4);
        return XmpPacketDom.ReadCatalogPacket(doc, doc.Catalog.Dictionary, report!);
    }

    /// <summary>A one-page document with the given catalog and extra numbered objects.</summary>
    private static byte[] Pdf(string catalog, params (string Num, string Body)[] extra)
    {
        var bodies = new List<(string, string)>
        {
            ("1", catalog),
            ("2", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            ("3", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
        };
        bodies.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new SortedDictionary<int, int>();
        foreach (var (num, body) in bodies)
        {
            offsets[int.Parse(num)] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(num).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int max = offsets.Keys.Max();
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(max + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= max; i++)
            sb.Append(offsets.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        sb.Append("trailer\n<< /Size ").Append(max + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
