// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts.Cff;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A self-referential <c>'OTTO'</c> container must be refused, not recursed into.
/// </summary>
/// <remarks>
/// <para>
/// <c>CffParser.Parse</c> unwraps an sfnt container by extracting its <c>CFF </c> table and calling
/// itself. Nothing required the extracted table to be <i>smaller</i> than its container, so a table
/// directory entry with offset 0 and length equal to the file length produced a byte-for-byte copy
/// of the input — which still began with <c>'OTTO'</c>.
/// </para>
/// <para>
/// <b>This was a remote process kill, not a hang.</b> A <see cref="System.StackOverflowException"/>
/// cannot be caught in .NET Core, so <c>FontCache.LoadEmbedded</c>'s surrounding
/// <c>catch (Exception)</c> never ran and the whole host died. Rendering a single page of an
/// untrusted PDF carrying a 100-byte <c>/FontFile3</c> was enough to trigger it.
/// </para>
/// <para>
/// The assertion is on the guard's own evidence — a refusal plus the recorded reason — never on a
/// wall clock, per the repository's rule for DoS guards.
/// </para>
/// <para>
/// <b>What these tests do and do not pin.</b> The fix carries two guards — a depth cap of one
/// unwrap, and a refusal of any extracted table not strictly smaller than its container. Mutation
/// testing shows they are <i>redundant for this fixture</i>: removing the size check leaves both
/// tests green, because the depth cap catches the second pass. The depth cap is therefore the
/// load-bearing guard and the size check is defence in depth that nothing here pins. Removing
/// <i>both</i> is what reproduces the original defect, and it cannot be asserted from inside the
/// suite — it kills the test host rather than failing a test, which is precisely why the bug was a
/// process kill and not a hang.
/// </para>
/// </remarks>
public class CffSelfReferentialContainerTests
{
    [Fact]
    public void Parse_SelfReferentialOttoContainer_IsRefusedWithoutRecursing()
    {
        byte[] font = SelfReferentialOtto(totalLength: 100);

        CffFont result = CffParser.Parse(font);

        Assert.Null(result);
        Assert.Equal(CffParser.CffFailure.Malformed, CffParser.LastFailureReason);
    }

    /// <summary>A container whose declared table runs past the end of the data is refused too.</summary>
    [Fact]
    public void Parse_OttoContainerWithOversizedTable_IsRefused()
    {
        byte[] font = SelfReferentialOtto(totalLength: 100);
        // length = 0x7FFFFFFF, far past the 100 bytes present.
        font[24] = 0x7F; font[25] = 0xFF; font[26] = 0xFF; font[27] = 0xFF;

        Assert.Null(CffParser.Parse(font));
        Assert.Equal(CffParser.CffFailure.Malformed, CffParser.LastFailureReason);
    }

    /// <summary>
    /// An sfnt whose single table is tagged <c>CFF </c> at offset 0 with length == the whole file,
    /// so extracting it yields the container again.
    /// </summary>
    private static byte[] SelfReferentialOtto(int totalLength)
    {
        var d = new byte[totalLength];

        d[0] = 0x4F; d[1] = 0x54; d[2] = 0x54; d[3] = 0x4F;   // 'OTTO'
        d[4] = 0x00; d[5] = 0x01;                              // numTables = 1
        // searchRange / entrySelector / rangeShift left zero — unread by the extractor.

        int e = 12;                                            // first table directory entry
        d[e + 0] = (byte)'C'; d[e + 1] = (byte)'F';
        d[e + 2] = (byte)'F'; d[e + 3] = (byte)' ';            // tag 'CFF '
        // checksum d[e+4..7] = 0
        d[e +  8] = 0; d[e +  9] = 0; d[e + 10] = 0; d[e + 11] = 0;   // offset = 0
        d[e + 12] = (byte)(totalLength >> 24); d[e + 13] = (byte)(totalLength >> 16);
        d[e + 14] = (byte)(totalLength >> 8);  d[e + 15] = (byte)totalLength;  // length = whole file

        return d;
    }
}
