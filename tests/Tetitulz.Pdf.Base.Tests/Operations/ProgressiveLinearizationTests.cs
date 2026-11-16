// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Reading Annex F's opening question from a file only part of which has arrived — the third answer
/// a <see cref="Stream"/> cannot give.
/// </summary>
/// <remarks>
/// <para><b>The distinction under test is "not yet" against "no".</b> A stream has two answers,
/// bytes and the end, so <c>PdfRevisions.IsLinearized(Stream)</c> must fold a head that has not
/// arrived into <c>false</c> — indistinguishable from an ordinary file. A caller that acts on that
/// stops fetching a document it had merely not finished downloading.</para>
///
/// <para>GAPS.md §23, slice 14b. The seam is <see cref="IByteRangeSource"/>; this is its first
/// consumer, which is deliberate — a contract with no dispatch is the shape this repository treats
/// as a defect.</para>
/// </remarks>
public class ProgressiveLinearizationTests
{
    private static byte[] Linearized()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// A download that has delivered its first kilobyte is classified from that kilobyte, and the
    /// parameters a progressive fetch needs come with it.
    /// </summary>
    [Fact]
    public void AFileWhoseHeadHasArrivedIsClassifiedFromTheHead()
    {
        byte[] pdf = Linearized();
        var source = new PartialSource(pdf, arrived: PdfLimits.LinearizationDictionaryWindowBytes);

        Assert.Equal(PdfLinearizationState.Linearized, PdfRevisions.ReadLinearizationState(source));

        // And it did not read the rest: the bytes past the window are not there to read.
        Assert.True(source.MaxOffsetRead < pdf.Length,
            $"the read reached offset {source.MaxOffsetRead} of a {pdf.Length}-byte file");
    }

    /// <summary>
    /// <b>The claim this slice exists for.</b> Nothing has arrived, so the answer is "not yet" — not
    /// "not linearized", which is what a stream is forced to say about the same state.
    /// </summary>
    [Fact]
    public void AHeadThatHasNotArrivedIsNotYetAvailableRatherThanNotLinearized()
    {
        byte[] pdf = Linearized();

        Assert.Equal(
            PdfLinearizationState.NotYetAvailable,
            PdfRevisions.ReadLinearizationState(new PartialSource(pdf, arrived: 0)));

        // The same file, the same state, through a Stream: false — which reads as "no".
        using var head = new MemoryStream(Array.Empty<byte>(), writable: false);
        Assert.False(PdfRevisions.IsLinearized(head));
    }

    /// <summary>
    /// A head cut off <b>before the first object</b> is "not yet" — and the stream overload, handed
    /// exactly those bytes, says <c>false</c>. This is the pair the state exists to separate: one
    /// file, one prefix, two opposite conclusions.
    /// </summary>
    [Fact]
    public void APartialHeadIsNotYetAvailableWhereAStreamSaysNo()
    {
        byte[] pdf = Linearized();

        // Cut before the `obj` keyword the dictionary lives in, so the prefix carries no claim
        // either way — the state a download is in for its first packet.
        int cut = IndexOfObjKeyword(pdf);
        Assert.True(cut > 0, "the fixture has no object header to cut before");

        Assert.Equal(
            PdfLinearizationState.NotYetAvailable,
            PdfRevisions.ReadLinearizationState(new PartialSource(pdf, arrived: cut)));

        using var truncated = new MemoryStream(pdf.Take(cut).ToArray(), writable: false);
        Assert.False(PdfRevisions.IsLinearized(truncated));
    }

    private static int IndexOfObjKeyword(byte[] pdf)
    {
        for (int i = 0; i + 3 <= pdf.Length; i++)
            if (pdf[i] == (byte)'o' && pdf[i + 1] == (byte)'b' && pdf[i + 2] == (byte)'j') return i;
        return -1;
    }

    /// <summary>
    /// A file genuinely shorter than the window is <b>not</b> "not yet": the source knows its length,
    /// so a short read is the end of the file and the answer is real.
    /// </summary>
    [Fact]
    public void AFileShorterThanTheWindowIsAnsweredNotDeferred()
    {
        byte[] whole = MinimalPdfBuilder.CreateMinimalPdf();
        Assert.True(whole.Length < PdfLimits.LinearizationDictionaryWindowBytes,
            "the fixture is not shorter than the window, so it tests nothing");

        var source = new PartialSource(whole, arrived: whole.Length);

        Assert.Equal(PdfLinearizationState.NotLinearized, PdfRevisions.ReadLinearizationState(source));
    }

    /// <summary>An ordinary file whose head has arrived is a real "no", with all its bytes present.</summary>
    [Fact]
    public void AnOrdinaryFileIsNotLinearized()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);

        Assert.Equal(
            PdfLinearizationState.NotLinearized,
            PdfRevisions.ReadLinearizationState(new PartialSource(pdf, arrived: pdf.Length)));
    }

    /// <summary>
    /// The parameters read from a head-only source are the ones the stream overload reads from the
    /// whole file — the forwarding is real, not a second implementation.
    /// </summary>
    [Fact]
    public void TheParametersMatchTheWholeFileReading()
    {
        byte[] pdf = Linearized();

        using var whole = new MemoryStream(pdf, writable: false);
        Assert.True(PdfRevisions.TryReadLinearization(whole, out PdfLinearizationInfo fromStream));

        var source = new PartialSource(pdf, arrived: 4 * PdfLimits.LinearizationDictionaryWindowBytes);
        Assert.Equal(
            PdfLinearizationState.Linearized,
            PdfRevisions.ReadLinearization(source, out PdfLinearizationInfo fromSource));

        Assert.Equal(fromStream.FileLength, fromSource.FileLength);
        Assert.Equal(fromStream.PageCount, fromSource.PageCount);
        Assert.Equal(fromStream.HintStreamOffset, fromSource.HintStreamOffset);
        Assert.Equal(fromStream.HintStreamLength, fromSource.HintStreamLength);
        Assert.Equal(fromStream.FirstPageObjectNumber, fromSource.FirstPageObjectNumber);
    }

    /// <summary>
    /// A complete local file reaches the same code through <see cref="StreamByteRangeSource"/>, and
    /// answers what the stream overload answers.
    /// </summary>
    [Fact]
    public void AStreamSourceAnswersWhatTheStreamOverloadAnswers()
    {
        byte[] pdf = Linearized();
        using var stream = new MemoryStream(pdf, writable: false);

        Assert.Equal(
            PdfLinearizationState.Linearized,
            PdfRevisions.ReadLinearizationState(new StreamByteRangeSource(stream)));

        stream.Position = 0;
        Assert.True(PdfRevisions.IsLinearized(stream));
    }

    /// <summary>
    /// A stream that cannot seek is refused by name rather than silently buffered — the caller was
    /// trying not to hold the file, so a hidden copy is the opposite of what it asked for.
    /// </summary>
    [Fact]
    public void ANonSeekableStreamIsRefused()
    {
        using var forwardOnly = new ForwardOnlyStream(Linearized());

        var ex = Assert.Throws<ArgumentException>(() => new StreamByteRangeSource(forwardOnly));
        Assert.Contains("seekable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: a source that lies.</b> It claims a length it does not have and answers
    /// <c>true</c> for every range while delivering nothing. The reader must answer rather than
    /// throw or loop — a hostile or broken source is exactly what a byte-range seam exposes.
    /// </summary>
    [Fact]
    public void AdversarialSourceThatClaimsBytesItDoesNotHave()
    {
        var lying = new LyingSource(claimedLength: long.MaxValue);

        Assert.Equal(PdfLinearizationState.NotLinearized, PdfRevisions.ReadLinearizationState(lying));
        Assert.Equal(PdfLinearizationState.NotLinearized, PdfRevisions.ReadLinearization(lying, out PdfLinearizationInfo info));
        Assert.Null(info);
    }

    /// <summary>
    /// A first object that runs past the Annex F window is read by taking another window — the
    /// claim is settled by the first, the parameters need the whole object.
    /// </summary>
    [Fact]
    public void ADictionarySpanningTwoWindowsIsReadWhenBothHaveArrived()
    {
        byte[] pdf = SpanningDictionary(keys: 200, terminated: true);
        Assert.True(pdf.Length > PdfLimits.LinearizationDictionaryWindowBytes,
                    "the fixture fits in one window, so it does not span two");

        var source = new PartialSource(pdf, arrived: pdf.Length);

        Assert.Equal(PdfLinearizationState.Linearized,
                     PdfRevisions.ReadLinearization(source, out PdfLinearizationInfo info));
        Assert.NotNull(info);
        Assert.Equal(3, info.PageCount);
    }

    /// <summary>
    /// The same object with only its first window here is <b>not yet</b>: the claim is visible but
    /// the parameters are not, and more bytes would produce them.
    /// </summary>
    [Fact]
    public void ADictionarySpanningTwoWindowsWithOnlyOneHereIsNotYetAvailable()
    {
        byte[] pdf = SpanningDictionary(keys: 200, terminated: true);

        var source = new PartialSource(pdf, arrived: PdfLimits.LinearizationDictionaryWindowBytes);

        Assert.Equal(PdfLinearizationState.NotYetAvailable,
                     PdfRevisions.ReadLinearization(source, out PdfLinearizationInfo info));
        Assert.Null(info);
    }

    /// <summary>
    /// <b>Malformed is not the same as incomplete.</b> Every byte of this file is here and its first
    /// object never closes, so waiting for more would wait forever: the claim stands and the
    /// parameters do not — which is the pair the <see cref="Stream"/> overloads give.
    /// </summary>
    [Fact]
    public void AnUnterminatedDictionaryWhoseBytesAreAllHereClaimsLinearizedWithoutParameters()
    {
        byte[] pdf = SpanningDictionary(keys: 600, terminated: false);
        var source = new PartialSource(pdf, arrived: pdf.Length);

        Assert.Equal(PdfLinearizationState.Linearized,
                     PdfRevisions.ReadLinearization(source, out PdfLinearizationInfo info));
        Assert.Null(info);

        // The Stream overloads say the same two things about the same bytes.
        using var stream = new MemoryStream(pdf, writable: false);
        Assert.True(PdfRevisions.IsLinearized(stream));
        stream.Position = 0;
        Assert.False(PdfRevisions.TryReadLinearization(stream, out _));
    }

    /// <summary>
    /// A first object carrying <paramref name="keys"/> filler entries after Table F.1's, so the
    /// dictionary starts inside the Annex F window and ends well past it.
    /// </summary>
    private static byte[] SpanningDictionary(int keys, bool terminated)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n1 0 obj\n<< /Linearized 1 /L 99999 /H [1000 200] /O 5 /E 900 /N 3 /T 800\n");
        for (int i = 0; i < keys; i++) sb.Append("/Filler").Append(i).Append(' ').Append(i).Append('\n');
        if (terminated) sb.Append(">>\nendobj\n");

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A null source is a programming error, not an answer.</summary>
    [Fact]
    public void ANullSourceIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => PdfRevisions.ReadLinearizationState(null));
        Assert.Throws<ArgumentNullException>(() => PdfRevisions.ReadLinearization(null, out _));
    }

    // ================================================================

    /// <summary>
    /// A file of which only the first <c>arrived</c> bytes are here — the download-in-progress case.
    /// </summary>
    /// <remarks>
    /// It answers <c>false</c> for any range extending past what has arrived and <b>inside</b> the
    /// file, and <c>true</c> with a short count only where the range runs past the real end, which
    /// is the contract <see cref="IByteRangeSource"/> states.
    /// </remarks>
    private sealed class PartialSource : IByteRangeSource
    {
        private readonly byte[] _all;
        private readonly int _arrived;

        public PartialSource(byte[] all, int arrived)
        {
            _all = all;
            _arrived = arrived;
        }

        /// <summary>The highest offset any read reached, so a test can assert what was NOT read.</summary>
        public long MaxOffsetRead { get; private set; }

        public long? Length => _all.Length;

        public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
        {
            read = 0;
            if (offset >= _all.Length) return true;

            long wanted = Math.Min(count, _all.Length - offset);
            if (offset + wanted > _arrived) return false;

            Array.Copy(_all, offset, buffer, bufferOffset, wanted);
            read = (int)wanted;
            MaxOffsetRead = Math.Max(MaxOffsetRead, offset + wanted);
            return true;
        }
    }

    /// <summary>Claims a length it cannot serve and reports success for every range.</summary>
    private sealed class LyingSource : IByteRangeSource
    {
        public LyingSource(long claimedLength) => Length = claimedLength;

        public long? Length { get; }

        public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
        {
            read = count;          // the lie: it says it filled the buffer and wrote nothing
            return true;
        }
    }

    /// <summary>A stream that cannot seek, which is what the adapter must refuse.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public ForwardOnlyStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
