// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> sets a <c>&lt;textPath&gt;</c>'s characters along its path (SVG 1.1
/// §10.13): each one's midpoint on the path at <c>startOffset</c> plus the advance before it, turned
/// to the path's direction there; one whose midpoint is off the path is not drawn.
/// </summary>
/// <remarks>
/// A textPath was reported as not drawn and its text dropped, so a label along a curve — a road name,
/// a circular badge — was missing. Widths are Helvetica's at size 10 (A and B 667, so 6.67); the page
/// is 200 high and flipped, so a y of 100 is 100 and one of 20 is 180.
/// </remarks>
public class SvgTextPathTests
{
    private static string OnPath(string d, string textPath, string text = "AB", string textAttributes = "")
        => $@"<defs><path id=""p"" d=""{d}""/></defs><text font-size=""10"" {textAttributes}><textPath href=""#p"" {textPath}>{text}</textPath></text>";

    /// <summary>Along a straight path from (20, 100): A at 20, B at 26.67, upright.</summary>
    [Fact]
    public void CharactersFollowAStraightPath()
    {
        string c = Content(OnPath("M 20 100 L 180 100", ""));

        Assert.Equal(new[] { "(A) Tj", "(B) Tj" }, Shown(c));
        var td = All(c, "Td");
        Assert.Equal(new[] { 20f, 100f }, td[0]);
        Assert.Equal(new[] { 26.67f, 100f }, td[1]);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Down a vertical path each character is turned 90°, its origin on the path: A at (100, 20).</summary>
    [Fact]
    public void CharactersTurnWithThePath()
    {
        var tm = All(Content(OnPath("M 100 20 L 100 180", "")), "Tm");

        Assert.Equal(2, tm.Length);
        Assert.Equal(new[] { 0f, -1, 1, 0, 100, 180 }, tm[0]);
        Assert.Equal(90f, Angle(tm[1]), 1);
    }

    /// <summary><c>startOffset</c> moves the start along the path: 50 units, or 50% of its 160.</summary>
    [Theory]
    [InlineData(@"startOffset=""50""", 70f)]
    [InlineData(@"startOffset=""50%""", 100f)]
    public void TheStartOffsetMovesTheStart(string offset, float x)
    {
        Assert.Equal(x, All(Content(OnPath("M 20 100 L 180 100", offset)), "Td")[0][0], 2);
    }

    /// <summary>
    /// <c>text-anchor</c> places the text on the offset: "AB", 13.34 long, centred on 100 (starting at
    /// 93.33), or ending there (starting at 86.66).
    /// </summary>
    [Theory]
    [InlineData("middle", 93.33f)]
    [InlineData("end", 86.66f)]
    public void TheAnchorPlacesTheTextOnTheOffset(string anchor, float x)
    {
        string c = Content(OnPath("M 20 100 L 180 100", @"startOffset=""50%""", textAttributes: $@"text-anchor=""{anchor}"""));

        Assert.Equal(x, All(c, "Td")[0][0], 2);
    }

    /// <summary>
    /// Flat text before a textPath is a chunk of its own: X, 6.67 wide, is centred on 100 alone, not
    /// together with the path's characters.
    /// </summary>
    [Fact]
    public void FlatTextBeforeAPathIsAnchoredOnItsOwn()
    {
        string c = Content(@"<defs><path id=""p"" d=""M 20 150 L 180 150""/></defs>" +
                           @"<text x=""100"" y=""50"" font-size=""10"" text-anchor=""middle"">X<textPath href=""#p"">AB</textPath></text>");

        Assert.Equal(96.67f, All(c, "Td")[0][0], 2);
    }

    /// <summary>With an end anchor at the path's start, every character falls before it: none is drawn.</summary>
    [Fact]
    public void CharactersBeforeTheStartAreNotDrawn()
    {
        Assert.Empty(Shown(Content(OnPath("M 20 100 L 180 100", "", textAttributes: @"text-anchor=""end"""))));
    }

    /// <summary>
    /// A closed path's closing side is path too: round a 10-unit square, 40 long, six A's (midpoints
    /// up to 36.7) are all drawn, where an open one, 30 long, drew five.
    /// </summary>
    [Fact]
    public void AClosedPathsLastSideCounts()
    {
        Assert.Equal(6, Shown(Content(OnPath("M 20 100 h 10 v 10 h -10 Z", "", "AAAAAA"))).Length);
    }

    /// <summary>
    /// A path's subpaths join end to end, the jump between them no length: past the first, 10 long, C
    /// continues on the second from (100, 100), its midpoint 16.675 − 10 along — its origin at 103.34.
    /// </summary>
    [Fact]
    public void SubpathsAreJoinedWithoutTheirJump()
    {
        var td = All(Content(OnPath("M 20 100 h 10 M 100 100 h 100", "", "ABC")), "Td");

        Assert.Equal(new[] { 103.34f, 100f }, td[td.Length - 1]);
    }

    /// <summary>
    /// A character off its path takes no part in the text's box: a gradient over "ABC" on a path 10
    /// long is laid over A alone, 6.67 wide, from its place on the path at 20.
    /// </summary>
    [Fact]
    public void ACharacterOffThePathTakesNoPartInItsBox()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" +
            @"<linearGradient id=""g""><stop offset=""0"" stop-color=""red""/><stop offset=""1"" stop-color=""blue""/></linearGradient>" +
            OnPath("M 20 100 L 30 100", "", "ABC", @"fill=""url(#g)""") + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var patterns = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[new Tetitulz.Pdf.Core.Objects.PdfName("Pattern")]);
        var pattern = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(patterns[new Tetitulz.Pdf.Core.Objects.PdfName("P0")]);
        var matrix = (Tetitulz.Pdf.Core.Objects.PdfArray)doc.ResolveObject(pattern[Tetitulz.Pdf.Core.Objects.PdfName.Matrix]);

        Assert.Equal(6.67, Math.Round(((Tetitulz.Pdf.Core.Objects.PdfNumber)matrix[0]).DoubleValue, 2));
        Assert.Equal(20, Math.Round(((Tetitulz.Pdf.Core.Objects.PdfNumber)matrix[4]).DoubleValue, 2));
    }

    /// <summary>A character whose midpoint is off the path's end is not drawn: on a path 10 long, A's midpoint is 3.3 along, B's 10.0 and C's 16.7.</summary>
    [Fact]
    public void CharactersPastTheEndAreNotDrawn()
    {
        string c = Content(OnPath("M 20 100 L 30 100", "", "ABC"));

        Assert.Equal(new[] { "(A) Tj" }, Shown(c));
    }

    /// <summary>
    /// Along a curve each character turns with it: up the left of an arc over the top, the first
    /// character points up (270°), and one near the top lies level.
    /// </summary>
    [Fact]
    public void CharactersTurnAlongACurve()
    {
        string c = Content(OnPath("M 20 100 A 80 80 0 0 1 180 100", "", new string('I', 60)));
        float[] angles = All(c, "Tm").Select(Angle).ToArray();

        Assert.InRange(angles[0], 265f, 275f);
        Assert.Contains(angles, a => a < 5 || a > 355);
    }

    /// <summary>The path's own transform places it: moved 50 down, A sits at y 150 (page 50).</summary>
    [Fact]
    public void ThePathsTransformPlacesIt()
    {
        string c = Content(@"<defs><path id=""p"" d=""M 20 100 L 180 100"" transform=""translate(0 50)""/></defs>" +
                           @"<text font-size=""10""><textPath href=""#p"">A</textPath></text>");

        Assert.Equal(new[] { 20f, 50f }, All(c, "Td")[0]);
    }

    /// <summary>A textPath that names no path — nothing, or a shape that is not one — draws nothing, reported.</summary>
    [Theory]
    [InlineData("#nothing")]
    [InlineData("#r")]
    public void ATextPathThatNamesNoPathIsReported(string href)
    {
        string c = Content(@"<defs><rect id=""r"" width=""100"" height=""10""/></defs>" +
                           $@"<text font-size=""10""><textPath href=""{href}"">AB</textPath></text>");

        Assert.Empty(Shown(c));
        Assert.Contains("textPath", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Text after a textPath continues from where the path's text ended (§10.13.2): "AB" along a
    /// straight path from 20 ends at 33.34, and C starts there; along a vertical path down from
    /// (100, 20) it ends at (100, 33.34) — page y 166.66 — and C stands upright there. It kept its
    /// flat place, at 13.34 from the text's own origin, reported.
    /// </summary>
    [Theory]
    [InlineData("M 20 100 L 180 100", 33.34f, 100f)]
    [InlineData("M 100 20 L 100 180", 100f, 166.66f)]
    public void TextAfterATextPathContinuesFromThePathsEnd(string d, float x, float y)
    {
        string c = Content(OnPath(d, "").Replace("</textPath></text>", "</textPath>C</text>"));

        Assert.Equal(new[] { "(A) Tj", "(B) Tj", "(C) Tj" }, Shown(c));
        float[] td = All(c, "Td")[^1];   // C is upright: Td, after the path's Tm or Td
        Assert.Equal(x, td[0], 2);
        Assert.Equal(y, td[1], 2);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Where no character of the path's text was drawn — all past its end — the text after it starts
    /// at the path's end: (180, 100) here, not 13.34 from the text's origin.
    /// </summary>
    [Fact]
    public void TextAfterAnOverrunPathStartsAtThePathsEnd()
    {
        string c = Content(OnPath("M 20 100 L 180 100", @"startOffset=""200""").Replace("</textPath></text>", "</textPath>C</text>"));

        Assert.Equal(new[] { "(C) Tj" }, Shown(c));
        Assert.Equal(new[] { 180f, 100f }, All(c, "Td")[^1]);
    }

    /// <summary>
    /// A tspan with its own x or y after the path keeps that coordinate; the other follows the path's
    /// end: x 150 with the vertical path's end y (page 166.66), or y 50 (page 150) with the straight
    /// path's end x.
    /// </summary>
    [Theory]
    [InlineData("M 100 20 L 100 180", @"x=""150""", 150f, 166.66f)]
    [InlineData("M 20 100 L 180 100", @"y=""50""", 33.34f, 150f)]
    public void TextAfterATextPathKeepsItsOwnPosition(string d, string position, float x, float y)
    {
        string c = Content(OnPath(d, "").Replace("</textPath></text>", $@"</textPath><tspan {position}>C</tspan></text>"));

        float[] td = All(c, "Td")[^1];
        Assert.Equal(x, td[0], 2);
        Assert.Equal(y, td[1], 2);
    }

    /// <summary>The chunk after the path moves as one: D keeps its place after C, C's 7.22 on and dy 5 down (page 95).</summary>
    [Fact]
    public void TheChunkAfterATextPathMovesAsOne()
    {
        string c = Content(OnPath("M 20 100 L 180 100", "").Replace("</textPath></text>", @"</textPath>C<tspan dy=""5"">D</tspan></text>"));

        float[][] td = All(c, "Td");
        Assert.Equal(new[] { 33.34f, 100f }, td[^2].Select(v => (float)Math.Round(v, 2)).ToArray());
        Assert.Equal(new[] { 40.56f, 95f }, td[^1].Select(v => (float)Math.Round(v, 2)).ToArray());
    }

    /// <summary>
    /// A character's own position list applies along the path (§10.13.2): dx shifts along it, so A
    /// with dx 10 is at 30 and B follows at 36.67; x is a new offset along it from its start, so B
    /// with x 50 is at 20 + 50 = 70. They were laid out flat and then the path placed every character
    /// from the start.
    /// </summary>
    [Theory]
    [InlineData(@"<tspan dx=""10"">A</tspan>B", 30f, 36.67f)]
    [InlineData(@"A<tspan x=""50"">B</tspan>", 20f, 70f)]
    public void PositionListsApplyAlongThePath(string text, float a, float b)
    {
        string c = Content(OnPath("M 20 100 L 180 100", "", text));

        var td = All(c, "Td");
        Assert.Equal(a, td[0][0], 2);
        Assert.Equal(b, td[1][0], 2);
        Assert.Equal(100f, td[0][1], 2);
    }

    /// <summary>
    /// dy shifts across the path, to the glyph's down: 5 below a straight path is page y 95, and 5
    /// "below" a downward path is 5 to the left, x 95. y is not used on a path: A stays on it.
    /// </summary>
    [Theory]
    [InlineData("M 20 100 L 180 100", @"<tspan dy=""5"">A</tspan>", "Td", 20f, 95f)]
    [InlineData("M 100 20 L 100 180", @"<tspan dy=""5"">A</tspan>", "Tm", 95f, 180f)]
    [InlineData("M 20 100 L 180 100", @"<tspan y=""150"">A</tspan>", "Td", 20f, 100f)]
    public void DyShiftsAcrossThePathAndYIsNotUsed(string d, string text, string op, float x, float y)
    {
        float[] m = All(Content(OnPath(d, "", text)), op)[0];

        Assert.Equal(x, m[^2], 2);
        Assert.Equal(y, m[^1], 2);
    }

    /// <summary>
    /// Each x starts a chunk of its own, anchored on its offset: with <c>text-anchor="middle"</c> and
    /// the start at 50% — 80 along, page x 100 — A alone is centred on 100, and B, at x 100 along
    /// from the path's start at 20, is centred on 120: 96.67 and 116.67. A chunk's dx counts for its
    /// anchor: "A" and B with dx 10 are 23.34 long, so A starts at 88.33 and B at 105.
    /// </summary>
    [Theory]
    [InlineData(@"A<tspan x=""100"">B</tspan>", 96.67f, 116.67f)]
    [InlineData(@"A<tspan dx=""10"">B</tspan>", 88.33f, 105f)]
    public void EachXStartsAnAnchoredChunkOnThePath(string text, float a, float b)
    {
        string c = Content(OnPath("M 20 100 L 180 100", @"startOffset=""50%""", text, @"text-anchor=""middle"""));

        var td = All(c, "Td");
        Assert.Equal(a, td[0][0], 1);
        Assert.Equal(b, td[1][0], 1);
    }

    /// <summary>The chunk after the path ends at the next absolute position: D at its own x stays put.</summary>
    [Fact]
    public void TheNextChunkAfterThePathStaysPut()
    {
        string c = Content(OnPath("M 20 100 L 180 100", "").Replace("</textPath></text>", @"</textPath>C<tspan x=""150"">D</tspan></text>"));

        float[][] td = All(c, "Td");
        Assert.Equal(33.34f, td[^2][0], 2);
        Assert.Equal(150f, td[^1][0], 2);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A text matrix's turn in SVG's y-down terms, in degrees [0, 360); the page's y is flipped.</summary>
    private static float Angle(float[] tm)
    {
        double degrees = Math.Atan2(-tm[1], tm[0]) * 180 / Math.PI;
        return (float)((degrees + 360) % 360);
    }

    private static float[][] All(string content, string op)
        => Lines(content).Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => (float)Math.Round(float.Parse(p, CultureInfo.InvariantCulture), 2)).ToArray())
            .ToArray();

    private static string[] Shown(string content) => Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)).ToArray();

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}
