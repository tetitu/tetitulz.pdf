// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A Type 1 program whose built-in encoding is the named <c>StandardEncoding</c> (Type 1 Font Format §2.3;
/// the usual shape of a standard text face) has that encoding. Only <c>dup N /name put</c> entries were read,
/// so the program came back with 256 <c>.notdef</c>: every code not in a PDF <c>/Differences</c> resolved to
/// no glyph — drawn as nothing, and judged an unnamed code by PDF/A's A86 and PDF/UA-2.
/// </summary>
public sealed class Type1StandardEncodingTests
{
    private const string ArrayEncoding = "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\nreadonly def\n";

    private static (byte[] Program, int Length1, int Length2) WithEncoding(string encodingText)
    {
        var (program, length1, length2) = MinimalType1.Build(new List<(string, byte[])>
        {
            (".notdef", MinimalType1.AdvanceOnly(250)),
            ("A", MinimalType1.AdvanceOnly(600)),
        });
        string clear = Encoding.ASCII.GetString(program, 0, length1);
        Assert.Contains(ArrayEncoding, clear, StringComparison.Ordinal);
        byte[] newClear = Encoding.ASCII.GetBytes(clear.Replace(ArrayEncoding, encodingText));
        var rebuilt = new byte[newClear.Length + program.Length - length1];
        Buffer.BlockCopy(newClear, 0, rebuilt, 0, newClear.Length);
        Buffer.BlockCopy(program, length1, rebuilt, newClear.Length, program.Length - length1);
        return (rebuilt, newClear.Length, length2);
    }

    [Fact]
    public void ANamedStandardEncoding_IsTheStandardEncoding()
    {
        var (program, l1, l2) = WithEncoding("/Encoding StandardEncoding def\n");

        var font = Type1Parser.Parse(program, l1, l2);

        Assert.Equal("A", font.Encoding[65]);
        Assert.Equal("space", font.Encoding[32]);
        Assert.Equal(".notdef", font.Encoding[1]);
    }

    /// <summary>The control: an encoding array keeps exactly what it puts.</summary>
    [Fact]
    public void AnEncodingArray_KeepsWhatItPuts()
    {
        var (program, l1, l2) = WithEncoding("/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 66 /A put\nreadonly def\n");

        var font = Type1Parser.Parse(program, l1, l2);

        Assert.Equal("A", font.Encoding[66]);
        Assert.Equal(".notdef", font.Encoding[65]);
    }
}
