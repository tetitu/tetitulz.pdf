// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A list item with an explicit <see cref="ListItem.Marker"/> in the DOCX export: the marker
/// becomes the <b>level text</b> of a numbering instance of its own, so Word still sees a list.
/// </summary>
/// <remarks>
/// <para><b>What this replaces.</b> One overriding item used to demote the WHOLE list to typed
/// markers — paragraphs beginning with characters, which Word will not renumber, indent or
/// recognise as a list — and a diagnostic said so once per export. The premise was that a literal
/// marker has no scheme to rebuild a definition from. It does not need one: Word's own level text
/// is a literal string too (<c>w:lvlText</c>), and a bullet-format level whose text is the marker
/// draws exactly the marker the model recorded.</para>
///
/// <para><b>The instance shares the list's abstract definition</b>, and that is what keeps the
/// four exporters agreeing about the items AROUND the override. Instances that share an
/// <c>w:abstractNum</c> without a <c>w:startOverride</c> are one list for counting purposes
/// (§17.9.16), so the overriding paragraph still consumes its ordinal — which is the model's own
/// rule, <c>ListMarker(list, index) = Start + index</c>, and what the PDF and HTML exports draw. A
/// separate abstract definition would have numbered the item after it one lower than the other
/// three exporters do.</para>
///
/// <para><b>What a level text genuinely cannot carry</b> is a percent sign: <c>%N</c> is Word's
/// placeholder for level N's number and there is no escape for a literal one. That marker keeps
/// the typed run, and the report names the reason.</para>
/// </remarks>
public sealed class DocxLiteralMarkerTests
{
    /// <summary>An item with its own marker is still a native list paragraph.</summary>
    /// <remarks>
    /// <b>Both halves.</b> A <c>w:numPr</c> on every paragraph is what makes Word treat the run as
    /// a list; the absence of the marker from the paragraph text is what keeps it from being drawn
    /// twice — once by the definition and once as typed characters.
    /// </remarks>
    [Fact]
    public void AnItemWithAnExplicitMarker_IsStillANativeListParagraph()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Items[1].Marker = "א. ";

        XDocument xml = DocxXml(Doc(list));

        Assert.Equal(2, xml.Descendants(W + "numPr").Count());
        Assert.Equal("alphabeta", string.Concat(xml.Descendants(W + "t").Select(t => t.Value)));
    }

    /// <summary>
    /// The marker is the level text of a bullet level that the item's own instance overrides.
    /// </summary>
    /// <remarks>
    /// Read back from <c>numbering.xml</c> by following the paragraph's <c>w:numId</c>, not by
    /// searching the part for the string: a definition nothing references is a marker Word never
    /// draws.
    /// </remarks>
    [Fact]
    public void TheExplicitMarker_BecomesTheLevelTextOfTheItemsOwnInstance()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Items[1].Marker = "א. ";
        Document doc = Doc(list);

        string[] numIds = NumIds(DocxXml(doc));
        XElement instance = Instance(DocxNumberingXml(doc), numIds[1]);

        XElement level = instance.Element(W + "lvlOverride")!.Element(W + "lvl")!;

        Assert.Equal("0", instance.Element(W + "lvlOverride")!.Attribute(W + "ilvl")!.Value);
        Assert.Equal("bullet", level.Element(W + "numFmt")!.Attribute(W + "val")!.Value);
        Assert.Equal("א.", level.Element(W + "lvlText")!.Attribute(W + "val")!.Value);
    }

    /// <summary>
    /// The overriding instance points at the SAME abstract definition as the rest of the list, and
    /// restarts nothing.
    /// </summary>
    /// <remarks>
    /// This is the counting rule: instances sharing an abstract definition without a
    /// <c>w:startOverride</c> are one sequence, so "alpha", "א.", "gamma" numbers gamma <b>3</b> —
    /// the ordinal the model gives it (<c>Start + index</c>) and the one the PDF and HTML exports
    /// draw. A private abstract definition would number it 2, and only a reader opening all four
    /// files side by side would notice.
    /// </remarks>
    [Fact]
    public void TheOverridingInstance_SharesTheListsAbstractDefinitionAndRestartsNothing()
    {
        ListBlock list = Ordered("alpha", "beta", "gamma");
        list.Items[1].Marker = "א. ";
        Document doc = Doc(list);

        string[] numIds = NumIds(DocxXml(doc));
        XDocument numbering = DocxNumberingXml(doc);

        Assert.Equal(numIds[0], numIds[2]);
        Assert.NotEqual(numIds[0], numIds[1]);

        string listAbstract = Instance(numbering, numIds[0]).Element(W + "abstractNumId")!.Attribute(W + "val")!.Value;
        XElement literal = Instance(numbering, numIds[1]);

        Assert.Equal(listAbstract, literal.Element(W + "abstractNumId")!.Attribute(W + "val")!.Value);
        Assert.Empty(literal.Descendants(W + "startOverride"));
    }

    /// <summary>Items sharing one marker at one level share one instance; a different marker gets its own.</summary>
    /// <remarks>
    /// Twenty items marked "—" are twenty paragraphs and one <c>w:num</c>. Without the memo every
    /// item would mint an instance, which is legal and bloats the part for nothing.
    /// </remarks>
    [Fact]
    public void ItemsWithTheSameMarker_ShareOneInstance()
    {
        ListBlock list = Bulleted("one", "two", "three");
        list.Items[0].Marker = "— ";
        list.Items[1].Marker = "— ";
        list.Items[2].Marker = "→ ";

        string[] numIds = NumIds(DocxXml(Doc(list)));

        Assert.Equal(numIds[0], numIds[1]);
        Assert.NotEqual(numIds[0], numIds[2]);
    }

    /// <summary>A nested item's marker overrides ITS level, with that level's indent.</summary>
    /// <remarks>
    /// The override replaces one level of the definition, so it has to be the level the paragraph
    /// is written at — overriding level 0 for a level-1 item changes nothing the paragraph reads.
    /// And the replacement carries the list's own indent for that depth, or the item sits flush
    /// with its parent while every other level is stepped.
    /// </remarks>
    [Fact]
    public void ANestedItemsMarker_OverridesItsOwnLevelWithThatLevelsIndent()
    {
        ListBlock list = Ordered("outer");
        list.Items[0].Sublist = Ordered("inner");
        list.Items[0].Sublist!.Items[0].Marker = "→ ";
        Document doc = Doc(list);

        string[] numIds = NumIds(DocxXml(doc));
        XElement instance = Instance(DocxNumberingXml(doc), numIds[1]);
        XElement over = instance.Element(W + "lvlOverride")!;
        XElement level = over.Element(W + "lvl")!;

        Assert.Equal("1", over.Attribute(W + "ilvl")!.Value);
        Assert.Equal("1", level.Attribute(W + "ilvl")!.Value);
        Assert.Equal("→", level.Element(W + "lvlText")!.Attribute(W + "val")!.Value);

        // Level 1 of the list's own definition steps in by one more indent unit than level 0.
        string listLevel1Indent = DocxNumberingXml(doc).Descendants(W + "abstractNum").First()
            .Elements(W + "lvl").First(l => l.Attribute(W + "ilvl")!.Value == "1")
            .Descendants(W + "ind").Single().Attribute(W + "left")!.Value;

        Assert.Equal(listLevel1Indent, level.Descendants(W + "ind").Single().Attribute(W + "left")!.Value);
    }

    /// <summary>A list carrying explicit markers no longer says Word will not renumber it.</summary>
    /// <remarks>
    /// The report described the typed-marker fallback. Now that the marker is a level text the
    /// fallback is not taken, and a report that fired anyway would be a channel firing on input
    /// that lost nothing.
    /// </remarks>
    [Fact]
    public void AListWithExplicitMarkers_RaisesNoUnmaintainedReport()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Items[0].Marker = "א. ";
        list.Items[1].Marker = "ב. ";

        DocxXml(Doc(list));

        Assert.DoesNotContain("will not renumber", BuilderDiagnostics.LastError?.Detail ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker containing a percent sign cannot be a level text, so it keeps the typed run and
    /// the report names why.
    /// </summary>
    /// <remarks>
    /// <c>%1</c> in <c>w:lvlText</c> is Word's own placeholder for the level-1 counter and the
    /// format has no escape for a literal percent sign — a producer's "50% off" marker would be
    /// drawn as "50<i>counter</i> off". The one case the definition cannot express falls back to
    /// exactly what every case used to get, and only that case is reported.
    /// </remarks>
    [Fact]
    public void AMarkerWithAPercentSign_KeepsTheTypedRunAndReportsTheReason()
    {
        ListBlock list = Ordered("alpha", "beta");
        list.Items[0].Marker = "%1. ";

        XDocument xml = DocxXml(Doc(list));
        XElement[] paragraphs = xml.Descendants(W + "p").Where(p => p.Descendants(W + "t").Any()).ToArray();

        Assert.Empty(paragraphs[0].Descendants(W + "numPr"));
        Assert.Single(paragraphs[1].Descendants(W + "numPr"));
        Assert.Contains("%1. alpha", string.Concat(paragraphs[0].Descendants(W + "t").Select(t => t.Value)),
            StringComparison.Ordinal);

        string detail = BuilderDiagnostics.LastError?.Detail ?? "";
        Assert.Contains("will not renumber", detail, StringComparison.Ordinal);
        Assert.Contains("%", detail, StringComparison.Ordinal);
    }

    /// <summary>A loose item with no container still types its marker and says so.</summary>
    /// <remarks>
    /// The acceptance control for the change: a loose <see cref="ListItem"/> has no list for a
    /// definition to belong to, so the old path — and its report — is still the right answer there.
    /// </remarks>
    [Fact]
    public void ALooseItemWithAnExplicitMarker_StillTypesItAndReports()
    {
        XDocument xml = DocxXml(Doc(new ListItem { Text = "loose", Marker = "» " }));

        Assert.Empty(xml.Descendants(W + "numPr"));
        Assert.Contains("» loose", string.Concat(xml.Descendants(W + "t").Select(t => t.Value)),
            StringComparison.Ordinal);
        Assert.Contains("will not renumber", BuilderDiagnostics.LastError?.Detail ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>numbering.xml</c> keeps the schema's element order: every <c>w:abstractNum</c> before any
    /// <c>w:num</c> (<c>CT_Numbering</c>, ISO/IEC 29500 §17.9.17). The parts were appended per list —
    /// abstract then instance — so a second list, or any list allocated after a literal-marker
    /// instance wrote its <c>w:num</c>, placed its <c>w:abstractNum</c> after a <c>w:num</c>. Word
    /// repaired the part on open and a strict validator rejected it (Builder-1:4).
    /// </summary>
    [Fact]
    public void Numbering_KeepsEveryAbstractNumBeforeEveryNum()
    {
        ListBlock first = Ordered("one", "two");
        first.Items[1].Marker = "א. ";   // a literal-marker w:num, written before the second list is allocated
        ListBlock second = Ordered("a", "b");

        XDocument numbering = DocxNumberingXml(Doc(first, second));

        var kinds = numbering.Root!.Elements()
            .Where(e => e.Name == W + "abstractNum" || e.Name == W + "num")
            .Select(e => e.Name.LocalName)
            .ToList();

        int lastAbstract = kinds.LastIndexOf("abstractNum");
        int firstNum = kinds.IndexOf("num");
        Assert.True(lastAbstract >= 0 && firstNum >= 0, "the scenario produced both element kinds");
        Assert.True(lastAbstract < firstNum,
            "a w:abstractNum follows a w:num — numbering.xml is out of schema order: " + string.Join(",", kinds));

        // Control: two lists plus a literal marker really did produce two abstract definitions and
        // three instances, so the order assertion is not vacuously true on a one-list document.
        Assert.Equal(2, kinds.Count(k => k == "abstractNum"));
        Assert.Equal(3, kinds.Count(k => k == "num"));
    }

    // ================================================================ helpers

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>The <c>w:numId</c> of every list paragraph, in document order.</summary>
    private static string[] NumIds(XDocument document)
        => document.Descendants(W + "numId").Select(n => n.Attribute(W + "val")!.Value).ToArray();

    /// <summary>The <c>w:num</c> a paragraph's <c>w:numId</c> names.</summary>
    private static XElement Instance(XDocument numbering, string numId)
    {
        XElement? instance = numbering.Descendants(W + "num")
            .SingleOrDefault(n => n.Attribute(W + "numId")?.Value == numId);
        Assert.True(instance != null, $"numbering.xml defines no w:num with numId {numId}");
        return instance!;
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

    private static XDocument DocxNumberingXml(Document doc)
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = zip.GetEntry("word/numbering.xml");
        Assert.True(entry != null, "the package has no word/numbering.xml");

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
}
