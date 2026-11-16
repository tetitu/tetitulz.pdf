// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// The single site that resolves an <see cref="IHtmlScriptPlugin"/>, so page scripts cannot run
    /// on one managed entry point and not on another.
    /// </summary>
    /// <remarks>
    /// Modelled on <see cref="PluginBrowserRenderer"/>. Unlike the browser engine, absence here is
    /// <b>not</b> an error: the managed engine is the default and lays out a page without its
    /// scripts perfectly well — it reports that it did, rather than refusing the conversion.
    /// </remarks>
    internal static class PluginHtmlScriptRunner
    {
        /// <summary>The first registered script plugin, or <c>null</c> when none is.</summary>
        internal static IHtmlScriptPlugin? Resolve()
        {
            IReadOnlyList<IHtmlScriptPlugin> plugins =
                PdfPluginHost.Instance.GetPlugins<IHtmlScriptPlugin>();
            return plugins.Count > 0 ? plugins[0] : null;
        }
    }
}
