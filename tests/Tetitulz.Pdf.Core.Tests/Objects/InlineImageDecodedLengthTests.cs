// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// A Flate-filtered inline image with no <c>/L</c> gets an <b>exact</b> payload length, found by
/// decoding a candidate and checking it produced the sample count the geometry demands.
/// </summary>
/// <remarks>
/// <para><b>The case the other three strategies cannot reach.</b> A declared <c>/L</c> settles it;
/// <c>ComputeSelfDelimiting</c> covers the filters carrying an end-of-data marker (<c>/AHx</c>,
/// <c>/A85</c>, <c>/RL</c>, <c>/DCT</c>); <c>ComputeUnfiltered</c> covers no filter at all. Flate
/// and LZW have neither a marker nor a geometry-derived <i>encoded</i> size, so the only previous
/// answer was the "whitespace-delimited <c>EI</c> followed by printable ASCII" heuristic.</para>
///
/// <para><b>The fixture is built so the heuristic is wrong</b>, and the first assertion checks that
/// it is — otherwise the test would pass against the heuristic too and prove nothing. That is the
/// difference between a test of this feature and one that merely exercises it. The marker is
/// planted in the samples rather than hoped for in the compressed output; see
/// <c>FlateInlineImage</c> for why searching for it does not work.</para>
/// </remarks>
public sealed class InlineImageDecodedLengthTests
{
    [Fact]
    public void AFlatePayloadContainingAFalseTerminatorIsMeasuredByDecodingIt()
    {
        (byte[] content, int payloadStart, int payloadLength) = FlateInlineImage();

        PdfDictionary dict = ImageDictionary();

        // The premise: the heuristic ends the image early on this payload.
        int heuristic = FirstHeuristicTerminator(content, payloadStart);
        Assert.True(heuristic >= 0, "the fixture carries no false terminator, so it cannot "
                                    + "distinguish decoding from the heuristic");
        Assert.NotEqual(payloadLength, heuristic);

        int measured = InlineImageLength.ComputeByDecoding(dict, content, payloadStart);

        Assert.Equal(payloadLength, measured);
    }

    /// <summary>
    /// The parser reads the whole payload rather than stopping at the false terminator.
    /// </summary>
    /// <remarks>
    /// Asserted through <c>ContentStreamParser</c> rather than the length helper, because the
    /// length is only useful if the operation that follows the image is the one the page actually
    /// contains. Stopping early makes the parser hallucinate operators out of pixel bytes, which is
    /// the corruption the whole subsystem exists to prevent.
    /// </remarks>
    [Fact]
    public void TheParserReadsTheWholeFlatePayloadAndSeesTheOperatorAfterIt()
    {
        (byte[] content, _, _) = FlateInlineImage();

        var ops = ContentStreamParser.Parse(content);

        Assert.Contains(ops, o => o.Operator == "BI");
        Assert.Contains(ops, o => o.Operator == "Q");
    }

    /// <summary>
    /// The control: a payload the geometry does not match is refused rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Without this, "returns a length" would be satisfied by a version that accepted the first
    /// candidate that merely decoded. The sample count is the whole check, so a dictionary claiming
    /// a different size must produce no answer.
    /// </remarks>
    [Fact]
    public void APayloadThatDoesNotMatchTheDeclaredGeometryIsRefused()
    {
        (byte[] content, int payloadStart, _) = FlateInlineImage();

        PdfDictionary tooTall = ImageDictionary();
        tooTall.Set(PdfName.H, new PdfNumber(Height + 3));   // same bytes, different claim

        Assert.Equal(-1, InlineImageLength.ComputeByDecoding(tooTall, content, payloadStart));

        // Both directions, because they fail differently. A claim that is too LARGE is refused by
        // any comparison at all — the decode is short of it. A claim that is too SMALL is refused
        // only by an exact one: the payload decodes to more than the geometry asks for, and
        // "at least enough" would accept it and end the image inside its own samples.
        PdfDictionary tooShort = ImageDictionary();
        tooShort.Set(PdfName.H, new PdfNumber(Height - 3));

        Assert.Equal(-1, InlineImageLength.ComputeByDecoding(tooShort, content, payloadStart));
    }


    /// <summary>
    /// A chain of two general-purpose filters is measured by running both, not refused for being a
    /// chain.
    /// </summary>
    /// <remarks>
    /// <para>The sample count is not what the outermost decode produces, but it <i>is</i> what the
    /// last stage produces — so a <c>/Filter</c> array of more than one entry need not fall to the
    /// heuristic and leave the page unspliceable.</para>
    ///
    /// <para><b>The chain is <c>[/Fl /RL]</c> for a reason the fixture depends on.</b> Run-length
    /// encoding copies a literal run byte for byte, and the payload is stored rather than
    /// compressed, so the false terminator planted in the samples survives all the way into the
    /// bytes on the page — which is what makes the heuristic wrong here and the test worth
    /// running.</para>
    /// </remarks>
    [Fact]
    public void AChainOfGeneralPurposeFiltersIsMeasuredByDecodingAllOfIt()
    {
        (byte[] content, int payloadStart, int payloadLength) = ChainedInlineImage();

        int heuristic = FirstHeuristicTerminator(content, payloadStart);
        Assert.True(heuristic >= 0, "the fixture carries no false terminator, so it cannot "
                                    + "distinguish decoding from the heuristic");
        Assert.NotEqual(payloadLength, heuristic);

        int measured = InlineImageLength.ComputeByDecoding(ChainDictionary(), content, payloadStart);

        Assert.Equal(payloadLength, measured);
    }

    /// <summary>The same document, read through the parser, which is where the length is used.</summary>
    [Fact]
    public void TheParserReadsTheWholeChainedPayloadAndSeesTheOperatorAfterIt()
    {
        (byte[] content, _, _) = ChainedInlineImage();

        var ops = ContentStreamParser.Parse(content);

        Assert.Contains(ops, o => o.Operator == "BI");
        Assert.Contains(ops, o => o.Operator == "Q");
    }

    /// <summary>An image codec in the chain is refused rather than measured.</summary>
    /// <remarks>
    /// <c>/DCT</c> is a passthrough stage in this library, so the "decoded" length would be the JPEG
    /// codestream's and would match the geometry only by accident. <b>Turning uncertain into
    /// confidently wrong is the failure this whole subsystem exists to avoid</b>, and the heuristic
    /// path at least reports itself as uncertain. The same reasoning excludes <c>/CCF</c>, whose
    /// decoder can return a full-sized buffer of wrong rows for a truncated payload.
    /// </remarks>
    [Theory]
    [InlineData("DCT")]
    [InlineData("CCF")]
    [InlineData("JPXDecode")]
    public void AChainContainingAnImageCodecIsRefused(string codec)
    {
        (byte[] content, int payloadStart, _) = ChainedInlineImage();

        PdfDictionary dict = ChainDictionary();
        var chain = new PdfArray();
        chain.Add(PdfName.Fl);
        chain.Add(new PdfName(codec));
        dict.Set(PdfName.F, chain);

        Assert.Equal(-1, InlineImageLength.ComputeByDecoding(dict, content, payloadStart));
    }

    /// <summary>
    /// A chain whose outermost filter is not <c>/Fl</c> or <c>/LZW</c> is left to the strategy that
    /// can answer it exactly without decoding anything.
    /// </summary>
    /// <remarks>
    /// Not a limitation: <c>ComputeSelfDelimiting</c> reads an <c>/AHx</c> payload's <c>&gt;</c>
    /// straight out of the stored bytes, whatever filters sit inside it. Decoding candidates would
    /// reach the same answer more slowly and less certainly — the gate is on the outermost filter
    /// because that is the one whose truncation makes a wrong candidate <i>fail</i>.
    /// </remarks>
    [Fact]
    public void AChainWhoseOutermostFilterIsSelfDelimitingIsNotMeasuredByDecoding()
    {
        // A real [/AHx /Fl] payload, so the refusal is the gate's and not the fixture's: decoding
        // it would in fact reach the right answer, which is exactly what makes a fixture that
        // cannot be decoded at all useless as a test of the gate.
        (byte[] content, int payloadStart, int payloadLength) = AsciiHexOutermostInlineImage();

        var chain = new PdfArray();
        chain.Add(PdfName.AHx);
        chain.Add(PdfName.Fl);

        PdfDictionary dict = ImageDictionary();
        dict.Set(PdfName.F, chain);

        Assert.Equal(-1, InlineImageLength.ComputeByDecoding(dict, content, payloadStart));

        // And the strategy that owns this case does answer it, so nothing is lost by the gate.
        Assert.Equal(payloadLength,
            InlineImageLength.ComputeSelfDelimiting(dict, content, payloadStart));
    }

    /// <summary>
    /// A per-stage <c>/DecodeParms</c> is applied to its own stage, not to all of them.
    /// </summary>
    /// <remarks>
    /// <para>The chain is <c>[/Fl /Fl]</c> and only the second stage carries a predictor, which is
    /// the shape that separates "positional" from "the first entry, reused". A PNG predictor adds a
    /// filter-type byte per row, so applying it to the wrong stage — or to none — leaves 600 bytes
    /// where the geometry wants 576, and the candidate is refused.</para>
    /// <para><b>The failure is silent in the other direction too</b>: a predictor applied where none
    /// was asked for turns pixel data into de-filtered nonsense of a plausible length. That is why
    /// the alignment is asserted rather than assumed.</para>
    /// </remarks>
    [Fact]
    public void APerStageDecodeParmsIsAppliedToItsOwnStage()
    {
        (byte[] content, int payloadStart, int payloadLength) = PredictedInlineImage();

        int measured = InlineImageLength.ComputeByDecoding(
            PredictedDictionary(), content, payloadStart);

        Assert.Equal(payloadLength, measured);
    }

    /// <summary>
    /// An oversized candidate is refused without inflating it, so a decompression bomb costs a
    /// bounded amount of work per candidate.
    /// </summary>
    /// <remarks>
    /// <para><b>Every candidate is a decode, and the candidates come from a heuristic.</b> An inline
    /// image with no <c>/L</c> can offer many <c>EI</c>-shaped positions, and each one inflated under
    /// the 512 MB per-filter default would let one crafted payload buy an unbounded multiple of half a
    /// gigabyte from a scan that is only looking for a boundary. The ceiling is derived from the
    /// answer being sought: a candidate that decodes past a small multiple of
    /// the raster is already wrong.</para>
    ///
    /// <para><b>The assertion is on work done, not on a clock.</b> Elapsed time under a
    /// whole-solution run measures the machine's backlog; bytes allocated on this thread measure
    /// what the guard actually prevented, and the separation is three orders of magnitude — the
    /// payload inflates to 32 MiB and the ceiling admits about 66 KiB.</para>
    /// </remarks>
    [Fact]
    public void AnOversizedFlateCandidateIsRefusedWithoutInflatingIt()
    {
        byte[] payload = FlateEncodeFilter.Encode(new byte[32 * 1024 * 1024]);
        byte[] content = Assemble(payload, out int payloadStart);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int measured = InlineImageLength.ComputeByDecoding(ImageDictionary(), content, payloadStart);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(-1, measured);
        Assert.True(allocated < 4L * 1024 * 1024,
            $"the candidate was inflated: {allocated} bytes allocated for a {Width}x{Height} image");
    }

    /// <summary>
    /// A payload whose outermost stage says where it ends — a zlib stream's final block and trailer —
    /// is settled by one verifying decode: the end is read from the framing, not found by decoding at
    /// each <c>EI</c>. The answer cannot show which, so the decode count is the evidence.
    /// </summary>
    [Fact]
    public void AFlatePayloadIsMeasuredByOneVerifyingDecode()
    {
        (byte[] content, int payloadStart, int payloadLength) = FlateInlineImage();

        InlineImageLength.CandidateDecodes = 0;
        Assert.Equal(payloadLength, InlineImageLength.ComputeByDecoding(ImageDictionary(), content, payloadStart));
        Assert.Equal(1, InlineImageLength.CandidateDecodes);
    }

    /// <summary>
    /// Three thousand images whose payloads are not Flate at all and so never verify: each image's
    /// scan is bounded to <see cref="InlineImageLength.MaxDecodeCandidates"/> decodes. Unbounded, each
    /// image tried every later <c>EI</c> in the stream — about N(N+1)/2 decodes, each copying its
    /// slice — for a stream of a hundred kilobytes that passes every content ceiling.
    /// </summary>
    [Fact]
    public void ImagesWhosePayloadsNeverVerify_AreBoundedToAFewCandidateDecodesEach()
    {
        const int images = 3000;
        var sb = new StringBuilder();
        for (int i = 0; i < images; i++) sb.Append("BI /W 2 /H 2 /BPC 8 /CS /G /F /Fl ID AA EI\n");
        byte[] content = Encoding.ASCII.GetBytes(sb.ToString());

        InlineImageLength.CandidateDecodes = 0;
        var ops = ContentStreamParser.Parse(content);

        Assert.Equal(images, ops.Count(o => o.Operator == "BI"));
        Assert.InRange(InlineImageLength.CandidateDecodes, images, (long)images * InlineImageLength.MaxDecodeCandidates);
    }

    // ------------------------------------------------------------------ boundaries

    /// <summary>
    /// An image with no width or height has no samples to measure: the geometry is refused (-1),
    /// not answered as a payload of zero bytes. The control is the same dictionary at 1x1.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void AnImageWithNoAreaHasNoMeasurableLength(int width, int height)
    {
        Assert.Equal(-1, InlineImageLength.ComputeUnfiltered(Unfiltered(width, height, bpc: 8)));
        Assert.Equal(1, InlineImageLength.ComputeUnfiltered(Unfiltered(1, 1, bpc: 8)));
    }

    /// <summary>
    /// 32 bits per component is not one of §8.9.5.1's values (1, 2, 4, 8, 16), so the geometry is
    /// refused rather than computed. The control is 16 bits, the largest legal depth.
    /// </summary>
    [Fact]
    public void ABitDepthOutsideTheSpecIsRefused()
    {
        Assert.Equal(-1, InlineImageLength.ComputeUnfiltered(Unfiltered(2, 2, bpc: 32)));
        Assert.Equal(8, InlineImageLength.ComputeUnfiltered(Unfiltered(2, 2, bpc: 16)));
    }

    /// <summary>
    /// A candidate <c>E</c> as the very last byte of the stream has no <c>I</c> after it, and is
    /// passed over rather than read past the end of the buffer.
    /// </summary>
    [Fact]
    public void AnEAtTheVeryEndOfTheStreamIsNotReadPast()
    {
        byte[] content = Encoding.ASCII.GetBytes("abc E");
        Assert.Equal(-1, InlineImageLength.ComputeByDecoding(ImageDictionary(), content, 0));
    }

    /// <summary>
    /// A tiny image's decode stages are bounded by a floor, not by four times its raster alone. A
    /// 1x1 image whose outer Flate stage inflates to a whitespace-padded hex text of a few hundred
    /// bytes is legal (§7.4.2 allows whitespace), and is still measured.
    /// </summary>
    [Fact]
    public void ATinyImagesIntermediateStageIsBoundedByTheFloor()
    {
        byte[] hex = Encoding.ASCII.GetBytes("7f" + new string(' ', 300) + ">");
        byte[] payload = FlateEncodeFilter.Encode(hex);

        var dict = Unfiltered(1, 1, bpc: 8);
        var chain = new PdfArray();
        chain.Add(PdfName.Fl);
        chain.Add(PdfName.AHx);
        dict.Set(PdfName.F, chain);

        var bytes = new List<byte>();
        bytes.AddRange(payload);
        bytes.AddRange(Encoding.ASCII.GetBytes("\nEI\nQ\n"));

        Assert.Equal(payload.Length, InlineImageLength.ComputeByDecoding(dict, bytes.ToArray(), 0));
    }

    private static PdfDictionary Unfiltered(int width, int height, int bpc)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.W, new PdfNumber(width));
        dict.Set(PdfName.H, new PdfNumber(height));
        dict.Set(PdfName.BPC, new PdfNumber(bpc));
        dict.Set(PdfName.CS, PdfName.G);
        return dict;
    }

    // ------------------------------------------------------------------

    private const int Width = 24;
    private const int Height = 24;

    /// <summary>The image's parameters: 24x24, 8-bpc grey, Flate, no <c>/L</c>.</summary>
    private static PdfDictionary ImageDictionary()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.W, new PdfNumber(Width));
        dict.Set(PdfName.H, new PdfNumber(Height));
        dict.Set(PdfName.BPC, new PdfNumber(8));
        dict.Set(PdfName.CS, PdfName.G);
        dict.Set(PdfName.F, PdfName.Fl);
        return dict;
    }

    /// <summary>
    /// A content stream holding one Flate inline image, plus the payload's offset and true length.
    /// </summary>
    /// <remarks>
    /// <para><b>The false terminator is placed deliberately, not hoped for.</b> The first attempt
    /// compressed pseudo-random samples and searched the output for a sequence the heuristic
    /// accepts; across 400 seeds none appeared, which is unsurprising — the sequence is four
    /// specific bytes and random data does not compress, so the search space is the payload itself
    /// and the odds are about one in ten million per position.</para>
    /// <para>So the marker goes into the <b>samples</b>, and the payload is stored rather than
    /// compressed so those bytes survive into it verbatim. That is not a contrivance: a stored
    /// deflate block is what a Flate encoder emits for incompressible data, and grey samples
    /// spelling <c>0x20 'E' 'I' 0x20</c> are just four ordinary pixel values.</para>
    /// </remarks>
    private static (byte[] Content, int PayloadStart, int PayloadLength) FlateInlineImage()
    {
        byte[] samples = Samples();
        byte[] payload = FlateEncodeFilter.Encode(samples, CompressionLevel.NoCompression);

        byte[] content = Assemble(payload, out int payloadStart);
        return (content, payloadStart, payload.Length);
    }

    /// <summary>
    /// 8-bpc grey samples carrying the byte sequence the terminator heuristic mistakes for the end
    /// of the image.
    /// </summary>
    private static byte[] Samples()
    {
        var samples = new byte[Width * Height];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7 % 251 + 2);

        // " EI " in the middle of the picture: whitespace, E, I, whitespace - every condition the
        // heuristic tests, a long way from the real end.
        int at = samples.Length / 2;
        samples[at] = 0x20;
        samples[at + 1] = (byte)'E';
        samples[at + 2] = (byte)'I';
        samples[at + 3] = 0x20;
        return samples;
    }


    /// <summary>The chained image's parameters: 24x24, 8-bpc grey, <c>[/Fl /RL]</c>, no <c>/L</c>.</summary>
    private static PdfDictionary ChainDictionary()
    {
        PdfDictionary dict = ImageDictionary();
        var chain = new PdfArray();
        chain.Add(PdfName.Fl);
        chain.Add(PdfName.RL);
        dict.Set(PdfName.F, chain);
        return dict;
    }

    /// <summary>
    /// A content stream holding one <c>[/Fl /RL]</c> inline image, plus the payload's offset and
    /// true length.
    /// </summary>
    /// <remarks>
    /// The samples are run-length encoded and then stored uncompressed, so the false terminator
    /// planted among the pixels survives verbatim into the payload. Filters are listed in the order
    /// they were <i>applied</i> (§7.4.1), so <c>[/Fl /RL]</c> decodes Flate first and run-length
    /// second — the reverse of the order they were written in here.
    /// </remarks>
    private static (byte[] Content, int PayloadStart, int PayloadLength) ChainedInlineImage()
    {
        byte[] runLength = RunLengthEncodeFilter.Encode(Samples());
        byte[] payload = FlateEncodeFilter.Encode(runLength, CompressionLevel.NoCompression);

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(
            $"q\nBI /W {Width} /H {Height} /BPC 8 /CS /G /F [/Fl /RL] ID "));
        int payloadStart = bytes.Count;
        bytes.AddRange(payload);
        bytes.AddRange(Encoding.ASCII.GetBytes("\nEI\nQ\n"));

        return (bytes.ToArray(), payloadStart, payload.Length);
    }

    /// <summary>
    /// A content stream holding one <c>[/AHx /Fl]</c> inline image — hex on the outside, so the
    /// payload really is self-delimiting.
    /// </summary>
    private static (byte[] Content, int PayloadStart, int PayloadLength) AsciiHexOutermostInlineImage()
    {
        byte[] flate = FlateEncodeFilter.Encode(Samples(), CompressionLevel.NoCompression);

        var hex = new StringBuilder(flate.Length * 2 + 1);
        foreach (byte b in flate) hex.Append(b.ToString("x2"));
        hex.Append('>');

        byte[] payload = Encoding.ASCII.GetBytes(hex.ToString());

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(
            $"q\nBI /W {Width} /H {Height} /BPC 8 /CS /G /F [/AHx /Fl] ID "));
        int payloadStart = bytes.Count;
        bytes.AddRange(payload);
        bytes.AddRange(Encoding.ASCII.GetBytes("\nEI\nQ\n"));

        return (bytes.ToArray(), payloadStart, payload.Length);
    }

    /// <summary>The predicted image's parameters: <c>[/Fl /Fl]</c>, predictor on the second stage.</summary>
    private static PdfDictionary PredictedDictionary()
    {
        var chain = new PdfArray();
        chain.Add(PdfName.Fl);
        chain.Add(PdfName.Fl);

        var predictor = new PdfDictionary();
        predictor.Set(PdfName.Predictor, new PdfNumber(12));
        predictor.Set(PdfName.Colors, new PdfNumber(1));
        predictor.Set(PdfName.Columns, new PdfNumber(Width));
        predictor.Set(PdfName.BitsPerComponent, new PdfNumber(8));

        var parms = new PdfArray();
        parms.Add(PdfNull.Instance);
        parms.Add(predictor);

        PdfDictionary dict = ImageDictionary();
        dict.Set(PdfName.F, chain);
        dict.Set(PdfName.DP, parms);
        return dict;
    }

    /// <summary>
    /// A <c>[/Fl /Fl]</c> inline image whose inner stage carries a PNG predictor.
    /// </summary>
    /// <remarks>
    /// Every row is written with PNG filter type 0 (None), which is a legal choice for predictor 12
    /// — the type is per row and the decoder reads it — and keeps the fixture free of a predictor
    /// <i>encoder</i>, which Core does not have.
    /// </remarks>
    private static (byte[] Content, int PayloadStart, int PayloadLength) PredictedInlineImage()
    {
        byte[] samples = Samples();
        var predicted = new byte[Height * (Width + 1)];
        for (int row = 0; row < Height; row++)
        {
            predicted[row * (Width + 1)] = 0;   // PNG filter type: None
            Buffer.BlockCopy(samples, row * Width, predicted, row * (Width + 1) + 1, Width);
        }

        byte[] inner = FlateEncodeFilter.Encode(predicted, CompressionLevel.NoCompression);
        byte[] payload = FlateEncodeFilter.Encode(inner, CompressionLevel.NoCompression);

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(
            $"q\nBI /W {Width} /H {Height} /BPC 8 /CS /G /F [/Fl /Fl] " +
            $"/DP [null <</Predictor 12 /Colors 1 /Columns {Width} /BitsPerComponent 8>>] ID "));
        int payloadStart = bytes.Count;
        bytes.AddRange(payload);
        bytes.AddRange(Encoding.ASCII.GetBytes("\nEI\nQ\n"));

        return (bytes.ToArray(), payloadStart, payload.Length);
    }

    /// <summary>Wraps the payload in <c>q BI … ID … EI Q</c>.</summary>
    private static byte[] Assemble(byte[] payload, out int payloadStart)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(
            $"q\nBI /W {Width} /H {Height} /BPC 8 /CS /G /F /Fl ID "));
        payloadStart = bytes.Count;
        bytes.AddRange(payload);
        bytes.AddRange(Encoding.ASCII.GetBytes("\nEI\nQ\n"));
        return bytes.ToArray();
    }

    /// <summary>
    /// Payload length the "whitespace-delimited <c>EI</c> followed by a token boundary" heuristic
    /// would settle on, or -1.
    /// </summary>
    private static int FirstHeuristicTerminator(byte[] data, int start)
    {
        for (int p = start + 1; p + 1 < data.Length; p++)
        {
            if (data[p] != (byte)'E' || data[p + 1] != (byte)'I') continue;
            if (!IsWhitespace(data[p - 1])) continue;
            if (p + 2 < data.Length && !IsEndOfToken(data[p + 2])) continue;
            return p - 1 - start;
        }
        return -1;
    }

    private static bool IsWhitespace(byte b)
        => b == 0x20 || b == 0x0A || b == 0x0D || b == 0x09 || b == 0x0C || b == 0x00;

    private static bool IsEndOfToken(byte b)
        => IsWhitespace(b) || b == (byte)'/' || b == (byte)'[' || b == (byte)']'
           || b == (byte)'<' || b == (byte)'>' || b == (byte)'(' || b == (byte)')';
}
