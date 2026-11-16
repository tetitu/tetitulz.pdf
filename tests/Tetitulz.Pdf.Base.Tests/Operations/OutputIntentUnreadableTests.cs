// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfIccEmbedder.WriteOutputIntent"/> refuses an <c>/OutputIntents</c> it cannot read.
/// Read as none, the new intent replaced the document's own, a PDF/X file's among them.
/// </summary>
public sealed class OutputIntentUnreadableTests
{
    [Fact]
    public void Adversarial_unreadable_output_intents_refuse_a_new_intent()
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfIccEmbedder.WriteOutputIntent(new MemoryStream(Pdf(readable: false)), output, new OutputIntentSpec()));

        Assert.Contains("/OutputIntents could not be read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: a readable array keeps its PDF/X intent beside the new PDF/A one.</summary>
    [Fact]
    public void A_readable_array_keeps_its_other_intents()
    {
        var output = new MemoryStream();
        PdfIccEmbedder.WriteOutputIntent(new MemoryStream(Pdf(readable: true)), output, new OutputIntentSpec());

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var intents = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OutputIntents]));
        var subtypes = Enumerable.Range(0, intents.Count)
            .Select(i => ((doc.ResolveObject(intents[i]) as PdfDictionary)?[PdfName.S] as PdfName)?.Value)
            .ToList();
        Assert.Equal(new[] { "GTS_PDFX", "GTS_PDFA1" }, subtypes);
    }

    /// <summary>
    /// A one-page document whose catalog names object 4 as its <c>/OutputIntents</c>: an array holding
    /// a PDF/X intent (object 5), or a lone <c>]</c> the parser cannot read.
    /// </summary>
    private static byte[] Pdf(bool readable)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /OutputIntents 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            readable ? "[5 0 R]" : "]",
            "<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (FOGRA39) >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
