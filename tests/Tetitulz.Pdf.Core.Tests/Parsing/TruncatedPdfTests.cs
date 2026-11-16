// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Objects;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>Wave 5.3 + 5.6 — robustness tests for zero-byte, truncated, and
/// large-object-number PDFs. Confirms the parser raises a clean exception
/// (not a crash / stack overflow) in the degenerate cases and succeeds in
/// the large-objnum case.</summary>
public sealed class TruncatedPdfTests
{
    [Fact]
    public void Open_ZeroByteStream_ThrowsCleanException()
    {
        using var ms = new MemoryStream(Array.Empty<byte>());
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(ms));
    }

    [Fact]
    public void Open_HeaderOnly_ThrowsCleanException()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        using var ms = new MemoryStream(bytes);
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(ms));
    }

    /// <summary>
    /// Truncating the tail destroys the cross-reference section, not the objects — every one of
    /// them is still present and locatable — so the recovery scan rebuilds the file rather than
    /// refusing it, and the recovered document has to be the RIGHT one.
    ///
    /// <para>This asserted <c>ThrowsAny&lt;Exception&gt;</c> until the scan learned to take
    /// <c>/Root</c> from a recovered <c>/Type /Catalog</c> object. The throw was never the
    /// behaviour under test: it came from <c>PdfDocument.Load</c> finding no catalog, because the
    /// truncation had eaten half of the <c>trailer</c> dictionary and nothing else looked. Renamed
    /// rather than re-asserted, per CLAUDE.md — the two degenerate cases above still cover
    /// "a clean exception, not a crash".</para>
    /// </summary>
    [Fact]
    public void Open_TruncatedBeforeXref_RecoversEveryObjectByScanning()
    {
        byte[] full = BuildMinimalPdf();
        // Cut off the last 30 bytes — eats the xref + trailer + startxref marker.
        byte[] truncated = new byte[full.Length - 30];
        Buffer.BlockCopy(full, 0, truncated, 0, truncated.Length);
        using var ms = new MemoryStream(truncated);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Single(doc.Pages);
        var mediaBox = doc.Pages[0].MediaBox;
        Assert.Equal(612d, ((PdfNumber)mediaBox[2]).DoubleValue);
        Assert.Equal(792d, ((PdfNumber)mediaBox[3]).DoubleValue);
    }

    /// <summary>
    /// The positive control for the truncation tests above: the <i>whole</i> file must parse into
    /// the objects it declares. A non-null catalog is produced by any code path that reached the
    /// end of <c>Open</c>, including one that recovered by rebuilding an empty document — which is
    /// precisely the outcome a truncation test must not be measured against.
    /// </summary>
    [Fact]
    public void Open_MinimalValidPdf_Succeeds()
    {
        byte[] pdf = BuildMinimalPdf();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.NotNull(doc.Catalog);
        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary[PdfName.Type]);
        Assert.Single(doc.Pages);
        Assert.NotNull(doc.Pages[0].MediaBox);
    }

    [Fact]
    public void Open_LargeObjectNumber_Succeeds()
    {
        // Craft a minimal PDF whose catalog is object number 99_999.
        byte[] pdf = BuildPdfWithObjNumber(99_999);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        Assert.NotNull(doc.Catalog);
        Assert.Single(doc.Pages);
    }

    // ------- helpers -------

    private static byte[] BuildMinimalPdf()
    {
        // PDF 1.4, 4 objects: catalog, pages, page, (no content)
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("%\xFF\xFF\xFF\xFF\n");

        var offsets = new long[5];
        byte[] header = Encoding.Latin1.GetBytes(sb.ToString());
        long pos = header.Length;

        using var ms = new MemoryStream();
        ms.Write(header, 0, header.Length);

        void AppendObj(int num, string body)
        {
            offsets[num] = pos;
            string s = $"{num} 0 obj\n{body}\nendobj\n";
            byte[] bytes = Encoding.Latin1.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
            pos += bytes.Length;
        }

        AppendObj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        AppendObj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        AppendObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        long xrefPos = pos;
        var xs = new StringBuilder();
        xs.Append("xref\n0 4\n");
        xs.Append("0000000000 65535 f \n");
        for (int i = 1; i <= 3; i++)
            xs.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        xs.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n");
        xs.Append(xrefPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
        xs.Append("\n%%EOF\n");
        byte[] tail = Encoding.Latin1.GetBytes(xs.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithObjNumber(int highNum)
    {
        // Catalog = objnum 1, pages = objnum 2, page = objnum highNum.
        // We don't emit free entries for the 99 995 gap — the xref is sparse
        // using subsection headers (`0 3 \n …\n highNum 1 \n …`).
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("%\xFF\xFF\xFF\xFF\n");

        byte[] header = Encoding.Latin1.GetBytes(sb.ToString());
        long pos = header.Length;
        using var ms = new MemoryStream();
        ms.Write(header, 0, header.Length);

        var offsets = new System.Collections.Generic.Dictionary<int, long>();
        void AppendObj(int num, string body)
        {
            offsets[num] = pos;
            string s = $"{num} 0 obj\n{body}\nendobj\n";
            byte[] bytes = Encoding.Latin1.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
            pos += bytes.Length;
        }

        AppendObj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        AppendObj(2, $"<< /Type /Pages /Kids [{highNum} 0 R] /Count 1 >>");
        AppendObj(highNum, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        long xrefPos = pos;
        var xs = new StringBuilder();
        xs.Append("xref\n");
        xs.Append("0 3\n");
        xs.Append("0000000000 65535 f \n");
        xs.AppendFormat("{0:D10} 00000 n \n", offsets[1]);
        xs.AppendFormat("{0:D10} 00000 n \n", offsets[2]);
        xs.AppendFormat("{0} 1\n", highNum);
        xs.AppendFormat("{0:D10} 00000 n \n", offsets[highNum]);
        xs.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n", highNum + 1);
        xs.Append(xrefPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
        xs.Append("\n%%EOF\n");
        byte[] tail = Encoding.Latin1.GetBytes(xs.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }
}
