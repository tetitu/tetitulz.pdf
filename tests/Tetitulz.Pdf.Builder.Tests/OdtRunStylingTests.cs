// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Run styling in the ODT exporter.
///
/// <para><b>What it guards against.</b> A <see cref="TextRun"/> written as bare text would drop
/// bold, italic, underline, strike, colour, highlight, size, family and super/subscript — unlike the
/// images and background images this exporter drops, <b>without a diagnostic</b> — so an ODT export
/// would look like it had worked and have quietly lost every distinction the document made.
/// </para>
///
/// <para><b>Why a two-pass writer.</b> ODF has no inline style attribute: a
/// <c>&lt;text:span&gt;</c> can only name a style, and automatic styles are declared in their own
/// element <i>before</i> <c>&lt;office:body&gt;</c>. A single-pass writer has nowhere to put a
/// style at the moment it finds it needs one.</para>
///
/// <para>These assert on the parsed XML rather than on substrings — a style whose declaration is
/// present but whose span references a different name produces perfectly plausible-looking markup
/// and no formatting at all.</para>
/// </summary>
public class OdtRunStylingTests
{
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo =
        "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    /// <summary>
    /// The claim, end to end: a bold run is wrapped in a span whose named style really declares
    /// bold. Following the name from the span to the declaration is the point — asserting that
    /// <c>fo:font-weight</c> appears somewhere in the file would pass for a style nothing
    /// references.
    /// </summary>
    [Fact]
    public void ABoldRun_IsWrappedInASpanWhoseStyleDeclaresBold()
    {
        XDocument content = ContentOf(Doc(new TextRun { Text = "loud", IsBold = true }));

        var span = content.Descendants(Text + "span").Single();
        Assert.Equal("loud", span.Value);

        var declared = StyleNamed(content, (string)span.Attribute(Text + "style-name"));
        Assert.Equal("bold", (string)declared.Attribute(Fo + "font-weight"));
    }

    /// <summary>
    /// Each property reaches its own ODF attribute. Written as a theory so a mapping that happened
    /// to emit one attribute for everything could not pass.
    /// </summary>
    [Theory]
    [InlineData("italic")]
    [InlineData("underline")]
    [InlineData("strike")]
    [InlineData("colour")]
    [InlineData("size")]
    [InlineData("superscript")]
    public void EachRunProperty_ReachesItsOwnOdfAttribute(string which)
    {
        var run = new TextRun { Text = "x" };
        switch (which)
        {
            case "italic": run.IsItalic = true; break;
            case "underline": run.IsUnderline = true; break;
            case "strike": run.IsStrikethrough = true; break;
            case "colour": run.Color = "#ff0000"; break;
            case "size": run.FontSize = 24; break;
            case "superscript": run.VerticalAlign = VerticalAlign.Superscript; break;
        }

        XDocument content = ContentOf(Doc(run));
        var span = content.Descendants(Text + "span").Single();
        var declared = StyleNamed(content, (string)span.Attribute(Text + "style-name"));

        string expected = which switch
        {
            "italic" => (string)declared.Attribute(Fo + "font-style"),
            "underline" => (string)declared.Attribute(Style + "text-underline-style"),
            "strike" => (string)declared.Attribute(Style + "text-line-through-style"),
            "colour" => (string)declared.Attribute(Fo + "color"),
            "size" => (string)declared.Attribute(Fo + "font-size"),
            _ => (string)declared.Attribute(Style + "text-position"),
        };

        Assert.False(string.IsNullOrEmpty(expected),
            $"'{which}' produced no ODF attribute, so it is still being dropped");
    }

    /// <summary>
    /// An unstyled run writes bare text with no span at all. Wrapping every word would quadruple a
    /// plain document for nothing, and it is also what keeps this change from altering the output
    /// of documents that never asked for formatting.
    /// </summary>
    [Fact]
    public void APlainRun_IsNotWrappedAtAll()
    {
        XDocument content = ContentOf(Doc(new TextRun { Text = "plain" }));

        Assert.Empty(content.Descendants(Text + "span"));
        Assert.Contains("plain", content.ToString());
    }

    /// <summary>
    /// Runs that look the same share one declaration. A document bolding forty words declares one
    /// style, not forty — the deduplication key is the formatting itself, so two identical runs
    /// match however far apart they are.
    /// </summary>
    [Fact]
    public void IdenticalFormattings_ShareOneDeclaration()
    {
        var doc = new Document();
        var section = new Section();
        for (int i = 0; i < 5; i++)
        {
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = "w" + i, IsBold = true });
            section.Children.Add(p);
        }
        doc.Sections.Add(section);

        XDocument content = ContentOf(doc);

        Assert.Equal(5, content.Descendants(Text + "span").Count());
        Assert.Single(content.Descendants(Style + "style")
                             .Where(e => (string)e.Attribute(Style + "family") == "text"));
    }

    /// <summary>
    /// Different formattings get different styles — the control that stops the deduplication case
    /// above from passing for an implementation that declares exactly one style ever.
    /// </summary>
    [Fact]
    public void DifferentFormattings_GetDifferentDeclarations()
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "a", IsBold = true });
        p.Runs.Add(new TextRun { Text = "b", IsItalic = true });
        section.Children.Add(p);
        doc.Sections.Add(section);

        XDocument content = ContentOf(doc);

        var names = content.Descendants(Text + "span")
                           .Select(s => (string)s.Attribute(Text + "style-name"))
                           .Distinct()
                           .ToList();
        Assert.Equal(2, names.Count);
    }

    /// <summary>
    /// A colour ODF cannot parse is refused rather than passed through. It matters more than it
    /// looks: an invalid <c>fo:color</c> invalidates the whole style, and LibreOffice drops it — so
    /// one bad colour would silently take the bold off every run sharing that style.
    /// </summary>
    [Fact]
    public void AnUnparseableColour_IsRefusedRatherThanWrittenIntoTheStyle()
    {
        XDocument content = ContentOf(Doc(
            new TextRun { Text = "x", IsBold = true, Color = "rebeccapurple" }));

        var span = content.Descendants(Text + "span").Single();
        var declared = StyleNamed(content, (string)span.Attribute(Text + "style-name"));

        Assert.Null(declared.Attribute(Fo + "color"));
        // The rest of the run's formatting survives, which is the whole reason for refusing.
        Assert.Equal("bold", (string)declared.Attribute(Fo + "font-weight"));
    }

    /// <summary>Runs inside table cells are styled too — both cell shapes the model allows.</summary>
    [Fact]
    public void RunsInsideTableCells_AreStyledAsWell()
    {
        var doc = new Document();
        var section = new Section();
        var table = new Table();
        var row = new TableRow();

        var viaParagraphs = new TableCell();
        var cp = new Paragraph();
        cp.Runs.Add(new TextRun { Text = "head", IsBold = true });
        viaParagraphs.Paragraphs.Add(cp);

        var viaBlocks = new TableCell();
        var bp = new Paragraph();
        bp.Runs.Add(new TextRun { Text = "body", IsItalic = true });
        viaBlocks.Blocks.Add(bp);

        row.Cells.Add(viaParagraphs);
        row.Cells.Add(viaBlocks);
        table.Rows.Add(row);
        section.Children.Add(table);
        doc.Sections.Add(section);

        XDocument content = ContentOf(doc);

        var spans = content.Descendants(Text + "span").ToList();
        Assert.Equal(2, spans.Count);
        foreach (var span in spans)
            Assert.NotNull(StyleNamed(content, (string)span.Attribute(Text + "style-name")));
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// A styled run inside a native list item — and inside a sublist item — is wrapped in a span
    /// whose style is really declared (Builder-1:5). The style pre-pass walked paragraphs, loose
    /// list items and tables but not a <see cref="ListBlock"/>, so a run in a <c>&lt;text:list&gt;</c>
    /// reached the body write with no declaration: its span named a style the file did not contain
    /// and the formatting was silently lost. Following span → declaration is the point — the span is
    /// written either way, so a check for its presence would have passed on the broken output.
    /// </summary>
    [Fact]
    public void AStyledRunInAListItemAndItsSublist_ReferencesADeclaredStyle()
    {
        var inner = new ListItem();
        inner.Runs.Add(new TextRun { Text = "deep", IsItalic = true });
        var outer = new ListItem();
        outer.Runs.Add(new TextRun { Text = "loud", IsBold = true });
        outer.Sublist = new ListBlock();
        outer.Sublist.Items.Add(inner);
        var list = new ListBlock();
        list.Items.Add(outer);

        var doc = new Document();
        var section = new Section();
        section.Children.Add(list);
        doc.Sections.Add(section);

        XDocument content = ContentOf(doc);

        var boldSpan = content.Descendants(Text + "span").Single(s => s.Value == "loud");
        var boldStyle = StyleNamed(content, (string)boldSpan.Attribute(Text + "style-name"));
        Assert.Equal("bold", (string)boldStyle.Attribute(Fo + "font-weight"));

        var italicSpan = content.Descendants(Text + "span").Single(s => s.Value == "deep");
        var italicStyle = StyleNamed(content, (string)italicSpan.Attribute(Text + "style-name"));
        Assert.Equal("italic", (string)italicStyle.Attribute(Fo + "font-style"));
    }

    private static Document Doc(TextRun run)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(run);
        section.Children.Add(p);
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>The style declaration with this name — asserted to exist, since a span naming a
    /// style nothing declares produces no formatting while looking correct.</summary>
    private static XElement StyleNamed(XDocument content, string name)
    {
        Assert.False(string.IsNullOrEmpty(name), "the span carried no style name");
        var declared = content.Descendants(Style + "style")
                              .FirstOrDefault(e => (string)e.Attribute(Style + "name") == name);
        Assert.True(declared != null, $"no style named '{name}' was declared");
        return declared!.Element(Style + "text-properties");
    }

    private static XDocument ContentOf(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsOdt(ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("content.xml");
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return XDocument.Parse(reader.ReadToEnd());
    }
}
