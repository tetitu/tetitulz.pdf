// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Tests that change a <c>PdfLimits</c> value, which is <b>process-global</b>.
/// </summary>
/// <remarks>
/// The same collection as Core's, Base's, Maintenance's and the rest, for the same reason: a ceiling one test
/// lowers is a ceiling every concurrently running test sees. A class that <i>writes</i> a limit joins this and
/// restores it with <c>PdfLimits.ResetToDefaults()</c> in a <c>finally</c>.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PdfLimitsCollection
{
    public const string Name = "PdfLimits (process-global settings)";
}
