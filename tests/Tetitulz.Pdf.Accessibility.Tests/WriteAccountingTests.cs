// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Every accessibility writer has to answer the same two questions honestly:
/// <b>did the revision I planned actually reach the output?</b> and
/// <b>was there anything to write in the first place?</b>
///
/// <para>
/// Both are easy to get wrong in the same direction — optimistically. The incremental writer is
/// documented never to throw: it catches every failure, reports it through
/// <c>AccessibilityDiagnostics</c> and leaves the output holding just the original bytes. A caller
/// that assembles its result from what it <i>planned</i> therefore reports a fully remediated
/// document about a file that is byte-for-byte the input. And an operation that appends
/// unconditionally grows the file by a no-op revision every time it is re-run over its own output.
/// </para>
///
/// <para>
/// These tests pin both directions for the two writers where they were last outstanding:
/// <see cref="PdfArtifactMarker"/> (write accounting) and <see cref="PdfUaXmpWriter"/>
/// (idempotence). The same shape is covered for <c>PdfAutoTagger</c> in
/// <see cref="PdfAutoTaggerTests"/>.
/// </para>
/// </summary>
public class WriteAccountingTests
{
    // ================================================================
    //  PdfArtifactMarker — the result must describe what was written
    // ================================================================

    /// <summary>
    /// The marking pass counted its artifacts before handing the edit to the incremental writer
    /// and returned those counts unconditionally afterwards. Against an output stream that fails
    /// while the appended revision is copied out — a disk filling up — the writer swallows the
    /// <see cref="IOException"/> by design, so the caller was told "N artifacts marked, document
    /// changed" about a file that is byte-for-byte the unmarked input.
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_WhenTheRevisionNeverReachesTheOutput_ReportsZeroAndWarns()
    {
        byte[] input = HeaderFooterPdf();

        // Control first, so the assertions below are about the write failing rather than about a
        // document that was never going to be marked.
        using (var healthyIn = new MemoryStream(input))
        using (var healthyOut = new MemoryStream())
        {
            ArtifactMarkDetail healthy =
                PdfArtifactMarker.MarkHeadersAndFootersDetailed(healthyIn, healthyOut);
            Assert.True(healthy.ArtifactsMarked > 0);
            Assert.True(healthy.DocumentChanged);
            Assert.True(healthyOut.Length > input.Length);
        }

        // Now the same run against a sink that accepts exactly the original document and then
        // fails. The original bytes are written first and the revision second, so this is precisely
        // the "writer fell back to the original" condition.
        var sink = new FailsAfterStream(input.Length);
        ArtifactMarkDetail result;
        using (var failingIn = new MemoryStream(input))
            result = PdfArtifactMarker.MarkHeadersAndFootersDetailed(failingIn, sink);

        // What was actually produced is the unmarked input, unchanged.
        Assert.Equal(input, sink.Accepted());

        // So the result must say so, rather than describing artifacts that do not exist.
        Assert.Equal(0, result.ArtifactsMarked);
        Assert.Equal(0, result.PagesChanged);
        Assert.False(result.DocumentChanged);

        // The page whose rewrite was lost is accounted for, not silently dropped from the tally.
        Assert.Equal(1, result.PagesProcessed);
        Assert.Equal(1, result.PagesSkipped);
        Assert.Equal(
            result.PagesProcessed,
            result.PagesChanged + result.PagesSkipped + result.PagesWithNothingToMark);

        Assert.Contains(result.Warnings,
            w => w.IndexOf("never reached the output stream", StringComparison.Ordinal) >= 0);
    }

    /// <summary>The narrow overload must not report a count the detailed one refuses to.</summary>
    [Fact]
    public void MarkHeadersAndFooters_NarrowOverload_AlsoReportsZeroWhenTheWriteFails()
    {
        byte[] input = HeaderFooterPdf();

        var sink = new FailsAfterStream(input.Length);
        ArtifactMarkResult result;
        using (var failingIn = new MemoryStream(input))
            result = PdfArtifactMarker.MarkHeadersAndFooters(failingIn, sink);

        Assert.Equal(0, result.ArtifactsMarked);
    }

    /// <summary>
    /// The positive direction: a run that succeeds reports the number of artifact sequences that
    /// are really in the output, and the output really differs from the input. Counting
    /// <c>/Artifact</c> in the produced content stream is what makes "reports the real count" an
    /// assertion rather than a restatement of the return value.
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_SuccessfulRun_ReportsTheCountThatIsActuallyInTheOutput()
    {
        byte[] input = HeaderFooterPdf();

        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        ArtifactMarkDetail result =
            PdfArtifactMarker.MarkHeadersAndFootersDetailed(inStream, outStream);

        byte[] output = outStream.ToArray();

        Assert.True(result.DocumentChanged);
        Assert.Equal(1, result.PagesProcessed);
        Assert.Equal(1, result.PagesChanged);
        Assert.Equal(0, result.PagesSkipped);
        // The output differs from the input: an appended revision can only grow the file, and the
        // content assertions below show the growth is the marking rather than padding.
        Assert.True(output.Length > input.Length);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        string content = DecodePageContent(doc, 0);

        // One "/Artifact" tag per marked-content artifact sequence. This is what makes the count
        // "real": it is compared against the document, not against the value that produced it.
        Assert.True(result.ArtifactsMarked > 0,
            "the header at y=760 and the footer at y=30 sit inside the default 50 pt bands");
        Assert.Equal(result.ArtifactsMarked, CountOccurrences(content, "/Artifact"));
    }

    /// <summary>
    /// A document with nothing in the header and footer bands is not a failure: no revision is
    /// needed, the input is copied through, and zero is the honest count with no warning about a
    /// lost write.
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_NothingInTheBands_CopiesThroughWithoutClaimingAFailedWrite()
    {
        byte[] input = HeaderFooterPdf();

        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        ArtifactMarkDetail result = PdfArtifactMarker.MarkHeadersAndFootersDetailed(
            inStream, outStream, headerMarginPt: 0f, footerMarginPt: 0f);

        Assert.Equal(0, result.ArtifactsMarked);
        Assert.False(result.DocumentChanged);
        Assert.Equal(input, outStream.ToArray());
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("never reached the output stream", StringComparison.Ordinal) >= 0);
    }

    // ================================================================
    //  PdfUaXmpWriter — a second run must cost nothing
    // ================================================================

    /// <summary>
    /// The packet builder is deterministic, so re-running the operation over its own output would
    /// append a byte-identical <c>/Metadata</c> stream for nothing. The remediation pipeline gates
    /// the step on exactly this comparison; the standalone public API did not, so a caller who ran
    /// it twice grew the file by a no-op revision each time.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_RunTwice_SecondOutputIsByteIdenticalToTheFirst()
    {
        byte[] input = HeaderFooterPdf();

        byte[] first;
        using (var inStream = new MemoryStream(input))
        using (var outStream = new MemoryStream())
        {
            Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(
                inStream, outStream, part: 1, title: "Quarterly Report", language: "en-US"));
            Assert.True(PdfUaXmpWriter.LastCallAppendedRevision);
            first = outStream.ToArray();
        }

        Assert.True(first.Length > input.Length);

        byte[] second;
        using (var inStream = new MemoryStream(first))
        using (var outStream = new MemoryStream())
        {
            // Still true: the output does declare pdfuaid:part. It just did not have to be written.
            Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(
                inStream, outStream, part: 1, title: "Quarterly Report", language: "en-US"));
            Assert.False(PdfUaXmpWriter.LastCallAppendedRevision);
            Assert.Null(PdfUaXmpWriter.LastError);
            second = outStream.ToArray();
        }

        Assert.Equal(first, second);
    }

    /// <summary>
    /// The no-op detection must not swallow a real edit: asking for a <i>different</i> packet on a
    /// document that already carries one still appends a revision.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_SecondRunWithDifferentValues_StillAppendsARevision()
    {
        byte[] input = HeaderFooterPdf();

        byte[] first;
        using (var inStream = new MemoryStream(input))
        using (var outStream = new MemoryStream())
        {
            Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(
                inStream, outStream, part: 1, title: "First Title", language: "en-US"));
            first = outStream.ToArray();
        }

        byte[] second;
        using (var inStream = new MemoryStream(first))
        using (var outStream = new MemoryStream())
        {
            Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(
                inStream, outStream, part: 1, title: "Second Title", language: "en-US"));
            Assert.True(PdfUaXmpWriter.LastCallAppendedRevision);
            second = outStream.ToArray();
        }

        Assert.True(second.Length > first.Length);
        Assert.Contains("Second Title", ReadXmpPacket(second), StringComparison.Ordinal);
    }

    /// <summary>
    /// A document that genuinely needs the packet gets exactly one appended revision — not zero
    /// (which the no-op check must never cause) and not two.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_DocumentWithoutAPacket_WritesExactlyOneRevision()
    {
        byte[] input = HeaderFooterPdf();
        Assert.Equal(1, CountOccurrences(Encoding.ASCII.GetString(input), "startxref"));

        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(inStream, outStream, part: 1));
        Assert.True(PdfUaXmpWriter.LastCallAppendedRevision);

        byte[] output = outStream.ToArray();

        // One appended revision === one further cross-reference section.
        Assert.Equal(2, CountOccurrences(Encoding.ASCII.GetString(output), "startxref"));

        string packet = ReadXmpPacket(output);
        Assert.Contains("pdfuaid:part", packet, StringComparison.Ordinal);
        Assert.Contains(PdfUaXmpWriter.PdfUaIdNamespace, packet, StringComparison.Ordinal);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>
    /// Untagged, uncompressed, single-page PDF (MediaBox 612x792) drawing a header at y=760 —
    /// inside the default 50 pt top band — three body lines in the middle, and a footer at y=30,
    /// inside the bottom band. Built in memory so the test depends on no file on disk.
    /// </summary>
    private static byte[] HeaderFooterPdf()
    {
        var content = new StringBuilder();
        content.Append("BT /F1 9 Tf 72 760 Td (Confidential Header) Tj ET\n");
        content.Append("BT /F1 10 Tf 72 650 Td (Body line one of the paragraph.) Tj ET\n");
        content.Append("BT /F1 10 Tf 72 638 Td (Body line two of the paragraph.) Tj ET\n");
        content.Append("BT /F1 10 Tf 72 626 Td (Body line three of the paragraph.) Tj ET\n");
        content.Append("BT /F1 9 Tf 72 30 Td (Page 1 of 1) Tj ET\n");
        string cs = content.ToString();

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ")
          .Append("/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        int off4 = sb.Length;
        sb.Append("4 0 obj\n<< /Length ").Append(cs.Length.ToString(CultureInfo.InvariantCulture))
          .Append(" >>\nstream\n").Append(cs).Append("endstream\nendobj\n");
        int off5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica ")
          .Append("/Encoding /WinAnsiEncoding >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off1);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off2);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off3);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off4);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    /// <summary>Decodes the (possibly array-valued) /Contents of one page.</summary>
    private static string DecodePageContent(PdfDocument doc, int pageIndex)
    {
        PdfDictionary page = doc.Pages[pageIndex].Dictionary;
        PdfObject contents = doc.ResolveObject(page.Get(new PdfName("Contents")));

        var buffer = new MemoryStream();
        if (contents is PdfArray array)
        {
            foreach (PdfObject part in array)
            {
                if (doc.ResolveObject(part) is PdfStream partStream)
                {
                    byte[] decoded = partStream.GetDecodedData();
                    buffer.Write(decoded, 0, decoded.Length);
                    buffer.WriteByte((byte)'\n');
                }
            }
        }
        else if (contents is PdfStream stream)
        {
            byte[] decoded = stream.GetDecodedData();
            buffer.Write(decoded, 0, decoded.Length);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>Decodes the catalog's /Metadata XMP packet from a produced document.</summary>
    private static string ReadXmpPacket(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfDictionary catalog = doc.Catalog?.Dictionary;
        Assert.NotNull(catalog);

        var metadata = doc.ResolveObject(catalog.Get(new PdfName("Metadata"))) as PdfStream;
        Assert.NotNull(metadata);
        return Encoding.UTF8.GetString(metadata.GetDecodedData());
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Write-only sink that accepts the first <c>limit</c> bytes and then throws
    /// <see cref="IOException"/> — a disk filling up part-way through the copy. Everything it
    /// accepted is retained so a test can compare the produced file with the input.
    /// <see cref="CanSeek"/> is false, which is also the harder case for the writer.
    /// </summary>
    private sealed class FailsAfterStream : Stream
    {
        private readonly MemoryStream _accepted = new MemoryStream();
        private readonly long _limit;

        internal FailsAfterStream(long limit) => _limit = limit;

        /// <summary>Exactly the bytes the caller managed to write.</summary>
        internal byte[] Accepted() => _accepted.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length => _accepted.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Reject the whole write rather than truncating it: a stream that accepted part of a
            // buffer and then failed would make the test's byte-for-byte comparison ambiguous.
            if (_accepted.Length + count > _limit)
                throw new IOException("There is not enough space on the disk.");

            _accepted.Write(buffer, offset, count);
        }
    }
}
