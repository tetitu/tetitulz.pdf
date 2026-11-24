// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Plugins.CMap;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Regression tests for vertical CID (Identity-V) text rendering.
/// Three bugs were fixed:
///   1. glyphScaleMat incorrectly rotated CJK glyphs 90° CW — they should render upright.
///   2. GetVerticalAdvance returned raw negative DW2[1] values; callers expect positive magnitude.
///   3. Multi-glyph Tj strings placed all characters at the same position (horizontal pdfWidths
///      used instead of per-glyph vertical advance).
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class VerticalCidTextTests
{
    // ── helpers ───────────────────────────────────────────────────────────



    private static byte[] BuildPdf(string contentStream, string extraObjects = "")
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        // Minimal Type0/CID font with Identity-V encoding, DW=1000, DW2=[880 -1000]
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Helvetica-VertFake " +
                  "/Encoding /Identity-V /DescendantFonts [6 0 R] >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Helvetica-VertFake " +
                  "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                  "/DW 1000 /DW2 [880 -1000] >>\nendobj\n");
        if (!string.IsNullOrEmpty(extraObjects))
            sb.Append(extraObjects);
        int o4 = sb.Length;
        byte[] cs = Encoding.Latin1.GetBytes(contentStream);
        sb.Append($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n");
        int streamStart = sb.Length;
        sb.Append(contentStream);
        sb.Append("\nendstream\nendobj\n");
        // xref
        string body = sb.ToString();
        int xrefPos = body.Length;
        sb.Clear();
        sb.Append(body);
        sb.Append("xref\n0 7\n");
        sb.Append($"0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append($"{o4:D10} 00000 n \n");
        sb.Append($"{o5:D10} 00000 n \n");
        sb.Append($"{o6:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ── tests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The bug this guards was a spurious 90° rotation in <c>glyphScaleMat</c> for
    /// <c>wMode = 1</c>: vertical writing advances glyphs downward but must <b>not</b> turn each
    /// glyph on its side. A single glyph therefore keeps its normal orientation — wider than tall
    /// for this face — which a rotation would invert.
    ///
    /// <para>The predecessor asserted only that a PNG came back, which a rotated glyph, a blank
    /// page and a crash-free no-op all satisfy. (CID 0x41 draws nothing in the substitute face, so
    /// this uses 0x42, which does.)</para>
    /// </summary>
    [Fact]
    public void VerticalCidFont_SingleGlyph_DrawsUprightNotRotated()
    {
        const string cs = "BT /F1 20 Tf 20 0 0 20 100 700 Tm <0042> Tj ET";

        using var bmp = PixelAssert.Render(BuildPdf(cs));

        var box = PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);
        var (l, t, r, b) = box!.Value;

        Assert.True(r - l > b - t,
            $"a single glyph should stay upright (wider than tall), got {r - l}x{b - t}");
    }

    [Fact]
    public void VerticalCidFont_MultipleGlyphs_AdvanceDownward()
    {
        // Three CID glyphs in a single Tj string using Identity-V wMode=1.
        // They advance downward — not stacked at one position (no advance), nor upward.
        // Adding a glyph must grow the inked region's HEIGHT and leave its width and top edge
        // alone. Horizontal advance would grow the width; no advance would grow neither — and
        // "a PNG came back" cannot tell those three apart.
        // Tm scale 1, not 20: `20 Tf` with a scale-20 Tm makes one em 400 device units, so with
        // the §9.7.4.3 position vector applied (the glyph is drawn at `currentPoint − v`, dropping
        // it 0.88 em) the SECOND glyph's origin would land at y ≈ −52 and be clipped away
        // entirely, leaving both renders pixel-identical.
        const string prefix = "BT /F1 20 Tf 1 0 0 1 100 700 Tm ";

        using var one = PixelAssert.Render(BuildPdf(prefix + "<0042> Tj ET"));
        using var two = PixelAssert.Render(BuildPdf(prefix + "<0042 0043> Tj ET"));

        var oneBox = PixelAssert.InkBounds(one);
        var twoBox = PixelAssert.InkBounds(two);
        Assert.NotNull(oneBox);
        Assert.NotNull(twoBox);

        Assert.True(twoBox!.Value.bottom - twoBox.Value.top > oneBox!.Value.bottom - oneBox.Value.top,
            "a second glyph should extend the run downward");
        Assert.Equal(oneBox.Value.right - oneBox.Value.left,
                     twoBox.Value.right - twoBox.Value.left);   // ...and not sideways
        Assert.Equal(oneBox.Value.top, twoBox.Value.top);       // growth is downward, not upward
    }

    /// <summary>
    /// A <c>TJ</c> number moves a vertical run along its writing direction — down (§9.4.4:
    /// <c>ty = (w1 − Tj/1000)·Tfs</c>) — not sideways. At 20 pt a 500 drops the second glyph 10 px:
    /// the ink reaches 10 lower and its left edge stays. The horizontal formula moved it 10 left.
    /// The SVG writer's twin is <c>SvgFontEmbeddingTests.ATjNumberMovesAVerticalRunDown</c>.
    /// </summary>
    [Fact]
    public void VerticalCidFont_TjNumberMovesTheRunDown()
    {
        const string prefix = "BT /F1 20 Tf 1 0 0 1 100 700 Tm ";

        using var plain  = PixelAssert.Render(BuildPdf(prefix + "[<0042> 0 <0042>] TJ ET"));
        using var spaced = PixelAssert.Render(BuildPdf(prefix + "[<0042> 500 <0042>] TJ ET"));

        var p = PixelAssert.InkBounds(plain);
        var s = PixelAssert.InkBounds(spaced);
        Assert.NotNull(p);
        Assert.NotNull(s);

        Assert.Equal(p!.Value.left, s!.Value.left);
        Assert.InRange(s.Value.bottom - p.Value.bottom, 9, 11);
    }

    /// <summary>
    /// An embedded CID-keyed CFF program in vertical writing advances down like any other vertical
    /// run: a second glyph grows the ink downward, not sideways. The CFF run paths (wrapped as
    /// OpenType, or evaluated from charstrings) advanced every glyph horizontally whatever the
    /// writing mode, laying a vertical Japanese line out as a horizontal one.
    /// </summary>
    [Fact]
    public void VerticalCidFont_EmbeddedCffProgram_AdvancesDownward()
    {
        using var one = PixelAssert.Render(CffCidPdf("<0001>"));
        using var two = PixelAssert.Render(CffCidPdf("<00010002>"));

        var oneBox = PixelAssert.InkBounds(one);
        var twoBox = PixelAssert.InkBounds(two);
        Assert.NotNull(oneBox);
        Assert.NotNull(twoBox);

        Assert.True(twoBox!.Value.bottom - twoBox.Value.top > oneBox!.Value.bottom - oneBox.Value.top,
            $"a second glyph should extend the run downward: {oneBox} then {twoBox}");
        Assert.Equal(oneBox.Value.right - oneBox.Value.left, twoBox.Value.right - twoBox.Value.left);
        Assert.Equal(oneBox.Value.top, twoBox.Value.top);
    }

    /// <summary>
    /// The glyph is drawn at <c>currentPoint − v</c> (§9.7.4.3), v = (w0/2, 880) with this font's
    /// <c>/DW 1000</c>: at 20 pt the box, which starts at the glyph origin, starts 10 left of the pen.
    /// </summary>
    [Fact]
    public void VerticalCidFont_EmbeddedCffProgram_IsCentredOnTheColumnByThePositionVector()
    {
        using var bmp = PixelAssert.Render(CffCidPdf("<0001>"));
        var box = PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);
        Assert.InRange(box!.Value.left, 89, 91);
    }

    /// <summary>
    /// <c>Tc</c> is added to the vertical displacement <c>ty = w1·Tfs + Tc</c> (§9.4.4), and
    /// <c>w1</c> is negative — downward — so a positive <c>Tc</c> shortens the step (§9.3.2: in
    /// vertical writing a negative <c>Tc</c> spreads the glyphs): 10 more puts the second glyph
    /// 10 higher, where it was put 10 lower.
    /// </summary>
    [Fact]
    public void VerticalCidFont_EmbeddedCffProgram_CharSpacingShortensTheVerticalStep()
    {
        using var plain  = PixelAssert.Render(CffCidPdf("<00010002>"));
        using var spaced = PixelAssert.Render(CffCidPdf("<00010002>", "10 Tc"));
        var p = PixelAssert.InkBounds(plain);
        var s = PixelAssert.InkBounds(spaced);
        Assert.NotNull(p);
        Assert.NotNull(s);
        Assert.InRange(p!.Value.bottom - s!.Value.bottom, 9, 11);
    }

    /// <summary>
    /// <c>Tw</c> is in the same displacement (§9.4.4), for the single-byte code 32 alone (§9.3.3):
    /// under a one-byte vertical CMap mapping code 32 to the first glyph, 10 of word spacing puts
    /// the second glyph 10 higher; under a two-byte code whose low byte is 32 it moves nothing.
    /// </summary>
    [Theory]
    [InlineData(true, 10)]
    [InlineData(false, 0)]
    public void VerticalCidFont_WordSpacingShortensTheStepAfterTheSingleByteSpace(bool oneByte, int expectedRise)
    {
        string cmap = oneByte ? OneByteVerticalCMap() : IdentityCMap(true);
        string codes = oneByte ? "<2021>" : "<00200002>";
        using var plain  = PixelAssert.Render(CffCidPdf(codes, cmapDictEntries: "/WMode 1", cmapBody: cmap));
        using var spaced = PixelAssert.Render(CffCidPdf(codes, "10 Tw", cmapDictEntries: "/WMode 1", cmapBody: cmap));
        var p = PixelAssert.InkBounds(plain);
        var s = PixelAssert.InkBounds(spaced);
        Assert.NotNull(p);
        Assert.NotNull(s);
        Assert.InRange(p!.Value.bottom - s!.Value.bottom, expectedRise - 1, expectedRise + 1);
        // Both runs are two glyphs tall, so the first glyph's row is where the step is measured from.
        Assert.Equal(p.Value.top, s.Value.top);
    }

    /// <summary>
    /// A vertical run in a clip mode has its clip laid out as the visible run is: the clip's
    /// <c>&lt;text&gt;</c> carries the per-glyph position lists, so two glyphs clip two rows of
    /// the column, not one. The visible copy of the same run is the control.
    /// </summary>
    [Fact]
    public void VerticalCidFont_ClipModeClip_IsLaidOutDownTheColumn()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(CffCidPdf("<00010002>", "7 Tr")), 0);
        var doc = System.Xml.Linq.XDocument.Parse(svg);
        var clipText = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "clipPath")
            .SelectMany(c => c.Descendants().Where(e => e.Name.LocalName == "text")));

        string[] ys = clipText.Attribute("y")!.Value.Split(' ');
        Assert.Equal(2, ys.Length);
        Assert.NotEqual(ys[0], ys[1]);
        Assert.Equal(2, clipText.Attribute("x")!.Value.Split(' ').Length);
    }

    /// <summary>A one-byte vertical CMap: code 32 is CID 1, code 33 is CID 2.</summary>
    private static string OneByteVerticalCMap() =>
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /TestOneByteV def /CMapType 1 def /WMode 1 def"
        + " 1 begincodespacerange <00> <FF> endcodespacerange"
        + " 2 begincidchar <20> 1 <21> 2 endcidchar endcmap CMapName currentdict /CMap defineresource pop end end";

    /// <summary>The identity CID map as an embedded CMap program, with or without its <c>/WMode 1 def</c>.</summary>
    private static string IdentityCMap(bool wModeInBody) =>
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /TestCMap def /CMapType 1 def"
        + (wModeInBody ? " /WMode 1 def" : "")
        + " 1 begincodespacerange <0000> <FFFF> endcodespacerange"
        + " 1 begincidrange <0000> <FFFF> 0 endcidrange endcmap CMapName currentdict /CMap defineresource pop end end";

    /// <summary>
    /// An embedded CMap stream as the Type0 <c>/Encoding</c> carries the writing mode itself
    /// (Table 120) — in its program (<c>/WMode 1 def</c>) or its stream dictionary's <c>/WMode</c>.
    /// Either makes the run vertical: a second glyph grows the ink downward. Neither was read, so
    /// such text was laid out horizontally. The control, with neither, runs sideways.
    /// </summary>
    [Theory]
    [InlineData(true, "", true)]
    [InlineData(false, "/WMode 1", true)]
    [InlineData(false, "", false)]
    public void AnEmbeddedCMapsWritingModeIsHonoured(bool wModeInBody, string dictEntries, bool vertical)
    {
        string cmap = IdentityCMap(wModeInBody);
        using var one = PixelAssert.Render(CffCidPdf("<0001>", cmapDictEntries: dictEntries, cmapBody: cmap));
        using var two = PixelAssert.Render(CffCidPdf("<00010002>", cmapDictEntries: dictEntries, cmapBody: cmap));
        var o = PixelAssert.InkBounds(one);
        var t = PixelAssert.InkBounds(two);
        Assert.NotNull(o);
        Assert.NotNull(t);

        bool grewDown = t!.Value.bottom - t.Value.top > o!.Value.bottom - o.Value.top;
        bool grewAcross = t.Value.right - t.Value.left > o.Value.right - o.Value.left;
        Assert.Equal(vertical, grewDown);
        Assert.Equal(!vertical, grewAcross);
    }

    /// <summary>A Type0 font over a CID-keyed CFF program (two box glyphs, CIDs 1 and 2) in Identity-V.</summary>
    private static byte[] CffCidPdf(string codes, string textState = "", string cmapDictEntries = null,
                                    string cmapBody = null)
    {
        byte[] cff = Tetitulz.Pdf.TestHelpers.MinimalOpenTypeCff.BuildCidKeyed(new[] { 1, 2 });
        string content = "BT /F1 20 Tf " + textState + " 100 700 Td " + codes + " Tj ET";
        var objects = new (string Dict, byte[] Stream)[]
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>", null),
            ($"<< /Length {content.Length} >>", Encoding.ASCII.GetBytes(content)),
            ("<< /Type /Font /Subtype /Type0 /BaseFont /TestCid /Encoding " + (cmapBody != null ? "9 0 R" : "/Identity-V")
                + " /DescendantFonts [6 0 R] >>", null),
            ("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /TestCid /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"
                + " /DW 1000 /FontDescriptor 7 0 R >>", null),
            ("<< /Type /FontDescriptor /FontName /TestCid /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 880"
                + " /Descent -120 /CapHeight 700 /StemV 80 /FontFile3 8 0 R >>", null),
            ($"<< /Subtype /CIDFontType0C /Length {cff.Length} >>", cff),
            cmapBody != null
                ? ($"<< /Type /CMap /CMapName /TestCMap /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"
                   + $" {cmapDictEntries} /Length {cmapBody.Length} >>", Encoding.ASCII.GetBytes(cmapBody))
                : (null, null),
        };
        objects = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(objects, o => o.Dict != null));

        var ms = new System.IO.MemoryStream();
        void Ascii(string t) { byte[] b = Encoding.ASCII.GetBytes(t); ms.Write(b, 0, b.Length); }
        Ascii("%PDF-1.4\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = ms.Position;
            Ascii($"{i + 1} 0 obj\n{objects[i].Dict}");
            if (objects[i].Stream != null)
            {
                Ascii("\nstream\n");
                ms.Write(objects[i].Stream, 0, objects[i].Stream.Length);
                Ascii("\nendstream");
            }
            Ascii("\nendobj\n");
        }
        long xref = ms.Position;
        Ascii($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets) Ascii($"{off:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void VerticalCidFont_DW2NegativeW1y_AdvancesCorrectDirection()
    {
        // DW2 = [880, -1000]: w1y is negative per spec (downward).
        // Before GetVerticalAdvance fix: negative w1y → negative emAdv → upward advance.
        // After fix: Math.Abs applied → emAdv=1.0 → downward advance.
        // Two Tj strings at different explicit Y positions — the second must render below the first.
        // We cannot check pixel positions precisely here, but confirm no rendering error.
        const string cs =
            "BT /F1 20 Tf " +
            "20 0 0 20 100 700 Tm <0041 0042> Tj " +
            "20 0 0 20 100 600 Tm <0043 0044> Tj " +
            "ET";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        var ex = Record.Exception(() =>
        {
            using var ms2 = new MemoryStream(pdf);
            PdfPageRenderer.RenderPage(ms2, 0, 72);
        });
        Assert.Null(ex);
    }

    // ── Integration test against the real VerticalText.pdf ──────────────

    [Tetitulz.Pdf.TestHelpers.CorpusFact("VerticalText.pdf")]
    public void VerticalText_Japan1CidFont_RendersJapaneseGlyphs()
    {
        // Corpus-resolved: skips visibly when absent instead of returning green.
        string path = Tetitulz.Pdf.TestHelpers.TestCorpus.Find("VerticalText.pdf")!;

        // Register the AdobeCMapPlugin CMaps so Japan1 CIDs decode to Unicode.
        // Use Initialize() directly to avoid PdfPluginHost duplicate-name throw
        // when the plugin was already registered by another test.
        if (!PredefinedCMapRegistry.IsRegistered("UniJIS-UTF16-H"))
            new AdobeCMapPlugin().Initialize();

        var diagnostics = new System.Collections.Generic.List<string>();
        void OnDiag(RenderDiagnosticKind kind, string detail, Exception? _)
            => diagnostics.Add($"{kind}: {detail}");
        RenderDiagnostics.OnEvent += OnDiag;

        try
        {
            byte[] png = PdfPageRenderer.RenderPage(path, 0, 100);
            Assert.NotNull(png);

            using var bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);

            // VerticalText.pdf Japanese column is near x=465 (out of 612 pts).
            // At 100 DPI that is ≈ 465*(100/72) ≈ 645 px from left, roughly 75% of width.
            // Scan that column for dark pixels in the upper portion (y < 60% of height).
            int colLeft   = (int)(bmp.Width  * 0.65f);
            int colRight  = (int)(bmp.Width  * 0.90f);
            int rowBottom = (int)(bmp.Height * 0.60f);
            int darkPixels = 0;
            for (int y = 0; y < rowBottom; y++)
                for (int x = colLeft; x < colRight; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if ((c.Red + c.Green + c.Blue) / 3 < 180)
                        darkPixels++;
                }

            Assert.True(darkPixels > 20,
                $"Right column has only {darkPixels} dark pixels — Japanese text not rendered. " +
                $"Diagnostics: {string.Join("; ", diagnostics)}");
        }
        finally
        {
            RenderDiagnostics.OnEvent -= OnDiag;
        }
    }
}
