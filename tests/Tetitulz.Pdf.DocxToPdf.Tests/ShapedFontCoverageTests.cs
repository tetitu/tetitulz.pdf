// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Which code points this renderer demands a candidate face cover: the ones the run will be
    /// <b>drawn</b> as, which for Arabic are the presentation forms shaping substitutes and not the
    /// letters the document stores.
    /// </summary>
    /// <remarks>
    /// <para>Resolved by the source text's code points, a face covering ب ي ت was accepted and then
    /// had no glyph for one character of the word — and this renderer's
    /// <c>MissingGlyphPolicy.SubstituteSpace</c> turns that into a space per glyph, silently. The
    /// four other producers in the tree asked the same wrong question;
    /// <c>Core.Text.TextRunComposer.CodePointsToCover</c> is the one answer they now share.</para>
    ///
    /// <para><b>Host-independent, because the machine's own fonts would decide it otherwise.</b>
    /// Windows and most Linux containers carry a face covering both the letters and the forms, so
    /// asking the wrong question there still resolves a font that works and the defect is invisible.
    /// The font list is replaced for the duration of each test with exactly one synthetic file —
    /// the same seam <c>HostFontLicenceTests</c> uses, and its remarks explain why it exists.</para>
    /// </remarks>
    /// <remarks>
    /// <para><b>In the diagnostics collection</b>, for both of the reasons that collection exists
    /// and one more: these tests subscribe to the process-wide <c>DocxDiagnostics.OnEvent</c>, and
    /// they replace <see cref="FontResolver"/>'s host font list — which clears its <b>static</b>
    /// resolution caches on the way in and out. A class resolving fonts in parallel would repopulate
    /// those caches from the real machine between the clear and the resolution, so the seam's
    /// isolation is only as good as the serialisation around it.</para>
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public sealed class ShapedFontCoverageTests : IDisposable
    {
        /// <summary>بيت — three letters that all join, so every one of them is substituted.</summary>
        private const string Arabic = "بيت";

        /// <summary>
        /// What the shaper draws <see cref="Arabic"/> as: initial beh, medial yeh, final teh.
        /// Written out rather than taken from the shaper, so the fixture states the expectation
        /// instead of agreeing with the code under test by construction.
        /// </summary>
        private const string ArabicShaped = "ﺑﻴﺖ";

        private readonly List<string> _written = new List<string>();

        public void Dispose()
        {
            foreach (string path in _written)
                try { File.Delete(path); } catch { /* a leftover temp file is not a test failure */ }
        }

        private string HostFontFile(string covered)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "tetitulz-docx-shaped-" + Guid.NewGuid().ToString("N") + ".ttf");
            File.WriteAllBytes(path, MinimalTrueType.BuildNamed(covered, "DocxShapedProbe"));
            _written.Add(path);
            return path;
        }

        /// <summary>
        /// Resolves an Arabic run against a host holding exactly one face, the one covering
        /// <paramref name="covered"/>.
        /// </summary>
        private TrueTypeFontProgram ResolveAgainst(string covered)
        {
            using (FontResolver.UseHostFontFilesForTest(new[] { HostFontFile(covered) }))
                return FontResolver.Resolve("DocxShapedProbe", bold: false, italic: false,
                                            UnicodeText.CodePointsToCover(Arabic));
        }

        /// <summary>
        /// The refusal: a face that covers the letters cannot draw their joined forms, so it is not
        /// the face this run resolves to.
        /// </summary>
        [Fact]
        public void AFontCoveringTheLettersButNotTheirShapedFormsIsNotTheFontChosen()
        {
            Assert.Null(ResolveAgainst(Arabic));
        }

        /// <summary>
        /// The acceptance control. Without it the refusal above would pass for a face that was
        /// unparseable, licence-refused, or never reached at all.
        /// </summary>
        [Fact]
        public void AFontCoveringTheShapedFormsIsTheFontChosen()
        {
            TrueTypeFontProgram program = ResolveAgainst(Arabic + ArabicShaped);

            Assert.NotNull(program);
            foreach (char c in ArabicShaped)
                Assert.True(program.Covers(c), "the resolved face must cover " + ((int)c).ToString("X4"));
        }

        /// <summary>
        /// The same refusal through the converter, so the assertion is about the question the
        /// <b>renderer</b> asks and not only about the helper it asks it with: a host holding one
        /// letters-only face converts an Arabic paragraph and reports the forms as uncovered.
        /// </summary>
        /// <remarks>
        /// The message names what was actually needed. Naming U+0628 would send a reader looking
        /// for a font that was right there on the machine.
        /// </remarks>
        [Fact]
        public void TheConverterReportsTheShapedFormsAsUncovered()
        {
            var reported = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            Action<string> collect = m => { if (System.Environment.CurrentManagedThreadId == collectingThread) reported.Add(m); };

            DocxDiagnostics.OnEvent += collect;
            try
            {
                using (FontResolver.UseHostFontFilesForTest(new[] { HostFontFile(Arabic) }))
                    DocxToPdfConverter.ConvertToBytes(ArabicDocx(), new DocxToPdfOptions());
            }
            finally { DocxDiagnostics.OnEvent -= collect; }

            Assert.Contains(reported, m => m.Contains("U+FE91", StringComparison.Ordinal));
        }

        /// <summary>
        /// And again for a run inside a table cell, which resolves its own font: the body paragraph
        /// and the cell are two call sites, and a mutation of the cell's survives the body's test.
        /// </summary>
        [Fact]
        public void TheConverterReportsTheShapedFormsAsUncoveredInATableCell()
        {
            var reported = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            Action<string> collect = m => { if (System.Environment.CurrentManagedThreadId == collectingThread) reported.Add(m); };

            DocxDiagnostics.OnEvent += collect;
            try
            {
                using (FontResolver.UseHostFontFilesForTest(new[] { HostFontFile(Arabic) }))
                    DocxToPdfConverter.ConvertToBytes(ArabicCellDocx(), new DocxToPdfOptions());
            }
            finally { DocxDiagnostics.OnEvent -= collect; }

            Assert.Contains(reported, m => m.Contains("U+FE91", StringComparison.Ordinal));
        }

        /// <summary>A one-paragraph DOCX whose only run is <see cref="Arabic"/>.</summary>
        private static byte[] ArabicDocx()
            => Docx(body => body.Append(new Paragraph(new Run(new Text(Arabic)))));

        /// <summary>The same text, in a table cell and nowhere else.</summary>
        private static byte[] ArabicCellDocx()
            => Docx(body =>
            {
                var table = new Table();
                var row = new TableRow();
                row.Append(new TableCell(new Paragraph(new Run(new Text(Arabic)))));
                table.Append(row);
                body.Append(table);
            });

        private static byte[] Docx(Action<Body> build)
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
            return ms.ToArray();
        }

        /// <summary>
        /// And the drawn run really is those forms — the measurement the resolution is for. A face
        /// covering only the letters would compose three spaces.
        /// </summary>
        [Fact]
        public void TheRunIsDrawnAsTheFormsTheResolutionAskedFor()
        {
            var program = TrueTypeFontProgram.Parse(MinimalTrueType.Build(Arabic + ArabicShaped));

            UnicodeText.GlyphRun run = UnicodeText.Shape(Arabic, program, 12);

            Assert.Equal(Arabic.Length, run.Gids.Length);
            foreach (var (gid, _) in run.Used)
                Assert.NotEqual(0, (int)gid);
        }
    }
}
