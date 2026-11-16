// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A <c>lut16Type</c> header is 52 bytes, four more than <c>lut8Type</c>'s: a tag of 48 to 51 bytes
/// that ends the profile holds no entry counts, and is refused with a reason instead of read past
/// the end of the data.
/// </summary>
public class IccLutShortHeaderTests
{
    private static byte[] Tag(string type, int bytes)
    {
        var data = new byte[bytes];
        Encoding.ASCII.GetBytes(type).CopyTo(data, 0);
        data[8] = 3;    // input channels
        data[9] = 3;    // output channels
        data[10] = 2;   // grid points
        return data;
    }

    [Theory]
    [InlineData(48)]
    [InlineData(51)]
    public void ALut16TagShorterThanItsHeaderAtTheEndOfTheData_IsRefused_Hostile(int bytes)
    {
        byte[] data = Tag("mft2", bytes);

        IccLut lut = IccLut.Parse(data, 0, bytes, labPcs: false, out string reason);

        Assert.Null(lut);
        Assert.Contains("lut16Type header", reason);
    }

    [Fact]
    public void ALut16TagHoldingItsHeader_IsJudgedByItsTables()
    {
        // The control: with the header whole the refusal is the tables', which the tag does not hold.
        byte[] data = Tag("mft2", 52);
        data[49] = 2; data[51] = 2;   // two entries in each input and output table

        Assert.Null(IccLut.Parse(data, 0, 52, labPcs: false, out string reason));
        Assert.Contains("bytes of tables", reason);
    }
}
