// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.Threading;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Optional interface for plugins that want to intercept PDF bytes
    /// before and after processing operations.
    /// </summary>
    /// <remarks>
    /// <para><b>Discovery contract — the library never calls this.</b> Registering an implementation
    /// with <see cref="PdfPluginHost"/> does not put it in the path of any <c>PdfOperations</c>
    /// write: nothing in <c>Base</c> or <c>Core</c> calls
    /// <see cref="PdfPluginHost.InvokeBeforeProcess"/> or
    /// <see cref="PdfPluginHost.InvokeAfterProcess"/>. Those two methods are for a <b>host</b> to
    /// call around its own pipeline; a plugin whose unit tests invoke them directly is testing the
    /// host method, not a dispatch that exists.</para>
    /// <para>This is stated here because it is not observable: an unwired hook is silent, and a
    /// plugin written to sanitize every document before it is written would pass registration,
    /// pass <c>IsRegistered</c>, and never run. Contrast <see cref="ITextExtractionHook"/> and
    /// <see cref="IRenderHook"/> below, which <i>are</i> dispatched — by
    /// <c>PdfOperations.ExtractText</c> and the page-render paths respectively.</para>
    /// </remarks>
    public interface IPdfProcessingHook : IPdfPlugin
    {
        /// <summary>
        /// Called before a PDF processing operation.
        /// The plugin may inspect or replace <paramref name="pdfBytes"/>.
        /// Return the (possibly modified) bytes, or the same reference to pass through unchanged.
        /// </summary>
        byte[] OnBeforeProcess(string operationName, byte[] pdfBytes,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Called after a PDF processing operation completes.
        /// The plugin may post-process the output bytes.
        /// </summary>
        byte[] OnAfterProcess(string operationName, byte[] pdfBytes,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optional interface for plugins that want to observe text extraction results.
    /// </summary>
    public interface ITextExtractionHook : IPdfPlugin
    {
        /// <summary>Called for each page of text extracted from a PDF.</summary>
        void OnTextExtracted(int pageIndex, string text,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optional interface for plugins that want to observe render operations.
    /// </summary>
    public interface IRenderHook : IPdfPlugin
    {
        /// <summary>Called after each page is rendered to a bitmap.</summary>
        void OnPageRendered(int pageIndex, int widthPx, int heightPx,
            CancellationToken cancellationToken = default);
    }
}
