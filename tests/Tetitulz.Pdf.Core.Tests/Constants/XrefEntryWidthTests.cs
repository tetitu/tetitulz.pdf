// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Constants;

/// <summary>
/// <see cref="PdfXrefFormat.Entry"/> must produce exactly <see cref="PdfXrefFormat.EntryLength"/>
/// bytes for every input it accepts, and refuse the inputs it cannot.
/// </summary>
/// <remarks>
/// <para><b>The 10-digit offset ceiling is the shared helper's</b> — the one this type's own doc
/// comment tells every writer to use — not <c>PdfWriter</c>'s alone. An 11-digit offset would make
/// a <b>21-byte</b> row
/// against the <see cref="PdfXrefFormat.EntryLength"/> invariant this very type declares, and a
/// classic cross-reference table is read by fixed-width position: one long row shifts the meaning of
/// every entry after it, so the damage is to the whole table rather than to one object.</para>
///
/// <para>Refusing is the right answer rather than clamping. A clamped offset points at the wrong
/// bytes, which is a corrupt document that <i>looks</i> readable; the exception says the file needs
/// a cross-reference stream, which is true and actionable.</para>
/// </remarks>
public class XrefEntryWidthTests
{
    /// <summary>Every accepted input produces a row of exactly the declared width.</summary>
    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 0)]
    [InlineData(9L, 65535)]
    [InlineData(1234567890L, 12345)]
    [InlineData(PdfXrefFormat.MaxOffset, 0)]
    [InlineData(PdfXrefFormat.MaxOffset, PdfXrefFormat.MaxGenerationNumber)]
    public void AnAcceptedEntryIsExactlyTheDeclaredWidth(long offset, int generation)
        => Assert.Equal(PdfXrefFormat.EntryLength, PdfXrefFormat.Entry(offset, generation).Length);

    /// <summary>
    /// <b>The defect.</b> An offset past the ten-digit field is refused rather than written as an
    /// oversized row.
    /// </summary>
    [Theory]
    [InlineData(10_000_000_000L)]
    [InlineData(99_999_999_999L)]
    [InlineData(long.MaxValue)]
    public void AnOffsetTooLargeForTheFixedWidthFieldIsRefused(long offset)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PdfXrefFormat.Entry(offset));

        Assert.Contains("cross-reference stream", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The boundary, from both sides — without which the theory above would pass for a guard that
    /// rejected everything, and the width theory would pass for one that rejected nothing.
    /// </summary>
    [Fact]
    public void TheCeilingIsInclusiveAndOneByteBeyondItIsNot()
    {
        Assert.Equal(PdfXrefFormat.EntryLength, PdfXrefFormat.Entry(PdfXrefFormat.MaxOffset).Length);
        Assert.Throws<InvalidOperationException>(() => PdfXrefFormat.Entry(PdfXrefFormat.MaxOffset + 1));
    }

    /// <summary>
    /// A generation past the five-digit field is <b>clamped</b>, not refused — the pre-existing
    /// contract, restated so the new offset behaviour cannot be mistaken for a general policy.
    /// </summary>
    /// <remarks>
    /// The two differ for a reason worth keeping: a clamped generation still names a plausible
    /// object and only 65535 is reachable in practice, whereas a clamped <i>offset</i> points at
    /// arbitrary bytes.
    /// </remarks>
    [Theory]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void AnOutOfRangeGenerationIsClampedRatherThanRefused(int generation)
    {
        string entry = PdfXrefFormat.Entry(100, generation);

        Assert.Equal(PdfXrefFormat.EntryLength, entry.Length);
        Assert.Equal(generation < 0 ? "00000" : "65535", entry.Substring(11, 5));
    }

    /// <summary>The free-list head is the same width, since it is written beside real rows.</summary>
    [Fact]
    public void TheFreeListHeadIsTheSameWidth()
        => Assert.Equal(PdfXrefFormat.EntryLength, PdfXrefFormat.FreeListHead.Length);
}
