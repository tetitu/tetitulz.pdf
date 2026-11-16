// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// How an <b>independent</b> PDF renderer — one this repository did not write — drew one image stream:
/// a one-page PDF whose only content is the stream as an image XObject, rendered one pixel per point with
/// smoothing off, so a rendered pixel is an image pixel. An encoder test compares its input with this,
/// not with this library's own decoder, which could share the encoder's mistake.
/// </summary>
/// <remarks>
/// <para><b>Recorded, not run.</b> Each render was made once by the independent renderer and stored under
/// <c>Fixtures/IndependentRenders</c>, keyed by the SHA-256 of the stream, its image entries and its size.
/// When an encoder's output changes the key changes and the test fails with the key it looked for: the
/// new bytes have been read by nothing but this library.</para>
/// <para><b>Re-recording.</b> A host that can run an independent renderer sets <see cref="LiveGray"/> and
/// <see cref="LiveRgb"/>; with <c>TETITULZ_RECORD_INDEPENDENT=1</c> its answers become the records, and
/// otherwise each live answer is compared with its record.</para>
/// </remarks>
internal static class IndependentImageRender
{
    internal static Func<byte[], string, int, int, byte[,]>? LiveGray { get; set; }

    internal static Func<byte[], string, int, int, byte[,,]>? LiveRgb { get; set; }

    private static bool Recording => Environment.GetEnvironmentVariable("TETITULZ_RECORD_INDEPENDENT") == "1";

    /// <summary>The stream drawn as a grey image: one byte a pixel, rows top to bottom.</summary>
    internal static byte[,] RenderGray(byte[] stream, string imageEntries, int width, int height)
    {
        byte[] flat = Render("gray", stream, imageEntries, width, height, width * height,
            LiveGray == null ? null : () => Flatten(LiveGray(stream, imageEntries, width, height)));
        var grey = new byte[height, width];
        Buffer.BlockCopy(flat, 0, grey, 0, flat.Length);
        return grey;
    }

    /// <summary>The stream drawn as an RGB image: three bytes a pixel, rows top to bottom.</summary>
    internal static byte[,,] RenderRgb(byte[] stream, string imageEntries, int width, int height)
    {
        byte[] flat = Render("rgb", stream, imageEntries, width, height, width * height * 3,
            LiveRgb == null ? null : () => Flatten(LiveRgb(stream, imageEntries, width, height)));
        var rgb = new byte[height, width, 3];
        Buffer.BlockCopy(flat, 0, rgb, 0, flat.Length);
        return rgb;
    }

    private static byte[] Render(string kind, byte[] stream, string imageEntries, int width, int height, int length, Func<byte[]>? live)
    {
        string key = Key(kind, stream, imageEntries, width, height);
        string? path = RecordPath(key);
        byte[]? recorded = path != null && File.Exists(path) ? Gunzip(File.ReadAllBytes(path)) : null;

        if (live != null)
        {
            byte[] answer = live();
            if (Recording)
            {
                if (path == null) throw new InvalidOperationException("No repository root to record into.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Gzip(answer));
            }
            else if (recorded != null)
            {
                Assert.True(recorded.AsSpan().SequenceEqual(answer), $"The independent renderer's answer for {key} no longer matches its record.");
            }
            return answer;
        }

        if (recorded == null)
            Assert.Fail($"No independent render is recorded for this stream (key {key}, {kind} {width}x{height}). " +
                        "The encoder's output changed: render it with an independent PDF renderer and record the result.");
        Assert.Equal(length, recorded!.Length);
        return recorded;
    }

    private static string Key(string kind, byte[] stream, string imageEntries, int width, int height)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(stream);
        sha.AppendData(Encoding.ASCII.GetBytes($"\0{kind}\0{imageEntries}\0{width}x{height}"));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant().Substring(0, 32);
    }

    private static string? RecordPath(string key)
    {
        string relative = Path.Combine("Fixtures", "IndependentRenders", key + ".gz");
        if (Recording)
            return TestCorpus.RepoRoot is { } repo ? Path.Combine(repo, "tests", "Tetitulz.Pdf.Base.Tests", relative) : null;
        return Path.Combine(AppContext.BaseDirectory, relative);
    }

    private static byte[] Flatten(Array pixels)
    {
        var flat = new byte[Buffer.ByteLength(pixels)];
        Buffer.BlockCopy(pixels, 0, flat, 0, flat.Length);
        return flat;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(data, 0, data.Length);
        return buffer.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var o = new MemoryStream();
        gz.CopyTo(o);
        return o.ToArray();
    }
}
