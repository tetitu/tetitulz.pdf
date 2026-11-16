// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Serialises the Base test classes that redirect <c>TETITULZ_PDF_FONTDIR</c> and reset
/// <c>FontFileLocator</c>'s process-wide cache.
/// </summary>
/// <remarks>
/// <para>The twin of <c>Core.Tests.Fonts.SystemFontCacheCollection</c>, and duplicated rather than
/// shared for the reason every other per-project test helper here is: cross-project test
/// dependencies are banned by CLAUDE.md. The hazard is the same one that file describes — the
/// environment variable and the locator's cache are <b>process-wide</b>, so a class that swaps the
/// font search path is making a claim about the whole process for as long as it runs, and xUnit
/// runs different classes in parallel by default.</para>
///
/// <para><b>An env-var swap is worse than a cache reset, not better.</b> A reset makes a neighbour
/// re-read; a swap makes it read the wrong directory and get a wrong <i>answer</i> — a resolution
/// that succeeds against a one-font fixture directory rather than against the host. That fails as a
/// plausible result rather than as an obvious one.</para>
///
/// <para>Membership stays narrow, same rule as Core's: a class belongs here only if it writes
/// <c>FontFileLocator.FontDirEnvVar</c> or calls <c>ResetCache</c>. "Font tests" is not the
/// criterion — widening it would serialise work with no reason to be serial.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SystemFontCacheCollection
{
    public const string Name = "SystemFontCache";
}
