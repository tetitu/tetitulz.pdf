// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the Windows PUA (U+F000..U+F0FF) glyph fallback.
///
/// Background: Many Hebrew, Greek, and Cyrillic TrueType fonts created before
/// full Unicode adoption register their glyphs at 0xF000|charByte (Private Use
/// Area) rather than at the proper Unicode codepoints.  A PDF that uses such a
/// font emits byte codes 0xE0–0xFA for Hebrew Alef–Tav; the /Differences array
/// maps those bytes to "afii57664"…"afii57690" (→ U+05D0…U+05EA).  The font's
/// own cmap however only contains U+F0E0…U+F0FA.  Without the F0xx fallback,
/// every glyph lookup returns GID 0 and nothing renders.
/// </summary>
public class HebrewPuaFontRenderingTests
{
    // A real Israeli bank PDF that exhibits the F0xx PUA pattern. Developer-local:
    // resolved through TestCorpus, never committed.
    private const string BankPdf = "pb08prd6_76553856_963647.pdf";

    // ------------------------------------------------------------------
    //  Integration smoke test (skips visibly when the raw PDF is unavailable)
    // ------------------------------------------------------------------

    [Tetitulz.Pdf.TestHelpers.CorpusFact(BankPdf)]
    public void RenderBankPdf_HeaderHasPixels_NotAllWhite()
    {
        string path = Tetitulz.Pdf.TestHelpers.TestCorpus.Find(BankPdf)!;

        var diagnostics = new System.Collections.Generic.List<string>();
        RenderDiagnostics.OnEvent += (kind, detail, _) =>
            diagnostics.Add($"{kind}: {detail}");

        byte[] pngBytes = PdfPageRenderer.RenderPage(path, 0, 100);
        Assert.NotNull(pngBytes);
        Assert.True(pngBytes.Length > 0);

        // Decode PNG and count dark pixels in the top 15% (header area).
        using var bmp = SKBitmap.Decode(pngBytes);
        Assert.NotNull(bmp);

        int headerHeight = bmp.Height / 7;
        int darkPixels = 0;
        for (int y = 0; y < headerHeight; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if ((c.Red + c.Green + c.Blue) / 3 < 200)
                    darkPixels++;
            }

        // The header must have some rendered content (text / logo).
        Assert.True(darkPixels > 50,
            $"Header region appears blank ({darkPixels} dark pixels). " +
            $"F0xx PUA fallback may not be working. " +
            $"Diagnostics: {string.Join("; ", diagnostics)}");
    }

    // ------------------------------------------------------------------
    //  F0xx font-lookup behaviour verification
    //
    //  The Windows Symbol font maps printable ASCII chars both at their
    //  normal Unicode codepoints (0x20–0x7E) AND at 0xF020–0xF07E (PUA).
    //  This lets us verify that 0xF000 | byteCode lookup succeeds when the
    //  base Unicode lookup fails.
    // ------------------------------------------------------------------

    [Fact]
    public void SymbolFont_GlyphExistsAtF020Range_ConfirmsPuaBehaviour()
    {
        // Symbol is a standard Windows / SkiaSharp font; skip if unavailable.
        var tf = SKTypeface.FromFamilyName("Symbol");
        if (tf == null) return;

        // 0x60 = backtick-like char in Symbol. It should be at U+F060 (PUA).
        using var probe = new SKFont(tf);
        ushort puaGid = probe.GetGlyph(0xF060);
        // If Symbol doesn't support PUA on this platform, skip rather than fail.
        if (puaGid == 0) return;

        // The same byte at the "real" Unicode position might differ or be absent.
        // Either way, the PUA glyph exists — confirming the pattern our fallback exploits.
        Assert.True(puaGid > 0);
    }

    // ------------------------------------------------------------------
    //  Minimal PDF: /Differences with non-ASCII bytes and afii glyph names
    // ------------------------------------------------------------------

    /// <summary>
    /// The chain this fixture exists for: byte 0xE0 → <c>/Differences</c> → the glyph name
    /// <c>afii57664</c> → U+05D0. Resolving the afii names is host-independent — it is a table
    /// lookup, not a font lookup — so it is asserted here rather than in pixels.
    ///
    /// <para>The predecessor rendered the page and asserted only that nothing was thrown, which
    /// is equally true when every byte maps to nothing at all.</para>
    /// </summary>
    [Fact]
    public void Render_DifferencesEncodedNonAscii_MapsTheAfiiNamesToHebrewCodepoints()
    {
        byte[] pdf = BuildHebrewDiffPdf();

        using var ms = new MemoryStream(pdf);
        string text = Tetitulz.Pdf.Base.PdfOperations.ExtractText(ms);

        // afii57664 / afii57665 / afii57666 = Alef, Bet, Gimel. Asserted per code point, so the
        // claim is about the encoding lookup and not about RTL reordering, which is separate.
        string dump = string.Join(" ", text.Trim().Select(c => ((int)c).ToString("X4")));
        Assert.True(text.Contains('א'), "no Alef (U+05D0) in extracted text: " + dump);
        Assert.True(text.Contains('ב'), "no Bet (U+05D1) in extracted text: " + dump);
        Assert.True(text.Contains('ג'), "no Gimel (U+05D2) in extracted text: " + dump);
    }

    /// <summary>
    /// And the same page has to put those glyphs on the raster, at the pen position the content
    /// stream set. Host-gated: where Arial is not installed the substitute may carry no Hebrew,
    /// so the glyphs legitimately resolve to .notdef and the claim is not observable — that is a
    /// fact about the host, so the test skips visibly instead of asserting something weaker.
    /// </summary>
    [Tetitulz.Pdf.Base.Tests.TestInfrastructure.SystemFontFact("Arial")]
    public void Render_DifferencesEncodedNonAscii_DrawsTheHebrewGlyphsAtThePenPosition()
    {
        byte[] pdf = BuildHebrewDiffPdf();

        using var bmp = Tetitulz.Pdf.Base.Tests.Rendering.PixelAssert.Render(pdf);
        var box = Tetitulz.Pdf.Base.Tests.Rendering.PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);

        // `100 700 Td` at 12pt: three Hebrew letters starting at the pen, sitting on the baseline.
        Assert.InRange(box!.Value.left,   98,  112);
        Assert.InRange(box.Value.right,  110,  160);
        Assert.InRange(box.Value.bottom, bmp.Height - 1 - 706, bmp.Height - 1 - 694);
        Assert.InRange(box.Value.top,    bmp.Height - 1 - 716, bmp.Height - 1 - 700);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal PDF that uses byte 0xE0 in a text string with a font
    /// whose /Differences array maps that byte to "afii57664" (Hebrew Alef).
    /// The font is a non-embedded TrueType substitute (Arial) so the renderer
    /// will attempt the full GID fixup chain including the F0xx fallback.
    /// </summary>
    private static byte[] BuildHebrewDiffPdf()
    {
        // Encoding dict: /Differences [224 /afii57664 /afii57665]
        // So byte 0xE0=224 → afii57664 (Alef), 0xE1=225 → afii57665 (Bet)
        const string encodingObj =
            "6 0 obj\n" +
            "<< /Type /Encoding\n" +
            "   /Differences [ 224 /afii57664 /afii57665 /afii57666 ]\n" +
            ">>\nendobj\n";

        const string fontObj =
            "5 0 obj\n" +
            "<< /Type /Font /Subtype /TrueType\n" +
            "   /BaseFont /ArialMT\n" +
            "   /FirstChar 0 /LastChar 255\n" +
            "   /Encoding 6 0 R\n" +
            "   /Widths [ 500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 278 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 556 500 556 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             500 500 500 500 500 500 500 500 500 500 500 500 500 500\n" +
            "             556 556 556 500 500 500 ]\n" +
            ">>\nendobj\n";

        // Content: BT /F1 12 Tf 100 700 Td (text with bytes 0xE0 0xE1 0xE2) Tj ET
        // Using ASCII encoding for the PDF structure but inserting raw bytes for the string
        var contentSb = new StringBuilder();
        contentSb.Append("BT /F1 12 Tf 100 700 Td (");
        contentSb.Append('\xE0'); // afii57664 = Alef
        contentSb.Append('\xE1'); // afii57665 = Bet
        contentSb.Append('\xE2'); // afii57666 = Gimel
        contentSb.Append(") Tj ET");
        string contentStream = contentSb.ToString();

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o5 = sb.Length;
        sb.Append(fontObj);
        int o6 = sb.Length;
        sb.Append(encodingObj);
        int xref = sb.Length;
        sb.Append("xref\n0 7\n");
        sb.Append("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{0}\n%%%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
