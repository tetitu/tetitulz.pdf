// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A graphics-state transfer function (/TR, /TR2) must remap fill/stroke colours, not just
/// image samples (PDF §10.4). Pre-fix it was applied only to image bitmaps.
/// </summary>
public class TransferFunctionColorTests
{
    [Fact]
    public void TransferFunction_RemapsFillColor()
    {
        // Fill a rectangle in DeviceGray 0.25 (→ 64) with an inverting /TR (x → 1−x),
        // so the painted gray becomes ~191. Without the fix it would stay ~64.
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string b) { off.Add(pdf.Length); pdf.Append(b); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
            "/Resources << /ExtGState << /GS0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        string c = "/GS0 gs 0.25 g 8 8 56 56 re f";
        Obj($"4 0 obj\n<< /Length {c.Length} >>\nstream\n{c}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /ExtGState /TR 6 0 R >>\nendobj\n");
        Obj("6 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>\nendobj\n");
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(Encoding.ASCII.GetBytes(pdf.ToString())), 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        SKColor c2 = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

        // Inverted 0.25 → ~0.75 → ~191; the un-transferred value would be ~64.
        Assert.True(c2.Red > 150, $"transfer function should lighten the fill to ~191, got {c2}");
    }
}
