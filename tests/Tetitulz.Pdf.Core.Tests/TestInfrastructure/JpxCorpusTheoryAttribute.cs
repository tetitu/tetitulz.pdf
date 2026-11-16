// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.TestInfrastructure;

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
