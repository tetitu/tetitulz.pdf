// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// A <c>[Fact]</c> that <b>skips</b> — visibly — when the JPEG 2000 corpus is not present
/// (<see cref="TestCorpus.JpxRoot"/>: a sparse clone of the reference decoder's test data).
///
/// <para>The <see cref="CorpusFactAttribute"/> rule for the other corpus, and Core's
/// <c>JpxCorpusFactAttribute</c> mirrored here for the consumers' tests: an early <c>return</c>
/// when the folder is missing reports <b>Passed</b> while executing nothing — banned test pattern
/// 2. The corpus is not committed, so Skipped is the honest outcome on a clean checkout.
/// Duplicated per test project because cross-project test dependencies are banned.</para>
/// </summary>
public sealed class JpxCorpusFactAttribute : FactAttribute
{
    public JpxCorpusFactAttribute()
    {
        if (TestCorpus.JpxRoot == null)
            Skip = TestCorpus.JpxUnavailableReason;
    }
}

/// <summary>
/// A <c>[Theory]</c> that skips, visibly, when the JPEG 2000 corpus is not present — the
/// <see cref="JpxCorpusFactAttribute"/> rule for a data-driven test, so each corpus file is its
/// own row and a missing corpus reports every row as Skipped rather than Passed.
/// </summary>
public sealed class JpxCorpusTheoryAttribute : TheoryAttribute
{
    public JpxCorpusTheoryAttribute()
    {
        if (TestCorpus.JpxRoot == null)
            Skip = TestCorpus.JpxUnavailableReason;
    }
}
