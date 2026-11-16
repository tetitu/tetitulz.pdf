// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// A stamp over every page — a header, a Bates number, a watermark — shares what the pages can
/// share (<see cref="PdfPageContentAppender.ResourceSharing"/>): each page was given a direct copy of
/// its resources with the stamp's merged in, so pages inheriting one dictionary each carried the
/// whole of it — 40 MB from 455 KB at eight hundred pages over eight hundred entries.
/// </summary>
public class SharedStampResourcesTests
{
    private const int Pages = 40, Fonts = 40;

    private static byte[] Document()
    {
        var bodies = new List<string>();
        var kids = new StringBuilder();
        var fonts = new StringBuilder();
        int firstFont = 4, firstPage = firstFont + Fonts, content = firstPage + Pages;
        for (int p = 0; p < Pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        for (int i = 0; i < Fonts; i++) fonts.Append("/F").Append(i).Append(' ').Append(firstFont + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {Pages} /MediaBox [0 0 612 792] /Resources 3 0 R >>");
        bodies.Add($"<< /Font << {fonts}>> >>");
        for (int i = 0; i < Fonts; i++) bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (int p = 0; p < Pages; p++) bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R >>");
        bodies.Add("<< /Length 35 >>\nstream\nBT /F7 12 Tf 72 700 Td (Hi) Tj ET\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// <b>Adversarial: a stamp over forty pages inheriting one resource dictionary of forty fonts.</b>
    /// Every page shows its stamp and keeps the fonts it had; the pages name one merged dictionary,
    /// and the stamp's own font is written once — the revision a small multiple of the input.
    /// </summary>
    [Fact]
    public void AdversarialStampOverSharedResources_SharesTheMergedResources()
    {
        byte[] input = Document();
        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContentToAllPages(new MemoryStream(input), output,
            (b, info) =>
            {
                string font = b.AddStandardFont("Helvetica");
                b.BeginText().SetFont(font, 10).SetTextPosition(36, 36).ShowText("STAMP").EndText();
            });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var resources = new List<PdfObject>();
        foreach (var page in doc.Pages)
        {
            resources.Add(page.Dictionary[PdfName.Resources]);
            var fonts = doc.ResolveObject(page.Resources?[PdfName.Font]) as PdfDictionary;
            Assert.Equal(Fonts + 1, fonts?.Count ?? 0);                                    // its own and the stamp's
            Assert.Contains("STAMP", PdfTextExtractor.ExtractText(page), System.StringComparison.Ordinal);
        }
        Assert.Single(resources.OfType<PdfReference>().Select(r => r.ReferenceObjectNumber).Distinct());
        Assert.True(output.Length < 3 * input.Length, $"the output is {output.Length} bytes from {input.Length}");
    }
}
