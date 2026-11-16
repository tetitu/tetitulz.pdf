// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>charpath</c> on a standard font's text: the outlines of the face its text is drawn with — the
/// host's, or the one <see cref="PostScriptConversionOptions.OutlineFontProvider"/> supplies — laid at
/// the pen, which advances by the standard face's AFM widths, as <c>show</c>'s does. Every test here
/// supplies the face, so what the host has installed cannot answer for the code.
/// </summary>
public sealed class StandardFontOutlineTests
{
    /// <summary>'A' (glyph 1): a 400 × 700 box from x = 100. 'B' (glyph 2): a quadratic hump.</summary>
    private static readonly byte[] Face = OutlineTrueType.Build(new[]
    {
        OutlineTrueType.Simple(new[]
        {
            new OutlineTrueType.Point(100, 0), new OutlineTrueType.Point(500, 0),
            new OutlineTrueType.Point(500, 700), new OutlineTrueType.Point(100, 700),
        }),
        OutlineTrueType.Simple(new[]
        {
            new OutlineTrueType.Point(0, 0), new OutlineTrueType.Point(150, 300, false), new OutlineTrueType.Point(300, 0),
        }),
    });

    private static PostScriptConversionResult Convert(string ps, System.Func<string, byte[]> provider, List<string> asked = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), new PostScriptConversionOptions
        {
            OutlineFontProvider = name => { asked?.Add(name); return provider(name); },
        });

    private static PostScriptConversionResult Convert(string ps) => Convert(ps, _ => Face);

    private static List<ContentOp> Path(byte[] content)
    {
        var ops = ContentStreamAssert.Parse(content).Where(o => o.Operator is "m" or "l" or "c" or "h").ToList();
        return ops.Where((o, k) => o.Operator != "m" || (k + 1 < ops.Count && ops[k + 1].Operator != "m")).ToList();
    }

    private static double[] Numbers(ContentOp op)
        => op.Operands.Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private const string Helvetica100 = "/Helvetica findfont 100 scalefont setfont ";

    /// <summary>The face's box at size 100 from (10, 20): x = 100 lands at 20, the far corner at (60, 90).</summary>
    [Fact]
    public void CharPath_StandardFont_AppendsTheFacesOutline()
    {
        var path = Path(Convert(Helvetica100 + "newpath 10 20 moveto (A) true charpath fill").Content);

        Assert.Equal(new[] { "m", "l", "l", "l", "h" }, path.Select(o => o.Operator));
        Assert.Equal(new[] { 20.0, 20.0 }, Numbers(path[0]));
        Assert.Equal(new[] { 60.0, 90.0 }, Numbers(path[2]));
    }

    /// <summary>A quadratic is drawn as the cubic with the same curve.</summary>
    [Fact]
    public void CharPath_StandardFont_DrawsAQuadraticAsItsCubic()
    {
        var curve = Path(Convert(Helvetica100 + "newpath 0 0 moveto (B) true charpath fill").Content).Single(o => o.Operator == "c");

        Assert.Equal(new[] { 10.0, 20.0, 20.0, 20.0, 30.0, 0.0 }, Numbers(curve));
    }

    /// <summary>
    /// The pen advances by Helvetica's AFM width, 667 for 'A' — the width <c>show</c> moves by and a
    /// reader draws — not the face's 600.
    /// </summary>
    [Fact]
    public void CharPath_StandardFont_AdvancesByTheAfmWidth()
    {
        var moves = Path(Convert(Helvetica100 + "newpath 0 0 moveto (AA) true charpath fill").Content).Where(o => o.Operator == "m").ToList();

        Assert.Equal(66.7 + 10.0, Numbers(moves[1])[0], 6);
    }

    /// <summary>The current point is left after the last glyph, by the AFM widths: 5 + 66.7 + 66.7.</summary>
    [Fact]
    public void CharPath_StandardFont_LeavesTheCurrentPointAtTheEnd()
    {
        var r = Convert(Helvetica100 + "newpath 5 7 moveto (AA) false charpath currentpoint newpath moveto 0 0 lineto stroke");

        double[] m = Numbers(ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m"));
        Assert.Equal(5 + 66.7 + 66.7, m[0], 6);
        Assert.Equal(7.0, m[1], 6);
    }

    /// <summary>
    /// A re-encoded face draws the glyph its encoding names: 90 re-encoded to /A is the face's 'A',
    /// the box from x = 100 — not the metric box WinAnsi's 'Z', which the face lacks, would get.
    /// </summary>
    [Fact]
    public void CharPath_AReencodedStandardFont_DrawsTheGlyphItsEncodingNames()
    {
        var r = Convert("/Helvetica findfont dup length dict begin {1 index /FID ne {def} {pop pop} ifelse} forall " +
                        "/Encoding StandardEncoding 256 array copy dup 90 /A put def currentdict end " +
                        "/HA exch definefont pop /HA findfont 100 scalefont setfont " +
                        "newpath 0 0 moveto (Z) true charpath fill");

        var path = Path(r.Content);
        Assert.Equal(new[] { "m", "l", "l", "l", "h" }, path.Select(o => o.Operator));
        Assert.Equal(new[] { 10.0, 0.0 }, Numbers(path[0]));
    }

    /// <summary>The face asked for is the standard font's own name, bold and italic in it.</summary>
    [Fact]
    public void CharPath_StandardFont_AsksForTheShownFace()
    {
        var asked = new List<string>();
        Convert("/Times-BoldItalic findfont 10 scalefont setfont newpath 0 0 moveto (A) true charpath fill", _ => Face, asked);

        Assert.Equal(new[] { "Times-BoldItalic" }, asked);
    }

    /// <summary>The document entry point takes the provider too: its face draws the page's <c>charpath</c>.</summary>
    [Fact]
    public void ConvertDocument_TakesTheOutlineFontProvider()
    {
        var asked = new List<string>();
        using var input = new System.IO.MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS\n" + Helvetica100 + "newpath 10 20 moveto (A) true charpath fill showpage\n"));
        using var output = new System.IO.MemoryStream();

        PostScriptConversion.ConvertDocument(input, output, new PostScriptConversionOptions
        {
            OutlineFontProvider = name => { asked.Add(name); return Face; },
        });

        Assert.Equal(new[] { "Helvetica" }, asked);
    }

    /// <summary>A face is read once per conversion, however many times its text is outlined.</summary>
    [Fact]
    public void CharPath_StandardFont_ReadsTheFaceOnce()
    {
        var asked = new List<string>();
        Convert(Helvetica100 + "newpath 0 0 moveto (A) true charpath (A) true charpath fill", _ => Face, asked);

        Assert.Single(asked);
    }

    /// <summary>Real outlines are not reported; a code the face lacks keeps its box and is.</summary>
    [Fact]
    public void CharPath_StandardFont_ReportsOnlyTheCodesItBoxed()
    {
        var clean = Convert(Helvetica100 + "newpath 0 0 moveto (A) true charpath fill");
        var boxed = Convert(Helvetica100 + "newpath 0 0 moveto (AZ) true charpath fill");

        Assert.DoesNotContain("metric box", clean.LastError ?? string.Empty, System.StringComparison.Ordinal);
        Assert.Contains("metric box", boxed.LastError ?? string.Empty, System.StringComparison.Ordinal);
        Assert.Equal(10, Path(boxed.Content).Count);                // the box's outline, then Z's box
    }

    /// <summary>A face whose only glyph, the box, is reached by <paramref name="character"/>.</summary>
    private static byte[] BoxFaceFor(char character, bool symbolic = false) => OutlineTrueType.Build(new[]
    {
        OutlineTrueType.Simple(new[]
        {
            new OutlineTrueType.Point(100, 0), new OutlineTrueType.Point(500, 0),
            new OutlineTrueType.Point(500, 700), new OutlineTrueType.Point(100, 700),
        }),
    }, characters: character.ToString(), symbolic: symbolic);

    private static List<ContentOp> SymbolPath(string font, string text, byte[] face, List<string> asked = null)
        => Path(Convert($"/{font} findfont 100 scalefont setfont newpath 0 0 moveto ({text}) true charpath fill", _ => face, asked).Content);

    /// <summary>
    /// Symbol's code 0x61 is its glyph 'alpha' (§9.6.6.4, the built-in encoding), so the face is asked
    /// for U+03B1 — the box, at x = 100 → 10; Symbol itself is the face asked for.
    /// </summary>
    [Fact]
    public void CharPath_Symbol_FindsTheGlyphItsBuiltInNameStandsFor()
    {
        var asked = new List<string>();
        var path = SymbolPath("Symbol", "a", BoxFaceFor('α'), asked);

        Assert.Equal(new[] { "Symbol" }, asked);
        Assert.Equal(new[] { "m", "l", "l", "l", "h" }, path.Select(o => o.Operator));
        Assert.Equal(new[] { 10.0, 0.0 }, Numbers(path[0]));
    }

    /// <summary>A Windows symbol font maps the code itself, at U+F000 + 0x61, in a (3,0) cmap.</summary>
    [Fact]
    public void CharPath_Symbol_FallsBackToASymbolFontsPrivateUseCode()
    {
        var path = SymbolPath("Symbol", "a", BoxFaceFor('a', symbolic: true));

        Assert.Equal(new[] { 10.0, 0.0 }, Numbers(path[0]));
        Assert.Equal(5, path.Count);
    }

    /// <summary>ZapfDingbats' code 0x21 is 'a1', which its glyph list makes U+2701.</summary>
    [Fact]
    public void CharPath_ZapfDingbats_FindsTheGlyphThroughItsGlyphList()
    {
        var path = SymbolPath("ZapfDingbats", "!", BoxFaceFor('✁'));

        Assert.Equal(new[] { 10.0, 0.0 }, Numbers(path[0]));
    }

    /// <summary>A re-encoded Symbol names its glyph itself: 0x61 re-encoded to /beta asks for U+03B2.</summary>
    [Fact]
    public void CharPath_AReencodedSymbol_FindsTheGlyphItsEncodingNames()
    {
        var path = Path(Convert("/Symbol findfont dup length dict begin {1 index /FID ne {def} {pop pop} ifelse} forall " +
                                "/Encoding 256 array def 0 1 255 { Encoding exch /.notdef put } for Encoding 97 /beta put " +
                                "currentdict end /SB exch definefont pop /SB findfont 100 scalefont setfont " +
                                "newpath 0 0 moveto (a) true charpath fill", _ => BoxFaceFor('β')).Content);

        Assert.Equal(new[] { 10.0, 0.0 }, Numbers(path[0]));
    }

    /// <summary>A Symbol code whose character the face lacks, with no private-use entry, keeps its box, reported.</summary>
    [Fact]
    public void CharPath_ASymbolCodeTheFaceLacks_KeepsItsBox()
    {
        var r = Convert("/Symbol findfont 100 scalefont setfont newpath 0 0 moveto (b) true charpath fill", _ => BoxFaceFor('α'));

        Assert.Contains("metric box", r.LastError ?? string.Empty, System.StringComparison.Ordinal);
    }

    /// <summary>A provider with no face for the name leaves the metric box; one that throws is reported.</summary>
    [Fact]
    public void CharPath_NoFace_KeepsTheMetricBox_AndAThrowingProviderIsReported()
    {
        var none = Convert(Helvetica100 + "newpath 0 0 moveto (A) true charpath fill", _ => null);
        List<string> reports;
        using (var collector = DiagnosticCollector.Start())
        {
            Convert(Helvetica100 + "newpath 0 0 moveto (A) true charpath fill",
                    _ => throw new System.InvalidOperationException("no fonts here"));
            reports = new List<string>(collector.Messages);
        }

        Assert.Contains("metric box", none.LastError ?? string.Empty, System.StringComparison.Ordinal);
        Assert.Equal(5, Path(none.Content).Count);
        Assert.Contains(reports, d => d.Contains("no fonts here", System.StringComparison.Ordinal));
        Assert.Contains(reports, d => d.Contains("metric box", System.StringComparison.Ordinal));
    }
}
