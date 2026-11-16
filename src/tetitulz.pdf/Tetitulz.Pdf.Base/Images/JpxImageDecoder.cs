// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// What <see cref="JpxImageDecoder.DecodeToBitmap"/> did beside the bitmap it may have returned:
    /// which decoder answered, whether the caller still applies <c>/Decode</c>, and — when the
    /// image dictionary's own colour space governs — the decoded samples for the caller's raw
    /// sample path, which is the one place an Indexed palette, a tint transform, <c>/Decode</c> and
    /// a colour key are applied to samples.
    /// </summary>
    internal readonly struct JpxDecodeOutcome
    {
        internal JpxDecodeOutcome(bool fromInHouseDecoder, bool decodeArrayApplies, byte[] rawSamples, PdfDictionary rawDictionary,
            byte[] rawOpacity = null)
        {
            FromInHouseDecoder = fromInHouseDecoder;
            DecodeArrayApplies = decodeArrayApplies;
            RawSamples = rawSamples;
            RawDictionary = rawDictionary;
            RawOpacity = rawOpacity;
        }

        /// <summary>
        /// With <see cref="RawSamples"/>: the opacity channel <c>/SMaskInData</c> named, one
        /// <b>straight</b> byte per pixel of the samples' geometry (a premultiplied channel has
        /// been divided out of the samples), for the caller to composite after it has painted
        /// the samples through the dictionary's colour space — the raw path carries no alpha of
        /// its own. Null when the image has none.
        /// </summary>
        internal byte[] RawOpacity { get; }

        /// <summary>True when the in-house decoder produced the result; false when the plugin did, or nothing did.</summary>
        internal bool FromInHouseDecoder { get; }

        /// <summary>
        /// Whether the caller applies the image dictionary's <c>/Decode</c> to the returned bitmap:
        /// ISO 32000-2 §7.4.9's rule (only with <c>/ColorSpace</c> or <c>/ImageMask</c>) for the
        /// in-house decoder, and never after a CMYK conversion, whose array described samples that
        /// no longer exist.
        /// </summary>
        internal bool DecodeArrayApplies { get; }

        /// <summary>
        /// When no bitmap is returned but the samples are the caller's to read: 8 bits per
        /// component, interleaved, for a colour space the dictionary defines; packed 1-bit rows for
        /// a stencil mask. Null otherwise.
        /// </summary>
        internal byte[] RawSamples { get; }

        /// <summary>
        /// The dictionary describing <see cref="RawSamples"/> — the image's, with
        /// <c>/BitsPerComponent 8</c> where the samples were narrowed, and with <c>/Width</c> and
        /// <c>/Height</c> replaced when the decode was reduced. <b>It is the one description of the
        /// samples</b>: the caller takes their geometry from it, as it takes their depth.
        /// </summary>
        internal PdfDictionary RawDictionary { get; }
    }

    /// <summary>
    /// The one place JPEG 2000 (<c>JPXDecode</c>) image data is turned into pixels, for both
    /// renderers and every operation that wants an image's samples.
    ///
    /// <para><b>The order is fixed here and nowhere else: the in-house decoder answers first, and a
    /// registered <see cref="IImageDecodePlugin"/> only when it declines.</b> <c>Core</c> decodes
    /// JPEG 2000 itself (<c>Core.Imaging.Jpx.JpxDecoder</c>), with the JP2 colour
    /// rules of ISO 32000-2 §7.4.9 applied by <c>JpxPdfImage</c>; <c>Core.IO.Filters.JpxDecodeFilter</c>
    /// still passes the codestream through, because a filter has no image dictionary and the
    /// colour rules need one. A host's own <see cref="IImageDecodePlugin"/> (the library ships none)
    /// is the second opinion; <c>PluginImageDecoder</c>'s generic
    /// dispatch skips <c>JPXDecode</c> so the plugin is never asked ahead of the decoder, or twice.</para>
    ///
    /// <para>Why it declined is readable: <see cref="LastDecodeError"/> on the calling thread, and a
    /// <see cref="RenderDiagnostics"/> report per image. A decode that tolerated an irregularity — a
    /// tile that stopped early, an unreadable header box — reports it as
    /// <see cref="RenderDiagnosticKind.JpxDecodeDeviation"/> rather than rendering it silently.</para>
    /// </summary>
    internal static class JpxImageDecoder
    {
        /// <summary>The PDF filter name, spelled as the spec spells it.</summary>
        internal const string FilterName = PdfFilterNames.JpxDecode;

        [ThreadStatic] private static string _lastDecodeError;
        [ThreadStatic] private static int _lastDecodeThreads;

        /// <summary>
        /// The degree the in-house decoder ran the last <c>JPXDecode</c> image at on <b>this
        /// thread</b> — the render's thread budget forwarded through <c>maxThreads</c>, or the
        /// automatic degree — or 0 when it declined or nothing was attempted. Reset at the start of
        /// every decode, on every path; the evidence that a forwarded budget reached the decoder.
        /// </summary>
        internal static int LastDecodeThreads => _lastDecodeThreads;

        /// <summary>
        /// Why the in-house decoder declined the last <c>JPXDecode</c> image on <b>this thread</b>,
        /// or <c>null</c> when it decoded it (whatever the plugin then did) or nothing was
        /// attempted. Reset at the start of every decode, on every path.
        /// </summary>
        /// <remarks>
        /// A JPX decode that declines is indistinguishable in the rendered page from one that was
        /// never attempted — the image is simply absent, or the plugin's answer stands in — so the
        /// refusal has to be readable somewhere. <c>[ThreadStatic]</c> per CLAUDE.md's Diagnostics
        /// Pattern; read it on the thread that rendered.
        /// </remarks>
        internal static string LastDecodeError => _lastDecodeError;

        /// <summary>
        /// Decodes JPEG 2000 bytes to an <see cref="SKBitmap"/>: the in-house decoder first, the
        /// registered plugin when it declines. Returns null when neither produced a usable bitmap
        /// — the caller then continues with its own fallbacks — and also when the samples are the
        /// caller's to read (<see cref="JpxDecodeOutcome.RawSamples"/>).
        /// </summary>
        /// <remarks>
        /// <para><c>graphicsStateIntent</c> is the rendering intent at the <c>Do</c>, so a JPX CMYK image
        /// — whether its profile is the dictionary's or the JP2 file's — is colour-managed under
        /// the same intent as any other image on the page. Prose rather than a <c>&lt;param&gt;</c>
        /// tag because tagging one parameter obliges every parameter.</para>
        /// <para><c>maxThreads</c> is the render's thread budget for the decode — 1 for the calling
        /// thread (a batch render already runs a worker per page, and a page render that asked for
        /// one thread means one), N for at most N, 0 for the decoder's automatic degree. Without it
        /// every page of a batch started its own automatic degree, N pages times up to sixteen
        /// threads.</para>
        /// <para><c>targetWidth</c> and <c>targetHeight</c> are the device-pixel extent the image
        /// will be drawn into, or 0 for "decode at native size". With a target, the codestream is
        /// decoded as many resolution levels short as <see cref="ImageDownsampling.HalvingsFor"/>
        /// allows — a JPEG 2000 image at r levels short is a decode of about 4^r less work, tier 1
        /// included, because the finer sub-bands' packets are skipped — and the reduction is
        /// reported through <see cref="RenderDiagnosticKind.ImageDownsampled"/>, since a reduced
        /// decode followed by the resampler is not bit-identical to a full one. The in-house
        /// decoder alone reduces; the plugin decodes at native size. A stencil mask is never
        /// reduced: its bits are read by the dictionary's geometry, and thinning a mask by resolution
        /// level is not the nearest-neighbour rule the raw path applies to a stencil.</para>
        /// </remarks>
        internal static SKBitmap DecodeToBitmap(byte[] data, PdfDictionary imageDict, PdfDocument doc,
            SKAlphaType alphaTypeWithAlpha, out JpxDecodeOutcome outcome,
            IccRenderingIntent graphicsStateIntent = IccRenderingIntents.Default,
            int targetWidth = 0, int targetHeight = 0, int maxThreads = 0)
        {
            _lastDecodeError = null;
            _lastDecodeThreads = 0;
            outcome = default;
            if (data == null || data.Length < 4) return null;

            IImageDecodePlugin plugin = PdfPluginHost.Instance.FindDecodePlugin(FilterName);

            Jp2PdfImageFacts facts = FactsOf(imageDict, doc);
            int reduction = ReductionFor(data, facts, targetWidth, targetHeight);
            string reason;
            if (JpxPdfImage.TryDecode(data, facts, IccProfileUsable, reduction, out JpxPdfImage image, out reason, maxThreads))
            {
                _lastDecodeThreads = image.Threads;
                if (image.Deviations.Count > 0)
                    RenderDiagnostics.Report(RenderDiagnosticKind.JpxDecodeDeviation,
                        "JPEG 2000 image decoded with tolerated irregularities: " + string.Join("; ", image.Deviations));

                SKBitmap bitmap = BuildBitmap(image, imageDict, doc, alphaTypeWithAlpha, graphicsStateIntent, out outcome);
                if (bitmap != null || outcome.RawSamples != null)
                {
                    // Reported once the reduced decode is what will be drawn: a build that declines
                    // (a dictionary geometry unlike the codestream's) draws nothing, and a report of
                    // a reduction that reached no pixel misled.
                    if (image.Reduction > 0)
                        RenderDiagnostics.Report(RenderDiagnosticKind.ImageDownsampled,
                            $"JPEG 2000 image decoded at {image.Width}x{image.Height} instead of {image.NativeWidth}x{image.NativeHeight} " +
                            $"for a {targetWidth}x{targetHeight} device target ({image.Reduction} resolution level(s) short).", null);
                    return bitmap;
                }
                reason = _lastDecodeError;
            }
            else
            {
                _lastDecodeError = reason;
            }

            RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                $"JPEG 2000: the decoder declined this codestream ({data.Length} byte(s)): {reason}" +
                (plugin != null
                    ? $"; falling back to IImageDecodePlugin '{plugin.Name}'."
                    : "; no IImageDecodePlugin is registered for JPXDecode."));
            if (plugin == null) return null;
            return DecodeThroughPlugin(plugin, data, imageDict, doc, alphaTypeWithAlpha, graphicsStateIntent, out outcome);
        }

        /// <summary>
        /// The in-house decoder alone, for an operation that wants the samples rather than a bitmap
        /// (a thumbnail reader, a colour conversion): the codestream decoded under the image
        /// dictionary's facts, with this assembly's say on ICC profiles. False, with the reason,
        /// when it declines; the plugin is not consulted, because its 8-bit interleaved output says
        /// nothing about which channels are colour.
        /// </summary>
        internal static bool TryDecodeSamples(byte[] data, PdfDictionary imageDict, PdfDocument doc,
            out JpxPdfImage image, out string reason)
        {
            _lastDecodeError = null;
            _lastDecodeThreads = 0;
            image = null;
            reason = null;
            if (data == null || data.Length < 4)
            {
                reason = "the image stream carries no JPEG 2000 data";
                _lastDecodeError = reason;
                return false;
            }
            if (JpxPdfImage.TryDecode(data, FactsOf(imageDict, doc), IccProfileUsable, 0, out image, out reason))
            {
                _lastDecodeThreads = image.Threads;
                return true;
            }
            _lastDecodeError = reason;
            return false;
        }

        /// <summary>
        /// The resolution levels to decode short of the codestream's size for a device target (see
        /// <see cref="DecodeToBitmap"/>'s remarks): <see cref="ImageDownsampling.HalvingsFor"/> over
        /// the SIZ's image area — 0 with no target, for a stencil mask, or when the header cannot be
        /// read (the decoder will say why).
        /// </summary>
        private static int ReductionFor(byte[] data, Jp2PdfImageFacts facts, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0 || facts.ImageMask) return 0;
            if (!JpxHeader.TryReadImageSize(data, out long width, out long height, out _)) return 0;
            return ImageDownsampling.HalvingsFor(width, height, targetWidth, targetHeight);
        }

        /// <summary>
        /// The PDF colour-space family a <c>JPXDecode</c> image with no <c>/ColorSpace</c> is read
        /// in — <c>DeviceGray</c>, <c>DeviceRGB</c>, <c>DeviceCMYK</c>, <c>ICCBased</c> or
        /// <c>Lab</c> — from the JP2 header alone (ISO 32000-2 §7.4.9's precedence, a raw
        /// codestream's device fallback), without decoding a sample. Null when the codestream's
        /// components cannot be read or no colour space fits them.
        /// </summary>
        internal static string ColourSpaceFamilyOf(byte[] data)
        {
            if (!JpxHeader.TryReadComponentPrecision(data, out int[] precision, out _)) return null;
            var components = new JpxComponentSize[precision.Length];
            for (int c = 0; c < components.Length; c++) components[c] = new JpxComponentSize(precision[c], false, 1, 1);

            Jp2ColourDescription header = JpxHeader.IsJp2Container(data)
                && Jp2ColourDescription.TryRead(data, out Jp2ColourDescription read, out _)
                ? read
                : Jp2ColourDescription.ForRawCodestream();
            if (!header.TryPlanForPdf(components, new Jp2PdfImageFacts(0, false, 0, false), IccProfileUsable, out Jp2PdfChannelPlan plan, out _))
                return null;

            switch (plan.ColourSpace)
            {
                case Jp2ColourSpaceKind.Greyscale:
                case Jp2ColourSpaceKind.DeviceGray: return PdfColorSpaceNames.DeviceGray;
                case Jp2ColourSpaceKind.Cmyk:
                case Jp2ColourSpaceKind.DeviceCmyk: return PdfColorSpaceNames.DeviceCMYK;
                case Jp2ColourSpaceKind.CieLab: return PdfColorSpaceNames.Lab;
                case Jp2ColourSpaceKind.IccProfile: return PdfColorSpaceNames.ICCBased;
                default: return PdfColorSpaceNames.DeviceRGB;
            }
        }

        /// <summary>
        /// What the image dictionary says, as ISO 32000-2 §7.4.9 reads it: the <c>/ColorSpace</c>'s
        /// component count (0 when absent — the JP2 file's own specification then governs) and
        /// whether it is Indexed, <c>/SMaskInData</c>, and <c>/ImageMask</c>. Every entry read
        /// through <see cref="PdfResolve"/>.
        /// </summary>
        internal static Jp2PdfImageFacts FactsOf(PdfDictionary imageDict, PdfDocument doc)
        {
            if (imageDict == null) return new Jp2PdfImageFacts(0, false, 0, false);
            bool imageMask = ImageStencil.IsStencilMask(imageDict, doc);
            int components = 0;
            bool indexed = false;
            PdfObject csEntry = PdfResolve.Value(doc, imageDict[PdfName.ColorSpace] ?? imageDict[PdfName.CS]);
            if (csEntry != null && !imageMask)
            {
                components = ImageSamples.ComponentCount(imageDict, doc);
                indexed = ContentStreamInterpreter.IsIndexedFamily(PdfColor.ResolveSpace(csEntry, doc).Family);
            }
            return new Jp2PdfImageFacts(components, indexed, (int)ImageSampleLayout.ReadSMaskInData(imageDict, doc), imageMask);
        }

        /// <summary>
        /// The <c>iccProfileUsable</c> delegate of the channel plan: a <c>colr</c> box's profile is
        /// usable when this assembly's ICC reader accepts its header (<see cref="IccProfile.TryReadHeader"/>)
        /// <b>and</b> its data colour space is one the transform can take — GRAY, RGB or CMYK,
        /// the three <see cref="IccProfile"/> parses (a 2CLR, CMY, HSV or XYZ profile has no
        /// transform here) — so the plan falls past any other to the next lower colour
        /// specification, as ISO 32000-2 §7.4.9 says.
        /// </summary>
        private static bool IccProfileUsable(byte[] profile) =>
            IccProfile.TryReadHeader(profile, out IccProfile.IccHeader header, out _)
            && (header.DataColorSpace == "GRAY" || header.DataColorSpace == "RGB " || header.DataColorSpace == "CMYK");

        /// <summary>
        /// Turns the decoded image into a bitmap, or hands its samples to the caller's raw path.
        /// A colour space the dictionary defines goes to the raw path (which reads every space,
        /// <c>/Decode</c> and the colour key from the dictionary): an Indexed one's samples are
        /// the indices verbatim (<see cref="Jp2PdfChannelPlan.IndicesVerbatim"/>), and an opacity
        /// channel riding along — which the raw path cannot carry — is handed over beside them
        /// (<see cref="JpxDecodeOutcome.RawOpacity"/>, straight) for the caller to composite after
        /// the paint, one rule for <c>/Indexed</c>, <c>/Separation</c>, <c>/DeviceN</c>, <c>/Lab</c>
        /// and the rest. A 1-, 3- or 4-component device-arity space with opacity is built here the
        /// way the plugin path builds it, its <c>/Decode</c> applied to the samples first where the
        /// build would consume them (CMYK). A stencil mask's bits go to the raw path packed.
        /// </summary>
        private static SKBitmap BuildBitmap(JpxPdfImage image, PdfDictionary imageDict, PdfDocument doc,
            SKAlphaType alphaTypeWithAlpha, IccRenderingIntent graphicsStateIntent, out JpxDecodeOutcome outcome)
        {
            outcome = default;
            int w = image.Width, h = image.Height;
            // The raw path reads the dictionary's geometry; the codestream's is authoritative for a
            // JPX image, so the two must agree before the samples are handed over — the codestream's
            // own size, not a reduced decode's: the dictionary handed over then carries the
            // reduced size (WithDecodedGeometry), because the raw path reads /Width and /Height.
            bool sizeAsDeclared = imageDict != null
                && PdfResolve.Int(doc, imageDict, PdfName.Width, PdfName.W, image.NativeWidth) == image.NativeWidth
                && PdfResolve.Int(doc, imageDict, PdfName.Height, PdfName.H, image.NativeHeight) == image.NativeHeight;

            switch (image.ColourModel)
            {
                case JpxColourModel.Stencil:
                    if (!sizeAsDeclared)
                    {
                        _lastDecodeError = $"the stencil codestream is {image.NativeWidth}x{image.NativeHeight} but the image dictionary declares another size; a stencil is read by the dictionary's geometry";
                        return null;
                    }
                    outcome = new JpxDecodeOutcome(true, true, PackBits(image.Colour, w, h), WithDecodedGeometry(imageDict, image));
                    return null;

                case JpxColourModel.ImageDictionary:
                    if (image.Opacity == null || !BuildsDirectly(imageDict, doc, image.ColourComponents))
                    {
                        if (!sizeAsDeclared)
                        {
                            _lastDecodeError = $"the codestream is {image.NativeWidth}x{image.NativeHeight} but the image dictionary declares another size; with a /ColorSpace the samples are read by the dictionary's geometry";
                            return null;
                        }
                        byte[] opacity = image.Opacity;
                        if (opacity != null && image.OpacityIsPremultiplied)
                        {
                            // The raw path paints straight samples. A premultiplied index is not a
                            // thing: an Indexed image's samples are handed over as they are.
                            if (image.Plan.IndicesVerbatim)
                                RenderDiagnostics.Report(RenderDiagnosticKind.JpxDecodeDeviation,
                                    "JPEG 2000: /SMaskInData 2 says the samples are premultiplied by the opacity channel, but they are the /Indexed colour space's palette indices, which cannot be; they are read as they are.");
                            else
                                ImageSampleLayout.UnpremultiplySamples(image.Colour, image.ColourComponents, opacity);
                        }
                        outcome = new JpxDecodeOutcome(true, true, image.Colour, WithDecodedGeometry(WithEightBitSamples(imageDict), image), opacity);
                        return null;
                    }
                    // Colour plus opacity in a 1-, 3- or 4-component space: built here, converted as
                    // the plugin path converts the same samples — the dictionary's ICC profile for CMYK.
                    // §7.4.9: /Decode applies when /ColorSpace is present. Build converts four
                    // components to RGB before the caller could apply it, so a CMYK space's array
                    // is applied to the samples here, first; the caller then applies none.
                    byte[] dictionarySamples = image.Colour;
                    bool decodeConsumed = false;
                    if (image.ColourComponents == PdfColorSpaceNames.CmykComponents && image.DecodeArrayApplies
                        && TryReadDecode(imageDict, doc, PdfColorSpaceNames.CmykComponents, out float[] cmykDecode))
                    {
                        dictionarySamples = WithDecodeApplied(image.Colour, PdfColorSpaceNames.CmykComponents, cmykDecode);
                        decodeConsumed = true;
                    }
                    var dictionaryCmyk = image.ColourComponents == PdfColorSpaceNames.CmykComponents
                        ? IccColorTransform.IccImageConverter.TryCreate(doc, imageDict, PdfColorSpaceNames.CmykComponents, graphicsStateIntent)
                        : null;
                    SKBitmap dictionaryBitmap = Build(image, dictionarySamples, image.ColourComponents, dictionaryCmyk, alphaTypeWithAlpha, out outcome);
                    if (dictionaryBitmap != null && decodeConsumed)
                        outcome = new JpxDecodeOutcome(true, false, null, null);
                    return dictionaryBitmap;

                case JpxColourModel.Lab:
                    return Build(image, LabToRgb(image), PdfColorSpaceNames.RgbComponents, null, alphaTypeWithAlpha, out outcome);

                default:
                {
                    // Grey, RGB or CMYK as the file defines it. A CMYK profile from the colr box
                    // converts per sample, where the four components still exist; a grey or RGB one
                    // transforms the built bitmap, as the dictionary's /ICCBased does after a decode.
                    IccColorTransform.IccImageConverter fileCmyk = null;
                    if (image.ColourModel == JpxColourModel.Cmyk && image.IccProfile != null)
                        fileCmyk = IccColorTransform.IccImageConverter.TryCreate(image.IccProfile, PdfColorSpaceNames.CmykComponents,
                            IccRenderingIntents.ForImage(doc, imageDict, graphicsStateIntent));
                    SKBitmap bitmap = Build(image, image.Colour, image.ColourComponents, fileCmyk, alphaTypeWithAlpha, out outcome);
                    if (bitmap != null && image.IccProfile != null && image.ColourModel != JpxColourModel.Cmyk)
                        IccRenderingIntents.TransformImage(image.IccProfile, bitmap,
                            IccRenderingIntents.ForImage(doc, imageDict, graphicsStateIntent));
                    return bitmap;
                }
            }
        }

        /// <summary>Interleaves the opacity channel behind the colour samples and builds through <see cref="ImageSampleLayout.BuildBitmap"/>, the one place straight and premultiplied alpha are stored.</summary>
        private static SKBitmap Build(JpxPdfImage image, byte[] colour, int components,
            IccColorTransform.IccImageConverter cmykIcc, SKAlphaType alphaTypeWithAlpha, out JpxDecodeOutcome outcome)
        {
            outcome = default;
            byte[] samples = colour;
            int total = components;
            ImageAlphaMode alpha = ImageAlphaMode.None;
            if (image.Opacity != null)
            {
                samples = WithOpacity(colour, components, image.Opacity);
                total = components + 1;
                alpha = image.OpacityIsPremultiplied ? ImageAlphaMode.Premultiplied : ImageAlphaMode.Straight;
            }
            var layout = ImageSampleLayout.ForDecodedChannels(components, total, alpha, cmykIcc);
            SKBitmap bitmap = layout.BuildBitmap(samples, image.Width, image.Height, alphaTypeWithAlpha);
            if (bitmap == null)
            {
                _lastDecodeError = $"the decoded samples ({image.Width}x{image.Height}, {total} channel(s)) could not be turned into a bitmap";
                return null;
            }
            // A CMYK image's /Decode described the four samples, which the conversion consumed.
            outcome = new JpxDecodeOutcome(true, image.DecodeArrayApplies && components != PdfColorSpaceNames.CmykComponents, null, null);
            return bitmap;
        }

        private static SKBitmap DecodeThroughPlugin(IImageDecodePlugin plugin, byte[] data, PdfDictionary imageDict, PdfDocument doc,
            SKAlphaType alphaTypeWithAlpha, IccRenderingIntent graphicsStateIntent, out JpxDecodeOutcome outcome)
        {
            outcome = default;
            int width, height, components;
            byte[] pixels;
            try
            {
                // Scalars resolved for the plugin, which has no document to resolve them with.
                PdfDictionary pluginDict = imageDict == null ? null : PluginImageDecoder.WithResolvedScalars(imageDict, doc);
                if (!plugin.TryDecode(FilterName, data, pluginDict, out width, out height, out components, out pixels)
                    || pixels == null)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                        $"IImageDecodePlugin '{plugin.Name}' declined the JPEG 2000 codestream as well; the image was not drawn.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                    $"IImageDecodePlugin '{plugin.Name}' threw decoding {FilterName}; the image was not drawn.", ex);
                return null;
            }

            var resolved = ImageSampleLayout.Resolve(imageDict, doc, components, data, graphicsStateIntent);
            SKBitmap bitmap = resolved.BuildBitmap(pixels, width, height, alphaTypeWithAlpha);
            if (bitmap == null)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    $"IImageDecodePlugin '{plugin.Name}' returned unusable samples ({width}x{height}, {components} channel(s)); the image was not drawn.");
                return null;
            }
            // The plugin contract hands back one byte per sample: a deeper codestream was narrowed
            // on the way, and an Indexed image's indices with it — said here, since the layout
            // cannot see the depth.
            if (JpxHeader.TryReadComponentPrecision(data, out int[] precision, out _))
            {
                int deepest = 0;
                foreach (int p in precision) if (p > deepest) deepest = p;
                if (deepest != 8)
                    RenderDiagnostics.Report(RenderDiagnosticKind.JpxDecodeDeviation,
                        $"IImageDecodePlugin '{plugin.Name}' returned 8-bit samples for a codestream of {deepest}-bit components; the samples were narrowed by the plugin.");
            }
            // The plugin path's rule, unchanged: /Decode applies unless the samples were CMYK.
            outcome = new JpxDecodeOutcome(false, !resolved.IsCmyk, null, null);
            return bitmap;
        }

        /// <summary>
        /// Whether the dictionary's colour space is one <see cref="Build"/> paints as it is — a
        /// device space, or an ICCBased one of device arity (the caller's ICC transform follows)
        /// — rather than one whose samples mean something else: an Indexed palette index, a
        /// Separation tint, a Lab or CalRGB triple, which the raw path alone reads. Decided by
        /// the family, not the component count: Indexed and Separation are one component too.
        /// </summary>
        private static bool BuildsDirectly(PdfDictionary imageDict, PdfDocument doc, int components)
        {
            if (!IsDeviceArity(components) || imageDict == null) return false;
            PdfObject csEntry = PdfResolve.Value(doc, imageDict[PdfName.ColorSpace] ?? imageDict[PdfName.CS]);
            string family = PdfColor.ResolveSpace(csEntry, doc).Family;
            return family == PdfColorSpaceNames.DeviceGray || family == PdfColorSpaceNames.DeviceRGB
                || family == PdfColorSpaceNames.DeviceCMYK || family == PdfColorSpaceNames.ICCBased;
        }

        /// <summary>
        /// The dictionary's <c>/Decode</c> for <paramref name="components"/> samples when it is
        /// there, resolved, complete and not the identity; false otherwise. Read here rather than
        /// through the interpreter's reader because this decoder has no interpreter.
        /// </summary>
        private static bool TryReadDecode(PdfDictionary imageDict, PdfDocument doc, int components, out float[] decode)
        {
            decode = null;
            if (imageDict == null) return false;
            if (PdfResolve.Value(doc, imageDict[PdfName.Decode] ?? imageDict[PdfName.D]) is not PdfArray arr || arr.Count < components * 2) return false;
            var values = new float[components * 2];
            bool identity = true;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = PdfResolve.ArrayFloat(doc, arr, i);
                if (values[i] != (i % 2 == 0 ? 0f : 1f)) identity = false;
            }
            if (identity) return false;
            decode = values;
            return true;
        }

        /// <summary>The samples with <paramref name="decode"/> applied per component (§8.9.5.2), through the one decode rule and the one quantisation rule.</summary>
        private static byte[] WithDecodeApplied(byte[] samples, int components, float[] decode)
        {
            var mapped = new byte[samples.Length];
            for (long i = 0; i < samples.Length; i++)
            {
                int c = (int)(i % components);
                mapped[i] = PdfColor.Channel(PdfColor.ApplyDecode(samples[i] / 255f, decode, c));
            }
            return mapped;
        }

        /// <summary>T.801 M.11.7.4.1's L*, a*, b* from the 8-bit samples (see <see cref="JpxPdfImage.LabRanges"/>), through the one Lab conversion.</summary>
        private static byte[] LabToRgb(JpxPdfImage image)
        {
            long area = (long)image.Width * image.Height;
            var rgb = new byte[area * PdfColorSpaceNames.RgbComponents];
            double[] range = image.LabRanges, offset = image.LabOffsets;
            byte[] lab = image.Colour;
            for (long i = 0; i < area; i++)
            {
                long at = i * 3;
                float l = (float)((lab[at] - offset[0]) * range[0] / 255.0);
                float a = (float)((lab[at + 1] - offset[1]) * range[1] / 255.0);
                float b = (float)((lab[at + 2] - offset[2]) * range[2] / 255.0);
                SKColor c = PdfColor.LabToSRgb(l, a, b);
                rgb[at] = c.Red;
                rgb[at + 1] = c.Green;
                rgb[at + 2] = c.Blue;
            }
            return rgb;
        }

        private static byte[] WithOpacity(byte[] colour, int components, byte[] opacity)
        {
            long area = opacity.Length;
            int total = components + 1;
            var samples = new byte[area * total];
            long s = 0, d = 0;
            for (long i = 0; i < area; i++)
            {
                for (int c = 0; c < components; c++) samples[d++] = colour[s++];
                samples[d++] = opacity[i];
            }
            return samples;
        }

        /// <summary>One bit per sample, most significant first, rows padded to a byte (§8.9.3) — the stencil the raw path reads.</summary>
        private static byte[] PackBits(byte[] bits, int width, int height)
        {
            int stride = (width + 7) / 8;
            var packed = new byte[(long)stride * height];
            for (int y = 0; y < height; y++)
            {
                long row = (long)y * stride;
                long src = (long)y * width;
                for (int x = 0; x < width; x++)
                    if (bits[src + x] != 0) packed[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
            return packed;
        }

        /// <summary>
        /// The image dictionary with <c>/BitsPerComponent 8</c>, describing the narrowed samples.
        /// For <c>JPXDecode</c> the entry is optional and ignored when present (ISO 32000-2 Table
        /// 87: the codestream's own precision governs), so the raw path must not read the file's.
        /// </summary>
        private static PdfDictionary WithEightBitSamples(PdfDictionary imageDict)
        {
            PdfDictionary d = PdfDictionary.CloneShallow(imageDict);
            d.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            d.Remove(PdfName.BPC);
            return d;
        }

        /// <summary>
        /// The dictionary the raw path reads the samples by: <paramref name="imageDict"/> itself for
        /// a full decode; for a reduced one a copy whose <c>/Width</c> and <c>/Height</c> are the
        /// decoded size (the abbreviations removed, so no reader finds the native size beside them).
        /// </summary>
        private static PdfDictionary WithDecodedGeometry(PdfDictionary imageDict, JpxPdfImage image)
        {
            if (image.Reduction == 0) return imageDict;
            PdfDictionary d = PdfDictionary.CloneShallow(imageDict);
            d.Set(PdfName.Width, new PdfNumber(image.Width));
            d.Set(PdfName.Height, new PdfNumber(image.Height));
            d.Remove(PdfName.W);
            d.Remove(PdfName.H);
            return d;
        }

        private static bool IsDeviceArity(int components) =>
            components == PdfColorSpaceNames.GrayComponents
            || components == PdfColorSpaceNames.RgbComponents
            || components == PdfColorSpaceNames.CmykComponents;
    }
}
