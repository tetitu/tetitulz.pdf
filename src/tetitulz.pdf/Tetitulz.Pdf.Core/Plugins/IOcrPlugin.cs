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
    /// Plugin that performs optical character recognition on raw pixel data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Dispatch is explicit, not automatic.</b> Call
    /// <c>PdfOperations.RecognizeTextWithOcr(stream, pageIndex, dpi)</c>, which rasterizes the
    /// page and hands the samples to the first registered plugin whose
    /// <see cref="CanProcess"/> accepts the format (resolved via
    /// <c>PdfPluginHost.FindOcrPlugin</c>).
    /// </para>
    /// <para>
    /// <c>ExtractText</c> does <b>not</b> silently fall back to OCR on image-only pages: OCR is
    /// lossy and slow, and doing so would change what existing callers get back. The text
    /// extraction pipeline does not dispatch it.
    /// </para>
    /// </remarks>
    public interface IOcrPlugin : IPdfPlugin
    {
        /// <summary>
        /// Return <c>true</c> if this plugin can process pixels with the given
        /// colour-component count and bit depth.
        /// </summary>
        bool CanProcess(int components, int bitsPerComponent);

        /// <summary>
        /// Recognise text in the provided raw pixel buffer.
        /// </summary>
        /// <param name="pixels">
        /// Interleaved 8-bit pixel data, row-major, top-to-bottom.
        /// </param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="components">Number of colour components (1=gray, 3=RGB).</param>
        /// <returns>Recognised text, or <c>null</c> if recognition failed.</returns>
        string? RecognizeText(byte[] pixels, int width, int height, int components);
    }
}
