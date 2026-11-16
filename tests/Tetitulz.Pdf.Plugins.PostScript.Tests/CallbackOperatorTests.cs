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
/// <c>pathforall</c>, <c>kshow</c> and <c>cshow</c> (PLRM §8.2), each calling a procedure per item
/// through the step loop. All three were arity-table entries that consumed their operands and did
/// nothing: a driver's kerned text came out blank, and a path copied by <c>pathforall</c> never
/// arrived.
/// </summary>
public sealed class CallbackOperatorTests
{
    static CallbackOperatorTests()
    {
        // The composite cases need the predefined CMaps, registered as a host would (idempotent).
        new Tetitulz.Pdf.Plugins.CMap.AdobeCMapPlugin().Initialize();
    }

    /// <summary>An empty procedure leaves what <c>kshow</c> pushed — the two codes of each pair — as PLRM says.</summary>
    [Fact]
    public void AnEmptyKshowProcedure_LeavesEachPairsCodes()
    {
        string[] ops = Ops(Helvetica + "0 0 moveto mark {} (ABC) kshow counttomark 4 eq {" + Marker + "} if");
        Assert.Contains("f", ops);
    }

    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";
    private const string Helvetica = "/Helvetica findfont 10 scalefont setfont ";

    private static string[] Ops(string postScript) =>
        ContentStreamAssert.Parse(PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript)).Content)
            .Select(o => o.ToString()).ToArray();

    private static bool InOrder(string[] ops, params string[] expected)
    {
        int at = 0;
        foreach (string op in ops)
            if (at < expected.Length && op == expected[at]) at++;
        return at == expected.Length;
    }

    /// <summary>
    /// <c>pathforall</c> hands each segment to its procedure in the current user space: a path
    /// copied onto itself by <c>{moveto} {lineto} {curveto} {closepath}</c> is drawn twice, under a
    /// scale as without one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("2 2 scale ")]
    public void Pathforall_HandsEachSegmentToItsProcedure(string transform)
    {
        string[] ops = Ops(transform + "newpath 10 20 moveto 30 40 lineto 50 60 70 80 90 100 curveto closepath" +
                           " {moveto} {lineto} {curveto} {closepath} pathforall fill");
        string[] once = { "10 20 m", "30 40 l", "50 60 70 80 90 100 c", "h" };
        Assert.True(InOrder(ops, once.Concat(once).Append("f").ToArray()), string.Join(" | ", ops));
    }

    /// <summary><c>exit</c> in a procedure ends <c>pathforall</c>: the copy stops after its first line.</summary>
    [Fact]
    public void ExitInAPathforallProcedure_EndsIt()
    {
        string[] ops = Ops("newpath 10 20 moveto 30 40 lineto 50 60 lineto" +
                           " {moveto} {lineto exit} {curveto} {closepath} pathforall fill");
        Assert.True(InOrder(ops, "10 20 m", "30 40 l", "50 60 l", "10 20 m", "30 40 l", "f"), string.Join(" | ", ops));
        Assert.Equal(2, ops.Count(o => o == "10 20 m"));
        Assert.Equal(1, ops.Count(o => o == "50 60 l"));
    }

    /// <summary>
    /// Hostile: a procedure that grows the path it is enumerating — each line adding another — ends,
    /// because the segments are the path's when <c>pathforall</c> was called.
    /// </summary>
    [Fact]
    public void HostilePathGrowingProcedure_EndsWithTheSegmentsItWasCalledWith()
    {
        string[] ops = Ops("newpath 0 0 moveto 1 1 lineto 2 2 lineto" +
                           " {pop pop} {lineto 9 9 lineto} {} {} pathforall fill");
        Assert.Equal(2, ops.Count(o => o == "9 9 l"));
    }

    /// <summary>
    /// <c>kshow</c> shows each character, and between each pair pushes the two codes and runs the
    /// procedure: red is set between A and B — the only pair the procedure matches — and the 5-point
    /// kern after each puts the pen past 30, where without the procedure it would be at 20.56.
    /// </summary>
    [Fact]
    public void Kshow_ShowsEachCharacter_RunningTheProcedureBetweenEachPair()
    {
        string[] ops = Ops(Helvetica + "0 0 moveto" +
                           " { 2 copy 66 eq exch 65 eq and {1 0 0 setrgbcolor} if pop pop 5 0 rmoveto } (ABC) kshow" +
                           " currentpoint pop 30.5 gt {" + Marker + "} if");
        Assert.True(InOrder(ops, "(A) Tj", "1 0 0 rg", "(B) Tj", "(C) Tj", "f"), string.Join(" | ", ops));
        Assert.Equal(1, ops.Count(o => o == "1 0 0 rg"));

        string[] plain = Ops(Helvetica + "0 0 moveto {pop pop} (ABC) kshow currentpoint pop 20.5 gt {" + Marker + "} if");
        Assert.Contains("f", plain);                                            // 20.56 without a kern
    }

    /// <summary><c>exit</c> in a <c>kshow</c> procedure ends it after the character before.</summary>
    [Fact]
    public void ExitInAKshowProcedure_EndsIt()
    {
        string[] ops = Ops(Helvetica + "0 0 moveto {pop pop exit} (ABC) kshow");
        Assert.Contains("(A) Tj", ops);
        Assert.DoesNotContain("(B) Tj", ops);
    }

    /// <summary>In a composite font <c>kshow</c> shows its string whole, and says the procedure was not run.</summary>
    [Fact]
    public void KshowInACompositeFont_ShowsTheStringWhole_AndSaysSo()
    {
        PostScriptDiagnostics.Clear();
        string[] ops = Ops("/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont 0 0 moveto" +
                           " {pop pop 1 0 0 setrgbcolor} <82A041> kshow");
        Assert.Single(ops, o => o.EndsWith(" Tj"));
        Assert.DoesNotContain("1 0 0 rg", ops);
        Assert.Contains("not run between characters", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>cshow</c> shows nothing and runs the procedure once a character with its code and advance:
    /// A's 6.67 in Helvetica at 10, B's code not A's.
    /// </summary>
    [Fact]
    public void Cshow_RunsTheProcedurePerCharacter_WithItsCodeAndAdvance()
    {
        string[] ops = Ops(Helvetica + "0 0 moveto /n 0 def" +
                           " { pop dup 6.66 gt exch 6.68 lt and exch 65 eq and { /n n 1 add def } if } (AB) cshow" +
                           " n 1 eq {" + Marker + "} if");
        Assert.Contains("f", ops);
        Assert.DoesNotContain(ops, o => o.EndsWith(" Tj"));
    }

    /// <summary>
    /// A composite font's string is split by its CMap — Shift-JIS <c>82A0</c> is one code, <c>41</c>
    /// another — and the procedure is given each code's last byte (PLRM §8.2, <c>cshow</c>): A0 for
    /// <c>82A0</c>.
    /// </summary>
    [Fact]
    public void CshowInACompositeFont_SplitsTheStringByItsCMap_GivingEachCodesLastByte()
    {
        string[] ops = Ops(Kozuka + "0 0 moveto /n 0 def /first 0 def" +
                           " { pop pop n 0 eq { /first exch def } { pop } ifelse /n n 1 add def } <82A041> cshow" +
                           " n 2 eq first 16#A0 eq and {" + Marker + "} if");
        Assert.Contains("f", ops);
    }

    private const string Kozuka = "/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont ";

    // A print driver's cshow procedure: the byte it was given put into a one-byte string and shown.
    private const string DriverProcedure = "{ pop pop /code exch def 1 string dup 0 code put show }";

    private static string[] Shown(string[] ops) => ops.Where(o => o.EndsWith(" Tj", StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Showing the one byte <c>cshow</c> gave its procedure shows the code the CMap selected — the
    /// Microsoft PS Class Driver places every glyph so — the same as showing the code itself; a byte
    /// shown outside <c>cshow</c> is not (the control).
    /// </summary>
    [Fact]
    public void ShowingTheByteCshowGave_ShowsTheSelectedCode()
    {
        string[] direct = Shown(Ops(Kozuka + "0 0 moveto <82A0> show"));
        string[] byByte = Shown(Ops(Kozuka + "0 0 moveto " + DriverProcedure + " <82A0> cshow"));
        string[] lone = Shown(Ops(Kozuka + "0 0 moveto <A0> show"));

        Assert.Single(direct);
        Assert.Equal(direct, byByte);
        Assert.NotEqual(direct, lone);
    }

    /// <summary><c>charpath</c> of the byte draws the selected code's outline, as <c>charpath</c> of the code does.</summary>
    [Fact]
    public void CharpathOfTheByteCshowGave_OutlinesTheSelectedCode()
    {
        string[] direct = Ops(Kozuka + "newpath 0 0 moveto <82A0> true charpath fill");
        string[] byByte = Ops(Kozuka + "newpath 0 0 moveto { pop pop /code exch def 1 string dup 0 code put true charpath } <82A0> cshow fill");
        Assert.Contains("f", direct);
        Assert.Equal(direct, byByte);
    }

    /// <summary>Any other string shown in the font inside the procedure is a <c>rangecheck</c>, as PLRM says.</summary>
    [Fact]
    public void ShowingAnotherByteInsideCshow_IsARangecheck()
    {
        string[] ops = Ops(Kozuka + "0 0 moveto { { pop pop pop (Z) show } <82A0> cshow } stopped" +
                           " { $error /errorname get /rangecheck eq {" + Marker + "} if } if");
        Assert.Contains("f", ops);
    }

    /// <summary>The selection is the composite font's: in another font the procedure shows what it shows.</summary>
    [Fact]
    public void InAnotherFont_TheProcedureShowsWhatItShows()
    {
        string[] ops = Ops(Kozuka + "0 0 moveto { pop pop pop /Helvetica findfont 10 scalefont setfont (A) show } <82A0> cshow");
        Assert.Contains("(A) Tj", ops);
    }

    /// <summary>
    /// A <c>cshow</c> inside the procedure gives back the outer selection when it ends: the outer
    /// byte shown after it still shows the outer code.
    /// </summary>
    [Fact]
    public void ANestedCshow_RestoresTheOuterSelection()
    {
        string[] direct = Shown(Ops(Kozuka + "0 0 moveto <82A0> show"));
        string[] nested = Shown(Ops(Kozuka + "0 0 moveto { pop pop /code exch def { pop pop pop } <82A2> cshow" +
                                    " 1 string dup 0 code put show } <82A0> cshow"));
        Assert.Equal(direct, nested);
    }

    /// <summary>
    /// A procedure an error unwound leaves no selection behind: the byte shown after it is that byte,
    /// not the code the unwound <c>cshow</c> had selected.
    /// </summary>
    [Fact]
    public void AnUnwoundCshow_LeavesNoSelection()
    {
        string[] after = Shown(Ops(Kozuka + "0 0 moveto { { pop pop pop stop } <82A0> cshow } stopped pop <A0> show"));
        string[] lone = Shown(Ops(Kozuka + "0 0 moveto <A0> show"));
        Assert.Single(lone);
        Assert.Equal(lone, after);
    }
}
