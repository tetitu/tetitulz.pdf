// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Soft proofing (batch 114, <see cref="PdfRenderOptions.ProofProfile"/>): the finished page
/// through an output profile's <c>B2A</c> and back through its <c>A2B</c>, on a 33-node grid — whole
/// pages, tiles and bands alike, never the plates. The profile is the synthetic CMYK of
/// <see cref="IccOutputTransformTests.SyntheticCmyk"/> (K from lightness in, lightness from K with a toe
/// out), whose two halves are each pinned on their own, so the proof's claim is the composition and
/// its plumbing; RSWOP, where the host has it, is the real-profile check.
/// </summary>
public class SoftProofTests
{
    private static byte[] ToeProfile() => SyntheticCmykProfile.Build(
        b2a: SyntheticCmykProfile.KFromLightness(),
        a2b: SyntheticCmykProfile.LightnessFromKWithToe());

    /// <summary>
    /// Black through the toe profile stays near black — both sides compensate, so the profile's
    /// L* 20 black lands on sRGB black as the reference renderer's proof has it — and the pixel is the exact
    /// composition's (black is a grid node). A dark grey below the profile's black (L* 20) shows the
    /// compensation switch: with it the grey keeps a step above black, without it the table clips it
    /// to full K and it comes back as black.
    /// </summary>
    [Fact]
    public void BlackThroughTheToeProfile_StaysNearBlack_AndTheGridMatchesTheExactComposition()
    {
        byte[] profile = ToeProfile();
        var options = new PdfRenderOptions { ProofProfile = profile };
        var uncompensated = new PdfRenderOptions { ProofProfile = profile, ProofBlackPointCompensation = false };

        var proofed = Centre(Render(FilledPage("0 g"), options));
        var plain = Centre(Render(FilledPage("0 g"), null));
        var grey = Centre(Render(FilledPage("0.1 g"), options));
        var greyWithout = Centre(Render(FilledPage("0.1 g"), uncompensated));

        Assert.Equal((0, 0, 0), plain);
        Assert.True(proofed.R < 30 && proofed.G < 30 && proofed.B < 30, $"the proofed black is {proofed}, not near black");
        var exact = SoftProof.TryCreate(profile, true, out _);
        Assert.True(exact.TryProofExactly(SKColors.Black, out SKColor e));
        Assert.True(Math.Abs(proofed.R - e.Red) <= 1 && Math.Abs(proofed.G - e.Green) <= 1 && Math.Abs(proofed.B - e.Blue) <= 1,
            $"grid {proofed} against exact ({e.Red}, {e.Green}, {e.Blue})");
        Assert.True(grey.R - greyWithout.R > 2, $"compensation changed nothing: dark grey {grey} with, {greyWithout} without");
    }

    /// <summary>A grey between grid nodes (100) is read off the grid by interpolation: within two levels of the exact composition, where the nearest node would be off by more.</summary>
    [Fact]
    public void AColourBetweenGridNodes_IsInterpolated()
    {
        byte[] profile = ToeProfile();
        var proofed = Centre(Render(FilledPage("0.392 g"), new PdfRenderOptions { ProofProfile = profile }));

        var exact = SoftProof.TryCreate(profile, true, out _);
        Assert.True(exact.TryProofExactly(new SKColor(100, 100, 100), out SKColor e));
        Assert.True(Math.Abs(proofed.R - e.Red) <= 2, $"grid {proofed} against exact ({e.Red}, {e.Green}, {e.Blue})");
        // The nearest node's answer, for scale: it must be further off than the interpolation.
        Assert.True(exact.TryProofExactly(new SKColor(SoftProof.Node(13), SoftProof.Node(13), SoftProof.Node(13)), out SKColor node));
        Assert.True(Math.Abs(node.Red - e.Red) > 2, $"the fixture cannot tell interpolation from the nearest node ({node.Red} vs {e.Red})");
    }

    /// <summary>White stays exactly white, and a transparent background keeps its alpha.</summary>
    [Fact]
    public void WhiteStaysWhite_AndATransparentBackgroundKeepsItsAlpha()
    {
        byte[] profile = ToeProfile();
        var white = Render(FilledPage("1 g"), new PdfRenderOptions { ProofProfile = profile });
        Assert.Equal((255, 255, 255), Centre(white));

        var transparent = Render(EmptyPage(), new PdfRenderOptions { ProofProfile = profile, BackgroundArgb = 0 });
        int i = (transparent.Height / 2) * transparent.Stride + (transparent.Width / 2) * 4;
        Assert.Equal(0, transparent.Pixels[i + 3]);
    }

    /// <summary>Bytes that are not a profile, and a profile with no <c>B2A</c>, are reported once and the page rendered unproofed — byte for byte the ordinary render.</summary>
    [Fact]
    public void AnUnusableProfile_IsReported_AndThePageRenderedUnproofed()
    {
        var kinds = new List<RenderDiagnosticKind>();
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex) { lock (kinds) kinds.Add(kind); }
        RenderDiagnostics.OnEvent += Sink;
        RenderedPage proofed, plain;
        try
        {
            proofed = Render(FilledPage("0 g"), new PdfRenderOptions { ProofProfile = new byte[300] });
            plain = Render(FilledPage("0 g"), null);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Equal(1, kinds.Count(k => k == RenderDiagnosticKind.ProofProfileUnsupported));
        Assert.Equal(plain.Pixels, proofed.Pixels);
    }

    /// <summary>Bands and the whole page are proofed the same: two bands reassembled are the page, proofed and unproofed alike — and the grid is built once for the session.</summary>
    [Fact]
    public void BandsAgreeWithThePage_AndShareOneGrid()
    {
        byte[] pdf = FilledPage("0.2 0.6 0.9 rg");
        var options = new PdfRenderOptions { ProofProfile = ToeProfile() };
        var page = Render(pdf, options);

        SoftProof.GridBuilds = 0;
        var rows = new byte[page.Height][];
        using (var session = PdfRenderSession.Open(new MemoryStream(pdf), options))
        {
            int y = 0;
            session.RenderPageBands(0, 72, page.Height / 2, (_, band) =>
            {
                for (int r = 0; r < band.Height; r++, y++)
                    rows[y] = band.Pixels.Skip(r * band.Stride).Take(band.Width * 4).ToArray();
            });
            Assert.Equal(page.Height, y);
        }
        int builds = SoftProof.GridBuilds;

        for (int y = 0; y < page.Height; y++)
            Assert.Equal(page.Pixels.Skip(y * page.Stride).Take(page.Width * 4).ToArray(), rows[y]);
        Assert.True(builds <= 1, $"the grid was built {builds} times for one session");
        var proofedCentre = Centre(page);
        Assert.NotEqual(Centre(Render(pdf, null)), proofedCentre);
    }

    /// <summary>The separation plates are never proofed: the same tints with and without a proof profile.</summary>
    [Fact]
    public void ThePlates_AreNotProofed()
    {
        byte[] pdf = FilledPage("0.2 0.6 0.9 rg");
        var with = PdfOperations.RenderPageSeparations(new MemoryStream(pdf), 0, 72, new PdfRenderOptions { ProofProfile = ToeProfile() });
        var without = PdfOperations.RenderPageSeparations(new MemoryStream(pdf), 0, 72, null);

        Assert.Equal(without.Plates.Count, with.Plates.Count);
        for (int i = 0; i < with.Plates.Count; i++) Assert.Equal(without.Plates[i].Tints, with.Plates[i].Tints);
        Assert.Contains(with.Plates, p => p.Tints.Any(t => t > 0));
    }

    /// <summary>
    /// The overprint preview through a CMYK proof profile is the press's: its plates — cyan over magenta, both full —
    /// come back through the profile's own <c>A2B</c> table, not the composite's conversion proofed afterwards.
    /// </summary>
    [Fact]
    public void TheOverprintPreview_IsThePressesInks()
    {
        byte[] pdf = OverprintingPage();
        var options = new PdfRenderOptions { SimulateOverprint = true, ProofProfile = ToeProfile() };
        int builds = PressProof.GridBuilds;
        var pressed = Centre(Render(pdf, options));
        Centre(Render(pdf, options));
        Assert.Equal(0, PressProof.GridBuilds - builds);   // two inks: through the table, no grid needed

        var press = PressProof.For(options);
        Assert.NotNull(press);
        Assert.Same(press, PressProof.For(options));       // one press for the options, every render
        SKColor inks = press.Exactly(255, 255, 0, 0);   // a grid node, so the grid reads it exactly
        Assert.Equal((inks.Red, inks.Green, inks.Blue), ((byte)pressed.R, (byte)pressed.G, (byte)pressed.B));

        // The old route — the naive recombination (blue), proofed — is a different colour.
        var plain = Centre(Render(pdf, new PdfRenderOptions { SimulateOverprint = true }));
        Assert.True(SoftProof.TryCreate(ToeProfile(), true, out _).TryProofExactly(new SKColor((byte)plain.R, (byte)plain.G, (byte)plain.B), out SKColor old));
        Assert.NotEqual((old.Red, old.Green, old.Blue), (inks.Red, inks.Green, inks.Blue));

        // A tile of the preview is the page's.
        byte[] tile = PdfOperations.RenderPageTile(new MemoryStream(pdf), 0, 10, 10, 20, 20, 72, options);
        using var bitmap = SKBitmap.Decode(tile);
        SKColor t = bitmap.GetPixel(10, 10);
        Assert.Equal((inks.Red, inks.Green, inks.Blue), (t.Red, t.Green, t.Blue));
    }

    /// <summary>
    /// The SVG writer is proofed as the raster is: a path's colour through the exact composition, the background's
    /// white staying the paper, and nothing changed without a proof profile.
    /// </summary>
    [Fact]
    public void TheSvg_IsProofed_AsTheRasterIs()
    {
        byte[] pdf = FilledPage("0.2 0.6 0.9 rg");
        string plain = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        Assert.Equal(plain, PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, new PdfRenderOptions()));

        string proofed = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, new PdfRenderOptions { ProofProfile = ToeProfile() });
        string before = PathFill(plain), after = PathFill(proofed);
        Assert.True(PdfColor.TryParseCssColor(before, out SKColor colour));
        Assert.True(SoftProof.TryCreate(ToeProfile(), true, out _).TryProofExactly(colour, out SKColor exact));
        Assert.Equal(PdfColor.ToCssRgb(exact), after);
        Assert.NotEqual(before, after);

        // The raster proof reads the same composition off its grid.
        var raster = Centre(Render(pdf, new PdfRenderOptions { ProofProfile = ToeProfile() }));
        Assert.True(Math.Abs(raster.R - exact.Red) <= 2 && Math.Abs(raster.G - exact.Green) <= 2 && Math.Abs(raster.B - exact.Blue) <= 2);

        var background = XDocument.Parse(proofed).Descendants().First(e => e.Name.LocalName == "rect");
        Assert.Equal("rgb(255,255,255)", background.Attribute("fill")!.Value);
    }

    /// <summary>
    /// An image is proofed through the grid; a mask — here the knockout that keeps a translucent fill out from
    /// under its stroke, white fill and black stroke, which is coverage, not colour — is left as it was.
    /// </summary>
    [Fact]
    public void TheSvg_ProofsImages_AndLeavesMasksAlone()
    {
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Resources << /XObject << /Im 5 0 R >> /ExtGState << /Half << /ca 0.5 /CA 0.5 >> >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("q 20 0 0 20 0 0 cm /Im Do Q /Half gs 0.2 0.6 0.9 rg 1 0 0 RG 4 w 22 22 12 12 re B")),
            HalftoneTests.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8",
                new byte[] { 200, 40, 40, 30, 160, 90 }));
        var options = new PdfRenderOptions { ProofProfile = ToeProfile() };
        var plain = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
        var proofed = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, options));

        string Masks(XDocument d) => string.Concat(d.Descendants().Where(e => e.Name.LocalName == "mask").Select(e => e.ToString()));
        Assert.Contains("stroke=\"black\"", Masks(plain));   // the knockout is a mask
        Assert.Equal(Masks(plain), Masks(proofed));

        SKColor First(XDocument d)
        {
            var image = d.Descendants().First(e => e.Name.LocalName == "image" && !e.Ancestors().Any(a => a.Name.LocalName == "mask"));
            string href = (image.Attribute("href") ?? image.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))!.Value;
            using var bitmap = SKBitmap.Decode(Convert.FromBase64String(href.Substring(href.IndexOf(',') + 1)));
            return bitmap.GetPixel(0, 0);
        }
        SKColor before = First(plain), after = First(proofed);
        using var probe = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        probe.SetPixel(0, 0, before);
        SoftProof.TryCreate(ToeProfile(), true, out _).Apply(probe);
        Assert.Equal(probe.GetPixel(0, 0), after);
        Assert.NotEqual(before, after);
    }

    private static string PathFill(string svg)
        => XDocument.Parse(svg).Descendants().First(e => e.Name.LocalName == "path" && e.Attribute("fill") != null).Attribute("fill")!.Value;

    /// <summary>Through RSWOP, cyan overprinting magenta previews as the press's blue — its table's, read off the grid.</summary>
    [RealIccProfileFact("RSWOP.icm")]
    public void ThroughRswop_TheOverprintPreviewIsThePressesBlue()
    {
        var options = new PdfRenderOptions { SimulateOverprint = true, ProofProfile = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm")) };
        var pressed = Centre(Render(OverprintingPage(), options));
        var press = PressProof.For(options);
        SKColor exact = press.Exactly(255, 255, 0, 0);
        Assert.Equal((exact.Red, exact.Green, exact.Blue), ((byte)pressed.R, (byte)pressed.G, (byte)pressed.B));
        Assert.True(exact.Blue > exact.Red + 40 && exact.Blue > exact.Green + 40, $"C+M on SWOP is a blue, not {exact}");

        // A render's session is the table itself while its colours are few; the grid alone, past them, stays
        // within the eleven levels measured on these 2 000 colours (at a saturated red, where sRGB clips).
        var random = new Random(11);
        var session = press.Session();
        var gridOnly = press.Session(maxExact: 0);
        int worst = 0;
        string at = "";
        for (int i = 0; i < 2000; i++)
        {
            byte c = (byte)random.Next(256), m = (byte)random.Next(256), y = (byte)random.Next(256), k = (byte)random.Next(256);
            SKColor table = press.Exactly(c, m, y, k), grid = gridOnly(c, m, y, k);
            Assert.Equal(table, session(c, m, y, k));
            int d = Math.Max(Math.Abs(grid.Red - table.Red), Math.Max(Math.Abs(grid.Green - table.Green), Math.Abs(grid.Blue - table.Blue)));
            if (d > worst) { worst = d; at = $"({c}, {m}, {y}, {k}): grid {grid}, table {table}"; }
        }
        Assert.True(worst <= 11, $"worst {worst} at {at}");
        Assert.True(worst > 0, "the grid-only session read the table");   // it is the grid
        Assert.Equal(SKColors.White, press.ToSrgb(0, 0, 0, 0));
    }

    /// <summary>The press grid between its nodes, on the synthetic profile — present on every machine.</summary>
    [Fact]
    public void ThePressGrid_FollowsItsTableBetweenNodes()
    {
        var press = PressProof.For(new PdfRenderOptions { ProofProfile = ToeProfile() });
        int builds = PressProof.GridBuilds;
        Assert.Equal(SKColors.White, press.ToSrgb(0, 0, 0, 0));
        press.ToSrgb(1, 2, 3, 4);
        Assert.Equal(1, PressProof.GridBuilds - builds);   // sampled once, on first use
        foreach (int k in new[] { 9, 40, 101, 177, 230 })
        {
            SKColor grid = press.ToSrgb(0, 0, 0, (byte)k), table = press.Exactly(0, 0, 0, (byte)k);
            Assert.True(Math.Abs(grid.Red - table.Red) <= 3, $"K {k}: grid {grid}, table {table}");
        }
        // The table answers black's ink, so the ramp darkens: a grid reading the wrong corner would not.
        Assert.True(press.ToSrgb(0, 0, 0, 230).Red < press.ToSrgb(0, 0, 0, 40).Red - 60);
    }

    /// <summary>Through RSWOP, sRGB green is outside the press gamut and comes back duller; white stays white; the pixel is the exact composition's.</summary>
    [RealIccProfileFact("RSWOP.icm")]
    public void ThroughRswop_GreenIsPulledIntoGamut()
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm"));
        var options = new PdfRenderOptions { ProofProfile = profile };

        var green = Centre(Render(FilledPage("0 1 0 rg"), options));
        var exact = SoftProof.TryCreate(profile, true, out _);
        Assert.True(exact.TryProofExactly(new SKColor(0, 255, 0), out SKColor e));
        Assert.True(green.G < 240 || green.R > 30, $"green came back as {green}, inside no press gamut");
        Assert.True(Math.Abs(green.R - e.Red) <= 1 && Math.Abs(green.G - e.Green) <= 1 && Math.Abs(green.B - e.Blue) <= 1,
            $"grid {green} against exact ({e.Red}, {e.Green}, {e.Blue})");
        Assert.Equal((255, 255, 255), Centre(Render(FilledPage("1 g"), options)));
    }

    // ------------------------------------------------------------------

    private static RenderedPage Render(byte[] pdf, PdfRenderOptions options)
        => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72, options);

    private static (int R, int G, int B) Centre(RenderedPage page)
    {
        int i = (page.Height / 2) * page.Stride + (page.Width / 2) * 4;
        return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
    }

    /// <summary>A 40×40pt page filled edge to edge in <paramref name="colour"/> (a colour operator and its operands).</summary>
    private static byte[] FilledPage(string colour) => Build(colour + " 0 0 40 40 re f");

    private static byte[] EmptyPage() => Build("");

    /// <summary>A page whose one fill overprints (<c>/OP true /OPM 1</c>) in cyan over a magenta fill, so the overprint preview takes the plates' route.</summary>
    private static byte[] OverprintingPage() => Build("0 1 0 0 k 0 0 40 40 re f /GS gs 1 0 0 0 k 0 0 40 40 re f",
        "/ExtGState << /GS << /Type /ExtGState /OP true /op true /OPM 1 >> >>");

    private static byte[] Build(string content, string resources = "")
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Contents 4 0 R /Resources << " + resources + " >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
