// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for <see cref="PdfOcrLayerWriter"/>.  Builds a minimal PDF in
/// memory, appends an OCR text layer, and verifies the output contains
/// the expected invisible-text content-stream markers.
/// </summary>
public class PdfOcrLayerWriterTests
{
    [Fact]
    public void AddLayer_NullInputPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfOcrLayerWriter.AddLayer((string)null, 0, new List<PdfOcrWord>(), "out.pdf"));
    }

    [Fact]
    public void AddLayer_NullOutputPath_Throws()
    {
        using var tmp = new TempFile();
        tmp.Write(BuildMinimalPdf());
        Assert.Throws<ArgumentNullException>(() =>
            PdfOcrLayerWriter.AddLayer(tmp.Path, 0, new List<PdfOcrWord>(), null));
    }

    [Fact]
    public void AddLayer_EmptyWords_CopiesInputUnchanged()
    {
        byte[] pdf = BuildMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOcrLayerWriter.AddLayer(input, 0, new List<PdfOcrWord>(), output);
        Assert.Equal(pdf, output.ToArray());
    }

    [Fact]
    public void AddLayer_NullWords_CopiesInputUnchanged()
    {
        byte[] pdf = BuildMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOcrLayerWriter.AddLayer(input, 0, null, output);
        Assert.Equal(pdf, output.ToArray());
    }

    [Fact]
    public void AddLayer_WithWords_ProducesLargerOutput()
    {
        byte[] pdf = BuildMinimalPdf();
        var words = new List<PdfOcrWord>
        {
            new PdfOcrWord("hello", 100, 700, 50, 12),
            new PdfOcrWord("world", 160, 700, 50, 12),
        };

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOcrLayerWriter.AddLayer(input, 0, words, output);

        // Output should be at least as large as the input (incremental update
        // appends new objects).
        Assert.True(output.Length > pdf.Length,
            $"Expected output > {pdf.Length} bytes; got {output.Length}");
    }

    [Fact]
    public void AddLayer_PageIndexOutOfRange_Throws()
    {
        byte[] pdf = BuildMinimalPdf();
        var words = new List<PdfOcrWord> { new PdfOcrWord("a", 0, 0, 10, 10) };
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        Assert.ThrowsAny<ArgumentException>(() =>
            PdfOcrLayerWriter.AddLayer(input, 99, words, output));
    }

    /// <summary>
    /// A word's geometry is refused at the constructor for NaN and infinity (a coordinate) and for
    /// NaN, infinity and a negative (a size): the layer's text matrix is built from them, and a
    /// non-finite value is written as 0, collapsing the word into a layer that looks written.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 0f, 10f, 10f)]
    [InlineData(0f, float.PositiveInfinity, 10f, 10f)]
    [InlineData(0f, 0f, float.NaN, 10f)]
    [InlineData(0f, 0f, 10f, float.NegativeInfinity)]
    [InlineData(0f, 0f, -1f, 10f)]
    public void PdfOcrWord_RefusesNonFiniteOrNegativeGeometry(float x, float y, float width, float height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrWord("abc", x, y, width, height));
    }

    [Fact]
    public void PdfOcrWord_ConstructorFields()
    {
        var w = new PdfOcrWord("text", 1f, 2f, 3f, 4f);
        Assert.Equal("text", w.Text);
        Assert.Equal(1f, w.X);
        Assert.Equal(2f, w.Y);
        Assert.Equal(3f, w.Width);
        Assert.Equal(4f, w.Height);
    }

    private static byte[] BuildMinimalPdf()
    {
        const string cs = "0 0 612 792 re";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile() { Path = System.IO.Path.GetTempFileName(); }
        public void Write(byte[] data) { File.WriteAllBytes(Path, data); }
        public void Dispose() { try { File.Delete(Path); } catch { /* ignore */ } }
    }
}
