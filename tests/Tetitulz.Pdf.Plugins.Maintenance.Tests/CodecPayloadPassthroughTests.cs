// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Every image pass that decodes and re-encodes samples must refuse a stream whose filter chain is
/// a <b>codec payload</b> — DCT, JPX, JBIG2, CCITT — and leave its bytes exactly as found.
/// </summary>
/// <remarks>
/// <b>Nothing asserted the refusal.</b> Three passes spelled the same predicate out by hand and
/// none of them had a test. The failure it guards against is not subtle: a pass that treats a
/// JPEG's entropy-coded bytes as raw samples writes back an image made of noise, under a
/// <c>/Filter</c> that no longer describes it.
///
/// <para><b>These tests pin the contract and they do NOT discriminate that predicate — said here
/// rather than left to be discovered.</b> Mutating it to accept a single non-Flate filter leaves
/// every case below green, including with the declared geometry made exactly consistent with the
/// payload so that no size check could be standing in for it. Whatever refuses these images, it is
/// not only the filter test: for every input that could be constructed here the predicate is
/// redundant defence in depth. It was consolidated because three identical spellings of one rule
/// are one rule, not because a test proved it load-bearing.</para>
///
/// <para>What remains is real: "a codec payload is never re-encoded" was asserted nowhere at all,
/// and these cases catch a regression in whichever guard is actually enforcing it. The control
/// matters for the same reason — it proves an unfiltered image still reaches the pass, so the
/// twelve refusals are refusals rather than a pass that quietly stopped running.</para>
///
/// <para>The three passes are opted into individually (all off by default) and are exercised
/// separately, so a failure names which one changed.</para>
/// </remarks>
public class CodecPayloadPassthroughTests
{
    private const int PayloadWidth = 8;
    private const int PayloadHeight = 32;

    /// <summary>
    /// Recognisable bytes, exactly an 8x32 grey image's worth so the declared geometry is exact.
    /// </summary>
    /// <remarks>
    /// The size is deliberate. With a payload whose length disagreed with the declared dimensions,
    /// a later sanity check refused the image on its own and the filter test never ran — a second,
    /// misleading reason for the mutation to survive, sitting on top of the real one.
    /// </remarks>
    private static readonly byte[] Payload = BuildPayload();

    private static byte[] BuildPayload()
    {
        var b = new byte[PayloadWidth * PayloadHeight];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(0xA0 + (i % 32));
        return b;
    }

    public static IEnumerable<object[]> CodecFilters => new[]
    {
        new object[] { "/DCTDecode" },
        new object[] { "/JPXDecode" },
        new object[] { "/JBIG2Decode" },
        new object[] { "/CCITTFaxDecode" },
    };

    [Theory]
    [MemberData(nameof(CodecFilters))]
    public void CcittPassLeavesACodecPayloadByteIdentical(string filter)
        => AssertPayloadSurvives(filter, o => o.UseCcittForBilevelImages = true);

    [Theory]
    [MemberData(nameof(CodecFilters))]
    public void IndexedPassLeavesACodecPayloadByteIdentical(string filter)
        => AssertPayloadSurvives(filter, o => o.ConvertImagesToIndexed = true);

    [Theory]
    [MemberData(nameof(CodecFilters))]
    public void PredictorPassLeavesACodecPayloadByteIdentical(string filter)
        => AssertPayloadSurvives(filter, o => o.UsePngPredictorForImages = true);

    private static void AssertPayloadSurvives(string filter, Action<OptimizationOptions> configure)
    {
        var options = new OptimizationOptions
        {
            // Off, so the only thing that can touch the image stream is the pass under test.
            CompressStreams = false,
            RemoveDuplicates = false,
            RemoveUnusedObjects = false,
        };
        configure(options);

        using var input = new MemoryStream(ImagePdf(filter));
        using var output = new MemoryStream();
        PdfOptimizer.Optimize(input, output, options);

        byte[] result = output.ToArray();

        Assert.True(IndexOf(result, Payload) >= 0,
            $"the {filter} payload was re-encoded or dropped; a codec payload must pass through untouched");
        Assert.Contains(filter, Encoding.Latin1.GetString(result));
    }

    /// <summary>
    /// The control: an UNFILTERED image is the shape these passes exist for, so it must still reach
    /// them. Without it, a predicate that rejected everything would satisfy all twelve cases above.
    /// </summary>
    [Fact]
    public void AnUnfilteredImageIsStillAcceptedByThePredictorPass()
    {
        var options = new OptimizationOptions
        {
            CompressStreams = false,
            RemoveDuplicates = false,
            RemoveUnusedObjects = false,
            UsePngPredictorForImages = true,
        };

        using var input = new MemoryStream(ImagePdf(filter: null));
        using var output = new MemoryStream();
        PdfOptimizer.Optimize(input, output, options);

        // The pass predicts and deflates, so the literal raw samples must NOT survive verbatim.
        Assert.True(IndexOf(output.ToArray(), RawSamples) < 0,
            "an unfiltered image should have been processed by the predictor pass, not passed through");
    }

    private const int RawSide = 32;

    /// <summary>
    /// A 32x32 grey ramp, constant down each column — ideal for the Up predictor, so the pass has a
    /// reason to act. The SIZE matters: a 2x2 fixture is too small to be worth predicting, and the
    /// control failed on it while all twelve codec cases passed. A control that fails beside
    /// passing cases is the cheapest signal that the fixture, not the code, is wrong.
    /// </summary>
    private static readonly byte[] RawSamples = BuildRamp();

    private static byte[] BuildRamp()
    {
        var b = new byte[RawSide * RawSide];
        for (int y = 0; y < RawSide; y++)
            for (int x = 0; x < RawSide; x++)
                b[y * RawSide + x] = (byte)(x * 8);
        return b;
    }

    private static byte[] ImagePdf(string filter)
    {
        byte[] data = filter == null ? RawSamples : Payload;
        string filterEntry = filter == null ? "" : " /Filter " + filter;
        int w = filter == null ? RawSide : PayloadWidth;
        int h = filter == null ? RawSide : PayloadHeight;
        const string content = "q 2 0 0 2 0 0 cm /Im1 Do Q";

        var acc = new List<byte>();
        var off = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(int n, string dict, byte[] streamData)
        {
            off.Add(acc.Count);
            Emit($"{n} 0 obj\n{dict}");
            if (streamData != null) { Emit("\nstream\n"); acc.AddRange(streamData); Emit("\nendstream"); }
            Emit("\nendobj\n");
        }

        Emit("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>", null);
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null);
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
               "/Resources << /XObject << /Im1 5 0 R >> >> >>", null);
        Obj(4, $"<< /Length {content.Length} >>", Encoding.Latin1.GetBytes(content));
        Obj(5, $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray " +
               $"/BitsPerComponent 8{filterEntry} /Length {data.Length} >>", data);

        int xref = acc.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in off) sb.Append($"{o:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        Emit(sb.ToString());
        return acc.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }
}
