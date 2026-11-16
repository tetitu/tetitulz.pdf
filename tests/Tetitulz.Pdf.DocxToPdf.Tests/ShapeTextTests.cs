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
    /// Text inside a shape — a Word text box (<c>wps:txbx/w:txbxContent</c>) and DrawingML shape
    /// text (<c>a:txBody</c>), which SmartArt also uses.
    ///
    /// <para>
    /// The subtle requirement is the <b>text matrix</b>. The drawing frame has a negative vertical
    /// scale, so a glyph placed with <c>Td</c> renders upside-down — visually obvious, and
    /// completely invisible to an assertion that only looks for the drawn string. Hence
    /// <see cref="ShapeText_UsesAFlippedTextMatrix_SoGlyphsAreUpright"/>.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ShapeTextTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        // A 3" × 1" shape (2743200 × 914400 EMU = 216 × 72 pt).
        private static string Inline(string payload)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""2743200"" cy=""914400""/>
<wp:docPr id=""1"" name=""S1""/><a:graphic><a:graphicData uri=""{ShapeUri}"">{payload}
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        // DrawingML shape text (a:txBody) — what SmartArt writes.
        private static string DrawingMlShape(string paragraphs, string bodyPrAttrs = "", string fill = null)
            => $@"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>{fill ?? ""}</wps:spPr>
<wps:txBody><a:bodyPr {bodyPrAttrs}/><a:lstStyle/>{paragraphs}</wps:txBody></wps:wsp>";

        private static string Para(string text, string rPr = @"sz=""1200""", string pPrAttrs = null,
            string rPrInner = null)
            => $@"<a:p>{(pPrAttrs == null ? "" : $"<a:pPr {pPrAttrs}/>")}
<a:r><a:rPr lang=""en-US"" {rPr}{(rPrInner == null ? "/>" : $">{rPrInner}</a:rPr>")}
<a:t>{text}</a:t></a:r></a:p>";

        // A Word text box (wps:txbx/w:txbxContent) — WordprocessingML paragraphs inside a shape.
        private static string TextBoxShape(string innerText)
            => $@"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom></wps:spPr>
<wps:txbx><w:txbxContent><w:p><w:r><w:rPr><w:sz w:val=""24""/></w:rPr>
<w:t>{innerText}</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp>";

        private static string Convert(string drawingXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Drawing(drawingXml)))));
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));
        }

        // Every "a b c d e f Tm" in the content stream.
        private static List<double[]> Tms(string pdf)
            => Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm")
                .Select(m => Enumerable.Range(1, 6)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();

        private static List<double> FontSizes(string pdf)
            => Regex.Matches(pdf, @"/F\d+ ([\d.]+) Tf")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

        // ------------------------------------------------------------------ the essentials

        [Fact]
        public void DrawingMlShapeText_IsDrawn()
        {
            string pdf = Convert(Inline(DrawingMlShape(Para("Hello"))));

            Assert.Contains("(Hello) Tj", pdf);
        }

        [Fact]
        public void WordTextBoxText_IsDrawn()
        {
            string pdf = Convert(Inline(TextBoxShape("Boxed")));

            Assert.Contains("(Boxed) Tj", pdf);
        }

        /// <summary>
        /// A text box run's <c>w:b</c> is an <c>ST_OnOff</c>: <c>w:val="on"</c> is bold, as <c>&lt;w:b/&gt;</c> is, and
        /// <c>w:val="off"</c> plain. The shape-text reader took "on" as off, so the run was drawn plain.
        /// </summary>
        [Fact]
        public void ATextBoxRunsOnOffBold_IsReadAsSTOnOff()
        {
            static string FontOf(string rPrExtra)
            {
                string shape = $@"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom></wps:spPr>
<wps:txbx><w:txbxContent><w:p><w:r><w:rPr>{rPrExtra}<w:sz w:val=""24""/></w:rPr>
<w:t>Boxed</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp>";
                // A shape is a form with its own font names, so the face is told by the document's base fonts —
                // the run is the document's only text.
                string pdf = Convert(Inline(shape));
                Assert.Contains("(Boxed) Tj", pdf);
                return string.Join(" ", Regex.Matches(pdf, @"/BaseFont /([\w-]+)").Select(f => f.Groups[1].Value).Distinct().OrderBy(f => f));
            }

            string plain = FontOf(""), bold = FontOf("<w:b/>");
            Assert.NotEqual(plain, bold);
            Assert.Equal(bold, FontOf(@"<w:b w:val=""on""/>"));
            Assert.Equal(bold, FontOf(@"<w:b w:val=""true""/>"));
            Assert.Equal(plain, FontOf(@"<w:b w:val=""off""/>"));
            Assert.Equal(plain, FontOf(@"<w:b w:val=""0""/>"));
        }

        [Fact]
        public void ShapeText_UsesAFlippedTextMatrix_SoGlyphsAreUpright()
        {
            // Inside the drawing's y-down frame a plain Td would render the glyphs mirrored, and
            // "(Hello) Tj" would still be present — so this is the assertion that catches it.
            string pdf = Convert(Inline(DrawingMlShape(Para("Hello"))));

            var tms = Tms(pdf);
            Assert.NotEmpty(tms);
            Assert.All(tms, m =>
            {
                Assert.Equal(1.0, m[0], 3);
                Assert.Equal(-1.0, m[3], 3);   // the vertical re-flip
            });
        }

        [Fact]
        public void ShapeWithTextButNoFillOrOutline_StillDrawsItsText()
        {
            // A text box normally has neither. Discarding unpainted shapes would lose the text.
            string pdf = Convert(Inline(DrawingMlShape(Para("Naked"), fill: "<a:noFill/>")));

            Assert.Contains("(Naked) Tj", pdf);
            Assert.DoesNotContain(" re\n", pdf);   // and nothing was painted for it
        }

        // ------------------------------------------------------------------ layout

        [Fact]
        public void ShapeText_RespectsTheRunFontSize()
        {
            string pdf = Convert(Inline(DrawingMlShape(Para("Sized", @"sz=""2400"""))));

            Assert.Contains(24.0, FontSizes(pdf));
        }

        [Fact]
        public void CentredShapeText_IsHorizontallyCentredInTheBox()
        {
            string centred = Convert(Inline(DrawingMlShape(Para("AB", pPrAttrs: @"algn=""ctr"""))));
            string left = Convert(Inline(DrawingMlShape(Para("AB"))));

            double xCentred = Tms(centred).Single()[4];
            double xLeft = Tms(left).Single()[4];

            // 216pt box, 0.1" insets: a two-character string centred must start well right of the
            // left inset, and the two must differ.
            Assert.True(xCentred > xLeft + 50,
                $"expected the centred x ({xCentred}) to be well right of the left-aligned x ({xLeft})");
        }

        [Fact]
        public void RightAlignedShapeText_EndsNearTheRightInset()
        {
            string pdf = Convert(Inline(DrawingMlShape(Para("AB", pPrAttrs: @"algn=""r"""))));

            double x = Tms(pdf).Single()[4];
            Assert.True(x > 150, $"expected the right-aligned x ({x}) to be near the 216pt box's right edge");
        }

        [Fact]
        public void VerticallyCentredText_SitsLowerThanTopAnchoredText()
        {
            string top = Convert(Inline(DrawingMlShape(Para("A"), bodyPrAttrs: @"anchor=""t""")));
            string middle = Convert(Inline(DrawingMlShape(Para("A"), bodyPrAttrs: @"anchor=""ctr""")));

            // The frame is y-down, so "lower on the page" is a LARGER y here.
            Assert.True(Tms(middle).Single()[5] > Tms(top).Single()[5],
                "centre-anchored text must sit below top-anchored text");
        }

        [Fact]
        public void LongText_WrapsOntoSeveralLines()
        {
            string words = string.Join(" ", Enumerable.Repeat("wrapping", 30));
            string pdf = Convert(Inline(DrawingMlShape(Para(words))));

            // One Tm per drawn line; 30 words at 12pt cannot fit one 216pt line.
            Assert.True(Tms(pdf).Count > 1, "expected the text to wrap onto more than one line");
        }

        [Fact]
        public void ShortText_DoesNotWrap()
        {
            // The control for the wrap test: a fixture that always produced several lines would
            // make the assertion above unfalsifiable.
            string pdf = Convert(Inline(DrawingMlShape(Para("short"))));

            Assert.Single(Tms(pdf));
        }

        [Fact]
        public void NormAutofitFontScale_ShrinksTheDrawnText()
        {
            string pdf = Convert(Inline(DrawingMlShape(
                Para("Shrunk", @"sz=""2000"""),
                bodyPrAttrs: "")).Replace("<a:bodyPr />",
                    @"<a:bodyPr><a:normAutofit fontScale=""50000""/></a:bodyPr>"));

            Assert.Contains(10.0, FontSizes(pdf));   // 20pt × 50%
        }

        [Fact]
        public void ShapeTextColour_ComesFromTheRunFill()
        {
            string pdf = Convert(Inline(DrawingMlShape(Para("Red",
                rPrInner: @"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>"))));

            Assert.Contains("(Red) Tj", pdf);                 // the fixture reached the feature
            Assert.Contains("1.000 0.000 0.000 rg", pdf);
        }

        // ------------------------------------------------------------------ any script, every run

        /// <summary>
        /// Hebrew shape text is drawn as a body run outside WinAnsi is: shaped glyphs in an embedded font,
        /// never WinAnsi bytes. The control is the Latin shape, drawn in a standard face.
        /// </summary>
        [Fact]
        public void HebrewShapeText_IsDrawnThroughAnEmbeddedFont()
        {
            string hebrew = Convert(Inline(DrawingMlShape(Para("שלום"))));
            string latin = Convert(Inline(DrawingMlShape(Para("Hello"))));

            Assert.Matches(@"/EF\d+ [\d.]+ Tf", hebrew);
            Assert.DoesNotContain(") Tj", hebrew);
            Assert.DoesNotMatch(@"/EF\d+ [\d.]+ Tf", latin);
            Assert.Contains("(Hello) Tj", latin);
        }

        /// <summary>
        /// A text box's runs are read through what holds them — a hyperlink here — and a <c>w:br</c>
        /// ends the line: the linked word is drawn, and the two lines are two text objects.
        /// </summary>
        [Fact]
        public void TextBoxRuns_InsideAHyperlinkAndAcrossABreak_AreDrawn()
        {
            string shape = @"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom></wps:spPr>
<wps:txbx><w:txbxContent><w:p><w:hyperlink w:anchor=""a""><w:r><w:t>LINKED</w:t></w:r></w:hyperlink>
<w:r><w:t xml:space=""preserve""> PLAIN</w:t><w:br/><w:t>SECOND</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp>";

            string pdf = Convert(Inline(shape));

            Assert.Contains("(LINKED PLAIN) Tj", pdf);
            Assert.Contains("(SECOND) Tj", pdf);
            Assert.Equal(2, Tms(pdf).Count);
        }

        [Fact]
        public void TextTallerThanItsShape_IsDrawnAndReported()
        {
            string many = string.Join("", Enumerable.Range(0, 12).Select(i => Para("line " + i)));
            string pdf = Convert(Inline(DrawingMlShape(many)));

            Assert.Contains("(line 0) Tj", pdf);
            Assert.Contains("taller than the shape", DocxDiagnostics.LastError);
        }
    }
}

