// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// A rectangular area on a page, in PDF points, and the colour of the box drawn over it: the
    /// region a redaction removes content from (<see cref="IRedactPlugin.Redact"/>, PageEdits'
    /// <c>PdfRedactor.RedactRegions</c>) or a cover box is drawn over (<c>PdfRedactor.CoverRegions</c>,
    /// which removes nothing).
    /// </summary>
    /// <remarks>
    /// One type for the plugin contract and the PageEdits redactor alike; it lives here because Core
    /// is the one assembly both reach.
    /// </remarks>
    public sealed class RedactionRegion
    {
        /// <summary>Zero-based page index.</summary>
        public int PageIndex { get; set; }
        public double X      { get; set; }
        public double Y      { get; set; }
        public double Width  { get; set; }
        public double Height { get; set; }
        public double FillR  { get; set; } = 0;
        public double FillG  { get; set; } = 0;
        public double FillB  { get; set; } = 0;
    }

    /// <summary>Plugin that permanently burns redaction rectangles onto PDF pages.</summary>
    /// <remarks>
    /// <b>Discovery contract — the library does not dispatch this automatically.</b>
    /// Nothing inside Tetitulz.Pdf calls IRedactPlugin; it exists so a host application that
    /// references only the plugin SDK can discover and invoke the capability without taking a
    /// compile-time dependency on the assembly that implements it. Resolve it yourself:
    /// <code>
    ///   var p = PdfPluginHost.Instance.GetFirstPlugin&lt;IRedactPlugin&gt;();
    ///   if (p != null) { /* use it */ }
    /// </code>
    /// If you want the built-in behaviour instead, call the concrete implementation directly
    /// (e.g. <c>PdfSanitizer.Sanitize</c>) — registering a plugin does not replace it.
    /// </remarks>
    public interface IRedactPlugin : IPdfPlugin
    {
        void Redact(Stream input, Stream output, IReadOnlyList<RedactionRegion> regions);
    }
}
