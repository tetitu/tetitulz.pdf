// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Tests for the font-embedding remediation step — the one that closes <b>UA21</b> (deep checker)
/// and <b>UA25</b> (conformance plugin).
///
/// <para>
/// The defect pinned here is that <c>RemediateWithReport</c> could reduce a probe document to two
/// residual errors and then stop for ever, because PDF/UA-1 §7.21.4.1 requires every font to be
/// embedded and grants the Standard 14 no exemption, while no pipeline step could put a font
/// program into the file. The remaining tests pin the properties that make embedding safe: the
/// advance widths the content stream was laid out against survive it, symbolic fonts are refused
/// rather than silently replaced with a Latin substitute, and a subset never drops a glyph the
/// document draws.
/// </para>
///
/// <para>
/// Every document <i>and every font program</i> is synthesised in memory. The TrueType builder
/// below emits a real sfnt — head/hhea/maxp/hmtx/loca/glyf/cmap/name/post/OS/2 — so the embedded
/// bytes go through the same metric parsing and subsetting a font from disk would, without any
/// test depending on a file or on which fonts the host happens to have installed.
/// </para>
/// </summary>
public class FontEmbeddingRemediationTests
{
    // =====================================================================
    //  PDF fixtures
    // =====================================================================

    /// <summary>Assembles a classic-xref PDF; bodies[0] is object 1 and is always the catalog.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies) => BuildPdf(bodies, string.Empty);

    /// <summary>
    /// As <see cref="BuildPdf(IReadOnlyList{string})"/>, with <paramref name="extraTrailerEntries"/>
    /// spliced into the trailer dictionary — which is the only way to mark a document encrypted
    /// without enciphering it.
    /// </summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies, string extraTrailerEntries)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(extraTrailerEntries ?? string.Empty).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    /// <summary>
    /// A tagged single-page document whose only font is object 5. <paramref name="fontBody"/> is
    /// the whole font dictionary, and <paramref name="extraObjects"/> is appended from object 9 on,
    /// so a descriptor or a font file can be added without shifting any other object number.
    /// </summary>
    private static byte[] TaggedPdfWithFont(
        string fontBody, string text = "Hello", params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (" + text + ") Tj ET\nEMC\n"),
            fontBody,
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [7 0 R]] >>",
        };
        bodies.AddRange(extraObjects);
        return BuildPdf(bodies);
    }

    /// <summary>A Standard-14 Helvetica font with no /Widths and no /FontDescriptor — the exact
    /// shape that made UA21/UA25 unclosable.</summary>
    private static byte[] Standard14HelveticaPdf(string baseFont = "Helvetica")
        => TaggedPdfWithFont("<< /Type /Font /Subtype /Type1 /BaseFont /" + baseFont + " >>");

    /// <summary>A TrueType font with an explicit /Widths array and a descriptor carrying no program.</summary>
    private static byte[] NonEmbeddedTrueTypePdf(string text = "Hello")
        => TaggedPdfWithFont(
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 9 0 R >>",
            text,
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>");

    private static string UniformWidths(int first, int last, int width)
    {
        var sb = new StringBuilder("[");
        for (int i = first; i <= last; i++)
            sb.Append(width.ToString(CultureInfo.InvariantCulture)).Append(' ');
        return sb.Append(']').ToString();
    }

    // =====================================================================
    //  Assertions helpers
    // =====================================================================

    private static byte[] Run(byte[] input, FontEmbeddingOptions options, out FontEmbeddingResult result)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        result = PdfAccessibility.EmbedMissingFonts(inMs, outMs, options);
        return outMs.ToArray();
    }

    private static PdfDictionary FirstPageFont(PdfDocument doc, string resourceName = "F1")
    {
        PdfDictionary resources = doc.Pages[0].Resources;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        return doc.ResolveObject(fonts[new PdfName(resourceName)]) as PdfDictionary;
    }

    private static PdfDictionary DescriptorOf(PdfDocument doc, PdfDictionary font)
        => doc.ResolveObject(font[PdfName.FontDescriptor]) as PdfDictionary;

    private static PdfStream EmbeddedProgram(PdfDocument doc, PdfDictionary descriptor)
        => doc.ResolveObject(descriptor[PdfName.FontFile2]) as PdfStream
        ?? doc.ResolveObject(descriptor[PdfName.FontFile3]) as PdfStream;

    private static HashSet<string> AuditCodes(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        AccessibilityAuditReport report = PdfAccessibility.Audit(ms);
        return new HashSet<string>(report.Issues.Select(i => i.Code), StringComparer.Ordinal);
    }

    // =====================================================================
    //  Embedding a supplied program
    // =====================================================================

    [Fact]
    public void SuppliedProgram_IsEmbeddedAsFontFile2()
    {
        byte[] font = MinimalTrueType.Build("Helo");
        byte[] input = NonEmbeddedTrueTypePdf();

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => font,
            SubsetEmbeddedFonts = false,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(0, result.FontsSkipped);
        Assert.True(result.Changed);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary fontDict = FirstPageFont(doc);
        Assert.NotNull(fontDict);

        PdfDictionary descriptor = DescriptorOf(doc, fontDict);
        Assert.NotNull(descriptor);
        Assert.NotNull(descriptor[PdfName.FontFile2]);

        PdfStream program = EmbeddedProgram(doc, descriptor);
        Assert.NotNull(program);
        byte[] decoded = program.GetDecodedData();
        Assert.Equal(font.Length, decoded.Length);
        Assert.Equal(font, decoded);

        // /Length1 is the uncompressed program length, which is what a consumer needs to find the
        // end of the sfnt after inflating.
        Assert.Equal(font.Length, ((PdfNumber)doc.ResolveObject(program[new PdfName("Length1")])).IntValue);
    }

    [Fact]
    public void ExistingWidthsAreNeverTouched()
    {
        byte[] input = NonEmbeddedTrueTypePdf();
        int[] before = WidthsOf(input);

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(before, WidthsOf(output));

        // Every entry must be the document's own 600, not the substitute's metrics.
        Assert.All(WidthsOf(output), w => Assert.Equal(600, w));

        static int[] WidthsOf(byte[] pdf)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
            PdfDictionary fontDict = FirstPageFont(doc);
            var widths = doc.ResolveObject(fontDict[PdfName.Widths]) as PdfArray;
            Assert.NotNull(widths);
            return widths.Select(w => ((PdfNumber)w).IntValue).ToArray();
        }
    }

    [Fact]
    public void ResourceNameAndFontObjectIdentityAreStable()
    {
        byte[] input = NonEmbeddedTrueTypePdf();

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary resources = doc.Pages[0].Resources;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;

        // The content stream says "/F1 12 Tf"; if the resource name or the object it points at
        // moved, the page would stop rendering.
        var reference = fonts[new PdfName("F1")] as PdfReference;
        Assert.NotNull(reference);
        Assert.Equal(5, reference.ReferenceObjectNumber);
    }

    // =====================================================================
    //  Standard-14: widths must be synthesised from the ORIGINAL metrics
    // =====================================================================

    [Theory]
    [InlineData("Helvetica")]
    [InlineData("ArialMT")]        // the same face under the alias real producers write
    public void Standard14WithoutWidths_GetsOriginalAfmWidths(string baseFont)
    {
        byte[] input = Standard14HelveticaPdf(baseFont);

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary fontDict = FirstPageFont(doc);

        var widths = doc.ResolveObject(fontDict[PdfName.Widths]) as PdfArray;
        Assert.NotNull(widths);

        int firstChar = ((PdfNumber)doc.ResolveObject(fontDict[PdfName.FirstChar])).IntValue;
        int lastChar  = ((PdfNumber)doc.ResolveObject(fontDict[PdfName.LastChar])).IntValue;
        Assert.Equal(32, firstChar);
        Assert.Equal(255, lastChar);
        Assert.Equal(lastChar - firstChar + 1, widths.Count);

        // Adobe's Helvetica AFM: space 278, 'A' 667, 'H' 722, 'e' 556, 'l' 222, 'o' 556.
        // These must come from the original Standard-14 metrics, NOT from the embedded substitute
        // (whose glyphs are all 600 wide) — otherwise every text position in the document shifts.
        Assert.Equal(278, WidthOf(widths, firstChar, ' '));
        Assert.Equal(667, WidthOf(widths, firstChar, 'A'));
        Assert.Equal(722, WidthOf(widths, firstChar, 'H'));
        Assert.Equal(556, WidthOf(widths, firstChar, 'e'));
        Assert.Equal(222, WidthOf(widths, firstChar, 'l'));
        Assert.Equal(556, WidthOf(widths, firstChar, 'o'));

        static int WidthOf(PdfArray widths, int firstChar, char c)
            => ((PdfNumber)widths[c - firstChar]).IntValue;
    }

    /// <summary>
    /// A Standard-14 font with no <c>/Encoding</c> gets <c>/StandardEncoding</c> — the table it
    /// already implied — and the warning says so.
    /// </summary>
    /// <remarks>
    /// <para><b>Renamed rather than re-asserted.</b> This test used to be
    /// <c>Standard14WithoutEncoding_GetsWinAnsiAndSaysSo</c> and pinned the wrong behaviour:
    /// <c>PlanSimpleFont</c> discarded the effective encoding it had just resolved
    /// (AdobeStandardEncoding) and substituted WinAnsi. The two tables disagree across the whole
    /// upper half — 0xFB is germandbls in one and ucircumflex in the other, 0xE1 is AE against
    /// aacute, 0xD0 is emdash against eth — so a German invoice drawing <c>(Stra\373e) Tj</c> against
    /// an unencoded /Helvetica came out of remediation reading "Straûe", with the synthesised
    /// /Widths carrying the wrong advance to match. The old warning told the caller only that
    /// 0x27 and 0x60 had changed and that "advance widths are unchanged".</para>
    /// </remarks>
    [Fact]
    public void Standard14WithoutEncoding_GetsStandardEncodingAndSaysSo()
    {
        byte[] output = Run(Standard14HelveticaPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary fontDict = FirstPageFont(doc);

        var encoding = doc.ResolveObject(fontDict[PdfName.Encoding]) as PdfName;
        Assert.NotNull(encoding);
        Assert.Equal("StandardEncoding", encoding.Value);

        // Making an implicit encoding explicit is a real change to the document, so it is stated
        // rather than done quietly.
        Assert.Contains(result.Warnings, w => w.Contains("/StandardEncoding"));
    }

    /// <summary>
    /// The synthesised <c>/Widths</c> for an unencoded Standard-14 font are computed against
    /// <b>StandardEncoding</b>, so the upper half keeps the advances the content stream was laid out
    /// against.
    /// </summary>
    /// <remarks>
    /// <para>0xFB is the code that separates the two tables at their most expensive: germandbls in
    /// StandardEncoding, ucircumflex in WinAnsi, and Helvetica gives them different advances. The
    /// expected numbers are read out of <see cref="Standard14Metrics"/> rather than written as
    /// literals, so the test states the rule ("the width of the glyph StandardEncoding assigns to
    /// this code") rather than a transcription of it — and the second assertion proves the two
    /// really do differ, without which the first would pass under either table.</para>
    /// </remarks>
    [Fact]
    public void Standard14WithoutEncoding_SynthesisesWidthsAgainstStandardEncoding()
    {
        double germandbls  = Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, 'ß');
        double ucircumflex = Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, 'û');
        Assert.NotEqual(germandbls, ucircumflex);   // otherwise this fixture proves nothing

        byte[] output = Run(Standard14HelveticaPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary fontDict = FirstPageFont(doc);

        var widths = doc.ResolveObject(fontDict[PdfName.Widths]) as PdfArray;
        Assert.NotNull(widths);
        int firstChar = ((PdfNumber)doc.ResolveObject(fontDict[PdfName.FirstChar])).IntValue;

        Assert.Equal((int)Math.Round(germandbls),
                     ((PdfNumber)widths[0xFB - firstChar]).IntValue);
    }

    [Fact]
    public void Standard14WithoutWidths_GetsAFullDescriptor()
    {
        byte[] output = Run(Standard14HelveticaPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary descriptor = DescriptorOf(doc, FirstPageFont(doc));
        Assert.NotNull(descriptor);

        // ISO 32000-1 Table 122: a descriptor for a non-Type3 font needs all of these, and each
        // must carry a value of the right *kind* — a present-but-wrong entry would satisfy a mere
        // presence check while still producing an invalid descriptor.
        foreach (string key in new[]
                 { "FontName", "Flags", "FontBBox", "ItalicAngle", "Ascent", "Descent", "CapHeight", "StemV" })
            Assert.NotNull(descriptor[new PdfName(key)]);

        Assert.IsType<PdfName>(descriptor[new PdfName("FontName")]);
        Assert.IsType<PdfNumber>(descriptor[new PdfName("Flags")]);
        Assert.Equal(4, Assert.IsType<PdfArray>(descriptor[new PdfName("FontBBox")]).Count);
        Assert.True(((PdfNumber)descriptor[new PdfName("Ascent")]).DoubleValue > 0);
        Assert.True(((PdfNumber)descriptor[new PdfName("Descent")]).DoubleValue < 0);

        // ...and the program itself is embedded, which is the whole point of the pass.
        var fontFile = doc.ResolveObject(descriptor[PdfName.FontFile2]) as PdfStream;
        Assert.NotNull(fontFile);
        Assert.NotEmpty(fontFile!.RawData);
    }

    // =====================================================================
    //  The gap itself: UA21 + UA25 must actually go away
    // =====================================================================

    [Fact]
    public void EmbeddingClosesUa21AndUa25()
    {
        byte[] input = Standard14HelveticaPdf();

        HashSet<string> before = AuditCodes(input);
        Assert.Contains("UA21", before);
        Assert.Contains("UA25", before);

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        HashSet<string> after = AuditCodes(output);
        Assert.DoesNotContain("UA21", after);
        Assert.DoesNotContain("UA25", after);
    }

    // =====================================================================
    //  Honest refusals
    // =====================================================================

    [Fact]
    public void FontProviderReturningNull_SkipsWithAStatedReasonAndNamesTheConsequence()
    {
        byte[] input = Standard14HelveticaPdf();

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => null,
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Equal(1, result.FontsSkipped);

        FontEmbeddingSkip skip = result.SkippedFonts[0];
        Assert.Equal("Helvetica", skip.BaseFont);
        Assert.Equal("F1", skip.ResourceName);
        Assert.Contains("FontProvider", skip.Reason);
        Assert.Contains("AllowSystemFontSubstitution", skip.Reason);

        // The consequence, not just the reason: a caller reading Warnings must learn which checks
        // are still open and what to do about them.
        Assert.Contains(result.Warnings, w => w.Contains("UA21/UA25"));
        Assert.Contains(result.Warnings, w => w.Contains("FontProvider"));

        // A refusal must not rewrite the document.
        Assert.Equal(input, output);
    }

    [Fact]
    public void NoOptionsAtAll_SkipsRatherThanThrowing()
    {
        byte[] input = Standard14HelveticaPdf();
        byte[] output = Run(input, null, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Equal(1, result.FontsSkipped);
        Assert.Contains("no font source was available", result.SkippedFonts[0].Reason);
        Assert.Equal(input, output);
    }

    [Theory]
    [InlineData("Symbol")]
    [InlineData("ZapfDingbats")]
    [InlineData("ZaDb")]
    public void SymbolicStandard14Fonts_AreSkippedRatherThanSubstituted(string baseFont)
    {
        byte[] input = TaggedPdfWithFont(
            "<< /Type /Font /Subtype /Type1 /BaseFont /" + baseFont + " >>");

        // Substitution is explicitly authorised here — the point is that it is still refused for
        // these two faces, because no Latin text font reproduces their built-in encoding.
        byte[] output = Run(input, new FontEmbeddingOptions
        {
            AllowSystemFontSubstitution = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Equal(1, result.FontsSkipped);

        string reason = result.SkippedFonts[0].Reason;
        Assert.Contains("symbolic", reason);
        Assert.Contains(baseFont == "ZaDb" ? "ZapfDingbats" : baseFont, reason);
        Assert.Contains("FontProvider", reason);

        Assert.Equal(input, output);
    }

    [Fact]
    public void SymbolicFlaggedFont_IsSkippedRatherThanSubstituted()
    {
        byte[] input = TaggedPdfWithFont(
            "<< /Type /Font /Subtype /TrueType /BaseFont /SomeIcons /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /FontDescriptor 9 0 R >>",
            "Hello",
            "<< /Type /FontDescriptor /FontName /SomeIcons /Flags 4 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>");

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            AllowSystemFontSubstitution = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Contains("Symbolic", result.SkippedFonts[0].Reason);
        Assert.Equal(input, output);
    }

    [Fact]
    public void UnsupportedProgramFormat_IsRefusedWithTheFormatNamed()
    {
        // 'ttcf' — a TrueType Collection. Embedding it as one font would produce a broken file.
        byte[] collection = new byte[] { 0x74, 0x74, 0x63, 0x66, 0, 1, 0, 0, 0, 0, 0, 2 };
        byte[] input = NonEmbeddedTrueTypePdf();

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => collection,
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Contains("Collection", result.SkippedFonts[0].Reason);
        Assert.Equal(input, output);
    }

    [Fact]
    public void ThrowingFontProvider_IsContainedAndReported()
    {
        byte[] input = NonEmbeddedTrueTypePdf();

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => throw new InvalidOperationException("no licence for this font"),
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Equal(1, result.FontsSkipped);
        Assert.Contains("no licence for this font", result.SkippedFonts[0].Reason);
        Assert.Equal(input, output);
    }

    [Fact]
    public void UnparseableDocument_IsRefusedLoudlyAndCopiedThrough()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("this is not a PDF at all");

        byte[] output = Run(garbage, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(garbage, output);
    }

    // =====================================================================
    //  Already-embedded fonts
    // =====================================================================

    [Fact]
    public void AlreadyEmbeddedFont_IsLeftUntouchedAndCostsNoRevision()
    {
        byte[] input = TaggedPdfWithFont(
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 9 0 R >>",
            "Hello",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 "
                + "/FontFile2 10 0 R >>",
            StreamObject("/Length1 8", "FAKEFONT"));

        int calls = 0;
        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => { calls++; return MinimalTrueType.Build("Helo"); },
        }, out FontEmbeddingResult result);

        Assert.Equal(0, calls);                       // the provider is never even asked
        Assert.Equal(1, result.FontsAlreadyEmbedded);
        Assert.Equal(0, result.FontsEmbedded);
        Assert.Equal(0, result.FontsSkipped);
        Assert.False(result.Changed);

        // No spurious revision: an unchanged document comes out byte-identical.
        Assert.Equal(input, output);
    }

    [Fact]
    public void Type3Font_IsNotReportedAsMissingAProgram()
    {
        // A Type 3 font's glyphs are content streams inside the file; there is nothing to embed,
        // and PDF/UA does not ask for anything.
        byte[] input = TaggedPdfWithFont(
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] "
                + "/CharProcs << >> /Encoding << /Type /Encoding /Differences [] >> "
                + "/FirstChar 32 /LastChar 32 /Widths [0] >>");

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Equal(0, result.FontsSkipped);
        Assert.Equal(1, result.FontsAlreadyEmbedded);
        Assert.Equal(input, output);
    }

    // =====================================================================
    //  Composite (Type0 / CID) fonts
    // =====================================================================

    [Fact]
    public void Type0FontWithNonEmbeddedDescendant_GetsTheProgramOnTheCidFont()
    {
        byte[] input = TaggedPdfWithFont(
            "<< /Type /Font /Subtype /Type0 /BaseFont /TestFont /Encoding /Identity-H "
                + "/DescendantFonts [9 0 R] >>",
            "Hello",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 10 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>");

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary type0 = FirstPageFont(doc);
        Assert.NotNull(type0);

        // The program belongs on the descendant CIDFont's descriptor, not on the Type0 dictionary.
        Assert.Null(type0[PdfName.FontDescriptor]);

        var descendants = doc.ResolveObject(type0[PdfName.DescendantFonts]) as PdfArray;
        Assert.NotNull(descendants);
        var cidFont = doc.ResolveObject(descendants[0]) as PdfDictionary;
        PdfDictionary descriptor = DescriptorOf(doc, cidFont);
        Assert.NotNull(descriptor[PdfName.FontFile2]);

        // A CIDFont's metrics come from /W and /DW; the step must not invent a simple-font /Widths.
        Assert.Null(type0[PdfName.Widths]);
        Assert.Null(cidFont[PdfName.Widths]);
    }

    [Fact]
    public void Type0Font_IsNeverSubsetted_AndSaysWhy()
    {
        byte[] font = MinimalTrueType.Build("HeloZ");

        byte[] output = Run(TaggedPdfWithFont(
            "<< /Type /Font /Subtype /Type0 /BaseFont /TestFont /Encoding /Identity-H "
                + "/DescendantFonts [9 0 R] >>",
            "Hello",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 10 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>"),
            new FontEmbeddingOptions { FontProvider = _ => font, SubsetEmbeddedFonts = true },
            out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(0, result.FontsSubsetted);
        Assert.Contains(result.Warnings, w => w.Contains("Type0"));

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var descendants = doc.ResolveObject(FirstPageFont(doc)[PdfName.DescendantFonts]) as PdfArray;
        var cidFont = doc.ResolveObject(descendants[0]) as PdfDictionary;
        byte[] embedded = EmbeddedProgram(doc, DescriptorOf(doc, cidFont)).GetDecodedData();
        Assert.Equal(font, embedded);
    }

    // =====================================================================
    //  Subsetting
    // =====================================================================

    [Fact]
    public void Subsetting_KeepsEveryUsedGlyphAndDropsTheRest()
    {
        // The program covers the whole Latin alphabet; the page draws "Hello".
        byte[] font = MinimalTrueType.Build("HeloABCDEFGIJKMNPQRSTUVWXYZ");
        byte[] input = NonEmbeddedTrueTypePdf("Hello");

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => font,
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(1, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary fontDict = FirstPageFont(doc);
        byte[] embedded = EmbeddedProgram(doc, DescriptorOf(doc, fontDict)).GetDecodedData();

        Dictionary<int, int> cmap = MinimalTrueType.ReadCmap(embedded);

        // Every character the content stream draws must still resolve to a real glyph.
        foreach (char c in "Helo")
            Assert.True(cmap.TryGetValue(c, out int gid) && gid != 0,
                "the subset dropped '" + c + "', which the page draws");

        // …and the ones it does not draw must be gone, or nothing was subsetted at all.
        Assert.False(cmap.ContainsKey('Z'));
        Assert.False(cmap.ContainsKey('Q'));
        Assert.True(embedded.Length < font.Length);
    }

    [Fact]
    public void Subsetting_TagsTheFontNameAsIso32000Requires()
    {
        byte[] output = Run(NonEmbeddedTrueTypePdf("Hello"), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary fontDict = FirstPageFont(doc);

        var baseFont = doc.ResolveObject(fontDict[PdfName.BaseFont]) as PdfName;
        Assert.NotNull(baseFont);
        Assert.Matches("^[A-Z]{6}\\+TestFont$", baseFont.Value);

        // §9.8.1: /FontName shall equal /BaseFont, or strict validators reject the file.
        var fontName = doc.ResolveObject(DescriptorOf(doc, fontDict)[new PdfName("FontName")]) as PdfName;
        Assert.Equal(baseFont.Value, fontName.Value);
    }

    [Fact]
    public void SubsettingIsDeterministic()
    {
        byte[] input = NonEmbeddedTrueTypePdf("Hello");

        byte[] first  = Run(input, Options(), out _);
        byte[] second = Run(input, Options(), out _);

        Assert.Equal(first, second);

        static FontEmbeddingOptions Options() => new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        };
    }

    [Fact]
    public void SubsettingDisabled_EmbedsTheWholeProgramUnchanged()
    {
        byte[] font = MinimalTrueType.Build("HeloZQ");

        byte[] output = Run(NonEmbeddedTrueTypePdf("Hello"), new FontEmbeddingOptions
        {
            FontProvider = _ => font,
            SubsetEmbeddedFonts = false,
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        byte[] embedded = EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))).GetDecodedData();
        Assert.Equal(font, embedded);

        // No subset means no subset tag: the name must keep saying it is the whole font.
        var baseFont = doc.ResolveObject(FirstPageFont(doc)[PdfName.BaseFont]) as PdfName;
        Assert.Equal("TestFont", baseFont.Value);
    }

    [Fact]
    public void GlyphsDrawnInsideAFormXObject_SurviveSubsetting()
    {
        // "He" is drawn on the page and "lo" only inside a Form XObject. A scan that stopped at the
        // page would subset away two glyphs the document really shows.
        byte[] input = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /X1 7 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (He) Tj ET\nq 1 0 0 1 0 0 cm /X1 Do Q\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 612 792] "
                       + "/Resources << /Font << /F1 5 0 R >> >>",
                         "BT /F1 12 Tf 72 300 Td (lo) Tj ET\n"),
        });

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(1, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        byte[] embedded = EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))).GetDecodedData();
        Dictionary<int, int> cmap = MinimalTrueType.ReadCmap(embedded);

        foreach (char c in "Helo")
            Assert.True(cmap.TryGetValue(c, out int gid) && gid != 0,
                "the subset dropped '" + c + "', which is drawn inside a Form XObject");
        Assert.False(cmap.ContainsKey('Z'));
    }

    [Fact]
    public void SelfReferencingFormXObject_DoesNotHangTheScan()
    {
        // A Form XObject that invokes itself. Nothing in the format forbids it, and an unguarded
        // usage scan recurses until the stack dies.
        byte[] input = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /X1 7 0 R >> >> >>",
            StreamObject("", "/X1 Do\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 612 792] "
                       + "/Resources << /Font << /F1 5 0 R >> /XObject << /X1 7 0 R >> >>",
                         "BT /F1 12 Tf 72 300 Td (He) Tj ET\n/X1 Do\n"),
        });

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        Assert.NotNull(EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))));
        Assert.True(output.Length > input.Length);
    }

    // =====================================================================
    //  Annotation appearance streams
    // =====================================================================
    //
    //  An annotation's /AP /N is a Form XObject that the viewer draws over the page without any
    //  `Do` in the page content stream. A usage scan that stops at the page therefore never sees
    //  the characters it draws, and a subset built on that answer blanks the annotation — while
    //  the result still says FontsSubsetted, with no warning. This is the AcroForm `Helv` shape:
    //  one font dictionary named by both the page resources and every field's appearance.

    /// <summary>The /AP /N appearance stream of the first annotation on the first page.</summary>
    private static PdfStream FirstAppearanceStream(PdfDocument doc)
    {
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        Assert.NotNull(annots);
        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        var ap = doc.ResolveObject(annot[PdfName.AP]) as PdfDictionary;
        return doc.ResolveObject(ap[PdfName.N]) as PdfStream;
    }

    private static PdfDictionary FontInAppearanceStream(PdfDocument doc, string resourceName = "F1")
    {
        PdfStream form = FirstAppearanceStream(doc);
        Assert.NotNull(form);
        var resources = doc.ResolveObject(form[PdfName.Resources]) as PdfDictionary;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        return doc.ResolveObject(fonts[new PdfName(resourceName)]) as PdfDictionary;
    }

    /// <summary>
    /// A page that draws "He" and a widget whose /AP /N draws "lo" with the very same font
    /// dictionary (object 5) — the shape every AcroForm produces.
    /// </summary>
    private static byte[] WidgetAppearancePdf(
        string pageText = "He", string appearanceText = "lo", string pageFontResource = "/Font << /F1 5 0 R >> ")
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [7 0 R] "
                + "/Resources << " + pageFontResource + ">> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (" + pageText + ") Tj ET\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /Rect [10 10 210 40] /F 4 "
                + "/AP << /N 8 0 R >> >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 30] "
                       + "/Resources << /Font << /F1 5 0 R >> >>",
                         "/Tx BMC q BT /F1 12 Tf 2 2 Td (" + appearanceText + ") Tj ET Q EMC\n"),
        });

    [Fact]
    public void GlyphsDrawnInAnAnnotationAppearanceStream_SurviveSubsetting()
    {
        byte[] output = Run(WidgetAppearancePdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(1, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        byte[] embedded = EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))).GetDecodedData();
        Dictionary<int, int> cmap = MinimalTrueType.ReadCmap(embedded);

        // 'l' and 'o' are drawn only by the widget's appearance stream. Losing them leaves the
        // field rendering .notdef while the report claims a successful subset.
        foreach (char c in "Helo")
            Assert.True(cmap.TryGetValue(c, out int gid) && gid != 0,
                "the subset dropped '" + c + "', which the widget's /AP /N draws");

        // The subset must still be a subset — otherwise this passes for the wrong reason.
        Assert.False(cmap.ContainsKey('Z'));
    }

    [Fact]
    public void FontReachableOnlyFromAnAppearanceStream_IsStillFound()
    {
        // The page names no font at all; only the widget's /AP /N does. A walk that stops at the
        // page resources never sees it, so UA21/UA25 stay open with nothing reported.
        byte[] input = WidgetAppearancePdf(pageText: " ", pageFontResource: string.Empty);

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsExamined);
        Assert.Equal(1, result.FontsEmbedded);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary font = FontInAppearanceStream(doc);
        Assert.NotNull(EmbeddedProgram(doc, DescriptorOf(doc, font)));
    }

    [Fact]
    public void SelfReferencingAppearanceStream_DoesNotHangTheScan()
    {
        // An /AP /N whose own /Resources name it under /XObject, and whose content invokes it.
        // Nothing in the format forbids it; an unguarded appearance walk recurses until the
        // stack dies, and this walk is new, so it needs its own guard and its own proof.
        byte[] input = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [7 0 R] "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (He) Tj ET\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            "<< /Type /Annot /Subtype /Widget /Rect [10 10 210 40] /F 4 /AP << /N 8 0 R >> >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 30] "
                       + "/Resources << /Font << /F1 5 0 R >> /XObject << /X1 8 0 R >> >>",
                         "BT /F1 12 Tf 2 2 Td (lo) Tj ET\n/X1 Do\n"),
        });

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        Dictionary<int, int> cmap = MinimalTrueType.ReadCmap(
            EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))).GetDecodedData());

        foreach (char c in "Helo")
            Assert.True(cmap.TryGetValue(c, out int gid) && gid != 0,
                "the subset dropped '" + c + "' from a self-referencing appearance stream");
    }

    [Fact]
    public void GlyphsDrawnByATilingPattern_SurviveSubsetting()
    {
        // A tiling pattern's content stream is never named by `Do`; it is selected as a colour.
        // Text inside it is text the page shows, so its glyphs must survive the subset.
        byte[] input = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /Pattern << /P1 7 0 R >> >> >>",
            StreamObject("", "/Pattern cs /P1 scn 0 0 100 100 re f\n"
                           + "BT /F1 12 Tf 72 400 Td (He) Tj ET\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            StreamObject("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 "
                       + "/BBox [0 0 50 50] /XStep 50 /YStep 50 "
                       + "/Resources << /Font << /F1 5 0 R >> >>",
                         "BT /F1 8 Tf 1 1 Td (lo) Tj ET\n"),
        });

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        Dictionary<int, int> cmap = MinimalTrueType.ReadCmap(
            EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))).GetDecodedData());

        foreach (char c in "Helo")
            Assert.True(cmap.TryGetValue(c, out int gid) && gid != 0,
                "the subset dropped '" + c + "', which a tiling pattern draws");
    }

    // =====================================================================
    //  Resource-graph reachability — the paths a font is found by
    // =====================================================================

    /// <summary>
    /// A font reachable ONLY through a tiling pattern's own <c>/Resources</c> is still a font the
    /// page draws with, and must be embedded.
    /// </summary>
    /// <remarks>
    /// <b>The existing tiling-pattern test could not see this arm.</b> Its font is named by the
    /// page's <c>/Resources /Font</c> as well, so the collector finds it whether or not it descends
    /// into patterns — deleting the pattern arm of the walk left the whole Accessibility suite
    /// green. The fixture here names the font NOWHERE except inside the pattern, which is the only
    /// shape that can tell the two apart.
    /// </remarks>
    [Fact]
    public void FontReachableOnlyThroughATilingPattern_IsEmbedded()
    {
        Run(FontReachableOnlyVia(
                pageResources: "/Pattern << /P1 7 0 R >>",
                pageContent: "/Pattern cs /P1 scn 0 0 100 100 re f\n",
                carrier: StreamObject(
                    "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 "
                    + "/BBox [0 0 50 50] /XStep 50 /YStep 50 "
                    + "/Resources << /Font << /F1 5 0 R >> >>",
                    "BT /F1 8 Tf 1 1 Td (Hi) Tj ET\n")),
            Embedding(), out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
    }

    /// <summary>
    /// A font reachable ONLY through a Form XObject's own <c>/Resources</c> — the commonest of
    /// these paths, and the last one still uncovered when the other three gained tests.
    /// </summary>
    [Fact]
    public void FontReachableOnlyThroughAFormXObject_IsEmbedded()
    {
        Run(FontReachableOnlyVia(
                pageResources: "/XObject << /Fx 7 0 R >>",
                pageContent: "/Fx Do\n",
                carrier: StreamObject(
                    "/Type /XObject /Subtype /Form /BBox [0 0 100 100] "
                    + "/Resources << /Font << /F1 5 0 R >> >>",
                    "BT /F1 8 Tf 1 1 Td (Hi) Tj ET\n")),
            Embedding(), out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
    }

    /// <summary>
    /// A font reachable ONLY through a Type 3 font's own <c>/Resources</c> — its glyph procedures
    /// are content streams that draw with them.
    /// </summary>
    [Fact]
    public void FontReachableOnlyThroughAType3FontsResources_IsEmbedded()
    {
        Run(FontReachableOnlyVia(
                pageResources: "/Font << /T3 7 0 R >>",
                pageContent: "BT /T3 12 Tf 72 700 Td (a) Tj ET\n",
                carrier: "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] "
                       + "/FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << >> "
                       + "/Encoding << /Type /Encoding >> /FirstChar 97 /LastChar 97 /Widths [100] "
                       + "/Resources << /Font << /F1 5 0 R >> >> >>"),
            Embedding(), out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
    }

    /// <summary>
    /// A font named only by an <c>/ExtGState /Font</c> array. ISO 32000-1 §8.4.5 says that entry
    /// sets the text font exactly as <c>Tf</c> does, so it is a font the page draws with even
    /// though it is not a named font resource.
    /// </summary>
    [Fact]
    public void FontReachableOnlyThroughAnExtGStateFontArray_IsEmbedded()
    {
        Run(FontReachableOnlyVia(
                pageResources: "/ExtGState << /GS1 7 0 R >>",
                pageContent: "/GS1 gs BT 72 700 Td (Hi) Tj ET\n",
                carrier: "<< /Type /ExtGState /Font [5 0 R 12] >>"),
            Embedding(), out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
    }

    /// <summary>
    /// The control the three above need: with the carrier removed the font is unreachable, so
    /// nothing is embedded. Without it, a collector that embedded every font object in the file —
    /// reachable or not — would satisfy all three.
    /// </summary>
    [Fact]
    public void AFontReachableByNoPathAtAllIsNotEmbedded()
    {
        Run(FontReachableOnlyVia(pageResources: "", pageContent: "0 0 10 10 re f\n", carrier: "<< >>"),
            Embedding(), out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
    }

    private static FontEmbeddingOptions Embedding()
        => new FontEmbeddingOptions { FontProvider = _ => MinimalTrueType.Build("Hi") };

    /// <summary>
    /// A page whose non-embedded font (object 5) is named ONLY from <paramref name="carrier"/>,
    /// object 7, which the page reaches through <paramref name="pageResources"/>.
    /// </summary>
    private static byte[] FontReachableOnlyVia(string pageResources, string pageContent, string carrier)
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << " + pageResources + " >> >>",
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            carrier,
        });

    // =====================================================================
    //  Adversarial input — traversal cost
    // =====================================================================

    [Fact]
    public void SharedResourceGraph_IsNotRewalkedOncePerPage()
    {
        // Object numbers make sharing free: every page names one /Resources object, that object
        // names one /XObject dictionary, and every Form XObject in it names that same dictionary
        // back through a /Resources of its own. Resetting the visited set per page therefore
        // re-explores the whole graph N times, and each exploration is itself O(N^2) because the
        // shared /XObject dictionary is re-enumerated from every form. 400 pages is ~130 KB of
        // input and several minutes of walking.
        const int N = 400;

        //  1 catalog | 2 /Pages | 3 shared /Resources | 4 shared /XObject | 5 shared /Contents
        //  6 .. N+5  the forms | N+6 the font | N+7 its descriptor | N+8 .. 2N+7 the pages
        int firstForm     = 6;
        int fontObj       = firstForm + N;
        int descriptorObj = fontObj + 1;
        int firstPage     = descriptorObj + 1;

        var kids = new StringBuilder();
        var xobjects = new StringBuilder();
        for (int i = 0; i < N; i++)
        {
            kids.Append((firstPage + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
            xobjects.Append("/X").Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append((firstForm + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
        }

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [" + kids + "] /Count "
                + N.ToString(CultureInfo.InvariantCulture) + " >>",
            "<< /Font << /F1 " + fontObj.ToString(CultureInfo.InvariantCulture)
                + " 0 R >> /XObject 4 0 R >>",
            "<< " + xobjects + ">>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (He) Tj ET\n"),
        };

        for (int i = 0; i < N; i++)
            bodies.Add(StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                                  + "/Resources << /XObject 4 0 R >>", " "));

        bodies.Add("<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 "
                 + "/LastChar 122 /Widths " + UniformWidths(32, 122, 600)
                 + " /Encoding /WinAnsiEncoding /FontDescriptor "
                 + descriptorObj.ToString(CultureInfo.InvariantCulture) + " 0 R >>");
        bodies.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 32 "
                 + "/FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 800 /Descent -200 "
                 + "/CapHeight 700 /StemV 80 >>");

        for (int i = 0; i < N; i++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                     + "/Contents 5 0 R /Resources 3 0 R >>");

        byte[] input = BuildPdf(bodies);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Run(input, new FontEmbeddingOptions { FontProvider = _ => MinimalTrueType.Build("Helo") },
            out FontEmbeddingResult result);
        stopwatch.Stop();

        // The one shared font is examined once, not once per page.
        Assert.Equal(1, result.FontsExamined);
        Assert.Equal(1, result.FontsEmbedded);

        // Deliberately loose: this asserts "not quadratic in the page count", not a speed. The
        // per-page walk needs minutes for these numbers; one walk needs well under a second.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            "the shared resource graph was re-walked per page — it took " + stopwatch.Elapsed);
    }

    // =====================================================================
    //  A subset is only accepted when it provably kept the glyphs
    // =====================================================================

    [Fact]
    public void SubsetThatLostEveryGlyph_IsRefusedAndTheWholeProgramIsEmbedded()
    {
        // A font that maps NONE of the characters the page shows. PdfFontSubsetter resolves no
        // character and returns a well-formed sfnt holding nothing but .notdef. Accepting that as
        // "the subset succeeded" stamps an ABCDEF+ tag onto the font and blanks every glyph the
        // page draws, which is what this refusal exists to prevent.
        //
        // THE FIXTURE USED TO BE A WINDOWS-SYMBOL (3,0) FONT, and that is no longer a font which
        // loses every glyph: the subsetter now scores (3,0) and folds the 0xF000 PUA convention,
        // so symbol/wingdings/marlett subset properly. The refusal this test guards is still
        // right and still reachable -- an unmapped character set reaches it by a different road --
        // so the fixture moved and the name did not. (A test whose name and body disagree is the
        // failure mode no guard can catch.)
        byte[] font = MinimalTrueType.Build("XYZ");
        Assert.Equal(4, MinimalTrueType.ReadNumGlyphs(font));      // .notdef + X,Y,Z

        byte[] output = Run(NonEmbeddedTrueTypePdf("Hello"), new FontEmbeddingOptions
        {
            FontProvider = _ => font,
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        // The font is still embedded — UA21/UA25 close — but not as a subset.
        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(0, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary fontDict = FirstPageFont(doc);
        byte[] embedded = EmbeddedProgram(doc, DescriptorOf(doc, fontDict)).GetDecodedData();

        Assert.Equal(font, embedded);
        Assert.True(MinimalTrueType.ReadNumGlyphs(embedded) > 1,
            "a .notdef-only program was embedded, so every glyph the page draws is now blank");

        // No subset tag either: a document that claims a full font must be shipping one.
        var baseFont = doc.ResolveObject(fontDict[PdfName.BaseFont]) as PdfName;
        Assert.Equal("TestFont", baseFont.Value);

        Assert.Contains(result.Warnings,
            w => w.Contains("embedded whole rather than subsetted") && w.Contains("cmap"));
    }

    [Fact]
    public void SubsetThatKeptTheGlyphs_IsStillAccepted()
    {
        // The guard above must not be a blanket refusal: the ordinary (3,1) font still subsets.
        byte[] output = Run(NonEmbeddedTrueTypePdf("Hello"), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        byte[] embedded = EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))).GetDecodedData();
        Assert.True(MinimalTrueType.ReadNumGlyphs(embedded) > 1);
    }

    // =====================================================================
    //  Composite (Type0) fonts: a CID is an index into one specific program
    // =====================================================================

    /// <summary>A Type0 document whose descendant CIDFont declares <paramref name="cidSubtype"/>
    /// (pass null to omit /Subtype entirely) and carries no font program.</summary>
    private static byte[] Type0Pdf(string cidSubtype)
        => TaggedPdfWithFont(
            "<< /Type /Font /Subtype /Type0 /BaseFont /TestFont /Encoding /Identity-H "
                + "/DescendantFonts [9 0 R] >>",
            "Hello",
            "<< /Type /Font "
                + (cidSubtype == null ? string.Empty : "/Subtype /" + cidSubtype + " ")
                + "/BaseFont /TestFont "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 10 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>");

    [Fact]
    public void Type0Font_IsNeverGivenAnInstalledSystemFont()
    {
        // A CID indexes the glyph order of the program the document was laid out against. A font
        // this library picks off the host cannot be that program, so every code would select an
        // unrelated glyph — the page would still show text, just not the document's text.
        byte[] input = Type0Pdf("CIDFontType2");

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            AllowSystemFontSubstitution = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        FontEmbeddingSkip skip = Assert.Single(result.SkippedFonts);
        Assert.Contains("CIDs", skip.Reason);
        Assert.Contains("FontProvider", skip.Reason);

        // Nothing was staged, so the document comes back byte-identical.
        Assert.Equal(input, output);
    }

    [Fact]
    public void Type0CidFontType0_RefusesAGlyfProgramRatherThanWritingFontFile2()
    {
        // ISO 32000-1 Table 126: a /CIDFontType0's program has CFF outlines. Hanging a TrueType
        // one on it produces /FontFile2 on a CIDFontType0 — a combination no reader accepts, so
        // the text does not merely change, it disappears.
        byte[] input = Type0Pdf("CIDFontType0");

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        FontEmbeddingSkip skip = Assert.Single(result.SkippedFonts);
        Assert.Contains("CIDFontType0", skip.Reason);
        Assert.Contains("Table 126", skip.Reason);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Type0DescendantWithNoSubtype_IsRefusedRatherThanGuessedAt()
    {
        byte[] input = Type0Pdf(null);

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(0, result.FontsEmbedded);
        FontEmbeddingSkip skip = Assert.Single(result.SkippedFonts);
        Assert.Contains("CIDFontType2", skip.Reason);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Type0FontThatIsEmbedded_WarnsThatCidsBindToTheProgramsGlyphOrder()
    {
        // The legitimate path — a caller-supplied program for a /CIDFontType2 — still goes
        // through, because only the caller can know which program the document used. But it is
        // never silent: nothing in the file can prove the binding, and the caller has to be told.
        byte[] output = Run(Type0Pdf("CIDFontType2"), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Contains(result.Warnings,
            w => w.Contains("CIDs") && w.Contains("/CIDToGIDMap"));

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var descendants = doc.ResolveObject(FirstPageFont(doc)[PdfName.DescendantFonts]) as PdfArray;
        var cidFont = doc.ResolveObject(descendants[0]) as PdfDictionary;
        Assert.NotNull(DescriptorOf(doc, cidFont)[PdfName.FontFile2]);
    }

    // =====================================================================
    //  Two font dictionaries sharing one /FontDescriptor
    // =====================================================================

    [Fact]
    public void TwoFontsSharingOneDescriptor_EachKeepItsOwnProgram()
    {
        // One face registered twice under different /Encodings is legal and real, and both font
        // dictionaries then name the same /FontDescriptor object. A descriptor can carry only one
        // /FontFile, so revising it twice from the original silently discards the first font's
        // program: font 1 ends up resolving to font 2's subset — built for a different character
        // set — and renders .notdef for everything it draws.
        byte[] input = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (abc) Tj ET\n"
                           + "BT /F2 12 Tf 72 300 Td (xyz) Tj ET\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 7 0 R >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /MacRomanEncoding "
                + "/FontDescriptor 7 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        });

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("abcxyz"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        Assert.Equal(2, result.FontsEmbedded);
        Assert.Equal(2, result.FontsSubsetted);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));

        Dictionary<int, int> first = MinimalTrueType.ReadCmap(
            EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc, "F1"))).GetDecodedData());
        Dictionary<int, int> second = MinimalTrueType.ReadCmap(
            EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc, "F2"))).GetDecodedData());

        foreach (char c in "abc")
        {
            Assert.True(first.ContainsKey(c),
                "/F1 draws '" + c + "' but its descriptor now points at the other font's program");
            Assert.False(second.ContainsKey(c));
        }

        foreach (char c in "xyz")
        {
            Assert.True(second.ContainsKey(c),
                "/F2 draws '" + c + "' but its descriptor now points at the other font's program");
            Assert.False(first.ContainsKey(c));
        }
    }

    // =====================================================================
    //  Adversarial input: a Form XObject invoked many times
    // =====================================================================

    [Fact]
    public void FormXObjectInvokedManyTimes_IsScannedOncePerResourceDictionary()
    {
        // The glyph-usage scan's visited set is a PATH guard — it is removed again on the way out —
        // so a form invoked N times in sequence used to be entered N times, and each entry paid a
        // full tokenising pass over the child plus a token array of roughly two bytes per byte of
        // it, straight onto the large-object heap. The old budget could not stop it: it was spent
        // one unit per token PRODUCED, and a stream of nothing but spaces produces none, so the
        // cost was N x sizeof(child) for N units of a 20,000,000-unit budget. 66 KB of PDF bought
        // gigabytes of allocation.
        const int Invocations = 100;
        const int ChildBytes  = 1024 * 1024;

        var pageContent = new StringBuilder(Invocations * 8 + 64);
        pageContent.Append("BT /F1 12 Tf 72 400 Td (Hello) Tj ET\n");
        for (int i = 0; i < Invocations; i++) pageContent.Append("/X1 Do\n");

        byte[] input = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /X1 7 0 R >> >> >>",
            StreamObject("", pageContent.ToString()),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 612 792] "
                       + "/Resources << /Font << /F1 5 0 R >> >>",
                         new string(' ', ChildBytes)),
        });

        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        byte[] output = Run(input, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("HeloZ"),
            SubsetEmbeddedFonts = true,
        }, out FontEmbeddingResult result);

        stopwatch.Stop();
        long allocatedMb = (GC.GetAllocatedBytesForCurrentThread() - before) / (1024 * 1024);

        Assert.Equal(1, result.FontsEmbedded);

        // Scanned once, the whole pass costs a handful of megabytes; scanned once per `Do`, it
        // costs ~100 MB of re-tokenising plus ~200 MB of token arrays. The bound is deliberately
        // far from both numbers — this asserts "not linear in the invocation count", not a size.
        Assert.True(allocatedMb < 48,
            "the Form XObject was re-scanned per invocation: the pass allocated " + allocatedMb
            + " MB for a " + (input.Length / 1024) + " KB document");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            "the Form XObject was re-scanned per invocation — it took " + stopwatch.Elapsed);

        // Memoising must not cost accuracy: the glyphs the page draws still survive subsetting.
        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        Dictionary<int, int> cmap = MinimalTrueType.ReadCmap(
            EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc))).GetDecodedData());
        foreach (char c in "Helo")
            Assert.True(cmap.ContainsKey(c), "the subset dropped '" + c + "', which the page draws");
    }

    // =====================================================================
    //  A count nobody could establish is not a count of zero
    // =====================================================================

    [Fact]
    public void CountNonEmbeddedFonts_ReportsAnHonestCountForAReadableDocument()
    {
        using var doc = PdfDocument.Open(
            new MemoryStream(Standard14HelveticaPdf(), writable: false));

        int count = PdfFontEmbeddingRemediator.CountNonEmbeddedFonts(doc, out string incompleteness);

        Assert.Equal(1, count);
        Assert.Null(incompleteness);
    }

    [Fact]
    public void CountNonEmbeddedFonts_ReportsUnknownRatherThanZero_WhenTheWalkCannotRun()
    {
        // 0 is a positive claim — "every font this document uses already carries a program" — and
        // the pipeline turns it into exactly that sentence when it skips the embedding step. A
        // walk that never ran is not entitled to it: the fonts are still unembedded and UA21/UA25
        // are still open, so the answer has to be "unknown", which no comparison reads as "done".
        byte[] encrypted = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (Hello) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 "
                + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
                + "/U <2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40> >>",
        },
        " /Encrypt 6 0 R /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]");

        using var doc = PdfDocument.Open(new MemoryStream(encrypted, writable: false));
        Assert.True(doc.IsEncrypted);

        int count = PdfFontEmbeddingRemediator.CountNonEmbeddedFonts(doc, out string incompleteness);

        Assert.Equal(PdfFontEmbeddingRemediator.UnknownFontCount, count);
        Assert.NotEqual(0, count);
        Assert.NotNull(incompleteness);
        Assert.Contains("encrypted", incompleteness, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CountNonEmbeddedFonts_ReportsUnknown_ForANullDocument()
    {
        Assert.Equal(PdfFontEmbeddingRemediator.UnknownFontCount,
            PdfFontEmbeddingRemediator.CountNonEmbeddedFonts(null, out string incompleteness));
        Assert.NotNull(incompleteness);
    }

    // =====================================================================
    //  Pipeline wiring
    // =====================================================================

    private static RemediationStepResult EmbedStep(RemediationReport report)
        => report.Steps.Single(s => s.Name == "EmbedFonts");

    [Fact]
    public void Pipeline_SkipsEmbedFontsByDefault_AndNamesTheConsequence()
    {
        using var input  = new MemoryStream(Standard14HelveticaPdf());
        using var output = new MemoryStream();

        RemediationReport report = PdfAccessibility.RemediateWithReport(input, output);

        RemediationStepResult step = EmbedStep(report);
        Assert.True(step.Skipped);
        Assert.Contains("not requested", step.SkipReason);
        Assert.Contains("EmbedMissingFonts", step.SkipReason);

        // Exactly the wording the rest of the pipeline uses for a violation it leaves open.
        Assert.Contains(step.Warnings, w => w.Contains("UA21/UA25"));
        Assert.Contains(report.AllWarnings, w => w.Contains("RemediationOptions.FontEmbedding.FontProvider"));

        // And the audit still reports them, so the warning is not a false alarm.
        Assert.Contains("UA21", report.AfterAudit.Issues.Select(i => i.Code));
    }

    [Fact]
    public void Pipeline_SkipsEmbedFonts_WhenRequestedButNoFontSourceIsAvailable()
    {
        using var input  = new MemoryStream(Standard14HelveticaPdf());
        using var output = new MemoryStream();

        RemediationReport report = PdfAccessibility.RemediateWithReport(input, output,
            new RemediationOptions { EmbedMissingFonts = true, FontEmbedding = null });

        RemediationStepResult step = EmbedStep(report);
        Assert.True(step.Skipped);
        Assert.Contains("no font source", step.SkipReason);
        Assert.Contains(step.Warnings, w => w.Contains("UA21/UA25"));
    }

    [Fact]
    public void Pipeline_EmbedsFonts_WhenAskedAndGivenAProvider()
    {
        using var input  = new MemoryStream(Standard14HelveticaPdf());
        using var output = new MemoryStream();

        RemediationReport report = PdfAccessibility.RemediateWithReport(input, output,
            new RemediationOptions
            {
                Title = "Font Embedding Probe",
                EmbedMissingFonts = true,
                FontEmbedding = new FontEmbeddingOptions
                {
                    FontProvider = _ => MinimalTrueType.Build("Helo"),
                },
            });

        RemediationStepResult step = EmbedStep(report);
        Assert.True(step.Applied);
        Assert.Equal(1, step.ItemsChanged);

        var codes = report.AfterAudit.Issues.Select(i => i.Code).ToList();
        Assert.DoesNotContain("UA21", codes);
        Assert.DoesNotContain("UA25", codes);

        byte[] produced = output.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(produced, writable: false));
        Assert.NotNull(DescriptorOf(doc, FirstPageFont(doc))[PdfName.FontFile2]);
    }

    [Fact]
    public void Pipeline_SkipsEmbedFonts_WhenEveryFontIsAlreadyEmbedded()
    {
        byte[] pdf = TaggedPdfWithFont(
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 9 0 R >>",
            "Hello",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 "
                + "/FontFile2 10 0 R >>",
            StreamObject("/Length1 8", "FAKEFONT"));

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        RemediationReport report = PdfAccessibility.RemediateWithReport(input, output,
            new RemediationOptions
            {
                EmbedMissingFonts = true,
                FontEmbedding = new FontEmbeddingOptions { FontProvider = _ => MinimalTrueType.Build("Helo") },
            });

        RemediationStepResult step = EmbedStep(report);
        Assert.True(step.Skipped);
        Assert.Contains("already satisfied", step.SkipReason);
        Assert.Empty(step.Warnings);
    }

    [Fact]
    public void Pipeline_ReportsEmbedFontsAsSkipped_ForAnUnparseableDocument()
    {
        using var input  = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\nbroken"));
        using var output = new MemoryStream();

        RemediationReport report = PdfAccessibility.RemediateWithReport(input, output);

        // The step must appear in the log even when the whole pipeline is skipped, or a caller
        // enumerating Steps silently loses it.
        RemediationStepResult step = EmbedStep(report);
        Assert.True(step.Skipped);
    }

    // =====================================================================
    //  System substitution embeds the face the name asks for
    // =====================================================================

    /// <summary>
    /// A substituted Standard-14 face is the weight and slant its name says: <c>Helvetica-Bold</c>
    /// embeds a bold program, <c>Helvetica-Oblique</c> an italic one, through
    /// <c>Core.Fonts.FontNameStyle</c> — the renderer's reading of the same name. The dedup sweep's
    /// row D05 (2026-10-21) made the remediator ask for a regular face every time and no test
    /// failed: every other test supplies its program through <c>FontProvider</c>. The face is the
    /// host's, so the test skips where the host has no metric-compatible family in all four styles;
    /// plain Helvetica is the control, a regular face.
    /// </summary>
    [SubstitutableSansStylesTheory]
    [InlineData("Helvetica", false, false)]
    [InlineData("Helvetica-Bold", true, false)]
    [InlineData("Helvetica-Oblique", false, true)]
    [InlineData("Helvetica-BoldOblique", true, true)]
    public void SystemSubstitution_EmbedsTheWeightAndSlantTheNameSays(string baseFont, bool bold, bool italic)
    {
        byte[] output = Run(Standard14HelveticaPdf(baseFont), new FontEmbeddingOptions
        {
            AllowSystemFontSubstitution = true,
            SubsetEmbeddedFonts = false,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfStream program = EmbeddedProgram(doc, DescriptorOf(doc, FirstPageFont(doc)));
        Assert.NotNull(program);

        // head.macStyle: bit 0 bold, bit 1 italic (OpenType 'head').
        int macStyle = MacStyleOf(program.GetDecodedData());
        Assert.Equal(bold, (macStyle & 1) != 0);
        Assert.Equal(italic, (macStyle & 2) != 0);
    }

    private static int MacStyleOf(byte[] sfnt)
    {
        int tables = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + 16 * i;
            if (Encoding.ASCII.GetString(sfnt, record, 4) != "head") continue;
            int offset = (sfnt[record + 8] << 24) | (sfnt[record + 9] << 16) | (sfnt[record + 10] << 8) | sfnt[record + 11];
            return (sfnt[offset + 44] << 8) | sfnt[offset + 45];
        }
        throw new InvalidOperationException("the embedded program has no 'head' table");
    }

    /// <summary>
    /// Runs only where the system resolves Helvetica's substitute family in all four styles to a
    /// metric-compatible face of that style: the test reads the host's fonts, so a host without
    /// them has nothing to say.
    /// </summary>
    private sealed class SubstitutableSansStylesTheoryAttribute : TheoryAttribute
    {
        public SubstitutableSansStylesTheoryAttribute()
        {
            string family = Standard14Names.SystemFamily(StandardFonts.Helvetica);
            foreach (var style in new[] { SkiaSharp.SKFontStyle.Normal, SkiaSharp.SKFontStyle.Bold,
                                          SkiaSharp.SKFontStyle.Italic, SkiaSharp.SKFontStyle.BoldItalic })
            {
                using var face = SkiaSharp.SKTypeface.FromFamilyName(family, style);
                bool styled = face != null
                    && face.IsBold == (style.Weight >= (int)SkiaSharp.SKFontStyleWeight.Bold)
                    && face.IsItalic == (style.Slant != SkiaSharp.SKFontStyleSlant.Upright);
                if (face == null || Folded(face.FamilyName) != Folded(family) || !styled)
                {
                    Skip = "requires the '" + family + "' family in regular, bold, italic and bold italic, " +
                           "which this host does not resolve";
                    return;
                }
            }
        }

        private static string Folded(string name)
            => new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    // =====================================================================
    //  A minimal, real TrueType font, built in memory
    // =====================================================================

}
