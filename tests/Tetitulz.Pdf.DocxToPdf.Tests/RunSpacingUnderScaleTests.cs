// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A run with both <c>w:w</c> (character scale) and <c>w:spacing</c> (tracking): the tracking
    /// drawn is the tracking measured.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> ISO 32000-2 §9.3.4 has horizontal scaling "also affect the spacing
    /// parameters Tc and Tw", so a run drawn with <c>150 Tz</c> and <c>2 Tc</c> advances by
    /// (width + 2·n)·1.5, while <see cref="RunRenderer.WithRunSpacing"/> — the one measurement the
    /// body and the cell share — had it at width·1.5 + 2·n. The two differ by Tc·n·(Th − 1): a
    /// right-aligned line overshot its margin by that much, and a wrapped line was laid out for a
    /// width it did not draw at.</para>
    ///
    /// <para><b>Which side was right is ECMA-376's call, not PDF's.</b> ISO/IEC 29500-1 §17.3.2.35
    /// defines <c>spacing</c> as "the amount of character pitch which shall be added or removed
    /// after each character" in twentieths of a point — an absolute distance — and §17.3.2.43 has
    /// <c>w</c> stretch "each character in this run", "as opposed to the spacing element, which
    /// expands or compresses the text by adding additional character pitch but not changing the
    /// width of the actual characters". So the measurement stood and the emitter moved: the
    /// <c>Tc</c> written is the spacing divided by the scale factor
    /// (<see cref="RunRenderer.EmittedCharSpacing"/>), at every emitter — the body paragraph, the
    /// table cell, and the Builder path, whose <c>TextRun.CharSpacing</c> is PDF's <c>Tc</c>.</para>
    ///
    /// <para><b>Asserted on the operand.</b> The layout already measured the absolute pitch, so no
    /// line position changed; what changed is the number after <c>Tc</c>. Each emitter's stream is
    /// read for it, with the unscaled run as the control that the number is the spacing itself.</para>
    /// </remarks>
    public class RunSpacingUnderScaleTests
    {
        private const string Sample = "Spaced";
        private const int SpacingTwips = 40;   // 2 pt after every character
        private const double SpacingPt = 2.0;

        // ── the rule ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(2.0, 150, 2.0 / 1.5)]
        [InlineData(2.0, 200, 1.0)]
        [InlineData(2.0, 50, 4.0)]
        [InlineData(-1.0, 200, -0.5)]
        [InlineData(2.0, 100, 2.0)]
        [InlineData(0.0, 150, 0.0)]
        public void TheEmittedSpacing_IsTheSpacingOverTheScaleFactor(double spacing, int scale, double expected)
        {
            Assert.Equal(expected, RunRenderer.EmittedCharSpacing(spacing, scale), 9);
        }

        /// <summary>
        /// A scale Word cannot write (its range is 1–600) is not divided by: the spacing passes
        /// through rather than becoming infinite or NaN.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-50)]
        public void AScaleOfZeroOrLess_LeavesTheSpacingAlone(int scale)
        {
            Assert.Equal(2.0, RunRenderer.EmittedCharSpacing(2.0, scale));
        }

        /// <summary>
        /// <b>The two answers to one question, side by side.</b> What the page draws for a run under
        /// <c>Tz</c> and the emitted <c>Tc</c> — (width + Tc·n)·Th, §9.3.4 — is what
        /// <see cref="RunRenderer.WithRunSpacing"/> measured for it.
        /// </summary>
        [Theory]
        [InlineData(100)]
        [InlineData(150)]
        [InlineData(300)]
        [InlineData(66)]
        public void TheDrawnAdvance_IsTheMeasuredWidth(int scale)
        {
            const double glyphWidth = 31.4;
            double measured = RunRenderer.WithRunSpacing(glyphWidth, Sample, scale, SpacingPt);
            double drawn = (glyphWidth + RunRenderer.EmittedCharSpacing(SpacingPt, scale) * Sample.Length) * scale / 100.0;
            Assert.Equal(measured, drawn, 9);
        }

        // ── each emitter writes it ──────────────────────────────────────────────────

        /// <summary>The body paragraph: <c>Spacing 40</c> at 150% is drawn as 1.333 Tc, not 2.</summary>
        [Fact]
        public void ABodyParagraph_EmitsTheSpacingOverTheScale()
        {
            string scaled = Convert(body => body.Append(new Paragraph(TrackedRun(150))), legacy: true);
            string plain = Convert(body => body.Append(new Paragraph(TrackedRun(100))), legacy: true);

            Assert.Contains("150 Tz\n", scaled, StringComparison.Ordinal);
            Assert.Equal(SpacingPt / 1.5, TcOperand(scaled), 3);
            Assert.Equal(SpacingPt, TcOperand(plain), 3);
        }

        /// <summary>The table cell, whose emitter is its own loop.</summary>
        [Fact]
        public void ATableCell_EmitsTheSpacingOverTheScale()
        {
            string scaled = Convert(body => body.Append(TableWith(TrackedRun(150))), legacy: true);
            string plain = Convert(body => body.Append(TableWith(TrackedRun(100))), legacy: true);

            Assert.Contains("150 Tz\n", scaled, StringComparison.Ordinal);
            Assert.Equal(SpacingPt / 1.5, TcOperand(scaled), 3);
            Assert.Equal(SpacingPt, TcOperand(plain), 3);
        }

        /// <summary>
        /// The Builder path: the mapper hands the Builder a <c>CharSpacing</c> that is PDF's
        /// <c>Tc</c> — already divided — so the Builder's measurement (which scales it, as PDF
        /// does) and the page agree with Word.
        /// </summary>
        [Fact]
        public void TheBuilderPath_EmitsTheSpacingOverTheScale()
        {
            string scaled = Convert(body => body.Append(new Paragraph(TrackedRun(150))), legacy: false);
            string plain = Convert(body => body.Append(new Paragraph(TrackedRun(100))), legacy: false);

            Assert.Contains("150 Tz\n", scaled, StringComparison.Ordinal);
            Assert.Equal(SpacingPt / 1.5, TcOperand(scaled), 3);
            Assert.Equal(SpacingPt, TcOperand(plain), 3);
        }

        /// <summary>
        /// Two runs on one line, tracked alike but scaled differently, each get their own
        /// <c>Tc</c>: the operator is re-emitted when the divided value changes, not only when the
        /// raw spacing does — 2 pt at 150% is 1.333 Tc and the same 2 pt at 300% is 0.667, and an
        /// emitter that only watched the spacing would leave the second run at the first's value.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TwoRuns_TrackedAlikeButScaledApart_EachGetTheirOperator(bool inCell)
        {
            var paragraph = new Paragraph(TrackedRun(150), TrackedRun(300));
            string pdf = Convert(body => body.Append(inCell ? TableWith(paragraph) : paragraph), legacy: true);

            double[] tcs = TcOperands(pdf);
            Assert.Contains(tcs, v => Math.Abs(v - 2.0 / 1.5) < 1e-3);
            Assert.Contains(tcs, v => Math.Abs(v - 2.0 / 3) < 1e-3);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>The operand of the first non-zero <c>Tc</c> in the stream; fails when there is none.</summary>
        private static double TcOperand(string pdf)
        {
            double[] all = TcOperands(pdf).Where(v => v != 0).ToArray();
            Assert.True(all.Length > 0, "no Tc operator reached the page; the fixture did not render its tracking");
            return all[0];
        }

        private static double[] TcOperands(string pdf)
            => Regex.Matches(pdf, @"(-?[\d.]+) Tc\n")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToArray();

        private static Run TrackedRun(int scale, int spacingTwips = SpacingTwips)
        {
            var rPr = new RunProperties(new Spacing { Val = spacingTwips });
            if (scale != 100) rPr.Append(new CharacterScale { Val = scale });
            return new Run(rPr, new Text(Sample));
        }

        /// <summary>A one-cell table holding <paramref name="run"/>; the grid is required or no cell renders.</summary>
        private static Table TableWith(Run run) => TableWith(new Paragraph(run));

        private static Table TableWith(Paragraph paragraph) => new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Dxa, Width = "5000" }),
            new TableGrid(new GridColumn { Width = "5000" }),
            new TableRow(
                new TableCell(
                    new TableCellProperties(
                        new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "5000" }),
                    paragraph)));

        /// <summary>
        /// The document converted on the <paramref name="legacy"/> renderer or the Builder path,
        /// its first page's content decoded — the Builder path deflates its streams whatever the
        /// option says, so the content is read through the parser rather than off the raw bytes.
        /// </summary>
        private static string Convert(Action<Body> build, bool legacy)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                build(body);
                main.Document = new Document(body);
                main.Document.Save();
            }
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
                new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = legacy });

            using var pdfStream = new MemoryStream(pdf);
            using var document = PdfDocument.Open(pdfStream);
            string content = Encoding.Latin1.GetString(document.Pages[0].GetContentStreamData());
            Assert.True(content.Length > 0, "the page has no content stream");
            return content;
        }
    }
}
