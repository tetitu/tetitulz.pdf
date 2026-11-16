// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Operations that took a <c>Stream</c> still work when that stream can only be read once, front
/// to back — and do not close it.
/// </summary>
/// <remarks>
/// <para>This is the regression suite for the <c>EnsureSeekable</c> consolidation. Twenty-five call
/// sites wrapped their argument in a seekable view purely to hand it to
/// <c>PdfDocument.Open</c> — which buffers a non-seekable stream <i>itself</i>, and owns the copy,
/// while <c>PdfParser</c> seeks absolutely from <c>startxref</c> and so never reads from the
/// incoming position. The wrapper therefore bought nothing and cost a buffer nobody disposed. The
/// calls are gone; these tests are what says the forward-only path still works without them.</para>
///
/// <para>The remaining six sites genuinely read the stream twice — parse, then serve as the
/// incremental writer's base — and now ask
/// <c>Core.IO.SeekableStream.Ensure(input, rewind, out bool owned)</c> so they can release the
/// buffer they caused. <b>Each case here checks the caller's own stream is still open afterwards</b>,
/// which is the failure mode a naive "dispose what you were given" fix would have introduced.</para>
/// </remarks>
public class ForwardOnlyInputTests
{
    [Fact]
    public void TextExtractionReadsAForwardOnlyStream()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Forward only");

        using var input = new ForwardOnlyStream(pdf);
        string text = PdfOperations.ExtractText(input);

        Assert.Contains("Forward only", text, StringComparison.Ordinal);
        Assert.False(input.Disposed);
    }

    [Fact]
    public void SearchReadsAForwardOnlyStream()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Needle in here");

        using var input = new ForwardOnlyStream(pdf);
        var hits = PdfTextSearch.Search(input, "Needle");

        Assert.NotEmpty(hits);
        Assert.False(input.Disposed);
    }

    /// <summary>
    /// A write path: the stream is read twice, so a buffer really is allocated — and the caller's
    /// stream must survive the call that releases it.
    /// </summary>
    [Fact]
    public void RotateAllPagesReadsAForwardOnlyStreamAndLeavesItOpen()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);

        using var input = new ForwardOnlyStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.RotateAllPages(input, output, 90);

        Assert.False(input.Disposed);

        using var result = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(3, result.Pages.Count);
        foreach (var page in result.Pages)
            Assert.Equal(90, page.Rotate);
    }

    [Fact]
    public void SetPageLabelsReadsAForwardOnlyStreamAndLeavesItOpen()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(2);
        var ranges = new List<PageLabelRange> { new PageLabelRange { StartPageIndex = 0, Style = "D" } };

        using var input = new ForwardOnlyStream(pdf);
        using var output = new MemoryStream();
        PdfPageLabels.SetLabels(input, output, ranges);

        Assert.False(input.Disposed);

        byte[] written = output.ToArray();
        using var readBack = new MemoryStream(written);
        var labels = PdfPageLabels.GetLabels(readBack);
        Assert.NotNull(labels);
        Assert.NotEmpty(labels);
    }

    [Fact]
    public void AddAttachmentReadsAForwardOnlyStreamAndLeavesItOpen()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] payload = Encoding.ASCII.GetBytes("attached bytes");

        using var input = new ForwardOnlyStream(pdf);
        using var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(input, output, "note.txt", payload);

        Assert.False(input.Disposed);

        using var readBack = new MemoryStream(output.ToArray());
        var attachments = PdfAttachmentManager.ListAttachments(readBack);
        Assert.Contains(attachments, a => a.Filename == "note.txt");
    }

    [Fact]
    public void ConvertToPortfolioReadsAForwardOnlyStreamAndLeavesItOpen()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var input = new ForwardOnlyStream(pdf);
        using var output = new MemoryStream();
        PdfPortfolioManager.ConvertToPortfolio(input, output);

        Assert.False(input.Disposed);

        using var readBack = new MemoryStream(output.ToArray());
        Assert.True(PdfPortfolioManager.IsPortfolio(readBack));
    }

    /// <summary>
    /// The other half, and the one the forward-only cases structurally cannot cover: when the
    /// caller's stream <i>is</i> seekable, no buffer is allocated, and the write paths must not
    /// dispose the stream they were handed.
    /// </summary>
    /// <remarks>
    /// A forward-only input makes <c>owned</c> true, so an unconditional
    /// <c>ms.Dispose()</c> at those six call sites would close the <i>buffer</i> and every
    /// forward-only test would still pass. Only a seekable caller stream separates
    /// "dispose what you allocated" from "dispose what you were given".
    /// </remarks>
    [Theory]
    [InlineData("rotate")]
    [InlineData("labels")]
    [InlineData("attach")]
    [InlineData("portfolio")]
    public void AWritePathNeverDisposesASeekableCallerStream(string operation)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(2);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        switch (operation)
        {
            case "rotate":
                PdfOperations.RotateAllPages(input, output, 90);
                break;
            case "labels":
                PdfPageLabels.SetLabels(input, output,
                    new List<PageLabelRange> { new PageLabelRange { StartPageIndex = 0, Style = "D" } });
                break;
            case "attach":
                PdfAttachmentManager.AddAttachment(input, output, "note.txt",
                    Encoding.ASCII.GetBytes("attached bytes"));
                break;
            default:
                PdfPortfolioManager.ConvertToPortfolio(input, output);
                break;
        }

        Assert.True(output.Length > 0);

        // Both throw ObjectDisposedException on a closed MemoryStream, so this is the assertion —
        // not `CanRead`, which a Stream subclass may report as a constant.
        Assert.Equal(pdf.Length, input.Length);
        input.Position = 0;
        Assert.Equal(pdf[0], input.ReadByte());
    }

    /// <summary>A stream that can be read once, front to back, and nothing else.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _at;

        public ForwardOnlyStream(byte[] data) => _data = data;

        /// <summary>
        /// Set by Dispose. `CanRead` cannot stand in for this: it is an override returning a
        /// constant, so it stays true after disposal and an assertion on it could never fail —
        /// banned test pattern 3, and the first draft of these tests had exactly that.
        /// </summary>
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _at);
            Array.Copy(_data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
