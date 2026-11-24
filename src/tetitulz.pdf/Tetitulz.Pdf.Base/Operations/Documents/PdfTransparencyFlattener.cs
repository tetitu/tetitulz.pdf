// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>The colour space a flattened page's image is written in.</summary>
    public enum FlattenedColorSpace
    {
        /// <summary><c>/DeviceRGB</c>, three 8-bit components.</summary>
        Rgb,

        /// <summary><c>/DeviceGray</c>, one 8-bit component, by BT.601 luminance.</summary>
        Gray,

        /// <summary>
        /// The page's separations: <c>/DeviceCMYK</c> from its process plates, or — when it paints with
        /// spot inks — <c>/DeviceN</c> carrying each spot as a channel of its own, so a press still
        /// gets the spot plate. What PDF/X-1a needs.
        /// </summary>
        Cmyk,
    }

    /// <summary>Policy for <see cref="PdfTransparencyFlattener.Flatten"/>.</summary>
    public sealed class TransparencyFlattenOptions
    {
        /// <summary>Resolution each flattened page is rendered at. Defaults to 300.</summary>
        public int Dpi { get; set; } = 300;

        /// <summary>The flattened image's colour space. Defaults to <see cref="FlattenedColorSpace.Rgb"/>.</summary>
        public FlattenedColorSpace ColorSpace { get; set; } = FlattenedColorSpace.Rgb;

        /// <summary>
        /// Under <see cref="FlattenedColorSpace.Cmyk"/>, a CMYK output profile (an ICC <c>prtr</c>
        /// profile with <c>B2A</c> tables) that RGB and CIE-based content reaches the process plates
        /// through, instead of the naive inverse of the CMYK conversion — the press's characterisation,
        /// typically the PDF/X output intent's. CMYK, grey, spot and DeviceN colour keep their own
        /// components either way. Null: no profile.
        /// </summary>
        public byte[] OutputIccProfile { get; set; }

        /// <summary>
        /// Lay the page's text back over its image as invisible text, so a flattened page can still
        /// be searched, selected and copied. On by default.
        /// </summary>
        public bool KeepTextSearchable { get; set; } = true;

        /// <summary>
        /// Flatten only where the page paints transparently: everything else stays vector, and an
        /// image of the page covers just the bounds of what painted transparently. Off by default.
        /// A page whose patterns or Type 3 fonts reach transparency is flattened whole, with a
        /// warning saying why.
        /// </summary>
        public bool KeepOpaqueContentAsVector { get; set; }

        /// <summary>
        /// Flatten a signed document anyway. Off by default: replacing a page's content is not a
        /// change any signature permits, so a signature over it would be reported broken or the
        /// certification invalid.
        /// </summary>
        public bool AllowSignedDocument { get; set; }
    }

    /// <summary>What a flattening did, and what it left.</summary>
    public sealed class TransparencyFlattenResult
    {
        internal TransparencyFlattenResult(IReadOnlyList<int> flattened, IReadOnlyList<int> keptAsVector,
            IReadOnlyList<TransparencyFinding> found, IReadOnlyList<TransparencyFinding> remaining, IReadOnlyList<string> warnings)
        {
            FlattenedPages = flattened;
            PagesKeptAsVector = keptAsVector;
            Found = found;
            Remaining = remaining;
            Warnings = warnings;
        }

        /// <summary>Zero-based indices of the pages flattened, ascending.</summary>
        public IReadOnlyList<int> FlattenedPages { get; }

        /// <summary>
        /// Of <see cref="FlattenedPages"/>, those flattened only where they painted transparently, the
        /// rest kept as vector (<see cref="TransparencyFlattenOptions.KeepOpaqueContentAsVector"/>).
        /// </summary>
        public IReadOnlyList<int> PagesKeptAsVector { get; }

        /// <summary>The transparency the input held.</summary>
        public IReadOnlyList<TransparencyFinding> Found { get; }

        /// <summary>
        /// The transparency the output still holds — found by scanning what was written, not
        /// assumed. Empty when everything was flattened; otherwise transparency inside an annotation,
        /// which flattening a page's content does not reach.
        /// </summary>
        public IReadOnlyList<TransparencyFinding> Remaining { get; }

        /// <summary>Notes on what the flattening changed beyond the pixels.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>
    /// Finds the pages that use transparency, and flattens them — each replaced by an opaque image of
    /// itself, with its text laid back over it as invisible text.
    /// </summary>
    /// <remarks>
    /// <para><b>Why flatten.</b> PDF/A-1 (ISO 19005-1 §6.4) and PDF/X-1a and X-3 forbid the
    /// transparent imaging model, so a document using it cannot be converted to them as it stands;
    /// both converters report it as a blocker. The renderer already composites every transparency
    /// construct correctly — groups, soft masks, knockout, blend modes — so an image of the page is
    /// what the page looks like, with nothing transparent left in it.</para>
    /// <para><b>A whole page at a time, and what that costs.</b> A flattened page is a raster at
    /// <see cref="TransparencyFlattenOptions.Dpi"/>: its vector artwork and its text's outlines are
    /// pixels now, sharp to that resolution and no further. The text stays searchable and
    /// selectable through the invisible layer. Pages with no transparency are not touched.</para>
    /// <para><b>Or only where it is transparent</b>
    /// (<see cref="TransparencyFlattenOptions.KeepOpaqueContentAsVector"/>): the page's content stays,
    /// with every operation that painted transparently taken out of it, and an image of the page covers
    /// just the bounds those operations painted — measured by the renderer, operation by operation
    /// (<see cref="TransparencyRegions"/>). A page whose only transparency is a group or an unused
    /// graphics state keeps all of itself as vector.</para>
    /// <para><b>Annotations are left as they are.</b> The page's content is flattened; its
    /// annotations are not drawn into the image and stay annotations — links still work, form
    /// fields still fill. An annotation that is itself transparent is reported in
    /// <see cref="TransparencyFlattenResult.Remaining"/>, from a scan of the output.</para>
    /// <para><b>An incremental update</b>: the flattened pages are superseded at their own object
    /// numbers, so every reference to them still holds. Encrypted input is refused, as every
    /// writer here that would append clear text to it is; decrypt it first.</para>
    /// </remarks>
    public static class PdfTransparencyFlattener
    {
        /// <summary>The resource name the flattened image is drawn by.</summary>
        private const string ImageName = "Flattened0";

        /// <summary>Every transparency construct each page reaches.</summary>
        public static IReadOnlyList<TransparencyFinding> FindTransparency(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            return TransparencyScan.Scan(doc);
        }

        /// <summary>Every transparency construct each page of an open document reaches.</summary>
        public static IReadOnlyList<TransparencyFinding> FindTransparency(PdfDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return TransparencyScan.Scan(document);
        }

        /// <summary>
        /// Writes <paramref name="input"/> to <paramref name="output"/> with every page that uses
        /// transparency flattened. A document with none is copied unchanged.
        /// </summary>
        /// <exception cref="PdfException">The document is encrypted, or signed and
        /// <see cref="TransparencyFlattenOptions.AllowSignedDocument"/> is not set.</exception>
        public static TransparencyFlattenResult Flatten(Stream input, Stream output, TransparencyFlattenOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options ??= new TransparencyFlattenOptions();
            if (options.Dpi <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Dpi must be positive.");

            byte[] original = ReadAll(input);
            var warnings = new List<string>();
            List<TransparencyFinding> found;
            var pages = new SortedSet<int>();
            var keptAsVector = new SortedSet<int>();
            byte[] flattened;

            using (var doc = PdfDocument.Open(new MemoryStream(original, writable: false)))
            {
                if (doc.IsEncrypted)
                    throw new PdfException(
                        "Cannot flatten an encrypted document: the flattened pages would be appended in clear " +
                        "text. Decrypt it first (PdfEncryptedRoundTrip), then flatten.");
                if (!options.AllowSignedDocument && PdfSignatureDetection.HasSignature(doc.Trailer, doc.GetObject))
                    throw new PdfException(
                        "The document is signed, and replacing a page's content is a change no signature " +
                        $"permits. Set {nameof(TransparencyFlattenOptions)}.{nameof(TransparencyFlattenOptions.AllowSignedDocument)} " +
                        "to flatten it anyway.");

                found = TransparencyScan.Scan(doc);
                foreach (var f in found)
                    // An annotation's own opacity and blend mode are composited over the page by the
                    // viewer; flattening the page's content does not reach them, so they stay remaining.
                    if (f.Kind != TransparencyKind.AnnotationOpacity && f.Kind != TransparencyKind.AnnotationBlendMode)
                        pages.Add(f.PageIndex);

                if (pages.Count == 0)
                {
                    output.Write(original, 0, original.Length);
                    return new TransparencyFlattenResult(Array.Empty<int>(), Array.Empty<int>(), found, found, warnings);
                }

                flattened = WriteFlattenedPages(doc, original, pages, options, keptAsVector, warnings);
            }

            byte[] result = flattened;
            if (options.KeepTextSearchable)
            {
                var whole = new SortedSet<int>(pages);
                whole.ExceptWith(keptAsVector);
                var words = CollectWords(original, whole);
                // A page kept as vector still holds its text — shown invisibly where it was
                // transparent; only what a dropped form showed is gone, and only that is laid back.
                foreach (var lost in WordsLost(original, flattened, keptAsVector)) words[lost.Key] = lost.Value;
                if (words.Count > 0)
                {
                    using var withText = new MemoryStream();
                    PdfOcrLayerWriter.AddLayers(new MemoryStream(flattened, writable: false), words, withText,
                        alwaysEmbeddedFont: true);
                    result = withText.ToArray();
                }
            }

            output.Write(result, 0, result.Length);

            List<TransparencyFinding> remaining;
            using (var check = PdfDocument.Open(new MemoryStream(result, writable: false)))
                remaining = TransparencyScan.Scan(check);
            if (remaining.Count > 0)
                warnings.Add($"{remaining.Count} transparency construct(s) remain, inside annotations, which " +
                             "flattening a page's content does not reach.");
            int wholePages = pages.Count - keptAsVector.Count;
            if (wholePages > 0)
                warnings.Add($"{wholePages} page(s) flattened to {options.Dpi} dpi images; their vector artwork is " +
                             "pixels now" + (options.KeepTextSearchable ? ", and their text an invisible layer over them." : "."));
            if (keptAsVector.Count > 0)
                warnings.Add($"{keptAsVector.Count} page(s) flattened only where they painted transparently, at {options.Dpi} " +
                             "dpi; the rest of each is vector still, and text shown transparently is invisible text now.");

            return new TransparencyFlattenResult(new List<int>(pages), new List<int>(keptAsVector), found, remaining, warnings);
        }

        /// <summary>
        /// One incremental update superseding each page in <paramref name="pages"/>: with an image of
        /// itself, or — under <see cref="TransparencyFlattenOptions.KeepOpaqueContentAsVector"/> — with
        /// its content made opaque (<see cref="TransparencyRegions"/>) and an image of itself over just
        /// the regions that painted transparently. Those pages go in <paramref name="keptAsVector"/>.
        /// </summary>
        private static byte[] WriteFlattenedPages(PdfDocument doc, byte[] original, SortedSet<int> pages,
            TransparencyFlattenOptions options, SortedSet<int> keptAsVector, List<string> warnings)
        {
            var renderOptions = new PdfRenderOptions { Intent = RenderIntent.Print, ContentOnly = true };
            SeparationOutput plateOutput = null;
            if (options.ColorSpace == FlattenedColorSpace.Cmyk && options.OutputIccProfile != null)
            {
                plateOutput = SeparationOutput.TryCreate(options.OutputIccProfile, blackPointCompensation: true, out string reason);
                if (plateOutput == null) throw new ArgumentException("The output ICC profile cannot be used: " + reason, nameof(options));
            }
            int next = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var objects = new List<IndirectObject>();
            int AddObject(PdfObject value)
            {
                int number = next++;
                objects.Add(new IndirectObject(number, 0, value));
                return number;
            }

            foreach (int p in pages)
            {
                PdfPage page = doc.Pages[p];
                if (page.Dictionary.ObjectNumber <= 0)
                    throw new PdfException($"Page {p + 1} is not an indirect object, so it cannot be superseded.");

                PageBoxMetrics box = PageBoxMetrics.Of(PdfPageRenderer.ResolveRenderBox(page, doc, renderOptions));
                int rotation = PdfPageRotation.Normalize(page.Rotate, out _);
                float[] placement = PlacementMatrix(box, rotation);
                PageRaster Raster() => Rasterize(original, p, doc, options, renderOptions, plateOutput, AddObject);

                TransparencyRegions regions = null;
                if (options.KeepOpaqueContentAsVector)
                {
                    regions = TransparencyRegions.TryRewrite(page, doc, renderOptions, out string whyNot);
                    if (regions == null) warnings.Add($"Page {p + 1} was flattened whole: {whyNot}.");
                }

                using var content = new MemoryStream();
                PdfDictionary resources;
                if (regions == null)
                {
                    int imageNumber = AddObject(ImageStream(Raster()));
                    var xobjects = new PdfDictionary();
                    xobjects.Set(new PdfName(ImageName), new PdfReference(imageNumber, 0));
                    resources = new PdfDictionary();
                    resources.Set(PdfName.XObject, xobjects);
                    WriteAscii(content, $"q {Format(placement)} cm /{ImageName} Do Q\n");
                }
                else
                {
                    keptAsVector.Add(p);
                    resources = regions.Resources;
                    // The page's own content, made opaque, isolated so the image is drawn in the page's initial state.
                    WriteAscii(content, PdfOperators.Save + "\n");
                    content.Write(regions.Content, 0, regions.Content.Length);
                    WriteAscii(content, PdfOperators.Restore + "\n");

                    List<SKRect> clip = ClipRegions(regions.Regions, box, options.Dpi);
                    if (clip.Count > 0 && Crop(Raster(), placement, clip, out PageRaster cropped, out float[] within))
                    {
                        string name = FreeImageName(resources, doc);
                        int imageNumber = AddObject(ImageStream(cropped));
                        if (!(resources[PdfName.XObject] is PdfDictionary xobjects))
                        {
                            xobjects = new PdfDictionary();
                            resources.Set(PdfName.XObject, xobjects);
                        }
                        xobjects.Set(new PdfName(name), new PdfReference(imageNumber, 0));

                        var drawn = new StringBuilder(PdfOperators.Save).Append(' ');
                        foreach (SKRect r in clip)
                            drawn.Append(Format(new[] { r.Left, r.Top, r.Width, r.Height })).Append(' ')
                                 .Append(PdfOperators.Rectangle).Append(' ');
                        drawn.Append(PdfOperators.Clip).Append(' ').Append(PdfOperators.EndPath).Append(' ')
                             .Append(Format(placement)).Append(" cm ").Append(Format(within)).Append(" cm /")
                             .Append(name).Append(" Do ").Append(PdfOperators.Restore).Append('\n');
                        WriteAscii(content, drawn.ToString());
                    }
                }

                var contentStream = new PdfStream { RawData = FlateEncodeFilter.Encode(content.ToArray()) };
                contentStream.Set(PdfName.Filter, PdfName.FlateDecode);
                contentStream.Set(PdfName.Length, new PdfNumber(contentStream.RawData.Length));
                int contentNumber = AddObject(contentStream);

                PdfDictionary superseding = PdfDictionary.CloneShallow(page.Dictionary);
                superseding.Remove(PdfName.Group);
                superseding.Set(PdfName.Contents, new PdfReference(contentNumber, 0));
                superseding.Set(PdfName.Resources, resources);
                objects.Add(new IndirectObject(page.Dictionary.ObjectNumber, page.Dictionary.GenerationNumber, superseding));
            }

            var trailer = PdfPageContentAppender.BuildTrailer(doc.Trailer, next);
            using var output = new MemoryStream();
            IncrementalUpdateWriter.WriteUpdate(
                new MemoryStream(original, writable: false), output, objects, trailer, doc.Parser.StartXrefOffset, next);
            return output.ToArray();
        }

        /// <summary>
        /// The <c>cm</c> operands that draw the rendered image — which is the page as a viewer shows
        /// it, <c>/Rotate</c> applied — back onto the render box in unrotated user space, so the page,
        /// rotated as before, looks as it did.
        /// </summary>
        /// <remarks>
        /// A quarter turn clockwise (<c>/Rotate 90</c>) shows user +y as the image's right and user −x
        /// as its up, so the image's origin, its bottom-left, is the box's lower-right corner; the
        /// other two turns follow the same way.
        /// </remarks>
        internal static string Placement(PageBoxMetrics box, int rotation) => Format(PlacementMatrix(box, rotation));

        /// <summary><see cref="Placement"/>'s matrix.</summary>
        private static float[] PlacementMatrix(PageBoxMetrics box, int rotation)
        {
            float x = box.X, y = box.Y, w = box.Width, h = box.Height;
            return rotation switch
            {
                90 => new[] { 0f, h, -w, 0f, x + w, y },
                180 => new[] { -w, 0f, 0f, -h, x + w, y + h },
                270 => new[] { 0f, -h, w, 0f, x, y + h },
                _ => new[] { w, 0f, 0f, h, x, y },
            };
        }

        private static string Format(float[] values)
            => string.Join(" ", Array.ConvertAll(values, v => v.ToString("0.####", CultureInfo.InvariantCulture)));

        private static void WriteAscii(Stream to, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            to.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// The regions to cover, each grown by a device pixel at <paramref name="dpi"/> — an edge's
        /// antialiasing belongs to the image — and cut to the page box; empty ones dropped.
        /// </summary>
        internal static List<SKRect> ClipRegions(IReadOnlyList<SKRect> regions, PageBoxMetrics box, int dpi)
        {
            float pad = PdfUnits.PointsPerInch / dpi;
            var page = new SKRect(box.X, box.Y, box.X + box.Width, box.Y + box.Height);
            var clipped = new List<SKRect>();
            foreach (SKRect r in regions)
            {
                var grown = new SKRect(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad);
                if (grown.IntersectsWith(page))
                {
                    grown.Intersect(page);
                    if (grown.Width > 0 && grown.Height > 0) clipped.Add(grown);
                }
            }
            return clipped;
        }

        /// <summary>
        /// The part of <paramref name="raster"/> — the whole page, drawn by <paramref name="placement"/>
        /// — that covers <paramref name="regions"/>' bounding box, whole pixels outward, and the matrix
        /// that places that part where it was inside the unit square the whole image fills. False when
        /// the regions miss the image.
        /// </summary>
        internal static bool Crop(PageRaster raster, float[] placement, IReadOnlyList<SKRect> regions,
            out PageRaster cropped, out float[] within)
        {
            cropped = null;
            within = null;
            float a = placement[0], b = placement[1], c = placement[2], d = placement[3], e = placement[4], f = placement[5];
            float det = a * d - b * c;
            if (det == 0) return false;

            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            foreach (SKRect r in regions)
                foreach (var (x, y) in new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Left, r.Bottom), (r.Right, r.Bottom) })
                {
                    // User space to the image's unit square, through the placement's inverse.
                    float u = (d * (x - e) - c * (y - f)) / det;
                    float v = (a * (y - f) - b * (x - e)) / det;
                    uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u);
                    vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
                }

            // Image rows run top down: v = 1 is row 0.
            int x0 = Math.Max(0, SaturatingCast.ToInt(Math.Floor(uMin * raster.Width)));
            int x1 = Math.Min(raster.Width, SaturatingCast.ToInt(Math.Ceiling(uMax * raster.Width)));
            int y0 = Math.Max(0, SaturatingCast.ToInt(Math.Floor((1 - vMax) * raster.Height)));
            int y1 = Math.Min(raster.Height, SaturatingCast.ToInt(Math.Ceiling((1 - vMin) * raster.Height)));
            if (x1 <= x0 || y1 <= y0) return false;

            int w = x1 - x0, h = y1 - y0, n = raster.Channels;
            var samples = new byte[(long)w * h * n];
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(raster.Samples, ((y0 + row) * raster.Width + x0) * n, samples, row * w * n, w * n);
            cropped = new PageRaster(samples, w, h, n, raster.Space);
            within = new[]
            {
                (float)w / raster.Width, 0f, 0f, (float)h / raster.Height,
                (float)x0 / raster.Width, 1f - (float)y1 / raster.Height,
            };
            return true;
        }

        /// <summary>A name for the region image the page's resources do not already use.</summary>
        private static string FreeImageName(PdfDictionary resources, PdfDocument doc)
        {
            var used = new HashSet<string>(PdfPageContentAppender.ResourceNames(resources, doc));
            for (int i = 0; ; i++)
                if (!used.Contains("Flattened" + i)) return "Flattened" + i;
        }

        /// <summary>A rendered page's samples, interleaved, and the colour space they are in.</summary>
        internal sealed class PageRaster
        {
            internal PageRaster(byte[] samples, int width, int height, int channels, PdfObject space)
            {
                Samples = samples;
                Width = width;
                Height = height;
                Channels = channels;
                Space = space;
            }

            internal byte[] Samples { get; }
            internal int Width { get; }
            internal int Height { get; }
            internal int Channels { get; }
            internal PdfObject Space { get; }
        }

        /// <summary>The page rendered in the colour space the options ask for, as viewed (<c>/Rotate</c> applied).</summary>
        private static PageRaster Rasterize(byte[] original, int p, PdfDocument doc, TransparencyFlattenOptions options,
            PdfRenderOptions renderOptions, SeparationOutput plateOutput, Func<PdfObject, int> addObject)
        {
            if (options.ColorSpace == FlattenedColorSpace.Cmyk)
            {
                PdfSeparations plates = PdfSeparationRenderer.RenderPage(
                    new MemoryStream(original, writable: false), p, options.Dpi, renderOptions, plateOutput, out SpotColorantList spots);
                return RasterOfPlates(plates, spots, doc, addObject);
            }
            RenderedPage pixels = PdfPageRenderer.RenderPageToPixels(
                new MemoryStream(original, writable: false), p, options.Dpi, renderOptions);
            return RasterOf(pixels, options.ColorSpace);
        }

        /// <summary>A raster as an image XObject: its samples, deflated.</summary>
        private static PdfStream ImageStream(PageRaster raster)
        {
            byte[] encoded = FlateEncodeFilter.Encode(raster.Samples);
            var image = new PdfStream { RawData = encoded };
            image.Set(PdfName.Type, PdfName.XObject);
            image.Set(PdfName.Subtype, PdfName.Image);
            image.Set(PdfName.Width, new PdfNumber(raster.Width));
            image.Set(PdfName.Height, new PdfNumber(raster.Height));
            image.Set(PdfName.ColorSpace, raster.Space);
            image.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            image.Set(PdfName.Filter, PdfName.FlateDecode);
            image.Set(PdfName.Length, new PdfNumber(encoded.Length));
            return image;
        }

        /// <summary>The rendered page's colour samples. The render is on an opaque background, so alpha is dropped.</summary>
        private static PageRaster RasterOf(RenderedPage pixels, FlattenedColorSpace space)
        {
            int components = space == FlattenedColorSpace.Gray ? 1 : 3;
            var samples = new byte[(long)pixels.Width * pixels.Height * components];
            int o = 0;
            for (int y = 0; y < pixels.Height; y++)
            {
                int row = y * pixels.Stride;
                for (int x = 0; x < pixels.Width; x++)
                {
                    int i = row + x * 4;
                    byte r = pixels.Pixels[i], g = pixels.Pixels[i + 1], b = pixels.Pixels[i + 2];
                    if (components == 1)
                        samples[o++] = (byte)((r * PdfColorSpaceNames.LumaRedPermille + g * PdfColorSpaceNames.LumaGreenPermille
                                               + b * PdfColorSpaceNames.LumaBluePermille) / PdfColorSpaceNames.LumaScale);
                    else
                    {
                        samples[o++] = r; samples[o++] = g; samples[o++] = b;
                    }
                }
            }
            return new PageRaster(samples, pixels.Width, pixels.Height, components,
                components == 1 ? PdfName.DeviceGray : PdfName.DeviceRGB);
        }

        /// <summary>
        /// The page's plates as one raster: <c>/DeviceCMYK</c> from the four process plates when the page
        /// paints with no spot ink, else <c>/DeviceN</c> — the process inks, then each spot — whose
        /// alternate is <c>/DeviceCMYK</c> through a calculator function (<see cref="SpotTintTransform"/>)
        /// that adds each spot's ink, as CMYK, into the process inks. <paramref name="addObject"/> gives
        /// the function stream its object number.
        /// </summary>
        private static PageRaster RasterOfPlates(PdfSeparations plates, SpotColorantList spots, PdfDocument doc,
            Func<PdfObject, int> addObject)
        {
            int channels = plates.Plates.Count;
            long pixels = (long)plates.Width * plates.Height;
            var samples = new byte[pixels * channels];
            for (int c = 0; c < channels; c++)
            {
                byte[] tints = plates.Plates[c].Tints;
                for (long i = 0; i < pixels; i++) samples[i * channels + c] = tints[i];
            }

            PdfObject space;
            if (channels == 4) space = PdfName.DeviceCMYK;
            else
            {
                var names = new PdfArray();
                var equivalents = new List<float[]>();
                foreach (var plate in plates.Plates)
                {
                    names.Add(new PdfName(plate.Colorant));
                    if (!SeparationPlate.IsProcess(plate.Colorant))
                        equivalents.Add(SpotAsCmyk(plate.Colorant, spots?.SpaceOf(plate.Colorant), doc));
                }
                var deviceN = new PdfArray();
                deviceN.Add(new PdfName(PdfColorSpaceNames.DeviceN));
                deviceN.Add(names);
                deviceN.Add(PdfName.DeviceCMYK);
                deviceN.Add(new PdfReference(addObject(SpotTintTransform(equivalents)), 0));
                space = deviceN;
            }
            return new PageRaster(samples, plates.Width, plates.Height, channels, space);
        }

        /// <summary>
        /// A spot ink at full tint, as CMYK: its own colour space's tint transform evaluated with the ink
        /// at 1 (and any other colorant of a DeviceN at 0), read directly when the alternate is
        /// <c>/DeviceCMYK</c>, and otherwise taken through its sRGB colour by the exact inverse of the
        /// CMYK conversion — the inverse the plates themselves use for RGB. A spot whose space the render
        /// did not record reads as no ink.
        /// </summary>
        internal static float[] SpotAsCmyk(string colorant, PdfArray space, PdfDocument doc)
        {
            if (space == null || space.Count < 4) return new float[4];
            var family = (doc.ResolveObject(space[0]) as PdfName)?.Value;
            float[] inputs;
            if (family == PdfColorSpaceNames.Separation) inputs = new[] { 1f };
            else
            {
                var names = SeparationPlate.ColorantNames(space);
                inputs = new float[names.Count];
                int at = names.IndexOf(colorant);
                if (at >= 0) inputs[at] = 1f;
            }

            float[] alt = Core.Functions.PdfFunctionEvaluator.EvaluateEntry(space[3], inputs, doc.ResolveObject);
            if (alt == null || alt.Length == 0) return new float[4];
            PdfObject alternate = doc.ResolveObject(space[2]);
            string altName = (alternate as PdfName)?.Value;
            if (altName == PdfColorSpaceNames.DeviceCMYK || (altName == null && alt.Length == 4))
                return new[] { Clamp(alt[0]), Clamp(alt.Length > 1 ? alt[1] : 0), Clamp(alt.Length > 2 ? alt[2] : 0), Clamp(alt.Length > 3 ? alt[3] : 0) };

            string device = altName ?? (alt.Length >= 3 ? PdfColorSpaceNames.DeviceRGB : PdfColorSpaceNames.DeviceGray);
            SkiaSharp.SKColor rgb = PdfColor.ComponentsToColor(alt, device);
            OverprintCompositor.RgbToCmykExactInverse(rgb, out float c, out float m, out float y, out float k);
            return new[] { c, m, y, k };
        }

        private static float Clamp(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>
        /// A Type 4 (PostScript calculator) function from <c>c m y k s1 … sn</c> to CMYK: each process
        /// ink plus every spot's tint times that spot's ink in it — the spots overprinting the process
        /// inks, as on press — clamped at 1 by the function's <c>/Range</c>, which §7.10.1 clips every
        /// output to, so the program itself does not clamp.
        /// </summary>
        internal static PdfStream SpotTintTransform(IReadOnlyList<float[]> spotsAsCmyk)
        {
            int n = spotsAsCmyk.Count, inputs = 4 + n;
            var ps = new StringBuilder("{");
            for (int i = 0; i < 4; i++)
            {
                // The stack holds the inputs and the i outputs already made: input i is inputs - 1 deep.
                ps.Append(' ').Append(inputs - 1).Append(" index");
                for (int j = 0; j < n; j++)
                {
                    // Above spot j: the inputs after it, the i outputs, and the partial sum.
                    int depth = (inputs - 1 - (4 + j)) + i + 1;
                    ps.Append(' ').Append(depth).Append(" index ")
                      .Append(spotsAsCmyk[j][i].ToString("0.####", CultureInfo.InvariantCulture)).Append(" mul add");
                }
            }
            // Bring the four outputs under the inputs, then drop the inputs.
            ps.Append(' ').Append(inputs + 4).Append(" 4 roll");
            for (int k = 0; k < inputs; k++) ps.Append(" pop");
            ps.Append(" }");

            byte[] program = Encoding.ASCII.GetBytes(ps.ToString());
            var function = new PdfStream { RawData = program };
            function.Set(PdfName.FunctionType, new PdfNumber(4));
            var domain = new PdfArray();
            for (int k = 0; k < inputs; k++) { domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1)); }
            function.Set(PdfName.Domain, domain);
            var range = new PdfArray();
            for (int k = 0; k < 4; k++) { range.Add(new PdfNumber(0)); range.Add(new PdfNumber(1)); }
            function.Set(PdfName.Range, range);
            function.Set(PdfName.Length, new PdfNumber(program.Length));
            return function;
        }

        /// <summary>The words of each flattened page, read from the original, for the invisible layer.</summary>
        private static Dictionary<int, IReadOnlyList<PdfOcrWord>> CollectWords(byte[] original, SortedSet<int> pages)
        {
            var byPage = new Dictionary<int, IReadOnlyList<PdfOcrWord>>();
            foreach (int p in pages)
            {
                PdfPageTextLayout layout = PdfStructuredTextExtractor.Extract(new MemoryStream(original, writable: false), p);
                var words = new List<PdfOcrWord>();
                foreach (var line in layout.Lines)
                    foreach (var word in line.Words)
                        if (!string.IsNullOrWhiteSpace(word.Text))
                            words.Add(new PdfOcrWord(word.Text, word.X, word.Y, word.Width, word.Height));
                if (words.Count > 0) byPage[p] = words;
            }
            return byPage;
        }

        /// <summary>
        /// Per page kept as vector, the words the original shows that the flattened page no longer does
        /// — text inside a form that was dropped — each matched by its text and a centre within half
        /// its height, once.
        /// </summary>
        private static Dictionary<int, IReadOnlyList<PdfOcrWord>> WordsLost(byte[] original, byte[] flattened, SortedSet<int> pages)
        {
            var lost = new Dictionary<int, IReadOnlyList<PdfOcrWord>>();
            if (pages.Count == 0) return lost;
            var before = CollectWords(original, pages);
            var after = CollectWords(flattened, pages);
            foreach (var kvp in before)
            {
                var unmatched = after.TryGetValue(kvp.Key, out var shown) ? new List<PdfOcrWord>(shown) : new List<PdfOcrWord>();
                var missing = new List<PdfOcrWord>();
                foreach (PdfOcrWord word in kvp.Value)
                {
                    float tolerance = word.Height / 2;
                    int match = unmatched.FindIndex(o => o.Text == word.Text
                        && Math.Abs(o.X + o.Width / 2 - (word.X + word.Width / 2)) <= tolerance
                        && Math.Abs(o.Y + o.Height / 2 - (word.Y + word.Height / 2)) <= tolerance);
                    if (match >= 0) unmatched.RemoveAt(match);
                    else missing.Add(word);
                }
                if (missing.Count > 0) lost[kvp.Key] = missing;
            }
            return lost;
        }

        /// <summary>The whole document, from its start when the stream can seek, as every reader here takes it.</summary>
        private static byte[] ReadAll(Stream input)
        {
            if (input.CanSeek) input.Position = 0;
            using var copy = new MemoryStream();
            input.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
