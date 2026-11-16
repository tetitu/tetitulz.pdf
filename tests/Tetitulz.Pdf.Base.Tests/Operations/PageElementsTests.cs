// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfPageElements"/>: what a page draws, listed with the box each thing paints in, and
/// the page written again without some of them — every other element where it was.
/// </summary>
/// <remarks>
/// "Where it was" is asserted by listing the output again: a remaining element's box comes from a
/// fresh measuring render of the new file, so a removal that moved it is seen. The fixture page is
/// 200 by 200 and holds two runs of text on one line, a picture, a filled square, a stroked line, a
/// fill under a clip, a form, and a second text object whose lines are started with <c>'</c>.
/// </remarks>
public class PageElementsTests
{
    private const string PageContent =
        "BT /F1 12 Tf 10 180 Td (Hello) Tj ( World) Tj ET\n" +        // 0 BT, 1 Tf, 2 Td, 3 Tj, 4 Tj, 5 ET
        "q 50 0 0 50 10 10 cm /Im1 Do Q\n" +                           // 6 q, 7 cm, 8 Do, 9 Q
        "1 0 0 rg 150 150 40 40 re f\n" +                              // 10 rg, 11 re, 12 f
        "0 G 4 w 10 100 m 190 100 l S\n" +                             // 13 G, 14 w, 15 m, 16 l, 17 S
        "q 100 0 100 100 re W n 0 1 0 rg 0 0 200 200 re f Q\n" +       // 18 q, 19 re, 20 W, 21 n, 22 rg, 23 re, 24 f, 25 Q
        "/Fx1 Do\n" +                                                  // 26 Do
        "BT /F1 12 Tf 14 TL 10 80 Td (First) Tj (Second) ' (Tail) Tj ET\n";   // 27 BT, 28 Tf, 29 TL, 30 Td, 31 Tj, 32 ', 33 Tj, 34 ET
    private const string FormContent = "0 0 1 rg 60 120 20 20 re f";

    private const int Hello = 3, World = 4, Picture = 8, Square = 12, Line = 17, ClippedFill = 24, Form = 26, First = 31, Second = 32, Tail = 33;

    // ── listing ─────────────────────────────────────────────────────────────

    [Fact]
    public void List_NamesEachPaintingOperation_InDrawingOrder_WithItsKind()
    {
        IReadOnlyList<PdfPageElement> elements = Elements(Fixture());

        Assert.Equal(new[] { Hello, World, Picture, Square, Line, ClippedFill, Form, First, Second, Tail }, elements.Select(e => e.Id));
        Assert.Equal(new[]
        {
            PdfPageElementKind.Text, PdfPageElementKind.Text, PdfPageElementKind.Image, PdfPageElementKind.Path, PdfPageElementKind.Path,
            PdfPageElementKind.Path, PdfPageElementKind.Form, PdfPageElementKind.Text, PdfPageElementKind.Text, PdfPageElementKind.Text,
        }, elements.Select(e => e.Kind));
        Assert.All(elements, e => Assert.Equal(0, e.PageIndex));
    }

    [Fact]
    public void List_GivesEachElementTheBoxItPaintsIn_InUserSpace()
    {
        IReadOnlyList<PdfPageElement> elements = Elements(Fixture());

        AssertBox(elements.Single(e => e.Id == Square), 150, 150, 40, 40);
        AssertBox(elements.Single(e => e.Id == Picture), 10, 10, 50, 50);
        // A stroked line's box is the line and half its width (4) either side — not the room the
        // renderer's bound leaves for a miter join the line does not have.
        AssertBox(elements.Single(e => e.Id == Line), 8, 98, 184, 4);
        // The form's box is what it draws, not the page.
        AssertBox(elements.Single(e => e.Id == Form), 60, 120, 20, 20);
        // A bound is the geometry's, never narrowed by the clip in force.
        AssertBox(elements.Single(e => e.Id == ClippedFill), 0, 0, 200, 200);

        // Text is where its glyphs are, with the room the renderer's bound leaves round a run: the
        // box holds the run's start on its baseline, and the second run's box lies further along.
        PdfPageElement hello = elements.Single(e => e.Id == Hello), world = elements.Single(e => e.Id == World);
        Assert.InRange(hello.X, 0, 10);
        Assert.InRange(hello.X + hello.Width, 30, 70);
        Assert.InRange(hello.Y, 165, 180);
        Assert.InRange(hello.Y + hello.Height, 186, 200);
        Assert.True(world.X > hello.X + 15, $"World's box starts at {world.X}, Hello's at {hello.X}");
        Assert.True(world.X + world.Width > hello.X + hello.Width + 15, "World's box ends further along than Hello's");
    }

    [Fact]
    public void List_SaysWhichTextObjectShowsEachRun()
    {
        IReadOnlyList<PdfPageElement> elements = Elements(Fixture());

        Assert.Equal(new[] { 0, 0, 1, 1, 1 }, elements.Where(e => e.Kind == PdfPageElementKind.Text).Select(e => e.TextObject));
        Assert.All(elements.Where(e => e.Kind != PdfPageElementKind.Text), e => Assert.Equal(-1, e.TextObject));
    }

    /// <summary>A run of text carries what it shows, as its font maps it; anything else carries no text.</summary>
    [Fact]
    public void List_GivesEachRunItsText()
    {
        IReadOnlyList<PdfPageElement> elements = Elements(Fixture());

        Assert.Equal("Hello", elements.Single(e => e.Id == Hello).Text);
        Assert.Equal(" World", elements.Single(e => e.Id == World).Text);
        Assert.Equal(new[] { "First", "Second", "Tail" }, new[] { First, Second, Tail }.Select(id => elements.Single(e => e.Id == id).Text));
        Assert.All(elements.Where(e => e.Kind != PdfPageElementKind.Text), e => Assert.Null(e.Text));
    }

    /// <summary>
    /// A font whose codes a–d map to final mem, vav, lamed and shin, e to bet with its dagesh (two
    /// characters, as a presentation form's glyph maps), 1 and 2 and the space to themselves.
    /// </summary>
    private static byte[] HebrewFixture(string content)
    {
        const string cmap =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /HebrewProbe def\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "8 beginbfchar <61> <05DD> <62> <05D5> <63> <05DC> <64> <05E9> <65> <05D105BC> <31> <0031> <32> <0032> <20> <0020> endbfchar\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end";
        return new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F3 5 0 R >> >> >>")
            .StreamObj(4, "<<", content)
            .Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>")
            .StreamObj(6, "<<", cmap)
            .Finish();
    }

    /// <summary>
    /// A run's glyphs are drawn left to right, so a Hebrew run is a visual line and its text is read
    /// back from its right end: shin-lamed-vav-final mem drawn as final mem first reads "שלום"; a
    /// letter with its point keeps the point after the letter; digits drawn beside the Hebrew stay
    /// "12", after the word they follow. A left-to-right run is as it was (<see cref="List_GivesEachRunItsText"/>).
    /// </summary>
    [Fact]
    public void List_GivesARightToLeftRunItsTextInReadingOrder()
    {
        // 0 BT, 1 Tf, 2 Td, 3 Tj, 4 Td, 5 Tj, 6 Td, 7 Tj, 8 ET
        byte[] pdf = HebrewFixture("BT /F3 12 Tf 10 180 Td (abcd) Tj 0 -20 Td (ae) Tj 0 -20 Td (12 abcd) Tj ET");
        IReadOnlyList<PdfPageElement> elements = Elements(pdf);

        Assert.Equal("שלום", elements.Single(e => e.Id == 3).Text);
        Assert.Equal("בּם", elements.Single(e => e.Id == 5).Text);
        Assert.Equal("שלום 12", elements.Single(e => e.Id == 7).Text);
    }

    /// <summary>
    /// A run drawn right to left — each glyph placed before the last, as a converted print's joined line
    /// may be — reads in its order on the page, not the order drawn: a, b, c, d drawn leftwards from
    /// x 100 stand d, c, b, a, so the visual line is shin-lamed-vav-final mem reversed, and reads
    /// "םולש" — the same text drawn rightwards reads "שלום". Replacing it starts the new text at the
    /// run's left end and sizes it to the run's whole span, the TJ numbers included.
    /// </summary>
    [Fact]
    public void ARunDrawnRightToLeft_ReadsInItsPageOrder_AndIsReplacedFromItsLeftEnd()
    {
        // Helvetica draws a–d at 556/556/500/556 units; each number moves the pen back over the glyph
        // just drawn and the next one, so each stands just left of the last: a at 100, d at about 80.7.
        byte[] pdf = HebrewFixture("BT /F3 12 Tf 100 180 Td [(a) 1112 (b) 1056 (c) 1056 (d)] TJ ET");
        PdfPageElement run = Elements(pdf).Single(e => e.Kind == PdfPageElementKind.Text);
        Assert.Equal("םולש", run.Text);

        const string value = "Newer";
        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(pdf), output, 0, new[] { run.Id }, value,
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.BuildWithDistinctWidths(value) });
        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        Assert.Equal(run.Text, Assert.Single(result.ReplacedKeys));
        PdfPageElement drawn = Elements(output.ToArray()).Single(e => e.Text == value);
        Assert.True(drawn.X < 100 - 12, $"the new text starts at the run's left end, left of the first glyph drawn at 100: {drawn.X}");
    }

    /// <summary>
    /// A replaced run is reported under the key its element shows — a right-to-left run's text in
    /// reading order, not its glyphs as drawn — so a caller matches the result to the element.
    /// </summary>
    [Fact]
    public void ReplaceRun_ReportsARightToLeftRun_AsItsElementsText()
    {
        byte[] pdf = HebrewFixture("BT /F3 12 Tf 10 180 Td (abcd) Tj ET");
        PdfPageElement run = Elements(pdf).Single(e => e.Kind == PdfPageElementKind.Text);
        Assert.Equal("שלום", run.Text);

        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(pdf), output, 0, new[] { run.Id }, "New",
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("New") });

        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        Assert.Equal(run.Text, Assert.Single(result.ReplacedKeys));
    }

    // ── replacing a run's text ──────────────────────────────────────────────

    /// <summary>The same word twice on a page: the run named changes, the other says what it said and stays where it was.</summary>
    [Fact]
    public void ReplaceRun_ChangesTheRunsNamed_AndLeavesTheSameWordElsewhere()
    {
        // 0 BT, 1 Tf, 2 Td, 3 Tj, 4 ET, 5 BT, 6 Tf, 7 Td, 8 Tj, 9 ET
        byte[] input = Fixture(content: "BT /F1 12 Tf 10 180 Td (Total) Tj ET\nBT /F1 12 Tf 10 100 Td (Total) Tj ET\n");
        IReadOnlyList<PdfPageElement> before = Elements(input);
        Assert.Equal(new[] { "Total", "Total" }, before.Select(e => e.Text));

        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, new[] { 8 }, "Sum",
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Sum") });

        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        Assert.Equal("Total", Assert.Single(result.ReplacedKeys));
        Assert.Empty(result.NotFoundKeys);
        string text = ExtractText(output.ToArray());
        Assert.Contains("Sum", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("Total").Length - 1);
        // The run left alone is where it was, and the replacement stands inside the slot of the one
        // named (the old run's box has the renderer's room round a font that is not embedded; the
        // replacement's, drawn in an embedded one, is tight).
        IReadOnlyList<PdfPageElement> after = Elements(output.ToArray());
        AssertBox(after.Single(e => e.Text == "Total"), before[0].X, before[0].Y, before[0].Width, before[0].Height, tolerance: 0.05);
        PdfPageElement sum = after.Single(e => e.Text == "Sum"), slot = before[1];
        Assert.InRange(sum.X, slot.X - 1, slot.X + slot.Width);
        Assert.InRange(sum.X + sum.Width, slot.X, slot.X + slot.Width + 1);
        Assert.InRange(sum.Y + sum.Height / 2, slot.Y, slot.Y + slot.Height);
        Assert.True(sum.Y < 100 && sum.Y + sum.Height > 100, "the replacement stands on the baseline at y 100");
    }

    /// <summary>
    /// A run written as a print driver writes it — <c>1 Tf</c>, the 24 points in the text matrix —
    /// takes a longer replacement shrunk into its slot, nothing said: the 4-point floor is points on
    /// the page, not Tf's units, where 4 would be 96 points. A replacement that would need less than
    /// 4 points is said and drawn at 4 points on the page, its glyphs a few points high, not 96.
    /// </summary>
    [Theory]
    [InlineData("BT /F1 1 Tf 24 0 0 24 10 100 Tm (Total) Tj ET\n")]
    [InlineData("BT /F1 24 Tf 10 100 Td (Total) Tj ET\n")]
    public void ReplaceRun_ShrinksIntoTheSlot_WithTheFloorInPointsOnThePage(string content)
    {
        byte[] input = Fixture(content: content);
        PdfPageElement slot = Elements(input).Single(e => e.Text == "Total");
        const string longer = "Grand total";
        const string longest = "Grand total of every line on every page of this report, carried";

        using var fitted = new MemoryStream();
        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), fitted, 0, new[] { slot.Id }, longer,
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build(longer) });
        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        Assert.Empty(result.Warnings);
        PdfPageElement shrunk = Elements(fitted.ToArray()).Single(e => e.Text == longer);
        Assert.InRange(shrunk.X + shrunk.Width, slot.X, slot.X + slot.Width + 1);

        using var floored = new MemoryStream();
        result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), floored, 0, new[] { slot.Id }, longest,
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build(longest) });
        Assert.Contains(result.Warnings, w => w.Contains("even at 4pt", StringComparison.Ordinal));
        PdfPageElement atFloor = Elements(floored.ToArray()).Single(e => e.Text == longest);
        Assert.True(atFloor.Width > slot.Width, $"at the floor the text overflows its slot: {atFloor.Width}");
        Assert.True(atFloor.Height < 12, $"drawn at 4 points on the page, not 4 of Tf's units: {atFloor.Height} high");
    }

    /// <summary>
    /// A line a print driver wrote in pieces — "AB" in one text object at 12 points, "C" in another
    /// under a doubled CTM and a 6-point matrix, standing where "AB" ends (each letter 2 em) — is one
    /// slot on the page: both runs erased, the replacement drawn from the line's left end at x 10 to
    /// no further than its right end at 82, on the baseline at y 100. Runs on two lines are refused.
    /// </summary>
    [Fact]
    public void ReplaceRun_AcrossTextObjects_OnOneLine_IsOneSlotOnThePage()
    {
        // 0 q, 1 BT, 2 Tf, 3 Tm, 4 Tj, 5 ET, 6 Q, 7 q, 8 cm, 9 BT, 10 Tf, 11 Tm, 12 Tj, 13 ET, 14 Q
        byte[] input = WideFixture("q BT /F2 1 Tf 12 0 0 12 10 100 Tm (AB) Tj ET Q\n" +
                                   "q 2 0 0 2 0 0 cm BT /F2 1 Tf 6 0 0 6 29 50 Tm (C) Tj ET Q\n");
        // About 37 points at 12: wider than the first glyph's 24, within the line's 72, so drawn at its size.
        const string value = "Newer";
        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, new[] { 4, 12 }, value,
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.BuildWithDistinctWidths(value) });

        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        IReadOnlyList<PdfPageElement> after = Elements(output.ToArray());
        Assert.DoesNotContain(after, e => e.Text is "AB" or "C");
        PdfPageElement drawn = after.Single(e => e.Text == value);
        Assert.InRange(drawn.X, 9, 11);
        Assert.InRange(drawn.X + drawn.Width, 40, 83);
        Assert.True(drawn.Y < 100 && drawn.Y + drawn.Height > 100, $"on the baseline at y 100: {drawn.Y} to {drawn.Y + drawn.Height}");

        byte[] twoLines = WideFixture("q BT /F2 1 Tf 12 0 0 12 10 100 Tm (AB) Tj ET Q\n" +
                                      "q 2 0 0 2 0 0 cm BT /F2 1 Tf 6 0 0 6 29 40 Tm (C) Tj ET Q\n");
        using var refused = new MemoryStream();
        result = PdfTemplateEngine.ReplaceRun(new MemoryStream(twoLines), refused, 0, new[] { 4, 12 }, value,
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build(value) });
        Assert.False(result.ChangedAnything);
        Assert.Contains(result.Warnings, w => w.Contains("not on one line of the page", StringComparison.Ordinal));
    }

    /// <summary>
    /// Pieces drawn right to left — the right one first, as a right-to-left line may be — make the
    /// same slot: the replacement starts at the left piece, x 10, not where the first was drawn.
    /// </summary>
    [Fact]
    public void ReplaceRun_AcrossTextObjects_StartsAtTheLinesLeftEnd()
    {
        // 0 q, 1 BT, 2 Tf, 3 Tm, 4 Tj, 5 ET, 6 Q, 7 q, 8 BT, 9 Tf, 10 Tm, 11 Tj, 12 ET, 13 Q
        byte[] input = WideFixture("q BT /F2 1 Tf 12 0 0 12 58 100 Tm (C) Tj ET Q\n" +
                                   "q BT /F2 1 Tf 12 0 0 12 10 100 Tm (AB) Tj ET Q\n");
        const string value = "New";
        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, new[] { 4, 11 }, value,
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build(value) });

        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        PdfPageElement drawn = Elements(output.ToArray()).Single(e => e.Text == value);
        Assert.InRange(drawn.X, 9, 11);
        Assert.InRange(drawn.X + drawn.Width, 11, 83);
    }

    /// <summary>
    /// A second edit of the page leaves the first one's text as it was: each edit embeds a font
    /// holding only the glyphs it draws, and the second takes a resource name the page does not use
    /// — named as the first one's, it repainted the first edit's text in its own glyphs.
    /// </summary>
    [Fact]
    public void ReplaceRun_Twice_LeavesTheFirstEditsTextAsItWas()
    {
        byte[] input = Fixture(content: "BT /F1 12 Tf 10 180 Td (Total) Tj ET\nBT /F1 12 Tf 10 100 Td (Sum) Tj ET\n");

        byte[] once = Edit(input, "Total", "Grand");
        byte[] twice = Edit(once, "Sum", "Other");

        IReadOnlyList<PdfPageElement> after = Elements(twice);
        Assert.Contains(after, e => e.Text == "Grand");
        Assert.Contains(after, e => e.Text == "Other");
        using var doc = PdfDocument.Open(new MemoryStream(twice));
        var fonts = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]);
        Assert.Equal(new[] { "TplF0", "TplF1" }, fonts.Select(kv => kv.Key.Value).Where(n => n.StartsWith("TplF", StringComparison.Ordinal)).Order());

        static byte[] Edit(byte[] pdf, string run, string value)
        {
            int id = Elements(pdf).Single(e => e.Text == run).Id;
            using var output = new MemoryStream();
            TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(pdf), output, 0, new[] { id }, value,
                new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build(value) });
            Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
            return output.ToArray();
        }
    }

    /// <summary>Runs with other text drawn between them are no one slot: refused by name, nothing changed.</summary>
    [Fact]
    public void ReplaceRun_OfRunsWithOtherTextBetweenThem_RefusesNamingThem_AndChangesNothing()
    {
        byte[] input = Fixture();
        using var output = new MemoryStream();

        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, new[] { First, Tail }, "Joined",
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Joined") });

        Assert.False(result.ChangedAnything);
        Assert.Equal("elements 31, 33 of page 1", Assert.Single(result.NotFoundKeys));
        Assert.Contains(result.Warnings, w => w.Contains("between the runs", StringComparison.Ordinal));
        Assert.Equal(input, output.ToArray());
        // The control: the two runs beside each other are one slot.
        using var joined = new MemoryStream();
        Assert.True(PdfTemplateEngine.ReplaceRun(new MemoryStream(input), joined, 0, new[] { Hello, World }, "Joined",
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Joined") }).ChangedAnything);
        Assert.Contains("Joined", ExtractText(joined.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", ExtractText(joined.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The operations the edit leaves alone are written with the numbers the parser read — a
    /// PostScript driver's page draws under a <c>cm</c> of <c>8.33333</c> and text matrices of
    /// <c>0.999436</c>, under which a number rounded to four decimals through a <c>float</c> puts
    /// every glyph a hair from where it was, and the engine's own check then refuses the page.
    /// </summary>
    [Fact]
    public void ReplaceRun_WritesTheUntouchedOperationsWithTheNumbersRead()
    {
        // 0 cm, 1 cm, 2 BT, 3 Tf, 4 Tm, 5 Tj, 6 ET, 7 BT, 8 Tf, 9 Tm, 10 Tj, 11 ET
        byte[] input = Fixture(content:
            "0.12 0 0 0.12 0 0 cm 8.33333 0 0 8.33333 0 0 cm\n" +
            "BT /F1 12 Tf 0.999436 0 0 1 132.6 711.92 Tm (Keep) Tj ET\n" +
            "BT /F1 12 Tf 0.999436 0 0 1 132.6 611.92 Tm (Change) Tj ET\n");
        using var output = new MemoryStream();

        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, new[] { 10 }, "Changed",
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Changed") });

        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        List<ContentStreamOperation> ops = PageOps(output.ToArray());
        double[] Numbers(ContentStreamOperation op) => op.Operands.Select(o => ((PdfNumber)o).DoubleValue).ToArray();
        Assert.Equal(new[] { 8.33333, 0, 0, 8.33333, 0, 0 }, Numbers(ops[1]));
        Assert.Equal(new[] { 0.999436, 0, 0, 1, 132.6, 711.92 }, Numbers(ops.First(o => o.Operator == PdfOperators.SetTextMatrix)));
        Assert.Contains("Keep", ExtractText(output.ToArray()), StringComparison.Ordinal);
        Assert.Contains("Changed", ExtractText(output.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The replacement is asked for in the family the run was drawn in — the <c>/BaseFont</c>
    /// without its subset tag and its style — so it reads like the text round it; the fixture's
    /// own Helvetica run asks for Helvetica.
    /// </summary>
    [Fact]
    public void ReplaceRun_AsksForTheFamilyTheRunWasDrawnIn()
    {
        byte[] david = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F2 5 0 R >> >> >>")
            .StreamObj(4, "<<", "BT /F2 12 Tf 10 180 Td (ABC) Tj ET")      // 0 BT, 1 Tf, 2 Td, 3 Tj, 4 ET
            .Obj(5, "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+David,Bold /FirstChar 65 /LastChar 67 /Widths [600 600 600] /Encoding /WinAnsiEncoding >>")
            .Finish();
        var asked = new List<string>();
        byte[] Provider(string family) { asked.Add(family); return MinimalTrueType.Build("ABCXYZ"); }

        using var output = new MemoryStream();
        Assert.True(PdfTemplateEngine.ReplaceRun(new MemoryStream(david), output, 0, new[] { 3 }, "XYZ",
            new TemplateReplaceOptions { FontProvider = Provider }).ChangedAnything);
        Assert.True(PdfTemplateEngine.ReplaceRun(new MemoryStream(Fixture()), output, 0, new[] { Hello }, "XYZ",
            new TemplateReplaceOptions { FontProvider = Provider }).ChangedAnything);

        Assert.Equal(new[] { "David", "Helvetica" }, asked);
        Assert.Equal("Helvetica", PdfTemplateEngine.FamilyOf("Helvetica-BoldOblique"));
        Assert.Equal("Arial", PdfTemplateEngine.FamilyOf("Arial-ItalicMT"));
        Assert.Equal("Arial-Narrow", PdfTemplateEngine.FamilyOf("Arial-Narrow"));
        Assert.Equal("Noto Sans Hebrew", PdfTemplateEngine.FamilyOf("Noto Sans Hebrew"));
    }

    /// <summary>An id that names no run of text, or a page the document lacks, is refused by name.</summary>
    [Fact]
    public void ReplaceRun_OfWhatIsNoText_Refuses()
    {
        byte[] input = Fixture();
        using var output = new MemoryStream();

        TemplateReplaceResult result = PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, new[] { Square }, "x", new TemplateReplaceOptions());

        Assert.False(result.ChangedAnything);
        Assert.Equal("element 12 of page 1", Assert.Single(result.NotFoundKeys));
        Assert.Throws<ArgumentException>(() => PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, Array.Empty<int>(), "x", null));
        Assert.Throws<ArgumentNullException>(() => PdfTemplateEngine.ReplaceRun(new MemoryStream(input), output, 0, new[] { Hello }, null, null));
    }

    /// <summary>What paints nothing is not an element: invisible text, a path that only clips, text with no size.</summary>
    [Fact]
    public void List_LeavesOutWhatPaintsNothing()
    {
        byte[] pdf = Fixture(content:
            "BT /F1 12 Tf 3 Tr 10 180 Td (Hidden) Tj 0 Tr (Shown) Tj ET\n" +     // 0 BT, 1 Tf, 2 Tr, 3 Td, 4 Tj, 5 Tr, 6 Tj
            "0 0 50 50 re W n\n" +
            "BT /F1 0 Tf 10 100 Td (NoSize) Tj ET\n");

        PdfPageElement only = Assert.Single(Elements(pdf));

        Assert.Equal(PdfPageElementKind.Text, only.Kind);
        Assert.Equal(6, only.Id);
    }

    // ── removing ────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_OfAPath_EndsItUnpainted_AndLeavesEverythingElseWhereItWas()
    {
        byte[] input = Fixture();
        (byte[] output, int removed) = Remove(input, Square);

        Assert.Equal(1, removed);
        List<string> before = Operators(input), after = Operators(output);
        Assert.Equal(before.Count(o => o == PdfOperators.Fill) - 1, after.Count(o => o == PdfOperators.Fill));
        Assert.Equal(before.Count(o => o == PdfOperators.EndPath) + 1, after.Count(o => o == PdfOperators.EndPath));
        AssertTheRestIsWhereItWas(input, output, Square);
    }

    [Fact]
    public void Remove_OfAPicture_AFormAndAStroke_DropsEach()
    {
        byte[] input = Fixture();
        (byte[] output, int removed) = Remove(input, Picture, Form, Line);

        Assert.Equal(3, removed);
        List<string> after = Operators(output);
        Assert.DoesNotContain(PdfOperators.InvokeXObject, after);
        Assert.DoesNotContain(PdfOperators.Stroke, after);
        // The control: the input drew both XObjects and the stroke.
        Assert.Equal(2, Operators(input).Count(o => o == PdfOperators.InvokeXObject));
        Assert.Contains(PdfOperators.Stroke, Operators(input));
        AssertTheRestIsWhereItWas(input, output, Picture, Form, Line);
    }

    /// <summary>
    /// The run after a removed one has no position of its own: it is drawn where the removed run
    /// left the pen. The pen is moved as the run moved it, so the second run stays where it was.
    /// </summary>
    [Fact]
    public void Remove_OfARun_LeavesTheRunAfterItOnTheLineWhereItWas()
    {
        byte[] input = Fixture();
        (byte[] output, _) = Remove(input, Hello);

        string text = ExtractText(output);
        Assert.DoesNotContain("Hello", text);
        Assert.Contains("World", text);
        Assert.Contains("Hello", ExtractText(input));
        AssertTheRestIsWhereItWas(input, output, Hello);
    }

    /// <summary>A <c>'</c> moves to the next line before it shows: taken out, the move stays, and the run after it too.</summary>
    [Fact]
    public void Remove_OfARunThatStartsALine_KeepsTheLineMove()
    {
        byte[] input = Fixture();
        (byte[] output, _) = Remove(input, Second);

        string text = ExtractText(output);
        Assert.DoesNotContain("Second", text);
        Assert.Contains("Tail", text);
        Assert.Contains(PdfOperators.NextLine, Operators(output));
        Assert.DoesNotContain(PdfOperators.NextLine, Operators(input));
        AssertTheRestIsWhereItWas(input, output, Second);
    }

    /// <summary>A <c>"</c> sets the word and character spacing as well: both stay in force for the run after it.</summary>
    [Fact]
    public void Remove_OfARunThatSetsSpacing_KeepsTheSpacingAndTheLineMove()
    {
        byte[] input = Fixture(content: "BT /F1 12 Tf 14 TL 10 180 Td (One) Tj 5 2 (Two words) \" (Three words) Tj ET\n");
        const int two = 5, three = 6;
        PdfPageElement threeBefore = Elements(input).Single(e => e.Id == three);

        (byte[] output, _) = Remove(input, two);

        List<ContentStreamOperation> ops = PageOps(output);
        Assert.Equal(5.0, ((PdfNumber)ops.Single(o => o.Operator == PdfOperators.SetWordSpacing).Operands[0]).DoubleValue);
        Assert.Equal(2.0, ((PdfNumber)ops.Single(o => o.Operator == PdfOperators.SetCharSpacing).Operands[0]).DoubleValue);
        PdfPageElement threeAfter = Elements(output).Single(e => e.Kind == PdfPageElementKind.Text && Math.Abs(e.Y - threeBefore.Y) < 1);
        AssertBox(threeAfter, threeBefore.X, threeBefore.Y, threeBefore.Width, threeBefore.Height, tolerance: 0.05);
        Assert.DoesNotContain("Two", ExtractText(output));
    }

    /// <summary>A <c>TJ</c>'s own numbers move the pen too, and a turned text matrix turns the line: the run after stays in both.</summary>
    [Theory]
    [InlineData("BT /F1 12 Tf 10 180 Td [(He) -300 (llo) 150] TJ (After) Tj ET\n", 3)]
    [InlineData("BT /F1 12 Tf 0 1 -1 0 100 20 Tm (Up) Tj (After) Tj ET\n", 3)]
    [InlineData("BT /F1 12 Tf 150 Tz 3 Tc 10 180 Td (Wide) Tj (After) Tj ET\n", 5)]
    [InlineData("BT /F1 12 Tf 2 0 0 2 10 100 Tm (Big) Tj (After) Tj ET\n", 3)]
    public void Remove_OfARun_KeepsThePenWhereverTheLineGoes(string content, int removedId)
    {
        byte[] input = Fixture(content: content);
        PdfPageElement afterBefore = Elements(input).Single(e => e.Id == removedId + 1);

        (byte[] output, _) = Remove(input, removedId);

        PdfPageElement afterAfter = Assert.Single(Elements(output));
        AssertBox(afterAfter, afterBefore.X, afterBefore.Y, afterBefore.Width, afterBefore.Height, tolerance: 0.05);
        Assert.Equal("After", ExtractText(output).Trim());
    }

    /// <summary>
    /// A Type 3 glyph is drawn by running its own procedure, operation by operation, in the middle
    /// of the run that shows it. The run's move is still the whole run's — both glyphs' — and the
    /// run after it stays.
    /// </summary>
    [Fact]
    public void Remove_OfAType3Run_LeavesTheRunAfterItWhereItWas()
    {
        byte[] input = Fixture(content: "BT /F3 10 Tf 10 100 Td (aa) Tj (a) Tj ET\n");
        const int first = 3;
        PdfPageElement afterBefore = Elements(input).Single(e => e.Id == first + 1);
        // The second run starts two glyphs along: each advances 10, and the glyph's square starts 1 in.
        Assert.InRange(afterBefore.X, 29, 33);

        (byte[] output, _) = Remove(input, first);

        PdfPageElement afterAfter = Assert.Single(Elements(output));
        AssertBox(afterAfter, afterBefore.X, afterBefore.Y, afterBefore.Width, afterBefore.Height, tolerance: 0.05);
    }

    /// <summary>Text that adds to the clip stays, in mode 7: what it clipped is still clipped, and it paints nothing.</summary>
    [Fact]
    public void Remove_OfTextThatClips_KeepsItClipping()
    {
        byte[] input = Fixture(content: "q BT 6 Tr /F1 40 Tf 10 120 Td (Clip) Tj ET 0 0 1 rg 0 100 200 60 re f Q\n");
        const int clip = 5;

        (byte[] output, int removed) = Remove(input, clip);

        Assert.Equal(1, removed);
        List<ContentStreamOperation> ops = PageOps(output);
        int show = ops.FindIndex(o => o.Operator == PdfOperators.ShowText);
        Assert.True(show > 0, "the clipping run is still shown");
        Assert.Equal(7.0, ((PdfNumber)ops[show - 1].Operands[0]).DoubleValue);
        Assert.Equal(6.0, ((PdfNumber)ops[show + 1].Operands[0]).DoubleValue);
        // It no longer paints, so it is no element; the fill it clips is still one.
        PdfPageElement fill = Assert.Single(Elements(output));
        Assert.Equal(PdfPageElementKind.Path, fill.Kind);
    }

    /// <summary>The removed text is not in the file: no stream of the output holds it, where one of the input did.</summary>
    [Fact]
    public void Remove_WritesTheFileAnew_WithoutTheRemovedText()
    {
        byte[] input = Fixture();
        (byte[] output, _) = Remove(input, Hello);

        Assert.True(AnyStreamHolds(input, "(Hello)"));
        Assert.False(AnyStreamHolds(output, "(Hello)"));
        Assert.True(AnyStreamHolds(output, "( World)"));
    }

    /// <summary>Two pages that draw one content stream: the page edited gets a stream of its own, and the other draws what it drew.</summary>
    [Fact]
    public void Remove_FromAPageThatSharesItsContent_LeavesTheOtherPage()
    {
        byte[] input = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>")
            .StreamObj(4, "<<", "1 0 0 rg 10 10 30 30 re f 0 0 1 rg 100 100 30 30 re f")
            .Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>")
            .Finish();
        int first = PdfPageElements.List(new MemoryStream(input), 0)[0].Id;

        using var output = new MemoryStream();
        PdfPageElements.Remove(new MemoryStream(input), output, 0, new[] { first });

        Assert.Single(PdfPageElements.List(new MemoryStream(output.ToArray()), 0));
        Assert.Equal(2, PdfPageElements.List(new MemoryStream(output.ToArray()), 1).Count);
    }

    [Fact]
    public void Remove_OfNothing_CopiesTheFileThrough()
    {
        byte[] input = Fixture();
        using var output = new MemoryStream();

        int removed = PdfPageElements.Remove(new MemoryStream(input), output, 0, Array.Empty<int>());

        Assert.Equal(0, removed);
        Assert.Equal(input, output.ToArray());
    }

    // ── a stroke's box ──────────────────────────────────────────────────────

    /// <summary>
    /// A stroked path's box is its own points and half its line width round them, whatever the
    /// path is drawn under: a rule of a table lies inside a band drawn round the table.
    /// </summary>
    [Theory]
    [InlineData("1 w 100 20 m 100 180 l S\n", 99.5, 19.5, 1, 161)]                         // a vertical rule
    [InlineData("2 w 20 30 100 50 re S\n", 19, 29, 102, 52)]                               // a rectangle's outline
    [InlineData("q 2 0 0 2 10 10 cm 1 w 10 10 m 60 10 l S Q\n", 29, 29, 102, 2)]           // under a scaling: the width scales with it
    [InlineData("1 w 0.5 g 20 30 100 50 re B\n", 19.5, 29.5, 101, 51)]                     // filled and stroked
    public void List_GivesAStrokeTheBoxOfItsPointsAndHalfItsWidth(string content, double x, double y, double width, double height)
    {
        PdfPageElement stroke = Assert.Single(Elements(Fixture(content: content)));

        AssertBox(stroke, x, y, width, height, tolerance: 0.3);
    }

    /// <summary>
    /// The half width is the line width in force, not a share of the renderer's roomy bound: a
    /// miter limit of 100 or of 1 leaves a 1-point rule's box 1 point wide, and the thinnest line
    /// (width 0) has no width at all.
    /// </summary>
    [Theory]
    [InlineData("100 M 1 w 100 20 m 100 180 l S\n", 99.5, 19.5, 1, 161)]
    [InlineData("1 M 1 w 100 20 m 100 180 l S\n", 99.5, 19.5, 1, 161)]
    [InlineData("0 w 100 20 m 100 180 l S\n", 100, 20, 0, 160)]
    public void List_TakesAStrokesWidthFromTheLineWidth_WhateverTheMiterLimit(string content, double x, double y, double width, double height)
    {
        PdfPageElement stroke = Assert.Single(Elements(Fixture(content: content)));

        AssertBox(stroke, x, y, width, height, tolerance: 0.3);
    }

    /// <summary>
    /// A path with another operator among its points — a line width set after the first point, as
    /// some producers write it — is still one path: its box holds every point, it is taken out
    /// whole, and it is not given a matrix of its own in the middle of itself.
    /// </summary>
    [Theory]
    [InlineData("20 30 m 2 w 120 30 l 120 130 l S\n")]                          // a line width among the points
    [InlineData("2 w 20 30 m /Span BMC 120 30 l EMC 120 130 l S\n")]            // marked content among them: an operand that is no number
    public void APathWithAnotherOperatorAmongItsPoints_IsListedWhole_Removed_AndNotMoved(string content)
    {
        byte[] input = Fixture(content: content);
        PdfPageElement path = Assert.Single(Elements(input));

        AssertBox(path, 19, 29, 102, 102, tolerance: 0.3);

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { path.Id }, Shift));
        Assert.Contains("other operators", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);

        (byte[] removed, _) = Remove(input, path.Id);
        Assert.Empty(Elements(removed));
        // The control: the same path with the width set before it moves.
        byte[] plain = Fixture(content: "2 w 20 30 m 120 30 l 120 130 l S\n");
        Assert.Equal(1, Move(plain, Shift, Assert.Single(Elements(plain)).Id).Moved);
    }

    // ── what a reader does not show ─────────────────────────────────────────

    /// <summary>
    /// Content on a layer the document hides by default is not an element: nobody sees it to
    /// select it, and a band over it must not take it along.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void List_LeavesOutContentTheDocumentHidesByDefault(bool hidden, int expected)
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] /D << " + (hidden ? "/OFF [5 0 R]" : "/ON [5 0 R]") + " >> >> >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Properties << /MC0 5 0 R >> >> >>")
            .StreamObj(4, "<<", "/OC /MC0 BDC 1 0 0 rg 10 10 30 30 re f EMC 0 0 1 rg 100 100 30 30 re f")
            .Obj(5, "<< /Type /OCG /Name (Watermark) >>")
            .Finish();

        IReadOnlyList<PdfPageElement> elements = PdfPageElements.List(new MemoryStream(pdf), 0);

        Assert.Equal(expected, elements.Count);
        AssertBox(elements[^1], 100, 100, 30, 30);
    }

    // ── the advance the file defines ────────────────────────────────────────

    /// <summary>A font that is not embedded, whose own widths — 2000 units a letter — are far from any face a host would draw it in.</summary>
    private const string WideFont = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 67 /Widths [2000 2000 2000] >>";

    private static byte[] WideFixture(string content = "BT /F2 12 Tf 10 180 Td (A) Tj (B) Tj (C) Tj ET")     // 0 BT, 1 Tf, 2 Td, 3 Tj, 4 Tj, 5 Tj, 6 ET
        => new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F2 5 0 R >> >> >>")
            .StreamObj(4, "<<", content)
            .Obj(5, WideFont)
            .Finish();

    /// <summary>
    /// A move to the next line puts the pen back on the line: what the face measured beyond the
    /// widths on the line before is not carried onto this one.
    /// </summary>
    [Fact]
    public void Transform_OnASecondLine_CountsOnlyThatLinesRuns()
    {
        // 0 BT, 1 Tf, 2 Td, 3 Tj A, 4 Td, 5 Tj B, 6 Tj C, 7 ET
        byte[] input = WideFixture("BT /F2 12 Tf 10 180 Td (A) Tj 0 -20 Td (B) Tj (C) Tj ET");

        (byte[] output, _) = Move(input, Shift, 6);

        var matrices = PageOps(output).Where(o => o.Operator == PdfOperators.SetTextMatrix)
                                      .Select(o => o.Operands.Select(n => Math.Round(((PdfNumber)n).DoubleValue, 3)).ToArray()).ToList();
        // C starts one letter — B's 24 points — after the second line's start at (10, 160), moved by 20 and −30.
        Assert.Equal(new[] { 1.0, 0, 0, 1, 54, 130 }, matrices[0]);
        Assert.Equal(new[] { 1.0, 0, 0, 1, 10, 160 }, matrices[1]);
        Assert.Equal(-4000, Assert.Single(PenMoves(output)), 2);
    }

    /// <summary>The numbers of the one-number <c>TJ</c> operations of a page's content, in order.</summary>
    private static List<double> PenMoves(byte[] pdf)
        => PageOps(pdf).Where(o => o.Operator == PdfOperators.ShowTextArray && o.Operands[0] is PdfArray a && a.Count == 1 && a[0] is PdfNumber)
                       .Select(o => ((PdfNumber)((PdfArray)o.Operands[0])[0]).DoubleValue).ToList();

    /// <summary>
    /// The pen is moved by what the file says the run's width is (§9.4.4: the font's own widths),
    /// not by the measure of whatever face this host drew a font that is not embedded in — so the
    /// text after the run stays where it was in any reader, and the output is the same on any host.
    /// </summary>
    [Fact]
    public void Remove_MovesThePenByTheFontsOwnWidths_NotByTheFaceItWasDrawnIn()
    {
        (byte[] output, _) = Remove(WideFixture(), 3);

        // One letter of 2000 units: a TJ number of −2000 moves the pen as far.
        Assert.Equal(-2000, Assert.Single(PenMoves(output)), 2);
        Assert.Equal("BC", ExtractText(output).Trim());
    }

    [Fact]
    public void Transform_StartsARunAndLeavesThePenByTheFontsOwnWidths()
    {
        (byte[] output, _) = Move(WideFixture(), Shift, 4);
        List<ContentStreamOperation> ops = PageOps(output);

        // B starts one letter (2000 units at 12 points: 24 points) after the line's start at 10, moved by the matrix's 20 and −30.
        var matrices = ops.Where(o => o.Operator == PdfOperators.SetTextMatrix)
                          .Select(o => o.Operands.Select(n => ((PdfNumber)n).DoubleValue).ToArray()).ToList();
        Assert.Equal(2, matrices.Count);
        Assert.Equal(new[] { 1.0, 0, 0, 1, 54, 150 }, matrices[0].Select(v => Math.Round(v, 3)));
        // Then the line is put back and the pen two letters along it, where C starts.
        Assert.Equal(new[] { 1.0, 0, 0, 1, 10, 180 }, matrices[1].Select(v => Math.Round(v, 3)));
        Assert.Equal(-4000, Assert.Single(PenMoves(output)), 2);
    }

    // ── moving and scaling ──────────────────────────────────────────────────

    private static readonly double[] Shift = { 1, 0, 0, 1, 20, -30 };

    /// <summary>The same factor both ways about the point (100, 100).</summary>
    private static readonly double[] Double = { 2, 0, 0, 2, -100, -100 };

    [Fact]
    public void Transform_OfAPath_APictureAndAForm_MovesEach_AndNothingElse()
    {
        byte[] input = Fixture();
        (byte[] output, int moved) = Move(input, Shift, Square, Picture, Form, Line);

        Assert.Equal(4, moved);
        AssertMoved(input, output, Shift, Square, Picture, Form, Line);
    }

    /// <summary>The run after a moved one is drawn where the moved run left the pen before it moved: on the line, as it was.</summary>
    [Theory]
    [InlineData(Hello)]
    [InlineData(World)]
    [InlineData(First)]
    [InlineData(Second)]     // started with ', which moves to the next line: the move stays where it was
    [InlineData(Tail)]
    public void Transform_OfARun_MovesIt_AndLeavesTheRunsRoundItWhereTheyWere(int run)
    {
        byte[] input = Fixture();
        (byte[] output, _) = Move(input, Shift, run);

        AssertMoved(input, output, Shift, run);
        // Every letter is still text: none lost, none shown twice. (Where a word ends is the
        // extractor's reading of the gaps, which a moved run changes.)
        static string Letters(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)).OrderBy(c => c));
        Assert.Equal(Letters(ExtractText(input)), Letters(ExtractText(output)));
        Assert.Equal(Letters("HelloWorldFirstSecondTail"), Letters(ExtractText(input)));
    }

    [Fact]
    public void Transform_ByAScaling_ScalesTextPicturesAndPathsAboutItsPoint()
    {
        byte[] input = Fixture();
        (byte[] output, _) = Move(input, Double, Hello, Picture, Square);

        AssertMoved(input, output, Double, Hello, Picture, Square);
    }

    /// <summary>
    /// The matrix is in the page's coordinates whatever the element is drawn under: a square and a
    /// run drawn at twice the size, and a run under a turned text matrix, each move by the matrix's
    /// own 20 across and 30 down — not twice that, and not along the turn.
    /// </summary>
    [Theory]
    [InlineData("q 2 0 0 2 20 20 cm 0 0 1 rg 10 10 5 5 re f Q\n", 4)]
    [InlineData("q 2 0 0 2 0 0 cm BT /F1 12 Tf 10 50 Td (Moved) Tj (Stays) Tj ET Q\n", 5)]
    [InlineData("BT /F1 12 Tf 0 1 -1 0 100 20 Tm (Moved) Tj (Stays) Tj ET\n", 3)]
    [InlineData("BT /F1 12 Tf 14 TL 10 180 Td (One) Tj 5 2 (Moved words) \" (Stays here) Tj ET\n", 5)]
    public void Transform_IsInThePagesCoordinates_WhateverTheElementIsDrawnUnder(string content, int id)
    {
        byte[] input = Fixture(content: content);
        (byte[] output, _) = Move(input, Shift, id);

        AssertMoved(input, output, Shift, id);
    }

    [Theory]
    [InlineData("q 0 100 60 60 re W n /Sh1 sh Q\n", 4, "shading")]
    [InlineData("0 0 100 100 re W f\n", 2, "sets the clip")]
    [InlineData("BT 4 Tr /F1 40 Tf 10 120 Td (Clip) Tj ET\n", 4, "adds to the clip")]
    public void Transform_OfWhatCannotMoveByItself_Refuses_NamingWhy(string content, int id, string why)
    {
        byte[] input = Fixture(content: content);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { id }, Shift));

        Assert.Contains(why, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
        // The control: the element is one, and is taken out without complaint.
        Assert.Equal(1, Remove(input, id).Removed);
    }

    [Fact]
    public void Transform_RefusesAMatrixThatIsNotOne()
    {
        byte[] input = Fixture();
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { Square }, new double[] { 1, 0, 0, 1, 0 }));
        Assert.Throws<ArgumentException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { Square }, new double[] { 1, 2, 2, 4, 0, 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { Square }, new[] { 1, 0, 0, 1, double.NaN, 0 }));
        // Finite, and past what a page's coordinates hold: written, it would be a 0 and the element gone.
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { Square }, new[] { 1e39, 0, 0, 1, 0, 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { Square }, new[] { 1, 0, 0, 1, 0, -1e39 }));
        Assert.Throws<ArgumentException>(() => PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { Square, 0 }, Shift));
        Assert.Throws<PdfEncryptedDocumentException>(() => PdfPageElements.Transform(new MemoryStream(Fixture(encrypted: true)), output, 0, new[] { Square }, Shift));
        Assert.Equal(0, output.Length);
        // The control: the same call with a matrix that is one is carried out.
        Assert.Equal(1, PdfPageElements.Transform(new MemoryStream(input), output, 0, new[] { Square }, Shift));
    }

    [Fact]
    public void Transform_OfNothing_CopiesTheFileThrough()
    {
        byte[] input = Fixture();
        using var output = new MemoryStream();

        Assert.Equal(0, PdfPageElements.Transform(new MemoryStream(input), output, 0, Array.Empty<int>(), Shift));
        Assert.Equal(input, output.ToArray());
    }

    // ── refusals ────────────────────────────────────────────────────────────

    /// <summary>An id that names no element — an operation that does not paint, or one past the end — is the caller's mistake, and nothing is written.</summary>
    [Theory]
    [InlineData(0)]      // BT
    [InlineData(11)]     // the square's re: construction, not paint
    [InlineData(9999)]
    [InlineData(-1)]
    public void Remove_OfAnIdThatNamesNoElement_Refuses(int id)
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() => PdfPageElements.Remove(new MemoryStream(Fixture()), output, 0, new[] { Square, id }));

        Assert.Equal("elementIds", ex.ParamName);
        Assert.Equal(0, output.Length);
        // The control: the same call without the stray id is carried out.
        Assert.Equal(1, Remove(Fixture(), Square).Removed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void APageTheDocumentDoesNotHave_IsRefused(int pageIndex)
    {
        // Refused as the page index it is, by name — not by whatever a list says of a bad index.
        var listing = Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageElements.List(new MemoryStream(Fixture()), pageIndex));
        Assert.Equal("pageIndex", listing.ParamName);
        using var output = new MemoryStream();
        var removing = Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageElements.Remove(new MemoryStream(Fixture()), output, pageIndex, new[] { Square }));
        Assert.Equal("pageIndex", removing.ParamName);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AnEncryptedDocument_IsRefused()
    {
        Assert.Throws<PdfEncryptedDocumentException>(() => PdfPageElements.List(new MemoryStream(Fixture(encrypted: true)), 0));
        using var output = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfPageElements.Remove(new MemoryStream(Fixture(encrypted: true)), output, 0, new[] { Square }));
        Assert.Equal(0, output.Length);
    }

    /// <summary>The facade reaches both doors with the same answers.</summary>
    [Fact]
    public void TheFacade_ListsAndRemoves()
    {
        byte[] input = Fixture();
        Assert.Equal(Elements(input).Select(e => e.Id), PdfOperations.ListPageElements(new MemoryStream(input), 0).Select(e => e.Id));

        using var output = new MemoryStream();
        Assert.Equal(1, PdfOperations.RemovePageElements(new MemoryStream(input), output, 0, new[] { Square }));
        Assert.DoesNotContain(Elements(output.ToArray()), e => Math.Abs(e.X - 150) < 1 && Math.Abs(e.Y - 150) < 1);

        using var edited = new MemoryStream();
        TemplateReplaceResult result = PdfOperations.ReplacePageElementText(new MemoryStream(input), edited, 0, new[] { Hello }, "Bye",
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Bye") });
        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        Assert.Contains("Bye", ExtractText(edited.ToArray()), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private const string EncryptTrailer =
        " /Encrypt 9 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]";

    private const string EncryptDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
        "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
        "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    /// <summary>Object 3 is the page, 4 its content, 5 the picture, 6 the form, 8 the font, 10 and 11 the Type 3 font and its glyph.</summary>
    private static byte[] Fixture(bool encrypted = false, string content = PageContent)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                    "/Resources << /Font << /F1 8 0 R /F3 10 0 R >> /XObject << /Im1 5 0 R /Fx1 6 0 R >> /Shading << /Sh1 7 0 R >> >> >>")
            .StreamObj(4, "<<", content)
            .Obj(5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>",
                 new byte[] { 0, 0, 255 })
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200]", FormContent)
            .Obj(7, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 1 1] /N 1 >> /Extend [true true] >>")
            .Obj(8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
            // A Type 3 font of one glyph, a square, 100 units wide in a 1000-unit em a tenth the size.
            .Obj(10, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] " +
                     "/CharProcs << /a 11 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> " +
                     "/FirstChar 97 /LastChar 97 /Widths [100] >>")
            .StreamObj(11, "<<", "100 0 d0 10 10 80 80 re f");
        if (!encrypted) return b.Finish();
        return b.Obj(9, EncryptDictionary).Finish(EncryptTrailer);
    }

    private static IReadOnlyList<PdfPageElement> Elements(byte[] pdf) => PdfPageElements.List(new MemoryStream(pdf), 0);

    private static (byte[] Pdf, int Removed) Remove(byte[] pdf, params int[] ids)
    {
        using var output = new MemoryStream();
        int removed = PdfPageElements.Remove(new MemoryStream(pdf), output, 0, ids);
        return (output.ToArray(), removed);
    }

    private static (byte[] Pdf, int Moved) Move(byte[] pdf, double[] matrix, params int[] ids)
    {
        using var output = new MemoryStream();
        int moved = PdfPageElements.Transform(new MemoryStream(pdf), output, 0, ids, matrix);
        return (output.ToArray(), moved);
    }

    /// <summary>
    /// The output draws what the input drew, in the same order: the elements named in
    /// <paramref name="moved"/> where <paramref name="matrix"/> (a scaling and a move) takes their
    /// boxes, every other where it was — measured afresh from the output.
    /// </summary>
    private static void AssertMoved(byte[] input, byte[] output, double[] matrix, params int[] moved)
    {
        IReadOnlyList<PdfPageElement> before = Elements(input), now = Elements(output);

        Assert.Equal(before.Select(e => e.Kind), now.Select(e => e.Kind));
        for (int i = 0; i < before.Count; i++)
        {
            PdfPageElement was = before[i];
            if (moved.Contains(was.Id))
                AssertBox(now[i], matrix[0] * was.X + matrix[4], matrix[3] * was.Y + matrix[5], matrix[0] * was.Width, matrix[3] * was.Height, tolerance: 0.1);
            else
                AssertBox(now[i], was.X, was.Y, was.Width, was.Height, tolerance: 0.05);
        }
    }

    private static void AssertBox(PdfPageElement element, double x, double y, double width, double height, double tolerance = 0.6)
    {
        Assert.InRange(element.X, x - tolerance, x + tolerance);
        Assert.InRange(element.Y, y - tolerance, y + tolerance);
        Assert.InRange(element.Width, width - 2 * tolerance, width + 2 * tolerance);
        Assert.InRange(element.Height, height - 2 * tolerance, height + 2 * tolerance);
    }

    /// <summary>
    /// Every element of <paramref name="input"/> but those removed is in <paramref name="output"/>,
    /// in the same order, of the same kind, in the same box — measured afresh from the output.
    /// </summary>
    private static void AssertTheRestIsWhereItWas(byte[] input, byte[] output, params int[] removed)
    {
        List<PdfPageElement> kept = Elements(input).Where(e => !removed.Contains(e.Id)).ToList();
        IReadOnlyList<PdfPageElement> now = Elements(output);

        Assert.Equal(kept.Select(e => e.Kind), now.Select(e => e.Kind));
        for (int i = 0; i < kept.Count; i++)
            AssertBox(now[i], kept[i].X, kept[i].Y, kept[i].Width, kept[i].Height, tolerance: 0.05);
    }

    private static List<ContentStreamOperation> PageOps(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
    }

    private static List<string> Operators(byte[] pdf) => PageOps(pdf).Select(o => o.Operator).ToList();

    private static string ExtractText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return PdfTextExtractor.ExtractText(doc);
    }

    /// <summary>Whether any stream the file's cross-reference table lists decodes to bytes holding <paramref name="text"/>.</summary>
    private static bool AnyStreamHolds(byte[] pdf, string text)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        int max = doc.CrossReferenceTable.GetMaxObjectNumber();
        for (int n = 1; n <= max; n++)
            if (doc.GetObject(n) is PdfStream stream
                && Encoding.Latin1.GetString(stream.GetDecodedData() ?? Array.Empty<byte>()).Contains(text, StringComparison.Ordinal))
                return true;
        return false;
    }
}
