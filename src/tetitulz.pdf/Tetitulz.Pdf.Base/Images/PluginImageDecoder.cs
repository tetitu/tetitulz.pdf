// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Offers a decoded image to a registered <see cref="IImageDecodePlugin"/>, dispatching on
    /// the image's <b>declared</b> <c>/Filter</c> name(s) rather than on sniffed magic bytes —
    /// which is what makes the interface's documented purpose ("add support for currently
    /// unsupported filters such as JBIG2Decode") reachable.
    ///
    /// <para>Shared by the raster renderer and the SVG renderer so a plugin cannot be live in
    /// one and invisible in the other.</para>
    /// </summary>
    internal static class PluginImageDecoder
    {
        /// <summary>
        /// A copy of <paramref name="imageDict"/> whose <b>indirectly-written entries are
        /// resolved</b>, for handing to a plugin that has no document to resolve them with.
        /// </summary>
        /// <remarks>
        /// <para>Every entry is passed through, so a plugin sees the same dictionary it always did;
        /// only values written as <c>PdfReference</c> change, and they change into what the file
        /// says they are. Returns the original instance when nothing needed resolving, which is the
        /// overwhelmingly common case — an ordinary image allocates nothing.</para>
        ///
        /// <para><b>Shallow on purpose.</b> The keys a decoder reads — <c>/Width</c>,
        /// <c>/Height</c>, <c>/BitsPerComponent</c>, <c>/SMaskInData</c>, <c>/ColorSpace</c> — are
        /// scalars or arrays the plugin walks itself. Resolving the whole graph would copy stream
        /// data a plugin may never touch.</para>
        /// </remarks>
        internal static PdfDictionary WithResolvedScalars(PdfDictionary imageDict, PdfDocument doc)
        {
            if (doc == null) return imageDict;

            PdfDictionary copy = null;
            foreach (var kvp in imageDict)
            {
                if (!(kvp.Value is PdfReference)) continue;

                if (copy == null)
                {
                    copy = new PdfDictionary();
                    foreach (var existing in imageDict) copy.Set(existing.Key, existing.Value);
                }

                copy.Set(kvp.Key, doc.ResolveObject(kvp.Value) ?? kvp.Value);
            }

            return copy ?? imageDict;
        }

        /// <summary>
        /// Returns the bitmap produced by the first plugin that handles one of the image's
        /// declared filters, or null when no plugin handles the filter, the plugin declines, or
        /// the plugin fails — in every case the caller continues with the built-in decoders.
        /// </summary>
        /// <param name="data">The image stream's filter-encoded bytes.</param>
        /// <param name="imageDict">The image XObject dictionary, passed on to the plugin.</param>
        /// <param name="doc">Document used to resolve an indirect <c>/ColorSpace</c>.</param>
        /// <param name="alphaTypeWithAlpha">
        /// Alpha type for the result when the samples carry alpha. See
        /// <see cref="ImageSampleLayout.BuildBitmap"/>.
        /// </param>
        /// <param name="layout">How the returned bitmap's samples were read; default when null
        /// is returned.</param>
        /// <param name="graphicsStateIntent">
        /// The rendering intent at the <c>Do</c>, forwarded to <see cref="ImageSampleLayout.Resolve"/>
        /// so a plugin-decoded CMYK image is colour-managed under the same intent as any other.
        /// </param>
        internal static SKBitmap DecodeToBitmap(byte[] data, PdfDictionary imageDict, PdfDocument doc,
            SKAlphaType alphaTypeWithAlpha, out ImageSampleLayout layout,
            Rendering.IccRenderingIntent graphicsStateIntent
                = Rendering.IccRenderingIntents.Default)
        {
            layout = default;
            if (imageDict == null || data == null) return null;

            // Handed to the plugin with its scalar entries already resolved, because a plugin
            // CANNOT resolve: IImageDecodePlugin.TryDecode receives a PdfDictionary and no
            // document, by contract. So `/Width 9 0 R` — legal under §7.3.10 — read as absent
            // inside every image plugin, and the JPEG 2000 one then fell back to /Height, or gave
            // up and returned false, leaving the image invisible with no diagnostic.
            //
            // Fixed here rather than in the plugin, and rather than by widening the contract: this
            // is the single dispatch point both renderers share, so one copy fixes every plugin,
            // and no plugin author has to be told about a breaking signature change.
            PdfDictionary pluginDict = WithResolvedScalars(imageDict, doc);

            foreach (string filterName in GetImageFilterNames(imageDict, doc))
            {
                // JPEG 2000 is JpxImageDecoder's: the in-house decoder answers first and the
                // plugin only when it declines, and both renderers reach it before this dispatch.
                // Offering the bytes here as well would ask the plugin ahead of the decoder, or
                // ask it twice.
                if (string.Equals(filterName, JpxImageDecoder.FilterName, StringComparison.Ordinal)) continue;

                var plugin = PdfPluginHost.Instance.FindDecodePlugin(filterName);
                if (plugin == null) continue;

                int pw, ph, comps;
                byte[] pixels;
                try
                {
                    if (!plugin.TryDecode(filterName, data, pluginDict,
                            out pw, out ph, out comps, out pixels))
                        continue;   // plugin declined — fall through to the built-in decoders
                }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                        $"IImageDecodePlugin '{plugin.Name}' threw decoding /Filter /{filterName}; " +
                        "falling back to the built-in decoder.", ex);
                    continue;
                }

                // The JP2 header is extra evidence for the channel layout, but only when these
                // bytes really are a JPEG 2000 codestream.
                byte[] jpxData = string.Equals(filterName, JpxImageDecoder.FilterName, StringComparison.Ordinal)
                    ? data : null;

                var resolved = ImageSampleLayout.Resolve(imageDict, doc, comps, jpxData,
                                                         graphicsStateIntent);
                var bmp = resolved.BuildBitmap(pixels, pw, ph, alphaTypeWithAlpha);
                if (bmp != null)
                {
                    layout = resolved;
                    return bmp;
                }

                RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                    $"IImageDecodePlugin '{plugin.Name}' returned unusable samples for /Filter /{filterName} " +
                    $"({pw}x{ph}, {comps} component(s)); falling back to the built-in decoder.");
            }

            return null;
        }

        /// <summary>
        /// Whether a registered plugin would be offered this image — one of its declared filters,
        /// JPEG 2000 apart, has a decode plugin. For a caller deciding whether an encoded image may
        /// pass through as it is (the SVG writer's JPEG fast path): a plugin-decoded image takes
        /// the decode, as it does in the raster path.
        /// </summary>
        internal static bool HasDecoderFor(PdfDictionary imageDict, PdfDocument doc)
        {
            if (imageDict == null) return false;
            foreach (string filterName in GetImageFilterNames(imageDict, doc))
            {
                if (string.Equals(filterName, JpxImageDecoder.FilterName, StringComparison.Ordinal)) continue;
                if (PdfPluginHost.Instance.FindDecodePlugin(filterName) != null) return true;
            }
            return false;
        }

        /// <summary>
        /// Yields the image's declared filter names in application order. <c>/Filter</c> may be
        /// a single name or an array (a filter chain); the image codec is the last entry.
        /// </summary>
        /// <remarks>
        /// <b>Read through <see cref="PdfResolve"/>, both for the entry and for each array
        /// element.</b> <c>PdfDictionary</c>'s indexer does not follow an indirect reference, so a
        /// producer writing <c>/Filter 12 0 R</c> - legal; the spec constrains only <c>/Length</c> -
        /// would produce a <c>PdfReference</c>, which matches neither arm, and no plugin would be
        /// offered the image — indistinguishable from "no plugin is registered for this filter",
        /// the single dispatch point for <c>IImageDecodePlugin</c> silently switched off.
        /// </remarks>
        internal static IEnumerable<string> GetImageFilterNames(PdfDictionary imageDict, PdfDocument doc = null)
        {
            var filterObj = PdfResolve.Value(doc, imageDict[PdfName.Filter])
                         ?? PdfResolve.Value(doc, imageDict[PdfName.F]);
            if (filterObj is PdfName single)
            {
                if (!string.IsNullOrEmpty(single.Value)) yield return single.Value;
                yield break;
            }
            if (filterObj is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                    if (PdfResolve.Value(doc, arr[i]) is PdfName n && !string.IsNullOrEmpty(n.Value))
                        yield return n.Value;
            }
        }
    }
}
