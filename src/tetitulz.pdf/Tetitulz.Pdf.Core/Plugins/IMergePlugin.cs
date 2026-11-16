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
    /// <summary>Plugin that merges multiple PDF streams into one.</summary>
    /// <remarks>
    /// <b>Discovery contract — the library does not dispatch this automatically.</b>
    /// Nothing inside Tetitulz.Pdf calls IMergePlugin; it exists so a host application that
    /// references only the plugin SDK can discover and invoke the capability without taking a
    /// compile-time dependency on the assembly that implements it. Resolve it yourself:
    /// <code>
    ///   var p = PdfPluginHost.Instance.GetFirstPlugin&lt;IMergePlugin&gt;();
    ///   if (p != null) { /* use it */ }
    /// </code>
    /// If you want the built-in behaviour instead, call the concrete implementation directly
    /// (e.g. <c>PdfSanitizer.Sanitize</c>) — registering a plugin does not replace it.
    /// </remarks>
    public interface IMergePlugin : IPdfPlugin
    {
        /// <summary>
        /// Merges <paramref name="inputs"/> into <paramref name="output"/>.
        /// Consistent with <see cref="ISplitPlugin"/>: both interfaces use Stream I/O
        /// for symmetry and to avoid large in-memory byte arrays for multi-document merges.
        /// </summary>
        void Merge(Stream output, IReadOnlyList<Stream> inputs);
    }
}
