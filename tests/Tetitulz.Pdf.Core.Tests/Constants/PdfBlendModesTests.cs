// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Constants;

/// <summary><see cref="PdfBlendModes.IsStandard"/>: the sixteen standard modes and the Compatible alias, by exact name.</summary>
public class PdfBlendModesTests
{
    [Theory]
    [InlineData("Normal")]
    [InlineData("Compatible")]
    [InlineData("Multiply")]
    [InlineData("Screen")]
    [InlineData("Overlay")]
    [InlineData("Darken")]
    [InlineData("Lighten")]
    [InlineData("ColorDodge")]
    [InlineData("ColorBurn")]
    [InlineData("HardLight")]
    [InlineData("SoftLight")]
    [InlineData("Difference")]
    [InlineData("Exclusion")]
    [InlineData("Hue")]
    [InlineData("Saturation")]
    [InlineData("Color")]
    [InlineData("Luminosity")]
    public void EachStandardMode_IsStandard(string name) => Assert.True(PdfBlendModes.IsStandard(name));

    [Theory]
    [InlineData("Custom")]
    [InlineData("normal")]
    [InlineData("Multiply ")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsNot(string? name) => Assert.False(PdfBlendModes.IsStandard(name!));
}
