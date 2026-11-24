// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Mutation fuzzing aimed <b>directly at the stream filters</b>, rather than at a whole document.
/// </summary>
/// <remarks>
/// <para><b>Why this exists beside <c>ParserFuzzTests</c>.</b> A document-level fuzzer spends almost
/// every mutant inside the parser and essentially none inside a codec: to reach the LZW decoder at
/// all, a mutant must still have a valid header, a findable <c>startxref</c>, an intact xref, a page
/// tree, and a stream dictionary naming the filter. Corrupting the payload and handing it straight
/// to <see cref="StreamFilter.Decode"/> reaches the decoder every time.</para>
///
/// <para><b>The assertion is the exception TYPE, not "it did not throw"</b> — the same contract
/// <c>ParserFuzzTests</c> asserts. Declining a corrupt payload is correct; declining it as
/// <see cref="InvalidDataException"/>, <see cref="PdfFilterException"/>,
/// <see cref="ArgumentException"/>, <see cref="NotSupportedException"/> or
/// <see cref="IOException"/> is the contract. An <c>IndexOutOfRangeException</c> or
/// <c>NullReferenceException</c> means an unvalidated read reached an array.</para>
///
/// <para><b><see cref="OutOfMemoryException"/> is a finding, not a pass.</b> Bounding allocation
/// against a hostile payload is precisely what <see cref="StreamFilter.MaxOutputBytes"/> and the
/// <c>PdfLimits</c> ceilings are for, so reaching OOM means one of them did not fire.</para>
///
/// <para><b><see cref="MutatedPayloadRespectsTheOutputCeiling"/> is the assertion a document-level
/// fuzzer structurally cannot make.</b> It turns "did not crash" into "respected the ceiling the
/// caller declared", which is a different and stronger claim: a filter that ignores
/// <c>MaxOutputBytes</c> never throws, so nothing in an exception-type harness can see it.</para>
///
/// <para><b>Deterministic on purpose.</b> Fixed seed, fixed counts, so a finding replays exactly and
/// CI cost is constant. The failure message carries the base64 of the failing payload, so a finding
/// replays even after the mutation loop is edited — which the loop in <c>ParserFuzzTests</c> cannot
/// do, and which matters the moment anyone adds a mutation kind.</para>
/// </remarks>
public class FilterFuzzTests
{
    private readonly ITestOutputHelper _out;

    public FilterFuzzTests(ITestOutputHelper output) => _out = output;

    /// <summary>Fixed so a finding replays exactly. Change it only to widen a search.</summary>
    private const int Seed = 20260819;

    /// <summary>Mutants per filter. Bounded so CI cost is constant.</summary>
    private const int MutantsPerFilter = 500;

    /// <summary>
    /// The ceiling handed to every filter under test. Deliberately far below
    /// <c>PdfLimits.MaxDecompressedBytes</c> so the assertion is about the <i>caller's</i> number
    /// rather than Core's, and so an over-ceiling decode costs kilobytes rather than gigabytes.
    /// </summary>
    private const long CallerCeiling = 1L * 1024 * 1024;

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    [InlineData("FlateDecode")]
    public void AdversarialPayload_DecodesOrThrowsADeclaredException_NeverARuntimeFault(string filterName)
    {
        var rng = new Random(Seed);
        byte[] seed = SeedPayload(filterName);
        byte[] pristine = Create(filterName).Decode(seed, null);
        var findings = new List<string>();
        int decoded = 0, declined = 0, differed = 0;

        for (int i = 0; i < MutantsPerFilter; i++)
        {
            PayloadMutation kind = (PayloadMutation)(i % (int)PayloadMutation.Count);
            byte[] mutant = Mutate(seed, kind, rng);

            try
            {
                StreamFilter filter = Create(filterName);
                filter.MaxOutputBytes = CallerCeiling;
                byte[] result = filter.Decode(mutant, null);

                Assert.NotNull(result);
                decoded++;
                if (result.Length != pristine.Length || !SequenceEqual(result, pristine)) differed++;
            }
            catch (InvalidDataException) { declined++; }
            catch (PdfFilterException) { declined++; }
            catch (PdfException) { declined++; }
            catch (ArgumentException) { declined++; }
            catch (NotSupportedException) { declined++; }
            catch (IOException) { declined++; }
            catch (Exception ex)
            {
                findings.Add(
                    $"mutation #{i} ({kind}): {ex.GetType().Name}: {ex.Message}\n" +
                    $"      replay: Convert.FromBase64String(\"{Replay(mutant)}\")");
                if (findings.Count >= 10) break;
            }
        }

        _out.WriteLine(
            $"{filterName}: {MutantsPerFilter} mutations; {decoded} decoded " +
            $"({differed} with output differing from the pristine seed), {declined} declined cleanly.");

        Assert.True(findings.Count == 0,
            $"{filterName} faulted instead of declining (seed {Seed}):\n  " +
            string.Join("\n  ", findings));

        // Floors for the instrument itself. Without them the theory passes while reaching nothing:
        // a seed the filter rejects outright would decline 500 times and assert nothing about
        // decoding, and a mutator that never corrupts anything would look identical to a working run.
        Assert.True(decoded > 0,
            $"No {filterName} mutant decoded, so the fuzzer never exercised the decode path. " +
            "The seed payload or the mutations are too destructive to be reaching the filter.");

        // NOT "declined > 0". Four of these five filters are lenient BY DESIGN — ASCIIHex, ASCII85
        // and LZW skip or stop on bad input and return what they have, matching another reader — so
        // demanding a refusal would assert a contract they deliberately do not have, and would fail
        // on a perfectly healthy filter. What must be true for every filter is that corrupting the
        // payload reached the decoder and changed its answer.
        Assert.True(differed + declined > 0,
            $"Every {filterName} mutant produced byte-identical output to the pristine seed, so the " +
            "mutations never reached the decoder. The fixture is not testing the filter.");
    }

    /// <summary>
    /// Every filter must respect the ceiling its caller declared, on a payload built to expand as
    /// hard as that filter can.
    /// </summary>
    /// <remarks>
    /// <para>This is separate from the mutation loop because maximal expansion is a property of a
    /// <i>deliberately shaped</i> payload, not something a bit-flip stumbles onto — and because the
    /// failure it looks for is silent. A filter that ignores <see cref="StreamFilter.MaxOutputBytes"/>
    /// returns a perfectly valid oversized buffer and throws nothing, so the exception-type harness
    /// above is structurally blind to it.</para>
    /// <para>Each payload is sized to demand roughly 4x the ceiling, so a filter that honours the
    /// ceiling refuses and one that ignores it is caught by the length assertion rather than by
    /// running the host out of memory.</para>
    /// </remarks>
    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    [InlineData("FlateDecode")]
    public void MutatedPayloadRespectsTheOutputCeiling(string filterName)
    {
        byte[] bomb = MaximalExpansionPayload(filterName);

        StreamFilter filter = Create(filterName);
        filter.MaxOutputBytes = CallerCeiling;

        byte[] result;
        try
        {
            result = filter.Decode(bomb, null);
        }
        catch (InvalidDataException) { return; }   // refused at the ceiling — the correct answer
        catch (PdfFilterException) { return; }
        catch (PdfException) { return; }

        // POTENCY. Reaching here means the filter did NOT refuse, so the payload must at least have
        // reached the bound — otherwise the length assertion below is satisfied by a payload that
        // never approached it, and the test passes with the ceiling check deleted.
        //
        // LZW's did exactly that for its whole life: its encoder used the /EarlyChange 0 widen
        // convention against a decoder implementing 1, so the stream desynchronised at 3.1% of the
        // ceiling and returned normally. Nothing here could tell that from a filter honouring a
        // bound it never met. ASCIIHexDecode passes this legitimately — it contracts 2:1, so a
        // 4x-ceiling payload still yields 2x.
        Assert.True(result.LongLength >= CallerCeiling,
            $"{filterName} neither refused nor reached the ceiling: {bomb.LongLength:N0} bytes in, " +
            $"{result.LongLength:N0} out, against a {CallerCeiling:N0} ceiling. The fixture is a dud " +
            "— the assertion below would pass with the ceiling check removed from the filter.");

        // The invariant is "honour the ceiling OR do not amplify", not "honour the ceiling".
        // A filter whose output cannot exceed its input needs no ceiling to be bounded: the caller
        // already holds the input and therefore already knows the bound. Asserting the ceiling
        // alone would fail ASCIIHexDecode, which is 2 chars to 1 byte and cannot amplify at all —
        // a false finding against the filter that is behaving best.
        long bound = Math.Max(CallerCeiling, bomb.LongLength);

        Assert.True(result.LongLength <= bound,
            $"{filterName} amplified past the caller's MaxOutputBytes: asked for at most " +
            $"{CallerCeiling:N0} bytes, produced {result.LongLength:N0} from a " +
            $"{bomb.LongLength:N0}-byte payload. A filter that amplifies AND does not consult the " +
            "ceiling cannot be bounded by the caller that set it, and it returns silently rather " +
            "than throwing — so no exception-type harness can see it.");
    }

    /// <summary>
    /// LZW that stops on an unaddressable code must say why. A silent truncation returned as if it
    /// were the whole stream is the failure mode this codebase refuses everywhere else, and the
    /// slot that reports it had no test of any kind.
    /// </summary>
    /// <remarks>
    /// <para>The fixture emits <c>Clear</c>, a literal <c>'A'</c>, and then code 400 — inside the
    /// 9-bit width but well past the 258 entries the table holds after a clear, so it is
    /// unaddressable rather than merely unexpected. That is the one path
    /// <see cref="LzwDecodeFilter.LastTruncationReason"/> is written on.</para>
    /// <para><b>The literal is load-bearing, not padding.</b> The decoder's <c>ClearTable</c> branch
    /// consumes the following code inline and acts on it only <c>if (code &lt; 256)</c>, with no
    /// else — so a bad code in that one position is discarded in silence and never reaches the
    /// reporting branch below it. Putting 400 immediately after the clear therefore tests nothing,
    /// which is how this test was first written.</para>
    /// </remarks>
    [Fact]
    public void CorruptLzwCode_ReportsTheTruncationRatherThanReturningSilently()
    {
        // Clear (256), 'A' (65), then 400 — a code no entry has been assigned to yet.
        byte[] corrupt = PackBits("100000000" + "001000001" + "110010000");

        var filter = new LzwDecodeFilter();
        byte[] result = filter.Decode(corrupt, null);

        Assert.NotNull(LzwDecodeFilter.LastTruncationReason);
        Assert.Contains("400", LzwDecodeFilter.LastTruncationReason);

        // The literal before the corrupt code must have survived, or the fixture stopped earlier
        // than intended and the reason above is about a different failure.
        Assert.Equal(new byte[] { (byte)'A' }, result);
    }

    /// <summary>
    /// A stream whose input runs out part-way through a code is <b>not</b> reported, and this test
    /// records that rather than asserting a contract the decoder does not have.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a known gap, pinned here so the next reader does not have to rediscover
    /// which of the two truncation paths is covered.</b>
    /// <see cref="LzwDecodeFilter.LastTruncationReason"/> is written on the unaddressable-code path
    /// only; exhausting the input mid-code simply ends the loop. Both produce a short buffer that
    /// is byte-for-byte indistinguishable from a stream that legitimately ended there, which is
    /// exactly the ambiguity the slot's own doc comment says it exists to remove — so the slot
    /// covers one of the two ways in.</para>
    /// <para>A PDF LZW stream is supposed to end with EOD (257), so running out of bits without one
    /// is malformed input rather than a clean end. Closing it is a two-line change; it is left
    /// open here because this file is the instrument, and changing decoder behaviour from inside
    /// the harness that measures it is how a fuzzer stops being evidence.</para>
    /// </remarks>
    [Fact]
    public void TruncatedLzwPayload_EndsSilently_TheKnownGapInTheTruncationSlot()
    {
        byte[] full = LzwAbc();          // Clear, 'A', 'B', 'C', EOD — 45 bits in 6 bytes.

        // Cut mid-code: 4 bytes is 32 bits, which holds three 9-bit codes and 5 orphan bits, so the
        // decoder runs out of input part-way through the fourth code rather than reaching EOD.
        var cut = new byte[4];
        Array.Copy(full, cut, cut.Length);

        var filter = new LzwDecodeFilter();
        byte[] result = filter.Decode(cut, null);

        Assert.True(result.Length < 3,
            $"The cut stream should not have produced the whole 'ABC' payload; got {result.Length} " +
            "bytes, so the fixture did not actually truncate anything and the pin below is vacuous.");

        Assert.Null(LzwDecodeFilter.LastTruncationReason);
    }

    /// <summary>
    /// The instrument's own guard: every mutation kind must actually change the payload, or the
    /// decodes above are all of a pristine stream and the whole theory is theatre.
    /// </summary>
    [Fact]
    public void TheMutator_ActuallyChangesThePayload()
    {
        byte[] original = SeedPayload("FlateDecode");
        var rng = new Random(Seed);

        for (PayloadMutation k = 0; k < PayloadMutation.Count; k++)
        {
            byte[] mutant = Mutate(original, k, rng);
            Assert.False(
                mutant.Length == original.Length && SequenceEqual(mutant, original),
                $"mutation {k} produced an identical payload");
        }
    }

    private static bool SequenceEqual(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Base64 of the failing payload, capped so one finding cannot flood the log.</summary>
    private static string Replay(byte[] payload)
    {
        const int MaxReplayBytes = 3 * 1024;
        if (payload.Length <= MaxReplayBytes) return Convert.ToBase64String(payload);
        var head = new byte[MaxReplayBytes];
        Array.Copy(payload, head, MaxReplayBytes);
        return Convert.ToBase64String(head) + "\" /* truncated from " + payload.Length + " bytes */ \"";
    }

    // =====================================================================
    //  Mutations
    // =====================================================================

    private enum PayloadMutation
    {
        /// <summary>Flip one bit. Finds off-by-one reads and bad code-width arithmetic.</summary>
        BitFlip,
        /// <summary>Cut the payload short, mid-token.</summary>
        Truncate,
        /// <summary>Overwrite a run with noise, so a structure becomes unparseable mid-way.</summary>
        Noise,
        /// <summary>Append garbage past the end-of-data marker.</summary>
        Extend,
        /// <summary>
        /// Repeat a slice many times. Targets the expanding filters specifically: a run that is
        /// individually legal becomes an amplification when repeated.
        /// </summary>
        RepeatRun,

        // HugeNumber, which ParserFuzzTests has, is deliberately absent: these payloads are binary,
        // so there is no ASCII number to overwrite and the mutation would be indistinguishable
        // from Noise.
        Count,
    }

    private static byte[] Mutate(byte[] input, PayloadMutation kind, Random rng)
    {
        byte[] b = (byte[])input.Clone();

        // Deliberately low. A guard set above a seed's length silently turns every mutation into a
        // no-op, and the theory then fuzzes a pristine payload 500 times while reporting success —
        // as an 8-byte guard would do to the 6-byte LZW seed. The `differed + declined > 0` floor
        // catches that.
        if (b.Length < 4) return b;

        switch (kind)
        {
            case PayloadMutation.BitFlip:
                {
                    int i = rng.Next(b.Length);
                    b[i] ^= (byte)(1 << rng.Next(8));
                    return b;
                }

            case PayloadMutation.Truncate:
                {
                    int keep = rng.Next(2, b.Length);
                    var cut = new byte[keep];
                    Array.Copy(b, cut, keep);
                    return cut;
                }

            case PayloadMutation.Noise:
                {
                    int start = rng.Next(b.Length - 2);
                    int len = Math.Min(rng.Next(1, 16), b.Length - start);
                    for (int i = 0; i < len; i++) b[start + i] = (byte)rng.Next(256);
                    return b;
                }

            case PayloadMutation.Extend:
                {
                    int extra = rng.Next(1, 64);
                    var grown = new byte[b.Length + extra];
                    Array.Copy(b, grown, b.Length);
                    for (int i = 0; i < extra; i++) grown[b.Length + i] = (byte)rng.Next(256);
                    return grown;
                }

            case PayloadMutation.RepeatRun:
                {
                    int start = rng.Next(Math.Max(1, b.Length / 2));
                    int len = Math.Min(rng.Next(1, 8), b.Length - start);
                    int times = rng.Next(2, 64);
                    var grown = new byte[b.Length + len * times];
                    Array.Copy(b, grown, b.Length);
                    for (int t = 0; t < times; t++)
                        Array.Copy(b, start, grown, b.Length + t * len, len);
                    return grown;
                }

            default:
                return b;
        }
    }

    // =====================================================================
    //  Seed payloads — valid input, so a mutation corrupts something real
    // =====================================================================

    private static StreamFilter Create(string filterName) => filterName switch
    {
        "ASCIIHexDecode"  => new AsciiHexDecodeFilter(),
        "ASCII85Decode"   => new Ascii85DecodeFilter(),
        "LZWDecode"       => new LzwDecodeFilter(),
        "RunLengthDecode" => new RunLengthDecodeFilter(),
        "FlateDecode"     => new FlateDecodeFilter(),
        _ => throw new ArgumentOutOfRangeException(nameof(filterName), filterName, "unknown filter"),
    };

    private static byte[] SeedPayload(string filterName) => filterName switch
    {
        // "Hello" plus the '>' terminator.
        "ASCIIHexDecode"  => Encoding.ASCII.GetBytes("48656C6C6F>"),

        // "Man " is the canonical ASCII85 vector; the 'z' exercises the four-zero-bytes shortcut,
        // which is the one path in this filter that expands rather than contracts.
        "ASCII85Decode"   => Encoding.ASCII.GetBytes("9jqo^z9jqo^~>"),

        // A long run rather than the 5-code "ABC" vector: a 6-byte payload gives a bit-flip almost
        // nothing structural to land on, and mutations that shorten it fall below the mutator's
        // own minimum length.
        "LZWDecode"       => LzwLiteralRun(40),

        // Two literal runs and one repeat run, then EOD.
        "RunLengthDecode" => new byte[]
                             { 0x03, (byte)'H', (byte)'e', (byte)'l', (byte)'l',
                               0x00, (byte)'o',
                               0x81, (byte)'!',      // 128 copies of '!'
                               0x80 },               // EOD

        "FlateDecode"     => Deflate(Encoding.ASCII.GetBytes(
                                 "Fuzz me. This string is long enough to compress to more than a "
                               + "single deflate block boundary, so a bit flip lands somewhere "
                               + "structural rather than always in the header.")),

        _ => throw new ArgumentOutOfRangeException(nameof(filterName), filterName, "unknown filter"),
    };

    /// <summary>
    /// A payload built to expand as hard as the named filter can, sized to demand roughly
    /// <c>4 x CallerCeiling</c>.
    /// </summary>
    private static byte[] MaximalExpansionPayload(string filterName)
    {
        switch (filterName)
        {
            case "ASCIIHexDecode":
                {
                    // Two hex digits produce one byte, so this filter cannot outgrow its input at
                    // all. Included so the theory covers every filter uniformly and the claim in
                    // StreamFilter's own doc comment is checked rather than assumed.
                    var hex = new byte[CallerCeiling * 4];
                    for (int i = 0; i < hex.Length; i++) hex[i] = (byte)'A';
                    return hex;
                }

            case "ASCII85Decode":
                {
                    // 'z' is one input byte producing four output bytes — a 4x expansion, and the
                    // reason ASCII85 does not belong in StreamFilter's "cannot outgrow their input
                    // by enough to matter" group.
                    var z = new byte[CallerCeiling];
                    for (int i = 0; i < z.Length; i++) z[i] = (byte)'z';
                    return z;
                }

            case "LZWDecode":
                return LzwSelfExtendingRun();

            case "RunLengthDecode":
                {
                    // Each 2-byte pair expands to 128 bytes: a 64x amplification.
                    long pairs = CallerCeiling * 4 / 128;
                    var rle = new byte[pairs * 2 + 1];
                    for (long i = 0; i < pairs; i++)
                    {
                        rle[i * 2]     = 0x81;
                        rle[i * 2 + 1] = 0xAA;
                    }
                    rle[rle.Length - 1] = 0x80;   // EOD
                    return rle;
                }

            case "FlateDecode":
                {
                    var zeros = new byte[CallerCeiling * 4];
                    return Deflate(zeros);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(filterName), filterName, "unknown filter");
        }
    }

    /// <summary>Clear, 'A', 'B', 'C', EOD as 9-bit MSB-first codes.</summary>
    private static byte[] LzwAbc() => PackBits("100000000" + "001000001" + "001000010" + "001000011"
                                             + "100000001");

    /// <summary>
    /// Clear, <paramref name="count"/> printable literals, EOD — all at 9 bits.
    /// </summary>
    /// <remarks>
    /// The table reaches 258 + <paramref name="count"/> - 1 entries, so for any count below ~250 the
    /// code width never has to grow and every code in the stream is exactly 9 bits. Keeping the
    /// width fixed is what makes a bit flip land on a code value rather than on a width transition,
    /// which would desynchronise the whole remainder and make every mutant look alike.
    /// </remarks>
    private static byte[] LzwLiteralRun(int count)
    {
        var bits = new StringBuilder();
        bits.Append("100000000");                       // Clear
        for (int i = 0; i < count; i++)
        {
            int literal = 'A' + (i % 26);
            bits.Append(Convert.ToString(literal, 2).PadLeft(9, '0'));
        }
        bits.Append("100000001");                       // EOD
        return PackBits(bits.ToString());
    }

    /// <summary>
    /// LZW whose dictionary entries chain into ever-longer strings: Clear, then a literal, then
    /// each newly-created code re-emitted so entry <i>n</i> is entry <i>n-1</i> plus one byte.
    /// A few kilobytes of codes reconstruct megabytes of output.
    /// </summary>
    /// <remarks>
    /// <b>The widen condition must match the decoder's, or the bomb is a dud.</b> It read
    /// <c>code &gt;= (1 &lt;&lt; width)</c> — the <c>/EarlyChange 0</c> convention — while
    /// <c>LzwDecodeFilter</c> widens at <c>table.Count &gt;= (1 &lt;&lt; codeSize) - earlyChange</c>
    /// with <c>earlyChange</c> defaulting to 1. Code 511 was written at 9 bits and read at 10,
    /// desynchronising the stream: it decoded 32 385 bytes — 3.1% of the ceiling — and returned
    /// normally, having consumed 286 of its 5 408 bytes. The test that used it promised a ceiling it
    /// never came near. Corrected, the same payload decodes 7 370 880 bytes and is refused.
    /// </remarks>
    private static byte[] LzwSelfExtendingRun()
    {
        var bits = new StringBuilder();
        bits.Append("100000000");     // Clear
        bits.Append("001000001");     // 'A' — creates no entry yet

        // 258 is the first assignable code. Emitting the code just created makes the next entry one
        // byte longer than the last, so total output grows quadratically in the number of codes.
        int code = 258;
        int width = 9;
        while (code < 4096 && bits.Length < 64 * 1024)
        {
            // `- 1` is /EarlyChange 1, the PDF default and what LzwDecodeFilter implements.
            if (code >= (1 << width) - 1 && width < 12) width++;
            bits.Append(Convert.ToString(code, 2).PadLeft(width, '0'));
            code++;
        }

        while (bits.Length % 8 != 0) bits.Append('0');
        return PackBits(bits.ToString());
    }

    private static byte[] PackBits(string bits)
    {
        var padded = new StringBuilder(bits);
        while (padded.Length % 8 != 0) padded.Append('0');

        var packed = new byte[padded.Length / 8];
        for (int i = 0; i < padded.Length; i++)
            if (padded[i] == '1')
                packed[i / 8] |= (byte)(0x80 >> (i % 8));
        return packed;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);   // zlib header
        ms.WriteByte(0x9C);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }
}
