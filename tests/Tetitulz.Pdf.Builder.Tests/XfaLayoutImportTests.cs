// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Import;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The designed layout of a dynamic XFA form: the template packet laid
/// out and the datasets bound into it. Every geometric assertion reads the drawn text's position
/// back out of the exported PDF, in page points, against the number the template's measurements
/// give — never against the model alone, since a model can hold a placement the exporter ignores.
/// The fixtures are the shape the XFA form designer writes (the an independent renderer pixel-test templates): a root
/// subform flowing top to bottom, a page-filling positioned subform inside it, draws and fields by
/// <c>x</c>/<c>y</c> in inches and millimetres.
/// </summary>
public sealed class XfaLayoutImportTests
{
    private const string Ns = "http://www.xfa.org/schema/xfa-template/3.3/";

    // ---------------------------------------------------------------- measurements

    [Theory]
    [InlineData("1in", 72.0)]
    [InlineData("25.4mm", 72.0)]
    [InlineData("2.54cm", 72.0)]
    [InlineData("1pc", 12.0)]
    [InlineData("1000mp", 1.0)]
    [InlineData("96px", 72.0)]
    [InlineData("12pt", 12.0)]
    [InlineData("12", 12.0)]
    [InlineData(" -0.5in ", -36.0)]
    public void Measure_ParsesEveryUnit(string text, double points)
    {
        Assert.True(XfaMeasure.TryParse(text, out double p));
        Assert.Equal(points, p, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e400")]
    [InlineData("NaN")]
    [InlineData("12 furlongs")]
    public void Measure_RefusesWhatIsNotOne(string text)
    {
        Assert.False(XfaMeasure.TryParse(text, out _));
        Assert.Equal(7.0, XfaMeasure.Parse(text, 7.0));
    }

    [Fact]
    public void Measure_RefusesTextLongerThanAMeasurement_BeforeThePatternRuns()
    {
        // The cap is the guard against a quadratic match on a long digit string that then fails;
        // a value one over it is refused, one under it that is a measurement still parses.
        string over = new string('0', XfaMeasure.MaxTextLength - 1) + "1in";
        Assert.Equal(XfaMeasure.MaxTextLength + 2, over.Length);
        Assert.False(XfaMeasure.TryParse(over, out _));
        string under = new string('0', XfaMeasure.MaxTextLength - 3) + "1in";
        Assert.True(XfaMeasure.TryParse(under, out double pts));
        Assert.Equal(72.0, pts, 6);
        // Padding spaces do not count: the text is trimmed first.
        Assert.True(XfaMeasure.TryParse(new string(' ', 100) + "1in" + new string(' ', 100), out pts));
        Assert.Equal(72.0, pts, 6);
    }

    [Fact]
    public void Color_RgbBecomesHex_AndIsClamped()
    {
        Assert.Equal("#FF0080", XfaMeasure.Color("255,0,128"));
        Assert.Equal("#FF00FF", XfaMeasure.Color("999,-5,255"));
        Assert.Null(XfaMeasure.Color("red"));
        Assert.Null(XfaMeasure.Color("1,2"));
    }

    // ---------------------------------------------------------------- hostile sizes

    /// <summary>
    /// A non-positive <c>medium</c> side would reach <c>PdfDocumentBuilder.AddPage</c>
    /// (<c>ArgumentOutOfRangeException "widthPt"</c>) or <c>ModelMeasurements</c> in every exporter;
    /// XfaMeasure admits a leading '-'. It falls back to Letter and the page lays out.
    /// </summary>
    [Theory]
    [InlineData("short=\"0\" long=\"11in\"")]
    [InlineData("short=\"-1in\" long=\"11in\"")]
    public void ANegativeOrZeroMediumSide_FallsBackToLetter_NotThrown(string medium)
    {
        string pageSet = "<pageSet><pageArea name=\"Page1\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"576pt\" h=\"756pt\"/><medium " + medium + "/></pageArea></pageSet>";
        byte[] pdf = Export(Template(pageSet, Block(Draw("T", "1in", "1in", "2in", "0.25in", "HELLO"))), null, out _);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
        Assert.True(TryText(pdf, "HELLO").HasValue, "the page laid out at the fallback size");
    }

    /// <summary>
    /// A negative caption <c>reserve</c> would become <c>BoxStart.MinHeightPt = -72</c>, which
    /// <c>ToPdf</c>/<c>ToDocx</c>/<c>ToHtml</c>/<c>ToOdt</c> all refuse ("must be at least 0"); it is
    /// dropped as it is read, and the field lays out.
    /// </summary>
    [Fact]
    public void ANegativeCaptionReserve_IsDropped_NotThrown()
    {
        string field = Field("f", "0in", "1in", "4in", "0.3in", caption: "L", reserve: "-1in");
        byte[] pdf = Export(Template(Page(), Block(field)), Data("<form1><f>v</f></form1>"), out _);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
        Assert.True(TryText(pdf, "L").HasValue, "the caption was drawn");
    }

    // ---------------------------------------------------------------- placement

    /// <summary>
    /// A draw at <c>x="1in" y="2in"</c> inside the page-filling subform of a content area at
    /// <c>0.25in</c>: its text starts 90 pt from the page's left edge and sits in the band 162 pt
    /// from its top, on one page.
    /// </summary>
    [Fact]
    public void PositionedDraw_LandsAtItsXY_InsideTheContentArea()
    {
        byte[] pdf = Export(Template(Page(), Block(Draw("T1", "1in", "2in", "2in", "0.25in", "HELLO"))), null, out var result);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
        var hello = Text(pdf, "HELLO");
        Assert.Equal(90.0, hello.X, 1);
        // 792 - 162 is the band's top; the baseline is within one line of it — the font's, 1.2 times its 12 pt.
        Assert.InRange(hello.Y, 792 - 162 - 1.2 * 12, 792 - 162);
        Assert.Empty(result.Approximations);
    }

    [Fact]
    public void Field_CaptionLeft_SitsBeforeTheValue_WithinItsReserve()
    {
        string field = Field("name", "0in", "1in", "4in", "0.3in", caption: "Name:", reserve: "1in");
        byte[] pdf = Export(Template(Page(), Block(field)), Data("<form1><name>Ada</name></form1>"), out _);
        var caption = Text(pdf, "Name:");
        var value = Text(pdf, "Ada");
        Assert.Equal(18.0, caption.X, 1);                 // content area's left
        Assert.Equal(18.0 + 72.0 + 1.0, value.X, 1);     // after the reserve, inside the value box
        Assert.InRange(Math.Abs(caption.Y - value.Y), 0, 2);   // the same band (the value box's 1 pt inset)
    }

    [Fact]
    public void Field_CaptionTop_PutsTheValueBelow()
    {
        string field = Field("name", "0in", "1in", "4in", "0.5in", caption: "Name:", reserve: "0.2in", placement: "top");
        byte[] pdf = Export(Template(Page(), Block(field)), Data("<form1><name>Ada</name></form1>"), out _);
        Assert.True(Text(pdf, "Ada").Y < Text(pdf, "Name:").Y - 10, "the value is not below its caption");
    }

    // ---------------------------------------------------------------- binding

    /// <summary>
    /// <b>Adversarial: two hundred fields bound by name into a record of five thousand children.</b> Each
    /// binding read the record's children afresh — fields × children, 10⁶ here and 10¹¹ from a datasets
    /// packet of a few megabytes; each element's children are read once now, the first time any is asked
    /// for, and every field still takes its own value.
    /// </summary>
    [Fact]
    public void AdversarialManyBindingsIntoALargeRecord_ReadTheRecordOnce()
    {
        var fields = new StringBuilder();
        for (int i = 0; i < 200; i++) fields.Append(Field("f" + i, "0in", "0in", "1in", "0.2in"));
        var record = new StringBuilder("<form1>");
        for (int i = 0; i < 5000; i++) record.Append("<f").Append(i).Append(">v").Append(i).Append("x</f").Append(i).Append('>');
        record.Append("</form1>");

        byte[] pdf = Export(Template(Page(), Block(fields.ToString())), Data(record.ToString()), out var result);

        Assert.Equal(1 + 5000, result.DataChildrenExamined);   // the data root's one child, then the record's
        Text(pdf, "v0x");
        Text(pdf, "v199x");
        Assert.Null(TryText(pdf, "v200x"));
    }

    [Fact]
    public void Field_TakesTheDataValue_OrTheTemplatesOwnWithoutData()
    {
        string field = Field("city", "0in", "1in", "3in", "0.3in", defaultValue: "Nowhere");
        byte[] bound = Export(Template(Page(), Block(field)), Data("<form1><city>Paris</city></form1>"), out var r1);
        Text(bound, "Paris");
        Assert.Null(TryText(bound, "Nowhere"));
        Assert.True(r1.HasData);

        byte[] unbound = Export(Template(Page(), Block(field)), null, out var r2);
        Assert.NotNull(TryText(unbound, "Nowhere"));
        Assert.False(r2.HasData);
    }

    [Fact]
    public void BindMatchNone_IgnoresTheData()
    {
        string field = Field("city", "0in", "1in", "3in", "0.3in", defaultValue: "Fixed", extra: "<bind match=\"none\"/>");
        byte[] pdf = Export(Template(Page(), Block(field)), Data("<form1><city>Paris</city></form1>"), out _);
        Assert.NotNull(TryText(pdf, "Fixed"));
        Assert.Null(TryText(pdf, "Paris"));

        // On a subform: nothing under it binds, whatever the data holds under its name.
        string plain = Field("city", "0in", "1in", "3in", "0.3in", defaultValue: "Fixed");
        string wrapper = "<subform name=\"w\" w=\"576pt\" h=\"100pt\"><bind match=\"none\"/>" + plain + "</subform>";
        byte[] unbound = Export(Template(Page(), wrapper), Data("<form1><w><city>Paris</city></w></form1>"), out _);
        Assert.NotNull(TryText(unbound, "Fixed"));
        Assert.Null(TryText(unbound, "Paris"));
        // The control: the same wrapper bound by name takes the data.
        byte[] bound = Export(Template(Page(), wrapper.Replace("<bind match=\"none\"/>", "")), Data("<form1><w><city>Paris</city></w></form1>"), out _);
        Assert.NotNull(TryText(bound, "Paris"));
    }

    [Fact]
    public void BindRef_FollowsADottedPath_AndReportsWhatItCannot()
    {
        string field = Field("x", "0in", "1in", "3in", "0.3in", extra: "<bind match=\"dataRef\" ref=\"$record.customer.name\"/>");
        byte[] pdf = Export(Template(Page(), Block(field)), Data("<form1><customer><name>Grace</name></customer></form1>"), out var ok);
        Assert.NotNull(TryText(pdf, "Grace"));
        Assert.DoesNotContain(ok.Approximations, a => a.Contains("bind ref", StringComparison.Ordinal));

        string odd = Field("x", "0in", "1in", "3in", "0.3in", extra: "<bind match=\"dataRef\" ref=\"$record.customer.*.name\"/>");
        Export(Template(Page(), Block(odd)), Data("<form1><customer><name>Grace</name></customer></form1>"), out var reported);
        Assert.Contains(reported.Approximations, a => a.Contains("bind ref beyond a dotted path", StringComparison.Ordinal));
    }

    /// <summary>A repeating subform occurs once per data instance, each below the last.</summary>
    [Fact]
    public void Occur_RepeatsASubformPerDataInstance_StackedDownThePage()
    {
        string item = "<subform name=\"item\" w=\"576pt\" h=\"20pt\"><occur min=\"1\" max=\"-1\"/>"
                      + Field("qty", "0in", "0in", "1in", "16pt") + "</subform>";
        byte[] pdf = Export(Template(Page(), item), Data("<form1><item><qty>AA1</qty></item><item><qty>BB2</qty></item><item><qty>CC3</qty></item></form1>"), out _);
        var a = Text(pdf, "AA1"); var b = Text(pdf, "BB2"); var c = Text(pdf, "CC3");
        Assert.Equal(a.X, b.X, 1);
        Assert.True(a.Y > b.Y && b.Y > c.Y, $"instances are not stacked: {a.Y:F1}, {b.Y:F1}, {c.Y:F1}");
        Assert.InRange(a.Y - b.Y, 19, 21);               // one 20 pt box per instance
    }

    [Fact]
    public void Occur_WithoutData_ShowsTheMinimum_AndZeroWhenMinIsZero()
    {
        string one = "<subform name=\"item\" w=\"576pt\" h=\"20pt\"><occur min=\"1\" max=\"-1\"/>" + Draw("d", "0in", "0in", "1in", "16pt", "ROW") + "</subform>";
        Assert.Single(Texts(Export(Template(Page(), one), null, out _), "ROW"));

        string zero = "<subform name=\"item\" w=\"576pt\" h=\"20pt\"><occur min=\"0\" max=\"-1\"/>" + Draw("d", "0in", "0in", "1in", "16pt", "ROW") + "</subform>";
        Assert.Empty(Texts(Export(Template(Page(), zero), Data("<form1/>"), out _), "ROW"));
    }

    [Fact]
    public void CheckButtonAndChoiceList_ShowWhatTheValueMeans()
    {
        string check = "<field name=\"ok\" x=\"0in\" y=\"1in\" w=\"2in\" h=\"0.3in\"><ui><checkButton/></ui><items><integer>1</integer></items></field>";
        string list = "<field name=\"colour\" x=\"0in\" y=\"2in\" w=\"2in\" h=\"0.3in\"><ui><choiceList/></ui>"
                      + "<items><text>Red</text><text>Blue</text></items><items save=\"1\"><text>r</text><text>b</text></items></field>";
        byte[] on = Export(Template(Page(), Block(check + list)), Data("<form1><ok>1</ok><colour>b</colour></form1>"), out _);
        var mark = Text(on, "[X]");
        var blue = Text(on, "Blue");
        // Each in its own field's box: the check at y = 1 in, the list an inch below it, same x.
        Assert.Equal(mark.X, blue.X, 1);
        Assert.Equal(72.0, mark.Y - blue.Y, 1);
        Assert.Null(TryText(on, "Red"));
        byte[] off = Export(Template(Page(), Block(check + list)), Data("<form1><ok>0</ok><colour>r</colour></form1>"), out _);
        var clear = Text(off, "[" + (char)0xA0 + "]");   // a no-break space: one draw, not three
        Assert.Equal(mark.Y, clear.Y, 1);
        Text(off, "Red");
        Assert.Null(TryText(off, "Blue"));
    }

    // ---------------------------------------------------------------- structure

    [Fact]
    public void TableLayout_BecomesATable_RowPerSubformInstance()
    {
        string table = "<subform name=\"t\" layout=\"table\" columnWidths=\"2in 3in\">"
                       + "<subform name=\"r\" layout=\"row\"><occur min=\"1\" max=\"-1\"/>" + Draw("a", "0", "0", "2in", "0.3in", "Label") + Field("v", "0", "0", "3in", "0.3in") + "</subform></subform>";
        var result = XfaLayout.Layout(Parse(Template(Page(), table)), Data("<form1><t><r><v>one</v></r><r><v>two</v></r></t></form1>"));
        Table t = Assert.Single(result.Document.Sections[0].Children.OfType<Table>());
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(new[] { 144.0, 216.0 }, t.ColumnWidths);
        Assert.Equal("two", t.Rows[1].Cells[1].Paragraphs[0].Runs.OfType<TextRun>().Single().Text);
        Assert.Equal("Label", t.Rows[1].Cells[0].Paragraphs[0].Runs.OfType<TextRun>().Single().Text);
    }

    [Fact]
    public void BreakBefore_ToAPageArea_StartsASectionWithThatPagesGeometry()
    {
        string pages = "<pageSet><pageArea name=\"Page1\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"8in\" h=\"10.5in\"/><medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/></pageArea>"
                       + "<pageArea name=\"Page2\"><contentArea x=\"0.5in\" y=\"0.5in\" w=\"10in\" h=\"7.5in\"/><medium stock=\"letter\" short=\"8.5in\" long=\"11in\" orientation=\"landscape\"/></pageArea></pageSet>";
        string second = "<subform w=\"8in\" h=\"1in\"><breakBefore targetType=\"pageArea\" target=\"xfa.template.form1.Page2\"/>" + Draw("d", "0", "0", "2in", "0.3in", "SECOND") + "</subform>";
        byte[] pdf = Export(Template(pages, Block(Draw("f", "0", "0", "2in", "0.3in", "FIRST")) + second), null, out var result);
        Assert.Equal(2, result.PageAreasUsed);
        Assert.Equal(2, result.Document.Sections.Count);
        Assert.Equal(792.0, result.Document.Sections[1].PageWidthPt, 3);
        Assert.Equal(612.0, result.Document.Sections[1].PageHeightPt, 3);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// A page area of two content areas side by side (XFA 3.3 chapter 8, "Flowing Between
    /// ContentArea Objects"): the flow fills the left one, then the right one from its top, then the
    /// next page's left — twenty-five 20 pt rows in areas 200 pt tall, ten to an area.
    /// </summary>
    [Fact]
    public void SeveralContentAreas_TheFlowFillsEachInDocumentOrder()
    {
        string page = "<pageSet><pageArea name=\"Page1\">"
                      + "<contentArea name=\"left\" x=\"36pt\" y=\"72pt\" w=\"250pt\" h=\"200pt\"/>"
                      + "<contentArea name=\"right\" x=\"326pt\" y=\"72pt\" w=\"250pt\" h=\"200pt\"/>"
                      + "<medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>";
        var rows = new StringBuilder();
        for (int i = 1; i <= 25; i++) rows.Append("<subform w=\"250pt\" h=\"20pt\">" + Draw("d" + i, "0", "0", "2in", "16pt", "ROW" + i + "X") + "</subform>");
        byte[] pdf = Export(Template(page, rows.ToString()), null, out var result);

        var xs = Enumerable.Range(1, 25).Select(i => Math.Round(Text(pdf, "ROW" + i + "X").X)).ToList();
        Assert.All(xs.Take(10), x => Assert.Equal(36, x));
        Assert.All(xs.Skip(10).Take(10), x => Assert.Equal(326, x));
        Assert.All(xs.Skip(20), x => Assert.Equal(36, x));
        Assert.Equal(Text(pdf, "ROW1X").Y, Text(pdf, "ROW11X").Y, 1);   // the right area from its own top
        Assert.InRange(Text(pdf, "ROW1X").Y, 792 - 72 - 20, 792 - 72);   // 72 pt down, where the areas begin
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
        Assert.Equal(2, result.Document.Sections[0].ContentAreas.Count);
        Assert.Empty(result.Approximations);
    }

    /// <summary>
    /// Content areas of different widths are the section's regions at their own widths, and nothing is
    /// reported: a block continuing from one into the other takes the width it enters.
    /// <c>ContentAreaFlowTests</c> has the widths.
    /// </summary>
    [Fact]
    public void ContentAreasOfDifferentWidths_AreRegionsOfTheirOwnWidths()
    {
        string pages = "<pageSet><pageArea name=\"Page1\">"
            + "<contentArea x=\"36pt\" y=\"72pt\" w=\"250pt\" h=\"200pt\"/>"
            + "<contentArea x=\"326pt\" y=\"72pt\" w=\"150pt\" h=\"200pt\"/>"
            + "<medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>";
        string body = "<subform w=\"250pt\" h=\"20pt\">" + Draw("d", "0", "0", "2in", "16pt", "ROW") + "</subform>";

        var mixed = XfaLayout.Layout(Parse(Template(pages, body)), null);
        Assert.Equal(new[] { 250.0, 150.0 }, mixed.Document.Sections[0].ContentAreas.Select(a => a.WidthPt).ToArray());
        Assert.Empty(mixed.Approximations);
    }

    /// <summary>A flowed draw that states no width takes its content area's: right-aligned, it ends at the area's right edge.</summary>
    [Fact]
    public void AFlowedDrawWithoutAWidth_TakesItsContentAreasWidth()
    {
        string page = "<pageSet><pageArea name=\"Page1\">"
                      + "<contentArea x=\"36pt\" y=\"72pt\" w=\"250pt\" h=\"200pt\"/><contentArea x=\"326pt\" y=\"72pt\" w=\"250pt\" h=\"200pt\"/>"
                      + "<medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>";
        string draw = "<draw name=\"r\" h=\"16pt\"><value><text>RIGHTX</text></value><font typeface=\"Myriad Pro\" size=\"12pt\"/><para hAlign=\"right\"/></draw>";
        byte[] pdf = Export(Template(page, draw), null, out _);
        Assert.InRange(Text(pdf, "RIGHTX").X, 36 + 250 - 60, 36 + 250 - 20);
    }

    /// <summary>
    /// Content areas of different heights: a subform taller than the first is not held to the
    /// first's height — it goes whole to the taller area, and the next subform follows it there.
    /// </summary>
    [Fact]
    public void ASubformTallerThanTheFirstArea_FitsTheTallerOne()
    {
        string page = "<pageSet><pageArea name=\"Page1\">"
                      + "<contentArea x=\"36pt\" y=\"72pt\" w=\"250pt\" h=\"200pt\"/><contentArea x=\"326pt\" y=\"72pt\" w=\"250pt\" h=\"400pt\"/>"
                      + "<medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>";
        string first = "<subform w=\"250pt\" h=\"20pt\">" + Draw("f", "0", "0", "2in", "16pt", "FIRSTX") + "</subform>";
        string tall = "<subform w=\"250pt\" h=\"300pt\">" + Draw("t", "0", "0", "2in", "16pt", "TALLX") + "</subform>";
        string next = "<subform w=\"250pt\" h=\"20pt\">" + Draw("n", "0", "0", "2in", "16pt", "NEXTX") + "</subform>";
        byte[] pdf = Export(Template(page, first + tall + next), null, out _);

        Assert.Equal(36, Text(pdf, "FIRSTX").X, 1);
        Assert.Equal(326, Text(pdf, "TALLX").X, 1);
        Assert.Equal(Text(pdf, "FIRSTX").Y, Text(pdf, "TALLX").Y, 1);    // at the right area's top
        Assert.Equal(326, Text(pdf, "NEXTX").X, 1);
        // 300 pt below the tall subform's top, 72 pt down.
        Assert.InRange(Text(pdf, "NEXTX").Y, 792 - 72 - 300 - 20, 792 - 72 - 300);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// A repeating subform inside a positioned one: each instance is placed by its one offset vector
    /// (XFA 3.3 chapter 7, "Positioned Layout"), so both are drawn at the same place — what the
    /// template says, and not reported.
    /// </summary>
    [Fact]
    public void RepeatingSubformInsideAPositionedOne_SharesItsOffset()
    {
        string item = "<subform name=\"item\" x=\"1in\" y=\"2in\" w=\"2in\" h=\"20pt\"><occur min=\"1\" max=\"-1\"/>"
                      + Field("qty", "0in", "0in", "1in", "16pt") + "</subform>";
        byte[] pdf = Export(Template(Page(), "<subform name=\"page\" w=\"576pt\" h=\"756pt\">" + item + "</subform>"),
                            Data("<form1><page><item><qty>AA1</qty></item><item><qty>BB2</qty></item></page></form1>"), out var result);
        var a = Text(pdf, "AA1");
        var b = Text(pdf, "BB2");
        Assert.Equal(a.X, b.X, 1);
        Assert.Equal(a.Y, b.Y, 1);
        Assert.Equal(18 + 72 + 1, a.X, 1);     // the content area, the offset, the value box's inset
        Assert.Empty(result.Approximations);
    }

    /// <summary>
    /// A table subform inside a positioned one is placed at its offset vector, its rows laid out
    /// inside it — not after the positioned subform in the flow — and is not reported.
    /// </summary>
    [Fact]
    public void TableInsideAPositionedSubform_IsPlacedAtItsOffset()
    {
        string table = "<subform name=\"t\" layout=\"table\" x=\"1in\" y=\"2in\" w=\"5in\" columnWidths=\"2in 3in\">"
                       + "<subform name=\"r\" layout=\"row\"><occur min=\"1\" max=\"-1\"/>" + Draw("a", "0", "0", "2in", "0.3in", "Label") + Field("v", "0", "0", "3in", "0.3in") + "</subform></subform>";
        string after = Draw("below", "0in", "5in", "2in", "0.3in", "BELOW");
        byte[] pdf = Export(Template(Page(), Block(table + after)), Data("<form1><t><r><v>one</v></r><r><v>two</v></r></t></form1>"), out var result);

        var label = Texts(pdf, "Label").OrderByDescending(t => t.Y).First();
        var one = Text(pdf, "one");
        var two = Text(pdf, "two");
        Assert.InRange(label.X, 18 + 72, 18 + 72 + 6);              // the table's left edge and a cell's inset
        Assert.InRange(one.X, 18 + 72 + 144, 18 + 72 + 144 + 6);    // the second column, 2 in along
        Assert.InRange(label.Y, 792 - 18 - 144 - 20, 792 - 18 - 144);
        Assert.True(two.Y < one.Y, "the rows are not laid out down the table");
        // The draw 5 in down stays below the table, on the same page: nothing moved into the flow.
        Assert.True(Text(pdf, "BELOW").Y < two.Y);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
        Assert.Empty(result.Approximations);
    }

    /// <summary>
    /// A positioned subform that only part fits the rest of its content area goes whole to the next
    /// page (XFA 3.3 chapter 8, "Content Splitting": its draws are kept intact): forty 20 pt rows in a
    /// 750 pt area, so the thirty-eighth, with 10 pt left for it, starts page 2 — its draw with it, not
    /// left below the first page's area.
    /// </summary>
    [Fact]
    public void APositionedSubformThatDoesNotFit_MovesWhole()
    {
        string page = "<pageSet><pageArea name=\"Page1\"><contentArea x=\"18pt\" y=\"18pt\" w=\"576pt\" h=\"750pt\"/><medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>";
        var rows = new StringBuilder();
        for (int i = 1; i <= 40; i++) rows.Append("<subform w=\"576pt\" h=\"20pt\">" + Draw("d" + i, "0", "0", "2in", "16pt", "ROW" + i + "X") + "</subform>");
        byte[] pdf = Export(Template(page, rows.ToString()), null, out _);

        double areaBottom = 792 - 18 - 750;
        Assert.True(Text(pdf, "ROW37X").Y > areaBottom, "the last row that fits is not on the first page");
        // Row 38 at the second page's top, where row 1 is on the first.
        Assert.Equal(Text(pdf, "ROW1X").Y, Text(pdf, "ROW38X").Y, 1);
        Assert.Equal(Text(pdf, "ROW38X").Y - 20, Text(pdf, "ROW39X").Y, 1);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    [Fact]
    public void PageDecorations_RepeatOnEveryPage_TheFlowProduces()
    {
        // A footer draw on the page area, and forty 20 pt rows: two pages at least.
        string page = "<pageSet><pageArea name=\"Page1\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"8in\" h=\"10.5in\"/><medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/>"
                      + Draw("foot", "0.25in", "10.8in", "2in", "0.15in", "FOOTER") + "</pageArea></pageSet>";
        var rows = new StringBuilder();
        for (int i = 0; i < 40; i++) rows.Append("<subform w=\"8in\" h=\"20pt\">" + Draw("d" + i, "0", "0", "2in", "16pt", "ROW" + i) + "</subform>");
        byte[] pdf = Export(Template(page, rows.ToString()), null, out _);
        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 2, "forty 20 pt boxes should overflow a 756 pt area");
        Assert.Equal(pages, Texts(pdf, "FOOTER").Count);
    }

    [Fact]
    public void HiddenNodes_AreNotDrawn()
    {
        string hidden = Draw("h", "1in", "1in", "2in", "0.3in", "HIDDEN").Replace("<draw ", "<draw presence=\"hidden\" ");
        byte[] pdf = Export(Template(Page(), Block(hidden + Draw("v", "1in", "2in", "2in", "0.3in", "SHOWN"))), null, out _);
        Assert.Null(TryText(pdf, "HIDDEN"));
        Assert.NotNull(TryText(pdf, "SHOWN"));
    }

    [Fact]
    public void RectangleAndLine_DrawAsRules()
    {
        string rect = "<draw name=\"r\" x=\"1in\" y=\"1in\" w=\"2in\" h=\"1in\"><value><rectangle/></value><border><edge thickness=\"1pt\"/></border></draw>";
        string line = "<draw name=\"l\" x=\"1in\" y=\"3in\" w=\"3in\" h=\"0pt\"><value><line/></value><border><edge thickness=\"1pt\"/></border></draw>";
        byte[] pdf = Export(Template(Page(), Block(rect + line)), null, out _);
        string content = PdfContentProbe.DecodeStreams(pdf);
        var horizontals = PdfContentProbe.StrokedSegments(content).Where(s => s.IsHorizontal).ToList();
        // The rectangle's top and bottom edges (144 pt wide at x = 90) and the 216 pt rule at x = 90.
        string drawn = string.Join(" ", horizontals.Select(s => s.ToString()));
        Assert.True(horizontals.Count >= 3, "expected the rectangle's edges and the rule; horizontals: " + drawn);
        Assert.True(horizontals.Any(s => Math.Abs(Math.Min(s.X0, s.X1) - 90) < 2 && Math.Abs(Math.Abs(s.X1 - s.X0) - 144) < 3), "no rectangle edge; horizontals: " + drawn);
        Assert.True(horizontals.Any(s => Math.Abs(Math.Min(s.X0, s.X1) - 90) < 2 && Math.Abs(Math.Abs(s.X1 - s.X0) - 216) < 3), "no 216 pt rule; horizontals: " + drawn);
    }

    /// <summary>
    /// A line whose box has both sides is its diagonal (chapter 17: <c>\</c> top-left to bottom-right,
    /// <c>/</c> bottom-left to top-right): the stroke drawn under the turn ends at the box's corners, in
    /// the line's own edge colour. A line no taller than its stroke stays a horizontal rule, unturned.
    /// </summary>
    [Fact]
    public void SlopedLine_IsDrawnCornerToCorner()
    {
        string down = "<draw name=\"down\" x=\"1in\" y=\"3in\" w=\"3in\" h=\"1in\"><value><line><edge thickness=\"1pt\"><color value=\"0,0,255\"/></edge></line></value></draw>";
        string up = "<draw name=\"up\" x=\"1in\" y=\"5in\" w=\"3in\" h=\"1in\"><value><line slope=\"/\"><edge thickness=\"1pt\"/></line></value></draw>";
        string flat = "<draw name=\"flat\" x=\"1in\" y=\"7in\" w=\"3in\" h=\"0.5pt\"><value><line><edge thickness=\"1pt\"/></line></value></draw>";
        string hidden = "<draw name=\"hidden\" x=\"1in\" y=\"9in\" w=\"3in\" h=\"0pt\"><value><line><edge presence=\"hidden\"/></line></value></draw>";
        byte[] pdf = Export(Template(Page(), Block(down + up + flat + hidden)), null, out XfaLayoutResult result);
        string content = PdfContentProbe.DecodeStreams(pdf);
        var turned = TurnedSegments(content);
        string drawn = string.Join(" ", turned.Select(s => $"({s.X0:0.#},{s.Y0:0.#})→({s.X1:0.#},{s.Y1:0.#})"));

        // The content area starts 18 pt in; the page is 792 pt tall and PDF's y runs up.
        static bool Joins(TurnedSegment s, double ax, double ay, double bx, double by) =>
            (Near(s.X0, ax) && Near(s.Y0, ay) && Near(s.X1, bx) && Near(s.Y1, by))
            || (Near(s.X0, bx) && Near(s.Y0, by) && Near(s.X1, ax) && Near(s.Y1, ay));
        Assert.True(turned.Any(s => Joins(s, 90, 558, 306, 486)), "no top-left to bottom-right diagonal; turned: " + drawn);
        Assert.True(turned.Any(s => Joins(s, 90, 342, 306, 414)), "no bottom-left to top-right diagonal; turned: " + drawn);
        Assert.Contains("0 0 1 RG", content, StringComparison.Ordinal);
        Assert.Contains(PdfContentProbe.StrokedSegments(content),
            s => s.IsHorizontal && Near(Math.Min(s.X0, s.X1), 90) && Near(Math.Abs(s.X1 - s.X0), 216) && s.Y0 < 280);
        Assert.Equal(2, turned.Count(s => Math.Abs(s.Y1 - s.Y0) > 10));
        Assert.DoesNotContain(turned, s => s.Y0 < 280 && s.Y1 < 280);   // the flat line is not turned
        // The hidden edge draws nothing at its 126 pt.
        Assert.DoesNotContain(PdfContentProbe.StrokedSegments(content), s => Math.Abs(s.Y0 - 126) < 3 || Math.Abs(s.Y1 - 126) < 3);
        Assert.DoesNotContain(result.Approximations, a => a.Contains("line", StringComparison.Ordinal));
    }

    /// <summary>
    /// In a flow a shape is a box of its size: an arc the ellipse box, filled and stroked by its own fill
    /// and edge, inside the draw's border's own box; a sloped line a box whose edge is turned onto the
    /// diagonal.
    /// </summary>
    [Fact]
    public void FlowedShapes_AreBoxesOfTheirShape()
    {
        string arc = "<draw name=\"a\" w=\"2in\" h=\"1in\"><border><edge thickness=\"3pt\"/></border><value><arc circular=\"1\"><edge thickness=\"2pt\"/>"
                     + "<fill><color value=\"255,0,0\"/></fill></arc></value></draw>";
        string line = "<draw name=\"l\" w=\"3in\" h=\"1in\"><value><line slope=\"/\"/></value></draw>";
        var result = XfaLayout.Layout(Parse(Template(Page(), "<subform layout=\"tb\" w=\"576pt\">" + arc + line + "</subform>")), null);
        var boxes = result.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().ToList();

        BoxStart ellipse = boxes.Single(b => b.CornerRadiiPercent != null);
        Assert.Equal(new[] { 50.0, 50, 50, 50 }, ellipse.CornerRadiiPercent);
        Assert.Equal("#FF0000", ellipse.Shading);
        Assert.Equal(2, ellipse.BorderTop!.Width, 3);
        Assert.Equal(72, ellipse.WidthPt, 3);
        Assert.Contains(boxes, b => b.BorderTop?.Width == 3 && b.WidthPt == 144 && b.CornerRadiiPercent == null);

        BoxStart turned = boxes.Single(b => b.Transform != null);
        Assert.NotNull(turned.BorderBottom);
        Assert.Null(turned.BorderTop);
        double angle = Math.Atan2(turned.Transform![1], turned.Transform[0]) * 180 / Math.PI;
        Assert.Equal(Math.Atan2(72, 216) * 180 / Math.PI, angle, 3);
        Assert.Equal(Math.Sqrt(216.0 * 216 + 72 * 72) / 216, Math.Sqrt(turned.Transform[0] * turned.Transform[0] + turned.Transform[1] * turned.Transform[1]), 3);
        Assert.Equal(new[] { 0.0, 0, 1, 0 }, turned.TransformOrigin);
        Assert.DoesNotContain(result.Approximations, a => a.Contains("arc", StringComparison.Ordinal));

        // A quarter arc in a flow is its own path in a box of its size: from 3 o'clock up to 12.
        string quarter = "<subform layout=\"tb\" w=\"576pt\">" + arc.Replace("<arc circular=\"1\">", "<arc sweepAngle=\"90\">") + "</subform>";
        var partial = XfaLayout.Layout(Parse(Template(Page(), quarter)), null);
        Assert.DoesNotContain(partial.Approximations, a => a.Contains("arc", StringComparison.Ordinal));
        var children = partial.Document.Sections.SelectMany(s => s.Children).ToList();
        ImageRun drawn = children.OfType<Paragraph>().SelectMany(p => p.Runs).OfType<ImageRun>().Single();
        Assert.Equal((144.0, 72.0), (drawn.WidthPt, drawn.HeightPt));
        Assert.Equal((144.0, 36.0), (drawn.Drawing!.Path[0].X, drawn.Drawing.Path[0].Y));
        Assert.Equal((72.0, 0.0), (Math.Round(drawn.Drawing.Path[^1].X, 6), Math.Round(drawn.Drawing.Path[^1].Y, 6)));
        Assert.Equal(("#FF0000", 2.0), (drawn.Drawing.FillColor, drawn.Drawing.StrokeWidthPt));
        Assert.True(drawn.IsDecorative);
        // The box holding it keeps the arc's room.
        Assert.Contains(children.OfType<BoxStart>(), b => b.WidthPt == 144 && b.MinHeightPt == 72 && b.CornerRadiiPercent == null && b.BorderTop == null);
    }

    /// <summary>
    /// An arc of a full turn is the ellipse its region holds, stroked and filled by its own edge and
    /// fill; <c>circular="1"</c> is a circle across the smaller side, centred; the draw's border is a
    /// frame apart. An arc of less than a turn is its own path, placed in the region and left open.
    /// </summary>
    [Fact]
    public void Arc_IsItsEllipse_WithItsOwnEdgeAndFill()
    {
        string arc = "<draw name=\"a\" x=\"1in\" y=\"1in\" w=\"2in\" h=\"1in\"><value><arc><edge thickness=\"2pt\"><color value=\"0,0,255\"/></edge>"
                     + "<fill><color value=\"255,0,0\"/></fill></arc></value></draw>";
        string circle = "<draw name=\"c\" x=\"4in\" y=\"1in\" w=\"2in\" h=\"1in\"><border><edge/></border><value><arc circular=\"1\"/></value></draw>";
        var result = XfaLayout.Layout(Parse(Template(Page(), Block(arc + circle))), null);
        var shapes = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>()
            .Where(p => p.CornerRadiiPercent != null).ToList();
        Assert.Equal(2, shapes.Count);
        foreach (Paragraph p in shapes) Assert.Equal(new[] { 50.0, 50, 50, 50 }, p.CornerRadiiPercent);

        Paragraph ellipse = shapes.Single(p => p.Shading == "#FF0000");
        Assert.Equal(144, ellipse.Placement!.WidthPt, 3);
        Assert.Equal(2, ellipse.BorderTop!.Width, 3);
        Assert.Equal("#0000FF", ellipse.BorderTop.Color);

        Paragraph round = shapes.Single(p => p != ellipse);
        Assert.Equal(72, round.Placement!.WidthPt, 3);
        Assert.Equal(72, round.LineSpacingPt, 3);
        Assert.Equal(3 * 72 + 36, round.Placement.LeftPt - ellipse.Placement.LeftPt, 3);
        // The circle's draw has a border: its frame is the draw's own 144 pt box, with no corners.
        Assert.Contains(result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>(),
            p => p.CornerRadiiPercent == null && p.Placement != null && Near(p.Placement.WidthPt, 144)
                 && Near(p.Placement.LeftPt, round.Placement.LeftPt - 36));
        Assert.DoesNotContain(result.Approximations, a => a.Contains("arc", StringComparison.Ordinal));

        // A shape is drawn in the draw's content region: its margin insets it.
        string inset = "<draw name=\"m\" x=\"1in\" y=\"4in\" w=\"2in\" h=\"1in\"><margin leftInset=\"10pt\" rightInset=\"20pt\" topInset=\"5pt\" bottomInset=\"5pt\"/>"
                       + "<value><arc/></value></draw>";
        var insetResult = XfaLayout.Layout(Parse(Template(Page(), Block(arc + inset))), null);
        var insetShapes = insetResult.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>()
            .Where(p => p.CornerRadiiPercent != null).ToList();
        Paragraph outer = insetShapes.Single(p => p.Shading == "#FF0000"), inner = insetShapes.Single(p => p != outer);
        Assert.Equal(10, inner.Placement!.LeftPt - outer.Placement!.LeftPt, 3);
        Assert.Equal(144 - 30, inner.Placement.WidthPt, 3);
        Assert.Equal(72 - 10, inner.LineSpacingPt, 3);

        // A half turn is its own path, placed in the region: 3 o'clock over the top to 9, open, so its fill
        // is the half ellipse its chord closes — the specification's "red-filled semicircle".
        string half = arc.Replace("<arc>", "<arc sweepAngle=\"180\">");
        var partial = XfaLayout.Layout(Parse(Template(Page(), Block(half))), null);
        Assert.DoesNotContain(partial.Approximations, a => a.Contains("arc", StringComparison.Ordinal));
        Paragraph placed = partial.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.Runs.OfType<ImageRun>().Any());
        Assert.Equal(ellipse.Placement.LeftPt, placed.Placement!.LeftPt, 3);
        Assert.Equal(ellipse.Placement.TopPt, placed.Placement.TopPt, 3);
        VectorDrawing halfPath = placed.Runs.OfType<ImageRun>().Single().Drawing!;
        Assert.Equal(3, halfPath.Path.Count);                               // a move and two quarter curves
        Assert.Equal((0.0, 36.0), (Math.Round(halfPath.Path[^1].X, 6), Math.Round(halfPath.Path[^1].Y, 6)));
        Assert.DoesNotContain(halfPath.Path, c => c.Verb == PathVerb.Close);
        Assert.Equal(("#FF0000", "#0000FF"), (halfPath.FillColor, halfPath.StrokeColor));
    }

    /// <summary>
    /// <see cref="XfaLayout.ArcDrawing"/>: an arc's path in its box, its angles counter-clockwise from 3
    /// o'clock as the page is seen (y runs down in the box), clockwise for a negative sweep; cubic
    /// segments of at most a quarter turn, each passing through the ellipse at its middle; a sweep
    /// past a turn held at one; a hidden edge strokes nothing.
    /// </summary>
    [Fact]
    public void ArcDrawing_FollowsTheEllipse_CounterClockwise()
    {
        var edge = new XfaBorder { Visible = true, Thickness = 1, Color = "#000000" };
        (double X, double Y) End(VectorDrawing d) => (Math.Round(d.Path[^1].X, 6), Math.Round(d.Path[^1].Y, 6));

        VectorDrawing up = XfaLayout.ArcDrawing(100, 100, 0, 90, edge);
        Assert.Equal((100.0, 50.0), (up.Path[0].X, up.Path[0].Y));
        Assert.Equal(2, up.Path.Count);
        Assert.Equal((50.0, 0.0), End(up));
        Assert.Equal((50.0, 100.0), End(XfaLayout.ArcDrawing(100, 100, 0, -90, edge)));

        VectorDrawing tilted = XfaLayout.ArcDrawing(100, 100, 45, 180, edge);
        Assert.Equal(3, tilted.Path.Count);
        Assert.Equal((Math.Round(50 - 50 * Math.Sqrt(0.5), 6), Math.Round(50 + 50 * Math.Sqrt(0.5), 6)), End(tilted));

        // Each segment's middle (t = ½) lies on the ellipse: 200 × 100, a 45° sweep and a 90° one.
        foreach (VectorDrawing d in new[] { XfaLayout.ArcDrawing(200, 100, 0, 45, edge), XfaLayout.ArcDrawing(200, 100, 30, 90, edge) })
        {
            PathCommand from = d.Path[0], c = d.Path[1];
            double mx = (from.X + 3 * c.C1X + 3 * c.C2X + c.X) / 8, my = (from.Y + 3 * c.C1Y + 3 * c.C2Y + c.Y) / 8;
            double onEllipse = Math.Pow((mx - 100) / 100, 2) + Math.Pow((my - 50) / 50, 2);
            Assert.InRange(onEllipse, 0.999, 1.001);
        }

        // Past a turn, one turn: four quarters, ending where it began.
        VectorDrawing twice = XfaLayout.ArcDrawing(100, 100, 0, 720, edge);
        Assert.Equal(5, twice.Path.Count);
        Assert.Equal((100.0, 50.0), End(twice));

        var hidden = new XfaBorder { Visible = false, Thickness = 1, Color = "#000000", Fill = "#00FF00" };
        VectorDrawing filled = XfaLayout.ArcDrawing(100, 100, 0, 90, hidden);
        Assert.Null(filled.StrokeColor);
        Assert.Equal("#00FF00", filled.FillColor);
    }

    /// <summary>
    /// <c>rotate</c> turns a positioned container counter-clockwise about its anchor point (chapter 2,
    /// "Rotating Containers"): a 2 in × 1 in frame anchored at its top-left, turned 90°, stands upright
    /// above the anchor — its top edge runs up the anchor's x, its bottom edge 1 in to the right. A sloped
    /// line inside a turned draw is turned with it; a turned subform turns what it holds about its own
    /// anchor.
    /// </summary>
    [Fact]
    public void Rotate_TurnsAPositionedContainerAboutItsAnchor()
    {
        string frame = "<draw name=\"f\" x=\"2in\" y=\"3in\" w=\"2in\" h=\"1in\" rotate=\"90\"><border><edge thickness=\"1pt\"/></border><value><text/></value></draw>";
        string line = "<draw name=\"l\" x=\"2in\" y=\"6in\" w=\"2in\" h=\"1in\" rotate=\"90\"><value><line><edge thickness=\"1pt\"/></line></value></draw>";
        byte[] pdf = Export(Template(Page(), Block(frame + line)), null, out XfaLayoutResult result);
        var turned = TurnedSegments(PdfContentProbe.DecodeStreams(pdf));
        string drawn = string.Join(" ", turned.Select(s => $"({s.X0:0.#},{s.Y0:0.#})→({s.X1:0.#},{s.Y1:0.#})"));

        // The anchor is (18 + 144, 792 − 18 − 216) = (162, 558).
        static bool Vertical(TurnedSegment s, double x, double y0, double y1) =>
            Near(s.X0, x) && Near(s.X1, x) && Near(Math.Min(s.Y0, s.Y1), y0) && Near(Math.Max(s.Y0, s.Y1), y1);
        Assert.True(turned.Any(s => Vertical(s, 162, 558, 702)), "no turned top edge up the anchor's x; turned: " + drawn);
        Assert.True(turned.Any(s => Vertical(s, 234, 558, 702)), "no turned bottom edge 72 pt right; turned: " + drawn);
        // The line's diagonal from its anchor (162, 342), (144, −72) turned a quarter: (72, 144).
        Assert.True(turned.Any(s => (Near(s.X0, 162) && Near(s.Y0, 342) && Near(s.X1, 234) && Near(s.Y1, 486))
                                    || (Near(s.X1, 162) && Near(s.Y1, 342) && Near(s.X0, 234) && Near(s.Y0, 486))),
            "no turned diagonal; turned: " + drawn);
        Assert.DoesNotContain(result.Approximations, a => a.Contains("rotat", StringComparison.Ordinal));

        // A subform turned half a turn turns its draw about the subform's anchor.
        string sub = "<subform x=\"3in\" y=\"2in\" w=\"2in\" h=\"1in\" rotate=\"180\">"
                     + "<draw name=\"in\" x=\"0.5in\" y=\"0.25in\" w=\"1in\" h=\"0.5in\"><border/><value><text/></value></draw></subform>";
        var half = XfaLayout.Layout(Parse(Template(Page(), Block(sub))), null);
        Paragraph inner = half.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.Transform != null);
        Assert.Equal(new[] { -1.0, 0, 0, -1 }, inner.Transform!.Take(4).Select(v => Math.Round(v, 9)));
        // The subform's anchor (216, 144) is the turn's fixed point, the draw 36 pt right and 18 pt below it.
        Assert.Equal(252, inner.Placement!.LeftPt, 3);
        Assert.Equal(162, inner.Placement.TopPt, 3);
        var (fx, fy) = Apply(inner.Transform, 216 - inner.Placement.LeftPt, -(144 - inner.Placement.TopPt));
        Assert.Equal(216 - inner.Placement.LeftPt, fx, 6);
        Assert.Equal(-(144 - inner.Placement.TopPt), fy, 6);

        // A turn below zero or past a whole one is the same quarter: −90 is 270.
        string back = "<draw name=\"b\" x=\"1in\" y=\"1in\" w=\"1in\" h=\"1in\" rotate=\"-90\"><border/><value><text/></value></draw>";
        var backResult = XfaLayout.Layout(Parse(Template(Page(), Block(back))), null);
        Paragraph backFrame = backResult.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.Transform != null);
        Assert.Equal(new[] { 0.0, -1, 1, 0 }, backFrame.Transform!.Take(4).Select(v => Math.Round(v, 9)));
    }

    /// <summary>
    /// <c>x</c> and <c>y</c> place a container's anchor point (<c>anchorType</c>): a middle-centre
    /// anchor puts the extent's top-left half its width left and half its height up; bottom-right a whole
    /// width and height; top-left, the default, where it was. A rotation that is not a quarter turn, and a
    /// rotated container in a flow, are reported.
    /// </summary>
    [Fact]
    public void AnchorType_PlacesTheExtentFromItsAnchor()
    {
        string Box(string name, string anchor) =>
            $"<draw name=\"{name}\" x=\"3in\" y=\"8in\" w=\"2in\" h=\"1in\"{anchor}><border/><value><text/></value></draw>";
        foreach (var (anchor, left, top) in new[] { ("", 216.0, 576.0), (" anchorType=\"middleCenter\"", 144.0, 540.0), (" anchorType=\"bottomRight\"", 72.0, 504.0) })
        {
            var result = XfaLayout.Layout(Parse(Template(Page(), Block(Box("b", anchor)))), null);
            Paragraph frame = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.BorderTop != null);
            Assert.Equal(left, frame.Placement!.LeftPt, 3);
            Assert.Equal(top, frame.Placement.TopPt, 3);
            Assert.Empty(result.Approximations);
        }

        // A subform's own anchor moves what it holds: its draw at (0, 0) lands at the subform's top-left.
        string sub = "<subform x=\"3in\" y=\"8in\" w=\"2in\" h=\"1in\" anchorType=\"bottomRight\">"
                     + "<draw name=\"in\" w=\"1in\" h=\"0.5in\"><border/><value><text/></value></draw></subform>";
        var subResult = XfaLayout.Layout(Parse(Template(Page(), Block(sub))), null);
        Paragraph held = subResult.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.BorderTop != null);
        Assert.Equal(72, held.Placement!.LeftPt, 3);
        Assert.Equal(504, held.Placement.TopPt, 3);

        // An anchor needs the size it is measured into: without one it is the top-left, reported.
        string unsized = "<draw name=\"u\" x=\"3in\" y=\"8in\" anchorType=\"middleCenter\"><border/><value><text>U</text></value></draw>";
        var unsizedResult = XfaLayout.Layout(Parse(Template(Page(), Block(unsized))), null);
        Assert.Contains(unsizedResult.Approximations, a => a.StartsWith("an anchor point other than the top-left", StringComparison.Ordinal));

        string odd = "<draw name=\"o\" x=\"1in\" y=\"1in\" w=\"1in\" h=\"1in\" rotate=\"45\"><border/><value><text/></value></draw>";
        var oddResult = XfaLayout.Layout(Parse(Template(Page(), Block(odd))), null);
        Assert.Contains(oddResult.Approximations, a => a.StartsWith("a rotation that is not a multiple of 90", StringComparison.Ordinal));
        Assert.DoesNotContain(oddResult.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>(), p => p.Transform != null);

        // A turned draw in a flow is laid out along its turned axes (TurnedInFlow_TakesItsTurnedExtent), and
        // a turned flowing subform too (TurnedFlowingSubform_IsLaidOutTurned).
        string flowed = "<subform layout=\"tb\" w=\"576pt\"><draw name=\"r\" w=\"1in\" h=\"1in\" rotate=\"90\"><value><text>R</text></value></draw></subform>";
        Assert.DoesNotContain(XfaLayout.Layout(Parse(Template(Page(), flowed)), null).Approximations, a => a.Contains("rotated", StringComparison.Ordinal));
        // A flowing subform of no height has no extent to turn: unturned, said.
        string turnedFlow = "<subform layout=\"tb\" w=\"576pt\"><subform layout=\"tb\" w=\"1in\" rotate=\"90\">" + Draw("t", "0", "0", "1in", "0.3in", "T") + "</subform></subform>";
        Assert.Contains(XfaLayout.Layout(Parse(Template(Page(), turnedFlow)), null).Approximations, a => a.StartsWith("a rotated container in a flow", StringComparison.Ordinal));
        string whole = flowed.Replace("rotate=\"90\"", "rotate=\"360\"");
        Assert.Empty(XfaLayout.Layout(Parse(Template(Page(), whole)), null).Approximations);
    }

    /// <summary>
    /// A container turned in a flow takes its turned extent (XFA 3.3 chapter 8, "Rotating Containers"): a
    /// 2 in × 1 in framed draw turned a quarter either way stands 1 in wide and 2 in tall in its box, the
    /// frame's edges drawn exactly round that box — and the text after it starts below the 2 in; turned a
    /// half, it keeps its 2 in × 1 in. Each turn's edges are read off the page through the CTM.
    /// </summary>
    [Fact]
    public void TurnedInFlow_TakesItsTurnedExtent()
    {
        foreach (var (rotate, wide, tall) in new[] { ("90", 72.0, 144.0), ("270", 72.0, 144.0), ("180", 144.0, 72.0) })
        {
            string body = $"<subform layout=\"tb\" w=\"576pt\"><draw name=\"r\" w=\"2in\" h=\"1in\" rotate=\"{rotate}\"><border/><value><text/></value></draw>"
                          + Draw("after", "0", "0", "2in", "0.3in", "AFTER") + "</subform>";
            byte[] pdf = Export(Template(Page(), body), null, out XfaLayoutResult result);
            Assert.DoesNotContain(result.Approximations, a => a.Contains("rotat", StringComparison.Ordinal));
            BoxStart turnedBox = result.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().Single(b => b.KeepTogether && b.WidthPt > 0 && b.MinHeightPt > 0);
            Assert.Equal((wide, tall), (turnedBox.WidthPt, turnedBox.MinHeightPt));

            var edges = TurnedSegments(PdfContentProbe.DecodeStreams(pdf));
            Assert.Equal(4, edges.Count);
            double left = edges.Min(s => Math.Min(s.X0, s.X1)), right = edges.Max(s => Math.Max(s.X0, s.X1));
            double low = edges.Min(s => Math.Min(s.Y0, s.Y1)), high = edges.Max(s => Math.Max(s.Y0, s.Y1));
            Assert.Equal(18, left, 0);                                          // the content area's left
            Assert.Equal(wide, right - left, 0);
            Assert.Equal(792 - 18, high, 0);                                    // the content area's top
            Assert.Equal(tall, high - low, 0);
            Assert.True(PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(pdf), "AFTER") < low, "the next draw starts below the turned box");
        }
    }

    /// <summary>
    /// A flowing subform turned in a flow (XFA 3.3 chapter 8, "Rotating Containers"): its two draws, one
    /// under the other in its own flow, are turned with it — a quarter turn counter-clockwise puts the
    /// second to the right of the first on the page, three quarters to its left, at one height — and its
    /// 2 in × 1 in extent stands 1 in wide and 2 in tall at the content area's corner, the text after it
    /// below. Unturned, the control, the second is under the first. A turned flowing subform holding a
    /// positioned container is laid out unturned, said.
    /// </summary>
    [Fact]
    public void TurnedFlowingSubform_IsLaidOutTurned()
    {
        string Body(string rotate, string inner = "") =>
            $"<subform layout=\"tb\" w=\"576pt\"><subform layout=\"tb\" w=\"2in\" h=\"1in\" rotate=\"{rotate}\">"
            + Draw("a", "0", "0", "2in", "0.3in", "FIRSTX") + Draw("b", "0", "0", "2in", "0.3in", "SECONDX") + inner + "</subform>"
            + Draw("after", "0", "0", "2in", "0.3in", "AFTERX") + "</subform>";
        (double X, double Y) At(byte[] pdf, string text) { var t = PdfContentProbe.DrawnTextsOnPage(pdf).First(d => d.Text == text); return (t.X, t.Y); }

        byte[] ccw = Export(Template(Page(), Body("90")), null, out XfaLayoutResult turned);
        Assert.DoesNotContain(turned.Approximations, a => a.Contains("rotat", StringComparison.Ordinal) || a.Contains("turned", StringComparison.Ordinal));
        var (f, s) = (At(ccw, "FIRSTX"), At(ccw, "SECONDX"));
        Assert.Equal(f.Y, s.Y, 1);
        Assert.True(s.X - f.X > 15, $"FIRSTX at x {f.X:F1}, SECONDX at {s.X:F1}");
        Assert.InRange(f.X, 18, 18 + 72);
        Assert.InRange(s.X, 18, 18 + 72);
        // Turned a quarter counter-clockwise, a line starts at the extent's bottom and runs up.
        Assert.Equal(792 - 18 - 144, f.Y, 1);
        Assert.True(At(ccw, "AFTERX").Y < 792 - 18 - 144 + 1, "the text after does not start below the turned extent");

        byte[] cw = Export(Template(Page(), Body("270")), null, out _);
        Assert.True(At(cw, "FIRSTX").X - At(cw, "SECONDX").X > 15, "three quarters does not put the second to the left");

        byte[] plain = Export(Template(Page(), Body("0")), null, out _);
        Assert.True(At(plain, "FIRSTX").Y - At(plain, "SECONDX").Y > 15, "unturned, the second is not under the first");

        // A positioned container in it is turned with the rest, not sent back unturned: its
        // draw, placed in the container's own box, comes third along the turned flow, at the first two's height.
        string positioned = "<subform layout=\"position\" w=\"1in\" h=\"0.3in\">" + Draw("p", "0", "0", "1in", "0.3in", "PLACEDX") + "</subform>";
        byte[] held = Export(Template(Page(), Body("90", positioned)), null, out XfaLayoutResult heldResult);
        Assert.DoesNotContain(heldResult.Approximations, a => a.Contains("turned", StringComparison.Ordinal));
        Assert.Equal(At(held, "FIRSTX").Y, At(held, "PLACEDX").Y, 1);
        Assert.True(At(held, "PLACEDX").X - At(held, "SECONDX").X > 15, $"SECONDX at x {At(held, "SECONDX").X:F1}, PLACEDX at {At(held, "PLACEDX").X:F1}");

        // And a line of an lr-tb flow in it: turned a quarter counter-clockwise, its left-to-right runs bottom to top —
        // the second draw above the first, at one x.
        string line = "<subform layout=\"lr-tb\" w=\"2in\">" + Draw("l", "0", "0", "0.8in", "0.3in", "LEFTX") + Draw("r", "0", "0", "0.8in", "0.3in", "RIGHTX") + "</subform>";
        byte[] lined = Export(Template(Page(), Body("90", line)), null, out XfaLayoutResult linedResult);
        Assert.DoesNotContain(linedResult.Approximations, a => a.Contains("turned", StringComparison.Ordinal) || a.Contains("top to bottom", StringComparison.Ordinal));
        Assert.Equal(At(lined, "LEFTX").X, At(lined, "RIGHTX").X, 1);
        Assert.True(At(lined, "RIGHTX").Y - At(lined, "LEFTX").Y > 50, $"LEFTX at y {At(lined, "LEFTX").Y:F1}, RIGHTX at {At(lined, "RIGHTX").Y:F1}");

        // A turned draw with an anchor point other than its top-left is turned in the flow: a flowed
        // child's anchor point is ignored, so it lands where a top-left one does.
        string Single(string anchor) => "<subform layout=\"tb\" w=\"576pt\">"
            + Draw("t", "0", "0", "2in", "0.3in", "TURNEDX").Replace("<draw ", $"<draw rotate=\"90\" {anchor}") + "</subform>";
        byte[] anchored = Export(Template(Page(), Single("anchorType=\"bottomRight\" ")), null, out XfaLayoutResult anchoredResult);
        Assert.DoesNotContain(anchoredResult.Approximations, a => a.Contains("rotat", StringComparison.Ordinal));
        byte[] topLeft = Export(Template(Page(), Single("")), null, out _);
        Assert.Equal(At(topLeft, "TURNEDX"), At(anchored, "TURNEDX"));
    }

    /// <summary>
    /// <c>para vAlign</c> aligns a text in the part of its box it is set in: a positioned field's value
    /// in its value region (the 1 pt inset each side kept), a left caption in the content height, a
    /// draw's text in its content region; a table cell takes the alignment itself; a flowed box aligns
    /// its blocks in its height; a box its text sizes has nothing to align; a flowed field whose caption
    /// stands above or below it is two bands; a positioned field of no height with a caption beside it a
    /// placed row of two aligned cells.
    /// </summary>
    [Fact]
    public void VAlign_SetsTheTextInItsBand()
    {
        string field = "<field name=\"f\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"1in\"><ui><textEdit/></ui>"
                       + "<caption reserve=\"1in\"><para vAlign=\"bottom\"/><value><text>CAP</text></value></caption>"
                       + "<value><text>VAL</text></value><para vAlign=\"middle\"/></field>";
        string draw = "<draw name=\"d\" x=\"1in\" y=\"3in\" w=\"2in\" h=\"40pt\"><margin topInset=\"5pt\" bottomInset=\"5pt\"/>"
                      + "<value><text>DRAW</text></value><para vAlign=\"bottom\"/></draw>";
        var result = XfaLayout.Layout(Parse(Template(Page(), Block(field + draw))), null);
        var placed = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Where(p => p.Placement != null).ToList();
        AbsolutePlacement Of(string text) =>
            placed.Single(p => p.Runs.OfType<TextRun>().Any(r => r.Text == text)).Placement!;

        Assert.Equal(70, Of("VAL").BandHeightPt!.Value, 3);
        Assert.Equal(CellVerticalAlign.Center, Of("VAL").BandAlign);
        Assert.Equal(72, Of("CAP").BandHeightPt!.Value, 3);
        Assert.Equal(CellVerticalAlign.Bottom, Of("CAP").BandAlign);
        Assert.Equal(30, Of("DRAW").BandHeightPt!.Value, 3);
        Assert.Equal(CellVerticalAlign.Bottom, Of("DRAW").BandAlign);
        Assert.DoesNotContain(result.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));

        string table = "<subform layout=\"table\" w=\"4in\"><subform layout=\"row\">"
                       + "<draw name=\"c\" w=\"2in\" h=\"30pt\"><value><text>CELL</text></value><para vAlign=\"middle\"/></draw></subform></subform>";
        var tableResult = XfaLayout.Layout(Parse(Template(Page(), "<subform layout=\"tb\" w=\"576pt\">" + table + "</subform>")), null);
        TableCell cell = tableResult.Document.Sections.SelectMany(s => s.Children).OfType<Table>().Single().Rows[0].Cells[0];
        Assert.Equal(CellVerticalAlign.Center, cell.VAlign);
        Assert.DoesNotContain(tableResult.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));

        // A positioned draw of no given height is as tall as its text: nothing to align, nothing to report.
        var unsized = XfaLayout.Layout(Parse(Template(Page(), Block(draw.Replace(" h=\"40pt\"", "")))), null);
        Assert.DoesNotContain(unsized.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));
        Assert.DoesNotContain(unsized.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>(), p => p.Placement?.BandHeightPt != null);
        Assert.Contains(unsized.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>(),
                        p => p.Runs.OfType<TextRun>().Any(r => r.Text == "DRAW"));
        // So is a positioned field of no given height with no caption. One with a caption beside it is as
        // tall as its taller part, each part aligned in that height: a placed table of one row, the
        // caption's cell its reserve wide on its side, each cell aligned by its own para.
        string unsizedField = "<field name=\"u\" x=\"1in\" y=\"5in\" w=\"3in\"><ui><textEdit/></ui>"
                              + "<value><text>UVAL</text></value><para vAlign=\"middle\"/></field>";
        var unsizedValue = XfaLayout.Layout(Parse(Template(Page(), Block(unsizedField))), null);
        Assert.DoesNotContain(unsizedValue.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));
        Assert.Contains(unsizedValue.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>(),
                        p => p.Runs.OfType<TextRun>().Any(r => r.Text == "UVAL"));
        string captioned = unsizedField.Replace("<value>", "<caption reserve=\"1in\"><para vAlign=\"bottom\"/><value><text>UCAP</text></value></caption><value>");
        foreach (string placement in new[] { "left", "right" })
        {
            var unsizedCaptioned = XfaLayout.Layout(Parse(Template(Page(), Block(captioned.Replace("<caption ", $"<caption placement=\"{placement}\" ")))), null);
            Assert.DoesNotContain(unsizedCaptioned.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));
            Paragraph anchor = unsizedCaptioned.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>()
                .Single(p => p.Placement?.FollowingBlocks.Count > 0);
            Assert.Equal((72.0, 360.0), (anchor.Placement!.LeftPt, anchor.Placement.TopPt));
            Table row = Assert.IsType<Table>(Assert.Single(anchor.Placement.FollowingBlocks));
            TableCell capCell = row.Rows[0].Cells.Single(c => c.Paragraphs.Any(p => p.Runs.OfType<TextRun>().Any(r => r.Text == "UCAP")));
            TableCell valCell = row.Rows[0].Cells.Single(c => c != capCell);
            Assert.Equal((CellVerticalAlign.Bottom, CellVerticalAlign.Center), (capCell.VAlign, valCell.VAlign));
            int capAt = row.Rows[0].Cells.IndexOf(capCell);
            Assert.Equal(placement == "left" ? 0 : 1, capAt);
            Assert.Equal(72, row.ColumnWidths[capAt], 3);
            Assert.Equal(144, row.ColumnWidths[1 - capAt], 3);
        }
        // With every part at the top, nothing aligns: the parts are placed as before.
        var topOnly = XfaLayout.Layout(Parse(Template(Page(), Block(captioned.Replace(" vAlign=\"bottom\"", "").Replace("<para vAlign=\"middle\"/>", "")))), null);
        Assert.DoesNotContain(topOnly.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>(), p => p.Placement?.FollowingBlocks.Count > 0);
        // A caption above or below an unsized field is a row of its own, at least its reserve tall, so it
        // follows the value whatever the value's height — aligned or not.
        foreach (string placement in new[] { "top", "bottom" })
        {
            string stacked = captioned.Replace("<caption reserve=\"1in\">", $"<caption placement=\"{placement}\" reserve=\"20pt\">")
                                      .Replace(" vAlign=\"bottom\"", "").Replace("<para vAlign=\"middle\"/>", "");
            var stackedResult = XfaLayout.Layout(Parse(Template(Page(), Block(stacked))), null);
            Assert.DoesNotContain(stackedResult.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));
            Table rows = Assert.IsType<Table>(Assert.Single(stackedResult.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>()
                .Single(p => p.Placement?.FollowingBlocks.Count > 0).Placement!.FollowingBlocks));
            Assert.Equal(2, rows.Rows.Count);
            int captionRow = rows.Rows.FindIndex(r => r.Cells[0].Paragraphs.Any(p => p.Runs.OfType<TextRun>().Any(t => t.Text == "UCAP")));
            Assert.Equal(placement == "top" ? 0 : 1, captionRow);
            Assert.Equal(20, rows.Rows[captionRow].HeightPt);
            Assert.Null(rows.Rows[1 - captionRow].HeightPt);
            Assert.Equal(216, Assert.Single(rows.ColumnWidths), 3);
        }

        // A flowed draw's box aligns its text in the box's height (BoxStart.ContentAlign).
        string flowed = "<subform layout=\"tb\" w=\"576pt\">" + draw.Replace(" x=\"1in\" y=\"3in\"", "") + "</subform>";
        var flowResult = XfaLayout.Layout(Parse(Template(Page(), flowed)), null);
        Assert.DoesNotContain(flowResult.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));
        BoxStart drawBox = flowResult.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().Single(b => b.MinHeightPt == 40);
        Assert.Equal(CellVerticalAlign.Bottom, drawBox.ContentAlign);

        // A flowed field whose caption shares its paragraph aligns as one; one whose caption is above or
        // below it is two bands, the caption's its reserve tall, the value's the rest, each aligned.
        string inlineField = "<subform layout=\"tb\" w=\"576pt\"><field name=\"g\" w=\"3in\" h=\"1in\"><ui><textEdit/></ui>"
                             + "<caption><value><text>CAP</text></value></caption><value><text>VAL</text></value>"
                             + "<para vAlign=\"middle\"/></field></subform>";
        var inlineResult = XfaLayout.Layout(Parse(Template(Page(), inlineField)), null);
        Assert.DoesNotContain(inlineResult.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));
        Assert.Equal(CellVerticalAlign.Center,
                     inlineResult.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().Single(b => b.MinHeightPt == 72).ContentAlign);
        foreach (string placement in new[] { "top", "bottom" })
        {
            string bordered = inlineField.Replace("<ui><textEdit/></ui>", "<ui><textEdit/></ui><border><edge thickness=\"2pt\"/></border>");
            var split = XfaLayout.Layout(Parse(Template(Page(), bordered.Replace("<caption>", $"<caption placement=\"{placement}\"><para vAlign=\"bottom\"/>"))), null);
            Assert.DoesNotContain(split.Approximations, a => a.Contains("vAlign", StringComparison.Ordinal));
            var blocks = split.Document.Sections.SelectMany(s => s.Children).ToList();
            BoxStart fieldBox = blocks.OfType<BoxStart>().Single(b => b.MinHeightPt == 72);
            Assert.Equal(CellVerticalAlign.Top, fieldBox.ContentAlign);
            // The field's 2 pt border each side leaves 68 pt; the caption's default reserve, a line of the
            // default 10 pt font, is 13 pt of it.
            BoxStart BandOf(string text) => (BoxStart)blocks[blocks.FindIndex(b => b is Paragraph p && p.Runs.OfType<TextRun>().Any(r => r.Text == text)) - 1];
            Assert.Equal((13.0, CellVerticalAlign.Bottom), (BandOf("CAP").MinHeightPt, BandOf("CAP").ContentAlign));
            Assert.Equal(55, BandOf("VAL").MinHeightPt, 6);
            Assert.Equal(CellVerticalAlign.Center, BandOf("VAL").ContentAlign);
            Assert.Equal(placement == "top", blocks.IndexOf(BandOf("CAP")) < blocks.IndexOf(BandOf("VAL")));
        }
        // A field of no given height: the value's band is its text's, the caption's still its reserve.
        var growing = XfaLayout.Layout(Parse(Template(Page(), inlineField.Replace(" h=\"1in\"", "").Replace("<caption>", "<caption placement=\"top\" reserve=\"20pt\">"))), null);
        var grown = growing.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().ToList();
        Assert.Contains(grown, b => b.MinHeightPt == 20);
        Assert.Contains(grown, b => b.MinHeightPt == 0 && b.ContentAlign == CellVerticalAlign.Center);
        // A reserve past the field's height is held to it, and the value has none left.
        var squeezed = XfaLayout.Layout(Parse(Template(Page(), inlineField.Replace(" h=\"1in\"", " h=\"20pt\"").Replace("<caption>", "<caption placement=\"top\" reserve=\"30pt\">"))), null);
        var squeezedBoxes = squeezed.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().Select(b => b.MinHeightPt).ToList();
        Assert.Equal(new[] { 0.0, 20.0, 20.0, 0.0 }, squeezedBoxes);      // the flow's box, the field's, the caption's, the value's
    }

    private static (double X, double Y) Apply(double[] m, double x, double y) => (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]);

    private readonly record struct TurnedSegment(double X0, double Y0, double X1, double Y1);

    private static bool Near(double a, double b) => Math.Abs(a - b) < 2;

    /// <summary>
    /// Every <c>m … l … S</c> drawn under a <c>cm</c>, through the matrix in force (<c>q</c>/<c>Q</c>
    /// nesting followed) — how a turned rule reaches the page. Text objects are skipped whole.
    /// </summary>
    private static List<TurnedSegment> TurnedSegments(string content)
    {
        var list = new List<TurnedSegment>();
        var stack = new Stack<double[]>();
        double[] ctm = { 1, 0, 0, 1, 0, 0 };
        var operands = new List<double>();
        double mx = 0, my = 0, lx = 0, ly = 0;
        bool inText = false, haveLine = false;
        foreach (string token in Regex.Split(content, @"\s+"))
        {
            if (token.Length == 0) continue;
            if (token == "BT") inText = true;
            else if (token == "ET") inText = false;
            if (inText) { operands.Clear(); continue; }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) { operands.Add(number); continue; }
            switch (token)
            {
                case "q": stack.Push(ctm); break;
                case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                case "cm" when operands.Count == 6:
                    double[] k = operands.ToArray();
                    ctm = new[]
                    {
                        k[0] * ctm[0] + k[1] * ctm[2], k[0] * ctm[1] + k[1] * ctm[3],
                        k[2] * ctm[0] + k[3] * ctm[2], k[2] * ctm[1] + k[3] * ctm[3],
                        k[4] * ctm[0] + k[5] * ctm[2] + ctm[4], k[4] * ctm[1] + k[5] * ctm[3] + ctm[5],
                    };
                    break;
                case "m" when operands.Count == 2: mx = operands[0]; my = operands[1]; haveLine = false; break;
                case "l" when operands.Count == 2: lx = operands[0]; ly = operands[1]; haveLine = true; break;
                case "S":
                    bool identity = ctm[0] == 1 && ctm[1] == 0 && ctm[2] == 0 && ctm[3] == 1 && ctm[4] == 0 && ctm[5] == 0;
                    if (haveLine && !identity)
                        list.Add(new TurnedSegment(ctm[0] * mx + ctm[2] * my + ctm[4], ctm[1] * mx + ctm[3] * my + ctm[5],
                                                   ctm[0] * lx + ctm[2] * ly + ctm[4], ctm[1] * lx + ctm[3] * ly + ctm[5]));
                    haveLine = false;
                    break;
            }
            operands.Clear();
        }
        return list;
    }

    // ---------------------------------------------------------------- approximations and hostility

    [Fact]
    public void WhatIsNotDone_IsReported_AndWhatIsDoneIsNot()
    {
        // A child turned by other than quarter turns cannot be placed in a line: the flow is laid out top to bottom, reported.
        string lr = "<subform layout=\"lr-tb\" w=\"8in\">" + Draw("a", "0", "0", "1in", "0.3in", "A").Replace("<draw ", "<draw rotate=\"45\" ")
                    + Draw("b", "0", "0", "1in", "0.3in", "B") + "</subform>";
        string scripted = Field("s", "0", "1in", "2in", "0.3in", extra: "<event activity=\"click\"><script>app.alert(1)</script></event>");
        string proto = Draw("p", "0", "2in", "1in", "0.3in", "P").Replace("<draw ", "<draw use=\"#proto1\" ");
        var result = XfaLayout.Layout(Parse(Template(Page(), lr + Block(scripted + proto))), null);
        Assert.Contains(result.Approximations, a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
        Assert.Contains(result.Approximations, a => a.StartsWith("a script", StringComparison.Ordinal));
        Assert.Contains(result.Approximations, a => a.StartsWith("a use/usehref prototype", StringComparison.Ordinal));

        var clean = XfaLayout.Layout(Parse(Template(Page(), Block(Draw("a", "0", "0", "1in", "0.3in", "A")))), null);
        Assert.Empty(clean.Approximations);

        // A left-to-right flow of full-width sections is one per line either way: nothing to report.
        string sections = "<subform layout=\"lr-tb\" w=\"8in\">" + Draw("a", "0", "0", "8in", "0.3in", "A") + Draw("b", "0", "0", "5in", "0.3in", "B")
                          + Draw("c", "0", "0", "4in", "0.3in", "C") + "</subform>";
        Assert.Empty(XfaLayout.Layout(Parse(Template(Page(), sections)), null).Approximations);
        string unsized = sections.Replace("w=\"4in\"", "");
        Assert.Contains(XfaLayout.Layout(Parse(Template(Page(), unsized)), null).Approximations,
            a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));

        bool Reported(string inner, string width = " w=\"8in\"") =>
            XfaLayout.Layout(Parse(Template(Page(), $"<subform layout=\"lr-tb\"{width}>" + inner + "</subform>")), null)
                .Approximations.Any(a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
        // A grown subform holding a child of no width has no width to judge or place by.
        Assert.True(Reported("<subform>" + Draw("u", "0", "0", "1in", "0.3in", "U").Replace(" w=\"1in\"", "") + "</subform>" + Draw("n", "0", "0", "1in", "0.3in", "N")));
        // A flow of no given width is unbounded: two children share a line, and with no width to wrap
        // at it cannot be lined; one child shares nothing.
        Assert.True(Reported(Draw("a", "0", "0", "8in", "0.3in", "A") + Draw("b", "0", "0", "8in", "0.3in", "B"), ""));
        Assert.False(Reported(Draw("a", "0", "0", "8in", "0.3in", "A"), ""));
        Assert.False(Reported(Draw("a", "0", "0", "8in", "0.3in", "A") + Draw("b", "0", "0", "8in", "0.3in", "B")));
    }

    /// <summary>
    /// A left-to-right flow whose children share a line is laid out in lines (XFA 3.3 chapter 8): each
    /// line a box as tall as its tallest child, its children placed at their running offsets — from the
    /// right under <c>rl-tb</c> — wrapping where the next does not fit; a positioned subform of no width
    /// as wide as its content reaches; a repeating subform's instances one after another. A flow whose
    /// children are one per line either way stays top to bottom.
    /// </summary>
    [Fact]
    public void LeftToRightFlow_LaysItsChildrenOutInLines()
    {
        // (text, left, the line's box) for every placed text, in document order.
        List<(string Text, double Left, BoxStart? Line, double Top)> Placed(string inner, string layout = "lr-tb", XElement? data = null, string margin = "")
        {
            var result = XfaLayout.Layout(Parse(Template(Page(), $"<subform layout=\"{layout}\" w=\"8in\">{margin}" + inner + "</subform>")), data);
            Assert.DoesNotContain(result.Approximations, a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
            return result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>()
                .Where(p => p.Placement != null && p.Runs.OfType<TextRun>().Any(r => r.Text.Trim().Length > 0))
                .Select(p => (string.Concat(p.Runs.OfType<TextRun>().Select(r => r.Text)), p.Placement!.LeftPt, p.Placement.ContainingBox, p.Placement.TopPt))
                .ToList();
        }
        string D(string text, string w, string h = "0.3in") => Draw(text.ToLowerInvariant(), "0", "0", w, h, text);

        var three = Placed(Draw("a", "1in", "1in", "3in", "0.3in", "A") + D("B", "3in", "0.5in") + D("C", "3in"));
        Assert.Equal(0, three[0].Top, 3);                                   // its own x and y are not its place
        Assert.Equal(new[] { ("A", 0.0), ("B", 216.0), ("C", 0.0) }, three.Select(t => (t.Text, Math.Round(t.Left, 3))));
        Assert.Same(three[0].Line, three[1].Line);
        Assert.NotSame(three[1].Line, three[2].Line);
        Assert.Equal(36, three[0].Line!.MinHeightPt, 3);                   // the taller of the line's two
        Assert.Equal(21.6, three[2].Line!.MinHeightPt, 3);

        var mirrored = Placed(D("A", "3in") + D("B", "3in") + D("C", "3in"), "rl-tb");
        Assert.Equal(new[] { ("A", 360.0), ("B", 144.0), ("C", 360.0) }, mirrored.Select(t => (t.Text, Math.Round(t.Left, 3))));

        // 5 in, then 4 in on a line of its own, and 3 in beside it.
        var restart = Placed(D("A", "5in") + D("B", "4in") + D("C", "3in"));
        Assert.Equal(new[] { 0.0, 0.0, 288.0 }, restart.Select(t => Math.Round(t.Left, 3)));
        Assert.Same(restart[1].Line, restart[2].Line);

        // A positioned subform of no width reaches 1 in + 6 in: the 1 in draw fits beside it at 7 in.
        string grown = "<subform>" + Draw("g", "1in", "0.2in", "6in", "0.3in", "G") + "</subform>" + D("N", "1in");
        var grownLine = Placed(grown);
        Assert.Equal(new[] { ("G", 72.0), ("N", 504.0) }, grownLine.Select(t => (t.Text, Math.Round(t.Left, 3))));
        Assert.Equal(36, grownLine[0].Line!.MinHeightPt, 3);              // its child reaches 0.2 in + 0.3 in down
        // At 1 in + 7 in it fills the line, so the flow is one child per line and stays top to bottom.
        var full = XfaLayout.Layout(Parse(Template(Page(), "<subform layout=\"lr-tb\" w=\"8in\">" + grown.Replace("w=\"6in\"", "w=\"7in\"") + "</subform>")), null);
        Assert.DoesNotContain(full.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>(),
            p => p.Placement != null && p.Runs.OfType<TextRun>().Any(r => r.Text == "N"));

        // A repeating subform's instances are laid one after another: three 3 in instances, two per line.
        string item = "<subform name=\"item\" w=\"3in\" h=\"0.3in\"><occur min=\"1\" max=\"-1\"/>" + Field("q", "0", "0", "3in", "0.3in") + "</subform>";
        var instances = Placed(item, data: Data("<form1><item><q>one</q></item><item><q>two</q></item><item><q>three</q></item></form1>"));
        Assert.Equal(new[] { "one", "two", "three" }, instances.Select(t => t.Text));
        Assert.Equal(new[] { 0.0, 216.0, 0.0 }, instances.Select(t => Math.Round(t.Left - instances[0].Left, 3)));

        // The flow's margins narrow its lines: two 3.5 in draws share an 8 in line, not a 6 in one.
        var margined = Placed(D("A", "3.5in") + D("B", "3.5in"), margin: "<margin leftInset=\"1in\" rightInset=\"1in\"/>");
        Assert.NotSame(margined[0].Line, margined[1].Line);
        var wide = Placed(D("A", "3.5in") + D("B", "3.5in"));
        Assert.Same(wide[0].Line, wide[1].Line);

        // A quarter-turned child stands in its line at its turned extent: B, 3 in by 0.3 in, turned, is 0.3 in
        // wide and 3 in tall beside A, and its line is that tall; turned a
        // quarter counter-clockwise, its top-left corner goes 3 in down its 0.3 in column. An anchored child is placed by
        // its top-left corner, as a flowed child's anchor point is ignored.
        var turned = Placed(D("A", "3in") + D("B", "3in").Replace("<draw ", "<draw rotate=\"90\" ") + D("C", "1in").Replace("<draw ", "<draw anchorType=\"bottomRight\" "));
        Assert.Same(turned[0].Line, turned[1].Line);
        Assert.Same(turned[1].Line, turned[2].Line);
        Assert.Equal(216, turned[0].Line!.MinHeightPt, 3);
        Assert.Equal(new[] { ("A", 0.0, 0.0), ("B", 216.0, 216.0), ("C", 237.6, 0.0) }, turned.Select(t => (t.Text, Math.Round(t.Left, 3), Math.Round(t.Top, 3))));
        // A turned flowing subform needs a box of its own, which a line is not: still laid out top to bottom, reported.
        var turnedFlow = XfaLayout.Layout(Parse(Template(Page(), "<subform layout=\"lr-tb\" w=\"8in\">" + D("A", "3in")
            + "<subform layout=\"tb\" w=\"2in\" h=\"1in\" rotate=\"90\">" + D("F", "2in") + "</subform></subform>")), null);
        Assert.Contains(turnedFlow.Approximations, a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
    }

    /// <summary>
    /// A child whose height is not known before it is laid out — a flowing subform, a field of no
    /// given height — stands on a line alone and is laid out there as the flow lays out any child,
    /// between the lines before and after it; a nested left-to-right flow so laid out is lined in turn
    /// (another reader's <c>xfa_filled_imm1344e.pdf</c>: Sex, Date of birth and Place of birth on one line). A
    /// top-to-bottom flow of no width is its widest child and its margins wide (XFA 3.3 chapter 8,
    /// "Growable Containers"). Sharing its line, or narrower than a right-to-left line, its line is
    /// floated boxes, one a child (<see cref="LeftToRightFlow_ALineOfAChildOfUnknownHeight_IsFloatedBoxes"/>);
    /// still top to bottom, reported: one breaking to a page area.
    /// </summary>
    [Fact]
    public void LeftToRightFlow_AChildOfUnknownHeight_StandsOnALineAlone()
    {
        string D(string text, string w) => Draw(text.ToLowerInvariant(), "0", "0", w, "0.3in", text);
        XfaLayoutResult Lay(string inner, string layout = "lr-tb")
            => XfaLayout.Layout(Parse(Template(Page(), $"<subform layout=\"{layout}\" w=\"8in\">" + inner + "</subform>")), null);
        bool Reported(XfaLayoutResult r) => r.Approximations.Any(a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
        List<IDocElement> Blocks(XfaLayoutResult r) => r.Document.Sections.SelectMany(s => s.Children).ToList();
        int IndexOf(List<IDocElement> blocks, string text)
            => blocks.FindIndex(b => b is Paragraph p && p.Runs.OfType<TextRun>().Any(t => t.Text == text));
        BoxStart? LineOf(List<IDocElement> blocks, string text)
            => ((Paragraph)blocks[IndexOf(blocks, text)]).Placement?.ContainingBox;

        // A and B share a line, the flowing subform holding C stands alone, D and E share the next.
        var middle = Lay(D("A", "3in") + D("B", "3in") + "<subform layout=\"tb\" w=\"8in\">" + D("C", "8in") + "</subform>"
                         + D("D", "3in") + D("E", "3in"));
        Assert.False(Reported(middle));
        var blocks = Blocks(middle);
        Assert.Same(LineOf(blocks, "A"), LineOf(blocks, "B"));
        Assert.Same(LineOf(blocks, "D"), LineOf(blocks, "E"));
        Assert.NotSame(LineOf(blocks, "A"), LineOf(blocks, "D"));
        Assert.Null(((Paragraph)blocks[IndexOf(blocks, "C")]).Placement);   // laid out in the flow, not placed
        Assert.InRange(IndexOf(blocks, "C"), IndexOf(blocks, "B") + 1, IndexOf(blocks, "D") - 1);

        // A nested left-to-right flow alone on its line is lined in turn: P and Q side by side.
        var nested = Lay(D("A", "8in") + "<subform layout=\"lr-tb\" w=\"8in\">" + D("P", "3in") + D("Q", "3in") + "</subform>");
        Assert.False(Reported(nested));
        var inner = Blocks(nested);
        Assert.Same(LineOf(inner, "P"), LineOf(inner, "Q"));
        Assert.Equal(216, ((Paragraph)inner[IndexOf(inner, "Q")]).Placement!.LeftPt - ((Paragraph)inner[IndexOf(inner, "P")]).Placement!.LeftPt, 3);

        // A field of no given height, alone on its line.
        string tallField = Field("f", "0", "0", "8in", "0.3in", defaultValue: "FV").Replace(" h=\"0.3in\"", "");
        var field = Lay(D("A", "3in") + D("B", "3in") + tallField);
        Assert.False(Reported(field));
        Assert.NotEqual(-1, IndexOf(Blocks(field), "FV"));

        // A top-to-bottom flow of no width is its widest child and its margins: 5 in + 2 in leaves no
        // room for a 2 in draw beside it, so it stands alone, no float; 4 in + 2 in leaves exactly that,
        // so the two share a line of floats.
        string Margined(string w) => "<subform layout=\"tb\"><margin leftInset=\"1in\" rightInset=\"1in\"/>" + D("W", w) + "</subform>";
        bool Floats(XfaLayoutResult r) => Blocks(r).OfType<BoxStart>().Any(b => b.Float != FloatSide.None);
        var alone = Lay(D("A", "3in") + D("B", "3in") + Margined("5in") + D("X", "2in"));
        Assert.False(Reported(alone));
        Assert.False(Floats(alone));
        var shared = Lay(D("A", "3in") + D("B", "3in") + Margined("4in") + D("X", "2in"));
        Assert.False(Reported(shared));
        Assert.Equal(new[] { 432.0, 144.0 }, Blocks(shared).OfType<BoxStart>().Where(b => b.Float == FloatSide.Left).Select(b => b.FloatWidth));

        // One that breaks to a page area is refused, beside its control.
        string breaking = "<subform layout=\"tb\" w=\"8in\"><breakBefore targetType=\"pageArea\"/>" + D("K", "8in") + "</subform>";
        Assert.True(Reported(Lay(D("A", "3in") + D("B", "3in") + breaking)));
        Assert.True(Reported(Lay(D("A", "3in") + D("B", "3in") + breaking.Replace("breakBefore", "breakAfter"))));
        Assert.False(Reported(Lay(D("A", "3in") + D("B", "3in") + breaking.Replace("<breakBefore targetType=\"pageArea\"/>", ""))));
    }

    /// <summary>
    /// A line holding a child whose height its own layout decides: each child a box of its width floated to
    /// the line's start — left, right under <c>rl-tb</c> — its
    /// content laid out in it, the line's first clearing the floats before it, an empty clearing box after the
    /// flow. Drawn, the flowing child stands beside the draw before it at one top, and what follows the flow below
    /// both; under <c>rl-tb</c> a narrower child alone on its line is at the line's right.
    /// </summary>
    [Fact]
    public void LeftToRightFlow_ALineOfAChildOfUnknownHeight_IsFloatedBoxes()
    {
        string D(string text, string w) => Draw(text.ToLowerInvariant(), "0", "0", w, "0.3in", text);
        string narrow = "<subform layout=\"tb\" w=\"3in\">" + D("NARROWX", "3in") + D("UNDERX", "3in") + "</subform>";
        string Body(string layout, string inner) =>
            $"<subform layout=\"tb\" w=\"576pt\"><subform layout=\"{layout}\" w=\"8in\">" + inner + "</subform>" + D("AFTERX", "2in") + "</subform>";

        byte[] pdf = Export(Template(Page(), Body("lr-tb", D("LEFTX", "3in") + narrow)), null, out XfaLayoutResult result);
        Assert.DoesNotContain(result.Approximations, a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
        var boxes = result.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().ToList();
        var floated = boxes.Where(b => b.Float == FloatSide.Left).ToList();
        Assert.Equal(new[] { 216.0, 216.0 }, floated.Select(b => b.FloatWidth));
        Assert.Equal(new[] { true, false }, floated.Select(b => b.ClearFloats));
        Assert.Contains(boxes.Skip(boxes.IndexOf(floated[1]) + 1), b => b.ClearFloats && b.Float == FloatSide.None);

        (double X, double Y) At(string text) { var t = PdfContentProbe.DrawnTextsOnPage(pdf).First(d => d.Text == text); return (t.X, t.Y); }
        Assert.Equal(At("LEFTX").Y, At("NARROWX").Y, 1);
        Assert.Equal(216, At("NARROWX").X - At("LEFTX").X, 1);
        Assert.True(At("UNDERX").Y < At("NARROWX").Y - 15, "the flowing child's second draw is not under its first");
        Assert.True(At("AFTERX").Y < At("UNDERX").Y - 15, "what follows the flow is not below its line");

        // Right to left: a 3 in flowing child alone on its line, at the right of the 8 in line.
        byte[] rtl = Export(Template(Page(), Body("rl-tb", D("WIDEX", "8in") + narrow)), null, out XfaLayoutResult rtlResult);
        Assert.DoesNotContain(rtlResult.Approximations, a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
        var right = rtlResult.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>().Single(b => b.Float == FloatSide.Right);
        Assert.Equal(216, right.FloatWidth, 3);
        var page = PdfContentProbe.DrawnTextsOnPage(rtl);
        Assert.Equal(page.First(d => d.Text == "WIDEX").X + 360, page.First(d => d.Text == "NARROWX").X, 1);

        // And a right-to-left flow whose children are each alone on a line, every height known: a narrower one is
        // placed at the right too — it was laid out top to bottom, at the left, unreported.
        var lined = XfaLayout.Layout(Parse(Template(Page(), "<subform layout=\"rl-tb\" w=\"8in\">" + D("WIDEX", "8in") + D("SHORTX", "3in") + "</subform>")), null);
        var shortDraw = lined.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.Runs.OfType<TextRun>().Any(r => r.Text == "SHORTX"));
        Assert.Equal(360, shortDraw.Placement!.LeftPt, 3);
    }

    /// <summary>
    /// Lines after a line of floats start below its tallest child: a second line of floats, a line of known heights
    /// (its box), and a flowing child alone on its line — each first word below the last word of the line before.
    /// </summary>
    [Fact]
    public void LeftToRightFlow_TheLinesAfterALineOfFloats_StartBelowIt()
    {
        string D(string text, string w) => Draw(text.ToLowerInvariant(), "0", "0", w, "0.3in", text);
        string Tall(string top, string bottom) => "<subform layout=\"tb\" w=\"3in\">" + D(top, "3in") + D(bottom, "3in") + D(bottom + "Z", "3in") + "</subform>";
        string inner = D("ONEX", "3in") + Tall("TALLAX", "TALLBX")              // floats: 3 in + a 0.9 in tall flow
                       + D("TWOX", "3in") + Tall("TALLCX", "TALLDX")            // floats again (6 in + 3 in is past 8 in)
                       + D("KNOWNAX", "5in") + D("KNOWNBX", "3in")               // a line box of known heights
                       + "<subform layout=\"tb\" w=\"8in\">" + D("ALONEX", "8in") + "</subform>" // alone
                       + D("LASTAX", "3in") + Tall("TALLEX", "TALLFX")          // floats once more
                       + "<subform layout=\"tb\" w=\"8in\">" + D("ALONEBX", "8in") + "</subform>";
        byte[] pdf = Export(Template(Page(), "<subform layout=\"lr-tb\" w=\"8in\">" + inner + "</subform>"), null, out XfaLayoutResult result);
        Assert.DoesNotContain(result.Approximations, a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
        double Y(string text) => PdfContentProbe.DrawnTextsOnPage(pdf).First(d => d.Text == text).Y;

        Assert.True(Y("TWOX") < Y("TALLBXZ") - 10, "the second line of floats is not below the first's tallest");
        Assert.True(Y("KNOWNAX") < Y("TALLDXZ") - 10, "the line of known heights is not below the floats");
        Assert.True(Y("ALONEX") < Y("KNOWNAX") - 10, "the flowing child is not below the line of known heights");
        Assert.True(Y("ALONEBX") < Y("TALLFXZ") - 10, "the flowing child after floats is not below them");
    }

    /// <summary>
    /// A line holds its height in the flow: forty lines of two 20 pt boxes overflow a 756 pt content
    /// area onto a second page, as forty positioned 20 pt subforms do, rather than collapsing onto one.
    /// </summary>
    [Fact]
    public void LeftToRightFlow_ItsLinesHoldTheirHeightAcrossPages()
    {
        string item = "<subform name=\"item\" w=\"3in\" h=\"20pt\"><occur min=\"1\" max=\"-1\"/>" + Field("q", "0", "0", "3in", "16pt") + "</subform>";
        var data = new StringBuilder("<form1>");
        for (int i = 0; i < 80; i++) data.Append("<item><q>R").Append(i.ToString(CultureInfo.InvariantCulture)).Append("</q></item>");
        data.Append("</form1>");
        byte[] pdf = Export(Template(Page(), "<subform layout=\"lr-tb\" w=\"8in\">" + item + "</subform>"), Data(data.ToString()), out XfaLayoutResult result);
        Assert.DoesNotContain(result.Approximations, a => a.StartsWith("a left-to-right flow", StringComparison.Ordinal));
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
        Assert.NotNull(TryText(pdf, "R79"));
    }

    [Fact]
    public void HostileTemplate_DeeplyNested_IsBoundedAndSaysSo()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 300; i++) sb.Append("<subform w=\"1in\" h=\"1in\">");
        sb.Append(Draw("deep", "0", "0", "1in", "0.3in", "DEEP"));
        for (int i = 0; i < 300; i++) sb.Append("</subform>");
        XfaTemplate template = Parse(Template(Page(), sb.ToString()));
        Assert.True(template.DepthRefusals > 0);
        var result = XfaLayout.Layout(template, null);
        Assert.Contains(result.Approximations, a => a.Contains("nested deeper", StringComparison.Ordinal));
        Assert.NotEmpty(result.Document.Sections);
    }

    /// <summary>
    /// A page area of two thousand content areas, broken to by every one of ten thousand repeated
    /// instances: each section copied the area's content areas, uncharged, and the ceiling was not
    /// tested between sections — 2 × 10⁷ objects before any export. Sections are charged now, the
    /// ceiling stops the layout, and it says so.
    /// </summary>
    [Fact]
    public void HostileTemplate_PageAreaOfManyAreasRepeated_IsBoundedByTheBlockCeiling()
    {
        string page = "<pageSet><pageArea name=\"P\">" + string.Concat(Enumerable.Repeat("<contentArea w=\"1in\" h=\"1in\"/>", 2_000)) +
                      "<medium short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>";
        string body = "<subform layout=\"tb\"><occur max=\"-1\" initial=\"10000\"/>" +
                      "<subform layout=\"tb\"><breakBefore targetType=\"pageArea\" target=\"P\"/></subform></subform>";

        var result = XfaLayout.Layout(Parse(Template(page, body)), null);

        Assert.Contains(result.Approximations, a => a.Contains("block ceiling", StringComparison.Ordinal));
        Assert.InRange(result.Document.Sections.Sum(s => (long)s.ContentAreas.Count), 2_000, XfaLayout.MaxBlocks + 2_000);
    }

    /// <summary>
    /// A subform set inside a subform set: its members lay out as the outer set's do, in the flow and
    /// bound to their own data. The inner set was read as a positioned subform, its members drawn at
    /// their zero offsets on top of each other, with no data.
    /// </summary>
    [Fact]
    public void ASubformSetInsideASubformSet_FlowsItsMembersWithTheirData()
    {
        string body = "<subformSet><subformSet name=\"s\">" +
                      "<subform name=\"A\" layout=\"tb\">" + Field("a", "0", "0", "2in", "10mm") + "</subform>" +
                      "<subform name=\"B\" layout=\"tb\">" + Field("b", "0", "0", "2in", "10mm") + "</subform>" +
                      "</subformSet></subformSet>";
        byte[] pdf = Export(Template(Page(), body), Data("<form1><A><a>VALA1</a></A><B><b>VALB2</b></B></form1>"), out _);

        var a = TryText(pdf, "VALA1");
        var b = TryText(pdf, "VALB2");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(b!.Value.Y < a!.Value.Y - 10, $"B flows below A: {a.Value.Y} then {b.Value.Y}");
    }

    [Fact]
    public void HostileTemplate_UnboundedOccurOverThousandsOfRows_IsCappedAndSaysSo()
    {
        string item = "<subform name=\"item\" layout=\"tb\"><occur min=\"1\" max=\"-1\"/>" + Field("q", "0", "0", "1in", "10pt") + "</subform>";
        var data = new StringBuilder("<form1>");
        for (int i = 0; i < XfaLayout.MaxInstancesPerSubform + 50; i++) data.Append("<item><q>x</q></item>");
        data.Append("</form1>");
        var result = XfaLayout.Layout(Parse(Template(Page(), item)), Data(data.ToString()));
        Assert.Contains(result.Approximations, a => a.Contains("more data instances than its occur max", StringComparison.Ordinal));
        int fields = result.Document.Sections.Sum(s => s.Children.OfType<BoxStart>().Count());
        Assert.InRange(fields, XfaLayout.MaxInstancesPerSubform, XfaLayout.MaxInstancesPerSubform + 1);
    }

    [Fact]
    public void HostileTemplate_BlockCeiling_StopsTheLayoutAndSaysSo()
    {
        // The ceiling is a parameter so a template of ordinary size reaches it; restored in finally.
        var body = new StringBuilder();
        for (int i = 0; i < 200; i++) body.Append(Draw("d" + i, "0", (i * 3) + "pt", "1in", "3pt", "T" + i));
        int before = XfaLayout.MaxBlocks;
        try
        {
            XfaLayout.MaxBlocks = 60;
            var result = XfaLayout.Layout(Parse(Template(Page(), Block(body.ToString()))), null);
            Assert.Contains(result.Approximations, a => a.Contains("block ceiling", StringComparison.Ordinal));
            int blocks = result.Document.Sections.Sum(s => s.Children.Count);
            Assert.InRange(blocks, 1, 60 + 4);   // the ceiling, a block or two of slack for the box being closed
        }
        finally { XfaLayout.MaxBlocks = before; }
        // The same draws in the flow rather than positioned: the flow's own check stops it.
        try
        {
            XfaLayout.MaxBlocks = 60;
            var flowed = XfaLayout.Layout(Parse(Template(Page(), body.ToString().Replace(" x=\"0\"", ""))), null);
            Assert.Contains(flowed.Approximations, a => a.Contains("block ceiling", StringComparison.Ordinal));
            Assert.InRange(flowed.Document.Sections.Sum(s => s.Children.Count), 1, 60 + 4);
        }
        finally { XfaLayout.MaxBlocks = before; }
        // The control: under the default ceiling every draw is laid out and nothing is said.
        var whole = XfaLayout.Layout(Parse(Template(Page(), Block(body.ToString()))), null);
        Assert.DoesNotContain(whole.Approximations, a => a.Contains("block ceiling", StringComparison.Ordinal));
        Assert.True(whole.Document.Sections.Sum(s => s.Children.Count) >= 200);
    }

    [Fact]
    public void HostileTemplate_ExternalEntity_IsRefusedAsNotWellFormed()
    {
        string xxe = "<?xml version=\"1.0\"?><!DOCTYPE t [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>"
                     + "<template xmlns=\"" + Ns + "\"><subform name=\"form1\">&x;</subform></template>";
        Assert.Null(XfaTemplate.Parse(Encoding.UTF8.GetBytes(xxe)));
        // The control: the same template without the entity parses.
        Assert.NotNull(XfaTemplate.Parse(Encoding.UTF8.GetBytes("<template xmlns=\"" + Ns + "\"><subform name=\"form1\"/></template>")));
    }

    [Fact]
    public void Read_FromAPdf_BindsTemplateAndDatasets_AndSaysWhyItCannot()
    {
        byte[] pdf = XfaPdf(Template(Page(), Block(Field("name", "0in", "1in", "4in", "0.3in"))), Datasets("<form1><name>Linus</name></form1>"));
        XfaLayoutResult? result = XfaLayout.Read(new MemoryStream(pdf));
        Assert.NotNull(result);
        Assert.Null(XfaLayout.LastError);
        Assert.NotNull(TryText(result!.Document.ToPdf(), "Linus"));

        Assert.Null(XfaLayout.Read(new MemoryStream(XfaPdf("not xml at all", Datasets("<form1/>")))));
        Assert.Contains("not well-formed", XfaLayout.LastError, StringComparison.Ordinal);

        // An /XFA array with no template packet: the reason names the packet.
        byte[] noTemplate = Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            Contents("BT ET"),
            "<< /Fields [] /XFA [(datasets) 6 0 R] >>",
            Contents(Datasets("<form1/>")),
        });
        Assert.Null(XfaLayout.Read(new MemoryStream(noTemplate)));
        Assert.Contains("'template' packet", XfaLayout.LastError, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- a real Designer template

    /// <summary>
    /// A template the XFA form designer wrote (an independent renderer's pixel-test fixture): a draw with three
    /// paragraph-separated lines and two fields with captions at <c>reserve</c>, one placed right.
    /// </summary>
    [CorpusFact("xfa_node_caption.pdf")]
    public void DesignerTemplate_FromAPublicFixture_LaysOutItsDrawsAndCaptions()
    {
        using var fs = File.OpenRead(TestCorpus.Find("xfa_node_caption.pdf")!);
        XfaLayoutResult? result = XfaLayout.Read(fs);
        Assert.NotNull(result);
        byte[] pdf = result!.Document.ToPdf();
        // Designer wrote two page-filling positioned subforms (8 in × 10.5 in each): two pages,
        // the second's buttons on the second — not collapsed over the first.
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
        Text(pdf, "Button1");
        // The title's first word ("Class: node  Object: caption  Properties: placement" — the
        // exporter shows a line's words one draw each where the line is justified or spaced).
        var title = Text(pdf, "Class:");
        // x="6.35mm" in a content area at 0.25in: 18 + 18 + the draw's 0.5mm inset.
        Assert.Equal(18 + 18 + 0.5 * 72 / 25.4, title.X, 1);
        Text(pdf, "default");
        Text(pdf, "placement=\"inline\",caption");
        // The scripts the form carries are reported, not run.
        Assert.Contains(result.Approximations, a => a.StartsWith("a script", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- fixtures

    private static string Page() =>
        "<pageSet><pageArea name=\"Page1\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"576pt\" h=\"756pt\"/><medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>";

    /// <summary>A root subform <c>form1</c> flowing top to bottom, as Designer writes it.</summary>
    private static string Template(string pageSet, string body) =>
        "<template xmlns=\"" + Ns + "\"><subform name=\"form1\" layout=\"tb\">" + pageSet + body + "</subform></template>";

    /// <summary>The page-filling positioned subform Designer puts the page's content in.</summary>
    private static string Block(string inner) => "<subform w=\"576pt\" h=\"756pt\">" + inner + "</subform>";

    private static string Draw(string name, string x, string y, string w, string h, string text) =>
        $"<draw name=\"{name}\" x=\"{x}\" y=\"{y}\" w=\"{w}\" h=\"{h}\"><ui><textEdit/></ui><value><text>{text}</text></value><font typeface=\"Myriad Pro\" size=\"12pt\"/></draw>";

    private static string Field(string name, string x, string y, string w, string h, string? caption = null, string? reserve = null,
                                string placement = "left", string? defaultValue = null, string extra = "")
    {
        string cap = caption == null ? "" :
            $"<caption{(placement != "left" ? $" placement=\"{placement}\"" : "")}{(reserve != null ? $" reserve=\"{reserve}\"" : "")}><value><text>{caption}</text></value></caption>";
        string value = defaultValue == null ? "" : $"<value><text>{defaultValue}</text></value>";
        return $"<field name=\"{name}\" x=\"{x}\" y=\"{y}\" w=\"{w}\" h=\"{h}\"><ui><textEdit/></ui>{cap}{value}<font typeface=\"Myriad Pro\" size=\"12pt\"/>{extra}</field>";
    }

    private static string Datasets(string body) =>
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data>" + body + "</xfa:data></xfa:datasets>";

    private static XElement Data(string body) => XElement.Parse(Datasets(body)).Elements().First();

    private static XfaTemplate Parse(string template)
    {
        XfaTemplate? t = XfaTemplate.Parse(Encoding.UTF8.GetBytes(template));
        Assert.NotNull(t);
        return t!;
    }

    private static byte[] Export(string template, XElement? data, out XfaLayoutResult result)
    {
        result = XfaLayout.Layout(Parse(template), data);
        return result.Document.ToPdf();
    }

    private static PdfContentProbe.DrawnText Text(byte[] pdf, string text)
    {
        var hit = TryText(pdf, text);
        Assert.True(hit.HasValue, "'" + text + "' was not drawn; drawn: " +
            string.Join(" | ", PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)).Select(t => t.Text)));
        return hit!.Value;
    }

    private static PdfContentProbe.DrawnText? TryText(byte[] pdf, string text)
    {
        var all = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
        foreach (var t in all) if (t.Text.Contains(text, StringComparison.Ordinal)) return t;
        return null;
    }

    private static List<PdfContentProbe.DrawnText> Texts(byte[] pdf, string text) =>
        PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)).Where(t => t.Text.Contains(text, StringComparison.Ordinal)).ToList();

    private static byte[] XfaPdf(string templateXml, string datasetsXml) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        Contents("BT ET"),
        "<< /Fields [] /XFA [(template) 6 0 R (datasets) 7 0 R] >>",
        Contents(templateXml),
        Contents(datasetsXml),
    });

    private static string Contents(string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        return "<< /Length " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + body + "\nendstream";
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(Encoding.UTF8.GetByteCount(sb.ToString()));
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = Encoding.UTF8.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
