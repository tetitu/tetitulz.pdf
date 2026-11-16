// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Plugin that interprets embedded PostScript and returns the equivalent <b>PDF content
    /// stream</b>. Register an implementation to make legacy <c>/Subtype /PS</c> XObjects
    /// (ISO 32000-1 §8.10.7) and the deprecated inline <c>PS</c> operator paint real content
    /// instead of the built-in placeholder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a content stream and not pixels.</b> The result is executed by the renderer's
    /// existing form-XObject machinery, so the raster and SVG renderers share one code path and
    /// the output stays vector — which matters because the SVG renderer is resolution-independent
    /// and has no DPI to rasterize at. A plugin that wraps an external raster RIP still complies:
    /// emit <c>q w 0 0 h 0 0 cm /Im0 Do Q</c> and put an image <see cref="Objects.PdfStream"/>
    /// under <c>/XObject /Im0</c> in <see cref="PostScriptContentResult.Resources"/>.
    /// </para>
    /// <para>
    /// <b>Dispatch.</b> Registered plugins are consulted in registration order; the first that
    /// returns <c>true</c> with usable output wins. A plugin that returns <c>false</c> declines
    /// and the next is asked. A plugin that throws is reported to <c>RenderDiagnostics</c> as
    /// <c>PluginFailure</c> and the next is asked. When nobody handles it the renderer reports
    /// <c>PostScriptUnsupported</c> and falls back to the placeholder (XObject) or a no-op
    /// (inline operator) — a broken or absent plugin degrades output, it never breaks rendering.
    /// </para>
    /// <para>
    /// <b>The returned graph is untrusted.</b> The content stream is rejected above
    /// <see cref="Constants.PdfLimits.MaxPluginContentStreamBytes"/>; the resource dictionary is
    /// rejected above <see cref="Constants.PdfLimits.MaxPluginResourceEntries"/> direct entries
    /// or <see cref="Constants.PdfLimits.MaxObjectGraphDepth"/> nesting levels. Rejection is
    /// reported as <c>PluginFailure</c> and the placeholder is drawn.
    /// </para>
    /// <para>
    /// <b>Object identity.</b> Any <see cref="Objects.PdfReference"/> inside the returned
    /// resources is resolved against <see cref="PostScriptContentRequest.Document"/>. Reusing a
    /// reference read out of <see cref="PostScriptContentRequest.Resources"/> is correct and
    /// supported; fabricating an object number is not — it will alias an unrelated object in the
    /// user's file. Anything the plugin creates itself must be a direct object.
    /// </para>
    /// <para><b>Thread safety.</b> <see cref="TryConvertToPdfContent"/> may be called
    /// concurrently for different pages and must not mutate the request.</para>
    /// </remarks>
    public interface IPostScriptPlugin : IPdfPlugin
    {
        /// <summary>
        /// Attempt to convert <paramref name="request"/>'s PostScript into PDF content-stream
        /// operators.
        /// </summary>
        /// <param name="request">The PostScript program and its rendering context.</param>
        /// <param name="result">The converted content stream and its resources, on success.</param>
        /// <returns>
        /// <c>true</c> with a usable <paramref name="result"/>; <c>false</c> to decline (the next
        /// registered plugin is asked, then the built-in placeholder). Decline — do not emit
        /// something approximate — when <see cref="PostScriptContentRequest.DeviceScale"/> is 0
        /// and this plugin can only rasterize.
        /// </returns>
        bool TryConvertToPdfContent(PostScriptContentRequest request,
                                    out PostScriptContentResult result);
    }
}
