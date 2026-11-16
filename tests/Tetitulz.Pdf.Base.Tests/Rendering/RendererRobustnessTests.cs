// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Robustness guards (Phase 5): a malformed content stream with pathological/unbalanced
/// graphics-state nesting must not exhaust memory; rendering still completes and paints.
/// </summary>
public class RendererRobustnessTests
{
    [Fact]
    public void DeeplyUnbalancedQ_DoesNotExhaustMemory_AndStillPaints()
    {
        // 20,000 unbalanced `q` then a red page fill. The GS-stack cap bounds memory; the
        // fill (CTM unchanged by q) must still render red.
        string content = string.Concat(Enumerable.Repeat("q\n", 20000)) +
                         "1 0 0 rg\n0 0 612 792 re f";
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(contentStream: content);

        byte[] png = PdfOperations.RenderPageToPng(new MemoryStream(pdf), 0, dpi: 36);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        SKColor c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.True(c.Red > 200 && c.Green < 80 && c.Blue < 80,
            $"page fill should still paint red despite pathological q nesting, got {c}");
    }
}
