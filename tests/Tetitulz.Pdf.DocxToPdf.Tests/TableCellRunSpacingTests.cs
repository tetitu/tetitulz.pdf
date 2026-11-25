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
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// <c>w:w</c> (character scale) and <c>w:spacing</c> (tracking) inside a <b>table cell</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this file exists.</b> A body paragraph honoured both attributes — measuring
    /// with them and emitting <c>Tz</c>/<c>Tc</c> — and the table path read <b>neither</b>. Its
    /// segments carried no scale or tracking at all, so the same run rendered at one width in a
    /// paragraph and a different width inside a cell, depending only on which container it sat in.
    /// A 150%-scaled run in a cell drew at 100%.</para>
    ///
    /// <para><b>GAPS.md described this as a measure-versus-draw divergence, and it was not.</b>
    /// The row said the wrap loop "measures through the resolver directly, so a run is wrapped at
    /// one width and drawn at another". Reading the code found no such disagreement: the cell path
    /// dropped the attributes at segment construction, before either step, so wrapping and drawing
    /// agreed with each other and both differed from Word. The register row was a hypothesis; the
    /// code was the authority.</para>
    ///
    /// <para><b>Asserted as a cell-versus-paragraph comparison, not against a number.</b> What
    /// makes it a defect is that one document rendered two ways, so the test says the two
    /// containers agree — which no fixed coordinate could, and which keeps holding if the metrics
    /// ever change.</para>
    /// </remarks>
    public class TableCellRunSpacingTests
    {
        private const string Sample = "Spaced";

        // ── the operators reach the cell at all ─────────────────────────────────────

        /// <summary>
        /// The cell's content stream carries both operators. Without them the page draws unscaled,
        /// untracked text while the layout has reserved room for neither.
        /// </summary>
        [Fact]
        public void ACellRun_EmitsBothScaleAndTracking()
        {
            string pdf = Convert(body => body.Append(TableWith(ScaledRun())));

            Assert.Contains("150 Tz\n", pdf, StringComparison.Ordinal);
            Assert.Contains(" Tc\n", pdf, StringComparison.Ordinal);
        }

        /// <summary>
        /// And they are reset before the text object closes, so the next cell — or the next line —
        /// does not inherit them. A leaked <c>Tz</c> rescales everything after it.
        /// </summary>
        [Fact]
        public void TheOperatorsAreResetBeforeTheTextObjectCloses()
        {
            string pdf = Convert(body => body.Append(TableWith(ScaledRun())));

            int scaled = pdf.IndexOf("150 Tz\n", StringComparison.Ordinal);
            Assert.True(scaled >= 0, "the scaled run never reached the page");

            int reset = pdf.IndexOf("100 Tz\n", scaled, StringComparison.Ordinal);
            int end = pdf.IndexOf("ET\n", scaled, StringComparison.Ordinal);

            Assert.True(reset >= 0 && reset < end,
                "the horizontal scale must be restored before ET, or it leaks into the next text object.");
        }

        // ── the cell and the paragraph agree ────────────────────────────────────────

        /// <summary>
        /// <b>The claim.</b> A scaled run occupies the same width inside a cell as outside one.
        /// Measured from the emitted operators rather than asserted against a constant: a cell that
        /// drew the run at 100% while a paragraph drew it at 150% would fail it.
        /// </summary>
        [Fact]
        public void ACellAppliesTheSameScaleAsAParagraph()
        {
            string inCell = Convert(body => body.Append(TableWith(ScaledRun())));
            string inBody = Convert(body => body.Append(new Paragraph(ScaledRun())));

            Assert.Equal(ScalePercent(inBody), ScalePercent(inCell));
        }

        /// <summary>
        /// The control: an <b>unscaled</b> cell run emits no scale operator at all, so the test
        /// above is pinning the attribute rather than "the cell always writes 150 Tz". A document
        /// that sets nothing must produce the bytes it always did.
        /// </summary>
        [Fact]
        public void AnUnscaledCellRun_EmitsNoScaleOperator()
        {
            string pdf = Convert(body => body.Append(TableWith(new Run(new Text(Sample)))));

            // The fixture has to actually render, or this passes against a blank page — which is
            // exactly how a missing w:tblGrid hid the whole table the first time round.
            Assert.Contains(Sample, pdf, StringComparison.Ordinal);

            Assert.DoesNotContain(" Tz\n", pdf, StringComparison.Ordinal);
            Assert.DoesNotContain(" Tc\n", pdf, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>The percent of the first <c>Tz</c> in the stream, or 100 when there is none.</summary>
        private static int ScalePercent(string pdf)
        {
            Match m = Regex.Match(pdf, @"(\d+) Tz\n");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 100;
        }

        private static Run ScaledRun() => new Run(
            new RunProperties(
                new Spacing { Val = 40 },            // +2pt tracking → Tc
                new CharacterScale { Val = 150 }),   // 150% horizontal scale → Tz
            new Text(Sample));

        /// <summary>
        /// A one-cell table holding <paramref name="run"/>.
        /// </summary>
        /// <remarks>
        /// The <c>w:tblGrid</c> is required, not decoration: <c>TableRenderer.LayoutTable</c>
        /// returns an empty layout when the column widths resolve to nothing, so a table without
        /// one renders no cells at all — and every assertion about cell content then passes
        /// vacuously against a blank page.
        /// </remarks>
        private static Table TableWith(Run run) => new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Dxa, Width = "5000" }),
            new TableGrid(new GridColumn { Width = "5000" }),
            new TableRow(
                new TableCell(
                    new TableCellProperties(
                        new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "5000" }),
                    new Paragraph(run))));

        private static string Convert(Action<Body> build)
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
            return Encoding.Latin1.GetString(
                DocxToPdfConverter.ConvertToBytes(ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));
        }
    }
}
