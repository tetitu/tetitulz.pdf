// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The font-embedding remediator used to decline every CFF program outright — "only TrueType
/// (glyf) programs can be subsetted" — and embed it whole. Plan 11 §5e slice 2 replaced that with
/// charstring blanking for sfnt-wrapped CFF, and left a narrower, different decline for bare CFF.
///
/// <para><b>What makes these falsifiable.</b> A blanking subsetter produces a program the same
/// length as the input, whose cmap is untouched — so both "the output is smaller" and "the output
/// still covers the text" pass with the whole feature deleted. The assertions here read the
/// embedded <c>/FontFile3</c> back out and count which charstrings are still their original bytes.</para>
/// </summary>
public class CffFontEmbeddingSubsetTests
{
    /// <summary>The text the fixture page draws — four distinct characters.</summary>
    private const string PageText = "Hello";

    /// <summary>Everything a provider font covers, so most of it is never drawn.</summary>
    private const string ProviderCoverage = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // =====================================================================
    //  Fixtures — a tagged page whose only font is non-embedded
    // =====================================================================

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder();
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
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string UniformWidths(int first, int last, int width)
    {
        var sb = new StringBuilder("[");
        for (int i = first; i <= last; i++)
            sb.Append(width.ToString(CultureInfo.InvariantCulture)).Append(' ');
        return sb.Append(']').ToString();
    }

    private static byte[] NonEmbeddedFontPdf()
    {
        string content = "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (" + PageText + ") Tj ET\nEMC\n";
        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
                + content + "\nendstream",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + UniformWidths(32, 122, 600) + " /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 9 0 R >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [7 0 R]] >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        });
    }

    private static byte[] Run(byte[] input, FontEmbeddingOptions options, out FontEmbeddingResult result)
    {
        using var inMs = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        result = PdfAccessibility.EmbedMissingFonts(inMs, outMs, options);
        return outMs.ToArray();
    }

    // =====================================================================
    //  The OpenType/CFF path — subsetted, not declined
    // =====================================================================

    /// <summary>
    /// The claim the old decline denied: an OpenType/CFF program supplied to the remediator comes
    /// out with the undrawn glyphs' charstrings emptied.
    /// </summary>
    [Fact]
    public void AnOpenTypeCffProgram_IsSubsettedRatherThanEmbeddedWhole()
    {
        byte[] otf = MinimalOpenTypeCff.Build(ProviderCoverage, padTo: 120);

        byte[] output = Run(NonEmbeddedFontPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => otf,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        byte[] embedded = PdfContentProbe.EmbeddedFontProgram(output, "FontFile3");
        Assert.NotNull(embedded);

        List<byte[]> charstrings = MinimalOpenTypeCff.ReadCharstrings(embedded!);
        Assert.Equal(ProviderCoverage.Length + 1, charstrings.Count);

        int blanked = charstrings.Count(MinimalOpenTypeCff.IsBlanked);

        // "Hello" is H, e, l, o — four distinct glyphs. Everything else in a 52-letter font is
        // dead weight, and .notdef reads as blanked because the fixture gives it a bare endchar.
        Assert.True(blanked >= 48,
            $"only {blanked} of {charstrings.Count} charstrings were blanked; the page draws four " +
            "distinct letters, so the CFF path is not subsetting");
    }

    /// <summary>
    /// The other half: every glyph the page actually draws must survive byte for byte. Without this
    /// the count assertion above is satisfied by a subsetter that blanked the whole font.
    /// </summary>
    [Fact]
    public void TheGlyphsThePageDrawsSurviveTheCffSubset()
    {
        byte[] otf = MinimalOpenTypeCff.Build(ProviderCoverage, padTo: 120);
        List<byte[]> original = MinimalOpenTypeCff.ReadCharstrings(otf);
        string sorted = new string(new SortedSet<char>(ProviderCoverage).ToArray());

        byte[] output = Run(NonEmbeddedFontPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => otf,
        }, out _);

        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(
            PdfContentProbe.EmbeddedFontProgram(output, "FontFile3")!);

        foreach (char c in new SortedSet<char>(PageText))
        {
            int gid = sorted.IndexOf(c) + 1;
            Assert.True(gid > 0, $"the fixture font does not cover '{c}', so this proves nothing");
            Assert.True(original[gid].SequenceEqual(after[gid]),
                $"the charstring for '{c}' (GID {gid}) was blanked, but the page draws it");
        }
    }

    /// <summary>
    /// ISO 32000-1 §9.6.4 — a partial program is named <c>ABCDEF+</c>. The remediator already
    /// applied this for glyf subsets; a CFF subset is the same claim and must carry the same tag.
    /// </summary>
    [Fact]
    public void ACffSubsetIsTaggedAsASubset()
    {
        byte[] output = Run(NonEmbeddedFontPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalOpenTypeCff.Build(ProviderCoverage, padTo: 120),
        }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        PdfDictionary resources = doc.Pages[0].Resources;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        var fontDict = doc.ResolveObject(fonts["F1"]) as PdfDictionary;
        var baseFont = doc.ResolveObject(fontDict[PdfName.BaseFont]) as PdfName;

        Assert.NotNull(baseFont);
        Assert.True(baseFont!.Value.Length > 7 && baseFont.Value[6] == '+',
            $"/BaseFont is \"{baseFont.Value}\", which carries no ABCDEF+ subset tag");
    }

    /// <summary>
    /// With subsetting switched off the whole program goes in — and no subset tag, because there is
    /// no restriction to declare. This is the control that shows the two tests above are measuring
    /// subsetting rather than something the pipeline does anyway.
    /// </summary>
    [Fact]
    public void WithSubsettingDisabled_TheWholeCffProgramIsEmbeddedAndUntagged()
    {
        byte[] otf = MinimalOpenTypeCff.Build(ProviderCoverage, padTo: 120);
        List<byte[]> original = MinimalOpenTypeCff.ReadCharstrings(otf);

        byte[] output = Run(NonEmbeddedFontPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => otf,
            SubsetEmbeddedFonts = false,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(
            PdfContentProbe.EmbeddedFontProgram(output, "FontFile3")!);
        for (int gid = 1; gid < original.Count; gid++)
            Assert.True(original[gid].SequenceEqual(after[gid]),
                $"glyph {gid} was blanked with SubsetEmbeddedFonts = false");

        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var fonts = doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]) as PdfDictionary;
        var fontDict = doc.ResolveObject(fonts["F1"]) as PdfDictionary;
        var baseFont = doc.ResolveObject(fontDict[PdfName.BaseFont]) as PdfName;
        Assert.False(baseFont!.Value.Length > 7 && baseFont.Value[6] == '+',
            "a whole program was tagged ABCDEF+, which claims a glyph restriction the file does not have");
    }

    // =====================================================================
    //  Refusals, each with the reason a caller can act on
    // =====================================================================

    /// <summary>
    /// A bare CFF (<c>/FontFile3 /Type1C</c>) has no cmap, so the obvious
    /// <c>TrueTypeFontProgram.TryGetGlyph</c> answers nothing for it — which is why this whole font
    /// shape declined for the first half of this round. It resolves through the font's own glyph
    /// <b>names</b> instead, which is a fact the font states rather than a guess about it.
    /// </summary>
    [Fact]
    public void ABareCffProgram_IsSubsettedThroughItsGlyphNames()
    {
        // NamedBoxGlyphs, not BoxGlyphs: the latter names its glyphs g1..g52, which carry no Unicode
        // value, so a bare CFF built from it is correctly unresolvable by name. That is the NEXT
        // test's case, and building this one on it failed against working code.
        byte[] bareCff = MinimalOpenTypeCff.BuildBareCff(
            MinimalOpenTypeCff.NamedBoxGlyphs(ProviderCoverage, padTo: 120));
        Assert.Equal(1, bareCff[0]);   // a bare CFF1, with no sfnt and so no cmap anywhere

        List<byte[]> original = MinimalOpenTypeCff.ReadCharstrings(bareCff);

        byte[] output = Run(NonEmbeddedFontPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => bareCff,
        }, out FontEmbeddingResult result);

        Assert.Equal(1, result.FontsEmbedded);

        byte[] embedded = PdfContentProbe.EmbeddedFontProgram(output, "FontFile3");
        Assert.NotNull(embedded);
        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(embedded!);

        int blanked = after.Count(MinimalOpenTypeCff.IsBlanked);
        Assert.True(blanked >= 48,
            $"only {blanked} of {after.Count} charstrings were blanked; a bare CFF is supposed to " +
            "resolve through its glyph names now, not decline");

        // The glyphs the page draws survive. If this fails while the count above passes, the
        // resolver mapped SOMETHING and it mapped it to the wrong glyph.
        string sorted = new string(new SortedSet<char>(ProviderCoverage).ToArray());
        foreach (char c in new SortedSet<char>(PageText))
        {
            int gid = sorted.IndexOf(c) + 1;
            if (gid <= 0) continue;
            Assert.True(original[gid].SequenceEqual(after[gid]),
                $"the charstring for '{c}' (GID {gid}) was blanked, but the page draws it");
        }
    }

    /// <summary>
    /// A bare CFF whose glyph names carry no Unicode value cannot be resolved, and that must be a
    /// refusal with a stated reason rather than a subset built on a partial map. The fixture's
    /// default names (<c>g1</c>, <c>g2</c>, …) are exactly that case.
    /// </summary>
    [Fact]
    public void ABareCffWhoseGlyphNamesCarryNoUnicode_IsEmbeddedWholeWithAReason()
    {
        byte[] bareCff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(20, padTo: 120));
        List<byte[]> original = MinimalOpenTypeCff.ReadCharstrings(bareCff);

        byte[] output = Run(NonEmbeddedFontPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => bareCff,
        }, out FontEmbeddingResult result);

        string all = string.Join(" | ", result.Warnings.Concat(result.SkippedFonts.Select(s => s.Reason)));
        Assert.Contains("names no glyph", all, StringComparison.Ordinal);

        if (result.FontsEmbedded > 0)
        {
            List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(
                PdfContentProbe.EmbeddedFontProgram(output, "FontFile3")!);
            for (int gid = 1; gid < original.Count; gid++)
                Assert.True(original[gid].SequenceEqual(after[gid]),
                    $"glyph {gid} was blanked on an unresolvable glyph set");
        }
    }

    /// <summary>
    /// A CFF2 program must be embedded whole with a stated reason, never rewritten. Fail-closed:
    /// the cost of being wrong here is a document whose text does not render.
    /// </summary>
    [Fact]
    public void ACff2Program_IsEmbeddedWholeAndTheReasonNamesCff2()
    {
        byte[] otf = MinimalOpenTypeCff.Build(ProviderCoverage, padTo: 120);
        DeclareCff2(otf);
        List<byte[]> original = MinimalOpenTypeCff.ReadCharstrings(otf);

        byte[] output = Run(NonEmbeddedFontPdf(), new FontEmbeddingOptions
        {
            FontProvider = _ => otf,
        }, out FontEmbeddingResult result);

        string all = string.Join(" | ", result.Warnings.Concat(result.SkippedFonts.Select(s => s.Reason)));
        Assert.Contains("CFF2", all, StringComparison.Ordinal);

        if (result.FontsEmbedded > 0)
        {
            List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(
                PdfContentProbe.EmbeddedFontProgram(output, "FontFile3")!);
            for (int gid = 1; gid < original.Count; gid++)
                Assert.True(original[gid].SequenceEqual(after[gid]),
                    $"glyph {gid} of a CFF2 program was rewritten");
        }
    }

    /// <summary>Sets the <c>CFF </c> table's major version to 2, keeping the tag so the CFF route is taken.</summary>
    private static void DeclareCff2(byte[] sfnt)
    {
        int numTables = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16;
            if (Encoding.ASCII.GetString(sfnt, at, 4) != "CFF ") continue;
            int offset = (sfnt[at + 8] << 24) | (sfnt[at + 9] << 16) | (sfnt[at + 10] << 8) | sfnt[at + 11];
            sfnt[offset] = 2;
            return;
        }
        throw new InvalidOperationException("no \"CFF \" table");
    }
}
