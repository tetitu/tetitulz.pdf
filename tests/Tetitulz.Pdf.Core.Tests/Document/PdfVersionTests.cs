// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Core.Tests.Document;

public class PdfVersionTests
{
    [Theory]
    [InlineData("2.0", 2, 0)]
    [InlineData("1.7", 1, 7)]
    [InlineData("0.9", 0, 9)]
    [InlineData("10.0", 10, 0)]
    public void TryParse_WellFormedVersion_ParsesMajorMinor(string text, int major, int minor)
    {
        Assert.True(PdfVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    [InlineData(".0")]
    [InlineData("2.")]
    [InlineData("2.0.1")]
    [InlineData("two.zero")]
    [InlineData("-1.0")]
    [InlineData("2.-1")]
    public void TryParse_MalformedInput_ReturnsFalseWithoutThrowing(string text)
    {
        Assert.False(PdfVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    [Fact]
    public void Comparison_HigherMinorSameMajor_IsGreater()
    {
        Assert.True(new PdfVersion(1, 7) < new PdfVersion(2, 0));
        Assert.True(new PdfVersion(2, 0) > new PdfVersion(1, 7));
    }
}
