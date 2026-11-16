// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The render's thread budget reaches the JPEG 2000 decoder (the review of 2026-09-26, row 25):
/// a page render forwards <see cref="PdfRenderOptions.MaxRenderThreads"/> as the decode's cap, and
/// a batch render's page workers decode on their own thread — the batch already parallelises by
/// page, and every worker starting the decoder's automatic degree ran N times up to sixteen
/// threads. The evidence is <see cref="JpxImageDecoder.LastDecodeThreads"/>, read on the thread
/// that decoded.
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class JpxDecodeThreadsTests : IDisposable
{
    private const int Size = 256;

    public JpxDecodeThreadsTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void APageRender_ForwardsMaxRenderThreads_AsTheDecodesCap(int threads)
    {
        using var ms = new MemoryStream(JpxTestPdf.ImagePdf(Jpx(), Size, Size, ""));
        PdfPageRenderer.RenderPage(ms, 0, 72, new PdfRenderOptions { MaxRenderThreads = threads });
        Assert.Equal(threads, JpxImageDecoder.LastDecodeThreads);
    }

    /// <summary>
    /// A batch of two pages on two workers: each page's decode runs at degree 1, whatever
    /// <c>MaxRenderThreads</c> says; the control is the sequential batch of one page, where the
    /// option is the page render's cap and the decode runs at 3. The sink runs on the worker's
    /// thread, where the slot is read.
    /// </summary>
    [Fact]
    public void ABatchRender_DecodesOnEachWorkersThread()
    {
        byte[] jpx = Jpx();
        var degrees = new int[2];
        PdfPageRenderer.RenderAllPagesCore(TwoPages(jpx), 72, PdfImageFormat.Png, 90,
            new PdfRenderOptions { MaxRenderThreads = 2 }, CancellationToken.None,
            (page, bytes) => degrees[page] = JpxImageDecoder.LastDecodeThreads);
        Assert.Equal(new[] { 1, 1 }, degrees);

        int single = 0;
        PdfPageRenderer.RenderAllPagesCore(JpxTestPdf.ImagePdf(jpx, Size, Size, ""), 72, PdfImageFormat.Png, 90,
            new PdfRenderOptions { MaxRenderThreads = 3 }, CancellationToken.None,
            (page, bytes) => single = JpxImageDecoder.LastDecodeThreads);
        Assert.Equal(3, single);
    }

    private static byte[] Jpx()
    {
        var samples = new byte[Size * Size * 3];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7 + (i / 3) % 11);
        return JpxImageEncoding.Encode(samples, Size, Size, 3, 8, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 3, Jp2Container = false });
    }

    /// <summary>Two pages, each the image over its whole area, sharing one image XObject (object 6).</summary>
    private static byte[] TwoPages(byte[] jpx)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }
        string content = $"q {Size} 0 0 {Size} 0 0 cm /Im0 Do Q";
        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Size} {Size}] /Resources << /XObject << /Im0 6 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Size} {Size}] /Resources << /XObject << /Im0 6 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        Obj($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets.Add(acc.Count);
        Emit($"6 0 obj\n<< /Type /XObject /Subtype /Image /Width {Size} /Height {Size} /Filter /JPXDecode /Length {jpx.Length} >>\nstream\n");
        acc.AddRange(jpx);
        Emit("\nendstream\nendobj\n");
        int xref = acc.Count;
        Emit($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
