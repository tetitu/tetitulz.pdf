// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The ODT table of contents (Wave 22, 1:3).
///
/// <para><b>What was wrong.</b> <c>WriteToc</c> wrote a contents entry as
/// <c>&lt;text:a xlink:type="simple" xlink:href="#toc0"&gt;</c>, but <c>content.xml</c>'s root
/// declared no <c>xlink</c> namespace — so the whole part was not well-formed XML and
/// <c>XDocument.Load</c> (and every namespace-aware ODF consumer) rejected it with "xlink is an
/// undeclared prefix". A document with a TOC did not open at all. Even had the prefix been declared,
/// <c>#toc0</c> named nothing: no element carried the bookmark, and the title went into
/// <c>&lt;text:index-title-template&gt;</c> (how to regenerate), which a non-regenerating reader
/// does not show.
/// </para>
///
/// <para><b>These parse the XML and follow each link to its target</b> — a bookmark written under a
/// different name, or a title in the template instead of the body, both produce plausible markup and
/// a broken contents list, which a substring check would pass (2026-10-23).</para>
/// </summary>
public class OdtTableOfContentsTests
{
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    /// <summary>A section of two headings followed by a contents block titled "Contents".</summary>
    private static Document Doc()
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(Heading("Chapter One"));
        section.Children.Add(Heading("Chapter Two"));
        section.Children.Add(new TocBlock { Title = "Contents", MaxLevel = 3 });
        doc.Sections.Add(section);
        return doc;
    }

    private static Paragraph Heading(string text)
    {
        var p = new Paragraph { Role = Tetitulz.Pdf.Core.Constants.PdfStructureTypes.H1 };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    /// <summary>
    /// Reads <c>content.xml</c> as parsed XML. The parse itself is the first half of the fix: before
    /// it, the undeclared <c>xlink</c> prefix made this throw.
    /// </summary>
    private static XDocument ContentOf(Document doc)
    {
        byte[] odt = doc.ToOdt();
        using var zip = new ZipArchive(new MemoryStream(odt, writable: false), ZipArchiveMode.Read);
        ZipArchiveEntry? entry = zip.GetEntry("content.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return XDocument.Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// The content is well-formed XML and the <c>xlink</c> prefix the TOC uses is declared on the
    /// root. A missing declaration throws in <see cref="ContentOf"/> before this runs; the explicit
    /// assertion names the specific fix so a future reader knows what the parse is guarding.
    /// </summary>
    [Fact]
    public void ContentWithAToc_IsWellFormed_AndDeclaresTheXlinkPrefix()
    {
        XDocument content = ContentOf(Doc());

        XElement root = content.Root!;
        bool declaresXlink = root.Attributes()
            .Any(a => a.IsNamespaceDeclaration && (string)a == Xlink.NamespaceName);
        Assert.True(declaresXlink, "content.xml did not declare the xlink namespace the TOC entries use");
    }

    /// <summary>
    /// End to end: every TOC entry's <c>xlink:href="#X"</c> points at a <c>&lt;text:bookmark&gt;</c>
    /// that really carries the name <c>X</c>. A bookmark emitted under a different name — or none at
    /// all, the original defect — leaves the fragment dangling, which this catches and a check for
    /// "contains a bookmark" would not.
    /// </summary>
    [Fact]
    public void EveryTocEntryLink_ResolvesToABookmarkOfThatName()
    {
        XDocument content = ContentOf(Doc());

        var bookmarkNames = content.Descendants(Text + "bookmark")
            .Select(b => (string)b.Attribute(Text + "name"))
            .ToHashSet();

        var links = content.Descendants(Text + "a")
            .Select(a => (string)a.Attribute(Xlink + "href"))
            .Where(h => h != null && h.StartsWith("#"))
            .ToList();

        Assert.Equal(2, links.Count); // one per heading — the control that the entries exist at all
        foreach (string href in links)
        {
            string target = href.Substring(1);
            Assert.Contains(target, bookmarkNames);
        }
    }

    /// <summary>
    /// An attribute value is escaped (Builder-1:6). A heading anchor the caller set with a
    /// double-quote in it reaches both <c>text:name</c> and <c>xlink:href</c>; <c>Xml()</c> escapes
    /// the quote, so the part stays well-formed and the name round-trips. Had the escaper left the
    /// quote raw — the original defect, where <c>Xml()</c> escaped only <c>&amp; &lt; &gt;</c> — the
    /// attribute <c>text:name="a"b"</c> would make <see cref="ContentOf"/> throw.
    /// </summary>
    [Fact]
    public void AnAnchorNameWithAQuote_IsEscapedInItsAttribute_AndRoundTrips()
    {
        var doc = new Document();
        var section = new Section();
        var heading = Heading("Quoted");
        heading.AnchorName = "a\"b";
        section.Children.Add(heading);
        section.Children.Add(new TocBlock { Title = "Contents", MaxLevel = 3 });
        doc.Sections.Add(section);

        XDocument content = ContentOf(doc); // throws if the quote broke the attribute

        var bookmark = content.Descendants(Text + "bookmark").Single();
        Assert.Equal("a\"b", (string)bookmark.Attribute(Text + "name"));

        var link = content.Descendants(Text + "a").Single();
        Assert.Equal("#a\"b", (string)link.Attribute(Xlink + "href"));
    }

    /// <summary>
    /// The title is the shown <c>&lt;text:index-title&gt;</c> inside the body, not only the
    /// <c>&lt;text:index-title-template&gt;</c> a non-regenerating reader ignores.
    /// </summary>
    [Fact]
    public void TheTocTitle_IsShownInTheIndexBody()
    {
        XDocument content = ContentOf(Doc());

        XElement body = content.Descendants(Text + "index-body").Single();
        XElement? title = body.Elements(Text + "index-title").FirstOrDefault();
        Assert.NotNull(title);
        Assert.Equal("Contents", title!.Value.Trim());
    }
}
