// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// The tests that register this plugin on the process-wide <c>PdfPluginHost</c> run one at a time:
/// each class unregisters by the plugin's name, so two in parallel could remove each other's
/// registration between a register and a dispatch.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginHostCollection
{
    public const string Name = "JavaScriptPluginHost";
}
