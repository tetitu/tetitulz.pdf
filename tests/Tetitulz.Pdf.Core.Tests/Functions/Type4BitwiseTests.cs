// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// Type 4 functions take PostScript's operators (ISO 32000-1 §7.10.5), where <c>and</c>, <c>or</c>,
/// <c>xor</c> and <c>not</c> are bitwise on integers and <c>bitshift</c> is a 32-bit shift: they were
/// logical only (<c>12 10 and</c> was 1), and <c>1 64 bitshift</c> was 1. On booleans they stay logical
/// (the controls).
/// </summary>
public sealed class Type4BitwiseTests
{
    private static float Run(string program)
    {
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        fn.Set(new PdfName("Domain"), PdfArray.OfNumbers(0, 1));
        fn.Set(new PdfName("Range"), PdfArray.OfNumbers(-1000, 1000));
        fn.RawData = Encoding.ASCII.GetBytes(program);
        return PdfFunctionEvaluator.Evaluate(fn, new[] { 0f })[0];
    }

    [Theory]
    [InlineData("{ pop 12 10 and }", 8)]
    [InlineData("{ pop 12 10 or }", 14)]
    [InlineData("{ pop 12 10 xor }", 6)]
    [InlineData("{ pop 5 not }", -6)]
    [InlineData("{ pop 1 64 bitshift }", 0)]
    [InlineData("{ pop 1 3 bitshift }", 8)]
    [InlineData("{ pop -8 -1 bitshift }", 1000)]   // a logical right shift of -8 is 2^31 - 4, past the range's 1000
    [InlineData("{ pop 64 -3 bitshift }", 8)]
    [InlineData("{ pop 1 2 lt 3 4 lt and }", 1)]   // booleans, the controls
    [InlineData("{ pop 1 2 lt not }", 0)]
    [InlineData("{ pop 2 1 lt not }", 1)]
    [InlineData("{ pop 1 2 lt 4 3 lt xor }", 1)]
    public void BitOperators_FollowPostScript(string program, float expected)
        => Assert.Equal(expected, Run(program));
}
