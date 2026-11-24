// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The optimizer's bounded decode keeps nothing on the stream it decoded. It holds the whole document
/// and decodes each image, font program and content stream once a pass, so a memo on every stream it
/// decoded but did not rewrite would hold the sum of the decodes — each up to
/// <see cref="OptimizationOptions.MaxStreamDecodeBytes"/> — until the run ended: a page of small Flate
/// bombs whose geometry no pass accepts would multiply the ceiling by their count.
/// </summary>
public sealed class OptimizerDecodeRetentionTests
{
    /// <summary>
    /// The memo is read by removing <c>/Filter</c> without touching the raw bytes, which a <c>Set</c> or
    /// <c>Remove</c> does not clear: a remembered decode would still answer the inflated payload.
    /// </summary>
    [Fact]
    public void TheBoundedDecode_ReturnsTheSamples_AndLeavesNoMemoOnTheStream()
    {
        byte[] payload = new byte[40_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 233);
        var stream = FlateStream(payload);

        Assert.True(PdfOptimizer.TryDecodeBounded(stream, new Dictionary<int, PdfObject>(),
            new OptimizationOptions(), out byte[] decoded, out string reason));
        Assert.Null(reason);
        Assert.Equal(payload, decoded);

        stream.Remove(PdfName.Filter);
        Assert.Equal(stream.RawData, stream.GetDecodedData());
    }

    private static PdfStream FlateStream(byte[] payload)
    {
        using var raw = new MemoryStream();
        raw.WriteByte(0x78);
        raw.WriteByte(0x9C);
        using (var deflate = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);

        var stream = new PdfStream { RawData = raw.ToArray() };
        stream.Set(PdfName.Filter, PdfName.FlateDecode);
        return stream;
    }
}
