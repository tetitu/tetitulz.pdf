// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The trailer of the revision <see cref="PdfSigningOperations.AddDocumentTimestamp"/> appends must
/// carry the document's own trailer keys forward — <c>/Info</c> above all — not just the four the
/// writer happened to name.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>PdfDocumentTimestamper</c> hand-spelled its trailer as
/// <c>&lt;&lt; /Size … /Root … /Prev … [ /ID … ] &gt;&gt;</c>. Those four keys were the whole
/// dictionary, so the newest revision of a timestamped file dropped <c>/Info</c> — §14.3.3's
/// document information dictionary — and every producer-specific key with it. Signing the same
/// document through <c>PdfSigner</c> keeps them, because that writer had already moved to
/// <c>Core.Writing.PdfTrailerBuilder.ForAppendedRevision</c>; the two writers therefore produced
/// structurally different last revisions from identical input.</para>
///
/// <para><b>Why it survived.</b> Nothing that reopens the finished file can see it. The parser
/// MERGES trailers along the <c>/Prev</c> chain — first section wins per key — so <c>/Info</c> is
/// still found in the older revision and <c>doc.Trailer</c> looks complete. Only the raw bytes of
/// the last <c>trailer</c> dictionary show the loss, which is what a reader that stops at the
/// newest trailer, or a revision extracted on its own, actually sees. So this test reads those
/// bytes and never goes through <c>PdfDocument</c>.</para>
/// </remarks>
public sealed class TimestampTrailerKeyPreservationTests
{
    [Fact]
    public void TheAppendedRevisionsTrailer_CarriesInfoAndProducerKeysForward()
    {
        using var input = new MemoryStream(PdfWithInfoAndProducerKey());
        using var output = new MemoryStream();

        // No TSA is reachable; the revision — and its trailer — is written before that call.
        Record.Exception(() =>
            PdfSigningOperations.AddDocumentTimestamp(input, output, "http://localhost:1/tsa"));

        byte[] written = output.ToArray();
        Assert.True(written.Length > 0, "no revision was written, so there is no trailer to read");

        string appended = LastTrailerDictionary(written);

        // The two carried-forward keys. Before the fix the appended trailer read
        // "<< /Size 5 /Root 1 0 R /Prev NNN /ID [...] >>" and neither appeared anywhere in it.
        Assert.Contains("/Info 4 0 R", appended, StringComparison.Ordinal);
        Assert.Contains("/TetitulzProducerKey (kept)", appended, StringComparison.Ordinal);

        // And the keys the appended revision restates for itself are still right, so the fix did
        // not buy /Info at the cost of a chain no reader can follow.
        Assert.Contains("/Root 1 0 R", appended, StringComparison.Ordinal);
        // Five objects in (0..4), so the two the timestamper appends make the highest number 6.
        Assert.Contains("/Size 7", appended, StringComparison.Ordinal);
        Assert.Contains("/Prev ", appended, StringComparison.Ordinal);

        // /XRefStm and the ORIGINAL /Prev are deliberately not carried: a new section must not
        // claim a stream it did not write, and the original's /Prev names the section before IT.
        Assert.DoesNotContain("/XRefStm", appended, StringComparison.Ordinal);
    }

    /// <summary>
    /// The text of the dictionary following the LAST <c>trailer</c> keyword in the file — i.e. the
    /// trailer of the revision that was just appended, not the merged view a parser would build.
    /// </summary>
    private static string LastTrailerDictionary(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int kw = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(kw >= 0, "the appended revision wrote no trailer keyword at all");

        int open = text.IndexOf("<<", kw, StringComparison.Ordinal);
        Assert.True(open >= 0, "the last trailer keyword is not followed by a dictionary");

        int depth = 0;
        for (int i = open; i < text.Length - 1; i++)
        {
            if (text[i] == '<' && text[i + 1] == '<') { depth++; i++; }
            else if (text[i] == '>' && text[i + 1] == '>')
            {
                depth--;
                if (depth == 0) return text.Substring(open, i + 2 - open);
                i++;
            }
        }

        Assert.Fail("the last trailer dictionary is unterminated");
        return null;
    }

    /// <summary>
    /// A minimal one-page document whose trailer carries an indirect <c>/Info</c> and one
    /// producer-specific key, alongside the <c>/Size</c> / <c>/Root</c> / <c>/ID</c> the timestamper
    /// used to restate by hand.
    /// </summary>
    private static byte[] PdfWithInfoAndProducerKey()
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj("4 0 obj\n<< /Producer (Tetitulz test) /Title (Row 55) >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat(
            "trailer\n<< /Size {0} /Root 1 0 R /Info 4 0 R /TetitulzProducerKey (kept) " +
            "/ID [<0102030405060708090a0b0c0d0e0f10> <0102030405060708090a0b0c0d0e0f10>] >>\n" +
            "startxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
