// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Signing;

/// <summary>
/// The visible signature appearance has to be able to draw the signer's name.
///
/// <para>It could not, for anything outside <c>/WinAnsiEncoding</c>: the generator wrote
/// <c>/BaseFont /Helvetica /Encoding /WinAnsiEncoding</c> and then encoded the whole content
/// stream as ISO-8859-1, so every Hebrew, Arabic, Greek, Cyrillic or CJK character became a
/// literal <c>?</c> byte. The appearance stream was well-formed, non-empty and drew text — which
/// is why nothing short of reading the glyphs it shows can see the defect.</para>
/// </summary>
public sealed class SignatureAppearanceTextTests
{
    private const string Hebrew = "שלום";     // שלום
    private const string Arabic = "بيت";           // بيت

    private static string Content(PdfStream ap) =>
        Encoding.GetEncoding("iso-8859-1").GetString(ap.RawData);

    /// <summary>The font dictionary the appearance's <c>/Resources /Font /F1</c> resolves to.</summary>
    private static PdfDictionary AppearanceFont(
        PdfStream ap, IReadOnlyList<(int objNum, PdfObject obj)> side)
    {
        var resources = Assert.IsType<PdfDictionary>(ap[PdfName.Resources]);
        var fonts = Assert.IsType<PdfDictionary>(resources[PdfName.Font]);
        var reference = Assert.IsType<PdfReference>(fonts[new PdfName("F1")]);

        foreach (var (objNum, obj) in side)
            if (objNum == reference.ReferenceObjectNumber)
                return Assert.IsType<PdfDictionary>(obj);

        Assert.Fail($"the appearance references font object {reference.ReferenceObjectNumber}, "
                    + "which is not among the side objects it returned");
        return null;
    }

    /// <summary>The glyph ids of the first hex-encoded show operation in the stream.</summary>
    private static List<ushort> DrawnGlyphs(string content)
    {
        var match = Regex.Match(content, @"<([0-9A-F]+)> Tj");
        Assert.True(match.Success, "the appearance shows no hex-encoded glyph run: " + content);

        var drawn = new List<ushort>();
        string hex = match.Groups[1].Value;
        for (int i = 0; i + 4 <= hex.Length; i += 4)
            drawn.Add(ushort.Parse(hex.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return drawn;
    }

    // ---------------------------------------------------------------- the defect

    /// <summary>
    /// A Hebrew signer name is drawn from an embedded composite font, not from Helvetica.
    /// </summary>
    [EmbeddableFontFact(Hebrew)]
    public void HebrewSignerName_IsDrawnFromAnEmbeddedCompositeFont()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Hebrew);
        int next = 10;

        var (ap, side) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = Hebrew, FontProvider = _ => fontBytes },
            ref next);

        var font = AppearanceFont(ap, side);
        Assert.Equal("Type0", font.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Identity-H", font.GetName(PdfName.Encoding)?.Value);
        Assert.Single(Assert.IsType<PdfArray>(font[PdfName.DescendantFonts]));

        // And the name is really shown: a Type0 resource nothing draws with would be just as
        // invisible as the '?' it replaced.
        Assert.Equal(Hebrew.Length, DrawnGlyphs(Content(ap)).Count);
    }

    /// <summary>
    /// Hebrew is held in logical order and drawn right-to-left, so the glyph ids in the content
    /// stream are the reverse of the logical sequence. Drawing them in logical order produces an
    /// appearance that reads backwards and looks entirely plausible to anyone who cannot read it.
    /// </summary>
    [EmbeddableFontFact(Hebrew)]
    public void HebrewSignerName_IsDrawnInVisualOrder()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Hebrew);
        int next = 10;

        var (ap, _) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = Hebrew, FontProvider = _ => fontBytes },
            ref next);

        var program = TrueTypeFontProgram.Parse(fontBytes);
        var logical = Hebrew.Select(c =>
        {
            Assert.True(program.TryGetGlyph(c, out ushort gid), $"the probe font has no U+{(int)c:X4}");
            return gid;
        }).ToList();

        Assert.Equal(logical.AsEnumerable().Reverse(), DrawnGlyphs(Content(ap)));
    }

    /// <summary>
    /// Arabic must be <b>shaped</b> as well as reordered: each letter takes an initial / medial /
    /// final form from its neighbours, and the isolated forms a naive path draws are legible but
    /// wrong. All three letters of <c>بيت</c> join, so a correct implementation shares no glyph
    /// with the unshaped rendering — asserted as a difference rather than against a hard-coded
    /// glyph list, which would only hold for one host's font.
    /// </summary>
    [EmbeddableFontFact(Arabic)]
    public void ArabicSignerName_IsShapedBeforeItIsDrawn()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Arabic);
        int next = 10;

        var (ap, _) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = Arabic, FontProvider = _ => fontBytes },
            ref next);

        var program = TrueTypeFontProgram.Parse(fontBytes);
        var isolated = Arabic.Select(c => program.TryGetGlyph(c, out ushort g) ? g : (ushort)0)
                             .Reverse().ToList();

        var drawn = DrawnGlyphs(Content(ap));
        Assert.Equal(isolated.Count, drawn.Count);   // still 1:1 — no ligatures formed
        Assert.NotEqual(isolated, drawn);            // ...but every form is a joined one
        Assert.DoesNotContain(drawn, g => g == 0);
    }

    /// <summary>
    /// A font covering the Arabic letters but not the forms they are shaped into cannot draw the
    /// name, so it must not be the font this chooses. Asked about the letters, the resolver would
    /// accept it and the appearance would then be refused.
    /// </summary>
    [EmbeddableFontFact(Arabic)]
    public void AFontCoveringTheLettersButNotTheirShapedFormsIsNotTheFontChosen()
    {
        int next = 10;

        var (ap, _) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions
            {
                SignerName = Arabic,
                FontProvider = _ => Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build(Arabic),
                AllowSystemFontSubstitution = true,
            },
            ref next, out string error);

        Assert.True(ap != null, "no appearance was built: " + error);
    }

    // ---------------------------------------------------------------- refusal

    /// <summary>
    /// With no font source, text outside WinAnsi is <b>refused</b> rather than drawn as question
    /// marks — and the refusal says which setting would have supplied a font.
    /// </summary>
    [Fact]
    public void HebrewSignerName_WithNoFontSource_IsRefusedWithAReason()
    {
        int next = 10;

        var (ap, side) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = Hebrew }, ref next, out string error);

        Assert.Null(ap);
        Assert.Empty(side);
        Assert.Contains("FontProvider", error);
    }

    /// <summary>A refusal costs the caller no object numbers, so the update it was building is
    /// still numbered as it was.</summary>
    [Fact]
    public void RefusedAppearance_LeavesTheObjectCounterUntouched()
    {
        int next = 10;

        SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = Hebrew }, ref next, out string error);

        Assert.NotNull(error);
        Assert.Equal(10, next);
    }

    /// <summary>The throwing overload names the same reason rather than producing wrong output.</summary>
    [Fact]
    public void HebrewSignerName_WithNoFontSource_ThrowsFromTheOverloadThatCannotReport()
    {
        int next = 10;
        var opts = new SignatureAppearanceOptions { SignerName = Hebrew };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            int n = next;
            SignatureAppearanceGenerator.Build(opts, ref n);
        });

        Assert.Contains("WinAnsiEncoding", ex.Message);
    }

    /// <summary>
    /// A font that parses but has no glyph for the text is refused too, naming the code point.
    /// The distinction matters: "no font was supplied" and "the font you supplied cannot draw
    /// this" call for different fixes by the caller.
    /// </summary>
    /// <remarks>
    /// U+0378 is <b>permanently unassigned</b> in Unicode, so no font on any host has a glyph for
    /// it. A missing glyph asserted against some ordinary character would instead be a claim about
    /// which fonts the runner happens to have installed.
    /// </remarks>
    [EmbeddableFontFact(Hebrew)]
    public void SuppliedFontMissingAGlyph_IsRefusedNamingTheCodePoint()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Hebrew);
        int next = 10;

        var (ap, _) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions
            {
                SignerName = Hebrew + "͸",
                FontProvider = _ => fontBytes,
            },
            ref next, out string error);

        Assert.Null(ap);
        Assert.Contains("U+0378", error);
        Assert.Equal(10, next);
    }

    // ---------------------------------------------------------------- the unchanged path

    /// <summary>
    /// WinAnsi text is untouched by all of the above: still a Standard-14 simple font, still a
    /// literal string. This is the control — without it the composite path could have replaced
    /// the simple one outright and every assertion above would still pass.
    /// </summary>
    [Fact]
    public void AsciiSignerName_StillUsesTheStandard14SimpleFont()
    {
        int next = 10;

        var (ap, side) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = "Ada Lovelace", SigningDate = "2026-09-01" },
            ref next);

        var font = AppearanceFont(ap, side);
        Assert.Equal("Type1", font.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Helvetica", font.GetName(PdfName.BaseFont)?.Value);
        Assert.Equal("WinAnsiEncoding", font.GetName(PdfName.Encoding)?.Value);

        string content = Content(ap);
        Assert.Contains("(Ada Lovelace) Tj", content);
        Assert.Contains("(Date: 2026-09-01) Tj", content);
        Assert.DoesNotContain("> Tj", content);
    }

    /// <summary>
    /// Latin-1 accents stay on the simple path — <c>é</c> is a WinAnsi character, so pulling an
    /// embedded font in for it would be a regression in file size for no gain.
    /// </summary>
    [Fact]
    public void Latin1SignerName_StaysOnTheSimplePath()
    {
        int next = 10;

        var (ap, side) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = "René Descartes" }, ref next);

        Assert.Equal("Type1", AppearanceFont(ap, side).GetName(PdfName.Subtype)?.Value);
        Assert.Contains("(René Descartes) Tj", Content(ap));
    }

    // ---------------------------------------------------------------- variants

    /// <summary>
    /// <c>BuildAllVariants</c> clones the options for /R and /D. Dropping the font source in that
    /// clone would leave /N drawing the signer's name and the other two refusing it — three
    /// appearances of one signature that disagree about whether the name can be drawn.
    /// </summary>
    [EmbeddableFontFact(Hebrew)]
    public void AllVariants_CarryTheFontSourceIntoTheRolloverAndDownAppearances()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Hebrew);
        int next = 10;

        var (normal, rollover, down, _) = SignatureAppearanceGenerator.BuildAllVariants(
            new SignatureAppearanceOptions { SignerName = Hebrew, FontProvider = _ => fontBytes },
            ref next);

        foreach (var ap in new[] { normal, rollover, down })
            Assert.Equal(Hebrew.Length, DrawnGlyphs(Content(ap)).Count);
    }

    // ---------------------------------------------------------------- the logo's inner form

    /// <summary>
    /// <b>A logo is drawn through an inner form that carries its own <c>/Resources</c></b>, and the
    /// appearance names that form, never the image: Acrobat Reader does not validate a signature
    /// whose appearance names an image under <c>/XObject</c> ("Expected a dict object").
    /// </summary>
    /// <remarks>
    /// The inner form places the logo where the appearance placed it — the logo column, inset by
    /// the margin — inside a box that is the appearance's own, so the stamp renders as before.
    /// </remarks>
    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    public void ALogo_IsDrawnThroughAnInnerFormThatCarriesItsOwnResources(string format)
    {
        int next = 10;
        var opts = new SignatureAppearanceOptions { SignerName = "Ada Lovelace", Width = 200, Height = 60 };
        if (format == "jpeg") opts.LogoJpegBytes = Jpeg(40, 20);
        else opts.LogoPngBytes = Png(40, 20);

        var (ap, side) = SignatureAppearanceGenerator.Build(opts, ref next);
        Func<PdfObject, PdfObject> resolve = o => Resolve(o, side);

        Assert.Equal(1, SignatureAppearanceXObjectRule.AssertAndCountImages(ap, resolve));

        string outer = Content(ap);
        Assert.Contains("q /SigForm Do Q", outer, StringComparison.Ordinal);
        Assert.DoesNotContain("/Im1", outer, StringComparison.Ordinal);

        var xobjects = (PdfDictionary)resolve(((PdfDictionary)ap[PdfName.Resources])[PdfName.XObject]);
        var inner = (PdfStream)resolve(xobjects[new PdfName("SigForm")]);
        Assert.Equal(new double[] { 0, 0, 200, 60 }, Numbers((PdfArray)inner[PdfName.BBox]));
        Assert.Null(inner[PdfName.Matrix]);

        // The logo column: 30% of the width less a 4 pt margin each side, the height less the same.
        var place = Regex.Match(Content(inner), @"([\d.]+) 0 0 ([\d.]+) ([\d.]+) ([\d.]+) cm\s+/Im1 Do");
        Assert.True(place.Success, "the inner form does not place and draw /Im1: " + Content(inner));
        Assert.Equal(new double[] { 52, 52, 4, 4 },
            Enumerable.Range(1, 4).Select(i => double.Parse(place.Groups[i].Value, CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The control beside the test above: a text-only appearance names its font and no XObject at
    /// all — the shape pyHanko's text appearance has, which Acrobat validates.
    /// </summary>
    [Fact]
    public void ATextOnlyAppearance_NamesAFontAndNoXObject()
    {
        int next = 10;
        var (ap, side) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = "Ada Lovelace", Width = 200, Height = 60 }, ref next);

        var resources = (PdfDictionary)ap[PdfName.Resources];
        Assert.NotNull(resources[PdfName.Font]);
        Assert.Null(resources[PdfName.XObject]);
        Assert.Equal(0, SignatureAppearanceXObjectRule.AssertAndCountImages(ap, o => Resolve(o, side)));
        Assert.DoesNotContain(" Do", Content(ap), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>BuildAllVariants</c> builds /R and /D through the same <c>Build</c>, so the rollover and
    /// down appearances draw their logo through an inner form of their own too.
    /// </summary>
    [Fact]
    public void AllVariants_DrawTheLogoThroughAnInnerForm()
    {
        int next = 10;
        var (normal, rollover, down, side) = SignatureAppearanceGenerator.BuildAllVariants(
            new SignatureAppearanceOptions
            {
                SignerName = "Ada Lovelace", LogoJpegBytes = Jpeg(40, 20), Width = 200, Height = 60,
            },
            ref next);

        // Each variant's own objects are among the side objects except its form, which is returned.
        var all = new List<(int objNum, PdfObject obj)>(side)
        {
            (normal.ObjectNumber, normal), (rollover.ObjectNumber, rollover), (down.ObjectNumber, down),
        };
        foreach (var ap in new[] { normal, rollover, down })
            Assert.Equal(1, SignatureAppearanceXObjectRule.AssertAndCountImages(ap, o => Resolve(o, all)));
    }

    private static PdfObject Resolve(PdfObject o, IReadOnlyList<(int objNum, PdfObject obj)> objects)
    {
        if (o is not PdfReference r) return o;
        foreach (var (objNum, obj) in objects)
            if (objNum == r.ReferenceObjectNumber) return obj;
        return null;
    }

    private static double[] Numbers(PdfArray array)
        => Enumerable.Range(0, array.Count).Select(i => ((PdfNumber)array[i]).DoubleValue).ToArray();

    /// <summary>A baseline JFIF frame header of three components — what the generator reads, not pixels.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };                          // SOI
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });        // SOF0, length 17, 8-bit
        bytes.Add((byte)(height >> 8)); bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8));  bytes.Add((byte)(width & 0xFF));
        bytes.Add(0x03);
        bytes.AddRange(new byte[] { 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });
        bytes.AddRange(new byte[] { 0xFF, 0xD9 });                          // EOI
        return bytes.ToArray();
    }

    /// <summary>An opaque red RGB PNG.</summary>
    private static byte[] Png(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3) rgb[i] = 220;
        return Tetitulz.Pdf.Base.Rendering.PdfRawImageEncoder.EncodePng(rgb, width, height, 3);
    }
}
