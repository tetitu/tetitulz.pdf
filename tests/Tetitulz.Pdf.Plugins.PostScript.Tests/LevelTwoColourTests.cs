// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Level 2 colour (PLRM §4.8): <c>setcolorspace</c> makes the space's initial colour current, and
/// <c>setcolor</c> sets a colour in it — the device spaces directly, <c>/Indexed</c> through its palette,
/// <c>/Separation</c> and <c>/DeviceN</c> through the program's tint transform into the alternate space.
/// <c>setcolor</c> used to consume its operands and change nothing, so every such colour painted black.
/// </summary>
public sealed class LevelTwoColourTests
{
    private const string Square = " newpath 10 10 moveto 90 10 lineto 90 90 lineto closepath fill";

    private static byte[] Convert(string postScript)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript)).Content;

    private static string[] Ops(byte[] content) => ContentStreamAssert.Parse(content).Select(o => o.ToString()).ToArray();

    /// <summary>Whether the operations appear in this order, others between them allowed.</summary>
    private static bool InOrder(string[] ops, params string[] expected)
    {
        int at = 0;
        foreach (string op in ops)
            if (at < expected.Length && op == expected[at]) at++;
        return at == expected.Length;
    }

    private static void AssertInOrder(byte[] content, params string[] expected)
    {
        string[] ops = Ops(content);
        Assert.True(InOrder(ops, expected), $"expected, in order: {string.Join(" | ", expected)}\nemitted: {string.Join(" | ", ops)}");
    }

    [Fact]
    public void ADeviceSpacesColour_IsSetBySetcolor()
    {
        string[] rgb = Ops(Convert("/DeviceRGB setcolorspace 1 0 0 setcolor" + Square));
        Assert.True(InOrder(rgb, "1 0 0 rg", "f"), string.Join(" | ", rgb));
        string[] cmyk = Ops(Convert("/DeviceCMYK setcolorspace 0 1 1 0 setcolor" + Square));
        Assert.True(InOrder(cmyk, "0 1 1 0 k", "f"), string.Join(" | ", cmyk));
        string[] gray = Ops(Convert("/DeviceGray setcolorspace 0.25 setcolor" + Square));
        Assert.True(InOrder(gray, "0.25 g", "f"), string.Join(" | ", gray));
        Assert.DoesNotContain("setcolor", PostScriptDiagnostics.UnimplementedOperators);
    }

    /// <summary><c>setcolorspace</c> makes the space's initial colour current: black in CMYK is <c>0 0 0 1</c>.</summary>
    [Fact]
    public void Setcolorspace_MakesItsInitialColourCurrent()
    {
        string[] ops = Ops(Convert("1 0 0 setrgbcolor /DeviceCMYK setcolorspace" + Square));
        Assert.True(InOrder(ops, "0 0 0 1 k", "f"), string.Join(" | ", ops));
        Assert.DoesNotContain("setcolorspace", PostScriptDiagnostics.UnimplementedOperators);
    }

    /// <summary>An indexed colour is its palette entry in the base space; index 1 of a two-entry RGB palette is green.</summary>
    [Fact]
    public void AnIndexedColour_IsItsPaletteEntry()
    {
        const string space = "[/Indexed /DeviceRGB 1 <FF000000FF00>] setcolorspace";
        string[] one = Ops(Convert(space + " 1 setcolor" + Square));
        Assert.True(InOrder(one, "0 1 0 rg", "f"), string.Join(" | ", one));
        string[] initial = Ops(Convert(space + Square));
        Assert.True(InOrder(initial, "1 0 0 rg", "f"), string.Join(" | ", initial));      // index 0
        string[] clamped = Ops(Convert(space + " 5 setcolor" + Square));
        Assert.True(InOrder(clamped, "0 1 0 rg", "f"), string.Join(" | ", clamped));      // clamped to hival
    }

    /// <summary>
    /// A separation is painted in its alternate space through its tint transform — here a procedure
    /// turning a tint t into CMYK <c>t/2 0 t 0</c> — its initial tint being full, and the approximation
    /// said once however many colours are set.
    /// </summary>
    [Fact]
    public void ASeparation_IsPaintedInItsAlternateSpace_ThroughItsTintTransform()
    {
        const string space = "[/Separation /Orange /DeviceCMYK {dup 0.5 mul exch 0 exch 0}] setcolorspace";
        PostScriptDiagnostics.Clear();
        byte[] content = Convert(space + Square + " 0.6 setcolor" + Square + " 0.2 setcolor" + Square);
        AssertInOrder(content, "0.5 0 1 0 k", "f", "0.3 0 0.6 0 k", "f", "0.1 0 0.2 0 k", "f");
        Assert.Equal(1, PostScriptDiagnostics.ReportCount);
        Assert.Contains("alternate space", PostScriptDiagnostics.LastError, System.StringComparison.Ordinal);
    }

    /// <summary>A Level 3 function dictionary as the tint transform: FunctionType 2 from white to red, N = 2, at half.</summary>
    [Fact]
    public void ASeparationsFunctionDictionary_IsEvaluated()
    {
        string[] ops = Ops(Convert("[/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 2 >>] " +
                                   "setcolorspace 0.5 setcolor" + Square));
        Assert.True(InOrder(ops, "1 0.75 0.75 rg", "f"), string.Join(" | ", ops));   // 0.5 squared
    }

    /// <summary>A DeviceN space's tints go through its transform together: two inks into RGB, every one starting full.</summary>
    [Fact]
    public void ADeviceNColour_GoesThroughItsTransformTogether()
    {
        string[] ops = Ops(Convert("[/DeviceN [/A /B] /DeviceRGB {0}] setcolorspace" + Square + " 0.2 0.4 setcolor" + Square));
        Assert.True(InOrder(ops, "1 1 0 rg", "f", "0.2 0.4 0 rg", "f"), string.Join(" | ", ops));
    }

    /// <summary>A space this converter cannot paint — ICCBased — still says so, and leaves the colour as it was.</summary>
    [Fact]
    public void AnIccBasedColour_IsStillReported()
    {
        PostScriptDiagnostics.Clear();
        byte[] content = Convert("0 0 1 setrgbcolor [/ICCBased null] setcolorspace 0.1 0.2 0.3 setcolor" + Square);
        AssertInOrder(content, "0 0 1 rg", "f");
        Assert.Contains("setcolor", PostScriptDiagnostics.UnimplementedOperators);
    }

    /// <summary>
    /// A tint transform that never returns is stopped by the program's work budget — the child runs
    /// under the parent's, so the conversion's one budget is what it spends, and the result says it was
    /// exhausted; a transform that returns leaves the budget alone.
    /// </summary>
    [Fact]
    public void HostileTintTransform_ALoopIsStoppedByTheConversionsBudget()
    {
        PostScriptConversionResult? result = null;
        var thread = new System.Threading.Thread(() => result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "0 0 1 setrgbcolor [/Separation /X /DeviceRGB {{} loop}] setcolorspace 0.5 setcolor" + Square)));
        thread.Start();
        Assert.True(thread.Join(System.TimeSpan.FromSeconds(60)), "the loop was not stopped");
        Assert.True(result!.BudgetExhausted);

        var calm = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "[/Separation /X /DeviceRGB {pop 1 0 0}] setcolorspace 0.5 setcolor" + Square));
        Assert.False(calm.BudgetExhausted);
        AssertInOrder(calm.Content, "1 0 0 rg", "f");
    }

    /// <summary>
    /// A tint transform that leaves a dictionary open does not leave it on the program's stack: the
    /// stack is as deep after <c>setcolor</c> as before (red), not one deeper (green).
    /// </summary>
    [Fact]
    public void ATintTransformsOpenDictionary_IsNotLeftOnTheStack()
    {
        string[] ops = Ops(Convert("countdictstack /before exch def [/Separation /X /DeviceGray {1 dict begin}] setcolorspace 0.5 setcolor " +
                                   "countdictstack before eq {1 0 0 setrgbcolor} {0 1 0 setrgbcolor} ifelse" + Square));
        Assert.True(InOrder(ops, "1 0 0 rg", "f"), string.Join(" | ", ops));
    }
}
