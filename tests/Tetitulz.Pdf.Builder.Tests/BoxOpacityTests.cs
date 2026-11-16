// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.Opacity"/> (batch 106): a box of blocks below full opacity draws each page's
/// fragment — its fill, border and its blocks — into a transparency group the page paints once at the
/// box's opacity, as a boxed paragraph has since batch 90. Before batch 106 a box had no opacity: its
/// fill was drawn opaque and its blocks each carried whatever alpha the producer gave them.
/// </summary>
public class BoxOpacityTests
{
    private static readonly Regex GsOp = new(@"/(GS\d+)\s+gs", RegexOptions.Compiled);
    private static readonly Regex DoOp = new(@"/Fm\d+ Do", RegexOptions.Compiled);

    /// <summary>The page sets one state, fill and stroke alpha both 0.5, and paints the form once; inside it the fill and the text carry no state.</summary>
    [Fact]
    public void ATranslucentBox_IsATransparencyGroup_PaintedOnceAtItsAlpha()
    {
        byte[] pdf = Pdf(BlueBox(0.5), Para("Inside"), new BoxEnd());
        string content = PdfContentProbe.DecodeStreams(pdf);

        string gs = Assert.Single(GsOp.Matches(content).Cast<Match>()).Groups[1].Value;
        Assert.Equal("0.5", AlphaOf(pdf, gs, "ca"));
        Assert.Equal("0.5", AlphaOf(pdf, gs, "CA"));
        Assert.Single(DoOp.Matches(content).Cast<Match>());
        Assert.True(content.IndexOf(" gs", StringComparison.Ordinal) < content.IndexOf(" Do", StringComparison.Ordinal), "the state is not set before the group is painted");
        Assert.Matches(@"q\s+0 0 1 rg\s+[\d. -]+re\s+f\s+Q", content);
        Assert.DoesNotMatch(@"BT[\s\S]*?/GS\d+ gs[\s\S]*?\(Inside\) Tj", content);
        Assert.Contains("(Inside) Tj", content, StringComparison.Ordinal);
    }

    /// <summary>The control: an opaque box mints no form and sets no state.</summary>
    [Fact]
    public void AnOpaqueBox_MintsNoGroup()
    {
        string content = PdfContentProbe.DecodeStreams(Pdf(BlueBox(1), Para("Inside"), new BoxEnd()));

        Assert.DoesNotContain(" Do", content, StringComparison.Ordinal);
        Assert.Empty(GsOp.Matches(content));
        Assert.Matches(@"0 0 1 rg\s+[\d. -]+re\s+f", content);
    }

    /// <summary>A block inside the box keeps the opacity it carries: a 0.5 paragraph in a 0.5 box is a second alpha of 0.5 inside the form, not nothing and not 0.25.</summary>
    [Fact]
    public void ABlockInsideTheBox_KeepsItsOwnOpacity()
    {
        var dim = Para("Dimmer");
        dim.Opacity = 0.5;
        byte[] pdf = Pdf(BlueBox(0.5), Para("Plain"), dim, new BoxEnd());
        string content = PdfContentProbe.DecodeStreams(pdf);

        // The page's state before the Do, and the paragraph's own before its text inside the form
        // (the reset to 1 after it is a third); the plain paragraph sets none.
        var pageState = Regex.Match(content, @"/(GS\d+) gs\s+/Fm\d+ Do");
        var dimmerState = Regex.Match(content, @"/(GS\d+) gs[^(]*\(Dimmer\) Tj");
        Assert.True(pageState.Success, "the page does not paint the form at a state");
        Assert.True(dimmerState.Success, "the dimmer paragraph sets no state of its own inside the form");
        Assert.Equal("0.5", AlphaOf(pdf, pageState.Groups[1].Value, "ca"));
        Assert.Equal("0.5", AlphaOf(pdf, dimmerState.Groups[1].Value, "ca"));
        Assert.DoesNotMatch(@"/GS\d+ gs[^(]*\(Plain\) Tj", content);
    }

    /// <summary>A box broken across pages is a group per page: every page paints one form, and every page's <c>q</c> is closed.</summary>
    [Fact]
    public void ASlicedTranslucentBox_IsAGroupPerPage()
    {
        var blocks = new List<IDocElement> { BlueBox(0.5) };
        for (int i = 0; i < 120; i++) blocks.Add(Para("Line " + i, spacing: 2));
        blocks.Add(new BoxEnd());
        byte[] pdf = Pdf(blocks.ToArray());

        using var read = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(read.Pages.Count >= 3, "the box was not sliced over three pages");
        foreach (var page in read.Pages)
        {
            string content = Encoding.Latin1.GetString(page.GetContentStreamData());
            Assert.Single(DoOp.Matches(content).Cast<Match>());
            Assert.Single(GsOp.Matches(content).Cast<Match>());
            Assert.Equal(Regex.Matches(content, @"(?m)^q$").Count, Regex.Matches(content, @"(?m)^Q$").Count);
        }
    }

    /// <summary>A translucent box inside a table cell is a group there too.</summary>
    [Fact]
    public void ATranslucentBoxInACell_IsAGroup()
    {
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(BlueBox(0.5));
        cell.Blocks.Add(Para("Inside"));
        cell.Blocks.Add(new BoxEnd());
        row.Cells.Add(cell);
        table.Rows.Add(row);
        string content = PdfContentProbe.DecodeStreams(Pdf(Para("Before"), table));

        Assert.Single(DoOp.Matches(content).Cast<Match>());
        Assert.Matches(@"q\s+0 0 1 rg\s+[\d. -]+re\s+f\s+Q", content);
        Assert.True(content.IndexOf("(Before)", StringComparison.Ordinal) < content.IndexOf(" Do", StringComparison.Ordinal), "the paragraph before the table is not before the group");
    }

    /// <summary>
    /// In a tagged document the group's <c>Do</c> is one <c>Div</c> sequence — a form invoked inside
    /// a marked sequence belongs to it (§14.7.4.4) — nothing inside the form is marked, and the
    /// blocks inside make no structure elements of their own (an empty <c>P</c> would be one).
    /// </summary>
    [Fact]
    public void ATaggedTranslucentBox_MarksTheDoAsADiv_AndMakesNoElementInside()
    {
        var doc = Document(BlueBox(0.5), Para("One"), Para("Two"), new BoxEnd());
        doc.Tagged = true;
        byte[] pdf = doc.ToPdf();

        using var read = PdfDocument.Open(new MemoryStream(pdf));
        string page = Encoding.Latin1.GetString(read.Pages[0].GetContentStreamData());
        var xobjects = Assert.IsType<PdfDictionary>(read.ResolveObject(read.Pages[0].Resources[PdfName.XObject]));
        var form = Assert.IsType<PdfStream>(read.ResolveObject(xobjects[new PdfName("Fm1")]));
        string inner = Encoding.Latin1.GetString(form.GetDecodedData());
        string raw = Encoding.Latin1.GetString(pdf);

        Assert.Matches(@"/Div <</MCID \d+>> BDC\s+q\s+/GS\d+ gs\s+/Fm1 Do\s+Q\s+EMC", page);
        Assert.DoesNotContain("BDC", inner, StringComparison.Ordinal);
        Assert.Contains("(One) Tj", inner, StringComparison.Ordinal);
        Assert.Contains("/S /Div", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/S /P", raw, StringComparison.Ordinal);
    }

    /// <summary>A translucent box with a transform writes the matrix inside its group, round the fragment's paint, not on the page.</summary>
    [Fact]
    public void ATranslucentTransformedBox_TransformsInsideTheGroup()
    {
        var box = BlueBox(0.5);
        box.Transform = new double[] { 1, 0, 0, 1, 20, -30 };
        byte[] pdf = Pdf(box, Para("Inside"), new BoxEnd());

        using var read = PdfDocument.Open(new MemoryStream(pdf));
        string page = Encoding.Latin1.GetString(read.Pages[0].GetContentStreamData());
        var xobjects = Assert.IsType<PdfDictionary>(read.ResolveObject(read.Pages[0].Resources[PdfName.XObject]));
        var form = Assert.IsType<PdfStream>(read.ResolveObject(xobjects[new PdfName("Fm1")]));
        string inner = Encoding.Latin1.GetString(form.GetDecodedData());

        Assert.DoesNotContain(" cm", page, StringComparison.Ordinal);
        Assert.Matches(@"1 0 0 1 20 -30 cm[\s\S]*0 0 1 rg[\s\S]*\(Inside\) Tj", inner);
    }

    /// <summary>A translucent boxed paragraph inside a translucent box is a group inside the group: two forms, the inner one painted inside the outer.</summary>
    [Fact]
    public void ATranslucentParagraphInsideATranslucentBox_IsANestedGroup()
    {
        var para = Para("Faded");
        para.Shading = "#FF0000";
        para.Opacity = 0.5;
        byte[] pdf = Pdf(BlueBox(0.5), para, new BoxEnd());

        using var read = PdfDocument.Open(new MemoryStream(pdf));
        string page = Encoding.Latin1.GetString(read.Pages[0].GetContentStreamData());
        var xobjects = Assert.IsType<PdfDictionary>(read.ResolveObject(read.Pages[0].Resources[PdfName.XObject]));
        Assert.Single(DoOp.Matches(page).Cast<Match>());
        var outer = Assert.IsType<PdfStream>(read.ResolveObject(xobjects[new PdfName("Fm1")]));
        string outerContent = Encoding.Latin1.GetString(outer.GetDecodedData());
        Assert.Single(DoOp.Matches(outerContent).Cast<Match>());
        Assert.Contains("0 0 1 rg", outerContent, StringComparison.Ordinal);
        var outerResources = Assert.IsType<PdfDictionary>(read.ResolveObject(outer[PdfName.Resources]));
        var innerForms = Assert.IsType<PdfDictionary>(read.ResolveObject(outerResources[PdfName.XObject]));
        var inner = Assert.IsType<PdfStream>(read.ResolveObject(innerForms.Single().Value));
        string innerContent = Encoding.Latin1.GetString(inner.GetDecodedData());
        Assert.Contains("1 0 0 rg", innerContent, StringComparison.Ordinal);
        Assert.Contains("(Faded) Tj", innerContent, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private static BoxStart BlueBox(double opacity) => new() { Shading = "#0000FF", Opacity = opacity };

    private static Paragraph Para(string text, double spacing = 0)
    {
        var p = new Paragraph { SpacingBefore = spacing, SpacingAfter = spacing };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static Document Document(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc;
    }

    private static byte[] Pdf(params IDocElement[] blocks) => Document(blocks).ToPdf();

    /// <summary>The <c>/ca</c> or <c>/CA</c> value of the ExtGState resource named <paramref name="gsName"/>.</summary>
    private static string AlphaOf(byte[] pdf, string gsName, string key)
    {
        string raw = PdfContentProbe.Raw(pdf);
        var refMatch = Regex.Match(raw, @"/" + Regex.Escape(gsName) + @"\s+(\d+)\s+0\s+R");
        Assert.True(refMatch.Success, $"no /{gsName} entry in any /ExtGState resource dictionary");
        var objMatch = Regex.Match(raw, @"(?<![\d])" + refMatch.Groups[1].Value + @"\s+0\s+obj\s*<<(?<body>[^>]*)>>");
        Assert.True(objMatch.Success, $"object {refMatch.Groups[1].Value} (the /{gsName} ExtGState) not found");
        var alpha = Regex.Match(objMatch.Groups["body"].Value, @"/" + key + @"\s+([\d.]+)");
        Assert.True(alpha.Success, $"/{gsName} has no /{key}: <<{objMatch.Groups["body"].Value}>>");
        return alpha.Groups[1].Value;
    }
}
