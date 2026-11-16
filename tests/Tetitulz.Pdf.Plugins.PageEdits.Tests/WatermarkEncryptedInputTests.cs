// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Watermarking an encrypted document is refused, not silently skipped.
///
/// <para><b>Why this exists.</b> <see cref="PdfDocument"/> loads neither the catalog nor the page
/// tree of an encrypted file, so it reports <b>zero pages</b> rather than throwing.
/// <c>PdfPageContentAppender.AppendContentToAllPages</c> iterated that empty collection and the
/// incremental writer emitted the original bytes plus an empty revision — so
/// <c>PdfWatermarker.AddWatermark</c> returned normally, produced a valid PDF of about the input's
/// size, and stamped nothing at all. A DMS marking every outbound file "CONFIDENTIAL" had no
/// exception, no <c>Last*Error</c> and no page count to tell it one document went out unmarked.
/// <c>PdfMerger.Merge</c> already failed closed on the identical trap.</para>
/// </summary>
public class WatermarkEncryptedInputTests
{
    /// <summary>The zero-page premise the silent no-op depended on — pinned, so it cannot drift.</summary>
    [Fact]
    public void AnEncryptedDocument_ReportsNoPages_RatherThanThrowing()
    {
        using var doc = PdfDocument.Open(new MemoryStream(OnePagePdf(encrypted: true)));
        Assert.True(doc.IsEncrypted);
        Assert.Empty(doc.Pages);
    }

    [Fact]
    public void AddWatermark_OnAnEncryptedDocument_FailsClosed()
    {
        using var input  = new MemoryStream(OnePagePdf(encrypted: true));
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfWatermarker.AddWatermark(input, output, new WatermarkOptions { Text = "CONFIDENTIAL" }));

        Assert.Contains("encrypted", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The control: a plain document is still stamped, so the refusal above cannot pass by the
    /// watermarker having started to refuse everything. Asserted on the text the output actually
    /// draws, not on the output being non-empty — an unwatermarked copy is non-empty too, which is
    /// exactly the failure this pair pins.
    /// </summary>
    [Fact]
    public void AddWatermark_OnAPlainDocument_DrawsTheText()
    {
        using var input  = new MemoryStream(OnePagePdf(encrypted: false));
        using var output = new MemoryStream();

        PdfWatermarker.AddWatermark(input, output, new WatermarkOptions { Text = "CONFIDENTIAL" });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Single(doc.Pages);
        Assert.Contains("CONFIDENTIAL", PdfTextExtractor.ExtractText(doc.Pages[0]));
    }

    private static byte[] OnePagePdf(bool encrypted)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        if (encrypted)
            Obj("5 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 "
                + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
                + "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        string enc = encrypted ? " /Encrypt 5 0 R /ID [<0102> <0102>]" : "";
        Emit($"trailer\n<< /Size {size} /Root 1 0 R{enc} >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
