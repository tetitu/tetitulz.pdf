// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The ICC readers over mutated profiles. A profile is a document's own bytes, and the render path
/// alone wraps its parse; the output-intent, device-link, named-colour and black-point readers are
/// called bare, so each declines a damaged profile by its answer and never by an exception.
/// </summary>
public class IccProfileMutationTests
{
    private static readonly IccRenderingIntent[] Intents =
    {
        IccRenderingIntent.Perceptual, IccRenderingIntent.MediaRelativeColorimetric,
        IccRenderingIntent.Saturation, IccRenderingIntent.AbsoluteColorimetric,
    };

    private static readonly uint[] Interesting =
    {
        0, 1, 2, 3, 4, 8, 12, 47, 48, 51, 52, 127, 128, 255, 256, 0x7FFF, 0x8000, 0xFFFF, 0x10000,
        0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF,
    };

    private static List<byte[]> Seeds()
    {
        byte[] gamma = IccTransformTests.CurvGamma(2.2);
        return new List<byte[]>
        {
            IccB2AConversionTests.BuildB2AProfile("CMYK", 3, 4, 2, new int[2 * 2 * 2 * 4], "mft2"),
            IccB2AConversionTests.BuildB2AProfile("CMYK", 4, 3, 2, new int[2 * 2 * 2 * 2 * 3], "mft2", tag: "A2B0"),
            IccB2AConversionTests.BuildB2AProfile("RGB ", 3, 3, 3, new int[3 * 3 * 3 * 3], "mft2", tag: "A2B0", pcs: "XYZ "),
            IccLutAToBProfileTests.MabProfile("CMYK", inputs: 4, lab: (50, 10, -10)),
            IccLutAToBProfileTests.MabProfile("RGB ", inputs: 3, lab: (70, 0, 0)),
            IccTransformTests.BuildProfile("mntr", "RGB ", "XYZ ",
                ("rXYZ", IccTransformTests.XyzTag(0.4361, 0.2225, 0.0139)),
                ("gXYZ", IccTransformTests.XyzTag(0.3851, 0.7169, 0.0971)),
                ("bXYZ", IccTransformTests.XyzTag(0.1431, 0.0606, 0.7141)),
                ("wtpt", IccTransformTests.XyzTag(0.9642, 1.0, 0.8249)),
                ("rTRC", gamma), ("gTRC", gamma), ("bTRC", gamma)),
            IccTransformTests.BuildProfile("mntr", "GRAY", "XYZ ", ("kTRC", gamma)),
        };
    }

    /// <summary>One mutation of <paramref name="seed"/>: a byte, a big-endian field, a cut or a tail.</summary>
    private static byte[] Mutate(byte[] seed, Random random)
    {
        byte[] data = (byte[])seed.Clone();
        int edits = 1 + random.Next(3);
        for (int e = 0; e < edits; e++)
        {
            switch (random.Next(6))
            {
                case 0:
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 2:
                case 3:
                {
                    // A four-byte field at an aligned offset: the lengths, offsets and counts live there.
                    if (data.Length < 8) break;
                    int at = random.Next(data.Length / 4 - 1) * 4;
                    uint v = Interesting[random.Next(Interesting.Length)];
                    data[at] = (byte)(v >> 24); data[at + 1] = (byte)(v >> 16); data[at + 2] = (byte)(v >> 8); data[at + 3] = (byte)v;
                    break;
                }
                case 4:
                    if (data.Length > 1) Array.Resize(ref data, 1 + random.Next(data.Length - 1));
                    break;
                default:
                {
                    // Two bytes: the lut16 entry counts, a curve's function type, a channel pair.
                    if (data.Length < 2) break;
                    int at = random.Next(data.Length - 1);
                    uint v = Interesting[random.Next(Interesting.Length)];
                    data[at] = (byte)(v >> 8); data[at + 1] = (byte)v;
                    break;
                }
            }
        }
        return data;
    }

    /// <summary>Every bare reader of a profile, and the evaluation of what parsed; how many parsed.</summary>
    private static int Read(byte[] data)
    {
        int parsed = 0;
        IccProfile profile = IccProfile.Parse(data);
        if (profile != null && profile.Model != IccModel.Unsupported)
        {
            parsed++;
            var components = new float[profile.ComponentCount];
            for (int i = 0; i < components.Length; i++) components[i] = 0.25f * (i + 1);
            foreach (IccRenderingIntent intent in Intents) profile.TryEvaluate(components, intent, out SKColor _);
        }

        foreach (IccRenderingIntent intent in Intents)
        {
            if (IccProfile.ParseB2A(data, out _, out _, intent) != null) parsed++;
            IccOutputTransform output = IccOutputTransform.TryCreate(data, intent, blackPointCompensation: true, out _);
            if (output != null)
            {
                parsed++;
                output.TryConvert(new SKColor(200, 100, 50), new float[16]);
            }
            IccBlackPoint.TryDetectDestination(data, intent, out _);
        }
        if (IccProfile.ParseDeviceLink(data, out _) != null) parsed++;
        IccNamedColors.TryParse(data, out _);
        IccProfile.MediaWhiteScaleOf(data);
        return parsed;
    }

    [Fact]
    public void MutatedProfiles_AreDeclinedByAnAnswer_NeverByAnException_Hostile()
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("TETITULZ_ICC_MUTATION_ROUNDS"), out int asked) ? asked : 4000;
        List<byte[]> seeds = Seeds();
        var random = new Random(20261114);
        int parsed = 0;
        string failure = null;

        // The seeds themselves first: the control that these readers do read a whole profile.
        int whole = 0;
        foreach (byte[] seed in seeds) whole += Read(seed);
        Assert.True(whole >= seeds.Count, $"only {whole} readings of {seeds.Count} whole seed profiles succeeded");

        for (int round = 0; round < rounds && failure == null; round++)
        {
            byte[] seed = seeds[round % seeds.Count];
            byte[] data = Mutate(seed, random);
            try { parsed += Read(data); }
            catch (Exception ex)
            {
                failure = $"round {round} (seed {round % seeds.Count}, {data.Length} bytes): {ex.GetType().Name}: {ex.Message}\n"
                          + Convert.ToBase64String(data) + "\n" + ex.StackTrace;
            }
        }

        Assert.Null(failure);
        // Potency: the mutations are not all refused at the header, so the tables were reached.
        Assert.True(parsed > rounds / 20, $"only {parsed} readings of {rounds} mutated profiles succeeded");
    }
}
