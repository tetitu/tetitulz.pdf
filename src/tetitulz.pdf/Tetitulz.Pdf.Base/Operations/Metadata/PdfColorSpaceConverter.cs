// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// Reports and converts colour spaces used in a PDF document.
    ///
    /// <para>
    /// <b>Inspection:</b> <see cref="GetColorSpaces(Stream)"/> returns the family of every colour
    /// space declared in a page's or a form XObject's <c>/Resources /ColorSpace</c> dictionary, and
    /// of every image XObject's own <c>/ColorSpace</c>.
    /// </para>
    ///
    /// <para><b>Which images.</b> Both directions reach the same set, through
    /// <see cref="DocumentImageIndex"/>: every image XObject a page draws, through nested forms,
    /// tiling patterns, Type 3 fonts and annotation appearances, each object once — and, since batch
    /// 109, every <b>inline image</b> (<c>BI … ID … EI</c>) in a page's content streams and in each
    /// container that walk visits (a form XObject, a tiling pattern cell, an appearance stream),
    /// rewritten in its stream, which is written back as the parsed operations
    /// (<see cref="ContentOperationWriter"/>) only when an image in it converted. A Type 3 glyph
    /// procedure's inline image is not reached: the walk visits the font, not its glyph streams.</para>
    ///
    /// <para>
    /// <b>Conversion:</b>
    /// <list type="bullet">
    ///   <item><see cref="ConvertRgbToGrayscale"/> converts all DeviceRGB raster images
    ///     to DeviceGray using ITU-R BT.601 luminance weighting.</item>
    ///   <item><see cref="ConvertRgbToCmyk(Stream,Stream,double)"/> converts DeviceRGB rasters to DeviceCMYK
    ///     using sRGB gamma linearisation + separable CMY + K extraction with a
    ///     user-configurable Gray Component Replacement (GCR) and Under Colour Removal
    ///     (UCR) factor. This is not a gamut-accurate ICC transform, but produces
    ///     noticeably better print-proof output than a raw `K = 1 - max(R,G,B)` formula.</item>
    /// </list>
    /// Both conversion methods write the result as an incremental update.
    /// </para>
    ///
    /// <b>Limitations:</b> Images whose whole <c>/Filter</c> chain decodes to raw samples
    /// (absent, Flate, LZW, ASCII85, ASCIIHex, RunLength — see <c>DecodesToRawSamples</c>) are
    /// converted in-place, and so is a JPEG 2000 (/JPXDecode) image, decoded by the in-house decoder
    /// under the JP2 colour rules of ISO 32000-2 §7.4.9 and written back as 8-bit Flate samples.
    /// JPEG (/DCTDecode), JBIG2 and CCITT are skipped — they require codec-level
    /// decoding which is outside this library's scope — and the chain is inspected in <b>every</b>
    /// form it can be written in, including an array and an indirect reference.
    /// </summary>
    public static class PdfColorSpaceConverter
    {
        [ThreadStatic] private static string _lastConversionWarning;

        /// <summary>
        /// What the last conversion on this thread had to discard or leave alone, or <c>null</c>
        /// when there was nothing to say. The conversion methods return only a count, so without
        /// this a caller cannot learn that an image was left unconverted because its
        /// <c>/Decode</c> could not be read, or that a colour key that could not be evaluated was
        /// dropped along with the colour space it was expressed in.
        /// </summary>
        /// <remarks>
        /// Reset at the start of every conversion, so a value read after a call always belongs
        /// to that call. Read it on the thread that made the call — the slot is
        /// <c>[ThreadStatic]</c>, per CLAUDE.md's Diagnostics Pattern.
        /// </remarks>
        public static string LastConversionWarning => _lastConversionWarning;

        private enum TargetCs { Gray, Cmyk }

        // ----------------------------------------------------------------
        //  Inspection
        // ----------------------------------------------------------------

        /// <summary>Returns the colour spaces used across all pages and image XObjects.</summary>
        public static IReadOnlyList<ColorSpaceInfo> GetColorSpaces(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return GetColorSpaces(fs);
        }

        /// <summary>Returns the colour spaces used across all pages and image XObjects.</summary>
        public static IReadOnlyList<ColorSpaceInfo> GetColorSpaces(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            return CollectColorSpaces(doc);
        }

        // ----------------------------------------------------------------
        //  Conversion: RGB → Grayscale
        // ----------------------------------------------------------------

        /// <summary>
        /// Converts all DeviceRGB raster images in the document to DeviceGray.
        /// </summary>
        /// <returns>
        /// Number of images converted. An image that does not decode to 8-bit samples — a JPEG,
        /// JBIG2 or CCITT codestream, or a depth other than 8 — is left as it was and not counted;
        /// a JPEG 2000 image is decoded, converted and written back as Flate.
        /// </returns>
        public static int ConvertRgbToGrayscale(Stream input, Stream output)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            return ConvertImages(input, output, TargetCs.Gray, _gcrFactor);
        }

        // ----------------------------------------------------------------
        //  Conversion: RGB → CMYK
        // ----------------------------------------------------------------

        /// <summary>
        /// Converts all DeviceRGB raster images in the document to DeviceCMYK.
        /// Uses a simple separable sRGB→CMYK approximation with the process-wide
        /// <see cref="GcrFactor"/>.
        /// </summary>
        /// <remarks>
        /// Prefer <see cref="ConvertRgbToCmyk(Stream,Stream,double)"/>, which carries the factor
        /// on the call. This overload reads shared mutable state, so two concurrent conversions
        /// cannot use different settings — see <see cref="GcrFactor"/>.
        /// </remarks>
        /// <returns>
        /// Number of images converted. An image that does not decode to 8-bit samples — a JPEG,
        /// JPEG 2000, JBIG2 or CCITT codestream, or a depth other than 8 — is left as it was and
        /// not counted.
        /// </returns>
        public static int ConvertRgbToCmyk(Stream input, Stream output)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            return ConvertImages(input, output, TargetCs.Cmyk, _gcrFactor);
        }

        /// <summary>
        /// Converts all DeviceRGB raster images in the document to DeviceCMYK, using
        /// <paramref name="gcrFactor"/> for this call only.
        /// </summary>
        /// <param name="gcrFactor">
        /// Gray Component Replacement, clamped to 0..1. 0 = no K channel, 1 = full GCR (the naive
        /// <c>K = 1 - max(R,G,B)</c>). 0.8 is the historical default.
        /// </param>
        /// <remarks>
        /// The factor travels with the call, the way <c>ImageReplacementOptions</c> does for
        /// PdfImageReplacer. The static <see cref="GcrFactor"/> it replaces is process-wide mutable
        /// state that changes converted pixel values: on a server running two conversions at once,
        /// one request's write lands in the middle of the other's image loop, and neither output is
        /// what its caller asked for.
        /// </remarks>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the converted PDF.</param>
        /// <returns>
        /// Number of images converted. An image that does not decode to 8-bit samples — a JPEG,
        /// JPEG 2000, JBIG2 or CCITT codestream, or a depth other than 8 — is left as it was and
        /// not counted.
        /// </returns>
        public static int ConvertRgbToCmyk(Stream input, Stream output, double gcrFactor)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            // The clamp below is Math.Max/Math.Min, which pass NaN through: a NaN factor made every
            // K sample NaN, written as 0 — a CMYK image with no black, and a successful count.
            NumericArguments.RequireFinite(gcrFactor, nameof(gcrFactor));
            return ConvertImages(input, output, TargetCs.Cmyk, gcrFactor);
        }

        /// <summary>
        /// Converts DeviceRGB rasters to DeviceCMYK <b>through a destination ICC profile's
        /// <c>B2A0</c> table</b>, rather than by the separable GCR/UCR formula.
        /// </summary>
        /// <remarks>
        /// <para><b>What "gamut-accurate" means here and what it does not.</b> Each pixel goes
        /// sRGB → PCS → the profile's own <c>B2A0</c> table, so the inks are the ones the profile's
        /// author chose for that colour on that press. It is <c>B2A0</c> — the perceptual table —
        /// with Adobe's black-point compensation and white kept white (no ink), as converters
        /// do by default; <b>no</b> gamut mapping of our own, and <b>no</b>
        /// rendering-intent selection. A colour outside the destination gamut lands wherever the
        /// table sends it, which is the profile's answer rather than one invented here.</para>
        ///
        /// <para><b>It falls back rather than failing.</b> A profile with no usable <c>B2A0</c> —
        /// no such tag, a malformed table, a data space that is not CMYK — converts by the
        /// formula instead and says why through <see cref="LastConversionWarning"/>. Refusing would
        /// leave the caller with an unconverted document; converting silently by formula would let
        /// them believe the profile was applied.</para>
        ///
        /// </remarks>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the converted PDF.</param>
        /// <param name="destinationIccProfile">
        /// The destination profile's bytes: an output profile with a <c>B2A0</c> table over a CMYK
        /// data space, or a device link from RGB to CMYK, whose <c>A2B0</c> converts the samples as
        /// they are (its own rendering — no intent or black-point step is added); anything else falls
        /// back to the formula and says so.
        /// </param>
        /// <param name="gcrFactor">
        /// Used only by the fallback, which is why it is still taken: a caller who supplies a
        /// profile the converter cannot use still gets their chosen Gray Component
        /// Replacement rather than a default one.
        /// </param>
        /// <returns>
        /// Number of images converted. An image that does not decode to 8-bit samples — a JPEG,
        /// JPEG 2000, JBIG2 or CCITT codestream, or a depth other than 8 — is left as it was and
        /// not counted.
        /// </returns>
        public static int ConvertRgbToCmyk(
            Stream input, Stream output, byte[] destinationIccProfile, double gcrFactor)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (destinationIccProfile == null) throw new ArgumentNullException(nameof(destinationIccProfile));

            return ConvertImages(input, output, TargetCs.Cmyk, gcrFactor, destinationIccProfile);
        }

        /// <summary>
        /// Converts the document's colour into CMYK through an output profile — content operators, images,
        /// shadings, palettes, spot colours' alternates, patterns and transparency groups alike — for a press
        /// workflow (PDF/X-1a, or DeviceRGB under a CMYK output intent). See <see cref="ColourConversionResult"/>
        /// for what is reported as left, and <see cref="ColourConverter"/> for how each part converts.
        /// </summary>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the converted PDF: one fresh revision, so a signature does not survive.</param>
        /// <param name="options">The output profile and which colour spaces convert.</param>
        /// <exception cref="ArgumentException">The profile cannot take colour into CMYK; the message says why.</exception>
        /// <exception cref="Core.PdfEncryptedDocumentException">The input is encrypted.</exception>
        public static ColourConversionResult ConvertToCmyk(Stream input, Stream output, CmykConversionOptions options)
            => ColourConverter.ToCmyk(input, output, options);

        /// <summary>
        /// Converts the document's DeviceCMYK colour into RGB — the values of an RGB output intent's profile, sRGB
        /// by default — everywhere it is set or held, as <see cref="ConvertToCmyk"/> converts into CMYK: for a
        /// PDF/A document under an RGB output intent, where DeviceCMYK is not admitted. The CMYK is read as the
        /// renderer shows it.
        /// </summary>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the converted PDF: one fresh revision, so a signature does not survive.</param>
        /// <param name="options">The RGB profile the values are meant in, and whether DeviceCMYK converts.</param>
        /// <exception cref="ArgumentException">The profile cannot take colour into RGB; the message says why.</exception>
        /// <exception cref="Core.PdfEncryptedDocumentException">The input is encrypted.</exception>
        public static ColourConversionResult ConvertToRgb(Stream input, Stream output, RgbConversionOptions options = null)
            => ColourConverter.ToRgb(input, output, options);

        // ----------------------------------------------------------------
        //  Core conversion engine
        // ----------------------------------------------------------------

        private static int ConvertImages(Stream input, Stream output, TargetCs target,
                                         double gcrFactor, byte[] destinationProfile = null)
        {
            // Reset at the start of the operation, on every path — a caller reads this after
            // the call returns, so a value left over from a previous conversion would be
            // attributed to this one.
            _lastConversionWarning = null;

            var ms  = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Fail closed, as every writer in this folder does. An encrypted document loads no
            // catalog, so the image walk found no pages and this returned 0 with the input copied
            // through — "no RGB images" about a document it never looked at. Converting would be
            // no better: the new samples would be written in clear text where the reader expects
            // ciphertext.
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Cannot convert the images of an encrypted document: the converted samples " +
                    "would be written in clear text where the reader expects per-object ciphertext, " +
                    "and this assembly cannot encrypt. Decrypt first (Tetitulz.Pdf.Encryption." +
                    "PdfDecryptor), convert, then re-encrypt — or use Tetitulz.Pdf.Encryption." +
                    "PdfEncryptedRoundTrip.Run.");

            int converted = 0;
            int nextObj   = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var objects   = new List<IndirectObject>();
            Core.Util.DistinctStrings notes = null;
            string to = target == TargetCs.Gray
                ? PdfColorSpaceNames.DeviceGray
                : PdfColorSpaceNames.DeviceCMYK;

            // One entry per image object, however many pages or forms draw it, so a shared image is
            // converted, and counted, once. The containers the
            // same walk visits are kept for the inline-image pass below, once each by object number.
            var containers = new Dictionary<int, (PdfStream Stream, int PageIndex)>();
            var index = DocumentImageIndex.Build(doc, onContainer: (pageIndex, container) =>
            {
                if (container is PdfStream s && s.ObjectNumber > 0 && !containers.ContainsKey(s.ObjectNumber))
                    containers[s.ObjectNumber] = (s, pageIndex);
            });
            foreach (var image in index.Entries)
            {
                int imgObjNum = image.Key;
                PdfStream xo = image.Value.Stream;
                string where = "/" + image.Value.Names[0];

                // The RGB samples: the stream's own when its chain decodes to raw samples, the
                // in-house decoder's when it is JPEG 2000.
                if (!TryReadRgbSamples(doc, xo, where, ref notes, out RgbSource source)) continue;
                int width = source.Width, height = source.Height;
                int numPixels = width * height;
                byte[] decoded = source.Samples;

                // §8.9.5.2: what the image shows is its samples mapped through /Decode. Read with
                // PdfColor.DecodeArray, the renderers' reader; an array that will not read leaves
                // the image unconverted, since there is no telling what it looks like. ISO 32000-2
                // §7.4.9: a JPEG 2000 image without a /ColorSpace ignores the array.
                float[] decode = null;
                if (source.DecodeArrayApplies && xo[PdfName.Decode] != null)
                {
                    decode = PdfColor.DecodeArray(doc, PdfResolve.Array(doc, xo, PdfName.Decode));
                    if (decode == null)
                    {
                        Note(ref notes,
                            $"{where}: left unconverted — its /Decode array cannot be read, so what it shows is unknown");
                        continue;
                    }
                }

                // §8.9.6.4: the colour key names RAW sample values, before /Decode — so it is
                // evaluated here, on the samples as stored, by the rule both renderers use.
                bool[] keyed = null;
                var keyArray = doc.ResolveObject(xo[PdfName.Mask]) as PdfArray;
                if (keyArray != null)
                {
                    keyed = ImageSamples.TryComputeColorKeyMask(decoded, width, height, keyArray, source.KeyDictionary, doc);
                    if (keyed == null)
                        Note(ref notes,
                            $"{where}: colour-key /Mask removed for {to} — it could not be evaluated (transparency lost)");
                }

                byte[] samples = decode == null ? decoded : ApplyDecode(decoded, numPixels, decode);

                byte[] converted_data = target == TargetCs.Gray
                    ? RgbToGray(samples, numPixels)
                    : (destinationProfile != null
                  ? RgbToCmykThroughProfile(samples, numPixels, destinationProfile, gcrFactor, ref notes)
                  : RgbToCmykWithGcr(samples, numPixels, gcrFactor));

                // One Flate encoder, Core.IO.Filters' — this file carried its own zlib framing.
                byte[] compressed = FlateEncodeFilter.Encode(converted_data);

                var newImg = CloneStream(xo);
                newImg[PdfName.ColorSpace] = target == TargetCs.Gray
                    ? PdfName.DeviceGray
                    : PdfName.DeviceCMYK;
                newImg[PdfName.Filter]  = PdfName.FlateDecode;
                newImg[PdfName.Length]  = new PdfNumber(compressed.Length);
                newImg.Remove(PdfName.DecodeParms);

                // The samples above already carry /Decode's mapping, so the identity is what
                // describes them now — and Table 89 makes a 3-component /Decode invalid on a 1- or
                // 4-component image in any case.
                newImg.Remove(PdfName.Decode);

                if (keyArray != null)
                {
                    newImg.Remove(PdfName.Mask);
                    if (keyed != null)
                    {
                        int maskObjNum = nextObj++;
                        objects.Add(new IndirectObject(maskObjNum, 0,
                            BuildStencilMask(keyed, width, height)));
                        newImg[PdfName.Mask] = new PdfReference(maskObjNum, 0);
                    }
                }

                if (source.FromJpx)
                {
                    // The converted samples are 8-bit Flate now, whatever the codestream held: the
                    // /BitsPerComponent JPXDecode ignored (Table 87) is what describes them, the
                    // geometry is the codestream's, and the opacity the codestream carried under
                    // /SMaskInData becomes the /SMask it stood for — that key means nothing to a
                    // Flate image.
                    newImg[PdfName.BitsPerComponent] = new PdfNumber(8);
                    newImg[PdfName.Width] = new PdfNumber(width);
                    newImg[PdfName.Height] = new PdfNumber(height);
                    newImg.Remove(PdfName.SMaskInData);
                    if (source.Opacity != null)
                    {
                        int softMaskObjNum = nextObj++;
                        objects.Add(new IndirectObject(softMaskObjNum, 0,
                            BuildSoftMask(source.Opacity, width, height)));
                        newImg[PdfName.SMask] = new PdfReference(softMaskObjNum, 0);
                    }
                }

                NoteMatte(doc, newImg, where, to, ref notes);
                newImg.RawData = compressed;

                // Superseded at the generation it has — §7.5.6: an updated object keeps its number
                // and generation, and every `N G R` in the file still names that generation. The
                // IndirectObject carries the identity the writer uses; a number stamped on the
                // stream itself is read by nothing.
                objects.Add(new IndirectObject(imgObjNum, xo.GenerationNumber, newImg));
                converted++;
            }

            // Inline images: in each page's content streams, against the page's
            // resources, and in each container the image walk visited, against its own. A stream is
            // superseded, re-serialised from its parsed operations, only when an image in it converted.
            var rewritten = new HashSet<int>();
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                PdfDictionary pageResources = doc.Pages[p].Resources;
                foreach (PdfStream content in PageContentStreams(doc, doc.Pages[p].Dictionary))
                {
                    if (content.ObjectNumber <= 0 || !rewritten.Add(content.ObjectNumber)) continue;
                    string where = "page " + (p + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "'s content";
                    converted += ConvertInlineImages(doc, content, pageResources, target, gcrFactor, destinationProfile,
                        where, ref notes, objects);
                }
            }
            foreach (var kvp in containers)
            {
                if (!rewritten.Add(kvp.Key)) continue;
                PdfStream container = kvp.Value.Stream;
                string where = "the content of object " + kvp.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + (kvp.Value.PageIndex >= 0 ? " on page " + (kvp.Value.PageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
                converted += ConvertInlineImages(doc, container,
                    doc.ResolveObject(container[PdfName.Resources]) as PdfDictionary, target, gcrFactor, destinationProfile,
                    where, ref notes, objects);
            }

            if (notes != null)
                _lastConversionWarning = notes.Joined("; ") + ".";

            if (objects.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return 0;
            }

            int size    = nextObj;
            var trailer = BuildTrailer(doc.Trailer, size);
            IncrementalUpdateWriter.WriteUpdate(ms, output, objects, trailer,
                doc.Parser.StartXrefOffset, size);
            return converted;
        }

        /// <summary>
        /// A page's content streams: its <c>/Contents</c> stream, or each stream of its array, as
        /// stored — never concatenated, since each is superseded under its own object number.
        /// </summary>
        internal static IEnumerable<PdfStream> PageContentStreams(PdfDocument doc, PdfDictionary page)
        {
            PdfObject contents = doc.ResolveObject(page?[PdfName.Contents]);
            if (contents is PdfStream one) yield return one;
            else if (contents is PdfArray many)
                for (int i = 0; i < many.Count; i++)
                    if (doc.ResolveObject(many[i]) is PdfStream part) yield return part;
        }

        /// <summary>
        /// Converts the DeviceRGB inline images of one content stream and, when any
        /// converted, supersedes the stream with its operations written back
        /// (<see cref="ContentOperationWriter"/>), each converted image as <c>BI</c> with the full
        /// key names, Flate-compressed samples in the target space and its <c>/L</c>. Returns the
        /// number of images converted; 0 leaves the stream untouched and nothing in <paramref name="objects"/>.
        /// </summary>
        /// <remarks>
        /// An inline image's dictionary uses abbreviations and may name a colour space from the
        /// stream's resources (§8.9.7), so it is read through the renderers' two readers —
        /// <see cref="ContentStreamInterpreter.ExpandAbbreviatedImageDict"/> and
        /// <see cref="InlineImageColorSpace.Resolve"/> — on a copy; the parsed operand is left as
        /// it was. The same rules as an image XObject decide what converts: DeviceRGB, 8 bits, a
        /// filter chain that decodes to raw samples, an area under the cap; an image mask has no
        /// colour and is left. A <c>/Decode</c> array is applied first, as for an XObject.
        /// </remarks>
        private static int ConvertInlineImages(PdfDocument doc, PdfStream content, PdfDictionary resources,
            TargetCs target, double gcrFactor, byte[] destinationProfile, string where, ref Core.Util.DistinctStrings notes,
            List<IndirectObject> objects)
        {
            byte[] data;
            try { data = content.GetDecodedData(); }
            catch (Exception ex) when (ex is PdfException || ex is InvalidDataException || ex is IOException)
            {
                Note(ref notes, $"{where}: not read for inline images — its stream could not be decoded ({ex.Message})");
                return 0;
            }
            if (data == null || data.Length == 0) return 0;
            if (System.Text.Encoding.ASCII.GetString(data).IndexOf(PdfOperators.BeginInlineImage, StringComparison.Ordinal) < 0)
                return 0;   // nothing to parse for

            List<ContentStreamOperation> ops = ContentStreamParser.Parse(data);
            int converted = 0;
            for (int i = 0; i < ops.Count; i++)
            {
                ContentStreamOperation op = ops[i];
                if (op.Operator != PdfOperators.BeginInlineImage || op.Operands.Length < 1 || op.Operands[0] is not PdfStream image)
                    continue;

                var copy = CloneStream(image);
                copy.RawData = image.RawData;
                ContentStreamInterpreter.ExpandAbbreviatedImageDict(copy);
                copy = InlineImageColorSpace.Resolve(copy, resources, doc);

                if (copy[PdfName.ImageMask] is PdfBoolean mask && mask.Value) continue;
                if (!(doc.ResolveObject(copy[PdfName.ColorSpace]) is PdfName cs) || cs.Value != PdfColorSpaceNames.DeviceRGB) continue;
                if (PdfResolve.Int(doc, copy, PdfName.BitsPerComponent, 8) != 8)
                {
                    Note(ref notes, $"{where}: an inline image left unconverted — its depth is not 8 bits");
                    continue;
                }
                if (!DecodesToRawSamples(copy, doc))
                {
                    Note(ref notes, $"{where}: an inline image left unconverted — its filter chain does not decode to raw samples");
                    continue;
                }
                int width = PdfResolve.Int(doc, copy, PdfName.Width, 0), height = PdfResolve.Int(doc, copy, PdfName.Height, 0);
                if (width <= 0 || height <= 0) continue;
                long area = (long)width * height;
                if (area > PdfLimits.MaxImagePixelArea) continue;
                byte[] decoded = copy.GetDecodedData();
                if (decoded == null || decoded.Length < area * 3) continue;
                int numPixels = (int)area;

                float[] decode = null;
                if (copy[PdfName.Decode] != null)
                {
                    decode = PdfColor.DecodeArray(doc, PdfResolve.Array(doc, copy, PdfName.Decode));
                    if (decode == null)
                    {
                        Note(ref notes, $"{where}: an inline image left unconverted — its /Decode array cannot be read, so what it shows is unknown");
                        continue;
                    }
                }
                byte[] samples = decode == null ? decoded : ApplyDecode(decoded, numPixels, decode);
                byte[] convertedData = target == TargetCs.Gray
                    ? RgbToGray(samples, numPixels)
                    : (destinationProfile != null
                        ? RgbToCmykThroughProfile(samples, numPixels, destinationProfile, gcrFactor, ref notes)
                        : RgbToCmykWithGcr(samples, numPixels, gcrFactor));
                byte[] compressed = FlateEncodeFilter.Encode(convertedData);

                var replacement = new PdfStream();
                replacement.Set(PdfName.Width, new PdfNumber(width));
                replacement.Set(PdfName.Height, new PdfNumber(height));
                replacement.Set(PdfName.BitsPerComponent, new PdfNumber(8));
                replacement.Set(PdfName.ColorSpace, target == TargetCs.Gray ? PdfName.DeviceGray : PdfName.DeviceCMYK);
                replacement.Set(PdfName.Filter, PdfName.FlateDecode);
                replacement.Set(PdfName.L, new PdfNumber(compressed.Length));
                if (copy[PdfName.Interpolate] is PdfBoolean interpolate) replacement.Set(PdfName.Interpolate, interpolate);
                replacement.RawData = compressed;
                ops[i] = new ContentStreamOperation(PdfOperators.BeginInlineImage, new PdfObject[] { replacement });
                converted++;
            }
            if (converted == 0) return 0;

            using var ms = new MemoryStream();
            var w = new PdfWriter(ms);
            foreach (ContentStreamOperation op in ops) ContentOperationWriter.Write(w, op);
            byte[] rewrittenContent = FlateEncodeFilter.Encode(ms.ToArray());

            var replacementStream = CloneStream(content);
            replacementStream[PdfName.Filter] = PdfName.FlateDecode;
            replacementStream.Remove(PdfName.DecodeParms);
            replacementStream[PdfName.Length] = new PdfNumber(rewrittenContent.Length);
            replacementStream.RawData = rewrittenContent;
            objects.Add(new IndirectObject(content.ObjectNumber, content.GenerationNumber, replacementStream));
            return converted;
        }

        /// <summary>The RGB samples an image converts from, and what describes them.</summary>
        private readonly struct RgbSource
        {
            internal RgbSource(byte[] samples, int width, int height, bool decodeArrayApplies,
                PdfDictionary keyDictionary, bool fromJpx, byte[] opacity)
            {
                Samples = samples;
                Width = width;
                Height = height;
                DecodeArrayApplies = decodeArrayApplies;
                KeyDictionary = keyDictionary;
                FromJpx = fromJpx;
                Opacity = opacity;
            }

            /// <summary>Three 8-bit samples per pixel, row-major.</summary>
            internal byte[] Samples { get; }
            internal int Width { get; }
            internal int Height { get; }

            /// <summary>Whether the dictionary's <c>/Decode</c> describes <see cref="Samples"/> (ISO 32000-2 §7.4.9 says not for a JPEG 2000 image without a <c>/ColorSpace</c>).</summary>
            internal bool DecodeArrayApplies { get; }

            /// <summary>The dictionary the colour key is evaluated against: the image's, or one saying the samples are 8 bits when the codestream's own depth was narrowed.</summary>
            internal PdfDictionary KeyDictionary { get; }

            /// <summary>True when the samples are the in-house JPEG 2000 decoder's.</summary>
            internal bool FromJpx { get; }

            /// <summary>The opacity channel a JPEG 2000 image carried under <c>/SMaskInData</c>, one byte per pixel, or null.</summary>
            internal byte[] Opacity { get; }
        }

        /// <summary>
        /// The RGB samples of an image this converter converts, or false for one it leaves alone:
        /// a colour space other than DeviceRGB, a chain that does not decode to samples, a depth
        /// other than 8, or a geometry past the sanity cap. A JPEG 2000 image is decoded by the
        /// in-house decoder and taken when what its samples hold is RGB — the dictionary's
        /// DeviceRGB, or the file's own RGB (sRGB, an sYCC already converted, the device fallback).
        /// </summary>
        private static bool TryReadRgbSamples(PdfDocument doc, PdfStream xo, string where, ref Core.Util.DistinctStrings notes,
            out RgbSource source)
        {
            source = default;

            // Resolved rather than read straight off the dictionary: PdfDictionary.GetName
            // is `v as PdfName`, so a /ColorSpace written as an indirect reference — which
            // is legal and common — read as "not DeviceRGB" and the image was skipped.
            var cs = doc.ResolveObject(xo[PdfName.ColorSpace]) as PdfName;
            bool declaredRgb = cs?.Value == PdfColorSpaceNames.DeviceRGB;

            if (EndsInJpx(xo, doc))
            {
                // A /ColorSpace other than DeviceRGB governs the codestream (§7.4.9); not this
                // converter's image. Without one, the file's own colour decides below.
                if (cs != null && !declaredRgb) return false;
                if (xo[PdfName.ColorSpace] != null && cs == null) return false;
                byte[] codestream = xo.GetDecodedData();
                if (!JpxImageDecoder.TryDecodeSamples(codestream, xo, doc, out JpxPdfImage image, out string reason))
                {
                    Note(ref notes, $"{where}: left unconverted — its JPEG 2000 codestream could not be decoded ({reason})");
                    return false;
                }
                bool rgb = image.ColourComponents == PdfColorSpaceNames.RgbComponents
                    && (image.ColourModel == JpxColourModel.Rgb
                        || (image.ColourModel == JpxColourModel.ImageDictionary && declaredRgb));
                if (!rgb) return false;
                if (image.IccProfile != null)
                    Note(ref notes, $"{where}: the JPEG 2000 file's ICC profile is not carried across; its samples are converted as RGB");
                if (image.Opacity != null && image.OpacityIsPremultiplied)
                {
                    // /SMaskInData 2: the samples are premultiplied by the opacity, and a
                    // conversion of them would convert the darkening as colour. The straight
                    // colour is what converts, through the one un-premultiply rule; the opacity
                    // then goes out as the /SMask, which is straight by definition.
                    ImageSampleLayout.UnpremultiplySamples(image.Colour, image.ColourComponents, image.Opacity);
                    Note(ref notes, $"{where}: its samples were premultiplied by the opacity channel (/SMaskInData 2); the straight colour is converted and the opacity written as an /SMask");
                }

                // The colour key names raw sample values (§8.9.6.4); the codestream's are 8 bits
                // now, whatever depth the file declared.
                var keyDict = PdfDictionary.CloneShallow(xo);
                keyDict.Set(PdfName.BitsPerComponent, new PdfNumber(8));
                source = new RgbSource(image.Colour, image.Width, image.Height, image.DecodeArrayApplies, keyDict, true, image.Opacity);
                return true;
            }

            if (!declaredRgb) return false;

            // Only convert images this converter can decode back to raw samples.
            if (!DecodesToRawSamples(xo, doc)) return false;

            // Resolved for the reason given at /ColorSpace above.
            int width  = PdfResolve.Int(doc, xo, PdfName.Width, 0);
            int height = PdfResolve.Int(doc, xo, PdfName.Height, 0);
            if (width <= 0 || height <= 0) return false;

            // The conversions take one byte per channel, and the length check after this is "at
            // least three bytes per pixel" — which a 16-bit image passes with room to spare. Its
            // high and low bytes were converted as separate channels and the clone kept
            // /BitsPerComponent 16 over one-byte samples: the image was destroyed and counted as
            // converted. Anything but 8 bits is left as it was.
            if (PdfResolve.Int(doc, xo, PdfName.BitsPerComponent, 8) != 8) return false;
            // Compute the pixel count in long to avoid int overflow on adversarial dimensions
            // (e.g. 46341x46341 wraps to a small or negative int and would pass the length check).
            long pixelCountL = (long)width * height;
            if (pixelCountL > PdfLimits.MaxImagePixelArea) return false;   // ~100 MP sanity cap

            byte[] decoded = xo.GetDecodedData();
            if (decoded == null || decoded.Length < pixelCountL * 3) return false;
            source = new RgbSource(decoded, width, height, true, xo, false, null);
            return true;
        }

        /// <summary>
        /// True when the chain's last stage is <c>JPXDecode</c> and every stage before it decodes
        /// to bytes (<see cref="RawSampleFilters"/>), so <see cref="PdfStream.GetDecodedData"/>
        /// hands back the codestream itself for the in-house decoder.
        /// </summary>
        private static bool EndsInJpx(PdfStream xo, PdfDocument doc)
        {
            var filter = doc.ResolveObject(xo[PdfName.Filter]);
            if (filter is PdfName name) return name.Value == PdfFilterNames.JpxDecode;
            if (filter is PdfArray arr && arr.Count > 0)
            {
                for (int i = 0; i < arr.Count - 1; i++)
                {
                    var stage = doc.ResolveObject(arr[i]) as PdfName;
                    if (stage == null || !RawSampleFilters.Contains(stage.Value)) return false;
                }
                return (doc.ResolveObject(arr[arr.Count - 1]) as PdfName)?.Value == PdfFilterNames.JpxDecode;
            }
            return false;
        }

        /// <summary>
        /// The colour-space family a JPEG 2000 image with no <c>/ColorSpace</c> is read in, from its
        /// JP2 header (ISO 32000-2 §7.4.9), for <see cref="GetColorSpaces(Stream)"/>; null when the
        /// codestream cannot be read.
        /// </summary>
        private static string JpxColourSpaceFamily(PdfStream xo, PdfDocument doc)
        {
            byte[] codestream;
            try { codestream = xo.GetDecodedData(); }
            catch (PdfException) { return null; }
            return codestream == null ? null : JpxImageDecoder.ColourSpaceFamilyOf(codestream);
        }

        /// <summary>
        /// Filters after which <see cref="PdfStream.GetDecodedData"/> really does hand back raw
        /// image samples. Everything absent from this set — <c>DCTDecode</c>, <c>JPXDecode</c>,
        /// <c>JBIG2Decode</c>, <c>CCITTFaxDecode</c>, <c>Crypt</c>, anything unrecognised — is a
        /// codec the pipeline deliberately passes through, so its "decoded" bytes are still the
        /// compressed codestream.
        /// </summary>
        private static readonly HashSet<string> RawSampleFilters = new HashSet<string>(StringComparer.Ordinal)
        {
            PdfName.FlateDecode.Value,    PdfName.Fl.Value,
            PdfName.LZWDecode.Value,      PdfName.LZW.Value,
            PdfName.ASCII85Decode.Value,  PdfName.A85.Value,
            PdfName.ASCIIHexDecode.Value, PdfName.AHx.Value,
            PdfName.RunLengthDecode.Value, PdfName.RL.Value,
        };

        /// <summary>
        /// True when every stage of the stream's <c>/Filter</c> chain decodes to raw samples, so
        /// the bytes this converter is about to reinterpret as RGB triples really are RGB triples.
        /// </summary>
        /// <remarks>
        /// <b>The guard this replaces read <c>/Filter</c> with
        /// <see cref="PdfDictionary.GetName"/>, which is <c>v as PdfName</c>.</b> That returns
        /// <c>null</c> for <c>/Filter [/DCTDecode]</c>, for <c>/Filter [/ASCII85Decode
        /// /DCTDecode]</c> (very common in older producers) and for an indirect <c>/Filter</c>
        /// alike — and <c>null</c> was the one value that meant "unfiltered, convert it". A JPEG
        /// therefore passed the guard, <c>GetDecodedData()</c> handed the codestream back
        /// untouched (the image codecs are passthrough by design), and <c>RgbToGray</c> read its
        /// SOI/APP0/Huffman-table bytes as RGB triples: the photo was permanently overwritten with
        /// static while <c>ConvertRgbToGrayscale</c> counted it as a success.
        /// </remarks>
        private static bool DecodesToRawSamples(PdfStream xo, PdfDocument doc)
        {
            var filter = doc.ResolveObject(xo[PdfName.Filter]);
            if (filter == null || filter is PdfNull) return true;      // unfiltered

            if (filter is PdfName name) return RawSampleFilters.Contains(name.Value);

            if (filter is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var stage = doc.ResolveObject(arr[i]) as PdfName;
                    if (stage == null || !RawSampleFilters.Contains(stage.Value)) return false;
                }
                return true;
            }

            return false;   // an unrecognised /Filter shape is not something to guess at
        }

        // ----------------------------------------------------------------
        //  Pixel converters
        // ----------------------------------------------------------------

        /// <summary>
        /// The RGB samples as the image shows them — each component mapped through its
        /// <c>/Decode</c> pair (§8.9.5.2) by <see cref="PdfColor.ApplyDecode"/> and quantised by
        /// <see cref="PdfColor.Channel(float)"/>, the rules both renderers use.
        /// </summary>
        /// <remarks>
        /// <b>Applied, not dropped.</b> Deleting <c>/Decode</c> and converting the stored samples
        /// would assume the converted samples correspond to the identity map. They do not: the image showed the <i>mapped</i> samples,
        /// so an RGB image written as stored negatives under <c>/Decode [1 0 1 0 1 0]</c> — a
        /// common scanner output — came out of a grayscale conversion as a negative. Luminance is
        /// linear, so mapping first and converting second is exactly what the image looked like.
        /// </remarks>
        private static byte[] ApplyDecode(byte[] rgb, int numPixels, float[] decode)
        {
            // One table per component: 256 evaluations instead of one per sample.
            var tables = new byte[3][];
            for (int c = 0; c < 3; c++)
            {
                tables[c] = new byte[256];
                for (int v = 0; v < 256; v++)
                    tables[c][v] = PdfColor.Channel(PdfColor.ApplyDecode(v / 255f, decode, c));
            }

            var mapped = new byte[numPixels * 3];
            for (int i = 0; i < mapped.Length; i++)
                mapped[i] = tables[i % 3][rgb[i]];
            return mapped;
        }

        private static byte[] RgbToGray(byte[] rgb, int numPixels)
        {
            byte[] gray = new byte[numPixels];
            for (int i = 0; i < numPixels; i++)
            {
                int r = rgb[i * 3];
                int g = rgb[i * 3 + 1];
                int b = rgb[i * 3 + 2];
                // ITU-R BT.601 in ×1000 fixed point (GAPS.md §2b). Kept integer, and kept
                // truncating: the float form elsewhere in the tree rounds, so switching
                // representations here would shift output by a level. See PdfColorSpaceNames.
                gray[i] = (byte)((r * PdfColorSpaceNames.LumaRedPermille
                                + g * PdfColorSpaceNames.LumaGreenPermille
                                + b * PdfColorSpaceNames.LumaBluePermille) / PdfColorSpaceNames.LumaScale);
            }
            return gray;
        }

        /// <summary>
        /// Default Gray Component Replacement factor for the two-argument
        /// <see cref="ConvertRgbToCmyk(Stream,Stream)"/>. 0 = no K channel, 1 = full GCR (matches
        /// the naive K=1-max(R,G,B) formula). 0.8 gives rich blacks while preserving some CMY for
        /// tonal smoothness.
        /// </summary>
        /// <remarks>
        /// <b>This is process-wide mutable state and it changes converted pixel values.</b> Two
        /// concurrent conversions cannot use different settings, and one thread's write takes
        /// effect mid-way through another's image loop — so a server handling two requests at once
        /// can emit a document whose images do not even agree with each other. It is kept because
        /// removing it would break callers, but the per-call
        /// <see cref="ConvertRgbToCmyk(Stream,Stream,double)"/> is the one to use: the factor
        /// travels with the call and nothing else can change it. Marked obsolete so the compiler says
        /// so at the call site.
        /// </remarks>
        [Obsolete("Process-wide: two concurrent conversions cannot use different factors. Pass the factor to ConvertRgbToCmyk(input, output, gcrFactor) instead.")]
        public static double GcrFactor
        {
            get => _gcrFactor;
            set
            {
                // Refused here, not at the conversion: a NaN set once poisoned every later
                // conversion on every thread, and the clamp passes NaN through.
                NumericArguments.RequireFinite(value, nameof(value));
                _gcrFactor = value;
            }
        }
        private static double _gcrFactor = 0.8;

        /// <summary>
        /// sRGB → DeviceCMYK through a destination profile, one pixel at a time — the perceptual
        /// <c>B2A0</c> table with black-point compensation and white kept white, through
        /// <see cref="IccOutputTransform"/>, the one route into an output profile.
        /// </summary>
        /// <remarks>
        /// <para><b>The usual defaults.</b> The perceptual table, as this
        /// conversion always read, and black-point compensation, which converters apply unless told not
        /// to: the source black (sRGB's, zero) lands on the profile's own black rather than on
        /// whatever the table answers for a black darker than the press can print.</para>
        ///
        /// <para><b>Parsed once, not per pixel.</b> The table is read on the first pixel and reused;
        /// a per-pixel parse of a 40 KB CLUT would make a photograph unconvertible in practice.</para>
        ///
        /// <para><b>The whole image falls back together.</b> If the profile cannot be used, the
        /// formula converts every pixel rather than the image ending up half managed — which would
        /// be a seam down the middle of a photograph and far worse than either answer alone.</para>
        /// </remarks>
        private static byte[] RgbToCmykThroughProfile(
            byte[] rgb, int numPixels, byte[] profile, double gcrFactor, ref Core.Util.DistinctStrings notes)
        {
            IccOutputTransform transform = IccOutputTransform.TryCreate(
                profile, IccRenderingIntent.Perceptual, blackPointCompensation: true, out string reason);
            if (transform == null || transform.Channels != 4)
            {
                Note(ref notes,
                    "the destination ICC profile was not used (" +
                    (reason ?? $"its data space has {transform.Channels} components, not 4") +
                    "); the separable GCR/UCR formula converted the image instead");
                return RgbToCmykWithGcr(rgb, numPixels, gcrFactor);
            }

            byte[] cmyk = RgbToCmyk(rgb, numPixels, transform);
            if (cmyk != null) return cmyk;

            // Established as usable above, so reaching here means the table refused one specific
            // colour. Falling the whole image back keeps it internally consistent.
            Note(ref notes,
                "the destination ICC profile's B2A table refused a colour; the separable "
                + "GCR/UCR formula converted the image instead");
            return RgbToCmykWithGcr(rgb, numPixels, gcrFactor);
        }

        /// <summary>
        /// sRGB samples through a CMYK output transform, a pixel at a time, 8 bits a channel — or null when the table
        /// refuses a colour. The one conversion an image takes, here and in the document conversion
        /// (<see cref="ColourConverter"/>).
        /// </summary>
        internal static byte[] RgbToCmyk(byte[] rgb, int numPixels, IccOutputTransform transform)
        {
            var cmyk = new byte[checked(numPixels * 4)];
            var device = new float[4];
            for (int i = 0; i < numPixels; i++)
            {
                var colour = new SKColor(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
                if (!transform.TryConvert(colour, device)) return null;
                for (int c = 0; c < 4; c++)
                    cmyk[(i * 4) + c] = PdfColor.Channel(device[c]);
            }
            return cmyk;
        }

        /// <summary>
        /// sRGB → DeviceCMYK for print: linearises, then pulls <see cref="GcrFactor"/> of
        /// <c>min(c,m,y)</c> into the K channel.
        ///
        /// <para><b>Not interchangeable with
        /// <c>Rendering.OverprintCompositor.RgbToCmykExactInverse</c></b>, which is the exact
        /// inverse of <c>PdfColor.CmykToRgb</c> so an unblended colour round-trips. Both are
        /// correct for their own job — print quality here, round-trip fidelity there — so the two
        /// are not a duplicate, though both turn RGB into CMYK. See GAPS.md §2b.</para>
        /// </summary>
        private static byte[] RgbToCmykWithGcr(byte[] rgb, int numPixels, double gcrFactor)
        {
            byte[] cmyk = new byte[numPixels * 4];
            double gcr = Math.Max(0.0, Math.Min(1.0, gcrFactor));

            for (int i = 0; i < numPixels; i++)
            {
                // Linearise sRGB so that C/M/Y are computed in linear light.
                double r = SrgbToLinear(rgb[i * 3]     / 255.0);
                double g = SrgbToLinear(rgb[i * 3 + 1] / 255.0);
                double b = SrgbToLinear(rgb[i * 3 + 2] / 255.0);

                double cL = 1.0 - r;
                double mL = 1.0 - g;
                double yL = 1.0 - b;

                // GCR: pull a portion of the min(C,M,Y) into the K channel.
                double kLinear = Math.Min(cL, Math.Min(mL, yL)) * gcr;
                cL -= kLinear; mL -= kLinear; yL -= kLinear;

                // Gamma-encode for output (printing presses use gamma-encoded values).
                double c = LinearToSrgb(cL);
                double m = LinearToSrgb(mL);
                double y = LinearToSrgb(yL);
                double k = LinearToSrgb(kLinear);

                // One quantisation rule, Rendering.PdfColor.Clamp255 — the double counterpart of
                // Channel. These four were hand-written and clamped only the UPPER end, so a
                // negative component (which GCR arithmetic can produce) wrapped to a high ink
                // value instead of to zero. Clamp255 clamps both ends.
                cmyk[i * 4]     = Rendering.PdfColor.Clamp255(c);
                cmyk[i * 4 + 1] = Rendering.PdfColor.Clamp255(m);
                cmyk[i * 4 + 2] = Rendering.PdfColor.Clamp255(y);
                cmyk[i * 4 + 3] = Rendering.PdfColor.Clamp255(k);
            }
            return cmyk;
        }

        private static double SrgbToLinear(double c) => Core.Imaging.SrgbTransfer.Decode(c);

        private static double LinearToSrgb(double c) => Core.Imaging.SrgbTransfer.Encode(c);

        // ----------------------------------------------------------------
        //  Color space inspector
        // ----------------------------------------------------------------

        /// <summary>
        /// The colour spaces the document declares and draws images in, by family.
        /// </summary>
        /// <remarks>
        /// <para><b>A family, not a resource name.</b> Reported by its <i>keys</i>, the <c>/ColorSpace</c>
        /// resource dictionary would list a page declaring <c>/CS0 [/ICCBased 7 0 R]</c> as a colour
        /// space called "CS0", in a list whose <see cref="ColorSpaceInfo.Name"/> promises
        /// "DeviceRGB", "DeviceCMYK", "ICCBased". Resources and images both go through <see cref="PdfColor.ResolveSpace"/>, the renderers' reader.</para>
        ///
        /// <para><b>Every resource dictionary the image walk visits</b> — the pages' and the forms'
        /// under them — through <see cref="DocumentImageIndex"/>, so the inspector reports the images
        /// the converter converts. It read the pages' own resources alone, while its summary said
        /// forms were included.</para>
        ///
        /// <para><see cref="ColorSpaceInfo.IsImageColorSpace"/> is true when <i>any</i> image uses
        /// the space, not whatever the first sighting said.</para>
        /// </remarks>
        private static IReadOnlyList<ColorSpaceInfo> CollectColorSpaces(PdfDocument doc)
        {
            var order = new List<string>();
            var byName = new Dictionary<string, (bool IsImage, List<int> Pages)>(StringComparer.Ordinal);

            void Add(string name, int pageNo, bool isImage)
            {
                if (!byName.TryGetValue(name, out var entry))
                {
                    entry = (false, new List<int>());
                    order.Add(name);
                }
                if (!entry.Pages.Contains(pageNo)) entry.Pages.Add(pageNo);
                byName[name] = (entry.IsImage || isImage, entry.Pages);
            }

            var images = DocumentImageIndex.Build(doc, (pageIndex, resources) =>
            {
                if (doc.ResolveObject(resources[PdfName.ColorSpace]) is PdfDictionary csDict)
                    foreach (var kvp in csDict)
                        Add(FamilyOf(doc, kvp.Value), pageIndex + 1, false);
            });

            foreach (var image in images.Entries)
            {
                // An /ImageMask declares none. A JPEG 2000 image that leaves its space to the
                // codestream is read in the space its JP2 header names (ISO 32000-2 §7.4.9).
                PdfObject csEntry = image.Value.Stream[PdfName.ColorSpace];
                string family;
                if (csEntry != null) family = FamilyOf(doc, csEntry);
                else if (EndsInJpx(image.Value.Stream, doc)) family = JpxColourSpaceFamily(image.Value.Stream, doc);
                else continue;
                if (family == null) continue;
                foreach (int pageIndex in image.Value.Pages) Add(family, pageIndex + 1, true);
            }

            var result = new List<ColorSpaceInfo>();
            foreach (string name in order)
            {
                var entry = byName[name];
                entry.Pages.Sort();
                result.Add(new ColorSpaceInfo(name, entry.IsImage, entry.Pages));
            }
            return result;
        }

        /// <summary>A colour space's family name, or "Unknown" when it declares none.</summary>
        private static string FamilyOf(PdfDocument doc, PdfObject colorSpace)
            => PdfColor.ResolveSpace(colorSpace, doc).Family ?? "Unknown";

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        // One implementation, in Core.Objects.PdfDictionary.CloneShallow, which clones a stream as
        // a stream.
        private static PdfStream CloneStream(PdfStream src) => (PdfStream)PdfDictionary.CloneShallow(src);

        /// <summary>Adds <paramref name="text"/> once, however many images give the same reason.</summary>
        private static void Note(ref Core.Util.DistinctStrings notes, string text)
        {
            notes ??= new Core.Util.DistinctStrings();
            notes.Add(text);
        }

        /// <summary>
        /// The opacity a JPEG 2000 image carried under <c>/SMaskInData</c>, written as the
        /// <c>/SMask</c> it stands for (ISO 32000-2 Table 87): an 8-bit DeviceGray image of the
        /// converted image's size, deflated.
        /// </summary>
        private static PdfStream BuildSoftMask(byte[] opacity, int width, int height)
        {
            byte[] compressed = FlateEncodeFilter.Encode(opacity);
            var mask = new PdfStream { RawData = compressed };
            mask.Set(PdfName.Type, PdfName.XObject);
            mask.Set(PdfName.Subtype, PdfName.Image);
            mask.Set(PdfName.Width, new PdfNumber(width));
            mask.Set(PdfName.Height, new PdfNumber(height));
            mask.Set(PdfName.ColorSpace, PdfName.DeviceGray);
            mask.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            mask.Set(PdfName.Filter, PdfName.FlateDecode);
            mask.Set(PdfName.Length, new PdfNumber(compressed.Length));
            return mask;
        }

        /// <summary>
        /// A colour key carried across the conversion as an explicit mask (§8.9.6.3): a one-bit
        /// image mask whose 1 bits are the pixels the key removed.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the key cannot simply stay.</b> A colour-key <c>/Mask</c> names ranges of
        /// sample values in the <i>source</i> space (§8.9.6.4) — three pairs for RGB — and Table 89
        /// makes it invalid on a one-component DeviceGray or four-component DeviceCMYK image. Dropped
        /// with "transparency lost", a logo keyed out of a white background would come out of a
        /// grayscale conversion on a white box.</para>
        ///
        /// <para><b>The explicit mask is what the key meant.</b> The key is evaluated on the stored
        /// samples by <see cref="ImageSamples.TryComputeColorKeyMask"/> — the rule both renderers use
        /// — and written as the stencil it describes: with the default <c>/Decode [0 1]</c>, "a
        /// sample value of 0 shall mark the page … and a 1 shall leave the previous contents
        /// unchanged" (§8.9.6.2), so a keyed pixel is a 1. Same width and height as the image, rows
        /// padded to a byte (§8.9.3).</para>
        /// </remarks>
        private static PdfStream BuildStencilMask(bool[] masked, int width, int height)
        {
            int rowBytes = (width + 7) / 8;
            var bits = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * rowBytes;
                for (int x = 0; x < width; x++)
                    if (masked[(y * width) + x])
                        bits[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }

            byte[] compressed = FlateEncodeFilter.Encode(bits);
            var mask = new PdfStream { RawData = compressed };
            mask.Set(PdfName.Type, PdfName.XObject);
            mask.Set(PdfName.Subtype, PdfName.Image);
            mask.Set(PdfName.Width, new PdfNumber(width));
            mask.Set(PdfName.Height, new PdfNumber(height));
            mask.Set(PdfName.ImageMask, PdfBoolean.True);
            mask.Set(PdfName.BitsPerComponent, new PdfNumber(1));
            mask.Set(PdfName.Filter, PdfName.FlateDecode);
            mask.Set(PdfName.Length, new PdfNumber(compressed.Length));
            return mask;
        }

        /// <summary>
        /// Reports a soft mask whose <c>/Matte</c> is still in the source colour space.
        /// </summary>
        /// <remarks>
        /// <b>/SMask is deliberately kept.</b> §11.6.5.3 makes a soft mask an independent DeviceGray
        /// image with its own /ColorSpace, so converting the base image cannot invalidate it —
        /// dropping it would destroy valid transparency rather than preserve it. Its optional
        /// /Matte <i>is</i> in the parent's colour space; that lives on the soft-mask object, which
        /// this conversion does not rewrite, and it is reported rather than silently left
        /// inconsistent.
        /// </remarks>
        private static void NoteMatte(PdfDocument doc, PdfStream img, string where, string to,
                                      ref Core.Util.DistinctStrings notes)
        {
            if (doc.ResolveObject(img[PdfName.SMask]) is PdfStream sm && sm[PdfName.Matte] != null)
                Note(ref notes, $"{where}: /SMask /Matte is still in the source colour space, not {to}");
        }

        // One implementation, in Core.Writing.PdfTrailerBuilder — including the /XRefStm drop,
        // which this was one of only two of seven copies to do.
        private static PdfDictionary BuildTrailer(PdfDictionary orig, int size)
            => PdfTrailerBuilder.ForUpdate(orig, size);
    }

    // ----------------------------------------------------------------
    //  Result type
    // ----------------------------------------------------------------

    /// <summary>Information about a colour space found in a PDF document.</summary>
    public sealed class ColorSpaceInfo
    {
        /// <summary>Name of the colour space (e.g. "DeviceRGB", "DeviceCMYK", "ICCBased").</summary>
        public string Name { get; }

        /// <summary>True if this colour space was found on an image XObject.</summary>
        public bool IsImageColorSpace { get; }

        /// <summary>Page numbers (1-based) on which this colour space appears.</summary>
        public IReadOnlyList<int> PageNumbers { get; }

        internal ColorSpaceInfo(string name, bool isImage, List<int> pages)
        {
            Name = name;
            IsImageColorSpace = isImage;
            PageNumbers = pages;
        }

        public override string ToString()
            => $"{Name}{(IsImageColorSpace ? " [image]" : "")} (pages: {string.Join(", ", PageNumbers)})";
    }
}
