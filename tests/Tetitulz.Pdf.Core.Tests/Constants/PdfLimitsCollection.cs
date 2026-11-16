// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Constants;

/// <summary>
/// Tests that change a <c>PdfLimits</c> value, which is <b>process-global</b>.
/// </summary>
/// <remarks>
/// <para>Same hazard and same remedy as <c>PdfDecryptorHolder</c> and <c>PdfPluginHost</c>: a
/// setting one test changes is a setting every concurrently running test sees. xUnit runs
/// collections in parallel, so lowering a ceiling here while another class parses a fixture would
/// make that class fail for a reason nothing in it mentions — the intermittent this repository has
/// already diagnosed three times by other routes.</para>
///
/// <para><b>Membership is narrow on purpose.</b> Only a class that <i>writes</i> a limit belongs
/// here; reading one is what every other test does and needs no serialisation. Widening it would
/// turn a targeted fix into a blanket one.</para>
///
/// <para>This cost is the honest argument for the API's own advice: set limits once at startup,
/// before any parsing, and leave them alone.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PdfLimitsCollection
{
    public const string Name = "PdfLimits (process-global settings)";
}
