// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.TestInfrastructure;

/// <summary>
/// A <c>[Fact]</c> that <b>skips</b> — visibly — when the JPEG 2000 corpus is not present
/// (<see cref="TestCorpus.JpxRoot"/>: a sparse clone of the reference decoder's test data).
///
/// <para>Modelled on <c>Base.Tests</c>' <c>CorpusFact</c>, for the same reason: the alternative,
/// an early <c>return</c> when the folder is missing, reports <b>Passed</b> while executing nothing —
/// banned test pattern 2. The corpus is not committed, so Skipped is the honest outcome on a clean
/// checkout.</para>
/// </summary>
public sealed class JpxCorpusFactAttribute : FactAttribute
{
    public JpxCorpusFactAttribute()
    {
        if (TestCorpus.JpxRoot == null)
            Skip = TestCorpus.JpxUnavailableReason;
    }
}
