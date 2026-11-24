// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering
{
    /// <summary>
    /// Filling TEXT glyphs with a shading PATTERN (`/Pattern cs /P scn … Tj`). Glyph-draw paths
    /// ignoring `FillIsPattern` would paint the stale solid `FillColor` (black), so gradient-filled
    /// headline text (e.g. the "SUPREME" banner on a card issuer's statements) would render solid
    /// black.
    /// </summary>
    public sealed class PatternTextFillTests
    {
        // Page 100×100; a large glyph filled with an axial red(left)→blue(right) shading
        // pattern spanning the page width.
        private static byte[] BuildPatternTextPdf(string glyph = "H")
        {
            var pdf = new StringBuilder();
            pdf.Append("%PDF-1.4\n");
            var offsets = new List<int>();
            void Obj(string body) { offsets.Add(pdf.Length); pdf.Append(body); }

            Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
                "/Contents 4 0 R /Resources << /Font << /F1 6 0 R >> /Pattern << /P1 5 0 R >> >> >>\nendobj\n");

            string content = "/Pattern cs /P1 scn\nBT /F1 90 Tf 12 18 Td (" + glyph + ") Tj ET";
            Obj("4 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");

            Obj("5 0 obj\n<< /Type /Pattern /PatternType 2 /Shading << /ShadingType 2 " +
                "/ColorSpace /DeviceRGB /Coords [0 0 100 0] /Domain [0 1] " +
                "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> " +
                "/Extend [true true] >> >>\nendobj\n");
            Obj("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

            int xref = pdf.Length;
            pdf.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
            pdf.Append("0000000000 65535 f \n");
            foreach (int off in offsets) pdf.Append(off.ToString("D10")).Append(" 00000 n \n");
            pdf.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\n");
            pdf.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        [Fact]
        public void ShadingPatternText_FillsGlyphsWithGradient_NotSolidBlack()
        {
            byte[] pdf = BuildPatternTextPdf();
            using var ms = new MemoryStream(pdf);
            byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
            using var bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);

            int reddish = 0, bluish = 0, blackish = 0, glyphPixels = 0;
            for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                // Glyph pixels = not (near-)white background.
                if (c.Red > 235 && c.Green > 235 && c.Blue > 235) continue;
                glyphPixels++;
                if (c.Red > 130 && c.Blue < 110) reddish++;            // red end (left)
                if (c.Blue > 130 && c.Red < 110) bluish++;             // blue end (right)
                int mx = System.Math.Max(c.Red, System.Math.Max(c.Green, c.Blue));
                int mn = System.Math.Min(c.Red, System.Math.Min(c.Green, c.Blue));
                if (mx < 60 && mx - mn < 24) blackish++;               // near-black & gray (old bug)
            }

            Assert.True(glyphPixels > 200, $"expected a painted glyph, got {glyphPixels} px");
            // The gradient must show both ends — proving the pattern was applied per-pixel.
            Assert.True(reddish > 20, $"expected red (left) gradient pixels, got {reddish}");
            Assert.True(bluish > 20, $"expected blue (right) gradient pixels, got {bluish}");
            // Ignoring the pattern, the whole glyph would be solid black; require the gradient dominates.
            Assert.True(blackish < glyphPixels / 4,
                $"too many solid-black glyph pixels ({blackish}/{glyphPixels}); pattern not applied?");
        }
    }
}
