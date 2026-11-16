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
    /// <summary>
    /// Plugin that validates a PDF document against a conformance level such as
    /// PDF/A-1b, PDF/UA-1, or PDF/X-1a.
    /// Multiple validation plugins may be registered; each is independent.
    /// </summary>
    public interface IValidationPlugin : IPdfPlugin
    {
        /// <summary>
        /// The conformance level this plugin validates (e.g. "PDF/A-1b", "PDF/UA-1", "PDF/X-1a").
        /// Used for dispatch when a caller requests validation of a specific level.
        /// </summary>
        string ConformanceLevel { get; }

        /// <summary>
        /// Validate the PDF in <paramref name="pdfStream"/> against <see cref="ConformanceLevel"/>.
        /// The stream must be readable and seekable; the plugin must not dispose it.
        /// </summary>
        PluginValidationResult Validate(Stream pdfStream);
    }
}
