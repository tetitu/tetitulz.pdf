// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Merging an encrypted source fails closed instead of quietly losing its pages.
///
/// <para><b>Why this exists.</b> <see cref="PdfDocument"/> does not load the catalog or the page
/// tree of an encrypted file, so such a document reports <b>zero pages</b> rather than throwing.
/// <c>PdfMerger</c> had no encrypted-input check, so it simply contributed nothing: merging a
/// one-page plain PDF with a one-page encrypted one returned normally and produced a
/// <b>one-page</b> document. The caller got a valid PDF quietly missing everything the encrypted
/// source was meant to add — the failure mode this repository keeps finding, where "empty" and
/// "refused" are indistinguishable.</para>
///
/// <para>Every other whole-file rewrite here already fails closed on encrypted input
/// (<c>PdfSanitizer</c>, <c>PdfLinearizer</c>, <c>PdfRedactionEngine</c>), with the same exception
/// type and the same "decrypt first, then re-encrypt" guidance.</para>
/// </summary>
public class MergeEncryptedInputTests
{
    /// <summary>The zero-page premise the silent loss depended on — pinned, so it cannot drift.</summary>
    [Fact]
    public void AnEncryptedDocument_ReportsNoPages_RatherThanThrowing()
    {
        using var doc = PdfDocument.Open(new MemoryStream(OnePagePdf(encrypted: true)));
        Assert.True(doc.IsEncrypted);
        Assert.Empty(doc.Pages);
    }

    /// <summary>An encrypted input is refused, and the message says which one.</summary>
    [Fact]
    public void Merge_WithAnEncryptedSource_FailsClosed()
    {
        byte[] plain     = OnePagePdf(encrypted: false);
        byte[] encrypted = OnePagePdf(encrypted: true);

        using var outMs = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfMerger.Merge(outMs, new MemoryStream(plain), new MemoryStream(encrypted)));

        Assert.Contains("Input 1", ex.Message);
    }

    /// <summary>
    /// Two plain documents still merge to two pages — the control, so the guard above cannot pass
    /// by the merger having started refusing everything.
    /// </summary>
    [Fact]
    public void Merge_WithPlainSources_StillProducesEveryPage()
    {
        byte[] plain = OnePagePdf(encrypted: false);

        using var outMs = new MemoryStream();
        PdfMerger.Merge(outMs, new MemoryStream(plain), new MemoryStream(plain));

        using var merged = PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        Assert.Equal(2, merged.Pages.Count);
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
