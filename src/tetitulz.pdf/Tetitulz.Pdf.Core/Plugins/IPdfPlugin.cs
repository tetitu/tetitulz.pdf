// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Base interface that all Tetitulz.Pdf plugins must implement.
    /// Plugins are loaded by <see cref="PdfPluginHost"/> and can participate
    /// in the processing pipeline via hook points.
    /// </summary>
    public interface IPdfPlugin
    {
        /// <summary>Unique name identifying this plugin (e.g. "MyCompany.WatermarkPlugin").</summary>
        string Name { get; }

        /// <summary>Human-readable description of the plugin's function.</summary>
        string Description { get; }

        /// <summary>Plugin version (SemVer recommended).</summary>
        Version Version { get; }

        /// <summary>
        /// Called once when the plugin is registered with a <see cref="PdfPluginHost"/>.
        /// Use to allocate resources or validate configuration.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Called when the plugin is unregistered or the host is disposed.
        /// Use to release resources.
        /// </summary>
        void Shutdown();
    }
}
