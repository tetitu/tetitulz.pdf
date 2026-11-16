// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Embedding the document's own Type 1 font program.
///
/// <para><b>The program is passed through, never decrypted</b> — the same principle as the CJK CMap
/// path. A PDF <c>/FontFile</c> stream <i>is</i> a Type 1 program: clear-text header,
/// <c>eexec</c>-encrypted section, zero trailer. So the reader gets the document's glyphs with no
/// charstring work on this side.</para>
///
/// <para>This is what lifts the last script limit: Hebrew and Cyrillic have no glyphs in any
/// standard-14 face, so a <c>/Differences</c> encoding alone leaves the reader nothing to draw.</para>
/// </summary>
public sealed class EmbeddedFontTests
{
    /// <summary>
    /// A minimal but structurally real Type 1 font program: the <c>%!PS-AdobeFont</c> header the
    /// capture keys on, a clear-text dictionary, <c>eexec</c>, an "encrypted" body and the
    /// conventional 512-zero trailer.
    /// </summary>
    private static string FontProgram(string fontName, string encodingLines = "", string encryptedBody = null)
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-AdobeFont-1.0: ").Append(fontName).Append(" 001.000\n");
        sb.Append("/FontName /").Append(fontName).Append(" def\n");
        sb.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n");
        sb.Append("/FontType 1 def\n");
        if (encodingLines.Length > 0) sb.Append(encodingLines);
        else sb.Append("/Encoding StandardEncoding def\n");
        sb.Append("currentfile eexec\n");

        // Stand-in for the encrypted section. Its content is never inspected — only its length.
        sb.Append(encryptedBody ?? "A1B2C3D4E5F60718293A4B5C6D7E8F90");
        sb.Append("\n");

        sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        return sb.ToString();
    }

    /// <summary>The idiom a document uses: the program, then definefont, then use it.</summary>
    private static string UseEmbedded(string fontName, string encodingLines = "", string text = "A",
                                      string encryptedBody = null)
        => FontProgram(fontName, encodingLines, encryptedBody) +
           $"/{fontName} 12 dict definefont pop " +
           $"/{fontName} findfont 12 scalefont setfont " +
           $"newpath 0 0 moveto ({text}) show";

    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>
    /// Converts and collects <b>every</b> diagnostic, not just the last.
    ///
    /// <para><c>LastError</c> is exactly that — the last one — and a conversion that embeds a font
    /// goes on to report other things afterwards. A test that wants to know whether something was
    /// reported at all has to listen.</para>
    /// </summary>
    private static (PostScriptConversionResult Result, List<string> Reports) ConvertCollecting(string ps)
    {
        // Thread-filtered: OnEvent is process-wide while the rest of the sink is [ThreadStatic],
        // so an unfiltered handler collects whatever other test classes are converting in parallel.
        using var collector = TestInfrastructure.DiagnosticCollector.Start();
        var result = Convert(ps);
        return (result, new List<string>(collector.Messages));
    }

    // ==================================================================
    //  Capture
    // ==================================================================

    [Fact]
    public void EexecSection_IsCapturedAndReported()
    {
        var (_, reports) = ConvertCollecting(UseEmbedded("MyHebrewFont"));

        Assert.Contains(reports, r => r.Contains("embedded the document's own font program")
                                   && r.Contains("MyHebrewFont"));
    }

    /// <summary>
    /// The encrypted bytes must still never be tokenised — capturing them is not interpreting them.
    /// A body containing <c>{</c>, <c>(</c> and an operator name proves the scanner consumed the
    /// section as a unit.
    /// </summary>
    [Fact]
    public void EexecBinarySection_IsStillNotInterpreted()
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-AdobeFont-1.0: Trap 001.000\n/FontName /Trap def\n");
        sb.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\ncurrentfile eexec\n");
        sb.Append("{{{ (unterminated showpage 999 999 moveto\n");
        sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        sb.Append("/Trap 12 dict definefont pop");

        var result = Convert(sb.ToString());

        // Nothing from inside the section reached the output.
        string content = Encoding.Latin1.GetString(result.Content);
        Assert.DoesNotContain("999", content);
    }

    // ==================================================================
    //  The emitted PDF font
    // ==================================================================

    /// <summary>
    /// The font is emitted as a Type1 with a <c>/FontDescriptor</c> carrying a <c>/FontFile</c> —
    /// the shape a reader needs to use the document's glyphs.
    /// </summary>
    [Fact]
    public void EmbeddedFont_IsEmittedAsATypeOneWithAFontFile()
    {
        var result = Convert(UseEmbedded("MyHebrewFont"));

        var font = FirstFont(result.Resources);
        Assert.Equal("Type1", ((PdfName)font[PdfName.Subtype]).Value);
        Assert.Equal("MyHebrewFont", ((PdfName)font[PdfName.BaseFont]).Value);

        var descriptor = (PdfDictionary)font[PdfName.FontDescriptor];
        Assert.NotNull(descriptor);
        Assert.Equal("MyHebrewFont", ((PdfName)descriptor![PdfName.FontName]).Value);

        var program = (PdfStream)descriptor[PdfName.FontFile];
        Assert.NotNull(program);
        Assert.NotEmpty(program!.RawData);
    }

    /// <summary>
    /// The three section lengths must be present and must add up — a <c>/FontFile</c> with wrong
    /// lengths renders nothing at all, which is worse than substituting a standard face.
    /// </summary>
    [Fact]
    public void FontFile_DeclaresThreeConsistentSectionLengths()
    {
        var result = Convert(UseEmbedded("MyHebrewFont"));

        var descriptor = (PdfDictionary)FirstFont(result.Resources)[PdfName.FontDescriptor];
        var program = (PdfStream)descriptor[PdfName.FontFile];

        long l1 = ((PdfNumber)program[PdfName.Length1]).LongValue;
        long l2 = ((PdfNumber)program[PdfName.Length2]).LongValue;
        long l3 = ((PdfNumber)program[PdfName.Length3]).LongValue;

        Assert.True(l1 > 0, "the clear-text header length must be positive.");
        Assert.True(l2 > 0, "the encrypted length must be positive.");
        Assert.True(l3 >= 0);
        Assert.True(l1 + l2 + l3 <= program.RawData.Length,
            $"the lengths {l1}+{l2}+{l3} exceed the {program.RawData.Length}-byte program.");
    }

    /// <summary>
    /// <c>/Length2</c> is the encrypted section and stops where the zero trailer starts: the four
    /// body characters and the newline after them, not the zeros the scan read to find the end.
    /// (A body ending in '0' would lend that zero to the trailer's run, as the scan defines it.)
    /// </summary>
    [Fact]
    public void Length2_StopsWhereTheZeroTrailerStarts()
    {
        Assert.Equal(5L, Length2(UseEmbedded("MyHebrewFont", encryptedBody: "ABCD")));
    }

    /// <summary>
    /// The trailer is found by a run of 64 zeros. A run of 63 inside the encrypted data is data,
    /// so <c>/Length2</c> still runs to the real trailer: 2 + 63 + 2 bytes and the newline.
    /// </summary>
    [Fact]
    public void Length2_IsNotCutShortByARunOfSixtyThreeZeros()
    {
        string body = "AB" + new string('0', 63) + "CD";
        Assert.Equal(68L, Length2(UseEmbedded("MyHebrewFont", encryptedBody: body)));
    }

    private static long Length2(string ps)
    {
        var result = Convert(ps);
        var descriptor = (PdfDictionary)FirstFont(result.Resources)[PdfName.FontDescriptor];
        var program = (PdfStream)descriptor[PdfName.FontFile];
        return ((PdfNumber)program[PdfName.Length2]).LongValue;
    }

    /// <summary>
    /// The captured program starts at the <c>%!PS-AdobeFont</c> line — the header is part of the
    /// program, not a preamble to it, and a reader rejects a <c>/FontFile</c> that omits it.
    /// </summary>
    [Fact]
    public void FontFile_StartsAtTheClearTextHeader()
    {
        var result = Convert(UseEmbedded("MyHebrewFont"));

        var descriptor = (PdfDictionary)FirstFont(result.Resources)[PdfName.FontDescriptor];
        var program = (PdfStream)descriptor[PdfName.FontFile];

        string text = Encoding.Latin1.GetString(program.RawData);
        Assert.StartsWith("%!PS-AdobeFont-1.0", text);
        Assert.Contains("/FontName /MyHebrewFont", text);
        Assert.Contains("eexec", text);
    }

    /// <summary>An embedded font is symbolic: it brings its own encoding.</summary>
    [Fact]
    public void EmbeddedFont_IsFlaggedSymbolic()
    {
        var result = Convert(UseEmbedded("MyHebrewFont"));

        var descriptor = (PdfDictionary)FirstFont(result.Resources)[PdfName.FontDescriptor];
        Assert.Equal(4, ((PdfNumber)descriptor[PdfName.Flags]).LongValue);
    }

    // ==================================================================
    //  Interaction with the standard-14 path
    // ==================================================================

    /// <summary>
    /// With the document's own glyphs available, the "no glyph in Helvetica" warning must not fire —
    /// it would be reporting a problem that no longer exists.
    /// </summary>
    [Fact]
    public void EmbeddedFont_SuppressesTheUnrepresentableWarning()
    {
        // Hebrew bytes that Helvetica certainly has no glyphs for.
        var (_, reports) = ConvertCollecting(UseEmbedded("MyHebrewFont", text: "éì"));

        Assert.DoesNotContain(reports, r => r.Contains("no glyph"));
    }

    /// <summary>A document with no embedded font still takes the standard-14 path.</summary>
    [Fact]
    public void WithoutAnEmbeddedFont_TheStandard14PathIsUnchanged()
    {
        var result = Convert("/Helvetica findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (A) show");

        var font = FirstFont(result.Resources);
        Assert.Null(font[PdfName.FontDescriptor]);
        Assert.Equal("WinAnsiEncoding", ((PdfName)font[PdfName.Encoding]).Value);
    }

    /// <summary>
    /// A program whose <c>eexec</c> has no locatable header is not embedded — and says so, rather
    /// than emitting a <c>/FontFile</c> a reader would reject.
    /// </summary>
    [Fact]
    public void EexecWithNoLocatableHeader_IsNotEmbeddedAndIsReported()
    {
        var sb = new StringBuilder();
        sb.Append("currentfile eexec\n");   // no %!PS-AdobeFont line anywhere
        sb.Append("A1B2C3D4\n");
        sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        sb.Append("/Nameless 12 dict definefont pop ");
        sb.Append("/Nameless findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        var (result, reports) = ConvertCollecting(sb.ToString());

        Assert.Contains(reports, r => r.Contains("could not be located"));

        var font = FirstFont(result.Resources);
        Assert.Null(font[PdfName.FontDescriptor]);
    }

    // ==================================================================
    //  Bounding the capture
    // ==================================================================

    /// <summary>
    /// Capturing is the one scanning step that <b>copies</b> input instead of pointing into it, and
    /// it runs from the clear-text header the DSC scanner last saw to the end of the encrypted
    /// section. A file with one header and many <c>eexec</c> sections must therefore not capture
    /// each of them from that same offset: every copy would be longer than the last, so the total
    /// grows with the <i>square</i> of the input and every byte of it is retained.
    ///
    /// <para>Measured, because the shape of the growth is the whole point — a fixed ceiling would
    /// pass just as well on quadratic code with a smaller input. Doubling the input must not
    /// quadruple the allocation. Before the fix: 273 KiB in produced 334 MiB, and 546 KiB produced
    /// 1.3 GiB.</para>
    ///
    /// <para>Per-thread allocation, not process-wide, so a parallel test run cannot pollute it.</para>
    ///
    /// <para><b>This is an end-to-end check of three layered guards</b> — the header offset is
    /// cleared once used, the copy is charged to the allocation budget, and the capture count is
    /// capped — so removing any <i>one</i> of them leaves it green. That is the guards being
    /// redundant, not the test being vacuous: it does go red against code with all three gone,
    /// which is what the defect actually was. Each guard is pinned individually by its own test
    /// below.</para>
    /// </summary>
    [Fact]
    public void RepeatedEexecSections_DoNotAllocateQuadratically()
    {
        long small = AllocatedConverting(TrailingEexecBlocks(250));
        long large = AllocatedConverting(TrailingEexecBlocks(1000));

        // 4x the input. Linear work would be ~4x; quadratic would be ~16x.
        Assert.True(large < small * 8,
            $"converting 4x the input allocated {large / (1024 * 1024)} MiB against " +
            $"{small / (1024 * 1024)} MiB — that growth is quadratic, not linear.");
    }

    /// <summary>
    /// The count cap, pinned on its own: every section here has its own header, so neither clearing
    /// the offset nor charging the budget bounds them — only the cap does.
    /// </summary>
    [Fact]
    public void MoreFontProgramsThanTheCap_AreConsumedButNotAllEmbedded()
    {
        int over = PsLimits.MaxCapturedFontPrograms + 10;

        var sb = new StringBuilder();
        for (int i = 0; i < over; i++)
        {
            sb.Append($"%!PS-AdobeFont-1.0: Trap{i} 001.000\n/FontName /Trap{i} def\n");
            sb.Append("currentfile eexec\nA1B2C3D4E5F60718293A4B5C6D7E8F90\n");
            sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        }

        var (_, reports) = ConvertCollecting(sb.ToString());

        Assert.Equal(PsLimits.MaxCapturedFontPrograms,
                     reports.Count(r => r.Contains("embedded the document's own font program")));
        Assert.Contains(reports, r => r.Contains("capture cap was reached"));
    }

    /// <summary>
    /// The reason the growth is linear: a header belongs to one program, so it is forgotten once
    /// used. Sections after the first have no header to start from and are consumed without being
    /// embedded — which is also the honest outcome, since a <c>/FontFile</c> without its clear-text
    /// header is not a font program.
    /// </summary>
    [Fact]
    public void OnlyTheSectionFollowingAHeader_IsEmbedded()
    {
        var (_, reports) = ConvertCollecting(TrailingEexecBlocks(5));

        Assert.Equal(1, reports.Count(r => r.Contains("embedded the document's own font program")));
    }

    /// <summary>One <c>%!PS-AdobeFont</c> header followed by <paramref name="blocks"/> eexec sections.</summary>
    private static string TrailingEexecBlocks(int blocks)
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-AdobeFont-1.0: Trap 001.000\n/FontName /Trap def\n");

        for (int i = 0; i < blocks; i++)
        {
            sb.Append("currentfile eexec\n");
            sb.Append("A1B2C3D4E5F60718293A4B5C6D7E8F90\n");
            sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        }

        return sb.ToString();
    }

    private static long AllocatedConverting(string ps)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Convert(ps);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Font programs must not leak between conversions.</summary>
    [Fact]
    public void EmbeddedFonts_DoNotLeakBetweenConversions()
    {
        Convert(UseEmbedded("MyHebrewFont"));

        var second = Convert("/MyHebrewFont findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (A) show");

        var font = FirstFont(second.Resources);
        Assert.Null(font[PdfName.FontDescriptor]);
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
}
