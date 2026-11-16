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
/// What <c>gsave</c> keeps and <c>grestore</c> puts back (PLRM §4.2): the current path, its current
/// point, and the font. All three survived <c>grestore</c> until 2026-10-19 — the reference renderer's
/// alphabet.ps, which measures a letter at 50 points inside <c>gsave … grestore</c> and shows each
/// line inside another, drew every line at 50 points, each one starting where the last ended.
/// </summary>
public sealed class GraphicsStateScopeTests
{
    private const string Helvetica15 = "/Helvetica findfont 15 scalefont setfont ";

    private static string[] Ops(string postScript) =>
        ContentStreamAssert.Parse(PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript)).Content)
            .Select(o => o.ToString()).ToArray();

    /// <summary>A font set inside <c>gsave … grestore</c> is gone after it: the text after is 15-point.</summary>
    [Fact]
    public void GRestore_PutsBackTheFont()
    {
        string[] ops = Ops(Helvetica15 + "gsave /Helvetica findfont 50 scalefont setfont grestore 0 0 moveto (A) show");
        Assert.Contains("15 0 0 15 0 0 Tm", ops);
        Assert.DoesNotContain(ops, o => o.StartsWith("50 0 0 50"));
    }

    /// <summary>The control: a font set outside any bracket stays, and <c>restore</c> puts one back as <c>grestore</c> does.</summary>
    [Fact]
    public void AFontSetOutsideABracket_Stays_AndRestorePutsOneBack()
    {
        string[] kept = Ops(Helvetica15 + "/Helvetica findfont 50 scalefont setfont 0 0 moveto (A) show");
        Assert.Contains("50 0 0 50 0 0 Tm", kept);
        string[] restored = Ops(Helvetica15 + "/s save def /Helvetica findfont 50 scalefont setfont s restore 0 0 moveto (A) show");
        Assert.Contains("15 0 0 15 0 0 Tm", restored);
    }

    /// <summary>A <c>show</c> inside <c>gsave … grestore</c> leaves the current point where it was: C draws at 10, not after AB.</summary>
    [Fact]
    public void GRestore_PutsBackTheCurrentPointAShowMoved()
    {
        string[] ops = Ops(Helvetica15 + "10 10 moveto gsave (AB) show grestore (C) show");
        Assert.Equal(2, ops.Count(o => o == "15 0 0 15 10 10 Tm"));
    }

    /// <summary>
    /// <c>//name</c> is the value the name has when it is scanned (PLRM §3.12.2): the reference renderer's
    /// escher.ps redefines <c>moveto</c> through <c>//moveto</c>, which an ordinary name turned into a
    /// call of the new procedure from itself.
    /// </summary>
    [Fact]
    public void AnImmediatelyEvaluatedName_IsItsValueWhenScanned()
    {
        string[] redefined = Ops("/moveto { 1 1 add pop //moveto } def 5 5 moveto 6 6 lineto stroke");
        Assert.Contains("5 5 m", redefined);
        Assert.Contains("6 6 l", redefined);

        string[] captured = Ops("/x 1 def /p { //x } def /x 2 def p 1 eq { 0 0 moveto 1 1 lineto stroke } if");
        Assert.Contains("S", captured);
    }

    /// <summary>The extent of <c>ashow</c> text covers its spacing: two 6.67-point letters 20 apart reach past 53.</summary>
    [Fact]
    public void AnAshowsExtent_CoversItsSpacing()
    {
        var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "/Helvetica findfont 10 scalefont setfont 0 0 moveto 20 0 (AB) ashow"));
        Assert.True(r.BoundingBox[2] >= 53.3, $"x1 = {r.BoundingBox[2]}");
    }

    /// <summary>A font set inside a Type 3 glyph's procedure, or anywhere a bracket nests, is the bracket's alone: <c>grestoreall</c> unwinds them all.</summary>
    [Fact]
    public void GRestoreAll_PutsBackTheOutermostFont()
    {
        string[] ops = Ops(Helvetica15 + "gsave /Helvetica findfont 30 scalefont setfont gsave /Helvetica findfont 50 scalefont setfont grestoreall 0 0 moveto (A) show");
        Assert.Contains("15 0 0 15 0 0 Tm", ops);
    }

    /// <summary><c>grestoreall</c> puts back the path the outermost <c>gsave</c> saved.</summary>
    [Fact]
    public void GRestoreAll_PutsBackTheOutermostPath()
    {
        string[] ops = Ops("newpath 5 5 moveto gsave 1 1 lineto gsave 2 2 lineto grestoreall 20 20 lineto stroke");
        Assert.Contains("5 5 m", ops);
        Assert.Contains("20 20 l", ops);
        Assert.DoesNotContain("1 1 l", ops);
    }

    /// <summary>A document's <c>//name</c> is looked up as a fragment's is: escher's pattern through <c>ConvertDocument</c>.</summary>
    [Fact]
    public void AnImmediatelyEvaluatedName_IsLookedUpInADocumentToo()
    {
        using var input = new System.IO.MemoryStream(Encoding.ASCII.GetBytes(
            "%!PS\n/moveto { 1 1 add pop //moveto } def 5 5 moveto 6 6 lineto stroke showpage\n"));
        using var output = new System.IO.MemoryStream();
        var r = PostScriptConversion.ConvertDocument(input, output);
        Assert.Equal(1, r.PageCount);
        Assert.Null(r.LastError);
    }
}
