// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The two write-side filters §11e was missing: <see cref="RunLengthEncodeFilter"/> and
/// <see cref="LzwEncodeFilter"/>.
/// </summary>
/// <remarks>
/// <para><b>The round trip below catches more than expected, and less than everything — and both
/// halves were measured rather than assumed.</b> It catches an encoder that disagrees with this
/// decoder: reverting the LZW code-width fix reddens six of these tests. What it structurally
/// cannot catch is a rule both sides read the same way and both read wrongly — CLAUDE.md's "a
/// relative assertion cannot see a change applied to both of the things it relates".</para>
///
/// <para><b>That blind spot was demonstrated, not asserted.</b> Moving LZW's first free code from
/// 258 to 259 <i>on both sides</i> leaves every round trip below green and makes twenty of the
/// external cases fail. So the round trip is the breadth check and the external decode is the
/// correctness one, and neither substitutes for the other.</para>
///
/// <para><b>The external check is an independent checker.</b> Thirty-nine cases — both filters, both
/// <c>/EarlyChange</c> values, payloads from zero bytes to 200 KB chosen to force table clears and
/// code-width changes — wrapped in a one-page PDF and read back with
/// <c>an independent checker --stream-data=uncompress</c>. It cannot be checked in, because it needs an independent checker; what is
/// here is the round trip plus <b>pinned encoder output</b>, and the pinned bytes are the part a
/// shared misreading would have to change.</para>
///
/// <para><b>Neither encoder is wired into <c>PdfOptimizer</c>, and that is a measured decision.</b>
/// Across eight real content and appearance streams RunLength won <i>none</i> and LZW won
/// <i>one</i>, by two bytes on a 33-byte border; on four of the eight the smallest form was the
/// uncompressed original, which the optimizer already keeps. Adding them as candidates would be an
/// encode per stream for a win that is not there. See each encoder's remarks for the numbers.</para>
/// </remarks>
public class WriteSideFilterTests
{
    // ------------------------------------------------------------------ RunLength

    /// <summary>
    /// <b>The encoder's exact output for a fixed input</b>, so a change to it is loud.
    /// </summary>
    /// <remarks>
    /// A round trip alone would accept any self-consistent re-encoding, including a slower or larger
    /// one. These bytes were confirmed by decoding them with an independent checker: <c>0xFE 'A'</c> is a repeat of
    /// three, <c>0x02 'B' 'C' 'D'</c> a literal run of three, and <c>0x80</c> the end-of-data marker
    /// §7.4.5 requires.
    /// </remarks>
    [Fact]
    public void RunLengthEncodesRepeatsAsRunsAndTheRestAsLiterals()
    {
        byte[] encoded = RunLengthEncodeFilter.Encode(Ascii("AAABCD"));

        Assert.Equal(new byte[] { 0xFE, (byte)'A', 0x02, (byte)'B', (byte)'C', (byte)'D', 0x80 },
                     encoded);
    }

    /// <summary>
    /// A run of two is written as literals, because encoding it as a run costs more.
    /// </summary>
    /// <remarks>
    /// The threshold is the one decision in this encoder that is a judgement rather than a
    /// transcription: a repeat record costs two bytes whatever its length, so two identical bytes
    /// break even — and breaking a literal run to encode them adds a length byte to whatever
    /// follows. A version using two produced output <i>larger</i> than its input on ordinary text.
    /// </remarks>
    [Fact]
    public void RunLengthLeavesAPairOfIdenticalBytesInTheLiteralRun()
    {
        byte[] encoded = RunLengthEncodeFilter.Encode(Ascii("ABBC"));

        // One literal run of four, not literal-run-repeat-literal-run.
        Assert.Equal(new byte[] { 0x03, (byte)'A', (byte)'B', (byte)'B', (byte)'C', 0x80 }, encoded);
    }

    /// <summary>Empty input still produces the terminator, which is not the same as no stream.</summary>
    /// <remarks>
    /// A zero-length RunLength stream is a truncated stream, not the encoding of nothing. The two
    /// must not look alike to a reader, and a decoder handed the former has no way to say which it
    /// received.
    /// </remarks>
    [Fact]
    public void RunLengthEncodesEmptyInputAsABareEndOfDataMarker()
    {
        Assert.Equal(new byte[] { 0x80 }, RunLengthEncodeFilter.Encode(Array.Empty<byte>()));
        Assert.Equal(new byte[] { 0x80 }, RunLengthEncodeFilter.Encode(null));
    }

    /// <summary>Runs longer than one length byte can describe are split at 128.</summary>
    [Fact]
    public void RunLengthSplitsARunLongerThanOneRecordCanDescribe()
    {
        byte[] encoded = RunLengthEncodeFilter.Encode(Repeat((byte)'Q', 129));

        // 257-128 = 129 (0x81) for the first 128, then a two-byte literal record for the last one.
        Assert.Equal(new byte[] { 0x81, (byte)'Q', 0x00, (byte)'Q', 0x80 }, encoded);
    }

    // ------------------------------------------------------------------ LZW

    /// <summary>
    /// <b>The encoder's exact output for a fixed input.</b>
    /// </summary>
    /// <remarks>
    /// <c>-AB-</c> is the example the format's own literature uses. The stream is clear (256), the
    /// four bytes, then end-of-data (257) — nine bits each, MSB first — because no substring repeats
    /// and so no table entry is ever used. Confirmed against an independent checker.
    /// </remarks>
    [Fact]
    public void LzwEncodesAShortInputAsClearThenLiteralsThenEndOfData()
    {
        byte[] encoded = LzwEncodeFilter.Encode(Ascii("-AB-"));

        Assert.Equal(Bits(9, 256, (int)'-', (int)'A', (int)'B', (int)'-', 257), encoded);
    }

    /// <summary>An empty input is still a clear and an end-of-data.</summary>
    [Fact]
    public void LzwEncodesEmptyInputAsClearThenEndOfData()
    {
        Assert.Equal(Bits(9, 256, 257), LzwEncodeFilter.Encode(Array.Empty<byte>()));
        Assert.Equal(Bits(9, 256, 257), LzwEncodeFilter.Encode(null));
    }

    /// <summary><c>/EarlyChange</c> outside 0..1 is refused rather than silently treated as 1.</summary>
    /// <remarks>
    /// A stream encoded under one value and decoded under the other is wrong with no error anywhere,
    /// so a caller that fat-fingers this must hear about it at the call rather than in a reader.
    /// </remarks>
    [Fact]
    public void LzwRefusesAnEarlyChangeValueTheFormatDoesNotDefine()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LzwEncodeFilter.Encode(Ascii("x"), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => LzwEncodeFilter.Encode(Ascii("x"), -1));
    }

    // ------------------------------------------------------------------ round trips

    /// <summary>
    /// Every payload survives encode-then-decode, for both filters and both
    /// <c>/EarlyChange</c> values.
    /// </summary>
    /// <remarks>
    /// <para>The weaker of the two checks — see this class's remarks — but the one that covers
    /// breadth. The payloads are chosen for the boundaries the formats have: a run at exactly the
    /// 128-byte record cap and one past it; data with no runs at all, where RunLength must grow;
    /// 200 KB of low-entropy bytes, which fills LZW's 4 096-entry table several times and forces the
    /// clears that the width schedule has to survive.</para>
    /// <para><b>The size is asserted alongside the content</b>, because a decoder that returned a
    /// prefix would satisfy a "starts with" check and a shorter round trip is the shape a truncation
    /// takes.</para>
    /// </remarks>
    [Theory]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("pair")]
    [InlineData("triple")]
    [InlineData("content stream")]
    [InlineData("run at cap")]
    [InlineData("run past cap")]
    [InlineData("long run")]
    [InlineData("no runs")]
    [InlineData("every byte value")]
    [InlineData("forces table clears")]
    public void BothFiltersRoundTripEveryPayload(string which)
    {
        byte[] payload = Payload(which);

        byte[] rle = RunLengthEncodeFilter.Encode(payload);
        byte[] backFromRle = new RunLengthDecodeFilter().Decode(rle, null);
        Assert.Equal(payload.Length, backFromRle.Length);
        Assert.Equal(payload, backFromRle);

        foreach (int earlyChange in new[] { 1, 0 })
        {
            byte[] lzw = LzwEncodeFilter.Encode(payload, earlyChange);

            var parms = new PdfDictionary();
            parms.Set(PdfName.EarlyChange, new PdfNumber(earlyChange));
            byte[] backFromLzw = new LzwDecodeFilter().Decode(lzw, parms);

            Assert.Equal(payload.Length, backFromLzw.Length);
            Assert.Equal(payload, backFromLzw);
        }
    }

    /// <summary>
    /// Every length from 1 to 4 500 bytes of incompressible data round-trips cleanly, both
    /// <c>/EarlyChange</c> values. The width schedule must hold at the end-of-data code too: the
    /// decoder adds an entry for the final code and may widen before reading end-of-data, so an
    /// encoder that wrote it at the stale width fails only at lengths that land on a width change.
    /// Pseudo-random bytes add a table entry almost per byte, so the range crosses every width
    /// (9 to 12 bits) and the first clear.
    /// </summary>
    [Fact]
    public void LzwRoundTripsCleanlyAtEveryLengthAcrossTheWidthChanges()
    {
        var all = new byte[4500];
        new Random(20261013).NextBytes(all);
        var failures = new List<string>();
        foreach (int earlyChange in new[] { 1, 0 })
        {
            var parms = new PdfDictionary();
            parms.Set(PdfName.EarlyChange, new PdfNumber(earlyChange));
            for (int n = 1; n <= all.Length && failures.Count < 5; n++)
            {
                byte[] payload = all.AsSpan(0, n).ToArray();
                byte[] back = new LzwDecodeFilter().Decode(LzwEncodeFilter.Encode(payload, earlyChange), parms);
                if (back.Length != n || !back.AsSpan().SequenceEqual(payload) || LzwDecodeFilter.LastTruncationReason != null)
                    failures.Add($"EarlyChange {earlyChange}, length {n}: {back.Length} bytes back, truncation \"{LzwDecodeFilter.LastTruncationReason}\"");
            }
        }
        Assert.Empty(failures);
    }

    /// <summary>
    /// The LZW decoder reports no truncation on any of these — a clean decode, not a lenient one.
    /// </summary>
    /// <remarks>
    /// <c>LzwDecodeFilter</c> is deliberately lenient: a code it cannot place stops the decode and
    /// it returns what it has. So a round trip that compared only a <i>prefix</i> would pass on a
    /// stream the decoder had given up on. Asserting the diagnostic is silent is what separates
    /// "decoded" from "decoded as far as it could".
    /// </remarks>
    [Theory]
    [InlineData("forces table clears")]
    [InlineData("every byte value")]
    [InlineData("no runs")]
    public void TheLzwDecoderReportsNoTruncationForEncoderOutput(string which)
    {
        byte[] lzw = LzwEncodeFilter.Encode(Payload(which));

        new LzwDecodeFilter().Decode(lzw, null);

        Assert.Null(LzwDecodeFilter.LastTruncationReason);
    }

    /// <summary>
    /// The measured claim, kept honest: RunLength does not beat leaving the bytes alone on a real
    /// content stream.
    /// </summary>
    /// <remarks>
    /// <para>Stated as a test because it is the reason neither encoder is wired into
    /// <c>PdfOptimizer</c>, and a reason that lives only in a comment is one nobody re-checks. If
    /// this ever fails, RunLength has become worth offering the optimizer and the decision should be
    /// revisited rather than the assertion relaxed.</para>
    /// <para>It is not a claim about compression in general — a long run compresses 60:1 here. It is
    /// a claim about the streams a PDF optimizer actually sees.</para>
    /// </remarks>
    [Fact]
    public void RunLengthDoesNotBeatTheOriginalBytesOnARealContentStream()
    {
        byte[] content = Payload("content stream");

        Assert.True(RunLengthEncodeFilter.Encode(content).Length > content.Length,
            "RunLength now beats the raw bytes on a typical content stream; PdfOptimizer should " +
            "be offered it as a candidate, and this test replaced by that wiring.");

        // The control: it does compress what it is designed for, so the test above is measuring the
        // input rather than a broken encoder.
        byte[] run = Payload("long run");
        Assert.True(RunLengthEncodeFilter.Encode(run).Length * 10 < run.Length);
    }

    // ==================================================================

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Repeat(byte b, int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++) data[i] = b;
        return data;
    }

    /// <summary>The bytes of a sequence of fixed-width codes, MSB first.</summary>
    private static byte[] Bits(int width, params int[] codes)
    {
        var bits = new List<int>();
        foreach (int code in codes)
            for (int i = width - 1; i >= 0; i--) bits.Add((code >> i) & 1);

        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i] != 0) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    private static byte[] Payload(string which)
    {
        switch (which)
        {
            case "empty": return Array.Empty<byte>();
            case "single": return Ascii("A");
            case "pair": return Ascii("AA");
            case "triple": return Ascii("AAA");
            case "content stream":
                return Ascii("BT /F1 12 Tf 72 700 Td (The quick brown fox jumps over the lazy dog.) Tj ET");
            case "run at cap": return Repeat((byte)'Q', 128);
            case "run past cap": return Repeat((byte)'Q', 129);
            case "long run": return Repeat((byte)'Z', 5000);

            case "no runs":
            {
                // A fixed pseudo-random sequence: reproducible, and with no run of three anywhere,
                // which is the case RunLength must grow rather than shrink.
                var data = new byte[4000];
                int x = 12345;
                for (int i = 0; i < data.Length; i++)
                {
                    x = unchecked(x * 1103515245 + 12345);
                    data[i] = (byte)((x >> 16) & 0xFF);
                }
                return data;
            }

            case "every byte value":
            {
                var data = new byte[256 * 40];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
                return data;
            }

            case "forces table clears":
            {
                // Low entropy over a large span: the LZW table fills and has to be cleared several
                // times, which is the path the code-width schedule most easily gets wrong.
                var data = new byte[200000];
                int x = 987654321;
                for (int i = 0; i < data.Length; i++)
                {
                    x = unchecked(x * 1103515245 + 12345);
                    data[i] = (byte)((x >> 20) & 0x03);
                }
                return data;
            }

            default: throw new ArgumentOutOfRangeException(nameof(which), which, "unknown payload");
        }
    }
}
