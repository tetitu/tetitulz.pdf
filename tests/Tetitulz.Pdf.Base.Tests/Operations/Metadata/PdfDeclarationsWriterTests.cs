// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Xml;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Metadata;

/// <summary>
/// <see cref="PdfDeclarationsWriter"/> — the PDF Association "PDF Declarations" XMP schema
/// (<c>pdfd:declarations</c>), verified against the specification's own worked example (a
/// <c>pdfd:declarations</c>/<c>rdf:Bag</c> of <c>rdf:li rdf:parseType="Resource"</c> entries, each
/// carrying <c>pdfd:conformsTo</c> and an optional <c>pdfd:claimData</c> block with
/// <c>pdfd:claimBy</c>/<c>pdfd:claimDate</c>/<c>pdfd:claimCredentials</c>/<c>pdfd:claimReport</c>).
/// </summary>
public class PdfDeclarationsWriterTests
{
    private const string ConformsTo = "https://example.org/standards/accessibility-v2";

    [Fact]
    public void AddDeclaration_WritesConformsTo()
    {
        byte[] pdf = AddDeclaration(ConformsTo);

        XmlDocument xmp = ReadXmp(pdf);
        XmlElement conformsTo = FindSingle(xmp, "conformsTo");
        Assert.Equal(ConformsTo, conformsTo.InnerText);

        // The declaration entry must be an rdf:li rdf:parseType="Resource" inside
        // pdfd:declarations/rdf:Bag — the shape the specification's own example uses.
        var li = (XmlElement)conformsTo.ParentNode;
        Assert.Equal("li", li.LocalName);
        Assert.Equal("Resource", li.GetAttribute("parseType", Ns.Rdf));

        var bag = (XmlElement)li.ParentNode;
        Assert.Equal("Bag", bag.LocalName);
        Assert.Equal(Ns.Rdf, bag.NamespaceURI);

        var declarations = (XmlElement)bag.ParentNode;
        Assert.Equal("declarations", declarations.LocalName);
        Assert.Equal(PdfDeclarationsWriter.PdfDeclarationsNamespace, declarations.NamespaceURI);
    }

    [Fact]
    public void AddDeclaration_WithClaimData_WritesAllFields()
    {
        var date = new DateTimeOffset(2026, 1, 17, 0, 0, 0, TimeSpan.Zero);
        byte[] pdf = AddDeclaration(ConformsTo,
            claimBy: "Acme Conformance Labs",
            claimDate: date,
            claimCredentials: "ISO 17065 accredited",
            claimReport: "https://example.org/reports/12345.html");

        XmlDocument xmp = ReadXmp(pdf);
        Assert.Equal("Acme Conformance Labs", FindSingle(xmp, "claimBy").InnerText);
        Assert.Equal("2026-01-17", FindSingle(xmp, "claimDate").InnerText);
        Assert.Equal("ISO 17065 accredited", FindSingle(xmp, "claimCredentials").InnerText);
        Assert.Equal("https://example.org/reports/12345.html", FindSingle(xmp, "claimReport").InnerText);
    }

    [Fact]
    public void AddDeclaration_NoClaimData_OmitsClaimDataElement()
    {
        byte[] pdf = AddDeclaration(ConformsTo);

        XmlDocument xmp = ReadXmp(pdf);
        var nsm = NsManager(xmp);
        Assert.Null(xmp.SelectSingleNode("//pdfd:claimData", nsm));
    }

    /// <summary>
    /// Merge, don't clobber: a second declaration is appended beside the first, not in place of
    /// it — a document can carry independent claims from more than one source.
    /// </summary>
    [Fact]
    public void AddDeclaration_CalledTwice_PreservesBothDeclarations()
    {
        byte[] first = AddDeclaration(ConformsTo);

        using var input = new MemoryStream(first);
        using var output = new MemoryStream();
        const string second = "https://example.org/standards/privacy-v1";
        bool ok = PdfDeclarationsWriter.AddDeclaration(input, output, second);
        Assert.True(ok, PdfDeclarationsWriter.LastError);

        XmlDocument xmp = ReadXmp(output.ToArray());
        var nsm = NsManager(xmp);
        var conformsToNodes = xmp.SelectNodes("//pdfd:conformsTo", nsm);
        Assert.NotNull(conformsToNodes);
        Assert.Equal(2, conformsToNodes.Count);

        var values = new[] { conformsToNodes[0].InnerText, conformsToNodes[1].InnerText };
        Assert.Contains(ConformsTo, values);
        Assert.Contains(second, values);
    }

    /// <summary>An existing, unrelated XMP property (dc:title) survives a declaration being added.</summary>
    [Fact]
    public void AddDeclaration_PreservesExistingUnrelatedXmpProperty()
    {
        string existingXmp =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Quarterly Report</rdf:li></rdf:Alt></dc:title>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>\n" +
            "<?xpacket end=\"w\"?>";

        byte[] pdf = BuildPdfWithMetadata(existingXmp);
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        bool ok = PdfDeclarationsWriter.AddDeclaration(input, output, ConformsTo);
        Assert.True(ok, PdfDeclarationsWriter.LastError);

        XmlDocument xmp = ReadXmp(output.ToArray());
        var nsm = NsManager(xmp);
        Assert.Equal("Quarterly Report", xmp.SelectSingleNode("//dc:title//rdf:li", nsm)?.InnerText);
        Assert.Equal(ConformsTo, FindSingle(xmp, "conformsTo").InnerText);
    }

    [Fact]
    public void AddDeclaration_EmptyConformsTo_Throws()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => PdfDeclarationsWriter.AddDeclaration(input, output, ""));
    }

    [Fact]
    public void AddDeclaration_EncryptedDocument_FailsClosedWithoutThrowing()
    {
        byte[] pdf = EncryptedLookingPdf();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        bool ok = PdfDeclarationsWriter.AddDeclaration(input, output, ConformsTo);

        Assert.False(ok);
        Assert.Contains("encrypted", PdfDeclarationsWriter.LastError, StringComparison.OrdinalIgnoreCase);
        // Fails closed: the output is the original bytes, unmodified — never a clear-text
        // revision atop an encrypted document's cross-reference table.
        Assert.Equal(pdf, output.ToArray());
    }

    // ====================================================================
    //  Fixtures / helpers
    // ====================================================================

    private static class Ns
    {
        public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    }

    private static byte[] AddDeclaration(
        string conformsTo, string claimBy = null, DateTimeOffset? claimDate = null,
        string claimCredentials = null, string claimReport = null)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        bool ok = PdfDeclarationsWriter.AddDeclaration(
            input, output, conformsTo, claimBy, claimDate, claimCredentials, claimReport);
        Assert.True(ok, PdfDeclarationsWriter.LastError);
        return output.ToArray();
    }

    private static XmlElement FindSingle(XmlDocument xmp, string localName)
    {
        var nsm = NsManager(xmp);
        var node = xmp.SelectSingleNode($"//pdfd:{localName}", nsm) as XmlElement;
        Assert.NotNull(node);
        return node;
    }

    private static XmlNamespaceManager NsManager(XmlDocument xmp)
    {
        var nsm = new XmlNamespaceManager(xmp.NameTable);
        nsm.AddNamespace("rdf", Ns.Rdf);
        nsm.AddNamespace("pdfd", PdfDeclarationsWriter.PdfDeclarationsNamespace);
        nsm.AddNamespace("dc", "http://purl.org/dc/elements/1.1/");
        return nsm;
    }

    private static XmlDocument ReadXmp(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var catalog = doc.Catalog?.Dictionary;
        Assert.NotNull(catalog);

        var metaObj = doc.ResolveObject(catalog[PdfName.Metadata]);
        var metaStream = Assert.IsType<PdfStream>(metaObj);
        byte[] bytes = metaStream.GetDecodedData();
        string text = Encoding.UTF8.GetString(bytes).Trim('﻿', '\0');

        // Strip the xpacket processing instructions before parsing as plain XML.
        int start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        int end = text.LastIndexOf("</x:xmpmeta>", StringComparison.Ordinal) + "</x:xmpmeta>".Length;
        string xml = text.Substring(start, end - start);

        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(xml);
        return xmlDoc;
    }

    internal static byte[] EncryptedLookingPdf()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");
        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int obj2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int obj3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Filter /Standard /V 2 /R 3 /O <00> /U <00> /P -4 >>\nendobj\n");

        int xrefOffset = pdf.Length;
        pdf.Append("xref\n0 4\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3);
        pdf.Append("trailer\n<< /Size 4 /Root 1 0 R /Encrypt 3 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    internal static byte[] BuildPdfWithMetadata(string xmp)
    {
        byte[] xmpBytes = Encoding.UTF8.GetBytes(xmp);

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");
        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>\nendobj\n");
        int obj2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int obj4 = pdf.Length;
        pdf.Append("4 0 obj\n<< /Type /Metadata /Subtype /XML /Length ").Append(xmpBytes.Length).Append(" >>\nstream\n");
        byte[] prefix = Encoding.ASCII.GetBytes(pdf.ToString());

        using var ms = new MemoryStream();
        ms.Write(prefix, 0, prefix.Length);
        ms.Write(xmpBytes, 0, xmpBytes.Length);

        var tail = new StringBuilder();
        tail.Append("\nendstream\nendobj\n");
        var xrefSb = new StringBuilder();
        xrefSb.Append("xref\n0 5\n");
        xrefSb.Append("0000000000 65535 f \n");
        xrefSb.AppendFormat("{0:D10} 00000 n \n", obj1);
        xrefSb.AppendFormat("{0:D10} 00000 n \n", obj2);
        xrefSb.AppendFormat("{0:D10} 00000 n \n", obj3);
        xrefSb.AppendFormat("{0:D10} 00000 n \n", obj4);
        xrefSb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");

        byte[] tailBytes = Encoding.ASCII.GetBytes(tail.ToString());
        ms.Write(tailBytes, 0, tailBytes.Length);
        int realXrefOffset = (int)ms.Length;
        xrefSb.AppendFormat("startxref\n{0}\n%%EOF\n", realXrefOffset);
        byte[] xrefBytes = Encoding.ASCII.GetBytes(xrefSb.ToString());
        ms.Write(xrefBytes, 0, xrefBytes.Length);

        return ms.ToArray();
    }
}
