// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The asynchronous face of §23 — awaiting the ranges a page needs.
/// </summary>
/// <remarks>
/// <para><b>The third answer exists because a synchronous call cannot wait.</b> It has to say *the
/// bytes are not here* and hand the decision back. An asynchronous one can take longer, so
/// <i>not yet</i> becomes <i>await</i> — which is why <see cref="IAsyncByteRangeSource"/> has no
/// third answer and <c>ReadPageAsync</c> is the synchronous protocol driven to completion rather
/// than a second decoder.</para>
///
/// <para>So what these tests pin is that it really is the same protocol: the ranges fetched are the
/// ones the synchronous reader names, the count of them is bounded, and the file is never read
/// whole.</para>
/// </remarks>
public class ProgressivePageReaderAsyncTests
{
    private const int PageCount = 60;
    private const int LatePage = 55;

    private static byte[] Linearized()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(PageCount));
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// <b>A page arrives after a bounded number of awaited fetches, and the file is never read
    /// whole.</b>
    /// </summary>
    [Fact]
    public async Task APageIsReadFromAFewAwaitedRanges()
    {
        byte[] pdf = Linearized();
        var source = new RecordingAsyncSource(pdf);

        PdfProgressivePage page = await PdfProgressivePageReader.ReadPageAsync(source, LatePage);

        Assert.Null(page.Error);
        Assert.NotNull(page.Page);
        Assert.Equal("Page", (page.Page[PdfName.Type] as PdfName)?.Value);
        Assert.Contains($"(Page {LatePage + 1})", Encoding.ASCII.GetString(page.Content),
                        StringComparison.Ordinal);

        Assert.True(source.Fetches.Count <= 3,
                    "fetched: " + string.Join(" | ", source.Fetches));
        Assert.True(source.BytesFetched < pdf.Length,
                    $"fetched {source.BytesFetched} bytes of a {pdf.Length}-byte file");
    }

    /// <summary>
    /// The ranges it awaits are the ones the synchronous reader names — the same protocol, not a
    /// parallel one that happens to arrive at the same page.
    /// </summary>
    [Fact]
    public async Task TheRangesAwaitedAreTheOnesTheSynchronousReaderNames()
    {
        byte[] pdf = Linearized();

        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfLinearizationHintReader.TryRead(whole, out PdfLinearizationHints hints));

        var source = new RecordingAsyncSource(pdf);
        await PdfProgressivePageReader.ReadPageAsync(source, LatePage);

        Assert.Contains(source.Fetches, f => f.Offset == 0);
        Assert.Contains(source.Fetches, f => f.Offset == hints.Pages[LatePage].Offset
                                             && f.Length == hints.Pages[LatePage].Length);
    }

    /// <summary>
    /// <b>The reason travels on the result, not on a <c>[ThreadStatic]</c> slot</b>, because a
    /// continuation may resume on another thread.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryFileIsRefusedThroughTheResult()
    {
        var source = new RecordingAsyncSource(MinimalPdfBuilder.CreateMultiPagePdf(4));

        PdfProgressivePage page = await PdfProgressivePageReader.ReadPageAsync(source, 0);

        Assert.Null(page.Page);
        Assert.NotNull(page.Error);
        Assert.Contains("progressive access", page.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a source that never delivers what it was asked for.</b> The loop is bounded
    /// by the ranges, so it ends with a reported reason instead of spinning.
    /// </summary>
    [Fact]
    public async Task AdversarialSourceThatNeverDeliversEndsWithAReason()
    {
        var source = new EmptyAsyncSource(Linearized().Length);

        PdfProgressivePage page = await PdfProgressivePageReader.ReadPageAsync(source, 0);

        Assert.Null(page.Page);
        Assert.NotNull(page.Error);
        Assert.True(source.Fetches <= 8, $"asked {source.Fetches} times before giving up");
    }

    /// <summary>
    /// <b>A file smaller than a request is read anyway.</b> The head request is four Annex F
    /// windows, so for a small document it runs past the end of the file and comes back short —
    /// which means end of file, not a failed fetch, and the read must proceed on what exists.
    /// </summary>
    /// <remarks>
    /// The sixty-page fixture cannot show this: every request fits inside it, so the short-read path
    /// is never taken and a reader that rejected such a range would still pass.
    /// </remarks>
    [Fact]
    public async Task AFileSmallerThanARequestIsStillRead()
    {
        byte[] pdf = SmallLinearized();
        Assert.True(pdf.Length < 4 * 1024,
                    $"the fixture is {pdf.Length} bytes, so no request runs past its end");

        var source = new RecordingAsyncSource(pdf);

        PdfProgressivePage page = await PdfProgressivePageReader.ReadPageAsync(source, 1);

        Assert.Null(page.Error);
        Assert.NotNull(page.Page);
        Assert.Contains("(Page 2)", Encoding.ASCII.GetString(page.Content), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a source that returns less than it was asked for in the middle of the
    /// file.</b>
    /// </summary>
    /// <remarks>
    /// <para><see cref="IAsyncByteRangeSource"/> says a short result means <i>end of file</i>. A
    /// source that truncates mid-file breaks that, and accepting the fragment would decode tables
    /// and objects out of bytes that stop halfway.</para>
    ///
    /// <para><b>The assertion is which side is blamed, and that is the whole point.</b> Accepting
    /// the fragment does not produce a crash — it produces "the hint tables could not be read",
    /// which accuses the document of being malformed when the transport is at fault. Asserting
    /// merely that <i>some</i> error came back passes either way; a mutation removing the guard
    /// survived exactly that assertion.</para>
    /// </remarks>
    [Fact]
    public async Task AdversarialSourceThatTruncatesMidFileIsNotAccepted()
    {
        var source = new HalvingAsyncSource(Linearized());

        PdfProgressivePage page = await PdfProgressivePageReader.ReadPageAsync(source, LatePage);

        Assert.Null(page.Page);
        Assert.Contains("did not deliver", page.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>Cancellation travels to the transport and out to the caller.</summary>
    [Fact]
    public async Task CancellationIsObserved()
    {
        var source = new RecordingAsyncSource(Linearized());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PdfProgressivePageReader.ReadPageAsync(source, 0, cts.Token));
    }

    /// <summary>A null source is a programming error, not an answer.</summary>
    [Fact]
    public async Task ANullSourceIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => PdfProgressivePageReader.ReadPageAsync(null, 0));
    }

    // ================================================================

    /// <summary>A linearized document small enough that a 4 KB request runs past its end.</summary>
    private static byte[] SmallLinearized()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// A source that breaks the contract by returning half of every range, mid-file, where a short
    /// result is supposed to mean end of file.
    /// </summary>
    private sealed class HalvingAsyncSource : IAsyncByteRangeSource
    {
        private readonly byte[] _all;

        public HalvingAsyncSource(byte[] all) => _all = all;

        public long? Length => _all.Length;

        public Task<byte[]> FetchAsync(long offset, long length, CancellationToken cancellationToken)
        {
            if (offset >= _all.Length) return Task.FromResult(Array.Empty<byte>());

            int take = (int)Math.Min(length, _all.Length - offset) / 2;
            var bytes = new byte[take];
            Array.Copy(_all, offset, bytes, 0, take);

            return Task.FromResult(bytes);
        }
    }

    /// <summary>A complete file that records every range it was asked for.</summary>
    private sealed class RecordingAsyncSource : IAsyncByteRangeSource
    {
        private readonly byte[] _all;

        public RecordingAsyncSource(byte[] all) => _all = all;

        public List<(long Offset, long Length)> Fetches { get; } = new();

        public long BytesFetched { get; private set; }

        public long? Length => _all.Length;

        public Task<byte[]> FetchAsync(long offset, long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fetches.Add((offset, length));

            if (offset >= _all.Length) return Task.FromResult(Array.Empty<byte>());

            int take = (int)Math.Min(length, _all.Length - offset);
            var bytes = new byte[take];
            Array.Copy(_all, offset, bytes, 0, take);
            BytesFetched += take;

            return Task.FromResult(bytes);
        }
    }

    /// <summary>A source that answers every fetch with nothing at all.</summary>
    private sealed class EmptyAsyncSource : IAsyncByteRangeSource
    {
        public EmptyAsyncSource(long length) => Length = length;

        public int Fetches { get; private set; }

        public long? Length { get; }

        public Task<byte[]> FetchAsync(long offset, long length, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(Array.Empty<byte>());
        }
    }
}
