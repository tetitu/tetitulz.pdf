// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The XMP packet the two conformance converters stamp, asserted by <b>parsing it as XML</b>.
///
/// <para><b>Why parsing rather than matching.</b> A substring check for
/// <c>&lt;dc:title&gt;</c> passes on a packet that is not well-formed XML at all — which is exactly
/// what an unescaped <c>&lt;</c> in a document title produces, and exactly the state every reader
/// downstream (the published profiles, a common viewer, this library's own checker) would refuse. Loading the packet into
/// an <see cref="XDocument"/> is the independent second opinion; reading the title back out of it
/// and comparing against the ORIGINAL string then catches the opposite error, double-escaping,
/// which is well-formed and silently wrong.</para>
///
/// <para>Both converters wrote their own envelope, their own W3CDTF date line and their own
/// byte-identical five-entity escaper. They now share <c>XmpConformancePacket</c>, and neither had
/// a test covering the escape.</para>
/// </summary>
public class ConformanceXmpPacketTests
{
    /// <summary>
    /// Every character the five-entity escape exists for, plus text either side so a truncation
    /// cannot pass. The apostrophe and double quote matter here as much as the angle brackets:
    /// the packet writes <c>xml:lang="x-default"</c> on the very element carrying this text.
    /// </summary>
    private const string HostileTitle = "Ampersand & angle <b> quote \" apostrophe ' end";

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace PdfaId = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly XNamespace PdfX = "http://ns.adobe.com/pdfx/1.3/";

    [Fact]
    public void APdfAPacketSurvivesEveryCharacterTheEscapeExistsFor()
    {
        XDocument packet = ConvertToPdfAAndParseXmp(HostileTitle);

        Assert.Equal(HostileTitle, TitleOf(packet));
        Assert.Equal("1", SchemaValue(packet, PdfaId, "part"));
        Assert.Equal("B", SchemaValue(packet, PdfaId, "conformance"));
    }

    [Fact]
    public void APdfXPacketSurvivesEveryCharacterTheEscapeExistsFor()
    {
        XDocument packet = ConvertToPdfXAndParseXmp(HostileTitle);

        Assert.Equal(HostileTitle, TitleOf(packet));
        Assert.False(string.IsNullOrEmpty(SchemaValue(packet, PdfX, "GTS_PDFXVersion")));
    }

    /// <summary>
    /// PDF/A sets <c>xmp:CreatorTool</c> and PDF/X does not — the one asymmetry the shared builder
    /// has to preserve, and the reason its <c>creatorTool</c> parameter is nullable rather than
    /// defaulted. A builder that always emitted it, or never did, passes every other test here.
    /// </summary>
    [Fact]
    public void OnlyThePdfAPacketCarriesACreatorTool()
    {
        Assert.NotNull(ConvertToPdfAAndParseXmp("t").Descendants(Xmp + "CreatorTool").FirstOrDefault());
        Assert.Null(ConvertToPdfXAndParseXmp("t").Descendants(Xmp + "CreatorTool").FirstOrDefault());
    }

    /// <summary>
    /// Both packets carry a W3CDTF <c>xmp:ModifyDate</c>. The invariant-calendar defect this pins
    /// against is covered under a th-TH culture by <c>ConformanceMediumFindingsTests</c>; what is
    /// asserted here is that the shared builder emits the element for BOTH converters, since the
    /// line moved out of two hand-written templates into one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothPacketsCarryAW3CdtfModifyDate(bool pdfA)
    {
        XDocument packet = pdfA ? ConvertToPdfAAndParseXmp("t") : ConvertToPdfXAndParseXmp("t");

        string date = packet.Descendants(Xmp + "ModifyDate").Single().Value;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", date);
    }

    // ------------------------------------------------------------------ helpers

    private static string TitleOf(XDocument packet)
        => packet.Descendants(Dc + "title").Single().Descendants(Rdf + "li").Single().Value;

    private static string SchemaValue(XDocument packet, XNamespace ns, string local)
        => packet.Descendants(ns + local).SingleOrDefault()?.Value;

    private static XDocument ConvertToPdfAAndParseXmp(string title)
    {
        using var input = new MemoryStream(PlainPdf());
        using var output = new MemoryStream();
        PdfAConverter.ConvertToPdfA1b(input, output, new PdfAConversionOptions
        {
            IccProfile = IccProfileFixtures.Cmyk(), // matches PageContent's CMYK fill (A17)
            DocumentTitle = title,
        });
        return ParseXmp(output.ToArray());
    }

    private static XDocument ConvertToPdfXAndParseXmp(string title)
    {
        using var input = new MemoryStream(PlainPdf());
        using var output = new MemoryStream();
        var r = PdfXConverter.Convert(input, output, new PdfXConversionOptions
        {
            IccProfile = IccProfileFixtures.Cmyk(),
            OutputConditionIdentifier = "FOGRA39",
            Trapped = false,
            DocumentTitle = title,
        });
        Assert.True(r.DeclaredPdfXIdentifier, $"conformant={r.IsConformant} blockers=[{string.Join(" | ", r.Blockers ?? new System.Collections.Generic.List<string>())}]");
        return ParseXmp(output.ToArray());
    }

    /// <summary>
    /// The <c>x:xmpmeta</c> element out of the file, parsed. <see cref="XDocument.Parse"/> throwing
    /// IS the assertion for the escape: an unescaped angle bracket in the title makes the packet
    /// unparseable, and that is what every consumer of the file would hit.
    /// </summary>
    private static XDocument ParseXmp(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int open = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        Assert.True(open >= 0, "no XMP packet was written");
        int close = text.IndexOf("</x:xmpmeta>", open, StringComparison.Ordinal);
        Assert.True(close >= 0, "the XMP packet has no closing tag");

        return XDocument.Parse(text.Substring(open, close - open + "</x:xmpmeta>".Length));
    }

    /// <summary>
    /// CMYK-only painting, no font, and the page carries a /TrimBox — the shape PdfXConverter can
    /// actually take to X-1a. It matters here because the converter WITHHOLDS the identifier (and
    /// therefore writes no XMP at all) when the input cannot reach conformance, so a fixture that
    /// merely "is a PDF" makes every assertion below vacuous. The first version of this file used a
    /// text-showing page with a non-embedded font and produced no packet to parse.
    /// </summary>
    // CMYK: PDF/X-1a (the other converter sharing this fixture) forbids RGB outright. The PDF/A
    // side is kept consistent by supplying a CMYK profile at its own call site instead —
    // this fixture's real interest is XMP escaping, not colour, and the two standards' colour
    // rules cannot both be satisfied by one choice of paint operator.
    private const string PageContent = "0 0 0 1 k 10 10 100 100 re f";

    private static byte[] PlainPdf()
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /TrimBox [0 0 200 200] " +
                "/Contents 4 0 R /Resources << >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        };

        var acc = new List<byte>();
        var off = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        Emit("%PDF-1.4\n");
        for (int i = 0; i < objs.Count; i++)
        {
            off.Add(acc.Count);
            Emit($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = acc.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in off) sb.Append($"{o:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        Emit(sb.ToString());
        return acc.ToArray();
    }
}
