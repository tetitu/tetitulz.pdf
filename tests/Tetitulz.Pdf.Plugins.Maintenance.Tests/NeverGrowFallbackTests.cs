// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="OptimizationOptions.NeverGrow"/> promises that when the optimized form is not smaller
/// the caller gets the input back <i>unchanged</i>, and every preset turns it on. The copy it hands
/// back is sized from <c>Stream.Length</c> and filled by a read loop that used to break on a short
/// read without checking the count, so any stream whose <c>Length</c> over-reports left the
/// shortfall as the zero bytes <c>new byte[]</c> supplied — and the fallback then wrote a NUL-padded
/// file while reporting <c>FellBackToInput</c> and a byte-identical size.
/// </summary>
public class NeverGrowFallbackTests
{
    /// <summary>
    /// The output must be a PDF, not the input plus a run of NULs. Asserting on
    /// <c>FellBackToInput</c> alone would not catch it: the corrupt path sets that flag too — that
    /// is the whole defect.
    /// </summary>
    [Fact]
    public void NeverGrow_DoesNotHandBackANulPaddedCopyWhenTheStreamOverReportsItsLength()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        DocumentEditDiagnostics.Clear();

        using var input = new OverReportingStream(pdf, phantomBytes: 512);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output,
            new OptimizationOptions { NeverGrow = true });

        byte[] written = output.ToArray();

        Assert.False(result.FellBackToInput,
            "the retained copy was not the input, so it must not be handed back as if it were");
        Assert.True(result.ToleratedFailures > 0, "the shortfall has to reach the caller");
        Assert.Contains("delivered", DocumentEditDiagnostics.LastError);

        // The observable damage: 512 zero bytes past the end of the document.
        Assert.False(EndsWithNulRun(written, 16),
            "the output ends in a run of NUL bytes, i.e. it is the padded copy");

        // And what came out is still a readable PDF.
        using var reopened = PdfDocument.Open(new MemoryStream(written));
        Assert.NotEmpty(reopened.Pages);
    }

    /// <summary>
    /// The same call over an honest stream still takes the fallback, so the test above is the short
    /// read being detected rather than <c>NeverGrow</c> having stopped working.
    /// </summary>
    [Fact]
    public void NeverGrow_StillHandsBackTheInputWhenTheStreamReportsItsLengthHonestly()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var input = new OverReportingStream(pdf, phantomBytes: 0);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output,
            new OptimizationOptions { NeverGrow = true });

        // The fixture is a hand-written minimal PDF, which a rewrite does not shrink.
        Assert.True(result.FellBackToInput,
            "the fixture no longer exercises the fallback at all");
        Assert.Equal(pdf, output.ToArray());
    }

    private static bool EndsWithNulRun(byte[] data, int runLength)
    {
        if (data.Length < runLength) return false;
        for (int i = data.Length - runLength; i < data.Length; i++)
        {
            if (data[i] != 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Seekable — so <c>PdfHelpers.EnsureSeekable</c> hands it straight through rather than copying
    /// it into a right-sized <see cref="MemoryStream"/> — and reports <paramref name="phantomBytes"/>
    /// more than it will ever deliver. That models a file truncated by another process between the
    /// length read and the copy, and any shim over a chunked or compressed source whose length is an
    /// estimate.
    /// </summary>
    private sealed class OverReportingStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _phantom;

        internal OverReportingStream(byte[] data, int phantomBytes)
        {
            _inner = new MemoryStream(data, writable: false);
            _phantom = phantomBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length + _phantom;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = Math.Min(value, _inner.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _inner.Position + offset,
                _ => Length + offset,
            };
            _inner.Position = Math.Max(0, Math.Min(target, _inner.Length));
            return _inner.Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
