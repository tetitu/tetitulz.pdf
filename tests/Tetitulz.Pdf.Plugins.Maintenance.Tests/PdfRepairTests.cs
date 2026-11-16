// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

public sealed class PdfRepairTests
{
    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// Corrupts a well-formed PDF by overwriting the xref section with garbage
    /// so that the standard parser cannot load it, but the object bytes remain intact.
    /// </summary>
    private static byte[] CorruptXref(byte[] pdf)
    {
        byte[] result = (byte[])pdf.Clone();
        // Find "xref" and overwrite 50 bytes starting there with 'X'.
        byte[] marker = Encoding.ASCII.GetBytes("xref");
        for (int i = result.Length - marker.Length - 1; i >= 0; i--)
        {
            bool m = true;
            for (int j = 0; j < marker.Length; j++)
                if (result[i + j] != marker[j]) { m = false; break; }
            if (!m) continue;
            int count = Math.Min(50, result.Length - i);
            for (int k = 0; k < count; k++) result[i + k] = (byte)'X';
            break;
        }
        return result;
    }

    // ----------------------------------------------------------------
    //  Repair — well-formed input
    // ----------------------------------------------------------------

    [Fact]
    public void Repair_WellFormedPdf_OutputStartsWithPdfHeader()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);

        output.Position = 0;
        byte[] result = output.ToArray();
        Assert.True(result.Length > 0);
        string header = Encoding.ASCII.GetString(result, 0, Math.Min(8, result.Length));
        Assert.StartsWith("%PDF-", header);
    }

    [Fact]
    public void Repair_WellFormedPdf_RecoveredObjectCountPositive()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        Assert.True(result.RecoveredObjectCount > 0);
    }

    [Fact]
    public void Repair_WellFormedPdf_OutputContainsEof()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);

        string text = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("%%EOF", text);
    }

    [Fact]
    public void Repair_WellFormedPdf_OutputContainsXref()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);

        string text = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("xref", text);
        Assert.Contains("startxref", text);
    }

    [Fact]
    public void Repair_WellFormedPdf_OutputContainsTrailerWithRoot()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);

        string text = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("/Root", text);
    }

    [Fact]
    public void Repair_WellFormedPdf_IsCleanNoErrors()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        Assert.True(result.IsClean);
        Assert.Empty(result.Errors);
    }

    // ----------------------------------------------------------------
    //  Repair — corrupted xref
    // ----------------------------------------------------------------

    [Fact]
    public void Repair_CorruptedXref_OutputHasPositiveLength()
    {
        byte[] pdf       = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] corrupted = CorruptXref(pdf);

        using var input  = new MemoryStream(corrupted);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void Repair_CorruptedXref_RecoversSomeObjects()
    {
        byte[] pdf       = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] corrupted = CorruptXref(pdf);

        using var input  = new MemoryStream(corrupted);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        Assert.True(result.RecoveredObjectCount > 0);
    }

    [Fact]
    public void Repair_CorruptedXref_OutputStartsWithPdfHeader()
    {
        byte[] pdf       = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] corrupted = CorruptXref(pdf);

        using var input  = new MemoryStream(corrupted);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);

        string header = Encoding.ASCII.GetString(output.ToArray(), 0,
            Math.Min(8, (int)output.Length));
        Assert.StartsWith("%PDF-", header);
    }

    // ----------------------------------------------------------------
    //  Repair — multi-page PDF
    // ----------------------------------------------------------------

    [Fact]
    public void Repair_MultiPagePdf_RecoversManyObjects()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        // 3-page PDF has at minimum: catalog, pages, 3 page dicts, 3 content streams, font = 9+ objs
        Assert.True(result.RecoveredObjectCount >= 5);
    }

    // ----------------------------------------------------------------
    //  Repair — file-path overload
    // ----------------------------------------------------------------

    [Fact]
    public void Repair_FilePaths_WritesOutputFile()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        string src = TempFiles.NewPath();
        string dst = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(src, pdf);
            var result = PdfRepair.Repair(src, dst);
            Assert.True(File.Exists(dst));
            Assert.True(new FileInfo(dst).Length > 0);
            Assert.True(result.RecoveredObjectCount > 0);
        }
        finally
        {
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    // ----------------------------------------------------------------
    //  PdfRepairResult API
    // ----------------------------------------------------------------

    [Fact]
    public void RepairResult_ToString_ContainsObjectCount()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        Assert.Contains(result.RecoveredObjectCount.ToString(), result.ToString());
    }

    // ----------------------------------------------------------------
    //  Error / null input
    // ----------------------------------------------------------------

    [Fact]
    public void Repair_NullInputStream_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => PdfRepair.Repair((Stream)null!, new MemoryStream()));

    [Fact]
    public void Repair_NullOutputStream_Throws()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentNullException>(
            () => PdfRepair.Repair(ms, (Stream)null!));
    }
}
