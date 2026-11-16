// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="Latin1Encoding"/>: the one-byte-per-character mapping both ways, through the
/// <see cref="Encoding"/> surface a stored decoder calls — the same answers as
/// <see cref="Latin1Text"/>, and a character Latin-1 cannot hold replaced, not dropped.
/// </summary>
public class Latin1EncodingTests
{
    private static readonly Encoding Latin1 = new Latin1Encoding();

    [Fact]
    public void DecodesEveryByteToItsCodePoint_AsLatin1TextDoes()
    {
        var bytes = new byte[256];
        for (int i = 0; i < 256; i++) bytes[i] = (byte)i;

        string s = Latin1.GetString(bytes);
        Assert.Equal(256, s.Length);
        for (int i = 0; i < 256; i++) Assert.Equal((char)i, s[i]);
        Assert.Equal(Latin1Text.Decode(bytes, 0, bytes.Length), s);

        // The offset overload, which GetString(byte[]) reaches through GetChars.
        Assert.Equal("été", Latin1.GetString(new byte[] { 0x00, 0xE9, 0x74, 0xE9, 0x00 }, 1, 3));
        Assert.Equal(3, Latin1.GetCharCount(bytes, 0, 3));
    }

    [Fact]
    public void EncodesACodePointBelow256ToItsByte_AndReplacesOneAbove()
    {
        byte[] bytes = Latin1.GetBytes("Aé€");   // A, é, €
        Assert.Equal(new byte[] { 0x41, 0xE9, (byte)'?' }, bytes);
        Assert.Equal(3, Latin1.GetByteCount("Aé€"));
        Assert.Equal(7, Latin1.GetMaxByteCount(7));
        Assert.Equal(7, Latin1.GetMaxCharCount(7));
        Assert.Equal("iso-8859-1", Latin1.WebName);
    }
}
