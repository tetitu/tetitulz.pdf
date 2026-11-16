// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The Level 2 image forms, closed on 2026-08-19.
///
/// <para>Two defects, of the two different kinds this repo distinguishes. The <b>dictionary form</b>
/// (<c>&lt;&lt; /ImageType 1 … &gt;&gt; image</c>) was consumed and reported, drawing nothing — an
/// honest refusal. The <b>colour space</b> was the other kind: <c>image</c> never consulted
/// <c>setcolorspace</c>, so an <c>/Indexed</c> image drew with correct geometry and wrong colours,
/// and the only diagnostic was <c>setcolorspace</c> in the unimplemented list, which reads as "a
/// colour operator was skipped" rather than "your image came out greyscale".</para>
///
/// <para>Assertions read the emitted image XObject's own dictionary — <c>/Width</c>,
/// <c>/BitsPerComponent</c>, <c>/ColorSpace</c>, <c>/Decode</c>, <c>/ImageMask</c> — because "a
/// <c>Do</c> was emitted" is true of every one of these cases including the broken ones.</para>
/// </summary>
public sealed class ImageFormAndColorSpaceTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>The single image XObject the fragment registered, or null when it drew none.</summary>
    private static PdfStream OnlyImage(PostScriptConversionResult r)
    {
        if (r.Resources?[PdfName.XObject] is not PdfDictionary xobjects) return null;
        foreach (var kv in xobjects)
            if (kv.Value is PdfStream stream) return stream;
        return null;
    }

    private static string ColorSpaceName(PdfStream image) =>
        (image[PdfName.ColorSpace] as PdfName)?.Value;

    private static double[] DecodeOf(PdfStream image)
    {
        if (image[PdfName.Decode] is not PdfArray array) return null;
        var result = new double[array.Count];
        for (int k = 0; k < array.Count; k++) result[k] = ((PdfNumber)array[k]).DoubleValue;
        return result;
    }

    // ==================================================================
    //  The dictionary form
    // ==================================================================

    /// <summary>
    /// <c>&lt;&lt; /ImageType 1 … &gt;&gt; image</c> draws. It used to emit nothing at all.
    /// </summary>
    [Fact]
    public void ImageDictionary_Draws()
    {
        var r = Convert("<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 " +
                        "/ImageMatrix [2 0 0 -2 0 2] /DataSource <00FF00FF> >> image");

        Assert.Contains("Do", ContentStreamAssert.Operators(r.Content));

        var image = OnlyImage(r);
        Assert.NotNull(image);
        Assert.Equal(2, ((PdfNumber)image[PdfName.Width]).LongValue);
        Assert.Equal(2, ((PdfNumber)image[PdfName.Height]).LongValue);
        Assert.Equal(8, ((PdfNumber)image[PdfName.BitsPerComponent]).LongValue);
        Assert.DoesNotContain("image (dictionary form)", r.UnimplementedOperators);
    }

    /// <summary>
    /// Its <c>/Decode</c> reaches the PDF image. PLRM's Decode and PDF's are the same concept with
    /// the same formula, which is why the operand form's polarity and this array converged on one
    /// representation rather than becoming two rules.
    /// </summary>
    [Fact]
    public void ImageDictionary_CarriesItsDecodeArray()
    {
        var r = Convert("<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /Decode [1 0] " +
                        "/ImageMatrix [2 0 0 -2 0 2] /DataSource <00FF00FF> >> image");

        Assert.Equal(new double[] { 1, 0 }, DecodeOf(OnlyImage(r)));
    }

    /// <summary>An image with no <c>/Decode</c> writes none, keeping the PDF default.</summary>
    [Fact]
    public void ImageDictionary_WithoutDecode_WritesNone()
    {
        var r = Convert("<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 " +
                        "/ImageMatrix [2 0 0 -2 0 2] /DataSource <00FF00FF> >> image");

        Assert.Null(DecodeOf(OnlyImage(r)));
    }

    /// <summary><c>/ImageMask true</c> makes a dictionary image a stencil.</summary>
    [Fact]
    public void ImageDictionary_WithImageMask_EmitsAStencil()
    {
        var r = Convert("<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 1 /ImageMask true " +
                        "/ImageMatrix [2 0 0 -2 0 2] /DataSource <C0> >> imagemask");

        var image = OnlyImage(r);
        Assert.NotNull(image);
        Assert.Equal(PdfBoolean.True, image[PdfName.ImageMask]);
        Assert.Null(image[PdfName.ColorSpace]);   // a stencil has no colour space
    }

    /// <summary>
    /// An <c>/ImageType</c> other than 1, 3 and 4 — and under <c>imagemask</c>, other than 1 (PLRM
    /// §8.2) — is reported, not approximated. Types 3 and 4 under <c>image</c> are drawn
    /// (<c>ImageMaskTypeTests</c>).
    /// </summary>
    [Theory]
    [InlineData("image", 2)]
    [InlineData("image", 5)]
    [InlineData("imagemask", 3)]
    [InlineData("imagemask", 4)]
    public void ImageDictionary_OfATypeNotDrawn_IsReported(string op, int imageType)
    {
        var r = Convert($"<< /ImageType {imageType} /Width 1 /Height 1 >> {op}");

        Assert.Contains($"{op} (/ImageType {imageType})", r.UnimplementedOperators);
        ContentStreamAssert.DoesNotContainOperator(r.Content, "Do");
    }

    /// <summary>
    /// A malformed <c>/ImageMatrix</c> raises <c>typecheck</c> rather than an unchecked CLR cast.
    /// The cast would escape to the conversion's catch-all and discard every mark already emitted,
    /// costing the whole fragment instead of one image — so the following triangle must survive.
    /// </summary>
    [Fact]
    public void ImageDictionary_WithANonArrayImageMatrix_CostsOnlyTheImage()
    {
        var r = Convert("<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 " +
                        "/ImageMatrix /Foo /DataSource <00FF00FF> >> image " +
                        "newpath 0 0 moveto 9 0 lineto 9 9 lineto fill");

        ContentStreamAssert.DoesNotContainOperator(r.Content, "Do");
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }

    // ==================================================================
    //  The current colour space
    // ==================================================================

    /// <summary><c>/DeviceRGB setcolorspace</c> reaches the image's <c>/ColorSpace</c>.</summary>
    [Fact]
    public void SetColorSpace_DeviceRgb_ReachesTheImage()
    {
        var r = Convert("/DeviceRGB setcolorspace << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource " +
                        "<FF000000FF000000FFFFFFFF> >> image");

        Assert.Equal("DeviceRGB", ColorSpaceName(OnlyImage(r)));
    }

    /// <summary>
    /// An <c>/Indexed</c> image carries its palette. This is the case a component count cannot
    /// express: <c>[/Indexed /DeviceRGB 1 &lt;…&gt;]</c> and <c>/DeviceGray</c> both have one
    /// sample per pixel, so anything that stored only an arity would draw this as greyscale.
    /// </summary>
    [Fact]
    public void SetColorSpace_Indexed_CarriesThePaletteIntoTheImage()
    {
        var r = Convert("[/Indexed /DeviceRGB 1 <FF000000FF00>] setcolorspace " +
                        "<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 1 /ImageMatrix [2 0 0 -2 0 2] /DataSource <C0> >> image");

        var space = OnlyImage(r)?[PdfName.ColorSpace] as PdfArray;
        Assert.NotNull(space);
        Assert.Equal(4, space.Count);
        Assert.Equal("Indexed", ((PdfName)space[0]).Value);
        Assert.Equal("DeviceRGB", ((PdfName)space[1]).Value);
        Assert.Equal(1, ((PdfNumber)space[2]).LongValue);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00 }, ((PdfString)space[3]).RawBytes);
    }

    /// <summary>
    /// A space that resolved but cannot be carried is <b>refused</b>, not approximated. Emitting
    /// DeviceGray for a Separation image paints tint values as grey levels AND sizes the sample
    /// buffer to a quarter of the payload, so it would then report "the remainder was padded" —
    /// the wrong picture behind a diagnostic naming the wrong cause.
    /// </summary>
    [Fact]
    public void SetColorSpace_Separation_IsRefusedRatherThanDrawnAsGrey()
    {
        var r = Convert("[/Separation /Spot /DeviceCMYK {}] setcolorspace " +
                        "<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource <00FF00FF> >> image");

        ContentStreamAssert.DoesNotContainOperator(r.Content, "Do");
        Assert.Contains("Separation", r.LastError);
    }

    /// <summary>
    /// A one-ink separation image whose tint transform gives its alternate colour is carried as an
    /// <c>/Indexed</c> image over the alternate: here tint t is grey <c>1 − t</c>, so palette entry k is
    /// <c>255 − k</c>; the samples travel as they are, with no <c>/Decode</c>, and the approximation is said.
    /// </summary>
    [Fact]
    public void ASeparationImage_IsCarriedAsAnIndexedImageOverItsAlternate()
    {
        var r = Convert("[/Separation /Spot /DeviceGray {1 exch sub}] setcolorspace << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource <00FF00FF> >> image");
        PdfStream image = OnlyImage(r);
        Assert.NotNull(image);
        var space = (PdfArray)image[PdfName.ColorSpace];
        Assert.Equal("Indexed", ((PdfName)space[0]).Value);
        Assert.Equal("DeviceGray", ((PdfName)space[1]).Value);
        Assert.Equal(255, ((PdfNumber)space[2]).LongValue);
        byte[] palette = ((PdfString)space[3]).RawBytes;
        Assert.Equal(256, palette.Length);
        Assert.Equal(new byte[] { 255, 254, 128, 1, 0 }, new[] { palette[0], palette[1], palette[127], palette[254], palette[255] });
        Assert.Null(DecodeOf(image));
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00, 0xFF }, image.RawData);
        Assert.Contains("alternate-space colour", r.LastError, System.StringComparison.Ordinal);
    }

    /// <summary>The image's <c>/Decode</c> is folded into the palette: <c>[1 0]</c> reverses the tints, so entry k is k.</summary>
    [Fact]
    public void ASeparationImagesDecode_IsFoldedIntoThePalette()
    {
        var r = Convert("[/Separation /Spot /DeviceGray {1 exch sub}] setcolorspace " +
                        "<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /Decode [1 0] /ImageMatrix [2 0 0 -2 0 2] /DataSource <00FF00FF> >> image");
        PdfStream image = OnlyImage(r);
        byte[] palette = ((PdfString)((PdfArray)image[PdfName.ColorSpace])[3]).RawBytes;
        Assert.Equal(new byte[] { 0, 1, 127, 255 }, new[] { palette[0], palette[1], palette[127], palette[255] });
        Assert.Null(DecodeOf(image));
    }

    /// <summary>A one-bit separation image has a two-entry palette: no ink and full ink — here black, CMYK <c>0 0 0 t</c>.</summary>
    [Fact]
    public void AOneBitSeparationImage_HasATwoEntryPalette()
    {
        var r = Convert("[/Separation /Black /DeviceCMYK {0 0 0 4 -1 roll}] setcolorspace << /ImageType 1 /Width 8 /Height 1 /BitsPerComponent 1 /ImageMatrix [8 0 0 -1 0 1] /DataSource <0F> >> image");
        var space = (PdfArray)OnlyImage(r)[PdfName.ColorSpace];
        Assert.Equal(1, ((PdfNumber)space[2]).LongValue);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 255 }, ((PdfString)space[3]).RawBytes);
    }

    /// <summary>
    /// A 16-bit separation image — more values than an indexed palette's 256 — travels as a
    /// separation: its samples as they are, its transform sampled at 256 tints into a function.
    /// </summary>
    [Fact]
    public void ASixteenBitSeparationImage_IsCarriedAsASeparation()
    {
        var r = Convert("[/Separation /Spot /DeviceGray {1 exch sub}] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 16 /ImageMatrix [1 0 0 -1 0 1] /DataSource <8000> >> image");
        PdfStream image = OnlyImage(r);
        var space = (PdfArray)image[PdfName.ColorSpace];
        Assert.Equal("Separation", ((PdfName)space[0]).Value);
        Assert.Equal("Spot", ((PdfName)space[1]).Value);
        Assert.Equal("DeviceGray", ((PdfName)space[2]).Value);
        var function = (PdfStream)space[3];
        Assert.Equal(256, function.RawData.Length);
        Assert.Equal(new byte[] { 255, 128, 0 }, new[] { function.RawData[0], function.RawData[127], function.RawData[255] });
        Assert.Equal(16, ((PdfNumber)image[PdfName.BitsPerComponent]).IntValue);
        Assert.Equal(new byte[] { 0x80, 0x00 }, image.RawData);
        Assert.Equal("Indexed", ((PdfName)((PdfArray)OnlyImage(Convert(
            "[/Separation /Spot /DeviceGray {1 exch sub}] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <80> >> image"))[PdfName.ColorSpace])[0]).Value);
    }

    /// <summary>
    /// A DeviceN image travels as one: its colorants named, its transform — two inks into grey, the
    /// second ink's tint — sampled on a 64 × 64 grid (the first ink fastest), the samples as they are,
    /// and the sampling said.
    /// </summary>
    [Fact]
    public void ADeviceNImage_IsCarriedAsDeviceN()
    {
        PostScriptDiagnostics.Clear();
        var r = Convert("[/DeviceN [/A /B] /DeviceGray {exch pop}] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <00FF> >> image");
        PdfStream image = OnlyImage(r);
        var space = (PdfArray)image[PdfName.ColorSpace];
        Assert.Equal("DeviceN", ((PdfName)space[0]).Value);
        Assert.Equal(new[] { "A", "B" }, ((PdfArray)space[1]).Cast<PdfName>().Select(n => n.Value).ToArray());
        Assert.Equal("DeviceGray", ((PdfName)space[2]).Value);
        var function = (PdfStream)space[3];
        Assert.Equal(new[] { 64.0, 64 }, ((PdfArray)function[PdfName.Size]).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray());
        Assert.Equal(64 * 64, function.RawData.Length);
        Assert.Equal(0, function.RawData[63]);          // A full, B none: grey is B's tint
        Assert.Equal(255, function.RawData[63 * 64]);   // A none, B full
        Assert.Equal(new byte[] { 0x00, 0xFF }, image.RawData);
        Assert.Contains("emitted as /DeviceN: its tint transform is sampled at 64 tints", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A DeviceN space whose names array holds something that is not a name or a string names no plate, and is refused.</summary>
    [Fact]
    public void ADeviceNImageWithANamelessColorant_IsRefused()
    {
        var r = Convert("[/DeviceN [/A 5] /DeviceGray {exch pop}] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <00FF> >> image");
        Assert.Null(OnlyImage(r));
        Assert.Contains("does not name its colorants", r.LastError, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A DeviceN image whose transform gives no answer is refused, and says so, its inline block
    /// consumed so the program resumes after it; one that answers is the control above.
    /// </summary>
    [Fact]
    public void ADeviceNImageWhoseTransformGivesNoAnswer_IsRefused_ItsBlockConsumed()
    {
        var r = Convert("[/DeviceN [/A /B] /DeviceGray {pop pop}] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource" +
                        " currentfile /ASCIIHexDecode filter >> image\n00FF>\n0.25 setgray newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill");
        Assert.Null(OnlyImage(r));
        Assert.Contains("gave no answer", r.LastError, System.StringComparison.Ordinal);
        Assert.Contains("0.25 g", Encoding.Latin1.GetString(r.Content), System.StringComparison.Ordinal);
    }

    /// <summary>A DeviceN image keeps its <c>/Decode</c>: its samples travel as they are, so the decode does too.</summary>
    [Fact]
    public void ADeviceNImage_KeepsItsDecode()
    {
        var r = Convert("[/DeviceN [/A /B] /DeviceGray {exch pop}] setcolorspace " +
                        "<< /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /Decode [1 0 0 1] /ImageMatrix [1 0 0 -1 0 1] /DataSource <00FF> >> image");
        Assert.Equal(new[] { 1.0, 0, 0, 1 }, DecodeOf(OnlyImage(r)));
    }

    /// <summary>A DeviceN space over an indexed alternate is refused — PDF's alternate is a device space; over a device space it is the control.</summary>
    [Fact]
    public void ADeviceNImageOverAnIndexedAlternate_IsRefused()
    {
        var r = Convert("[/DeviceN [/A /B] [/Indexed /DeviceRGB 1 <FF000000FF00>] {pop pop 0}] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <00FF> >> image");
        Assert.Null(OnlyImage(r));
        Assert.Contains("not a device space", r.LastError, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The regression this whole design turns on.</b> <c>setgray</c> / <c>setrgbcolor</c> /
    /// <c>setcmykcolor</c> set the colour space, and the dictionary form of <c>image</c> paints in it —
    /// but the five-operand form does not: wiring it to that space would make this fragment demand
    /// three samples per pixel — 48 bytes where the file supplied 16 — pad the difference with zeros
    /// and emit DeviceRGB, silently breaking every Level 1 greyscale image that sets a colour first.
    /// </summary>
    [Fact]
    public void SettingAColour_DoesNotChangeTheImageColourSpace()
    {
        var r = Convert("1 0 0 setrgbcolor 4 4 8 [4 0 0 -4 0 4] " +
                        "<00112233445566778899AABBCCDDEEFF> image");

        Assert.Equal("DeviceGray", ColorSpaceName(OnlyImage(r)));
        Assert.DoesNotContain("padded", r.LastError ?? "");
    }

    /// <summary>
    /// Nor does <c>setcolorspace</c>: the five-operand form "always renders a monochrome image according
    /// to the DeviceGray color space, regardless of the current color space" (PLRM §8.2, <c>image</c>) — an indexed or
    /// DeviceN space set before it leaves its one-component samples grey, the dictionary form with the
    /// same space the control.
    /// </summary>
    [Theory]
    [InlineData("[/Indexed /DeviceRGB 1 <FF000000FF00>]")]
    [InlineData("[/DeviceN [/A /B] /DeviceGray {exch pop}]")]
    [InlineData("/DeviceRGB")]
    public void SetColorSpace_DoesNotReachTheOperandForm(string space)
    {
        var operand = Convert(space + " setcolorspace 2 1 8 [2 0 0 -1 0 1] <00FF> image");
        Assert.Equal("DeviceGray", ColorSpaceName(OnlyImage(operand)));
        Assert.Equal(new byte[] { 0x00, 0xFF }, OnlyImage(operand).RawData);

        var dictionary = Convert(space + " setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 " +
                                 "/ImageMatrix [1 0 0 -1 0 1] /DataSource <00FF00> >> image");
        Assert.NotEqual("DeviceGray", ColorSpaceName(OnlyImage(dictionary)));
    }

    /// <summary>
    /// <b>The two classifiers must agree.</b> <c>setcolorspace</c> once used a second switch, four
    /// lines from the image one, and they disagreed in both directions: that table knew
    /// <c>CalGray</c>/<c>CalRGB</c>/<c>Lab</c> and the image classifier did not, so
    /// <c>/CalRGB setcolorspace … image</c> sized <c>setcolor</c> to three operands while emitting a
    /// <b>DeviceGray</b> image and reading a third of the samples it needed.
    /// </summary>
    [Theory]
    [InlineData("/CalRGB",  "DeviceRGB")]
    [InlineData("/CalGray", "DeviceGray")]
    public void ACieDeviceAnalogue_CarriesItsDeviceSpace(string space, string expected)
    {
        var r = Convert($"{space} setcolorspace << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource " +
                        "<FF000000FF000000FFFFFFFFFF00FF00> >> image");

        Assert.Equal(expected, ColorSpaceName(OnlyImage(r)));
    }

    /// <summary>
    /// <c>/Lab</c> is <b>not</b> a device space in disguise — L is 0..100 and a/b are signed — so
    /// its samples cannot travel as DeviceRGB. Refused rather than approximated, unlike CalRGB.
    /// </summary>
    [Fact]
    public void Lab_IsRefusedRatherThanTreatedAsDeviceRgb()
    {
        var r = Convert("/Lab setcolorspace << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource " +
                        "<FF000000FF000000FFFFFFFFFF00FF00> >> image");

        ContentStreamAssert.DoesNotContainOperator(r.Content, "Do");
        Assert.Contains("Lab", r.LastError);
    }

    /// <summary>With no <c>setcolorspace</c> at all, an image is DeviceGray — PLRM's Level 1 rule.</summary>
    [Fact]
    public void WithNoSetColorSpace_AnImageIsDeviceGray()
    {
        var r = Convert("4 4 8 [4 0 0 -4 0 4] <00112233445566778899AABBCCDDEEFF> image");

        Assert.Equal("DeviceGray", ColorSpaceName(OnlyImage(r)));
    }

    /// <summary>
    /// <c>colorimage</c>'s own arity operand wins over the current colour space: it is what the
    /// file said about <i>this</i> image.
    /// </summary>
    [Fact]
    public void ColorImage_KeepsItsOwnComponentCount()
    {
        var r = Convert("/DeviceGray setcolorspace 2 2 8 [2 0 0 -2 0 2] " +
                        "<FF000000FF000000FFFFFFFF> false 3 colorimage");

        Assert.Equal("DeviceRGB", ColorSpaceName(OnlyImage(r)));
    }

    /// <summary><c>initgraphics</c> discards the space, so a later image does not inherit it.</summary>
    [Fact]
    public void InitGraphics_DiscardsTheImageColourSpace()
    {
        var r = Convert("/DeviceRGB setcolorspace initgraphics " +
                        "4 4 8 [4 0 0 -4 0 4] <00112233445566778899AABBCCDDEEFF> image");

        Assert.Equal("DeviceGray", ColorSpaceName(OnlyImage(r)));
    }

    /// <summary>
    /// The space is part of the graphics state, so <c>gsave</c> carries it into the nested level —
    /// PLRM §4.8, which is why it lives on <c>PsGraphicsState</c>.
    ///
    /// <para><b>The obvious fixture for this cannot fail, and writing it first is the trap.</b>
    /// <c>gsave /DeviceRGB setcolorspace grestore image</c> asserts DeviceGray and passes whether
    /// or not <c>Clone</c> copies the field, because <c>grestore</c> returns to the original state
    /// object — which was never mutated either way. Only setting the space <i>first</i> and reading
    /// it back <i>inside</i> the <c>gsave</c> exercises the copy.</para>
    /// </summary>
    [Fact]
    public void TheImageColourSpace_SurvivesGsave()
    {
        var r = Convert("/DeviceRGB setcolorspace gsave " +
                        "<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource <FF000000FF000000FFFFFFFF> >> image grestore");

        Assert.Equal("DeviceRGB", ColorSpaceName(OnlyImage(r)));
    }

    /// <summary>…and <c>grestore</c> drops a space established inside the bracket.</summary>
    [Fact]
    public void TheImageColourSpace_IsDroppedByGrestore()
    {
        var r = Convert("gsave /DeviceRGB setcolorspace grestore " +
                        "4 4 8 [4 0 0 -4 0 4] <00112233445566778899AABBCCDDEEFF> image");

        Assert.Equal("DeviceGray", ColorSpaceName(OnlyImage(r)));
    }

    // ==================================================================
    //  Wave 17 R10.2 / R10.4
    // ==================================================================

    /// <summary>
    /// A CalRGB image is carried as DeviceRGB <b>and says so</b>, once: the classifier's comment
    /// promised the approximation would be "reported at the image", and nothing did until
    /// 2026-10-16. The control: a DeviceRGB image reports nothing.
    /// </summary>
    [Fact]
    public void ACieDeviceAnalogue_ReportsTheApproximationOncePerConversion()
    {
        var r = Convert("/CalRGB setcolorspace << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource " +
                        "<FF000000FF000000FFFFFFFFFF00FF00> >> image " +
                        "<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource <FF000000FF000000FFFFFFFFFF00FF00> >> image");

        Assert.Equal(2, ContentStreamAssert.Count(r.Content, "Do"));
        Assert.Contains("CalRGB", r.LastError);
        Assert.Contains("DeviceRGB", r.LastError);

        // Once per conversion: a second image adds no report. (`setcolorspace` itself reports
        // once as unimplemented for colour, so the count is compared, not pinned at one.)
        var one = Convert("/CalRGB setcolorspace << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource " +
                          "<FF000000FF000000FFFFFFFFFF00FF00> >> image");
        Assert.Equal(one.DiagnosticCount, r.DiagnosticCount);

        // The control: a device-space image reports nothing at all.
        var control = Convert("<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource <00FF00FF> >> image");
        Assert.True(control.IsClean, control.LastError);
    }

    /// <summary>
    /// An <c>image</c> inside an ASCII <c>%%BeginData</c> section — the DSC-conformant layout,
    /// where the section begins on the line after the comment and so holds the operator that
    /// reads the data — draws. The scanner used to skip the whole section by its count, operator
    /// included, and report only that a binary section was skipped.
    /// </summary>
    [Fact]
    public void AnImageInsideAnAsciiDataSection_Draws()
    {
        var r = Convert("%%BeginData: 80 ASCII Bytes\n" +
                        "2 2 8 [2 0 0 -2 0 2] currentfile /ASCIIHexDecode filter image\n" +
                        "00FF00FF>\n" +
                        "%%EndData\n");

        Assert.Contains("Do", ContentStreamAssert.Operators(r.Content));
        Assert.False(r.SkippedBinarySection);
        Assert.Equal("DeviceGray", ColorSpaceName(OnlyImage(r)));
    }

    // ==================================================================
    //  The colour operators and the image space
    // ==================================================================

    /// <summary>
    /// <c>setgray</c> establishes DeviceGray as the current colour space (PLRM §4.8.2), so an image
    /// after it no longer carries the palette of an <c>/Indexed</c> space set earlier — the same
    /// reset <c>initgraphics</c>'s own <c>0 setgray</c> already made.
    /// </summary>
    [Fact]
    public void SetGray_AfterSetColorSpace_PutsAnImageBackInDeviceGray()
    {
        var r = Convert("[/Indexed /DeviceRGB 1 <000000FFFFFF>] setcolorspace 0.5 setgray " +
                        "2 2 8 [2 0 0 -2 0 2] <00010100> image");

        Assert.Equal("DeviceGray", ColorSpaceName(OnlyImage(r)));
    }

    /// <summary>The control: with no colour operator between them, the palette is carried.</summary>
    [Fact]
    public void SetColorSpace_Indexed_WithNoLaterColorOperator_StillCarriesThePalette()
    {
        var r = Convert("[/Indexed /DeviceRGB 1 <000000FFFFFF>] setcolorspace " +
                        "<< /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource <00010100> >> image");

        Assert.True(OnlyImage(r)[PdfName.ColorSpace] is PdfArray, "the palette was dropped");
    }

    // ==================================================================
    //  Bits per component
    // ==================================================================

    /// <summary>
    /// 16 bits per component is a depth PDF carries (§8.9.5, PDF 1.5), so the samples pass through.
    /// They used to be "assumed" 8 — half the samples read as twice the pixels, and noise drawn.
    /// </summary>
    [Fact]
    public void Image_SixteenBitsPerComponent_PassesThrough()
    {
        var r = Convert("1 1 16 [1 0 0 -1 0 1] <0102> image");

        var image = OnlyImage(r);
        Assert.NotNull(image);
        Assert.Equal(16, ((PdfNumber)image[PdfName.BitsPerComponent]).LongValue);
        Assert.Equal(new byte[] { 0x01, 0x02 }, image.RawData);
    }

    /// <summary>
    /// A depth PDF has no spelling for is refused with its data consumed, not approximated: the
    /// triangle after the inline block still paints, and the gap is named.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(3)]
    public void Image_UnsupportedBitsPerComponent_IsRefusedWithItsDataConsumed(int bpc)
    {
        var r = Convert($"1 1 {bpc} [1 0 0 -1 0 1] currentfile /ASCIIHexDecode filter image\n0102>\n" +
                        "newpath 0 0 moveto 10 0 lineto 10 10 lineto fill");

        Assert.Null(OnlyImage(r));
        Assert.Contains($"image ({bpc} bits per component)", r.UnimplementedOperators);
        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "f"));
    }
}
/// <summary>The tint-transform sampling cap, set through <see cref="PsLimits"/>.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class TintSamplingLimitTests
{
    /// <summary>
    /// Hostile: a DeviceN space's grid is bounded by <c>MaxTintTransformSamples</c> — at 4, two inks
    /// take a 2 × 2 grid; at 3, two a colorant no longer fit and the image is refused, saying why.
    /// </summary>
    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    public void HostileManyInkGrid_IsBoundedByTheSamplingCap(int cap, bool carried)
    {
        try
        {
            PsLimits.MaxTintTransformSamples = cap;
            PostScriptDiagnostics.Clear();
            var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "[/DeviceN [/A /B] /DeviceGray {exch pop}] setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <00FF> >> image"));
            var images = r.Resources?[PdfName.XObject] as PdfDictionary;
            Assert.Equal(carried, images != null && images.Count == 1);
            if (carried)
            {
                var function = (PdfStream)((PdfArray)((PdfStream)images[new PdfName("Im1")])[PdfName.ColorSpace])[3];
                Assert.Equal(4, function.RawData.Length);
            }
            else
                Assert.Contains("need more than", r.LastError, System.StringComparison.Ordinal);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
