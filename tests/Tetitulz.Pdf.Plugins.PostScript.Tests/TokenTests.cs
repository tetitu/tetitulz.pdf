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
/// <c>token</c> (PLRM §8.2): one object scanned from a string or a file as the interpreter scans its
/// program. It was an arity-table entry answering null, so a program parsing its own data lost the
/// object at the next operator.
/// </summary>
public sealed class TokenTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static string[] Ops(string postScript) =>
        ContentStreamAssert.Parse(PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript)).Content)
            .Select(o => o.ToString()).ToArray();

    /// <summary>A number, and the rest after it and its one delimiter.</summary>
    [Fact]
    public void AStringsToken_IsItsFirstObject_AndTheRest()
    {
        Assert.Contains("f", Ops("(15 abc) token pop 15 eq exch (abc) eq and {" + Marker + "} if"));
    }

    /// <summary>A procedure is taken whole, executable: it runs when executed.</summary>
    [Fact]
    public void AProcedureToken_IsTakenWhole()
    {
        Assert.Contains("f", Ops("({1 2 add} rest) token pop exec 3 eq exch (rest) eq and {" + Marker + "} if"));
    }

    /// <summary>A string with no object answers false, and nothing else.</summary>
    [Fact]
    public void AStringWithNoObject_AnswersFalse()
    {
        Assert.Contains("f", Ops("mark (  ) token not counttomark 1 eq and {" + Marker + "} if"));
    }

    /// <summary><c>//name</c> in a token is looked up as the program's own is.</summary>
    [Fact]
    public void AnImmediateNameInAToken_IsLookedUp()
    {
        Assert.Contains("f", Ops("/x 7 def (//x) token pop exch pop 7 eq {" + Marker + "} if"));
    }

    /// <summary>A file's token is the program's next object, read where the program has got to.</summary>
    [Fact]
    public void AFilesToken_IsTheProgramsNextObject()
    {
        Assert.Contains("f", Ops("{ currentfile token pop 42 eq {" + Marker + "} if } exec\n42\n"));
    }

    /// <summary>A filtered file's token comes from its decoded block, a second from where the first ended.</summary>
    [Fact]
    public void AFilteredFilesTokens_ComeFromItsBlockInTurn()
    {
        Assert.Contains("f", Ops("/f currentfile /ASCIIHexDecode filter def" +
                                 " { f token pop f token pop 2 eq exch 1 eq and {" + Marker + "} if } exec\n31203220>\n"));
    }

    /// <summary>Anything but a string or a file is a <c>typecheck</c>; a string is the control above.</summary>
    [Fact]
    public void ATokenOfANumber_IsATypecheck()
    {
        Assert.Contains("f", Ops("{ 5 token } stopped {" + Marker + "} if"));
        Assert.DoesNotContain("f", Ops("{ (5) token } stopped {" + Marker + "} if"));
    }
}
