// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The list <b>container</b>: what a <see cref="ListBlock"/> can express that a run of loose
/// <see cref="ListItem"/>s could not.
/// </summary>
/// <remarks>
/// <para><b>An ordinal needs somewhere to come from.</b> A loose item carries
/// <see cref="ListItem.IsNumbered"/> and an <see cref="ListItem.Level"/>, and neither says which
/// items belong together — so a marker derived from the <i>level</i> would render three sibling
/// items all as "1. ", unless the caller resolved every marker itself and passed it in
/// <see cref="ListItem.Marker"/>.</para>
///
/// <para><b>Three formats get structure, not just numbers.</b> HTML writes a list, not a
/// <c>&lt;p&gt;</c> with the marker as literal text, which looks like a list and is not one — a
/// screen reader announces nothing and a copy picks the numbers up as characters; ODT a list, not
/// a bare <c>&lt;text:p&gt;</c>; tagged PDF <c>/L</c>, not <c>/P</c>.</para>
///
/// <para><b>Loose items are not deprecated and still render.</b> A list of one paragraph is not a
/// list; wrapping every loose item in an <c>/L</c> of one <c>/LI</c> would be conformant and
/// useless. Loose items keep their rendering, which is also what keeps three producers that build
/// the model directly working; a run of two or more consecutive loose items is tagged as the list
/// it is.</para>
/// </remarks>
public class ListContainerTests
{
    // ================================================================ numbering

    /// <summary>
    /// Items are numbered by position, which is what a loose item could not be.
    /// </summary>
    /// <remarks>
    /// The assertion is <c>1. 2. 3.</c> against a fixture of three items at the same level — the
    /// exact case a number taken from the level plus one would render as "1." three times.
    /// </remarks>
    [Fact]
    public void AnOrderedList_NumbersItsItemsByPosition()
    {
        ListBlock list = Ordered("alpha", "beta", "gamma");

        Assert.Equal(new[] { "1. ", "2. ", "3. " },
            Enumerable.Range(0, 3).Select(i => ModelRules.ListMarker(list, i)).ToArray());
    }

    /// <summary>An ordered list starts where it says it starts.</summary>
    [Fact]
    public void AnOrderedList_CountsFromItsStart()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Start = 7;

        Assert.Equal("7. ", ModelRules.ListMarker(list, 0));
        Assert.Equal("8. ", ModelRules.ListMarker(list, 1));
    }

    /// <summary>An explicit marker still wins over the derived one.</summary>
    /// <remarks>
    /// The model documents <see cref="ListItem.Marker"/> as overriding what is derived, and a
    /// producer that has already resolved <c>"1.2."</c> or <c>"א."</c> knows something a positional
    /// ordinal cannot: <c>HtmlToBuilderMapper</c> resolves CSS's <c>list-style-type</c>, and DOCX
    /// numbering can restart, skip and share a sequence across paragraphs.
    /// </remarks>
    [Fact]
    public void AnExplicitMarker_StillOverridesThePositionalOrdinal()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Items[1].Marker = "1.2. ";

        Assert.Equal("1. ", ModelRules.ListMarker(list, 0));
        Assert.Equal("1.2. ", ModelRules.ListMarker(list, 1));
    }

    /// <summary>Bullets cycle with depth, so a sublist is distinguishable from its parent.</summary>
    [Fact]
    public void BulletsCycleWithNestingDepth()
    {
        ListBlock list = Bulleted("outer");
        list.Items[0].Sublist = Bulleted("inner");
        list.Items[0].Sublist!.Items[0].Sublist = Bulleted("innermost");

        string[] markers = ModelRules.Flatten(list).Select(e => e.Marker).ToArray();

        Assert.Equal(3, markers.Length);
        Assert.Equal(3, markers.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Flattening fills each item's level in from the depth it was found at.</summary>
    /// <remarks>
    /// <b>The depth walked is authoritative, not what a producer set.</b> <see cref="ListItem.Level"/>
    /// is a public setter on a model three producers build directly, so an item can carry a level
    /// that disagrees with where it actually sits — and the indent has to follow the structure, or a
    /// sublist is drawn flush with its parent.
    /// </remarks>
    [Fact]
    public void FlatteningCorrectsAnItemsLevelToTheDepthItWasFoundAt()
    {
        ListBlock list = Bulleted("outer");
        list.Items[0].Sublist = Bulleted("inner");
        list.Items[0].Sublist!.Items[0].Level = 9;      // a producer's mistake

        Assert.Equal(new[] { 0, 1 }, ModelRules.Flatten(list).Select(e => e.Item.Level).ToArray());
    }

    /// <summary>A cyclic sublist is bounded rather than walked for ever.</summary>
    /// <remarks>
    /// The model is a public mutable graph, so an item can be made its own descendant. The
    /// assertion is the guard's own evidence — a bounded result — rather than a wall clock: a walk
    /// that returns at all has been bounded, and one that has not never reaches the assertion.
    /// </remarks>
    [Fact]
    public void ACyclicSublist_IsBoundedRatherThanWalkedForever()
    {
        ListBlock list = Bulleted("loop");
        list.Items[0].Sublist = list;

        List<(ListItem Item, string Marker)> flat = ModelRules.Flatten(list);

        Assert.InRange(flat.Count, 1, ModelRules.MaxListDepth + 1);
    }

    // ================================================================ HTML

    /// <summary>HTML emits a real <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c> with <c>&lt;li&gt;</c>.</summary>
    /// <remarks>
    /// Parsed as XML rather than matched as a substring, so the elements have to nest correctly
    /// rather than merely appear.
    /// </remarks>
    [Fact]
    public void Html_OrderedList_EmitsAnOlWithListItems()
    {
        XElement ol = ListXml(Doc(Ordered("alpha", "beta")));

        Assert.Equal("ol", ol.Name.LocalName);
        Assert.Equal(new[] { "alpha", "beta" },
            ol.Elements("li").Select(li => li.Value.Trim()).ToArray());
    }

    /// <summary>A bulleted list is a <c>&lt;ul&gt;</c>, and a sublist nests inside its item.</summary>
    [Fact]
    public void Html_NestedList_NestsInsideItsParentItem()
    {
        ListBlock list = Bulleted("outer");
        list.Items[0].Sublist = Bulleted("inner");

        XElement outer = ListXml(Doc(list));

        XElement outerItem = Assert.Single(outer.Elements("li"));
        XElement inner = Assert.Single(outerItem.Elements("ul"));

        Assert.Equal("inner", Assert.Single(inner.Elements("li")).Value.Trim());
    }

    /// <summary><c>start</c> reaches the <c>&lt;ol&gt;</c>.</summary>
    [Fact]
    public void Html_OrderedListWithAStart_WritesTheStartAttribute()
    {
        ListBlock list = Ordered("alpha");
        list.Start = 4;

        XElement ol = ListXml(Doc(list));

        Assert.Equal("4", ol.Attribute("start")?.Value);
    }

    /// <summary>
    /// An explicit numeric marker becomes <c>&lt;li value&gt;</c>; anything else stays as text with
    /// the automatic marker off.
    /// </summary>
    /// <remarks>
    /// <b>Both branches, because dropping either loses the producer's own numbering.</b> Renumbering
    /// an item that already said "5." changes the document; writing "א." as an <c>&lt;ol&gt;</c>
    /// ordinal loses it entirely.
    /// </remarks>
    [Fact]
    public void Html_ExplicitMarkers_SurviveAsValueOrAsText()
    {
        ListBlock list = Ordered("numbered", "lettered");
        list.Items[0].Marker = "5.";
        list.Items[1].Marker = "א.";

        XElement ol = ListXml(Doc(list));
        XElement[] items = ol.Elements("li").ToArray();

        Assert.Equal("5", items[0].Attribute("value")?.Value);
        Assert.DoesNotContain("5.", items[0].Value, StringComparison.Ordinal);

        Assert.Null(items[1].Attribute("value"));
        Assert.Contains("א.", items[1].Value, StringComparison.Ordinal);
        Assert.Contains("none", items[1].Attribute("style")?.Value ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ================================================================ ODT

    /// <summary>ODT emits a native <c>&lt;text:list&gt;</c> with real items.</summary>
    [Fact]
    public void Odt_List_EmitsATextListWithItems()
    {
        string xml = Odt(Doc(Ordered("alpha", "beta")));

        Assert.Contains("<text:list ", xml, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(xml, "<text:list-item>"));
        Assert.Contains("alpha", xml, StringComparison.Ordinal);
        Assert.Contains("2. ", xml, StringComparison.Ordinal);
    }

    // ================================================================ DOCX

    /// <summary>DOCX writes a native Word list, not paragraphs with the markers typed in.</summary>
    /// <remarks>
    /// <para><b>What "typed in" costs.</b> Prepending the marker as a literal text run and setting an
    /// indent per level produces a document which <i>looks</i> like a list and
    /// is a run of paragraphs beginning with characters: inserting an item renumbers nothing,
    /// deleting one leaves a gap, Word's list commands do not apply, and a copy picks the numbers
    /// up as text.</para>
    ///
    /// <para><b>The absence is the load-bearing assertion.</b> A <c>w:numPr</c> can be written
    /// while the literal marker stays, and the document would then show every number twice — which
    /// no assertion about the presence of numbering would catch.</para>
    /// </remarks>
    [Fact]
    public void Docx_List_IsANativeWordListRatherThanTypedMarkers()
    {
        ListBlock list = Ordered("alpha", "beta");

        XDocument xml = DocxXml(Doc(list));

        Assert.Equal(2, xml.Descendants(W + "numPr").Count());

        string text = string.Concat(xml.Descendants(W + "t").Select(t => t.Value));
        Assert.Equal("alphabeta", text);
    }

    /// <summary>The numbering definition the paragraphs point at is really in the package.</summary>
    /// <remarks>
    /// <c>w:numPr</c> naming a <c>w:numId</c> that no <c>numbering.xml</c> defines is worse than
    /// no numbering at all: Word reports the file as damaged. The part had never been written by
    /// this package, so its existence is the new thing and cannot be assumed.
    /// </remarks>
    [Fact]
    public void Docx_List_WritesTheNumberingPartItsParagraphsReference()
    {
        XDocument document = DocxXml(Doc(Ordered("alpha", "beta")));
        XDocument numbering = DocxNumberingXml(Doc(Ordered("alpha", "beta")));

        string numId = document.Descendants(W + "numId").First().Attribute(W + "val")!.Value;

        Assert.Contains(numbering.Descendants(W + "num"),
            n => n.Attribute(W + "numId")?.Value == numId);
        Assert.NotEmpty(numbering.Descendants(W + "abstractNum"));
    }

    /// <summary>A sublist item is written at its own <c>w:ilvl</c>.</summary>
    /// <remarks>
    /// The level is how Word indents and numbers a nested item, not an explicit <c>w:ind</c>: an
    /// indent set here would fight the definition's own, which is the more common way to get a list
    /// that looks wrong in Word.
    /// </remarks>
    [Fact]
    public void Docx_List_ASublistItemIsWrittenAtItsOwnLevel()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Items[1].Sublist = Ordered("nested");

        XDocument xml = DocxXml(Doc(list));

        Assert.Equal(
            new[] { "0", "0", "1" },
            xml.Descendants(W + "ilvl").Select(l => l.Attribute(W + "val")!.Value));
    }

    /// <summary>A bulleted list is written as a bullet definition, not a numbered one.</summary>
    [Fact]
    public void Docx_BulletList_UsesABulletNumberingFormat()
    {
        XDocument numbering = DocxNumberingXml(Doc(Bulleted("alpha", "beta")));

        Assert.Equal(
            "bullet",
            numbering.Descendants(W + "numFmt").First().Attribute(W + "val")!.Value);
    }

    /// <summary>An ordered list's definition starts where the list does.</summary>
    /// <remarks>
    /// The acceptance control for the format above, and the property that makes the numbering
    /// native rather than decorative: Word counts from the definition's start, so a list continued
    /// after an interruption keeps its sequence without anything being typed.
    /// </remarks>
    [Fact]
    public void Docx_OrderedList_StartsWhereTheListDoes()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Start = 7;

        XDocument numbering = DocxNumberingXml(Doc(list));
        XElement firstLevel = numbering.Descendants(W + "lvl")
            .First(l => l.Attribute(W + "ilvl")?.Value == "0");

        Assert.Equal("decimal", firstLevel.Element(W + "numFmt")!.Attribute(W + "val")!.Value);
        Assert.Equal("7", firstLevel.Element(W + "start")!.Attribute(W + "val")!.Value);
    }

    /// <summary>Two lists in one document get separate definitions.</summary>
    /// <remarks>
    /// <b>Sharing one would make the second continue the first's numbers</b> — the classic
    /// symptom of a single reused <c>w:numId</c>, and one that a document with a single list can
    /// never show. The definitions must differ as well as the instances: two
    /// <c>w:abstractNum</c> elements with the same id is a package Word repairs.
    /// </remarks>
    [Fact]
    public void Docx_TwoLists_GetSeparateNumberingDefinitions()
    {
        Document doc = Doc(Ordered("a"), Ordered("b"));

        string[] ids = DocxXml(doc).Descendants(W + "numId")
            .Select(n => n.Attribute(W + "val")!.Value).ToArray();

        Assert.Equal(2, ids.Length);
        Assert.Equal(2, ids.Distinct().Count());

        string[] abstractIds = DocxNumberingXml(doc).Descendants(W + "abstractNum")
            .Select(a => a.Attribute(W + "abstractNumId")!.Value).ToArray();

        Assert.Equal(abstractIds.Length, abstractIds.Distinct().Count());
    }

    /// <summary>Each level numbers itself, so a sublist does not repeat its parent's ordinal.</summary>
    /// <remarks>
    /// The level text is <c>%N.</c> where N is the level's own one-based index. Writing
    /// <c>%1.</c> at every depth makes every nested item show the top-level counter — a document
    /// numbered 1, 1, 1 — and no assertion about the first level would notice.
    /// </remarks>
    [Fact]
    public void Docx_EachOrderedLevelNumbersItself()
    {
        XDocument numbering = DocxNumberingXml(Doc(Ordered("alpha")));

        string[] texts = numbering.Descendants(W + "lvl")
            .OrderBy(l => int.Parse(l.Attribute(W + "ilvl")!.Value))
            .Take(3)
            .Select(l => l.Element(W + "lvlText")!.Attribute(W + "val")!.Value)
            .ToArray();

        Assert.Equal(new[] { "%1.", "%2.", "%3." }, texts);
    }

    /// <summary>The bullet cycles with depth, as it does in every other export of the model.</summary>
    /// <remarks>
    /// <c>ModelRules.BulletForDepth</c> is the one implementation, reached from here so the DOCX,
    /// PDF, HTML and ODT exports of one document agree at depth. A literal here would be the second
    /// copy of that decision, and it would disagree from the second level down — where nothing
    /// looks.
    /// </remarks>
    [Fact]
    public void Docx_TheBulletCyclesWithDepth()
    {
        XDocument numbering = DocxNumberingXml(Doc(Bulleted("alpha")));

        string[] bullets = numbering.Descendants(W + "lvl")
            .OrderBy(l => int.Parse(l.Attribute(W + "ilvl")!.Value))
            .Take(3)
            .Select(l => l.Element(W + "lvlText")!.Attribute(W + "val")!.Value)
            .ToArray();

        Assert.Equal(3, bullets.Distinct().Count());
        Assert.Equal(Tetitulz.Pdf.Builder.Model.ModelRules.BulletForDepth(0), bullets[0]);
        Assert.Equal(Tetitulz.Pdf.Builder.Model.ModelRules.BulletForDepth(1), bullets[1]);
    }

    /// <summary>
    /// A list whose items carry their own markers is still a native Word list, with each marker
    /// written as level text, and raises no report.
    /// </summary>
    /// <remarks>
    /// <para><b>The model records a resolved marker string, not the scheme behind it</b> — and a
    /// Word level text is a literal string too. An item marked <c>"א."</c> gets a numbering
    /// instance of its own whose level text IS <c>"א."</c> in bullet format, so Word draws the
    /// marker the model recorded and still treats the paragraph as a list item. A typed marker is the
    /// fallback only for what a level text cannot carry;
    /// <c>DocxLiteralMarkerTests</c> pins the definition's shape.</para>
    /// </remarks>
    [Fact]
    public void Docx_ListWithExplicitMarkers_WritesThemAsLevelTextAndKeepsTheList()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Items[0].Marker = "א. ";
        list.Items[1].Marker = "ב. ";

        XDocument xml = DocxXml(Doc(list));
        string text = string.Concat(xml.Descendants(W + "t").Select(t => t.Value));

        Assert.Equal(2, xml.Descendants(W + "numPr").Count());
        Assert.Equal("alphabeta", text);

        Assert.DoesNotContain("will not renumber", BuilderDiagnostics.LastError?.Detail ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>An ordinary list raises no such report.</summary>
    /// <remarks>
    /// The acceptance control for the report: one that fired on every list is one a caller learns
    /// to ignore, which is the rule <c>IgnoredParameterReporting.Deviates</c> states one area over
    /// — presence is not deviation.
    /// </remarks>
    [Fact]
    public void Docx_AnOrdinaryList_RaisesNoUnmaintainedReport()
    {
        DocxXml(Doc(Ordered("alpha", "beta")));

        Assert.DoesNotContain("will not renumber", BuilderDiagnostics.LastError?.Detail ?? "",
            StringComparison.Ordinal);
    }

    // ================================================================ tagged PDF

    /// <summary>
    /// Tagged PDF emits <c>/L</c> → <c>/LI</c> → <c>/Lbl</c> + <c>/LBody</c>, not <c>/P</c>.
    /// </summary>
    /// <remarks>
    /// <b>PDF/UA-1 §7.6 is the reason those four exist</b>, and this repository's own deep checker
    /// enforces the shape: every child of an <c>/L</c> must be an <c>/LI</c> or a <c>/Caption</c>,
    /// and every child of an <c>/LI</c> an <c>/Lbl</c> or an <c>/LBody</c>. A list rendered as a
    /// run of <c>/P</c> elements passes every one of those rules by having no list in it.
    /// </remarks>
    [Fact]
    public void Pdf_TaggedList_EmitsListStructureElements()
    {
        Document doc = Doc(Ordered("alpha", "beta"));
        doc.Tagged = true;

        HashSet<string> roles = StructureRoles(doc.ToPdf());

        // Exact role names, not substrings: "/S /L" is a prefix of "/S /LI" and of "/S /Lbl", so a
        // Contains for it is satisfied by the elements it is supposed to be the parent of — which
        // is how a mutation replacing the /L with a /P survived the first version of this test.
        Assert.Contains("L", roles);
        Assert.Contains("LI", roles);
        Assert.Contains("Lbl", roles);
        Assert.Contains("LBody", roles);
    }

    /// <summary>A loose list item is still a <c>/P</c>.</summary>
    /// <remarks>
    /// The over-correction guard. A list of one paragraph is not a list, and wrapping every loose
    /// item in an <c>/L</c> containing one <c>/LI</c> would be conformant, meaningless, and a
    /// change to every document three producers already build.
    /// </remarks>
    [Fact]
    public void Pdf_LooseListItem_IsStillAParagraph()
    {
        Document doc = Doc(new ListItem { Text = "loose" });
        doc.Tagged = true;

        HashSet<string> roles = StructureRoles(doc.ToPdf());

        Assert.DoesNotContain("L", roles);
        Assert.DoesNotContain("LI", roles);
        Assert.Contains("P", roles);
    }

    /// <summary>
    /// Two or more consecutive loose items are a list: one <c>/L</c>, an <c>/LI</c> per item
    /// whose paragraph is its <c>/LBody</c>, a deeper item in an <c>/L</c> inside the item before it. A
    /// paragraph ends the run, and a lone item after it is still a <c>/P</c> (the guard above).
    /// </summary>
    [Fact]
    public void Pdf_ConsecutiveLooseListItems_AreOneList()
    {
        Document doc = Doc(
            new ListItem { Text = "one" }, new ListItem { Text = "two", Level = 1 }, new ListItem { Text = "three" },
            new Paragraph { Runs = { new TextRun { Text = "between" } } },
            new ListItem { Text = "alone" });
        doc.Tagged = true;

        var edges = StructureEdges(doc.ToPdf());

        Assert.Equal(2, edges.Count(e => e.Role == "L"));
        Assert.Single(edges, e => e.Role == "L" && e.Parent == "LI");          // the nested one, in "one"'s item
        Assert.Equal(3, edges.Count(e => e.Role == "LI" && e.Parent == "L"));
        Assert.Equal(3, edges.Count(e => e.Role == "LBody" && e.Parent == "LI"));
        Assert.Equal(2, edges.Count(e => e.Role == "P"));                       // "between" and "alone"
        // "one" and "three" in the outer list, "two" in the list inside "one"'s item.
        Assert.Equal(2, edges.Count(e => e.Path.EndsWith("/L/LI", StringComparison.Ordinal) && !e.Path.Contains("/LI/L/", StringComparison.Ordinal)));
        Assert.Single(edges, e => e.Path.EndsWith("/L/LI/L/LI", StringComparison.Ordinal));
    }

    /// <summary>
    /// A list part of <see cref="ListBlock.NestingDepth"/> past 0 is tagged in the item last
    /// drawn one level up: "inner" and "inner2" — the second going on from the first — in "one"'s
    /// <c>/LI</c>, "deep" in "inner"'s; "two", going on at 0, back in the outer <c>/L</c>, and "twoinner"
    /// after it in "two"'s; "other", a list of its own, a new one. A depth past every open list is the
    /// next one down.
    /// </summary>
    [Fact]
    public void Pdf_AListPartTakenOutOfItsItem_IsTaggedInIt()
    {
        Document doc = Doc(
            Ordered("one"),
            new ListBlock { NestingDepth = 1, Items = { new ListItem { Text = "inner" } } },
            new ListBlock { NestingDepth = 2, Items = { new ListItem { Text = "deep" } } },
            new ListBlock { NestingDepth = 1, ContinuesPrevious = true, Items = { new ListItem { Text = "inner2" } } },
            new ListBlock { IsOrdered = true, Start = 2, ContinuesPrevious = true, Items = { new ListItem { Text = "two", IsNumbered = true } } },
            new ListBlock { NestingDepth = 1, Items = { new ListItem { Text = "twoinner" } } },
            Ordered("other"));
        doc.Tagged = true;

        byte[] pdf = doc.ToPdf();
        var edges = StructureEdges(pdf);
        int Level(string path) => Occurrences(path, "/LI");

        Assert.Equal(5, edges.Count(e => e.Role == "L"));
        Assert.Equal(3, edges.Count(e => e.Role == "L" && e.Parent == "LI"));
        var items = edges.Where(e => e.Role == "LI").ToList();
        Assert.Equal(3, items.Count(e => Level(e.Path) == 1));    // one, two, other
        Assert.Equal(3, items.Count(e => Level(e.Path) == 2));    // inner, inner2, twoinner
        Assert.Equal(1, items.Count(e => Level(e.Path) == 3));    // deep
        // Each sublist in its own item: "one", "inner" and "two" hold one /L each.
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 1 }, ListChildrenOfEachItem(pdf).OrderBy(c => c));

        Document past = Doc(Ordered("one"), new ListBlock { NestingDepth = 5, Items = { new ListItem { Text = "inner" } } });
        past.Tagged = true;
        Assert.Single(StructureEdges(past.ToPdf()), e => e.Role == "L" && e.Parent == "LI");
    }

    /// <summary>The marker and the body are drawn as separate runs, giving a hanging indent.</summary>
    /// <remarks>
    /// <b>Asserted through the x of the drawn text, because that is what separates them.</b> A
    /// single "• text" string would place both at the same x and could carry only one
    /// marked-content sequence — so <c>/Lbl</c> and <c>/LBody</c> would have nothing to be attached
    /// to, and a wrapped item's second line would align under its marker.
    /// </remarks>
    [Fact]
    public void Pdf_MarkerAndBody_AreDrawnAtDifferentX()
    {
        string content = PdfContentProbe.DecodeStreams(Doc(Ordered("alpha")).ToPdf());
        var drawn = PdfContentProbe.DrawnTexts(content);

        PdfContentProbe.DrawnText marker = drawn.First(d => d.Text.Contains("1.", StringComparison.Ordinal));
        PdfContentProbe.DrawnText body = drawn.First(d => d.Text.Contains("alpha", StringComparison.Ordinal));

        Assert.True(body.X > marker.X,
            $"the body should hang to the right of the marker: marker at {marker.X:F1}, body at {body.X:F1}");
    }

    // ================================================================ the fluent surface

    /// <summary>The fluent API reaches the container, its ordering and its nesting.</summary>
    [Fact]
    public void FluentApi_BuildsAnOrderedNestedList()
    {
        Document doc = new DocumentBuilder()
            .AddSection(s => s.AddList(l => l
                .StartAt(3)
                .AddItem("first")
                .AddItem(i => i.Text("second").AddList(sub => sub.AddItem("nested")))))
            .Build();

        var list = Assert.IsType<ListBlock>(Assert.Single(doc.Sections[0].Children));

        Assert.True(list.IsOrdered);
        Assert.Equal(3, list.Start);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("nested", Assert.Single(list.Items[1].Sublist!.Items).Text);
    }

    // ================================================================ helpers

    /// <summary>Every <c>/S</c> value in the produced document's structure tree.</summary>
    /// <remarks>
    /// Read through the object model rather than out of the bytes, because the roles are what is
    /// being asserted and a substring of the file cannot distinguish <c>/L</c> from the prefix of
    /// <c>/LI</c>.
    /// </remarks>
    private static HashSet<string> StructureRoles(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var roles = new HashSet<string>(StringComparer.Ordinal);

        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;
        if (root == null) return roles;

        var queue = new Queue<PdfObject?>();
        queue.Enqueue(root[new PdfName("K")]);
        var seen = new HashSet<int>();

        while (queue.Count > 0)
        {
            PdfObject? resolved = doc.ResolveObject(queue.Dequeue());
            if (resolved is PdfArray array)
            {
                foreach (PdfObject item in array) queue.Enqueue(item);
                continue;
            }

            if (resolved is not PdfDictionary dict) continue;
            if (dict.ObjectNumber > 0 && !seen.Add(dict.ObjectNumber)) continue;

            if (doc.ResolveObject(dict[new PdfName("S")]) is PdfName role) roles.Add(role.Value);
            if (dict[new PdfName("K")] != null) queue.Enqueue(dict[new PdfName("K")]);
        }

        return roles;
    }

    /// <summary>
    /// Every structure element's role with its parent element's role ("" under the root) and the roles
    /// from the root down to it, '/'-joined, in walk order.
    /// </summary>
    private static List<(string Role, string Parent, string Path)> StructureEdges(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var edges = new List<(string, string, string)>();
        if (doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) is not PdfDictionary root) return edges;

        var queue = new Queue<(PdfObject? Node, string Parent, string Path)>();
        queue.Enqueue((root[new PdfName("K")], "", ""));
        var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            var (node, parent, path) = queue.Dequeue();
            PdfObject? resolved = doc.ResolveObject(node);
            if (resolved is PdfArray array)
            {
                foreach (PdfObject item in array) queue.Enqueue((item, parent, path));
                continue;
            }
            if (resolved is not PdfDictionary dict || doc.ResolveObject(dict[new PdfName("S")]) is not PdfName role) continue;
            if (dict.ObjectNumber > 0 && !seen.Add(dict.ObjectNumber)) continue;
            string here = path + "/" + role.Value;
            edges.Add((role.Value, parent, here));
            if (dict[new PdfName("K")] != null) queue.Enqueue((dict[new PdfName("K")], role.Value, here));
        }
        return edges;
    }

    /// <summary>For each <c>/LI</c> in the structure tree, how many of its children are an <c>/L</c>.</summary>
    private static List<int> ListChildrenOfEachItem(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        string? RoleOf(PdfObject? node) =>
            doc.ResolveObject(node) is PdfDictionary d && doc.ResolveObject(d[new PdfName("S")]) is PdfName s ? s.Value : null;
        IEnumerable<PdfObject> Kids(PdfDictionary d) => doc.ResolveObject(d[new PdfName("K")]) switch
        {
            PdfArray a => a,
            null => Array.Empty<PdfObject>(),
            PdfObject one => new[] { one },
        };
        var counts = new List<int>();
        void Visit(PdfObject? node, int depth)
        {
            if (depth > 40 || doc.ResolveObject(node) is not PdfDictionary dict) return;
            if (RoleOf(dict) == "LI") counts.Add(Kids(dict).Count(k => RoleOf(k) == "L"));
            foreach (PdfObject kid in Kids(dict)) Visit(kid, depth + 1);
        }
        if (doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) is PdfDictionary root) Visit(root, 0);
        return counts;
    }

    private static ListBlock Ordered(params string[] items)
    {
        var list = new ListBlock { IsOrdered = true };
        foreach (string text in items) list.Items.Add(new ListItem { Text = text, IsNumbered = true });
        return list;
    }

    private static ListBlock Bulleted(params string[] items)
    {
        var list = new ListBlock();
        foreach (string text in items) list.Items.Add(new ListItem { Text = text });
        return list;
    }

    private static Document Doc(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (IDocElement child in children) section.Children.Add(child);
        doc.Sections.Add(section);
        return doc;
    }

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// The outermost <c>&lt;ul&gt;</c> or <c>&lt;ol&gt;</c> of the exported HTML, parsed as XML.
    /// </summary>
    /// <remarks>
    /// <b>The whole document cannot be parsed as XML and should not be.</b> It is HTML5, so
    /// <c>&lt;meta&gt;</c> is void and unclosed, which is correct and is not what these tests are
    /// about. The list fragment <i>is</i> well-formed by construction — every element this exporter
    /// writes for a list is closed — so parsing it is a real structural assertion rather than a
    /// substring match, which is the point: <c>&lt;li&gt;</c> appearing somewhere and
    /// <c>&lt;li&gt;</c> nested inside the right <c>&lt;ul&gt;</c> are different claims.
    /// </remarks>
    private static XElement ListXml(Document doc)
    {
        string html = Html(doc);

        int start = html.IndexOfAny(new[] { 'u', 'o' });
        for (start = 0; start < html.Length; start++)
        {
            if (html[start] != '<') continue;
            if (html.Length - start < 3) break;
            string open = html.Substring(start + 1, 2);
            if (open == "ul" || open == "ol") break;
        }

        Assert.True(start < html.Length, "the exported HTML contains no <ul> or <ol>:" +
            Environment.NewLine + html);

        string tag = html.Substring(start + 1, 2);
        int depth = 0;
        int i = start;

        while (i < html.Length)
        {
            int open = html.IndexOf("<" + tag, i, StringComparison.Ordinal);
            int close = html.IndexOf("</" + tag + ">", i, StringComparison.Ordinal);

            if (close < 0) break;
            if (open >= 0 && open < close) { depth++; i = open + 1; continue; }

            depth--;
            i = close + tag.Length + 3;
            if (depth == 0) return XElement.Parse(html.Substring(start, i - start));
        }

        throw new InvalidOperationException("unbalanced <" + tag + "> in the exported HTML:"
            + Environment.NewLine + html);
    }

    private static string Html(Document doc)
    {
        using var ms = new MemoryStream();
        new HtmlExporter().Export(doc, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>The exported package's <c>word/numbering.xml</c>.</summary>
    private static XDocument DocxNumberingXml(Document doc)
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        ZipArchiveEntry entry = zip.GetEntry("word/numbering.xml");

        Assert.True(entry != null,
            "the package has no word/numbering.xml, so its w:numId references resolve to nothing");

        using Stream part = entry!.Open();
        return XDocument.Load(part);
    }

    private static XDocument DocxXml(Document doc)
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using Stream part = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(part);
    }

    private static string Odt(Document doc)
    {
        using var ms = new MemoryStream();
        new OdtExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using Stream part = zip.GetEntry("content.xml")!.Open();
        using var reader = new StreamReader(part, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
