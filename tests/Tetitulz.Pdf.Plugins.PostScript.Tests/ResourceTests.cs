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
/// Level 2 resources (PLRM §3.9): a category's instances defined, found, enumerated and removed.
/// <c>defineresource</c>, <c>undefineresource</c> and <c>resourceforall</c> were arity-table entries,
/// and <c>findresource</c> answered null outside <c>Font</c> and <c>Encoding</c> — so Illustrator's
/// <c>/AdobeProc … /ProcSet defineresource</c> lost its procedure set and the
/// <c>findresource begin</c> after it failed.
/// </summary>
public sealed class ResourceTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static string[] Ops(string program)
        => ContentStreamAssert.Parse(PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program)).Content)
            .Select(o => o.ToString()).ToArray();

    private static bool Drawn(string program) => Ops(program).Contains("f");

    private const string DefineProcs = "/MyProcs 10 dict dup begin /sq { dup mul } def end /ProcSet defineresource pop ";

    /// <summary>The Illustrator idiom: a procedure set defined, found and begun.</summary>
    [Fact]
    public void AProcSet_IsDefinedAndFound()
        => Assert.True(Drawn(DefineProcs + "/MyProcs /ProcSet findresource begin 3 sq 9 eq {" + Marker + "} if end"));

    /// <summary><c>defineresource</c> answers the instance it defined.</summary>
    [Fact]
    public void Defineresource_AnswersTheInstance()
        => Assert.True(Drawn("/K 5 /Generic defineresource 5 eq {" + Marker + "} if"));

    /// <summary>An instance never defined is <c>undefinedresource</c>, which a program's <c>stopped</c> sees; a defined one is the control.</summary>
    [Theory]
    [InlineData("/Nope", true)]
    [InlineData("/MyProcs", false)]
    public void AnUndefinedInstance_IsUndefinedresource(string key, bool refused)
        => Assert.Equal(refused, Drawn(DefineProcs + "{ " + key + " /ProcSet findresource pop } stopped" +
                                        " { $error /errorname get /undefinedresource eq {" + Marker + "} if } if"));

    /// <summary><c>resourcestatus</c> answers <c>0 0 true</c> for a defined instance and <c>false</c> after <c>undefineresource</c>.</summary>
    [Fact]
    public void Resourcestatus_FollowsDefineAndUndefine()
    {
        Assert.True(Drawn(DefineProcs + "/MyProcs /ProcSet resourcestatus { 0 eq exch 0 eq and {" + Marker + "} if } if"));
        Assert.True(Drawn(DefineProcs + "/MyProcs /ProcSet undefineresource /MyProcs /ProcSet resourcestatus not {" + Marker + "} if"));
    }

    /// <summary>A defined encoding is found before a built-in one, and a built-in one is found.</summary>
    [Fact]
    public void Encodings_AreTheProgramsThenTheBuiltIn()
    {
        Assert.True(Drawn("/StandardEncoding [/a] /Encoding defineresource pop /StandardEncoding /Encoding findresource length 1 eq {" + Marker + "} if"));
        Assert.True(Drawn("/ISOLatin1Encoding /Encoding findresource length 256 eq {" + Marker + "} if"));
    }

    /// <summary><c>/Font defineresource</c> is <c>definefont</c>: the font is in <c>FontDirectory</c>.</summary>
    [Fact]
    public void AFontResource_IsDefinefont()
        => Assert.True(Drawn("/MyFont /Helvetica findfont /Font defineresource pop FontDirectory /MyFont known {" + Marker + "} if"));

    /// <summary>
    /// <c>resourceforall</c> runs its procedure once a key the template matches — <c>*</c> any run,
    /// <c>?</c> one character, <c>\</c> the next as it is — each key in the scratch string.
    /// </summary>
    [Theory]
    [InlineData("(A*)", 3)]
    [InlineData("(?B)", 1)]
    [InlineData("(*)", 3)]
    [InlineData("(A\\\\*)", 1)]
    [InlineData("(Z*)", 0)]
    [InlineData("(AB*)", 1)]
    public void Resourceforall_RunsOnceAMatchingKey(string template, int count)
    {
        string program = "/AA 1 /ProcSet defineresource pop /AB 2 /ProcSet defineresource pop /A* 3 /ProcSet defineresource pop" +
                         " /n 0 def " + template + " { pop /n n 1 add def } 10 string /ProcSet resourceforall n " + count + " eq {" + Marker + "} if";
        Assert.True(Drawn(program));
    }

    /// <summary>The key a procedure is handed is the one matched, in the scratch string.</summary>
    [Fact]
    public void Resourceforall_HandsTheKeyInTheScratchString()
        => Assert.True(Drawn("/Key 1 /ProcSet defineresource pop (K*) { (Key) eq {" + Marker + "} if } 10 string /ProcSet resourceforall"));

    /// <summary><c>exit</c> in the procedure ends the enumeration, as in any loop.</summary>
    [Fact]
    public void Resourceforall_IsALoopForExit()
        => Assert.True(Drawn("/A1 1 /ProcSet defineresource pop /A2 2 /ProcSet defineresource pop /n 0 def" +
                             " (*) { pop /n n 1 add def exit } 10 string /ProcSet resourceforall n 1 eq {" + Marker + "} if"));

    /// <summary>The implicit categories answer what this interpreter carries; defining into one is <c>invalidaccess</c>.</summary>
    [Fact]
    public void ImplicitCategories_AnswerWhatIsCarried()
    {
        Assert.True(Drawn("3 /ImageType resourcestatus { pop pop" + Marker + " } if"));
        Assert.True(Drawn("/DeviceRGB /ColorSpaceFamily findresource /DeviceRGB eq {" + Marker + "} if"));
        Assert.False(Drawn("2 /ImageType resourcestatus {" + Marker + "} if"));
        Assert.True(Drawn("{ 9 9 /ImageType defineresource } stopped { $error /errorname get /invalidaccess eq {" + Marker + "} if } if"));
    }

    /// <summary>A child interpreter — a pattern's <c>PaintProc</c> — finds what the program defined.</summary>
    [Fact]
    public void APatternCell_FindsTheProgramsResources()
    {
        var ops = Ops(DefineProcs + "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10" +
                      " /PaintProc { pop /MyProcs /ProcSet findresource begin 3 sq 9 eq { 0 0 5 5 rectfill } if end } >>" +
                      " matrix makepattern setpattern" + Marker);
        Assert.Contains(ops, o => o.EndsWith(" scn", System.StringComparison.Ordinal));
        Assert.True(ops.Contains("f"));
    }

    /// <summary>
    /// A template of many stars against a long key is matched in linear time, as every template is:
    /// the program chooses it. A backtracking match tried every way to split the key between them.
    /// </summary>
    [Fact]
    public void AHostileTemplate_IsMatchedInLinearTime()
    {
        string key = new string('a', 200);
        string stars = string.Concat(Enumerable.Repeat("*a", 30)) + "b";
        Assert.True(Drawn($"/{key} 1 /ProcSet defineresource pop /n 0 def ({stars}) {{ pop /n n 1 add def }} 300 string /ProcSet resourceforall" +
                          " n 0 eq {" + Marker + "} if"));
    }
}
