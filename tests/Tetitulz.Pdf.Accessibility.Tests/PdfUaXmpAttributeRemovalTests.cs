// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Core.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The XMP writer removes a property's attribute shorthand in one walk of the packet: it searched from the
/// root again after each removal, so a packet carrying <c>dc:title</c> on 120 000 elements took 134 s to
/// merge a title into (07's DoS hunt, F10).
/// </summary>
public sealed class PdfUaXmpAttributeRemovalTests
{
    private const string Dc = "http://purl.org/dc/elements/1.1/";

    private static string Packet(int elements)
    {
        var x = new StringBuilder("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:dc=\"" + Dc + "\">");
        for (int i = 0; i < elements; i++) x.Append("<rdf:Description dc:title=\"t").Append(i).Append("\"/>");
        return x.Append("</rdf:RDF></x:xmpmeta>").ToString();
    }

    /// <summary>
    /// Every shorthand is removed and the title written as its element, and the attribute searches visit
    /// a few nodes per element — one walk per search, not one per removal (2 000 elements visited about
    /// two million nodes before).
    /// </summary>
    [Fact]
    public void ManyShorthandTitles_AreRemovedInOneWalk()
    {
        XmpPacketDom.AttributeSearchNodesVisited = 0;
        string merged = PdfUaXmpWriter.BuildPdfUaXmpPacket(Packet(2000), 1, "Report");

        Assert.Equal(0, Regex.Matches(merged, "dc:title=").Count);
        Assert.Contains(">Report<", merged, System.StringComparison.Ordinal);
        Assert.InRange(XmpPacketDom.AttributeSearchNodesVisited, 2000, 20 * 2000);
    }

    /// <summary>All the attributes, in the order the first-match search meets them, and that one first.</summary>
    [Fact]
    public void FindAttributes_ListsEveryMatchInDocumentOrder()
    {
        var doc = new XmlDocument();
        doc.LoadXml(Packet(3));

        var all = XmpPacketDom.FindAttributes(doc.DocumentElement, Dc, "title");

        Assert.Equal(new[] { "t0", "t1", "t2" }, all.Select(a => a.Value).ToArray());
        Assert.Same(all[0], XmpPacketDom.FindAttribute(doc.DocumentElement, Dc, "title"));
        Assert.Empty(XmpPacketDom.FindAttributes(doc.DocumentElement, Dc, "creator"));
    }

    /// <summary>The first-match search stops at its match: of a hundred elements it visits the first three nodes.</summary>
    [Fact]
    public void FindAttribute_StopsAtTheFirstMatch()
    {
        var doc = new XmlDocument();
        doc.LoadXml(Packet(100));

        XmpPacketDom.AttributeSearchNodesVisited = 0;
        Assert.Equal("t0", XmpPacketDom.FindAttribute(doc.DocumentElement, Dc, "title").Value);
        Assert.Equal(3, XmpPacketDom.AttributeSearchNodesVisited);

        XmpPacketDom.AttributeSearchNodesVisited = 0;
        Assert.Equal(100, XmpPacketDom.FindAttributes(doc.DocumentElement, Dc, "title").Count);
        Assert.Equal(102, XmpPacketDom.AttributeSearchNodesVisited);
    }

    /// <summary>An attribute below the search's depth cap is found by neither search.</summary>
    [Fact]
    public void FindAttributes_KeepsTheSearchDepthCap()
    {
        var x = new StringBuilder("<r xmlns:dc=\"" + Dc + "\">");
        for (int i = 0; i < XmpPacketDom.MaxSearchDepth + 2; i++) x.Append("<e>");
        x.Append("<e dc:title=\"deep\"/>");
        for (int i = 0; i < XmpPacketDom.MaxSearchDepth + 2; i++) x.Append("</e>");
        x.Append("<e dc:title=\"shallow\"/></r>");
        var doc = new XmlDocument();
        doc.LoadXml(x.ToString());

        Assert.Equal(new[] { "shallow" }, XmpPacketDom.FindAttributes(doc.DocumentElement, Dc, "title").Select(a => a.Value).ToArray());
        Assert.Equal("shallow", XmpPacketDom.FindAttribute(doc.DocumentElement, Dc, "title").Value);
    }
}
