// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Offers embedded PostScript — a <c>/Subtype /PS</c> XObject or the legacy inline <c>PS</c>
    /// operator — to a registered <see cref="IPostScriptPlugin"/> and returns the PDF content
    /// stream it produced, or false when there is no plugin, every plugin declined, a plugin threw,
    /// or the output failed validation. In every one of those cases the caller falls through to the
    /// existing placeholder.
    ///
    /// <para>Shared by the raster renderer and the SVG renderer so a plugin cannot be live in one
    /// and invisible in the other — the same reason <see cref="Images.PluginImageDecoder"/>
    /// exists.</para>
    /// </summary>
    internal static class PluginPostScriptTranslator
    {
        /// <summary>
        /// Runs the registered plugins in registration order and returns the first usable result.
        /// </summary>
        /// <param name="source">Which legacy surface produced this PostScript.</param>
        /// <param name="postScript">Already filter-decoded PostScript bytes.</param>
        /// <param name="xobject">The PS XObject stream, or null for the inline operator.</param>
        /// <param name="resources">The <c>/Resources</c> in force at the operator.</param>
        /// <param name="doc">Document backing <paramref name="xobject"/> and <paramref name="resources"/>.</param>
        /// <param name="ctm">The CTM in force; copied, never handed out by reference.</param>
        /// <param name="pageWidth">Page width in points.</param>
        /// <param name="pageHeight">Page height in points.</param>
        /// <param name="deviceScale">Device pixels per point, or 0 for a resolution-independent target.</param>
        /// <param name="contentStream">The plugin's PDF content stream, on success.</param>
        /// <param name="contentResources">The plugin's resource dictionary, or null to inherit.</param>
        /// <param name="effectiveResources">
        /// The dictionary the returned content stream must actually run against, per Table 91 —
        /// see <see cref="SelectResources"/>. Both renderers had derived this themselves.
        /// </param>
        internal static bool TryTranslate(
            PostScriptSource source,
            byte[] postScript,
            PdfDictionary xobject,
            PdfDictionary resources,
            PdfDocument doc,
            float[] ctm,
            float pageWidth, float pageHeight, float deviceScale,
            out byte[] contentStream, out PdfDictionary contentResources,
            out PdfDictionary effectiveResources)
        {
            contentStream = null;
            contentResources = null;
            effectiveResources = resources;
            if (postScript == null || postScript.Length == 0) return false;

            var plugins = PdfPluginHost.Instance.GetPlugins<IPostScriptPlugin>();
            if (plugins.Count == 0)
            {
                ReportNoPluginRegistered();
                return false;
            }

            var request = new PostScriptContentRequest
            {
                Source           = source,
                PostScript       = postScript,
                Level1PostScript = DecodeLevel1(xobject, doc),
                XObject          = xobject,
                Resources        = resources,
                Document         = doc,
                Ctm              = (float[])(ctm ?? new float[] { 1, 0, 0, 1, 0, 0 }).Clone(),
                PageWidth        = pageWidth,
                PageHeight       = pageHeight,
                DeviceScale      = deviceScale
            };

            foreach (var plugin in plugins)
            {
                PostScriptContentResult result;
                try
                {
                    if (!plugin.TryConvertToPdfContent(request, out result))
                        continue;   // declined — ask the next plugin, then the placeholder
                }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                        $"IPostScriptPlugin '{plugin.Name}' threw converting {Describe(source)}; " +
                        "falling back to the built-in placeholder.", ex);
                    continue;
                }

                if (!IsUsable(result, plugin.Name, source)) continue;

                contentStream    = result.ContentStream;
                contentResources = result.Resources;
                effectiveResources = SelectResources(result.Resources, xobject, resources, doc);
                return true;
            }

            ReportEveryPluginDeclined(plugins, source, deviceScale);
            return false;
        }

        /// <summary>
        /// Table 91's resource rule, applied once for both renderers: what the plugin returns
        /// REPLACES the inherited dictionary; when it returns none, a PostScript XObject inherits
        /// its own <c>/Resources</c>, falling back to the page's; the inline <c>PS</c> operator has
        /// no XObject at all and therefore always inherits the page's.
        /// </summary>
        /// <remarks>
        /// <b>This was written out four times</b> — once per renderer for the XObject form and once
        /// per renderer for the inline operator — and only the raster XObject copy carried the
        /// comment saying what rule it was implementing. A resource selection is a VALUE, which is
        /// the category that gets one implementation reached from both renderers; and this file is
        /// already "the single dispatch site, so a plugin cannot be live in one renderer and
        /// invisible in the other", which the resource rule was quietly outside of.
        /// </remarks>
        private static PdfDictionary SelectResources(
            PdfDictionary pluginResources, PdfDictionary xobject, PdfDictionary resources, PdfDocument doc)
        {
            if (pluginResources != null) return pluginResources;

            if (xobject != null && xobject[PdfName.Resources] is PdfObject own)
                return doc?.ResolveObject(own) as PdfDictionary ?? resources;

            return resources;
        }

        [ThreadStatic]
        private static bool _reportedNoPlugin;

        /// <summary>
        /// "There is no PostScript plugin at all" — reported <b>once per thread</b>, with the hint
        /// that names the package, exactly as the JPEG 2000 path does for a missing codec.
        /// </summary>
        /// <remarks>
        /// <b>This is the distinction CLAUDE.md records as a shipped defect for JPEG 2000</b>
        /// ("JpxImageDecoder stayed silent when a codec <i>was</i> present and declined a
        /// codestream, making that indistinguishable from having no codec at all"). One generic "No
        /// IPostScriptPlugin handled …" message for both states would send an operator whose
        /// registered plugin had correctly declined at
        /// <c>DeviceScale 0</c> — a fact about the <i>target</i>, not about the plugin being
        /// missing — off re-checking registration. Reported from here
        /// rather than from the callers so both renderers say the same thing.
        /// </remarks>
        private static void ReportNoPluginRegistered()
        {
            if (_reportedNoPlugin) return;
            _reportedNoPlugin = true;
            RenderDiagnostics.Report(RenderDiagnosticKind.PostScriptUnsupported,
                "No IPostScriptPlugin is registered, so embedded PostScript draws the built-in " +
                "placeholder. Reference Tetitulz.Pdf.Plugins.PostScript and register it (directly, " +
                "or via PdfPluginHost.Instance.RegisterFromLoadedAssemblies()).");
        }

        /// <summary>
        /// "Plugins were present and every one of them declined" — reported per occurrence, naming
        /// them and the <c>DeviceScale</c>, which is the parameter they most often decline on.
        /// </summary>
        private static void ReportEveryPluginDeclined(
            IReadOnlyList<IPostScriptPlugin> plugins, PostScriptSource source, float deviceScale)
        {
            var names = new List<string>(plugins.Count);
            foreach (var p in plugins) names.Add(p.Name);

            RenderDiagnostics.Report(RenderDiagnosticKind.PostScriptUnsupported,
                $"Every registered IPostScriptPlugin declined {Describe(source)} at DeviceScale " +
                $"{deviceScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}: " +
                $"{string.Join(", ", names)}. The built-in placeholder was drawn.");
        }

        /// <summary>Re-arms the once-per-thread "no plugin registered" report. For tests.</summary>
        internal static void ResetNoPluginReporting() => _reportedNoPlugin = false;

        /// <summary>
        /// Decodes the optional <c>/Level1</c> alternate stream (ISO 32000-1 §8.10.7, Table 91).
        /// A decode failure is not fatal — the plugin still gets the main stream.
        /// </summary>
        private static byte[] DecodeLevel1(PdfDictionary xobject, PdfDocument doc)
        {
            if (xobject == null || doc == null) return null;
            if (doc.ResolveObject(xobject[PdfName.Level1]) is not PdfStream lvl1) return null;
            try
            {
                byte[] d = lvl1.GetDecodedData();
                return d != null && d.Length > 0 ? d : null;
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "PostScript XObject /Level1 alternate stream decode failed; " +
                    "the plugin receives only the main stream.", ex);
                return null;
            }
        }

        /// <summary>
        /// Bounds the untrusted output: rejects — and reports — an empty or oversized content
        /// stream, and a resource graph that is too wide or too deep.
        /// </summary>
        private static bool IsUsable(PostScriptContentResult result, string pluginName,
                                     PostScriptSource source)
        {
            if (result?.ContentStream == null || result.ContentStream.Length == 0)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                    $"IPostScriptPlugin '{pluginName}' returned no content for {Describe(source)}; " +
                    "falling back to the built-in placeholder.");
                return false;
            }

            if (result.ContentStream.Length > PdfLimits.MaxPluginContentStreamBytes)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                    $"IPostScriptPlugin '{pluginName}' returned a {result.ContentStream.Length}-byte " +
                    $"content stream, over the {PdfLimits.MaxPluginContentStreamBytes}-byte cap; " +
                    "falling back to the built-in placeholder.");
                return false;
            }

            if (result.Resources != null && !IsResourceGraphWithinLimits(result.Resources))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                    $"IPostScriptPlugin '{pluginName}' returned a resource dictionary exceeding " +
                    $"{PdfLimits.MaxPluginResourceEntries} direct entries or " +
                    $"{PdfLimits.MaxObjectGraphDepth} levels; falling back to the built-in placeholder.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Bounded walk of the DIRECT resource graph. Indirect references are deliberately NOT
        /// followed: they belong to the host document's already-validated object graph, and
        /// following them would let a plugin's one-entry dictionary charge us the whole file.
        /// The depth cap is also what makes this safe against a self-nesting direct dictionary,
        /// which is why no visited-set is needed.
        /// </summary>
        private static bool IsResourceGraphWithinLimits(PdfObject root)
        {
            int budget = PdfLimits.MaxPluginResourceEntries;
            var stack = new Stack<(PdfObject Node, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (depth > PdfLimits.MaxObjectGraphDepth) return false;

                switch (node)
                {
                    // PdfStream derives from PdfDictionary, so this arm covers both.
                    case PdfDictionary dict:
                        foreach (var kv in dict)
                        {
                            if (--budget < 0) return false;
                            if (kv.Value is PdfDictionary or PdfArray)
                                stack.Push((kv.Value, depth + 1));
                        }
                        break;

                    case PdfArray arr:
                        for (int i = 0; i < arr.Count; i++)
                        {
                            if (--budget < 0) return false;
                            if (arr[i] is PdfDictionary or PdfArray)
                                stack.Push((arr[i], depth + 1));
                        }
                        break;
                }
            }

            return true;
        }

        private static string Describe(PostScriptSource s)
            => s == PostScriptSource.XObject
                ? "a /Subtype /PS XObject"
                : "the inline `PS` operator";
    }
}
