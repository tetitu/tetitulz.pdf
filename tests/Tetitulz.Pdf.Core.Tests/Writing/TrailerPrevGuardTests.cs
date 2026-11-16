// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfTrailerBuilder.IsUsablePreviousXref"/>: a <c>/Prev</c> must be a real offset and
/// must lie before the revision naming it. The zero half was tested; the upper bound was not.
/// </summary>
public class TrailerPrevGuardTests
{
    /// <summary>
    /// An offset at or past where this revision begins names no earlier section; one before it does.
    /// </summary>
    [Theory]
    [InlineData(100L, 400L, true)]
    [InlineData(400L, 400L, false)]
    [InlineData(500L, 400L, false)]
    [InlineData(0L, 400L, false)]
    public void APreviousSectionMustLieBeforeThisRevision(long previous, long thisRevision, bool usable)
    {
        Assert.Equal(usable, PdfTrailerBuilder.IsUsablePreviousXref(previous, thisRevision));
    }

    /// <summary>The appended trailer carries <c>/Prev</c> exactly when the guard allows it.</summary>
    [Theory]
    [InlineData(100L, true)]
    [InlineData(500L, false)]
    public void TheAppendedTrailerChainsOnlyBackwards(long previous, bool chained)
    {
        PdfDictionary trailer = PdfTrailerBuilder.ForAppendedRevision(new PdfDictionary(), 10, previous, 400);

        Assert.Equal(chained, trailer[PdfName.Prev] != null);
    }
}
