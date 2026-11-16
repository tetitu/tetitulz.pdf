// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A caller's numbers are refused before they reach a content stream (CLAUDE.md: one check,
/// naming the parameter and the options member). <c>BaseMatrix</c> is the one numeric option the
/// plugin reads straight into every emitted coordinate: a NaN in it made every number the emitter
/// wrote come out as <c>0</c>, reported as a non-finite coordinate — a blank drawing returned as a
/// successful conversion for what is an argument error.
/// </summary>
public sealed class OptionsValidationTests
{
    private static readonly byte[] Program = Encoding.ASCII.GetBytes("newpath 0 0 moveto 10 10 lineto stroke");

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ConvertFragment_RefusesANonFiniteBaseMatrix(double bad)
    {
        var options = new PostScriptConversionOptions { BaseMatrix = new[] { 1, 0, 0, bad, 0, 0 } };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PostScriptConversion.ConvertFragment(Program, options));
        Assert.Contains("BaseMatrix", ex.Message);
    }

    [Fact]
    public void ConvertDocument_RefusesANonFiniteBaseMatrix()
    {
        var options = new PostScriptConversionOptions { BaseMatrix = new[] { double.NaN, 0, 0, 1, 0, 0 } };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PostScriptConversion.ConvertDocument(new MemoryStream(Program), new MemoryStream(), options));
        Assert.Contains("BaseMatrix", ex.Message);
    }

    /// <summary>A matrix that is not six numbers is not a matrix.</summary>
    [Fact]
    public void ConvertFragment_RefusesABaseMatrixOfTheWrongLength()
    {
        var options = new PostScriptConversionOptions { BaseMatrix = new[] { 1.0, 0, 0, 1 } };

        var ex = Assert.Throws<ArgumentException>(
            () => PostScriptConversion.ConvertFragment(Program, options));
        Assert.Contains("BaseMatrix", ex.Message);
    }

    /// <summary>The control: a finite matrix is applied, as the first emitted <c>cm</c>.</summary>
    [Fact]
    public void ConvertFragment_AppliesAFiniteBaseMatrix()
    {
        var options = new PostScriptConversionOptions { BaseMatrix = new[] { 2.0, 0, 0, 2, 5, 7 } };

        var result = PostScriptConversion.ConvertFragment(Program, options);

        // The line's end is emitted in user space; the base matrix is the renderer's business, and
        // the conversion runs to completion.
        Assert.Equal(new[] { 10.0, 10.0 }, ContentStreamAssert.FirstOperandsOf(result.Content, "l"));
        Assert.True(result.IsClean);
    }
}
