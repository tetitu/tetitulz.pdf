// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The per-pass prefix lengths and distortion of <see cref="JpxBlockCode"/>, which quality layers
/// and rate allocation stand on. The oracle for a length is <see cref="JpxBlockDecoder"/> itself:
/// a prefix of the segment, padded with <c>0xFF</c> as D.4.1 says, decoded for the passes the
/// prefix claims, must yield what the whole segment yields for those passes — and the prefix one
/// byte shorter must not, somewhere in the fixture, or the rule is not being tested.
/// </summary>
public sealed class JpxEncoderTruncationTests
{
    private static JpxCodeBlock Block(int width, int height, int length, int passes)
    {
        var block = new JpxCodeBlock(new JpxRect(0, 0, width, height)) { Included = true };
        block.Add(new JpxCodeChunk(new JpxRange(0, length), passes, terminated: false));
        return block;
    }

    /// <summary>Tier 1 over the first <paramref name="length"/> bytes of <paramref name="data"/> for <paramref name="passes"/> passes; the decoder's doubled values.</summary>
    private static int[] Decode(byte[] data, int length, int passes, int width, int height, int orientation, int planes)
    {
        var prefix = new byte[length];
        Array.Copy(data, prefix, length);
        int[] values = JpxBlockDecoder.Decode(Block(width, height, length, passes), prefix, orientation, JpxCodeBlockStyle.None, planes, 0, out string problem);
        Assert.Null(problem);
        return values;
    }

    private static int[] RandomCoefficients(int width, int height, int seed, int maxPlanes = 12)
    {
        var random = new Random(seed);
        var coefficients = new int[width * height];
        int range = 1 << random.Next(1, maxPlanes);
        for (int i = 0; i < coefficients.Length; i++)
            coefficients[i] = random.Next(4) == 0 ? 0 : random.Next(-range, range);
        coefficients[0] = -range - 1;
        return coefficients;
    }

    [Theory]
    [InlineData(64, 64, 0, 11)]
    [InlineData(64, 64, 3, 12)]
    [InlineData(32, 32, 1, 13)]
    [InlineData(17, 9, 2, 14)]
    [InlineData(8, 8, 0, 15)]
    [InlineData(4, 4, 3, 16)]
    [InlineData(1, 4, 0, 17)]
    public void EveryPassDecodesExactly_FromItsPrefix_AndNotFromOneByteLess(int width, int height, int orientation, int seed)
    {
        int[] coefficients = RandomCoefficients(width, height, seed);
        JpxBlockCode code = JpxBlockEncoder.Encode(coefficients, width, height, orientation);
        Assert.Equal(3 * code.Planes - 2, code.Passes);
        Assert.Equal(code.Passes, code.Lengths.Length);
        Assert.True(code.Lengths[code.Passes - 1] <= code.Data.Length);

        int shorterFails = 0;
        for (int k = 0; k < code.Passes; k++)
        {
            if (k > 0) Assert.True(code.Lengths[k] >= code.Lengths[k - 1], $"pass {k}: {code.Lengths[k]} after {code.Lengths[k - 1]}");
            int[] whole = Decode(code.Data, code.Data.Length, k + 1, width, height, orientation, code.Planes);
            int[] fromPrefix = Decode(code.Data, code.Lengths[k], k + 1, width, height, orientation, code.Planes);
            Assert.True(whole.SequenceEqual(fromPrefix), $"pass {k} of {code.Passes}: the {code.Lengths[k]}-byte prefix of {code.Data.Length} decodes differently");

            if (code.Lengths[k] == 0) continue;
            int[] fromLess = Decode(code.Data, code.Lengths[k] - 1, k + 1, width, height, orientation, code.Planes);
            if (!whole.SequenceEqual(fromLess)) shorterFails++;
        }
        // Potency: a rule that answered "the whole segment" for every pass would pass the loop
        // above; the lengths are short enough that one byte less changes some pass's decode.
        Assert.True(shorterFails > 0, "no pass's prefix was tight to the byte");
        int firstPassCeiling = width * height >= 64 ? code.Data.Length / 2 : code.Data.Length;
        Assert.True(code.Lengths[0] < firstPassCeiling, $"the first pass's prefix is {code.Lengths[0]} of {code.Data.Length} bytes");
    }

    /// <summary>
    /// Against the behavioural minimum — the shortest prefix that happens to decode a pass
    /// exactly, found by trying every length: the rule never falls below it, and over these blocks
    /// it is the minimum itself at least nine passes in ten, at most two bytes above it otherwise.
    /// A rule that padded every length by a safe margin would fail the first count; a rule that
    /// got the frame wrong by a bit would fall below the minimum somewhere in the loop above.
    /// </summary>
    [Theory]
    [InlineData(8, 8, 0, 21)]
    [InlineData(8, 8, 3, 22)]
    [InlineData(6, 10, 1, 23)]
    [InlineData(5, 5, 2, 24)]
    public void TheRuleIsTheBehaviouralMinimum_AlmostAlways(int width, int height, int orientation, int seed)
    {
        int[] coefficients = RandomCoefficients(width, height, seed, maxPlanes: 8);
        JpxBlockCode code = JpxBlockEncoder.Encode(coefficients, width, height, orientation);
        int exact = 0, total = 0;
        for (int k = 0; k < code.Passes; k++)
        {
            int[] whole = Decode(code.Data, code.Data.Length, k + 1, width, height, orientation, code.Planes);
            int minimum = 0;
            while (!whole.SequenceEqual(Decode(code.Data, minimum, k + 1, width, height, orientation, code.Planes))) minimum++;
            Assert.True(code.Lengths[k] >= minimum, $"pass {k}: the rule says {code.Lengths[k]} bytes, {minimum} decode it");
            Assert.True(code.Lengths[k] - minimum <= 2, $"pass {k}: the rule says {code.Lengths[k]} bytes, {minimum} decode it");
            total++;
            if (code.Lengths[k] == minimum) exact++;
        }
        Assert.True(exact * 10 >= total * 9, $"the rule met the behavioural minimum on {exact} of {total} passes");
    }

    /// <summary>A prefix never ends in 0xFF (B.10.7 for a segment's end; the padding supplies the same ones), and the whole segment does not either.</summary>
    [Fact]
    public void NoPrefixEndsInFF()
    {
        int found = 0;
        for (int seed = 30; seed < 60; seed++)
        {
            int[] coefficients = RandomCoefficients(16, 16, seed);
            JpxBlockCode code = JpxBlockEncoder.Encode(coefficients, 16, 16, seed & 3);
            Assert.NotEqual(0xFF, code.Data[code.Data.Length - 1]);
            foreach (int length in code.Lengths)
                if (length > 0) Assert.NotEqual(0xFF, code.Data[length - 1]);
            foreach (byte b in code.Data) if (b == 0xFF) found++;
        }
        Assert.True(found > 0, "no segment held an 0xFF byte, so the rule's treatment of one was never exercised");
    }

    /// <summary>
    /// The distortion a pass removes is what the decoder's partial reconstruction shows: after
    /// each pass above the last plane, the squared error between the coefficients and the
    /// decoded values (halved, since the decoder doubles them and carries the half of E-6) equals
    /// the coefficients' energy less the cumulative distortion. After the last pass everything is
    /// removed. The counts are strict: a pass that codes a sample removes something.
    /// </summary>
    [Theory]
    [InlineData(16, 16, 0, 41)]
    [InlineData(9, 7, 3, 42)]
    public void DistortionPerPass_MatchesTheDecodersPartialReconstruction(int width, int height, int orientation, int seed)
    {
        int[] coefficients = RandomCoefficients(width, height, seed);
        JpxBlockCode code = JpxBlockEncoder.Encode(coefficients, width, height, orientation);
        double energy = 0;
        foreach (int c in coefficients) energy += (double)c * c;

        Assert.Equal(energy, code.Distortion[code.Passes - 1], 6);
        double previous = 0;
        for (int k = 0; k < code.Passes - 3; k++)   // the passes above plane 0, where the decoder's half is the reconstruction's
        {
            int[] values = Decode(code.Data, code.Data.Length, k + 1, width, height, orientation, code.Planes);
            double error = 0;
            for (int i = 0; i < values.Length; i++)
            {
                double e = Math.Abs(coefficients[i]) - Math.Abs(values[i]) / 2.0;
                error += e * e;
            }
            Assert.Equal(energy - error, code.Distortion[k], 6);
            Assert.True(code.Distortion[k] >= previous, $"pass {k} removed {code.Distortion[k] - previous}");
            previous = code.Distortion[k];
        }
        Assert.True(code.Distortion[0] > 0, "the first cleanup pass found no significant sample");
        Assert.True(code.Distortion[0] < energy, "the first cleanup pass removed everything");
    }

    [Fact]
    public void ABlockOfZeros_HasNoPassesLengthsOrDistortion()
    {
        JpxBlockCode code = JpxBlockEncoder.Encode(new int[12], 3, 4, 1);
        Assert.Equal((0, 0, 0, 0, 0), (code.Planes, code.Passes, code.Data.Length, code.Lengths.Length, code.Distortion.Length));
    }

    /// <summary>The MQ coder's own account, without tier 1: a checkpoint's prefix decodes the decisions before it, and the padded decoder reads the rest as it will.</summary>
    [Fact]
    public void MqEncoder_PrefixLengths_DecodeTheDecisionsBeforeEachCheckpoint()
    {
        var random = new Random(77);
        var decisions = new (int Context, int Bit)[600];
        for (int i = 0; i < decisions.Length; i++) decisions[i] = (random.Next(3), random.Next(5) == 0 ? 1 : 0);
        var encoder = new MqEncoder(3);
        var marks = new List<MqEncoder.Checkpoint>();
        for (int i = 0; i < decisions.Length; i++)
        {
            encoder.Encode(decisions[i].Context, decisions[i].Bit);
            if (i % 50 == 49) marks.Add(encoder.Mark());
        }
        byte[] segment = encoder.FinishCodewordSegment();
        int[] lengths = MqEncoder.PrefixLengths(marks, segment);
        Assert.Equal(marks.Count, lengths.Length);

        int tight = 0;
        for (int m = 0; m < marks.Count; m++)
        {
            int decisionsBefore = 50 * (m + 1);
            Assert.True(DecodesFirst(segment, lengths[m], decisions, decisionsBefore), $"checkpoint {m}: {lengths[m]} bytes do not decode {decisionsBefore} decisions");
            if (lengths[m] > 0 && !DecodesFirst(segment, lengths[m] - 1, decisions, decisionsBefore)) tight++;
        }
        Assert.True(tight > marks.Count / 2, $"only {tight} of {marks.Count} checkpoints were tight to the byte");
        Assert.True(lengths[0] < segment.Length / 4, $"the first checkpoint needs {lengths[0]} of {segment.Length} bytes");
    }

    /// <summary>
    /// Every checkpoint of many streams, one every few decisions: each prefix decodes the decisions before it.
    /// The case the rule once missed — a prefix ending on an 0xFF whose following byte has its stuffed top bit
    /// set, a carry the 0xFF could not take, so the true value lies above the prefix's ones and the padded one
    /// below the interval — turns up across these streams (counted, so the test cannot pass without meeting it),
    /// as it did in an ISO block whose Part 1 re-encode decoded 8 samples wrong from a prefix three bytes short.
    /// </summary>
    [Fact]
    public void MqEncoder_EveryPrefixDecodes_EvenWhereACarryFollowsAnFF()
    {
        int carries = 0, checkpoints = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var decisions = new (int Context, int Bit)[500];
            int skew = 2 + seed % 6;
            for (int i = 0; i < decisions.Length; i++) decisions[i] = (random.Next(4), random.Next(skew) == 0 ? 1 : 0);
            var encoder = new MqEncoder(4);
            var marks = new List<MqEncoder.Checkpoint>();
            var at = new List<int>();
            for (int i = 0; i < decisions.Length; i++)
            {
                encoder.Encode(decisions[i].Context, decisions[i].Bit);
                if (i % 7 == 6) { marks.Add(encoder.Mark()); at.Add(i + 1); }
            }
            byte[] segment = encoder.FinishCodewordSegment();
            int[] lengths = MqEncoder.PrefixLengths(marks, segment);
            for (int m = 0; m < marks.Count; m++)
            {
                checkpoints++;
                Assert.True(DecodesFirst(segment, lengths[m], decisions, at[m], 4), $"seed {seed}, checkpoint {m}: {lengths[m]} of {segment.Length} bytes do not decode {at[m]} decisions");
                int j = marks[m].Bytes - 1;
                if (j >= 1 && j < segment.Length && segment[j - 1] == 0xFF && (segment[j] & 0x80) != 0) carries++;
            }
        }
        Assert.True(carries > 0, $"no checkpoint of {checkpoints} sat after an 0xFF whose next byte carries");
    }

    private static bool DecodesFirst(byte[] segment, int length, (int Context, int Bit)[] decisions, int count, int contexts = 3)
    {
        var decoder = new MqDecoder(segment, 0, length, contexts);
        for (int i = 0; i < count; i++)
            if (decoder.Decode(decisions[i].Context) != decisions[i].Bit) return false;
        return true;
    }
}
