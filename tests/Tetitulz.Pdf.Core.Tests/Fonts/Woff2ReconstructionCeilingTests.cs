// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The WOFF 2.0 decoder's ceilings <b>after</b> the Brotli stream — on a transformed table's
/// declared original size, on the glyf reconstruction, and on the reassembled font as a whole.
/// </summary>
/// <remarks>
/// <para><b>Each fixture is built so that one ceiling, and only that one, can refuse it.</b> These
/// checks stack: the block is bounded before glyf is rebuilt from it, and glyf before the font is
/// summed. A fixture that is merely "too big" is refused by whichever runs first and proves nothing
/// about the rest, so every test asserts which ceilings its fixture <i>passes</i> before asserting
/// the one it is aimed at.</para>
///
/// <para><b>The ceiling is a parameter here, not the 64 MiB constant.</b> The reassembled-total
/// check fires only on a font larger than the ceiling once rebuilt, and at production scale that is
/// 64 MiB of allocation per test. <c>Woff2Decoder.Decode(byte[], long)</c> runs the same code under
/// a ceiling of a few hundred bytes, so eight hand-built glyphs stand where 64 MiB of them would.
/// Every ceiling in the decoder takes that one value, so none is being tested against a number
/// production does not use.</para>
///
/// <para><b>The §5.1 transform rebuilds more than it reads, and that arithmetic is the whole
/// fixture.</b> A glyph without an explicit bounding box gets eight bytes computed from its points,
/// the contour count and instruction length widen to two bytes each, and loca is written from
/// nothing. So the rebuilt tables outgrow the block they came from, which is what lets a block, a
/// glyf and a whole font land on three different sides of one ceiling. The sizes are
/// <b>measured</b> from the decoder's own rebuild, never written here: a change to what the rebuild
/// emits must not silently move a fixture onto the wrong side of the line it tests.</para>
/// </remarks>
public class Woff2ReconstructionCeilingTests
{
    /// <summary>Enough glyphs that the rebuilt glyf is larger than the transformed one.</summary>
    private const int Glyphs = 8;

    /// <summary>A ceiling no fixture here comes near.</summary>
    private const long Generous = 1L << 20;

    /// <summary>
    /// The acceptance control: the same container every refusal below starts from decodes when the
    /// ceiling is out of the way — so each refusal is a ceiling talking, not a malformed fixture.
    /// </summary>
    [Fact]
    public void AnHonestTransformedFontDecodes()
    {
        byte[] transformed = TransformedGlyf(Glyphs);
        var (glyf, loca) = Measure(transformed);

        byte[] sfnt = Woff2Decoder.Decode(GlyfLocaContainer(transformed, (uint)glyf, (uint)loca), Generous);

        Assert.NotNull(sfnt);
        Assert.Equal(2, (sfnt[4] << 8) | sfnt[5]);      // numTables: glyf and loca
    }

    /// <summary>
    /// <b>A font whose parts each pass the ceiling but whose whole does not is refused</b> — and one
    /// byte more of ceiling lets it through, so what is pinned is the boundary.
    /// </summary>
    /// <remarks>
    /// The ceiling sits one byte under glyf plus loca. The block is smaller than both, so
    /// <c>BrotliInflate</c> passes it; glyf alone is under the ceiling, so the rebuild passes it; the
    /// header and the directory claim sizes under it too. Only the check on the reassembled total can
    /// refuse — and the header's claim here <i>understates</i> the font, which is exactly the case
    /// that check exists for: a claim is not a measurement.
    /// </remarks>
    [Fact]
    public void AFontWhosePartsPassTheCeilingButWhoseWholeDoesNotIsRefused()
    {
        byte[] transformed = TransformedGlyf(Glyphs);
        var (glyf, loca) = Measure(transformed);
        long ceiling = glyf + loca - 1;
        byte[] container = GlyfLocaContainer(transformed, (uint)glyf, (uint)loca);

        Assert.True(transformed.Length <= ceiling, "the block itself must pass the ceiling");
        Assert.True(glyf <= ceiling, "the rebuilt glyf itself must pass the ceiling");

        Assert.Null(Woff2Decoder.Decode(container, ceiling));
        Assert.NotNull(Woff2Decoder.Decode(container, ceiling + 1));
    }

    /// <summary>
    /// <b>A glyf that rebuilds past the ceiling is refused.</b>
    /// </summary>
    /// <remarks>
    /// <para>The block passes and so does every claim, so the refusal is about the rebuilt table. Two
    /// ceilings stand on this and <b>each masks the other</b>: the rebuild stops as glyf crosses the
    /// ceiling, and the reassembled total, which includes glyf, would refuse it anyway. So this pins
    /// what the pair jointly guarantees, and
    /// <see cref="TheGlyfRebuildStopsAtItsCeilingAndNotOneByteBefore"/> pins the rebuild's own
    /// boundary directly.</para>
    ///
    /// <para>What the rebuild's ceiling adds is stopping <i>early</i>. That is a bounded saving, not
    /// a bomb defence: the transform writes a fixed handful of bytes per glyph and per point that it
    /// did not read, and <c>numGlyphs</c> is a UInt16, so a rebuilt glyf is at most a small multiple of
    /// the block that <c>BrotliInflate</c> already bounded.</para>
    /// </remarks>
    [Fact]
    public void AGlyfThatRebuildsPastTheCeilingIsRefused()
    {
        byte[] transformed = TransformedGlyf(Glyphs);
        var (glyf, loca) = Measure(transformed);
        long ceiling = glyf - 1;

        // glyf's origLength is only a claim for a transformed table, and §5.1 forbids rejecting a
        // rebuilt glyf whose length differs from it — so it is set under the ceiling here, where the
        // directory's check on it cannot be what refuses.
        byte[] container = GlyfLocaContainer(transformed, (uint)transformed.Length, (uint)loca);

        Assert.True(transformed.Length <= ceiling, "the block itself must pass the ceiling");
        Assert.True(loca <= ceiling);

        Assert.Null(Woff2Decoder.Decode(container, ceiling));
    }

    /// <summary>
    /// The rebuild's own ceiling, reached directly: a glyf of exactly the ceiling is produced, and one
    /// byte less refuses it.
    /// </summary>
    /// <remarks>
    /// Through the decoder this ceiling is masked by the reassembled total (see
    /// <see cref="AGlyfThatRebuildsPastTheCeilingIsRefused"/>), so it is asserted here, where nothing
    /// stands behind it — both the check and which side of the boundary it falls on.
    /// </remarks>
    [Fact]
    public void TheGlyfRebuildStopsAtItsCeilingAndNotOneByteBefore()
    {
        byte[] transformed = TransformedGlyf(Glyphs);
        var (glyf, _) = Measure(transformed);

        Assert.NotNull(Woff2GlyfTransform.Rebuild(transformed, glyf).Glyf);
        Assert.Null(Woff2GlyfTransform.Rebuild(transformed, glyf - 1).Glyf);
    }

    /// <summary>
    /// <b>A transformed table claiming an oversized original is refused</b>, although nothing it
    /// would decode to is oversized.
    /// </summary>
    /// <remarks>
    /// For an untransformed table <c>origLength</c> is the size of its slice of the block, and the
    /// block's own ceiling covers it. For a transformed one it is the size of the table before the
    /// transform, which nothing downstream reads — so the directory's check is the only thing that
    /// ever looks at it, and without it a directory can declare a four-gigabyte glyf and decode as
    /// though it had not. The container is <see cref="AnHonestTransformedFontDecodes"/>'s with that
    /// one claim changed.
    /// </remarks>
    [Fact]
    public void ATransformedTableClaimingAnOversizedOriginalIsRefused()
    {
        byte[] transformed = TransformedGlyf(Glyphs);
        var (_, loca) = Measure(transformed);

        byte[] container = GlyfLocaContainer(transformed, (uint)(Generous + 1), (uint)loca);

        Assert.Null(Woff2Decoder.Decode(container, Generous));
    }

    /// <summary>The glyf and loca the decoder's own rebuild produces from <paramref name="transformed"/>.</summary>
    private static (int Glyf, int Loca) Measure(byte[] transformed)
    {
        var (glyf, loca) = Woff2GlyfTransform.Rebuild(transformed, long.MaxValue);
        Assert.NotNull(glyf);
        Assert.True(glyf.Length > transformed.Length,
            "the fixture relies on the rebuild outgrowing the block; with this few glyphs it did not");
        return (glyf.Length, loca.Length);
    }

    /// <summary>
    /// A §5.1 transformed glyf of <paramref name="glyphs"/> simple glyphs, each one contour of one
    /// on-curve point — the smallest glyph the transform can carry — with no explicit bounding boxes,
    /// no instructions and a short-format loca.
    /// </summary>
    private static byte[] TransformedGlyf(int glyphs)
    {
        var nContours = new byte[2 * glyphs];
        var nPoints = new byte[glyphs];
        var flags = new byte[glyphs];
        var glyphStream = new byte[2 * glyphs];
        for (int g = 0; g < glyphs; g++)
        {
            nContours[2 * g + 1] = 1;       // Int16: one contour
            nPoints[g] = 1;                 // 255UInt16: one point
            flags[g] = 0;                   // on-curve; triplet row 0 is dx 0, dy from one data byte
            glyphStream[2 * g] = 10;        // that data byte
            glyphStream[2 * g + 1] = 0;     // 255UInt16: no instructions
        }

        var bboxBitmap = new byte[4 * ((glyphs + 31) / 32)];   // all clear: every box is computed

        byte[][] streams =
        {
            nContours, nPoints, flags, glyphStream,
            Array.Empty<byte>(),            // composites
            bboxBitmap,
            Array.Empty<byte>(),            // instructions
        };

        const int HeaderSize = 36;
        int length = HeaderSize;
        foreach (byte[] s in streams) length += s.Length;

        var table = new byte[length];
        Woff2Bytes.WriteU16(table, 0, 0);           // reserved
        Woff2Bytes.WriteU16(table, 2, 0);           // optionFlags: no overlap bitmap
        Woff2Bytes.WriteU16(table, 4, glyphs);      // numGlyphs
        Woff2Bytes.WriteU16(table, 6, 0);           // indexFormat: short loca

        int at = HeaderSize;
        for (int i = 0; i < streams.Length; i++)
        {
            Woff2Bytes.WriteU32(table, 8 + 4 * i, (uint)streams[i].Length);
            Buffer.BlockCopy(streams[i], 0, table, at, streams[i].Length);
            at += streams[i].Length;
        }
        return table;
    }

    /// <summary>
    /// A two-entry container — a transformed glyf over <paramref name="transformed"/>, and the empty
    /// transformed loca §5.3 requires beside it — declaring the given original sizes.
    /// </summary>
    /// <remarks>
    /// Known tags 10 and 11 are glyf and loca, and transform version 0 is the real transform for
    /// both (their null transform is 3). <c>totalSfntSize</c> is set to the transformed glyf's size:
    /// below every ceiling a test here uses, so the header's check never refuses one of these
    /// fixtures — it has its own test in <c>Woff2BrotliBombTests</c>.
    /// </remarks>
    private static byte[] GlyfLocaContainer(byte[] transformed, uint glyfOrigLength, uint locaOrigLength)
    {
        const int HeaderSize = 48;
        byte[] block = RuntimeBrotli(transformed, 5, 10);

        byte[][] directoryParts =
        {
            new byte[] { 10 }, Woff2Bytes.UIntBase128(glyfOrigLength), Woff2Bytes.UIntBase128((uint)transformed.Length),
            new byte[] { 11 }, Woff2Bytes.UIntBase128(locaOrigLength), Woff2Bytes.UIntBase128(0),
        };
        int directory = 0;
        foreach (byte[] part in directoryParts) directory += part.Length;

        var file = new byte[HeaderSize + directory + block.Length];
        file[0] = 0x77; file[1] = 0x4F; file[2] = 0x46; file[3] = 0x32;           // 'wOF2'
        Woff2Bytes.WriteU32(file, 4, 0x00010000);                                 // flavor: TrueType sfnt
        Woff2Bytes.WriteU32(file, 8, (uint)file.Length);                          // length
        Woff2Bytes.WriteU16(file, 12, 2);                                         // numTables
        Woff2Bytes.WriteU16(file, 14, 0);                                         // reserved
        Woff2Bytes.WriteU32(file, 16, (uint)transformed.Length);                  // totalSfntSize
        Woff2Bytes.WriteU32(file, 20, (uint)block.Length);                        // totalCompressedSize

        int at = HeaderSize;
        foreach (byte[] part in directoryParts)
        {
            Buffer.BlockCopy(part, 0, file, at, part.Length);
            at += part.Length;
        }
        Buffer.BlockCopy(block, 0, file, at, block.Length);
        return file;
    }

    /// <summary>The runtime's Brotli encoder (Google's, inside .NET) — an encoder the decoder under test shares nothing with.</summary>
    private static byte[] RuntimeBrotli(byte[] plain, int quality, int window)
    {
        var destination = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(plain.Length)];
        if (!System.IO.Compression.BrotliEncoder.TryCompress(plain, destination, out int written, quality, window))
            throw new System.InvalidOperationException("the runtime's Brotli encoder declined the fixture");
        return destination.AsSpan(0, written).ToArray();
    }
}
