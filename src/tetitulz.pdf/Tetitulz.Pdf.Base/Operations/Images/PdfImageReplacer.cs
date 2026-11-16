// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Encryption;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>
    /// Replaces an existing image XObject's pixels in place, via an incremental update.
    ///
    /// <para><b>How this differs from <see cref="PdfImageInserter"/>.</b> The inserter
    /// <i>adds</i> an image on top of a page, leaving the original drawn underneath. This
    /// rewrites the image object itself — same object number, new samples — so the old image
    /// is no longer reachable from the document and every place that drew it now draws the
    /// replacement.</para>
    ///
    /// <para><b>Signatures survive; certifications do not.</b> The original bytes are copied
    /// verbatim and the new revision is appended, so an existing approval signature's
    /// <c>/ByteRange</c> digest still matches and the document can be signed again afterwards.
    /// Viewers will correctly report that the document changed after that signature — the
    /// signer vouched for their revision, not this one. A <b>DocMDP certification</b> is a
    /// different matter: replacing image content is not a permitted change at any <c>/P</c>
    /// level, so this refuses unless
    /// <see cref="ImageReplacementOptions.AllowCertifiedDocument"/> is set.</para>
    ///
    /// <para><b>An incremental update cannot destroy the old pixels.</b> They remain in the
    /// earlier revision, which the first signature's digest requires, and anyone can recover
    /// them from the file — <see cref="PdfRevisions"/> does exactly that. If the point of the
    /// swap is to remove sensitive image content, this is the wrong tool: a full rewrite is
    /// needed, and that invalidates every existing signature.</para>
    ///
    /// <para><b>Geometry is unchanged.</b> An image XObject is drawn into the unit square by
    /// the CTM in the content stream, which this does not touch — so the replacement lands on
    /// exactly the same rectangle at the same size regardless of its pixel dimensions. Those
    /// dimensions set resolution, not placement; a different aspect ratio therefore stretches
    /// unless <see cref="ImageReplacementOptions.Fit"/> is <see cref="ImageFit.Contain"/>.</para>
    /// </summary>
    public static class PdfImageReplacer
    {
        /// <summary>
        /// Keys this writes itself from the replacement image. Their values in the original
        /// describe the samples being replaced, so carrying one over would describe the wrong
        /// bytes.
        /// </summary>
        private static readonly HashSet<string> WrittenKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Type", "Subtype", "Width", "Height", "BitsPerComponent", "ColorSpace",
            "Filter", "Length", "Decode", "SMask", "SMaskInData",
        };

        /// <summary>
        /// Keys carried over from the original image dictionary because they describe the
        /// image's role in the document rather than how to interpret its samples: its place in
        /// the structure tree (<c>/StructParent</c> — dropping it silently breaks tagged
        /// reading order), its optional-content membership (<c>/OC</c>), and two rendering
        /// hints (<c>/Intent</c>, <c>/Interpolate</c>).
        ///
        /// <para>Everything else present in the original is dropped and named in
        /// <see cref="ImageReplacementResult.DroppedKeys"/>. That is deliberate rather than
        /// conservative: <c>/SMask</c>, <c>/Mask</c> and <c>/Decode</c> are all defined against
        /// the sample data being thrown away, so preserving them would apply the old image's
        /// transparency or inversion to the new one's pixels. The replacement supplies its own
        /// <c>/SMask</c> and <c>/Decode</c> when its samples need them.</para>
        /// </summary>
        private static readonly HashSet<string> PreservedKeys = new HashSet<string>(StringComparer.Ordinal)
        { "StructParent", "OC", "Intent", "Interpolate" };

        /// <summary>
        /// Ratio by which the replacement's aspect ratio may differ from the original's before
        /// <see cref="ImageReplacementResult.AspectRatioChanged"/> is set. Pixel dimensions are
        /// integers, so an exact-looking crop routinely lands a fraction of a percent off.
        /// </summary>
        private const double AspectRatioTolerance = 0.01;

        // ================================================================
        //  Entry points
        // ================================================================

        /// <summary>
        /// Replaces the <paramref name="imageIndex"/>-th image on <paramref name="pageIndex"/>.
        /// The index is the one <see cref="PdfImageExporter.ExtractImage"/> and
        /// <see cref="PdfImageExporter.CountImages"/> use — all three read the same enumeration —
        /// so extract, inspect, then replace at the same index round-trips.
        ///
        /// <para>That enumeration is the page's own <c>/Resources /XObject</c> and does not
        /// descend into form XObjects. For an image drawn from inside a form, pair
        /// <see cref="FindImages(Stream)"/> with <see cref="ReplaceByObjectNumber"/>.</para>
        /// </summary>
        /// <param name="input">Source PDF. Read only; never modified.</param>
        /// <param name="output">Receives the original bytes followed by one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="imageIndex">Zero-based index into the page's image XObjects.</param>
        /// <param name="image">The replacement pixels.</param>
        /// <param name="options">Policy settings; may be null.</param>
        public static ImageReplacementResult Replace(
            Stream input, Stream output,
            int pageIndex, int imageIndex,
            ReplacementImage image,
            ImageReplacementOptions options = null)
        {
            if (imageIndex < 0) throw new ArgumentOutOfRangeException(nameof(imageIndex));
            return ReplaceCore(input, output, image, options,
                doc => SelectByIndex(doc, pageIndex, imageIndex));
        }

        /// <summary>
        /// Replaces the image reached by <paramref name="resourceName"/> in the page's
        /// <c>/Resources /XObject</c> dictionary (the name used by the <c>Do</c> operator, e.g.
        /// <c>Im0</c>).
        ///
        /// <para>Prefer this over <see cref="Replace"/> when the caller already knows the name:
        /// a resource dictionary has no defined entry order, so an index is only meaningful
        /// relative to a specific enumeration of a specific document, whereas the name is what
        /// the content stream itself refers to.</para>
        /// </summary>
        public static ImageReplacementResult ReplaceByName(
            Stream input, Stream output,
            int pageIndex, string resourceName,
            ReplacementImage image,
            ImageReplacementOptions options = null)
        {
            if (resourceName == null) throw new ArgumentNullException(nameof(resourceName));
            return ReplaceCore(input, output, image, options,
                doc => SelectByName(doc, pageIndex, resourceName));
        }

        /// <summary>
        /// Replaces the image XObject with the given object number, wherever it is referenced.
        ///
        /// <para>This is the selector to pair with <see cref="FindImages(Stream)"/>: it is not
        /// scoped to a page, so it reaches an image drawn from inside a form XObject, one shared
        /// by many pages, and one no page reaches at all. An object number is stable within a
        /// document in a way a resource index is not.</para>
        /// </summary>
        public static ImageReplacementResult ReplaceByObjectNumber(
            Stream input, Stream output,
            int objectNumber,
            ReplacementImage image,
            ImageReplacementOptions options = null)
        {
            if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
            return ReplaceCore(input, output, image, options,
                doc => SelectByObjectNumber(doc, objectNumber));
        }

        /// <summary>
        /// Every image XObject the document reaches, one entry per object, listing the pages and
        /// resource names that reach it. Unlike the page-level enumeration behind
        /// <see cref="Replace"/>, this <b>descends into form XObjects</b> — cycle-guarded and
        /// bounded by <see cref="PdfLimits.MaxFormXObjectDepth"/> — so it finds images a
        /// page-level scan cannot see. Feed <see cref="DocumentImageInfo.ObjectNumber"/> to
        /// <see cref="ReplaceByObjectNumber"/> to change one everywhere at once.
        /// </summary>
        public static IReadOnlyList<DocumentImageInfo> FindImages(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the images", "FindImages(Stream, string)");
            return BuildIndex(doc).ToInfoList(doc);
        }

        /// <summary>
        /// <see cref="FindImages(Stream)"/> for an <b>encrypted</b> document, decrypting with
        /// <paramref name="password"/>.
        /// </summary>
        /// <remarks>
        /// Enumerating is a read, so it needs nothing from the Encryption assembly beyond the
        /// registered decryptor. <b>Replacing</b> an image in an encrypted document still routes
        /// through <c>Tetitulz.Pdf.Encryption.PdfEncryptedImages</c>, and must: it appends a
        /// revision encrypted with the document's own key, which is the whole reason that path
        /// preserves a signature where the decrypt/re-encrypt round trip cannot.
        /// </remarks>
        public static IReadOnlyList<DocumentImageInfo> FindImages(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var decrypted = PdfDecryptedInput.OpenDecrypted(input, password);
            using var doc = PdfDocument.Open(decrypted);
            return BuildIndex(doc).ToInfoList(doc);
        }

        // ================================================================
        //  Core
        // ================================================================

        private static ImageReplacementResult ReplaceCore(
            Stream input, Stream output,
            ReplacementImage image,
            ImageReplacementOptions options,
            Func<PdfDocument, (string Name, PdfStream Stream)> select)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (image == null) throw new ArgumentNullException(nameof(image));
            options = options ?? new ImageReplacementOptions();

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // An encrypted document's streams are ciphertext under a per-object key. This writes
            // the replacement in clear text, which would decode to garbage. Fail closed rather
            // than emit a file that opens and shows a broken image.
            if (doc.IsEncrypted)
                throw new PdfException(
                    "Cannot replace an image in an encrypted document here: the replacement stream " +
                    "would be written in clear text, and this assembly cannot decrypt. Use " +
                    "Tetitulz.Pdf.Encryption.PdfEncryptedImages.ReplaceByObjectNumber, which reads " +
                    "the image from a decrypted view and appends the revision encrypted with the " +
                    "document's own key.");

            ImageReplacementPlan plan = PlanCore(doc, image, options, select, firstFreeObjectNumber: 0);

            ms.Position = 0;
            IncrementalUpdateWriter.WriteUpdate(
                ms, output, new List<IndirectObject>(plan.Objects), plan.Trailer,
                doc.Parser.StartXrefOffset, plan.NextObjectNumber);

            return plan.Result;
        }

        /// <summary>
        /// Works out which objects a replacement would write, without writing anything — the step
        /// <c>Tetitulz.Pdf.Encryption.PdfEncryptedImages</c> needs, because an encrypted document
        /// must be <i>read</i> from a decrypted copy while the revision is appended to the
        /// original, still-encrypted bytes. See <see cref="Forms.PdfFormFiller.Plan"/> for the same split.
        /// </summary>
        /// <param name="document">Document to read the image from.</param>
        /// <param name="imageObjectNumber">Object number of the image XObject to supersede.</param>
        /// <param name="image">The replacement pixels.</param>
        /// <param name="options">Policy settings; may be null.</param>
        /// <param name="firstFreeObjectNumber">
        /// Lowest object number the plan may allocate; 0 means "one past what
        /// <paramref name="document"/> declares". Pass the original's ceiling when appending to a
        /// different file — a decrypted copy drops object and cross-reference streams, so its
        /// highest number can be lower.
        /// </param>
        /// <remarks>
        /// The certified-document refusal reads only whether a certification exists and at what
        /// <c>/P</c>, which the object graph answers on its own — so this asks
        /// <see cref="DocMdpAnalyzer.TryReadCertification"/> rather than running the full analysis,
        /// whose modification verdict would be discarded, and which for a decrypted copy would compare
        /// the original's <c>/ByteRange</c> against another file's bytes.
        /// </remarks>
        public static ImageReplacementPlan Plan(
            PdfDocument document,
            int imageObjectNumber,
            ReplacementImage image,
            ImageReplacementOptions options = null,
            int firstFreeObjectNumber = 0)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (image == null) throw new ArgumentNullException(nameof(image));

            return PlanCore(document, image, options,
                doc => SelectByObjectNumber(doc, imageObjectNumber), firstFreeObjectNumber);
        }

        /// <summary>
        /// <see cref="Plan(PdfDocument, int, ReplacementImage, ImageReplacementOptions, int)"/> with a
        /// length the plan no longer reads.
        /// </summary>
        /// <param name="document">Document to read the image from.</param>
        /// <param name="documentLength">Ignored. The plan reads only whether the document is certified, which needs no length.</param>
        /// <param name="imageObjectNumber">Object number of the image XObject to supersede.</param>
        /// <param name="image">The replacement pixels.</param>
        /// <param name="options">Policy settings; may be null.</param>
        /// <param name="firstFreeObjectNumber">As on the overload without a length.</param>
        [Obsolete("The plan reads only whether the document is certified, which needs no length; " +
                  "use the overload without documentLength.")]
        public static ImageReplacementPlan Plan(
            PdfDocument document,
            long documentLength,
            int imageObjectNumber,
            ReplacementImage image,
            ImageReplacementOptions options = null,
            int firstFreeObjectNumber = 0)
            => Plan(document, imageObjectNumber, image, options, firstFreeObjectNumber);

        private static ImageReplacementPlan PlanCore(
            PdfDocument doc,
            ReplacementImage image,
            ImageReplacementOptions options,
            Func<PdfDocument, (string Name, PdfStream Stream)> select,
            int firstFreeObjectNumber)
        {
            options = options ?? new ImageReplacementOptions();

            if (DocMdpAnalyzer.TryReadCertification(doc, out int permission, out string permissionDescription)
                && !options.AllowCertifiedDocument)
                throw new PdfException(
                    $"Document carries a DocMDP certification signature (P={permission}: " +
                    $"{permissionDescription}). Replacing image content is not a permitted " +
                    $"change at any /P level and will invalidate the certification. Set " +
                    $"{nameof(ImageReplacementOptions)}.{nameof(ImageReplacementOptions.AllowCertifiedDocument)} " +
                    $"to proceed anyway.");

            var (name, original) = select(doc);

            int objNum = original.ObjectNumber;
            if (objNum <= 0)
                throw new PdfException(
                    $"Image '{name}' is not an indirect object, so it cannot be superseded by an " +
                    "incremental update.");

            // Resolved: the fit modes size the replacement to these, and read raw an indirect
            // /Width or /Height made the original look 0x0.
            int originalWidth  = Rendering.PdfResolve.Int(doc, original, PdfName.Width, 0);
            int originalHeight = Rendering.PdfResolve.Int(doc, original, PdfName.Height, 0);

            image = ApplyFit(image, originalWidth, originalHeight, options.Fit, out bool letterboxed);

            int nextObjNum = Math.Max(
                Math.Max(doc.CrossReferenceTable.GetMaxObjectNumber(), objNum) + 1,
                firstFreeObjectNumber);
            var objects = new List<IndirectObject>();

            // A soft mask is a second image XObject. It is genuinely new, so it takes a fresh
            // object number — only the image being replaced keeps its identity.
            int softMaskObjNum = 0;
            if (image.SoftMask != null)
            {
                softMaskObjNum = nextObjNum++;
                var maskStream = BuildSoftMaskStream(image.SoftMask);
                maskStream.ObjectNumber = softMaskObjNum;
                maskStream.GenerationNumber = 0;
                objects.Add(new IndirectObject(softMaskObjNum, 0, maskStream));
            }

            var replacement = BuildReplacementStream(original, image, softMaskObjNum, out var dropped);
            replacement.ObjectNumber = objNum;
            replacement.GenerationNumber = original.GenerationNumber;
            objects.Add(new IndirectObject(objNum, original.GenerationNumber, replacement));

            var pages = BuildIndex(doc).PagesReferencing(objNum);

            int newSize = nextObjNum;
            var trailer = PdfPageContentAppender.BuildTrailer(doc.Trailer, newSize);

            var result = BuildResult(name, objNum, softMaskObjNum, originalWidth, originalHeight,
                image, dropped, pages, letterboxed, options.Fit);

            return new ImageReplacementPlan(objects, result, trailer, newSize);
        }

        // ---------------- selection ----------------

        private static (string, PdfStream) SelectByIndex(PdfDocument doc, int pageIndex, int imageIndex)
        {
            var images = PdfImageExporter.CollectPageImages(doc, pageIndex);
            if (imageIndex >= images.Count)
                throw new ArgumentOutOfRangeException(nameof(imageIndex),
                    $"Page {pageIndex} has {images.Count} images; requested index {imageIndex}.");
            return images[imageIndex];
        }

        private static (string, PdfStream) SelectByName(PdfDocument doc, int pageIndex, string resourceName)
        {
            var images = PdfImageExporter.CollectPageImages(doc, pageIndex);
            foreach (var candidate in images)
                if (string.Equals(candidate.ResourceName, resourceName, StringComparison.Ordinal))
                    return candidate;

            string names = images.Count == 0
                ? "none"
                : string.Join(", ", images.ConvertAll(c => "/" + c.ResourceName));
            throw new ArgumentException(
                $"Page {pageIndex} has no image XObject named '/{resourceName}'. Present: {names}.",
                nameof(resourceName));
        }

        private static (string, PdfStream) SelectByObjectNumber(PdfDocument doc, int objectNumber)
        {
            var index = BuildIndex(doc);
            var reached = index.Find(objectNumber);
            if (reached != null)
                return (index.PrimaryName(objectNumber), reached);

            // Not reached from any page — still replaceable if the object exists and is an image.
            // By number: an existing object is not addressed by a reference built at generation 0.
            if (doc.GetObject(objectNumber) is PdfStream direct &&
                PdfResolve.Name(doc, direct, PdfName.Subtype) == PdfName.Image)
                return (objectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), direct);

            throw new ArgumentOutOfRangeException(nameof(objectNumber),
                $"Object {objectNumber} is not an image XObject in this document.");
        }

        // ---------------- object construction ----------------

        /// <summary>
        /// Builds the superseding image XObject: the replacement's own sample description, plus
        /// the <see cref="PreservedKeys"/> carried over from <paramref name="original"/>.
        /// Every other key the original carried is reported through <paramref name="dropped"/>.
        /// </summary>
        private static PdfStream BuildReplacementStream(
            PdfStream original, ReplacementImage image, int softMaskObjNum, out List<string> dropped)
        {
            var replacement = new PdfStream();
            replacement.Set(PdfName.Type, PdfName.XObject);
            replacement.Set(PdfName.Subtype, PdfName.Image);
            replacement.Set(PdfName.Width, new PdfNumber(image.Width));
            replacement.Set(PdfName.Height, new PdfNumber(image.Height));
            // A JPEG 2000 replacement may leave both to the file and ask for its opacity channel
            // (ISO 32000-2 s7.4.9); every other source states both.
            if (image.BitsPerComponent > 0)
                replacement.Set(PdfName.BitsPerComponent, new PdfNumber(image.BitsPerComponent));
            if (image.ColorSpace != null)
                replacement.Set(PdfName.ColorSpace, image.ColorSpace);
            if (image.Filter != null)
                replacement.Set(PdfName.Filter, image.Filter);
            if (image.SMaskInData > 0)
                replacement.Set(PdfName.SMaskInData, new PdfNumber(image.SMaskInData));
            if (image.Decode != null)
                replacement.Set(PdfName.Decode, image.Decode);
            if (softMaskObjNum > 0)
                replacement.Set(PdfName.SMask, new PdfReference(softMaskObjNum, 0));
            replacement.RawData = image.Data;
            replacement.Set(PdfName.Length, new PdfNumber(image.Data.Length));

            dropped = new List<string>();
            foreach (var kvp in original)
            {
                string key = kvp.Key.Value;
                if (WrittenKeys.Contains(key)) continue;
                if (PreservedKeys.Contains(key)) { replacement.Set(kvp.Key, kvp.Value); continue; }
                dropped.Add(key);
            }
            // /SMask and /Decode are in WrittenKeys so they can never be inherited, but the caller
            // still needs to know when one was discarded rather than superseded.
            if (softMaskObjNum == 0 && original[PdfName.SMask] != null) dropped.Add("SMask");
            if (image.Decode == null && original[PdfName.Decode] != null) dropped.Add("Decode");
            dropped.Sort(StringComparer.Ordinal);
            return replacement;
        }

        /// <summary>
        /// A soft mask is an 8-bit DeviceGray image XObject whose samples are the alpha channel
        /// (PDF 32000-1 §11.6.5.2). It carries no <c>/SMask</c> of its own, and its dimensions
        /// need not match the image it masks — readers scale it.
        ///
        /// <para><c>/Matte</c> is written only when the caller supplied one
        /// (<see cref="ReplacementImage.WithSoftMask(byte[], int, int, float[])"/>). It is not
        /// defaultable: its presence is what tells a reader the base samples are pre-blended, so
        /// writing it for straight samples would make every edge pixel wrong.</para>
        /// </summary>
        private static PdfStream BuildSoftMaskStream(SoftMaskSamples mask)
        {
            byte[] encoded = FlateEncodeFilter.Encode(mask.Data);
            var s = new PdfStream();
            s.Set(PdfName.Type, PdfName.XObject);
            s.Set(PdfName.Subtype, PdfName.Image);
            s.Set(PdfName.Width, new PdfNumber(mask.Width));
            s.Set(PdfName.Height, new PdfNumber(mask.Height));
            s.Set(PdfName.ColorSpace, PdfName.DeviceGray);
            s.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            s.Set(PdfName.Filter, PdfName.FlateDecode);
            if (mask.Matte != null)
            {
                var matte = new PdfArray();
                foreach (float component in mask.Matte) matte.Add(new PdfNumber(component));
                s.Set(PdfName.Matte, matte);
            }
            s.RawData = encoded;
            s.Set(PdfName.Length, new PdfNumber(encoded.Length));
            return s;
        }

        // ---------------- fit ----------------

        /// <summary>
        /// Letterboxes the replacement to the original's aspect ratio when
        /// <see cref="ImageFit.Contain"/> is requested, by padding it with fully transparent
        /// pixels rather than by touching the content stream's CTM. The drawn rectangle is
        /// therefore unchanged — the visible image simply occupies the largest centred sub-rect
        /// of it that keeps its own proportions.
        ///
        /// <para>The padded result is raw RGB plus a soft mask, so an input JPEG loses its DCT
        /// compression here. That is the price of not moving geometry the caller did not ask to
        /// move: the alternative is rewriting the page's CTM.</para>
        ///
        /// <para><see cref="ImageFit.Cover"/> is the same operation with the opposite sign: it
        /// <b>crops</b> the centred sub-rectangle that matches the original's ratio, so the frame is
        /// filled edge to edge and the overflow is discarded. It never needs a soft mask, since no
        /// transparent pixel is introduced — but it does <b>destroy</b> the cropped-off pixels,
        /// which is why it is opt-in rather than the default.</para>
        /// </summary>
        private static ReplacementImage ApplyFit(
            ReplacementImage image, int originalWidth, int originalHeight, ImageFit fit,
            out bool letterboxed)
        {
            letterboxed = false;
            if (fit != ImageFit.Contain && fit != ImageFit.Cover) return image;
            if (originalWidth <= 0 || originalHeight <= 0) return image;

            double target = (double)originalWidth / originalHeight;
            double actual = (double)image.Width / image.Height;
            if (Math.Abs(actual - target) <= AspectRatioTolerance * target) return image;

            // Refuse rather than silently drop the matte. Fitting rebuilds the image from straight
            // RGBA and re-emits a plain /SMask, so a pre-blended image would keep its blended
            // samples and lose the /Matte that says so — the fringe defect the entry exists to
            // prevent, arrived at by a different road.
            if (image.SoftMask?.Matte != null)
                throw new PdfException(
                    $"{nameof(ImageFit)}.{fit} rebuilds the image, which cannot carry a /Matte " +
                    "through: the samples would stay pre-blended while the /Matte entry saying so " +
                    $"was dropped. Use {nameof(ImageFit)}.{nameof(ImageFit.Stretch)}, or supply a " +
                    "replacement that already matches the original's aspect ratio.");

            var raster = RasterSamples.From(image);
            if (raster == null)
                throw new PdfException(
                    $"{nameof(ImageFit)}.{fit} has to read the replacement's " +
                    "pixels, and this image could not be decoded (bytes no decoder reads, for example). " +
                    $"Use {nameof(ImageFit)}.{nameof(ImageFit.Stretch)}, or " +
                    "supply raw samples.");

            if (fit == ImageFit.Cover)
            {
                // Crop the largest centred rect of the target ratio. Too WIDE means the height is
                // the binding axis and width is trimmed; too TALL is the mirror. The result is
                // never larger than the source on either axis, so no ceiling check is needed —
                // the padding path needs one precisely because it can grow.
                int cropWidth, cropHeight;
                if (actual > target)
                {
                    cropHeight = raster.Height;
                    cropWidth  = SaturatingCast.ToInt(Math.Round(raster.Height * target));
                }
                else
                {
                    cropWidth  = raster.Width;
                    cropHeight = SaturatingCast.ToInt(Math.Round(raster.Width / target));
                }

                // Rounding can land one pixel over; clamp, and keep at least one pixel so a
                // degenerate ratio cannot produce a zero-dimension image.
                cropWidth  = Math.Max(1, Math.Min(cropWidth,  raster.Width));
                cropHeight = Math.Max(1, Math.Min(cropHeight, raster.Height));

                letterboxed = true;
                return raster.CropTo(cropWidth, cropHeight).ToReplacementImage();
            }

            int canvasWidth, canvasHeight;
            if (actual > target)
            {
                canvasWidth  = raster.Width;
                canvasHeight = SaturatingCast.ToInt(Math.Round(raster.Width / target));
            }
            else
            {
                canvasHeight = raster.Height;
                canvasWidth  = SaturatingCast.ToInt(Math.Round(raster.Height * target));
            }
            if (canvasWidth  < raster.Width)  canvasWidth  = raster.Width;
            if (canvasHeight < raster.Height) canvasHeight = raster.Height;
            if ((long)canvasWidth * canvasHeight > PdfLimits.MaxImagePixelArea)
                throw new PdfException(
                    $"Letterboxing to {canvasWidth}x{canvasHeight} exceeds the " +
                    $"{PdfLimits.MaxImagePixelArea}-pixel limit.");

            letterboxed = true;
            return raster.PadTo(canvasWidth, canvasHeight).ToReplacementImage();
        }

        // ---------------- document image index ----------------

        // The walk is DocumentImageIndex's, shared with PdfColorSpaceConverter.
        private static DocumentImageIndex BuildIndex(PdfDocument doc) => DocumentImageIndex.Build(doc);

        // ---------------- result ----------------

        private static ImageReplacementResult BuildResult(
            string name, int objNum, int softMaskObjNum, int originalWidth, int originalHeight,
            ReplacementImage image, List<string> dropped, IReadOnlyList<int> pages, bool resized,
            ImageFit fit)
        {
            // "Letterboxed" and "cropped" are opposite operations and a caller may need to know
            // which happened — Contain keeps every pixel and adds transparency, Cover fills the
            // frame and destroys what overflowed. One flag for both would hide the data loss.
            bool letterboxed = resized && fit == ImageFit.Contain;
            bool cropped     = resized && fit == ImageFit.Cover;

            // AspectRatioChanged is documented as "Always false when Letterboxed is true — that is
            // what letterboxing fixes", and the Warnings list below is an else-if chain that omits
            // the stretch note whenever a fit ran. Recomputing the ratio from the FITTED dimensions
            // contradicted both: the padded canvas is integer-rounded, so a small original misses
            // the 1% tolerance and the result claimed to be letterboxed AND stretched at once.
            // Original 7x3 (2.3333) with a 10x10 replacement and ImageFit.Contain pads to 23x10
            // (2.30) — |2.30 - 2.3333| = 0.0333 against a tolerance of 0.0233. A caller following
            // the documented contract then emits a warning the result's own Warnings list does not.
            // A fit ran precisely to make the ratio match, so it is the fit that decides.
            bool aspectChanged =
                !letterboxed && !cropped &&
                originalWidth > 0 && originalHeight > 0 &&
                Math.Abs((double)image.Width / image.Height - (double)originalWidth / originalHeight)
                    > AspectRatioTolerance * ((double)originalWidth / originalHeight);

            var warnings = new List<string>();
            if (letterboxed)
                warnings.Add(
                    $"Letterboxed to {image.Width}x{image.Height} to match the original's " +
                    $"{originalWidth}x{originalHeight} aspect ratio; the padding is transparent and " +
                    "the replacement was re-encoded as raw RGB, losing any original compression.");
            else if (cropped)
                warnings.Add(
                    $"Cropped to {image.Width}x{image.Height} to match the original's " +
                    $"{originalWidth}x{originalHeight} aspect ratio; the pixels outside that " +
                    "centred rectangle are NOT in the output, and the replacement was re-encoded " +
                    "as raw RGB, losing any original compression.");
            else if (aspectChanged)
                warnings.Add(
                    $"Aspect ratio changed ({originalWidth}x{originalHeight} → " +
                    $"{image.Width}x{image.Height}). The drawing rectangle is fixed by the " +
                    "content stream's CTM, which is unchanged, so the replacement will be " +
                    $"stretched. Set {nameof(ImageReplacementOptions)}.{nameof(ImageReplacementOptions.Fit)} " +
                    $"to {nameof(ImageFit)}.{nameof(ImageFit.Contain)} to letterbox it instead.");
            if (pages.Count > 1)
                warnings.Add(
                    $"Image object {objNum} is referenced by {pages.Count} pages; all of them " +
                    "now show the replacement.");
            if (dropped.Count > 0)
                warnings.Add(
                    "Dropped keys from the original image dictionary (they described the replaced " +
                    $"samples): /{string.Join(", /", dropped)}.");

            return new ImageReplacementResult(
                name, objNum, softMaskObjNum, originalWidth, originalHeight,
                image.Width, image.Height, aspectChanged, letterboxed, cropped, pages, dropped,
                warnings);
        }
    }

    // ====================================================================

    /// <summary>How a replacement whose aspect ratio differs from the original's is handled.</summary>
    public enum ImageFit
    {
        /// <summary>
        /// Leave the content stream alone and let the reader stretch the replacement into the
        /// rectangle the original occupied. The default, and the only mode that preserves an
        /// input JPEG's compression byte-for-byte.
        /// </summary>
        Stretch = 0,

        /// <summary>
        /// Pad the replacement with fully transparent pixels until its aspect ratio matches the
        /// original's, so it draws undistorted and centred inside the same rectangle. The padded
        /// image is written as raw RGB plus a soft mask, so an input JPEG is re-encoded.
        /// </summary>
        Contain = 1,

        /// <summary>
        /// Crop the replacement to the original's aspect ratio, keeping the centred sub-rectangle,
        /// so it draws undistorted and fills the frame edge to edge. The mirror of
        /// <see cref="Contain"/>: that one adds transparent pixels and keeps everything, this one
        /// keeps the frame full and <b>discards</b> what falls outside it.
        ///
        /// <para>Like <see cref="Contain"/> it re-encodes, because it has to read the pixels —
        /// but unlike it, no soft mask is written, since cropping introduces no transparency.
        /// The cropped-off pixels are gone from the output; that data loss is why this is opt-in.
        /// </para>
        /// </summary>
        Cover = 2,
    }

    /// <summary>Policy settings for <see cref="PdfImageReplacer"/>.</summary>
    public sealed class ImageReplacementOptions
    {
        /// <summary>
        /// Proceed even when the document carries a DocMDP certification signature. Defaults to
        /// <c>false</c>, which refuses: image content is not a permitted change at <c>/P</c> 1, 2
        /// or 3, so the output would open with the certification shown as invalid rather than as
        /// modified. Set this only when that is the intended outcome.
        /// </summary>
        public bool AllowCertifiedDocument { get; set; }

        /// <summary>
        /// What to do when the replacement's aspect ratio differs from the original's. Defaults
        /// to <see cref="ImageFit.Stretch"/> — the historical behaviour, and the only mode that
        /// leaves the bytes you supplied untouched.
        /// </summary>
        public ImageFit Fit { get; set; } = ImageFit.Stretch;
    }

    /// <summary>
    /// One image XObject reached by <see cref="PdfImageReplacer.FindImages(Stream)"/>, with every
    /// page and resource name that reaches it.
    /// </summary>
    public sealed class DocumentImageInfo
    {
        internal DocumentImageInfo(int objectNumber, int width, int height,
            IReadOnlyList<int> pages, IReadOnlyList<string> resourceNames, bool onlyInsideFormXObject)
        {
            ObjectNumber = objectNumber;
            Width = width;
            Height = height;
            Pages = pages;
            ResourceNames = resourceNames;
            OnlyInsideFormXObject = onlyInsideFormXObject;
        }

        /// <summary>Pass this to <see cref="PdfImageReplacer.ReplaceByObjectNumber"/>.</summary>
        public int ObjectNumber { get; }

        /// <summary>Declared pixel width, or 0 when the dictionary has none.</summary>
        public int Width { get; }

        /// <summary>Declared pixel height, or 0 when the dictionary has none.</summary>
        public int Height { get; }

        /// <summary>Zero-based indices of the pages that reach this image, ascending.</summary>
        public IReadOnlyList<int> Pages { get; }

        /// <summary>Distinct resource names this image is reached by, sorted.</summary>
        public IReadOnlyList<string> ResourceNames { get; }

        /// <summary>
        /// <c>true</c> when every path to this image runs through a form XObject, so the
        /// page-level index used by <see cref="PdfImageReplacer.Replace"/> and
        /// <see cref="PdfImageExporter.ExtractImage"/> cannot see it.
        /// </summary>
        public bool OnlyInsideFormXObject { get; }
    }

    /// <summary>
    /// The objects a replacement would write, computed without touching an output stream — what
    /// <see cref="PdfImageReplacer.Plan(PdfDocument, int, ReplacementImage, ImageReplacementOptions, int)"/>
    /// returns, so an encrypted document can be read from a
    /// decrypted copy while its revision is appended to the original bytes.
    /// </summary>
    public sealed class ImageReplacementPlan
    {
        internal ImageReplacementPlan(
            IReadOnlyList<IndirectObject> objects, ImageReplacementResult result,
            PdfDictionary trailer, int nextObjectNumber)
        {
            Objects = objects;
            Result = result;
            Trailer = trailer;
            NextObjectNumber = nextObjectNumber;
        }

        /// <summary>The replacement image, and its soft mask when it has one.</summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>What the replacement changed, dropped and re-fitted.</summary>
        public ImageReplacementResult Result { get; }

        /// <summary>Trailer for the appended revision.</summary>
        public PdfDictionary Trailer { get; }

        /// <summary>One past the highest object number allocated — the revision's minimum /Size.</summary>
        public int NextObjectNumber { get; }
    }

    /// <summary>
    /// What <see cref="PdfImageReplacer"/> actually did — the output, not the plan. A returned
    /// result means the appended revision reached the caller's stream; the writer throws
    /// otherwise.
    /// </summary>
    public sealed class ImageReplacementResult
    {
        internal ImageReplacementResult(
            string resourceName, int objectNumber, int softMaskObjectNumber,
            int originalWidth, int originalHeight,
            int newWidth, int newHeight,
            bool aspectRatioChanged, bool letterboxed, bool cropped,
            IReadOnlyList<int> pages,
            IReadOnlyList<string> droppedKeys, IReadOnlyList<string> warnings)
        {
            ResourceName = resourceName;
            ObjectNumber = objectNumber;
            SoftMaskObjectNumber = softMaskObjectNumber;
            OriginalWidth = originalWidth;
            OriginalHeight = originalHeight;
            NewWidth = newWidth;
            NewHeight = newHeight;
            AspectRatioChanged = aspectRatioChanged;
            Letterboxed = letterboxed;
            Cropped = cropped;
            Pages = pages;
            DroppedKeys = droppedKeys;
            Warnings = warnings;
        }

        /// <summary>Resource name the page's /XObject dictionary uses for the replaced image.</summary>
        public string ResourceName { get; }

        /// <summary>Object number that was superseded — unchanged by the replacement.</summary>
        public int ObjectNumber { get; }

        /// <summary>
        /// Object number of the soft mask written alongside the replacement, or 0 when it is
        /// opaque. Unlike <see cref="ObjectNumber"/> this is always a newly allocated object.
        /// </summary>
        public int SoftMaskObjectNumber { get; }

        /// <summary>The replaced image's pixel width, or 0 if its dictionary declared none.</summary>
        public int OriginalWidth { get; }

        /// <summary>The replaced image's pixel height, or 0 if its dictionary declared none.</summary>
        public int OriginalHeight { get; }

        /// <summary>The written replacement's pixel width — after letterboxing, if any.</summary>
        public int NewWidth { get; }

        /// <summary>The written replacement's pixel height — after letterboxing, if any.</summary>
        public int NewHeight { get; }

        /// <summary>
        /// Whether the written image's aspect ratio differs from the original's, meaning it will
        /// be stretched into the rectangle the content stream already draws. Always <c>false</c>
        /// when <see cref="Letterboxed"/> is <c>true</c> — that is what letterboxing fixes.
        /// </summary>
        public bool AspectRatioChanged { get; }

        /// <summary>
        /// Whether <see cref="ImageFit.Contain"/> padded the replacement to match the original's
        /// aspect ratio. Implies it was re-encoded as raw RGB plus a soft mask.
        /// </summary>
        public bool Letterboxed { get; }

        /// <summary>
        /// Whether <see cref="ImageFit.Cover"/> cropped the replacement to match the original's
        /// aspect ratio. Implies it was re-encoded as raw RGB — and, unlike
        /// <see cref="Letterboxed"/>, that the pixels outside the centred rectangle are <b>not in
        /// the output</b>. The two are mutually exclusive.
        /// </summary>
        public bool Cropped { get; }

        /// <summary>Zero-based indices of every page that reaches the replaced object, ascending.</summary>
        public IReadOnlyList<int> Pages { get; }

        /// <summary>How many pages reference the replaced object; more than one means a shared image.</summary>
        public int PagesSharingImage => Pages.Count;

        /// <summary>
        /// Keys present on the original image dictionary that were not carried over, sorted.
        /// Typically <c>/Mask</c>, <c>/Decode</c> or <c>/DecodeParms</c> — all defined against the
        /// samples that were replaced. A <c>/SMask</c> or <c>/Decode</c> the replacement supplies
        /// itself is <b>not</b> listed; it was superseded, not dropped.
        /// </summary>
        public IReadOnlyList<string> DroppedKeys { get; }

        /// <summary>Human-readable notes about what the replacement changed beyond the pixels.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>8-bit grayscale alpha samples destined for an image XObject's <c>/SMask</c>.</summary>
    internal sealed class SoftMaskSamples
    {
        internal SoftMaskSamples(byte[] data, int width, int height, float[] matte = null)
        {
            Data = data;
            Width = width;
            Height = height;
            Matte = matte;
        }

        internal byte[] Data { get; }
        internal int Width { get; }
        internal int Height { get; }

        /// <summary>
        /// The <c>/Matte</c> backdrop the base image's colour samples were pre-blended against
        /// (§11.6.5.3), in the base image's colour space — or null when they are straight.
        /// </summary>
        internal float[] Matte { get; }
    }

    /// <summary>
    /// Replacement pixels for <see cref="PdfImageReplacer"/>, in a form a PDF image XObject can
    /// carry directly. Each factory validates that the bytes it is handed actually describe the
    /// image they claim to, so a mismatch surfaces as an argument exception here rather than as
    /// a corrupt image in the output.
    /// </summary>
    public sealed class ReplacementImage
    {
        private ReplacementImage(byte[] data, int width, int height,
            PdfName filter, PdfName colorSpace, int bitsPerComponent,
            PdfArray decode, SoftMaskSamples softMask)
        {
            Data = data;
            Width = width;
            Height = height;
            Filter = filter;
            ColorSpace = colorSpace;
            BitsPerComponent = bitsPerComponent;
            Decode = decode;
            SoftMask = softMask;
        }

        internal byte[] Data { get; }
        internal int Width { get; }
        internal int Height { get; }
        /// <summary>Stream filter, or null when the samples are stored uncompressed.</summary>
        internal PdfName Filter { get; }
        internal PdfName ColorSpace { get; }
        internal int BitsPerComponent { get; }
        /// <summary>A <c>/Decode</c> array these samples require, or null.</summary>
        internal PdfArray Decode { get; }
        /// <summary>Alpha channel to write as a <c>/SMask</c>, or null when opaque.</summary>
        internal SoftMaskSamples SoftMask { get; }

        /// <summary><c>/SMaskInData</c> for a JPEG 2000 source whose file declares an opacity channel; 0 otherwise.</summary>
        internal int SMaskInData { get; private set; }

        /// <summary>
        /// A JPEG, embedded verbatim as <c>DCTDecode</c>. Dimensions and colour space are read
        /// from the codestream's SOF marker rather than taken from the caller, so they cannot
        /// disagree with the bytes.
        ///
        /// <para><b>Four-component (CMYK / YCCK) JPEGs are supported when they carry an Adobe
        /// APP14 marker</b>, which is what makes their sample polarity knowable: Adobe writes
        /// those values inverted, so the image is emitted as <c>/DeviceCMYK</c> with
        /// <c>/Decode [1 0 1 0 1 0 1 0]</c>. Without the marker nothing in the codestream says
        /// which way round the values are, and this throws rather than guess — a wrong guess
        /// produces a colour-negative image that still opens fine.</para>
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The bytes carry no readable frame header, use an unsupported component count, or are a
        /// four-component frame with no Adobe marker.
        /// </exception>
        /// <remarks>
        /// The colour space and the Adobe rule are <see cref="JpegImageDictionary"/>'s, whose remarks
        /// named this method as one of its three callers while this carried its own copy of the
        /// whole decision. The copies agreed, down to the wording of the refusal, which is what a
        /// duplicate looks like the day before it drifts.
        /// </remarks>
        public static ReplacementImage Jpeg(byte[] jpegBytes)
        {
            if (jpegBytes == null) throw new ArgumentNullException(nameof(jpegBytes));
            if (!JpegImageDictionary.TryDescribe(
                    jpegBytes, out var frame, out string colorSpace, out double[] decodeValues, out string why))
                throw new ArgumentException(why, nameof(jpegBytes));

            PdfArray decode = null;
            if (decodeValues != null)
            {
                decode = new PdfArray();
                foreach (double v in decodeValues) decode.Add(new PdfNumber(v));
            }

            ValidatePixelArea(frame.Width, frame.Height);
            return new ReplacementImage(jpegBytes, frame.Width, frame.Height,
                PdfName.DCTDecode, new PdfName(colorSpace), 8, decode, softMask: null);
        }

        /// <summary>
        /// A JPEG 2000 file (JP2) or raw codestream, embedded verbatim as <c>JPXDecode</c>.
        /// Dimensions come from the codestream's SIZ marker; the colour space is named when the
        /// file names sRGB, greyscale or CMYK (or is a raw codestream of 1, 3 or 4 components) and
        /// is otherwise left to the file, as ISO 32000-2 §7.4.9 provides — naming one for an sYCC
        /// or ICC-profiled file would repaint it. A file that declares an opacity channel gets
        /// <c>/SMaskInData 1</c>, so a viewer uses it.
        /// </summary>
        /// <exception cref="ArgumentException">The bytes are not a JPEG 2000 file with a readable SIZ, or name no colour space a PDF can.</exception>
        public static ReplacementImage Jpeg2000(byte[] jpxBytes)
        {
            if (jpxBytes == null) throw new ArgumentNullException(nameof(jpxBytes));
            if (!Core.Imaging.Jpx.JpxImageDictionary.TryDescribe(jpxBytes, out int width, out int height,
                    out int bitsPerComponent, out string colorSpace, out int smaskInData, out string why))
                throw new ArgumentException(why, nameof(jpxBytes));
            ValidatePixelArea(width, height);
            return new ReplacementImage(jpxBytes, width, height, PdfName.JPXDecode,
                colorSpace == null ? null : new PdfName(colorSpace), bitsPerComponent, decode: null, softMask: null)
            { SMaskInData = smaskInData };
        }

        /// <summary>
        /// Uncompressed 8-bit RGB samples, three bytes per pixel, row-major from the top-left —
        /// exactly <c>width * height * 3</c> bytes.
        /// </summary>
        public static ReplacementImage Rgb24(byte[] rgbBytes, int widthPx, int heightPx)
            => Raw(rgbBytes, widthPx, heightPx, PdfName.DeviceRGB, 3, nameof(rgbBytes));

        /// <summary>
        /// Uncompressed 8-bit grayscale samples, one byte per pixel, row-major from the
        /// top-left — exactly <c>width * height</c> bytes.
        /// </summary>
        public static ReplacementImage Gray8(byte[] grayBytes, int widthPx, int heightPx)
            => Raw(grayBytes, widthPx, heightPx, PdfName.DeviceGray, 1, nameof(grayBytes));

        /// <summary>
        /// Uncompressed 8-bit RGBA samples, four bytes per pixel, row-major from the top-left —
        /// exactly <c>width * height * 4</c> bytes. The colour channels become the image and the
        /// alpha channel becomes its <c>/SMask</c>; a fully opaque alpha channel is dropped rather
        /// than written, since it would have no effect.
        ///
        /// <para>PDF soft masks are <b>straight</b> (non-premultiplied) alpha, so pass
        /// straight-alpha samples — premultiplied colour renders dark at the edges.</para>
        /// </summary>
        public static ReplacementImage Rgba32(byte[] rgbaBytes, int widthPx, int heightPx)
        {
            if (rgbaBytes == null) throw new ArgumentNullException(nameof(rgbaBytes));
            ValidatePixelArea(widthPx, heightPx);

            long expected = (long)widthPx * heightPx * 4;
            if (rgbaBytes.Length != expected)
                throw new ArgumentException(
                    $"Expected exactly {expected} bytes for a {widthPx}x{heightPx} RGBA 8-bit " +
                    $"image; got {rgbaBytes.Length}.",
                    nameof(rgbaBytes));

            int count = widthPx * heightPx;
            var rgb = new byte[count * 3];
            var alpha = new byte[count];
            bool opaque = true;
            for (int i = 0; i < count; i++)
            {
                rgb[i * 3]     = rgbaBytes[i * 4];
                rgb[i * 3 + 1] = rgbaBytes[i * 4 + 1];
                rgb[i * 3 + 2] = rgbaBytes[i * 4 + 2];
                byte a = rgbaBytes[i * 4 + 3];
                alpha[i] = a;
                if (a != 255) opaque = false;
            }

            var image = Rgb24(rgb, widthPx, heightPx);
            return opaque ? image : image.WithSoftMask(alpha, widthPx, heightPx);
        }

        /// <summary>
        /// Returns a copy of this image carrying an explicit 8-bit grayscale alpha channel,
        /// written as a <c>/SMask</c>. The mask's dimensions need not match the image's — a reader
        /// scales it — so a small mask can shape a large photograph.
        /// </summary>
        public ReplacementImage WithSoftMask(byte[] gray8Alpha, int widthPx, int heightPx)
            => WithSoftMask(gray8Alpha, widthPx, heightPx, matteComponents: null);

        /// <summary>
        /// The same, for an image whose colour samples have already been <b>blended against a
        /// backdrop colour</b> — written as the mask's <c>/Matte</c> entry (§11.6.5.3).
        ///
        /// <para><b>This is the exact inverse of the contract on <see cref="Rgba32"/>.</b> Without
        /// <c>/Matte</c> the samples must be straight; with it they must be pre-blended,
        /// <c>c′ = m + α·(c − m)</c>, because that is the equation a reader inverts to recover the
        /// colour (<c>ContentStreamInterpreter.ApplyImageSoftMask</c>, and the SVG path with it).
        /// Passing straight samples here is worse than passing no matte at all: the reader divides
        /// by α anyway and every partly-transparent pixel comes out too bright. Both of this
        /// library's renderers do exactly that, so the mistake is not hypothetical.</para>
        ///
        /// <para>Two things §11.6.5.3 makes non-optional, both enforced here rather than left to
        /// produce a plausible-looking wrong image:</para>
        /// <list type="number">
        ///   <item>the matte has one component per component of <b>this</b> image's colour space —
        ///   a reader has nothing else to infer the space from, so a 3-element matte on a grayscale
        ///   image is silently read as DeviceRGB;</item>
        ///   <item>the mask's dimensions must equal the image's, which is why the scaling tolerance
        ///   documented on the other overload does not apply to this one — un-blending needs the
        ///   α that was used, not a resampled approximation of it.</item>
        /// </list>
        /// </summary>
        /// <param name="matteComponents">
        /// Backdrop colour in this image's colour space, each component in 0..1. Null selects the
        /// straight-alpha overload's behaviour.
        /// </param>
        /// <param name="gray8Alpha">
        /// One 8-bit alpha sample per pixel, row-major — the <c>/SMask</c>'s own samples, which need
        /// not match the base image's dimensions.
        /// </param>
        /// <param name="widthPx">Width of <paramref name="gray8Alpha"/> in samples.</param>
        /// <param name="heightPx">Height of <paramref name="gray8Alpha"/> in samples.</param>
        public ReplacementImage WithSoftMask(
            byte[] gray8Alpha, int widthPx, int heightPx, float[] matteComponents)
        {
            if (gray8Alpha == null) throw new ArgumentNullException(nameof(gray8Alpha));
            ValidatePixelArea(widthPx, heightPx);

            long expected = (long)widthPx * heightPx;
            if (gray8Alpha.Length != expected)
                throw new ArgumentException(
                    $"Expected exactly {expected} bytes for a {widthPx}x{heightPx} 8-bit alpha " +
                    $"channel; got {gray8Alpha.Length}.",
                    nameof(gray8Alpha));

            float[] matte = null;
            if (matteComponents != null)
            {
                int arity = ComponentCount(ColorSpace);
                if (matteComponents.Length != arity)
                    throw new ArgumentException(
                        $"/Matte needs one component per component of the image's colour space: " +
                        $"{ColorSpace} takes {arity}, got {matteComponents.Length}. A reader infers " +
                        "the matte's colour space from the array's length, so a mismatched count is " +
                        "not rejected downstream — it is read as a different space.",
                        nameof(matteComponents));

                foreach (float component in matteComponents)
                    if (!(component >= 0f && component <= 1f))     // also rejects NaN
                        throw new ArgumentOutOfRangeException(nameof(matteComponents),
                            $"Matte component {component} is outside the 0..1 range every device " +
                            "colour space defines (§8.6.4). 0..255 byte values are the usual cause.");

                if (widthPx != Width || heightPx != Height)
                    throw new ArgumentException(
                        $"§11.6.5.3 requires a matted soft mask to have the image's own dimensions: " +
                        $"the image is {Width}x{Height}, the mask {widthPx}x{heightPx}. Un-blending " +
                        "divides each colour sample by the alpha that produced it, so a scaled mask " +
                        "supplies the wrong divisor.",
                        nameof(gray8Alpha));

                matte = (float[])matteComponents.Clone();          // the caller keeps no handle on it
            }

            return new ReplacementImage(Data, Width, Height, Filter, ColorSpace, BitsPerComponent,
                Decode, new SoftMaskSamples(gray8Alpha, widthPx, heightPx, matte));
        }

        /// <summary>
        /// Component count of the colour space a replacement image carries, for validating
        /// <c>/Matte</c>'s arity. <b>One implementation, in
        /// <see cref="Core.Constants.PdfColorSpaceNames.ComponentCount(string,int)"/>.</b>
        /// </summary>
        /// <remarks>
        /// <para><b>Every colour space the shared answer knows</b>, though only <c>/DeviceGray</c>,
        /// <c>/DeviceRGB</c> and <c>/DeviceCMYK</c> reach it: <see cref="ReplacementImage.ColorSpace"/>
        /// is assigned only by the private constructor, and every factory — <c>Jpeg</c>,
        /// <c>Rgb24</c>, <c>Gray8</c>, <c>Rgba32</c>, <c>RawWithMask</c>, <c>Raw</c> (private) — sets
        /// one of those three. One Core rule, so a future factory admitting a non-device space
        /// inherits the right answer instead of a silent 1.</para>
        /// </remarks>
        private static int ComponentCount(PdfName colorSpace) =>
            Core.Constants.PdfColorSpaceNames.ComponentCount(colorSpace?.Value, fallback: 1);

        /// <summary>Flate-compressed 8-bit samples in a device space plus an optional mask — the fitting output.</summary>
        internal static ReplacementImage RawWithMask(
            byte[] samples, int width, int height, PdfName colorSpace, SoftMaskSamples mask)
        {
            byte[] encoded = FlateEncodeFilter.Encode(samples);
            return new ReplacementImage(encoded, width, height,
                PdfName.FlateDecode, colorSpace, 8, decode: null, softMask: mask);
        }

        private static ReplacementImage Raw(
            byte[] bytes, int widthPx, int heightPx, PdfName colorSpace, int components, string paramName)
        {
            if (bytes == null) throw new ArgumentNullException(paramName);
            ValidatePixelArea(widthPx, heightPx);

            long expected = (long)widthPx * heightPx * components;
            if (bytes.Length != expected)
                throw new ArgumentException(
                    $"Expected exactly {expected} bytes for a {widthPx}x{heightPx} " +
                    $"{components}-component 8-bit image; got {bytes.Length}.",
                    paramName);

            return new ReplacementImage(bytes, widthPx, heightPx,
                filter: null, colorSpace, 8, decode: null, softMask: null);
        }

        private static void ValidatePixelArea(int widthPx, int heightPx)
        {
            if (widthPx <= 0) throw new ArgumentOutOfRangeException(nameof(widthPx));
            if (heightPx <= 0) throw new ArgumentOutOfRangeException(nameof(heightPx));
            if ((long)widthPx * heightPx > PdfLimits.MaxImagePixelArea)
                throw new ArgumentOutOfRangeException(nameof(widthPx),
                    $"Image area {widthPx}x{heightPx} exceeds the {PdfLimits.MaxImagePixelArea}-pixel limit.");
        }
    }

    /// <summary>
    /// A replacement image as 8-bit samples in its own device space — grey, RGB or CMYK — with a
    /// straight alpha plane beside them, which is the form fitting works on. Decoding happens once,
    /// here, so the fit path never has to branch on the source encoding again.
    /// </summary>
    /// <remarks>
    /// <para><b>The space is kept.</b> This flattened everything to RGBA and re-emitted
    /// <c>/DeviceRGB</c>, so a CMYK JPEG fitted with <see cref="ImageFit.Contain"/> or
    /// <see cref="ImageFit.Cover"/> came back as Skia's naive RGB of its inks — the black plate
    /// spread over three — and a grey image tripled in size. A JPEG is read through
    /// <see cref="Base.Images.JpegSamples"/>, whose four-component route gives the inks
    /// themselves.</para>
    /// </remarks>
    internal sealed class RasterSamples
    {
        private RasterSamples(byte[] colour, int channels, PdfName colorSpace, byte[] alpha, int width, int height)
        {
            Colour = colour;
            Channels = channels;
            ColorSpace = colorSpace;
            Alpha = alpha;
            Width = width;
            Height = height;
        }

        /// <summary>The colour samples, <see cref="Channels"/> bytes per pixel, row-major from the top-left.</summary>
        internal byte[] Colour { get; }
        internal int Channels { get; }
        internal PdfName ColorSpace { get; }
        /// <summary>One straight alpha byte per pixel, or null when every pixel is opaque.</summary>
        internal byte[] Alpha { get; }
        internal int Width { get; }
        internal int Height { get; }

        /// <summary>Returns null when the samples cannot be recovered (bytes no decoder reads, or a filtered raw image).</summary>
        internal static RasterSamples From(ReplacementImage image)
        {
            byte[] colour;
            PdfName space;
            if (image.Filter == PdfName.DCTDecode)
            {
                colour = Base.Images.JpegSamples.TryDecode(image.Data, PdfLimits.MaxImageDimension, PdfLimits.MaxImagePixelArea,
                    out int w, out int h, out int n);
                if (colour == null || w != image.Width || h != image.Height) return null;
                // Four components come back as the inks, the Adobe inversion undone — DeviceCMYK as
                // it stands; the JPEG's inverted /Decode is not carried into the fitted image.
                space = image.ColorSpace;
            }
            else
            {
                int n = Core.Constants.PdfColorSpaceNames.ComponentCount(image.ColorSpace?.Value, fallback: 0);
                if (image.Filter != null || n == 0 || image.Data.Length < (long)image.Width * image.Height * n) return null;
                colour = image.Data;
                space = image.ColorSpace;
            }

            int channels = Core.Constants.PdfColorSpaceNames.ComponentCount(space?.Value, fallback: 0);
            if (channels == 0) return null;
            byte[] alpha = image.SoftMask == null ? null : AlphaFrom(image.SoftMask, image.Width, image.Height);
            return new RasterSamples(colour, channels, space, alpha, image.Width, image.Height);
        }

        /// <summary>
        /// A soft mask as one alpha byte per pixel, nearest-neighbour scaled when the mask's
        /// dimensions differ from the image's (which PDF permits).
        /// </summary>
        private static byte[] AlphaFrom(SoftMaskSamples mask, int width, int height)
        {
            var alpha = new byte[(long)width * height];
            for (int y = 0; y < height; y++)
            {
                int my = mask.Height == height ? y : (int)((long)y * mask.Height / height);
                for (int x = 0; x < width; x++)
                {
                    int mx = mask.Width == width ? x : (int)((long)x * mask.Width / width);
                    int mi = my * mask.Width + mx;
                    alpha[y * width + x] = mi >= 0 && mi < mask.Data.Length ? mask.Data[mi] : (byte)255;
                }
            }
            return alpha;
        }

        /// <summary>
        /// Takes the centred sub-rectangle of the given size — the dual of <see cref="PadTo"/>.
        /// The caller guarantees the crop is no larger than the source on either axis.
        /// </summary>
        internal RasterSamples CropTo(int cropWidth, int cropHeight)
        {
            int dx = (Width - cropWidth) / 2, dy = (Height - cropHeight) / 2;
            var colour = new byte[(long)cropWidth * cropHeight * Channels];
            byte[] alpha = Alpha == null ? null : new byte[(long)cropWidth * cropHeight];
            for (int y = 0; y < cropHeight; y++)
            {
                Buffer.BlockCopy(Colour, ((y + dy) * Width + dx) * Channels, colour, y * cropWidth * Channels, cropWidth * Channels);
                if (alpha != null) Buffer.BlockCopy(Alpha, (y + dy) * Width + dx, alpha, y * cropWidth, cropWidth);
            }
            return new RasterSamples(colour, Channels, ColorSpace, alpha, cropWidth, cropHeight);
        }

        /// <summary>Centres these samples on a fully transparent canvas of the given size.</summary>
        internal RasterSamples PadTo(int canvasWidth, int canvasHeight)
        {
            int dx = (canvasWidth - Width) / 2, dy = (canvasHeight - Height) / 2;
            var colour = new byte[(long)canvasWidth * canvasHeight * Channels];
            var alpha = new byte[(long)canvasWidth * canvasHeight];   // the padding is transparent
            for (int y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(Colour, y * Width * Channels, colour, ((y + dy) * canvasWidth + dx) * Channels, Width * Channels);
                int row = (y + dy) * canvasWidth + dx;
                if (Alpha != null) Buffer.BlockCopy(Alpha, y * Width, alpha, row, Width);
                else for (int x = 0; x < Width; x++) alpha[row + x] = 255;
            }
            return new RasterSamples(colour, Channels, ColorSpace, alpha, canvasWidth, canvasHeight);
        }

        internal ReplacementImage ToReplacementImage()
        {
            bool opaque = true;
            if (Alpha != null)
                foreach (byte a in Alpha)
                    if (a != 255) { opaque = false; break; }
            byte[] colour = Colour.Length == (long)Width * Height * Channels
                ? Colour
                : Colour.AsSpan(0, Width * Height * Channels).ToArray();
            return ReplacementImage.RawWithMask(colour, Width, Height, ColorSpace,
                opaque ? null : new SoftMaskSamples(Alpha, Width, Height));
        }
    }
}
