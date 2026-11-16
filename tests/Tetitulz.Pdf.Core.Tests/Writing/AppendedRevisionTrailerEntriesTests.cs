// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// The trailer <see cref="IncrementalUpdateWriter"/> appends carries the previous trailer's entries.
/// </summary>
/// <remarks>
/// <para><b>ISO 32000-2 §7.5.6: "The added trailer shall contain all the entries except the Prev entry
/// (if present) from the previous trailer, whether modified or not."</b> The writer copied four keys —
/// <c>/Root</c>, <c>/Info</c>, <c>/ID</c>, <c>/Encrypt</c> — and dropped everything else a producer
/// put there, while <see cref="PdfTrailerBuilder.ForUpdate"/>, which the signer uses, kept them. The
/// parser merges trailers along the <c>/Prev</c> chain, so reopening the file cannot see the loss;
/// these tests read the appended trailer's bytes.</para>
/// </remarks>
public sealed class AppendedRevisionTrailerEntriesTests
{
    private static byte[] Original(string trailerExtras)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R ").Append(trailerExtras).Append(" >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Appends one revision that rewrites the page, and returns the new trailer's text.</summary>
    private static string AppendedTrailer(byte[] original)
    {
        using var doc = PdfDocument.Open(new MemoryStream(original));
        var page = (PdfDictionary)doc.GetObject(3);
        var objects = new List<IndirectObject> { new IndirectObject(3, 0, page) };

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output, objects, new IncrementalUpdateOptions { Document = doc });

        string text = Encoding.Latin1.GetString(output.ToArray());
        int trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailer > original.Length, "no trailer was appended");
        return text.Substring(trailer, text.IndexOf("startxref", trailer, StringComparison.Ordinal) - trailer);
    }

    [Fact]
    public void AnEntryAProducerPutInThePreviousTrailer_IsCarriedForward()
    {
        string trailer = AppendedTrailer(Original("/TetitulzProducerKey (kept)"));

        Assert.Contains("/TetitulzProducerKey (kept)", trailer, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleAuthCodeIsNotCarriedForward()
    {
        // ISO/TS 32004 6.5.1: an AuthCode's /ByteRange covers the revision it was written for, and
        // only the most recent revision's is taken into account (B.1). Copied into a revision nothing
        // re-sealed, it would describe bytes that no longer end the file.
        string trailer = AppendedTrailer(Original(
            "/AuthCode << /MACLocation /Standalone /ByteRange [0 10 20 30] /MAC <00> >>"));

        Assert.DoesNotContain("/AuthCode", trailer, StringComparison.Ordinal);
    }
}
