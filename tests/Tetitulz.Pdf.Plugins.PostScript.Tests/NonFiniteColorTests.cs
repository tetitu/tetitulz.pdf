// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>setcolor</c> with a non-finite component is a <c>rangecheck</c>, raised where the components are
/// consumed, not carried into a colour-space conversion. A NaN or ±∞ component (<c>1e308 1e308 mul</c>,
/// or a CIEBased Decode of <c>Infinity − Infinity</c>) would reach a CIE Decode table that indexes
/// <c>(int)pos</c>; <c>(int)NaN</c> is runtime-dependent — <c>int.MinValue</c> on the netstandard2.0 leg,
/// which throws <c>IndexOutOfRangeException</c> and (uncaught by <c>PsInterpreter.Run</c>) blanks the
/// whole document, while .NET 10 saturates to 0 and carries on, so the result would depend on the host.
/// </summary>
public class NonFiniteColorTests
{
    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private const string Fill = "0 0 moveto 100 0 lineto 100 100 lineto closepath fill\n";

    [Fact]
    public void AnInfiniteSetcolorComponent_IsReported_NotSilentlySaturated()
    {
        // 1e308 1e308 mul overflows to +Infinity (mul does not trap); the first setcolor component.
        PostScriptConversionResult bad =
            Convert("%!PS\n/DeviceRGB setcolorspace\n1e308 1e308 mul 0 0 setcolor\n" + Fill);

        Assert.False(bad.IsClean, "a non-finite colour component must be reported, not silently accepted");
    }

    [Fact]
    public void AnOrdinaryColour_ConvertsClean()
    {
        PostScriptConversionResult good =
            Convert("%!PS\n/DeviceRGB setcolorspace\n0.2 0.4 0.6 setcolor\n" + Fill);

        Assert.True(good.IsClean, "a finite colour is not a rangecheck");
    }
}
