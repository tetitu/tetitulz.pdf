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
    /// <summary>Configuration for <see cref="IWatermarkPlugin"/>.</summary>
    public sealed class PluginWatermarkOptions
    {
        public string Text         { get; set; } = "CONFIDENTIAL";
        public string FontName     { get; set; } = "Helvetica";
        public double FontSize     { get; set; } = 48;
        public double AngleDegrees { get; set; } = 45;
        public double Opacity      { get; set; } = 0.3;
        public double ColorR       { get; set; } = 0.7;
        public double ColorG       { get; set; } = 0.7;
        public double ColorB       { get; set; } = 0.7;
    }

    /// <summary>Plugin that stamps a diagonal text watermark on all PDF pages.</summary>
    /// <remarks>
    /// <b>Discovery contract — the library does not dispatch this automatically.</b>
    /// Nothing inside Tetitulz.Pdf calls IWatermarkPlugin; it exists so a host application that
    /// references only the plugin SDK can discover and invoke the capability without taking a
    /// compile-time dependency on the assembly that implements it. Resolve it yourself:
    /// <code>
    ///   var p = PdfPluginHost.Instance.GetFirstPlugin&lt;IWatermarkPlugin&gt;();
    ///   if (p != null) { /* use it */ }
    /// </code>
    /// If you want the built-in behaviour instead, call the concrete implementation directly
    /// (e.g. <c>PdfSanitizer.Sanitize</c>) — registering a plugin does not replace it.
    /// </remarks>
    public interface IWatermarkPlugin : IPdfPlugin
    {
        void AddWatermark(Stream input, Stream output, PluginWatermarkOptions options);
    }
}
