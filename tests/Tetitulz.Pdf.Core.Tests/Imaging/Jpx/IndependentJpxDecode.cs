// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// What an <b>independent</b> JPEG 2000 decoder made of the encoder's bytes — a decoder this repository
/// did not write — so an encoder test's oracle is not the library that wrote the bytes (a symmetric
/// misreading of T.800 survives a round trip through our own decoder).
/// </summary>
/// <remarks>
/// <para><b>Recorded, not run.</b> Each decode was made once by the independent decoder and stored under
/// <c>Fixtures/IndependentDecodes/jpx</c>, keyed by the SHA-256 of the bytes and the decode's options. A
/// test asks for the decode of the bytes it wrote; when the encoder's output changes, the key changes and
/// the test fails with the key it looked for — the new bytes have not been seen by anything but this
/// library, which is exactly the condition the test exists to catch.</para>
/// <para><b>Re-recording.</b> A host that can run an independent decoder sets <see cref="Live"/> (a module
/// initializer outside the repository does); with <c>TETITULZ_RECORD_INDEPENDENT=1</c> its answers are
/// written as the new records, and otherwise each live answer is compared with the record.</para>
/// </remarks>
internal static class IndependentJpxDecode
{
    /// <summary>The decode: an image mode (<c>L</c>, <c>I;16</c>, <c>RGB</c>, <c>RGBA</c>, <c>CMYK</c>…), the size, and one plane per band in raster order.</summary>
    internal sealed record Decoded(string Mode, int Width, int Height, int[][] Planes);

    /// <summary>A live independent decoder: bytes, extension, resolution reduction, quality layers (0 for all).</summary>
    internal static Func<byte[], string, int, int, Decoded>? Live { get; set; }

    private static bool Recording => Environment.GetEnvironmentVariable("TETITULZ_RECORD_INDEPENDENT") == "1";

    /// <summary>
    /// The independent decode of <paramref name="data"/>, read as <paramref name="extension"/> (<c>.j2k</c>
    /// a raw codestream, <c>.jp2</c> / <c>.jph</c> the file format), <paramref name="reduce"/> resolution
    /// levels short of the full size, from the first <paramref name="layers"/> quality layers (0 for all).
    /// </summary>
    internal static Decoded Decode(byte[] data, string extension, int reduce = 0, int layers = 0)
    {
        string key = Key(data, extension, reduce, layers);
        string? recordedPath = RecordPath(key);
        Decoded? recorded = recordedPath != null && File.Exists(recordedPath) ? Read(File.ReadAllBytes(recordedPath)) : null;

        if (Live != null)
        {
            Decoded live = Live(data, extension, reduce, layers);
            if (Recording)
            {
                if (recordedPath == null) throw new InvalidOperationException("No repository root to record into.");
                Directory.CreateDirectory(Path.GetDirectoryName(recordedPath)!);
                File.WriteAllBytes(recordedPath, Write(live));
            }
            else if (recorded != null)
            {
                Assert.True(Same(recorded, live), $"The independent decoder's answer for {key} no longer matches its record.");
            }
            return live;
        }

        if (recorded == null)
            Assert.Fail($"No independent decode is recorded for these bytes (key {key}, {extension}, reduce {reduce}, layers {layers}). " +
                        "The encoder's output changed: decode it with an independent JPEG 2000 decoder and record the result.");
        return recorded!;
    }

    private static string Key(byte[] data, string extension, int reduce, int layers)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(data);
        sha.AppendData(Encoding.ASCII.GetBytes($"\0{extension}\0{reduce}\0{layers}"));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant().Substring(0, 32);
    }

    /// <summary>The record's place: in the source tree when recording, beside the test assembly otherwise.</summary>
    private static string? RecordPath(string key)
    {
        string relative = Path.Combine("Fixtures", "IndependentDecodes", "jpx", key + ".gz");
        if (Recording)
            return TestCorpus.RepoRoot is { } repo ? Path.Combine(repo, "tests", "Tetitulz.Pdf.Core.Tests", relative) : null;
        return Path.Combine(AppContext.BaseDirectory, relative);
    }

    private static bool Same(Decoded a, Decoded b) =>
        a.Mode == b.Mode && a.Width == b.Width && a.Height == b.Height && a.Planes.Length == b.Planes.Length &&
        a.Planes.Zip(b.Planes).All(p => p.First.AsSpan().SequenceEqual(p.Second));

    private static byte[] Write(Decoded d)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(d.Mode);
            w.Write(d.Width);
            w.Write(d.Height);
            w.Write(d.Planes.Length);
            foreach (int[] plane in d.Planes)
            {
                w.Write(plane.Length);
                foreach (int v in plane) w.Write(v);
            }
        }
        return buffer.ToArray();
    }

    private static Decoded Read(byte[] bytes)
    {
        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        string mode = r.ReadString();
        int width = r.ReadInt32(), height = r.ReadInt32();
        var planes = new int[r.ReadInt32()][];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = new int[r.ReadInt32()];
            for (int j = 0; j < planes[i].Length; j++) planes[i][j] = r.ReadInt32();
        }
        return new Decoded(mode, width, height, planes);
    }
}
