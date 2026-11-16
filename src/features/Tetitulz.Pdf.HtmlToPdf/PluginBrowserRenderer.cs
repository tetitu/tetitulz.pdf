// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// The single site that resolves an <see cref="IHtmlBrowserPlugin"/>, so the browser engine
    /// cannot be live on one entry point and absent on another.
    /// </summary>
    /// <remarks>
    /// <para>Modelled on <c>Base.Scripting.PluginJavaScriptRunner</c> and
    /// <c>Base.Rendering.PluginPostScriptTranslator</c>: one dispatch point per plugin contract is
    /// what stopped JPEG 2000 and PostScript from being live in the raster renderer and invisible in
    /// the SVG one. Every browser-engine call in this assembly comes through here.</para>
    /// <para><b>Absence is an error, never a fallback.</b> A caller who asked for
    /// <see cref="HtmlRenderEngine.Browser"/> wants JavaScript, remote subresources or full CSS —
    /// quietly serving the managed engine instead would hand them a document that is wrong in ways
    /// they cannot see. This is the same rule the URL entry points already apply in the other
    /// direction: <c>RefuseManagedUrlConversion</c> refuses rather than silently launching a
    /// browser.</para>
    /// </remarks>
    internal static class PluginBrowserRenderer
    {
        /// <summary>True when a browser plugin is registered.</summary>
        internal static bool IsAvailable =>
            PdfPluginHost.Instance.GetPlugins<IHtmlBrowserPlugin>().Count > 0;

        /// <summary>
        /// Renders through the first registered browser plugin.
        /// </summary>
        /// <exception cref="HtmlToPdfException">No plugin is registered.</exception>
        internal static async Task<byte[]> RenderAsync(HtmlBrowserRequest request)
        {
            IReadOnlyList<IHtmlBrowserPlugin> plugins =
                PdfPluginHost.Instance.GetPlugins<IHtmlBrowserPlugin>();

            if (plugins.Count == 0)
                throw new HtmlToPdfException(
                    "HtmlRenderEngine.Browser needs a browser plugin, and none is registered. " +
                    "Reference the Tetitulz.Pdf.Plugins.HtmlBrowser package and register it " +
                    "(PdfPluginHost.Instance.Register(new PlaywrightHtmlBrowserPlugin()), or " +
                    "PdfPluginHost.RegisterFromLoadedAssemblies()), then install the browsers with " +
                    "PlaywrightHtmlBrowserPlugin.InstallBrowsers(). Or use " +
                    "HtmlRenderEngine.Managed, the default, which needs nothing installed.");

            // First registered wins, as everywhere else in the host. Not wrapped in a try/catch:
            // unlike a diagnostic hook, a failed render has no meaningful degraded result, and
            // swallowing it here would return an empty PDF the caller cannot distinguish from a
            // blank page.
            byte[] pdf = await plugins[0].RenderAsync(request).ConfigureAwait(false);

            if (pdf == null || pdf.Length == 0)
                throw new HtmlToPdfException(
                    "The registered browser plugin '" + plugins[0].Name + "' returned no PDF bytes.");

            return pdf;
        }
    }
}
