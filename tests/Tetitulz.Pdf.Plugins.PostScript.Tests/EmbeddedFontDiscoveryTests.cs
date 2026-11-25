// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// How a program reaches a font it embedded.
///
/// <para>A real Type 1 program calls <c>definefont</c> <b>inside its encrypted section</b> — the
/// Adobe Type 1 Font Format's own example ends the <c>eexec</c> portion with
/// <c>dup /FontName get exch definefont pop</c> — and this scanner skips that section whole. So
/// the only clear-text trace of the font is its header's <c>/FontName</c>, and the document's
/// <c>/CMR10 findfont</c> must find the captured program by that name. A <c>findfont</c> consulting
/// only the fonts a program defined in clear text, which real files never do, would draw every
/// embedded font as the Helvetica substitute with a report blaming the program for naming a font
/// that is "not one of the standard 14".</para>
///
/// <para>The association between a font dictionary and its program travels in the font dictionary,
/// so it survives <c>scalefont</c>, <c>makefont</c>, a <c>def</c> and the re-encoding idiom's copy.
/// Held in a thread-static "the font <c>findfont</c> just returned", consumed by the next
/// <c>setfont</c>, two fonts looked up before either was set would swap programs, and every later
/// <c>definefont</c> whose name matched no captured header would be handed the <i>last</i>
/// captured program whatever it was.</para>
/// </summary>
public sealed class EmbeddedFontDiscoveryTests
{
    /// <summary>
    /// A Type 1 program as a document carries it: header, the clear-text dictionary opened with
    /// <c>begin</c> and left on the operand stack by <c>currentdict end</c>, <c>eexec</c>, a body
    /// standing in for the encrypted section (which in a real font holds the <c>definefont</c>),
    /// the zero trailer and <c>cleartomark</c>. No clear-text <c>definefont</c>.
    /// </summary>
    private static string RealFontProgram(string fontName)
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-AdobeFont-1.0: ").Append(fontName).Append(" 001.000\n");
        sb.Append("11 dict begin\n");
        sb.Append("/FontType 1 def\n");
        sb.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n");
        sb.Append("/FontName /").Append(fontName).Append(" def\n");
        sb.Append("/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\n");
        sb.Append("dup 65 /alef put\nreadonly def\n");
        sb.Append("currentdict end\ncurrentfile eexec\n");
        sb.Append("A1B2C3D4E5F60718293A4B5C6D7E8F90\n");
        sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        return sb.ToString();
    }

    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    private static (PostScriptConversionResult Result, List<string> Reports) ConvertCollecting(string ps)
    {
        using var collector = DiagnosticCollector.Start();
        var result = Convert(ps);
        return (result, new List<string>(collector.Messages));
    }

    private static List<PdfDictionary> Fonts(PdfDictionary resources)
    {
        var list = new List<PdfDictionary>();
        if (resources?[PdfName.Font] is not PdfDictionary fonts) return list;
        foreach (var kv in fonts) list.Add((PdfDictionary)kv.Value);
        return list;
    }

    /// <summary>The <c>/FontName</c> of a font's descriptor, or null for a font with none.</summary>
    private static string EmbeddedProgramName(PdfDictionary font)
        => (font[PdfName.FontDescriptor] as PdfDictionary)?[PdfName.FontName] is PdfName n ? n.Value : null;

    private static string ShownFontResource(byte[] content)
    {
        var tf = ContentStreamAssert.Parse(content).First(o => o.Operator == "Tf");
        return tf.Operands[0].TrimStart('/');
    }

    private static PdfDictionary FontNamed(PostScriptConversionResult r, string resource)
        => (PdfDictionary)((PdfDictionary)r.Resources[PdfName.Font])[new PdfName(resource)];

    // ==================================================================
    //  findfont reaches a captured program by its header name
    // ==================================================================

    [Fact]
    public void FindFont_ReachesAProgramCapturedFromItsHeader()
    {
        var (r, reports) = ConvertCollecting(RealFontProgram("MyHebrewFont") +
            "/MyHebrewFont findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        var font = FontNamed(r, ShownFontResource(r.Content));
        Assert.Equal("MyHebrewFont", EmbeddedProgramName(font));
        Assert.Equal("MyHebrewFont", ((PdfName)font[PdfName.BaseFont]).Value);

        // The font is the document's own, so it is not reported as a substitution.
        Assert.DoesNotContain(reports, m => m.Contains("not one of the standard 14"));
    }

    /// <summary>The control: a name no program was captured for still substitutes, and says so.</summary>
    [Fact]
    public void FindFont_WithNoCapturedProgram_StillSubstitutesAndReports()
    {
        var (r, reports) = ConvertCollecting(RealFontProgram("MyHebrewFont") +
            "/SomeOtherFont findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        var font = FontNamed(r, ShownFontResource(r.Content));
        Assert.Null(font[PdfName.FontDescriptor]);
        Assert.Contains(reports, m => m.Contains("SomeOtherFont") && m.Contains("not one of the standard 14"));
    }

    /// <summary>The program's own <c>/Encoding</c> is still read, through the header it came from.</summary>
    [Fact]
    public void FindFont_CapturedProgram_UsesTheHeadersBuiltInEncodingForMetrics()
    {
        var (_, reports) = ConvertCollecting(RealFontProgram("MyHebrewFont") +
            "/MyHebrewFont findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        // An embedded program brings its own glyphs: nothing is "unrepresentable".
        Assert.DoesNotContain(reports, m => m.Contains("have no glyph"));
    }

    // ==================================================================
    //  The association travels in the dictionary
    // ==================================================================

    /// <summary>
    /// Two fonts looked up before either is set — the prologue idiom <c>/F1 … findfont def</c>.
    /// Each must keep its own program.
    /// </summary>
    [Fact]
    public void TwoFontsDefinedUpFront_KeepTheirOwnPrograms()
    {
        var r = Convert(RealFontProgram("FontA") + RealFontProgram("FontB") +
            "/FA /FontA findfont def /FB /FontB findfont def " +
            "FA 12 scalefont setfont newpath 0 0 moveto (A) show " +
            "FB 12 scalefont setfont newpath 0 20 moveto (B) show");

        var tfs = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tf").ToList();
        Assert.Equal(2, tfs.Count);

        var first  = FontNamed(r, tfs[0].Operands[0].TrimStart('/'));
        var second = FontNamed(r, tfs[1].Operands[0].TrimStart('/'));
        Assert.Equal("FontA", EmbeddedProgramName(first));
        Assert.Equal("FontB", EmbeddedProgramName(second));
    }

    /// <summary>
    /// A re-encoding of a standard face defined after a capture is still the standard face: the
    /// captured program belongs to the font whose header named it, not to whatever is defined next.
    /// </summary>
    [Fact]
    public void ReEncodedStandardFont_AfterACapture_IsNotHandedTheCapturedProgram()
    {
        var r = Convert(RealFontProgram("FontA") +
            "/Helvetica findfont dup length dict begin " +
            "{1 index /FID ne {def} {pop pop} ifelse} forall " +
            "/Encoding ISOLatin1Encoding def " +
            "currentdict end /Helvetica-ISO exch definefont pop " +
            "/Helvetica-ISO findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        var font = FontNamed(r, ShownFontResource(r.Content));
        Assert.Null(font[PdfName.FontDescriptor]);
        Assert.Equal("Helvetica", ((PdfName)font[PdfName.BaseFont]).Value);
        Assert.NotNull(font[PdfName.Encoding]);
    }

    /// <summary>A re-encoded copy of an embedded font keeps the program and gains the encoding.</summary>
    [Fact]
    public void ReEncodedCopyOfAnEmbeddedFont_KeepsTheProgram()
    {
        var r = Convert(RealFontProgram("FontA") +
            "/FontA findfont dup length dict begin " +
            "{1 index /FID ne {def} {pop pop} ifelse} forall " +
            "/Encoding ISOLatin1Encoding def " +
            "currentdict end /FontA-ISO exch definefont pop " +
            "/FontA-ISO findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        var font = FontNamed(r, ShownFontResource(r.Content));
        Assert.Equal("FontA", EmbeddedProgramName(font));
        Assert.True(font[PdfName.Encoding] is PdfDictionary enc && enc[PdfName.Differences] != null,
                    "the re-encoding did not reach the emitted font");
    }

    /// <summary>
    /// The clear-text <c>definefont</c> form the embedding tests use still works: a fresh dictionary
    /// defined under a captured header's name is that program.
    /// </summary>
    [Fact]
    public void ClearTextDefineFont_UnderACapturedName_IsThatProgram()
    {
        var r = Convert(RealFontProgram("FontA") +
            "/FontA 12 dict definefont pop /FontA findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        Assert.Equal("FontA", EmbeddedProgramName(FontNamed(r, ShownFontResource(r.Content))));
    }

    /// <summary>
    /// A program whose header declares no <c>/FontName</c> can only be claimed by the next
    /// <c>definefont</c> — the shape of every real embedding is header, program, definefont — and
    /// only once, so the font defined after that gets nothing.
    /// </summary>
    [Fact]
    public void UnnamedProgram_IsClaimedByTheNextDefineFont_Once()
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-AdobeFont-1.0: Anon 001.000\n");
        sb.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n");
        sb.Append("currentfile eexec\nA1B2C3D4E5F60718293A4B5C6D7E8F90\n");
        sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        sb.Append("/First 12 dict definefont pop /Second 12 dict definefont pop ");
        sb.Append("/First findfont 12 scalefont setfont newpath 0 0 moveto (A) show ");
        sb.Append("/Second findfont 12 scalefont setfont newpath 0 20 moveto (B) show");

        var r = Convert(sb.ToString());

        var tfs = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tf").ToList();
        Assert.Equal(2, tfs.Count);
        Assert.NotNull(FontNamed(r, tfs[0].Operands[0].TrimStart('/'))[PdfName.FontDescriptor]);
        Assert.Null(FontNamed(r, tfs[1].Operands[0].TrimStart('/'))[PdfName.FontDescriptor]);
    }

    /// <summary>
    /// The pen advance of an embedded font is measured against the standard face its name maps to,
    /// because the real widths are in charstrings this path never decrypts. That approximation is
    /// reported once, with the <c>/Widths</c> omission it shares a cause with.
    /// </summary>
    [Fact]
    public void EmbeddedFont_ReportsThatItsAdvancesAreApproximate()
    {
        var (_, reports) = ConvertCollecting(RealFontProgram("MyHebrewFont") +
            "/MyHebrewFont findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        Assert.Contains(reports, m => m.Contains("/Widths") && m.Contains("advance"));
    }
}
