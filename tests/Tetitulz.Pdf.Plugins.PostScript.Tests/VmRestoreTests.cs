// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>restore</c> undoes VM (PLRM §3.7.3): every change to an array's or a dictionary's contents since
/// its <c>save</c>, latest first — strings excepted, as PLRM and the reference renderer have it, and global VM
/// (<c>systemdict</c>, <c>globaldict</c>) untouched. It restored the graphics state alone, said once,
/// so a definition made inside <c>save … restore</c> outlived it.
/// </summary>
public sealed class VmRestoreTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static PostScriptConversionResult Convert(string program, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program), options);

    private static bool Drawn(string program) =>
        ContentStreamAssert.Parse(Convert(program).Content).Any(o => o.Operator == "f");

    /// <summary>A definition made since the save is undone; one made before it is the control.</summary>
    [Theory]
    [InlineData("/x 1 def save /x 2 def restore x 1 eq", true)]
    [InlineData("/x 1 def save /x 2 def restore x 2 eq", false)]
    [InlineData("save /y 2 def restore /y where { pop false } { true } ifelse", true)]
    public void ADefinitionSinceTheSave_IsUndone(string program, bool drawn)
        => Assert.Equal(drawn, Drawn(program + " {" + Marker + "} if"));

    /// <summary>An array element written since the save gets its old value back.</summary>
    [Fact]
    public void AnArrayWriteSinceTheSave_IsUndone()
        => Assert.True(Drawn("/a [1 2 3] def save a 0 9 put a 2 8 put restore a 0 get 1 eq a 2 get 3 eq and {" + Marker + "} if"));

    /// <summary>A write through a subarray, which shares the storage, is undone with it.</summary>
    [Fact]
    public void AWriteThroughASubarray_IsUndone()
        => Assert.True(Drawn("/a [1 2 3] def save a 1 2 getinterval 0 9 put restore a 1 get 2 eq {" + Marker + "} if"));

    /// <summary>A string's contents are not restored (PLRM §3.7.3's exception); a value can be carried out of a save in one.</summary>
    [Fact]
    public void AStringsContents_AreNotRestored()
        => Assert.True(Drawn("/s (abc) def save s 0 120 put restore s 0 get 120 eq {" + Marker + "} if"));

    /// <summary>Global VM is not restored: <c>globaldict</c> keeps what was put in it.</summary>
    [Fact]
    public void GlobalVm_IsNotRestored()
        => Assert.True(Drawn("save globaldict /g 5 put restore globaldict /g get 5 eq {" + Marker + "} if"));

    /// <summary>Saves nest: each restore undoes its own level, an outer one every level inside it too.</summary>
    [Fact]
    public void Saves_Nest()
    {
        Assert.True(Drawn("/x 1 def save /x 2 def save /x 3 def restore x 2 eq exch restore x 1 eq and {" + Marker + "} if"));
        Assert.True(Drawn("/x 1 def save /outer exch def /x 2 def save pop /x 3 def outer restore x 1 eq {" + Marker + "} if"));
    }

    /// <summary>
    /// A second save at the level a restore left is a new level: an object copied under the first is
    /// copied again, so the second restore undoes its write too.
    /// </summary>
    [Theory]
    [InlineData("/a [1] def save a 0 2 put restore save a 0 3 put restore a 0 get 1 eq")]
    [InlineData("/x 1 def save /x 2 def restore save /x 3 def restore x 1 eq")]
    public void ASecondSaveAtTheSameLevel_CopiesAgain(string program)
        => Assert.True(Drawn(program + " {" + Marker + "} if"));

    /// <summary>A save restored once cannot be restored again: <c>invalidrestore</c>; an open one is the control.</summary>
    [Theory]
    [InlineData("save dup restore restore", true)]
    [InlineData("save restore", false)]
    public void ASaveRestoredTwice_IsInvalidrestore(string body, bool refused)
        => Assert.Equal(refused, Drawn("{ " + body + " } stopped { $error /errorname get /invalidrestore eq {" + Marker + "} if } if"));

    /// <summary>
    /// An object's contents are copied once a level, however often it is written: a loop writing one
    /// array inside a save runs to the operation budget, never to the allocation budget.
    /// </summary>
    [Fact]
    public void AHostileWriteLoop_CopiesOnceALevel()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000, MaxCompositeAllocationBytes = 1_000_000 };
        var r = Convert("/a 1000 array def save { a 0 1 put } loop", options);
        Assert.True(r.BudgetExhausted);
        Assert.DoesNotContain("VMerror", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>An object made since the save is not copied: writing it costs no copy, so the same loop over a fresh array runs as long.</summary>
    [Fact]
    public void AnObjectMadeSinceTheSave_IsNotCopied()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000, MaxCompositeAllocationBytes = 1_000_000 };
        var r = Convert("save /b 1000 array def { 0 1 1 { b exch 1 put } for } loop", options);
        Assert.True(r.BudgetExhausted);
        Assert.DoesNotContain("VMerror", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A removal since the save is undone: <c>undef</c>'s entry comes back.</summary>
    [Fact]
    public void AnUndefSinceTheSave_IsUndone()
        => Assert.True(Drawn("/x 1 def save currentdict /x undef restore /x where { pop x 1 eq } { false } ifelse {" + Marker + "} if"));

    private static bool RunsClean(string body, long budget)
    {
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = budget };
        var r = Convert("{ " + body + " } stopped not {" + Marker + "} if", options);
        return ContentStreamAssert.Parse(r.Content).Any(o => o.Operator == "f");
    }

    /// <summary>
    /// The copy is charged at the price of making the array: one of 10 000 elements fits a budget of
    /// one and a half times its own price, and its copy does not — a <c>VMerror</c>, which <c>stopped</c> sees.
    /// The same write with no save is the control.
    /// </summary>
    [Fact]
    public void ACopy_IsChargedAsItsArray()
    {
        Assert.False(RunsClean("/a 10000 array def save a 0 1 put", 360_000));
        Assert.True(RunsClean("/a 10000 array def a 0 1 put", 360_000));
    }

    /// <summary>An array made since the save is not copied however large: the same write costs nothing beyond the array.</summary>
    [Fact]
    public void AnArrayMadeSinceTheSave_CostsNoCopy()
        => Assert.True(RunsClean("save /a 10000 array def a 0 1 put", 360_000));

    /// <summary>An array is copied once a level, not once a write: a thousand writes cost one copy.</summary>
    [Fact]
    public void AThousandWrites_CostOneCopy()
        => Assert.True(RunsClean("/a 1000 array def save 1000 { a 0 1 put } repeat", 1_000_000));

    /// <summary>A dictionary is copied once a level, not once a write.</summary>
    [Fact]
    public void AThousandDefinitions_CostOneCopy()
        => Assert.True(RunsClean("/d 1000 dict def 0 1 999 { d exch 0 put } for save 1000 { d 0 1 put } repeat", 1_000_000));

    /// <summary>A restore after the page its save was made on keeps the new page's state, and is no error.</summary>
    [Fact]
    public void ARestoreAfterShowpage_IsNoError()
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(
            "%!PS\n/x 1 def save /x 2 def 0 0 10 10 rectfill showpage restore x 1 eq { 0 0 5 5 rectfill } if showpage\n"));
        using var output = new MemoryStream();
        var r = PostScriptConversion.ConvertDocument(input, output);
        Assert.Equal(2, r.PageCount);
        Assert.DoesNotContain("invalidrestore", r.LastError ?? "", System.StringComparison.Ordinal);
    }
}
