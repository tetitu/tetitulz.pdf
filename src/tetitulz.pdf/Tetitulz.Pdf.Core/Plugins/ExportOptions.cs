// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Options bag passed to <see cref="IExportPlugin.Export"/>.
    /// Use <see cref="Extra"/> for format-specific key/value settings.
    /// </summary>
    public sealed class ExportOptions
    {
        /// <summary>Format-specific extension properties (e.g. "IncludeImages", "EmbedFonts").</summary>
        public Dictionary<string, object> Extra { get; } = new Dictionary<string, object>();
    }
}
