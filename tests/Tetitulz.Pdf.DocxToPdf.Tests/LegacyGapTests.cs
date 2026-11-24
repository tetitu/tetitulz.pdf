// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Regression tests for the legacy (default) renderer gap-closure work:
    /// /Info metadata, cell shading, border styles, and page-number formats.
    /// Conversions use uncompressed content streams so operators can be asserted directly.
    /// </summary>
    public class LegacyGapTests
    {
        private static readonly DocxToPdfOptions NoCompress = new DocxToPdfOptions { CompressStreams = false };

        private static byte[] Convert(Action<WordprocessingDocument, Body> build)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                build(doc, body);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return DocxToPdfConverter.ConvertToBytes(ms.ToArray(), NoCompress);
        }

        [Fact]
        public void Metadata_FromCoreProperties_WrittenToInfoDict()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                doc.PackageProperties.Title = "My Legacy Title";
                doc.PackageProperties.Creator = "Author X";
                doc.PackageProperties.Subject = "Subj";
                body.Append(new Paragraph(new Run(new Text("hi"))));
            });
            string raw = Encoding.Latin1.GetString(pdf);
            Assert.Contains("/Info", raw);
            Assert.Contains("/Producer", raw);
            Assert.Contains("My Legacy Title", raw);
            Assert.Contains("Author X", raw);
        }

        [Fact]
        public void CellShading_EmitsFill()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                var cell = new TableCell(
                    new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "FFCC00" }),
                    new Paragraph(new Run(new Text("c"))));
                body.Append(new Table(new TableRow(cell)));
            });
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Matches(@"re\s+f", content);  // filled cell-background rectangle
        }

        [Fact]
        public void BorderStyle_Dashed_EmitsDashArray()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                var cell = new TableCell(
                    new TableCellProperties(new TableCellBorders(
                        new TopBorder { Val = BorderValues.Dashed, Size = 8, Color = "000000" },
                        new BottomBorder { Val = BorderValues.Dashed, Size = 8, Color = "000000" },
                        new LeftBorder { Val = BorderValues.Dashed, Size = 8, Color = "000000" },
                        new RightBorder { Val = BorderValues.Dashed, Size = 8, Color = "000000" })),
                    new Paragraph(new Run(new Text("c"))));
                body.Append(new Table(new TableRow(cell)));
            });
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Contains("0 d\n", content);  // PDF dash-pattern operator
        }

        [Fact]
        public void RightTab_WithDotLeader_DrawsLeaderAndAdvances()
        {
            // A ToC-style line: "Chapter" <tab right@300pt dot-leader> "5".
            byte[] pdf = Convert((doc, body) =>
            {
                var pPr = new ParagraphProperties(new Tabs(new TabStop
                { Val = TabStopValues.Right, Position = 6000, Leader = TabStopLeaderCharValues.Dot }));
                body.Append(new Paragraph(pPr, new Run(
                    new Text("Chapter") { Space = SpaceProcessingModeValues.Preserve },
                    new TabChar(),
                    new Text("5") { Space = SpaceProcessingModeValues.Preserve })));
            });
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Contains("....", content);  // dotted leader fills the tab gap
            Assert.Contains("TJ", content);    // exact-advance position adjustment
        }

        /// <summary>
        /// A dot-leader tab whose stop is two billion twips away draws its leader across the page and
        /// no further: one string of the tab's width over a dot's was some forty million characters
        /// (Wave 22, DocxToPdf-3:0). The leader is still drawn, the page's width of it.
        /// </summary>
        [Fact]
        public void ATabStopBeyondThePage_DrawsItsLeaderOnlyAcrossThePage()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                var pPr = new ParagraphProperties(new Tabs(new TabStop
                { Val = TabStopValues.Left, Position = int.MaxValue, Leader = TabStopLeaderCharValues.Dot }));
                body.Append(new Paragraph(pPr, new Run(
                    new Text("a") { Space = SpaceProcessingModeValues.Preserve },
                    new TabChar(),
                    new Text("b") { Space = SpaceProcessingModeValues.Preserve })));
            });
            string content = Encoding.Latin1.GetString(pdf);

            int longest = 0, run = 0;
            foreach (char c in content) { run = c == '.' ? run + 1 : 0; if (run > longest) longest = run; }
            Assert.InRange(longest, 50, 1000);   // a page of dots — 612 points over a dot's width
        }

        [Fact]
        public void DefaultTab_AdvancesToNextStop()
        {
            byte[] pdf = Convert((doc, body) => body.Append(new Paragraph(new Run(
                new Text("a") { Space = SpaceProcessingModeValues.Preserve },
                new TabChar(),
                new Text("b") { Space = SpaceProcessingModeValues.Preserve }))));
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Contains("] TJ", content);  // tab advance via TJ (no leader)
            Assert.DoesNotContain("(a    b)", content); // no longer the old 4-space approximation
        }

        [Fact]
        public void Footnote_RendersMarkAndBottomText()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                var fnPart = doc.MainDocumentPart.AddNewPart<FootnotesPart>();
                fnPart.Footnotes = new Footnotes(
                    new Footnote(new Paragraph(new Run(new Text("My footnote body.")))) { Id = 1 });
                body.Append(new Paragraph(
                    new Run(new Text("Body text")),
                    new Run(new FootnoteReference { Id = 1 })));
            });
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Contains("My footnote body", content);  // footnote body laid in the page band
            Assert.Contains("(1)", content);               // superscript reference mark
        }

        /// <summary>
        /// Two hundred references to one footnote lay its body once, and every reference shows its
        /// number. Each reference rebuilt the note's text and queued another copy, numbered 1 to 200
        /// (Wave 22, DocxToPdf-3:1). The control: a second note, a reference of its own, is numbered 2.
        /// </summary>
        [Fact]
        public void AFootnoteReferencedManyTimes_IsLaidOutOnce()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                var fnPart = doc.MainDocumentPart.AddNewPart<FootnotesPart>();
                fnPart.Footnotes = new Footnotes(
                    new Footnote(new Paragraph(new Run(new Text("Shared note body.")))) { Id = 1 },
                    new Footnote(new Paragraph(new Run(new Text("Second note body.")))) { Id = 2 });
                var paragraph = new Paragraph(new Run(new Text("Body text")));
                for (int i = 0; i < 200; i++) paragraph.Append(new Run(new FootnoteReference { Id = 1 }));
                paragraph.Append(new Run(new FootnoteReference { Id = 2 }));
                body.Append(paragraph);
            });
            string content = Encoding.Latin1.GetString(pdf);

            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(content, "Shared note body").Count);
            Assert.Contains("Second note body", content);
            Assert.DoesNotContain("(3)", content);   // no further copies were numbered
            Assert.Contains("(2)", content);         // the second note's own number
        }

        [Fact]
        public void Endnote_RenderedAtDocumentEnd()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                var enPart = doc.MainDocumentPart.AddNewPart<EndnotesPart>();
                enPart.Endnotes = new Endnotes(
                    new Endnote(new Paragraph(new Run(new Text("My endnote body.")))) { Id = 1 });
                body.Append(new Paragraph(
                    new Run(new Text("Body text")),
                    new Run(new EndnoteReference { Id = 1 })));
            });
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Contains("My endnote body", content);
        }

        /// <summary>
        /// An internal hyperlink must produce a <c>/GoTo</c> whose <c>/D</c> <b>resolves against the
        /// destination container this converter writes</b> — not merely a <c>/GoTo</c> and a
        /// destination that happen to be in the same file.
        /// </summary>
        /// <remarks>
        /// <para><b>Substring checks on the bytes cannot hold this.</b> Of <c>/Dests</c>,
        /// <c>/target</c> and <c>/GoTo</c>, the middle one is satisfied by the <i>destination
        /// dictionary's own key</i>, which is written <c>/target</c> no matter what the link points
        /// at; they never look at the link's <c>/D</c>, and a <c>/D</c> changed from a name to a
        /// string would pass them while making every internal link in every converted document
        /// dead.</para>
        ///
        /// <para><b>The type is the whole point.</b> §12.3.2.3 gives two containers: the catalog
        /// <c>/Dests</c> <i>dictionary</i>, keyed by names, and the <c>/Names /Dests</c> name
        /// <i>tree</i>, keyed by strings. This converter writes the first, so its <c>/D</c> must be a
        /// <see cref="PdfName"/> — while Core's <c>PdfDocumentBuilder</c> writes the second and its
        /// <c>/D</c> must be a <c>PdfString</c>. Both are valid; each is wrong in the other's file.
        /// Asserting the <i>lookup succeeds</i> is what pins that, and it is why this cannot be
        /// simplified back into a check that a <c>/GoTo</c> exists.</para>
        /// </remarks>
        [Fact]
        public void InternalHyperlink_EmitsGoToWhoseDestinationResolves()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                body.Append(new Paragraph(
                    new BookmarkStart { Id = "1", Name = "target" }, new BookmarkEnd { Id = "1" },
                    new Run(new Text("Destination"))));
                body.Append(new Paragraph(new Hyperlink(new Run(new Text("jump"))) { Anchor = "target" }));
            });

            using var ms = new MemoryStream(pdf);
            using var parsed = PdfDocument.Open(ms);

            // The link annotation's action.
            PdfDictionary action = null;
            foreach (var page in parsed.Pages)
            {
                var annots = page.GetAnnotations();
                if (annots == null) continue;
                for (int i = 0; i < annots.Count && action == null; i++)
                {
                    if (!(parsed.ResolveObject(annots[i]) is PdfDictionary annot)) continue;
                    if (annot.GetName(PdfName.Subtype)?.Value != "Link") continue;
                    action = parsed.ResolveObject(annot[PdfName.A]) as PdfDictionary;
                }
            }

            Assert.NotNull(action);
            Assert.Equal("GoTo", action.GetName(PdfName.S)?.Value);

            // /D must be a NAME, because the container below is keyed by names.
            var target = parsed.ResolveObject(action[PdfName.D]) as PdfName;
            Assert.NotNull(target);
            Assert.Equal("target", target.Value);

            // …and that name must actually be declared in the catalog's /Dests dictionary,
            // which is the half a "a /GoTo exists" assertion cannot see.
            var dests = parsed.ResolveObject(parsed.Catalog.Dictionary[PdfName.Dests]) as PdfDictionary;
            Assert.NotNull(dests);
            Assert.NotNull(parsed.ResolveObject(dests[target]));
        }

        /// <summary>
        /// A bookmark's destination opens at the column its text starts in: the <c>/XYZ</c> left is
        /// the page's left margin, and the zoom is left to the viewer — <c>null</c>, which §12.3.2.2
        /// reads as "unchanged", not 0. The destination is built by <c>Core.Writing.XyzDestination</c>
        /// now, and the margin is this converter's choice: a mutation that dropped it left every
        /// DocxToPdf test green.
        /// </summary>
        [Fact]
        public void InternalDestination_OpensAtThePagesLeftMargin()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                body.Append(new Paragraph(
                    new BookmarkStart { Id = "1", Name = "target" }, new BookmarkEnd { Id = "1" },
                    new Run(new Text("Destination"))));
                body.Append(new SectionProperties(
                    new PageMargin { Left = 2160U, Right = 1440U, Top = 1440, Bottom = 1440 }));
            });

            using var parsed = PdfDocument.Open(new MemoryStream(pdf));
            var dests = (PdfDictionary)parsed.ResolveObject(parsed.Catalog.Dictionary[PdfName.Dests]);
            var dest = (PdfArray)parsed.ResolveObject(dests[new PdfName("target")]);

            Assert.Equal("XYZ", ((PdfName)dest[1]).Value);
            Assert.Equal(108, ((PdfNumber)dest[2]).DoubleValue, 3);   // 2160 twips
            Assert.IsType<PdfNull>(dest[4]);
        }

        [Fact]
        public void Headings_BuildOutlineTree()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                body.Append(new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text("Chapter One"))));
                body.Append(new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }),
                    new Run(new Text("Section A"))));
            });
            string raw = Encoding.Latin1.GetString(pdf);
            Assert.Contains("/Outlines", raw);   // outline (bookmark pane) tree
            Assert.Contains("Chapter One", raw); // heading title
            Assert.Contains("Section A", raw);
        }

        /// <summary>
        /// The outline tree's shape: each parent's <c>/First</c>, <c>/Last</c> and <c>/Count</c>, the root's
        /// included, from headings H1 H2 H2 H1 H2 H3. The parent of each group of children was found by a
        /// scan of every item (Wave 22, DocxToPdf-2:0); it is now kept beside them, and the links must come
        /// out the same. <c>/Count</c> is §12.3.3 Table 152's — every visible descendant, the root's all six
        /// items — where it was each parent's direct children.
        /// </summary>
        [Fact]
        public void Headings_BuildTheOutlineTreesShape()
        {
            string[] headings = { "1:A", "2:A1", "2:A2", "1:B", "2:B1", "3:B1a" };
            byte[] pdf = Convert((doc, body) =>
            {
                foreach (string h in headings)
                    body.Append(new Paragraph(
                        new ParagraphProperties(new ParagraphStyleId { Val = "Heading" + h[0] }),
                        new Run(new Text(h.Substring(2)))));
            });

            using var parsed = PdfDocument.Open(new MemoryStream(pdf));
            PdfDictionary Resolve(PdfObject o) => (PdfDictionary)parsed.ResolveObject(o);
            string Title(PdfDictionary d) => ((PdfString)parsed.ResolveObject(d[PdfName.Title])).GetText();
            string Children(PdfDictionary parent)
            {
                var titles = new System.Collections.Generic.List<string>();
                for (var item = parent[PdfName.First] == null ? null : Resolve(parent[PdfName.First]);
                     item != null;
                     item = item[PdfName.Next] == null ? null : Resolve(item[PdfName.Next]))
                    titles.Add(Title(item));
                Assert.Equal(titles[titles.Count - 1], Title(Resolve(parent[PdfName.Last])));
                return string.Join(",", titles);
            }
            int Count(PdfDictionary d) => ((PdfNumber)d[PdfName.Count]).IntValue;

            PdfDictionary root = Resolve(parsed.Catalog.Dictionary[PdfName.Outlines]);
            Assert.Equal("A,B", Children(root));
            PdfDictionary a = Resolve(root[PdfName.First]), b = Resolve(root[PdfName.Last]);
            PdfDictionary b1 = Resolve(b[PdfName.First]);
            Assert.Equal("A1,A2", Children(a));
            Assert.Equal("B1", Children(b));
            Assert.Equal("B1a", Children(b1));
            Assert.Equal(new[] { 6, 2, 2, 1 }, new[] { Count(root), Count(a), Count(b), Count(b1) });
        }

        [Fact]
        public void PageNumberFormat_UpperRoman_RendersRomanNumeral()
        {
            byte[] pdf = Convert((doc, body) =>
            {
                body.Append(new Paragraph(new SimpleField(new Run(new Text("1"))) { Instruction = "PAGE" }));
                body.Append(new SectionProperties(new PageNumberType { Format = NumberFormatValues.UpperRoman }));
            });
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Contains("(I)", content);  // page 1 shown as upper-roman "I"
        }

        /// <summary>
        /// The letter formats reach this renderer too, in both cases.
        /// </summary>
        /// <remarks>
        /// The roman test above was the only page-number-format test on this side, so when the
        /// five-format vocabulary was consolidated into <c>Core.Util.OrdinalNumbering</c> the
        /// mutation that lower-cases <c>upperLetter</c> died in Core and in the Builder and
        /// **survived here** — this call site was unasserted for four of the five formats. Page 1
        /// is "A" upper and "a" lower, which no other format spells.
        /// </remarks>
        [Theory]
        [InlineData("upperLetter", "(A)")]
        [InlineData("lowerLetter", "(a)")]
        public void PageNumberFormat_Letters_RenderTheLetterInItsOwnCase(string format, string expected)
        {
            var value = format == "upperLetter" ? NumberFormatValues.UpperLetter : NumberFormatValues.LowerLetter;
            byte[] pdf = Convert((doc, body) =>
            {
                body.Append(new Paragraph(new SimpleField(new Run(new Text("1"))) { Instruction = "PAGE" }));
                body.Append(new SectionProperties(new PageNumberType { Format = value }));
            });
            string content = Encoding.Latin1.GetString(pdf);
            Assert.Contains(expected, content);
        }
    }
}
