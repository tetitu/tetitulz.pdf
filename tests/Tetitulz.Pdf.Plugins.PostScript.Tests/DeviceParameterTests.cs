// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The device parameters a PDF <c>/ExtGState</c> carries (ISO 32000-1 Table 58): <c>setoverprint</c>
/// as <c>/OP</c> and <c>/op</c>, <c>setblackgeneration</c> and <c>setundercolorremoval</c> as sampled
/// <c>/BG</c> and <c>/UCR</c>, <c>setflat</c>, <c>setsmoothness</c> and <c>setstrokeadjust</c> as
/// <c>/FL</c>, <c>/SM</c> and <c>/SA</c> — each written with <c>gs</c> as it is set and rolled back by
/// <c>grestore</c>. They were arity-table entries: a prepress file's overprinted black printed as a
/// knockout. And <c>currentcolorspace</c> / <c>currentcolor</c>, which answered null.
/// </summary>
public sealed class DeviceParameterTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private static string[] Ops(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()).ToArray();

    private static string[] StateNames(PostScriptConversionResult r)
        => Ops(r).Where(o => o.EndsWith(" gs", System.StringComparison.Ordinal)).Select(o => o.Split(' ')[0].TrimStart('/')).ToArray();

    private static PdfDictionary State(PostScriptConversionResult r, string name)
        => (PdfDictionary)((PdfDictionary)r.Resources[PdfName.ExtGState])[new PdfName(name)];

    /// <summary><c>true setoverprint</c> is <c>/OP true /op true</c>, set before the paint; <c>false</c> after it writes them false.</summary>
    [Fact]
    public void Setoverprint_IsOpAndOp_SetBeforeThePaint()
    {
        var r = Convert("true setoverprint" + Marker + " false setoverprint" + Marker);
        string[] ops = Ops(r);
        int gs = System.Array.FindIndex(ops, o => o.EndsWith(" gs", System.StringComparison.Ordinal));
        Assert.True(gs >= 0 && gs < System.Array.IndexOf(ops, "f"));

        string[] names = StateNames(r);
        Assert.Equal(2, names.Length);
        Assert.True(((PdfBoolean)State(r, names[0])[PdfName.OP]).Value);
        Assert.True(((PdfBoolean)State(r, names[0])[PdfName.op]).Value);
        Assert.False(((PdfBoolean)State(r, names[1])[PdfName.OP]).Value);
        Assert.False(((PdfBoolean)State(r, names[1])[PdfName.op]).Value);
    }

    /// <summary>Nothing set writes nothing: a stream holds the initial parameters already.</summary>
    [Fact]
    public void TheInitialParameters_WriteNothing()
    {
        var r = Convert("false setoverprint 1 setflat 0 setsmoothness false setstrokeadjust" + Marker);
        Assert.Empty(StateNames(r));
        Assert.Contains("f", Ops(r));
    }

    /// <summary><c>grestore</c> puts the parameters back with the rest of the state: no second <c>gs</c> is needed.</summary>
    [Fact]
    public void Grestore_PutsTheParametersBack()
    {
        var ops = Ops(Convert("gsave true setoverprint" + Marker + " grestore" + Marker));
        Assert.Single(ops, o => o.EndsWith(" gs", System.StringComparison.Ordinal));
        int q = System.Array.LastIndexOf(ops, "Q");
        Assert.True(q > System.Array.FindIndex(ops, o => o.EndsWith(" gs", System.StringComparison.Ordinal)) && q < System.Array.LastIndexOf(ops, "f"));
    }

    /// <summary>A widening that closes the level the parameters were set under writes them again.</summary>
    [Fact]
    public void AWidening_WritesTheParametersAgain()
        => Assert.Equal(2, StateNames(Convert("0 0 10 10 rectclip true setoverprint initclip" + Marker)).Length);

    /// <summary>The <c>current*</c> forms answer what was set.</summary>
    [Fact]
    public void TheReadbacks_AnswerWhatWasSet()
    {
        Assert.Contains("f", Ops(Convert("true setoverprint currentoverprint {" + Marker + "} if")));
        Assert.DoesNotContain("f", Ops(Convert("currentoverprint {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("5 setflat currentflat 5 eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("0.25 setsmoothness currentsmoothness 0.25 eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("/p {0.5 mul} def /p load setblackgeneration currentblackgeneration /p load eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("/p {0.5 mul} def /p load setundercolorremoval currentundercolorremoval /p load eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("currentblackgeneration length 0 eq {" + Marker + "} if")));
    }

    /// <summary><c>/FL</c>, <c>/SM</c> and <c>/SA</c> carry the values set, each written as it is set, a state holding only what changed.</summary>
    [Fact]
    public void FlatnessSmoothnessAndStrokeAdjust_AreCarried()
    {
        var r = Convert("5 setflat 0.25 setsmoothness true setstrokeadjust" + Marker);
        string[] names = StateNames(r);
        Assert.Equal(3, names.Length);
        Assert.Equal(5.0, ((PdfNumber)State(r, names[0])[PdfName.FL]).DoubleValue);
        Assert.Equal(0.25, ((PdfNumber)State(r, names[1])[PdfName.SM]).DoubleValue);
        Assert.True(((PdfBoolean)State(r, names[2])[PdfName.SA]).Value);
        Assert.Null(State(r, names[1])[PdfName.FL]);   // only what changed
    }

    /// <summary>
    /// Black generation is sampled over 0 to 1 — an empty procedure is the identity, a function, not the
    /// device's default; undercolour removal over −1 to 1 (ISO 32000-1 §10.3.4), so half of 1 is 0.75 of the way up.
    /// </summary>
    [Fact]
    public void BlackGenerationAndUnderColorRemoval_AreSampledFunctions()
    {
        var bg = Convert("{} setblackgeneration" + Marker);
        var function = Assert.IsType<PdfStream>(State(bg, StateNames(bg).Single())[PdfName.BG]);
        Assert.Equal(new[] { 0.0, 1 }, ((PdfArray)function[PdfName.Range]).Select(n => ((PdfNumber)n).DoubleValue).ToArray());
        Assert.Equal(0, function.RawData[0]);
        Assert.Equal(255, function.RawData[255]);

        var ucr = Convert("{0.5 mul} setundercolorremoval" + Marker);
        var removal = Assert.IsType<PdfStream>(State(ucr, StateNames(ucr).Single())[PdfName.UCR]);
        Assert.Equal(new[] { -1.0, 1 }, ((PdfArray)removal[PdfName.Range]).Select(n => ((PdfNumber)n).DoubleValue).ToArray());
        Assert.Equal(128, removal.RawData[0]);     // 0 → the middle of −1..1
        Assert.Equal(191, removal.RawData[255]);   // 0.5 → three quarters
    }

    /// <summary>
    /// A <c>setgstate</c> back to a state with the device's own black generation, where the stream holds
    /// a sampled one, writes <c>/BG2 /Default</c> — PDF's way back to the device's (the control: the
    /// same change of overprint writes <c>/OP</c>, and no <c>/BG2</c>).
    /// </summary>
    [Fact]
    public void SetgstateBackToTheDevicesBlackGeneration_WritesBg2Default()
    {
        var r = Convert("gstate /g exch def {} setblackgeneration" + Marker + " g setgstate" + Marker);
        string[] names = StateNames(r);
        Assert.Equal(2, names.Length);
        Assert.IsType<PdfStream>(State(r, names[0])[PdfName.BG]);
        Assert.Equal("Default", ((PdfName)State(r, names[1])[PdfName.BG2]).Value);

        var overprint = Convert("gstate /g exch def true setoverprint" + Marker + " g setgstate" + Marker);
        string last = StateNames(overprint).Last();
        Assert.False(((PdfBoolean)State(overprint, last)[PdfName.OP]).Value);
        Assert.Null(State(overprint, last)[PdfName.BG2]);
    }

    /// <summary>A procedure that is not one is a <c>typecheck</c>; one that loops spends the program's budget.</summary>
    [Fact]
    public void AHostileBlackGenerationProcedure_SpendsTheBudget()
    {
        Assert.Contains("f", Ops(Convert("{ 5 setblackgeneration } stopped { $error /errorname get /typecheck eq {" + Marker + "} if } if")));
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };
        var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("{ 1000 { 1 pop } repeat } setblackgeneration" + Marker), options);
        Assert.True(r.BudgetExhausted);
    }

    // ==================================================================
    //  currentcolorspace and currentcolor
    // ==================================================================

    /// <summary>A space set as an array is answered as that array, and the colour as the tint set — not the alternate colour painted.</summary>
    [Fact]
    public void ASeparation_IsAnsweredAsSet()
    {
        const string Space = "/cs [/Separation /Spot /DeviceCMYK {dup dup dup}] def cs setcolorspace ";
        Assert.Contains("f", Ops(Convert(Space + "currentcolorspace cs eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert(Space + "0.3 setcolor currentcolor 0.3 eq count 1 eq and {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert(Space + "currentcolor 1 eq {" + Marker + "} if")));   // the initial tint
    }

    /// <summary>A family set by name alone is answered in an array of one; its initial colour is the space's.</summary>
    [Fact]
    public void AFamilyName_IsAnsweredInAnArray()
    {
        Assert.Contains("f", Ops(Convert("/DeviceCMYK setcolorspace currentcolorspace dup length 1 eq exch 0 get /DeviceCMYK eq and {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("/DeviceCMYK setcolorspace currentcolor 1 eq exch 0 eq and exch 0 eq and exch 0 eq and {" + Marker + "} if")));
    }

    /// <summary>After <c>setrgbcolor</c> the space is <c>[/DeviceRGB]</c> and the colour its three components, whatever was set before.</summary>
    [Fact]
    public void ADeviceColour_IsAnsweredInItsDeviceSpace()
    {
        const string Before = "[/Separation /Spot /DeviceCMYK {dup dup dup}] setcolorspace 0.3 setcolor ";
        Assert.Contains("f", Ops(Convert(Before + "0.1 0.2 0.3 setrgbcolor currentcolorspace 0 get /DeviceRGB eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert(Before + "0.1 0.2 0.3 setrgbcolor currentcolor count 3 eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("currentcolorspace 0 get /DeviceGray eq currentcolor 0 eq and {" + Marker + "} if")));
    }

    /// <summary>A pattern set is answered as the Pattern space and the pattern itself.</summary>
    [Fact]
    public void APattern_IsAnsweredAsThePatternSpaceAndThePattern()
    {
        const string Pattern =
            "/pat << /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 " +
            "/PaintProc { pop 0 0 5 5 rectfill } >> matrix makepattern def pat setpattern ";
        Assert.Contains("f", Ops(Convert(Pattern + "currentcolorspace 0 get /Pattern eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert(Pattern + "currentcolor pat eq {" + Marker + "} if")));
    }
}

/// <summary>The device-parameter state cap, in the collection every test writing a limit joins.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class DeviceParameterCapTests
{
    /// <summary>Past <see cref="PsLimits.MaxDeviceParameterStates"/> a new change is not written, said once; the one written is.</summary>
    [Fact]
    public void PastTheCap_ANewChangeIsNotWritten()
    {
        try
        {
            PsLimits.MaxDeviceParameterStates = 1;
            var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "2 setflat 0 0 1 1 rectfill 3 setflat 0 0 1 1 rectfill 4 setflat 0 0 1 1 rectfill"));
            Assert.Equal(1, ContentStreamAssert.Parse(r.Content).Count(o => o.Operator == "gs"));
            Assert.Contains("cap for overprint", r.LastError ?? "", System.StringComparison.Ordinal);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
