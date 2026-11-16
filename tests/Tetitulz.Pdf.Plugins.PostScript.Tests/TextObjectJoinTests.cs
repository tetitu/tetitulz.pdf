// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A driver that places every glyph itself — the Microsoft PS Class Driver shows one glyph at a time
/// through <c>cshow</c>, flipping the transform around each — writes one show a glyph. Each show that
/// continues the text object before it on its line is one more string of that object's <c>TJ</c>, the
/// gap from the pen the number before it, so a reader selects and edits the line, not its letters.
/// The fixture's 'A' is 610 wide and 'B' 620, at size 100: 61 and 62 points.
/// </summary>
public sealed class TextObjectJoinTests
{
    private static readonly string Font = Type42FontTests.Font(Type42FontTests.Hex(Type42FontTests.Program));

    /// <summary>The driver's transform flip around a glyph: two of them leave the transform as it was.</summary>
    private const string Flip = " [1 0 0 -1 0 0] concat";

    /// <summary>The same program again under another name, so a second font resource; the first is left current.</summary>
    private static readonly string SecondFont = Font.Replace("ProbeT42", "ProbeT42b") +
                                                "/ProbeT42 findfont 100 scalefont setfont\n";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(Font + SecondFont + program));

    private static string[] Shown(PostScriptConversionResult r, string op)
        => ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == op).Select(o => o.Operands[0]).ToArray();

    /// <summary>
    /// Three one-glyph shows on one baseline, the driver's flips between them, are one text object:
    /// B 10 past where A left the pen (−100), the second A 5 past B's end (−50) — the number measured
    /// from the pen as a reader moves it, the first number included, so the third glyph lands where the
    /// program put it. The flips are gone with the text objects they separated.
    /// </summary>
    [Fact]
    public void OneGlyphShows_OnALine_AreOneTextObject()
    {
        var r = Convert("0 0 moveto (A) show" + Flip + Flip +
                        " 71 0 moveto (B) show" + Flip + Flip +
                        " 138 0 moveto (A) show");

        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "BT"));
        Assert.Empty(Shown(r, "Tj"));
        Assert.Equal(new[] { @"[(\000\001) -100 (\000\002) -50 (\000\001)]" }, Shown(r, "TJ"));
        Assert.Equal(0, ContentStreamAssert.Count(r.Content, "cm"));
        Assert.Null(r.LastError);
    }

    /// <summary>
    /// A line laid out leftwards — each show ending where the one before began, as a driver may place
    /// a right-to-left line — is one text object too: each number moves the pen back over what is
    /// drawn, 1.23 em here. The fourth glyph ends 1.23 em before the first began: past the allowance
    /// from where the line started, within it of where the line now begins.
    /// </summary>
    [Fact]
    public void OneGlyphShows_LaidOutLeftwards_AreOneTextObject()
    {
        var r = Convert("138 0 moveto (A) show 76 0 moveto (B) show 15 0 moveto (A) show -47 0 moveto (B) show");

        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "BT"));
        Assert.Equal(new[] { @"[(\000\001) 1230 (\000\002) 1230 (\000\001) 1230 (\000\002)]" }, Shown(r, "TJ"));
    }

    /// <summary>A glyph that starts where the pen stands is joined with no number between.</summary>
    [Fact]
    public void AGlyphAtThePen_IsJoinedWithNoNumber()
    {
        var r = Convert("0 0 moveto (A) show 61 0 moveto (B) show");
        Assert.Equal(new[] { @"[(\000\001) (\000\002)]" }, Shown(r, "TJ"));
    }

    /// <summary>
    /// What a show does not continue starts its own text object: another baseline, a gap past one em
    /// (another column), a glyph more than half an em behind the pen, another size, a paint between,
    /// and a transform the operators between leave changed. Each beside a control that does join.
    /// </summary>
    [Theory]
    [InlineData("0 0 moveto (A) show 61 1 moveto (B) show", "another baseline")]
    [InlineData("0 0 moveto (A) show 162 0 moveto (B) show", "a gap of 1.01 em")]
    [InlineData("0 0 moveto (A) show 10 0 moveto (B) show", "51 behind the pen")]
    [InlineData("0 0 moveto (A) show /ProbeT42 findfont 50 scalefont setfont 61 0 moveto (B) show", "another size")]
    [InlineData("0 0 moveto (A) show 0 0 moveto 1 1 lineto stroke 61 0 moveto (B) show", "a stroke between")]
    [InlineData("0 0 moveto (A) show [1 0 0 1 10 0] concat 51 0 moveto (B) show", "a transform left moved")]
    [InlineData("0 0 moveto (A) show /ProbeT42b findfont 100 scalefont setfont 61 0 moveto (B) show", "another font")]
    public void AShowThatDoesNotContinueTheLine_StartsItsOwnTextObject(string program, string why)
    {
        var r = Convert(program);
        Assert.True(ContentStreamAssert.Count(r.Content, "BT") == 2, why);
        Assert.Empty(Shown(r, "TJ"));
    }

    /// <summary>The joining controls at the edges the refusals stand past: exactly one em ahead, exactly half an em behind.</summary>
    [Theory]
    [InlineData("0 0 moveto (A) show 161 0 moveto (B) show", @"[(\000\001) -1000 (\000\002)]")]
    [InlineData("0 0 moveto (A) show 11 0 moveto (B) show", @"[(\000\001) 500 (\000\002)]")]
    public void AShowAtTheEdges_IsJoined(string program, string expected)
    {
        var r = Convert(program);
        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "BT"));
        Assert.Equal(new[] { expected }, Shown(r, "TJ"));
    }
}
