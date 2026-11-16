// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Plugin that decodes image filter data into raw pixel samples.
    /// Register an implementation to supplement a built-in decoder, or to supply one the
    /// library does not have. For <c>JPXDecode</c> the library decodes JPEG 2000 itself
    /// (<c>Core.Imaging.Jpx</c>) and a JPEG 2000 plugin (a host's own) is consulted <b>only when the in-house decoder
    /// declines</b> a codestream — a second opinion, never the first.
    ///
    /// Dispatch: exactly ONE plugin is consulted per filter name — the first one
    /// registered that lists the filter in <see cref="SupportedFilters"/> (the
    /// host caches a single plugin per filter). If that plugin's
    /// <see cref="TryDecode"/> returns <c>false</c>, the host falls back to the
    /// built-in decoder — it does NOT try a second plugin for the same filter.
    /// Register at most one plugin per filter name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dispatch keys on the image dictionary's declared <c>/Filter</c> name(s) — every name in
    /// the chain is offered — so a plugin can add support for any filter, JBIG2Decode and
    /// CCITTFaxDecode included.
    /// </para>
    /// <para>
    /// The returned geometry is treated as untrusted: samples are rejected if the dimensions
    /// exceed the renderer's size limits or the buffer is shorter than
    /// <c>width * height * components</c>. A plugin that throws is reported to
    /// <c>RenderDiagnostics</c> as <c>PluginFailure</c> and the built-in decoder takes over.
    /// </para>
    /// </remarks>
    public interface IImageDecodePlugin : IPdfPlugin
    {
        /// <summary>
        /// PDF filter names this plugin handles, e.g. ["JPXDecode", "JBIG2Decode"].
        /// Names are case-sensitive and must match the PDF spec spelling.
        /// </summary>
        IReadOnlyList<string> SupportedFilters { get; }

        /// <summary>
        /// Attempt to decode <paramref name="data"/> as filtered image bytes.
        /// </summary>
        /// <param name="filterName">The PDF filter name (e.g. "JPXDecode").</param>
        /// <param name="data">Raw filter-encoded bytes.</param>
        /// <param name="imageDictionary">
        /// The <b>image XObject dictionary</b> (may be null) — <c>/Width</c>, <c>/Height</c>,
        /// <c>/ColorSpace</c>, <c>/BitsPerComponent</c>, <c>/DecodeParms</c> and the rest.
        /// <para><b>NOT the decode-parameters dictionary.</b> This parameter was named
        /// <c>parameters</c> and documented as "/DecodeParms", while both dispatch sites
        /// (<c>PluginImageDecoder</c> and <c>JpxImageDecoder</c>) have always passed the image
        /// dictionary — so a JBIG2 plugin written to the contract read
        /// <c>parameters[/JBIG2Globals]</c>, got null, and declined every page that used a shared
        /// symbol dictionary. The dispatch sites are right (a decoder needs the geometry and the
        /// colour space too); the name and the doc were wrong.</para>
        /// <para>Resolve <c>/DecodeParms</c> from this dictionary yourself, and note that for a
        /// filter <b>chain</b> it is an ARRAY positionally matched to <c>/Filter</c>, so the entry
        /// for <paramref name="filterName"/> is the one at that filter's index.</para>
        /// </param>
        /// <param name="width">Decoded image width in pixels.</param>
        /// <param name="height">Decoded image height in pixels.</param>
        /// <param name="components">Number of colour components (1=gray, 3=RGB, 4=CMYK).</param>
        /// <param name="pixels">
        /// Interleaved 8-bit component samples, row-major, top-to-bottom.
        /// Length == width * height * components.
        /// </param>
        /// <returns>
        /// <c>true</c> on success; <c>false</c> to fall back to the built-in
        /// decoder (no other plugin is consulted for this filter — see the
        /// interface remarks).
        /// </returns>
        bool TryDecode(string filterName, byte[] data, PdfDictionary? imageDictionary,
            out int width, out int height, out int components, out byte[] pixels);
    }
}
