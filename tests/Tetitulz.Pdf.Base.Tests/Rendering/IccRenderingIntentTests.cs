// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Asserts that a PDF rendering intent (§8.6.5.8) selects the ICC <c>A2B</c> table it names, and
/// that the selection survives all the way from the <c>ri</c> operator to a rendered pixel.
/// </summary>
/// <remarks>
/// <para><b>The discriminating colour is near the black point, and that had to be measured.</b> In
/// every CMYK press profile on this host, <c>A2B0</c> (perceptual) and <c>A2B1</c> (media-relative
/// colorimetric) agree across the mid-tones and differ where perceptual compresses the black point:
/// paper white, solid cyan and a mid ink mixture all come out identical under both. A fixture built
/// from any of those would make "the intent changed the colour" unfalsifiable — the test would fail
/// against correct code — so every comparison below is made on solid K.</para>
///
/// <para><b>Real profiles only, for the comparisons.</b> A synthetic profile proves the selection
/// plumbing and nothing about whether the tables really differ, because a fixture with two tables
/// is a fixture whose author decided they differ. The host-gated tests are the evidence; the
/// synthetic one below covers the case a synthetic fixture <i>can</i> settle — what happens when
/// the profile has only one table.</para>
/// </remarks>
public class IccRenderingIntentTests
{
    private const string Press = "RSWOP.icm";

    /// <summary>Solid black — the one ink mixture where the two tables disagree.</summary>
    private static readonly float[] SolidBlack = { 0f, 0f, 0f, 1f };

    // ------------------------------------------------------------------
    //  Table selection
    // ------------------------------------------------------------------

    /// <summary>Perceptual and media-relative colorimetric select different tables.</summary>
    /// <remarks>
    /// The load-bearing assertion of the whole feature. If <c>LutFor</c> ignored the intent, or the
    /// parse never read <c>A2B1</c>, these two would be equal.
    /// </remarks>
    [RealIccProfileFact(Press)]
    public void PerceptualAndRelativeColorimetricSelectDifferentTables()
    {
        IccProfile profile = ParsePress();

        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.Perceptual, out SKColor p));
        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.MediaRelativeColorimetric,
                                        out SKColor r));

        Assert.NotEqual(p, r);
    }

    /// <summary>
    /// The default intent is media-relative colorimetric, not perceptual.
    /// </summary>
    /// <remarks>
    /// §8.6.5.8 makes <c>/RelativeColorimetric</c> the default value of <c>/RI</c>, and this
    /// renderer used to read <c>A2B0</c> unconditionally — so honouring the parameter changed what
    /// an <i>unset</i> document renders as, wherever its profile carries a distinct <c>A2B1</c>.
    /// That is a deliberate behaviour change and it deserves an assertion of its own rather than
    /// living only in a commit message.
    /// </remarks>
    [RealIccProfileFact(Press)]
    public void TheDefaultIntentIsMediaRelativeColorimetricRatherThanPerceptual()
    {
        IccProfile profile = ParsePress();

        Assert.True(profile.TryEvaluate(SolidBlack, out SKColor byDefault));
        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.MediaRelativeColorimetric,
                                        out SKColor relative));
        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.Perceptual,
                                        out SKColor perceptual));

        Assert.Equal(relative, byDefault);
        Assert.NotEqual(perceptual, byDefault);
    }

    /// <summary>
    /// Absolute colorimetric reads the relative table, which is what it is built on, and scales
    /// its colour by the profile's media white: RSWOP's solid black comes out lighter than the bare
    /// table's (34/33/33) — the reference renderer's 32/31/31 — and never compensated (relative's is black).
    /// </summary>
    [RealIccProfileFact(Press)]
    public void AbsoluteColorimetricReadsTheRelativeTable_ScaledByTheMediaWhite()
    {
        IccProfile profile = ParsePress();

        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.AbsoluteColorimetric,
                                        out SKColor absolute));
        Assert.True(profile.LutFor(IccRenderingIntent.MediaRelativeColorimetric).TryEvaluate(SolidBlack, out SKColor table));
        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.MediaRelativeColorimetric,
                                        out SKColor relative));

        Assert.NotEqual(table, absolute);
        Assert.True(Math.Abs(absolute.Red - 32) <= 1 && Math.Abs(absolute.Green - 31) <= 1 && Math.Abs(absolute.Blue - 31) <= 1,
            $"absolute solid black is {absolute}, the reference 32/31/31");
        Assert.Same(profile.LutFor(IccRenderingIntent.MediaRelativeColorimetric), profile.LutFor(IccRenderingIntent.AbsoluteColorimetric));
        Assert.True(relative.Red <= 2 && relative.Green <= 2 && relative.Blue <= 2, $"compensated solid black is {relative}");
    }

    /// <summary>
    /// This profile aliases its saturation table onto its perceptual one, and the reader shares the
    /// parsed table rather than building it twice.
    /// </summary>
    /// <remarks>
    /// <para><b>A fact about the profile, recorded because it looks like a bug.</b> Every one of the
    /// host profiles surveyed that carries these tags points <c>A2B2</c> at <c>A2B0</c>'s own byte
    /// offset — saturation is perceptual under another name. Someone comparing the two intents and
    /// finding them equal would otherwise reasonably conclude the selection is broken.</para>
    /// <para>The tag is still <i>present</i>, which is what <c>HasTableFor</c> answers, so this
    /// separates "the profile has no saturation table" from "it has one that is the perceptual
    /// table".</para>
    /// </remarks>
    [RealIccProfileFact(Press)]
    public void ThisProfileAliasesSaturationOntoPerceptual()
    {
        IccProfile profile = ParsePress();

        Assert.True(profile.HasTableFor(IccRenderingIntent.Saturation));

        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.Saturation, out SKColor s));
        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.Perceptual, out SKColor p));

        Assert.Equal(p, s);
    }

    /// <summary>
    /// A profile carrying only <c>A2B0</c> gives the same colour under every intent.
    /// </summary>
    /// <remarks>
    /// The fallback, and the reason this change is safe for every synthetic fixture in the suite:
    /// they all build a one-entry tag table. A profile with nothing to select is not a profile with
    /// a gap — one table is the answer for all four intents — which is also why an intent naming a
    /// missing table raises no diagnostic.
    /// </remarks>
    /// <remarks>
    /// Asserted on the table each intent reads, not the compensated colour: since 2026-10-03 each
    /// intent has its own black point for compensation (absolute none at all), so one table can give
    /// different colours at solid black under different intents — as the reference colour engine gives them.
    /// </remarks>
    [Fact]
    public void AProfileWithOnlyOneTableGivesTheSameColourUnderEveryIntent()
    {
        IccProfile profile = IccProfile.Parse(SingleTableCmykProfile());
        Assert.Equal(IccModel.LutAToB, profile.Model);

        Assert.True(profile.LutFor(IccRenderingIntent.Perceptual).TryEvaluate(SolidBlack, out SKColor first));

        foreach (IccRenderingIntent intent in new[]
                 {
                     IccRenderingIntent.MediaRelativeColorimetric,
                     IccRenderingIntent.Saturation,
                     IccRenderingIntent.AbsoluteColorimetric,
                 })
        {
            Assert.False(profile.HasTableFor(intent));
            Assert.Same(profile.LutFor(IccRenderingIntent.Perceptual), profile.LutFor(intent));
            Assert.True(profile.LutFor(intent).TryEvaluate(SolidBlack, out SKColor other));
            Assert.Equal(first, other);
        }
        // Absolute is never compensated, so it is the table's own colour.
        Assert.True(profile.TryEvaluate(SolidBlack, IccRenderingIntent.AbsoluteColorimetric, out SKColor absolute));
        Assert.Equal(first, absolute);
    }

    // ------------------------------------------------------------------
    //  The intent reaches a pixel
    // ------------------------------------------------------------------

    /// <summary>
    /// The <c>ri</c> operator changes the rendered pixel, so the intent really travels from the
    /// content stream to the ICC evaluator.
    /// </summary>
    /// <remarks>
    /// Everything above tests the profile in isolation. This is the one that fails if the intent is
    /// parsed, stored and then never handed to <c>TryResolveIccBasedColor</c> — which is exactly
    /// what the renderer did before: <c>GraphicsState.RenderingIntent</c> had no readers anywhere
    /// in the tree.
    /// </remarks>
    [RealIccProfileFact(Press)]
    public void TheRiOperatorChangesTheRenderedPixel()
    {
        SKColor perceptual = RenderCentre("/Perceptual ri /Cs1 cs 0 0 0 1 scn 0 0 100 100 re f");
        SKColor relative   = RenderCentre("/RelativeColorimetric ri /Cs1 cs 0 0 0 1 scn 0 0 100 100 re f");

        Assert.NotEqual(perceptual, relative);
    }

    /// <summary>
    /// With no <c>ri</c>, the page renders as media-relative colorimetric — §8.6.5.8's default.
    /// </summary>
    [RealIccProfileFact(Press)]
    public void APageThatNamesNoIntentRendersAsRelativeColorimetric()
    {
        SKColor unset    = RenderCentre("/Cs1 cs 0 0 0 1 scn 0 0 100 100 re f");
        SKColor relative = RenderCentre("/RelativeColorimetric ri /Cs1 cs 0 0 0 1 scn 0 0 100 100 re f");

        Assert.Equal(relative, unset);
    }

    /// <summary>
    /// The intent survives <c>q</c>/<c>Q</c>, because it is a graphics-state parameter.
    /// </summary>
    /// <remarks>
    /// Table 52 lists the rendering intent among the parameters <c>q</c> saves, so a
    /// <c>/Perceptual ri</c> inside a <c>q…Q</c> must not leak past the <c>Q</c>. Worth its own
    /// fixture because the value is stored as a string on the state and read through a converter —
    /// a reader that cached the parsed intent anywhere else would pass every test above and fail
    /// this one.
    /// </remarks>
    [RealIccProfileFact(Press)]
    public void TheIntentIsRestoredByTheStateItWasSetIn()
    {
        SKColor afterRestore = RenderCentre(
            "q /Perceptual ri Q /Cs1 cs 0 0 0 1 scn 0 0 100 100 re f");
        SKColor relative = RenderCentre("/Cs1 cs 0 0 0 1 scn 0 0 100 100 re f");

        Assert.Equal(relative, afterRestore);
    }

    /// <summary>
    /// The SVG exporter honours the intent too, and it is the same intent the raster renderer used.
    /// </summary>
    /// <remarks>
    /// The parity assertion CLAUDE.md asks for. Every "silently wrong output" rendering defect this
    /// repository has found was one renderer lagging the other, and a colour-management change that
    /// reached only the raster path would be exactly that — the same document exported two
    /// different colours depending on which exporter a caller happened to use. Asserting the SVG
    /// fill equals the raster pixel is stronger than asserting the two SVG exports merely differ.
    /// </remarks>
    [RealIccProfileFact(Press)]
    public void TheSvgExporterSelectsTheSameTableAsTheRasterRenderer()
    {
        const string Perceptual = "/Perceptual ri /Cs1 cs 0 0 0 1 scn 0 0 100 100 re f";
        const string Relative   = "/RelativeColorimetric ri /Cs1 cs 0 0 0 1 scn 0 0 100 100 re f";

        string svgPerceptual = Svg(Perceptual);
        string svgRelative   = Svg(Relative);

        Assert.NotEqual(svgPerceptual, svgRelative);

        Assert.Contains(CssOf(RenderCentre(Perceptual)), svgPerceptual, StringComparison.Ordinal);
        Assert.Contains(CssOf(RenderCentre(Relative)),   svgRelative,   StringComparison.Ordinal);
    }

    /// <summary>
    /// An image's own <c>/Intent</c> overrides the graphics state's (§8.9.5.2 Table 89).
    /// </summary>
    /// <remarks>
    /// An image XObject is the one piece of content that carries its own intent, and the override
    /// runs the other way from everything else in the graphics state — the dictionary beats the
    /// state rather than inheriting it. Without this the entry would be parsed and dropped, which
    /// looks identical to honouring it whenever the page's own intent happens to match.
    /// </remarks>
    [RealIccProfileFact(Press)]
    public void AnImagesOwnIntentOverridesTheGraphicsState()
    {
        // The page sets no intent, so it renders relative; the image asks for perceptual.
        SKColor inherited = RenderImageCentre(imageIntent: null);
        SKColor overridden = RenderImageCentre(imageIntent: "/Perceptual");

        Assert.NotEqual(inherited, overridden);

        // ...and the override lands on the perceptual table rather than on some third answer.
        SKColor pageWidePerceptual = RenderImageCentre(imageIntent: null, pageIntent: "/Perceptual");
        Assert.Equal(pageWidePerceptual, overridden);
    }

    /// <summary>
    /// The SVG exporter decodes an image under the state's intent too, and to the raster renderer's
    /// pixel: since batch 81 the writer hands its intent to the shared decode, so a page-wide
    /// <c>/Perceptual ri</c> selects the perceptual table for the exported image as it does for the
    /// drawn one.
    /// </summary>
    [RealIccProfileFact(Press)]
    public void TheSvgExporterDecodesAnImageUnderTheStatesIntent()
    {
        SKColor rasterPerceptual = RenderImageCentre(imageIntent: null, pageIntent: "/Perceptual");
        SKColor rasterRelative   = RenderImageCentre(imageIntent: null);
        Assert.NotEqual(rasterPerceptual, rasterRelative);

        Assert.True(Near(rasterPerceptual, SvgImagePixel(pageIntent: "/Perceptual")), "perceptual: the SVG image differs from the raster pixel");
        Assert.True(Near(rasterRelative, SvgImagePixel(pageIntent: null)), "relative: the SVG image differs from the raster pixel");
    }

    private static bool Near(SKColor a, SKColor b)
        => Math.Abs(a.Red - b.Red) <= 1 && Math.Abs(a.Green - b.Green) <= 1 && Math.Abs(a.Blue - b.Blue) <= 1;

    /// <summary>The exported image's one pixel, read back out of its PNG data URI.</summary>
    private static SKColor SvgImagePixel(string pageIntent)
    {
        using var ms = new MemoryStream(IccImagePdf(imageIntent: null, pageIntent));
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        var image = System.Xml.Linq.XDocument.Parse(svg).Descendants().First(e => e.Name.LocalName == "image");
        string href = image.Attributes().First(a => a.Name.LocalName == "href").Value;
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href);
        using var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        return bmp.GetPixel(0, 0);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static string Svg(string content)
    {
        using var ms = new MemoryStream(IccBasedPdf(content));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string CssOf(SKColor c)
        => string.Format(CultureInfo.InvariantCulture, "rgb({0},{1},{2})", c.Red, c.Green, c.Blue);

    /// <summary>
    /// The centre pixel of a page drawing a 1 × 1 solid-black CMYK ICC image over its whole area.
    /// </summary>
    private static SKColor RenderImageCentre(string imageIntent, string pageIntent = null)
    {
        using var ms = new MemoryStream(IccImagePdf(imageIntent, pageIntent));
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, 72);

        int offset = (50 * page.Stride) + (50 * 4);
        return new SKColor(page.Pixels[offset], page.Pixels[offset + 1],
                           page.Pixels[offset + 2], page.Pixels[offset + 3]);
    }

    private static IccProfile ParsePress()
    {
        IccProfile profile = IccProfile.Parse(File.ReadAllBytes(RealIccProfile.Path(Press)));
        Assert.Equal(IccModel.LutAToB, profile.Model);
        return profile;
    }

    /// <summary>The centre pixel of a 100 × 100 pt page carrying <paramref name="content"/>.</summary>
    private static SKColor RenderCentre(string content)
    {
        using var ms = new MemoryStream(IccBasedPdf(content));
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, 72);

        int offset = (50 * page.Stride) + (50 * 4);
        return new SKColor(page.Pixels[offset], page.Pixels[offset + 1],
                           page.Pixels[offset + 2], page.Pixels[offset + 3]);
    }

    /// <summary>
    /// A page whose <c>/Cs1</c> is <c>[/ICCBased …]</c> over this host's CMYK press profile.
    /// </summary>
    /// <remarks>
    /// Assembled as <b>bytes</b> rather than as a string, because the profile stream is binary and
    /// every xref offset has to be the position of a byte. Building the skeleton as text and
    /// splicing the profile in afterwards works only as long as the text encoding maps one char to
    /// one byte, which is a property of the fixture that nothing would check.
    /// </remarks>
    private static byte[] IccBasedPdf(string content)
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path(Press));

        var bytes = new List<byte>();
        var offsets = new List<int>();

        void Ascii(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));

        void Obj(string body)
        {
            offsets.Add(bytes.Count);
            Ascii(offsets.Count + " 0 obj\n" + body + "\nendobj\n");
        }

        Ascii("%PDF-1.4\n");

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources "
            + "<< /ColorSpace << /Cs1 [/ICCBased 5 0 R] >> >> >>");
        Obj("<< /Length " + Encoding.ASCII.GetByteCount(content).ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + content + "\nendstream");

        offsets.Add(bytes.Count);
        Ascii(offsets.Count + " 0 obj\n<< /N 4 /Length "
              + profile.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        bytes.AddRange(profile);
        Ascii("\nendstream\nendobj\n");

        int xref = bytes.Count;
        Ascii("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (int off in offsets) Ascii(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Ascii("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\n");
        Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return bytes.ToArray();
    }

    /// <summary>
    /// A page drawing one 1 × 1 CMYK ICC image of solid black across the whole page, optionally
    /// carrying its own <c>/Intent</c> and optionally under a page-level <c>ri</c>.
    /// </summary>
    private static byte[] IccImagePdf(string imageIntent, string pageIntent)
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path(Press));
        byte[] samples = { 0, 0, 0, 255 };   // one CMYK pixel: solid K

        string content = (pageIntent == null ? string.Empty : pageIntent + " ri ")
                       + "q 100 0 0 100 0 0 cm /Im1 Do Q";

        var bytes = new List<byte>();
        var offsets = new List<int>();

        void Ascii(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));

        void Obj(string body)
        {
            offsets.Add(bytes.Count);
            Ascii(offsets.Count + " 0 obj\n" + body + "\nendobj\n");
        }

        void StreamObj(string dict, byte[] payload)
        {
            offsets.Add(bytes.Count);
            Ascii(offsets.Count + " 0 obj\n" + dict + "\nstream\n");
            bytes.AddRange(payload);
            Ascii("\nendstream\nendobj\n");
        }

        Ascii("%PDF-1.4\n");

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources "
            + "<< /XObject << /Im1 6 0 R >> >> >>");
        Obj("<< /Length " + Encoding.ASCII.GetByteCount(content).ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + content + "\nendstream");
        StreamObj("<< /N 4 /Length " + profile.Length.ToString(CultureInfo.InvariantCulture) + " >>",
                  profile);
        StreamObj("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                  + "/ColorSpace [/ICCBased 5 0 R] "
                  + (imageIntent == null ? string.Empty : "/Intent " + imageIntent + " ")
                  + "/Length 4 >>",
                  samples);

        int xref = bytes.Count;
        Ascii("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (int off in offsets) Ascii(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Ascii("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\n");
        Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return bytes.ToArray();
    }

    /// <summary>
    /// A minimal CMYK profile with a single <c>A2B0</c> tag, so there is nothing for an intent to
    /// select. Built here rather than shared, because what it is <i>missing</i> is the point.
    /// </summary>
    private static byte[] SingleTableCmykProfile()
    {
        // 4 inputs, 3 outputs, a 2-per-axis CLUT: the smallest lut16Type that evaluates.
        const int Grid = 2;
        int clutEntries = Grid * Grid * Grid * Grid * 3;
        int lutSize = 52 + (4 * 2 * 2) + (clutEntries * 2) + (3 * 2 * 2);
        var lut = new byte[lutSize];

        WriteAscii(lut, 0, "mft2");
        lut[8] = 4;                    // input channels
        lut[9] = 3;                    // output channels
        lut[10] = Grid;                // CLUT grid points
        WriteU16(lut, 48, 2);          // input table entries
        WriteU16(lut, 50, 2);          // output table entries

        int p = 52;
        for (int i = 0; i < 4; i++) { WriteU16(lut, p, 0); WriteU16(lut, p + 2, 0xFFFF); p += 4; }

        // A ramp that is not constant, so a misread of the grid shows up as a wrong colour.
        for (int i = 0; i < clutEntries; i++) { WriteU16(lut, p, (ushort)(i * 907 % 0xFFFF)); p += 2; }
        for (int i = 0; i < 3; i++) { WriteU16(lut, p, 0); WriteU16(lut, p + 2, 0xFFFF); p += 4; }

        var profile = new byte[132 + 12 + lutSize];
        WriteAscii(profile, 12, "prtr");
        WriteAscii(profile, 16, "CMYK");
        WriteAscii(profile, 20, "Lab ");
        WriteAscii(profile, 36, "acsp");
        WriteU32(profile, 0, (uint)profile.Length);
        WriteU32(profile, 128, 1);                 // one tag
        WriteAscii(profile, 132, "A2B0");
        WriteU32(profile, 136, 144);
        WriteU32(profile, 140, (uint)lutSize);
        Array.Copy(lut, 0, profile, 144, lutSize);
        return profile;
    }

    private static void WriteAscii(byte[] b, int at, string s)
    {
        for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i];
    }

    private static void WriteU16(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 8); b[at + 1] = (byte)v;
    }

    private static void WriteU32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }
}
