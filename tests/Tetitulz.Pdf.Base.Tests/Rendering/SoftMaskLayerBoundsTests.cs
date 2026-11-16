// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The soft-mask scratch layer is now sized to the geometry being drawn rather than to the page.
/// <para>
/// That is a pure performance change — the layer is scratch space and restricting it to a region
/// the draw cannot paint outside of yields the same pixels. <b>The failure mode is a layer that
/// is too small</b>, which silently clips the edges of what was drawn: a stroke extends half its
/// width either side of the path and a miter join reaches further still, so bounds taken from the
/// fill outline alone would shave the outside of every stroke. Nothing about that is visible in a
/// test that only checks the middle of a shape.
/// </para>
/// </summary>
public class SoftMaskLayerBoundsTests
{
    private const int Dpi = 72;   // 1 pt == 1 px

    /// <summary>
    /// A thick stroked square under a luminosity mask that covers the whole page. Samples the
    /// <i>outer</i> edge of the stroke — the first thing a too-tight layer removes.
    /// </summary>
    [Fact]
    public void SoftMaskedStroke_OuterEdgeIsNotClipped()
    {
        // 20pt-wide stroke on a square from (40,40) to (160,160): the stroke spans 30..50 on each
        // side, so x=32 is well outside the path but well inside the stroke.
        byte[] pdf = MaskedPage("0 0 0 RG 20 w 40 40 120 120 re S");

        using SKBitmap bmp = Render(pdf);

        SKColor outerEdge = bmp.GetPixel(32, 100);
        SKColor wellOutside = bmp.GetPixel(10, 100);

        Assert.True(outerEdge.Red < 100 && outerEdge.Green < 100 && outerEdge.Blue < 100,
            $"outer edge of the stroke should be painted black, was {outerEdge}");
        Assert.True(wellOutside.Red > 200,
            $"well outside the stroke should stay white page, was {wellOutside}");
    }

    /// <summary>
    /// A sharp miter join projects beyond both the path bounds and the plain half-stroke margin.
    /// The spike here points left from a narrow V, so its tip lands outside the path's own bounds.
    /// </summary>
    [Fact]
    public void SoftMaskedMiterJoin_SpikeIsNotClipped()
    {
        // A narrow V opening to the right, stroked thick with a miter join: the join at the left
        // vertex projects further left than the path itself reaches.
        byte[] pdf = MaskedPage("0 0 0 RG 14 w 0 j 10 M 120 30 m 60 100 l 120 170 l S");

        using SKBitmap bmp = Render(pdf);

        // The vertex sits at x=60; the miter spike extends left of it.
        SKColor atVertex = bmp.GetPixel(56, 100);
        Assert.True(atVertex.Red < 100 && atVertex.Green < 100 && atVertex.Blue < 100,
            $"miter spike left of the vertex should be painted, was {atVertex}");
    }

    /// <summary>
    /// The control: without a mask the same content renders identically. This is what makes the
    /// two tests above about <i>the mask path</i> rather than about stroking in general — if the
    /// bounds were clipping, the masked render would differ from the unmasked one.
    /// </summary>
    [Fact]
    public void SoftMaskedAndUnmasked_RenderTheSamePixels_WhenTheMaskIsFullyOpaque()
    {
        const string content = "0 0 0 RG 20 w 40 40 120 120 re S";

        using SKBitmap masked = Render(MaskedPage(content));
        using SKBitmap plain = Render(UnmaskedPage(content));

        Assert.Equal(plain.Width, masked.Width);
        Assert.Equal(plain.Height, masked.Height);

        int differing = 0;
        for (int y = 0; y < plain.Height; y++)
            for (int x = 0; x < plain.Width; x++)
                if (plain.GetPixel(x, y) != masked.GetPixel(x, y)) differing++;

        // A fully white luminosity mask is alpha 1 everywhere, so the two must agree. Allow a
        // handful of pixels for antialiasing differences introduced by the extra layer.
        Assert.True(differing <= plain.Width * plain.Height / 1000,
            $"{differing} pixels differ between the masked and unmasked render");
    }

    /// <summary>
    /// The wrapper must invoke the draw it is handed <b>exactly once</b>.
    ///
    /// <para><b>Why this needs its own test, and why it is text.</b> A path fill or stroke under a
    /// mask takes the shader fast path (<c>TryDrawPathWithMaskShader</c>), which never enters the
    /// scratch layer at all — so the three tests above exercise the shader, not the wrapper. Text
    /// does not try the shader path, so it is the shortest route into
    /// <c>DrawWithSoftMask</c>.</para>
    ///
    /// <para><b>And why <c>ca 0.5</c>.</b> An opaque draw is idempotent under SrcOver: painting
    /// it twice into the layer gives byte-identical pixels, so no opaque fixture can tell one
    /// invocation from two. At half alpha they separate cleanly — 0.5 of black over white is 127,
    /// a second pass takes it to 64. That is the failure a refactor of the wrapper's calling
    /// convention introduces, and it had no coverage before this.</para>
    /// </summary>
    [Fact]
    public void SoftMaskedText_AtHalfAlpha_IsPaintedOnceIntoTheScratchLayer()
    {
        // 8 = ca 0.5 + a fully white luminosity mask (alpha 1 everywhere, so it changes nothing);
        // 9 = the mask group. The glyph is EmbeddedCffFixture's solid box, so the sample point is
        // deep inside flat ink rather than on an antialiased letter edge.
        const string maskGroup = "1 1 1 rg 0 0 612 792 re f";
        byte[] pdf = EmbeddedCffFixture.Page(
            "/GM gs 0 0 0 rg BT /F1 100 Tf 100 300 Td (A) Tj ET",
            extraPageResources: "/ExtGState << /GM 8 0 R >>",
            extraObjects:
                "8 0 obj\n<< /Type /ExtGState /ca 0.5 "
              + "/SMask << /Type /Mask /S /Luminosity /G 9 0 R >> >>\nendobj\n"
              + "9 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] "
              + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << >> "
              + $"/Length {maskGroup.Length} >>\nstream\n{maskGroup}\nendstream\nendobj\n");

        using SKBitmap bmp = Render(pdf);
        SKColor inGlyph = PixelAssert.At(bmp, 120f, 320f);

        Assert.InRange(inGlyph.Red, 100, 155);
        Assert.InRange(inGlyph.Green, 100, 155);
        Assert.InRange(inGlyph.Blue, 100, 155);
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new System.IO.MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>A 200x200 page whose content runs under a fully-white luminosity soft mask.</summary>
    private static byte[] MaskedPage(string content) => Build(content, withMask: true);

    /// <summary>The same page with no ExtGState at all.</summary>
    private static byte[] UnmaskedPage(string content) => Build(content, withMask: false);

    private static byte[] Build(string content, bool withMask)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }
        void EndObj() => Emit("endobj\n");

        string cs = withMask ? "/GM gs\n" + content : content;
        string resources = withMask
            ? "/Resources << /ExtGState << /GM 6 0 R >> >>"
            : "/Resources << >>";

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\n"); EndObj();
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n"); EndObj();

        BeginObj(3);
        Emit($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] {resources} /Contents 4 0 R >>\n");
        EndObj();

        BeginObj(4);
        Emit($"<< /Length {Encoding.Latin1.GetByteCount(cs)} >>\nstream\n{cs}\nendstream\n");
        EndObj();

        // Mask group: white over the whole page, i.e. luminosity 1 → alpha 1 everywhere.
        const string fs = "1 1 1 rg\n0 0 200 200 re f\n";
        BeginObj(5);
        Emit("<< /Type /XObject /Subtype /Form /BBox [0 0 200 200]\n" +
             "   /Group << /Type /Group /S /Transparency /CS /DeviceGray >>\n" +
             $"   /Resources << >> /Length {Encoding.Latin1.GetByteCount(fs)} >>\nstream\n{fs}\nendstream\n");
        EndObj();

        BeginObj(6);
        Emit("<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >>\n");
        EndObj();

        int xrefOffset = acc.Count;
        const int objCount = 7;
        Emit($"xref\n0 {objCount}\n0000000000 65535 f \n");
        for (int i = 1; i < objCount; i++)
            Emit(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {objCount} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return acc.ToArray();
    }
}
