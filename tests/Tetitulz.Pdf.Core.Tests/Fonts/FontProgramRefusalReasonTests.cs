// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A font program that does not parse is a null program, as before, and now says why: too short, a WOFF container that
/// would not unwrap (its decoder's own reason), a collection, or what the reader threw. The slot belongs to the call.
/// </summary>
public class FontProgramRefusalReasonTests
{
    private static byte[] Bytes(uint signature, int length, int numTables = 0)
    {
        var b = new byte[length];
        b[0] = (byte)(signature >> 24); b[1] = (byte)(signature >> 16); b[2] = (byte)(signature >> 8); b[3] = (byte)signature;
        if (length >= 6) { b[4] = (byte)(numTables >> 8); b[5] = (byte)numTables; }
        return b;
    }

    [Theory]
    [InlineData(0x00010000u, 8, 0, "fewer than 12 bytes")]
    [InlineData(0x74746366u, 64, 0, "font collection")]
    [InlineData(0x00010000u, 12, 0xFFFF, "the sfnt could not be read")]
    [InlineData(0x774F4646u, 64, 0, "WOFF 1.0")]
    [InlineData(0x774F4632u, 64, 0, "WOFF 2.0")]
    public void ARefusedProgram_SaysWhy(uint signature, int length, int numTables, string reason)
    {
        Assert.Null(TrueTypeFontProgram.Parse(Bytes(signature, length, numTables)));
        Assert.Contains(reason, TrueTypeFontProgram.LastParseError, StringComparison.Ordinal);
    }

    /// <summary>A program that parses leaves the slot empty, after a refusal on the same thread too.</summary>
    [Fact]
    public void AProgramThatParses_LeavesNoReason()
    {
        Assert.Null(TrueTypeFontProgram.Parse(new byte[4]));
        Assert.NotNull(TrueTypeFontProgram.LastParseError);

        Assert.NotNull(TrueTypeFontProgram.Parse(Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("AB")));
        Assert.Null(TrueTypeFontProgram.LastParseError);
    }

    /// <summary>The WOFF decoders' own slots: set by a container that does not unwrap, cleared by the next call.</summary>
    [Fact]
    public void TheWoffDecoders_SayWhyAndForgetOnTheNextCall()
    {
        Assert.Null(WoffDecoder.Decode(Bytes(0x774F4646u, 64)));
        Assert.Contains("WOFF 1.0", WoffDecoder.LastError, StringComparison.Ordinal);
        Assert.Null(Woff2Decoder.Decode(Bytes(0x774F4632u, 64)));
        Assert.Contains("WOFF 2.0", Woff2Decoder.LastError, StringComparison.Ordinal);

        Assert.Null(Woff2GlyfTransform.Rebuild(new byte[8], 1 << 20).Glyf);
        Assert.Contains("glyf", Woff2GlyfTransform.LastError, StringComparison.Ordinal);

        Assert.NotNull(WoffDecoder.Decode(Tetitulz.Pdf.TestHelpers.MinimalWoff.Wrap(Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("AB"))));
        Assert.Null(WoffDecoder.LastError);
    }
}
