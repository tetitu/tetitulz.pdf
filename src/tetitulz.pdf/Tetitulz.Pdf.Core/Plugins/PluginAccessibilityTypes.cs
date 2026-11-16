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
    /// <summary>A single accessibility issue found by an <see cref="IAccessibilityPlugin"/>.</summary>
    public sealed class PluginAccessibilityViolation
    {
        /// <summary>Short issue code (e.g. "WCAG2.1-1.1.1").</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Human-readable description of the issue.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>1-based page number, or 0 for document-level issues.</summary>
        public int PageNumber { get; set; }
    }

    /// <summary>
    /// Options controlling which accessibility fixes an <see cref="IAccessibilityPlugin"/> applies.
    /// </summary>
    public sealed class PluginAccessibilityFixOptions
    {
        /// <summary>Add missing /Alt text entries for images.</summary>
        public bool AddAltText { get; set; } = true;

        /// <summary>Tag unmarked content.</summary>
        public bool TagContent { get; set; } = true;

        /// <summary>Set document language if missing.</summary>
        public bool SetLanguage { get; set; } = true;

        /// <summary>Language code to use when <see cref="SetLanguage"/> is true (e.g. "en-US").</summary>
        public string LanguageCode { get; set; } = "en";
    }
}
