// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// LOW-severity findings from the assembly scan: null-argument contracts, the buffers
/// <c>EnsureSeekable</c> allocates for a non-seekable input, the per-page rescan in
/// <c>GetPageLabels</c>, and <c>GetWidth</c>'s documented fallback.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class FacadeLowFindingsTests : IDisposable
{
    public FacadeLowFindingsTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    // ==================================================================
    //  Null-stream contract
    // ==================================================================

    [Fact]
    public void StreamReaders_NullInput_ThrowArgumentNullExceptionNotNullReference()
    {
        // These reach PdfHelpers.EnsureSeekable before any argument check, so a null surfaced as
        // NullReferenceException — which a caller catching ArgumentNullException, the contract
        // every sibling on this facade honours, does not catch.
        Assert.Throws<ArgumentNullException>(() => PdfOperations.GetMetadata((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.GetBookmarks(null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractLinks((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.GetPageCount((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.GetVersion((Stream)null));
    }

    // ==================================================================
    //  EnsureSeekable's buffer is disposed, not abandoned
    // ==================================================================

    /// <summary>
    /// A forward-only wrapper: <c>CanSeek</c> is false, so <c>EnsureSeekable</c> must copy it, and
    /// the copy is the object whose disposal is under test. The wrapper records whether the
    /// facade disposed <i>it</i> — which it must not, since the caller owns its own stream.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);

        public bool WasDisposed { get; private set; }

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
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void GetMetadata_NonSeekableInput_StillReadsAndLeavesTheCallersStreamAlone()
    {
        // The finally block disposed only the decrypted stream, so for an unencrypted document
        // (ownsSource == false) nothing was disposed and EnsureSeekable's full-document copy was
        // left to the GC — one per request under load. The buffer is internal, so what is
        // assertable here is that the operation still works and the caller's own stream is not
        // the thing that got disposed.
        using var input = new NonSeekableStream(MinimalPdfBuilder.CreatePdfWithMetadata("Piped"));

        var metadata = PdfOperations.GetMetadata(input);

        Assert.Equal("Piped", metadata.Title);
        Assert.False(input.WasDisposed, "the facade disposed the caller's stream");
    }

    [Fact]
    public void Validate_NonSeekableInput_HandsThePluginARewoundStreamAndDisposesTheCopy()
    {
        // `validator.Validate(PdfHelpers.EnsureSeekable(input))` abandoned the copy. The plugin
        // records the stream it was given, so the test can check afterwards that it was disposed
        // — the copy's own evidence, not a memory measurement.
        var plugin = new StreamCapturingValidationPlugin("PDF/X-1a");
        PdfPluginHost.Instance.Register(plugin);

        using var input = new NonSeekableStream(MinimalPdfBuilder.CreateMinimalPdf());
        var result = PdfOperations.Validate(input, "PDF/X-1a");

        Assert.NotNull(result);
        Assert.NotSame(input, plugin.Captured);
        Assert.Equal(0L, plugin.PositionSeen);
        Assert.True(plugin.CapturedWasDisposedAfterwards(),
            "the MemoryStream EnsureSeekable allocated was not disposed");
        Assert.False(input.WasDisposed, "the facade disposed the caller's stream");
    }

    private sealed class StreamCapturingValidationPlugin : IValidationPlugin
    {
        public StreamCapturingValidationPlugin(string level) => ConformanceLevel = level;

        public string Name => "Test.CapturingValidator";
        public string Description => "records the stream it was handed";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public string ConformanceLevel { get; }
        public Stream Captured { get; private set; }
        public long PositionSeen { get; private set; } = -1;

        public PluginValidationResult Validate(Stream pdfStream)
        {
            Captured = pdfStream;
            PositionSeen = pdfStream.Position;
            return new PluginValidationResult { IsConformant = true };
        }

        /// <summary>A disposed MemoryStream throws on every member; that is the observable.</summary>
        public bool CapturedWasDisposedAfterwards()
        {
            try { _ = Captured.Length; return false; }
            catch (ObjectDisposedException) { return true; }
        }
    }

    // ==================================================================
    //  GetPageLabels: one cursor, same labels
    // ==================================================================

    [Fact]
    public void GetPageLabels_ManyRanges_ProducesTheSameLabelsAsTheFullRescan()
    {
        // The single-cursor walk must select exactly the range the per-page rescan did: the last
        // range whose firstPage is <= p. Ranges that start beyond the last page, and a first range
        // that does not start at page 0, are the cases a cursor gets wrong if it advances eagerly.
        byte[] pdf = PageLabelPdf(pageCount: 6, nums:
            "0 << /S /r >> 2 << /S /D /St 1 >> 4 << /S /A /St 3 >> 99 << /S /D /St 500 >>");

        using var input = new MemoryStream(pdf);
        var labels = PdfOperations.GetPageLabels(input);

        Assert.Equal(new[] { "i", "ii", "1", "2", "C", "D" }, labels);
    }

    [Fact]
    public void GetPageLabels_FirstRangeStartingAfterPageZero_LeavesEarlierPagesOnTheDefault()
    {
        byte[] pdf = PageLabelPdf(pageCount: 4, nums: "2 << /S /D /St 7 >>");

        using var input = new MemoryStream(pdf);
        var labels = PdfOperations.GetPageLabels(input);

        // Pages before the first range have no style, so only the (absent) prefix applies.
        Assert.Equal(new[] { "", "", "7", "8" }, labels);
    }

    private static byte[] PageLabelPdf(int pageCount, string nums)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        int firstPageObj = 4;

        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /PageLabels 3 0 R >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [");
        for (int i = 0; i < pageCount; i++)
            sb.AppendFormat(ci, "{0}{1} 0 R", i == 0 ? "" : " ", firstPageObj + i);
        sb.AppendFormat(ci, "] /Count {0} >>\nendobj\n", pageCount);

        offsets.Add(sb.Length);
        sb.AppendFormat(ci, "3 0 obj\n<< /Nums [{0}] >>\nendobj\n", nums);

        for (int i = 0; i < pageCount; i++)
        {
            offsets.Add(sb.Length);
            sb.AppendFormat(ci,
                "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n",
                firstPageObj + i);
        }

        int xref = sb.Length;
        sb.AppendFormat(ci, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int o in offsets) sb.AppendFormat(ci, "{0:D10} 00000 n \n", o);
        sb.AppendFormat(ci, "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
                        offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ==================================================================
    //  GetWidth's two fallbacks
    // ==================================================================

    [Fact]
    public void GetWidth_UnmappedCode_UsesTheNotdefAdvance_NotAFlat500()
    {
        // The doc claimed "falls back to 500 if unmapped"; the code uses glyph 0's advance and
        // reaches 500 only when the font supplied no widths at all. Both are now documented, and
        // both are pinned here so the comment cannot drift from the behaviour again.
        var metrics = PdfFontEmbedder.GetFontMetrics(MinimalTrueType.BuildWithDistinctWidths("A"));

        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, metrics.GetWidth('A'));
        Assert.Equal(MinimalTrueType.NotdefAdvance, metrics.GetWidth('Z'));
        Assert.NotEqual(500, metrics.GetWidth('Z'));
    }

    [Fact]
    public void GetWidth_FontWithNoHmtxWidths_IsTheOnlyCaseThatYields500()
    {
        // A font with no readable hmtx has no advance to fall back to, and 500 is the invented
        // plausible number — the only place the documented 500 applies.
        var metrics = PdfFontEmbedder.GetFontMetrics(new byte[13]);
        Assert.Equal(500, metrics.GetWidth('A'));
    }
}
