// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Resolving a <c>setcolorspace</c> operand is bounded in depth. A space's array can name itself
/// (<c>a 1 a put</c> makes <c>a[1]</c> reference <c>a</c>, which no array setter checks) or be built
/// thousands deep by a loop. Descended by C# recursion that never returns to the interpreter's step loop,
/// it would leave the work budget unable to tick, and a <c>StackOverflowException</c>, which no handler
/// can catch, would take the host process down.
/// </summary>
public class ColorSpaceDepthTests
{
    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    /// <summary>
    /// An <c>/Indexed</c> space whose base is itself resolves to a declined space instead of recursing
    /// forever; the conversion returns.
    /// </summary>
    [Fact]
    public void ACyclicIndexedColorSpace_DoesNotOverflow()
    {
        // `a 1 a put` makes a[1] (the /Indexed base) reference the array itself.
        var result = Convert("%!PS\n/a [ /Indexed /DeviceGray 0 <00> ] def  a 1 a put  a setcolorspace\n");

        Assert.NotNull(result);
    }

    /// <summary>
    /// An <c>/Indexed</c> space nested far deeper than any real one — built by rebinding a name in a
    /// loop — resolves to a declined space rather than overflowing the stack during descent.
    /// </summary>
    [Fact]
    public void ADeeplyNestedColorSpace_DoesNotOverflow()
    {
        var result = Convert(
            "%!PS\n/a [ /DeviceGray ] def\n" +
            "1000 { /a [ /Indexed a 0 <00> ] def } repeat\n" +
            "a setcolorspace\n");

        Assert.NotNull(result);
    }

    /// <summary>The control: an ordinary <c>/Indexed</c> space over DeviceGray resolves and paints.</summary>
    [Fact]
    public void AnOrdinaryIndexedColorSpace_Resolves()
    {
        var result = Convert("%!PS\n[ /Indexed /DeviceGray 1 <0080> ] setcolorspace\n1 setcolor\n" +
                             "0 0 moveto 100 0 lineto 100 100 lineto closepath fill\nshowpage\n");

        Assert.NotNull(result);
        string content = Encoding.ASCII.GetString(result.Content);
        // index 1 selects palette entry 0x80, resolved through the /Indexed base DeviceGray to
        // 0x80/255 = 0.501961 — a resolved space, not the black a declined one would paint — and filled.
        Assert.Contains("0.501961 g", content, StringComparison.Ordinal);
        Assert.Contains("\nf\n", content, StringComparison.Ordinal);
    }
}
