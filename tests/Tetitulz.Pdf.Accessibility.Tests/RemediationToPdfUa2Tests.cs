// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The remediation pipeline writes PDF/UA-2 output when asked for it (<see cref="RemediationOptions.PdfUaPart"/>
/// 2): the tree in the PDF 2.0 standard structure namespace, <c>pdfuaid:rev</c> beside
/// <c>pdfuaid:part</c>, and the file's version 2.0 in the catalog — an untagged page comes out conforming
/// under the PDF/UA-2 audit, as it does under PDF/UA-1, and a part 1 run writes none of it.
/// </summary>
public sealed class RemediationToPdfUa2Tests
{
    private static byte[] Untagged()
    {
        string content = "BT /F1 18 Tf 72 720 Td (Heading) Tj ET\nBT /F1 12 Tf 72 680 Td (Hello hello hello) Tj ET\n";
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var off = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++) { off[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in off) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static (RemediationReport Report, byte[] Output) Remediate(int part)
    {
        using var input = new MemoryStream(Untagged());
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(input, output, new RemediationOptions
        {
            PdfUaPart = part,
            Title = "Remediated",
            EmbedMissingFonts = true,
            FontEmbedding = new FontEmbeddingOptions { FontProvider = _ => MinimalTrueType.Build("Hadeglino ") },
        });
        return (report, output.ToArray());
    }

    [Fact]
    public void AnUntaggedPage_RemediatedForPart2_ConformsUnderThePart2Audit()
    {
        var (report, output) = Remediate(2);

        Assert.True(report.AfterAudit.IsConforming,
            string.Join("\n", report.AfterAudit.Issues.Select(i => i.Code + ": " + i.Message)));

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        Assert.Equal("2.0", ((PdfName)doc.Catalog.Dictionary[PdfName.Version]).Value);
        Assert.Equal(2, doc.Version.Major);

        string xmp = Encoding.UTF8.GetString(((PdfStream)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata])).GetDecodedData());
        Assert.Equal(new[] { "2024" }, Texts(xmp, PdfUaXmpWriter.PdfUaIdNamespace, "rev"));

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]);
        Assert.NotNull(root[new PdfName("Namespaces")]);
    }

    [Fact]
    public void AnUntaggedPage_RemediatedForPart1_WritesNoneOfIt()
    {
        var (report, output) = Remediate(1);

        Assert.True(report.AfterAudit.IsConforming);
        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        Assert.Null(doc.Catalog.Dictionary[PdfName.Version]);
        string xmp = Encoding.UTF8.GetString(((PdfStream)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata])).GetDecodedData());
        Assert.Empty(Texts(xmp, PdfUaXmpWriter.PdfUaIdNamespace, "rev"));
        Assert.Equal(new[] { "1" }, Texts(xmp, PdfUaXmpWriter.PdfUaIdNamespace, "part"));   // the potency half
        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]);
        Assert.Null(root[new PdfName("Namespaces")]);
    }

    [Fact]
    public void APdfA2PacketsPdfUaSchemaDescription_GainsTheRevision()
    {
        string described = PdfUaXmpWriter.BuildPdfUaXmpPacket(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"2\" pdfaid:conformance=\"A\"/>" +
            "</rdf:RDF></x:xmpmeta>", 1, null, null, null);
        Assert.Equal(new[] { "part" }, Texts(described, PropertyNs, "name"));

        string part2 = PdfUaXmpWriter.BuildPdfUaXmpPacket(described, 2, null, null, null);
        Assert.Equal(new[] { "part", "rev" }, Texts(part2, PropertyNs, "name"));
        Assert.Equal(new[] { PdfUaXmpWriter.PdfUaIdNamespace }, Texts(part2, "http://www.aiim.org/pdfa/ns/schema#", "namespaceURI"));
    }

    private const string PropertyNs = "http://www.aiim.org/pdfa/ns/property#";

    /// <summary>The text of every element <paramref name="localName"/> in <paramref name="ns"/>, the packet parsed.</summary>
    private static string[] Texts(string packet, string ns, string localName)
    {
        var xml = new System.Xml.XmlDocument { XmlResolver = null };
        xml.LoadXml(packet.Trim());
        return xml.GetElementsByTagName(localName, ns).Cast<System.Xml.XmlNode>().Select(n => n.InnerText.Trim()).ToArray();
    }
}