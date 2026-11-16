// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Renderers
{
    /// <summary>
    /// <see cref="IRendererPlugin"/> that wraps the built-in SkiaSharp raster renderer,
    /// making it an explicit, replaceable plugin.
    ///
    /// Priority is 1 — runs before the implicit fallback (priority 0) but after any
    /// higher-priority custom renderer registered by the host application.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new SkiaSharpRendererPlugin());
    /// </code>
    ///
    /// <para><b>Registering this plugin costs a full re-parse per page — know that before you
    /// do.</b> <see cref="IRendererPlugin"/> hands an implementation an already-open
    /// <c>PdfDocument</c>, but the only public entry point that renders without re-entering
    /// dispatch (<c>PdfPageRenderer.RenderPageWithoutPlugins</c>) takes a <c>Stream</c>, so
    /// <see cref="RenderPage"/> re-opens the document: a fresh parse, a fresh page-tree walk, a
    /// fresh <c>FontCache</c> and a cold operation cache for every page. On the batch and session
    /// paths that is strictly worse than not registering it, because those build one
    /// <c>PdfDocument</c> and one <c>PdfRenderSession.SharedState</c> per worker and this discards
    /// both — a document with embedded fonts decodes them once per page instead of once per
    /// worker. Nothing warns, so it is stated here.</para>
    ///
    /// <para>The plugin exists for string-keyed dynamic discovery and as a replaceable seam, not as
    /// a faster path: with nothing registered the same renderer runs through the built-in fallback
    /// and keeps the caller's shared state. The fix is a contract that can accept the open
    /// document's parsed state, which is a change to <c>Base</c> rather than to this file — and it
    /// is an <b>unmeasured</b> hypothesis until someone benchmarks <c>RenderAllPages</c> with and
    /// without this plugin registered.</para>
    /// </summary>
    public sealed class SkiaSharpRendererPlugin : IRendererPlugin
    {
        public string Name        => "Tetitulz.Plugins.SkiaSharpRenderer";
        public string Description => "Renders PDF pages to PNG using SkiaSharp (built-in raster renderer).";
        public Version Version    => new Version(1, 0, 0);

        /// <summary>
        /// Priority 1 — sits just above the implicit fallback so it can be displaced by any
        /// higher-priority plugin without removing the built-in behaviour entirely.
        /// </summary>
        public int Priority => 1;

        public void Initialize() { }
        public void Shutdown()   { }

        /// <summary>Accepts every page — this is a general-purpose raster renderer.</summary>
        public bool CanRender(PdfPage page, PdfDocument document) => true;

        /// <summary>Renders <paramref name="page"/> to PNG bytes at <paramref name="dpi"/> DPI.</summary>
        public byte[] RenderPage(PdfPage page, PdfDocument document, int dpi)
        {
            if (page == null)     throw new ArgumentNullException(nameof(page));
            if (document == null) throw new ArgumentNullException(nameof(document));

            int pageIndex = -1;
            for (int i = 0; i < document.Pages.Count; i++)
            {
                if (ReferenceEquals(document.Pages[i], page)) { pageIndex = i; break; }
            }
            if (pageIndex == -1)
                throw new ArgumentException("The page does not belong to this document.", nameof(page));

            long savedPos = document.BaseStream.Position;
            try
            {
                document.BaseStream.Position = 0;
                // RenderPageWithoutPlugins, NOT RenderPage: the latter is the entry point that
                // dispatches to this plugin, so calling it would re-enter dispatch, select this
                // plugin again, and recurse until the stack overflows (uncatchable, kills the
                // process). A test that calls RenderPage on the plugin directly with nothing
                // registered cannot see that: dispatch finds no plugin there and the built-in renders.
                return PdfPageRenderer.RenderPageWithoutPlugins(document.BaseStream, pageIndex, dpi);
            }
            finally
            {
                document.BaseStream.Position = savedPos;
            }
        }
    }
}
