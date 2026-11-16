// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Plugins.PostScript;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>Regressions from the 2026-11-12 review of the PostScript plugin, each against PLRM's own rule.</summary>
public sealed class FifthReviewRegressionTests
{
    private static string Text(string ps)
        => Encoding.Latin1.GetString(PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps)).Content ?? []);

    /// <summary>PLRM <c>round</c>: a tie goes to the greater integer, <c>-6.5 round</c> is -6.0.</summary>
    [Theory]
    [InlineData("-6.5", -6)]
    [InlineData("6.5", 7)]
    [InlineData("-6.4", -6)]
    [InlineData("-6.6", -7)]
    [InlineData("0.49999999999999994", 0)]
    public void Round_TakesATieToTheGreaterInteger(string operand, int expected)
    {
        string s = Text($"newpath 0 0 moveto {operand} round cvi 0 rlineto 10 0 rlineto fill");
        Assert.Contains($"\n{expected} 0 l\n", s);
    }

    /// <summary>
    /// A name and a string compare by characters (PLRM <c>eq</c>: <c>(abc) /abc eq</c> is true), and a
    /// string used as a key is its name, so <c>(abc) 1 def</c> defines what <c>abc</c> looks up. A
    /// different spelling stays unequal (the control).
    /// </summary>
    [Theory]
    [InlineData("(abc) /abc eq", true)]
    [InlineData("/abc (abc) eq", true)]
    [InlineData("(abc) /abd eq", false)]
    [InlineData("(abc) 7 def abc 7 eq", true)]
    [InlineData("/abc 7 def currentdict (abc) known", true)]
    [InlineData("(abc) 7 def currentdict /abc get 7 eq", true)]
    [InlineData("/abc 7 def currentdict (abc) get 7 eq", true)]
    public void ANameAndAStringOfTheSameCharacters_AreOneKey(string test, bool expected)
    {
        string s = Text($"newpath 0 0 moveto {test} {{ 4 }} {{ 9 }} ifelse 0 rlineto 10 0 rlineto fill");
        Assert.Contains($"\n{(expected ? 4 : 9)} 0 l\n", s);
    }

    /// <summary>
    /// <c>grestore</c> and <c>grestoreall</c> right after a <c>save</c> restore from the save's state
    /// without popping it (PLRM), so the <c>restore</c> that follows returns to the saved state: the
    /// 2× scale set in between does not reach the line drawn after it. Popping made the save's own
    /// snapshot the live state, and the scale was set on it. A <c>gsave</c>/<c>grestore</c> pair
    /// inside the save still pops (the control).
    /// </summary>
    [Theory]
    [InlineData("save grestoreall 2 2 scale restore")]
    [InlineData("save grestore 2 2 scale restore")]
    [InlineData("save gsave 3 3 scale grestore 2 2 scale restore")]
    public void RestoreAfterAGrestoreAtASaveLevel_ReturnsToTheSavedState(string prefix)
    {
        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
            prefix + " newpath 0 0 moveto 10 0 lineto stroke"));
        Assert.NotNull(r.BoundingBox);
        Assert.InRange(r.BoundingBox[2] - r.BoundingBox[0], 9.0, 12.0);
    }

    /// <summary>
    /// And the restore itself: <c>grestore</c> or <c>grestoreall</c> at a save level puts the save's
    /// state back as the live one, so a scale set after the save does not reach a line drawn after
    /// the grestore.
    /// </summary>
    [Theory]
    [InlineData("save 3 3 scale grestore")]
    [InlineData("save 3 3 scale grestoreall")]
    public void AGrestoreAtASaveLevel_PutsTheSavedStateBack(string prefix)
    {
        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
            prefix + " newpath 0 0 moveto 10 0 lineto stroke restore"));
        Assert.NotNull(r.BoundingBox);
        Assert.InRange(r.BoundingBox[2] - r.BoundingBox[0], 9.0, 12.0);
    }

    /// <summary>
    /// An inline block begins after the one white-space character that ended the operator's token
    /// (PLRM §3.8.2), a space as much as a newline: after a space it began with the space, so the
    /// image read " AB" and the program went on to execute <c>C</c> as an undefined name.
    /// </summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AnInlineBlock_BeginsAfterTheTokensDelimiter(string delimiter)
    {
        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
            "currentfile 3 () /SubFileDecode filter /f exch def 3 1 8 [3 0 0 1 0 0] f image" + delimiter + "ABC 0 0 moveto"));
        Assert.Null(r.LastError);
        Assert.Equal(0, r.DiagnosticCount);
    }

    /// <summary>
    /// A moveto directly after a moveto replaces it (PLRM): three in a row write one <c>m</c>, so a
    /// show's pen advances do not each add a segment every CTM change re-expresses. A moveto after a
    /// drawn segment starts a new subpath (the control).
    /// </summary>
    [Fact]
    public void AdversarialMovetoAfterMoveto_ReplacesIt()
    {
        string s = Text("newpath 0 0 moveto 10 10 moveto 3 4 moveto 5 5 lineto stroke");
        Assert.Single(Regex.Matches(s, @"(?m) m$"));
        Assert.Contains("3 4 m", s);

        string two = Text("newpath 0 0 moveto 1 1 lineto 3 4 moveto 5 5 lineto stroke");
        Assert.Equal(2, Regex.Matches(two, @"(?m) m$").Count);
    }
}
