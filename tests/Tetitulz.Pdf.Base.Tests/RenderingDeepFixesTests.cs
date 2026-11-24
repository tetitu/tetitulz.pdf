// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the second-pass rendering fixes:
///  • Mesh shading Type 4 — strip (flag=1) and fan (flag=2) reuse previous triangle vertices
///    rather than collapsing into the same a/b/c as flag=0.
///  • Radial shading Type 3 — radius is scaled by the CTM (axis-averaged with elliptical
///    local matrix when the CTM is non-uniform), not by DpiScale.
///  • SVG renderer — Form XObjects whose /Group dict has /S /Transparency are wrapped
///    in `<g style="isolation:isolate">` so opacity / blend collapses onto a single
///    composite step (PDF spec §11.6.6).
/// </summary>
public sealed class RenderingDeepFixesTests
{
    // ------------------------------------------------------------------
    //  Mesh shading Type 4 — strip / fan flag handling
    // ------------------------------------------------------------------

    // The fixture's mesh is a 612×792 page.  Vertex coordinates decode over
    // /Decode [0 612 0 792 …], so the flag-0 triangle is (0,0) red, (612,0) green,
    // (0,792) blue — the half-page below the diagonal.  Everything above that diagonal
    // is untouched white unless a strip/fan vertex adds a second triangle.
    //
    //   InsideFirstTriangle  — painted by the flag-0 triangle in every variant.
    //   AboveDiagonal        — only the flag-1 STRIP triangle reaches here.
    //   FanCentroid          — inside the flag-0 triangle, but repainted by the flag-2 FAN
    //                          triangle, whose third vertex is white.
    private static readonly (float X, float Y) InsideFirstTriangle = (100f, 91f);
    private static readonly (float X, float Y) AboveDiagonal       = (500f, 691f);
    private static readonly (float X, float Y) FanCentroid         = (102f, 395f);

    // Gouraud colour of the flag-0 triangle at FanCentroid: barycentric (0.331, 0.168, 0.501)
    // over red / green / blue.
    private static readonly SKColor FlagZeroColorAtFanCentroid = new(85, 43, 127);
    // …and with the fan triangle on top: the mean of red, blue and the fan's white vertex.
    private static readonly SKColor FanColorAtFanCentroid = new(170, 85, 170);

    private static SKColor At(SKBitmap bmp, (float X, float Y) p) => PixelAssert.At(bmp, p.X, p.Y);

    private static bool IsUnpainted(SKColor c) => c.Red > 245 && c.Green > 245 && c.Blue > 245;

    [Fact]
    public void MeshShading_Type4_StripFlag_DrawsTheSecondTriangle()
    {
        // 4 vertices: 3 with flag 0 (initial triangle) then 1 with flag 1 (strip).
        // The flag is read from the *first* vertex of each triangle, and (b,c) of the previous
        // triangle reused for flag 1 — collapsing flags 1 and 2 onto the flag-0 layout would make
        // the fourth vertex start a triangle that never gets its remaining two vertices, so
        // nothing would be drawn for it.
        using var strip    = PixelAssert.Render(BuildPdfWithType4Mesh(includeStrip: true,  includeFan: false));
        using var flagZero = PixelAssert.Render(BuildPdfWithType4Mesh(includeStrip: false, includeFan: false));

        // The strip triangle is (612,0) green, (0,792) blue, (612,792) yellow — the half of
        // the page the flag-0 triangle does not cover.
        SKColor painted = At(strip, AboveDiagonal);
        Assert.True(PixelAssert.Near(painted, new SKColor(176, 209, 46), tolerance: 40),
            $"the flag-1 strip triangle should paint above the diagonal; got {painted}.");

        // Same point with the strip vertex absent: nothing is drawn there at all.
        Assert.True(IsUnpainted(At(flagZero, AboveDiagonal)),
            "without the strip vertex this point is outside every triangle.");

        // The first triangle is unaffected by the strip.
        Assert.True(PixelAssert.Near(At(strip, InsideFirstTriangle),
                                     At(flagZero, InsideFirstTriangle), tolerance: 2),
            "the flag-0 triangle must render identically with or without the strip vertex.");
    }

    [Fact]
    public void MeshShading_Type4_FanFlag_DrawsTheFanTriangle()
    {
        // Triangle fan: flag=2 reuses (a, c) of the previous triangle, giving
        // (0,0) red, (0,792) blue, (307.7,398.1) white.  That triangle is almost exactly
        // co-located with the flag-0 one, so it is invisible by coverage — but its colours
        // differ, and it is painted second.
        using var fan      = PixelAssert.Render(BuildPdfWithType4Mesh(includeStrip: false, includeFan: true));
        using var flagZero = PixelAssert.Render(BuildPdfWithType4Mesh(includeStrip: false, includeFan: false));

        SKColor withFan = At(fan, FanCentroid);
        Assert.True(PixelAssert.Near(withFan, FanColorAtFanCentroid, tolerance: 25),
            $"the flag-2 fan triangle should repaint the centroid; got {withFan}.");
        Assert.True(PixelAssert.Near(At(flagZero, FanCentroid), FlagZeroColorAtFanCentroid, tolerance: 25),
            "baseline: without the fan vertex the flag-0 Gouraud colour shows through.");

        // Reusing (a,c) — not (b,c) — means the fan does NOT reach the strip's half-page.
        Assert.True(IsUnpainted(At(fan, AboveDiagonal)),
            "a fan vertex must not produce the strip triangle.");
    }

    [Fact]
    public void MeshShading_Type4_OnlyFlagZero_PaintsTheGouraudTriangle_Regression()
    {
        // The simple "all flag 0" case must still work after the loop refactor: one
        // Gouraud triangle, interpolated across its three vertex colours, and nothing outside it.
        using var bmp = PixelAssert.Render(BuildPdfWithType4Mesh(includeStrip: false, includeFan: false));

        // Close to the (0,0) red vertex.
        SKColor nearRed = PixelAssert.At(bmp, 30f, 29f);
        Assert.True(nearRed.Red > 200 && nearRed.Green < 60 && nearRed.Blue < 60,
            $"the corner nearest the red vertex should be red; got {nearRed}.");

        // Interior: the exact barycentric blend of the three vertex colours.
        Assert.True(PixelAssert.Near(At(bmp, FanCentroid), FlagZeroColorAtFanCentroid, tolerance: 25),
            $"interior should carry the interpolated colour; got {At(bmp, FanCentroid)}.");

        // Outside the triangle nothing is painted — proof the mesh is a triangle, not a page flood.
        Assert.True(IsUnpainted(At(bmp, AboveDiagonal)),
            "the far side of the diagonal is outside the only triangle.");
    }

    // ------------------------------------------------------------------
    //  Radial shading Type 3 — CTM scale, not DpiScale
    // ------------------------------------------------------------------

    [Fact]
    public void RadialShading_UniformCtmScale_ScalesTheRadiusByTheCtm()
    {
        // /Coords [200 200 0 200 200 100] under a uniform 2× scale is a disc centred at user
        // (400,400) with radius 200, running red (centre) → blue (edge), /Extend defaulting to
        // false so nothing is painted beyond it.  The old DpiScale-based code left the radius at
        // 100 device units, which the 150-unit probe below is placed to catch.
        using var bmp = PixelAssert.Render(BuildPdfWithRadialShading(ctm: "2 0 0 2 0 0"));

        SKColor centre = PixelAssert.At(bmp, 400f, 399f);
        Assert.True(centre.Red > 200 && centre.Blue < 60,
            $"t=0 at the centre should be the /C0 red; got {centre}.");

        SKColor at150 = PixelAssert.At(bmp, 550f, 399f);
        Assert.False(IsUnpainted(at150),
            $"radius must be CTM-scaled to 200, so 150 units out is still inside; got {at150}.");
        Assert.True(at150.Blue > at150.Red,
            $"three-quarters of the way out the gradient should be past the midpoint; got {at150}.");

        Assert.True(IsUnpainted(PixelAssert.At(bmp, 400f, 649f)),
            "250 units out is beyond r=200 and /Extend is false, so it must stay unpainted.");
    }

    [Fact]
    public void RadialShading_NonUniformCtmScale_RendersAsEllipse()
    {
        // Non-uniform CTM (3× X, 1× Y) — the circular shading must come out as an ellipse with
        // semi-axes 300 × 100 centred at user (600,200).  The two probes are the whole point:
        // 250 units along X is inside, 150 units along Y is outside.  A circle of either radius
        // fails one of them, which is exactly what uniform scaling would produce.
        using var bmp = PixelAssert.Render(BuildPdfWithRadialShading(ctm: "3 0 0 1 0 0"));

        SKColor centre = PixelAssert.At(bmp, 600f, 199f);
        Assert.True(centre.Red > 200 && centre.Blue < 60,
            $"t=0 at the ellipse centre should be the /C0 red; got {centre}.");

        Assert.False(IsUnpainted(PixelAssert.At(bmp, 350f, 199f)),
            "250 units along the 300-unit X semi-axis must be painted.");
        Assert.True(IsUnpainted(PixelAssert.At(bmp, 600f, 349f)),
            "150 units along the 100-unit Y semi-axis must be outside the ellipse.");
    }

    [Fact]
    public void RadialShading_RotatedCtm_PaintsTheDiscAtTheRotatedCentre()
    {
        // 45° rotation: CTM = [cos sin -sin cos 0 0]; scaleX == scaleY == 1, so the disc keeps
        // radius 100 while its centre moves from (200,200) to (0, 282.8).
        using var bmp = PixelAssert.Render(
            BuildPdfWithRadialShading(ctm: "0.7071 0.7071 -0.7071 0.7071 0 0"));

        SKColor centre = PixelAssert.At(bmp, 0f, 282f);
        Assert.True(centre.Red > 200 && centre.Blue < 60,
            $"the rotated centre should be the /C0 red; got {centre}.");

        SKColor half = PixelAssert.At(bmp, 50f, 282f);
        Assert.True(PixelAssert.Near(half, new SKColor(128, 0, 128), tolerance: 30),
            $"half-way out, t≈0.5 gives an even red/blue mix; got {half}.");

        Assert.True(IsUnpainted(PixelAssert.At(bmp, 150f, 282f)),
            "the radius must stay 100 under a pure rotation, so 150 units out is unpainted.");
        // The un-rotated centre is 200 units from the rotated one — outside the disc.
        Assert.True(IsUnpainted(PixelAssert.At(bmp, 200f, 199f)),
            "the disc must move with the CTM rather than staying at the /Coords centre.");
    }

    // ------------------------------------------------------------------
    //  SVG renderer — Form XObject /Group transparency isolation
    // ------------------------------------------------------------------

    [Fact]
    public void Svg_IsolatedTransparencyGroup_EmitsIsolationWrapper()
    {
        // /Group << /S /Transparency /I true >> must render inside a <g> tagged
        // `isolation:isolate`, so its contents composite as a single image rather than
        // blending each element with the page.
        byte[] pdf = BuildPdfWithTransparencyGroupForm(isolated: true);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("isolation:isolate", svg);
    }

    /// <summary>
    /// <c>/I</c> defaults to <b>false</b>, so emitting the isolation wrapper anyway would make every
    /// exported group isolated. SVG's own default for a group is non-isolated, so the property is
    /// emitted only when asked. The fixture below never says <c>/I true</c>.
    /// </summary>
    [Fact]
    public void Svg_NonIsolatedTransparencyGroup_OmitsIsolationWrapper()
    {
        byte[] pdf = BuildPdfWithTransparencyGroupForm(isolated: false);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.DoesNotContain("isolation:isolate", svg);
    }

    [Fact]
    public void Svg_FormXObjectWithoutGroup_DoesNotEmitIsolationWrapper()
    {
        // A plain Form XObject (no /Group entry) must NOT emit an isolation wrapper —
        // that would create a stacking context where none is required.
        byte[] pdf = BuildPdfWithPlainForm();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.DoesNotContain("isolation:isolate", svg);
    }

    [Fact]
    public void Svg_TransparencyGroupWithCa_EmitsOpacityOnWrapper()
    {
        // When the parent state has CA=0.5 (set via gs /CA 0.5), the wrapper should
        // carry an opacity attribute so opacity collapses onto the group composite.
        byte[] pdf = BuildPdfWithTransparencyGroupAndAlpha();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("isolation:isolate", svg);
        Assert.Contains("opacity=\"0.5\"", svg);
    }

    /// <summary>
    /// The group's alpha rides on its wrapper only: the elements inside are reset to opaque, or the
    /// constant alpha would be applied twice (0.25 for a 0.5 group).
    /// </summary>
    [Fact]
    public void Svg_TransparencyGroupWithCa_DoesNotRepeatTheAlphaOnItsElements()
    {
        using var ms = new MemoryStream(BuildPdfWithTransparencyGroupAndAlpha());
        var doc = System.Xml.Linq.XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));

        Assert.Contains(doc.Descendants(), e => e.Attribute("opacity")?.Value == "0.5");
        Assert.DoesNotContain(doc.Descendants(), e => e.Attribute("fill-opacity")?.Value == "0.5");
    }

    /// <summary>
    /// A non-isolated group under constant alpha cannot be exported as non-isolated — CSS opacity
    /// forms a stacking context — and says so; the isolated one has nothing to report.
    /// </summary>
    [Fact]
    public void Svg_NonIsolatedGroupWithCa_ReportsTheForcedIsolation()
    {
        var nonIsolated = SvgDiagnostics(BuildPdfWithFormXObject(includeGroup: true, alpha: 0.5f, isolated: false));
        var isolated    = SvgDiagnostics(BuildPdfWithFormXObject(includeGroup: true, alpha: 0.5f, isolated: true));

        Assert.Contains(nonIsolated, d => d.Contains("non-isolated group exported to SVG with opacity"));
        Assert.DoesNotContain(isolated, d => d.Contains("non-isolated group exported to SVG with opacity"));
    }

    /// <summary>A knockout group (<c>/K true</c>) is reported — SVG has no knockout model; without /K nothing is.</summary>
    [Fact]
    public void Svg_KnockoutGroup_IsReported()
    {
        var knockout = SvgDiagnostics(BuildPdfWithFormXObject(includeGroup: true, alpha: 1f, knockout: true));
        var plain    = SvgDiagnostics(BuildPdfWithFormXObject(includeGroup: true, alpha: 1f));

        Assert.Contains(knockout, d => d.Contains("knockout group (/K true)"));
        Assert.DoesNotContain(plain, d => d.Contains("knockout group (/K true)"));
    }

    /// <summary>The diagnostic details an SVG export reports on this thread.</summary>
    private static System.Collections.Generic.List<string> SvgDiagnostics(byte[] pdf)
    {
        int thread = Environment.CurrentManagedThreadId;
        var details = new System.Collections.Generic.List<string>();
        Action<RenderDiagnosticKind, string, Exception> handler = (_, d, _) =>
        {
            if (Environment.CurrentManagedThreadId == thread && d != null) lock (details) details.Add(d);
        };
        RenderDiagnostics.OnEvent += handler;
        try
        {
            using var ms = new MemoryStream(pdf);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        }
        finally { RenderDiagnostics.OnEvent -= handler; }
        return details;
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static byte[] BuildPdfWithType4Mesh(bool includeStrip, bool includeFan)
    {
        // Vertex layout (BPC=8, BPC=8, BPF=8, 3 components):
        //   flag(1) + x(1) + y(1) + R(1) + G(1) + B(1) = 6 bytes per vertex
        // Coords are decoded over [0..255] → [0..1] then mapped through /Decode
        // [xMin xMax yMin yMax c0Min c0Max c1Min c1Max c2Min c2Max].

        var vertices = new System.Collections.Generic.List<byte>(48);
        // First triangle (flag 0 on each of the first 3 vertices)
        vertices.AddRange(new byte[] { 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00 }); // (0,0) red
        vertices.AddRange(new byte[] { 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00 }); // (255,0) green
        vertices.AddRange(new byte[] { 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF }); // (0,255) blue

        if (includeStrip)
            vertices.AddRange(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }); // strip
        if (includeFan)
            vertices.AddRange(new byte[] { 0x02, 0x80, 0x80, 0xFF, 0xFF, 0xFF }); // fan

        var hex = new StringBuilder();
        foreach (byte b in vertices) hex.AppendFormat("{0:X2}", b);
        hex.Append('>');
        string hexData = hex.ToString();

        // Decode array maps: x:[0..612], y:[0..792], R/G/B:[0..1]
        // Function-less Type 4 — colours come direct from vertex bytes.
        const string decode = "[0 612 0 792 0 1 0 1 0 1]";
        const string cs     = "/Sh1 sh";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /ShadingType 4 /ColorSpace /DeviceRGB" +
            " /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8" +
            " /Decode {0} /Filter /ASCIIHexDecode /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
            decode, hexData.Length, hexData);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Shading << /Sh1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithRadialShading(string ctm)
    {
        // sh operator paints a radial shading after concat(ctm).
        string cs = $"q {ctm} cm /Sh1 sh Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append(
            "5 0 obj\n<< /ShadingType 3 /ColorSpace /DeviceRGB" +
            " /Coords [200 200 0 200 200 100]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Shading << /Sh1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  FontCache isolation — Form XObject with its own /Resources
    // ------------------------------------------------------------------

    [Fact]
    public void FormXObject_WithOwnFont_DoesNotPolluteFontCacheOfPage()
    {
        // FontCache keys by the resources as well as the name: keyed by resource name only, a
        // page /Resources with /F1 → Helvetica and a Form XObject's own /Resources with
        // /F1 → Times-Roman would make the XObject's Tf /F1 return the page's cached Helvetica.
        // MakeKey(resources, name) uses the resources dict's identity so both font entries
        // coexist in the cache under distinct keys.
        //
        // Both documents below are identical except for the form's own /F1 BaseFont. Under a
        // name-only key the form would draw whatever the page had cached — Helvetica — and the two
        // renders would come out pixel-identical. A serif and a sans face cannot draw the same
        // string the same way, so "the form band differs" IS the claim.
        using var serifForm = PixelAssert.Render(BuildPdfPageAndFormWithDifferentF1("Times-Roman"));
        using var sansForm  = PixelAssert.Render(BuildPdfPageAndFormWithDifferentF1("Helvetica"));

        // The form draws "Form text" on a baseline at user y=600 → rows ≈183..196.
        const int formTop = 175, formBottom = 200;
        // The page draws "Page text" on a baseline at user y=700 → rows ≈83..96.
        const int pageTop = 75, pageBottom = 100;

        Assert.True(InkInRows(serifForm, formTop, formBottom) > 50,
            "the form XObject's own text should have been drawn.");
        Assert.True(InkInRows(sansForm, formTop, formBottom) > 50,
            "the control form XObject's text should have been drawn.");

        int formDiff = DiffInRows(serifForm, sansForm, formTop, formBottom);
        Assert.True(formDiff > 20,
            $"the form must use its OWN /F1 (Times-Roman), not the page's cached Helvetica; " +
            $"only {formDiff} pixels differ in the form's text band.");

        // The page's own text is the same in both, so the difference above is localised to the
        // XObject rather than being an artefact of the whole render shifting.
        Assert.Equal(0, DiffInRows(serifForm, sansForm, pageTop, pageBottom));
    }

    private static int InkInRows(SKBitmap bmp, int top, int bottom)
    {
        int n = 0;
        for (int y = top; y <= bottom && y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (PixelAssert.IsInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static int DiffInRows(SKBitmap a, SKBitmap b, int top, int bottom)
    {
        int n = 0;
        for (int y = top; y <= bottom && y < a.Height && y < b.Height; y++)
            for (int x = 0; x < a.Width && x < b.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    private static byte[] BuildPdfPageAndFormWithDifferentF1(string formBaseFont = "Times-Roman")
    {
        // Page text uses Helvetica as /F1; Form XObject has its OWN /Resources
        // also naming a (by default different) font as /F1 — under the same key.
        const string pageCs  = "BT /F1 12 Tf 50 700 Td (Page text) Tj ET q /Fm1 Do Q";
        const string formCs  = "BT /F1 12 Tf 50 600 Td (Form text) Tj ET";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Form XObject (obj 5) — owns its /Resources, /F1 → Times-Roman.
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792]" +
            " /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /{2} >> >> >>" +
            " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            formCs.Length, formCs, formBaseFont);

        // Page (obj 3) — /F1 → Helvetica, XObject /Fm1 → form.
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources <<" +
                  " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>" +
                  " /XObject << /Fm1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageCs.Length, pageCs);

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithTransparencyGroupForm(bool isolated = true)
        => BuildPdfWithFormXObject(includeGroup: true, alpha: 1f, isolated: isolated);

    private static byte[] BuildPdfWithPlainForm()
        => BuildPdfWithFormXObject(includeGroup: false, alpha: 1f);

    private static byte[] BuildPdfWithTransparencyGroupAndAlpha()
        => BuildPdfWithFormXObject(includeGroup: true, alpha: 0.5f, isolated: true);

    private static byte[] BuildPdfWithFormXObject(bool includeGroup, float alpha,
        bool isolated = true, bool knockout = false)
    {
        // The Form XObject draws a single red rectangle.
        const string formCs = "1 0 0 rg 0 0 100 100 re f";
        // /I is written explicitly: it defaults to FALSE, so a fixture that omits it is asking
        // for a NON-isolated group, which is what these tests used to do by accident.
        string formGroup = includeGroup
            ? $" /Group << /S /Transparency /CS /DeviceRGB /I {(isolated ? "true" : "false")}"
              + (knockout ? " /K true" : "") + " >>"
            : "";

        // Page content: optionally set CA=alpha, then Do the form.
        string cs = alpha < 1f
            ? $"q /GS1 gs 1 0 0 1 100 100 cm /F1 Do Q"
            : "q 1 0 0 1 100 100 cm /F1 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Form XObject (obj 5).
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 100]" + formGroup +
            " /Resources << >> /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            formCs.Length, formCs);

        // ExtGState (obj 6) only when alpha < 1.
        int o6 = -1;
        if (alpha < 1f)
        {
            o6 = sb.Length;
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "6 0 obj\n<< /Type /ExtGState /CA {0} /ca {0} >>\nendobj\n", alpha);
        }

        // Page (obj 3) with appropriate resources.
        int o3 = sb.Length;
        if (alpha < 1f)
            sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                      " /Contents 4 0 R /Resources << /XObject << /F1 5 0 R >>" +
                      " /ExtGState << /GS1 6 0 R >> >> >>\nendobj\n");
        else
            sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                      " /Contents 4 0 R /Resources << /XObject << /F1 5 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int xref = sb.Length;
        int objCount = alpha < 1f ? 7 : 6;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", objCount);
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        if (o6 >= 0) sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", objCount);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  BuildUnicodeToGidFromNames — "uniXXXX" glyph name reverse index
    // ------------------------------------------------------------------

    [Fact]
    public void BuildUnicodeToGidFromNames_HandlesUni4DigitNames()
    {
        // Standard CFF subset naming convention: glyph named "uni0041" = Unicode U+0041 ('A').
        var nameToGid = new System.Collections.Generic.Dictionary<string, int>
        {
            { "uni0041", 1 },  // A
            { "uni0065", 2 },  // e
            { "uni00E9", 3 },  // é (uppercase hex)
            { "uni00e9", 4 },  // é (lowercase hex — same codepoint, second wins)
        };

        var result = FontCache.BuildUnicodeToGidFromNames(nameToGid);

        Assert.True(result.ContainsKey(0x0041));
        Assert.Equal(1, result[0x0041]);
        Assert.True(result.ContainsKey(0x0065));
        Assert.Equal(2, result[0x0065]);
        // Either the uppercase or lowercase entry wins for U+00E9; both map to the same cp.
        Assert.True(result.ContainsKey(0x00E9));
    }

    [Fact]
    public void BuildUnicodeToGidFromNames_HandlesU5And6DigitNames()
    {
        // Some PDF generators use 5- or 6-digit hex after "u" for supplementary codepoints.
        var nameToGid = new System.Collections.Generic.Dictionary<string, int>
        {
            { "u1F600", 5 },   // 5-digit: U+1F600 (GRINNING FACE emoji)
            { "u01F601", 6 },  // 6-digit: U+01F601 (BEAMING FACE)
        };

        var result = FontCache.BuildUnicodeToGidFromNames(nameToGid);

        Assert.True(result.ContainsKey(0x1F600));
        Assert.Equal(5, result[0x1F600]);
        Assert.True(result.ContainsKey(0x1F601));
        Assert.Equal(6, result[0x1F601]);
    }

    /// <summary>
    /// The AGL <c>u</c> form takes <b>four</b>, five or six hex digits, and the four-digit case was
    /// excluded here while <c>StandardEncodings.TryGetGlyphUnicode</c> accepted it — so a font whose
    /// glyphs are named <c>uF0B7</c> resolved when a name was looked up one at a time and produced
    /// nothing when the whole Unicode→GID map was built from names. The two answer different
    /// questions (a BMP <c>char</c> versus a codepoint→GID map) and are correctly separate; what
    /// was not correct was disagreeing about which names are AGL names.
    /// </summary>
    [Fact]
    public void BuildUnicodeToGidFromNames_HandlesU4DigitNames()
    {
        var nameToGid = new System.Collections.Generic.Dictionary<string, int>
        {
            { "uF0B7", 7 },   // a private-use bullet, the shape this really appears as
            { "u0041", 8 },
        };

        var result = FontCache.BuildUnicodeToGidFromNames(nameToGid);

        Assert.Equal(7, result[0xF0B7]);
        Assert.Equal(8, result[0x0041]);
    }

    /// <summary>
    /// Widening to four digits must not start matching ordinary words. <c>union</c> is five
    /// characters beginning with <c>u</c>, and <c>uacute</c> is a real Adobe glyph name — neither
    /// parses as hex, which is what keeps the widening free of false positives.
    /// </summary>
    [Fact]
    public void BuildUnicodeToGidFromNames_DoesNotMistakeOrdinaryNamesForHex()
    {
        var nameToGid = new System.Collections.Generic.Dictionary<string, int>
        {
            { "union",  1 },
            { "uacute", 2 },
            { "uhorn",  3 },
        };

        Assert.Empty(FontCache.BuildUnicodeToGidFromNames(nameToGid));
    }

    [Fact]
    public void BuildUnicodeToGidFromNames_IgnoresStandardGlyphNames()
    {
        // Standard Adobe glyph names ("A", "eacute") are handled by the direct name-lookup
        // path in ApplyPdfEncodingOverride, not by this reverse-index method.
        var nameToGid = new System.Collections.Generic.Dictionary<string, int>
        {
            { "A",       1 },
            { "eacute",  2 },
            { "space",   3 },
            { "uni0041", 4 },  // only this one should appear in the result
        };

        var result = FontCache.BuildUnicodeToGidFromNames(nameToGid);

        // Only "uni0041" is parsed; "A", "eacute", "space" are not "uniXXXX" or "uXXXXX" names.
        Assert.False(result.ContainsKey(0x0041) && result[0x0041] != 4);
        Assert.True(result.ContainsKey(0x0041));
        Assert.Equal(4, result[0x0041]);
    }

    [Fact]
    public void ApplyPdfEncodingOverride_Afii57xxxDifferences_BridgesViaUnicodeToUniXXXX()
    {
        // A bank statement's Hebrew header text: the embedded CFF font uses "uni05D0"
        // glyph names, and the PDF /Encoding Differences uses "afii57664". A direct
        // name lookup alone would fail and leave the char-code→GID map empty, rendering
        // all Hebrew glyphs as .notdef (invisible).

        // CFF font: only two glyphs — .notdef (GID 0) and "uni05D0" (GID 1, alef).
        var glyphNameToGid = new Dictionary<string, int>
        {
            [".notdef"] = 0,
            ["uni05D0"] = 1,
        };
        var cffFont = new CffFont(
            charstrings:    new[] { new byte[] { 14 }, new byte[] { 14 } }, // endchar stubs
            localSubrs:     null,
            globalSubrs:    null,
            charCodeToGid:  null,
            defaultWidthX:  500f,
            nominalWidthX:  0f,
            glyphNameToGid: glyphNameToGid);

        // Inline encoding dictionary: /Differences [160 /afii57664]
        var diffs = new PdfArray();
        diffs.Add(new PdfNumber(160));
        diffs.Add(new PdfName("afii57664"));

        var encDict = new PdfDictionary();
        encDict.Set(new PdfName("Type"),        new PdfName("Encoding"));
        encDict.Set(PdfName.Differences,        diffs);

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Encoding, encDict); // inline dict — no PdfReference needed

        // Minimal parseable PDF (doc is only used for ResolveObject pass-through).
        byte[] minPdf = Encoding.ASCII.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Kids[]/Count 0>>\nendobj\n" +
            "xref\n0 3\n" +
            "0000000000 65535 f \n" +
            "0000000009 00000 n \n" +
            "0000000058 00000 n \n" +
            "trailer\n<</Size 3/Root 1 0 R>>\nstartxref\n107\n%%EOF\n");
        PdfDocument doc;
        using (var ms = new MemoryStream(minPdf))
            doc = PdfDocument.Open(ms);

        var result = FontCache.ApplyPdfEncodingOverride(cffFont, fontDict, doc);

        // Char code 160 (mapped to "afii57664" = alef = U+05D0) must resolve to GID 1.
        Assert.True(result.CharCodeToGid.TryGetValue(160, out int gid),
            "charCode 160 (afii57664) not found in resulting CharCodeToGid map");
        Assert.Equal(1, gid);
    }

    /// <summary>
    /// §9.6.6.1: <c>/Differences</c> with no <c>/BaseEncoding</c> are differences from the embedded
    /// program's own built-in encoding, not StandardEncoding. This program's encoding swaps A and B
    /// (code 0x41 draws glyph B); the PDF names only code 0x43. 0x41 keeps the built-in glyph B —
    /// the StandardEncoding base gave it A — and 0x43 takes the name /Differences gives it.
    /// </summary>
    [Fact]
    public void ApplyPdfEncodingOverride_DifferencesWithoutABaseEncoding_StartFromTheBuiltInEncoding()
    {
        var cffFont = new CffFont(
            charstrings:    new[] { new byte[] { 14 }, new byte[] { 14 }, new byte[] { 14 } },
            localSubrs:     null,
            globalSubrs:    null,
            charCodeToGid:  new Dictionary<int, int> { [0x41] = 2, [0x42] = 1 },   // built-in: A↔B
            defaultWidthX:  500f,
            nominalWidthX:  0f,
            glyphNameToGid: new Dictionary<string, int> { [".notdef"] = 0, ["A"] = 1, ["B"] = 2 });

        var diffs = new PdfArray();
        diffs.Add(new PdfNumber(0x43));
        diffs.Add(new PdfName("A"));
        var encDict = new PdfDictionary();
        encDict.Set(PdfName.Differences, diffs);
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Encoding, encDict);

        using var ms = new MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf());
        using var doc = PdfDocument.Open(ms);
        var result = FontCache.ApplyPdfEncodingOverride(cffFont, fontDict, doc);

        Assert.Equal(2, result.CharCodeToGid[0x41]);   // built-in B, not StandardEncoding's A
        Assert.Equal(1, result.CharCodeToGid[0x43]);   // the /Differences name
    }

    /// <summary>
    /// §9.6.6.1: each name after a code in <c>/Differences</c> takes the next code. A run of two
    /// names from 0x43 puts the second at 0x44, and leaves 0x43 with the first.
    /// </summary>
    [Fact]
    public void ApplyPdfEncodingOverride_ARunOfDifferencesNames_TakesConsecutiveCodes()
    {
        var cffFont = new CffFont(
            charstrings:    new[] { new byte[] { 14 }, new byte[] { 14 }, new byte[] { 14 } },
            localSubrs:     null,
            globalSubrs:    null,
            charCodeToGid:  null,
            defaultWidthX:  500f,
            nominalWidthX:  0f,
            glyphNameToGid: new Dictionary<string, int> { [".notdef"] = 0, ["A"] = 1, ["B"] = 2 });

        var diffs = new PdfArray();
        diffs.Add(new PdfNumber(0x43));
        diffs.Add(new PdfName("A"));
        diffs.Add(new PdfName("B"));
        var encDict = new PdfDictionary();
        encDict.Set(PdfName.Differences, diffs);
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Encoding, encDict);

        using var ms = new MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf());
        using var doc = PdfDocument.Open(ms);
        var result = FontCache.ApplyPdfEncodingOverride(cffFont, fontDict, doc);

        Assert.Equal(1, result.CharCodeToGid[0x43]);
        Assert.Equal(2, result.CharCodeToGid[0x44]);
    }

    /// <summary>
    /// <c>/ToUnicode</c> fills only the codes the encoding left empty. Here WinAnsi already maps
    /// 0x41 to the program's <c>A</c>; a <c>/ToUnicode</c> claiming 0x41 is alef must not replace
    /// it, while the empty code 0x01 it also names does take the alef glyph.
    /// </summary>
    [Fact]
    public void ApplyPdfEncodingOverride_ToUnicode_FillsGapsAndNeverReplacesTheEncoding()
    {
        var cffFont = new CffFont(
            charstrings:    new[] { new byte[] { 14 }, new byte[] { 14 }, new byte[] { 14 } },
            localSubrs:     null,
            globalSubrs:    null,
            charCodeToGid:  null,
            defaultWidthX:  500f,
            nominalWidthX:  0f,
            glyphNameToGid: new Dictionary<string, int> { [".notdef"] = 0, ["A"] = 1, ["uni05D0"] = 2 });

        var toUnicode = new PdfStream();
        toUnicode.RawData = Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "2 beginbfchar\n<01> <05D0>\n<41> <05D0>\nendbfchar\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end\n");
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Encoding, new PdfName("WinAnsiEncoding"));
        fontDict.Set(PdfName.ToUnicode, toUnicode);

        using var ms = new MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf());
        using var doc = PdfDocument.Open(ms);
        var result = FontCache.ApplyPdfEncodingOverride(cffFont, fontDict, doc);

        Assert.Equal(1, result.CharCodeToGid[0x41]);   // the encoding's A, kept
        Assert.Equal(2, result.CharCodeToGid[0x01]);   // the gap, filled
    }

    /// <summary>
    /// A <c>/ToUnicode</c> entry naming a lone surrogate is skipped, and the entries after it still
    /// bridge to the program's <c>uniXXXX</c> glyphs. <c>ConvertToUtf32</c> threw on it, and the
    /// font load's catch then dropped the embedded program for a system font. Core's CMap parser
    /// already empties a <i>single</i> surrogate unit (<c>&lt;D800&gt;</c>), so the shape that
    /// reaches here is a longer value: a high surrogate followed by no low one.
    /// </summary>
    [Fact]
    public void ApplyPdfEncodingOverride_ALoneSurrogateInToUnicode_SkipsThatEntryOnly()
    {
        var cffFont = new CffFont(
            charstrings:    new[] { new byte[] { 14 }, new byte[] { 14 } },
            localSubrs:     null,
            globalSubrs:    null,
            charCodeToGid:  null,
            defaultWidthX:  500f,
            nominalWidthX:  0f,
            glyphNameToGid: new Dictionary<string, int> { [".notdef"] = 0, ["uni05D0"] = 1 });

        var toUnicode = new PdfStream();
        toUnicode.RawData = Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "2 beginbfchar\n<01> <D8000041>\n<02> <05D0>\nendbfchar\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end\n");
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Encoding, new PdfName("WinAnsiEncoding"));
        fontDict.Set(PdfName.ToUnicode, toUnicode);

        using var ms = new MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf());
        using var doc = PdfDocument.Open(ms);
        var result = FontCache.ApplyPdfEncodingOverride(cffFont, fontDict, doc);

        Assert.Equal(1, result.CharCodeToGid[0x02]);
        Assert.False(result.CharCodeToGid.ContainsKey(0x01));
    }

    [Fact]
    public void Afii57xxx_GlyphName_MapsToCorrectUnicodeForBridge()
    {
        // The bridge in ApplyPdfEncodingOverride uses StandardEncodings.TryGetGlyphUnicode
        // to convert afii57xxx glyph names → Unicode codepoints, then looks up the
        // corresponding "uniXXXX" glyph in the CFF's GlyphNameToGid.
        // Bank Hapoalim PDFs embed CFF fonts with "uni05D0"-style names while the PDF
        // encoding Differences use "afii57664"-style names — this test validates the bridge.

        // Step 1: afii57xxx names map to the correct Unicode codepoints.
        Assert.True(Tetitulz.Pdf.Core.Fonts.StandardEncodings.TryGetGlyphUnicode("afii57664", out char alef));
        Assert.Equal('א', alef); // Hebrew alef

        Assert.True(Tetitulz.Pdf.Core.Fonts.StandardEncodings.TryGetGlyphUnicode("afii57665", out char bet));
        Assert.Equal('ב', bet);

        Assert.True(Tetitulz.Pdf.Core.Fonts.StandardEncodings.TryGetGlyphUnicode("afii57672", out char tet));
        Assert.Equal('ט', tet);

        // Step 2: BuildUnicodeToGidFromNames correctly builds the reverse map for uniXXXX names.
        var glyphNames = new System.Collections.Generic.Dictionary<string, int>
        {
            { "uni05D0", 1 },   // alef
            { "uni05D1", 2 },   // bet
            { "uni05D8", 9 },   // tet
            { ".notdef", 0 },
        };
        var reverseMap = FontCache.BuildUnicodeToGidFromNames(glyphNames);

        Assert.Equal(1, reverseMap[0x05D0]); // afii57664 → U+05D0 → GID 1
        Assert.Equal(2, reverseMap[0x05D1]); // afii57665 → U+05D1 → GID 2
        Assert.Equal(9, reverseMap[0x05D8]); // afii57672 → U+05D8 → GID 9

        // Step 3: The bridge correctly converts afii57664 → U+05D0 → GID 1.
        Tetitulz.Pdf.Core.Fonts.StandardEncodings.TryGetGlyphUnicode("afii57664", out char u1);
        Assert.True(reverseMap.TryGetValue((int)u1, out int gid1));
        Assert.Equal(1, gid1);
    }

    [Fact]
    public void BuildUnicodeToGidFromNames_IgnoresMalformedUniNames()
    {
        // "uniXYZW" where XYZW contains non-hex chars must be ignored.
        var nameToGid = new System.Collections.Generic.Dictionary<string, int>
        {
            { "uniGHIJ", 1 },  // non-hex
            { "uni",     2 },  // too short
            { "uni004",  3 },  // 3 hex digits (needs 4)
        };

        var result = FontCache.BuildUnicodeToGidFromNames(nameToGid);
        Assert.Empty(result);
    }
}
