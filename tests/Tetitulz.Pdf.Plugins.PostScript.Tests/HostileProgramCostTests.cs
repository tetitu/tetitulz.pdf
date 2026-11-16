// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// What a program can make the converter do past its two budgets: recurse in the CLR, walk a graph
/// of its own making, keep a copy, decode a block. The evidence is the budget or a count, never a
/// clock, each beside a control the same shape and small enough to pass.
/// </summary>
public class HostileProgramCostTests
{
    private static PostScriptConversionResult Run(string program, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(program), options);

    private static string Hex(byte[] bytes) => "<" + Convert.ToHexString(bytes) + ">";

    /// <summary>A zlib stream of <paramref name="zeros"/> zero bytes.</summary>
    private static byte[] Zlib(int zeros)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(new byte[zeros], 0, zeros);
        return ms.ToArray();
    }

    /// <summary>A profile header <c>IccDataColourSpaces</c> reads as <paramref name="space"/> data.</summary>
    private static byte[] IccHeader(string space)
    {
        var profile = new byte[132];
        profile[3] = 132;                                    // size, big-endian
        Encoding.ASCII.GetBytes(space).CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        profile[8] = 0x02; profile[9] = 0x10;                // version 2.1
        return profile;
    }

    // ------------------------------------------------------------------
    //  CLR recursion
    // ------------------------------------------------------------------

    /// <summary>
    /// A decode procedure setting the space it decodes re-enters the build, each level CLR frames no
    /// budget can stop. Unguarded, this test takes the test host down. The inner set is declined; the
    /// outer space is built and painted in.
    /// </summary>
    [Fact]
    public void ACieDecodeProcedureSettingItsOwnSpace_IsDeclinedAndTheSpaceStillBuilt()
    {
        var result = Run("/d 4 dict def d /WhitePoint [1 1 1] put\n" +
                         "d /DecodeA { [/CIEBasedA d] setcolorspace 0 } put\n" +
                         "[/CIEBasedA d] setcolorspace 0 0 moveto 10 10 lineto stroke");

        Assert.NotNull(result.Resources?[PdfName.ColorSpace]);
        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
    }

    /// <summary>The control: the same space with a procedure that sets none is built the same way.</summary>
    [Fact]
    public void ACieDecodeProcedure_ThatSetsNoSpace_IsBuilt()
    {
        var result = Run("/d 4 dict def d /WhitePoint [1 1 1] put\nd /DecodeA { 0.5 mul } put\n" +
                         "[/CIEBasedA d] setcolorspace 0 0 moveto 10 10 lineto stroke");

        Assert.NotNull(result.Resources?[PdfName.ColorSpace]);
        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
    }

    /// <summary>
    /// A CIE build that declined leaves the guard down: the next space is built. Reaches the
    /// <c>finally</c> a build that returned early would otherwise skip.
    /// </summary>
    [Fact]
    public void ACieSpace_RefusedByItsDictionary_DoesNotStopTheNextBuild()
    {
        var result = Run("[/CIEBasedA << /WhitePoint [0 0 0] >>] setcolorspace\n" +
                         "/d 4 dict def d /WhitePoint [1 1 1] put\n" +
                         "[/CIEBasedA d] setcolorspace 0 0 moveto 10 10 lineto stroke");

        Assert.NotNull(result.Resources?[PdfName.ColorSpace]);
    }

    /// <summary>
    /// An ICC space whose <c>/Alternate</c> names itself is resolved once, the alternate read under the
    /// depth the rest of the resolver carries. Unguarded, this test takes the test host down.
    /// </summary>
    [Fact]
    public void AnIccSpaceWhoseAlternateIsItself_Resolves()
    {
        var result = Run($"/d 4 dict def d /N 3 put d /DataSource {Hex(IccHeader("RGB "))} put\n" +
                         "d /Alternate [/ICCBased d] put\n" +
                         "[/ICCBased d] setcolorspace 0.5 0.5 0.5 setcolor 0 0 moveto 10 10 lineto stroke");

        Assert.NotNull(result.Resources?[PdfName.ColorSpace]);
        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused shading whose <c>/Function</c> array holds itself: the walk for its file sources.
    /// Unguarded, this test takes the test host down. The drawing after it is kept.
    /// </summary>
    [Fact]
    public void ARefusedShadingWhoseFunctionArrayHoldsItself_IsWalkedOnce()
    {
        PsShading.DiscardVisits = 0;
        var result = Run("/a 1 array def a 0 a put\n" +
                         "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 1] /Function a >> shfill\n" +
                         "0 0 moveto 10 10 lineto stroke");

        Assert.Equal(2, PsShading.DiscardVisits);   // the shading, and the array once
        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
    }

    /// <summary>
    /// 64 entries naming one dictionary whose <c>/Functions</c> is those 64: 64^n visits by path, three
    /// by object. Unguarded, this test does not return.
    /// </summary>
    [Fact]
    public void ARefusedShadingWhoseFunctionsShareOneList_VisitsEachOnce()
    {
        PsShading.DiscardVisits = 0;
        Run("/f 2 dict def /l 64 array def 0 1 63 { l exch f put } for f /Functions l put\n" +
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 1] /Function f /Functions l >> shfill");

        Assert.Equal(3, PsShading.DiscardVisits);   // the shading, f and l
    }

    /// <summary>The walk still finds a file nested through a function list: its block never reaches the scanner.</summary>
    [Fact]
    public void ARefusedShading_ConsumesAFileNestedInItsFunctions()
    {
        var result = Run("/f 2 dict def f /DataSource currentfile /ASCIIHexDecode filter put\n" +
                         "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 1] /Function [f] >> shfill\n" +
                         "414243>\n0 0 moveto 10 10 lineto stroke");

        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
        Assert.DoesNotContain(result.UnimplementedOperators, name => name.Contains("414243", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  Blocks decoded
    // ------------------------------------------------------------------

    /// <summary>
    /// A reusable file's flate block decoded again and again, held or not: each is charged, so the
    /// headroom the next decodes under shrinks until one no longer fits and is refused.
    /// </summary>
    [Fact]
    public void ABlockDecodedInALoop_SpendsTheAllocationBudget()
    {
        string program = $"/r {Hex(Zlib(200_000))} /ReusableStreamDecode filter def /b 1 string def\n" +
                         "{0} {{ r 0 setfileposition r /FlateDecode filter b readstring pop pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };

        Assert.DoesNotContain("could not be decoded", Run(string.Format(program, 2), options).LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("could not be decoded", Run(string.Format(program, 20), options).LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>What a file's read hands back is still the block, and the block held by it is read in pieces.</summary>
    [Fact]
    public void AChargedBlock_IsStillRead()
    {
        var result = Run($"/f {Hex(Zlib(100))} /FlateDecode filter def /b 40 string def\n" +
                         "f b readstring pop pop f b readstring pop pop f b readstring not exch length 20 eq and\n" +
                         "{ 0 0 moveto 10 10 lineto stroke } if");

        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
    }

    /// <summary>A stage's output counts, though a later stage narrows it: the work was done.</summary>
    [Fact]
    public void AStageNarrowedByTheNext_IsChargedItsOutput()
    {
        string program = $"/r {Hex(Zlib(200_000))} /ReusableStreamDecode filter def /b 1 string def\n" +
                         "{0} {{ r 0 setfileposition r /FlateDecode filter 1 () /SubFileDecode filter b readstring pop pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };

        Assert.DoesNotContain("could not be decoded", Run(string.Format(program, 2), options).LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("could not be decoded", Run(string.Format(program, 20), options).LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A decode refused at its ceiling keeps nothing and says nothing of how far it got, so it is
    /// charged to the work budget at the most its input can have produced; uncharged, a loop of them is
    /// a few steps each. The same loop over a stream under the ceiling is the control.
    /// </summary>
    [Fact]
    public void ADecodeRefusedAtItsCeiling_SpendsTheWorkBudget()
    {
        string program = "/r {0} /ReusableStreamDecode filter def /b 1 string def\n" +
                         "200 {{ r 0 setfileposition r /FlateDecode filter b readstring pop pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 500_000, MaxTotalOps = 200_000 };

        Assert.True(Run(string.Format(program, Hex(Zlib(5_000_000))), options).BudgetExhausted, "refused at the ceiling");
        Assert.False(Run(string.Format(program, Hex(Zlib(500))), options).BudgetExhausted, "decoded under it");
    }

    /// <summary>
    /// A filter fed by a procedure decodes its data before anything is drawn. An image the matrix then
    /// declines holds nothing and takes no image slot, so that decode is all a loop of them spends.
    /// </summary>
    [Fact]
    public void AProcedureFedDecode_SpendsTheWorkBudgetByWhatItProduced()
    {
        string program = $"/s {Hex(Zlib(2_000_000))} def /once {{{{ s }}}} def\n" +
                         "{0} {{ 1 1 8 [0 0 0 0 0 0] /once load /FlateDecode filter image }} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000 };

        Assert.False(Run(string.Format(program, 2), options).BudgetExhausted, "two decodes fit");
        Assert.True(Run(string.Format(program, 40), options).BudgetExhausted, "forty do not");
    }

    /// <summary>
    /// A decode that throws says nothing of how far it got, so it is charged the most its input could
    /// have made. Corrupt data is the only route here: a stream past the ceiling comes back cut short, not thrown.
    /// </summary>
    [Fact]
    public void ACorruptDecode_SpendsTheWorkBudgetByWhatItsInputCouldHaveMade()
    {
        string corrupt = Hex(Encoding.ASCII.GetBytes(new string('v', 100)));
        string program = "/r {0} /ReusableStreamDecode filter def /b 1 string def\n" +
                         "200 {{ r 0 setfileposition r /FlateDecode filter b readstring pop pop }} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000 };

        Assert.True(Run(string.Format(program, corrupt), options).BudgetExhausted, "corrupt");
        Assert.False(Run(string.Format(program, Hex(Zlib(10))), options).BudgetExhausted, "a good one, as small");
    }

    /// <summary>A decode's input is walked whatever comes out: a hundred kilobytes of white space decode to nothing.</summary>
    [Fact]
    public void ADecodesInput_SpendsTheWorkBudgetThoughNothingComesOut()
    {
        string program = "/r ({0}) /ReusableStreamDecode filter def /b 1 string def\n" +
                         "200 {{ r 0 setfileposition r /ASCIIHexDecode filter b readstring pop pop }} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000 };

        Assert.True(Run(string.Format(program, new string(' ', 100_000)), options).BudgetExhausted, "large");
        Assert.False(Run(string.Format(program, new string(' ', 100)), options).BudgetExhausted, "small");
    }

    /// <summary>A reusable file's bytes handed to an image are a block kept: each time, though the file holds them once.</summary>
    [Fact]
    public void AReusableFilesBlockHandedOver_SpendsTheAllocationBudgetEachTime()
    {
        string program = "/r 200000 string /ReusableStreamDecode filter def\n" +
                         "{0} {{ r 0 setfileposition 1 1 8 [0 0 0 0 0 0] r image }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };

        Assert.False(Run(string.Format(program, 2), options).BudgetExhausted, "two fit");
        Assert.True(Run(string.Format(program, 20), options).BudgetExhausted, "twenty do not");
    }

    /// <summary>
    /// The block the first filter takes from the program itself is kept too, the way a stage's output is:
    /// a run-length block ends where its data says, so a program holds a hundred kilobytes in a kilobyte.
    /// </summary>
    [Fact]
    public void AFiltersFirstBlock_FromTheProgram_SpendsTheAllocationBudget()
    {
        string Images(int count)
        {
            var block = new StringBuilder();
            for (int n = 0; n < 800; n++) block.Append((char)129).Append('A');     // 128 of 'A', twice over the bytes
            block.Append((char)128);                                                 // RunLength's end of data

            var program = new StringBuilder();
            for (int n = 0; n < count; n++)
                program.Append("1 1 8 [0 0 0 0 0 0] currentfile /RunLengthDecode filter image\n").Append(block).Append('\n');
            return program.ToString();
        }
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };

        Assert.DoesNotContain("could not be decoded", Run(Images(2), options).LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("could not be decoded", Run(Images(20), options).LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A filter over a string keeps the string's bytes as well as the file: each one, whatever it holds.</summary>
    [Fact]
    public void AFilterOverAString_SpendsTheAllocationBudgetByItsBytes()
    {
        string program = "/s 100000 string def {0} {{ s /ASCIIHexDecode filter pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };

        Assert.False(Run(string.Format(program, 2), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 20), options).BudgetExhausted);
    }

    /// <summary>A filter over a procedure holds it and its file: the charge the loop has to pay for keeping them.</summary>
    [Fact]
    public void AFilterOverAProcedure_SpendsTheAllocationBudget()
    {
        string program = "/p {{ () }} def {0} {{ /p load /ASCIIHexDecode filter pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 100_000 };

        Assert.False(Run(string.Format(program, 10), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 1000), options).BudgetExhausted);
    }

    /// <summary>A reusable file over a string holds a copy of it.</summary>
    [Fact]
    public void AReusableFileOverAString_SpendsTheAllocationBudgetByItsBytes()
    {
        string program = "/s 100000 string def {0} {{ s << /Filter [/ASCIIHexDecode] >> /ReusableStreamDecode filter pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };

        Assert.False(Run(string.Format(program, 2), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 20), options).BudgetExhausted);
    }

    /// <summary>
    /// And of each stage it decodes through: four copies of the source fit where four sources and their
    /// outputs do not, which is what tells the stage's charge from the source's.
    /// </summary>
    [Fact]
    public void AReusableFilesStageOutput_SpendsTheAllocationBudget()
    {
        string digits = new string('0', 200_000);
        string program = "/s (" + digits + ") def {0} {{ s << /Filter [/ASCIIHexDecode] >> /ReusableStreamDecode filter pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_200_000 };

        Assert.False(Run(string.Format(program, 1), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 4), options).BudgetExhausted);
    }

    /// <summary>An ICC profile given as a string is a copy kept for the conversion, for each space built.</summary>
    [Fact]
    public void AnIccProfileString_SpendsTheAllocationBudgetForEachSpace()
    {
        string profile = Hex(IccHeader("RGB ").Concat(new byte[100_000]).ToArray());
        string program = "/p " + profile + " def\n" +
                         "{0} {{ /d 3 dict def d /N 3 put d /DataSource p put [/ICCBased d] setcolorspace }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };

        Assert.False(Run(string.Format(program, 2), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 20), options).BudgetExhausted);
    }

    /// <summary>A shading's numbers are walked and copied for each fill: a step each, the memory aside.</summary>
    [Fact]
    public void AShadingsNumberArrays_SpendTheWorkBudget()
    {
        string fill = "/a 10000 array def 0 1 9999 {{ a exch 0 put }} for\n" +
                      "/sh << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 1] /Function << /FunctionType 2 /Domain a /C0 [0] /C1 [1] /N 1 >> >> def\n" +
                      "{0} {{ sh shfill }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1L << 40, MaxTotalOps = 200_000 };

        Assert.False(Run(string.Format(fill, 1), options).BudgetExhausted, "one fits");
        Assert.True(Run(string.Format(fill, 20), options).BudgetExhausted, "twenty do not");
    }

    /// <summary>Once refused, the budget's headroom is nothing, so no later decode runs under a figure no charge can reach.</summary>
    [Fact]
    public void AnExhaustedBudget_HasNoRemainder()
    {
        var budget = new PsAllocationBudget(100);
        Assert.Equal(100, budget.Remaining);
        Assert.False(budget.TryCharge(101));
        Assert.Equal(0, budget.Remaining);
    }

    // ------------------------------------------------------------------
    //  Filter chains
    // ------------------------------------------------------------------

    [Fact]
    public void AFilterChainPastTheLimit_IsALimitCheck()
    {
        string program = "(abc) /ReusableStreamDecode filter {0} {{ /ASCIIHexDecode filter }} repeat";

        Assert.DoesNotContain("limitcheck", Run(string.Format(program, PsLimits.MaxFilterChainDepth)).LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("limitcheck", Run(string.Format(program, PsLimits.MaxFilterChainDepth + 1)).LastError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void AReusableFilterArrayPastTheLimit_IsALimitCheck()
    {
        string names = string.Concat(Enumerable.Repeat("/SubFileDecode ", PsLimits.MaxFilterChainDepth));

        Assert.DoesNotContain("limitcheck",
            Run($"(abc) << /Filter [{names}] >> /ReusableStreamDecode filter").LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("limitcheck",
            Run($"(abc) << /Filter [{names} /SubFileDecode] >> /ReusableStreamDecode filter").LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A filter made and kept costs what it holds: a loop keeping them runs out of the budget.</summary>
    [Fact]
    public void FiltersKept_SpendTheAllocationBudget()
    {
        string program = "/r (a) /ReusableStreamDecode filter def {0} {{ r /ASCIIHexDecode filter pop }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 10_000 };

        Assert.False(Run(string.Format(program, 10), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 1000), options).BudgetExhausted);
    }

    [Fact]
    public void CurrentFile_IsOneObject()
    {
        var result = Run("currentfile currentfile eq { 0 0 moveto 10 10 lineto stroke } if");

        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Retained copies
    // ------------------------------------------------------------------

    /// <summary>A shading's number array becomes PDF numbers kept to the end: one array of the program's, charged at each.</summary>
    [Theory]
    [InlineData("/FunctionType 2 /Domain a /C0 [0] /C1 [1] /N 1", "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>")]
    public void AShadingsNumberArrays_SpendTheAllocationBudget(string function, string small)
    {
        string fill = "/a 10000 array def 0 1 9999 {{ a exch 0 put }} for\n" +
                      "/sh << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 1] /Function {0} >> def {1} {{ sh shfill }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 2_000_000 };

        Assert.False(Run(string.Format(fill, $"<< {function} >>", 1), options).BudgetExhausted, "one fits");
        Assert.True(Run(string.Format(fill, $"<< {function} >>", 8), options).BudgetExhausted, "eight do not");
        Assert.False(Run(string.Format(fill, small, 8), options).BudgetExhausted, "eight small ones fit");
    }

    /// <summary>A palette holds hival + 1 entries, whatever the string: the rest is not kept.</summary>
    [Fact]
    public void AnIndexedPalette_KeepsItsEntriesAlone()
    {
        var space = PsImageColorSpace.Resolve(PsObject.Arr(new PsArray(new[]
        {
            PsObject.LitName("Indexed"), PsObject.LitName("DeviceRGB"), PsObject.Int(1), PsObject.Str(new PsString(new byte[1000])),
        })));

        Assert.Equal(6, space.Lookup.Length);
    }

    /// <summary>A palette shorter than its entries is kept as it is.</summary>
    [Fact]
    public void AShortIndexedPalette_IsKeptWhole()
    {
        var space = PsImageColorSpace.Resolve(PsObject.Arr(new PsArray(new[]
        {
            PsObject.LitName("Indexed"), PsObject.LitName("DeviceRGB"), PsObject.Int(255), PsObject.Str(new PsString(new byte[10])),
        })));

        Assert.Equal(10, space.Lookup.Length);
    }

    /// <summary>A palette set in a loop is charged: a graphics state keeps the space that holds it.</summary>
    [Fact]
    public void AnIndexedPalette_SpendsTheAllocationBudget()
    {
        string program = "/s 1020 string def {0} {{ [/Indexed /DeviceCMYK 254 s] setcolorspace }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 100_000 };

        Assert.False(Run(string.Format(program, 10), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 200), options).BudgetExhausted);
    }

    [Fact]
    public void ASeparationColorantString_SpendsTheAllocationBudget()
    {
        string program = "/s 10000 string def {0} {{ [/Separation s /DeviceGray {{}}] setcolorspace }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 200_000 };

        Assert.False(Run(string.Format(program, 2), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 50), options).BudgetExhausted);
    }

    [Fact]
    public void ADeviceNColorantString_SpendsTheAllocationBudget()
    {
        string program = "/s 10000 string def {0} {{ [/DeviceN [s] /DeviceGray {{}}] setcolorspace }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 200_000 };

        Assert.False(Run(string.Format(program, 2), options).BudgetExhausted);
        Assert.True(Run(string.Format(program, 50), options).BudgetExhausted);
    }

    [Fact]
    public void DeviceNPastTheColorantLimit_IsNotCarried()
    {
        static PsImageColorSpace Space(int colorants) => PsImageColorSpace.Resolve(PsObject.Arr(new PsArray(new[]
        {
            PsObject.LitName("DeviceN"),
            PsObject.Arr(new PsArray(Enumerable.Repeat(PsObject.LitName("A"), colorants).ToArray())),
            PsObject.LitName("DeviceGray"),
            PsObject.Proc(new PsArray(Array.Empty<PsObject>())),
        })));

        Assert.Equal(PsLimits.MaxDeviceNColorants, Space(PsLimits.MaxDeviceNColorants).SetColorArity);
        Assert.NotNull(Space(PsLimits.MaxDeviceNColorants).Colorants);

        var refused = Space(PsLimits.MaxDeviceNColorants + 1);
        Assert.Equal(-1, refused.SetColorArity);
        Assert.Null(refused.Colorants);
        Assert.Contains("colorants", refused.CarryFailure, StringComparison.Ordinal);
    }

    /// <summary>A CMap's names are kept and written into its program: each define is charged them.</summary>
    [Fact]
    public void ACMapsNames_SpendTheAllocationBudget()
    {
        string program = "/CIDInit /ProcSet findresource begin /r {0} string def\n" +
                         "/si << /Registry r /Ordering r /Supplement 0 >> def\n" +
                         "{1} {{ 4 dict begin /CIDSystemInfo si def begincmap endcmap /X currentdict /CMap defineresource pop end }} repeat";
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 200_000 };

        Assert.False(Run(string.Format(program, 10, 50), options).BudgetExhausted, "fifty with short names");
        Assert.True(Run(string.Format(program, 10_000, 50), options).BudgetExhausted, "fifty with long ones");
    }

    // ------------------------------------------------------------------
    //  Keys
    // ------------------------------------------------------------------

    /// <summary>A key is hashed whole for the operator's one step: each dictionary operator charges it.</summary>
    [Theory]
    [InlineData("k s known pop")]
    [InlineData("k s 1 put")]
    [InlineData("k s undef")]
    [InlineData("s 1 def")]
    [InlineData("s where {{ pop }} if")]
    [InlineData("s 1 store")]
    [InlineData("<< s 1 >> pop")]
    [InlineData("h s get pop")]
    public void ALongKey_SpendsTheWorkBudgetByItsLength(string operation)
    {
        string program = $"/s {{0}} string def /k 1 dict def k /x 1 put /h 1 dict def h s 1 put 200 {{{{ {operation} }}}} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 300_000 };

        Assert.True(Run(string.Format(program, 100_000), options).BudgetExhausted, "a 100 000-byte key");
        Assert.False(Run(string.Format(program, 100), options).BudgetExhausted, "a 100-byte key");
    }

    [Fact]
    public void ALongKeyLoaded_SpendsTheWorkBudgetByItsLength()
    {
        string program = "/s {0} string def s 1 def 200 {{ s load pop }} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 300_000 };

        Assert.True(Run(string.Format(program, 100_000), options).BudgetExhausted);
        Assert.False(Run(string.Format(program, 100), options).BudgetExhausted);
    }

    /// <summary>A dictionary copied hashes every key again.</summary>
    [Fact]
    public void ACopiedDictionary_SpendsTheWorkBudgetByItsKeys()
    {
        string program = "/s {0} string def /a 1 dict def a s 1 put /b 1 dict def 200 {{ a b copy pop }} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 300_000 };

        Assert.True(Run(string.Format(program, 100_000), options).BudgetExhausted);
        Assert.False(Run(string.Format(program, 100), options).BudgetExhausted);
    }

    /// <summary>An executable name is hashed at each dictionary on the stack: the charge has both factors.</summary>
    [Fact]
    public void ALongExecutableName_SpendsTheWorkBudgetByLengthAndDepth()
    {
        string name = new string('n', PsLimits.MaxNameChars);
        string program = $"/{name} 1 def /x 1 def /p 1 dict def p /x 1 put {{0}} {{{{ p begin }}}} repeat 1000 {{{{ {{1}} pop }}}} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 500_000 };

        Assert.True(Run(string.Format(program, 60, name), options).BudgetExhausted, "a long name under a deep stack");
        Assert.False(Run(string.Format(program, 0, "x"), options).BudgetExhausted, "a short name");
    }

    /// <summary>bind looks each name in a procedure up.</summary>
    [Fact]
    public void BindingALongName_SpendsTheWorkBudget()
    {
        string name = new string('n', PsLimits.MaxNameChars);
        string program = "/p {{ {0} }} def 400 {{ /p load bind pop }} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };

        Assert.True(Run(string.Format(program, name), options).BudgetExhausted);
        Assert.False(Run(string.Format(program, "x"), options).BudgetExhausted);
    }

    [Fact]
    public void ANamePastTheLimit_IsALimitCheckFromCvn()
    {
        Assert.DoesNotContain("limitcheck", Run($"{PsLimits.MaxNameChars} string cvn").LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("limitcheck", Run($"{PsLimits.MaxNameChars + 1} string cvn").LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A name token past the limit is cut to it, literal or executable, and said.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("")]
    public void ANameTokenPastTheLimit_IsCut(string prefix)
    {
        string token = prefix + new string('a', PsLimits.MaxNameChars + 10);
        var scanner = new PsScanner(Encoding.ASCII.GetBytes(token));

        Assert.True(scanner.TryNext(out PsToken read));
        Assert.Equal(PsLimits.MaxNameChars, read.Text.Length);
    }

    /// <summary>A restore puts a dictionary's entries back, and later writes go on from them.</summary>
    [Fact]
    public void ARestoredDictionary_IsItsOldEntriesAndStaysWritable()
    {
        var result = Run("/d 1 dict def d /a 1 put save d /a 2 put d /b 3 put restore\n" +
                         "d /a get 1 eq d /b known not and d /c 4 put d /c get 4 eq and\n" +
                         "{ 0 0 moveto 10 10 lineto stroke } if");

        Assert.Contains("\nS\n", Encoding.ASCII.GetString(result.Content), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Reads
    // ------------------------------------------------------------------

    /// <summary>A hexadecimal read skips what is no digit: the bytes passed over are charged, though none is stored.</summary>
    [Fact]
    public void AHexReadOverNoDigits_SpendsTheWorkBudgetByWhatItPassed()
    {
        string program = "/s {0} string def /r s /ReusableStreamDecode filter def /b 1 string def\n" +
                         "200 {{ r 0 setfileposition r b readhexstring pop pop }} repeat";
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };

        Assert.True(Run(string.Format(program, 100_000), options).BudgetExhausted, "100 000 bytes passed over a read");
        Assert.False(Run(string.Format(program, 100), options).BudgetExhausted, "100 bytes passed over a read");
    }

    /// <summary>The program's own bytes the same way: a run of no digits, read once, is charged by its length.</summary>
    [Fact]
    public void AHexReadOfTheProgram_SpendsTheWorkBudgetByWhatItPassed()
    {
        string program = "currentfile 1 string readhexstring pop pop\n" + new string(' ', 200_000);

        Assert.True(Run(program, new PostScriptConversionOptions { MaxTotalOps = 1_000 }).BudgetExhausted);
        Assert.False(Run(program, new PostScriptConversionOptions { MaxTotalOps = 10_000 }).BudgetExhausted);
    }
}
