// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> sets text WinAnsi cannot carry in an embedded font when
/// <see cref="SvgConversionOptions.FontProvider"/> supplies one: a Type 0 font under Identity-H,
/// its glyphs shaped and in visual order, measured for <c>text-anchor</c> by the font's advances.
/// </summary>
/// <remarks>
/// Such text was written as <c>?</c> whatever the caller offered, and reported: the only fonts were
/// the standard faces in WinAnsi. The fixture font is <see cref="MinimalTrueType"/>'s, whose glyph
/// <i>i</i> is the <i>i</i>-th of the covered characters in code-point order, every one 600 wide.
/// </remarks>
public class SvgEmbeddedFontTests
{
    private const string Hebrew = "שלום";

    /// <summary>
    /// The four letters, sorted: ו U+05D5 is glyph 1, ל U+05DC glyph 2, ם U+05DD glyph 3, ש U+05E9
    /// glyph 4. Read right to left the word is ש ל ו ם, so the visual order written is ם ו ל ש.
    /// </summary>
    private const string HebrewGlyphs = "<0003000100020004>";

    [Fact]
    public void HebrewIsSetInAnEmbeddedFontFromTheProvider()
    {
        var (content, fonts, doc) = Convert(@"<text x=""10"" y=""20"" font-size=""12"">" + Hebrew + "</text>", Provider(Hebrew));

        Assert.Equal(new[] { "/F2 12.0000 Tf" }, Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal)));
        Assert.Equal(new[] { HebrewGlyphs + " Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        var font = (PdfDictionary)doc.ResolveObject(fonts[new PdfName("F2")]);
        Assert.Equal("Type0", ((PdfName)font[PdfName.Subtype]).Value);
        Assert.Equal("Identity-H", ((PdfName)font[PdfName.Encoding]).Value);
        var descendant = (PdfDictionary)doc.ResolveObject(((PdfArray)doc.ResolveObject(font[PdfName.DescendantFonts]))[0]);
        Assert.Equal("CIDFontType2", ((PdfName)descendant[PdfName.Subtype]).Value);
        var descriptor = (PdfDictionary)doc.ResolveObject(descendant[PdfName.FontDescriptor]);
        Assert.IsType<PdfStream>(doc.ResolveObject(descriptor[PdfName.FontFile2]));
        // /ToUnicode maps the glyphs back to the letters: the text reads out as it was written.
        Assert.Contains(Hebrew, PdfTextExtractor.ExtractText(doc), StringComparison.Ordinal);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Latin text stays in the standard face; the provider is not asked.</summary>
    [Fact]
    public void LatinStaysInTheStandardFace()
    {
        int calls = 0;
        var (content, fonts, _) = Convert(@"<text x=""10"" y=""20"">Hi</text>", _ => { calls++; return MinimalTrueType.Build("Hi"); });

        Assert.Equal(new[] { "/F1 12.0000 Tf" }, Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal)));
        Assert.Equal(new[] { "(Hi) Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Equal(new[] { "F1" }, fonts.Keys.Select(k => k.Value));
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// Without a provider or substitution the text is <c>?</c>, as before, and the report now names
    /// what would embed a font.
    /// </summary>
    [Fact]
    public void WithoutAFontSourceTheTextIsQuestionMarksAndReported()
    {
        var (content, fonts, _) = Convert(@"<text x=""10"" y=""20"">" + Hebrew + "</text>", null);

        Assert.Equal(new[] { "(????) Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Equal(new[] { "F1" }, fonts.Keys.Select(k => k.Value));
        Assert.Contains("FontProvider", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        // No resolution is attempted without a source: the resolver's own refusal is not reported.
        Assert.DoesNotContain("no font covers", SvgToPdfConverter.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run with Latin and Hebrew is one run in the embedded font, the space included: seven glyphs,
    /// the Latin ones first.
    /// </summary>
    [Fact]
    public void AMixedRunIsWhollySetInTheEmbeddedFont()
    {
        var (content, _, _) = Convert(@"<text x=""10"" y=""20"">ab " + Hebrew + "</text>", Provider("ab " + Hebrew));

        string tj = Assert.Single(Lines(content), l => l.EndsWith(" Tj", StringComparison.Ordinal));
        Assert.StartsWith("<", tj, StringComparison.Ordinal);
        Assert.Equal(7, (tj.IndexOf('>') - 1) / 4);
        // Sorted: space 1, a 2, b 3, then the Hebrew 4–7: "ab " leads, then ם ו ל ש.
        Assert.Equal("<0002000300010006000400050007> Tj", tj);
    }

    /// <summary>
    /// The run is measured by the font's advances: four glyphs of 600 at size 10 are 24 wide, so an
    /// end anchor at 100 starts it at 76. Letter-spacing is added per glyph: 2 each makes 32, so 68.
    /// </summary>
    [Theory]
    [InlineData("", 76f)]
    [InlineData(@" letter-spacing=""2""", 68f)]
    public void TheAnchorMeasuresTheShapedRun(string spacing, float start)
    {
        var (content, _, _) = Convert(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end""" + spacing + ">" + Hebrew + "</text>", Provider(Hebrew));

        string td = Assert.Single(Lines(content), l => l.EndsWith(" Td", StringComparison.Ordinal));
        Assert.Equal(start, float.Parse(td.Split(' ')[0], CultureInfo.InvariantCulture), 3);
        if (spacing.Length > 0) Assert.Contains("2.0000 Tc", Lines(content));
    }

    /// <summary>
    /// <c>Tw</c> applies to the single byte 32 (ISO 32000-1 §9.3.3), which a composite font never
    /// writes, so word-spacing on an embedded run is a <c>TJ</c> adjustment after each space glyph
    /// (§9.4.3: thousandths of the font size, negative to move the next glyph on — 10 at size 10 is
    /// −1000, at size 20 −500), and no <c>Tw</c> is written. The run is measured with it: nine glyphs
    /// of 600 and one spacing of 10 at size 10 are 64 wide, so an end anchor at 100 starts it at 36.
    /// The glyphs are in visual order — the whole run reversed, the space staying in the middle —
    /// with the provider's ids in code-point order (space 1, ו 2, ל 3, ם 4, ש 5). Nothing is reported.
    /// The standard-face control still writes <c>Tw</c>.
    /// </summary>
    /// <remarks>Before batch 82 the spacing was dropped, the run measured without it, and reported.</remarks>
    [Theory]
    [InlineData(10, "-1000", 36f)]
    [InlineData(20, "-500", -18f)]
    public void WordSpacingIsATjAdjustmentAfterEachSpaceOfAnEmbeddedRun(int size, string adjustment, float start)
    {
        var (content, _, _) = Convert(@"<text x=""100"" y=""20"" font-size=""" + size + @""" text-anchor=""end"" word-spacing=""10"">" + Hebrew + " " + Hebrew + "</text>", Provider(Hebrew + " "));
        string reported = SvgToPdfConverter.LastError;   // before the control's conversion resets it
        var (latin, _, _) = Convert(@"<text x=""100"" y=""20"" font-size=""10"" word-spacing=""10"">a b</text>", null);

        Assert.Contains("[<00040002000300050001> " + adjustment + " <0004000200030005>] TJ", Lines(content));
        Assert.DoesNotContain(Lines(content), l => l.EndsWith(" Tw", StringComparison.Ordinal) || l.EndsWith(" Tj", StringComparison.Ordinal));
        string td = Assert.Single(Lines(content), l => l.EndsWith(" Td", StringComparison.Ordinal));
        Assert.Equal(start, float.Parse(td.Split(' ')[0], CultureInfo.InvariantCulture), 3);
        Assert.Null(reported);
        Assert.Contains("10.0000 Tw", Lines(latin));
    }

    /// <summary>
    /// A space at the run's end is spaced too — the adjustment closes the array — and the next run
    /// starts after it: "שלום " is 30 + 10 = 40 wide, the tspan 24, so under an end anchor at 100
    /// the chunk starts at 36 and the tspan at 76. Letter-spacing adds per glyph beside it: 2 on
    /// each of nine glyphs and 10 at the space make 82, so the same anchor starts that run at 18.
    /// </summary>
    [Fact]
    public void ATrailingSpaceIsSpacedAndLetterSpacingAddsToIt()
    {
        var (content, _, _) = Convert(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end"" word-spacing=""10"">" + Hebrew + " <tspan>" + Hebrew + "</tspan></text>", Provider(Hebrew + " "));
        var (both, _, _) = Convert(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end"" word-spacing=""10"" letter-spacing=""2"">" + Hebrew + " " + Hebrew + "</text>", Provider(Hebrew + " "));

        Assert.Contains("[<00040002000300050001> -1000] TJ", Lines(content));
        Assert.Equal(new[] { 36f, 76f }, Lines(content).Where(l => l.EndsWith(" Td", StringComparison.Ordinal)).Select(l => float.Parse(l.Split(' ')[0], CultureInfo.InvariantCulture)));
        Assert.Contains("2.0000 Tc", Lines(both));
        Assert.Contains("[<00040002000300050001> -1000 <0004000200030005>] TJ", Lines(both));
        Assert.Equal(18f, float.Parse(Assert.Single(Lines(both), l => l.EndsWith(" Td", StringComparison.Ordinal)).Split(' ')[0], CultureInfo.InvariantCulture), 3);
    }

    /// <summary>A run with no space in it is written as a plain <c>Tj</c>, whatever its word-spacing, and measured unspaced.</summary>
    [Fact]
    public void ARunWithoutASpaceIsAPlainTjUnderWordSpacing()
    {
        var (content, _, _) = Convert(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end"" word-spacing=""10"">" + Hebrew + "</text>", Provider(Hebrew));

        Assert.Contains(HebrewGlyphs + " Tj", Lines(content));
        Assert.DoesNotContain(Lines(content), l => l.EndsWith(" TJ", StringComparison.Ordinal) || l.EndsWith(" Tw", StringComparison.Ordinal));
        Assert.Equal(76f, float.Parse(Assert.Single(Lines(content), l => l.EndsWith(" Td", StringComparison.Ordinal)).Split(' ')[0], CultureInfo.InvariantCulture), 3);
    }

    /// <summary>
    /// One font serves every run it covers: two elements in the default family are one Type 0 font,
    /// resolved once; a family of its own — the first in the list — is a second.
    /// </summary>
    [Fact]
    public void OneFontServesEveryRunItCovers()
    {
        var families = new List<string>();
        var (content, fonts, _) = Convert(
            @"<text x=""10"" y=""20"">" + Hebrew + @"</text><text x=""10"" y=""40"">" + Hebrew + "</text>" +
            @"<text x=""10"" y=""60"" font-family=""David, serif"">" + Hebrew + "</text>",
            family => { families.Add(family); return MinimalTrueType.Build(Hebrew); });

        Assert.Equal(new[] { "Helvetica", "David" }, families);
        Assert.Equal(new[] { "F1", "F2", "F3" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "F2", "F2", "F3" }, Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal)).Select(l => l.Split(' ')[0].TrimStart('/')));
    }

    /// <summary>A generic family asks for the standard family it falls to, which the resolver knows.</summary>
    [Theory]
    [InlineData("serif", "Times")]
    [InlineData("monospace", "Courier")]
    [InlineData("sans-serif", "Helvetica")]
    public void AGenericFamilyAsksForItsStandardFamily(string generic, string asked)
    {
        var families = new List<string>();
        Convert(@"<text x=""10"" y=""20"" font-family=""" + generic + @""">" + Hebrew + "</text>",
            family => { families.Add(family); return MinimalTrueType.Build(Hebrew); });

        Assert.Equal(new[] { asked }, families);
    }

    /// <summary>
    /// A font already embedded for the family serves a run only when it covers it: Greek after Hebrew
    /// in one family is a second resolution and a second font, not four <c>?</c>.
    /// </summary>
    [Fact]
    public void ASecondFontIsResolvedWhenTheFirstDoesNotCoverARun()
    {
        const string Greek = "αβγ";
        int calls = 0;
        var (content, fonts, _) = Convert(
            @"<text x=""10"" y=""20"">" + Hebrew + @"</text><text x=""10"" y=""40"">" + Greek + "</text>",
            _ => MinimalTrueType.Build(calls++ == 0 ? Hebrew : Greek));

        Assert.Equal(2, calls);
        Assert.Equal(new[] { "F1", "F2", "F3" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "/F2 12.0000 Tf", "/F3 12.0000 Tf" }, Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal)));
        Assert.Equal(new[] { HebrewGlyphs + " Tj", "<000100020003> Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A gradient over the text is laid over its box, whose height is the embedded font's ascent and
    /// descent (800 and −200 per 1000 here): "שלום" at 50 from (10, 100) is 120 wide, 40 up and 10
    /// down, so the box is (10, 60) to (130, 110) — onto the flipped page, y → 200 − (60 + 50·y).
    /// Helvetica's metrics would give −46.25 and 135.9.
    /// </summary>
    [Fact]
    public void TheGradientBoxTakesTheEmbeddedFontsMetrics()
    {
        var (_, _, doc) = Convert(
            @"<linearGradient id=""g""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>" +
            @"<text x=""10"" y=""100"" font-size=""50"" fill=""url(#g)"">" + Hebrew + "</text>", Provider(Hebrew));

        var patterns = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[new PdfName("Pattern")]));
        var pattern = Assert.IsType<PdfDictionary>(doc.ResolveObject(patterns[new PdfName("P0")]));
        var matrix = Assert.IsType<PdfArray>(doc.ResolveObject(pattern[PdfName.Matrix]))
            .Select(o => Math.Round(((PdfNumber)doc.ResolveObject(o)).DoubleValue, 4)).ToArray();
        Assert.Equal(new[] { 120.0, 0, 0, -50, 10, 140 }, matrix);
    }

    /// <summary>
    /// A named family no standard face stands for is asked of the provider for Latin text too: "Hi"
    /// in David is set in the supplied font (H is glyph 1, i glyph 2), nothing reported.
    /// </summary>
    [Fact]
    public void ANamedFamilyTheProviderSuppliesIsEmbeddedForLatinText()
    {
        var families = new List<string>();
        var (content, fonts, _) = Convert(@"<text x=""10"" y=""20"" font-family=""David"">Hi</text>",
            family => { families.Add(family); return MinimalTrueType.Build("Hi"); });

        Assert.Equal(new[] { "David" }, families);
        Assert.Equal(new[] { "F1", "F2" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "<00010002> Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// One the provider declines is the standard face, reported as before — asked once for the two
    /// runs — and the resolver's own reason is not reported: the face is the answer.
    /// </summary>
    [Fact]
    public void ANamedFamilyTheProviderDeclinesIsTheStandardFaceAndReported()
    {
        int calls = 0;
        var (content, fonts, _) = Convert(
            @"<text x=""10"" y=""20"" font-family=""David"">Hi</text><text x=""10"" y=""40"" font-family=""David, serif"">Ho</text>",
            _ => { calls++; return null; });

        Assert.Equal(1, calls);
        Assert.Equal(new[] { "F1", "F2" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "(Hi) Tj", "(Ho) Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Equal("Times-Roman", ((PdfName)((PdfDictionary)fonts[new PdfName("F2")])[PdfName.BaseFont]).Value);
        Assert.Contains("'David' names none", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("no font covers", SvgToPdfConverter.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run with no characters — a tspan of white space after the text's end — asks for no font:
    /// the family only it names is not put to the provider, and no font is written for it. Placement
    /// makes a run per character, so such a run is no run at all by the time fonts are chosen.
    /// </summary>
    [Fact]
    public void AnEmptyRunAsksForNoFont()
    {
        int calls = 0;
        var (content, fonts, _) = Convert(@"<text x=""10"" y=""20"">Hi<tspan font-family=""David""> </tspan></text>", _ => { calls++; return MinimalTrueType.Build("Hi"); });

        Assert.Equal(0, calls);
        Assert.Equal(new[] { "F1" }, fonts.Keys.Select(k => k.Value));
        Assert.Equal(new[] { "(Hi) Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
    }

    /// <summary>A family a standard face stands for, or a generic one, is not asked of the provider for Latin text.</summary>
    [Theory]
    [InlineData("Arial")]
    [InlineData("'Courier New'")]
    [InlineData("serif")]
    public void AStandardFamilyIsNotAskedOfTheProviderForLatinText(string family)
    {
        int calls = 0;
        var (content, fonts, _) = Convert(@"<text x=""10"" y=""20"" font-family=""" + family + @""">Hi</text>", _ => { calls++; return MinimalTrueType.Build("Hi"); });

        Assert.Equal(0, calls);
        Assert.Equal(new[] { "(Hi) Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.All(fonts.Keys, k => Assert.Equal("Type1", ((PdfName)((PdfDictionary)fonts[k])[PdfName.Subtype]).Value));
    }

    /// <summary>
    /// The resolver does not require white space of a font, so a font with no space glyph is
    /// resolved for a run with a space and cannot shape it: the run falls back to <c>?</c>, and the
    /// missing code point is reported.
    /// </summary>
    [Fact]
    public void AFontWithoutAGlyphForTheRunFallsBackAndIsReported()
    {
        var (content, fonts, _) = Convert(@"<text x=""10"" y=""20"">" + Hebrew + " " + Hebrew + "</text>", Provider(Hebrew));

        Assert.Equal(new[] { "(???? ????) Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Equal(new[] { "F1", "F2" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("no glyph for U+0020", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A provider font that does not cover the text is passed over: with no substitution the run is
    /// <c>?</c>, reported with the resolver's reason, and the family is not asked for again.
    /// </summary>
    [Fact]
    public void AProviderFontThatDoesNotCoverTheTextIsReportedOnce()
    {
        int calls = 0;
        var (content, fonts, _) = Convert(
            @"<text x=""10"" y=""20"">" + Hebrew + @"</text><text x=""10"" y=""40"">" + Hebrew + "</text>",
            _ => { calls++; return MinimalTrueType.Build("של"); });

        Assert.Equal(new[] { "(????) Tj", "(????) Tj" }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Equal(new[] { "F1" }, fonts.Keys.Select(k => k.Value));
        Assert.Equal(1, calls);
        Assert.Contains("does not cover", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A weight and a style are a font of their own: bold Hebrew beside regular is a second font,
    /// asked for with the same family.
    /// </summary>
    [Fact]
    public void AWeightIsAFontOfItsOwn()
    {
        var (content, fonts, _) = Convert(
            @"<text x=""10"" y=""20"">" + Hebrew + @"<tspan font-weight=""bold"">" + Hebrew + "</tspan></text>", Provider(Hebrew));

        Assert.Equal(new[] { "F1", "F2", "F3" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "F2", "F3" }, Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal)).Select(l => l.Split(' ')[0].TrimStart('/')));
    }

    /// <summary>
    /// A hostile document naming a family per element asks for a resolution per element, each a
    /// scan of the host's fonts: past <see cref="PdfLimits.MaxSvgFontResolutions"/> no more are made,
    /// and that is reported. The provider's call count is the evidence.
    /// </summary>
    [Fact]
    public void AHostileFamilyPerElementStopsAtTheResolutionCeiling()
    {
        int calls = 0;
        var body = new StringBuilder();
        for (int i = 0; i < PdfLimits.MaxSvgFontResolutions + 20; i++)
            body.Append(@"<text x=""10"" y=""20"" font-family=""Family").Append(i).Append(@""">").Append(Hebrew).Append("</text>");

        var (content, _, _) = Convert(body.ToString(), _ => { calls++; return MinimalTrueType.Build("a"); });

        Assert.Equal(PdfLimits.MaxSvgFontResolutions, calls);
        Assert.Equal(PdfLimits.MaxSvgFontResolutions + 20, Lines(content).Count(l => l == "(????) Tj"));
        Assert.Contains("font resolutions", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static Func<string, byte[]> Provider(string covers) => _ => MinimalTrueType.Build(covers);

    internal static (string Content, PdfDictionary Fonts, PdfDocument Document) Convert(string body, Func<string, byte[]> provider, bool allowSystem = false)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FontProvider = provider, AllowSystemFontSubstitution = allowSystem });
        var doc = PdfDocument.Open(new MemoryStream(pdf));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]));
        return (Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), fonts, doc);
    }

    internal static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}

/// <summary>
/// The host's fonts are used only when <see cref="SvgConversionOptions.AllowSystemFontSubstitution"/>
/// says so — the same licensing gate as every other embedding path.
/// </summary>
/// <remarks>
/// Host-independent: <c>TETITULZ_PDF_FONTDIR</c> replaces the search path, so the one font found is
/// the one the test writes. An installed Hebrew font would otherwise pass the substitution case for
/// the wrong reason and fail the refusal case on a machine that has one.
/// </remarks>
[Collection(SystemFontCacheCollection.Name)]
public sealed class SvgHostFontTests : IDisposable
{
    private const string Hebrew = "שלום";
    private readonly string _dir;
    private readonly string _previousEnv;

    public SvgHostFontTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(FontFileLocator.FontDirEnvVar);
        _dir = Path.Combine(Path.GetTempPath(), "tetitulz-svghost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "host.ttf"), MinimalTrueType.Build(Hebrew));
        File.WriteAllBytes(Path.Combine(_dir, "David.ttf"), MinimalTrueType.BuildNamed(Hebrew + "Hi", "David"));
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _dir);
        FontFileLocator.ResetCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _previousEnv);
        FontFileLocator.ResetCache();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AHostFontIsEmbeddedOnlyWhenSubstitutionIsAllowed()
    {
        var (allowed, allowedFonts, _) = SvgEmbeddedFontTests.Convert(@"<text x=""10"" y=""20"">" + Hebrew + "</text>", null, allowSystem: true);
        var (refused, refusedFonts, _) = SvgEmbeddedFontTests.Convert(@"<text x=""10"" y=""20"">" + Hebrew + "</text>", null, allowSystem: false);

        Assert.Equal(new[] { "F1", "F2" }, allowedFonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Single(SvgEmbeddedFontTests.Lines(allowed), l => l.StartsWith("<", StringComparison.Ordinal) && l.EndsWith(" Tj", StringComparison.Ordinal));
        Assert.Equal(new[] { "F1" }, refusedFonts.Keys.Select(k => k.Value));
        Assert.Contains("(????) Tj", SvgEmbeddedFontTests.Lines(refused));
    }

    /// <summary>
    /// The host is not searched for text a standard face carries: "Hi" in David, a family the host
    /// has, stays Helvetica and is reported, with no resolver reason; Hebrew in David is set in the
    /// host's David — in the same document, so the provider's refusal of the family for the Latin
    /// run (a key without the host) does not stand in for the host's answer.
    /// </summary>
    [Fact]
    public void TheHostIsNotSearchedForTextAStandardFaceCarries()
    {
        var (content, fonts, _) = SvgEmbeddedFontTests.Convert(
            @"<text x=""10"" y=""20"" font-family=""David"">Hi</text><text x=""10"" y=""40"" font-family=""David"">" + Hebrew + "</text>",
            _ => null, allowSystem: true);

        Assert.Equal(new[] { "F1", "F2" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "/F1 12.0000 Tf", "/F2 12.0000 Tf" }, SvgEmbeddedFontTests.Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal)));
        Assert.Equal("(Hi) Tj", SvgEmbeddedFontTests.Lines(content).First(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Contains("'David' names none", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("no font covers", SvgToPdfConverter.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Latin text in families nobody supplies costs no resolution: with no provider, the host closed
    /// to it, there is nothing to ask. Past the ceiling's worth of such runs, Hebrew in David is still
    /// resolved against the host — under a gate that read the options instead, the Latin runs would
    /// have spent every resolution and the Hebrew would be <c>?</c>.
    /// </summary>
    [Fact]
    public void LatinTextInUnknownFamiliesCostsNoResolution()
    {
        var body = new StringBuilder();
        for (int i = 0; i < PdfLimits.MaxSvgFontResolutions; i++)
            body.Append(@"<text x=""10"" y=""20"" font-family=""Family").Append(i).Append(@""">Hi</text>");
        body.Append(@"<text x=""10"" y=""40"" font-family=""David"">").Append(Hebrew).Append("</text>");

        var (content, fonts, _) = SvgEmbeddedFontTests.Convert(body.ToString(), null, allowSystem: true);

        Assert.Equal(new[] { "F1", "F2" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Single(SvgEmbeddedFontTests.Lines(content), l => l.StartsWith("<", StringComparison.Ordinal) && l.EndsWith(" Tj", StringComparison.Ordinal));
        Assert.DoesNotContain("font resolutions", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }
}
