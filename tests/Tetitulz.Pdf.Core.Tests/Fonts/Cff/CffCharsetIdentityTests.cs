// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// <see cref="CffCharsetIdentity"/> makes a CID-keyed CFF's charset the identity in the program's own
/// bytes, and leaves alone what is the identity already or has no charset indirection at all.
/// </summary>
public class CffCharsetIdentityTests
{
    /// <summary>
    /// A charset giving glyphs 1 to 3 the CIDs 10, 20 and 30 is rewritten as the one range 1..3 over
    /// the same bytes: the program keeps its length, its glyph count and its CID keying, and the
    /// parser now maps CID <i>i</i> to glyph <i>i</i>. Bare and inside an <c>'OTTO'</c> sfnt alike.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACidKeyedProgram_GetsAnIdentityCharset_InItsOwnBytes(bool wrapped)
    {
        byte[] program = MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20, 30 });
        if (wrapped) program = MinimalOpenTypeCff.WrapInOtto(program, "ABC");
        Assert.Equal(1, CffParser.Parse(program).CharCodeToGid[10]);

        byte[] rewritten = CffCharsetIdentity.Rewrite(program, out string reason);

        Assert.True(rewritten != null, reason);
        Assert.NotSame(program, rewritten);
        Assert.Equal(program.Length, rewritten.Length);
        CffFont parsed = CffParser.Parse(rewritten);
        Assert.True(parsed.IsCidKeyed);
        Assert.Equal(4, parsed.Charstrings.Count);
        Assert.Equal(1, parsed.CharCodeToGid[1]);
        Assert.Equal(2, parsed.CharCodeToGid[2]);
        Assert.Equal(3, parsed.CharCodeToGid[3]);
        Assert.False(parsed.CharCodeToGid.ContainsKey(10));
    }

    /// <summary>A charset that is the identity already, and a program with no CID keying, come back as they are.</summary>
    [Fact]
    public void AnIdentityCharsetAndANonCidProgram_AreLeftAsTheyAre()
    {
        byte[] identity = MinimalOpenTypeCff.BuildCidKeyed(new[] { 1, 2, 3 });
        Assert.Same(identity, CffCharsetIdentity.Rewrite(identity, out _));

        byte[] plain = Tetitulz.Pdf.Core.Tests.Writing.Type0FontSubsetTagTests.BuildFont(asCff: true);
        Assert.Same(plain, CffCharsetIdentity.Rewrite(plain, out _));
    }
}
