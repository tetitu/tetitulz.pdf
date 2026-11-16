// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Plugin that can render a PDF page to a PNG bitmap.
    /// Registered plugins are tried in descending <see cref="Priority"/> order.
    /// The built-in SkiaSharp renderer is always the final fallback (priority 0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scope: the PNG render paths.</b> Dispatch happens at the single chokepoint through
    /// which every PNG render flows (<c>PdfPageRenderer.RenderPage(page, doc, dpi, options)</c>),
    /// so <c>PdfOperations.RenderPageToPng</c> / <c>RenderAllPagesToPng</c> and the viewer all
    /// honour it. The JPEG and tile paths do <b>not</b>: they need an <c>SKBitmap</c>, and this
    /// interface returns encoded PNG bytes.
    /// </para>
    /// <para>
    /// If <see cref="RenderPage"/> throws or returns empty output, the failure is reported to
    /// <c>RenderDiagnostics</c> as <c>PluginFailure</c> and the built-in renderer takes over —
    /// a broken plugin degrades quality, it does not break rendering.
    /// </para>
    /// </remarks>
    public interface IRendererPlugin : IPdfPlugin
    {
        /// <summary>
        /// Relative priority. Higher values are tried first.
        /// Built-in renderer has effective priority 0; use positive values to override it.
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Return <c>true</c> if this plugin is able to render <paramref name="page"/>.
        /// Called before <see cref="RenderPage"/> to allow capability filtering.
        /// </summary>
        bool CanRender(PdfPage page, PdfDocument document);

        /// <summary>
        /// Render <paramref name="page"/> to PNG bytes at the given <paramref name="dpi"/>.
        /// Only called when <see cref="CanRender"/> returned <c>true</c>.
        /// </summary>
        byte[] RenderPage(PdfPage page, PdfDocument document, int dpi);
    }
}
