// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// <c>/BrotliDecode</c>: a raw Brotli stream (RFC 7932), the filter the PDF Association proposed for
/// PDF 2.0 and another reader reads — with Flate's predictor parameters, a custom dictionary refused by name,
/// and its output bounded by the stage's ceiling. Until 2026-10-17 the pipeline passed such a stream
/// through undecoded and named the filter unknown, so a document compressed with it opened with no
/// pages (another reader's <c>Brotli-Prototype-FileA</c>).
/// </summary>
public class BrotliDecodeFilterTests
{
    private static byte[] Compress(byte[] plain) => RuntimeBrotli(plain, 5, 22);

    private static PdfName Name(string value) => new PdfName(value);

    [Fact]
    public void ABrotliStream_DecodesThroughThePipeline_ByItsName()
    {
        // Varied, and past the first 16 KiB output segment: every segment must reach the answer (a
        // fixture inside one segment, or of zeros, cannot see a dropped one).
        byte[] plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(i => "BT /F1 12 Tf 72 " + i + " Td (Brotli " + i * 7919 % 10007 + ") Tj ET\n")));
        Assert.True(plain.Length > 64 * 1024, "the fixture is " + plain.Length + " bytes");
        FilterPipeline.ResetLastUnknownFilterName();

        byte[] decoded = FilterPipeline.Decode(Compress(plain), PdfName.BrotliDecode, null);

        Assert.Equal(plain, decoded);
        Assert.Null(FilterPipeline.LastUnknownFilterName);
    }

    /// <summary>
    /// The PNG Up predictor (§7.4.4.4, <c>/Predictor 12</c>) is undone after decompression, as for
    /// Flate: two rows of three bytes, the second stored as its difference from the first.
    /// </summary>
    [Fact]
    public void ThePredictorParameters_AreFlates()
    {
        byte[] predicted = { 2, 10, 20, 30, 2, 1, 1, 1 };   // filter byte 2 (Up) on each row
        var parms = new PdfDictionary();
        parms[Name("Predictor")] = new PdfNumber(12);
        parms[Name("Columns")] = new PdfNumber(3);

        byte[] decoded = FilterPipeline.Decode(Compress(predicted), PdfName.BrotliDecode, parms);

        Assert.Equal(new byte[] { 10, 20, 30, 11, 21, 31 }, decoded);
    }

    [Fact]
    public void ACustomDictionary_IsRefusedByName()
    {
        var parms = new PdfDictionary();
        parms[Name("D")] = new PdfReference(9, 0);

        var ex = Assert.Throws<PdfFilterException>(
            () => new BrotliDecodeFilter().Decode(Compress(new byte[] { 1, 2, 3 }), parms));
        Assert.Contains("/D", ex.Message, StringComparison.Ordinal);

        Assert.Equal(new byte[] { 1, 2, 3 }, new BrotliDecodeFilter().Decode(Compress(new byte[] { 1, 2, 3 }), new PdfDictionary()));
    }

    /// <summary>
    /// A few hundred bytes that expand past the stage's ceiling are refused at the ceiling, not after
    /// inflating everything; the same stream under a ceiling it fits decodes.
    /// </summary>
    [Fact]
    public void AnAdversarialExpansion_IsStoppedAtTheCeiling()
    {
        byte[] bomb = Compress(new byte[4 * 1024 * 1024]);
        Assert.True(bomb.Length < 1024, "the fixture compresses to " + bomb.Length + " bytes");

        var ex = Assert.Throws<PdfFilterException>(() => FilterPipeline.Decode(bomb, PdfName.BrotliDecode, null, 64 * 1024));
        Assert.Contains("65536-byte decode limit", ex.Message, StringComparison.Ordinal);

        Assert.Equal(4 * 1024 * 1024, FilterPipeline.Decode(bomb, PdfName.BrotliDecode, null, 8 * 1024 * 1024).Length);
    }

    [Fact]
    public void CorruptBrotliData_IsAPdfException()
    {
        byte[] good = Compress(Encoding.ASCII.GetBytes(new string('x', 5000)));
        byte[] bad = (byte[])good.Clone();
        for (int i = 2; i < bad.Length; i += 3) bad[i] ^= 0x5A;

        Assert.ThrowsAny<Tetitulz.Pdf.Core.PdfException>(() => new BrotliDecodeFilter().Decode(bad, null));
    }

    /// <summary>A page whose content stream is Brotli-compressed is read: its text comes back.</summary>
    [Fact]
    public void APageWithABrotliContentStream_ExtractsItsText()
    {
        byte[] content = Compress(Encoding.ASCII.GetBytes("BT /F1 24 Tf 72 700 Td (Hello Brotli) Tj ET"));
        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new int[6];
        var bytes = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); bytes.Write(b, 0, b.Length); }
        Write("%PDF-2.0\n");
        offsets[1] = (int)bytes.Length; Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = (int)bytes.Length; Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = (int)bytes.Length; Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        offsets[4] = (int)bytes.Length; Write("4 0 obj\n<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " /Filter /BrotliDecode >>\nstream\n");
        bytes.Write(content, 0, content.Length); Write("\nendstream\nendobj\n");
        offsets[5] = (int)bytes.Length; Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = (int)bytes.Length;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int n = 1; n <= 5; n++) Write(offsets[n].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        using var doc = PdfDocument.Open(new MemoryStream(bytes.ToArray()));
        Assert.Contains("Hello Brotli", PdfTextExtractor.ExtractText(doc), StringComparison.Ordinal);
    }

    /// <summary>The runtime's Brotli encoder (Google's, inside .NET) — an encoder the decoder under test shares nothing with.</summary>
    private static byte[] RuntimeBrotli(byte[] plain, int quality, int window)
    {
        var destination = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(plain.Length)];
        if (!System.IO.Compression.BrotliEncoder.TryCompress(plain, destination, out int written, quality, window))
            throw new System.InvalidOperationException("the runtime's Brotli encoder declined the fixture");
        return destination.AsSpan(0, written).ToArray();
    }
}
