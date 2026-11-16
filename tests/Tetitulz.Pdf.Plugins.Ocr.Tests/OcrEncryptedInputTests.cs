// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Plugins.Ocr;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Ocr.Tests;

/// <summary>
/// An encrypted document no decryptor is registered to open is refused, not read as blank pages:
/// the renderer draws its still-encrypted content, so every page came back empty and the run
/// "succeeded" with no text — a batch indexer could not tell that from a scan with nothing on it.
/// </summary>
public class OcrEncryptedInputTests
{
    private static string MissingTessData =>
        Path.Combine(Path.GetTempPath(), "tetitulz-tessdata-does-not-exist-" + nameof(OcrEncryptedInputTests));

    [Fact]
    public void AnEncryptedDocumentWithNoDecryptor_IsRefused_ByBothDoors()
    {
        Assert.Null(PdfDecryptorHolder.Current);   // this test project registers none
        byte[] pdf = EncryptedPdf();

        var page = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfOcrEngine.OcrPage(pdf, 0, tessDataPath: MissingTessData));
        var document = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfOcrEngine.OcrDocument(pdf, tessDataPath: MissingTessData));

        Assert.Contains("encrypted", page.Message, StringComparison.Ordinal);
        Assert.Contains("encrypted", document.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: an ordinary document is not refused — it gets as far as the missing tessdata,
    /// which both doors report as they always did.
    /// </summary>
    [Fact]
    public void APlainDocument_IsNotRefused_AndReachesTheEngineCheck()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        Assert.Null(PdfOcrEngine.OcrPage(pdf, 0, tessDataPath: MissingTessData));
        Assert.Contains("tessdata", PdfOcrEngine.LastError!, StringComparison.Ordinal);
        Assert.Null(PdfOcrEngine.OcrDocument(pdf, tessDataPath: MissingTessData));
        Assert.Contains("tessdata", PdfOcrEngine.LastError!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A resolution that is not positive is the caller's error, refused at both doors before any
    /// page is read — not every page "failed to render" with the document's text missing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-300)]
    public void ANonPositiveDpi_IsRefusedAsAnArgument_ByBothDoors(int dpi)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        Assert.Equal("dpi", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOcrEngine.OcrPage(pdf, 0, tessDataPath: MissingTessData, dpi: dpi)).ParamName);
        Assert.Equal("dpi", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOcrEngine.OcrDocument(pdf, tessDataPath: MissingTessData, dpi: dpi)).ParamName);

        // The control: a positive one is accepted and reaches the engine check.
        Assert.Null(PdfOcrEngine.OcrDocument(pdf, tessDataPath: MissingTessData, dpi: 1));
        Assert.Contains("tessdata", PdfOcrEngine.LastError!, StringComparison.Ordinal);
    }

    private static byte[] EncryptedPdf()
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 "
            + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
            + "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R /Encrypt 5 0 R /ID [<0102> <0102>] >>\n"
             + $"startxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
