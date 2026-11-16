// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A picture in a header or a footer carries a relationship owned by <b>that</b> part, not by
    /// the main document. Relationship ids are numbered per-part, so the same id routinely names
    /// one thing in a header and something else entirely in the body — which is why resolving a
    /// header blip against <c>MainDocumentPart</c> painted the wrong image, dropped the picture,
    /// or threw straight out of the converter.
    /// </summary>
    public class HeaderFooterImagePartTests
    {
        private static string ConvertToText(byte[] docx)
        {
            // Uncompressed streams so the image XObject dictionaries are inspectable as text.
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false });
            return Encoding.Latin1.GetString(pdf);
        }

        /// <summary>The same conversion through the Builder model rather than the legacy renderer.</summary>
        private static string ConvertViaBuilder(byte[] docx)
        {
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { UseLegacyRenderer = false, CompressStreams = false });
            return Encoding.Latin1.GetString(pdf);
        }

        /// <summary>
        /// The header and the main document both own the relationship id <c>rId9</c>, pointing at
        /// two differently sized images — the ordinary case, since ids restart per part. Only the
        /// header's own 4×3 image may reach the PDF.
        /// </summary>
        [Fact]
        public void HeaderImage_WithIdAlsoUsedByMainDocument_PaintsTheHeaderPartsImage()
        {
            byte[] docx = BuildDocxWithBandImage(
                headerImage: DocxImageBuilder.MakeTruecolorPng(4, 3, 10, 20, 30),
                mainImage: DocxImageBuilder.MakeTruecolorPng(7, 5, 200, 50, 50),
                sharedRelId: "rId9",
                inFooter: false);

            string pdf = ConvertToText(docx);

            Assert.Contains("/Subtype /Image", pdf);
            Assert.Contains("/Width 4", pdf);      // the header part's image
            Assert.Contains("/Height 3", pdf);
            Assert.DoesNotContain("/Width 7", pdf); // never the main document's same-id image
            Assert.DoesNotContain("/Height 5", pdf);
        }

        /// <summary>The same rule for a footer, which hangs off its own part too.</summary>
        [Fact]
        public void FooterImage_WithIdAlsoUsedByMainDocument_PaintsTheFooterPartsImage()
        {
            byte[] docx = BuildDocxWithBandImage(
                headerImage: DocxImageBuilder.MakeTruecolorPng(4, 3, 10, 20, 30),
                mainImage: DocxImageBuilder.MakeTruecolorPng(7, 5, 200, 50, 50),
                sharedRelId: "rId9",
                inFooter: true);

            string pdf = ConvertToText(docx);

            Assert.Contains("/Width 4", pdf);
            Assert.Contains("/Height 3", pdf);
            Assert.DoesNotContain("/Width 7", pdf);
        }

        /// <summary>
        /// The id the header blip names exists <i>only</i> on the header part. Resolving it against
        /// the main document made <c>GetPartById</c> throw, and nothing on the picture path caught
        /// it — so the converter turned a perfectly valid document into a
        /// <c>DocxToPdfException</c>. The image must render instead.
        /// </summary>
        [Fact]
        public void HeaderImage_WithIdTheMainDocumentDoesNotOwn_IsRendered()
        {
            byte[] docx = BuildDocxWithBandImage(
                headerImage: DocxImageBuilder.MakeTruecolorPng(6, 5, 10, 20, 30),
                mainImage: null,                 // main owns no image relationship at all
                sharedRelId: "rId777",
                inFooter: false);

            string pdf = ConvertToText(docx);

            Assert.Contains("/Subtype /Image", pdf);
            Assert.Contains("/Width 6", pdf);
            Assert.Contains("/Height 5", pdf);
        }

        /// <summary>
        /// A body picture resolves against the main document while a header picture sharing its
        /// relationship id resolves against the header part — both images reach the PDF, each at
        /// its own size.
        ///
        /// <para><b>This replaces <c>BodyImage_StillResolvesAgainstMainDocumentPart</c>, which
        /// could not fail.</b> That test converted a document holding one body picture and claimed
        /// in its remarks to guard the <c>?? fallbackOwner</c> operand of
        /// <see cref="OpenXmlPartLookup.Resolve"/>. It guarded neither the fallback nor the choice
        /// of operand: for a picture loaded from the main document part <c>OwningPart(blip)</c>
        /// <i>is</i> <c>MainDocumentPart</c>, so the two operands are the same object and a
        /// single-part fixture cannot distinguish them. Measured, not argued — swapping them to
        /// <c>fallbackOwner ?? OwningPart(element)</c> left that test green while the three
        /// header/footer tests above went red.</para>
        ///
        /// <para>What a body picture <i>can</i> falsify is per-part resolution in a document that
        /// has both owners at once. The main document and the header each own <c>rId9</c>, pointing
        /// at differently sized images, and each draws it. Resolution collapsing onto one part — in
        /// either direction — drops one of the two images, so each assertion below is a live
        /// claim.</para>
        /// </summary>
        [Fact]
        public void BodyAndHeaderPictures_SharingOneRelationshipId_EachResolveAgainstTheirOwnPart()
        {
            byte[] docx = BuildDocxWithBandImage(
                headerImage: DocxImageBuilder.MakeTruecolorPng(4, 3, 10, 20, 30),
                mainImage: DocxImageBuilder.MakeTruecolorPng(9, 2, 200, 50, 50),
                sharedRelId: "rId9",
                inFooter: false,
                bodyDrawsSharedId: true);

            string pdf = ConvertToText(docx);

            // Exact tokens: "/Width 9" must not be satisfied by a "/Width 90" elsewhere.
            Assert.Matches(@"/Width 9(?![0-9])", pdf);   // the body's image, off MainDocumentPart
            Assert.Matches(@"/Height 2(?![0-9])", pdf);
            Assert.Matches(@"/Width 4(?![0-9])", pdf);   // the header's image, off the HeaderPart
            Assert.Matches(@"/Height 3(?![0-9])", pdf);

            // Two pictures were drawn, so two image XObjects — neither part's image stood in for
            // the other's, which would leave one distinct image and a duplicate.
            Assert.Equal(2, Regex.Matches(pdf, @"/Subtype\s*/Image").Count);
        }

        /// <summary>
        /// The <c>?? fallbackOwner</c> operand of <see cref="OpenXmlPartLookup.Resolve"/> has no
        /// reachable fixture in a converted document: every element the converter walks was loaded
        /// from a part, so <c>OwningPart</c> is never null on that path. Deleting the operand
        /// outright leaves the whole DocxToPdf suite green — measured — which is why the contract
        /// is asserted here directly, against a blip that was <i>constructed</i> rather than
        /// loaded. That is the one shape reaching it, and the shape the doc comment promises.
        /// </summary>
        [Fact]
        public void Resolve_ForAnElementDetachedFromAnyPart_UsesTheFallbackOwner()
        {
            using var ms = new MemoryStream();
            using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);
            var mainPart = doc.AddMainDocumentPart();
            var imagePart = mainPart.AddNewPart<ImagePart>("image/png", "rId9");
            using (var s = new MemoryStream(DocxImageBuilder.MakeTruecolorPng(3, 3, 1, 2, 3)))
                imagePart.FeedData(s);
            mainPart.Document = new Document(new Body(new Paragraph()));

            // Never inserted into any part's tree — the fallback's whole precondition.
            var detached = new A.Blip { Embed = "rId9" };

            Assert.Null(OpenXmlPartLookup.OwningPart(detached));
            Assert.Same(imagePart, OpenXmlPartLookup.Resolve(detached, "rId9", mainPart));
        }

        // Builds a DOCX whose header (or footer) holds an inline picture referencing
        // <paramref name="sharedRelId"/> on its own part. When <paramref name="mainImage"/> is
        // given, the main document part is made to own the very same id, pointing at a different
        // image — the collision that per-part id numbering makes routine. With
        // <paramref name="bodyDrawsSharedId"/> the body draws that id too, so one document holds
        // both owners of the id AND a picture off each of them.
        // ------------------------------------------------------------------
        //  The SAME rule on the Builder-model path, which had its own resolution
        // ------------------------------------------------------------------
        //
        //  DocxToBuilderMapper.ExtractImage resolved r:embed against MainDocumentPart alone, so
        //  every failure mode OpenXmlPartLookup was written to close was live again on this path
        //  only: a header picture painted the BODY's same-id image, and an id the main document
        //  does not own threw straight out of GetPartById.
        //
        //  Two converters reading one file must agree, so each of these is the legacy test above
        //  run through the other path.

        /// <summary>
        /// The header's own image is painted, not the body's image that shares its id.
        /// </summary>
        [Fact]
        public void ViaBuilder_HeaderImage_WithIdAlsoUsedByMainDocument_PaintsTheHeaderPartsImage()
        {
            byte[] docx = BuildDocxWithBandImage(
                headerImage: DocxImageBuilder.MakeTruecolorPng(4, 3, 10, 20, 30),
                mainImage: DocxImageBuilder.MakeTruecolorPng(7, 5, 200, 50, 50),
                sharedRelId: "rId9",
                inFooter: false);

            string pdf = ConvertViaBuilder(docx);

            Assert.Matches(@"/Width 4(?![0-9])", pdf);
            Assert.Matches(@"/Height 3(?![0-9])", pdf);
            Assert.DoesNotMatch(@"/Width 7(?![0-9])", pdf);
        }

        /// <summary>
        /// An id only the header part owns renders, rather than throwing out of the converter.
        /// </summary>
        /// <remarks>
        /// <c>GetPartById</c> raises <see cref="System.ArgumentOutOfRangeException"/> for an id its
        /// part does not own, and the mapper's call sat outside the <c>try</c> that guards the
        /// image read — so a valid document became an exception. <c>TryGetPart</c> reports null.
        /// </remarks>
        [Fact]
        public void ViaBuilder_HeaderImage_WithIdTheMainDocumentDoesNotOwn_IsRendered()
        {
            byte[] docx = BuildDocxWithBandImage(
                headerImage: DocxImageBuilder.MakeTruecolorPng(6, 5, 10, 20, 30),
                mainImage: null,
                sharedRelId: "rId777",
                inFooter: false);

            string pdf = ConvertViaBuilder(docx);

            Assert.Contains("/Subtype /Image", pdf);
            Assert.Matches(@"/Width 6(?![0-9])", pdf);
            Assert.Matches(@"/Height 5(?![0-9])", pdf);
        }

        /// <summary>
        /// Both owners at once, so a resolution collapsing onto either part drops one image.
        /// This is the acceptance control for the body half: the mapper must still reach
        /// <c>MainDocumentPart</c> for a body picture.
        /// </summary>
        [Fact]
        public void ViaBuilder_BodyAndHeaderPictures_SharingOneRelationshipId_EachResolveAgainstTheirOwnPart()
        {
            byte[] docx = BuildDocxWithBandImage(
                headerImage: DocxImageBuilder.MakeTruecolorPng(4, 3, 10, 20, 30),
                mainImage: DocxImageBuilder.MakeTruecolorPng(9, 2, 200, 50, 50),
                sharedRelId: "rId9",
                inFooter: false,
                bodyDrawsSharedId: true);

            string pdf = ConvertViaBuilder(docx);

            Assert.Matches(@"/Width 9(?![0-9])", pdf);
            Assert.Matches(@"/Height 2(?![0-9])", pdf);
            Assert.Matches(@"/Width 4(?![0-9])", pdf);
            Assert.Matches(@"/Height 3(?![0-9])", pdf);
        }

        private static byte[] BuildDocxWithBandImage(byte[] headerImage, byte[]? mainImage,
            string sharedRelId, bool inFooter, bool bodyDrawsSharedId = false)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                if (mainImage != null)
                {
                    var mainImgPart = mainPart.AddNewPart<ImagePart>("image/png", sharedRelId);
                    using var s = new MemoryStream(mainImage);
                    mainImgPart.FeedData(s);
                }

                var drawing = DocxImageBuilder.BuildInlineDrawing(sharedRelId, 914400, 914400);
                var sectPr = new SectionProperties();

                if (inFooter)
                {
                    var footerPart = mainPart.AddNewPart<FooterPart>("rIdBand");
                    var bandImg = footerPart.AddNewPart<ImagePart>("image/png", sharedRelId);
                    using (var s = new MemoryStream(headerImage)) bandImg.FeedData(s);
                    footerPart.Footer = new Footer(new Paragraph(new Run(drawing)));
                    footerPart.Footer.Save();
                    sectPr.AppendChild(new FooterReference
                    { Type = HeaderFooterValues.Default, Id = "rIdBand" });
                }
                else
                {
                    var headerPart = mainPart.AddNewPart<HeaderPart>("rIdBand");
                    var bandImg = headerPart.AddNewPart<ImagePart>("image/png", sharedRelId);
                    using (var s = new MemoryStream(headerImage)) bandImg.FeedData(s);
                    headerPart.Header = new Header(new Paragraph(new Run(drawing)));
                    headerPart.Header.Save();
                    sectPr.AppendChild(new HeaderReference
                    { Type = HeaderFooterValues.Default, Id = "rIdBand" });
                }

                // A second Drawing instance: an OpenXmlElement has one parent, so the band's
                // drawing cannot also hang off the body.
                var bodyRun = bodyDrawsSharedId
                    ? new Run(DocxImageBuilder.BuildInlineDrawing(sharedRelId, 914400, 914400))
                    : new Run(new Text("body"));

                mainPart.Document = new Document(new Body(new Paragraph(bodyRun), sectPr));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }
    }
}
