// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the second round of PostScript-derived rendering improvements:
/// - Transfer functions (/TR, /TR2 in ExtGState) — per-component gamma/curve remapping
/// - Color-key masking (/Mask array on image XObjects) — transparent-color compositing
/// </summary>
/// <remarks>
/// The transfer-function and colour-key tests assert on <b>decoded pixels</b>. They previously
/// checked only that a PNG came back, which cannot tell an applied transfer function from an
/// ignored one — one of them even carried the comment "(identity test for robustness)" while
/// claiming in its name to test inversion.
/// </remarks>
public sealed class PostScriptPs2GapsTests
{
    // ------------------------------------------------------------------
    //  Pixel helpers
    //
    //  The transfer-function fixture draws a 2x2 RGB image — red, green, blue, white —
    //  scaled to fill (100,400)-(300,600) in user space at 72 dpi.
    // ------------------------------------------------------------------

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>Pixel at a PDF user-space point (origin bottom-left).</summary>
    private static SKColor At(SKBitmap bmp, float userX, float userY)
        => bmp.GetPixel((int)userX, bmp.Height - 1 - (int)userY);

    /// <summary>Centre of the image's top-left quadrant — the red source pixel.</summary>
    private static SKColor TopLeftQuadrant(SKBitmap bmp) => At(bmp, 150, 550);

    private static void AssertClose(SKColor expected, SKColor actual, int tol, string because)
    {
        Assert.True(
            Math.Abs(expected.Red   - actual.Red)   <= tol &&
            Math.Abs(expected.Green - actual.Green) <= tol &&
            Math.Abs(expected.Blue  - actual.Blue)  <= tol,
            $"{because}: expected ~RGB({expected.Red},{expected.Green},{expected.Blue}), " +
            $"got RGB({actual.Red},{actual.Green},{actual.Blue}).");
    }

    // ====================================================================
    //  Transfer functions (/TR / /TR2)
    // ====================================================================

    /// <summary>
    /// <c>/TR /Identity</c> is the name form of the transfer function, and its contract is that it
    /// <b>clears</b> whatever is active — so the image must come out with its own colours. The
    /// fixture defines a fully inverting function alongside it and points <c>/TR</c> at the name:
    /// if <c>/Identity</c> were ignored and the function object applied, red would come out cyan;
    /// if <c>/Identity</c> were mishandled some other way, red would come out something else again.
    ///
    /// <para>The predecessor rendered <c>q Q</c> — no image, no drawing at all — and asserted a
    /// non-empty PNG. It passed with transfer functions entirely unimplemented, which is exactly
    /// what its sibling <c>ExtGState_TR_InvertFunction_InvertsImagePixels</c> was written to catch
    /// after the same weakness was found there.</para>
    /// </summary>
    [Fact]
    public void ExtGState_TR_IdentityName_ClearsAnActiveTransferFunction()
    {
        // GS1 installs a fully inverting function (C0=[1] C1=[0], so v -> 1-v); GS2 then names
        // /Identity. The image must come back RED. Its sibling
        // ExtGState_TR_InvertFunction_InvertsImagePixels runs the same fixture without GS2 and
        // asserts CYAN — between them, red here can only mean /Identity did its job.
        byte[] pdf = BuildPdfWithTransferFunctionAndImage(
            trFnObjNum: 7,
            trFnDef: "7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>\nendobj\n",
            secondTr: "/Identity");

        AssertClose(SKColors.Red, TopLeftQuadrant(Render(pdf)), 12,
            "/TR /Identity did not clear the active transfer function");
    }

    /// <summary>An identity /TR must leave the image exactly as it was.</summary>
    [Fact]
    public void ExtGState_TR_IdentityFunction_LeavesImagePixelsUnchanged()
    {
        byte[] pdf = BuildPdfWithTransferFunctionAndImage(
            trFnObjNum: 7,
            trFnDef: "7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>\nendobj\n");

        AssertClose(SKColors.Red, TopLeftQuadrant(Render(pdf)), 12,
            "an identity /TR altered the image");
    }

    /// <summary>
    /// The real check for /TR: <c>C0=[1] C1=[0]</c> maps every component v → 1-v, so the
    /// image's red pixel must come out cyan. This test previously asserted only that a PNG
    /// was produced, and its own comment conceded it was "an identity test for robustness" —
    /// it passed with transfer functions entirely unimplemented.
    /// </summary>
    [Fact]
    public void ExtGState_TR_InvertFunction_InvertsImagePixels()
    {
        byte[] pdf = BuildPdfWithTransferFunctionAndImage(
            trFnObjNum: 7,
            trFnDef: "7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>\nendobj\n");

        // Source pixel is pure red (255,0,0); inverted it is cyan (0,255,255).
        AssertClose(SKColors.Cyan, TopLeftQuadrant(Render(pdf)), 12,
            "/TR did not invert the image");
    }

    /// <summary>
    /// /TR2 takes precedence over /TR (PDF 1.3+). With an inverting /TR and an identity
    /// /TR2, the output must match the untransformed image — not the inverted one.
    /// </summary>
    [Fact]
    public void ExtGState_TR2_OverridesTR()
    {
        byte[] pdf = BuildPdfWithTransferFunctionAndImage(
            trFnObjNum: 7,
            trFnDef: "7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>\nendobj\n",
            extraExtGStateKeys: "/TR2 /Identity");

        var px = TopLeftQuadrant(Render(pdf));
        AssertClose(SKColors.Red, px, 12, "/TR2 /Identity did not override the inverting /TR");
        Assert.False(px.Green > 128 && px.Blue > 128,
            "the inverting /TR was applied even though /TR2 /Identity should have replaced it");
    }

    [Fact]
    public void NoTransferFunction_ImageRendersUnchanged()
    {
        // Baseline: with no /TR the image's own colours must survive to the page.
        byte[] pdf = BuildPdfWithTransferFunctionAndImage(
            trFnObjNum: 7,
            trFnDef: "7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>\nendobj\n");

        AssertClose(SKColors.Red, TopLeftQuadrant(Render(pdf)), 12,
            "the untransformed image did not reach the page");
    }

    // ====================================================================
    //  Color-key masking (/Mask as array)
    // ====================================================================

    // The raw-image fixture is a 4x1 row — red, green, blue, white — drawn across
    // (100,400)-(500,500), so each source pixel owns a 100 pt band:
    //   red 100..200, green 200..300, blue 300..400, white 400..500.
    private static SKColor RedBand(SKBitmap b)   => At(b, 150, 450);
    private static SKColor GreenBand(SKBitmap b) => At(b, 250, 450);

    [Fact]
    public void ColorKeyMask_RgbImage_MatchingPixelsTransparent()
    {
        // /Mask [255 255 0 0 0 0] makes pure-red pixels transparent, so the white page
        // shows through the red band while the green band is untouched.
        byte[] masked = BuildPdfWithRawImage(decode: null, maskArray: "[255 255 0 0 0 0]");
        byte[] plain  = BuildPdfWithRawImage(decode: null, maskArray: null);

        var m = Render(masked);
        var p = Render(plain);

        AssertClose(SKColors.Red,   RedBand(p), 12, "baseline red band is wrong");
        AssertClose(SKColors.White, RedBand(m), 12, "/Mask did not make the red pixel transparent");
        AssertClose(GreenBand(p), GreenBand(m), 12, "/Mask wrongly affected a non-matching pixel");
    }

    [Fact]
    public void ColorKeyMask_GreyscaleImage_MatchingPixelsTransparent()
    {
        // Greyscale row is [0, 64, 128, 255]; /Mask [0 0] makes the black pixel transparent.
        byte[] masked = BuildPdfWithRawImage(decode: null, maskArray: "[0 0]", components: 1);
        byte[] plain  = BuildPdfWithRawImage(decode: null, maskArray: null,    components: 1);

        var m = Render(masked);
        var p = Render(plain);

        AssertClose(SKColors.Black, RedBand(p), 12, "baseline first band should be black");
        AssertClose(SKColors.White, RedBand(m), 12, "/Mask [0 0] did not clear the black pixel");
        // Value 64 is outside the mask range, so the second band must survive.
        AssertClose(GreenBand(p), GreenBand(m), 12, "/Mask cleared a non-matching grey pixel");
    }

    [Fact]
    public void ColorKeyMask_NoMatchingPixels_AllPixelsOpaque()
    {
        // /Mask range that can never match (min > max for each component): the image must
        // come out byte-identical to the unmasked render.
        byte[] impossible = BuildPdfWithRawImage(
            decode: null, maskArray: "[200 100 200 100 200 100]");
        byte[] plain = BuildPdfWithRawImage(decode: null, maskArray: null);

        var i = Render(impossible);
        var p = Render(plain);

        AssertClose(SKColors.Red, RedBand(i), 12,
            "an unsatisfiable /Mask range made a pixel transparent");
        AssertClose(RedBand(p),   RedBand(i),   4, "masked render differs from the baseline");
        AssertClose(GreenBand(p), GreenBand(i), 4, "masked render differs from the baseline");
    }

    [Fact]
    public void ColorKeyMask_AbsentMaskEntry_LeavesEveryPixelOpaque()
    {
        // Baseline: no /Mask key at all — the colour-key path must be a no-op, so all four
        // source pixels reach the page in their own colours.
        var bmp = Render(BuildPdfWithRawImage(decode: null, maskArray: null));

        AssertClose(SKColors.Red,   At(bmp, 150, 450), 12, "red band");
        AssertClose(SKColors.Lime,  At(bmp, 250, 450), 12, "green band");
        AssertClose(SKColors.Blue,  At(bmp, 350, 450), 12, "blue band");
        AssertClose(SKColors.White, At(bmp, 450, 450), 12, "white band");
    }

    // ====================================================================
    //  Inline image (BI…ID…EI) — abbreviated keys + post-processing chain
    // ====================================================================

    // The inline-image fixture is a 2x1 RGB row — red then green — drawn across
    // (100,400)-(300,500), so the left half is red and the right half green.
    private static SKColor InlineLeftHalf(SKBitmap b)  => At(b, 150, 450);
    private static SKColor InlineRightHalf(SKBitmap b) => At(b, 250, 450);

    /// <summary>
    /// <c>/IM</c> is the abbreviated spelling of <c>/ImageMask</c>, so
    /// <c>ExpandAbbreviatedImageDict</c> has to map it before <c>DecodeToBitmap</c> looks for
    /// <c>/ImageMask</c>. With <c>/IM false</c> the image stays an ordinary RGB bitmap and must
    /// paint its own red and green samples — not the stencil-mask path's current fill colour.
    ///
    /// <para>Asserted against the identical image with no <c>/IM</c> key at all, so the test says
    /// "the abbreviation changed nothing", which is the actual contract.</para>
    /// </summary>
    [Fact]
    public void InlineImage_AbbreviatedIM_False_RendersItsOwnColours()
    {
        var withIm    = Render(BuildPdfWithInlineImage(extraKeys: "/IM false"));
        var withoutIm = Render(BuildPdfWithInlineImage(extraKeys: null));

        AssertClose(SKColors.Red,  InlineLeftHalf(withIm),  12, "/IM false lost the red sample");
        AssertClose(SKColors.Lime, InlineRightHalf(withIm), 12, "/IM false lost the green sample");

        AssertClose(InlineLeftHalf(withoutIm),  InlineLeftHalf(withIm),  4,
            "/IM false rendered differently from the same image with no /IM key");
        AssertClose(InlineRightHalf(withoutIm), InlineRightHalf(withIm), 4,
            "/IM false rendered differently from the same image with no /IM key");
    }

    /// <summary>
    /// The baseline for the case above, and for the SMask / colour-key / transfer-function
    /// post-processing chain that <c>OpBi</c> runs after decoding an inline image: with none of
    /// those optional entries present every step must be a no-op, so the two source samples reach
    /// the page in their own colours and nothing is painted outside the image's placement.
    /// </summary>
    [Fact]
    public void InlineImage_NoExtraKeys_PostProcessingLeavesTheSamplesAlone()
    {
        var bmp = Render(BuildPdfWithInlineImage(extraKeys: null));

        AssertClose(SKColors.Red,  InlineLeftHalf(bmp),  12, "the inline image's red sample");
        AssertClose(SKColors.Lime, InlineRightHalf(bmp), 12, "the inline image's green sample");
        AssertClose(SKColors.White, At(bmp, 450, 450), 12,
            "ink appeared to the right of the inline image's 200 pt width");
        AssertClose(SKColors.White, At(bmp, 200, 300), 12,
            "ink appeared below the inline image's placement rectangle");
    }

    /// <summary>
    /// Builds a PDF whose content stream contains a 2×1 inline RGB image (via BI…ID…EI).
    /// <paramref name="extraKeys"/> is an optional space-separated string of additional
    /// key/value pairs to append to the inline image header (e.g. "/IM false").
    /// </summary>
    private static byte[] BuildPdfWithInlineImage(string extraKeys)
    {
        // 2×1 raw RGB pixels: red + green, ASCIIHex-encoded.
        const string hexPixels = "FF0000 00FF00>";   // ">" terminates ASCIIHexDecode

        string extra = extraKeys != null ? " " + extraKeys : "";

        // Inline image content stream: BI <keys> ID <data> EI
        string cs =
            $"q 200 0 0 100 100 400 cm\n" +
            $"BI /W 2 /H 1 /CS /RGB /BPC 8 /F /AHx{extra}\nID\n{hexPixels}\nEI Q";

        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    /// <summary>
    /// Builds a minimal PDF with one ExtGState resource and a simple content stream.
    /// </summary>
    private static byte[] BuildPdfWithExtGState(string extGsDict, string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n" + extGsDict + "endobj\n");
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>" +
            " /ExtGState << /GS1 6 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a PDF that sets a transfer function via ExtGState then paints a raw image.
    /// <paramref name="trFnObjNum"/> is the object number for the function dict.
    /// <paramref name="trFnDef"/> is the literal object definition to embed (e.g. "7 0 obj ...").
    /// </summary>
    private static byte[] BuildPdfWithTransferFunctionAndImage(
        int trFnObjNum, string trFnDef, string extraExtGStateKeys = "", string trValue = null,
        string secondTr = null)
    {
        // 2×2 red/green/blue/white pixels as raw RGB
        byte[] imgData = { 255,0,0, 0,255,0, 0,0,255, 255,255,255 };
        var hexSb = new StringBuilder();
        foreach (byte b in imgData) hexSb.AppendFormat("{0:X2}", b);
        hexSb.Append(">");
        string hexData = hexSb.ToString();

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Transfer function object
        int oTr = sb.Length;
        sb.Append(trFnDef);

        // ExtGState referencing the transfer function (obj 8)
        int o8 = sb.Length;
        // trValue lets a test point /TR at a NAME (/Identity, /Default) instead of the function.
        sb.AppendFormat(
            "8 0 obj\n<< /TR {0} {1} >>\nendobj\n",
            trValue ?? (trFnObjNum + " 0 R"), extraExtGStateKeys);

        // Image XObject (obj 5)
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2" +
            " /ColorSpace /DeviceRGB /BitsPerComponent 8" +
            " /Filter /ASCIIHexDecode /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            hexData.Length, hexData);

        // A second ExtGState, written INLINE in the resource dictionary (legal per §7.8.3, and it
        // needs no extra indirect object, so the xref below is untouched). It exists so a test can
        // apply a real transfer function and then apply /TR /Identity over it — the only fixture
        // shape that can prove /Identity CLEARS an active function rather than merely parsing.
        string gs2 = secondTr == null ? "" : $" /GS2 << /TR {secondTr} >>";

        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 6 0 R >>" +
            " /XObject << /Im1 5 0 R >>" +
            " /ExtGState << /GS1 8 0 R" + gs2 + " >> >> >>\nendobj\n");

        // `/GS1 gs`, not `gs GS1`: the ExtGState name is the OPERAND and precedes the operator
        // (PDF §8.4.5). The reversed form parsed as a no-operand `gs` followed by a stray token,
        // so the ExtGState — and therefore the transfer function under test — was never applied.
        string cs = (secondTr == null ? "/GS1 gs" : "/GS1 gs /GS2 gs")
                  + " q 200 0 0 200 100 400 cm /Im1 Do Q";
        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", oTr);  // obj 7
        sb.AppendFormat("{0:D10} 00000 n \n", o8);   // obj 8
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a PDF with a 4×1 raw-pixel image and optional /Mask (color-key) and /Decode arrays.
    /// </summary>
    private static byte[] BuildPdfWithRawImage(string decode, string maskArray, int components = 3)
    {
        // 4×1 pixel row: [red, green, blue, white] for RGB; [0,64,128,255] for grey.
        byte[] imgData;
        if (components == 3)
            imgData = new byte[] { 255,0,0, 0,255,0, 0,0,255, 255,255,255 };
        else
            imgData = new byte[] { 0, 64, 128, 255 };

        var hexSb = new StringBuilder();
        foreach (byte b in imgData) hexSb.AppendFormat("{0:X2}", b);
        hexSb.Append(">");
        string hexData = hexSb.ToString();

        string colorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB";
        string decodeEntry = decode   != null ? $" /Decode {decode}" : "";
        string maskEntry   = maskArray != null ? $" /Mask {maskArray}" : "";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 4 /Height 1" +
            " /ColorSpace {0} /BitsPerComponent 8{1}{2}" +
            " /Filter /ASCIIHexDecode /Length {3} >>\nstream\n{4}\nendstream\nendobj\n",
            colorSpace, decodeEntry, maskEntry, hexData.Length, hexData);
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 6 0 R >>" +
            " /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        const string cs = "q 400 0 0 100 100 400 cm /Im1 Do Q";
        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
