// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.TestInfrastructure;

/// <summary>
/// A <c>[Theory]</c> that <b>skips</b> — visibly — when the HTJ2K corpus is not present
/// (<see cref="TestCorpus.HtJpxRoot"/>), as <see cref="JpxCorpusFactAttribute"/> does for the reference decoder's.
/// </summary>
public sealed class HtJpxCorpusTheoryAttribute : TheoryAttribute
{
    public HtJpxCorpusTheoryAttribute()
    {
        if (TestCorpus.HtJpxRoot == null)
            Skip = TestCorpus.HtJpxUnavailableReason;
    }
}

/// <summary>
/// A <c>[Theory]</c> that <b>skips</b> — visibly — when the ISO HTJ2K conformance codestreams are not
/// present (<see cref="TestCorpus.IsoHtJpxRoot"/>).
/// </summary>
public sealed class IsoHtJpxCorpusTheoryAttribute : TheoryAttribute
{
    public IsoHtJpxCorpusTheoryAttribute()
    {
        if (TestCorpus.IsoHtJpxRoot == null)
            Skip = TestCorpus.IsoHtJpxUnavailableReason;
    }
}
