// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The two contents-block residuals that depend on the <b>section</b>: a bare <c>H</c> heading is
/// placed at the depth its section nesting gives it, and an entry's page number is written in the
/// format its section declares.
/// </summary>
/// <remarks>
/// <para><b>A bare <c>H</c> is PDF/UA's strongly structured heading</b>, whose level is how many
/// grouping elements enclose it rather than a digit in its name (ISO 32000-1 §14.8.4.3.2). This
/// model nests exactly one: every <see cref="Section"/> is written as a <c>/Sect</c> under the
/// document root and nothing else is a section — a table cell is a cell. So the level the model
/// carries for a bare <c>H</c> is 1, and the entry is placed there rather than dropped, which is
/// what the tag tree already says about it.</para>
///
/// <para><b>The page number takes the section's format</b> — <see cref="Section.PageNumberFormat"/>
/// and <see cref="Section.PageNumberStart"/> have been on the model all along, documented as the
/// format of PAGE fields, and the PDF exporter read neither: the footer of a lower-Roman front
/// matter section said "3". One formatter now serves the <c>{{PAGE}}</c> field and the contents
/// entry, so the two cannot disagree about what page a heading is on.</para>
/// </remarks>
public sealed class TocSectionNumberingTests
{
    // ==================================================================== bare H

    /// <summary>A bare <c>H</c> in a section is an entry at level 1.</summary>
    [Fact]
    public void ABareStrongStructureHeading_IsPlacedAtItsSectionDepth()
    {
        Document doc = WithToc(maxLevel: 6, Bare("Unlevelled"));

        TocEntries.Entry entry = Assert.Single(TocEntries.Collect(doc, 6));

        Assert.Equal("Unlevelled", entry.Text);
        Assert.Equal(1, entry.Level);
    }

    /// <summary>A table is not a section, so a bare <c>H</c> inside a cell is still level 1.</summary>
    [Fact]
    public void ABareHeadingInsideATableCell_IsStillAtTheSectionDepth()
    {
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Paragraphs.Add(Bare("Nested"));
        row.Cells.Add(cell);
        table.Rows.Add(row);

        Assert.Equal(1, Assert.Single(TocEntries.Collect(WithToc(maxLevel: 3, table), 3)).Level);
    }

    /// <summary>A contents limited to nothing (level 0) lists no bare heading either.</summary>
    /// <remarks>
    /// The acceptance control for the placement: a bare heading placed at level 1 is subject to
    /// the same depth limit as an <c>H1</c>, so it must not appear in a contents that admits none.
    /// </remarks>
    [Fact]
    public void ABareHeading_RespectsTheDepthLimitLikeAnyOtherLevelOneHeading()
        => Assert.Empty(TocEntries.Collect(WithToc(maxLevel: 0, Bare("Unlevelled")), 0));

    /// <summary>The PDF gives a bare heading's entry a page number, not a blank.</summary>
    /// <remarks>
    /// The page map is filled by the paragraph renderer when it meets a heading; a bare <c>H</c>
    /// that is collected but never recorded gets an entry with no number, which is worse than no
    /// entry — it looks like a heading on page "".
    /// </remarks>
    [Fact]
    public void ThePdf_NumbersABareHeadingsEntry()
    {
        Document doc = WithToc(maxLevel: 6, Bare("Unlevelled"));

        Assert.Equal(new[] { "1" }, TocNumbers(doc));
    }

    // ==================================================================== formats

    /// <summary>An entry's page number is written in its section's format.</summary>
    [Theory]
    [InlineData("lowerRoman", "i")]
    [InlineData("upperRoman", "I")]
    [InlineData("lowerLetter", "a")]
    [InlineData("upperLetter", "A")]
    [InlineData("decimal", "1")]
    public void AnEntrysPageNumber_TakesTheSectionsFormat(string format, string expected)
    {
        Document doc = WithToc(maxLevel: 1, Head(1, "Alpha"));
        doc.Sections[0].PageNumberFormat = format;

        Assert.Equal(new[] { expected }, TocNumbers(doc));
    }

    /// <summary>A section that restarts its numbering restarts the entries' numbers too.</summary>
    [Fact]
    public void AnEntrysPageNumber_CountsFromTheSectionsStart()
    {
        Document doc = WithToc(maxLevel: 1, Head(1, "Alpha"));
        doc.Sections[0].PageNumberFormat = "lowerRoman";
        doc.Sections[0].PageNumberStart = 4;

        Assert.Equal(new[] { "iv" }, TocNumbers(doc));
    }

    /// <summary>The <c>{{PAGE}}</c> field and the contents entry come from one formatter.</summary>
    /// <remarks>
    /// A footer saying "3" under a contents saying "iii" is the disagreement a shared formatter
    /// exists to prevent, so the field is asserted in the same fixture as the entry.
    /// </remarks>
    [Fact]
    public void ThePageField_HonoursTheSectionsFormatAndStart()
    {
        // No contents block in this fixture: its entry would draw the same "iv" and satisfy the
        // assertion with the footer still saying "4".
        Document doc = TwoSections();
        doc.Sections.RemoveAt(1);
        doc.Sections[0].PageNumberFormat = "lowerRoman";
        doc.Sections[0].PageNumberStart = 4;
        doc.Sections[0].Footer = FooterWith("Page " + PdfExporter.PagePlaceholder);

        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);

        Assert.Contains(drawn, d => d.Text == "iv");
        Assert.DoesNotContain(drawn, d => d.Text == "4");
    }

    /// <summary>A section with no start continues the previous section's count.</summary>
    /// <remarks>
    /// Word's rule for <c>w:pgNumType</c> without <c>w:start</c>, and the model's default — so a
    /// document that never sets either draws exactly the global page number it always did.
    /// </remarks>
    [Fact]
    public void ASectionWithoutAStart_ContinuesThePreviousSectionsCount()
    {
        Document doc = TwoSections();
        doc.Sections[1].Footer = FooterWith(PdfExporter.PagePlaceholder);

        Assert.Contains(Drawn(doc), d => d.Text == "2");
    }

    /// <summary>A section with a start restarts the count there.</summary>
    [Fact]
    public void ASectionWithAStart_RestartsTheCount()
    {
        Document doc = TwoSections();
        doc.Sections[1].PageNumberStart = 1;
        doc.Sections[1].Footer = FooterWith(PdfExporter.PagePlaceholder);

        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);

        Assert.Contains(drawn, d => d.Text == "1");
        Assert.DoesNotContain(drawn, d => d.Text == "2");
    }

    // ==================================================================== right to left

    /// <summary>The <c>{{PAGE}}</c> and <c>{{NUMPAGES}}</c> fields in a right-to-left footer are replaced, not drawn.</summary>
    /// <remarks>
    /// A field's braces resolve to the paragraph's odd level and its name to the even one above it,
    /// so the bidi pass cut the token into <c>{{</c>, <c>PAGE</c> and <c>}}</c>, which the
    /// replacement never matched — a Hebrew footer showed the placeholder itself. The control is the
    /// same footer left to right, which was always replaced.
    /// </remarks>
    [Theory]
    [InlineData(TextDirection.RightToLeft)]
    [InlineData(TextDirection.LeftToRight)]
    public void ThePageFields_AreReplacedInAFooterOfEitherDirection(TextDirection direction)
    {
        Document doc = TwoSections();
        doc.Sections.RemoveAt(1);
        doc.Sections[0].Direction = direction;
        doc.Sections[0].PageNumberStart = 7;
        doc.Sections[0].Footer = FooterWith(PdfExporter.PagePlaceholder + " " + PdfExporter.NumPagesPlaceholder);
        doc.Sections[0].Footer!.Paragraphs[0].Direction = direction;

        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);

        Assert.Contains(drawn, d => d.Text == "7");     // {{PAGE}}, from the section's start
        Assert.Contains(drawn, d => d.Text == "1");     // {{NUMPAGES}}, the one page
        Assert.DoesNotContain(drawn, d => d.Text.Contains("PAGE", StringComparison.Ordinal));
        Assert.DoesNotContain(drawn, d => d.Text.Contains("{", StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>{{NUMPAGES}}</c> field in a run naming a provider-supplied family is replaced: the field is
    /// found in the finished content as a literal, which a run shaped into the family's glyph ids has none
    /// of, so it is drawn in the standard face whatever the run names. The control is the word beside it,
    /// drawn as a glyph run of the named family.
    /// </summary>
    [Fact]
    public void TheNumPagesField_InARunNamingAProvidedFamily_IsReplaced()
    {
        Document doc = TwoSections();
        var footer = new Footer();
        var line = new Paragraph();
        line.Runs.Add(new TextRun { FontName = "TestFace", Text = "of " + PdfExporter.NumPagesPlaceholder });
        footer.Paragraphs.Add(line);
        doc.Sections[0].Footer = footer;
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build("of {}NUMPAGES"),
        };

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(options));
        List<PdfContentProbe.DrawnText> drawn = PdfContentProbe.DrawnTexts(content);

        Assert.Contains(drawn, d => d.Text == "2");                                   // the two sections' pages
        Assert.DoesNotContain(drawn, d => d.Text.Contains("NUMPAGES", StringComparison.Ordinal));
        Assert.Contains(PdfContentProbe.DrawnGlyphRuns(content), run => run.Gids.Length == 2);   // "of", in the family
    }

    /// <summary>A page field glued to Hebrew — "עמוד{{PAGE}}" — is replaced as well.</summary>
    /// <remarks>
    /// Glued, the field was inside one word, which the bidi pass cut at the Hebrew's edge and
    /// again inside the braces; it is now a token of its own, as <c>{{NUMPAGES}}</c> already was.
    /// </remarks>
    [HebrewCapableFontFact]
    public void ThePageField_GluedToHebrew_IsReplaced()
    {
        Document doc = TwoSections();
        doc.Sections.RemoveAt(1);
        doc.Sections[0].Direction = TextDirection.RightToLeft;
        doc.Sections[0].PageNumberStart = 7;
        doc.Sections[0].Footer = FooterWith("\u05E2\u05DE\u05D5\u05D3" + PdfExporter.PagePlaceholder);
        doc.Sections[0].Footer!.Paragraphs[0].Direction = TextDirection.RightToLeft;

        string text = ExtractedText(doc);

        Assert.Contains("7", text, StringComparison.Ordinal);
        Assert.Contains("\u05E2\u05DE\u05D5\u05D3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PAGE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page number in Hebrew (<c>hebrew1</c>) is drawn in Hebrew though nothing else in the
    /// document is: the exporter decides once whether any text needs an embedded font, and a page
    /// number, made at draw time, was not counted — the field came out as "??".
    /// </summary>
    [HebrewCapableFontFact]
    public void AHebrewPageNumber_IsDrawnInHebrewWhenNothingElseIs()
    {
        Document doc = TwoSections();
        doc.Sections.RemoveAt(1);
        doc.Sections[0].PageNumberFormat = "hebrew1";
        doc.Sections[0].PageNumberStart = 15;
        doc.Sections[0].Footer = FooterWith(PdfExporter.PagePlaceholder);

        string text = ExtractedText(doc);

        Assert.Contains("\u05D8\u05D5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("?", text, StringComparison.Ordinal);
        Assert.Contains("first", text, StringComparison.Ordinal);   // potency: the page was read
    }

    // ==================================================================== helpers

    private static string ExtractedText(Document doc)
    {
        using var pdf = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(Pdf(doc)));
        return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(pdf);
    }

    private static Paragraph Bare(string text)
    {
        var p = new Paragraph { Role = "H" };
        p.Runs.Add(new TextRun { Text = text, FontSize = 16, IsBold = true });
        return p;
    }

    private static Paragraph Head(int level, string text)
    {
        var p = new Paragraph { Role = "H" + level.ToString() };
        p.Runs.Add(new TextRun { Text = text, FontSize = 16, IsBold = true });
        return p;
    }

    private static Paragraph Body(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static Footer FooterWith(string text)
    {
        var footer = new Footer();
        footer.Paragraphs.Add(Body(text));
        return footer;
    }

    private static Document WithToc(int maxLevel, params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(new TocBlock { Title = "Contents", MaxLevel = maxLevel });
        foreach (IDocElement child in children) section.Children.Add(child);
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>Two one-page sections with a body line each, and no footer on the first.</summary>
    private static Document TwoSections()
    {
        var doc = new Document();
        foreach (string text in new[] { "first", "second" })
        {
            var section = new Section();
            section.Children.Add(Body(text));
            doc.Sections.Add(section);
        }
        return doc;
    }

    private static byte[] Pdf(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsPdf(ms);
        return ms.ToArray();
    }

    private static List<PdfContentProbe.DrawnText> Drawn(Document doc)
        => PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(Pdf(doc)));

    /// <summary>The page numbers the contents block drew, in entry order.</summary>
    /// <remarks>
    /// The number is the only text drawn right-aligned at the margin after a leader, so it is the
    /// draw furthest to the right on its line — which is how a reader finds it too. Unlike the
    /// digit-only filter in <c>TableOfContentsTests</c>, this accepts letters, because a Roman
    /// numeral is the thing under test.
    /// </remarks>
    private static string[] TocNumbers(Document doc)
    {
        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);
        if (drawn.Count == 0) return Array.Empty<string>();

        double rightmost = drawn.Max(d => d.X);

        return drawn
            .Where(d => d.X > rightmost - 1 && d.Text.Length > 0 && d.Text.All(char.IsLetterOrDigit))
            .Select(d => d.Text)
            .ToArray();
    }
}
