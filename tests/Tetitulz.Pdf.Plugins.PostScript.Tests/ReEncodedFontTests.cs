// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>definefont</c> with a custom <c>/Encoding</c> — the mechanism every legacy non-Latin document
/// uses to select its glyphs.
///
/// <para>The text bytes of a Greek, Hebrew, Cyrillic or Central-European PostScript file are a
/// single-byte codepage; the <c>/Encoding</c> array is what says which glyph each byte means.
/// Carrying it into a PDF <c>/Differences</c> encoding is the difference between the reader drawing
/// the intended characters and drawing the WinAnsi letters at those code points.</para>
/// </summary>
public sealed class ReEncodedFontTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>
    /// The re-encoding idiom exactly as a real prologue writes it: copy the base font's dictionary,
    /// swap <c>/Encoding</c>, define the result.
    /// </summary>
    private const string ReEncodeIdiom =
        "/Helvetica findfont dup length dict begin " +
        "{1 index /FID ne {def} {pop pop} ifelse} forall " +
        "/Encoding EncodingArray def " +
        "currentdict end /CustomFont exch definefont pop ";

    /// <summary>Builds an /Encoding array that maps a few codes onto named glyphs.</summary>
    private static string EncodingArray(params (int Code, string Glyph)[] entries)
    {
        var slots = new string[256];
        for (int i = 0; i < 256; i++) slots[i] = "/.notdef";
        foreach (var (code, glyph) in entries) slots[code] = "/" + glyph;
        return "/EncodingArray [ " + string.Join(" ", slots) + " ] def ";
    }

    // ==================================================================
    //  definefont
    // ==================================================================

    /// <summary><c>definefont</c> is no longer reported as unimplemented.</summary>
    [Fact]
    public void DefineFont_IsImplemented_AndNotReportedAsMissing()
    {
        var result = Convert(EncodingArray((0xE1, "alpha")) + ReEncodeIdiom +
                             "/CustomFont findfont 12 scalefont setfont " +
                             @"newpath 0 0 moveto (\341) show");

        Assert.DoesNotContain("definefont", result.UnimplementedOperators);
    }

    /// <summary>A defined font is retrievable by the name it was defined under.</summary>
    [Fact]
    public void FindFont_ReturnsAFontDefinedByTheProgram()
    {
        var result = Convert(EncodingArray((0xE1, "alpha")) + ReEncodeIdiom +
                             "/CustomFont findfont 12 scalefont setfont " +
                             @"newpath 0 0 moveto (\341) show");

        // It resolved to a real font and drew — no "not one of the standard 14" substitution report.
        Assert.DoesNotContain("not one of the standard 14", result.LastError ?? "");
        ContentStreamAssert.Contains(result.Content, "/F1 1 Tf");
    }

    // ==================================================================
    //  /Differences reaches the PDF font
    // ==================================================================

    /// <summary>
    /// The custom encoding becomes a PDF <c>/Differences</c> encoding naming the same glyphs. This
    /// is the whole fix: without it the reader applies WinAnsi and draws the wrong letters.
    /// </summary>
    [Fact]
    public void ReEncodedFont_EmitsADifferencesEncodingNamingTheGlyphs()
    {
        var result = Convert(
            EncodingArray((0xE1, "alpha"), (0xE2, "beta"), (0xE3, "gamma")) + ReEncodeIdiom +
            "/CustomFont findfont 12 scalefont setfont " +
            @"newpath 0 0 moveto (\341\342\343) show");

        var font = FirstFont(result.Resources);
        var encoding = font[PdfName.Encoding] as PdfDictionary;

        Assert.NotNull(encoding);
        Assert.Equal("WinAnsiEncoding", ((PdfName)encoding![PdfName.BaseEncoding]).Value);

        var differences = (PdfArray)encoding[PdfName.Differences];
        Assert.NotNull(differences);

        var text = RenderArray(differences);
        Assert.Contains("225", text);      // 0xE1, the starting code
        Assert.Contains("/alpha", text);
        Assert.Contains("/beta", text);
        Assert.Contains("/gamma", text);
    }

    /// <summary>
    /// Consecutive codes are run-length encoded — one starting number followed by the names — which
    /// is what keeps a full 256-entry re-encoding from tripling in size.
    /// </summary>
    [Fact]
    public void Differences_RunLengthEncodesConsecutiveCodes()
    {
        var result = Convert(
            EncodingArray((0xE1, "alpha"), (0xE2, "beta"), (0xE3, "gamma")) + ReEncodeIdiom +
            "/CustomFont findfont 12 scalefont setfont " +
            @"newpath 0 0 moveto (\341) show");

        var encoding = (PdfDictionary)FirstFont(result.Resources)[PdfName.Encoding];
        var differences = (PdfArray)encoding[PdfName.Differences];

        // Three consecutive codes: one number then three names, not three number/name pairs.
        Assert.Equal(4, differences.Count);
        Assert.Equal(225, ((PdfNumber)differences[0]).LongValue);
    }

    /// <summary>Non-consecutive codes each get their own starting number.</summary>
    [Fact]
    public void Differences_StartsANewRunForANonConsecutiveCode()
    {
        var result = Convert(
            EncodingArray((0x41, "alpha"), (0x60, "beta")) + ReEncodeIdiom +
            "/CustomFont findfont 12 scalefont setfont " +
            @"newpath 0 0 moveto (A) show");

        var encoding = (PdfDictionary)FirstFont(result.Resources)[PdfName.Encoding];
        var differences = (PdfArray)encoding[PdfName.Differences];

        // 65 /alpha 96 /beta
        Assert.Equal(4, differences.Count);
        Assert.Equal(65, ((PdfNumber)differences[0]).LongValue);
        Assert.Equal(96, ((PdfNumber)differences[2]).LongValue);
    }

    /// <summary>
    /// A font with no custom encoding still gets the plain WinAnsi name, not a pointless
    /// <c>/Differences</c> dictionary.
    /// </summary>
    [Fact]
    public void FontWithoutACustomEncoding_KeepsPlainWinAnsiEncoding()
    {
        var result = Convert("/Helvetica findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (A) show");

        var font = FirstFont(result.Resources);
        Assert.Equal("WinAnsiEncoding", ((PdfName)font[PdfName.Encoding]).Value);
    }

    /// <summary>
    /// An all-<c>.notdef</c> encoding names nothing, so it must not produce a <c>/Differences</c>
    /// array — that would make the font strictly worse than leaving it on WinAnsi.
    /// </summary>
    [Fact]
    public void AllNotdefEncoding_DoesNotProduceADifferencesArray()
    {
        var result = Convert(EncodingArray() + ReEncodeIdiom +
                             "/CustomFont findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (A) show");

        var font = FirstFont(result.Resources);
        Assert.Equal("WinAnsiEncoding", ((PdfName)font[PdfName.Encoding]).Value);
    }

    // ==================================================================
    //  Measurement follows the encoding
    // ==================================================================

    /// <summary>
    /// <c>stringwidth</c> measures through the custom encoding, not through WinAnsi.
    ///
    /// <para>Code 0x41 is normally <c>A</c> (Helvetica 667). Re-encoded to <c>space</c> it must
    /// measure 278. Getting this wrong would centre and right-align every label in a re-encoded
    /// document against the wrong widths.</para>
    /// </summary>
    [Fact]
    public void StringWidth_MeasuresThroughTheCustomEncoding()
    {
        var result = Convert(
            EncodingArray((0x41, "space")) + ReEncodeIdiom +
            "/CustomFont findfont 1 scalefont setfont (A) stringwidth pop " +
            "newpath 0 0 moveto 0 rlineto fill");

        double[] line = ContentStreamAssert.FirstOperandsOf(result.Content, "l");
        Assert.Equal(0.278, line[0], 3);
    }

    /// <summary>The same byte measures its ordinary width when the font is not re-encoded.</summary>
    [Fact]
    public void StringWidth_WithoutAReEncoding_UsesTheWinAnsiWidth()
    {
        var result = Convert(
            "/Helvetica findfont 1 scalefont setfont (A) stringwidth pop " +
            "newpath 0 0 moveto 0 rlineto fill");

        double[] line = ContentStreamAssert.FirstOperandsOf(result.Content, "l");
        Assert.Equal(0.667, line[0], 3);
    }

    // ==================================================================
    //  Isolation
    // ==================================================================

    /// <summary>
    /// A font defined by one conversion must not leak into the next — the registry is
    /// <c>[ThreadStatic]</c> and cleared on entry, like every other slot in this assembly.
    /// </summary>
    [Fact]
    public void DefinedFonts_DoNotLeakBetweenConversions()
    {
        Convert(EncodingArray((0xE1, "alpha")) + ReEncodeIdiom);

        var second = Convert("/CustomFont findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (A) show");

        // The second conversion must not find the first one's font; it substitutes and says so.
        Assert.Contains("CustomFont", second.LastError);
        Assert.Contains("not one of the standard 14", second.LastError);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static PdfDictionary FirstFont(PdfDictionary resources)
    {
        Assert.NotNull(resources);
        var fonts = (PdfDictionary)resources[PdfName.Font];
        Assert.NotNull(fonts);
        foreach (var kv in fonts) return (PdfDictionary)kv.Value;
        throw new Xunit.Sdk.XunitException("no font was registered");
    }

    private static string RenderArray(PdfArray array)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < array.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(array[i] switch
            {
                PdfName n   => "/" + n.Value,
                PdfNumber d => d.LongValue.ToString(),
                _           => "?"
            });
        }
        return sb.ToString();
    }
}
