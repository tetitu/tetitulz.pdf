// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.CMap;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// CJK composite fonts, via the Adobe predefined CMaps that
/// <c>Tetitulz.Pdf.Plugins.CMap</c> registers.
///
/// <para><b>The bytes are never decoded here, and that is the design.</b> A PostScript composite
/// font turns bytes into CIDs with a CMap; a PDF Type0 font naming the <i>same</i> CMap makes the
/// reader do the identical decoding. So <c>show</c> operands pass through into <c>Tj</c> unchanged
/// and this converter needs no Shift-JIS, Big5 or EUC knowledge at all.</para>
///
/// <para>The registry is in <b>Core</b>, so the PostScript plugin reads it without referencing the
/// CMap plugin — which would be a plugin-to-plugin dependency the architecture forbids. These tests
/// populate it the way a host would.</para>
/// </summary>
public sealed class CjkCompositeFontTests
{
    static CjkCompositeFontTests()
    {
        // Idempotent: the plugin skips a CMap that is already registered.
        new AdobeCMapPlugin().Initialize();
    }

    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>
    /// The corpus has to be present for anything below to mean anything — if the CMap plugin
    /// stopped embedding its resources these tests would otherwise pass over nothing.
    /// </summary>
    [Fact]
    public void PredefinedCMaps_AreRegistered()
    {
        Assert.True(PredefinedCMapRegistry.IsRegistered("UniJIS-UCS2-H"));
        Assert.True(PredefinedCMapRegistry.IsRegistered("90ms-RKSJ-H"));
        Assert.True(PredefinedCMapRegistry.IsRegistered("UniGB-UCS2-H"));
        Assert.True(PredefinedCMapRegistry.IsRegistered("UniKS-UCS2-H"));
    }

    // ==================================================================
    //  Composite font names
    // ==================================================================

    /// <summary>
    /// A CJK font name carries its CMap, and both halves contain hyphens — so only the registry can
    /// say where the split is. <c>KozMinPro-Regular-90ms-RKSJ-H</c> is the face
    /// <c>KozMinPro-Regular</c> under <c>90ms-RKSJ-H</c>.
    /// </summary>
    [Fact]
    public void FindFont_SplitsACompositeNameIntoFaceAndCMap()
    {
        var result = Convert(
            "/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont " +
            @"newpath 0 0 moveto (\202\240) show");

        var font = FirstFont(result.Resources);
        Assert.Equal("Type0", ((PdfName)font[PdfName.Subtype]).Value);
        Assert.Equal("90ms-RKSJ-H", ((PdfName)font[PdfName.Encoding]).Value);

        var descendant = (PdfDictionary)((PdfArray)font[PdfName.DescendantFonts])[0];
        Assert.Equal("KozMinPro-Regular", ((PdfName)descendant[PdfName.BaseFont]).Value);
    }

    /// <summary>An ordinary Latin name must not be mistaken for a composite one.</summary>
    [Fact]
    public void FindFont_DoesNotTreatALatinNameAsComposite()
    {
        var result = Convert("/Helvetica-Bold findfont 12 scalefont setfont " +
                             "newpath 0 0 moveto (A) show");

        var font = FirstFont(result.Resources);
        Assert.Equal("Type1", ((PdfName)font[PdfName.Subtype]).Value);
    }

    // ==================================================================
    //  composefont
    // ==================================================================

    [Fact]
    public void ComposeFont_BuildsATypeZeroFontNamingTheCMap()
    {
        var result = Convert(
            "/UniJIS-UCS2-H /KozMinPro-Regular /Composed composefont " +
            @"12 scalefont setfont newpath 0 0 moveto (\060\102) show");

        var font = FirstFont(result.Resources);
        Assert.Equal("Type0", ((PdfName)font[PdfName.Subtype]).Value);
        Assert.Equal("UniJIS-UCS2-H", ((PdfName)font[PdfName.Encoding]).Value);
    }

    /// <summary>
    /// With no CMap plugin registered the registry is empty and nothing can be recognised. The
    /// converter says so instead of guessing — the message names the registration a host needs.
    /// </summary>
    [Fact]
    public void ComposeFont_WithNoRegisteredCMap_ReportsWhatTheHostMustRegister()
    {
        var result = Convert("/NoSuchCMapName /SomeFace /Composed composefont " +
                             "12 scalefont setfont");

        Assert.Contains("AdobeCMapPlugin", result.LastError);
    }

    // ==================================================================
    //  The descendant font
    // ==================================================================

    [Theory]
    [InlineData("UniJIS-UCS2-H", "Japan1")]
    [InlineData("90ms-RKSJ-H", "Japan1")]
    [InlineData("UniGB-UCS2-H", "GB1")]
    [InlineData("UniCNS-UCS2-H", "CNS1")]
    [InlineData("UniKS-UCS2-H", "Korea1")]
    [InlineData("Identity-H", "Identity")]
    public void DescendantFont_DeclaresTheRightCharacterCollection(string cmap, string ordering)
    {
        var result = Convert($"/UniFace-{cmap} findfont 12 scalefont setfont " +
                             @"newpath 0 0 moveto (\060\102) show");

        var font = FirstFont(result.Resources);
        var descendant = (PdfDictionary)((PdfArray)font[PdfName.DescendantFonts])[0];
        var info = (PdfDictionary)descendant[PdfName.CIDSystemInfo];

        Assert.Equal("Adobe", ((PdfString)info[PdfName.Registry]).GetText());
        Assert.Equal(ordering, ((PdfString)info[PdfName.Ordering]).GetText());
    }

    /// <summary>
    /// <b>The declared supplement, which nothing pinned.</b> The comment above
    /// <c>PsCidFonts.Collections</c> described a rule the table has never followed — "deliberately
    /// conservative … a reader tolerates a font declaring a lower supplement than the CMap needs
    /// far better than the reverse" — against a table declaring the <b>maximum</b> Adobe publishes
    /// for every collection. A supplement is additive, so the maximum is the safe declaration and
    /// the comment had it exactly backwards: a maintainer adding a collection would have picked the
    /// lowest value and produced a font declaring less coverage than its CMap requires, which is the
    /// failure the sentence warned about, reached by obeying it.
    ///
    /// <para>The theory above asserts only <c>/Ordering</c>, which is why the table and its comment
    /// could disagree indefinitely. Japan1 was the single entry that matched the old comment rather
    /// than the table, at 6; it is 7 now, and this is what would catch it drifting back.</para>
    /// </summary>
    [Theory]
    [InlineData("UniJIS-UCS2-H", 7)]
    [InlineData("90ms-RKSJ-H", 7)]
    [InlineData("UniGB-UCS2-H", 5)]
    [InlineData("UniCNS-UCS2-H", 7)]
    [InlineData("UniKS-UCS2-H", 2)]
    [InlineData("Identity-H", 0)]
    public void DescendantFont_DeclaresTheHighestPublishedSupplement(string cmap, int supplement)
    {
        var result = Convert($"/UniFace-{cmap} findfont 12 scalefont setfont " +
                             @"newpath 0 0 moveto (\060\102) show");

        var font = FirstFont(result.Resources);
        var descendant = (PdfDictionary)((PdfArray)font[PdfName.DescendantFonts])[0];
        var info = (PdfDictionary)descendant[PdfName.CIDSystemInfo];

        Assert.Equal(supplement, ((PdfNumber)info[PdfName.Supplement]).LongValue);
    }

    /// <summary>
    /// The descendant is a CIDFont with a default width. Per-CID widths need the font program, so
    /// <c>/DW 1000</c> — the PDF default and the correct em for full-width CJK — is what is
    /// declared, and the caller is told.
    /// </summary>
    [Fact]
    public void DescendantFont_IsACidFontWithADefaultWidth()
    {
        var result = Convert("/KozMinPro-Regular-UniJIS-UCS2-H findfont 12 scalefont setfont " +
                             @"newpath 0 0 moveto (\060\102) show");

        var font = FirstFont(result.Resources);
        var descendant = (PdfDictionary)((PdfArray)font[PdfName.DescendantFonts])[0];

        Assert.Equal("CIDFontType0", ((PdfName)descendant[PdfName.Subtype]).Value);
        Assert.Equal(1000, ((PdfNumber)descendant[PdfName.DW]).LongValue);
        Assert.Contains("/DW 1000", result.LastError);
    }

    /// <summary>
    /// Table 117 lists <c>/FontDescriptor</c> as <b>Required</b> for every CIDFont, embedded or not,
    /// and the descendant is emitted with one.
    ///
    /// <para>That is precisely the case where it matters: no CJK program is embedded on this path,
    /// so the reader must substitute a face — and <c>/Flags</c>, <c>/FontBBox</c>,
    /// <c>/ItalicAngle</c> and <c>/StemV</c> are the only things it has to choose and metric that
    /// substitute from. A strict reader or preflight rejects the font outright without them, so the
    /// whole CJK run draws as nothing while the plugin reports success.</para>
    /// </summary>
    [Fact]
    public void DescendantFont_CarriesTheRequiredFontDescriptor()
    {
        var result = Convert("/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont " +
                             @"newpath 0 0 moveto (\202\240) show");

        var font = FirstFont(result.Resources);
        var descendant = (PdfDictionary)((PdfArray)font[PdfName.DescendantFonts])[0];

        var descriptor = descendant[PdfName.FontDescriptor] as PdfDictionary;
        Assert.NotNull(descriptor);

        Assert.Equal("FontDescriptor", ((PdfName)descriptor[PdfName.Type]).Value);
        Assert.Equal("KozMinPro-Regular", ((PdfName)descriptor[PdfName.FontName]).Value);

        // The four entries a substituting reader actually uses. A descriptor present but empty
        // would satisfy "not null" and help nobody.
        Assert.Equal(4, ((PdfNumber)descriptor[PdfName.Flags]).LongValue);
        Assert.Equal(4, ((PdfArray)descriptor[PdfName.FontBBox]).Count);
        Assert.NotNull(descriptor[PdfName.ItalicAngle]);
        Assert.True(((PdfNumber)descriptor[PdfName.StemV]).LongValue > 0);

        // /MissingWidth must agree with the descendant's /DW, or a reader gets two different answers
        // for the same unlisted CID.
        Assert.Equal(((PdfNumber)descendant[PdfName.DW]).LongValue,
                     ((PdfNumber)descriptor[PdfName.MissingWidth]).LongValue);

        // No font program is embedded on this path, so none of the /FontFile keys may appear —
        // a descriptor claiming an embedded program that is not there is worse than no descriptor.
        Assert.Null(descriptor[PdfName.FontFile]);
        Assert.Null(descriptor[PdfName.FontFile2]);
        Assert.Null(descriptor[PdfName.FontFile3]);
    }

    // ==================================================================
    //  Bytes pass through
    // ==================================================================

    /// <summary>
    /// The shown bytes reach <c>Tj</c> unchanged. This is the property the whole approach rests on:
    /// the reader's CMap does the decoding, so re-encoding here would corrupt the text.
    /// </summary>
    [Fact]
    public void Show_PassesTheBytesThroughUnchanged()
    {
        // 0x82 0xA0 is Shift-JIS hiragana 'a'.
        var result = Convert("/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont " +
                             @"newpath 0 0 moveto (\202\240) show");

        string content = Encoding.Latin1.GetString(result.Content);

        // Emitted as octal escapes, which is how the emitter writes any non-printable byte.
        Assert.Contains(@"\202\240", content);
        Assert.Contains("Tj", ContentStreamAssert.Operators(result.Content));
    }

    /// <summary>
    /// A two-byte CMap advances one em per <i>code</i>, not per byte. Getting this wrong would put
    /// the pen at twice the right distance after every run.
    /// </summary>
    [Fact]
    public void Show_AdvancesOneEmPerCodeNotPerByte()
    {
        // Two 2-byte codes at size 10 → 20 units, not 40.
        var result = Convert("/KozMinPro-Regular-UniJIS-UCS2-H findfont 10 scalefont setfont " +
                             @"newpath 0 0 moveto (\060\102\060\104) show currentpoint " +
                             "newpath moveto 0 0 lineto stroke");

        // Assert the ADVANCE, which is what the name promises. This read `Assert.NotNull(move)`,
        // and FirstOperandsOf can never return null — it asserts non-null itself and then projects
        // — so the test passed with any advance at all, including the per-byte 40 it exists to
        // rule out.
        double[] move = ContentStreamAssert.FirstOperandsOf(result.Content, "m");
        Assert.Equal(new double[] { 20, 0 }, move);
    }

    /// <summary>
    /// A mixed-width CMap counts a byte below 0x80 as its own code and a byte above it as the start
    /// of a two-byte pair — the rule Shift-JIS, EUC, Big5 and GBK all follow.
    /// </summary>
    [Fact]
    public void MixedWidthCMap_CountsSingleAndDoubleByteCodes()
    {
        // "A" (1 byte) + one 2-byte code = 2 codes.
        Assert.Equal(2, PsCidFontsProbe.CountCodes("90ms-RKSJ-H",
            new byte[] { 0x41, 0x82, 0xA0 }));

        // A fixed two-byte CMap sees the same three bytes as two codes as well, but by a different
        // rule — halving rather than inspecting.
        Assert.Equal(2, PsCidFontsProbe.CountCodes("UniJIS-UCS2-H",
            new byte[] { 0x30, 0x42, 0x30 }));
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
