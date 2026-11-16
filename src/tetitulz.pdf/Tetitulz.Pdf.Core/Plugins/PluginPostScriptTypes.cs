// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>Which of the two legacy PostScript surfaces produced a request.</summary>
    public enum PostScriptSource
    {
        /// <summary>A <c>/Subtype /PS</c> XObject invoked by <c>Do</c> (ISO 32000-1 §8.10.7).</summary>
        XObject,

        /// <summary>The inline <c>(...) PS</c> content-stream operator (PDF 1.1, deprecated in 1.2).</summary>
        InlineOperator
    }

    /// <summary>
    /// Everything an <see cref="IPostScriptPlugin"/> is given about one PostScript fragment.
    /// </summary>
    public sealed class PostScriptContentRequest
    {
        /// <summary>Which surface this came from.</summary>
        public PostScriptSource Source { get; set; }

        /// <summary>
        /// The PostScript program, already filter-decoded by the host — so the host's
        /// decompression caps apply and the plugin need not call <c>GetDecodedData</c>.
        /// Never null or empty.
        /// </summary>
        public byte[] PostScript { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// The decoded <c>/Level1</c> alternate stream (ISO 32000-1 §8.10.7, Table 91) when the
        /// XObject carries one, else null. A producer supplies it when the main stream needs a
        /// Level 2/3 interpreter; a Level-1-only plugin should prefer this and decline when it is
        /// absent. Always null for <see cref="PostScriptSource.InlineOperator"/>.
        /// </summary>
        public byte[]? Level1PostScript { get; set; }

        /// <summary>
        /// The <c>/Subtype /PS</c> XObject dictionary — it is a <see cref="PdfStream"/>, so
        /// <c>/BBox</c> and any producer-private keys are reachable — or null for the inline
        /// operator. Do not mutate.
        /// </summary>
        public PdfDictionary? XObject { get; set; }

        /// <summary>
        /// The <c>/Resources</c> dictionary in force at the <c>Do</c> / <c>PS</c>: page
        /// resources, or the enclosing form's. Read-only — use it to look up a font or colour
        /// space the PostScript references by PDF name. Do not mutate.
        /// </summary>
        public PdfDictionary? Resources { get; set; }

        /// <summary>
        /// Document backing <see cref="XObject"/> and <see cref="Resources"/>, for resolving
        /// indirect references.
        /// </summary>
        public PdfDocument? Document { get; set; }

        /// <summary>
        /// A copy of the 6-element current transformation matrix in force at the operator.
        /// <b>Informational only.</b> The returned content stream is executed in this same CTM,
        /// so a plugin that also applies it will double-transform.
        /// </summary>
        public float[] Ctm { get; set; } = new float[] { 1, 0, 0, 1, 0, 0 };

        /// <summary>
        /// Page width in PDF points — a default extent for a <c>/PS</c> XObject with no
        /// <c>/BBox</c>.
        /// </summary>
        public float PageWidth { get; set; }

        /// <summary>Page height in PDF points.</summary>
        public float PageHeight { get; set; }

        /// <summary>
        /// Device pixels per PDF point for the current target, or <b>0 when the target is
        /// resolution-independent</b> (the SVG renderer). A plugin that can only rasterize should
        /// decline at 0 rather than guess a DPI: declining falls back to the placeholder, which
        /// is honest, where a guessed raster would be permanently blurry.
        /// </summary>
        public float DeviceScale { get; set; }
    }

    /// <summary>What an <see cref="IPostScriptPlugin"/> hands back.</summary>
    public sealed class PostScriptContentResult
    {
        /// <summary>
        /// Uncompressed PDF content-stream operators. Executed exactly like a form XObject body:
        /// inside a <c>q</c>/<c>Q</c> pair, in the CTM in force at the operator, one recursion
        /// level deeper. Emit <c>re W n</c> first if you want to bound your own marks — the host
        /// clips only to the XObject's <c>/BBox</c>, and the inline operator has none.
        /// </summary>
        public byte[] ContentStream { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Resources for <see cref="ContentStream"/>, or null to inherit. A non-null value
        /// <b>replaces</b> the inherited dictionary for the duration of the stream — the same
        /// rule PDF applies to a form XObject's own <c>/Resources</c> — so include everything you
        /// name. Null inherits the XObject's own <c>/Resources</c> if it has one, else the page's.
        /// </summary>
        public PdfDictionary? Resources { get; set; }
    }
}
