// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A transparent PNG in a DOCX keeps its transparency: the image XObject gets a real
    /// <c>/SMask</c> rather than having its alpha flattened against white.
    /// </summary>
    /// <remarks>
    /// <para><b>The old behaviour was a white rectangle.</b> The DOCX image path composited alpha
    /// over white inside its PNG decoder, which is right only where the picture sits on bare page —
    /// over a coloured shape, a shaded table cell, or another image, a transparent-background logo
    /// drew an opaque box around itself. Word documents put logos over exactly those things.</para>
    ///
    /// <para>The flattening was correct <i>as a call-site decision</i> and wrong as a decoder's
    /// contract; when the two PNG decoders in this tree were merged into
    /// <c>Core.Imaging.PngImageDecoder</c> the alpha became a first-class output, and this path had
    /// somewhere to put it.</para>
    /// </remarks>
    public class TransparentPngTests
    {
        private const int Inch = 914400;

        /// <summary>
        /// The claim, asserted on the mask's <b>samples</b>. A uniformly opaque <c>/SMask</c> is
        /// exactly what a dropped alpha channel produces and is structurally indistinguishable from
        /// a correct one, so the fixture's alpha varies along x and the assertion reads it back.
        /// </summary>
        [Fact]
        public void ATransparentPng_IsEmbeddedWithASoftMask()
        {
            // 4 px wide: transparent, a third, two thirds, opaque.
            byte[] png = DocxImageBuilder.MakeAlphaPng(4, 2, 200, 100, 50,
                                                       x => (byte)(x * 85));
            byte[] docx = DocxWithImage(png);

            using var doc = PdfDocument.Open(new MemoryStream(Convert(docx)));
            var image = FirstImageXObject(doc);

            var mask = doc.ResolveObject(image[PdfName.SMask]) as PdfStream;
            Assert.True(mask != null,
                "the transparent PNG was embedded with no /SMask, so its alpha was flattened away");

            Assert.Equal("DeviceGray", (doc.ResolveObject(mask![PdfName.ColorSpace]) as PdfName)?.Value);

            byte[] alpha = mask.GetDecodedData();
            Assert.Equal(8, alpha.Length);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 4; x++)
                    Assert.Equal((byte)(x * 85), alpha[y * 4 + x]);
        }

        /// <summary>
        /// <b>And the colour samples are the file's own, not pre-multiplied against white.</b> The
        /// mask assertion alone passes for an implementation that writes a correct mask over samples
        /// it had already flattened — which would double-lighten every partly transparent pixel.
        /// </summary>
        [Fact]
        public void TheColourSamplesAreNotAlsoCompositedOverWhite()
        {
            byte[] png = DocxImageBuilder.MakeAlphaPng(4, 1, 200, 100, 50, x => (byte)(x * 85));

            using var doc = PdfDocument.Open(new MemoryStream(Convert(DocxWithImage(png))));
            byte[] rgb = FirstImageXObject(doc).GetDecodedData();

            Assert.Equal(12, rgb.Length);
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(200, rgb[x * 3]);
                Assert.Equal(100, rgb[x * 3 + 1]);
                Assert.Equal(50, rgb[x * 3 + 2]);
            }
        }

        /// <summary>
        /// The control: an opaque PNG gets <b>no</b> <c>/SMask</c>. Without it, "a mask is written"
        /// would be satisfied by an implementation that writes an all-opaque mask for every image,
        /// costing a second XObject per picture and proving nothing about transparency.
        /// </summary>
        [Fact]
        public void AnOpaquePng_GetsNoSoftMask()
        {
            byte[] docx = DocxWithImage(DocxImageBuilder.MakeTruecolorPng(4, 2, 10, 20, 30));

            using var doc = PdfDocument.Open(new MemoryStream(Convert(docx)));

            Assert.Null(FirstImageXObject(doc)[PdfName.SMask]);
        }

        // ------------------------------------------------------------------

        private static PdfStream FirstImageXObject(PdfDocument doc)
        {
            var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
            var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
            Assert.True(xobjects != null && xobjects.Count > 0, "the page declares no XObject resources");

            foreach (var entry in xobjects!)
            {
                if (doc.ResolveObject(entry.Value) is PdfStream s &&
                    (doc.ResolveObject(s[PdfName.Subtype]) as PdfName)?.Value == "Image")
                    return s;
            }

            throw new Xunit.Sdk.XunitException("no image XObject on the page");
        }

        private static byte[] Convert(byte[] docx)
            // Uncompressed streams keep the dictionaries inspectable; the image data itself is
            // still /FlateDecode, which GetDecodedData undoes.
            => DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { CompressStreams = false });

        private static byte[] DocxWithImage(byte[] png)
            => DocxImageBuilder.CreateDocxWithImage(png, "image/png", Inch, Inch);
    }
}
