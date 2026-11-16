// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> for the guards that read this repository's test sources off disk
/// (<see cref="NoHardcodedPathsTests"/>, <see cref="NoUnfalsifiableAssertionsTests"/>). It skips —
/// <b>visibly, with a reason</b> — when the repo is not present, e.g. when the assemblies are run
/// from somewhere other than a checkout.
///
/// <para><b>Why this exists.</b> Both guards previously opened with
/// <c>if (TestsRoot == null) return;</c>, and <see cref="TestSourceScanner"/>'s own documentation
/// claimed that made them "skip (visibly)". It did not: an early <c>return</c> from a
/// <c>[Fact]</c> reports <b>Passed</b>. That is banned test pattern 2 from CLAUDE.md — the exact
/// rule <see cref="NoHardcodedPathsTests.NoTestSilentlyReturnsWhenItsInputFileIsMissing"/> exists
/// to enforce on everyone else — committed by the enforcers, and a guard that reports green when
/// it could not run is the failure mode this whole area is about.</para>
///
/// <para>Modelled on <see cref="CorpusFactAttribute"/>, which fixed the same defect for
/// corpus-dependent tests.</para>
/// </summary>
public sealed class RepoSourceFactAttribute : FactAttribute
{
    public RepoSourceFactAttribute()
    {
        if (TestSourceScanner.TestsRoot == null)
            Skip = "The repository's tests/ directory is not present, so the test sources " +
                   "cannot be scanned. This guard only has teeth inside a checkout.";
    }
}
