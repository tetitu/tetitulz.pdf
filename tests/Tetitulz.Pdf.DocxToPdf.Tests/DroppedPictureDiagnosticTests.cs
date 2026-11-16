// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A picture the converter cannot paint is dropped rather than allowed to fail the document —
    /// the right trade, and a change from the days when an unresolvable <c>r:embed</c> threw
    /// straight out of <c>GetPartById</c>. But a dropped picture and a document that never had one
    /// produce byte-identical PDFs, so tolerating the failure without reporting it leaves the
    /// caller unable to tell the two apart (CLAUDE.md → Diagnostics Pattern).
    ///
    /// <para>
    /// Every test here therefore asserts <b>both</b> halves: the conversion completed and produced
    /// the rest of the document, <i>and</i> the reason the picture is missing reached
    /// <see cref="DocxToPdfConverter.LastDiagnostic"/> / <c>DiagnosticReported</c>. Asserting only
    /// that a PDF came back would pass with the whole reporting path deleted, which is banned
    /// pattern 3.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class DroppedPictureDiagnosticTests
    {
        private const long Inch = 914400;

        // ---------------------------------------------------------------- the reported failures

        /// <summary>
        /// The archetype: the blip names <c>rIdGone</c>, which no part in the package owns. The
        /// picture cannot be painted, the conversion must still succeed, and the diagnostic must
        /// name the id so the caller can find the offending drawing in the source.
        /// </summary>
        [Fact]
        public void UnresolvableEmbed_ConvertsWithoutTheImage_AndReportsTheRelationshipId()
        {
            byte[] docx = DocxWithBlipNaming("rIdGone", imagePart: null);

            string pdf = Convert(docx, out var reported);

            // Converted: the rest of the document is there...
            Assert.StartsWith("%PDF-", pdf);
            Assert.Contains("(AFTER) Tj", pdf);
            // ...and the picture genuinely is not (otherwise there is nothing to report).
            Assert.DoesNotContain("/Subtype /Image", pdf);

            // Reported: the reason, naming the id that would not resolve.
            string drop = Assert.Single(DecodeDiagnostics(reported));
            Assert.Contains("rIdGone", drop);
            Assert.Contains("resolves to no relationship", drop);
            Assert.Equal(drop, DocxToPdfConverter.LastDiagnostic);
        }

        /// <summary>
        /// The id resolves, but to a header part rather than an image part — a stale or crossed
        /// relationship. Distinguished from "no such relationship" in the message, because the two
        /// call for different repairs in the producing document.
        /// </summary>
        [Fact]
        public void EmbedResolvingToANonImagePart_ConvertsWithoutTheImage_AndSaysWhatItFound()
        {
            byte[] docx = DocxWithBlipNamingAHeaderPart("rIdCrossed");

            string pdf = Convert(docx, out var reported);

            Assert.Contains("(AFTER) Tj", pdf);
            Assert.DoesNotContain("/Subtype /Image", pdf);

            string drop = Assert.Single(DecodeDiagnostics(reported));
            Assert.Contains("rIdCrossed", drop);
            Assert.Contains("rather than an image part", drop);
        }

        /// <summary>
        /// The relationship resolves to a real image part holding a format that cannot be embedded
        /// (only JPEG and non-interlaced PNG can). The content type belongs in the message: it is
        /// the whole of what the caller needs to know to convert the asset up front.
        /// </summary>
        [Fact]
        public void UndecodableImageFormat_ConvertsWithoutTheImage_AndNamesTheContentType()
        {
            byte[] docx = DocxWithBlipNaming("rIdGif", imagePart: GifBytes(), contentType: "image/gif");

            string pdf = Convert(docx, out var reported);

            Assert.Contains("(AFTER) Tj", pdf);
            Assert.DoesNotContain("/Subtype /Image", pdf);

            string drop = Assert.Single(DecodeDiagnostics(reported));
            Assert.Contains("rIdGif", drop);
            Assert.Contains("image/gif", drop);
        }

        /// <summary>
        /// The relationship and the part both exist, but the part holds no bytes — a truncated or
        /// half-written package. Reported separately from "undecodable", because there is no format
        /// to name and the repair is to re-save the source, not to convert the asset.
        /// </summary>
        [Fact]
        public void EmptyImagePart_ConvertsWithoutTheImage_AndReportsThePartIsEmpty()
        {
            byte[] docx = DocxWithBlipNaming("rIdEmpty", imagePart: Array.Empty<byte>());

            string pdf = Convert(docx, out var reported);

            Assert.Contains("(AFTER) Tj", pdf);
            Assert.DoesNotContain("/Subtype /Image", pdf);

            string drop = Assert.Single(DecodeDiagnostics(reported));
            Assert.Contains("rIdEmpty", drop);
            Assert.Contains("is empty", drop);
        }

        /// <summary>
        /// A blip with no <c>r:embed</c> at all — an image linked with <c>r:link</c> only, whose
        /// bytes were never stored in the package. Not a malformed file, but still a picture the
        /// reader will not see, so it reports too.
        /// </summary>
        [Fact]
        public void BlipWithNoEmbedAttribute_ConvertsWithoutTheImage_AndReportsTheLinkedImage()
        {
            byte[] docx = DocxWithBlipNaming("rIdUnused", imagePart: null, stripEmbedAttribute: true);

            string pdf = Convert(docx, out var reported);

            Assert.Contains("(AFTER) Tj", pdf);
            Assert.DoesNotContain("/Subtype /Image", pdf);

            string drop = Assert.Single(DecodeDiagnostics(reported));
            Assert.Contains("no r:embed", drop);
        }

        // ------------------------------------------------------------------------- the control

        /// <summary>
        /// The counterpart that keeps the three above honest: a picture that <i>does</i> resolve is
        /// painted and reports nothing. Without this, a "fix" that reported on every picture would
        /// satisfy every other test in this file while making the diagnostic worthless.
        /// </summary>
        [Fact]
        public void ResolvableEmbed_PaintsTheImage_AndReportsNoDroppedPicture()
        {
            byte[] docx = DocxWithBlipNaming("rIdGood",
                imagePart: DocxImageBuilder.MakeTruecolorPng(9, 2, 10, 20, 30),
                contentType: "image/png");

            string pdf = Convert(docx, out var reported);

            Assert.Contains("/Subtype /Image", pdf);
            Assert.Contains("/Width 9", pdf);
            Assert.Contains("/Height 2", pdf);
            Assert.Empty(DecodeDiagnostics(reported));
        }

        // ------------------------------------------------------------------------- infrastructure

        // Only the picture-drop reports. A conversion legitimately reports other things (an
        // approximated shape, a missing glyph); filtering keeps this file's assertions about the
        // path under test rather than about everything the converter happens to say.
        private static List<string> DecodeDiagnostics(List<string> reported)
            => reported.Where(m => m.StartsWith("[ImageRenderer.Decode]", StringComparison.Ordinal)).ToList();

        private static string Convert(byte[] docx, out List<string> reported)
        {
            var seen = new List<string>();
            void Collect(string m) => seen.Add(m);

            DocxToPdfConverter.DiagnosticReported += Collect;
            try
            {
                // Uncompressed streams so image XObject dictionaries and text are inspectable.
                byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx,
                    new DocxToPdfOptions { CompressStreams = false });
                reported = seen;
                return Encoding.Latin1.GetString(pdf);
            }
            finally
            {
                DocxToPdfConverter.DiagnosticReported -= Collect;
            }
        }

        // A one-paragraph body holding an inline picture that names <paramref name="relId"/>,
        // followed by the text AFTER so a test can prove the conversion carried on past the
        // dropped picture. The image part is created only when bytes are supplied — with none,
        // the id resolves to nothing at all.
        private static byte[] DocxWithBlipNaming(string relId, byte[] imagePart,
            string contentType = "image/png", bool stripEmbedAttribute = false)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                if (imagePart != null)
                {
                    var part = mainPart.AddNewPart<ImagePart>(contentType, relId);
                    using var s = new MemoryStream(imagePart);
                    part.FeedData(s);
                }

                var drawing = DocxImageBuilder.BuildInlineDrawing(relId, Inch, Inch);
                if (stripEmbedAttribute)
                    drawing.Descendants<A.Blip>().First().Embed = null;

                mainPart.Document = new Document(new Body(
                    new Paragraph(new Run(drawing)),
                    new Paragraph(new Run(new Text("AFTER")))));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        // The same body, but the id the blip names belongs to a header part: it resolves, and to
        // the wrong kind of part.
        private static byte[] DocxWithBlipNamingAHeaderPart(string relId)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                var headerPart = mainPart.AddNewPart<HeaderPart>(relId);
                headerPart.Header = new Header(new Paragraph(new Run(new Text("hdr"))));
                headerPart.Header.Save();

                mainPart.Document = new Document(new Body(
                    new Paragraph(new Run(DocxImageBuilder.BuildInlineDrawing(relId, Inch, Inch))),
                    new Paragraph(new Run(new Text("AFTER")))));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        // A minimal GIF87a header — a real image format the embedder does not support, so it
        // reaches the "neither JPEG nor decodable PNG" report rather than any earlier one.
        private static byte[] GifBytes()
        {
            var gif = new List<byte>(Encoding.ASCII.GetBytes("GIF87a"));
            gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x3B });
            return gif.ToArray();
        }
    }
}
