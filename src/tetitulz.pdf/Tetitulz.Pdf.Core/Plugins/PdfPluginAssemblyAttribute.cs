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
    /// Assembly-level marker attribute. Apply to plugin assemblies so that
    /// <see cref="PdfPluginHost.RegisterFromDirectory"/> can quickly identify
    /// candidate assemblies and skip unrelated ones.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class PdfPluginAssemblyAttribute : Attribute { }
}
