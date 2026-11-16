// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
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
/// Spot colours drawn through an ICC named-colour profile (<see cref="PdfRenderOptions.NamedColorProfile"/>):
/// the colour the profile names, a tint the straight line in Lab from white.
/// </summary>
/// <remarks>
/// The pinned colours are the reference renderer's — <c>png16m</c> at 72 dpi with <c>-sNamedProfile</c> a table naming
/// <c>SPOT 485 C</c> at L* 47, a* 69, b* 52, the colour <see cref="NamedColorProfile"/> builds — for tints 1,
/// 0.5 and 0.25; its full tint is the second CMM's sRGB for that Lab within a level. The reference renderer's white is
/// <c>(65535, 32767, 32767)</c> in the reference colour engine's 16-bit Lab encoding — a* and b* −0.5, not 0 — so its line starts a
/// shade off white, and a light tint lands up to three levels from this one.
/// </remarks>
public class NamedSpotColorTests
{
    private static readonly (float Tint, int R, int G, int B)[] Reference =
    {
        (1f, 216, 23, 28), (0.5f, 248, 155, 135), (0.25f, 252, 205, 194),
    };

    private static PdfRenderOptions Named() => new PdfRenderOptions { NamedColorProfile = NamedColorProfile.Build() };

    [Fact]
    public void ASpotTheProfileNames_IsItsColour_TintsOnTheLabLineFromWhite()
    {
        byte[] pdf = BandsPage("SPOT#20485#20C");
        var named = Pixels(pdf, Named());
        var plain = Pixels(pdf, null);

        foreach (var (tint, r, g, b) in Reference)
        {
            var (pr, pg, pb) = Band(named, tint);
            Assert.True(Math.Abs(pr - r) <= 3 && Math.Abs(pg - g) <= 3 && Math.Abs(pb - b) <= 3,
                $"tint {tint}: ({pr}, {pg}, {pb}), reference ({r}, {g}, {b})");

            // The rule exactly: white + t × (solid − white) in Lab, to sRGB.
            SKColor rule = PdfColor.LabToSRgb(100 + tint * (47f - 100), tint * 69f, tint * 52f);
            Assert.Equal((rule.Red, rule.Green, rule.Blue), ((byte)pr, (byte)pg, (byte)pb));
        }
        Assert.Equal((255, 255, 255), Band(named, 0f));

        // The control: without the profile the file's own transform, to a CMYK blue.
        var (cr, cg, cb) = Band(plain, 1f);
        Assert.True(cb > cr + 60, $"the file's own transform paints blue, not ({cr}, {cg}, {cb})");
    }

    [Fact]
    public void TheSvg_PaintsTheSameColours()
    {
        byte[] pdf = BandsPage("SPOT#20485#20C");
        var raster = Pixels(pdf, Named());
        var fills = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, Named()))
            .Descendants().Where(e => e.Name.LocalName == "path" && e.Attribute("fill") != null)
            .Select(e => e.Attribute("fill")!.Value).ToList();
        Assert.Equal(4, fills.Count);
        float[] tints = { 1f, 0.5f, 0.25f, 0f };
        for (int i = 0; i < 4; i++)
        {
            Assert.True(PdfColor.TryParseCssColor(fills[i], out SKColor svg));
            Assert.Equal(Band(raster, tints[i]), (svg.Red, svg.Green, svg.Blue));
        }
    }

    [Theory]
    [InlineData("spot#20485#20c")]   // the full name ignoring case
    public void TheFullName_IsMatchedIgnoringCase(string colorant)
        => Assert.Equal(Band(Pixels(BandsPage("SPOT#20485#20C"), Named()), 1f), Band(Pixels(BandsPage(colorant), Named()), 1f));

    [Theory]
    [InlineData("485")]     // a root alone is not a spot's name
    [InlineData("Black")]   // nor is a process colorant, though the profile has a root of that name
    [InlineData("SPOT#20999#20C")]
    public void OtherColorants_KeepTheFilesTransform(string colorant)
        => Assert.Equal(Band(Pixels(BandsPage(colorant), null), 1f), Band(Pixels(BandsPage(colorant), Named()), 1f));

    [Fact]
    public void AProcessColorant_KeepsTheFilesTransform_EvenWhenTheProfileNamesItInFull()
    {
        // A profile with no prefix or suffix: each root is a full name, "Black" among them.
        var bare = new PdfRenderOptions { NamedColorProfile = NamedColorProfile.Build(bareNames: true) };
        var (r, g, b) = Band(Pixels(BandsPage("485"), bare), 1f);
        SKColor solid = PdfColor.LabToSRgb(47, 69, 52);
        Assert.Equal((solid.Red, solid.Green, solid.Blue), ((byte)r, (byte)g, (byte)b));   // the profile is potent

        Assert.Equal(Band(Pixels(BandsPage("Black"), null), 1f), Band(Pixels(BandsPage("Black"), bare), 1f));
    }

    [Fact]
    public void ImagesAndShadings_TakeTheNamedColourToo()
    {
        // A two-sample Separation image (tints 1 and 0.5) and an axial shading from 0 to 1 across the page's
        // lower half, in the same space.
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << /ColorSpace << /Cs1 6 0 R >> /XObject << /Im 7 0 R >> /Shading << /Sh 8 0 R >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("q 80 0 0 20 0 20 cm /Im Do Q q 0 0 80 20 re W n /Sh sh Q")),
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 1 0 0] /N 1 >>",
            "[/Separation /SPOT#20485#20C /DeviceCMYK 5 0 R]",
            HalftoneTests.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace 6 0 R /BitsPerComponent 8", new byte[] { 255, 128 }),
            "<< /ShadingType 2 /ColorSpace 6 0 R /Coords [0 0 80 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /Extend [true true] >>");
        var page = Pixels(pdf, Named());
        SKColor solid = PdfColor.LabToSRgb(47, 69, 52);
        SKColor half = PdfColor.LabToSRgb(100 + 128 / 255f * (47 - 100), 128 / 255f * 69, 128 / 255f * 52);
        Assert.Equal((solid.Red, solid.Green, solid.Blue), At(page, 20, 10));    // the image's first sample
        Assert.Equal((half.Red, half.Green, half.Blue), At(page, 60, 10));       // its second
        // The shading at its last pixel's centre, tint 79.5 / 80, and at its first's, 0.5 / 80.
        foreach (int x in new[] { 79, 0 })
        {
            float t = (x + 0.5f) / 80;
            SKColor rule = PdfColor.LabToSRgb(100 + t * (47 - 100), t * 69, t * 52);
            var at = At(page, x, 30);
            Assert.True(Math.Abs(at.R - rule.Red) <= 2 && Math.Abs(at.G - rule.Green) <= 2 && Math.Abs(at.B - rule.Blue) <= 2,
                $"shading at {x}: {at}, the rule ({rule.Red}, {rule.Green}, {rule.Blue})");
        }
        // The file's own transform would paint the end blue.
        Assert.True(At(page, 79, 30).R > 200);
    }

    [Fact]
    public void AnIndexedBase_AndAPatternsUnderlyingSpace_TakeTheNamedColour()
    {
        // [/Indexed spot 1 <FF 80>] painting index 0 then 1; an uncoloured tiling pattern painted in the spot at 1.
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 60 20] /Resources << /ColorSpace << /Ix [/Indexed 6 0 R 1 <FF80>] /Pt [/Pattern 6 0 R] >> /Pattern << /P 7 0 R >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("/Ix cs 0 sc 0 0 20 20 re f 1 sc 20 0 20 20 re f /Pt cs 1 /P scn 40 0 20 20 re f")),
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 1 0 0] /N 1 >>",
            "[/Separation /SPOT#20485#20C /DeviceCMYK 5 0 R]",
            HalftoneTests.Stream("/Type /Pattern /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>",
                Encoding.ASCII.GetBytes("0 0 4 4 re f")));
        var page = Pixels(pdf, Named());
        SKColor solid = PdfColor.LabToSRgb(47, 69, 52);
        SKColor half = PdfColor.LabToSRgb(100 + 128 / 255f * (47 - 100), 128 / 255f * 69, 128 / 255f * 52);
        Assert.Equal((solid.Red, solid.Green, solid.Blue), At(page, 10, 10));
        Assert.Equal((half.Red, half.Green, half.Blue), At(page, 30, 10));
        Assert.Equal((solid.Red, solid.Green, solid.Blue), At(page, 50, 10));
    }

    [Fact]
    public void ThePsd_ShowsTheSpotChannelInTheNamedColour()
    {
        var separations = PdfSeparationRenderer.RenderPage(new MemoryStream(BandsPage("SPOT#20485#20C")), 0, 72, Named());
        SKColor solid = PdfColor.LabToSRgb(47, 69, 52);
        Assert.Equal(solid, separations["SPOT 485 C"].Display);
        // The plate itself is ink: the same with the profile and without it.
        Assert.Equal(PdfSeparationRenderer.RenderPage(new MemoryStream(BandsPage("SPOT#20485#20C")), 0, 72).Plates.Select(p => p.Tints),
                     separations.Plates.Select(p => p.Tints));
    }

    [Fact]
    public void AnUnreadableProfile_IsReported_AndTheSpotsKeepTheirTransforms()
    {
        byte[] pdf = BandsPage("SPOT#20485#20C");
        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == thread && kind == RenderDiagnosticKind.NamedColorProfileUnsupported)
                lock (reports) reports.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        RenderedPage page;
        try { page = Pixels(pdf, new PdfRenderOptions { NamedColorProfile = NamedColorProfile.Build(deviceClass: "prtr") }); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Contains(reports, r => r.Contains("not a named-colour profile", StringComparison.Ordinal));
        Assert.Equal(Band(Pixels(pdf, null), 1f), Band(page, 1f));
        Assert.True(new PdfRenderOptions { NamedColorProfile = new byte[1] }.RequestsAnythingAPluginCannotHonour());
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Four 20-point bands of one Separation, tints 1, 0.5, 0.25 and 0; its own transform a CMYK blue.</summary>
    private static byte[] BandsPage(string colorant) => HalftoneTests.Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 20] /Resources << /ColorSpace << /Cs1 [/Separation /{colorant} /DeviceCMYK 5 0 R] >> >> /Contents 4 0 R >>",
        HalftoneTests.Stream("", Encoding.ASCII.GetBytes("/Cs1 cs 1 scn 0 0 20 20 re f 0.5 scn 20 0 20 20 re f 0.25 scn 40 0 20 20 re f 0 scn 60 0 20 20 re f")),
        "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 1 0 0] /N 1 >>");

    private static RenderedPage Pixels(byte[] pdf, PdfRenderOptions options)
        => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72, options);

    private static (int R, int G, int B) Band(RenderedPage page, float tint)
        => At(page, tint switch { 1f => 10, 0.5f => 30, 0.25f => 50, _ => 70 }, 10);

    private static (int R, int G, int B) At(RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
    }
}
