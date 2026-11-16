// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf;
using Xunit;

using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A numbered run of DOCX paragraphs becomes a <see cref="M.ListBlock"/> in the Builder model,
    /// numbered from <c>numbering.xml</c> and nested by <c>w:ilvl</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The Builder-model path drew a hard-coded bullet.</b> Every paragraph carrying
    /// <c>w:numPr</c> got <c>"• "</c> prepended and an indent derived from its level, whatever the
    /// numbering definitions said — so an ordered list converted to a bulleted one, a multi-level
    /// <c>%1.%2</c> scheme lost both of its numbers, and nothing in the model distinguished a list
    /// from a run of indented paragraphs. The numbering was already read correctly one output path
    /// over, in <c>Rendering.ListRenderer</c>; this reaches the same reader rather than starting a
    /// second, which is what stops the two paths from drifting apart again.</para>
    ///
    /// <para><b>OOXML has no list element</b>, so the grouping is reconstructed: a list is a run of
    /// consecutive paragraphs sharing a <c>w:numId</c>, each carrying its own absolute level. That
    /// is why the cases below are mostly about <i>boundaries</i> — where a list starts, where it
    /// stops, and which item a deeper one belongs to — rather than about a single paragraph.</para>
    ///
    /// <para>Assertions read the mapped model. The exported page cannot tell a list from indented
    /// paragraphs with markers drawn in front of them, which is precisely the state this replaces.</para>
    /// </remarks>
    public class DocxListContainerTests
    {
        /// <summary>Consecutive numbered paragraphs become one list.</summary>
        [Fact]
        public void ConsecutiveNumberedParagraphsBecomeOneList()
        {
            M.ListBlock list = FirstList(Decimal(items: 3));

            Assert.Equal(3, list.Items.Count);
        }

        /// <summary>A decimal level is an ordered list, and its markers are its numbers.</summary>
        /// <remarks>
        /// <b>Both halves matter and only one is visible on the page.</b> The markers are what a
        /// reader sees; <c>IsOrdered</c> is what lets an exporter write <c>&lt;ol&gt;</c> rather
        /// than <c>&lt;ul&gt;</c>, and the marker string cannot carry it — a level whose text is
        /// "1." and a bullet that happens to be the character "1" produce the same prefix.
        /// </remarks>
        [Fact]
        public void ADecimalLevelIsOrderedAndNumbered()
        {
            M.ListBlock list = FirstList(Decimal(items: 3));

            Assert.True(list.IsOrdered);
            Assert.Equal(new[] { "1. ", "2. ", "3. " }, list.Items.Select(i => i.Marker));
        }

        /// <summary>A bullet level is not ordered.</summary>
        /// <remarks>
        /// The acceptance control: without it, "a decimal list is ordered" is equally satisfied by
        /// marking every list ordered — which is exactly what the old hard-coded path did in
        /// reverse, marking every list bulleted.
        /// </remarks>
        [Fact]
        public void ABulletLevelIsNotOrdered()
        {
            M.ListBlock list = FirstList(BuildListDocx(
                new[] { (0, NumberFormatValues.Bullet, 1, "•") },
                new[] { (0, "one"), (0, "two") }));

            Assert.False(list.IsOrdered);
        }

        /// <summary>Roman and alphabetic levels keep their own numbering.</summary>
        /// <remarks>
        /// The case the hard-coded bullet destroyed most visibly: these are ordered lists that came
        /// out as identical bullets, so the document lost its sequence entirely.
        /// </remarks>
        [Theory]
        [InlineData("lowerRoman", "i. ", "ii. ")]
        [InlineData("upperRoman", "I. ", "II. ")]
        [InlineData("lowerLetter", "a. ", "b. ")]
        [InlineData("upperLetter", "A. ", "B. ")]
        public void ANonDecimalLevelKeepsItsOwnNumbering(
            string format, string first, string second)
        {
            // NumberFormatValues is a struct in this OpenXML version, so it cannot be an
            // [InlineData] constant; the row names the format and the body resolves it.
            NumberFormatValues fmt = format switch
            {
                "lowerRoman" => NumberFormatValues.LowerRoman,
                "upperRoman" => NumberFormatValues.UpperRoman,
                "lowerLetter" => NumberFormatValues.LowerLetter,
                _ => NumberFormatValues.UpperLetter,
            };

            M.ListBlock list = FirstList(BuildListDocx(
                new[] { (0, fmt, 1, "%1.") },
                new[] { (0, "one"), (0, "two") }));

            Assert.Equal(new[] { first, second }, list.Items.Select(i => i.Marker));
        }

        /// <summary>A deeper level hangs off the item above it.</summary>
        [Fact]
        public void ADeeperLevelBecomesASublistOfTheItemAboveIt()
        {
            M.ListBlock list = FirstList(BuildListDocx(
                new[]
                {
                    (0, NumberFormatValues.Decimal, 1, "%1."),
                    (1, NumberFormatValues.Decimal, 1, "%1.%2."),
                },
                new[] { (0, "one"), (1, "one-a"), (0, "two") }));

            Assert.Equal(2, list.Items.Count);            // the two level-0 items
            Assert.NotNull(list.Items[0].Sublist);
            Assert.Single(list.Items[0].Sublist!.Items);
            Assert.Null(list.Items[1].Sublist);
        }

        /// <summary>A multi-level scheme numbers each level in its own format.</summary>
        /// <remarks>
        /// <c>%1.%2</c> is the shape the old path could not represent at all: it drew one bullet
        /// and neither number. The nested item's marker is the evidence that both counters are
        /// being tracked, not just the innermost.
        /// </remarks>
        [Fact]
        public void AMultiLevelSchemeNumbersEveryLevel()
        {
            M.ListBlock list = FirstList(BuildListDocx(
                new[]
                {
                    (0, NumberFormatValues.Decimal, 1, "%1."),
                    (1, NumberFormatValues.Decimal, 1, "%1.%2."),
                },
                new[] { (0, "one"), (1, "one-a"), (1, "one-b") }));

            Assert.Equal("1. ", list.Items[0].Marker);
            Assert.Equal(new[] { "1.1. ", "1.2. " },
                         list.Items[0].Sublist!.Items.Select(i => i.Marker));
        }

        /// <summary>An ordinary paragraph ends the list.</summary>
        /// <remarks>
        /// Where a run of numbered paragraphs stops is the only thing that says where the list
        /// stops, since OOXML records no end. A grouping that never closed would swallow the rest
        /// of the document.
        /// </remarks>
        [Fact]
        public void AnOrdinaryParagraphEndsTheList()
        {
            List<M.IDocElement> children = Children(BuildListDocx(
                new[] { (0, NumberFormatValues.Decimal, 1, "%1.") },
                new[] { (0, "one"), (0, "two") },
                trailingParagraph: "after"));

            Assert.Equal(2, children.Count);
            Assert.IsType<M.ListBlock>(children[0]);
            Assert.IsType<M.Paragraph>(children[1]);
        }

        /// <summary>A document ending on a list item still gets its list.</summary>
        /// <remarks>
        /// The failure a flush-on-interruption design has and a flush-at-the-end design does not:
        /// with nothing after the last item, the open group is never closed and the whole list is
        /// dropped. Every other test here would still pass.
        /// </remarks>
        [Fact]
        public void ADocumentEndingOnAListItemStillGetsItsList()
        {
            List<M.IDocElement> children = Children(Decimal(items: 2));

            Assert.Single(children);
            Assert.IsType<M.ListBlock>(children[0]);
        }

        /// <summary>Two adjacent numbering ids are two lists.</summary>
        /// <remarks>
        /// Merging them would put one sequence's items under the other's container and renumber
        /// both — and with no paragraph between them, nothing else marks the boundary.
        /// </remarks>
        [Fact]
        public void TwoAdjacentNumberingIdsAreTwoLists()
        {
            List<M.IDocElement> children = Children(TwoListsBackToBack());

            Assert.Equal(2, children.OfType<M.ListBlock>().Count());
        }

        /// <summary>The item's text is the paragraph's, and no marker run is prepended.</summary>
        /// <remarks>
        /// <b>The old path wrote the marker as a literal run inside the paragraph.</b> If that
        /// survived alongside the container's marker the item would be drawn with two, which no
        /// structural assertion elsewhere in this file would notice.
        /// </remarks>
        [Fact]
        public void TheItemCarriesItsOwnTextAndNoLiteralMarkerRun()
        {
            M.ListBlock list = FirstList(Decimal(items: 1));

            string text = string.Concat(list.Items[0].Runs.OfType<M.TextRun>().Select(r => r.Text));

            Assert.Equal("item0", text);
        }

        /// <summary>
        /// A level that skips one and then comes back keeps both depths under the same item.
        /// </summary>
        /// <remarks>
        /// <para><b>Word allows a level jump</b> — <c>w:ilvl</c> is an absolute depth, not a
        /// step — so 0, 2, 1 is a real document, and the two deeper paragraphs belong to the
        /// same level-0 item. Returning to a shallower-but-still-deeper level is the <i>only</i>
        /// way to reach an item that already has a sublist, because any paragraph at the item's own
        /// level would start a new item instead.</para>
        ///
        /// <para><b>Which is why this exists.</b> The grouping reuses an item's existing sublist
        /// rather than replacing it, and replacing it changed no other test in this file: every
        /// other fixture steps one level at a time, and on that path an item's sublist is always
        /// null when it is created. Under the mutation the level-2 paragraph is silently dropped
        /// — content loss, from a document that is not even malformed.</para>
        /// </remarks>
        [Fact]
        public void ALevelJumpAndBackKeepsBothDepthsUnderTheSameItem()
        {
            M.ListBlock list = FirstList(BuildListDocx(
                new[]
                {
                    (0, NumberFormatValues.Decimal, 1, "%1."),
                    (1, NumberFormatValues.Decimal, 1, "%2."),
                    (2, NumberFormatValues.Decimal, 1, "%3."),
                },
                new[] { (0, "top"), (2, "deep"), (1, "middle") }));

            Assert.Single(list.Items);

            M.ListBlock sub = list.Items[0].Sublist;
            Assert.NotNull(sub);

            // Both deeper paragraphs are under the one top-level item, in document order. The
            // level-2 one is the paragraph the replace-rather-than-reuse mutation loses.
            Assert.Equal(
                new[] { "deep", "middle" },
                sub!.Items.Select(i => string.Concat(
                    i.Runs.OfType<M.TextRun>().Select(r => r.Text))));
        }

        // ==================================================================== helpers

        private static M.ListBlock FirstList(byte[] docx)
        {
            M.ListBlock found = Children(docx).OfType<M.ListBlock>().FirstOrDefault();

            Assert.True(found != null, "the mapper produced no list container at all");
            return found!;
        }

        private static List<M.IDocElement> Children(byte[] docx)
        {
            using var ms = new MemoryStream(docx);
            using WordprocessingDocument doc = WordprocessingDocument.Open(ms, false);

            M.Document model = DocxToBuilderMapper.Map(doc, new DocxToPdfOptions());

            Assert.True(model.Sections.Count > 0, "the mapper produced no section");
            return model.Sections[0].Children;
        }

        private static byte[] Decimal(int items)
            => BuildListDocx(
                new[] { (0, NumberFormatValues.Decimal, 1, "%1.") },
                Enumerable.Range(0, items).Select(i => (0, "item" + i)).ToArray());

        /// <summary>One numbering definition, one list, optionally followed by a paragraph.</summary>
        private static byte[] BuildListDocx(
            (int ilvl, NumberFormatValues fmt, int start, string levelText)[] levels,
            (int ilvl, string text)[] items,
            string trailingParagraph = null)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                MainDocumentPart mainPart = doc.AddMainDocumentPart();

                var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
                var abstractNum = new AbstractNum { AbstractNumberId = 0 };
                foreach ((int ilvl, NumberFormatValues fmt, int start, string levelText) in levels)
                {
                    abstractNum.Append(new Level(
                        new StartNumberingValue { Val = start },
                        new NumberingFormat { Val = fmt },
                        new LevelText { Val = levelText })
                    { LevelIndex = ilvl });
                }
                var instance = new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 };
                numberingPart.Numbering = new Numbering(abstractNum, instance);
                numberingPart.Numbering.Save();

                var body = new Body();
                foreach ((int ilvl, string text) in items)
                {
                    body.Append(new Paragraph(
                        new ParagraphProperties(new NumberingProperties(
                            new NumberingLevelReference { Val = ilvl },
                            new NumberingId { Val = 1 })),
                        new Run(new Text(text))));
                }

                if (trailingParagraph != null)
                    body.Append(new Paragraph(new Run(new Text(trailingParagraph))));

                mainPart.Document = new Document(body);
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        /// <summary>Two lists with different numbering ids and no paragraph between them.</summary>
        private static byte[] TwoListsBackToBack()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                MainDocumentPart mainPart = doc.AddMainDocumentPart();
                var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();

                var numbering = new Numbering();
                for (int i = 0; i < 2; i++)
                {
                    numbering.Append(new AbstractNum(new Level(
                        new StartNumberingValue { Val = 1 },
                        new NumberingFormat { Val = NumberFormatValues.Decimal },
                        new LevelText { Val = "%1." })
                    { LevelIndex = 0 })
                    { AbstractNumberId = i });
                }
                for (int i = 0; i < 2; i++)
                {
                    numbering.Append(new NumberingInstance(new AbstractNumId { Val = i })
                    { NumberID = i + 1 });
                }
                numberingPart.Numbering = numbering;
                numberingPart.Numbering.Save();

                var body = new Body();
                foreach (int numId in new[] { 1, 1, 2, 2 })
                {
                    body.Append(new Paragraph(
                        new ParagraphProperties(new NumberingProperties(
                            new NumberingLevelReference { Val = 0 },
                            new NumberingId { Val = numId })),
                        new Run(new Text("n" + numId))));
                }

                mainPart.Document = new Document(body);
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }
    }
}
