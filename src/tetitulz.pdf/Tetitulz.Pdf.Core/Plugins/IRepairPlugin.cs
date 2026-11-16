// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.IO;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>Plugin that repairs a malformed PDF by rebuilding its cross-reference table.</summary>
    /// <remarks>
    /// <b>Discovery contract — the library does not dispatch this automatically.</b>
    /// Nothing inside Tetitulz.Pdf calls IRepairPlugin; it exists so a host application that
    /// references only the plugin SDK can discover and invoke the capability without taking a
    /// compile-time dependency on the assembly that implements it. Resolve it yourself:
    /// <code>
    ///   var p = PdfPluginHost.Instance.GetFirstPlugin&lt;IRepairPlugin&gt;();
    ///   if (p != null) { /* use it */ }
    /// </code>
    /// If you want the built-in behaviour instead, call the concrete implementation directly
    /// (e.g. <c>PdfSanitizer.Sanitize</c>) — registering a plugin does not replace it.
    /// </remarks>
    public interface IRepairPlugin : IPdfPlugin
    {
        /// <summary>
        /// Attempt to repair <paramref name="input"/> and write a repaired PDF to <paramref name="output"/>.
        /// </summary>
        /// <returns>
        /// A <see cref="PluginRepairResult"/> describing what was recovered.
        /// <see cref="PluginRepairResult.Succeeded"/> is <c>false</c> if the PDF is unrecoverable.
        /// </returns>
        PluginRepairResult Repair(Stream input, Stream output);
    }
}
