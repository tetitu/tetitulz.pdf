// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfSeparationRenderer"/> against the reference renderer.3's separation output device on the same
/// files (rendered at 72 dpi; separation output stores 255 − ink, read back here as ink). Where
/// the reference renderer cannot answer — it skips a spot ink used only inside a pattern — the expectation is
/// the specification's and says so.
/// </summary>
public class PdfSeparationRendererTests
{
    private const int Side = 200;

    /// <summary>
    /// A 200 × 200 pt page: rich black and magenta squares, a grey square, a Spot1 square at 70%,
    /// an overprinting yellow bar across the first two (OPM 1), a DeviceN [Cyan Spot2] bar (no
    /// overprint), an /All mark, and an overprinting Spot1 bar on the magenta.
    /// </summary>
    private const string VectorPage = @"
0.6 0.5 0.4 1 k 10 10 80 80 re f
0 1 0 0 k 110 10 80 80 re f
0.25 g 10 110 80 80 re f
/CS1 cs 0.7 scn 110 110 80 80 re f
/GS1 gs
0 0 1 0 k 50 50 100 30 re f
/GS0 gs
/CS2 cs 0.4 0.9 scn 50 120 100 30 re f
/CS3 cs 1 scn 95 95 10 10 re f
/GS1 gs
/CS1 cs 1 scn 140 20 40 20 re f
";

    private const string Spot1 = "[/Separation /Spot1 /DeviceCMYK 5 0 R]";
    private const string Resources =
        "/ColorSpace << /CS1 " + Spot1 + " /CS2 [/DeviceN [/Cyan /Spot2] /DeviceCMYK 6 0 R] " +
        "/CS3 [/Separation /All /DeviceCMYK 7 0 R] /PCS [/Pattern " + Spot1 + "] /PCS2 [/Pattern /DeviceCMYK] >> " +
        "/ExtGState << /GS1 << /OP true /op true /OPM 1 >> /GS0 << /OP false /op false /OPM 0 >> " +
        "/GSa << /ca 0.5 >> /GSm << /SMask << /S /Luminosity /G 11 0 R >> >> /GSl << /BM /Luminosity >> /GSt << /TR 18 0 R >> >> " +
        "/XObject << /Im1 8 0 R /Mk1 9 0 R /ImS 12 0 R /ImG 13 0 R /ImN 14 0 R /ImR 15 0 R /ImA 16 0 R /ImD 21 0 R >> /Pattern << /P1 10 0 R /P2 20 0 R >> /Shading << /Sh1 19 0 R >>";

    private static PdfSeparations Render(string content)
    {
        using var ms = new MemoryStream(Pdf(content));
        return PdfSeparationRenderer.RenderPage(ms, 0, 72);
    }

    /// <summary>Ink on <paramref name="plate"/> at PDF point (x, y) — y up, as the content stream writes it.</summary>
    private static int Ink(PdfSeparations s, string plate, int x, int y)
    {
        var p = s[plate];
        Assert.NotNull(p);
        return p.Tints[(s.Height - y) * s.Width + x];
    }

    public static IEnumerable<object[]> ReferenceVectorPoints()
    {
        //                       x    y    Cyan Mag  Yel  Black Spot1 Spot2
        yield return Row("rich black",            30,  30, 153, 127, 102, 255,   0,   0);
        yield return Row("rich black + yellow op", 60,  60, 153, 127, 255, 255,   0,   0);
        yield return Row("magenta",               130,  30,   0, 255,   0,   0,   0,   0);
        yield return Row("magenta + yellow op",   130,  60,   0, 255, 255,   0,   0,   0);
        yield return Row("grey 0.25",              30, 130,   0,   0,   0, 191,   0,   0);
        yield return Row("DeviceN over grey",      60, 130, 102,   0,   0,   0,   0, 230);
        yield return Row("DeviceN over Spot1",    130, 135, 102,   0,   0,   0,   0, 230);
        yield return Row("All",                   100, 100, 255, 255, 255, 255, 255, 255);
        yield return Row("Spot1 op on magenta",   160,  30,   0, 255,   0,   0, 255,   0);
        yield return Row("Spot1 alone",           180, 170,   0,   0,   0,   0, 179,   0);
    }

    private static object[] Row(string what, int x, int y, params int[] ink) => new object[] { what, x, y, ink };

    /// <summary>
    /// Every plate at ten points against separation output's. A tolerance of one: at exactly 50% the reference renderer
    /// truncates (127) where <see cref="PdfColor.Channel"/> rounds (128).
    /// </summary>
    [Theory]
    [MemberData(nameof(ReferenceVectorPoints))]
    public void VectorPaints_LandOnThePlatesTheReferencePutsThemOn(string what, int x, int y, int[] ink)
    {
        PdfSeparations s = Render(VectorPage);
        string[] plates = { "Cyan", "Magenta", "Yellow", "Black", "Spot1", "Spot2" };
        for (int i = 0; i < plates.Length; i++)
            Assert.True(Math.Abs(Ink(s, plates[i], x, y) - ink[i]) <= 1,
                $"{what}: {plates[i]} is {Ink(s, plates[i], x, y)}, reference {ink[i]}");
    }

    [Fact]
    public void ThePlates_AreTheProcessColorants_ThenTheSpotsInFirstPaintedOrder_AllExact()
    {
        PdfSeparations s = Render(VectorPage);
        Assert.Equal(new[] { "Cyan", "Magenta", "Yellow", "Black", "Spot1", "Spot2" }, s.Plates.Select(p => p.Colorant));
        Assert.Equal((Side, Side), (s.Width, s.Height));
        Assert.All(s.Plates, p => Assert.Equal(0, p.ApproximatedPaints));
    }

    /// <summary>
    /// Separable transparency on a plate is §11.3.5's subtractive blend: half-opaque cyan over full
    /// black leaves half the black and lays half the cyan — separation output's 128 and 127.
    /// </summary>
    [Fact]
    public void HalfOpaqueCyanOverBlack_BlendsPerPlateAsTheReferenceDoes()
    {
        PdfSeparations s = Render("0 0 0 1 k 10 10 180 40 re f q /GSa gs 1 0 0 0 k 20 20 60 60 re f Q");
        Assert.InRange(Ink(s, "Cyan", 40, 30), 127, 128);
        Assert.InRange(Ink(s, "Black", 40, 30), 127, 128);
        Assert.InRange(Ink(s, "Cyan", 40, 70), 127, 128);
        Assert.Equal(0, Ink(s, "Black", 40, 70));
    }

    /// <summary>A stencil mask paints in the fill colour (§8.9.6.2) — here a spot ink at 50%: on its own plate, as separation output has it, and nowhere else.</summary>
    [Fact]
    public void AStencilMaskInASpotColour_PaintsOnlyTheSpotPlate()
    {
        PdfSeparations s = Render("q /CS1 cs 0.5 scn 80 0 0 80 110 110 cm /Mk1 Do Q");
        Assert.InRange(Ink(s, "Spot1", 130, 170), 127, 128);   // sample 0 paints
        Assert.Equal(0, Ink(s, "Spot1", 170, 170));            // sample 1 does not
        Assert.Equal(0, Ink(s, "Magenta", 130, 170));          // not the alternate's ink
        Assert.Equal(0, s["Spot1"].ApproximatedPaints);
    }

    /// <summary>
    /// An uncoloured tiling pattern whose underlying space is a spot ink paints its cells in that
    /// ink (§8.7.3.3). The reference renderer skips this separation (it lists them before rendering), so
    /// the expectation is the specification's: full Spot1 inside a cell's square, none between.
    /// </summary>
    [Fact]
    public void AnUncolouredPatternInASpotColour_PaintsItsCellsOnTheSpotPlate()
    {
        PdfSeparations s = Render("q /PCS cs 1 /P1 scn 10 110 80 80 re f Q");
        Assert.Equal(255, Ink(s, "Spot1", 12, 112));
        Assert.Equal(0, Ink(s, "Spot1", 17, 117));
        Assert.Equal(0, Ink(s, "Magenta", 12, 112));
    }

    /// <summary>
    /// A luminosity soft mask is computed from its group's colour, not from any plate: a grey 0.5
    /// group halves full cyan to 128. Drawn on the cyan plate the group's grey would carry no cyan
    /// ink, luminosity 1, and the cyan would stay full.
    /// </summary>
    [Fact]
    public void ALuminositySoftMask_IsComputedInColour_NotOnThePlate()
    {
        PdfSeparations s = Render("q /GSm gs 1 0 0 0 k 100 60 90 40 re f Q");
        Assert.InRange(Ink(s, "Cyan", 150, 80), 127, 128);
        Assert.Equal(0, Ink(s, "Black", 150, 80));
    }

    public static IEnumerable<object[]> ReferenceImagePoints()
    {
        // Each image 2 × 1 samples drawn 80 × 40 pt; the left sample at x + 20, the right at x + 60.
        //                                                 x    y   Cyan Mag  Yel  Black Spot1 Spot2
        yield return Row("CMYK rich black",                130,  30, 153, 128, 102, 255,   0,   0);
        yield return Row("CMYK magenta",                   170,  30,   0, 255,   0,   0,   0,   0);
        yield return Row("Separation 255",                  30,  30,   0,   0,   0,   0, 255,   0);
        yield return Row("Separation 64",                   70,  30,   0,   0,   0,   0,  64,   0);
        yield return Row("DeviceGray 64",                   30,  70,   0,   0,   0, 191,   0,   0);
        yield return Row("DeviceGray 255",                  70,  70,   0,   0,   0,   0,   0,   0);
        yield return Row("DeviceN 16-bit (0.4, 0.9)",       30, 130, 102,   0,   0,   0,   0, 230);
        yield return Row("DeviceN 16-bit (0, 0.25)",        70, 130,   0,   0,   0,   0,   0,  64);
        yield return Row("CMYK rich black under SMask 0.5", 130, 130,  77,  64,  51, 128,   0,   0);
        yield return Row("CMYK magenta under SMask 0.5",    170, 130,   0, 128,   0,   0,   0,   0);
    }

    /// <summary>
    /// Images are read from their samples, not their sRGB pixels: a DeviceCMYK rich black keeps
    /// its 60/50/40 (sRGB would make it plain black), a spot image is ink on its own plate — the
    /// plate discovered from the image alone — grey lands on black, a 16-bit DeviceN splits by
    /// name, and a soft mask scales the ink. Every value separation output's; one apart where the reference renderer
    /// truncates (0.25 of 16 bits is 63.75).
    /// </summary>
    [Theory]
    [MemberData(nameof(ReferenceImagePoints))]
    public void Images_LandOnThePlatesFromTheirSamples_AsTheReferenceHasThem(string what, int x, int y, int[] ink)
    {
        PdfSeparations s = Render(ImagePage);
        string[] plates = { "Cyan", "Magenta", "Yellow", "Black", "Spot1", "Spot2" };
        for (int i = 0; i < plates.Length; i++)
            Assert.True(Math.Abs(Ink(s, plates[i], x, y) - ink[i]) <= 1,
                $"{what}: {plates[i]} is {Ink(s, plates[i], x, y)}, reference {ink[i]}");
        Assert.All(s.Plates, p => Assert.Equal(0, p.ApproximatedPaints));
    }

    private const string ImagePage =
        "q 80 0 0 40 110 10 cm /Im1 Do Q q 80 0 0 40 10 10 cm /ImS Do Q q 80 0 0 40 10 50 cm /ImG Do Q " +
        "q 80 0 0 40 10 110 cm /ImN Do Q q 80 0 0 40 110 110 cm /ImA Do Q";

    /// <summary>
    /// An RGB image has no colorants to read: it reaches the plates through the inverse of the CMYK
    /// conversion (pure red → magenta and yellow), and every process plate counts the approximation
    /// and reports it once.
    /// </summary>
    [Fact]
    public void AnRgbImage_IsApproximatedThroughSrgb_AndSaysSo()
    {
        var reports = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.SeparationApproximated && Environment.CurrentManagedThreadId == own)
                reports.Add(detail);
        }

        PdfSeparations s;
        RenderDiagnostics.OnEvent += Sink;
        try { s = Render("/CS1 cs 1 scn 100 0 100 100 re f q 80 0 0 40 110 10 cm /ImR Do Q"); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Equal((0, 255, 255, 0), (Ink(s, "Cyan", 150, 30), Ink(s, "Magenta", 150, 30),
                                        Ink(s, "Yellow", 150, 30), Ink(s, "Black", 150, 30)));
        // An RGB image names no spot: it knocks the spot ink beneath out, and beside it the ink stays.
        Assert.Equal(0, Ink(s, "Spot1", 150, 30));
        Assert.Equal(255, Ink(s, "Spot1", 150, 80));
        // The process plates are approximated; the spot plate is not — an RGB image names no spot,
        // so knocking it out is the exact answer.
        Assert.All(s.Plates, p => Assert.Equal(p.Colorant == "Spot1" ? 0 : 1, p.ApproximatedPaints));
        Assert.Equal(4, reports.Count);
        Assert.All(reports, r => Assert.Contains("an image", r, StringComparison.Ordinal));
    }

    /// <summary>
    /// A shading and a shading pattern are read from their own components on every plate — here a
    /// constant DeviceCMYK magenta — and nothing is reported as approximated. (That the components,
    /// not the sRGB round trip, are what is read: <c>ShadingPlateTests</c>' rich black.)
    /// </summary>
    [Theory]
    [InlineData("q 10 10 80 80 re W n /Sh1 sh Q", "a shading")]
    [InlineData("/Pattern cs /P2 scn 10 10 80 80 re f", "a shading pattern")]
    public void ShadingsAndShadingPatterns_AreReadFromTheirComponents_NotApproximated(string content, string what)
    {
        var reports = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.SeparationApproximated && Environment.CurrentManagedThreadId == own)
                reports.Add(detail);
        }

        PdfSeparations s;
        RenderDiagnostics.OnEvent += Sink;
        try { s = Render(content); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Equal(255, Ink(s, "Magenta", 50, 50));
        Assert.Equal(0, Ink(s, "Cyan", 50, 50));
        Assert.Equal(0, Ink(s, "Magenta", 150, 150));
        Assert.All(s.Plates, p => Assert.Equal(0, p.ApproximatedPaints));
        Assert.DoesNotContain(reports, r => r.Contains(what + " reached", StringComparison.Ordinal));
    }

    /// <summary>
    /// §8.6.7: non-zero overprint mode "shall not … apply to the painting of images". An
    /// overprinting DeviceCMYK image under OPM 1 writes all four process plates, so its magenta
    /// sample knocks the black band underneath out — separation output's 0 — where a vector fill of the same
    /// colour would leave it.
    /// </summary>
    [Fact]
    public void AnOverprintingCmykImage_IgnoresTheOverprintMode_AndKnocksOutBlack()
    {
        PdfSeparations s = Render("0 0 0 1 k 10 10 180 40 re f /CS1 cs 1 scn 100 0 100 40 re f 100 110 100 10 re f " +
                                  "/GS1 gs q 80 0 0 40 110 10 cm /Im1 Do Q /GS0 gs q 80 0 0 40 110 110 cm /Im1 Do Q " +
                                  "/GS1 gs 0 1 0 0 k 20 20 20 20 re f");
        Assert.Equal(255, Ink(s, "Magenta", 170, 30));
        Assert.Equal(0, Ink(s, "Black", 170, 30));
        // The image names no spot, so overprinting it leaves the spot ink beneath; not overprinting, it knocks it out.
        Assert.Equal(255, Ink(s, "Spot1", 170, 20));
        Assert.Equal(0, Ink(s, "Spot1", 170, 115));
        Assert.Equal(255, Ink(s, "Spot1", 105, 115));
        // The control: the same magenta as a vector fill, same state, leaves the black alone.
        Assert.Equal(255, Ink(s, "Magenta", 30, 30));
        Assert.Equal(255, Ink(s, "Black", 30, 30));
    }

    /// <summary>
    /// An image's <c>/Decode</c> maps its samples before they are ink (§8.9.5.2): inverted on every
    /// component, the rich black sample 153/128/102/255 is ink 102/127/153/0.
    /// </summary>
    [Fact]
    public void AnImagesDecodeArray_MapsItsSamplesBeforeTheyAreInk()
    {
        PdfSeparations s = Render("q 80 0 0 40 110 10 cm /ImD Do Q");
        Assert.Equal(102, Ink(s, "Cyan", 130, 30));
        Assert.InRange(Ink(s, "Magenta", 130, 30), 127, 128);
        Assert.Equal(153, Ink(s, "Yellow", 130, 30));
        Assert.Equal(0, Ink(s, "Black", 130, 30));
    }

    /// <summary>
    /// An overprinting spot image leaves the process plates alone on a plate render — and says
    /// nothing about overprint not being simulated, because here it is. The ordinary render of the
    /// same page does report it (the potency control).
    /// </summary>
    [Fact]
    public void AnOverprintingSpotImage_IsSimulatedOnThePlates_AndNotReportedAsSkipped()
    {
        const string content = "0 1 0 0 k 0 0 200 200 re f /GS1 gs q 80 0 0 40 10 10 cm /ImS Do Q";
        var onPlates = new List<string>();
        var ordinary = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        List<string> into = onPlates;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.OverprintNotSimulated && Environment.CurrentManagedThreadId == own)
                into.Add(detail);
        }

        PdfSeparations s;
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            s = Render(content);
            into = ordinary;
            PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf(content)), 0, 72);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Equal(255, Ink(s, "Spot1", 30, 30));
        Assert.Equal(255, Ink(s, "Magenta", 30, 30));
        Assert.DoesNotContain(onPlates, d => d.Contains("image", StringComparison.Ordinal));
        Assert.Contains(ordinary, d => d.Contains("image", StringComparison.Ordinal));
    }

    /// <summary>An image drawn twice is read from its samples both times: the decode cache, whose bitmap has none, is not used on a plate.</summary>
    [Fact]
    public void AnImageDrawnTwice_IsExactBothTimes()
    {
        PdfSeparations s = Render("q 80 0 0 40 110 10 cm /Im1 Do Q q 80 0 0 40 110 110 cm /Im1 Do Q");
        Assert.Equal(153, Ink(s, "Cyan", 130, 30));
        Assert.Equal(153, Ink(s, "Cyan", 130, 130));
        Assert.All(s.Plates, p => Assert.Equal(0, p.ApproximatedPaints));
    }

    /// <summary>
    /// An uncoloured pattern in a DeviceCMYK base paints its cells in the pattern's components —
    /// a rich black here, whose 60/50/40 its sRGB would lose — and not in the colour set before it
    /// (pure yellow), which the pattern operator must replace.
    /// </summary>
    [Fact]
    public void AnUncolouredCmykPattern_PaintsItsOwnComponents_NotTheColourBeforeIt()
    {
        PdfSeparations s = Render("0 0 1 0 k q /PCS2 cs 0.6 0.5 0.4 1 /P1 scn 10 110 80 80 re f Q");
        Assert.Equal(153, Ink(s, "Cyan", 12, 112));
        Assert.Equal(102, Ink(s, "Yellow", 12, 112));
        Assert.Equal(255, Ink(s, "Black", 12, 112));
    }

    /// <summary>
    /// A plate decides overprint itself: asking for the sRGB overprint simulation changes no
    /// plate (it would read a plate's grey back as CMYK), and the page used has overprinting
    /// paints for it to act on.
    /// </summary>
    [Fact]
    public void TheOverprintSimulationOption_ChangesNoPlate()
    {
        byte[] pdf = Pdf(VectorPage);
        PdfSeparations plain = PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        PdfSeparations simulated = PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72,
            new PdfRenderOptions { SimulateOverprint = true });
        for (int i = 0; i < plain.Plates.Count; i++)
            Assert.Equal(plain.Plates[i].Tints, simulated.Plates[i].Tints);
        Assert.Equal(255, Ink(plain, "Yellow", 60, 60));
    }

    /// <summary>A plate starts with no ink whatever background the caller asks an ordinary render for — a transparent one included.</summary>
    [Fact]
    public void ATransparentBackgroundOption_LeavesBlankAreasWithNoInk()
    {
        using var ms = new MemoryStream(Pdf("0 0 0 1 k 10 10 20 20 re f"));
        PdfSeparations s = PdfSeparationRenderer.RenderPage(ms, 0, 72, new PdfRenderOptions { BackgroundArgb = 0 });
        Assert.Equal(0, Ink(s, "Black", 150, 150));
        Assert.Equal(0, Ink(s, "Cyan", 150, 150));
        Assert.Equal(255, Ink(s, "Black", 20, 20));
    }

    [Theory]
    [InlineData("/GSl gs 0 1 0 0 k 10 10 50 50 re f", "non-separable blend mode")]
    [InlineData("/GSt gs 0 1 0 0 k 10 10 50 50 re f", "transfer function")]
    public void WhatAPlateCannotExpress_IsCountedAndReported(string content, string reason)
    {
        var reports = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.SeparationApproximated && Environment.CurrentManagedThreadId == own)
                reports.Add(detail);
        }

        PdfSeparations s;
        RenderDiagnostics.OnEvent += Sink;
        try { s = Render(content); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.All(s.Plates, p => Assert.True(p.ApproximatedPaints > 0, p.Colorant));
        Assert.Contains(reports, r => r.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(255, Ink(s, "Magenta", 30, 30));
    }

    /// <summary>The facade and the renderer are one answer: the same plates, byte for byte — on a page whose plates differ from one another, so equal is not trivially equal.</summary>
    [Fact]
    public void TheFacade_AndTheRenderer_GiveTheSamePlates()
    {
        byte[] pdf = Pdf(VectorPage);
        PdfSeparations direct = PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        PdfSeparations facade = PdfOperations.RenderPageSeparations(new MemoryStream(pdf), 0, 72);

        Assert.Equal(direct.Plates.Select(p => p.Colorant), facade.Plates.Select(p => p.Colorant));
        for (int i = 0; i < direct.Plates.Count; i++)
            Assert.Equal(direct.Plates[i].Tints, facade.Plates[i].Tints);
        Assert.NotEqual(direct["Cyan"].Tints, direct["Black"].Tints);
    }

    /// <summary>The ordinary render of the same page is untouched by a separation render: colour, not plates.</summary>
    [Fact]
    public void AnOrdinaryRender_StillPaintsColour()
    {
        using var ms = new MemoryStream(Pdf("0 1 0 0 k 0 0 200 200 re f"));
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, 72);
        int i = 100 * page.Stride + 100 * 4;
        Assert.Equal((255, 0, 255), (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]));
    }

    // ------------------------------------------------------------------ fixture

    private static byte[] Pdf(string content)
    {
        string cell = "0 0 5 5 re f";
        string mask = "0.5 g 100 60 90 40 re f";
        string img = "\u0099\u0080fÿ\u0000ÿ\u0000\u0000";   // rich black, magenta
        string stencil = "@\u0080";                                   // 2x2: 0 1 / 1 0, 0 paints
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << " + Resources + " >> /Contents 4 0 R >>",
            Stream("", content),
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>",
            Stream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1 0 1]", "{ 0 0 3 -1 roll }"),
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 1 1 1] /N 1 >>",
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8", img),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ImageMask true", stencil),
            Stream("/Type /Pattern /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >>", cell),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>", mask),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace " + Spot1 + " /BitsPerComponent 8", "ÿ@"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "@ÿ"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace [/DeviceN [/Cyan /Spot2] /DeviceCMYK 6 0 R] /BitsPerComponent 16",
                   "ffæf\u0000\u0000@\u0000"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "ÿ\u0000\u0000"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /SMask 17 0 R", img),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "\u0080\u0080"),
            "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>",
            "<< /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0 1 0 0] /C1 [0 1 0 0] /N 1 >> >>",
            "<< /Type /Pattern /PatternType 2 /Shading 19 0 R >>",
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Decode [1 0 1 0 1 0 1 0]", img),
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string dict, string data) =>
        "<< " + dict + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";
}
