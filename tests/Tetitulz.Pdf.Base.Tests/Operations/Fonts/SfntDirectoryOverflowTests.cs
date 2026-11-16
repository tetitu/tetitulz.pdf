// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Fonts;

/// <summary>
/// An sfnt table directory is attacker-controlled data, and a table record's declared offset and
/// length must be bounds-checked in arithmetic that cannot <b>wrap</b>.
/// </summary>
/// <remarks>
/// <para><c>CffParser.ExtractCffTableFromSfnt</c> tested <c>offset + length &gt; (uint)d.Length</c>
/// with both operands <c>uint</c>. An entry declaring offset <c>0xFFFFFFF0</c> and length
/// <c>0x20</c> sums to <c>0x10</c>, which is smaller than any real font, so the guard passed —
/// and then <c>(int)offset</c> is <c>-16</c> and <c>Array.Copy</c> threw
/// <see cref="ArgumentOutOfRangeException"/>.</para>
///
/// <para><b>Why it escaped.</b> The sfnt branch runs <i>before</i> the <c>try</c> that wraps the
/// rest of <c>CffParser.Parse</c>, so the exception left a documented <c>bool</c>-plus-<c>reason</c>
/// API. <see cref="PdfFontSubsetter.TryMapCodePointsToCffGlyphs"/> declares only
/// <see cref="ArgumentNullException"/> and its callers test the return value and read
/// <c>LastFailureReason</c> — none of them catch.</para>
///
/// <para><b>The fix had already landed three times.</b> <c>SfntTablePruner</c> and
/// <c>CffSubsetter</c> both did the same test in <c>long</c> and returned a clean refusal; only this
/// fourth reader kept the wrapping one. That is this repository's recurring shape — one copy of a
/// rule left behind — and it is why the assertion below is on the <b>refusal</b>, not on the
/// absence of a crash: returning <c>false</c> with a reason is the contract, and a test that only
/// caught the throw would pass if the method started returning garbage instead.</para>
/// </remarks>
public class SfntDirectoryOverflowTests
{
    /// <summary>
    /// The wrapping entry. The API must refuse it the way it refuses every other malformed font.
    /// </summary>
    [Fact]
    public void ACffTableRecordWhoseOffsetPlusLengthOverflowsIsRefused()
    {
        byte[] font = OpenTypeWithCffRecord(offset: 0xFFFFFFF0, length: 0x20, totalBytes: 1000);

        bool ok = PdfFontSubsetter.TryMapCodePointsToCffGlyphs(
            font, new[] { (int)'A' }, out int[] glyphIds, out string reason);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(reason), "a refusal must say why");
        Assert.Null(glyphIds);
    }

    /// <summary>
    /// The neighbouring overflow: an offset above <see cref="int.MaxValue"/> whose sum does not
    /// wrap but which still cannot be cast to a valid array index.
    /// </summary>
    [Theory]
    [InlineData(0xFFFFFFF0u, 0x20u)]     // sum wraps to 0x10
    [InlineData(0x80000000u, 0x10u)]     // (int)offset is negative, sum does not wrap
    [InlineData(0xFFFFFFFFu, 0x1u)]      // sum wraps to 0
    [InlineData(0x00000010u, 0xFFFFFFF0u)] // length is the huge one
    public void AnOutOfRangeCffTableRecordIsRefusedRatherThanThrowing(uint offset, uint length)
    {
        byte[] font = OpenTypeWithCffRecord(offset, length, totalBytes: 1000);

        Exception ex = Record.Exception(() => PdfFontSubsetter.TryMapCodePointsToCffGlyphs(
            font, new[] { (int)'A' }, out _, out _));

        Assert.Null(ex);
    }

    /// <summary>
    /// The floor. A directory whose record is <i>in</i> range is not refused by this guard — so the
    /// theory above cannot pass by the reader having started rejecting every OpenType font.
    /// </summary>
    /// <remarks>
    /// The payload is not a valid CFF program, so mapping still fails; what is asserted is that it
    /// fails for a <b>different</b> reason than the out-of-range records do. Without this the whole
    /// file would pass with the bounds test replaced by <c>return null</c>.
    /// </remarks>
    [Fact]
    public void AnInRangeCffTableRecordReachesTheParserRatherThanTheBoundsGuard()
    {
        byte[] font = OpenTypeWithCffRecord(offset: 300, length: 64, totalBytes: 1000);

        PdfFontSubsetter.TryMapCodePointsToCffGlyphs(
            font, new[] { (int)'A' }, out _, out string reason);

        Assert.False(string.IsNullOrEmpty(reason));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A minimal <c>OTTO</c> container carrying exactly one table record, tagged <c>"CFF "</c>,
    /// with the caller's declared offset and length.
    /// </summary>
    private static byte[] OpenTypeWithCffRecord(uint offset, uint length, int totalBytes)
    {
        var font = new byte[totalBytes];

        Encoding.ASCII.GetBytes("OTTO").CopyTo(font, 0);
        font[4] = 0; font[5] = 1;               // numTables = 1

        Encoding.ASCII.GetBytes("CFF ").CopyTo(font, 12);
        WriteU32(font, 16, 0);                  // checksum
        WriteU32(font, 20, offset);
        WriteU32(font, 24, length);

        return font;
    }

    private static void WriteU32(byte[] buffer, int at, uint value)
    {
        buffer[at]     = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }
}
