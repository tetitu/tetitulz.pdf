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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Renderer defects from the 2026-08 assembly scan (MEDIUM band): headers, page size,
    /// drawings, fonts, line wrapping, tab stops, fields, justification and table geometry.
    ///
    /// <para>
    /// Every assertion is made on the <b>produced PDF</b> — a drawn coordinate, an emitted
    /// operator, a string that reached the content stream — or on a reported diagnostic. Content
    /// streams are left uncompressed so the operators can be read directly.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ScanMediumRenderTests
    {
        private static readonly DocxToPdfOptions NoCompress =
            new DocxToPdfOptions { CompressStreams = false };

        private static string Convert(Action<WordprocessingDocument, Body> build,
            DocxToPdfOptions options = null)
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
            return Encoding.Latin1.GetString(
                DocxToPdfConverter.ConvertToBytes(ms.ToArray(), options ?? NoCompress));
        }

        /// <summary>The x of every drawn text line's origin, in emit order.</summary>
        private static List<double> LineOriginsX(string pdf)
            => Regex.Matches(pdf, @"BT\n(-?[\d.]+) (-?[\d.]+) Td")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

        /// <summary>The y of every drawn text line's baseline, in emit order.</summary>
        private static List<double> LineBaselinesY(string pdf)
            => Regex.Matches(pdf, @"BT\n(-?[\d.]+) (-?[\d.]+) Td")
                .Select(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                .ToList();

        // ================================================================== DocxRenderContext:737

        private const string HeaderText = "RUNNINGHEADERTEXT";

        private static void AddDefaultHeader(WordprocessingDocument doc, Body body, bool titlePage)
        {
            var main = doc.MainDocumentPart;
            var hp = main.AddNewPart<HeaderPart>();
            hp.Header = new Header(new Paragraph(new Run(new Text(HeaderText))));
            string relId = main.GetIdOfPart(hp);

            body.Append(new Paragraph(new Run(new Text("BODY"))));

            var sect = new SectionProperties();
            if (titlePage) sect.Append(new TitlePage());
            sect.Append(new HeaderReference { Id = relId, Type = HeaderFooterValues.Default });
            body.Append(sect);
        }

        [Fact]
        public void TitlePage_WithNoFirstPageHeaderReference_LeavesTheCoverPageClean()
        {
            // Word shows NO header on the first page of a w:titlePg section that references no
            // `first` header — that is the whole point of titlePg. The unconditional
            // `id ??= fallback` stamped the running header across the cover title instead.
            string withTitlePg = Convert((doc, body) => AddDefaultHeader(doc, body, titlePage: true));
            Assert.DoesNotContain(HeaderText, withTitlePg);

            // The control proves the fixture really carries a header the renderer can draw, so the
            // assertion above is about titlePg and not about a header that never existed.
            string withoutTitlePg = Convert((doc, body) => AddDefaultHeader(doc, body, titlePage: false));
            Assert.Contains(HeaderText, withoutTitlePg);
        }

        /// <summary>
        /// A paragraph's line height comes from the first level that states <c>w:line</c>, the document
        /// defaults included: a docDefaults <c>w:spacing w:line="480"</c> doubles the baseline gap of an
        /// unstyled paragraph, as in Word; the control without it is single spaced (12 pt × 1.2).
        /// </summary>
        [Theory]
        [InlineData(true, 28.8)]
        [InlineData(false, 14.4)]
        public void DocDefaultsLineSpacing_SetsTheBaselineGap(bool doubled, double expectedGap)
        {
            string pdf = Convert((doc, body) =>
            {
                if (doubled)
                {
                    var styles = doc.MainDocumentPart.AddNewPart<StyleDefinitionsPart>();
                    styles.Styles = new Styles(new DocDefaults(new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines { Line = "480", LineRule = LineSpacingRuleValues.Auto }))));
                }
                body.Append(new Paragraph(new Run(new Text(string.Join(" ", Enumerable.Repeat("word", 60))))));
            });

            var baselines = LineBaselinesY(pdf);
            Assert.True(baselines.Count >= 2, "the paragraph should wrap");
            Assert.InRange(baselines[0] - baselines[1], expectedGap - 0.6, expectedGap + 0.6);
        }

        // ================================================================== DocxRenderContext:454

        [Fact]
        public void UseDocumentMarginsFalse_StillHonoursTheSectionPageSize()
        {
            // A4 landscape: 16838 × 11906 twips = 841.9 × 595.3 pt.
            void Build(WordprocessingDocument doc, Body body)
            {
                body.Append(new Paragraph(new Run(new Text("BODY"))));
                body.Append(new SectionProperties(
                    new PageSize { Width = 16838, Height = 11906, Orient = PageOrientationValues.Landscape },
                    new PageMargin { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440 }));
            }

            string pdf = Convert(Build, new DocxToPdfOptions
            {
                CompressStreams = false,
                UseDocumentMargins = false,
                MarginTop = 36, MarginBottom = 36, MarginLeft = 36, MarginRight = 36,
            });

            var box = Regex.Match(pdf, @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]");
            Assert.True(box.Success, "no /MediaBox in the output");
            double w = double.Parse(box.Groups[1].Value, CultureInfo.InvariantCulture);
            double h = double.Parse(box.Groups[2].Value, CultureInfo.InvariantCulture);

            // UseDocumentMargins is documented as governing the margins. Returning early also
            // discarded w:pgSz, so this page came out 612 × 792 portrait US Letter and the
            // right-hand third of every line fell outside the MediaBox.
            Assert.Equal(841.9, w, 1);
            Assert.Equal(595.3, h, 1);

            // …and the caller's margins really were the ones applied, so the two halves of the
            // split guard are both pinned.
            Assert.Equal(36.0, LineOriginsX(pdf)[0], 1);
        }

        // ================================================================== FontResolver:33

        [Fact]
        public void FontResolveCacheKey_DistinguishesDifferentCodePointSetsOfTheSameLength()
        {
            // "אב" and "א€": same family, same style, same count, same first code point. The old
            // key summarised the set as "{count}:{first}", so the second run was handed the
            // Hebrew-only face resolved for the first — without re-checking coverage — and
            // UnicodeText.Shape substituted the space glyph for the euro sign.
            string hebrew = FontResolver.CacheKey("David", false, false, new[] { 0x05D0, 0x05D1 });
            string mixed = FontResolver.CacheKey("David", false, false, new[] { 0x05D0, 0x20AC });
            Assert.NotEqual(hebrew, mixed);

            // The same set in a different order is the same request, so it must share the entry.
            Assert.Equal(hebrew, FontResolver.CacheKey("David", false, false, new[] { 0x05D1, 0x05D0 }));

            // Family and style still separate entries.
            Assert.NotEqual(hebrew, FontResolver.CacheKey("David", true, false, new[] { 0x05D0, 0x05D1 }));
            Assert.NotEqual(hebrew, FontResolver.CacheKey("Arial", false, false, new[] { 0x05D0, 0x05D1 }));
        }

        // ================================================================== MathMeasure:60 / UnicodeText

        [Fact]
        public void CodePoints_SurrogatePair_IsOneCodePointNotTwoLoneSurrogates()
        {
            // 𝑥 U+1D465 MATHEMATICAL ITALIC SMALL X, which Word's equation editor writes into m:t.
            Assert.Equal(new[] { 0x1D465 }, UnicodeText.CodePoints("\U0001D465").ToArray());

            Assert.Equal(new[] { (int)'a', 0x1D538, (int)'b' },
                UnicodeText.CodePoints("a\U0001D538b").ToArray());

            // A lone high surrogate is malformed input; it is passed through as itself rather than
            // swallowed, so the enumeration still terminates and still reports honestly.
            Assert.Equal(new[] { (int)'a', 0xD835, (int)'b' },
                UnicodeText.CodePoints("a\uD835b").ToArray());
        }

        // ================================================================== ParagraphRenderer:632

        private const string Chunk = "aaaa bbbb cccc dddd eeee";

        [Fact]
        public void HorizontalTextScale_IsCountedWhenWrapping_NotOnlyWhenDrawing()
        {
            // TWO runs, deliberately. The wrap loop keeps two accumulators: `bufWidth` for the
            // words of the segment being scanned, and `currentWidth` for the segments already
            // committed to the line. A single-run paragraph exercises only the first, so a fixture
            // with one run cannot tell whether the committed width is scale-aware — which is
            // exactly what a surviving mutation of AddPiece revealed about the first version of
            // this test. At 100% both runs share one line; at 200% they cannot.
            string Run200(int scale) => Convert((doc, body) =>
            {
                RunProperties Props()
                {
                    var rPr = new RunProperties();
                    if (scale != 100) rPr.Append(new CharacterScale { Val = scale });
                    return rPr;
                }

                body.Append(new Paragraph(
                    new Run(Props(), new Text(Chunk) { Space = SpaceProcessingModeValues.Preserve }),
                    new Run(Props(), new Text(Chunk) { Space = SpaceProcessingModeValues.Preserve })));
            });

            string plain = Run200(100);
            string scaled = Run200(200);

            // The fixture really reached the feature: w:w is emitted as Tz.
            Assert.Contains("200 Tz", scaled);

            // The wrap loop measured the raw string while the emit path multiplied by Tz, so a
            // 200%-scaled run was packed to twice the column width and ran a full column past the
            // right margin — on the same number of lines as the unscaled text.
            int plainLines = LineOriginsX(plain).Count;
            int scaledLines = LineOriginsX(scaled).Count;
            Assert.True(scaledLines > plainLines,
                $"200%-scaled text wrapped to {scaledLines} lines, same as unscaled ({plainLines})");
        }

        // ================================================================== ParagraphRenderer:799

        /// <summary>The single tab advance in the content, in points.</summary>
        private static double TabAdvancePt(string pdf, double fontSize)
        {
            var m = Regex.Match(pdf, @"\[(-?[\d.]+)\] TJ");
            Assert.True(m.Success, "no TJ tab advance in the output");
            double units = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return -units / 1000.0 * fontSize;
        }

        [Fact]
        public void TabStops_AreMeasuredFromTheTextMargin_NotFromTheIndentedLineOrigin()
        {
            // A ToC-style line: text, a right tab at 9360 twips (6.5" — the right margin of a
            // Letter page with 1" margins), text.
            string WithIndent(int leftTwips) => Convert((doc, body) =>
            {
                var pPr = new ParagraphProperties(
                    new Tabs(new TabStop { Val = TabStopValues.Right, Position = 9360 }));
                if (leftTwips > 0) pPr.Append(new Indentation { Left = leftTwips.ToString(CultureInfo.InvariantCulture) });
                body.Append(new Paragraph(pPr, new Run(
                    new Text("A") { Space = SpaceProcessingModeValues.Preserve },
                    new TabChar(),
                    new Text("B") { Space = SpaceProcessingModeValues.Preserve })));
            });

            double plain = TabAdvancePt(WithIndent(0), 12);
            double indented = TabAdvancePt(WithIndent(1440), 12);   // 1 inch

            // w:tab/@w:pos is measured from the paragraph's text-start edge, so indenting the
            // paragraph by 1" moves its content 72 pt closer to a stop that has NOT moved — the
            // advance must shrink by exactly the indent. Resolving the stop against the line's
            // drawn origin moved the target with the indent instead, leaving the advance identical
            // and putting the right-aligned text 1" past the right margin.
            Assert.Equal(72.0, plain - indented, 1);
        }

        // ================================================================== ParagraphRenderer:269

        [Fact]
        public void FieldInstructions_MatchTheFieldName_NotASubstringOfTheWholeInstruction()
        {
            // A table-of-contents entry. PAGEREF is not PAGE: the cached result run holds the
            // number of the page being referenced, and a substring match both replaced it with the
            // current page number and discarded the cached run that had the right answer.
            string toc = Convert((doc, body) => body.Append(new Paragraph(
                new SimpleField(new Run(new Text("17"))) { Instruction = " PAGEREF _Toc90210 \\h " })));
            Assert.Contains("(17)", toc);

            // CREATEDATE / SAVEDATE / PRINTDATE all matched Contains("DATE") and rendered today.
            string created = Convert((doc, body) => body.Append(new Paragraph(
                new SimpleField(new Run(new Text("01/01/2020")))
                { Instruction = " CREATEDATE \\@ \"dd/MM/yyyy\" " })));
            Assert.Contains("(01/01/2020)", created);

            // EDITTIME matched Contains("TIME") and rendered the wall clock.
            string edit = Convert((doc, body) => body.Append(new Paragraph(
                new SimpleField(new Run(new Text("HOURS42"))) { Instruction = " EDITTIME " })));
            Assert.Contains("(HOURS42)", edit);

            // The four fields that ARE evaluated still are — the control that keeps this from
            // being satisfied by disabling field handling altogether.
            string page = Convert((doc, body) => body.Append(new Paragraph(
                new SimpleField(new Run(new Text("IGNOREDCACHE"))) { Instruction = "PAGE" })));
            Assert.Contains("(1)", page);
            Assert.DoesNotContain("IGNOREDCACHE", page);
        }

        // ================================================================== ParagraphRenderer:861

        /// <summary>The left x of the single underline rule in the content.</summary>
        private static double UnderlineX1(string pdf)
        {
            var m = Regex.Match(pdf, @"([\d.]+) ([\d.]+) m\n([\d.]+) \2 l\nS");
            Assert.True(m.Success, "no underline rule in the output");
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        [Fact]
        public void JustifiedLine_PlacesDecorationsWhereTheStretchedGlyphsActuallyLand()
        {
            // Line 1 is justified (an explicit break makes it a non-last line), and carries an
            // underlined run after five stretched spaces.
            string Build(JustificationValues jc) => Convert((doc, body) =>
            {
                var underlined = new RunProperties(new Underline { Val = UnderlineValues.Single });
                body.Append(new Paragraph(
                    new ParagraphProperties(new Justification { Val = jc }),
                    new Run(new Text("alpha beta gamma delta epsilon zeta")
                    { Space = SpaceProcessingModeValues.Preserve }),
                    new Run(underlined, new Text("MARK") { Space = SpaceProcessingModeValues.Preserve }),
                    new Run(new Break(), new Text("tail") { Space = SpaceProcessingModeValues.Preserve })));
            });

            string justified = Build(JustificationValues.Both);
            string left = Build(JustificationValues.Left);

            // The fixture really justified: Tw is only emitted for a stretched line.
            Assert.Contains(" Tw\n", justified);

            // The per-segment x positions were accumulated from widths that knew nothing about the
            // Tw the same line emits, so the underline (and the /Link rect, and the equation slot)
            // were drawn where the glyphs would have been WITHOUT the stretch — tens of points to
            // the left of the text they belong to.
            Assert.True(UnderlineX1(justified) > UnderlineX1(left) + 20,
                $"underline at {UnderlineX1(justified):F2} justified vs {UnderlineX1(left):F2} left — "
                    + "word spacing was not counted into the segment positions");
        }

        // ================================================================== RunRenderer:98 / TableRenderer:702

        // U+0378 is an UNASSIGNED code point in the Greek and Coptic block. No font maps an
        // unassigned code point, so this is the "nothing available covers it" case on every
        // machine rather than only on a bare container — and unlike a noncharacter such as
        // U+FFFF it is legal XML, so the fixture DOCX can actually be written.
        private const string Uncoverable = "A͸B";

        [Fact]
        public void TextNoAvailableFontCovers_IsReported_NotSilentlyDrawnAsBlanks()
        {
            string pdf = Convert((doc, body) => body.Append(new Paragraph(new Run(
                new Text(Uncoverable) { Space = SpaceProcessingModeValues.Preserve }))));

            // It really did fall through to the standard-14 path, where WinAnsi maps it to a space.
            Assert.Contains("(A B)", pdf);

            // …and said so. Without this a Hebrew or CJK document on a container with no matching
            // font produced a page of correctly-sized blank lines with LastDiagnostic still null.
            Assert.Contains("no available font covers", DocxToPdfConverter.LastDiagnostic ?? "");
            Assert.Contains("U+0378", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        [Fact]
        public void TextNoAvailableFontCovers_InATableCell_IsReportedToo()
        {
            string pdf = Convert((doc, body) => body.Append(new Table(new TableRow(
                new TableCell(new Paragraph(new Run(
                    new Text(Uncoverable) { Space = SpaceProcessingModeValues.Preserve })))))));

            Assert.Contains("(A B)", pdf);
            Assert.Contains("no available font covers", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        // ================================================================== StyleResolver:401

        [Fact]
        public void DirectSpacingWithoutLine_InheritsTheStylesLineSpacing()
        {
            string Build(bool directSpacingAfter) => Convert((doc, body) =>
            {
                var stylesPart = doc.MainDocumentPart.AddNewPart<StyleDefinitionsPart>();
                stylesPart.Styles = new Styles(new Style(
                    new StyleName { Val = "BodyText" },
                    new StyleParagraphProperties(new SpacingBetweenLines
                    { Line = "480", LineRule = LineSpacingRuleValues.Auto }))
                {
                    Type = StyleValues.Paragraph,
                    StyleId = "BodyText",
                });
                stylesPart.Styles.Save();

                for (int i = 0; i < 2; i++)
                {
                    var pPr = new ParagraphProperties(new ParagraphStyleId { Val = "BodyText" });
                    if (directSpacingAfter) pPr.Append(new SpacingBetweenLines { After = "0" });
                    body.Append(new Paragraph(pPr, new Run(new Text("LINE" + i))));
                }
            });

            var withoutDirect = LineBaselinesY(Build(false));
            var withDirect = LineBaselinesY(Build(true));
            Assert.Equal(2, withoutDirect.Count);
            Assert.Equal(2, withDirect.Count);

            // OOXML resolves each attribute independently. A direct w:spacing carrying only
            // w:after used to be selected as THE spacing element and, stating no w:line, made
            // ResolveLineHeight return 0 — so the paragraph rendered single-spaced among its
            // double-spaced siblings. The two baseline gaps must agree.
            Assert.Equal(withoutDirect[0] - withoutDirect[1], withDirect[0] - withDirect[1], 1);

            // …and both must be the style's double spacing (12 pt × 1.2 × 480/240 = 28.8 pt),
            // not the 14.4 pt single-spaced fallback.
            Assert.Equal(28.8, withDirect[0] - withDirect[1], 1);
        }

        // ================================================================== StyleResolver:331

        [Fact]
        public void CharacterStyle_SuppliesVertAlignAndTextScale_ThroughTheStyleChain()
        {
            string pdf = Convert((doc, body) =>
            {
                var stylesPart = doc.MainDocumentPart.AddNewPart<StyleDefinitionsPart>();
                stylesPart.Styles = new Styles(new Style(
                    new StyleName { Val = "FootnoteRef" },
                    new StyleRunProperties(
                        new VerticalTextAlignment { Val = VerticalPositionValues.Superscript },
                        new CharacterScale { Val = 200 }))
                {
                    Type = StyleValues.Character,
                    StyleId = "FootnoteRef",
                });
                stylesPart.Styles.Save();

                body.Append(new Paragraph(new Run(
                    new RunProperties(new RunStyle { Val = "FootnoteRef" }),
                    new Text("REF") { Space = SpaceProcessingModeValues.Preserve })));
            });

            // These five resolvers took the paragraph argument and never used it, so a character
            // style — the textbook way to mark a footnote reference — was ignored entirely and the
            // mark was drawn full-size on the baseline.
            Assert.Matches(@"[\d.]+ Ts\n", pdf);          // raised baseline (w:vertAlign)
            Assert.Contains("200 Tz", pdf);                // horizontal scale (w:w)
        }

        // ================================================================== TableRenderer:635

        [Fact]
        public void GridSpan_AdversarialOverflow_ClampsAndReportsInsteadOfThrowing()
        {
            TableCell Spanning(int span, string text) => new TableCell(
                new TableCellProperties(new GridSpan { Val = span }),
                new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));

            string pdf = Convert((doc, body) => body.Append(new Table(
                new TableGrid(new GridColumn { Width = "3000" }, new GridColumn { Width = "3000" },
                              new GridColumn { Width = "3000" }),
                new TableRow(
                    Spanning(int.MaxValue, "FIRSTCELL"),
                    Spanning(int.MaxValue, "SECONDCELL"),
                    Spanning(1, "THIRDCELL")))));

            // `col += span` is unchecked: two int.MaxValue spans wrapped col to -2, and the third
            // cell's `col + s < colWidths.Count` test then passed for a negative index, so
            // colWidths[-2] threw and ConvertBuiltIn re-wrapped it as a whole-document failure —
            // in code whose stated intent is that a gridSpan overshoot must still render.
            Assert.Contains("(THIRDCELL)", pdf);
            Assert.Contains("(FIRSTCELL)", pdf);
            Assert.Contains("gridSpan", DocxToPdfConverter.LastDiagnostic ?? "");
        }
    }
}
