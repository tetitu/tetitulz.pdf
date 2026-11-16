// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// The tests that reset and register on the process-wide <c>PdfPluginHost</c> run one at a time.
/// Two classes did so in parallel — xUnit makes each class its own collection — and one's
/// <c>ResetForTesting</c> could clear the other's registration between its register and its
/// dispatch, failing an import "found by format key" about one whole-solution run in several.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginHostCollection
{
    public const string Name = "HtmlPluginHost";
}
