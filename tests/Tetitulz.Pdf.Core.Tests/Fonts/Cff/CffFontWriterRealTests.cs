// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// <c>CffFontWriter.WriteReal</c>: a DICT real in TN #5176's nibble encoding (Table 5), read as
/// bytes, because this library's parser accepts the wrong encoding too.
/// </summary>
public class CffFontWriterRealTests
{
    /// <summary>
    /// A negative exponent is nibble 0xC. It was written as 0xB (E) then 0xE (minus), which
    /// Skia reads as 1. A positive exponent, a negative mantissa and a plain decimal are the
    /// controls.
    /// </summary>
    [Theory]
    [InlineData(1e-5, new byte[] { 30, 0x1C, 0x05, 0xFF })]       // 1 E- 0 5
    [InlineData(1e15, new byte[] { 30, 0x1B, 0x15, 0xFF })]       // 1 E 1 5
    [InlineData(-0.5, new byte[] { 30, 0xE0, 0xA5, 0xFF })]       // - 0 . 5
    [InlineData(0.001, new byte[] { 30, 0x0A, 0x00, 0x1F })]      // 0 . 0 0 1
    public void ARealIsWrittenInTheSpecificationsNibbles(double value, byte[] expected)
    {
        using var ms = new MemoryStream();
        CffFontWriter.WriteReal(ms, value);

        Assert.Equal(expected, ms.ToArray());
    }
}
