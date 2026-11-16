// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests.TestInfrastructure;

/// <summary>
/// Tests that change a <c>PdfLimits</c> value, which is <b>process-global</b>.
/// </summary>
/// <remarks>
/// The same collection as Core's, Base's, Signing's and Accessibility's, for the same reason: a ceiling one test
/// lowers is a ceiling every concurrently running test sees. A class that <i>writes</i> a limit joins this and
/// restores it with <c>PdfLimits.ResetToDefaults()</c> in a <c>finally</c>.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PdfLimitsCollection
{
    public const string Name = "PdfLimits (process-global settings)";
}
